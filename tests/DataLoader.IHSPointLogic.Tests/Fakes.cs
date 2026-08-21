using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataLoader.IHSPointLogic.Tests;

// =============================================================================
// Test doubles — no network, no database, no clock. Everything the IHSPointLogic
// loader touches at run time (HTTP, the arm.FileLog upsert, the reference id
// lists, the ILogger) is substituted with an in-memory fake so every test is
// deterministic, fast and offline. Mirrors the CWG/AGSI/StormVista test posture.
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is referenced).
/// Records every request URI + message (so a test can assert the pager's page-index join and
/// that a secret never rides the URL) and answers via a scripted responder that can look at the
/// request (e.g. branch on <c>pageIndex</c>). Observes cancellation exactly as a real
/// <see cref="HttpClient"/> would.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> Requests { get; } = new();
    public List<HttpRequestMessage> RequestMessages { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>Always returns the given status + JSON body.</summary>
    public static FakeHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new(_ => Json(status, body));

    /// <summary>Answers based on the request (typically the <c>pageIndex</c> query value).</summary>
    public static FakeHttpMessageHandler ForRequest(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(responder);

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Extracts the 0-based <c>pageIndex</c> query value from a request URI (manual parse, no System.Web).</summary>
    public static int PageIndexOf(HttpRequestMessage request)
    {
        var query = request.RequestUri!.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "pageIndex" && int.TryParse(kv[1], out var n))
                return n;
        }
        return 0;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        RequestMessages.Add(request);
        cancellationToken.ThrowIfCancellationRequested(); // observe cancellation like HttpClient
        return Task.FromResult(_responder(request));
    }

    public HttpClient NewClient() => new(this);
}

/// <summary>Records every <see cref="IPlFileLog.UpsertAsync"/> call and returns a fixed FileLogId.</summary>
internal sealed class FakePlFileLog : IPlFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(PlFileContext File, string Status, int? HttpStatus, string RequestPath, int RowCount);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        PlFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(file, status, httpStatus, requestPath, rowCount));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>A FileLog fake whose upsert throws — proves a best-effort 'Failed' write never masks the original error.</summary>
internal sealed class ThrowingPlFileLog : IPlFileLog
{
    public Task<int> UpsertAsync(
        PlFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("filelog boom");
}

/// <summary>
/// A point reference-provider fake that serves a preassembled <see cref="PlPointRef"/> list (each carrying
/// the nullable <c>MaxDateQueued</c> watermark that drives the PointVolume incremental-backfill batching)
/// with no DB access, counting how many times it was asked (to assert the batched-fact provider reads the
/// reference exactly once). The bare-<c>int</c> overload maps every id to a NULL watermark (never queued →
/// backfills from the default), preserving the pre-Section-C call sites.
/// </summary>
internal sealed class CountingPointProvider : IPlPointProvider
{
    private readonly IReadOnlyList<PlPointRef> _points;
    private int _calls;

    public CountingPointProvider(IEnumerable<int> ids)
        : this(ids.Select(id => new PlPointRef(id, null))) { }

    public CountingPointProvider(IEnumerable<PlPointRef> points) => _points = points.ToList();

    public int Calls => _calls;

    public Task<IReadOnlyList<PlPointRef>> GetPointsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_points);
    }
}

/// <summary>A counting id-list delegate source (the C/D providers take a <c>Func</c>, not an interface).</summary>
internal sealed class CountingIdList
{
    private readonly IReadOnlyList<int> _ids;
    private int _calls;

    public CountingIdList(params int[] ids) => _ids = ids.ToList();

    public int Calls => _calls;

    public Task<IReadOnlyList<int>> GetAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_ids);
    }
}

/// <summary>Captures log entries (level + rendered message) so tests can assert logging behaviour.</summary>
internal sealed class ListLogger : ILogger
{
    public sealed record Entry(LogLevel Level, string Message);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public IEnumerable<Entry> OfLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
}

/// <summary>
/// A typed <see cref="ILogger{T}"/> that captures entries (wraps a <see cref="ListLogger"/>) so a class
/// requiring a generic logger — e.g. <c>SqlPlEndpointSchedule</c> (<c>ILogger&lt;SqlPlEndpointSchedule&gt;</c>) —
/// can have its fail-open WARN lines asserted.
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ListLogger Inner { get; } = new();

    public IEnumerable<ListLogger.Entry> OfLevel(LogLevel level) => Inner.OfLevel(level);

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        ((ILogger)Inner).Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>Small helpers shared across the test classes.</summary>
internal static class Json
{
    /// <summary>Parses one JSON object literal to a detached (Clone()d) element usable after the doc is disposed.</summary>
    public static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
