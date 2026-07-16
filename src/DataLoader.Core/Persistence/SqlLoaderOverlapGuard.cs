using System.Data;
using DataLoader.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Persistence;

/// <summary>
/// Acquires an exclusive per-loader SQL Server app lock so two processes
/// running the same loader can't overlap. The lock is session-scoped — it
/// stays held while the connection stays open, and auto-releases on
/// disconnect (so a crashed runner doesn't leave the lock stuck).
///
/// Used by <see cref="Hosting.PlatformHost"/> around the per-loader call to
/// <c>module.RunAsync</c>. If the lock can't be acquired, the loader is
/// skipped on this invocation and the next scheduled run will retry.
/// </summary>
public interface ILoaderOverlapGuard
{
    /// <summary>
    /// Try to acquire exclusive ownership of <paramref name="loaderId"/>.
    /// Returns a disposable handle on success (release the lock by
    /// disposing), or <c>null</c> if another process already holds it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string loaderId, CancellationToken cancellationToken);
}

public sealed class SqlLoaderOverlapGuard : ILoaderOverlapGuard
{
    private readonly PlatformSettings _settings;
    private readonly ILogger<SqlLoaderOverlapGuard> _logger;

    public SqlLoaderOverlapGuard(IOptions<PlatformSettings> settings, ILogger<SqlLoaderOverlapGuard> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string loaderId, CancellationToken cancellationToken)
    {
        // The lock is session-scoped, so the same connection has to stay
        // open for the duration of the loader run. Hand the connection to
        // the returned handle; disposing the handle closes it (and releases
        // the lock).
        var conn = new SqlConnection(_settings.LoadLogConnectionString);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var cmd = new SqlCommand("core.usp_TryAcquireLoaderLock", conn)
                         { CommandType = CommandType.StoredProcedure })
            {
                cmd.Parameters.AddWithValue("@LoaderId", loaderId);
                cmd.Parameters.AddWithValue("@TimeoutMillis", 0);
                var outParam = new SqlParameter("@Acquired", SqlDbType.Bit)
                    { Direction = ParameterDirection.Output };
                cmd.Parameters.Add(outParam);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                bool acquired = outParam.Value is bool b && b;
                if (!acquired)
                {
                    _logger.LogWarning(
                        "Loader {LoaderId} is already running in another process — skipping this invocation",
                        loaderId);
                    await conn.DisposeAsync().ConfigureAwait(false);
                    return null;
                }
            }

            _logger.LogDebug("Acquired overlap lock for {LoaderId}", loaderId);
            return new LockHandle(conn, loaderId, _logger);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly SqlConnection _conn;
        private readonly string _loaderId;
        private readonly ILogger _logger;
        private int _disposed;

        public LockHandle(SqlConnection conn, string loaderId, ILogger logger)
        {
            _conn = conn;
            _loaderId = loaderId;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                await using var cmd = new SqlCommand("core.usp_ReleaseLoaderLock", _conn)
                    { CommandType = CommandType.StoredProcedure };
                cmd.Parameters.AddWithValue("@LoaderId", _loaderId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to release overlap lock for {Loader}; relying on session-close auto-release",
                    _loaderId);
            }
            finally
            {
                await _conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
