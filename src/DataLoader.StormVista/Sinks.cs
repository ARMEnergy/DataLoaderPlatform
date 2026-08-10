using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.StormVista;

/// <summary>
/// SQL sink for the Daily feed. Bulk-merges the FileLogId-stamped rows into
/// <c>dbo.DailyWdd</c> via <c>dbo.usp_BulkMergeDailyWdd(@Records dbo.DailyWddTvp)</c>.
/// The proc de-dups the batch on the merge key <c>(FileLogId, ValidDate)</c>,
/// resolves FlagCode → FlagId, and returns the affected row count. Concurrent-MERGE
/// serialization is applied automatically by <see cref="SqlSinkBase{TRow}"/>.
/// </summary>
public sealed class DailyWddSqlSink : SqlSinkBase<DailyWddRow>
{
    private readonly StormVistaSettings _settings;

    public DailyWddSqlSink(IOptions<StormVistaSettings> settings, ILogger<DailyWddSqlSink> logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeDailyWdd";
    protected override string TableValuedParameterType => "dbo.DailyWddTvp";
    protected override bool ProcedureReturnsRowCount => true;

    protected override DataTable BuildTable(IReadOnlyList<DailyWddRow> rows)
    {
        // Column ORDER must match dbo.DailyWddTvp exactly:
        // (FileLogId, ValidDate, FlagCode, Value).
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ValidDate", typeof(DateTime));
        t.Columns.Add("FlagCode", typeof(int));
        t.Columns.Add("Value", typeof(decimal));

        foreach (var r in rows)
            t.Rows.Add(
                r.FileLogId,
                r.ValidDate.ToDateTime(TimeOnly.MinValue),
                r.FlagCode,
                DbNullable(r.Value));

        return t;
    }
}

/// <summary>
/// SQL sink for the Regional feed. Bulk-merges the FileLogId-stamped rows into
/// <c>dbo.RegionalWdd</c> via <c>dbo.usp_BulkMergeRegionalWdd(@Records dbo.RegionalWddTvp)</c>.
/// The proc de-dups on <c>(FileLogId, RegionName, ValidDate)</c> and resolves
/// RegionName → RegionId scoped by the parent FileLog's RegionSet.
/// </summary>
public sealed class RegionalWddSqlSink : SqlSinkBase<RegionalWddRow>
{
    private readonly StormVistaSettings _settings;

    public RegionalWddSqlSink(IOptions<StormVistaSettings> settings, ILogger<RegionalWddSqlSink> logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeRegionalWdd";
    protected override string TableValuedParameterType => "dbo.RegionalWddTvp";
    protected override bool ProcedureReturnsRowCount => true;

    protected override DataTable BuildTable(IReadOnlyList<RegionalWddRow> rows)
    {
        // Column ORDER must match dbo.RegionalWddTvp exactly:
        // (FileLogId, RegionName, ValidDate, Value).
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("RegionName", typeof(string));
        t.Columns.Add("ValidDate", typeof(DateTime));
        t.Columns.Add("Value", typeof(decimal));

        foreach (var r in rows)
            t.Rows.Add(
                r.FileLogId,
                r.RegionName,
                r.ValidDate.ToDateTime(TimeOnly.MinValue),
                DbNullable(r.Value));

        return t;
    }
}
