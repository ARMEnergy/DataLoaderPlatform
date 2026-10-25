using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Pins the connection-disposal idiom <c>SqlWatchdogRepository</c> and
/// <c>SqlLoadLogRepository</c> both rely on: bind the connection to the
/// caller's <c>using</c> scope BEFORE awaiting the (possibly throwing) call to
/// <c>OpenAsync</c>.
///
/// <para><c>SqlWatchdogRepository</c> cannot be exercised directly here — it
/// constructs <c>Microsoft.Data.SqlClient.SqlConnection</c> inline with no
/// injectable seam, and proving the leak against a real server would need
/// either a live database or an unroutable one, neither of which can
/// distinguish "leaked" from "disposed" from outside the process (the thrown
/// exception looks identical either way; the only difference is an object
/// nothing can observe without a seam). So this test pins the two candidate
/// SHAPES by name, using a fake <see cref="DbConnection"/> whose
/// <c>OpenAsync</c> always throws: the shape <c>SqlWatchdogRepository</c> used
/// BEFORE this fix (construct-and-open inside a helper that returns the
/// connection — if <c>Open</c> throws, the helper never returns, so the
/// caller's <c>await using var conn = await Helper(...)</c> never binds and
/// <c>DisposeAsync</c> is never called) against the shape it uses NOW (bind to
/// the <c>using</c> scope first). Reverting <c>SqlWatchdogRepository.cs</c> to
/// the old shape does not fail this test — the test targets the pattern, not
/// the file — but copying that exact old shape into
/// <see cref="UsingPreFixShapeLeaksOnOpenFailureAsync"/> is what caught the
/// real bug, and is pinned here so the pattern cannot silently return.</para>
/// </summary>
public class SqlWatchdogRepositoryDisposalTests
{
    /// <summary>A connection whose <see cref="OpenAsync(CancellationToken)"/> always throws,
    /// and which records whether it was ever disposed.</summary>
    private sealed class ThrowsOnOpenConnection : DbConnection
    {
        public bool AsyncDisposeCalled { get; private set; }
        public int SyncDisposeCount { get; private set; }

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated: the server could not be reached");

        public override ValueTask DisposeAsync()
        {
            AsyncDisposeCalled = true;
            return base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            SyncDisposeCount++;
            base.Dispose(disposing);
        }

        // DbConnection's remaining abstract members — never exercised by these
        // tests, since OpenAsync always throws before anything else runs.
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    /// <summary>
    /// The shape <c>SqlWatchdogRepository.OpenAsync</c> used BEFORE this fix:
    /// construct and await <c>Open</c> INSIDE the helper, then return the
    /// connection. A throw from <c>Open</c> means this method never returns.
    /// </summary>
    private static async Task<DbConnection> OpenAsync_PreFixShape(Func<DbConnection> factory, CancellationToken ct)
    {
        var conn = factory();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task UsePreFixShapeAsync(Func<DbConnection> factory, CancellationToken ct)
    {
        await using var conn = await OpenAsync_PreFixShape(factory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The shape <c>SqlWatchdogRepository</c> uses now, matching
    /// <c>SqlLoadLogRepository</c>: bind to the <c>using</c> scope BEFORE
    /// awaiting the throwing <c>Open</c> call.
    /// </summary>
    private static async Task UseFixedShapeAsync(Func<DbConnection> factory, CancellationToken ct)
    {
        await using var conn = factory();
        await conn.OpenAsync(ct).ConfigureAwait(false);
    }

    [Fact]
    public async Task UsingPreFixShapeLeaksOnOpenFailureAsync()
    {
        var fake = new ThrowsOnOpenConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UsePreFixShapeAsync(() => fake, CancellationToken.None));

        // This is the bug the reviewer found: Open threw, so the helper never
        // returned, so the caller's `await using` never bound, so nothing was
        // ever disposed.
        Assert.False(fake.AsyncDisposeCalled);
        Assert.Equal(0, fake.SyncDisposeCount);
    }

    [Fact]
    public async Task UsingFixedShapeDisposesConnectionOnOpenFailureAsync()
    {
        var fake = new ThrowsOnOpenConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseFixedShapeAsync(() => fake, CancellationToken.None));

        // Bound to the `using` scope before the throwing await, so disposal
        // still runs even though Open never succeeded.
        Assert.True(fake.AsyncDisposeCalled);
    }
}
