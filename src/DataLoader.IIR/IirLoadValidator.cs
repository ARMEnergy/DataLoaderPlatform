using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.IIR;

/// <summary>
/// Module-level post-load quality check (design §9), following the AGSI <c>AgsiLoadValidator</c>
/// precedent. Calls <c>arm.usp_ValidateLoad(@RunDate)</c> for the run's US-Central date and logs the
/// returned anomaly report (columns <c>CheckName, Scope, ExpectedCount, ActualCount, Detail</c>).
/// Purely observational — it never fails the run (a legitimately sparse OfflineEvent day must not fail
/// an otherwise-good load); the deeper reconciliation is DATA_QUALITY_VALIDATOR's job.
///
/// <para><b>Build-only:</b> coded and unit-testable this pass but not exercised against live data.</para>
/// </summary>
public sealed class IirLoadValidator
{
    private readonly IirSettings _settings;
    private readonly ILogger<IirLoadValidator> _logger;

    public IirLoadValidator(IOptions<IirSettings> settings, ILogger<IirLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(LoaderRunContext context, CancellationToken cancellationToken)
    {
        // The run's US-Central date — the same basis the OfflineEvent RunDate partition is stamped on
        // (§5.1/§5.4), so the validation scope can never drift from the load.
        var runDate = IirTime.CentralDate(context.StartedAtUtc);

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@RunDate", SqlDbType.Date).Value = runDate.ToDateTime(TimeOnly.MinValue);

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

                // An "expected" value the actual count fails to match is a real anomaly (lat/long range,
                // geography consistency and flag-domain checks all expect 0). The rest are informational.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "IIR validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "IIR validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "IIR validation complete for {RunDate:yyyy-MM-dd}: {Anomalies} anomaly row(s), {Info} informational row(s)",
                runDate, anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must surface as cancellation, not a non-fatal validation failure.
            throw;
        }
        catch (Exception ex)
        {
            // Observational only: a validation failure must not fail the load.
            _logger.LogError(ex, "IIR post-load validation failed to run (non-fatal)");
        }
    }
}
