using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.ICE;

/// <summary>
/// The single write path for every ICE feed. One class, parameterised by the TABLE
/// descriptor, rather than 12 near-identical sinks.
///
/// <para>
/// <b>The TVP contract.</b> A TVP binds BY POSITION, so the DataTable's column
/// order must match <c>sql/ICE/002_CreateIceTvpTypes.sql</c> exactly. Here that is
/// structural rather than clerical: <see cref="BuildTable"/> iterates
/// <see cref="IceTableDescriptor.Columns"/>, and the reader filled
/// <see cref="IceRow.Values"/> from the same list in the same order, so the two
/// sides cannot drift from each other. What still COULD drift is the descriptor
/// versus the .sql file — and <c>IceTvpContractTests</c> parses the real .sql and
/// asserts name + order + type against the descriptor, so that fails the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is DB-stamped and is never a TVP column, matching every
/// other loader in the repo.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> acquisition keys on the
/// proc name. That matters more here than usual: the six feeds writing
/// <c>arm.Futures</c> all call <c>arm.usp_BulkMergeFutures</c>, so they serialize
/// against each other while the other tables' feeds proceed in parallel.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: <c>SinkTests</c> subclasses it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the TVP contract
/// without opening a connection.
/// </remarks>
public class IceTableSink : SqlSinkBase<IceRow>
{
    private readonly IceTableDescriptor _table;
    private readonly string _connectionString;

    public IceTableSink(IceTableDescriptor table, string connectionString, ILogger logger)
        : base(logger)
    {
        _table = table;
        _connectionString = connectionString;
    }

    protected override string GetConnectionString() => _connectionString;
    protected override string StoredProcedureName => _table.MergeProc;
    protected override string TableValuedParameterType => _table.TvpType;

    protected override DataTable BuildTable(IReadOnlyList<IceRow> rows)
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
                    $"ICE {_table.TableName}: row has {row.Values.Length} value(s) but the descriptor " +
                    $"declares {_table.Columns.Count} column(s).");

            table.Rows.Add(row.Values);
        }

        return table;
    }
}
