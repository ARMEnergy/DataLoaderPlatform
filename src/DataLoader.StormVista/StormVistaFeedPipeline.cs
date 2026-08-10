using System.Diagnostics;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Transforms;
using Microsoft.Extensions.Logging;

namespace DataLoader.StormVista;

/// <summary>Marker so <see cref="StormVistaModule.RunAsync"/> can enumerate and toggle feed pipelines (mirrors IPlattsFeedPipeline).</summary>
public interface IStormVistaFeedPipeline : ILoaderPipeline
{
    string FeedId { get; }
}

/// <summary>
/// Custom windowed orchestrator for one feed (design §6.3). It chunks the run's
/// date range into ordered windows (oldest → newest) and processes ONE window at
/// a time so peak memory is a single window's units — never the whole ~828k
/// backfill. Per window it does NOT re-implement the per-unit loop: it delegates
/// to a standard <see cref="LoaderPipelineBase{TUnit,TItem,TRow}"/> bound to a
/// <see cref="StaticWorkUnitProvider{TUnit}"/> of that window's precomputed units,
/// reusing the vetted idempotency / skip / per-unit-timeout / fail-one-not-the-run
/// behaviour (and <see cref="Core.Concurrency.ParallelRunner"/> bounding).
///
/// <para>
/// Range precedence: this loader uses its own <c>Mode</c>/<c>DaysBack</c>/backfill
/// settings and intentionally does NOT apply <c>context.DateFrom/DateTo</c>. The
/// platform host always injects those from <c>Platform:DefaultDaysBack*</c>, so
/// honouring them would silently override the incremental window and break
/// backfill. Settings are authoritative here.
/// </para>
/// </summary>
internal sealed class StormVistaFeedPipeline<TUnit, TRow> : IStormVistaFeedPipeline
    where TUnit : WorkUnit
{
    private readonly IStormVistaUnitEnumerator<TUnit> _enumerator;
    private readonly ISourceReader<TUnit, TRow> _source;
    private readonly ISink<TRow> _sink;
    private readonly ILoadLogRepository _loadLog;
    private readonly StormVistaSettings _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public StormVistaFeedPipeline(
        string feedId,
        IStormVistaUnitEnumerator<TUnit> enumerator,
        ISourceReader<TUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        StormVistaSettings settings,
        ILoggerFactory loggerFactory)
    {
        FeedId = feedId;
        _enumerator = enumerator;
        _source = source;
        _sink = sink;
        _loadLog = loadLog;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger($"StormVista.{feedId}.Pipeline");
    }

    public string FeedId { get; }

    public async Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context)
    {
        var sw = Stopwatch.StartNew();
        var todayUtc = DateOnly.FromDateTime(context.StartedAtUtc);

        if (_settings.Mode == StormVistaMode.Backfill && !_settings.BackfillStart.HasValue)
        {
            const string msg = "Backfill mode requires Loaders:StormVista:BackfillStart";
            _logger.LogError("[{Feed}] {Message}", FeedId, msg);
            return LoaderRunResult.Failed(StormVistaModule.Id, $"[{FeedId}] {msg}", sw.Elapsed);
        }

        var (start, end) = ResolveRange(todayUtc);
        if (start > end)
        {
            _logger.LogWarning("[{Feed}] resolved date range {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} is empty; nothing to do",
                FeedId, start, end);
            return new LoaderRunResult { LoaderId = StormVistaModule.Id, Success = true, Duration = sw.Elapsed };
        }

        var windows = BuildWindows(start, end).ToList();
        _logger.LogInformation("[{Feed}] {Mode}: range {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} in {Windows} window(s)",
            FeedId, _settings.Mode, start, end, windows.Count);

        var totals = new Totals();
        foreach (var (wStart, wEnd) in windows)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await ProcessWindowAsync(wStart, wEnd, context, totals).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "[{Feed}] done — {Total} units: {OK} ok, {Skip} skipped, {Fail} failed; {Recs} records in {Elapsed}",
            FeedId, totals.Total, totals.Succeeded, totals.Skipped, totals.Failed, totals.Records, sw.Elapsed);

        return new LoaderRunResult
        {
            LoaderId = StormVistaModule.Id,
            Success = totals.Failed == 0,
            WorkUnitsTotal = totals.Total,
            WorkUnitsSucceeded = totals.Succeeded,
            WorkUnitsSkipped = totals.Skipped,
            WorkUnitsFailed = totals.Failed,
            RecordsProcessed = totals.Records,
            ErrorMessage = totals.Failed == 0 ? null : $"[{FeedId}] {totals.Failed} work unit(s) failed",
            Duration = sw.Elapsed
        };
    }

    private async Task ProcessWindowAsync(DateOnly wStart, DateOnly wEnd, LoaderRunContext context, Totals totals)
    {
        IReadOnlyList<TUnit> units;
        try
        {
            units = await _enumerator.EnumerateAsync(wStart, wEnd, context, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A window whose enumeration fails is logged and skipped; the run
            // continues (fail-a-block-not-the-run).
            _logger.LogError(ex, "[{Feed}] failed to enumerate window {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}", FeedId, wStart, wEnd);
            return;
        }

        if (units.Count == 0)
        {
            _logger.LogDebug("[{Feed}] window {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}: no units", FeedId, wStart, wEnd);
            return;
        }

        var windowPipeline = new LoaderPipelineBase<TUnit, TRow, TRow>(
            StormVistaModule.Id,
            new StaticWorkUnitProvider<TUnit>(units),
            _source,
            new IdentityTransformer<TRow>(),
            _sink,
            _loadLog,
            _settings,
            _loggerFactory.CreateLogger($"StormVista.{FeedId}.Window"));

        var result = await windowPipeline.ExecuteAsync(context).ConfigureAwait(false);
        totals.Add(result);

        _logger.LogInformation(
            "[{Feed}] window {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}: {OK}/{Total} ok, {Skip} skipped, {Fail} failed, {Recs} records",
            FeedId, wStart, wEnd, result.WorkUnitsSucceeded, result.WorkUnitsTotal,
            result.WorkUnitsSkipped, result.WorkUnitsFailed, result.RecordsProcessed);
    }

    private (DateOnly Start, DateOnly End) ResolveRange(DateOnly todayUtc)
    {
        // Backfill-without-start is checked before this is called, so TryResolveRaw succeeds here.
        StormVistaDateRange.TryResolveRaw(_settings, todayUtc, out var rawStart, out var rawEnd);
        var (start, end) = StormVistaDateRange.Clamp(rawStart, rawEnd, todayUtc);

        if (start != rawStart)
            _logger.LogWarning("[{Feed}] start {Raw:yyyy-MM-dd} precedes the archive floor; clamped to {Start:yyyy-MM-dd}",
                FeedId, rawStart, start);
        if (end != rawEnd)
            _logger.LogWarning("[{Feed}] end {Raw:yyyy-MM-dd} is in the future; clamped to {End:yyyy-MM-dd}",
                FeedId, rawEnd, end);

        return (start, end);
    }

    private IEnumerable<(DateOnly Start, DateOnly End)> BuildWindows(DateOnly start, DateOnly end)
    {
        var chunk = _settings.ChunkDays > 0 ? _settings.ChunkDays : 30;
        for (var wStart = start; wStart <= end; wStart = wStart.AddDays(chunk))
        {
            var wEnd = wStart.AddDays(chunk - 1);
            if (wEnd > end) wEnd = end;
            yield return (wStart, wEnd);
        }
    }

    /// <summary>Mutable per-feed accumulator across windows.</summary>
    private sealed class Totals
    {
        public int Total { get; private set; }
        public int Succeeded { get; private set; }
        public int Skipped { get; private set; }
        public int Failed { get; private set; }
        public int Records { get; private set; }

        public void Add(LoaderRunResult r)
        {
            Total += r.WorkUnitsTotal;
            Succeeded += r.WorkUnitsSucceeded;
            Skipped += r.WorkUnitsSkipped;
            Failed += r.WorkUnitsFailed;
            Records += r.RecordsProcessed;
        }
    }
}
