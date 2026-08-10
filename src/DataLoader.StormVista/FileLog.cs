using System.Data;
using System.Globalization;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.StormVista;

/// <summary>
/// The natural-key identity of one request/file, passed to the FileLog upsert.
/// These values come from the work unit (+ the feed's endpoint name). The upsert
/// resolves each to its dimension id server-side (design load-flow step 3).
/// </summary>
public readonly record struct StormVistaFileContext(
    string EndpointName,   // "Daily" | "Regional" (dbo.Endpoint.Name)
    string ModelSlug,
    string CycleCode,
    string WddTypeSlug,
    string? RegionSetCode, // NULL for daily; "3" | "5" | "9" | "iso" for regional
    DateOnly InitDate);

/// <summary>
/// Upserts the mandatory <c>dbo.FileLog</c> hub row for one request (ALL outcomes:
/// Success / NotAvailable(404) / Failed) and returns its <c>FileLogId</c>. Written
/// from the source reader — which holds the HTTP status + row count and needs the
/// id to stamp onto every produced fact row (design load flow) — and from the
/// reader's catch path (Failed). The old <c>EnableFileLog</c> toggle is gone:
/// FileLog is the fact tables' parent, so it is always written.
/// </summary>
public interface IStormVistaFileLog
{
    Task<int> UpsertAsync(
        StormVistaFileContext file,
        string status,
        int? httpStatus,
        string requestPath,
        int rowCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>dbo.usp_UpsertFileLog</c> and reading the returned
/// scalar <c>FileLogId</c> with <c>ExecuteScalar</c>. The call is serialized on the
/// <see cref="SqlWriteGate"/> (keyed like the other direct-proc writers) so parallel
/// work units cannot deadlock on the hub upsert.
/// </summary>
public sealed class SqlStormVistaFileLog : IStormVistaFileLog
{
    private readonly StormVistaSettings _settings;
    private readonly ILogger<SqlStormVistaFileLog> _logger;

    public SqlStormVistaFileLog(IOptions<StormVistaSettings> settings, ILogger<SqlStormVistaFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        StormVistaFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        // Serialize concurrent upserts against the same proc so parallel work
        // units cannot deadlock on it (same posture as the Platts FileLog writer).
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "dbo.usp_UpsertFileLog"), cancellationToken).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("dbo.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        // Explicit SqlDbType + size matching the proc's VARCHAR/CHAR parameters, so
        // no implicit NVARCHAR→VARCHAR conversion happens server-side.
        cmd.Parameters.Add("@EndpointName", SqlDbType.VarChar, 20).Value = file.EndpointName;
        cmd.Parameters.Add("@ModelSlug", SqlDbType.VarChar, 40).Value = file.ModelSlug;
        cmd.Parameters.Add("@CycleCode", SqlDbType.Char, 2).Value = file.CycleCode;
        cmd.Parameters.Add("@WddTypeSlug", SqlDbType.VarChar, 10).Value = file.WddTypeSlug;
        cmd.Parameters.Add("@RegionSetCode", SqlDbType.VarChar, 4).Value = (object?)file.RegionSetCode ?? DBNull.Value;
        cmd.Parameters.Add("@InitDate", SqlDbType.Date).Value = file.InitDate.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = requestPath;
        cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = (object?)httpStatus ?? DBNull.Value;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;

        // The proc emits one row/one column named FileLogId — read it with ExecuteScalar.
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            throw new InvalidOperationException(
                $"dbo.usp_UpsertFileLog returned no FileLogId for {file.EndpointName} {file.ModelSlug} " +
                $"{file.InitDate:yyyy-MM-dd} {file.CycleCode} {file.WddTypeSlug} reg{file.RegionSetCode ?? "-"}.");

        var fileLogId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        _logger.LogDebug(
            "StormVista FileLog #{Id}: {Endpoint} {Path} → {Status} ({Rows} rows)",
            fileLogId, file.EndpointName, requestPath, status, rowCount);
        return fileLogId;
    }
}
