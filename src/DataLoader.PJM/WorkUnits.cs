using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM;

/// <summary>Resume-key zone of a unit (design §3.2, §14.3).</summary>
public enum PjmZone
{
    /// <summary>The unit ended less than <c>RecentHours</c> ago (or has not ended): keyed on the run HOUR.</summary>
    Recent,

    /// <summary>Inside the correction horizon: keyed on the run DATE (re-read once per day).</summary>
    Hot,

    /// <summary>Older: stable key, read once successfully and skipped forever after.</summary>
    Settled,

    /// <summary>A snapshot unit (<c>load_frcstd_7_day</c>): keyed on the publication slot (§14.3).</summary>
    Snapshot
}

/// <summary>
/// One UTC window of one feed — a UTC day, a <see cref="PjmFeed.UnitHours"/> block of a day, or
/// a snapshot (design §3.1, §14.2). The key never carries the API key, a URL or a run id — only
/// feed, day/block/publication slot and a date/hour token.
/// </summary>
public sealed class PjmDayWorkUnit : WorkUnit
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public required PjmFeed Feed { get; init; }

    /// <summary>The UTC calendar day of <see cref="WindowStartUtc"/> (the run day for a snapshot).</summary>
    public required DateOnly Day { get; init; }

    /// <summary>The run's UTC calendar day ("today"), used for the empty-read age rule (design §6.2).</summary>
    public required DateOnly RunDay { get; init; }

    public required PjmZone Zone { get; init; }

    /// <summary>Precomputed resume key.</summary>
    public required string KeyValue { get; init; }

    /// <summary>First UTC hour of the block (0 for a day unit).</summary>
    public int BlockStartHour { get; init; }

    /// <summary>The run's <c>StartedAtUtc</c> (informational; the snapshot checks use the read time, design §14.8).</summary>
    public DateTime? RunStartedUtc { get; init; }

    /// <summary>Snapshot only: the publication slot the key is built on (latest :15/:45 at or before the run start).</summary>
    public DateTime? PublicationSlotUtc { get; init; }

    public override string Key => KeyValue;

    /// <summary><c>today − Day</c> in days; 0 = today.</summary>
    public int Age => RunDay.DayNumber - Day.DayNumber;

    public bool IsSnapshot => Feed.Kind == PjmFeedKind.Snapshot;

    /// <summary>Inclusive window start, <c>HH:00</c> UTC.</summary>
    public DateTime WindowStartUtc => Day.ToDateTime(new TimeOnly(BlockStartHour, 0));

    /// <summary>Inclusive minute of the window end, <c>HH+UnitHours−1:59</c> UTC (never touches the next block's <c>:00</c>).</summary>
    public DateTime WindowEndUtc => WindowStartUtc.AddHours(Feed.UnitHours).AddMinutes(-1);

    /// <summary>
    /// <c>MM/dd/yyyy HH:mmtoMM/dd/yyyy HH:mm</c> — invariant culture, literal <c>to</c>, inclusive
    /// minute-resolution upper bound (design §3.1, §14.2). <c>null</c> for a snapshot (no filter).
    /// For a day unit this is exactly <c>MM/dd/yyyy 00:00toMM/dd/yyyy 23:59</c>.
    /// </summary>
    public string? FilterValue => IsSnapshot ? null : FormatFilter(WindowStartUtc, WindowEndUtc);

    /// <summary>Short label used in every message: <c>yyyy-MM-dd</c> for a day unit (unchanged).</summary>
    public string Label
    {
        get
        {
            if (IsSnapshot)
                return PublicationSlotUtc is { } p ? $"snapshot pub={p.ToString("yyyyMMddHHmm", Inv)}" : "snapshot";
            var day = Day.ToString("yyyy-MM-dd", Inv);
            return Feed.UnitHours >= 24
                ? day
                : $"{day} {WindowStartUtc.ToString("HH:mm", Inv)}-{WindowEndUtc.ToString("HH:mm", Inv)} UTC";
        }
    }

    public override string DisplayName =>
        IsSnapshot
            ? $"PJM {Feed.Name} {Label}"
            : Feed.UnitHours >= 24
                ? $"PJM {Feed.Name} {Day.ToString("yyyy-MM-dd", Inv)} ({Zone.ToString().ToLowerInvariant()})"
                : $"PJM {Feed.Name} {Day.ToString("yyyy-MM-dd", Inv)}T{BlockStartHour.ToString("00", Inv)} ({Zone.ToString().ToLowerInvariant()})";

    public static string FormatFilter(DateOnly day)
    {
        var d = day.ToString("MM/dd/yyyy", Inv);
        return $"{d} 00:00to{d} 23:59";
    }

    public static string FormatFilter(DateTime startUtc, DateTime endUtc) =>
        $"{startUtc.ToString("MM/dd/yyyy HH:mm", Inv)}to{endUtc.ToString("MM/dd/yyyy HH:mm", Inv)}";
}

