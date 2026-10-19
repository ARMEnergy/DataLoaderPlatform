using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Timeout;
using Polly.Extensions.Http;

namespace DataLoader.EIA;

/// <summary>
/// HTTP concerns shared by the metadata client and the data reader: secret redaction,
/// the retry policy, and the account-wide request throttle.
/// </summary>
internal static partial class EiaHttp
{
    /// <summary>
    /// 🔒 Strip the API key out of any string bound for a log or an exception message.
    ///
    /// <para>
    /// EIA has no header auth — <c>api_key=</c> is in the query string of EVERY
    /// request, so a single logged URI leaks the credential for the whole organisation.
    /// Three layers guard that: the named client has
    /// <c>RemoveAllLoggers()</c> (the factory's own handlers log the raw URI at
    /// Information level, which is on by default), the loader logs series/frequency/
    /// window rather than URIs, and anything derived from an exception goes through
    /// here. <see cref="HttpRequestException.Message"/> can and does carry the request
    /// URI.
    /// </para>
    /// <para>
    /// Unlike AGSI, EIA does NOT echo the key back in its error bodies — a bad key gets
    /// <c>{"error":{"code":"API_KEY_INVALID","message":"An invalid api_key was supplied...
    /// "}}</c> with no key in it (verified 2026-10-16). Error bodies are still passed
    /// through this before being quoted, because that is a vendor behaviour that can
    /// change without notice.
    /// </para>
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return ApiKeyParam().Replace(text, "$1=***");
    }

    [GeneratedRegex(
        @"\b(api_key|apikey)=([^&\s""'<>\]\)]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyParam();

    /// <summary>
    /// The resilience pipeline for every EIA call: a <b>per-attempt</b> timeout wrapped
    /// by a retry.
    ///
    /// <para>
    /// ⚠ <b>WHY THE TIMEOUT IS HERE AND NOT ON THE HttpClient.</b> A policy added with
    /// <c>AddPolicyHandler</c> runs as a <see cref="DelegatingHandler"/> INSIDE the
    /// client's own timeout, and <see cref="HttpClient.Timeout"/> bounds the whole
    /// operation — every retry attempt and every backoff wait together. So a client
    /// timeout of 120s with 4 retries does not give each attempt 120s; it gives all five
    /// attempts 120s between them. Against an endpoint whose gateway cuts requests off at
    /// ~60s with a 504 — which <c>electricity/rto/*</c> does routinely — the second
    /// attempt blows the budget and the failure surfaces as
    /// <c>TaskCanceledException</c>/"HTTP request timed out", hiding the fact that the
    /// vendor 504'd. That is a real failure mode this loader hit on its first full run.
    /// </para>
    /// <para>
    /// The fix is the standard one: <see cref="EiaModule"/> sets the client's timeout to
    /// <see cref="Timeout.InfiniteTimeSpan"/> and the budget lives HERE, inside the
    /// retry, so each attempt gets <paramref name="perAttemptTimeoutSeconds"/> of its
    /// own. Nothing can hang indefinitely: the work-unit CTS
    /// (<c>WorkUnitTimeoutSeconds</c>) still cancels the whole unit, and an optimistic
    /// timeout policy observes that same token.
    /// </para>
    ///
    /// <para>
    /// Retries transient faults, <c>408</c>, <c>429</c>, 5xx and a per-attempt timeout,
    /// with exponential backoff. It deliberately does NOT retry:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>400</c> — a malformed request (an unknown <c>data[]</c> column or
    ///         frequency). The body names the valid values; retrying cannot help and
    ///         the catalog is what needs fixing.</item>
    ///   <item><c>403 API_KEY_INVALID</c> — a bad key will not fix itself, and
    ///         hammering a gateway that rate-limits per key is the one thing that makes
    ///         it worse.</item>
    ///   <item><c>404</c> — the route does not exist. For this loader that means the
    ///         CATALOG has drifted from the API, which is a finding, not a blip.</item>
    /// </list>
    /// <para>
    /// <b>⚠ 5xx IS RETRIED, AND ONE EIA 5xx IS NOT TRANSIENT.</b> A <c>local-hourly</c>
    /// request whose <c>start</c>/<c>end</c> lacks a <c>±HH</c> zone suffix answers
    /// <b>HTTP 500</b> deterministically — see <see cref="EiaPeriod.FormatRequestBound"/>.
    /// The loader cannot hit that (bounds are always rendered with <c>-00</c>), but if
    /// a future change reintroduces it the symptom is a window that retries to
    /// exhaustion and then fails, not a hang: the retry count is bounded and
    /// <see cref="EiaSourceReader"/> names the suffix trap in the exception it throws
    /// for a 500 on a zoned frequency.
    /// </para>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> BuildResiliencePipeline(
        int retryCount, int retryDelayMs, int perAttemptTimeoutSeconds, ILogger logger)
    {
        var count = Math.Max(0, retryCount);
        var delay = Math.Max(1, retryDelayMs);
        var budget = Math.Max(1, perAttemptTimeoutSeconds);

        // Optimistic: cancels through the CancellationToken that HttpClient already
        // flows into the handler pipeline. Pessimistic would abandon the request on a
        // background thread and leak a connection per timeout.
        var perAttempt = Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(budget), TimeoutStrategy.Optimistic);

        var retry = HttpPolicyExtensions
            .HandleTransientHttpError()                       // 5xx + HttpRequestException
            .OrResult(r => r.StatusCode is HttpStatusCode.RequestTimeout
                                        or HttpStatusCode.TooManyRequests)
            // The per-attempt timeout above. Without this the policy would time an
            // attempt out and then give up instead of retrying it.
            .Or<TimeoutRejectedException>()
            // A client-side timeout surfaces as TaskCanceledException wrapping a
            // TimeoutException. A genuine cancellation (run abort, unit budget) wraps
            // something else and is NOT matched, so it still propagates immediately
            // rather than being retried against a dead token.
            .Or<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .WaitAndRetryAsync(
                count,
                (attempt, outcome, _) => Backoff(attempt, outcome, delay),
                (outcome, timespan, attempt, _) =>
                {
                    // The request URI is NEVER logged here -- it carries api_key.
                    var reason = outcome.Result is not null
                        ? $"HTTP {(int)outcome.Result.StatusCode}"
                        : outcome.Exception is TimeoutRejectedException
                            ? $"no response within {budget}s (per-attempt budget)"
                            : Redact(outcome.Exception?.Message);

                    // ⚠ DEBUG, not Warning, and that is deliberate. A retry that
                    // succeeds is ROUTINE against this vendor — a 17-hour backfill
                    // running at 99% success emitted thousands of these, drowning out
                    // every sign of progress and making a healthy run look broken. What
                    // deserves an operator's attention is a unit that exhausts its
                    // retries (EiaPipeline logs that at Warning/Error) and the periodic
                    // progress line. Raise this category to Debug in config when
                    // diagnosing the vendor specifically.
                    logger.LogDebug(
                        "EIA HTTP retry {Attempt}/{Count} in {Delay:N0}ms: {Reason}",
                        attempt, count, timespan.TotalMilliseconds, reason);

                    return Task.CompletedTask;
                });

        // Retry OUTSIDE, timeout INSIDE => the budget applies to each attempt.
        return retry.WrapAsync(perAttempt);
    }

    /// <summary>
    /// How long a 503/504 waits relative to everything else.
    ///
    /// <para>
    /// EIA's 503 is NOT a fast gateway rejection — it is the vendor's own backend giving
    /// up on an expensive query, and it takes roughly <b>45 seconds to produce</b>
    /// (measured 2026-10-16: three identical deep-offset requests returned 503 after
    /// 49.0s, 49.9s and 41.6s, as 299 bytes of <c>text/html</c> with
    /// <c>X-Frame-Options: DENY</c> — where a successful response carries
    /// <c>SAMEORIGIN</c> and api.data.gov's rate-limit refusal would be JSON with
    /// <c>OVER_RATE_LIMIT</c>).
    /// </para>
    /// <para>
    /// Retrying such a response one second later asks the same overloaded backend the
    /// same expensive question before it has recovered, which is close to guaranteed to
    /// fail again and adds load while doing it. So an overload answer backs off five
    /// times as far as a 408/429 does.
    /// </para>
    /// </summary>
    private const int OverloadBackoffMultiplier = 5;

    /// <summary>
    /// Exponential backoff with ±25% jitter, scaled up for overload answers.
    ///
    /// <para>
    /// ⚠ <b>The jitter is load-bearing, not decoration.</b> Several work units run
    /// concurrently and all share one API key, so a fixed schedule makes their retries
    /// SYNCHRONISE: a burst of 503s produces a burst of retries exactly
    /// <c>RetryDelayMs</c> later, which produces another burst of 503s. Spreading each
    /// delay breaks that lockstep. Observed in a live run as waves of
    /// "retry 1/4 in 1000ms: HTTP 503" arriving together.
    /// </para>
    /// <para>
    /// <see cref="Random.Shared"/> is thread-safe, which matters because this runs on
    /// whichever worker the retry happens to be on.
    /// </para>
    /// </summary>
    internal static TimeSpan Backoff(int attempt, DelegateResult<HttpResponseMessage> outcome, int baseDelayMs)
    {
        var overloaded = outcome.Result?.StatusCode is HttpStatusCode.ServiceUnavailable
                                                    or HttpStatusCode.GatewayTimeout;

        var scale = overloaded ? OverloadBackoffMultiplier : 1;
        var ms = (double)baseDelayMs * scale * Math.Pow(2, attempt - 1);

        var jittered = ms * (0.75 + (Random.Shared.NextDouble() * 0.5));
        return TimeSpan.FromMilliseconds(jittered);
    }

    /// <summary>
    /// Worst-case seconds one request can occupy — every attempt at its full budget plus
    /// the longest backoff each could draw. Used by the tests to assert the retry budget
    /// fits inside <c>WorkUnitTimeoutSeconds</c>, which is the inequality that keeps a
    /// unit from being cancelled mid-retry and losing the real error.
    /// </summary>
    internal static double WorstCaseSeconds(int retryCount, int retryDelayMs, int perAttemptTimeoutSeconds)
    {
        var attempts = Math.Max(0, retryCount) + 1;
        var backoffMs = 0d;

        for (var i = 1; i <= Math.Max(0, retryCount); i++)
            backoffMs += retryDelayMs * OverloadBackoffMultiplier * Math.Pow(2, i - 1) * 1.25;   // max jitter

        return (attempts * perAttemptTimeoutSeconds) + (backoffMs / 1000d);
    }
}

