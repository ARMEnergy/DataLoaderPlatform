using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Genscape;

/// <summary>
/// One work unit = one FEED × one REQUEST REGION × one window chunk = one HTTP request
/// (or a few, if the window has to be bisected) = one merge.
///
/// <para>
/// <b>Why the unit is a date RANGE and not a day.</b> Most day-windowed loaders in this
/// repo make one unit per day. These two cannot: the endpoints reject
/// <c>startDate == endDate</c> outright (<c>400 [endDate] must be after
/// [startDate]</c>), and the data is WEEKLY, so 31 day-units per region would be 31
/// requests to retrieve the same 4 or 5 report dates.
/// </para>
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful):
/// </para>
/// <code>
///   settled:  genscape:{FeedId}:{Region}:{start}..{end}
///   hot:      genscape:{FeedId}:{Region}:{start}..{end}:run={token}
/// </code>
/// <para>
/// A chunk whose NEWEST day is older than
/// <see cref="GenscapeSettings.SettledAfterDays"/> gets the STABLE key — loaded once,
/// then a cheap skip forever. Anything newer gets the run-varying HOT key so the
/// vendor's revisions are re-pulled. With the shipped 30/30 defaults the whole window
/// is one chunk ending today, so it is hot and re-pulls every run; that is deliberate,
/// and it is what <c>revision=revised</c> is for — the vendor restates recent weeks.
/// </para>
/// <para>
/// ⚠ The window BOUNDS are part of the key, so changing
/// <see cref="GenscapeSettings.DaysBack"/> or
/// <see cref="GenscapeSettings.WindowChunkDays"/> moves the chunk boundaries and the
/// old keys stop matching — the affected range re-loads under fresh keys instead of
/// being skipped. That is the safe direction: re-merging is idempotent, whereas a stale
/// key that no longer covers the same days would leave a gap.
/// </para>
/// </summary>
public sealed class GenscapeWorkUnit : WorkUnit
{
    public required GenscapeFeedDescriptor Feed { get; init; }

    /// <summary>
    /// The REQUEST region — <c>NorthAmerica</c> or <c>GulfCoast</c>. Not the region
    /// stored on the row: each response row carries its own finer-grained region, and
    /// the two request regions return overlapping sets of them.
    /// </summary>
    public required string Region { get; init; }

    /// <summary>First day the unit covers, inclusive.</summary>
    public required DateOnly Start { get; init; }

    /// <summary>
    /// Last day the unit covers, INCLUSIVE — loader semantics. The API's
    /// <c>endDate</c> is exclusive, so the reader sends <c>End.AddDays(1)</c>.
    /// </summary>
    public required DateOnly End { get; init; }

    /// <summary>Empty for a settled chunk; <c>:run=…</c> for a hot one.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>True when this chunk is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot => KeySuffix.Length > 0;

    public string Slice => $"{Region}:{GenscapeTime.Iso(Start)}..{GenscapeTime.Iso(End)}";

    public override string Key => $"genscape:{Feed.FeedId}:{Slice}{KeySuffix}";

    public override string DisplayName =>
        $"Genscape {Feed.FeedId} {Region} {GenscapeTime.Iso(Start)}..{GenscapeTime.Iso(End)}";
}

/// <summary>
/// Produces the work units for one feed: the configured regions crossed with the window
/// split into chunks.
///
/// <para>
/// With the shipped defaults (<c>DaysBack=30</c>, <c>WindowChunkDays=365</c>) that is
/// ONE chunk per region — two units per feed, four in total, four HTTP requests for the
/// whole loader. Chunking only starts to matter on a backfill; see
/// <see cref="GenscapeSettings.WindowChunkDays"/> for why the chunk exists at all.
/// </para>
/// <para>
/// No requests are made here — this provider is pure arithmetic. Enumeration therefore
/// cannot fail on a network error, and a bad region name surfaces as one failed work
/// unit rather than a failed run.
/// </para>
/// </summary>
public sealed class GenscapeWorkUnitProvider : IWorkUnitProvider<GenscapeWorkUnit>
{
    private readonly GenscapeFeedDescriptor _feed;
    private readonly GenscapeSettings _settings;
    private readonly ILogger _logger;

    public GenscapeWorkUnitProvider(
        GenscapeFeedDescriptor feed, GenscapeSettings settings, ILogger logger)
    {
        _feed = feed;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<GenscapeWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var today = GenscapeTime.Today(context.StartedAtUtc);

        // ⚠ CLAMPED. A negative DaysBack would invert the window and produce zero units
        // — a loader that silently does nothing. A negative SettledAfterDays would make
        // every chunk settled, so revisions would never be re-pulled again. A
        // non-positive chunk size would make the loop step by zero days and never
        // terminate.
        var daysBack = Math.Max(0, _settings.DaysBack);
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);
        var chunkDays = Math.Max(1, _settings.WindowChunkDays);

        var windowStart = today.AddDays(-daysBack);
        var hotToken = HotToken(context);

        var regions = _settings.Regions
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (regions.Count == 0)
        {
            _logger.LogWarning(
                "Genscape {Feed}: Regions is empty — no work units. Both endpoints require a region and " +
                "answer 404 without one, so there is no 'all regions' request to fall back to",
                _feed.FeedId);

            return Task.FromResult<IReadOnlyList<GenscapeWorkUnit>>(Array.Empty<GenscapeWorkUnit>());
        }

        var units = new List<GenscapeWorkUnit>();
        var hot = 0;

        foreach (var region in regions)
        {
            // Walk the window in chunks, then reverse, so the newest chunk of each
            // region is attempted first. Correctness never depends on the order —
            // ParallelRunner may finish units in any order and the merges are
            // order-independent — but a run that is cut short has then done the days
            // anyone is actually watching.
            var chunks = new List<(DateOnly Start, DateOnly End)>();

            for (var start = windowStart; start <= today; start = start.AddDays(chunkDays))
            {
                var end = start.AddDays(chunkDays - 1);
                if (end > today) end = today;
                chunks.Add((start, end));
            }

            chunks.Reverse();

            foreach (var (start, end) in chunks)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // A chunk ages by its NEWEST day: as long as any day it covers is still
                // inside the hot zone, the whole chunk must be re-pulled, because one
                // request retrieves them together.
                var ageDays = today.DayNumber - end.DayNumber;
                var isHot = ageDays <= settledAfterDays;
                if (isHot) hot++;

                units.Add(new GenscapeWorkUnit
                {
                    Feed = _feed,
                    Region = region,
                    Start = start,
                    End = end,
                    KeySuffix = isHot ? $":run={hotToken}" : string.Empty
                });
            }
        }

        _logger.LogInformation(
            "Genscape {Feed}: {Count} work unit(s) over {Start}..{End} for region(s) {Regions} " +
            "({Hot} hot, {Settled} settled, chunk {Chunk} day(s))",
            _feed.FeedId, units.Count, GenscapeTime.Iso(windowStart), GenscapeTime.Iso(today),
            string.Join(", ", regions), hot, units.Count - hot, chunkDays);

        return Task.FromResult<IReadOnlyList<GenscapeWorkUnit>>(units);
    }

    /// <summary>
    /// The run-varying component of a hot key. UTC in every case — see
    /// <see cref="GenscapeTime"/> for why a local-zone hour token would break
    /// monotonicity.
    /// </summary>
    internal string HotToken(LoaderRunContext context) => _settings.HotKeyStrategy switch
    {
        GenscapeHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", GenscapeTime.Inv),
        GenscapeHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMdd", GenscapeTime.Inv)
    };
}
