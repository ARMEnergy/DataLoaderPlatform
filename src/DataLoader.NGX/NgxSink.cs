using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGX;

/// <summary>
/// The TVP write path for both NGX feeds. One class, parameterised by the TABLE
/// descriptor, rather than two near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A TVP binds BY POSITION, so the DataTable's column order
/// must match <c>sql/NGX/002</c> exactly. Here that is structural rather than clerical:
/// <see cref="BuildTable"/> iterates <see cref="NgxTableDescriptor.Columns"/>, and both
/// readers filled <see cref="NgxRow.Values"/> from the same list in the same order, so
/// the two cannot drift from each other. What still COULD drift is the descriptor
/// versus the .sql file — and <c>NgxTvpContractTests</c> parses the real .sql and
/// asserts name + order + type against the descriptor, so that fails the build.
/// </para>
/// <para>
/// <b>Strings are truncated, not rejected.</b> <c>HubName</c> is VARCHAR(50) while the
/// sibling <c>MarketName</c> is VARCHAR(256), and <c>IndexName</c> is VARCHAR(500)
/// against observed names already 52 characters long. If the vendor ever exceeds a
/// width, SqlClient aborts the ENTIRE batch with "String or binary data would be
/// truncated" — losing a whole work unit's rows over one long label. Truncating to the
/// declared width and warning once per batch keeps the load going and makes the problem
/// visible.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every other
/// loader in the repo.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the proc
/// name. Each feed has its own proc, so the two pipelines never serialize against each
/// other — but two work units of the SAME feed running concurrently do, which is what
/// keeps parallel merges into one table off each other's locks.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: <c>NgxSinkTests</c> subclasses it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract without
/// opening a connection.
/// </remarks>
internal class NgxTvpSink : SqlSinkBase<NgxRow>
{
    private readonly NgxTableDescriptor _table;
    private readonly string _connectionString;
    private readonly ILogger _logger;

    internal NgxTvpSink(NgxTableDescriptor table, string connectionString, ILogger logger)
        : base(logger)
    {
        _table = table;
        _connectionString = connectionString;
        _logger = logger;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _table.MergeProc;
    protected override string TableValuedParameterType => _table.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<NgxRow> rows)
    {
        var table = new DataTable();

        foreach (var column in _table.Columns)
            table.Columns.Add(column.Name, column.ClrType);

        var truncated = 0;
        var droppedForKeyOverflow = 0;
        string? firstTruncatedColumn = null;
        string? firstOverflowedKeyColumn = null;

        foreach (var row in rows)
        {
            // Defensive: a length mismatch would mean a reader and the descriptor
            // disagree, which is a bug that must not reach the server as silently
            // shifted columns.
            if (row.Values.Length != _table.Columns.Count)
                throw new InvalidOperationException(
                    $"NGX {_table.TableName}: row has {row.Values.Length} value(s) but the descriptor " +
                    $"declares {_table.Columns.Count} column(s).");

            var values = row.Values;
            var keyOverflowed = false;

            for (var i = 0; i < _table.Columns.Count; i++)
            {
                var column = _table.Columns[i];
                var max = column.MaxLength;
                if (max is null) continue;
                if (values[i] is not string s || s.Length <= max.Value) continue;

                // ⚠ A PRIMARY KEY column is never truncated. Clipping a key does not
                // fail — it merges the row into a DIFFERENT one via the merge predicate,
                // which is silent corruption rather than a clipped label. Drop the row
                // instead and say so loudly.
                if (column.IsKey)
                {
                    keyOverflowed = true;
                    droppedForKeyOverflow++;
                    firstOverflowedKeyColumn ??= column.Name;
                    break;
                }

                // Copy on first write so the caller's array is not mutated — the rows
                // are also what a retry would re-send.
                if (ReferenceEquals(values, row.Values))
                    values = (object[])row.Values.Clone();

                values[i] = s[..max.Value];
                truncated++;
                firstTruncatedColumn ??= column.Name;
            }

            if (keyOverflowed) continue;

            table.Rows.Add(values);
        }

        if (droppedForKeyOverflow > 0)
            _logger.LogError(
                "NGX {Table}: DROPPED {Count} row(s) whose primary key value exceeded its declared width " +
                "(first was {Column}). Truncating a key would have merged the row into a different one, so " +
                "it is discarded instead. This is a source or schema mismatch and needs investigating",
                _table.TableName, droppedForKeyOverflow, firstOverflowedKeyColumn);

        if (truncated > 0)
            _logger.LogWarning(
                "NGX {Table}: truncated {Count} oversized string value(s) to their declared width " +
                "(first was {Column}). The source has outgrown the column — widen it in sql/NGX/001 " +
                "and 002 rather than leaving values clipped",
                _table.TableName, truncated, firstTruncatedColumn);

        return table;
    }
}
