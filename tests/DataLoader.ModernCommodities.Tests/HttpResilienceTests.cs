using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The ModernCommodities HTTP resilience pipeline (<see cref="ModComHttpPolicy"/>) and its
/// coupling to <see cref="ModernCommoditiesModule"/>'s client registration.
///
/// <para>
/// ⚠ <b>The bug this guards against.</b> <see cref="ModernCommoditiesModule"/> used to set
/// <c>client.Timeout = HttpTimeoutSeconds</c>, but a policy registered via
/// <c>AddPolicyHandler</c> runs INSIDE that timeout, so it bounded the whole retried
/// operation (every attempt and backoff together) rather than one attempt — and the retry
/// predicate never matched the <see cref="TaskCanceledException"/> a client timeout throws,
/// so <c>RetryCount</c> was decorative for exactly that failure mode. EIA (2026-10-16) and
/// IHSPointLogic (2026-11-02) both hit this shape in production; this loader carried the
/// same latent defect without ever having been bitten by it live. See
/// <see cref="DataLoader.Core.Resilience.PerAttemptHttpTimeout"/>.
/// </para>
/// </summary>
public sealed class HttpResilienceTests
{
    private const int PerAttemptSeconds = 1;

    private static Polly.IAsyncPolicy<HttpResponseMessage> Pipeline(int retryCount = 2) =>
        ModComHttpPolicy.Build(retryCount, delayMs: 1, perAttemptTimeoutSeconds: PerAttemptSeconds, NullLogger.Instance);

    // --------------------------------------------------- the per-attempt timeout itself

    /// <summary>
    /// The actual bug, asserted directly: each attempt must get its OWN timeout budget. A
    /// responder that never answers, under a 1-second per-attempt budget and 2 retries, must
    /// be invoked three times — once per attempt — not once total. Under the unfixed
    /// <c>client.Timeout</c> arrangement this responder would be called once and the whole
    /// operation would fail without a single retry ever starting, because the shared client
    /// timeout and the first attempt's hang are the same budget.
    /// </summary>
    [Fact]
    public async Task Each_attempt_gets_its_own_timeout_budget()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            Pipeline(retryCount: 2).ExecuteAsync(async ct =>
            {
                Interlocked.Increment(ref attempts);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // never answers within the budget
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, CancellationToken.None));

        Assert.Equal(3, attempts);   // 1 initial + 2 retries, EACH timed out and retried independently
    }

    /// <summary>
    /// A retry that follows a timed-out attempt must be able to succeed on its own merits —
    /// proof the second attempt is not silently starved by whatever budget the first spent.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_timeout_can_still_succeed()
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 2).ExecuteAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n == 1) await Task.Delay(TimeSpan.FromSeconds(30), ct);   // only the FIRST attempt hangs
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    // ----------------------------------------------------------- what gets retried

    /// <summary>
    /// A <see cref="TimeoutRejectedException"/> (what the per-attempt policy throws) must be
    /// retried. Before this fix the predicate only matched <c>HandleTransientHttpError</c> +
    /// 429, so a timed-out attempt failed outright with no retry at all.
    /// </summary>
    [Fact]
    public async Task TimeoutRejectedException_is_retried()
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 3).ExecuteAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n <= 2) await Task.Delay(TimeSpan.FromSeconds(30), ct);  // first 2 attempts time out
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    /// <summary>
    /// 400/401/403/404 must NOT be retried — these are deliberate, documented behaviours
    /// (<see cref="ModComHttpPolicy"/>'s own remarks): all four are deterministic here (an
    /// over-cap window, a bad credential with no token to refresh, an entitlement gap, and a
    /// deployment-path error respectively), and this change must not regress any of them.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Does_not_retry_the_statuses_that_cannot_clear(HttpStatusCode status)
    {
        var attempts = 0;

        var response = await Pipeline(retryCount: 4).ExecuteAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(status));
        }, CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    // ------------------------------------------- the registered HttpClient itself

    /// <summary>
    /// <see cref="ModernCommoditiesModule"/> must register its named <see cref="HttpClient"/>
    /// with an INFINITE <c>Timeout</c> — any finite value here would silently reintroduce the
    /// bug no matter how correct <see cref="ModComHttpPolicy"/> is. Exercises the REAL
    /// registration path: builds a minimal container, calls
    /// <see cref="ModernCommoditiesModule.RegisterServices"/>, and asks the resulting
    /// <see cref="IHttpClientFactory"/> for the named client exactly as the pipelines do.
    /// </summary>
    [Fact]
    public void Registered_client_has_an_infinite_timeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:ModernCommodities:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
                // Real (non-"SEE_DB") values so resolving IOptions<ModComSettings> below never
                // reaches ISeeDbParamStore / the platform database.
                ["Loaders:ModernCommodities:Username"] = "dummy-user",
                ["Loaders:ModernCommodities:Password"] = "dummy-pass",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        new ModernCommoditiesModule().RegisterServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ModernCommoditiesModule.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
