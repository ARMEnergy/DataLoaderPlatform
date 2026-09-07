namespace DataLoader.EOX;

/// <summary>
/// The scalar shapes this feed uses. Drives BOTH the CSV-to-CLR conversion in
/// <see cref="EoxCsv"/> and the DataTable column type in <see cref="EoxTableSink"/>.
/// </summary>
public enum EoxColumnType
{
    /// <summary><c>VARCHAR(n)</c> -&gt; <see cref="string"/>.</summary>
    String,

    /// <summary><c>DATE</c> -&gt; <see cref="DateTime"/>. See <see cref="EoxCsv.DateFormats"/>.</summary>
    Date,

    /// <summary>
    /// <c>FLOAT</c> -&gt; <see cref="double"/>. The requester's DDL specifies FLOAT
    /// for every price column, so the CLR side is <c>double</c> and not
    /// <c>decimal</c> — a <c>decimal</c> DataTable column against a <c>FLOAT</c>
    /// TVP column would force a server-side conversion on every row.
    /// </summary>
    Double
}

/// <summary>Where a column's value comes from when it is NOT a CSV header.</summary>
public enum EoxDerived
{
    /// <summary>Read from <see cref="EoxColumn.SourceHeaders"/>.</summary>
    None,

    /// <summary>
    /// The bare source file name, e.g. <c>EOD_CSV_NG_20260904_1430.csv</c>. Stored
    /// on every fact row as provenance AND used by the merge procs as the ordering
    /// guard, which is why it is <c>Required</c> in every TVP.
    /// </summary>
    FileName
}

/// <summary>
/// One column of one feed. This single record drives four things that would
/// otherwise have to be kept in sync by hand:
///
/// <list type="number">
///   <item>which CSV header(s) the reader looks for (<see cref="SourceHeaders"/>),</item>
///   <item>how the text is converted (<see cref="Type"/>),</item>
///   <item>the DataTable column name, order and CLR type in the sink,</item>
///   <item>the expected TVP column in <c>sql/EOX/002</c>, asserted by
///         <c>EoxTvpContractTests</c> against <see cref="SqlType"/>.</item>
/// </list>
///
/// <para>
/// Because the TVP binds BY POSITION, the ORDER of a feed's column list IS the
/// contract. The test parses the real <c>.sql</c> file, so changing one side alone
/// fails the build rather than silently corrupting rows.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable column name.</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(8)</c>.</param>
/// <param name="Type">Conversion + CLR type.</param>
/// <param name="SourceHeaders">
/// CSV headers to look for, IN PREFERENCE ORDER; empty when <paramref name="Derived"/>
/// is set. More than one entry is not defensive padding — EOX really did rename
/// columns mid-history (<c>Line</c>/<c>Number</c>, <c>Data_Code</c>/<c>Code</c>),
/// and a backfill run reads those older files.
/// </param>
/// <param name="Required">
/// <c>true</c> =&gt; <c>NOT NULL</c> in the TVP, and a row whose value is blank or
/// unparseable is DROPPED rather than merged under a blank key. Every primary-key
/// component is required, and so is <c>FileName</c> (the ordering guard).
/// </param>
/// <param name="HeaderOptional">
/// <c>true</c> =&gt; a file whose header lacks this column still loads, with the
/// column NULL for every row. Only <c>FP</c> uses this: NaturalGas files published
/// before roughly 2016 have 14 fields and no <c>FP</c> at all. Never combined with
/// <paramref name="Required"/> — see <see cref="EoxDescriptors.Validate"/>.
/// </param>
/// <param name="Derived">Non-CSV source for the value.</param>
public sealed record EoxColumn(
    string Name,
    string SqlType,
    EoxColumnType Type,
    IReadOnlyList<string> SourceHeaders,
    bool Required,
    bool HeaderOptional = false,
    EoxDerived Derived = EoxDerived.None)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        EoxColumnType.String => typeof(string),
        EoxColumnType.Date => typeof(DateTime),
        EoxColumnType.Double => typeof(double),
        _ => throw new NotSupportedException($"Unmapped EoxColumnType '{Type}'.")
    };

    // ---- terse builders, so the registry below reads like the SQL ----

    public static EoxColumn Str(string name, int size, bool required = false, params string[] headers) =>
        new(name, $"VARCHAR({size})", EoxColumnType.String, headers, required);

    public static EoxColumn Dat(string name, bool required = false, params string[] headers) =>
        new(name, "DATE", EoxColumnType.Date, headers, required);

    public static EoxColumn Flt(string name, params string[] headers) =>
        new(name, "FLOAT", EoxColumnType.Double, headers, Required: false);

    /// <summary>A float whose header may be absent from older files (<c>FP</c> only).</summary>
    public static EoxColumn FltOptionalHeader(string name, params string[] headers) =>
        new(name, "FLOAT", EoxColumnType.Double, headers, Required: false, HeaderOptional: true);

    /// <summary>A column with no CSV header — the loader supplies the value.</summary>
    public static EoxColumn Derive(string name, string sqlType, EoxColumnType type, EoxDerived from) =>
        new(name, sqlType, type, Array.Empty<string>(), Required: true, HeaderOptional: false, Derived: from);
}

