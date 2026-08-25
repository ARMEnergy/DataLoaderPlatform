namespace DataLoader.ModernCommodities;

/// <summary>
/// The verbatim CSV header names, exactly as the vendor publishes them (design §5.2, §8).
///
/// <para><b>⚠⚠ 24 of the 34 trades headers contain a space; two contain an ampersand; one contains
/// a slash.</b>
/// <code>
/// "Pipeline/Terminal"   "GT&amp;C"   "Click &amp; Trade"
/// "Apportionment Protected"   "Last Updated Timestamp"   "Unit of Measure"
/// </code>
/// <b>No <c>System.Text.Json</c> / CSV naming policy matches these.</b> A property named
/// <c>PipelineTerminal</c>, <c>GTandC</c> or <c>ClickAndTrade</c> binds to <b>nothing</b> —
/// camelCase yields <c>pipelineTerminal</c>, snake_case <c>pipeline_terminal</c>, and neither is
/// <c>Pipeline/Terminal</c>. This is exactly the trap that produced a silently all-NULL table for
/// NGI (<c>"Point Code"</c>, <c>"Issue Date"</c>) and it must not recur: every field read goes
/// through <see cref="ModComHeaderMap"/> by <b>literal name</b>, never by ordinal literal and never
/// by implicit POCO binding.</para>
/// </summary>
internal static class ModComColumns
{
    // ---- trades (allTrades/v1 and myTrades/v1 — a BYTE-IDENTICAL 497-byte header) --------------
    public const string TradeNumber = "Trade Number";
    public const string State = "State";
    public const string Product = "Product";
    public const string Location = "Location";
    public const string PipelineTerminal = "Pipeline/Terminal";
    public const string PriceBasis = "Price Basis";
    public const string Term = "Term";
    public const string TermStart = "Term Start";
    public const string TermEnd = "Term End";
    public const string Price = "Price";
    public const string Volume = "Volume";
    public const string UnitOfMeasure = "Unit of Measure";
    public const string ExecutedTimestamp = "Executed Timestamp";
    public const string LastUpdatedTimestamp = "Last Updated Timestamp";
    public const string TradeType = "Trade Type";
    public const string Side = "Side";
    public const string BidTrader = "Bid Trader";
    public const string BidLegalName = "Bid Legal Name";
    public const string BidAddress = "Bid Address";
    public const string BidCommission = "Bid Commission";
    public const string OfferTrader = "Offer Trader";
    public const string OfferLegalName = "Offer Legal Name";
    public const string OfferAddress = "Offer Address";
    public const string OfferCommission = "Offer Commission";
    public const string SpreadTradeNumber = "Spread Trade Number";
    public const string ApportionmentProtected = "Apportionment Protected";
    public const string ClearingId = "Clearing ID";
    public const string SettlementCurrency = "Settlement Currency";
    public const string ContractTerms = "Contract Terms";
    public const string GTandC = "GT&C";
    public const string Notes = "Notes";
    public const string InIndex = "In Index";
    public const string ClickAndTrade = "Click & Trade";
    public const string ProductType = "Product Type";

    // ---- settlements (settlements/v1) ---------------------------------------------------------
    public const string SettlementDate = "Settlement Date";
    // Product / Location / Pipeline/Terminal / Price Basis / Term / Term Start / Term End / Price
    // reuse the trades constants above — the SOURCE header spells them identically. (Only the
    // TARGET column diverges: arm.Settlements spells it `PieplineTerminal` [sic] — decision D2.)

    /// <summary>The 34 trades headers in CSV order — which is also the target column order and the TVP order.</summary>
    public static readonly string[] Trades =
    {
        TradeNumber, State, Product, Location, PipelineTerminal, PriceBasis, Term, TermStart, TermEnd,
        Price, Volume, UnitOfMeasure, ExecutedTimestamp, LastUpdatedTimestamp, TradeType, Side,
        BidTrader, BidLegalName, BidAddress, BidCommission, OfferTrader, OfferLegalName, OfferAddress,
        OfferCommission, SpreadTradeNumber, ApportionmentProtected, ClearingId, SettlementCurrency,
        ContractTerms, GTandC, Notes, InIndex, ClickAndTrade, ProductType
    };

    /// <summary>The 9 settlements headers in CSV order.</summary>
    public static readonly string[] Settlements =
    {
        SettlementDate, Product, Location, PipelineTerminal, PriceBasis, Term, TermStart, TermEnd, Price
    };
}

