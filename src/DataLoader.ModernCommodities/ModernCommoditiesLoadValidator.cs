using System.Data;
using DataLoader.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.ModernCommodities;

/// <summary>
/// Module-level post-load quality check (design §9), following the StormVista/AGSI/IIR/NGI
/// precedent (<c>LoaderPipelineBase</c> has no post-load hook and
/// <see cref="ModernCommoditiesModule"/> owns orchestration). Calls
/// <c>arm.usp_ValidateLoad(@DateFrom, @DateTo, @ModifiedSinceUtc)</c> after all enabled pipelines
/// complete and logs the returned report (ONE result set, uniform shape
/// <c>CheckName, Scope, ExpectedCount, ActualCount, Detail</c>).
///
/// <para>The window comes from <see cref="ModComTime.ResolveWindow"/> — the <b>same helper</b> every
/// work-unit provider calls — so the validation window can never drift from the load window. Because
/// <c>DaysBack</c> and the history clamp are <b>per-endpoint</b> (decision D10), the validator passes
/// the <b>union</b>: <c>@DateTo = today</c>, <c>@DateFrom = MIN(start)</c> across the <i>enabled</i>
/// endpoints, each with its own <c>HistoryLimited</c> flag applied. At the shipped defaults all three
/// windows are identical, so the union <i>is</i> the window — an inclusive <b>31</b> calendar days at
/// <c>DaysBack = 30</c> (decision D13; do not "fix" that into a silent one-day gap).</para>
///
/// <para><c>@ModifiedSinceUtc = context.StartedAtUtc − ValidationClockSkewMinutes</c>. The tolerance
/// exists because fact rows are stamped by the <b>SQL Server's</b> <c>SYSUTCDATETIME()</c> while the
/// run start comes from the <b>host's</b> clock; without it a few seconds of skew would make every
/// freshness check read zero (design §6.3).</para>
///
/// <para><b>⚠ IT IS OBSERVATIONAL AND MUST NEVER THROW.</b> A row with a non-NULL
/// <c>ExpectedCount</c> that differs from <c>ActualCount</c> is an anomaly → <c>LogWarning</c>;
/// everything else is informational → <c>LogInformation</c>. A legitimately sparse window — a quiet
/// <c>myTrades</c> month, a weekend-only settlements window, or a run whose trades recency guard
/// updated nothing — must not be able to fail an otherwise-good load. It catches and logs its own
/// exceptions (non-fatal) and only rethrows <see cref="OperationCanceledException"/>. Several checks
/// are informational <i>on purpose</i> (the 100 %-NULL anonymised columns, the never-populated
/// <c>ClearingID</c>, the intended cross-table <c>TradeNumber</c> overlap, the expected-NON-ZERO
/// "executed outside window" revision signal) — <b>do not "promote" them</b>.</para>
///
/// <para><b>Build-only:</b> coded and unit-testable this pass but not exercised against live data;
/// the deeper reconciliation is DATA_QUALITY_VALIDATOR's job and needs a live, loaded database
/// (decision D12).</para>
/// </summary>
public sealed class ModernCommoditiesLoadValidator
{
    private readonly ModComSettings _settings;
    private readonly ILogger<ModernCommoditiesLoadValidator> _logger;

    public ModernCommoditiesLoadValidator(IOptions<ModComSettings> settings, ILogger<ModernCommoditiesLoadValidator> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <param name="enabledEndpoints">
    /// The endpoints that actually ran, so the union window reflects only their effective
    /// <c>DaysBack</c>/history clamp. An empty list falls back to all three descriptors.
    /// </param>
    public async Task ValidateAsync(
        LoaderRunContext context, IReadOnlyList<ModComEndpointDescriptor> enabledEndpoints, CancellationToken cancellationToken)
    {
        var (from, to) = ResolveUnionWindow(context, enabledEndpoints);
        var modifiedSinceUtc = context.StartedAtUtc.AddMinutes(-Math.Max(0, _settings.ValidationClockSkewMinutes));

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("arm.usp_ValidateLoad", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@DateFrom", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@DateTo", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@ModifiedSinceUtc", SqlDbType.DateTime2, 3).Value = modifiedSinceUtc;

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

                // The proc sets ExpectedCount ONLY where a genuine expectation exists (UnkeyableDrops,
                // PkDuplicates, TermOrdering, SettlementSentinelPairing and FileLogFailuresInWindow all
                // expect 0). Everything else is an informational counter by design.
                if (expected.HasValue && expected.Value != actual)
                {
                    anomalies++;
                    _logger.LogWarning(
                        "ModernCommodities validation anomaly: {Check} scope={Scope} expected={Expected} actual={Actual} {Detail}",
                        check, scope ?? "-", expected, actual, detail ?? string.Empty);
                }
                else
                {
                    informational++;
                    _logger.LogInformation(
                        "ModernCommodities validation: {Check} scope={Scope} count={Actual} {Detail}",
                        check, scope ?? "-", actual, detail ?? string.Empty);
                }
            }

            _logger.LogInformation(
                "ModernCommodities validation complete for {From}..{To} (modifiedSinceUtc={Since:O}): {Anomalies} anomaly row(s), {Info} informational row(s)",
                ModComTime.Iso(from), ModComTime.Iso(to), modifiedSinceUtc, anomalies, informational);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run must surface as cancellation, not a non-fatal validation failure.
            throw;
        }
        catch (Exception ex)
        {
            // Observational only: a validation failure must not fail the load.
            _logger.LogError(ex, "ModernCommodities post-load validation failed to run (non-fatal)");
        }
    }

    /// <summary>
    /// The union of the enabled endpoints' windows: <c>@DateTo = today</c> and
    /// <c>@DateFrom = MIN(start)</c>, each computed through the shared
    /// <see cref="ModComTime.ResolveWindow"/> with that endpoint's own effective <c>DaysBack</c> and
    /// <c>HistoryLimited</c> flag. <b>Do not recompute this arithmetic anywhere else.</b>
    /// </summary>
    internal (DateOnly From, DateOnly To) ResolveUnionWindow(
        LoaderRunContext context, IReadOnlyList<ModComEndpointDescriptor> enabledEndpoints)
    {
        var descriptors = enabledEndpoints.Count > 0 ? enabledEndpoints : ModComDescriptors.All;

        DateOnly? from = null;
        var to = DateOnly.FromDateTime(context.StartedAtUtc);

        foreach (var d in descriptors)
        {
            var w = ModComTime.ResolveWindow(context.StartedAtUtc, _settings.EffectiveDaysBack(d.EndpointId), d.HistoryLimited);
            to = w.End;
            if (from is null || w.Start < from.Value) from = w.Start;
        }

        return (from ?? to, to);
    }
}
