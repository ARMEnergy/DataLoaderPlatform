namespace DataLoader.CME;

/// <summary>
/// The scalar shapes the two fact tables use. Drives BOTH the text-to-CLR
/// conversion in <see cref="CmeBulletinParser"/> and the DataTable column type in
/// <see cref="CmeTableSink"/>.
/// </summary>
public enum CmeColumnType
{
    /// <summary><c>VARCHAR(n)</c> -&gt; <see cref="string"/>.</summary>
    String,

    /// <summary><c>CHAR(1)</c> -&gt; <see cref="string"/> of length 1 (the A/B indicator).</summary>
    Char,

    /// <summary><c>DATE</c> -&gt; <see cref="DateTime"/>.</summary>
    Date,

    /// <summary><c>SMALLINT</c> -&gt; <see cref="short"/>.</summary>
    Int16,

    /// <summary><c>TINYINT</c> -&gt; <see cref="byte"/>.</summary>
    Byte,

    /// <summary><c>INT</c> -&gt; <see cref="int"/>.</summary>
    Int32,

    /// <summary>
    /// <c>DECIMAL(18,8)</c> -&gt; <see cref="decimal"/>. The requester's DDL
    /// specifies DECIMAL(18,8) for every price and volume column, so the CLR side
    /// is <c>decimal</c> and not <c>double</c> — a <c>double</c> DataTable column
    /// against a <c>DECIMAL</c> TVP column would force a server-side conversion on
    /// every row and could round a tick-derived eighth.
    /// </summary>
    Decimal
}

/// <summary>
/// One column of one fact table. This single record drives four things that would
/// otherwise have to be kept in sync by hand:
///
/// <list type="number">
///   <item>the DataTable column name, ORDER and CLR type in the sink,</item>
///   <item>how the value is pulled off a parsed row (<see cref="Get"/>),</item>
///   <item>the expected TVP column in <c>sql/CME/002</c>, asserted by
///         <c>CmeTvpContractTests</c> against <see cref="SqlType"/>,</item>
///   <item>whether the parser may emit NULL for it (<see cref="Required"/>).</item>
/// </list>
///
/// <para>
/// Because a TVP binds BY POSITION, the ORDER of a table's column list IS the
/// contract. <see cref="Get"/> living on the column itself is what makes that
/// structural rather than clerical: the sink walks ONE list to build both the
/// schema and the values, so the DataTable's shape and its contents cannot drift
/// from each other. What still COULD drift is this registry versus the
/// <c>.sql</c> file — and the contract test parses the real <c>.sql</c>, so
/// editing one side alone fails the build.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable / table column name, without brackets.</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(50)</c>.</param>
/// <param name="Type">Conversion + CLR type.</param>
/// <param name="Required">
/// <c>true</c> =&gt; <c>NOT NULL</c> in the TVP, and the parser must never emit
/// NULL. Every primary-key component is required. Note that SQL Server silently
/// promotes a nullable column to NOT NULL when it joins a PRIMARY KEY, so the
/// requester's DDL — which declares e.g. <c>ExchangeCode varchar(50)</c> with no
/// NOT NULL yet lists it in the PK — really does mean NOT NULL. That is why
/// <see cref="CmeDescriptors.Validate"/> asserts key ⇒ required.
/// </param>
/// <param name="Get">Projects the value out of a parsed row; returns <see cref="DBNull.Value"/> for NULL.</param>
public sealed record CmeColumn(
    string Name,
    string SqlType,
    CmeColumnType Type,
    bool Required,
    Func<CmeFactRow, object> Get)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        CmeColumnType.String => typeof(string),
        CmeColumnType.Char => typeof(string),
        CmeColumnType.Date => typeof(DateTime),
        CmeColumnType.Int16 => typeof(short),
        CmeColumnType.Byte => typeof(byte),
        CmeColumnType.Int32 => typeof(int),
        CmeColumnType.Decimal => typeof(decimal),
        _ => throw new NotSupportedException($"Unmapped CmeColumnType '{Type}'.")
    };
}

/// <summary>
/// Compile-time description of one target table: which TVP, which merge proc, and
/// the ordered column list that IS the TVP contract.
/// </summary>
/// <param name="KeyColumns">
/// The target table's primary key, in order. Used by the descriptor self-check to
/// prove every key component is <see cref="CmeColumn.Required"/>, and by the tests
/// that pin the merge procs' <c>PARTITION BY</c>.
/// </param>
public sealed record CmeTableDescriptor(
    string TableId,
    string TargetTable,
    string TvpType,
    string MergeProc,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<CmeColumn> Columns)
{
    public int IndexOf(string columnName)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, columnName, StringComparison.Ordinal)) return i;

        return -1;
    }
}

