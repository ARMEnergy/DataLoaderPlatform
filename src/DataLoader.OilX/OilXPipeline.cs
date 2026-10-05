using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using Microsoft.Extensions.Logging;

namespace DataLoader.OilX;

/// <summary>
/// Marker so <see cref="OilXModule.RunAsync"/> can enumerate the per-feed pipelines
/// without their generics colliding with another loader's registrations in the shared
/// DI container.
/// </summary>
public interface IOilXPipeline : ILoaderPipeline
{
    /// <summary>The feed id, matched case-insensitively against <c>EnabledFeeds</c>.</summary>
    string FeedId { get; }
}

/// <summary>
/// One feed's ETL loop.
///
/// <para>
/// <b>Why this does not use <c>LoaderPipelineBase</c>.</b> That base implements
/// read-all → transform-all → write-once, which is the right shape for almost every
/// loader here and the wrong one for this feed: a single CargoTracking snapshot is
/// 208 MB / 396,866 rows, and a day holds four of them. Materialising a unit's rows
/// before writing would mean ~1.6 million row objects in memory per unit, times
/// <c>MaxConcurrentWorkUnits</c>. So the loop is written out here with the same
/// structure — idempotency via <see cref="ILoadLogRepository"/>, bounded concurrency via
/// <see cref="ParallelRunner"/>, per-unit timeout, counters — but the body streams and
/// merges in batches.
/// </para>
/// <para>
/// <b>Within a unit, files are sequential and ordered.</b> A day's snapshots all carry
/// the same in-file <c>RunDate</c>, so they collide on <c>(RunDate, RowId)</c>; merging
/// them oldest-published-first is what makes the newest value win
/// (<c>docs/design/OilX.md</c> §4). Parallelism is across units, never within one.
/// </para>
/// </summary>
internal sealed class OilXPipeline : IOilXPipeline
{
    private readonly OilXFeedDescriptor _feed;
    private readonly OilXWorkUnitProvider _units;
    private readonly OilXManifestClient _manifest;
    private readonly OilXSourceReader _reader;
    private readonly OilXTableSink _sink;
    private readonly ILoadLogRepository _loadLog;
    private readonly OilXSettings _settings;
    private readonly ILogger _logger;

    public OilXPipeline(
        OilXFeedDescriptor feed,
        OilXWorkUnitProvider units,
        OilXManifestClient manifest,
        OilXSourceReader reader,
        OilXTableSink sink,
        ILoadLogRepository loadLog,
        OilXSettings settings,
        ILogger logger)
    {
        _feed = feed;
        _units = units;
        _manifest = manifest;
        _reader = reader;
        _sink = sink;
        _loadLog = loadLog;
        _settings = settings;
        _logger = logger;
    }

    public string FeedId => _feed.FeedId;

