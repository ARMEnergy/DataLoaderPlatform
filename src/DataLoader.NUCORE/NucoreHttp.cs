using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Timeout;

namespace DataLoader.NUCORE;

/// <summary>
/// The retry/timeout policy and the log redaction for every NUCORE request.
/// </summary>
internal static partial class NucoreHttp
{
    /// <summary>
    /// Builds the per-attempt policy: a timeout WRAPPED BY a retry.
    ///
    /// <para>
    /// ⚠ The ordering is the whole point. <c>HttpClient.Timeout</c> bounds a complete
    /// RETRIED operation, because a policy from <c>AddPolicyHandler</c> runs inside it. So
    /// a 300 s client timeout with 4 retries gives all five attempts 300 s BETWEEN them,
    /// and when a slow vendor eats the budget on attempt two, the run reports a CLIENT
    /// timeout and hides the server's actual 502/504. The budget therefore lives here, per
    /// attempt, and the caller leaves <c>HttpClient.Timeout</c> infinite.
    /// </para>
    /// <para>
    /// ⚠ <c>HTTP 500 is NOT retried.</c> On this API a 500 is what a malformed request
    /// gets - a garbage date string returns 500, not 400 - so it is deterministic and
    /// retrying it just burns the budget five times before failing anyway. 502, 503 and 504
    /// ARE retried: those are the gateway under load, which is transient. 401 is never
    /// retried; a wrong key will be wrong next time too.
    /// </para>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> Build(
        int retryCount, int retryDelayMs, int attemptTimeoutSeconds, ILogger logger)
    {
        var timeout = Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(Math.Max(1, attemptTimeoutSeconds)));

        var retry = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            // A per-attempt timeout surfaces as TimeoutRejectedException and must be
            // retryable, or the timeout policy would turn a slow attempt into a hard
            // failure instead of a retried one.
            .Or<TimeoutRejectedException>()
            .Or<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .OrResult(IsTransientStatus)
            .WaitAndRetryAsync(
                Math.Max(0, retryCount),
                attempt => TimeSpan.FromMilliseconds(retryDelayMs * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, delay, attempt, _) =>
                {
                    var reason = outcome.Exception is not null
                        ? Redact(outcome.Exception.Message)
                        : $"HTTP {(int)outcome.Result.StatusCode}";
                    logger.LogWarning(
                        "NUCORE: retry {Attempt} in {Delay}ms after {Reason}",
                        attempt, delay.TotalMilliseconds, reason);
                });

        // retry OUTSIDE, timeout INSIDE => the timeout bounds each attempt.
        return retry.WrapAsync(timeout);
    }

    /// <summary>
    /// Which statuses are worth trying again. Deliberately narrower than Polly's
    /// <c>HandleTransientHttpError</c>, which includes every 5xx and would retry this API's
    /// deterministic 500.
    /// </summary>
    public static bool IsTransientStatus(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.RequestTimeout        // 408
                            or HttpStatusCode.TooManyRequests       // 429
                            or HttpStatusCode.BadGateway            // 502
                            or HttpStatusCode.ServiceUnavailable    // 503
                            or HttpStatusCode.GatewayTimeout;       // 504

    /// <summary>
    /// Strips anything from a string that could carry the API key before it reaches a log
    /// or a load-log error message.
    ///
    /// <para>
    /// The key travels as a header rather than in the query string, so it is far less
    /// exposed here than in a loader like OilX. This still runs on every message that
    /// leaves the loader, because an <c>HttpRequestException</c> can quote a request, and
    /// because a future change to the vendor's auth should not quietly start leaking.
    /// </para>
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var scrubbed = ApiKeyHeaderPattern().Replace(message, "X-Api-Key: ***");
        scrubbed = ApiKeyQueryPattern().Replace(scrubbed, "$1=***");
        return scrubbed;
    }

    [GeneratedRegex(@"(?i)x-api-key\s*[:=]\s*\S+")]
    private static partial Regex ApiKeyHeaderPattern();

    [GeneratedRegex(@"(?i)\b(api[_-]?key|apikey|key|code|token)=[^&\s""']+")]
    private static partial Regex ApiKeyQueryPattern();
}
