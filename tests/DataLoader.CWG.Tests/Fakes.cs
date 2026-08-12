using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DataLoader.CWG.Tests;

// =============================================================================
// Test doubles. No network, no database. Everything the CWG loader touches at
// run time (HTTP, the arm.FileLog upsert, the ILogger) is substituted with an
// in-memory fake so the tests are deterministic, fast and offline. Mirrors the
// StormVista/Platts test posture.
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is
/// referenced). Records every request URI (which still carries the
/// <c>?apikey=</c>, so a test can prove the key reaches the wire but never the
/// sanitized log / FileLog path) and returns a scripted status + CSV body, or
/// observes cancellation exactly as a real <see cref="HttpClient"/> would.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> Requests { get; } = new();

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>Always returns the given status + CSV body.</summary>
    public static StubHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/csv")
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        cancellationToken.ThrowIfCancellationRequested(); // observe cancellation like HttpClient
        return Task.FromResult(_responder(request));
    }

    public HttpClient NewClient() => new(this);
}

/// <summary>Records every <see cref="ICwgFileLog.UpsertAsync"/> call and returns a fixed FileLogId.</summary>
internal sealed class FakeCwgFileLog : ICwgFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(CwgFileContext File, string Status, int? HttpStatus, string RequestPath, int RowCount);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        CwgFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(file, status, httpStatus, requestPath, rowCount));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>A FileLog fake whose upsert throws — to prove a 'Failed' write never masks the original error.</summary>
internal sealed class ThrowingCwgFileLog : ICwgFileLog
{
    public Task<int> UpsertAsync(
        CwgFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("filelog boom");
}

/// <summary>Captures log entries (level + rendered message) so tests can assert the Debug/Warning classification of drops.</summary>
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
