namespace DataLoader.Criterion;

/// <summary>
/// The scalar shapes the Criterion feeds use. Drives BOTH the PostgreSQL-to-CLR
/// conversion in <see cref="CriterionConvert"/> and the DataTable column type in
/// <see cref="CriterionTableSink"/>.
///
/// <para>
/// Unlike the file-based loaders in this repo, the source here is a RELATIONAL
/// DATABASE, so values arrive already typed. These cases therefore describe the
/// TARGET type; <see cref="CriterionConvert"/>'s job is the narrowing (PostgreSQL
/// <c>smallint</c> to <see cref="short"/>, <c>numeric</c> to <see cref="decimal"/>,
/// <c>boolean</c> to a SQL Server <c>BIT</c>) rather than text parsing.
/// </para>
/// </summary>
public enum CriterionColumnType
{
    /// <summary><c>VARCHAR(n)</c> / <c>VARCHAR(MAX)</c> -> <see cref="string"/>.</summary>
    String,

    /// <summary><c>CHAR(n)</c> -> <see cref="string"/> (SqlClient binds CHAR from string).</summary>
    Char,

    /// <summary><c>UNIQUEIDENTIFIER</c> -> <see cref="System.Guid"/>.</summary>
    Guid,

    /// <summary><c>BIT</c> -> <see cref="bool"/>.</summary>
    Boolean,

    /// <summary><c>SMALLINT</c> -> <see cref="short"/>. PostgreSQL <c>int2</c>.</summary>
    Int16,

    /// <summary><c>INT</c> -> <see cref="int"/>.</summary>
    Int32,

    /// <summary><c>FLOAT</c> -> <see cref="double"/>. PostgreSQL <c>double precision</c>.</summary>
    Double,

    /// <summary><c>DECIMAL(13,10)</c> -> <see cref="decimal"/>. Latitude/Longitude only.</summary>
    Decimal,

    /// <summary><c>DATE</c> -> <see cref="System.DateTime"/> at midnight.</summary>
    Date,

    /// <summary><c>DATETIME2</c> -> <see cref="System.DateTime"/>.</summary>
    DateTime
}

