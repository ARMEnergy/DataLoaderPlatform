using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

public class WatchdogRunnerTests
{
    private sealed class FakeRepository : IWatchdogRepository
    {
        public List<LoaderLastRun> LastRuns = new();
        public List<StuckRun> Stuck = new();

        // What GetRecentFailuresAsync returns — already split into reported vs
        // suppressed, exactly as SqlWatchdogRepository would hand back after resolution.
        // These tests exercise WatchdogRunner's own handling of that split (does it log
        // the suppressed ones, does it refrain from re-filtering Failures, does it thread
        // the flag through) — the resolution ALGORITHM itself is pure and is tested
        // directly in WatchdogFailureResolverTests, with no database or fake needed.
        public List<RecentFailure> Failures = new();
        public List<RecentFailure> Suppressed = new();

        /// <summary>Captures what WatchdogRunner actually passed, so a test can confirm
        /// <see cref="WatchdogSettings.SkipAlertIfResolved"/> made it all the way through.</summary>
        public bool? ReceivedSkipAlertIfResolved;

        // GetLastRunsAsync is the primary signal: a failure here aborts the
        // whole pass. GetStuckRunsAsync/GetRecentFailuresAsync are secondary
        // and must degrade independently of it and of each other — see
        // WatchdogRunner.RunAsync and WatchdogReport.Degraded.
        public Exception? ThrowThis;
        public Exception? ThrowOnStuck;
        public Exception? ThrowOnFailures;

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) =>
            ThrowThis is not null
                ? Task.FromException<IReadOnlyList<LoaderLastRun>>(ThrowThis)
                : Task.FromResult<IReadOnlyList<LoaderLastRun>>(LastRuns);

        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(
            int h, int days, string? host, CancellationToken ct) =>
            ThrowOnStuck is not null
                ? Task.FromException<IReadOnlyList<StuckRun>>(ThrowOnStuck)
                : Task.FromResult<IReadOnlyList<StuckRun>>(Stuck);

