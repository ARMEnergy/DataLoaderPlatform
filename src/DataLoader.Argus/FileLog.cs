using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus;

/// <summary>Natural-key identity of one source file, passed to the FileLog upsert.</summary>
/// <param name="RequestPath">
/// <c>ftp://host:port/path</c>. NEVER credentials — this value is persisted and
/// read by humans.
/// </param>
public readonly record struct ArgusFileContext(
    string FileName,
    string FeedId,
    string RemoteFolder,
    DateOnly? SourceFileDate,
    DateTime? RemoteLastModifiedUtc,
    long? SizeBytes,
    string RequestPath);

/// <summary>
/// Upserts the <c>dlp.FileLog</c> hub row for one file — for EVERY outcome
/// (Success / NotAvailable / Failed) — and returns its id.
///
/// <para>
/// Unlike the OPIS loader, the returned id is not stamped onto fact rows: the
/// supplied fact DDL has no <c>FileLogId</c> column and one was not added.
/// Provenance runs the other way, through
/// <c>dlp.TimeSeriesDetail.SourcePath</c>, which contains the file name.
/// </para>
/// </summary>
public interface IArgusFileLog
{
    Task<int> UpsertAsync(
        ArgusFileContext file,
        string status,
        int rowCount,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>dlp.usp_UpsertFileLog</c>.
///
/// <para>
/// The call is serialized on the <see cref="SqlWriteGate"/> under a proc key
/// DISTINCT from every merge proc, so a parallel work unit's hub upsert can never
/// deadlock against another's <c>MERGE</c> — the OPIS/CWG/Platts posture.
/// </para>
/// </summary>
public sealed class SqlArgusFileLog : IArgusFileLog
{
    private readonly ArgusSettings _settings;
    private readonly ILogger<SqlArgusFileLog> _logger;

    public SqlArgusFileLog(IOptions<ArgusSettings> settings, ILogger<SqlArgusFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        ArgusFileContext file, string status, int rowCount, string? errorMessage,
        CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "dlp.usp_UpsertFileLog"), cancellationToken)
            .ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("dlp.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };

        // Explicit SqlDbType + size matching the proc's parameters, so no implicit
        // NVARCHAR -> VARCHAR conversion happens server-side.
        cmd.Parameters.Add("@FileName", SqlDbType.VarChar, 200).Value = file.FileName;
        cmd.Parameters.Add("@FeedId", SqlDbType.VarChar, 50).Value = NullIfBlank(file.FeedId);
        cmd.Parameters.Add("@RemoteFolder", SqlDbType.VarChar, 100).Value = NullIfBlank(file.RemoteFolder);
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

        _logger.LogError("Argus: dlp.usp_UpsertFileLog returned no FileLogId for {File}", file.FileName);
        throw new InvalidOperationException($"dlp.usp_UpsertFileLog returned no FileLogId for '{file.FileName}'.");
    }

    private static object NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
