using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>
/// Design §4: every reader rule, each with a passing baseline and a FAILING case. A failure must fail
/// the unit (throw), never drop rows silently.
/// </summary>
public sealed class ReaderValidationTests
{
    private static readonly DateOnly D = Samples.D(2026, 11, 18);
    private static readonly DateOnly I = Samples.D(2026, 11, 17);

    private static MisoDayReader<TRow> Reader<TRow>(IMisoPageSource pages, IMisoRowMapper<TRow> mapper) =>
        new(pages, mapper, Samples.Settings(), NullLogger.Instance);

    private static Task<IReadOnlyList<MisoZonalLoadHourlyRow>> ReadZonal(string envelope) =>
        Reader(FakePageSource.Body(envelope), new MisoZonalLoadMapper()).ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, D), default);

    private static Task<IReadOnlyList<MisoLoadForecastMtlfRow>> ReadMtlf(string envelope) =>
        Reader(FakePageSource.Body(envelope), new MisoLoadForecastMtlfMapper())
            .ReadAsync(Samples.Unit(MisoFeeds.LoadForecastMtlf, D, init: I), default);

    private static Task<IReadOnlyList<MisoFuelMixHourlyRow>> ReadFuel(string envelope) =>
        Reader(FakePageSource.Body(envelope), new MisoFuelMixMapper()).ReadAsync(Samples.Unit(MisoFeeds.FuelMixHourly, D), default);

    private static Task<IReadOnlyList<MisoLoadHourlyRow>> ReadRegion(string envelope) =>
        Reader(FakePageSource.Body(envelope), new MisoLoadMapper()).ReadAsync(Samples.Unit(MisoFeeds.LoadHourly, D), default);

    // ---- baselines -------------------------------------------------------------

    [Fact]
    public async Task Baseline_complete_days_pass_for_every_feed()
    {
        Assert.Equal(24, (await ReadZonal(Samples.Envelope(Samples.ZonalDay(D)))).Count);
        Assert.Equal(24, (await ReadRegion(Samples.Envelope(Samples.RegionDay(D)))).Count);
        Assert.Equal(72, (await ReadFuel(Samples.Envelope(Samples.FuelDay(D)))).Count);
        Assert.Equal(240, (await ReadMtlf(Samples.Envelope(Samples.MtlfDay(D, I)))).Count);
    }

    // ---- rule 1: count reconciliation + page echo --------------------------------

    [Fact]
    public async Task Rule1_rows_read_must_equal_totalElements()
    {
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(Samples.ZonalDay(D), totalElements: 145)));
        Assert.Contains("totalElements 145", ex.Message);
    }

    [Fact]
    public async Task Rule1_page_number_must_echo_the_request()
    {
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(Samples.ZonalDay(D), pageNumber: 2)));
        Assert.Contains("envelope says page 2", ex.Message);
    }

    [Fact]
    public async Task Rule1_rows_across_pages_are_reconciled_against_page_1_total()
    {
        var rows = Samples.ZonalDay(D);
        var pages = new FakePageSource(p => MisoApiClient.ParseEnvelope(p == 1
            ? Samples.Envelope(rows.Take(100), totalElements: 144, pageNumber: 1, lastPage: false, totalPages: 2)
            : Samples.Envelope(rows.Skip(100), totalElements: 144, pageNumber: 2, lastPage: true, totalPages: 2), "t"));
        var result = await Reader(pages, new MisoZonalLoadMapper()).ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, D), default);
        Assert.Equal(24, result.Count);
        Assert.Equal(new[] { 1, 2 }, pages.Pages);
    }

    // ---- rule 2: hourly, one hour, date echo -------------------------------------

    [Fact]
    public async Task Rule2_resolution_must_be_hourly()
    {
        var rows = Samples.ZonalDay(D);
        rows[5]["timeInterval"]!["resolution"] = "daily";
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(rows)));
        Assert.Contains("resolution", ex.Message);
    }

    [Fact]
    public async Task Rule2_interval_must_be_exactly_one_hour()
    {
        var rows = Samples.ZonalDay(D);
        rows[0]["timeInterval"]!["end"] = Samples.Iso(D.ToDateTime(new TimeOnly(2, 0)));
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(rows)));
        Assert.Contains("not exactly one hour", ex.Message);
    }

    [Fact]
    public async Task Rule2_start_must_be_on_the_requested_date()
    {
        var other = Samples.ZonalDay(D.AddDays(-1));
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(other)));
        Assert.Contains("date echo", ex.Message);
    }

    [Fact]
    public async Task Rule2_start_with_an_offset_is_refused()
    {
        var rows = Samples.ZonalDay(D);
        rows[0]["timeInterval"]!["start"] = "2026-11-18T00:00:00-05:00";
        await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(rows)));
    }

    // ---- rule 3: MTLF init echo --------------------------------------------------

    [Fact]
    public async Task Rule3_mtlf_init_must_echo_the_request()
    {
        // The vendor silently defaults init to date−1; a request for init=11-16 answered with 11-17 must fail.
        var rows = Samples.MtlfDay(D, I);
        var ex = await Assert.ThrowsAsync<MisoDataException>(() =>
            Reader(FakePageSource.Body(Samples.Envelope(rows)), new MisoLoadForecastMtlfMapper())
                .ReadAsync(Samples.Unit(MisoFeeds.LoadForecastMtlf, D, init: I.AddDays(-1)), default));
        Assert.Contains("init echo", ex.Message);
    }

    [Fact]
    public async Task Rule3_mtlf_init_missing_on_one_row_fails()
    {
        var rows = Samples.MtlfDay(D, I);
        rows[17].Remove("init");
        await Assert.ThrowsAsync<MisoDataException>(() => ReadMtlf(Samples.Envelope(rows)));
    }

    // ---- rule 4: known member, case-insensitive ------------------------------------

    [Fact]
    public async Task Rule4_unknown_lrz_fails()
    {
        var rows = Samples.ZonalDay(D);
        rows[3]["localResourceZone"] = "LRZ99";
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(rows)));
        Assert.Contains("unknown localResourceZone 'LRZ99'", ex.Message);
    }

    [Fact]
    public async Task Rule4_unknown_region_fails()
    {
        var rows = Samples.RegionDay(D);
        rows[0]["region"] = "MISO";
        await Assert.ThrowsAsync<MisoDataException>(() => ReadRegion(Samples.Envelope(rows)));
    }

    [Fact]
    public async Task Rule4_unknown_mtlf_zone_fails()
    {
        var rows = Samples.MtlfDay(D, I);
        rows[0]["localResourceZone"] = "Z11";
        await Assert.ThrowsAsync<MisoDataException>(() => ReadMtlf(Samples.Envelope(rows)));
    }

    [Fact]
    public async Task Rule4_members_match_case_insensitively_title_and_upper()
    {
        // Actual load sends "Central", fuel sends "CENTRAL" (design §4).
        var title = await ReadRegion(Samples.Envelope(Samples.RegionDay(D, s => s[..1] + s[1..].ToLowerInvariant())));
        var upper = await ReadRegion(Samples.Envelope(Samples.RegionDay(D, s => s.ToUpperInvariant())));
        Assert.Equal(24, title.Count);
        Assert.Equal(title.Select(r => r.Miso), upper.Select(r => r.Miso));
    }

    [Fact]
    public async Task Rule4_fuel_region_is_stored_upper_and_mtlf_zone_as_sent()
    {
        var fuelRows = Samples.FuelDay(D);
        foreach (var r in fuelRows) r["region"] = ((string)r["region"]!).ToLowerInvariant();
        var fuel = await ReadFuel(Samples.Envelope(fuelRows));
        Assert.All(fuel, r => Assert.Contains(r.Region, MisoFeeds.Regions));

        var mtlfRows = Samples.MtlfDay(D, I);
        foreach (var r in mtlfRows) r["region"] = ((string)r["region"]!).ToLowerInvariant();
        var mtlf = await ReadMtlf(Samples.Envelope(mtlfRows));
        Assert.All(mtlf, r => Assert.Contains(r.Region, MisoFeeds.Regions));
        Assert.Contains(mtlf, r => r.LocalResourceZone == "Z10");
        Assert.All(mtlf, r => Assert.Equal(I, r.InitDate));
    }

    [Theory]
    [InlineData("Z2", "NORTH")]     // the API doc's grouping; the live data puts Z2 in CENTRAL
    [InlineData("Z3", "CENTRAL")]   // the API doc's grouping; the live data puts Z3 in NORTH
    [InlineData("Z8", "CENTRAL")]
    [InlineData("Z1", "SOUTH")]
    public async Task Rule4_mtlf_region_must_match_its_zone(string zone, string wrongRegion)
    {
        var rows = Samples.MtlfDay(D, I);
        rows.First(r => (string)r["localResourceZone"]! == zone)["region"] = wrongRegion;
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadMtlf(Samples.Envelope(rows)));
        Assert.Contains($"does not match zone {zone}", ex.Message);
    }

    [Fact]
    public async Task Rule4_mtlf_zone_is_stored_canonical_and_region_upper()
    {
        var rows = Samples.MtlfDay(D, I);
        foreach (var r in rows)
        {
            r["localResourceZone"] = ((string)r["localResourceZone"]!).ToLowerInvariant();
            r["region"] = " " + ((string)r["region"]!).ToLowerInvariant();
        }
        var result = await ReadMtlf(Samples.Envelope(rows));
        Assert.Equal(MisoFeeds.MtlfZones.OrderBy(z => z), result.Select(r => r.LocalResourceZone).Distinct().OrderBy(z => z));
        Assert.All(result, r => Assert.Equal(MisoFeeds.MtlfZoneRegion[r.LocalResourceZone], r.Region));
    }

    [Fact]
    public void Zone_region_map_covers_exactly_the_ten_zones_and_three_regions()
    {
        Assert.Equal(MisoFeeds.MtlfZones.OrderBy(z => z), MisoFeeds.MtlfZoneRegion.Keys.OrderBy(z => z));
        Assert.Equal(MisoFeeds.Regions.OrderBy(r => r), MisoFeeds.MtlfZoneRegion.Values.Distinct().OrderBy(r => r));
    }

    [Fact]
    public async Task Rule1_totals_on_a_later_page_must_equal_page_1()
    {
        var rows = Samples.ZonalDay(D);
        foreach (var (total, pages) in new[] { (145, 2), (144, 3) })
        {
            var src = new FakePageSource(p => MisoApiClient.ParseEnvelope(p == 1
                ? Samples.Envelope(rows.Take(100), totalElements: 144, pageNumber: 1, lastPage: false, totalPages: 2)
                : Samples.Envelope(rows.Skip(100), totalElements: total, pageNumber: 2, lastPage: true, totalPages: pages), "t"));
            var ex = await Assert.ThrowsAsync<MisoDataException>(() =>
                Reader(src, new MisoZonalLoadMapper()).ReadAsync(Samples.Unit(MisoFeeds.ZonalLoadHourly, D), default));
            Assert.Contains("but page 1 reported 144 / 2", ex.Message);
        }
    }

    [Fact]
    public async Task Rule4_mtlf_unknown_region_fails()
    {
        var rows = Samples.MtlfDay(D, I);
        rows[0]["region"] = "EAST";
        await Assert.ThrowsAsync<MisoDataException>(() => ReadMtlf(Samples.Envelope(rows)));
    }

    // ---- rule 5: fuel keys -------------------------------------------------------

    [Fact]
    public async Task Rule5_unknown_fuel_key_fails()
    {
        var rows = Samples.FuelDay(D);
        ((JsonObject)rows[10]["fuelTypes"]!)["imports"] = 12.5m;
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadFuel(Samples.Envelope(rows)));
        Assert.Contains("unknown fuel type 'imports'", ex.Message);
    }

    [Fact]
    public async Task Rule5_fuel_keys_are_case_insensitive_and_a_missing_known_key_is_NULL()
    {
        var rows = Samples.FuelDay(D);
        var fuel = (JsonObject)rows[0]["fuelTypes"]!;
        fuel.Remove("solar");
        var coal = fuel["coal"]!.DeepClone();
        fuel.Remove("coal");
        fuel["COAL"] = coal;

        var result = await ReadFuel(Samples.Envelope(rows));
        var first = result.Single(r => r.IntervalStartLocal == D.ToDateTime(TimeOnly.MinValue) && r.Region == "NORTH");   // rows[0]
        Assert.Equal(6m, result.Single(r => r.IntervalStartLocal == D.ToDateTime(TimeOnly.MinValue) && r.Region == "CENTRAL").Solar);
        Assert.Null(first.Solar);
        Assert.Equal(1.5m, first.Coal);
        Assert.Equal(0.1m, first.Storage);
    }

    [Fact]
    public async Task Rule5_fuel_key_repeated_under_another_casing_fails()
    {
        var rows = Samples.FuelDay(D);
        ((JsonObject)rows[0]["fuelTypes"]!)["Gas"] = 9m;
        await Assert.ThrowsAsync<MisoDataException>(() => ReadFuel(Samples.Envelope(rows)));
    }

    [Fact]
    public async Task Rule5_fuelTypes_missing_fails()
    {
        var rows = Samples.FuelDay(D);
        rows[0].Remove("fuelTypes");
        await Assert.ThrowsAsync<MisoDataException>(() => ReadFuel(Samples.Envelope(rows)));
    }

    // ---- rule 6: duplicates ------------------------------------------------------

    [Fact]
    public async Task Rule6_duplicate_interval_member_fails_even_across_casing()
    {
        var rows = Samples.RegionDay(D);
        var dup = (JsonObject)rows[0].DeepClone();
        dup["region"] = ((string)dup["region"]!).ToUpperInvariant();
        rows.Add(dup);
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadRegion(Samples.Envelope(rows)));
        Assert.Contains("duplicate", ex.Message);
    }

    // ---- rule 7: completeness ----------------------------------------------------

    [Fact]
    public async Task Rule7_23_intervals_fail()
    {
        var rows = Samples.ZonalDay(D).Where(r => (string)r["timeInterval"]!["start"]! != "2026-11-18T23:00:00").ToList();
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(rows)));
        Assert.Contains("23 distinct hourly interval(s), expected 24", ex.Message);
    }

    [Fact]
    public async Task Rule7_an_interval_missing_one_member_fails()
    {
        var rows = Samples.MtlfDay(D, I);
        rows.RemoveAt(15);
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => ReadMtlf(Samples.Envelope(rows)));
        Assert.Contains("incomplete day", ex.Message);
    }

    [Fact]
    public async Task Rule7_an_empty_200_fails()
    {
        await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(Array.Empty<JsonObject>())));
    }

    // ---- shape ---------------------------------------------------------------------

    [Fact]
    public async Task A_missing_load_property_fails_but_an_explicit_null_is_NULL_and_nulls_the_sum()
    {
        var rows = Samples.ZonalDay(D);
        rows[0]["load"] = null;
        var result = await ReadZonal(Samples.Envelope(rows));
        Assert.Null(result[0].Lrz1);
        Assert.Null(result[0].Miso);
        Assert.NotNull(result[1].Miso);

        var missing = Samples.ZonalDay(D);
        missing[0].Remove("load");
        await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(missing)));

        var text = Samples.ZonalDay(D);
        text[0]["load"] = "10519.1";
        await Assert.ThrowsAsync<MisoDataException>(() => ReadZonal(Samples.Envelope(text)));
    }

    [Theory]
    [InlineData("{\"page\":{\"pageNumber\":1,\"pageSize\":1000,\"totalElements\":0,\"totalPages\":1,\"lastPage\":true}}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[],\"page\":{\"pageNumber\":1,\"pageSize\":1000,\"totalElements\":0,\"totalPages\":1}}")]
    [InlineData("not json")]
    public void Envelope_without_data_or_page_is_a_contract_failure(string body)
    {
        Assert.Throws<MisoContractException>(() => MisoApiClient.ParseEnvelope(body, "t"));
    }
}
