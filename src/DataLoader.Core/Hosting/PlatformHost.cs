using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Configuration;
using DataLoader.Core.Notifications;
using DataLoader.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Hosting;

/// <summary>
/// Runs the discovered loader modules. The host's <c>Program.cs</c> resolves
/// this and calls <see cref="RunAsync"/>.
///
/// This class owns:
///   - filtering loaders by the <c>EnabledLoaders</c> config
///   - opening/closing the <c>core.LoaderRun</c> row
///   - running enabled loaders with bounded parallelism
///   - aggregating results
/// </summary>
public sealed class PlatformHost
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<ILoaderModule> _modules;
    private readonly ILoadLogRepository _runLog;
    private readonly ILoaderOverlapGuard _overlapGuard;
    private readonly IAdditionalProcessRunner _additionalProcesses;
    private readonly ILoadLogSweeper? _loadLogSweeper;
    private readonly IFailureNotifier? _notifier;
    private readonly PlatformSettings _settings;
    private readonly ILogger<PlatformHost> _logger;

    public PlatformHost(
        IServiceProvider services,
        IReadOnlyList<ILoaderModule> modules,
        ILoadLogRepository runLog,
        ILoaderOverlapGuard overlapGuard,
        IAdditionalProcessRunner additionalProcesses,
        IOptions<PlatformSettings> settings,
        ILogger<PlatformHost> logger,
        ILoadLogSweeper? loadLogSweeper = null,
        IFailureNotifier? notifier = null)
    {
        _services = services;
        _modules = modules;
        _runLog = runLog;
        _overlapGuard = overlapGuard;
        _additionalProcesses = additionalProcesses;
        _loadLogSweeper = loadLogSweeper;
        _notifier = notifier;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Closes <c>core.LoadLog</c> rows left open by EARLIER runs, so the table has no
    /// permanently-open rows. Those arise when <c>usp_BeginLoadLog</c>'s INSERT commits but the
    /// client never receives the id back (a command timeout or dropped connection), or when the
    /// process is killed mid-unit — in both cases nothing holds the handle needed to close the row.
    ///
    /// <para>Best-effort and never fatal: housekeeping must not stop a run from loading data. Rows
    /// belonging to a still-running invocation are never touched — the proc only closes rows whose
    /// own run has finished or started outside the staleness window.</para>
    ///
    /// <para>Returns how many rows were closed. A non-zero count is itself reportable —
    /// it means an earlier invocation was killed — so the caller forwards it into the
    /// run's failure report rather than only logging it.</para>
    /// </summary>
    private async Task<int> SweepStaleLoadLogsAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!_settings.AbandonStaleLoadLogs || _loadLogSweeper is null) return 0;

        try
        {
            var closed = await _loadLogSweeper.AbandonStaleAsync(runId, cancellationToken).ConfigureAwait(false);
            if (closed > 0)
                _logger.LogWarning(
                    "Closed {Count} load-log row(s) left open by earlier runs — a unit opened its row " +
                    "but never closed it (see core.usp_AbandonStaleLoadLogs)", closed);
            return closed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sweep stale load-log rows; continuing with the run");
            return 0;
        }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var enabled = SelectEnabledModules();
        if (enabled.Count == 0)
        {
            _logger.LogWarning("No loaders enabled — nothing to do");
            return 0;
        }

        _logger.LogInformation("Run {RunId} starting with {Count} loaders: {Names}",
            runId, enabled.Count, string.Join(", ", enabled.Select(m => m.LoaderId)));

        await _runLog.BeginRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var staleRowsClosed = await SweepStaleLoadLogsAsync(runId, cancellationToken).ConfigureAwait(false);

        var context = BuildContext(runId, startedAt, cancellationToken);
        var results = new List<LoaderRunResult>();
        var resultsLock = new object();
        var allSucceeded = false;

        // From here down the core.LoaderRun row is OPEN, so everything is guarded by
        // the finally. Without it a cancellation (Ctrl+C, SIGTERM, service stop) is
        // rethrown by ParallelRunner, propagates straight past CompleteRunAsync, and
        // strands the run row with CompletedAtUtc NULL forever — a graceful,
        // non-crash exit that still leaks. 24 of 844 production run rows were
        // stranded that way before this try/finally existed.
        try
        {
            await ParallelRunner.RunAsync(
                enabled,
                _settings.MaxConcurrentLoaders,
                async (module, ct) =>
                {
                    var ctx = new LoaderRunContext
                    {
                        RunId = runId,
                        StartedAtUtc = startedAt,
                        DateFrom = context.DateFrom,
                        DateTo = context.DateTo,
                        CancellationToken = ct
                    };

                    // Seeded, not left unassigned: this is what gets recorded if anything
                    // below throws before producing a real result (TryAcquireAsync, or the
                    // lock's own disposal). A loader must never silently vanish from
                    // `results` — All(...) over an empty list returns TRUE, so a lost entry
                    // could report the whole run successful while a loader never ran.
                    var result = LoaderRunResult.Failed(
                        module.LoaderId, "Did not complete", TimeSpan.Zero);
                    try
                    {
                        // Overlap guard — if another process is already running this
                        // loader, skip rather than racing. The next scheduled run
                        // will pick up whatever the current run leaves behind
                        // (idempotent thanks to the load log).
                        await using var lockHandle = await _overlapGuard
                            .TryAcquireAsync(module.LoaderId, ct).ConfigureAwait(false);

                        if (lockHandle is null)
                        {
                            result = new LoaderRunResult
                            {
                                LoaderId = module.LoaderId,
                                Success = true,
                                ErrorMessage = "Skipped — another instance already running",
                                Duration = TimeSpan.Zero
                            };
                            return;   // the finally below still records `result`
                        }

                        try
                        {
                            result = await module.RunAsync(_services, ctx).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Loader {Loader} threw out of RunAsync", module.LoaderId);
                            result = LoaderRunResult.Failed(module.LoaderId, ex.Message, TimeSpan.Zero);
                        }

                        // Post-run hook: core.usp_RunAdditionalProcesses(@LoaderName, @RunId).
                        //
                        // Runs after success AND after failure — partial data still landed,
                        // so post-processing is still wanted. Still INSIDE the overlap lock,
                        // so it can never run concurrently with another process's run of the
                        // same loader. Deliberately NOT reached when the guard skipped this
                        // loader (we returned above — that other process will run the hook
                        // itself) nor when the run is cancelled: the operator asked to stop,
                        // and the hook is new work, not cleanup.
                        //
                        // Note the explicit cancellation check. `catch (Exception)` above
                        // swallows OperationCanceledException too, so a cancelled module
                        // arrives here looking merely failed.
                        if (!ct.IsCancellationRequested)
                            await _additionalProcesses
                                .RunAsync(module.LoaderId, runId, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (resultsLock) results.Add(result);
                    }
                },
                cancellationToken
            ).ConfigureAwait(false);

            allSucceeded = results.All(r => r.Success);
        }
        finally
        {
            // CancellationToken.None — closing the run row must not itself be
            // cancellable. On a cancelled or faulted run `allSucceeded` is still
            // false, which is the honest outcome.
            await _runLog.CompleteRunAsync(runId, allSucceeded, CancellationToken.None).ConfigureAwait(false);
        }

        var elapsed = sw.Elapsed;
        LogSummary(results, elapsed);
        await ReportRunOutcomeAsync(runId, startedAt, elapsed, results, staleRowsClosed).ConfigureAwait(false);
        return allSucceeded ? 0 : 1;
    }

    private IReadOnlyList<ILoaderModule> SelectEnabledModules()
    {
        if (_settings.EnabledLoaders.Count == 0)
            return _modules;
        var set = new HashSet<string>(_settings.EnabledLoaders, StringComparer.OrdinalIgnoreCase);
        return _modules.Where(m => set.Contains(m.LoaderId)).ToList();
    }

    private LoaderRunContext BuildContext(Guid runId, DateTime startedAt, CancellationToken ct)
    {
        DateTime? from = null, to = null;
        if (_settings.DefaultDaysBackStart.HasValue)
            from = startedAt.Date.AddDays(-_settings.DefaultDaysBackStart.Value);
        if (_settings.DefaultDaysBackEnd.HasValue)
            to = startedAt.Date.AddDays(-_settings.DefaultDaysBackEnd.Value);

        return new LoaderRunContext
        {
            RunId = runId, StartedAtUtc = startedAt, DateFrom = from, DateTo = to,
            CancellationToken = ct
        };
    }

    private void LogSummary(IReadOnlyList<LoaderRunResult> results, TimeSpan elapsed)
    {
        _logger.LogInformation("=== Run summary ({Elapsed}) ===", elapsed);
        foreach (var r in results)
        {
            _logger.LogInformation(
                "  {Loader}: {Status} — {OK}/{Total} ok, {Skip} skipped, {Fail} failed, {Recs} records",
                r.LoaderId, r.Success ? "OK" : "FAIL",
                r.WorkUnitsSucceeded, r.WorkUnitsTotal, r.WorkUnitsSkipped, r.WorkUnitsFailed, r.RecordsProcessed);
            if (!r.Success && !string.IsNullOrEmpty(r.ErrorMessage))
                _logger.LogWarning("    error: {Error}", r.ErrorMessage);
        }
    }

    /// <summary>
    /// Emails the run's outcome when there is something to say.
    ///
    /// <para>Placed AFTER the finally that closes <c>core.LoaderRun</c>, not inside it:
    /// that row should close as promptly as possible and SMTP is not prompt.</para>
    ///
    /// <para>Never reached on cancellation — <c>ParallelRunner</c> rethrows
    /// <see cref="OperationCanceledException"/>, so a cancelled run leaves via the
    /// finally. That is deliberate. An operator pressing Ctrl+C or ending the scheduled
    /// task is not a failure; a KILLED process is, and nothing in-process can report
    /// that, which is what the watchdog exists for.</para>
    ///
    /// <para>The try/catch is belt and braces. <c>FailureNotifier</c> already
    /// swallows everything, but a run that loaded millions of rows must not be reported
    /// as failed because a mail relay was down, so the host does not rely on another
    /// component's politeness.</para>
    /// </summary>
    private async Task ReportRunOutcomeAsync(
        Guid runId, DateTime startedAt, TimeSpan elapsed, IReadOnlyList<LoaderRunResult> results, int staleRowsClosed)
    {
        if (_notifier is null) return;

        try
        {
            var report = new RunFailureReport
            {
                HostName = Environment.MachineName,
                RunId = runId,
                StartedAtUtc = startedAt,
                // Derived from the same Stopwatch LogSummary used, not a fresh DateTime.UtcNow
                // call — one clock describing one run, so an NTP correction mid-run (plausible
                // across this platform's documented multi-hour EIA/NUCORE backfills) cannot make
                // the log and the email disagree about how long it took.
                CompletedAtUtc = startedAt + elapsed,
                Results = results,
                StaleRowsClosed = staleRowsClosed
            };

            // CancellationToken.None — an operator stopping the job should still
            // learn why it was failing.
            await _notifier.NotifyAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure notification threw; the run's own outcome is unchanged");
        }
    }
}
