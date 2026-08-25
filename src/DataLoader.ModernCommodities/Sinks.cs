using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ModernCommodities;

// =============================================================================
// The three SQL sinks. Each bulk-merges via arm.usp_BulkMerge<Target>(@Records <Tvp>).
//
// *** THE TVP CONTRACT IS LOAD-BEARING AND BINDS BY POSITION. ***
// Each BuildTable's column NAME + ORDER + TYPE must match its type in
// sql/ModernCommodities/002 EXACTLY. A silent reorder on either side produces no
// compile error, no SQL error and no warning — it just writes every value into the
// wrong column of every loaded row. The same order must appear in five places: the
// 001 table body (which is the USER'S OWN DDL and is the anchor), the 002 type, the
// 003 proc's SELECT/UPDATE/INSERT lists, the BuildTable below, and the CODE_TESTER
// test that pins BuildTable against 002.
//
// *** NEITHER TVP BEGINS WITH FileLogId — AND THAT IS DELIBERATE. ***
// The house rule is "FileLogId is column 1 where the table carries provenance".
// Here the column DOES NOT EXIST: the user's DDL gives the three fact tables no
// FileLogId, no surrogate Id and no DateCreated (decision D1), so provenance lives
// in arm.FileLog alone and both TVPs start at the FIRST PAYLOAD COLUMN
// (TradeNumber / SettlementDate). Anyone "restoring the convention" must first add
// the column to the user's three tables — adding it to a TVP alone shifts the
// positional binding by one and lands every value in the wrong column.
//
// WHAT NEVER CROSSES A TVP HERE:
//   * ModifiedAtUtc — stamped by the MERGE proc (SYSUTCDATETIME()) on insert AND on
//     match; its table DEFAULT is effectively unreachable.
//   * DateCreated / a surrogate Id — these columns do not exist on any of the three
//     fact tables.
//   * any per-batch scalar. *** There are none in this loader: *** no @RunDate, no
//     @WindowStart, no @RunToken. Nothing about the request window is persisted on a
//     fact row — that is arm.FileLog's job — so all three procs take the TVP and
//     nothing else.
//
// Each sink de-dups the batch on its merge key before building the DataTable, with
// the SAME rule the proc applies, so the two can never disagree. Concurrent-MERGE
// serialization is applied automatically by SqlSinkBase (SqlWriteGate); the three
// procs are three distinct keys, so the three tables' merges never serialise against
// one another, and all three are distinct from the arm.usp_UpsertFileLog key that
// SqlModComFileLog acquires (design §11.1).
// =============================================================================

/// <summary>
/// Common wiring for a ModCom sink. All three procs name their TVP parameter <c>@Records</c> —
/// which is what <see cref="SqlSinkBase{TRow}"/> passes (contrast IIR's <c>@Rows</c>, which needed a
/// custom sink base this loader deliberately does not use) — and all three <c>SELECT</c> an affected
/// row count, so <see cref="ProcedureReturnsRowCount"/> is <c>true</c>.
/// </summary>
public abstract class ModComSqlSinkBase<TRow> : SqlSinkBase<TRow>
{
    private readonly ModComSettings _settings;

    protected ModComSqlSinkBase(IOptions<ModComSettings> settings, ILogger logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override bool ProcedureReturnsRowCount => true;

    /// <summary>A nullable SQL <c>DATE</c> column: <c>null</c> → <see cref="DBNull"/>, else midnight of that day.</summary>
    protected static object D(DateOnly? d) => d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

    /// <summary>A non-null SQL <c>DATE</c> column.</summary>
    protected static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}

/// <summary>
/// Shared 34-column <c>BuildTable</c> for <b>both</b> trades tables (design §1.1, §8.3).
///
/// <para>The <c>allTrades</c> and <c>myTrades</c> CSV headers are byte-identical and the user's DDL
/// gives the two tables identical column lists, so they share one row type, one TVP type
/// (<c>arm.TradesTvp</c>) and this one <c>BuildTable</c>. <b>Two 34-column definitions kept in sync
/// by hand is exactly the drift a positional contract cannot survive.</b> The <i>only</i>
/// per-endpoint C# is the subclass's <see cref="SqlSinkBase{TRow}.StoredProcedureName"/>.</para>
/// </summary>
public abstract class TradesSqlSinkBase : ModComSqlSinkBase<TradeRow>
{
    protected TradesSqlSinkBase(IOptions<ModComSettings> settings, ILogger logger) : base(settings, logger) { }

