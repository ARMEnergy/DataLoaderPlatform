using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.Argus;

/// <summary>
/// The single write path for every Argus feed. One class, parameterised by the
/// feed descriptor, rather than 16 near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A TVP binds BY POSITION, so the DataTable's column
/// order must match <c>sql/Argus/002_CreateArgusTvpTypes.sql</c> exactly. Here
/// that is structural rather than clerical: <see cref="BuildTable"/> iterates
/// <see cref="ArgusFeedDescriptor.Columns"/>, and the reader filled
/// <see cref="ArgusRow.Values"/> from the same list in the same order, so the two
/// sides cannot drift from each other. What still COULD drift is the descriptor
/// versus the .sql file — and <c>ArgusTvpContractTests</c> parses the real .sql
/// and asserts name + order + type against the descriptor, so that fails the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every
/// other loader in the repo.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the
/// proc name, so two work units hitting the same proc serialize while different
/// feeds proceed in parallel, and none of them contends with
/// <c>dlp.usp_UpsertFileLog</c>.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: <c>SinkTests</c> subclasses it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract
/// without opening a connection.
/// </remarks>
public class ArgusTableSink : SqlSinkBase<ArgusRow>
{
    private readonly ArgusFeedDescriptor _feed;
    private readonly string _connectionString;

    public ArgusTableSink(ArgusFeedDescriptor feed, string connectionString, ILogger logger)
        : base(logger)
    {
        _feed = feed;
        _connectionString = connectionString;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _feed.MergeProc;
    protected override string TableValuedParameterType => _feed.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<ArgusRow> rows)
    {
        var table = new DataTable();

        foreach (var column in _feed.Columns)
            table.Columns.Add(column.Name, column.ClrType);

        foreach (var row in rows)
        {
            // Defensive: a length mismatch would mean the reader and the descriptor
            // disagree, which is a bug that must not reach the server as silently
            // shifted columns.
            if (row.Values.Length != _feed.Columns.Count)
                throw new InvalidOperationException(
                    $"Argus {_feed.FeedId}: row has {row.Values.Length} value(s) but the descriptor " +
                    $"declares {_feed.Columns.Count} column(s).");

            table.Rows.Add(row.Values);
        }

        return table;
    }
}
