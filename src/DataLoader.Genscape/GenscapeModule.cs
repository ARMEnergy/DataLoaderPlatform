using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Genscape;

/// <summary>
/// Marker so <see cref="GenscapeModule.RunAsync"/> can enumerate the per-feed pipelines
/// without their generics colliding with another loader's registrations in the shared
/// DI container.
/// </summary>
public interface IGenscapePipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. The reader
/// already emits the sink's row type, so the transformer is the identity.
/// </summary>
public sealed class GenscapePipeline
    : LoaderPipelineBase<GenscapeWorkUnit, GenscapeRow, GenscapeRow>, IGenscapePipeline
{
    public GenscapePipeline(
        string feedId,
        IWorkUnitProvider<GenscapeWorkUnit> provider,
        ISourceReader<GenscapeWorkUnit, GenscapeRow> source,
        ISink<GenscapeRow> sink,
        ILoadLogRepository loadLog,
        GenscapeSettings settings,
        ILogger logger)
        : base(GenscapeModule.Id, provider, source, new IdentityTransformer<GenscapeRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the Genscape oil-fundamentals loader.
///
/// <para>
/// Two feeds read Genscape's REST API (<c>api.genscape.com/oil-fundamentals/v1</c>,
/// authenticated with a <c>Gen-Api-Key</c> header) and merge into two <c>arm</c> tables
/// in the <c>Genscape</c> database. Everything is the platform's standard machinery —
/// work units, resume keys, TVP merges, the write gate — with three vendor quirks
/// confined to <see cref="GenscapeSourceReader"/> and <see cref="GenscapeTime"/>:
/// </para>
/// <list type="number">
///   <item>
///     the request window is <c>[startDate, endDate)</c>, so the reader sends the
///     window's last day PLUS ONE or the newest report is dropped every run;
///   </item>
///   <item>
///     responses are silently capped at 5,000 rows with the OLDEST dropped, so a
///     response at the cap is discarded and the window bisected;
///   </item>
///   <item>
///     the crude-storage endpoint's <c>week</c> is a week-of-MONTH, so
///     <c>Year</c>/<c>Week</c> are derived from <c>ReportDate</c> rather than read.
///   </item>
/// </list>
/// <para>
/// One feed, one pipeline, one table. Every column mapping, TVP and proc lives in
/// <see cref="GenscapeDescriptors"/>, so this class only wires them up — adding a feed
/// is a descriptor plus its SQL, not new plumbing.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK between
/// the target tables. A failure in one feed must not cost the other, so
/// <see cref="RunAsync"/> records the failure and carries on (the Argus/ICE/Criterion
/// posture).
/// </para>
/// <para>
/// Pipelines are built explicitly rather than via open-generic DI, so this loader's
/// <c>IWorkUnitProvider&lt;T&gt;</c> and <c>ISink&lt;T&gt;</c> registrations cannot
/// collide with another loader's.
/// </para>
/// </summary>
public sealed class GenscapeModule : ILoaderModule
{
    public const string Id = "Genscape";

    /// <summary>Named <c>HttpClient</c> for both feeds.</summary>
    public const string HttpClientName = "Genscape";

    public string LoaderId => Id;
    public string DisplayName => "Genscape oil fundamentals — weekly crude storage and transportation";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" ApiKey sentinel into a real value from
        // core.Param at run time.
        services.AddLoaderSettings<GenscapeSettings>(configuration, Id);

        services.AddSingleton<GenscapeLoadValidator>();

        // The Gen-Api-Key is a DEFAULT HEADER, not a query parameter, so it cannot
        // appear in a URL anywhere — but the factory's own logging handlers are removed
        // anyway, matching CWG/AGSI, so the only request lines in the log are the
        // sanitized ones the reader writes.
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<GenscapeSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, s.HttpTimeoutSeconds));
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            // The vendor's own examples send it, and the endpoint sits behind an
            // Imperva CDN; a cached weekly window would silently hide a revision.
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

            // An empty value would send a blank header and earn a 401 with no body,
            // which is harder to diagnose than a missing header. WarnAboutConfiguration
            // flags an unresolved SEE_DB sentinel at startup.
            if (!string.IsNullOrWhiteSpace(s.ApiKey))
                client.DefaultRequestHeaders.Add("Gen-Api-Key", s.ApiKey);
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<GenscapeSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Genscape.Http");
            return GenscapeHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        });

        services.AddSingleton<IReadOnlyList<IGenscapePipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<IGenscapePipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<GenscapeSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var pipelines = new List<IGenscapePipeline>(GenscapeDescriptors.All.Count);

        foreach (var feed in GenscapeDescriptors.All)
        {
            var provider = new GenscapeWorkUnitProvider(
                feed, options.Value, loggerFactory.CreateLogger($"Genscape.{feed.FeedId}.Units"));

            var reader = new GenscapeSourceReader(
                feed, httpFactory.CreateClient(HttpClientName), options.Value,
                loggerFactory.CreateLogger($"Genscape.{feed.FeedId}.Reader"));

            var sink = new GenscapeTvpSink(
                feed.Table, options.Value.ConnectionString,
                loggerFactory.CreateLogger($"Genscape.{feed.FeedId}.Sink"));

            pipelines.Add(new GenscapePipeline(
                feed.FeedId, provider, reader, sink, loadLog, options.Value,
                loggerFactory.CreateLogger($"Genscape.{feed.FeedId}.Pipeline")));
        }

        return pipelines;
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Genscape.Module");
        var settings = services.GetRequiredService<IOptions<GenscapeSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<IGenscapePipeline>>();

        WarnAboutConfiguration(settings, logger);

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("Genscape: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        // The base URL and the regions are logged; the Gen-Api-Key never is.
        logger.LogInformation(
            "Genscape: running {Count} of {Total} feed(s) against {BaseUrl} for region(s) {Regions} " +
            "over {DaysBack} day(s)",
            enabled.Count, pipelines.Count, settings.BaseUrl,
            string.Join(", ", settings.Regions), settings.DaysBack);

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one feed must not cost the other.
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
                logger.LogError(ex, "Genscape: feed {Feed} threw out of its pipeline", pipeline.FeedId);
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
        // GenscapeLoadValidator swallows its own failures.
        await services.GetRequiredService<GenscapeLoadValidator>()
            .ValidateAsync(GenscapeTime.Today(context.StartedAtUtc), context.CancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Genscape run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration that
    /// "works" but silently does less than the operator expects, which is worse than a
    /// loud failure.
    /// </summary>
    internal static void WarnAboutConfiguration(GenscapeSettings settings, ILogger logger)
    {
        foreach (var id in settings.EnabledFeeds)
            if (GenscapeDescriptors.Find(id) is null)
                logger.LogWarning("Genscape: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);

        var daysBack = Math.Max(0, settings.DaysBack);
        var settledAfterDays = Math.Max(0, settings.SettledAfterDays);

        if (settledAfterDays < daysBack)
            logger.LogWarning(
                "Genscape SettledAfterDays={SettledAfterDays} is below DaysBack={DaysBack}: chunks whose " +
                "newest day is aged {From}..{To} get a STABLE resume key and will never be re-pulled, so the " +
                "revisions this feed exists to deliver would be missed. Prefer SettledAfterDays >= DaysBack " +
                "(the shipped 30/30 default, all-hot)",
                settledAfterDays, daysBack, settledAfterDays + 1, daysBack);

        if (settings.DaysBack < 0)
            logger.LogWarning(
                "Genscape DaysBack={DaysBack} is negative and has been clamped to 0 — only today will load",
                settings.DaysBack);

        // An unresolved sentinel means the SEE_DB lookup found no core.Param row. The
        // request would go out with the literal string as the key and come back 401 with
        // an EMPTY body, so saying so here is worth a lot.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) ||
            settings.ApiKey.Equals("SEE_DB", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "Genscape ApiKey is still the unresolved '{Sentinel}' sentinel: no " +
                "core.Param(LoaderName='Genscape', ParamName='ApiKey') row was found. Every request will " +
                "fail 401 with an empty body",
                settings.ApiKey);

        if (!settings.Revision.Equals("revised", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "Genscape Revision={Revision}: 'revised' is the only value the API was observed to accept — " +
                "anything else is rejected with 400 'Must have a value specified', which fails every work unit",
                settings.Revision);

        var unknownRegions = settings.Regions
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Where(r => !r.Trim().Equals("NorthAmerica", StringComparison.OrdinalIgnoreCase)
                     && !r.Trim().Equals("GulfCoast", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (unknownRegions.Count > 0)
            logger.LogWarning(
                "Genscape Regions contains {Regions}, which are not the two request regions this loader " +
                "verified (NorthAmerica, GulfCoast). An unknown region is rejected with 400, failing that " +
                "work unit",
                string.Join(", ", unknownRegions));

        if (settings.Regions.Count(r => !string.IsNullOrWhiteSpace(r)) == 0)
            logger.LogWarning(
                "Genscape Regions is empty: the loader will produce no work units. Both endpoints require a " +
                "region and answer 404 without one");

        // The cap guard is the whole defence against silent truncation.
        if (settings.MaxRowsPerResponse > 5000)
            logger.LogWarning(
                "Genscape MaxRowsPerResponse={Cap} is above the 5,000 rows the API was measured to return at " +
                "most. Above the real cap the guard never fires and truncated responses load as if complete " +
                "— losing the OLDEST rows of the window, silently",
                settings.MaxRowsPerResponse);

        if (settings.WindowChunkDays > 3650)
            logger.LogWarning(
                "Genscape WindowChunkDays={Chunk} is very large. At the observed density (~460 rows per year " +
                "for the busiest feed/region) a chunk beyond ~10 years will hit the 5,000-row cap and be " +
                "bisected repeatedly, costing extra requests",
                settings.WindowChunkDays);
    }
}
