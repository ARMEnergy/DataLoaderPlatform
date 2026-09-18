using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataLoader.Genscape;

/// <summary>The scalar shapes the Genscape target tables use.</summary>
public enum GenscapeColumnType
{
    /// <summary><c>VARCHAR(n)</c> -> <see cref="string"/>.</summary>
    String,

    /// <summary><c>TINYINT</c> -> <see cref="byte"/>. The derived <c>Week</c>.</summary>
    Byte,

    /// <summary><c>SMALLINT</c> -> <see cref="short"/>. The derived <c>Year</c>.</summary>
    Int16,

    /// <summary><c>FLOAT</c> -> <see cref="double"/>.</summary>
    Double,

    /// <summary><c>DATE</c> -> <see cref="System.DateTime"/> at midnight.</summary>
    Date
}

/// <summary>
/// One column of one target table. This single record drives three things that would
/// otherwise have to be kept in sync by hand:
///
/// <list type="number">
///   <item>the DataTable column name, order and CLR type in <see cref="GenscapeTvpSink"/>,</item>
///   <item>the expected TVP column in <c>sql/Genscape/002</c>, asserted by
///         <c>GenscapeTvpContractTests</c> against <see cref="SqlType"/>,</item>
///   <item>whether a record missing the value is dropped (<see cref="Required"/>).</item>
/// </list>
///
/// <para>
/// Because a TVP binds BY POSITION, the ORDER of a table's column list IS the contract.
/// The test parses the real <c>.sql</c> file, so a change on either side alone fails
/// the build rather than silently corrupting rows.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable column name.</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(50)</c>.</param>
/// <param name="Type">CLR type.</param>
/// <param name="Required">
/// <c>true</c> =&gt; <c>NOT NULL</c> in the TVP, and a record whose value is missing is
/// DROPPED rather than merged under a blank key. Every primary-key component is
/// required.
/// </param>
public sealed record GenscapeColumn(string Name, string SqlType, GenscapeColumnType Type, bool Required)
{
    /// <summary>The CLR type this column contributes to the sink's DataTable.</summary>
    public Type ClrType => Type switch
    {
        GenscapeColumnType.String => typeof(string),
        GenscapeColumnType.Byte => typeof(byte),
        GenscapeColumnType.Int16 => typeof(short),
        GenscapeColumnType.Double => typeof(double),
        GenscapeColumnType.Date => typeof(DateTime),
        _ => throw new NotSupportedException($"Unmapped GenscapeColumnType '{Type}'.")
    };

    /// <summary>The declared width of a <c>VARCHAR(n)</c>, or <c>-1</c> for a type with no length.</summary>
    public int MaxLength
    {
        get
        {
            var open = SqlType.IndexOf('(');
            if (open < 0) return -1;

            var close = SqlType.IndexOf(')', open + 1);
            if (close < 0) return -1;

            return int.TryParse(SqlType[(open + 1)..close].Trim(), out var n) ? n : -1;
        }
    }

    // ---- terse builders, so the registry below reads like the SQL ----

    public static GenscapeColumn Str(string name, int size, bool required = false) =>
        new(name, $"VARCHAR({size})", GenscapeColumnType.String, required);

    public static GenscapeColumn U8(string name, bool required = false) =>
        new(name, "TINYINT", GenscapeColumnType.Byte, required);

    public static GenscapeColumn I16(string name, bool required = false) =>
        new(name, "SMALLINT", GenscapeColumnType.Int16, required);

    public static GenscapeColumn Flt(string name, bool required = false) =>
        new(name, "FLOAT", GenscapeColumnType.Double, required);

    public static GenscapeColumn Dat(string name, bool required = false) =>
        new(name, "DATE", GenscapeColumnType.Date, required);
}

/// <summary>
/// One TARGET TABLE: its TVP, its merge proc, and the ordered column list that IS the
/// TVP contract.
/// </summary>
/// <param name="TableName">Fully qualified SQL Server table, e.g. <c>arm.OilFundamentals_CrudeStorage_Weekly</c>.</param>
/// <param name="TvpType">The table type in <c>sql/Genscape/002</c>.</param>
/// <param name="MergeProc">The merge proc in <c>sql/Genscape/003</c>.</param>
/// <param name="Columns">Ordered TVP columns. Order is the contract.</param>
public sealed record GenscapeTableDescriptor(
    string TableName,
    string TvpType,
    string MergeProc,
    IReadOnlyList<GenscapeColumn> Columns);

