namespace DataLoader.ModernCommodities;

/// <summary>
/// The two CSV payload shapes. <c>allTrades</c> and <c>myTrades</c> share
/// <see cref="Trades"/>: their headers are <b>byte-identical</b> (497 bytes, 34 columns, same
/// order — verified across three live captures) and the user's DDL gives the two tables identical
/// column lists, so they share one row type, one reader class, one <c>BuildTable</c> and one TVP
/// (design §1.1). Only the sink's stored-procedure name differs.
/// </summary>
public enum ModComParseShape
{
    /// <summary>34 columns, merge key <c>Trade Number</c> → <c>arm.TradesTvp</c>.</summary>
    Trades,

    /// <summary>9 columns, 6-column composite merge key → <c>arm.SettlementsTvp</c>.</summary>
    Settlements
}

/// <summary>
/// Endpoint id constants — the <c>EnabledEndpoints</c> values, the <c>IModComPipeline.EndpointId</c>
/// values <b>and</b> the seeded <c>arm.Endpoint.Name</c> values, which is why they are literals in
/// one place.
/// </summary>
public static class ModComEndpoints
{
    public const string AllTrades = "AllTrades";
    public const string MyTrades = "MyTrades";
    public const string Settlements = "Settlements";
}

/// <summary>
/// Compile-time description of one Modern Commodities endpoint (design §1.1). Enumeration is
/// <b>DB-free</b>: nothing here is discovered at run time, and nothing reads a ModCom table back
/// (design §2 — there is deliberately no <c>usp_Get…</c> read proc, no reference provider and no
/// tier barrier).
/// </summary>
/// <param name="EndpointId">Stable id: <c>AllTrades</c> | <c>MyTrades</c> | <c>Settlements</c>.</param>
/// <param name="DisplayName">Human-readable label for logs.</param>
/// <param name="Path">Relative path under <c>ModComSettings.BaseUrl</c>, including the <c>/v1</c> segment.</param>
/// <param name="ParseShape">Which CSV shape / row type / TVP this endpoint feeds.</param>
/// <param name="HistoryLimited">
/// <c>true</c> when <c>startDate</c> is capped at 6 <b>calendar</b> months back (<c>allTrades</c>,
/// <c>settlements</c>). <c>myTrades</c> is all-time and is <b>not</b> clamped — only its row cap
/// applies (design §3.2 #4).
/// </param>
/// <param name="RowCap">
/// The vendor's hard per-request row cap. Exceeding it is a <c>400</c>, <b>not</b> a truncation —
/// there is no paging, so the only remedy is a narrower date range (design §3.4 / §5.6).
/// </param>
/// <param name="SupportsLegalEntityName">
/// <c>true</c> only for <c>myTrades</c>. Omitting the parameter returns <b>both</b> ARM legal
/// entities (verified), which is why the shipped default omits it entirely.
/// </param>
/// <param name="EstimatedRowsPerCalendarDay">
/// A measured rate used <b>only</b> for the pre-flight cap warning (design §1.6 step 3) and the
/// row-cap error message (§5.6 rule 3). <b>Never a gate and never a reason to skip a request.</b>
/// </param>
/// <param name="TargetTable">Target fact table (for log/warning text only).</param>
/// <param name="TargetTvp">TVP type name passed to <c>SqlSinkBase</c>.</param>
/// <param name="TargetProc">Merge proc name passed to <c>SqlSinkBase</c>.</param>
public sealed record ModComEndpointDescriptor(
    string EndpointId,
    string DisplayName,
    string Path,
    ModComParseShape ParseShape,
    bool HistoryLimited,
    int RowCap,
    bool SupportsLegalEntityName,
    double EstimatedRowsPerCalendarDay,
    string TargetTable,
    string TargetTvp,
    string TargetProc);

