using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Plugin entry point for the Evolution Markets ("EVO DataPipeline API") loader. One module, one
/// closed pipeline built explicitly in a factory closure (design §1), so the shared generics are
/// never resolved from DI (§1.1).
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c> (disabled by default — the CWG/AGSI/IHSPointLogic/IIR/NGI/
/// ModernCommodities posture). The SQL has never been deployed and no data has been loaded.</para>
/// </summary>
public sealed class EvolutionMarketsModule : ILoaderModule
{
    public const string Id = "EvolutionMarkets";

    /// <summary>The authenticated, rate-limited data client.</summary>
    public const string HttpClientName = "EvolutionMarkets";

    /// <summary>The literal <c>SEE_DB</c> sentinel, checked by the secondary credential guard in <see cref="RunAsync"/>.</summary>
    private const string SeeDbSentinel = "SEE_DB";

    /// <summary>
    /// <c>true</c> when the API key is still unusable: blank, or the literal
    /// <see cref="SeeDbSentinel"/> placeholder compared <b>trimmed and case-insensitively</b>.
    ///
    /// <para>The looser comparison matters because the value can arrive from a hand-edited
    /// <c>core.Param</c> row or environment variable: <c>see_db</c> or <c>" SEE_DB "</c> is just as
    /// unconfigured as <c>SEE_DB</c>, and an exact <c>==</c> would let it through to surface later as
    /// an opaque <c>403</c> on every work unit instead of this actionable message.</para>
    ///
    /// <para><b>The value itself is never logged, echoed or returned.</b></para>
    /// </summary>
    private static bool IsUnresolved(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), SeeDbSentinel, StringComparison.OrdinalIgnoreCase);

    public string LoaderId => Id;
    public string DisplayName => "Evolution Markets (HTTP/JSON — EVO DataPipeline market-data history)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Binds Loaders:EvolutionMarkets and wires the SEE_DB indirection: ApiKey resolves lazily
        // from core.Param(LoaderName='EvolutionMarkets', …) at run time.
        // NEVER services.Configure<EvoSettings>(…).
        services.AddLoaderSettings<EvoSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler (transient).
        services.AddSingleton<EvoRateLimiter>();
        services.AddTransient<EvoRateLimitingHandler>();

        // Static api-key handler — transient so it re-stamps the header on every retry attempt.
        services.AddTransient<EvoApiKeyAuthHandler>();

        // ---- the data client -----------------------------------------------------------------
        // Handler order is the repo standard (design §1.4):
        //   retry (OUTER, drives the loop; 5xx/408/network/429 — NOT 400/401/403/404) →
        //   auth (re-stamps the Authorization header each attempt) →
        //   throttle (INNERMOST, paces every attempt including each retry).
        //
        // RemoveAllLoggers: the repo standard, so the log surface is identical across loaders and
        // *** the Authorization header can never reach a log sink. *** Unlike CWG/StormVista the URL
        // itself is safe here (the key is a header, not a query parameter), but the header is not.
        //
        // There is NO token client and NO refresh path: this is a STATIC api key, not OAuth. A 401 is
        // therefore terminal rather than a trigger to re-mint (contrast NGI/IHSPointLogic).
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<EvoSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<EvoSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("EvolutionMarkets.Http");
            return EvoHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<EvoApiKeyAuthHandler>()
        .AddHttpMessageHandler<EvoRateLimitingHandler>();

        services.AddSingleton<IEvoFileLog, SqlEvoFileLog>();
        services.AddSingleton<EvolutionMarketsLoadValidator>();

        services.AddSingleton<IEvoPipeline>(BuildMarketDataPipeline);
    }

    private static IEvoPipeline BuildMarketDataPipeline(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<EvoSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IEvoFileLog>();

        var provider = new EvoMarketDataWorkUnitProvider(
            settings, loggerFactory.CreateLogger("EvolutionMarkets.MarketDataHistory.Provider"));
        var source = new EvoMarketDataSourceReader(
            http, settings, fileLog, loggerFactory.CreateLogger("EvolutionMarkets.MarketDataHistory.Source"));
        var sink = new MarketDataSqlSink(
            sp.GetRequiredService<IOptions<EvoSettings>>(), loggerFactory.CreateLogger<MarketDataSqlSink>());

        return new EvoPipeline<EvoMarketDataWorkUnit, MarketDataRow>(
            EvoEndpoints.MarketDataHistory, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            loggerFactory.CreateLogger("EvolutionMarkets.MarketDataHistory.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<EvoSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("EvolutionMarkets.Module");

        // Surface a misconfigured host time-zone database: the window silently falls back to UTC when
        // neither US-Central id resolves, which would shift the window boundary (design §7).
        if (EvoTime.UsingUtcFallback)
            logger.LogWarning(
                "EvolutionMarkets could not resolve the US Central time zone (neither 'America/Chicago' nor " +
                "'Central Standard Time'); falling back to UTC for business-date enumeration — verify the " +
                "host time-zone database");

        var daysBack = Math.Max(1, settings.DaysBack);
        var settledAfterDays = Math.Max(0, settings.SettledAfterDays);   // clamped identically in the provider

        // ---- RESUME-HEURISTIC GUARD (design §3.5) --------------------------------------------
        // The settled zone trades re-probing for cheap skips, and that trade costs two things here:
        //   (a) REVISIONS. This feed has no revision/version/status field; a corrected price arrives as
        //       the SAME marketDataId re-served with new values. Only a re-pull can ever see it.
        //   (b) LATE PUBLICATION. An empty 200 completes a unit SUCCESSFULLY (it is indistinguishable
        //       from a weekend), so a date that had merely not published yet is recorded permanently
        //       done and its prices are lost with NO further signal.
        // The shipped 30/30 default keeps the settled zone EMPTY. Any configuration that opens it says
        // so out loud, here, at the top of the run.
        if (settledAfterDays < daysBack)
            logger.LogWarning(
                "EvolutionMarkets SettledAfterDays={SettledAfterDays} is below DaysBack={DaysBack}: candidate " +
                "business dates aged {OldestHot}..{OldestInWindow} days get a STABLE resume key, so they are " +
                "pulled ONCE and never re-probed — losing any later REVISION, and permanently losing a date " +
                "that had not published yet when first probed (an empty 200 completes as SUCCESS). Safest is " +
                "SettledAfterDays >= DaysBack (the shipped 30/30 default, all-hot) — design §3.5",
                settledAfterDays, daysBack, settledAfterDays + 1, daysBack - 1);

        // ---- VENDOR RETENTION GUARD ----------------------------------------------------------
        // The vendor serves a rolling 60-day window per dataset (GET /v1/datasets → lookbackWindow).
        // Asking beyond it is not an error — it returns an empty 200 — so this is a warning, not a
        // clamp: a deliberate 90-day DaysBack simply wastes ~30 requests per run. It matters more when
        // a settled zone is also open, because a pre-retention date would then be recorded as
        // permanently done having never returned a single row.
        if (daysBack > EvoSettings.VendorLookbackDays)
            logger.LogWarning(
                "EvolutionMarkets DaysBack={DaysBack} exceeds the vendor's published retention horizon of " +
                "{Lookback} days (GET /v1/datasets -> lookbackWindow): dates older than that return an empty " +
                "200 and cost a wasted request each. Harmless while the window is all-hot; combined with a " +
                "settled zone it would record pre-retention dates as permanently loaded",
                daysBack, EvoSettings.VendorLookbackDays);

        // ---- PAGE-SIZE GUARD -----------------------------------------------------------------
        // Clamped at the point of use rather than rejected, so a mis-set value cannot fail every unit
        // with a vendor 400 the operator then has to decode.
        if (settings.PageSize < 1 || settings.PageSize > EvoSettings.MaxVendorPageSize)
            logger.LogWarning(
                "EvolutionMarkets PageSize={PageSize} is outside the vendor's accepted range 1..{Max} and will " +
                "be clamped; the vendor rejects an out-of-range limit with HTTP 400",
                settings.PageSize, EvoSettings.MaxVendorPageSize);

        // ---- CREDENTIAL GUARD (design §1.5 step 3) -------------------------------------------
        // Unconditional: every path on this API requires the key (there is no public endpoint —
        // contrast AGSI, whose /api/about is open and whose guard is therefore Storage-only).
        //
        // NOTE this is the SECONDARY check. Materializing IOptions<EvoSettings> above already ran
        // SeeDbSettingsResolver, which THROWS if core.Param(LoaderName='EvolutionMarkets',
        // ParamName='ApiKey') is missing — so that row must exist for ANY run. This guard catches a
        // row whose VALUE was left as the literal SEE_DB placeholder (or blank).
        // *** THE VALUE IS NEVER LOGGED. ***
        if (IsUnresolved(settings.ApiKey))
        {
            logger.LogError(
                "EvolutionMarkets ApiKey is not configured — set " +
                "core.Param(LoaderName='EvolutionMarkets', ParamName='ApiKey') or the " +
                "DATALOADER_Loaders__EvolutionMarkets__ApiKey environment variable before running this loader. " +
                "NOTE: the key is sent as the RAW Authorization header value, NOT as HTTP Basic");
            return LoaderRunResult.Failed(Id,
                "EvolutionMarkets ApiKey is not configured; set core.Param(LoaderName='EvolutionMarkets') " +
                "or DATALOADER_Loaders__EvolutionMarkets__ApiKey.",
                TimeSpan.Zero);
        }

        // Loaders:EvolutionMarkets:EnabledEndpoints bound to JSON `null` would otherwise
        // ArgumentNullException here (the property is non-nullable, so the binder overwrites the
        // initialiser); an absent/blank list means "run nothing", reported below as a warning + success.
        var enabledEndpoints = settings.EnabledEndpoints ?? Array.Empty<string>();

        var enabled = new HashSet<string>(enabledEndpoints, StringComparer.OrdinalIgnoreCase);
        var byId = services.GetServices<IEvoPipeline>()
            .ToDictionary(p => p.EndpointId, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in enabledEndpoints.Where(e => !byId.ContainsKey(e)))
            logger.LogWarning(
                "Enabled EvolutionMarkets endpoint '{Endpoint}' has no matching pipeline and will be skipped",
                endpoint);

        var ordered = byId
            .Where(kv => enabled.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Value)
            .ToList();

        if (ordered.Count == 0)
        {
            logger.LogWarning("No EvolutionMarkets endpoints enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        var results = new List<LoaderRunResult>();
        foreach (var pipeline in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failure in one pipeline must not prevent any other from running: record it and
                // carry on. (Only one pipeline exists today; this keeps adding a second safe.)
                logger.LogError(ex,
                    "EvolutionMarkets pipeline '{Endpoint}' failed; continuing with the remaining pipeline(s)",
                    pipeline.EndpointId);
                results.Add(LoaderRunResult.Failed(Id, $"{pipeline.EndpointId}: {ex.Message}", TimeSpan.Zero));
            }
        }

        // Module-level post-load validation (design §1.5 step 6 / §9). Observational — it logs
        // warnings and never throws.
        await services.GetRequiredService<EvolutionMarketsLoadValidator>()
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
