using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// The shared HTTP status matrix (design §5.4), applied identically by both readers, driven entirely
/// by a fake handler.
///
/// <para><b>404 is the NORMAL MAJORITY outcome</b> of this monthly feed - over the default 60-day
/// window ~58 of 60 units legitimately 404 - so it is a successful, EMPTY read whose work unit
/// SUCCEEDS. <b>400 must THROW</b> (a malformed <c>issue_date</c> is a loader bug, not a data
/// condition). <b>200 with a non-object <c>data</c> must THROW</b> - a "tolerant" zero-row success
/// there is precisely the silent-corruption mode this loader exists to prevent.</para>
/// </summary>
public class StatusMatrixTests
{
    // ================================================== 404: success with zero rows

    [Fact]
    public async Task Http404_ZeroRows_UnitSucceeds_NotAvailable_NeverAWarning()
    {
        var result = await ReaderHarness.RunDatafeedAsync(
            HttpStatusCode.NotFound,
            """{"msg":"Datafeed not found. The date entered could be a weekend or holiday."}""");

        Assert.Empty(result.Rows);                       // nothing thrown -> the work unit SUCCEEDS

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);              // the REAL HTTP status reaches arm.FileLog
        Assert.Equal(0, call.RowCount);
        Assert.Equal(Samples.FixtureIssueDate, call.File.RepresentativeDate);

