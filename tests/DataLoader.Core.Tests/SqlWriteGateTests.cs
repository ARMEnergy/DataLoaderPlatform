using DataLoader.Core.Concurrency;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SqlWriteGate"/> — a pure in-process keyed lock.
/// No SQL/network is touched. Every test that exercises the gate uses a unique
/// key (<see cref="NewKey"/>) so it is isolated from the process-wide static
/// semaphore dictionary and cannot interfere with other tests, regardless of
/// ordering or parallelism.
///
/// Determinism: "still blocked" is asserted via a short bounded wait (a blocked
/// acquire never completes until released, so this direction cannot be flaky);
/// "now completes" is asserted with a generous timeout so slow CI machines do
/// not produce false failures.
/// </summary>
public class SqlWriteGateTests
{
    /// <summary>Short probe used to confirm an acquire is still blocked.</summary>
    private static readonly TimeSpan BlockedProbe = TimeSpan.FromMilliseconds(300);

    /// <summary>Generous timeout for waits that are expected to complete.</summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private static string NewKey() => $"test::{Guid.NewGuid():N}";

    /// <summary>Asserts <paramref name="task"/> has NOT completed within a short bounded wait.</summary>
    private static async Task AssertStillBlockedAsync(Task task, string because)
    {
        var winner = await Task.WhenAny(task, Task.Delay(BlockedProbe));
        Assert.True(winner != task, because);
        Assert.False(task.IsCompleted, because);
    }

    // ---------------------------------------------------------------------
    // 1. KeyFor: stability & safety
    // ---------------------------------------------------------------------

    [Fact]
    public void KeyFor_SameInputs_ProducesEqualKeys()
    {
        const string cs = "Server=tcp:dbhost,1433;Database=WriteTarget";
        Assert.Equal(
            SqlWriteGate.KeyFor(cs, "dbo.upsert_widget"),
            SqlWriteGate.KeyFor(cs, "dbo.upsert_widget"));
    }

