using System.Text.Json;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IHSPointLogic;

/// <summary>
/// Plugin entry point for the IHSPointLogic (S&amp;P Global / IHS Markit PointLogic) loader. One
/// module, 25 closed per-endpoint pipelines built explicitly (CWG pattern) and toggled by
/// <c>EnabledEndpoints[]</c>, executed in <b>three tiers with hard barriers between them</b> (design
/// §1.4): Tier 0 (19 independent lookups + snapshot facts) → Tier 1 (3 discovery-fed lookups) →
/// Tier 2 (3 parametrized facts). Each tier's reference providers read the tables the previous tier
/// wrote, so awaiting a whole tier before starting the next is load-bearing. The generic
/// provider/reader are never resolved from DI (each is <c>new</c>'d in a factory closure), avoiding
/// the shared-generic-service collision (§1.1). All 25 share one rate-limited, Basic-auth
/// <see cref="HttpClient"/>; a module-level post-load validation runs after all three tiers.
///
/// <para><b>Build-only posture:</b> wired and buildable but left OUT of
/// <c>Platform:EnabledLoaders</c> (disabled by default, CWG/AGSI posture).</para>
/// </summary>
public sealed class IHSPointLogicModule : ILoaderModule
{
    public const string Id = "IHSPointLogic";
    public const string HttpClientName = "IHSPointLogic";

    public string LoaderId => Id;
    public string DisplayName => "IHS PointLogic (HTTP/JSON — 25 gas lookups, facts & supply/demand endpoints)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLoaderSettings<IHSPointLogicSettings>(configuration, Id);

        // Shared client-side throttle (singleton state) + its delegating handler, plus the static
        // Basic-auth handler (transient — re-stamps on every retry attempt).
        services.AddSingleton<PlRateLimiter>();
        services.AddTransient<PlRateLimitingHandler>();
        services.AddTransient<PlBasicAuthHandler>();

