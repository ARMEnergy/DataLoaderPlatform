using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// The DATA_VOLUME rule (design 2026-11-20, §R.4 as amended by §A1/§A2/§A5), its settings
/// validation, and the read-side aggregation/key parsing it depends on. Pure: no clock, no
/// database. Acceptance item numbers (§R.10 1–21, amendments 22–29) are cited per test.
/// Proc-side halves (items 9/10/17/20 closure in T-SQL) live in
/// sql/Core/tests/008_WatchdogDataVolume.Tests.sql.
/// </summary>
public class WatchdogVolumeEvaluatorTests
{
    /// <summary>Friday. Window at the defaults = Tue 11-17, Wed 11-18, Thu 11-19.</summary>
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static readonly DateOnly Thu1119 = new(2026, 11, 19);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static WatchdogSettings Settings(Action<WatchdogDataVolumeSettings>? configure = null, params string[] loaders)
    {
        var s = new WatchdogSettings();
        s.DataVolume.Enabled = true;
        configure?.Invoke(s.DataVolume);
        foreach (var l in loaders.Length == 0 ? new[] { "Marex" } : loaders)
            s.Loaders[l] = new WatchdogLoaderSettings { MaxGapHours = 31 };
        return s;
    }

    /// <summary>One successful run per day over the whole query range; null = no loader-day.</summary>
    private static List<DailyVolume> History(string loader, Func<DateOnly, long?> rows, DateOnly? today = null)
    {
        var (from, to) = WatchdogVolumeEvaluator.QueryRange(new WatchdogDataVolumeSettings(), today ?? Today);
        var list = new List<DailyVolume>();
        var id = 1000;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            id++;
            if (rows(d) is { } r) list.Add(new DailyVolume(loader, d, r, 1, 1, new[] { id }));
        }
        return list;
    }

    private static WatchdogVolumeResult Eval(
        IReadOnlyList<DailyVolume> volumes, WatchdogSettings settings,
        IReadOnlyCollection<VolumeLoaderDay>? excluded = null, DateOnly? today = null) =>
        WatchdogVolumeEvaluator.Evaluate(volumes, excluded ?? Array.Empty<VolumeLoaderDay>(), settings, today ?? Today);

    private static bool Judged(WatchdogVolumeResult r, string loader, DateOnly day) =>
        r.JudgedInRange.Contains(new VolumeLoaderDay(loader, day));

    // ------------------------------------------------------------------ window ------

    [Fact]
    public void Window_IsTheLastWindowDaysCompletedCentralDays_NeverToday()
    {
        // Today carries a wildly abnormal value; it must not be looked at.
        var history = History("Marex", d => d == Thu1119 ? 1000 : 1000);
        history.Add(new DailyVolume("Marex", Today, 0, 1, 1, new[] { 1 }));

        var r = Eval(history, Settings());

        Assert.Equal(D(11, 17), r.WindowStart);
        Assert.Equal(D(11, 19), r.WindowEnd);
        Assert.Empty(r.Anomalies);
        Assert.DoesNotContain(r.JudgedInRange, j => j.DayCentral == Today);
        Assert.DoesNotContain(r.Unjudged, u => u.DayCentral == Today);
        Assert.Equal(3, r.JudgedInRange.Count);
    }

    [Fact]
    public void QueryRange_EndsYesterday_AndSpansWindowPlusMaxLookbackWeeks()
    {
        var (from, to) = WatchdogVolumeEvaluator.QueryRange(new WatchdogDataVolumeSettings(), Today);

        Assert.Equal(Today.AddDays(-1), to);
        Assert.Equal(Today.AddDays(-3 - 7 * 18), from);   // ~129 days (§A2)
    }

    // ------------------------------------------------------- detection (§R.4.4) ------

    [Fact]
    public void Item02_LowDay_IsReportedLow_WithRatioAbout024()
    {
        var r = Eval(History("Marex", d => d == Thu1119 ? 240 : 1000), Settings());

        var a = Assert.Single(r.Anomalies);
        Assert.Equal(VolumeDirection.Low, a.Direction);
        Assert.Equal(Thu1119, a.DayCentral);
        Assert.Equal(240, a.Rows);
        Assert.Equal(1000, a.MedianRows);
        Assert.Equal(0.24, a.Ratio, 6);
        Assert.Equal(6, a.Samples.Count);                       // BaselineWeeks
        Assert.All(a.Samples, s => Assert.Equal(DayOfWeek.Thursday, s.DayCentral.DayOfWeek));
        Assert.Equal(D(11, 12), a.Samples[0].DayCentral);       // most recent first
        Assert.Empty(a.ExcludedSampleDays);
        Assert.Equal(50, a.LowPercent);
        Assert.Equal(3, a.HighMultiple);
        Assert.True(Judged(r, "Marex", D(11, 17)));
        Assert.True(Judged(r, "Marex", D(11, 18)));
        Assert.False(Judged(r, "Marex", Thu1119));
    }

    [Fact]
    public void Item03_ZeroRows_AgainstNonZeroMedian_IsZero()
    {
        var a = Assert.Single(Eval(History("Marex", d => d == Thu1119 ? 0 : 1000), Settings()).Anomalies);
        Assert.Equal(VolumeDirection.Zero, a.Direction);
        Assert.Equal(0, a.Ratio);
    }

    [Fact]
    public void Item04_DoubleDay_LikeAManualRerun_IsNotReported()
    {
        var r = Eval(History("Marex", d => d == Thu1119 ? 2000 : 1000), Settings());
        Assert.Empty(r.Anomalies);
        Assert.True(Judged(r, "Marex", Thu1119));
    }

    [Fact]
    public void Item05_ThreeAndAHalfTimes_IsHigh()
    {
        var a = Assert.Single(Eval(History("Marex", d => d == Thu1119 ? 3500 : 1000), Settings()).Anomalies);
        Assert.Equal(VolumeDirection.High, a.Direction);
        Assert.Equal(3.5, a.Ratio, 6);
    }

    [Fact]
    public void ExactlyAtTheThresholds_IsNotReported()
    {
        // Low is strictly below median × 50%, High strictly above median × 3.
        var r = Eval(History("Marex", d => d == Thu1119 ? 500 : d == D(11, 18) ? 3000 : 1000), Settings());
        Assert.Empty(r.Anomalies);
    }

    [Fact]
    public void Item06_ThreeSamples_IsNotJudged()
    {
        // Only three earlier Thursdays exist.
        var history = History("Marex", d =>
            d.DayOfWeek != DayOfWeek.Thursday ? 1000
            : d == Thu1119 ? 10
            : d >= D(10, 29) ? 1000
            : null);

        var r = Eval(history, Settings());

        Assert.DoesNotContain(r.Anomalies, a => a.DayCentral == Thu1119);
        Assert.False(Judged(r, "Marex", Thu1119));
        var u = Assert.Single(r.Unjudged, x => x.DayCentral == Thu1119);
        Assert.Contains("3 baseline sample(s), need 4", u.Reason);
    }

    [Fact]
    public void Item07_MedianOf15_IsBelowMinMedianRows_AndNotJudged()
    {
        var r = Eval(History("Marex", d => d == Thu1119 ? 0 : 15), Settings());

        Assert.Empty(r.Anomalies);
        Assert.Contains(r.Unjudged, u => u.DayCentral == Thu1119 && u.Reason.Contains("below MinMedianRows 100"));
    }

    [Fact]
    public void MedianZero_IsNotJudged_EvenWithMinMedianRowsZero()
    {
        var r = Eval(History("Marex", d => d == Thu1119 ? 50 : 0), Settings(v => v.MinMedianRows = 0));
        Assert.Empty(r.Anomalies);
        Assert.Contains(r.Unjudged, u => u.DayCentral == Thu1119);
    }

    [Fact]
    public void Item08_WeekendDip_IsComparedWithEarlierWeekends_AndNotReported()
    {
        // Monday 2026-11-23: window = Fri 11-20, Sat 11-21, Sun 11-22.
        var monday = D(11, 23);
        var history = History("Marex",
            d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 200 : 10_000, monday);

        var r = Eval(history, Settings(), today: monday);

        Assert.Empty(r.Anomalies);
        Assert.True(Judged(r, "Marex", D(11, 21)));
        Assert.True(Judged(r, "Marex", D(11, 22)));
    }

    [Fact]
    public void Item18_PerLoaderLowPercent30_DayAt40Percent_IsNotReported()
    {
        var settings = Settings();
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { LowPercent = 30 };

        var r = Eval(History("Marex", d => d == Thu1119 ? 400 : 1000), settings);

        Assert.Empty(r.Anomalies);
        Assert.True(Judged(r, "Marex", Thu1119));
    }

    [Fact]
    public void PerLoaderOverrides_AreCarriedOnTheAnomaly_AsEffectiveValues()
    {
        var settings = Settings();
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { LowPercent = 30, HighMultiple = 5 };

        var a = Assert.Single(Eval(History("Marex", d => d == Thu1119 ? 200 : 1000), settings).Anomalies);
        Assert.Equal(30, a.LowPercent);
        Assert.Equal(5, a.HighMultiple);
    }

    // ----------------------------------------------- A1: all runs, ≥ 1 success ------

    [Fact]
    public void Item22_RetryDay_FailedRunRowsCount_SoNoFalseLow()
    {
        // AGSI shape: failed run 480 rows + successful retry 5, median 500 → 485, normal.
        var runs = new List<SqlWatchdogRepository.VolumeRunRow>();
        var (from, to) = WatchdogVolumeEvaluator.QueryRange(new WatchdogDataVolumeSettings(), Today);
        var id = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d == Thu1119)
            {
                runs.Add(new(++id, "AGSI", d, 480, false));
                runs.Add(new(++id, "AGSI", d, 5, true));
            }
            else runs.Add(new(++id, "AGSI", d, 500, true));
        }

        var volumes = SqlWatchdogRepository.AggregateDailyVolumes(runs);
        var day = Assert.Single(volumes, v => v.DayCentral == Thu1119);
        Assert.Equal(485, day.Rows);
        Assert.Equal(1, day.SuccessfulRuns);
        Assert.Equal(2, day.TotalRuns);
        Assert.Equal(2, day.LoaderRunIds.Count);

        var r = Eval(volumes, Settings(loaders: "AGSI"));
        Assert.Empty(r.Anomalies);
        Assert.True(Judged(r, "AGSI", Thu1119));
    }

    [Fact]
    public void Item23_DayWithOnlyFailedRuns_DoesNotExist_NotJudgedAndNotASample()
    {
        var runs = new List<SqlWatchdogRepository.VolumeRunRow>
        {
            new(1, "AGSI", Thu1119, 500, false),
            new(2, "AGSI", Thu1119, 0, false),
            new(3, "AGSI", D(11, 12), 900, false)   // a would-be sample
        };

        var volumes = SqlWatchdogRepository.AggregateDailyVolumes(runs);
        Assert.Empty(volumes);

        // At evaluator level: a missing day is "no successful run that day" and is stepped
        // over by the sample walk.
        var history = History("AGSI", d => d == Thu1119 || d == D(11, 12) ? null : 1000);
        var r = Eval(history, Settings(loaders: "AGSI"));
        Assert.Contains(r.Unjudged, u => u.DayCentral == Thu1119 && u.Reason == "no successful run that day");

        var wed = Assert.Single(Eval(History("AGSI", d => d == D(11, 12) ? null : d == Thu1119 ? 100 : 1000),
            Settings(loaders: "AGSI")).Anomalies);
        Assert.DoesNotContain(wed.Samples, s => s.DayCentral == D(11, 12));
        Assert.Equal(D(11, 5), wed.Samples[0].DayCentral);
    }

    [Fact]
    public void Item19_NullOrBlankLoaderNames_AreIgnored()
    {
        var runs = new List<SqlWatchdogRepository.VolumeRunRow>
        {
            new(1, null!, Thu1119, 100, true),
            new(2, "", Thu1119, 100, true),
            new(3, "   ", Thu1119, 100, true),
            new(4, "Marex", Thu1119, 100, true)
        };

        var v = Assert.Single(SqlWatchdogRepository.AggregateDailyVolumes(runs));
        Assert.Equal("Marex", v.LoaderName);
        Assert.Equal(100, v.Rows);

        // The evaluator tolerates them too.
        var history = History("Marex", d => d == Thu1119 ? 240 : 1000);
        history.Add(new DailyVolume(null!, Thu1119, 5, 1, 1, new[] { 9 }));
        history.Add(new DailyVolume(" ", Thu1119, 5, 1, 1, new[] { 9 }));
        Assert.Single(Eval(history, Settings()).Anomalies);
    }

    [Fact]
    public void Aggregate_GroupsLoaderNamesCaseInsensitively_AndListsEveryRunIdAscending()
    {
        var runs = new List<SqlWatchdogRepository.VolumeRunRow>
        {
            new(7, "Marex", Thu1119, 10, false),
            new(3, "MAREX", Thu1119, 20, true),
            new(5, "marex ", Thu1119, 30, false)
        };

        var v = Assert.Single(SqlWatchdogRepository.AggregateDailyVolumes(runs));
        Assert.Equal(60, v.Rows);
        Assert.Equal(1, v.SuccessfulRuns);
        Assert.Equal(3, v.TotalRuns);
        Assert.Equal(new[] { 3, 5, 7 }, v.LoaderRunIds);
    }

    [Fact]
    public void Aggregate_SumsBeyondIntRange()
    {
        var runs = new List<SqlWatchdogRepository.VolumeRunRow>
        {
            new(1, "ICE", Thu1119, int.MaxValue, true),
            new(2, "ICE", Thu1119, int.MaxValue, true)
        };
        Assert.Equal(2L * int.MaxValue, Assert.Single(SqlWatchdogRepository.AggregateDailyVolumes(runs)).Rows);
    }

    // ----------------------------------------------- A2/A5: exclusion & lookback ------

    [Fact]
    public void Item11_FiveLowThursdays_AreAllReported_EarlierOnesInExcludedSampleDays()
    {
        var low = new[] { D(10, 22), D(10, 29), D(11, 5), D(11, 12), D(11, 19) };
        var history = History("Marex", d => low.Contains(d) ? 200 : 1000);

        for (var k = 0; k < low.Length; k++)
        {
            var reported = low.Take(k).Select(d => new VolumeLoaderDay("Marex", d)).ToList();
            var r = Eval(history, Settings(), reported, today: low[k].AddDays(1));

            var a = Assert.Single(r.Anomalies, x => x.DayCentral == low[k]);
            Assert.Equal(VolumeDirection.Low, a.Direction);
            Assert.Equal(1000, a.MedianRows);
            Assert.Equal(low.Take(k).Reverse(), a.ExcludedSampleDays);
            Assert.All(a.Samples, s => Assert.DoesNotContain(s.DayCentral, low));
        }
    }

    [Fact]
    public void WithoutExclusion_TheBaselineWouldRaiseItsOwnGoalposts()
    {
        // Control for item 11: proves the exclusion set is load-bearing.
        var low = new[] { D(10, 22), D(10, 29), D(11, 5), D(11, 12), D(11, 19) };
        var r = Eval(History("Marex", d => low.Contains(d) ? 200 : 1000), Settings());
        Assert.DoesNotContain(r.Anomalies, a => a.DayCentral == Thu1119);
    }

    [Fact]
    public void Exclusion_MatchesLoaderNamesCaseInsensitively()
    {
        var r = Eval(History("Marex", d => d == D(11, 12) ? 1 : d == Thu1119 ? 240 : 1000), Settings(),
            new[] { new VolumeLoaderDay("MAREX", D(11, 12)) });

        var a = Assert.Single(r.Anomalies);
        Assert.Equal(new[] { D(11, 12) }, a.ExcludedSampleDays);
    }

    [Fact]
    public void Item24_PermanentDropForEightWeeks_IsStillReported_AgainstPreDropSamples()
    {
        var dropStart = D(9, 24);   // Thursday; 8 Thursdays dropped before 11-19
        var history = History("Marex", d => d >= dropStart ? 200 : 1000);
        var reported = history.Where(v => v.DayCentral >= dropStart && v.DayCentral < D(11, 17))
            .Select(v => new VolumeLoaderDay("Marex", v.DayCentral)).ToList();

        var r = Eval(history, Settings(), reported);

        var a = Assert.Single(r.Anomalies, x => x.DayCentral == Thu1119);
        Assert.Equal(VolumeDirection.Low, a.Direction);
        Assert.Equal(8, a.ExcludedSampleDays.Count);
        Assert.Equal(6, a.Samples.Count);
        Assert.All(a.Samples, s => Assert.True(s.DayCentral < dropStart));
        Assert.All(a.Samples, s => Assert.True(s.DayCentral >= Thu1119.AddDays(-7 * 18)));
        // Every window day is still reported, not silenced.
        Assert.Equal(3, r.Anomalies.Count);
    }

    [Fact]
    public void LookbackExhausted_BeforeMinSamples_IsUnjudged_NotSilentlyNormal()
    {
        // 15 dropped Thursdays → only weeks 16–18 remain inside the 18-week lookback.
        var dropStart = Thu1119.AddDays(-7 * 15);
        var history = History("Marex", d => d >= dropStart ? 200 : 1000);
        var reported = history.Where(v => v.DayCentral >= dropStart && v.DayCentral < D(11, 17))
            .Select(v => new VolumeLoaderDay("Marex", v.DayCentral)).ToList();

        var r = Eval(history, Settings(), reported);

        Assert.DoesNotContain(r.Anomalies, a => a.DayCentral == Thu1119);
        Assert.False(Judged(r, "Marex", Thu1119));
        Assert.Contains(r.Unjudged, u => u.DayCentral == Thu1119 && u.Reason.StartsWith("3 baseline sample(s)"));
    }

    [Fact]
    public void Item28_LoaderDisabledClosedDay_PassedAsExcluded_IsNeverASample()
    {
        // The repository returns days closed with anything but NotDetected (A5 — its SQL
        // predicate is pinned in WatchdogVolumeMapperTests); the evaluator must honour it.
        var history = History("Marex", d => d == D(11, 12) ? 50 : d == Thu1119 ? 240 : 1000);
        var a = Assert.Single(Eval(history, Settings(), new[] { new VolumeLoaderDay("Marex", D(11, 12)) }).Anomalies);

        Assert.DoesNotContain(a.Samples, s => s.DayCentral == D(11, 12));
        Assert.Contains(D(11, 12), a.ExcludedSampleDays);
    }

    [Fact]
    public void CollectSamples_TakesMostRecentBaselineWeeks_SteppingOverMissingAndExcluded()
    {
        var days = new Dictionary<DateOnly, DailyVolume>();
        for (var w = 1; w <= 18; w++)
        {
            var d = Thu1119.AddDays(-7 * w);
            if (w == 2) continue;   // missing
            days[d] = new DailyVolume("Marex", d, w, 1, 1, new[] { w });
        }

        var (samples, excluded) = WatchdogVolumeEvaluator.CollectSamples(
            Thu1119, days, d => d == Thu1119.AddDays(-21), null, 6, 18);

        Assert.Equal(new long[] { 1, 4, 5, 6, 7, 8 }, samples.Select(s => s.Rows));
        Assert.Equal(new[] { Thu1119.AddDays(-21) }, excluded);
    }

    [Fact]
    public void CollectSamples_NeverWalksPastMaxLookbackWeeks()
    {
        var days = new Dictionary<DateOnly, DailyVolume>();
        for (var w = 1; w <= 30; w++)
        {
            var d = Thu1119.AddDays(-7 * w);
            days[d] = new DailyVolume("Marex", d, w, 1, 1, new[] { w });
        }

        var (samples, excluded) = WatchdogVolumeEvaluator.CollectSamples(Thu1119, days, d => d > Thu1119.AddDays(-7 * 17), null, 6, 18);
        Assert.Equal(new long[] { 17, 18 }, samples.Select(s => s.Rows));
        Assert.Equal(16, excluded.Count);
    }

    // --------------------------------------------------- BaselineFromCentral ------

    [Fact]
    public void Item12_BaselineFromCentral_SilentUntilFourNewSamples_ThenJudgesTheNewLevel()
    {
        // Level change 1000 → 300 declared from Thu 10-22.
        var settings = Settings();
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { BaselineFromCentral = "2026-10-22" };
        var history = History("Marex", d => d >= D(10, 22) ? 300 : 1000);

        // Pass on Fri 11-13: Thu 11-12 has 3 post-baseline samples (11-05, 10-29, 10-22).
        var early = Eval(history, settings, today: D(11, 13));
        Assert.Empty(early.Anomalies);
        Assert.Contains(early.Unjudged, u => u.DayCentral == D(11, 12) && u.Reason.StartsWith("3 baseline sample(s)"));

        // Pass on Fri 11-20: Thu 11-19 has 4 → judged normal at the new level.
        var later = Eval(history, settings);
        Assert.Empty(later.Anomalies);
        Assert.True(Judged(later, "Marex", Thu1119));
        Assert.Equal(D(10, 22), later.BaselineFromByLoader["MAREX"]);   // case-insensitive
    }

    [Fact]
    public void BaselineFromCentral_IsCarriedOnTheAnomaly()
    {
        var settings = Settings();
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { BaselineFromCentral = " 2026-10-01 " };
        var a = Assert.Single(Eval(History("Marex", d => d == Thu1119 ? 10 : 1000), settings).Anomalies);
        Assert.Equal(D(10, 1), a.BaselineFromCentral);
        Assert.All(a.Samples, s => Assert.True(s.DayCentral >= D(10, 1)));
    }

    // ----------------------------------------------------------- settings ------

    [Fact]
    public void Defaults_MatchTheDesign_AndAreValid()
    {
        var s = new WatchdogDataVolumeSettings();
        Assert.False(s.Enabled);
        Assert.False(s.Notify);
        Assert.Equal(3, s.WindowDays);
        Assert.Equal(6, s.BaselineWeeks);
        Assert.Equal(18, s.MaxBaselineLookbackWeeks);
        Assert.Equal(4, s.MinSamples);
        Assert.Equal(100, s.MinMedianRows);
        Assert.Equal(50, s.LowPercent);
        Assert.Equal(3, s.HighMultiple);
        Assert.Null(WatchdogVolumeEvaluator.ValidateSection(s));
        Assert.True(new WatchdogLoaderVolumeSettings().Enabled);
    }

    [Fact]
    public void Item21_MinSamplesAboveBaselineWeeks_IsAnInvalidSection()
    {
        var reason = WatchdogVolumeEvaluator.ValidateSection(new WatchdogDataVolumeSettings { MinSamples = 7, BaselineWeeks = 6 });
        Assert.NotNull(reason);
        Assert.Contains("MinSamples=7 must be <= BaselineWeeks=6", reason);
    }

    public static IEnumerable<object[]> InvalidSections() => new[]
    {
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.WindowDays = 0), "WindowDays" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => { s.BaselineWeeks = 0; s.MinSamples = 0; }), "BaselineWeeks" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.MinSamples = 0), "MinSamples" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.MaxBaselineLookbackWeeks = 5), "MaxBaselineLookbackWeeks" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.MinMedianRows = -1), "MinMedianRows" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.LowPercent = 0), "LowPercent" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.LowPercent = 100), "LowPercent" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.HighMultiple = 1), "HighMultiple" },
        new object[] { (Action<WatchdogDataVolumeSettings>)(s => s.HighMultiple = double.NaN), "HighMultiple" },
    };

    [Theory]
    [MemberData(nameof(InvalidSections))]
    public void ValidateSection_RejectsEachOutOfRangeValue(Action<WatchdogDataVolumeSettings> mutate, string named)
    {
        var s = new WatchdogDataVolumeSettings();
        mutate(s);
        Assert.Contains(named, WatchdogVolumeEvaluator.ValidateSection(s));
    }

    [Fact]
    public void ValidateSection_NullSection_IsInvalid() =>
        Assert.NotNull(WatchdogVolumeEvaluator.ValidateSection(null));

    [Theory]
    [InlineData(null, null, null, "11/12/2026", "BaselineFromCentral")]
    [InlineData(null, null, null, "2026-13-01", "BaselineFromCentral")]
    [InlineData(0, null, null, null, "LowPercent")]
    [InlineData(null, 0.5, null, null, "HighMultiple")]
    [InlineData(null, null, -5, null, "MinMedianRows")]
    public void PerLoaderInvalid_SkipsThatLoaderOnly_WithAReason(int? low, double? high, int? minMedian, string? baseline, string named)
    {
        var settings = Settings(loaders: new[] { "Marex", "ICE" });
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings
        {
            LowPercent = low, HighMultiple = high, MinMedianRows = minMedian, BaselineFromCentral = baseline
        };
        var history = History("Marex", d => d == Thu1119 ? 0 : 1000);
        history.AddRange(History("ICE", d => d == Thu1119 ? 0 : 1000));

        var r = Eval(history, settings);

        var skipped = Assert.Single(r.SkippedLoaders);
        Assert.Equal("Marex", skipped.LoaderName);
        Assert.Contains(named, skipped.Reason);
        Assert.Equal("ICE", Assert.Single(r.Anomalies).LoaderName);
        Assert.DoesNotContain(r.JudgedInRange, j => j.LoaderName == "Marex");
    }

    [Fact]
    public void MonitoredLoaders_SkipsDisabledAndVolumeDisabled_KeepsConfigSpelling()
    {
        var settings = Settings(loaders: new[] { "Marex", "ICE", "AGSI" });
        settings.Loaders["ICE"].Enabled = false;
        settings.Loaders["AGSI"].Volume = new WatchdogLoaderVolumeSettings { Enabled = false };

        Assert.Equal(new[] { "Marex" }, WatchdogVolumeEvaluator.MonitoredLoaders(settings));
    }

    [Fact]
    public void Item17_VolumeDisabledLoader_IsNotEvaluated()
    {
        var settings = Settings();
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { Enabled = false };

        var r = Eval(History("Marex", d => d == Thu1119 ? 0 : 1000), settings);

        Assert.Empty(r.Anomalies);
        Assert.Empty(r.JudgedInRange);
        Assert.Empty(r.Unjudged);
    }

    [Fact]
    public void AnomalyLoaderName_IsTheConfigSpelling_NotTheDatabaseOne()
    {
        var a = Assert.Single(Eval(History("MAREX", d => d == Thu1119 ? 0 : 1000), Settings()).Anomalies);
        Assert.Equal("Marex", a.LoaderName);
    }

    [Fact]
    public void LoaderNotInConfig_IsNeverEvaluated()
    {
        var r = Eval(History("Platts", d => d == Thu1119 ? 0 : 1000), Settings());
        Assert.Empty(r.Anomalies);
        Assert.DoesNotContain(r.Unjudged, u => u.LoaderName == "Platts");
    }

    // -------------------------------------------------------------- median ------

    [Fact]
    public void Median_EvenCount_IsTheMeanOfTheMiddleTwo()
    {
        Assert.Equal(2.5, WatchdogVolumeEvaluator.Median(new long[] { 4, 1, 3, 2 }));
        Assert.Equal(150, WatchdogVolumeEvaluator.Median(new long[] { 100, 200 }));
        Assert.Equal(1_000_000_000_000.5, WatchdogVolumeEvaluator.Median(new long[] { 1_000_000_000_000, 1_000_000_000_001 }));
    }

    [Fact]
    public void Median_OddCount_IsTheMiddle_AndEmptyIsZero()
    {
        Assert.Equal(3, WatchdogVolumeEvaluator.Median(new long[] { 9, 3, 1 }));
        Assert.Equal(0, WatchdogVolumeEvaluator.Median(Array.Empty<long>()));
    }

    [Fact]
    public void EvenSampleCount_UsesTheTrueMedian_ForTheDecision()
    {
        // Six samples 400,400,400,1600,1600,1600 → median 1000; 450 < 500 → Low.
        var thursdays = new Dictionary<DateOnly, long>
        {
            [D(11, 12)] = 400, [D(11, 5)] = 1600, [D(10, 29)] = 400,
            [D(10, 22)] = 1600, [D(10, 15)] = 400, [D(10, 8)] = 1600
        };
        var history = History("Marex", d => d == Thu1119 ? 450 : thursdays.TryGetValue(d, out var r) ? r : d.DayOfWeek == DayOfWeek.Thursday ? null : 1000);

        var a = Assert.Single(Eval(history, Settings()).Anomalies);
        Assert.Equal(1000, a.MedianRows);
        Assert.Equal(VolumeDirection.Low, a.Direction);
    }

    // ------------------------------------------------------- key parsing ------

    [Theory]
    [InlineData("DATA_VOLUME|Marex|2026-11-19", "Marex", "2026-11-19")]
    [InlineData("data_volume|ICE|2026-01-02", "ICE", "2026-01-02")]
    [InlineData("DATA_VOLUME|A|B|2026-11-19", "A|B", "2026-11-19")]
    public void ParseVolumeFindingKey_ReadsLoaderAndDay(string key, string loader, string day)
    {
        var parsed = SqlWatchdogRepository.ParseVolumeFindingKey(key);
        Assert.NotNull(parsed);
        Assert.Equal(loader, parsed!.Value.LoaderName);
        Assert.Equal(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), parsed.Value.DayCentral);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OVERDUE|Marex")]
    [InlineData("OVERDUE|Marex|2026-11-19")]
    [InlineData("DATA_VOLUME|Marex")]
    [InlineData("DATA_VOLUME||2026-11-19")]
    [InlineData("DATA_VOLUME| |2026-11-19")]
    [InlineData("DATA_VOLUME|Marex|11/19/2026")]
    [InlineData("DATA_VOLUME|Marex|2026-11-19T00:00")]
    public void ParseVolumeFindingKey_IgnoresAnythingElse(string? key) =>
        Assert.Null(SqlWatchdogRepository.ParseVolumeFindingKey(key));

    [Fact]
    public void FindingKey_RoundTripsThroughTheParser()
    {
        var key = WatchdogFindingMapper.VolumeFindingKey("Marex", Thu1119);
        Assert.Equal("DATA_VOLUME|Marex|2026-11-19", key);
        Assert.Equal(new VolumeLoaderDay("Marex", Thu1119), SqlWatchdogRepository.ParseVolumeFindingKey(key));
    }
}