/// <summary>
/// One row destined for a target table, positionally aligned to its table's
/// <see cref="GenscapeTableDescriptor.Columns"/>.
///
/// <para>
/// <see cref="Values"/> never contains a CLR <c>null</c> — an absent value is
/// <see cref="DBNull.Value"/>, which is what <c>DataTable.Rows.Add</c> wants.
/// </para>
/// </summary>
public sealed class GenscapeRow
{
    public GenscapeRow(object[] values) => Values = values;

    /// <summary>Values in descriptor column order. Length always equals the column count.</summary>
    public object[] Values { get; }
}

/// <summary>Stable feed ids — the <c>EnabledFeeds</c> values and the pipeline ids.</summary>
public static class GenscapeFeed
{
    public const string CrudeStorageWeekly = "CrudeStorageWeekly";
    public const string CrudeTransportationWeekly = "CrudeTransportationWeekly";
}

/// <summary>
/// What one response body cost in dropped or altered values. Accumulated across a work
/// unit (including every sub-request a bisection makes) and logged once, so a shape
/// change at the source shows up as a number in the log rather than as quietly missing
/// rows later.
/// </summary>
public sealed class GenscapeReadStats
{
    /// <summary>Records the API returned, before any were dropped.</summary>
    public int SourceRecords;

    /// <summary>Records dropped because a PRIMARY KEY component was missing or unreadable.</summary>
    public int DroppedRequired;

    /// <summary>String values shortened to fit their VARCHAR(50) target.</summary>
    public int Truncated;

    /// <summary>HTTP requests issued — more than one means the window was bisected.</summary>
    public int Requests;

    /// <summary>Times a response came back at the row cap and had to be split.</summary>
    public int CapHits;
}

/// <summary>
/// One Genscape feed: its endpoint, its target table, and the only code that knows the
/// shape of its JSON.
///
/// <para>
/// This is a small class hierarchy rather than a record with a delegate because the two
/// payloads are genuinely different types. Each subclass owns its DTO, and its
/// <see cref="Parse"/> emits <see cref="GenscapeRow.Values"/> in
/// <see cref="GenscapeTableDescriptor.Columns"/> order — the same order the sink builds
/// its DataTable from and the same order <c>sql/Genscape/002</c> declares. A column
/// added on one side without the other fails <c>GenscapeTvpContractTests</c>.
/// </para>
/// <para>
/// <b>Year and Week are NOT read from the payload.</b> Both feeds derive them from
/// <c>ReportDate</c> via <see cref="GenscapeTime.WeekOfYear"/> and
/// <see cref="GenscapeTime.YearOf"/>. See those methods for why, and
/// <c>docs/apis/Genscape.md</c> §4 for the live evidence.
/// </para>
/// </summary>
public abstract class GenscapeFeedDescriptor
{
    /// <summary>
    /// Shared deserialiser options.
    ///
    /// <para>
    /// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> so the observed
    /// camelCase (<c>reportDate</c>, <c>storageFieldType</c>, <c>flowBPD</c>) binds
    /// without a naming policy, and a vendor that re-cases a field does not silently
    /// null the column.
    /// </para>
    /// <para>
    /// <see cref="JsonNumberHandling.AllowReadingFromString"/> because the quantities
    /// arrive as JSON numbers today but cost nothing to accept as quoted strings; the
    /// alternative failure is a whole work unit lost to one re-typed field.
    /// </para>
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    protected GenscapeFeedDescriptor(
        string feedId, string displayName, string endpointPath, GenscapeTableDescriptor table)
    {
        FeedId = feedId;
        DisplayName = displayName;
        EndpointPath = endpointPath;
        Table = table;
    }

    /// <summary>Stable id — matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    public string FeedId { get; }

    public string DisplayName { get; }

