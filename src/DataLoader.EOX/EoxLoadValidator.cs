using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EOX;

/// <summary>
/// Module-level post-load quality check, following the OPIS / Argus / AGSI
/// precedent. Calls <c>arm.usp_ValidateLoad(@CurveDate)</c> and logs the returned
/// anomaly report (<c>CheckName, Scope, ExpectedCount, ActualCount, Detail</c>).
///
/// <para>
/// Purely OBSERVATIONAL — it never throws out of <see cref="ValidateAsync"/>. EOX
/// publishes on trading days only, so a Saturday run legitimately adds nothing and
/// must not be reported as a failed load. Deeper reconciliation is
/// DATA_QUALITY_VALIDATOR's job, and needs a live loaded database.
/// </para>
/// </summary>
public sealed class EoxLoadValidator
{
    private readonly EoxSettings _settings;
    private readonly ILogger<EoxLoadValidator> _logger;

    public EoxLoadValidator(IOptions<EoxSettings> settings, ILogger<EoxLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <param name="latestCurveDate">
    /// The newest curve date this run processed, used to scope the per-date checks.
    /// Null (no work units ran) checks the whole table instead.
    /// </param>
    public async Task ValidateAsync(DateOnly? latestCurveDate, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@CurveDate", SqlDbType.Date).Value =
                latestCurveDate.HasValue ? latestCurveDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

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
                // ones) are real anomalies when they miss. The rest are counters —
                // including the per-date row counts, which are legitimately 0 on a
                // weekend or holiday.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "EOX validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "EOX validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "EOX validation complete (curveDate={Date}): {Anomalies} anomaly row(s), {Info} informational row(s)",
                latestCurveDate.HasValue ? EoxTime.Iso(latestCurveDate.Value) : "ALL", anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled run must surface as cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EOX post-load validation failed to run (non-fatal)");
        }
    }
}
