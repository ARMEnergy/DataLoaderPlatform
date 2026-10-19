using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Timeout;
using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// The HTTP resilience pipeline.
///
/// <para>
/// ⚠ <b>The regression these tests exist for.</b> On the loader's first full run every
/// <c>electricity/rto/daily-fuel-type-data</c> unit failed with "HTTP request timed out
/// (HttpTimeoutSeconds)". The cause was not the vendor and not the window size: a policy
/// added with <c>AddPolicyHandler</c> runs INSIDE <see cref="HttpClient.Timeout"/>, which
/// bounds the whole retried operation. A 120s client timeout with four retries therefore
/// gave all five attempts 120s BETWEEN them, so against an endpoint whose gateway 504s at
/// ~60s the second attempt blew the budget — and the real 504 was hidden behind a
/// client-timeout message.
/// </para>
/// <para>
/// The budget now lives in the policy, per attempt, and the client's timeout is infinite.
/// </para>
/// </summary>
public sealed class EiaResilienceTests
{
    private const int PerAttemptSeconds = 1;

    private static Polly.IAsyncPolicy<HttpResponseMessage> Pipeline(int retryCount = 2) =>
        EiaHttp.BuildResiliencePipeline(
            retryCount, retryDelayMs: 1, perAttemptTimeoutSeconds: PerAttemptSeconds,
            NullLogger.Instance);

    // --------------------------------------- the per-attempt timeout itself

