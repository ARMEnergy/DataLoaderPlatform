using Xunit;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// The week-of-year derivation — the correction the requester asked for, and the one
/// piece of this loader that changes a value the vendor supplied.
///
/// <para>
/// <c>Week</c> is a PRIMARY KEY component of both tables, so these numbers are not
/// cosmetic: get one wrong and the same report loads twice under two keys. They are
/// pinned here rather than left to review.
/// </para>
/// </summary>
public sealed class GenscapeWeekTests
{
    /// <summary>
    /// ⚠ THE LOAD-BEARING TEST. Every expectation below was read off the LIVE
    /// crude-transportation endpoint on 2026-09-08, which reports the calendar
    /// week-of-year directly. The crude-storage endpoint returns the value in the third
    /// column for the same date — a week-of-MONTH — which is the bug.
    /// </summary>
    [Theory]
    // date          expected week   (what crude-storage wrongly returns)
    [InlineData("2025-12-05", 49, 1)]
    [InlineData("2025-12-12", 50, 2)]
    [InlineData("2025-12-19", 51, 3)]
    [InlineData("2025-12-26", 52, 4)]
    [InlineData("2026-01-02", 1, 1)]
    [InlineData("2026-01-09", 2, 2)]
    [InlineData("2026-01-16", 3, 3)]
    [InlineData("2026-01-23", 4, 4)]
    [InlineData("2026-01-30", 5, 5)]
    [InlineData("2026-02-06", 6, 6)]
    [InlineData("2026-02-13", 7, 7)]
    [InlineData("2026-08-28", 35, 4)]
    public void WeekOfYear_matches_the_value_the_transportation_endpoint_reports(
        string iso, int expectedWeek, int storageEndpointWeek)
    {
        var date = DateOnly.Parse(iso);

        Assert.Equal((byte)expectedWeek, GenscapeTime.WeekOfYear(date));

        // Sanity on the fixture itself: for the dates where the two differ, the test
        // would be vacuous if it accidentally asserted the wrong-but-equal number.
        if (expectedWeek != storageEndpointWeek)
            Assert.NotEqual((byte)storageEndpointWeek, GenscapeTime.WeekOfYear(date));
    }

    /// <summary>
    /// The boundaries SQL Server's DATEPART(week, …) is defined by, under the us_english
    /// default DATEFIRST of 7: week 1 runs from 1 January to the first Saturday, and
    /// every later week starts on a Sunday.
    /// </summary>
    [Theory]
    // 2026-01-01 is a Thursday, so week 1 is Thu 1 Jan .. Sat 3 Jan (3 days).
    [InlineData("2026-01-01", 1)]
    [InlineData("2026-01-03", 1)]  // Saturday — last day of week 1
    [InlineData("2026-01-04", 2)]  // Sunday    — first day of week 2
    // 2027-01-01 is a Friday.
    [InlineData("2027-01-01", 1)]
    [InlineData("2027-01-02", 1)]  // Saturday
    [InlineData("2027-01-03", 2)]  // Sunday
    // 2023-01-01 is itself a Sunday, so week 1 is a full seven days.
    [InlineData("2023-01-01", 1)]
    [InlineData("2023-01-07", 1)]  // Saturday
    [InlineData("2023-01-08", 2)]
    public void Week_boundaries_follow_DATEFIRST_7(string iso, int expected) =>
        Assert.Equal((byte)expected, GenscapeTime.WeekOfYear(DateOnly.Parse(iso)));

    /// <summary>
    /// The last days of a year, including leap years. 53 and 54 are both reachable — 54
    /// only when 1 January is a Saturday in a leap year — which is why the target column
    /// is TINYINT rather than something narrower.
    /// </summary>
    [Theory]
    [InlineData("2026-12-31", 53)]
    [InlineData("2024-12-31", 53)]  // leap year, 1 Jan was a Monday
    [InlineData("2028-12-31", 54)]  // leap year AND 1 Jan was a Saturday — the only way to reach 54
    [InlineData("2028-01-01", 1)]
    [InlineData("2028-01-02", 2)]   // Sunday, the day after a Saturday 1 January
    public void Year_end_weeks_stay_inside_TINYINT(string iso, int expected)
    {
        var week = GenscapeTime.WeekOfYear(DateOnly.Parse(iso));
        Assert.Equal((byte)expected, week);
        Assert.InRange(week, (byte)1, (byte)54);
    }

