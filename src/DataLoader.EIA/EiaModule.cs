using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// Plugin entry point for the U.S. Energy Information Administration (EIA) API v2
/// loader.
///
/// <para>
/// <b>What makes this loader unusual in this repo:</b> its scope is not in
/// configuration, it is in the DATABASE. <c>arm.Series</c>, <c>arm.SeriesFrequency</c>
/// and <c>arm.SeriesDataType</c> say which routes, which periodicities and which data
/// columns to request, read through stored procedures so no query is compiled into the
/// binary. Everything lands in ONE generic table, <c>arm.SeriesData</c>, as canonical
/// JSON plus two hashes — which is what lets 95 structurally different EIA datasets
/// share a schema.
/// </para>
/// <para>
/// Design of record: <c>docs/design/EIA.md</c>. Field reference:
/// <c>docs/apis/EIA.md</c>.
/// </para>
/// </summary>
public sealed class EiaModule : ILoaderModule
{
    public const string Id = "EIA";

    public string LoaderId => Id;
    public string DisplayName => "U.S. Energy Information Administration (API v2)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // -- settings binding (resolves the "SEE_DB" sentinel from core.Param) --
        services.AddLoaderSettings<EiaSettings>(configuration, Id);

        // 🔒 ONE limiter for the whole loader. api.data.gov rate-limits per API KEY, and
        // the documented penalty for exceeding it is temporary SUSPENSION OF THE KEY --
        // which would stop every other consumer of that key, not just this run. A
        // per-unit limiter would multiply the real rate by MaxConcurrentWorkUnits and
        // defeat the point, so this is a singleton.
        services.AddSingleton(sp =>
            new EiaRateLimiter(sp.GetRequiredService<IOptions<EiaSettings>>().Value.RequestsPerSecond));

        services.AddHttpClient<EiaApiClient>((sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<EiaSettings>>().Value;
            if (!string.IsNullOrWhiteSpace(s.BaseUrl))
                client.BaseAddress = new Uri(s.BaseUrl.EndsWith('/') ? s.BaseUrl : s.BaseUrl + "/");

            // ⚠ LOAD-BEARING: the request budget lives in the POLICY, not here.
            // HttpClient.Timeout bounds the whole operation including every retry
            // attempt and backoff wait, so leaving it at HttpTimeoutSeconds would give
            // all five attempts that long BETWEEN them — and against an endpoint that
            // 504s at ~60s the second attempt blows it, reporting a client timeout and
            // hiding the vendor's 504. See EiaHttp.BuildResiliencePipeline.
            //
            // Nothing can hang forever: the per-attempt timeout policy cancels each
            // attempt, and the work-unit CTS (WorkUnitTimeoutSeconds) bounds the unit.
            client.Timeout = Timeout.InfiniteTimeSpan;

            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        // 🔒 LOAD-BEARING. EIA authenticates with a QUERY PARAMETER -- it offers no
        // header form -- so the factory's own request-logging handlers would write the
        // api_key into the log on every call, at Information level, which is on by
        // default. Nothing in this loader logs a URI; removing these handlers is what
        // makes that true of the whole pipeline rather than just of our own code. Same
        // posture as OilX and EnergyAspects, the repo's other query-string-authenticated
        // loaders.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<EiaSettings>>().Value;
            var log = sp.GetRequiredService<ILogger<EiaApiClient>>();
            return EiaHttp.BuildResiliencePipeline(
                s.RetryCount, s.RetryDelayMs, s.HttpTimeoutSeconds, log);
        });

        // 🔒 Reads core.LoadLog in the PLATFORM database, not EIAv2, so it takes its own
        // connection string. Read-only, and used for exactly one decision: whether a hot
        // window may have its read narrowed (see EiaSettings.NarrowHotWindowReads).
        services.AddSingleton(sp => new EiaLoadHistory(
            configuration["Platform:LoadLogConnectionString"],
            sp.GetRequiredService<ILogger<EiaLoadHistory>>()));

        services.AddSingleton<EiaCatalogRepository>();
        services.AddSingleton<EiaSourceReader>();
        services.AddSingleton<EiaSink>();
        services.AddSingleton<EiaWorkUnitProvider>();
        services.AddSingleton<EiaLoadValidator>();
        services.AddSingleton<EiaPipeline>();
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        WarnOnExpensiveConfiguration(services);

        var pipeline = services.GetRequiredService<EiaPipeline>();
        var result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);

        await services.GetRequiredService<EiaLoadValidator>()
                      .ValidateAsync(context.CancellationToken)
                      .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Two configurations turn a cheap incremental run into a full re-pull of roughly
    /// 90 million rows and tens of thousands of requests. Both are legitimate on
    /// purpose and ruinous by accident, so each says so at the top of the run rather
    /// than being discovered from the request count hours later.
    /// </summary>
    private static void WarnOnExpensiveConfiguration(IServiceProvider services)
    {
        var s = services.GetRequiredService<IOptions<EiaSettings>>().Value;
        var logger = services.GetRequiredService<ILogger<EiaModule>>();

        if (s.ReloadAll)
            logger.LogWarning(
                "EIA: Loaders:EIA:ReloadAll is TRUE — every window is hot this run, so the whole " +
                "history of every enabled series is re-requested (~90M rows, tens of thousands of " +
                "requests at {Rps}/s). Correct and idempotent, but not something to leave on",
                s.RequestsPerSecond);

        if (!s.DefaultAllowIncremental)
            logger.LogWarning(
                "EIA: Loaders:EIA:DefaultAllowIncremental is FALSE and arm.Series.AllowIncremental is " +
                "NULL on every row of the live catalog, so EVERY series is in full-reload mode. Set it " +
                "back to true unless a full re-verify is intended");

        if (s.SettledAfterDays <= 0)
            logger.LogWarning(
                "EIA: Loaders:EIA:SettledAfterDays is {Days}, so no window ever settles and the " +
                "backfill repeats on every run", s.SettledAfterDays);
    }
}
