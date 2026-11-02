using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// The NGI HTTP resilience pipeline (<see cref="NgiHttpPolicy"/>, shared by BOTH the data
/// client and the token-mint client) and its coupling to <see cref="NgiModule"/>'s client
/// registrations.
///
/// <para>
/// ⚠ <b>The bug this guards against.</b> <see cref="NgiModule"/> used to set
/// <c>client.Timeout = HttpTimeoutSeconds</c> on both clients, but a policy registered via
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
        NgiHttpPolicy.Build(retryCount, delayMs: 1, perAttemptTimeoutSeconds: PerAttemptSeconds, NullLogger.Instance);

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
    /// 404/400/401/403 must NOT be retried — 404 is the normal majority outcome of this
    /// monthly feed, 400 is a malformed <c>issue_date</c> (a caller bug), 401 is excluded on
    /// purpose so <see cref="NgiTokenAuthHandler"/> owns the re-mint-once path, and 403 is an
    /// entitlement gap. This change must not regress any of them.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
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

    // ------------------------------------------- the registered HttpClients themselves

    /// <summary>
    /// <see cref="NgiModule"/> must register BOTH its named <see cref="HttpClient"/>s (the
    /// data client and the token-mint client) with an INFINITE <c>Timeout</c> — any finite
    /// value here would silently reintroduce the bug no matter how correct
    /// <see cref="NgiHttpPolicy"/> is. Exercises the REAL registration path: builds a minimal
    /// container, calls <see cref="NgiModule.RegisterServices"/>, and asks the resulting
    /// <see cref="IHttpClientFactory"/> for each named client exactly as the pipelines /
    /// token provider do.
    /// </summary>
    [Theory]
    [InlineData(NgiModule.HttpClientName)]
    [InlineData(NgiModule.TokenClientName)]
    public void Registered_client_has_an_infinite_timeout(string clientName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:NGI:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
                // Real (non-"SEE_DB") values so resolving IOptions<NgiSettings> below never
                // reaches ISeeDbParamStore / the platform database.
                ["Loaders:NGI:Username"] = "dummy-user@example.test",
                ["Loaders:NGI:Password"] = "dummy-pass",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        new NgiModule().RegisterServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
