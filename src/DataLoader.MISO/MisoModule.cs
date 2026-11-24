using System.Net;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.MISO;

/// <summary>
/// Plugin entry point for the MISO Data Exchange (Load, Generation &amp; Interchange API) loader
/// (docs/design/MISO.md). One module, four closed pipelines into database <c>Misc</c>, schema
/// <c>miso</c>, run sequentially in <see cref="MisoFeeds.ExecutionOrder"/>.
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c>, with no watchdog entry (design §0).</para>
/// </summary>
public sealed class MisoModule : ILoaderModule
{
    public const string Id = "MISO";
    public const string HttpClientName = "MISO";

    public string LoaderId => Id;
    public string DisplayName => "MISO Data Exchange (HTTP/JSON — actual load, fuel mix, MTLF load forecast)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // 1. Settings; ApiKey = SEE_DB resolves from core.Param('MISO','ApiKey').
        services.AddLoaderSettings<MisoSettings>(configuration, Id);

        // 2. Shared sliding-window throttle + handlers.
        services.AddSingleton<MisoRateLimiter>();
        services.AddTransient<MisoRateLimitingHandler>();
        services.AddTransient<MisoBufferingHandler>();

        // 3. Data client. Handler order, outer → inner:
        //      retry → limiter → per-attempt timeout → body buffering → primary (no redirects)
        //    NO default key header: MisoApiClient attaches it per request.
        services.AddHttpClient(HttpClientName, client =>
        {
            // ⚠ LOAD-BEARING: the request budget lives in the per-attempt policy, not here (a policy
            // added via AddPolicyHandler runs INSIDE HttpClient.Timeout — the EIA/IHSPointLogic defect).
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        // 🔒 Redirects are never followed: HttpClient would forward the key header to the target.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        // 🔒 The factory's default handlers log headers at Trace; the key is a header.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<MisoSettings>>().Value;
            return MisoHttpPolicy.BuildRetry(s.RetryCount, s.RetryDelayMs, s.HttpTimeoutSeconds,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("MISO.Http"));
        })
        .AddHttpMessageHandler<MisoRateLimitingHandler>()
        .AddPolicyHandler((sp, _) =>
            MisoHttpPolicy.BuildPerAttemptTimeout(sp.GetRequiredService<IOptions<MisoSettings>>().Value.HttpTimeoutSeconds))
        .AddHttpMessageHandler<MisoBufferingHandler>();

        // 4. Run guard (one process = one run).
        services.AddSingleton<MisoRunGuard>();

