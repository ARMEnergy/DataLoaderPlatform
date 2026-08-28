namespace DataLoader.EvolutionMarkets;

// =============================================================================
// Target row — the source reader builds these directly (the identity transform passes
// them straight to the sink). Each row carries its FileLogId, stamped by the reader
// AFTER the arm.FileLog upsert returns, and its Checksum, computed by the reader once
// the payload fields are all set.
//
// Property order below mirrors (and documents) the 25-column TVP contract in
// sql/EvolutionMarkets/002. FileLogId is FIRST, per the cross-loader convention
// ("FileLogId first where the table carries provenance") that NGI follows.
//
// DateCreated (table DEFAULT) and ModifiedAtUtc (stamped by the MERGE proc) NEVER
// cross the TVP. The merge proc takes no scalar alongside its TVP.
// =============================================================================

/// <summary>
/// <c>arm.MarketData</c> — the Evolution Markets price fact, one row per
/// <c>GET /v1/market-data/history</c> record. PK / merge key <see cref="MarketDataId"/>.
///
/// <para><b>The merge key is a vendor-supplied surrogate, and that is unusual enough to state.</b>
/// Most facts in this repo key on a composite natural key. Here the vendor hands us a stable
/// <c>UNIQUEIDENTIFIER</c> per (instrument, term, tenor, business date) — verified unique across all
/// 8,118 rows of available history, and verified to be the SAME id when a date is re-pulled — so it
/// is both the natural and the surrogate key. The composite alternative
/// <c>(InstrumentId, Term, Tenor, BusinessDate)</c> was ALSO verified unique over the same 8,118
/// rows and is what <c>arm.usp_ValidateLoad</c> reconciles against, so a vendor-side id change would
/// be caught rather than silently duplicating rows.</para>
///
/// <para><b>⚠ Ten of the 23 payload columns are ALWAYS NULL for the only permissioned dataset</b>
/// (<c>EVOID/USNaturalGasIndex</c>): <see cref="Term2"/>, <see cref="InstrumentSourceName"/>,
/// <see cref="Size"/>, <see cref="Depth"/>, <see cref="Price"/>, <see cref="AskSize"/>,
/// <see cref="BidSize"/>, <see cref="MidSize"/> and <see cref="PctRetDaily"/> returned no value on
/// any row, and <see cref="Tenor"/> is populated on only ~47%. That is <b>normal, not a bad load</b>:
/// they are columns of the vendor's documented <c>TransformedDataItem</c> contract that other
/// datasets (carbon, power/nodal) populate. They are requested anyway and modelled anyway so a new
/// entitlement needs no migration. <c>arm.usp_ValidateLoad</c> reports them observationally.</para>
/// </summary>
public sealed class MarketDataRow
{
    /// <summary>TVP column 1. Provenance only — UPDATEd on match, NEVER part of the merge key.</summary>
    public int FileLogId { get; set; }

    /// <summary>
    /// TVP column 2 — <b>the merge key / PRIMARY KEY</b>. Read from the JSON key <c>priceId</c>
    /// (which the vendor's own CSV and XML renderings of the same row call <c>marketDataId</c>).
    ///
    /// <para>Non-nullable: the reader DROPS-AND-COUNTS any record whose id is missing or unparseable,
    /// because an unkeyable row cannot be merged. Everything else degrades to NULL.</para>
    /// </summary>
    public required Guid MarketDataId { get; init; }

    /// <summary>TVP column 3 — <c>market</c>. The dataset's display name, e.g. <c>US Natural Gas Index</c>.
    /// Only returned when the pinned <c>field</c> projection is sent; the default projection omits it.</summary>
    public string? Market { get; init; }

    /// <summary>TVP column 4 — <c>term</c>. The contract period AS PUBLISHED, e.g. <c>Sep'26-Oct'26</c>,
    /// <c>2026-Winter</c>, <c>2026-Oct</c>. Free text in several distinct shapes: never parse it, never
    /// normalise it, and never derive a date from it.</summary>
    public string? Term { get; init; }

    /// <summary>TVP column 5 — <c>term2</c>. Never populated for <c>EVOID/USNaturalGasIndex</c> (a
    /// spread's far leg on datasets that have one).</summary>
    public string? Term2 { get; init; }

    /// <summary>TVP column 6 — <c>tenor</c>. Populated on ~47% of rows (values <c>2m</c>..<c>5m</c>);
    /// legitimately ABSENT on the rest, so its absence must never be treated as drift. Part of the
    /// composite key <c>arm.usp_ValidateLoad</c> reconciles against.</summary>
    public string? Tenor { get; init; }

    /// <summary>TVP column 7 — <c>instrumentSourceName</c>. Never populated for this dataset.</summary>
    public string? InstrumentSourceName { get; init; }

    /// <summary>TVP column 8 — <c>instrumentId</c>. The stable per-product id; 41 distinct values
    /// observed. Nullable per the tolerant-parse contract even though it was never null in 8,118 rows.</summary>
    public Guid? InstrumentId { get; init; }

