using System.Data;
using System.Globalization;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.AGSI;

/// <summary>
/// The natural-key identity of one request/outcome, passed to the FileLog upsert
/// (design §5). <see cref="Region"/> / <see cref="RepresentativeDate"/> are
/// NULL-able; SQL NULL-equality in <c>arm.usp_UpsertFileLog</c> collapses the
/// undated / no-region About request to a single stable hub row. Unlike CWG's
/// context there is NO <c>Variant</c> slot (AGSI has no sub-variant).
/// </summary>
public readonly record struct AgsiFileContext(
    string Endpoint,
    string? Region,
    DateOnly? RepresentativeDate,
    string RequestPath);

/// <summary>
/// Upserts the mandatory <c>arm.FileLog</c> hub row for one request (ALL outcomes:
/// Success / NotAvailable / Failed) and returns its <c>FileLogId</c>, which the
/// source reader stamps onto every produced row (design §5).
/// </summary>
public interface IAgsiFileLog
{
    Task<int> UpsertAsync(
        AgsiFileContext file,
        string status,
        int? httpStatus,
        string requestPath,
        int rowCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c> and reading the returned
/// scalar <c>FileLogId</c> with <c>ExecuteScalar</c>. The call is serialized on the
/// <see cref="SqlWriteGate"/> (one shared key across both endpoints; a fast
/// single-row upsert) so parallel work units cannot deadlock on the hub upsert —
/// the Platts/StormVista/CWG posture. The <c>x-key</c> is never involved here; the
/// <c>RequestPath</c> is sanitized (no secret).
/// </summary>
public sealed class SqlAgsiFileLog : IAgsiFileLog
{
    private readonly AgsiSettings _settings;
    private readonly ILogger<SqlAgsiFileLog> _logger;

    public SqlAgsiFileLog(IOptions<AgsiSettings> settings, ILogger<SqlAgsiFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        AgsiFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), cancellationToken).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        // Explicit SqlDbType + size matching the proc's VARCHAR/DATE/INT params, so no
        // implicit NVARCHAR→VARCHAR conversion happens server-side.
        cmd.Parameters.Add("@Endpoint", SqlDbType.VarChar, 40).Value = file.Endpoint;
        cmd.Parameters.Add("@Region", SqlDbType.VarChar, 16).Value = (object?)file.Region ?? DBNull.Value;
        cmd.Parameters.Add("@RepresentativeDate", SqlDbType.Date).Value =
            file.RepresentativeDate.HasValue ? file.RepresentativeDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = (object?)httpStatus ?? DBNull.Value;
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = requestPath;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;

        // The proc emits one row/one column named FileLogId — read it with ExecuteScalar.
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            throw new InvalidOperationException(
                $"arm.usp_UpsertFileLog returned no FileLogId for {file.Endpoint} " +
                $"{file.Region ?? "-"} {file.RepresentativeDate?.ToString("yyyy-MM-dd") ?? "-"}.");

        var fileLogId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        _logger.LogDebug(
            "AGSI FileLog #{Id}: {Endpoint} {Path} → {Status} ({Rows} rows)",
            fileLogId, file.Endpoint, requestPath, status, rowCount);
        return fileLogId;
    }
}
