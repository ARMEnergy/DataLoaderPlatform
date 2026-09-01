namespace DataLoader.Argus;

/// <summary>
/// The scalar shapes the Argus feed uses. Drives BOTH the CSV-to-CLR conversion in
/// <see cref="ArgusCsv"/> and the DataTable column type in <see cref="ArgusTableSink"/>.
/// </summary>
public enum ArgusColumnType
{
    /// <summary><c>VARCHAR(n)</c> -> <see cref="string"/>.</summary>
    String,

    /// <summary><c>SMALLINT</c> -> <see cref="short"/>.</summary>
    Int16,

    /// <summary><c>INT</c> -> <see cref="int"/>.</summary>
    Int32,

    /// <summary><c>BIGINT</c> -> <see cref="long"/>. Load-bearing for NewsCategory ids.</summary>
    Int64,

    /// <summary><c>TINYINT</c> -> <see cref="byte"/>.</summary>
    Byte,

    /// <summary><c>DECIMAL(p,s)</c> -> <see cref="decimal"/>.</summary>
    Decimal,

    /// <summary><c>DATE</c> -> <see cref="DateTime"/>, parsed as <c>dd-MMM-yyyy</c>.</summary>
    Date,

    /// <summary><c>TIME(0)</c> -> <see cref="TimeSpan"/>, parsed as <c>HH:mm:ss</c>.</summary>
    Time,

    /// <summary><c>CHAR(1)</c> -> <see cref="string"/> (SqlClient binds CHAR from string).</summary>
    Char
}

/// <summary>
/// Where a column's value comes from when it is NOT a CSV header. Only the DCRDEUS
/// fact feed has these — see <c>docs/apis/Argus.md</c> §5.3.
/// </summary>
public enum ArgusDerived
{
    /// <summary>Read from <see cref="ArgusColumn.SourceHeader"/>.</summary>
    None,

    /// <summary>The uppercased file-name suffix (<c>dhc</c> -> <c>DHC</c>).</summary>
    Module,

    /// <summary>The <c>yyyyMMdd</c> from the file name.</summary>
    SourceFileDate,

    /// <summary>The remote path, e.g. <c>/DCRDEUS/20260827dhc.csv</c>.</summary>
    SourcePath
}

/// <summary>How a feed's rows reach its table.</summary>
public enum ArgusWriteMode
{
    /// <summary>MERGE on the primary key. Never deletes by absence.</summary>
    Merge,

    /// <summary>
    /// Delete-and-reload in one transaction, so the table mirrors the snapshot
    /// including removals. Used by <c>QuoteLookup</c> alone, and guarded against
    /// empty and short sources in the proc (design §4.2).
    /// </summary>
    Replace
}

