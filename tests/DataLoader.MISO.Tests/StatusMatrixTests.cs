using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Polly;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>
/// Design §5 / API doc §4 status matrix, exercised through the REAL named HttpClient (retry → limiter →
/// per-attempt timeout → buffering) over a stub primary handler, using the live error bodies.
/// </summary>
public sealed class StatusMatrixTests
{
    private static readonly DateOnly D = Samples.D(2026, 11, 18);

    private static MisoWorkUnit Zonal(int age = 1, MisoZone zone = MisoZone.Hot) => Samples.Unit(MisoFeeds.ZonalLoadHourly, D, age, zone);

    private static async Task<(IReadOnlyList<MisoZonalLoadHourlyRow> Rows, MisoHarness H)> Read(StubHttpMessageHandler stub, MisoWorkUnit unit)
    {
        var h = MisoHarness.Create(stub);
        return (await h.Reader(new MisoZonalLoadMapper()).ReadAsync(unit, default), h);
    }

    // ---- 400, classified by reason ------------------------------------------------

    [Theory]
    [InlineData("err_future.json")]     // Invalid date: data not available yet for this market date
    [InlineData("err_mtlf_d1.json")]    // Init date must be before today
    public async Task Not_yet_400_is_a_zero_row_success_without_retry(string fixture)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, Samples.Fixture(fixture));
        var (rows, h) = await Read(stub, Zonal(age: 1));
        Assert.Empty(rows);
        Assert.Equal(1, stub.Count);
        Assert.False(h.Guard.IsTripped);
    }

    [Fact]
    public async Task Not_yet_with_a_Retry_After_header_is_still_not_retried()
    {
        var stub = new StubHttpMessageHandler((_, _) =>
        {
            var r = StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, Samples.Fixture("err_future.json"));
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return r;
        });
        var (rows, _) = await Read(stub, Zonal());
        Assert.Empty(rows);
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public async Task Out_of_retention_400_is_a_zero_row_success_with_a_warning_and_may_settle()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, Samples.Fixture("err_old2023.json"));
        var (rows, h) = await Read(stub, Zonal(age: 1100, zone: MisoZone.Settled));
        Assert.Empty(rows);
        Assert.Equal(1, stub.Count);
        Assert.Contains(h.Logs.OfLevel(LogLevel.Warning), e => e.Message.Contains("rolling lookback"));
    }

    [Theory]
    [InlineData(1065, true)]    // MaxLookbackDays 1095 − 30: the youngest age still believed
    [InlineData(1064, false)]
    [InlineData(100, false)]
    [InlineData(1, false)]
    public async Task Out_of_retention_is_accepted_only_within_30_days_of_MaxLookbackDays(int age, bool accepted)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, Samples.Fixture("err_old2023.json"));
        using var h = MisoHarness.Create(stub);
        var unit = Zonal(age, age > 10 ? MisoZone.Settled : MisoZone.Hot);
        var read = h.Reader(new MisoZonalLoadMapper()).ReadAsync(unit, default);
        if (accepted)
            Assert.Empty(await read);
        else
            Assert.Contains("only " + age + " days old", (await Assert.ThrowsAsync<MisoDataException>(() => read)).Message);
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public async Task Out_of_retention_age_for_MTLF_is_measured_from_the_forecast_date_D()
    {
        // I is 1066 days old (past the floor) but D = I+2 is 1064 days old: the vendor checks D, so this fails.
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, Samples.Fixture("err_old2023.json"));
        using var h = MisoHarness.Create(stub);
        var today = Samples.D(2026, 11, 24);
        var init = today.AddDays(-1066);
        MisoWorkUnit Mtlf(int h2) => new()
        {
            Feed = MisoFeeds.LoadForecastMtlf, InitDate = init, MarketDate = init.AddDays(h2), TodayEst = today,
            Zone = MisoZone.Settled, KeyValue = "k" + h2,
        };
        var reader = h.Reader(new MisoLoadForecastMtlfMapper());
        Assert.Empty(await reader.ReadAsync(Mtlf(1), default));                                   // D age 1065
        await Assert.ThrowsAsync<MisoDataException>(() => reader.ReadAsync(Mtlf(2), default));    // D age 1064
    }

    [Theory]
    [InlineData("err_garbage.json", "expected format")]
    [InlineData("err_badzone.json", "acceptableValues")]
    [InlineData("err_mtlf_d8.json", "within 7 days")]
    [InlineData("err_mtlf_initeq.json", "after init date")]
    public async Task Other_documented_400s_fail_the_unit_without_retry(string fixture, string reasonFragment)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, Samples.Fixture(fixture));
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(), default));
        Assert.Equal(400, ex.HttpStatus);
        Assert.Contains(reasonFragment, ex.Message);
        Assert.Equal(1, stub.Count);
        Assert.False(h.Guard.IsTripped);
    }

    [Theory]
    [InlineData("{\"status\":400,\"reason\":\"Something new\"}")]
    [InlineData("{\"status\":400,\"reason\":\"data not available yet\"}")]   // prefix is matched on the FULL documented reason
    [InlineData("<html>bad request</html>")]
    [InlineData("")]
    public async Task Unknown_400_fails_without_retry(string body)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, body);
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(), default));
        Assert.Equal(1, stub.Count);
        Assert.DoesNotContain("<html>", ex.Message);   // the raw body is never quoted
    }

    [Fact]
    public async Task Reason_prefix_match_ignores_case()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest, "{\"status\":400,\"reason\":\"INVALID DATE: DATA NOT AVAILABLE YET for this market date\"}");
        var (rows, _) = await Read(stub, Zonal());
        Assert.Empty(rows);
    }

    // ---- 404 ----------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Empty_404_on_page_1_within_ExpectRowsWithinDays_is_a_zero_row_success(int age)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.NotFound, Samples.Fixture("err_page2.json"));
        var (rows, _) = await Read(stub, Zonal(age));
        Assert.Empty(rows);
        Assert.Equal(1, stub.Count);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(30)]
    public async Task Empty_404_on_page_1_older_than_ExpectRowsWithinDays_fails(int age)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.NotFound, Samples.Fixture("err_page2.json"));
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoDataException>(() =>
            h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(age, age > 10 ? MisoZone.Settled : MisoZone.Hot), default));
        Assert.Contains($"{age} days old", ex.Message);
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public async Task Empty_404_on_page_2_fails_even_for_a_young_day()
    {
        var rows = Samples.ZonalDay(D);
        var stub = new StubHttpMessageHandler((r, i) =>
        {
            var page = Req.Parse(r.RequestUri!).Query["pageNumber"];
            return page == "1"
                ? Req.Ok(Samples.Envelope(rows.Take(100), totalElements: 144, lastPage: false, totalPages: 2))
                : StubHttpMessageHandler.Json(HttpStatusCode.NotFound, Samples.Fixture("err_page2.json"));
        });
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(age: 1), default));
        Assert.Contains("page 2", ex.Message);
        Assert.Equal(2, stub.Count);
    }

    [Fact]
    public async Task A_404_without_the_empty_data_reason_fails()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.NotFound, "{ \"statusCode\": 404, \"message\": \"Resource not found\" }");
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(age: 1), default));
        Assert.Equal(404, ex.HttpStatus);
    }

    // ---- 401 / 403: stop the run ---------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_refusal_trips_the_run_guard_without_retry_and_blocks_further_requests(HttpStatusCode status)
    {
        var stub = StubHttpMessageHandler.Always(status, Samples.Fixture("err_badkey.json"));
        var h = MisoHarness.Create(stub);
        var reader = h.Reader(new MisoZonalLoadMapper());

        await Assert.ThrowsAsync<MisoRunHaltedException>(() => reader.ReadAsync(Zonal(), default));
        Assert.True(h.Guard.IsTripped);
        Assert.Contains($"HTTP {(int)status}", h.Guard.Reason);
        Assert.Contains("Access denied due to invalid subscription key", h.Guard.Reason);
        Assert.Equal(1, stub.Count);

        await Assert.ThrowsAsync<MisoRunHaltedException>(() => reader.ReadAsync(Zonal(age: 2), default));
        Assert.Equal(1, stub.Count);   // nothing more was sent
    }

    // ---- transient: retried, then fail ---------------------------------------------

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Retryable_status_is_retried_RetryCount_times_then_fails(int status)
    {
        var stub = StubHttpMessageHandler.Always((HttpStatusCode)status, "{\"status\":" + status + ",\"reason\":\"busy\"}");
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(), default));
        Assert.Equal(status, ex.HttpStatus);
        Assert.Equal(h.Settings.RetryCount + 1, stub.Count);
        Assert.False(h.Guard.IsTripped);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Retryable_status_then_200_succeeds(int status)
    {
        var stub = StubHttpMessageHandler.Sequence(
            ((HttpStatusCode)status, "{}"),
            (HttpStatusCode.OK, Samples.Envelope(Samples.ZonalDay(D))));
        var (rows, _) = await Read(stub, Zonal());
        Assert.Equal(24, rows.Count);
        Assert.Equal(2, stub.Count);
    }

    [Fact]
    public async Task HttpRequestException_is_retried()
    {
        var stub = new StubHttpMessageHandler((_, i, _) => i == 0
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(Req.Ok(Samples.Envelope(Samples.ZonalDay(D)))));
        var (rows, _) = await Read(stub, Zonal());
        Assert.Equal(24, rows.Count);
        Assert.Equal(2, stub.Count);
    }

    [Fact]
    public async Task HttpRequestException_every_time_fails_after_retries()
    {
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("connection reset"));
        var h = MisoHarness.Create(stub);
        var ex = await Assert.ThrowsAsync<MisoRequestException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(), default));
        Assert.Contains("transport failure", ex.Message);
        Assert.Equal(h.Settings.RetryCount + 1, stub.Count);
    }

    [Fact]
    public async Task Per_attempt_timeout_is_retried_with_a_fresh_budget()
    {
        var stub = new StubHttpMessageHandler(async (_, i, ct) =>
        {
            if (i == 0) await Task.Delay(TimeSpan.FromSeconds(10), ct);   // cut off by the 1s per-attempt budget
            return Req.Ok(Samples.Envelope(Samples.ZonalDay(D)));
        });
        var h = MisoHarness.Create(stub, new Dictionary<string, string?> { ["Loaders:MISO:HttpTimeoutSeconds"] = "1" });
        var rows = await h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(), default);
        Assert.Equal(24, rows.Count);
        Assert.Equal(2, stub.Count);
    }

    [Fact]
    public void Retry_After_stretches_the_backoff_and_is_capped()
    {
        static DelegateResult<HttpResponseMessage> With(TimeSpan? retryAfter)
        {
            var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (retryAfter is { } ra) r.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
            return new DelegateResult<HttpResponseMessage>(r);
        }

        Assert.Equal(TimeSpan.FromSeconds(2), MisoHttpPolicy.ComputeDelay(1, 2000, With(null)));
        Assert.Equal(TimeSpan.FromSeconds(8), MisoHttpPolicy.ComputeDelay(3, 2000, With(null)));
        Assert.Equal(TimeSpan.FromSeconds(30), MisoHttpPolicy.ComputeDelay(1, 2000, With(TimeSpan.FromSeconds(30))));
        Assert.Equal(TimeSpan.FromSeconds(4), MisoHttpPolicy.ComputeDelay(2, 2000, With(TimeSpan.FromSeconds(1))));   // shorter hint: backoff wins
        Assert.Equal(MisoHttpPolicy.MaxRetryAfter, MisoHttpPolicy.ComputeDelay(1, 2000, With(TimeSpan.FromHours(5))));
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(302, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    public void Retry_predicate_matches_the_matrix(int status, bool retryable) =>
        Assert.Equal(retryable, MisoHttpPolicy.IsRetryableStatus((HttpStatusCode)status));

    // ---- redirects -----------------------------------------------------------------

    [Fact]
    public async Task A_redirect_is_a_contract_failure_and_is_not_followed_by_the_real_primary_handler()
    {
        // Loopback only: the module's own SocketsHttpHandler (AllowAutoRedirect = false) is used.
        await using var server = new LoopbackHttpServer("127.0.0.1", (_, _) =>
            LoopbackHttpServer.Raw("HTTP/1.1 302 Found\nLocation: /elsewhere\nContent-Length: 0\nConnection: close"));

        var h = MisoHarnessWithRealPrimary.Create(server.BaseUrl);
        var ex = await Assert.ThrowsAsync<MisoContractException>(() => h.Reader(new MisoZonalLoadMapper()).ReadAsync(Zonal(), default));
        Assert.Equal(302, ex.HttpStatus);
        Assert.Equal(1, server.Count);
        Assert.Contains(MisoApiClient.KeyHeader + ": " + Samples.Key, server.Requests.Single());
        Assert.DoesNotContain(Samples.Key, server.Requests.Single().Split('\n')[0]);   // not in the request line
    }

    // ---- the never-settle-a-not-yet invariant ----------------------------------------

    [Theory]
    [InlineData(MisoPageOutcome.NotYet)]
    [InlineData(MisoPageOutcome.Empty)]
    public async Task A_zero_row_not_yet_or_young_empty_outcome_on_a_SETTLED_unit_fails(MisoPageOutcome outcome)
    {
        var reader = new MisoDayReader<MisoZonalLoadHourlyRow>(FakePageSource.Outcome(outcome), new MisoZonalLoadMapper(),
            Samples.Settings(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<MisoDataException>(() => reader.ReadAsync(Zonal(age: 1, zone: MisoZone.Settled), default));
        Assert.Contains("SETTLED", ex.Message);
    }
}

