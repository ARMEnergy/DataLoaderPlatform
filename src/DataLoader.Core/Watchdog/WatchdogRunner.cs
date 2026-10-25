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

    private readonly IWatchdogRepository _repository;
    private readonly IFailureNotifier _notifier;
    private readonly WatchdogSettings _settings;
    private readonly ILogger<WatchdogRunner> _logger;

    public WatchdogRunner(
        IWatchdogRepository repository,
        IFailureNotifier notifier,
        IOptions<WatchdogSettings> settings,
        ILogger<WatchdogRunner> logger)
    {
        _repository = repository;
        _notifier = notifier;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Exit code 0 even when loaders are overdue, or when a secondary check
    /// could not run but the alert about that still went out — finding and
    /// reporting problems is the job being done, not failing. 1 is reserved
    /// for "nobody was told": the one query the overdue calculation needs
    /// could not be read at all, or a built alert could not be delivered.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var nowCentral = CentralNow();

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
            await NotifyDatabaseReadFailureAsync(ex).ConfigureAwait(false);
            return 1;
        }

        // The two secondary signals degrade INDEPENDENTLY of the primary one
        // and of each other. A transient failure here self-heals next pass; a
        // persistent one (e.g. schema drift breaking just this query) must not
        // mask overdue reporting behind a generic error forever — see
        // WatchdogReport.Degraded.
        var degraded = new List<string>();

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
            degraded.Add(DegradedReason("stuck runs", ex));
            stuck = Array.Empty<StuckRun>();
        }

        IReadOnlyList<RecentFailure> failures;
        try
        {
            failures = await _repository
                .GetRecentFailuresAsync(_settings.ReportRecentFailureHours, nowCentral, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog could not check for recent failures; continuing with the rest of the pass");
            degraded.Add(DegradedReason("recent failures", ex));
            failures = Array.Empty<RecentFailure>();
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
        var report = WatchdogEvaluator.Evaluate(
            lastRuns, stuck, failures, _settings, Environment.MachineName, nowCentral, degraded);

        _logger.LogInformation(
            "Watchdog: {Overdue} overdue, {Stuck} stuck, {Failures} recent failures, {Misconfigured} misconfigured, {Degraded} degraded",
            report.Overdue.Count, report.Stuck.Count, report.RecentFailures.Count,
            report.Misconfigured.Count, report.Degraded.Count);

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
    private async Task NotifyDatabaseReadFailureAsync(Exception ex)
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
            FailedStage = "Watchdog"
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
    /// One degraded-signal line: which check, and why, short enough to read in
    /// an email and redacted the same way every other piece of exception text
    /// reaching a notification is — see <see cref="ErrorTextRedactor"/>.
    /// </summary>
    private static string DegradedReason(string signal, Exception ex) =>
        $"{signal} ({ex.GetType().Name}: {ErrorTextRedactor.Clean(ex.Message, MaxDegradedReasonChars)})";

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
