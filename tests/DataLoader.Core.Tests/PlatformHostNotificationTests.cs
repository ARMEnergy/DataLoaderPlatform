using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Notifications;
using DataLoader.Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// The host's side of failure notification: who gets told, and when nobody
/// should be.
/// </summary>
public class PlatformHostNotificationTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeModule : ILoaderModule
    {
        private readonly Func<LoaderRunContext, Task<LoaderRunResult>> _run;
        public FakeModule(string id, Func<LoaderRunContext, Task<LoaderRunResult>> run)
        {
            LoaderId = id; _run = run;
        }
        public string LoaderId { get; }
        public string DisplayName => LoaderId;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context) => _run(context);

        public static FakeModule Succeeding(string id) => new(id, _ =>
            Task.FromResult(new LoaderRunResult { LoaderId = id, Success = true }));
        public static FakeModule Failing(string id) => new(id, _ =>
            Task.FromResult(LoaderRunResult.Failed(id, "boom", TimeSpan.Zero)));
    }

    private sealed class FakeRunLog : ILoadLogRepository
    {
        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task<LoadLogHandle?> BeginAsync(string l, Guid r, string k, string d, CancellationToken ct) =>
            Task.FromResult<LoadLogHandle?>(null);
        public Task CompleteSuccessAsync(LoadLogHandle h, int n, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteFailureAsync(LoadLogHandle h, string m, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOverlapGuard : ILoaderOverlapGuard
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string loaderId, CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(new FakeLock());
    }

    private sealed class FakeAdditionalProcesses : IAdditionalProcessRunner
    {
        public Task RunAsync(string loaderId, Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSweeper : ILoadLogSweeper
    {
        public int Returns;
        public Task<int> AbandonStaleAsync(Guid currentRunId, CancellationToken ct) => Task.FromResult(Returns);
    }

    /// <summary>Captures what the host decided to report, without a transport.</summary>
    private sealed class CapturingNotifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();
        public bool Throw;

        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            // The real notifier never throws. This proves the host does not
            // depend on that politeness.
            return Throw
                ? Task.FromException<NotificationOutcome>(new InvalidOperationException("notifier blew up"))
                : Task.FromResult(NotificationOutcome.Sent);
        }

        public RunFailureReport Single() => Assert.IsType<RunFailureReport>(Assert.Single(Payloads));
    }

    private static PlatformHost Build(
        IEnumerable<ILoaderModule> modules,
        IFailureNotifier? notifier = null,
        ILoadLogSweeper? sweeper = null)
        => new(
            new ServiceCollection().BuildServiceProvider(),
            modules.ToList(),
            new FakeRunLog(),
            new FakeOverlapGuard(),
            new FakeAdditionalProcesses(),
            Options.Create(new PlatformSettings { MaxConcurrentLoaders = 1, AbandonStaleLoadLogs = true }),
            NullLogger<PlatformHost>.Instance,
            sweeper,
            notifier);

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Notifies_WhenALoaderFails()
    {
        var notifier = new CapturingNotifier();

        var exit = await Build(new[] { FakeModule.Failing("Platts") }, notifier).RunAsync(default);

        Assert.Equal(1, exit);
        var report = notifier.Single();
        Assert.True(report.ShouldNotify);
        Assert.Equal("Platts", Assert.Single(report.Results).LoaderId);
    }

    [Fact]
    public async Task DoesNotNotify_WhenEverythingSucceeded()
    {
        var notifier = new CapturingNotifier();

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, notifier).RunAsync(default);

        Assert.Equal(0, exit);
        // The host may still hand over a report; what matters is that it says
        // there is nothing to send.
        foreach (var p in notifier.Payloads) Assert.False(p.ShouldNotify);
    }

    [Fact]
    public async Task Notifies_WhenAnEarlierRunLeftRowsOpen_EvenThoughThisRunSucceeded()
    {
        var notifier = new CapturingNotifier();
        var sweeper = new FakeSweeper { Returns = 3 };

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, notifier, sweeper).RunAsync(default);

        Assert.Equal(0, exit);                 // this run was fine
        var report = notifier.Single();
        Assert.Equal(3, report.StaleRowsClosed);
        Assert.True(report.ShouldNotify);      // but there is still news
    }

    [Fact]
    public async Task TheReportCarriesTheRunIdAndHostName()
    {
        var notifier = new CapturingNotifier();

        await Build(new[] { FakeModule.Failing("Platts") }, notifier).RunAsync(default);

        var report = notifier.Single();
        Assert.NotEqual(Guid.Empty, report.RunId);
        Assert.False(string.IsNullOrWhiteSpace(report.HostName));
    }

    [Fact]
    public async Task ANotifierThatThrowsDoesNotChangeTheRunsOutcome()
    {
        // Belt and braces: the real notifier swallows everything, but the host
        // must not rely on that.
        var notifier = new CapturingNotifier { Throw = true };

        var exit = await Build(new[] { FakeModule.Failing("Platts") }, notifier).RunAsync(default);

        Assert.Equal(1, exit);   // the loader's failure, not the notifier's
    }

    [Fact]
    public async Task ASucceedingRunWithAThrowingNotifierStillExitsZero()
    {
        var notifier = new CapturingNotifier { Throw = true };
        var sweeper = new FakeSweeper { Returns = 2 };   // forces a notify attempt

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, notifier, sweeper).RunAsync(default);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task WorksWithNoNotifierRegistered()
    {
        // The parameter is optional so existing host tests keep compiling and
        // so a deployment with notifications absent still runs.
        var exit = await Build(new[] { FakeModule.Failing("Platts") }).RunAsync(default);

        Assert.Equal(1, exit);
    }
}
