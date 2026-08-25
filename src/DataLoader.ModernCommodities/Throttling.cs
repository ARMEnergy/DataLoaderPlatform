using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

namespace DataLoader.ModernCommodities;

/// <summary>
/// Stamps the static HTTP Basic header <c>Authorization: Basic base64(Username:Password)</c> on
/// <b>every</b> outgoing request (design §4.1) — the <c>PlBasicAuthHandler</c> shape.
///
/// <para><b>There is no token.</b> The vendor publishes no mint endpoint, no refresh endpoint, no
/// expiry and nothing to cache — the sharp difference from IIR and NGI, whose designs hinge on a
/// re-mint-once-on-<c>401</c> handler. Consequently there is no token provider, no second un-authed
/// client, <b>no 401 replay</b>, and <c>401</c> is <b>terminal</b>: it means the configured
/// credential is wrong or revoked, and replaying it cannot help. <c>401</c> is also excluded from
/// the retry policy for the same reason (design §4.2).</para>
///
/// <para>Registered <b>INNER of the retry policy</b> (so it re-stamps on every retry attempt) and
/// <b>OUTER of the throttle</b> → <b>retry (OUTER) → auth → throttle (INNER)</b>, the repo standard.
/// The credential is computed once and cached (both values are immutable for a run).</para>
///
/// <para><b>The <c>Authorization</c> header, the base64 credential and both raw values are never
/// logged</b> — not at Trace, not in an exception message, not in a <c>FileLog</c> row. Combined
/// with <c>.RemoveAllLoggers()</c> on the client, no request log line can leak them.</para>
/// </summary>
public sealed class ModComBasicAuthHandler : DelegatingHandler
{
    private readonly ModComSettings _settings;
    private string? _credential;

    public ModComBasicAuthHandler(IOptions<ModComSettings> settings) => _settings = settings.Value;

    private string Credential =>
        _credential ??= Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Credential);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Process-wide client-side pace limiter (design §4.3). Spaces outgoing requests by
/// <c>1 / RequestsPerSecond</c>. The vendor publishes no numeric rate limit, sends no
/// <c>Retry-After</c> and no <c>X-RateLimit-*</c> header, and <b>no <c>429</c> was observed</b>
/// across the probe session — but the PDF states a limit <i>"may be applied to requests that are
/// made too rapidly"</i> and that the API <i>"is not intended to be rapidly polled, but rather
/// queried periodically"</i>. With 3 requests per run, pacing at 1 rps costs nothing and honours
/// that instruction.
///
/// <para>Registered as a singleton so pacing is global across all three pipelines and survives
/// <c>HttpClientFactory</c> handler rotation — the delegating handler is transient; the limiter
/// state lives here.</para>
/// </summary>
public sealed class ModComRateLimiter
{
    private readonly double _minIntervalMs;
    private readonly object _gate = new();
    private DateTime _nextAvailableUtc = DateTime.MinValue;

    public ModComRateLimiter(IOptions<ModComSettings> settings)
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

/// <summary>Delegating handler that paces every request (retries included) through the shared limiter. Registered <b>INNERMOST</b>.</summary>
public sealed class ModComRateLimitingHandler : DelegatingHandler
{
    private readonly ModComRateLimiter _limiter;

    public ModComRateLimitingHandler(ModComRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// ModernCommodities HTTP retry policy (design §4.3). Treats 5xx / 408 / network errors AND
/// <c>429</c> as transient, honours a <c>Retry-After</c> header when present, and backs off
/// exponentially otherwise.
///
/// <para><b>What is deliberately NOT retried — all four are deterministic here, and retrying only
/// delays the real signal:</b></para>
/// <list type="bullet">
///   <item><c>400</c> — an over-cap window, an out-of-range <c>startDate</c>, a malformed date or a
///     bad <c>legalEntityName</c>. Every one is <b>our bug</b>, and the four are distinguishable
///     only by the response <b>body</b>, which the reader reads and classifies.</item>
///   <item><c>401</c> — a wrong or revoked credential. <b>There is no token to refresh</b>, so a
///     replay cannot help; it must surface immediately and loudly.</item>
///   <item><c>403</c> — an entitlement gap.</item>
///   <item><c>404</c> — <b>not a data condition here</b> (contrast NGI, where 404 is the normal
///     majority outcome). It means the path or the <c>/v1</c> version segment is wrong: a deployment
///     error.</item>
/// </list>
/// <c>HandleTransientHttpError()</c> covers 5xx/408/network only, so 4xx statuses are excluded by
/// construction — the single explicit <c>OrResult</c> adds <c>429</c> and nothing else.
/// </summary>
internal static class ModComHttpPolicy
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
                        "ModernCommodities HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
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
