using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// The conversions that are silently wrong if you get them wrong. Every expected value
/// here was taken from the incumbent <c>dbo.*</c> tables for the same source record, so
/// these are compatibility assertions, not restatements of the implementation.
/// </summary>
public class NgxTimeTests
{
    // ------------------------------------------------------------- timestamps

    /// <summary>
    /// SUMMER. The vendor stamps <c>-06:00</c> (Mountain Daylight); the incumbent stores
    /// <c>07:37:06</c> for this exact trade (ExchangeReference 48000000003842).
    /// </summary>
    [Fact]
    public void Timestamp_SummerMountainOffset_ConvertsToCentral()
    {
        var actual = NgxTime.ParseCentralTimestamp("2026-09-01T06:37:06-06:00");
        Assert.Equal(new DateTime(2026, 9, 1, 7, 37, 6), actual);
    }

    /// <summary>
    /// WINTER. The vendor stamps <c>-07:00</c> (Mountain Standard); the incumbent stores
    /// <c>15:14:54</c> for this exact trade (ExchangeReference 48000000007314).
    ///
    /// <para>Paired with the summer case, this is what shows the conversion follows the
    /// stated offset rather than adding a constant.</para>
    /// </summary>
    [Fact]
    public void Timestamp_WinterMountainOffset_ConvertsToCentral()
    {
        var actual = NgxTime.ParseCentralTimestamp("2026-01-15T14:14:54-07:00");
        Assert.Equal(new DateTime(2026, 1, 15, 15, 14, 54), actual);
    }

    /// <summary>The index feed's <c>lastUpdateDate</c>, cross-checked the same way.</summary>
    [Fact]
    public void Timestamp_LastUpdateDate_ConvertsToCentral()
    {
        var actual = NgxTime.ParseCentralTimestamp("2026-09-18T02:15:41-06:00");
        Assert.Equal(new DateTime(2026, 9, 18, 3, 15, 41), actual);
    }

    /// <summary>
    /// A UTC-stamped value must land on the same INSTANT, not be read as a wall clock.
    /// The vendor does not send these today; this pins the behaviour if it ever does.
    /// </summary>
    [Fact]
    public void Timestamp_UtcOffset_IsConvertedNotReinterpreted()
    {
        // 12:37:06Z is 07:37:06 Central in September — the same instant as the summer
        // case above, which is exactly the point.
        Assert.Equal(
            new DateTime(2026, 9, 1, 7, 37, 6),
            NgxTime.ParseCentralTimestamp("2026-09-01T12:37:06Z"));
    }

