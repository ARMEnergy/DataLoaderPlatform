using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// The EIA ETL loop.
///
/// <para>
/// <b>Why this does not use <c>LoaderPipelineBase</c>.</b> That base implements
/// read-all → transform-all → write-once, which is the right shape for most loaders
/// here and the wrong one for this source: one window of <c>international</c> annual is
/// ~557,000 rows and one of <c>electricity/rto/fuel-type-data</c> hourly is ~296,000,
/// with <c>MaxConcurrentWorkUnits</c> of them in flight. Materialising a unit before
/// writing would hold hundreds of megabytes of row objects for no benefit. The loop
/// below keeps the base's structure exactly — idempotency through
/// <see cref="ILoadLogRepository"/>, bounded concurrency through
/// <see cref="ParallelRunner"/>, a per-unit timeout, the same counters — but its body
/// streams pages into the sink in batches. Same reasoning, same shape, as
/// <c>OilXPipeline</c>.
/// </para>
/// <para>
/// <b>RecordsProcessed is rows CHANGED, not rows read.</b> The merge is
/// change-guarded on <c>DataHash</c>, so re-verifying a window where nothing moved
/// reports 0. That is a healthy success, not an empty read.
/// </para>
/// </summary>
internal sealed class EiaPipeline : ILoaderPipeline
{
    private readonly EiaWorkUnitProvider _units;
    private readonly EiaSourceReader _reader;
    private readonly EiaSink _sink;
    private readonly ILoadLogRepository _loadLog;
    private readonly EiaSettings _settings;
    private readonly ILogger<EiaPipeline> _logger;

    public EiaPipeline(
        EiaWorkUnitProvider units,
        EiaSourceReader reader,
        EiaSink sink,
        ILoadLogRepository loadLog,
        IOptions<EiaSettings> settings,
        ILogger<EiaPipeline> logger)
    {
        _units = units;
        _reader = reader;
        _sink = sink;
        _loadLog = loadLog;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context)
    {
        var sw = Stopwatch.StartNew();

        IReadOnlyList<EiaWorkUnit> units;
        try
        {
            units = await _units.GetWorkUnitsAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EIA: failed to enumerate work units");
            return LoaderRunResult.Failed(EiaModule.Id, EiaHttp.Redact(ex.Message), sw.Elapsed);
        }

        if (units.Count == 0)
        {
            _logger.LogWarning("EIA: no work units to process");
            return new LoaderRunResult { LoaderId = EiaModule.Id, Success = true, Duration = sw.Elapsed };
        }

        var counters = new Counters(units.Count, _logger);

        await ParallelRunner.RunAsync(
            units,
            _settings.MaxConcurrentWorkUnits,
            (unit, ct) => ProcessOneAsync(unit, context, counters, ct),
            context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "EIA: {Total} unit(s) — {OK} ok, {Skip} skipped, {Fail} failed; " +
            "{Read} row(s) read, {Changed} changed in {Elapsed}",
            units.Count, counters.Succeeded, counters.Skipped, counters.Failed,
            counters.RowsRead, counters.RowsChanged, sw.Elapsed);

        return new LoaderRunResult
        {
            LoaderId = EiaModule.Id,
            Success = counters.Failed == 0,
            WorkUnitsTotal = units.Count,
            WorkUnitsSucceeded = counters.Succeeded,
            WorkUnitsSkipped = counters.Skipped,
            WorkUnitsFailed = counters.Failed,
            RecordsProcessed = counters.RowsChanged,
            Duration = sw.Elapsed
        };
    }

    private async Task ProcessOneAsync(
        EiaWorkUnit unit, LoaderRunContext context, Counters counters, CancellationToken ct)
    {
        using var unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_settings.WorkUnitTimeoutSeconds > 0)
            unitCts.CancelAfter(TimeSpan.FromSeconds(_settings.WorkUnitTimeoutSeconds));
        var unitCt = unitCts.Token;

        // 1. Idempotency. A SETTLED window already in core.LoadLog returns null here
        //    and costs nothing further -- no request, no parse, no merge. That is the
        //    entire mechanism that keeps a daily run from re-pulling ~90M rows.
        LoadLogHandle? handle;
        try
        {
            handle = await _loadLog
                .BeginAsync(EiaModule.Id, context.RunId, unit.Key, unit.DisplayName, unitCt)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            _logger.LogError(ex, "EIA: failed to open load log for {Unit}", unit.DisplayName);
            return;
        }

        if (handle is null)
        {
            counters.IncrementSkipped();
            _logger.LogDebug("EIA: skipping {Unit} — already loaded", unit.DisplayName);
            return;
        }

        try
        {
            var result = await _reader
                .LoadAsync(unit, (batch, token) => _sink.WriteAsync(batch, token), unitCt)
                .ConfigureAwait(false);

            await _loadLog.CompleteSuccessAsync(handle, result.RowsChanged, unitCt).ConfigureAwait(false);
            counters.IncrementSucceeded(result.RowsRead, result.RowsChanged);

            _logger.LogDebug(
                "EIA {Unit}: {Read} read / {Changed} changed over {Requests} request(s) and {Splits} " +
                "split(s), API total {Total}",
                unit.DisplayName, result.RowsRead, result.RowsChanged,
                result.Requests, result.Subdivisions, result.ReportedTotal);
        }
        catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            // Two very different failures arrive as the same exception type; telling
            // them apart is the difference between tuning the right setting and the
            // wrong one. Same distinction LoaderPipelineBase draws.
            counters.IncrementFailed();

