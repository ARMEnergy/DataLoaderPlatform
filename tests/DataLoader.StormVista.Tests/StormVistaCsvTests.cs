using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// The pure, quote-aware RFC-4180 splitter and the invariant-culture value parsers
/// (<see cref="StormVistaCsv"/>). This is the highest-value pure-logic area, so it
/// is covered directly (the type is internal, reached via InternalsVisibleTo).
/// </summary>
public class StormVistaCsvTests
{
    // ------------------------------------------------------------------ Parse split

    [Fact]
    public void Parse_QuotedFlagHeader_IsOneFieldNotThree()
    {
        // The daily flag header is quoted and contains spaces — a naive comma split
        // would shatter it. Quote-awareness must keep it as a single 3rd field.
        var csv = "Date,Value,\"Flag (0=obs 1=fcst 2=norm)\"\n2024-07-28,12.105,0\n";

        var records = StormVistaCsv.Parse(csv);

        Assert.Equal(2, records.Count);
        Assert.Equal(3, records[0].Length);
        Assert.Equal("Date", records[0][0]);
        Assert.Equal("Value", records[0][1]);
        Assert.Equal("Flag (0=obs 1=fcst 2=norm)", records[0][2]);
        Assert.Equal(new[] { "2024-07-28", "12.105", "0" }, records[1]);
    }

    [Fact]
    public void Parse_StripsLeadingUtf8Bom()
    {
        // A BOM-prefixed body must not corrupt header[0]; otherwise the regional
        // header[0] == "Date" check would fail on a perfectly valid file.
        var csv = "﻿Date,West,East\n2024-08-04,1.0,2.0\n";

        var records = StormVistaCsv.Parse(csv);

        Assert.Equal("Date", records[0][0]);   // not "﻿Date"
        Assert.Equal(3, records[0].Length);
    }

    [Fact]
    public void Parse_DropsFullyBlankLines()
    {
        var csv = "Date,Value,Flag\n\n2024-07-28,12.1,0\n\n";

        var records = StormVistaCsv.Parse(csv);

        Assert.Equal(2, records.Count); // header + one data row; blank lines dropped
    }

    [Fact]
    public void Parse_HandlesCrLfAndTrailingRecordWithoutNewline()
    {
        var csv = "Date,Value,Flag\r\n2024-07-28,12.1,0\r\n2024-07-29,13.2,1"; // no trailing newline

        var records = StormVistaCsv.Parse(csv);

        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "2024-07-29", "13.2", "1" }, records[2]);
    }

    [Fact]
    public void Parse_DoubledQuoteEscape_YieldsLiteralQuote()
    {
        var csv = "a,\"he said \"\"hi\"\"\",c\n";

        var records = StormVistaCsv.Parse(csv);

        Assert.Equal(new[] { "a", "he said \"hi\"", "c" }, records[0]);
    }

    [Fact]
    public void Parse_EmbeddedCommaInQuotedField_IsNotASeparator()
    {
        var csv = "\"a,b\",c\n";

        var records = StormVistaCsv.Parse(csv);

        Assert.Equal(new[] { "a,b", "c" }, records[0]);
    }

    [Fact]
    public void Parse_HeaderOnly_YieldsSingleRecord()
    {
        var records = StormVistaCsv.Parse("Date,Value,\"Flag (0=obs 1=fcst 2=norm)\"\n");
        Assert.Single(records);
    }

    [Fact]
    public void Parse_Empty_YieldsNoRecords()
    {
        Assert.Empty(StormVistaCsv.Parse(string.Empty));
    }

    // ------------------------------------------------------------------ TryParseDate

    [Theory]
    [InlineData("2024-07-28", 2024, 7, 28)]
    [InlineData(" 2024-08-04 ", 2024, 8, 4)] // surrounding whitespace trimmed
    public void TryParseDate_ValidIsoDate_Parses(string input, int y, int m, int d)
    {
        Assert.True(StormVistaCsv.TryParseDate(input, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("2024/07/28")]   // wrong separator
    [InlineData("07-28-2024")]   // wrong order (US)
    [InlineData("not-a-date")]
    public void TryParseDate_InvalidOrBlank_ReturnsFalse(string? input)
    {
        Assert.False(StormVistaCsv.TryParseDate(input, out _));
    }

    // ------------------------------------------------------------------ TryParseDecimal

    [Theory]
    [InlineData("12.105", 12.105)]
    [InlineData("-3.5", -3.5)]
    [InlineData(" 27.11 ", 27.11)]
    public void TryParseDecimal_ValidInvariant_Parses(string input, double expected)
    {
        Assert.True(StormVistaCsv.TryParseDecimal(input, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("NaN")]
    [InlineData("abc")]
    public void TryParseDecimal_BlankOrUnparseable_ReturnsFalse(string? input)
    {
        Assert.False(StormVistaCsv.TryParseDecimal(input, out _));
    }

    // ------------------------------------------------------------------ TryParseFlag

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData(" 1 ", 1)]
    public void TryParseFlag_InDomain_Parses(string input, int expected)
    {
        Assert.True(StormVistaCsv.TryParseFlag(input, out var flag));
        Assert.Equal(expected, flag);
    }

    [Theory]
    [InlineData("3")]    // out of {0,1,2}
    [InlineData("-1")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseFlag_OutOfDomainOrUnparseable_ReturnsFalse(string? input)
    {
        Assert.False(StormVistaCsv.TryParseFlag(input, out _));
    }
}
