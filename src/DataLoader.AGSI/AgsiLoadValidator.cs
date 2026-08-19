using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.AGSI;

/// <summary>
/// Module-level post-load quality check (design §8), following the StormVista
/// <c>StormVistaLoadValidator</c> precedent. Calls
/// <c>arm.usp_ValidateLoad(@DateFrom, @DateTo)</c> for the run's storage window and
/// logs the returned anomaly report (columns <c>CheckName, Scope, ExpectedCount,
/// ActualCount, Detail</c>). Purely observational — it never throws out (a
/// legitimately sparse day with many <c>NotAvailable</c> outcomes must not fail an
/// otherwise-good load); the deeper reconciliation is DATA_QUALITY_VALIDATOR's job.
///
/// <para><b>Build-only:</b> coded and unit-testable this pass but not exercised
/// against live data.</para>
/// </summary>
public sealed class AgsiLoadValidator
{
    private readonly AgsiSettings _settings;
    private readonly ILogger<AgsiLoadValidator> _logger;

    public AgsiLoadValidator(IOptions<AgsiSettings> settings, ILogger<AgsiLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(LoaderRunContext context, CancellationToken cancellationToken)
    {
        // Same window derivation as the storage work-unit provider (shared — design §3.2),
        // so the validation window can never drift from the load window.
        var window = AgsiTime.ResolveWindow(context.StartedAtUtc, _settings.DaysBack);
        var from = window.From;
        var to = window.To;

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@DateFrom", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@DateTo", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);

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

                // An "expected" value the actual count fails to match is a real anomaly
                // (EntityBlankKey / StorageStatusDomain / GasDayEndOffByOne / OrphanEntityId /
                // DateEqualsGasDayStart all expect 0). The rest are informational counters.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "AGSI validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "AGSI validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "AGSI validation complete for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}: {Anomalies} anomaly row(s), {Info} informational row(s)",
                from, to, anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must surface as cancellation, not a non-fatal validation failure.
            throw;
        }
        catch (Exception ex)
        {
            // Observational only: a validation failure must not fail the load.
            _logger.LogError(ex, "AGSI post-load validation failed to run (non-fatal)");
        }
    }
}
