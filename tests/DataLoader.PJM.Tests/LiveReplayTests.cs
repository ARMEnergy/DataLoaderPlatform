using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// REAL-DATA REPLAY. The fixtures under <c>Fixtures/live/</c> are live PJM Data Miner 2 responses
/// (public data, user-approved pull on 2026-11-23): UTC day 2026-11-01 (the DST fall-back day) and
/// 2026-11-18, plus one <c>load_frcstd_7_day</c> snapshot and every feed's <c>/metadata</c>. Files
/// above ~300 KB are gzipped; items are byte-for-byte as PJM sent them (trimmed only where noted in
/// each file's <c>source</c>). Each fixture is served over a stubbed wire through the REAL
/// <see cref="PjmApiClient"/> (real handler chain), the REAL mapper and <see cref="PjmDayReader{TRow}"/>,
/// for the work unit the provider itself builds at the pull time — so a PJM column, timestamp or
/// key change shows up here as a failing test, offline.
/// </summary>
[Collection(ExtraFieldCollection.Name)]
public sealed class LiveReplayTests
{
    /// <summary>When the live pull was made (17:02-17:08 Central): the run start every unit is built for.</summary>
    internal static readonly DateTime PulledAtUtc = new(2026, 11, 23, 23, 8, 0, DateTimeKind.Utc);

    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly DateOnly Nov18 = new(2026, 11, 18);

    internal static string Dir => Path.Combine(RepoPaths.Root, "tests", "DataLoader.PJM.Tests", "Fixtures", "live");

    internal static PjmSettings ReplaySettings() => Samples.Settings(s => s.HttpTimeoutSeconds = 120);

    /// <summary>Reads a fixture (plain or <c>.gz</c>) and returns its document root.</summary>
    internal static JsonElement LoadDoc(string path)
    {
        if (!File.Exists(path) && File.Exists(path + ".gz")) path += ".gz";
        using var fs = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    internal static List<JsonElement> Items(JsonElement doc) => doc.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();

    /// <summary>Serves <paramref name="items"/> by <c>startRow</c>/<c>rowCount</c>, with the true <c>totalRows</c>.</summary>
    internal static StubHttpMessageHandler Serve(IReadOnlyList<JsonElement> items) =>
        new((req, _) =>
        {
            var q = Query(req.RequestUri!);
            var start = int.Parse(q["startRow"], CultureInfo.InvariantCulture);
            var count = int.Parse(q["rowCount"], CultureInfo.InvariantCulture);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                Samples.Envelope(items.Skip(start - 1).Take(count), totalRows: items.Count, nextLink: null));
        });

    internal static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

    internal static PjmDayWorkUnit UnitFor(PjmFeed feed, DateOnly day, int blockStartHour = 0) =>
        PjmWorkUnitProvider.Build(feed, ReplaySettings(), PulledAtUtc).Single(u => u.Day == day && u.BlockStartHour == blockStartHour);

    internal sealed record Replay(IReadOnlyList<IPjmRow> Rows, CapturingLoggerFactory Logs, StubHttpMessageHandler Wire);

    internal static async Task<Replay> ReadAsync<TRow>(IPjmRowMapper<TRow> mapper, PjmDayWorkUnit unit, StubHttpMessageHandler wire)
        where TRow : IPjmRow
    {
        var settings = ReplaySettings();
        var (client, _, _, _) = ClientFactory.Build(wire, new FakeKeyProvider("replay-key"), settings);
        var logs = new CapturingLoggerFactory();
        var reader = new PjmDayReader<TRow>(client, mapper, settings, logs.CreateLogger("replay"), FeedSamples.Clock(PulledAtUtc));
        var rows = await reader.ReadDayAsync(unit, CancellationToken.None);
        return new Replay(rows.Cast<IPjmRow>().ToList(), logs, wire);
    }

