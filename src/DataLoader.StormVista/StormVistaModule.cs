using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.StormVista;

/// <summary>
/// Plugin entry point for the StormVista WDD loader. One module, two closed
/// windowed pipelines (Daily + Regional) toggled by <c>EnabledFeeds</c> — the
/// Platts two-pipelines shape. The pipelines are built explicitly (not via a
/// shared <c>IWorkUnitProvider&lt;T&gt;</c> registration) so the two feeds don't
/// collide in DI. Both share one rate-limited <see cref="HttpClient"/> and run
/// sequentially (Daily then Regional); after both, a module-level post-load
/// validation runs (design §1 / §10).
/// </summary>
public sealed class StormVistaModule : ILoaderModule
{
    public const string Id = "StormVista";
    public const string HttpClientName = "StormVista";

    public string LoaderId => Id;
    public string DisplayName => "StormVista Wx Models (HTTP/CSV WDD — Daily national + Regional/weekly)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLoaderSettings<StormVistaSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler.
        services.AddSingleton<StormVistaRateLimiter>();
        services.AddTransient<StormVistaRateLimitingHandler>();

        // Shared, rate-limited HttpClient. Handler order matters: the retry policy
        // is OUTER (it drives the retry loop) and the throttle is INNER, so the
        // limiter paces every attempt — the first and each retry (design §7).
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<StormVistaSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "text/csv");
        })
        // Suppress IHttpClientFactory's default logging handlers: they log the full
        // request URI — including the ?apikey=… query string — at Information under
        // System.Net.Http.HttpClient.StormVista.*, which the Microsoft:Warning filter
        // does not cover, leaking the key. The reader logs the sanitized relative path,
        // so diagnostics are preserved. Scoped to the StormVista client only.
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<StormVistaSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("StormVista.Http");
            return StormVistaHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<StormVistaRateLimitingHandler>();

        // Shared services used by both feeds.
        services.AddSingleton<IStormVistaReferenceProvider, SqlStormVistaReferenceProvider>();
        services.AddSingleton<IStormVistaFileLog, SqlStormVistaFileLog>();
        services.AddSingleton<StormVistaLoadValidator>();

        // Two closed windowed pipelines, built explicitly.
        services.AddSingleton<IStormVistaFeedPipeline>(BuildDailyPipeline);
        services.AddSingleton<IStormVistaFeedPipeline>(BuildRegionalPipeline);
    }

    private static IStormVistaFeedPipeline BuildDailyPipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<StormVistaSettings>>();
        var settings = options.Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IStormVistaFileLog>();
        var reference = sp.GetRequiredService<IStormVistaReferenceProvider>();

        var enumerator = new DailyWorkUnitProvider(reference, options, loggerFactory.CreateLogger<DailyWorkUnitProvider>());
        var source = new DailySourceReader(http, settings, fileLog, loggerFactory.CreateLogger<DailySourceReader>());
        var sink = new DailyWddSqlSink(options, loggerFactory.CreateLogger<DailyWddSqlSink>());

        return new StormVistaFeedPipeline<DailyWorkUnit, DailyWddRow>(
            "Daily", enumerator, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings, loggerFactory);
    }

    private static IStormVistaFeedPipeline BuildRegionalPipeline(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<StormVistaSettings>>();
        var settings = options.Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IStormVistaFileLog>();
        var reference = sp.GetRequiredService<IStormVistaReferenceProvider>();

        var enumerator = new RegionalWorkUnitProvider(reference, options, loggerFactory.CreateLogger<RegionalWorkUnitProvider>());
        var source = new RegionalSourceReader(http, settings, fileLog, reference, loggerFactory.CreateLogger<RegionalSourceReader>());
        var sink = new RegionalWddSqlSink(options, loggerFactory.CreateLogger<RegionalWddSqlSink>());

        return new StormVistaFeedPipeline<RegionalWorkUnit, RegionalWddRow>(
            "Regional", enumerator, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings, loggerFactory);
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<StormVistaSettings>>().Value;
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("StormVista.Module");
        var enabled = new HashSet<string>(settings.EnabledFeeds, StringComparer.OrdinalIgnoreCase);

        var allPipelines = services.GetServices<IStormVistaFeedPipeline>().ToList();
        var pipelines = allPipelines.Where(p => enabled.Contains(p.FeedId)).ToList();

        var known = allPipelines.Select(p => p.FeedId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in settings.EnabledFeeds.Where(f => !known.Contains(f)))
            logger.LogWarning("Enabled StormVista feed '{Feed}' has no matching pipeline and will be skipped", feed);

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No StormVista feeds enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        // Fail fast on a missing/placeholder API key before running any feed. The
        // key value itself is never logged.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey == "REPLACE-VIA-ENV-VAR")
        {
            logger.LogError(
                "StormVista API key is not configured — set the environment variable DATALOADER_Loaders__StormVista__ApiKey before running");
            return LoaderRunResult.Failed(
                Id,
                "StormVista API key is not configured; set DATALOADER_Loaders__StormVista__ApiKey.",
                TimeSpan.Zero);
        }

        // Startup configuration warnings (warn only; never silently change the operator's values).
        if (settings.DaysBack < settings.SettledAfterDays)
            logger.LogWarning(
                "StormVista DaysBack ({DaysBack}) < SettledAfterDays ({SettledAfterDays}): the incremental window is narrower than the hot zone, so some hot init-dates won't refresh incrementally — recommend DaysBack >= SettledAfterDays",
                settings.DaysBack, settings.SettledAfterDays);

        // Load the seeded reference once, up front (fail fast on a missing seed).
        try
        {
            await services.GetRequiredService<IStormVistaReferenceProvider>()
                .GetAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StormVista reference load failed — aborting the loader");
            return LoaderRunResult.Failed(Id, ex.Message, TimeSpan.Zero);
        }

        // Run the enabled feeds sequentially (they share the one rate-limited client).
        var results = new List<LoaderRunResult>();
        foreach (var pipeline in pipelines)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        // Module-level post-load validation, after both feeds complete (design §10).
        await services.GetRequiredService<StormVistaLoadValidator>()
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
