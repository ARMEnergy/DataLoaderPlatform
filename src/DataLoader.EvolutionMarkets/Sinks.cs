using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EvolutionMarkets;

// =============================================================================
// The SQL sink. Bulk-merges FileLogId-stamped, checksummed rows into arm.MarketData
// via arm.usp_BulkMergeMarketData(@Records arm.MarketDataTvp).
//
// *** THE TVP CONTRACT IS LOAD-BEARING AND BINDS BY POSITION. ***
// BuildTable's DataTable must match arm.MarketDataTvp in sql/EvolutionMarkets/002 by
// column NAME + ORDER + TYPE, exactly. A silent reorder on either side produces no
// compile error, no SQL error and no warning — it just writes every value into the
// wrong column of every loaded row. And this TVP is unusually easy to get wrong: it
// carries FOUR interleaved decimal/int pairs (Price/Ask/AskSize, Bid/BidSize,
// Mid/MidSize) whose types would still bind if two were swapped.
//
// The SAME order must appear in five places:
//   1) the arm.MarketData table body in 001,
//   2) the arm.MarketDataTvp type body in 002,
//   3) the merge proc's SELECT / UPDATE / INSERT lists in 003,
//   4) BuildTable below,
//   5) the test that pins (4) against 002 (SinkTests).
//
// ModifiedAtUtc (stamped by the MERGE) and DateCreated (table DEFAULT) NEVER cross the
// TVP. The proc takes no scalar alongside its TVP: BusinessDate is per-row, and the
// run date is not persisted by this loader at all.
//
// Concurrent-MERGE serialization is applied automatically by SqlSinkBase
// (SqlWriteGate); its key is distinct from the arm.usp_UpsertFileLog key SqlEvoFileLog
// uses, so the fact merge and the hub upsert never serialize against each other
// (design §11).
// =============================================================================

/// <summary>
/// <c>arm.MarketData</c> — merge key <see cref="MarketDataRow.MarketDataId"/> (the vendor's stable
/// row id). Upsert-only: the proc has no <c>WHEN NOT MATCHED BY SOURCE</c> branch, so a truncated or
/// partial read can never wipe history (design §5.2).
///
/// <para>The proc names its TVP parameter <c>@Records</c> — which is what
/// <see cref="SqlSinkBase{TRow}"/> passes (contrast IIR's <c>@Rows</c>, which required a custom sink
/// base this loader deliberately does not need) — and <c>SELECT</c>s an affected row count, so
/// <see cref="ProcedureReturnsRowCount"/> is <c>true</c>.</para>
///
/// <para><b>⚠ The returned count is CHANGED rows, not rows sent.</b> The merge short-circuits on
/// <c>Checksum</c>, so a re-pull of an unchanged date reports <c>RecordsProcessed = 0</c> and that is
/// a healthy SUCCESS, not a silent failure. See <c>arm.usp_BulkMergeMarketData</c> in 003 and
/// design §8.4.</para>
/// </summary>
public sealed class MarketDataSqlSink : SqlSinkBase<MarketDataRow>
{
    private readonly EvoSettings _settings;

    public MarketDataSqlSink(IOptions<EvoSettings> settings, ILogger<MarketDataSqlSink> logger)
        : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "arm.usp_BulkMergeMarketData";
    protected override string TableValuedParameterType => "arm.MarketDataTvp";
    protected override bool ProcedureReturnsRowCount => true;

