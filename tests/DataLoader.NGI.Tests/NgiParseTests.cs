using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiParse"/> - the tolerant, invariant-culture value readers every NGI field goes
/// through. Two behaviours here are load-bearing:
///
/// <list type="number">
///   <item><b>Candidate-name lookup</b>, because five live field names contain SPACES that no
///     <c>JsonNamingPolicy</c> can produce.</item>
///   <item><b>The literal string <c>"None"</c> is the vendor's null sentinel</b>, on EVERY field type
///     including the strings. It is applied BEFORE any conversion, so nothing can persist the text
///     "None" and nothing can fall back to a fabricated 0.</item>
/// </list>
///
/// Per the platform contract an unrecognised/unparseable value degrades to <c>null</c> (never a row
/// or run failure) and only an unusable KEY drops a record.
/// </summary>
public class NgiParseTests
{
    // ================================================== the "None" sentinel

    [Theory]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("  None  ")]
    [InlineData("null")]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("NA")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsNullSentinel_True(string? value) => Assert.True(NgiParse.IsNullSentinel(value));

    [Theory]
    [InlineData("0")]                 // a real zero is NOT null
    [InlineData("0.000")]
    [InlineData("None of the above")] // only the exact token, trimmed, counts
    [InlineData("Northeast")]
    [InlineData("-1.125")]            // a negative price is NOT the "-" sentinel
    [InlineData("NAM")]
    public void IsNullSentinel_False(string value) => Assert.False(NgiParse.IsNullSentinel(value));

    // ================================================== candidate-name property lookup

    [Fact]
    public void Prop_FindsTheSpaceBearingLiveSpelling_Exactly()
    {
        var e = Js.Element("""{"Point Code":"STXAGUAD"}""");
        Assert.Equal("STXAGUAD", NgiParse.Str(e, NgiFields.PointCode));
    }

    [Theory]
    [InlineData("""{"Point Code":"STXAGUAD"}""")]
    [InlineData("""{"PointCode":"STXAGUAD"}""")]
    [InlineData("""{"point_code":"STXAGUAD"}""")]
    [InlineData("""{"POINT CODE":"STXAGUAD"}""")]   // case-insensitive fallback
    [InlineData("""{"pointcode":"STXAGUAD"}""")]
    public void Prop_AcceptsEveryDocumentedCandidateSpelling(string json) =>
        Assert.Equal("STXAGUAD", NgiParse.Str(Js.Element(json), NgiFields.PointCode));

    [Fact]
    public void Prop_CandidateOrderWins_TheLiveSpellingIsPreferred()
    {
        var e = Js.Element("""{"point_code":"WRONG","Point Code":"STXAGUAD"}""");
        Assert.Equal("STXAGUAD", NgiParse.Str(e, NgiFields.PointCode)); // first candidate listed wins
    }

    [Fact]
    public void Prop_SkipsAJsonNullCandidate_AndKeepsLooking()
    {
        var e = Js.Element("""{"Region":null,"region":"South Texas"}""");
        Assert.Equal("South Texas", NgiParse.Str(e, NgiFields.Region));
    }

    [Fact]
    public void Prop_MissingProperty_IsNull_NotAThrow()
    {
        var e = Js.Element("""{"Something":"else"}""");
        Assert.Null(NgiParse.Prop(e, NgiFields.PointCode));
        Assert.Null(NgiParse.Str(e, NgiFields.PointCode));
    }

    [Fact]
    public void Prop_NonObjectElement_IsNull_NotAThrow()
    {
        Assert.Null(NgiParse.Prop(Js.Element("[1,2,3]"), NgiFields.PointCode));
        Assert.Null(NgiParse.Prop(Js.Element("\"scalar\""), NgiFields.PointCode));
    }

    [Fact]
    public void Str_UnexpectedObjectOrArrayValue_DegradesToNull_NeverAThrow()
    {
        Assert.Null(NgiParse.Str(Js.Element("""{"Region":{"nested":1}}"""), NgiFields.Region));
        Assert.Null(NgiParse.Str(Js.Element("""{"Region":[1,2]}"""), NgiFields.Region));
    }

    // ================================================== Str

    [Fact]
    public void Str_ReadsABareScalarElement_ThroughTheNullableOverload()
    {
        // The locations feed's VALUE is a bare JSON string (not a property of an object), so it must be
        // read with the JsonElement? overload - Str(JsonElement?).
        //
        // *** OVERLOAD HAZARD (this is a live bug at one call site - see the CODE_TESTER report). ***
        // `NgiParse.Str(someJsonElement)` with ONE argument does NOT bind to Str(JsonElement?); the
        // identity conversion makes the params overload Str(JsonElement, params string[]) the better
        // candidate, so it binds there with an EMPTY name list, `Prop` sees a non-object element,
        // and the result is ALWAYS null. Pass a JsonElement? (or a name list) explicitly.
        JsonElementHolder holder = new(Js.Element("\"STXAGUAD\""));

        Assert.Equal("STXAGUAD", NgiParse.Str(holder.Nullable));
        Assert.Equal("STXAGUAD", NgiParse.Str(NgiParse.Prop(Js.Element("""{"v":"STXAGUAD"}"""), "v")));
    }

    /// <summary>Forces the <c>JsonElement?</c> overload without an inline cast obscuring the point.</summary>
    private readonly record struct JsonElementHolder(System.Text.Json.JsonElement Value)
    {
        public System.Text.Json.JsonElement? Nullable => Value;
    }

    [Fact]
    public void Str_TrimsAndNullsTheSentinel()
    {
        Assert.Equal("South Texas", NgiParse.Str(Js.Element("""{"Region":"  South Texas  "}"""), NgiFields.Region));
        Assert.Null(NgiParse.Str(Js.Element("""{"Region":"None"}"""), NgiFields.Region));
        Assert.Null(NgiParse.Str(Js.Element("""{"Region":""}"""), NgiFields.Region));
    }

    // ================================================== Dec

    [Theory]
    [InlineData("\"2.360\"", 2.360)]
    [InlineData("\"2\"", 2)]
    [InlineData("2.375", 2.375)]        // a JSON number, defensively accepted
    [InlineData("\"-1.125\"", -1.125)]  // NEGATIVE GAS PRICES ARE REAL - a leading minus must parse
    [InlineData("\"0.000\"", 0)]
    [InlineData("\"  2.360  \"", 2.360)]
    public void Dec_ParsesInvariant(string valueJson, double expected)
    {
        var v = NgiParse.Dec(Js.Element($"{{\"Low\":{valueJson}}}"), NgiFields.Low, out var bad);
        Assert.False(bad);
        Assert.Equal((decimal)expected, v);
    }

    [Fact]
    public void Dec_PreservesTheFullPublishedPrecision()
    {
        var v = NgiParse.Dec(Js.Element("""{"Low":"2.123456"}"""), NgiFields.Low, out _);
        Assert.Equal(2.123456m, v); // DECIMAL(13,6) - a published price must round-trip exactly
    }

    [Theory]
    [InlineData("\"None\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Dec_Sentinel_IsNull_AndNotFlaggedUnparseable(string valueJson)
    {
        var v = NgiParse.Dec(Js.Element($"{{\"Low\":{valueJson}}}"), NgiFields.Low, out var bad);
        Assert.Null(v);
        Assert.False(bad);   // a sentinel is expected data, not a parse failure
    }

    [Theory]
    [InlineData("\"2,36\"")]      // European decimal comma is NOT the invariant format
    [InlineData("\"abc\"")]
    [InlineData("\"$2.36\"")]
    public void Dec_Unparseable_IsNullAndFlagged(string valueJson)
    {
        var v = NgiParse.Dec(Js.Element($"{{\"Low\":{valueJson}}}"), NgiFields.Low, out var bad);
        Assert.Null(v);      // NEVER 0 - an invented price of zero would be worse than a NULL
        Assert.True(bad);
    }

    // ================================================== Int

    [Theory]
    [InlineData("\"240\"", 240)]
    [InlineData("240", 240)]
    [InlineData("\"240.0\"", 240)]    // integral decimal accepted
    [InlineData("\"1,234\"", 1234)]   // thousands separator accepted
    [InlineData("\"0\"", 0)]
    [InlineData("\"-3\"", -3)]
    public void Int_Parses(string valueJson, int expected)
    {
        var v = NgiParse.Int(Js.Element($"{{\"Volume\":{valueJson}}}"), NgiFields.Volume, out var bad);
        Assert.False(bad);
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Int_FractionalValue_IsNotSilentlyTruncated()
    {
        var v = NgiParse.Int(Js.Element("""{"Volume":"240.5"}"""), NgiFields.Volume, out var bad);
        Assert.Null(v);
        Assert.True(bad);
    }

    [Fact]
    public void Int_Sentinel_IsNull()
    {
        var v = NgiParse.Int(Js.Element("""{"Volume":"None"}"""), NgiFields.Volume, out var bad);
        Assert.Null(v);
        Assert.False(bad);
    }

    // ================================================== Date

    [Fact]
    public void Date_ParsesTheOnlyObservedFormat()
    {
        var v = NgiParse.Date(Js.Element("""{"Issue Date":"2026-08-01"}"""), NgiFields.IssueDate, out var bad);
        Assert.False(bad);
        Assert.Equal(new DateOnly(2026, 8, 1), v);
    }

    [Theory]
    [InlineData("\"08/01/2026\"")]
    [InlineData("\"2026-08-01T00:00:00\"")]
    [InlineData("\"1 Aug 2026\"")]
    [InlineData("\"20260801\"")]
    public void Date_AnyOtherFormat_IsNullAndFlagged(string valueJson)
    {
        var v = NgiParse.Date(Js.Element($"{{\"Issue Date\":{valueJson}}}"), NgiFields.IssueDate, out var bad);
        Assert.Null(v);
        Assert.True(bad);
    }

    [Fact]
    public void Date_Sentinel_IsNull_AndNotFlagged()
    {
        var v = NgiParse.Date(Js.Element("""{"Survey Start":"None"}"""), NgiFields.SurveyStart, out var bad);
        Assert.Null(v);
        Assert.False(bad);
    }

    // ================================================== the counters object

    [Fact]
    public void Counters_StartClean_AndReportEveryDimension()
    {
        var c = new NgiParseCounters();
        Assert.True(c.IsClean);

        c.Dropped = 1;
        Assert.False(c.IsClean);

        var text = new NgiParseCounters
        {
            Dropped = 1, MapKeyFallback = 2, MapKeyMismatch = 3, IssueDateMismatch = 4,
            UnparseableNumeric = 5, UnparseableDate = 6, MissingSurveyWindow = 7, DuplicateKeys = 8
        }.ToString();

        foreach (var fragment in new[]
                 {
                     "dropped=1", "mapKeyFallback=2", "mapKeyMismatch=3", "issueDateMismatch=4",
                     "unparseableNumeric=5", "unparseableDate=6", "missingSurveyWindow=7", "duplicates=8"
                 })
            Assert.Contains(fragment, text);
    }

    // ================================================== the candidate-name catalogue itself

    [Fact]
    public void FieldCatalogue_ListsTheLiveSpaceBearingSpellingFirst()
    {
        // The live spelling must stay FIRST so it is the cheap exact-match path (and so candidate
        // order resolves a conflict in the live payload's favour).
        Assert.Equal("Point Code", NgiFields.PointCode[0]);
        Assert.Equal("Issue Date", NgiFields.IssueDate[0]);
        Assert.Equal("Survey Start", NgiFields.SurveyStart[0]);
        Assert.Equal("Survey End", NgiFields.SurveyEnd[0]);
        Assert.Equal("Pricing Point", NgiFields.PricingPoint[0]);
        Assert.Equal("Bidweek Locations", NgiFields.BidweekLocations[0]);
        Assert.Equal("access_token", NgiFields.AccessToken[0]);
    }

    [Fact]
    public void EndpointIdsAndStatusLabels_AreTheSeededLiterals()
    {
        Assert.Equal("BidWeekLocations", NgiEndpoints.BidWeekLocations);
        Assert.Equal("BidWeekData", NgiEndpoints.BidWeekData);
        Assert.Equal("Success", NgiFileStatus.Success);
        Assert.Equal("NotAvailable", NgiFileStatus.NotAvailable);
        Assert.Equal("Failed", NgiFileStatus.Failed);
    }
}