    /// <summary>
    /// Swept across two decades: the value must always fit TINYINT and must never exceed
    /// 54, or a merge would fail on an overflow rather than on anything diagnosable.
    /// </summary>
    [Fact]
    public void Every_date_over_twenty_years_yields_a_week_between_1_and_54()
    {
        for (var date = new DateOnly(2010, 1, 1); date <= new DateOnly(2030, 12, 31); date = date.AddDays(1))
            Assert.InRange(GenscapeTime.WeekOfYear(date), (byte)1, (byte)54);
    }

    /// <summary>
    /// The week restarts at 1 on 1 January and never decreases or skips within a year —
    /// the property that makes (Year, Week) a coherent pair.
    /// </summary>
    [Fact]
    public void Week_is_monotonic_within_a_year_and_restarts_each_January()
    {
        for (var year = 2010; year <= 2030; year++)
        {
            Assert.Equal((byte)1, GenscapeTime.WeekOfYear(new DateOnly(year, 1, 1)));

            byte previous = 1;
            for (var date = new DateOnly(year, 1, 1); date.Year == year; date = date.AddDays(1))
            {
                var week = GenscapeTime.WeekOfYear(date);
                Assert.True(week >= previous, $"{date:yyyy-MM-dd}: week went backwards ({previous} -> {week}).");
                Assert.True(week - previous <= 1, $"{date:yyyy-MM-dd}: week skipped ({previous} -> {week}).");
                previous = week;
            }
        }
    }

    /// <summary>
    /// <see cref="GenscapeTime.YearOf"/> is the CALENDAR year of the report date, not the
    /// payload's <c>year</c>. The pair has to come from one source or a January report
    /// could land as (2025, 1).
    /// </summary>
    [Theory]
    [InlineData("2026-01-02", 2026)]
    [InlineData("2025-12-31", 2025)]
    public void YearOf_is_the_calendar_year_of_the_report_date(string iso, int expected) =>
        Assert.Equal((short)expected, GenscapeTime.YearOf(DateOnly.Parse(iso)));

    /// <summary>
    /// The derivation must not move with the host's culture. A Thai Buddhist calendar
    /// default would otherwise render — and, through any culture-sensitive path, compute
    /// — a different year for the same instant, and Year is in the key.
    /// </summary>
    [Fact]
    public void Derivation_is_independent_of_the_ambient_culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");

            var date = new DateOnly(2025, 12, 5);
            Assert.Equal((byte)49, GenscapeTime.WeekOfYear(date));
            Assert.Equal((short)2025, GenscapeTime.YearOf(date));
            Assert.Equal("2025-12-05", GenscapeTime.Iso(date));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------- date parsing

    [Theory]
    [InlineData("2026-01-02", 2026, 1, 2)]
    [InlineData("2026-01-02T00:00:00Z", 2026, 1, 2)]
    [InlineData(" 2026-01-02 ", 2026, 1, 2)]
    public void Report_dates_parse_in_the_documented_forms(string text, int y, int m, int d)
    {
        Assert.True(GenscapeTime.TryParseReportDate(text, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    /// <summary>
    /// ⚠ <c>01/02/2026</c> is REJECTED rather than guessed at. The API documents and
    /// emits RFC 3339 only; accepting a slash form would mean choosing between 2 January
    /// and 1 February, and ReportDate is in the primary key.
    /// </summary>
    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("02-01-2026")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Ambiguous_or_unreadable_report_dates_are_rejected(string? text) =>
        Assert.False(GenscapeTime.TryParseReportDate(text, out _));
}
