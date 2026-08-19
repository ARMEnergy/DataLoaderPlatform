using System.Text.Json;

namespace DataLoader.IHSPointLogic;

// =============================================================================
// Target rows — the shared source reader maps each accumulated JSON element to one
// of these via the per-endpoint row factory (the ONLY typed mapping code, design
// §7). Each row carries only its FileLogId (stamped after the FileLog upsert) + its
// own key/data columns; property order documents (and each sink's BuildTable
// mirrors) the TVP column contract in sql/IHSPointLogic/002 — FileLogId first. A
// factory returns null to DROP an element (a required NOT NULL / key cell failed to
// parse, or a required injected field is missing).
// =============================================================================

/// <summary>A fact/dimension row whose parent <c>arm.FileLog</c> id is stamped by the source reader.</summary>
public interface IPlFactRow
{
    int FileLogId { get; set; }
}

// ============================================================================
// TIER 0 — dimensions (archetype A)
// ============================================================================

/// <summary>arm.Region — key (RegionId). TVP: FileLogId, RegionId, Name.</summary>
public sealed class RegionRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int RegionId { get; init; }
    public required string Name { get; init; }

    public static RegionRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new RegionRow { RegionId = id, Name = name };
    }
}

/// <summary>arm.State — key (StateId). TVP: FileLogId, StateId, Name.</summary>
public sealed class StateRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int StateId { get; init; }
    public required string Name { get; init; }

    public static StateRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new StateRow { StateId = id, Name = name };
    }
}

/// <summary>arm.PointStatus — key (PointStatusId). TVP: FileLogId, PointStatusId, Name.</summary>
public sealed class PointStatusRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PointStatusId { get; init; }
    public required string Name { get; init; }

    public static PointStatusRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new PointStatusRow { PointStatusId = id, Name = name };
    }
}

/// <summary>arm.PointType — key (PointTypeId). TVP: FileLogId, PointTypeId, Name.</summary>
public sealed class PointTypeRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PointTypeId { get; init; }
    public required string Name { get; init; }

    public static PointTypeRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new PointTypeRow { PointTypeId = id, Name = name };
    }
}

/// <summary>arm.PipelineNoticeCategory — key (PipelineNoticeCategoryId). TVP: FileLogId, PipelineNoticeCategoryId, Name.</summary>
public sealed class PipelineNoticeCategoryRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PipelineNoticeCategoryId { get; init; }
    public required string Name { get; init; }

    public static PipelineNoticeCategoryRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new PipelineNoticeCategoryRow { PipelineNoticeCategoryId = id, Name = name };
    }
}

/// <summary>arm.Pipeline — key (PipelineId). TVP: FileLogId, PipelineId, Name, LegacyName.</summary>
public sealed class PipelineRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PipelineId { get; init; }
    public required string Name { get; init; }
    public string? LegacyName { get; init; }

    public static PipelineRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new PipelineRow { PipelineId = id, Name = name, LegacyName = PlParse.String(e, "legacyname") };
    }
}

/// <summary>arm.Point — key (PointId). TVP: FileLogId, PointId, Name, PipelineId, PointTypeId, PointStatusId.</summary>
public sealed class PointRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PointId { get; init; }
    public required string Name { get; init; }
    public int? PipelineId { get; init; }
    public int? PointTypeId { get; init; }
    public int? PointStatusId { get; init; }

    public static PointRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int id) return null;
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new PointRow
        {
            PointId = id,
            Name = name,
            PipelineId = PlParse.Int(e, "pipelineid_0"), // note the verified `_0` suffix
            PointTypeId = PlParse.Int(e, "pointtypeid"),
            PointStatusId = PlParse.Int(e, "pointstatusid")
        };
    }
}

