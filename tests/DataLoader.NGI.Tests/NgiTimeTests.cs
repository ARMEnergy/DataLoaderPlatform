using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiTime"/> - the US-Central run-date basis and the single-source-of-truth window
/// helper both the work-unit provider and <see cref="NgiLoadValidator"/> call (design §3.2, §7).
/// Pure arithmetic over a supplied <c>StartedAtUtc</c>: no clock, no network, no DB.
/// </summary>
public class NgiTimeTests
{
    // ================================================== Central calendar date

    [Theory]
    // August = CDT (UTC-5): 04:00Z is still the previous Central day.
    [InlineData("2026-08-21T04:00:00Z", "2026-08-20")]
    [InlineData("2026-08-21T05:00:00Z", "2026-08-21")]
    [InlineData("2026-08-21T12:00:00Z", "2026-08-21")]
    [InlineData("2026-08-22T04:59:00Z", "2026-08-21")]
    // January = CST (UTC-6).
    [InlineData("2026-01-15T05:30:00Z", "2026-01-14")]
    [InlineData("2026-01-15T06:00:00Z", "2026-01-15")]
    public void CentralToday_UsesTheUsCentralCalendarDate(string utc, string expected)
    {
        if (NgiTime.UsingUtcFallback) return; // an exotic host with no tz database - nothing to assert

        var startedAt = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
                                                 System.Globalization.DateTimeStyles.AssumeUniversal);

        Assert.Equal(DateOnly.Parse(expected), NgiTime.CentralToday(startedAt));
    }

    [Fact]
    public void CentralDate_IsMonotonicNonDecreasing_AcrossBothDstTransitions()
    {
        if (NgiTime.UsingUtcFallback) return;

        // Why the RunDate hot token is safe on the Central clock: at DATE granularity the Central
        // calendar date never goes backwards, in either transition. (It would NOT be safe at hour
        // granularity - 01:00 Central occurs twice on a fall-back night.)
        foreach (var start in new[]
                 {
                     new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),   // spring forward 2026-03-08
                     new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)  // fall back 2026-11-01
                 })
        {
            var previous = NgiTime.CentralToday(start);
            for (var h = 1; h <= 96; h++)
            {
                var current = NgiTime.CentralToday(start.AddHours(h));
                Assert.True(current >= previous, $"Central date went backwards at +{h}h from {start:O}");
                previous = current;
            }
        }
    }

    // ================================================== ResolveWindow

    [Fact]
    public void ResolveWindow_EndsAtRunDate_AndSpansDaysBackInclusive()
    {
        var w = NgiTime.ResolveWindow(new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc), 60);

        Assert.Equal(new DateOnly(2026, 8, 21), w.RunDate);
        Assert.Equal(w.RunDate, w.Newest);      // NEVER a future date
        Assert.Equal(w.Newest, w.To);
        Assert.Equal(new DateOnly(2026, 6, 23), w.From);   // runDate - 59
        Assert.Equal(60, w.DaysBack);
        Assert.Equal(59, w.To.DayNumber - w.From.DayNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveWindow_ClampsDaysBackToOne(int daysBack)
    {
        var w = NgiTime.ResolveWindow(new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc), daysBack);

        Assert.Equal(1, w.DaysBack);
        Assert.Equal(w.From, w.To);
        Assert.Equal(w.RunDate, w.From);
    }

    [Fact]
    public void ResolveWindow_IsPure_SameInputSameOutput()
    {
        var startedAt = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(NgiTime.ResolveWindow(startedAt, 60), NgiTime.ResolveWindow(startedAt, 60));
    }

    // ================================================== HotToken

    [Fact]
    public void HotToken_RunDate_IsTheCentralRunDateStamp()
    {
        var token = NgiTime.HotToken(NgiHotKeyStrategy.RunDate, new DateOnly(2026, 8, 21), Guid.NewGuid());
        Assert.Equal("20260821", token);
    }

    [Fact]
    public void HotToken_RunDate_IgnoresTheRunId()
    {
        var day = new DateOnly(2026, 8, 21);
        Assert.Equal(
            NgiTime.HotToken(NgiHotKeyStrategy.RunDate, day, Guid.NewGuid()),
            NgiTime.HotToken(NgiHotKeyStrategy.RunDate, day, Guid.NewGuid()));
    }

    [Fact]
    public void HotToken_RunId_IsTheDashlessGuid()
    {
        var runId = Guid.NewGuid();
        var token = NgiTime.HotToken(NgiHotKeyStrategy.RunId, new DateOnly(2026, 8, 21), runId);

        Assert.Equal(runId.ToString("N"), token);
        Assert.Equal(32, token.Length);
        Assert.DoesNotContain("-", token);
    }
}
