using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// <see cref="AgsiStorageSourceReader"/> — the transient-BODY retry added 2026-11-11.
/// AGSI reports two transient faults inside an HTTP 200, where the HTTP retry policy cannot
/// see them: <c>error:'Try/Catch error'</c>, and a body whose every record belongs to
/// another entity (the vendor answered a different request). Those, and only those, are
/// re-requested up to <see cref="AgsiSettings.TransientBodyRetryCount"/> extra times; any
/// other body error (a bad key's <c>access denied</c>) still fails at once. Offline: stub
/// HTTP, fake FileLog, capturing logger.
/// </summary>
public class StorageTransientRetryTests
{
    private const string ApiKey = "SECRET-XKEY-123"; // = the key echoed in Samples.StorageVendorFaultEchoingKey

    private static AgsiSettings Settings(int retryCount = 2, int delayMs = 0) => new()
    {
        BaseUrl = "https://agsi.gie.eu",
        ApiKey = ApiKey,
        TransientBodyRetryCount = retryCount,
        TransientBodyRetryDelayMs = delayMs
    };

    private static AgsiStorageWorkUnit Unit(string code = "de", DateOnly? date = null, int entityId = 7)
    {
        var d = date ?? new DateOnly(2026, 8, 13);
        return new AgsiStorageWorkUnit
        {
            EntityId = entityId,
            CountryCode = code,
            Date = d,
            RequestPath = $"/api?country={code}&date={d:yyyy-MM-dd}",
            KeyValue = "k"
        };
    }

    private static AgsiStorageWorkUnit AggregateUnit(string code) =>
        Unit(code, new DateOnly(2026, 9, 1), code == "eu" ? 6600 : 6609);

    private static AgsiStorageSourceReader Reader(
        StubHttpMessageHandler handler, IAgsiFileLog fileLog, AgsiSettings? settings = null, ILogger? logger = null) =>
        new(handler.NewClient(), settings ?? Settings(), fileLog, logger ?? new ListLogger());

    private static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

    // ================================================================ Try/Catch error

    [Fact]
    public async Task TryCatch_ThenSuccess_ReturnsRows_TwoRequests_OneSuccessRow_NoFailedRow()
    {
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageVendorFaultEchoingKey), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog { FileLogIdToReturn = 91 };
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(7, r.EntityId);
        Assert.Equal(91, r.FileLogId);
        Assert.Equal(121.1238m, r.GasInStorage);
        Assert.Equal(2, handler.Requests.Count);