    /// <summary>
    /// Path under <see cref="GenscapeSettings.BaseUrl"/>, e.g. <c>crude-storage/weekly</c>.
    /// No leading slash: it is combined with a base URL that has its trailing slash trimmed.
    /// </summary>
    public string EndpointPath { get; }

    public GenscapeTableDescriptor Table { get; }

    /// <summary>
    /// Turns one response body into target rows, accumulating into
    /// <paramref name="stats"/>.
    ///
    /// <para>
    /// A record missing any PRIMARY KEY component is DROPPED and counted rather than
    /// merged under a blank key. The nullable measures (<c>StorageAmount</c>,
    /// <c>CapacityUtilization</c>, <c>FlowBPD</c>) are allowed through as NULL.
    /// </para>
    /// </summary>
    public abstract IReadOnlyList<GenscapeRow> Parse(string json, GenscapeReadStats stats);

    /// <summary>
    /// Deserialises the <c>{"data":[…]}</c> envelope both endpoints return.
    ///
    /// <para>
    /// An ABSENT <c>data</c> property throws: it means the envelope changed shape, and
    /// silently reading that as "no rows" would turn a broken contract into a
    /// successful empty load. An EMPTY <c>data</c> array is a legitimate outcome (a
    /// window entirely before the first report or after the latest published one) and
    /// returns an empty list — see the status matrix in
    /// <see cref="GenscapeSourceReader"/>.
    /// </para>
    /// </summary>
    protected static List<T> Envelope<T>(string json, string feedId)
    {
        GenscapeEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<GenscapeEnvelope<T>>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Genscape {feedId}: the response is not valid JSON ({ex.Message}). The endpoint returned " +
                "200, so this is a payload shape change, not a transport failure.", ex);
        }

        if (envelope?.Data is null)
            throw new InvalidOperationException(
                $"Genscape {feedId}: the response has no 'data' array. Both endpoints return " +
                "{\"data\":[…]}; a missing array is a contract change and must not be read as an empty load.");

        return envelope.Data;
    }

    /// <summary>
    /// Common leading columns: ReportDate, then the DERIVED Year and Week. Returns
    /// false when <paramref name="reportDate"/> cannot be read, which drops the record.
    /// </summary>
    protected static bool TryStamp(string? reportDate, object[] values, GenscapeReadStats stats)
    {
        if (!GenscapeTime.TryParseReportDate(reportDate, out var date))
        {
            stats.DroppedRequired++;
            return false;
        }

        values[0] = date.ToDateTime(TimeOnly.MinValue);
        values[1] = GenscapeTime.YearOf(date);
        values[2] = GenscapeTime.WeekOfYear(date);
        return true;
    }

    /// <summary>
    /// A REQUIRED VARCHAR component of the primary key: trimmed, width-fitted, and
    /// rejected when blank.
    ///
    /// <para>
    /// Truncation is deliberate rather than a failure. SqlClient shortens an over-long
    /// TVP string silently on some paths and errors on others; neither is a good
    /// outcome, so the decision is made here and COUNTED. No live value comes close —
    /// the widest observed is 24 characters into VARCHAR(50) — but the vendor can widen
    /// a label at any time without telling anyone.
    /// </para>
    /// </summary>
    protected static bool TryKeyText(
        string? raw, GenscapeColumn column, GenscapeReadStats stats, out object value)
    {
        value = DBNull.Value;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            stats.DroppedRequired++;
            return false;
        }

        var max = column.MaxLength;
        if (max > 0 && text.Length > max)
        {
            text = text[..max];
            stats.Truncated++;
        }

        value = text;
        return true;
    }

    /// <summary>
    /// A nullable FLOAT measure. Never drops a record.
    ///
    /// <para>
    /// NaN and infinity become NULL rather than reaching SQL Server: <c>FLOAT</c>
    /// accepts neither, and the whole TVP call would fail on one bad quantity.
    /// </para>
    /// </summary>
    protected static object Measure(double? value) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value.Value
            : DBNull.Value;
}

