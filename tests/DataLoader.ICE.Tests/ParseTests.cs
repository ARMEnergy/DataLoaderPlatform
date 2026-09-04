using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// Parsing and header mapping, against real captured rows.
/// </summary>
public sealed class ParseTests
{
    private static (List<IceRow> Rows, int Dropped) Parse(string feedId, string content) =>
        TestHelpers.Reader(IceDescriptors.Find(feedId)!)
                   .Parse(TestHelpers.Bytes(content), "https://downloads.ice.com/sample.dat");

    // ------------------------------------------------------------------ mapping

    /// <summary>
    /// ⚠ The core mapping hazard. The file's column order is
    /// <c>TRADE DATE, HUB, PRODUCT, STRIP, CONTRACT, …</c> while the table's is
    /// <c>TradeDate, Contract, ContractType, Strip, ProductId, Hub, Product, …</c>.
    /// A positional load would put the hub in Contract and corrupt the PK of every row.
    /// </summary>
    [Fact]
    public void Columns_are_mapped_by_header_name_not_position()
    {
        var table = IceDescriptors.EnvFuturesTable;
        var (rows, _) = Parse("EnvFutures", Samples.PhysEnv);

        var row = rows[0];

        Assert.Equal("ACA", row.Value(table, "Contract"));                 // 5th in the file
        Assert.Equal("CCA", row.Value(table, "Hub"));                      // 2nd in the file
        Assert.Equal("CCA ACP Advance Futures", row.Value(table, "Product"));
        Assert.Equal("Feb27", row.Value(table, "Strip"));
        Assert.Equal("F", row.Value(table, "ContractType"));
        Assert.Equal(22323, row.Value(table, "ProductId"));
        Assert.Equal(new DateTime(2026, 8, 28), row.Value(table, "TradeDate"));
        Assert.Equal(new DateTime(2027, 2, 26), row.Value(table, "ExpirationDate"));
        Assert.Equal(0.30000m, row.Value(table, "SettlementPrice"));
    }

    /// <summary>A blank optional value (STRIKE on a futures row) is NULL, not a drop.</summary>
    [Fact]
    public void Blank_optional_value_becomes_null_and_keeps_the_row()
    {
        var table = IceDescriptors.EnvFuturesTable;
        var (rows, dropped) = Parse("EnvFutures", Samples.PhysEnv);

        Assert.Equal(0, dropped);
        Assert.Equal(3, rows.Count);
        Assert.Equal(DBNull.Value, rows[0].Value(table, "Strike"));
    }

    [Fact]
    public void SourcePath_is_stamped_on_every_row()
    {
        var table = IceDescriptors.EnvFuturesTable;
        var reader = TestHelpers.Reader(IceDescriptors.Find("EnvFutures")!);
        var (rows, _) = reader.Parse(TestHelpers.Bytes(Samples.PhysEnv), "https://downloads.ice.com/a/b.dat");

        Assert.All(rows, r => Assert.Equal("https://downloads.ice.com/a/b.dat", r.Value(table, "SourcePath")));
    }

    // ------------------------------------------------------------------ required-drop

    /// <summary>
    /// Every options feed carries rows for the underlying FUTURE with a blank STRIKE.
    /// Strike is a NOT NULL PK column, so those rows are dropped and counted rather
    /// than merged under a blank key.
    /// </summary>
    [Fact]
    public void Blank_required_strike_drops_the_row_and_is_counted()
    {
        var (rows, dropped) = Parse("EnvOptions", Samples.PhysEnvOptions);

        Assert.Equal(1, dropped);          // the 'F' row
        Assert.Equal(2, rows.Count);       // the C and P rows survive
    }

    [Fact]
    public void Surviving_option_rows_keep_their_greeks()
    {
        var table = IceDescriptors.EnvOptionsTable;
        var (rows, _) = Parse("EnvOptions", Samples.PhysEnvOptions);

        var call = rows[0];
        Assert.Equal("C", call.Value(table, "ContractType"));
        Assert.Equal(30.0000m, call.Value(table, "Strike"));
        Assert.Equal(28.50000m, call.Value(table, "OptionVolatility"));
        Assert.Equal(0.75000m, call.Value(table, "DeltaFactor"));
    }

    /// <summary>
    /// The multi-leg spread strip is exactly why Strip stays VARCHAR on this table.
    /// It must survive verbatim, not be coerced or dropped.
    /// </summary>
    [Fact]
    public void Spread_strip_is_kept_verbatim_on_varchar_tables()
    {
        var table = IceDescriptors.EnvOptionsTable;
        var (rows, _) = Parse("EnvOptions", Samples.PhysEnvOptions);

        Assert.Equal("BH26 Apr27 FH26", rows[1].Value(table, "Strip"));
    }

