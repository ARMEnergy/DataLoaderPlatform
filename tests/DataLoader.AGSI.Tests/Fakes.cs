using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DataLoader.AGSI.Tests;

// =============================================================================
// Test doubles. No network, no database. Everything the AGSI loader touches at
// run time (HTTP, the arm.FileLog upsert, the country reference provider, the
// ILogger) is substituted with an in-memory fake so the tests are deterministic,
// fast and offline. Mirrors the CWG/StormVista test posture.
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is
/// referenced). Records every request URI and its headers (so a test can prove the
/// endpoint-2 <c>x-key</c> reaches the wire but never a URL/log), and returns a
/// scripted status + JSON body, or observes cancellation exactly as a real
/// <see cref="HttpClient"/> would.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<Uri> Requests { get; } = new();
    public List<HttpRequestMessage> RequestMessages { get; } = new();

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>Always returns the given status + JSON body.</summary>
    public static StubHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        RequestMessages.Add(request);
        cancellationToken.ThrowIfCancellationRequested(); // observe cancellation like HttpClient
        return Task.FromResult(_responder(request));
    }

    public HttpClient NewClient() => new(this);
}

/// <summary>Records every <see cref="IAgsiFileLog.UpsertAsync"/> call and returns a fixed FileLogId.</summary>
internal sealed class FakeAgsiFileLog : IAgsiFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(AgsiFileContext File, string Status, int? HttpStatus, string RequestPath, int RowCount);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        AgsiFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        Calls.Add(new Call(file, status, httpStatus, requestPath, rowCount));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>A FileLog fake whose upsert throws — to prove a 'Failed' write never masks the original error.</summary>
internal sealed class ThrowingAgsiFileLog : IAgsiFileLog
{
    public Task<int> UpsertAsync(
        AgsiFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("filelog boom");
}

/// <summary>Serves a preassembled distinct (Id, Code) entity list with no DB access (the pipeline-1 → 2 hand-off).</summary>
internal sealed class FakeAgsiCountryProvider : IAgsiCountryProvider
{
    private readonly IReadOnlyList<AgsiCountry> _countries;

    /// <summary>Codes with synthetic sequential Ids (1..n) — the enumeration/key tests only assert on Code.</summary>
    public FakeAgsiCountryProvider(params string[] codes) =>
        _countries = codes.Select((c, i) => new AgsiCountry(i + 1, c)).ToList();

    /// <summary>
    /// Explicit (Id, Code) pairs — used to prove the storage provider stamps <c>EntityId = country.Id</c>
    /// by TRUE Id↔Code pairing, not by a positional/array-index coincidence (feed non-sequential Ids).
    /// </summary>
    public FakeAgsiCountryProvider(IEnumerable<AgsiCountry> countries) =>
        _countries = countries.ToList();

    public Task<IReadOnlyList<AgsiCountry>> GetCountriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_countries);
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
