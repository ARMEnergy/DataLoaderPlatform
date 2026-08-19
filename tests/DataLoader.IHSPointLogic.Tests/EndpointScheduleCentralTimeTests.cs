using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The DST-aware US-Central gate (design §B.1/§B.2): <c>SqlPlEndpointSchedule.ShouldRunAsync</c> now
/// interprets <c>arm.Endpoint.RunHoursCST</c> as US Central and converts the run's UTC start to Central
/// before the hour comparison, via
/// <c>TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(runStartedUtc, DateTimeKind.Utc), PlCentralTimeZone.Central)</c>.
///
/// <para>Because <c>ShouldRunAsync</c> loads the schedule from SQL (no offline seam), the gate is proven
/// <b>compositionally</b> from its internal building blocks — the <c>PlCentralTimeZone.Central</c> conversion
/// plus the parsed <see cref="HourSet"/> — which is the same decision <c>ShouldRunAsync</c> makes, minus the
/// DB load. Both <c>PlCentralTimeZone</c> and <c>ParseHours</c> are <c>internal</c> (visible via
/// <c>InternalsVisibleTo</c>). All instants are fixed UTC literals — no <c>DateTime.Now</c> — so the tests are
/// deterministic year-round. The headline is the winter/summer boundary: the same clock hour maps to a
/// different UTC instant across DST, so an endpoint tuned to a Central hour must fire at the right UTC moment
/// in both seasons.</para>
/// </summary>
public class EndpointScheduleCentralTimeTests
{
    // Fixed reference instants (UTC). Jan = CST (UTC-6), Jul = CDT (UTC-5).
    private static readonly DateTime JanNoonUtc = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc); // winter → Central 06
    private static readonly DateTime JulNoonUtc = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc); // summer → Central 07
    private static readonly DateTime Jul11Utc   = new(2026, 7, 15, 11, 0, 0, DateTimeKind.Utc); // summer → Central 06

    /// <summary>Mirrors the production conversion in <c>SqlPlEndpointSchedule.ShouldRunAsync</c> exactly.</summary>
    private static int CentralHourOf(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), PlCentralTimeZone.Central).Hour;

    private static HourSet Parse(string? raw)
    {
        // ConnectionString is never read on the ParseHours path — no DB access here.
        var schedule = new SqlPlEndpointSchedule(
            Options.Create(new IHSPointLogicSettings()), new ListLogger<SqlPlEndpointSchedule>());
        return schedule.ParseHours("SomeEndpoint", raw);
    }

    // ============================================================ UTC → Central hour, winter vs summer

    [Theory]
    [InlineData(2026, 1, 15, 12, 6)]  // Jan noon UTC → 06 Central (CST, UTC-6)
    [InlineData(2026, 7, 15, 12, 7)]  // Jul noon UTC → 07 Central (CDT, UTC-5)
    [InlineData(2026, 7, 15, 11, 6)]  // Jul 11:00 UTC → 06 Central (CDT, UTC-5)
    public void UtcInstant_ConvertsToExpectedCentralHour(int y, int mo, int d, int h, int expectedCentralHour)
    {
        var utc = new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedCentralHour, CentralHourOf(utc));
    }

    // ============================================================ end-to-end gate: conversion + HourSet.Contains

    [Fact]
    public void EndpointTunedToCentral6_Fires_At_Winter1200_And_Summer1100_ButNot_Summer1200()
    {
        // The exact decision ShouldRunAsync makes (schedule.Contains(centralHour)) for an endpoint whose
        // RunHoursCST is "6" — proven without the SQL load. Same clock hour (06 Central), two seasons,
        // two different UTC instants.
        var hours = Parse("6");
        Assert.False(hours.IsEveryHour);

        Assert.True(hours.Contains(CentralHourOf(JanNoonUtc)));  // winter: 12:00Z → 06 Central → fires
        Assert.True(hours.Contains(CentralHourOf(Jul11Utc)));    // summer: 11:00Z → 06 Central → fires
        Assert.False(hours.Contains(CentralHourOf(JulNoonUtc))); // summer: 12:00Z → 07 Central → does NOT fire
    }

    [Fact]
    public void SameUtcHour_MapsToDifferentCentralHour_AcrossDstBoundary()
    {
        // The regression guard for the change: 12:00Z is 06 Central in Jan but 07 Central in Jul. A loader
        // that (wrongly) compared the raw UTC hour, or used a fixed UTC-6, would map both to the same hour.
        var winter = CentralHourOf(JanNoonUtc);
        var summer = CentralHourOf(JulNoonUtc);

        Assert.Equal(6, winter);
        Assert.Equal(7, summer);
        Assert.NotEqual(winter, summer);
    }

    // ============================================================ DST is real, not a fixed offset

    [Fact]
    public void CentralZone_IsDstAware_NotFixedMinusSix()
    {
        // A regression to a fixed UTC-6 offset would make both offsets -6 and fail this test.
        Assert.Equal(TimeSpan.FromHours(-6), PlCentralTimeZone.Central.GetUtcOffset(JanNoonUtc));
        Assert.Equal(TimeSpan.FromHours(-5), PlCentralTimeZone.Central.GetUtcOffset(JulNoonUtc));
        Assert.True(PlCentralTimeZone.Central.SupportsDaylightSavingTime);
    }

    // ============================================================ every-hour fallbacks are TZ-independent

    [Theory]
    [InlineData("*")]      // explicit every-hour marker
    [InlineData(null)]     // missing column value (DBNull) → fail-open
    [InlineData("")]       // blank → fail-open
    [InlineData("nonsense")] // all-invalid → fail-open
    public void EveryHourFallback_RunsInBothSeasons_RegardlessOfConvertedHour(string? raw)
    {
        var hours = Parse(raw);
        Assert.True(hours.IsEveryHour);

        // Whatever the UTC→Central conversion yields in either season, an every-hour schedule fires.
        Assert.True(hours.Contains(CentralHourOf(JanNoonUtc)));
        Assert.True(hours.Contains(CentralHourOf(JulNoonUtc)));
        Assert.True(hours.Contains(CentralHourOf(Jul11Utc)));
    }

    [Fact]
    public void MissingRow_RunsAlways_RegardlessOfConvertedHour()
    {
        // ShouldRunAsync returns true directly when the endpoint has no schedule row (design §B.2). That
        // "run-always" fallthrough has the same shape as default(HourSet)/EveryHour, which — like the gate —
        // ignores the converted Central hour entirely.
        HourSet missingRow = default;
        Assert.True(missingRow.IsEveryHour);
        Assert.True(missingRow.Contains(CentralHourOf(JanNoonUtc)));
        Assert.True(missingRow.Contains(CentralHourOf(JulNoonUtc)));
    }
}
