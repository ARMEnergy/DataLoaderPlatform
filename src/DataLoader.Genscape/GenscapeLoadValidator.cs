using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Genscape;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after a run and logs whatever it reports.
///
/// <para>
/// <b>Observational only.</b> It swallows its own failures and never changes the run's
/// outcome — a validation query that cannot connect must not turn a successful load
/// into a failed one.
/// </para>
/// <para>
/// The finding that matters most here is <c>OilFundWeekDrift</c>: the proc re-computes
/// <c>DATEPART(week, ReportDate)</c> server-side under an explicit <c>SET DATEFIRST
/// 7</c> and compares it to the stored <c>Week</c>. That is the independent check on
/// the one value this loader overwrites, and the only thing that would notice if the
/// derivation were ever removed or changed.
/// </para>
/// </summary>
public sealed class GenscapeLoadValidator
{
    private readonly IOptions<GenscapeSettings> _settings;
    private readonly ILogger<GenscapeLoadValidator> _logger;

    public GenscapeLoadValidator(IOptions<GenscapeSettings> settings, ILogger<GenscapeLoadValidator> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <param name="asOfDate">Scope the staleness check to this day, or null for today (UTC).</param>
    public async Task ValidateAsync(DateOnly? asOfDate, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.Value.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.Add("@AsOfDate", SqlDbType.Date).Value =
                asOfDate.HasValue ? asOfDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var findings = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings++;

                var severity = reader["Severity"] as string ?? "INFO";
                var check = reader["Check"] as string ?? "(unnamed)";
                var detail = reader["Detail"] as string ?? string.Empty;

                // Severity comes from the proc so the SQL owns what is alarming; the
                // loader only decides which log level carries it.
                switch (severity)
                {
                    case "ERROR":
                        _logger.LogError("Genscape validation [{Check}] {Detail}", check, detail);
                        break;
                    case "WARN":
                        _logger.LogWarning("Genscape validation [{Check}] {Detail}", check, detail);
                        break;
                    default:
                        _logger.LogInformation("Genscape validation [{Check}] {Detail}", check, detail);
                        break;
                }
            }

            if (findings == 0)
                _logger.LogInformation("Genscape validation: no findings{Scope}",
                    asOfDate.HasValue ? $" for {GenscapeTime.Iso(asOfDate.Value)}" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Genscape: post-load validation could not run — run outcome is unaffected");
        }
    }
}
