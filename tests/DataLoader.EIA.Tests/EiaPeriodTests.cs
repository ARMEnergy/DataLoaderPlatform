using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// Period masks and window arithmetic.
///
/// <para>
/// These rules decide where window boundaries fall, and window boundaries appear
/// verbatim in a settled unit's resume key. A change here silently re-tiles the axis:
/// overlaps are merely wasteful, but HOLES are recorded in <c>core.LoadLog</c> as
/// successfully loaded and never looked at again.
/// </para>
/// </summary>
public sealed class EiaPeriodTests
{
    /// <summary>
    /// <c>EiaPeriodMask</c> is internal and an xUnit theory method must be public, so
    /// masks travel through <c>[InlineData]</c> by NAME and are resolved here.
    /// </summary>
    private static EiaPeriodMask Mask(string name) => Enum.Parse<EiaPeriodMask>(name);

    // ------------------------------------------------------------ masks

    [Theory]
    [InlineData("YYYY", "Year")]
    [InlineData("YYYY-\"Q\"Q", "Quarter")]
    [InlineData("YYYY-MM", "Month")]
    [InlineData("YYYY-MM-DD", "Day")]
    [InlineData("YYYY-MM-DD\"T\"HH24", "Hour")]
    [InlineData("YYYY-MM-DD\"T\"HH24TZH", "HourZone")]
    public void Recognises_every_mask_in_the_live_catalog(string format, string expected)
    {
        Assert.True(EiaPeriod.TryParseMask(format, out var mask));
        Assert.Equal(Mask(expected), mask);
        Assert.Equal(format, EiaPeriod.MaskToFormat(mask));
    }

    /// <summary>
    /// An unknown mask must be REFUSED, not guessed. Guessing picks a step size, and a
    /// wrong step mis-tiles the axis.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("YYYY-MM-DD HH24:MI")]
    [InlineData("DD/MM/YYYY")]
    [InlineData("YYYY-WW")]
    public void Refuses_an_unknown_mask(string? format) =>
        Assert.False(EiaPeriod.TryParseMask(format, out _));

    // ---------------------------------------------------------- parsing

    [Theory]
    [InlineData("2021", "Year", "2021")]
    [InlineData("2026-Q2", "Quarter", "2026-Q2")]
    [InlineData("2026-07", "Month", "2026-07")]
    [InlineData("2026-10-15", "Day", "2026-10-15")]
    [InlineData("2026-10-15T06", "Hour", "2026-10-15T06")]
    public void Round_trips_each_mask(string text, string maskName, string expected)
    {
        Assert.True(EiaPeriod.TryParse(text, Mask(maskName), out var period));
        Assert.Equal(expected, period.Format());
    }

