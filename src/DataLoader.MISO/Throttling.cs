using System.Net;
using System.Net.Http.Headers;
using DataLoader.Core.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace DataLoader.MISO;

/// <summary>
/// Sliding-window-log limiter (design §7, mirrors PJM's): at most <c>RequestsPerMinute</c> sends in
/// ANY rolling 60 seconds, shared by all four feeds. Instants are MONOTONIC timestamps, so a
/// wall-clock step cannot open or close the window. ⚠ This IS the vendor limit: MISO allows 100
/// calls per 60 s per key and answers 429 beyond it (<see cref="MisoSettings.VendorRequestsPerMinute"/>).
///
/// <para>Singleton. In the client it sits INSIDE the retry and OUTSIDE the per-attempt timeout, so
/// every attempt is paced and a limiter wait never eats the request's own budget.</para>
/// </summary>
public sealed class MisoRateLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan EdgeMargin = TimeSpan.FromMilliseconds(250);

    private readonly int _limit;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<long> _sent = new();
    private long _admitted;

    public MisoRateLimiter(IOptions<MisoSettings> settings)
        : this(settings.Value.RequestsPerMinute, TimeProvider.System) { }

    internal MisoRateLimiter(int requestsPerMinute, TimeProvider time)
    {
        _limit = Math.Max(1, requestsPerMinute);
        _time = time;
    }

    /// <summary>Total sends admitted (test seam).</summary>
    public long AdmittedCount => Interlocked.Read(ref _admitted);

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

/// <summary>Paces every request (retries included) through the shared limiter.</summary>
public sealed class MisoRateLimitingHandler : DelegatingHandler
{
    private readonly MisoRateLimiter _limiter;

    public MisoRateLimitingHandler(MisoRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Buffers the response body INSIDE the per-attempt timeout and the retry scope (as PJM). With
/// <c>client.Timeout</c> infinite, a body read after the handler chain would be bounded only by the
/// work-unit budget; here a stalled body is cut off by the per-attempt timeout and a truncated body
/// surfaces as <see cref="HttpRequestException"/>, both of which the retry handles.
/// </summary>
public sealed class MisoBufferingHandler : DelegatingHandler
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
                // The type only: never any of the (partial) body.
                throw new HttpRequestException($"MISO response body could not be read completely ({ex.GetType().Name})");
            }

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
/// MISO HTTP resilience (design §5, §7). Retries 408 / 429 / 500 / 502 / 503 / 504,
/// <see cref="HttpRequestException"/> and the per-attempt <see cref="TimeoutRejectedException"/>,
/// with exponential backoff stretched by <c>Retry-After</c> (capped at <see cref="MaxRetryAfter"/>).
/// 3xx / 400 / 401 / 403 / 404 are never retried: <see cref="MisoApiClient"/> classifies them.
///
/// <para>⚠ The client is registered with <c>client.Timeout = InfiniteTimeSpan</c>; the per-attempt
/// budget is a policy INSIDE the retry (retry → limiter → timeout → buffering), with
/// <see cref="TimeoutRejectedException"/> in the predicate — the EIA/IHSPointLogic lesson
/// (<see cref="PerAttemptHttpTimeout"/>).</para>
/// </summary>
public static class MisoHttpPolicy
{
    /// <summary>Longest wait a <c>Retry-After</c> may impose on one retry.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    public static bool IsRetryableStatus(HttpStatusCode status) => (int)status is 408 or 429 or 500 or 502 or 503 or 504;

    public static TimeSpan? RetryAfterHint(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } when) return when - DateTimeOffset.UtcNow;
        return null;
    }

    public static IAsyncPolicy<HttpResponseMessage> BuildPerAttemptTimeout(int perAttemptTimeoutSeconds) =>
        PerAttemptHttpTimeout.Build(perAttemptTimeoutSeconds);

    public static IAsyncPolicy<HttpResponseMessage> BuildRetry(int retryCount, int delayMs, int perAttemptTimeoutSeconds, ILogger logger) =>
        Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutRejectedException>()
            .OrResult<HttpResponseMessage>(r => IsRetryableStatus(r.StatusCode))
            .WaitAndRetryAsync(
                Math.Max(0, retryCount),
                (attempt, outcome, _) => ComputeDelay(attempt, delayMs, outcome),
                (outcome, delay, attempt, _) =>
                {
                    // Never the body (it may echo a header) and never a header dump.
                    var reason = outcome.Exception is TimeoutRejectedException
                        ? $"no response within {perAttemptTimeoutSeconds}s (per-attempt budget)"
                        : outcome.Exception is not null
                            ? outcome.Exception.GetType().Name
                            : $"HTTP {(int)outcome.Result!.StatusCode}";
                    logger.LogWarning("MISO HTTP retry {Attempt}: {Reason}, waiting {Delay}ms", attempt, reason, (long)delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });

    /// <summary>Backoff for attempt N, stretched by a <c>Retry-After</c> hint when longer, never past <see cref="MaxRetryAfter"/>.</summary>
    internal static TimeSpan ComputeDelay(int attempt, int delayMs, DelegateResult<HttpResponseMessage> outcome)
    {
        var backoff = Backoff(attempt, delayMs);
        if (RetryAfterHint(outcome.Result?.Headers?.RetryAfter) is { } h && h > backoff)
            return h > MaxRetryAfter ? MaxRetryAfter : h;
        return backoff;
    }

    /// <summary><c>delayMs × 2^(N−1)</c>.</summary>
    internal static TimeSpan Backoff(int attempt, int delayMs) =>
        TimeSpan.FromMilliseconds(Math.Max(0, delayMs) * Math.Pow(2, Math.Max(0, attempt - 1)));
}
