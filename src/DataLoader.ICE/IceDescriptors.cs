namespace DataLoader.ICE;

/// <summary>
/// The scalar shapes the ICE feeds use. Drives BOTH the text-to-CLR conversion in
/// <see cref="IceConvert"/> and the DataTable column type in <see cref="IceTableSink"/>.
/// </summary>
public enum IceColumnType
{
    /// <summary><c>VARCHAR(n)</c> -> <see cref="string"/>.</summary>
    String,

    /// <summary><c>CHAR(1)</c> -> <see cref="string"/> (SqlClient binds CHAR from string).</summary>
    Char,

    /// <summary><c>INT</c> -> <see cref="int"/>.</summary>
    Int32,

    /// <summary><c>BIGINT</c> -> <see cref="long"/>. Load-bearing for <c>DEAL_ID</c>.</summary>
    Int64,

    /// <summary><c>DECIMAL(18,6)</c> -> <see cref="decimal"/>.</summary>
    Decimal,

    /// <summary><c>DATE</c> -> <see cref="DateTime"/>.</summary>
    Date,

    /// <summary><c>DATETIME2</c> -> <see cref="DateTime"/>.</summary>
    DateTime
}

/// <summary>
/// Where a column's value comes from when it is NOT a file header.
/// </summary>
public enum IceDerived
{
    /// <summary>Read from one of <see cref="IceColumn.SourceHeaders"/>.</summary>
    None,

    /// <summary>The https URL the file came from. Never contains the SSO token.</summary>
    SourcePath
}

/// <summary>
/// One column of one target table. This single record drives four things that
/// would otherwise have to be kept in sync by hand:
///
/// <list type="number">
///   <item>which header(s) the reader looks for (<see cref="SourceHeaders"/>),</item>
///   <item>how the text is converted (<see cref="Type"/>),</item>
///   <item>the DataTable column name, order and CLR type in the sink,</item>
///   <item>the expected TVP column in <c>sql/ICE/002</c>, asserted by
///         <c>IceTvpContractTests</c> against <see cref="SqlType"/>.</item>
/// </list>
///
/// <para>
/// Because a TVP binds BY POSITION, the ORDER of a table's column list IS the
/// contract. The test parses the real <c>.sql</c> file, so a change on either side
/// alone fails the build rather than silently corrupting rows.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable column name.</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(50)</c>.</param>
/// <param name="Type">Conversion + CLR type.</param>
/// <param name="SourceHeaders">
/// Accepted header spellings, tried in order, matched case-insensitively.
/// A list rather than a single string because the XLSX feed spells the greeks'
/// 6th column <c>PUT/CALL</c> while the three <c>.dat</c> greeks feeds spell it
/// <c>PUT_CALL</c> (docs/apis/ICE.md 4.4). Empty when <paramref name="Derived"/> is set.
/// </param>
/// <param name="Required">
/// <c>true</c> => <c>NOT NULL</c> in the TVP, and a row whose value is blank or
/// unparseable is DROPPED rather than merged under a blank key. Every primary-key
/// component is required. This is what excludes the underlying-future rows that
/// every options feed carries with an empty STRIKE (docs/apis/ICE.md 5.4).
/// </param>
/// <param name="Derived">Non-file source for the value.</param>
public sealed record IceColumn(
    string Name,
    string SqlType,
    IceColumnType Type,
    IReadOnlyList<string> SourceHeaders,
    bool Required,
    IceDerived Derived = IceDerived.None)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        IceColumnType.String => typeof(string),
        IceColumnType.Char => typeof(string),
        IceColumnType.Int32 => typeof(int),
        IceColumnType.Int64 => typeof(long),
        IceColumnType.Decimal => typeof(decimal),
        IceColumnType.Date => typeof(DateTime),
        IceColumnType.DateTime => typeof(DateTime),
        _ => throw new NotSupportedException($"Unmapped IceColumnType '{Type}'.")
    };

    // ---- terse builders, so the registry below reads like the SQL ----

    public static IceColumn Str(string name, int size, string header, bool required = false) =>
        new(name, $"VARCHAR({size})", IceColumnType.String, new[] { header }, required);

    public static IceColumn Chr(string name, string header, bool required = false) =>
        new(name, "CHAR(1)", IceColumnType.Char, new[] { header }, required);

    public static IceColumn Chr(string name, string[] headers, bool required = false) =>
        new(name, "CHAR(1)", IceColumnType.Char, headers, required);

    public static IceColumn I32(string name, string header, bool required = false) =>
        new(name, "INT", IceColumnType.Int32, new[] { header }, required);

    public static IceColumn I64(string name, string header, bool required = false) =>
        new(name, "BIGINT", IceColumnType.Int64, new[] { header }, required);

    public static IceColumn Dec(string name, string header, bool required = false) =>
        new(name, "DECIMAL(18,6)", IceColumnType.Decimal, new[] { header }, required);

    public static IceColumn Dat(string name, string header, bool required = false) =>
        new(name, "DATE", IceColumnType.Date, new[] { header }, required);

    public static IceColumn Dtm(string name, string header, bool required = false) =>
        new(name, "DATETIME2(7)", IceColumnType.DateTime, new[] { header }, required);

    /// <summary>The provenance column every table ends with.</summary>
    public static IceColumn SourcePath() =>
        new("SourcePath", "VARCHAR(500)", IceColumnType.String,
            Array.Empty<string>(), Required: false, Derived: IceDerived.SourcePath);
}