/// <summary>
/// arm.PointMetadata — key (PointId), 22 business cols. TVP: FileLogId, PointId, PointLciId, Drn,
/// PointName, PointTypeId, PointType, StateId, State, County, Region, PipelineId, PipelineDisplayName,
/// PointIsActive, DesignCapacity, FlowDirectionId, FlowDirection, DisplayName, LocProp, PointLatitude,
/// PointLongitude, CountyId, RegionId.
/// </summary>
public sealed class PointMetadataRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PointId { get; init; }
    public string? PointLciId { get; init; }
    public string? Drn { get; init; }
    public required string PointName { get; init; }
    public int? PointTypeId { get; init; }
    public string? PointType { get; init; }
    public int? StateId { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
    public string? Region { get; init; }
    public int? PipelineId { get; init; }
    public string? PipelineDisplayName { get; init; }
    public bool? PointIsActive { get; init; }
    public decimal? DesignCapacity { get; init; }
    public int? FlowDirectionId { get; init; }
    public string? FlowDirection { get; init; }
    public string? DisplayName { get; init; }
    public string? LocProp { get; init; }
    public decimal? PointLatitude { get; init; }
    public decimal? PointLongitude { get; init; }
    public int? CountyId { get; init; }
    public int? RegionId { get; init; }

    public static PointMetadataRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "pointid") is not int pointId) return null;
        var pointName = PlParse.String(e, "pointname");
        if (pointName is null) return null;
        return new PointMetadataRow
        {
            PointId = pointId,
            PointLciId = PlParse.String(e, "pointlciid"),
            Drn = PlParse.String(e, "drn"),
            PointName = pointName,
            PointTypeId = PlParse.Int(e, "pointtypeid"),
            PointType = PlParse.String(e, "pointtype"),
            StateId = PlParse.Int(e, "stateid"),
            State = PlParse.String(e, "state"),
            County = PlParse.String(e, "county"),
            Region = PlParse.String(e, "region"),
            PipelineId = PlParse.Int(e, "pipelineid"),
            PipelineDisplayName = PlParse.String(e, "pipelinedisplayname"),
            PointIsActive = PlParse.Bit(e, "pointisactive"),
            DesignCapacity = PlParse.Decimal(e, "designcapacity"), // JSON string-number
            FlowDirectionId = PlParse.Int(e, "flowdirectionid"),
            FlowDirection = PlParse.String(e, "flowdirection"),
            DisplayName = PlParse.String(e, "displayname"),
            LocProp = PlParse.String(e, "locprop"),
            PointLatitude = PlParse.Decimal(e, "pointlatitude"),
            PointLongitude = PlParse.Decimal(e, "pointlongitude"),
            CountyId = PlParse.Int(e, "countyid"),
            RegionId = PlParse.Int(e, "regionid")
        };
    }
}

/// <summary>
/// arm.PipelineNoticeSearch — key (Id). TVP: FileLogId, Id, Subject, Format, PostedDate, ExternalId,
/// CategoryId, IsCritical, ContentLink, PipelineId, EffectiveDate, EndDate.
/// </summary>
public sealed class PipelineNoticeSearchRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required long Id { get; init; }
    public string? Subject { get; init; }
    public string? Format { get; init; }
    public DateTimeOffset? PostedDate { get; init; }
    public string? ExternalId { get; init; }
    public int? CategoryId { get; init; }
    public bool? IsCritical { get; init; }
    public string? ContentLink { get; init; }
    public int? PipelineId { get; init; }
    public DateTimeOffset? EffectiveDate { get; init; }
    public DateTimeOffset? EndDate { get; init; }

    public static PipelineNoticeSearchRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Long(e, "id") is not long id) return null;
        return new PipelineNoticeSearchRow
        {
            Id = id,
            Subject = PlParse.String(e, "subject"),
            Format = PlParse.String(e, "format"),
            PostedDate = PlParse.DateTimeOffset(e, "posteddate"),
            ExternalId = PlParse.String(e, "externalid"),
            CategoryId = PlParse.Int(e, "categoryid"),
            IsCritical = PlParse.Bit(e, "iscritical"),
            ContentLink = PlParse.String(e, "contentlink"),
            PipelineId = PlParse.Int(e, "pipelineid"),
            EffectiveDate = PlParse.DateTimeOffset(e, "effectivedate"),
            EndDate = PlParse.DateTimeOffset(e, "enddate")
        };
    }
}

// ============================================================================
// TIER 0 — fact snapshots (archetype B)
// ============================================================================