    protected override string TableValuedParameterType => "arm.TradesTvp";

    // arm.TradesTvp — 34 columns, in this exact order (sql/ModernCommodities/002):
    //    1. TradeNumber            INT           NOT NULL   <- merge key (the whole PK)
    //    2. State                  VARCHAR(50)   NULL
    //    3. Product                VARCHAR(50)   NULL
    //    4. Location               VARCHAR(50)   NULL
    //    5. PipelineTerminal       VARCHAR(256)  NULL       (spelled CORRECTLY here)
    //    6. PriceBasis             VARCHAR(256)  NULL
    //    7. Term                   VARCHAR(256)  NULL
    //    8. TermStart              DATE          NULL
    //    9. TermEnd                DATE          NULL
    //   10. Price                  DECIMAL(9,2)  NULL       SIGNED
    //   11. Volume                 DECIMAL(9,2)  NULL       observed max 300,000
    //   12. UnitOfMeasure          VARCHAR(50)   NULL
    //   13. Executed               DATETIME2(0)  NULL
    //   14. LastUpdated            DATETIME2(0)  NULL       <- MERGE recency guard
    //   15. TradeType              VARCHAR(50)   NULL
    //   16. Side                   VARCHAR(256)  NULL
    //   17. BidTrader              VARCHAR(256)  NULL
    //   18. BidLegalName           VARCHAR(256)  NULL
    //   19. BidAddress             VARCHAR(256)  NULL
    //   20. BidCommission          DECIMAL(9,2)  NULL
    //   21. OfferTrader            VARCHAR(256)  NULL
    //   22. OfferLegalName         VARCHAR(256)  NULL
    //   23. OfferAddress           VARCHAR(256)  NULL
    //   24. OfferCommission        DECIMAL(9,2)  NULL
    //   25. SpreadTradeNumber      VARCHAR(50)   NULL       TEXT, not INT
    //   26. ApportionmentProtected BIT           NULL
    //   27. ClearingID             VARCHAR(50)   NULL       never populated by either endpoint
    //   28. SettlementCurrency     VARCHAR(50)   NULL
    //   29. ContractTerms          VARCHAR(256)  NULL
    //   30. GTandC                 VARCHAR(256)  NULL       source header is 'GT&C'
    //   31. Notes                  VARCHAR(8000) NULL
    //   32. InIndex                BIT           NULL
    //   33. ClickAndTrade          BIT           NULL
    //   34. ProductType            VARCHAR(50)   NULL
    protected override DataTable BuildTable(IReadOnlyList<TradeRow> rows)
    {
        // De-dup on the merge key, LAST WINS ORDERED BY LastUpdated — the proc applies the same rule
        // (ROW_NUMBER … ORDER BY LastUpdated DESC). OrderBy is a stable sort, so Last() takes the
        // newest revision and, among ties, the last occurrence; a NULL timestamp sorts lowest and
        // therefore never beats a dated copy (design §8.3, §11.2).
        var deduped = rows
            .GroupBy(r => r.TradeNumber)
            .Select(g => g.OrderBy(r => r.LastUpdated ?? DateTime.MinValue).Last());

        var t = new DataTable();
        t.Columns.Add("TradeNumber", typeof(int));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("Product", typeof(string));
        t.Columns.Add("Location", typeof(string));
        t.Columns.Add("PipelineTerminal", typeof(string));
        t.Columns.Add("PriceBasis", typeof(string));
        t.Columns.Add("Term", typeof(string));
        t.Columns.Add("TermStart", typeof(DateTime));
        t.Columns.Add("TermEnd", typeof(DateTime));
        t.Columns.Add("Price", typeof(decimal));
        t.Columns.Add("Volume", typeof(decimal));
        t.Columns.Add("UnitOfMeasure", typeof(string));
        t.Columns.Add("Executed", typeof(DateTime));
        t.Columns.Add("LastUpdated", typeof(DateTime));
        t.Columns.Add("TradeType", typeof(string));
        t.Columns.Add("Side", typeof(string));
        t.Columns.Add("BidTrader", typeof(string));
        t.Columns.Add("BidLegalName", typeof(string));
        t.Columns.Add("BidAddress", typeof(string));
        t.Columns.Add("BidCommission", typeof(decimal));
        t.Columns.Add("OfferTrader", typeof(string));
        t.Columns.Add("OfferLegalName", typeof(string));
        t.Columns.Add("OfferAddress", typeof(string));
        t.Columns.Add("OfferCommission", typeof(decimal));
        t.Columns.Add("SpreadTradeNumber", typeof(string));
        t.Columns.Add("ApportionmentProtected", typeof(bool));
        t.Columns.Add("ClearingID", typeof(string));
        t.Columns.Add("SettlementCurrency", typeof(string));
        t.Columns.Add("ContractTerms", typeof(string));
        t.Columns.Add("GTandC", typeof(string));
        t.Columns.Add("Notes", typeof(string));
        t.Columns.Add("InIndex", typeof(bool));
        t.Columns.Add("ClickAndTrade", typeof(bool));
        t.Columns.Add("ProductType", typeof(string));

        foreach (var r in deduped)
            t.Rows.Add(
                r.TradeNumber,
                NullIfEmpty(r.State),
                NullIfEmpty(r.Product),
                NullIfEmpty(r.Location),
                NullIfEmpty(r.PipelineTerminal),
                NullIfEmpty(r.PriceBasis),
                NullIfEmpty(r.Term),
                D(r.TermStart),
                D(r.TermEnd),
                DbNullable(r.Price),
                DbNullable(r.Volume),
                NullIfEmpty(r.UnitOfMeasure),
                DbNullable(r.Executed),
                DbNullable(r.LastUpdated),
                NullIfEmpty(r.TradeType),
                NullIfEmpty(r.Side),
                NullIfEmpty(r.BidTrader),
                NullIfEmpty(r.BidLegalName),
                NullIfEmpty(r.BidAddress),
                DbNullable(r.BidCommission),
                NullIfEmpty(r.OfferTrader),
                NullIfEmpty(r.OfferLegalName),
                NullIfEmpty(r.OfferAddress),
                DbNullable(r.OfferCommission),
                NullIfEmpty(r.SpreadTradeNumber),
                DbNullable(r.ApportionmentProtected),
                NullIfEmpty(r.ClearingID),
                NullIfEmpty(r.SettlementCurrency),
                NullIfEmpty(r.ContractTerms),
                NullIfEmpty(r.GTandC),
                NullIfEmpty(r.Notes),
                DbNullable(r.InIndex),
                DbNullable(r.ClickAndTrade),
                NullIfEmpty(r.ProductType));

        return t;
    }
}

/// <summary>
/// <c>arm.AllTrades</c> — the anonymised market tape; merge key <c>TradeNumber</c>.
///
/// <para>The 14 counterparty columns are 100 % NULL here <b>by design</b> (the vendor anonymises
/// them on this endpoint) and that is correct, not a defect. <b>Upsert-only</b>: the proc has no
/// <c>WHEN NOT MATCHED BY SOURCE</c> branch, so a truncated or empty response can never wipe the
/// table. Its <c>WHEN MATCHED</c> branch is guarded by last-updated recency (decision D14), which
/// makes the outcome independent of chunk arrival order and re-run count.</para>
/// </summary>
public sealed class AllTradesSqlSink : TradesSqlSinkBase
{
    public AllTradesSqlSink(IOptions<ModComSettings> settings, ILogger<AllTradesSqlSink> logger)
        : base(settings, logger) { }

