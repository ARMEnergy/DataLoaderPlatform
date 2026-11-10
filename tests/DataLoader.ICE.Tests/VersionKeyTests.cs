using System.Net;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// <see cref="IceHotKeyStrategy.LastModified"/>: a hot date is re-pulled exactly when
/// ICE's file version changes, checked by <c>HEAD</c>.
///
/// <para>
/// The incident this replaces (2026-11-09): the 15:03 Central run asked for that day's
/// gas file before ICE published it at 16:05, recorded it done with 0 rows under a UTC
/// <c>RunDate</c> key, and every run until the UTC date rolled at 18:00 Central skipped
/// it — the file loaded at 18:33. Separately, a clock-driven re-pull was served from the
/// local cache, so a file ICE re-published days later was never re-downloaded.
/// </para>
/// </summary>
public sealed class VersionKeyTests
{
    private static readonly IceFeedDescriptor Gas = IceDescriptors.Find("IceGas")!;

    // 2026-11-09 15:03 Central (CST, UTC-6) and 21:30 Central = 03:30 UTC the next day.
    private static readonly DateTime Afternoon = new(2026, 11, 9, 21, 3, 0, DateTimeKind.Utc);
    private static readonly DateTime Evening = new(2026, 11, 10, 3, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Nov9 = new(2026, 11, 9);

    private static IceSettings Shipped(Action<IceSettings>? configure = null) => TestHelpers.Settings(s =>
    {
        s.HotKeyStrategy = IceHotKeyStrategy.LastModified;
        s.VersionCheckDaysBack = 3;
        s.FullVersionCheckHoursCentral = new List<int> { 21 };
        configure?.Invoke(s);
    });

    private static LoaderRunContext Context(DateTime startedAtUtc, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = startedAtUtc,
        CancellationToken = CancellationToken.None
    };

    private static async Task<IReadOnlyList<IceWorkUnit>> Units(
        IceSettings settings, FakeProbe probe, DateTime startedAtUtc, Guid? runId = null) =>
        await new IceWorkUnitProvider(Gas, settings, probe, NullLogger.Instance)
            .GetWorkUnitsAsync(Context(startedAtUtc, runId));

    // ------------------------------------------------------------ window

    [Fact]
    public async Task An_ordinary_run_checks_only_the_last_three_days()
    {
        var probe = new FakeProbe(_ => IceFileVersion.Published("20261109170504"));

        var units = await Units(Shipped(), probe, Afternoon);

        // [today-3, today] inclusive = Fri 11-06 .. Mon 11-09: a Monday reaches Friday.
        Assert.Equal(new[] { new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 6) },
            units.Select(u => u.TradeDate));
        Assert.Equal(4, probe.Calls.Count);
    }

    [Fact]
    public async Task The_21_Central_run_checks_the_whole_window()
    {
        var probe = new FakeProbe(_ => IceFileVersion.Published("20261109170504"));

        var units = await Units(Shipped(), probe, Evening);

        // 21:30 Central on 11-09 is still trade date 11-09 in Central: 31 dates, all probed.
        Assert.Equal(31, units.Count);
        Assert.Equal(Nov9, units[0].TradeDate);
        Assert.Equal(31, probe.Calls.Count);
    }

