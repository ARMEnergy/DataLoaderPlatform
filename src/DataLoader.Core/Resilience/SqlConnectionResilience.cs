using System.ComponentModel;
using Microsoft.Data.SqlClient;

namespace DataLoader.Core.Resilience;

/// <summary>
/// How hard the platform tries to open a SQL connection before giving up.
///
/// <para>⚠ The two numbers are COUPLED to the loader's own budget, exactly as
/// <c>HttpTimeoutSeconds</c>/<c>RetryCount</c> are: worst-case time to fail an open is
/// <c>MaxAttempts × ConnectTimeoutSeconds</c> plus the backoff between attempts. A
/// connection opened inside a work unit is additionally bounded by that loader's
/// <c>WorkUnitTimeoutSeconds</c>, which simply wins when it is lower — see the coupling
/// test in <c>SqlConnectionResilienceTests</c>.</para>
/// </summary>
public sealed class SqlConnectionResiliencePolicy
{
    /// <summary>
    /// Seconds to wait for a single connection attempt, written into the connection
    /// string as <c>Connect Timeout</c>.
    ///
    /// <para>⚠ Deliberately NOT ADO.NET's 15-second default. Production measured
    /// 2026-11-03: Windows Update / Automatic Maintenance on ARMH-OPSDB01 stalls the OS
    /// hard enough that Service Control Manager logs its own 30-second service timeouts
    /// (event 7011, two or three per window). SQL Server stays up throughout — uptime was
    /// unbroken for 96 days across every one of these incidents — but a TLS pre-login
    /// handshake started inside the stall cannot finish in 15 seconds, so the loader dies
    /// before it has read a single row. 26 runs across 7 loaders were lost that way
    /// between 2026-10-05 and 2026-11-03, about 1.2% of all runs.</para>
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Total attempts (NOT retries after the first). 1 disables retrying.
    ///
    /// <para>Three because the observed stalls outlast a single attempt: the 2026-11-02
    /// window logged SCM timeouts at 05:02:38, 05:03:08 and 05:03:38, so a lone 60-second
    /// attempt starting at 05:02 would still have failed.</para>
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Delay before the 2nd attempt; each subsequent attempt waits three times longer
    /// (2s, 6s, …). Short on purpose — <see cref="ConnectTimeoutSeconds"/> already
    /// supplies most of the elapsed time, and the point is to straddle the stall rather
    /// than to be polite to a server that is not under load.
    /// </summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Connection-string defaults and open-with-retry for every SQL Server connection the
/// platform makes.
///
/// <para>Why an ambient <see cref="Policy"/> rather than an injected dependency: there are
/// 60-odd <c>new SqlConnection(...)</c> sites across 23 loader assemblies, and threading a
/// policy object through all of them would be a far larger and riskier change than the bug
/// warrants. <see cref="Concurrency.SqlWriteGate"/> already establishes process-wide static
/// coordination as the house pattern for exactly this situation. Tests that need a
/// different policy pass one explicitly to
/// <see cref="OpenWithRetryAsync(SqlConnection,CancellationToken,SqlConnectionResiliencePolicy?)"/>
/// instead of mutating the static, so they do not interfere with each other.</para>
/// </summary>
public static class SqlConnectionResilience
{
    /// <summary>
    /// Process-wide policy, set once from <c>Platform:*</c> at host startup. Reads are
    /// cheap and lock-free; nothing mutates it after startup.
    /// </summary>
    public static SqlConnectionResiliencePolicy Policy { get; set; } = new();

    /// <summary>
    /// Returns <paramref name="connectionString"/> with <c>Connect Timeout</c> set to
    /// <paramref name="connectTimeoutSeconds"/>.
    ///
    /// <para>⚠ An explicit operator-supplied <c>Connect Timeout</c> always wins — if the
    /// key is already present the string is returned untouched. Silently overriding a
    /// value someone deliberately configured is how a "safe default" becomes an
    /// unexplainable production surprise.</para>
    ///
    /// <para>A blank or unparseable string is returned unchanged rather than throwing.
    /// This runs during options post-configuration at startup, where the useful error is
    /// the one the connection attempt itself produces ("Server not found", naming the
    /// server) and not a parser exception from a defaults helper.</para>
    /// </summary>
    public static string ApplyDefaults(string? connectionString, int connectTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? string.Empty;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);

            // ⚠ ShouldSerialize, NOT ContainsKey. SqlConnectionStringBuilder is strongly
            // typed, so ContainsKey answers "is this a known keyword?" — true for every
            // setting whether or not the parsed string mentioned it. Using it here made
            // this method a silent no-op that left every connection on the 15-second
            // default while appearing to work. ShouldSerialize reports only keys the
            // input actually set, and normalises the "Connection Timeout" alias onto the
            // same keyword.
            if (builder.ShouldSerialize("Connect Timeout")) return connectionString;

            builder.ConnectTimeout = connectTimeoutSeconds;
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }

    /// <summary>Convenience overload using the ambient <see cref="Policy"/>.</summary>
    public static string ApplyDefaults(string? connectionString) =>
        ApplyDefaults(connectionString, Policy.ConnectTimeoutSeconds);

