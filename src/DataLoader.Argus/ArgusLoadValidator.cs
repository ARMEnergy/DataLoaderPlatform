using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Argus;

/// <summary>
/// Module-level post-load quality check, following the OPIS / AGSI / StormVista
/// precedent. Calls <c>dlp.usp_ValidateLoad(@ReportDate)</c> and logs the returned
/// anomaly report (<c>CheckName, Scope, ExpectedCount, ActualCount, Detail</c>).
///
/// <para>
/// Purely OBSERVATIONAL — it never throws out of <see cref="ValidateAsync"/>.
/// DCRDEUS publishes on weekdays only, so a Saturday run legitimately adds nothing
/// and must not be reported as a failed load. Deeper reconciliation is
/// DATA_QUALITY_VALIDATOR's job, and needs a live loaded database.
/// </para>
/// <para>
/// Note that several checks are EXPECTED to be non-zero at times: the reference
/// and fact feeds load independently, so a fact row can arrive before the lookup
/// snapshot that explains it. Those show as warnings, which is the point — they
/// are worth seeing, not worth failing a run over.
/// </para>
/// </summary>
public sealed class ArgusLoadValidator
{
    private readonly ArgusSettings _settings;
    private readonly ILogger<ArgusLoadValidator> _logger;

    public ArgusLoadValidator(IOptions<ArgusSettings> settings, ILogger<ArgusLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <param name="latestReportDate">
    /// The newest DCRDEUS file date this run processed, used to scope the per-date
    /// check. Null (no dated work units ran) checks the whole table instead.
    /// </param>
    public async Task ValidateAsync(DateOnly? latestReportDate, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("dlp.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
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
                        "Argus validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "Argus validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "Argus validation complete (reportDate={Date}): {Anomalies} anomaly row(s), {Info} informational row(s)",
                latestReportDate?.ToString("yyyy-MM-dd") ?? "ALL", anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled run must surface as cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Argus post-load validation failed to run (non-fatal)");
        }
    }
}
