using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>
/// The single write path for the LP feed. One TVP, one proc
/// (<c>arm.usp_BulkMergeLPReport</c>), which fans the rows into BOTH
/// <c>arm.LPReportHistory</c> and <c>arm.LPReport</c> inside one transaction — so
/// the current table and the history table can never drift apart, and a failure
/// leaves neither half-written.
///
/// <para>
/// The base's <see cref="SqlWriteGate"/> acquisition keys on this proc name, which
/// is distinct from <c>arm.usp_UpsertFileLog</c>'s key, so parallel work units
/// serialize on the merge without contending with the hub upsert.
/// </para>
/// </summary>
public sealed class OpisLpReportSqlSink : SqlSinkBase<OpisLpReportRow>
{
    private readonly OpisSettings _settings;

    public OpisLpReportSqlSink(IOptions<OpisSettings> settings, ILogger<OpisLpReportSqlSink> logger)
        : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "arm.usp_BulkMergeLPReport";
    protected override string TableValuedParameterType => "arm.LPReportTvp";

    protected override DataTable BuildTable(IReadOnlyList<OpisLpReportRow> rows)
    {
        // LOAD-BEARING: order/name/type must match arm.LPReportTvp in
        // sql/OPIS/002_CreateOpisTvpTypes.sql EXACTLY — the TVP binds BY POSITION,
        // so a reorder corrupts every row silently.
        //
        //   (FileLogId, Mkt_Prod, [Date], Timing, Price,
        //    [Low], [High], [Avg], Country, [Unit], Freq, SourceFileDate)
        //
        // DateCreated / ModifiedAtUtc are DB-stamped and are NOT columns here.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Mkt_Prod", typeof(string));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Timing", typeof(string));
        t.Columns.Add("Price", typeof(string));
        t.Columns.Add("Low", typeof(decimal));
        t.Columns.Add("High", typeof(decimal));
        t.Columns.Add("Avg", typeof(decimal));
        t.Columns.Add("Country", typeof(string));
        t.Columns.Add("Unit", typeof(string));
        t.Columns.Add("Freq", typeof(string));
        t.Columns.Add("SourceFileDate", typeof(DateTime));

        foreach (var r in rows)
        {
            t.Rows.Add(
                r.FileLogId,
                r.MktProd,
                r.Date.ToDateTime(TimeOnly.MinValue),
                r.Timing,
                r.Price,
                DbNullable(r.Low),
                DbNullable(r.High),
                DbNullable(r.Avg),
                NullIfEmpty(r.Country),
                NullIfEmpty(r.Unit),
                NullIfEmpty(r.Freq),
                r.SourceFileDate.ToDateTime(TimeOnly.MinValue));
        }

        return t;
    }
}