/// <summary>
/// arm.DemandForecastRegion — key (ForecastDate, Date, Region, Subregion). ForecastDate is STAMPED
/// from the run's UTC date (unit.ReportDate). TVP: FileLogId, ForecastDate, Date, Region, Subregion,
/// DepartureFromNormalF, NormalTemperatureF, TotalConsumption, Power, Industrial, ResCom, DepartureFromNormal.
/// </summary>
public sealed class DemandForecastRegionRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ForecastDate { get; init; }
    public required DateOnly Date { get; init; }
    public required string Region { get; init; }
    public required string Subregion { get; init; }
    public decimal? DepartureFromNormalF { get; init; }
    public decimal? NormalTemperatureF { get; init; }
    public decimal? TotalConsumption { get; init; }
    public decimal? Power { get; init; }
    public decimal? Industrial { get; init; }
    public decimal? ResCom { get; init; }
    public decimal? DepartureFromNormal { get; init; }

    public static DemandForecastRegionRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (unit.ReportDate is not DateOnly forecastDate) return null; // stamped UTC run date
        if (PlParse.Date(e, "date") is not DateOnly date) return null;
        var region = PlParse.String(e, "region");
        var subregion = PlParse.String(e, "subregion");
        if (region is null || subregion is null) return null;
        return new DemandForecastRegionRow
        {
            ForecastDate = forecastDate,
            Date = date,
            Region = region,
            Subregion = subregion,
            DepartureFromNormalF = PlParse.Decimal(e, "departurefromnormalf"),
            NormalTemperatureF = PlParse.Decimal(e, "normaltemperaturef"),
            TotalConsumption = PlParse.Decimal(e, "totalconsumption"),
            Power = PlParse.Decimal(e, "power"),
            Industrial = PlParse.Decimal(e, "industrial"),
            ResCom = PlParse.Decimal(e, "rescom"),
            DepartureFromNormal = PlParse.Decimal(e, "departurefromnormal")
        };
    }
}

/// <summary>
/// arm.DemandForecastUsLower48 — key (ForecastDate, Date, Region). ForecastDate STAMPED (unit.ReportDate);
/// Date from the long name `pointreadingaggregate_endingdate`; Region from `regionname`. TVP:
/// FileLogId, ForecastDate, Date, Region, Power, Industrial, ResidentialCommercial, Subtotal.
/// </summary>
public sealed class DemandForecastUsLower48Row : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ForecastDate { get; init; }
    public required DateOnly Date { get; init; }
    public required string Region { get; init; }
    public decimal? Power { get; init; }
    public decimal? Industrial { get; init; }
    public decimal? ResidentialCommercial { get; init; }
    public decimal? Subtotal { get; init; }

    public static DemandForecastUsLower48Row? From(JsonElement e, PlWorkUnit unit)
    {
        if (unit.ReportDate is not DateOnly forecastDate) return null; // stamped UTC run date
        if (PlParse.Date(e, "pointreadingaggregate_endingdate") is not DateOnly date) return null;
        var region = PlParse.String(e, "regionname");
        if (region is null) return null;
        return new DemandForecastUsLower48Row
        {
            ForecastDate = forecastDate,
            Date = date,
            Region = region,
            Power = PlParse.Decimal(e, "power"),
            Industrial = PlParse.Decimal(e, "industrial"),
            ResidentialCommercial = PlParse.Decimal(e, "residentialcommercial"),
            Subtotal = PlParse.Decimal(e, "subtotal")
        };
    }
}

/// <summary>
/// arm.GasProductionProducingArea — key (ReportedDate, ReferenceDate, Region, ProducingArea, State).
/// TVP: FileLogId, ReportedDate, ReferenceDate, Region, ProducingArea, State, DryFactoredValue, WellheadValue.
/// </summary>
public sealed class GasProductionProducingAreaRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateTime ReportedDate { get; init; } // yyyy-MM-dd HH:mm
    public required DateOnly ReferenceDate { get; init; }
    public required string Region { get; init; }
    public required string ProducingArea { get; init; }
    public required string State { get; init; }
    public decimal? DryFactoredValue { get; init; }
    public decimal? WellheadValue { get; init; }

    public static GasProductionProducingAreaRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.DateTime2(e, "reporteddate") is not DateTime reportedDate) return null;
        if (PlParse.Date(e, "referencedate") is not DateOnly referenceDate) return null;
        var region = PlParse.String(e, "region");
        var producingArea = PlParse.String(e, "producingarea");
        var state = PlParse.String(e, "state");
        if (region is null || producingArea is null || state is null) return null;
        return new GasProductionProducingAreaRow
        {
            ReportedDate = reportedDate,
            ReferenceDate = referenceDate,
            Region = region,
            ProducingArea = producingArea,
            State = state,
            DryFactoredValue = PlParse.Decimal(e, "dryfactoredvalue"),
            WellheadValue = PlParse.Decimal(e, "wellheadvalue")
        };
    }
}

