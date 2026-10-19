using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Extensions.Logging;

namespace DataLoader.NUCORE;

/// <summary>
/// Marker so <see cref="NucoreModule.RunAsync"/> can enumerate the per-endpoint pipelines
/// without their generics colliding with another loader's registrations in the shared DI
/// container.
/// </summary>
internal interface INucorePipeline : ILoaderPipeline
{
    /// <summary>The endpoint name, matched case-insensitively against <c>EnabledEndpoints</c>.</summary>
    string EndpointName { get; }
}

/// <summary>
/// One endpoint's ETL loop.
///
/// <para>
/// <b>Why this does not use <c>LoaderPipelineBase</c>.</b> That base implements read-all →
/// transform-all → write-once, which is the right shape for most loaders here and the wrong
/// one for this vendor: a single <c>Volatilities</c> day is 147,908 rows, and
/// <c>PositionDetails</c> is 2,468,453 rows across its trade months. Materialising a unit's
/// rows before writing would mean millions of row objects in memory, times
/// <c>MaxConcurrentWorkUnits</c>. So the loop is written out here with the same structure -
/// idempotency via <see cref="ILoadLogRepository"/>, bounded concurrency via
/// <see cref="ParallelRunner"/>, per-unit timeout, counters - but the body streams and
/// merges in batches.
/// </para>
/// </summary>
internal sealed class NucorePipeline : INucorePipeline
{
    private readonly NucoreEndpoint _endpoint;
    private readonly NucoreWorkUnitProvider _units;
    private readonly NucoreSourceReader _reader;
    private readonly NucoreTableSink _sink;
    private readonly NucoreWatermarkStore _watermarks;
    private readonly ILoadLogRepository _loadLog;
    private readonly NucoreSettings _settings;
    private readonly ILogger _logger;

    public NucorePipeline(
        NucoreEndpoint endpoint,
        NucoreWorkUnitProvider units,
        NucoreSourceReader reader,
        NucoreTableSink sink,
        NucoreWatermarkStore watermarks,
        ILoadLogRepository loadLog,
        NucoreSettings settings,
        ILogger logger)
    {
        _endpoint = endpoint;
        _units = units;
        _reader = reader;
        _sink = sink;
        _watermarks = watermarks;
        _loadLog = loadLog;
        _settings = settings;
        _logger = logger;
    }

    public string EndpointName => _endpoint.Name;

