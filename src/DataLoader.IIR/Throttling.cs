using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace DataLoader.IIR;

/// <summary>
/// Process-wide client-side pace limiter (design §3/§4/§10). Spaces outgoing requests by
/// <c>1 / RequestsPerSecond</c> so a run stays under a conservative cap (IIR publishes no hard
/// limit). Registered as a singleton so pacing is global across all three pipelines' internal
/// paging and survives HttpClientFactory handler rotation (the handler is transient; the state lives
/// here).
/// </summary>
public sealed class IirRateLimiter
{
    private readonly double _minIntervalMs;
    private readonly object _gate = new();
    private DateTime _nextAvailableUtc = DateTime.MinValue;

    public IirRateLimiter(IOptions<IirSettings> settings)
    {
        var rps = settings.Value.RequestsPerSecond;
        _minIntervalMs = rps is > 0 ? 1000.0 / rps.Value : 0;
    }

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

/// <summary>Delegating handler that paces every request (retries and the 401 re-mint replay included) through the shared limiter.</summary>
public sealed class IirRateLimitingHandler : DelegatingHandler
{
    private readonly IirRateLimiter _limiter;

    public IirRateLimitingHandler(IirRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// IIR HTTP retry policy (design §1.3): treats 5xx / 408 / network errors AND 429 as transient,
/// honours a <c>Retry-After</c> header when present, and backs off exponentially otherwise. 404 is
/// NOT retried (it is a valid "no data" outcome handled by the source reader); 401 is NOT retried
/// (the auth handler owns the 401 re-mint); 403 is NOT retried (an entitlement gap — surface loudly).
/// </summary>
internal static class IirHttpPolicy
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
                        "IIR HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
                        attempt, outcome.Result?.StatusCode, delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });
    }

    private static TimeSpan ComputeDelay(int attempt, int delayMs, DelegateResult<HttpResponseMessage> outcome)
    {
        var backoff = TimeSpan.FromMilliseconds(delayMs * Math.Pow(2, attempt - 1));

        var retryAfter = outcome.Result?.Headers?.RetryAfter;
        if (retryAfter is not null)
        {
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
