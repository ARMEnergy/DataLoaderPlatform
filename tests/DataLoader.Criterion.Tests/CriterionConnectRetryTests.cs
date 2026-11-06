using System.Text.Json;
using Npgsql;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// The source connect retry added after five mornings of 11:00 UTC connect timeouts
/// killed the whole <c>FinancialSeriesData</c> feed at work-unit enumeration. Driven
/// through <see cref="CriterionConnectRetry.OpenWithRetryAsync{T}"/> with a fake open and
/// a recording delay, so no database or real waiting is involved.
/// </summary>
public sealed class CriterionConnectRetryTests
{
    private sealed class Recorder
    {
        public List<TimeSpan> Waits { get; } = new();
        public int Calls { get; set; }

        public Task Delay(TimeSpan wait, CancellationToken _)
        {
            Waits.Add(wait);
            return Task.CompletedTask;
        }
    }

    private static Task<string> Run(
        Recorder rec, Func<int, string> attempt, int maxAttempts = 3,
        CancellationToken cancellationToken = default) =>
        CriterionConnectRetry.OpenWithRetryAsync(
            _ =>
            {
                rec.Calls++;
                return Task.FromResult(attempt(rec.Calls));
            },
            maxAttempts,
            TimeSpan.FromSeconds(10),
            rec.Delay,
            onRetry: null,
            cancellationToken);

    // The exact shapes production logged.
    private static Exception WrappedTimeout() =>
        new NpgsqlException("The operation has timed out", new TimeoutException("The operation has timed out."));

    private static Exception BareTimeout() => new TimeoutException("The operation has timed out.");

    // ----------------------------------------------------------------- classifier

    [Fact]
    public void Npgsql_wrapped_timeout_is_transient() =>
        Assert.True(CriterionConnectRetry.IsTransientConnectFailure(WrappedTimeout()));

    [Fact]
    public void Bare_TimeoutException_is_transient() =>
        // 2026-11-05 arrived like this, straight out of Npgsql.TaskTimeoutAndCancellation.
        Assert.True(CriterionConnectRetry.IsTransientConnectFailure(BareTimeout()));

    [Fact]
    public void Failed_login_is_NOT_transient()
    {
        // 🔒 Retrying a bad password is how a service account gets locked out.
        var badPassword = new PostgresException(
            "password authentication failed for user \"x\"", "FATAL", "FATAL", "28P01");

        Assert.False(CriterionConnectRetry.IsTransientConnectFailure(badPassword));
    }

    [Theory]
    [InlineData("57P03")] // cannot_connect_now (server starting up)
    [InlineData("53300")] // too_many_connections
    public void Transient_server_side_states_ARE_transient(string sqlState) =>
        Assert.True(CriterionConnectRetry.IsTransientConnectFailure(
            new PostgresException("transient", "FATAL", "FATAL", sqlState)));

    [Fact]
    public void Cancellation_exception_is_NOT_transient() =>
        Assert.False(CriterionConnectRetry.IsTransientConnectFailure(new OperationCanceledException()));

    [Fact]
    public void Unrelated_exception_is_NOT_transient() =>
        Assert.False(CriterionConnectRetry.IsTransientConnectFailure(new InvalidOperationException("bug")));

    // ----------------------------------------------------------------- the loop

    [Fact]
    public async Task Succeeds_on_first_attempt_without_waiting()
    {
        var rec = new Recorder();

        var result = await Run(rec, _ => "conn");

        Assert.Equal("conn", result);
        Assert.Equal(1, rec.Calls);
        Assert.Empty(rec.Waits);
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_the_next_attempt_wins()
    {
        var rec = new Recorder();

        var result = await Run(rec, n => n == 1 ? throw WrappedTimeout() : "conn");

        Assert.Equal("conn", result);
        Assert.Equal(2, rec.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(10) }, rec.Waits);
    }

    [Fact]
    public async Task Backoff_triples_and_the_last_failure_is_rethrown_after_max_attempts()
    {
        var rec = new Recorder();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Run(rec, _ => throw BareTimeout()));

