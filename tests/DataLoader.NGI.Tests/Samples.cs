using System.Reflection;
using System.Text.Json;

namespace DataLoader.NGI.Tests;

/// <summary>
/// The REAL captured NGI live payloads (committed under <c>Samples\</c>, copied next to the test
/// assembly by the csproj) plus the small synthetic literals the edge-case tests need.
///
/// <para>The two fixtures are the ground truth for every mapping assertion in this project:</para>
/// <list type="bullet">
///   <item><c>bidweekDatafeed_20260801.json</c> - a real live <c>200</c> for
///     <c>issue_date=2026-08-01</c> (a <b>SATURDAY</b>), <b>163 records</b>, all 11 fields per
///     record, the literal string <c>"None"</c> as the null sentinel.</item>
///   <item><c>bidweekLocations.json</c> - a real live <c>200</c>, <b>163 entries</b> in a single
///     <c>"Bidweek Locations"</c> object mapping location NAME -&gt; point CODE.</item>
/// </list>
///
/// <para>Everything is read from disk, never from the network, so the whole suite runs offline and
/// repeatably.</para>
/// </summary>
internal static class Samples
{
    // ---- the real fixtures --------------------------------------------------------------------

    public const string DatafeedFileName = "bidweekDatafeed_20260801.json";
    public const string LocationsFileName = "bidweekLocations.json";

    /// <summary>The live 2026-08-01 datafeed response (163 records).</summary>
    public static string Datafeed20260801 => Read(DatafeedFileName);

    /// <summary>The live bidweekLocations response (163 name -&gt; code entries).</summary>
    public static string Locations => Read(LocationsFileName);

    /// <summary>Facts about the fixtures, asserted directly by the tests (see FixtureFactsTests).</summary>
    public const int ExpectedRecordCount = 163;
    public const int ExpectedLocationCount = 163;
    public const int ExpectedPriceNullCount = 45;    // Low / High / Average = "None"
    public const int ExpectedActivityNullCount = 47; // Volume / Deals      = "None"

    public static readonly DateOnly FixtureIssueDate = new(2026, 8, 1);   // a SATURDAY
    public static readonly DateOnly FixtureSurveyStart = new(2026, 7, 27);
    public static readonly DateOnly FixtureSurveyEnd = new(2026, 7, 29);

    private static readonly string SamplesDir =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Samples");

    public static string Read(string fileName)
    {
        var path = Path.Combine(SamplesDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"NGI sample fixture not found: {path}", path);
        return File.ReadAllText(path);
    }

    /// <summary>The raw <c>data</c> map of the datafeed fixture, for cross-checks against the parse output.</summary>
    public static Dictionary<string, JsonElement> RawDatafeedRecords()
    {
        using var doc = JsonDocument.Parse(Datafeed20260801);
        return doc.RootElement.GetProperty("data").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>The raw locations map (NAME -&gt; CODE) of the locations fixture.</summary>
    public static Dictionary<string, string> RawLocationMap()
    {
        using var doc = JsonDocument.Parse(Locations);
        return doc.RootElement.GetProperty("Bidweek Locations").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    // ---- synthetic literals for the edge cases the live capture cannot contain ------------------

    /// <summary>Envelope helper: wraps a <c>data</c> map body in the live <c>{meta,data}</c> shape.</summary>
    public static string Envelope(string dataJson, string metaJson = MetaBlock) =>
        $"{{\"meta\":{metaJson},\"data\":{dataJson}}}";

    public const string MetaBlock =
        "{\"issue_date\":\"2026-08-01\",\"start_date\":\"2026-07-27\",\"end_date\":\"2026-07-29\"}";

    /// <summary>One fully-populated record, live field spellings (note the SPACES).</summary>
    public const string OneGoodRecord = """
    {"STXNGPL":{"Point Code":"STXNGPL","Issue Date":"2026-08-01","Survey Start":"2026-07-27","Survey End":"2026-07-29","Region":"South Texas","Pricing Point":"NGPL S. TX","Low":"2.325","High":"2.365","Average":"2.335","Volume":"240","Deals":"15"}}
    """;

    /// <summary>Every field is the literal null sentinel "None" - INCLUDING the two strings.</summary>
    public const string AllNoneRecord = """
    {"ETXCARTH":{"Point Code":"ETXCARTH","Issue Date":"2026-08-01","Survey Start":"None","Survey End":"None","Region":"None","Pricing Point":"None","Low":"None","High":"None","Average":"None","Volume":"None","Deals":"None"}}
    """;

    /// <summary>The map key disagrees with the record's own "Point Code" - the RECORD must win.</summary>
    public const string MapKeyMismatchRecord = """
    {"WRONG-KEY":{"Point Code":"STXAGUAD","Issue Date":"2026-08-01","Survey Start":"2026-07-27","Survey End":"2026-07-29","Region":"South Texas","Pricing Point":"Agua Dulce","Low":"2.360","High":"2.390","Average":"2.375","Volume":"None","Deals":"None"}}
    """;

    /// <summary>No "Point Code" field at all - the map key is the fallback.</summary>
    public const string NoPointCodeFieldRecord = """
    {"STXAGUAD":{"Issue Date":"2026-08-01","Survey Start":"2026-07-27","Survey End":"2026-07-29","Region":"South Texas","Pricing Point":"Agua Dulce","Low":"2.360","High":"2.390","Average":"2.375","Volume":"None","Deals":"None"}}
    """;

    /// <summary>Blank map key AND a "None" point code - unkeyable, must be DROPPED and counted.</summary>
    public const string UnkeyableRecord = """
    {"":{"Point Code":"None","Issue Date":"2026-08-01","Region":"South Texas","Pricing Point":"Nowhere","Low":"2.360","High":"2.390","Average":"2.375","Volume":"1","Deals":"1"}}
    """;

    /// <summary>Snake/camel fallback spellings - none of the live space-bearing names present.</summary>
    public const string SnakeCaseRecord = """
    {"STXNGPL":{"point_code":"STXNGPL","issue_date":"2026-08-01","survey_start":"2026-07-27","survey_end":"2026-07-29","region":"South Texas","pricing_point":"NGPL S. TX","low":"2.325","high":"2.365","average":"2.335","volume":"240","deals":"15"}}
    """;

    /// <summary>No survey window on the record - the meta block is the fallback source.</summary>
    public const string NoSurveyWindowRecord = """
    {"STXNGPL":{"Point Code":"STXNGPL","Issue Date":"2026-08-01","Region":"South Texas","Pricing Point":"NGPL S. TX","Low":"2.325","High":"2.365","Average":"2.335","Volume":"240","Deals":"15"}}
    """;

    /// <summary>A locations envelope with the live spelling (note the space and the capital L).</summary>
    public static string LocationsEnvelope(string mapJson, string node = "Bidweek Locations") =>
        $"{{\"{node}\":{mapJson}}}";
}
