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
    /// HTTP retry policy for transient failures (5xx, 408, network errors).
    /// Use with <c>AddPolicyHandler</c> on an <c>HttpClient</c>.
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> BuildHttpRetryPolicy(
        int retryCount,
        int delayMs,
        Action<DelegateResult<HttpResponseMessage>, TimeSpan, int>? onRetry = null) =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
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