        public Task<RecentFailuresResult> GetRecentFailuresAsync(
            int h, DateTime n, bool skipAlertIfResolved, CancellationToken ct)
        {
            ReceivedSkipAlertIfResolved = skipAlertIfResolved;
            return ThrowOnFailures is not null
                ? Task.FromException<RecentFailuresResult>(ThrowOnFailures)
                : Task.FromResult(new RecentFailuresResult(Failures, Suppressed));
        }
    }

    /// <summary>Captures formatted log lines so a test can assert on their content,
    /// unlike <see cref="NullLogger{T}"/> which discards everything.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeNotifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();
        public NotificationOutcome Outcome = NotificationOutcome.Sent;

        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>
    /// Violates IFailureNotifier's documented never-throw contract on purpose,
    /// to prove WatchdogRunner defends itself rather than trusting the
    /// interface's doc comment — matching PlatformHost.ReportRunOutcomeAsync's
    /// identical defensive wrap around the same call.
    /// </summary>
    private sealed class ThrowingNotifier : IFailureNotifier
    {
        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct) =>
            throw new InvalidOperationException("notifier contract violated");
    }

    private static WatchdogRunner Build(
        FakeRepository repo, FakeNotifier notifier, params (string Loader, int Gap)[] loaders) =>
        Build(repo, (IFailureNotifier)notifier, loaders);

    private static WatchdogRunner Build(
        FakeRepository repo, IFailureNotifier notifier, params (string Loader, int Gap)[] loaders)
    {
        var settings = new WatchdogSettings();
        foreach (var (loader, gap) in loaders)
            settings.Loaders[loader] = new WatchdogLoaderSettings { MaxGapHours = gap };

        return new WatchdogRunner(
            repo, notifier, Options.Create(settings), NullLogger<WatchdogRunner>.Instance);
    }

    private static WatchdogRunner Build(
        FakeRepository repo, IFailureNotifier notifier, WatchdogSettings settings, ILogger<WatchdogRunner> logger) =>
        new(repo, notifier, Options.Create(settings), logger);

    [Fact]
    public async Task ExitsZeroAndSendsNothing_WhenEverythingIsHealthy()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) }
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        Assert.All(notifier.Payloads, p => Assert.False(p.ShouldNotify));
    }

    [Fact]
    public async Task ExitsZeroButReports_WhenALoaderIsOverdue()
    {
        // Finding an overdue loader is the watchdog succeeding, not failing.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Single(report.Overdue);
    }

    [Fact]
    public async Task ExitsOne_WhenTheDatabaseCannotBeRead()
    {
        var repo = new FakeRepository { ThrowThis = new InvalidOperationException("db down") };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task StillTriesToEmail_WhenTheDatabaseCannotBeRead()
    {
        // An unreachable platform DB is itself alert-worthy, and the in-process
        // notifier cannot report it either — nothing ran.
        var repo = new FakeRepository { ThrowThis = new InvalidOperationException("db down") };
        var notifier = new FakeNotifier();

        await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        var payload = Assert.Single(notifier.Payloads);
        Assert.True(payload.ShouldNotify);
    }

    [Fact]
    public async Task ExitsOne_WhenItFoundSomethingButCouldNotDeliver()
    {
        // An undelivered watchdog alert is indistinguishable from all-clear,
        // so the exit code has to carry the bad news instead.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };
        var notifier = new FakeNotifier { Outcome = NotificationOutcome.Failed };

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ExitsZero_WhenNotificationsAreMerelyDisabled()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };
        var notifier = new FakeNotifier { Outcome = NotificationOutcome.Disabled };

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
    }

    // ----------------------------------------------------- independent degrade

    [Fact]
    public async Task OverdueFindingSurvives_WhenTheStuckRunsCheckFails()
    {
        // One try wrapping all three repository calls used to throw the
        // overdue finding away the moment ANY of them failed. A secondary
        // query failing must not hide what the primary query already found.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) },
            ThrowOnStuck = new InvalidOperationException("stuck query broke")
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Single(report.Overdue);
        var degraded = Assert.Single(report.Degraded);
        Assert.Contains("stuck runs", degraded);
        Assert.Contains("stuck query broke", degraded);
    }

    [Fact]
    public async Task OverdueFindingSurvives_WhenTheRecentFailuresCheckFails()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) },
            ThrowOnFailures = new InvalidOperationException("failures query broke")
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Single(report.Overdue);
        var degraded = Assert.Single(report.Degraded);
        Assert.Contains("recent failures", degraded);
        Assert.Contains("failures query broke", degraded);
    }

    [Fact]
    public async Task ADegradedCheckAlone_StillNotifies_EvenWithNoOtherFinding()
    {
        // Degraded alone must trip ShouldNotify — a watchdog that can only
        // half-see must say so, even when nothing else is wrong.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) },
            ThrowOnStuck = new InvalidOperationException("stuck query broke")
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Empty(report.Overdue);
        Assert.Single(report.Degraded);
        Assert.True(report.ShouldNotify);
    }

    // -------------------------------------------------- zero loaders configured

    [Fact]
    public async Task DegradesAndNotifies_WhenNoLoadersAreConfiguredForMonitoring()
    {
        // A watchdog monitoring zero loaders reports "all healthy" forever,
        // which is indistinguishable from a working platform — the exact
        // blindness this component exists to prevent. This is also what a
        // malformed Platform:Watchdog:Loaders section looks like from here: a
        // scalar string, or an entry with a non-numeric MaxGapHours, binds
        // SILENTLY to an empty dictionary, with nothing landing for
        // WatchdogEvaluator's per-entry Misconfigured guard to ever see.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) }
        };
        var notifier = new FakeNotifier();

        // No loader tuples passed — settings.Loaders stays the empty dictionary
        // it defaults to.
        var exit = await Build(repo, notifier).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.True(report.ShouldNotify);
        var degraded = Assert.Single(report.Degraded);
        Assert.Contains("no loaders are configured", degraded, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------- notifier contract violated

    [Fact]
    public async Task ExitsOne_NoEscape_WhenTheNotifierThrowsOnTheMainPath()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };

        var exit = await Build(repo, new ThrowingNotifier(), ("Platts", 26)).RunAsync(default);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ExitsOne_NoEscape_WhenTheNotifierThrowsOnTheDatabaseFailureCatchPath()
    {
        var repo = new FakeRepository { ThrowThis = new InvalidOperationException("db down") };

        var exit = await Build(repo, new ThrowingNotifier(), ("Platts", 26)).RunAsync(default);

        Assert.Equal(1, exit);
    }

    // --------------------------------------------- SkipAlertIfResolved / suppression
    //
    // The resolution ALGORITHM — matching on (LoaderId, WorkUnitDisplay), never
    // WorkUnitKey — is pure and lives in WatchdogFailureResolver; it is tested directly,
    // with no fake or database, in WatchdogFailureResolverTests. What belongs here is
    // WatchdogRunner's own job: thread the setting through to the repository, trust the
    // repository's Failures/Suppressed split without re-filtering it, and log what was
    // suppressed — including never letting a suppressed-only pass notify.

    [Fact]
    public async Task SkipAlertIfResolved_False_IsPassedToTheRepository_AndRunnerReportsExactlyWhatItReturns()
    {
        // Simulates what SqlWatchdogRepository returns when the setting is off: every
        // candidate in Failures, unfiltered, nothing in Suppressed. WatchdogRunner must
        // not re-apply any filtering of its own on top of that — the report should show
        // exactly what the repository handed back, "as today".
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) },
            Failures =
            {
                new RecentFailure("Platts", DateTime.Now.AddHours(-2), 3, 14, 3),
                new RecentFailure("EnergyAspects", DateTime.Now.AddHours(-1), 1, 7, 1)
            }
        };
        var settings = new WatchdogSettings { SkipAlertIfResolved = false };
        settings.Loaders["Platts"] = new WatchdogLoaderSettings { MaxGapHours = 26 };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, settings, NullLogger<WatchdogRunner>.Instance).RunAsync(default);

        Assert.Equal(0, exit);
        Assert.Equal(false, repo.ReceivedSkipAlertIfResolved);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Equal(2, report.RecentFailures.Count);
    }

    [Fact]
    public async Task SuppressionAlone_NeverTriggersANotification()
    {
        // All-resolved and nothing else wrong: ShouldNotify must be false, and the
        // suppressed finding must never appear in RecentFailures.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) },
            Suppressed = { new RecentFailure("Platts", DateTime.Now.AddHours(-2), 3, 14, 0) }
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Empty(report.RecentFailures);
        Assert.False(report.ShouldNotify);
        Assert.All(notifier.Payloads, p => Assert.False(p.ShouldNotify));
    }

    [Fact]
    public async Task SuppressedCount_ReachesTheSummaryLogLine()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) },
            Suppressed =
            {
                new RecentFailure("Platts", DateTime.Now.AddHours(-2), 3, 14, 0),
                new RecentFailure("AGSI", DateTime.Now.AddHours(-3), 25, 2630, 0)
            }
        };
        var notifier = new FakeNotifier();
        var settings = new WatchdogSettings();
        settings.Loaders["Platts"] = new WatchdogLoaderSettings { MaxGapHours = 26 };
        var logger = new CapturingLogger<WatchdogRunner>();

        await Build(repo, notifier, settings, logger).RunAsync(default);

        // The existing "Watchdog: N overdue, N stuck, …" summary line now carries the
        // suppressed count too, so it is visible even on a pass where nothing else fires.
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information &&
                 e.Message.Contains("Watchdog:") &&
                 e.Message.Contains("2 suppressed as resolved"));

        // Plus a dedicated line naming the count, and per-item debug detail for the
        // Log\Watchdog.log audit trail.
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("2 recent failure(s) suppressed"));
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Platts"));
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("AGSI"));
    }
}
