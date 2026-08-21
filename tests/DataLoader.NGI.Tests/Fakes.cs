using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGI.Tests;

// =============================================================================
// Test doubles - no network, no database, no real clock. Everything the NGI loader
// touches at run time (HTTP, the arm.FileLog upsert, the ILogger) is substituted with
// an in-memory fake so every test is deterministic, fast, offline and repeatable.
// Mirrors the CWG/AGSI/IIR test posture.
//
// NOTE: no real credential appears anywhere in this project. The dummies are
// deliberately obvious - "user@example.test" / "dummy-password".
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is referenced).
/// Records every request URI + message (so a test can prove no credential ever rides an NGI URL)
/// and answers via a scripted responder that may inspect the request or the call ordinal. Observes
/// cancellation exactly as a real <see cref="HttpClient"/> would.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _calls;

    public List<Uri> Requests { get; } = new();
    public List<HttpRequestMessage> RequestMessages { get; } = new();

    /// <summary>Request bodies, captured as text INSIDE SendAsync (the content is disposed later).</summary>
    public List<string> RequestBodies { get; } = new();

    public int Calls => _calls;

    private FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>Always returns the given status + JSON body.</summary>
    public static FakeHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new((_, _) => Json(status, body));

    /// <summary>Answers based on the request.</summary>
    public static FakeHttpMessageHandler ForRequest(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new((r, _) => responder(r));

    /// <summary>Answers based on the 1-based call ordinal - the scripted-sequence form.</summary>
    public static FakeHttpMessageHandler ByCall(Func<int, HttpResponseMessage> responder) =>
        new((_, n) => responder(n));

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
            RequestMessages.Add(request);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (RequestBodies) RequestBodies.Add(body);
        }

        cancellationToken.ThrowIfCancellationRequested(); // observe cancellation like HttpClient
        return _responder(request, n);
    }

    public HttpClient NewClient() => new(this);
}

/// <summary>
/// A one-client <see cref="IHttpClientFactory"/> handing <see cref="NgiTokenProvider"/> an
/// <see cref="HttpClient"/> over a supplied handler (the provider resolves its client by name).
/// </summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public List<string> NamesRequested { get; } = new();

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name)
    {
        lock (NamesRequested) NamesRequested.Add(name);
        return new HttpClient(_handler, disposeHandler: false);
    }
}

/// <summary>Records every <see cref="INgiFileLog.UpsertAsync"/> call and returns a fixed FileLogId.</summary>
internal sealed class FakeNgiFileLog : INgiFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(NgiFileContext File, string Status, int? HttpStatus, string RequestPath, int RowCount);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        NgiFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(file, status, httpStatus, requestPath, rowCount));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>
/// A FileLog fake whose upsert always throws - proves the best-effort Failed hub write can never
/// mask the ORIGINAL error the reader was reporting.
/// </summary>
internal sealed class ThrowingNgiFileLog : INgiFileLog
{
    public int Calls { get; private set; }

    public Task<int> UpsertAsync(
        NgiFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("filelog boom");
    }
}

/// <summary>A token provider double: hands out a starting token and a counted fresh one per re-mint.</summary>
internal sealed class FakeNgiTokenProvider : INgiTokenProvider
{
    private string _current = "tok0";

    public int GetCalls { get; private set; }
    public int RefreshCalls { get; private set; }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        GetCalls++;
        return Task.FromResult(_current);
    }

    public Task<string> RefreshTokenAsync(string staleToken, CancellationToken cancellationToken)
    {
        RefreshCalls++;
        _current = "tok" + RefreshCalls; // tok1, tok2, ...
        return Task.FromResult(_current);
    }
}

/// <summary>Captures log entries (level + rendered message) so tests can assert logging behaviour.</summary>
internal sealed class ListLogger : ILogger
{
    public sealed record Entry(LogLevel Level, string Message);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public IEnumerable<Entry> OfLevel(LogLevel level) => Entries.Where(e => e.Level == level);
    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue(new Entry(logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception)));
}

/// <summary>An <see cref="ILoggerProvider"/> fanning every category out to one shared <see cref="ListLogger"/>.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    public ListLogger Logger { get; } = new();
    public ILogger CreateLogger(string categoryName) => Logger;
    public void Dispose() { }
}

/// <summary>An <see cref="ILogger{T}"/> adapter over a shared <see cref="ListLogger"/>.</summary>
internal sealed class TypedListLogger<T> : ILogger<T>
{
    public ListLogger Inner { get; }
    public TypedListLogger(ListLogger inner) => Inner = inner;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>Small shared helpers.</summary>
internal static class Js
{
    /// <summary>Parses one JSON literal into a detached (Clone()d) element usable after the doc is disposed.</summary>
    public static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
