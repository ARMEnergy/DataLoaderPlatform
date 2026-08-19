using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IHSPointLogic;

// =============================================================================
// Per-endpoint SQL sinks (×25). Each bulk-merges its FileLogId-stamped rows into
// arm.<Table> via arm.usp_BulkMerge<Table>(@Records arm.<Table>Tvp). Column ORDER
// in every BuildTable mirrors sql/IHSPointLogic/002 EXACTLY — FileLogId first (a
// load-bearing contract; the TVP binds BY POSITION). Each de-dups the batch on its
// merge key before building the TVP so an in-batch duplicate cannot break the MERGE.
// Concurrent-MERGE serialization is applied automatically by SqlSinkBase
// (SqlWriteGate); the 25 procs are distinct keys, so different endpoints never
// serialize against each other (design §9).
// =============================================================================

/// <summary>Common base wiring for an IHSPointLogic sink.</summary>
public abstract class PlSqlSinkBase<TRow> : SqlSinkBase<TRow>
{
    private readonly IHSPointLogicSettings _settings;

    protected PlSqlSinkBase(IOptions<IHSPointLogicSettings> settings, ILogger logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override bool ProcedureReturnsRowCount => true;

    /// <summary>DateOnly → midnight DateTime for a TVP DATE column.</summary>
    protected static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}

// -------------------------------------------------------------------- 5 Region
public sealed class RegionSqlSink : PlSqlSinkBase<RegionRow>
{
    public RegionSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<RegionSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeRegion";
    protected override string TableValuedParameterType => "arm.RegionTvp";

