using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>Design §6: enumeration, resume keys (settled vs hot), MTLF pairs, lookback clamp, order, and the invariant.</summary>
public sealed class WorkUnitTests
{
    /// <summary>2026-11-24 14:00 UTC = 09:00 EST → todayEst 2026-11-24.</summary>
    private static readonly DateTime Started = new(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = Samples.D(2026, 11, 24);

    private static List<MisoWorkUnit> Build(MisoFeed feed, Action<MisoSettings>? configure = null, DateTime? started = null) =>
        MisoWorkUnitProvider.Build(feed, Samples.Settings(configure), started ?? Started);

    [Fact]
    public void TodayEst_is_the_EST_calendar_day_not_the_UTC_one()
    {
        Assert.Equal(Samples.D(2026, 11, 23), MisoTime.TodayEst(new DateTime(2026, 11, 24, 4, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(Samples.D(2026, 11, 24), MisoTime.TodayEst(new DateTime(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc)));
        // Summer: still a FIXED −5h (EDT would roll at 04:00 UTC).
        Assert.Equal(Samples.D(2026, 7, 14), MisoTime.TodayEst(new DateTime(2026, 7, 15, 4, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Day_feed_defaults_clamp_to_MaxLookbackDays_and_end_yesterday()
    {
        var units = Build(MisoFeeds.ZonalLoadHourly);
        // StartDate 2023-11-20 is older than today − 1095 = 2023-11-25, so the lookback wins.
        Assert.Equal(Today.AddDays(-1095), Samples.D(2023, 11, 25));
        Assert.Equal(1095, units.Count);
        Assert.Equal(Today.AddDays(-1), units.First().MarketDate);
        Assert.Equal(Samples.D(2023, 11, 25), units.Last().MarketDate);
        Assert.DoesNotContain(units, u => u.MarketDate >= Today);
        Assert.All(units, u => Assert.Null(u.InitDate));
    }

    [Fact]
    public void A_later_StartDate_is_the_floor_when_it_is_inside_the_lookback()
    {
        var units = Build(MisoFeeds.LoadHourly, s => s.StartDate = Samples.D(2026, 1, 1));
        Assert.Equal(Samples.D(2026, 1, 1), units.Last().MarketDate);
        Assert.Equal(Today.DayNumber - Samples.D(2026, 1, 1).DayNumber, units.Count);
    }

    [Fact]
    public void The_last_DaysBack_days_are_always_included_even_if_StartDate_is_later()
    {
        var units = Build(MisoFeeds.FuelMixHourly, s => s.StartDate = Today.AddDays(5));
        Assert.Equal(Enumerable.Range(1, 10).Select(a => Today.AddDays(-a)), units.Select(u => u.MarketDate));
    }

    [Fact]
    public void Settled_and_hot_keys_split_at_SettledAfterDays()
    {
        var units = Build(MisoFeeds.ZonalLoadHourly).ToDictionary(u => u.Age);

        Assert.Equal(MisoZone.Hot, units[10].Zone);
        Assert.Equal("MISO|zonal_load_hourly|2026-11-14|run=2026-11-24", units[10].Key);
        Assert.Equal(MisoZone.Settled, units[11].Zone);
        Assert.Equal("MISO|zonal_load_hourly|2026-11-13", units[11].Key);
        Assert.Equal("MISO|zonal_load_hourly|2026-11-23|run=2026-11-24", units[1].Key);

        Assert.All(units.Values, u => Assert.Equal(u.Age > 10 ? MisoZone.Settled : MisoZone.Hot, u.Zone));
        Assert.All(units.Values.Where(u => u.Zone == MisoZone.Hot), u => Assert.EndsWith("|run=2026-11-24", u.Key));
        Assert.All(units.Values.Where(u => u.Zone == MisoZone.Settled), u => Assert.DoesNotContain("run=", u.Key));
        Assert.Equal(units.Count, units.Values.Select(u => u.Key).Distinct().Count());
    }

    [Fact]
    public void Hot_key_rolls_over_once_per_EST_day()
    {
        var morning = Build(MisoFeeds.LoadHourly, started: new DateTime(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc));
        var evening = Build(MisoFeeds.LoadHourly, started: new DateTime(2026, 11, 25, 4, 59, 0, DateTimeKind.Utc));
        Assert.Equal(morning.Select(u => u.Key), evening.Select(u => u.Key));   // same EST day → same keys (re-run skips)

        var nextDay = Build(MisoFeeds.LoadHourly, started: new DateTime(2026, 11, 25, 5, 0, 0, DateTimeKind.Utc));
        Assert.Contains("MISO|load_hourly|2026-11-24|run=2026-11-25", nextDay.Select(u => u.Key));
    }

    [Fact]
    public void Units_are_newest_first()
    {
        var units = Build(MisoFeeds.ZonalLoadHourly);
        Assert.Equal(units.OrderByDescending(u => u.MarketDate).Select(u => u.MarketDate), units.Select(u => u.MarketDate));

        var mtlf = Build(MisoFeeds.LoadForecastMtlf);
        Assert.Equal(mtlf.OrderByDescending(u => u.InitDate).Select(u => u.InitDate), mtlf.Select(u => u.InitDate));
    }

    [Fact]
    public void Mtlf_has_seven_forecast_dates_per_init_keyed_and_zoned_by_init()
    {
        var units = Build(MisoFeeds.LoadForecastMtlf);
        Assert.Equal(1095 * 7, units.Count);

        foreach (var g in units.GroupBy(u => u.InitDate!.Value))
        {
            Assert.Equal(Enumerable.Range(1, 7).Select(h => g.Key.AddDays(h)), g.Select(u => u.MarketDate));
            Assert.All(g, u => Assert.Equal(Today.DayNumber - g.Key.DayNumber, u.Age));
        }

        var latest = units.Where(u => u.InitDate == Today.AddDays(-1)).ToList();
        Assert.Equal("MISO|load_forecast_mtlf|init=2026-11-23|2026-11-24|run=2026-11-24", latest[0].Key);
        Assert.Equal("MISO|load_forecast_mtlf|init=2026-11-23|2026-11-30|run=2026-11-24", latest[6].Key);
        Assert.All(latest, u => Assert.Equal(MisoZone.Hot, u.Zone));   // forecast dates in the future are fine; age is from I

        var settled = units.First(u => u.InitDate == Today.AddDays(-11));
        Assert.Equal(MisoZone.Settled, settled.Zone);
        Assert.Equal("MISO|load_forecast_mtlf|init=2026-11-13|2026-11-14", settled.Key);
        Assert.All(units, u => Assert.True(u.MarketDate > u.InitDate && u.MarketDate <= u.InitDate!.Value.AddDays(7)));
    }

    [Fact]
    public void MtlfHorizonDays_limits_the_pairs_per_init()
    {
        var units = Build(MisoFeeds.LoadForecastMtlf, s => { s.MtlfHorizonDays = 3; s.StartDate = Today; });
        Assert.Equal(10 * 3, units.Count);
        Assert.All(units.GroupBy(u => u.InitDate), g => Assert.Equal(3, g.Count()));
    }

    [Fact]
    public void Lookback_clamp_follows_MaxLookbackDays()
    {
        var units = Build(MisoFeeds.ZonalLoadHourly, s => s.MaxLookbackDays = 30);
        Assert.Equal(30, units.Count);
        Assert.Equal(Today.AddDays(-30), units.Last().MarketDate);
    }

    [Fact]
    public void Keys_never_carry_the_key_or_a_url()
    {
        foreach (var feed in MisoFeeds.All)
            foreach (var u in Build(feed, s => s.StartDate = Today))
            {
                Assert.DoesNotContain(Samples.Key, u.Key);
                Assert.DoesNotContain("http", u.Key);
                Assert.StartsWith($"MISO|{feed.Id}|", u.Key);
            }
    }

    /// <summary>
    /// Design §6: a not-yet or young-empty outcome (age ≤ ExpectRowsWithinDays) must never leave a
    /// settled key behind. For every configuration Validate accepts, every unit that young is HOT.
    /// </summary>
    [Fact]
    public void Invariant_every_unit_young_enough_to_be_not_yet_or_empty_is_hot()
    {
        for (var settled = 1; settled <= 15; settled++)
        for (var expect = 1; expect <= 6; expect++)
        {
            var s = Samples.Settings(x => { x.SettledAfterDays = settled; x.ExpectRowsWithinDays = expect; x.StartDate = Today.AddDays(-40); });
            if (s.Validate().Count > 0)
            {
                Assert.True(settled < expect, $"settled={settled} expect={expect} rejected for another reason: {string.Join(" ", s.Validate())}");
                continue;
            }

            foreach (var feed in MisoFeeds.All)
                foreach (var u in MisoWorkUnitProvider.Build(feed, s, Started).Where(u => u.Age <= expect))
                    Assert.Equal(MisoZone.Hot, u.Zone);
        }
    }

    [Fact]
    public void Validate_rejects_a_configuration_that_would_settle_a_young_day()
    {
        var errors = Samples.Settings(s => { s.SettledAfterDays = 2; s.ExpectRowsWithinDays = 3; }).Validate();
        Assert.Contains(errors, e => e.Contains("SettledAfterDays (2) must be at least ExpectRowsWithinDays (3)"));
    }
}
