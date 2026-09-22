using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Marex;

/// <summary>
/// Marker so <see cref="MarexModule.RunAsync"/> can enumerate the per-feed pipelines
/// without their generics colliding with another loader's registrations in the shared
/// DI container.
/// </summary>
internal interface IMarexPipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One closed per-feed pipeline over the platform's standard ETL loop. The reader already
/// emits the sink's row type, so the transformer is the identity.
/// </summary>
internal sealed class MarexPipeline
    : LoaderPipelineBase<MarexSnapshotWorkUnit, MarexRow, MarexRow>, IMarexPipeline
{
    public MarexPipeline(
        string feedId,
        IWorkUnitProvider<MarexSnapshotWorkUnit> provider,
        ISourceReader<MarexSnapshotWorkUnit, MarexRow> source,
        ISink<MarexRow> sink,
        ILoadLogRepository loadLog,
        MarexSettings settings,
        ILogger logger)
        : base(MarexModule.Id, provider, source, new IdentityTransformer<MarexRow>(), sink, loadLog, settings, logger)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}

/// <summary>
/// Plugin entry point for the Marex <b>Neon</b> crude-market loader.
///
/// <para>
/// Five feeds land in five <c>arm</c> tables in the <c>Marex</c> database, all merged by
/// primary key:
/// </para>
/// <list type="bullet">
///   <item><c>PeriodGroupSnapshot</c>    -> <c>arm.PeriodGroup</c>     (dimension, keyed on the vendor id)</item>
///   <item><c>PeriodSnapshot</c>         -> <c>arm.Period</c>          (dimension, keyed on the vendor id)</item>
///   <item><c>ProductSnapshot</c>        -> <c>arm.Product</c>         (dimension, keyed on the vendor id)</item>
///   <item><c>ClosingPriceSnapshot</c>   -> <c>arm.ClosingPrice</c>    (keyed on ExchangeDate + the vendor id)</item>
///   <item><c>MarketStatisticSnapshot</c>-> <c>arm.MarketStatistic</c> (keyed on TradeDate + the vendor id)</item>
/// </list>
///
/// <para>
/// <b>This loader is shaped by a source that pushes rather than answers.</b> Neon is a
/// live SignalR gateway reached through the vendor's own .NET SDK (vendored in
/// <c>lib/neon</c>): the client authenticates with an Auth0 password grant, opens a
/// websocket, and the gateway pushes one complete opening snapshot of every entity and
/// then streams deltas. There is no "fetch me the closing prices" request to make.
/// </para>
/// <para>
/// So a run is: connect once, capture the opening snapshot, merge all five tables from
/// it, disconnect. The five pipelines share ONE connection
/// (<see cref="MarexSnapshotSession"/>) — the gateway sends all five entities down the
/// same socket regardless, and sharing is what guarantees the five feeds agree on the
/// exchange date that keys two of the tables.
/// </para>
/// <para>
/// The live UPDATE stream is deliberately not consumed. Consuming it would mean a
/// resident process, and this platform runs loaders as batch jobs under an overlap
/// lock that exists precisely to stop long-lived overlapping runs. Re-running on a
/// schedule re-merges the current snapshot by primary key, which is the same end state
/// for these five entities. The cost is intra-interval movement in the price and
/// statistic tables, which is why the default resume key varies by HOUR rather than by
/// day — see <see cref="MarexResumeKeyStrategy"/>.
/// </para>
/// <para>
/// <b>Four vendor behaviours fail silently or confusingly if mishandled</b>, and each is
/// documented where it bites:
/// </para>
/// <list type="number">
///   <item>
///     <c>NeonApiConfig.Name</c> is the SignalR <b>hub name</b> (<c>Gateway.Crude</c>),
///     not a client label. A wrong value makes the gateway answer the negotiate with
///     HTTP 500 while the SDK retries every 5 s forever and the status sits on
///     <c>Connecting</c> — <see cref="MarexSettings.HubName"/>.
///   </item>
///   <item>
///     Handlers must be attached BEFORE <c>Connect</c>, and the run must wait on the
///     snapshots rather than on <c>ConnectionStatus == Connected</c>, which is reached
///     AFTER the snapshot arrives — <see cref="MarexSnapshotSession"/>.
///   </item>
///   <item>
///     <c>ExchangeDate</c>/<c>TradeDate</c> come from the gateway's own
///     <c>ExchangeDateSnapshot</c>, not from any clock here —
///     <see cref="MarexDescriptors.ClosingPrice"/>.
///   </item>
///   <item>
///     <c>ClosingPriceDto.Time</c>/<c>.PreviousTime</c> are non-nullable in the SDK, so
///     "no value" arrives as <c>0001-01-01</c>, which SQL Server stores without
///     complaint — <see cref="MarexMapper.NullIfUnset(DateTime)"/>.
///   </item>
/// </list>
///
/// <para>
/// The pipelines are MUTUALLY INDEPENDENT: no barrier, no shared state beyond the
/// read-only snapshot, no FK between the target tables. A failure in one feed must not
/// cost the others, so <see cref="RunAsync"/> records the failure and carries on (the
/// Genscape/Argus/ICE/NGX posture).
/// </para>
/// </summary>
public sealed class MarexModule : ILoaderModule
{
    public const string Id = "Marex";

