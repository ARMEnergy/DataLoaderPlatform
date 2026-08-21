using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGI;

// =============================================================================
// The two SQL sinks. Each bulk-merges its FileLogId-stamped rows into arm.<Table>
// via arm.usp_BulkMerge<Table>(@Records arm.<Table>Tvp).
//
// *** THE TVP CONTRACT IS LOAD-BEARING AND BINDS BY POSITION. ***
// Each BuildTable's column NAME + ORDER + TYPE must match its type in sql/NGI/002
// EXACTLY. A silent reorder on either side produces no compile error, no SQL error
// and no warning — it just writes every value into the wrong column of every loaded
// row. The same order must appear in five places: the 001 table body, the 002 type,
// the 003 proc's SELECT/INSERT/UPDATE lists, the BuildTable below, and the
// CODE_TESTER test that pins BuildTable against 002.
//
// FileLogId is column 1 in BOTH types. DateCreated (table DEFAULT) and
// ModifiedAtUtc (stamped by the MERGE) never cross a TVP, and neither proc takes a
// scalar alongside its TVP.
//
// Each sink de-dups the batch on its merge key before building the DataTable so an
// in-batch duplicate cannot break the MERGE (the procs de-dup identically).
// Concurrent-MERGE serialization is applied automatically by SqlSinkBase
// (SqlWriteGate); the two procs are distinct keys, so the location and fact merges
// never serialize against each other, and both are distinct from the
// arm.usp_UpsertFileLog key SqlNgiFileLog uses (design §11).
// =============================================================================

/// <summary>
/// Common wiring for an NGI sink. Both procs name their TVP parameter <c>@Records</c> — which is what
/// <see cref="SqlSinkBase{TRow}"/> passes (contrast IIR's <c>@Rows</c>, which needed a custom sink
/// base NGI deliberately does not use) — and both <c>SELECT</c> an affected row count, so
/// <see cref="ProcedureReturnsRowCount"/> is <c>true</c>.
/// </summary>
public abstract class NgiSqlSinkBase<TRow> : SqlSinkBase<TRow>
{
    private readonly NgiSettings _settings;

    protected NgiSqlSinkBase(IOptions<NgiSettings> settings, ILogger logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override bool ProcedureReturnsRowCount => true;

    /// <summary>A SQL <c>DATE</c> column takes a <see cref="DateTime"/> at midnight.</summary>
    protected static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    /// <summary>A nullable SQL <c>DATE</c> column: <c>null</c> → <see cref="DBNull"/>.</summary>
    protected static object D(DateOnly? d) => d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
}

// -------------------------------------------------------------- endpoint 2: arm.BidWeekLocation
/// <summary>
/// <c>arm.BidWeekLocation</c> — merge key <c>PointCode</c>. Upsert-only: the proc has no
/// <c>WHEN NOT MATCHED BY SOURCE</c> branch, so a retired code stays in the table and a truncated
/// snapshot can never wipe the crosswalk (design §5.2 step 7).
/// </summary>
public sealed class BidWeekLocationSqlSink : NgiSqlSinkBase<BidWeekLocationRow>
{
    public BidWeekLocationSqlSink(IOptions<NgiSettings> settings, ILogger<BidWeekLocationSqlSink> logger)
        : base(settings, logger) { }

    protected override string StoredProcedureName => "arm.usp_BulkMergeBidWeekLocation";
    protected override string TableValuedParameterType => "arm.BidWeekLocationTvp";

    // arm.BidWeekLocationTvp — 3 columns, in this exact order (sql/NGI/002):
    //   1. FileLogId      INT           NULL
    //   2. PointCode      VARCHAR(20)   NOT NULL   <- merge key (the JSON object VALUE)
    //   3. LocationName   VARCHAR(100)  NULL       <- the JSON object KEY
    protected override DataTable BuildTable(IReadOnlyList<BidWeekLocationRow> rows)
    {
        // De-dup on the merge key (last wins), matching the proc's ROW_NUMBER dedup.
        var deduped = rows
            .GroupBy(r => r.PointCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("PointCode", typeof(string));
        t.Columns.Add("LocationName", typeof(string));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.PointCode, NullIfEmpty(r.LocationName));
        return t;
    }
}

// -------------------------------------------------------------- endpoint 1: arm.BidWeekData
/// <summary>
/// <c>arm.BidWeekData</c> — merge key <c>(IssueDate, PointCode)</c>, <b>never</b> <c>FileLogId</c>
/// (provenance, UPDATEd on match). Upsert-only, plain last-wins: whether NGI ever <i>revises</i> a
/// published issue is unknown and the feed carries no revision/version/status field at all, so if a
/// revision ever needs preserving the pattern to reach for is OPIS's <c>arm.LPReportHistory</c>
/// (a record-status code in the key). Not built now (design §11, §12 item 1).
/// </summary>
public sealed class BidWeekDataSqlSink : NgiSqlSinkBase<BidWeekDataRow>
{
    public BidWeekDataSqlSink(IOptions<NgiSettings> settings, ILogger<BidWeekDataSqlSink> logger)
        : base(settings, logger) { }

    protected override string StoredProcedureName => "arm.usp_BulkMergeBidWeekData";
    protected override string TableValuedParameterType => "arm.BidWeekDataTvp";

    // arm.BidWeekDataTvp — 12 columns, in this exact order (sql/NGI/002):
    //    1. FileLogId      INT           NULL
    //    2. IssueDate      DATE          NOT NULL   <- merge key part 1
    //    3. PointCode      VARCHAR(20)   NOT NULL   <- merge key part 2
    //    4. SurveyStart    DATE          NULL
    //    5. SurveyEnd      DATE          NULL
    //    6. Region         VARCHAR(64)   NULL
    //    7. PricingPoint   VARCHAR(100)  NULL
    //    8. [Low]          DECIMAL(13,6) NULL
    //    9. [High]         DECIMAL(13,6) NULL
    //   10. [Average]      DECIMAL(13,6) NULL
    //   11. [Volume]       INT           NULL
    //   12. Deals          INT           NULL
    protected override DataTable BuildTable(IReadOnlyList<BidWeekDataRow> rows)
    {
        // De-dup on the natural/MERGE key (last wins), matching the proc.
        var deduped = rows
            .GroupBy(r => (r.IssueDate, PointCode: r.PointCode.ToUpperInvariant()))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("IssueDate", typeof(DateTime));
        t.Columns.Add("PointCode", typeof(string));
        t.Columns.Add("SurveyStart", typeof(DateTime));
        t.Columns.Add("SurveyEnd", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("PricingPoint", typeof(string));
        t.Columns.Add("Low", typeof(decimal));
        t.Columns.Add("High", typeof(decimal));
        t.Columns.Add("Average", typeof(decimal));
        t.Columns.Add("Volume", typeof(int));
        t.Columns.Add("Deals", typeof(int));

        foreach (var r in deduped)
            t.Rows.Add(
                r.FileLogId, D(r.IssueDate), r.PointCode,
                D(r.SurveyStart), D(r.SurveyEnd),
                NullIfEmpty(r.Region), NullIfEmpty(r.PricingPoint),
                DbNullable(r.Low), DbNullable(r.High), DbNullable(r.Average),
                DbNullable(r.Volume), DbNullable(r.Deals));
        return t;
    }
}