/// <summary>
/// One column of one target table. This single record drives four things that
/// would otherwise have to be kept in sync by hand:
///
/// <list type="number">
///   <item>the expression the reader selects from PostgreSQL (<see cref="SourceExpression"/>),</item>
///   <item>how the value is converted (<see cref="Type"/>),</item>
///   <item>the DataTable column name, order and CLR type in the sink,</item>
///   <item>the expected TVP column in <c>sql/Criterion/002</c>, asserted by
///         <c>CriterionTvpContractTests</c> against <see cref="SqlType"/>.</item>
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
/// <param name="SourceExpression">
/// The expression selected from PostgreSQL, aliased to <see cref="Name"/>. For most
/// columns this is the bare source column; where the source type is
/// <c>character(n)</c> it is wrapped, because <c>bpchar</c> is BLANK-PADDED and an
/// untrimmed <c>'IA   '</c> would not compare equal to <c>'IA'</c> anywhere
/// downstream. Empty for a column the reader derives itself (the JSON unpivot).
/// </param>
/// <param name="Required">
/// <c>true</c> =&gt; <c>NOT NULL</c> in the TVP, and a source row whose value is
/// missing is DROPPED rather than merged under a blank key. Every primary-key
/// component is required.
/// </param>
public sealed record CriterionColumn(
    string Name,
    string SqlType,
    CriterionColumnType Type,
    string SourceExpression,
    bool Required)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        CriterionColumnType.String => typeof(string),
        CriterionColumnType.Char => typeof(string),
        CriterionColumnType.Guid => typeof(Guid),
        CriterionColumnType.Boolean => typeof(bool),
        CriterionColumnType.Int16 => typeof(short),
        CriterionColumnType.Int32 => typeof(int),
        CriterionColumnType.Double => typeof(double),
        CriterionColumnType.Decimal => typeof(decimal),
        CriterionColumnType.Date => typeof(DateTime),
        CriterionColumnType.DateTime => typeof(DateTime),
        _ => throw new NotSupportedException($"Unmapped CriterionColumnType '{Type}'.")
    };

    /// <summary>
    /// The <c>expr AS "Name"</c> fragment for the generated SELECT. The alias is
    /// double-quoted so PostgreSQL preserves the target's PascalCase — unquoted
    /// identifiers fold to lower case, and the reader looks columns up by the
    /// descriptor name.
    /// </summary>
    public string SelectFragment => $"{SourceExpression} AS \"{Name}\"";

    // ---- terse builders, so the registry below reads like the SQL ----

    public static CriterionColumn Str(string name, int size, string source, bool required = false) =>
        new(name, $"VARCHAR({size})", CriterionColumnType.String, source, required);

    public static CriterionColumn StrMax(string name, string source, bool required = false) =>
        new(name, "VARCHAR(MAX)", CriterionColumnType.String, source, required);

    /// <summary>
    /// A <c>CHAR(n)</c> target. The source column is <c>character(n)</c> in every
    /// case, so the expression is wrapped in <see cref="Trimmed"/>.
    /// </summary>
    public static CriterionColumn Chr(string name, int size, string source, bool required = false) =>
        new(name, $"CHAR({size})", CriterionColumnType.Char, source, required);

    public static CriterionColumn Uid(string name, string source, bool required = false) =>
        new(name, "UNIQUEIDENTIFIER", CriterionColumnType.Guid, source, required);

    public static CriterionColumn Bit(string name, string source, bool required = false) =>
        new(name, "BIT", CriterionColumnType.Boolean, source, required);

    public static CriterionColumn I16(string name, string source, bool required = false) =>
        new(name, "SMALLINT", CriterionColumnType.Int16, source, required);

    public static CriterionColumn I32(string name, string source, bool required = false) =>
        new(name, "INT", CriterionColumnType.Int32, source, required);

    public static CriterionColumn Flt(string name, string source, bool required = false) =>
        new(name, "FLOAT", CriterionColumnType.Double, source, required);

    public static CriterionColumn Dec13(string name, string source, bool required = false) =>
        new(name, "DECIMAL(13,10)", CriterionColumnType.Decimal, source, required);

    public static CriterionColumn Dat(string name, string source, bool required = false) =>
        new(name, "DATE", CriterionColumnType.Date, source, required);

    public static CriterionColumn Dtm7(string name, string source, bool required = false) =>
        new(name, "DATETIME2(7)", CriterionColumnType.DateTime, source, required);

    /// <summary>
    /// Wraps a <c>character(n)</c> source column so it arrives trimmed, with an
    /// all-blank value collapsed to NULL.
    ///
    /// <para>
    /// Both halves matter. PostgreSQL <c>bpchar</c> pads to the declared width, so
    /// <c>state_abb</c> comes back as <c>'IA   '</c> and would be stored — and
    /// compared — with the padding. And <c>financial_json_latest.unit_id</c> holds
    /// 24 spaces rather than NULL for 60 rows in a 30-day window; without the
    /// <c>NULLIF</c> those become an empty-string UnitId that matches no dimension
    /// row and reads as "a unit id we could not resolve" instead of "no unit".
    /// </para>
    /// </summary>
    public static string Trimmed(string column) => $"NULLIF(TRIM({column}), '')";
}

/// <summary>
/// One TARGET TABLE: its TVP, its merge proc, the ordered column list that IS the
/// TVP contract, and the PostgreSQL relation the rows come from.
/// </summary>
/// <param name="TableName">Fully qualified SQL Server table, e.g. <c>arm.Misc_Unit</c>.</param>
/// <param name="TvpType">The table type in <c>sql/Criterion/002</c>.</param>
/// <param name="MergeProc">The merge proc in <c>sql/Criterion/003</c>.</param>
/// <param name="Columns">Ordered TVP columns. Order is the contract.</param>
public sealed record CriterionTableDescriptor(
    string TableName,
    string TvpType,
    string MergeProc,
    IReadOnlyList<CriterionColumn> Columns);

/// <summary>How a feed decides which source rows a run should read.</summary>
public enum CriterionWindowMode
{
    /// <summary>
    /// Read the whole relation every run. Used for the five dimension feeds, whose
    /// sources are between 12 and 42,446 rows and carry no usable change column.
    /// One work unit per run.
    /// </summary>
    Snapshot,

