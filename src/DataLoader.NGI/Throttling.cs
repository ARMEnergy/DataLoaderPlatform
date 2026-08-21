using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace DataLoader.NGI;

/// <summary>
/// Process-wide client-side pace limiter (design §4.4). Spaces outgoing requests by
/// <c>1 / RequestsPerSecond</c> so a run stays under a conservative cap. NGI publishes <b>no</b> rate
/// limit, no <c>Retry-After</c>, no <c>X-RateLimit-*</c> header, and no <c>429</c> was ever observed
/// in ~11 probe calls — so pace gently (default 2 rps) and back off on <c>429</c>.
///
/// <para>Registered as a singleton so pacing is global across <b>both</b> clients (the data client
/// and the un-authed token client) and survives <c>HttpClientFactory</c> handler rotation — the
/// delegating handler is transient; the limiter state lives here.</para>
/// </summary>
public sealed class NgiRateLimiter
{
    private readonly double _minIntervalMs;
    private readonly object _gate = new();
    private DateTime _nextAvailableUtc = DateTime.MinValue;

    public NgiRateLimiter(IOptions<NgiSettings> settings)
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

/// <summary>
/// Delegating handler that paces every request through the shared limiter — the first attempt, each
/// Polly retry, and the 401 re-mint replay. Registered <b>INNERMOST</b> (design §1.4).
/// </summary>
public sealed class NgiRateLimitingHandler : DelegatingHandler
{
    private readonly NgiRateLimiter _limiter;

    public NgiRateLimitingHandler(NgiRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// NGI HTTP retry policy (design §4.4). Treats 5xx / 408 / network errors AND <c>429</c> as transient,
/// honours a <c>Retry-After</c> header when present, and backs off exponentially otherwise.
///
/// <para><b>What is deliberately NOT retried:</b></para>
/// <list type="bullet">
///   <item><c>404</c> — the <b>normal majority outcome</b> of this monthly feed (~58 of 60 units over
///     the default window). Handled by the reader as a successful empty read.</item>
///   <item><c>400</c> — a malformed <c>issue_date</c>, i.e. a caller bug; unfixable by retry and it
///     must fail loudly.</item>
///   <item><c>401</c> — <b>excluded on purpose so <see cref="NgiTokenAuthHandler"/> owns it</b>
///     (re-mint once and replay). Retrying it here would race the handler.</item>
///   <item><c>403</c> — an entitlement/subscription gap; surface loudly.</item>
/// </list>
/// <c>HandleTransientHttpError()</c> covers 5xx/408/network only, so 4xx statuses are excluded by
/// construction — the single explicit <c>OrResult</c> adds <c>429</c> and nothing else.
/// </summary>
internal static class NgiHttpPolicy
{
    public static IAsyncPolicy<HttpResponseMessage> Build(int retryCount, int delayMs, ILogger logger)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()                                      // 5xx, 408, network errors
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)   // 429
            .WaitAndRetryAsync(
                Math.Max(0, retryCount),
                (attempt, outcome, _) => ComputeDelay(attempt, delayMs, outcome),
                (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "NGI HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
                        attempt, outcome.Result?.StatusCode, delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });
    }

    private static TimeSpan ComputeDelay(int attempt, int delayMs, DelegateResult<HttpResponseMessage> outcome)
    {
        var backoff = TimeSpan.FromMilliseconds(Math.Max(1, delayMs) * Math.Pow(2, attempt - 1));

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
