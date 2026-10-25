using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
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
        public List<RecentFailure> Failures = new();

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

        public Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(int h, DateTime n, CancellationToken ct) =>
            ThrowOnFailures is not null
                ? Task.FromException<IReadOnlyList<RecentFailure>>(ThrowOnFailures)
                : Task.FromResult<IReadOnlyList<RecentFailure>>(Failures);
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
}
