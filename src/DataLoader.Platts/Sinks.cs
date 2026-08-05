using System.Data;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Sinks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Platts;

/// <summary>
/// SQL sink for the SymbolData feed. Bulk-merges into <c>arm.SymbolData</c> via a
/// TVP. The batch is de-duplicated on the merge key (keeping the latest
/// <c>ActionDate</c>) before the TVP is built, so an accidental in-file duplicate
/// key cannot break the merge.
/// </summary>
public sealed class SymbolDataSqlSink : SqlSinkBase<SymbolDataRow>
{
    private readonly PlattsSettings _settings;

    public SymbolDataSqlSink(IOptions<PlattsSettings> settings, ILogger<SymbolDataSqlSink> logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "arm.usp_BulkMergeSymbolData";
    protected override string TableValuedParameterType => "arm.SymbolDataTvp";
    protected override bool ProcedureReturnsRowCount => true;

    protected override DataTable BuildTable(IReadOnlyList<SymbolDataRow> rows)
    {
        // De-dup by (Symbol, Bate, Date, Action), keeping the row with the max
        // ActionDate. Within a single file ActionDate is constant, so keep-last
        // is equivalent, but max-ActionDate is correct regardless.
        var deduped = rows
            .GroupBy(r => (r.Symbol, r.Bate, r.Date, r.Action))
            .Select(g => g.OrderByDescending(r => r.ActionDate).First())
            .ToList();

        // Column ORDER must match arm.SymbolDataTvp exactly.
        var t = new DataTable();
        t.Columns.Add("MDC", typeof(string));
        t.Columns.Add("Symbol", typeof(string));
        t.Columns.Add("Bate", typeof(string));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Action", typeof(string));
        t.Columns.Add("Value", typeof(decimal));
        t.Columns.Add("ActionDate", typeof(DateTime));
        t.Columns.Add("SourcePath", typeof(string));

        foreach (var r in deduped)
            t.Rows.Add(
                NullIfEmpty(r.MDC), NullIfEmpty(r.Symbol), NullIfEmpty(r.Bate), r.Date,
                NullIfEmpty(r.Action), DbNullable(r.Value), r.ActionDate, NullIfEmpty(r.SourcePath));

        return t;
    }
}

/// <summary>
/// SQL sink for the Symbol reference feed. Bulk-merges into <c>arm.Symbol</c>
/// (keyed by Symbol) via a TVP. The carry fields (SourcePath, FileName, …) are
/// not written here; they feed the audit log through the decorator.
/// </summary>
public sealed class SymbolSqlSink : SqlSinkBase<SymbolRow>
{
    private readonly PlattsSettings _settings;

    public SymbolSqlSink(IOptions<PlattsSettings> settings, ILogger<SymbolSqlSink> logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override string StoredProcedureName => "arm.usp_BulkMergeSymbol";
    protected override string TableValuedParameterType => "arm.SymbolTvp";
    protected override bool ProcedureReturnsRowCount => true;

    protected override DataTable BuildTable(IReadOnlyList<SymbolRow> rows)
    {
        // De-dup by Symbol (the merge key) keeping the last occurrence, so two
        // rows for the same Symbol in one CSV can't trigger a "same row more than
        // once" MERGE error or a PK violation.
        var deduped = rows
            .GroupBy(r => r.Symbol)
            .Select(g => g.Last())
            .ToList();

        // Column ORDER must match arm.SymbolTvp exactly.
        var t = new DataTable();
        t.Columns.Add("MDC", typeof(string));
        t.Columns.Add("Trans", typeof(string));
        t.Columns.Add("Symbol", typeof(string));
        t.Columns.Add("Bates", typeof(string));
        t.Columns.Add("Freq", typeof(string));
        t.Columns.Add("Curr", typeof(string));
        t.Columns.Add("UOM", typeof(string));
        t.Columns.Add("DEC", typeof(int));
        t.Columns.Add("Conv", typeof(decimal));
        t.Columns.Add("Flag", typeof(string));
        t.Columns.Add("To_UOM", typeof(string));
        t.Columns.Add("Earliest", typeof(DateTime));
        t.Columns.Add("Latest", typeof(DateTime));
        t.Columns.Add("Description", typeof(string));

        foreach (var r in deduped)
            t.Rows.Add(
                NullIfEmpty(r.MDC), NullIfEmpty(r.Trans), NullIfEmpty(r.Symbol), NullIfEmpty(r.Bates),
                NullIfEmpty(r.Freq), NullIfEmpty(r.Curr), NullIfEmpty(r.UOM), DbNullable(r.Dec),
                DbNullable(r.Conv), NullIfEmpty(r.Flag), NullIfEmpty(r.ToUom), DbNullable(r.Earliest),
                DbNullable(r.Latest), NullIfEmpty(r.Description));

        return t;
    }
}

/// <summary>Writes one per-file audit row to <c>arm.FileLog</c> (upsert by Feed + SourcePath).</summary>
public interface IPlattsFileLog
{
    Task UpsertAsync(
        string feed, string sourcePath, string fileName, DateTime lastModifiedUtc,
        long? sizeBytes, int rowCount, string status, CancellationToken ct);
}

/// <summary>SQL implementation of <see cref="IPlattsFileLog"/>, calling <c>arm.usp_UpsertFileLog</c>.</summary>
public sealed class PlattsFileLogWriter : IPlattsFileLog
{
    private readonly PlattsSettings _settings;
    private readonly ILogger<PlattsFileLogWriter> _logger;