    /// <summary>
    /// The result must be Unspecified, not Local. A Local kind would be re-converted by
    /// SqlClient or by any later ToUniversalTime on a machine outside Central.
    /// </summary>
    [Fact]
    public void Timestamp_HasUnspecifiedKind()
    {
        var actual = NgxTime.ParseCentralTimestamp("2026-09-01T06:37:06-06:00");
        Assert.Equal(DateTimeKind.Unspecified, actual!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a timestamp")]
    public void Timestamp_Unparseable_IsNull(string? text) =>
        Assert.Null(NgxTime.ParseCentralTimestamp(text));

    // ----------------------------------------------------------------- today

    /// <summary>
    /// ExecutionDate is a primary key component, so the date must be Central. At
    /// 2026-09-19 03:00 UTC it is still 2026-09-18 in Chicago — a UTC-based stamp would
    /// write a whole extra snapshot generation.
    /// </summary>
    [Fact]
    public void Today_JustAfterUtcMidnight_IsStillYesterdayInCentral()
    {
        var today = NgxTime.Today(new DateTime(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2026, 9, 18), today);
    }

    [Fact]
    public void Today_AfterCentralMidnight_RollsOver()
    {
        // 06:00 UTC is 01:00 Central on 19 September.
        var today = NgxTime.Today(new DateTime(2026, 9, 19, 6, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2026, 9, 19), today);
    }

    // ------------------------------------------------------------- amounts

    /// <summary>
    /// THOUSANDS SEPARATORS. <c>313,100</c> is three hundred thirteen thousand one
    /// hundred — the single most likely way to load this feed wrong by three orders of
    /// magnitude. The incumbent stores 313100.00000000 for this record.
    /// </summary>
    [Theory]
    [InlineData("313,100", 313100)]
    [InlineData("1,070,000", 1070000)]
    [InlineData("2500", 2500)]
    [InlineData("-0.0046", -0.0046)]
    [InlineData("1.2000", 1.2)]
    [InlineData("2.5", 2.5)]
    public void ParseAmount_HandlesVendorFormatting(string text, decimal expected) =>
        Assert.Equal(expected, NgxTime.ParseAmount(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("n/a")]
    public void ParseAmount_Unparseable_IsNull(string? text) =>
        Assert.Null(NgxTime.ParseAmount(text));

    /// <summary>
    /// Decimal, not double. <c>DECIMAL(18,8)</c> round-trips exactly; a double would
    /// introduce representation error into a price column.
    /// </summary>
    [Fact]
    public void ParseAmount_PreservesEightDecimalPlaces() =>
        Assert.Equal(1.23456789m, NgxTime.ParseAmount("1.23456789"));

    // --------------------------------------------------------- request dates

    /// <summary>
    /// The vendor's <c>d-MMMM-yyyy</c>, with no leading zero and an English month name.
    /// </summary>
    [Theory]
    [InlineData(2026, 9, 1, "1-September-2026")]
    [InlineData(2026, 9, 18, "18-September-2026")]
    [InlineData(2027, 3, 1, "1-March-2027")]
    [InlineData(2026, 12, 31, "31-December-2026")]
    public void RequestDate_UsesVendorSpelling(int y, int m, int d, string expected) =>
        Assert.Equal(expected, NgxTime.RequestDate(new DateOnly(y, m, d)));

    /// <summary>
    /// Formatted invariantly, so a server whose current culture is not English still
    /// sends "September" rather than a localised month name the endpoint will not parse.
    /// </summary>
    [Fact]
    public void RequestDate_IgnoresCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.Equal("1-September-2026", NgxTime.RequestDate(new DateOnly(2026, 9, 1)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <summary>
    /// A comma-decimal culture must not turn <c>313,100</c> into 313.1 — the parse pins
    /// InvariantCulture rather than inheriting the thread's.
    /// </summary>
    [Fact]
    public void ParseAmount_IgnoresCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(313100m, NgxTime.ParseAmount("313,100"));
            Assert.Equal(-0.0046m, NgxTime.ParseAmount("-0.0046"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------ month math

    [Theory]
    [InlineData(2026, 9, 18, -3, 2026, 6, 1)]   // the specified 3-months-back start
    [InlineData(2026, 9, 18, 6, 2027, 3, 1)]    // the specified 6-months-forward end
    [InlineData(2026, 9, 18, -1, 2026, 8, 1)]   // the strip window start
    [InlineData(2026, 9, 18, 1, 2026, 10, 1)]   // the strip window end
    [InlineData(2026, 1, 31, -1, 2025, 12, 1)]  // crosses a year boundary
    [InlineData(2026, 12, 15, 3, 2027, 3, 1)]   // crosses forward over a year boundary
    public void FirstOfMonthOffset_LandsOnTheFirst(
        int y, int m, int d, int offset, int ey, int em, int ed) =>
        Assert.Equal(
            new DateOnly(ey, em, ed),
            NgxTime.FirstOfMonthOffset(new DateOnly(y, m, d), offset));

    // ----------------------------------------------------------------- bools

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    public void ParseBool_ReadsVendorLiterals(string text, bool expected) =>
        Assert.Equal(expected, NgxTime.ParseBool(text));

    /// <summary>
    /// Absent must be NULL, not false. All three BIT columns are nullable, and a
    /// defaulted false would be indistinguishable from a real one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public void ParseBool_Unparseable_IsNull(string? text) =>
        Assert.Null(NgxTime.ParseBool(text));
}