/// <summary>
/// One column of one feed. This single record drives four things that would
/// otherwise have to be kept in sync by hand:
///
/// <list type="number">
///   <item>which CSV header the reader looks for (<see cref="SourceHeader"/>),</item>
///   <item>how the text is converted (<see cref="Type"/>),</item>
///   <item>the DataTable column name, order and CLR type in the sink,</item>
///   <item>the expected TVP column in <c>sql/Argus/002</c>, asserted by
///         <c>ArgusTvpContractTests</c> against <see cref="SqlType"/>.</item>
/// </list>
///
/// <para>
/// Because the TVP binds BY POSITION, the order of the column list IS the
/// contract. The test parses the real <c>.sql</c> file, so a change on either side
/// alone fails the build rather than silently corrupting rows.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable column name.</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(50)</c>.</param>
/// <param name="Type">Conversion + CLR type.</param>
/// <param name="SourceHeader">CSV header to read, or <c>null</c> when <paramref name="Derived"/> is set.</param>
/// <param name="Required">
/// <c>true</c> => <c>NOT NULL</c> in the TVP, and a row whose value is blank or
/// unparseable is DROPPED rather than merged under a blank key. Every primary-key
/// component is required.
/// </param>
/// <param name="Derived">Non-CSV source for the value.</param>
public sealed record ArgusColumn(
    string Name,
    string SqlType,
    ArgusColumnType Type,
    string? SourceHeader,
    bool Required,
    ArgusDerived Derived = ArgusDerived.None)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        ArgusColumnType.String => typeof(string),
        ArgusColumnType.Char => typeof(string),
        ArgusColumnType.Int16 => typeof(short),
        ArgusColumnType.Int32 => typeof(int),
        ArgusColumnType.Int64 => typeof(long),
        ArgusColumnType.Byte => typeof(byte),
        ArgusColumnType.Decimal => typeof(decimal),
        ArgusColumnType.Date => typeof(DateTime),
        ArgusColumnType.Time => typeof(TimeSpan),
        _ => throw new NotSupportedException($"Unmapped ArgusColumnType '{Type}'.")
    };

    // ---- terse builders, so the registry below reads like the SQL ----

    public static ArgusColumn Str(string name, int size, string header, bool required = false) =>
        new(name, $"VARCHAR({size})", ArgusColumnType.String, header, required);

    public static ArgusColumn I16(string name, string header, bool required = false) =>
        new(name, "SMALLINT", ArgusColumnType.Int16, header, required);

    public static ArgusColumn I32(string name, string header, bool required = false) =>
        new(name, "INT", ArgusColumnType.Int32, header, required);

    public static ArgusColumn I64(string name, string header, bool required = false) =>
        new(name, "BIGINT", ArgusColumnType.Int64, header, required);

    public static ArgusColumn U8(string name, string header, bool required = false) =>
        new(name, "TINYINT", ArgusColumnType.Byte, header, required);

    public static ArgusColumn Dec(string name, int precision, int scale, string header, bool required = false) =>
        new(name, $"DECIMAL({precision},{scale})", ArgusColumnType.Decimal, header, required);

    public static ArgusColumn Dat(string name, string header, bool required = false) =>
        new(name, "DATE", ArgusColumnType.Date, header, required);

    public static ArgusColumn Tim(string name, string header, bool required = false) =>
        new(name, "TIME(0)", ArgusColumnType.Time, header, required);

    public static ArgusColumn Chr(string name, string header, bool required = false) =>
        new(name, "CHAR(1)", ArgusColumnType.Char, header, required);

    /// <summary>A column with no CSV header — the loader supplies the value.</summary>
    public static ArgusColumn Derive(string name, string sqlType, ArgusColumnType type, ArgusDerived from, bool required = true) =>
        new(name, sqlType, type, SourceHeader: null, Required: required, Derived: from);
}

/// <summary>
/// Compile-time description of one feed: which file, which TVP, which proc, and
/// the ordered column list that is the TVP contract.
/// </summary>
/// <param name="FeedId">Stable id — the <c>EnabledFeeds</c> value and the pipeline's id.</param>
/// <param name="RemoteFolder">
/// <c>DOCUMENTATION</c> or <c>DCRDEUS</c>. Combined with the settings' directory
/// paths at run time.
/// </param>
/// <param name="FileName">
/// Exact remote file name for a reference feed; <c>null</c> for the fact feed,
/// whose files are discovered by <c>FileNameDatePattern</c>.
/// </param>
public sealed record ArgusFeedDescriptor(
    string FeedId,
    string DisplayName,
    string RemoteFolder,
    string? FileName,
    string TvpType,
    string MergeProc,
    string TargetTable,
    ArgusWriteMode WriteMode,
    IReadOnlyList<ArgusColumn> Columns)
{
    /// <summary>True for the DCRDEUS feed, whose work units are discovered rather than fixed.</summary>
    public bool IsDatedFeed => FileName is null;

    /// <summary>Headers the reader must find. A missing one fails the whole file.</summary>
    public IEnumerable<string> RequiredHeaders =>
        Columns.Where(c => c.SourceHeader is not null).Select(c => c.SourceHeader!);
}

/// <summary>
/// The feed registry — the single source of truth for every column mapping in this
/// loader.
///
/// <para>
/// Every header string here was copied from the live files on 2026-08-27 (see
/// <c>docs/apis/Argus.md</c> §4). Header spelling is inconsistent across files
/// (<c>TimeStampID</c> / <c>TimestampID</c> / <c>TS Type</c> all mean the same
/// thing), which is exactly why the reader matches by NAME and never by position.
/// </para>
/// <para>
/// <b>Column ORDER in each list is the TVP contract.</b> It matches
/// <c>sql/Argus/002_CreateArgusTvpTypes.sql</c>, and
/// <c>ArgusTvpContractTests</c> parses that file and asserts it.
/// </para>
/// </summary>
public static class ArgusDescriptors
{
    public const string DocumentationFolder = "DOCUMENTATION";
    public const string TimeSeriesFolder = "DCRDEUS";

    // ------------------------------------------------------------------ reference feeds

