using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.ModernCommodities.Tests;

// =============================================================================
// Test doubles - no network, no database, no real clock. Everything the ModCom
// loader touches at run time (HTTP, the arm.FileLog upsert, core.LoadLog, the
// sink, the ILogger) is substituted with an in-memory fake, so every test is
// deterministic, fast, offline and repeatable. Mirrors the NGI/CWG/AGSI/IIR
// test posture.
//
// NOTE: no real credential appears anywhere in this project, and none may. The
// ModCom credential is HTTP Basic in a request HEADER; the dummies below are
// deliberately obvious ("modcom-test-user" / "dummy-password").
// =============================================================================

/// <summary>
/// Hand-written <see cref="HttpMessageHandler"/> stub (no mocking library is referenced).
/// Records every request URI and message - so a test can prove no ModCom URL ever carries a
/// credential - and answers via a scripted responder that may inspect the request or the 1-based
/// call ordinal. Observes cancellation exactly as a real <see cref="HttpClient"/> would.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _calls;

    public List<Uri> Requests { get; } = new();
    public List<HttpRequestMessage> RequestMessages { get; } = new();
    public int Calls => _calls;

    private FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>Always answers with the given status and CSV body.</summary>
    public static FakeHttpMessageHandler Respond(HttpStatusCode status, string body = "") =>
        new((_, _) => Csv(status, body));

    /// <summary>Answers based on the request (e.g. on the query string).</summary>
    public static FakeHttpMessageHandler ForRequest(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new((r, _) => responder(r));

    /// <summary>Answers based on the 1-based call ordinal - the scripted-sequence form.</summary>
    public static FakeHttpMessageHandler ByCall(Func<int, HttpResponseMessage> responder) =>
        new((_, n) => responder(n));

    /// <summary>A <c>text/csv</c> response, which is what the live API sends.</summary>
    public static HttpResponseMessage Csv(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/csv") };

    /// <summary>A response carrying an explicit content type (for the "not text/csv" warning path).</summary>
    public static HttpResponseMessage WithContentType(HttpStatusCode status, string body, string mediaType) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
            RequestMessages.Add(request);
        }

        cancellationToken.ThrowIfCancellationRequested(); // observe cancellation like HttpClient
        return Task.FromResult(_responder(request, n));
    }

    public HttpClient NewClient() => new(this);
}

/// <summary>Records every <see cref="IModComFileLog.UpsertAsync"/> call and returns a fixed FileLogId.</summary>
internal sealed class FakeModComFileLog : IModComFileLog
{
    public int FileLogIdToReturn { get; set; } = 4242;

    public sealed record Call(
        ModComFileContext File, string Status, int? HttpStatus, int RowCount, int DroppedRowCount, string? ErrorMessage);

    public List<Call> Calls { get; } = new();

    public Task<int> UpsertAsync(
        ModComFileContext file, string status, int? httpStatus, int rowCount, int droppedRowCount,
        string? errorMessage, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add(new Call(file, status, httpStatus, rowCount, droppedRowCount, errorMessage));
        return Task.FromResult(FileLogIdToReturn);
    }
}

/// <summary>
/// A hub fake whose upsert always throws - proves the best-effort <c>Failed</c> hub write can never
/// mask the ORIGINAL exception the reader was reporting.
/// </summary>
internal sealed class ThrowingModComFileLog : IModComFileLog
{
    public int Calls { get; private set; }

    public Task<int> UpsertAsync(
        ModComFileContext file, string status, int? httpStatus, int rowCount, int droppedRowCount,
        string? errorMessage, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("filelog boom");
    }
}

/// <summary>
/// A scripted <see cref="ILoadLogRepository"/> - the whole resumability rule lives here.
///
/// <para><c>BeginAsync</c> returns <c>null</c> (= SKIP) <b>only</b> for a key listed in
/// <see cref="AlreadySucceeded"/>. A key that is missing, or whose previous attempt failed, gets a
/// handle and IS processed. Every call is recorded so a test can assert exactly which keys were
/// begun, completed successfully (with the record count), or failed.</para>
///
/// <para>That is faithful to the platform proc it stands in for: <c>core.usp_BeginLoadLog</c>
/// short-circuits (<c>@LoadLogId = -1</c>, which <c>SqlLoadLogRepository</c> turns into
/// <c>null</c>) <b>only</b> when a row exists for the same <c>(LoaderId, WorkUnitKey)</c> with
/// <c>IsComplete = 1</c>, and <c>core.usp_CompleteLoadLog</c> closes a FAILED attempt with
/// <c>IsComplete = 0</c> - so a failed or half-finished unit is retried, by design.</para>
/// </summary>
internal sealed class ScriptedLoadLog : ILoadLogRepository
{
    private long _nextId;