    /// <summary>
    /// The core assertion. Each attempt must get its OWN budget: with three attempts and
    /// a 1-second budget, a responder that never answers must be called three times.
    /// Under the old global-timeout arrangement the first attempt would have consumed
    /// everything and the rest would never have run.
    /// </summary>
    [Fact]
    public async Task Each_attempt_gets_its_own_timeout_budget()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            Pipeline(retryCount: 2).ExecuteAsync(async ct =>
            {
                Interlocked.Increment(ref attempts);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);    // never answers in time
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, CancellationToken.None));

        Assert.Equal(3, attempts);   // 1 initial + 2 retries, each timed out on its own
    }

    /// <summary>
    /// A slow-but-not-too-slow attempt must SUCCEED rather than being killed by budget
    /// that an earlier attempt already spent.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_timeout_can_still_succeed()
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 2).ExecuteAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n == 1) await Task.Delay(TimeSpan.FromSeconds(30), ct);   // first attempt hangs
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    // ------------------------------------------------- what gets retried

    /// <summary>
    /// 503 and 504 are exactly what the api.data.gov gateway returns when it abandons a
    /// slow EIA request, so they must be retried.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Retries_the_statuses_that_can_clear(HttpStatusCode status)
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 2).ExecuteAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(status));
        }, CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    /// <summary>
    /// 400/403/404 cannot clear. 403 especially must not be retried — the gateway
    /// rate-limits per KEY, so hammering it is the one thing that makes a key problem
    /// worse.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Does_not_retry_the_statuses_that_cannot_clear(HttpStatusCode status)
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 4).ExecuteAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(status));
        }, CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// A real cancellation — the run aborting, or the work-unit budget expiring — must
    /// propagate at once rather than being retried against a dead token.
    /// </summary>
    [Fact]
    public async Task Does_not_retry_a_genuine_cancellation()
    {
        var attempts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Pipeline(retryCount: 4).ExecuteAsync(ct =>
            {
                Interlocked.Increment(ref attempts);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }, cts.Token));

        Assert.True(attempts <= 1, $"a cancelled operation was attempted {attempts} times");
    }

    [Fact]
    public async Task A_success_is_not_retried()
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 4).ExecuteAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    // ------------------------------------------------------ the settings

    /// <summary>
    /// The shipped budget sits just above the gateway's own ~60s cutoff: high enough to
    /// let a slow-but-real response land, low enough that a request the vendor has
    /// already abandoned is retried promptly rather than waited out.
    /// </summary>
    [Fact]
    public void The_shipped_per_attempt_budget_brackets_the_gateway_cutoff()
    {
        var settings = new EiaSettings();

        Assert.InRange(settings.HttpTimeoutSeconds, 61, 120);
    }

    /// <summary>
    /// Worst-case wall time for one request — all attempts at full budget plus the
    /// longest backoff each could draw — must fit inside the work-unit budget, or a unit
    /// is killed mid-retry and the real reason is replaced by a generic cancellation.
    /// </summary>
    [Fact]
    public void Retry_budget_fits_inside_the_work_unit_budget()
    {
        var s = new EiaSettings();

        var worstCaseSeconds = EiaHttp.WorstCaseSeconds(s.RetryCount, s.RetryDelayMs, s.HttpTimeoutSeconds);

        Assert.True(worstCaseSeconds < s.WorkUnitTimeoutSeconds,
            $"One request could take {worstCaseSeconds:N0}s across {s.RetryCount + 1} attempts, but " +
            $"WorkUnitTimeoutSeconds is {s.WorkUnitTimeoutSeconds}s.");
    }

    // --------------------------------------------------------- backoff

    private static DelegateResult<HttpResponseMessage> Outcome(HttpStatusCode status) =>
        new(new HttpResponseMessage(status));

    /// <summary>
    /// ⚠ EIA's 503 is the vendor's BACKEND giving up on an expensive query — measured at
    /// 41-50 seconds to produce — not a fast gateway rejection. Retrying it one second
    /// later asks the same overloaded backend the same question before it has recovered.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void An_overload_answer_backs_off_much_further_than_a_throttle(HttpStatusCode overload)
    {
        const int BaseMs = 1000;

        // Compare the minimum possible overload delay against the maximum possible
        // ordinary delay, so the assertion cannot flake on jitter.
        var overloadMin = Enumerable.Range(0, 50)
            .Min(_ => EiaHttp.Backoff(1, Outcome(overload), BaseMs).TotalMilliseconds);
        var throttleMax = Enumerable.Range(0, 50)
            .Max(_ => EiaHttp.Backoff(1, Outcome(HttpStatusCode.TooManyRequests), BaseMs).TotalMilliseconds);

        Assert.True(overloadMin > throttleMax,
            $"A {(int)overload} waited {overloadMin:N0}ms at least, a 429 up to {throttleMax:N0}ms — " +
            "the overload answer must back off further.");
    }

    [Fact]
    public void Backoff_grows_with_the_attempt_number()
    {
        const int BaseMs = 1000;
        var o = Outcome(HttpStatusCode.ServiceUnavailable);

        var first = Enumerable.Range(0, 50).Max(_ => EiaHttp.Backoff(1, o, BaseMs).TotalMilliseconds);
        var third = Enumerable.Range(0, 50).Min(_ => EiaHttp.Backoff(3, o, BaseMs).TotalMilliseconds);

        Assert.True(third > first, $"attempt 3 ({third:N0}ms) must wait longer than attempt 1 ({first:N0}ms)");
    }

    /// <summary>
    /// ⚠ The jitter is what stops several concurrent work units — all sharing one API
    /// key — from retrying in lockstep and turning one burst of 503s into the next. A
    /// fixed delay would make every sample identical.
    /// </summary>
    [Fact]
    public void Backoff_is_jittered_so_concurrent_units_do_not_retry_in_lockstep()
    {
        var o = Outcome(HttpStatusCode.ServiceUnavailable);

        var samples = Enumerable.Range(0, 200)
            .Select(_ => EiaHttp.Backoff(1, o, 1000).TotalMilliseconds)
            .ToList();

        Assert.True(samples.Distinct().Count() > 100,
            $"only {samples.Distinct().Count()} distinct delays out of 200 — the backoff is not jittered");

        // And the spread stays inside the documented +/-25%.
        var nominal = 1000d * 5;
        Assert.All(samples, s => Assert.InRange(s, nominal * 0.75, nominal * 1.25));
    }
}