    /// <summary>
    /// latestCategory.csv. NOTE the source order is (Code, DisplayName, Category)
    /// but the table and TVP are (Code, Category, DisplayName) — a position-based
    /// load would swap the two 500-char columns and corrupt the primary key.
    /// </summary>
    public static readonly ArgusFeedDescriptor Category = new(
        FeedId: "Category",
        DisplayName: "Category hierarchy",
        RemoteFolder: DocumentationFolder,
        FileName: "latestCategory.csv",
        TvpType: "dlp.CategoryLookupTvp",
        MergeProc: "dlp.usp_BulkMergeCategoryLookup",
        TargetTable: "dlp.CategoryLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("Code", 50, "Code", required: true),
            ArgusColumn.Str("Category", 500, "Category", required: true),
            ArgusColumn.Str("DisplayName", 500, "DisplayName")
        });

    /// <summary>latestCodes.csv. Specification is the column added to the supplied DDL.</summary>
    public static readonly ArgusFeedDescriptor Codes = new(
        FeedId: "Codes",
        DisplayName: "Price codes",
        RemoteFolder: DocumentationFolder,
        FileName: "latestCodes.csv",
        TvpType: "dlp.CodeLookupTvp",
        MergeProc: "dlp.usp_BulkMergeCodeLookup",
        TargetTable: "dlp.CodeLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("Code", 50, "Code", required: true),
            ArgusColumn.Str("DisplayName", 2000, "DisplayName"),
            ArgusColumn.Str("DeliveryMode", 50, "DeliveryMode"),
            ArgusColumn.Str("Unit", 50, "Unit"),
            ArgusColumn.Str("Frequency", 50, "Frequency"),
            ArgusColumn.Str("Specification", 50, "Specification")
        });

    /// <summary>latestModuleDetails.csv. Headers TimeStampID / StartDateInModule / EndDateInModule are renamed.</summary>
    public static readonly ArgusFeedDescriptor ModuleDetails = new(
        FeedId: "ModuleDetails",
        DisplayName: "Module / code membership",
        RemoteFolder: DocumentationFolder,
        FileName: "latestModuleDetails.csv",
        TvpType: "dlp.ModuleDetailLookupTvp",
        MergeProc: "dlp.usp_BulkMergeModuleDetailLookup",
        TargetTable: "dlp.ModuleDetailLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("Module", 50, "Module", required: true),
            ArgusColumn.Str("Code", 50, "Code", required: true),
            ArgusColumn.I16("TimestampTypeID", "TimeStampID", required: true),
            ArgusColumn.I16("PriceTypeID", "PriceTypeID", required: true),
            ArgusColumn.I16("ContinuousForwardPeriod", "ContinuousForwardPeriod", required: true),
            ArgusColumn.Dat("StartDate", "StartDateInModule"),
            ArgusColumn.Dat("EndDate", "EndDateInModule")
        });

    /// <summary>
    /// latestModules.csv — also the authority for the DCRDEUS suffix-to-Module
    /// mapping (<c>FileName</c> = <c>dhc</c> -> <c>Module</c> = <c>DHC</c>).
    /// </summary>
    public static readonly ArgusFeedDescriptor Modules = new(
        FeedId: "Modules",
        DisplayName: "Module catalog",
        RemoteFolder: DocumentationFolder,
        FileName: "latestModules.csv",
        TvpType: "dlp.ModuleLookupTvp",
        MergeProc: "dlp.usp_BulkMergeModuleLookup",
        TargetTable: "dlp.ModuleLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("Module", 50, "Module", required: true),
            ArgusColumn.Str("Path", 250, "Path"),
            ArgusColumn.Str("FileName", 50, "FileName"),
            ArgusColumn.Str("Description", 500, "Description"),
            ArgusColumn.Str("Folder", 250, "Folder"),
            ArgusColumn.Str("Time", 50, "Time"),
            ArgusColumn.Tim("LocalTime", "LocalTime"),
            ArgusColumn.Str("LocalTimeZone", 250, "LocalTimeZone")
        });

    /// <summary>latestPricetype.csv.</summary>
    public static readonly ArgusFeedDescriptor PriceType = new(
        FeedId: "PriceType",
        DisplayName: "Price types",
        RemoteFolder: DocumentationFolder,
        FileName: "latestPricetype.csv",
        TvpType: "dlp.PriceTypeLookupTvp",
        MergeProc: "dlp.usp_BulkMergePriceTypeLookup",
        TargetTable: "dlp.PriceTypeLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("PriceTypeID", "PriceTypeID", required: true),
            ArgusColumn.Str("Description", 250, "Description")
        });

    /// <summary>
    /// latestQuotes.csv — the loader's only <see cref="ArgusWriteMode.Replace"/>
    /// feed. The 4-column natural key has 2 313 duplicate groups because rows are
    /// validity-window versions; adding StartDate makes it unique. The table is
    /// specified with no PK, so it is mirrored rather than merged.
    /// </summary>
    public static readonly ArgusFeedDescriptor Quotes = new(
        FeedId: "Quotes",
        DisplayName: "Quote definitions",
        RemoteFolder: DocumentationFolder,
        FileName: "latestQuotes.csv",
        TvpType: "dlp.QuoteLookupTvp",
        MergeProc: "dlp.usp_ReplaceQuoteLookup",
        TargetTable: "dlp.QuoteLookup",
        WriteMode: ArgusWriteMode.Replace,
        Columns: new[]
        {
            ArgusColumn.Str("Code", 50, "Code", required: true),
            ArgusColumn.I16("ContinuousForwardPeriod", "ContinuousForwardPeriod"),
            ArgusColumn.Str("Timing", 50, "Timing"),
            ArgusColumn.Str("ForwardPeriodDescription", 500, "ForwardPeriodDescription"),
            ArgusColumn.I16("TimestampTypeID", "TimestampID"),
            ArgusColumn.I16("PriceTypeID", "PriceTypeID"),
            ArgusColumn.Str("DifferentialBasis", 500, "DifferentialBasis"),
            ArgusColumn.Str("DifferentialBasisTiming", 50, "DifferentialBasisTiming"),
            ArgusColumn.Dat("StartDate", "StartDate"),
            ArgusColumn.Dat("EndDate", "EndDate"),
            ArgusColumn.Str("OldCode", 50, "OldCode"),
            ArgusColumn.U8("DecimalPlaces", "DecimalPlaces")
        });

    /// <summary>latestTimestamp.csv. Source header is TimestampID.</summary>
    public static readonly ArgusFeedDescriptor Timestamp = new(
        FeedId: "Timestamp",
        DisplayName: "Timestamp types",
        RemoteFolder: DocumentationFolder,
        FileName: "latestTimestamp.csv",
        TvpType: "dlp.TimestampTypeLookupTvp",
        MergeProc: "dlp.usp_BulkMergeTimestampTypeLookup",
        TargetTable: "dlp.TimestampTypeLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("TimestampTypeID", "TimestampID", required: true),
            ArgusColumn.Str("Description", 250, "Description")
        });

    /// <summary>latestTiming.csv. Min/MaxForwardPeriod double as year values (up to 2099).</summary>
    public static readonly ArgusFeedDescriptor Timing = new(
        FeedId: "Timing",
        DisplayName: "Timing types",
        RemoteFolder: DocumentationFolder,
        FileName: "latestTiming.csv",
        TvpType: "dlp.TimingLookupTvp",
        MergeProc: "dlp.usp_BulkMergeTimingLookup",
        TargetTable: "dlp.TimingLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("TimingID", "TimingId", required: true),
            ArgusColumn.Str("Description", 250, "Description"),
            ArgusColumn.I16("MinForwardPeriod", "MinForwardPeriod"),
            ArgusColumn.I16("MaxForwardPeriod", "MaxForwardPeriod"),
            ArgusColumn.Str("ForwardPeriodDescription", 500, "ForwardPeriodDescription")
        });

    /// <summary>latestUnits.csv. Headers are SCREAMING_SNAKE in this one file.</summary>
    public static readonly ArgusFeedDescriptor Units = new(
        FeedId: "Units",
        DisplayName: "Units",
        RemoteFolder: DocumentationFolder,
        FileName: "latestUnits.csv",
        TvpType: "dlp.UnitLookupTvp",
        MergeProc: "dlp.usp_BulkMergeUnitLookup",
        TargetTable: "dlp.UnitLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("UnitID", "UNIT_ID", required: true),
            ArgusColumn.Str("Description", 250, "DESCRIPTION"),
            ArgusColumn.Str("UnitDetails", 250, "UNIT_DETAILS")
        });

    /// <summary>
    /// latestUnitCodeConv.csv. CodeID is an INT here, not a 'PAxxxxxxx' string.
    /// Ratio needs DECIMAL(28,17) — 17 decimal places observed live.
    /// </summary>
    public static readonly ArgusFeedDescriptor UnitCodeConv = new(
        FeedId: "UnitCodeConv",
        DisplayName: "Unit conversion ratios",
        RemoteFolder: DocumentationFolder,
        FileName: "latestUnitCodeConv.csv",
        TvpType: "dlp.UnitCodeConversionTvp",
        MergeProc: "dlp.usp_BulkMergeUnitCodeConversion",
        TargetTable: "dlp.UnitCodeConversion",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("UnitID", "UnitID", required: true),
            ArgusColumn.I16("BaseUnitID", "BaseUnitID", required: true),
            ArgusColumn.I32("CodeID", "CodeID", required: true),
            ArgusColumn.Dat("ValidFrom", "ValidFrom", required: true),
            ArgusColumn.Dat("ValidTo", "ValidTo"),
            ArgusColumn.Dec("Ratio", 28, 17, "Ratio")
        });

    /// <summary>latestHolidayRegion.csv.</summary>
    public static readonly ArgusFeedDescriptor HolidayRegion = new(
        FeedId: "HolidayRegion",
        DisplayName: "Holiday regions",
        RemoteFolder: DocumentationFolder,
        FileName: "latestHolidayRegion.csv",
        TvpType: "dlp.HolidayRegionLookupTvp",
        MergeProc: "dlp.usp_BulkMergeHolidayRegionLookup",
        TargetTable: "dlp.HolidayRegionLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("HolidayRegionID", "HolidayRegionID", required: true),
            ArgusColumn.Str("HolidayRegionDescription", 250, "HolidayRegionDescription")
        });

    /// <summary>latestHoliday.csv. The source ships 11 exact duplicate rows; the merge de-duplicates.</summary>
    public static readonly ArgusFeedDescriptor Holiday = new(
        FeedId: "Holiday",
        DisplayName: "Holiday calendar",
        RemoteFolder: DocumentationFolder,
        FileName: "latestHoliday.csv",
        TvpType: "dlp.HolidayTvp",
        MergeProc: "dlp.usp_BulkMergeHoliday",
        TargetTable: "dlp.Holiday",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.I16("HolidayRegionID", "HolidayRegionID", required: true),
            ArgusColumn.Dat("HolidayDate", "HolidayDate", required: true)
        });

    /// <summary>latestQuoteHolidayRegion.csv. Source header TimeStampID is renamed.</summary>
    public static readonly ArgusFeedDescriptor QuoteHolidayRegion = new(
        FeedId: "QuoteHolidayRegion",
        DisplayName: "Quote / holiday-region map",
        RemoteFolder: DocumentationFolder,
        FileName: "latestQuoteHolidayRegion.csv",
        TvpType: "dlp.QuoteHolidayRegionTvp",
        MergeProc: "dlp.usp_BulkMergeQuoteHolidayRegion",
        TargetTable: "dlp.QuoteHolidayRegion",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("Code", 50, "Code", required: true),
            ArgusColumn.I16("ContinuousForwardPeriod", "ContinuousForwardPeriod", required: true),
            ArgusColumn.I16("TimestampTypeID", "TimeStampID", required: true),
            ArgusColumn.I16("PriceTypeID", "PriceTypeID", required: true),
            ArgusColumn.I16("HolidayRegionID1", "HolidayRegionID1"),
            ArgusColumn.I16("HolidayRegionID2", "HolidayRegionID2"),
            ArgusColumn.I16("HolidayRegionID3", "HolidayRegionID3")
        });

    /// <summary>
    /// latestNewsCategory.csv. CategoryID/ParentID are Int64 and that is
    /// LOAD-BEARING — the live data reaches 10 000 004 936, which overflows Int32.
    /// </summary>
    public static readonly ArgusFeedDescriptor NewsCategory = new(
        FeedId: "NewsCategory",
        DisplayName: "News categories",
        RemoteFolder: DocumentationFolder,
        FileName: "latestNewsCategory.csv",
        TvpType: "dlp.NewsCategoryLookupTvp",
        MergeProc: "dlp.usp_BulkMergeNewsCategoryLookup",
        TargetTable: "dlp.NewsCategoryLookup",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("CategoryType", 50, "CATEGORY_TYPE", required: true),
            ArgusColumn.I64("CategoryID", "CATEGORY_ID", required: true),
            ArgusColumn.I64("ParentID", "PARENT_ID"),
            ArgusColumn.Str("Description", 500, "DESCRIPTION"),
            ArgusColumn.Chr("Active", "ACTIVE")
        });

    /// <summary>latestRVP_Code_reference.csv.</summary>
    public static readonly ArgusFeedDescriptor RvpCodeReference = new(
        FeedId: "RvpCodeReference",
        DisplayName: "RVP code cross-reference",
        RemoteFolder: DocumentationFolder,
        FileName: "latestRVP_Code_reference.csv",
        TvpType: "dlp.RvpCodeReferenceTvp",
        MergeProc: "dlp.usp_BulkMergeRvpCodeReference",
        TargetTable: "dlp.RvpCodeReference",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Str("CodeID", 50, "CODE_ID", required: true),
            ArgusColumn.Str("RvpCodeID", 50, "RVP_CODE_ID", required: true)
        });

    // ------------------------------------------------------------------ fact feed

    /// <summary>
    /// DCRDEUS <c>&lt;yyyyMMdd&gt;&lt;suffix&gt;.csv</c>. One descriptor, many files.
    ///
    /// <para>
    /// Three columns are DERIVED because the CSV does not carry them: Module (the
    /// file-name suffix), RecordStatusDate (the file-name date) and SourcePath.
    /// SourceFileDate is a fourth derived column that exists ONLY in the TVP — it
    /// is the merge ordering guard, not a column of either fact table.
    /// </para>
    /// </summary>
    public static readonly ArgusFeedDescriptor TimeSeries = new(
        FeedId: "TimeSeries",
        DisplayName: "DCRDEUS daily price time series",
        RemoteFolder: TimeSeriesFolder,
        FileName: null,
        TvpType: "dlp.TimeSeriesDetailTvp",
        MergeProc: "dlp.usp_BulkMergeTimeSeriesDetail",
        TargetTable: "dlp.TimeSeriesDetail",
        WriteMode: ArgusWriteMode.Merge,
        Columns: new[]
        {
            ArgusColumn.Derive("Module", "VARCHAR(50)", ArgusColumnType.String, ArgusDerived.Module),
            ArgusColumn.Str("Code", 50, "Code", required: true),
            ArgusColumn.I16("TimestampTypeID", "TS Type", required: true),
            ArgusColumn.I16("PriceTypeID", "PT Code", required: true),
            ArgusColumn.I16("ContFwd", "Cont Fwd", required: true),
            ArgusColumn.Dat("Date", "Date", required: true),
            ArgusColumn.Chr("RecordStatus", "Record Status", required: true),
            ArgusColumn.Derive("RecordStatusDate", "DATE", ArgusColumnType.Date, ArgusDerived.SourceFileDate, required: false),
            ArgusColumn.I16("FwdPeriod", "Fwd Period"),
            ArgusColumn.Dec("Value", 18, 6, "Value"),
            ArgusColumn.I16("DiffBaseRoll", "Diff Base Roll"),
            ArgusColumn.I16("Year", "Year"),
            ArgusColumn.Derive("SourcePath", "VARCHAR(500)", ArgusColumnType.String, ArgusDerived.SourcePath, required: false),
            ArgusColumn.Derive("SourceFileDate", "DATE", ArgusColumnType.Date, ArgusDerived.SourceFileDate)
        });

    // ------------------------------------------------------------------ registry

    /// <summary>The 15 DOCUMENTATION feeds, in a stable order for deterministic logs.</summary>
    public static readonly IReadOnlyList<ArgusFeedDescriptor> Documentation = new[]
    {
        Category, Codes, ModuleDetails, Modules, PriceType, Quotes, Timestamp,
        Timing, Units, UnitCodeConv, HolidayRegion, Holiday, QuoteHolidayRegion,
        NewsCategory, RvpCodeReference
    };

    /// <summary>All 16 feeds. Reference feeds first so the lookups exist before the facts land.</summary>
    public static readonly IReadOnlyList<ArgusFeedDescriptor> All =
        Documentation.Append(TimeSeries).ToArray();

    /// <summary>Look up a feed by its id, case-insensitively. Null when unknown.</summary>
    public static ArgusFeedDescriptor? Find(string feedId) =>
        All.FirstOrDefault(f => string.Equals(f.FeedId, feedId, StringComparison.OrdinalIgnoreCase));
}
