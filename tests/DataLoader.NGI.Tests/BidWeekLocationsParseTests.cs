using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiLocationsSourceReader"/> - endpoint 2 <c>GET /bidweekLocations?format=json</c>,
/// driven by the REAL captured live payload <c>Samples/bidweekLocations.json</c> (163 entries).
///
/// <para>*** DIRECTION IS THE WHOLE POINT OF THIS FILE. *** The source map runs
/// <b>NAME -&gt; CODE</b>: <c>{"Bidweek Locations":{"Agua Dulce":"STXAGUAD"}}</c>. The JSON
/// <b>KEY</b> is <c>LocationName</c>; the JSON <b>VALUE</b> is <c>PointCode</c>. Inverting the two
/// throws nothing, logs nothing and parses cleanly - it just produces 163 rows with the name in
/// <c>PointCode</c> and the code in <c>LocationName</c>, after which every join and every
/// reconciliation check silently misses. The vendor spec declares "No response body" for this
/// endpoint, so these assertions are the only regression net that exists.</para>
/// </summary>
public class BidWeekLocationsParseTests
{
    // ================================================== the direction contract, asserted unmissably

    [Fact]
    public async Task LiveFixture_Direction_CodeGoesToPointCode_NameGoesToLocationName()
    {
        var rows = await ReaderHarness.ReadLocationsAsync(Samples.Locations);

        // The single most important assertion in this file. Source entry: "Agua Dulce":"STXAGUAD".
        var agua = Assert.Single(rows, r => r.PointCode == "STXAGUAD");
        Assert.Equal("STXAGUAD", agua.PointCode);      // <- the JSON VALUE (uppercase, unspaced)
        Assert.Equal("Agua Dulce", agua.LocationName); // <- the JSON KEY

        // ... and never the reverse. If the two were swapped, a row keyed on the NAME would exist.
        Assert.DoesNotContain(rows, r => r.PointCode == "Agua Dulce");
        Assert.DoesNotContain(rows, r => r.LocationName == "STXAGUAD");
    }

    [Fact]
    public async Task LiveFixture_Direction_HoldsForEveryOneOfThe163Entries()
    {
        var raw = Samples.RawLocationMap();                       // NAME -> CODE, straight from the file
        var rows = await ReaderHarness.ReadLocationsAsync(Samples.Locations);

        Assert.Equal(Samples.ExpectedLocationCount, rows.Count);  // 163
        Assert.Equal(163, raw.Count);

        var byCode = rows.ToDictionary(r => r.PointCode, StringComparer.Ordinal);
        foreach (var (name, code) in raw)
            Assert.Equal(name, byCode[code].LocationName);         // value -> PointCode, key -> LocationName
    }

    [Fact]
    public async Task LiveFixture_Direction_MnemonicHolds_PointCodesAreUppercaseAndUnspaced()
    {
        var rows = await ReaderHarness.ReadLocationsAsync(Samples.Locations);

        // "Codes are UPPERCASE and unspaced; the uppercase side is always the VALUE."
        Assert.All(rows, r =>
        {
            Assert.Equal(r.PointCode.ToUpperInvariant(), r.PointCode);
            Assert.DoesNotContain(" ", r.PointCode);
            Assert.InRange(r.PointCode.Length, 1, 20);   // VARCHAR(20) in sql/NGI/002
        });

        // At least one name really does contain a space and a lowercase letter, so the assertion above
        // could not pass vacuously if the two sides were swapped.
        Assert.Contains(rows, r => r.LocationName!.Contains(' '));
        Assert.Contains(rows, r => r.LocationName!.Any(char.IsLower));
    }

    [Fact]
    public async Task LiveFixture_StampsFileLogIdOnEveryRow_AndLogsSuccess()
    {
        var result = await ReaderHarness.RunLocationsAsync(HttpStatusCode.OK, Samples.Locations);

        Assert.Equal(163, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal(4242, r.FileLogId));

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("BidWeekLocations", call.File.Endpoint);
        Assert.Null(call.File.RepresentativeDate);        // the undated snapshot -> one stable hub row
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(163, call.RowCount);
        Assert.Equal("/bidweekLocations?format=json", call.RequestPath);
    }

