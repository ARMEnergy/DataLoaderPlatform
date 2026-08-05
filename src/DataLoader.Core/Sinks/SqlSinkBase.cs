using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataLoader.Core.Sinks;

/// <summary>
/// Convenience base for SQL Server sinks that bulk-merge via a Table-Valued
/// Parameter calling a stored procedure.
///
/// Subclasses supply:
///   - the connection string         (<see cref="GetConnectionString"/>)
///   - the proc name                  (<see cref="StoredProcedureName"/>)
///   - the TVP type name              (<see cref="TableValuedParameterType"/>)
///   - how to build the DataTable     (<see cref="BuildTable"/>)
///
/// Everything else — connection lifecycle, error logging, return-value
/// handling — happens here. This replaces the boilerplate that used to live
/// in <c>DataRepository</c>.
/// </summary>
public abstract class SqlSinkBase<TRow> : ISink<TRow>
{
    private readonly ILogger _logger;

    protected SqlSinkBase(ILogger logger)
    {
        _logger = logger;
    }

    protected abstract string GetConnectionString();
    protected abstract string StoredProcedureName { get; }
    protected abstract string TableValuedParameterType { get; }
    protected abstract DataTable BuildTable(IReadOnlyList<TRow> rows);

    /// <summary>
    /// Override to return false if the proc does not return a row count via
    /// <c>ExecuteScalar</c>. Defaults to true (matching the existing
    /// <c>usp_BulkMergeTimeseriesData</c> pattern).
    /// </summary>
    protected virtual bool ProcedureReturnsRowCount => true;

    public async Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return 0;

        var table = BuildTable(rows);

        try
        {
            // Serialize concurrent MERGE/upsert calls against the same target
            // (database + proc) to avoid parallel-run deadlocks and insert races.
            var key = SqlWriteGate.KeyFor(GetConnectionString(), StoredProcedureName);
            using var gate = await SqlWriteGate.AcquireAsync(key, cancellationToken).ConfigureAwait(false);

            await using var conn = new SqlConnection(GetConnectionString());
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand(StoredProcedureName, conn) { CommandType = CommandType.StoredProcedure };
            var tvp = cmd.Parameters.AddWithValue("@Records", table);
            tvp.SqlDbType = SqlDbType.Structured;
            tvp.TypeName = TableValuedParameterType;

            if (ProcedureReturnsRowCount)
            {
                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return result is int n ? n : rows.Count;
            }
            else
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return rows.Count;
            }
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL sink failure: {Proc} with {Count} rows", StoredProcedureName, rows.Count);
            throw;
        }
    }

    /// <summary>Helper for building TVP DataTables — null DB value when string is blank.</summary>
    protected static object NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    /// <summary>Helper — nullable to DBNull.</summary>
    protected static object DbNullable<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    /// <summary>Helper — nullable reference to DBNull.</summary>
    protected static object DbNullableObj(object? value) =>
        value ?? DBNull.Value;
}
