using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CME;

/// <summary>Natural-key identity of one bulletin, passed to the FileLog upsert.</summary>
/// <param name="RequestPath">
/// <c>sftp://host:port/path</c>. NEVER credentials — this value is persisted and
/// read by humans.
/// </param>
public readonly record struct CmeFileContext(
    string FileName,
    string FeedId,
    string ExchangeCode,
    string ProductCode,
    DateOnly? TradeDate,
    DateTime? RemoteLastModifiedUtc,
    long? SizeBytes,
    string RequestPath);

/// <summary>
/// Upserts the <c>arm.FileLog</c> hub row for one bulletin — for EVERY outcome
/// (Success / Failed) — and returns its id.
///
/// <para>
/// The returned id is not stamped onto fact rows: the requester's DDL gives the
/// two fact tables no provenance column at all, so the audit trail lives entirely
/// in this hub, keyed by file name. That makes the hub the ONLY place a
/// half-parsed or skipped bulletin is visible, which is why the parse counters
/// (rows, skipped BALMO rows, unclassified lines) are written here rather than
/// only logged.
/// </para>
/// </summary>
public interface ICmeFileLog
{
    Task<int> UpsertAsync(
        CmeFileContext file,
        string status,
        int rowCount,
        CmeParseStats? stats,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c>, reading the returned
/// scalar id with <c>ExecuteScalar</c>.
///
/// <para>
/// The call is serialized on the <see cref="SqlWriteGate"/> under a proc key
/// DISTINCT from both merge procs, so a parallel work unit's hub upsert can never
/// deadlock against another's <c>MERGE</c>.
/// </para>
/// </summary>
public sealed class SqlCmeFileLog : ICmeFileLog
{
    private readonly CmeSettings _settings;
    private readonly ILogger<SqlCmeFileLog> _logger;

    public SqlCmeFileLog(IOptions<CmeSettings> settings, ILogger<SqlCmeFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        CmeFileContext file, string status, int rowCount, CmeParseStats? stats, string? errorMessage,
        CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
                SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), cancellationToken)
            .ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        // Explicit SqlDbType + size matching the proc's parameters, so no implicit
        // NVARCHAR -> VARCHAR conversion happens server-side.
        cmd.Parameters.Add("@FileName", SqlDbType.VarChar, 128).Value = file.FileName;
        cmd.Parameters.Add("@FeedId", SqlDbType.VarChar, 50).Value = file.FeedId;
        cmd.Parameters.Add("@ExchangeCode", SqlDbType.VarChar, 50).Value = file.ExchangeCode;
        cmd.Parameters.Add("@ProductCode", SqlDbType.VarChar, 50).Value = file.ProductCode;
        cmd.Parameters.Add("@TradeDate", SqlDbType.Date).Value =
            file.TradeDate.HasValue ? file.TradeDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@RemoteLastModifiedUtc", SqlDbType.DateTime2, 3).Value =
            (object?)file.RemoteLastModifiedUtc ?? DBNull.Value;
        cmd.Parameters.Add("@SizeBytes", SqlDbType.BigInt).Value = (object?)file.SizeBytes ?? DBNull.Value;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;
        cmd.Parameters.Add("@OptionRowCount", SqlDbType.Int).Value = stats?.OptionRows ?? 0;
        cmd.Parameters.Add("@FutureRowCount", SqlDbType.Int).Value = stats?.FutureRows ?? 0;
        cmd.Parameters.Add("@DayLabelRowsSkipped", SqlDbType.Int).Value = stats?.DayLabelRowsSkipped ?? 0;
        cmd.Parameters.Add("@UnclassifiedLines", SqlDbType.Int).Value = stats?.UnclassifiedLines ?? 0;
        cmd.Parameters.Add("@ReportHeader", SqlDbType.VarChar, 200).Value =
            string.IsNullOrWhiteSpace(stats?.ReportHeader) ? DBNull.Value : Truncate(stats!.ReportHeader!, 200);
        cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, 400).Value =
            string.IsNullOrWhiteSpace(errorMessage) ? DBNull.Value : Truncate(errorMessage, 400);
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = Truncate(file.RequestPath, 400);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is int id) return id;

        _logger.LogError("CME: arm.usp_UpsertFileLog returned no FileLogId for {File}", file.FileName);
        throw new InvalidOperationException($"arm.usp_UpsertFileLog returned no FileLogId for '{file.FileName}'.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
