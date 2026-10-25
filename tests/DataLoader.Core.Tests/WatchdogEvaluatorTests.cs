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
        IEnumerable<RecentFailure>? failures = null)
        => WatchdogEvaluator.Evaluate(
            (lastRuns ?? Array.Empty<LoaderLastRun>()).ToList(),
            (stuck ?? Array.Empty<StuckRun>()).ToList(),
            (failures ?? Array.Empty<RecentFailure>()).ToList(),
            settings,
            "TestHost",
            NowCentral);

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
            lastRun, Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", NowCentral);
        Assert.Empty(correct.Overdue);

        // Documents the failure mode: the same data against a UTC-shifted clock.
        var wrong = WatchdogEvaluator.Evaluate(
            lastRun, Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, "TestHost", NowCentral.AddHours(5));
        Assert.Single(wrong.Overdue);
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
}
