using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The invariant-culture scalar readers (<see cref="ModComParse"/>) and the width/range guards
/// layered on top of them (<see cref="ModComFieldReader"/>).
///
/// <para>Three things here are load-bearing:</para>
/// <list type="bullet">
///   <item><b>The timestamps are 12-hour <c>tt</c> text.</b> <c>2026-08-24 01:44:41 PM</c> is
///     <b>13:44:41</b>. An <c>HH</c> pattern mis-parses or fails every afternoon row (about a third
///     of the tape) and a bare <c>DateTime.Parse</c> on a non-US locale may not recognise <c>PM</c>
///     at all - losing 12 hours. The <c>12:xx PM</c> noon hour is the subtlest case. At least one
///     test below runs under <b>de-DE</b> to prove the parse is invariant, not ambient.</item>
///   <item><b>The only null representation is the empty string.</b> <c>-</c> is <b>NOT</b> a
///     sentinel (it is a settlements PK <i>value</i>), and neither is <c>None</c>/<c>N/A</c>
///     (contrast NGI, where <c>"None"</c> IS the sentinel). Porting NGI's sentinel list here would
///     map a key value to NULL and violate a NOT NULL PK.</item>
///   <item><b>Blank <c>BIT</c> is NULL, never <c>false</c></b>, and an over-range
///     <c>DECIMAL(9,2)</c> degrades that one field to NULL instead of failing the whole batch with a
///     server-side arithmetic overflow.</item>
/// </list>
/// </summary>
public class ParseTests
{
    /// <summary>Runs an assertion body under a non-US ambient culture, then restores it.</summary>
    private static void InCulture(string cultureName, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ModComFieldReader Reader(out ModComParseCounters counters, IReadOnlyList<string>? expected = null)
    {
        counters = new ModComParseCounters();
        var columns = expected ?? ModComColumns.Trades;
        var header = columns.ToArray();
        return new ModComFieldReader(ModComHeaderMap.Build(header, columns), counters, NullLogger.Instance, "AllTrades");
    }

    /// <summary>A record laid out in the given column order, with the named field set.</summary>
    private static string[] RecordWith(IReadOnlyList<string> columns, string column, string? value) =>
        columns.Select(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase) ? value ?? string.Empty : string.Empty).ToArray();

    // ============================================================ the AM/PM timestamp

