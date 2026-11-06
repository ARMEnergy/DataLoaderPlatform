using Npgsql;

namespace DataLoader.Criterion;

/// <summary>
/// Open-with-retry for the PostgreSQL source. The SQL Server side has
/// <c>SqlConnectionResilience.OpenWithRetryAsync</c>; that helper is typed to
/// <c>SqlConnection</c>/<c>SqlException</c>, so the Npgsql source needs its own, much
/// smaller version of the same shape.
///
/// <para>
/// Split out of <see cref="NpgsqlCriterionSource"/> so the loop and the classifier are
/// testable without a database: <c>open</c> stands in for the connection and
/// <c>delay</c> for <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// </para>
/// </summary>
internal static class CriterionConnectRetry
{
    /// <summary>Longest single backoff wait, however many attempts are configured.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a failed open is worth another attempt.
    ///
    /// <para>
    /// Both shapes production has logged are covered: Npgsql usually wraps the timeout
    /// (<c>NpgsqlException: The operation has timed out</c> → inner
    /// <see cref="TimeoutException"/>, which Npgsql reports as
    /// <see cref="NpgsqlException.IsTransient"/>), but on 2026-11-05 a bare
    /// <see cref="TimeoutException"/> came straight out of
    /// <c>Npgsql.TaskTimeoutAndCancellation.ExecuteAsync</c>.
    /// </para>
    /// <para>
    /// ⚠ A server-side error (<see cref="PostgresException"/>) is classified by Npgsql's
    /// own <see cref="NpgsqlException.IsTransient"/>, which says false for a bad password
    /// (<c>28P01</c>) and a missing database. Retrying a failed login is how a service
    /// account gets locked out, so that must stay a fail-fast.
    /// </para>
    /// <para>
    /// Note Npgsql also reports POOL exhaustion as an <see cref="NpgsqlException"/> over a
    /// <see cref="TimeoutException"/>, so it too would be retried. That cannot happen with
    /// today's flow (feeds run one at a time, enumeration holds one connection, and the
    /// pool is sized concurrency + 1), but a future pool-sizing bug would show up as
    /// slow retries before it shows up as a failure.
    /// </para>
    /// </summary>
    internal static bool IsTransientConnectFailure(Exception exception) => exception switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        TimeoutException => true,
        _ => false
    };

    /// <summary>
    /// Runs <paramref name="open"/> up to <paramref name="maxAttempts"/> times, waiting
    /// <paramref name="baseDelay"/> before the 2nd attempt and three times longer before
    /// each one after that.
    ///
    /// <para>
    /// ⚠ Cancellation is never retried: a cancelled token means the work unit's own budget
    /// expired or the process is stopping, and the caller's deadline outranks this one.
    /// </para>
    /// </summary>
    /// <param name="onRetry">
    /// Called before each wait with (failed attempt number, total attempts, the wait, the
    /// exception). Used for logging; the exception carries no connection string.
    /// </param>
    internal static async Task<T> OpenWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> open,
        int maxAttempts,
        TimeSpan baseDelay,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<int, int, TimeSpan, Exception>? onRetry,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, maxAttempts);
        var wait = baseDelay < TimeSpan.Zero ? TimeSpan.Zero : baseDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await open(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < attempts &&
                !cancellationToken.IsCancellationRequested &&
                IsTransientConnectFailure(ex))
            {
                onRetry?.Invoke(attempt, attempts, wait, ex);
                await delay(wait, cancellationToken).ConfigureAwait(false);
                // Capped so a large configured attempt count cannot grow the wait past
                // Task.Delay's ~49-day limit (or overflow the tick count).
                wait = wait >= MaxWait / 3 ? MaxWait : TimeSpan.FromTicks(wait.Ticks * 3);
            }
        }
    }
}
