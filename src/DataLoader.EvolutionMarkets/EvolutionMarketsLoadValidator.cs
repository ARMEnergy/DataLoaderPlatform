using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Module-level post-load quality check (design §9), following the NGI/StormVista/AGSI/IIR precedent
/// (<c>LoaderPipelineBase</c> has no post-load hook, so <see cref="EvolutionMarketsModule"/> owns
/// it). Calls <c>arm.usp_ValidateLoad(@DateFrom, @DateTo)</c> for the run's business-date window and
/// logs the returned report (one result set, uniform shape <c>CheckName, Scope, ExpectedCount,
/// ActualCount, Detail</c>).
///
/// <para>The window comes from <see cref="EvoTime.ResolveWindow"/> — the <b>same call</b> the
/// work-unit provider makes — so the validation window can never drift from the load window
/// (design §3.2).</para>
///
/// <para><b>⚠ IT IS OBSERVATIONAL AND MUST NEVER THROW.</b> A row with a non-NULL
/// <c>ExpectedCount</c> that differs from <c>ActualCount</c> is an anomaly → <c>LogWarning</c>;
/// everything else is informational → <c>LogInformation</c>. A legitimately sparse window (weekends,
/// the July 4 cluster, the unexplained 2026-08-20 gap) must not be able to fail an otherwise-good
/// load. It catches and logs its own exceptions (non-fatal) and only rethrows
/// <see cref="OperationCanceledException"/>.</para>
///
/// <para>Several checks are informational <b>on purpose</b> — the always-NULL column census, the
/// distinct <c>PriceType</c>/<c>Currency</c> values, the composite-key cross-check and the
/// zero-row date count. Do not "promote" them: this dataset legitimately leaves 9 of 23 payload
/// columns empty, and a new <c>PriceType</c> value is news, not a fault.</para>
///
/// <para><b>Build-only:</b> coded and unit-testable this pass but not exercised against live data;
/// the deeper reconciliation is DATA_QUALITY_VALIDATOR's job and needs a live, loaded database.</para>
/// </summary>
public sealed class EvolutionMarketsLoadValidator
{
    private readonly EvoSettings _settings;
    private readonly ILogger<EvolutionMarketsLoadValidator> _logger;

    public EvolutionMarketsLoadValidator(
        IOptions<EvoSettings> settings, ILogger<EvolutionMarketsLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ValidateAsync(LoaderRunContext context, CancellationToken cancellationToken)
    {
        // Same window derivation as EvoMarketDataWorkUnitProvider — one shared helper (design §3.2).
        var window = EvoTime.ResolveWindow(context.StartedAtUtc, _settings.DaysBack);
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

                // The proc sets ExpectedCount only where one genuinely exists (e.g. NullKeyRows,
                // DuplicateCompositeKey, PriceOrdering, BusinessDateOutsideWindow, PriceTsDateMismatch
                // all expect 0). Everything else is an informational counter.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "EvolutionMarkets validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "EvolutionMarkets validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "EvolutionMarkets validation complete for {From}..{To}: {Anomalies} anomaly row(s), {Info} informational row(s)",
                EvoTime.Iso(from), EvoTime.Iso(to), anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must surface as cancellation, not a non-fatal validation failure.
            throw;
        }
        catch (Exception ex)
        {
            // Observational only: a validation failure must not fail the load.
            _logger.LogError(ex, "EvolutionMarkets post-load validation failed to run (non-fatal)");
        }
    }
}
