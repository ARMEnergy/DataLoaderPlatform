using System.Net;
using DataLoader.Core.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace DataLoader.Genscape;

/// <summary>
/// The retry policy for the Genscape client.
///
/// <para>
/// <see cref="RetryPolicyFactory.BuildHttpRetryPolicy"/> already covers the transient
/// set (<c>5xx</c>, <c>408</c>, socket failures) with exponential backoff. This adds
/// <c>429 Too Many Requests</c>, which
/// <c>HttpPolicyExtensions.HandleTransientHttpError</c> does NOT treat as transient.
/// </para>
/// <para>
/// No published rate limit was found for these endpoints, and no <c>X-RateLimit-*</c>
/// or <c>Retry-After</c> headers came back on any live probe — but the API sits behind
/// an Imperva CDN (<c>X-CDN: Imperva</c>), which throttles on its own terms. Retrying a
/// 429 with backoff is the difference between a run that slows down and a run that
/// fails. The loader makes only four requests with the shipped defaults, so this
/// matters for backfills rather than for normal operation.
/// </para>
/// <para>
/// ⚠ Deliberately NOT retried: <c>400</c>, <c>401</c>, <c>403</c> and <c>404</c>. A
/// malformed window, a bad key and a wrong path are all permanent; retrying them three
/// times just delays the same failure and, for the auth cases, replays rejected
/// credentials at the vendor. <see cref="GenscapeSourceReader"/> turns each into a
/// diagnosable exception instead.
/// </para>
/// </summary>
internal static class GenscapeHttpPolicy
{
    public static IAsyncPolicy<HttpResponseMessage> Build(int retryCount, int delayMs, ILogger logger)
    {
        var transient = RetryPolicyFactory.BuildHttpRetryPolicy(
            retryCount, delayMs,
            (outcome, delay, attempt) => logger.LogWarning(
                "Genscape HTTP retry {Attempt}/{Total}: {Status}, waiting {Delay}ms",
                attempt, retryCount,
                outcome.Result is null ? outcome.Exception?.GetType().Name : ((int)outcome.Result.StatusCode).ToString(),
                delay.TotalMilliseconds));

        var throttled = Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount,
                // Honour Retry-After when the CDN sends one; otherwise back off
                // exponentially from the configured base like the transient policy.
                (attempt, outcome, _) =>
                    outcome.Result?.Headers.RetryAfter?.Delta
                    ?? (outcome.Result?.Headers.RetryAfter?.Date is { } date
                            ? Max(date - DateTimeOffset.UtcNow, TimeSpan.Zero)
                            : TimeSpan.FromMilliseconds(delayMs * Math.Pow(2, attempt - 1))),
                (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "Genscape throttled (429) — retry {Attempt}/{Total} in {Delay}ms",
                        attempt, retryCount, delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });

        // Throttling is the OUTER policy: a 429 retry should restart the whole
        // transient-retry budget rather than consume it.
        return Policy.WrapAsync(throttled, transient);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
