using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Tests for the PJM code-review fixes: no redirects, the retry → limiter → timeout → buffering
/// handler order, body buffering inside the per-attempt timeout, the single-attempt post-401
/// re-send and <see cref="PjmSettings.WorstCaseUnitSeconds"/>, the empty-past-day trip class, the
/// interval-boundary check, paging by items received, the monotonic limiter, the Retry-After cap
/// trip, and the new startup validation rules.
/// </summary>
public sealed class ReviewFixTests
{
    private const string Key = "CANARY-RF-KEY-5b6c7d8e9f";
    private const string New = "CANARY-RF-NEW-0a1b2c3d4e";
    private static readonly DateOnly Day = new(2026, 11, 20);

    private static PjmDayWorkUnit Gen(int age = 3) => ClientFactory.GenUnit(Day, age);
    private static PjmDayWorkUnit Ld(int age = 3) => ClientFactory.LoadUnit(Day, age);
    private static DateTime At(int h, int m = 0, int s = 0) => Day.ToDateTime(new TimeOnly(h, m, s));
    private static string FuelDayJson(int hours = 24) => Samples.Envelope(Samples.FuelDay(Day, hours: hours), nextLink: null);

    // =====================================================================================
    // 1. Redirects are never followed (both named clients), through the REAL primary handler.
    // =====================================================================================

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task R1_Data_client_does_not_follow_a_redirect_to_another_host(int status)
    {
        await using var target = new LoopbackHttpServer("127.0.0.2", (_, _) => LoopbackHttpServer.Ok(FuelDayJson()));
        var location = $"{target.BaseUrl}/steal/gen_by_fuel?subscription-key={Key}&x=1";
        await using var origin = new LoopbackHttpServer("127.0.0.1", (_, _) =>
            LoopbackHttpServer.Raw($"HTTP/1.1 {status} Moved\nLocation: {location}\nContent-Length: 0\nConnection: close"));

        var settings = Samples.Settings(s => s.BaseUrl = origin.BaseUrl + "/api/v1");
        var (client, guard, _, logs) = ClientFactory.Build(stub: null, new FakeKeyProvider(Key), settings);

        var ex = await Assert.ThrowsAsync<PjmContractException>(() => client.GetPageAsync(Gen(), 1, CancellationToken.None));

        Assert.Equal(1, origin.Count);               // exactly one request, not retried
        Assert.Equal(0, target.Count);               // the Location host never saw anything — no key header there
        Assert.Contains($"{PjmApiClient.KeyHeader}: {Key}", origin.Requests.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PjmFailureClass.Contract, ex.FailureClass);
        Assert.Contains($"HTTP {status}", ex.Message);
        Assert.DoesNotContain("?", ex.Message);       // no query from the Location
        Assert.DoesNotContain("subscription-key", ex.Message);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Key);
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task R1_Data_client_redirect_counts_toward_the_contract_trip_through_the_source_reader()
    {
        var stub = new StubHttpMessageHandler((_, _) =>
        {
            var r = StubHttpMessageHandler.Json(HttpStatusCode.Found, "");
            r.Headers.Location = new Uri($"https://elsewhere.example.com/x?key={Key}");
            return r;
        });
        var (client, guard, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        var source = new PjmFuelMixSourceReader(
            new PjmDayReader<PjmFuelMixRow>(client, new PjmFuelMixRowMapper(), Samples.Settings(), ClientFactory.Null), guard);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<PjmContractException>(() => source.ReadAsync(Gen(), CancellationToken.None));

        Assert.Equal(3, stub.Count);   // one request per unit, never retried
        Assert.True(guard.IsTripped);
    }

    [Fact]
    public async Task R1_Settings_client_does_not_follow_a_redirect_and_never_quotes_it()
    {
        await using var target = new LoopbackHttpServer("127.0.0.2", (_, _) => LoopbackHttpServer.Ok(Samples.SettingsJson(New)));
        await using var origin = new LoopbackHttpServer("127.0.0.1", (_, _) =>
            LoopbackHttpServer.Raw($"HTTP/1.1 302 Found\nLocation: {target.BaseUrl}/config/settings.json?k={Key}\nContent-Length: 0\nConnection: close"));

        var settings = Samples.Settings(s => s.SettingsUrl = origin.BaseUrl + "/config/settings.json");
        var http = ClientFactory.SettingsClient(stub: null, settings);
        var logs = new CapturingLoggerFactory();
        var provider = new PjmKeyProvider(settings, http, FakeParamStore.Returning(null), logs.CreateLogger("PJM.Key"));

        var ex = await Assert.ThrowsAsync<PjmKeyAcquisitionException>(() => provider.InitializeAsync(CancellationToken.None));

        Assert.Equal(1, origin.Count);   // 3xx is not retried
        Assert.Equal(0, target.Count);   // never followed: the key in the target's body was never fetched
        Assert.Contains("302", ex.Message);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Key, New);
    }

