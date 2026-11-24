using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.MISO;

/// <summary>Resume-key zone (design §6).</summary>
public enum MisoZone
{
    /// <summary><c>age ≤ SettledAfterDays</c>: keyed on the EST run date (re-read once per EST day).</summary>
    Hot,

    /// <summary>Older: stable key, read once successfully and skipped forever after.</summary>
    Settled,
}

/// <summary>
/// One market date of a day feed, or one <c>(init, forecast date)</c> pair of MTLF (design §6).
/// The key never carries the API key, a URL or a run id.
/// </summary>
public sealed class MisoWorkUnit : WorkUnit
{
    public required MisoFeed Feed { get; init; }

    /// <summary>The requested market / forecast date <c>D</c>.</summary>
    public required DateOnly MarketDate { get; init; }

    /// <summary>MTLF only: the forecast-run date <c>I</c>.</summary>
    public DateOnly? InitDate { get; init; }

    /// <summary>The run's EST calendar day.</summary>
    public required DateOnly TodayEst { get; init; }

    public required MisoZone Zone { get; init; }

    public required string KeyValue { get; init; }

    public override string Key => KeyValue;

    /// <summary><c>todayEst − D</c> for a day feed; <c>todayEst − I</c> for MTLF (design §6).</summary>
    public int Age => TodayEst.DayNumber - (InitDate ?? MarketDate).DayNumber;

    public string Label => InitDate is { } i
        ? $"init={MisoTime.Format(i)} {MisoTime.Format(MarketDate)}"
        : MisoTime.Format(MarketDate);

    public override string DisplayName => $"MISO {Feed.Id} {Label} ({Zone.ToString().ToLowerInvariant()})";
}

/// <summary>Enumerates one feed's units, newest first (design §6). Constructed per feed by the module.</summary>
public sealed class MisoWorkUnitProvider : IWorkUnitProvider<MisoWorkUnit>
{
    private readonly MisoFeed _feed;
    private readonly MisoSettings _settings;
    private readonly ILogger _logger;

    public MisoWorkUnitProvider(MisoFeed feed, MisoSettings settings, ILogger logger)
    {
        _feed = feed;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<MisoWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var units = Build(_feed, _settings, context.StartedAtUtc);
        _logger.LogInformation("[MISO {Feed}] {Count} unit(s): {Hot} hot, {Settled} settled",
            _feed.Id, units.Count, units.Count(u => u.Zone == MisoZone.Hot), units.Count(u => u.Zone == MisoZone.Settled));
        return Task.FromResult<IReadOnlyList<MisoWorkUnit>>(units);
    }

    /// <summary>
    /// <c>Start = max(StartDate, todayEst − MaxLookbackDays)</c>, widened to always cover the last
    /// <c>DaysBack</c> days; <c>End = todayEst − 1</c>. Pure; exposed for tests.
    /// </summary>
    internal static (DateOnly Start, DateOnly End, DateOnly TodayEst) Range(MisoSettings settings, DateTime startedAtUtc)
    {
        var today = MisoTime.TodayEst(startedAtUtc);
        var end = today.AddDays(-1);
        var start = settings.StartDate;
        var lookbackFloor = today.AddDays(-Math.Max(0, settings.MaxLookbackDays));
        if (start < lookbackFloor) start = lookbackFloor;
        var daysBackFloor = today.AddDays(-Math.Max(1, settings.DaysBack));
        if (start > daysBackFloor) start = daysBackFloor;
        return (start, end, today);
    }

    internal static List<MisoWorkUnit> Build(MisoFeed feed, MisoSettings settings, DateTime startedAtUtc)
    {
        var (start, end, today) = Range(settings, startedAtUtc);
        var units = new List<MisoWorkUnit>();
        var run = MisoTime.Format(today);

        // Newest first: a partial run lands the freshest data first.
        for (var d = end; d >= start; d = d.AddDays(-1))
        {
            var age = today.DayNumber - d.DayNumber;
            var zone = age > settings.SettledAfterDays ? MisoZone.Settled : MisoZone.Hot;

            if (feed.Kind == MisoFeedKind.MarketDay)
            {
                var key = $"MISO|{feed.Id}|{MisoTime.Format(d)}";
                units.Add(new MisoWorkUnit
                {
                    Feed = feed, MarketDate = d, TodayEst = today, Zone = zone,
                    KeyValue = zone == MisoZone.Settled ? key : $"{key}|run={run}",
                });
            }
            else
            {
                // d is the init date I; forecast dates I+1 … I+H, nearest horizon first.
                for (var h = 1; h <= settings.MtlfHorizonDays; h++)
                {
                    var target = d.AddDays(h);
                    var key = $"MISO|{feed.Id}|init={MisoTime.Format(d)}|{MisoTime.Format(target)}";
                    units.Add(new MisoWorkUnit
                    {
                        Feed = feed, MarketDate = target, InitDate = d, TodayEst = today, Zone = zone,
                        KeyValue = zone == MisoZone.Settled ? key : $"{key}|run={run}",
                    });
                }
            }
        }

        return units;
    }
}
