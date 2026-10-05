using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OilX;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after a run and logs what it reports.
///
/// <para>
/// <b>Observational only.</b> It never changes the run's outcome, and it swallows its
/// own failures — a validation query that cannot run (the proc not deployed yet, a
/// permissions gap, a timeout) must not turn a successful load into a failed one. The
/// anomalies it surfaces are judgement calls for an operator, not conditions the loader
/// can act on: a <c>RunDateGap</c> is usually the vendor genuinely not publishing that
/// day, which this loader correctly records as an empty success.
/// </para>
/// </summary>
internal sealed class OilXLoadValidator
{
    private readonly OilXSettings _settings;
    private readonly ILogger<OilXLoadValidator> _logger;

    public OilXLoadValidator(IOptions<OilXSettings> settings, ILogger<OilXLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            _logger.LogDebug("OilX: no connection string — skipping post-load validation");
            return;
        }

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 300
            };

            // Look a little past the window so a gap at its leading edge is still visible.
            cmd.Parameters.AddWithValue("@Days", Math.Max(1, _settings.DaysBack) + 5);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var findings = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings++;
                _logger.LogWarning(
                    "OilX validation — {Check} on {Table}: {Detail} ({Rows} row(s))",
                    reader["Check"], reader["TableName"], reader["Detail"], reader["RowCount"]);
            }

            if (findings == 0)
                _logger.LogInformation("OilX validation: no anomalies reported");
            else
                _logger.LogWarning("OilX validation: {Count} finding(s) — see the lines above", findings);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the run ending, not a validation finding.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "OilX: post-load validation could not run ({Message}). The load itself is unaffected",
                OilXHttp.Redact(ex.Message));
        }
    }
}
