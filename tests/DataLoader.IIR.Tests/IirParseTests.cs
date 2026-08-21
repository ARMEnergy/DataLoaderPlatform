using System.Text.Json;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirParse"/> — the tolerant, invariant-culture accessors every row factory relies on
/// (design §6.1). Because the three field schemas are RECONSTRUCTED (⚠), a wrong-cased key, a missing
/// field, or an unexpected flag encoding must degrade to <c>null</c>, never a row failure. Covers:
/// case-insensitive lookup, nested-object descent, string-number tolerance, the <b>Fix #3</b>
/// float-formatted-integer-id regression, the <c>Z[UTC]</c> date-suffix strip, and every documented
/// boolean-flag encoding.
/// </summary>
public class IirParseTests
{
    private static JsonElement E(string json) => Json.Element(json);

    // ---------------------------------------------------------------- Prop (case-insensitive + candidates)

    [Fact]
    public void Str_IsCaseInsensitive_AndTrims_BlankToNull()
    {
        var el = E("""{ "PlantName": "  Acme  ", "blank": "   " }""");
        Assert.Equal("Acme", IirParse.Str(el, "plantname"));   // wrong case still resolves
        Assert.Null(IirParse.Str(el, "blank"));                // whitespace-only → null
        Assert.Null(IirParse.Str(el, "missing"));              // absent → null
    }

    [Fact]
    public void Prop_ReturnsFirstPresentCandidateInOrder()
    {
        var el = E("""{ "eventTypeDesc": "Turnaround" }""");
        // eventType absent, eventTypeDesc present → the second candidate wins.
        Assert.Equal("Turnaround", IirParse.Str(el, "eventType", "eventTypeDesc"));
    }

    // ---------------------------------------------------------------- Int (number, string, float-int)

    [Fact]
    public void Int_ParsesPlainNumberAndStringNumber()
    {
        Assert.Equal(3207542, IirParse.Int(E("""{ "id": 3207542 }"""), "id"));
        Assert.Equal(3207542, IirParse.Int(E("""{ "id": "3207542" }"""), "id"));
    }

    [Theory]
    [InlineData("""{ "id": 3207542.0 }""")]     // JSON number with a trailing .0
    [InlineData("""{ "id": "3207542.0" }""")]   // string-formatted float
    public void Int_Fix3_FloatFormattedInteger_ParsesToIntNotDropped(string json)
    {
        // Regression: a float-formatted integer PK (3207542.0) must parse to the int, never be dropped.
        Assert.Equal(3207542, IirParse.Int(E(json), "id"));
    }

    [Theory]
    [InlineData("""{ "id": "abc" }""")]
    [InlineData("""{ "id": "" }""")]
    [InlineData("""{ "x": 1 }""")]   // absent
    public void Int_UnparseableOrMissing_IsNull(string json) =>
        Assert.Null(IirParse.Int(E(json), "id"));

    // ---------------------------------------------------------------- Float

    [Theory]
    [InlineData("""{ "v": -95.363 }""", -95.363)]
    [InlineData("""{ "v": "29.760" }""", 29.760)]   // string measure tolerated
    [InlineData("""{ "v": 0 }""", 0)]
    public void Float_ParsesNumberAndStringNumber_PreservesSign(string json, double expected) =>
        Assert.Equal(expected, IirParse.Float(E(json), "v"));

    [Theory]
    [InlineData("""{ "v": "N/A" }""")]
    [InlineData("""{ "v": "1,5" }""")]   // comma-decimal is NOT invariant-valid → null
    [InlineData("""{ "x": 1 }""")]
    public void Float_UnparseableOrMissing_IsNull(string json) =>
        Assert.Null(IirParse.Float(E(json), "v"));

    // ---------------------------------------------------------------- Flag → INT 0/1 (all encodings)

    [Theory]
    [InlineData("""{ "f": 1 }""", 1)]
    [InlineData("""{ "f": 0 }""", 0)]
    [InlineData("""{ "f": true }""", 1)]
    [InlineData("""{ "f": false }""", 0)]
    [InlineData("""{ "f": "Y" }""", 1)]
    [InlineData("""{ "f": "N" }""", 0)]
    [InlineData("""{ "f": "y" }""", 1)]
    [InlineData("""{ "f": "true" }""", 1)]
    [InlineData("""{ "f": "false" }""", 0)]
    [InlineData("""{ "f": "1" }""", 1)]
    [InlineData("""{ "f": "0" }""", 0)]
    [InlineData("""{ "f": "yes" }""", 1)]
    [InlineData("""{ "f": "no" }""", 0)]
    public void Flag_NormalizesEveryEncoding_ToZeroOrOne(string json, int expected) =>
        Assert.Equal(expected, IirParse.Flag(E(json), "f"));

    [Theory]
    [InlineData("""{ "f": 2 }""")]        // out-of-domain integer → null
    [InlineData("""{ "f": "maybe" }""")]  // unknown token → null
    [InlineData("""{ "x": 1 }""")]        // absent → null
    public void Flag_UnknownOrMissing_IsNull(string json) =>
        Assert.Null(IirParse.Flag(E(json), "f"));

    // ---------------------------------------------------------------- DateStripZ

    [Fact]
    public void DateStripZ_StripsZUtcSuffix_ParsesUtc()
    {
        var dt = IirParse.DateStripZ(E("""{ "d": "2019-01-29T22:39:21Z[UTC]" }"""), "d");
        Assert.Equal(new DateTime(2019, 1, 29, 22, 39, 21), dt);
    }

    [Fact]
    public void DateStripZ_StripsBareBracketZoneSuffix()
    {
        // A trailing "[…]" IANA-zone suffix without a Z is stripped, then the offset is honoured.
        var dt = IirParse.DateStripZ(E("""{ "d": "2020-06-15T10:00:00[UTC]" }"""), "d");
        Assert.Equal(new DateTime(2020, 6, 15, 10, 0, 0), dt);
    }

    [Fact]
    public void DateStripZ_PlainDate_Accepted()
    {
        var dt = IirParse.DateStripZ(E("""{ "d": "2019-01-29" }"""), "d");
        Assert.Equal(new DateTime(2019, 1, 29), dt!.Value.Date);
    }

    [Theory]
    [InlineData("""{ "d": "" }""")]
    [InlineData("""{ "d": "not-a-date" }""")]
    [InlineData("""{ "x": 1 }""")]
    public void DateStripZ_BlankOrUnparseableOrMissing_IsNull(string json) =>
        Assert.Null(IirParse.DateStripZ(E(json), "d"));

    // ---------------------------------------------------------------- Nested (case-insensitive descent)

    [Fact]
    public void Nested_DescendsCaseInsensitively_AcceptsObjectAndFieldVariants()
    {
        var el = E("""{ "plantAddress": { "City": "Houston" } }""");
        // object candidate list tolerates plantPhysicalAddress|plantAddress|physicalAddress; field "city" wrong-cased.
        var v = IirParse.Nested(el, new[] { "plantPhysicalAddress", "plantAddress", "physicalAddress" }, "city");
        Assert.Equal("Houston", IirParse.Str(v));
    }

    [Fact]
    public void Nested_MissingObject_IsNull()
    {
        var el = E("""{ "other": { "city": "x" } }""");
        Assert.Null(IirParse.Nested(el, new[] { "mailingAddress" }, "city"));
    }
}
