using Polly;
using Polly.Extensions.Http;

namespace DataLoader.Core.Resilience;

/// <summary>
/// Polly retry policies for HTTP and arbitrary code. Centralised so every
/// loader gets the same retry shape (exponential backoff with jitter) without
/// each one reinventing it.
/// </summary>
public static class RetryPolicyFactory
{
    /// <summary>
    /// HTTP retry policy for transient failures (5xx, 408, network errors) and
    /// for a client-side request timeout.
    ///
    /// <para>
    /// <c>HandleTransientHttpError</c> alone does NOT cover an
    /// <see cref="HttpClient.Timeout"/> expiry: that surfaces as
    /// <see cref="TaskCanceledException"/>, not <c>HttpRequestException</c>, so a
    /// single slow response used to cost a whole work unit with no retry at all.
    /// EnergyAspects lost a unit to exactly that — one request over the 60s limit
    /// while the same request took 13-17s on every other window.
    /// </para>
    ///
    /// <para>
    /// The predicate is what makes this safe. A timeout throws
    /// <see cref="TaskCanceledException"/> with a <see cref="TimeoutException"/>
    /// inside it; a genuine cancellation (run abort, per-unit budget) throws the
    /// same type with a different inner, so it is NOT matched and still
    /// propagates immediately rather than being retried against a dead token.
    /// </para>
    ///
    /// <para>
    /// Note the interaction with <c>WorkUnitTimeoutSeconds</c>: retries are
    /// serial, so the worst case is roughly
    /// <c>HttpTimeoutSeconds × (retryCount + 1)</c> plus backoff. Keep that under
    /// the per-unit budget or the unit is cancelled mid-retry.
    /// </para>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> BuildHttpRetryPolicy(
        int retryCount,
        int delayMs,
        Action<DelegateResult<HttpResponseMessage>, TimeSpan, int>? onRetry = null) =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .WaitAndRetryAsync(
                retryCount,
                attempt => TimeSpan.FromMilliseconds(delayMs * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, delay, attempt, _) =>
                    onRetry?.Invoke(outcome, delay, attempt));

    /// <summary>
    /// Generic exception-based retry policy for non-HTTP work (file I/O,
    /// SQL transients, etc). Caller specifies which exceptions are retryable.
    /// </summary>
    public static IAsyncPolicy BuildRetryPolicy(
        int retryCount,
        int delayMs,
        Func<Exception, bool>? shouldRetry = null,
        Action<Exception, TimeSpan, int>? onRetry = null) =>
        Policy
            .Handle<Exception>(ex => shouldRetry?.Invoke(ex) ?? true)
            .WaitAndRetryAsync(
                retryCount,
                attempt => TimeSpan.FromMilliseconds(delayMs * Math.Pow(2, attempt - 1)),
                onRetry: (ex, delay, attempt, _) =>
                    onRetry?.Invoke(ex, delay, attempt));
}