    /// <summary>
    /// One work unit per day in <c>[today - DaysBack, today]</c>, filtered on the
    /// feed's date column. Used for the two pipeline-flow feeds
    /// (<c>eff_gas_day</c>, indexed at the source) and for
    /// <see cref="CriterionFeed.FinancialSeries"/> (<c>post_date</c>, the source's
    /// RANGE partition key, so the filter prunes partitions rather than scanning
    /// 52 million rows).
    /// </summary>
    DayWindow,

    /// <summary>
    /// Like <see cref="DayWindow"/>, but each day is further split into PAGES of
    /// source rows. Only <see cref="CriterionFeed.FinancialSeriesData"/> uses this:
    /// its rows carry a JSON array averaging 165 KB (max 1.7 MB) that unpivots into
    /// thousands of target rows, so a whole day at once would buffer gigabytes.
    /// See <see cref="CriterionSettings.SeriesPageSize"/>.
    /// </summary>
    PagedDayWindow
}

/// <summary>Stable feed ids — the <c>EnabledFeeds</c> values and the pipeline ids.</summary>
public static class CriterionFeed
{
    public const string FinancialMetadata = "FinancialMetadata";
    public const string FinancialSeries = "FinancialSeries";
    public const string FinancialSeriesData = "FinancialSeriesData";
    public const string MiscPeriod = "MiscPeriod";
    public const string MiscUnit = "MiscUnit";
    public const string PipelinesMetadata = "PipelinesMetadata";
    public const string PipelinesNominationPoint = "PipelinesNominationPoint";
    public const string PipelinesPointflows = "PipelinesPointflows";
    public const string PipelinesRegion = "PipelinesRegion";
}

/// <summary>
/// One FEED: the PostgreSQL relation to read, how the run is windowed, and the
/// table the rows land in.
/// </summary>
/// <param name="FeedId">Stable id — matched case-insensitively against <c>EnabledFeeds</c>.</param>
/// <param name="FromClause">
/// The <c>FROM</c> body, without the keyword. Usually a single relation; for
/// <see cref="CriterionFeed.PipelinesPointflows"/> it is the two-table LEFT JOIN
/// that derives the table (see <see cref="CriterionDescriptors"/>).
/// </param>
/// <param name="WindowColumn">
/// The date column the window filters on, qualified where the FROM clause has more
/// than one relation. Empty for <see cref="CriterionWindowMode.Snapshot"/>.
/// </param>
/// <param name="KeyColumns">
/// For <see cref="CriterionWindowMode.PagedDayWindow"/>, the columns the work-unit
/// provider selects to enumerate and page the day's rows. Empty otherwise.
/// </param>
public sealed record CriterionFeedDescriptor(
    string FeedId,
    string DisplayName,
    string FromClause,
    CriterionWindowMode WindowMode,
    string WindowColumn,
    CriterionTableDescriptor Table,
    string KeyColumns = "")
{
    /// <summary>The ordered <c>SELECT</c> list, built from the table's columns.</summary>
    public string SelectList => string.Join(",\n       ", Table.Columns.Select(c => c.SelectFragment));
}

/// <summary>
/// The registry — the single source of truth for every column mapping in this
/// loader.
///
/// <para>
/// Every <c>SourceExpression</c> below was read from the live database on
/// 2026-09-03 (<c>pg_attribute</c> for names and types, then sampled for content);
/// none is reconstructed or guessed. <c>docs/apis/Criterion.md</c> records the
/// verification and every place the source disagrees with the requested DDL.
/// </para>
/// </summary>
public static class CriterionDescriptors
{
    // =========================================================================
    // data_series.financial_metadata -> arm.Financial_Metadata
    //
    // Enabled is ABSENT on purpose: it is an ARM-local flag with no source column,
    // so it must never reach the TVP or the merge would clear it. Same reasoning
    // as IsLatest on the nomination table and MappingId on the pipeline catalog.
    // =========================================================================
    public static readonly CriterionTableDescriptor FinancialMetadataTable = new(
        "arm.Financial_Metadata",
        "arm.FinancialMetadataTvp",
        "arm.usp_BulkMergeFinancialMetadata",
        new[]
        {
            CriterionColumn.Uid("MetadataId", "metadata_uuid", required: true),
            CriterionColumn.Str("EntityName", 100, "entity_name"),
            CriterionColumn.Str("MetadataDesc", 100, "metadata_desc"),
            CriterionColumn.Str("CmdtyClass", 50, "cmdty_class"),
            CriterionColumn.Str("SubCmdtyDesc", 150, "sub_cmdty_desc"),
            CriterionColumn.Str("AssetName", 150, "asset_name"),
            CriterionColumn.Str("RegionName", 200, "region_name"),
            CriterionColumn.Str("CountryName", 50, "country_name"),
            CriterionColumn.Str("StateName", 50, "state_name"),
            CriterionColumn.Str("ProvinceName", 100, "province_name"),
            CriterionColumn.Str("MongoId", 24, CriterionColumn.Trimmed("mongo_id")),
            CriterionColumn.Bit("Status", "status"),
            CriterionColumn.Str("SeriesDesc", 150, "series_desc"),
            CriterionColumn.Str("SeriesId", 50, "series_id"),
            // NULL for every source row today; kept because the DDL asks for it.
            CriterionColumn.Str("TableName", 100, "table_name"),
            CriterionColumn.Str("EntityId", 24, CriterionColumn.Trimmed("entity_id")),
            CriterionColumn.Str("SeriesType", 50, "series_type"),
            CriterionColumn.Str("SubRegion", 100, "sub_region"),
            CriterionColumn.Str("Ticker", 100, "ticker")
        });