    [Fact]
    public void AfternoonTimestamp_ParsesTo13_44_41_NotTo01_44_41()
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Timestamp("2026-08-24 01:44:41 PM", out var value));
        Assert.Equal(new DateTime(2026, 8, 24, 13, 44, 41), value);
        Assert.Equal(13, value.Hour);
    }

    [Fact]
    public void MorningTimestamp_StaysAsGiven()
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Timestamp("2026-08-24 09:31:48 AM", out var value));
        Assert.Equal(new DateTime(2026, 8, 24, 9, 31, 48), value);
    }

    [Theory]
    [InlineData("2026-08-06 12:42:37 PM", 12, 42, 37)]   // noon hour - the subtlest case
    [InlineData("2026-08-06 12:05:00 AM", 0, 5, 0)]      // midnight hour
    [InlineData("2026-08-24 11:59:59 PM", 23, 59, 59)]
    [InlineData("2026-08-24 12:00:00 PM", 12, 0, 0)]
    public void TwelveHourEdges_ParseToTheRightWallClockTime(string raw, int hour, int minute, int second)
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Timestamp(raw, out var value));
        Assert.Equal(hour, value.Hour);
        Assert.Equal(minute, value.Minute);
        Assert.Equal(second, value.Second);
    }

    [Fact]
    public void TimestampParsing_IsInvariant_NotAmbient_ProvedUnderDeDE()
    {
        // German has no "PM" designator and uses ',' as the decimal separator. If any of these went
        // through the ambient culture, this test fails - which is the whole point of running it here.
        InCulture("de-DE", () =>
        {
            Assert.Equal(ModComParseOutcome.Ok, ModComParse.Timestamp("2026-08-24 01:44:41 PM", out var pm));
            Assert.Equal(new DateTime(2026, 8, 24, 13, 44, 41), pm);

            Assert.Equal(ModComParseOutcome.Ok, ModComParse.Timestamp("2026-08-06 12:42:37 PM", out var noon));
            Assert.Equal(new DateTime(2026, 8, 6, 12, 42, 37), noon);

            Assert.Equal(ModComParseOutcome.Ok, ModComParse.Dec92("-16.65", out var price));
            Assert.Equal(-16.65m, price);                                  // '.' is the decimal point, always

            Assert.Equal(ModComParseOutcome.Ok, ModComParse.IsoDate("2026-09-01", out var date));
            Assert.Equal(new DateOnly(2026, 9, 1), date);

            Assert.Equal("2026-09-01", ModComTime.Iso(new DateOnly(2026, 9, 1)));   // query-string rendering
        });
    }

    [Fact]
    public void Dec92AndTimestampParsing_AreInvariant_AlsoUnderFrFR()
    {
        InCulture("fr-FR", () =>
        {
            Assert.Equal(ModComParseOutcome.Ok, ModComParse.Dec92("300000", out var volume));
            Assert.Equal(300000m, volume);
            Assert.Equal(ModComParseOutcome.Ok, ModComParse.Timestamp("2026-08-21 01:48:09 PM", out var executed));
            Assert.Equal(new DateTime(2026, 8, 21, 13, 48, 9), executed);
        });
    }

    [Theory]
    [InlineData("2026-08-24 13:44:41")]        // 24-hour: NOT the vendor format
    [InlineData("2026-08-24 01:44:41")]        // no designator
    [InlineData("08/24/2026 01:44:41 PM")]     // US date order
    [InlineData("2026-08-24T13:44:41")]        // ISO-8601
    [InlineData("2026-08-24 01:44:41.123 PM")] // fractional seconds were never observed
    [InlineData("not a timestamp")]
    public void ATimestampInAnyOtherShape_IsUnparseable_NotSilentlyWrong(string raw)
    {
        Assert.Equal(ModComParseOutcome.Unparseable, ModComParse.Timestamp(raw, out _));
    }

    [Fact]
    public void ABlankTimestamp_IsAbsent_NotUnparseable()
    {
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Timestamp("", out _));
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Timestamp("   ", out _));
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Timestamp(null, out _));
    }

    [Fact]
    public void TheTimestampFormatConstant_IsTheTwelveHourPattern()
    {
        Assert.Equal("yyyy-MM-dd hh:mm:ss tt", ModComParse.TimestampFormat);
        Assert.DoesNotContain("HH", ModComParse.TimestampFormat);
    }

    // ============================================================ numerics

    [Theory]
    [InlineData("-16.65", -16.65)]      // a differential: NEGATIVE is normal, not an error
    [InlineData("-0.15", -0.15)]
    [InlineData("0.00", 0)]             // a real value, not "missing"
    [InlineData("0.01", 0.01)]
    [InlineData("300000", 300000)]      // the observed Volume maximum
    [InlineData("9999999.99", 9999999.99)]
    [InlineData(" 2.65 ", 2.65)]
    public void Dec92_ParsesSignedDecimals(string raw, double expected)
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Dec92(raw, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Fact]
    public void ABlankCommission_IsAbsent_SoTheColumnPersistsAsNull()
    {
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Dec92("", out var value));
        Assert.Equal(0m, value);   // the out value is meaningless when Absent; the caller writes NULL
    }

    [Theory]
    [InlineData("10000000.00")]
    [InlineData("-10000000.00")]   // Math.Abs: the ceiling is symmetric
    [InlineData("99999999")]
    public void AnOverDecimal92Value_DegradesToNull_RatherThanThrowing(string raw)
    {
        // Letting it through would raise a server-side ARITHMETIC OVERFLOW that fails the whole
        // batch - the opposite of tolerant parsing. The parsed value is retained so the error log
        // can name it, but the caller persists NULL.
        Assert.Equal(ModComParseOutcome.OutOfRange, ModComParse.Dec92(raw, out var value));
        Assert.True(Math.Abs(value) > ModComParse.Dec92Ceiling);
    }

    [Fact]
    public void TheDecimalCeilingIsTheDECIMAL92Maximum()
    {
        Assert.Equal(9_999_999.99m, ModComParse.Dec92Ceiling);
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Dec92("9999999.99", out _));
        Assert.Equal(ModComParseOutcome.OutOfRange, ModComParse.Dec92("9999999.999", out _));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("16.65 USD")]
    [InlineData("--16.65")]
    [InlineData("(16.65)")]      // accounting negatives are not a thing here
    public void Dec92_OnNonNumericText_IsUnparseable_AndTheFieldDegradesToNull(string raw)
    {
        Assert.Equal(ModComParseOutcome.Unparseable, ModComParse.Dec92(raw, out _));
    }

    [Fact]
    public void Dec92_AcceptsAThousandsSeparator_DocumentingActualBehaviour()
    {
        // NumberStyles.Number includes AllowThousands, so "1,234.56" parses. No live value carries a
        // separator; this pins the behaviour rather than asserting a wish, so a future switch to a
        // stricter NumberStyles is a deliberate, visible change.
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Dec92("1,234.56", out var value));
        Assert.Equal(1234.56m, value);
    }

    [Theory]
    [InlineData("68043", 68043)]
    [InlineData(" 68043 ", 68043)]
    [InlineData("0", 0)]
    public void Int32_ParsesTheTradeNumber(string raw, int expected)
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Int32(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTradeNumber_IsAbsent_WhichMakesTheRowUnkeyable(string raw)
    {
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Int32(raw, out _));
    }

    [Theory]
    [InlineData("68043a")]
    [InlineData("68,043")]
    [InlineData("6.8e4")]
    [InlineData("TN68043")]
    public void AnUnparseableTradeNumber_IsUnparseable_WhichAlsoMakesTheRowUnkeyable(string raw)
    {
        Assert.Equal(ModComParseOutcome.Unparseable, ModComParse.Int32(raw, out _));
    }

    // ============================================================ booleans

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData(" True ", true)]
    public void Bit_ParsesTrueAndFalseCaseInsensitively(string raw, bool expected)
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.Bit(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ABlankBit_IsAbsent_MeaningNULL_NeverFalse()
    {
        // Conflating them destroys the anonymisation signal: every allTrades row would read
        // "not a click trade" instead of "unknown".
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Bit("", out _));
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Bit("   ", out _));
        Assert.Equal(ModComParseOutcome.Absent, ModComParse.Bit(null, out _));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("Y")]
    [InlineData("yes")]
    public void ABitInAnyOtherShape_IsUnparseable_AndDegradesToNull(string raw)
    {
        Assert.Equal(ModComParseOutcome.Unparseable, ModComParse.Bit(raw, out _));
    }

    // ============================================================ dates

    [Theory]
    [InlineData("2026-09-01", 2026, 9, 1)]
    [InlineData("2026-09-30", 2026, 9, 30)]
    [InlineData("2028-02-29", 2028, 2, 29)]   // leap year
    [InlineData("2031-12-31", 2031, 12, 31)]  // the settlement curve really does run this far out
    public void IsoDate_ParsesTermStartTermEndAndSettlementDate(string raw, int y, int m, int d)
    {
        Assert.Equal(ModComParseOutcome.Ok, ModComParse.IsoDate(raw, out var value));
        Assert.Equal(new DateOnly(y, m, d), value);
    }

    [Theory]
    [InlineData("09/01/2026")]
    [InlineData("2026-9-1")]
    [InlineData("2026-02-30")]   // not a real date
    [InlineData("SEP-26")]       // that is the Term column, and it is never parsed
    public void ADateInAnyOtherShape_IsUnparseable(string raw)
    {
        Assert.Equal(ModComParseOutcome.Unparseable, ModComParse.IsoDate(raw, out _));
    }

    // ============================================================ text: NO sentinel but blank

    [Fact]
    public void Text_KeepsTheLiteralDash_BecauseItIsASettlementsKeyVALUE()
    {
        Assert.Equal("-", ModComParse.Text("-"));
        Assert.Equal("-", ModComParse.Text(" - "));
    }

    [Fact]
    public void Text_KeepsUsdDollarExactly_SpaceAndDollarIncluded()
    {
        Assert.Equal("USD $", ModComParse.Text("USD $"));
    }

    [Fact]
    public void Text_DoesNotTreatNoneOrNAOrNULLAsSentinels_UnlikeNGI()
    {
        // NGI's null sentinel is the literal string "None". This API has no such sentinel, so
        // porting NgiParse.IsNullSentinel here would map real key values to NULL.
        Assert.Equal("None", ModComParse.Text("None"));
        Assert.Equal("N/A", ModComParse.Text("N/A"));
        Assert.Equal("NULL", ModComParse.Text("NULL"));
        Assert.Equal("0", ModComParse.Text("0"));
    }

    [Fact]
    public void Text_TrimsAndMapsOnlyBlankToNull()
    {
        Assert.Equal("Hardisty", ModComParse.Text("  Hardisty  "));
        Assert.Null(ModComParse.Text(""));
        Assert.Null(ModComParse.Text("   "));
        Assert.Null(ModComParse.Text(null));
    }

    // ============================================================ ModComFieldReader guards

    [Fact]
    public void FieldReader_NonKeyOverWidthValue_IsClampedAndCounted()
    {
        var reader = Reader(out var counters);
        var record = RecordWith(ModComColumns.Trades, ModComColumns.Side, new string('x', 300));

        var value = reader.Text(record, ModComColumns.Side, 256);

        Assert.Equal(256, value!.Length);          // clamped to the column width, row kept
        Assert.Equal(1, counters.TruncatedFields);
    }

    [Fact]
    public void FieldReader_NonKeyValueAtExactlyTheWidth_IsNotCounted()
    {
        var reader = Reader(out var counters);
        var record = RecordWith(ModComColumns.Trades, ModComColumns.Side, new string('x', 256));

        Assert.Equal(256, reader.Text(record, ModComColumns.Side, 256)!.Length);
        Assert.Equal(0, counters.TruncatedFields);
    }

    [Fact]
    public void FieldReader_KeyOverWidthValue_YieldsNullAndFlagsOverWidth_NeverTruncates()
    {
        // A TRUNCATED key silently MERGEs onto a different logical entity, which is worse than a
        // missing row - so the caller drops the row instead.
        var reader = Reader(out _, ModComColumns.Settlements);
        var record = RecordWith(ModComColumns.Settlements, ModComColumns.Location, new string('x', 51));

        var value = reader.KeyText(record, ModComColumns.Location, 50, out var overWidth);

        Assert.Null(value);
        Assert.True(overWidth);
    }

    [Fact]
    public void FieldReader_BlankKeyValue_YieldsNullWithoutTheOverWidthFlag()
    {
        var reader = Reader(out _, ModComColumns.Settlements);
        var record = RecordWith(ModComColumns.Settlements, ModComColumns.Location, "");

        Assert.Null(reader.KeyText(record, ModComColumns.Location, 50, out var overWidth));
        Assert.False(overWidth);
    }

    [Fact]
    public void FieldReader_KeyValueDash_SurvivesTheKeyGuard()
    {
        var reader = Reader(out _, ModComColumns.Settlements);
        var record = RecordWith(ModComColumns.Settlements, ModComColumns.Location, "-");

        Assert.Equal("-", reader.KeyText(record, ModComColumns.Location, 50, out var overWidth));
        Assert.False(overWidth);
    }

    [Fact]
    public void FieldReader_CountsUnparseableFieldsAndOutOfRangeFieldsSeparately()
    {
        var reader = Reader(out var counters);

        Assert.Null(reader.Dec92(RecordWith(ModComColumns.Trades, ModComColumns.Price, "abc"), ModComColumns.Price));
        Assert.Equal(1, counters.UnparseableFields);
        Assert.Equal(0, counters.OutOfRangeFields);

        Assert.Null(reader.Dec92(RecordWith(ModComColumns.Trades, ModComColumns.Volume, "99999999"), ModComColumns.Volume));
        Assert.Equal(1, counters.UnparseableFields);
        Assert.Equal(1, counters.OutOfRangeFields);

        Assert.Null(reader.Bit(RecordWith(ModComColumns.Trades, ModComColumns.InIndex, "1"), ModComColumns.InIndex));
        Assert.Equal(2, counters.UnparseableFields);

        Assert.Null(reader.Timestamp(
            RecordWith(ModComColumns.Trades, ModComColumns.ExecutedTimestamp, "2026-08-24 13:44:41"),
            ModComColumns.ExecutedTimestamp));
        Assert.Equal(3, counters.UnparseableFields);
    }

    [Fact]
    public void FieldReader_ABlankFieldIsNeverCountedAsUnparseable()
    {
        var reader = Reader(out var counters);
        var blank = ModComColumns.Trades.Select(_ => string.Empty).ToArray();

        Assert.Null(reader.Dec92(blank, ModComColumns.Price));
        Assert.Null(reader.Bit(blank, ModComColumns.InIndex));
        Assert.Null(reader.IsoDate(blank, ModComColumns.TermStart));
        Assert.Null(reader.Timestamp(blank, ModComColumns.LastUpdatedTimestamp));
        Assert.Null(reader.Int32(blank, ModComColumns.TradeNumber));
        Assert.Null(reader.Text(blank, ModComColumns.Notes, 8000));

        Assert.Equal(0, counters.UnparseableFields);
        Assert.Equal(0, counters.OutOfRangeFields);
        Assert.Equal(0, counters.TruncatedFields);
    }

    [Fact]
    public void ParseCounters_ToString_ReportsEveryTally()
    {
        var counters = new ModComParseCounters
        {
            Parsed = 9, Dropped = 1, UnparseableFields = 2, OutOfRangeFields = 3,
            TruncatedFields = 4, DuplicateKeys = 5
        };

        var text = counters.ToString();
        Assert.Contains("parsed=9", text);
        Assert.Contains("dropped=1", text);
        Assert.Contains("unparseable=2", text);
        Assert.Contains("outOfRange=3", text);
        Assert.Contains("truncated=4", text);
        Assert.Contains("duplicateKeys=5", text);
    }
}