/// <summary>
/// 🔒 A single account-wide request throttle.
///
/// <para>
/// api.data.gov rate-limits per API KEY. Exceeding the limit does not merely fail the
/// offending request — the documented behaviour is that the key is <b>temporarily
/// suspended</b>, which takes down every other consumer of the same key until it cools
/// off. A full EIA backfill is tens of thousands of requests across
/// <c>MaxConcurrentWorkUnits</c> parallel units, so an unthrottled run is the realistic
/// way to get the organisation's key locked.
/// </para>
/// <para>
/// Registered as a singleton so the gate is shared by every work unit and by the
/// metadata pass: a per-unit limiter would multiply the real rate by the concurrency
/// and defeat the purpose. This is the same posture ICE takes for its hard 30/min
/// account-wide cap.
/// </para>
/// <para>
/// Deliberately simple — a serialised minimum-interval gate, not a token bucket. It
/// cannot burst, which is the conservative direction, and it has no background timer to
/// leak.
/// </para>
/// </summary>
internal sealed class EiaRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _minIntervalTicks;
    private long _lastIssuedTicks;

    public EiaRateLimiter(double requestsPerSecond)
    {
        var rps = requestsPerSecond > 0 ? requestsPerSecond : 1d;
        _minIntervalTicks = (long)(Stopwatch.Frequency / rps);
        _lastIssuedTicks = Stopwatch.GetTimestamp() - _minIntervalTicks;
    }

    /// <summary>Waits until the next request may be issued.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var earliest = _lastIssuedTicks + _minIntervalTicks;

            if (earliest > now)
            {
                var waitMs = (earliest - now) * 1000.0 / Stopwatch.Frequency;
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false);
                now = Stopwatch.GetTimestamp();
            }

            _lastIssuedTicks = now;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