/// <summary>
/// How a feed writes fractional ("tick") prices such as <c>491'4</c>.
///
/// <para>
/// <b>Empirically determined, not assumed.</b> Across 16 live bulletins spanning
/// 8 feeds and 2 trade dates, tick notation appears in exactly two feeds, and the
/// MAXIMUM fractional numerator observed for each fraction width pins the
/// denominator beyond doubt:
/// </para>
/// <list type="bullet">
///   <item><c>STLAGS</c> — width 1 only, max numerator <b>7</b> ⇒ EIGHTHS.
///         <c>491'4</c> = 491 + 4/8 = 491.5, the Chicago grain convention.</item>
///   <item><c>STLINT</c> — width 1 (max 0), width 2 (max <b>63</b>) and width 3
///         (max <b>635</b>) ⇒ SIXTY-FOURTHS, with width 3 carrying a tenth of a
///         64th: <c>'635</c> = 63.5/64. The rates convention.</item>
///   <item>Every other feed — no tick notation at all.</item>
/// </list>
/// <para>
/// The denominator is therefore a PER-FEED property, not a global one: reading
/// <c>'4</c> as 4/64 in a grain bulletin, or <c>'32</c> as 32/8 in a rates
/// bulletin, silently corrupts the price. A width this convention does not allow
/// is treated as an anomaly (value NULL, counted, logged) rather than converted
/// under a guessed denominator.
/// </para>
/// </summary>
/// <param name="Denominator">Ticks per whole unit — 8 or 64.</param>
/// <param name="MaxFractionDigits">
/// Widest fraction this feed may write. A width of 3 means the last digit is a
/// TENTH of a <paramref name="Denominator"/>th.
/// </param>
public sealed record CmeTickConvention(int Denominator, int MaxFractionDigits)
{
    /// <summary>Grain/oilseed eighths — <c>491'4</c> = 491.5.</summary>
    public static readonly CmeTickConvention Eighths = new(8, 1);

    /// <summary>Rates sixty-fourths, allowing a tenth-of-a-64th third digit — <c>'635</c> = 63.5/64.</summary>
    public static readonly CmeTickConvention SixtyFourths = new(64, 3);
}

/// <summary>
/// One feed = one <c>&lt;ProductCode&gt;_&lt;ExchangeCode&gt;</c> directory on the
/// drop = one daily bulletin series.
///
/// <para>
/// Feeds are DISCOVERED at run time by walking the drop rather than hard-coded,
/// because the account's entitlements decide which exist. The names are split on
/// the FIRST underscore, per the requester's rule: in <c>EOD_STLAGS</c> the text
/// before the underscore is the ProductCode (<c>EOD</c>) and the text after is the
/// ExchangeCode (<c>STLAGS</c>).
/// </para>
/// <para>
/// Splitting on the FIRST underscore matters: the PRODUCT-level folder
/// <c>BAS_STLNYMEX_STLCPC</c> holds two feed folders, <c>EOD_STLNYMEX</c> and
/// <c>EOD_STLCPC</c>, so only the feed-level name is split — and every feed-level
/// name observed has exactly one underscore.
/// </para>
/// </summary>
/// <param name="ProductDirectory">The product-level folder, e.g. <c>BAS_STLAGS</c>. Relative to the root.</param>
/// <param name="FeedDirectory">The feed-level folder, e.g. <c>EOD_STLAGS</c>.</param>
public sealed record CmeFeed(
    string ProductCode,
    string ExchangeCode,
    string ProductDirectory,
    string FeedDirectory)
{
    /// <summary>Stable id — the <c>EnabledFeeds</c> value and the log/label key.</summary>
    public string FeedId => $"{ProductCode}_{ExchangeCode}";

    /// <summary>Path of the feed folder relative to the configured root.</summary>
    public string RelativePath => CmeSftpFileSystem.Combine(ProductDirectory, FeedDirectory);

    /// <summary>
    /// The tick convention for this feed, or null when the feed writes no tick
    /// prices. Keyed on ExchangeCode — see <see cref="CmeTickConvention"/>.
    /// </summary>
    public CmeTickConvention? Ticks => CmeDescriptors.TickConventionFor(ExchangeCode);

    /// <summary>The bulletin file name for a trade date, e.g. <c>STLAGS_20260904.txt</c>.</summary>
    public string FileNameFor(DateOnly tradeDate) =>
        $"{ExchangeCode}_{CmeTime.FileToken(tradeDate)}.txt";
}

