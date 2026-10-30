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

    /// <summary>
    /// Command timeout applied to every TVP merge this sink makes, in seconds.
    ///
    /// <para>
    /// ⚠ Deliberately NOT ADO.NET's 30-second default. Every one of this base class's
    /// subclasses inherited that default until 2026-10-30, when Criterion's
    /// FinancialSeriesData feed — merging ~136.9M rows/day in 50,000-row TVP batches —
    /// started throwing <c>SqlException -2</c> (a CLIENT-side command timeout, not a
    /// server error) roughly 1 run in 15. Sporadic failures sitting at a fixed boundary
    /// are the signature of a command timeout, not of a consistently slow query. The
    /// same table had already taught this lesson once, on the READ side: a 2026-10-02
    /// incident raised <c>CriterionLoadValidator</c>'s post-load check to 300s, but the
    /// sinks were never revisited, so every MERGE kept running on 30s while every
    /// validation query ran on 300s.
    /// </para>
    /// <para>
    /// ⚠ <b>Coupled to the loader's <c>WorkUnitTimeoutSeconds</c> — this must stay
    /// comfortably below it.</b> The pipeline's per-work-unit budget is what finally
    /// cancels a wedged unit; if this timeout sits close to or above that budget, a
    /// stuck MERGE holds the unit open until something else gives up, instead of this
    /// timeout failing the call cleanly and letting the loader retry the unit next run.
    /// Criterion ships <c>WorkUnitTimeoutSeconds = 1800</c>, so this default leaves
    /// ample room — but a loader with a short unit budget and a long sink timeout here
    /// would be misconfigured, not merely generous.
    /// </para>
    /// </summary>
    protected virtual int CommandTimeoutSeconds => 300;

    /// <summary>
    /// Builds the (unopened-connection-safe) command this sink executes, including the
    /// timeout. Pulled out of <see cref="WriteAsync"/> as its own seam so a test can
    /// assert <see cref="CommandTimeoutSeconds"/> reaches a real <see cref="SqlCommand"/>
    /// without opening a connection to a server — which is exactly what a regression
    /// test for a CLIENT-side command timeout can and must do, since the bug this guards
    /// against never involves the server at all.
    /// </summary>
    protected virtual SqlCommand CreateCommand(SqlConnection connection) =>
        new SqlCommand(StoredProcedureName, connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = CommandTimeoutSeconds
        };

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

            await using var cmd = CreateCommand(conn);
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
