using System.Globalization;
using System.Text.Json;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// <see cref="PlParse"/> — the tolerant, invariant-culture value readers (design §5.3). Every measure
/// MAY arrive as a JSON string; dates arrive in mixed formats. Each reader returns <c>null</c> (never a
/// throw) for missing / JSON-null / blank / <c>" "</c> / unparseable input, so a single bad cell can
/// never fail a whole row. Internals are visible via InternalsVisibleTo.
/// </summary>
public class PlParseTests
{
    private static JsonElement E(string json) => Json.Element(json);

    // ---------------------------------------------------------------- Decimal: number vs string-number, signs

    [Theory]
    [InlineData("""{ "v": 1.5 }""", 1.5)]
    [InlineData("""{ "v": "0.00000000" }""", 0.0)]     // designcapacity string-zero
    [InlineData("""{ "v": "1" }""", 1.0)]
    [InlineData("""{ "v": -48.25 }""", -48.25)]        // negative number (sample storage)
    [InlineData("""{ "v": "-1113.0385" }""", -1113.0385)] // negative string-number (imports/exports)
    [InlineData("""{ "v": 2947 }""", 2947.0)]          // integer JSON number as decimal
    public void Decimal_ParsesNumberAndStringNumber_PreservingSign(string json, double expected)
    {
        Assert.Equal((decimal)expected, PlParse.Decimal(E(json), "v"));
    }

    [Theory]
    [InlineData("""{ "v": null }""")]
    [InlineData("""{ "v": "" }""")]
    [InlineData("""{ "v": " " }""")]
    [InlineData("""{ "v": "abc" }""")]
    [InlineData("""{ "other": 1 }""")]  // missing property
    public void Decimal_MissingBlankOrUnparseable_IsNull(string json)
    {
        Assert.Null(PlParse.Decimal(E(json), "v"));
    }

    // ---------------------------------------------------------------- Int / Long

    [Theory]
    [InlineData("""{ "v": 9 }""", 9)]
    [InlineData("""{ "v": "1" }""", 1)]      // facilitytypeid string
    [InlineData("""{ "v": "26105" }""", 26105)]
    public void Int_ParsesNumberAndString(string json, int expected) =>
        Assert.Equal(expected, PlParse.Int(E(json), "v"));

    [Theory]
    [InlineData("""{ "v": " " }""")]
    [InlineData("""{ "v": "x" }""")]
    [InlineData("""{ "nope": 1 }""")]
    public void Int_BlankMissingOrUnparseable_IsNull(string json) =>
        Assert.Null(PlParse.Int(E(json), "v"));

    [Theory]
    [InlineData("""{ "id": 635182 }""", 635182L)]     // notice id as JSON number
    [InlineData("""{ "id": "635199" }""", 635199L)]   // notice id as string
    public void Long_ParsesNumberAndString(string json, long expected) =>
        Assert.Equal(expected, PlParse.Long(E(json), "id"));

    // ---------------------------------------------------------------- Bit

    [Theory]
    [InlineData("""{ "v": true }""", true)]
    [InlineData("""{ "v": false }""", false)]
    [InlineData("""{ "v": "true" }""", true)]
    [InlineData("""{ "v": "false" }""", false)]
    public void Bit_ParsesBoolAndStringBool(string json, bool expected) =>
        Assert.Equal(expected, PlParse.Bit(E(json), "v"));

    [Theory]
    [InlineData("""{ "v": "yes" }""")]
    [InlineData("""{ "v": null }""")]
    [InlineData("""{ "nope": true }""")]
    public void Bit_UnparseableOrMissing_IsNull(string json) =>
        Assert.Null(PlParse.Bit(E(json), "v"));

    // ---------------------------------------------------------------- Date: BOTH yyyy-MM-dd and MM/dd/yyyy

