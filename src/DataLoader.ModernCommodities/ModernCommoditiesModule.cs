using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ModernCommodities;

/// <summary>
/// Plugin entry point for the Modern Commodities ("ModCom") loader — a trade tape + daily
/// settlements feed from <c>app.modcom.inc</c>. One module, <b>three closed pipelines</b>
/// (<c>AllTrades</c>, <c>MyTrades</c>, <c>Settlements</c>) built explicitly in factory closures so
/// the shared generics are never resolved from DI (design §1).
///
/// <para><b>⚠ The three pipelines are MUTUALLY INDEPENDENT</b> (design §1.2). Unlike AGSI — whose
/// two pipelines are coupled and ordered, with a discovery endpoint filling a dimension that a
/// reference provider reads back behind a hard barrier and an enforcing FK — <b>all three ModCom
/// endpoints are parameterised by DATES ALONE</b> and none returns an id another needs. There is
/// deliberately <b>no reference provider, no <c>usp_Get…</c> read proc, no tier barrier and no
/// FK</b>, and a <c>Settlements</c>-only (or <c>MyTrades</c>-only, or any-subset) run is fully
/// valid. The fixed order <c>AllTrades → MyTrades → Settlements</c> is <b>cosmetic</b>: a failure in
/// one pipeline must not prevent the others from running.</para>
///
/// <para><b>⚠ There is NO settled resume zone.</b> Every work unit's key carries <c>:run={hot}</c>,
/// so every window is re-pulled on the next clock hour regardless of the previous outcome. That is
/// not defensive padding: the trades window filters on <c>Last Updated Timestamp</c>, so
/// <b>nothing in this feed is ever final</b> — a trade can be cancelled or restated months later and
/// the rolling re-pull is the <i>only</i> mechanism that surfaces it. A stable (settled) key would
/// freeze a trade at its pre-revision state forever while the loader reported clean runs
/// (design "Rationale A").</para>
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c> — disabled by default, the CWG/AGSI/IHSPointLogic/IIR/NGI posture
/// (decision D12).</para>
/// </summary>
public sealed class ModernCommoditiesModule : ILoaderModule
{
    public const string Id = "ModernCommodities";

    /// <summary>
    /// The one authenticated, rate-limited client. <b>There is no second, un-authed client</b> —
    /// there is nothing to mint (design §4.2).
    /// </summary>
    public const string HttpClientName = "ModernCommodities";

    private const string SeeDbSentinel = "SEE_DB";

    /// <summary>The pre-flight cap warning fires when the estimated row count exceeds this fraction of the endpoint's cap.</summary>
    private const double RowCapWarnFraction = 0.60;