    protected override string StoredProcedureName => "arm.usp_BulkMergeAllTrades";
}

/// <summary>
/// <c>arm.MyTrades</c> — the fully attributed own-trades table; merge key <c>TradeNumber</c>.
///
/// <para>The same <c>TradeNumber</c> legitimately exists in <b>both</b> trades tables, with
/// <b>no FK and no cross-table dedup</b>: they are two <i>views</i> of the venue at different
/// disclosure levels, not a parent/child pair. The validator reports the overlap
/// <b>informationally</b>, never as a defect (design §1.2).</para>
/// </summary>
public sealed class MyTradesSqlSink : TradesSqlSinkBase
{
    public MyTradesSqlSink(IOptions<ModComSettings> settings, ILogger<MyTradesSqlSink> logger)
        : base(settings, logger) { }

    protected override string StoredProcedureName => "arm.usp_BulkMergeMyTrades";
}

/// <summary>
/// <c>arm.Settlements</c> — merge key = all six of
/// <c>(SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term)</c>.
///
/// <para><b>Plain last-wins, upsert-only, no delete branch.</b> A settlement price revision
/// therefore <b>overwrites</b>: <c>Price</c> sits outside the PK, so no history is kept. That is
/// correct <i>if</i> settlements are never restated — which is unverified, and the payload carries
/// no revision/version/status field to distinguish one. If it proves false, the pattern to reach for
/// is OPIS's <c>arm.LPReportHistory</c> (put the distinguishing value in the key so both prints
/// survive). Not built now (design §11.3, §12 item 2).</para>
///
/// <para><b>Cross-chunk collisions are impossible here</b> — chunks partition by
/// <c>SettlementDate</c>, which is PK column 1, so two concurrent settlements units always write
/// disjoint key sets. That is precisely why settlements needs no recency guard while trades does:
/// the trades window predicate (<c>LastUpdated</c>) is <b>not</b> a key column.</para>
/// </summary>
public sealed class SettlementsSqlSink : ModComSqlSinkBase<SettlementRow>
{
    public SettlementsSqlSink(IOptions<ModComSettings> settings, ILogger<SettlementsSqlSink> logger)
        : base(settings, logger) { }