        Assert.Equal("The operation has timed out.", ex.Message);
        Assert.Equal(3, rec.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) }, rec.Waits);
    }

    [Fact]
    public async Task Permanent_failure_fails_fast()
    {
        var rec = new Recorder();
        var badPassword = new PostgresException("password authentication failed", "FATAL", "FATAL", "28P01");

        await Assert.ThrowsAsync<PostgresException>(() => Run(rec, _ => throw badPassword));

        Assert.Equal(1, rec.Calls);
        Assert.Empty(rec.Waits);
    }

    [Fact]
    public async Task Cancelled_token_is_never_retried()
    {
        var rec = new Recorder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TimeoutException>(
            () => Run(rec, _ => throw BareTimeout(), cancellationToken: cts.Token));

        Assert.Equal(1, rec.Calls);
    }

    [Fact]
    public async Task Cancellation_during_the_backoff_stops_retrying()
    {
        // The work unit's budget firing mid-wait must win over the retry: one attempt,
        // the token fires during the backoff, and the real Task.Delay throws.
        using var cts = new CancellationTokenSource();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CriterionConnectRetry.OpenWithRetryAsync<int>(
                _ => { calls++; throw BareTimeout(); },
                3, TimeSpan.FromSeconds(10),
                (wait, ct) => { cts.Cancel(); return Task.Delay(wait, ct); },
                null, cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Backoff_is_capped_however_many_attempts_are_configured()
    {
        var rec = new Recorder();

        await Assert.ThrowsAsync<TimeoutException>(() => Run(rec, _ => throw BareTimeout(), maxAttempts: 40));

        Assert.Equal(40, rec.Calls);
        Assert.All(rec.Waits, w => Assert.True(w <= CriterionConnectRetry.MaxWait));
        Assert.Equal(CriterionConnectRetry.MaxWait, rec.Waits[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task One_or_fewer_attempts_disables_retrying(int maxAttempts)
    {
        var rec = new Recorder();

        await Assert.ThrowsAsync<TimeoutException>(() => Run(rec, _ => throw BareTimeout(), maxAttempts));

        Assert.Equal(1, rec.Calls);
    }

    [Fact]
    public async Task OnRetry_reports_attempt_numbers_and_waits()
    {
        var seen = new List<(int Attempt, int Of, TimeSpan Wait)>();
        var calls = 0;

        await CriterionConnectRetry.OpenWithRetryAsync(
            _ => ++calls < 3 ? throw BareTimeout() : Task.FromResult(1),
            3, TimeSpan.FromSeconds(10), (_, _) => Task.CompletedTask,
            (a, of, wait, _) => seen.Add((a, of, wait)),
            CancellationToken.None);

        Assert.Equal(new[] { (1, 3, TimeSpan.FromSeconds(10)), (2, 3, TimeSpan.FromSeconds(30)) }, seen);
    }

    // ----------------------------------------------------------------- settings

    [Fact]
    public void Defaults_are_three_attempts_with_a_ten_second_base_delay()
    {
        var s = new CriterionSettings();
        Assert.Equal(3, s.SourceConnectMaxAttempts);
        Assert.Equal(10, s.SourceConnectRetryDelaySeconds);
    }

    /// <summary>
    /// ⚠ COUPLING GUARD: the worst case to fail an open (every attempt timing out, plus
    /// every backoff wait) must stay well inside the shipped per-unit budget, or a dead
    /// source would hold each unit open until the work-unit timeout fired instead of
    /// failing on its own. Reads the real appsettings.json, falling back to the class
    /// defaults for the retry keys, which do not ship in the file.
    /// </summary>
    [Fact]
    public void Shipped_worst_case_connect_time_stays_well_under_the_work_unit_budget()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.HostAppSettingsFile),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var criterion = doc.RootElement.GetProperty("Loaders").GetProperty("Criterion");
        var defaults = new CriterionSettings();

        int Read(string name, int fallback) =>
            criterion.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;

        var connectTimeout = Math.Max(5, Read("SourceConnectTimeoutSeconds", defaults.SourceConnectTimeoutSeconds));
        var attempts = Math.Max(1, Read("SourceConnectMaxAttempts", defaults.SourceConnectMaxAttempts));
        var delay = Math.Max(0, Read("SourceConnectRetryDelaySeconds", defaults.SourceConnectRetryDelaySeconds));
        var workUnitTimeout = criterion.GetProperty("WorkUnitTimeoutSeconds").GetInt32();

        var worstCase = attempts * connectTimeout;
        for (int i = 0, wait = delay; i < attempts - 1; i++, wait *= 3) worstCase += wait;

        Assert.True(worstCase * 4 <= workUnitTimeout,
            $"Worst-case source connect ({worstCase}s = {attempts} x {connectTimeout}s + backoff from {delay}s) " +
            $"must stay under a quarter of WorkUnitTimeoutSeconds ({workUnitTimeout}s).");
    }

    // ----------------------------------------------------------------- failure summary

    [Fact]
    public void Failed_feed_with_a_reason_carries_it() =>
        Assert.Equal(
            "FinancialSeriesData (The operation has timed out)",
            CriterionModule.DescribeFailedFeed("FinancialSeriesData", "The operation has timed out"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Failed_feed_without_a_reason_is_named_alone(string? reason) =>
        Assert.Equal("FinancialSeriesData", CriterionModule.DescribeFailedFeed("FinancialSeriesData", reason));
}
