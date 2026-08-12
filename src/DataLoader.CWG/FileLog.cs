using System.Data;
using System.Globalization;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CWG;

/// <summary>
/// The natural-key identity of one request/file, passed to the FileLog upsert
/// (design §5). Region / Variant / RepresentativeDate are NULL-able; SQL
/// NULL-equality in <c>arm.usp_UpsertFileLog</c> collapses undated / no-region
/// requests to a single stable hub row.
/// </summary>
public readonly record struct CwgFileContext(
    string Endpoint,
    string? Region,
    string? Variant,
    DateOnly? RepresentativeDate,
    string RequestPath);

/// <summary>
/// Upserts the mandatory <c>arm.FileLog</c> hub row for one request (ALL
/// outcomes: Success / NotAvailable / Failed) and returns its <c>FileLogId</c>,
/// which the source reader stamps onto every produced fact row (design §5).
/// </summary>
public interface ICwgFileLog
{
    Task<int> UpsertAsync(
        CwgFileContext file,
        string status,
        int? httpStatus,
        string requestPath,
        int rowCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c> and reading the
/// returned scalar <c>FileLogId</c> with <c>ExecuteScalar</c>. The call is
/// serialized on the <see cref="SqlWriteGate"/> (one shared key across all 15
/// endpoints; a fast single-row upsert) so parallel work units cannot deadlock
/// on the hub upsert — Platts/StormVista posture.
/// </summary>
public sealed class SqlCwgFileLog : ICwgFileLog
{
    private readonly CwgSettings _settings;
    private readonly ILogger<SqlCwgFileLog> _logger;

    public SqlCwgFileLog(IOptions<CwgSettings> settings, ILogger<SqlCwgFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        CwgFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
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
        cmd.Parameters.Add("@Variant", SqlDbType.VarChar, 16).Value = (object?)file.Variant ?? DBNull.Value;
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
                $"{file.Region ?? "-"} {file.Variant ?? "-"} {file.RepresentativeDate?.ToString("yyyy-MM-dd") ?? "-"}.");

        var fileLogId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        _logger.LogDebug(
            "CWG FileLog #{Id}: {Endpoint} {Path} → {Status} ({Rows} rows)",
            fileLogId, file.Endpoint, requestPath, status, rowCount);
        return fileLogId;
    }
}
