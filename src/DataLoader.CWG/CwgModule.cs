using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CWG;

/// <summary>
/// Plugin entry point for the CWG (Commodity Weather Group) loader. One module,
/// eighteen closed per-endpoint pipelines (design §1), toggled by
/// <c>EnabledEndpoints[]</c>. Each pipeline is assembled explicitly in a factory
/// closure that <c>new</c>s a descriptor-bound provider + reader + per-endpoint
/// sink, so the shared <see cref="CwgWorkUnit"/> generics are never resolved by
/// the container — avoiding the shared-generic-service DI collision (§1.1). All
/// 18 share one rate-limited <see cref="HttpClient"/> and run sequentially.
/// </summary>
public sealed class CwgModule : ILoaderModule
{
    public const string Id = "CWG";
    public const string HttpClientName = "CWG";

    public string LoaderId => Id;
    public string DisplayName => "Commodity Weather Group (HTTP/CSV — 18 forecast/observation/renewable endpoints)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLoaderSettings<CwgSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler.
        services.AddSingleton<CwgRateLimiter>();
        services.AddTransient<CwgRateLimitingHandler>();

        // Shared, rate-limited HttpClient. Handler order matters: the retry policy is
        // OUTER (it drives the retry loop) and the throttle is INNER, so the limiter
        // paces every attempt — the first and each retry (design §1.3).
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<CwgSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "text/csv");
        })
        // Suppress IHttpClientFactory's default logging handlers: they log the full
        // request URI — including the ?apikey=… query string — which the reader
        // deliberately never does. The reader logs the sanitized relative path, so
        // diagnostics are preserved. Scoped to the CWG client only.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<CwgSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CWG.Http");
            return CwgHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<CwgRateLimitingHandler>();

        services.AddSingleton<ICwgFileLog, SqlCwgFileLog>();

        // The 18 closed per-endpoint pipelines, built explicitly. Each line names its
        // descriptor, the shared shape parser its ParseShape selects, its typed row
        // factory (the only mapping code) and its per-endpoint sink.
        Add(services, CwgDescriptors.CityForecast, CwgShapeParsers.A, CityForecastRow.From,
            sp => new CityForecastSqlSink(Opt(sp), Log<CityForecastSqlSink>(sp)));
        Add(services, CwgDescriptors.CityGasForecast, CwgShapeParsers.A, CityGasForecastRow.From,
            sp => new CityGasForecastSqlSink(Opt(sp), Log<CityGasForecastSqlSink>(sp)));
        Add(services, CwgDescriptors.CityObservation, CwgShapeParsers.A, CityObservationRow.From,
            sp => new CityObservationSqlSink(Opt(sp), Log<CityObservationSqlSink>(sp)));
        Add(services, CwgDescriptors.DailyNormal, CwgShapeParsers.B, DailyNormalRow.From,
            sp => new DailyNormalSqlSink(Opt(sp), Log<DailyNormalSqlSink>(sp)));
        Add(services, CwgDescriptors.SolarForecast, CwgShapeParsers.C, SolarForecastRow.From,
            sp => new SolarForecastSqlSink(Opt(sp), Log<SolarForecastSqlSink>(sp)));
        Add(services, CwgDescriptors.SolarForecastChange, CwgShapeParsers.C, SolarForecastChangeRow.From,
            sp => new SolarForecastChangeSqlSink(Opt(sp), Log<SolarForecastChangeSqlSink>(sp)));
        Add(services, CwgDescriptors.SolarHourly, CwgShapeParsers.B, SolarHourlyRow.From,
            sp => new SolarHourlySqlSink(Opt(sp), Log<SolarHourlySqlSink>(sp)),
            sentinelPredicate: cell => CwgParse.IsSentinel(cell.RawValue)); // "NULL"/blank ActualMw is expected
        Add(services, CwgDescriptors.NationalDegreeDays, CwgShapeParsers.A, NationalDegreeDaysRow.From,
            sp => new NationalDegreeDaysSqlSink(Opt(sp), Log<NationalDegreeDaysSqlSink>(sp)));
        Add(services, CwgDescriptors.WindForecast, CwgShapeParsers.C, WindForecastRow.From,
            sp => new WindForecastSqlSink(Opt(sp), Log<WindForecastSqlSink>(sp)));
        Add(services, CwgDescriptors.WindForecastSubRegion, CwgShapeParsers.D, WindForecastSubRegionRow.From,
            sp => new WindForecastSubRegionSqlSink(Opt(sp), Log<WindForecastSubRegionSqlSink>(sp)));
        Add(services, CwgDescriptors.WindHourly, CwgShapeParsers.B, WindHourlyRow.From,
            sp => new WindHourlySqlSink(Opt(sp), Log<WindHourlySqlSink>(sp)),
            sentinelPredicate: cell => CwgParse.IsSentinel(cell.RawValue)); // "NULL"/blank ActualMw is expected
        Add(services, CwgDescriptors.WindTotalCapacityClimatology, CwgShapeParsers.E, WindTotalCapacityClimatologyRow.From,
            sp => new WindTotalCapacityClimatologySqlSink(Opt(sp), Log<WindTotalCapacityClimatologySqlSink>(sp)));
        Add(services, CwgDescriptors.WindTotalCapacityMW, CwgShapeParsers.E, WindTotalCapacityMWRow.From,
            sp => new WindTotalCapacityMWSqlSink(Opt(sp), Log<WindTotalCapacityMWSqlSink>(sp)));
        Add(services, CwgDescriptors.WindTotalCapacityPct, CwgShapeParsers.E, WindTotalCapacityPctRow.From,
            sp => new WindTotalCapacityPctSqlSink(Opt(sp), Log<WindTotalCapacityPctSqlSink>(sp)));
        Add(services, CwgDescriptors.Station, CwgShapeParsers.A, StationRow.From,
            sp => new StationSqlSink(Opt(sp), Log<StationSqlSink>(sp)));
        Add(services, CwgDescriptors.Regions5DegreeDays, CwgShapeParsers.A, Regions5DegreeDaysRow.From,
            sp => new Regions5DegreeDaysSqlSink(Opt(sp), Log<Regions5DegreeDaysSqlSink>(sp)));
        Add(services, CwgDescriptors.Regions9DegreeDays, CwgShapeParsers.A, Regions9DegreeDaysRow.From,
            sp => new Regions9DegreeDaysSqlSink(Opt(sp), Log<Regions9DegreeDaysSqlSink>(sp)));
        Add(services, CwgDescriptors.ISODegreeDays, CwgShapeParsers.A, ISODegreeDaysRow.From,
            sp => new ISODegreeDaysSqlSink(Opt(sp), Log<ISODegreeDaysSqlSink>(sp)));
    }

    private static IOptions<CwgSettings> Opt(IServiceProvider sp) => sp.GetRequiredService<IOptions<CwgSettings>>();
    private static ILogger<T> Log<T>(IServiceProvider sp) => sp.GetRequiredService<ILogger<T>>();

    /// <summary>Registers one closed endpoint pipeline (design §1.3).</summary>
    private static void Add<TRecord, TRow>(
        IServiceCollection services,
        CwgEndpointDescriptor descriptor,
        ICwgShapeParser<TRecord> shapeParser,
        Func<TRecord, CwgWorkUnit, TRow?> rowFactory,
        Func<IServiceProvider, ISink<TRow>> sinkFactory,
        Func<TRecord, bool>? sentinelPredicate = null)
        where TRow : class, ICwgFactRow
    {
        services.AddSingleton<ICwgEndpointPipeline>(sp =>
            BuildPipeline(sp, descriptor, shapeParser, rowFactory, sinkFactory, sentinelPredicate));
    }

    /// <summary>
    /// Constructs a descriptor-bound provider + reader + per-endpoint sink and wraps
    /// them in a <see cref="CwgEndpointPipeline{TRow}"/>. The generic provider/reader
    /// are never resolved from the container, so all 18 endpoints coexist without a
    /// shared-generic-service collision (design §1.1).
    /// </summary>
    private static ICwgEndpointPipeline BuildPipeline<TRecord, TRow>(
        IServiceProvider sp,
        CwgEndpointDescriptor descriptor,
        ICwgShapeParser<TRecord> shapeParser,
        Func<TRecord, CwgWorkUnit, TRow?> rowFactory,
        Func<IServiceProvider, ISink<TRow>> sinkFactory,
        Func<TRecord, bool>? sentinelPredicate)
        where TRow : class, ICwgFactRow
    {
        var settings = sp.GetRequiredService<IOptions<CwgSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<ICwgFileLog>();

        var provider = new CwgWorkUnitProvider(descriptor, settings,
            loggerFactory.CreateLogger($"CWG.{descriptor.EndpointId}.Provider"));
        var source = new CwgSourceReader<TRecord, TRow>(http, settings, fileLog, descriptor, shapeParser, rowFactory,
            loggerFactory.CreateLogger($"CWG.{descriptor.EndpointId}.Source"), sentinelPredicate);
        var sink = sinkFactory(sp);

        return new CwgEndpointPipeline<TRow>(
            descriptor.EndpointId, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            loggerFactory.CreateLogger($"CWG.{descriptor.EndpointId}.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<CwgSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CWG.Module");

        // Fail fast on a missing/placeholder API key before running any endpoint. The
        // key value itself is never logged.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey == "SEE_DB")
        {
            logger.LogError(
                "CWG API key is not configured — set core.Param(LoaderName='CWG', ParamName='ApiKey') or the environment variable DATALOADER_Loaders__CWG__ApiKey before running");
            return LoaderRunResult.Failed(Id, "CWG API key is not configured; set core.Param or DATALOADER_Loaders__CWG__ApiKey.", TimeSpan.Zero);
        }

        var enabled = new HashSet<string>(settings.EnabledEndpoints, StringComparer.OrdinalIgnoreCase);
        var allPipelines = services.GetServices<ICwgEndpointPipeline>().ToList();
        var pipelines = allPipelines.Where(p => enabled.Contains(p.EndpointId)).ToList();

        var known = allPipelines.Select(p => p.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in settings.EnabledEndpoints.Where(e => !known.Contains(e)))
            logger.LogWarning("Enabled CWG endpoint '{Endpoint}' has no matching pipeline and will be skipped", endpoint);

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No CWG endpoints enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        // Run the enabled endpoints sequentially (they share the one rate-limited
        // client; the global throttle bounds RPS regardless of per-endpoint fan-out).
        var results = new List<LoaderRunResult>();
        foreach (var pipeline in pipelines)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        var failureMessages = results
            .Where(r => !r.Success && r.ErrorMessage != null)
            .Select(r => r.ErrorMessage)
            .ToList();

        return new LoaderRunResult
        {
            LoaderId = Id,
            Success = results.All(r => r.Success),
            WorkUnitsTotal = results.Sum(r => r.WorkUnitsTotal),
            WorkUnitsSucceeded = results.Sum(r => r.WorkUnitsSucceeded),
            WorkUnitsSkipped = results.Sum(r => r.WorkUnitsSkipped),
            WorkUnitsFailed = results.Sum(r => r.WorkUnitsFailed),
            RecordsProcessed = results.Sum(r => r.RecordsProcessed),
            ErrorMessage = failureMessages.Count > 0 ? string.Join("; ", failureMessages) : null,
            Duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration)
        };
    }
}
