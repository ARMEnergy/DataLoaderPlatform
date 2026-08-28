namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Endpoint ids — the <c>EnabledEndpoints</c> values, the <see cref="IEvoPipeline.EndpointId"/>
/// values and the <c>arm.Endpoint</c> seed names, all one set of literals so they cannot drift
/// (design §1.4, §6). <c>arm.usp_UpsertFileLog</c> rejects any other name.
/// </summary>
internal static class EvoEndpoints
{
    /// <summary>
    /// <c>GET /v1/market-data/history</c> — the only in-scope endpoint.
    ///
    /// <para>The sibling <c>GET /v1/market-data</c> (a same-day snapshot of the most recent business
    /// date) is deliberately OUT of scope: the loader spec names the history path, and the snapshot
    /// carries no data the history endpoint does not also serve one day later. See design §12 item 2
    /// for the one thing it would add (same-day latency) if that is ever wanted.</para>
    /// </summary>
    public const string MarketDataHistory = "MarketDataHistory";
}

/// <summary>
/// The three <c>arm.Status</c> labels <c>arm.usp_UpsertFileLog</c> accepts (design §6). Anything
/// else raises server-side, so they are constants rather than inline strings.
/// </summary>
internal static class EvoFileStatus
{
    public const string Success = "Success";

    /// <summary>
    /// A legitimate empty read: <c>200</c> with <c>[]</c>. <b>Routine, not a fault</b> — it is what
    /// every weekend, US market holiday and pre-retention date returns (design §5.4). The work unit
    /// SUCCEEDS with <c>RowCount = 0</c>.
    /// </summary>
    public const string NotAvailable = "NotAvailable";

    public const string Failed = "Failed";
}

// =============================================================================
// *** THE TWO FIELD VOCABULARIES — READ THIS BEFORE TOUCHING EITHER LIST ***
//
// This API uses TWO DIFFERENT SPELLINGS for the same field, and they are NOT
// interchangeable:
//
//   REQUEST side  (`&field=` values)  -> the "output"/CSV names, e.g. marketDataId,
//                                        instrumentName, priceTS, businessDate,
//                                        pct_ret_daily.
//   RESPONSE side (the JSON keys)     -> the `GET /v1/datasets` responseFields
//                                        names, e.g. priceId, instrument, priceTs,
//                                        date, pctRetDaily.
//
// So a round trip legitimately looks like:
//     ask for  field=marketDataId   ->  get back  {"priceId": "..."}
//     ask for  field=businessDate   ->  get back  {"date": "..."}
//
// Asking with the RESPONSE spelling silently returns NOTHING for that field
// (`field=priceId` yields no priceId at all), and an outright unknown name returns
// HTTP 500, not 400. Both were verified live on 2026-08-25 — see docs/apis §4.
//
// EvoRequestFields below is the REQUEST vocabulary. EvoFields is the RESPONSE
// vocabulary. Do not merge them, and do not "correct" either one to match the other.
// =============================================================================