/// <summary>
/// One parsed fact row, strongly typed. The sink projects it into a DataTable
/// through the column list's <see cref="CmeColumn.Get"/> selectors, so the schema
/// and the values always come from the same ordered list.
///
/// <para>
/// One class serves BOTH tables rather than two near-identical POCOs.
/// <see cref="Kind"/> says which table it belongs to, and the two column lists
/// simply project different subsets — <see cref="Strike"/> and
/// <see cref="PutCall"/> are read only by the option list. Two row classes would
/// mean two projection paths and two chances for the DataTable/TVP drift that
/// <c>tvp-contract-check</c> exists to catch.
/// </para>
/// </summary>
public sealed class CmeFactRow
{
    public required CmeRowKind Kind { get; init; }

    public required string ExchangeCode { get; init; }
    public required string ProductCode { get; init; }
    public required DateOnly TradeDate { get; init; }
    public required string ProductSymbol { get; init; }
    public required string ProductDescription { get; init; }
    public required short ContractYear { get; init; }
    public required byte ContractMonth { get; init; }

    /// <summary>Option only — <c>C</c> or <c>P</c>. Empty for a future.</summary>
    public string PutCall { get; init; } = string.Empty;

    /// <summary>Option only. Zero for a future (the future column list never reads it).</summary>
    public decimal Strike { get; init; }

    public decimal? Open { get; init; }
    public decimal? High { get; init; }
    public string? HighABIndicator { get; init; }
    public decimal? Low { get; init; }
    public string? LowABIndicator { get; init; }
    public decimal? Last { get; init; }
    public string? LastABIndicator { get; init; }
    public decimal? Settle { get; init; }
    public decimal? PctChange { get; init; }
    public decimal? EstVol { get; init; }
    public decimal? PriorSettle { get; init; }
    public decimal? PriorVol { get; init; }
    public decimal? PriorInt { get; init; }

    /// <summary>
    /// Position of this row within its bulletin, 1-based. NOT a table column — it
    /// exists only in the TVP, so the merge's batch de-duplication has a
    /// deterministic tiebreak ("last occurrence in the file wins") instead of
    /// depending on row order inside a table variable. See <c>sql/CME/003</c>.
    /// </summary>
    public required int RowOrdinal { get; init; }
}

/// <summary>Which fact table a parsed row belongs to.</summary>
public enum CmeRowKind
{
    Future,
    Option
}

/// <summary>
/// The registry: fixed-width geometry, tick conventions, and the two ordered
/// column lists that are the TVP contract.
/// </summary>
public static class CmeDescriptors
{
    // ---------------------------------------------------------------------
    // Fixed-width geometry
    // ---------------------------------------------------------------------

    /// <summary>
    /// Column spans of a bulletin data row, as 1-based inclusive character
    /// positions.
    ///
    /// <para>
    /// <b>⚠ WHY FIXED-WIDTH AND NOT WHITESPACE SPLITTING.</b> A missing value is
    /// sometimes the placeholder <c>----</c> and sometimes simply BLANK, in the
    /// same file:
    /// </para>
    /// <code>
    ///   SEP26             ----         ----         ----         ----       1821.0         -7.4                     1828.4
    /// </code>
    /// <para>
    /// That row has 10 measure columns but only 8 whitespace-delimited tokens —
    /// ACTUAL VOL, PRIOR VOL and PRIOR INT are blank. Splitting on whitespace
    /// would slide <c>1828.4</c> two columns left, into ACTUAL VOL, and load a
    /// prior-day settle as a volume. Every field must therefore be taken by
    /// POSITION.
    /// </para>
    /// <para>
    /// <b>How these numbers were established.</b> Not from the header line, which
    /// only labels the fields, but from a character-occupancy histogram over
    /// 727,042 live data rows: the columns that are NEVER occupied are the field
    /// separators, and the widest observed value fixes each field's left edge.
    /// Values are RIGHT-aligned, so a field grows LEFTWARD; each span therefore
    /// absorbs the dead gap to its LEFT, which is where growth will appear.
    /// </para>
    /// <para>
    /// The right edges (22, 35, 48, 61, 74, 87, 99, 114, 126, 138) are the stable
    /// anchors and are asserted separately by
    /// <see cref="CmeBulletinParser"/>'s right-edge check, so a future widening
    /// that outgrows a span is DETECTED rather than silently mis-sliced.
    /// </para>
    /// </summary>
    public const int LabelStart = 1, LabelEnd = 13;

