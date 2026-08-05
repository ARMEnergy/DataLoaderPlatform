using System.Data;
using DataLoader.Core.Sinks;
using DataLoader.Vulcan.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

public sealed class UnderConstructionSink : SqlSinkBase<UnderConstructionRow>
{
    private readonly VulcanSettings _s;
    public UnderConstructionSink(IOptions<VulcanSettings> s, ILogger<UnderConstructionSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeUnderConstruction";
    protected override string TableValuedParameterType => "dbo.UnderConstructionTvp";
    protected override DataTable BuildTable(IReadOnlyList<UnderConstructionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantId", typeof(string));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("NameplateCapacity", typeof(decimal));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("ProjectRank", typeof(decimal));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("BalancingAuthority", typeof(string));
        t.Columns.Add("Latitude", typeof(decimal));
        t.Columns.Add("Longitude", typeof(decimal));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantId), NullIfEmpty(r.PlantName), NullIfEmpty(r.Technology),
                DbNullable((decimal?)r.NameplateCapacity), NullIfEmpty(r.VulcanStatus), DbNullable((decimal?)r.ProjectRank),
                DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline), NullIfEmpty(r.StateCode),
                NullIfEmpty(r.BalancingAuthority), DbNullable((decimal?)r.Latitude), DbNullable((decimal?)r.Longitude),
                DbNullable(r.DateVulcanStatusChange), DbNullable(r.DateImage));
        return t;
    }
}

