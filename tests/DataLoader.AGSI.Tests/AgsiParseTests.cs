using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// <see cref="AgsiParse"/> — tolerant, invariant-culture parsers (design §4). Every
/// numeric measure arrives as a JSON string; a blank / missing / unparseable value
/// becomes <c>null</c> (never a row failure). Signs are preserved. Parsing is
/// invariant-culture: a comma-decimal input does NOT silently parse.
/// </summary>
public class AgsiParseTests
{
    // ---------------------------------------------------------------- Decimal

    [Theory]
    [InlineData("121.1238", 121.1238)]
    [InlineData("903.9000", 903.9)]
    [InlineData("6.5", 6.5)]
    [InlineData("100", 100)]
    [InlineData("  49.14  ", 49.14)] // leading/trailing whitespace tolerated
    public void Decimal_ParsesValidInvariantNumbers(string input, double expected)
    {
        Assert.Equal((decimal)expected, AgsiParse.Decimal(input));
    }

    [Theory]
    [InlineData("-537.3", -537.3)]
    [InlineData("+5", 5)]
    [InlineData("-0.24", -0.24)]
    public void Decimal_PreservesSign(string input, double expected)
    {
        Assert.Equal((decimal)expected, AgsiParse.Decimal(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notanumber")]
    [InlineData("N/A")]
    public void Decimal_BlankMissingOrUnparseable_IsNull(string? input)
    {
        Assert.Null(AgsiParse.Decimal(input));
    }

    [Fact]
    public void Decimal_CommaDecimalInput_DoesNotSilentlyParse_IsNull()
    {
        // Invariant culture + NumberStyles.Float (no thousands): "1,5" is NOT a valid number.
        // Confirmed actual behaviour: it does NOT become 1.5 and does NOT become 15 — it is null.
        Assert.Null(AgsiParse.Decimal("1,5"));
        Assert.NotEqual(1.5m, AgsiParse.Decimal("1,5") ?? -1m);
    }

    // ---------------------------------------------------------------- Date (yyyy-MM-dd)

    [Fact]
    public void Date_ParsesIsoDate()
    {
        Assert.Equal(new DateOnly(2026, 8, 13), AgsiParse.Date("2026-08-13"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026/08/13")]   // wrong separator — exact format only
    [InlineData("13-08-2026")]   // wrong order
    [InlineData("2026-08-13 00:00:00")]
    public void Date_BlankOrWrongFormat_IsNull(string? input)
    {
        Assert.Null(AgsiParse.Date(input));
    }

    // ---------------------------------------------------------------- DateTime (yyyy-MM-dd HH:mm:ss)

    [Fact]
    public void DateTime_ParsesGieWallClock()
    {
        Assert.Equal(new DateTime(2026, 8, 17, 8, 0, 55), AgsiParse.DateTime("2026-08-17 08:00:55"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-08-17")]           // date only, no time
    [InlineData("2026-08-17T08:00:55")]  // ISO 'T' separator not accepted (exact format)
    public void DateTime_BlankOrWrongFormat_IsNull(string? input)
    {
        Assert.Null(AgsiParse.DateTime(input));
    }
}