/// <summary>
/// Compile-time description of one feed: which file series, which TVP, which
/// merge proc, and the ordered column list that is the TVP contract.
/// </summary>
/// <param name="FeedId">Stable id — the <c>EnabledFeeds</c> value and the pipeline's id.</param>
/// <param name="FilePrefix">
/// Everything in the file name before the <c>yyyyMMdd</c> token, e.g.
/// <c>EOD_CSV_NG_</c>. Combined with the date and
/// <c>EoxSettings.FileNameTimeToken</c> by <see cref="FileNameFor"/>.
/// </param>
/// <param name="KeyColumns">
/// The target table's primary key, in order. Used by the descriptor self-check to
/// prove every key component is <c>Required</c>, and by the tests that pin the
/// merge procs' <c>PARTITION BY</c>.
/// </param>
public sealed record EoxFeedDescriptor(
    string FeedId,
    string DisplayName,
    string FilePrefix,
    string TvpType,
    string MergeProc,
    string TargetTable,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<EoxColumn> Columns)
{
    /// <summary>
    /// The exact remote file name for one curve date, e.g.
    /// <c>EOD_CSV_NG_20260904_1430.csv</c>.
    ///
    /// <para>
    /// Addressing files by an exact constructed name (rather than by a glob) is
    /// what keeps the ~20 <c>"…(HOST's conflicted copy DATE).csv"</c> files sitting
    /// in the same directory out of the load — they share the prefix and the date
    /// but not the whole name.
    /// </para>
    /// </summary>
    public string FileNameFor(DateOnly curveDate, string timeToken) =>
        $"{FilePrefix}{EoxTime.FileToken(curveDate)}_{timeToken}.csv";
}

/// <summary>
/// The feed registry — the single source of truth for every column mapping in this
/// loader.
///
/// <para>
/// Every header string here was copied from the live files on 2026-09-04, sampled
/// across 2011..2026 (see <c>docs/apis/EOX.md</c> §3). Header spelling is NOT
/// stable across that history, which is exactly why the reader matches by NAME and
/// never by position:
/// </para>
/// <list type="bullet">
///   <item>2014 CrudeOil and NGL files head column 1 <c>Number</c>, not <c>Line</c>;</item>
///   <item>2014 NGL heads column 2 <c>Code</c>, not <c>Data_Code</c>;</item>
///   <item>NaturalGas files before ~2016 have no <c>FP</c> column at all.</item>
/// </list>
/// <para>
/// <b>Column ORDER in each list is the TVP contract.</b> It matches
/// <c>sql/EOX/002_CreateEoxTvpTypes.sql</c>, and <c>EoxTvpContractTests</c> parses
/// that file and asserts it.
/// </para>
/// </summary>
public static class EoxDescriptors
{
    /// <summary>
    /// <c>EOD_CSV_C_&lt;yyyyMMdd&gt;_1430.csv</c> — crude oil curves. ~20,800 rows/day.
    /// Header (2026): <c>Line, Code, Curve_Date, Locat._Code, Contract_Term,
    /// Contract_Name, Contract_Begin, Contract_End, Time_Key, Location, Mid, Bid, Ask</c>.
    /// Dates are <c>yyyy-MM-dd</c> in this feed.
    /// </summary>
    public static readonly EoxFeedDescriptor CrudeOil = new(
        FeedId: "CrudeOil",
        DisplayName: "Crude oil EOD curves",
        FilePrefix: "EOD_CSV_C_",
        TvpType: "arm.CrudeOilTvp",
        MergeProc: "arm.usp_BulkMergeCrudeOil",
        TargetTable: "arm.CrudeOil",
        KeyColumns: new[] { "CurveDate", "LocationCode", "TimeKey" },
        Columns: new[]
        {
            EoxColumn.Dat("CurveDate", true, "Curve_Date"),
            EoxColumn.Str("LocationCode", 8, true, "Locat._Code"),
            EoxColumn.Str("TimeKey", 16, true, "Time_Key"),
            EoxColumn.Str("Line", 64, false, "Line", "Number"),
            EoxColumn.Str("Code", 128, false, "Code", "Data_Code"),
            EoxColumn.Str("ContractTerm", 32, false, "Contract_Term"),
            EoxColumn.Str("ContractName", 64, false, "Contract_Name"),
            EoxColumn.Dat("ContractBegin", false, "Contract_Begin"),
            EoxColumn.Dat("ContractEnd", false, "Contract_End"),
            EoxColumn.Str("Location", 128, false, "Location"),
            EoxColumn.Flt("Mid", "Mid"),
            EoxColumn.Flt("Bid", "Bid"),
            EoxColumn.Flt("Ask", "Ask"),
            EoxColumn.Derive("FileName", "VARCHAR(128)", EoxColumnType.String, EoxDerived.FileName)
        });

