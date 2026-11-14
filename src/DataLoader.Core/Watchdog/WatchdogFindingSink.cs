using DataLoader.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// Builds the closing hints for one pass, then records it through
/// <see cref="IWatchdogFindingStore"/>.
///
/// <para>Hints carry the facts the proc cannot see: whether a loader was disabled or
/// removed from config (OVERDUE / MISCONFIGURED), and whether a FAILED run that left the
/// look-back window self-healed. The FAILED decision runs the SAME
/// <see cref="WatchdogFailureResolver"/> the email uses, without the window, so the
/// resolution algorithm is never duplicated in T-SQL.</para>
///
/// <para>⚠ If that resolution cannot run, FAILED is treated as DEGRADED for this pass
/// (a <c>DEGRADED|FAILURE_RESOLUTION</c> finding, FAILED moved out of the checked
/// set) so no FAILED finding closes on a guess. The proc would otherwise default to
/// AgedOutUnresolved, which is safe, but would still be a closure nobody confirmed.</para>
/// </summary>
public sealed class WatchdogFindingSink : IWatchdogFindingSink
{
    private readonly IWatchdogFindingStore _store;
    private readonly IWatchdogRepository _repository;
    private readonly WatchdogSettings _settings;
    private readonly ILogger<WatchdogFindingSink> _logger;

    public WatchdogFindingSink(
        IWatchdogFindingStore store,
        IWatchdogRepository repository,
        IOptions<WatchdogSettings> settings,
        ILogger<WatchdogFindingSink> logger)
    {
        _store = store;
        _repository = repository;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Half the pass budget, so a slow resolution leaves time to record the pass.</summary>
    private TimeSpan ResolutionBudget =>
        TimeSpan.FromSeconds(Math.Max(1, _settings.PersistTimeoutSeconds / 2.0));

    public async Task<WatchdogPersistResult> RecordPassAsync(WatchdogPassRecord pass, CancellationToken cancellationToken)
    {
        var findings = pass.Findings.ToList();
        var signalsChecked = pass.SignalsChecked.ToList();
        var signalsDegraded = pass.SignalsDegraded.ToList();
        var status = pass.Status;
        IReadOnlyList<WatchdogResolutionHint> hints = Array.Empty<WatchdogResolutionHint>();

        // Nothing checked (a Failed pass) means nothing can close, so there is nothing to
        // hint about — and the database the view lives in is probably the thing that is down.
        if (signalsChecked.Count > 0)
        {
            var open = await _store.ReadOpenFindingsAsync(cancellationToken).ConfigureAwait(false);

            RecentFailuresResult? resolution = null;
            if (signalsChecked.Contains(WatchdogSignals.Failed))
            {
                var runIds = WatchdogFindingMapper.FailedRunIdsToResolve(open, findings);
                if (runIds.Count > 0)
                {
                    // ⚠ Its OWN sub-budget, shorter than the whole pass's: this read has no
                    // time window and can be slow, and if it ran into the pass budget the
                    // cancellation would lose the ENTIRE pass (no heartbeat row) instead of
                    // degrading just FAILED.
                    using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    resolveCts.CancelAfter(ResolutionBudget);
                    try
                    {
                        resolution = await _repository
                            .ResolveFailuresByRunIdAsync(runIds, resolveCts.Token)
                            .WaitAsync(ResolutionBudget, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex,
                            "Watchdog could not resolve {Count} open FAILED finding(s); FAILED is treated as " +
                            "degraded for this pass and nothing FAILED will be closed", runIds.Count);

                        var reason = ex is TimeoutException or OperationCanceledException
                            ? $"timed out after {ResolutionBudget.TotalSeconds:F0}s"
                            : $"{ex.GetType().Name}: {ErrorTextRedactor.Clean(ex.Message, WatchdogFindingMapper.MaxReasonChars)}";
                        findings.Add(WatchdogFindingMapper.DegradedRow(new DegradedCheck(
                            WatchdogDegradedCodes.FailureResolution,
                            $"failure resolution ({reason})",
                            reason)));
                        DegradeFailed();
                    }
                }
            }
            else
            {
                // FAILED could not be checked at all this pass, so FAILURE_RESOLUTION did not
                // get a chance to succeed either. Its absence would otherwise read as "the
                // check ran" (DEGRADED is checked) and close it NotDetected — silence
                // mistaken for health. Carry it forward until a resolution actually runs.
                const string key = WatchdogSignals.Degraded + "|" + WatchdogDegradedCodes.FailureResolution;
                if (open.Any(o => string.Equals(o.FindingKey, key, StringComparison.OrdinalIgnoreCase)) &&
                    !findings.Any(f => string.Equals(f.FindingKey, key, StringComparison.OrdinalIgnoreCase)))
                {
                    const string carried = "not re-attempted: FAILED could not be checked this pass";
                    findings.Add(WatchdogFindingMapper.DegradedRow(new DegradedCheck(
                        WatchdogDegradedCodes.FailureResolution, $"failure resolution ({carried})", carried)));
                }
            }

            hints = WatchdogFindingMapper.BuildHints(open, findings, _settings, resolution);
        }

        void DegradeFailed()
        {
            signalsChecked.Remove(WatchdogSignals.Failed);
            if (!signalsDegraded.Contains(WatchdogSignals.Failed))
                signalsDegraded.Add(WatchdogSignals.Failed);
            if (status == WatchdogPassStatus.Complete)
                status = WatchdogPassStatus.Degraded;
        }

        var record = pass with
        {
            Status = status,
            SignalsChecked = signalsChecked,
            SignalsDegraded = signalsDegraded,
            Findings = findings
        };

        return await _store.RecordPassAsync(record, hints, cancellationToken).ConfigureAwait(false);
    }
}
