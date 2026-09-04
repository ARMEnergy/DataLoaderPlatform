using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Criterion;

/// <summary>
/// Marker so <see cref="CriterionModule.RunAsync"/> can enumerate the per-feed
/// pipelines without their generics colliding with another loader's registrations in
/// the shared DI container.
/// </summary>
public interface ICriterionPipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. The reader
/// already emits the sink's row type, so the transformer is the identity.
/// </summary>
public sealed class CriterionPipeline : LoaderPipelineBase<CriterionWorkUnit, CriterionRow, CriterionRow>, ICriterionPipeline
{
    public CriterionPipeline(
        string feedId,
        IWorkUnitProvider<CriterionWorkUnit> provider,
        ISourceReader<CriterionWorkUnit, CriterionRow> source,
        ISink<CriterionRow> sink,
        ILoadLogRepository loadLog,
        CriterionSettings settings,
        ILogger logger)
        : base(CriterionModule.Id, provider, source, new IdentityTransformer<CriterionRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the Criterion Research loader.
///
/// <para>
/// <b>The first loader in this repo whose source is a relational database</b> rather
/// than an HTTP API, an FTP drop or a file feed. It reads Criterion's PostgreSQL
/// delivery replica (dda.criterionrsch.com:443, SSL required) and merges nine
/// relations into nine <c>arm</c> tables in the <c>Criterion</c> database. That
/// difference is confined to <see cref="NpgsqlCriterionSource"/>; everything else —
/// work units, resume keys, TVP merges, the write gate — is the platform's standard
/// machinery.
/// </para>
/// <para>
/// Nine feeds, one pipeline each, one table each. Every relation's column mapping,
/// window mode, TVP and proc live in <see cref="CriterionDescriptors"/>, so this
/// class only wires them up — adding a table is a descriptor plus its SQL, not new
/// plumbing.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK between
/// the target tables. A failure in one feed must not cost the other eight, so
/// <see cref="RunAsync"/> records the failure and carries on (the Argus/ICE posture).
/// The feeds are ordered dimensions-first in <see cref="CriterionDescriptors.All"/> so
/// an interrupted run leaves the database consistent at more points in time, but
/// nothing depends on that ordering.
/// </para>
/// <para>
/// Pipelines are built explicitly rather than via open-generic DI, so this loader's
/// <c>IWorkUnitProvider&lt;T&gt;</c> and <c>ISink&lt;T&gt;</c> registrations cannot
/// collide with another loader's.
/// </para>
/// </summary>
public sealed class CriterionModule : ILoaderModule
{
    public const string Id = "Criterion";

    public string LoaderId => Id;
    public string DisplayName => "Criterion Research financial series and pipeline flows";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" SourceUsername/SourcePassword sentinels into
        // real values from core.Param at run time.
        services.AddLoaderSettings<CriterionSettings>(configuration, Id);

        // One source for all nine pipelines. Singleton so the Npgsql connection string
        // is assembled once (and so its pool cap is shared, not multiplied by nine).
        services.AddSingleton<ICriterionSource, NpgsqlCriterionSource>();
        services.AddSingleton<CriterionLoadValidator>();

        services.AddSingleton<IReadOnlyList<ICriterionPipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<ICriterionPipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<CriterionSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var source = sp.GetRequiredService<ICriterionSource>();

        var pipelines = new List<ICriterionPipeline>(CriterionDescriptors.All.Count);

        foreach (var feed in CriterionDescriptors.All)
        {
            var provider = new CriterionWorkUnitProvider(
                feed, source, options.Value, loggerFactory.CreateLogger($"Criterion.{feed.FeedId}.Units"));

            var reader = new CriterionSourceReader(
                feed, source, loggerFactory.CreateLogger($"Criterion.{feed.FeedId}.Reader"));

            var sink = new CriterionBatchingSink(
                feed.Table, options.Value.ConnectionString, options.Value.MergeBatchSize,
                loggerFactory.CreateLogger($"Criterion.{feed.FeedId}.Sink"));

            pipelines.Add(new CriterionPipeline(
                feed.FeedId, provider, reader, sink, loadLog, options.Value,
                loggerFactory.CreateLogger($"Criterion.{feed.FeedId}.Pipeline")));
        }

        return pipelines;
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Criterion.Module");
        var settings = services.GetRequiredService<IOptions<CriterionSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<ICriterionPipeline>>();

        WarnAboutConfiguration(settings, logger);

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("Criterion: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        // The source host and database are logged; the credentials and the assembled
        // Npgsql connection string never are.
        logger.LogInformation(
            "Criterion: running {Count} of {Total} feed(s) against {Host}:{Port}/{Database} over {DaysBack} day(s)",
            enabled.Count, pipelines.Count, settings.SourceHost, settings.SourcePort,
            settings.SourceDatabase, settings.DaysBack);

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one feed must not cost the other eight.
            LoaderRunResult result;
            try
            {
                result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Criterion: feed {Feed} threw out of its pipeline", pipeline.FeedId);
                failures.Add(pipeline.FeedId);
                continue;
            }

            if (!result.Success) failures.Add(pipeline.FeedId);

            aggregate = new LoaderRunResult
            {
                LoaderId = Id,
                Success = aggregate.Success && result.Success,
                WorkUnitsTotal = aggregate.WorkUnitsTotal + result.WorkUnitsTotal,
                WorkUnitsSucceeded = aggregate.WorkUnitsSucceeded + result.WorkUnitsSucceeded,
                WorkUnitsSkipped = aggregate.WorkUnitsSkipped + result.WorkUnitsSkipped,
                WorkUnitsFailed = aggregate.WorkUnitsFailed + result.WorkUnitsFailed,
                RecordsProcessed = aggregate.RecordsProcessed + result.RecordsProcessed,
                Duration = aggregate.Duration + result.Duration
            };
        }

        if (failures.Count > 0)
        {
            // LoaderRunResult is an init-only class, not a record — rebuild it.
            aggregate = new LoaderRunResult
            {
                LoaderId = Id,
                Success = false,
                ErrorMessage = $"Failed feed(s): {string.Join(", ", failures)}",
                WorkUnitsTotal = aggregate.WorkUnitsTotal,
                WorkUnitsSucceeded = aggregate.WorkUnitsSucceeded,
                WorkUnitsSkipped = aggregate.WorkUnitsSkipped,
                WorkUnitsFailed = aggregate.WorkUnitsFailed,
                RecordsProcessed = aggregate.RecordsProcessed,
                Duration = aggregate.Duration
            };
        }

        // Post-load validation is observational and must not change the run's outcome —
        // CriterionLoadValidator swallows its own failures. Scoped to the newest day the
        // window covered.
        await services.GetRequiredService<CriterionLoadValidator>()
            .ValidateAsync(CriterionTime.Today(context.StartedAtUtc), context.CancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Criterion run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration
    /// that "works" but silently does less than the operator expects, which is worse
    /// than a loud failure.
    /// </summary>
    internal static void WarnAboutConfiguration(CriterionSettings settings, ILogger logger)
    {
        foreach (var id in settings.EnabledFeeds)
            if (CriterionDescriptors.Find(id) is null)
                logger.LogWarning("Criterion: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);

        var daysBack = Math.Max(0, settings.DaysBack);
        var settledAfterDays = Math.Max(0, settings.SettledAfterDays);

        if (settledAfterDays < daysBack)
            logger.LogWarning(
                "Criterion SettledAfterDays={SettledAfterDays} is below DaysBack={DaysBack}: days aged " +
                "{From}..{To} get a STABLE resume key and will never be re-pulled, so revisions the source " +
                "publishes for those days would be missed. Prefer SettledAfterDays >= DaysBack (the shipped " +
                "30/30 default, all-hot)",
                settledAfterDays, daysBack, settledAfterDays + 1, daysBack);

        if (settings.DaysBack < 0)
            logger.LogWarning(
                "Criterion DaysBack={DaysBack} is negative and has been clamped to 0 — only today will load",
                settings.DaysBack);

        if (!settings.AlwaysReloadSnapshots)
            logger.LogWarning(
                "Criterion AlwaysReloadSnapshots=false: the five dimension feeds get a STABLE resume key, so " +
                "they load once and are skipped on every later run. The catalogs do change — expect " +
                "arm.Financial_Metadata and arm.Pipelines_Metadata to go stale");

        // Criterion requires SSL. Npgsql's Disable/Allow/Prefer would either fail the
        // handshake or, with Prefer, fall back to plaintext credentials on a proxy that
        // permits it.
        if (!settings.SourceSslMode.Equals("Require", StringComparison.OrdinalIgnoreCase) &&
            !settings.SourceSslMode.Equals("VerifyCA", StringComparison.OrdinalIgnoreCase) &&
            !settings.SourceSslMode.Equals("VerifyFull", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "Criterion SourceSslMode={Mode}: the source requires SSL, and a weaker mode either fails the " +
                "handshake or risks sending credentials in plaintext. 'Require' is the shipped default",
                settings.SourceSslMode);

        if (settings.SeriesPageSize <= 0)
            logger.LogWarning(
                "Criterion SeriesPageSize={Size} is not positive and has been clamped to 1 — every source row " +
                "becomes its own work unit, which will be extremely slow", settings.SeriesPageSize);
        else if (settings.SeriesPageSize > 1000)
            logger.LogWarning(
                "Criterion SeriesPageSize={Size} is large. Each source row carries a JSON array averaging " +
                "165 KB and reaching 1.7 MB that unpivots to ~2,400 rows, so a page of {Size} can buffer " +
                "millions of rows per work unit — multiplied by MaxConcurrentWorkUnits={Conc}. The shipped " +
                "default is 250",
                settings.SeriesPageSize, settings.SeriesPageSize, settings.MaxConcurrentWorkUnits);

        // The one setting most likely to be edited without appreciating the cost.
        if (settings.EnabledFeeds.Contains(CriterionFeed.FinancialSeriesData, StringComparer.OrdinalIgnoreCase)
            && daysBack > 30)
            logger.LogWarning(
                "Criterion DaysBack={DaysBack} with the {Feed} feed enabled: that feed reads roughly " +
                "1,530 source rows per day carrying ~230 MB of JSON, and merges on the order of 3.7 million " +
                "rows per day. At {DaysBack} days expect ~{Gb:N0} GB read and ~{Rows:N0} million rows merged " +
                "per run",
                daysBack, CriterionFeed.FinancialSeriesData, daysBack,
                daysBack * 0.23, daysBack * 3.7);
    }
}