        // Shared, rate-limited, Basic-auth HttpClient. Handler order (design §1.3/§5.1):
        //   retry (OUTER, drives the loop) → auth (re-stamps each attempt) → throttle (INNER, paces
        //   every attempt). RemoveAllLoggers suppresses the IHttpClientFactory default logging — the
        //   Basic credential is a HEADER (the URL carries no secret) but must never be logged; the
        //   reader logs only the sanitized relative path.
        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<IHSPointLogicSettings>>().Value;
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .RemoveAllLoggers()
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<IHSPointLogicSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("IHSPointLogic.Http");
            return PlHttpPolicy.Build(s.RetryCount, s.RetryDelayMs, log);
        })
        .AddHttpMessageHandler<PlBasicAuthHandler>()
        .AddHttpMessageHandler<PlRateLimitingHandler>();

        services.AddSingleton<IPlFileLog, SqlPlFileLog>();

        // The 5 reference providers (design §3) — each a singleton (= load-once-per-run).
        services.AddSingleton<IPlRegionProvider, SqlPlRegionProvider>();
        services.AddSingleton<IPlStateProvider, SqlPlStateProvider>();
        services.AddSingleton<IPlPointTypeProvider, SqlPlPointTypeProvider>();
        services.AddSingleton<IPlSubregionProvider, SqlPlSubregionProvider>();
        services.AddSingleton<IPlPointProvider, SqlPlPointProvider>();

        // Per-endpoint hourly run schedule (design §B.2) — singleton = load-once per run.
        services.AddSingleton<IPlEndpointSchedule, SqlPlEndpointSchedule>();

        services.AddSingleton<IHSPointLogicLoadValidator>();

        // ---- The 25 closed per-endpoint pipelines, built explicitly. Each line names its
        //      descriptor, its typed row factory (the only mapping code) and its per-endpoint sink.
        // Tier 0 — dimensions (A)
        Add(services, PlDescriptors.Region, RegionRow.From, sp => new RegionSqlSink(Opt(sp), Log<RegionSqlSink>(sp)));
        Add(services, PlDescriptors.State, StateRow.From, sp => new StateSqlSink(Opt(sp), Log<StateSqlSink>(sp)));
        Add(services, PlDescriptors.PointStatus, PointStatusRow.From, sp => new PointStatusSqlSink(Opt(sp), Log<PointStatusSqlSink>(sp)));
        Add(services, PlDescriptors.PointType, PointTypeRow.From, sp => new PointTypeSqlSink(Opt(sp), Log<PointTypeSqlSink>(sp)));
        Add(services, PlDescriptors.PipelineNoticeCategory, PipelineNoticeCategoryRow.From, sp => new PipelineNoticeCategorySqlSink(Opt(sp), Log<PipelineNoticeCategorySqlSink>(sp)));
        Add(services, PlDescriptors.Pipeline, PipelineRow.From, sp => new PipelineSqlSink(Opt(sp), Log<PipelineSqlSink>(sp)));
        Add(services, PlDescriptors.Point, PointRow.From, sp => new PointSqlSink(Opt(sp), Log<PointSqlSink>(sp)));
        Add(services, PlDescriptors.PointMetadata, PointMetadataRow.From, sp => new PointMetadataSqlSink(Opt(sp), Log<PointMetadataSqlSink>(sp)));
        Add(services, PlDescriptors.PipelineNoticeSearch, PipelineNoticeSearchRow.From, sp => new PipelineNoticeSearchSqlSink(Opt(sp), Log<PipelineNoticeSearchSqlSink>(sp)));
        // Tier 0 — fact snapshots (B)
        Add(services, PlDescriptors.DemandForecastRegion, DemandForecastRegionRow.From, sp => new DemandForecastRegionSqlSink(Opt(sp), Log<DemandForecastRegionSqlSink>(sp)));
        Add(services, PlDescriptors.DemandForecastUsLower48, DemandForecastUsLower48Row.From, sp => new DemandForecastUsLower48SqlSink(Opt(sp), Log<DemandForecastUsLower48SqlSink>(sp)));
        Add(services, PlDescriptors.GasProductionProducingArea, GasProductionProducingAreaRow.From, sp => new GasProductionProducingAreaSqlSink(Opt(sp), Log<GasProductionProducingAreaSqlSink>(sp)));
        Add(services, PlDescriptors.MarketBalancesUsLower48, MarketBalancesUsLower48Row.From, sp => new MarketBalancesUsLower48SqlSink(Opt(sp), Log<MarketBalancesUsLower48SqlSink>(sp)));
        Add(services, PlDescriptors.ModeledDemandRegionType, ModeledDemandRegionTypeRow.From, sp => new ModeledDemandRegionTypeSqlSink(Opt(sp), Log<ModeledDemandRegionTypeSqlSink>(sp)));
        Add(services, PlDescriptors.PipelineFlowThroughput, PipelineFlowThroughputRow.From, sp => new PipelineFlowThroughputSqlSink(Opt(sp), Log<PipelineFlowThroughputSqlSink>(sp)));
        Add(services, PlDescriptors.UsImportsExportsByPointsAggregate, UsImportsExportsByPointsAggregateRow.From, sp => new UsImportsExportsByPointsAggregateSqlSink(Opt(sp), Log<UsImportsExportsByPointsAggregateSqlSink>(sp)));
        Add(services, PlDescriptors.UsSampleStorageFacility, UsSampleStorageFacilityRow.From, sp => new UsSampleStorageFacilitySqlSink(Opt(sp), Log<UsSampleStorageFacilitySqlSink>(sp)));
        Add(services, PlDescriptors.StateFlowsThroughputAggregate, StateFlowsThroughputAggregateRow.From, sp => new StateFlowsThroughputAggregateSqlSink(Opt(sp), Log<StateFlowsThroughputAggregateSqlSink>(sp)));
        Add(services, PlDescriptors.SupplyAndDemand, SupplyAndDemandRow.From, sp => new SupplyAndDemandSqlSink(Opt(sp), Log<SupplyAndDemandSqlSink>(sp)));
        // Tier 1 — discovery-fed lookups (C)
        Add(services, PlDescriptors.County, CountyRow.From, sp => new CountySqlSink(Opt(sp), Log<CountySqlSink>(sp)));
        Add(services, PlDescriptors.Facility, FacilityRow.From, sp => new FacilitySqlSink(Opt(sp), Log<FacilitySqlSink>(sp)));
        Add(services, PlDescriptors.Subregion, SubregionRow.From, sp => new SubregionSqlSink(Opt(sp), Log<SubregionSqlSink>(sp)));
        // Tier 2 — parametrized facts (D, E)
        Add(services, PlDescriptors.SupplyAndDemandByRegion, SupplyAndDemandByRegionRow.From, sp => new SupplyAndDemandByRegionSqlSink(Opt(sp), Log<SupplyAndDemandByRegionSqlSink>(sp)));
        Add(services, PlDescriptors.SupplyAndDemandBySubRegion, SupplyAndDemandBySubRegionRow.From, sp => new SupplyAndDemandBySubRegionSqlSink(Opt(sp), Log<SupplyAndDemandBySubRegionSqlSink>(sp)));
        Add(services, PlDescriptors.PointVolume, PointVolumeRow.From, sp => new PointVolumeSqlSink(Opt(sp), Log<PointVolumeSqlSink>(sp)));
    }

    private static IOptions<IHSPointLogicSettings> Opt(IServiceProvider sp) => sp.GetRequiredService<IOptions<IHSPointLogicSettings>>();
    private static ILogger<T> Log<T>(IServiceProvider sp) => sp.GetRequiredService<ILogger<T>>();

    /// <summary>Registers one closed endpoint pipeline (design §1.3).</summary>
    private static void Add<TRow>(
        IServiceCollection services,
        PlEndpointDescriptor descriptor,
        Func<JsonElement, PlWorkUnit, TRow?> rowFactory,
        Func<IServiceProvider, ISink<TRow>> sinkFactory)
        where TRow : class, IPlFactRow
    {
        services.AddSingleton<IPlPipeline>(sp => BuildPipeline(sp, descriptor, rowFactory, sinkFactory));
    }

    /// <summary>
    /// Constructs a descriptor-bound work-unit provider (per archetype), the shared tolerant JSON
    /// pager and the per-endpoint sink, and wraps them in a <see cref="PlPipeline{TRow}"/>. The
    /// generic provider/reader are never resolved from the container, so all 25 endpoints coexist
    /// without a shared-generic-service collision (design §1.1).
    /// </summary>
    private static IPlPipeline BuildPipeline<TRow>(
        IServiceProvider sp,
        PlEndpointDescriptor descriptor,
        Func<JsonElement, PlWorkUnit, TRow?> rowFactory,
        Func<IServiceProvider, ISink<TRow>> sinkFactory)
        where TRow : class, IPlFactRow
    {
        var settings = sp.GetRequiredService<IOptions<IHSPointLogicSettings>>().Value;
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        var fileLog = sp.GetRequiredService<IPlFileLog>();

        var providerLogger = lf.CreateLogger($"IHSPointLogic.{descriptor.EndpointId}.Provider");
        IWorkUnitProvider<PlWorkUnit> provider = descriptor.Archetype switch
        {
            PlArchetype.LatestLookup or PlArchetype.GoForwardSnapshot =>
                new PlSnapshotWorkUnitProvider(descriptor, settings, providerLogger),

            PlArchetype.DiscoveryLookup =>
                new PlDiscoveryLookupWorkUnitProvider(descriptor, settings, ParentIds(sp, descriptor),
                    VariantLabel(descriptor.PathParam), providerLogger),

            PlArchetype.DiscoveryDatedFact =>
                new PlDiscoveryDatedFactWorkUnitProvider(descriptor, settings, ParentIds(sp, descriptor),
                    SubToRegionMap(sp, descriptor), VariantLabel(descriptor.PathParam), providerLogger),

            PlArchetype.BatchedFact =>
                new PlBatchedFactWorkUnitProvider(descriptor, settings, sp.GetRequiredService<IPlPointProvider>(), providerLogger),

            _ => throw new InvalidOperationException($"Unknown archetype {descriptor.Archetype} for '{descriptor.EndpointId}'.")
        };

        var source = new PlSourceReader<TRow>(http, settings, fileLog, descriptor, rowFactory,
            lf.CreateLogger($"IHSPointLogic.{descriptor.EndpointId}.Source"));
        var sink = sinkFactory(sp);

        return new PlPipeline<TRow>(
            descriptor.EndpointId, descriptor.Tier, provider, source, sink,
            sp.GetRequiredService<ILoadLogRepository>(), settings,
            lf.CreateLogger($"IHSPointLogic.{descriptor.EndpointId}.Pipeline"));
    }

    /// <summary>Selects the reference-provider id list feeding a C/D descriptor (design §3).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<int>>> ParentIds(IServiceProvider sp, PlEndpointDescriptor d) =>
        d.RefProvider switch
        {
            "Region" => sp.GetRequiredService<IPlRegionProvider>().GetRegionIdsAsync,
            "State" => sp.GetRequiredService<IPlStateProvider>().GetStateIdsAsync,
            "PointType" => sp.GetRequiredService<IPlPointTypeProvider>().GetPointTypeIdsAsync,
            "Subregion" => sp.GetRequiredService<IPlSubregionProvider>().GetSubRegionIdsAsync,
            _ => throw new InvalidOperationException($"Descriptor '{d.EndpointId}' has no id-list reference provider ('{d.RefProvider}').")
        };

    /// <summary>Non-null only for SD-by-subregion — the SubRegionId→RegionId map used to inject the parent RegionId (design §3).</summary>
    private static Func<CancellationToken, Task<IReadOnlyDictionary<int, int>>>? SubToRegionMap(IServiceProvider sp, PlEndpointDescriptor d) =>
        d.PathParam == PlPathParam.SubRegionId
            ? sp.GetRequiredService<IPlSubregionProvider>().GetSubRegionToRegionMapAsync
            : null;

    /// <summary>The FileLog Variant / param-kind label for a discovery/batched descriptor (design §6).</summary>
    private static string VariantLabel(PlPathParam pathParam) => pathParam switch
    {
        PlPathParam.StateId => "State",
        PlPathParam.PointTypeId => "PointType",
        PlPathParam.RegionId => "Region",
        PlPathParam.SubRegionId => "SubRegion",
        PlPathParam.PointBatch => "Batch",
        _ => "-"
    };

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<IHSPointLogicSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IHSPointLogic.Module");

        // Fail fast on missing/placeholder Basic-auth credentials before running any endpoint. Every
        // call carries the Basic header, so BOTH values are required. The values are never logged.
        // (Secondary guard: materializing IOptions above already ran the SEE_DB resolver, which
        // throws if the core.Param rows are missing — this catches a value left as the literal SEE_DB.)
        if (IsMissing(settings.ClientId) || IsMissing(settings.ClientSecret))
        {
            logger.LogError(
                "IHSPointLogic Basic-auth credentials are not configured — set core.Param(LoaderName='IHSPointLogic', ParamName='ClientId'|'ClientSecret') or the environment variables DATALOADER_Loaders__IHSPointLogic__ClientId / __ClientSecret before running");
            return LoaderRunResult.Failed(Id, "IHSPointLogic ClientId/ClientSecret not configured; set core.Param or DATALOADER_Loaders__IHSPointLogic__ClientId/__ClientSecret.", TimeSpan.Zero);
        }

        var enabled = new HashSet<string>(settings.EnabledEndpoints, StringComparer.OrdinalIgnoreCase);
        var allPipelines = services.GetServices<IPlPipeline>().ToList();
        var pipelines = allPipelines.Where(p => enabled.Contains(p.EndpointId)).ToList();

        var known = allPipelines.Select(p => p.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in settings.EnabledEndpoints.Where(e => !known.Contains(e)))
            logger.LogWarning("Enabled IHSPointLogic endpoint '{Endpoint}' has no matching pipeline and will be skipped", endpoint);

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No IHSPointLogic endpoints enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        // Per-endpoint hourly run schedule (design §B.3) — loaded once per run (the singleton caches it).
        var schedule = services.GetRequiredService<IPlEndpointSchedule>();

        // Run in tier order with a hard barrier between tiers (design §1.4): endpoints are sequential
        // within a tier; each pipeline fans its work units out concurrently via ParallelRunner. AWAIT
        // all of a tier's pipelines before starting the next — Tier 1's reference providers read
        // tables Tier 0 wrote, and Tier 2's read tables Tier 1 wrote.
        var results = new List<LoaderRunResult>();
        var executed = 0;
        var scheduleSkipped = 0;
        foreach (var tier in new[] { 0, 1, 2 })
        {
            var tierPipelines = pipelines.Where(p => p.Tier == tier).ToList(); // registration order preserved
            foreach (var pipeline in tierPipelines)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // §B.3: gate on the per-endpoint US-Central-hour schedule. EnabledEndpoints stays the hard
                // master; RunHoursCST is the cadence gate among enabled endpoints (the provider converts
                // the UTC run-start to Central). A gated-off pipeline enumerates no work units and makes no
                // HTTP call — it is logged and skipped, and is NOT counted as a failure (no synthetic
                // failing result is fabricated).
                if (!await schedule.ShouldRunAsync(pipeline.EndpointId, context.StartedAtUtc, context.CancellationToken).ConfigureAwait(false))
                {
                    logger.LogInformation(
                        "IHSPointLogic endpoint {Endpoint} skipped — not scheduled at run start {Hour:00}Z (schedule is US Central)",
                        pipeline.EndpointId, context.StartedAtUtc.Hour);
                    scheduleSkipped++;
                    continue;
                }

                results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
                executed++;
            }
        }

        logger.LogInformation(
            "IHSPointLogic tiers complete — ran {Ran}, skipped {Skipped} enabled endpoint(s) this run hour",
            executed, scheduleSkipped);

        // Module-level post-load validation, after all three tiers complete (design §10).
        await services.GetRequiredService<IHSPointLogicLoadValidator>()
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
