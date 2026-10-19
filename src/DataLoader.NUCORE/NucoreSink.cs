using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.NUCORE;

/// <summary>
/// The single write path for every NUCORE endpoint. One class parameterised by the
/// descriptor, rather than five near-identical sinks over 436 columns.
///
/// <para>
/// <b>The TVP contract.</b> A table-valued parameter binds BY POSITION, so the DataTable's
/// column order must match <c>sql/NUCORE/002</c> exactly. Here that is structural rather
/// than clerical: <see cref="BuildTable"/> iterates <see cref="NucoreEndpoint.Columns"/>,
/// and <see cref="NucoreRowBuilder"/> filled <see cref="NucoreRow.Values"/> from the same
/// list in the same order, so those two cannot drift. What COULD drift is the descriptor
/// against the <c>.sql</c> - and both are emitted by the same generator, with
/// <c>NucoreTvpContractTests</c> parsing the real <c>.sql</c> and asserting name + order +
/// type + nullability, so a one-sided edit fails the build.
/// </para>
/// <para>
/// <c>FileLogId</c> and <c>ModifiedAtUtc</c> are set by the procedure and are never TVP
/// columns, matching every other loader in the repo.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> keys on the proc name, so the
/// several units of ONE endpoint serialise on that endpoint's merge while the other four
/// endpoints proceed in parallel. That is what keeps five concurrent bulk merges from
/// deadlocking against each other.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: the sink tests subclass it to reach the protected <see cref="BuildTable"/>
/// and assert the DataTable half of the contract without opening a connection.
/// </remarks>
internal class NucoreTableSink : SqlSinkBase<NucoreRow>
{
    private readonly NucoreEndpoint _endpoint;
    private readonly string _connectionString;

    public NucoreTableSink(NucoreEndpoint endpoint, string connectionString, ILogger logger)
        : base(logger)
    {
        _endpoint = endpoint;
        _connectionString = connectionString;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _endpoint.MergeProc;
    protected override string TableValuedParameterType => _endpoint.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<NucoreRow> rows)
    {
        var table = new DataTable();

        foreach (var column in _endpoint.Columns)
            table.Columns.Add(column.Name, column.ClrType);

        foreach (var row in rows)
        {
            // Defensive: a length mismatch means the row builder and the descriptor
            // disagree, which is a bug that must not reach the server as silently shifted
            // columns.
            if (row.Values.Length != _endpoint.Columns.Count)
                throw new InvalidOperationException(
                    $"NUCORE {_endpoint.Name}: row has {row.Values.Length} value(s) but the " +
                    $"descriptor declares {_endpoint.Columns.Count} column(s).");

            table.Rows.Add(row.Values);
        }

        return table;
    }
}
