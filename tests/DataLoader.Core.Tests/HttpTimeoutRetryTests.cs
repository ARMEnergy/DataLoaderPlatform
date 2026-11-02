using System.Net;
using DataLoader.Core.Resilience;
using Polly;
using Polly.Timeout;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// A client-side request timeout must be retried, and a genuine cancellation
/// must not be. Both arrive as <see cref="TaskCanceledException"/>, so the only
/// thing separating them is the inner exception — which makes this worth pinning
/// from both sides.
///
/// EnergyAspects lost a work unit to an unretried 60s timeout while the same
/// request completed in 13-17s on every other window.
/// </summary>
public class HttpTimeoutRetryTests
{
    private static IAsyncPolicy<HttpResponseMessage> Policy(int retryCount = 3) =>
        RetryPolicyFactory.BuildHttpRetryPolicy(retryCount, delayMs: 1);

    /// <summary>What HttpClient throws when its Timeout expires (.NET 5+).</summary>
    private static TaskCanceledException HttpClientTimeout() =>
        new("The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException("A task was canceled."));

    /// <summary>What HttpClient throws when the caller's token is cancelled.</summary>
    private static TaskCanceledException CallerCancellation() =>
        new("A task was canceled.");

    [Fact]
    public async Task RequestTimeout_IsRetried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Policy().ExecuteAsync(() =>
            {
                attempts++;
                throw HttpClientTimeout();
            }));

        Assert.Equal(4, attempts); // initial + 3 retries
    }

    [Fact]
    public async Task RequestTimeout_ThatRecovers_ReturnsTheGoodResponse()
    {
        var attempts = 0;

        var response = await Policy().ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw HttpClientTimeout();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        // The dangerous direction: retrying a cancelled run would hammer the
        // vendor against a dead token and delay shutdown.
        var attempts = 0;

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Policy().ExecuteAsync(() =>
            {
                attempts++;
                throw CallerCancellation();
            }));

        Assert.Equal(1, attempts); // no retry
    }

    [Fact]
    public async Task TransientHttpErrors_AreStillRetried()
    {
        // Guard the pre-existing behaviour the new predicate sits alongside.
        var attempts = 0;

        await Policy().ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(
                attempts < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task NonTransientStatus_IsNotRetried()
    {
        var attempts = 0;

        var response = await Policy().ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryCountIsHonoured()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Policy(retryCount: 1).ExecuteAsync(() =>
            {
                attempts++;
                throw HttpClientTimeout();
            }));

        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// <see cref="TimeoutRejectedException"/> — what
    /// <see cref="DataLoader.Core.Resilience.PerAttemptHttpTimeout"/> throws — must also be
    /// retried by this shared factory. Every loader built on
    /// <see cref="RetryPolicyFactory.BuildHttpRetryPolicy"/> is migrated to register its
    /// <c>HttpClient</c> with an infinite <c>Timeout</c> and wrap the policy around a
    /// per-attempt timeout instead; without this clause that per-attempt timeout would fire
    /// once and the operation would simply fail, reintroducing the "zero effective retries on
    /// a slow response" defect for every one of this factory's callers at once.
    /// </summary>
    [Fact]
    public async Task TimeoutRejectedException_IsRetried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            Policy().ExecuteAsync(() =>
            {
                attempts++;
                throw new TimeoutRejectedException();
            }));

        Assert.Equal(4, attempts); // initial + 3 retries
    }
}
