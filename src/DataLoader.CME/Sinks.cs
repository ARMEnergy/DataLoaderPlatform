using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataLoader.CME;

/// <summary>
/// Writes one bulletin's parsed rows to BOTH fact tables.
///
/// <para>
/// <b>Why one sink and not two pipelines.</b> A bulletin interleaves option and
/// future sections in a single 3–13 MB file, so the two tables are fed by one
/// parse. Running an option pipeline and a future pipeline would download and
/// re-parse every file twice — ~40 MB and ~750k lines of duplicated work per
/// trade date — and would split one file's outcome across two
/// <c>core.LoadLog</c> rows, so a half-loaded bulletin would look fully loaded
/// from either side. One work unit, one parse, one log row, two merges.
/// </para>
/// <para>
/// <b>The TVP contract.</b> A table-valued parameter binds BY POSITION, so each
/// DataTable's column order must match <c>sql/CME/002</c> exactly. Here that is
/// structural rather than clerical: <see cref="BuildTable"/> walks a
/// <see cref="CmeTableDescriptor"/>'s column list to create the columns AND to
/// project each row's values through the same list's
/// <see cref="CmeColumn.Get"/> selectors, so the schema and the values cannot
/// drift from one another. What still COULD drift is the descriptor versus the
/// <c>.sql</c> file — and <c>CmeTvpContractTests</c> parses the real
/// <c>.sql</c> and asserts name + order + type + nullability against the
/// descriptors, so that fails the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every
/// other loader in the repo.
/// </para>
/// <para>
/// <b>Batching.</b> One bulletin can yield ~150k rows across 20-plus columns.
/// Rows are merged in <see cref="CmeSettings.MergeBatchSize"/> chunks so each
/// request and each server-side transaction stays bounded, rather than sending a
/// single enormous table-valued parameter.
/// </para>
/// <para>
/// The base's <see cref="SqlWriteGate"/> keys on the PROC name, so the option
/// merge and the future merge serialize independently of each other and neither
/// contends with <c>arm.usp_UpsertFileLog</c>. Two work units hitting the same
/// table do serialize on it, which is what keeps concurrent MERGEs off each
/// other's key ranges.
/// </para>
/// </summary>
public sealed class CmeFactSink : ISink<CmeFactRow>
{
    private readonly string _connectionString;
    private readonly int _batchSize;
    private readonly ILogger _logger;

    public CmeFactSink(string connectionString, int batchSize, ILogger logger)
    {
        _connectionString = connectionString;
        _batchSize = batchSize <= 0 ? 20000 : batchSize;
        _logger = logger;
    }

    public async Task<int> WriteAsync(IReadOnlyList<CmeFactRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return 0;

        var written = 0;

        // Options first, then futures — a fixed order so two concurrent work units
        // always take the two write gates the same way round and cannot deadlock by
        // taking them in opposite orders.
        foreach (var table in CmeDescriptors.All)
        {
            var kind = table.TableId == "Option" ? CmeRowKind.Option : CmeRowKind.Future;
            var subset = rows.Where(r => r.Kind == kind).ToList();
            if (subset.Count == 0) continue;

            written += await MergeAsync(table, subset, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private async Task<int> MergeAsync(
        CmeTableDescriptor table, List<CmeFactRow> rows, CancellationToken cancellationToken)
    {
        var affected = 0;

        for (var offset = 0; offset < rows.Count; offset += _batchSize)
        {
            var batch = rows.GetRange(offset, Math.Min(_batchSize, rows.Count - offset));
            affected += await MergeBatchAsync(table, batch, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("CME: merged {Rows} row(s) into {Table} via {Proc}",
            rows.Count, table.TargetTable, table.MergeProc);

        return affected;
    }

    private async Task<int> MergeBatchAsync(
        CmeTableDescriptor table, List<CmeFactRow> batch, CancellationToken cancellationToken)
    {
        var dataTable = BuildTable(table, batch);

        try
        {
            // Serialize concurrent MERGE calls against the same target (database +
            // proc) to avoid parallel-run deadlocks and insert races.
            using var gate = await SqlWriteGate.AcquireAsync(
                SqlWriteGate.KeyFor(_connectionString, table.MergeProc), cancellationToken).ConfigureAwait(false);

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand(table.MergeProc, conn) { CommandType = CommandType.StoredProcedure };

            var tvp = cmd.Parameters.AddWithValue("@Records", dataTable);
            tvp.SqlDbType = SqlDbType.Structured;
            tvp.TypeName = table.TvpType;

            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is int n ? n : batch.Count;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "CME SQL sink failure: {Proc} with {Count} row(s)", table.MergeProc, batch.Count);
            throw;
        }
    }

    /// <summary>
    /// Build the TVP DataTable for one table descriptor. Both the schema and the
    /// values come from the SAME ordered column list, so they cannot disagree.
    /// </summary>
    internal static DataTable BuildTable(CmeTableDescriptor table, IReadOnlyList<CmeFactRow> rows)
    {
        var dataTable = new DataTable();

        foreach (var column in table.Columns)
            dataTable.Columns.Add(column.Name, column.ClrType);

        var values = new object[table.Columns.Count];

        foreach (var row in rows)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                var value = column.Get(row);

                // Defensive: a NOT NULL TVP column reaching the server as NULL fails
                // the whole batch with a server-side message that names the type, not
                // the row. Fail here instead, where the offending column is known.
                if (column.Required && (value is null || value == DBNull.Value))
                    throw new InvalidOperationException(
                        $"CME {table.TableId}: column '{column.Name}' is NOT NULL but the parsed row " +
                        $"({row.ExchangeCode} {CmeTime.Iso(row.TradeDate)} {row.ProductSymbol}) has no value.");

                values[i] = value;
            }

            dataTable.Rows.Add(values);
        }

        return dataTable;
    }
}
