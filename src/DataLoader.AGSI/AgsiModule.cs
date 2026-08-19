using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.AGSI;

/// <summary>
/// Plugin entry point for the GIE AGSI loader. One module, two closed pipelines
/// (About + Storage) built explicitly (design §1) and run <b>sequentially with
/// About first</b> — the ordering is load-bearing because the storage pipeline's
/// work units are the distinct country codes the entities pipeline writes to
/// <c>arm.GasStorageEntity</c>. The generic providers/readers are never resolved
/// from DI (each is new'ed in a factory closure), avoiding the shared-generic
/// collision (§1.1). Both share one rate-limited <see cref="HttpClient"/>; a
/// module-level post-load validation runs after both complete (§8).
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c> (disabled by default, CWG posture).</para>
/// </summary>
public sealed class AgsiModule : ILoaderModule
{
    public const string Id = "AGSI";
    public const string HttpClientName = "AGSI";

    private const string AboutEndpoint = "About";
    private const string StorageEndpoint = "Storage";

    public string LoaderId => Id;
    public string DisplayName => "GIE AGSI (HTTP/JSON — country map + daily gas-storage inventory)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLoaderSettings<AgsiSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler.
        services.AddSingleton<AgsiRateLimiter>();
        services.AddTransient<AgsiRateLimitingHandler>();

        // Shared, rate-limited HttpClient. Handler order matters: the retry policy is OUTER
        // (it drives the retry loop) and the throttle is INNER, so the limiter paces every
        // attempt — the first and each retry (design §1.3).
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<AgsiSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        // Suppress IHttpClientFactory's default logging handlers (they log the full request URI)
        // for parity with CWG/StormVista and to keep the log surface identical. Endpoint-2 auth
        // is a HEADER, so the URL carries no secret regardless; the readers log sanitized paths.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<AgsiSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AGSI.Http");
            return AgsiHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<AgsiRateLimitingHandler>();

        services.AddSingleton<IAgsiFileLog, SqlAgsiFileLog>();
        services.AddSingleton<IAgsiCountryProvider, SqlAgsiCountryProvider>();
        services.AddSingleton<AgsiLoadValidator>();

        // The two closed pipelines, built explicitly.
        services.AddSingleton<IAgsiPipeline>(BuildEntitiesPipeline);
        services.AddSingleton<IAgsiPipeline>(BuildStoragePipeline);
    }

    private static IAgsiPipeline BuildEntitiesPipeline(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<AgsiSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IAgsiFileLog>();

        var provider = new AgsiEntitiesWorkUnitProvider(settings, loggerFactory.CreateLogger("AGSI.About.Provider"));
        var source = new AgsiEntitiesSourceReader(http, settings, fileLog, loggerFactory.CreateLogger("AGSI.About.Source"));
        var sink = new GasStorageEntitySqlSink(sp.GetRequiredService<IOptions<AgsiSettings>>(),
            loggerFactory.CreateLogger<GasStorageEntitySqlSink>());

        return new AgsiPipeline<AgsiEntitiesWorkUnit, GasStorageEntityRow>(
            AboutEndpoint, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings, loggerFactory.CreateLogger("AGSI.About.Pipeline"));
    }

    private static IAgsiPipeline BuildStoragePipeline(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<AgsiSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IAgsiFileLog>();
        var countryProvider = sp.GetRequiredService<IAgsiCountryProvider>();

        var provider = new AgsiStorageWorkUnitProvider(settings, countryProvider, loggerFactory.CreateLogger("AGSI.Storage.Provider"));
        var source = new AgsiStorageSourceReader(http, settings, fileLog, loggerFactory.CreateLogger("AGSI.Storage.Source"));
        var sink = new GasStorageSqlSink(sp.GetRequiredService<IOptions<AgsiSettings>>(),
            loggerFactory.CreateLogger<GasStorageSqlSink>());

        return new AgsiPipeline<AgsiStorageWorkUnit, GasStorageRow>(
            StorageEndpoint, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings, loggerFactory.CreateLogger("AGSI.Storage.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<AgsiSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AGSI.Module");

        // Surface a misconfigured host time-zone database: enumeration silently falls back to UTC
        // when neither CET id resolves, which would shift the gas-day boundary (design §6).
        if (AgsiTime.UsingUtcFallback)
            logger.LogWarning(
                "AGSI could not resolve the Central European time zone (neither 'Europe/Brussels' nor 'Central European Standard Time'); falling back to UTC for gas-day enumeration — verify the host time-zone database");

        var enabled = new HashSet<string>(settings.EnabledEndpoints, StringComparer.OrdinalIgnoreCase);
        var allPipelines = services.GetServices<IAgsiPipeline>().ToList();
        var byId = allPipelines.ToDictionary(p => p.EndpointId, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in settings.EnabledEndpoints.Where(e => !byId.ContainsKey(e)))
            logger.LogWarning("Enabled AGSI endpoint '{Endpoint}' has no matching pipeline and will be skipped", endpoint);

        // Fail fast on a missing/placeholder key ONLY when Storage (the keyed endpoint) is enabled.
        // NOTE: this is a SECONDARY guard. Materializing IOptions<AgsiSettings> above already ran
        // the SEE_DB resolver, which THROWS if core.Param(LoaderName='AGSI', ParamName='ApiKey') is
        // missing — so ANY AGSI run (even About-only) requires that row to exist (a placeholder
        // value is fine for an About-only run; About sends no key). By the time we reach here the
        // key has a value; this check catches a value left as the literal SEE_DB placeholder for a
        // Storage run. The key value itself is never logged (design §1.4 / §9).
        if (enabled.Contains(StorageEndpoint) &&
            (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey == "SEE_DB"))
        {
            logger.LogError(
                "AGSI API key is not configured — set core.Param(LoaderName='AGSI', ParamName='ApiKey') or the environment variable DATALOADER_Loaders__AGSI__ApiKey before running the Storage endpoint");
            return LoaderRunResult.Failed(Id, "AGSI API key is not configured; set core.Param or DATALOADER_Loaders__AGSI__ApiKey.", TimeSpan.Zero);
        }

        // Fixed order: About first (writes the country list), then Storage (reads it) — §1.4.
        var ordered = new List<IAgsiPipeline>();
        if (enabled.Contains(AboutEndpoint) && byId.TryGetValue(AboutEndpoint, out var about)) ordered.Add(about);
        if (enabled.Contains(StorageEndpoint) && byId.TryGetValue(StorageEndpoint, out var storage)) ordered.Add(storage);

        if (ordered.Count == 0)
        {
            logger.LogWarning("No AGSI endpoints enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        var results = new List<LoaderRunResult>();
        foreach (var pipeline in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        // Module-level post-load validation, after both pipelines complete (design §8).
        await services.GetRequiredService<AgsiLoadValidator>()
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
}