    /// <summary>
    /// <c>EOD_CSV_NG_&lt;yyyyMMdd&gt;_1430.csv</c> — natural gas curves. ~34,000 rows/day
    /// across 224 market codes. Header (2026): <c>Line, Data_Code, Curve_Date,
    /// Region, Market, Market_Code, Contract_Name, Contract_Term, Contract_Begin,
    /// Contract_End, Time_Key, Mid, Bid, Ask, FP</c>.
    ///
    /// <para>
    /// Two things are unique to this feed: dates are <c>MM/dd/yy</c> (two-digit
    /// year — see <see cref="EoxCsv.ExpandTwoDigitYear"/>), and <c>FP</c> is absent
    /// from files older than roughly 2016.
    /// </para>
    /// </summary>
    public static readonly EoxFeedDescriptor NaturalGas = new(
        FeedId: "NaturalGas",
        DisplayName: "Natural gas EOD curves",
        FilePrefix: "EOD_CSV_NG_",
        TvpType: "arm.NaturalGasTvp",
        MergeProc: "arm.usp_BulkMergeNaturalGas",
        TargetTable: "arm.NaturalGas",
        KeyColumns: new[] { "CurveDate", "MarketCode", "TimeKey" },
        Columns: new[]
        {
            EoxColumn.Dat("CurveDate", true, "Curve_Date"),
            EoxColumn.Str("MarketCode", 8, true, "Market_Code"),
            EoxColumn.Str("TimeKey", 16, true, "Time_Key"),
            EoxColumn.Str("Line", 64, false, "Line", "Number"),
            EoxColumn.Str("Code", 128, false, "Data_Code", "Code"),
            EoxColumn.Str("Region", 64, false, "Region"),
            EoxColumn.Str("Market", 128, false, "Market"),
            EoxColumn.Str("ContractName", 64, false, "Contract_Name"),
            EoxColumn.Str("ContractTerm", 32, false, "Contract_Term"),
            EoxColumn.Dat("ContractBegin", false, "Contract_Begin"),
            EoxColumn.Dat("ContractEnd", false, "Contract_End"),
            EoxColumn.Flt("Mid", "Mid"),
            EoxColumn.Flt("Bid", "Bid"),
            EoxColumn.Flt("Ask", "Ask"),
            EoxColumn.FltOptionalHeader("FP", "FP"),
            EoxColumn.Derive("FileName", "VARCHAR(128)", EoxColumnType.String, EoxDerived.FileName)
        });

