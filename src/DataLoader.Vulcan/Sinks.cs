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
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantId), NullIfEmpty(r.PlantName), NullIfEmpty(r.Technology),
                DbNullable((decimal?)r.NameplateCapacity), NullIfEmpty(r.VulcanStatus), DbNullable((decimal?)r.ProjectRank),
                DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline), NullIfEmpty(r.StateCode),
                NullIfEmpty(r.BalancingAuthority), DbNullable(r.Latitude), DbNullable(r.Longitude),
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
        t.Columns.Add("DataCenterType", typeof(string));
        t.Columns.Add("UnitCapacity", typeof(decimal));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("BalancingAuthority", typeof(string));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("ModifiedAt", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantId), NullIfEmpty(r.PlantName), NullIfEmpty(r.UnitId),
                NullIfEmpty(r.UnitName), NullIfEmpty(r.OwnerName), NullIfEmpty(r.DataCenterType),
                DbNullable((decimal?)r.UnitCapacity), NullIfEmpty(r.VulcanStatus), NullIfEmpty(r.StateCode),
                NullIfEmpty(r.BalancingAuthority), DbNullable(r.DateVulcanEarliestOnline),
                DbNullable(r.DateVulcanLatestOnline), DbNullable(r.ModifiedAt));
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
        t.Columns.Add("NameplateCapacity", typeof(double));
        t.Columns.Add("CapacityUnit", typeof(string));
        t.Columns.Add("Trains", typeof(int));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("Observation", typeof(string));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        t.Columns.Add("ModifiedAt", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(NullIfEmpty(r.PlantName), r.PhaseNumber, NullIfEmpty(r.EntityName), NullIfEmpty(r.Technology),
                DbNullable(r.NameplateCapacity), NullIfEmpty(r.CapacityUnit), DbNullable(r.Trains),
                NullIfEmpty(r.VulcanStatus), DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline),
                DbNullable(r.Latitude), DbNullable(r.Longitude), NullIfEmpty(r.Observation),
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
        t.Columns.Add("NameplateCapacity", typeof(double));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("County", typeof(string));
        t.Columns.Add("DatePlannedOperations", typeof(DateTime));
        t.Columns.Add("PlantStatus", typeof(string));
        t.Columns.Add("DateEiaUpdated", typeof(DateTime));
        t.Columns.Add("DaysPlannedOperationMinusFirstSeenPlannedOperation", typeof(double));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("BalancingAuthorityCode", typeof(string));
        t.Columns.Add("SectorName", typeof(string));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantName), NullIfEmpty(r.Technology), DbNullable(r.NameplateCapacity),
                NullIfEmpty(r.StateCode), NullIfEmpty(r.County), DbNullable(r.DatePlannedOperations),
                NullIfEmpty(r.PlantStatus), DbNullable(r.DateEiaUpdated),
                DbNullable(r.DaysPlannedOperationMinusFirstSeenPlannedOperation), DbNullable(r.Latitude),
                DbNullable(r.Longitude), NullIfEmpty(r.BalancingAuthorityCode), NullIfEmpty(r.SectorName));
        return t;
    }
}
