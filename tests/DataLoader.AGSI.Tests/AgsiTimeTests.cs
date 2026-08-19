using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// <see cref="AgsiTime.ResolveWindow"/> (design §3.2) — the SINGLE source of truth for
/// both the storage load window and the post-load validation window, so the two can
/// never drift. Newest requestable gas day = <c>runDate + DateOffsetDays</c> (−1); the
/// inclusive window is <c>[From .. To]</c> with <c>To == Newest</c> and length
/// <c>DaysBack</c> (clamped to a minimum of 1). A run start of 12:00 UTC keeps the CET
/// calendar date stable regardless of the CET-vs-UTC-fallback host resolution.
/// </summary>
public class AgsiTimeTests
{
    // 12:00 UTC on 2026-08-17 → CET/CEST same calendar day → runDate 2026-08-17 (stable
    // whether the host resolves CET or falls back to UTC).
    private static readonly DateTime StartedAt = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DateOffsetDays_IsMinusOne()
    {
        Assert.Equal(-1, AgsiTime.DateOffsetDays);
    }

    [Fact]
    public void ResolveWindow_NewestIsRunDatePlusOffset_ToEqualsNewest()
    {
        var w = AgsiTime.ResolveWindow(StartedAt, daysBack: 21);

        Assert.Equal(new DateOnly(2026, 8, 17), w.RunDate);
        Assert.Equal(w.RunDate.AddDays(AgsiTime.DateOffsetDays), w.Newest); // runDate − 1
        Assert.Equal(new DateOnly(2026, 8, 16), w.Newest);
        Assert.Equal(w.Newest, w.To);
    }

    [Fact]
    public void ResolveWindow_FromToSpansDaysBack_Inclusive()
    {
        var w = AgsiTime.ResolveWindow(StartedAt, daysBack: 21);

        Assert.Equal(21, w.DaysBack);
        Assert.Equal(new DateOnly(2026, 7, 27), w.From); // Newest − (21 − 1)
        // Inclusive [From..To] must contain exactly DaysBack days.
        Assert.Equal(w.DaysBack, w.To.DayNumber - w.From.DayNumber + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveWindow_DaysBackBelow1_IsClampedTo1(int daysBack)
    {
        var w = AgsiTime.ResolveWindow(StartedAt, daysBack);

        Assert.Equal(1, w.DaysBack);
        Assert.Equal(w.Newest, w.From); // single-day window
        Assert.Equal(w.From, w.To);
    }

    [Fact]
    public void CetToday_At12Utc_IsSameCalendarDay()
    {
        Assert.Equal(new DateOnly(2026, 8, 17), AgsiTime.CetToday(StartedAt));
    }
}