    [Fact]
    public void Full_check_hour_is_matched_in_Central_not_UTC()
    {
        var settings = Shipped();

        Assert.False(IceWorkUnitProvider.IsFullVersionCheck(settings, Afternoon)); // 15:03 CST
        Assert.True(IceWorkUnitProvider.IsFullVersionCheck(settings, Evening));    // 21:30 CST
        // 21:00 UTC is 15:00 CST — must NOT count as the 21:00 hour.
        Assert.False(IceWorkUnitProvider.IsFullVersionCheck(settings, new DateTime(2026, 11, 9, 21, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void An_empty_hour_list_never_runs_a_full_check()
    {
        var settings = Shipped(s => s.FullVersionCheckHoursCentral = new List<int>());

        Assert.False(IceWorkUnitProvider.IsFullVersionCheck(settings, Evening));
        Assert.Equal(3, IceWorkUnitProvider.VersionCheckDays(settings, Evening));
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(45, 30)]
    public void VersionCheckDaysBack_is_clamped_to_the_window(int configured, int expected)
    {
        var settings = Shipped(s => s.VersionCheckDaysBack = configured);
        Assert.Equal(expected, IceWorkUnitProvider.VersionCheckDays(settings, Afternoon));
    }

    [Fact]
    public async Task Settled_dates_keep_their_stable_key_and_are_never_probed()
    {
        var probe = new FakeProbe(_ => IceFileVersion.Published("20261109170504"));
        var settings = Shipped(s => { s.DaysBack = 10; s.SettledAfterDays = 5; });

        var units = await Units(settings, probe, Afternoon);

        // ages 0..3 checked by version; 4..5 hot but outside the check window (not
        // enumerated); 6..10 settled (enumerated, stable key, no HEAD).
        Assert.Equal(4 + 5, units.Count);
        Assert.Equal(4, probe.Calls.Count);
        Assert.All(units.Where(u => !u.IsHot), u => Assert.Equal($"ice:IceGas:{IceTime.Iso(u.TradeDate)}", u.Key));
        Assert.DoesNotContain(probe.Calls, d => Nov9.DayNumber - d.DayNumber > 3);
    }

    [Fact]
    public async Task LastModified_without_a_probe_fails_loudly()
    {
        var provider = new IceWorkUnitProvider(Gas, Shipped(), NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetWorkUnitsAsync(Context(Afternoon)));
    }

    [Fact]
    public async Task Clock_strategies_ignore_the_probe_and_keep_the_full_window()
    {
        var probe = new FakeProbe(_ => throw new InvalidOperationException("must not be called"));
        var settings = TestHelpers.Settings(s => s.HotKeyStrategy = IceHotKeyStrategy.RunDate);

        var units = await Units(settings, probe, Afternoon);

        Assert.Equal(31, units.Count);
        Assert.Empty(probe.Calls);
        Assert.All(units, u => Assert.Contains(":run=", u.Key));
    }

    // ------------------------------------------------------------ keys

    [Fact]
    public async Task Same_version_gives_the_same_key_so_the_next_run_skips()
    {
        var probe = new FakeProbe(_ => IceFileVersion.Published("20261109170504"));

        var first = (await Units(Shipped(), probe, Afternoon, Guid.NewGuid()))[0];
        var later = (await Units(Shipped(), probe, Afternoon.AddMinutes(30), Guid.NewGuid()))[0];

        Assert.Equal("ice:IceGas:2026-11-09:lm=20261109170504", first.Key);
        Assert.Equal(first.Key, later.Key);
        Assert.Equal("20261109170504", first.ExpectedVersion);
    }

    [Fact]
    public async Task A_republished_file_gets_a_new_key()
    {
        var original = (await Units(Shipped(), new FakeProbe(_ => IceFileVersion.Published("20260930172000")), Afternoon))[0];
        var republished = (await Units(Shipped(), new FakeProbe(_ => IceFileVersion.Published("20261008104807")), Afternoon))[0];

        Assert.NotEqual(original.Key, republished.Key);
    }

    /// <summary>
    /// The incident, end to end at the key level: at 15:03 there is no file, at 16:33
    /// there is — and the key must change so the 16:33 run loads it.
    /// </summary>
    [Fact]
    public async Task A_file_published_after_an_empty_read_gets_a_new_key_the_next_run()
    {
        var at1503 = (await Units(Shipped(), new FakeProbe(_ => IceFileVersion.Absent), Afternoon, Guid.NewGuid()))[0];
        var at1533 = (await Units(Shipped(), new FakeProbe(_ => IceFileVersion.Absent), Afternoon.AddMinutes(30), Guid.NewGuid()))[0];
        var at1633 = (await Units(Shipped(), new FakeProbe(_ => IceFileVersion.Published("20261109170504")), Afternoon.AddMinutes(90), Guid.NewGuid()))[0];

        Assert.Equal("ice:IceGas:2026-11-09:lm=none", at1503.Key);
        Assert.Equal(at1503.Key, at1533.Key);       // confirmed missing once; not re-downloaded
        Assert.NotEqual(at1503.Key, at1633.Key);    // published → loads
        Assert.Null(at1503.ExpectedVersion);
    }

    [Fact]
    public async Task A_failed_HEAD_always_downloads()
    {
        var probe = new FakeProbe(_ => IceFileVersion.Unknown);

        var one = (await Units(Shipped(), probe, Afternoon, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")))[0];
        var two = (await Units(Shipped(), probe, Afternoon, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002")))[0];

        Assert.StartsWith("ice:IceGas:2026-11-09:lm=unknown:run=", one.Key);
        Assert.NotEqual(one.Key, two.Key);
        Assert.Null(one.ExpectedVersion);
        Assert.True(one.BypassCache);
    }

    [Fact]
    public async Task Keys_stay_unique_within_a_full_run()
    {
        var units = await Units(Shipped(), new FakeProbe(d => IceFileVersion.Published($"{d:yyyyMMdd}170000")), Evening);
        Assert.Equal(units.Count, units.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Version_token_is_the_header_digits_not_a_converted_instant()
    {
        // ICE says "GMT" but means Eastern; the token must reproduce the header as-is.
        var header = DateTimeOffset.Parse("Mon, 09 Nov 2026 17:05:04 GMT", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("20261109170504", IceFileVersion.Format(header));
    }

    // ------------------------------------------------------------ probe

    [Fact]
    public async Task Probe_reads_Last_Modified_as_the_version()
    {
        var handler = new HeadHandler(_ => Published("Mon, 09 Nov 2026 17:05:04 GMT"));
        var version = await Probe(handler).ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(IceFileVersion.Published("20261109170504"), version);
        Assert.Equal(HttpMethod.Head, handler.Requests.Single().Method);
        Assert.EndsWith("/Settlement_Reports_CSV/Gas/icecleared_gas_2026_11_09.dat", handler.Requests.Single().Uri);
        Assert.Equal("iceSsoCookie=token-0", handler.Requests.Single().Cookie);
    }

    [Fact]
    public async Task Probe_reports_Absent_when_ICE_sends_no_Last_Modified()
    {
        // Verified live: an unpublished file answers 200 text/html with no Last-Modified.
        var version = await Probe(new HeadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        })).ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(IceFileVersion.Absent, version);
    }

    [Fact]
    public async Task Probe_refreshes_the_token_once_on_a_sign_on_redirect()
    {
        var auth = new FakeAuthenticator();
        var handler = new HeadHandler(request => request.Cookie == "iceSsoCookie=token-0"
            ? new HttpResponseMessage(HttpStatusCode.Found)
            : Published("Mon, 09 Nov 2026 17:05:04 GMT"));

        var version = await Probe(handler, auth).ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(1, auth.RefreshCount);
        Assert.Equal(IceFileVersionKind.Published, version.Kind);
    }

    /// <summary>
    /// Still bounced after a refresh: Unknown, never Absent. Absent can be recorded as
    /// "confirmed missing" and skipped, which would hide a published file.
    /// </summary>
    [Fact]
    public async Task Probe_never_reports_Absent_when_it_cannot_authenticate()
    {
        var version = await Probe(new HeadHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)))
            .ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(IceFileVersion.Unknown, version);
    }

    [Fact]
    public void A_followed_redirect_to_another_host_counts_as_a_sign_on_bounce()
    {
        var original = new HttpRequestMessage(HttpMethod.Head, "https://downloads.ice.com/x.dat");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Head, "https://sso.ice.com/appUserLogin")
        };

        Assert.True(IceHeadVersionProbe.IsAuthRedirect(original, response));

        response.RequestMessage = new HttpRequestMessage(HttpMethod.Head, "https://downloads.ice.com/x.dat");
        Assert.False(IceHeadVersionProbe.IsAuthRedirect(original, response));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Probe_reports_Unknown_on_a_non_success_status(HttpStatusCode status)
    {
        var version = await Probe(new HeadHandler(_ => new HttpResponseMessage(status)))
            .ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(IceFileVersion.Unknown, version);
    }

    [Fact]
    public async Task Probe_reports_Unknown_on_a_transport_failure()
    {
        var version = await Probe(new HeadHandler(_ => throw new HttpRequestException("connection reset")))
            .ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(IceFileVersion.Unknown, version);
    }

    /// <summary>A file-shaped answer without a usable Last-Modified must never read as "not published".</summary>
    [Fact]
    public void A_file_shaped_answer_without_a_usable_Last_Modified_is_Unknown_not_Absent()
    {
        var attachment = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        attachment.Content.Headers.ContentDisposition =
            System.Net.Http.Headers.ContentDispositionHeaderValue.Parse("attachment; filename=icecleared_gas_2026_11_09.dat");
        Assert.Equal(IceFileVersion.Unknown, IceHeadVersionProbe.Interpret(attachment));

        var garbled = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        garbled.Content.Headers.TryAddWithoutValidation("Last-Modified", "not a date");
        Assert.Equal(IceFileVersion.Unknown, IceHeadVersionProbe.Interpret(garbled));
    }

    [Fact]
    public async Task Probe_follows_a_cross_host_redirect_as_a_sign_on_bounce()
    {
        // What the real redirect-following client surfaces: a 200 whose final URI is sso.ice.com.
        var auth = new FakeAuthenticator();
        var handler = new HeadHandler(request => request.Cookie == "iceSsoCookie=token-0"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
                RequestMessage = new HttpRequestMessage(HttpMethod.Head, "https://sso.ice.com/appUserLogin")
            }
            : Published("Mon, 09 Nov 2026 17:05:04 GMT"));

        var version = await Probe(handler, auth).ProbeAsync(Gas, Nov9, CancellationToken.None);

        Assert.Equal(1, auth.RefreshCount);
        Assert.Equal(IceFileVersion.Published("20261109170504"), version);
    }

    [Fact]
    public async Task A_HEAD_timeout_is_Unknown_but_caller_cancellation_propagates()
    {
        var hang = new HangingHandler();
        var probe = new IceHeadVersionProbe(new HttpClient(hang), new FakeAuthenticator(),
            Options.Create(TestHelpers.Settings()), NullLogger.Instance, TimeSpan.FromMilliseconds(50));

        Assert.Equal(IceFileVersion.Unknown, await probe.ProbeAsync(Gas, Nov9, CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var slow = new IceHeadVersionProbe(new HttpClient(hang), new FakeAuthenticator(),
            Options.Create(TestHelpers.Settings()), NullLogger.Instance, TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.ProbeAsync(Gas, Nov9, cts.Token));
    }

    /// <summary>A failed sign-on fails the feed rather than retrying SSO once per date.</summary>
    [Fact]
    public async Task A_failed_sign_on_propagates_instead_of_becoming_Unknown()
    {
        var probe = new IceHeadVersionProbe(new HttpClient(new HeadHandler(_ => Published("Mon, 09 Nov 2026 17:05:04 GMT"))),
            new FailingAuthenticator(), Options.Create(TestHelpers.Settings()), NullLogger.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => probe.ProbeAsync(Gas, Nov9, CancellationToken.None));
    }

    [Fact]
    public async Task The_breaker_stops_sending_HEADs_after_repeated_failures_and_a_success_resets_it()
    {
        var failing = true;
        var handler = new HeadHandler(_ => failing
            ? throw new HttpRequestException("down")
            : Published("Mon, 09 Nov 2026 17:05:04 GMT"));
        var probe = Probe(handler);

        for (var i = 0; i < IceHeadVersionProbe.BreakerThreshold - 1; i++)
            await probe.ProbeAsync(Gas, Nov9, CancellationToken.None);
        failing = false;
        Assert.Equal(IceFileVersionKind.Published, (await probe.ProbeAsync(Gas, Nov9, CancellationToken.None)).Kind);
        Assert.False(probe.BreakerOpen); // the success reset the count

        failing = true;
        for (var i = 0; i < IceHeadVersionProbe.BreakerThreshold; i++)
            await probe.ProbeAsync(Gas, Nov9, CancellationToken.None);
        Assert.True(probe.BreakerOpen);

        var sent = handler.Requests.Count;
        Assert.Equal(IceFileVersion.Unknown, await probe.ProbeAsync(Gas, Nov9, CancellationToken.None));
        Assert.Equal(sent, handler.Requests.Count); // no request once open
    }

    // ------------------------------------------------------------ cache

    [Fact]
    public async Task Cache_serves_a_copy_only_when_its_version_matches()
    {
        var cache = TestHelpers.Cache(TestHelpers.Settings());
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), "20261109170504", CancellationToken.None);

        Assert.NotNull(await cache.TryReadAsync(Gas, Nov9, "20261109170504", CancellationToken.None));
        Assert.Null(await cache.TryReadAsync(Gas, Nov9, "20261110093000", CancellationToken.None));
        Assert.NotNull(await cache.TryReadAsync(Gas, Nov9, expectedVersion: null, CancellationToken.None));
    }

    /// <summary>Every file cached before this change has no sidecar: one re-download, never a stale merge.</summary>
    [Fact]
    public async Task An_untracked_cached_copy_does_not_satisfy_a_version()
    {
        var cache = TestHelpers.Cache(TestHelpers.Settings());
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), CancellationToken.None);

        Assert.Null(await cache.TryReadAsync(Gas, Nov9, "20261109170504", CancellationToken.None));
        Assert.NotNull(await cache.TryReadAsync(Gas, Nov9, CancellationToken.None));
    }

    [Fact]
    public async Task Saving_without_a_version_removes_a_stale_sidecar()
    {
        var cache = TestHelpers.Cache(TestHelpers.Settings());
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), "20261109170504", CancellationToken.None);
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), version: null, CancellationToken.None);