    [Theory]
    [InlineData("2026-Q1", 1)]
    [InlineData("2026-Q2", 4)]
    [InlineData("2026-Q3", 7)]
    [InlineData("2026-Q4", 10)]
    public void Anchors_a_quarter_on_its_first_month(string text, int expectedMonth)
    {
        Assert.True(EiaPeriod.TryParse(text, Mask("Quarter"), out var period));
        Assert.Equal(expectedMonth, period.Anchor.Month);
        Assert.Equal(1, period.Anchor.Day);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-period")]
    [InlineData("2026-13")]        // month 13
    [InlineData("2026-02-30")]     // day out of range for February
    [InlineData("2026-Q5")]        // quarter 5
    [InlineData("2026-10-15T24")]  // hour 24
    public void Rejects_an_unparseable_period(string? text) =>
        Assert.False(EiaPeriod.TryParse(text, Mask("Month"), out _));

    // ------------------------------------------- cross-mask re-expression

    /// <summary>
    /// The real case this exists for. <c>crude-oil-imports</c> has
    /// DefaultDateFormat <c>YYYY-MM</c> and StartPeriod <c>2009-01</c>, but it also
    /// publishes an ANNUAL frequency, whose axis must start at <c>2009</c>.
    /// </summary>
    [Fact]
    public void Re_expresses_a_monthly_start_period_for_an_annual_frequency()
    {
        Assert.True(EiaPeriod.TryParse("2009-01", Mask("Month"), out var monthly));
        Assert.Equal("2009", monthly.ToMask(Mask("Year")).Format());
    }

    [Fact]
    public void Re_expresses_a_coarser_period_as_the_start_of_the_finer_one()
    {
        Assert.True(EiaPeriod.TryParse("1949", Mask("Year"), out var annual));

        Assert.Equal("1949-01", annual.ToMask(Mask("Month")).Format());
        Assert.Equal("1949-Q1", annual.ToMask(Mask("Quarter")).Format());
        Assert.Equal("1949-01-01", annual.ToMask(Mask("Day")).Format());
        Assert.Equal("1949-01-01T00", annual.ToMask(Mask("Hour")).Format());
    }

    [Fact]
    public void Truncates_rather_than_rounding_when_going_coarser()
    {
        Assert.True(EiaPeriod.TryParse("2026-10-15T23", Mask("Hour"), out var hourly));

        Assert.Equal("2026-10-15", hourly.ToMask(Mask("Day")).Format());
        Assert.Equal("2026-10", hourly.ToMask(Mask("Month")).Format());
        Assert.Equal("2026-Q4", hourly.ToMask(Mask("Quarter")).Format());
        Assert.Equal("2026", hourly.ToMask(Mask("Year")).Format());
    }

    // --------------------------------------------------------- stepping

    [Theory]
    [InlineData("Year", "2000", 10, "2010")]
    [InlineData("Quarter", "2000-Q1", 40, "2010-Q1")]
    [InlineData("Month", "2000-01", 60, "2005-01")]
    [InlineData("Day", "2019-01-01", 365, "2020-01-01")]
    [InlineData("Hour", "2026-10-01T00", 744, "2026-11-01T00")]
    public void Steps_by_the_masks_own_unit(string maskName, string start, int steps, string expected)
    {
        Assert.True(EiaPeriod.TryParse(start, Mask(maskName), out var period));
        Assert.Equal(expected, period.AddSteps(steps).Format());
    }

    [Fact]
    public void Stepping_is_reversible()
    {
        Assert.True(EiaPeriod.TryParse("2026-07", Mask("Month"), out var period));
        Assert.Equal("2026-07", period.AddSteps(60).AddSteps(-60).Format());
    }

    /// <summary>
    /// Windows must tile with no gap and no overlap: the next window starts exactly one
    /// step after the previous one ends. This is the arithmetic the provider relies on.
    /// </summary>
    [Theory]
    [InlineData("Year", "1949", 10)]
    [InlineData("Month", "2009-01", 60)]
    [InlineData("Day", "2019-01-01", 365)]
    [InlineData("Hour", "2019-01-01T00", 744)]
    public void Consecutive_windows_tile_without_gap_or_overlap(string maskName, string start, int steps)
    {
        Assert.True(EiaPeriod.TryParse(start, Mask(maskName), out var first));

        var firstEnd = first.AddSteps(steps - 1);
        var second = firstEnd.AddSteps(1);
        var secondEnd = second.AddSteps(steps - 1);

        Assert.True(firstEnd.IsBefore(second));                        // no overlap
        Assert.Equal(second.Format(), firstEnd.AddSteps(1).Format());  // no gap
        Assert.True(second.IsBefore(secondEnd));
    }

    // ------------------------------------------------- the local-hourly trap

    /// <summary>
    /// ⚠ The behaviour that would otherwise cost a whole frequency. A local-hourly
    /// <c>start</c>/<c>end</c> WITHOUT a two-digit zone suffix answers HTTP 500, which
    /// looks transient and gets retried to exhaustion. The <c>-00</c> form means UTC
    /// and avoids the <c>+</c>-decodes-as-space hazard in a query string.
    /// </summary>
    [Fact]
    public void Local_hourly_request_bounds_carry_a_two_digit_zone_suffix()
    {
        Assert.True(EiaPeriod.TryParse("2026-10-01T00", Mask("HourZone"), out var period));

        var bound = period.FormatRequestBound();

        Assert.Equal("2026-10-01T00-00", bound);
        Assert.EndsWith("-00", bound, StringComparison.Ordinal);
        Assert.DoesNotContain('+', bound);
        Assert.DoesNotContain('Z', bound);
    }

    [Fact]
    public void Plain_hourly_request_bounds_carry_no_zone_suffix()
    {
        Assert.True(EiaPeriod.TryParse("2026-10-01T00", Mask("Hour"), out var period));
        Assert.Equal("2026-10-01T00", period.FormatRequestBound());
    }

    /// <summary>
    /// A period the API returns in local-hourly form carries the respondent's own
    /// offset. Parsing normalises it to UTC so two labels for the same instant compare
    /// equal — which keeps window arithmetic consistent even though the Period COLUMN
    /// stores the vendor's label verbatim.
    /// </summary>
    [Fact]
    public void Normalises_a_zoned_period_to_utc()
    {
        Assert.True(EiaPeriod.TryParse("2026-10-15T02-04", Mask("HourZone"), out var minus4));
        Assert.True(EiaPeriod.TryParse("2026-10-15T06-00", Mask("HourZone"), out var utc));

        Assert.Equal(utc.Anchor, minus4.Anchor);
    }

    // ----------------------------------------------------- settled test

    [Fact]
    public void Reports_age_in_days_for_the_settled_decision()
    {
        Assert.True(EiaPeriod.TryParse("2026-01-01", Mask("Day"), out var period));

        var asOf = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(60, period.DaysBefore(asOf), 3);
    }

    [Fact]
    public void A_future_period_is_never_older_than_the_horizon()
    {
        // steo publishes quarterly forecasts out to 2027-Q4; those windows must stay hot.
        Assert.True(EiaPeriod.TryParse("2027-Q4", Mask("Quarter"), out var period));

        var asOf = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(period.DaysBefore(asOf) < 0);
    }
}
