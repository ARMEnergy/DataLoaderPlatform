using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Process-wide client-side pace limiter (design §4.4). Spaces outgoing requests by
/// <c>1 / RequestsPerSecond</c> so a run stays under a conservative cap. The vendor publishes
/// <b>no</b> rate limit, no <c>Retry-After</c>, no <c>X-RateLimit-*</c> header, and no <c>429</c> was
/// observed across ~60 probe calls — so pace gently (default 5 rps) and back off on <c>429</c>.
///
/// <para>Registered as a singleton so pacing is global across the process and survives
/// <c>HttpClientFactory</c> handler rotation — the delegating handler is transient; the limiter state
/// lives here.</para>
/// </summary>
public sealed class EvoRateLimiter
{
    private readonly double _minIntervalMs;
    private readonly object _gate = new();
    private DateTime _nextAvailableUtc = DateTime.MinValue;

    public EvoRateLimiter(IOptions<EvoSettings> settings)
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
/// Delegating handler that paces every request through the shared limiter — the first attempt and
/// each Polly retry alike. Registered <b>INNERMOST</b> (design §1.4).
/// </summary>
public sealed class EvoRateLimitingHandler : DelegatingHandler
{
    private readonly EvoRateLimiter _limiter;

    public EvoRateLimitingHandler(EvoRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Stamps the API key on <b>every</b> outgoing request as the RAW value of the
/// <c>Authorization</c> header (design §4.1).
///
/// <para><b>⚠ THIS IS NOT HTTP BASIC, DESPITE THE NAME IN THE LOADER REQUEST.</b> The vendor's
/// OpenAPI document declares the scheme as <c>type: apiKey</c>, <c>name: Authorization</c>,
/// <c>in: header</c>, with the example value given as a bare token. The live API was verified to
/// accept exactly that:</para>
/// <code>
///   Authorization: 0bf5…                      -> 200   (correct)
///   (no header)                               -> 401 {"message":"Unauthorized"}
///   Authorization: &lt;wrong key&gt;               -> 403 explicit-deny
/// </code>
/// <para>Do NOT "fix" this to <c>new AuthenticationHeaderValue("Basic", base64(user:pass))</c>, and
/// do not split the key into a username/password pair — there is no username. A Basic-encoded value
/// is rejected.</para>
///
/// <para><b>The header value is set via <c>TryAddWithoutValidation</c></b> because a bare token is
/// not a well-formed <c>Authorization</c> credential per RFC 7235 (it has no
/// <c>scheme SP parameter</c> shape), and the validating setter rejects it with a
/// <see cref="FormatException"/>. This is the one place that matters.</para>
///
/// <para><b>The key is never logged</b> — not here, not by the readers, not on a retry. It is also
/// never placed in a URL, which is why this loader's request URIs are safe to log in full.</para>
/// </summary>
public sealed class EvoApiKeyAuthHandler : DelegatingHandler
{
    private readonly EvoSettings _settings;

    public EvoApiKeyAuthHandler(IOptions<EvoSettings> settings) => _settings = settings.Value;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Re-stamped on every attempt (the handler is transient and sits inside the retry policy),
        // so a retried request never goes out unauthenticated.
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", _settings.ApiKey);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Evolution Markets HTTP retry policy (design §4.4). Treats 5xx / 408 / network errors AND
/// <c>429</c> as transient, honours a <c>Retry-After</c> header when present, and backs off
/// exponentially otherwise.
///
/// <para><b>What is deliberately NOT retried:</b></para>
/// <list type="bullet">
///   <item><c>400</c> — a malformed <c>dateFrom</c>, an out-of-range <c>limit</c>/<c>offset</c>, or a
///     future date. All are caller bugs: unfixable by retry, and they must fail loudly.</item>
///   <item><c>401</c> — a missing or blank API key. There is <b>no token to refresh</b> (this is a
///     static key, not OAuth), so a retry can only produce the same 401.</item>
///   <item><c>403</c> — the key is valid but not entitled. Surface loudly.</item>
///   <item><c>404</c> — not observed on this endpoint; classified as an empty read by the status
///     matrix rather than retried.</item>
/// </list>
///
/// <para><b>⚠ 500 IS retried, and that has a known cost worth stating.</b> This API returns
/// <c>500 {"message":"Something went wrong."}</c> for an <i>unknown</i> <c>field</c> name — a
/// permanent caller bug wearing a transient status code. A typo in
/// <see cref="EvoRequestFields.All"/> therefore burns the full retry budget on every work unit
/// before failing. The status code alone cannot distinguish that from a genuine server blip, so the
/// policy stays correct and <see cref="EvoStatus.Describe"/> names the field list as the first thing
/// to check whenever a 500 survives its retries.</para>
///
/// <para><c>HandleTransientHttpError()</c> covers 5xx/408/network only, so 4xx statuses are excluded
/// by construction — the single explicit <c>OrResult</c> adds <c>429</c> and nothing else.</para>
/// </summary>
internal static class EvoHttpPolicy
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
                        "EvolutionMarkets HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
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
