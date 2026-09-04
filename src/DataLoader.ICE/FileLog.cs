using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ICE;

/// <summary>Natural-key identity of one source file, passed to the FileLog upsert.</summary>
/// <param name="RequestPath">
/// The https URL. <b>NEVER the SSO token</b> — this value is persisted and read by
/// humans. The token travels in a Cookie header and is never part of the URL.
/// </param>
public readonly record struct IceFileContext(
    string FeedId,
    DateOnly TradeDate,
    string FileName,
    string? TargetTable,
    long? SizeBytes,
    string RequestPath);

/// <summary>
/// Upserts the <c>arm.FileLog</c> hub row for one (feed, trade date) — for EVERY
/// outcome, including the non-error <c>NotAvailable</c> that a weekend or holiday
/// produces.
///
/// <para>
/// <c>rowsDropped</c> is carried separately from <c>rowCount</c> because every
/// options feed legitimately discards its underlying-future rows (blank STRIKE
/// against a NOT NULL PK column) — between 1.3 % and 25.9 % of a file. Recording
/// the count is what lets <c>arm.usp_ValidateLoad</c> distinguish "the normal shape
/// of this feed" from "the parser just broke".
/// </para>
/// </summary>
public interface IIceFileLog
{
    Task<int> UpsertAsync(
        IceFileContext file,
        string status,
        int rowCount,
        int rowsDropped,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c>.
///
/// <para>
/// The call is serialized on the <see cref="SqlWriteGate"/> under a proc key
/// DISTINCT from every merge proc, so a parallel work unit's hub upsert can never
/// deadlock against another's <c>MERGE</c> — the OPIS/Argus/Platts posture.
/// </para>
/// </summary>
public sealed class SqlIceFileLog : IIceFileLog
{
    private readonly IceSettings _settings;
    private readonly ILogger<SqlIceFileLog> _logger;

    public SqlIceFileLog(IOptions<IceSettings> settings, ILogger<SqlIceFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        IceFileContext file, string status, int rowCount, int rowsDropped, string? errorMessage,
        CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), cancellationToken)
            .ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };

        // Explicit SqlDbType + size matching the proc's parameters, so no implicit
        // NVARCHAR -> VARCHAR conversion happens server-side.
        cmd.Parameters.Add("@FeedId", SqlDbType.VarChar, 50).Value = file.FeedId;
        cmd.Parameters.Add("@TradeDate", SqlDbType.Date).Value = file.TradeDate.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@FileName", SqlDbType.VarChar, 200).Value = Truncate(file.FileName, 200);
        cmd.Parameters.Add("@TargetTable", SqlDbType.VarChar, 128).Value = NullIfBlank(file.TargetTable);
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@SizeBytes", SqlDbType.BigInt).Value = (object?)file.SizeBytes ?? DBNull.Value;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;
        cmd.Parameters.Add("@RowsDropped", SqlDbType.Int).Value = rowsDropped;
        cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, 400).Value =
            string.IsNullOrWhiteSpace(errorMessage) ? DBNull.Value : Truncate(errorMessage, 400);
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = Truncate(file.RequestPath, 400);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is int id) return id;

        _logger.LogError("ICE: arm.usp_UpsertFileLog returned no FileLogId for {Feed}/{Date}",
            file.FeedId, file.TradeDate);
        throw new InvalidOperationException(
            $"arm.usp_UpsertFileLog returned no FileLogId for '{file.FeedId}' / {file.TradeDate:yyyy-MM-dd}.");
    }

    private static object NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
