using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// <see cref="AgsiEntitiesSourceReader"/> — endpoint 1 <c>GET /api/about</c>. Walks
/// the <c>SSO → region → country → [entities]</c> object tree, projects each
/// entity's <c>data</c> to <c>(Code, Name, ParentCode, ParentName)</c>, skips
/// null/blank-country entities, and dedups to one row per <c>Code</c> (design §4.1).
/// A 2xx that parses to ZERO entities is a FAILURE (finding-6 fix). HTTP is a stub
/// handler and the FileLog is an in-memory fake — no network, no database.
/// </summary>
public class EntitiesSourceReaderTests
{
    private static AgsiSettings Settings() => new() { BaseUrl = "https://agsi.gie.eu" };

    private static AgsiEntitiesWorkUnit Unit() => new() { KeyValue = "agsi:entities:run=20260817" };

    private static AgsiEntitiesSourceReader Reader(StubHttpMessageHandler handler, IAgsiFileLog fileLog) =>
        new(handler.NewClient(), Settings(), fileLog, NullLogger.Instance);

    // ---------------------------------------------------------------- parse + dedup + skip

    [Fact]
    public async Task Read_Nested_YieldsUniqueCountryRows_DedupsSsos_SkipsNullCountry_MapsRightPaths()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.AboutNested);
        var fileLog = new FakeAgsiFileLog { FileLogIdToReturn = 777 };
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        // Two Austria SSOs collapse to one AT row; Germany one DE row; the missing-country
        // and blank-code entities are skipped → exactly two distinct country rows.
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "AT", "DE" }, rows.Select(r => r.Code).OrderBy(c => c).ToArray());

        // Code/Name/ParentCode/ParentName map from data.country.code / data.country.name /
        // data.code / data.name (NOT the display map keys, NOT the operator-level name).
        var at = Assert.Single(rows, r => r.Code == "AT");
        Assert.Equal("Austria", at.Name);
        Assert.Equal("EU", at.ParentCode);
        Assert.Equal("Europe", at.ParentName);

        var de = Assert.Single(rows, r => r.Code == "DE");
        Assert.Equal("Germany", de.Name);
        Assert.Equal("EU", de.ParentCode);
        Assert.Equal("Europe", de.ParentName);

        // The operator legal name ("GSA LLC"/"OMV …") is dropped; Name is the COUNTRY name.
        Assert.DoesNotContain(rows, r => r.Name.Contains("LLC") || r.Name.Contains("GmbH"));
    }

    [Fact]
    public async Task Read_Success_StampsFileLogIdOnEveryRow_UpsertsSuccessOnce()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.AboutNested);
        var fileLog = new FakeAgsiFileLog { FileLogIdToReturn = 777 };
        var reader = Reader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit(), CancellationToken.None);

        Assert.All(rows, r => Assert.Equal(777, r.FileLogId));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(rows.Count, call.RowCount);
        Assert.Equal("About", call.File.Endpoint);
        Assert.Equal("/api/about", call.RequestPath);
    }

    // ---------------------------------------------------------------- finding-6: 2xx + 0 entities throws

    [Fact]
    public async Task Read_200_ButZeroEntitiesParsed_Throws_AndWritesFailedHubRow()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.AboutZeroEntities);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        // The discovery endpoint is mandatory: a 2xx that yields 0 country entities is a
        // FAILURE, not a silent NotAvailable+success (which would mark discovery "done").
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));
        Assert.Contains("0 country entities", ex.Message);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(0, call.RowCount);
    }

    [Fact]
    public async Task Read_200_NoSsoKey_ZeroEntities_Throws()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.AboutNoSso);
        var reader = Reader(handler, new FakeAgsiFileLog());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(Unit(), CancellationToken.None));
    }

    // ---------------------------------------------------------------- non-2xx is loud (public discovery call)

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task Read_NonSuccess_WritesFailed_ThenThrows(HttpStatusCode status, int expected)
    {
        var handler = StubHttpMessageHandler.Respond(status, "boom");
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(Unit(), CancellationToken.None));

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Failed", call.Status);
        Assert.Equal(expected, call.HttpStatus);
    }

    [Fact]
    public async Task Read_FileLogThrowsOnFailurePath_OriginalErrorNotMasked()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.AboutZeroEntities);
        var reader = Reader(handler, new ThrowingAgsiFileLog());

        // The best-effort 'Failed' write throws internally, but the ORIGINAL zero-entities error surfaces.
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Unit(), CancellationToken.None));
    }

    [Fact]
    public async Task Read_Cancellation_Rethrows_WithNoFileLogWrite()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, Samples.AboutNested);
        var fileLog = new FakeAgsiFileLog();
        var reader = Reader(handler, fileLog);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Unit(), cts.Token));
        Assert.Empty(fileLog.Calls); // cancellation must NOT write a spurious hub row
    }
}