    // arm.MarketDataTvp — 25 columns, in this EXACT order (sql/EvolutionMarkets/002):
    //    1. FileLogId             INT              NULL
    //    2. MarketDataId          UNIQUEIDENTIFIER NOT NULL   <- merge key / PK
    //    3. Market                VARCHAR(250)     NULL
    //    4. Term                  VARCHAR(50)      NULL
    //    5. Term2                 VARCHAR(50)      NULL
    //    6. Tenor                 VARCHAR(50)      NULL
    //    7. InstrumentSourceName  VARCHAR(250)     NULL
    //    8. InstrumentId          UNIQUEIDENTIFIER NULL
    //    9. InstrumentName        VARCHAR(250)     NULL
    //   10. PriceTS               DATETIME2(0)     NULL
    //   11. BusinessDate          DATE             NULL
    //   12. PriceType             VARCHAR(250)     NULL
    //   13. [Size]                INT              NULL
    //   14. Depth                 INT              NULL
    //   15. Price                 DECIMAL(18,8)    NULL
    //   16. Ask                   DECIMAL(18,8)    NULL
    //   17. AskSize               INT              NULL
    //   18. Bid                   DECIMAL(18,8)    NULL
    //   19. BidSize               INT              NULL
    //   20. Mid                   DECIMAL(18,8)    NULL
    //   21. MidSize               INT              NULL
    //   22. [Change]              DECIMAL(18,8)    NULL
    //   23. PctRetDaily           DECIMAL(18,8)    NULL
    //   24. Currency              VARCHAR(50)      NULL
    //   25. [Checksum]            INT              NOT NULL
    protected override DataTable BuildTable(IReadOnlyList<MarketDataRow> rows)
    {
        // De-dup on the merge key (last wins), matching the proc's ROW_NUMBER dedup. A MERGE errors
        // outright if the same target row is matched twice, so this is not optional even though the
        // reader already de-dups across pages: the pipeline could in principle hand this sink a batch
        // assembled from more than one read.
        var deduped = rows
            .GroupBy(r => r.MarketDataId)
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("MarketDataId", typeof(Guid));
        t.Columns.Add("Market", typeof(string));
        t.Columns.Add("Term", typeof(string));
        t.Columns.Add("Term2", typeof(string));
        t.Columns.Add("Tenor", typeof(string));
        t.Columns.Add("InstrumentSourceName", typeof(string));
        t.Columns.Add("InstrumentId", typeof(Guid));
        t.Columns.Add("InstrumentName", typeof(string));
        t.Columns.Add("PriceTS", typeof(DateTime));
        t.Columns.Add("BusinessDate", typeof(DateTime));
        t.Columns.Add("PriceType", typeof(string));
        t.Columns.Add("Size", typeof(int));
        t.Columns.Add("Depth", typeof(int));
        t.Columns.Add("Price", typeof(decimal));
        t.Columns.Add("Ask", typeof(decimal));
        t.Columns.Add("AskSize", typeof(int));
        t.Columns.Add("Bid", typeof(decimal));
        t.Columns.Add("BidSize", typeof(int));
        t.Columns.Add("Mid", typeof(decimal));
        t.Columns.Add("MidSize", typeof(int));
        t.Columns.Add("Change", typeof(decimal));
        t.Columns.Add("PctRetDaily", typeof(decimal));
        t.Columns.Add("Currency", typeof(string));
        t.Columns.Add("Checksum", typeof(int));

        foreach (var r in deduped)
            t.Rows.Add(
                r.FileLogId,
                r.MarketDataId,
                NullIfEmpty(r.Market),
                NullIfEmpty(r.Term),
                NullIfEmpty(r.Term2),
                NullIfEmpty(r.Tenor),
                NullIfEmpty(r.InstrumentSourceName),
                DbNullable(r.InstrumentId),
                NullIfEmpty(r.InstrumentName),
                DbNullable(r.PriceTs),
                D(r.BusinessDate),
                NullIfEmpty(r.PriceType),
                DbNullable(r.Size),
                DbNullable(r.Depth),
                DbNullable(r.Price),
                DbNullable(r.Ask),
                DbNullable(r.AskSize),
                DbNullable(r.Bid),
                DbNullable(r.BidSize),
                DbNullable(r.Mid),
                DbNullable(r.MidSize),
                DbNullable(r.Change),
                DbNullable(r.PctRetDaily),
                NullIfEmpty(r.Currency),
                r.Checksum);

        return t;
    }

    /// <summary>A nullable SQL <c>DATE</c> column: <c>null</c> → <see cref="DBNull"/>.</summary>
    private static object D(DateOnly? d) =>
        d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
}
