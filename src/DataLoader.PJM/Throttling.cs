using System.Net;
using System.Net.Http.Headers;
using DataLoader.Core.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace DataLoader.PJM;

/// <summary>
/// Sliding-window-log limiter (design §10.1): at most <c>RequestsPerMinute</c> sends in ANY rolling
/// 60 seconds, shared by both feeds. Keeps the last N send instants under a lock and, when full,
/// waits until the oldest one ages out. Instants are MONOTONIC timestamps
/// (<see cref="TimeProvider.GetTimestamp"/>), so a wall-clock step cannot open or close the window.
///
/// <para>⚠ Deliberately NOT ICE's/AGSI's minimum-interval spacer: a 12-second spacer admits SIX
/// requests in one closed 60-second window (t = 0, 12, 24, 36, 48, 60) — against a documented
/// non-member limit of 6/min on a pool shared with anonymous DM2 users, the margin must not hinge
/// on how PJM rounds its window edge. A small <see cref="EdgeMargin"/> is added for the same
/// reason, so the (N+1)th send is strictly more than 60 s after the 1st.</para>
///
/// <para>Singleton. In the data client it sits INSIDE the retry and OUTSIDE the per-attempt
/// timeout (design §1.3), so every attempt is paced and a limiter wait never eats the request's
/// own budget. The settings.json client bypasses it entirely.</para>
/// </summary>
public sealed class PjmRateLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan EdgeMargin = TimeSpan.FromMilliseconds(250);

    private readonly int _limit;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<long> _sent = new();
    private long _admitted;

    public PjmRateLimiter(IOptions<PjmSettings> settings)
        : this(settings.Value.RequestsPerMinute, TimeProvider.System) { }

    internal PjmRateLimiter(int requestsPerMinute, TimeProvider time)
    {
        _limit = Math.Max(1, requestsPerMinute);
        _time = time;
    }

    /// <summary>Total sends admitted by this limiter (test seam: proves settings.json bypasses it).</summary>
    public long AdmittedCount => Interlocked.Read(ref _admitted);

    /// <summary>Waits until one more send is allowed, then records it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var span = Window + EdgeMargin;
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                while (_sent.Count > 0 && _time.GetElapsedTime(_sent.Peek()) >= span)
                    _sent.Dequeue();

                if (_sent.Count < _limit)
                {
                    _sent.Enqueue(_time.GetTimestamp());
                    Interlocked.Increment(ref _admitted);
                    return;
                }

                wait = span - _time.GetElapsedTime(_sent.Peek());
            }

            if (wait < TimeSpan.FromMilliseconds(1)) wait = TimeSpan.FromMilliseconds(1);
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Delegating handler that paces every data-API request (retries included) through the shared limiter.</summary>
public sealed class PjmRateLimitingHandler : DelegatingHandler
{
    private readonly PjmRateLimiter _limiter;

