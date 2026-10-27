using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NUCORE;

/// <summary>
/// Plugin entry point for the NUCORE loader (ARM's own risk system, read through the
/// NUCORE.IntegrationService gateway).
///
/// <para>
/// Five risk endpoints land in five <c>dbo</c> tables in the <c>NUCORE</c> database. The
/// shape is the platform's standard machinery - work units, resume keys, TVP merges, the
/// write gate - with the vendor's quirks confined to <see cref="NucoreSourceReader"/> and
/// <see cref="NucoreHttp"/>. Five of those quirks are silent if you get them wrong:
/// </para>
/// <list type="number">
///   <item>
///     <b><c>PnlExplain</c> ignores <c>systemDate</c></b>; its parameter is <c>CurDate</c>.
///     Asked the wrong way it returns HTTP 200 and the LATEST run, for every date.
///   </item>
///   <item>
///     <b><c>PositionDetails</c> has no date parameter at all</b>, so it is partitioned by
///     trade month and read incrementally against a watermark.
///   </item>
///   <item>
///     <b>Omitting a date parameter returns 200 with the latest day</b>, not an error - so
///     every row's echoed date is verified against the date requested.
///   </item>
///   <item>
///     <b>An empty read and a malformed request look nothing alike but neither is loud</b>:
///     weekends/holidays/pre-history answer 200 with zero rows, while a garbage date answers
///     HTTP 500 rather than 400 - which is why 500 is excluded from the retry predicate.
///   </item>
///   <item>
///     <b><c>pageSize=500000</c> answers HTTP 504</b> on <c>PositionDetails</c>, because one
///     unfiltered call is 2,468,453 rows.
///   </item>
/// </list>
///
/// <para>
/// One endpoint, one pipeline, one table. Every column mapping, TVP and proc comes from
/// <see cref="NucoreDescriptors"/> and the generated <see cref="NucoreColumns"/>, so adding
/// an endpoint is a descriptor plus its SQL, not new plumbing.
/// </para>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK between the
/// target tables. A failure in one endpoint must not cost the others, so
/// <see cref="RunAsync"/> records the failure and carries on.
/// </para>
/// </summary>
public sealed class NucoreModule : ILoaderModule
{
    public const string Id = "NUCORE";

    /// <summary>Named <c>HttpClient</c> for every risk endpoint.</summary>
    public const string HttpClientName = "NUCORE";

    public string LoaderId => Id;
    public string DisplayName => "NUCORE - ARM risk VaR, positions, P&L explain and volatilities";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Fail at startup rather than mid-run if the registry is internally inconsistent -
        // a key/nullability mismatch would otherwise surface as a failing MERGE or silently
        // duplicated rows.
        NucoreDescriptors.Validate();

        // AddLoaderSettings (not a bare Configure<>) - this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" ApiKey sentinel into a real value from core.Param
        // at run time.
        services.AddLoaderSettings<NucoreSettings>(configuration, Id);

        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<NucoreSettings>>().Value;

            // ⚠ INFINITE on purpose. HttpClient.Timeout bounds a whole RETRIED operation,
            // because the Polly handler runs inside it - so a finite value here would be
            // shared across all attempts and would surface a vendor 504 as a client timeout.
            // The real budget is per-attempt, inside NucoreHttp.Build.
            client.Timeout = Timeout.InfiniteTimeSpan;

            client.DefaultRequestHeaders.Add("Accept", "application/json");

