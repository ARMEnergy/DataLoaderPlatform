using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.IIR.Tests;

// =============================================================================
// Test doubles — no network, no database, no clock. Everything the IIR loader
// touches at run time (HTTP, the arm.FileLog upsert, the ILogger) is substituted
// with an in-memory fake so every test is deterministic, fast and offline.
// Mirrors the CWG/AGSI/IHSPointLogic test posture.
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is referenced).
/// Records every request URI + message (so a test can assert the pager's offset/limit join and that
/// a secret never rides the wire) and answers via a scripted responder that can look at the request
/// (e.g. branch on <c>offset</c>). Observes cancellation exactly as a real <see cref="HttpClient"/> would.
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

    /// <summary>Answers based on the request (typically the <c>offset</c> query value).</summary>
    public static FakeHttpMessageHandler ForRequest(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(responder);

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Extracts an integer query value (manual parse, no System.Web).</summary>
    public static int QueryIntOf(HttpRequestMessage request, string key)
    {
        var query = request.RequestUri!.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out var n))
                return n;
        }
        return -1;
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

/// <summary>
/// A one-client <see cref="IHttpClientFactory"/> that hands the token provider an <see cref="HttpClient"/>
/// backed by a supplied inner handler (the token provider resolves its client by name).
/// </summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>Records every <see cref="IIirFileLog.UpsertAsync"/> call and returns a fixed FileLogId.</summary>
internal sealed class FakeIirFileLog : IIirFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(IirFileContext File, string Status, int? HttpStatus, string RequestPath, int RowCount);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        IirFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(file, status, httpStatus, requestPath, rowCount));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>A FileLog fake whose upsert throws — proves a best-effort 'Failed' write never masks the original error.</summary>
internal sealed class ThrowingIirFileLog : IIirFileLog
{
    public Task<int> UpsertAsync(
        IirFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("filelog boom");
}

/// <summary>
/// Captures the STEP-1 id-catalog census the reader side-writes (design §4.4). Records each
/// <see cref="ISink{T}.WriteAsync"/> call so a test can assert the census was persisted exactly once,
/// with one row per discovered id (EntityId + carry-forward lat/long + the Central RunDate).
/// </summary>
internal sealed class FakeSummarySink : ISink<IirSummaryRow>
{
    public int WriteCalls { get; private set; }
    public List<IReadOnlyList<IirSummaryRow>> Batches { get; } = new();
    public List<IirSummaryRow> Rows { get; } = new();

    public Task<int> WriteAsync(IReadOnlyList<IirSummaryRow> rows, CancellationToken cancellationToken)
    {
        WriteCalls++;
        Batches.Add(rows);
        Rows.AddRange(rows);
        return Task.FromResult(rows.Count);
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

/// <summary>An <see cref="ILoggerProvider"/> that fans every logger out to one shared <see cref="ListLogger"/>.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    public ListLogger Logger { get; } = new();
    public ILogger CreateLogger(string categoryName) => Logger;
    public void Dispose() { }
}

/// <summary>Small helpers shared across the test classes.</summary>
internal static class Json
{
    /// <summary>Parses one JSON literal to a detached (Clone()d) element usable after the doc is disposed.</summary>
    public static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
