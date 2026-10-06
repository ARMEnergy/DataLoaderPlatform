using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// <see cref="CwgSourceReader{TRecord,TRow}"/> — the shared HTTP + FileLog + parse
/// + map flow (design §5). 404 / zero-row / success / auth-error / cancellation
/// semantics, the sentinel-vs-invalid drop classification, and api-key
/// sanitization. HTTP is a stub handler and the FileLog is an in-memory fake — no
/// network, no database.
///
/// <para>Since the 2026-10-05 reshape the reader no longer STAMPS a FileLogId onto
/// rows (the facts have no such column), but it still writes the hub row for every
/// request — that is the run/status log the status matrix reads.</para>
/// </summary>
public class SourceReaderTests
{
    private const string ApiKey = "SECRET-KEY-123";
    private const string CityFile = "city15dfcst_northamerica_20260811_F.csv";
    private const string SanitizedPath = "/" + CityFile;

    private static CwgSettings Settings() => new()
    {
        BaseUrl = "https://api.commoditywx.com/v1",
        ApiKey = ApiKey
    };

    private static CwgWorkUnit CityUnit() => new()
    {
        EndpointId = "CityForecast",
        Region = "northamerica",
        Units = "F", // northamerica is fetched in _F; CityForecastRow.From drops rows when Units is null
        RepresentativeDate = new DateOnly(2026, 8, 11),
        Filename = CityFile,
        KeyValue = "k"
    };

    private static CwgSourceReader<CwgTabularRecord, CityForecastRow> CityReader(
        StubHttpMessageHandler handler, ICwgFileLog fileLog, ILogger? logger = null) =>
        new(handler.NewClient(), Settings(), fileLog, CwgDescriptors.CityForecast,
            new ShapeAParser(), CityForecastRow.From, logger ?? NullLogger.Instance);

    // ---------------------------------------------------------------- 200 with data

