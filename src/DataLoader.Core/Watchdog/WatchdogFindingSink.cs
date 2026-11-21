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
///
/// <para><c>DATA_VOLUME</c> hints come from <see cref="WatchdogFindingMapper.BuildVolumeHints"/>
/// using the outcome the runner attached as <see cref="WatchdogPassRecord.Volume"/>. The one
/// volume decision made HERE is design §A4 (master switch off with findings still open),
/// because only the sink reads the open view.</para>
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

            // Same idea for DEGRADED|VOLUME_QUERY: on a pass where the volume reads never ran
            // (invalid section, empty Loaders section) the code is simply absent, which would
            // close it NotDetected while DEGRADED is checked — "fixed" by not trying. Carry it
            // forward until a pass actually reads successfully (pass.Volume set) or fails
            // again (the runner adds a fresh one). The one exception is the master switch
            // turned OFF: nobody is asking for this check any more, so let it close.
            if (pass.Volume is null &&
                _settings.DataVolume is { Enabled: true } &&
                !signalsDegraded.Contains(WatchdogSignals.DataVolume))
            {
                const string key = WatchdogSignals.Degraded + "|" + WatchdogDegradedCodes.VolumeQuery;
                if (open.Any(o => string.Equals(o.FindingKey, key, StringComparison.OrdinalIgnoreCase)) &&
                    !findings.Any(f => string.Equals(f.FindingKey, key, StringComparison.OrdinalIgnoreCase)))
                {
                    const string carried = "not re-attempted: the row-volume check did not run this pass";
                    findings.Add(WatchdogFindingMapper.DegradedRow(new DegradedCheck(
                        WatchdogDegradedCodes.VolumeQuery, $"row volume ({carried})", carried)));
                }
            }

            hints = WatchdogFindingMapper.BuildHints(open, findings, _settings, resolution);

            // DATA_VOLUME (design §A3/§A4). The runner put it in SignalsChecked only if the
            // check ran and its reads succeeded, and attached the outcome as pass.Volume.
            var masterSwitchOff = MasterSwitchOffWithLeftovers(open, signalsChecked);
            if (masterSwitchOff)
                signalsChecked.Add(WatchdogSignals.DataVolume);

            if (signalsChecked.Contains(WatchdogSignals.DataVolume))
            {
                var volumeHints = WatchdogFindingMapper.BuildVolumeHints(
                    open, findings, _settings, pass.Volume, masterSwitchOff);
                if (volumeHints.Count > 0)
                    hints = hints.Concat(volumeHints).ToList();
            }
        }

        // §A4: the master switch is off but findings it opened are still open. Close them as
        // LoaderDisabled by declaring DATA_VOLUME checked for this pass. When none are open
        // this is false and the pass is byte-for-byte what it was before the signal existed.
        //
        // ⚠ Not when the Loaders section is empty: that is the same binding failure
        // NO_LOADERS_CONFIGURED reports, and a section that failed to bind leaves
        // DataVolume.Enabled at its default (false) too — so "switched off" cannot be told
        // apart from "config did not load", and nothing closes on a guess.
        bool MasterSwitchOffWithLeftovers(IReadOnlyList<OpenWatchdogFinding> open, List<string> checkedSoFar) =>
            _settings.DataVolume is not { Enabled: true } &&
            _settings.Loaders is { Count: > 0 } &&
            !checkedSoFar.Contains(WatchdogSignals.DataVolume) &&
            !signalsDegraded.Contains(WatchdogSignals.DataVolume) &&
            open.Any(o => o.Signal == WatchdogSignals.DataVolume);

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