    internal static Task<Replay> ReadFeedAsync(string feedName, PjmDayWorkUnit unit, StubHttpMessageHandler wire) => feedName switch
    {
        PjmFeeds.InstLoadName => ReadAsync(new PjmLoadRowMapper(), unit, wire),
        PjmFeeds.GenByFuelName => ReadAsync(new PjmFuelMixRowMapper(), unit, wire),
        PjmFeeds.LoadMeteredName => ReadAsync(new PjmLoadMeteredRowMapper(), unit, wire),
        PjmFeeds.LoadPrelimName => ReadAsync(new PjmLoadPrelimRowMapper(), unit, wire),
        PjmFeeds.LoadEstimatedName => ReadAsync(new PjmLoadEstimatedRowMapper(), unit, wire),
        PjmFeeds.LoadForecastHistName => ReadAsync(new PjmLoadForecastHistRowMapper(), unit, wire),
        PjmFeeds.LoadForecastVeryShortName => ReadAsync(new PjmLoadForecastVeryShortRowMapper(), unit, wire),
        PjmFeeds.LoadForecast7DayName => ReadAsync(new PjmLoadForecast7DayRowMapper(), unit, wire),
        _ => throw new ArgumentOutOfRangeException(nameof(feedName)),
    };

    internal static void AssertKeysUniqueAsSql(IReadOnlyList<IPjmRow> rows) =>
        Assert.Equal(rows.Count, rows.Select(r => (r.Key.K1, r.Key.K2, r.Key.Member.TrimEnd(' ').ToUpperInvariant())).Distinct().Count());

    private static PjmFeed Feed(string name) => PjmFeeds.All(ReplaySettings()).Single(f => f.Name == name);

    // =========================================================================
    // Per-feed day replay
    // =========================================================================

    [Theory]
    [InlineData("gen_by_fuel", "gen_by_fuel.20261101.json", "2026-11-01", 230)]
    [InlineData("hrl_load_metered", "hrl_load_metered.20261101.json", "2026-11-01", 720)]
    [InlineData("hrl_load_metered", "hrl_load_metered.20261118.json", "2026-11-18", 720)]
    [InlineData("hrl_load_prelim", "hrl_load_prelim.20261101.json", "2026-11-01", 240)]
    [InlineData("hrl_load_prelim", "hrl_load_prelim.20261118.json", "2026-11-18", 240)]
    [InlineData("hrl_load_estimated", "hrl_load_estimated.20261101.json", "2026-11-01", 240)]
    [InlineData("hrl_load_estimated", "hrl_load_estimated.20261118.json", "2026-11-18", 50)]   // partial at age 5: accepted
    [InlineData("load_frcstd_hist", "load_frcstd_hist.20261101.json", "2026-11-01", 1991)]
    [InlineData("load_frcstd_hist", "load_frcstd_hist.20261118.json", "2026-11-18", 1980)]
    [InlineData("inst_load", "inst_load.20261101.trimmed.json", "2026-11-01", 4 * 288)]
    public async Task Replay_real_day_loads_with_zero_failures_unique_keys_and_no_warnings(string feedName, string file, string dayText, int expectedRows)
    {
        var day = DateOnly.ParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var feed = Feed(feedName);
        var doc = LoadDoc(Path.Combine(Dir, file));
        var items = Items(doc);
        Assert.Equal(expectedRows, items.Count);

        // Every real item carries exactly the descriptor's known field set (required present, nothing unknown).
        foreach (var item in items)
        {
            var names = item.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Superset(feed.RequiredFields.ToHashSet(StringComparer.OrdinalIgnoreCase), names);
            Assert.All(names, n => Assert.True(feed.IsKnownField(n), $"{feedName}: unexpected live field '{n}'"));
        }

        var unit = UnitFor(feed, day);
        var replay = await ReadFeedAsync(feedName, unit, Serve(items));

        Assert.Equal(expectedRows, replay.Rows.Count);
        AssertKeysUniqueAsSql(replay.Rows);
        Assert.Empty(replay.Logs.OfLevel(LogLevel.Warning));
        Assert.Equal(1, replay.Wire.Count);   // one page per unit

        // Every row echoes the requested UTC day on the feed's FilterField.
        Assert.All(replay.Rows, r => Assert.Equal(day, DateOnly.FromDateTime(r.EchoUtc)));

        // The loader asks exactly what the live pull asked (filter column and value as PJM echoed them).
        var sent = Query(replay.Wire.Requests.Single().Uri);
        Assert.Equal(feed.SortField, sent["sort"]);
        if (doc.TryGetProperty("searchSpecification", out var spec))
        {
            var liveFilter = spec.GetProperty("filters").EnumerateArray().Single().EnumerateObject().Single();
            Assert.Equal(liveFilter.Name, feed.FilterField);
            Assert.Equal(liveFilter.Value.GetString(), sent[feed.FilterField!]);
        }
    }