    // =========================================================================
    // data_series.financial_json_latest -> arm.Financial_Series
    //
    // NOT data_series.financial_json (which the request named): _latest holds one
    // row per (series, post_date) — 46,032 rows over 30 days against 165,859,
    // where the extra rows are superseded republications. See docs/apis 5.1.
    //
    // The `data` column is deliberately NOT selected here: it averages 165 KB and
    // this feed does not need it. Only FinancialSeriesData pays that cost.
    // =========================================================================
    public static readonly CriterionTableDescriptor FinancialSeriesTable = new(
        "arm.Financial_Series",
        "arm.FinancialSeriesTvp",
        "arm.usp_BulkMergeFinancialSeries",
        new[]
        {
            CriterionColumn.Uid("FinancialJsonId", "financial_json_uuid", required: true),
            CriterionColumn.Uid("MetadataId", "metadata_uuid"),
            CriterionColumn.Dat("PostDate", "post_date"),
            CriterionColumn.Dat("ForecastDate", "forecast_date"),
            CriterionColumn.Dtm7("LoadDate", "load_date"),
            CriterionColumn.Str("Filename", 255, "filename"),
            CriterionColumn.I32("Version", "version"),
            // Mongo ids, not UUIDs — they join arm.Misc_Period/Unit on MongoId.
            CriterionColumn.Str("PeriodId", 24, CriterionColumn.Trimmed("period_id")),
            CriterionColumn.Str("UnitId", 24, CriterionColumn.Trimmed("unit_id")),
            CriterionColumn.Bit("Active", "active"),
            CriterionColumn.Dtm7("ForecastDateTime", "forecast_date_time")
        });

    // =========================================================================
    // data_series.financial_json_latest.data -> arm.Financial_SeriesData
    //
    // The two value columns come from the JSON array, not from a source column, so
    // their SourceExpression is empty and CriterionSeriesData does the extraction.
    // FinancialJsonId is the one real column, and it is what ties an observation
    // back to its arm.Financial_Series row.
    // =========================================================================
    public static readonly CriterionTableDescriptor FinancialSeriesDataTable = new(
        "arm.Financial_SeriesData",
        "arm.FinancialSeriesDataTvp",
        "arm.usp_BulkMergeFinancialSeriesData",
        new[]
        {
            CriterionColumn.Uid("FinancialJsonId", "financial_json_uuid", required: true),
            new CriterionColumn("Date", "DATE", CriterionColumnType.Date, string.Empty, Required: true),
            new CriterionColumn("Value", "FLOAT", CriterionColumnType.Double, string.Empty, Required: false)
        });

    // =========================================================================
    // misc.periods -> arm.Misc_Period      (request said "misc.period")
    // misc.units   -> arm.Misc_Unit        (request said "misc.unit")
    // Both names and both key columns corrected against the live catalog.
    // =========================================================================
    public static readonly CriterionTableDescriptor MiscPeriodTable = new(
        "arm.Misc_Period",
        "arm.MiscPeriodTvp",
        "arm.usp_BulkMergeMiscPeriod",
        new[]
        {
            CriterionColumn.Uid("PeriodId", "period_uuid", required: true),
            CriterionColumn.Str("MongoId", 24, CriterionColumn.Trimmed("mongo_id")),
            CriterionColumn.Str("PeriodDesc", 75, "period_desc"),
            CriterionColumn.Str("PeriodShort", 20, "period_short")
        });

