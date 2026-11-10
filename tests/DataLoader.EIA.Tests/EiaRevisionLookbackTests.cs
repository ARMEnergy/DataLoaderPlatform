using System.Globalization;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// <see cref="EiaSettings.RevisionLookbackPeriods"/>: the settle rule and the narrowed hot
/// read for the sparse frequencies must cover EIA's publication lag, not just
/// <see cref="EiaSettings.SettledAfterDays"/> measured from a period's start.
///
/// <para>
/// ⚠ The regression (prod, 2026-11-10 ~04:31Z): <c>natural-gas/move/poe2</c>'s monthly
/// hot window 2023-01..2027-12 was narrowed to read from 2026-09 (today − 45 days), so
/// 2026-08 — published late October — was never requested and 2026-07 stayed at 285 of
/// 390 rows, every run reporting success.
/// </para>
/// </summary>
public sealed class EiaRevisionLookbackTests
{
    private static readonly DateTime Poe2AsOf = new(2026, 11, 10, 4, 31, 0, DateTimeKind.Utc);

    private static EiaPeriod P(string text, EiaPeriodMask mask)
    {
        Assert.True(EiaPeriod.TryParse(text, mask, out var p), $"'{text}' does not parse as {mask}.");
        return p;
    }

    /// <summary>The read start before 2026-11-10: asOf.Date − SettledAfterDays, truncated.</summary>
    private static EiaPeriod OldReadStart(EiaSettings s, EiaPeriod windowStart, EiaPeriod windowEnd,
                                          EiaPeriodMask mask, DateTime asOfUtc)
    {
        var cutoffInstant = asOfUtc.Date.AddDays(-Math.Max(0, s.SettledAfterDays));
        if (!EiaPeriod.TryParse(cutoffInstant.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture),
                                mask, out var cutoff))
            return windowStart;
        if (!windowStart.IsBefore(cutoff)) return windowStart;
        if (!cutoff.IsAtOrBefore(windowEnd)) return windowStart;
        return cutoff;
    }

    /// <summary>The settle rule before 2026-11-10.</summary>
    private static bool OldSettled(EiaSettings s, EiaPeriod windowEnd, DateTime asOfUtc) =>
        windowEnd.DaysBefore(asOfUtc) > Math.Max(0, s.SettledAfterDays);

    // ------------------------------------------------------- the regression

    [Fact]
    public void Poe2_monthly_hot_window_reads_back_24_months_not_45_days()
    {
        var s = new EiaSettings();
        var ws = P("2023-01", EiaPeriodMask.Month);
        var we = P("2027-12", EiaPeriodMask.Month);

        var start = EiaWorkUnitProvider.NarrowReadStart(s, "monthly", ws, we, EiaPeriodMask.Month, Poe2AsOf);

        Assert.Equal("2024-11", start.Format());
        Assert.True(start.IsAtOrBefore(P("2026-07", EiaPeriodMask.Month)),
            "2026-07 and 2026-08 must be inside the re-read.");
        Assert.True(start.IsAtOrBefore(P("2026-08", EiaPeriodMask.Month)));

        // And the old rule really was the bug.
        Assert.Equal("2026-09", OldReadStart(s, ws, we, EiaPeriodMask.Month, Poe2AsOf).Format());
    }

    [Fact]
    public void Poe2_window_is_hot()
    {
        var s = new EiaSettings();
        Assert.False(EiaWorkUnitProvider.IsSettled(s, "monthly", P("2027-12", EiaPeriodMask.Month),
                                                   EiaPeriodMask.Month, Poe2AsOf));
    }

    [Fact]
    public void RevisionLookbackStart_at_poe2_asOf()
    {
        var s = new EiaSettings();
        Assert.Equal("2024-11", EiaWorkUnitProvider.RevisionLookbackStart(s, "monthly", EiaPeriodMask.Month, Poe2AsOf)?.Format());
        Assert.Equal("2024-Q4", EiaWorkUnitProvider.RevisionLookbackStart(s, "quarterly", EiaPeriodMask.Quarter, Poe2AsOf)?.Format());
        Assert.Equal("2023", EiaWorkUnitProvider.RevisionLookbackStart(s, "annual", EiaPeriodMask.Year, Poe2AsOf)?.Format());
        Assert.Null(EiaWorkUnitProvider.RevisionLookbackStart(s, "daily", EiaPeriodMask.Day, Poe2AsOf));
        Assert.Null(EiaWorkUnitProvider.RevisionLookbackStart(s, "hourly", EiaPeriodMask.Hour, Poe2AsOf));
        Assert.Null(EiaWorkUnitProvider.RevisionLookbackStart(s, "weekly", EiaPeriodMask.Day, Poe2AsOf));
    }

    [Fact]
    public void Annual_window_whose_lookback_equals_its_start_reads_in_full()
    {
        var s = new EiaSettings();
        var ws = P("2023", EiaPeriodMask.Year);
        var we = P("2047", EiaPeriodMask.Year);

        var start = EiaWorkUnitProvider.NarrowReadStart(s, "annual", ws, we, EiaPeriodMask.Year, Poe2AsOf);

        Assert.Equal("2023", start.Format());
    }

    [Fact]
    public void Annual_window_starting_earlier_narrows_to_the_lookback()
    {
        var s = new EiaSettings();
        var start = EiaWorkUnitProvider.NarrowReadStart(
            s, "annual", P("2001", EiaPeriodMask.Year), P("2025", EiaPeriodMask.Year), EiaPeriodMask.Year, Poe2AsOf);

        Assert.Equal("2023", start.Format());
    }

    [Fact]
    public void Quarterly_window_narrows_to_the_lookback()
    {
        var s = new EiaSettings();
        var start = EiaWorkUnitProvider.NarrowReadStart(
            s, "quarterly", P("2020-Q1", EiaPeriodMask.Quarter), P("2029-Q4", EiaPeriodMask.Quarter),
            EiaPeriodMask.Quarter, Poe2AsOf);

        Assert.Equal("2024-Q4", start.Format());
    }

    /// <summary>
    /// A window that ENDED before the day cutoff (old rule: settled) but inside the
    /// lookback is now hot and must read its lookback tail — never past its own end.
    /// </summary>
    [Fact]
    public void Window_ending_inside_lookback_but_before_day_cutoff_reads_its_tail()
    {
        var s = new EiaSettings();
        var ws = P("2020-01", EiaPeriodMask.Month);
        var we = P("2024-12", EiaPeriodMask.Month);

        Assert.False(EiaWorkUnitProvider.IsSettled(s, "monthly", we, EiaPeriodMask.Month, Poe2AsOf));
        var start = EiaWorkUnitProvider.NarrowReadStart(s, "monthly", ws, we, EiaPeriodMask.Month, Poe2AsOf);
        Assert.Equal("2024-11", start.Format());
        Assert.True(start.IsAtOrBefore(we));
    }

    // ------------------------------------------------------- IsSettled

    [Theory]
    [InlineData("monthly",   "Month",   "2022-12", true)]
    [InlineData("monthly",   "Month",   "2024-10", true)]   // last period before lookback start 2024-11
    [InlineData("monthly",   "Month",   "2024-11", false)]  // boundary: the lookback start itself is revisable
    [InlineData("monthly",   "Month",   "2026-07", false)]  // >45 days old, within 24 months: the bug
    [InlineData("monthly",   "Month",   "2026-08", false)]
    [InlineData("monthly",   "Month",   "2027-12", false)]
    [InlineData("quarterly", "Quarter", "2022-Q4", true)]
    [InlineData("quarterly", "Quarter", "2024-Q3", true)]
    [InlineData("quarterly", "Quarter", "2024-Q4", false)]
    [InlineData("quarterly", "Quarter", "2026-Q2", false)]
    [InlineData("annual",    "Year",    "2020",    true)]
    [InlineData("annual",    "Year",    "2022",    true)]
    [InlineData("annual",    "Year",    "2023",    false)]
    [InlineData("annual",    "Year",    "2025",    false)]
    public void IsSettled_for_sparse_frequencies(string freq, string maskName, string windowEnd, bool expected)
    {
        var mask = Enum.Parse<EiaPeriodMask>(maskName);
        var s = new EiaSettings();

        Assert.Equal(expected, EiaWorkUnitProvider.IsSettled(s, freq, P(windowEnd, mask), mask, Poe2AsOf));
    }

    [Theory]
    [InlineData("monthly", "Month", "2026-07")]
    [InlineData("quarterly", "Quarter", "2026-Q2")]
    [InlineData("annual", "Year", "2025")]
    public void Those_windows_were_settled_under_the_old_rule(string freq, string maskName, string windowEnd)
    {
        _ = freq;
        var mask = Enum.Parse<EiaPeriodMask>(maskName);
        Assert.True(OldSettled(new EiaSettings(), P(windowEnd, mask), Poe2AsOf),
            "Precondition: the old rule settled this window — the case the lookback exists for.");
    }

    // -------------------------------- frequencies with no lookback are unchanged

    public static IEnumerable<object[]> NoLookbackCases()
    {
        var asOfs = new[]
        {
            Poe2AsOf,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 2, 29, 23, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 8, 7, 15, 0, DateTimeKind.Utc)
        };
        var cases = new (string Freq, EiaPeriodMask Mask, int Step)[]
        {
            ("daily", EiaPeriodMask.Day, 12),
            ("weekly", EiaPeriodMask.Day, 1825),
            ("four-week-average", EiaPeriodMask.Day, 1825),
            ("hourly", EiaPeriodMask.Hour, 48),
            ("local-hourly", EiaPeriodMask.HourZone, 48)
        };
        foreach (var asOf in asOfs)
        foreach (var c in cases)
            yield return new object[] { c.Freq, c.Mask.ToString(), c.Step, asOf };
    }

    private static IEnumerable<(EiaPeriod Start, EiaPeriod End)> WindowsAround(EiaPeriodMask mask, int step, DateTime asOf)
    {
        var anchor = P(asOf.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture), mask);
        // Windows of the given width at offsets from well in the past to the future,
        // plus narrower ones so boundaries fall near the cutoffs.
        foreach (var width in new[] { step, Math.Max(1, step / 4), 1 })
        for (var k = -6; k <= 2; k++)
        {
            var start = anchor.AddSteps(k * width - width / 2);
            yield return (start, start.AddSteps(width - 1));
        }
        // Day masks: windows hugging the 45-day cutoff.
        if (mask is EiaPeriodMask.Day or EiaPeriodMask.Hour or EiaPeriodMask.HourZone)
        {
            var perDay = mask == EiaPeriodMask.Day ? 1 : 24;
            for (var d = 40; d <= 50; d++)
            {
                var end = anchor.AddSteps(-d * perDay);
                yield return (end.AddSteps(-perDay * 3), end);
            }
        }
    }

    private static EiaSettings WithLookback(string? freq, int? periods)
    {
        var s = new EiaSettings();
        if (freq is not null && periods is { } n) s.RevisionLookbackPeriods[freq] = n;
        return s;
    }

    [Theory]
    [MemberData(nameof(NoLookbackCases))]
    public void No_lookback_frequency_matches_the_old_rule_with_defaults(string freq, string mask, int step, DateTime asOf)
        => AssertMatchesOldRule(new EiaSettings(), freq, mask, step, asOf);

    [Theory]
    [MemberData(nameof(NoLookbackCases))]
    public void Lookback_of_zero_matches_the_old_rule(string freq, string mask, int step, DateTime asOf)
        => AssertMatchesOldRule(WithLookback(freq, 0), freq, mask, step, asOf);

    [Theory]
    [MemberData(nameof(NoLookbackCases))]
    public void Negative_lookback_matches_the_old_rule(string freq, string mask, int step, DateTime asOf)
        => AssertMatchesOldRule(WithLookback(freq, -5), freq, mask, step, asOf);

    [Theory]
    [InlineData("monthly", "Month", 60)]
    [InlineData("quarterly", "Quarter", 40)]
    [InlineData("annual", "Year", 25)]
    public void Sparse_frequency_with_lookback_zero_or_absent_matches_the_old_rule(string freq, string maskName, int step)
    {
        var mask = Enum.Parse<EiaPeriodMask>(maskName);
        var zero = WithLookback(freq, 0);
        AssertMatchesOldRule(zero, freq, mask, step, Poe2AsOf);

        var absent = new EiaSettings();
        absent.RevisionLookbackPeriods.Remove(freq);
        AssertMatchesOldRule(absent, freq, mask, step, Poe2AsOf);

        var nullMap = new EiaSettings { RevisionLookbackPeriods = null! };
        AssertMatchesOldRule(nullMap, freq, mask, step, Poe2AsOf);
    }

    private static void AssertMatchesOldRule(EiaSettings s, string freq, string maskName, int step, DateTime asOf)
        => AssertMatchesOldRule(s, freq, Enum.Parse<EiaPeriodMask>(maskName), step, asOf);

    private static void AssertMatchesOldRule(EiaSettings s, string freq, EiaPeriodMask mask, int step, DateTime asOf)
    {
        var n = 0;
        foreach (var (ws, we) in WindowsAround(mask, step, asOf))
        {
            Assert.Equal(OldSettled(s, we, asOf), EiaWorkUnitProvider.IsSettled(s, freq, we, mask, asOf));
            Assert.Equal(OldReadStart(s, ws, we, mask, asOf),
                         EiaWorkUnitProvider.NarrowReadStart(s, freq, ws, we, mask, asOf));
            n++;
        }
        Assert.True(n > 0);
    }

    // ------------------------------------------- the lookback can only widen

    public static IEnumerable<object[]> AllFrequencies()
    {
        var freqs = new (string Freq, EiaPeriodMask Mask, int Step)[]
        {
            ("annual", EiaPeriodMask.Year, 25),
            ("quarterly", EiaPeriodMask.Quarter, 40),
            ("monthly", EiaPeriodMask.Month, 60),
            ("weekly", EiaPeriodMask.Day, 1825),
            ("four-week-average", EiaPeriodMask.Day, 1825),
            ("daily", EiaPeriodMask.Day, 12),
            ("hourly", EiaPeriodMask.Hour, 48),
            ("local-hourly", EiaPeriodMask.HourZone, 48)
        };
        foreach (var f in freqs) yield return new object[] { f.Freq, f.Mask.ToString(), f.Step };
    }

    private static IEnumerable<DateTime> AsOfSpread()
    {
        var t = new DateTime(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        while (t < end)
        {
            yield return t;
            t = t.AddDays(17).AddHours(5);  // walks across month/quarter/year boundaries and hours
        }
        yield return Poe2AsOf;
    }

    [Theory]
    [MemberData(nameof(AllFrequencies))]
    public void Lookback_only_widens(string freq, string maskName, int step)
    {
        var mask = Enum.Parse<EiaPeriodMask>(maskName);
        var s = new EiaSettings();
        var checkedHot = 0;
        foreach (var asOf in AsOfSpread())
        foreach (var (ws, we) in WindowsAround(mask, step, asOf))
        {
            var newSettled = EiaWorkUnitProvider.IsSettled(s, freq, we, mask, asOf);
            var oldSettled = OldSettled(s, we, asOf);

            if (newSettled)
                Assert.True(oldSettled, $"{freq} {ws}..{we} @ {asOf:o}: settled under the new rule but hot under the old.");

            var newStart = EiaWorkUnitProvider.NarrowReadStart(s, freq, ws, we, mask, asOf);

            // Always inside the window.
            Assert.True(ws.IsAtOrBefore(newStart), $"{freq} {ws}..{we} @ {asOf:o}: read start {newStart} before window.");
            Assert.True(newStart.IsAtOrBefore(we), $"{freq} {ws}..{we} @ {asOf:o}: read start {newStart} past window end.");

            // For any window the OLD rule kept hot, the new read starts no later.
            if (!oldSettled)
            {
                var oldStart = OldReadStart(s, ws, we, mask, asOf);
                Assert.True(newStart.IsAtOrBefore(oldStart),
                    $"{freq} {ws}..{we} @ {asOf:o}: new read start {newStart} is later than old {oldStart}.");
                checkedHot++;
            }
        }
        Assert.True(checkedHot > 0);
    }

    // ------------------------------------------------- settings / defaults

    [Fact]
    public void Defaults_are_present_on_new_settings()
    {
        var map = new EiaSettings().RevisionLookbackPeriods;

        Assert.NotNull(map);
        Assert.Equal(3, map.Count);
        Assert.Equal(24, map["monthly"]);
        Assert.Equal(8, map["quarterly"]);
        Assert.Equal(3, map["annual"]);
        Assert.False(map.ContainsKey("daily"));
        Assert.False(map.ContainsKey("hourly"));
        Assert.False(map.ContainsKey("weekly"));
    }

    [Fact]
    public void Frequency_key_is_case_insensitive()
    {
        var s = new EiaSettings();
        Assert.True(s.RevisionLookbackPeriods.ContainsKey("MONTHLY"));
        Assert.True(s.RevisionLookbackPeriods.ContainsKey("Quarterly"));

        var ws = P("2023-01", EiaPeriodMask.Month);
        var we = P("2027-12", EiaPeriodMask.Month);
        Assert.Equal("2024-11", EiaWorkUnitProvider.NarrowReadStart(s, "Monthly", ws, we, EiaPeriodMask.Month, Poe2AsOf).Format());
        Assert.Equal("2024-11", EiaWorkUnitProvider.RevisionLookbackStart(s, "MONTHLY", EiaPeriodMask.Month, Poe2AsOf)?.Format());
        Assert.False(EiaWorkUnitProvider.IsSettled(s, "MoNtHlY", P("2026-07", EiaPeriodMask.Month), EiaPeriodMask.Month, Poe2AsOf));
    }

    /// <summary>
    /// Binding from configuration must keep the case-insensitive comparer and override
    /// (not replace) the defaults; this is how appsettings.json reaches the loader.
    /// </summary>
    [Fact]
    public void Configuration_binding_overrides_defaults_and_keeps_case_insensitivity()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RevisionLookbackPeriods:monthly"] = "12",
                ["RevisionLookbackPeriods:weekly"] = "4"
            })
            .Build();

        var s = new EiaSettings();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(config, s);

        Assert.Equal(12, s.RevisionLookbackPeriods["MONTHLY"]);
        Assert.Equal(4, s.RevisionLookbackPeriods["Weekly"]);
        Assert.Equal(8, s.RevisionLookbackPeriods["quarterly"]);
        Assert.Equal(3, s.RevisionLookbackPeriods["annual"]);
    }

    [Fact]
    public void A_larger_lookback_moves_the_read_earlier()
    {
        var s = WithLookback("monthly", 36);
        var start = EiaWorkUnitProvider.NarrowReadStart(
            s, "monthly", P("2020-01", EiaPeriodMask.Month), P("2027-12", EiaPeriodMask.Month),
            EiaPeriodMask.Month, Poe2AsOf);

        Assert.Equal("2023-11", start.Format());
    }
}