            var reason = unitCts.IsCancellationRequested
                ? $"Work unit timed out after {_settings.WorkUnitTimeoutSeconds}s (WorkUnitTimeoutSeconds). " +
                  "A window this large may need a smaller Loaders:EIA:WindowSteps for its frequency."
                : ex.InnerException is TimeoutException
                    ? "HTTP request timed out (HttpTimeoutSeconds); the work unit budget was not reached"
                    : "Cancelled before completion (not the work unit budget)";

            await TryFailAsync(handle, reason).ConfigureAwait(false);
            _logger.LogWarning("EIA {Unit}: {Reason}", unit.DisplayName, reason);
        }
        catch (OperationCanceledException)
        {
            await TryFailAsync(handle, "Run cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Polly.Timeout.TimeoutRejectedException)
        {
            // Polly's own message is "The delegate executed asynchronously through
            // TimeoutPolicy did not complete within the timeout" — which names neither
            // the unit, the budget, nor the setting, and 17 of them landed in
            // core.LoadLog looking like an internal fault. Say what actually happened.
            counters.IncrementFailed();

            var reason =
                $"Every attempt exceeded the {_settings.HttpTimeoutSeconds}s per-request budget " +
                $"(Loaders:EIA:HttpTimeoutSeconds) across {_settings.RetryCount + 1} attempts. The " +
                "vendor was not answering, rather than answering with an error.";

            await TryFailAsync(handle, reason).ConfigureAwait(false);
            _logger.LogWarning("EIA {Unit}: {Reason}", unit.DisplayName, reason);
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();

            // 🔒 Redacted: an HttpRequestException can carry the request URI, and every
            // URI in this loader holds the api_key in its query string.
            var message = EiaHttp.Redact(ex.Message);
            await TryFailAsync(handle, message).ConfigureAwait(false);

            if (ex is EiaApiException { Permanent: true })
                _logger.LogError("EIA {Unit} failed permanently — {Message}", unit.DisplayName, message);
            else
                _logger.LogError(ex, "EIA {Unit} failed — {Message}", unit.DisplayName, message);
        }
    }

    private async Task TryFailAsync(LoadLogHandle handle, string message)
    {
        try
        {
            await _loadLog.CompleteFailureAsync(handle, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EIA: failed to close load log {Id} as failure", handle.LoadLogId);
        }
    }

    /// <summary>
    /// Run counters, plus the periodic progress line.
    ///
    /// <para>
    /// ⚠ <b>Why this exists.</b> A full backfill runs for many hours. The first version
    /// logged every HTTP retry at Warning and every unit SUCCESS at Debug, so an
    /// operator watching a 17-hour run that was 89% complete and succeeding 99% of the
    /// time saw nothing but a wall of 504 warnings — and reasonably concluded it was
    /// broken. Retries are routine against this vendor; progress is the thing worth
    /// saying out loud. Both were the wrong way round.
    /// </para>
    /// </summary>
    private sealed class Counters
    {
        private readonly int _total;
        private readonly ILogger _logger;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly object _reportGate = new();

        private int _succeeded;
        private int _skipped;
        private int _failed;
        private int _rowsRead;
        private int _rowsChanged;
        private int _lastReportedAt;

        public Counters(int total, ILogger logger)
        {
            _total = Math.Max(1, total);
            _logger = logger;
        }

        public int Succeeded => _succeeded;
        public int Skipped => _skipped;
        public int Failed => _failed;
        public int RowsRead => _rowsRead;
        public int RowsChanged => _rowsChanged;

        public void IncrementSucceeded(int read, int changed)
        {
            Interlocked.Increment(ref _succeeded);
            Interlocked.Add(ref _rowsRead, read);
            Interlocked.Add(ref _rowsChanged, changed);
            ReportIfDue();
        }

        public void IncrementSkipped()
        {
            Interlocked.Increment(ref _skipped);
            ReportIfDue();
        }

        public void IncrementFailed()
        {
            Interlocked.Increment(ref _failed);
            ReportIfDue();
        }

        /// <summary>
        /// Emit a progress line every <see cref="ReportEveryUnits"/> finished units.
        ///
        /// <para>
        /// Counted in UNITS rather than on a timer so the cadence scales with how much
        /// is actually happening: a stalled run goes quiet, which is itself the signal.
        /// The ETA is deliberately computed from units ACTUALLY DONE this run (skips
        /// excluded), because a resumed backfill skips thousands of settled units in
        /// seconds and a naive rate would promise an absurd finish time.
        /// </para>
        /// </summary>
        private void ReportIfDue()
        {
            const int ReportEveryUnits = 100;

            var finished = _succeeded + _skipped + _failed;
            if (finished % ReportEveryUnits != 0) return;

            lock (_reportGate)
            {
                if (finished <= _lastReportedAt) return;      // another thread got here first
                _lastReportedAt = finished;
            }

            var elapsed = DateTime.UtcNow - _startedUtc;
            var worked = _succeeded + _failed;                // skips cost no wall time
            var eta = worked > 0 && finished < _total
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / worked * (_total - finished))
                : TimeSpan.Zero;

            _logger.LogInformation(
                "EIA progress: {Finished}/{Total} units ({Percent:P0}) — {Ok} ok, {Skipped} skipped, " +
                "{Failed} failed; {Rows:N0} rows changed; elapsed {Elapsed:hh\\:mm\\:ss}{Eta}",
                finished, _total, (double)finished / _total,
                _succeeded, _skipped, _failed, _rowsChanged, elapsed,
                eta > TimeSpan.Zero ? $", ~{eta:hh\\:mm} remaining" : string.Empty);
        }
    }
}
