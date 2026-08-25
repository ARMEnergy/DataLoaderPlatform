using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ModernCommodities;

/// <summary>
/// The natural-key identity of one pull, passed to the <c>arm.FileLog</c> upsert (design §6.2).
/// The hub's natural key is <c>(EndpointId, WindowStart, WindowEnd, RunToken)</c> — all four
/// <c>NOT NULL</c>, so a plain <c>UNIQUE</c> constraint suffices (no NULL-equality trick is needed
/// here, unlike NGI's undated Locations row).
///
/// <para><b>⚠ The hub grain is ONE ROW PER PULL, not an upsert over a stable window key</b>
/// (decision D7). <c>arm.FileLog</c> is the <i>only</i> provenance this loader has — the three fact
/// tables carry no <c>FileLogId</c> (decision D1) — so collapsing pulls would <b>erase</b> the fact
/// that the 03:00 pull returned 0 rows after the 02:00 pull returned 20. Every hour's outcome for
/// every window must survive; ~26 k rows/year/endpoint is negligible.</para>
///
/// <para>It is still a MERGE rather than a blind INSERT because a <i>failed</i> unit is retried by
/// the next run within the same hour (<c>core.LoadLog</c> only skips successes) and that retry
/// carries the same four key values — so the retry's outcome <b>replaces</b> the failure in place.
/// Distinct hours and distinct windows never collapse.</para>
/// </summary>
/// <param name="Endpoint"><c>AllTrades</c> | <c>MyTrades</c> | <c>Settlements</c>; resolved server-side to <c>arm.Endpoint.Id</c>, raising on an unknown name.</param>
/// <param name="WindowStart">The <b>actual</b> <c>startDate</c> sent, after clamping and chunking.</param>
/// <param name="WindowEnd">The <b>actual</b> <c>endDate</c> sent.</param>
/// <param name="RunToken">The resume key's <c>{hot}</c> token — <c>yyyyMMddHH</c> (UTC) at the default.</param>
/// <param name="ScopeLabel">The <c>myTrades</c> <c>legalEntityName</c> when set, else <c>null</c>.</param>
/// <param name="RequestPath">Sanitised: path + <c>startDate</c>/<c>endDate</c> (+ <c>legalEntityName</c>). No ModCom URL ever carries a credential — it is a header.</param>
public readonly record struct ModComFileContext(
    string Endpoint,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    string RunToken,
    string? ScopeLabel,
    string RequestPath);

/// <summary>
/// Upserts the mandatory <c>arm.FileLog</c> hub row for one pull — for <b>ALL</b> outcomes
/// (<c>Success</c> / <c>NotAvailable</c> / <c>Failed</c>).
///
/// <para>It returns the <c>FileLogId</c>, but <b>nothing consumes it as data</b>: the fact tables
/// have no <c>FileLogId</c> column, so the reader stamps nothing onto rows. The value is returned
/// purely so a pull can be logged as <c>FileLog #N</c> and so a future schema change has it
/// available without a signature change (design §6.1).</para>
/// </summary>
public interface IModComFileLog
{
    Task<int> UpsertAsync(
        ModComFileContext file,
        string status,
        int? httpStatus,
        int rowCount,
        int droppedRowCount,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c> and reading the returned scalar
/// <c>FileLogId</c> (the proc emits exactly one row / one column).
///
/// <para>Because this is a <b>direct</b> proc caller (not a <c>SqlSinkBase</c> subclass, which
/// would acquire the gate for us) it acquires the <see cref="SqlWriteGate"/> itself — <b>one shared
/// key across all three endpoints, distinct from the three merge-proc keys</b>, so the hub upsert
/// can neither deadlock against a fact merge nor against another endpoint's hub write. It is a fast
/// single-row upsert, so serialising it is cheap (design §11.1).</para>
///
/// <para>Parameters are added with an <b>explicit</b> <c>SqlDbType</c> + size matching the proc's
/// declarations, so no implicit <c>NVARCHAR → VARCHAR</c> conversion happens server-side.
/// <c>ErrorMessage</c> is clamped to the column's 400 characters here, because it carries the
/// <b>verbatim non-2xx body</b> and the four <c>400</c>s are only distinguishable by that text.
/// <b>No credential can appear in it</b>: the ModCom credential is a request header, never a URL or
/// body value.</para>
/// </summary>
public sealed class SqlModComFileLog : IModComFileLog
{
    /// <summary><c>arm.FileLog.ErrorMessage</c> is <c>NVARCHAR(400)</c>; over-long bodies are clamped client-side.</summary>
    private const int ErrorMessageMaxLength = 400;

    /// <summary><c>arm.FileLog.RequestPath</c> is <c>NVARCHAR(400) NOT NULL</c>.</summary>
    private const int RequestPathMaxLength = 400;

    /// <summary><c>arm.FileLog.ScopeLabel</c> is <c>VARCHAR(100)</c>.</summary>
    private const int ScopeLabelMaxLength = 100;

    private readonly ModComSettings _settings;
    private readonly ILogger<SqlModComFileLog> _logger;

    public SqlModComFileLog(IOptions<ModComSettings> settings, ILogger<SqlModComFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        ModComFileContext file, string status, int? httpStatus, int rowCount, int droppedRowCount,
        string? errorMessage, CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), cancellationToken).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@Endpoint", SqlDbType.VarChar, 40).Value = file.Endpoint;
        cmd.Parameters.Add("@WindowStart", SqlDbType.Date).Value = file.WindowStart.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@WindowEnd", SqlDbType.Date).Value = file.WindowEnd.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@RunToken", SqlDbType.VarChar, 40).Value = file.RunToken;
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = (object?)httpStatus ?? DBNull.Value;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;
        cmd.Parameters.Add("@DroppedRowCount", SqlDbType.Int).Value = droppedRowCount;
        cmd.Parameters.Add("@ScopeLabel", SqlDbType.VarChar, ScopeLabelMaxLength).Value =
            (object?)Clamp(file.ScopeLabel, ScopeLabelMaxLength) ?? DBNull.Value;
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, RequestPathMaxLength).Value =
            Clamp(file.RequestPath, RequestPathMaxLength) ?? string.Empty;
        cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, ErrorMessageMaxLength).Value =
            (object?)Clamp(errorMessage, ErrorMessageMaxLength) ?? DBNull.Value;

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
            throw new InvalidOperationException(
                $"arm.usp_UpsertFileLog returned no FileLogId for {file.Endpoint} " +
                $"{ModComTime.Iso(file.WindowStart)}..{ModComTime.Iso(file.WindowEnd)} run={file.RunToken}.");

        var fileLogId = Convert.ToInt32(result, ModComTime.Inv);
        _logger.LogDebug(
            "ModCom FileLog #{Id}: {Endpoint} {Path} run={RunToken} → {Status} ({Rows} rows, {Dropped} dropped)",
            fileLogId, file.Endpoint, file.RequestPath, file.RunToken, status, rowCount, droppedRowCount);
        return fileLogId;
    }

    private static string? Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
    }
}
