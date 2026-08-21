using System.Text.Json;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IIR;

/// <summary>
/// Plugin entry point for the IIR (Industrial Info Resources — IDB API v2.7) loader. One module, three
/// closed per-endpoint pipelines (Plant → Unit → OfflineEvent) built explicitly (CWG pattern) and
/// toggled by <c>EnabledEndpoints[]</c>, executed <b>sequentially with no tier/order dependency</b>
/// (design §1) — each payload is self-contained, so no reference provider reads another pull's table.
/// The generic provider/reader are never resolved from DI (each is <c>new</c>'d in a factory closure),
/// avoiding the shared-generic-service collision (§1.1). All three share one rate-limited, JWT-Bearer
/// <see cref="HttpClient"/>; a module-level post-load validation runs after all three complete.
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of <c>Platform:EnabledLoaders</c>
/// (disabled by default, CWG/AGSI/IHS posture).</para>
/// </summary>
public sealed class IirModule : ILoaderModule
{
    public const string Id = "IIR";
    public const string HttpClientName = "IIR";
    public const string TokenClientName = "IIR.Token";

    public string LoaderId => Id;
    public string DisplayName => "Industrial Info Resources (HTTP/JSON — plant, unit & offline-event catalogues)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLoaderSettings<IirSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler.
        services.AddSingleton<IirRateLimiter>();
        services.AddTransient<IirRateLimitingHandler>();

        // JWT token machinery: the provider is a SINGLETON (shared JWT cache + thread-safe re-mint
        // across all three pipelines' internal paging); the auth handler is transient (re-stamps the
        // Bearer header on every retry attempt).
        services.AddSingleton<IIirTokenProvider, IirTokenProvider>();
        services.AddTransient<IirTokenAuthHandler>();