    [Fact]
    public void KeyFor_DifferentInitialCatalog_ProducesDifferentKeys()
    {
        var k1 = SqlWriteGate.KeyFor("Server=tcp:dbhost,1433;Database=CatalogOne", "dbo.merge_rows");
        var k2 = SqlWriteGate.KeyFor("Server=tcp:dbhost,1433;Database=CatalogTwo", "dbo.merge_rows");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void KeyFor_DifferentDataSource_ProducesDifferentKeys()
    {
        var k1 = SqlWriteGate.KeyFor("Server=tcp:hostone,1433;Database=WriteTarget", "dbo.merge_rows");
        var k2 = SqlWriteGate.KeyFor("Server=tcp:hosttwo,1433;Database=WriteTarget", "dbo.merge_rows");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void KeyFor_DifferentProcedure_ProducesDifferentKeys()
    {
        const string cs = "Server=tcp:dbhost,1433;Database=WriteTarget";
        Assert.NotEqual(
            SqlWriteGate.KeyFor(cs, "dbo.upsert_widget"),
            SqlWriteGate.KeyFor(cs, "dbo.upsert_gadget"));
    }

    [Fact]
    public void KeyFor_ProcedureNameCasing_IsNormalized()
    {
        const string cs = "Server=tcp:dbhost,1433;Database=WriteTarget";
        Assert.Equal(
            SqlWriteGate.KeyFor(cs, "arm.USP_X"),
            SqlWriteGate.KeyFor(cs, "arm.usp_x"));
    }

    [Fact]
    public void KeyFor_ServerAndCatalogCasing_IsNormalized()
    {
        Assert.Equal(
            SqlWriteGate.KeyFor("Server=tcp:DBHOST,1433;Database=WRITETARGET", "dbo.merge_rows"),
            SqlWriteGate.KeyFor("Server=tcp:dbhost,1433;Database=writetarget", "dbo.merge_rows"));
    }

    [Fact]
    public void KeyFor_DoesNotLeakCredentials()
    {
        // Data source / catalog deliberately avoid the substrings "sa"/"supersecret123".
        const string cs = "Server=tcp:dbhost,1433;Database=WriteTarget;User ID=sa;Password=SuperSecret123";
        var key = SqlWriteGate.KeyFor(cs, "dbo.merge_rows");

        Assert.DoesNotContain("supersecret123", key);
        Assert.DoesNotContain("sa", key);
    }

    [Fact]
    public void KeyFor_MalformedConnectionString_DoesNotThrow_FallsBackToProcName()
    {
        // An unrecognized keyword makes SqlConnectionStringBuilder throw; KeyFor
        // must swallow that and fall back to the (lowercased) procedure name.
        var key = SqlWriteGate.KeyFor("NotARealKeyword=oops", "dbo.MyProc");
        Assert.Equal("dbo.myproc", key);
    }

    [Fact]
    public async Task KeyFor_EmptyConnectionString_DoesNotThrow_AndKeyIsUsable()
    {
        var key = SqlWriteGate.KeyFor(string.Empty, "dbo.MyProc");
        Assert.False(string.IsNullOrEmpty(key));

        // The returned key must be usable with the gate.
        using var releaser = await SqlWriteGate.AcquireAsync(key, CancellationToken.None)
            .WaitAsync(CompletionTimeout);
    }

    // ---------------------------------------------------------------------
    // 2. Mutual exclusion (same key)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_SameKey_SecondCallBlocksUntilFirstReleased()
    {
        var key = NewKey();

        var first = await SqlWriteGate.AcquireAsync(key, CancellationToken.None)
            .WaitAsync(CompletionTimeout);

        var second = SqlWriteGate.AcquireAsync(key, CancellationToken.None);
        await AssertStillBlockedAsync(second, "second acquire of the same key must block while the first is held");

        first.Dispose();

        var secondReleaser = await second.WaitAsync(CompletionTimeout);
        secondReleaser.Dispose();
    }

    // ---------------------------------------------------------------------
    // 3. Different keys don't block each other
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_DifferentKeys_DoNotBlock()
    {
        var keyA = NewKey();
        var keyB = NewKey();

        var a = SqlWriteGate.AcquireAsync(keyA, CancellationToken.None);
        var b = SqlWriteGate.AcquireAsync(keyB, CancellationToken.None);

        // Both must complete concurrently without either being released first.
        var releasers = await Task.WhenAll(a, b).WaitAsync(CompletionTimeout);

        foreach (var r in releasers)
        {
            r.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // 4. Release-exactly-once / double-Dispose is safe
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotBumpSemaphoreCount()
    {
        var key = NewKey();

        var holder = await SqlWriteGate.AcquireAsync(key, CancellationToken.None)
            .WaitAsync(CompletionTimeout);

        // Double dispose. If the second Dispose wrongly released again, the
        // semaphore count would climb to 2 and BOTH acquires below would proceed.
        holder.Dispose();
        holder.Dispose();

        var t1 = SqlWriteGate.AcquireAsync(key, CancellationToken.None);
        var t2 = SqlWriteGate.AcquireAsync(key, CancellationToken.None);

        // Exactly one must proceed; the other must block.
        var winner = await Task.WhenAny(t1, t2).WaitAsync(CompletionTimeout);
        var loser = winner == t1 ? t2 : t1;

        await AssertStillBlockedAsync(loser, "double-dispose must not bump the semaphore count above 1");

        (await winner).Dispose();

        var loserReleaser = await loser.WaitAsync(CompletionTimeout);
        loserReleaser.Dispose();
    }

    // ---------------------------------------------------------------------
    // 5. Cancellation
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_CanceledToken_ThrowsAndLeavesGateUsable()
    {
        var key = NewKey();

        var holder = await SqlWriteGate.AcquireAsync(key, CancellationToken.None)
            .WaitAsync(CompletionTimeout);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SqlWriteGate.AcquireAsync(key, cts.Token));

        // The cancelled wait must NOT have consumed a permit: after the holder
        // releases, a fresh acquire must still succeed.
        holder.Dispose();

        var fresh = await SqlWriteGate.AcquireAsync(key, CancellationToken.None)
            .WaitAsync(CompletionTimeout);
        fresh.Dispose();
    }

    // ---------------------------------------------------------------------
    // 6. Serialization under contention
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AcquireAsync_ManyContendersSameKey_AreFullySerialized()
    {
        var key = NewKey();
        const int n = 50;

        var currentlyInside = 0;   // atomic in/out counter for reliable max tracking
        var maxConcurrency = 0;    // updated via InterlockedMax
        var completed = 0;         // PLAIN int: any overlap corrupts this count

        // Release all contenders simultaneously to maximize contention.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, n).Select(async _ =>
        {
            await start.Task;
            using (await SqlWriteGate.AcquireAsync(key, CancellationToken.None))
            {
                var inside = Interlocked.Increment(ref currentlyInside);
                InterlockedMax(ref maxConcurrency, inside);

                // Non-atomic increment: safe only if the gate truly serializes.
                completed++;

                // Widen the critical-section window to expose any overlap.
                await Task.Yield();

                Interlocked.Decrement(ref currentlyInside);
            }
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, maxConcurrency);
        Assert.Equal(n, completed);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
