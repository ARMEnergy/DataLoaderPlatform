using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Polly.Timeout;
using Xunit;

namespace DataLoader.Vulcan.Tests;

/// <summary>
/// The Vulcan HTTP resilience pipeline — assembled inline in
/// <see cref="VulcanModule.RegisterServices"/> from the shared
/// <see cref="DataLoader.Core.Resilience.RetryPolicyFactory.BuildHttpRetryPolicy"/> wrapped in a
/// <see cref="DataLoader.Core.Resilience.PerAttemptHttpTimeout"/>, rather than a loader-local
/// <c>XxxHttpPolicy</c> type — and its coupling to the module's named-client registration.
///
/// <para>
/// Because the policy is built inline rather than behind a standalone, directly callable
/// method, these tests exercise it through the REAL DI-registered pipeline: they call
/// <see cref="VulcanModule.RegisterServices"/> exactly as the host does, then swap in a fake
/// primary <see cref="HttpMessageHandler"/> so no socket is ever touched. A regression in the
/// module's own wiring (wrong nesting order, a dropped <c>TimeoutRejectedException</c> clause,
/// a client timeout reintroduced) would be caught here, unlike a test that merely
/// re-implements the same two calls locally.
/// </para>
///
/// <para>
/// ⚠ <b>The bug this guards against.</b> <see cref="VulcanModule"/> used to set
/// <c>client.Timeout = HttpTimeoutSeconds</c>, but a policy registered via
/// <c>AddPolicyHandler</c> runs INSIDE that timeout, so it bounded the whole retried
/// operation (every attempt and backoff together) rather than one attempt — exactly the shape
/// that bit EIA (2026-10-16) and IHSPointLogic (2026-11-02). See
/// <see cref="DataLoader.Core.Resilience.PerAttemptHttpTimeout"/>.
/// </para>
/// </summary>
public sealed class HttpResilienceTests
{
    /// <summary>
    /// Builds the real container via <see cref="VulcanModule.RegisterServices"/> with a
    /// 1-second <c>HttpTimeoutSeconds</c> and a fast retry delay, then swaps the named client's
    /// primary handler for <paramref name="handler"/> so no socket is ever touched.
    /// </summary>
    private static ServiceProvider BuildProvider(FakeHandler handler, int retryCount = 2)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:Vulcan:BaseUrl"] = "https://vulcan.synmax.test",
                ["Loaders:Vulcan:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
                ["Loaders:Vulcan:RetryCount"] = retryCount.ToString(),
                ["Loaders:Vulcan:RetryDelayMs"] = "1",
                ["Loaders:Vulcan:HttpTimeoutSeconds"] = "1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        new VulcanModule().RegisterServices(services, configuration);

        services.AddHttpClient(VulcanModule.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    private static HttpClient Client(ServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(VulcanModule.HttpClientName);

    /// <summary>Counts attempts and lets each test script the response/delay per attempt.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Attempts;
        public Func<int, CancellationToken, Task<HttpResponseMessage>> Respond =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Attempts);
            return Respond(n, cancellationToken);
        }
    }

    // --------------------------------------------------- the per-attempt timeout itself

    /// <summary>
    /// The actual bug, asserted directly through the real registered pipeline: each attempt
    /// must get its OWN timeout budget. A handler that never answers, under the 1-second
    /// <c>HttpTimeoutSeconds</c> configured above and 2 retries, must be invoked three times —
    /// once per attempt — not once total. Under the unfixed <c>client.Timeout</c> arrangement
    /// this handler would be called once and the whole operation would fail without a single
    /// retry ever starting, because the shared client timeout and the first attempt's hang are
    /// the same budget.
    /// </summary>
    [Fact]
    public async Task Each_attempt_gets_its_own_timeout_budget()
    {
        var handler = new FakeHandler
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // never answers within the budget
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var provider = BuildProvider(handler, retryCount: 2);
        using var client = Client(provider);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            client.GetAsync("https://vulcan.synmax.test/query_datalinks"));

        Assert.Equal(3, handler.Attempts);   // 1 initial + 2 retries, EACH timed out and retried independently
    }

    /// <summary>
    /// A retry that follows a timed-out attempt must be able to succeed on its own merits —
    /// proof the second attempt is not silently starved by whatever budget the first spent.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_timeout_can_still_succeed()
    {
        var handler = new FakeHandler
        {
            Respond = async (n, ct) =>
            {
                if (n == 1) await Task.Delay(TimeSpan.FromSeconds(30), ct);   // only the FIRST attempt hangs
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var provider = BuildProvider(handler, retryCount: 2);
        using var client = Client(provider);

        using var response = await client.GetAsync("https://vulcan.synmax.test/query_datalinks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    // ----------------------------------------------------------- what gets retried

    /// <summary>
    /// A <see cref="TimeoutRejectedException"/> (what the per-attempt policy throws) must be
    /// retried. <see cref="DataLoader.Core.Resilience.RetryPolicyFactory.BuildHttpRetryPolicy"/>
    /// treats it as retryable; before that fix a timed-out attempt failed outright with no
    /// retry at all.
    /// </summary>
    [Fact]
    public async Task TimeoutRejectedException_is_retried()
    {
        var handler = new FakeHandler
        {
            Respond = async (n, ct) =>
            {
                if (n <= 2) await Task.Delay(TimeSpan.FromSeconds(30), ct);   // first 2 attempts time out
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var provider = BuildProvider(handler, retryCount: 3);
        using var client = Client(provider);

        using var response = await client.GetAsync("https://vulcan.synmax.test/query_datalinks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Attempts);
    }

    /// <summary>
    /// 404 must NOT be retried — the shared transient predicate leaves 4xx alone except the
    /// explicit 408, and this change must not regress that.
    /// </summary>
    [Fact]
    public async Task NotFound_is_not_retried()
    {
        var handler = new FakeHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        };
        using var provider = BuildProvider(handler, retryCount: 4);
        using var client = Client(provider);

        using var response = await client.GetAsync("https://vulcan.synmax.test/query_datalinks");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    /// <summary>
    /// ⚠ Existing, pre-fix behaviour this change must preserve exactly: Vulcan never added a
    /// 429 <c>OrResult</c> on top of the shared factory (unlike AGSI/CWG/IIR/etc.), so 429 is
    /// NOT retried here either — only 5xx/408/network/timeout are.
    /// </summary>
    [Fact]
    public async Task TooManyRequests_is_not_retried()
    {
        var handler = new FakeHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))
        };
        using var provider = BuildProvider(handler, retryCount: 4);
        using var client = Client(provider);

        using var response = await client.GetAsync("https://vulcan.synmax.test/query_datalinks");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    // ------------------------------------------- the registered HttpClient itself

    /// <summary>
    /// <see cref="VulcanModule"/> must register its named client with an INFINITE
    /// <c>Timeout</c> — any finite value here would silently reintroduce the bug no matter how
    /// correct the policy wrapping is.
    /// </summary>
    [Fact]
    public void Registered_client_has_an_infinite_timeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:Vulcan:BaseUrl"] = "https://vulcan.synmax.test",
                ["Loaders:Vulcan:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        new VulcanModule().RegisterServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(VulcanModule.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