    /// <summary>Same drop rule on the greeks shape, where the marker is PUT_CALL='F'.</summary>
    [Fact]
    public void Greeks_blank_strike_row_is_dropped()
    {
        var table = IceDescriptors.FusFinOptionsTable;
        var (rows, dropped) = Parse("FusFinOptions", Samples.Greeks);

        Assert.Equal(1, dropped);
        var row = Assert.Single(rows);

        Assert.Equal("RS", row.Value(table, "CONTRACT"));
        Assert.Equal(635.0000m, row.Value(table, "STRIKE"));
        Assert.Equal("C", row.Value(table, "PUT_CALL"));
        Assert.Equal(-4.6617m, row.Value(table, "THETA"));
    }

    // ------------------------------------------------------------------ typed strips

    /// <summary>arm.Futures types Strip as DATE; the six feeds publish date-shaped strips.</summary>
    [Fact]
    public void Futures_strip_parses_as_a_date()
    {
        var table = IceDescriptors.FuturesTable;
        var (rows, dropped) = Parse("IceGas", Samples.IceGas);

        Assert.Equal(0, dropped);
        Assert.Equal(new DateTime(2026, 9, 1), rows[0].Value(table, "Strip"));
        Assert.Equal(new DateTime(2026, 10, 1), rows[1].Value(table, "Strip"));
    }

    /// <summary>arm.Options also types Strip as DATE, and drops its blank-strike row.</summary>
    [Fact]
    public void Options_feed_parses_dates_and_drops_the_future_row()
    {
        var table = IceDescriptors.OptionsTable;
        var (rows, dropped) = Parse("GasOptions", Samples.GasOptions);

        Assert.Equal(1, dropped);
        var row = Assert.Single(rows);

        Assert.Equal(new DateTime(2026, 9, 1), row.Value(table, "Strip"));
        Assert.Equal(1.7500m, row.Value(table, "Strike"));
        Assert.Equal(DBNull.Value, row.Value(table, "NetChange"));   // blank in the source
    }

    /// <summary>
    /// EnvFutures keeps Strip as text, so a month code and a daily contract both
    /// survive where a DATE column would have dropped them.
    /// </summary>
    [Fact]
    public void EnvFutures_strip_stays_text()
    {
        var table = IceDescriptors.EnvFuturesTable;
        var (rows, dropped) = Parse("EnvFutures", Samples.PhysEnv);

        Assert.Equal(0, dropped);
        Assert.Equal("Feb27", rows[0].Value(table, "Strip"));
        Assert.Equal("01 Sep 26", rows[2].Value(table, "Strip"));
    }

    // ------------------------------------------------------------------ CSV feeds

    /// <summary>
    /// The Crude Index CSV mixes quoted strings with bare numbers, and its header
    /// order differs from the table's (INDEX_DATE_RANGE is 1st in the file, 6th here).
    /// </summary>
    [Fact]
    public void Crude_index_csv_maps_by_name_across_a_different_column_order()
    {
        var table = IceDescriptors.CrudeIndexTable;
        var (rows, dropped) = Parse("CrudeIndex", Samples.CrudeIndex);

        Assert.Equal(0, dropped);
        Assert.Equal(2, rows.Count);

        var row = rows[0];
        Assert.Equal(273, row.Value(table, "MKTID"));
        Assert.Equal("BGS", row.Value(table, "PCC"));
        Assert.Equal(1416, row.Value(table, "INDEX_ID"));
        Assert.Equal(new DateTime(2026, 8, 28), row.Value(table, "INDEX_DATE"));
        Assert.Equal(-3.6m, row.Value(table, "INDEX_PRICE"));
        Assert.Equal("08/26/2026-09/25/2026", row.Value(table, "INDEX_DATE_RANGE"));
        Assert.Equal("ICE WCS CUS 1a - INDEX - Oct26", row.Value(table, "MKT_DESC"));
    }

    /// <summary>
    /// CREATION_TIME is 12-hour with AM/PM. A 24-hour-only parser would reject every
    /// row in this feed.
    /// </summary>
    [Fact]
    public void Crude_index_parses_12_hour_am_pm_timestamps()
    {
        var table = IceDescriptors.CrudeIndexTable;
        var (rows, _) = Parse("CrudeIndex", Samples.CrudeIndex);

        Assert.Equal(new DateTime(2026, 8, 28, 15, 0, 0), rows[0].Value(table, "CREATION_TIME"));
        Assert.Equal(new DateTime(2026, 8, 28, 15, 0, 0), rows[0].Value(table, "LAST_UPDATE_TIME"));
    }

