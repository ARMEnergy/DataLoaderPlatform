using System.Net.Http.Headers;
using System.Text;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGX;

/// <summary>
/// Marker so <see cref="NgxModule.RunAsync"/> can enumerate the per-feed pipelines
/// without their generics colliding with another loader's registrations in the shared
/// DI container.
/// </summary>
internal interface INgxPipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. Each reader
/// already emits the sink's row type, so the transformer is the identity.
/// </summary>
internal sealed class NgxPipeline<TUnit>
    : LoaderPipelineBase<TUnit, NgxRow, NgxRow>, INgxPipeline
    where TUnit : WorkUnit
{
    public NgxPipeline(
        string feedId,
        IWorkUnitProvider<TUnit> provider,
        ISourceReader<TUnit, NgxRow> source,
        ISink<NgxRow> sink,
        ILoadLogRepository loadLog,
        NgxSettings settings,
        ILogger logger)
        : base(NgxModule.Id, provider, source, new IdentityTransformer<NgxRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the ICE NGX clearing loader.
///
/// <para>
/// Two feeds read the legacy NGX XML web services
/// (<c>ngxclearing.ice.com/ngxcs</c>) and merge into two <c>arm</c> tables in the
/// <c>NGX</c> database:
/// </para>
/// <list type="bullet">
///   <item><c>indexPrice.xml</c> -> <c>arm.IndexPrice</c> — a daily snapshot of a
///     rolling [3 months back .. 6 months forward] delivery window, for the indices
///     listed in <c>dbo.[Index]</c>;</item>
///   <item><c>stripTradingSummaryXml.xml</c> -> <c>arm.StripTradingSummary</c> — every
///     reported trade over a rolling [1 month back .. 1 month forward] window.</item>
/// </list>
/// <para>
/// Everything is the platform's standard machinery — work units, resume keys, TVP
/// merges, the write gate — with four vendor quirks that are all documented where they
/// bite, and all of which fail SILENTLY if mishandled:
/// </para>
/// <list type="number">
///   <item>
///     authentication is HTTP <b>Basic</b>, not the documented <c>/api/v2</c> bearer
///     token — the token is simply ignored here and the request 302s to the SSO login
///     form (<see cref="NgxReaderBase"/>);
///   </item>
///   <item>
///     at most <b>10</b> <c>indexId</c> parameters per request, and one unentitled id
///     fails the WHOLE batch with a 403 (<see cref="NgxIndexPriceReader"/>);
///   </item>
///   <item>
///     the index response silently truncates to <b>50 rows</b> unless <c>pageSize</c>
///     is sent, and the paging parameter is <c>page</c> — <c>pageNumber</c> and
///     <c>size</c> are accepted and ignored (<see cref="NgxIndexPriceReader"/>);
///   </item>
///   <item>
///     every timestamp must be converted from the vendor's Mountain offset to <b>US
///     Central</b> to match the incumbent, and <c>TradeDateTime</c> is a primary key
///     component (<see cref="NgxTime"/>).
///   </item>
/// </list>
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state, no FK between
/// the target tables. A failure in one feed must not cost the other, so
/// <see cref="RunAsync"/> records the failure and carries on (the Genscape/Argus/ICE
/// posture).
/// </para>
/// <para>
/// Pipelines are built explicitly rather than via open-generic DI, so this loader's
/// <c>IWorkUnitProvider&lt;T&gt;</c> and <c>ISink&lt;T&gt;</c> registrations cannot
/// collide with another loader's.
/// </para>
/// </summary>
public sealed class NgxModule : ILoaderModule
{
    public const string Id = "NGX";

    /// <summary>Named <c>HttpClient</c> for both feeds.</summary>
    public const string HttpClientName = "NGX";

    public string LoaderId => Id;
    public string DisplayName => "ICE NGX clearing — index prices and strip trading summary";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" Username/Password sentinels into real values
        // from core.Param at run time.
        services.AddLoaderSettings<NgxSettings>(configuration, Id);

        services.AddSingleton<NgxLoadValidator>();

        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<NgxSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, s.HttpTimeoutSeconds));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

            // HTTP Basic. The credentials go in a header, never in a URL, so the
            // request lines the reader logs carry no secret. The factory's own logging
            // handlers are removed anyway, matching CWG/AGSI/Genscape.
            if (!string.IsNullOrWhiteSpace(s.Username))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Username}:{s.Password}")));
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // ⚠ MUST NOT follow redirects. An unauthenticated request 302s to the ICE
            // SSO login page, which returns 200 and 33 KB of HTML — following it would
            // turn an authentication failure into a silent "zero records" result on
            // every work unit. The reader turns the 302 into a loud error instead.
            AllowAutoRedirect = false
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<NgxSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("NGX.Http");
            return NgxHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        });

        services.AddSingleton<IReadOnlyList<INgxPipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<INgxPipeline> BuildPipelines(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<NgxSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var catalog = new NgxIndexCatalog(
            settings.ConnectionString, settings.IndexTypeFilter,
            loggerFactory.CreateLogger("NGX.IndexCatalog"));

        var indexPipeline = new NgxPipeline<NgxIndexPriceWorkUnit>(
            NgxDescriptors.IndexPriceFeedId,
            new NgxIndexPriceWorkUnitProvider(catalog, settings, loggerFactory.CreateLogger("NGX.IndexPrice.Units")),
            new NgxIndexPriceReader(httpFactory.CreateClient(HttpClientName), settings, loggerFactory.CreateLogger("NGX.IndexPrice.Reader")),
            new NgxTvpSink(NgxDescriptors.IndexPrice, settings.ConnectionString, loggerFactory.CreateLogger("NGX.IndexPrice.Sink")),
            loadLog, settings, loggerFactory.CreateLogger("NGX.IndexPrice.Pipeline"));

        var stripPipeline = new NgxPipeline<NgxStripWorkUnit>(
            NgxDescriptors.StripFeedId,
            new NgxStripWorkUnitProvider(settings, loggerFactory.CreateLogger("NGX.Strip.Units")),
            new NgxStripReader(httpFactory.CreateClient(HttpClientName), settings, loggerFactory.CreateLogger("NGX.Strip.Reader")),
            new NgxTvpSink(NgxDescriptors.StripTradingSummary, settings.ConnectionString, loggerFactory.CreateLogger("NGX.Strip.Sink")),
            loadLog, settings, loggerFactory.CreateLogger("NGX.Strip.Pipeline"));

        return new INgxPipeline[] { indexPipeline, stripPipeline };
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("NGX.Module");
        var settings = services.GetRequiredService<IOptions<NgxSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<INgxPipeline>>();

        WarnAboutConfiguration(settings, logger);

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("NGX: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        var executionDate = NgxTime.Today(context.StartedAtUtc);

        // The base URL and the user name are logged; the password never is.
        logger.LogInformation(
            "NGX: running {Count} of {Total} feed(s) against {BaseUrl} as {User}, ExecutionDate {Exec} (US Central)",
            enabled.Count, pipelines.Count, settings.BaseUrl, settings.Username, NgxTime.Iso(executionDate));

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        foreach (var pipeline in enabled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A feed's failure is recorded and the run continues: the pipelines are
            // independent, and losing one feed must not cost the other.
            LoaderRunResult result;
            try
            {
                result = await pipeline.ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NGX: feed {Feed} threw out of its pipeline", pipeline.FeedId);
                failures.Add(pipeline.FeedId);
                continue;
            }

            if (!result.Success) failures.Add(pipeline.FeedId);

            aggregate = new LoaderRunResult
            {
                LoaderId = Id,
                Success = aggregate.Success && result.Success,
                WorkUnitsTotal = aggregate.WorkUnitsTotal + result.WorkUnitsTotal,
                WorkUnitsSucceeded = aggregate.WorkUnitsSucceeded + result.WorkUnitsSucceeded,
                WorkUnitsSkipped = aggregate.WorkUnitsSkipped + result.WorkUnitsSkipped,
                WorkUnitsFailed = aggregate.WorkUnitsFailed + result.WorkUnitsFailed,
                RecordsProcessed = aggregate.RecordsProcessed + result.RecordsProcessed,
                Duration = aggregate.Duration + result.Duration
            };
        }

        if (failures.Count > 0)
        {
            // LoaderRunResult is an init-only class, not a record — rebuild it.
            aggregate = new LoaderRunResult
            {
                LoaderId = Id,
                Success = false,
                ErrorMessage = $"Failed feed(s): {string.Join(", ", failures)}",
                WorkUnitsTotal = aggregate.WorkUnitsTotal,
                WorkUnitsSucceeded = aggregate.WorkUnitsSucceeded,
                WorkUnitsSkipped = aggregate.WorkUnitsSkipped,
                WorkUnitsFailed = aggregate.WorkUnitsFailed,
                RecordsProcessed = aggregate.RecordsProcessed,
                Duration = aggregate.Duration
            };
        }

        // Post-load validation is observational and must not change the run's outcome —
        // NgxLoadValidator swallows its own failures.
        await services.GetRequiredService<NgxLoadValidator>()
            .ValidateAsync(
                executionDate,
                enabled.Any(p => p.FeedId == NgxDescriptors.IndexPriceFeedId),
                enabled.Any(p => p.FeedId == NgxDescriptors.StripFeedId),
                context.CancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "NGX run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration that
    /// "works" but silently does less than the operator expects, which is worse than a
    /// loud failure.
    /// </summary>
    internal static void WarnAboutConfiguration(NgxSettings settings, ILogger logger)
    {
        var known = new[] { NgxDescriptors.IndexPriceFeedId, NgxDescriptors.StripFeedId };

        foreach (var id in settings.EnabledFeeds)
            if (!known.Contains(id, StringComparer.OrdinalIgnoreCase))
                logger.LogWarning("NGX: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);

        // An unresolved sentinel means the SEE_DB lookup found no core.Param row. Every
        // request would then go out with the literal string as the credential and come
        // back 302 to the SSO login form, which the reader reports — but saying so here
        // is worth a lot.
        foreach (var (name, value) in new[] { ("Username", settings.Username), ("Password", settings.Password) })
            if (string.IsNullOrWhiteSpace(value) || value.Equals("SEE_DB", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning(
                    "NGX {Setting} is still the unresolved 'SEE_DB' sentinel: no " +
                    "core.Param(LoaderName='NGX', ParamName='{Setting}') row was found. Every request will " +
                    "be redirected to the ICE SSO login page",
                    name, name);

        // The 10-id ceiling is a measured vendor limit, not a tuning knob. Above it
        // EVERY work unit fails 403, so a misconfiguration here is total.
        if (settings.IndexIdsPerRequest > 10)
            logger.LogWarning(
                "NGX IndexIdsPerRequest={Value} exceeds the vendor's hard limit of 10 indexId parameters " +
                "per request and has been clamped to 10. Above the limit the endpoint answers 403 for the " +
                "whole request, which would fail every index work unit",
                settings.IndexIdsPerRequest);

        if (settings.IndexIdsPerRequest < 1)
            logger.LogWarning(
                "NGX IndexIdsPerRequest={Value} is below 1 and has been clamped to 1 — the feed will " +
                "issue one request per index",
                settings.IndexIdsPerRequest);

        // Below the 50-row default the vendor truncates and the reader has to page,
        // which works but costs a request per 50 rows over a ~1,200-row window.
        if (settings.IndexPageSize < 50)
            logger.LogWarning(
                "NGX IndexPageSize={Value} is below the vendor's own default of 50. The reader will page " +
                "correctly but will issue many more requests than necessary",
                settings.IndexPageSize);

        if (settings.IndexPageSize > 20000)
            logger.LogWarning(
                "NGX IndexPageSize={Value} is above the server-side cap of 20,000. The vendor accepts the " +
                "value and silently clamps it; the reader clamps too so the request matches the log",
                settings.IndexPageSize);

        if (!settings.IndexTypeFilter.Equals("IndexPrice", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "NGX IndexTypeFilter='{Filter}'. Only the 'IndexPrice' rows of dbo.[Index] are entitled on " +
                "indexPrice.xml — the 'CrudeIndexPrice' rows belong to a different endpoint and return 403. " +
                "Because one unentitled id fails its WHOLE batch, a wider filter costs entitled indices too " +
                "(the reader recovers by narrowing, at one request per index)",
                settings.IndexTypeFilter);

        if (settings.IndexMonthsBack < 0 || settings.IndexMonthsForward < 0)
            logger.LogWarning(
                "NGX index window months are negative (back={Back}, forward={Forward}) and have been " +
                "clamped to 0 — the window will collapse to the current month",
                settings.IndexMonthsBack, settings.IndexMonthsForward);

        if (settings.StripChunkDays > 31)
            logger.LogWarning(
                "NGX StripChunkDays={Value} asks for more than a month of trades in one request. A calendar " +
                "month is ~36,000 records and ~29 MB of XML, and this endpoint has NO truncation flag — " +
                "there would be no way to tell a capped response from a complete one",
                settings.StripChunkDays);

        if (settings.StripChunkDays < 1)
            logger.LogWarning(
                "NGX StripChunkDays={Value} is below 1 and has been clamped to 1 (one request per day)",
                settings.StripChunkDays);

        if (settings.StripSettledAfterDays < 0)
            logger.LogWarning(
                "NGX StripSettledAfterDays={Value} is negative and has been clamped to 0: every chunk " +
                "becomes settled immediately, so amended trades would never be re-pulled",
                settings.StripSettledAfterDays);
    }
}
