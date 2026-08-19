namespace DataLoader.IHSPointLogic;

/// <summary>
/// Static description of one IHSPointLogic endpoint (design §2). The descriptor
/// registry is the (compile-time) discovery result — enumeration is DB-free.
/// Everything a work-unit provider, source reader and FileLog writer needs to drive
/// one endpoint comes from here; the only per-endpoint C# is the row type, its
/// <c>From(elem, unit)</c> factory and its sink.
/// </summary>
/// <param name="EndpointId">Stable id, e.g. "Region" … "PointVolume".</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Family">P (cs/v1/pointlogic/*) | V (cs/v1/plview/retrieve/*).</param>
/// <param name="Archetype">Selects the work-unit provider + resume-key rule (design §4).</param>
/// <param name="Tier">0 | 1 | 2 — run order + barriers (design §1.4).</param>
/// <param name="PathTemplate">Relative path, may contain <c>{id}</c>.</param>
/// <param name="PathParam">None | StateId | PointTypeId | RegionId | SubRegionId | PointBatch.</param>
/// <param name="UsesReportDate">true → append <c>?reportDate=yyyy-MM-dd</c> (archetype D only).</param>
/// <param name="Envelope">FlatArray | Wrapper — informational; the reader auto-detects &amp; pages BOTH.</param>
/// <param name="ReportDateBasis">None | PayloadCarried | StampUtcRunDate | ParamInjected.</param>
/// <param name="RefProvider">"Region" | "State" | "PointType" | "Subregion" | "Point" | null.</param>
/// <param name="TargetTable">arm.&lt;Table&gt; (informational; the sink is per-endpoint).</param>
/// <param name="TargetTvp">arm.&lt;Table&gt;Tvp (informational).</param>
/// <param name="TargetProc">arm.usp_BulkMerge&lt;Table&gt; (informational).</param>
public sealed record PlEndpointDescriptor(
    string EndpointId,
    string DisplayName,
    PlFamily Family,
    PlArchetype Archetype,
    int Tier,
    string PathTemplate,
    PlPathParam PathParam,
    bool UsesReportDate,
    PlEnvelope Envelope,
    PlReportDateBasis ReportDateBasis,
    string? RefProvider,
    string TargetTable,
    string TargetTvp,
    string TargetProc);

/// <summary>The 25 concrete IHSPointLogic endpoint descriptors (design §2.1) — the static discovery result.</summary>
public static class PlDescriptors
{
    // ------------------------------------------------------------------ TIER 0 — dimensions (A)
    public static readonly PlEndpointDescriptor Region = new(
        "Region", "Region lookup", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_region", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.Region", "arm.RegionTvp", "arm.usp_BulkMergeRegion");

    public static readonly PlEndpointDescriptor State = new(
        "State", "State lookup", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_state", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.State", "arm.StateTvp", "arm.usp_BulkMergeState");

    public static readonly PlEndpointDescriptor PointStatus = new(
        "PointStatus", "Point status lookup", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_pointStatus", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.PointStatus", "arm.PointStatusTvp", "arm.usp_BulkMergePointStatus");

    public static readonly PlEndpointDescriptor PointType = new(
        "PointType", "Point type lookup", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_pointType", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.PointType", "arm.PointTypeTvp", "arm.usp_BulkMergePointType");

    public static readonly PlEndpointDescriptor PipelineNoticeCategory = new(
        "PipelineNoticeCategory", "Pipeline notice category lookup", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_pipelineNoticeCategory", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.PipelineNoticeCategory", "arm.PipelineNoticeCategoryTvp", "arm.usp_BulkMergePipelineNoticeCategory");

    public static readonly PlEndpointDescriptor Pipeline = new(
        "Pipeline", "Pipeline lookup", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_pipeline", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.Pipeline", "arm.PipelineTvp", "arm.usp_BulkMergePipeline");

    public static readonly PlEndpointDescriptor Point = new(
        "Point", "Point lookup (paged wrapper)", PlFamily.P, PlArchetype.LatestLookup, 0,
        "cs/v1/pointlogic/lookup_point", PlPathParam.None, false, PlEnvelope.Wrapper,
        PlReportDateBasis.None, null,
        "arm.Point", "arm.PointTvp", "arm.usp_BulkMergePoint");

