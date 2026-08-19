using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The per-endpoint hourly run schedule (design §B.1/§B.2) — the parse tolerance in
/// <see cref="SqlPlEndpointSchedule.ParseHours"/> and the gating semantics of the parsed
/// <see cref="HourSet"/>. Everything here is DB-free: <c>ParseHours</c> is exercised directly (it is
/// <c>internal</c>, visible via <c>InternalsVisibleTo</c>) and never touches the connection string, so
/// no <c>arm.usp_GetEndpointSchedule</c> read happens. The WARN fail-open lines are asserted through a
/// capturing <see cref="ListLogger{T}"/>. (Design's own preferred path: test ParseHours + HourSet
/// directly rather than driving the SQL-backed <c>ShouldRunAsync</c>.)
/// </summary>
public class EndpointScheduleTests
{
    private static SqlPlEndpointSchedule NewSchedule(out ListLogger<SqlPlEndpointSchedule> logger)
    {
        logger = new ListLogger<SqlPlEndpointSchedule>();
        // ConnectionString is never read by ParseHours — no DB access on this path.
        return new SqlPlEndpointSchedule(Options.Create(new IHSPointLogicSettings()), logger);
    }

    private static HourSet Parse(string? raw, out ListLogger<SqlPlEndpointSchedule> logger)
    {
        var schedule = NewSchedule(out logger);
        return schedule.ParseHours("SomeEndpoint", raw);
    }

    private static HourSet Parse(string? raw) => Parse(raw, out _);

    private static bool Warned(ListLogger<SqlPlEndpointSchedule> logger) =>
        logger.OfLevel(LogLevel.Warning).Any();

    // ================================================================ §B.1 fail-open to every-hour

    [Fact]
    public void Star_IsEveryHour_NoWarn()
    {
        var hs = Parse("*", out var logger);
        Assert.True(hs.IsEveryHour);
        Assert.False(Warned(logger));           // '*' is the intended every-hour marker, not a fault
        for (var h = 0; h <= 23; h++)
            Assert.True(hs.Contains(h));
    }

    [Theory]
    [InlineData(null)]     // missing column value (DBNull) → fail-open
    [InlineData("")]       // blank
    [InlineData("   ")]    // whitespace-only
    [InlineData("x,y")]    // all-invalid tokens
    [InlineData("24,-1,99")] // all out-of-range
    [InlineData("24,-1")]  // both boundary rejects, nothing valid
    public void BlankInvalidOrOutOfRange_FailsOpenToEveryHour_WithWarn(string? raw)
    {
        var hs = Parse(raw, out var logger);
        Assert.True(hs.IsEveryHour);            // a typo must never silently disable an endpoint
        Assert.True(Warned(logger));
        Assert.True(hs.Contains(0));
        Assert.True(hs.Contains(23));
    }

    // ================================================================ §B.1 partial-valid KEEPS the valid hours (NOT fail-open)

    [Fact]
    public void PartialValid_KeepsValidHours_WarnsButDoesNotFailOpen()
    {
        var hs = Parse("6,x,18", out var logger);

        Assert.False(hs.IsEveryHour);           // NOT fail-open — the valid hours are honored
        Assert.True(hs.Contains(6));
        Assert.True(hs.Contains(18));
        Assert.False(hs.Contains(0));
        Assert.False(hs.Contains(12));
        Assert.True(Warned(logger));            // the invalid token still WARNs
    }

    // ================================================================ §B.1 boundaries 0 and 23 accepted; 24 and -1 rejected

    [Fact]
    public void Boundaries_Hour0And23_Accepted()
    {
        var hs = Parse("0,23");
        Assert.False(hs.IsEveryHour);
        Assert.True(hs.Contains(0));
        Assert.True(hs.Contains(23));
        Assert.False(hs.Contains(1));
        Assert.False(hs.Contains(22));
    }

    [Fact]
    public void Boundaries_Hour24AndMinus1_Rejected_ValidNeighbourKept()
    {
        // "24" and "-1" are out of range; "6" is valid → keep {6}, WARN on the two rejects.
        var hs = Parse("24,6,-1", out var logger);
        Assert.False(hs.IsEveryHour);
        Assert.True(hs.Contains(6));
        Assert.False(hs.Contains(24 % 24)); // hour 0 not scheduled
        Assert.True(Warned(logger));
    }

    // ================================================================ §B.1 dedup + whitespace tolerance

    [Fact]
    public void Duplicates_AreDeduped_ToASingleHour()
    {
        // "6,6,06" → the single hour 6 (06 parses to 6). No invalid token → no WARN.
        var hs = Parse("6,6,06", out var logger);
        Assert.False(hs.IsEveryHour);
        Assert.True(hs.Contains(6));
        Assert.False(hs.Contains(5));
        Assert.False(hs.Contains(7));
        Assert.False(Warned(logger));
    }

    [Fact]
    public void WhitespaceAroundTokens_IsTolerated()
    {
        var hs = Parse(" 6 , 18 ", out var logger);
        Assert.False(hs.IsEveryHour);
        Assert.True(hs.Contains(6));
        Assert.True(hs.Contains(18));
        Assert.False(Warned(logger));
    }

    // ================================================================ §B.1 invariant-culture int parse

    [Fact]
    public void ParsesUnderCommaDecimalCulture_InvariantThroughout()
    {
        // de-DE uses ',' as the decimal separator; ParseHours pins InvariantCulture, so the hour CSV
        // is split/parsed identically. Mirrors PlParseTests' culture-invariance guard.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var hs = Parse("6,18");
            Assert.False(hs.IsEveryHour);
            Assert.True(hs.Contains(6));
            Assert.True(hs.Contains(18));

            Assert.True(Parse("*").IsEveryHour);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ================================================================ Gating semantics on the parsed HourSet

    [Fact]
    public void ScheduledSingleHour_RunsOnlyOnThatHour()
    {
        // An endpoint scheduled {6} runs only when the current UTC hour is 6.
        var hs = Parse("6");
        for (var h = 0; h <= 23; h++)
            Assert.Equal(h == 6, hs.Contains(h));
    }

    [Fact]
    public void EveryHourMarker_RunsOnEveryHour()
    {
        var hs = HourSet.EveryHour;
        Assert.True(hs.IsEveryHour);
        for (var h = 0; h <= 23; h++)
            Assert.True(hs.Contains(h));
    }

    [Fact]
    public void DefaultHourSet_IsEveryHour()
    {
        // default(HourSet) is the every-hour marker (the missing-row / fail-open fallback shape, §B.2).
        HourSet hs = default;
        Assert.True(hs.IsEveryHour);
        Assert.True(hs.Contains(0));
        Assert.True(hs.Contains(13));
        Assert.True(hs.Contains(23));
    }

    [Fact]
    public void HourSetOf_ContainsOnlyGivenHours()
    {
        var hs = HourSet.Of(new HashSet<int> { 6, 18 });
        Assert.False(hs.IsEveryHour);
        Assert.True(hs.Contains(6));
        Assert.True(hs.Contains(18));
        Assert.False(hs.Contains(5));
        Assert.False(hs.Contains(0));
    }
}
