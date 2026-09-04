using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.ICE;

/// <summary>
/// One work unit = one FEED × one TRADE DATE = one file = one merge.
///
/// <para>
/// <b>Resume key</b> (the idempotency contract — <c>core.LoadLog</c> skips a key
/// already recorded successful):
/// </para>
/// <code>
///   settled:  ice:{FeedId}:{yyyy-MM-dd}
///   hot:      ice:{FeedId}:{yyyy-MM-dd}:run={token}
/// </code>
/// <para>
/// A trade date older than <see cref="IceSettings.SettledAfterDays"/> gets the
/// STABLE key — loaded once, then a cheap skip forever. A newer one gets the
/// run-varying HOT key so ICE's revised settlements are picked up.
/// </para>
/// <para>
/// With the shipped 30/30 defaults every date in the window is hot; see
/// <see cref="IceSettings.SettledAfterDays"/> for why that is deliberate.
/// </para>
/// </summary>
public sealed class IceWorkUnit : WorkUnit
{
    public required IceFeedDescriptor Feed { get; init; }
    public required DateOnly TradeDate { get; init; }

    /// <summary>Empty for a settled date; <c>:run=…</c> for a hot one.</summary>
    public string KeySuffix { get; init; } = string.Empty;

    /// <summary>True when this date is inside the hot zone. Logged, and asserted in tests.</summary>
    public bool IsHot => KeySuffix.Length > 0;

    public override string Key => $"ice:{Feed.FeedId}:{IceTime.Iso(TradeDate)}{KeySuffix}";

    public override string DisplayName => $"ICE {Feed.FeedId} {IceTime.Iso(TradeDate)}";
}

/// <summary>
/// Produces one work unit per trade date in the <see cref="IceSettings.DaysBack"/>
/// window for a single feed.
///
/// <para>
/// The window is <c>[today - DaysBack, today]</c> inclusive, in US Central
/// (<see cref="IceTime"/>). Weekends and holidays are NOT filtered out: ICE does not
/// publish a trading calendar with these files, and a missing day is already a
/// first-class non-error outcome (<c>NotAvailable</c>) that costs one small HTTP
/// response. Guessing at a calendar would risk skipping a day ICE did publish —
/// the failure mode that actually loses data.
/// </para>
/// </summary>
public sealed class IceWorkUnitProvider : IWorkUnitProvider<IceWorkUnit>
{
    private readonly IceFeedDescriptor _feed;
    private readonly IceSettings _settings;
    private readonly ILogger _logger;

    public IceWorkUnitProvider(IceFeedDescriptor feed, IceSettings settings, ILogger logger)
    {
        _feed = feed;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<IceWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var today = IceTime.Today(context.StartedAtUtc);

        // ⚠ CLAMPED. A negative DaysBack would invert the window and produce zero
        // units — a loader that silently does nothing. A negative SettledAfterDays
        // would make `age > SettledAfterDays` true for EVERY date, settling the whole
        // window on stable keys so revisions are never re-pulled again.
        var daysBack = Math.Max(0, _settings.DaysBack);
        var settledAfterDays = Math.Max(0, _settings.SettledAfterDays);

        var start = today.AddDays(-daysBack);
        var hotToken = HotToken(context);

        var units = new List<IceWorkUnit>();
        var hot = 0;

        for (var date = start; date <= today; date = date.AddDays(1))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var ageDays = today.DayNumber - date.DayNumber;
            var isHot = ageDays <= settledAfterDays;
            if (isHot) hot++;

            units.Add(new IceWorkUnit
            {
                Feed = _feed,
                TradeDate = date,
                KeySuffix = isHot ? $":run={hotToken}" : string.Empty
            });
        }

        // Newest first: the day everyone actually cares about lands before the
        // backfill tail if the run is cut short. Correctness never depends on this —
        // ParallelRunner may complete units in any order, and the merges are
        // order-independent (see sql/ICE/003).
        units.Reverse();

        _logger.LogInformation(
            "ICE {Feed}: {Count} work unit(s) for {Start}..{End} ({Hot} hot, {Settled} settled)",
            _feed.FeedId, units.Count, IceTime.Iso(start), IceTime.Iso(today), hot, units.Count - hot);

        return Task.FromResult<IReadOnlyList<IceWorkUnit>>(units);
    }

    /// <summary>
    /// The run-varying component of a hot key. UTC in every case — see
    /// <see cref="IceTime"/> for why a local-zone hour token would break monotonicity.
    /// </summary>
    internal string HotToken(LoaderRunContext context) => _settings.HotKeyStrategy switch
    {
        IceHotKeyStrategy.RunHour => context.StartedAtUtc.ToString("yyyyMMddHH", IceTime.Inv),
        IceHotKeyStrategy.RunId => context.RunId.ToString("N"),
        _ => context.StartedAtUtc.ToString("yyyyMMdd", IceTime.Inv)
    };
}
