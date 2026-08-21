using System.Data;
using System.Globalization;
using DataLoader.Core.Concurrency;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGI;

/// <summary>
/// The natural-key identity of one request/outcome, passed to the <c>arm.FileLog</c> upsert
/// (design §6). The hub's natural key is <c>(EndpointId, RepresentativeDate)</c>; SQL NULL-equality in
/// the UNIQUE constraint collapses the <b>undated</b> Locations pull to a single stable hub row that
/// is upserted every run.
///
/// <para><b>⚠ Divergence from AGSI: there is NO <c>Region</c> slot.</b> AGSI's hub key is
/// <c>(EndpointId, RegionId, RepresentativeDate)</c> because its request axis <i>is</i> the country.
/// NGI has no region request axis — one request returns all regions — and worse, a
/// <c>FileLog.RegionId</c> (an audit axis) sitting next to <c>arm.BidWeekData.Region</c> (a
/// <b>payload</b> column) would mean two unrelated things under one name. So: no <c>arm.Region</c>
/// table, no <c>@Region</c> parameter, no <c>Region</c> slot here (design §6).</para>
/// </summary>
/// <param name="Endpoint">
/// <c>BidWeekLocations</c> or <c>BidWeekData</c> — see <see cref="NgiEndpoints"/>. Resolved
/// server-side to <c>arm.Endpoint.Id</c>; any other value raises.
/// </param>
/// <param name="RepresentativeDate">
/// The requested issue date for <c>BidWeekData</c>; <c>NULL</c> for the undated
/// <c>BidWeekLocations</c> snapshot.
/// </param>
/// <param name="RequestPath">Sanitised path (+ <c>issue_date</c>/<c>format</c> only) — no credential.</param>
public readonly record struct NgiFileContext(
    string Endpoint,
    DateOnly? RepresentativeDate,
    string RequestPath);

/// <summary>
/// Upserts the mandatory <c>arm.FileLog</c> hub row for one request — for <b>ALL</b> outcomes
/// (<c>Success</c> / <c>NotAvailable</c> / <c>Failed</c>) — and returns its <c>FileLogId</c>, which the
/// source reader stamps onto every produced row (design §6).
///
/// <para>This is the audit surface that makes "no publication that day" <b>visible rather than
/// invisible</b>, and it is the standing mitigation for the settled-zone-404 invariant (design §3.5):
/// even a frozen settled date's 404 stays auditable via
/// <c>SELECT … FROM arm.FileLog WHERE HttpStatus = 404</c>.</para>
/// </summary>
public interface INgiFileLog
{
    Task<int> UpsertAsync(
        NgiFileContext file,
        string status,
        int? httpStatus,
        string requestPath,
        int rowCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// SQL implementation calling <c>arm.usp_UpsertFileLog</c> and reading the returned scalar
/// <c>FileLogId</c> with <c>ExecuteScalar</c> (the proc emits exactly one row / one column).
///
/// <para>Because this is a <b>direct</b> proc caller (not a <c>SqlSinkBase</c> subclass, which would
/// acquire the gate for us) it acquires the <see cref="SqlWriteGate"/> itself — one shared key across
/// both endpoints, distinct from the two merge-proc keys, so parallel work units cannot deadlock on
/// the hub upsert and the hub upsert cannot deadlock against a fact merge. It is a fast single-row
/// upsert, so serialising it is cheap (design §11) — the Platts/StormVista/CWG/AGSI posture.</para>
///
/// <para>Parameters are added with an <b>explicit</b> <c>SqlDbType</c> + size matching the proc's
/// <c>VARCHAR</c>/<c>DATE</c>/<c>INT</c> declarations, so no implicit <c>NVARCHAR → VARCHAR</c>
/// conversion happens server-side. No credential is involved: <c>RequestPath</c> is sanitised and no
/// NGI URL carries a secret (the credential lives in the <c>POST /auth</c> body).</para>
/// </summary>
public sealed class SqlNgiFileLog : INgiFileLog
{
    private readonly NgiSettings _settings;
    private readonly ILogger<SqlNgiFileLog> _logger;

    public SqlNgiFileLog(IOptions<NgiSettings> settings, ILogger<SqlNgiFileLog> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<int> UpsertAsync(
        NgiFileContext file, string status, int? httpStatus, string requestPath, int rowCount,
        CancellationToken cancellationToken)
    {
        using var gate = await SqlWriteGate.AcquireAsync(
            SqlWriteGate.KeyFor(_settings.ConnectionString, "arm.usp_UpsertFileLog"), cancellationToken).ConfigureAwait(false);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new SqlCommand("arm.usp_UpsertFileLog", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@Endpoint", SqlDbType.VarChar, 40).Value = file.Endpoint;
        cmd.Parameters.Add("@RepresentativeDate", SqlDbType.Date).Value =
            file.RepresentativeDate.HasValue ? file.RepresentativeDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        cmd.Parameters.Add("@StatusLabel", SqlDbType.VarChar, 20).Value = status;
        cmd.Parameters.Add("@HttpStatus", SqlDbType.Int).Value = (object?)httpStatus ?? DBNull.Value;
        cmd.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 400).Value = requestPath;
        cmd.Parameters.Add("@RowCount", SqlDbType.Int).Value = rowCount;

        // The proc emits one row / one column named FileLogId — read it with ExecuteScalar.
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            throw new InvalidOperationException(
                $"arm.usp_UpsertFileLog returned no FileLogId for {file.Endpoint} " +
                $"{file.RepresentativeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}.");

        var fileLogId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        _logger.LogDebug(
            "NGI FileLog #{Id}: {Endpoint} {Path} → {Status} ({Rows} rows)",
            fileLogId, file.Endpoint, requestPath, status, rowCount);
        return fileLogId;
    }
}