        // It must never look like a problem: no warning/error for the expected case.
        Assert.Empty(result.Log.OfLevel(LogLevel.Warning));
        Assert.Empty(result.Log.OfLevel(LogLevel.Error));
    }

    // ================================================== 400 and the other loud statuses

    [Fact]
    public async Task Http400_Throws_AndTheFileLogRecordsTheRealStatus()
    {
        var (reader, fileLog, _) = ReaderHarness.BidWeekReader(
            HttpStatusCode.BadRequest,
            """{"msg":"Incorrect date format for issue_date, should be YYYY-MM-DD"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        // The message names the cause so a 400 is never mistaken for a data condition.
        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("LOADER BUG", ex.Message);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(400, call.HttpStatus);              // the real HTTP status on the failure path
        Assert.Equal(0, call.RowCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    public async Task NonSuccessNon404_Throws_AndLogsFailedWithTheRealHttpStatus(HttpStatusCode status, int expected)
    {
        var (reader, fileLog, _) = ReaderHarness.BidWeekReader(status, "boom");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task Locations_NonSuccessNon404_Throws_AndLogsFailed(HttpStatusCode status, int expected)
    {
        var (reader, fileLog, _) = ReaderHarness.LocationsReader(status, "boom");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.LocationsUnit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
    }

    // ================================================== 200 + shape drift

    [Fact]
    public async Task Http200_DataIsAnArray_Throws_NotASilentEmptySuccess()
    {
        // THE load-bearing guard: `data` is a MAP keyed by point code, never an array. A tolerant
        // zero-row success here would report a clean, successful, EMPTY load.
        var (reader, fileLog, _) = ReaderHarness.BidWeekReader(
            HttpStatusCode.OK, Samples.Envelope("""[{"Point Code":"STXAGUAD"}]"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        Assert.Contains("shape drift", ex.Message);
        Assert.Contains("Array", ex.Message);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Theory]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task Http200_DataIsAnyNonObjectScalar_Throws(string dataJson)
    {
        var (reader, fileLog, _) = ReaderHarness.BidWeekReader(HttpStatusCode.OK, Samples.Envelope(dataJson));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task Http200_RootIsAnArray_Throws()
    {
        var (reader, _, _) = ReaderHarness.BidWeekReader(HttpStatusCode.OK, "[]");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        Assert.Contains("envelope shape drift", ex.Message);
    }

    [Fact]
    public async Task Http200_DataAbsent_IsToleratedAsNoPublication_ZeroRows_NotAvailable()
    {
        // Absent (as opposed to present-but-wrong-shape) is tolerated: NGI 404s when nothing published,
        // so this is unexpected but not corrupting.
        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, "{\"meta\":" + Samples.MetaBlock + "}");

        Assert.Empty(result.Rows);
        Assert.Equal("NotAvailable", Assert.Single(result.FileLog.Calls).Status);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("'data' is absent/empty"));
    }

    [Fact]
    public async Task Http200_DataEmptyObject_IsToleratedAsNoPublication()
    {
        var result = await ReaderHarness.RunDatafeedAsync(HttpStatusCode.OK, Samples.Envelope("{}"));

        Assert.Empty(result.Rows);
        Assert.Equal("NotAvailable", Assert.Single(result.FileLog.Calls).Status);
    }

    [Fact]
    public async Task Http200_MalformedJson_Throws_AndLogsFailed()
    {
        var (reader, fileLog, _) = ReaderHarness.BidWeekReader(HttpStatusCode.OK, "{not json");

        // JsonReaderException derives from JsonException - ThrowsAny, so the concrete STJ type is free
        // to change without breaking this.
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(200, call.HttpStatus);   // the transport really did return 200
    }

    // ================================================== 401: re-mint once, then propagate

    private static HttpClient AuthedClient(FakeNgiTokenProvider provider, params HttpResponseMessage[] scripted)
    {
        var queue = new Queue<HttpResponseMessage>(scripted);
        var inner = FakeHttpMessageHandler.ByCall(_ => queue.Count > 0
            ? queue.Dequeue()
            : FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Envelope("{}")));
        var handler = new NgiTokenAuthHandler(provider) { InnerHandler = inner };
        return new HttpClient(handler);
    }

    [Fact]
    public async Task Http401_ThenSuccess_ReMintsExactlyOnce_AndTheUnitSucceeds()
    {
        var provider = new FakeNgiTokenProvider();
        var client = AuthedClient(provider,
            FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"detail":"token expired"}"""),
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Datafeed20260801));

        var result = await ReaderHarness.RunDatafeedAsync(client);

        Assert.Equal(163, result.Rows.Count);            // the replay carried the whole payload through
        Assert.Equal(1, provider.RefreshCalls);          // re-minted EXACTLY once
        Assert.Equal("Success", Assert.Single(result.FileLog.Calls).Status);
    }

    [Fact]
    public async Task Http401Twice_Throws_NoSecondReMint_AndLogs401()
    {
        var provider = new FakeNgiTokenProvider();
        var client = AuthedClient(provider,
            FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "no"),
            FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "still no"));

        var fileLog = new FakeNgiFileLog();
        var reader = new NgiBidWeekSourceReader(client, ReaderHarness.Settings(), fileLog, new ListLogger());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        Assert.Contains("HTTP 401 after a token re-mint and replay", ex.Message);
        Assert.Equal(1, provider.RefreshCalls);          // one re-mint attempt only

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(401, call.HttpStatus);
    }

    // ================================================== the failure path never masks the real error

    [Fact]
    public async Task AFailingFileLogWrite_DoesNotMaskTheOriginalError()
    {
        var throwing = new ThrowingNgiFileLog();
        var (reader, log) = ReaderHarness.BidWeekReaderWith(throwing, HttpStatusCode.BadRequest, "bad date");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), CancellationToken.None));

        Assert.Contains("HTTP 400", ex.Message);          // the ORIGINAL error, not "filelog boom"
        Assert.Equal(1, throwing.Calls);
        Assert.Contains(log.OfLevel(LogLevel.Error), e => e.Message.Contains("FileLog row"));
    }

    [Fact]
    public async Task Locations_AFailingFileLogWrite_DoesNotMaskTheOriginalError()
    {
        var throwing = new ThrowingNgiFileLog();
        var (reader, log) = ReaderHarness.LocationsReaderWith(throwing, HttpStatusCode.Forbidden, "nope");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(ReaderHarness.LocationsUnit(), CancellationToken.None));

        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains(log.OfLevel(LogLevel.Error), e => e.Message.Contains("FileLog row"));
    }

    // ================================================== cancellation writes no hub row

    [Fact]
    public async Task Cancellation_RethrowsAndWritesNoFileLogRow()
    {
        var fileLog = new FakeNgiFileLog();
        var handler = FakeHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.Datafeed20260801);
        var reader = new NgiBidWeekSourceReader(handler.NewClient(), ReaderHarness.Settings(), fileLog, new ListLogger());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(ReaderHarness.BidWeekUnit(), cts.Token));

        Assert.Empty(fileLog.Calls);   // no hub write on a spent token
    }
}
