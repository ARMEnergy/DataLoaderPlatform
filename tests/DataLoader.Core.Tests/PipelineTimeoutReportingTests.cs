using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using DataLoader.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// An HTTP request timing out and a work unit exhausting its budget are very
/// different problems with different fixes, and both surface as
/// <see cref="OperationCanceledException"/>. Reporting them with the same words
/// once sent an investigation to the wrong setting — the unit had died at
/// <c>HttpTimeoutSeconds</c> (60s) while the message said "Work unit timed out",
/// pointing at <c>WorkUnitTimeoutSeconds</c> (300s). Pin the distinction.
/// </summary>
public class PipelineTimeoutReportingTests
{
    private sealed class Unit : WorkUnit
    {
        public override string Key => "unit=1";
    }

    private sealed class Settings : LoaderSettingsBase { }

    /// <summary>Captures the failure message the pipeline records.</summary>
    private sealed class CapturingLoadLog : ILoadLogRepository
    {
        public string? FailureMessage;
        public int SuccessCount;

        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct) => Task.CompletedTask;

        public Task<LoadLogHandle?> BeginAsync(string l, Guid r, string k, string d, CancellationToken ct) =>
            Task.FromResult<LoadLogHandle?>(new LoadLogHandle
            {
                LoadLogId = 1, LoaderId = l, WorkUnitKey = k, StartedAtUtc = DateTime.UtcNow
            });

        public Task CompleteSuccessAsync(LoadLogHandle h, int n, CancellationToken ct)
        {
            SuccessCount++;
            return Task.CompletedTask;
        }

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

    private sealed class Source : ISourceReader<Unit, string>
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _read;
        public Source(Func<CancellationToken, Task<IReadOnlyList<string>>> read) => _read = read;
        public Task<IReadOnlyList<string>> ReadAsync(Unit unit, CancellationToken ct) => _read(ct);
    }

    private sealed class PassThrough : ITransformer<string, string>
    {
        public IReadOnlyList<string> Transform(IReadOnlyList<string> items, WorkUnit unit) => items;
    }

    private sealed class Sink : ISink<string>
    {
        public Task<int> WriteAsync(IReadOnlyList<string> rows, CancellationToken ct) =>
            Task.FromResult(rows.Count);
    }

    private static async Task<CapturingLoadLog> RunWith(
        Func<CancellationToken, Task<IReadOnlyList<string>>> read, int workUnitTimeoutSeconds)
    {
        var log = new CapturingLoadLog();
        var pipeline = new LoaderPipelineBase<Unit, string, string>(
            "TestLoader", new Provider(), new Source(read), new PassThrough(), new Sink(), log,
            new Settings { WorkUnitTimeoutSeconds = workUnitTimeoutSeconds, MaxConcurrentWorkUnits = 1 },
            NullLogger.Instance);

        await pipeline.ExecuteAsync(new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            CancellationToken = CancellationToken.None
        });
        return log;
    }

    [Fact]
    public async Task HttpRequestTimeout_IsReportedAsAnHttpTimeout()
    {
        // HttpClient.Timeout expiring: TaskCanceledException with a
        // TimeoutException inside, and the unit's own budget untouched.
        var log = await RunWith(
            _ => throw new TaskCanceledException("timeout", new TimeoutException()),
            workUnitTimeoutSeconds: 300);

        Assert.Contains("HTTP request timed out", log.FailureMessage);
        Assert.Contains("HttpTimeoutSeconds", log.FailureMessage);
        Assert.DoesNotContain("WorkUnitTimeoutSeconds", log.FailureMessage);
    }

    [Fact]
    public async Task WorkUnitBudgetExpiring_IsReportedAsTheWorkUnitTimeout()
    {
        // The unit's own CTS fires while the source is still working.
        var log = await RunWith(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Array.Empty<string>(); },
            workUnitTimeoutSeconds: 1);

        Assert.Contains("Work unit timed out", log.FailureMessage);
        Assert.Contains("WorkUnitTimeoutSeconds", log.FailureMessage);
        Assert.Contains("1s", log.FailureMessage);
    }

    [Fact]
    public async Task TheTwoMessagesAreDistinguishable()
    {
        var http = await RunWith(
            _ => throw new TaskCanceledException("timeout", new TimeoutException()),
            workUnitTimeoutSeconds: 300);
        var budget = await RunWith(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Array.Empty<string>(); },
            workUnitTimeoutSeconds: 1);

        Assert.NotEqual(http.FailureMessage, budget.FailureMessage);
    }

    [Fact]
    public async Task SuccessfulUnit_IsNotAffected()
    {
        var log = await RunWith(
            _ => Task.FromResult<IReadOnlyList<string>>(new[] { "a", "b" }),
            workUnitTimeoutSeconds: 300);

        Assert.Null(log.FailureMessage);
        Assert.Equal(1, log.SuccessCount);
    }
}
