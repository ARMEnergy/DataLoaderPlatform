using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace DataLoader.ICE;

/// <summary>
/// Process-wide client-side pace limiter.
///
/// <para>
/// <b>ICE enforces a hard, documented limit of 30 requests per minute</b>, stated in
/// the body of its own <c>429</c> (verified live 2026-09-01):
/// </para>
/// <code>
/// You are limited to 30 requests per minute. You are being blocked due to Rate
/// Limiting Threshold. Please retry after 1 min. Contact: support@ice.com
/// </code>
/// <para>
/// It is a <b>rate</b> limit, not a concurrency gate — 20 simultaneous requests all
/// returned 200, while exceeding 30 in a minute blocked <i>every</i> subsequent
/// request for 60 seconds regardless of how few were in flight. So the fix is
/// pacing, not a semaphore.
/// </para>
/// <para>
/// Requests are spaced by <c>60000 / RequestsPerMinute</c> ms. Registered as a
/// singleton so pacing is global across all 18 pipelines and survives
/// <c>HttpClientFactory</c> handler rotation — the delegating handler is transient;
/// the limiter state lives here.
/// </para>
/// <para>
/// The limit is <b>account-wide</b>. If anything else at ARM uses the same ICE
/// credential concurrently, its requests count against the same budget and this
/// limiter cannot see them — that is what <see cref="IceHttpPolicy"/>'s
/// <c>Retry-After</c> backoff is the safety net for.
/// </para>
/// </summary>
public sealed class IceRateLimiter
{
    private readonly double _minIntervalMs;
    private readonly object _gate = new();
    private DateTime _nextAvailableUtc = DateTime.MinValue;

    public IceRateLimiter(IOptions<IceSettings> settings)
    {
        var rpm = settings.Value.RequestsPerMinute;
        _minIntervalMs = rpm > 0 ? 60000.0 / rpm : 0;
    }

    /// <summary>Effective spacing between requests, for logging and tests.</summary>
    public TimeSpan MinInterval => TimeSpan.FromMilliseconds(_minIntervalMs);

    /// <summary>Waits until this request is allowed to go out. No-op when unlimited.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (_minIntervalMs <= 0) return;

        TimeSpan delay;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var slot = _nextAvailableUtc > now ? _nextAvailableUtc : now;
            _nextAvailableUtc = slot.AddMilliseconds(_minIntervalMs);
            delay = slot - now;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Delegating handler that paces every request — retries included — through the
/// shared limiter.
///
/// <para>
/// Sitting INNER of the retry policy is what makes a retried request wait its turn
/// too. A retry that jumped the queue would spend the very budget the backoff is
/// trying to protect.
/// </para>
/// </summary>
public sealed class IceRateLimitingHandler : DelegatingHandler
{
    private readonly IceRateLimiter _limiter;

    public IceRateLimitingHandler(IceRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// ICE HTTP retry policy: treats 5xx / 408 / network errors AND <c>429</c> as
/// transient, honours the <c>Retry-After</c> header, and backs off exponentially
/// otherwise.
///
/// <para>
/// <b>429 handling is the point of this class.</b> ICE answers a throttled request
/// with <c>429</c> and <c>Retry-After: 60</c>. Without this policy a throttle is an
/// immediate hard failure: <c>EnsureSuccessStatusCode</c> throws, the work unit is
/// recorded failed, and — because the run's other units keep spending the same
/// budget — one burst can cascade into a whole run of failures.
/// </para>
/// <para>
/// A 60-second wait is far longer than the exponential backoff would choose, so
/// honouring <c>Retry-After</c> rather than the computed delay is what actually
/// clears the block; retrying sooner just re-trips it and burns an attempt.
/// </para>
/// <para>
/// <b>200 is never retried</b>, which matters here more than usual: ICE returns 200
/// for missing files and for expired auth alike, and both are handled by
/// <see cref="IceResponseClassifier"/>, not by this policy.
/// </para>
/// </summary>
internal static class IceHttpPolicy
{
    public static IAsyncPolicy<HttpResponseMessage> Build(int retryCount, int delayMs, ILogger logger)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()                                      // 5xx, 408, network errors
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)   // 429
            .WaitAndRetryAsync(
                retryCount,
                (attempt, outcome, _) => ComputeDelay(attempt, delayMs, outcome),
                (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "ICE HTTP retry {Attempt}: status {Status}, waiting {Delay:N0}ms{RateLimited}",
                        attempt,
                        outcome.Result?.StatusCode,
                        delay.TotalMilliseconds,
                        outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests
                            ? " (rate limited — ICE allows 30 requests/minute)"
                            : string.Empty);

                    return Task.CompletedTask;
                });
    }

    private static TimeSpan ComputeDelay(int attempt, int delayMs, DelegateResult<HttpResponseMessage> outcome)
    {
        var backoff = TimeSpan.FromMilliseconds(delayMs * Math.Pow(2, attempt - 1));

        var retryAfter = outcome.Result?.Headers?.RetryAfter;
        if (retryAfter is not null)
        {
            // ICE sends "Retry-After: 60" as a delta. Take it whenever it is longer
            // than our own backoff — retrying before the window resets cannot succeed.
            if (retryAfter.Delta is { } delta && delta > backoff)
                return delta;

            if (retryAfter.Date is { } when)
            {
                var wait = when - DateTimeOffset.UtcNow;
                if (wait > backoff)
                    return wait;
            }
        }

        return backoff;
    }
}
