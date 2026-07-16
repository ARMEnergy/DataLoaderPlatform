using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Persistence;

/// <summary>
/// SQL Server implementation of <see cref="ILoadLogRepository"/>. Writes to
/// the platform's shared <c>core.LoaderRun</c> and <c>core.LoadLog</c> tables.
///
/// This is registered once by the host and shared across every loader.
/// Loaders never touch this directly — the pipeline does, on their behalf.
/// </summary>
public sealed class SqlLoadLogRepository : ILoadLogRepository
{
    private readonly PlatformSettings _settings;
    private readonly ILogger<SqlLoadLogRepository> _logger;

    public SqlLoadLogRepository(IOptions<PlatformSettings> settings, ILogger<SqlLoadLogRepository> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    private SqlConnection CreateConnection() => new(_settings.LoadLogConnectionString);

    public async Task BeginRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand("core.usp_BeginLoaderRun", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@RunId", runId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteRunAsync(Guid runId, bool success, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand("core.usp_CompleteLoaderRun", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@RunId", runId);
        cmd.Parameters.AddWithValue("@Success", success);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoadLogHandle?> BeginAsync(
        string loaderId, Guid runId, string workUnitKey, string workUnitDisplay,
        CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("core.usp_BeginLoadLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@LoaderId", loaderId);
        cmd.Parameters.AddWithValue("@RunId", runId);
        cmd.Parameters.AddWithValue("@WorkUnitKey", workUnitKey);
        cmd.Parameters.AddWithValue("@WorkUnitDisplay", (object?)workUnitDisplay ?? DBNull.Value);

        var outParam = new SqlParameter("@LoadLogId", SqlDbType.BigInt) { Direction = ParameterDirection.Output };
        cmd.Parameters.Add(outParam);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // SP returns -1 when this unit is already successfully loaded
        if (outParam.Value is long id && id > 0)
        {
            return new LoadLogHandle
            {
                LoadLogId = id,
                LoaderId = loaderId,
                WorkUnitKey = workUnitKey,
                StartedAtUtc = DateTime.UtcNow
            };
        }
        return null;
    }

    public async Task CompleteSuccessAsync(LoadLogHandle handle, int recordsProcessed, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand("core.usp_CompleteLoadLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@LoadLogId", handle.LoadLogId);
        cmd.Parameters.AddWithValue("@IsComplete", true);
        cmd.Parameters.AddWithValue("@RecordsProcessed", recordsProcessed);
        cmd.Parameters.AddWithValue("@ErrorMessage", DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteFailureAsync(LoadLogHandle handle, string errorMessage, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand("core.usp_CompleteLoadLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@LoadLogId", handle.LoadLogId);
        cmd.Parameters.AddWithValue("@IsComplete", false);
        cmd.Parameters.AddWithValue("@RecordsProcessed", DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
