using System.Collections.Concurrent;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Notifications;
using DataLoader.Core.Persistence;
using DataLoader.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <c>Platform:RunDeadlineMinutes</c>. On 2026-11-21 the IHS PointLogic API stopped
/// answering and Task Scheduler killed two IHSPointLogic runs at the task's 1-hour limit.
/// A killed process closes nothing: <c>core.LoaderRun</c> stayed open, no failure email
/// went out, and the watchdog could only report a nameless STUCK run. The deadline makes
/// the host stop the loader ITSELF, so the run ends as an ordinary closed failure.
/// </summary>
public class RunDeadlineTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeModule : ILoaderModule
    {
        private readonly Func<LoaderRunContext, Task<LoaderRunResult>> _run;
        public LoaderRunContext? Seen;

        public FakeModule(string id, Func<LoaderRunContext, Task<LoaderRunResult>> run)
        {
            LoaderId = id;
            _run = run;
        }

        public string LoaderId { get; }
        public string DisplayName => LoaderId;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
        {
            Seen = context;
            return _run(context);
        }

        /// <summary>A vendor that never answers: waits until cancelled.</summary>
        public static FakeModule Hanging(string id) => new(id, async ctx =>
        {
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return new LoaderRunResult { LoaderId = id, Success = true };
        });

        public static FakeModule Succeeding(string id) => new(id, _ =>
            Task.FromResult(new LoaderRunResult { LoaderId = id, Success = true, WorkUnitsTotal = 3, WorkUnitsSucceeded = 3 }));
    }

    private sealed class FakeRunLog : ILoadLogRepository
    {
        public int CompleteRunCalls;
        public bool? CompletedSuccess;

        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;

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
        public Task<IAsyncDisposable?> TryAcquireAsync(string loaderId, CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(new FakeLock());
    }

    private sealed class FakeAdditionalProcesses : IAdditionalProcessRunner
    {
        public readonly ConcurrentBag<string> Calls = new();
        public Task RunAsync(string loaderId, Guid runId, CancellationToken ct)
        {
            Calls.Add(loaderId);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingNotifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();
        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.FromResult(NotificationOutcome.Sent);
        }
    }

    private sealed record Harness(
        PlatformHost Host, FakeRunLog RunLog, FakeAdditionalProcesses Hook, CapturingNotifier Notifier);

    private static Harness Build(ILoaderModule module, Dictionary<string, double>? deadlines = null)
    {
        var runLog = new FakeRunLog();
        var hook = new FakeAdditionalProcesses();
        var notifier = new CapturingNotifier();
        var settings = new PlatformSettings { MaxConcurrentLoaders = 1, AbandonStaleLoadLogs = false };
        foreach (var (k, v) in deadlines ?? new()) settings.RunDeadlineMinutes[k] = v;

        var host = new PlatformHost(
            new ServiceCollection().BuildServiceProvider(),
            new[] { module },
            runLog,
            new FakeOverlapGuard(),
            hook,
            Options.Create(settings),
            NullLogger<PlatformHost>.Instance,
            loadLogSweeper: null,
            notifier: notifier);
        return new Harness(host, runLog, hook, notifier);
    }

    /// <summary>About 60 ms — long enough to be observably "later", short enough for a unit test.</summary>
    private const double ShortDeadlineMinutes = 0.001;

    // ------------------------------------------- acceptance 1: a hung loader ends itself

    [Fact]
    public async Task HungLoader_IsStoppedAtItsDeadline_AndTheRunClosesAsAFailure()
    {
        var h = Build(FakeModule.Hanging("IHSPointLogic"),
            new() { ["IHSPointLogic"] = ShortDeadlineMinutes });

        var run = h.Host.RunAsync(CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(run, finished);                         // it ended by itself
        Assert.Equal(1, await run);                         // exit 1, an ordinary failure
        Assert.Equal(1, h.RunLog.CompleteRunCalls);         // core.LoaderRun closed …
        Assert.False(h.RunLog.CompletedSuccess);            // … with Success = 0
    }

    [Fact]
    public async Task HungLoader_FailureReportNamesTheDeadline_AndIsSent()
    {
        var h = Build(FakeModule.Hanging("IHSPointLogic"),
            new() { ["IHSPointLogic"] = ShortDeadlineMinutes });

        await h.Host.RunAsync(CancellationToken.None);

        var report = Assert.IsType<RunFailureReport>(Assert.Single(h.Notifier.Payloads));
        var result = Assert.Single(report.Results);
        Assert.False(result.Success);
        Assert.Contains("Run deadline", result.ErrorMessage);
        Assert.Contains("Platform:RunDeadlineMinutes:IHSPointLogic", result.ErrorMessage);
        Assert.Contains("not attempted", result.ErrorMessage);
    }

    [Fact]
    public async Task HungLoader_PostRunHookStillRuns_BecauseADeadlineIsNotAnOperatorCancel()
    {
        // Partial data landed; post-processing is wanted exactly as after any failure.
        var h = Build(FakeModule.Hanging("IHSPointLogic"),
            new() { ["IHSPointLogic"] = ShortDeadlineMinutes });

        await h.Host.RunAsync(CancellationToken.None);

        Assert.Equal("IHSPointLogic", Assert.Single(h.Hook.Calls));
    }

    [Fact]
    public async Task LoaderThatSwallowsTheCancellation_AndReturnsSuccess_IsStillAFailure()
    {
        // Its remaining units were not attempted; reporting success would hide that.
        var module = new FakeModule("X", async ctx =>
        {
            try { await Task.Delay(Timeout.Infinite, ctx.CancellationToken); }
            catch (OperationCanceledException) { }
            return new LoaderRunResult { LoaderId = "X", Success = true, WorkUnitsTotal = 5, WorkUnitsSucceeded = 2 };
        });
        var h = Build(module, new() { ["X"] = ShortDeadlineMinutes });

        Assert.Equal(1, await h.Host.RunAsync(CancellationToken.None));

        var result = Assert.Single(Assert.IsType<RunFailureReport>(Assert.Single(h.Notifier.Payloads)).Results);
        Assert.False(result.Success);
        Assert.Contains("Run deadline", result.ErrorMessage);
        Assert.Equal(2, result.WorkUnitsSucceeded);         // what it did report is kept
    }

    [Fact]
    public async Task DeadlineTokenIsExposed_SoAPipelineCanSayWhyItStopped()
    {
        var module = FakeModule.Hanging("X");
        var h = Build(module, new() { ["X"] = ShortDeadlineMinutes });

        await h.Host.RunAsync(CancellationToken.None);

        Assert.True(module.Seen!.RunDeadlineToken.CanBeCanceled);
        Assert.True(module.Seen.RunDeadlineToken.IsCancellationRequested);
        Assert.True(module.Seen.CancellationToken.IsCancellationRequested);
    }

    // ---------------------------------- acceptance 2 and 3: nothing changes otherwise

    [Fact]
    public async Task LoaderFinishingBeforeItsDeadline_IsUnchanged()
    {
        var module = FakeModule.Succeeding("IHSPointLogic");
        var h = Build(module, new() { ["IHSPointLogic"] = 50 });

        Assert.Equal(0, await h.Host.RunAsync(CancellationToken.None));
        Assert.True(h.RunLog.CompletedSuccess);
        Assert.False(module.Seen!.RunDeadlineToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(null)]   // no entry at all
    [InlineData(0.0)]    // explicitly off
    [InlineData(-5.0)]   // nonsense is off, not "already expired"
    public async Task NoDeadline_MeansNoDeadlineToken_AndNoCancellation(double? minutes)
    {
        var module = new FakeModule("X", async ctx =>
        {
            await Task.Delay(100, ctx.CancellationToken);
            return new LoaderRunResult { LoaderId = "X", Success = true };
        });
        var deadlines = minutes is null ? null : new Dictionary<string, double> { ["X"] = minutes.Value };
        var h = Build(module, deadlines);

        Assert.Equal(0, await h.Host.RunAsync(CancellationToken.None));
        Assert.False(module.Seen!.RunDeadlineToken.CanBeCanceled);
        Assert.DoesNotContain(h.Notifier.Payloads.OfType<RunFailureReport>().SelectMany(r => r.Results), r => !r.Success);
    }

    [Fact]
    public async Task DeadlineForAnotherLoader_DoesNotApply()
    {
        var module = FakeModule.Succeeding("Platts");
        var h = Build(module, new() { ["IHSPointLogic"] = ShortDeadlineMinutes });

        Assert.Equal(0, await h.Host.RunAsync(CancellationToken.None));
        Assert.False(module.Seen!.RunDeadlineToken.CanBeCanceled);
    }

    [Fact]
    public void RunDeadlineFor_IsCaseInsensitive_AndZeroOrNegativeIsOff()
    {
        var s = new PlatformSettings();
        s.RunDeadlineMinutes["IHSPointLogic"] = 50;
        s.RunDeadlineMinutes["Off"] = 0;

        Assert.Equal(TimeSpan.FromMinutes(50), s.RunDeadlineFor("ihspointlogic"));
        Assert.Null(s.RunDeadlineFor("Off"));
        Assert.Null(s.RunDeadlineFor("Missing"));
    }

    [Fact]
    public void RunDeadlineMinutes_BindsFromConfiguration_AsShipped()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RunDeadlineMinutes:IHSPointLogic"] = "50" })
            .Build();
        var s = new PlatformSettings();
        config.GetSection(PlatformSettings.SectionName).Bind(s);

        Assert.Equal(TimeSpan.FromMinutes(50), s.RunDeadlineFor("IHSPointLogic"));
    }

    [Fact]
    public void ShippedAppSettings_GivesIHSPointLogicADeadlineUnderItsOneHourTaskLimit()
    {
        // The task \DLP\IHS PointLogic has a trigger ExecutionTimeLimit of PT1H and runs
        // Mako.dlp.merge_ihs_pointlogic as a SECOND action inside it, so the deadline must
        // leave room; the slowest successful run in the 90 days to 2026-11-23 took 36 min.
        var path = Path.Combine(RepoRoot(), "src", "DataLoader.Host", "appsettings.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var minutes = doc.RootElement.GetProperty("Platform").GetProperty("RunDeadlineMinutes")
            .GetProperty("IHSPointLogic").GetDouble();

        Assert.InRange(minutes, 40, 55);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DataLoaderPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ------------------------------------------------ operator cancellation is not a deadline

    [Fact]
    public async Task OperatorCancellation_IsNotReportedAsADeadline()
    {
        using var operatorCts = new CancellationTokenSource();
        var module = new FakeModule("X", async ctx =>
        {
            operatorCts.Cancel();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return new LoaderRunResult { LoaderId = "X", Success = true };
        });
        var h = Build(module, new() { ["X"] = 50 });

        try { await h.Host.RunAsync(operatorCts.Token); } catch (OperationCanceledException) { }

        Assert.False(module.Seen!.RunDeadlineToken.IsCancellationRequested);
        Assert.Empty(h.Hook.Calls);                         // unchanged: no hook after Ctrl+C
        Assert.False(h.RunLog.CompletedSuccess);            // and the run row still closes
    }

    // ------------------------------------------------ the pipeline labels its in-flight unit

    private sealed class Unit : WorkUnit
    {
        public override string Key => "unit=1";
    }

    private sealed class Settings : LoaderSettingsBase { }

    private sealed class CapturingLoadLog : ILoadLogRepository
    {
        public string? FailureMessage;
        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task<LoadLogHandle?> BeginAsync(string l, Guid r, string k, string d, CancellationToken ct) =>
            Task.FromResult<LoadLogHandle?>(new LoadLogHandle { LoadLogId = 1, LoaderId = l, WorkUnitKey = k, StartedAtUtc = DateTime.UtcNow });
        public Task CompleteSuccessAsync(LoadLogHandle h, int n, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteFailureAsync(LoadLogHandle h, string m, CancellationToken ct)
        {
            FailureMessage = m;
            return Task.CompletedTask;
        }
    }

    private sealed class Provider : IWorkUnitProvider<Unit>
    {
        public Task<IReadOnlyList<Unit>> GetWorkUnitsAsync(LoaderRunContext c) =>
            Task.FromResult<IReadOnlyList<Unit>>(new[] { new Unit() });
    }

    private sealed class HangingSource : ISourceReader<Unit, string>
    {
        /// <summary>Set once the unit is in flight, so the test cancels a unit that HAS a load-log row.</summary>
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<string>> ReadAsync(Unit unit, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<string>();
        }
    }

    private sealed class PassThrough : ITransformer<string, string>
    {
        public IReadOnlyList<string> Transform(IReadOnlyList<string> items, WorkUnit unit) => items;
    }

    private sealed class Sink : ISink<string>
    {
        public Task<int> WriteAsync(IReadOnlyList<string> rows, CancellationToken ct) => Task.FromResult(rows.Count);
    }

    private static async Task<string?> InFlightUnitMessage(bool deadline)
    {
        var log = new CapturingLoadLog();
        var source = new HangingSource();
        var pipeline = new LoaderPipelineBase<Unit, string, string>(
            "TestLoader", new Provider(), source, new PassThrough(), new Sink(), log,
            new Settings { WorkUnitTimeoutSeconds = 300, MaxConcurrentWorkUnits = 1 },
            NullLogger.Instance);

        using var deadlineCts = new CancellationTokenSource();
        using var operatorCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operatorCts.Token, deadlineCts.Token);

        var run = pipeline.ExecuteAsync(new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            CancellationToken = linked.Token,
            RunDeadlineToken = deadlineCts.Token
        });
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        (deadline ? deadlineCts : operatorCts).Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return log.FailureMessage;
    }

    [Fact]
    public async Task InFlightUnit_StoppedByTheDeadline_SaysSoInItsLoadLogRow()
    {
        var message = await InFlightUnitMessage(deadline: true);
        Assert.Contains("Run deadline reached", message);
        Assert.Contains("Platform:RunDeadlineMinutes", message);
    }

    [Fact]
    public async Task InFlightUnit_StoppedByTheOperator_StillSaysRunCancelled()
    {
        Assert.Equal("Run cancelled", await InFlightUnitMessage(deadline: false));
    }
}