    /// <summary>Work-unit keys the log already records as SUCCESSFULLY COMPLETED - these, and only these, skip.</summary>
    public HashSet<string> AlreadySucceeded { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys with a previous FAILED / incomplete attempt. Recorded for the reader of the test only:
    /// they deliberately do NOT skip, which is the rule under test.
    /// </summary>
    public HashSet<string> PreviouslyFailed { get; } = new(StringComparer.Ordinal);

    public ConcurrentBag<string> Begun { get; } = new();
    public ConcurrentBag<string> Skipped { get; } = new();
    public ConcurrentBag<(string Key, int Records)> Succeeded { get; } = new();
    public ConcurrentBag<(string Key, string Error)> Failed { get; } = new();

    public Task<LoadLogHandle?> BeginAsync(
        string loaderId, Guid runId, string workUnitKey, string workUnitDisplay, CancellationToken cancellationToken)
    {
        if (AlreadySucceeded.Contains(workUnitKey))
        {
            Skipped.Add(workUnitKey);
            return Task.FromResult<LoadLogHandle?>(null);   // the ONLY skip condition
        }

        Begun.Add(workUnitKey);
        return Task.FromResult<LoadLogHandle?>(new LoadLogHandle
        {
            LoadLogId = Interlocked.Increment(ref _nextId),
            LoaderId = loaderId,
            WorkUnitKey = workUnitKey,
            StartedAtUtc = DateTime.UtcNow
        });
    }

    public Task CompleteSuccessAsync(LoadLogHandle handle, int recordsProcessed, CancellationToken cancellationToken)
    {
        Succeeded.Add((handle.WorkUnitKey, recordsProcessed));
        return Task.CompletedTask;
    }

    public Task CompleteFailureAsync(LoadLogHandle handle, string errorMessage, CancellationToken cancellationToken)
    {
        Failed.Add((handle.WorkUnitKey, errorMessage));
        return Task.CompletedTask;
    }

    public Task BeginRunAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CompleteRunAsync(Guid runId, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A work-unit provider returning a fixed list - used where the units, not their derivation, are the subject.</summary>
internal sealed class FixedWorkUnitProvider : IWorkUnitProvider<ModComWorkUnit>
{
    private readonly IReadOnlyList<ModComWorkUnit> _units;
    public FixedWorkUnitProvider(params ModComWorkUnit[] units) => _units = units;
    public Task<IReadOnlyList<ModComWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context) => Task.FromResult(_units);
}

/// <summary>A source-reader double: scripted rows per work unit, or a throw for a unit scripted to fail.</summary>
internal sealed class ScriptedSourceReader<TRow> : ISourceReader<ModComWorkUnit, TRow>
{
    private readonly Func<ModComWorkUnit, IReadOnlyList<TRow>> _rows;

    public ScriptedSourceReader(Func<ModComWorkUnit, IReadOnlyList<TRow>> rows) => _rows = rows;

    public ConcurrentBag<string> ReadKeys { get; } = new();

    public Task<IReadOnlyList<TRow>> ReadAsync(ModComWorkUnit unit, CancellationToken cancellationToken)
    {
        ReadKeys.Add(unit.Key);
        return Task.FromResult(_rows(unit));
    }
}

/// <summary>An in-memory sink recording every batch it was handed.</summary>
internal sealed class RecordingSink<TRow> : ISink<TRow>
{
    public ConcurrentBag<IReadOnlyList<TRow>> Batches { get; } = new();
    public int TotalRows => Batches.Sum(b => b.Count);

    public Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        Batches.Add(rows);
        return Task.FromResult(rows.Count);
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