        Assert.False(File.Exists(IceFileCache.VersionPathFor(cache.PathFor(Gas, Nov9))));
        Assert.Null(await cache.TryReadAsync(Gas, Nov9, "20261109170504", CancellationToken.None));
    }

    // ------------------------------------------------------------ reader

    /// <summary>
    /// A stale cached copy is refused, the file is downloaded, and the sidecar records
    /// the version ICE actually SERVED — so a file that changed between HEAD and GET
    /// is not downloaded a second time by the next run.
    /// </summary>
    [Fact]
    public async Task Reader_redownloads_a_changed_file_and_records_the_served_version()
    {
        var settings = TestHelpers.Settings();
        var cache = TestHelpers.Cache(settings);
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), "20261109170504", CancellationToken.None);

        var handler = new HeadHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestHelpers.Bytes(Samples.IceGas)) };
            response.Content.Headers.LastModified = DateTimeOffset.Parse("Tue, 10 Nov 2026 09:31:00 GMT", System.Globalization.CultureInfo.InvariantCulture);
            return response;
        });

        var reader = new IceSourceReader(Gas, new HttpClient(handler), new FakeAuthenticator(), cache,
            new FakeFileLog(), Options.Create(settings), NullLogger.Instance);

        var unit = new IceWorkUnit { Feed = Gas, TradeDate = Nov9, KeySuffix = ":lm=20261110093000", ExpectedVersion = "20261110093000" };
        var rows = await reader.ReadAsync(unit, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("20261110093100", File.ReadAllText(IceFileCache.VersionPathFor(cache.PathFor(Gas, Nov9))));
    }

    [Fact]
    public async Task Reader_uses_a_matching_cached_copy_without_any_request()
    {
        var settings = TestHelpers.Settings();
        var cache = TestHelpers.Cache(settings);
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), "20261109170504", CancellationToken.None);

        var handler = new HeadHandler(_ => throw new InvalidOperationException("must not download"));
        var reader = new IceSourceReader(Gas, new HttpClient(handler), new FakeAuthenticator(), cache,
            new FakeFileLog(), Options.Create(settings), NullLogger.Instance);

        var unit = new IceWorkUnit { Feed = Gas, TradeDate = Nov9, KeySuffix = ":lm=20261109170504", ExpectedVersion = "20261109170504" };

        Assert.Equal(2, (await reader.ReadAsync(unit, CancellationToken.None)).Count);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The HEAD said version X exists; the GET got "No Files Available". Recording
    /// <c>:lm=X</c> as a clean empty load would hide X for good — it must FAIL so the
    /// same key is retried.
    /// </summary>
    [Fact]
    public async Task A_versioned_unit_whose_download_is_missing_fails_instead_of_recording_empty()
    {
        var settings = TestHelpers.Settings();
        var fileLog = new FakeFileLog();
        var handler = new HeadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestHelpers.Bytes(Samples.NoFilesHtml))
        });
        var reader = new IceSourceReader(Gas, new HttpClient(handler), new FakeAuthenticator(),
            TestHelpers.Cache(settings), fileLog, Options.Create(settings), NullLogger.Instance);

        var unit = new IceWorkUnit { Feed = Gas, TradeDate = Nov9, KeySuffix = ":lm=20261109170504", ExpectedVersion = "20261109170504" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(unit, CancellationToken.None));
        Assert.Equal("Failed", fileLog.Calls.Single().Status);
    }

    [Fact]
    public async Task An_unversioned_missing_file_is_still_a_clean_empty_read()
    {
        var settings = TestHelpers.Settings();
        var reader = new IceSourceReader(Gas,
            new HttpClient(new HeadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TestHelpers.Bytes(Samples.NoFilesHtml))
            })),
            new FakeAuthenticator(), TestHelpers.Cache(settings), new FakeFileLog(), Options.Create(settings), NullLogger.Instance);

        var unit = new IceWorkUnit { Feed = Gas, TradeDate = Nov9, KeySuffix = ":lm=none" };

        Assert.Empty(await reader.ReadAsync(unit, CancellationToken.None));
    }

    /// <summary>A failed HEAD must download even when a cached copy exists — it may be a replaced version.</summary>
    [Fact]
    public async Task A_unit_whose_HEAD_failed_ignores_the_cache()
    {
        var settings = TestHelpers.Settings();
        var cache = TestHelpers.Cache(settings);
        await cache.SaveAsync(Gas, Nov9, TestHelpers.Bytes(Samples.IceGas), CancellationToken.None);

        var handler = new HeadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestHelpers.Bytes(Samples.IceGas))
        });
        var reader = new IceSourceReader(Gas, new HttpClient(handler), new FakeAuthenticator(), cache,
            new FakeFileLog(), Options.Create(settings), NullLogger.Instance);

        var unit = new IceWorkUnitProvider(Gas, settings, NullLogger.Instance).UnitFor(Nov9, IceFileVersion.Unknown, Guid.NewGuid());
        await reader.ReadAsync(unit, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    // ------------------------------------------------------------ shipped config

    /// <summary>
    /// Binds the REAL <c>src/DataLoader.Host/appsettings.json</c> through the real
    /// binder. The hour list must come out exactly <c>[21]</c>: the binder appends to a
    /// pre-populated list, which is why the class default is empty.
    /// </summary>
    [Fact]
    public void Shipped_config_checks_three_days_and_a_full_window_at_21_Central()
    {
        var settings = new IceSettings();
        new ConfigurationBuilder().AddJsonFile(HostAppSettings(), optional: false).Build()
            .GetSection("Loaders:ICE").Bind(settings);

        Assert.Equal(IceHotKeyStrategy.LastModified, settings.HotKeyStrategy);
        Assert.Equal(3, settings.VersionCheckDaysBack);
        Assert.Equal(new[] { 21 }, settings.FullVersionCheckHoursCentral);
    }

    // ------------------------------------------------------------ helpers

    private static IceHeadVersionProbe Probe(HeadHandler handler, FakeAuthenticator? auth = null) =>
        new(new HttpClient(handler), auth ?? new FakeAuthenticator(), Options.Create(TestHelpers.Settings()), NullLogger.Instance);

    private static HttpResponseMessage Published(string lastModified)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        response.Content.Headers.LastModified = DateTimeOffset.Parse(lastModified, System.Globalization.CultureInfo.InvariantCulture);
        return response;
    }

    private static string HostAppSettings()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DataLoader.Host", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate src/DataLoader.Host/appsettings.json above the test output.");
    }

    private sealed class FakeProbe : IIceVersionProbe
    {
        private readonly Func<DateOnly, IceFileVersion> _answer;
        public FakeProbe(Func<DateOnly, IceFileVersion> answer) => _answer = answer;
        public List<DateOnly> Calls { get; } = new();

        public Task<IceFileVersion> ProbeAsync(IceFeedDescriptor feed, DateOnly tradeDate, CancellationToken ct)
        {
            Calls.Add(tradeDate);
            return Task.FromResult(_answer(tradeDate));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FailingAuthenticator : IIceAuthenticator
    {
        public Task<string> GetTokenAsync(CancellationToken ct) =>
            throw new HttpRequestException("ICE SSO sign-on failed with HTTP 401 Unauthorized.");

        public Task<string> RefreshAsync(string staleToken, CancellationToken ct) => GetTokenAsync(ct);
    }

    private sealed record SeenRequest(HttpMethod Method, string Uri, string? Cookie);

    private sealed class HeadHandler : HttpMessageHandler
    {
        private readonly Func<SeenRequest, HttpResponseMessage> _respond;
        public HeadHandler(Func<SeenRequest, HttpResponseMessage> respond) => _respond = respond;
        public List<SeenRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var seen = new SeenRequest(request.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null);
            Requests.Add(seen);

            var response = _respond(seen);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