    // =====================================================================================
    // 2. Handler order: a limiter wait longer than the per-attempt timeout is NOT a timeout.
    // =====================================================================================

    [Fact]
    public async Task R2_Limiter_wait_longer_than_the_per_attempt_timeout_does_not_time_out()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 23, 14, 20, 0, TimeSpan.Zero));
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, FuelDayJson());
        var settings = Samples.Settings(s => s.HttpTimeoutSeconds = 1);
        var (client, _, limiter, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key), settings, time: clock, requestsPerMinute: 1);

        await limiter.WaitAsync(CancellationToken.None);   // use up the one slot: the next send must wait ~60s (fake)

        var page = client.GetPageAsync(Gen(), 1, CancellationToken.None);
        for (var i = 0; i < 200 && clock.PendingTimers == 0; i++) await Task.Delay(10);
        Assert.True(clock.PendingTimers > 0, "the request never parked in the limiter");

        await Task.Delay(TimeSpan.FromSeconds(2.5));        // > HttpTimeoutSeconds of REAL time inside the limiter
        Assert.False(page.IsCompleted, page.Exception?.ToString());
        Assert.Equal(0, stub.Count);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("HTTP retry"));

        clock.Advance(PjmRateLimiter.Window + PjmRateLimiter.EdgeMargin);
        var result = await page.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(24, result.Items.Count);
        Assert.Equal(1, stub.Count);
        Assert.Equal(2, limiter.AdmittedCount);
    }

    // =====================================================================================
    // 3. PjmBufferingHandler: stalled and truncated bodies are retried inside the attempt budget.
    // =====================================================================================

    /// <summary>Headers arrive at once; the body then stalls until cancelled.</summary>
    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"items\":["), cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    /// <summary>Writes part of the body, then the connection "resets".</summary>
    private sealed class ResettingContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"items\":[{\"datetime_beginning_utc\":\"" + Key));
            throw new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed " + Key);
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    [Fact]
    public async Task R3_A_stalled_body_times_out_and_is_retried()
    {
        var stub = new StubHttpMessageHandler((_, i) => i == 0
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() }
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, FuelDayJson()));
        var settings = Samples.Settings(s => s.HttpTimeoutSeconds = 1);
        var (client, _, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key), settings);

        var sw = Stopwatch.StartNew();
        var page = await client.GetPageAsync(Gen(), 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(24, page.Items.Count);
        Assert.Equal(2, stub.Count);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(0.9));
        Assert.Contains(logs.Entries, e => e.Message.Contains("per-attempt budget"));
    }

    [Fact]
    public async Task R3_A_body_that_stalls_on_every_attempt_fails_as_transient_exhausted()
    {
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() });
        var settings = Samples.Settings(s => { s.HttpTimeoutSeconds = 1; s.RetryCount = 1; });
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key), settings);

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Gen(), 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(PjmFailureClass.TransientExhausted, ex.FailureClass);
        Assert.IsType<TimeoutRejectedException>(ex.InnerException);
        Assert.Equal(2, stub.Count);
    }

    [Fact]
    public async Task R3_A_body_stream_exception_is_retried_as_transient_and_never_quoted()
    {
        var stub = new StubHttpMessageHandler((_, i) => i == 0
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ResettingContent() }
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, FuelDayJson()));
        var (client, _, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var page = await client.GetPageAsync(Gen(), 1, CancellationToken.None);

        Assert.Equal(24, page.Items.Count);
        Assert.Equal(2, stub.Count);
        Assert.Contains(logs.Entries, e => e.Message.Contains("HttpRequestException"));
        Secrets.AssertAbsent(logs.AllText(), Key);
    }

    [Fact]
    public async Task R3_A_stream_exception_on_every_attempt_fails_without_quoting_the_partial_body()
    {
        var stub = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ResettingContent() });
        var (client, _, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Gen(), 1, CancellationToken.None));
        Assert.Equal(PjmFailureClass.TransientExhausted, ex.FailureClass);
        Assert.Equal(4, stub.Count);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Key);
    }

    [Fact]
    public async Task R3_Content_length_mismatch_on_the_wire_is_retried_and_the_retry_returns_full_rows()
    {
        var full = FuelDayJson();
        await using var server = new LoopbackHttpServer("127.0.0.1", (_, i) => i == 0
            ? LoopbackHttpServer.Raw($"HTTP/1.1 200 OK\nContent-Type: application/json\nContent-Length: {full.Length + 5000}\nConnection: close",
                full[..(full.Length / 2)])                    // promises more than it sends, then closes
            : LoopbackHttpServer.Ok(full));

        var settings = Samples.Settings(s => s.BaseUrl = server.BaseUrl + "/api/v1");
        var (client, _, _, _) = ClientFactory.Build(stub: null, new FakeKeyProvider(Key), settings);
        var reader = new PjmDayReader<PjmFuelMixRow>(client, new PjmFuelMixRowMapper(), settings, ClientFactory.Null);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(24, rows.Count);
        Assert.Equal(2, server.Count);
        Assert.Equal(Enumerable.Range(0, 24).Select(h => At(h)), rows.Select(r => r.DatetimeBeginningUtc));
    }

    // =====================================================================================
    // 4. The post-401 re-send is ONE attempt; WorstCaseUnitSeconds is pinned.
    // =====================================================================================

    [Fact]
    public async Task R4_401_refetch_then_503_on_the_resend_fails_without_a_retry_cycle()
    {
        var stub = new StubHttpMessageHandler((req, _) =>
            req.Headers.GetValues(PjmApiClient.KeyHeader).Single() == Key
                ? StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "")
                : StubHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, ""));
        var keys = new FakeKeyProvider(Key, refreshTo: New);
        var (client, guard, limiter, logs) = ClientFactory.Build(stub, keys);

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Gen(), 1, CancellationToken.None));

        Assert.Equal(new[] { Key, New }, stub.Requests.OrderBy(r => r.Index).Select(r => r.KeyHeader));   // exactly two sends
        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal(2, limiter.AdmittedCount);   // the re-send is still paced
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("HTTP retry"));
        Assert.Equal(1, keys.RefreshCalls);
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task R4_A_normal_first_send_still_gets_the_full_retry_cycle()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Gen(), 1, CancellationToken.None));
        Assert.Equal(4, stub.Count);
    }

    private static PjmSettings Shipped()
    {
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(File.ReadAllBytes(RepoPaths.AppSettings))).Build();
        var s = new PjmSettings();
        config.GetSection("Loaders:PJM").Bind(s);
        return s;
    }

    [Fact]
    public void R4_WorstCaseUnitSeconds_fits_under_WorkUnitTimeoutSeconds_for_the_shipped_appsettings()
    {
        var s = Shipped();
        // Independent recomputation of design §10.2 with the shipped values:
        //   read cycle 4 × (60 + 60×2/5) + (60+60+60) = 516; re-send 84; settings re-fetch 4×30 + (2+4+8) = 134.
        Assert.Equal(734, s.WorstCaseUnitSeconds(), 3);
        Assert.True(s.WorstCaseUnitSeconds() < s.WorkUnitTimeoutSeconds);
        Assert.Empty(s.Validate());
    }

    [Theory]
    [InlineData(734)]   // equal is not "fits"
    [InlineData(600)]
    public void R4_Validate_rejects_a_WorkUnitTimeoutSeconds_below_the_worst_case(int timeout)
    {
        var s = Shipped();
        s.WorkUnitTimeoutSeconds = timeout;
        Assert.Contains(s.Validate(), e => e.Contains("WorkUnitTimeoutSeconds"));
    }

    [Fact]
    public void R4_Validate_rejects_a_retry_budget_that_outgrows_the_unit_budget()
    {
        var s = Shipped();
        s.RetryCount = 8;
        Assert.True(s.WorstCaseUnitSeconds() >= s.WorkUnitTimeoutSeconds);
        Assert.Contains(s.Validate(), e => e.Contains("WorkUnitTimeoutSeconds"));
    }

    // =====================================================================================
    // 5. Run guard: the empty-past-day class trips; echo/duplicate/boundary reset the streak.
    // =====================================================================================

    private static (PjmFuelMixSourceReader Source, PjmRunGuard Guard) Source(IPjmPageSource pages)
    {
        var guard = new PjmRunGuard(3, ClientFactory.Null);
        return (new PjmFuelMixSourceReader(new PjmDayReader<PjmFuelMixRow>(pages, new PjmFuelMixRowMapper(), Samples.Settings(), ClientFactory.Null), guard), guard);
    }

    private static PjmPage EmptyPage() => new(new List<JsonElement>(), 0);
    private static PjmPage PageOf(params JsonElement[] items) => new(items.ToList(), items.Length);

    [Fact]
    public async Task R5_Three_consecutive_empty_past_days_trip_the_guard()
    {
        var (source, guard) = Source(new FakePageSource((_, _) => EmptyPage()));

        for (var i = 0; i < 2; i++)
        {
            var e = await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Gen(), CancellationToken.None));
            Assert.Equal(PjmFailureClass.EmptyPastDay, e.FailureClass);
            Assert.False(guard.IsTripped);
        }
        await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Gen(), CancellationToken.None));
        Assert.True(guard.IsTripped);
        Assert.Contains(PjmFailureClass.EmptyPastDay, guard.Reason);
    }

    [Fact]
    public async Task R5_Empty_today_is_success_and_resets_the_streak()
    {
        var (source, guard) = Source(new FakePageSource((_, _) => EmptyPage()));
        await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Gen(), CancellationToken.None));
        await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Gen(), CancellationToken.None));
        Assert.Empty(await source.ReadAsync(Gen(age: 0), CancellationToken.None));
        await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Gen(), CancellationToken.None));
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task R5_Echo_duplicate_and_boundary_failures_reset_the_empty_day_streak()
    {
        var echo = PageOf(Samples.Fuel(Day.AddDays(1).ToDateTime(new TimeOnly(2, 0))));
        var dup = PageOf(Samples.Fuel(At(2)), Samples.Fuel(At(2), "GAS"));
        var boundary = PageOf(Samples.Fuel(At(2, 30)));
        var script = new[] { EmptyPage(), EmptyPage(), echo, EmptyPage(), EmptyPage(), dup, EmptyPage(), EmptyPage(), boundary, EmptyPage(), EmptyPage() };
        var call = 0;
        var (source, guard) = Source(new FakePageSource((_, _) => script[call++]));

        foreach (var _ in script)
        {
            var ex = await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Gen(), CancellationToken.None));
            Assert.False(guard.IsTripped, $"tripped after: {ex.Message}");
        }
    }

    // =====================================================================================
    // 6. Interval-boundary check. Seconds are posting JITTER (LIVE-VERIFIED 2026-11-23: inst_load
    //    00:15:01, 23:55:01), so they are truncated, not failed — only the MINUTE must be on the
    //    boundary, and seconds beyond TimestampJitterToleranceSeconds (10) fail.
    // =====================================================================================

    [Theory]
    [InlineData(5, 3, 0)]
    [InlineData(23, 59, 0)]
    [InlineData(23, 59, 1)]
    public async Task R6_inst_load_row_off_the_5_minute_boundary_fails(int h, int m, int s)
    {
        var reader = new PjmDayReader<PjmLoadRow>(FakePageSource.Paged(new[] { Samples.Load(At(h, m, s)) }, 50000),
            new PjmLoadRowMapper(), Samples.Settings(), ClientFactory.Null);
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Ld(), CancellationToken.None));
        Assert.Contains("5-minute boundary", ex.Message);
    }

    [Theory]
    [InlineData(14, 5, 0)]
    [InlineData(14, 55, 1)]
    [InlineData(14, 55, 0)]
    public async Task R6_gen_by_fuel_row_off_the_hour_fails(int h, int m, int s)
    {
        var reader = new PjmDayReader<PjmFuelMixRow>(FakePageSource.Paged(new[] { Samples.Fuel(At(h, m, s)) }, 50000),
            new PjmFuelMixRowMapper(), Samples.Settings(), ClientFactory.Null);
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("60-minute boundary", ex.Message);
    }

    [Theory]
    [InlineData(0, 15, 1)]
    [InlineData(23, 55, 1)]
    [InlineData(5, 0, 2)]
    [InlineData(5, 0, 10)]
    public async Task R6_inst_load_seconds_jitter_is_truncated_to_the_slot_start(int h, int m, int s)
    {
        var reader = new PjmDayReader<PjmLoadRow>(FakePageSource.Paged(new[] { Samples.Load(At(h, m, s)) }, 50000),
            new PjmLoadRowMapper(), Samples.Settings(), ClientFactory.Null);
        var row = Assert.Single(await reader.ReadDayAsync(Ld(age: 0), CancellationToken.None));
        Assert.Equal(At(h, m), row.DatetimeBeginningUtc);
        Assert.Equal(PjmTime.ToEastern(At(h, m)), row.DatetimeBeginningEpt);
    }

    [Fact]
    public async Task R6_seconds_beyond_the_jitter_tolerance_fail()
    {
        var reader = new PjmDayReader<PjmLoadRow>(FakePageSource.Paged(new[] { Samples.Load(At(5, 0, 30)) }, 50000),
            new PjmLoadRowMapper(), Samples.Settings(), ClientFactory.Null);
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Ld(), CancellationToken.None));
        Assert.Contains("jitter tolerance", ex.Message);
    }

    [Fact]
    public async Task R6_two_rows_in_one_slot_with_different_seconds_are_a_duplicate()
    {
        var reader = new PjmDayReader<PjmLoadRow>(
            FakePageSource.Paged(new[] { Samples.Load(At(5, 0, 0)), Samples.Load(At(5, 0, 1)) }, 50000),
            new PjmLoadRowMapper(), Samples.Settings(), ClientFactory.Null);
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Ld(age: 0), CancellationToken.None));
        Assert.Contains("duplicate key", ex.Message);
    }

    [Fact]
    public void R6_fall_back_day_has_exactly_one_repeated_eastern_hour()
    {
        Assert.Equal(1, PjmTime.RepeatedEasternHours(new DateOnly(2026, 11, 1)));
        Assert.Equal(0, PjmTime.RepeatedEasternHours(new DateOnly(2026, 11, 2)));
        Assert.Equal(0, PjmTime.RepeatedEasternHours(new DateOnly(2026, 3, 8)));
    }

    [Fact]
    public async Task R6_On_boundary_rows_pass_including_the_last_interval_and_hour()
    {
        var load = new PjmDayReader<PjmLoadRow>(
            FakePageSource.Paged(new[] { Samples.Load(At(0)), Samples.Load(At(5, 5)), Samples.Load(At(23, 55)) }, 50000),
            new PjmLoadRowMapper(), Samples.Settings(), ClientFactory.Null);
        Assert.Equal(3, (await load.ReadDayAsync(Ld(age: 0), CancellationToken.None)).Count);

        var fuel = new PjmDayReader<PjmFuelMixRow>(
            FakePageSource.Paged(new[] { Samples.Fuel(At(0)), Samples.Fuel(At(14)), Samples.Fuel(At(23)) }, 50000),
            new PjmFuelMixRowMapper(), Samples.Settings(), ClientFactory.Null);
        Assert.Equal(3, (await fuel.ReadDayAsync(Gen(age: 0), CancellationToken.None)).Count);
    }

    // =====================================================================================
    // 7. Paging advances startRow by items RECEIVED.
    // =====================================================================================

    [Fact]
    public async Task R7_Server_capping_pages_below_PageSize_still_reads_everything()
    {
        var items = Samples.FuelDay(Day, hours: 10);
        var src = new FakePageSource((startRow, _) => new PjmPage(items.Skip(startRow - 1).Take(3).ToList(), 10));   // caps at 3
        var reader = new PjmDayReader<PjmFuelMixRow>(src, new PjmFuelMixRowMapper(), Samples.Settings(s => s.PageSize = 50000), ClientFactory.Null);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);

        Assert.Equal(10, rows.Count);
        Assert.Equal(new[] { 1, 4, 7, 10 }, src.StartRows);
        Assert.Equal(10, rows.Select(r => r.DatetimeBeginningUtc).Distinct().Count());
    }

    [Fact]
    public async Task R7_Capped_pages_through_the_wire_use_received_counts_for_startRow()
    {
        var items = Samples.FuelDay(Day, hours: 7);
        var stub = new StubHttpMessageHandler((req, _) =>
        {
            var start = Samples.Parse(req.RequestUri!).StartRow;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, Samples.Envelope(items.Skip(start - 1).Take(2), totalRows: 7, nextLink: null));
        });
        var settings = Samples.Settings(s => s.PageSize = 5);
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key), settings);
        var reader = new PjmDayReader<PjmFuelMixRow>(client, new PjmFuelMixRowMapper(), settings, ClientFactory.Null);

        Assert.Equal(7, (await reader.ReadDayAsync(Gen(), CancellationToken.None)).Count);
        Assert.Equal(new[] { 1, 3, 5, 7 }, stub.Requests.OrderBy(r => r.Index).Select(r => Samples.Parse(r.Uri).StartRow));
    }

    // =====================================================================================
    // 8. The limiter runs on monotonic time.
    // =====================================================================================

    [Fact]
    public async Task R8_A_forward_wall_clock_jump_does_not_release_an_extra_request()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 23, 14, 20, 0, TimeSpan.Zero));
        var limiter = new PjmRateLimiter(2, clock);
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);

        clock.JumpWallClock(TimeSpan.FromHours(2));
        var third = limiter.WaitAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, limiter.AdmittedCount);

        clock.Advance(PjmRateLimiter.Window + PjmRateLimiter.EdgeMargin);
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, limiter.AdmittedCount);
    }

    [Fact]
    public async Task R8_A_backward_wall_clock_jump_does_not_block_a_due_request()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 23, 14, 20, 0, TimeSpan.Zero));
        var limiter = new PjmRateLimiter(1, clock);
        await limiter.WaitAsync(CancellationToken.None);

        var second = limiter.WaitAsync(CancellationToken.None);
        for (var i = 0; i < 200 && clock.PendingTimers == 0; i++) await Task.Delay(10);
        clock.JumpWallClock(TimeSpan.FromHours(-2));
        clock.Advance(PjmRateLimiter.Window + PjmRateLimiter.EdgeMargin);

        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, limiter.AdmittedCount);

        // and an immediately following one is still held (the window is intact, not reset by the jump)
        var third = limiter.WaitAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);
    }

    // =====================================================================================
    // 9. Retry-After beyond 60s trips at once, no retry.
    // =====================================================================================

    public static IEnumerable<object[]> LongRetryAfter() => new[]
    {
        new object[] { 429, new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)) },
        new object[] { 503, new RetryConditionHeaderValue(TimeSpan.FromSeconds(61)) },
        new object[] { 429, new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(10)) },
    };

    [Theory]
    [MemberData(nameof(LongRetryAfter))]
    public async Task R9_Retry_After_beyond_60s_trips_the_guard_immediately_with_no_retry(int status, RetryConditionHeaderValue retryAfter)
    {
        var stub = new StubHttpMessageHandler((_, _) =>
        {
            var r = StubHttpMessageHandler.Json((HttpStatusCode)status, "");
            r.Headers.RetryAfter = retryAfter;
            return r;
        });
        var (client, guard, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(Gen(), 1, CancellationToken.None));

        Assert.Equal(1, stub.Count);
        Assert.True(guard.IsTripped);
        Assert.Contains("Retry-After", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "it waited instead of tripping");

        await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(Ld(), 1, CancellationToken.None));
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public async Task R9_Retry_After_within_the_cap_is_still_retried()
    {
        var stub = new StubHttpMessageHandler((_, i) =>
        {
            if (i > 0) return StubHttpMessageHandler.Json(HttpStatusCode.OK, FuelDayJson());
            var r = StubHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, "");
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return r;
        });
        var (client, guard, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        Assert.Equal(24, (await client.GetPageAsync(Gen(), 1, CancellationToken.None)).Items.Count);
        Assert.Equal(2, stub.Count);
        Assert.False(guard.IsTripped);
    }

    // =====================================================================================
    // 10. New startup validation rules.
    // =====================================================================================

    [Fact]
    public void R10_Validate_rejects_negative_DaysBack()
    {
        Assert.Contains(new PjmSettings { DaysBack = -1 }.Validate(), e => e.Contains("DaysBack must be 0 or more"));
        Assert.DoesNotContain(new PjmSettings { DaysBack = 0, SettledAfterDays = 0 }.Validate(), e => e.Contains("DaysBack must"));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void R10_Validate_bounds_RecentHours_to_0_through_24(int hours, bool ok)
    {
        var errors = new PjmSettings { RecentHours = hours }.Validate();
        Assert.Equal(!ok, errors.Any(e => e.Contains("RecentHours")));
    }

    [Theory]
    [InlineData(29, 28, false)]
    [InlineData(28, 28, true)]
    public void R10_Validate_rejects_SettledAfterDays_beyond_InstLoadMaxDaysBack(int settled, int max, bool ok)
    {
        var errors = new PjmSettings { SettledAfterDays = settled, InstLoadMaxDaysBack = max, InstLoadExpectRowsWithinDays = 25, BackfillDays = 30 }.Validate();
        Assert.Equal(!ok, errors.Any(e => e.Contains("must not exceed InstLoadMaxDaysBack")));
    }
}
