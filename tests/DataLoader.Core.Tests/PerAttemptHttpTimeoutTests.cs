using System.Net;
using DataLoader.Core.Resilience;
using Polly;
using Polly.Timeout;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <see cref="PerAttemptHttpTimeout"/> — the shared per-attempt-timeout/retry nesting seam
/// IHSPointLogic now uses instead of adding a third hand-rolled copy of the fix EIA and NUCORE
/// each wrote independently. These tests pin the two things the type promises on its own,
/// isolated from any loader: the timeout fires per call to <c>ExecuteAsync</c>, not once
/// globally, and <see cref="WorstCaseSeconds"/>'s arithmetic matches what it documents.
///
/// Loader-specific behaviour (which statuses/exceptions are transient, backoff shape) is
/// exercised by each loader's own tests (e.g. <c>DataLoader.IHSPointLogic.Tests.HttpResilienceTests</c>,
/// <c>DataLoader.EIA.Tests.EiaResilienceTests</c>) — deliberately not duplicated here.
/// </summary>
public class PerAttemptHttpTimeoutTests
{
    [Fact]
    public async Task Build_TimesOutAnAttemptThatRunsTooLong()
    {
        var policy = PerAttemptHttpTimeout.Build(perAttemptTimeoutSeconds: 1);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            policy.ExecuteAsync(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // never answers within the budget
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Build_LetsAFastAttemptThrough()
    {
        var policy = PerAttemptHttpTimeout.Build(perAttemptTimeoutSeconds: 5);

        var response = await policy.ExecuteAsync(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The nesting order itself. A retry policy whose predicate treats
    /// <see cref="TimeoutRejectedException"/> as transient, wrapped around a 1-second
    /// per-attempt timeout via <see cref="PerAttemptHttpTimeout.Wrap"/>: a responder that
    /// hangs on its first call and answers immediately on its second must succeed on retry —
    /// proof the second attempt gets a FULL budget of its own rather than inheriting whatever
    /// was left of the first one's (which, under the old client.Timeout arrangement, would
    /// have been nothing).
    /// </summary>
    [Fact]
    public async Task Wrap_GivesEveryRetryAttemptItsOwnFullBudget()
    {
        var retry = Policy<HttpResponseMessage>
            .Handle<TimeoutRejectedException>()
            .WaitAndRetryAsync(1, _ => TimeSpan.Zero);

        var pipeline = PerAttemptHttpTimeout.Wrap(retry, perAttemptTimeoutSeconds: 1);

        var attempts = 0;
        var response = await pipeline.ExecuteAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n == 1) await Task.Delay(TimeSpan.FromSeconds(30), ct);   // only the FIRST attempt hangs
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// Without <see cref="TimeoutRejectedException"/> in the retry predicate, wrapping buys
    /// nothing — a timed-out attempt just fails once. This is defect #2 from the
    /// IHSPointLogic incident, reproduced directly against the Core type so the trap documented
    /// on <see cref="PerAttemptHttpTimeout.Wrap"/> cannot silently stop being true.
    /// </summary>
    [Fact]
    public async Task Wrap_WithAPredicateThatIgnoresTheTimeout_DoesNotRetryIt()
    {
        var retryThatIgnoresTimeouts = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()   // deliberately does NOT include TimeoutRejectedException
            .WaitAndRetryAsync(3, _ => TimeSpan.Zero);

        var pipeline = PerAttemptHttpTimeout.Wrap(retryThatIgnoresTimeouts, perAttemptTimeoutSeconds: 1);

        var attempts = 0;
        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            pipeline.ExecuteAsync(async ct =>
            {
                Interlocked.Increment(ref attempts);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void WorstCaseSeconds_IsEveryAttemptsBudgetPlusBackoff()
    {
        // 3 retries => 4 attempts at 10s each = 40s, plus backoff 1+2+4=7s for attempts 1..3.
        var worstCase = PerAttemptHttpTimeout.WorstCaseSeconds(
            retryCount: 3, perAttemptTimeoutSeconds: 10,
            backoffAt: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

        Assert.Equal(47, worstCase);
    }

    [Fact]
    public void WorstCaseSeconds_WithNoRetries_IsJustOneAttemptAndNoBackoff()
    {
        var worstCase = PerAttemptHttpTimeout.WorstCaseSeconds(
            retryCount: 0, perAttemptTimeoutSeconds: 10, backoffAt: _ => TimeSpan.FromHours(1));

        // backoffAt must never be called for attempt 1..0 — there is no backoff before the
        // only attempt there is.
        Assert.Equal(10, worstCase);
    }
}
