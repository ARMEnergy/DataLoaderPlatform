using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.NGX;

/// <summary>
/// One index work unit = one BATCH OF INDEX IDS x one window chunk = one HTTP request
/// (or a few, if the response reports itself truncated) = one merge.
///
/// <para>
/// <b>Resume key.</b> Index units are ALWAYS hot:
/// </para>
/// <code>
///   ngx:IndexPrice:ids=1,2,3,...:2026-06-01..2027-03-01:run={token}
/// </code>
/// <para>
/// There is no settled zone here, and that is deliberate rather than an oversight. The
/// whole point of this feed is the daily snapshot — <c>ExecutionDate</c> leads the
/// primary key, so every run is supposed to write a NEW generation of the same window,
/// and a stable key would skip the run entirely after the first day. With the default
/// <c>RunDate</c> token the unit re-pulls once per UTC day and a same-day retry is a
/// cheap skip.
/// </para>
/// <para>
/// ⚠ The ID LIST is part of the key. Adding or removing a row in <c>dbo.[Index]</c>
/// reshuffles the batches, so every key changes and that day's window re-loads under
/// fresh keys instead of being skipped. That is the safe direction — the merge is
/// idempotent, whereas a stale key that no longer covers the same ids would leave a
/// silent gap.
/// </para>
/// </summary>
internal sealed class NgxIndexPriceWorkUnit : WorkUnit
{
    /// <summary>The index ids this request names. At most <c>IndexIdsPerRequest</c> (vendor limit 10).</summary>
    public required IReadOnlyList<int> IndexIds { get; init; }

    /// <summary>First delivery day the unit covers, inclusive.</summary>
    public required DateOnly Start { get; init; }

    /// <summary>Last delivery day the unit covers. The vendor's <c>effectiveEnd</c> is INCLUSIVE.</summary>
    public required DateOnly End { get; init; }

    /// <summary>The snapshot stamp written to every row's <c>ExecutionDate</c>.</summary>
    public required DateOnly ExecutionDate { get; init; }

    /// <summary>Always <c>:run=…</c> for this feed — see the type remarks.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    private string Ids => string.Join(",", IndexIds);

    /// <summary>
    /// <c>ExecutionDate</c> is in the key EXPLICITLY, not merely implied by the run
    /// token. It is the leading column of the target primary key, so two units that
    /// write different snapshot generations must never share a key whatever the hot-key
    /// strategy is — including under <see cref="NgxHotKeyStrategy.RunHour"/>, where the
    /// token is UTC-based by design.
    /// </summary>
    public override string Key =>
        $"ngx:IndexPrice:ids={Ids}:{NgxTime.Iso(Start)}..{NgxTime.Iso(End)}" +
        $":exec={NgxTime.Iso(ExecutionDate)}{KeySuffix}";

    public override string DisplayName =>
        $"NGX IndexPrice [{IndexIds.Count} id(s) {IndexIds[0]}..{IndexIds[^1]}] " +
        $"{NgxTime.Iso(Start)}..{NgxTime.Iso(End)}";
}

/// <summary>
/// One strip work unit = one date chunk = one HTTP request = one merge.
///
/// <code>
///   settled:  ngx:StripTradingSummary:2026-08-01..2026-08-07
///   hot:      ngx:StripTradingSummary:2026-09-14..2026-09-20:run={token}
/// </code>
/// <para>
/// Unlike the index feed this one DOES have a settled zone: a strip row is a trade that
/// happened, not a snapshot of a moving value, so once a chunk is old enough to stop
/// being amended it never needs re-reading. With the shipped 45-day default the whole
/// two-month window is still hot, which is the conservative setting; raising
/// <c>StripMonthsBack</c> for a backfill without raising
/// <c>StripSettledAfterDays</c> is what makes the older chunks load once and then skip.
/// </para>
/// </summary>
internal sealed class NgxStripWorkUnit : WorkUnit
{
    /// <summary>First trade day the unit covers, inclusive.</summary>
    public required DateOnly Start { get; init; }

    /// <summary>Last trade day the unit covers. The vendor's <c>tradeEndDate</c> is INCLUSIVE.</summary>
    public required DateOnly End { get; init; }

    /// <summary>Empty for a settled chunk; <c>:run=…</c> for a hot one.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>True when this chunk is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot => KeySuffix.Length > 0;

    public override string Key =>
        $"ngx:StripTradingSummary:{NgxTime.Iso(Start)}..{NgxTime.Iso(End)}{KeySuffix}";

    public override string DisplayName =>
        $"NGX StripTradingSummary {NgxTime.Iso(Start)}..{NgxTime.Iso(End)}";
}