/// <summary>
/// arm.MarketBalancesUsLower48 — key (TimePeriod), 16 measures. TVP: FileLogId, TimePeriod, Wellhead,
/// ProductionLoss, DryGas, CanadaImports, LngSendout, TotalSupply, Power, Industrial,
/// ResidentialCommercial, Subtotal, MexicoExports, LngFeedGas, PipeLoss, TotalDemand, Storage, BalancingItem.
/// </summary>
public sealed class MarketBalancesUsLower48Row : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly TimePeriod { get; init; }
    public decimal? Wellhead { get; init; }
    public decimal? ProductionLoss { get; init; }
    public decimal? DryGas { get; init; }
    public decimal? CanadaImports { get; init; }
    public decimal? LngSendout { get; init; }
    public decimal? TotalSupply { get; init; }
    public decimal? Power { get; init; }
    public decimal? Industrial { get; init; }
    public decimal? ResidentialCommercial { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? MexicoExports { get; init; }
    public decimal? LngFeedGas { get; init; }
    public decimal? PipeLoss { get; init; }
    public decimal? TotalDemand { get; init; }
    public decimal? Storage { get; init; }
    public decimal? BalancingItem { get; init; }

    public static MarketBalancesUsLower48Row? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "timeperiod") is not DateOnly timePeriod) return null;
        return new MarketBalancesUsLower48Row
        {
            TimePeriod = timePeriod,
            Wellhead = PlParse.Decimal(e, "wellhead"),
            ProductionLoss = PlParse.Decimal(e, "productionloss"),
            DryGas = PlParse.Decimal(e, "drygas"),
            CanadaImports = PlParse.Decimal(e, "canadaimports"),
            LngSendout = PlParse.Decimal(e, "lngsendout"),
            TotalSupply = PlParse.Decimal(e, "totalsupply"),
            Power = PlParse.Decimal(e, "power"),
            Industrial = PlParse.Decimal(e, "industrial"),
            ResidentialCommercial = PlParse.Decimal(e, "residentialcommercial"),
            Subtotal = PlParse.Decimal(e, "subtotal"),
            MexicoExports = PlParse.Decimal(e, "mexicoexports"),
            LngFeedGas = PlParse.Decimal(e, "lngfeedgas"),
            PipeLoss = PlParse.Decimal(e, "pipeloss"),
            TotalDemand = PlParse.Decimal(e, "totaldemand"),
            Storage = PlParse.Decimal(e, "storage"),
            BalancingItem = PlParse.Decimal(e, "balancingitem")
        };
    }
}

/// <summary>arm.ModeledDemandRegionType — key (ReferenceDate, PLEProductName, RegionName). TVP: FileLogId, ReferenceDate, PLEProductName, RegionName, Volume.</summary>
public sealed class ModeledDemandRegionTypeRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ReferenceDate { get; init; }
    public required string PLEProductName { get; init; }
    public required string RegionName { get; init; }
    public decimal? Volume { get; init; }

    public static ModeledDemandRegionTypeRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "referencedate") is not DateOnly referenceDate) return null;
        var product = PlParse.String(e, "pleproductname");
        var region = PlParse.String(e, "regionname");
        if (product is null || region is null) return null;
        return new ModeledDemandRegionTypeRow
        {
            ReferenceDate = referenceDate,
            PLEProductName = product,
            RegionName = region,
            Volume = PlParse.Decimal(e, "volume")
        };
    }
}