        // 5. The four closed pipelines. Registration order is irrelevant: RunAsync orders them.
        services.AddSingleton<IMisoPipeline>(sp => BuildPipeline(sp, MisoFeeds.ZonalLoadHourly, new MisoZonalLoadMapper(),
            new MisoZonalLoadHourlySqlSink(Settings(sp), sp.GetRequiredService<ILogger<MisoZonalLoadHourlySqlSink>>())));
        services.AddSingleton<IMisoPipeline>(sp => BuildPipeline(sp, MisoFeeds.LoadHourly, new MisoLoadMapper(),
            new MisoLoadHourlySqlSink(Settings(sp), sp.GetRequiredService<ILogger<MisoLoadHourlySqlSink>>())));
        services.AddSingleton<IMisoPipeline>(sp => BuildPipeline(sp, MisoFeeds.FuelMixHourly, new MisoFuelMixMapper(),
            new MisoFuelMixHourlySqlSink(Settings(sp), sp.GetRequiredService<ILogger<MisoFuelMixHourlySqlSink>>())));
        services.AddSingleton<IMisoPipeline>(sp => BuildPipeline(sp, MisoFeeds.LoadForecastMtlf, new MisoLoadForecastMtlfMapper(),
            new MisoLoadForecastMtlfSqlSink(Settings(sp), sp.GetRequiredService<ILogger<MisoLoadForecastMtlfSqlSink>>())));
    }

    private static MisoSettings Settings(IServiceProvider sp) => sp.GetRequiredService<IOptions<MisoSettings>>().Value;

    /// <summary>Builds one feed's pipeline over the shared client and guard. Internal so tests can swap the sink only.</summary>
    internal static IMisoPipeline BuildPipeline<TRow>(IServiceProvider sp, MisoFeed feed, IMisoRowMapper<TRow> mapper, ISink<TRow> sink)
    {
        var settings = Settings(sp);
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var guard = sp.GetRequiredService<MisoRunGuard>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var client = new MisoApiClient(http, settings, guard, lf.CreateLogger($"MISO.{feed.Id}.Http"));
        var reader = new MisoDayReader<TRow>(client, mapper, settings, lf.CreateLogger($"MISO.{feed.Id}.Source"));
        var provider = new MisoWorkUnitProvider(feed, settings, lf.CreateLogger($"MISO.{feed.Id}.Provider"));
        return new MisoPipeline<TRow>(feed.Id, provider, reader, sink, sp.GetRequiredService<ILoadLogRepository>(),
            settings, lf.CreateLogger($"MISO.{feed.Id}.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MISO.Module");

        // Materialising the options runs the SEE_DB resolver, which THROWS when core.Param has no
        // MISO/ApiKey row. Fail the run with that (key-free) message rather than an unhandled throw.
        MisoSettings settings;
        try
        {
            settings = services.GetRequiredService<IOptions<MisoSettings>>().Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = "MISO settings could not be resolved: " + ErrorTextRedactor.Clean(ex.Message, 300);
            logger.LogError("{Message}", message);
            return LoaderRunResult.Failed(Id, message, TimeSpan.Zero);
        }

        // Startup validation (design §8), before any request.
        var invalid = settings.Validate();
        if (invalid.Count > 0)
        {
            foreach (var m in invalid) logger.LogError("MISO configuration invalid: {Message}", m);
            return LoaderRunResult.Failed(Id, "MISO configuration invalid: " + string.Join(" ", invalid), TimeSpan.Zero);
        }

        var enabled = new HashSet<string>(settings.EffectiveEnabledFeeds, StringComparer.OrdinalIgnoreCase);
        var byId = services.GetServices<IMisoPipeline>().ToDictionary(p => p.FeedId, StringComparer.OrdinalIgnoreCase);
        var ordered = MisoFeeds.ExecutionOrder
            .Where(id => enabled.Contains(id) && byId.ContainsKey(id))
            .Select(id => byId[id])
            .ToList();

        if (ordered.Count == 0)
        {
            logger.LogWarning("No MISO feeds enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        // Every pipeline runs under a token linked to the run guard. Once a 401/403 trips it, units
        // still waiting for a slot are never begun (no core.LoadLog row per remaining unit), later
        // pipelines are not started, and the run ends with ONE aggregate failure. Only the unit that
        // tripped (and any already in flight) records a row; the next run re-attempts everything.
        var guard = services.GetRequiredService<MisoRunGuard>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, guard.Token);
        var runContext = new LoaderRunContext
        {
            RunId = context.RunId,
            StartedAtUtc = context.StartedAtUtc,
            DateFrom = context.DateFrom,
            DateTo = context.DateTo,
            RunDeadlineToken = context.RunDeadlineToken,
            CancellationToken = linked.Token,
        };

        var results = new List<LoaderRunResult>();
        var notStarted = new List<string>();
        foreach (var pipeline in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (guard.IsTripped)
            {
                notStarted.Add(pipeline.FeedId);
                continue;
            }

            try
            {
                results.Add(await pipeline.ExecuteAsync(runContext).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (guard.IsTripped && !context.CancellationToken.IsCancellationRequested)
            {
                // The guard stopped this pipeline mid-run; its unit counts are lost with it, which is
                // acceptable because the whole run is reported failed and is re-attempted next time.
            }
        }

        var duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
        if (guard.Reason is { } reason)
        {
            var message = $"MISO run guard tripped: {reason}" +
                          (notStarted.Count > 0 ? $"; feeds not started: {string.Join(", ", notStarted)}" : string.Empty);
            logger.LogError("MISO run stopped by the run guard: {Message}", message);
            results.Add(LoaderRunResult.Failed(Id, message, TimeSpan.Zero));
            return LoaderRunResult.Aggregate(Id, results, duration, message);
        }

        return LoaderRunResult.Aggregate(Id, results, duration);
    }
}
