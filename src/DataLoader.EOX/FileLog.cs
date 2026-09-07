using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EOX;

/// <summary>Natural-key identity of one source file, passed to the FileLog upsert.</summary>
/// <param name="RequestPath">
/// <c>ftp://host:port/path</c>. NEVER credentials — this value is persisted and
/// read by humans.
/// </param>
public readonly record struct EoxFileContext(
    string FileName,
    string FeedId,
    DateOnly? CurveDate,
    DateTime? RemoteLastModifiedUtc,
    long? SizeBytes,
    string RequestPath);

/// <summary>
/// Upserts the <c>arm.FileLog</c> hub row for one file — for EVERY outcome
/// (Success / NotAvailable / Failed) — and returns its id.
///
/// <para>
/// The returned id is not stamped onto fact rows: the requester's DDL gives the
/// three fact tables a <c>FileName</c> column rather than a <c>FileLogId</c> FK,
/// so provenance runs through the name. That name is also the merge ordering
/// guard, which is why it is <c>NOT NULL</c> in every TVP.
/// </para>
/// </summary>
public interface IEoxFileLog
{
    Task<int> UpsertAsync(
        EoxFileContext file,
        string status,
        int rowCount,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c>, reading the returned
/// scalar id with <c>ExecuteScalar</c>.
///
/// <para>
/// The call is serialized on the <see cref="SqlWriteGate"/> under a proc key
/// DISTINCT from every merge proc, so a parallel work unit's hub upsert can never
/// deadlock against another's <c>MERGE</c> — the OPIS/Argus/CWG/Platts posture.
/// </para>
/// </summary>
public sealed class SqlEoxFileLog : IEoxFileLog
{
    private readonly EoxSettings _settings;
    private readonly ILogger<SqlEoxFileLog> _logger;

    public SqlEoxFileLog(IOptions<EoxSettings> settings, ILogger<SqlEoxFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        EoxFileContext file, string status, int rowCount, string? errorMessage,
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
        cmd.Parameters.Add("@FileName", SqlDbType.VarChar, 128).Value = file.FileName;
        cmd.Parameters.Add("@FeedId", SqlDbType.VarChar, 20).Value = file.FeedId;
        cmd.Parameters.Add("@CurveDate", SqlDbType.Date).Value =
            file.CurveDate.HasValue ? file.CurveDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
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

        _logger.LogError("EOX: arm.usp_UpsertFileLog returned no FileLogId for {File}", file.FileName);
        throw new InvalidOperationException($"arm.usp_UpsertFileLog returned no FileLogId for '{file.FileName}'.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