    /// <summary>TVP column 9 — the JSON key <c>instrument</c> (CSV: <c>instrumentName</c>), e.g.
    /// <c>Socal-Border Index Futures</c>. Observed max length 63.</summary>
    public string? InstrumentName { get; init; }

    /// <summary>
    /// TVP column 10 — <c>priceTs</c>, stored as <b>UTC</b> in <c>DATETIME2(0)</c>.
    ///
    /// <para>The source is an ISO-8601 instant with an explicit <c>Z</c>; every observed value is
    /// exactly midnight UTC and equal to <see cref="BusinessDate"/>. The <c>.000</c> milliseconds are
    /// discarded by the column's zero scale, which is why <see cref="EvoChecksum"/> hashes this at
    /// second precision.</para>
    /// </summary>
    public DateTime? PriceTs { get; init; }

    /// <summary>
    /// TVP column 11 — the JSON key <c>date</c> (CSV: <c>businessDate</c>): the bare
    /// <c>yyyy-MM-dd</c> business date, and the value this work unit requested. <c>DATE</c>, not
    /// <c>DATETIME2</c> — there is no time component and no timezone on this field anywhere in the feed.
    /// </summary>
    public DateOnly? BusinessDate { get; init; }

    /// <summary>TVP column 12 — <c>priceType</c>. Observed: the single value <c>Indicative</c> on all
    /// 8,118 rows. <b>Not an enum in any vendor document</b> — no lookup table, no CHECK constraint;
    /// a new value is reported by the validator, never rejected.</summary>
    public string? PriceType { get; init; }

    /// <summary>TVP column 13 — <c>size</c>. Never populated for this dataset.</summary>
    public int? Size { get; init; }

    /// <summary>TVP column 14 — <c>depth</c>. Never populated for this dataset.</summary>
    public int? Depth { get; init; }

    /// <summary>TVP column 15 — <c>price</c>. Never populated for this dataset: it is an index feed
    /// quoted as a bid/ask/mid spread, so the outright <c>price</c> slot stays empty. Do NOT
    /// backfill it from <see cref="Mid"/> — that would invent a print the vendor never made.</summary>
    public decimal? Price { get; init; }

    /// <summary>TVP column 16 — <c>ask</c>. <c>DECIMAL(18,8)</c>, SIGNED: these are basis
    /// differentials and NEGATIVE VALUES ARE ROUTINE (observed on many points).</summary>
    public decimal? Ask { get; init; }

    /// <summary>TVP column 17 — <c>askSize</c>. Never populated for this dataset.</summary>
    public int? AskSize { get; init; }

    /// <summary>TVP column 18 — <c>bid</c>. Signed; may equal <see cref="Ask"/> on a locked market.</summary>
    public decimal? Bid { get; init; }

    /// <summary>TVP column 19 — <c>bidSize</c>. Never populated for this dataset.</summary>
    public int? BidSize { get; init; }

    /// <summary>TVP column 20 — <c>mid</c>. Taken from the feed and <b>never computed</b>: the
    /// published value is not always the exact <c>(bid+ask)/2</c> midpoint (the vendor rounds to
    /// 4 dp, e.g. bid <c>-0.0325</c> / ask <c>-0.02</c> publishes mid <c>-0.0263</c>).</summary>
    public decimal? Mid { get; init; }

    /// <summary>TVP column 21 — <c>midSize</c>. Never populated for this dataset.</summary>
    public int? MidSize { get; init; }

    /// <summary>
    /// TVP column 22 — <c>change</c>. The day-over-day move; signed, and legitimately <c>0</c> on a
    /// point that did not move.
    ///
    /// <para><b>⚠ Only returned correctly when the FULL pinned <c>field</c> projection is sent</b> —
    /// see <see cref="EvoRequestFields"/>. In a short projection the vendor substitutes the
    /// <c>term</c> STRING for it, which the tolerant decimal parse degrades to <c>NULL</c>; the
    /// failure mode is therefore a silent column of NULLs, never a wrong number. The reader's
    /// response-shape guard warns when the key is missing from a populated page.</para>
    /// </summary>
    public decimal? Change { get; init; }

    /// <summary>TVP column 23 — the JSON key <c>pctRetDaily</c> (REQUEST name <c>pct_ret_daily</c>).
    /// Never populated for this dataset.</summary>
    public decimal? PctRetDaily { get; init; }

    /// <summary>TVP column 24 — <c>currency</c>. Observed: <c>USD</c> on all 8,118 rows. Free text.</summary>
    public string? Currency { get; init; }

    /// <summary>
    /// TVP column 25 — the change-detection hash over columns 3..24, computed by
    /// <see cref="EvoChecksum.Compute"/> (design §8.4). Settable because the reader assigns it once
    /// the payload fields are populated.
    ///
    /// <para>The merge proc UPDATEs a matched row <b>only</b> when this differs, so
    /// <c>ModifiedAtUtc</c> tracks when the price last actually changed rather than when the loader
    /// last ran. It is NOT part of the merge key and NOT an integrity checksum.</para>
    /// </summary>
    public int Checksum { get; set; }
}
