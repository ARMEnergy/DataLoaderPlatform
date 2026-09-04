using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Criterion;

/// <summary>
/// One work unit = one FEED × one SLICE of its source = one read = one merge.
///
/// <para>
/// What a "slice" is depends on the feed's <see cref="CriterionWindowMode"/>:
/// </para>
/// <code>
///   Snapshot        the whole relation                  (5 dimension feeds)
///   DayWindow       one post_date / eff_gas_day         (3 feeds)
///   PagedDayWindow  one post_date, one page of series   (FinancialSeriesData)
/// </code>
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful):
/// </para>
/// <code>
///   settled:  criterion:{FeedId}:{slice}
///   hot:      criterion:{FeedId}:{slice}:run={token}
/// </code>
/// <para>
/// A day older than <see cref="CriterionSettings.SettledAfterDays"/> gets the STABLE
/// key — loaded once, then a cheap skip forever. A newer one gets the run-varying
/// HOT key so the source's revisions are picked up. With the shipped 30/30 defaults
/// every day in the window is hot; see
/// <see cref="CriterionSettings.SettledAfterDays"/> for why that is deliberate.
/// </para>
/// <para>
/// ⚠ The <see cref="Page"/> component is part of the key for
/// <see cref="CriterionWindowMode.PagedDayWindow"/>, so a page boundary shift
/// (someone changing <see cref="CriterionSettings.SeriesPageSize"/>) re-loads the
/// day under fresh keys rather than skipping half of it. That is the safe
/// direction: re-merging is idempotent, whereas a stale page key that no longer
/// covers the same series would leave a gap.
/// </para>
/// </summary>
public sealed class CriterionWorkUnit : WorkUnit
{
    public required CriterionFeedDescriptor Feed { get; init; }

    /// <summary>The day this unit covers, or null for a snapshot feed.</summary>
    public DateOnly? Day { get; init; }

    /// <summary>Zero-based page number within the day. Always 0 unless the feed is paged.</summary>
    public int Page { get; init; }

    /// <summary>
    /// The source keys this page covers, for a paged feed. Empty otherwise.
    /// Enumerated up front by <see cref="CriterionWorkUnitProvider"/> so each unit is
    /// an index seek on the source PK rather than an OFFSET scan.
    /// </summary>
    public IReadOnlyList<Guid> Keys { get; init; } = Array.Empty<Guid>();

    /// <summary>Empty for a settled slice; <c>:run=…</c> for a hot one.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>True when this slice is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot => KeySuffix.Length > 0;

    /// <summary>The slice component of the key: a date, a date plus page, or "snapshot".</summary>
    public string Slice =>
        Day is null
            ? "snapshot"
            : Feed.WindowMode == CriterionWindowMode.PagedDayWindow
                ? $"{CriterionTime.Iso(Day.Value)}:p{Page}"
                : CriterionTime.Iso(Day.Value);

    public override string Key => $"criterion:{Feed.FeedId}:{Slice}{KeySuffix}";

    public override string DisplayName => $"Criterion {Feed.FeedId} {Slice}";
}

/// <summary>
/// Produces the work units for one feed.
///
/// <para>
/// Snapshot feeds yield exactly one unit. Day-windowed feeds yield one per day in
/// <c>[today - DaysBack, today]</c> inclusive, in UTC (see <see cref="CriterionTime"/>
/// for why UTC rather than a business calendar). Weekends and holidays are NOT
/// filtered out: the source publishes on its own schedule, a day with no rows is a
/// first-class non-error outcome costing one cheap indexed query, and guessing at a
/// calendar would risk skipping a day the source did publish.
/// </para>
/// <para>
/// The paged feed additionally runs ONE key-enumeration query per day — selecting
/// just <c>financial_json_uuid</c>, never the 165 KB <c>data</c> column — and chunks
/// the result into pages. That is what lets each unit seek its rows by primary key
/// instead of re-scanning the day's partition once per page.
/// </para>
/// </summary>
public sealed class CriterionWorkUnitProvider : IWorkUnitProvider<CriterionWorkUnit>
{
    private readonly CriterionFeedDescriptor _feed;
    private readonly ICriterionSource _source;
    private readonly CriterionSettings _settings;
    private readonly ILogger _logger;

