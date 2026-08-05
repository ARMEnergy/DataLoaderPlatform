using System.Net;
using DataLoader.Vulcan;
using DataLoader.Vulcan.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Vulcan.Tests;

/// <summary>
/// Reader-level tests for the multi-page ReadAsync loop. Uses a hand-written
/// HttpMessageHandler stub (no mocking library referenced by the test csproj)
/// that returns a scripted sequence of response bodies and records each request's
/// posted SQL so offsets can be asserted. Never touches the network or a database.
/// </summary>
public class VulcanQuerySourceReaderPagingTests
{
    /// <summary>Stub handler returning a scripted queue of bodies; captures each request's SQL.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        private readonly string? _repeatBody; // when set, always returns this (for MaxPages guard)
        public List<string> CapturedSql { get; } = new();
        public int CallCount { get; private set; }

        public ScriptedHandler(IEnumerable<string> bodies) => _bodies = new Queue<string>(bodies);
        private ScriptedHandler(string repeatBody) { _bodies = new Queue<string>(); _repeatBody = repeatBody; }
        public static ScriptedHandler AlwaysReturns(string body) => new(body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var requestJson = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            CapturedSql.Add(ExtractQuery(requestJson));

            var body = _repeatBody ?? (_bodies.Count > 0 ? _bodies.Dequeue() : "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }

        // The reader posts { "query": "<sql>" }; pull the sql back out for offset assertions.
        private static string ExtractQuery(string requestJson)
        {
            if (string.IsNullOrEmpty(requestJson)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            return doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        }
    }

    private static VulcanQuerySourceReader<ProjectRankingRow> BuildReader(ScriptedHandler handler, int pageSize, int maxPages = 10000)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://vulcan.test") };
        var settings = Options.Create(new VulcanSettings { PageSize = pageSize, MaxPages = maxPages });
        return new VulcanQuerySourceReader<ProjectRankingRow>(http, settings, NullLogger.Instance);
    }

    private static VulcanWorkUnit WorkUnit() => new()
    {
        TableId = "metadata_history",
        RunDate = new DateOnly(2026, 7, 21),
        Query = "unused",
        Spec = VulcanTableSpec.MetadataHistory,
        Watermark = new DateOnly(2026, 1, 15)
    };

    // NDJSON page body of n rows (one bare JSON object per line).
    private static string Page(int n) =>
        string.Concat(Enumerable.Range(0, n).Select(i => $"{{ \"synmax_id\": \"R{i}\" }}\n"));

    [Fact]
    public async Task ReadAsync_MultiPage_ConcatsAndTerminatesOnShortFinalPage()
    {
        // page1 = 2 rows (== PageSize), page2 = 1 row (< PageSize) -> stop.
        var handler = new ScriptedHandler(new[] { Page(2), Page(1) });
        var reader = BuildReader(handler, pageSize: 2);

        var rows = await reader.ReadAsync(WorkUnit(), CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("OFFSET 0 ROWS", handler.CapturedSql[0]);
        Assert.Contains("OFFSET 2 ROWS", handler.CapturedSql[1]);
    }

    [Fact]
    public async Task ReadAsync_ExactlyFullThenEmptyPage_TerminatesWithoutThirdCall()
    {
        // page1 = 2 rows (== PageSize) forces a second call; page2 = 0 rows -> stop.
        var handler = new ScriptedHandler(new[] { Page(2), Page(0) });
        var reader = BuildReader(handler, pageSize: 2);

        var rows = await reader.ReadAsync(WorkUnit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ReadAsync_SingleFullThenShort_AdvancesOffsetByPageSize()
    {
        // Focus on offset progression (not row totals): full, full, short.
        var handler = new ScriptedHandler(new[] { Page(2), Page(2), Page(1) });
        var reader = BuildReader(handler, pageSize: 2);

        await reader.ReadAsync(WorkUnit(), CancellationToken.None);

        Assert.Equal(3, handler.CallCount);
        Assert.Contains("OFFSET 0 ROWS", handler.CapturedSql[0]);
        Assert.Contains("OFFSET 2 ROWS", handler.CapturedSql[1]);
        Assert.Contains("OFFSET 4 ROWS", handler.CapturedSql[2]);
    }

    [Fact]
    public async Task ReadAsync_MaxPagesGuard_ThrowsInvalidOperation()
    {
        // Always-full pages would loop forever; MaxPages=1 must abort. AlwaysReturns
        // guards against a real infinite loop even if the guard were broken.
        var handler = ScriptedHandler.AlwaysReturns(Page(2));
        var reader = BuildReader(handler, pageSize: 2, maxPages: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(WorkUnit(), CancellationToken.None));
        Assert.Contains("MaxPages", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadAsync_InvalidPageSize_Throws(int pageSize)
    {
        var handler = new ScriptedHandler(Array.Empty<string>());
        var reader = BuildReader(handler, pageSize: pageSize);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(WorkUnit(), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_LoneObjectPage_YieldsSingleRow()
    {
        // A page body that is a single bare JSON object counts as 1 row (< PageSize) -> stop.
        var handler = new ScriptedHandler(new[] { """{ "synmax_id": "SOLO" }""" });
        var reader = BuildReader(handler, pageSize: 2);

        var rows = await reader.ReadAsync(WorkUnit(), CancellationToken.None);

        Assert.Equal("SOLO", Assert.Single(rows).SynmaxId);
        Assert.Equal(1, handler.CallCount);
    }
}