/// <summary>
/// Shared window arithmetic and the hot-key token. Kept off both providers so the two
/// feeds cannot drift in how they read <see cref="NgxSettings.HotKeyStrategy"/>.
/// </summary>
internal static class NgxWindow
{
    /// <summary>
    /// The [first-of-month N back .. first-of-month M forward] span both feeds use,
    /// clamped so a misconfiguration cannot invert the window into zero work units.
    /// </summary>
    internal static (DateOnly Start, DateOnly End) MonthSpan(DateOnly today, int monthsBack, int monthsForward)
    {
        var start = NgxTime.FirstOfMonthOffset(today, -Math.Max(0, monthsBack));
        var end = NgxTime.FirstOfMonthOffset(today, Math.Max(0, monthsForward));
        if (end < start) end = start;
        return (start, end);
    }

    /// <summary>
    /// The run-varying component of a hot key.
    ///
    /// <para>
    /// ⚠ <b><see cref="NgxHotKeyStrategy.RunDate"/> is stamped in US CENTRAL, to agree
    /// with <c>ExecutionDate</c>.</b> A UTC date token here would disagree with the
    /// Central <c>ExecutionDate</c> for the five or six hours between UTC midnight and
    /// Central midnight: a run at 03:00Z on the 19th (Central evening of the 18th) and
    /// one at 13:00Z on the 19th (Central morning of the 19th) share a UTC date but
    /// write DIFFERENT <c>ExecutionDate</c> values. With the window bounds identical
    /// inside a month, the two units would produce a byte-identical key, the load log
    /// would skip the second as already done, and that day's snapshot generation would
    /// never be written — a silent, permanent gap in the leading primary key column.
    /// </para>
    /// <para>
    /// <see cref="NgxHotKeyStrategy.RunHour"/> stays on UTC: an HOUR token must be
    /// monotonic, and 01:00 local occurs twice on a fall-back night, which would make
    /// the token repeat and let an already-recorded success suppress a legitimate
    /// re-pull. A DATE token has no such problem — Central dates advance monotonically —
    /// so the two strategies differ on purpose.
    /// </para>
    /// </summary>
    internal static string HotToken(NgxSettings settings, LoaderRunContext context) => settings.HotKeyStrategy switch
    {
        NgxHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", NgxTime.Inv),
        NgxHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => NgxTime.Today(context.StartedAtUtc).ToString("yyyyMMdd", NgxTime.Inv)
    };
}

/// <summary>
/// Produces the index-price work units: the entitled id list cut into batches of at
/// most <see cref="NgxSettings.IndexIdsPerRequest"/>, crossed with the window split into
/// chunks.
///
/// <para>With the shipped defaults that is 47 ids in 5 batches x 1 chunk = 5 work units,
/// five HTTP requests for the whole feed.</para>
/// <para>This provider DOES hit the database (for the catalog) but never the API, so a
/// vendor outage surfaces as failed work units rather than a failed enumeration.</para>
/// </summary>
internal sealed class NgxIndexPriceWorkUnitProvider : IWorkUnitProvider<NgxIndexPriceWorkUnit>
{
    private readonly NgxIndexCatalog _catalog;
    private readonly NgxSettings _settings;
    private readonly ILogger _logger;

    internal NgxIndexPriceWorkUnitProvider(NgxIndexCatalog catalog, NgxSettings settings, ILogger logger)
    {
        _catalog = catalog;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NgxIndexPriceWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var today = NgxTime.Today(context.StartedAtUtc);
        var (windowStart, windowEnd) =
            NgxWindow.MonthSpan(today, _settings.IndexMonthsBack, _settings.IndexMonthsForward);

        var ids = await _catalog.GetIndexIdsAsync(context.CancellationToken).ConfigureAwait(false);
        if (ids.Count == 0) return Array.Empty<NgxIndexPriceWorkUnit>();

        // ⚠ CLAMPED at 10: the vendor answers 403 for the WHOLE request at 11 or more.
        // A configuration above the limit would fail every single work unit, so it is
        // clamped here and warned about in NgxModule.WarnAboutConfiguration rather than
        // trusted.
        var perRequest = Math.Clamp(_settings.IndexIdsPerRequest, 1, 10);
        var chunkMonths = Math.Max(1, _settings.IndexWindowChunkMonths);
        var hotToken = NgxWindow.HotToken(_settings, context);

        var batches = new List<IReadOnlyList<int>>();
        for (var i = 0; i < ids.Count; i += perRequest)
            batches.Add(ids.Skip(i).Take(perRequest).ToArray());

        var chunks = new List<(DateOnly Start, DateOnly End)>();
        for (var start = windowStart; start <= windowEnd; start = NgxTime.FirstOfMonthOffset(start, chunkMonths))
        {
            var end = NgxTime.FirstOfMonthOffset(start, chunkMonths).AddDays(-1);
            if (end > windowEnd) end = windowEnd;
            chunks.Add((start, end));
        }

        var units = (from batch in batches
                     from chunk in chunks
                     select new NgxIndexPriceWorkUnit
                     {
                         IndexIds = batch,
                         Start = chunk.Start,
                         End = chunk.End,
                         ExecutionDate = today,
                         KeySuffix = $":run={hotToken}"
                     }).ToList();

        _logger.LogInformation(
            "NGX IndexPrice: {Units} work unit(s) — {Ids} index id(s) in {Batches} batch(es) of <= {Per} " +
            "x {Chunks} window chunk(s) over {Start}..{End}, ExecutionDate {Exec}",
            units.Count, ids.Count, batches.Count, perRequest, chunks.Count,
            NgxTime.Iso(windowStart), NgxTime.Iso(windowEnd), NgxTime.Iso(today));

        return units;
    }
}