    [Fact]
    public async Task LiveFixture_ParsesCleanly_NoDropsNoDuplicates()
    {
        var result = await ReaderHarness.RunLocationsAsync(HttpStatusCode.OK, Samples.Locations);

        Assert.DoesNotContain(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("tolerance counters"));
        Assert.Equal(163, result.Rows.Select(r => r.PointCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RequestUri_IsTheExplicitFormatJsonPath()
    {
        var result = await ReaderHarness.RunLocationsAsync(HttpStatusCode.OK, Samples.Locations);

        // format=json is sent EXPLICITLY; note this endpoint uses a QUERY parameter while endpoint 1
        // carries its format as a path extension - the two shapes are deliberately not normalised.
        var uri = Assert.Single(result.Handler.Requests);
        Assert.Equal("https://api.ngidata.com/bidweekLocations?format=json", uri.ToString());
    }

    // ================================================== envelope tolerance + shape drift

    [Theory]
    [InlineData("Bidweek Locations")]  // the live spelling (space + capital L)
    [InlineData("BidweekLocations")]
    [InlineData("bidweek_locations")]
    [InlineData("bidweek locations")]  // case-insensitive fallback on the live spelling
    public async Task EnvelopeNode_ToleratesTheDocumentedCandidateSpellings(string node)
    {
        var body = Samples.LocationsEnvelope("""{"Agua Dulce":"STXAGUAD"}""", node);

        var rows = await ReaderHarness.ReadLocationsAsync(body);

        var r = Assert.Single(rows);
        Assert.Equal("STXAGUAD", r.PointCode);
        Assert.Equal("Agua Dulce", r.LocationName);
    }

    [Fact]
    public async Task EnvelopeNodeMissing_Throws_NeverASilentZeroRowSuccess()
    {
        var (reader, fileLog, _) = ReaderHarness.LocationsReader(HttpStatusCode.OK, """{"Something Else":{}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.LocationsUnit(), CancellationToken.None));

        Assert.Contains("shape drift", ex.Message);
        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task EnvelopeNodeIsAnArray_Throws()
    {
        var (reader, fileLog, _) = ReaderHarness.LocationsReader(
            HttpStatusCode.OK, """{"Bidweek Locations":[{"Agua Dulce":"STXAGUAD"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.LocationsUnit(), CancellationToken.None));

        Assert.Equal("Failed", Assert.Single(fileLog.Calls).Status);
    }

    [Fact]
    public async Task RootIsAnArray_Throws()
    {
        var (reader, _, _) = ReaderHarness.LocationsReader(HttpStatusCode.OK, """[{"Agua Dulce":"STXAGUAD"}]""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(ReaderHarness.LocationsUnit(), CancellationToken.None));
    }

    // ================================================== drop-and-count + dedup

    [Theory]
    [InlineData("\"None\"")]   // the vendor null sentinel
    [InlineData("\"\"")]       // blank
    [InlineData("\"   \"")]    // whitespace
    [InlineData("null")]       // JSON null
    [InlineData("{}")]         // unexpected shape -> NULL -> unkeyable
    public async Task EntryWithNoUsablePointCode_IsDroppedAndCounted_NotThrown(string valueJson)
    {
        var body = Samples.LocationsEnvelope($"{{\"Agua Dulce\":{valueJson},\"Carthage\":\"ETXCARTH\"}}");

        var result = await ReaderHarness.RunLocationsAsync(HttpStatusCode.OK, body);

        var r = Assert.Single(result.Rows);           // the good entry survives
        Assert.Equal("ETXCARTH", r.PointCode);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning),
            e => e.Message.Contains("dropped 1 entr"));  // dropped AND counted
    }

    [Fact]
    public async Task BlankLocationName_IsNulled_ButTheRowSurvives()
    {
        // The NAME is not the key, so a "None" name degrades to NULL rather than dropping the row.
        var body = Samples.LocationsEnvelope("""{"None":"STXAGUAD"}""");

        var rows = await ReaderHarness.ReadLocationsAsync(body);

        var r = Assert.Single(rows);
        Assert.Equal("STXAGUAD", r.PointCode);
        Assert.Null(r.LocationName);   // NOT the literal text "None"
    }

    [Fact]
    public async Task DuplicatePointCodeAcrossTwoNames_CollapsesLastWins_AndWarns()
    {
        var body = Samples.LocationsEnvelope("""{"Old Name":"STXAGUAD","Agua Dulce":"stxaguad"}""");

        var result = await ReaderHarness.RunLocationsAsync(HttpStatusCode.OK, body);

        var r = Assert.Single(result.Rows);            // one merge key -> one row
        Assert.Equal("Agua Dulce", r.LocationName);    // last wins
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("duplicate point code"));
    }

    // ================================================== status handling for this endpoint

    [Fact]
    public async Task Http404_IsAnEmptySnapshot_ZeroRows_UnitSucceeds_ButWarnsBecauseItIsUnexpected()
    {
        var result = await ReaderHarness.RunLocationsAsync(
            HttpStatusCode.NotFound, """{"msg":"not found"}""");

        Assert.Empty(result.Rows);                      // no exception - the unit SUCCEEDS
        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);
        Assert.Equal(404, call.HttpStatus);
        Assert.Equal(0, call.RowCount);

        // Unlike BidWeekData, a 404 here is NOT expected (undated snapshot) -> warn.
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("unexpected 404"));
    }

    [Fact]
    public async Task Http200_ButEmptyMap_ZeroRows_NotAvailable_AndWarns()
    {
        var result = await ReaderHarness.RunLocationsAsync(HttpStatusCode.OK, Samples.LocationsEnvelope("{}"));

        Assert.Empty(result.Rows);
        Assert.Equal("NotAvailable", Assert.Single(result.FileLog.Calls).Status);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("0 crosswalk entries"));
    }
}