    protected override string StoredProcedureName => "arm.usp_BulkMergeSettlements";
    protected override string TableValuedParameterType => "arm.SettlementsTvp";

    // arm.SettlementsTvp — 9 columns, in this exact order (sql/ModernCommodities/002):
    //   1. SettlementDate   DATE          NOT NULL   <- merge key 1/6
    //   2. Product          VARCHAR(50)   NOT NULL   <- merge key 2/6
    //   3. Location         VARCHAR(50)   NOT NULL   <- merge key 3/6  ('-' on 82/1443 rows — VERBATIM)
    //   4. PieplineTerminal VARCHAR(50)   NOT NULL   <- merge key 4/6  *** [sic] — the user's spelling, and it is IN THE PK ***
    //   5. PriceBasis       VARCHAR(100)  NOT NULL   <- merge key 5/6  ('USD $' — a space and a '$'; never normalised)
    //   6. Term             VARCHAR(50)   NOT NULL   <- merge key 6/6
    //   7. TermStart        DATE          NULL
    //   8. TermEnd          DATE          NULL
    //   9. Price            DECIMAL(9,2)  NULL       SIGNED (74% negative)
    protected override DataTable BuildTable(IReadOnlyList<SettlementRow> rows)
    {
        // De-dup on the 6-column merge key, last wins — matching the proc's ROW_NUMBER dedup. There
        // is no recency column to order by; see SettlementsSqlSink's remarks and design §11.3.
        var deduped = rows
            .GroupBy(r => (r.SettlementDate,
                           Product: r.Product.ToUpperInvariant(),
                           Location: r.Location.ToUpperInvariant(),
                           Piepline: r.PieplineTerminal.ToUpperInvariant(),
                           PriceBasis: r.PriceBasis.ToUpperInvariant(),
                           Term: r.Term.ToUpperInvariant()))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("SettlementDate", typeof(DateTime));
        t.Columns.Add("Product", typeof(string));
        t.Columns.Add("Location", typeof(string));
        t.Columns.Add("PieplineTerminal", typeof(string));   // [sic] — see the header comment
        t.Columns.Add("PriceBasis", typeof(string));
        t.Columns.Add("Term", typeof(string));
        t.Columns.Add("TermStart", typeof(DateTime));
        t.Columns.Add("TermEnd", typeof(DateTime));
        t.Columns.Add("Price", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(
                D(r.SettlementDate),
                // The five text keys are passed through DIRECTLY, never via NullIfEmpty: they are
                // NOT NULL key values that the reader already guaranteed non-blank, and '-' must
                // survive verbatim.
                r.Product,
                r.Location,
                r.PieplineTerminal,
                r.PriceBasis,
                r.Term,
                D(r.TermStart),
                D(r.TermEnd),
                DbNullable(r.Price));

        return t;
    }
}
