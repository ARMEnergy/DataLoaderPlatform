namespace DataLoader.ModernCommodities;

// =============================================================================
// The two target row types.
//
// *** NEITHER CARRIES A FileLogId. ***  The user's authoritative DDL gives
// arm.AllTrades / arm.MyTrades / arm.Settlements no FileLogId, no surrogate Id
// and no DateCreated (decision D1), so provenance lives in arm.FileLog ALONE and
// both TVPs begin at the FIRST PAYLOAD COLUMN. That is a deliberate, stated break
// from the cross-loader "FileLogId is column 1 where present" rule; there is also
// no ICwgFactRow-style provenance marker interface and the readers stamp nothing
// onto rows after the hub upsert (design §6.1).
//
// Property ORDER in each type mirrors the CSV source order, which is also the
// target column order and the TVP order. That is documentation, not binding —
// the sink's BuildTable is what binds — but keeping them aligned is what makes a
// drift visible when reading the two side by side.
// =============================================================================

/// <summary>
/// One trade, shared by <b>both</b> trades endpoints and both trades tables (design §8.1).
///
/// <para>The <c>allTrades</c> and <c>myTrades</c> CSV headers are <b>byte-identical</b> (497 bytes,
/// 34 columns, same order — verified across three captures) and the user's DDL gives the two tables
/// identical column lists, so there is <b>one</b> row type, <b>one</b> reader class, <b>one</b>
/// <c>BuildTable</c> and <b>one</b> TVP. Two 34-column definitions kept in sync by hand is exactly
/// the drift a positional TVP contract cannot survive.</para>
///
/// <para><b>The 14 anonymised columns are read the same way for both endpoints</b> — they simply
/// arrive empty from <c>allTrades</c> and populate from <c>myTrades</c>. Do <b>not</b> branch on the
/// endpoint, and do not "optimise" them away from <c>arm.AllTrades</c>: the venue could begin
/// populating some of them and a narrower table would silently discard data. 100 % NULL there is
/// <i>correct</i>, and the validator reports it informationally (design §9 check 9).</para>
/// </summary>
public sealed class TradeRow
{
    /// <summary>CSV 1 — <c>Trade Number</c>. <b>PK and merge key.</b> A row without it is dropped-and-counted.</summary>
    public required int TradeNumber { get; init; }

    /// <summary>CSV 2 — <c>Finalized</c>/<c>Cancelled</c>. <b>A revision can flip this</b>; that is the whole point of the hourly re-pull.</summary>
    public string? State { get; init; }

    /// <summary>CSV 3 — <c>/</c>-joined on spread rows. Observed max 28 of 50 — the tightest width ratio.</summary>
    public string? Product { get; init; }

    /// <summary>CSV 4 — blank on exactly the <c>Financial</c> rows. <b>Semantic, not a defect</b> (a financial contract has no delivery location).</summary>
    public string? Location { get; init; }

    /// <summary>CSV 5 — source header <c>Pipeline/Terminal</c> (contains a slash). Spelled <b>correctly</b> here; contrast <see cref="SettlementRow.PieplineTerminal"/>.</summary>
    public string? PipelineTerminal { get; init; }

    /// <summary>CSV 6 — free text (a <c> + </c>-joined index expression, observed max 45). <b>Not the same value space as the settlements <c>PriceBasis</c></b> — no shared dimension.</summary>
    public string? PriceBasis { get; init; }

    /// <summary>CSV 7 — four shapes, two separators: <c>MMM-YY</c>, <c>nQ-YY</c>, <c>MMM-YY/MMM-YY</c>, <c>MMM-YY~MMM-YY</c>. Persisted as published; never parsed.</summary>
    public string? Term { get; init; }

    /// <summary>CSV 8 — always the 1st of a month.</summary>
    public DateOnly? TermStart { get; init; }

    /// <summary>CSV 9 — always a month end; leap-year correct.</summary>
    public DateOnly? TermEnd { get; init; }

    /// <summary>CSV 10 — <b>SIGNED</b> (negative = a differential); <c>0.00</c> is a real value.</summary>
    public decimal? Price { get; init; }

