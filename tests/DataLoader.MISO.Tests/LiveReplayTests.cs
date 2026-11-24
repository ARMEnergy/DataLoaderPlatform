using System.Net;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>
/// Replays REAL MISO responses captured 2026-11-24 (Fixtures/live) through the real HTTP wiring
/// (stub primary only) and the real reader/mappers. No network.
/// </summary>
public sealed class LiveReplayTests
{
    private static readonly DateOnly D18 = Samples.D(2026, 11, 18);

    private static async Task<IReadOnlyList<TRow>> Replay<TRow>(string fixture, IMisoRowMapper<TRow> mapper, MisoWorkUnit unit)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.Fixture(fixture));
        using var h = MisoHarness.Create(stub);
        var rows = await h.Reader(mapper).ReadAsync(unit, default);
        Assert.Equal(1, stub.Count);
        return rows;
    }

    private static MisoWorkUnit Unit(MisoFeed feed, DateOnly d, DateOnly? init = null) => Samples.Unit(feed, d, age: 1, init: init);

    // ---- the GridStatusIO cross-check (design brief) -----------------------------------

    [Fact]
    public async Task Zonal_2026_11_18_HE1_pivots_to_the_GridStatusIO_dbo_values_with_an_exact_decimal_sum()
    {
        var rows = await Replay("actual_lrz_2026-11-18.json", new MisoZonalLoadMapper(), Unit(MisoFeeds.ZonalLoadHourly, D18));
        Assert.Equal(24, rows.Count);

        var he1 = rows[0];
        Assert.Equal(new DateTime(2026, 11, 18, 0, 0, 0), he1.IntervalStartLocal);
        Assert.Equal(10519.1m, he1.Lrz1);
        Assert.Equal(16000.7m, he1.Lrz2_7);
        Assert.Equal(9895.7m, he1.Lrz3_5);
        Assert.Equal(4606.7m, he1.Lrz4);
        Assert.Equal(9843.9m, he1.Lrz6);
        Assert.Equal(18056.9m, he1.Lrz8_9_10);
        Assert.Equal(68923.0m, he1.Miso);
        // Exactly 68923.0 — decimal arithmetic, not double (which would give 68923.00000000001 or similar).
        Assert.Equal("68923.0", he1.Miso!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(rows, r => Assert.Equal(r.Lrz1 + r.Lrz2_7 + r.Lrz3_5 + r.Lrz4 + r.Lrz6 + r.Lrz8_9_10, r.Miso));
    }

    [Fact]
    public async Task Region_load_2026_11_18_title_case_regions_pivot_and_sum_to_the_lrz_total()
    {
        var regions = await Replay("actual_region_2026-11-18.json", new MisoLoadMapper(), Unit(MisoFeeds.LoadHourly, D18));
        var zonal = await Replay("actual_lrz_2026-11-18.json", new MisoZonalLoadMapper(), Unit(MisoFeeds.ZonalLoadHourly, D18));
        Assert.Equal(24, regions.Count);
        Assert.Equal(34040.1m, regions[0].Central);
        Assert.Equal(16826m, regions[0].North);
        Assert.Equal(18056.9m, regions[0].South);
        Assert.Equal(68923.0m, regions[0].Miso);
        // API doc §5.1: "The region sum equals the LRZ sum for every hour checked."
        Assert.Equal(zonal.Select(z => z.Miso), regions.Select(r => r.Miso));
    }

    [Fact]
    public async Task Fuel_2026_11_18_upper_case_regions_one_row_each()
    {
        var rows = await Replay("fuel_type_2026-11-18.json", new MisoFuelMixMapper(), Unit(MisoFeeds.FuelMixHourly, D18));
        Assert.Equal(72, rows.Count);
        var central = rows.Single(r => r.Region == "CENTRAL" && r.IntervalStartLocal.Hour == 0);
        Assert.Equal(9508.0m, central.Coal);
        Assert.Equal(10626.8m, central.Gas);
        Assert.Equal(4087.8m, central.Nuclear);
        Assert.Equal(384.8m, central.Water);
        Assert.Equal(5727.0m, central.Wind);
        Assert.Equal(4.1m, central.Solar);
        Assert.Equal(167.6m, central.Other);
        Assert.Equal(0.1m, central.Storage);
        Assert.Equal(30506.2m, central.TotalMw);
        Assert.Equal(new[] { "CENTRAL", "NORTH", "SOUTH" }, rows.Select(r => r.Region).Distinct().OrderBy(x => x));
    }

    [Fact]
    public async Task Mtlf_2026_11_18_init_2026_11_17_has_240_rows_zone_as_sent()
    {
        var rows = await Replay("mtlf_2026-11-18_init_2026-11-17.json", new MisoLoadForecastMtlfMapper(),
            Unit(MisoFeeds.LoadForecastMtlf, D18, init: Samples.D(2026, 11, 17)));
        Assert.Equal(240, rows.Count);
        Assert.All(rows, r => Assert.Equal(Samples.D(2026, 11, 17), r.InitDate));
        var z1 = rows.Single(r => r.LocalResourceZone == "Z1" && r.IntervalStartLocal.Hour == 0);
        Assert.Equal(10575.000m, z1.LoadForecast);
        Assert.Equal("NORTH", z1.Region);
        Assert.Equal(10, rows.Select(r => r.LocalResourceZone).Distinct().Count());
    }

    [Fact]
    public async Task Mtlf_fixture_requested_with_another_init_fails_the_echo()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.Fixture("mtlf_2026-11-18_init_2026-11-17.json"));
        using var h = MisoHarness.Create(stub);
        await Assert.ThrowsAsync<MisoDataException>(() => h.Reader(new MisoLoadForecastMtlfMapper())
            .ReadAsync(Unit(MisoFeeds.LoadForecastMtlf, D18, init: Samples.D(2026, 11, 16)), default));
    }

    [Fact]
    public async Task A_fixture_served_for_the_wrong_date_fails_the_date_echo()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.Fixture("actual_lrz_2026-11-18.json"));
        using var h = MisoHarness.Create(stub);
        await Assert.ThrowsAsync<MisoDataException>(() => h.Reader(new MisoZonalLoadMapper())
            .ReadAsync(Unit(MisoFeeds.ZonalLoadHourly, D18.AddDays(1)), default));
    }

    // ---- EST → UTC: +5h always; DST days still have 24 intervals ----------------------------

    [Theory]
    [InlineData("actual_lrz_2026-03-08_dst.json", 2026, 3, 8)]
    [InlineData("actual_lrz_2026-11-01_dst.json", 2026, 11, 1)]
    [InlineData("actual_lrz_2026-11-18.json", 2026, 11, 18)]
    public async Task Every_day_including_DST_days_gives_24_intervals_and_utc_is_local_plus_5(string fixture, int y, int m, int d)
    {
        var day = Samples.D(y, m, d);
        var rows = await Replay(fixture, new MisoZonalLoadMapper(), Unit(MisoFeeds.ZonalLoadHourly, day));
        Assert.Equal(24, rows.Count);
        Assert.Equal(24, rows.Select(r => r.IntervalStartUtc).Distinct().Count());
        for (var h = 0; h < 24; h++)
        {
            var r = rows[h];
            Assert.Equal(day.ToDateTime(new TimeOnly(h, 0)), r.IntervalStartLocal);
            Assert.Equal(r.IntervalStartLocal.AddHours(5), r.IntervalStartUtc);
            Assert.Equal(r.IntervalStartLocal.AddHours(1), r.IntervalEndLocal);
            Assert.Equal(r.IntervalEndLocal.AddHours(5), r.IntervalEndUtc);
        }
        Assert.Equal(day.ToDateTime(new TimeOnly(5, 0)), rows[0].IntervalStartUtc);
        Assert.Equal(day.AddDays(1).ToDateTime(new TimeOnly(5, 0)), rows[23].IntervalEndUtc);
    }

    [Fact]
    public async Task Fuel_and_mtlf_on_the_fall_back_day_are_complete_and_fixed_offset()
    {
        var day = Samples.D(2026, 11, 1);
        var fuel = await Replay("fuel_type_2026-11-01_dst.json", new MisoFuelMixMapper(), Unit(MisoFeeds.FuelMixHourly, day));
        Assert.Equal(72, fuel.Count);
        Assert.All(fuel, r => Assert.Equal(r.IntervalStartLocal.AddHours(5), r.IntervalStartUtc));
        Assert.Equal(72, fuel.Select(r => (r.IntervalStartUtc, r.Region)).Distinct().Count());

        var mtlf = await Replay("mtlf_2026-11-01_init_2026-10-31_dst.json", new MisoLoadForecastMtlfMapper(),
            Unit(MisoFeeds.LoadForecastMtlf, day, init: Samples.D(2026, 10, 31)));
        Assert.Equal(240, mtlf.Count);
        Assert.Equal(240, mtlf.Select(r => (r.IntervalStartUtc, r.LocalResourceZone)).Distinct().Count());
        Assert.All(mtlf, r => Assert.Equal(r.IntervalStartLocal.AddHours(5), r.IntervalStartUtc));
    }

    [Theory]
    [InlineData(2026, 1, 15)]
    [InlineData(2026, 7, 15)]   // summer: EDT would be +4; MISO's clock is fixed EST
    [InlineData(2026, 3, 8)]
    [InlineData(2026, 11, 1)]
    public void EstToUtc_is_plus_five_hours_all_year(int y, int m, int d)
    {
        for (var h = 0; h < 24; h++)
        {
            var local = new DateTime(y, m, d, h, 0, 0);
            Assert.Equal(local.AddHours(5), MisoTime.EstToUtc(local));
        }
    }

    // ---- module-level replay: the whole run over fixtures -----------------------------------

    [Fact]
    public async Task Whole_run_over_fixtures_loads_every_feed_and_records_young_empty_days_as_hot_successes()
    {
        // todayEst = 2026-11-19; DaysBack 2 + StartDate 2026-11-17 → D ∈ {11-18, 11-17}; MTLF I ∈ {11-18, 11-17}, H = 1.
        var stub = new StubHttpMessageHandler((r, _) =>
        {
            var (path, q) = Req.Parse(r.RequestUri!);
            string? fixture = path switch
            {
                "/lgi/v1/real-time/2026-11-18/demand/actual" when q["geoResolution"] == "localResourceZone" => "actual_lrz_2026-11-18.json",
                "/lgi/v1/real-time/2026-11-18/demand/actual" when q["geoResolution"] == "region" => "actual_region_2026-11-18.json",
                "/lgi/v1/real-time/2026-11-18/generation/fuel-type" => "fuel_type_2026-11-18.json",
                "/lgi/v1/forecast/2026-11-18/load" when q["init"] == "2026-11-17" => "mtlf_2026-11-18_init_2026-11-17.json",
                _ => null,
            };
            Assert.Equal("1", q["pageNumber"]);
            return fixture is null
                ? StubHttpMessageHandler.Json(HttpStatusCode.NotFound, Samples.Fixture("err_page2.json"))
                : Req.Ok(Samples.Fixture(fixture));
        });

        using var h = MisoHarness.Create(stub, new Dictionary<string, string?>
        {
            ["Loaders:MISO:StartDate"] = "2026-11-17",
            ["Loaders:MISO:MtlfHorizonDays"] = "1",
        });
        var result = await h.RunAsync(new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(8, stub.Count);   // 3 day feeds × 2 dates + MTLF 2 inits × 1
        Assert.Equal(24, h.ZonalSink.AllRows.Count());
        Assert.Equal(24, h.LoadSink.AllRows.Count());
        Assert.Equal(72, h.FuelSink.AllRows.Count());
        Assert.Equal(240, h.MtlfSink.AllRows.Count());
        Assert.Empty(h.LoadLog.Failures);
        Assert.Equal(8, h.LoadLog.Successes.Count);
        Assert.All(h.LoadLog.Successes, s => Assert.EndsWith("|run=2026-11-19", s.Key));   // young empties never settle
        Assert.Contains(h.LoadLog.Successes, s => s.Key == "MISO|zonal_load_hourly|2026-11-17|run=2026-11-19" && s.Records == 0);
        Assert.Contains(h.LoadLog.Successes, s => s.Key == "MISO|zonal_load_hourly|2026-11-18|run=2026-11-19" && s.Records == 24);

        // Execution order is design §1 order: zonal, load, fuel, MTLF.
        var feedsInOrder = stub.Requests.OrderBy(r => r.Index).Select(r => Req.Parse(r.Uri)).Select(p =>
            p.Path.Contains("/forecast/") ? "mtlf" : p.Path.Contains("fuel-type") ? "fuel" : p.Query["geoResolution"]).Distinct();
        Assert.Equal(new[] { "localResourceZone", "region", "fuel", "mtlf" }, feedsInOrder);
    }

    [Fact]
    public void Requests_carry_the_explicit_query_shown_in_design_section_1()
    {
        Assert.Equal("/real-time/2026-11-18/demand/actual?geoResolution=localResourceZone&pageNumber=1",
            MisoApiClient.BuildPathAndQuery(Unit(MisoFeeds.ZonalLoadHourly, D18), 1));
        Assert.Equal("/real-time/2026-11-18/demand/actual?geoResolution=region&pageNumber=2",
            MisoApiClient.BuildPathAndQuery(Unit(MisoFeeds.LoadHourly, D18), 2));
        Assert.Equal("/real-time/2026-11-18/generation/fuel-type?pageNumber=1",
            MisoApiClient.BuildPathAndQuery(Unit(MisoFeeds.FuelMixHourly, D18), 1));
        Assert.Equal("/forecast/2026-11-18/load?init=2026-11-17&pageNumber=1",
            MisoApiClient.BuildPathAndQuery(Unit(MisoFeeds.LoadForecastMtlf, D18, Samples.D(2026, 11, 17)), 1));
    }
}