/// <summary>
/// Enumerates one feed's units, newest first (design §3.2, §14.3). One class, constructed per
/// feed inside the module's factory closure — never resolved from DI.
/// </summary>
public sealed class PjmWorkUnitProvider : IWorkUnitProvider<PjmDayWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PjmFeed _feed;
    private readonly PjmSettings _settings;
    private readonly ILogger _logger;

    public PjmWorkUnitProvider(PjmFeed feed, PjmSettings settings, ILogger logger)
    {
        _feed = feed;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<PjmDayWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var units = Build(_feed, _settings, context.StartedAtUtc);
        if (_feed.Kind == PjmFeedKind.Snapshot)
            _logger.LogInformation("[PJM {Feed}] 1 snapshot unit ({Label})", _feed.Name, units[0].Label);
        else
            _logger.LogInformation(
                "[PJM {Feed}] {Count} unit(s): {Recent} recent, {Hot} hot, {Settled} settled (depth {Depth} days, settle {Settle} days, {UnitHours}-hour units)",
                _feed.Name, units.Count,
                units.Count(u => u.Zone == PjmZone.Recent),
                units.Count(u => u.Zone == PjmZone.Hot),
                units.Count(u => u.Zone == PjmZone.Settled),
                _feed.EffectiveDepth(_settings.DaysBack),
                _feed.EffectiveSettledAfterDays(_settings.SettledAfterDays),
                _feed.UnitHours);
        return Task.FromResult<IReadOnlyList<PjmDayWorkUnit>>(units);
    }

    /// <summary>
    /// Pure enumeration, exposed for tests (design §14.3 pseudo-code; for the two original feeds it
    /// reduces exactly to §3.2, with byte-identical keys).
    /// </summary>
    internal static List<PjmDayWorkUnit> Build(PjmFeed feed, PjmSettings settings, DateTime startedAtUtc)
    {
        var nowUtc = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc);
        var today = PjmTime.UtcDay(nowUtc);

        if (feed.Kind == PjmFeedKind.Snapshot)
            return new List<PjmDayWorkUnit> { BuildSnapshot(feed, nowUtc, today) };

        if (feed.UnitHours <= 0 || 24 % feed.UnitHours != 0)
            throw new InvalidOperationException($"PJM {feed.Name}: UnitHours {feed.UnitHours} does not divide 24");

        var depth = Math.Max(0, feed.EffectiveDepth(settings.DaysBack));
        var settledAfter = feed.EffectiveSettledAfterDays(settings.SettledAfterDays);
        var recentHours = TimeSpan.FromHours(Math.Max(0, feed.EffectiveRecentHours(settings.RecentHours)));
        var runDate = today.ToString("yyyyMMdd", Inv);
        var runHour = nowUtc.ToString("yyyyMMddHH", Inv);
        var blocksPerDay = 24 / feed.UnitHours;

        var units = new List<PjmDayWorkUnit>((depth + 1) * blocksPerDay);
        for (var age = feed.IncludeToday ? 0 : 1; age <= depth; age++)
        {
            var d = today.AddDays(-age);
            var dayStart = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            // Newest first within the day too.
            for (var b = blocksPerDay - 1; b >= 0; b--)
            {
                var startHour = b * feed.UnitHours;
                var start = dayStart.AddHours(startHour);
                if (start > nowUtc) continue; // a block that has not begun yet

                var end = start.AddHours(feed.UnitHours);
                var keyBase = feed.UnitHours == 24
                    ? $"pjm:{feed.Name}:{d.ToString("yyyyMMdd", Inv)}"
                    : $"pjm:{feed.Name}:{d.ToString("yyyyMMdd", Inv)}T{startHour.ToString("00", Inv)}";

                PjmZone zone;
                string key;
                if (nowUtc < end + recentHours)
                {
                    zone = PjmZone.Recent;
                    key = $"{keyBase};run={runHour}";
                }
                else if (d > today.AddDays(-settledAfter))
                {
                    zone = PjmZone.Hot;
                    key = $"{keyBase};run={runDate}";
                }
                else
                {
                    zone = PjmZone.Settled;
                    key = keyBase;
                }

                units.Add(new PjmDayWorkUnit
                {
                    Feed = feed,
                    Day = d,
                    RunDay = today,
                    Zone = zone,
                    KeyValue = key,
                    BlockStartHour = startHour,
                    RunStartedUtc = nowUtc,
                });
            }
        }

        return units;
    }

    /// <summary>
    /// The snapshot unit (design §14.3, §14.8): key <c>pjm:{feed}:pub={yyyyMMddHHmm}</c>, where
    /// <c>pub</c> is the latest <c>:15</c> or <c>:45</c> UTC at or before the run start. A re-run in
    /// the same slot is skipped by <c>usp_BeginLoadLog</c>; the next slot reads again. Never a run
    /// id, a URL or the key.
    /// </summary>
    internal static PjmDayWorkUnit BuildSnapshot(PjmFeed feed, DateTime nowUtc, DateOnly today)
    {
        var pub = PublicationSlot(nowUtc);
        return new PjmDayWorkUnit
        {
            Feed = feed,
            Day = today,
            RunDay = today,
            Zone = PjmZone.Snapshot,
            KeyValue = $"pjm:{feed.Name}:pub={pub.ToString("yyyyMMddHHmm", Inv)}",
            RunStartedUtc = nowUtc,
            PublicationSlotUtc = pub,
        };
    }

    /// <summary>Latest <c>:15</c> or <c>:45</c> at or before <paramref name="nowUtc"/> (14:14 → 13:45, 14:15 → 14:15, 14:44 → 14:15).</summary>
    public static DateTime PublicationSlot(DateTime nowUtc)
    {
        var hour = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
        return nowUtc.Minute switch
        {
            >= 45 => hour.AddMinutes(45),
            >= 15 => hour.AddMinutes(15),
            _ => hour.AddMinutes(-15),
        };
    }
}
