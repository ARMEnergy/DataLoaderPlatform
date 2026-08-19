using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// <see cref="AgsiStorageSourceReader"/> — endpoint 2 <c>GET /api?country=&amp;date=</c>
/// with header <c>x-key</c>. Deserializes the envelope, maps each real <c>data[]</c>
/// element to a <see cref="GasStorageRow"/> with tolerant numeric parsing, and treats
/// 404 / empty <c>data[]</c> / a lone <c>status:"N"</c> record as no-data (0 rows, no
/// unit failure). HTTP is a stub handler and the FileLog is an in-memory fake.
/// </summary>
public class StorageSourceReaderTests
{
    private const string ApiKey = "SECRET-XKEY-123";

    private static AgsiSettings Settings() => new() { BaseUrl = "https://agsi.gie.eu", ApiKey = ApiKey };

    private static AgsiStorageWorkUnit Unit(string body = "de", DateOnly? date = null)
    {
        var d = date ?? new DateOnly(2026, 8, 13);
        return new AgsiStorageWorkUnit
        {
            EntityId = 7,
            CountryCode = body,
            Date = d,
            RequestPath = $"/api?country={body}&date={d:yyyy-MM-dd}",
            KeyValue = "k"
        };
    }

    private static AgsiStorageSourceReader Reader(StubHttpMessageHandler handler, IAgsiFileLog fileLog) =>
        new(handler.NewClient(), Settings(), fileLog, NullLogger.Instance);

    // ---------------------------------------------------------------- full record parse (persisted fields)

    [Fact]
    public async Task Read_SampleDe_MapsPersistedFields_TypedCorrectly_SignsPreserved()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageDe);
        var fileLog = new FakeAgsiFileLog { FileLogIdToReturn = 55 };
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(55, r.FileLogId);
        Assert.Equal(7, r.EntityId);                              // stamped from the work unit (FK), not the response echo

        // Identity / dates. Name/Code/Url are normalized out — the fact links via EntityId.
        Assert.Equal(new DateOnly(2026, 8, 13), r.Date);          // = request date param (audit)
        Assert.Equal(new DateOnly(2026, 8, 16), r.GasDay);        // top-level gas_day marker (NOT key)
        Assert.Equal(new DateTime(2026, 8, 17, 8, 0, 55), r.UpdatedAt); // yyyy-MM-dd HH:mm:ss
        Assert.Equal(new DateOnly(2026, 8, 13), r.GasDayStart);
        Assert.Equal(new DateOnly(2026, 8, 14), r.GasDayEnd);

        // All-string numerics parsed to decimals.
        Assert.Equal(121.1238m, r.GasInStorage);
        Assert.Equal(903.9000m, r.Consumption);
        Assert.Equal(13.4m, r.ConsumptionFull);
        Assert.Equal(543.71m, r.Injection);
        Assert.Equal(6.5m, r.Withdrawal);
        Assert.Equal(246.489m, r.WorkingGasVolume);
        Assert.Equal(4292.58m, r.InjectionCapacity);
        Assert.Equal(7067.36m, r.WithdrawalCapacity);
        Assert.Equal(194.1057m, r.ContractedCapacity);
        Assert.Equal(58.6043m, r.AvailableCapacity);
        Assert.Equal(100m, r.CoveredCapacity);
        Assert.Equal("C", r.Status);
        Assert.Equal(49.14m, r.Full);

        // Signed values preserved.
        Assert.Equal(-537.3m, r.NetWithdrawal);   // negative = net injection
        Assert.Equal(0.24m, r.Trend);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(1, call.RowCount);
    }

    [Fact]
    public void GasStorageRow_HasNoInfoProperty_InfoIsNotPersisted()
    {
        // `info` is the only data[] field dropped — the row type carries no such column.
        Assert.Null(typeof(GasStorageRow).GetProperty("Info"));
    }

    // ---------------------------------------------------------------- no-data tolerance

    [Fact]
    public async Task Read_404_NoException_ZeroRows_UpsertsNotAvailable()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.NotFound, "<html>not found</html>");
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_200_EmptyDataArray_ZeroRows_UpsertsNotAvailable()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageEmptyData);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(200, call.HttpStatus);
    }

    [Fact]
    public async Task Read_200_LoneStatusN_TreatedAsNoData_ZeroRows_UpsertsNotAvailable()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageStatusN);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status); // a lone status:"N" no-data record produces no fact row
    }

    // ---------------------------------------------------------------- model-boundary no-data (IsNoData)

    [Fact]
    public void IsNoData_StatusN_AllMeasuresBlank_IsTrue()
    {
        var rec = new AgsiStorageRecord { Status = "N" }; // all measures default null/blank
        Assert.True(rec.IsNoData());
    }

    [Fact]
    public void IsNoData_StatusC_WithMeasures_IsFalse()
    {
        var rec = new AgsiStorageRecord { Status = "C", GasInStorage = "121.1238" };
        Assert.False(rec.IsNoData());
    }

    [Fact]
    public void IsNoData_StatusN_ButHasAMeasure_IsFalse()
    {
        // status N but a populated measure is NOT the no-data shape.
        var rec = new AgsiStorageRecord { Status = "N", Withdrawal = "6.5" };
        Assert.False(rec.IsNoData());
    }

    // ---------------------------------------------------------------- auth errors are loud

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task Read_AuthOr5xx_WritesFailed_ThenThrows(HttpStatusCode status, int expected)
    {
        var handler = StubHttpMessageHandler.Respond(status, "boom");
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(Unit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
    }

    // ---------------------------------------------------------------- x-key is a header, never in the sanitized path

    [Fact]
    public async Task Read_ApiKey_SentAsXKeyHeader_NotInRequestUriOrLoggedPath()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageDe);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        await reader.ReadAsync(Unit(), CancellationToken.None);

        // The key rode the wire as the x-key request header ...
        var req = Assert.Single(handler.RequestMessages);
        Assert.True(req.Headers.TryGetValues("x-key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));

        // ... never in the URL, and never in the sanitized FileLog RequestPath.
        var uri = Assert.Single(handler.Requests);
        Assert.DoesNotContain(ApiKey, uri.ToString());
        var call = Assert.Single(fileLog.Calls);
        Assert.DoesNotContain(ApiKey, call.RequestPath);
        Assert.Equal("/api?country=de&date=2026-08-13", call.RequestPath);
    }
}
