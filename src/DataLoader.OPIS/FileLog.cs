using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>Natural-key identity of one source file, passed to the FileLog upsert.</summary>
public readonly record struct OpisFileContext(
    string FileName,
    DateOnly? SourceFileDate,
    DateTime? RemoteLastModifiedUtc,
    long? SizeBytes,
    string RequestPath);

/// <summary>
/// Upserts the <c>arm.FileLog</c> hub row for one file — for EVERY outcome
/// (Success / NotAvailable / Failed) — and returns its <c>FileLogId</c>, which
/// the reader stamps onto every row parsed from that file.
/// </summary>
public interface IOpisFileLog
{
    Task<int> UpsertAsync(
        OpisFileContext file,
        string status,
        int rowCount,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c>, reading the returned
/// scalar <c>FileLogId</c> with <c>ExecuteScalar</c>.
///
/// <para>
/// The call is serialized on the <see cref="SqlWriteGate"/> under a proc key
/// DISTINCT from the fact merge, so a parallel work unit's hub upsert can never
/// deadlock against another's <c>MERGE</c> — the CWG/Platts/StormVista posture.
/// </para>
/// </summary>
public sealed class SqlOpisFileLog : IOpisFileLog
{
    private readonly OpisSettings _settings;
    private readonly ILogger<SqlOpisFileLog> _logger;

    public SqlOpisFileLog(IOptions<OpisSettings> settings, ILogger<SqlOpisFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        OpisFileContext file, string status, int rowCount, string? errorMessage,
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
        cmd.Parameters.Add("@FileName", SqlDbType.VarChar, 200).Value = file.FileName;
        cmd.Parameters.Add("@SourceFileDate", SqlDbType.Date).Value =
            file.SourceFileDate.HasValue ? file.SourceFileDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@RemoteLastModifiedUtc", SqlDbType.DateTime2, 3).Value =
            (object?)file.RemoteLastModifiedUtc ?? DBNull.Value;
        cmd.Parameters.Add("@SizeBytes", SqlDbType.BigInt).Value = (object?)file.SizeBytes ?? DBNull.Value;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;
        cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, 400).Value =
            string.IsNullOrWhiteSpace(errorMessage) ? DBNull.Value : Truncate(errorMessage, 400);
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = Truncate(file.RequestPath, 400);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is int id) return id;

        _logger.LogError("OPIS: arm.usp_UpsertFileLog returned no FileLogId for {File}", file.FileName);
        throw new InvalidOperationException($"arm.usp_UpsertFileLog returned no FileLogId for '{file.FileName}'.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
