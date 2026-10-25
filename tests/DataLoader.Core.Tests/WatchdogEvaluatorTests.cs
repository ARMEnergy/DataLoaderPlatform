using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

public class WatchdogEvaluatorTests
{
    private static readonly DateTime NowCentral = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Unspecified);

    private static WatchdogSettings Settings(params (string Loader, int MaxGapHours)[] loaders)
    {
        var s = new WatchdogSettings { StuckAfterHours = 6, ReportRecentFailureHours = 24 };
        foreach (var (loader, gap) in loaders)
            s.Loaders[loader] = new WatchdogLoaderSettings { MaxGapHours = gap };
        return s;
    }

    private static WatchdogReport Evaluate(
        WatchdogSettings settings,
        IEnumerable<LoaderLastRun>? lastRuns = null,
        IEnumerable<StuckRun>? stuck = null,
        IEnumerable<RecentFailure>? failures = null,
        IEnumerable<string>? degraded = null)
        => WatchdogEvaluator.Evaluate(
            (lastRuns ?? Array.Empty<LoaderLastRun>()).ToList(),
            (stuck ?? Array.Empty<StuckRun>()).ToList(),
            (failures ?? Array.Empty<RecentFailure>()).ToList(),
            settings,
            "TestHost",
            NowCentral,
            (degraded ?? Array.Empty<string>()).ToList());

    // ---------------------------------------------------------------- overdue

    [Fact]
    public void Overdue_WhenTheGapExceedsMaxGapHours()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-30)) });

        var overdue = Assert.Single(report.Overdue);
        Assert.Equal("Platts", overdue.LoaderName);
        Assert.Equal(26, overdue.MaxGapHours);
    }

    [Fact]
    public void NotOverdue_WhenInsideTheWindow()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-2)) });

        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void NotOverdue_ExactlyAtTheBoundary()
    {
        // Strictly greater-than. A loader that runs every 24h with a 24h
        // threshold must not alert on every single run because of jitter.
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-26)) });

        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void Overdue_WhenAConfiguredLoaderHasNeverRunAtAll()
    {
        var report = Evaluate(Settings(("Platts", 26)));

        var overdue = Assert.Single(report.Overdue);
        Assert.Equal("Platts", overdue.LoaderName);
        Assert.Null(overdue.LastStartCentral);
    }

    [Fact]
    public void Ignored_WhenALoaderRanButIsNotConfigured()
    {
        // Build-only loaders get run by hand once and must not alert forever.
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[]
            {
                new LoaderLastRun("Platts", NowCentral.AddHours(-1)),
                new LoaderLastRun("NUCORE", NowCentral.AddDays(-200))
            });

        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void LoaderNameMatchingIsCaseInsensitive()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("PLATTS", NowCentral.AddHours(-1)) });

        Assert.Empty(report.Overdue);
    }

    // ⚠ the regression guard for the time-base trap
    [Fact]
    public void ComparesAgainstCentral_NotUtc()
    {
        // StartTime in core.LoaderRun_Summary is already Central. Passing a UTC
        // "now" would add ~5h to every gap and report the whole platform
        // overdue. One hour ago, against a 5h threshold, must be quiet.
        var lastRun = new[] { new LoaderLastRun("Criterion", NowCentral.AddHours(-1)) };
        var settings = Settings(("Criterion", 5));

        var correct = WatchdogEvaluator.Evaluate(
            lastRun, Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", NowCentral,
            Array.Empty<string>());
        Assert.Empty(correct.Overdue);

        // Documents the failure mode: the same data against a UTC-shifted clock.
        var wrong = WatchdogEvaluator.Evaluate(
            lastRun, Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", NowCentral.AddHours(5),
            Array.Empty<string>());
        Assert.Single(wrong.Overdue);
    }

    // ------------------------------------------------------------ SkipWeekends

    [Fact]
    public void SkipWeekends_GapEntirelyWithinOneWeekend_IsNotOverdue()
    {
        // Saturday 08:00 to Sunday 20:00 is a 36h wall-clock gap but touches no
        // weekday hours at all — the weekday-only elapsed time is ~0.
        var lastStart = new DateTime(2026, 10, 24, 8, 0, 0);  // Saturday
        var now = new DateTime(2026, 10, 25, 20, 0, 0);       // Sunday
        Assert.Equal(DayOfWeek.Saturday, lastStart.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, now.DayOfWeek);

        var settings = new WatchdogSettings();
        settings.Loaders["ICE"] = new WatchdogLoaderSettings { MaxGapHours = 6, SkipWeekends = true };

        var report = WatchdogEvaluator.Evaluate(
            new[] { new LoaderLastRun("ICE", lastStart) },
            Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", now,
            Array.Empty<string>());

        // A 36h raw gap against a 6h threshold would fire without the
        // weekend exclusion — this is the regression guard for that.
        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void SkipWeekends_FridayEveningToMondayMorning_CountsOnlyTheWeekdayEdges()
    {
        // Friday 18:00 -> Monday 09:00 is 63h of wall clock (2 days 15h), but
        // only Friday's last 6h and Monday's first 9h are weekdays: 15h.
        var lastStart = new DateTime(2026, 10, 23, 18, 0, 0); // Friday
        var now = new DateTime(2026, 10, 26, 9, 0, 0);        // Monday
        Assert.Equal(DayOfWeek.Friday, lastStart.DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, now.DayOfWeek);

        var skipSettings = new WatchdogSettings();
        skipSettings.Loaders["ICE"] = new WatchdogLoaderSettings { MaxGapHours = 20, SkipWeekends = true };

        var skipReport = WatchdogEvaluator.Evaluate(
            new[] { new LoaderLastRun("ICE", lastStart) },
            Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), skipSettings, "TestHost", now,
            Array.Empty<string>());

        Assert.Empty(skipReport.Overdue); // 15h counted <= 20h threshold

        // Same data, SkipWeekends off: the raw 63h gap blows straight through
        // the same 20h threshold. Proves the first assertion is the weekend
        // exclusion at work, not a loose threshold.
        var rawSettings = new WatchdogSettings();
        rawSettings.Loaders["ICE"] = new WatchdogLoaderSettings { MaxGapHours = 20, SkipWeekends = false };

        var rawReport = WatchdogEvaluator.Evaluate(
            new[] { new LoaderLastRun("ICE", lastStart) },
            Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), rawSettings, "TestHost", now,
            Array.Empty<string>());

        var overdue = Assert.Single(rawReport.Overdue);
        Assert.Equal(63, overdue.HoursSinceLastRun, precision: 0);
    }

    [Fact]
    public void SkipWeekends_GapSpanningSeveralWeeks_ExcludesEveryWeekendInBetween()
    {
        // Three Mondays apart (21 days = 504h wall clock) crosses 3 complete
        // weekends (6 weekend days = 144h excluded), leaving 15 weekday days
        // = 360h of counted elapsed time.
        var lastStart = new DateTime(2026, 10, 5, 8, 0, 0);  // Monday
        var now = new DateTime(2026, 10, 26, 8, 0, 0);       // Monday, 3 weeks later
        Assert.Equal(DayOfWeek.Monday, lastStart.DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, now.DayOfWeek);
        Assert.Equal(21, (now - lastStart).Days);

        var settings = new WatchdogSettings();
        settings.Loaders["Criterion"] = new WatchdogLoaderSettings { MaxGapHours = 359, SkipWeekends = true };

        var report = WatchdogEvaluator.Evaluate(
            new[] { new LoaderLastRun("Criterion", lastStart) },
            Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", now,
            Array.Empty<string>());

        var overdue = Assert.Single(report.Overdue);
        Assert.Equal(360, overdue.HoursSinceLastRun, precision: 0);

        // One hour above the computed weekday-only gap: now quiet.
        settings.Loaders["Criterion"].MaxGapHours = 361;
        var quietReport = WatchdogEvaluator.Evaluate(
            new[] { new LoaderLastRun("Criterion", lastStart) },
            Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", now,
            Array.Empty<string>());
        Assert.Empty(quietReport.Overdue);
    }

    [Fact]
    public void SkipWeekends_DefaultsToFalse_SoAnExistingEntryIsUnaffected()
    {
        // WatchdogLoaderSettings.SkipWeekends is never set here, exactly like
        // every config entry before this feature existed — pins that the
        // default path still compares the raw wall-clock gap.
        var settings = new WatchdogSettings();
        settings.Loaders["Platts"] = new WatchdogLoaderSettings { MaxGapHours = 26 };
        Assert.False(settings.Loaders["Platts"].SkipWeekends);

        var report = WatchdogEvaluator.Evaluate(
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-30)) },
            Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", NowCentral,
            Array.Empty<string>());

        var overdue = Assert.Single(report.Overdue);
        Assert.Equal(30, overdue.HoursSinceLastRun, precision: 0);
    }

    // ------------------------------------------------------------------ stuck

    [Fact]
    public void StuckRunsArePassedThrough()
    {
        var stuck = new[] { new StuckRun(Guid.NewGuid(), DateTime.UtcNow.AddHours(-20), "ARMH-OPSDB01") };

        var report = Evaluate(Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) }, stuck);

        Assert.Single(report.Stuck);
        Assert.True(report.ShouldNotify);
    }

    // --------------------------------------------------------------- quietness

    [Fact]
    public void ShouldNotify_False_WhenEverythingIsHealthy()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) });

        Assert.False(report.ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_False_OnEmptyInput_WithNoLoadersConfigured()
    {
        Assert.False(Evaluate(new WatchdogSettings()).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_OnRecentFailures()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) },
            failures: new[] { new RecentFailure("Platts", NowCentral.AddHours(-2), 3, 14) });

        Assert.True(report.ShouldNotify);
    }

    [Fact]
    public void DisabledLoadersAreSkipped()
    {
        var settings = Settings(("Platts", 26));
        settings.Loaders["Platts"].Enabled = false;

        Assert.Empty(Evaluate(settings).Overdue);
    }

    // ---------------------------------------------------------------- message

    [Fact]
    public void Subject_SummarisesWhatIsWrong()
    {
        var report = Evaluate(
            Settings(("Platts", 26), ("CWG", 26)),
            stuck: new[] { new StuckRun(Guid.NewGuid(), DateTime.UtcNow.AddHours(-20), "ARMH-OPSDB01") });

        var subject = report.BuildMessage().Subject;

        Assert.Contains("WATCHDOG", subject);
        Assert.Contains("2 loaders overdue", subject);
        Assert.Contains("1 run stuck", subject);
    }

    [Fact]
    public void Body_NamesEveryOverdueLoaderAndHowLateItIs()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-30)) });

        var body = report.BuildMessage().Body;

        Assert.Contains("Platts", body);
        Assert.Contains("30", body);
    }

    [Fact]
    public void BuildMessage_Throws_WhenNothingToReport()
    {
        var report = new WatchdogReport();

        Assert.Throws<InvalidOperationException>(() => report.BuildMessage());
    }

    [Fact]
    public void ReportsOverdueStuckAndFailuresTogether()
    {
        // The realistic shape: production currently has 5 stuck rows and 43
        // failures in 7 days, so a real pass will rarely contain just one kind.
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-30)) },
            stuck: new[] { new StuckRun(Guid.NewGuid(), DateTime.UtcNow.AddHours(-20), "ARMH-OPSDB01") },
            failures: new[] { new RecentFailure("EnergyAspects", NowCentral.AddHours(-2), 3, 14) });

        var message = report.BuildMessage();

        Assert.Contains("1 loader overdue", message.Subject);
        Assert.Contains("1 run stuck", message.Subject);
        Assert.Contains("1 recent failure", message.Subject);
        Assert.Contains("Platts", message.Body);
        Assert.Contains("EnergyAspects", message.Body);
    }

    [Fact]
    public void AMisconfiguredLoaderIsReported_NotSilentlySkipped()
    {
        var settings = new WatchdogSettings();
        settings.Loaders["Platts"] = new WatchdogLoaderSettings();   // MaxGapHours left at 0

        var report = Evaluate(settings);

        Assert.Equal("Platts", Assert.Single(report.Misconfigured));
        Assert.True(report.ShouldNotify);
        Assert.Contains("Platts", report.BuildMessage().Body);
    }

    [Fact]
    public void ADisabledLoaderWithNoThresholdIsNotMisconfigured()
    {
        // Deliberately muted, not broken.
        var settings = new WatchdogSettings();
        settings.Loaders["Platts"] = new WatchdogLoaderSettings { Enabled = false };

        var report = Evaluate(settings);

        Assert.Empty(report.Misconfigured);
        Assert.False(report.ShouldNotify);
    }

    [Fact]
    public void ANullLoadersSectionIsTreatedAsEmpty_NotAThrow()
    {
        // "Loaders": null in config used to throw NullReferenceException out of
        // this pure function; the caller's catch then mislabelled a config
        // mistake as "Watchdog could not read run history". It must instead
        // behave exactly like an empty Loaders section: nothing configured,
        // nothing to report.
        var settings = new WatchdogSettings { Loaders = null! };

        var report = Evaluate(settings);

        Assert.False(report.ShouldNotify);
        Assert.Empty(report.Overdue);
        Assert.Empty(report.Misconfigured);
    }

    // -------------------------------------------------------------- degraded

    [Fact]
    public void Degraded_TripsShouldNotify_EvenWithNothingElseWrong()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) },
            degraded: new[] { "stuck runs (InvalidOperationException: db down)" });

        Assert.True(report.ShouldNotify);
        Assert.Single(report.Degraded);
    }

    [Fact]
    public void Subject_IncludesTheDegradedCount()
    {
        var report = Evaluate(new WatchdogSettings(),
            degraded: new[] { "stuck runs (InvalidOperationException: db down)" });

        Assert.Contains("1 check degraded", report.BuildMessage().Subject);
    }

    [Fact]
    public void Body_ListsEveryDegradedCheckAndWhy()
    {
        var report = Evaluate(new WatchdogSettings(),
            degraded: new[] { "stuck runs (InvalidOperationException: db down)" });

        var body = report.BuildMessage().Body;

        Assert.Contains("COULD NOT CHECK", body);
        Assert.Contains("stuck runs", body);
        Assert.Contains("db down", body);
    }
}