/// <summary>
/// arm.PipelineFlowThroughput — key (FlowDate, Region, Pipeline, Throughput, FlowType). `reporteddate`
/// is EXCLUDED (not persisted). Throughput is a LABEL, not a measure. TVP: FileLogId, FlowDate, Region,
/// Pipeline, Throughput, FlowType, Volume.
/// </summary>
public sealed class PipelineFlowThroughputRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly FlowDate { get; init; } // MM/dd/yyyy
    public required string Region { get; init; }
    public required string Pipeline { get; init; }
    public required string Throughput { get; init; }
    public required string FlowType { get; init; }
    public decimal? Volume { get; init; }

    public static PipelineFlowThroughputRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "flowdate") is not DateOnly flowDate) return null;
        var region = PlParse.String(e, "region");
        var pipeline = PlParse.String(e, "pipeline");
        var throughput = PlParse.String(e, "throughput");
        var flowType = PlParse.String(e, "flowtype");
        if (region is null || pipeline is null || throughput is null || flowType is null) return null;
        return new PipelineFlowThroughputRow
        {
            FlowDate = flowDate,
            Region = region,
            Pipeline = pipeline,
            Throughput = throughput,
            FlowType = flowType,
            Volume = PlParse.Decimal(e, "volume")
        };
    }
}

/// <summary>
/// arm.UsImportsExportsByPointsAggregate — key (RunDate, FlowDate, PointName, PipelineName, LedgerSide).
/// TVP: FileLogId, RunDate, FlowDate, PointName, PipelineName, LedgerSide, Volume, State, County, Type,
/// PointGroupName, DistrictName.
/// </summary>
public sealed class UsImportsExportsByPointsAggregateRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly RunDate { get; init; }
    public required DateOnly FlowDate { get; init; }
    public required string PointName { get; init; }
    public required string PipelineName { get; init; }
    public required string LedgerSide { get; init; }
    public decimal? Volume { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
    public string? Type { get; init; }
    public string? PointGroupName { get; init; }
    public string? DistrictName { get; init; }

    public static UsImportsExportsByPointsAggregateRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "rundate") is not DateOnly runDate) return null;
        if (PlParse.Date(e, "flowdate") is not DateOnly flowDate) return null;
        var pointName = PlParse.String(e, "pointname");
        var pipelineName = PlParse.String(e, "pipelinename");
        var ledgerSide = PlParse.String(e, "ledgerside");
        if (pointName is null || pipelineName is null || ledgerSide is null) return null;
        return new UsImportsExportsByPointsAggregateRow
        {
            RunDate = runDate,
            FlowDate = flowDate,
            PointName = pointName,
            PipelineName = pipelineName,
            LedgerSide = ledgerSide,
            Volume = PlParse.Decimal(e, "volume"),
            State = PlParse.String(e, "state"),
            County = PlParse.String(e, "county"),
            Type = PlParse.String(e, "type"),
            PointGroupName = PlParse.String(e, "pointgroupname"),
            DistrictName = PlParse.String(e, "districtname")
        };
    }
}

/// <summary>
/// arm.UsSampleStorageFacility — key (ReportDate, FlowDate, Name, EiaRegion, State, FieldType).
/// FieldType from `field_type`. TVP: FileLogId, ReportDate, FlowDate, Name, EiaRegion, State, FieldType, Volume.
/// </summary>
public sealed class UsSampleStorageFacilityRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ReportDate { get; init; }
    public required DateOnly FlowDate { get; init; }
    public required string Name { get; init; }
    public required string EiaRegion { get; init; }
    public required string State { get; init; }
    public required string FieldType { get; init; }
    public decimal? Volume { get; init; }

    public static UsSampleStorageFacilityRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "reportdate") is not DateOnly reportDate) return null;
        if (PlParse.Date(e, "flowdate") is not DateOnly flowDate) return null;
        var name = PlParse.String(e, "name");
        var eiaRegion = PlParse.String(e, "eiaregion");
        var state = PlParse.String(e, "state");
        var fieldType = PlParse.String(e, "field_type");
        if (name is null || eiaRegion is null || state is null || fieldType is null) return null;
        return new UsSampleStorageFacilityRow
        {
            ReportDate = reportDate,
            FlowDate = flowDate,
            Name = name,
            EiaRegion = eiaRegion,
            State = state,
            FieldType = fieldType,
            Volume = PlParse.Decimal(e, "volume")
        };
    }
}

