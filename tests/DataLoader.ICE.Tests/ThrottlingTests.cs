using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// Rate limiting and 429 backoff.
///
/// <para>
/// ICE allows <b>30 requests per minute</b>, account-wide — stated in the body of its
/// own 429 and confirmed by measurement: at concurrency 1, exactly 30 succeed and the
/// 31st is rejected (docs/apis/ICE.md §2a). Exceeding it blocks every request for 60
/// seconds, so pacing is the only thing that helps.
/// </para>
/// </summary>
public sealed class ThrottlingTests
{
    private static IceRateLimiter Limiter(int requestsPerMinute) =>
        new(Options.Create(TestHelpers.Settings(s => s.RequestsPerMinute = requestsPerMinute)));

    [Theory]
    [InlineData(30, 2000)]   // ICE's stated ceiling -> one request every 2s
    [InlineData(25, 2400)]   // the shipped default
    [InlineData(60, 1000)]
    public void Interval_is_derived_from_requests_per_minute(int rpm, int expectedMs)
    {
        Assert.Equal(expectedMs, Limiter(rpm).MinInterval.TotalMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_rate_disables_pacing(int rpm)
    {
        Assert.Equal(TimeSpan.Zero, Limiter(rpm).MinInterval);
    }

    [Fact]
    public async Task Unlimited_limiter_does_not_delay()
    {
        var limiter = Limiter(0);
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < 50; i++)
            await limiter.WaitAsync(CancellationToken.None);

        Assert.True(sw.ElapsedMilliseconds < 500, $"Unlimited pacing took {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>
    /// The first request goes out immediately; subsequent ones are spaced. Uses a fast
    /// rate so the test stays quick while still proving the spacing accumulates.
    /// </summary>
    [Fact]
    public async Task Requests_are_spaced_by_the_configured_interval()
    {
        var limiter = Limiter(600);   // 100ms apart
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < 4; i++)
            await limiter.WaitAsync(CancellationToken.None);

        // 4 requests => 3 gaps => >= ~300ms. Generous lower bound for timer slop.
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"Expected pacing of at least ~300ms across 4 requests, got {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>
    /// Pacing must hold across CONCURRENT callers — the limiter is shared by all 18
    /// pipelines, and the budget is per-account, not per-pipeline.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_share_one_budget()
    {
        var limiter = Limiter(600);   // 100ms apart
        var sw = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => limiter.WaitAsync(CancellationToken.None)));

        // 6 concurrent waiters must still be serialised into 5 gaps => ~500ms.
        Assert.True(sw.ElapsedMilliseconds >= 400,
            $"Concurrent callers bypassed pacing: {sw.ElapsedMilliseconds}ms for 6 requests.");
    }

    [Fact]
    public async Task Wait_observes_cancellation()
    {
        var limiter = Limiter(1);   // 60s apart — the second wait would block
        await limiter.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cts.Token));
    }

    // ---------------------------------------------------------------- retry policy

    /// <summary>
    /// ⚠ The defect this fixes. Before the policy existed a 429 hit
    /// <c>EnsureSuccessStatusCode</c> and failed the work unit outright.
    /// </summary>
    [Fact]
    public async Task Rate_limited_response_is_retried_rather_than_failing_the_unit()
    {
        var attempts = 0;
        var policy = IceHttpPolicy.Build(retryCount: 3, delayMs: 10, NullLogger.Instance);

        var response = await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(
                attempts < 3 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Server_errors_are_retried()
    {
        var attempts = 0;
        var policy = IceHttpPolicy.Build(retryCount: 2, delayMs: 10, NullLogger.Instance);

        await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        Assert.Equal(3, attempts);   // initial + 2 retries
    }

    /// <summary>
    /// 200 must never be retried. It carries the missing-file and expired-auth
    /// sentinels, which <see cref="IceResponseClassifier"/> handles — not this policy.
    /// </summary>
    [Fact]
    public async Task Success_is_never_retried()
    {
        var attempts = 0;
        var policy = IceHttpPolicy.Build(retryCount: 3, delayMs: 10, NullLogger.Instance);

        await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        Assert.Equal(1, attempts);
    }

    /// <summary>A 404 is not transient and must not burn retries.</summary>
    [Fact]
    public async Task Not_found_is_not_retried()
    {
        var attempts = 0;
        var policy = IceHttpPolicy.Build(retryCount: 3, delayMs: 10, NullLogger.Instance);

        await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// ICE sends <c>Retry-After: 60</c>. Waiting the full delta is what clears the
    /// block — a shorter exponential backoff just re-trips the limit. Verified by
    /// timing out rather than by waiting 60s: the retry must still be pending well
    /// after the 10ms backoff would have fired.
    /// </summary>
    [Fact]
    public async Task Retry_After_header_overrides_the_shorter_exponential_backoff()
    {
        var policy = IceHttpPolicy.Build(retryCount: 1, delayMs: 10, NullLogger.Instance);

        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync(_ => Task.FromResult(throttled), cts.Token));
    }

    /// <summary>Without a Retry-After header the policy falls back to its own backoff.</summary>
    [Fact]
    public async Task Backoff_is_used_when_no_Retry_After_is_present()
    {
        var attempts = 0;
        var policy = IceHttpPolicy.Build(retryCount: 2, delayMs: 10, NullLogger.Instance);
        var sw = Stopwatch.StartNew();

        await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });

        Assert.Equal(3, attempts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            "Without Retry-After the policy must use its own short backoff.");
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// The shipped default sits UNDER ICE's stated 30/min, on purpose: Cloudflare
    /// counts over a sliding window, so pacing exactly at the limit puts boundary
    /// requests on the wrong side of it.
    /// </summary>
    [Fact]
    public void Default_rate_leaves_headroom_under_the_stated_limit()
    {
        var rpm = new IceSettings().RequestsPerMinute;

        Assert.True(rpm > 0, "Pacing must be on by default.");
        Assert.True(rpm < 30, $"Default RequestsPerMinute={rpm} must leave headroom under ICE's 30/min.");
        Assert.Equal(25, rpm);
    }

    /// <summary>
    /// A cold run is bounded by the rate limit, not by bandwidth. This pins the
    /// arithmetic that the startup log reports to the operator.
    /// </summary>
    [Fact]
    public void Cold_run_request_count_matches_the_documented_estimate()
    {
        var settings = new IceSettings();
        var requests = IceDescriptors.All.Count * (settings.DaysBack + 1);

        Assert.Equal(558, requests);

        var minutes = requests / (double)settings.RequestsPerMinute;
        Assert.InRange(minutes, 20, 25);
    }
}