    /// <summary>
    /// The measure fields, in bulletin order, as (name, start, end, indicatorColumn).
    ///
    /// <para>
    /// <c>indicatorColumn</c> is the single character position that may hold an
    /// A/B qualifier, or 0 when the field never carries one. Across 757,360 live
    /// rows an indicator appeared on exactly HIGH (<c>B</c>), LOW (<c>A</c>) and
    /// LAST (<c>A</c> or <c>B</c>) — which is precisely the three indicator
    /// columns the requester's DDL provides, so the DDL and the data agree.
    /// </para>
    /// </summary>
    public static readonly (string Name, int Start, int End, int IndicatorColumn)[] MeasureFields =
    {
        ("Open",        14,  22, 0),
        ("High",        23,  35, 36),
        ("Low",         37,  48, 49),
        ("Last",        50,  61, 62),
        ("Settle",      63,  74, 0),
        ("PctChange",   75,  87, 0),
        ("EstVol",      88,  99, 0),
        ("PriorSettle", 100, 114, 0),
        ("PriorVol",    115, 126, 0),
        ("PriorInt",    127, 138, 0)
    };

    /// <summary>
    /// Every character position a measure value may legally END on — the ten
    /// numeric right edges plus the three indicator columns. The parser uses this
    /// to prove a line really is a data row before trusting the slices.
    /// </summary>
    public static readonly HashSet<int> ValueRightEdges = BuildRightEdges();

    private static HashSet<int> BuildRightEdges()
    {
        var edges = new HashSet<int>();

        foreach (var (_, _, end, indicator) in MeasureFields)
        {
            // The numeric right edge is the field's end, EXCEPT where an indicator
            // column follows: there the number ends one short of the span.
            edges.Add(indicator > 0 ? indicator - 1 : end);
            if (indicator > 0) edges.Add(indicator);
        }

        return edges;
    }

