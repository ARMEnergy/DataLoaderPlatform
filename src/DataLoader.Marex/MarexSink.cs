using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.Marex;

/// <summary>
/// The TVP write path for all five Marex feeds. One class parameterised by the TABLE
/// descriptor, rather than five near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A TVP binds BY POSITION, so the DataTable's column order must
/// match <c>sql/Marex/002</c> exactly. Here that is structural rather than clerical:
/// <see cref="BuildTable"/> iterates <see cref="MarexTableDescriptor.Columns"/>, and
/// <see cref="MarexMapper"/> filled <see cref="MarexRow.Values"/> from the same list in
/// the same order, so the two cannot drift from each other. What still COULD drift is the
/// descriptor versus the .sql file — and <c>MarexTvpContractTests</c> parses the real
/// .sql and asserts name + order + type against the descriptor, so that fails the build.
/// </para>
/// <para>
/// <b>Strings are truncated, not rejected.</b> If the vendor ever exceeds a declared
/// width, SqlClient aborts the ENTIRE batch with "String or binary data would be
/// truncated" — losing a whole feed's rows over one long label. Truncating to the
/// declared width and warning once per batch keeps the load going and makes the problem
/// visible. The live margins are comfortable today (the widest observed <c>Product.Name</c>
/// is 45 of 256; the widest <c>MarketStatistic.Product</c> is 35 of 50), so a warning
/// here means the source really has changed.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every other
/// loader in the repo.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the proc
/// name, and each feed has its own proc, so the five pipelines never serialize against
/// one another.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: <c>MarexSinkTests</c> subclasses it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract without
/// opening a connection.
/// </remarks>
internal class MarexTvpSink : SqlSinkBase<MarexRow>
{
    private readonly MarexTableDescriptor _table;
    private readonly string _connectionString;
    private readonly ILogger _logger;

    internal MarexTvpSink(MarexTableDescriptor table, string connectionString, ILogger logger)
        : base(logger)
    {
        _table = table;
        _connectionString = connectionString;
        _logger = logger;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _table.MergeProc;
    protected override string TableValuedParameterType => _table.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<MarexRow> rows)
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
            // Defensive: a length mismatch means the mapper and the descriptor disagree,
            // which is a bug that must not reach the server as silently shifted columns.
            if (row.Values.Length != _table.Columns.Count)
                throw new InvalidOperationException(
                    $"Marex {_table.TableName}: row has {row.Values.Length} value(s) but the descriptor " +
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

                // Copy on first write so the caller's array is not mutated — the rows are
                // also what a retry would re-send.
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
                "Marex {Table}: DROPPED {Count} row(s) whose primary key value exceeded its declared width " +
                "(first was {Column}). Truncating a key would have merged the row into a different one, so " +
                "it is discarded instead. This is a source or schema mismatch and needs investigating",
                _table.TableName, droppedForKeyOverflow, firstOverflowedKeyColumn);

        if (truncated > 0)
            _logger.LogWarning(
                "Marex {Table}: truncated {Count} oversized string value(s) to their declared width " +
                "(first was {Column}). The source has outgrown the column — widen it in sql/Marex/001 " +
                "and 002 rather than leaving values clipped",
                _table.TableName, truncated, firstTruncatedColumn);

        return table;
    }
}