            // 🔒 The key is a HEADER, so it never appears in a URL or in the request line the
            // factory logs. It is still kept out of every message this loader emits via
            // NucoreHttp.Redact.
            if (!string.IsNullOrWhiteSpace(s.ApiKey))
                client.DefaultRequestHeaders.Add("X-Api-Key", s.ApiKey);
        })
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<NucoreSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("NUCORE.Http");
            return NucoreHttp.Build(s.RetryCount, s.RetryDelayMs, s.HttpTimeoutSeconds, log);
        });

        services.AddSingleton<IReadOnlyList<INucorePipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<INucorePipeline> BuildPipelines(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<NucoreSettings>>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var settings = options.Value;
        var pipelines = new List<INucorePipeline>(NucoreDescriptors.All.Count);

        foreach (var endpoint in NucoreDescriptors.All)
        {
            var provider = new NucoreWorkUnitProvider(
                endpoint, settings, loggerFactory.CreateLogger($"NUCORE.{endpoint.Name}.Units"));

            var reader = new NucoreSourceReader(
                endpoint, httpFactory.CreateClient(HttpClientName), settings,
                loggerFactory.CreateLogger($"NUCORE.{endpoint.Name}.Reader"));

            var sink = new NucoreTableSink(
                endpoint, settings.ConnectionString,
                loggerFactory.CreateLogger($"NUCORE.{endpoint.Name}.Sink"));

            var watermarks = new NucoreWatermarkStore(
                settings.ConnectionString,
                loggerFactory.CreateLogger($"NUCORE.{endpoint.Name}.Watermark"));

            pipelines.Add(new NucorePipeline(
                endpoint, provider, reader, sink, watermarks, loadLog, settings,
                loggerFactory.CreateLogger($"NUCORE.{endpoint.Name}.Pipeline")));
        }

        return pipelines;
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("NUCORE.Module");
        var settings = services.GetRequiredService<IOptions<NucoreSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<INucorePipeline>>();

        WarnAboutConfiguration(settings, logger);

        var enabled = pipelines
            .Where(p => settings.EnabledEndpoints.Contains(p.EndpointName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("NUCORE: no endpoints enabled - nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        // The base URL is logged; the api key never is.
        logger.LogInformation(
            "NUCORE: running {Count} of {Total} endpoint(s) against {BaseUrl}; window from " +
            "{Start}, settled after {Settled} day(s), pageSize {PageSize}, batch {Batch}",
            enabled.Count, pipelines.Count, settings.BaseUrl,
            string.IsNullOrWhiteSpace(settings.StartDate) ? $"{settings.DaysBack} day(s) back" : settings.StartDate,
            settings.SettledAfterDays, settings.PageSize, settings.BatchSize);

        var results = new List<LoaderRunResult>();
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // An endpoint's failure is recorded and the run continues: the pipelines are
            // independent, and losing one must not cost the other four.
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
                logger.LogError(ex, "NUCORE: endpoint {Endpoint} threw out of its pipeline",
                    pipeline.EndpointName);
                failures.Add(pipeline.EndpointName);
                results.Add(LoaderRunResult.Failed(Id, $"{pipeline.EndpointName}: {ex.Message}", TimeSpan.Zero));
                continue;
            }

            if (!result.Success) failures.Add(pipeline.EndpointName);
            results.Add(result);
        }

        var duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
        var errorMessage = failures.Count > 0 ? $"Failed endpoint(s): {string.Join(", ", failures)}" : null;
        var aggregate = LoaderRunResult.Aggregate(Id, results, duration, errorMessage);

        logger.LogInformation(
            "NUCORE run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, " +
            "{Records} row(s) changed",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration that
    /// "works" but silently does less - or far more - than the operator expects, which is
    /// worse than a loud failure.
    /// </summary>
    internal static void WarnAboutConfiguration(NucoreSettings settings, ILogger logger)
    {
        foreach (var name in settings.EnabledEndpoints)
            if (NucoreDescriptors.Find(name) is null)
                logger.LogWarning(
                    "NUCORE: EnabledEndpoints contains unknown endpoint '{Endpoint}' - ignored. " +
                    "Known endpoints: {Known}",
                    name, string.Join(", ", NucoreDescriptors.All.Select(e => e.Name)));

        // ⚠ The specific trap this loader was built around. PositionDetails answers HTTP 504
        // at the 500,000 the endpoints were originally specified with.
        if (settings.PageSize > 100_000)
            logger.LogWarning(
                "NUCORE PageSize={PageSize} is very large. PositionDetails returns 2,468,453 rows " +
                "unfiltered and answers HTTP 504 at pageSize=500000; 20,000 is the shipped default " +
                "and returns in roughly 30 seconds", settings.PageSize);

        if (settings.PageSize < 1)
            logger.LogWarning(
                "NUCORE PageSize={PageSize} is not positive; every request will ask for no rows",
                settings.PageSize);

        if (settings.BatchSize < 1)
            logger.LogWarning(
                "NUCORE BatchSize={Batch} is not positive; the reader will merge one row per call, " +
                "which works but is pathologically slow for a 147,908-row day", settings.BatchSize);

        // The budget is per ATTEMPT and must cover a whole page.
        if (settings.HttpTimeoutSeconds < 120)
            logger.LogWarning(
                "NUCORE HttpTimeoutSeconds={Timeout} is below 120. It bounds ONE attempt, and a " +
                "single 20,000-row PositionDetails page takes ~30s while Volatilities returns " +
                "147,908 rows in one response", settings.HttpTimeoutSeconds);

        // ⚠ The coupling that bit EIA: retries are serial, so the worst case is
        // (RetryCount + 1) x HttpTimeoutSeconds plus backoff. If that exceeds the per-unit
        // budget the unit is cancelled mid-retry and the log blames the wrong setting.
        var worstCase = (settings.RetryCount + 1) * settings.HttpTimeoutSeconds;
        if (settings.WorkUnitTimeoutSeconds > 0 && worstCase > settings.WorkUnitTimeoutSeconds)
            logger.LogWarning(
                "NUCORE timeout budgets are inconsistent: (RetryCount {Retries} + 1) x " +
                "HttpTimeoutSeconds {Http} = {Worst}s exceeds WorkUnitTimeoutSeconds {Unit}s, so a " +
                "unit can be cancelled mid-retry and reported as a unit timeout rather than the " +
                "vendor error that actually caused it",
                settings.RetryCount, settings.HttpTimeoutSeconds, worstCase,
                settings.WorkUnitTimeoutSeconds);

        if (settings.SettledAfterDays < 0)
            logger.LogWarning(
                "NUCORE SettledAfterDays={Settled} is negative; every date will be treated as hot " +
                "and re-read on every run", settings.SettledAfterDays);

        if (!string.IsNullOrWhiteSpace(settings.StartDate) &&
            !DateTime.TryParse(settings.StartDate, out _))
            logger.LogWarning(
                "NUCORE StartDate='{Start}' is not a parseable date; falling back to DaysBack={Days}",
                settings.StartDate, settings.DaysBack);

        if (!NucoreTime.TryParseTradeMonth(settings.TradeMonthFrom, out _))
            logger.LogWarning(
                "NUCORE TradeMonthFrom='{Value}' is not yyyyMM; PositionDetails will fall back to " +
                "202501", settings.TradeMonthFrom);

        // An unresolved sentinel means the SEE_DB lookup found no core.Param row. Every
        // request would go out with the literal string as the key and come back 401.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) ||
            settings.ApiKey.Equals("SEE_DB", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "NUCORE ApiKey is still the unresolved '{Sentinel}' sentinel: no " +
                "core.Param(LoaderName='NUCORE', ParamName='ApiKey') row was found. Every request " +
                "will fail with HTTP 401", settings.ApiKey);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            logger.LogWarning("NUCORE ConnectionString is empty - every merge will fail");
    }
}
