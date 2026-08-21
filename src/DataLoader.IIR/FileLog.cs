using System.Data;
using System.Globalization;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IIR;

/// <summary>
/// The natural-key identity of one endpoint pull/outcome, passed to the FileLog upsert (design §8).
/// <see cref="RepresentativeDate"/> is NULL-able; SQL NULL-equality in <c>arm.usp_UpsertFileLog</c>
/// collapses an undated request to a single stable hub row. IIR has no sub-variant, so — unlike CWG
/// — there is no <c>Variant</c> slot (the AGSI shape).
/// </summary>
public readonly record struct IirFileContext(
    string Endpoint,
    DateOnly? RepresentativeDate,
    string RequestPath);

/// <summary>
/// Upserts the mandatory <c>arm.FileLog</c> hub row for one endpoint pull (ALL outcomes:
/// Success / NotAvailable / Failed) and returns its <c>FileLogId</c>, which the source reader stamps
/// onto every produced row (design §8).
/// </summary>
public interface IIirFileLog
{
    Task<int> UpsertAsync(
        IirFileContext file,
        string status,
        int? httpStatus,
        string requestPath,
        int rowCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c> and reading the returned scalar
/// <c>FileLogId</c> with <c>ExecuteScalar</c>. The call is serialized on the <see cref="SqlWriteGate"/>
/// (one shared key across all three endpoints; a fast single-row upsert) so parallel work units cannot
/// deadlock on the hub upsert — the CWG/AGSI posture. The Bearer token is never involved here; the
/// <c>RequestPath</c> is sanitized (no secret) and clamped to the proc's 400-char column width.
/// </summary>
public sealed class SqlIirFileLog : IIirFileLog
{
    private const int MaxRequestPathLength = 400;

    private readonly IirSettings _settings;
    private readonly ILogger<SqlIirFileLog> _logger;

    public SqlIirFileLog(IOptions<IirSettings> settings, ILogger<SqlIirFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        IirFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        var path = requestPath.Length > MaxRequestPathLength ? requestPath.Substring(0, MaxRequestPathLength) : requestPath;

        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), cancellationToken).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        // Explicit SqlDbType + size matching the proc's VARCHAR/DATE/INT params so no implicit
        // NVARCHAR→VARCHAR conversion happens server-side.
        cmd.Parameters.Add("@Endpoint", SqlDbType.VarChar, 40).Value = file.Endpoint;
        cmd.Parameters.Add("@RepresentativeDate", SqlDbType.Date).Value =
            file.RepresentativeDate.HasValue ? file.RepresentativeDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = (object?)httpStatus ?? DBNull.Value;
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = path;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;

        // The proc emits one row/one column named FileLogId — read it with ExecuteScalar.
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            throw new InvalidOperationException(
                $"arm.usp_UpsertFileLog returned no FileLogId for {file.Endpoint} " +
                $"{file.RepresentativeDate?.ToString("yyyy-MM-dd") ?? "-"}.");

        var fileLogId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        _logger.LogDebug(
            "IIR FileLog #{Id}: {Endpoint} {Path} → {Status} ({Rows} rows)",
            fileLogId, file.Endpoint, path, status, rowCount);
        return fileLogId;
    }
}
