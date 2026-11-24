using System.Net;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.PJM;

/// <summary>
/// Plugin entry point for the PJM Data Miner 2 loader (design §1, §14). One module, eight closed
/// pipelines into database <c>Misc</c>, schema <c>dbo</c>, run SEQUENTIALLY in
/// <see cref="PjmFeeds.ExecutionOrder"/>: the two 30-day-retention feeds (<c>inst_load</c>,
/// <c>very_short_load_frcst</c>) first, then the <c>load_frcstd_7_day</c> snapshot (which keeps
/// only the latest evaluation), then the recoverable history feeds, <c>hrl_load_estimated</c>
/// last. If a run is cut short, the data that disappears soonest should be the data that got read.
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c>, with no watchdog entry (design §10.4).</para>
/// </summary>
public sealed class PjmModule : ILoaderModule
{
    public const string Id = "PJM";
    public const string HttpClientName = "PJM";
    public const string SettingsHttpClientName = "PJM.Settings";

    public string LoaderId => Id;
    public string DisplayName => "PJM Data Miner 2 (HTTP/JSON — load actuals, load forecasts and generation by fuel)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // 1. Settings. No property ships as SEE_DB, so the resolver never touches the store for PJM
        //    (design §5.2); the call still registers the shared ISeeDbParamStore the key provider uses.
        services.AddLoaderSettings<PjmSettings>(configuration, Id);

        // 2. Shared sliding-window throttle (singleton state) + its delegating handler.
        services.AddSingleton<PjmRateLimiter>();
        services.AddTransient<PjmRateLimitingHandler>();
        services.AddTransient<PjmBufferingHandler>();

        // 3. Data API client. Handler order, outer → inner (design §1.3):
        //      retry → limiter → per-attempt timeout → body buffering → primary (no redirects)
        //    so every attempt (retries included) is paced, a limiter wait never eats the request's
        //    own timeout, and a stalled/truncated BODY is timed out and retried like a stalled
        //    request. NO default key header: the key is attached per request because it can change
        //    mid-run (design §5.3).
        services.AddHttpClient(HttpClientName, client =>
        {
            // ⚠ LOAD-BEARING: the request budget lives in PjmHttpPolicy, not here. A policy added
            // via AddPolicyHandler runs INSIDE HttpClient.Timeout, so that timeout bounds the whole
            // retried operation (every attempt and backoff together), not one attempt — the defect
            // that bit EIA and IHSPointLogic. PjmHttpPolicy applies a per-attempt timeout instead;
            // the work-unit CTS still bounds the whole unit.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        // 🔒 Redirects are never followed: HttpClient forwards custom headers — the key — to the
        // redirect target. A 3xx is classified by PjmApiClient as a contract failure.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // PJM serves gzip (verified live); the first run would otherwise pull ~520 MB of very_short.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        // 🔒 The factory's default handlers log headers at Trace; the key is a header.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, request) =>
        {
            // The one post-401 re-send is a SINGLE attempt, never a retry cycle (design §5.3, §10.2).
            if (request.Options.TryGetValue(PjmHttpPolicy.SingleAttempt, out var single) && single)
                return Polly.Policy.NoOpAsync<HttpResponseMessage>();
            var s = sp.GetRequiredService<IOptions<PjmSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PJM.Http");
            return PjmHttpPolicy.BuildRetry(s.RetryCount, s.RetryDelayMs, s.HttpTimeoutSeconds, log, "PJM");
        })
        .AddHttpMessageHandler<PjmRateLimitingHandler>()
        .AddPolicyHandler((sp, _) =>
            PjmHttpPolicy.BuildPerAttemptTimeout(sp.GetRequiredService<IOptions<PjmSettings>>().Value.HttpTimeoutSeconds))
        .AddHttpMessageHandler<PjmBufferingHandler>();

        // 4. settings.json client: per-attempt timeout + transient retry, NO rate limiter (it targets
        //    dataminer2.pjm.com, not the API gateway), no redirects, body buffered inside the
        //    timeout. 🔒 Its body contains the key.
        services.AddHttpClient(SettingsHttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // PJM serves gzip (verified live); the first run would otherwise pull ~520 MB of very_short.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<PjmSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PJM.Settings.Http");
            return PjmHttpPolicy.Build(s.RetryCount, PjmSettings.SettingsRetryDelayMs(s.RetryDelayMs), s.SettingsFetchTimeoutSeconds, log, "PJM settings.json");
        })
        .AddHttpMessageHandler<PjmBufferingHandler>();