    /// <summary>
    /// SQL error numbers that mean "the connection attempt failed for a reason that may
    /// not recur". <c>-2</c> (timeout) and <c>258</c> (wait operation timed out) are the
    /// two this was built for; the rest are the standard socket/name-resolution set.
    /// </summary>
    private static readonly HashSet<int> TransientNumbers = new()
    {
        -2, 53, 64, 121, 233, 258, 1231, 10053, 10054, 10060, 10061, 11001,
        // Azure SQL throttling/failover. Harmless here and correct if this ever moves.
        4221, 40197, 40501, 40613, 49918, 49919, 49920
    };

    /// <summary>
    /// Errors that must FAIL FAST. ⚠ This check runs before
    /// <see cref="TransientNumbers"/> and is the more important half of the pair: a wrong
    /// password or a missing database does not improve on the second attempt, and
    /// retrying a failed login three times is a good way to lock out the service account.
    /// </summary>
    private static readonly HashSet<int> PermanentNumbers = new()
    {
        18456, // Login failed for user
        18452, // Login from an untrusted domain
        4060,  // Cannot open database requested by the login
        916,   // Server principal cannot access the database
        229    // Permission denied on object
    };

    /// <summary>
    /// Whether <paramref name="exception"/> is worth another attempt.
    ///
    /// <para>⚠ The SNI layer reports the interesting case through an INNER
    /// <see cref="Win32Exception"/>, not through <see cref="SqlException.Number"/>: the
    /// production failure arrives as "an error occurred during the pre-login handshake
    /// (provider: SSL Provider, error: 0 - The wait operation timed out)" wrapping
    /// <c>Win32Exception (258)</c>. Classifying on <c>Number</c> alone therefore misses
    /// the exact error this exists to survive, which is why the inner chain is walked.</para>
    /// </summary>
    public static bool IsTransientConnectFailure(Exception? exception) =>
        exception is SqlException sql && IsTransientFailure(sql.Number, sql);

    /// <summary>
    /// The classification itself, split from <see cref="IsTransientConnectFailure"/> so it
    /// can be tested. <see cref="SqlException"/> has no public constructor, and fabricating
    /// one through reflection would couple the test suite to Microsoft.Data.SqlClient's
    /// private shape — a test that breaks on an unrelated package upgrade. Taking the error
    /// number and the exception separately keeps every real rule (the permanent-before-
    /// transient ordering, and the inner <see cref="Win32Exception"/> walk) exercised with
    /// genuine exception objects.
    /// </summary>
    internal static bool IsTransientFailure(int number, Exception? root)
    {
        if (PermanentNumbers.Contains(number)) return false;
        if (TransientNumbers.Contains(number)) return true;

        for (Exception? inner = root?.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is Win32Exception win32 && TransientNumbers.Contains(win32.NativeErrorCode))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Opens <paramref name="connection"/>, retrying transient connect failures per
    /// <paramref name="policy"/> (default: the ambient <see cref="Policy"/>).
    ///
    /// <para>⚠ Cancellation is never retried. A cancelled token means the work unit's own
    /// budget expired or the process is shutting down, and the caller's deadline outranks
    /// this one — retrying there would quietly extend a budget someone chose.</para>
    /// </summary>
    public static Task OpenWithRetryAsync(
        this SqlConnection connection,
        CancellationToken cancellationToken,
        SqlConnectionResiliencePolicy? policy = null) =>
        RetryAsync(
            ct => connection.OpenAsync(ct),
            IsTransientConnectFailure,
            policy ?? Policy,
            cancellationToken);

    /// <summary>
    /// The retry loop, split from <see cref="OpenWithRetryAsync"/> so it can be tested
    /// without a SQL Server: <paramref name="open"/> stands in for the connection and
    /// <paramref name="isTransient"/> for the classifier, letting a test drive attempt
    /// counting, backoff growth, fail-fast and cancellation with ordinary exceptions.
    /// </summary>
    internal static async Task RetryAsync(
        Func<CancellationToken, Task> open,
        Func<Exception, bool> isTransient,
        SqlConnectionResiliencePolicy policy,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, policy.MaxAttempts);
        var delay = policy.BaseDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await open(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                attempt < attempts &&
                !cancellationToken.IsCancellationRequested &&
                isTransient(ex))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * 3);
            }
        }
    }

    /// <summary>
    /// Synchronous counterpart, for the one caller that cannot go async:
    /// <c>SqlParamStore</c> resolves <c>SEE_DB</c> settings from inside options
    /// post-configuration, which has no async entry point. It is also the FIRST database
    /// call a loader process makes, so leaving it unprotected would miss the very stall
    /// this class exists for.
    /// </summary>
    public static void OpenWithRetry(
        this SqlConnection connection,
        SqlConnectionResiliencePolicy? policy = null)
    {
        policy ??= Policy;
        var attempts = Math.Max(1, policy.MaxAttempts);
        var delay = policy.BaseDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                connection.Open();
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransientConnectFailure(ex))
            {
                Thread.Sleep(delay);
                delay = TimeSpan.FromTicks(delay.Ticks * 3);
            }
        }
    }
}
