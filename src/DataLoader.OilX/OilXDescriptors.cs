namespace DataLoader.OilX;

/// <summary>
/// The scalar shapes the OilX feeds use. Drives BOTH the CSV-to-CLR conversion in
/// <see cref="OilXCsv"/> and the DataTable column type in <see cref="OilXTableSink"/>.
/// </summary>
public enum OilXColumnType
{
    /// <summary><c>VARCHAR(n)</c> / <c>VARCHAR(MAX)</c> -&gt; <see cref="string"/>.</summary>
    String,

    /// <summary><c>DATE</c> -&gt; <see cref="DateTime"/> at midnight.</summary>
    Date,

    /// <summary><c>DATETIME2(n)</c> -&gt; <see cref="DateTime"/>.</summary>
    DateTime,

    /// <summary>
    /// <c>DECIMAL(p,8)</c> -&gt; <see cref="decimal"/>, ROUNDED to 8 dp on conversion.
    /// See <see cref="OilXCsv.DecimalScale"/> for why the rounding is not optional.
    /// </summary>
    Decimal,

    /// <summary>
    /// <c>FLOAT</c> -&gt; <see cref="double"/>. The supplied DDL specifies FLOAT for
    /// CargoTracking's quantity/gravity columns, so the CLR side is <c>double</c> and
    /// not <c>decimal</c> — a decimal DataTable column against a FLOAT TVP column
    /// would force a server-side conversion on every one of ~400,000 rows.
    /// </summary>
    Double,

    /// <summary><c>UNIQUEIDENTIFIER</c> -&gt; <see cref="Guid"/>. Only <c>RowId</c>.</summary>
    Guid,

    /// <summary><c>INT</c> -&gt; <see cref="int"/>. Only <c>Checksum</c>.</summary>
    Int
}

/// <summary>Where a column's value comes from when it is NOT read from a CSV header.</summary>
public enum OilXDerived
{
    /// <summary>Read from <see cref="OilXColumn.SourceHeaders"/>.</summary>
    None,

    /// <summary>
    /// The deterministic UUIDv5 of the feed's business key — see <see cref="OilXRowId"/>.
    /// Computed AFTER every CSV-sourced column has been converted, because the hash is
    /// over normalised values rather than raw text.
    /// </summary>
    RowId,

    /// <summary>
    /// The bare source file name, e.g. <c>CargoTracking.2026-09-30T03-39.csv</c>.
    /// Provenance: it names the snapshot that last wrote the row. Deliberately NOT part
    /// of <see cref="Checksum"/> — a later snapshot that changed nothing must not
    /// register as a change.
    /// </summary>
    FileName,

    /// <summary>
    /// FNV-1a/32 over the row's VALUE columns — see <see cref="OilXChecksum"/>.
    /// Computed last, since it reads every other column.
    /// </summary>
    Checksum
}