    public static readonly CriterionTableDescriptor MiscUnitTable = new(
        "arm.Misc_Unit",
        "arm.MiscUnitTvp",
        "arm.usp_BulkMergeMiscUnit",
        new[]
        {
            CriterionColumn.Uid("UnitId", "unit_uuid", required: true),
            CriterionColumn.Str("MongoId", 24, CriterionColumn.Trimmed("mongo_id")),
            CriterionColumn.Str("UnitDesc", 50, "unit_desc")
        });

    // =========================================================================
    // pipelines.metadata -> arm.Pipelines_Metadata
    //
    // Point and MappingId are ABSENT from the TVP: the first is built in the merge
    // proc from Latitude/Longitude, the second is a server-side IDENTITY.
    // =========================================================================
    public static readonly CriterionTableDescriptor PipelinesMetadataTable = new(
        "arm.Pipelines_Metadata",
        "arm.PipelinesMetadataTvp",
        "arm.usp_BulkMergePipelinesMetadata",
        new[]
        {
            CriterionColumn.Str("MetadataId", 30, "metadata_id", required: true),
            CriterionColumn.Str("AssetId", 24, CriterionColumn.Trimmed("asset_id")),
            CriterionColumn.Str("Tsp", 15, "tsp"),
            CriterionColumn.Str("PipelineName", 50, "pipeline_name"),
            CriterionColumn.Str("LocName", 100, "loc_name"),
            CriterionColumn.Str("Loc", 15, "loc"),
            CriterionColumn.I16("RecDelSign", "rec_del_sign"),
            CriterionColumn.I16("LocQtiId", "loc_qti_id"),
            CriterionColumn.Chr("LocQtiShort", 3, CriterionColumn.Trimmed("loc_qti_short")),
            CriterionColumn.Str("CategoryShort", 25, "category_short"),
            CriterionColumn.Str("SubCategoryDesc", 50, "sub_category_desc"),
            CriterionColumn.Str("SubCategory2Desc", 100, "sub_category_2_desc"),
            CriterionColumn.Str("CountryName", 50, "country_name"),
            CriterionColumn.Str("StateName", 50, "state_name"),
            CriterionColumn.Str("CountyName", 100, "county_name"),
            CriterionColumn.Str("OffshoreBlockName", 50, "offshore_block_name"),
            CriterionColumn.Str("ConnectingPipeline", 50, "connecting_pipeline"),
            CriterionColumn.Str("ConnectingEntity", 100, "connecting_entity"),
            CriterionColumn.Str("StorageName", 100, "storage_name"),
            CriterionColumn.Chr("StorageCalcFlag", 1, CriterionColumn.Trimmed("storage_calc_flag")),
            // NULL for all 42,446 source rows as of 2026-09-03, so Point lands NULL.
            CriterionColumn.Dec13("Latitude", "latitude"),
            CriterionColumn.Dec13("Longitude", "longitude"),
            CriterionColumn.Dtm7("UpdateDate", "update_date"),
            CriterionColumn.Chr("TspShort", 3, CriterionColumn.Trimmed("tsp_short")),
            CriterionColumn.Str("LocPurpDesc", 50, "loc_purp_desc"),
            CriterionColumn.Str("StateAbb", 7, "state_abb"),
            CriterionColumn.Str("FercPipelineId", 7, "ferc_pipeline_id"),
            CriterionColumn.Str("FercPointId", 17, "ferc_point_id"),
            CriterionColumn.I32("TransportationMaxDailyQuantity", "transportation_max_daily_quantity"),
            CriterionColumn.I32("StorageMaxDailyQuantity", "storage_max_daily_quantity"),
            CriterionColumn.Str("LocZone", 25, "loc_zone"),
            CriterionColumn.StrMax("UpdnLoc", "updn_loc"),
            CriterionColumn.Str("BasinName", 50, "basin_name"),
            CriterionColumn.Str("Units", 30, "units"),
            CriterionColumn.Str("PointType", 75, "point_type"),
            CriterionColumn.Str("ProvinceName", 100, "province_name"),
            CriterionColumn.Str("Ticker", 100, "ticker")
        });