    // ---- feed-specific facts on the real data -------------------------------------

    [Fact]
    public async Task Replay_gen_by_fuel_fall_back_day_has_23_hours_without_UTC_0600_and_no_warning()
    {
        var items = Items(LoadDoc(Path.Combine(Dir, "gen_by_fuel.20261101.json")));
        var r = await ReadFeedAsync("gen_by_fuel", UnitFor(Feed("gen_by_fuel"), Nov1), Serve(items));
        var hours = r.Rows.Select(x => x.SlotUtc).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(23, hours.Count);
        Assert.DoesNotContain(Nov1.ToDateTime(new TimeOnly(6, 0)), hours);
        Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task Replay_inst_load_keeps_all_288_slots_per_area_and_truncates_jitter_seconds_to_the_slot_start()
    {
        var items = Items(LoadDoc(Path.Combine(Dir, "inst_load.20261101.trimmed.json")));
        var jittered = items.Where(i => !i.GetProperty("datetime_beginning_utc").GetString()!.EndsWith(":00", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(jittered);   // the real :01/:02-second rows are in the fixture
        Assert.All(jittered, i => Assert.Matches(@":0[12]$", i.GetProperty("datetime_beginning_utc").GetString()!));

        var r = await ReadFeedAsync("inst_load", UnitFor(Feed("inst_load"), Nov1), Serve(items));
        var rows = r.Rows.Cast<PjmLoadRow>().ToList();

        foreach (var g in rows.GroupBy(x => x.Area))
        {
            Assert.Equal(288, g.Count());
            Assert.Equal(288, g.Select(x => x.DatetimeBeginningUtc).Distinct().Count());
        }
        Assert.All(rows, x =>
        {
            Assert.Equal(0, x.DatetimeBeginningUtc.Second);
            Assert.Equal(0, x.DatetimeBeginningUtc.Minute % 5);
            Assert.Equal(0, x.DatetimeBeginningEpt.Second);
        });

        // Each jittered source row maps to the slot start of its own minute.
        var raw = items.Select(i => (Area: i.GetProperty("area").GetString()!, Utc: DateTime.ParseExact(i.GetProperty("datetime_beginning_utc").GetString()!, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))).ToList();
        for (var i = 0; i < raw.Count; i++)
        {
            var expected = raw[i].Utc.AddSeconds(-raw[i].Utc.Second);
            Assert.Equal((raw[i].Area, expected), (rows[i].Area, rows[i].DatetimeBeginningUtc));
        }
    }

    [Fact]
    public async Task Replay_hrl_fall_back_day_keeps_24_UTC_hours_and_the_repeated_EPT_hour_passes()
    {
        var metered = await ReadFeedAsync("hrl_load_metered", UnitFor(Feed("hrl_load_metered"), Nov1),
            Serve(Items(LoadDoc(Path.Combine(Dir, "hrl_load_metered.20261101.json")))));
        var rows = metered.Rows.Cast<PjmLoadMeteredRow>().ToList();
        foreach (var g in rows.GroupBy(x => x.LoadArea))
        {
            Assert.Equal(24, g.Select(x => x.DatetimeBeginningUtc).Distinct().Count());
            Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0), g.Single(x => x.DatetimeBeginningUtc.Hour == 5).DatetimeBeginningEpt);   // EDT
            Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0), g.Single(x => x.DatetimeBeginningUtc.Hour == 6).DatetimeBeginningEpt);   // EST
        }