    public string LoaderId => Id;
    public string DisplayName => "Marex Neon — crude market snapshot (products, periods, closing prices, statistics)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // AddLoaderSettings (not a bare Configure<>) — this is what wires the SEE_DB
        // resolver that turns the "SEE_DB" ClientId/Username/Password sentinels into real
        // values from core.Param at run time.
        services.AddLoaderSettings<MarexSettings>(configuration, Id);

        services.AddSingleton<IMarexNeonClientFactory, MarexNeonClientFactory>();

        // One session per run, shared by every pipeline. Registered as a singleton
        // because the host builds one container per host run.
        services.AddSingleton(sp => new MarexSnapshotSession(
            sp.GetRequiredService<IOptions<MarexSettings>>().Value,
            sp.GetRequiredService<IMarexNeonClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Marex.Session")));

        services.AddSingleton<IReadOnlyList<IMarexPipeline>>(BuildPipelines);
    }

    private static IReadOnlyList<IMarexPipeline> BuildPipelines(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<IOptions<MarexSettings>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var loadLog = sp.GetRequiredService<ILoadLogRepository>();
        var session = sp.GetRequiredService<MarexSnapshotSession>();

        // Built explicitly rather than via open-generic DI, so this loader's
        // IWorkUnitProvider<T> and ISink<T> registrations cannot collide with another
        // loader's.
        return MarexDescriptors.All
            .Select(table => (IMarexPipeline)new MarexPipeline(
                table.FeedId,
                new MarexSnapshotWorkUnitProvider(table.FeedId, settings),
                new MarexSnapshotReader(session, MarexSnapshotReader.For(table.FeedId)),
                new MarexTvpSink(table, settings.ConnectionString,
                    loggerFactory.CreateLogger($"Marex.{table.FeedId}.Sink")),
                loadLog,
                settings,
                loggerFactory.CreateLogger($"Marex.{table.FeedId}.Pipeline")))
            .ToArray();
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Marex.Module");
        var settings = services.GetRequiredService<IOptions<MarexSettings>>().Value;
        var pipelines = services.GetRequiredService<IReadOnlyList<IMarexPipeline>>();
        var session = services.GetRequiredService<MarexSnapshotSession>();

        WarnAboutConfiguration(settings, logger);

        if (settings.EnableSdkLogging)
            MarexSdkLogBridge.Attach(
                services.GetRequiredService<ILoggerFactory>().CreateLogger("Marex.Sdk"));

        var enabled = pipelines
            .Where(p => settings.EnabledFeeds.Contains(p.FeedId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (enabled.Count == 0)
        {
            logger.LogWarning("Marex: no feeds enabled — nothing to do");
            return new LoaderRunResult { LoaderId = Id, Success = true, Duration = TimeSpan.Zero };
        }

        // The endpoint and the user name are logged; the password and the token never are.
        logger.LogInformation(
            "Marex: running {Count} of {Total} feed(s) against {Endpoint} as {User}",
            enabled.Count, pipelines.Count, settings.ApiEndpoint, settings.Username);

        var aggregate = new LoaderRunResult { LoaderId = Id, Success = true };
        var failures = new List<string>();

        try
        {
            // Sequential, and deliberately so. Every feed reads the same captured
            // snapshot, so there is no I/O to overlap — running them in parallel would
            // only contend on the write gate. The order is MarexDescriptors.All:
            // dimensions first, so that a run which dies partway leaves the fact tables
            // referencing rows that are already present.
            foreach (var pipeline in enabled)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

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
                    logger.LogError(ex, "Marex: feed {Feed} threw out of its pipeline", pipeline.FeedId);
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
        }
        finally
        {
            // Always drop the websocket. Left open it would hold a gateway session for
            // the life of the host process, and the vendor counts concurrent sessions.
            await session.DisposeAsync().ConfigureAwait(false);
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

        logger.LogInformation(
            "Marex run complete: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, {Records} record(s)",
            aggregate.WorkUnitsSucceeded, aggregate.WorkUnitsSkipped, aggregate.WorkUnitsFailed,
            aggregate.RecordsProcessed);

        return aggregate;
    }

    /// <summary>
    /// Startup checks that warn rather than throw. Each of these is a configuration that
    /// "works" but silently does less than the operator expects, which is worse than a
    /// loud failure.
    /// </summary>
    internal static void WarnAboutConfiguration(MarexSettings settings, ILogger logger)
    {
        var known = MarexDescriptors.All.Select(t => t.FeedId).ToArray();

        foreach (var id in settings.EnabledFeeds)
            if (!known.Contains(id, StringComparer.OrdinalIgnoreCase))
                logger.LogWarning("Marex: EnabledFeeds contains unknown feed id '{FeedId}' — ignored", id);

        // An unresolved sentinel means the SEE_DB lookup found no core.Param row. The
        // Auth0 grant would then be attempted with the literal string "SEE_DB" and fail
        // at the token step — which IS reported, but saying so here names the cause.
        foreach (var (name, value) in new[]
                 {
                     ("ClientId", settings.ClientId),
                     ("Username", settings.Username),
                     ("Password", settings.Password)
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("SEE_DB", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning(
                    "Marex {Setting} is still the unresolved 'SEE_DB' sentinel: no " +
                    "core.Param(LoaderName='Marex', ParamName='{Setting}') row was found. The Auth0 token " +
                    "request will fail",
                    name, name);
        }

        // Not a tuning knob — see MarexSettings.HubName. Warned about rather than
        // rejected, because a future environment could legitimately name its hub
        // differently.
        if (!string.IsNullOrWhiteSpace(settings.HubName) &&
            !settings.HubName.Equals("Gateway.Crude", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "Marex HubName='{Hub}' is neither empty nor the gateway's 'Gateway.Crude'. This value is " +
                "the SignalR HUB NAME, not a client label: if the hub does not exist the gateway answers " +
                "the negotiate with HTTP 500 and the SDK retries silently until the snapshot times out",
                settings.HubName);

        if (settings.ResumeKeyStrategy == MarexResumeKeyStrategy.RunDate)
            logger.LogWarning(
                "Marex ResumeKeyStrategy=RunDate: the first successful run of each UTC day will suppress " +
                "every later run that day. The gateway serves a LIVE snapshot — closing prices, settlement " +
                "prices and market statistics all move intraday — so RunHour is the default for a reason");

        if (settings.SnapshotTimeoutSeconds < 30)
            logger.LogWarning(
                "Marex SnapshotTimeoutSeconds={Value} is tight. The opening snapshot took ~5s against ~4,200 " +
                "closing prices when measured, but it is transferred compressed in one payload and a slow " +
                "link or a larger book will exceed a short timeout",
                settings.SnapshotTimeoutSeconds);

        if (settings.EnableSignalRTracing && !settings.EnableSdkLogging)
            logger.LogWarning(
                "Marex EnableSignalRTracing is on but EnableSdkLogging is off, so the traces have nowhere " +
                "to go — the SDK writes them through log4net, which is only bridged when EnableSdkLogging " +
                "is set");
    }
}
