using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// <see cref="DailySourceReader"/> — the daily CSV parse path plus the shared
/// per-request load flow (FileLog upsert, FileLogId stamping, 404 / error /
/// cancellation semantics, api-key sanitization). HTTP is a stub handler; the
/// FileLog is an in-memory fake. No network, no database.
/// </summary>
public class DailySourceReaderTests
{
    private const string ApiKey = "SECRET-KEY-123";

    private static StormVistaSettings Settings() => new()
    {
        BaseUrl = "https://api.stormvistawxmodels.com/v1",
        ApiKey = ApiKey
    };

    private static DailyWorkUnit Unit() => new()
    {
        Model = "gfs",
        InitDate = new DateOnly(2024, 8, 4),
        Cycle = "00",
        WddType = "ew_cdd",
        KeyValue = "sv:daily:gfs:20240804:00:ew_cdd"
    };

    private const string ExpectedPath = "/model-data/gfs/20240804/00z/wdd/ew_cdd-daily.csv";

    private static DailySourceReader NewReader(StubHttpMessageHandler handler, FakeStormVistaFileLog fileLog) =>
        new(handler.NewClient(), Settings(), fileLog, NullLogger<DailySourceReader>.Instance);

    // A realistic body: 7 observed (flag 0), forecast (flag 1), climate-normal (flag 2).
    private const string ValidCsv =
        "Date,Value,\"Flag (0=obs 1=fcst 2=norm)\"\n" +
        "2024-07-28,12.105,0\n" +
        "2024-08-04,15.500,1\n" +
        "2024-08-25,11.488,2\n";

    [Fact]
    public async Task Read_200_ParsesFlags012_StampsFileLogId_AndUpsertsSuccessOnce()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, ValidCsv);
        var fileLog = new FakeStormVistaFileLog { FileLogIdToReturn = 777 };
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 0, 1, 2 }, rows.Select(r => r.FlagCode).ToArray());
        Assert.Equal(new DateOnly(2024, 7, 28), rows[0].ValidDate);
        Assert.Equal(12.105m, rows[0].Value);
        Assert.Equal(15.500m, rows[1].Value);
        // The returned FileLogId is stamped onto EVERY leaf row.
        Assert.All(rows, r => Assert.Equal(777, r.FileLogId));

        // Exactly one FileLog upsert, Success, RowCount == number of leaf rows.
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(3, call.RowCount);
        Assert.Equal("Daily", call.File.EndpointName);
        Assert.Null(call.File.RegionSetCode); // daily has no region set
    }

    [Fact]
    public async Task Read_200_TolerantPerRowSkip_DropsBadDateValueAndOutOfDomainFlag()
    {
        var csv =
            "Date,Value,\"Flag (0=obs 1=fcst 2=norm)\"\n" +
            "2024-07-28,12.105,0\n" +   // ok
            "not-a-date,9.9,1\n" +       // bad date -> skip
            "2024-07-30,,1\n" +          // blank value -> skip
            "2024-07-31,7.7,3\n" +       // flag out of {0,1,2} -> skip
            "2024-08-01,8.8,2\n";        // ok

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { new DateOnly(2024, 7, 28), new DateOnly(2024, 8, 1) }, rows.Select(r => r.ValidDate).ToArray());
        Assert.Equal(2, Assert.Single(fileLog.Calls).RowCount); // RowCount counts only surviving rows
    }

    [Fact]
    public async Task Read_200_HeaderOnly_YieldsZeroRows_ButStillUpsertsSuccess()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, "Date,Value,\"Flag (0=obs 1=fcst 2=norm)\"\n");
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_200_LeadingBom_DoesNotBreakParsing()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, "﻿" + ValidCsv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Equal(3, rows.Count); // BOM stripped; first row still parses
    }

    [Fact]
    public async Task Read_404_UpsertsNotAvailable_ReturnsEmpty_DoesNotThrow()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.NotFound, "<html>not found</html>");
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    public async Task Read_NonSuccessNon404_UpsertsFailed_ThenThrows(HttpStatusCode status, int expected)
    {
        var handler = StubHttpMessageHandler.Respond(status, "boom");
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(Unit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_Cancellation_Rethrows_WithNoFileLogWrite()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, ValidCsv);
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Unit(), cts.Token));

        Assert.Empty(fileLog.Calls); // cancellation must NOT write a FileLog row
    }

    [Fact]
    public async Task Read_SanitizesApiKey_FromLoggedRequestPath_ButSendsItOnTheWire()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, ValidCsv);
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        await reader.ReadAsync(Unit(), CancellationToken.None);

        // The FileLog RequestPath is the sanitized relative path — no query, no key.
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal(ExpectedPath, call.RequestPath);
        Assert.DoesNotContain("apikey", call.RequestPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiKey, call.RequestPath);

        // But the actual request URI carried the key so the API authenticates.
        var uri = Assert.Single(handler.Requests);
        Assert.Contains("apikey=" + ApiKey, uri.Query);
        Assert.Equal("/v1" + ExpectedPath, uri.AbsolutePath);
    }
}