    /// <summary>CSV 11 — integer-valued in every observed row; observed max 300,000 against a 9,999,999.99 ceiling (~33× headroom).</summary>
    public decimal? Volume { get; init; }

    /// <summary>CSV 12 — <c>m3/month</c>, <c>bbls/day</c>, <c>bbls/month</c>, <c>contracts/month</c>. No enum: a new unit must load.</summary>
    public string? UnitOfMeasure { get; init; }

    /// <summary>CSV 13 — <c>Executed Timestamp</c>, 12-hour <c>tt</c>. <b>NOT the window predicate</b>; rows outside the window on this column are the revision signal.</summary>
    public DateTime? Executed { get; init; }

    /// <summary>CSV 14 — <c>Last Updated Timestamp</c>, 12-hour <c>tt</c>. <b>THIS is the window predicate AND the MERGE recency guard.</b></summary>
    public DateTime? LastUpdated { get; init; }

    /// <summary>CSV 15 — <c>Outright</c>/<c>Spread</c>/<c>First Leg</c>/<c>Second Leg</c>.</summary>
    public string? TradeType { get; init; }

    /// <summary>CSV 16 — ANONYMISED in <c>allTrades</c>; <c>Buy</c>/<c>Sell</c> in <c>myTrades</c>.</summary>
    public string? Side { get; init; }

    /// <summary>CSV 17 — ANONYMISED. <b>PII</b> — never log the value.</summary>
    public string? BidTrader { get; init; }

    /// <summary>CSV 18 — ANONYMISED. <b>Contains commas</b> (<c>ARM Energy Management, LLC</c>).</summary>
    public string? BidLegalName { get; init; }

    /// <summary>CSV 19 — ANONYMISED. Up to 4 embedded commas.</summary>
    public string? BidAddress { get; init; }

    /// <summary>CSV 20 — ANONYMISED and sparse even in <c>myTrades</c> (the company's own side only).</summary>
    public decimal? BidCommission { get; init; }

    /// <summary>CSV 21 — ANONYMISED. <b>PII.</b></summary>
    public string? OfferTrader { get; init; }

    /// <summary>CSV 22 — ANONYMISED. Contains commas.</summary>
    public string? OfferLegalName { get; init; }

    /// <summary>CSV 23 — ANONYMISED. Contains commas.</summary>
    public string? OfferAddress { get; init; }

    /// <summary>CSV 24 — ANONYMISED and sparse. <b>Complementary to <see cref="BidCommission"/></b> (16 + 21 = 37) — never build a "both present" rule.</summary>
    public decimal? OfferCommission { get; init; }

    /// <summary>CSV 25 — NOT anonymised. Numeric-looking but <b>stays text</b> per the DDL: it is a reference, not a measure. <b>Do not "fix" it to <c>INT</c>.</b></summary>
    public string? SpreadTradeNumber { get; init; }

    /// <summary>CSV 26 — <c>BIT</c>; <c>True</c> never observed. Blank → <c>null</c>.</summary>
    public bool? ApportionmentProtected { get; init; }

    /// <summary>CSV 27 — <b>blank in 100 % of BOTH endpoints; NOT an anonymised column</b>. Expected-NULL everywhere — never assert it populated.</summary>
    public string? ClearingID { get; init; }

    /// <summary>CSV 28 — ANONYMISED. Only <c>USD</c> observed; <b>do not constrain to it</b> (a Canadian venue may settle <c>CAD</c>).</summary>
    public string? SettlementCurrency { get; init; }

    /// <summary>CSV 29 — ANONYMISED. Contains commas. <i>"Whose paper governs"</i>, <b>not</b> a counterparty identifier.</summary>
    public string? ContractTerms { get; init; }

    /// <summary>CSV 30 — ANONYMISED. Source header is <c>GT&amp;C</c> (ampersand, no spaces).</summary>
    public string? GTandC { get; init; }

    /// <summary>CSV 31 — ANONYMISED and very sparse free operator text. <b>PII-adjacent</b> — never log the value, only its length.</summary>
    public string? Notes { get; init; }