    // =========================================================================
    // pipelines.nomination_points -> arm.Pipelines_NominationPoint
    //
    // IsLatest is ABSENT from the TVP — ARM-local flag, no source column.
    // =========================================================================
    public static readonly CriterionTableDescriptor PipelinesNominationPointTable = new(
        "arm.Pipelines_NominationPoint",
        "arm.PipelinesNominationPointTvp",
        "arm.usp_BulkMergePipelinesNominationPoint",
        new[]
        {
            CriterionColumn.Str("MetadataId", 30, "metadata_id", required: true),
            CriterionColumn.Dat("EffGasDay", "eff_gas_day", required: true),
            CriterionColumn.I16("CycleId", "cycle_id", required: true),
            CriterionColumn.I32("HourlyCycleId", "hourly_cycle_id", required: true),
            CriterionColumn.Dat("EndEffGasDay", "end_eff_gas_day"),
            CriterionColumn.Chr("TspShort", 3, CriterionColumn.Trimmed("tsp_short")),
            CriterionColumn.Str("CycleDesc", 30, "cycle_desc"),
            CriterionColumn.Flt("DesignCapacity", "design_capacity"),
            CriterionColumn.Flt("OperatingCapacity", "operating_capacity"),
            CriterionColumn.Flt("ScheduledQuantity", "scheduled_quantity"),
            CriterionColumn.Flt("OperationallyAvailable", "operationally_available"),
            CriterionColumn.Str("Tbl", 10, "tbl"),
            CriterionColumn.Str("Ticker", 100, "ticker")
        });

    // =========================================================================
    // pipelines.nomination_points LEFT JOIN pipelines.metadata
    //                                    -> arm.Pipelines_Pointflows
    //
    // Derived: no pointflows-shaped relation exists in the source. Columns are
    // qualified because the FROM clause has two relations, and both carry
    // metadata_id / state_name. Id is ABSENT — it is a server-side IDENTITY.
    // =========================================================================
    public static readonly CriterionTableDescriptor PipelinesPointflowsTable = new(
        "arm.Pipelines_Pointflows",
        "arm.PipelinesPointflowsTvp",
        "arm.usp_BulkMergePipelinesPointflows",
        new[]
        {
            CriterionColumn.Str("MetadataId", 30, "n.metadata_id", required: true),
            CriterionColumn.Dat("EffGasDay", "n.eff_gas_day", required: true),
            CriterionColumn.I16("CycleId", "n.cycle_id", required: true),
            CriterionColumn.Str("CycleDesc", 30, "n.cycle_desc"),
            CriterionColumn.Str("PipelineName", 50, "m.pipeline_name"),
            CriterionColumn.Str("LocName", 100, "m.loc_name"),
            CriterionColumn.Str("CategoryShort", 25, "m.category_short"),
            CriterionColumn.Str("LocZone", 25, "m.loc_zone"),
            CriterionColumn.Str("LocPurpDesc", 50, "m.loc_purp_desc"),
            CriterionColumn.Str("StateName", 50, "m.state_name"),
            CriterionColumn.Str("StateAbb", 7, "m.state_abb"),
            CriterionColumn.Str("CountryName", 50, "m.country_name"),
            CriterionColumn.Flt("ScheduledQuantity", "n.scheduled_quantity")
        });

    // =========================================================================
    // pipelines.regions -> arm.Pipelines_Region
    //
    // region_id, state_id and state_abb are all character(n) and blank-padded.
    // StateId is CHAR(24) NOT NULL in the target, so it is trimmed and required —
    // an untrimmed value would still fit but would make the PK compare oddly
    // against anything joined on a VARCHAR.
    // =========================================================================
    public static readonly CriterionTableDescriptor PipelinesRegionTable = new(
        "arm.Pipelines_Region",
        "arm.PipelinesRegionTvp",
        "arm.usp_BulkMergePipelinesRegion",
        new[]
        {
            CriterionColumn.Str("RegionId", 24, CriterionColumn.Trimmed("region_id"), required: true),
            CriterionColumn.Chr("StateId", 24, CriterionColumn.Trimmed("state_id"), required: true),
            CriterionColumn.Str("RegionName", 30, "region_name"),
            CriterionColumn.Str("StateAbb", 5, CriterionColumn.Trimmed("state_abb")),
            CriterionColumn.Str("StateName", 50, "state_name"),
            CriterionColumn.Str("EIA_NG_Regions", 30, "eia_ng_regions"),
            CriterionColumn.Str("EIA_PADD_Regions", 10, "eia_padd_regions")
        });

