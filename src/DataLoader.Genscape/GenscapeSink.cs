using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.Genscape;

/// <summary>
/// The TVP write path for both Genscape feeds. One class, parameterised by the TABLE
/// descriptor, rather than two near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A TVP binds BY POSITION, so the DataTable's column order
/// must match <c>sql/Genscape/002</c> exactly. Here that is structural rather than
/// clerical: <see cref="BuildTable"/> iterates
/// <see cref="GenscapeTableDescriptor.Columns"/>, and the feed's parser filled
/// <see cref="GenscapeRow.Values"/> from the same list in the same order, so the two
/// cannot drift from each other. What still COULD drift is the descriptor versus the
/// .sql file — and <c>GenscapeTvpContractTests</c> parses the real .sql and asserts
/// name + order + type against the descriptor, so that fails the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every other
/// loader in the repo.
/// </para>
/// <para>
/// <b>No batching.</b> Every other TVP sink in this repo splits large writes; this one
/// does not need to. A work unit's row count is bounded by the API's own 5,000-row
/// response cap times the number of bisection leaves, and at the observed density
/// (~460 rows per year for the busiest feed/region) even a decade-wide backfill chunk
/// is a few thousand rows. A batching layer here would be untested machinery guarding
/// against a case that cannot arise.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the proc
/// name. Each feed has its own proc, so the two pipelines never serialize against each
/// other — but two work units of the SAME feed running concurrently do, which is what
/// keeps parallel merges into one table off each other's locks.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: <c>GenscapeSinkTests</c> subclasses it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract without
/// opening a connection.
/// </remarks>
public class GenscapeTvpSink : SqlSinkBase<GenscapeRow>
{
    private readonly GenscapeTableDescriptor _table;
    private readonly string _connectionString;

    public GenscapeTvpSink(GenscapeTableDescriptor table, string connectionString, ILogger logger)
        : base(logger)
    {
        _table = table;
        _connectionString = connectionString;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _table.MergeProc;
    protected override string TableValuedParameterType => _table.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<GenscapeRow> rows)
    {
        var table = new DataTable();

        foreach (var column in _table.Columns)
            table.Columns.Add(column.Name, column.ClrType);

        foreach (var row in rows)
        {
            // Defensive: a length mismatch would mean the parser and the descriptor
            // disagree, which is a bug that must not reach the server as silently
            // shifted columns.
            if (row.Values.Length != _table.Columns.Count)
                throw new InvalidOperationException(
                    $"Genscape {_table.TableName}: row has {row.Values.Length} value(s) but the descriptor " +
                    $"declares {_table.Columns.Count} column(s).");

            table.Rows.Add(row.Values);
        }

        return table;
    }
}
