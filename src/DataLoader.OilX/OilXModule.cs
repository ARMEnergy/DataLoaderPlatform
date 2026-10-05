using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OilX;

/// <summary>
/// Plugin entry point for the OilX loader (Energy Aspects' OilX product line).
///
/// <para>
/// Eight CSV feeds are published to S3 once or more each day and merged into eight
/// <c>arm</c> tables in the <c>OilX</c> database. The shape is the platform's standard
/// machinery — work units, resume keys, TVP merges, the write gate — with the vendor's
/// quirks confined to <see cref="OilXManifestClient"/> and <see cref="OilXCsv"/>:
/// </para>
/// <list type="number">
///   <item>
///     the API hands back a MANIFEST of presigned S3 URLs, not the CSVs, and those URLs
///     expire in five hours — so the manifest is fetched per day at the moment that day
///     is processed, never once up front;
///   </item>
///   <item>
///     an empty day answers <b>HTTP 422</b>, the same status as a bad feed name, and the
///     two are told apart only by the error message prefix;
///   </item>
///   <item>
///     a day publishes up to FOUR snapshots per feed, all carrying the same in-file
///     <c>RunDate</c>, so they must merge oldest-published-first for the newest value to
///     win;
///   </item>
///   <item>
///     each daily file is a FULL snapshot of all history (~2.07M rows/day across the
///     eight feeds, 208 MB for CargoTracking alone), so everything streams and merges in
///     batches.
///   </item>
/// </list>
///
/// <para>
/// One feed, one pipeline, one table. Every column mapping, TVP and proc lives in
/// <see cref="OilXDescriptors"/>, so this class only wires them up — adding a feed is a
/// descriptor plus its SQL, not new plumbing.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK between
/// the target tables. A failure in one feed must not cost the others, so
/// <see cref="RunAsync"/> records the failure and carries on (the
/// Argus/ICE/Criterion/Genscape posture).
/// </para>
/// <para>
/// Pipelines are built explicitly rather than via open-generic DI, so this loader's
/// <c>IWorkUnitProvider&lt;T&gt;</c> and <c>ISink&lt;T&gt;</c> registrations cannot
/// collide with another loader's.
/// </para>
/// </summary>
public sealed class OilXModule : ILoaderModule
{
    public const string Id = "OilX";

    /// <summary>Named <c>HttpClient</c> for the manifest calls and the S3 downloads alike.</summary>
    public const string HttpClientName = "OilX";

    public string LoaderId => Id;
    public string DisplayName => "OilX — Energy Aspects crude cargo, flow and balance CSVs";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Fail at startup rather than mid-run if the registry is internally inconsistent
        // -- a key/IsKey mismatch would otherwise surface as silently forked RowIds.
        OilXDescriptors.Validate();

        // AddLoaderSettings (not a bare Configure<>) -- this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" ApiKey sentinel into a real value from
        // core.Param at run time.
        services.AddLoaderSettings<OilXSettings>(configuration, Id);

        services.AddSingleton<OilXLoadValidator>();

        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<OilXSettings>>().Value;