/// <summary>
/// A <c>header name → field index</c> map built from record 0 of a ModCom CSV payload
/// (design §5.2).
///
/// <para>Matching is by <b>literal vendor name</b> after <see cref="string.Trim()"/>. The comparer
/// is <see cref="StringComparer.OrdinalIgnoreCase"/> — deliberately one notch more tolerant than an
/// exact ordinal match, so a casing-only vendor change cannot blank all 34 columns at once. What is
/// <b>not</b> negotiable is that the names are the vendor's literal strings (spaces, <c>&amp;</c>
/// and <c>/</c> included): no naming policy or convention is applied to them, ever.</para>
///
/// <para>Header drift is <b>tolerated, reported and never silently absorbed</b>: a missing
/// <b>key</b> column fails the pull loudly (the caller throws <see cref="ModComFailureKind.ShapeDrift"/>),
/// a missing <b>non-key</b> column maps to NULL for every row with one warning, and an unexpected
/// extra column is warned about once and ignored. Every drift note is also written into the
/// <c>arm.FileLog</c> row's <c>ErrorMessage</c>, because with no per-row provenance the hub row is
/// the only durable record that a pull was parsed against a drifted header.</para>
/// </summary>
internal sealed class ModComHeaderMap
{
    private readonly Dictionary<string, int> _index;

    private ModComHeaderMap(Dictionary<string, int> index, int fieldCount,
        IReadOnlyList<string> missing, IReadOnlyList<string> unexpected)
    {
        _index = index;
        FieldCount = fieldCount;
        MissingExpected = missing;
        Unexpected = unexpected;
    }

    /// <summary>How many fields the header record actually carried.</summary>
    public int FieldCount { get; }

    /// <summary>Expected columns absent from the header — each maps to NULL for every row.</summary>
    public IReadOnlyList<string> MissingExpected { get; }

    /// <summary>Header columns we did not expect — ignored.</summary>
    public IReadOnlyList<string> Unexpected { get; }

    /// <summary><c>true</c> when the header matched the expected set exactly.</summary>
    public bool IsClean => MissingExpected.Count == 0 && Unexpected.Count == 0;

    public bool Has(string column) => _index.ContainsKey(column);

    /// <summary>The field index for a column, or <c>-1</c> when the header did not carry it.</summary>
    public int IndexOf(string column) => _index.TryGetValue(column, out var i) ? i : -1;

    /// <summary>
    /// The raw (untrimmed) field for a column in one data record, or <c>null</c> when the column is
    /// absent from the header <b>or</b> the record is short. A short record therefore degrades the
    /// tail to NULL rather than throwing <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    public string? Raw(string[] record, string column)
    {
        var i = IndexOf(column);
        if (i < 0 || i >= record.Length) return null;
        return record[i];
    }

    /// <summary>
    /// Builds the map from the header record, classifying drift against <paramref name="expected"/>.
    /// A duplicate header name keeps the <b>first</b> occurrence (and is reported as unexpected), so
    /// a vendor emitting a column twice cannot silently swap which one is read.
    /// </summary>
    public static ModComHeaderMap Build(string[] headerRecord, IReadOnlyList<string> expected)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unexpected = new List<string>();
        var expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < headerRecord.Length; i++)
        {
            var name = headerRecord[i].Trim();
            if (name.Length == 0) continue;

            if (!index.TryAdd(name, i))
            {
                unexpected.Add($"{name} (duplicate at position {i + 1})");
                continue;
            }
            if (!expectedSet.Contains(name))
                unexpected.Add(name);
        }

        var missing = expected.Where(e => !index.ContainsKey(e)).ToList();
        return new ModComHeaderMap(index, headerRecord.Length, missing, unexpected);
    }

    /// <summary>
    /// One-line, log-safe summary of the drift (empty when clean). Header names carry no PII and no
    /// credential, so quoting them verbatim is safe and is the only way an operator can act on it.
    /// </summary>
    public string DriftNote(int expectedCount)
    {
        if (IsClean && FieldCount == expectedCount) return string.Empty;

        var parts = new List<string>();
        if (FieldCount != expectedCount)
            parts.Add($"columnCount={FieldCount} (expected {expectedCount})");
        if (MissingExpected.Count > 0)
            parts.Add("missing=[" + string.Join(", ", MissingExpected) + "]");
        if (Unexpected.Count > 0)
            parts.Add("unexpected=[" + string.Join(", ", Unexpected) + "]");
        return "HeaderDrift: " + string.Join("; ", parts);
    }
}