    [Theory]
    [InlineData("""{ "d": "2026-08-19" }""", 2026, 8, 19)]   // ISO (demandforecast/marketbalances)
    [InlineData("""{ "d": "07/19/2026" }""", 2026, 7, 19)]   // US slash (pipelineflow/stateflows)
    [InlineData("""{ "d": "8/5/2026" }""", 2026, 8, 5)]      // single-digit US slash
    public void Date_AcceptsIsoAndUsSlashFormats(string json, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), PlParse.Date(E(json), "d"));

    [Theory]
    [InlineData("""{ "d": " " }""")]
    [InlineData("""{ "d": "notadate" }""")]
    [InlineData("""{ "d": null }""")]
    [InlineData("""{ "nope": "2026-08-19" }""")]
    public void Date_BlankMissingOrUnparseable_IsNull(string json) =>
        Assert.Null(PlParse.Date(E(json), "d"));

    // ---------------------------------------------------------------- DateTime2: yyyy-MM-dd HH:mm

    [Fact]
    public void DateTime2_ParsesMinutePrecision_GasProductionReportedDate()
    {
        Assert.Equal(new DateTime(2026, 8, 18, 13, 33, 0),
            PlParse.DateTime2(E("""{ "r": "2026-08-18 13:33" }"""), "r"));
    }

    [Theory]
    [InlineData("""{ "r": "2026-08-18 13:33:45" }""", 2026, 8, 18, 13, 33, 45)]
    [InlineData("""{ "r": "2026-08-18" }""", 2026, 8, 18, 0, 0, 0)]
    public void DateTime2_TolerantVariants(string json, int y, int mo, int d, int h, int mi, int s) =>
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), PlParse.DateTime2(E(json), "r"));

    [Fact]
    public void DateTime2_BlankIsNull() =>
        Assert.Null(PlParse.DateTime2(E("""{ "r": " " }"""), "r"));

    // ---------------------------------------------------------------- DateTimeOffset: "… -05:00" offsets

    [Fact]
    public void DateTimeOffset_ParsesMilliOffset_NoticePostedDate()
    {
        var dto = PlParse.DateTimeOffset(E("""{ "p": "2026-08-18 15:27:29.544 -05:00" }"""), "p");
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 15, 27, 29, 544, TimeSpan.FromHours(-5)), dto);
    }

    [Fact]
    public void DateTimeOffset_ParsesSecondOffset_NoMillis()
    {
        var dto = PlParse.DateTimeOffset(E("""{ "p": "2026-08-18 10:57:00 -08:00" }"""), "p");
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 10, 57, 0, TimeSpan.FromHours(-8)), dto);
    }

    [Fact]
    public void DateTimeOffset_MissingIsNull() =>
        Assert.Null(PlParse.DateTimeOffset(E("""{ "nope": "x" }"""), "p"));

    // ---------------------------------------------------------------- String: trim + blank/space → null

    [Theory]
    [InlineData("""{ "s": "  Amoco  " }""", "Amoco")]
    [InlineData("""{ "s": "10399" }""", "10399")]
    public void String_TrimsValue(string json, string expected) =>
        Assert.Equal(expected, PlParse.String(E(json), "s"));

    [Theory]
    [InlineData("""{ "s": " " }""")]   // pointmetadata drn/county/region blanks
    [InlineData("""{ "s": "" }""")]    // locprop empty
    [InlineData("""{ "s": null }""")]
    [InlineData("""{ "nope": "x" }""")]
    public void String_BlankMissingOrNull_IsNull(string json) =>
        Assert.Null(PlParse.String(E(json), "s"));

    // ---------------------------------------------------------------- culture-invariance (comma-decimal culture)

    [Fact]
    public void Parsers_AreInvariant_UnderCommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE uses ',' as the decimal separator and '.' as the thousands separator — a naive
            // CurrentCulture parse of "1.5" would yield 15. The readers pin InvariantCulture.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(1.5m, PlParse.Decimal(E("""{ "v": "1.5" }"""), "v"));
            Assert.Equal(-1113.0385m, PlParse.Decimal(E("""{ "v": "-1113.0385" }"""), "v"));
            Assert.Equal(0m, PlParse.Decimal(E("""{ "v": "0.00000000" }"""), "v"));
            Assert.Equal(new DateOnly(2026, 8, 19), PlParse.Date(E("""{ "d": "2026-08-19" }"""), "d"));
            Assert.Equal(new DateOnly(2026, 7, 19), PlParse.Date(E("""{ "d": "07/19/2026" }"""), "d"));
            Assert.Equal(new DateTime(2026, 8, 18, 13, 33, 0), PlParse.DateTime2(E("""{ "r": "2026-08-18 13:33" }"""), "r"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