            // Covers a whole 208 MB download, not just the headers.
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, s.HttpTimeoutSeconds));
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/csv, */*");
        })
        // 🔒 LOAD-BEARING. Unlike every other HTTP loader here, OilX authenticates with a
        // QUERY PARAMETER, so the factory's own request-logging handlers would write the
        // api_key into the log on every call. Removing them means the only request lines
        // in the log are the sanitized ones this loader writes itself.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<OilXSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OilX.Http");
            return OilXHttp.Build(s.RetryCount, s.RetryDelayMs, log);
        });

        services.AddSingleton<IReadOnlyList<IOilXPipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<IOilXPipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<OilXSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var pipelines = new List<IOilXPipeline>(OilXDescriptors.All.Count);

        foreach (var feed in OilXDescriptors.All)
        {
            var provider = new OilXWorkUnitProvider(
                feed, options.Value, loggerFactory.CreateLogger($"OilX.{feed.FeedId}.Units"));

            var manifest = new OilXManifestClient(
                httpFactory.CreateClient(HttpClientName), options.Value,
                loggerFactory.CreateLogger($"OilX.{feed.FeedId}.Manifest"));

            var reader = new OilXSourceReader(
                feed, httpFactory.CreateClient(HttpClientName), options.Value,
                loggerFactory.CreateLogger($"OilX.{feed.FeedId}.Reader"));

            var sink = new OilXTableSink(
                feed, options.Value.ConnectionString,
                loggerFactory.CreateLogger($"OilX.{feed.FeedId}.Sink"));

            pipelines.Add(new OilXPipeline(
                feed, provider, manifest, reader, sink, loadLog, options.Value,
                loggerFactory.CreateLogger($"OilX.{feed.FeedId}.Pipeline")));
        }

        return pipelines;
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("OilX.Module");
        var settings = services.GetRequiredService<IOptions<OilXSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<IOilXPipeline>>();

        WarnAboutConfiguration(settings, logger);

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("OilX: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        // The base URL is logged; the api_key never is.
        logger.LogInformation(
            "OilX: running {Count} of {Total} feed(s) against {BaseUrl} over {DaysBack} day(s) back, " +
            "settled after {Settled} day(s), batch {Batch}",
            enabled.Count, pipelines.Count, settings.BaseUrl, settings.DaysBack,
            settings.SettledAfterDays, settings.BatchSize);

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one feed must not cost the other seven.
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
                logger.LogError(ex, "OilX: feed {Feed} threw out of its pipeline", pipeline.FeedId);
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
            // LoaderRunResult is an init-only class, not a record -- rebuild it.
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

        // Post-load validation is observational and must not change the run's outcome --
        // OilXLoadValidator swallows its own failures.
        await services.GetRequiredService<OilXLoadValidator>()
            .ValidateAsync(context.CancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "OilX run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, " +
            "{Records} row(s) changed",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration that
    /// "works" but silently does less — or far more — than the operator expects, which is
    /// worse than a loud failure.
    /// </summary>
    internal static void WarnAboutConfiguration(OilXSettings settings, ILogger logger)
    {
        foreach (var id in settings.EnabledFeeds)
            if (OilXDescriptors.Find(id) is null)
                logger.LogWarning(
                    "OilX: EnabledFeeds contains unknown feed id '{FeedId}' — ignored. The vendor's " +
                    "catalogue is at {BaseUrl}/csv/list", id, settings.BaseUrl.TrimEnd('/'));

        if (settings.DaysBack < 0)
            logger.LogWarning(
                "OilX DaysBack={DaysBack} is negative and has been clamped to 0 — only today will load",
                settings.DaysBack);

        // ⚠ The inverse of the warning Genscape and ICE carry. Those vendors RESTATE
        // recent data, so a settled zone would freeze stale values and they ship
        // all-hot. OilX never restates -- a published day's file is immutable -- so here
        // a large settled threshold is the misconfiguration: it re-downloads gigabytes
        // every run to re-merge rows that provably cannot have changed.
        if (settings.SettledAfterDays >= settings.DaysBack && settings.DaysBack > 0)
            logger.LogWarning(
                "OilX SettledAfterDays={Settled} is at or above DaysBack={DaysBack}, so EVERY day in " +
                "the window stays hot and is re-downloaded on every run — roughly {Gb:F1} GB and " +
                "{Rows:N0} row(s) re-merged per run. OilX files are immutable once published, so a " +
                "small value (the shipped default is 1, covering only today's still-growing file set) " +
                "gets the same data for a fraction of the work",
                settings.SettledAfterDays, settings.DaysBack,
                0.21 * (settings.DaysBack + 1), 2_065_000L * (settings.DaysBack + 1));

        if (settings.BatchSize < 1)
            logger.LogWarning(
                "OilX BatchSize={Batch} is not positive; the reader will merge one row per call, " +
                "which works but is pathologically slow for a 396,866-row file", settings.BatchSize);

        if (settings.BatchSize > 100_000)
            logger.LogWarning(
                "OilX BatchSize={Batch} is very large. The whole batch is held in memory as a " +
                "DataTable before the merge, and CargoTracking rows carry three VARCHAR(MAX) columns",
                settings.BatchSize);

        // The HTTP timeout covers a whole download, not just the response headers.
        if (settings.HttpTimeoutSeconds < 300)
            logger.LogWarning(
                "OilX HttpTimeoutSeconds={Timeout} is below 300. It bounds the ENTIRE download, and a " +
                "single CargoTracking snapshot is ~208 MB — a short timeout fails the largest feeds " +
                "while the small ones keep working, which looks like a vendor problem rather than a " +
                "configuration one", settings.HttpTimeoutSeconds);

        // A unit may download four 208 MB files in sequence.
        if (settings.WorkUnitTimeoutSeconds > 0 && settings.WorkUnitTimeoutSeconds < 1800)
            logger.LogWarning(
                "OilX WorkUnitTimeoutSeconds={Timeout} is below 1800. One work unit downloads and " +
                "merges EVERY snapshot a feed published that day — up to four files of ~208 MB for " +
                "CargoTracking", settings.WorkUnitTimeoutSeconds);

        // An unresolved sentinel means the SEE_DB lookup found no core.Param row. Every
        // request would go out with the literal string as the key and come back 401.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) ||
            settings.ApiKey.Equals("SEE_DB", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "OilX ApiKey is still the unresolved '{Sentinel}' sentinel: no " +
                "core.Param(LoaderName='OilX', ParamName='ApiKey') row was found. Every request will " +
                "fail with HTTP 401 'Authorization key is invalid'", settings.ApiKey);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            logger.LogWarning("OilX ConnectionString is empty — every merge will fail");
    }
}
