using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DataLoader.MISO.Tests;

// =============================================================================
// Test doubles. No network, no database: HTTP, the core.Param store, core.LoadLog,
// the SQL sinks and ILogger are in-memory fakes, so every test is offline.
// =============================================================================

/// <summary>One request as it reached the (stub) wire, with the key header captured at send time.</summary>
internal sealed record SentRequest(Uri Uri, string? KeyHeader, int Index);

/// <summary>Stub primary handler with an async responder; records every request URI and key header.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _responder;
    private int _count;

    public ConcurrentQueue<SentRequest> Requests { get; } = new();
    public int Count => Volatile.Read(ref _count);

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        : this((r, i, _) => Task.FromResult(responder(r, i))) { }

    public static StubHttpMessageHandler Always(HttpStatusCode status, string body = "") => new((_, _) => Json(status, body));

    /// <summary>Scripted responses in order; the last repeats.</summary>
    public static StubHttpMessageHandler Sequence(params (HttpStatusCode Status, string Body)[] responses) =>
        new((_, i) =>
        {
            var (s, b) = responses[Math.Min(i, responses.Length - 1)];
            return Json(s, b);
        });

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _count) - 1;
        string? key = request.Headers.TryGetValues(MisoApiClient.KeyHeader, out var v) ? string.Join(",", v) : null;
        Requests.Enqueue(new SentRequest(request.RequestUri!, key, index));
        cancellationToken.ThrowIfCancellationRequested();
        return await _responder(request, index, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Captures every log line (level + rendered message + structured values + exception) across categories.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public sealed record Entry(string Category, LogLevel Level, string Message, string? Exception);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public IEnumerable<Entry> OfLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    public IEnumerable<string> AllText() => Entries.SelectMany(e => new[] { e.Category, e.Message, e.Exception ?? string.Empty });

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerFactory _owner;
        private readonly string _category;

        public CapturingLogger(CapturingLoggerFactory owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> kvs
                ? string.Join(" ", kvs.Select(kv => $"{kv.Key}={kv.Value}"))
                : string.Empty;
            _owner.Entries.Enqueue(new Entry(_category, logLevel, formatter(state, exception) + " || " + structured, exception?.ToString()));
        }
    }
}

/// <summary>Scripted core.Param store. Counts calls.</summary>
internal sealed class FakeParamStore : ISeeDbParamStore
{
    private readonly Func<string, string, string?> _get;
    private int _calls;

    public FakeParamStore(Func<string, string, string?> get) => _get = get;

    public static FakeParamStore Returning(string? value) => new((_, _) => value);

    public int Calls => Volatile.Read(ref _calls);
    public List<(string Loader, string Param)> Asked { get; } = new();

    public string? GetParam(string loaderName, string paramName)
    {
        Interlocked.Increment(ref _calls);
        lock (Asked) Asked.Add((loaderName, paramName));
        return _get(loaderName, paramName);
    }
}

/// <summary>In-memory core.LoadLog: never skips, records every key, success and failure text.</summary>
internal sealed class FakeLoadLog : ILoadLogRepository
{
    private long _id;

    public ConcurrentQueue<string> BegunKeys { get; } = new();
    public ConcurrentQueue<(string Key, int Records)> Successes { get; } = new();
    public ConcurrentQueue<(string Key, string Error)> Failures { get; } = new();

    public Task<LoadLogHandle?> BeginAsync(string loaderId, Guid runId, string workUnitKey, string workUnitDisplay, CancellationToken cancellationToken)
    {
        BegunKeys.Enqueue(workUnitKey);
        return Task.FromResult<LoadLogHandle?>(new LoadLogHandle
        {
            LoadLogId = Interlocked.Increment(ref _id),
            LoaderId = loaderId,
            WorkUnitKey = workUnitKey,
            StartedAtUtc = DateTime.UtcNow,
        });
    }

    public Task CompleteSuccessAsync(LoadLogHandle handle, int recordsProcessed, CancellationToken cancellationToken)
    {
        Successes.Enqueue((handle.WorkUnitKey, recordsProcessed));
        return Task.CompletedTask;
    }

    public Task CompleteFailureAsync(LoadLogHandle handle, string errorMessage, CancellationToken cancellationToken)
    {
        Failures.Enqueue((handle.WorkUnitKey, errorMessage));
        return Task.CompletedTask;
    }

    public Task BeginRunAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CompleteRunAsync(Guid runId, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Records every write; never touches SQL.</summary>
internal sealed class FakeSink<TRow> : ISink<TRow>
{
    public ConcurrentQueue<IReadOnlyList<TRow>> Writes { get; } = new();

    public IEnumerable<TRow> AllRows => Writes.SelectMany(w => w);

    public Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        Writes.Enqueue(rows);
        return Task.FromResult(rows.Count);
    }
}

/// <summary>Scripted <see cref="IMisoPageSource"/>: the responder gets the 1-based page number.</summary>
internal sealed class FakePageSource : IMisoPageSource
{
    private readonly Func<int, MisoPageResult> _responder;

    public FakePageSource(Func<int, MisoPageResult> responder) => _responder = responder;

    public List<int> Pages { get; } = new();

    public Task<MisoPageResult> GetPageAsync(MisoWorkUnit unit, int pageNumber, CancellationToken cancellationToken)
    {
        Pages.Add(pageNumber);
        return Task.FromResult(_responder(pageNumber));
    }

    /// <summary>Serves one envelope JSON (parsed by the real client parser) on page 1.</summary>
    public static FakePageSource Body(string envelopeJson) =>
        new(_ => MisoApiClient.ParseEnvelope(envelopeJson, "test"));

    public static FakePageSource Outcome(MisoPageOutcome outcome, string reason = "r") =>
        new(_ => MisoPageResult.NonData(outcome, reason));
}

/// <summary>Secret-sweep helpers.</summary>
internal static class Secrets
{
    public static IEnumerable<string> ExceptionText(Exception? ex)
    {
        if (ex is null) yield break;
        yield return ex.ToString();
        for (var e = ex; e is not null; e = e.InnerException)
            yield return e.Message;
    }

    public static void AssertAbsent(IEnumerable<string?> texts, params string[] secrets)
    {
        foreach (var text in texts)
        {
            if (text is null) continue;
            foreach (var secret in secrets)
                Xunit.Assert.False(text.Contains(secret, StringComparison.Ordinal),
                    $"Secret sentinel leaked into: {text.Replace(secret, "<<SENTINEL>>")}");
        }
    }
}
