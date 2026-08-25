using System.Reflection;
using System.Text;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The committed CSV fixtures (under <c>Samples\</c>, copied next to the test assembly by the
/// csproj) plus the small synthetic literals the edge-case tests need.
///
/// <list type="bullet">
///   <item><b><c>allTrades_live_slice.csv</c></b> - a VERBATIM slice of a real live <c>200</c>:
///     header + 9 real rows. The vendor anonymises all 14 counterparty columns on this endpoint,
///     so the payload carries no PII and needed no editing. LF line endings and <b>no trailing
///     newline</b>, exactly as the live body.</item>
///   <item><b><c>settlements_live_slice.csv</c></b> - a VERBATIM slice of a real live <c>200</c>:
///     header + 8 real rows across TWO settlement dates, including the two <c>-</c>/<c>-</c>
///     Sweet Guernsey Blend rows and two <c>USD $</c> price-basis rows. A trailing newline is
///     deliberately present.</item>
///   <item><b><c>myTrades_header_only.csv</c></b> - the REAL header-only <c>200</c> body,
///     byte-for-byte (498 bytes): the legitimate "nothing to report" answer.</item>
///   <item><b><c>myTrades_anonymised.csv</c></b> - <b>ANONYMISED</b> (decision D9). The live
///     myTrades capture carries real trader names, counterparty legal entities and street
///     addresses, so every name/company/address here is INVENTED - while the SHAPE is preserved
///     exactly: comma-laden quoted addresses, a comma inside a legal name, PM and noon-hour
///     (<c>12:42:37 PM</c>) timestamps, blank commissions, a negative price, <c>True</c>/
///     <c>False</c> bits and a 3-row spread group. <see cref="FixtureFactsTests"/> asserts those
///     traps are still present, so the fixture cannot be sanitised into uselessness.</item>
/// </list>
///
/// <para>Everything is read from disk, never from the network, so the whole suite runs offline and
/// repeatably. <b>No credential appears in any fixture</b> - the ModCom credential is an HTTP Basic
/// request header and never rides a URL or a payload.</para>
/// </summary>
internal static class Samples
{
    // ---- fixture files -------------------------------------------------------------------------

    public const string AllTradesFileName = "allTrades_live_slice.csv";
    public const string SettlementsFileName = "settlements_live_slice.csv";
    public const string MyTradesHeaderOnlyFileName = "myTrades_header_only.csv";
    public const string MyTradesAnonymisedFileName = "myTrades_anonymised.csv";

    /// <summary>Real live allTrades slice - header + 9 rows.</summary>
    public static string AllTradesLive => Read(AllTradesFileName);

    /// <summary>Real live settlements slice - header + 8 rows over two settlement dates.</summary>
    public static string SettlementsLive => Read(SettlementsFileName);

    /// <summary>The real header-only 200 body (the legitimate empty read).</summary>
    public static string MyTradesHeaderOnly => Read(MyTradesHeaderOnlyFileName);

    /// <summary>The anonymised myTrades slice - header + 5 rows (shape-preserving, invented identities).</summary>
    public static string MyTradesAnonymised => Read(MyTradesAnonymisedFileName);

    // ---- fixture facts, asserted directly (see FixtureFactsTests) -------------------------------

    public const int AllTradesRowCount = 9;
    public const int AllTradesNegativePriceCount = 3;      // 68043, 68041, 67989
    public const int AllTradesPmTimestampCount = 4;        // 68043, 68041, 68020, 67989
    public const int AllTradesFinancialRowCount = 2;       // 68004, 67989 - blank Location/Pipeline/PriceBasis
    public const int AllTradesInIndexTrueCount = 1;        // 68028

    public const int SettlementsRowCount = 8;
    public const int SettlementsDashKeyRowCount = 2;       // Location AND PieplineTerminal are the literal '-'
    public const int SettlementsUsdBasisRowCount = 2;      // PriceBasis 'USD $' - a space and a '$', and it is a KEY
    public const int SettlementsNegativePriceCount = 4;

    public const int MyTradesRowCount = 5;
    public const int MyTradesBlankBidCommissionCount = 4;
    public const int MyTradesBlankOfferCommissionCount = 1;
    public const int MyTradesNoonHourTimestampCount = 3;   // 12:42:37 PM - the subtlest AM/PM case

    /// <summary>The comma-laden quoted address that must survive as ONE field (3 embedded commas).</summary>
    public const string CommaAddress = "P.O. Box 1234, 200 - 7 Avenue SW, Calgary, AB T2P 0A0";

    /// <summary>A legal name containing a comma - the other embedded-comma trap.</summary>
    public const string CommaLegalName = "Bluewater Energy Trading, LLC";

    private static readonly string SamplesDir =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Samples");

    public static string Read(string fileName)
    {
        var path = Path.Combine(SamplesDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"ModCom sample fixture not found: {path}", path);
        return File.ReadAllText(path);
    }

    // ---- synthetic literals for the shapes a live capture cannot contain -----------------------

    /// <summary>
    /// The verbatim 34-column trades header, exactly as both trades endpoints publish it
    /// (byte-identical between allTrades and myTrades). <b>Note the space-, ampersand- and
    /// slash-bearing names</b>: no naming policy matches these, which is why the loader binds by
    /// literal name.
    /// </summary>
    public const string TradesHeader =
        "\"Trade Number\",\"State\",\"Product\",\"Location\",\"Pipeline/Terminal\",\"Price Basis\",\"Term\"," +
        "\"Term Start\",\"Term End\",\"Price\",\"Volume\",\"Unit of Measure\",\"Executed Timestamp\"," +
        "\"Last Updated Timestamp\",\"Trade Type\",\"Side\",\"Bid Trader\",\"Bid Legal Name\",\"Bid Address\"," +
        "\"Bid Commission\",\"Offer Trader\",\"Offer Legal Name\",\"Offer Address\",\"Offer Commission\"," +
        "\"Spread Trade Number\",\"Apportionment Protected\",\"Clearing ID\",\"Settlement Currency\"," +
        "\"Contract Terms\",\"GT&C\",\"Notes\",\"In Index\",\"Click & Trade\",\"Product Type\"";

    /// <summary>The verbatim 9-column settlements header.</summary>
    public const string SettlementsHeader =
        "\"Settlement Date\",\"Product\",\"Location\",\"Pipeline/Terminal\",\"Price Basis\",\"Term\"," +
        "\"Term Start\",\"Term End\",\"Price\"";

    /// <summary>A quoted CSV field with any embedded quote doubled, i.e. how the vendor writes every field.</summary>
    public static string Q(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    /// <summary>Joins pre-built records with the live single-LF terminator (no trailing newline).</summary>
    public static string Lf(params string[] records) => string.Join("\n", records);

    /// <summary>Joins pre-built records with CRLF - accepted but never observed live.</summary>
    public static string CrLf(params string[] records) => string.Join("\r\n", records);

    /// <summary>
    /// One 34-field trades record. Every field defaults to <b>blank</b> (the API has no other
    /// absent-value representation) except <c>Trade Number</c>, which defaults to <c>1</c> so a
    /// record is keyable unless a test deliberately blanks it. Overrides are matched against the
    /// literal vendor header names.
    /// </summary>
    public static string TradesRecord(params (string Column, string? Value)[] overrides) =>
        Record(ModComColumns.Trades, new (string, string?)[] { (ModComColumns.TradeNumber, "1") }, overrides);

    /// <summary>
    /// One 9-field settlements record, defaulted to a realistic live row so a test can override the
    /// single field it is about.
    /// </summary>
    public static string SettlementsRecord(params (string Column, string? Value)[] overrides) =>
        Record(ModComColumns.Settlements, new (string, string?)[]
        {
            (ModComColumns.SettlementDate, "2026-08-21"),
            (ModComColumns.Product, "AHS"),
            (ModComColumns.Location, "Edmonton"),
            (ModComColumns.PipelineTerminal, "Enb T@S"),
            (ModComColumns.PriceBasis, "WTI CMA"),
            (ModComColumns.Term, "AUG-26"),
            (ModComColumns.TermStart, "2026-08-01"),
            (ModComColumns.TermEnd, "2026-08-31"),
            (ModComColumns.Price, "-14.30")
        }, overrides);

    /// <summary>A complete trades payload: the live header plus the given records, LF-terminated.</summary>
    public static string TradesCsv(params string[] records) => Lf(new[] { TradesHeader }.Concat(records).ToArray());

    /// <summary>A complete settlements payload: the live header plus the given records, LF-terminated.</summary>
    public static string SettlementsCsv(params string[] records) => Lf(new[] { SettlementsHeader }.Concat(records).ToArray());

    private static string Record(
        IReadOnlyList<string> columns,
        (string Column, string? Value)[] defaults,
        (string Column, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in defaults) values[column] = value;
        foreach (var (column, value) in overrides)
        {
            if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"'{column}' is not one of the {columns.Count} vendor column names");
            values[column] = value;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Q(values.TryGetValue(columns[i], out var v) ? v : string.Empty));
        }
        return sb.ToString();
    }
}