    /// <summary>CSV 32 — <c>BIT</c>. Not anonymised.</summary>
    public bool? InIndex { get; init; }

    /// <summary>CSV 33 — ANONYMISED. Source header <c>Click &amp; Trade</c> (ampersand AND spaces). <b>Blank → <c>null</c>, not <c>false</c></b>.</summary>
    public bool? ClickAndTrade { get; init; }

    /// <summary>CSV 34 — <c>Physical</c>/<c>Financial</c>. The discriminator for the blankness of CSV 4/5/6.</summary>
    public string? ProductType { get; init; }
}

/// <summary>
/// One settlement curve point (design §8.2). <b>Six of the nine columns form the PRIMARY KEY</b>
/// and are all <c>NOT NULL</c>: <c>SettlementDate, Product, Location, PieplineTerminal, PriceBasis,
/// Term</c>. A record blank in any of them is dropped-and-counted rather than inserted with an
/// empty-string key.
/// </summary>
public sealed class SettlementRow
{
    /// <summary>CSV 1 — <b>PK 1/6</b>. Also this endpoint's window predicate (exact, no leakage).</summary>
    public required DateOnly SettlementDate { get; init; }

    /// <summary>CSV 2 — <b>PK 2/6</b>.</summary>
    public required string Product { get; init; }

    /// <summary>
    /// CSV 3 — <b>PK 3/6</b>. ⚠ Carries the <b>literal <c>-</c></b> on 82 of 1,443 rows (always
    /// paired with <see cref="PieplineTerminal"/>, all <c>Product = 'Sweet Guernsey Blend'</c>).
    /// <b>Persisted VERBATIM</b> — it is a key VALUE, not a null sentinel.
    /// </summary>
    public required string Location { get; init; }

    /// <summary>
    /// CSV 4 — <b>PK 4/6</b>. <b>⚠ The misspelling is intentional and load-bearing [sic].</b>
    ///
    /// <para>The source header is <c>Pipeline/Terminal</c>, but the user's authoritative DDL spells
    /// the <b>column</b> <c>PieplineTerminal</c> — and it is <b>in the PRIMARY KEY</b>. Decision D2
    /// reproduces that spelling exactly in the table, the TVP, the merge proc <b>and this C#
    /// property</b>, precisely so nobody later "tidies" the C# name: the TVP binds <b>by
    /// position</b>, so a rename here would break the binding silently. The two trades tables spell
    /// the same concept <i>correctly</i> (<see cref="TradeRow.PipelineTerminal"/>) — both spellings
    /// coexist on purpose.</para>
    /// </summary>
    public required string PieplineTerminal { get; init; }

    /// <summary>
    /// CSV 5 — <b>PK 5/6</b>. Only two values observed: <c>WTI CMA</c> and <c>USD $</c>.
    /// ⚠ <c>USD $</c> contains a space and a <c>$</c> — <b>no trimming, stripping or normalisation
    /// beyond the outer <see cref="string.Trim()"/></b>: it is a key.
    /// </summary>
    public required string PriceBasis { get; init; }

    /// <summary>CSV 6 — <b>PK 6/6</b>. <b>Always a single <c>MMM-YY</c> month</b> here — never <c>/</c>, <c>~</c> or a quarter (contrast <see cref="TradeRow.Term"/>).</summary>
    public required string Term { get; init; }

    /// <summary>CSV 7 — never blank in 1,443 rows, but the DDL column is NULLable and stays so (only an unusable KEY drops a row).</summary>
    public DateOnly? TermStart { get; init; }

    /// <summary>CSV 8 — extends to <c>2031-12-31</c> (the curve runs ≈5.4 years forward). <b>No tighter date-range assumption anywhere.</b></summary>
    public DateOnly? TermEnd { get; init; }

    /// <summary>CSV 9 — <b>SIGNED: negative in 1,071 of 1,443 rows (74 %)</b>; <c>0.00</c> is real. No non-negative <c>CHECK</c>.</summary>
    public decimal? Price { get; init; }
}
