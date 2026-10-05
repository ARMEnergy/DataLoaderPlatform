using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.OilX;

/// <summary>
/// The single write path for every OilX feed. One class, parameterised by the feed
/// descriptor, rather than eight near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A table-valued parameter binds BY POSITION, so the
/// DataTable's column order must match <c>sql/OilX/002_CreateOilXTvpTypes.sql</c>
/// exactly. Here that is structural rather than clerical: <see cref="BuildTable"/>
/// iterates <see cref="OilXFeedDescriptor.Columns"/>, and
/// <see cref="OilXSourceReader"/> filled <see cref="OilXRow.Values"/> from the same
/// list in the same order, so the two cannot drift from each other. What still COULD
/// drift is the descriptor versus the <c>.sql</c> file — and
/// <c>OilXTvpContractTests</c> parses the real <c>.sql</c> and asserts name + order +
/// type + nullability against the descriptor, so that fails the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every other
/// loader in the repo.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the proc
/// name, so the several day-units of ONE feed serialize on that feed's merge while the
/// other seven feeds proceed in parallel. That is what keeps eight concurrent bulk
/// merges from deadlocking against each other.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: the sink tests subclass it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract without
/// opening a connection.
/// </remarks>
internal class OilXTableSink : SqlSinkBase<OilXRow>
{
    private readonly OilXFeedDescriptor _feed;
    private readonly string _connectionString;

    public OilXTableSink(OilXFeedDescriptor feed, string connectionString, ILogger logger)
        : base(logger)
    {
        _feed = feed;
        _connectionString = connectionString;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _feed.MergeProc;
    protected override string TableValuedParameterType => _feed.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<OilXRow> rows)
    {
        var table = new DataTable();

        foreach (var column in _feed.Columns)
            table.Columns.Add(column.Name, column.ClrType);

        foreach (var row in rows)
        {
            // Defensive: a length mismatch means the reader and the descriptor disagree,
            // which is a bug that must not reach the server as silently shifted columns.
            if (row.Values.Length != _feed.Columns.Count)
                throw new InvalidOperationException(
                    $"OilX {_feed.FeedId}: row has {row.Values.Length} value(s) but the descriptor " +
                    $"declares {_feed.Columns.Count} column(s).");

            table.Rows.Add(row.Values);
        }

        return table;
    }
}
