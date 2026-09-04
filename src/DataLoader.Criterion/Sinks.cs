using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.Criterion;

/// <summary>
/// The TVP write path for every Criterion feed. One class, parameterised by the
/// TABLE descriptor, rather than nine near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A TVP binds BY POSITION, so the DataTable's column order
/// must match <c>sql/Criterion/002_CreateCriterionTvpTypes.sql</c> exactly. Here that
/// is structural rather than clerical: <see cref="BuildTable"/> iterates
/// <see cref="CriterionTableDescriptor.Columns"/>, and both the generated SELECT and
/// the reader filled <see cref="CriterionRow.Values"/> from the same list in the same
/// order, so the three cannot drift from each other. What still COULD drift is the
/// descriptor versus the .sql file — and <c>CriterionTvpContractTests</c> parses the
/// real .sql and asserts name + order + type against the descriptor, so that fails
/// the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every other
/// loader in the repo. Neither are the columns with no source
/// (<c>Enabled</c>, <c>IsLatest</c>, <c>MappingId</c>, <c>Id</c>) or the derived
/// <c>Point</c> — see 002 for why each is absent.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the
/// proc name. Every Criterion feed has its own proc, so the nine pipelines never
/// serialize against each other — but two work units of the SAME feed running
/// concurrently do, which is what keeps parallel merges into one table off each
/// other's locks.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: <c>SinkTests</c> subclasses it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract without
/// opening a connection.
/// </remarks>
public class CriterionTvpSink : SqlSinkBase<CriterionRow>
{
    private readonly CriterionTableDescriptor _table;
    private readonly string _connectionString;

    public CriterionTvpSink(CriterionTableDescriptor table, string connectionString, ILogger logger)
        : base(logger)
    {
        _table = table;
        _connectionString = connectionString;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _table.MergeProc;
    protected override string TableValuedParameterType => _table.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<CriterionRow> rows)
    {
        var table = new DataTable();

        foreach (var column in _table.Columns)
            table.Columns.Add(column.Name, column.ClrType);

        foreach (var row in rows)
        {
            // Defensive: a length mismatch would mean the reader and the descriptor
            // disagree, which is a bug that must not reach the server as silently
            // shifted columns.
            if (row.Values.Length != _table.Columns.Count)
                throw new InvalidOperationException(
                    $"Criterion {_table.TableName}: row has {row.Values.Length} value(s) but the descriptor " +
                    $"declares {_table.Columns.Count} column(s).");

            table.Rows.Add(row.Values);
        }

        return table;
    }
}

/// <summary>
/// Splits a work unit's rows into TVP-sized batches and delegates each to an inner
/// <see cref="CriterionTvpSink"/>.
///
/// <para>
/// Only <c>FinancialSeriesData</c> ever exceeds one batch in practice — a paged unit
/// unpivots to roughly 600,000 rows at the default page size, against a 50,000-row
/// batch. The other eight feeds' units are far smaller and take the single-batch
/// path, which is byte-for-byte what an unbatched sink would have done.
/// </para>
/// <para>
/// Batching matters for more than memory: a single 600,000-row TVP holds its MERGE in
/// one transaction, keeping locks on the target and tempdb space for the whole call.
/// Each batch is independently idempotent (every merge keys on the target's PK), so a
/// failure part-way leaves the earlier batches durably written and the work unit's
/// resume key UN-recorded — the next run redoes the whole unit and converges. That
/// at-least-once posture is the same one every other loader in this repo has; it is
/// safe here precisely because no merge deletes by absence.
/// </para>
/// <para>
/// This composes rather than subclasses because <see cref="SqlSinkBase{TRow}.WriteAsync"/>
/// is not virtual, and making it so would change shared Core behaviour for all
/// fourteen other loaders to serve one feed's needs.
/// </para>
/// </summary>
public sealed class CriterionBatchingSink : ISink<CriterionRow>
{
    private readonly CriterionTvpSink _inner;
    private readonly CriterionTableDescriptor _table;
    private readonly int _batchSize;
    private readonly ILogger _logger;

    public CriterionBatchingSink(
        CriterionTableDescriptor table, string connectionString, int batchSize, ILogger logger)
    {
        _table = table;
        _batchSize = Math.Max(1, batchSize);
        _logger = logger;
        _inner = new CriterionTvpSink(table, connectionString, logger);
    }

    public async Task<int> WriteAsync(IReadOnlyList<CriterionRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return 0;
        if (rows.Count <= _batchSize) return await _inner.WriteAsync(rows, cancellationToken).ConfigureAwait(false);

        var written = 0;
        var batches = 0;

        for (var offset = 0; offset < rows.Count; offset += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var take = Math.Min(_batchSize, rows.Count - offset);
            var batch = new CriterionRow[take];
            for (var i = 0; i < take; i++) batch[i] = rows[offset + i];

            written += await _inner.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            batches++;
        }

        _logger.LogDebug("Criterion {Table}: merged {Rows} row(s) in {Batches} batch(es) of up to {Size}",
            _table.TableName, rows.Count, batches, _batchSize);

        return written;
    }
}