/// <summary>
/// One TARGET TABLE: its TVP, its merge proc and the ordered column list that IS
/// the TVP contract.
///
/// <para>
/// Deliberately per-TABLE and not per-FEED. Six feeds land in <c>arm.Futures</c>
/// and two in <c>arm.Options</c>; they share one descriptor, which is what
/// guarantees they produce identically shaped rows for one TVP instead of eight
/// hand-maintained column lists that could drift apart.
/// </para>
/// </summary>
public sealed record IceTableDescriptor(
    string TableName,
    string TvpType,
    string MergeProc,
    IReadOnlyList<IceColumn> Columns)
{
    /// <summary>Headers the reader must find. A missing one fails the whole file.</summary>
    public IEnumerable<string> RequiredHeaders =>
        Columns.Where(c => c.Derived == IceDerived.None && c.Required)
               .Select(c => c.SourceHeaders[0]);
}

/// <summary>How a feed's bytes are decoded.</summary>
public enum IceFileFormat
{
    /// <summary>Pipe-delimited <c>.dat</c> with a header row.</summary>
    PipeDelimited,

    /// <summary>RFC 4180 comma CSV with quoted strings and bare numbers.</summary>
    Csv,

    /// <summary>OOXML worksheet — the single IFLL feed.</summary>
    Xlsx
}

