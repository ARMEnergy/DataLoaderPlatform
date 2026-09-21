using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGX;

/// <summary>
/// Runs <c>arm.usp_ValidateLoad</c> after the pipelines finish and logs whatever it
/// reports.
///
/// <para>
/// <b>Observational only.</b> This never changes the run's outcome and never throws:
/// its own failures are caught and logged at warning level. A validation query that
/// takes the run down would be worse than no validation at all — the data is already
/// merged by the time it runs, so failing the run would neither undo nor repair
/// anything, and would mask which pipeline was actually at fault.
/// </para>
/// <para>
/// The snapshot date is passed in from the loader's own US-Central "today" rather than
/// letting the proc default to the SERVER's date, so a run that starts before midnight
/// Central and validates after it still checks the rows it actually wrote.
/// </para>
/// </summary>
internal sealed class NgxLoadValidator
{
    private readonly IOptions<NgxSettings> _settings;
    private readonly ILogger<NgxLoadValidator> _logger;

    public NgxLoadValidator(IOptions<NgxSettings> settings, ILogger<NgxLoadValidator> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <param name="indexFeedRan">
    /// Whether the IndexPrice pipeline actually executed this run. Passed through so the
    /// proc can skip its index checks: a disabled feed would otherwise report
    /// <c>IndexPriceNoSnapshot</c> on every run forever, which is precisely how an
    /// operator learns to ignore this channel.
    /// </param>
    /// <param name="stripFeedRan">The same, for the StripTradingSummary pipeline.</param>
    internal async Task ValidateAsync(
        DateOnly executionDate, bool indexFeedRan, bool stripFeedRan, CancellationToken cancellationToken)
    {
        if (!indexFeedRan && !stripFeedRan)
        {
            _logger.LogDebug("NGX: no feed ran — skipping post-load validation");
            return;
        }

        try
        {
            await using var conn = new SqlConnection(_settings.Value.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 300
            };
            cmd.Parameters.Add("@ExecutionDate", SqlDbType.Date).Value =
                executionDate.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@IndexFeedRan", SqlDbType.Bit).Value = indexFeedRan;
            cmd.Parameters.Add("@StripFeedRan", SqlDbType.Bit).Value = stripFeedRan;

            var findings = 0;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                findings++;
                _logger.LogWarning(
                    "NGX validation [{Check}]: {Detail}",
                    reader.IsDBNull(0) ? "?" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1));
            }

            if (findings == 0)
                _logger.LogInformation(
                    "NGX validation clean for ExecutionDate {Date}", NgxTime.Iso(executionDate));
            else
                _logger.LogWarning(
                    "NGX validation reported {Count} finding(s) for ExecutionDate {Date}",
                    findings, NgxTime.Iso(executionDate));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Most often: the arm objects are not deployed yet. That is worth a warning,
            // not a failed run — the merges above would already have failed if the
            // tables were genuinely missing.
            _logger.LogWarning(ex,
                "NGX post-load validation could not run (is sql/NGX/003 deployed?). The load result is " +
                "unaffected");
        }
    }
}