/// <summary>
/// <c>GET /oil-fundamentals/v1/crude-storage/weekly</c> -&gt;
/// <c>arm.OilFundamentals_CrudeStorage_Weekly</c>.
///
/// <para>
/// ⚠ This is the endpoint whose <c>week</c> field is a week-of-MONTH
/// (2026-08-28 gives 4). <see cref="StorageRecord.Week"/> exists only so the value can
/// be compared in tests; it is never written. See <see cref="GenscapeTime.WeekOfYear"/>.
/// </para>
/// </summary>
public sealed class CrudeStorageWeeklyFeed : GenscapeFeedDescriptor
{
    public CrudeStorageWeeklyFeed()
        : base(GenscapeFeed.CrudeStorageWeekly,
               "Crude storage, weekly",
               "crude-storage/weekly",
               GenscapeDescriptors.CrudeStorageWeeklyTable)
    {
    }

    public override IReadOnlyList<GenscapeRow> Parse(string json, GenscapeReadStats stats)
    {
        var records = Envelope<StorageRecord>(json, FeedId);
        stats.SourceRecords += records.Count;

        var columns = Table.Columns;
        var rows = new List<GenscapeRow>(records.Count);

        foreach (var record in records)
        {
            var values = new object[columns.Count];

            if (!TryStamp(record.ReportDate, values, stats)) continue;
            if (!TryKeyText(record.Region, columns[3], stats, out values[3])) continue;
            if (!TryKeyText(record.Product, columns[4], stats, out values[4])) continue;
            if (!TryKeyText(record.StorageFieldType, columns[5], stats, out values[5])) continue;

            values[6] = Measure(record.StorageAmount);
            values[7] = Measure(record.CapacityUtilization);

            rows.Add(new GenscapeRow(values));
        }

        return rows;
    }

    /// <summary>
    /// The payload record. Field names and casing verified live 2026-09-08 against both
    /// request regions.
    /// </summary>
    internal sealed class StorageRecord
    {
        public string? ReportDate { get; set; }

        /// <summary>The RESPONSE region ('Cushing', 'Houston'), not the request region.</summary>
        public string? Region { get; set; }

        /// <summary>Read but NOT written — the loader derives Year from ReportDate.</summary>
        public int? Year { get; set; }

        /// <summary>Read but NOT written — this is a week-of-MONTH. See the class remarks.</summary>
        public int? Week { get; set; }

        public double? StorageAmount { get; set; }
        public double? CapacityUtilization { get; set; }
        public string? Product { get; set; }
        public string? StorageFieldType { get; set; }
    }
}

/// <summary>
/// <c>GET /oil-fundamentals/v1/crude-transportation/weekly</c> -&gt;
/// <c>arm.OilFundamentals_CrudeTransportation_Weekly</c>.
///
/// <para>
/// This endpoint's <c>week</c> field ALREADY equals the calendar week-of-year, which is
/// what the derivation was validated against. It is still derived rather than read, so
/// both tables are populated by the same rule and neither depends on the vendor
/// continuing to agree.
/// </para>
/// </summary>
public sealed class CrudeTransportationWeeklyFeed : GenscapeFeedDescriptor
{
    public CrudeTransportationWeeklyFeed()
        : base(GenscapeFeed.CrudeTransportationWeekly,
               "Crude transportation, weekly",
               "crude-transportation/weekly",
               GenscapeDescriptors.CrudeTransportationWeeklyTable)
    {
    }

    public override IReadOnlyList<GenscapeRow> Parse(string json, GenscapeReadStats stats)
    {
        var records = Envelope<TransportationRecord>(json, FeedId);
        stats.SourceRecords += records.Count;

        var columns = Table.Columns;
        var rows = new List<GenscapeRow>(records.Count);

        foreach (var record in records)
        {
            var values = new object[columns.Count];

            if (!TryStamp(record.ReportDate, values, stats)) continue;
            if (!TryKeyText(record.Region, columns[3], stats, out values[3])) continue;
            if (!TryKeyText(record.Type, columns[4], stats, out values[4])) continue;

            values[5] = Measure(record.FlowBPD);

            rows.Add(new GenscapeRow(values));
        }

        return rows;
    }

    internal sealed class TransportationRecord
    {
        /// <summary>Transport mode — 'Pipeline' or 'Rail' in every row observed.</summary>
        public string? Type { get; set; }

        public string? ReportDate { get; set; }

