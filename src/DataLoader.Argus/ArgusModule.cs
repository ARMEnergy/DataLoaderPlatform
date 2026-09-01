using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus;

/// <summary>
/// Marker so <see cref="ArgusModule.RunAsync"/> can enumerate and order the
/// per-feed pipelines without their generics colliding with another loader's
/// registrations in the shared DI container.
/// </summary>
public interface IArgusPipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. The reader
/// already emits the sink's row type, so the transformer is the identity.
/// </summary>
public sealed class ArgusPipeline : LoaderPipelineBase<ArgusWorkUnit, ArgusRow, ArgusRow>, IArgusPipeline
{
    public ArgusPipeline(
        string feedId,
        IWorkUnitProvider<ArgusWorkUnit> provider,
        ISourceReader<ArgusWorkUnit, ArgusRow> source,
        ISink<ArgusRow> sink,
        ILoadLogRepository loadLog,
        ArgusSettings settings,
        ILogger logger)
        : base(ArgusModule.Id, provider, source, new IdentityTransformer<ArgusRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the Argus Media FTP loader.
///
/// <para>
/// 16 feeds, one pipeline each: 15 full-snapshot reference files in
/// <c>DOCUMENTATION</c> and the dated price series in <c>DCRDEUS</c>. Every feed's
/// column mapping, TVP and proc live in <see cref="ArgusDescriptors"/>, so this
/// class only wires them up — adding a feed is a descriptor plus its SQL, not new
/// plumbing.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no reference provider, no
/// FK, no shared state. Reference feeds run before the fact feed so a fresh
/// database has its lookups populated first, but that ordering is cosmetic —
/// nothing depends on it, and a failure in one pipeline must not stop the others.
/// </para>
/// <para>
/// Pipelines are built explicitly (the OPIS/ModernCommodities posture) rather than
/// via open-generic DI, so this loader's <c>IWorkUnitProvider&lt;T&gt;</c> and
/// <c>ISink&lt;T&gt;</c> registrations cannot collide with another loader's.
/// </para>
/// </summary>
public sealed class ArgusModule : ILoaderModule
{
    public const string Id = "Argus";

    public string LoaderId => Id;
    public string DisplayName => "Argus Media FTP (reference data + DCRDEUS price series)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the
        // SEE_DB resolver that turns the "SEE_DB" Username/Password sentinels into
        // real values from core.Param at run time.
        services.AddLoaderSettings<ArgusSettings>(configuration, Id);

        services.AddSingleton<IArgusFtp, ArgusFtpFileSystem>();
        services.AddSingleton<IArgusFileLog, SqlArgusFileLog>();

        // One listing per directory per run, shared by all 16 pipelines — without
        // it the 15 reference feeds would each LIST /DOCUMENTATION separately.
        services.AddSingleton<ArgusListingCache>();
        services.AddSingleton<ArgusLoadValidator>();

        // Registered (rather than newed in the factory) so RunAsync can read back
        // the newest date this run enumerated for validation scoping.
        services.AddSingleton<ArgusTimeSeriesWorkUnitProvider>(sp => new ArgusTimeSeriesWorkUnitProvider(
            sp.GetRequiredService<ArgusListingCache>(),
            sp.GetRequiredService<IOptions<ArgusSettings>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Argus.TimeSeriesUnits")));

        services.AddSingleton<IReadOnlyList<IArgusPipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<IArgusPipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<ArgusSettings>>();
        var settings = options.Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();

        var ftp = sp.GetRequiredService<IArgusFtp>();
        var fileLog = sp.GetRequiredService<IArgusFileLog>();
        var listings = sp.GetRequiredService<ArgusListingCache>();

        var pipelines = new List<IArgusPipeline>();

        foreach (var feed in ArgusDescriptors.Documentation)
        {
            var provider = new ArgusDocumentationWorkUnitProvider(
                feed, listings, fileLog, settings, loggerFactory.CreateLogger($"Argus.{feed.FeedId}.Units"));

            pipelines.Add(BuildOne(feed, provider, ftp, fileLog, options, loadLog, loggerFactory));
        }

        pipelines.Add(BuildOne(
            ArgusDescriptors.TimeSeries,
            sp.GetRequiredService<ArgusTimeSeriesWorkUnitProvider>(),
            ftp, fileLog, options, loadLog, loggerFactory));

        return pipelines;
    }

    private static IArgusPipeline BuildOne(
        ArgusFeedDescriptor feed,
        IWorkUnitProvider<ArgusWorkUnit> provider,
        IArgusFtp ftp,
        IArgusFileLog fileLog,
        IOptions<ArgusSettings> options,
        ILoadLogRepository loadLog,
        ILoggerFactory loggerFactory)
    {
        var source = new ArgusSourceReader(
            ftp, fileLog, options, loggerFactory.CreateLogger($"Argus.{feed.FeedId}.Reader"));

        var sink = new ArgusTableSink(
            feed, options.Value.ConnectionString, loggerFactory.CreateLogger($"Argus.{feed.FeedId}.Sink"));

        return new ArgusPipeline(
            feed.FeedId, provider, source, sink, loadLog, options.Value,
            loggerFactory.CreateLogger($"Argus.{feed.FeedId}.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Argus.Module");
        var settings = services.GetRequiredService<IOptions<ArgusSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<IArgusPipeline>>();

        WarnAboutUnknownFeedIds(settings, logger);

        var enabled = pipelines.Where(p => IsEnabled(p, settings)).ToList();
        if (enabled.Count == 0)
        {
            logger.LogWarning("Argus: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        logger.LogInformation("Argus: running {Count} of {Total} feed(s): {Feeds}",
            enabled.Count, pipelines.Count, string.Join(", ", enabled.Select(p => p.FeedId)));

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one lookup file must not cost the other 15.
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
                logger.LogError(ex, "Argus: feed {Feed} threw out of its pipeline", pipeline.FeedId);
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

        // Post-load validation is observational and must not change the run's
        // outcome — ArgusLoadValidator swallows its own failures. Scope it with the
        // newest DCRDEUS date this run's own enumeration saw (null when the fact
        // feed did not run, which validates the whole table) — no second listing.
        var latestDate = services.GetRequiredService<ArgusTimeSeriesWorkUnitProvider>().LastEnumeratedMaxDate;
        await services.GetRequiredService<ArgusLoadValidator>()
            .ValidateAsync(latestDate, context.CancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Argus run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    private static bool IsEnabled(IArgusPipeline pipeline, ArgusSettings settings)
    {
        if (string.Equals(pipeline.FeedId, ArgusDescriptors.TimeSeries.FeedId, StringComparison.OrdinalIgnoreCase))
            return settings.EnableTimeSeries;

        return settings.EnabledFeeds.Contains(pipeline.FeedId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A typo in <c>EnabledFeeds</c> would otherwise silently skip a feed. Warn
    /// rather than throw — a stale config entry should not stop the whole loader.
    /// </summary>
    private static void WarnAboutUnknownFeedIds(ArgusSettings settings, ILogger logger)
    {
        foreach (var id in settings.EnabledFeeds)
            if (ArgusDescriptors.Find(id) is null)
                logger.LogWarning("Argus: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);
    }
}
