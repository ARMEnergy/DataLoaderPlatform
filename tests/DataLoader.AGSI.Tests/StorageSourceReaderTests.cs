using System.Net;
using Microsoft.Extensions.Logging;
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

    private static AgsiStorageSourceReader Reader(StubHttpMessageHandler handler, IAgsiFileLog fileLog, ILogger? logger = null) =>
        new(handler.NewClient(), Settings(), fileLog, logger ?? NullLogger.Instance);

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

    // ================================================================ attribution: data[] element -> requesting entity
    // AGSI answers BOTH `?country=eu` and `?country=ne` with the SAME two-element body
    // (data[0]=eu, data[1]=ne). Stamping unit.EntityId onto every element makes the two
    // rows collide on (EntityId, GasDayStart); the dedupe then keeps ONE — so the `eu`
    // entity silently stored the Non-EU aggregate. Each element must be attributed by
    // its OWN `code`, not by which request happened to return it.

    private static AgsiStorageWorkUnit AggregateUnit(string code) =>
        new()
        {
            EntityId = code == "eu" ? 6600 : 6609,
            CountryCode = code,
            Date = new DateOnly(2026, 9, 1),
            RequestPath = $"/api?country={code}&date=2026-09-01",
            KeyValue = "k"
        };

    [Fact]
    public async Task Read_EuRequest_TwoAggregatesReturned_KeepsOnlyEu()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAggregateEuAndNe);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(AggregateUnit("eu"), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(6600, r.EntityId);
        Assert.Equal(741.9174m, r.GasInStorage);       // data[0] = the EU aggregate ...
        Assert.Equal(1130.6108m, r.WorkingGasVolume);
        Assert.Equal(65.62m, r.Full);
        Assert.Equal("C", r.Status);
        Assert.NotEqual(110.6828m, r.GasInStorage);    // ... and NOT data[1], the Non-EU one
        Assert.Equal(1, Assert.Single(fileLog.Calls).RowCount);
    }

    [Fact]
    public async Task Read_NeRequest_SameTwoAggregatesReturned_KeepsOnlyNe()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAggregateEuAndNe);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(AggregateUnit("ne"), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(6609, r.EntityId);
        Assert.Equal(110.6828m, r.GasInStorage);       // data[1] = the Non-EU aggregate
        Assert.Equal(331.0191m, r.WorkingGasVolume);
        Assert.Equal(33.44m, r.Full);
        Assert.Equal("E", r.Status);
        Assert.Null(r.ContractedCapacity);             // "-" is the vendor's blank marker
        Assert.Null(r.AvailableCapacity);
    }

    [Fact]
    public async Task Read_MultiElementResponse_LogsWarning_NamingRequestedAndReturnedCodes()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAggregateEuAndNe);
        var logger = new ListLogger();
        var reader = Reader(handler, new FakeAgsiFileLog(), logger);

        await reader.ReadAsync(AggregateUnit("eu"), CancellationToken.None);

        var warning = Assert.Single(logger.OfLevel(LogLevel.Warning));
        Assert.Contains("eu", warning.Message);
        Assert.Contains("ne", warning.Message);
    }

    [Fact]
    public async Task Read_ForeignCodeOnly_NeverAttributedToRequestingEntity_ZeroRows_NotAvailable()
    {
        // A response carrying only some OTHER entity's record must NOT be stored under
        // the requesting entity — that is exactly how `eu` acquired Non-EU's numbers.
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageSingleRecordForeignCode);
        var fileLog = new FakeAgsiFileLog();
        var logger = new ListLogger();
        var reader = Reader(handler, fileLog, logger);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None); // requests "de", body carries "UA"

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(0, call.RowCount);
        Assert.NotEmpty(logger.OfLevel(LogLevel.Warning));   // unexpected — must not be silent
    }

    [Fact]
    public async Task Read_SingleRecordWithoutCode_IsUnambiguous_SoItIsAccepted()
    {
        // Tolerance: nothing to confuse a lone code-less record with, so it is still
        // attributed to the requesting entity (the pre-existing behaviour).
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageSingleRecordNoCode);
        var reader = Reader(handler, new FakeAgsiFileLog());

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(7, r.EntityId);
        Assert.Equal(121.1238m, r.GasInStorage);
    }

    [Fact]
    public async Task Read_CodeMatch_IsCaseInsensitive()
    {
        // The URL carries the lowercased code ("de"); the response echoes "DE".
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageDe);
        var reader = Reader(handler, new FakeAgsiFileLog());

        var rows = await reader.ReadAsync(Unit("de"), CancellationToken.None);

        Assert.Single(rows);
    }

    // ================================================================ clamped gas day
    // AGSI silently clamps an unpublished / future `date` to the latest available gas
    // day rather than answering empty. Storing that under the requested Date breaks the
    // Date == GasDayStart invariant the MERGE key depends on, and overwrites the
    // PREVIOUS day's row (the MERGE keys on GasDayStart).

    [Fact]
    public async Task Read_ResponseClampedToEarlierGasDay_ZeroRows_NotAvailable()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageClampedToEarlierGasDay);
        var fileLog = new FakeAgsiFileLog();
        var logger = new ListLogger();
        var reader = Reader(handler, fileLog, logger);

        // Requests 2026-09-27; the body answers with gas day 2026-09-26.
        var rows = await reader.ReadAsync(Unit("at", new DateOnly(2026, 9, 27)), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(0, call.RowCount);
        Assert.NotEmpty(logger.OfLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Read_EveryPersistedRow_HasGasDayStartEqualToRequestDate()
    {
        // The load-bearing single-date invariant (design §10): the resume key uses the
        // request Date while the MERGE keys on GasDayStart, so idempotency holds ONLY
        // while the two agree. Asserted at the reader boundary, not just post-load SQL.
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAggregateEuAndNe);
        var reader = Reader(handler, new FakeAgsiFileLog());

        var unit = AggregateUnit("eu");
        var rows = await reader.ReadAsync(unit, CancellationToken.None);

        Assert.All(rows, r => Assert.Equal(unit.Date, r.GasDayStart));
    }

    [Fact]
    public async Task Read_MissingGasDayStart_StillFallsBackToRequestDate()
    {
        // Unchanged tolerance: an ABSENT gasDayStart is not a clamped one.
        var body = Samples.StorageDe.Replace("\"gasDayStart\":\"2026-08-13\"", "\"gasDayStart\":\"\"");
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, body);
        var reader = Reader(handler, new FakeAgsiFileLog());

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        var r = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 8, 13), r.GasDayStart);
    }

    // ================================================================ HTTP 200 + body-level error
    // A missing / invalid / revoked x-key answers HTTP 200 with total:0 and an empty
    // data[] — indistinguishable from a legitimate no-data day unless the body's
    // `error` is read. Left unhandled, a revoked key marks the whole hot window done
    // for the CET day while loading nothing.

    [Fact]
    public async Task Read_200_AccessDenied_WritesFailed_ThenThrows()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAccessDenied);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.Contains("access denied", ex.Message, StringComparison.OrdinalIgnoreCase);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);   // NOT NotAvailable — a silent success here hides an outage
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_200_AccessDenied_ExceptionNeverCarriesTheApiKey()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageAccessDenied);
        var reader = Reader(handler, new FakeAgsiFileLog());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    // ---------------------------------------------------------------- the vendor echoes the key back

    [Fact]
    public async Task Read_VendorFaultEchoingKeyInMessage_ExceptionNeverCarriesTheKey()
    {
        // AGSI answers a transient fault with `"message":"API key: <the key>"`. The message
        // must never be repeated — it reaches logs AND core.LoadLog.ErrorMessage.
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.DoesNotContain(ApiKey, ex.Message);                  // the whole point
        Assert.DoesNotContain("API key:", ex.Message);              // the message field is suppressed entirely
        Assert.Contains("Try/Catch error", ex.Message);             // but the diagnosis survives
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task Read_VendorFaultEchoingKeyInErrorField_IsRedacted_NotJustSuppressed()
    {
        // Defence in depth: `error` IS quoted, so it must be redacted rather than trusted.
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultKeyInErrorField);
        var reader = Reader(handler, new FakeAgsiFileLog());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.Contains("***", ex.Message);                         // redacted in place
        Assert.Contains("bad request for key", ex.Message);         // surrounding text kept
    }

    [Fact]
    public async Task Read_VendorFaultEchoingKey_NothingLoggedCarriesTheKey()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageVendorFaultEchoingKey);
        var logger = new ListLogger();
        var reader = Reader(handler, new FakeAgsiFileLog(), logger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));

        Assert.All(logger.Entries, e => Assert.DoesNotContain(ApiKey, e.Message));
    }

    [Fact]
    public async Task Read_200_EmptyDataArray_WithNoErrorField_IsStillNotAvailable()
    {
        // Regression guard: the `error` probe must not reclassify a legitimate empty day.
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.StorageEmptyData);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.Empty(rows);
        Assert.Equal("NotAvailable", Assert.Single(fileLog.Calls).Status);
    }
}