/// <summary>arm.StateFlowsThroughputAggregate — key (FlowDate, Region, FromState, ToState, FlowType). TVP: FileLogId, FlowDate, Region, FromState, ToState, FlowType, Volume.</summary>
public sealed class StateFlowsThroughputAggregateRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly FlowDate { get; init; } // MM/dd/yyyy
    public required string Region { get; init; }
    public required string FromState { get; init; }
    public required string ToState { get; init; }
    public required string FlowType { get; init; }
    public decimal? Volume { get; init; }

    public static StateFlowsThroughputAggregateRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "flowdate") is not DateOnly flowDate) return null;
        var region = PlParse.String(e, "region");
        var fromState = PlParse.String(e, "fromstate");
        var toState = PlParse.String(e, "tostate");
        var flowType = PlParse.String(e, "flowtype");
        if (region is null || fromState is null || toState is null || flowType is null) return null;
        return new StateFlowsThroughputAggregateRow
        {
            FlowDate = flowDate,
            Region = region,
            FromState = fromState,
            ToState = toState,
            FlowType = flowType,
            Volume = PlParse.Decimal(e, "volume")
        };
    }
}

/// <summary>
/// arm.SupplyAndDemand (marketsHistory) — key (Date), the same 16 measures as MarketBalances. TVP:
/// FileLogId, Date, Wellhead, ProductionLoss, DryGas, CanadaImports, LngSendout, TotalSupply, Power,
/// Industrial, ResidentialCommercial, Subtotal, MexicoExports, LngFeedGas, PipeLoss, TotalDemand, Storage, BalancingItem.
/// </summary>
public sealed class SupplyAndDemandRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly Date { get; init; }
    public decimal? Wellhead { get; init; }
    public decimal? ProductionLoss { get; init; }
    public decimal? DryGas { get; init; }
    public decimal? CanadaImports { get; init; }
    public decimal? LngSendout { get; init; }
    public decimal? TotalSupply { get; init; }
    public decimal? Power { get; init; }
    public decimal? Industrial { get; init; }
    public decimal? ResidentialCommercial { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? MexicoExports { get; init; }
    public decimal? LngFeedGas { get; init; }
    public decimal? PipeLoss { get; init; }
    public decimal? TotalDemand { get; init; }
    public decimal? Storage { get; init; }
    public decimal? BalancingItem { get; init; }

    public static SupplyAndDemandRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Date(e, "date") is not DateOnly date) return null;
        return new SupplyAndDemandRow
        {
            Date = date,
            Wellhead = PlParse.Decimal(e, "wellhead"),
            ProductionLoss = PlParse.Decimal(e, "productionloss"),
            DryGas = PlParse.Decimal(e, "drygas"),
            CanadaImports = PlParse.Decimal(e, "canadaimports"),
            LngSendout = PlParse.Decimal(e, "lngsendout"),
            TotalSupply = PlParse.Decimal(e, "totalsupply"),
            Power = PlParse.Decimal(e, "power"),
            Industrial = PlParse.Decimal(e, "industrial"),
            ResidentialCommercial = PlParse.Decimal(e, "residentialcommercial"),
            Subtotal = PlParse.Decimal(e, "subtotal"),
            MexicoExports = PlParse.Decimal(e, "mexicoexports"),
            LngFeedGas = PlParse.Decimal(e, "lngfeedgas"),
            PipeLoss = PlParse.Decimal(e, "pipeloss"),
            TotalDemand = PlParse.Decimal(e, "totaldemand"),
            Storage = PlParse.Decimal(e, "storage"),
            BalancingItem = PlParse.Decimal(e, "balancingitem")
        };
    }
}

// ============================================================================
// TIER 1 — discovery-fed lookups (archetype C) — parent id injected from the path
// ============================================================================

/// <summary>arm.County — key (CountyId, StateId); StateId injected from the URL. TVP: FileLogId, CountyId, StateId, Name.</summary>
public sealed class CountyRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int CountyId { get; init; }
    public required int StateId { get; init; }
    public required string Name { get; init; }

    public static CountyRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int countyId) return null;
        if (unit.ParamId is not int stateId) return null; // injected from the path
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new CountyRow { CountyId = countyId, StateId = stateId, Name = name };
    }
}

