using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <see cref="LoaderPipelineBase{TUnit,TItem,TRow}"/> is used by every loader
/// on the platform, so these tests pin the contract for all three places it
/// records a work-unit failure: the load-log-open failure, the per-unit
/// timeout, and the general catch. <c>core.LoadLog</c> stores only the
/// exception MESSAGE — the stack is gone once the run ends — so this capture
/// has to happen in-process, at the moment of failure, or it is lost for good.
/// </summary>
public class FailedWorkUnitCaptureTests
{
    private sealed class TestUnit : WorkUnit
    {
        public required string UnitKey { get; init; }
        public override string Key => UnitKey;
        public override string DisplayName => $"Display({UnitKey})";
    }

    private sealed class Settings : LoaderSettingsBase { }

    /// <summary>A load log whose BeginAsync opens normally and never fails to close.</summary>
    private sealed class PassingLoadLog : ILoadLogRepository
    {
        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct) => Task.CompletedTask;

        public Task<LoadLogHandle?> BeginAsync(string l, Guid r, string k, string d, CancellationToken ct) =>
            Task.FromResult<LoadLogHandle?>(new LoadLogHandle
            {
                LoadLogId = 1, LoaderId = l, WorkUnitKey = k, StartedAtUtc = DateTime.UtcNow
            });