    /// <summary>
    /// <c>true</c> when a credential setting is still unusable: blank, or the literal
    /// <c>SEE_DB</c> placeholder compared <b>trimmed and case-insensitively</b> — a hand-edited
    /// <c>core.Param</c> row saying <c>see_db</c> or <c>" SEE_DB "</c> is just as unconfigured.
    /// <b>The value itself is never logged, echoed or returned.</b>
    /// </summary>
    private static bool IsUnresolved(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), SeeDbSentinel, StringComparison.OrdinalIgnoreCase);

    public string LoaderId => Id;
    public string DisplayName => "Modern Commodities (HTTP/CSV — trade tape & daily settlements)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Binds Loaders:ModernCommodities and wires the SEE_DB indirection: Username/Password resolve
        // lazily from core.Param(LoaderName='ModernCommodities', …) at run time.
        // *** NEVER services.Configure<ModComSettings>(…) — that skips the resolver. ***
        services.AddLoaderSettings<ModComSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler (transient), plus the
        // static Basic-auth handler — transient so it re-stamps the header on every retry attempt.
        // *** No singleton token provider: there is no token. ***
        services.AddSingleton<ModComRateLimiter>();
        services.AddTransient<ModComRateLimitingHandler>();
        services.AddTransient<ModComBasicAuthHandler>();

        // The single authenticated data client. Handler order is the repo standard (design §1.5):
        //   retry (OUTER, drives the loop; 5xx/408/network/429 — NOT 400/401/403/404)
        //     → auth (re-stamps the Basic header on every attempt)
        //       → throttle (INNER, paces every attempt).
        // RemoveAllLoggers is belt-and-braces here: the credential is a HEADER, so unlike
        // CWG/StormVista (?apikey=) the request URI is inherently safe to log — but the log surface
        // stays identical across loaders and *** the Authorization header must never be logged at any
        // level in any handler ***.
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<ModComSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "text/csv");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<ModComSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ModernCommodities.Http");
            return ModComHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<ModComBasicAuthHandler>()
        .AddHttpMessageHandler<ModComRateLimitingHandler>();

        services.AddSingleton<IModComFileLog, SqlModComFileLog>();
        services.AddSingleton<ModernCommoditiesLoadValidator>();

        // The three closed, independent pipelines, built explicitly. Nothing generic is registered:
        // no IWorkUnitProvider<T>, no ISourceReader<T>, no ISink<T> — the collision every other
        // loader here warns about (design §1.1).
        services.AddSingleton<IModComPipeline>(sp => BuildPipeline(sp, ModComDescriptors.AllTrades));
        services.AddSingleton<IModComPipeline>(sp => BuildPipeline(sp, ModComDescriptors.MyTrades));
        services.AddSingleton<IModComPipeline>(sp => BuildPipeline(sp, ModComDescriptors.Settlements));
    }

    private static IModComPipeline BuildPipeline(IServiceProvider sp, ModComEndpointDescriptor descriptor)
    {
        var options = sp.GetRequiredService<IOptions<ModComSettings>>();
        var settings = options.Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IModComFileLog>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();

        // NOTE (design §2): no reference provider is injected. This pipeline's work units come from
        // the date window ALONE — nothing is read back out of any ModCom table at run time.
        var provider = new ModComWorkUnitProvider(descriptor, settings,
            loggerFactory.CreateLogger($"ModernCommodities.{descriptor.EndpointId}.Provider"));

        var sourceLogger = loggerFactory.CreateLogger($"ModernCommodities.{descriptor.EndpointId}.Source");
        var pipelineLogger = loggerFactory.CreateLogger($"ModernCommodities.{descriptor.EndpointId}.Pipeline");

        if (descriptor.ParseShape == ModComParseShape.Trades)
        {
            var source = new ModComTradesSourceReader(http, settings, fileLog, descriptor, sourceLogger);

            // The ONLY per-endpoint difference between the two trades pipelines: which sink (i.e.
            // which merge proc / table). Row type, reader, BuildTable and TVP are shared.
            ModComSqlSinkBase<TradeRow> sink = descriptor.EndpointId == ModComEndpoints.MyTrades
                ? new MyTradesSqlSink(options, loggerFactory.CreateLogger<MyTradesSqlSink>())
                : new AllTradesSqlSink(options, loggerFactory.CreateLogger<AllTradesSqlSink>());

            return new ModComPipeline<TradeRow>(
                descriptor.EndpointId, provider, source, sink, loadLog, settings, pipelineLogger);
        }

        var settlementsSource = new ModComSettlementsSourceReader(http, settings, fileLog, descriptor, sourceLogger);
        var settlementsSink = new SettlementsSqlSink(options, loggerFactory.CreateLogger<SettlementsSqlSink>());

        return new ModComPipeline<SettlementRow>(
            descriptor.EndpointId, provider, settlementsSource, settlementsSink, loadLog, settings, pipelineLogger);
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<ModComSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ModernCommodities.Module");

        // ---- CREDENTIAL GUARD (design §1.6 step 2) -------------------------------------------
        // Unconditional: all three endpoints require the credential — there is no key-free endpoint.
        //
        // NOTE this is the SECONDARY check. Materializing IOptions<ModComSettings> above already ran
        // SeeDbSettingsResolver, which THROWS if core.Param(LoaderName='ModernCommodities',
        // ParamName='Username'/'Password') is missing — so both rows must exist for ANY run. This
        // guard catches a row whose VALUE was left as the literal SEE_DB placeholder (or blank).
        // *** NEITHER VALUE IS EVER LOGGED. ***
        var missing = new List<string>();
        if (IsUnresolved(settings.Username)) missing.Add("Username");
        if (IsUnresolved(settings.Password)) missing.Add("Password");
        if (missing.Count > 0)
        {
            var names = string.Join("/", missing);
            logger.LogError(
                "ModernCommodities credentials are not configured: {Names} — set core.Param(LoaderName='ModernCommodities', " +
                "ParamName=<the setting name>) or the matching DATALOADER_Loaders__ModernCommodities__<setting> environment " +
                "variable before running this loader",
                names);
            return LoaderRunResult.Failed(Id,
                $"ModernCommodities credentials are not configured ({names}); set core.Param(LoaderName='ModernCommodities') " +
                "or DATALOADER_Loaders__ModernCommodities__*.",
                TimeSpan.Zero);
        }

        // Loaders:ModernCommodities:EnabledEndpoints bound to JSON `null` would otherwise
        // ArgumentNullException here (the property is non-nullable, so the binder overwrites the
        // initialiser); an absent/blank list means "run nothing", reported as a warning + success.
        var enabledEndpoints = settings.EnabledEndpoints ?? Array.Empty<string>();
        var enabled = new HashSet<string>(enabledEndpoints, StringComparer.OrdinalIgnoreCase);

        var byId = services.GetServices<IModComPipeline>()
            .ToDictionary(p => p.EndpointId, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in enabledEndpoints.Where(e => !byId.ContainsKey(e)))
            logger.LogWarning("Enabled ModernCommodities endpoint '{Endpoint}' has no matching pipeline and will be skipped", endpoint);

        // Fixed order — COSMETIC ONLY (design §1.2). Any order, or full concurrency, is equally correct.
        var orderedDescriptors = ModComDescriptors.All.Where(d => enabled.Contains(d.EndpointId) && byId.ContainsKey(d.EndpointId)).ToList();

        if (orderedDescriptors.Count == 0)
        {
            logger.LogWarning("No ModernCommodities endpoints enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        LogConfigurationWarnings(settings, context, orderedDescriptors, logger);

        var results = new List<LoaderRunResult>();
        foreach (var descriptor in orderedDescriptors)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var pipeline = byId[descriptor.EndpointId];
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
                // A failure in one pipeline must NOT prevent the others from running (design §1.2):
                // the pipelines are independent, so record the failure and carry on.
                logger.LogError(ex, "ModernCommodities pipeline '{Endpoint}' failed; continuing with the remaining pipeline(s)",
                    descriptor.EndpointId);
                results.Add(LoaderRunResult.Failed(Id, $"{descriptor.EndpointId}: {ex.Message}", TimeSpan.Zero));
            }
        }

        // Module-level post-load validation, after ALL enabled pipelines complete (design §1.6 step 6
        // / §9), scoped to the union window. Observational — it logs warnings and never throws.
        await services.GetRequiredService<ModernCommoditiesLoadValidator>()
            .ValidateAsync(context, orderedDescriptors, context.CancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Configuration sanity warnings, emitted once at the top of the run <b>before any request</b>
    /// (design §1.6 step 3).
    ///
    /// <para>The cap pre-check is the pre-flight version of the row-cap <c>400</c>: it costs nothing
    /// and turns a failed run into a warning before the first request. It is a <b>warning only</b> —
    /// the estimates are measurement snapshots, never a gate and never a reason to skip a
    /// request.</para>
    /// </summary>
    private static void LogConfigurationWarnings(
        ModComSettings settings, LoaderRunContext context,
        IReadOnlyList<ModComEndpointDescriptor> descriptors, ILogger logger)
    {
        if (settings.HotKeyStrategy != ModComHotKeyStrategy.RunHour)
            logger.LogWarning(
                "ModernCommodities HotKeyStrategy={Strategy} selects a {Cadence} re-pull cadence instead of the default RunHour " +
                "(once per UTC clock hour). The host is expected to run hourly, and the rolling re-pull is the ONLY mechanism " +
                "that surfaces trade revisions and cancellations — a coarser cadence delays them by that much",
                settings.HotKeyStrategy,
                settings.HotKeyStrategy == ModComHotKeyStrategy.RunDate ? "once-per-UTC-day" : "every-invocation");

        foreach (var d in descriptors)
        {
            var daysBack = settings.EffectiveDaysBack(d.EndpointId);
            var chunkDays = settings.EffectiveChunkDays(d.EndpointId);

            ModComWindow window;
            try
            {
                window = ModComTime.ResolveWindow(context.StartedAtUtc, daysBack, d.HistoryLimited);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "[ModCom {Endpoint}] window resolution failed at pre-flight", d.EndpointId);
                continue;
            }

            if (window.Clamped)
                logger.LogInformation(
                    "[ModCom {Endpoint}] DaysBack={DaysBack} was clamped by the 6-calendar-month history limit: requested start " +
                    "{Requested}, effective start {Effective} ({Days} inclusive days)",
                    d.EndpointId, daysBack, ModComTime.Iso(window.End.AddDays(-daysBack)), ModComTime.Iso(window.Start), window.LengthDays);

            // Rows per REQUEST, i.e. per chunk — that is what the cap applies to.
            var daysPerRequest = chunkDays > 0 ? Math.Min(chunkDays, window.LengthDays) : window.LengthDays;
            var estimate = daysPerRequest * d.EstimatedRowsPerCalendarDay;

            if (estimate > d.RowCap * RowCapWarnFraction)
            {
                var recommended = d.ParseShape == ModComParseShape.Settlements ? 60 : 30;
                logger.LogWarning(
                    "[ModCom {Endpoint}] the configured window would send ~{Estimate:N0} rows per request ({Days} calendar day(s) " +
                    "x ~{Rate} rows/day), which is over {Pct:P0} of the {Cap:N0}-row cap. There is NO paging — an over-cap request " +
                    "is REJECTED with a 400, not truncated — so set " +
                    "Loaders:ModernCommodities:Endpoints:{Endpoint}:ChunkDays (currently {ChunkDays}); recommended {Recommended}",
                    d.EndpointId, estimate, daysPerRequest, d.EstimatedRowsPerCalendarDay, RowCapWarnFraction, d.RowCap,
                    d.EndpointId, chunkDays, recommended);
            }

            if (d.ParseShape == ModComParseShape.Trades && chunkDays > 90)
                logger.LogWarning(
                    "[ModCom {Endpoint}] ChunkDays={ChunkDays} exceeds the recommended trades maximum of 90. The measured per-day " +
                    "row rate is unreconciled by ~3x between the 170-day and 5-day samples, so a 90+ day chunk sits one adverse " +
                    "month away from the 10,000-row cap — 30 is the recommended value for a deep backfill",
                    d.EndpointId, chunkDays);

            if (!d.SupportsLegalEntityName && !string.IsNullOrWhiteSpace(settings.LegalEntityName))
                logger.LogDebug(
                    "[ModCom {Endpoint}] LegalEntityName is configured but this endpoint does not accept it; it is ignored here",
                    d.EndpointId);
        }
    }
}