public sealed class DataCentersSink : SqlSinkBase<DataCenterRow>
{
    private readonly VulcanSettings _s;
    public DataCentersSink(IOptions<VulcanSettings> s, ILogger<DataCentersSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeDataCenters";
    protected override string TableValuedParameterType => "dbo.DataCentersTvp";
    protected override DataTable BuildTable(IReadOnlyList<DataCenterRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantId", typeof(string));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("UnitId", typeof(string));
        t.Columns.Add("UnitName", typeof(string));
        t.Columns.Add("OwnerName", typeof(string));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("BalancingAuthority", typeof(string));
        t.Columns.Add("Country", typeof(string));
        t.Columns.Add("MarketRegion", typeof(string));
        t.Columns.Add("DataCenterType", typeof(string));
        t.Columns.Add("UnitCapacity", typeof(decimal));
        t.Columns.Add("UnitStatus", typeof(string));
        t.Columns.Add("PlantStatus", typeof(string));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("Source", typeof(string));
        t.Columns.Add("BtmGeneration", typeof(bool));
        t.Columns.Add("BtmClassification", typeof(string));
        t.Columns.Add("Observation", typeof(string));
        t.Columns.Add("DatePlannedOperation", typeof(DateTime));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        t.Columns.Add("DateImageReviewed", typeof(DateTime));
        t.Columns.Add("DateConstructionStart", typeof(DateTime));
        t.Columns.Add("DateLandCleared", typeof(DateTime));
        t.Columns.Add("DateFirstStructures", typeof(DateTime));
        t.Columns.Add("DateConstruction50PercentComplete", typeof(DateTime));
        t.Columns.Add("DateConstructionCompleted", typeof(DateTime));
        t.Columns.Add("DateVulcanEarliestPlus7", typeof(DateTime));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanMedianOnline", typeof(DateTime));
        t.Columns.Add("DaysIirMinusVulcanEarliestOnline", typeof(decimal));
        t.Columns.Add("DaysIirMinusVulcanLatestOnline", typeof(decimal));
        t.Columns.Add("DateProjectedEarliestLandClear", typeof(DateTime));
        t.Columns.Add("DateProjectedMedianLandClear", typeof(DateTime));
        t.Columns.Add("DateProjectedEarliestFirstStructures", typeof(DateTime));
        t.Columns.Add("DateProjectedMedianFirstStructures", typeof(DateTime));
        t.Columns.Add("WeeklyProgressIndicator", typeof(decimal));
        t.Columns.Add("TotalWpi", typeof(decimal));
        t.Columns.Add("WpiOnlineDate", typeof(DateTime));
        t.Columns.Add("CreatedAt", typeof(DateTime));
        t.Columns.Add("ModifiedAt", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(
                r.SynmaxId, NullIfEmpty(r.PlantId), NullIfEmpty(r.PlantName), NullIfEmpty(r.UnitId),
                NullIfEmpty(r.UnitName), NullIfEmpty(r.OwnerName), NullIfEmpty(r.StateCode),
                NullIfEmpty(r.BalancingAuthority), NullIfEmpty(r.Country), NullIfEmpty(r.MarketRegion),
                NullIfEmpty(r.DataCenterType), DbNullable((decimal?)r.UnitCapacity), NullIfEmpty(r.UnitStatus),
                NullIfEmpty(r.PlantStatus), NullIfEmpty(r.VulcanStatus), NullIfEmpty(r.Source),
                DbNullable(r.BtmGeneration), NullIfEmpty(r.BtmClassification), NullIfEmpty(r.Observation),
                DbNullable(r.DatePlannedOperation), DbNullable(r.DateVulcanStatusChange), DbNullable(r.DateImage),
                DbNullable(r.DateImageReviewed), DbNullable(r.DateConstructionStart), DbNullable(r.DateLandCleared),
                DbNullable(r.DateFirstStructures), DbNullable(r.DateConstruction50PercentComplete),
                DbNullable(r.DateConstructionCompleted), DbNullable(r.DateVulcanEarliestPlus7),
                DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline),
                DbNullable(r.DateVulcanMedianOnline), DbNullable((decimal?)r.DaysIirMinusVulcanEarliestOnline),
                DbNullable((decimal?)r.DaysIirMinusVulcanLatestOnline), DbNullable(r.DateProjectedEarliestLandClear),
                DbNullable(r.DateProjectedMedianLandClear), DbNullable(r.DateProjectedEarliestFirstStructures),
                DbNullable(r.DateProjectedMedianFirstStructures), DbNullable((decimal?)r.WeeklyProgressIndicator),
                DbNullable((decimal?)r.TotalWpi), DbNullable(r.WpiOnlineDate), DbNullable(r.CreatedAt),
                DbNullable(r.ModifiedAt));
        return t;
    }
}

public sealed class LngProjectsSink : SqlSinkBase<LngProjectRow>
{
    private readonly VulcanSettings _s;
    public LngProjectsSink(IOptions<VulcanSettings> s, ILogger<LngProjectsSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeLngProjects";
    protected override string TableValuedParameterType => "dbo.LngProjectsTvp";
    protected override DataTable BuildTable(IReadOnlyList<LngProjectRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("PhaseNumber", typeof(int));
        t.Columns.Add("EntityName", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("NameplateCapacity", typeof(decimal));
        t.Columns.Add("CapacityUnit", typeof(string));
        t.Columns.Add("Trains", typeof(int));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("Latitude", typeof(decimal));
        t.Columns.Add("Longitude", typeof(decimal));
        t.Columns.Add("Observation", typeof(string));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        t.Columns.Add("ModifiedAt", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(NullIfEmpty(r.PlantName), r.PhaseNumber, NullIfEmpty(r.EntityName), NullIfEmpty(r.Technology),
                DbNullable((decimal?)r.NameplateCapacity), NullIfEmpty(r.CapacityUnit), DbNullable(r.Trains),
                NullIfEmpty(r.VulcanStatus), DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline),
                DbNullable((decimal?)r.Latitude), DbNullable((decimal?)r.Longitude), NullIfEmpty(r.Observation),
                DbNullable(r.DateVulcanStatusChange), DbNullable(r.DateImage), DbNullable(r.ModifiedAt));
        return t;
    }
}

public sealed class ProjectRankingsSink : SqlSinkBase<ProjectRankingRow>
{
    private readonly VulcanSettings _s;
    public ProjectRankingsSink(IOptions<VulcanSettings> s, ILogger<ProjectRankingsSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeProjectRankings";
    protected override string TableValuedParameterType => "dbo.ProjectRankingsTvp";
    protected override DataTable BuildTable(IReadOnlyList<ProjectRankingRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantId", typeof(long));
        t.Columns.Add("GeneratorId", typeof(string));
        t.Columns.Add("FinalRank", typeof(decimal));
        t.Columns.Add("DateVulcanProposedV2Online", typeof(DateTime));
        t.Columns.Add("DateVulcanProposedOnline", typeof(DateTime));
        t.Columns.Add("DateUpdated", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, DbNullable(r.PlantId), NullIfEmpty(r.GeneratorId), DbNullable((decimal?)r.FinalRank),
                DbNullable(r.DateVulcanProposedV2Online), DbNullable(r.DateVulcanProposedOnline), DbNullable(r.DateUpdated));
        return t;
    }
}

public sealed class MetadataHistorySink : SqlSinkBase<MetadataHistoryRow>
{
    private readonly VulcanSettings _s;
    public MetadataHistorySink(IOptions<VulcanSettings> s, ILogger<MetadataHistorySink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeMetadataHistory";
    protected override string TableValuedParameterType => "dbo.MetadataHistoryTvp";
    protected override DataTable BuildTable(IReadOnlyList<MetadataHistoryRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("NameplateCapacity", typeof(decimal));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("County", typeof(string));
        t.Columns.Add("DatePlannedOperations", typeof(DateTime));
        t.Columns.Add("PlantStatus", typeof(string));
        t.Columns.Add("DateEiaUpdated", typeof(DateTime));
        t.Columns.Add("DaysPlannedOperationMinusFirstSeenPlannedOperation", typeof(decimal));
        t.Columns.Add("Latitude", typeof(decimal));
        t.Columns.Add("Longitude", typeof(decimal));
        t.Columns.Add("BalancingAuthorityCode", typeof(string));
        t.Columns.Add("SectorName", typeof(string));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantName), NullIfEmpty(r.Technology), DbNullable((decimal?)r.NameplateCapacity),
                NullIfEmpty(r.StateCode), NullIfEmpty(r.County), DbNullable(r.DatePlannedOperations),
                NullIfEmpty(r.PlantStatus), DbNullable(r.DateEiaUpdated),
                DbNullable((decimal?)r.DaysPlannedOperationMinusFirstSeenPlannedOperation), DbNullable((decimal?)r.Latitude),
                DbNullable((decimal?)r.Longitude), NullIfEmpty(r.BalancingAuthorityCode), NullIfEmpty(r.SectorName));
        return t;
    }
}
