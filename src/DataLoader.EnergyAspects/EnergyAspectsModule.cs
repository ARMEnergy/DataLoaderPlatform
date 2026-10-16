using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Resilience;
using DataLoader.Core.Transforms;
using DataLoader.EnergyAspects.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EnergyAspects;

/// <summary>
/// Plugin entry point for the Energy Aspects loader.
///
/// <para>
/// Everything Energy-Aspects-specific is wired up here:
///   - the strongly-typed settings class
///   - the HttpClient with retry policy
///   - the API source reader
///   - the SQL sink
///   - the transformer
///   - the work-unit provider
///   - the pipeline binding (mapping × window → API items → batch → SQL)
/// </para>
///
/// The host calls <see cref="RegisterServices"/> at startup. None of the
/// types registered here are visible to other loaders.
/// </summary>
public sealed class EnergyAspectsModule : ILoaderModule
{
    public const string Id = "EnergyAspects";

    public string LoaderId => Id;
    public string DisplayName => "Energy Aspects (HTTP/JSON timeseries)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // -- settings binding (resolves any "SEE_DB" sentinel from core.Param) --
        services.AddLoaderSettings<EnergyAspectsSettings>(configuration, Id);

        // -- HTTP client with Polly retry policy --
        services.AddHttpClient<EnergyAspectsApiSource>((sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<EnergyAspectsSettings>>().Value;
            if (!string.IsNullOrWhiteSpace(s.BaseUrl))
                client.BaseAddress = new Uri(s.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        // 🔒 LOAD-BEARING. Energy Aspects authenticates with a QUERY PARAMETER, so the
        // factory's own request-logging handlers write the api_key into the log on every
        // call — at Information level, which is on by default. This loader's
        // SanitiseForLog strips the query string, but those handlers run underneath it
        // and log the raw URI regardless. Removing them means the only request lines in
        // the log are the sanitized ones written here. Same posture as OilX, the repo's
        // other query-string-authenticated loader.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<EnergyAspectsSettings>>().Value;
            var log = sp.GetRequiredService<ILogger<EnergyAspectsApiSource>>();
            return RetryPolicyFactory.BuildHttpRetryPolicy(
                s.RetryCount, s.RetryDelayMs,
                (outcome, delay, attempt) =>
                    log.LogWarning("HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
                        attempt, outcome.Result?.StatusCode, delay.TotalMilliseconds));
        });

        // -- loader-specific services --
        services.AddSingleton<EnergyAspectsSink>();
        services.AddSingleton<EnergyAspectsTransformer>();
        services.AddSingleton<EnergyAspectsWorkUnitProvider>();

        // The API source needs an API-response logger. We just reuse the
        // sink (it has the LogApiResponseAsync method). Keep the indirection
        // as an interface so the API source isn't physically tied to SQL.
        services.AddSingleton<IEnergyAspectsApiLogger>(sp =>
            new SinkBackedApiLogger(sp.GetRequiredService<EnergyAspectsSink>()));

        // -- pipeline wiring --
        // The pipeline base class is generic over (unit, item, row).
        // EA: WorkUnit = EnergyAspectsWorkUnit; Item = TimeseriesApiItem; Row = TimeseriesBatch
        services.AddSingleton<IWorkUnitProvider<EnergyAspectsWorkUnit>>(
            sp => sp.GetRequiredService<EnergyAspectsWorkUnitProvider>());

        services.AddSingleton<ISourceReader<EnergyAspectsWorkUnit, TimeseriesApiItem>>(
            sp => sp.GetRequiredService<EnergyAspectsApiSource>());

        services.AddSingleton<ITransformer<TimeseriesApiItem, TimeseriesBatch>>(
            sp => sp.GetRequiredService<EnergyAspectsTransformer>());

        services.AddSingleton<ISink<TimeseriesBatch>>(
            sp => sp.GetRequiredService<EnergyAspectsSink>());

        // The pipeline itself
        services.AddSingleton<EnergyAspectsPipeline>();
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        WarnOnSettleHorizon(services);
        var pipeline = services.GetRequiredService<EnergyAspectsPipeline>();
        return await pipeline.ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Energy Aspects restates across the whole window, so a short settle horizon is the
    /// misconfiguration here — the opposite of OilX, whose published files are immutable
    /// and where a LARGE horizon is the waste. Measured 2026-10-15: of the 33,939 rows one
    /// run changed, only 0.8% had a DataDate inside the last day, 61.6% were 8-30 days old
    /// and 4.8% were 31-90 days old.
    /// </summary>
    private static void WarnOnSettleHorizon(IServiceProvider services)
    {
        var s = services.GetRequiredService<IOptions<EnergyAspectsSettings>>().Value;
        if (s.SettledAfterDays >= s.DaysBackStart) return;

        var logger = services.GetRequiredService<ILogger<EnergyAspectsModule>>();
        logger.LogWarning(
            "EnergyAspects SettledAfterDays={Settled} is below DaysBackStart={DaysBack}, so windows " +
            "older than {Settled} day(s) are frozen at the first value seen. The vendor restates far " +
            "beyond that — on a measured run only 0.8% of changed rows were within a day, while 61.6% " +
            "were 8-30 days old — so those revisions will be missed. Set it to {DaysBack} to keep the " +
            "whole window hot",
            s.SettledAfterDays, s.DaysBackStart, s.SettledAfterDays, s.DaysBackStart);
    }

    /// <summary>
    /// Tiny adapter: the API source wants an <see cref="IEnergyAspectsApiLogger"/>;
    /// the sink already knows how to write API responses. This wires them
    /// together without making the API source depend on the sink directly.
    /// </summary>
    private sealed class SinkBackedApiLogger : IEnergyAspectsApiLogger
    {
        private readonly EnergyAspectsSink _sink;
        public SinkBackedApiLogger(EnergyAspectsSink sink) => _sink = sink;
        public Task LogApiResponseAsync(int mappingId, int responseCode, CancellationToken ct) =>
            _sink.LogApiResponseAsync(mappingId, responseCode, ct);
    }
}

/// <summary>
/// Concrete pipeline binding the generic <see cref="LoaderPipelineBase{TUnit,TItem,TRow}"/>
/// to the Energy Aspects types and logger category. Exists so DI can resolve
/// it by a closed type.
/// </summary>
public sealed class EnergyAspectsPipeline : LoaderPipelineBase<EnergyAspectsWorkUnit, TimeseriesApiItem, TimeseriesBatch>
{
    public EnergyAspectsPipeline(
        IWorkUnitProvider<EnergyAspectsWorkUnit> workUnits,
        ISourceReader<EnergyAspectsWorkUnit, TimeseriesApiItem> source,
        ITransformer<TimeseriesApiItem, TimeseriesBatch> transformer,
        ISink<TimeseriesBatch> sink,
        ILoadLogRepository loadLog,
        IOptions<EnergyAspectsSettings> settings,
        ILogger<EnergyAspectsPipeline> logger)
        : base(EnergyAspectsModule.Id, workUnits, source, transformer, sink, loadLog, settings.Value, logger)
    {
    }
}
