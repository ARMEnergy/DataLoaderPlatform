using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.OPIS;

/// <summary>
/// Module-level post-load quality check, following the AGSI / StormVista
/// precedent. Calls <c>arm.usp_ValidateLoad(@ReportDate)</c> and logs the
/// returned anomaly report (<c>CheckName, Scope, ExpectedCount, ActualCount,
/// Detail</c>).
///
/// <para>
/// Purely OBSERVATIONAL — it never throws out of <see cref="ValidateAsync"/>. The
/// LP feed publishes on weekdays only, so a run on a Saturday legitimately adds
/// nothing and must not be reported as a failed load. Deeper reconciliation is
/// DATA_QUALITY_VALIDATOR's job.
/// </para>
/// </summary>
public sealed class OpisLoadValidator
{
    private readonly OpisSettings _settings;
    private readonly ILogger<OpisLoadValidator> _logger;

    public OpisLoadValidator(IOptions<OpisSettings> settings, ILogger<OpisLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <param name="latestReportDate">
    /// The newest file date this run processed, used to scope the per-date check.
    /// Null (no work units ran) checks the whole table instead.
    /// </param>
    public async Task ValidateAsync(DateOnly? latestReportDate, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@ReportDate", SqlDbType.Date).Value =
                latestReportDate.HasValue ? latestReportDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var checkOrd = reader.GetOrdinal("CheckName");
            var scopeOrd = reader.GetOrdinal("Scope");
            var expectedOrd = reader.GetOrdinal("ExpectedCount");
            var actualOrd = reader.GetOrdinal("ActualCount");
            var detailOrd = reader.GetOrdinal("Detail");

            int anomalies = 0, informational = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var check = reader.IsDBNull(checkOrd) ? "?" : reader.GetString(checkOrd);
                var scope = reader.IsDBNull(scopeOrd) ? null : reader.GetString(scopeOrd);
                int? expected = reader.IsDBNull(expectedOrd) ? null : reader.GetInt32(expectedOrd);
                var actual = reader.IsDBNull(actualOrd) ? 0L : reader.GetInt64(actualOrd);
                var detail = reader.IsDBNull(detailOrd) ? null : reader.GetString(detailOrd);

                // Checks that state an expected value (all the "expect 0" integrity
                // ones) are real anomalies when they miss. The rest are counters.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "OPIS validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "OPIS validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "OPIS validation complete (reportDate={Date}): {Anomalies} anomaly row(s), {Info} informational row(s)",
                latestReportDate?.ToString("yyyy-MM-dd") ?? "ALL", anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled run must surface as cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPIS post-load validation failed to run (non-fatal)");
        }
    }
}