        var call = Assert.Single(fileLog.Calls);             // the transient attempt wrote nothing
        Assert.Equal("Success", call.Status);
        Assert.Equal(1, call.RowCount);
        Assert.DoesNotContain(fileLog.Calls, c => c.Status == "Failed");
    }

    [Fact]
    public async Task TryCatch_EveryAttempt_ThrowsAfterOnePlusN_Requests_KeyFree_SingleFailedRow()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog, Settings(retryCount: 2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("Try/Catch error", ex.Message);
        Assert.Contains("after 3 attempt(s)", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.DoesNotContain("API key:", ex.Message);       // `message` stays suppressed

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task TryCatch_EveryRequestCarriesTheXKeyHeader_SameUri()
    {
        var handler = StubHttpMessageHandler.Sequence(
            Ok(Samples.StorageVendorFaultEchoingKey), Ok(Samples.StorageVendorFaultEchoingKey), Ok(Samples.StorageDe));
        var reader = Reader(handler, new FakeAgsiFileLog());

        await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(3, handler.RequestMessages.Count);
        Assert.All(handler.RequestMessages, m =>
        {
            Assert.True(m.Headers.TryGetValues("x-key", out var v));
            Assert.Equal(ApiKey, Assert.Single(v!));          // a fresh request per attempt, header re-added once
        });
        Assert.Single(handler.Requests.Distinct());
        Assert.All(handler.Requests, u => Assert.DoesNotContain(ApiKey, u.ToString()));
    }

    [Fact]
    public async Task TryCatch_RetryCountZero_OneRequest_FailsAtOnce()
    {
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageVendorFaultEchoingKey), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog, Settings(retryCount: 0));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Contains("after 1 attempt(s)", ex.Message);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task TryCatch_NegativeRetryCount_TreatedAsZero()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var reader = Reader(handler, new FakeAgsiFileLog(), Settings(retryCount: -5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("try/catch error")]
    [InlineData("TRY/CATCH ERROR")]
    [InlineData("  Try/Catch error  ")]
    public async Task TryCatch_MatchIsCaseInsensitiveAndTrimmed_IsRetried(string error)
    {
        var body = Samples.StorageVendorFaultEchoingKey.Replace("\"Try/Catch error\"", $"\"{error}\"");
        Assert.NotEqual(Samples.StorageVendorFaultEchoingKey, body); // the replace really happened
        var handler = StubHttpMessageHandler.Sequence(Ok(body), Ok(Samples.StorageDe));
        var reader = Reader(handler, new FakeAgsiFileLog());

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Try/Catch error: timeout")]   // not EXACTLY the transient text
    [InlineData("Try/Catch")]
    public async Task TryCatch_NotAnExactMatch_IsNotRetried(string error)
    {
        var body = Samples.StorageVendorFaultEchoingKey.Replace("\"Try/Catch error\"", $"\"{error}\"");
        var handler = StubHttpMessageHandler.Sequence(Ok(body), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("attempt(s)", ex.Message);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    // ================================================================ non-transient body errors

    [Fact]
    public async Task AccessDenied_OneRequestOnly_Throws_NotRetried()
    {
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageAccessDenied), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var logger = new ListLogger();
        var reader = Reader(handler, fileLog, logger: logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Contains("access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attempt(s)", ex.Message);
        Assert.Empty(logger.OfLevel(LogLevel.Warning));       // no "retrying" line
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
    }

    [Fact]
    public async Task KeyInErrorField_NotTransient_OneRequest_Redacted()
    {
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageVendorFaultKeyInErrorField), Ok(Samples.StorageDe));
        var reader = Reader(handler, new FakeAgsiFileLog());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task Http500_IsNotABodyRetry_OneRequestAtThisLayer()
    {
        // HTTP-status retries belong to the HttpClient policy, not this loop.
        var handler = StubHttpMessageHandler.Sequence((HttpStatusCode.InternalServerError, "boom"), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    // ================================================================ foreign-only body

    [Fact]
    public async Task ForeignOnly_EuNeBodyForDe_ThenCorrectBody_RowsStampedWithUnitEntityId()
    {
        // `?country=de` answered with the eu/ne aggregate body (observed 2026-11), then right.
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageAggregateEuAndNe), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("de", entityId: 7), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(7, r.EntityId);
        Assert.Equal(121.1238m, r.GasInStorage);              // DE's numbers, not EU's 741.9174
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Success", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task ForeignOnly_EveryAttempt_ThrowsNamingTheForeignCodes_NoRowEverStamped()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAggregateEuAndNe);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit("it", entityId: 42), CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("[eu, ne]", ex.Message);
        Assert.Contains("'it'", ex.Message);
        Assert.Contains("after 3 attempt(s)", ex.Message);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(0, call.RowCount);
    }

    [Theory]
    [InlineData("eu", 6600, 741.9174)]
    [InlineData("ne", 6609, 110.6828)]
    public async Task LegitAggregateRequest_EuNeBody_NotRetried_CorrectRowKept(string code, int entityId, double gis)
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAggregateEuAndNe);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(AggregateUnit(code), CancellationToken.None);

        Assert.Single(handler.Requests);
        var r = Assert.Single(rows);
        Assert.Equal(entityId, r.EntityId);
        Assert.Equal((decimal)gis, r.GasInStorage);
        Assert.Equal("Success", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task ClampedDate_RightEntityWrongGasDay_ZeroRows_NotAvailable_NotRetried()
    {
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageClampedToEarlierGasDay), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("at", new DateOnly(2026, 9, 27)), CancellationToken.None);

        Assert.Empty(rows);
        Assert.Single(handler.Requests);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task EmptyDataArray_StillNotAvailable_NotRetried()
    {
        var handler = StubHttpMessageHandler.Sequence(Ok(Samples.StorageEmptyData), Ok(Samples.StorageDe));
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        Assert.Single(handler.Requests);
        Assert.Equal("NotAvailable", Assert.Single(fileLog.Calls).Status);
    }

    // ================================================================ secrets in logs

    [Fact]
    public async Task EveryWarningDuringTryCatchRetries_IsFreeOfTheApiKey()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var logger = new ListLogger();
        var reader = Reader(handler, new FakeAgsiFileLog(), logger: logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Unit(), CancellationToken.None));

        var warnings = logger.OfLevel(LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);                      // one per retry
        Assert.Contains("attempt 2 of 3", warnings[0].Message);
        Assert.Contains("attempt 3 of 3", warnings[1].Message);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain(ApiKey, e.Message);
            Assert.DoesNotContain("API key:", e.Message);
        });
    }

    [Fact]
    public async Task ForeignCodeEchoingTheKey_IsRedactedInWarningsAndException()
    {
        // Defence in depth: the foreign-entity message quotes each record's `code`.
        var body = Samples.StorageSingleRecordForeignCode.Replace("\"code\":\"UA\"", $"\"code\":\"{ApiKey}\"");
        Assert.NotEqual(Samples.StorageSingleRecordForeignCode, body);
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, body);
        var logger = new ListLogger();
        var reader = Reader(handler, new FakeAgsiFileLog(), logger: logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.Contains("***", ex.Message);
        Assert.NotEmpty(logger.OfLevel(LogLevel.Warning));
        Assert.All(logger.Entries, e => Assert.DoesNotContain(ApiKey, e.Message));
    }

    // ================================================================ delay + cancellation

    [Fact]
    public async Task RetryDelay_IsLinearInAttempt()
    {
        // attempt 1 waits 1×delay, attempt 2 waits 2×delay: ≥ 3×60ms = 180ms in total.
        // Only a lower bound is asserted (Task.Delay is never meaningfully early).
        var handler = StubHttpMessageHandler.Sequence(
            Ok(Samples.StorageVendorFaultEchoingKey), Ok(Samples.StorageVendorFaultEchoingKey), Ok(Samples.StorageDe));
        var reader = Reader(handler, new FakeAgsiFileLog(), Settings(retryCount: 2, delayMs: 60));

        var sw = Stopwatch.StartNew();
        await reader.ReadAsync(Unit(), CancellationToken.None);
        sw.Stop();

        Assert.Equal(3, handler.Requests.Count);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"elapsed {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Cancels the token the moment the reader logs its "retrying" warning — i.e. just before Task.Delay.</summary>
    private sealed class CancelOnWarningLogger : ILogger
    {
        private readonly CancellationTokenSource _cts;
        public ListLogger Inner { get; } = new();
        public CancelOnWarningLogger(CancellationTokenSource cts) => _cts = cts;
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ((ILogger)Inner).Log(logLevel, eventId, state, exception, formatter);
            if (logLevel == LogLevel.Warning) _cts.Cancel();
        }
    }

    [Fact]
    public async Task CancelledDuringRetryDelay_ThrowsOperationCanceled_NoFailedRow_NoFurtherRequest()
    {
        using var cts = new CancellationTokenSource();
        var logger = new CancelOnWarningLogger(cts);
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var fileLog = new FakeAgsiFileLog();
        // 60s delay: if cancellation were not honoured the test would visibly hang.
        var reader = Reader(handler, fileLog, Settings(retryCount: 2, delayMs: 60_000), logger);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Unit(), cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"elapsed {sw.Elapsed}");
        Assert.Single(handler.Requests);                      // the retry request was never sent
        Assert.Empty(fileLog.Calls);                          // a cancel is re-thrown before the Failed upsert
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsOperationCanceled_NoFileLogRow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Unit(), cts.Token));

        Assert.Empty(fileLog.Calls);
    }
}
