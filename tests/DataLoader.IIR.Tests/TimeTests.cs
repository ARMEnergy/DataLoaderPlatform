using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirTime.CentralDate"/> (design §5.1) — the US-Central business-day boundary the
/// OfflineEvent daily-snapshot partition and the resume key are stamped on. A UTC instant shortly after
/// UTC midnight still falls on the PREVIOUS US-Central calendar day; the conversion must reflect that.
/// Guarded against an exotic host that lacks the Central tz database (documented UTC fallback).
/// </summary>
public class TimeTests
{
    [Fact]
    public void CentralDate_AtMiddayUtc_IsSameCalendarDay()
    {
        // 12:00 UTC is comfortably mid-morning in Central; the date is unambiguous either way.
        var utc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 8, 20), IirTime.CentralDate(utc));
    }

    [Fact]
    public void CentralDate_JustAfterUtcMidnight_IsPreviousCentralDay()
    {
        if (IirTime.UsingUtcFallback) return; // no Central tz on this host → identity conversion; skip

        // 2026-08-20 03:00 UTC = 2026-08-19 22:00 CDT (UTC−5 in August) → Central date is the day before.
        var utc = new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc);
        var central = IirTime.CentralDate(utc);

        Assert.Equal(new DateOnly(2026, 8, 19), central);
        Assert.NotEqual(DateOnly.FromDateTime(utc), central); // genuinely differs from the UTC date
    }
}
