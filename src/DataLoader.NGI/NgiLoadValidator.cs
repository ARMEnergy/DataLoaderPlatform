using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.NGI;

/// <summary>
/// Module-level post-load quality check (design §9), following the StormVista/AGSI/IIR precedent
/// (<c>LoaderPipelineBase</c> has no post-load hook and <see cref="NgiModule"/> owns orchestration).
/// Calls <c>arm.usp_ValidateLoad(@DateFrom, @DateTo)</c> for the run's issue-date window and logs the
/// returned report (one result set, uniform shape <c>CheckName, Scope, ExpectedCount, ActualCount,
/// Detail</c>).
///
/// <para>The window comes from <see cref="NgiTime.ResolveWindow"/> — the <b>same call</b> the
/// <c>BidWeekData</c> work-unit provider makes — so the validation window can never drift from the load
/// window (design §3.2).</para>
///
/// <para><b>⚠ IT IS OBSERVATIONAL AND MUST NEVER THROW.</b> A row with a non-NULL
/// <c>ExpectedCount</c> that differs from <c>ActualCount</c> is an anomaly → <c>LogWarning</c>;
/// everything else is informational → <c>LogInformation</c>. With ~58 of 60 units legitimately
/// returning 404 on this monthly feed, a legitimately sparse window must not be able to fail an
/// otherwise-good load. It catches and logs its own exceptions (non-fatal) and only rethrows
/// <see cref="OperationCanceledException"/>. Several checks are informational <i>on purpose</i>
/// (fact↔location reconciliation both ways, zero-row issue dates, the priced-without-activity and
/// <c>USAVG</c>/<c>Region</c> quirks) — do not "promote" them.</para>
///
/// <para><b>Build-only:</b> coded and unit-testable this pass but not exercised against live data;
/// the deeper reconciliation is DATA_QUALITY_VALIDATOR's job and needs a live, loaded database.</para>
/// </summary>
public sealed class NgiLoadValidator
{
    private readonly NgiSettings _settings;
    private readonly ILogger<NgiLoadValidator> _logger;

    public NgiLoadValidator(IOptions<NgiSettings> settings, ILogger<NgiLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(LoaderRunContext context, CancellationToken cancellationToken)
    {
        // Same window derivation as NgiBidWeekWorkUnitProvider — one shared helper (design §3.2).
        var window = NgiTime.ResolveWindow(context.StartedAtUtc, _settings.DaysBack);
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

                // An "expected" value the actual count fails to match is a real anomaly (the proc sets
                // ExpectedCount only where one genuinely exists — e.g. LocationBlankKey,
                // PriceRangeOrdering, SurveyWindowOrdering, UnexpectedNulls, IssueDateMatchesRequest all
                // expect 0). Everything else is an informational counter.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "NGI validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "NGI validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "NGI validation complete for {From}..{To}: {Anomalies} anomaly row(s), {Info} informational row(s)",
                NgiTime.Iso(from), NgiTime.Iso(to), anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must surface as cancellation, not a non-fatal validation failure.
            throw;
        }
        catch (Exception ex)
        {
            // Observational only: a validation failure must not fail the load.
            _logger.LogError(ex, "NGI post-load validation failed to run (non-fatal)");
        }
    }
}