        // prelim: EPT ending − beginning is 0 min on the fall-back hour (10 rows), UTC is 60 min on every row.
        var prelim = await ReadFeedAsync("hrl_load_prelim", UnitFor(Feed("hrl_load_prelim"), Nov1),
            Serve(Items(LoadDoc(Path.Combine(Dir, "hrl_load_prelim.20261101.json")))));
        var p = prelim.Rows.Cast<PjmLoadPrelimRow>().ToList();
        Assert.All(p, x => Assert.Equal(TimeSpan.FromHours(1), x.DatetimeEndingUtc - x.DatetimeBeginningUtc));
        Assert.Equal(10, p.Count(x => x.DatetimeEndingEpt == x.DatetimeBeginningEpt));
    }

    [Fact]
    public async Task Replay_hist_evaluations_sit_on_minute_45_and_forecast_hours_on_the_hour()
    {
        foreach (var (file, day) in new[] { ("load_frcstd_hist.20261101.json", Nov1), ("load_frcstd_hist.20261118.json", Nov18) })
        {
            var r = await ReadFeedAsync("load_frcstd_hist", UnitFor(Feed("load_frcstd_hist"), day), Serve(Items(LoadDoc(Path.Combine(Dir, file)))));
            var rows = r.Rows.Cast<PjmLoadForecastHistRow>().ToList();
            Assert.All(rows, x => Assert.Equal((45, 0), (x.EvaluatedAtUtc.Minute, x.EvaluatedAtUtc.Second)));
            Assert.All(rows, x => Assert.Equal((0, 0), (x.ForecastHourBeginningUtc.Minute, x.ForecastHourBeginningUtc.Second)));
            // (forecast_hour, area) alone is NOT a key: several evaluations forecast the same hour.
            Assert.True(rows.Select(x => (x.ForecastHourBeginningUtc, x.ForecastArea)).Distinct().Count() < rows.Count);
        }
    }

    [Fact]
    public async Task Replay_very_short_fall_back_block_is_43200_rows_in_one_page()
    {
        var doc = LoadDoc(Path.Combine(Dir, "very_short_load_frcst.20261101T00.json"));
        var items = Items(doc);
        var feed = Feed("very_short_load_frcst");
        var unit = UnitFor(feed, Nov1, 0);
        Assert.Equal(PjmZone.Settled, unit.Zone);

        var r = await ReadFeedAsync("very_short_load_frcst", unit, Serve(items));
        var rows = r.Rows.Cast<PjmLoadForecastVeryShortRow>().ToList();

        Assert.Equal(43_200, rows.Count);
        Assert.Equal(1, r.Wire.Count);
        Assert.Equal(new[] { "1" }, r.Wire.Requests.Select(q => Query(q.Uri)["startRow"]));
        Assert.Equal("11/01/2026 00:00to11/01/2026 05:59", Query(r.Wire.Requests.Single().Uri)["evaluated_at_utc"]);
        AssertKeysUniqueAsSql(r.Rows);
        Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));

        Assert.Equal(25, rows.Select(x => x.ForecastArea).Distinct().Count());
        Assert.All(rows.GroupBy(x => x.ForecastArea), g => Assert.Equal(72, g.Select(x => x.EvaluatedAtUtc).Distinct().Count()));
        // V-3: 24 steps per evaluation at evaluated_at + 0..115 min.
        Assert.All(rows, x => Assert.InRange((x.ForecastDatetimeBeginningUtc - x.EvaluatedAtUtc).TotalMinutes, 0, 115));
        // V-2: UTC end − begin is 5 min on every row; EPT shows −55 min across the fall-back (600 rows: 24 evaluations × 25 areas).
        Assert.All(rows, x => Assert.Equal(TimeSpan.FromMinutes(5), x.ForecastDatetimeEndingUtc - x.ForecastDatetimeBeginningUtc));
        Assert.Equal(600, rows.Count(x => x.ForecastDatetimeEndingEpt - x.ForecastDatetimeBeginningEpt == TimeSpan.FromMinutes(-55)));
    }

    [Fact]
    public async Task Replay_7_day_snapshot_is_4200_rows_one_evaluation_with_its_real_seconds_and_no_filter()
    {
        var items = Items(LoadDoc(Path.Combine(Dir, "load_frcstd_7_day.snap.json")));
        var feed = Feed("load_frcstd_7_day");
        var unit = PjmWorkUnitProvider.Build(feed, ReplaySettings(), PulledAtUtc).Single();
        Assert.Equal("pjm:load_frcstd_7_day:pub=202611232245", unit.Key);

        var r = await ReadFeedAsync("load_frcstd_7_day", unit, Serve(items));
        var rows = r.Rows.Cast<PjmLoadForecast7DayRow>().ToList();

        Assert.Equal(4200, rows.Count);
        AssertKeysUniqueAsSql(r.Rows);
        Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
        var evaluated = Assert.Single(rows.Select(x => x.EvaluatedAtDatetimeUtc).Distinct());
        Assert.Equal(new DateTime(2026, 11, 23, 22, 47, 12), evaluated);                       // seconds kept exact
        Assert.Equal(new DateTime(2026, 11, 23, 17, 47, 12), rows[0].EvaluatedAtDatetimeEpt);
        Assert.All(rows.GroupBy(x => x.ForecastArea), g => Assert.Equal(168, g.Count()));
        Assert.All(rows, x => Assert.Equal(TimeSpan.FromHours(1), x.ForecastDatetimeEndingUtc - x.ForecastDatetimeBeginningUtc));

        var sent = Query(r.Wire.Requests.Single().Uri);
        Assert.Equal(new[] { "order", "rowCount", "sort", "startRow" }, sent.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("forecast_datetime_beginning_utc", sent["sort"]);
    }

    // =========================================================================
    // Metadata: any PJM column change fails a test
    // =========================================================================

    public static IEnumerable<object[]> AllFeeds() => PjmFeeds.ExecutionOrder.Select(f => new object[] { f });

    private static (string Name, int Ordinal, bool Sortable, bool Filterable, bool Active)[] MetadataColumns(string feed) =>
        LoadDoc(Path.Combine(Dir, "metadata", feed + ".metadata.json")).GetProperty("columns").EnumerateArray()
            .Select(c => (c.GetProperty("fieldName").GetString()!, c.GetProperty("ordinalPosition").GetInt32(),
                Flag(c, "enableSorting"), Flag(c, "enableFiltering"),
                !c.TryGetProperty("isActive", out var a) || a.GetString() == "True"))
            .OrderBy(c => c.Item2).ToArray();

    /// <summary>PJM omits a flag rather than sending "False" (e.g. gen_by_fuel.mw has no enableSorting): absent = false.</summary>
    private static bool Flag(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.GetString() == "True";

    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Metadata_field_list_equals_the_mapper_field_set(string feedName)
    {
        var feed = Feed(feedName);
        var meta = MetadataColumns(feedName);
        Assert.All(meta, c => Assert.True(c.Active, $"{feedName}.{c.Name} is inactive in metadata"));
        var expected = feed.RequiredFields.Concat(feed.OptionalFields).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, meta.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Metadata_order_equals_the_TVP_column_order(string feedName)
    {
        var tvpType = feedName switch
        {
            "inst_load" => "dbo.PjmLoadType",
            "gen_by_fuel" => "dbo.PjmFuelMixType",
            "hrl_load_metered" => "dbo.PjmLoadMeteredType",
            "hrl_load_prelim" => "dbo.PjmLoadPrelimType",
            "hrl_load_estimated" => "dbo.PjmLoadEstimatedType",
            "load_frcstd_hist" => "dbo.PjmLoadForecastHistType",
            "very_short_load_frcst" => "dbo.PjmLoadForecastVeryShortType",
            "load_frcstd_7_day" => "dbo.PjmLoadForecast7DayType",
            _ => throw new ArgumentOutOfRangeException(nameof(feedName)),
        };
        var tvp = TvpParser.Parse(TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql("002_CreatePjmTvpTypes.sql"))), tvpType);
        Assert.Equal(MetadataColumns(feedName).Select(c => c.Name), tvp.Select(c => c.Name));
    }

    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Metadata_marks_the_sort_column_sortable_and_the_filter_column_filterable(string feedName)
    {
        var feed = Feed(feedName);
        var meta = MetadataColumns(feedName).ToDictionary(c => c.Name);
        Assert.True(meta[feed.SortField].Sortable, $"{feedName}: sort column {feed.SortField} is not sortable (V-5)");
        if (feed.FilterField is { } filter)
            Assert.True(meta[filter].Filterable, $"{feedName}: filter column {filter} is not filterable");
        else
            Assert.False(meta["evaluated_at_datetime_utc"].Sortable);   // why the snapshot is unfiltered (V-5)
    }

    // =========================================================================
    // Opt-in: the FULL very_short day (4 pages, 172,800 rows) from the raw pull
    // =========================================================================

    /// <summary>
    /// Skipped unless <c>PJM_LIVE_REPLAY_DIR</c> points at the raw pull's <c>keys</c> folder
    /// (<c>very_short_load_frcst.dst.p1..p4.json</c>, ~62 MB; not committed). Replays the day twice:
    /// (a) as the loader really reads it, four 6-hour block units of one page each; (b) the four
    /// original pages verbatim through explicit startRow paging on a day-sized unit.
    /// </summary>
    [LiveReplayFact]
    public async Task OptIn_full_very_short_day_replays_172800_rows()
    {
        var dir = Environment.GetEnvironmentVariable(LiveReplayFactAttribute.EnvVar)!;
        foreach (var (tag, day) in new[] { ("dst", Nov1), ("norm", Nov18) })
        {
            var pages = Enumerable.Range(1, 4).Select(p => Path.Combine(dir, $"very_short_load_frcst.{tag}.p{p}.json")).ToList();
            if (!pages.All(File.Exists))
            {
                Assert.True(tag != "dst", $"missing {pages[0]} … p4 under {dir}");
                continue;
            }

            var all = pages.SelectMany(p => Items(LoadDoc(p))).ToList();
            Assert.Equal(172_800, all.Count);
            var feed = Feed("very_short_load_frcst");

            // (a) four block units, exactly as enumerated at the pull time.
            var blockRows = new List<IPjmRow>();
            foreach (var start in new[] { 0, 6, 12, 18 })
            {
                var lo = Samples.Iso(day.ToDateTime(new TimeOnly(start, 0)));
                var hi = Samples.Iso(day.ToDateTime(new TimeOnly(start + 5, 59, 59)));
                var block = all.Where(i => string.CompareOrdinal(i.GetProperty("evaluated_at_utc").GetString(), lo) >= 0 &&
                                           string.CompareOrdinal(i.GetProperty("evaluated_at_utc").GetString(), hi) <= 0).ToList();
                var r = await ReadFeedAsync("very_short_load_frcst", UnitFor(feed, day, start), Serve(block));
                Assert.Equal(43_200, r.Rows.Count);
                Assert.Equal(1, r.Wire.Count);
                Assert.Empty(r.Logs.OfLevel(LogLevel.Warning));
                blockRows.AddRange(r.Rows);
            }
            Assert.Equal(172_800, blockRows.Count);
            AssertKeysUniqueAsSql(blockRows);

            // (b) the raw pages verbatim, startRow 1/50001/100001/150001 on a day-sized unit.
            var raw = pages.Select(File.ReadAllBytes).ToList();
            var wire = new StubHttpMessageHandler((req, _) =>
            {
                var idx = (int.Parse(Query(req.RequestUri!)["startRow"], CultureInfo.InvariantCulture) - 1) / 50_000;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(raw[idx]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } },
                };
            });
            var age = PulledAtUtc.Day - day.Day;

            // The shipped single-page cap refuses a whole real day before requesting page 2.
            var capped = FeedSamples.Unit(feed with { UnitHours = 24 }, day, age, PjmZone.Settled, 0, PulledAtUtc);
            var refused = await Assert.ThrowsAsync<PjmContractException>(() => ReadFeedAsync("very_short_load_frcst", capped, wire));
            Assert.Contains("totalRows 172800 exceeds 1 page(s)", refused.Message);
            Assert.Equal(1, wire.Count);

            // With the cap lifted, the four raw pages reconcile through explicit startRow paging.
            wire.Requests.Clear();
            var dayFeed = feed with { UnitHours = 24, MaxPagesPerUnit = null, MaxRowsPerUnit = null };
            var dayUnit = FeedSamples.Unit(dayFeed, day, age, PjmZone.Settled, 0, PulledAtUtc);
            var paged = await ReadFeedAsync("very_short_load_frcst", dayUnit, wire);
            Assert.Equal(172_800, paged.Rows.Count);
            Assert.Equal(new[] { "1", "50001", "100001", "150001" }, paged.Wire.Requests.OrderBy(q => q.Index).Select(q => Query(q.Uri)["startRow"]));
            AssertKeysUniqueAsSql(paged.Rows);
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips unless <c>PJM_LIVE_REPLAY_DIR</c> is set to an existing directory.</summary>
public sealed class LiveReplayFactAttribute : FactAttribute
{
    public const string EnvVar = "PJM_LIVE_REPLAY_DIR";

    public LiveReplayFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            Skip = $"Opt-in live replay: set {EnvVar} to the folder holding very_short_load_frcst.dst.p1..p4.json";
    }
}