    public PlattsFileLogWriter(IOptions<PlattsSettings> settings, ILogger<PlattsFileLogWriter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task UpsertAsync(
        string feed, string sourcePath, string fileName, DateTime lastModifiedUtc,
        long? sizeBytes, int rowCount, string status, CancellationToken ct)
    {
        // Serialize concurrent FileLog upserts against the same target so
        // parallel work units cannot deadlock on this proc.
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), ct).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@Feed", feed);
        cmd.Parameters.AddWithValue("@SourcePath", sourcePath);
        cmd.Parameters.AddWithValue("@FileName", fileName);
        // Set SqlDbType explicitly so the DATETIME2(3) column isn't fed via an
        // implicit DateTime→DateTime2 conversion.
        cmd.Parameters.Add("@LastModifiedUtc", SqlDbType.DateTime2).Value = lastModifiedUtc;
        cmd.Parameters.AddWithValue("@SizeBytes", (object?)sizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RowCount", rowCount);
        cmd.Parameters.AddWithValue("@Status", status);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("Platts FileLog upsert: feed {Feed}, {Path}, {Rows} rows, {Status}",
            feed, sourcePath, rowCount, status);
    }
}

/// <summary>
/// Sink decorator that writes an <c>arm.FileLog</c> audit row after the inner
/// data merge. <see cref="SqlSinkBase{TRow}.WriteAsync"/> is not virtual (and
/// Core must not change), so the audit is layered on via decoration rather than
/// an override. The audit records the file's own row count; the inner merge's
/// count is returned to the pipeline.
/// </summary>
public sealed class FileLoggingSink<TRow> : ISink<TRow>
{
    private readonly ISink<TRow> _inner;
    private readonly IPlattsFileLog _fileLog;
    private readonly string _feedId;
    private readonly Func<TRow, (string SourcePath, string FileName, DateTime LastModifiedUtc, long? SizeBytes)> _meta;
    private readonly ILogger _logger;

    public FileLoggingSink(
        ISink<TRow> inner,
        IPlattsFileLog fileLog,
        string feedId,
        Func<TRow, (string SourcePath, string FileName, DateTime LastModifiedUtc, long? SizeBytes)> meta,
        ILogger logger)
    {
        _inner = inner;
        _fileLog = fileLog;
        _feedId = feedId;
        _meta = meta;
        _logger = logger;
    }

    public async Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        int count;
        try
        {
            count = await _inner.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort failure audit; never mask the original exception. Use
            // CancellationToken.None so the "Failed" row is still written even when
            // the failure was a timeout/cancellation (same as LoaderPipelineBase).
            if (rows.Count > 0)
                await TryUpsertAsync(rows, "Failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (rows.Count > 0)
            await UpsertAsync(rows, "Success", cancellationToken).ConfigureAwait(false);

        return count;
    }

    private Task UpsertAsync(IReadOnlyList<TRow> rows, string status, CancellationToken ct)
    {
        var m = _meta(rows[0]);
        return _fileLog.UpsertAsync(_feedId, m.SourcePath, m.FileName, m.LastModifiedUtc, m.SizeBytes, rows.Count, status, ct);
    }

    private async Task TryUpsertAsync(IReadOnlyList<TRow> rows, string status, CancellationToken ct)
    {
        try
        {
            await UpsertAsync(rows, status, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Platts FileLog: failed to record '{Status}' audit for feed {Feed}", status, _feedId);
        }
    }
}