/// <summary>
/// One column of one feed. This single record drives four things that would otherwise
/// have to be kept in sync by hand:
///
/// <list type="number">
///   <item>which CSV header the reader looks for (<see cref="SourceHeaders"/>),</item>
///   <item>how the text is converted (<see cref="Type"/>),</item>
///   <item>the DataTable column name, order and CLR type in the sink,</item>
///   <item>the expected TVP column in <c>sql/OilX/002</c>, asserted by
///         <c>OilXTvpContractTests</c> against <see cref="SqlType"/> and
///         <see cref="Required"/>.</item>
/// </list>
///
/// <para>
/// Because the TVP binds BY POSITION, the ORDER of a feed's column list IS the
/// contract. The test parses the real <c>.sql</c> file, so changing one side alone
/// fails the build rather than silently corrupting rows.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable / table column name.</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(250)</c>.</param>
/// <param name="Type">Conversion + CLR type.</param>
/// <param name="SourceHeaders">
/// CSV headers to look for, IN PREFERENCE ORDER; empty when <paramref name="Derived"/>
/// is set. Several entries exist where the CSV spelling differs from the column name —
/// <c>GroupbyDateIndicator</c> (lower-case b) and <c>LoadQuantity(KT)</c> are real,
/// live spellings, not defensive padding.
/// </param>
/// <param name="Required">
/// <c>true</c> =&gt; <c>NOT NULL</c> in the TVP, and a row whose value is blank or
/// unparseable is DROPPED and counted rather than merged under a blank key. Only
/// <c>RunDate</c>, <c>RowId</c>, <c>FileName</c> and <c>Checksum</c> are required.
/// </param>
/// <param name="IsKey">
/// <c>true</c> =&gt; this column feeds <see cref="OilXRowId"/>. Key columns are
/// EXCLUDED from <see cref="OilXChecksum"/>: they are identical on both sides of every
/// matched comparison, so hashing them would add nothing.
///
/// <para>
/// A key column may legitimately be BLANK — Flow's two subcountry columns usually are
/// — which is why key columns are not <paramref name="Required"/>. A blank cell
/// converts to NULL (see <see cref="OilXCsv.TryConvert"/>), so it contributes
/// <see cref="OilXCanonical.NullToken"/> to the hash: a stable, intentional identity
/// rather than a blank key.
/// </para>
/// </param>
/// <param name="Derived">Non-CSV source for the value.</param>
public sealed record OilXColumn(
    string Name,
    string SqlType,
    OilXColumnType Type,
    IReadOnlyList<string> SourceHeaders,
    bool Required = false,
    bool IsKey = false,
    OilXDerived Derived = OilXDerived.None)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        OilXColumnType.String => typeof(string),
        OilXColumnType.Date => typeof(DateTime),
        OilXColumnType.DateTime => typeof(DateTime),
        OilXColumnType.Decimal => typeof(decimal),
        OilXColumnType.Double => typeof(double),
        OilXColumnType.Guid => typeof(Guid),
        OilXColumnType.Int => typeof(int),
        _ => throw new NotSupportedException($"Unmapped OilXColumnType '{Type}'.")
    };

    /// <summary>True when the value comes from the CSV rather than being computed.</summary>
    public bool FromCsv => Derived == OilXDerived.None;

    // ---- terse builders, so the registry below reads like the SQL ----

    /// <summary>A <c>VARCHAR(size)</c> column. <paramref name="headers"/> defaults to the name.</summary>
    public static OilXColumn Str(string name, int size, params string[] headers) =>
        new(name, $"VARCHAR({size})", OilXColumnType.String, Headers(name, headers));

    /// <summary>A <c>VARCHAR(MAX)</c> column.</summary>
    public static OilXColumn StrMax(string name, params string[] headers) =>
        new(name, "VARCHAR(MAX)", OilXColumnType.String, Headers(name, headers));

    /// <summary>A <c>VARCHAR(size)</c> column that is part of the business key.</summary>
    public static OilXColumn KeyStr(string name, int size, params string[] headers) =>
        new(name, $"VARCHAR({size})", OilXColumnType.String, Headers(name, headers), IsKey: true);

    /// <summary>A <c>DATE</c> column.</summary>
    public static OilXColumn Dat(string name, params string[] headers) =>
        new(name, "DATE", OilXColumnType.Date, Headers(name, headers));

    /// <summary>A <c>DATE</c> column that is part of the business key.</summary>
    public static OilXColumn KeyDat(string name, params string[] headers) =>
        new(name, "DATE", OilXColumnType.Date, Headers(name, headers), IsKey: true);

    /// <summary>A <c>DATETIME2(precision)</c> column.</summary>
    public static OilXColumn DtM(string name, int precision, params string[] headers) =>
        new(name, $"DATETIME2({precision})", OilXColumnType.DateTime, Headers(name, headers));

    /// <summary>A <c>DATETIME2(precision)</c> column that is part of the business key.</summary>
    public static OilXColumn KeyDtM(string name, int precision, params string[] headers) =>
        new(name, $"DATETIME2({precision})", OilXColumnType.DateTime, Headers(name, headers), IsKey: true);

    /// <summary>A <c>DECIMAL(precision,8)</c> column.</summary>
    public static OilXColumn Dec(string name, int precision, params string[] headers) =>
        new(name, $"DECIMAL({precision},8)", OilXColumnType.Decimal, Headers(name, headers));

    /// <summary>A <c>FLOAT</c> column.</summary>
    public static OilXColumn Flt(string name, params string[] headers) =>
        new(name, "FLOAT", OilXColumnType.Double, Headers(name, headers));

    /// <summary>
    /// <c>RunDate</c> — the one key-ish column that is READ FROM THE FILE rather than
    /// taken from the loader's clock. All of a day's snapshots carry the same value,
    /// which is exactly why they merge over one another (docs/apis/OilX.md §2
    /// Behaviour 2). It is NOT marked <see cref="IsKey"/>: it is a primary-key column
    /// in its own right, but <see cref="OilXRowId"/> identifies the SERIES WITHIN a
    /// day, so the same series carries the same RowId on every RunDate.
    /// </summary>
    public static OilXColumn RunDate() =>
        new("RunDate", "DATE", OilXColumnType.Date, new[] { "RunDate" }, Required: true);

    public static OilXColumn RowId() =>
        new("RowId", "UNIQUEIDENTIFIER", OilXColumnType.Guid, Array.Empty<string>(),
            Required: true, IsKey: false, Derived: OilXDerived.RowId);

    public static OilXColumn FileName() =>
        new("FileName", "VARCHAR(100)", OilXColumnType.String, Array.Empty<string>(),
            Required: true, IsKey: false, Derived: OilXDerived.FileName);

    public static OilXColumn Checksum() =>
        new("Checksum", "INT", OilXColumnType.Int, Array.Empty<string>(),
            Required: true, IsKey: false, Derived: OilXDerived.Checksum);

    private static IReadOnlyList<string> Headers(string name, string[] headers) =>
        headers.Length == 0 ? new[] { name } : headers;
}