    public PjmRateLimitingHandler(PjmRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Buffers the response body INSIDE the per-attempt timeout and the retry scope (design §1.3).
/// <c>client.Timeout</c> is infinite, so a body read after the handler chain would be bounded by
/// nothing but the work-unit budget, and a stalled or truncated body would never be retried. Here
/// a stall is cut off by the per-attempt timeout (→ <see cref="TimeoutRejectedException"/>), and a
/// truncated/aborted body surfaces as <see cref="HttpRequestException"/>, which the retry handles.
/// </summary>
public sealed class PjmBufferingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var original = response.Content;
            byte[] bytes;
            try
            {
                bytes = original is null
                    ? Array.Empty<byte>()
                    : await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                // Message names the type only: never any of the (partial) body.
                throw new HttpRequestException($"PJM response body could not be read completely ({ex.GetType().Name})");
            }

            // ⚠ gzip (design §14.11): with AutomaticDecompression on, a wire Content-Length counts the
            // COMPRESSED bytes, so it is never compared with — or copied onto — the decompressed buffer;
            // ByteArrayContent computes its own length. Truncation is still detected where it can be:
            // the connection enforces Content-Length / the chunked terminator on the raw compressed
            // stream and throws while it is read above (→ HttpRequestException → retry), and the
            // reader's JSON parse and totalRows reconciliation catch anything that slips through.
            var buffered = new ByteArrayContent(bytes);
            if (original is not null)
            {
                foreach (var header in original.Headers)
                {
                    if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                original.Dispose();
            }
            response.Content = buffered;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

/// <summary>
/// PJM HTTP resilience (design §6.1). Retries 408 / 429 / 500 / 502 / 503 / 504, network errors
/// (including a truncated body) and the per-attempt timeout, with exponential backoff, honouring
/// <c>Retry-After</c> (delta or date) when longer than the backoff. A response whose
/// <c>Retry-After</c> exceeds <see cref="MaxRetryAfter"/> is NOT retried — it is returned so
/// <see cref="PjmApiClient"/> trips the run guard (the pool is asking for more patience than a
/// unit has). 3xx/400/401/403/404 are never retried: they are classified by the client.
///
/// <para>⚠ 500 is transient only PROVISIONALLY (design §6.1, §13 item 5): on NUCORE and EIA it
/// turned out to mean "malformed request". If API doc §11.5 shows a malformed filter answers 500,
/// drop it from <see cref="IsRetryableStatus"/> before enabling.</para>
///
/// <para>⚠ The per-attempt timeout: clients are registered with
/// <c>client.Timeout = Timeout.InfiniteTimeSpan</c> and the budget lives in a policy, INSIDE the
/// retry, with <see cref="TimeoutRejectedException"/> in the predicate (the EIA/IHSPointLogic
/// lesson; see <see cref="PerAttemptHttpTimeout"/>). The data client composes the pieces
/// separately — retry → limiter → timeout → buffering — so limiter waits do not count against the
/// timeout; the settings.json client (no limiter) uses <see cref="Build"/>.</para>
/// </summary>
internal static class PjmHttpPolicy
{
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>Request option set on the one post-401 re-send: the retry selector returns a no-op (design §5.3).</summary>
    public static readonly HttpRequestOptionsKey<bool> SingleAttempt = new("PJM.SingleAttempt");

    public static bool IsRetryableStatus(HttpStatusCode status) => (int)status is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>The <c>Retry-After</c> hint (delta, or date relative to now), or <c>null</c>.</summary>
    public static TimeSpan? RetryAfterHint(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } when) return when - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>True when the response asks for a wait beyond <see cref="MaxRetryAfter"/> (→ trip, never retry early).</summary>
    public static bool RetryAfterExceedsCap(HttpResponseMessage response) =>
        RetryAfterHint(response.Headers.RetryAfter) is { } h && h > MaxRetryAfter;

    /// <summary>Retry + per-attempt timeout in one policy (the settings.json client, which has no limiter).</summary>
    public static IAsyncPolicy<HttpResponseMessage> Build(
        int retryCount, int delayMs, int perAttemptTimeoutSeconds, ILogger logger, string label) =>
        PerAttemptHttpTimeout.Wrap(BuildRetry(retryCount, delayMs, perAttemptTimeoutSeconds, logger, label), perAttemptTimeoutSeconds);

    /// <summary>The per-attempt timeout alone (innermost policy of the data client).</summary>
    public static IAsyncPolicy<HttpResponseMessage> BuildPerAttemptTimeout(int perAttemptTimeoutSeconds) =>
        PerAttemptHttpTimeout.Build(perAttemptTimeoutSeconds);

    /// <summary>The retry alone (outermost policy of the data client).</summary>
    public static IAsyncPolicy<HttpResponseMessage> BuildRetry(
        int retryCount, int delayMs, int perAttemptTimeoutSeconds, ILogger logger, string label) =>
        Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutRejectedException>()
            .OrResult<HttpResponseMessage>(r => IsRetryableStatus(r.StatusCode) && !RetryAfterExceedsCap(r))
            .WaitAndRetryAsync(
                Math.Max(0, retryCount),
                (attempt, outcome, _) => ComputeDelay(attempt, delayMs, outcome),
                (outcome, delay, attempt, _) =>
                {
                    // Never the response body (it may echo a header) and never a header dump.
                    var reason = outcome.Exception is TimeoutRejectedException
                        ? $"no response within {perAttemptTimeoutSeconds}s (per-attempt budget)"
                        : outcome.Exception is not null
                            ? outcome.Exception.GetType().Name
                            : $"HTTP {(int)outcome.Result!.StatusCode}";

                    logger.LogWarning("{Label} HTTP retry {Attempt}: {Reason}, waiting {Delay}ms",
                        label, attempt, reason, (long)delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });

    /// <summary>
    /// Backoff for attempt N (1-based): <c>delayMs × 2^(N−1)</c>, stretched by a <c>Retry-After</c>
    /// hint when longer. A hint above <see cref="MaxRetryAfter"/> never reaches here (not retried).
    /// </summary>
    internal static TimeSpan ComputeDelay(int attempt, int delayMs, DelegateResult<HttpResponseMessage> outcome)
    {
        var backoff = Backoff(attempt, delayMs);
        if (RetryAfterHint(outcome.Result?.Headers?.RetryAfter) is { } h && h > backoff)
            return h > MaxRetryAfter ? MaxRetryAfter : h;
        return backoff;
    }

    internal static TimeSpan Backoff(int attempt, int delayMs) =>
        TimeSpan.FromMilliseconds(Math.Max(0, delayMs) * Math.Pow(2, Math.Max(0, attempt - 1)));
}