        public Task CompleteSuccessAsync(LoadLogHandle h, int n, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteFailureAsync(LoadLogHandle h, string m, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A load log whose BeginAsync always throws — simulates the database being unreachable.</summary>
    private sealed class ThrowingOnBeginLoadLog : ILoadLogRepository
    {
        private readonly Exception _ex;
        public ThrowingOnBeginLoadLog(Exception ex) => _ex = ex;

        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task<LoadLogHandle?> BeginAsync(string l, Guid r, string k, string d, CancellationToken ct) =>
            throw _ex;
        public Task CompleteSuccessAsync(LoadLogHandle h, int n, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteFailureAsync(LoadLogHandle h, string m, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StaticProvider : IWorkUnitProvider<TestUnit>
    {
        private readonly IReadOnlyList<TestUnit> _units;
        public StaticProvider(IReadOnlyList<TestUnit> units) => _units = units;
        public Task<IReadOnlyList<TestUnit>> GetWorkUnitsAsync(LoaderRunContext c) => Task.FromResult(_units);
    }

    private sealed class FuncSource : ISourceReader<TestUnit, string>
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _read;
        public FuncSource(Func<CancellationToken, Task<IReadOnlyList<string>>> read) => _read = read;
        public Task<IReadOnlyList<string>> ReadAsync(TestUnit unit, CancellationToken ct) => _read(ct);
    }

    /// <summary>
    /// Throws for every unit, but only once a shared turnstile reaches that
    /// unit's own index — so units started together under real concurrency
    /// still FAIL in a fixed, known order, letting a test prove the capture
    /// queue preserves that order rather than reshuffling it.
    /// </summary>
    private sealed class OrderedFailureSource : ISourceReader<TestUnit, string>
    {
        private int _nextToFail;

        public async Task<IReadOnlyList<string>> ReadAsync(TestUnit unit, CancellationToken ct)
        {
            var index = int.Parse(unit.UnitKey.Substring("unit-".Length));
            while (Volatile.Read(ref _nextToFail) != index)
                await Task.Delay(5, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _nextToFail);
            throw new InvalidOperationException($"boom-{index}");
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

    private static async Task<LoaderRunResult> RunAsync(
        IReadOnlyList<TestUnit> units,
        ISourceReader<TestUnit, string> source,
        ILoadLogRepository loadLog,
        LoaderSettingsBase settings)
    {
        var pipeline = new LoaderPipelineBase<TestUnit, string, string>(
            "TestLoader", new StaticProvider(units), source, new PassThrough(), new Sink(), loadLog,
            settings, NullLogger.Instance);

        return await pipeline.ExecuteAsync(new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            CancellationToken = CancellationToken.None
        });
    }

    private static TestUnit[] Units(int count) =>
        Enumerable.Range(0, count).Select(i => new TestUnit { UnitKey = $"unit-{i:D2}" }).ToArray();

    // ----------------------------------------------------------- three paths

    [Fact]
    public async Task LoadLogOpenFailure_IsCapturedAsAFailedWorkUnit()
    {
        var unit = new TestUnit { UnitKey = "k1" };
        var ex = new InvalidOperationException("db unreachable");

        var result = await RunAsync(
            new[] { unit },
            new FuncSource(_ => throw new Exception("source must not be called on this path")),
            new ThrowingOnBeginLoadLog(ex),
            new Settings { MaxConcurrentWorkUnits = 1 });

        var failed = Assert.Single(result.FailedUnits);
        Assert.Equal("k1", failed.Key);
        Assert.Equal("Display(k1)", failed.DisplayName);
        Assert.Equal("db unreachable", failed.ErrorText);
        Assert.Contains("InvalidOperationException", failed.StackTrace);
        Assert.Equal(1, result.WorkUnitsFailed);
    }

    [Fact]
    public async Task WorkUnitTimeout_CapturesTheComposedReasonAndTheStack()
    {
        var unit = new TestUnit { UnitKey = "k2" };

        var result = await RunAsync(
            new[] { unit },
            new FuncSource(async ct => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return Array.Empty<string>(); }),
            new PassingLoadLog(),
            new Settings { MaxConcurrentWorkUnits = 1, WorkUnitTimeoutSeconds = 1 });

        var failed = Assert.Single(result.FailedUnits);
        Assert.Equal("k2", failed.Key);
        // The composed reason, not the bare exception message — it names
        // WHICH budget was exceeded, which the raw exception cannot.
        Assert.Contains("Work unit timed out", failed.ErrorText);
        Assert.Contains("WorkUnitTimeoutSeconds", failed.ErrorText);
        Assert.NotNull(failed.StackTrace);
        Assert.Contains("Canceled", failed.StackTrace);
    }

    [Fact]
    public async Task GeneralException_CapturesTheMessageAndTheStack()
    {
        var unit = new TestUnit { UnitKey = "k3" };

        var result = await RunAsync(
            new[] { unit },
            new FuncSource(_ => throw new InvalidOperationException("boom")),
            new PassingLoadLog(),
            new Settings { MaxConcurrentWorkUnits = 1 });

        var failed = Assert.Single(result.FailedUnits);
        Assert.Equal("k3", failed.Key);
        Assert.Equal("boom", failed.ErrorText);
        Assert.Contains("InvalidOperationException", failed.StackTrace);
        Assert.Contains("boom", failed.StackTrace);
    }

    [Fact]
    public async Task WholeRunCancellation_DoesNotIncrementFailedOrCaptureAnything()
    {
        // The whole-run OperationCanceledException path must stay untouched:
        // it bubbles up rather than being recorded as a per-unit failure.
        using var cts = new CancellationTokenSource();
        var unit = new TestUnit { UnitKey = "k4" };

        var pipeline = new LoaderPipelineBase<TestUnit, string, string>(
            "TestLoader", new StaticProvider(new[] { unit }),
            new FuncSource(async ct => { cts.Cancel(); await Task.Delay(TimeSpan.FromSeconds(30), ct); return Array.Empty<string>(); }),
            new PassThrough(), new Sink(), new PassingLoadLog(),
            new Settings { MaxConcurrentWorkUnits = 1, WorkUnitTimeoutSeconds = 0 }, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            CancellationToken = cts.Token
        }));
    }

    // ------------------------------------------------------------ retention cap

    [Fact]
    public async Task RetentionCap_HoldsAt25_WhileWorkUnitsFailedReportsTheTrueTotal()
    {
        var units = Units(30);

        var result = await RunAsync(
            units,
            new FuncSource(_ => throw new InvalidOperationException("boom")),
            new PassingLoadLog(),
            new Settings { MaxConcurrentWorkUnits = 1 });

        Assert.Equal(30, result.WorkUnitsFailed);
        Assert.Equal(25, result.FailedUnits.Count);
        for (var i = 0; i < 25; i++)
            Assert.Equal($"unit-{i:D2}", result.FailedUnits[i].Key);
    }

    // ------------------------------------------------------- order under concurrency

    [Fact]
    public async Task FailureOrder_IsDeterministic_UnderRealConcurrency()
    {
        const int n = 12;
        var units = Units(n);

        var result = await RunAsync(
            units,
            new OrderedFailureSource(),
            new PassingLoadLog(),
            new Settings { MaxConcurrentWorkUnits = 8 });

        Assert.Equal(n, result.WorkUnitsFailed);
        Assert.Equal(n, result.FailedUnits.Count);
        for (var i = 0; i < n; i++)
            Assert.Equal($"unit-{i:D2}", result.FailedUnits[i].Key);
    }

    // --------------------------------------------------------------- success

    [Fact]
    public async Task SuccessfulRun_HasAnEmptyFailedUnitsList()
    {
        var unit = new TestUnit { UnitKey = "ok1" };

        var result = await RunAsync(
            new[] { unit },
            new FuncSource(_ => Task.FromResult<IReadOnlyList<string>>(new[] { "a" })),
            new PassingLoadLog(),
            new Settings { MaxConcurrentWorkUnits = 1 });

        Assert.True(result.Success);
        Assert.Empty(result.FailedUnits);
    }
}