/// <summary>
/// Compile-time description of one feed: which CSV file series, which TVP, which merge
/// proc, and the ordered column list that is the TVP contract.
/// </summary>
/// <param name="FeedId">
/// Stable id — the <c>EnabledFeeds</c> value, the pipeline's id, AND the <c>files=</c>
/// query-parameter value. It is the VENDOR's file name, which is why
/// <c>Regional_Balance</c> keeps its underscore while its table is
/// <c>arm.RegionalBalance</c>.
/// </param>
/// <param name="FileNamePrefix">
/// What an S3 object name starts with for this feed, e.g. <c>CargoTracking.</c>. Used
/// to attribute a manifest entry to a feed, since the manifest carries no feed field
/// (docs/apis/OilX.md §3.1). The trailing dot matters: without it
/// <c>Terminals</c> would also match <c>TerminalsWeekly</c>.
/// </param>
/// <param name="KeyColumns">
/// The business key, in order, feeding <see cref="OilXRowId"/>. Must exactly match the
/// columns marked <see cref="OilXColumn.IsKey"/> — <see cref="OilXDescriptors.Validate"/>
/// proves it.
/// </param>
public sealed record OilXFeedDescriptor(
    string FeedId,
    string DisplayName,
    string FileNamePrefix,
    string TvpType,
    string MergeProc,
    string TargetTable,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<OilXColumn> Columns)
{
    /// <summary>True when an S3 object name belongs to this feed.</summary>
    public bool Owns(string fileName) =>
        fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The feed registry — the single source of truth for every column mapping in this
/// loader.
///
/// <para>
/// Every header string here was copied from the live files of <b>2026-09-30</b>
/// (<c>docs/apis/OilX.md</c> §4). Every key was verified unique against those same
/// files; the counts are in <c>sql/OilX/001</c> next to each table.
/// </para>
/// <para>
/// Column ORDER in each list is the TVP contract and matches <c>sql/OilX/002</c>
/// exactly. The last three columns of every feed are always
/// <c>FileName</c>, <c>Checksum</c> preceded by the feed's payload, and the first two
/// are always <c>RunDate</c>, <c>RowId</c> — the primary key.
/// </para>
/// </summary>
public static class OilXDescriptors
{
    public const string CargoTracking = "CargoTracking";
    public const string FloatingStorageVessels = "FloatingStorageVessels";
    public const string Flows = "Flows";
    public const string GlobalBalance = "GlobalBalance";
    public const string OilFieldsProduction = "OilFieldsProduction";
    public const string RegionalBalance = "Regional_Balance";
    public const string SupplyDemand = "SupplyDemand";
    public const string Terminals = "Terminals";

    // ------------------------------------------------------------------ CargoTracking

    private static readonly OilXFeedDescriptor CargoTrackingFeed = new(
        FeedId: CargoTracking,
        DisplayName: "Cargo tracking — vessel voyages, load/discharge legs and grades",
        FileNamePrefix: "CargoTracking.",
        TvpType: "arm.CargoTrackingTvp",
        MergeProc: "arm.usp_BulkMergeCargoTracking",
        TargetTable: "arm.CargoTracking",
        // FlowID is the vendor's own sha256 row identity: UNIQUE across all 396,865
        // rows of the 2026-09-30T03-39 file. VoyageID is NOT an identity — those same
        // rows carry only 681 distinct values.
        KeyColumns: new[] { "FlowID" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.Str("IMO", 250),
            OilXColumn.Str("VesselName", 250),
            OilXColumn.Str("VesselClass", 250),
            OilXColumn.DtM("LoadDate", 7),
            OilXColumn.Str("LoadArea", 250),
            OilXColumn.Str("LoadCountry", 250),
            OilXColumn.Str("LoadSubcountryArea", 250),
            OilXColumn.Str("LoadPort", 250),
            // '0'/'1' and '-1'-when-absent, kept as text per the supplied DDL.
            OilXColumn.Str("LoadSTSIndicator", 250),
            OilXColumn.Str("LoadSTSIMO", 250),
            OilXColumn.Str("OriginCountry", 250),
            OilXColumn.Str("OriginCountryGroup", 250),
            OilXColumn.Str("GradeName", 250),
            OilXColumn.DtM("DischargeDate", 7),
            OilXColumn.Str("DischargeArea", 250),
            OilXColumn.Str("DischargeCountry", 250),
            OilXColumn.Str("DischargeSubcountryArea", 250),
            OilXColumn.Str("DischargePort", 250),
            OilXColumn.Str("DischargeSTSIndicator", 250),
            OilXColumn.Str("DischargeSTSIMO", 250),
            OilXColumn.Str("DestinationCountry", 250),
            OilXColumn.Str("DestinationCountryGroup", 250),
            // ⚠ The CSV publishes KT BEFORE KBBL and spells both with PARENTHESES.
            // The table lists KBBL first. Mapping is by header NAME, so the orders
            // may differ safely — but only because of these explicit header strings.
            OilXColumn.Flt("LoadQuantity_KBBL", "LoadQuantity(KBBL)"),
            OilXColumn.Flt("LoadQuantity_KT", "LoadQuantity(KT)"),
            // VARCHAR(MAX) per the supplied DDL even though live values look numeric.
            OilXColumn.StrMax("SulphurContent"),
            OilXColumn.Flt("APIGravity"),
            OilXColumn.StrMax("Charterer"),
            OilXColumn.StrMax("Supplier"),
            OilXColumn.StrMax("Buyer"),
            OilXColumn.DtM("LastUpdateDate", 7),
            OilXColumn.KeyStr("FlowID", 64),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // ---------------------------------------------------------------- FloatingStorage

    private static readonly OilXFeedDescriptor FloatingStorageFeed = new(
        FeedId: FloatingStorageVessels,
        DisplayName: "Floating storage — vessels holding crude at anchor",
        FileNamePrefix: "FloatingStorageVessels.",
        TvpType: "arm.FloatingStorageTvp",
        MergeProc: "arm.usp_BulkMergeFloatingStorage",
        TargetTable: "arm.FloatingStorage",
        // One row per vessel per reference date. IMO alone is NOT unique: 1,474
        // distinct vessels over 6,072 rows, one of them appearing 68 times.
        KeyColumns: new[] { "IMO", "ReferenceDate" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyStr("IMO", 50),
            OilXColumn.KeyDat("ReferenceDate"),
            OilXColumn.Str("VesselName", 256),
            OilXColumn.Str("VesselClass", 50),
            OilXColumn.DtM("StartDate", 7),
            OilXColumn.DtM("EndDate", 7),
            OilXColumn.Dec("QuantityKiloBarrels", 18),
            OilXColumn.Str("Area", 256),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // -------------------------------------------------------------------------- Flow

    private static readonly OilXFeedDescriptor FlowFeed = new(
        FeedId: Flows,
        DisplayName: "Flows — country-to-country crude movements by grade",
        FileNamePrefix: "Flows.",
        TvpType: "arm.FlowTvp",
        MergeProc: "arm.usp_BulkMergeFlow",
        TargetTable: "arm.Flow",
        // ⚠ THE TWO SUBCOUNTRY COLUMNS ARE LOAD-BEARING. Without them 28,887 of the
        // feed's 232,311 rows collapse onto shared keys — measured, not hypothetical.
        // They are usually EMPTY, which is what makes them look droppable.
        KeyColumns: new[]
        {
            "OriginCountryName", "DestinationCountryName", "GroupByDateIndicator",
            "OriginSubCountry", "DestinationSubCountry", "ReferenceDate", "GradeName"
        },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyDat("ReferenceDate"),
            OilXColumn.KeyStr("OriginCountryName", 50),
            OilXColumn.KeyStr("DestinationCountryName", 50),
            // ⚠ The CSV header has a LOWER-CASE b: 'GroupbyDateIndicator'.
            // 'Exports'/'Imports' — the same cargo appears under both.
            OilXColumn.KeyStr("GroupByDateIndicator", 50, "GroupbyDateIndicator"),
            OilXColumn.KeyStr("OriginSubCountry", 50),
            OilXColumn.KeyStr("DestinationSubCountry", 50),
            OilXColumn.KeyStr("GradeName", 50),
            OilXColumn.Str("GradeCategory", 50),
            OilXColumn.Dec("ApiGravity", 18),
            OilXColumn.Dec("SulphurContent", 18),
            OilXColumn.Dec("QuantityKBD", 18),
            OilXColumn.Dec("QuantityKBBL", 18),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // ----------------------------------------------------------------- GlobalBalance

    private static readonly OilXFeedDescriptor GlobalBalanceFeed = new(
        FeedId: GlobalBalance,
        DisplayName: "Global balance — world supply/demand aggregates",
        FileNamePrefix: "GlobalBalance.",
        TvpType: "arm.GlobalBalanceTvp",
        MergeProc: "arm.usp_BulkMergeGlobalBalance",
        TargetTable: "arm.GlobalBalance",
        KeyColumns: new[] { "GroupName", "ReferenceDate", "FlowBreakdown", "UnitMeasure" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyStr("GroupName", 50),
            OilXColumn.KeyDtM("ReferenceDate", 7),
            OilXColumn.KeyStr("FlowBreakdown", 50),
            OilXColumn.KeyStr("UnitMeasure", 50),
            OilXColumn.Dec("ObservedValue", 28),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // ------------------------------------------------------------ OilFieldProduction

    private static readonly OilXFeedDescriptor OilFieldProductionFeed = new(
        FeedId: OilFieldsProduction,
        DisplayName: "Oil field production — per-field output by port",
        FileNamePrefix: "OilFieldsProduction.",
        TvpType: "arm.OilFieldProductionTvp",
        MergeProc: "arm.usp_BulkMergeOilFieldProduction",
        TargetTable: "arm.OilFieldProduction",
        // The minimal (OilFieldName, ReferenceDate, UnitMeasure) is ALSO unique live.
        // The fuller grain is deliberate: a field served by a second port becomes a
        // SECOND ROW rather than a value silently dropped by the merge's de-dup guard.
        // usp_ValidateLoad's OrphanPort check reports the cost (sql/OilX/001).
        KeyColumns: new[] { "OilFieldName", "PortName", "CountryName", "ReferenceDate", "UnitMeasure" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyDat("ReferenceDate"),
            OilXColumn.KeyStr("OilFieldName", 50),
            OilXColumn.KeyStr("UnitMeasure", 50),
            OilXColumn.KeyStr("PortName", 256),
            OilXColumn.KeyStr("CountryName", 50),
            OilXColumn.Dec("ObservedValue", 18),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // --------------------------------------------------------------- RegionalBalance

    private static readonly OilXFeedDescriptor RegionalBalanceFeed = new(
        FeedId: RegionalBalance,
        DisplayName: "Regional balance — per-region supply/demand aggregates",
        FileNamePrefix: "Regional_Balance.",
        TvpType: "arm.RegionalBalanceTvp",
        MergeProc: "arm.usp_BulkMergeRegionalBalance",
        TargetTable: "arm.RegionalBalance",
        // GeneralizedSource ('OilX') is a VALUE column, not a key: the key below is
        // already unique, and keying on a near-constant provenance string would make
        // every row's identity hostage to the vendor relabelling its own source.
        KeyColumns: new[] { "GroupName", "ReferenceDate", "FlowBreakdown", "UnitMeasure" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyStr("GroupName", 50),
            OilXColumn.KeyDtM("ReferenceDate", 7),
            OilXColumn.KeyStr("FlowBreakdown", 50),
            OilXColumn.KeyStr("UnitMeasure", 50),
            OilXColumn.Dec("ObservedValue", 28),
            OilXColumn.Str("GeneralizedSource", 50),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // ------------------------------------------------------------------ SupplyDemand

    private static readonly OilXFeedDescriptor SupplyDemandFeed = new(
        FeedId: SupplyDemand,
        DisplayName: "Supply and demand — per-country balances",
        FileNamePrefix: "SupplyDemand.",
        TvpType: "arm.SupplyDemandTvp",
        MergeProc: "arm.usp_BulkMergeSupplyDemand",
        TargetTable: "arm.SupplyDemand",
        // CountryName is functionally dependent on CountryISOCode, so keying on it too
        // would buy nothing and would fork a country's history the day it is renamed.
        KeyColumns: new[] { "CountryISOCode", "ReferenceDate", "FlowBreakdown", "UnitMeasure" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyStr("CountryISOCode", 50),
            OilXColumn.KeyDtM("ReferenceDate", 7),
            OilXColumn.KeyStr("FlowBreakdown", 50),
            OilXColumn.Str("CountryName", 250),
            OilXColumn.KeyStr("UnitMeasure", 50),
            OilXColumn.Dec("ObservedValue", 28),
            OilXColumn.Str("GeneralizedSource", 50),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    // ---------------------------------------------------------------------- Terminal

    private static readonly OilXFeedDescriptor TerminalFeed = new(
        FeedId: Terminals,
        DisplayName: "Terminals — per-terminal stocks and capacity",
        FileNamePrefix: "Terminals.",
        TvpType: "arm.TerminalTvp",
        MergeProc: "arm.usp_BulkMergeTerminal",
        TargetTable: "arm.Terminal",
        KeyColumns: new[] { "TerminalName", "ReferenceDate", "FlowBreakdown", "UnitMeasure" },
        Columns: new[]
        {
            OilXColumn.RunDate(),
            OilXColumn.RowId(),
            OilXColumn.KeyStr("TerminalName", 50),
            // DATETIME2(2) here, not (7) as on the other balance feeds. As supplied.
            OilXColumn.KeyDtM("ReferenceDate", 2),
            OilXColumn.KeyStr("FlowBreakdown", 50),
            OilXColumn.KeyStr("UnitMeasure", 50),
            OilXColumn.Dec("ObservedValue", 18),
            OilXColumn.Str("CountryName", 50),
            OilXColumn.FileName(),
            OilXColumn.Checksum()
        });

    /// <summary>All eight feeds, in a stable order.</summary>
    public static readonly IReadOnlyList<OilXFeedDescriptor> All = new[]
    {
        CargoTrackingFeed,
        FloatingStorageFeed,
        FlowFeed,
        GlobalBalanceFeed,
        OilFieldProductionFeed,
        RegionalBalanceFeed,
        SupplyDemandFeed,
        TerminalFeed
    };

    /// <summary>Find a feed by id, case-insensitively. <c>null</c> when unknown.</summary>
    public static OilXFeedDescriptor? Find(string feedId) =>
        All.FirstOrDefault(f => f.FeedId.Equals(feedId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Self-check over the registry, run once at startup by
    /// <see cref="OilXModule.RegisterServices"/> and asserted by the tests.
    ///
    /// <para>
    /// These are the invariants the rest of the loader assumes without re-checking —
    /// each would otherwise fail far from its cause, as a shifted TVP column or a
    /// silently forked key.
    /// </para>
    /// </summary>
    public static void Validate()
    {
        foreach (var feed in All)
        {
            var names = feed.Columns.Select(c => c.Name).ToList();

            var duplicates = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                                  .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                throw new InvalidOperationException(
                    $"OilX {feed.FeedId}: duplicate column name(s) {string.Join(", ", duplicates)}.");

            // The first two columns are the primary key, in order, on every feed.
            if (names.Count < 4 || names[0] != "RunDate" || names[1] != "RowId")
                throw new InvalidOperationException(
                    $"OilX {feed.FeedId}: columns must start with RunDate, RowId.");

            // The last two are provenance and the change guard, in order, on every feed.
            if (names[^2] != "FileName" || names[^1] != "Checksum")
                throw new InvalidOperationException(
                    $"OilX {feed.FeedId}: columns must end with FileName, Checksum.");

            // KeyColumns and the IsKey flags must agree, or RowId would be computed
            // over a different set than the one documented in the SQL.
            var flagged = feed.Columns.Where(c => c.IsKey).Select(c => c.Name)
                              .OrderBy(n => n, StringComparer.Ordinal).ToList();
            var declared = feed.KeyColumns.OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (!flagged.SequenceEqual(declared, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"OilX {feed.FeedId}: KeyColumns [{string.Join(", ", declared)}] does not match " +
                    $"the IsKey-flagged columns [{string.Join(", ", flagged)}].");

            if (feed.KeyColumns.Count == 0)
                throw new InvalidOperationException($"OilX {feed.FeedId}: no business key declared.");

            foreach (var key in feed.KeyColumns)
                if (!names.Contains(key, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"OilX {feed.FeedId}: key column '{key}' is not in the column list.");

            // A derived column must not also claim a CSV header, and vice versa.
            foreach (var column in feed.Columns)
            {
                if (column.FromCsv && column.SourceHeaders.Count == 0)
                    throw new InvalidOperationException(
                        $"OilX {feed.FeedId}.{column.Name}: CSV-sourced column has no source header.");

                if (!column.FromCsv && column.SourceHeaders.Count > 0)
                    throw new InvalidOperationException(
                        $"OilX {feed.FeedId}.{column.Name}: derived column must not declare a source header.");

                // A key column is allowed to be BLANK (Flow's subcountries usually are),
                // so it must never be Required — that would drop those rows. A blank cell
                // converts to NULL and hashes as the null token, a stable identity.
                if (column.IsKey && column.Required)
                    throw new InvalidOperationException(
                        $"OilX {feed.FeedId}.{column.Name}: a key column must not be Required — " +
                        "a blank key component is a legitimate, stable identity here.");
            }

            // Exactly one RowId, one FileName and one Checksum per feed.
            foreach (var derived in new[] { OilXDerived.RowId, OilXDerived.FileName, OilXDerived.Checksum })
                if (feed.Columns.Count(c => c.Derived == derived) != 1)
                    throw new InvalidOperationException(
                        $"OilX {feed.FeedId}: expected exactly one {derived} column.");
        }

        var ids = All.Select(f => f.FeedId).ToList();
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
            throw new InvalidOperationException("OilX: duplicate FeedId in the registry.");

        // Feed attribution is by file-name prefix, so one prefix must never be a
        // prefix of another — 'Terminals.' vs 'TerminalsWeekly.' is the real case the
        // trailing dot guards against.
        foreach (var a in All)
            foreach (var b in All)
                if (!ReferenceEquals(a, b) &&
                    a.FileNamePrefix.StartsWith(b.FileNamePrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"OilX: file-name prefix '{a.FileNamePrefix}' collides with '{b.FileNamePrefix}'.");
    }
}
