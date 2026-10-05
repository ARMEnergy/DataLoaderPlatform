using System.Collections.Concurrent;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// The first tests over <see cref="PlatformHost"/>. Two behaviours are covered:
/// the post-run hook (<c>core.usp_RunAdditionalProcesses</c>) firing rules, and
/// the guarantee that <c>core.LoaderRun</c> is always closed.
///
/// <para>
/// Both matter because the host had NO test coverage at all, which is how 24 of
/// 844 production run rows came to sit with CompletedAtUtc NULL: a cancellation
/// walked straight past the unguarded CompleteRunAsync call and stranded the row.
/// </para>
/// </summary>
public class PlatformHostTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeModule : ILoaderModule
    {
        private readonly Func<LoaderRunContext, Task<LoaderRunResult>> _run;

        public FakeModule(string id, Func<LoaderRunContext, Task<LoaderRunResult>> run)
        {
            LoaderId = id;
            _run = run;
        }

        public string LoaderId { get; }
        public string DisplayName => LoaderId;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context) => _run(context);

        public static FakeModule Succeeding(string id) => new(id, _ =>
            Task.FromResult(new LoaderRunResult { LoaderId = id, Success = true }));

        public static FakeModule Failing(string id) => new(id, _ =>
            Task.FromResult(LoaderRunResult.Failed(id, "boom", TimeSpan.Zero)));

        public static FakeModule Throwing(string id) => new(id, _ =>
            Task.FromException<LoaderRunResult>(new InvalidOperationException("thrown")));
    }

    private sealed class FakeRunLog : ILoadLogRepository
    {
        public int BeginRunCalls;
        public int CompleteRunCalls;
        public bool? CompletedSuccess;

        public Task BeginRunAsync(Guid runId, CancellationToken ct)
        {
            BeginRunCalls++;
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct)
        {
            CompleteRunCalls++;
            CompletedSuccess = success;
            return Task.CompletedTask;
        }

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
        private readonly Func<string, Task<IAsyncDisposable?>> _acquire;

        public FakeOverlapGuard(Func<string, Task<IAsyncDisposable?>>? acquire = null) =>
            _acquire = acquire ?? (_ => Task.FromResult<IAsyncDisposable?>(new FakeLock()));

        public Task<IAsyncDisposable?> TryAcquireAsync(string loaderId, CancellationToken ct) => _acquire(loaderId);

        public static FakeOverlapGuard AlwaysSkips() => new(_ => Task.FromResult<IAsyncDisposable?>(null));
    }

    private sealed class FakeAdditionalProcesses : IAdditionalProcessRunner
    {
        public readonly ConcurrentBag<(string LoaderId, Guid RunId)> Calls = new();
        private readonly bool _throw;

        public FakeAdditionalProcesses(bool shouldThrow = false) => _throw = shouldThrow;

        public Task RunAsync(string loaderId, Guid runId, CancellationToken ct)
        {
            Calls.Add((loaderId, runId));
            // The real SqlAdditionalProcessRunner never throws — it swallows. This
            // option exists to prove the host does not depend on that politeness.
            return _throw
                ? Task.FromException(new InvalidOperationException("hook blew up"))
                : Task.CompletedTask;
        }
    }

    private static PlatformHost Build(
        IEnumerable<ILoaderModule> modules,
        FakeRunLog runLog,
        FakeAdditionalProcesses hook,
        ILoaderOverlapGuard? guard = null,
        int maxConcurrent = 1)
        => new(
            new ServiceCollection().BuildServiceProvider(),
            modules.ToList(),
            runLog,
            guard ?? new FakeOverlapGuard(),
            hook,
            Options.Create(new PlatformSettings { MaxConcurrentLoaders = maxConcurrent }),
            NullLogger<PlatformHost>.Instance);

    // ------------------------------------------------- hook: when it DOES fire

    [Fact]
    public async Task Hook_Fires_AfterSuccessfulLoader_WithLoaderNameAndRunId()
    {
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, runLog, hook).RunAsync(default);

        Assert.Equal(0, exit);
        var call = Assert.Single(hook.Calls);
        Assert.Equal("Platts", call.LoaderId);
        Assert.NotEqual(Guid.Empty, call.RunId);
    }

    [Fact]
    public async Task Hook_Fires_AfterFailedLoader()
    {
        // Partial data still landed, so post-processing is still wanted.
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();

        var exit = await Build(new[] { FakeModule.Failing("Platts") }, runLog, hook).RunAsync(default);

        Assert.Equal(1, exit);
        Assert.Equal("Platts", Assert.Single(hook.Calls).LoaderId);
    }

    [Fact]
    public async Task Hook_Fires_AfterLoaderThrowsOutOfRunAsync()
    {
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();

        var exit = await Build(new[] { FakeModule.Throwing("Platts") }, runLog, hook).RunAsync(default);

        Assert.Equal(1, exit);
        Assert.Equal("Platts", Assert.Single(hook.Calls).LoaderId);
    }

    [Fact]
    public async Task Hook_FiresOncePerLoader_AllSharingTheSameRunId()
    {
        // @RunId is per host INVOCATION, not per loader. The pair with @LoaderName is
        // what identifies a loader's work — and is the grain of core.LoadLog.
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();
        var modules = new[]
        {
            FakeModule.Succeeding("Platts"),
            FakeModule.Succeeding("Vulcan"),
            FakeModule.Succeeding("StormVista")
        };

        await Build(modules, runLog, hook, maxConcurrent: 3).RunAsync(default);

        Assert.Equal(3, hook.Calls.Count);
        Assert.Equal(
            new[] { "Platts", "StormVista", "Vulcan" },
            hook.Calls.Select(c => c.LoaderId).OrderBy(x => x).ToArray());
        Assert.Single(hook.Calls.Select(c => c.RunId).Distinct());
    }

    // --------------------------------------------- hook: when it must NOT fire

    [Fact]
    public async Task Hook_DoesNotFire_WhenOverlapGuardSkipsTheLoader()
    {
        // Another process holds the lock and will run the hook itself; firing here
        // would race it into concurrent post-processing.
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();

        var exit = await Build(
                new[] { FakeModule.Succeeding("Platts") }, runLog, hook,
                guard: FakeOverlapGuard.AlwaysSkips())
            .RunAsync(default);

        Assert.Equal(0, exit);          // an overlap skip is not a failure
        Assert.Empty(hook.Calls);
    }

    [Fact]
    public async Task Hook_DoesNotFire_WhenRunIsCancelledDuringTheLoader()
    {
        // The operator asked to stop. The hook is new work, not cleanup.
        // This needs an explicit check in the host: the catch(Exception) around
        // module.RunAsync swallows OperationCanceledException too, so a cancelled
        // module arrives at the hook looking merely failed.
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();
        using var cts = new CancellationTokenSource();

        var module = new FakeModule("Platts", _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Build(new[] { module }, runLog, hook).RunAsync(cts.Token);

        Assert.Empty(hook.Calls);
    }

    [Fact]
    public async Task Hook_ThrowingDoesNotChangeTheRunOutcome()
    {
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses(shouldThrow: true);

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, runLog, hook).RunAsync(default);

        Assert.Equal(0, exit);
        Assert.Equal(1, runLog.CompleteRunCalls);
        Assert.True(runLog.CompletedSuccess);
    }

    // --------------------------------------- core.LoaderRun is always closed

    [Fact]
    public async Task CompleteRun_IsCalled_OnTheHappyPath()
    {
        var runLog = new FakeRunLog();

        await Build(new[] { FakeModule.Succeeding("Platts") }, runLog, new FakeAdditionalProcesses())
            .RunAsync(default);

        Assert.Equal(1, runLog.BeginRunCalls);
        Assert.Equal(1, runLog.CompleteRunCalls);
        Assert.True(runLog.CompletedSuccess);
    }

    [Fact]
    public async Task CompleteRun_IsCalled_EvenWhenTheRunIsCancelled()
    {
        // THE REGRESSION THIS FIXES. Before the try/finally, an OperationCanceledException
        // out of ParallelRunner propagated straight past CompleteRunAsync and stranded the
        // core.LoaderRun row with CompletedAtUtc NULL forever — on a graceful Ctrl+C that
        // never crashed the process.
        var runLog = new FakeRunLog();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var host = Build(new[] { FakeModule.Succeeding("Platts") }, runLog, new FakeAdditionalProcesses());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(cts.Token));

        Assert.Equal(1, runLog.CompleteRunCalls);
        Assert.False(runLog.CompletedSuccess);   // a cancelled run is not a success
    }

    [Fact]
    public async Task LoaderThatCannotEvenAcquireItsLock_StillAppearsInTheResults()
    {
        // ParallelRunner swallows non-cancellation exceptions, so a throw from
        // TryAcquireAsync used to skip results.Add entirely and leave `results` EMPTY.
        // All(...) over an empty list is TRUE, so the run reported success with exit 0
        // while the loader had never run at all.
        var runLog = new FakeRunLog();
        var guard = new FakeOverlapGuard(_ => throw new InvalidOperationException("lock server down"));

        var exit = await Build(
                new[] { FakeModule.Succeeding("Platts") }, runLog, new FakeAdditionalProcesses(), guard)
            .RunAsync(default);

        Assert.Equal(1, exit);
        Assert.Equal(1, runLog.CompleteRunCalls);
        Assert.False(runLog.CompletedSuccess);
    }
}
