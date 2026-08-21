using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGI;

/// <summary>
/// Plugin entry point for the NGI Data Services (Bidweek price survey) loader. One module, two closed
/// pipelines (<c>BidWeekLocations</c> + <c>BidWeekData</c>) built explicitly in factory closures
/// (design §1), so the shared generics are never resolved from DI (§1.1).
///
/// <para><b>⚠ The two pipelines are MUTUALLY INDEPENDENT — this is the critical divergence from
/// AGSI</b> (design §1.2). AGSI's two pipelines are coupled and ordered (its <c>/api/about</c> pull
/// populates a dimension that a reference provider reads back to build the second pipeline's work
/// units, behind a hard barrier with a fail-fast-if-empty guard). NGI has <b>no such
/// dependency</b>: <c>/bidweekDatafeed.json</c> is parameterised by <b>date alone</b>. Consequently
/// there is deliberately <b>no reference provider, no <c>usp_Get…</c> read proc, no tier barrier and
/// no FK</b> between the tables — and a <c>BidWeekData</c>-only run is completely valid.</para>
///
/// <para><b>Order:</b> Locations runs first, then BidWeekData, <b>purely so a run's log reads
/// deterministically</b> (the 1-unit lookup pull lands before the 60-unit fan-out). It is NOT
/// load-bearing: a failure in Locations must NOT prevent BidWeekData from running, so both are
/// executed unconditionally when enabled and their results aggregated. Running them concurrently
/// would also be correct; sequential keeps the global request rate predictable against an
/// unpublished rate limit.</para>
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c> (disabled by default — the CWG/AGSI/IHSPointLogic/IIR posture).</para>
/// </summary>
public sealed class NgiModule : ILoaderModule
{
    public const string Id = "NGI";

    /// <summary>The authenticated, rate-limited data client.</summary>
    public const string HttpClientName = "NGI";

    /// <summary>The dedicated UN-AUTHED mint client. It must NOT carry the auth handler (that would recurse).</summary>
    public const string TokenClientName = "NGI.Token";

    /// <summary>The literal <c>SEE_DB</c> sentinel, checked by the secondary credential guard in <see cref="RunAsync"/>.</summary>

    /// <summary>
    /// The design's recommended floor for <see cref="NgiSettings.SettledAfterDays"/> — longer than one
    /// monthly Bidweek publication cycle (design §3.5). Below it, <see cref="RunAsync"/> warns harder.
    /// </summary>
    private const int RecommendedSettledFloorDays = 35;

