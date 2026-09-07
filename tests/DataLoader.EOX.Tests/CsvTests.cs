using System.Globalization;
using Xunit;

namespace DataLoader.EOX.Tests;

/// <summary>
/// The CSV layer: record splitting, header mapping, and cell conversion. These run
/// against <see cref="EoxCsv"/> directly, so a change in the reader cannot hide a
/// regression here.
/// </summary>
public sealed class CsvTests
{
    // ---------------------------------------------------------------- records

    [Fact]
    public void Crlf_records_split_and_the_trailing_newline_is_not_a_row()
    {
        var records = EoxCsv.ParseRecords(Samples.CrudeOil2026);

        // 1 header + 4 data rows, and NOT a fifth empty record from the final CRLF.
        Assert.Equal(5, records.Count);
        Assert.Equal(13, records[0].Length);
        Assert.All(records, r => Assert.Equal(13, r.Length));
    }

    [Fact]
    public void Blank_lines_anywhere_are_skipped()
    {
        var records = EoxCsv.ParseRecords("a,b\r\n\r\n1,2\r\n\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "a", "b" }, records[0]);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    [Fact]
    public void A_final_record_without_a_trailing_newline_is_still_returned()
    {
        var records = EoxCsv.ParseRecords("a,b\r\n1,2");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    /// <summary>
    /// The live feed never quotes, but the TVP binds by position: an embedded comma
    /// handled naively would shift every following column one place left and write
    /// plausible garbage instead of failing.
    /// </summary>
    [Fact]
    public void Quoted_fields_keep_embedded_commas_newlines_and_doubled_quotes_together()
    {
        var records = EoxCsv.ParseRecords("a,b,c\r\n1,\"Gulf Coast, Midland\",3\r\n");

        Assert.Equal(3, records[1].Length);
        Assert.Equal("Gulf Coast, Midland", records[1][1]);

        var multiline = EoxCsv.ParseRecords("a,b\r\n1,\"two\r\nlines\"\r\n");
        Assert.Equal(2, multiline.Count);
        Assert.Equal("two\r\nlines", multiline[1][1]);

        var doubled = EoxCsv.ParseRecords("a\r\n\"say \"\"hi\"\"\"\r\n");
        Assert.Equal("say \"hi\"", doubled[1][0]);
    }

    [Fact]
    public void Values_are_trimmed_after_unquoting()
    {
        var records = EoxCsv.ParseRecords("a,b\r\n  M01  ,\"  padded  \"\r\n");

        Assert.Equal("M01", records[1][0]);
        Assert.Equal("padded", records[1][1]);
    }

    // ---------------------------------------------------------------- headers

    [Fact]
    public void Header_map_is_case_insensitive_and_keeps_the_first_of_a_duplicate()
    {
        var map = EoxCsv.BuildHeaderMap(new[] { "Curve_Date", "Mid", "MID" });

        Assert.Equal(0, map["curve_date"]);
        Assert.Equal(1, map["mid"]);   // first wins, not the later duplicate
    }

    /// <summary>
    /// The whole point of alternate headers: the 2014 CrudeOil file heads column 1
    /// <c>Number</c>, so a loader that only knew <c>Line</c> would reject it.
    /// </summary>
    [Fact]
    public void Resolve_index_falls_back_through_the_alternate_headers_in_order()
    {
        var line = EoxDescriptors.CrudeOil.Columns[EoxDescriptors.CrudeOil.Ordinal("Line")];

        var modern = EoxCsv.BuildHeaderMap(new[] { "Line", "Code" });
        var legacy = EoxCsv.BuildHeaderMap(new[] { "Number", "Code" });
        var neither = EoxCsv.BuildHeaderMap(new[] { "Code" });

        Assert.Equal(0, EoxCsv.ResolveIndex(modern, line));
        Assert.Equal(0, EoxCsv.ResolveIndex(legacy, line));
        Assert.Equal(-1, EoxCsv.ResolveIndex(neither, line));
    }

    /// <summary>
    /// When BOTH spellings are present the descriptor's preference order decides.
    /// NGL prefers <c>Data_Code</c>; CrudeOil prefers <c>Code</c>. Getting this
    /// backwards would silently load the wrong column on a file that has both.
    /// </summary>
    [Fact]
    public void Preference_order_decides_when_both_spellings_are_present()
    {
        var map = EoxCsv.BuildHeaderMap(new[] { "Code", "Data_Code" });

        var nglCode = EoxDescriptors.Ngl.Columns[EoxDescriptors.Ngl.Ordinal("Code")];
        var crudeCode = EoxDescriptors.CrudeOil.Columns[EoxDescriptors.CrudeOil.Ordinal("Code")];

        Assert.Equal(1, EoxCsv.ResolveIndex(map, nglCode));     // Data_Code
        Assert.Equal(0, EoxCsv.ResolveIndex(map, crudeCode));   // Code
    }

    // ---------------------------------------------------------------- conversion

    [Theory]
    [InlineData("2026-09-04", 2026, 9, 4)]     // CrudeOil / NGL
    [InlineData("09/04/26", 2026, 9, 4)]       // NaturalGas
    [InlineData("9/4/26", 2026, 9, 4)]
    [InlineData("12/31/36", 2036, 12, 31)]     // longest tenor actually published
    public void Both_published_date_shapes_parse(string raw, int year, int month, int day)
    {
        Assert.True(EoxCsv.TryConvert(EoxColumnType.Date, raw, out var value));
        Assert.Equal(new DateTime(year, month, day), (DateTime)value);
    }

    /// <summary>
    /// THE two-digit-year trap. .NET's default <c>TwoDigitYearMax</c> is 2049, so
    /// <c>ParseExact("06/30/50", "MM/dd/yy")</c> yields <b>1950</b>. NaturalGas
    /// publishes contract ends in this format and the tenor grows every year, so
    /// this is a dated bug, not a hypothetical one.
    /// </summary>
    [Theory]
    [InlineData("06/30/50", 2050)]
    [InlineData("06/30/99", 2099)]
    [InlineData("06/30/00", 2000)]
    [InlineData("06/30/11", 2011)]   // the oldest year EOX has published
    public void Two_digit_years_always_land_in_the_twenty_first_century(string raw, int expectedYear)
    {
        Assert.True(EoxCsv.TryConvert(EoxColumnType.Date, raw, out var value));
        Assert.Equal(expectedYear, ((DateTime)value).Year);
    }

    /// <summary>Proves the test above is not vacuous: the framework really does say 1950.</summary>
    [Fact]
    public void The_framework_default_really_would_have_read_1950()
    {
        Assert.True(DateTime.TryParseExact(
            "06/30/50", "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var framework));

        Assert.Equal(1950, framework.Year);
    }

    [Theory]
    [InlineData("06/30/2050")]   // already four digits
    [InlineData("2050-06-30")]
    [InlineData("")]
    [InlineData("notadate")]
    public void Expanding_a_two_digit_year_leaves_everything_else_untouched(string raw)
    {
        Assert.Equal(raw, EoxCsv.ExpandTwoDigitYear(raw));
    }

    [Theory]
    [InlineData("93.639", 93.639)]
    [InlineData("-15.804", -15.804)]     // Canada WCS trades at a discount
    [InlineData("0", 0d)]
    [InlineData("2.6691", 2.6691)]
    public void Prices_parse_including_negatives(string raw, double expected)
    {
        Assert.True(EoxCsv.TryConvert(EoxColumnType.Double, raw, out var value));
        Assert.Equal(expected, (double)value, 6);
    }

    [Fact]
    public void A_blank_cell_is_null_and_not_a_parse_failure()
    {
        Assert.True(EoxCsv.TryConvert(EoxColumnType.Double, "", out var number));
        Assert.Equal(DBNull.Value, number);

        Assert.True(EoxCsv.TryConvert(EoxColumnType.Date, "", out var date));
        Assert.Equal(DBNull.Value, date);

        Assert.True(EoxCsv.TryConvert(EoxColumnType.String, "", out var text));
        Assert.Equal(DBNull.Value, text);
    }

    [Theory]
    [InlineData(EoxColumnType.Double, "n/a")]
    [InlineData(EoxColumnType.Double, "1,234.5")]   // thousands separators are not this feed's dialect
    [InlineData(EoxColumnType.Double, "NaN")]
    [InlineData(EoxColumnType.Double, "Infinity")]
    [InlineData(EoxColumnType.Date, "31/12/2026")]  // dd/MM is not a shape EOX publishes
    public void A_non_blank_cell_that_cannot_convert_reports_failure(EoxColumnType type, string raw)
    {
        Assert.False(EoxCsv.TryConvert(type, raw, out var value));
        Assert.Equal(DBNull.Value, value);
    }
}
