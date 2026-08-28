using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the HTTP status matrix (design §5.4). This is the check the
/// <c>resume-key-and-status-matrix-check</c> skill exists for, and it is load-bearing for one
/// reason above all:
///
/// <para><b>An empty <c>200 []</c> must be a SUCCESS, and a <c>400</c> must be a FAILURE.</b> Both
/// are "we got no rows". Collapsing them either way is silently catastrophic:</para>
/// <list type="bullet">
///   <item>Treating an empty 200 as an error would fail ~1 unit in 3 on every run (weekends,
///     holidays, the not-yet-published current date) and drown every real signal.</item>
///   <item>Treating a 400 as an empty read would record a malformed request — a LOADER BUG, e.g. a
///     mis-formatted <c>dateFrom</c> — as a permanently-done work unit, losing that date forever with
///     no signal at all.</item>
/// </list>
/// </summary>
public class StatusMatrixTests
{
    // ---- the classifier itself -----------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public void Classify_TwoXx_Parses(HttpStatusCode status) =>
        Assert.Equal(EvoStatusAction.Parse, EvoStatus.Classify(status));

    [Fact]
    public void Classify_NotFound_IsAnEmptyRead_NotAFailure() =>
        Assert.Equal(EvoStatusAction.NotAvailable, EvoStatus.Classify(HttpStatusCode.NotFound));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]           // malformed dateFrom / bad limit / future date
    [InlineData(HttpStatusCode.Unauthorized)]         // missing or blank api key
    [InlineData(HttpStatusCode.Forbidden)]            // key not entitled
    [InlineData(HttpStatusCode.TooManyRequests)]      // Polly already exhausted its retries
    [InlineData(HttpStatusCode.InternalServerError)]  // ditto; also the unknown-`field` signature
    [InlineData(HttpStatusCode.BadGateway)]
    public void Classify_EverythingElse_Throws(HttpStatusCode status) =>
        Assert.Equal(EvoStatusAction.Throw, EvoStatus.Classify(status));

    // ---- the diagnostic messages ---------------------------------------------------------------

    [Fact]
    public void Describe_400_NamesTheLikelyCauses_AndCallsItALoaderBug()
    {
        var msg = EvoStatus.Describe("/v1/market-data/history?dateFrom=2026-08-24", 400,
            "{\"message\":\"\\\"dateFrom\\\" invalid format. Should be yyyy-MM-dd\"}");

        Assert.Contains("yyyy-MM-dd", msg);
        Assert.Contains("LOADER BUG", msg);
        Assert.Contains("limit must be 1..10000", msg);
    }

    [Fact]
    public void Describe_401_SaysTheKeyIsNotHttpBasic()
    {
        // The single most likely misconfiguration, given the loader request called it "Basic
        // authentication". The message must steer the operator away from base64-encoding the key.
        var msg = EvoStatus.Describe("/v1/market-data/history", 401, "{\"message\":\"Unauthorized\"}");

        Assert.Contains("ApiKey", msg);
        Assert.Contains("NOT as HTTP Basic", msg);
    }

    [Fact]
    public void Describe_500_PointsAtTheFieldProjection()
    {
        // This vendor reports an unknown `field` name as a 500, not a 400. Without this hint the next
        // operator chases a phantom outage.
        var msg = EvoStatus.Describe("/v1/market-data/history", 500, "{\"message\":\"Something went wrong.\"}");

        Assert.Contains("field", msg);
        Assert.Contains("EvoRequestFields", msg);
    }

    [Fact]
    public void Describe_BoundsAnOverlongBody()
    {
        // A proxy can return a full HTML error page; the log line must stay readable.
        var msg = EvoStatus.Describe("/p", 502, new string('x', 5000));
        Assert.True(msg.Length < 700, $"message was {msg.Length} chars; the body snippet is not bounded");
    }

    [Fact]
    public void Describe_HandlesAnEmptyBody()
    {
        var msg = EvoStatus.Describe("/p", 502, null);
        Assert.Contains("(empty body)", msg);
    }

    // ---- end-to-end through the reader ---------------------------------------------------------

    [Fact]
    public async Task Ok_WithRows_IsSuccess()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.LiveSlice);

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(EvoFileStatus.Success, result.FileLog.Single.Status);
        Assert.Equal(200, result.FileLog.Single.HttpStatus);
        Assert.Equal(5, result.FileLog.Single.RowCount);
        Assert.Null(result.FileLog.Single.ErrorMessage);
    }

    /// <summary>
    /// *** THE MOST IMPORTANT TEST IN THIS FILE. ***
    /// A weekend/holiday returns a literal <c>[]</c>. The unit must SUCCEED with zero rows and a
    /// <c>NotAvailable</c> hub row — never throw, never warn.
    /// </summary>
    [Fact]
    public async Task Ok_WithEmptyArray_IsASuccessfulEmptyRead()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.Empty);

        Assert.Empty(result.Rows);
        Assert.Equal(EvoFileStatus.NotAvailable, result.FileLog.Single.Status);
        Assert.Equal(200, result.FileLog.Single.HttpStatus);
        Assert.Equal(0, result.FileLog.Single.RowCount);
        Assert.Null(result.FileLog.Single.ErrorMessage);
    }

    [Fact]
    public async Task Ok_WithEmptyArray_DoesNotWarn()
    {
        // A routine non-publishing day must be quiet. If it warned, a normal run would emit ~10
        // warnings and the log would be untrustworthy.
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.Empty);
        Assert.Empty(result.Log.OfLevel(LogLevel.Warning));
        Assert.Empty(result.Log.OfLevel(LogLevel.Error));
    }

    [Fact]
    public async Task NotFound_IsAnEmptyReadButWarns()
    {
        // Never observed on this endpoint, so it is treated as empty (per the matrix) AND warned about,
        // because it most likely means the path or API version moved.
        var result = await ReaderHarness.RunAsync(HttpStatusCode.NotFound, "");

        Assert.Empty(result.Rows);
        Assert.Equal(EvoFileStatus.NotAvailable, result.FileLog.Single.Status);
        Assert.Contains(result.Log.Warnings, m => m.Contains("unexpected HTTP 404"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HardStatuses_Throw_AndWriteAFailedHubRow(HttpStatusCode status)
    {
        var (reader, fileLog, _) = ReaderHarness.Reader(status, "{\"message\":\"nope\"}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Equal(EvoFileStatus.Failed, fileLog.Single.Status);
        Assert.Equal((int)status, fileLog.Single.HttpStatus);
        Assert.Equal(0, fileLog.Single.RowCount);
        Assert.False(string.IsNullOrWhiteSpace(fileLog.Single.ErrorMessage));
    }

    [Fact]
    public async Task NonArrayBody_IsShapeDrift_AndThrows()
    {
        // The documented and observed shape is a bare array. An envelope would be a breaking vendor
        // change and must fail LOUDLY rather than yield a silent zero-row success.
        var (reader, fileLog, _) = ReaderHarness.Reader(
            HttpStatusCode.OK, "{\"PagingInfo\":{\"page_count\":1},\"Data\":[]}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Contains("shape drift", ex.Message);
        Assert.Contains("ARRAY", ex.Message);
        Assert.Equal(EvoFileStatus.Failed, fileLog.Single.Status);
    }

    [Fact]
    public async Task EmptyBody_IsShapeDrift_AndThrowsWithAReadableMessage()
    {
        // Without the explicit guard this surfaces as JsonException's opaque "The input does not
        // contain any JSON tokens", which reads like a loader bug rather than a vendor one.
        var (reader, _, _) = ReaderHarness.Reader(HttpStatusCode.OK, "   ");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Contains("shape drift", ex.Message);
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public async Task AFailingHubWrite_NeverMasksTheOriginalError()
    {
        // The Failed hub write is best-effort. If it throws too, the ORIGINAL exception is the one
        // that must propagate — otherwise the real cause is lost behind a bookkeeping error.
        var hub = new ThrowingEvoFileLog();
        var (reader, log) = ReaderHarness.ReaderWith(hub, HttpStatusCode.Forbidden, "{\"message\":\"denied\"}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None));

        Assert.Contains("403", ex.Message);                        // the original failure survived
        Assert.True(hub.Calls > 0);                                // the hub write was attempted
        Assert.Contains(log.OfLevel(LogLevel.Error).Select(e => e.Message),
                        m => m.Contains("could not write the Failed"));
    }

    [Fact]
    public async Task Cancellation_DoesNotWriteAHubRow()
    {
        // A spent cancellation token cannot be used for the hub write, and a cancelled run must
        // surface as cancellation rather than as a Failed outcome.
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.LiveSlice);
        var fileLog = new FakeEvoFileLog();
        var reader = new EvoMarketDataSourceReader(
            handler.NewClient(), ReaderHarness.Settings(), fileLog, new ListLogger());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(ReaderHarness.Unit(), cts.Token));

        Assert.Empty(fileLog.Calls);
    }

    // ---- the hub row's identity ----------------------------------------------------------------

    [Fact]
    public async Task HubRow_IsKeyedOnTheRequestedBusinessDate()
    {
        var date = new DateOnly(2026, 8, 19);
        var result = await ReaderHarness.RunAsync(
            HttpStatusCode.OK, Samples.Empty, ReaderHarness.Unit(date));

        Assert.Equal(EvoEndpoints.MarketDataHistory, result.FileLog.Single.File.Endpoint);
        Assert.Equal(date, result.FileLog.Single.File.RepresentativeDate);
    }

    [Fact]
    public async Task ProducedRows_CarryTheReturnedFileLogId_AndAChecksum()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.LiveSlice);

        Assert.All(result.Rows, r => Assert.Equal(4242, r.FileLogId));
        Assert.All(result.Rows, r => Assert.NotEqual(0, r.Checksum));
    }
}
