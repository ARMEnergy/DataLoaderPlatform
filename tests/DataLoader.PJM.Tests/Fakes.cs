using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM.Tests;

// =============================================================================
// Test doubles. No network, no database: HTTP, the core.Param store, core.LoadLog,
// the SQL sinks, the clock and ILogger are all in-memory fakes, so every test is
// deterministic and offline (AGSI test posture).
// =============================================================================

/// <summary>One request as it reached the wire. The key header is captured at send time (the request is disposed after).</summary>
internal sealed record SentRequest(Uri Uri, string? KeyHeader, int Index);

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub with an async responder. Records every
/// request URI and the <c>Ocp-Apim-Subscription-Key</c> header value it carried.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _responder;
    private int _count;

    public ConcurrentQueue<SentRequest> Requests { get; } = new();
    public int Count => Volatile.Read(ref _count);

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder) => _responder = responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        : this((r, i) => Task.FromResult(responder(r, i))) { }

    public static StubHttpMessageHandler Always(HttpStatusCode status, string body = "") =>
        new((_, _) => Json(status, body));

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
        string? key = request.Headers.TryGetValues(PjmApiClient.KeyHeader, out var v) ? string.Join(",", v) : null;
        Requests.Enqueue(new SentRequest(request.RequestUri!, key, index));
        cancellationToken.ThrowIfCancellationRequested();
        return await _responder(request, index).ConfigureAwait(false);
    }
}

/// <summary>Captures every log line (level + rendered message + exception text) across all categories.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public sealed record Entry(string Category, LogLevel Level, string Message, string? Exception);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public IEnumerable<Entry> OfLevel(LogLevel level) => Entries.Where(e => e.Level == level);

    /// <summary>Every captured string (message + exception) for secret sweeps.</summary>
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

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Also render the structured state's values, so a secret passed as a template
            // argument that the formatter happened not to print would still be caught.
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
    public static FakeParamStore Throwing(string message = "store boom") => new((_, _) => throw new InvalidOperationException(message));

    public int Calls => Volatile.Read(ref _calls);

    public string? GetParam(string loaderName, string paramName)
    {
        Interlocked.Increment(ref _calls);
        return _get(loaderName, paramName);
    }
}

/// <summary>Fixed-key provider for client-level tests. Refresh behaviour is scriptable.</summary>
internal sealed class FakeKeyProvider : IPjmKeyProvider
{
    private PjmKey _current;
    private readonly List<string> _known = new();

    public FakeKeyProvider(string key, bool isOverride = false, string? refreshTo = null)
    {
        _current = new PjmKey(key, 1);
        _known.Add(key);
        IsOverride = isOverride;
        RefreshTo = refreshTo;
    }

    public string? RefreshTo { get; set; }
    public int RefreshCalls { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public PjmKey Current => _current;
    public PjmKeySource Source => IsOverride ? PjmKeySource.EnvOverride : PjmKeySource.PublicSettings;
    public bool IsOverride { get; }
    public IReadOnlyList<string> KnownKeys => _known.ToArray();

    public Task<PjmKey?> RefreshAsync(int seenVersion, CancellationToken cancellationToken)
    {
        RefreshCalls++;
        if (IsOverride || RefreshTo is null || RefreshCalls > 1) return Task.FromResult<PjmKey?>(null);
        _current = new PjmKey(RefreshTo, _current.Version + 1);
        _known.Add(RefreshTo);
        return Task.FromResult<PjmKey?>(_current);
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

    public Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        Writes.Enqueue(rows);
        return Task.FromResult(rows.Count);
    }
}

/// <summary>Fixed unit list provider.</summary>
internal sealed class FixedUnitProvider : IWorkUnitProvider<PjmDayWorkUnit>
{
    private readonly IReadOnlyList<PjmDayWorkUnit> _units;
    public FixedUnitProvider(params PjmDayWorkUnit[] units) => _units = units;
    public Task<IReadOnlyList<PjmDayWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context) => Task.FromResult(_units);
}

/// <summary>
/// Scripted <see cref="IPjmPageSource"/>: the responder gets the 1-based <c>startRow</c> and the
/// zero-based call index across the whole unit (so a test can script a re-read).
/// </summary>
internal sealed class FakePageSource : IPjmPageSource
{
    private readonly Func<int, int, PjmPage> _responder;
    private int _calls;

    public FakePageSource(Func<int, int, PjmPage> responder) => _responder = responder;

    public List<int> StartRows { get; } = new();
    public int Calls => _calls;

    public Task<PjmPage> GetPageAsync(PjmDayWorkUnit unit, int startRow, CancellationToken cancellationToken)
    {
        StartRows.Add(startRow);
        return Task.FromResult(_responder(startRow, _calls++));
    }

    /// <summary>Serves <paramref name="items"/> as consecutive pages of <paramref name="pageSize"/>, totalRows = items.Count.</summary>
    public static FakePageSource Paged(IReadOnlyList<JsonElement> items, int pageSize) =>
        new((startRow, _) => new PjmPage(items.Skip(startRow - 1).Take(pageSize).ToList(), items.Count));
}

/// <summary>
/// Manual <see cref="TimeProvider"/>: time moves only on <see cref="Advance"/>, which fires any
/// timer that has come due (enough for <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c>).
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    private TimeSpan _wallOffset;

    /// <summary>Wall clock = monotonic clock + an offset that only <see cref="JumpWallClock"/> moves.</summary>
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now + _wallOffset; }

    /// <summary>Steps the WALL clock only (NTP correction, DST misconfig); timers and timestamps are unaffected.</summary>
    public void JumpWallClock(TimeSpan by) { lock (_gate) _wallOffset += by; }

    // Task.Delay(TimeSpan, TimeProvider) re-checks elapsed time via the provider's TIMESTAMP and re-arms
    // for the remainder, so the timestamp must follow the fake clock too (the base uses Stopwatch).
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _now.UtcTicks; }

    public int PendingTimers { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            lock (owner._gate) owner._timers.Add(this);
        }

        public DateTimeOffset? Due { get; set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool fireNow = false;
            lock (_owner._gate)
            {
                if (dueTime == Timeout.InfiniteTimeSpan) Due = null;
                else
                {
                    Due = _owner._now + dueTime;
                    if (dueTime <= TimeSpan.Zero) { Due = null; fireNow = true; }
                }
            }
            if (fireNow) Fire();
            return true;
        }

        public void Dispose() { lock (_owner._gate) { Due = null; _owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Secret-sweep helpers.</summary>
internal static class Secrets
{
    /// <summary>Message + every inner exception message + ToString (stack text) of an exception chain.</summary>
    public static IEnumerable<string> ExceptionText(Exception? ex)
    {
        if (ex is null) yield break;
        yield return ex.ToString();
        for (var e = ex; e is not null; e = e.InnerException)
            yield return e.Message;
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions)
                foreach (var t in ExceptionText(inner)) yield return t;
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