/// <summary>
/// The <c>&amp;field=</c> REQUEST projection — the vendor's <b>output/CSV</b> names.
///
/// <para><b>⚠⚠ THE LIST IS PINNED AND ORDER-SENSITIVE. DO NOT EDIT IT PIECEMEAL. ⚠⚠</b></para>
///
/// <para>Two vendor-side bugs make this list load-bearing rather than cosmetic (docs/apis §4.3):</para>
/// <list type="number">
///   <item><b><c>change</c> only resolves correctly in the FULL list.</b> Requested in a short list
///     — <c>field=marketDataId,change</c>, or
///     <c>field=marketDataId,ask,bid,change,currency</c> — the server returns <c>term</c> in its
///     place (a STRING where a decimal belongs) and no <c>change</c> key at all. In the full
///     23-name list it returns the genuine value (verified: 196 of 205 rows differ from
///     <c>mid</c>). Trimming this list to "just what we need" therefore silently NULLs
///     <c>Change</c>.</item>
///   <item><b>An unknown name is a 500, not a 400.</b> <c>field=pctRetDaily</c> (the response
///     spelling) returns <c>{"message":"Something went wrong."}</c> with HTTP 500 — which Polly
///     treats as transient and retries, so a typo here costs the full retry budget on every single
///     work unit before failing.</item>
/// </list>
///
/// <para><b>Why request fields the dataset never fills.</b> Nine of these 23
/// (<c>term2</c>, <c>instrumentSourceName</c>, <c>size</c>, <c>depth</c>, <c>price</c>,
/// <c>askSize</c>, <c>bidSize</c>, <c>midSize</c>, <c>pct_ret_daily</c>) returned no value on any of
/// the 8,118 rows available for <c>EVOID/USNaturalGasIndex</c>. They stay in the list on purpose:
/// they are real columns of the vendor's <c>TransformedDataItem</c> contract that <i>other</i>
/// datasets populate, requesting them is free, and dropping them would mean a silent gap the day the
/// key gains a permission. See <see cref="EvoFields"/> for the response-side census.</para>
///
/// <para>Omitting <c>field</c> entirely is NOT an option: the default projection returns only 10
/// fields and silently drops <c>market</c>, <c>priceType</c>, <c>currency</c> and <c>change</c>
/// (docs/apis §4.1).</para>
/// </summary>
internal static class EvoRequestFields
{
    /// <summary>
    /// The 23 REQUEST names, in the exact live-verified order. <b>Pinned</b> — a unit test asserts
    /// this array and <see cref="Csv"/> against the recorded contract so neither can drift silently.
    /// </summary>
    public static readonly string[] All =
    {
        "marketDataId",          // -> response key "priceId"        (the PK; NOT obtainable as "priceId")
        "market",                // -> "market"
        "term",                  // -> "term"
        "term2",                 // -> "term2"           (never populated for USNaturalGasIndex)
        "tenor",                 // -> "tenor"           (populated on ~47% of rows)
        "instrumentSourceName",  // -> "instrumentSourceName" (never populated)
        "instrumentId",          // -> "instrumentId"
        "instrumentName",        // -> response key "instrument"
        "priceTS",               // -> response key "priceTs"
        "businessDate",          // -> response key "date"
        "priceType",             // -> "priceType"
        "size",                  // -> "size"            (never populated)
        "depth",                 // -> "depth"           (never populated)
        "price",                 // -> "price"           (never populated)
        "ask",                   // -> "ask"
        "askSize",               // -> "askSize"         (never populated)
        "bid",                   // -> "bid"
        "bidSize",               // -> "bidSize"         (never populated)
        "mid",                   // -> "mid"
        "midSize",               // -> "midSize"         (never populated)
        "change",                // -> "change"          *** ONLY correct in this FULL list ***
        "pct_ret_daily",         // -> "pctRetDaily"     (never populated; note the SNAKE_CASE request name)
        "currency"               // -> "currency"
    };

    /// <summary>
    /// The pinned list as the literal query-parameter value. Built once — every request uses this
    /// exact string, so no call site can assemble a partial projection.
    /// </summary>
    public static readonly string Csv = string.Join(",", All);

    /// <summary>
    /// The response keys that MUST appear on a populated row for the projection to be considered
    /// intact. Checked once per non-empty page by the reader, which warns loudly if one is missing —
    /// the observable signature of the <c>change</c> bug or of a vendor-side rename (design §5.6).
    ///
    /// <para>Deliberately does NOT include the nine never-populated fields, nor <c>tenor</c> (legitimately
    /// absent on ~53% of rows), which would make the guard fire on every healthy load.</para>
    /// </summary>
    public static readonly string[] ExpectedResponseKeys =
    {
        "priceId", "market", "term", "instrumentId", "instrument",
        "priceTs", "date", "priceType", "ask", "bid", "mid", "change", "currency"
    };
}

