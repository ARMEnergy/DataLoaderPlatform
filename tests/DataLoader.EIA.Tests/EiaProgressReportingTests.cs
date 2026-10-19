using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// Run visibility.
///
/// <para>
/// ⚠ <b>The defect these exist for.</b> The first version of this loader logged every
/// HTTP retry at <c>Warning</c> and every unit SUCCESS at <c>Debug</c>. A 17-hour
/// backfill that was 89% complete, had loaded 45.6M rows and was succeeding 99% of the
/// time therefore produced a console containing nothing but thousands of 504 warnings —
/// and was reasonably reported as "not working at all". Retries are routine against this
/// vendor; progress is what deserves to be said out loud. These tests pin the levels so
/// the two cannot be swapped back.
/// </para>
/// </summary>
public sealed class EiaProgressReportingTests
{
    /// <summary>Captures what was logged, at which level.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((level, formatter(state, ex)));
        }

        public IEnumerable<string> At(LogLevel level) =>
            Entries.Where(e => e.Level == level).Select(e => e.Message);
    }

    // ------------------------------------------------- the retry level

    /// <summary>
    /// A retry that is about to be re-attempted is NOT a warning. This asserts against
    /// the real policy: drive it to exhaustion and confirm nothing lands at Warning or
    /// above.
    /// </summary>
    [Fact]
    public async Task A_retry_does_not_log_at_warning()
    {
        var logger = new CapturingLogger();
        var policy = EiaHttp.BuildResiliencePipeline(
            retryCount: 3, retryDelayMs: 1, perAttemptTimeoutSeconds: 5, logger);

        await policy.ExecuteAsync(ct =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)), CancellationToken.None);

        Assert.NotEmpty(logger.Entries);                       // it did log something
        Assert.Empty(logger.At(LogLevel.Warning));
        Assert.Empty(logger.At(LogLevel.Error));
        Assert.Equal(3, logger.At(LogLevel.Debug).Count());    // one per retry
    }

    [Fact]
    public async Task A_retry_still_records_the_status_for_diagnosis()
    {
        var logger = new CapturingLogger();
        var policy = EiaHttp.BuildResiliencePipeline(
            retryCount: 1, retryDelayMs: 1, perAttemptTimeoutSeconds: 5, logger);

        await policy.ExecuteAsync(ct =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout)), CancellationToken.None);

        Assert.Contains(logger.At(LogLevel.Debug), m => m.Contains("504", StringComparison.Ordinal));
    }

    /// <summary>
    /// A per-attempt timeout must say so in words an operator can act on, not leak
    /// Polly's "The delegate executed asynchronously through TimeoutPolicy did not
    /// complete within the timeout" — which named neither the unit, the budget, nor the
    /// setting, and which reached <c>core.LoadLog</c> 17 times looking like an internal
    /// fault.
    /// </summary>
    [Fact]
    public async Task A_per_attempt_timeout_is_reported_in_actionable_terms()
    {
        var logger = new CapturingLogger();
        var policy = EiaHttp.BuildResiliencePipeline(
            retryCount: 1, retryDelayMs: 1, perAttemptTimeoutSeconds: 1, logger);

        await Assert.ThrowsAsync<Polly.Timeout.TimeoutRejectedException>(() =>
            policy.ExecuteAsync(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }, CancellationToken.None));

        // The policy's own line names the budget rather than the Polly internals.
        Assert.Contains(logger.At(LogLevel.Debug),
            m => m.Contains("per-attempt budget", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries,
            e => e.Message.Contains("delegate executed asynchronously", StringComparison.Ordinal));
    }

    // 🔒 The retry line must never carry a URI, because every URI holds the api_key.
    [Fact]
    public async Task A_retry_line_never_contains_a_url_or_key()
    {
        var logger = new CapturingLogger();
        var policy = EiaHttp.BuildResiliencePipeline(
            retryCount: 2, retryDelayMs: 1, perAttemptTimeoutSeconds: 5, logger);

        await policy.ExecuteAsync(ct =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)), CancellationToken.None);

        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("api_key", e.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", e.Message, StringComparison.OrdinalIgnoreCase);
        });
    }
}
