using DataLoader.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// One watchdog pass: read history, evaluate, report, exit.
///
/// <para>This runs as a SEPARATE process from the loaders on its own schedule,
/// which is the entire point — in-process code cannot report its own death, so
/// a killed loader or a scheduled task that never fired is invisible from
/// inside the run it should have been.</para>
///
/// <para>Known limit: this still runs on the same machine, so it cannot report
/// that machine being down. Catching that needs something off-box.</para>
/// </summary>
public sealed class WatchdogRunner
{
    /// <summary>Keeps one degraded-signal reason readable in an email — a type
    /// name and a short, redacted slice of the message, not a full dump.</summary>
    private const int MaxDegradedReasonChars = 200;

    /// <summary>Floor on any persistence budget, so a typo of 0 cannot make it always time out.</summary>
    internal const int MinPersistBudgetSeconds = 2;

    /// <summary>Cap on persistence when the history read already failed — the DB-down alert waits on it.</summary>
    internal const int HistoryFailurePersistBudgetSeconds = 15;

    private readonly IWatchdogRepository _repository;
    private readonly IFailureNotifier _notifier;
    private readonly IWatchdogFindingSink _findingSink;
    private readonly WatchdogSettings _settings;
    private readonly ILogger<WatchdogRunner> _logger;

    public WatchdogRunner(
        IWatchdogRepository repository,
        IFailureNotifier notifier,
        IWatchdogFindingSink findingSink,
        IOptions<WatchdogSettings> settings,
        ILogger<WatchdogRunner> logger)
    {
        _repository = repository;
        _notifier = notifier;
        _findingSink = findingSink;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Test seam: the US CENTRAL wall clock for this pass. Null (production) reads the real
    /// clock. Exists because the <c>DATA_VOLUME</c> window depends on the Central time of
    /// day (<see cref="WatchdogDataVolumeSettings.SettleHours"/>).
    /// </summary>
    internal Func<DateTime>? CentralClock { get; init; }

    /// <summary>
    /// Exit code 0 even when loaders are overdue, or when a secondary check
    /// could not run but the alert about that still went out — finding and
    /// reporting problems is the job being done, not failing. 1 is reserved
    /// for "nobody was told": the one query the overdue calculation needs
    /// could not be read at all, or a built alert could not be delivered.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var nowCentral = CentralClock?.Invoke() ?? CentralNow();

        // GetLastRunsAsync is the one query the overdue calculation needs, and
        // it is the cheapest and least likely to fail — a single GROUP BY, no
        // date parameters. Without it the watchdog genuinely knows nothing, so
        // this is the only repository call still allowed to abort the pass.
        IReadOnlyList<LoaderLastRun> lastRuns;
        try
        {
            lastRuns = await _repository.GetLastRunsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog could not read run history");

            // Best effort, and almost certainly failing too when the platform DB is the
            // thing that is down — but when only the history query broke, this is what
            // makes the heartbeat show the pass happened. Nothing is checked, so nothing
            // closes (see WatchdogFindingMapper.ComputeSignals).
            //
            // ⚠ Shorter budget than a normal pass: the DB-down alert is waiting behind this,
            // and the database is probably the very thing that is unreachable.
            var footnote = await PersistAsync(
                () =>
                {
                    var historyCheck = DegradedReason(WatchdogDegradedCodes.HistoryQuery, "run history", ex);
                    var (noneChecked, allDegraded) = WatchdogFindingMapper.ComputeSignals(new[] { historyCheck.Code });
                    return new WatchdogPassRecord(
                        startedAtUtc,
                        Environment.MachineName,
                        WatchdogPassStatus.Failed,
                        noneChecked,
                        allDegraded,
                        new[] { WatchdogFindingMapper.DegradedRow(historyCheck) },
                        WatchdogFindingMapper.AppVersionFromBaseDirectory(AppContext.BaseDirectory));
                },
                Math.Min(HistoryFailurePersistBudgetSeconds, _settings.PersistTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            await NotifyDatabaseReadFailureAsync(ex, footnote).ConfigureAwait(false);
            return 1;
        }

        // The two secondary signals degrade INDEPENDENTLY of the primary one
        // and of each other. A transient failure here self-heals next pass; a
        // persistent one (e.g. schema drift breaking just this query) must not
        // mask overdue reporting behind a generic error forever — see
        // WatchdogReport.Degraded.
        var degraded = new List<DegradedCheck>();

        // A watchdog monitoring zero loaders reports "all healthy" forever,
        // which is indistinguishable from a working platform — exactly the
        // blindness this component exists to prevent, so it has to be able to
        // report on itself. This also catches a config binding failure
        // WatchdogEvaluator's own per-entry Misconfigured guard cannot see:
        // "Platform:Watchdog:Loaders" as a scalar string, or an entry whose
        // MaxGapHours is non-numeric, binds SILENTLY to an empty dictionary
        // (no exception) — the entries never land for that guard to inspect,
        // they just vanish.
        if (_settings.Loaders is null || _settings.Loaders.Count == 0)
        {
            const string reason =
                "no loaders are configured for monitoring (Platform:Watchdog:Loaders is empty or " +
                "failed to bind) — this pass can detect nothing";
            _logger.LogWarning(reason);
            degraded.Add(new DegradedCheck(WatchdogDegradedCodes.NoLoadersConfigured, reason, reason));
        }

        IReadOnlyList<StuckRun> stuck;
        try
        {
            stuck = await _repository
                .GetStuckRunsAsync(
                    _settings.StuckAfterHours,
                    _settings.StuckLookbackDays,
                    // Null means every host. Defaults to this one: most orphaned
                    // run rows in production were left by a developer laptop
                    // pointed at the production platform database, and a
                    // watchdog cannot act on another machine's run.
                    _settings.RestrictStuckRunsToThisHost ? Environment.MachineName : null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog could not check for stuck runs; continuing with the rest of the pass");
            degraded.Add(DegradedReason(WatchdogDegradedCodes.StuckQuery, "stuck runs", ex));
            stuck = Array.Empty<StuckRun>();
        }

        RecentFailuresResult failuresResult;
        try
        {
            failuresResult = await _repository
                .GetRecentFailuresAsync(
                    _settings.ReportRecentFailureHours, nowCentral, _settings.SkipAlertIfResolved, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog could not check for recent failures; continuing with the rest of the pass");
            degraded.Add(DegradedReason(WatchdogDegradedCodes.FailuresQuery, "recent failures", ex));
            failuresResult = new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>());
        }

        var failures = failuresResult.Failures;

        // Suppressed findings are logged, never silently dropped — core.LoadLog is the
        // only place an operator could otherwise see that a failure resolved itself. This
        // must happen whether or not anything else in this run notifies: a suppressed
        // finding is itself information worth having in Log\Watchdog.log even on an
        // all-clear pass.
        if (failuresResult.Suppressed.Count > 0)
        {
            _logger.LogInformation(
                "Watchdog: {Count} recent failure(s) suppressed as resolved (every failed work unit had a later success)",
                failuresResult.Suppressed.Count);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var s in failuresResult.Suppressed)
                {
                    _logger.LogDebug(
                        "Watchdog: suppressed as resolved — {LoaderName} {StartTime:yyyy-MM-dd HH:mm} Central, " +
                        "{UnitsFailed}/{UnitsTotal} units failed, all since succeeded",
                        s.LoaderName, s.StartTimeCentral, s.UnitsFailed, s.UnitsTotal);
                }
            }
        }

        // Deliberately OUTSIDE every catch above: a bug in evaluation itself
        // (e.g. a null Loaders section in config) is a config/evaluator
        // defect, not a database problem, and must not be mislabelled as
        // "Watchdog could not read run history" — WatchdogEvaluator defends
        // its own null-Loaders trigger directly instead of relying on a catch
        // here.
        //
        // hostName is supplied rather than read inside the report, so
        // WatchdogReport.BuildMessage() stays pure and testable.
        var (volumeInScope, volume) = await CheckDataVolumeAsync(nowCentral, degraded, cancellationToken)
            .ConfigureAwait(false);
        var notifyVolume = volumeInScope && _settings.DataVolume.Notify;

        // ⚠ With Notify off, a VOLUME_QUERY degradation is persisted (DEGRADED|VOLUME_QUERY,
        // pass Degraded) but kept OUT of the email — "persisted, not emailed" has to hold
        // for the signal's failures as much as for its findings, or an opt-out signal could
        // still start mailing people.
        var emailDegraded = degraded
            .Where(d => notifyVolume || d.Code != WatchdogDegradedCodes.VolumeQuery)
            .Select(d => d.EmailLine)
            .ToList();

        var report = WatchdogEvaluator.Evaluate(
            lastRuns, stuck, failures, _settings, Environment.MachineName, nowCentral, emailDegraded);

        if (notifyVolume && volume is { Anomalies.Count: > 0 })
            report = report.WithVolumeAnomalies(volume.Anomalies);

        _logger.LogInformation(
            "Watchdog: {Overdue} overdue, {Stuck} stuck, {Failures} recent failures, " +
            "{Suppressed} suppressed as resolved, {Misconfigured} misconfigured, {Degraded} degraded",
            report.Overdue.Count, report.Stuck.Count, report.RecentFailures.Count,
            failuresResult.Suppressed.Count, report.Misconfigured.Count, report.Degraded.Count);

        // Persist BEFORE notifying, so the email can say if it did not work. Fed from the
        // same report the email is built from; it changes nothing that is detected or sent.
        // Built lazily INSIDE PersistAsync's guard: a mapping bug must never cost the email,
        // and with PersistFindings off nothing here runs at all.
        var reportForPersistence = report;
        var persistenceWarning = await PersistAsync(
            () =>
            {
                var (signalsChecked, signalsDegraded) =
                    WatchdogFindingMapper.ComputeSignals(degraded.Select(d => d.Code), volumeInScope);
                return new WatchdogPassRecord(
                    startedAtUtc,
                    Environment.MachineName,
                    degraded.Count > 0 ? WatchdogPassStatus.Degraded : WatchdogPassStatus.Complete,
                    signalsChecked,
                    signalsDegraded,
                    WatchdogFindingMapper.BuildFindings(
                        reportForPersistence, failuresResult.Suppressed, degraded, _settings, volume?.Anomalies),
                    WatchdogFindingMapper.AppVersionFromBaseDirectory(AppContext.BaseDirectory))
                {
                    Volume = volume
                };
            },
            _settings.PersistTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (persistenceWarning is not null)
            report = report.WithPersistenceWarning(persistenceWarning);

        NotificationOutcome outcome;
        try
        {
            outcome = await _notifier.NotifyAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // IFailureNotifier is documented to never throw, and FailureNotifier
            // honours that — but a doc comment is not an enforcement mechanism
            // (PlatformHost.ReportRunOutcomeAsync makes the identical defensive
            // bet around its own call), and this is the watchdog's own last
            // line: nothing downstream will catch a throw here, so it must not
            // escape and crash the process instead of returning an exit code.
            _logger.LogError(ex, "Watchdog's own notification threw; treating the alert as undelivered");
            return 1;
        }

        // Disabled, NothingToReport, and a delivered DEGRADED report are all
        // fine — the pass completed, partially or not, and told someone,
        // including what it could not see. Only Failed means nobody was told.
        return outcome == NotificationOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// The fallback alert for when <see cref="IWatchdogRepository.GetLastRunsAsync"/>
    /// itself failed. An unreachable platform DB is itself worth an alert, and
    /// no loader run can report it — none of them got far enough to try.
    /// </summary>
    private async Task NotifyDatabaseReadFailureAsync(Exception ex, string? footnote)
    {
        // One clock read for both fields, not two — see RunFailureReport's
        // Run id/Duration omission, which keys off StartedAtUtc and
        // CompletedAtUtc being EQUAL. This is not a run with a duration; it is
        // a single instant the watchdog gave up at.
        var now = DateTime.UtcNow;
        var failureReport = new RunFailureReport
        {
            HostName = Environment.MachineName,
            RunId = Guid.Empty,
            StartedAtUtc = now,
            CompletedAtUtc = now,
            Exception = ex,
            FailedStage = "Watchdog",
            Footnote = footnote
        };

        try
        {
            var outcome = await _notifier.NotifyAsync(failureReport, CancellationToken.None).ConfigureAwait(false);

            // The exit code (1, from the caller) already carries this — this
            // line exists so "DB down AND nobody was paged about it" is
            // distinguishable in the log from "DB down, alert sent", which
            // otherwise read identically.
            if (outcome == NotificationOutcome.Failed)
                _logger.LogError("Watchdog could not read run history AND could not deliver the alert about it");
        }
        catch (Exception notifyEx)
        {
            // Same defensive bet as above: this is the catch path's own last
            // line, and a throwing notifier must not be the thing that crashes
            // the watchdog instead of returning the exit code that already
            // says "nobody was told".
            _logger.LogError(notifyEx, "Watchdog's fallback notification threw; treating the alert as undelivered");
        }
    }

    /// <summary>
    /// The <c>DATA_VOLUME</c> check (design §D7). Returns whether it was IN SCOPE this pass
    /// (enabled and a valid section — which decides whether <c>DATA_VOLUME</c> appears in
    /// the pass's signal lists at all) and the evaluated outcome, null unless both reads
    /// and the evaluation succeeded.
    ///
    /// <list type="bullet">
    /// <item>Disabled → (false, null) with NO query: the pass is byte-for-byte today's.</item>
    /// <item>Invalid section → Warning, (false, null): neither checked nor degraded, so
    /// nothing opens and nothing closes.</item>
    /// <item>No loaders configured → (true, null) without querying; the
    /// <c>NO_LOADERS_CONFIGURED</c> code already in <paramref name="degraded"/> blinds it.</item>
    /// <item>A read or the evaluation throws → <c>VOLUME_QUERY</c> added to
    /// <paramref name="degraded"/>, (true, null): degraded, so no open volume finding closes.</item>
    /// </list>
    /// </summary>
    private async Task<(bool InScope, WatchdogVolumeResult? Result)> CheckDataVolumeAsync(
        DateTime nowCentral, List<DegradedCheck> degraded, CancellationToken cancellationToken)
    {
        var settings = _settings.DataVolume;
        if (settings is not { Enabled: true }) return (false, null);

        var invalid = WatchdogVolumeEvaluator.ValidateSection(settings);
        if (invalid is not null)
        {
            _logger.LogWarning(
                "Watchdog: Platform:Watchdog:DataVolume is invalid ({Problems}); the row-volume check is skipped " +
                "this pass — nothing is opened or closed", invalid);
            return (false, null);
        }

        if (_settings.Loaders is null || _settings.Loaders.Count == 0) return (true, null);

        // ⚠ Central wall clock: LoaderRun_Summary.StartTime is Central, so "yesterday" must
        // be Central's yesterday, and the time of day decides whether yesterday has settled
        // (SettleHours) — before that the window ends the day before.
        var (from, to) = WatchdogVolumeEvaluator.QueryRange(settings, nowCentral);

        try
        {
            var volumes = await _repository.GetDailyVolumesAsync(from, to, cancellationToken).ConfigureAwait(false);
            var excluded = await _repository.GetVolumeExcludedDaysAsync(from, to, cancellationToken).ConfigureAwait(false);

            // Inside the catch on purpose, unlike WatchdogEvaluator.Evaluate: this is an
            // OPTIONAL signal, and an evaluation bug must degrade just DATA_VOLUME rather
            // than take the overdue/stuck/failed email down with it.
            var result = WatchdogVolumeEvaluator.Evaluate(volumes, excluded, _settings, nowCentral);

            foreach (var s in result.SkippedLoaders)
                _logger.LogWarning(
                    "Watchdog: row-volume check skipped for {Loader}: {Reason} (Platform:Watchdog:Loaders:{Loader}:Volume); " +
                    "its open findings are kept open", s.LoaderName, s.Reason, s.LoaderName);

            _logger.LogInformation(
                "Watchdog: {Anomalies} volume anomalies, {Unjudged} unjudged (window {WindowStart:yyyy-MM-dd}..{WindowEnd:yyyy-MM-dd} Central)",
                result.Anomalies.Count, result.Unjudged.Count,
                result.WindowStart.ToDateTime(TimeOnly.MinValue), result.WindowEnd.ToDateTime(TimeOnly.MinValue));

            if (result.Unjudged.Count > 0)
                _logger.LogInformation("Watchdog: row volume not judged — {Unjudged}",
                    string.Join("; ", result.Unjudged.Select(u => $"{u.LoaderName} {u.DayCentral:yyyy-MM-dd} ({u.Reason})")));

            return (true, result);
        }
        // A plain catch, like the stuck/failures reads: a cancellation here degrades
        // DATA_VOLUME and lets the rest of the pass (and its email) finish, rather than
        // aborting it.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog could not check row volumes; continuing with the rest of the pass");
            degraded.Add(DegradedReason(WatchdogDegradedCodes.VolumeQuery, "row volume", ex));
            return (true, null);
        }
    }

    /// <summary>
    /// One degraded-signal line: which check, and why, short enough to read in
    /// an email and redacted the same way every other piece of exception text
    /// reaching a notification is — see <see cref="ErrorTextRedactor"/>.
    /// </summary>
    private static DegradedCheck DegradedReason(string code, string signal, Exception ex)
    {
        var reason = $"{ex.GetType().Name}: {ErrorTextRedactor.Clean(ex.Message, MaxDegradedReasonChars)}";
        return new DegradedCheck(code, $"{signal} ({reason})", reason);
    }

    /// <summary>
    /// Records the pass through <see cref="IWatchdogFindingSink"/>. Returns null on
    /// success (or when <see cref="WatchdogSettings.PersistFindings"/> is off), else the
    /// one redacted line the email carries.
    ///
    /// <para>Never throws and never touches the exit code: persistence and email are
    /// independent, and exit 1 still means only "nobody was told". Bounded by
    /// <see cref="WatchdogSettings.PersistTimeoutSeconds"/>, because it runs before the
    /// email and must not hold an alert hostage.</para>
    /// </summary>
    private async Task<string?> PersistAsync(
        Func<WatchdogPassRecord> buildPass, int budgetSeconds, CancellationToken cancellationToken)
    {
        if (!_settings.PersistFindings) return null;

        var budget = TimeSpan.FromSeconds(Math.Max(MinPersistBudgetSeconds, budgetSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        Task<WatchdogPersistResult>? recording = null;
        try
        {
            var pass = buildPass();
            recording = _findingSink.RecordPassAsync(pass, cts.Token);

            // ⚠ WaitAsync makes the budget a HARD bound. The token alone is not one:
            // SqlConnection.OpenAsync reacts slowly to cancellation during TCP/login, so a
            // DB-down connect could otherwise hold the alert well past the budget. On
            // expiry the email goes out; the abandoned call finishes or faults on its own
            // (an interrupted proc call rolls back — it is one transaction).
            var result = await recording.WaitAsync(budget, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Watchdog pass {PassId} persisted: status {Status}, {Findings} finding(s) detected",
                result.PassId, pass.Status, pass.Findings.Count);

            if (!string.IsNullOrEmpty(result.RetentionError))
                _logger.LogWarning("Watchdog findings retention sweep failed (the pass itself was recorded): {Error}",
                    ErrorTextRedactor.Clean(result.RetentionError, MaxDegradedReasonChars));

            return null;
        }
        catch (Exception ex)
        {
            // Never leave a faulted task unobserved after we stopped waiting for it.
            if (recording is { IsCompleted: false })
                _ = recording.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

            string reason;
            if (cancellationToken.IsCancellationRequested)
                reason = "the watchdog was cancelled";
            else if (ex is TimeoutException || (ex is OperationCanceledException && cts.IsCancellationRequested))
                reason = $"timed out after {budget.TotalSeconds:F0}s";
            else
                reason = $"{ex.GetType().Name}: {ErrorTextRedactor.Clean(ex.Message, MaxDegradedReasonChars)}";

            _logger.LogError(ex, "Watchdog findings were NOT persisted: {Reason}", reason);
            return $"Findings were NOT persisted: {reason}";
        }
    }

    /// <summary>
    /// ⚠ Central, not UTC. <c>core.LoaderRun_Summary.StartTime</c> is stored
    /// already converted, so comparing it to a UTC clock would report every
    /// loader ~5 hours overdue.
    /// </summary>
    private static DateTime CentralNow()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            // The IANA id, for completeness — this platform runs on Windows.
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
    }
}