    /// <summary>Every target table, in deployment order. One per feed here — no sharing.</summary>
    public static readonly IReadOnlyList<CriterionTableDescriptor> AllTables = new[]
    {
        FinancialMetadataTable,
        FinancialSeriesTable,
        FinancialSeriesDataTable,
        MiscPeriodTable,
        MiscUnitTable,
        PipelinesMetadataTable,
        PipelinesNominationPointTable,
        PipelinesPointflowsTable,
        PipelinesRegionTable
    };

    /// <summary>
    /// The nine feeds.
    ///
    /// <para>
    /// Ordered dimensions-first. Nothing enforces it — the pipelines are
    /// independent and carry no foreign keys — but a run that loads the catalogs
    /// before the facts leaves the database consistent at more points in time if
    /// it is interrupted.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<CriterionFeedDescriptor> All = new[]
    {
        new CriterionFeedDescriptor(
            CriterionFeed.MiscPeriod,
            "Period dimension",
            "misc.periods",
            CriterionWindowMode.Snapshot,
            string.Empty,
            MiscPeriodTable),

        new CriterionFeedDescriptor(
            CriterionFeed.MiscUnit,
            "Unit dimension",
            "misc.units",
            CriterionWindowMode.Snapshot,
            string.Empty,
            MiscUnitTable),

        new CriterionFeedDescriptor(
            CriterionFeed.PipelinesRegion,
            "Region / state cross-reference",
            "pipelines.regions",
            CriterionWindowMode.Snapshot,
            string.Empty,
            PipelinesRegionTable),

        new CriterionFeedDescriptor(
            CriterionFeed.FinancialMetadata,
            "Financial series catalog",
            "data_series.financial_metadata",
            CriterionWindowMode.Snapshot,
            string.Empty,
            FinancialMetadataTable),

        new CriterionFeedDescriptor(
            CriterionFeed.PipelinesMetadata,
            "Pipeline point catalog",
            "pipelines.metadata",
            CriterionWindowMode.Snapshot,
            string.Empty,
            PipelinesMetadataTable),

        new CriterionFeedDescriptor(
            CriterionFeed.FinancialSeries,
            "Financial series publications",
            "data_series.financial_json_latest",
            CriterionWindowMode.DayWindow,
            // post_date is the source's RANGE partition key: filtering on it prunes
            // partitions instead of scanning the whole 52-million-row parent.
            "post_date",
            FinancialSeriesTable),

        new CriterionFeedDescriptor(
            CriterionFeed.FinancialSeriesData,
            "Financial series observations (JSON unpivot)",
            "data_series.financial_json_latest",
            CriterionWindowMode.PagedDayWindow,
            "post_date",
            FinancialSeriesDataTable,
            KeyColumns: "financial_json_uuid"),

        new CriterionFeedDescriptor(
            CriterionFeed.PipelinesNominationPoint,
            "Pipeline nomination points",
            "pipelines.nomination_points",
            CriterionWindowMode.DayWindow,
            "eff_gas_day",
            PipelinesNominationPointTable),

        new CriterionFeedDescriptor(
            CriterionFeed.PipelinesPointflows,
            "Pipeline point flows (derived join)",
            // LEFT, not INNER: 25 of 19,431 nomination rows on 2026-09-02 name a
            // metadata_id with no catalog row, and an INNER JOIN would drop those
            // flows silently. See docs/apis/Criterion.md 5.3.
            "pipelines.nomination_points AS n\n  LEFT JOIN pipelines.metadata AS m ON m.metadata_id = n.metadata_id",
            CriterionWindowMode.DayWindow,
            "n.eff_gas_day",
            PipelinesPointflowsTable)
    };

    /// <summary>The feed with this id, or <c>null</c>. Case-insensitive.</summary>
    public static CriterionFeedDescriptor? Find(string feedId) =>
        All.FirstOrDefault(f => string.Equals(f.FeedId, feedId, StringComparison.OrdinalIgnoreCase));
}