        // 5. Singletons: key provider and run guard (one process = one run).
        services.AddSingleton<IPjmKeyProvider>(sp => new PjmKeyProvider(
            sp.GetRequiredService<IOptions<PjmSettings>>().Value,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(SettingsHttpClientName),
            sp.GetRequiredService<ISeeDbParamStore>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("PJM.Key")));
        services.AddSingleton<PjmRunGuard>();

        // 6. The eight closed pipelines, one line each (design §14.2). Registration order is
        //    irrelevant: RunAsync orders them by PjmFeeds.ExecutionOrder.
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.InstLoad, new PjmLoadRowMapper(),
            (r, g) => new PjmLoadSourceReader(r, g),
            (s, lf) => new PjmLoadSqlSink(s, lf.CreateLogger<PjmLoadSqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.GenByFuel, new PjmFuelMixRowMapper(),
            (r, g) => new PjmFuelMixSourceReader(r, g),
            (s, lf) => new PjmFuelMixSqlSink(s, lf.CreateLogger<PjmFuelMixSqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.LoadForecastVeryShort, new PjmLoadForecastVeryShortRowMapper(),
            Generic<PjmLoadForecastVeryShortRow>, (s, lf) => new PjmLoadForecastVeryShortSqlSink(s, lf.CreateLogger<PjmLoadForecastVeryShortSqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.LoadForecast7Day, new PjmLoadForecast7DayRowMapper(),
            Generic<PjmLoadForecast7DayRow>, (s, lf) => new PjmLoadForecast7DaySqlSink(s, lf.CreateLogger<PjmLoadForecast7DaySqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.LoadMetered, new PjmLoadMeteredRowMapper(),
            Generic<PjmLoadMeteredRow>, (s, lf) => new PjmLoadMeteredSqlSink(s, lf.CreateLogger<PjmLoadMeteredSqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.LoadPrelim, new PjmLoadPrelimRowMapper(),
            Generic<PjmLoadPrelimRow>, (s, lf) => new PjmLoadPrelimSqlSink(s, lf.CreateLogger<PjmLoadPrelimSqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.LoadForecastHist, new PjmLoadForecastHistRowMapper(),
            Generic<PjmLoadForecastHistRow>, (s, lf) => new PjmLoadForecastHistSqlSink(s, lf.CreateLogger<PjmLoadForecastHistSqlSink>())));
        services.AddSingleton<IPjmPipeline>(sp => BuildPipeline(sp, PjmFeeds.LoadEstimated, new PjmLoadEstimatedRowMapper(),
            Generic<PjmLoadEstimatedRow>, (s, lf) => new PjmLoadEstimatedSqlSink(s, lf.CreateLogger<PjmLoadEstimatedSqlSink>())));
    }

    private static ISourceReader<PjmDayWorkUnit, TRow> Generic<TRow>(PjmDayReader<TRow> reader, PjmRunGuard guard)
        where TRow : IPjmRow => new PjmSourceReader<TRow>(reader, guard);

    private static IPjmPipeline BuildPipeline<TRow>(
        IServiceProvider sp,
        Func<PjmSettings, PjmFeed> feedFactory,
        IPjmRowMapper<TRow> mapper,
        Func<PjmDayReader<TRow>, PjmRunGuard, ISourceReader<PjmDayWorkUnit, TRow>> sourceFactory,
        Func<PjmSettings, ILoggerFactory, ISink<TRow>> sinkFactory)
        where TRow : IPjmRow
    {
        var settings = sp.GetRequiredService<IOptions<PjmSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var guard = sp.GetRequiredService<PjmRunGuard>();
        var feed = feedFactory(settings);

        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var client = new PjmApiClient(http, settings, sp.GetRequiredService<IPjmKeyProvider>(), guard,
            loggerFactory.CreateLogger($"PJM.{feed.Name}.Http"));
        var reader = new PjmDayReader<TRow>(client, mapper, settings, loggerFactory.CreateLogger($"PJM.{feed.Name}.Source"));
        var provider = new PjmWorkUnitProvider(feed, settings, loggerFactory.CreateLogger($"PJM.{feed.Name}.Provider"));

        // A snapshot's re-read of a stored evaluation merges 0 rows; say so (design §14.8).
        var sink = sinkFactory(settings, loggerFactory);
        if (feed.Kind == PjmFeedKind.Snapshot)
            sink = new PjmSnapshotSink<TRow>(sink, feed.Name, loggerFactory.CreateLogger($"PJM.{feed.Name}.Sink"));

        return new PjmPipeline<TRow>(
            feed.Name, provider, sourceFactory(reader, guard), sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings, loggerFactory.CreateLogger($"PJM.{feed.Name}.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<PjmSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PJM.Module");

        // Startup validation (design §10) — before any request.
        var invalid = settings.Validate();
        if (invalid.Count > 0)
        {
            foreach (var message in invalid) logger.LogError("PJM configuration invalid: {Message}", message);
            return LoaderRunResult.Failed(Id, "PJM configuration invalid: " + string.Join(" ", invalid), TimeSpan.Zero);
        }

        // 1. Feed toggle.
        var enabled = new HashSet<string>(settings.EffectiveEnabledFeeds, StringComparer.OrdinalIgnoreCase);
        var byId = services.GetServices<IPjmPipeline>().ToDictionary(p => p.FeedId, StringComparer.OrdinalIgnoreCase);

        foreach (var feed in enabled.Where(f => !byId.ContainsKey(f)))
            logger.LogWarning("Enabled PJM feed '{Feed}' has no matching pipeline and will be skipped", feed);

        // Fixed order regardless of list order (design §14.1): the 30-day-retention feeds first, estimated last.
        var ordered = PjmFeeds.ExecutionOrder
            .Where(id => enabled.Contains(id) && byId.ContainsKey(id))
            .Select(id => byId[id])
            .ToList();

        if (ordered.Count == 0)
        {
            logger.LogWarning("No PJM feeds enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        // The EPT echo check needs the Eastern zone; no UTC fallback (design §4.4).
        if (PjmTime.EasternOrNull is null)
        {
            const string tzMessage =
                "PJM could not resolve the US Eastern time zone (neither 'America/New_York' nor 'Eastern Standard Time'); refusing to run rather than falling back to UTC";
            logger.LogError(tzMessage);
            return LoaderRunResult.Failed(Id, tzMessage, TimeSpan.Zero);
        }

        // 2. Acquire the key ONCE, before any unit: with no key every unit would 401, and the run
        //    should fail once rather than 60 times. 🔒 Messages are key-free by construction.
        try
        {
            await services.GetRequiredService<IPjmKeyProvider>()
                .InitializeAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (PjmKeyAcquisitionException ex)
        {
            logger.LogError("PJM could not acquire a subscription key; no work unit was attempted: {Reason}", ex.Message);
            return LoaderRunResult.Failed(Id, ex.Message, TimeSpan.Zero);
        }

        // 3. Run the enabled pipelines in order. After a guard trip the later pipelines still run
        //    so core.LoadLog records each unit as failed (fast, no HTTP) and the next run re-attempts it.
        var guard = services.GetRequiredService<PjmRunGuard>();
        var results = new List<LoaderRunResult>();
        foreach (var pipeline in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        // 4. Aggregate; repeat the trip reason last so the final log line explains a red run.
        string? errorMessage = null;
        if (guard.Reason is { } reason)
        {
            logger.LogError("PJM run stopped by the run guard: {Reason}", reason);
            errorMessage = $"PJM run guard tripped: {reason}";
        }

        var duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
        return LoaderRunResult.Aggregate(Id, results, duration, errorMessage);
    }
}
