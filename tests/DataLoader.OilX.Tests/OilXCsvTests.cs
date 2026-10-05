using System.Text;
using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// The streaming RFC-4180 parser.
///
/// <para>
/// The quoting cases are the ones that matter: 3,447 of CargoTracking's 396,866 rows
/// carry a quoted field with an embedded comma, and because the TVP binds BY POSITION a
/// mis-split shifts every later column and writes plausible garbage rather than failing.
/// </para>
/// <para>
/// The buffer-boundary cases matter for a different reason: this parser reads in 64 KB
/// chunks, so a quote or a CR landing on the last character of a chunk exercises state
/// that a whole-document parser never has. <see cref="Read"/> forces those boundaries at
/// every offset.
/// </para>
/// </summary>
public sealed class OilXCsvTests
{
    private static List<string[]> Read(string text) =>
        OilXCsv.ReadRecords(new StringReader(text)).ToList();

    /// <summary>
    /// Re-read the same document through a reader that hands back at most
    /// <paramref name="chunk"/> characters at a time, so every internal boundary is
    /// exercised.
    /// </summary>
    private static List<string[]> ReadChunked(string text, int chunk) =>
        OilXCsv.ReadRecords(new ChokedReader(text, chunk)).ToList();

    [Fact]
    public void Splits_a_plain_record()
    {
        var records = Read("a,b,c\r\n1,2,3\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "a", "b", "c" }, records[0]);
        Assert.Equal(new[] { "1", "2", "3" }, records[1]);
    }

    [Fact]
    public void Keeps_an_embedded_comma_inside_a_quoted_field()
    {
        var records = Read("a,b\r\n\"Bonaire, Sint Eustatius and Saba\",x\r\n");

        Assert.Equal(new[] { "Bonaire, Sint Eustatius and Saba", "x" }, records[1]);
    }

    /// <summary>
    /// The real row. Two separate quoted country names, each with a comma — so the row
    /// has 52 fields, and FlowID must still land in position 47.
    /// </summary>
    [Fact]
    public void Parses_the_real_quoted_CargoTracking_row_without_shifting_columns()
    {
        var records = Read(Samples.CargoTrackingHeader + "\r\n" + Samples.CargoTrackingQuotedRow + "\r\n");

        var header = records[0];
        var row = records[1];

        Assert.Equal(52, header.Length);
        Assert.Equal(52, row.Length);

        var map = OilXCsv.BuildHeaderMap(header);

        Assert.Equal("Bonaire, Sint Eustatius and Saba", row[map["DischargeCountry"]]);
        Assert.Equal("Bonaire, Sint Eustatius and Saba", row[map["DestinationCountry"]]);
        Assert.Equal("ab8122f46016ce3c1dfb857aade40e0f7efcedd60e2d597e85023e0a36ee96f8", row[map["FlowID"]]);
        Assert.Equal("2026-09-30", row[map["RunDate"]]);
        Assert.Equal("84.45", row[map["LoadQuantity(KT)"]]);
        Assert.Equal("681", row[map["LoadQuantity(KBBL)"]]);
    }

    [Fact]
    public void Doubled_quotes_become_one_literal_quote()
    {
        var records = Read("a\r\n\"he said \"\"hi\"\"\"\r\n");

        Assert.Equal("he said \"hi\"", records[1][0]);
    }

    [Fact]
    public void A_newline_inside_a_quoted_field_does_not_split_the_record()
    {
        var records = Read("a,b\r\n\"line one\r\nline two\",x\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal("line one\r\nline two", records[1][0]);
        Assert.Equal("x", records[1][1]);
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\n")]
    [InlineData("a,b\n1,2\n")]
    [InlineData("a,b\r1,2\r")]
    public void Handles_crlf_lf_and_bare_cr(string text)
    {
        var records = Read(text);

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    [Fact]
    public void Skips_blank_lines_including_the_trailing_newline()
    {
        var records = Read("a,b\r\n\r\n1,2\r\n\r\n\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    [Fact]
    public void Yields_the_final_record_when_the_file_does_not_end_with_a_newline()
    {
        var records = Read("a,b\r\n1,2");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        Assert.Equal(new[] { "1", "2" }, Read("a,b\r\n  1 ,\t2  \r\n")[1]);
    }

    [Fact]
    public void An_empty_cell_is_an_empty_string_not_a_missing_field()
    {
        Assert.Equal(new[] { "1", "", "3" }, Read("a,b,c\r\n1,,3\r\n")[1]);
    }

    /// <summary>
    /// ⚠ The streaming-specific case. A quote, a CR or a CRLF pair landing exactly on a
    /// chunk boundary is state the whole-document parser never carries; reading the same
    /// document at every chunk size from 1 upward proves the carry is right.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(64)]
    public void Chunk_boundaries_do_not_change_the_result(int chunk)
    {
        const string text =
            "a,b,c\r\n" +
            "\"Bonaire, Sint Eustatius and Saba\",\"he said \"\"hi\"\"\",3\r\n" +
            "\"line one\r\nline two\",,6\r\n" +
            "7,8,9";

        Assert.Equal(Read(text), ReadChunked(text, chunk));
    }

    [Fact]
    public void Chunk_boundaries_do_not_change_the_real_sample()
    {
        var expected = Read(Samples.CargoTrackingCsv);

        foreach (var chunk in new[] { 1, 2, 3, 17, 64, 997 })
            Assert.Equal(expected, ReadChunked(Samples.CargoTrackingCsv, chunk));
    }

    [Fact]
    public void An_unterminated_quote_at_eof_still_yields_the_row()
    {
        var records = Read("a,b\r\n1,\"unterminated");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "unterminated" }, records[1]);
    }

    [Fact]
    public void Header_map_is_case_insensitive_and_keeps_the_first_duplicate()
    {
        var map = OilXCsv.BuildHeaderMap(new[] { "Alpha", "Beta", "ALPHA" });

        Assert.Equal(0, map["alpha"]);
        Assert.Equal(1, map["BETA"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Resolve_index_tries_source_headers_in_preference_order()
    {
        var column = OilXColumn.Str("GroupByDateIndicator", 50, "GroupByDateIndicator", "GroupbyDateIndicator");

        // Only the vendor's lower-case-b spelling is present.
        var map = OilXCsv.BuildHeaderMap(new[] { "x", "GroupbyDateIndicator" });
        Assert.Equal(1, OilXCsv.ResolveIndex(map, column));

        // Neither present.
        Assert.Equal(-1, OilXCsv.ResolveIndex(OilXCsv.BuildHeaderMap(new[] { "x" }), column));
    }

    // ------------------------------------------------------------- conversion

    [Fact]
    public void A_blank_cell_is_null_not_a_parse_failure()
    {
        Assert.True(OilXCsv.TryConvert(OilXColumnType.Decimal, "", out var value));
        Assert.Equal(DBNull.Value, value);
    }

    [Theory]
    [InlineData("2026-09-30", 2026, 9, 30, 0, 0, 0, 0)]
    [InlineData("2015-01-28 03:56:20", 2015, 1, 28, 3, 56, 20, 0)]
    [InlineData("2015-12-11 01:10:30.714", 2015, 12, 11, 1, 10, 30, 714)]
    [InlineData("2026-09-30 03:00:01.237916", 2026, 9, 30, 3, 0, 1, 237)]
    public void Parses_every_datetime_shape_the_feeds_publish(
        string raw, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        Assert.True(OilXCsv.TryConvert(OilXColumnType.DateTime, raw, out var value));

        var actual = Assert.IsType<DateTime>(value);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), new DateTime(actual.Year, actual.Month, actual.Day,
            actual.Hour, actual.Minute, actual.Second));
        Assert.Equal(ms, actual.Millisecond);
    }

    [Fact]
    public void A_date_column_drops_the_time_component()
    {
        Assert.True(OilXCsv.TryConvert(OilXColumnType.Date, "2015-01-28 03:56:20", out var value));

        Assert.Equal(new DateTime(2015, 1, 28), value);
    }

    /// <summary>
    /// ⚠ Rounding to the column's scale is required, not cosmetic: a 9-dp value against a
    /// DECIMAL(18,8) parameter raises "Parameter value out of range" and kills the whole
    /// 20,000-row batch, not just the cell.
    /// </summary>
    [Fact]
    public void Decimals_are_rounded_to_the_column_scale()
    {
        Assert.True(OilXCsv.TryConvert(OilXColumnType.Decimal, "1.123456789", out var value));

        Assert.Equal(1.12345679m, value);
        Assert.Equal(OilXCsv.DecimalScale, decimal.Round((decimal)value, OilXCsv.DecimalScale).Scale);
    }

    [Theory]
    [InlineData("195")]
    [InlineData("5.40")]
    [InlineData("-1E-05")]
    [InlineData("0.0")]
    public void Decimals_accept_the_shapes_the_feeds_publish(string raw)
    {
        Assert.True(OilXCsv.TryConvert(OilXColumnType.Decimal, raw, out _));
    }

    [Fact]
    public void Doubles_reject_NaN_and_infinity()
    {
        Assert.False(OilXCsv.TryConvert(OilXColumnType.Double, "NaN", out _));
        Assert.False(OilXCsv.TryConvert(OilXColumnType.Double, "Infinity", out _));
    }

    [Fact]
    public void A_non_blank_cell_that_cannot_convert_reports_failure_and_yields_null()
    {
        Assert.False(OilXCsv.TryConvert(OilXColumnType.Decimal, "not-a-number", out var value));
        Assert.Equal(DBNull.Value, value);
    }

    [Fact]
    public void Derived_column_types_are_never_parsed_from_a_cell()
    {
        Assert.Throws<InvalidOperationException>(
            () => OilXCsv.TryConvert(OilXColumnType.Guid, "x", out _));
        Assert.Throws<InvalidOperationException>(
            () => OilXCsv.TryConvert(OilXColumnType.Int, "1", out _));
    }

    /// <summary>A reader that never returns more than <c>chunk</c> characters per call.</summary>
    private sealed class ChokedReader : TextReader
    {
        private readonly string _text;
        private readonly int _chunk;
        private int _position;

        public ChokedReader(string text, int chunk) { _text = text; _chunk = Math.Max(1, chunk); }

        public override int Read(char[] buffer, int index, int count)
        {
            var remaining = _text.Length - _position;
            if (remaining <= 0) return 0;

            var take = Math.Min(Math.Min(count, _chunk), remaining);
            _text.CopyTo(_position, buffer, index, take);
            _position += take;
            return take;
        }

        public override int Peek() => _position < _text.Length ? _text[_position] : -1;

        public override int Read() => _position < _text.Length ? _text[_position++] : -1;
    }
}