/// <summary>
/// arm.Facility — key (FacilityId, PointTypeId); PointTypeId injected from the URL (= facilitytypeid).
/// The redundant `facilitytypeid` body echo is NOT persisted (dropped per the TVP). TVP: FileLogId,
/// FacilityId, PointTypeId, Name.
/// </summary>
public sealed class FacilityRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int FacilityId { get; init; }
    public required int PointTypeId { get; init; }
    public required string Name { get; init; }

    public static FacilityRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int facilityId) return null;
        if (unit.ParamId is not int pointTypeId) return null; // injected from the path
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new FacilityRow { FacilityId = facilityId, PointTypeId = pointTypeId, Name = name };
    }
}

/// <summary>arm.Subregion — key (SubRegionId, RegionId); RegionId injected from the URL. TVP: FileLogId, SubRegionId, RegionId, Name.</summary>
public sealed class SubregionRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int SubRegionId { get; init; }
    public required int RegionId { get; init; }
    public required string Name { get; init; }

    public static SubregionRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int subRegionId) return null;
        if (unit.ParamId is not int regionId) return null; // injected from the path
        var name = PlParse.String(e, "name");
        if (name is null) return null;
        return new SubregionRow { SubRegionId = subRegionId, RegionId = regionId, Name = name };
    }
}

// ============================================================================
// TIER 2 — parametrized facts (archetypes D, E)
// ============================================================================

/// <summary>
/// arm.SupplyAndDemandByRegion — key (RegionId, Date, Product); RegionId from the path, Date from the
/// reportDate param (both injected). TVP: FileLogId, RegionId, Date, Product, VolumeMmcfd.
/// </summary>
public sealed class SupplyAndDemandByRegionRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int RegionId { get; init; }
    public required DateOnly Date { get; init; }
    public required string Product { get; init; }
    public decimal? VolumeMmcfd { get; init; }

    public static SupplyAndDemandByRegionRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (unit.ParamId is not int regionId) return null;   // injected from the path
        if (unit.ReportDate is not DateOnly date) return null; // injected from the reportDate param
        var product = PlParse.String(e, "product");
        if (product is null) return null;
        return new SupplyAndDemandByRegionRow
        {
            RegionId = regionId,
            Date = date,
            Product = product,
            VolumeMmcfd = PlParse.Decimal(e, "volume_mmcfd")
        };
    }
}

/// <summary>
/// arm.SupplyAndDemandBySubRegion — key (SubRegionId, RegionId, Date, Product); SubRegionId from the
/// path, RegionId from the SubRegionId→RegionId map (unit.SecondaryId), Date from the reportDate param.
/// TVP: FileLogId, SubRegionId, RegionId, Date, Product, VolumeMmcfd.
/// </summary>
public sealed class SupplyAndDemandBySubRegionRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int SubRegionId { get; init; }
    public required int RegionId { get; init; }
    public required DateOnly Date { get; init; }
    public required string Product { get; init; }
    public decimal? VolumeMmcfd { get; init; }

    public static SupplyAndDemandBySubRegionRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (unit.ParamId is not int subRegionId) return null;  // injected from the path
        if (unit.SecondaryId is not int regionId) return null; // injected from the map
        if (unit.ReportDate is not DateOnly date) return null; // injected from the reportDate param
        var product = PlParse.String(e, "product");
        if (product is null) return null;
        return new SupplyAndDemandBySubRegionRow
        {
            SubRegionId = subRegionId,
            RegionId = regionId,
            Date = date,
            Product = product,
            VolumeMmcfd = PlParse.Decimal(e, "volume_mmcfd")
        };
    }
}

/// <summary>
/// arm.PointVolume — key (PointId, Date). `id` is the PointId so a multi-point batch splits cleanly
/// per row (no unit-side attribution). Volume can be negative. TVP: FileLogId, PointId, Date, Volume.
/// </summary>
public sealed class PointVolumeRow : IPlFactRow
{
    public int FileLogId { get; set; }
    public required int PointId { get; init; }
    public required DateOnly Date { get; init; }
    public decimal? Volume { get; init; }

    public static PointVolumeRow? From(JsonElement e, PlWorkUnit unit)
    {
        if (PlParse.Int(e, "id") is not int pointId) return null;
        if (PlParse.Date(e, "date") is not DateOnly date) return null;
        return new PointVolumeRow { PointId = pointId, Date = date, Volume = PlParse.Decimal(e, "volume") };
    }
}