    public async Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context)
    {
        var sw = Stopwatch.StartNew();

        IReadOnlyList<OilXWorkUnit> units;
        try
        {
            units = await _units.GetWorkUnitsAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OilX {Feed}: failed to enumerate work units", _feed.FeedId);
            return LoaderRunResult.Failed(OilXModule.Id, OilXHttp.Redact(ex.Message), sw.Elapsed);
        }

        if (units.Count == 0)
        {
            _logger.LogWarning("OilX {Feed}: no work units to process", _feed.FeedId);
            return new LoaderRunResult { LoaderId = OilXModule.Id, Success = true, Duration = sw.Elapsed };
        }

        var counters = new Counters();

        await ParallelRunner.RunAsync(
            units,
            _settings.MaxConcurrentWorkUnits,
            (unit, ct) => ProcessOneAsync(unit, context, counters, ct),
            context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "OilX {Feed}: {Total} unit(s) — {OK} ok, {Skip} skipped, {Fail} failed; " +
            "{Read} row(s) read, {Changed} changed in {Elapsed}",
            _feed.FeedId, units.Count, counters.Succeeded, counters.Skipped, counters.Failed,
            counters.RowsRead, counters.RowsChanged, sw.Elapsed);

        return new LoaderRunResult
        {
            LoaderId = OilXModule.Id,
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
        OilXWorkUnit unit, LoaderRunContext context, Counters counters, CancellationToken ct)
    {
        using var unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_settings.WorkUnitTimeoutSeconds > 0)
            unitCts.CancelAfter(TimeSpan.FromSeconds(_settings.WorkUnitTimeoutSeconds));
        var unitCt = unitCts.Token;

        // 1. Idempotency. A SETTLED day whose key is already recorded returns null here
        //    and costs nothing further -- no manifest call, no download. That is the
        //    whole mechanism behind not re-fetching ~6.5 GB every run.
        LoadLogHandle? handle;
        try
        {
            handle = await _loadLog
                .BeginAsync(OilXModule.Id, context.RunId, unit.Key, unit.DisplayName, unitCt)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            _logger.LogError(ex, "OilX {Feed}: failed to open load log for {Unit}",
                _feed.FeedId, unit.DisplayName);
            return;
        }

        if (handle is null)
        {
            counters.IncrementSkipped();
            _logger.LogDebug("OilX {Feed}: skipping {Unit} — already loaded", _feed.FeedId, unit.DisplayName);
            return;
        }

        try
        {
            var manifest = await _manifest.ListAsync(unit.Feed, unit.Day, unitCt).ConfigureAwait(false);

            if (manifest.EmptyDay)
            {
                // HTTP 422 "No data for files" -- a publication gap, which is normal
                // history for this vendor and a legitimate empty read, not a failure.
                await _loadLog.CompleteSuccessAsync(handle, 0, unitCt).ConfigureAwait(false);
                counters.IncrementSucceeded(0, 0);
                return;
            }

            var rowsRead = 0;
            var rowsChanged = 0;
            var rowsDropped = 0;

            // ⚠ SEQUENTIAL and in ascending uploaded_at. See the class remarks.
            foreach (var file in manifest.Files)
            {
                unitCt.ThrowIfCancellationRequested();

                var result = await _reader
                    .LoadAsync(file, WriteBatchAsync, unitCt)
                    .ConfigureAwait(false);

                rowsRead += result.RowsRead;
                rowsChanged += result.RowsChanged;
                rowsDropped += result.RowsDropped;

                _logger.LogDebug(
                    "OilX {Feed} {Unit}: {File} → {Read} read, {Dropped} dropped, {Changed} changed",
                    _feed.FeedId, unit.DisplayName, file.FileName,
                    result.RowsRead, result.RowsDropped, result.RowsChanged);
            }

            // A file that parsed to nothing but a header is not an empty DAY -- the
            // vendor signals that with a 422 -- so it is worth saying out loud.
            if (rowsRead == 0)
                _logger.LogWarning(
                    "OilX {Feed} {Unit}: {Count} file(s) published but no data rows parsed",
                    _feed.FeedId, unit.DisplayName, manifest.Files.Count);

            if (rowsDropped > 0)
                _logger.LogWarning(
                    "OilX {Feed} {Unit}: {Dropped} of {Read} row(s) dropped for missing required values",
                    _feed.FeedId, unit.DisplayName, rowsDropped, rowsRead);

            // RecordsProcessed is rows CHANGED, matching the merge procs' @@ROWCOUNT.
            // Zero is a healthy success on a re-merge where nothing moved.
            await _loadLog.CompleteSuccessAsync(handle, rowsChanged, unitCt).ConfigureAwait(false);
            counters.IncrementSucceeded(rowsRead, rowsChanged);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            counters.IncrementFailed();
            await TryFailAsync(handle, "Work unit timed out").ConfigureAwait(false);
            _logger.LogWarning("OilX {Feed}: {Unit} timed out after {Seconds}s",
                _feed.FeedId, unit.DisplayName, _settings.WorkUnitTimeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            await TryFailAsync(handle, "Run cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            counters.IncrementFailed();
            // Redacted: an HttpRequestException can carry a URL, and every URL in this
            // loader holds either the api_key or an AWS signature.
            var message = OilXHttp.Redact(ex.Message);
            await TryFailAsync(handle, message).ConfigureAwait(false);
            _logger.LogError(ex, "OilX {Feed}: {Unit} failed — {Message}",
                _feed.FeedId, unit.DisplayName, message);
        }
    }

    private Task<int> WriteBatchAsync(IReadOnlyList<OilXRow> batch, CancellationToken ct) =>
        _sink.WriteAsync(batch, ct);

    private async Task TryFailAsync(LoadLogHandle handle, string message)
    {
        try
        {
            await _loadLog.CompleteFailureAsync(handle, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OilX {Feed}: failed to close load log {Id} as failure",
                _feed.FeedId, handle.LoadLogId);
        }
    }

    private sealed class Counters
    {
        private int _succeeded;
        private int _skipped;
        private int _failed;
        private int _rowsRead;
        private int _rowsChanged;

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
        }

        public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
    }
}