/// <summary>
/// The candidate JSON property-name sets the reader binds against — the RESPONSE vocabulary
/// (design §5.1). The <b>live-verified spelling is always FIRST</b>; the remaining entries are the
/// tolerant fallbacks the platform convention asks for.
///
/// <para><b>Bind by NAME, never by position.</b> JSON object key order is not a contract, and this
/// API demonstrably reorders: <c>tenor</c> appears mid-object on rows that have it and is absent
/// entirely on rows that do not, so no two rows are guaranteed the same shape.</para>
///
/// <para><b>Why the fallback candidates are not paranoia here.</b> Several response keys already
/// disagree with the vendor's own CSV header for the same field (<c>priceId</c> vs
/// <c>marketDataId</c>, <c>instrument</c> vs <c>instrumentName</c>, <c>date</c> vs
/// <c>businessDate</c>, <c>pctRetDaily</c> vs <c>pct_ret_daily</c>). Both spellings of each are
/// listed so the loader survives the vendor settling on either one.</para>
/// </summary>
internal static class EvoFields
{
    /// <summary>
    /// The row id → <c>arm.MarketData.MarketDataId</c>, the PRIMARY KEY and merge key.
    ///
    /// <para><b>The live JSON key is <c>priceId</c>; the same value appears as <c>marketDataId</c> in
    /// the CSV and XML renderings of the identical row</b> (verified byte-for-byte on
    /// <c>6d11bc3c-a601-480a-8032-b6d42068b9ea</c>). They are ONE field, which is why the target
    /// column is named <c>MarketDataId</c> while the JSON reader looks for <c>priceId</c> first.</para>
    /// </summary>
    public static readonly string[] MarketDataId = { "priceId", "marketDataId", "price_id", "market_data_id" };

    public static readonly string[] Market = { "market" };
    public static readonly string[] Term = { "term" };
    public static readonly string[] Term2 = { "term2" };
    public static readonly string[] Tenor = { "tenor" };
    public static readonly string[] InstrumentSourceName = { "instrumentSourceName", "instrument_source_name" };
    public static readonly string[] InstrumentId = { "instrumentId", "instrument_id" };

    /// <summary>Live key <c>instrument</c>; the CSV/XML rendering calls the same field <c>instrumentName</c>.</summary>
    public static readonly string[] InstrumentName = { "instrument", "instrumentName", "instrument_name" };

    /// <summary>
    /// Live key <c>priceTs</c> (CSV: <c>priceTS</c>). An ISO-8601 instant with an explicit <c>Z</c>,
    /// e.g. <c>2026-08-24T00:00:00.000Z</c> — every observed value is exactly midnight UTC.
    /// <b>Stored as UTC</b> in <c>DATETIME2(0)</c>; the milliseconds are always <c>.000</c> and are
    /// discarded by the column's zero scale.
    /// </summary>
    public static readonly string[] PriceTs = { "priceTs", "priceTS", "price_ts" };

    /// <summary>
    /// Live key <c>date</c> (CSV: <c>businessDate</c>) — the bare <c>yyyy-MM-dd</c> business date, and
    /// the value the work unit requested. Persisted to <c>BusinessDate DATE</c>.
    /// </summary>
    public static readonly string[] BusinessDate = { "date", "businessDate", "business_date" };

    /// <summary>
    /// Live key <c>priceType</c>. Observed value: the single string <c>Indicative</c> on all 8,118
    /// rows. <b>Not an enum in any vendor document</b> — persisted as published, never validated
    /// against a list, and there is deliberately no lookup table and no CHECK constraint.
    /// </summary>
    public static readonly string[] PriceType = { "priceType", "price_type" };

    public static readonly string[] Size = { "size" };
    public static readonly string[] Depth = { "depth" };
    public static readonly string[] Price = { "price" };
    public static readonly string[] Ask = { "ask" };
    public static readonly string[] AskSize = { "askSize", "ask_size" };
    public static readonly string[] Bid = { "bid" };
    public static readonly string[] BidSize = { "bidSize", "bid_size" };
    public static readonly string[] Mid = { "mid" };
    public static readonly string[] MidSize = { "midSize", "mid_size" };

    /// <summary>
    /// Live key <c>change</c>. <b>⚠ Only returned correctly when the full pinned
    /// <see cref="EvoRequestFields.All"/> projection is requested</b> — see that type's remarks. In a
    /// short projection the server sends <c>term</c> instead, which the tolerant decimal parse
    /// degrades to <c>NULL</c> rather than corrupting (a string can never become a decimal), so the
    /// failure mode is a silent column of NULLs, not bad numbers.
    /// </summary>
    public static readonly string[] Change = { "change" };

    /// <summary>Live response spelling <c>pctRetDaily</c>; the REQUEST name is the snake_case <c>pct_ret_daily</c>.</summary>
    public static readonly string[] PctRetDaily = { "pctRetDaily", "pct_ret_daily" };

    /// <summary>Observed value: <c>USD</c> on all 8,118 rows. Free text, not an enum — no CHECK constraint.</summary>
    public static readonly string[] Currency = { "currency" };
}