    public CriterionWorkUnitProvider(
        CriterionFeedDescriptor feed,
        ICriterionSource source,
        CriterionSettings settings,
        ILogger logger)
    {
        _feed = feed;
        _source = source;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CriterionWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var hotToken = HotToken(context);

        if (_feed.WindowMode == CriterionWindowMode.Snapshot)
        {
            // A snapshot has no day to age, so AlwaysReloadSnapshots decides whether
            // it is hot. Hot is the default: the dimensions genuinely change, and a
            // stable key would load them once and never look again.
            var unit = new CriterionWorkUnit
            {
                Feed = _feed,
                Day = null,
                KeySuffix = _settings.AlwaysReloadSnapshots ? $":run={hotToken}" : string.Empty
            };

            _logger.LogInformation(
                "Criterion {Feed}: 1 snapshot work unit over {From} ({Hot})",
                _feed.FeedId, _feed.FromClause.Split('\n')[0], unit.IsHot ? "hot" : "settled");

            return new[] { unit };
        }

        var today = CriterionTime.Today(context.StartedAtUtc);

        // ⚠ CLAMPED. A negative DaysBack would invert the window and produce zero
        // units — a loader that silently does nothing. A negative SettledAfterDays
        // would make `age > SettledAfterDays` true for EVERY day, settling the whole
        // window on stable keys so revisions are never re-pulled again.
        var daysBack = Math.Max(0, _settings.DaysBack);
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);
        var start = today.AddDays(-daysBack);

        var units = new List<CriterionWorkUnit>();
        var hot = 0;

        for (var day = today; day >= start; day = day.AddDays(-1))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var ageDays = today.DayNumber - day.DayNumber;
            var isHot = ageDays <= settledAfterDays;
            if (isHot) hot++;

            var suffix = isHot ? $":run={hotToken}" : string.Empty;

            if (_feed.WindowMode == CriterionWindowMode.DayWindow)
            {
                units.Add(new CriterionWorkUnit { Feed = _feed, Day = day, KeySuffix = suffix });
                continue;
            }

            // PagedDayWindow: enumerate the day's source keys, then chunk them.
            var keys = await _source
                .GetPageKeysAsync(_feed, day, context.CancellationToken)
                .ConfigureAwait(false);

            if (keys.Count == 0)
            {
                _logger.LogDebug("Criterion {Feed}: {Day} has no source rows — no work units",
                    _feed.FeedId, CriterionTime.Iso(day));
                continue;
            }

            var pageSize = Math.Max(1, _settings.SeriesPageSize);
            for (var page = 0; page * pageSize < keys.Count; page++)
            {
                units.Add(new CriterionWorkUnit
                {
                    Feed = _feed,
                    Day = day,
                    Page = page,
                    Keys = keys.Skip(page * pageSize).Take(pageSize).ToArray(),
                    KeySuffix = suffix
                });
            }
        }

        // Newest first: the day everyone actually cares about lands before the
        // backfill tail if the run is cut short. Correctness never depends on this —
        // ParallelRunner may complete units in any order, and the merges are
        // order-independent (see sql/Criterion/003).
        _logger.LogInformation(
            "Criterion {Feed}: {Count} work unit(s) for {Start}..{End} ({Hot} hot day(s), {Settled} settled)",
            _feed.FeedId, units.Count, CriterionTime.Iso(start), CriterionTime.Iso(today),
            hot, (daysBack + 1) - hot);

        return units;
    }

    /// <summary>
    /// The run-varying component of a hot key. UTC in every case — see
    /// <see cref="CriterionTime"/> for why a local-zone hour token would break
    /// monotonicity.
    /// </summary>
    internal string HotToken(LoaderRunContext context) => _settings.HotKeyStrategy switch
    {
        CriterionHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", CriterionTime.Inv),
        CriterionHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMdd", CriterionTime.Inv)
    };
}