    /// <summary>DEAL_ID overflows INT — 297636150009 was observed live.</summary>
    [Fact]
    public void Trades_deal_id_is_read_as_a_long()
    {
        var table = IceDescriptors.CrudeIndexTradesTable;
        var (rows, _) = Parse("CrudeIndexTrades", Samples.CrudeIndexTrades);

        Assert.Equal(82817876L, rows[0].Value(table, "DEAL_ID"));
        Assert.Equal(297636150009L, rows[1].Value(table, "DEAL_ID"));
        Assert.Equal(new DateTime(2026, 8, 4, 12, 12, 39), rows[0].Value(table, "DEAL_EXECUTION_TIME"));
    }

    /// <summary>A header-only file is a legitimate zero-row success, not an error.</summary>
    [Fact]
    public void Header_only_file_parses_to_zero_rows_without_throwing()
    {
        var (rows, dropped) = Parse("CrudeIndexTrades", Samples.CrudeIndexTradesEmpty);

        Assert.Empty(rows);
        Assert.Equal(0, dropped);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public void Crlf_line_endings_parse_identically_to_lf()
    {
        var (lf, _) = Parse("EnvFutures", Samples.PhysEnv);
        var (crlf, _) = Parse("EnvFutures", Samples.PhysEnv.Replace("\n", "\r\n"));

        Assert.Equal(lf.Count, crlf.Count);
        Assert.Equal(lf[0].Values, crlf[0].Values);
    }

    [Fact]
    public void File_without_a_trailing_newline_keeps_its_last_row()
    {
        var (rows, _) = Parse("EnvFutures", Samples.PhysEnv.TrimEnd('\n'));
        Assert.Equal(3, rows.Count);
    }

    /// <summary>A short/ragged row must not throw — missing trailing fields are NULL.</summary>
    [Fact]
    public void Ragged_row_is_padded_rather_than_throwing()
    {
        const string ragged =
            "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
            "8/28/2026|CCA|CCA ACP Advance Futures|Feb27|ACA|F\n";

        var table = IceDescriptors.EnvFuturesTable;
        var (rows, dropped) = Parse("EnvFutures", ragged);

        Assert.Equal(0, dropped);
        var row = Assert.Single(rows);
        Assert.Equal("ACA", row.Value(table, "Contract"));
        Assert.Equal(DBNull.Value, row.Value(table, "ProductId"));
    }

    /// <summary>An unparseable REQUIRED value drops the row rather than merging a bad key.</summary>
    [Fact]
    public void Unparseable_required_value_drops_the_row()
    {
        const string badDate =
            "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
            "not-a-date|CCA|CCA ACP Advance Futures|Feb27|ACA|F||0.30000|0.00000|2/26/2027|22323\n";

        var (rows, dropped) = Parse("EnvFutures", badDate);

        Assert.Empty(rows);
        Assert.Equal(1, dropped);
    }

    /// <summary>
    /// arm.Futures requires ProductId because it is a key column, so a row without one
    /// is dropped rather than merged where it could collide with a different product.
    /// </summary>
    [Fact]
    public void Futures_row_without_a_product_id_is_dropped()
    {
        const string noProductId =
            "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
            "8/28/2026|AB-NIT|NG|9/1/2026|AEC|F||-1.95750|-0.01750|9/1/2026|\n";

        var (rows, dropped) = Parse("IceGas", noProductId);

        Assert.Empty(rows);
        Assert.Equal(1, dropped);
    }

    /// <summary>
    /// The reason ProductId is in the arm.Futures key: 'OLD' names two different
    /// products on the same date and strip. Both rows must survive with distinct
    /// product ids — under the original PK they would have collided.
    /// </summary>
    [Fact]
    public void Reused_contract_code_yields_two_rows_distinguished_by_product_id()
    {
        var table = IceDescriptors.FuturesTable;

        var (power, _) = Parse("NgxPower", Samples.NgxPowerOld);
        var (oil, _) = Parse("IceOil", Samples.IceOilOld);

        var powerRow = Assert.Single(power);
        var oilRow = Assert.Single(oil);

        // Same (TradeDate, Contract, ContractType, Strip) …
        foreach (var column in new[] { "TradeDate", "Contract", "ContractType", "Strip" })
            Assert.Equal(powerRow.Value(table, column), oilRow.Value(table, column));

        // … different product, and therefore a different key.
        Assert.Equal(30948, powerRow.Value(table, "ProductId"));
        Assert.Equal(26755, oilRow.Value(table, "ProductId"));
        Assert.NotEqual(powerRow.Value(table, "Hub"), oilRow.Value(table, "Hub"));
    }

    /// <summary>A file with no records at all is a fault, not an empty day.</summary>
    [Fact]
    public void Completely_empty_content_throws()
    {
        Assert.Throws<InvalidDataException>(() => Parse("EnvFutures", string.Empty));
    }

    // ------------------------------------------------------------------ conversion

    [Theory]
    [InlineData("8/28/2026", 2026, 8, 28)]
    [InlineData("08/28/2026", 2026, 8, 28)]
    [InlineData("2026-08-28", 2026, 8, 28)]
    [InlineData("12/1/2026", 2026, 12, 1)]
    public void Every_observed_date_shape_parses(string raw, int year, int month, int day)
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.Date, raw, out var value));
        Assert.Equal(new DateTime(year, month, day), value);
    }

    /// <summary>
    /// Exact-format matching only. An ambiguous d/M vs M/d value must be read as
    /// US-style M/d (which is what ICE writes), never silently as the other.
    /// </summary>
    [Fact]
    public void Ambiguous_date_is_read_as_month_first()
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.Date, "3/4/2026", out var value));
        Assert.Equal(new DateTime(2026, 3, 4), value);
    }

    [Theory]
    [InlineData("2026-08-28 03:00:00 PM", 2026, 8, 28, 15, 0, 0)]
    [InlineData("2026-08-04 12:12:39 PM", 2026, 8, 4, 12, 12, 39)]
    [InlineData("2026-08-04 12:12:39 AM", 2026, 8, 4, 0, 12, 39)]
    [InlineData("2026-08-28 15:00:00", 2026, 8, 28, 15, 0, 0)]
    public void Every_observed_timestamp_shape_parses(string raw, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.DateTime, raw, out var value));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), value);
    }

    [Fact]
    public void Blank_is_null_and_not_a_conversion_failure()
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.Decimal, "   ", out var value));
        Assert.Equal(DBNull.Value, value);
    }

    [Fact]
    public void Non_blank_garbage_is_a_conversion_failure()
    {
        Assert.False(IceConvert.TryConvert(IceColumnType.Decimal, "abc", out _));
        Assert.False(IceConvert.TryConvert(IceColumnType.Int32, "12.5", out _));
        Assert.False(IceConvert.TryConvert(IceColumnType.Date, "31/12/2026", out _));
    }

    [Fact]
    public void Negative_and_exponent_decimals_parse()
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.Decimal, "-1.95750", out var negative));
        Assert.Equal(-1.95750m, negative);

        Assert.True(IceConvert.TryConvert(IceColumnType.Decimal, "1.5E2", out var exponent));
        Assert.Equal(150m, exponent);
    }

    [Fact]
    public void Char_column_keeps_only_the_first_character()
    {
        Assert.True(IceConvert.TryConvert(IceColumnType.Char, "CALL", out var value));
        Assert.Equal("C", value);
    }

    // ------------------------------------------------------------------ delimited parser

    [Fact]
    public void Quoted_field_containing_the_delimiter_does_not_shift_columns()
    {
        var records = IceDelimited.ParseRecords("a,\"b,c\",d\n", ',');

        Assert.Single(records);
        Assert.Equal(new[] { "a", "b,c", "d" }, records[0]);
    }

    [Fact]
    public void Escaped_quotes_inside_a_quoted_field_are_unescaped()
    {
        var records = IceDelimited.ParseRecords("a,\"say \"\"hi\"\"\",c\n", ',');
        Assert.Equal(new[] { "a", "say \"hi\"", "c" }, records[0]);
    }

    [Fact]
    public void Newline_inside_a_quoted_field_stays_in_one_record()
    {
        var records = IceDelimited.ParseRecords("a,\"line1\nline2\",c\n", ',');

        Assert.Single(records);
        Assert.Equal("line1\nline2", records[0][1]);
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        var records = IceDelimited.ParseRecords("a|b\n\n\nc|d\n", '|');

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "c", "d" }, records[1]);
    }

    [Fact]
    public void Header_map_is_case_insensitive_and_keeps_the_first_duplicate()
    {
        var map = IceDelimited.BuildHeaderMap(new[] { "TRADE DATE", "HUB", "hub" });

        Assert.Equal(0, map["trade date"]);
        Assert.Equal(1, map["HUB"]);
    }
}