/// <summary>
/// Produces the strip work units: the window split into
/// <see cref="NgxSettings.StripChunkDays"/>-day chunks, newest first.
///
/// <para>Pure arithmetic — no database, no API — so enumeration cannot fail on anything
/// but a cancellation.</para>
/// </summary>
internal sealed class NgxStripWorkUnitProvider : IWorkUnitProvider<NgxStripWorkUnit>
{
    private readonly NgxSettings _settings;
    private readonly ILogger _logger;

    internal NgxStripWorkUnitProvider(NgxSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<NgxStripWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var today = NgxTime.Today(context.StartedAtUtc);
        var (windowStart, windowEnd) =
            NgxWindow.MonthSpan(today, _settings.StripMonthsBack, _settings.StripMonthsForward);

        // ⚠ CLAMPED to >= 1: a non-positive chunk size would step the loop by zero days
        // and never terminate.
        var chunkDays = Math.Max(1, _settings.StripChunkDays);
        var settledAfterDays = Math.Max(0, _settings.StripSettledAfterDays);
        var hotToken = NgxWindow.HotToken(_settings, context);

        // ⚠ Chunks are anchored to a FIXED EPOCH, not to the window start, and their
        // bounds are never clamped to the window.
        //
        // The window start walks forward on the 1st of each month. If chunks were cut
        // from it, every boundary would move when the month rolled — a settled chunk
        // minted in September as 2026-08-29..2026-09-04 would reappear in October as
        // 2026-09-01..2026-09-07, a different key, and the whole settled zone would
        // re-load every month instead of being skipped. Anchoring to day-number
        // arithmetic means a given trade day always falls in the same chunk with the
        // same key, for as long as StripChunkDays is unchanged, so a settled key really
        // is stable.
        //
        // The cost is that the first and last chunks can reach up to chunkDays-1 days
        // outside the window. That is harmless: the extra days merge idempotently, and a
        // request past the end of the data returns a well-formed empty list.
        var firstStart = DateOnly.FromDayNumber(
            windowStart.DayNumber - windowStart.DayNumber % chunkDays);

        var chunks = new List<(DateOnly Start, DateOnly End)>();
        for (var start = firstStart; start <= windowEnd; start = start.AddDays(chunkDays))
            chunks.Add((start, start.AddDays(chunkDays - 1)));

        // Newest first. Correctness never depends on the order — ParallelRunner may
        // finish units in any order and the merges are order-independent — but a run cut
        // short has then done the days anyone is actually watching.
        chunks.Reverse();

        var units = new List<NgxStripWorkUnit>(chunks.Count);
        var hot = 0;

        foreach (var (start, end) in chunks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // A chunk ages by its NEWEST day: while any day it covers is still inside
            // the hot zone the whole chunk must be re-pulled, because one request
            // retrieves them together. Chunks that end in the FUTURE have a negative
            // age and are therefore hot, which is what we want — tomorrow's trades do
            // not exist yet.
            var ageDays = today.DayNumber - end.DayNumber;
            var isHot = ageDays <= settledAfterDays;
            if (isHot) hot++;

            units.Add(new NgxStripWorkUnit
            {
                Start = start,
                End = end,
                KeySuffix = isHot ? $":run={hotToken}" : string.Empty
            });
        }

        _logger.LogInformation(
            "NGX StripTradingSummary: {Count} work unit(s) over {Start}..{End} " +
            "({Hot} hot, {Settled} settled, chunk {Chunk} day(s))",
            units.Count, NgxTime.Iso(windowStart), NgxTime.Iso(windowEnd),
            hot, units.Count - hot, chunkDays);

        return Task.FromResult<IReadOnlyList<NgxStripWorkUnit>>(units);
    }
}
