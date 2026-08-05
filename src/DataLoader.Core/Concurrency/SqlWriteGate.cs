using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DataLoader.Core.Concurrency;

/// <summary>
/// Process-local write gate that serializes stored-procedure MERGE/upsert calls
/// per target (database + procedure).
///
/// <para>
/// The pipeline runs work units in parallel (<see cref="ParallelRunner"/>), so
/// several MERGE statements can hit the same target table concurrently. SQL
/// Server can deadlock those against each other, and two concurrent
/// NOT-MATCHED inserts can race. Taking a per-target lock around the DB
/// round-trip removes both problems by letting only one writer touch a given
/// target at a time.
/// </para>
/// <para>
/// <b>Granularity is per writer-procedure</b> (<c>{dataSource}/{initialCatalog}::{proc}</c>).
/// This equals per-target-table only while each table has exactly one writer
/// procedure, which is the case today (1:1 proc→table). If a table ever gets a
/// second writer procedure, two concurrent writers would receive different keys
/// and would <b>not</b> be serialized against each other — in that case, key on
/// the target table name instead of the procedure name.
/// </para>
/// <para>
/// This is <b>process-local only</b>. Serializing writes within the process is
/// sufficient because a second same-loader host process is already blocked by
/// <c>SqlLoaderOverlapGuard</c> (a cross-process SQL app lock). Together they
/// cover both the in-process and cross-process cases.
/// </para>
/// </summary>
public static class SqlWriteGate
{
    /// <summary>
    /// One semaphore per distinct target key. Semaphores are never removed or
    /// disposed: a live waiter could still hold a reference, and the number of
    /// distinct keys is bounded by the number of distinct (database, procedure)
    /// targets, so the dictionary cannot grow without bound.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Builds a stable key identifying the write target — the data source and
    /// initial catalog plus the procedure name — <b>without</b> embedding
    /// credentials from the connection string. If the connection string cannot
    /// be parsed, falls back to the procedure name alone. This method never
    /// throws.
    ///
    /// <para>
    /// The key is per writer-procedure, which equals per-target-table only while
    /// each table has a single writer procedure (see the class remarks). If that
    /// ceases to hold, key on the target table name here instead.
    /// </para>
    /// </summary>
    public static string KeyFor(string connectionString, string procedureName)
    {
        var proc = procedureName ?? string.Empty;
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource ?? string.Empty;
            var initialCatalog = builder.InitialCatalog ?? string.Empty;
            // Lower-case the whole composed key once so server/catalog/proc are
            // all normalized together.
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{dataSource}/{initialCatalog}::{proc}").ToLowerInvariant();
        }
        catch (Exception)
        {
            // Malformed connection string — never throw from KeyFor. A key of
            // just the procedure name still serializes callers of that proc.
            return proc.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Acquires the gate for <paramref name="key"/>, waiting until it is free.
    /// Returns a releaser that frees the gate exactly once when disposed. The
    /// releaser is only created after the wait succeeds, so a cancelled wait
    /// never releases the semaphore.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        var sem = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            // Guard against double-release: only the first Dispose releases.
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }
}