    protected override DataTable BuildTable(IReadOnlyList<RegionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("RegionId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => x.RegionId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.RegionId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 6a State
public sealed class StateSqlSink : PlSqlSinkBase<StateRow>
{
    public StateSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<StateSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeState";
    protected override string TableValuedParameterType => "arm.StateTvp";

    protected override DataTable BuildTable(IReadOnlyList<StateRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("StateId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => x.StateId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.StateId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 6b PointStatus
public sealed class PointStatusSqlSink : PlSqlSinkBase<PointStatusRow>
{
    public PointStatusSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PointStatusSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePointStatus";
    protected override string TableValuedParameterType => "arm.PointStatusTvp";

    protected override DataTable BuildTable(IReadOnlyList<PointStatusRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PointStatusId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => x.PointStatusId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.PointStatusId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 6c PointType
public sealed class PointTypeSqlSink : PlSqlSinkBase<PointTypeRow>
{
    public PointTypeSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PointTypeSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePointType";
    protected override string TableValuedParameterType => "arm.PointTypeTvp";

    protected override DataTable BuildTable(IReadOnlyList<PointTypeRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PointTypeId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => x.PointTypeId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.PointTypeId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 6d PipelineNoticeCategory
public sealed class PipelineNoticeCategorySqlSink : PlSqlSinkBase<PipelineNoticeCategoryRow>
{
    public PipelineNoticeCategorySqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PipelineNoticeCategorySqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePipelineNoticeCategory";
    protected override string TableValuedParameterType => "arm.PipelineNoticeCategoryTvp";

    protected override DataTable BuildTable(IReadOnlyList<PipelineNoticeCategoryRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PipelineNoticeCategoryId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => x.PipelineNoticeCategoryId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.PipelineNoticeCategoryId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 6e Pipeline
public sealed class PipelineSqlSink : PlSqlSinkBase<PipelineRow>
{
    public PipelineSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PipelineSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePipeline";
    protected override string TableValuedParameterType => "arm.PipelineTvp";

    protected override DataTable BuildTable(IReadOnlyList<PipelineRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PipelineId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("LegacyName", typeof(string));
        foreach (var r in rows.GroupBy(x => x.PipelineId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.PipelineId, r.Name, DbNullableObj(r.LegacyName));
        return t;
    }
}

// -------------------------------------------------------------------- 7 Point
public sealed class PointSqlSink : PlSqlSinkBase<PointRow>
{
    public PointSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PointSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePoint";
    protected override string TableValuedParameterType => "arm.PointTvp";

    protected override DataTable BuildTable(IReadOnlyList<PointRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PointId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("PipelineId", typeof(int));
        t.Columns.Add("PointTypeId", typeof(int));
        t.Columns.Add("PointStatusId", typeof(int));
        foreach (var r in rows.GroupBy(x => x.PointId).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.PointId, r.Name,
                DbNullable(r.PipelineId), DbNullable(r.PointTypeId), DbNullable(r.PointStatusId));
        return t;
    }
}

// -------------------------------------------------------------------- 12 PointMetadata
public sealed class PointMetadataSqlSink : PlSqlSinkBase<PointMetadataRow>
{
    public PointMetadataSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PointMetadataSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePointMetadata";
    protected override string TableValuedParameterType => "arm.PointMetadataTvp";

    protected override DataTable BuildTable(IReadOnlyList<PointMetadataRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PointId", typeof(int));
        t.Columns.Add("PointLciId", typeof(string));
        t.Columns.Add("Drn", typeof(string));
        t.Columns.Add("PointName", typeof(string));
        t.Columns.Add("PointTypeId", typeof(int));
        t.Columns.Add("PointType", typeof(string));
        t.Columns.Add("StateId", typeof(int));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("County", typeof(string));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("PipelineId", typeof(int));
        t.Columns.Add("PipelineDisplayName", typeof(string));
        t.Columns.Add("PointIsActive", typeof(bool));
        t.Columns.Add("DesignCapacity", typeof(decimal));
        t.Columns.Add("FlowDirectionId", typeof(int));
        t.Columns.Add("FlowDirection", typeof(string));
        t.Columns.Add("DisplayName", typeof(string));
        t.Columns.Add("LocProp", typeof(string));
        t.Columns.Add("PointLatitude", typeof(decimal));
        t.Columns.Add("PointLongitude", typeof(decimal));
        t.Columns.Add("CountyId", typeof(int));
        t.Columns.Add("RegionId", typeof(int));

        foreach (var r in rows.GroupBy(x => x.PointId).Select(g => g.Last()))
            t.Rows.Add(
                r.FileLogId, r.PointId, DbNullableObj(r.PointLciId), DbNullableObj(r.Drn), r.PointName,
                DbNullable(r.PointTypeId), DbNullableObj(r.PointType), DbNullable(r.StateId), DbNullableObj(r.State),
                DbNullableObj(r.County), DbNullableObj(r.Region), DbNullable(r.PipelineId), DbNullableObj(r.PipelineDisplayName),
                DbNullable(r.PointIsActive), DbNullable(r.DesignCapacity), DbNullable(r.FlowDirectionId), DbNullableObj(r.FlowDirection),
                DbNullableObj(r.DisplayName), DbNullableObj(r.LocProp), DbNullable(r.PointLatitude), DbNullable(r.PointLongitude),
                DbNullable(r.CountyId), DbNullable(r.RegionId));
        return t;
    }
}

// -------------------------------------------------------------------- 15 PipelineNoticeSearch
public sealed class PipelineNoticeSearchSqlSink : PlSqlSinkBase<PipelineNoticeSearchRow>
{
    public PipelineNoticeSearchSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PipelineNoticeSearchSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePipelineNoticeSearch";
    protected override string TableValuedParameterType => "arm.PipelineNoticeSearchTvp";

    protected override DataTable BuildTable(IReadOnlyList<PipelineNoticeSearchRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Id", typeof(long));
        t.Columns.Add("Subject", typeof(string));
        t.Columns.Add("Format", typeof(string));
        t.Columns.Add("PostedDate", typeof(DateTimeOffset));
        t.Columns.Add("ExternalId", typeof(string));
        t.Columns.Add("CategoryId", typeof(int));
        t.Columns.Add("IsCritical", typeof(bool));
        t.Columns.Add("ContentLink", typeof(string));
        t.Columns.Add("PipelineId", typeof(int));
        t.Columns.Add("EffectiveDate", typeof(DateTimeOffset));
        t.Columns.Add("EndDate", typeof(DateTimeOffset));

        foreach (var r in rows.GroupBy(x => x.Id).Select(g => g.Last()))
            t.Rows.Add(
                r.FileLogId, r.Id, DbNullableObj(r.Subject), DbNullableObj(r.Format), DbNullable(r.PostedDate),
                DbNullableObj(r.ExternalId), DbNullable(r.CategoryId), DbNullable(r.IsCritical), DbNullableObj(r.ContentLink),
                DbNullable(r.PipelineId), DbNullable(r.EffectiveDate), DbNullable(r.EndDate));
        return t;
    }
}

// -------------------------------------------------------------------- 8 DemandForecastRegion
public sealed class DemandForecastRegionSqlSink : PlSqlSinkBase<DemandForecastRegionRow>
{
    public DemandForecastRegionSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<DemandForecastRegionSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeDemandForecastRegion";
    protected override string TableValuedParameterType => "arm.DemandForecastRegionTvp";

    protected override DataTable BuildTable(IReadOnlyList<DemandForecastRegionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("Subregion", typeof(string));
        t.Columns.Add("DepartureFromNormalF", typeof(decimal));
        t.Columns.Add("NormalTemperatureF", typeof(decimal));
        t.Columns.Add("TotalConsumption", typeof(decimal));
        t.Columns.Add("Power", typeof(decimal));
        t.Columns.Add("Industrial", typeof(decimal));
        t.Columns.Add("ResCom", typeof(decimal));
        t.Columns.Add("DepartureFromNormal", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.ForecastDate, x.Date, x.Region, x.Subregion)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.ForecastDate), D(r.Date), r.Region, r.Subregion,
                DbNullable(r.DepartureFromNormalF), DbNullable(r.NormalTemperatureF), DbNullable(r.TotalConsumption),
                DbNullable(r.Power), DbNullable(r.Industrial), DbNullable(r.ResCom), DbNullable(r.DepartureFromNormal));
        return t;
    }
}

// -------------------------------------------------------------------- 9 DemandForecastUsLower48
public sealed class DemandForecastUsLower48SqlSink : PlSqlSinkBase<DemandForecastUsLower48Row>
{
    public DemandForecastUsLower48SqlSink(IOptions<IHSPointLogicSettings> s, ILogger<DemandForecastUsLower48SqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeDemandForecastUsLower48";
    protected override string TableValuedParameterType => "arm.DemandForecastUsLower48Tvp";

    protected override DataTable BuildTable(IReadOnlyList<DemandForecastUsLower48Row> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("Power", typeof(decimal));
        t.Columns.Add("Industrial", typeof(decimal));
        t.Columns.Add("ResidentialCommercial", typeof(decimal));
        t.Columns.Add("Subtotal", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.ForecastDate, x.Date, x.Region)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.ForecastDate), D(r.Date), r.Region,
                DbNullable(r.Power), DbNullable(r.Industrial), DbNullable(r.ResidentialCommercial), DbNullable(r.Subtotal));
        return t;
    }
}

// -------------------------------------------------------------------- 10 GasProductionProducingArea
public sealed class GasProductionProducingAreaSqlSink : PlSqlSinkBase<GasProductionProducingAreaRow>
{
    public GasProductionProducingAreaSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<GasProductionProducingAreaSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeGasProductionProducingArea";
    protected override string TableValuedParameterType => "arm.GasProductionProducingAreaTvp";

    protected override DataTable BuildTable(IReadOnlyList<GasProductionProducingAreaRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ReportedDate", typeof(DateTime));
        t.Columns.Add("ReferenceDate", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ProducingArea", typeof(string));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("DryFactoredValue", typeof(decimal));
        t.Columns.Add("WellheadValue", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.ReportedDate, x.ReferenceDate, x.Region, x.ProducingArea, x.State)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.ReportedDate, D(r.ReferenceDate), r.Region, r.ProducingArea, r.State,
                DbNullable(r.DryFactoredValue), DbNullable(r.WellheadValue));
        return t;
    }
}

// -------------------------------------------------------------------- 11 MarketBalancesUsLower48
public sealed class MarketBalancesUsLower48SqlSink : PlSqlSinkBase<MarketBalancesUsLower48Row>
{
    public MarketBalancesUsLower48SqlSink(IOptions<IHSPointLogicSettings> s, ILogger<MarketBalancesUsLower48SqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeMarketBalancesUsLower48";
    protected override string TableValuedParameterType => "arm.MarketBalancesUsLower48Tvp";

    protected override DataTable BuildTable(IReadOnlyList<MarketBalancesUsLower48Row> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("TimePeriod", typeof(DateTime));
        t.Columns.Add("Wellhead", typeof(decimal));
        t.Columns.Add("ProductionLoss", typeof(decimal));
        t.Columns.Add("DryGas", typeof(decimal));
        t.Columns.Add("CanadaImports", typeof(decimal));
        t.Columns.Add("LngSendout", typeof(decimal));
        t.Columns.Add("TotalSupply", typeof(decimal));
        t.Columns.Add("Power", typeof(decimal));
        t.Columns.Add("Industrial", typeof(decimal));
        t.Columns.Add("ResidentialCommercial", typeof(decimal));
        t.Columns.Add("Subtotal", typeof(decimal));
        t.Columns.Add("MexicoExports", typeof(decimal));
        t.Columns.Add("LngFeedGas", typeof(decimal));
        t.Columns.Add("PipeLoss", typeof(decimal));
        t.Columns.Add("TotalDemand", typeof(decimal));
        t.Columns.Add("Storage", typeof(decimal));
        t.Columns.Add("BalancingItem", typeof(decimal));

        foreach (var r in rows.GroupBy(x => x.TimePeriod).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.TimePeriod),
                DbNullable(r.Wellhead), DbNullable(r.ProductionLoss), DbNullable(r.DryGas), DbNullable(r.CanadaImports),
                DbNullable(r.LngSendout), DbNullable(r.TotalSupply), DbNullable(r.Power), DbNullable(r.Industrial),
                DbNullable(r.ResidentialCommercial), DbNullable(r.Subtotal), DbNullable(r.MexicoExports), DbNullable(r.LngFeedGas),
                DbNullable(r.PipeLoss), DbNullable(r.TotalDemand), DbNullable(r.Storage), DbNullable(r.BalancingItem));
        return t;
    }
}

// -------------------------------------------------------------------- 13 ModeledDemandRegionType
public sealed class ModeledDemandRegionTypeSqlSink : PlSqlSinkBase<ModeledDemandRegionTypeRow>
{
    public ModeledDemandRegionTypeSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<ModeledDemandRegionTypeSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeModeledDemandRegionType";
    protected override string TableValuedParameterType => "arm.ModeledDemandRegionTypeTvp";

    protected override DataTable BuildTable(IReadOnlyList<ModeledDemandRegionTypeRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ReferenceDate", typeof(DateTime));
        t.Columns.Add("PLEProductName", typeof(string));
        t.Columns.Add("RegionName", typeof(string));
        t.Columns.Add("Volume", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.ReferenceDate, x.PLEProductName, x.RegionName)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.ReferenceDate), r.PLEProductName, r.RegionName, DbNullable(r.Volume));
        return t;
    }
}

// -------------------------------------------------------------------- 14 PipelineFlowThroughput
public sealed class PipelineFlowThroughputSqlSink : PlSqlSinkBase<PipelineFlowThroughputRow>
{
    public PipelineFlowThroughputSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PipelineFlowThroughputSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePipelineFlowThroughput";
    protected override string TableValuedParameterType => "arm.PipelineFlowThroughputTvp";

    protected override DataTable BuildTable(IReadOnlyList<PipelineFlowThroughputRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("FlowDate", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("Pipeline", typeof(string));
        t.Columns.Add("Throughput", typeof(string));
        t.Columns.Add("FlowType", typeof(string));
        t.Columns.Add("Volume", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.FlowDate, x.Region, x.Pipeline, x.Throughput, x.FlowType)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.FlowDate), r.Region, r.Pipeline, r.Throughput, r.FlowType, DbNullable(r.Volume));
        return t;
    }
}

// -------------------------------------------------------------------- 16 UsImportsExportsByPointsAggregate
public sealed class UsImportsExportsByPointsAggregateSqlSink : PlSqlSinkBase<UsImportsExportsByPointsAggregateRow>
{
    public UsImportsExportsByPointsAggregateSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<UsImportsExportsByPointsAggregateSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeUsImportsExportsByPointsAggregate";
    protected override string TableValuedParameterType => "arm.UsImportsExportsByPointsAggregateTvp";

    protected override DataTable BuildTable(IReadOnlyList<UsImportsExportsByPointsAggregateRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("RunDate", typeof(DateTime));
        t.Columns.Add("FlowDate", typeof(DateTime));
        t.Columns.Add("PointName", typeof(string));
        t.Columns.Add("PipelineName", typeof(string));
        t.Columns.Add("LedgerSide", typeof(string));
        t.Columns.Add("Volume", typeof(decimal));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("County", typeof(string));
        t.Columns.Add("Type", typeof(string));
        t.Columns.Add("PointGroupName", typeof(string));
        t.Columns.Add("DistrictName", typeof(string));

        foreach (var r in rows.GroupBy(x => (x.RunDate, x.FlowDate, x.PointName, x.PipelineName, x.LedgerSide)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.RunDate), D(r.FlowDate), r.PointName, r.PipelineName, r.LedgerSide,
                DbNullable(r.Volume), DbNullableObj(r.State), DbNullableObj(r.County), DbNullableObj(r.Type),
                DbNullableObj(r.PointGroupName), DbNullableObj(r.DistrictName));
        return t;
    }
}

// -------------------------------------------------------------------- 17 UsSampleStorageFacility
public sealed class UsSampleStorageFacilitySqlSink : PlSqlSinkBase<UsSampleStorageFacilityRow>
{
    public UsSampleStorageFacilitySqlSink(IOptions<IHSPointLogicSettings> s, ILogger<UsSampleStorageFacilitySqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeUsSampleStorageFacility";
    protected override string TableValuedParameterType => "arm.UsSampleStorageFacilityTvp";

    protected override DataTable BuildTable(IReadOnlyList<UsSampleStorageFacilityRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ReportDate", typeof(DateTime));
        t.Columns.Add("FlowDate", typeof(DateTime));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("EiaRegion", typeof(string));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("FieldType", typeof(string));
        t.Columns.Add("Volume", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.ReportDate, x.FlowDate, x.Name, x.EiaRegion, x.State, x.FieldType)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.ReportDate), D(r.FlowDate), r.Name, r.EiaRegion, r.State, r.FieldType, DbNullable(r.Volume));
        return t;
    }
}

// -------------------------------------------------------------------- 18 StateFlowsThroughputAggregate
public sealed class StateFlowsThroughputAggregateSqlSink : PlSqlSinkBase<StateFlowsThroughputAggregateRow>
{
    public StateFlowsThroughputAggregateSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<StateFlowsThroughputAggregateSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeStateFlowsThroughputAggregate";
    protected override string TableValuedParameterType => "arm.StateFlowsThroughputAggregateTvp";

    protected override DataTable BuildTable(IReadOnlyList<StateFlowsThroughputAggregateRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("FlowDate", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("FromState", typeof(string));
        t.Columns.Add("ToState", typeof(string));
        t.Columns.Add("FlowType", typeof(string));
        t.Columns.Add("Volume", typeof(decimal));

        foreach (var r in rows.GroupBy(x => (x.FlowDate, x.Region, x.FromState, x.ToState, x.FlowType)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.FlowDate), r.Region, r.FromState, r.ToState, r.FlowType, DbNullable(r.Volume));
        return t;
    }
}

// -------------------------------------------------------------------- 19 SupplyAndDemand (marketsHistory)
public sealed class SupplyAndDemandSqlSink : PlSqlSinkBase<SupplyAndDemandRow>
{
    public SupplyAndDemandSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<SupplyAndDemandSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSupplyAndDemand";
    protected override string TableValuedParameterType => "arm.SupplyAndDemandTvp";

    protected override DataTable BuildTable(IReadOnlyList<SupplyAndDemandRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Wellhead", typeof(decimal));
        t.Columns.Add("ProductionLoss", typeof(decimal));
        t.Columns.Add("DryGas", typeof(decimal));
        t.Columns.Add("CanadaImports", typeof(decimal));
        t.Columns.Add("LngSendout", typeof(decimal));
        t.Columns.Add("TotalSupply", typeof(decimal));
        t.Columns.Add("Power", typeof(decimal));
        t.Columns.Add("Industrial", typeof(decimal));
        t.Columns.Add("ResidentialCommercial", typeof(decimal));
        t.Columns.Add("Subtotal", typeof(decimal));
        t.Columns.Add("MexicoExports", typeof(decimal));
        t.Columns.Add("LngFeedGas", typeof(decimal));
        t.Columns.Add("PipeLoss", typeof(decimal));
        t.Columns.Add("TotalDemand", typeof(decimal));
        t.Columns.Add("Storage", typeof(decimal));
        t.Columns.Add("BalancingItem", typeof(decimal));

        foreach (var r in rows.GroupBy(x => x.Date).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, D(r.Date),
                DbNullable(r.Wellhead), DbNullable(r.ProductionLoss), DbNullable(r.DryGas), DbNullable(r.CanadaImports),
                DbNullable(r.LngSendout), DbNullable(r.TotalSupply), DbNullable(r.Power), DbNullable(r.Industrial),
                DbNullable(r.ResidentialCommercial), DbNullable(r.Subtotal), DbNullable(r.MexicoExports), DbNullable(r.LngFeedGas),
                DbNullable(r.PipeLoss), DbNullable(r.TotalDemand), DbNullable(r.Storage), DbNullable(r.BalancingItem));
        return t;
    }
}

// -------------------------------------------------------------------- 20 County
public sealed class CountySqlSink : PlSqlSinkBase<CountyRow>
{
    public CountySqlSink(IOptions<IHSPointLogicSettings> s, ILogger<CountySqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeCounty";
    protected override string TableValuedParameterType => "arm.CountyTvp";

    protected override DataTable BuildTable(IReadOnlyList<CountyRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("CountyId", typeof(int));
        t.Columns.Add("StateId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => (x.CountyId, x.StateId)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.CountyId, r.StateId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 21 Facility
public sealed class FacilitySqlSink : PlSqlSinkBase<FacilityRow>
{
    public FacilitySqlSink(IOptions<IHSPointLogicSettings> s, ILogger<FacilitySqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeFacility";
    protected override string TableValuedParameterType => "arm.FacilityTvp";

    protected override DataTable BuildTable(IReadOnlyList<FacilityRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("FacilityId", typeof(int));
        t.Columns.Add("PointTypeId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => (x.FacilityId, x.PointTypeId)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.FacilityId, r.PointTypeId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 22 Subregion
public sealed class SubregionSqlSink : PlSqlSinkBase<SubregionRow>
{
    public SubregionSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<SubregionSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSubregion";
    protected override string TableValuedParameterType => "arm.SubregionTvp";

    protected override DataTable BuildTable(IReadOnlyList<SubregionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("SubRegionId", typeof(int));
        t.Columns.Add("RegionId", typeof(int));
        t.Columns.Add("Name", typeof(string));
        foreach (var r in rows.GroupBy(x => (x.SubRegionId, x.RegionId)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.SubRegionId, r.RegionId, r.Name);
        return t;
    }
}

// -------------------------------------------------------------------- 23 SupplyAndDemandByRegion
public sealed class SupplyAndDemandByRegionSqlSink : PlSqlSinkBase<SupplyAndDemandByRegionRow>
{
    public SupplyAndDemandByRegionSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<SupplyAndDemandByRegionSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSupplyAndDemandByRegion";
    protected override string TableValuedParameterType => "arm.SupplyAndDemandByRegionTvp";

    protected override DataTable BuildTable(IReadOnlyList<SupplyAndDemandByRegionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("RegionId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Product", typeof(string));
        t.Columns.Add("VolumeMmcfd", typeof(decimal));
        foreach (var r in rows.GroupBy(x => (x.RegionId, x.Date, x.Product)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.RegionId, D(r.Date), r.Product, DbNullable(r.VolumeMmcfd));
        return t;
    }
}

// -------------------------------------------------------------------- 24 SupplyAndDemandBySubRegion
public sealed class SupplyAndDemandBySubRegionSqlSink : PlSqlSinkBase<SupplyAndDemandBySubRegionRow>
{
    public SupplyAndDemandBySubRegionSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<SupplyAndDemandBySubRegionSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSupplyAndDemandBySubRegion";
    protected override string TableValuedParameterType => "arm.SupplyAndDemandBySubRegionTvp";

    protected override DataTable BuildTable(IReadOnlyList<SupplyAndDemandBySubRegionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("SubRegionId", typeof(int));
        t.Columns.Add("RegionId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Product", typeof(string));
        t.Columns.Add("VolumeMmcfd", typeof(decimal));
        foreach (var r in rows.GroupBy(x => (x.SubRegionId, x.RegionId, x.Date, x.Product)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.SubRegionId, r.RegionId, D(r.Date), r.Product, DbNullable(r.VolumeMmcfd));
        return t;
    }
}

// -------------------------------------------------------------------- 25 PointVolume
public sealed class PointVolumeSqlSink : PlSqlSinkBase<PointVolumeRow>
{
    public PointVolumeSqlSink(IOptions<IHSPointLogicSettings> s, ILogger<PointVolumeSqlSink> l) : base(s, l) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergePointVolume";
    protected override string TableValuedParameterType => "arm.PointVolumeTvp";

    protected override DataTable BuildTable(IReadOnlyList<PointVolumeRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PointId", typeof(int));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Volume", typeof(decimal));
        foreach (var r in rows.GroupBy(x => (x.PointId, x.Date)).Select(g => g.Last()))
            t.Rows.Add(r.FileLogId, r.PointId, D(r.Date), DbNullable(r.Volume));
        return t;
    }
}
