using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Design §12.2 tests 30-35: the per-response status matrix (§6.1) through the REAL retry
/// policy and limiter handler over a stubbed wire, plus the run guard (§6.3).
/// </summary>
public sealed class StatusMatrixTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private const string Key = "CANARY-KEY-30-a1b2c3d4e5";

    private static PjmDayWorkUnit Unit() => ClientFactory.GenUnit(Day, 3);

    private static string OkBody() => Samples.Envelope(new[] { Samples.Fuel(Day.ToDateTime(new TimeOnly(1, 0))) });

    // ---- 30 -----------------------------------------------------------------------

    [Fact]
    public async Task T30_errors_in_a_200_body_fails_without_retry_and_redacts_the_key()
    {
        var body = "{\"items\":[],\"totalRows\":0,\"errors\":[{\"message\":\"bad filter for key " + Key + "\"},\"second " + Key + "\"]}";
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, body);
        var (client, guard, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));

        Assert.Equal(PjmFailureClass.Errors, ex.FailureClass);
        Assert.Equal(1, stub.Count);
        Assert.Contains("bad filter for key ***", ex.Message);
        Assert.Contains("second ***", ex.Message);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Key);
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task T30_errors_text_is_truncated_to_three_messages()
    {
        var errs = string.Join(",", Enumerable.Range(1, 5).Select(i => $"\"msg{i}\""));
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, "{\"items\":[],\"totalRows\":0,\"errors\":[" + errs + "]}");
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));
        Assert.Contains("msg3", ex.Message);
        Assert.DoesNotContain("msg4", ex.Message);
    }

    [Theory]
    [InlineData("{\"items\":[],\"totalRows\":0,\"errors\":[]}")]
    [InlineData("{\"items\":[],\"totalRows\":0,\"errors\":null}")]
    public async Task T30_empty_or_null_errors_is_not_a_failure(string body)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, body);
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        var page = await client.GetPageAsync(Unit(), 1, CancellationToken.None);
        Assert.Equal(0, page.TotalRows);
    }

    // ---- 31 -----------------------------------------------------------------------

    [Theory]
    [InlineData(400, "HTTP 400")]
    [InlineData(404, "HTTP 404")]
    public async Task T31_400_and_404_fail_with_exactly_one_request(int status, string cls)
    {
        var stub = StubHttpMessageHandler.Always((HttpStatusCode)status, "{\"message\":\"nope " + Key + "\"}");
        var (client, guard, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));

        Assert.Equal(cls, ex.FailureClass);
        Assert.Equal(status, ex.HttpStatus);
        Assert.Equal(1, stub.Count);
        Assert.False(guard.IsTripped);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Key);
    }

    // ---- 32 -----------------------------------------------------------------------

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    public async Task T32_transient_statuses_retry_RetryCount_times_then_fail_without_tripping(int status)
    {
        var stub = StubHttpMessageHandler.Always((HttpStatusCode)status);
        var settings = Samples.Settings(s => s.RetryCount = 3);
        var (client, guard, limiter, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key), settings);

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));

        Assert.Equal(PjmFailureClass.TransientExhausted, ex.FailureClass);
        Assert.Equal(4, stub.Count);
        Assert.Equal(4, limiter.AdmittedCount);   // every attempt — retries included — is paced
        Assert.False(guard.IsTripped);              // the run continues
    }

    [Fact]
    public async Task T32_transient_then_success_returns_the_page()
    {
        var stub = StubHttpMessageHandler.Sequence((HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.OK, OkBody()));
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        var page = await client.GetPageAsync(Unit(), 1, CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal(2, stub.Count);
    }

    // ---- 33 -----------------------------------------------------------------------

    [Fact]
    public async Task T33_429_with_Retry_After_2_waits_at_least_2s_then_succeeds()
    {
        var stub = new StubHttpMessageHandler((_, i) =>
        {
            if (i == 0)
            {
                var r = StubHttpMessageHandler.Json((HttpStatusCode)429, "");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return r;
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, OkBody());
        });
        var (client, guard, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var sw = Stopwatch.StartNew();
        var page = await client.GetPageAsync(Unit(), 1, CancellationToken.None);
        sw.Stop();

        Assert.Single(page.Items);
        Assert.Equal(2, stub.Count);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(1950), $"waited only {sw.Elapsed}");
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task T33_429_on_every_attempt_trips_the_guard_and_later_requests_send_nothing()
    {
        var stub = StubHttpMessageHandler.Always((HttpStatusCode)429);
        var (client, guard, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var ex = await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));
        Assert.Contains("429", ex.Message);
        Assert.True(guard.IsTripped);
        Assert.Equal(4, stub.Count);

        await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(ClientFactory.LoadUnit(Day, 3), 1, CancellationToken.None));
        Assert.Equal(4, stub.Count);   // zero further requests
    }

    // ---- 34 -----------------------------------------------------------------------

    [Fact]
    public async Task T34_403_trips_without_retry_and_says_entitlement_or_quota()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.Forbidden, "{\"statusCode\":403,\"message\":\"Out of call volume quota for " + Key + "\"}");
        var (client, guard, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Key));

        var ex = await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));

        Assert.Equal(1, stub.Count);
        Assert.True(guard.IsTripped);
        Assert.Contains("entitlement or quota", ex.Message);
        Assert.Contains("HTTP 403", ex.Message);
        Assert.DoesNotContain("401", ex.Message);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()).Append(guard.Reason), Key);

        await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(Unit(), 1, CancellationToken.None));
        Assert.Equal(1, stub.Count);
    }

    // ---- 35 -----------------------------------------------------------------------

    private static (PjmFuelMixSourceReader Source, PjmRunGuard Guard) SourceOver(IPjmPageSource pages, int trip = 3)
    {
        var guard = new PjmRunGuard(trip, ClientFactory.Null);
        var reader = new PjmDayReader<PjmFuelMixRow>(pages, new PjmFuelMixRowMapper(), Samples.Settings(), ClientFactory.Null);
        return (new PjmFuelMixSourceReader(reader, guard), guard);
    }

    private sealed class ThrowingPages : IPjmPageSource
    {
        private readonly Func<Exception> _ex;
        public ThrowingPages(Func<Exception> ex) => _ex = ex;
        public Task<PjmPage> GetPageAsync(PjmDayWorkUnit unit, int startRow, CancellationToken ct) => throw _ex();
    }

    [Fact]
    public async Task T35_three_consecutive_400_units_trip_the_guard()
    {
        var (source, guard) = SourceOver(new ThrowingPages(() => new PjmRequestException(PjmFailureClass.Http400, 400, "bad")));

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<PjmRequestException>(() => source.ReadAsync(Unit(), CancellationToken.None));
            Assert.False(guard.IsTripped);
        }
        await Assert.ThrowsAsync<PjmRequestException>(() => source.ReadAsync(Unit(), CancellationToken.None));
        Assert.True(guard.IsTripped);
        Assert.Contains("HTTP 400", guard.Reason);
    }

    [Fact]
    public async Task T35_three_consecutive_400s_through_the_wire_trip_and_the_fourth_unit_sends_nothing()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest);
        var (client, guard, _, _) = ClientFactory.Build(stub, new FakeKeyProvider(Key));
        var reader = new PjmDayReader<PjmFuelMixRow>(client, new PjmFuelMixRowMapper(), Samples.Settings(), ClientFactory.Null);
        var source = new PjmFuelMixSourceReader(reader, guard);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<PjmRequestException>(() => source.ReadAsync(Unit(), CancellationToken.None));
        Assert.True(guard.IsTripped);
        Assert.Equal(3, stub.Count);

        await Assert.ThrowsAsync<PjmRunHaltedException>(() => source.ReadAsync(Unit(), CancellationToken.None));
        Assert.Equal(3, stub.Count);
    }

    [Fact]
    public async Task T35_three_consecutive_echo_failures_do_not_trip()
    {
        var wrongDay = Samples.Fuel(Day.AddDays(1).ToDateTime(new TimeOnly(3, 0)));
        var (source, guard) = SourceOver(FakePageSource.Paged(new[] { wrongDay }, 50000));

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<PjmDataException>(() => source.ReadAsync(Unit(), CancellationToken.None));
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task T35_a_data_failure_or_a_different_class_breaks_the_streak()
    {
        var call = 0;
        var wrongDay = Samples.Fuel(Day.AddDays(1).ToDateTime(new TimeOnly(3, 0)));
        var pages = new FakePageSource((_, _) =>
        {
            var n = call++;
            if (n == 2) return new PjmPage(new List<JsonElement> { wrongDay }, 1);   // echo failure in the middle
            if (n == 5) throw new PjmRequestException(PjmFailureClass.Http404, 404, "nf");
            throw new PjmRequestException(PjmFailureClass.Http400, 400, "bad");
        });
        var (source, guard) = SourceOver(pages);

        for (var i = 0; i < 7; i++)
            await Record.ExceptionAsync(() => source.ReadAsync(Unit(), CancellationToken.None));
        // 400,400,echo,400,400,404,400 -> never three of one class in a row
        Assert.False(guard.IsTripped);
    }

    [Fact]
    public async Task T35_trip_count_zero_disables_the_consecutive_trip()
    {
        var (source, guard) = SourceOver(new ThrowingPages(() => new PjmRequestException(PjmFailureClass.Http400, 400, "bad")), trip: 0);
        for (var i = 0; i < 6; i++)
            await Assert.ThrowsAsync<PjmRequestException>(() => source.ReadAsync(Unit(), CancellationToken.None));
        Assert.False(guard.IsTripped);
    }
}