    /// <summary>
    /// <c>true</c> when a credential setting is still unusable: blank, or the literal
    /// <see cref="SeeDbSentinel"/> placeholder compared <b>trimmed and case-insensitively</b>.
    ///
    /// <para>The looser comparison matters because the value can arrive from a hand-edited
    /// <c>core.Param</c> row or environment variable: <c>see_db</c> or <c>" SEE_DB "</c> is just as
    /// unconfigured as <c>SEE_DB</c>, and an exact <c>==</c> would let it through to surface later as a
    /// confusing <c>/auth</c> mint failure instead of this actionable message.</para>
    ///
    /// <para><b>The value itself is never logged, echoed or returned.</b></para>
    /// </summary>
    private static bool IsUnresolved(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), SeeDbSentinel, StringComparison.OrdinalIgnoreCase);
    private const string SeeDbSentinel = "SEE_DB";

    public string LoaderId => Id;
    public string DisplayName => "NGI Data Services (HTTP/JSON — Bidweek natural-gas price survey)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Binds Loaders:NGI and wires the SEE_DB indirection: Username/Password resolve lazily from
        // core.Param(LoaderName='NGI', …) at run time. NEVER services.Configure<NgiSettings>(…).
        services.AddLoaderSettings<NgiSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler (transient).
        services.AddSingleton<NgiRateLimiter>();
        services.AddTransient<NgiRateLimitingHandler>();

        // JWT machinery: the provider is a SINGLETON (one mint per process, shared cache and one
        // thread-safe re-mint path across both pipelines); the auth handler is transient so it
        // re-stamps the Bearer header on every retry attempt.
        services.AddSingleton<INgiTokenProvider, NgiTokenProvider>();
        services.AddTransient<NgiTokenAuthHandler>();

        // ---- the un-authed mint client -------------------------------------------------------
        // RemoveAllLoggers is belt-and-braces here: unlike CWG/StormVista (?apikey=) and IIR (creds in
        // the mint query string), NGI's secret is in the /auth request BODY, so the URL carries nothing
        // sensitive — but the log surface stays identical across loaders, and the provider never logs
        // the body. A SMALL bounded retry absorbs a transient 5xx/429/network blip on /auth while a
        // persistent 401/403 (a real credential failure) is NOT retried and surfaces loudly. It carries
        // NO auth handler: it mints the token and cannot depend on it.
        services.AddHttpClient(TokenClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<NgiSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<NgiSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("NGI.Token.Http");
            return NgiHttpPolicy.Build(Math.Clamp(s.RetryCount, 0, 3), s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<NgiRateLimitingHandler>();

        // ---- the authenticated data client ---------------------------------------------------
        // Handler order is the repo standard (design §1.4):
        //   retry (OUTER, drives the loop; 5xx/408/network/429 — NOT 400/401/403/404) →
        //   auth (re-stamps the Bearer each attempt, re-mints once on 401) →
        //   throttle (INNER, paces every attempt incl. the 401 replay).
        // 401 is EXCLUDED from the policy precisely so NgiTokenAuthHandler owns it.
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<NgiSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<NgiSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("NGI.Http");
            return NgiHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<NgiTokenAuthHandler>()
        .AddHttpMessageHandler<NgiRateLimitingHandler>();

        services.AddSingleton<INgiFileLog, SqlNgiFileLog>();
        services.AddSingleton<NgiLoadValidator>();

        // The two closed, independent pipelines, built explicitly.
        services.AddSingleton<INgiPipeline>(BuildLocationsPipeline);
        services.AddSingleton<INgiPipeline>(BuildBidWeekDataPipeline);
    }

    private static INgiPipeline BuildLocationsPipeline(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<NgiSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<INgiFileLog>();

        var provider = new NgiLocationsWorkUnitProvider(settings, loggerFactory.CreateLogger("NGI.BidWeekLocations.Provider"));
        var source = new NgiLocationsSourceReader(http, settings, fileLog, loggerFactory.CreateLogger("NGI.BidWeekLocations.Source"));
        var sink = new BidWeekLocationSqlSink(sp.GetRequiredService<IOptions<NgiSettings>>(),
            loggerFactory.CreateLogger<BidWeekLocationSqlSink>());

        return new NgiPipeline<NgiLocationsWorkUnit, BidWeekLocationRow>(
            NgiEndpoints.BidWeekLocations, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            loggerFactory.CreateLogger("NGI.BidWeekLocations.Pipeline"));
    }

    private static INgiPipeline BuildBidWeekDataPipeline(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<NgiSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<INgiFileLog>();

        // NOTE (design §2): no reference provider is injected here. This pipeline's work units come
        // from the date window ALONE — nothing is read back out of arm.BidWeekLocation.
        var provider = new NgiBidWeekWorkUnitProvider(settings, loggerFactory.CreateLogger("NGI.BidWeekData.Provider"));
        var source = new NgiBidWeekSourceReader(http, settings, fileLog, loggerFactory.CreateLogger("NGI.BidWeekData.Source"));
        var sink = new BidWeekDataSqlSink(sp.GetRequiredService<IOptions<NgiSettings>>(),
            loggerFactory.CreateLogger<BidWeekDataSqlSink>());

        return new NgiPipeline<NgiBidWeekWorkUnit, BidWeekDataRow>(
            NgiEndpoints.BidWeekData, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            loggerFactory.CreateLogger("NGI.BidWeekData.Pipeline"));
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<NgiSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("NGI.Module");

        // Surface a misconfigured host time-zone database: the window silently falls back to UTC when
        // neither US-Central id resolves, which would shift the window boundary (design §7).
        if (NgiTime.UsingUtcFallback)
            logger.LogWarning(
                "NGI could not resolve the US Central time zone (neither 'America/Chicago' nor 'Central Standard Time'); " +
                "falling back to UTC for issue-date enumeration — verify the host time-zone database");

        // ---- RESUME-HEURISTIC GUARD (design §3.5) --------------------------------------------
        // The settled zone trades re-probing for cheap skips, and that trade is only safe while a
        // settled date can no longer gain a publication: a 404 COMPLETES its unit SUCCESSFULLY, so a
        // TRANSIENT 404 on a stable-key (settled) date is recorded as permanently done and that issue
        // is lost with NO further signal. The shipped 60/60 default keeps the settled zone EMPTY; any
        // configuration that opens it says so out loud here, at the top of the run.
        var daysBack = Math.Max(1, settings.DaysBack);
        var settledAfterDays = Math.Max(0, settings.SettledAfterDays);   // clamped identically in the provider
        if (settledAfterDays < daysBack)
        {
            logger.LogWarning(
                "NGI SettledAfterDays={SettledAfterDays} is below DaysBack={DaysBack}: candidate issue dates aged " +
                "{OldestHot}..{OldestInWindow} days get a STABLE resume key, so a TRANSIENT 404 on one of them is " +
                "recorded as a PERMANENT success and that issue is lost silently (design §3.5). Safest is " +
                "SettledAfterDays >= DaysBack (the shipped 60/60 default, all-hot)",
                settledAfterDays, daysBack, settledAfterDays + 1, daysBack - 1);

            if (settledAfterDays < RecommendedSettledFloorDays)
                logger.LogWarning(
                    "NGI SettledAfterDays={SettledAfterDays} is below the recommended floor of {Floor} days — Bidweek is " +
                    "a MONTHLY feed, so below one publication cycle a date can settle before its issue was ever " +
                    "published and a single transient 404 permanently loses a whole month of prices (design §3.5)",
                    settledAfterDays, RecommendedSettledFloorDays);
        }

        // ---- CREDENTIAL GUARD (design §1.5 step 3) -------------------------------------------
        // Unconditional: BOTH data paths declare security:[{jwtAuth:[]}], so there is no key-free
        // endpoint (contrast AGSI, whose /api/about is public and whose guard is Storage-only).
        //
        // NOTE this is the SECONDARY check. Materializing IOptions<NgiSettings> above already ran
        // SeeDbSettingsResolver, which THROWS if core.Param(LoaderName='NGI', ParamName='Username'/
        // 'Password') is missing — so both rows must exist for ANY run. This guard catches a row whose
        // VALUE was left as the literal SEE_DB placeholder (or blank).
        // *** NEITHER VALUE IS EVER LOGGED. ***
        var missing = new List<string>();
        if (IsUnresolved(settings.Username)) missing.Add("Username");
        if (IsUnresolved(settings.Password)) missing.Add("Password");
        if (missing.Count > 0)
        {
            var names = string.Join("/", missing);
            logger.LogError(
                "NGI credentials are not configured: {Names} — set core.Param(LoaderName='NGI', ParamName=<the setting name>) " +
                "or the matching DATALOADER_Loaders__NGI__<setting> environment variable before running this loader",
                names);
            return LoaderRunResult.Failed(Id,
                $"NGI credentials are not configured ({names}); set core.Param(LoaderName='NGI') or DATALOADER_Loaders__NGI__*.",
                TimeSpan.Zero);
        }

        // Loaders:NGI:EnabledEndpoints bound to JSON `null` would otherwise ArgumentNullException here
        // (the property is non-nullable, so the binder overwrites the initialiser); an absent/blank
        // list means "run nothing", which the guard below reports as a warning + success.
        var enabledEndpoints = settings.EnabledEndpoints ?? Array.Empty<string>();

        var enabled = new HashSet<string>(enabledEndpoints, StringComparer.OrdinalIgnoreCase);
        var byId = services.GetServices<INgiPipeline>()
            .ToDictionary(p => p.EndpointId, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in enabledEndpoints.Where(e => !byId.ContainsKey(e)))
            logger.LogWarning("Enabled NGI endpoint '{Endpoint}' has no matching pipeline and will be skipped", endpoint);

        // Fixed order: Locations first, then BidWeekData — COSMETIC ONLY (design §1.2). Either order,
        // or concurrent execution, would be equally correct.
        var ordered = new List<INgiPipeline>();
        if (enabled.Contains(NgiEndpoints.BidWeekLocations) && byId.TryGetValue(NgiEndpoints.BidWeekLocations, out var locations))
            ordered.Add(locations);
        if (enabled.Contains(NgiEndpoints.BidWeekData) && byId.TryGetValue(NgiEndpoints.BidWeekData, out var bidweek))
            ordered.Add(bidweek);

        if (ordered.Count == 0)
        {
            logger.LogWarning("No NGI endpoints enabled");
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
                // A failure in one pipeline must NOT prevent the other from running (design §1.2):
                // the pipelines are independent, so record the failure and carry on.
                logger.LogError(ex, "NGI pipeline '{Endpoint}' failed; continuing with the remaining pipeline(s)", pipeline.EndpointId);
                results.Add(LoaderRunResult.Failed(Id, $"{pipeline.EndpointId}: {ex.Message}", TimeSpan.Zero));
            }
        }

        // Module-level post-load validation, after BOTH pipelines complete (design §1.5 step 6 / §9).
        // Observational — it logs warnings and never throws.
        await services.GetRequiredService<NgiLoadValidator>()
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