    /// <summary>
    /// <c>EOD_CSV_NGL_&lt;yyyyMMdd&gt;_1430.csv</c> — NGL curves. ~4,560 rows/day.
    /// Same shape as <see cref="CrudeOil"/>, but column 2 is <c>Data_Code</c> in
    /// modern files and plain <c>Code</c> in the 2014 ones. Dates are
    /// <c>yyyy-MM-dd</c>.
    /// </summary>
    public static readonly EoxFeedDescriptor Ngl = new(
        FeedId: "NGL",
        DisplayName: "NGL EOD curves",
        FilePrefix: "EOD_CSV_NGL_",
        TvpType: "arm.NGLTvp",
        MergeProc: "arm.usp_BulkMergeNGL",
        TargetTable: "arm.NGL",
        KeyColumns: new[] { "CurveDate", "LocationCode", "TimeKey" },
        Columns: new[]
        {
            EoxColumn.Dat("CurveDate", true, "Curve_Date"),
            EoxColumn.Str("LocationCode", 8, true, "Locat._Code"),
            EoxColumn.Str("TimeKey", 16, true, "Time_Key"),
            EoxColumn.Str("Line", 64, false, "Line", "Number"),
            EoxColumn.Str("Code", 128, false, "Data_Code", "Code"),
            EoxColumn.Str("ContractTerm", 32, false, "Contract_Term"),
            EoxColumn.Str("ContractName", 64, false, "Contract_Name"),
            EoxColumn.Dat("ContractBegin", false, "Contract_Begin"),
            EoxColumn.Dat("ContractEnd", false, "Contract_End"),
            EoxColumn.Str("Location", 128, false, "Location"),
            EoxColumn.Flt("Mid", "Mid"),
            EoxColumn.Flt("Bid", "Bid"),
            EoxColumn.Flt("Ask", "Ask"),
            EoxColumn.Derive("FileName", "VARCHAR(128)", EoxColumnType.String, EoxDerived.FileName)
        });

    /// <summary>All three feeds, in the order a fresh load runs them.</summary>
    public static readonly IReadOnlyList<EoxFeedDescriptor> All = new[] { CrudeOil, NaturalGas, Ngl };

    /// <summary>Case-insensitive lookup by feed id; null when unknown.</summary>
    public static EoxFeedDescriptor? Find(string feedId) =>
        All.FirstOrDefault(f => string.Equals(f.FeedId, feedId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Self-check over the registry above, run once from the module at startup and
    /// asserted by the tests. These are the invariants that make the rest of the
    /// loader safe, and each of them is a one-character edit away from being false:
    ///
    /// <list type="bullet">
    ///   <item>every primary-key column exists and is <c>Required</c>, so a blank
    ///         can never be merged under a partial key;</item>
    ///   <item><c>Required</c> and <c>HeaderOptional</c> are never both set — a
    ///         missing header cannot supply a value a row is dropped without;</item>
    ///   <item>every non-derived column names at least one source header;</item>
    ///   <item>no feed carries a DB-stamped column in its TVP.</item>
    /// </list>
    /// </summary>
    /// <param name="feeds">
    /// Defaults to the real registry. Overridable so the tests can prove each rule
    /// actually fires — a self-check nobody has seen fail is a self-check nobody
    /// knows works.
    /// </param>
    /// <returns>Human-readable problems; empty when the registry is sound.</returns>
    public static IReadOnlyList<string> Validate(IReadOnlyList<EoxFeedDescriptor>? feeds = null)
    {
        var problems = new List<string>();

        foreach (var feed in feeds ?? All)
        {
            var byName = new Dictionary<string, EoxColumn>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in feed.Columns)
            {
                if (!byName.TryAdd(column.Name, column))
                    problems.Add($"{feed.FeedId}: duplicate column '{column.Name}'.");

                if (column.Required && column.HeaderOptional)
                    problems.Add($"{feed.FeedId}.{column.Name}: Required and HeaderOptional are mutually exclusive.");

                if (column.Derived == EoxDerived.None && column.SourceHeaders.Count == 0)
                    problems.Add($"{feed.FeedId}.{column.Name}: no source header and no derived source.");

                if (column.Derived != EoxDerived.None && column.SourceHeaders.Count > 0)
                    problems.Add($"{feed.FeedId}.{column.Name}: derived columns must not name a source header.");

                if (column.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase) ||
                    column.Name.Equals("DateCreated", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{feed.FeedId}.{column.Name}: DB-stamped columns must not appear in a TVP.");
            }

            foreach (var key in feed.KeyColumns)
            {
                if (!byName.TryGetValue(key, out var column))
                    problems.Add($"{feed.FeedId}: key column '{key}' is not in the column list.");
                else if (!column.Required)
                    problems.Add($"{feed.FeedId}.{key}: primary-key columns must be Required.");
            }

            var fileName = feed.Columns.LastOrDefault();
            if (fileName is null || fileName.Derived != EoxDerived.FileName)
                problems.Add($"{feed.FeedId}: the last TVP column must be the derived FileName ordering guard.");
            else if (!fileName.Required)
                problems.Add($"{feed.FeedId}.FileName: must be Required — the merge guard cannot order a NULL.");
        }

        return problems;
    }
}