/// <summary>
/// One FEED: one URL template, one date-token format, one file format, and the
/// table it lands in.
/// </summary>
/// <param name="FeedId">Stable id — the <c>EnabledFeeds</c> value and the pipeline's id.</param>
/// <param name="PathTemplate">
/// Path under <c>https://downloads.ice.com/</c> with <c>{date}</c> where the token goes.
/// </param>
/// <param name="DateFormat">
/// <c>yyyy_MM_dd</c> for every feed except the two Crude Index feeds, which use
/// <c>yyyyMMdd</c>.
/// </param>
public sealed record IceFeedDescriptor(
    string FeedId,
    string DisplayName,
    string PathTemplate,
    string DateFormat,
    IceFileFormat Format,
    IceTableDescriptor Table)
{
    /// <summary>Relative path for one trade date, e.g. <c>Settlement_Reports_CSV/Gas/icecleared_gas_2026_08_28.dat</c>.</summary>
    public string PathFor(DateOnly tradeDate) =>
        PathTemplate.Replace("{date}", tradeDate.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Bare file name for one trade date, used for the local cache and the FileLog.</summary>
    public string FileNameFor(DateOnly tradeDate)
    {
        var path = PathFor(tradeDate);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}

/// <summary>
/// The registry — the single source of truth for every column mapping in this
/// loader.
///
/// <para>
/// Every header string here was copied from the live files on 2026-09-01 (see
/// <c>docs/apis/ICE.md</c> 4). <b>Header order in the files differs from table
/// order</b> — the files lead with <c>TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT</c>
/// while the tables lead with <c>TradeDate, Contract, ContractType, Strip</c> —
/// which is exactly why the reader matches by NAME and never by position.
/// </para>
/// <para>
/// <b>Column ORDER in each table's list is the TVP contract.</b> It matches
/// <c>sql/ICE/002_CreateIceTvpTypes.sql</c>, and <c>IceTvpContractTests</c> parses
/// that file and asserts it.
/// </para>
/// </summary>
public static class IceDescriptors
{
    // ===================== header name constants =====================
    // Spelled once so a typo is a compile error rather than a silently
    // unmatched column that loads NULL.

    private const string HTradeDate = "TRADE DATE";
    private const string HHub = "HUB";
    private const string HProduct = "PRODUCT";
    private const string HStrip = "STRIP";
    private const string HContract = "CONTRACT";
    private const string HContractType = "CONTRACT TYPE";
    private const string HStrike = "STRIKE";
    private const string HSettlementPrice = "SETTLEMENT PRICE";
    private const string HNetChange = "NET CHANGE";
    private const string HExpirationDate = "EXPIRATION DATE";
    private const string HProductId = "PRODUCT_ID";
    private const string HOptionVolatility = "OPTION_VOLATILITY";
    private const string HDeltaFactor = "DELTA_FACTOR";

    // ===================== target tables =====================

    /// <summary>
    /// arm.EnvFutures — icecleared_physenv. Strip is VARCHAR: this feed publishes
    /// month codes and daily contracts ('Aug19', '01 Sep 26'), not dates.
    /// </summary>
    public static readonly IceTableDescriptor EnvFuturesTable = new(
        TableName: "arm.EnvFutures",
        TvpType: "arm.EnvFuturesTvp",
        MergeProc: "arm.usp_BulkMergeEnvFutures",
        Columns: new[]
        {
            IceColumn.Dat("TradeDate", HTradeDate, required: true),
            IceColumn.Str("Contract", 10, HContract, required: true),
            IceColumn.Chr("ContractType", HContractType, required: true),
            IceColumn.Str("Strip", 50, HStrip, required: true),
            IceColumn.I32("ProductId", HProductId),
            IceColumn.Str("Hub", 100, HHub),
            IceColumn.Str("Product", 100, HProduct),
            IceColumn.Dec("Strike", HStrike),
            IceColumn.Dec("SettlementPrice", HSettlementPrice),
            IceColumn.Dec("NetChange", HNetChange),
            IceColumn.Dat("ExpirationDate", HExpirationDate),
            IceColumn.SourcePath()
        });

    /// <summary>
    /// arm.EnvOptions — icecleared_physenvoptions. Strike is REQUIRED, which drops
    /// the feed's 'F' underlying-future rows (359 of 12,215 on 2026-08-28).
    /// </summary>
    public static readonly IceTableDescriptor EnvOptionsTable = new(
        TableName: "arm.EnvOptions",
        TvpType: "arm.EnvOptionsTvp",
        MergeProc: "arm.usp_BulkMergeEnvOptions",
        Columns: new[]
        {
            IceColumn.Dat("TradeDate", HTradeDate, required: true),
            IceColumn.Str("Contract", 10, HContract, required: true),
            IceColumn.Chr("ContractType", HContractType, required: true),
            IceColumn.Dec("Strike", HStrike, required: true),
            IceColumn.Str("Strip", 50, HStrip, required: true),
            IceColumn.I32("ProductId", HProductId),
            IceColumn.Str("Hub", 100, HHub),
            IceColumn.Str("Product", 100, HProduct),
            IceColumn.Dec("SettlementPrice", HSettlementPrice),
            IceColumn.Dec("NetChange", HNetChange),
            IceColumn.Dat("ExpirationDate", HExpirationDate),
            IceColumn.Dec("OptionVolatility", HOptionVolatility),
            IceColumn.Dec("DeltaFactor", HDeltaFactor),
            IceColumn.SourcePath()
        });

    /// <summary>
    /// arm.Futures — SIX feeds share this table.
    ///
    /// <para>
    /// <b>ProductId is Required here and nowhere else.</b> It is the 5th primary-key
    /// column, the one approved deviation from the supplied DDL: ICE reuses contract
    /// codes across markets ('OLD' is both an Ontario power future and a NYH heating
    /// oil future), so without it ~73 rows/day are silently lost to whichever file
    /// merges last. PRODUCT_ID was verified never blank across all six feeds, so
    /// requiring it drops nothing. See docs/apis/ICE.md 5.1.
    /// </para>
    /// <para>Strip is DATE here — all six feeds were verified 100 % date-parseable.</para>
    /// </summary>
    public static readonly IceTableDescriptor FuturesTable = new(
        TableName: "arm.Futures",
        TvpType: "arm.FuturesTvp",
        MergeProc: "arm.usp_BulkMergeFutures",
        Columns: new[]
        {
            IceColumn.Dat("TradeDate", HTradeDate, required: true),
            IceColumn.Str("Contract", 10, HContract, required: true),
            IceColumn.Chr("ContractType", HContractType, required: true),
            IceColumn.Dat("Strip", HStrip, required: true),
            IceColumn.I32("ProductId", HProductId, required: true),
            IceColumn.Str("Hub", 100, HHub),
            IceColumn.Str("Product", 100, HProduct),
            IceColumn.Dec("Strike", HStrike),
            IceColumn.Dec("SettlementPrice", HSettlementPrice),
            IceColumn.Dec("NetChange", HNetChange),
            IceColumn.Dat("ExpirationDate", HExpirationDate),
            IceColumn.SourcePath()
        });

    /// <summary>
    /// arm.ICE_Crude_Oil_Index. Column order follows the TABLE; the file leads with
    /// INDEX_DATE_RANGE, which is 6th here.
    /// </summary>
    public static readonly IceTableDescriptor CrudeIndexTable = new(
        TableName: "arm.ICE_Crude_Oil_Index",
        TvpType: "arm.CrudeOilIndexTvp",
        MergeProc: "arm.usp_BulkMergeCrudeOilIndex",
        Columns: new[]
        {
            IceColumn.I32("MKTID", "MKTID", required: true),
            IceColumn.Str("PCC", 50, "PCC", required: true),
            IceColumn.I32("INDEX_ID", "INDEX_ID", required: true),
            IceColumn.Dat("INDEX_DATE", "INDEX_DATE", required: true),
            IceColumn.Dec("INDEX_PRICE", "INDEX_PRICE"),
            IceColumn.Str("INDEX_DATE_RANGE", 50, "INDEX_DATE_RANGE"),
            IceColumn.Dec("VOLUME", "VOLUME"),
            IceColumn.Str("MKT_DESC", 500, "MKT_DESC"),
            IceColumn.Dtm("CREATION_TIME", "CREATION_TIME"),
            IceColumn.Dtm("LAST_UPDATE_TIME", "LAST_UPDATE_TIME"),
            IceColumn.Dec("BBL", "BBL"),
            IceColumn.Dec("DAILY_PRICE", "DAILY_PRICE"),
            IceColumn.Dec("DAILY_VOLUME", "DAILY_VOLUME"),
            IceColumn.Dec("DAILY_BBL", "DAILY_BBL"),
            IceColumn.I32("NUM_OF_TRADES", "NUM_OF_TRADES"),
            IceColumn.SourcePath()
        });

    /// <summary>
    /// arm.ICE_Crude_Oil_Index_Trades. DEAL_ID is BIGINT — 297636150009 overflows INT.
    /// This feed is a cumulative rolling window, so the same deal is republished
    /// across many files; merging on the PK is idempotent (docs/apis/ICE.md 4.3).
    /// </summary>
    public static readonly IceTableDescriptor CrudeIndexTradesTable = new(
        TableName: "arm.ICE_Crude_Oil_Index_Trades",
        TvpType: "arm.CrudeOilIndexTradesTvp",
        MergeProc: "arm.usp_BulkMergeCrudeOilIndexTrades",
        Columns: new[]
        {
            IceColumn.Dat("TRADE_DATE", "TRADE_DATE", required: true),
            IceColumn.Str("PCC_TRADE", 50, "PCC_TRADE", required: true),
            IceColumn.Str("PCC_INDEX", 50, "PCC_INDEX", required: true),
            IceColumn.Str("STRIP", 50, "STRIP", required: true),
            IceColumn.I32("INDEX_ID", "INDEX_ID", required: true),
            IceColumn.I64("DEAL_ID", "DEAL_ID", required: true),
            IceColumn.Str("PRODUCT_NAME", 500, "PRODUCT_NAME"),
            IceColumn.Str("HUB_NAME", 500, "HUB_NAME"),
            IceColumn.Dtm("DEAL_EXECUTION_TIME", "DEAL_EXECUTION_TIME"),
            IceColumn.Dec("DEAL_PRICE", "DEAL_PRICE"),
            IceColumn.Dec("DEAL_QUANTITY", "DEAL_QUANTITY"),
            IceColumn.Dec("DEAL_QTY_IN_BBL", "DEAL_QTY_IN_BBL"),
            IceColumn.I32("MARKET_TYPE_ID", "MARKET_TYPE_ID"),
            IceColumn.SourcePath()
        });

    /// <summary>arm.ICEClearedPowerFutures — icecleared_power. Strip stays VARCHAR per the supplied DDL.</summary>
    public static readonly IceTableDescriptor PowerFuturesTable = new(
        TableName: "arm.ICEClearedPowerFutures",
        TvpType: "arm.PowerFuturesTvp",
        MergeProc: "arm.usp_BulkMergeIceClearedPowerFutures",
        Columns: new[]
        {
            IceColumn.Dat("TradeDate", HTradeDate, required: true),
            IceColumn.Str("Contract", 10, HContract, required: true),
            IceColumn.Chr("ContractType", HContractType, required: true),
            IceColumn.Str("Strip", 50, HStrip, required: true),
            IceColumn.I32("ProductId", HProductId),
            IceColumn.Str("Hub", 100, HHub),
            IceColumn.Str("Product", 100, HProduct),
            IceColumn.Dec("Strike", HStrike),
            IceColumn.Dec("SettlementPrice", HSettlementPrice),
            IceColumn.Dec("NetChange", HNetChange),
            IceColumn.Dat("ExpirationDate", HExpirationDate),
            IceColumn.SourcePath()
        });

    /// <summary>arm.ICEClearedPowerOptions — icecleared_poweroptions (drops 6.4 % blank-strike rows).</summary>
    public static readonly IceTableDescriptor PowerOptionsTable = new(
        TableName: "arm.ICEClearedPowerOptions",
        TvpType: "arm.PowerOptionsTvp",
        MergeProc: "arm.usp_BulkMergeIceClearedPowerOptions",
        Columns: new[]
        {
            IceColumn.Dat("TradeDate", HTradeDate, required: true),
            IceColumn.Str("Contract", 10, HContract, required: true),
            IceColumn.Chr("ContractType", HContractType, required: true),
            IceColumn.Dec("Strike", HStrike, required: true),
            IceColumn.Str("Strip", 50, HStrip, required: true),
            IceColumn.I32("ProductId", HProductId),
            IceColumn.Str("Hub", 100, HHub),
            IceColumn.Str("Product", 100, HProduct),
            IceColumn.Dec("SettlementPrice", HSettlementPrice),
            IceColumn.Dec("NetChange", HNetChange),
            IceColumn.Dat("ExpirationDate", HExpirationDate),
            IceColumn.Dec("OptionVolatility", HOptionVolatility),
            IceColumn.Dec("DeltaFactor", HDeltaFactor),
            IceColumn.SourcePath()
        });

    /// <summary>
    /// The greeks column list, shared by four tables with identical shape.
    ///
    /// <para>
    /// <c>PUT_CALL</c> accepts BOTH spellings: the three <c>.dat</c> feeds publish
    /// <c>PUT_CALL</c> and the XLSX feed publishes <c>PUT/CALL</c>. One list with an
    /// alternate spelling is safer than two lists that could drift.
    /// </para>
    /// </summary>
    private static IReadOnlyList<IceColumn> GreeksColumns() => new[]
    {
        IceColumn.Dat("TRADE_DATE", HTradeDate, required: true),
        IceColumn.Str("CONTRACT", 50, HContract, required: true),
        IceColumn.Str("STRIP", 50, HStrip, required: true),
        IceColumn.Dat("EXPIRATION_DATE", "EXPIRATION_DATE", required: true),
        IceColumn.Dec("STRIKE", HStrike, required: true),
        IceColumn.Chr("PUT_CALL", new[] { "PUT_CALL", "PUT/CALL" }, required: true),
        IceColumn.Dec("SETTLEMENT_PRICE", "SETTLEMENT_PRICE"),
        IceColumn.Dec("VOLATILITY", "VOLATILITY"),
        IceColumn.Dec("DELTA", "DELTA"),
        IceColumn.Dec("GAMMA", "GAMMA"),
        IceColumn.Dec("THETA", "THETA"),
        IceColumn.Dec("VEGA", "VEGA"),
        IceColumn.SourcePath()
    };

    public static readonly IceTableDescriptor FcaOptionsTable = new(
        "arm.ICEFCA_Options", "arm.FcaOptionsTvp", "arm.usp_BulkMergeIcefcaOptions", GreeksColumns());

    /// <summary>
    /// arm.ICEFUS_FinOptions. Drops 25.9 % of its rows — by far the highest ratio —
    /// because the file is largely interest-rate futures carrying no strike. That is
    /// the shape of the feed, not a fault; arm.usp_ValidateLoad reports the ratio so
    /// it stays visible rather than looking like a regression later.
    /// </summary>
    public static readonly IceTableDescriptor FusFinOptionsTable = new(
        "arm.ICEFUS_FinOptions", "arm.FusFinOptionsTvp", "arm.usp_BulkMergeIcefusFinOptions", GreeksColumns());

    public static readonly IceTableDescriptor FusSoftOptionsTable = new(
        "arm.ICEFUS_SoftOptions", "arm.FusSoftOptionsTvp", "arm.usp_BulkMergeIcefusSoftOptions", GreeksColumns());

    public static readonly IceTableDescriptor IfllOptionsTable = new(
        "arm.IFLL_Options", "arm.IfllOptionsTvp", "arm.usp_BulkMergeIfllOptions", GreeksColumns());

    /// <summary>
    /// arm.Options — TWO feeds share this table. Strip is DATE here, unlike
    /// EnvOptions and PowerOptions. The two feeds were verified to produce zero
    /// conflicting payloads on the declared PK, so no PK change was needed.
    /// </summary>
    public static readonly IceTableDescriptor OptionsTable = new(
        TableName: "arm.Options",
        TvpType: "arm.OptionsTvp",
        MergeProc: "arm.usp_BulkMergeOptions",
        Columns: new[]
        {
            IceColumn.Dat("TradeDate", HTradeDate, required: true),
            IceColumn.Str("Contract", 10, HContract, required: true),
            IceColumn.Chr("ContractType", HContractType, required: true),
            IceColumn.Dec("Strike", HStrike, required: true),
            IceColumn.Dat("Strip", HStrip, required: true),
            IceColumn.I32("ProductId", HProductId),
            IceColumn.Str("Hub", 100, HHub),
            IceColumn.Str("Product", 100, HProduct),
            IceColumn.Dec("SettlementPrice", HSettlementPrice),
            IceColumn.Dec("NetChange", HNetChange),
            IceColumn.Dat("ExpirationDate", HExpirationDate),
            IceColumn.Dec("OptionVolatility", HOptionVolatility),
            IceColumn.Dec("DeltaFactor", HDeltaFactor),
            IceColumn.SourcePath()
        });

    /// <summary>Every target table, for the TVP contract test.</summary>
    public static IReadOnlyList<IceTableDescriptor> AllTables { get; } = new[]
    {
        EnvFuturesTable, EnvOptionsTable, FuturesTable, CrudeIndexTable, CrudeIndexTradesTable,
        PowerFuturesTable, PowerOptionsTable, FcaOptionsTable, FusFinOptionsTable,
        FusSoftOptionsTable, IfllOptionsTable, OptionsTable
    };

    // ===================== the 18 feeds =====================

    private const string DateDashed = "yyyy_MM_dd";
    private const string DateCompact = "yyyyMMdd";

    public static IReadOnlyList<IceFeedDescriptor> All { get; } = new[]
    {
        // --- Environmentals ---
        new IceFeedDescriptor("EnvFutures", "Environmentals physical futures",
            "Settlement_Reports_CSV/Environmentals/icecleared_physenv_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, EnvFuturesTable),

        new IceFeedDescriptor("EnvOptions", "Environmentals physical options",
            "Settlement_Reports_CSV/Environmentals/icecleared_physenvoptions_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, EnvOptionsTable),

        // --- the six that share arm.Futures ---
        new IceFeedDescriptor("NgxGas", "NGX cleared gas",
            "Settlement_Reports_CSV/Gas/ngxcleared_gas_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FuturesTable),

        new IceFeedDescriptor("NgxPower", "NGX cleared power",
            "Settlement_Reports_CSV/Power/ngxcleared_power_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FuturesTable),

        new IceFeedDescriptor("IceGas", "ICE cleared gas",
            "Settlement_Reports_CSV/Gas/icecleared_gas_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FuturesTable),

        new IceFeedDescriptor("IceNgl", "ICE cleared NGL",
            "Settlement_Reports_CSV/NGL/icecleared_ngl_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FuturesTable),

        new IceFeedDescriptor("IceOil", "ICE cleared oil",
            "Settlement_Reports_CSV/Oil/icecleared_oil_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FuturesTable),

        new IceFeedDescriptor("IceOilCa", "ICE cleared oil (Canada)",
            "Settlement_Reports_CSV/Oil/iceclearedoil_ca_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FuturesTable),

        // --- Crude Index (compact date token) ---
        new IceFeedDescriptor("CrudeIndex", "ICE crude oil index",
            "Crude_Index/ICE_Crude_Oil_Index_{date}.csv",
            DateCompact, IceFileFormat.Csv, CrudeIndexTable),

        new IceFeedDescriptor("CrudeIndexTrades", "ICE crude oil index trades",
            "Crude_Index/ICE_Crude_Oil_Index_Trades_{date}.csv",
            DateCompact, IceFileFormat.Csv, CrudeIndexTradesTable),

        // --- Power ---
        new IceFeedDescriptor("PowerFutures", "ICE cleared power futures",
            "Settlement_Reports_CSV/Power/icecleared_power_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, PowerFuturesTable),

        new IceFeedDescriptor("PowerOptions", "ICE cleared power options",
            "Settlement_Reports_CSV/Power/icecleared_poweroptions_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, PowerOptionsTable),

        // --- options greeks ---
        new IceFeedDescriptor("FcaOptions", "ICEFCA options greeks",
            "ICEF_options_greeks/ICEFCA_Options_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FcaOptionsTable),

        new IceFeedDescriptor("FusFinOptions", "ICEFUS financial options greeks",
            "ICEF_options_greeks/ICEFUS_FinOptions_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FusFinOptionsTable),

        new IceFeedDescriptor("FusSoftOptions", "ICEFUS softs options greeks",
            "ICEF_options_greeks/ICEFUS_SoftOptions_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, FusSoftOptionsTable),

        // --- the one XLSX ---
        new IceFeedDescriptor("IfllOptions", "IFLL options (fixed income settlements)",
            "FixedIncome_Settlements/IFLL_Options_{date}.xlsx",
            DateDashed, IceFileFormat.Xlsx, IfllOptionsTable),

        // --- the two that share arm.Options ---
        new IceFeedDescriptor("GasOptions", "ICE cleared gas options",
            "Settlement_Reports_CSV/Gas/icecleared_gasoptions_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, OptionsTable),

        new IceFeedDescriptor("OilOptions", "ICE cleared oil options",
            "Settlement_Reports_CSV/Oil/icecleared_oiloptions_{date}.dat",
            DateDashed, IceFileFormat.PipeDelimited, OptionsTable)
    };

    /// <summary>Case-insensitive feed lookup; null when the id is unknown.</summary>
    public static IceFeedDescriptor? Find(string feedId) =>
        All.FirstOrDefault(f => string.Equals(f.FeedId, feedId, StringComparison.OrdinalIgnoreCase));
}
