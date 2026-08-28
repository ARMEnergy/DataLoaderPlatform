using System.Net;
using System.Text.Json;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// One place that wires <see cref="EvoMarketDataSourceReader"/> over a fake
/// <see cref="System.Net.Http.HttpMessageHandler"/> and a fake <c>arm.FileLog</c>, so every reader
/// test is offline and identical in setup. No network, no database, no credential — the settings
/// carry an obvious dummy key, and the Evolution Markets credential is a request HEADER that never
/// rides a URL.
/// </summary>
internal static class ReaderHarness
{
    public const string BaseUrl = "https://evolve-api.evomarkets.com";

    /// <summary>An obvious dummy. The real key must never appear in this repo.</summary>
    public const string DummyApiKey = "evolutionmarkets-test-key-not-real";

    /// <summary>The date the committed live fixture was captured for.</summary>
    public static readonly DateOnly FixtureDate = new(2026, 8, 24);

    public static EvoSettings Settings(
        int daysBack = 30,
        int settledAfterDays = 30,
        int pageSize = 5000,
        EvoHotKeyStrategy hotKey = EvoHotKeyStrategy.RunDate,
        string? datasetName = null) => new()
        {
            BaseUrl = BaseUrl,
            ConnectionString = "unused-in-tests",
            ApiKey = DummyApiKey,
            DaysBack = daysBack,
            SettledAfterDays = settledAfterDays,
            PageSize = pageSize,
            HotZoneKeyStrategy = hotKey,
            DatasetName = datasetName
        };

    /// <summary>A work unit shaped exactly as <see cref="EvoMarketDataWorkUnitProvider"/> builds one.</summary>
    public static EvoMarketDataWorkUnit Unit(
        DateOnly? businessDate = null,
        string? datasetName = null,
        string hot = "20260825")
    {
        var d = businessDate ?? FixtureDate;
        return new EvoMarketDataWorkUnit
        {
            BusinessDate = d,
            RequestPath = EvoMarketDataWorkUnitProvider.BuildRequestPath(d, datasetName),
            KeyValue = EvoMarketDataWorkUnitProvider.BuildKey(d, settled: false, hot)
        };
    }

    public sealed record ReadResult(
        IReadOnlyList<MarketDataRow> Rows,
        FakeEvoFileLog FileLog,
        ListLogger Log,
        FakeHttpMessageHandler Handler);

    /// <summary>Runs the reader against a single scripted HTTP response and captures everything.</summary>
    public static async Task<ReadResult> RunAsync(
        HttpStatusCode status,
        string body,
        EvoMarketDataWorkUnit? unit = null,
        IEvoFileLog? fileLogOverride = null,
        EvoSettings? settings = null)
        => await RunAsync(FakeHttpMessageHandler.Respond(status, body), unit, fileLogOverride, settings);

    /// <summary>Runs the reader against an arbitrary scripted handler (the paging tests use this).</summary>
    public static async Task<ReadResult> RunAsync(
        FakeHttpMessageHandler handler,
        EvoMarketDataWorkUnit? unit = null,
        IEvoFileLog? fileLogOverride = null,
        EvoSettings? settings = null)
    {
        var fileLog = new FakeEvoFileLog();
        var log = new ListLogger();
        var reader = new EvoMarketDataSourceReader(
            handler.NewClient(), settings ?? Settings(), fileLogOverride ?? fileLog, log);

        var rows = await reader.ReadAsync(unit ?? Unit(), CancellationToken.None);
        return new ReadResult(rows, fileLog, log, handler);
    }