    public static readonly PlEndpointDescriptor PointMetadata = new(
        "PointMetadata", "Point metadata (paged flat)", PlFamily.V, PlArchetype.LatestLookup, 0,
        "cs/v1/plview/retrieve/pointmetadata_withids", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, null,
        "arm.PointMetadata", "arm.PointMetadataTvp", "arm.usp_BulkMergePointMetadata");

    public static readonly PlEndpointDescriptor PipelineNoticeSearch = new(
        "PipelineNoticeSearch", "Pipeline notice search (paged flat)", PlFamily.V, PlArchetype.LatestLookup, 0,
        "cs/v1/plview/retrieve/pipelinenotice_search", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.PipelineNoticeSearch", "arm.PipelineNoticeSearchTvp", "arm.usp_BulkMergePipelineNoticeSearch");

    // ------------------------------------------------------------------ TIER 0 — fact snapshots (B)
    public static readonly PlEndpointDescriptor DemandForecastRegion = new(
        "DemandForecastRegion", "Demand forecast by region", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/demandforecast_region", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.StampUtcRunDate, null,
        "arm.DemandForecastRegion", "arm.DemandForecastRegionTvp", "arm.usp_BulkMergeDemandForecastRegion");

    public static readonly PlEndpointDescriptor DemandForecastUsLower48 = new(
        "DemandForecastUsLower48", "Demand forecast US Lower-48", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/demandforecast_uslower48", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.StampUtcRunDate, null,
        "arm.DemandForecastUsLower48", "arm.DemandForecastUsLower48Tvp", "arm.usp_BulkMergeDemandForecastUsLower48");

    public static readonly PlEndpointDescriptor GasProductionProducingArea = new(
        "GasProductionProducingArea", "Gas production by producing area", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/gasproduction_producingarea", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.GasProductionProducingArea", "arm.GasProductionProducingAreaTvp", "arm.usp_BulkMergeGasProductionProducingArea");

    public static readonly PlEndpointDescriptor MarketBalancesUsLower48 = new(
        "MarketBalancesUsLower48", "Market balances US Lower-48", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/marketbalances_uslower48", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.MarketBalancesUsLower48", "arm.MarketBalancesUsLower48Tvp", "arm.usp_BulkMergeMarketBalancesUsLower48");

    public static readonly PlEndpointDescriptor ModeledDemandRegionType = new(
        "ModeledDemandRegionType", "Modeled demand by region/type", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/modeleddemand_region_type", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.ModeledDemandRegionType", "arm.ModeledDemandRegionTypeTvp", "arm.usp_BulkMergeModeledDemandRegionType");

    public static readonly PlEndpointDescriptor PipelineFlowThroughput = new(
        "PipelineFlowThroughput", "Pipeline flow throughputs", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/pipelineflow_throughputs", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.PipelineFlowThroughput", "arm.PipelineFlowThroughputTvp", "arm.usp_BulkMergePipelineFlowThroughput");

    public static readonly PlEndpointDescriptor UsImportsExportsByPointsAggregate = new(
        "UsImportsExportsByPointsAggregate", "US imports/exports by points (aggregate)", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/us_importsexportsby_points_aggregate", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.UsImportsExportsByPointsAggregate", "arm.UsImportsExportsByPointsAggregateTvp", "arm.usp_BulkMergeUsImportsExportsByPointsAggregate");

    public static readonly PlEndpointDescriptor UsSampleStorageFacility = new(
        "UsSampleStorageFacility", "US sample storage by facility (paged)", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/us_samplestorage_facility", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.UsSampleStorageFacility", "arm.UsSampleStorageFacilityTvp", "arm.usp_BulkMergeUsSampleStorageFacility");

    public static readonly PlEndpointDescriptor StateFlowsThroughputAggregate = new(
        "StateFlowsThroughputAggregate", "State flows throughput (aggregate)", PlFamily.V, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/plview/retrieve/stateflows_throughputaggregates", PlPathParam.None, false, PlEnvelope.FlatArray,
        PlReportDateBasis.PayloadCarried, null,
        "arm.StateFlowsThroughputAggregate", "arm.StateFlowsThroughputAggregateTvp", "arm.usp_BulkMergeStateFlowsThroughputAggregate");

    public static readonly PlEndpointDescriptor SupplyAndDemand = new(
        "SupplyAndDemand", "Supply & demand (markets history)", PlFamily.P, PlArchetype.GoForwardSnapshot, 0,
        "cs/v1/pointlogic/supplyDemand/marketsHistory", PlPathParam.None, false, PlEnvelope.Wrapper,
        PlReportDateBasis.PayloadCarried, null,
        "arm.SupplyAndDemand", "arm.SupplyAndDemandTvp", "arm.usp_BulkMergeSupplyAndDemand");

    // ------------------------------------------------------------------ TIER 1 — discovery-fed lookups (C)
    public static readonly PlEndpointDescriptor County = new(
        "County", "County lookup by state", PlFamily.P, PlArchetype.DiscoveryLookup, 1,
        "cs/v1/pointlogic/lookup_county/{id}", PlPathParam.StateId, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, "State",
        "arm.County", "arm.CountyTvp", "arm.usp_BulkMergeCounty");

    public static readonly PlEndpointDescriptor Facility = new(
        "Facility", "Facility lookup by point type", PlFamily.P, PlArchetype.DiscoveryLookup, 1,
        "cs/v1/pointlogic/lookup_facility/{id}", PlPathParam.PointTypeId, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, "PointType",
        "arm.Facility", "arm.FacilityTvp", "arm.usp_BulkMergeFacility");

    public static readonly PlEndpointDescriptor Subregion = new(
        "Subregion", "Sub-region lookup by region", PlFamily.P, PlArchetype.DiscoveryLookup, 1,
        "cs/v1/pointlogic/lookup_subregion/{id}", PlPathParam.RegionId, false, PlEnvelope.FlatArray,
        PlReportDateBasis.None, "Region",
        "arm.Subregion", "arm.SubregionTvp", "arm.usp_BulkMergeSubregion");

    // ------------------------------------------------------------------ TIER 2 — parametrized facts (D, E)
    public static readonly PlEndpointDescriptor SupplyAndDemandByRegion = new(
        "SupplyAndDemandByRegion", "Supply & demand by region (dated)", PlFamily.P, PlArchetype.DiscoveryDatedFact, 2,
        "cs/v1/pointlogic/supplyDemand/region/{id}", PlPathParam.RegionId, true, PlEnvelope.Wrapper,
        PlReportDateBasis.ParamInjected, "Region",
        "arm.SupplyAndDemandByRegion", "arm.SupplyAndDemandByRegionTvp", "arm.usp_BulkMergeSupplyAndDemandByRegion");

    // NOTE: reuses the SAME /region/{id} path with a SUB-region id (the distinct /subregion/ path 404s).
    public static readonly PlEndpointDescriptor SupplyAndDemandBySubRegion = new(
        "SupplyAndDemandBySubRegion", "Supply & demand by sub-region (dated)", PlFamily.P, PlArchetype.DiscoveryDatedFact, 2,
        "cs/v1/pointlogic/supplyDemand/region/{id}", PlPathParam.SubRegionId, true, PlEnvelope.Wrapper,
        PlReportDateBasis.ParamInjected, "Subregion",
        "arm.SupplyAndDemandBySubRegion", "arm.SupplyAndDemandBySubRegionTvp", "arm.usp_BulkMergeSupplyAndDemandBySubRegion");

    public static readonly PlEndpointDescriptor PointVolume = new(
        "PointVolume", "Point volume history (batched)", PlFamily.P, PlArchetype.BatchedFact, 2,
        "cs/v1/pointlogic/volumeHistory/point", PlPathParam.PointBatch, false, PlEnvelope.Wrapper,
        PlReportDateBasis.None, "Point",
        "arm.PointVolume", "arm.PointVolumeTvp", "arm.usp_BulkMergePointVolume");

    /// <summary>All 25 descriptors in registration order (Tier 0 → 1 → 2).</summary>
    public static readonly PlEndpointDescriptor[] All =
    {
        // Tier 0 — 9 dimensions (A) + 10 fact snapshots (B)
        Region, State, PointStatus, PointType, PipelineNoticeCategory, Pipeline, Point, PointMetadata, PipelineNoticeSearch,
        DemandForecastRegion, DemandForecastUsLower48, GasProductionProducingArea, MarketBalancesUsLower48,
        ModeledDemandRegionType, PipelineFlowThroughput, UsImportsExportsByPointsAggregate, UsSampleStorageFacility,
        StateFlowsThroughputAggregate, SupplyAndDemand,
        // Tier 1 — 3 discovery-fed lookups (C)
        County, Facility, Subregion,
        // Tier 2 — 2 dated facts (D) + 1 batched fact (E)
        SupplyAndDemandByRegion, SupplyAndDemandBySubRegion, PointVolume
    };

    /// <summary>The 25 endpoint ids (default <see cref="IHSPointLogicSettings.EnabledEndpoints"/>).</summary>
    public static readonly string[] AllIds = All.Select(d => d.EndpointId).ToArray();

    /// <summary>
    /// Registry invariants (design §2.2). Asserted once at first static access (mirroring
    /// <c>CwgDescriptors</c>' static-ctor check) so a mis-wired descriptor fails at startup rather
    /// than mis-building a request later. (Static field initializers run before this body, so
    /// <see cref="All"/> is fully populated.)
    /// </summary>
    static PlDescriptors()
    {
        if (All.Length != 25)
            throw new InvalidOperationException($"IHSPointLogic registry must have exactly 25 descriptors, found {All.Length}.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in All)
        {
            if (!ids.Add(d.EndpointId))
                throw new InvalidOperationException($"IHSPointLogic descriptor '{d.EndpointId}': duplicate EndpointId.");

            var hasIdToken = d.PathTemplate.Contains("{id}", StringComparison.Ordinal);
            var wantsIdParam = d.PathParam is PlPathParam.StateId or PlPathParam.PointTypeId
                or PlPathParam.RegionId or PlPathParam.SubRegionId;

            // {id} in the template <=> the path carries a substituted id param (PointBatch/None do not).
            if (hasIdToken != wantsIdParam)
                throw new InvalidOperationException(
                    $"IHSPointLogic descriptor '{d.EndpointId}': PathTemplate '{{id}}' presence ({hasIdToken}) must match a substituted id PathParam ({wantsIdParam}).");

            switch (d.Archetype)
            {
                case PlArchetype.LatestLookup:
                case PlArchetype.GoForwardSnapshot:
                    Require(d, d.Tier == 0, "Tier must be 0");
                    Require(d, d.PathParam == PlPathParam.None, "PathParam must be None");
                    Require(d, d.RefProvider is null, "RefProvider must be null");
                    Require(d, !d.UsesReportDate, "UsesReportDate must be false");
                    break;

                case PlArchetype.DiscoveryLookup:
                    Require(d, d.Tier == 1, "Tier must be 1");
                    Require(d, hasIdToken, "PathTemplate must contain {id}");
                    Require(d, d.PathParam is PlPathParam.StateId or PlPathParam.PointTypeId or PlPathParam.RegionId,
                        "PathParam must be StateId|PointTypeId|RegionId");
                    Require(d, d.RefProvider is not null, "RefProvider must be set");
                    Require(d, !d.UsesReportDate, "UsesReportDate must be false");
                    break;

                case PlArchetype.DiscoveryDatedFact:
                    Require(d, d.Tier == 2, "Tier must be 2");
                    Require(d, hasIdToken, "PathTemplate must contain {id}");
                    Require(d, d.PathParam is PlPathParam.RegionId or PlPathParam.SubRegionId,
                        "PathParam must be RegionId|SubRegionId");
                    Require(d, d.UsesReportDate, "UsesReportDate must be true");
                    Require(d, d.RefProvider is not null, "RefProvider must be set");
                    break;

                case PlArchetype.BatchedFact:
                    Require(d, d.Tier == 2, "Tier must be 2");
                    Require(d, d.PathParam == PlPathParam.PointBatch, "PathParam must be PointBatch");
                    Require(d, d.RefProvider == "Point", "RefProvider must be 'Point'");
                    break;
            }

            // StampUtcRunDate only on the two demand-forecast endpoints.
            var isStamp = d.ReportDateBasis == PlReportDateBasis.StampUtcRunDate;
            var isDemandForecast = d.EndpointId is "DemandForecastRegion" or "DemandForecastUsLower48";
            if (isStamp != isDemandForecast)
                throw new InvalidOperationException(
                    $"IHSPointLogic descriptor '{d.EndpointId}': ReportDateBasis=StampUtcRunDate is only valid on the demand-forecast endpoints.");
        }
    }

    private static void Require(PlEndpointDescriptor d, bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"IHSPointLogic descriptor '{d.EndpointId}' ({d.Archetype}): {message}.");
    }
}
