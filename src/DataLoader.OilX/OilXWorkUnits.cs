using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.OilX;

/// <summary>
/// One unit of work: <b>one feed, one day</b>.
///
/// <para>
/// Not one unit per FILE, even though a day publishes up to four: a day's snapshots all
/// carry the same in-file <c>RunDate</c>, so they collide on <c>(RunDate, RowId)</c> and
/// must merge oldest-to-newest. <c>ParallelRunner</c> gives no ordering between units,
/// so file-level units could leave a row at whichever snapshot happened to finish last.
/// The unit therefore owns the whole day and walks its files sequentially.
/// </para>
/// <para>
/// Not one unit per FEED either: <c>RunDate</c> leads the primary key, and a unit
/// spanning days could neither be resumed nor reported per day.
/// </para>
/// <para>
/// Units of the same feed but different days DO run in parallel safely — different
/// <c>RunDate</c>s touch disjoint keys.
/// </para>
/// </summary>
public sealed class OilXWorkUnit : WorkUnit
{
    public OilXWorkUnit(OilXFeedDescriptor feed, DateOnly day, bool settled, string resumeKey)
    {
        Feed = feed;
        Day = day;
        Settled = settled;
        Key = resumeKey;
    }

    public OilXFeedDescriptor Feed { get; }
    public DateOnly Day { get; }

    /// <summary>
    /// True when this day's published files can no longer change, so the unit carries a
    /// STABLE key and is loaded exactly once. See <see cref="OilXSettings.SettledAfterDays"/>.
    /// </summary>
    public bool Settled { get; }

    public override string Key { get; }

    public override string DisplayName => $"{Feed.FeedId} {OilXTime.DayToken(Day)}";
}

/// <summary>
/// Produces the (feed, day) units for one feed over the configured window.
///
/// <para>
/// This provider makes NO network calls. The manifest is fetched inside each unit, at
/// the moment it runs, because the URLs it hands back expire in five hours
/// (<c>docs/apis/OilX.md</c> §2 Behaviour 3). Enumerating here would also defeat the
/// settled-day skip: a settled unit must cost nothing at all, and
/// <c>ILoadLogRepository.BeginAsync</c> already returns <c>null</c> for it before any
/// work happens.
/// </para>
/// </summary>
public sealed class OilXWorkUnitProvider : IWorkUnitProvider<OilXWorkUnit>
{
    private readonly OilXFeedDescriptor _feed;
    private readonly OilXSettings _settings;
    private readonly ILogger _logger;

    public OilXWorkUnitProvider(OilXFeedDescriptor feed, OilXSettings settings, ILogger logger)
    {
        _feed = feed;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<OilXWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var today = OilXTime.Today(context.StartedAtUtc);
        var window = OilXTime.Window(today, _settings.DaysBack);

        var units = new List<OilXWorkUnit>(window.Count);
        var hot = 0;

        foreach (var day in window)
        {
            var settled = OilXTime.IsSettled(day, today, _settings.SettledAfterDays);
            if (!settled) hot++;

            var key = BuildKey(_feed, day, settled, _settings.HotKeyStrategy,
                               context.StartedAtUtc, context.RunId);

            units.Add(new OilXWorkUnit(_feed, day, settled, key));
        }

        _logger.LogInformation(
            "OilX {Feed}: {Total} day(s) {From}..{To} — {Hot} hot, {Settled} settled",
            _feed.FeedId, units.Count,
            OilXTime.DayToken(window[0]), OilXTime.DayToken(window[^1]),
            hot, units.Count - hot);

        return Task.FromResult<IReadOnlyList<OilXWorkUnit>>(units);
    }

    /// <summary>
    /// The resume key.
    ///
    /// <para>
    /// A SETTLED day gets <c>feed=…;day=…</c> — stable forever, so once the load log
    /// records it the unit is skipped without a manifest call or a download. That is the
    /// whole reason a 31-day window does not re-fetch ~6.5 GB on every run.
    /// </para>
    /// <para>
    /// A HOT day gets a run-varying suffix so new intraday snapshots are picked up.
    /// Tokens are stamped from <paramref name="startedAtUtc"/>, which is UTC — see
    /// <see cref="OilXHotKeyStrategy"/> for why a local-zone hour token would go
    /// backwards once a year and suppress a legitimate re-pull.
    /// </para>
    /// </summary>
    internal static string BuildKey(
        OilXFeedDescriptor feed,
        DateOnly day,
        bool settled,
        OilXHotKeyStrategy strategy,
        DateTime startedAtUtc,
        Guid runId)
    {
        var baseKey = $"feed={feed.FeedId};day={OilXTime.DayToken(day)}";
        if (settled) return baseKey;

        // An unmapped strategy THROWS rather than falling back to the stable key: a
        // silent fallback would freeze a hot day and quietly stop picking up the new
        // snapshots the hot zone exists to catch.
        var token = strategy switch
        {
            OilXHotKeyStrategy.RunDate => startedAtUtc.ToString("yyyyMMdd", OilXTime.Inv),
            OilXHotKeyStrategy.RunHour => startedAtUtc.ToString("yyyyMMddHH", OilXTime.Inv),
            OilXHotKeyStrategy.RunId => runId.ToString("N"),
            _ => throw new NotSupportedException($"Unmapped OilXHotKeyStrategy '{strategy}'.")
        };

        return $"{baseKey};run={token}";
    }
}
