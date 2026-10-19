using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after a run and logs what it reports.
///
/// <para>
/// <b>Observational only.</b> It never changes the run's outcome, and it swallows its
/// own failures — a validation query that cannot run (the proc not deployed yet, a
/// permissions gap, a timeout) must not turn a successful load into a failed one.
/// </para>
/// <para>
/// ⚠ <b>The timeout is set explicitly and is not the 30-second default.</b> Criterion
/// learned this the expensive way: one unbounded check in a validator expired at
/// <c>SqlCommand</c>'s default, and because the validator swallows its own failures
/// the abort took every OTHER check in the proc down with it while the run still
/// reported success. <c>arm.SeriesData</c> is headed for ~90 million rows, so both
/// halves of that lesson are applied here — every check inside the proc is bounded by
/// SeriesId (which leads the clustered key), and the budget is
/// <see cref="EiaSettings.ValidationTimeoutSeconds"/> rather than 30.
/// </para>
/// </summary>
internal sealed class EiaLoadValidator
{
    private readonly EiaSettings _settings;
    private readonly ILogger<EiaLoadValidator> _logger;

    public EiaLoadValidator(IOptions<EiaSettings> settings, ILogger<EiaLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if (!_settings.RunValidationAfterLoad) return;

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            _logger.LogDebug("EIA: no connection string — skipping post-load validation");
            return;
        }

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = Math.Max(30, _settings.ValidationTimeoutSeconds)
            };

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var findings = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings++;
                _logger.LogWarning(
                    "EIA validation — {Check} on {Table}: {Detail} ({Rows} row(s))",
                    reader["Check"], reader["TableName"], reader["Detail"], reader["RowCount"]);
            }

            if (findings == 0)
                _logger.LogInformation("EIA validation: no anomalies reported");
            else
                _logger.LogWarning("EIA validation: {Count} finding(s) — see the lines above", findings);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the run ending, not a validation finding.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "EIA: post-load validation could not run ({Message}). The load itself is unaffected",
                EiaHttp.Redact(ex.Message));
        }
    }
}