        // Dedicated UN-AUTHED token client. RemoveAllLoggers is load-bearing: the mint URL carries the
        // credentials in its query string (§3.1), so default IHttpClientFactory request logging would
        // leak them. This client has NO auth handler (it mints the token; it cannot depend on it). A
        // SMALL bounded retry (reusing the IirHttpPolicy shape) absorbs a transient 5xx/429/network blip
        // on /token — honouring Retry-After on 429 — while a persistent 401/403 (a real credential
        // failure) is NOT retried and surfaces loudly. The policy logs only status/delay, never the
        // URI/creds/token.
        services.AddHttpClient(TokenClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<IirSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<IirSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("IIR.Token.Http");
            return IirHttpPolicy.Build(Math.Clamp(s.RetryCount, 0, 3), s.RetryDelayMs, log);
        });

        // Shared, rate-limited, JWT-Bearer DATA client. Handler order (design §1.3):
        //   retry (OUTER, drives the loop; 5xx/408/network/429, NOT 401/403/404) →
        //   auth (re-stamps the Bearer each attempt, re-mints once on 401) →
        //   throttle (INNER, paces every attempt).
        // RemoveAllLoggers suppresses IHttpClientFactory default logging — the Bearer credential is a
        // header (the data URL carries no secret); the reader logs only the sanitized relative path.
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<IirSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<IirSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("IIR.Http");
            return IirHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<IirTokenAuthHandler>()
        .AddHttpMessageHandler<IirRateLimitingHandler>();

        services.AddSingleton<IIirFileLog, SqlIirFileLog>();
        services.AddSingleton<IirLoadValidator>();

        // The three closed per-endpoint pipelines, built explicitly. Each line names its descriptor,
        // its typed fact row factory (the only mapping code), its per-endpoint FACT sink and its
        // per-endpoint id-catalog CENSUS sink (the STEP-1 side-write, design §4.4). The census sink is
        // the one parameterized IirSummarySqlSink bound to this endpoint's summary proc + TVP.
        Add(services, IirDescriptors.Plant, IirPlantRow.From,
            sp => new IirPlantSqlSink(Opt(sp), Log<IirPlantSqlSink>(sp)),
            sp => new IirSummarySqlSink("arm.usp_UpsertPlantSummary", "arm.PlantSummaryTvp", Opt(sp), CensusLog(sp, "Plant")));
        Add(services, IirDescriptors.Unit, IirUnitRow.From,
            sp => new IirUnitSqlSink(Opt(sp), Log<IirUnitSqlSink>(sp)),
            sp => new IirSummarySqlSink("arm.usp_UpsertUnitSummary", "arm.UnitSummaryTvp", Opt(sp), CensusLog(sp, "Unit")));
        Add(services, IirDescriptors.OfflineEvent, IirOfflineEventRow.From,
            sp => new IirOfflineEventSqlSink(Opt(sp), Log<IirOfflineEventSqlSink>(sp)),
            sp => new IirSummarySqlSink("arm.usp_UpsertOfflineEventSummary", "arm.OfflineEventSummaryTvp", Opt(sp), CensusLog(sp, "OfflineEvent")));
    }

    private static IOptions<IirSettings> Opt(IServiceProvider sp) => sp.GetRequiredService<IOptions<IirSettings>>();
    private static ILogger<T> Log<T>(IServiceProvider sp) => sp.GetRequiredService<ILogger<T>>();

    /// <summary>Per-endpoint logger category for a census sink (e.g. "IIR.Plant.Census") so a census SQL error names its endpoint.</summary>
    private static ILogger CensusLog(IServiceProvider sp, string endpointId) =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger($"IIR.{endpointId}.Census");

    /// <summary>Registers one closed endpoint pipeline (design §1.3).</summary>
    private static void Add<TRow>(
        IServiceCollection services,
        IirEndpointDescriptor descriptor,
        Func<JsonElement, IirWorkUnit, TRow?> rowFactory,
        Func<IServiceProvider, ISink<TRow>> sinkFactory,
        Func<IServiceProvider, ISink<IirSummaryRow>> summarySinkFactory)
        where TRow : class, IIirFactRow
    {
        services.AddSingleton<IIirPipeline>(sp => BuildPipeline(sp, descriptor, rowFactory, sinkFactory, summarySinkFactory));
    }

    /// <summary>
    /// Constructs a descriptor-bound work-unit provider, the shared tolerant two-step pager (injected
    /// with the per-endpoint id-catalog census sink for the STEP-1 side-write) and the per-endpoint fact
    /// sink, and wraps them in an <see cref="IirPipeline{TRow}"/>. The generic provider/reader are never
    /// resolved from the container, so all three endpoints coexist without a shared-generic-service
    /// collision (design §1.1).
    /// </summary>
    private static IIirPipeline BuildPipeline<TRow>(
        IServiceProvider sp,
        IirEndpointDescriptor descriptor,
        Func<JsonElement, IirWorkUnit, TRow?> rowFactory,
        Func<IServiceProvider, ISink<TRow>> sinkFactory,
        Func<IServiceProvider, ISink<IirSummaryRow>> summarySinkFactory)
        where TRow : class, IIirFactRow
    {
        var settings = sp.GetRequiredService<IOptions<IirSettings>>().Value;
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IIirFileLog>();

        var provider = new IirWorkUnitProvider(descriptor, settings, lf.CreateLogger($"IIR.{descriptor.EndpointId}.Provider"));
        var summarySink = summarySinkFactory(sp);
        var source = new IirSourceReader<TRow>(http, settings, fileLog, descriptor, rowFactory, summarySink, lf.CreateLogger($"IIR.{descriptor.EndpointId}.Source"));
        var sink = sinkFactory(sp);

        return new IirPipeline<TRow>(
            descriptor.EndpointId, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            lf.CreateLogger($"IIR.{descriptor.EndpointId}.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<IirSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IIR.Module");

        // Surface a misconfigured host time-zone database: enumeration silently falls back to UTC when
        // neither Central id resolves, which would shift the OfflineEvent daily-snapshot boundary (§5.1).
        if (IirTime.UsingUtcFallback)
            logger.LogWarning(
                "IIR could not resolve the US-Central time zone (neither 'Central Standard Time' nor 'America/Chicago'); falling back to UTC for the run-date stamp — verify the host time-zone database");

        // Fail fast on missing/placeholder credentials before running any endpoint. Every run must mint
        // a token, so BOTH are required. The values are never logged. (Secondary guard: materializing
        // IOptions above already ran the SEE_DB resolver, which throws if the core.Param rows are
        // missing — this catches a value left as the literal SEE_DB.)
        if (IsMissing(settings.Username) || IsMissing(settings.Password))
        {
            logger.LogError(
                "IIR credentials are not configured — set core.Param(LoaderName='IIR', ParamName='Username'|'Password') or the environment variables DATALOADER_Loaders__IIR__Username / __Password before running");
            return LoaderRunResult.Failed(Id, "IIR Username/Password not configured; set core.Param or DATALOADER_Loaders__IIR__Username/__Password.", TimeSpan.Zero);
        }

        var enabled = new HashSet<string>(settings.EnabledEndpoints, StringComparer.OrdinalIgnoreCase);
        var allPipelines = services.GetServices<IIirPipeline>().ToList();
        var pipelines = allPipelines.Where(p => enabled.Contains(p.EndpointId)).ToList();

        var known = allPipelines.Select(p => p.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in settings.EnabledEndpoints.Where(e => !known.Contains(e)))
            logger.LogWarning("Enabled IIR endpoint '{Endpoint}' has no matching pipeline and will be skipped", endpoint);

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No IIR endpoints enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        // Run the enabled endpoints sequentially in registration order (Plant → Unit → OfflineEvent).
        // The order is cosmetic — the three pulls are independent. The shared token provider mints the
        // JWT lazily on the first call and every subsequent call reuses it; the one rate-limited client
        // bounds global RPS.
        var results = new List<LoaderRunResult>();
        foreach (var pipeline in pipelines)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        // Module-level post-load validation, after all three complete (design §9).
        await services.GetRequiredService<IirLoadValidator>()
            .ValidateAsync(context, context.CancellationToken).ConfigureAwait(false);

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

    private static bool IsMissing(string? value) => string.IsNullOrWhiteSpace(value) || value == "SEE_DB";
}