    /// <summary>Builds a reader without running it (for the throw-path tests).</summary>
    public static (EvoMarketDataSourceReader Reader, FakeEvoFileLog FileLog, ListLogger Log) Reader(
        HttpStatusCode status, string body, EvoSettings? settings = null)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var fileLog = new FakeEvoFileLog();
        var log = new ListLogger();
        return (new EvoMarketDataSourceReader(handler.NewClient(), settings ?? Settings(), fileLog, log), fileLog, log);
    }

    public static (EvoMarketDataSourceReader Reader, ListLogger Log) ReaderWith(
        IEvoFileLog fileLog, HttpStatusCode status, string body)
    {
        var handler = FakeHttpMessageHandler.Respond(status, body);
        var log = new ListLogger();
        return (new EvoMarketDataSourceReader(handler.NewClient(), Settings(), fileLog, log), log);
    }

    /// <summary>Convenience: the parsed rows of a 200 body.</summary>
    public static async Task<IReadOnlyList<MarketDataRow>> ReadAsync(string body) =>
        (await RunAsync(HttpStatusCode.OK, body)).Rows;

    /// <summary>Convenience: the single parsed row of a 200 body carrying exactly one record.</summary>
    public static async Task<MarketDataRow> ReadOneAsync(string record) =>
        (await ReadAsync("[" + record + "]")).Single();
}

/// <summary>
/// The committed live fixtures plus small hand-built JSON bodies.
///
/// <para>The fixtures are read from disk (copied next to the test assembly by the csproj) so the
/// tests assert against REAL vendor bytes rather than a paraphrase of them.</para>
/// </summary>
internal static class Samples
{
    private static string Load(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", fileName));

    /// <summary>A VERBATIM 5-record slice of a real live 200 for 2026-08-24, full pinned projection.</summary>
    public static string LiveSlice => Load("history_live_slice.json");

    /// <summary>The REAL body for a non-publishing day, byte-for-byte: two characters.</summary>
    public static string Empty => Load("history_empty.json");

    /// <summary>The live slice parsed, for tests that need to reason about the raw JSON.</summary>
    public static JsonDocument LiveSliceDoc => JsonDocument.Parse(LiveSlice);

    /// <summary>
    /// A synthetic array of <paramref name="count"/> records with distinct <c>priceId</c>s, for the
    /// paging tests. Values are deliberately uninteresting — the pager, not the parse, is the subject.
    /// </summary>
    public static string Page(int count, int idSeed = 0)
    {
        var records = Enumerable.Range(idSeed, count).Select(i => $$"""
            {"priceId":"{{Id(i)}}","market":"US Natural Gas Index","term":"2026-Oct",
             "instrumentId":"99c5fe58-9c6a-41d5-bd12-6340a7a6b6d6","instrument":"Test Instrument",
             "priceTs":"2026-08-24T00:00:00.000Z","date":"2026-08-24","priceType":"Indicative",
             "ask":0.1,"bid":0.05,"mid":0.075,"change":0.01,"currency":"USD"}
            """);
        return "[" + string.Join(",", records) + "]";
    }

    /// <summary>
    /// A deterministic GUID for record <paramref name="i"/> of a synthetic page.
    ///
    /// <para><b>Deliberately 1-based.</b> A 0 would render as
    /// <c>00000000-0000-0000-0000-000000000000</c> — <see cref="Guid.Empty"/> — which the reader
    /// correctly REJECTS as an unkeyable placeholder rather than an id (see
    /// <c>ParseTests.AnAllZeroGuidKey_IsRejected</c>). Generating it here would silently cost every
    /// synthetic page one row and make the paging assertions off by one.</para>
    /// </summary>
    public static string Id(int i) => $"00000000-0000-0000-0000-{i + 1:D12}";

    /// <summary>One record with every field the dataset ever populates, as a JSON object literal.</summary>
    public const string FullRecord = """
        {"priceId":"6d11bc3c-a601-480a-8032-b6d42068b9ea","market":"US Natural Gas Index",
         "term":"Sep'26-Oct'26","tenor":"3m","instrumentId":"997feae2-4c97-4e14-b090-203d625ff5a5",
         "instrument":"Socal-Border Index Futures","priceTs":"2026-08-24T00:00:00.000Z",
         "date":"2026-08-24","priceType":"Indicative","ask":-0.02,"bid":-0.0325,"mid":-0.0263,
         "change":-0.0263,"currency":"USD"}
        """;
}