/// <summary>
/// The three-entry endpoint registry (design §1.1).
///
/// <para><b>⚠ The three pipelines are mutually INDEPENDENT — do not inherit AGSI's coupling.</b>
/// In AGSI a discovery pipeline populates a dimension that a reference provider reads back to build
/// the fact pipeline's work units, behind a hard barrier with a fail-fast-if-empty guard and an
/// enforcing FK. ModernCommodities has <b>no such dependency anywhere</b>: all three endpoints are
/// parameterised by <b>dates alone</b> and none returns an id another needs. Consequently there is
/// no reference provider, no read proc, no barrier and no FK — and a <c>Settlements</c>-only or
/// <c>MyTrades</c>-only run is fully valid (design §1.2).</para>
///
/// <para>The fixed execution order <c>AllTrades → MyTrades → Settlements</c> exists purely so a
/// run's log reads deterministically and the global request rate stays predictable against an
/// unpublished rate limit. <b>It is not load-bearing</b>: any order, or full concurrency, would be
/// equally correct, and a failure in one pipeline must not prevent the others from running.</para>
/// </summary>
public static class ModComDescriptors
{
    /// <summary>Anonymised market-wide trade tape. 34 columns, cap 10,000, 6-month history limit.</summary>
    public static readonly ModComEndpointDescriptor AllTrades = new(
        EndpointId: ModComEndpoints.AllTrades,
        DisplayName: "All Trades (anonymised market tape)",
        Path: "allTrades/v1",
        ParseShape: ModComParseShape.Trades,
        HistoryLimited: true,
        RowCap: 10_000,
        SupportsLegalEntityName: false,
        // Sized against the 170-day measurement (9,849 rows -> ~57.9/day), NEVER against the 5-day
        // capture (~19.6/day). The two do NOT reconcile (design §12 item 1) and the LONG window is
        // the one that actually approached the cap: 170 days = 98.5% of it, 172 days = over.
        EstimatedRowsPerCalendarDay: 58,
        TargetTable: "arm.AllTrades",
        TargetTvp: "arm.TradesTvp",
        TargetProc: "arm.usp_BulkMergeAllTrades");

    /// <summary>Fully attributed own-trades feed. Byte-identical 34-column header, cap 10,000, <b>all-time</b> history.</summary>
    public static readonly ModComEndpointDescriptor MyTrades = new(
        EndpointId: ModComEndpoints.MyTrades,
        DisplayName: "My Trades (fully attributed)",
        Path: "myTrades/v1",
        ParseShape: ModComParseShape.Trades,
        HistoryLimited: false,
        RowCap: 10_000,
        SupportsLegalEntityName: true,
        // 37 rows over 180 days. The cap is unreachable in practice (10 years ~= 750 rows ~= 7.5%).
        EstimatedRowsPerCalendarDay: 0.21,
        TargetTable: "arm.MyTrades",
        TargetTvp: "arm.TradesTvp",
        TargetProc: "arm.usp_BulkMergeMyTrades");

    /// <summary>Daily settlement curves. 9 columns, cap 100,000, 6-month history limit.</summary>
    public static readonly ModComEndpointDescriptor Settlements = new(
        EndpointId: ModComEndpoints.Settlements,
        DisplayName: "Daily Settlements",
        Path: "settlements/v1",
        ParseShape: ModComParseShape.Settlements,
        HistoryLimited: true,
        RowCap: 100_000,
        SupportsLegalEntityName: false,
        // ~721 rows per PUBLISHED date x ~0.7 business days per calendar day ~= 505/calendar day.
        // The naive 1443/5-calendar-days ~= 289 understates the real volume by ~2.5x, and a single
        // full-6-month request lands at ~94% of the cap - see design §3.4.
        EstimatedRowsPerCalendarDay: 505,
        TargetTable: "arm.Settlements",
        TargetTvp: "arm.SettlementsTvp",
        TargetProc: "arm.usp_BulkMergeSettlements");

    /// <summary>All three, in the fixed (cosmetic) execution order.</summary>
    public static readonly IReadOnlyList<ModComEndpointDescriptor> All = new[] { AllTrades, MyTrades, Settlements };
}
