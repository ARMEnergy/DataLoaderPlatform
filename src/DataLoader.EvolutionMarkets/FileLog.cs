using System.Data;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// The natural-key identity of one request/outcome, passed to the <c>arm.FileLog</c> upsert
/// (design §6). The hub's natural key is <c>(EndpointId, RepresentativeDate)</c>, giving exactly one
/// stable hub row per business date that every run upserts in place.
///
/// <para><b>⚠ There is NO Region slot and NO Dataset slot.</b> AGSI's hub key carries a region
/// because its request axis <i>is</i> the country. This loader's only request axis is the DATE: one
/// request returns every instrument, every term and (absent a <c>datasetName</c> filter) every
/// permissioned dataset. Adding a dataset axis would also collide confusingly with the PAYLOAD
/// column <c>arm.MarketData.Market</c>. Do not add either (design §6).</para>
/// </summary>
/// <param name="Endpoint">
/// <c>MarketDataHistory</c> — see <see cref="EvoEndpoints"/>. Resolved server-side to
/// <c>arm.Endpoint.Id</c>; any other value raises.
/// </param>
/// <param name="RepresentativeDate">
/// The requested business date. <b>Never NULL for this loader</b> (every unit is dated), unlike
/// AGSI/NGI whose undated snapshot endpoints rely on the hub's NULL-equality behaviour.
/// </param>
/// <param name="RequestPath">
/// The relative request path. Carries no credential — the API key is a request HEADER, so no
/// Evolution Markets URL is ever sensitive.
/// </param>
public readonly record struct EvoFileContext(
    string Endpoint,
    DateOnly RepresentativeDate,
    string RequestPath);

/// <summary>
/// Upserts the mandatory <c>arm.FileLog</c> hub row for one request — for <b>ALL</b> outcomes
/// (<c>Success</c> / <c>NotAvailable</c> / <c>Failed</c>) — and returns its <c>FileLogId</c>, which
/// the source reader stamps onto every produced row (design §6).
///
/// <para>This is the audit surface that makes "that date published nothing" <b>visible rather than
/// invisible</b>. Because an empty <c>200</c> is indistinguishable from a weekend, the hub is the
/// ONLY place the distinction between "we asked and got nothing" and "we never asked" is recorded —
/// and it is the standing mitigation for the settled-zone hazard (design §3.5):
/// <c>SELECT … FROM arm.FileLog WHERE [RowCount] = 0</c> lists every date that produced no rows.</para>
/// </summary>
public interface IEvoFileLog
{
    Task<int> UpsertAsync(
        EvoFileContext file,
        string status,
        int? httpStatus,
        int rowCount,
        int droppedRowCount,
        int pageCount,
        string? errorMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c> and reading the returned scalar
/// <c>FileLogId</c> with <c>ExecuteScalar</c> (the proc emits exactly one row / one column).
///
/// <para>Because this is a <b>direct</b> proc caller (not a <c>SqlSinkBase</c> subclass, which would
/// acquire the gate for us) it acquires the <see cref="SqlWriteGate"/> itself — a key distinct from
/// the merge proc's, so parallel work units cannot deadlock on the hub upsert and the hub upsert
/// cannot deadlock against the fact merge. It is a fast single-row upsert, so serialising it is
/// cheap (design §11) — the NGI/CWG/AGSI posture.</para>
///
/// <para>Parameters are added with an <b>explicit</b> <c>SqlDbType</c> + size matching the proc's
/// declarations, so no implicit <c>NVARCHAR → VARCHAR</c> conversion happens server-side.</para>
/// </summary>
public sealed class SqlEvoFileLog : IEvoFileLog
{
    /// <summary>Matches <c>@ErrorMessage NVARCHAR(400)</c> in the proc; longer text is truncated here, not server-side.</summary>
    private const int ErrorMessageMaxLength = 400;

    /// <summary>Matches <c>@RequestPath NVARCHAR(1000)</c>. The pinned field projection makes these paths ~380 chars.</summary>
    private const int RequestPathMaxLength = 1000;

    private readonly EvoSettings _settings;
    private readonly ILogger<SqlEvoFileLog> _logger;

    public SqlEvoFileLog(IOptions<EvoSettings> settings, ILogger<SqlEvoFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        EvoFileContext file, string status, int? httpStatus, int rowCount, int droppedRowCount,
        int pageCount, string? errorMessage, CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"),
            cancellationToken).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@Endpoint", SqlDbType.VarChar, 40).Value = file.Endpoint;
        cmd.Parameters.Add("@RepresentativeDate", SqlDbType.Date).Value =
            file.RepresentativeDate.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = (object?)httpStatus ?? DBNull.Value;
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, RequestPathMaxLength).Value =
            Cap(file.RequestPath, RequestPathMaxLength);
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;
        cmd.Parameters.Add("@DroppedRowCount", SqlDbType.Int).Value = droppedRowCount;
        cmd.Parameters.Add("@PageCount", SqlDbType.Int).Value = pageCount;
        cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, ErrorMessageMaxLength).Value =
            string.IsNullOrWhiteSpace(errorMessage)
                ? DBNull.Value
                : Cap(errorMessage!, ErrorMessageMaxLength);

        // The proc emits one row / one column named FileLogId — read it with ExecuteScalar.
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
            throw new InvalidOperationException(
                $"arm.usp_UpsertFileLog returned no FileLogId for {file.Endpoint} " +
                $"{EvoTime.Iso(file.RepresentativeDate)}.");

        var fileLogId = Convert.ToInt32(result, EvoTime.Inv);
        _logger.LogDebug(
            "EvolutionMarkets FileLog #{Id}: {Endpoint} {Date} → {Status} ({Rows} rows, {Dropped} dropped, {Pages} page(s))",
            fileLogId, file.Endpoint, EvoTime.Iso(file.RepresentativeDate), status, rowCount, droppedRowCount, pageCount);
        return fileLogId;
    }

    /// <summary>
    /// Truncates to the proc's declared parameter width. Done client-side on purpose: an over-long
    /// value passed to a sized <see cref="SqlParameter"/> throws at execute time, and a hub write
    /// must never be the thing that fails a unit — least of all the <c>Failed</c> hub write that is
    /// reporting some other error.
    /// </summary>
    private static string Cap(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