    public async Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context)
    {
        var sw = Stopwatch.StartNew();

        IReadOnlyList<NucoreWorkUnit> units;
        try
        {
            units = await _units.GetWorkUnitsAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NUCORE {Endpoint}: failed to enumerate work units", _endpoint.Name);
            return LoaderRunResult.Failed(NucoreModule.Id, NucoreHttp.Redact(ex.Message), sw.Elapsed);
        }

        if (units.Count == 0)
        {
            _logger.LogWarning("NUCORE {Endpoint}: no work units to process", _endpoint.Name);
            return new LoaderRunResult { LoaderId = NucoreModule.Id, Success = true, Duration = sw.Elapsed };
        }

        var counters = new Counters();

        await ParallelRunner.RunAsync(
            units,
            _settings.MaxConcurrentWorkUnits,
            (unit, ct) => ProcessOneAsync(unit, context, counters, ct),
            context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "NUCORE {Endpoint}: {Total} unit(s) - {OK} ok, {Skip} skipped, {Fail} failed; " +
            "{Read} row(s) read, {Changed} changed, {Empty} empty read(s) in {Elapsed}",
            _endpoint.Name, units.Count, counters.Succeeded, counters.Skipped, counters.Failed,
            counters.RowsRead, counters.RowsChanged, counters.EmptyReads, sw.Elapsed);

        return new LoaderRunResult
        {
            LoaderId = NucoreModule.Id,
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
        NucoreWorkUnit unit, LoaderRunContext context, Counters counters, CancellationToken ct)
    {
        using var unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_settings.WorkUnitTimeoutSeconds > 0)
            unitCts.CancelAfter(TimeSpan.FromSeconds(_settings.WorkUnitTimeoutSeconds));
        var unitCt = unitCts.Token;

        // 1. Idempotency. A SETTLED unit whose key is already recorded returns null here and
        //    costs nothing further - no request at all. That is the whole mechanism behind
        //    not re-reading a full-history backfill every run.
        LoadLogHandle? handle;
        try
        {
            handle = await _loadLog
                .BeginAsync(NucoreModule.Id, context.RunId, unit.Key, unit.DisplayName, unitCt)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            _logger.LogError(ex, "NUCORE {Endpoint}: failed to open load log for {Unit}",
                _endpoint.Name, unit.DisplayName);
            return;
        }

        if (handle is null)
        {
            counters.IncrementSkipped();
            _logger.LogDebug("NUCORE {Endpoint}: skipping {Unit} - already loaded",
                _endpoint.Name, unit.DisplayName);
            return;
        }

        try
        {
            // 2. The watermark, for the endpoint that cannot be asked for a date.
            DateTime? watermark = null;
            if (_endpoint.Partition == NucorePartition.TradeMonth && unit.TradeMonth is not null)
            {
                watermark = await _watermarks
                    .GetAsync(_endpoint, unit.TradeMonth, unitCt)
                    .ConfigureAwait(false);

                if (watermark is not null)
                    _logger.LogDebug(
                        "NUCORE {Endpoint}: {Unit} resuming after {Watermark:yyyy-MM-dd}",
                        _endpoint.Name, unit.DisplayName, watermark.Value);
            }

            // 3. Read → convert → merge, in batches.
            var result = await _reader
                .ReadAsync(unit, watermark, WriteBatchAsync, unitCt)
                .ConfigureAwait(false);

            if (result.EmptyRead)
                // A weekend, a holiday, a pre-history date, or a trade month with no
                // positions. The vendor signals all of these with HTTP 200 and zero rows,
                // and they are normal history - not failures.
                _logger.LogDebug("NUCORE {Endpoint}: {Unit} returned no rows (legitimate empty read)",
                    _endpoint.Name, unit.DisplayName);

            // RecordsProcessed is rows CHANGED, matching the merge procs' @@ROWCOUNT. Zero
            // is a healthy success on a re-merge where nothing moved.
            await _loadLog.CompleteSuccessAsync(handle, result.RowsChanged, unitCt).ConfigureAwait(false);
            counters.IncrementSucceeded(result);
        }
        catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            counters.IncrementFailed();

            // Two different failures arrive as the same exception type, and reporting both
            // as "timed out" sends the investigation to the wrong setting.
            var reason = unitCts.IsCancellationRequested
                ? $"Work unit timed out after {_settings.WorkUnitTimeoutSeconds}s (WorkUnitTimeoutSeconds)"
                : ex.InnerException is TimeoutException
                    ? "HTTP attempt timed out (HttpTimeoutSeconds); the work unit budget was not reached"
                    : "Cancelled before completion (not the work unit budget)";

            await TryFailAsync(handle, reason).ConfigureAwait(false);
            _logger.LogWarning("NUCORE {Endpoint}: {Unit}: {Reason}",
                _endpoint.Name, unit.DisplayName, reason);
        }
        catch (OperationCanceledException)
        {
            await TryFailAsync(handle, "Run cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            var message = NucoreHttp.Redact(ex.Message);
            await TryFailAsync(handle, message).ConfigureAwait(false);
            _logger.LogError(ex, "NUCORE {Endpoint}: {Unit} failed - {Message}",
                _endpoint.Name, unit.DisplayName, message);
        }
    }

    private Task<int> WriteBatchAsync(IReadOnlyList<NucoreRow> batch, CancellationToken ct) =>
        _sink.WriteAsync(batch, ct);

    private async Task TryFailAsync(LoadLogHandle handle, string message)
    {
        try
        {
            await _loadLog.CompleteFailureAsync(handle, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NUCORE {Endpoint}: failed to close load log {Id} as failure",
                _endpoint.Name, handle.LoadLogId);
        }
    }

    private sealed class Counters
    {
        private int _succeeded;
        private int _skipped;
        private int _failed;
        private int _rowsRead;
        private int _rowsChanged;
        private int _emptyReads;

        public int Succeeded => _succeeded;
        public int Skipped => _skipped;
        public int Failed => _failed;
        public int RowsRead => _rowsRead;
        public int RowsChanged => _rowsChanged;
        public int EmptyReads => _emptyReads;

        public void IncrementSucceeded(NucoreReadResult result)
        {
            Interlocked.Increment(ref _succeeded);
            Interlocked.Add(ref _rowsRead, result.RowsRead);
            Interlocked.Add(ref _rowsChanged, result.RowsChanged);
            if (result.EmptyRead) Interlocked.Increment(ref _emptyReads);
        }

        public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
    }
}
