using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Tests for the review fixes to the six-feed extension: snapshot runs first, empty very_short blocks
/// count once per UTC day, the single-page contract, the immutable-only settle ceiling, gzip
/// transport, and the shipped config.
/// </summary>
public sealed class ExtensionReviewFixTests
{
    private static readonly DateOnly Today = new(2026, 11, 23);
    private static PjmFeed Feed(string name) => PjmFeeds.All(new PjmSettings()).Single(f => f.Name == name);

    // ---- run guard: very_short empties count once per UTC day ---------------------

    private static async Task EmptyBlock(PjmRunGuard guard, int age, int blockStart)
    {
        var unit = FeedSamples.Unit(Feed("very_short_load_frcst"), Today.AddDays(-age), age, PjmZone.Settled, blockStart);
        var reader = new PjmSourceReader<PjmLoadForecastVeryShortRow>(
            FeedSamples.Reader(FeedSamples.Page(Array.Empty<JsonElement>()), new PjmLoadForecastVeryShortRowMapper(), new CapturingLoggerFactory()), guard);
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadAsync(unit, default));
        Assert.Equal(PjmFailureClass.EmptyPastDay, ex.FailureClass);
    }

    [Fact]
    public async Task Guard_three_empty_blocks_on_one_UTC_day_do_not_trip()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        foreach (var b in new[] { 18, 12, 6, 0 })
            await EmptyBlock(guard, 3, b);
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task Guard_empty_blocks_on_three_distinct_days_trip()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        await EmptyBlock(guard, 3, 6);
        await EmptyBlock(guard, 4, 6);
        Assert.False(guard.IsTripped);
        await EmptyBlock(guard, 5, 6);
        Assert.True(guard.IsTripped);
    }

    [Fact]
    public async Task Guard_a_repeat_on_the_same_day_neither_adds_to_nor_resets_the_streak()
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        await EmptyBlock(guard, 3, 18);   // streak 1 (day A)
        await EmptyBlock(guard, 4, 18);   // streak 2 (day B)
        await EmptyBlock(guard, 3, 12);   // day A again: no add (else 3 → trip) ...
        Assert.False(guard.IsTripped);
        await EmptyBlock(guard, 5, 18);   // ... and no reset (else this would be 1): day C → 3 → trip
        Assert.True(guard.IsTripped);
    }

    [Fact]
    public void Guard_count_once_scope_applies_to_sub_day_units_only()
    {
        Assert.Equal("very_short_load_frcst:20261120",
            PjmSourceReaderBase<PjmLoadForecastVeryShortRow>.CountOnceScope(FeedSamples.Unit(Feed("very_short_load_frcst"), Today.AddDays(-3), 3, PjmZone.Settled, 6)));
        Assert.Null(PjmSourceReaderBase<PjmLoadMeteredRow>.CountOnceScope(FeedSamples.Unit(Feed("hrl_load_metered"), Today.AddDays(-3), 3)));
        Assert.Null(PjmSourceReaderBase<PjmLoadForecast7DayRow>.CountOnceScope(FeedSamples.Snapshot(Feed("load_frcstd_7_day"), Samples.Started)));
    }

    // ---- single-page contract ------------------------------------------------------

    [Fact]
    public void Very_short_is_capped_at_one_page_of_43200_rows()
    {
        var vs = Feed("very_short_load_frcst");
        Assert.Equal(1, vs.MaxPagesPerUnit);
        Assert.Equal(43_200, vs.MaxRowsPerUnit);
        Assert.All(PjmFeeds.All(new PjmSettings()).Where(f => f.Name != vs.Name), f => Assert.Null(f.MaxPagesPerUnit));
    }

    [Fact]
    public async Task First_page_with_totalRows_beyond_one_page_fails_as_contract_before_a_second_page()
    {
        var unit = FeedSamples.Unit(Feed("very_short_load_frcst"), Today.AddDays(-3), 3, PjmZone.Settled, 6);
        var ev = unit.WindowStartUtc;
        var src = new FakePageSource((_, _) => new PjmPage(new[] { FeedSamples.VeryShort(ev, ev) }, 50_001));
        var reader = FeedSamples.Reader(src, new PjmLoadForecastVeryShortRowMapper(), new CapturingLoggerFactory());

        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(unit, default));
        Assert.Contains("totalRows 50001 exceeds 1 page(s) of 50000", ex.Message);
        Assert.Equal(new[] { 1 }, src.StartRows);
        Assert.Equal(PjmFailureClass.Contract, ex.FailureClass);

        // Exactly one full page is fine.
        var okSrc = FakePageSource.Paged(Enumerable.Range(0, 72).Select(i => FeedSamples.VeryShort(ev.AddMinutes(5 * i), ev.AddMinutes(5 * i))).ToList(), 50_000);
        Assert.Equal(72, (await FeedSamples.Reader(okSrc, new PjmLoadForecastVeryShortRowMapper(), new CapturingLoggerFactory()).ReadDayAsync(unit, default)).Count);
    }

    [Fact]
    public void Validate_rejects_PageSize_below_43200_only_while_very_short_is_enabled()
    {
        Assert.Single(new PjmSettings { PageSize = 43_199 }.Validate(), e => e.Contains("PageSize (43199)") && e.Contains("very_short_load_frcst"));
        Assert.DoesNotContain(new PjmSettings { PageSize = 43_200 }.Validate(), e => e.Contains("very_short_load_frcst"));
        Assert.DoesNotContain(new PjmSettings { PageSize = 1000, EnabledFeeds = new[] { "inst_load", "gen_by_fuel" } }.Validate(),
            e => e.Contains("very_short_load_frcst"));
    }

    // ---- immutable-only settle ceiling ---------------------------------------------

    [Fact]
    public void Shipped_ceilings_are_exactly_very_short_1_and_hist_2_and_only_on_immutable_feeds()
    {
        var feeds = PjmFeeds.All(new PjmSettings());
        Assert.Equal(new Dictionary<string, int?> { ["very_short_load_frcst"] = 1, ["load_frcstd_hist"] = 2 },
            feeds.Where(f => f.SettledAfterDaysCeiling is not null).ToDictionary(f => f.Name, f => f.SettledAfterDaysCeiling));
        Assert.Equal(new[] { "load_frcstd_hist", "very_short_load_frcst" },
            feeds.Where(f => f.IssuedForecastsImmutable).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(feeds, f => f.SettledAfterDaysCeiling is not null && f.MinSettledAfterDays is not null);
        Assert.DoesNotContain(feeds, f => f.SettledAfterDaysCeiling is not null && !f.IssuedForecastsImmutable);
        // The restating feeds keep floors, never ceilings.
        Assert.Null(Feed("hrl_load_metered").SettledAfterDaysCeiling);
        Assert.Null(Feed("hrl_load_estimated").SettledAfterDaysCeiling);
    }

    [Fact]
    public void Validate_has_ceiling_rules_and_shipped_descriptors_pass_them()
    {
        // Validate iterates PjmFeeds.All(this), so a hand-built bad descriptor cannot be injected (no
        // seam); this pins that the rules exist and that the shipped descriptors satisfy them.
        Assert.DoesNotContain(new PjmSettings().Validate(), e => e.Contains("SettledAfterDaysCeiling"));
        Assert.DoesNotContain(new PjmSettings { SettledAfterDays = 1 }.Validate(), e => e.Contains("SettledAfterDaysCeiling"));
    }

    // ---- gzip transport --------------------------------------------------------------

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw);
        return ms.ToArray();
    }

    private static byte[] GzipResponse(byte[] gz, int? sendBytes = null)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: {gz.Length}\r\nConnection: close\r\n\r\n");
        return head.Concat(gz.Take(sendBytes ?? gz.Length)).ToArray();
    }

    private static byte[] PrelimFixture() =>
        File.ReadAllBytes(Path.Combine(LiveReplayTests.Dir, "hrl_load_prelim.20261101.json"));

    [Fact]
    public async Task Gzip_live_fixture_body_decodes_and_reads_and_the_data_client_advertises_gzip_deflate()
    {
        var gz = Gzip(PrelimFixture());
        Assert.True(gz.Length < PrelimFixture().Length / 3);
        await using var server = new LoopbackHttpServer("127.0.0.1", (_, _) => GzipResponse(gz));
        var settings = Samples.Settings(s => { s.BaseUrl = server.BaseUrl + "/api/v1"; s.HttpTimeoutSeconds = 30; });
        var (client, _, _, _) = ClientFactory.Build(stub: null, new FakeKeyProvider("k"), settings);
        var feed = Feed("hrl_load_prelim");
        var unit = PjmWorkUnitProvider.Build(feed, settings, LiveReplayTests.PulledAtUtc).Single(u => u.Day == new DateOnly(2026, 11, 1));

        var rows = await new PjmDayReader<PjmLoadPrelimRow>(client, new PjmLoadPrelimRowMapper(), settings, ClientFactory.Null).ReadDayAsync(unit, default);

        Assert.Equal(240, rows.Count);
        var head = server.Requests.Single();
        Assert.Matches(@"(?im)^Accept-Encoding:.*\bgzip\b", head);
        Assert.Matches(@"(?im)^Accept-Encoding:.*\bdeflate\b", head);
    }

    [Fact]
    public async Task Gzip_settings_client_advertises_gzip_deflate()
    {
        await using var server = new LoopbackHttpServer("127.0.0.1", (_, _) => LoopbackHttpServer.Ok(Samples.SettingsJson("k")));
        var settings = Samples.Settings(s => s.SettingsUrl = server.BaseUrl + "/config/settings.json");
        using var http = ClientFactory.SettingsClient(null, settings);
        using var response = await http.GetAsync(settings.SettingsUrl);
        Assert.True(response.IsSuccessStatusCode);
        var head = server.Requests.Single();
        Assert.Matches(@"(?im)^Accept-Encoding:.*\bgzip\b", head);
        Assert.Matches(@"(?im)^Accept-Encoding:.*\bdeflate\b", head);
    }

    [Fact]
    public async Task Gzip_body_truncated_mid_stream_is_retried_as_transient()
    {
        var gz = Gzip(PrelimFixture());
        await using var server = new LoopbackHttpServer("127.0.0.1", (_, i) => i == 0 ? GzipResponse(gz, gz.Length / 2) : GzipResponse(gz));
        var settings = Samples.Settings(s => { s.BaseUrl = server.BaseUrl + "/api/v1"; s.HttpTimeoutSeconds = 30; });
        var (client, guard, _, _) = ClientFactory.Build(stub: null, new FakeKeyProvider("k"), settings);
        var unit = PjmWorkUnitProvider.Build(Feed("hrl_load_prelim"), settings, LiveReplayTests.PulledAtUtc).Single(u => u.Day == new DateOnly(2026, 11, 1));

        var page = await client.GetPageAsync(unit, 1, default);

        Assert.Equal(2, server.Count);       // the truncated attempt, then a clean retry
        Assert.Equal(240, page.TotalRows);
        Assert.Equal(240, page.Items.Count);
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task Gzip_body_truncated_on_every_attempt_fails_as_transient_exhausted()
    {
        var gz = Gzip(PrelimFixture());
        await using var server = new LoopbackHttpServer("127.0.0.1", (_, _) => GzipResponse(gz, gz.Length / 2));
        var settings = Samples.Settings(s => { s.BaseUrl = server.BaseUrl + "/api/v1"; s.HttpTimeoutSeconds = 30; });
        var (client, _, _, _) = ClientFactory.Build(stub: null, new FakeKeyProvider("k"), settings);
        var unit = PjmWorkUnitProvider.Build(Feed("hrl_load_prelim"), settings, LiveReplayTests.PulledAtUtc).Single(u => u.Day == new DateOnly(2026, 11, 1));

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(unit, 1, default));
        Assert.Equal(PjmFailureClass.TransientExhausted, ex.FailureClass);
        Assert.Equal(settings.RetryCount + 1, server.Count);
    }

    // ---- shipped config ---------------------------------------------------------------

    [Fact]
    public void Appsettings_ships_jitter_tolerance_10_and_EnabledFeeds_in_the_new_order()
    {
        var pjm = JsonDocument.Parse(File.ReadAllText(RepoPaths.AppSettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            .RootElement.GetProperty("Loaders").GetProperty("PJM");
        Assert.Equal(10, pjm.GetProperty("TimestampJitterToleranceSeconds").GetInt32());
        Assert.Equal(PjmFeeds.ExecutionOrder, pjm.GetProperty("EnabledFeeds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("load_frcstd_7_day", PjmFeeds.ExecutionOrder[0]);

        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(File.ReadAllBytes(RepoPaths.AppSettings))).Build();
        var s = new PjmSettings();
        config.GetSection("Loaders:PJM").Bind(s);
        Assert.Equal(10, s.TimestampJitterToleranceSeconds);
        Assert.Empty(s.Validate());
    }
}
