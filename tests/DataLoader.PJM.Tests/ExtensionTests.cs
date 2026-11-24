using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Design §14.12 tests 52-71: the six-feed extension (§14, with the §14.14 decisions: D1 = B,
/// D2 = 30, D3 hourly, D4 6-hour blocks, D5 = 10/14). Offline: scripted page sources, stubbed
/// HTTP, in-memory sinks and load log.
/// </summary>
public sealed class ExtensionTests
{
    private static readonly PjmSettings S = new();
    private static readonly DateOnly Today = new(2026, 11, 23);
    private static readonly DateOnly D18 = new(2026, 11, 18);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);

    private static readonly string[] SpecOrder =
    {
        "load_frcstd_7_day", "inst_load", "very_short_load_frcst", "gen_by_fuel",
        "hrl_load_metered", "hrl_load_prelim", "load_frcstd_hist", "hrl_load_estimated",
    };

    private static DateTime At(DateOnly d, int h, int m = 0, int s = 0) => d.ToDateTime(new TimeOnly(h, m, s));
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static PjmFeed Feed(string name) => PjmFeeds.All(S).Single(f => f.Name == name);

    private static List<PjmDayWorkUnit> Build(string feed, DateTime startedUtc, PjmSettings? s = null) =>
        PjmWorkUnitProvider.Build(PjmFeeds.All(s ?? S).Single(f => f.Name == feed), s ?? S, startedUtc);

    private static PjmApiClient Client(PjmSettings? s = null) =>
        new(new HttpClient(), s ?? Samples.Settings(), new FakeKeyProvider("k"), new PjmRunGuard(3, ClientFactory.Null), ClientFactory.Null);

    private static JsonElement With(JsonElement e, string field, object? value)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.GetRawText())!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        if (ReferenceEquals(value, Samples.Remove)) d.Remove(field);
        else d[field] = value;
        return Samples.Element(d);
    }

    // Generic dispatch: read one unit of any feed through the REAL mapper + PjmDayReader.
    private static async Task<(IReadOnlyList<IPjmRow>? Rows, Exception? Error, CapturingLoggerFactory Logs)> Read(
        string feed, IReadOnlyList<JsonElement> items, PjmDayWorkUnit unit, PjmSettings? s = null, TimeProvider? time = null)
    {
        var logs = new CapturingLoggerFactory();
        // Deterministic: the response is "read" at the run start unless a test says otherwise.
        time ??= FeedSamples.Clock(unit.RunStartedUtc ?? Samples.Started);
        var src = FeedSamples.Page(items);
        try
        {
            IReadOnlyList<IPjmRow> rows = feed switch
            {
                PjmFeeds.InstLoadName => (await FeedSamples.Reader(src, new PjmLoadRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.GenByFuelName => (await FeedSamples.Reader(src, new PjmFuelMixRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.LoadMeteredName => (await FeedSamples.Reader(src, new PjmLoadMeteredRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.LoadPrelimName => (await FeedSamples.Reader(src, new PjmLoadPrelimRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.LoadEstimatedName => (await FeedSamples.Reader(src, new PjmLoadEstimatedRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.LoadForecastHistName => (await FeedSamples.Reader(src, new PjmLoadForecastHistRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.LoadForecastVeryShortName => (await FeedSamples.Reader(src, new PjmLoadForecastVeryShortRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                PjmFeeds.LoadForecast7DayName => (await FeedSamples.Reader(src, new PjmLoadForecast7DayRowMapper(), logs, s, time).ReadDayAsync(unit, default)).Cast<IPjmRow>().ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(feed)),
            };
            return (rows, null, logs);
        }
        catch (Exception ex)
        {
            return (null, ex, logs);
        }
    }

    /// <summary>A valid default item and a matching unit per new feed (D18 = 2026-11-18, age 5).</summary>
    private static (JsonElement Item, PjmDayWorkUnit Unit) Default(string feed) => feed switch
    {
        PjmFeeds.LoadMeteredName => (FeedSamples.Metered(At(D18, 6)), FeedSamples.Unit(Feed(feed), D18, 5)),
        PjmFeeds.LoadPrelimName => (FeedSamples.Prelim(At(D18, 6)), FeedSamples.Unit(Feed(feed), D18, 5)),
        PjmFeeds.LoadEstimatedName => (FeedSamples.Estimated(At(D18, 6)), FeedSamples.Unit(Feed(feed), D18, 5)),
        PjmFeeds.LoadForecastHistName => (FeedSamples.Hist(At(D18, 3, 45), At(D18, 5)), FeedSamples.Unit(Feed(feed), D18, 5)),
        PjmFeeds.LoadForecastVeryShortName => (FeedSamples.VeryShort(At(D18, 6), At(D18, 6, 30)),
            FeedSamples.Unit(Feed(feed), D18, 5, PjmZone.Settled, blockStartHour: 6)),
        PjmFeeds.LoadForecast7DayName => (FeedSamples.SevenDay(At(Today, 22, 47, 12), At(Today.AddDays(1), 0)),
            FeedSamples.Snapshot(Feed(feed), Utc(2026, 11, 23, 23, 0))),
        _ => throw new ArgumentOutOfRangeException(nameof(feed)),
    };

    // =========================================================================
    // 52: the two shipped descriptors are byte-identical under the new fields
    // =========================================================================

    [Fact]
    public void T52_Shipped_descriptors_carry_the_section_14_2_mapping()
    {
        var inst = PjmFeeds.InstLoad(S);
        var gen = PjmFeeds.GenByFuel(S);
        foreach (var f in new[] { inst, gen })
        {
            Assert.Equal(PjmFeedKind.Window, f.Kind);
            Assert.Equal("datetime_beginning_utc", f.FilterField);
            Assert.Equal("datetime_beginning_utc", f.SortField);
            Assert.Equal(24, f.UnitHours);
            Assert.True(f.IncludeToday);
            Assert.Equal(0, f.EmptyOkThroughAge);
            Assert.Null(f.MinRecentHours);
            Assert.Null(f.MinSettledAfterDays);
            Assert.Null(f.SettledAfterDaysCeiling);
            Assert.Empty(f.EndOffsets);
        }

        Assert.Equal(new[] { new PjmStampRule("datetime_beginning_utc", "datetime_beginning_ept", PjmStampMode.Slot, 5) }, inst.Stamps);
        Assert.Equal(new[] { new PjmStampRule("datetime_beginning_utc", "datetime_beginning_ept", PjmStampMode.Slot, 60) }, gen.Stamps);
        Assert.Equal(new[] { "datetime_beginning_utc", "area" }, inst.KeyFields);
        Assert.Equal(new[] { "datetime_beginning_utc", "fuel_type" }, gen.KeyFields);
        Assert.Equal(new PjmCompleteness(PjmCompletenessMode.DistinctSlotsPerMember, "datetime_beginning_utc", 288, 1), inst.Completeness);
        Assert.Equal(new PjmCompleteness(PjmCompletenessMode.DistinctSlotsTotal, "datetime_beginning_utc", 24, 1), gen.Completeness);
    }

    [Fact]
    public void T52_Shipped_feeds_produce_byte_identical_filters_urls_labels_and_keys()
    {
        var day = new DateOnly(2026, 11, 20);
        var gen = Samples.Unit(PjmFeeds.GenByFuel(S), day, 3);
        var inst = Samples.Unit(PjmFeeds.InstLoad(S), day, 3);
        var client = Client();

        Assert.Equal("11/20/2026 00:00to11/20/2026 23:59", gen.FilterValue);
        Assert.Equal(PjmDayWorkUnit.FormatFilter(day), gen.FilterValue);
        Assert.Equal("2026-11-20", gen.Label);
        Assert.Equal("PJM gen_by_fuel 2026-11-20 (hot)", gen.DisplayName);
        Assert.Equal(
            "/gen_by_fuel?rowCount=50000&startRow=1&sort=datetime_beginning_utc&order=Asc&datetime_beginning_utc=11%2F20%2F2026%2000%3A00to11%2F20%2F2026%2023%3A59",
            client.BuildPathAndQuery(gen, 1));
        Assert.Equal(
            "/inst_load?rowCount=50000&startRow=50001&sort=datetime_beginning_utc&order=Asc&datetime_beginning_utc=11%2F20%2F2026%2000%3A00to11%2F20%2F2026%2023%3A59&isActiveMetadata=true",
            client.BuildPathAndQuery(inst, 50001));

        // §3.2 key shapes for all three zones (no block suffix for a 24-hour unit).
        var units = Build("gen_by_fuel", Samples.Started);
        Assert.Equal("pjm:gen_by_fuel:20261123;run=2026112314", units.Single(u => u.Day == Today).Key);
        Assert.Equal("pjm:gen_by_fuel:20261120;run=20261123", units.Single(u => u.Day == day).Key);
        Assert.Equal("pjm:gen_by_fuel:20261101", units.Single(u => u.Day == Nov1).Key);
        Assert.Equal("pjm:inst_load:20261101", Build("inst_load", Samples.Started).Single(u => u.Day == Nov1).Key);
    }

    [Fact]
    public async Task T52_Shipped_feed_messages_are_unchanged()
    {
        // Echo, empty-past-day and duplicate texts that the earlier tests match on.
        var day = new DateOnly(2026, 11, 20);
        var echo = await Read("gen_by_fuel", new[] { Samples.Fuel(At(day.AddDays(1), 0)) }, Samples.Unit(PjmFeeds.GenByFuel(S), day, 3));
        Assert.Contains("lies outside the requested UTC day", echo.Error!.Message);

        var empty = await Read("gen_by_fuel", Array.Empty<JsonElement>(), Samples.Unit(PjmFeeds.GenByFuel(S), day, 3));
        Assert.Contains("0 rows for a past day; history is continuous since 2016", empty.Error!.Message);

        var dup = await Read("inst_load", new[] { Samples.Load(At(day, 1)), Samples.Load(At(day, 1)) }, Samples.Unit(PjmFeeds.InstLoad(S), day, 3));
        Assert.Contains("duplicate key (datetime_beginning_utc 2026-11-20T01:00:00, area 'PJM RTO')", dup.Error!.Message);
    }

    // =========================================================================
    // 53: execution order
    // =========================================================================

    private static JsonElement AppSettingsPjm() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.AppSettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            .RootElement.GetProperty("Loaders").GetProperty("PJM").Clone();

    private static PjmSettings Shipped()
    {
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(File.ReadAllBytes(RepoPaths.AppSettings))).Build();
        var s = new PjmSettings();
        config.GetSection("Loaders:PJM").Bind(s);
        return s;
    }

    [Fact]
    public void T53_ExecutionOrder_DefaultEnabledFeeds_and_shipped_EnabledFeeds_equal_the_section_14_1_list()
    {
        Assert.Equal(SpecOrder, PjmFeeds.ExecutionOrder);
        Assert.Equal(SpecOrder, PjmSettings.DefaultEnabledFeeds);
        Assert.Equal(SpecOrder, new PjmSettings().EffectiveEnabledFeeds);
        Assert.Equal(SpecOrder, AppSettingsPjm().GetProperty("EnabledFeeds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(SpecOrder, PjmFeeds.All(S).Select(f => f.Name));
    }

    [Fact]
    public async Task T53_All_eight_feeds_run_in_section_14_1_order_whatever_the_configured_order()
    {
        var reversed = SpecOrder.Reverse().ToArray();
        var config = new Dictionary<string, string?> { ["Loaders:PJM:ConsecutiveFailureTripCount"] = "0" };
        for (var i = 0; i < reversed.Length; i++) config[$"Loaders:PJM:EnabledFeeds:{i}"] = reversed[i];

        var data = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{\"items\":[],\"totalRows\":0}"));
        using var h = AllFeedsHarness.Create(data, config);

        await h.RunAsync(Samples.Started);

        var feedsInOrder = data.Requests.OrderBy(r => r.Index)
            .Select(r => r.Uri.AbsolutePath.TrimEnd('/').Split('/').Last())
            .Distinct().ToList();
        Assert.Equal(SpecOrder, feedsInOrder);

        // And the first-run request budget through the real module (§14.5): 29+115+1+31+30+30+30+30.
        Assert.Equal(296, data.Count);
    }

    // =========================================================================
    // 54: unit counts and zones at 14:20 UTC, against an independent oracle
    // =========================================================================

    /// <summary>The §14.3 pseudo-code with the §14.14 per-feed numbers typed in from the spec, not read from the descriptors.</summary>
    private static List<(string Key, PjmZone Zone)> Oracle(string feed, DateTime now)
    {
        var (depth, settle, recent, unitHours, includeToday) = feed switch
        {
            "inst_load" => (28, 10, 2, 24, true),
            "very_short_load_frcst" => (28, 1, 2, 6, true),
            "gen_by_fuel" => (30, 10, 2, 24, true),
            "hrl_load_metered" => (30, 30, 12, 24, false),
            "hrl_load_prelim" => (30, 10, 12, 24, false),
            "load_frcstd_hist" => (30, 2, 7, 24, false),
            "hrl_load_estimated" => (30, 14, 2, 24, false),
            _ => throw new ArgumentOutOfRangeException(nameof(feed)),
        };
        var today = DateOnly.FromDateTime(now);
        var list = new List<(string, PjmZone)>();
        for (var age = includeToday ? 0 : 1; age <= depth; age++)
        {
            var d = today.AddDays(-age);
            for (var s = 0; s < 24; s += unitHours)
            {
                var start = d.ToDateTime(new TimeOnly(s, 0));
                if (start > now) continue;
                var end = start.AddHours(unitHours);
                var b = unitHours == 24 ? $"pjm:{feed}:{d:yyyyMMdd}" : $"pjm:{feed}:{d:yyyyMMdd}T{s:00}";
                if (now < end.AddHours(recent)) list.Add(($"{b};run={now:yyyyMMddHH}", PjmZone.Recent));
                else if (d > today.AddDays(-settle)) list.Add(($"{b};run={today:yyyyMMdd}", PjmZone.Hot));
                else list.Add((b, PjmZone.Settled));
            }
        }
        return list;
    }

    [Theory]
    [InlineData("inst_load", 29)]
    [InlineData("very_short_load_frcst", 115)]
    [InlineData("load_frcstd_7_day", 1)]
    [InlineData("gen_by_fuel", 31)]
    [InlineData("hrl_load_metered", 30)]
    [InlineData("hrl_load_prelim", 30)]
    [InlineData("load_frcstd_hist", 30)]
    [InlineData("hrl_load_estimated", 30)]
    public void T54_Unit_counts_at_1420_UTC_on_an_empty_LoadLog(string feed, int expected)
    {
        Assert.Equal(expected, Build(feed, Samples.Started).Count);
    }

    public static IEnumerable<object[]> WindowFeedsAtTimes() =>
        from f in SpecOrder.Where(f => f != "load_frcstd_7_day")
        from t in new[] { "2026-11-23T14:20", "2026-11-23T00:20", "2026-11-23T06:59", "2026-11-23T07:00", "2026-11-23T11:59", "2026-11-23T12:00", "2026-11-23T23:55" }
        select new object[] { f, t };

    [Theory]
    [MemberData(nameof(WindowFeedsAtTimes))]
    public void T54_Zones_and_keys_match_the_section_14_3_pseudo_code(string feed, string at)
    {
        var now = DateTime.SpecifyKind(DateTime.ParseExact(at, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var actual = Build(feed, now).Select(u => (u.Key, u.Zone)).ToList();
        var expected = Oracle(feed, now);
        Assert.Equal(expected.OrderBy(x => x.Key, StringComparer.Ordinal), actual.OrderBy(x => x.Key, StringComparer.Ordinal));
        Assert.Equal(actual.Count, actual.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void T54_Zone_split_at_1420_matches_the_spec_budget()
    {
        // very_short: today's 00/06 blocks Hot, 12 Recent; yesterday 18 block settled by now (ended 00:00 + 2h).
        var vs = Build("very_short_load_frcst", Samples.Started);
        Assert.Equal(1, vs.Count(u => u.Zone == PjmZone.Recent));
        Assert.Equal(2, vs.Count(u => u.Zone == PjmZone.Hot));
        Assert.Equal(112, vs.Count(u => u.Zone == PjmZone.Settled));

        var metered = Build("hrl_load_metered", Samples.Started);
        Assert.Equal(0, metered.Count(u => u.Zone == PjmZone.Recent));   // 14:20 ≥ yesterday end + 12 h
        Assert.Equal(29, metered.Count(u => u.Zone == PjmZone.Hot));     // floor 30 → ages 1..29 hot
        Assert.Equal(1, metered.Count(u => u.Zone == PjmZone.Settled));

        var hist = Build("load_frcstd_hist", Samples.Started);
        Assert.Equal(1, hist.Count(u => u.Zone == PjmZone.Hot));         // ceiling 2 → only yesterday hot
        Assert.Equal(29, hist.Count(u => u.Zone == PjmZone.Settled));

        var est = Build("hrl_load_estimated", Samples.Started);
        Assert.Equal(13, est.Count(u => u.Zone == PjmZone.Hot));         // floor 14 → ages 1..13
    }

    // =========================================================================
    // 55: IncludeToday and MinRecentHours
    // =========================================================================

    [Theory]
    [InlineData("hrl_load_metered")]
    [InlineData("hrl_load_prelim")]
    [InlineData("hrl_load_estimated")]
    [InlineData("load_frcstd_hist")]
    public void T55_IncludeToday_false_feeds_never_enumerate_age_0(string feed)
    {
        Assert.False(Feed(feed).IncludeToday);
        foreach (var h in new[] { 0, 1, 7, 12, 14, 23 })
        {
            var units = Build(feed, Utc(2026, 11, 23, h, 20));
            Assert.DoesNotContain(units, u => u.Day == Today);
            Assert.Equal(Today.AddDays(-1), units[0].Day);
        }
    }

    [Theory]
    [InlineData("hrl_load_metered", 11, 59, 12, 0)]
    [InlineData("hrl_load_prelim", 11, 59, 12, 0)]
    [InlineData("load_frcstd_hist", 6, 59, 7, 0)]
    public void T55_MinRecentHours_keeps_yesterday_Recent_until_the_daily_post(string feed, int h1, int m1, int h2, int m2)
    {
        var yesterday = Today.AddDays(-1);
        var before = Build(feed, Utc(2026, 11, 23, h1, m1)).Single(u => u.Day == yesterday);
        var after = Build(feed, Utc(2026, 11, 23, h2, m2)).Single(u => u.Day == yesterday);

        Assert.Equal(PjmZone.Recent, before.Zone);
        Assert.Equal($"pjm:{feed}:20261122;run=20261123{h1:00}", before.Key);
        Assert.Equal(PjmZone.Hot, after.Zone);
        Assert.Equal($"pjm:{feed}:20261122;run=20261123", after.Key);
    }

    [Fact]
    public void T55_Estimated_has_no_recent_floor_so_yesterday_is_Hot_from_0200()
    {
        var yesterday = Today.AddDays(-1);
        Assert.Equal(PjmZone.Recent, Build("hrl_load_estimated", Utc(2026, 11, 23, 1, 59)).Single(u => u.Day == yesterday).Zone);
        Assert.Equal(PjmZone.Hot, Build("hrl_load_estimated", Utc(2026, 11, 23, 2, 0)).Single(u => u.Day == yesterday).Zone);
    }

    // =========================================================================
    // 56: very_short 6-hour blocks
    // =========================================================================

    [Fact]
    public void T56_Four_blocks_per_past_day_and_today_only_up_to_now()
    {
        var units = Build("very_short_load_frcst", Samples.Started);
        Assert.Equal(new[] { 12, 6, 0 }, units.Where(u => u.Day == Today).Select(u => u.BlockStartHour));
        foreach (var g in units.Where(u => u.Day < Today).GroupBy(u => u.Day))
            Assert.Equal(new[] { 18, 12, 6, 0 }, g.Select(u => u.BlockStartHour));   // newest first

        Assert.Equal(new[] { 0 }, Build("very_short_load_frcst", Utc(2026, 11, 23, 0, 0)).Where(u => u.Day == Today).Select(u => u.BlockStartHour));
        Assert.Equal(new[] { 18, 12, 6, 0 }, Build("very_short_load_frcst", Utc(2026, 11, 23, 18, 0)).Where(u => u.Day == Today).Select(u => u.BlockStartHour));
    }

    [Fact]
    public void T56_Block_key_filter_and_window_under_de_DE()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var u = Build("very_short_load_frcst", Samples.Started).Single(x => x.Day == D18 && x.BlockStartHour == 6);
            Assert.Equal(PjmZone.Settled, u.Zone);
            Assert.Equal("pjm:very_short_load_frcst:20261118T06", u.Key);
            Assert.Equal("11/18/2026 06:00to11/18/2026 11:59", u.FilterValue);
            Assert.Equal(At(D18, 6), u.WindowStartUtc);
            Assert.Equal(At(D18, 11, 59), u.WindowEndUtc);
            Assert.Equal("2026-11-18 06:00-11:59 UTC", u.Label);
            Assert.Equal("PJM very_short_load_frcst 2026-11-18T06 (settled)", u.DisplayName);
            Assert.Contains("&evaluated_at_utc=11%2F18%2F2026%2006%3A00to11%2F18%2F2026%2011%3A59", Client().BuildPathAndQuery(u, 1));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = saved;
        }
    }

    [Fact]
    public void T56_Block_is_Recent_until_end_plus_RecentHours()
    {
        Assert.Equal(PjmZone.Recent, Build("very_short_load_frcst", Utc(2026, 11, 23, 13, 59)).Single(u => u.Day == Today && u.BlockStartHour == 6).Zone);
        var at14 = Build("very_short_load_frcst", Utc(2026, 11, 23, 14, 0)).Single(u => u.Day == Today && u.BlockStartHour == 6);
        Assert.Equal(PjmZone.Hot, at14.Zone);
        Assert.Equal("pjm:very_short_load_frcst:20261123T06;run=20261123", at14.Key);

        // Yesterday's last block: Recent until 02:00 today, then Settled (ceiling 1 → only today is Hot).
        Assert.Equal(PjmZone.Recent, Build("very_short_load_frcst", Utc(2026, 11, 23, 1, 59)).Single(u => u.Day == Today.AddDays(-1) && u.BlockStartHour == 18).Zone);
        Assert.Equal(PjmZone.Settled, Build("very_short_load_frcst", Utc(2026, 11, 23, 2, 0)).Single(u => u.Day == Today.AddDays(-1) && u.BlockStartHour == 18).Zone);
    }

    [Fact]
    public void T56_Nothing_older_than_today_minus_VeryShortMaxDaysBack()
    {
        var units = Build("very_short_load_frcst", Samples.Started);
        Assert.Equal(Today.AddDays(-28), units.Min(u => u.Day));

        var wide = Samples.Settings(x => { x.DaysBack = 60; x.BackfillDays = 60; });
        Assert.Equal(Today.AddDays(-28), Build("very_short_load_frcst", Samples.Started, wide).Min(u => u.Day));
        var narrow = Samples.Settings(x => x.VeryShortMaxDaysBack = 20);
        Assert.Equal(Today.AddDays(-20), Build("very_short_load_frcst", Samples.Started, narrow).Min(u => u.Day));
    }

    [Fact]
    public void T56_A_full_6_hour_block_is_43200_rows_and_fits_one_page()
    {
        // 72 evaluations × 24 steps × 25 areas (API doc §12.6; replayed for real in LiveReplayTests).
        Assert.Equal(43_200, 72 * 24 * 25);
        Assert.True(43_200 <= new PjmSettings().PageSize);
        Assert.Equal(6, Feed("very_short_load_frcst").UnitHours);
    }

    // =========================================================================
    // 57: settle floors and ceilings
    // =========================================================================

    [Theory]
    [InlineData("inst_load", 10, 10)]
    [InlineData("gen_by_fuel", 10, 10)]
    [InlineData("hrl_load_prelim", 10, 10)]
    [InlineData("hrl_load_metered", 10, 30)]
    [InlineData("hrl_load_estimated", 10, 14)]
    [InlineData("very_short_load_frcst", 10, 1)]
    [InlineData("load_frcstd_hist", 10, 2)]
    [InlineData("hrl_load_metered", 20, 30)]
    [InlineData("hrl_load_estimated", 20, 20)]
    [InlineData("very_short_load_frcst", 20, 1)]
    [InlineData("load_frcstd_hist", 20, 2)]
    [InlineData("gen_by_fuel", 20, 20)]
    [InlineData("hrl_load_metered", 1, 30)]
    [InlineData("hrl_load_estimated", 1, 14)]
    [InlineData("load_frcstd_hist", 1, 1)]
    public void T57_Effective_settle_per_feed(string feed, int settledAfterDays, int expected)
    {
        Assert.Equal(expected, Feed(feed).EffectiveSettledAfterDays(settledAfterDays));
    }

    [Fact]
    public void T57_Only_the_two_immutable_forecast_feeds_carry_a_ceiling()
    {
        var ceilings = PjmFeeds.All(S).Where(f => f.SettledAfterDaysCeiling is not null).ToDictionary(f => f.Name, f => f.SettledAfterDaysCeiling);
        Assert.Equal(new Dictionary<string, int?> { ["very_short_load_frcst"] = 1, ["load_frcstd_hist"] = 2 }, ceilings);
        var floors = PjmFeeds.All(S).Where(f => f.MinSettledAfterDays is not null).ToDictionary(f => f.Name, f => f.MinSettledAfterDays);
        Assert.Equal(new Dictionary<string, int?> { ["hrl_load_metered"] = 30, ["hrl_load_estimated"] = 14 }, floors);
    }

    [Fact]
    public void T57_Ceilings_hold_and_settled_keys_stay_stable_across_two_days_with_SettledAfterDays_20()
    {
        var s = Samples.Settings(x => x.SettledAfterDays = 20);
        var day1 = Utc(2026, 11, 23, 14, 20);
        var day2 = day1.AddDays(1);

        var h1 = PjmWorkUnitProvider.Build(PjmFeeds.LoadForecastHist(s), s, day1).Single(u => u.Day == new DateOnly(2026, 11, 20));
        var h2 = PjmWorkUnitProvider.Build(PjmFeeds.LoadForecastHist(s), s, day2).Single(u => u.Day == new DateOnly(2026, 11, 20));
        Assert.Equal(PjmZone.Settled, h1.Zone);
        Assert.Equal("pjm:load_frcstd_hist:20261120", h1.Key);
        Assert.Equal(h1.Key, h2.Key);

        var v1 = PjmWorkUnitProvider.Build(PjmFeeds.LoadForecastVeryShort(s), s, day1).Single(u => u.Day == new DateOnly(2026, 11, 21) && u.BlockStartHour == 6);
        var v2 = PjmWorkUnitProvider.Build(PjmFeeds.LoadForecastVeryShort(s), s, day2).Single(u => u.Day == new DateOnly(2026, 11, 21) && u.BlockStartHour == 6);
        Assert.Equal(PjmZone.Settled, v1.Zone);
        Assert.Equal("pjm:very_short_load_frcst:20261121T06", v1.Key);
        Assert.Equal(v1.Key, v2.Key);

        // While gen_by_fuel at the same age follows the widened global lever and stays Hot.
        Assert.Equal(PjmZone.Hot, PjmWorkUnitProvider.Build(PjmFeeds.GenByFuel(s), s, day1).Single(u => u.Day == new DateOnly(2026, 11, 20)).Zone);
    }

    [Fact]
    public void T57_Metered_floor_30_holds_with_SettledAfterDays_10()
    {
        var units = Build("hrl_load_metered", Samples.Started);
        Assert.Equal(PjmZone.Hot, units.Single(u => u.Day == Today.AddDays(-29)).Zone);
        Assert.Equal(PjmZone.Settled, units.Single(u => u.Day == Today.AddDays(-30)).Zone);
        var est = Build("hrl_load_estimated", Samples.Started);
        Assert.Equal(PjmZone.Hot, est.Single(u => u.Day == Today.AddDays(-13)).Zone);
        Assert.Equal(PjmZone.Settled, est.Single(u => u.Day == Today.AddDays(-14)).Zone);
    }

    [Fact]
    public void T57_Startup_rule_rejects_effective_settle_beyond_depth()
    {
        var errors = new PjmSettings { BackfillDays = 20 }.Validate();
        var settleErrors = errors.Where(e => e.Contains("effective SettledAfterDays")).ToList();
        Assert.Single(settleErrors);
        Assert.Contains("hrl_load_metered", settleErrors[0]);
        Assert.Contains("(30)", settleErrors[0]);

        // Not enabled → not checked.
        var off = new PjmSettings { BackfillDays = 20, EnabledFeeds = new[] { "inst_load", "gen_by_fuel" } }.Validate();
        Assert.DoesNotContain(off, e => e.Contains("effective SettledAfterDays"));
    }

    // =========================================================================
    // 58: snapshot key
    // =========================================================================

    [Theory]
    [InlineData(14, 14, "202611231345")]
    [InlineData(14, 15, "202611231415")]
    [InlineData(14, 44, "202611231415")]
    [InlineData(14, 45, "202611231445")]
    [InlineData(14, 59, "202611231445")]
    [InlineData(0, 10, "202611222345")]
    public void T58_pub_is_the_latest_15_or_45_at_or_before_now(int h, int m, string pub)
    {
        var u = Build("load_frcstd_7_day", Utc(2026, 11, 23, h, m)).Single();
        Assert.Equal(PjmZone.Snapshot, u.Zone);
        Assert.Equal($"pjm:load_frcstd_7_day:pub={pub}", u.Key);
    }

    [Fact]
    public void T58_Same_slot_same_key_next_slot_new_key_and_no_secret_URL_or_run_id()
    {
        var a = Build("load_frcstd_7_day", Utc(2026, 11, 23, 14, 15)).Single().Key;
        var b = Build("load_frcstd_7_day", Utc(2026, 11, 23, 14, 44)).Single().Key;
        var c = Build("load_frcstd_7_day", Utc(2026, 11, 23, 14, 45)).Single().Key;
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);

        foreach (var key in new[] { a, c })
        {
            Assert.Matches(new Regex(@"^pjm:load_frcstd_7_day:pub=\d{12}$"), key);
            Assert.DoesNotContain("run=", key);
            Assert.DoesNotContain("http", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("?", key);
        }
    }

    // =========================================================================
    // 59: filter / sort / echo on FilterField
    // =========================================================================

    [Fact]
    public void T59_Each_feed_filters_and_sorts_on_its_FilterField_and_7_day_has_no_filter()
    {
        var c = Client();
        var vs = FeedSamples.Unit(Feed("very_short_load_frcst"), D18, 5, PjmZone.Settled, 6);
        Assert.Equal(
            "/very_short_load_frcst?rowCount=50000&startRow=1&sort=evaluated_at_utc&order=Asc&evaluated_at_utc=11%2F18%2F2026%2006%3A00to11%2F18%2F2026%2011%3A59",
            c.BuildPathAndQuery(vs, 1));
        Assert.Equal(
            "/load_frcstd_hist?rowCount=50000&startRow=1&sort=evaluated_at_utc&order=Asc&evaluated_at_utc=11%2F18%2F2026%2000%3A00to11%2F18%2F2026%2023%3A59",
            c.BuildPathAndQuery(FeedSamples.Unit(Feed("load_frcstd_hist"), D18, 5), 1));
        foreach (var f in new[] { "hrl_load_metered", "hrl_load_prelim", "hrl_load_estimated" })
            Assert.Equal(
                $"/{f}?rowCount=50000&startRow=1&sort=datetime_beginning_utc&order=Asc&datetime_beginning_utc=11%2F18%2F2026%2000%3A00to11%2F18%2F2026%2023%3A59",
                c.BuildPathAndQuery(FeedSamples.Unit(Feed(f), D18, 5), 1));

        var snap = FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0));
        Assert.Null(snap.FilterValue);
        Assert.Equal("/load_frcstd_7_day?rowCount=50000&startRow=1&sort=forecast_datetime_beginning_utc&order=Asc", c.BuildPathAndQuery(snap, 1));
    }

    [Fact]
    public async Task T59_very_short_row_outside_the_block_fails_but_targets_outside_are_accepted()
    {
        var unit = FeedSamples.Unit(Feed("very_short_load_frcst"), D18, 5, PjmZone.Settled, 6);

        var ok = await Read("very_short_load_frcst", new[] { FeedSamples.VeryShort(At(D18, 11, 55), At(D18, 13, 50)) }, unit);
        Assert.Null(ok.Error);

        foreach (var outside in new[] { At(D18, 12, 0), At(D18, 5, 55) })
        {
            var bad = await Read("very_short_load_frcst", new[] { FeedSamples.VeryShort(outside, outside) }, unit);
            var ex = Assert.IsType<PjmDataException>(bad.Error);
            Assert.Contains("evaluated_at_utc", ex.Message);
            Assert.Contains("outside the requested UTC block", ex.Message);
            Assert.Null(ex.FailureClass);
        }
    }

    [Fact]
    public async Task T59_hist_row_evaluated_outside_the_day_fails_but_forecast_days_ahead_are_accepted()
    {
        var unit = FeedSamples.Unit(Feed("load_frcstd_hist"), D18, 5);
        var ok = await Read("load_frcstd_hist", new[] { FeedSamples.Hist(At(D18, 22, 45), At(D18.AddDays(6), 23)) }, unit);
        Assert.Null(ok.Error);

        var bad = await Read("load_frcstd_hist", new[] { FeedSamples.Hist(At(D18.AddDays(1), 3, 45), At(D18.AddDays(1), 5)) }, unit);
        var ex = Assert.IsType<PjmDataException>(bad.Error);
        Assert.Contains("evaluated_at_utc 2026-11-19T03:45:00 lies outside the requested UTC day", ex.Message);
    }

    // =========================================================================
    // 60: stamp modes
    // =========================================================================

    [Fact]
    public async Task T60_7_day_evaluated_at_keeps_real_seconds_and_passes_the_EPT_check()
    {
        var (item, unit) = Default("load_frcstd_7_day");
        var r = await Read("load_frcstd_7_day", new[] { item }, unit);
        Assert.Null(r.Error);
        var row = Assert.IsType<PjmLoadForecast7DayRow>(r.Rows!.Single());
        Assert.Equal(At(Today, 22, 47, 12), row.EvaluatedAtDatetimeUtc);
        Assert.Equal(At(Today, 17, 47, 12), row.EvaluatedAtDatetimeEpt);
        Assert.Equal(PjmStampMode.Exact, Feed("load_frcstd_7_day").Stamps[0].Mode);
    }

    [Fact]
    public async Task T60_very_short_evaluated_at_off_the_5_minute_mark_fails()
    {
        var unit = FeedSamples.Unit(Feed("very_short_load_frcst"), D18, 5, PjmZone.Settled, 6);
        var r = await Read("very_short_load_frcst", new[] { FeedSamples.VeryShort(At(D18, 6, 3), At(D18, 6, 5)) }, unit);
        var ex = Assert.IsType<PjmDataException>(r.Error);
        Assert.Contains("evaluated_at_utc", ex.Message);
        Assert.Contains("5-minute boundary", ex.Message);
    }

    [Fact]
    public async Task T60_hist_evaluated_at_on_minute_45_has_no_hourly_boundary_check()
    {
        // §14.14 V-4 (overrides §14.6 "Exact"): evaluations sit on :45 with zero seconds; truncating
        // to the minute is harmless, but there must be NO hourly boundary check on evaluated_at.
        var unit = FeedSamples.Unit(Feed("load_frcstd_hist"), D18, 5);
        var r = await Read("load_frcstd_hist", new[] { FeedSamples.Hist(At(D18, 14, 45), At(D18, 15)) }, unit);
        Assert.Null(r.Error);
        var row = Assert.IsType<PjmLoadForecastHistRow>(r.Rows!.Single());
        Assert.Equal(At(D18, 14, 45), row.EvaluatedAtUtc);
        Assert.Equal(At(D18, 9, 45), row.EvaluatedAtEpt);

        // Seconds within the jitter tolerance truncate to the minute (Minute mode), never fail.
        var jitter = await Read("load_frcstd_hist", new[] { FeedSamples.Hist(At(D18, 14, 45, 7), At(D18, 15)) }, unit);
        Assert.Null(jitter.Error);
        Assert.Equal(At(D18, 14, 45), Assert.IsType<PjmLoadForecastHistRow>(jitter.Rows!.Single()).EvaluatedAtUtc);

        // forecast_hour_beginning keeps its hourly boundary check.
        var offHour = await Read("load_frcstd_hist", new[] { FeedSamples.Hist(At(D18, 14, 45), At(D18, 15, 30)) }, unit);
        Assert.Contains("60-minute boundary", Assert.IsType<PjmDataException>(offHour.Error).Message);
    }

    [Theory]
    [InlineData("hrl_load_metered")]
    [InlineData("hrl_load_prelim")]
    [InlineData("hrl_load_estimated")]
    public async Task T60_hrl_stamp_at_00_02_is_truncated_to_00_00(string feed)
    {
        var (item, unit) = Default(feed);
        foreach (var rule in Feed(feed).Stamps)
        {
            item = With(item, rule.UtcField, item.GetProperty(rule.UtcField).GetString()![..17] + "02");
            item = With(item, rule.EptField, item.GetProperty(rule.EptField).GetString()![..17] + "02");
        }

        var r = await Read(feed, new[] { item }, unit);
        Assert.Null(r.Error);
        var row = r.Rows!.Single();
        Assert.Equal(At(D18, 6), row.Key.K1);
        switch (row)
        {
            case PjmLoadMeteredRow m:
                Assert.Equal(At(D18, 1), m.DatetimeBeginningEpt);
                break;
            case PjmLoadPrelimRow p:
                Assert.Equal((At(D18, 1), At(D18, 7), At(D18, 2)), (p.DatetimeBeginningEpt, p.DatetimeEndingUtc, p.DatetimeEndingEpt));
                break;
            case PjmLoadEstimatedRow e:
                Assert.Equal((At(D18, 1), At(D18, 7), At(D18, 2)), (e.DatetimeBeginningEpt, e.DatetimeEndingUtc, e.DatetimeEndingEpt));
                break;
        }
    }

    // =========================================================================
    // 61: EPT on every pair
    // =========================================================================

    [Theory]
    [InlineData("hrl_load_metered", 1)]
    [InlineData("hrl_load_prelim", 2)]
    [InlineData("hrl_load_estimated", 2)]
    [InlineData("load_frcstd_hist", 2)]
    [InlineData("very_short_load_frcst", 3)]
    [InlineData("load_frcstd_7_day", 3)]
    public void T61_Pair_counts_per_feed(string feed, int pairs) => Assert.Equal(pairs, Feed(feed).Stamps.Count);

    public static IEnumerable<object[]> EveryPair() =>
        from f in new[] { "hrl_load_metered", "hrl_load_prelim", "hrl_load_estimated", "load_frcstd_hist", "very_short_load_frcst", "load_frcstd_7_day" }
        from r in PjmFeeds.All(new PjmSettings()).Single(x => x.Name == f).Stamps
        select new object[] { f, r.EptField };

    [Theory]
    [MemberData(nameof(EveryPair))]
    public async Task T61_A_wrong_EPT_on_any_single_pair_fails_the_unit(string feed, string eptField)
    {
        var (item, unit) = Default(feed);
        var ok = await Read(feed, new[] { item }, unit);
        Assert.Null(ok.Error);

        var wrong = DateTime.ParseExact(item.GetProperty(eptField).GetString()!, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture).AddHours(1);
        var bad = await Read(feed, new[] { With(item, eptField, Samples.Iso(wrong)) }, unit);
        var ex = Assert.IsType<PjmDataException>(bad.Error);
        Assert.Contains(eptField, ex.Message);
        Assert.Contains("is not Eastern prevailing time of", ex.Message);
    }

    [Fact]
    public async Task T61_Fall_back_day_passes_for_hrl_metered_with_24_UTC_hours()
    {
        var items = Enumerable.Range(0, 24).Select(h => FeedSamples.Metered(At(Nov1, h))).ToList();
        var r = await Read("hrl_load_metered", items, FeedSamples.Unit(Feed("hrl_load_metered"), Nov1, 22, PjmZone.Settled));

        Assert.Null(r.Error);
        var rows = r.Rows!.Cast<PjmLoadMeteredRow>().ToList();
        Assert.Equal(24, rows.Count);
        Assert.Equal(At(Nov1, 1), rows.Single(x => x.DatetimeBeginningUtc == At(Nov1, 5)).DatetimeBeginningEpt);   // EDT
        Assert.Equal(At(Nov1, 1), rows.Single(x => x.DatetimeBeginningUtc == At(Nov1, 6)).DatetimeBeginningEpt);   // EST, repeated
        Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
    }

    // =========================================================================
    // 62: end offsets (UTC only) are data failures that do not trip the guard
    // =========================================================================

    [Fact]
    public async Task T62_prelim_ending_not_beginning_plus_60_fails()
    {
        var (item, unit) = Default("hrl_load_prelim");
        item = With(item, "datetime_ending_utc", Samples.Iso(At(D18, 8)));
        item = With(item, "datetime_ending_ept", Samples.Iso(At(D18, 3)));
        var ex = Assert.IsType<PjmDataException>((await Read("hrl_load_prelim", new[] { item }, unit)).Error);
        Assert.Contains("datetime_ending_utc", ex.Message);
        Assert.Contains("is 120 min, expected 60", ex.Message);
        Assert.Null(ex.FailureClass);
    }

    [Fact]
    public async Task T62_very_short_ending_not_beginning_plus_5_fails()
    {
        var (item, unit) = Default("very_short_load_frcst");
        item = With(item, "forecast_datetime_ending_utc", Samples.Iso(At(D18, 6, 40)));
        item = With(item, "forecast_datetime_ending_ept", Samples.Iso(At(D18, 1, 40)));
        var ex = Assert.IsType<PjmDataException>((await Read("very_short_load_frcst", new[] { item }, unit)).Error);
        Assert.Contains("is 10 min, expected 5", ex.Message);
    }

    [Fact]
    public async Task T62_End_offset_failures_reset_the_guard_streak_rather_than_count()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        guard.RecordUnitFailure(PjmFailureClass.Http400);
        guard.RecordUnitFailure(PjmFailureClass.Http400);

        var (item, unit) = Default("hrl_load_prelim");
        item = With(item, "datetime_ending_utc", Samples.Iso(At(D18, 8)));
        item = With(item, "datetime_ending_ept", Samples.Iso(At(D18, 3)));
        var reader = new PjmSourceReader<PjmLoadPrelimRow>(
            FeedSamples.Reader(FeedSamples.Page(new[] { item }), new PjmLoadPrelimRowMapper(), new CapturingLoggerFactory()), guard);
        await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadAsync(unit, default));

        guard.RecordUnitFailure(PjmFailureClass.Http400);
        guard.RecordUnitFailure(PjmFailureClass.Http400);
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task T62_EPT_ending_minus_beginning_on_the_fall_back_hour_is_not_checked()
    {
        // §14.14 V-2: EPT is naive wall clock: prelim shows 0 min and very_short −55 min on the
        // fall-back hour; the offset rule runs on the UTC pair only, so both must load.
        var prelim = FeedSamples.Prelim(At(Nov1, 5));
        Assert.Equal(prelim.GetProperty("datetime_beginning_ept").GetString(), prelim.GetProperty("datetime_ending_ept").GetString());
        Assert.Null((await Read("hrl_load_prelim", new[] { prelim }, FeedSamples.Unit(Feed("hrl_load_prelim"), Nov1, 22, PjmZone.Settled))).Error);
        Assert.Null((await Read("hrl_load_estimated", new[] { FeedSamples.Estimated(At(Nov1, 5)) }, FeedSamples.Unit(Feed("hrl_load_estimated"), Nov1, 22, PjmZone.Settled))).Error);

        var vs = FeedSamples.VeryShort(At(Nov1, 5, 55), At(Nov1, 5, 55));
        Assert.Equal("2026-11-01T01:55:00", vs.GetProperty("forecast_datetime_beginning_ept").GetString());
        Assert.Equal("2026-11-01T01:00:00", vs.GetProperty("forecast_datetime_ending_ept").GetString());
        Assert.Null((await Read("very_short_load_frcst", new[] { vs }, FeedSamples.Unit(Feed("very_short_load_frcst"), Nov1, 22, PjmZone.Settled, 0))).Error);

        Assert.All(PjmFeeds.All(S).SelectMany(f => f.EndOffsets), o =>
        {
            Assert.EndsWith("_utc", o.BeginUtcField);
            Assert.EndsWith("_utc", o.EndUtcField);
        });
    }

    // =========================================================================
    // 63: three-column duplicate key
    // =========================================================================

    private static async Task<(LoaderRunResult Result, FakeSink<TRow> Sink, FakeLoadLog Log)> RunPipeline<TRow>(
        PjmFeed feed, IPjmRowMapper<TRow> mapper, IReadOnlyList<JsonElement> items, PjmDayWorkUnit unit, ISink<TRow>? sinkOverride = null)
        where TRow : IPjmRow
    {
        var settings = Samples.Settings();
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        var reader = new PjmDayReader<TRow>(FeedSamples.Page(items), mapper, settings, ClientFactory.Null, FeedSamples.Clock(unit.RunStartedUtc ?? Samples.Started));
        var sink = new FakeSink<TRow>();
        var log = new FakeLoadLog();
        var pipeline = new PjmPipeline<TRow>(feed.Name, new FixedUnitProvider(unit), new PjmSourceReader<TRow>(reader, guard),
            sinkOverride ?? sink, log, settings, ClientFactory.Null);
        var result = await pipeline.ExecuteAsync(new LoaderRunContext { RunId = Guid.NewGuid(), StartedAtUtc = Samples.Started, CancellationToken = default });
        return (result, sink, log);
    }

    [Fact]
    public async Task T63_hist_three_column_duplicate_fails_names_all_fields_and_writes_nothing()
    {
        var feed = Feed("load_frcstd_hist");
        var items = new[] { FeedSamples.Hist(At(D18, 3, 45), At(D18, 5)), FeedSamples.Hist(At(D18, 3, 45), At(D18, 5), "rto_combined ") };
        var (result, sink, log) = await RunPipeline(feed, new PjmLoadForecastHistRowMapper(), items, FeedSamples.Unit(feed, D18, 5));

        Assert.False(result.Success);
        Assert.Empty(sink.Writes);
        var error = Assert.Single(log.Failures).Error;
        Assert.Contains("duplicate key (evaluated_at_utc 2026-11-18T03:45:00, forecast_hour_beginning_utc 2026-11-18T05:00:00, forecast_area 'RTO_COMBINED')", error);
    }

    [Fact]
    public async Task T63_Same_forecast_hour_and_area_from_different_evaluations_is_accepted()
    {
        var feed = Feed("load_frcstd_hist");
        var items = new[]
        {
            FeedSamples.Hist(At(D18, 3, 45), At(D18, 20)),
            FeedSamples.Hist(At(D18, 10, 45), At(D18, 20)),
            FeedSamples.Hist(At(D18, 14, 45), At(D18, 20)),
        };
        var (result, sink, _) = await RunPipeline(feed, new PjmLoadForecastHistRowMapper(), items, FeedSamples.Unit(feed, D18, 5));
        Assert.True(result.Success);
        Assert.Equal(3, Assert.Single(sink.Writes).Count);
    }

    [Fact]
    public async Task T63_very_short_and_7_day_duplicates_on_the_three_column_key_fail()
    {
        var (vs, vsUnit) = Default("very_short_load_frcst");
        var vsDup = await Read("very_short_load_frcst", new[] { vs, vs }, vsUnit);
        Assert.Contains("forecast_datetime_beginning_utc", Assert.IsType<PjmDataException>(vsDup.Error).Message);

        var (sd, sdUnit) = Default("load_frcstd_7_day");
        var sdDup = await Read("load_frcstd_7_day", new[] { sd, sd }, sdUnit);
        Assert.Contains("evaluated_at_datetime_utc 2026-11-23T22:47:12", Assert.IsType<PjmDataException>(sdDup.Error).Message);
    }

    // =========================================================================
    // 64: NullableString and is_verified
    // =========================================================================

    private static PjmItem Item(Dictionary<string, object?> fields) =>
        new("hrl_load_metered", 0, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!);

    [Fact]
    public void T64_NullableString_null_too_long_and_non_string()
    {
        Assert.Null(PjmValueParser.NullableString(Item(new() { ["zone"] = null }), "zone"));
        Assert.Null(PjmValueParser.NullableString(Item(new()), "zone"));
        Assert.Equal(" AE ", PjmValueParser.NullableString(Item(new() { ["zone"] = " AE " }), "zone"));   // never trimmed
        Assert.Equal(new string('z', 50), PjmValueParser.NullableString(Item(new() { ["zone"] = new string('z', 50) }), "zone"));

        var tooLong = Assert.Throws<PjmContractException>(() => PjmValueParser.NullableString(Item(new() { ["zone"] = new string('z', 51) }), "zone"));
        Assert.Contains("'zone'", tooLong.Message);
        Assert.Contains("50-character", tooLong.Message);
        Assert.Throws<PjmContractException>(() => PjmValueParser.NullableString(Item(new() { ["zone"] = 7 }), "zone"));
        Assert.Throws<PjmContractException>(() => PjmValueParser.NullableString(Item(new() { ["zone"] = true }), "zone"));
    }

    [Fact]
    public async Task T64_Metered_zone_null_maps_to_NULL_and_is_verified_accepts_true_false_string_1_null()
    {
        var unit = FeedSamples.Unit(Feed("hrl_load_metered"), D18, 5);
        var values = new object?[] { true, "false", 1, null };
        var items = values.Select((v, i) => FeedSamples.Metered(At(D18, i), fields: new() { ["is_verified"] = v, ["zone"] = null })).ToList();

        var r = await Read("hrl_load_metered", items, unit);
        Assert.Null(r.Error);
        var rows = r.Rows!.Cast<PjmLoadMeteredRow>().ToList();
        Assert.Equal(new bool?[] { true, false, true, null }, rows.Select(x => x.IsVerified));
        Assert.All(rows, x => Assert.Null(x.Zone));

        var bad = await Read("hrl_load_metered", new[] { FeedSamples.Metered(At(D18, 0), fields: new() { ["zone"] = 12 }) }, unit);
        Assert.IsType<PjmContractException>(bad.Error);
        var badBool = await Read("hrl_load_metered", new[] { FeedSamples.Metered(At(D18, 0), fields: new() { ["is_verified"] = "yes" }) }, unit);
        Assert.IsType<PjmContractException>(badBool.Error);
    }

    // =========================================================================
    // 65: empty-read matrix (§14.7)
    // =========================================================================

    [Theory]
    [InlineData("hrl_load_metered", 1, true)]
    [InlineData("hrl_load_metered", 2, false)]
    [InlineData("hrl_load_prelim", 1, true)]
    [InlineData("hrl_load_prelim", 2, false)]
    [InlineData("hrl_load_estimated", 1, true)]
    [InlineData("hrl_load_estimated", 10, true)]
    [InlineData("hrl_load_estimated", 11, false)]
    [InlineData("hrl_load_estimated", 30, false)]
    [InlineData("load_frcstd_hist", 1, true)]
    [InlineData("load_frcstd_hist", 2, false)]
    public async Task T65_Day_feed_empty_read_by_age(string feed, int age, bool success)
    {
        var unit = FeedSamples.Unit(Feed(feed), Today.AddDays(-age), age);
        var r = await Read(feed, Array.Empty<JsonElement>(), unit);
        if (success)
        {
            Assert.Null(r.Error);
            Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
        }
        else
        {
            var ex = Assert.IsType<PjmDataException>(r.Error);
            Assert.Equal(PjmFailureClass.EmptyPastDay, ex.FailureClass);
            Assert.Contains($"age {age}", ex.Message);
        }
    }

    [Fact]
    public async Task T65_Estimated_partial_day_at_age_5_succeeds_with_no_completeness_warning()
    {
        var day = Today.AddDays(-5);
        var items = Enumerable.Range(0, 5).Select(h => FeedSamples.Estimated(At(day, h))).ToList();
        var r = await Read("hrl_load_estimated", items, FeedSamples.Unit(Feed("hrl_load_estimated"), day, 5));
        Assert.Null(r.Error);
        Assert.Equal(5, r.Rows!.Count);
        Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
    }

    [Theory]
    [InlineData(0, PjmZone.Recent, "success")]
    [InlineData(1, PjmZone.Recent, "success")]
    [InlineData(0, PjmZone.Hot, "fail")]        // a block of today that left its Recent tail
    [InlineData(1, PjmZone.Settled, "fail")]
    [InlineData(25, PjmZone.Settled, "fail")]
    [InlineData(26, PjmZone.Settled, "warn")]
    [InlineData(27, PjmZone.Settled, "warn")]
    [InlineData(28, PjmZone.Settled, "warn")]
    public async Task T65_very_short_empty_block_by_zone_and_age(int age, PjmZone zone, string outcome)
    {
        var unit = FeedSamples.Unit(Feed("very_short_load_frcst"), Today.AddDays(-age), age, zone, blockStartHour: 6);
        var r = await Read("very_short_load_frcst", Array.Empty<JsonElement>(), unit);
        switch (outcome)
        {
            case "success":
                Assert.Null(r.Error);
                Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
                break;
            case "fail":
                var ex = Assert.IsType<PjmDataException>(r.Error);
                Assert.Equal(PjmFailureClass.EmptyPastDay, ex.FailureClass);
                Assert.Contains("0 rows for a past block inside retention", ex.Message);
                break;
            case "warn":
                Assert.Null(r.Error);
                Assert.Single(r.Logs.OfLevel(LogLevel.Warning));
                break;
        }
    }

    [Fact]
    public async Task T65_Empty_7_day_snapshot_fails_with_class_EmptySnapshot()
    {
        var unit = FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0));
        var ex = Assert.IsType<PjmDataException>((await Read("load_frcstd_7_day", Array.Empty<JsonElement>(), unit)).Error);
        Assert.Equal(PjmFailureClass.EmptySnapshot, ex.FailureClass);
        Assert.Contains("empty snapshot", ex.Message);
    }

    // =========================================================================
    // 66: guard trips across feeds; estimated 1-10 never counts
    // =========================================================================

    private static async Task Unit<TRow>(PjmRunGuard guard, string feed, IPjmRowMapper<TRow> mapper, PjmDayWorkUnit unit, IReadOnlyList<JsonElement>? items = null)
        where TRow : IPjmRow
    {
        var reader = new PjmSourceReader<TRow>(FeedSamples.Reader(FeedSamples.Page(items ?? Array.Empty<JsonElement>()), mapper, new CapturingLoggerFactory()), guard);
        try { await reader.ReadAsync(unit, default); } catch (PjmDataException) { } catch (PjmRequestException) { }
    }

    private static PjmDayWorkUnit AgeUnit(string feed, int age) => FeedSamples.Unit(Feed(feed), Today.AddDays(-age), age, PjmZone.Hot);

    [Fact]
    public async Task T66_EmptySnapshot_then_two_empty_past_days_across_feeds_trip_the_guard()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        await Unit(guard, "7", new PjmLoadForecast7DayRowMapper(), FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0)));
        await Unit(guard, "m", new PjmLoadMeteredRowMapper(), AgeUnit("hrl_load_metered", 2));
        Assert.False(guard.IsTripped);
        await Unit(guard, "h", new PjmLoadForecastHistRowMapper(), AgeUnit("load_frcstd_hist", 2));
        Assert.True(guard.IsTripped);
        Assert.Contains("empty-past-day", guard.Reason);
    }

    [Fact]
    public async Task T66_Three_consecutive_EmptySnapshot_trip_the_guard()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        for (var i = 0; i < 3; i++)
            await Unit(guard, "7", new PjmLoadForecast7DayRowMapper(), FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0)));
        Assert.True(guard.IsTripped);
    }

    [Fact]
    public async Task T66_Estimated_empty_at_ages_1_to_10_never_counts_and_resets_the_streak()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        for (var age = 1; age <= 10; age++)
            await Unit(guard, "e", new PjmLoadEstimatedRowMapper(), AgeUnit("hrl_load_estimated", age));
        Assert.False(guard.IsTripped);

        await Unit(guard, "m", new PjmLoadMeteredRowMapper(), AgeUnit("hrl_load_metered", 2));
        await Unit(guard, "p", new PjmLoadPrelimRowMapper(), AgeUnit("hrl_load_prelim", 2));
        await Unit(guard, "e", new PjmLoadEstimatedRowMapper(), AgeUnit("hrl_load_estimated", 4));   // success → reset
        await Unit(guard, "h", new PjmLoadForecastHistRowMapper(), AgeUnit("load_frcstd_hist", 2));
        Assert.False(guard.IsTripped);

        // Estimated beyond its band DOES count.
        await Unit(guard, "e", new PjmLoadEstimatedRowMapper(), AgeUnit("hrl_load_estimated", 11));
        await Unit(guard, "e", new PjmLoadEstimatedRowMapper(), AgeUnit("hrl_load_estimated", 12));
        Assert.True(guard.IsTripped);
    }

    // =========================================================================
    // 67: snapshot checks
    // =========================================================================

    [Fact]
    public async Task T67_Two_distinct_evaluations_is_a_contract_failure()
    {
        var unit = FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0));
        var items = new[]
        {
            FeedSamples.SevenDay(At(Today, 22, 47, 12), At(Today.AddDays(1), 0)),
            FeedSamples.SevenDay(At(Today, 22, 17, 5), At(Today.AddDays(1), 1)),
        };
        var ex = Assert.IsType<PjmContractException>((await Read("load_frcstd_7_day", items, unit)).Error);
        Assert.Contains("2 distinct evaluated_at_datetime_utc", ex.Message);
    }

    [Fact]
    public async Task T67_Evaluation_10_minutes_after_the_response_time_fails()
    {
        // The check compares against when the response was READ (review fix), not RunStartedUtc.
        var unit = FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 22, 0));
        var items = new[] { FeedSamples.SevenDay(At(Today, 22, 47, 12), At(Today.AddDays(1), 0)) };
        var ex = Assert.IsType<PjmDataException>((await Read("load_frcstd_7_day", items, unit, time: FeedSamples.Clock(Utc(2026, 11, 23, 22, 37)))).Error);
        Assert.Contains("after it was read (2026-11-23T22:37:00)", ex.Message);

        // +4 min12s past the read time is inside the 5-minute tolerance.
        Assert.Null((await Read("load_frcstd_7_day", items, unit, time: FeedSamples.Clock(Utc(2026, 11, 23, 22, 43)))).Error);
    }

    [Fact]
    public async Task T67_First_run_snapshot_read_30_minutes_after_the_run_start_passes()
    {
        // A first run takes ~1 h; the snapshot unit can be read long after StartedAtUtc. An evaluation
        // issued 30 min after the run start (fresh at read time) must pass, not fail as "future".
        var started = Utc(2026, 11, 23, 22, 17);
        var unit = FeedSamples.Snapshot(Feed("load_frcstd_7_day"), started);
        var items = FeedSamples.SevenDayArea(At(Today, 22, 47, 12), "RTO_COMBINED");
        var r = await Read("load_frcstd_7_day", items, unit, time: FeedSamples.Clock(Utc(2026, 11, 23, 22, 50)));
        Assert.Null(r.Error);
        Assert.Equal(168, r.Rows!.Count);
        Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
        Assert.True(At(Today, 22, 47, 12) > started.AddMinutes(30));
    }

    [Fact]
    public async Task T67_Stale_evaluation_warns_and_still_succeeds()
    {
        // Run started fresh, but the response is read 7 h after 22:47:12: stale is judged at read time.
        var unit = FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0));
        var items = FeedSamples.SevenDayArea(At(Today, 22, 47, 12), "RTO_COMBINED");
        var r = await Read("load_frcstd_7_day", items, unit, time: FeedSamples.Clock(Utc(2026, 11, 24, 5, 50)));
        Assert.Null(r.Error);
        Assert.Equal(168, r.Rows!.Count);
        var w = Assert.Single(r.Logs.OfLevel(LogLevel.Warning));
        Assert.Contains("h old", w.Message);
    }

    private sealed class ZeroSink<TRow> : ISink<TRow>
    {
        public int Calls;
        public Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken) { Calls++; return Task.FromResult(0); }
    }

    [Fact]
    public async Task T67_Re_read_of_a_stored_evaluation_logs_already_stored_and_reports_0()
    {
        var logs = new CapturingLoggerFactory();
        var inner = new ZeroSink<PjmLoadForecast7DayRow>();
        var sink = new PjmSnapshotSink<PjmLoadForecast7DayRow>(inner, PjmFeeds.LoadForecast7DayName, logs.CreateLogger("s"));
        var row = new PjmLoadForecast7DayRow
        {
            EvaluatedAtDatetimeUtc = At(Today, 22, 47, 12), EvaluatedAtDatetimeEpt = At(Today, 17, 47, 12),
            ForecastDatetimeBeginningUtc = At(Today, 23), ForecastDatetimeBeginningEpt = At(Today, 18),
            ForecastDatetimeEndingUtc = At(Today.AddDays(1), 0), ForecastDatetimeEndingEpt = At(Today, 19),
            ForecastArea = "RTO_COMBINED",
        };

        var written = await sink.WriteAsync(new[] { row }, default);

        Assert.Equal(0, written);
        Assert.Equal(1, inner.Calls);
        var info = Assert.Single(logs.OfLevel(LogLevel.Information));
        Assert.Contains("7_day evaluation 2026-11-23T22:47:12 already stored (0 changes)", info.Message);
    }

    [Fact]
    public async Task T67_A_snapshot_unit_through_the_pipeline_records_0_on_a_stored_evaluation()
    {
        var feed = Feed("load_frcstd_7_day");
        var unit = FeedSamples.Snapshot(feed, Utc(2026, 11, 23, 23, 0));
        var zero = new ZeroSink<PjmLoadForecast7DayRow>();
        var (result, _, log) = await RunPipeline(feed, new PjmLoadForecast7DayRowMapper(),
            FeedSamples.SevenDayArea(At(Today, 22, 47, 12), "RTO_COMBINED"), unit,
            new PjmSnapshotSink<PjmLoadForecast7DayRow>(zero, feed.Name, ClientFactory.Null));
        Assert.True(result.Success);
        Assert.Equal(("pjm:load_frcstd_7_day:pub=202611232245", 0), Assert.Single(log.Successes));
    }

    // =========================================================================
    // 68: completeness
    // =========================================================================

    private static List<JsonElement> VsBlock(DateOnly day, int blockStart, string area, int evaluations = 72) =>
        Enumerable.Range(0, evaluations)
            .Select(i => At(day, blockStart).AddMinutes(5 * i))
            .Select(ev => FeedSamples.VeryShort(ev, ev, area)).ToList();

    [Fact]
    public async Task T68_very_short_area_with_71_evaluations_in_a_past_block_warns_once()
    {
        var items = VsBlock(D18, 6, "AE").Concat(VsBlock(D18, 6, "DOM", 71)).ToList();
        var r = await Read("very_short_load_frcst", items, FeedSamples.Unit(Feed("very_short_load_frcst"), D18, 5, PjmZone.Settled, 6));
        Assert.Null(r.Error);
        var w = Assert.Single(r.Logs.OfLevel(LogLevel.Warning));
        Assert.Contains("below 72", w.Message);
        Assert.Contains("DOM", w.Message);

        // While Recent, a partial block is expected: no warning.
        var recentSameDay = await Read("very_short_load_frcst", items,
            FeedSamples.Unit(Feed("very_short_load_frcst"), D18, 0, PjmZone.Recent, 6));
        Assert.Null(recentSameDay.Error);
        Assert.Empty(recentSameDay.Logs.OfLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task T68_7_day_area_with_167_rows_warns()
    {
        var ev = At(Today, 22, 47, 12);
        var items = FeedSamples.SevenDayArea(ev, "AE").Concat(FeedSamples.SevenDayArea(ev, "DOM", 167)).ToList();
        var r = await Read("load_frcstd_7_day", items, FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Utc(2026, 11, 23, 23, 0)));
        Assert.Null(r.Error);
        var w = Assert.Single(r.Logs.OfLevel(LogLevel.Warning));
        Assert.Contains("other than 168", w.Message);
        Assert.Contains("DOM", w.Message);
    }

    [Fact]
    public async Task T68_No_completeness_warning_for_hist_or_young_estimated()
    {
        var hist = await Read("load_frcstd_hist", new[] { FeedSamples.Hist(At(D18, 3, 45), At(D18, 5)) }, FeedSamples.Unit(Feed("load_frcstd_hist"), D18, 5, PjmZone.Settled));
        Assert.Null(hist.Error);
        Assert.Empty(hist.Logs.OfLevel(LogLevel.Warning));

        for (var age = 1; age <= 10; age++)
        {
            var day = Today.AddDays(-age);
            var est = await Read("hrl_load_estimated", new[] { FeedSamples.Estimated(At(day, 0)) }, FeedSamples.Unit(Feed("hrl_load_estimated"), day, age));
            Assert.Null(est.Error);
            Assert.Empty(est.Logs.OfLevel(LogLevel.Warning));
        }

        // Age 11: the band is over, the partial day is reported.
        var old = Today.AddDays(-11);
        var est11 = await Read("hrl_load_estimated", new[] { FeedSamples.Estimated(At(old, 0)) }, FeedSamples.Unit(Feed("hrl_load_estimated"), old, 11));
        Assert.Single(est11.Logs.OfLevel(LogLevel.Warning));
    }

    // =========================================================================
    // 71: settings
    // =========================================================================

    [Fact]
    public void T71_VeryShort_settings_class_and_shipped()
    {
        Assert.Equal(28, new PjmSettings().VeryShortMaxDaysBack);
        Assert.Equal(25, new PjmSettings().VeryShortExpectRowsWithinDays);
        var pjm = AppSettingsPjm();
        Assert.Equal(28, pjm.GetProperty("VeryShortMaxDaysBack").GetInt32());
        Assert.Equal(25, pjm.GetProperty("VeryShortExpectRowsWithinDays").GetInt32());
        Assert.Empty(Shipped().Validate());
    }

    [Theory]
    [InlineData(28, 28)]
    [InlineData(29, 28)]
    public void T71_Validation_rejects_VeryShort_band_not_below_its_ceiling(int expect, int max)
    {
        var errors = new PjmSettings { VeryShortExpectRowsWithinDays = expect, VeryShortMaxDaysBack = max }.Validate();
        Assert.Single(errors, e => e.Contains("VeryShortExpectRowsWithinDays"));
    }

    [Fact]
    public void T71_Validation_rejects_EmptyOkThroughAge_not_below_depth()
    {
        // BackfillDays 10 → estimated depth 10 = its EmptyOkThroughAge 10.
        var errors = new PjmSettings { BackfillDays = 10 }.Validate();
        var e = Assert.Single(errors, x => x.Contains("EmptyOkThroughAge"));
        Assert.Contains("hrl_load_estimated", e);
    }

    [Fact]
    public void T71_Validation_rejects_effective_settle_beyond_very_short_retention()
    {
        // The ceiling makes very_short immune; with no ceiling the rule would bite at depth 28.
        var errors = new PjmSettings { SettledAfterDays = 28, DaysBack = 28, BackfillDays = 28 }.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("very_short_load_frcst") && e.Contains("effective SettledAfterDays"));
        Assert.Single(errors, e => e.Contains("hrl_load_metered") && e.Contains("effective SettledAfterDays"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(0)]
    public void T71_UnitHours_that_does_not_divide_24_is_refused(int unitHours)
    {
        var feed = PjmFeeds.LoadForecastVeryShort(S) with { UnitHours = unitHours };
        Assert.Throws<InvalidOperationException>(() => PjmWorkUnitProvider.Build(feed, S, Samples.Started));
        Assert.All(PjmFeeds.All(S), f => Assert.Equal(0, 24 % f.UnitHours));
    }
}

/// <summary>
/// The REAL <see cref="PjmModule.RegisterServices"/> wiring with all EIGHT pipelines rebuilt over
/// in-memory sinks (the shipped harness rebuilds only the original two).
/// </summary>
internal sealed class AllFeedsHarness : IDisposable
{
    public CapturingLoggerFactory Logs { get; } = new();
    public FakeLoadLog LoadLog { get; } = new();
    public ServiceProvider Services { get; private set; } = null!;
    public PjmModule Module { get; } = new();

    public static AllFeedsHarness Create(StubHttpMessageHandler data, Dictionary<string, string?>? config = null)
    {
        var h = new AllFeedsHarness();
        var values = new Dictionary<string, string?>
        {
            ["Loaders:PJM:ConnectionString"] = "Server=unused.invalid;Database=Misc;",
            ["Loaders:PJM:RetryDelayMs"] = "1",
            ["Loaders:PJM:HttpTimeoutSeconds"] = "5",
            ["Loaders:PJM:SettingsFetchTimeoutSeconds"] = "5",
            ["Loaders:PJM:RequestsPerMinute"] = "100000",
            ["Loaders:PJM:MaxConcurrentWorkUnits"] = "1",
        };
        if (config is not null)
            foreach (var (k, v) in config) values[k] = v;

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(h.Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<ISeeDbParamStore>(FakeParamStore.Returning(null));
        services.AddSingleton<ILoadLogRepository>(h.LoadLog);
        h.Module.RegisterServices(services, new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        services.AddHttpClient(PjmModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => data);
        services.AddHttpClient(PjmModule.SettingsHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k")));

        services.RemoveAll<IPjmPipeline>();
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.InstLoad, new PjmLoadRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.GenByFuel, new PjmFuelMixRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.LoadForecastVeryShort, new PjmLoadForecastVeryShortRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.LoadForecast7Day, new PjmLoadForecast7DayRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.LoadMetered, new PjmLoadMeteredRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.LoadPrelim, new PjmLoadPrelimRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.LoadForecastHist, new PjmLoadForecastHistRowMapper()));
        services.AddSingleton<IPjmPipeline>(sp => Build(sp, PjmFeeds.LoadEstimated, new PjmLoadEstimatedRowMapper()));

        h.Services = services.BuildServiceProvider();
        return h;
    }

    private static IPjmPipeline Build<TRow>(IServiceProvider sp, Func<PjmSettings, PjmFeed> feedFactory, IPjmRowMapper<TRow> mapper)
        where TRow : IPjmRow
    {
        var settings = sp.GetRequiredService<IOptions<PjmSettings>>().Value;
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var guard = sp.GetRequiredService<PjmRunGuard>();
        var feed = feedFactory(settings);
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(PjmModule.HttpClientName);
        var client = new PjmApiClient(http, settings, sp.GetRequiredService<IPjmKeyProvider>(), guard, lf.CreateLogger($"PJM.{feed.Name}.Http"));
        var reader = new PjmDayReader<TRow>(client, mapper, settings, lf.CreateLogger($"PJM.{feed.Name}.Source"));
        var provider = new PjmWorkUnitProvider(feed, settings, lf.CreateLogger($"PJM.{feed.Name}.Provider"));
        return new PjmPipeline<TRow>(feed.Name, provider, new PjmSourceReader<TRow>(reader, guard), new FakeSink<TRow>(),
            sp.GetRequiredService<ILoadLogRepository>(), settings, lf.CreateLogger($"PJM.{feed.Name}.Pipeline"));
    }

    public Task<LoaderRunResult> RunAsync(DateTime startedAtUtc) =>
        Module.RunAsync(Services, new LoaderRunContext { RunId = Guid.NewGuid(), StartedAtUtc = startedAtUtc, CancellationToken = CancellationToken.None });

    public void Dispose() => Services?.Dispose();
}
