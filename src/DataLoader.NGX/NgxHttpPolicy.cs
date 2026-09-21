using System.Net;
using DataLoader.Core.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace DataLoader.NGX;

/// <summary>
/// The retry policy for the NGX client.
///
/// <para>
/// <see cref="RetryPolicyFactory.BuildHttpRetryPolicy"/> already covers the transient
/// set (<c>5xx</c>, <c>408</c>, socket failures) with exponential backoff. This adds
/// <c>429 Too Many Requests</c>, which
/// <c>HttpPolicyExtensions.HandleTransientHttpError</c> does NOT treat as transient.
/// </para>
/// <para>
/// No published rate limit was found for these endpoints and no <c>X-RateLimit-*</c>
/// header came back on any live probe, but the host sits behind Cloudflare
/// (<c>Server: cloudflare</c>, <c>cf-ray</c>) and a month-sized strip response takes
/// tens of seconds to stream — so a throttle is plausible even though it was never
/// provoked. Honouring <c>Retry-After</c> is the difference between a run that slows
/// down and a run that fails.
/// </para>
/// <para>
/// ⚠ Deliberately NOT retried: <c>302</c>, <c>400</c>, <c>403</c> and <c>404</c>.
/// </para>
/// <list type="bullet">
///   <item>
///     <c>403</c> is the important one. On this endpoint it means ENTITLEMENT — the
///     account may not see one of the requested indices — and the vendor fails the
///     whole batch for one bad id. It is permanent for that request, so retrying it
///     three times only delays the narrowing that
///     <see cref="NgxIndexPriceReader"/> does instead.
///   </item>
///   <item>
///     <c>302</c> is the signature of missing or rejected Basic credentials (a redirect
///     to the ICE SSO login form). Replaying rejected credentials at the vendor is
///     exactly what one should not do on a retry loop.
///   </item>
/// </list>
/// </summary>
internal static class NgxHttpPolicy
{
    public static IAsyncPolicy<HttpResponseMessage> Build(int retryCount, int delayMs, ILogger logger)
    {
        var transient = RetryPolicyFactory.BuildHttpRetryPolicy(
            retryCount, delayMs,
            (outcome, delay, attempt) => logger.LogWarning(
                "NGX HTTP retry {Attempt}/{Total}: {Status}, waiting {Delay}ms",
                attempt, retryCount,
                outcome.Result is null ? outcome.Exception?.GetType().Name : ((int)outcome.Result.StatusCode).ToString(),
                delay.TotalMilliseconds));

        var throttled = Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount,
                (attempt, outcome, _) =>
                    outcome.Result?.Headers.RetryAfter?.Delta
                    ?? (outcome.Result?.Headers.RetryAfter?.Date is { } date
                            ? Max(date - DateTimeOffset.UtcNow, TimeSpan.Zero)
                            : TimeSpan.FromMilliseconds(delayMs * Math.Pow(2, attempt - 1))),
                (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "NGX throttled (429) — retry {Attempt}/{Total} in {Delay}ms",
                        attempt, retryCount, delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });

        // Throttling is the OUTER policy: a 429 retry should restart the whole
        // transient-retry budget rather than consume it.
        return Policy.WrapAsync(throttled, transient);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
