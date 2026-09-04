using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Criterion;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after a run and logs whatever it reports.
///
/// <para>
/// <b>Observational only.</b> It swallows its own failures and never changes the
/// run's outcome — a validation query that cannot connect must not turn a successful
/// load into a failed one.
/// </para>
/// <para>
/// The findings that matter most here are the ones that would otherwise be invisible:
/// a dimension snapshot that came back empty, a gas day present in
/// <c>arm.Pipelines_NominationPoint</c> but absent from <c>arm.Pipelines_Pointflows</c>
/// (which means one of the two pipelines reading the SAME source rows failed), and
/// observations in <c>arm.Financial_SeriesData</c> whose parent publication is missing
/// from <c>arm.Financial_Series</c> — the drift that would appear if the two financial
/// pipelines were ever pointed at different source relations, which is exactly the
/// mistake the original specification would have produced.
/// </para>
/// </summary>
public sealed class CriterionLoadValidator
{
    private readonly IOptions<CriterionSettings> _settings;
    private readonly ILogger<CriterionLoadValidator> _logger;

    public CriterionLoadValidator(IOptions<CriterionSettings> settings, ILogger<CriterionLoadValidator> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <param name="asOfDate">Scope the day-windowed checks to this day, or null for today (UTC).</param>
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
                        _logger.LogError("Criterion validation [{Check}] {Detail}", check, detail);
                        break;
                    case "WARN":
                        _logger.LogWarning("Criterion validation [{Check}] {Detail}", check, detail);
                        break;
                    default:
                        _logger.LogInformation("Criterion validation [{Check}] {Detail}", check, detail);
                        break;
                }
            }

            if (findings == 0)
                _logger.LogInformation("Criterion validation: no findings{Scope}",
                    asOfDate.HasValue ? $" for {CriterionTime.Iso(asOfDate.Value)}" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Criterion: post-load validation could not run — run outcome is unaffected");
        }
    }
}
