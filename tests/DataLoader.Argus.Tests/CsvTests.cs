using Xunit;

namespace DataLoader.Argus.Tests;

/// <summary>
/// <see cref="ArgusCsv"/> — the RFC 4180 reader and the scalar converters.
/// </summary>
public sealed class CsvTests
{
    // ------------------------------------------------------------------ record splitting

    [Fact]
    public void Splits_a_plain_crlf_document()
    {
        var records = ArgusCsv.ParseRecords("A,B,C\r\n1,2,3\r\n4,5,6\r\n");

        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "A", "B", "C" }, records[0]);
        Assert.Equal(new[] { "4", "5", "6" }, records[2]);
    }

    [Fact]
    public void Handles_a_file_that_does_not_end_with_a_newline()
    {
        var records = ArgusCsv.ParseRecords("A,B\r\n1,2");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    [Fact]
    public void Handles_lf_only_and_lone_cr_line_endings()
    {
        Assert.Equal(2, ArgusCsv.ParseRecords("A,B\n1,2\n").Count);
        Assert.Equal(2, ArgusCsv.ParseRecords("A,B\r1,2\r").Count);
    }

    [Fact]
    public void Skips_blank_lines_rather_than_emitting_empty_records()
    {
        var records = ArgusCsv.ParseRecords("A,B\r\n\r\n1,2\r\n\r\n");

        Assert.Equal(2, records.Count);
    }

    /// <summary>
    /// The live trigger for quoting: a value with trailing whitespace. Trimming
    /// after unquoting is load-bearing because several such columns are PK parts.
    /// </summary>
    [Fact]
    public void Unquotes_and_trims_padded_values()
    {
        var records = ArgusCsv.ParseRecords("A,B\r\nx,\"Argus NPKs \"\r\n");

        Assert.Equal("Argus NPKs", records[1][1]);
    }

    [Fact]
    public void A_quoted_field_of_only_spaces_becomes_empty()
    {
        // latestCodes.csv really ships `," "` for Specification.
        var records = ArgusCsv.ParseRecords("A,B\r\nx,\" \"\r\n");

        Assert.Equal(string.Empty, records[1][1]);
    }

    /// <summary>
    /// Not observed live, but the reason the parser is quote-aware: a naive comma
    /// split would shift every later column one place left, and because the TVP
    /// binds by position that corrupts rows instead of failing them.
    /// </summary>
    [Fact]
    public void Keeps_a_comma_inside_a_quoted_field()
    {
        var records = ArgusCsv.ParseRecords("A,B,C\r\n1,\"two, and a half\",3\r\n");

        Assert.Equal(3, records[1].Length);
        Assert.Equal("two, and a half", records[1][1]);
    }

    [Fact]
    public void Keeps_a_newline_inside_a_quoted_field()
    {
        var records = ArgusCsv.ParseRecords("A,B\r\n1,\"line one\r\nline two\"\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal("line one\r\nline two", records[1][1]);
    }

    [Fact]
    public void Doubled_quotes_inside_a_quoted_field_become_one_literal_quote()
    {
        var records = ArgusCsv.ParseRecords("A\r\n\"say \"\"hi\"\"\"\r\n");

        Assert.Equal("say \"hi\"", records[1][0]);
    }

    [Fact]
    public void Preserves_trailing_empty_fields()
    {
        var records = ArgusCsv.ParseRecords("A,B,C\r\n1,,\r\n");

        Assert.Equal(3, records[1].Length);
        Assert.Equal(string.Empty, records[1][2]);
    }

    // ------------------------------------------------------------------ header map

    [Fact]
    public void Header_map_is_case_insensitive()
    {
        var map = ArgusCsv.BuildHeaderMap(new[] { "TimeStampID", "Code" });

        Assert.Equal(0, map["timestampid"]);
        Assert.Equal(1, map["CODE"]);
    }

    [Fact]
    public void Header_map_keeps_the_first_of_a_duplicated_header()
    {
        var map = ArgusCsv.BuildHeaderMap(new[] { "Code", "Code" });

        Assert.Equal(0, map["Code"]);
    }

    // ------------------------------------------------------------------ conversion

    [Fact]
    public void Blank_is_null_not_a_failure()
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Int16, "", out var value));
        Assert.Equal(DBNull.Value, value);
    }

    [Theory]
    [InlineData("26-Aug-2026", 2026, 8, 26)]
    [InlineData("01-Jan-1990", 1990, 1, 1)]
    [InlineData("13-Apr-2022", 2022, 4, 13)]
    public void Parses_the_feeds_dd_MMM_yyyy_dates(string raw, int year, int month, int day)
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Date, raw, out var value));
        Assert.Equal(new DateTime(year, month, day), value);
    }

    /// <summary>
    /// Pinned to InvariantCulture — a machine whose regional settings expect a
    /// different month name must not silently reinterpret or reject these.
    /// </summary>
    [Fact]
    public void Date_parsing_is_culture_invariant()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Date, "26-Aug-2026", out var value));
            Assert.Equal(new DateTime(2026, 8, 26), value);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Rejects_a_non_date_rather_than_guessing()
    {
        Assert.False(ArgusCsv.TryConvert(ArgusColumnType.Date, "not-a-date", out var value));
        Assert.Equal(DBNull.Value, value);
    }

    [Fact]
    public void Parses_local_time_to_a_timespan()
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Time, "20:00:00", out var value));
        Assert.Equal(new TimeSpan(20, 0, 0), value);
    }

    /// <summary>The live maximum is 17 decimal places; nothing may be rounded away.</summary>
    [Fact]
    public void Keeps_full_decimal_scale()
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Decimal, "0.00334112930170398", out var value));
        Assert.Equal(0.00334112930170398m, value);
    }

    [Theory]
    [InlineData("-24.5")]
    [InlineData("575000")]
    [InlineData("96.040")]
    public void Parses_the_observed_value_range(string raw)
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Decimal, raw, out _));
    }

    /// <summary>
    /// latestNewsCategory.CATEGORY_ID reaches 10 000 004 936. Int32 would overflow,
    /// which is why that column is Int64 in the descriptor and BIGINT in SQL.
    /// </summary>
    [Fact]
    public void Int64_holds_the_news_category_id_that_overflows_int32()
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Int64, "10000004936", out var value));
        Assert.Equal(10000004936L, value);

        Assert.False(ArgusCsv.TryConvert(ArgusColumnType.Int32, "10000004936", out _));
    }

    [Fact]
    public void Int16_rejects_an_out_of_range_value()
    {
        Assert.False(ArgusCsv.TryConvert(ArgusColumnType.Int16, "70000", out _));
    }

    [Fact]
    public void Char_keeps_a_single_character()
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Char, "N", out var value));
        Assert.Equal("N", value);
    }

    [Fact]
    public void Char_clips_rather_than_failing_on_a_longer_value()
    {
        Assert.True(ArgusCsv.TryConvert(ArgusColumnType.Char, "NX", out var value));
        Assert.Equal("N", value);
    }
}
