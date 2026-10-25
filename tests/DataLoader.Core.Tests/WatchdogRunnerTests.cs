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
        public Exception? ThrowThis;

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) =>
            ThrowThis is not null
                ? Task.FromException<IReadOnlyList<LoaderLastRun>>(ThrowThis)
                : Task.FromResult<IReadOnlyList<LoaderLastRun>>(LastRuns);

        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(
            int h, int days, string? host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StuckRun>>(Stuck);

        public Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(int h, DateTime n, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecentFailure>>(Failures);
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

    private static WatchdogRunner Build(
        FakeRepository repo, FakeNotifier notifier, params (string Loader, int Gap)[] loaders)
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
}
