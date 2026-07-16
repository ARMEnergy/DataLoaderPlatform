using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DataLoader.Core.Pipeline;

/// <summary>
/// Reusable Extract → Transform → Load pipeline.
///
/// The original Energy-Aspects-specific orchestrator hard-wired three things
/// together:
///   - how to discover work (mappings × date windows)
///   - how to read (HTTP)
///   - how to write (SQL)
///
/// This class breaks those apart. The shape of the loop is fixed and lives
/// here once. The three moving parts come in as DI-resolved services:
///
///   <see cref="IWorkUnitProvider{TUnit}"/>   — discovers what to do
///   <see cref="ISourceReader{TUnit,TItem}"/> — reads source data
///   <see cref="ITransformer{TItem,TRow}"/>   — converts to target rows
///   <see cref="ISink{TRow}"/>                — persists rows
///
/// Idempotency and observability come from <see cref="ILoadLogRepository"/>:
/// every unit checks "already done?" before doing the work, and every result
/// is logged. Concurrency comes from <see cref="ParallelRunner"/>.
///
/// A loader that fits this shape needs no orchestration code at all — just
/// these four services. A loader that doesn't fit can implement
/// <see cref="ILoaderPipeline"/> directly.
/// </summary>
public class LoaderPipelineBase<TUnit, TItem, TRow> : ILoaderPipeline
    where TUnit : WorkUnit
{
    private readonly string _loaderId;
    private readonly IWorkUnitProvider<TUnit> _workUnits;
    private readonly ISourceReader<TUnit, TItem> _source;
    private readonly ITransformer<TItem, TRow> _transformer;
    private readonly ISink<TRow> _sink;
    private readonly ILoadLogRepository _loadLog;
    private readonly LoaderSettingsBase _settings;
    private readonly ILogger _logger;

    public LoaderPipelineBase(
        string loaderId,
        IWorkUnitProvider<TUnit> workUnits,
        ISourceReader<TUnit, TItem> source,
        ITransformer<TItem, TRow> transformer,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        LoaderSettingsBase settings,
        ILogger logger)
    {
        _loaderId = loaderId;
        _workUnits = workUnits;
        _source = source;
        _transformer = transformer;
        _sink = sink;
        _loadLog = loadLog;
        _settings = settings;
        _logger = logger;
    }

    public async Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("[{Loader}] Pipeline starting (RunId={RunId})", _loaderId, context.RunId);

        IReadOnlyList<TUnit> units;
        try
        {
            units = await _workUnits.GetWorkUnitsAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Loader}] Failed to enumerate work units", _loaderId);
            return LoaderRunResult.Failed(_loaderId, ex.Message, sw.Elapsed);
        }

        if (units.Count == 0)
        {
            _logger.LogWarning("[{Loader}] No work units to process", _loaderId);
            return new LoaderRunResult
            {
                LoaderId = _loaderId, Success = true, Duration = sw.Elapsed
            };
        }

        _logger.LogInformation("[{Loader}] {Count} work units; max concurrency {Conc}",
            _loaderId, units.Count, _settings.MaxConcurrentWorkUnits);

        var counters = new Counters();

        await ParallelRunner.RunAsync(
            units,
            _settings.MaxConcurrentWorkUnits,
            async (unit, ct) => await ProcessOneAsync(unit, context, counters, ct).ConfigureAwait(false),
            context.CancellationToken
        ).ConfigureAwait(false);

        _logger.LogInformation(
            "[{Loader}] Done — {Total} units: {OK} ok, {Skip} skipped, {Fail} failed; {Recs} records in {Elapsed}",
            _loaderId, units.Count, counters.Succeeded, counters.Skipped, counters.Failed,
            counters.Records, sw.Elapsed);

        return new LoaderRunResult
        {
            LoaderId = _loaderId,
            Success = counters.Failed == 0,
            WorkUnitsTotal = units.Count,
            WorkUnitsSucceeded = counters.Succeeded,
            WorkUnitsSkipped = counters.Skipped,
            WorkUnitsFailed = counters.Failed,
            RecordsProcessed = counters.Records,
            Duration = sw.Elapsed
        };
    }

    private async Task ProcessOneAsync(TUnit unit, LoaderRunContext context, Counters counters, CancellationToken ct)
    {
        // Per-unit timeout layered on top of the run cancellation
        using var unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_settings.WorkUnitTimeoutSeconds > 0)
            unitCts.CancelAfter(TimeSpan.FromSeconds(_settings.WorkUnitTimeoutSeconds));
        var unitCt = unitCts.Token;

        // 1. Idempotency check + open load log row
        LoadLogHandle? handle;
        try
        {
            handle = await _loadLog.BeginAsync(
                _loaderId, context.RunId, unit.Key, unit.DisplayName, unitCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            _logger.LogError(ex, "[{Loader}] Failed to open load log for {Unit}", _loaderId, unit.DisplayName);
            return;
        }

        if (handle is null)
        {
            counters.IncrementSkipped();
            _logger.LogDebug("[{Loader}] Skipping {Unit} — already loaded", _loaderId, unit.DisplayName);
            return;
        }

        // 2. Extract → Transform → Load
        try
        {
            var items = await _source.ReadAsync(unit, unitCt).ConfigureAwait(false);
            var rows = _transformer.Transform(items, unit);
            var written = await _sink.WriteAsync(rows, unitCt).ConfigureAwait(false);

            await _loadLog.CompleteSuccessAsync(handle, written, unitCt).ConfigureAwait(false);

            counters.IncrementSucceeded(written);
            _logger.LogDebug("[{Loader}] {Unit} → {Rows} rows", _loaderId, unit.DisplayName, written);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            // Per-unit timeout, not whole-run cancellation — record as failure and move on
            counters.IncrementFailed();
            await TryCompleteFailureAsync(handle, "Work unit timed out").ConfigureAwait(false);
            _logger.LogWarning("[{Loader}] {Unit} timed out", _loaderId, unit.DisplayName);
        }
        catch (OperationCanceledException)
        {
            // Whole-run cancellation — record as failure but bubble up
            await TryCompleteFailureAsync(handle, "Run cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            await TryCompleteFailureAsync(handle, ex.Message).ConfigureAwait(false);
            _logger.LogError(ex, "[{Loader}] {Unit} failed", _loaderId, unit.DisplayName);
        }
    }

    private async Task TryCompleteFailureAsync(LoadLogHandle handle, string message)
    {
        try
        {
            await _loadLog.CompleteFailureAsync(handle, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Loader}] Failed to close load log {Id} as failure",
                _loaderId, handle.LoadLogId);
        }
    }

    private sealed class Counters
    {
        private int _succeeded;
        private int _skipped;
        private int _failed;
        private int _records;
        public int Succeeded => _succeeded;
        public int Skipped => _skipped;
        public int Failed => _failed;
        public int Records => _records;
        public void IncrementSucceeded(int recs) { Interlocked.Increment(ref _succeeded); Interlocked.Add(ref _records, recs); }
        public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
    }
}