    // ---------------------------------------------------------------------
    // Tick conventions
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, CmeTickConvention> Ticks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["STLAGS"] = CmeTickConvention.Eighths,
            ["STLINT"] = CmeTickConvention.SixtyFourths
        };

    /// <summary>The tick convention for an exchange code, or null when it writes no tick prices.</summary>
    public static CmeTickConvention? TickConventionFor(string exchangeCode) =>
        Ticks.TryGetValue(exchangeCode, out var convention) ? convention : null;

    /// <summary>
    /// The feed folders seen on the account when this loader was built, for
    /// reference and for the startup sanity warning. Discovery is authoritative —
    /// this list is documentation, not a filter.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownFeedIds = new[]
    {
        "EOD_STLAGS", "EOD_STLALT", "EOD_STLCOMEX", "EOD_STLCPC",
        "EOD_STLCUR", "EOD_STLEQT", "EOD_STLINT", "EOD_STLNYMEX"
    };

    // ---------------------------------------------------------------------
    // Column lists — THE TVP CONTRACT
    // ---------------------------------------------------------------------

    private const string Dec = "DECIMAL(18,8)";

    private static object Nz(decimal? v) => v ?? (object)DBNull.Value;
    private static object Nz(string? v) => string.IsNullOrEmpty(v) ? DBNull.Value : v;

    /// <summary>
    /// The 15 measure + indicator columns, identical in both tables and in the same
    /// relative order, built once so the two lists cannot disagree about them.
    /// </summary>
    private static IEnumerable<CmeColumn> Measures() => new[]
    {
        new CmeColumn("Open",            Dec,       CmeColumnType.Decimal, false, r => Nz(r.Open)),
        new CmeColumn("High",            Dec,       CmeColumnType.Decimal, false, r => Nz(r.High)),
        new CmeColumn("HighABIndicator", "CHAR(1)", CmeColumnType.Char,    false, r => Nz(r.HighABIndicator)),
        new CmeColumn("Low",             Dec,       CmeColumnType.Decimal, false, r => Nz(r.Low)),
        new CmeColumn("LowABIndicator",  "CHAR(1)", CmeColumnType.Char,    false, r => Nz(r.LowABIndicator)),
        new CmeColumn("Last",            Dec,       CmeColumnType.Decimal, false, r => Nz(r.Last)),
        new CmeColumn("LastABIndicator", "CHAR(1)", CmeColumnType.Char,    false, r => Nz(r.LastABIndicator)),
        new CmeColumn("Settle",          Dec,       CmeColumnType.Decimal, false, r => Nz(r.Settle)),
        new CmeColumn("PctChange",       Dec,       CmeColumnType.Decimal, false, r => Nz(r.PctChange)),
        new CmeColumn("EstVol",          Dec,       CmeColumnType.Decimal, false, r => Nz(r.EstVol)),
        new CmeColumn("PriorSettle",     Dec,       CmeColumnType.Decimal, false, r => Nz(r.PriorSettle)),
        new CmeColumn("PriorVol",        Dec,       CmeColumnType.Decimal, false, r => Nz(r.PriorVol)),
        new CmeColumn("PriorInt",        Dec,       CmeColumnType.Decimal, false, r => Nz(r.PriorInt))
    };

    /// <summary>
    /// <c>arm.STLBASIC_Option</c> — column order EXACTLY as the requester's DDL
    /// declares it, minus the DB-stamped <c>ModifiedAtUtc</c>, plus the TVP-only
    /// <c>RowOrdinal</c> last.
    /// </summary>
    public static readonly CmeTableDescriptor OptionTable = new(
        TableId: "Option",
        TargetTable: "arm.STLBASIC_Option",
        TvpType: "arm.STLBASIC_OptionTvp",
        MergeProc: "arm.usp_BulkMergeStlbasicOption",
        KeyColumns: new[]
        {
            "ExchangeCode", "ProductCode", "TradeDate", "ProductSymbol", "ProductDescription",
            "ContractYear", "ContractMonth", "PutCall", "Strike"
        },
        Columns: new[]
            {
                new CmeColumn("ExchangeCode",       "VARCHAR(50)",  CmeColumnType.String,  true, r => r.ExchangeCode),
                new CmeColumn("ProductCode",        "VARCHAR(50)",  CmeColumnType.String,  true, r => r.ProductCode),
                new CmeColumn("TradeDate",          "DATE",         CmeColumnType.Date,    true, r => r.TradeDate.ToDateTime(TimeOnly.MinValue)),
                new CmeColumn("ProductSymbol",      "VARCHAR(50)",  CmeColumnType.String,  true, r => r.ProductSymbol),
                new CmeColumn("ProductDescription", "VARCHAR(250)", CmeColumnType.String,  true, r => r.ProductDescription),
                new CmeColumn("ContractYear",       "SMALLINT",     CmeColumnType.Int16,   true, r => r.ContractYear),
                new CmeColumn("ContractMonth",      "TINYINT",      CmeColumnType.Byte,    true, r => r.ContractMonth),
                new CmeColumn("PutCall",            "VARCHAR(50)",  CmeColumnType.String,  true, r => r.PutCall),
                new CmeColumn("Strike",             Dec,            CmeColumnType.Decimal, true, r => r.Strike)
            }
            .Concat(Measures())
            .Append(new CmeColumn("RowOrdinal", "INT", CmeColumnType.Int32, true, r => r.RowOrdinal))
            .ToList());

    /// <summary>
    /// <c>arm.STLBASIC_Future</c> — column order EXACTLY as the requester's DDL
    /// declares it. NOTE that the future table places
    /// <c>ProductDescription</c> AFTER <c>ContractMonth</c> (the option table puts
    /// it before <c>ContractYear</c>), and that it is NOT part of the future key,
    /// so it is genuinely nullable here. Both differences are deliberate and
    /// mirror the supplied DDL.
    /// </summary>
    public static readonly CmeTableDescriptor FutureTable = new(
        TableId: "Future",
        TargetTable: "arm.STLBASIC_Future",
        TvpType: "arm.STLBASIC_FutureTvp",
        MergeProc: "arm.usp_BulkMergeStlbasicFuture",
        KeyColumns: new[]
        {
            "ExchangeCode", "ProductCode", "TradeDate", "ProductSymbol", "ContractYear", "ContractMonth"
        },
        Columns: new[]
            {
                new CmeColumn("ExchangeCode",       "VARCHAR(50)",   CmeColumnType.String, true,  r => r.ExchangeCode),
                new CmeColumn("ProductCode",        "VARCHAR(50)",   CmeColumnType.String, true,  r => r.ProductCode),
                new CmeColumn("TradeDate",          "DATE",          CmeColumnType.Date,   true,  r => r.TradeDate.ToDateTime(TimeOnly.MinValue)),
                new CmeColumn("ProductSymbol",      "VARCHAR(50)",   CmeColumnType.String, true,  r => r.ProductSymbol),
                new CmeColumn("ContractYear",       "SMALLINT",      CmeColumnType.Int16,  true,  r => r.ContractYear),
                new CmeColumn("ContractMonth",      "TINYINT",       CmeColumnType.Byte,   true,  r => r.ContractMonth),
                new CmeColumn("ProductDescription", "VARCHAR(2000)", CmeColumnType.String, false, r => Nz(r.ProductDescription))
            }
            .Concat(Measures())
            .Append(new CmeColumn("RowOrdinal", "INT", CmeColumnType.Int32, true, r => r.RowOrdinal))
            .ToList());

    /// <summary>Both tables, option first.</summary>
    public static readonly IReadOnlyList<CmeTableDescriptor> All = new[] { OptionTable, FutureTable };

    /// <summary>The descriptor a parsed row belongs to.</summary>
    public static CmeTableDescriptor For(CmeRowKind kind) =>
        kind == CmeRowKind.Option ? OptionTable : FutureTable;

    /// <summary>
    /// Self-check run at module startup, BEFORE any network or database work. A bad
    /// edit here would otherwise surface as a server-side type error mid-run or,
    /// worse, as silently shifted columns.
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var table in All)
        {
            if (table.Columns.Count == 0)
            {
                problems.Add($"{table.TableId}: no columns declared");
                continue;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                if (!names.Add(column.Name))
                    problems.Add($"{table.TableId}: duplicate column '{column.Name}'");

                if (string.IsNullOrWhiteSpace(column.SqlType))
                    problems.Add($"{table.TableId}.{column.Name}: empty SqlType");
            }

            // Every key component must be NOT NULL: it is part of the clustered PK,
            // and SQL Server promotes PK columns to NOT NULL whatever the DDL said.
            foreach (var key in table.KeyColumns)
            {
                var index = table.IndexOf(key);
                if (index < 0)
                {
                    problems.Add($"{table.TableId}: key column '{key}' is not in the column list");
                    continue;
                }

                if (!table.Columns[index].Required)
                    problems.Add($"{table.TableId}: key column '{key}' must be Required");
            }

            // RowOrdinal is the merge's de-dup tiebreak and must be last, so the
            // TVP's trailing column is the one 003 orders by.
            if (table.Columns[^1].Name != "RowOrdinal")
                problems.Add($"{table.TableId}: RowOrdinal must be the last TVP column");
        }

        // The parser fills a strongly-typed row and the sink projects it, so the two
        // cannot disagree — but the measure names must still line up with the
        // fixed-width geometry, which is what actually reads the bulletin.
        foreach (var (name, start, end, indicator) in MeasureFields)
        {
            if (OptionTable.IndexOf(name) < 0)
                problems.Add($"geometry: measure '{name}' has no Option column");

            if (FutureTable.IndexOf(name) < 0)
                problems.Add($"geometry: measure '{name}' has no Future column");

            if (start > end)
                problems.Add($"geometry: measure '{name}' has start {start} after end {end}");

            if (indicator != 0 && indicator != end + 1 && indicator != end)
                problems.Add($"geometry: measure '{name}' indicator column {indicator} is outside its span");

            if (indicator != 0 && OptionTable.IndexOf(name + "ABIndicator") < 0)
                problems.Add($"geometry: measure '{name}' carries an indicator but has no {name}ABIndicator column");
        }

        // Spans must be contiguous and ordered, or a slice would read another
        // field's characters. A field that carries an indicator occupies one column
        // MORE than its numeric end, so the cursor advances past that too.
        var cursor = LabelEnd;
        foreach (var (name, start, end, indicator) in MeasureFields)
        {
            if (start != cursor + 1)
                problems.Add($"geometry: measure '{name}' starts at {start}, expected {cursor + 1} (spans must be contiguous)");

            cursor = Math.Max(end, indicator);
        }

        if (cursor != 138)
            problems.Add($"geometry: the last measure ends at {cursor}, expected 138 (the bulletin's PRIOR INT right edge)");

        return problems;
    }
}
