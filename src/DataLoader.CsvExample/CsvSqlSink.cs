using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CsvExample;

/// <summary>
/// Writes CSV rows to <c>csv.RawRows</c> in this loader's own database.
/// Uses the platform's <see cref="SqlSinkBase{TRow}"/> so the connection,
/// TVP, error handling, and bulk-merge plumbing are inherited.
///
/// In a real loader, you'd write to a strongly-typed table — e.g.
/// <c>prices.DailyClose(CommodityCode, TradeDate, ClosePrice)</c> — and
/// the BuildTable override would map specific cells to specific columns.
/// </summary>
public sealed class CsvSqlSink : SqlSinkBase<CsvRow>
{
    private readonly CsvExampleSettings _settings;

    public CsvSqlSink(IOptions<CsvExampleSettings> settings, ILogger<CsvSqlSink> logger)
        : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "csv.usp_BulkInsertRawRows";
    protected override string TableValuedParameterType => "csv.RawRowTvp";
    protected override bool ProcedureReturnsRowCount => true;

    protected override DataTable BuildTable(IReadOnlyList<CsvRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SourceFile", typeof(string));
        t.Columns.Add("RowNumber", typeof(int));
        t.Columns.Add("RawLine", typeof(string));

        foreach (var r in rows)
        {
            // Stash the whole row as a comma-joined string. Real loaders
            // would split cells out into typed columns here.
            var rawLine = string.Join(",", r.Cells);
            t.Rows.Add(NullIfEmpty(r.SourceFile), r.RowNumber, rawLine);
        }
        return t;
    }
}
