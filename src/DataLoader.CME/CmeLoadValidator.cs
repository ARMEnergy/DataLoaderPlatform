using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CME;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after a pass and logs whatever it reports.
///
/// <para>
/// <b>Observational only.</b> This never changes the run's outcome and swallows
/// its own failures: a validation query that cannot run (permissions, a proc not
/// yet deployed) must not turn a good load into a failed one. Its job is to put
/// anomalies in the run log where a human will see them.
/// </para>
/// </summary>
public sealed class CmeLoadValidator
{
    private readonly CmeSettings _settings;
    private readonly ILogger<CmeLoadValidator> _logger;

    public CmeLoadValidator(IOptions<CmeSettings> settings, ILogger<CmeLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(DateOnly? tradeDate, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.Add("@TradeDate", SqlDbType.Date).Value =
                tradeDate.HasValue ? tradeDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var findings = 0;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings++;

                var check = reader["CheckName"]?.ToString() ?? "(unnamed)";
                var severity = reader["Severity"]?.ToString() ?? "Info";
                var detail = reader["Detail"]?.ToString() ?? string.Empty;

                if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase))
                    _logger.LogWarning("CME validation [{Check}]: {Detail}", check, detail);
                else
                    _logger.LogInformation("CME validation [{Check}]: {Detail}", check, detail);
            }

            if (findings == 0)
                _logger.LogInformation("CME validation: no anomalies reported for {Scope}",
                    tradeDate.HasValue ? CmeTime.Iso(tradeDate.Value) : "the whole table");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CME: post-load validation could not run — the load itself is unaffected");
        }
    }
}
