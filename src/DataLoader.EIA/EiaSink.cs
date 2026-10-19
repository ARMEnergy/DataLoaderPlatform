using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// The write path into <c>arm.SeriesData</c>.
///
/// <para>
/// <b>The TVP contract.</b> A table-valued parameter binds BY POSITION, so
/// <see cref="BuildTable"/>'s column order must match
/// <c>sql/EIA/002_CreateEiaTvpTypes.sql</c> exactly. A silent reorder would write
/// FacetJSON into DataJSON on every row without raising an error.
/// <c>tests/DataLoader.EIA.Tests/EiaTvpContractTests.cs</c> parses the real
/// <c>.sql</c> file and asserts name + order + type + nullability against this
/// DataTable, so the two cannot drift apart without failing the build.
/// </para>
/// <para>
/// <c>ModifiedAtUtc</c> is stamped by the merge proc with <c>SYSUTCDATETIME()</c> and
/// is never a TVP column — the table's own DEFAULT is <c>sysdatetime()</c>, which is
/// LOCAL time despite the column name, so leaving it to the default would be wrong.
/// </para>
/// <para>
/// The base's <see cref="Core.Concurrency.SqlWriteGate"/> keys on the proc name, so
/// every concurrent work unit serialises on the one merge. That is intentional: all
/// units write the same table, and parallel MERGEs against one target is how this repo
/// has produced deadlocks before. The parallelism that matters here is in the HTTP
/// fetch, which dominates by orders of magnitude.
/// </para>
/// </summary>
/// <remarks>
/// Not sealed: the sink tests subclass it to reach the protected
/// <see cref="BuildTable"/> and assert the DataTable half of the contract without
/// opening a connection.
/// </remarks>
internal class EiaSink : SqlSinkBase<EiaRow>
{
    private readonly EiaSettings _settings;

    public EiaSink(IOptions<EiaSettings> settings, ILogger<EiaSink> logger)
        : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "arm.usp_BulkMergeSeriesData";
    protected override string TableValuedParameterType => "arm.SeriesDataTvp";

    /// <summary>
    /// Column order is the contract. Keep it identical to <c>arm.SeriesDataTvp</c>.
    /// </summary>
    protected override DataTable BuildTable(IReadOnlyList<EiaRow> rows)
    {
        var table = new DataTable();
        table.Columns.Add("SeriesId",    typeof(string));
        table.Columns.Add("FrequencyId", typeof(string));
        table.Columns.Add("Period",      typeof(string));
        table.Columns.Add("FacetKey",    typeof(int));
        table.Columns.Add("FacetJSON",   typeof(string));
        table.Columns.Add("DataJSON",    typeof(string));
        table.Columns.Add("DataHash",    typeof(int));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.SeriesId,
                row.FrequencyId,
                row.Period,
                row.FacetKey,
                row.FacetJson,
                row.DataJson,
                row.DataHash);
        }

        return table;
    }
}