    [Fact]
    public async Task Read_200_WithData_ReturnsRows_UpsertsSuccessOnce()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, SampleData.Read(CityFile));
        var fileLog = new FakeCwgFileLog { FileLogIdToReturn = 999 };
        var reader = CityReader(handler, fileLog);

        var rows = await reader.ReadAsync(CityUnit(), CancellationToken.None);

        Assert.NotEmpty(rows);

        // The hub row is STILL written for every request (it is the run/status log) even though
        // the 2026-10-05 reshape removed FileLogId from the fact rows themselves.
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(rows.Count, call.RowCount);
        Assert.Equal("CityForecast", call.File.Endpoint);
        Assert.Equal("northamerica", call.File.Region);
    }

    // ---------------------------------------------------------------- 404

    [Fact]
    public async Task Read_404_UpsertsNotAvailable_ReturnsEmpty_DoesNotThrow()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.NotFound, "<html>not found</html>");
        var fileLog = new FakeCwgFileLog();
        var reader = CityReader(handler, fileLog);

        var rows = await reader.ReadAsync(CityUnit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    // ---------------------------------------------------------------- zero-row 200

    [Fact]
    public async Task Read_200_HeaderOnly_ZeroRows_UpsertsNotAvailable()
    {
        var headerOnly = "Production Date,Date,Station,Fcst Mn,Fcst Mx,Fcst Avg,Norm Mn,Norm Max,HDD,CDD\n";
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, headerOnly);
        var fileLog = new FakeCwgFileLog();
        var reader = CityReader(handler, fileLog);

        var rows = await reader.ReadAsync(CityUnit(), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("NotAvailable", call.Status); // a zero-row 200 logs NotAvailable, not Success (decision 5)
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    // ---------------------------------------------------------------- non-success (auth / 5xx)

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task Read_NonSuccessNon404_UpsertsFailed_ThenThrows(HttpStatusCode status, int expected)
    {
        var handler = StubHttpMessageHandler.Respond(status, "boom");
        var fileLog = new FakeCwgFileLog();
        var reader = CityReader(handler, fileLog);

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(CityUnit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_FileLogThrowsOnFailurePath_OriginalHttpErrorIsNotMasked()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.InternalServerError, "boom");
        var reader = CityReader(handler, new ThrowingCwgFileLog());

        // The best-effort 'Failed' write throws internally, but the ORIGINAL HttpRequestException surfaces.
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(CityUnit(), CancellationToken.None));
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task Read_Cancellation_Rethrows_WithNoFileLogWrite()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, SampleData.Read(CityFile));
        var fileLog = new FakeCwgFileLog();
        var reader = CityReader(handler, fileLog);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(CityUnit(), cts.Token));

        Assert.Empty(fileLog.Calls); // cancellation must NOT write a spurious hub row
    }

    // ---------------------------------------------------------------- api-key sanitization

    [Fact]
    public async Task Read_ApiKey_NeverInSanitizedPath_ButSentOnTheWire()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, SampleData.Read(CityFile));
        var fileLog = new FakeCwgFileLog();
        var reader = CityReader(handler, fileLog);

        await reader.ReadAsync(CityUnit(), CancellationToken.None);

        // FileLog RequestPath (and the CwgFileContext.RequestPath) are the sanitized relative path.
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal(SanitizedPath, call.RequestPath);
        Assert.Equal(SanitizedPath, call.File.RequestPath);
        Assert.DoesNotContain("apikey", call.RequestPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiKey, call.RequestPath);

        // But the real request URI carried the key so the API authenticates.
        var uri = Assert.Single(handler.Requests);
        Assert.Contains("apikey=" + ApiKey, uri.Query);
        Assert.Equal("/v1" + SanitizedPath, uri.AbsolutePath);
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath); // key only in the query, never the path
    }

    // ---------------------------------------------------------------- sentinel-vs-invalid drop classification

    [Fact]
    public async Task Read_SolarHourly_NullSentinelIsKeptAsNullColumn_NotDropped_NoWarning()
    {
        // The target is WIDE now, so the literal "NULL" is a NULL COLUMN on a kept row — it is
        // no longer a dropped record, which is why SolarHourly needs no sentinel predicate.
        var csv =
            "UTC_HOUR_ENDING,ERCOT,CAISO,PJM\n" +
            "2020-07-26 15:00:00,3100,NULL,500\n";
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeCwgFileLog { FileLogIdToReturn = 7 };
        var logger = new ListLogger();

        var unit = new CwgWorkUnit
        {
            EndpointId = "SolarHourly",
            RepresentativeDate = null,
            Filename = "Gen_hrly_solar.csv",
            KeyValue = "k"
        };
        var reader = new CwgSourceReader<CwgWideRow, SolarHourlyRow>(
            handler.NewClient(), Settings(), fileLog, CwgDescriptors.SolarHourly,
            new ShapeBParser(), SolarHourlyRow.From, logger);

        var rows = await reader.ReadAsync(unit, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2020, 7, 26, 15, 0, 0), row.UTCHourEnding);
        Assert.Equal(3100m, row.Values[Array.IndexOf(SolarHourlyRow.RegionColumns, "ERCOT")]);
        Assert.Null(row.Values[Array.IndexOf(SolarHourlyRow.RegionColumns, "CAISO")]); // "NULL" -> NULL

        // Nothing was dropped, so there is no invalid-drop warning.
        Assert.DoesNotContain(logger.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("unparseable/invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Read_SolarHourly_GarbageTargetCell_IsAnInvalidDrop_AndWarns()
    {
        // The other half: a published-but-unparseable cell in a TARGET column drops the row
        // and warns, so a real format change stays visible.
        var csv =
            "UTC_HOUR_ENDING,ERCOT,CAISO,PJM\n" +
            "2020-07-26 15:00:00,3100,notanumber,500\n";
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeCwgFileLog();
        var logger = new ListLogger();

        var unit = new CwgWorkUnit
        {
            EndpointId = "SolarHourly",
            RepresentativeDate = null,
            Filename = "Gen_hrly_solar.csv",
            KeyValue = "k"
        };
        var reader = new CwgSourceReader<CwgWideRow, SolarHourlyRow>(
            handler.NewClient(), Settings(), fileLog, CwgDescriptors.SolarHourly,
            new ShapeBParser(), SolarHourlyRow.From, logger);

        var rows = await reader.ReadAsync(unit, CancellationToken.None);

        Assert.Empty(rows);
        Assert.Contains(logger.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("unparseable/invalid", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("NotAvailable", Assert.Single(fileLog.Calls).Status); // zero rows -> NotAvailable
    }

    [Fact]
    public async Task Read_WindTotalCapacityMW_NonCurrentBlocks_AreSentinelDrops_NotWarnings()
    {
        // The capacity MW/Pct targets hold one block, so the row factory drops Yesterday and
        // Change. That drop is DELIBERATE and the module's sentinel predicate claims it, so it
        // is logged at Debug — never counted as a parse failure.
        var csv =
            "Total Wind Generation Across All Regions\n" +
            ",Total Capacity (MW),1-5 Day Avg (MW),6-10 Day Avg (MW),11-15 Day Avg (MW)\n" +
            "MISO,33687,7000,7100,7200\n" +
            "\n" +
            "Friday's Forecast\n" +
            "MISO,33687,6900,7000,7100\n" +
            "\n" +
            "Change from Friday's Forecast\n" +
            "MISO,,100,100,100\n";
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeCwgFileLog();
        var logger = new ListLogger();

        var unit = new CwgWorkUnit
        {
            EndpointId = "WindTotalCapacityMW",
            RepresentativeDate = new DateOnly(2026, 7, 27),
            Filename = "Total_Capacity_vals_07272026.csv",
            KeyValue = "k"
        };
        var reader = new CwgSourceReader<CwgCapacityRow, WindTotalCapacityMWRow>(
            handler.NewClient(), Settings(), fileLog, CwgDescriptors.WindTotalCapacityMW,
            new ShapeEParser(), WindTotalCapacityMWRow.From, logger,
            sentinelPredicate: cap => !CwgCapacity.IsCurrent(cap));

        var rows = await reader.ReadAsync(unit, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(7000m, row.OneToFiveDayAverage);   // the Current block's value

        // Two drops, both claimed as sentinels: Debug, and no invalid-drop warning.
        Assert.Contains(logger.OfLevel(LogLevel.Debug),
            e => e.Message.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("unparseable/invalid", StringComparison.OrdinalIgnoreCase));
    }
}
