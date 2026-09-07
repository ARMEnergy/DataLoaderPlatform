using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EOX;

/// <summary>
/// Marker so <see cref="EoxModule.RunAsync"/> can enumerate and order the per-feed
/// pipelines without their generics colliding with another loader's registrations
/// in the shared DI container.
/// </summary>
public interface IEoxPipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. The reader
/// already emits the sink's row type, so the transformer is the identity.
/// </summary>
public sealed class EoxPipeline : LoaderPipelineBase<EoxWorkUnit, EoxRow, EoxRow>, IEoxPipeline
{
    public EoxPipeline(
        string feedId,
        IWorkUnitProvider<EoxWorkUnit> provider,
        ISourceReader<EoxWorkUnit, EoxRow> source,
        ISink<EoxRow> sink,
        ILoadLogRepository loadLog,
        EoxSettings settings,
        ILogger logger)
        : base(EoxModule.Id, provider, source, new IdentityTransformer<EoxRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the EOX Live FTP loader.
///
/// <para>
/// Three feeds, one pipeline each — crude oil, natural gas and NGL end-of-day
/// broker curves, one CSV per trading day per feed on <c>ftp.eoxlive.com</c>.
/// Every feed's column mapping, TVP and merge proc lives in
/// <see cref="EoxDescriptors"/>, so this class only wires them up.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK
/// between the three tables. A failure in one is recorded and the run continues,
/// because losing the NGL file must not cost the other two.
/// </para>
/// <para>
/// Pipelines are built explicitly (the OPIS/Argus posture) rather than via
/// open-generic DI, so this loader's <c>IWorkUnitProvider&lt;T&gt;</c> and
/// <c>ISink&lt;T&gt;</c> registrations cannot collide with another loader's in the
/// shared container.
/// </para>
/// </summary>
public sealed class EoxModule : ILoaderModule
{
    public const string Id = "EOX";

    public string LoaderId => Id;
    public string DisplayName => "EOX Live FTP (crude, natural gas and NGL EOD curves)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" Username/Password sentinels into real
        // values from core.Param at run time.
        services.AddLoaderSettings<EoxSettings>(configuration, Id);

        services.AddSingleton<IEoxFtp, EoxFtpFileSystem>();
        services.AddSingleton<IEoxFileLog, SqlEoxFileLog>();

        // ONE listing of the 18k-entry root per run, shared by all three pipelines.
        services.AddSingleton<EoxListingCache>();
        services.AddSingleton<EoxLoadValidator>();

        // Registered (rather than newed in the factory) so RunAsync can read back
        // the newest curve date this run enumerated, for validation scoping.
        // Keyed BY FEED ID rather than by list position: the pipelines and the
        // providers are built in two places, and pairing them by index would break
        // silently — each feed would get another feed's units — if either list were
        // ever reordered or filtered.
        services.AddSingleton<IReadOnlyDictionary<string, EoxWorkUnitProvider>>(BuildProviders);
        services.AddSingleton<IReadOnlyList<IEoxPipeline>>(BuildPipelines);
    }

    private static IReadOnlyDictionary<string, EoxWorkUnitProvider> BuildProviders(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<EoxSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var listings = sp.GetRequiredService<EoxListingCache>();
        var fileLog = sp.GetRequiredService<IEoxFileLog>();

        return EoxDescriptors.All.ToDictionary(
            feed => feed.FeedId,
            feed => new EoxWorkUnitProvider(
                feed, listings, fileLog, settings, loggerFactory.CreateLogger($"EOX.{feed.FeedId}.Units")),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IEoxPipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<EoxSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();

        var ftp = sp.GetRequiredService<IEoxFtp>();
        var fileLog = sp.GetRequiredService<IEoxFileLog>();
        var providers = sp.GetRequiredService<IReadOnlyDictionary<string, EoxWorkUnitProvider>>();

        var pipelines = new List<IEoxPipeline>(EoxDescriptors.All.Count);

        foreach (var feed in EoxDescriptors.All)
        {
            var source = new EoxSourceReader(
                ftp, fileLog, options, loggerFactory.CreateLogger($"EOX.{feed.FeedId}.Reader"));

            var sink = new EoxTableSink(
                feed, options.Value.ConnectionString, loggerFactory.CreateLogger($"EOX.{feed.FeedId}.Sink"));

            pipelines.Add(new EoxPipeline(
                feed.FeedId, providers[feed.FeedId], source, sink, loadLog, options.Value,
                loggerFactory.CreateLogger($"EOX.{feed.FeedId}.Pipeline")));
        }

        return pipelines;
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("EOX.Module");
        var settings = services.GetRequiredService<IOptions<EoxSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<IEoxPipeline>>();

        // The descriptor registry is the contract for three TVPs. A bad edit there
        // would otherwise surface as a server-side type error mid-run, or worse, as
        // silently shifted columns — so fail before touching the network.
        var problems = EoxDescriptors.Validate();
        if (problems.Count > 0)
        {
            var message = "EOX descriptor registry is invalid: " + string.Join("; ", problems);
            logger.LogCritical("{Message}", message);
            return LoaderRunResult.Failed(Id, message, TimeSpan.Zero);
        }

        WarnAboutConfiguration(settings, logger);

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("EOX: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        logger.LogInformation("EOX: running {Count} of {Total} feed(s): {Feeds}",
            enabled.Count, pipelines.Count, string.Join(", ", enabled.Select(p => p.FeedId)));

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one series must not cost the other two.
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
                logger.LogError(ex, "EOX: feed {Feed} threw out of its pipeline", pipeline.FeedId);
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
        // outcome — EoxLoadValidator swallows its own failures. Scope it with the
        // newest curve date this run's own enumeration saw (null when nothing ran,
        // which validates the whole table) — no second FTP listing.
        var latestDate = services.GetRequiredService<IReadOnlyDictionary<string, EoxWorkUnitProvider>>()
            .Values
            .Select(p => p.LastEnumeratedMaxDate)
            .Where(d => d.HasValue)
            .DefaultIfEmpty(null)
            .Max();

        await services.GetRequiredService<EoxLoadValidator>()
            .ValidateAsync(latestDate, context.CancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "EOX run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup warnings for configurations that are legal but nearly always a
    /// mistake, or that change behaviour enough to be worth seeing in the run log
    /// rather than only in appsettings.
    /// </summary>
    private static void WarnAboutConfiguration(EoxSettings settings, ILogger logger)
    {
        // A typo in EnabledFeeds would otherwise silently skip a feed. Warn rather
        // than throw — a stale config entry should not stop the whole loader.
        foreach (var id in settings.EnabledFeeds)
            if (EoxDescriptors.Find(id) is null)
                logger.LogWarning("EOX: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);

        if (EoxTime.UsingUtcFallback)
            logger.LogWarning(
                "EOX: no US Central time-zone entry on this host — the curve-date window falls back to UTC, " +
                "so 'today' arrives up to 6 hours early and the newest date may be NotAvailable for a while");

        if (settings.DaysBack < 0)
            logger.LogWarning("EOX: DaysBack={DaysBack} is negative — clamped to 0", settings.DaysBack);

        if (settings.SettledAfterDays < 0)
            logger.LogWarning("EOX: SettledAfterDays={Days} is negative — clamped to 0", settings.SettledAfterDays);

        if (settings.SettledAfterDays < settings.DaysBack)
            logger.LogInformation(
                "EOX: SettledAfterDays={Settled} < DaysBack={Back} — dates older than {Settled} day(s) are settled and " +
                "reload only when EOX republishes the file (detected by its last-modified stamp and size)",
                settings.SettledAfterDays, settings.DaysBack, settings.SettledAfterDays);

        if (settings.ForceReprocess)
            logger.LogWarning(
                "EOX: ForceReprocess is ON — every date in the window re-downloads and re-merges, " +
                "regardless of whether the file changed");

        if (!settings.UseFtps)
            logger.LogWarning(
                "EOX: UseFtps is OFF — the account password crosses the network in cleartext. " +
                "ftp.eoxlive.com advertises AUTH TLS and explicit FTPS is the shipped default");
    }
}