        /// <summary>A CORRIDOR ('Cushing to Gulf Coast', 'Into Houston'), not a place.</summary>
        public string? Region { get; set; }

        /// <summary>Read but NOT written — derived from ReportDate.</summary>
        public int? Year { get; set; }

        /// <summary>Read but NOT written — derived from ReportDate.</summary>
        public int? Week { get; set; }

        public double? FlowBPD { get; set; }
    }
}

/// <summary>The <c>{"data":[…]}</c> envelope both endpoints return.</summary>
internal sealed class GenscapeEnvelope<T>
{
    public List<T>? Data { get; set; }
}

/// <summary>
/// The registry: the two target tables and the two feeds.
///
/// <para>
/// Every column below was verified against the live API on 2026-09-08 — the field names
/// and casing from the payloads themselves, the widths from the requester's DDL (the
/// widest observed value is a 24-character Region against VARCHAR(50)).
/// </para>
/// </summary>
public static class GenscapeDescriptors
{
    // =========================================================================
    // GET crude-storage/weekly -> arm.OilFundamentals_CrudeStorage_Weekly
    //
    // Column ORDER is the TVP contract, and the parsers above index into it
    // POSITIONALLY (values[3] is Region, values[4] is Product...). Reordering
    // this list without reordering the parser puts every value in the wrong
    // column, and because the TVP, the DataTable and the .sql all follow the
    // descriptor they would all agree with each other while being wrong.
    // GenscapeContractTests pins the order by name for exactly that reason.
    // =========================================================================
    public static readonly GenscapeTableDescriptor CrudeStorageWeeklyTable = new(
        "arm.OilFundamentals_CrudeStorage_Weekly",
        "arm.OilFundamentalsCrudeStorageWeeklyTvp",
        "arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly",
        new[]
        {
            GenscapeColumn.Dat("ReportDate", required: true),
            GenscapeColumn.I16("Year", required: true),          // derived
            GenscapeColumn.U8("Week", required: true),           // derived
            GenscapeColumn.Str("Region", 50, required: true),
            GenscapeColumn.Str("Product", 50, required: true),
            GenscapeColumn.Str("StorageFieldType", 50, required: true),
            GenscapeColumn.Flt("StorageAmount"),
            GenscapeColumn.Flt("CapacityUtilization")
        });

    // =========================================================================
    // GET crude-transportation/weekly
    //   -> arm.OilFundamentals_CrudeTransportation_Weekly
    //
    // Region precedes Type here because that is the TABLE's column order. The
    // table's PRIMARY KEY orders them the other way round (Type then Region);
    // that is a physical-layout choice and has nothing to do with the TVP, which
    // binds by position against the DataTable built from this list.
    // =========================================================================
    public static readonly GenscapeTableDescriptor CrudeTransportationWeeklyTable = new(
        "arm.OilFundamentals_CrudeTransportation_Weekly",
        "arm.OilFundamentalsCrudeTransportationWeeklyTvp",
        "arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly",
        new[]
        {
            GenscapeColumn.Dat("ReportDate", required: true),
            GenscapeColumn.I16("Year", required: true),          // derived
            GenscapeColumn.U8("Week", required: true),           // derived
            GenscapeColumn.Str("Region", 50, required: true),
            GenscapeColumn.Str("Type", 50, required: true),
            GenscapeColumn.Flt("FlowBPD")
        });

    /// <summary>The two feeds, in the order they are logged and run.</summary>
    public static readonly IReadOnlyList<GenscapeFeedDescriptor> All = new GenscapeFeedDescriptor[]
    {
        new CrudeStorageWeeklyFeed(),
        new CrudeTransportationWeeklyFeed()
    };

    /// <summary>Both target tables, for the TVP contract gate.</summary>
    public static readonly IReadOnlyList<GenscapeTableDescriptor> AllTables = new[]
    {
        CrudeStorageWeeklyTable,
        CrudeTransportationWeeklyTable
    };

    /// <summary>The feed with this id, or <c>null</c>. Case-insensitive.</summary>
    public static GenscapeFeedDescriptor? Find(string feedId) =>
        All.FirstOrDefault(f => string.Equals(f.FeedId, feedId, StringComparison.OrdinalIgnoreCase));
}
