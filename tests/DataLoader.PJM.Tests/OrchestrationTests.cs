using System.Net;
using System.Text.Json;
using DataLoader.Core.Abstractions;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Design §12.2 tests 26 (duplicate key → nothing written), 33 (429 trip across units), 46 and 47
/// (orchestration), through the platform pipeline loop and the real module wiring.
/// </summary>
public sealed class OrchestrationTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);

    private static LoaderRunContext Ctx() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = Samples.Started,
        CancellationToken = CancellationToken.None,
    };

    // ---- 26 -----------------------------------------------------------------------

    private static async Task<(LoaderRunResult Result, FakeSink<TRow> Sink, FakeLoadLog Log)> RunOneUnit<TRow>(
        PjmFeed feed, IPjmRowMapper<TRow> mapper, IReadOnlyList<JsonElement> items,
        Func<PjmDayReader<TRow>, PjmRunGuard, ISourceReader<PjmDayWorkUnit, TRow>> source)
        where TRow : IPjmRow
    {
        var settings = Samples.Settings();
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        var reader = new PjmDayReader<TRow>(FakePageSource.Paged(items, 50000), mapper, settings, ClientFactory.Null);
        var sink = new FakeSink<TRow>();
        var log = new FakeLoadLog();
        var unit = Samples.Unit(feed, Day, 3);
        var pipeline = new PjmPipeline<TRow>(feed.Name, new FixedUnitProvider(unit), source(reader, guard), sink, log, settings, ClientFactory.Null);
        var result = await pipeline.ExecuteAsync(Ctx());
        return (result, sink, log);
    }

    private static DateTime H(int h) => Day.ToDateTime(new TimeOnly(h, 0));

    public static IEnumerable<object[]> FuelDuplicates() => new[]
    {
        new object[] { "exact", Samples.Fuel(H(3), "Gas"), Samples.Fuel(H(3), "Gas") },
        new object[] { "case", Samples.Fuel(H(3), "Gas"), Samples.Fuel(H(3), "gas") },
        new object[] { "trailing space", Samples.Fuel(H(3), "Coal "), Samples.Fuel(H(3), "Coal") },
        new object[] { "Z suffix = same second", Samples.Fuel(H(3)), Samples.Fuel(H(3), fields: new() { ["datetime_beginning_utc"] = Samples.Iso(H(3)) + "Z" }) },
        new object[] { ".000 = same second", Samples.Fuel(H(3)), Samples.Fuel(H(3), fields: new() { ["datetime_beginning_utc"] = Samples.Iso(H(3)) + ".000" }) },
    };

    [Theory]
    [MemberData(nameof(FuelDuplicates))]
    public async Task T26_gen_by_fuel_duplicate_key_fails_the_unit_and_the_sink_is_never_called(string label, JsonElement a, JsonElement b)
    {
        var items = new List<JsonElement> { Samples.Fuel(H(1)), a, Samples.Fuel(H(2)), b };
        var (result, sink, log) = await RunOneUnit(PjmFeeds.GenByFuel(Samples.Settings()), new PjmFuelMixRowMapper(), items,
            (r, g) => new PjmFuelMixSourceReader(r, g));

        Assert.False(result.Success, label);
        Assert.Equal(1, result.WorkUnitsFailed);
        Assert.Empty(sink.Writes);
        var failure = Assert.Single(log.Failures);
        Assert.Contains("duplicate key", failure.Error);
        Assert.Empty(log.Successes);
    }

    [Theory]
    [InlineData("RECO ", "RECO")]
    [InlineData("reco", "RECO")]
    [InlineData("RECO", "RECO")]
    public async Task T26_inst_load_duplicate_key_by_case_or_trailing_space_fails_and_nothing_is_written(string a, string b)
    {
        var items = new List<JsonElement> { Samples.Load(H(1), a), Samples.Load(H(1), b) };
        var (result, sink, log) = await RunOneUnit(PjmFeeds.InstLoad(Samples.Settings()), new PjmLoadRowMapper(), items,
            (r, g) => new PjmLoadSourceReader(r, g));

        Assert.False(result.Success);
        Assert.Empty(sink.Writes);
        Assert.Contains("duplicate key", Assert.Single(log.Failures).Error);
    }

    [Fact]
    public async Task T26_Leading_space_is_a_distinct_key_as_in_SQL()
    {
        var items = new List<JsonElement> { Samples.Load(H(1), " RECO"), Samples.Load(H(1), "RECO") };
        var (result, sink, _) = await RunOneUnit(PjmFeeds.InstLoad(Samples.Settings()), new PjmLoadRowMapper(), items,
            (r, g) => new PjmLoadSourceReader(r, g));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, Assert.Single(sink.Writes).Count);
    }

    [Fact]
    public async Task T26_Duplicate_across_pages_is_still_caught()
    {
        var settings = Samples.Settings(s => s.PageSize = 2);
        var items = new List<JsonElement> { Samples.Fuel(H(1)), Samples.Fuel(H(2)), Samples.Fuel(H(1), "GAS") };
        var reader = new PjmDayReader<PjmFuelMixRow>(FakePageSource.Paged(items, 2), new PjmFuelMixRowMapper(), settings, ClientFactory.Null);
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(ClientFactory.GenUnit(Day, 3), CancellationToken.None));
        Assert.Contains("duplicate key", ex.Message);
    }

    [Fact]
    public async Task T26_Clean_unit_is_written_as_one_batch()
    {
        var (result, sink, log) = await RunOneUnit(PjmFeeds.GenByFuel(Samples.Settings()), new PjmFuelMixRowMapper(), Samples.FuelDay(Day),
            (r, g) => new PjmFuelMixSourceReader(r, g));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(24, Assert.Single(sink.Writes).Count);
        Assert.Single(log.Successes);
    }

    // ---- 33 (module level) --------------------------------------------------------

    [Fact]
    public async Task T33_429_exhausted_on_the_first_unit_trips_and_every_later_unit_sends_zero_requests()
    {
        var data = StubHttpMessageHandler.Always((HttpStatusCode)429);
        var settings = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k429"));
        using var h = PjmHarness.Create(data, settings);

        var result = await h.RunAsync();

        Assert.False(result.Success);
        Assert.Equal(4, data.Count);                          // RetryCount 3 + 1, all on the first unit
        Assert.True(h.Guard.IsTripped);
        Assert.Equal(29 + 31, h.LoadLog.Failures.Count);      // every unit recorded failed, re-attempted next run
        Assert.Contains("run guard tripped", result.ErrorMessage);
        Assert.Empty(h.LoadSink.Writes);
        Assert.Empty(h.FuelSink.Writes);
    }

    // ---- 46 -----------------------------------------------------------------------

    [Fact]
    public async Task T46_inst_load_runs_before_gen_by_fuel_whatever_the_EnabledFeeds_order()
    {
        var data = new StubHttpMessageHandler((r, _) => Samples.OneRowFor(r));
        var settings = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k46"));
        using var h = PjmHarness.Create(data, settings, configure: s => s.EnabledFeeds = new[] { "gen_by_fuel", "inst_load" });

        var result = await h.RunAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var feeds = data.Requests.OrderBy(r => r.Index).Select(r => Samples.Parse(r.Uri).Feed).ToList();
        Assert.Equal(29 + 31, feeds.Count);
        Assert.All(feeds.Take(29), f => Assert.Equal("inst_load", f));
        Assert.All(feeds.Skip(29), f => Assert.Equal("gen_by_fuel", f));
        Assert.All(data.Requests.Where(r => Samples.Parse(r.Uri).Feed == "inst_load"),
            r => Assert.Contains("isActiveMetadata=true", r.Uri.Query));
        Assert.All(data.Requests.Where(r => Samples.Parse(r.Uri).Feed == "gen_by_fuel"),
            r => Assert.DoesNotContain("isActiveMetadata", r.Uri.Query));
        Assert.Equal(1, settings.Count);
        Assert.Equal(29, h.LoadSink.Writes.Count);
        Assert.Equal(31, h.FuelSink.Writes.Count);
    }

    [Fact]
    public async Task T46_Only_the_enabled_feed_runs()
    {
        var data = new StubHttpMessageHandler((r, _) => Samples.OneRowFor(r));
        var settings = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k46b"));
        using var h = PjmHarness.Create(data, settings, config: new() { ["Loaders:PJM:EnabledFeeds:0"] = "GEN_BY_FUEL" });   // through the binder, as appsettings would

        var result = await h.RunAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.All(data.Requests, r => Assert.Equal("gen_by_fuel", Samples.Parse(r.Uri).Feed));
        Assert.Equal(31, data.Count);
    }

    [Fact]
    public async Task T46_No_feeds_enabled_succeeds_with_no_settings_json_request()
    {
        // An EMPTY list means "unset" (EffectiveEnabledFeeds falls back to both feeds, because the binder
        // cannot express an empty array), so "nothing enabled" is a list naming no known feed.
        var data = new StubHttpMessageHandler((r, _) => Samples.OneRowFor(r));
        var settings = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k46c"));
        using var h = PjmHarness.Create(data, settings, configure: s => s.EnabledFeeds = new[] { "none" });

        var result = await h.RunAsync();

        Assert.True(result.Success);
        Assert.Equal(0, settings.Count);
        Assert.Equal(0, data.Count);
        Assert.Equal(0, h.Store.Calls);
    }

    // ---- 47 -----------------------------------------------------------------------

    [Theory]
    [InlineData("store")]
    [InlineData("settings500")]
    public async Task T47_Key_acquisition_failure_fails_the_run_with_zero_units_attempted(string mode)
    {
        var data = new StubHttpMessageHandler((r, _) => Samples.OneRowFor(r));
        var settings = mode == "settings500"
            ? StubHttpMessageHandler.Always(HttpStatusCode.InternalServerError)
            : StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k47"));
        var store = mode == "store" ? FakeParamStore.Throwing() : FakeParamStore.Returning(null);
        using var h = PjmHarness.Create(data, settings, store);

        var result = await h.RunAsync();

        Assert.False(result.Success);
        Assert.Equal(0, result.WorkUnitsTotal);
        Assert.Equal(0, data.Count);
        Assert.Empty(h.LoadLog.BegunKeys);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        if (mode == "store") Assert.Equal(0, settings.Count);
    }
}
