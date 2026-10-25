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
    /// Exit code 0 even when loaders are overdue — finding them is the job
    /// being done, not failing. 1 only when the watchdog itself could not
    /// complete: it could not read the database, or it found something and
    /// could not deliver it.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var nowCentral = CentralNow();

        WatchdogReport report;

        try
        {
            var lastRuns = await _repository.GetLastRunsAsync(cancellationToken).ConfigureAwait(false);

            var stuck = await _repository
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

            var failures = await _repository
                .GetRecentFailuresAsync(_settings.ReportRecentFailureHours, nowCentral, cancellationToken)
                .ConfigureAwait(false);

            // hostName is supplied rather than read inside the report, so
            // WatchdogReport.BuildMessage() stays pure and testable.
            report = WatchdogEvaluator.Evaluate(
                lastRuns, stuck, failures, _settings, Environment.MachineName, nowCentral);

            _logger.LogInformation(
                "Watchdog: {Overdue} overdue, {Stuck} stuck, {Failures} recent failures, {Misconfigured} misconfigured",
                report.Overdue.Count, report.Stuck.Count, report.RecentFailures.Count, report.Misconfigured.Count);
        }
        catch (Exception ex)
        {
            // An unreachable platform DB is itself worth an alert, and no loader
            // run can report it — none of them got far enough to try.
            _logger.LogError(ex, "Watchdog could not read run history");

            var failureReport = new RunFailureReport
            {
                HostName = Environment.MachineName,
                RunId = Guid.Empty,
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Exception = ex,
                FailedStage = "Watchdog"
            };
            await _notifier.NotifyAsync(failureReport, CancellationToken.None).ConfigureAwait(false);
            return 1;
        }

        var outcome = await _notifier.NotifyAsync(report, CancellationToken.None).ConfigureAwait(false);

        // Disabled and NothingToReport are both fine. Failed is not: an
        // undelivered watchdog alert looks exactly like all-clear.
        return outcome == NotificationOutcome.Failed ? 1 : 0;
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
