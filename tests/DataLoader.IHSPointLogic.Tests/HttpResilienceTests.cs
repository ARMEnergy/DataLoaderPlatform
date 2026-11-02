using System.Net;
using System.Net.Http;
using System.Text.Json;
using DataLoader.Core.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The IHSPointLogic HTTP resilience pipeline (<see cref="PlHttpPolicy"/>) and its coupling to
/// <see cref="IHSPointLogicModule"/>'s client registration and to <c>appsettings.json</c>.
///
/// <para>
/// ⚠ <b>The production bug these tests exist for.</b> IHSPointLogic failed ~11 times in 10
/// days, almost always on the "PointMetadata latest" work unit, with "HTTP request timed out
/// (HttpTimeoutSeconds); the work unit budget was not reached" /
/// <c>TaskCanceledException: ... HttpClient.Timeout of 60 seconds elapsing</c>. Three
/// compounding defects: (1) <c>client.Timeout</c> was set to <c>HttpTimeoutSeconds</c>, but a
/// policy from <c>AddPolicyHandler</c> runs INSIDE that timeout, so it bounded every retry
/// attempt TOGETHER rather than one attempt each; (2) the retry predicate never matched the
/// <see cref="TaskCanceledException"/> a client timeout throws, so <c>RetryCount</c> was
/// decorative for this failure mode; (3) <c>PlSourceReader{T}.ReadAsync</c>'s paging loop makes
/// several <c>SendAsync</c> calls per unit, so the shared 60s budget ran out partway through.
/// </para>
/// <para>
/// Same shape as EIA's <c>EiaResilienceTests</c> (that loader hit the identical bug on
/// 2026-10-16), now built on the shared <see cref="PerAttemptHttpTimeout"/> seam instead of a
/// third hand-rolled copy.
/// </para>
/// </summary>
public sealed class HttpResilienceTests
{
    private const int PerAttemptSeconds = 1;

    private static Polly.IAsyncPolicy<HttpResponseMessage> Pipeline(int retryCount = 2) =>
        PlHttpPolicy.Build(retryCount, delayMs: 1, perAttemptTimeoutSeconds: PerAttemptSeconds, NullLogger.Instance);

    // --------------------------------------------------- the per-attempt timeout itself

    /// <summary>
    /// The actual bug, asserted directly: each attempt must get its OWN timeout budget. A
    /// responder that never answers, under a 1-second per-attempt budget and 2 retries, must be
    /// invoked three times — once per attempt — not once total. Under the broken
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
    /// proof the second attempt is not silently starved by whatever budget the first one spent.
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
    /// retried — this is defect #2 from the incident: the predicate used to match only
    /// <c>HandleTransientHttpError</c> + 429, so a timed-out attempt failed outright with no
    /// retry at all, making <c>RetryCount</c> decorative for the one failure mode that actually
    /// occurred in production.
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
    /// 404/401/403 must NOT be retried — these are deliberate, pre-existing behaviours
    /// (<see cref="PlSourceReader{TRow}"/> treats 404 as "no data" and 401/403 as an
    /// auth/entitlement failure with no token to refresh) and this change must not regress
    /// them. None of the three matches <c>HandleTransientHttpError</c>, 429, or the new
    /// <see cref="TimeoutRejectedException"/>/timeout-flavoured predicate branches, so a single
    /// attempt must see exactly one call.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
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

    // ------------------------------------------- the registered HttpClient itself

    /// <summary>
    /// <see cref="IHSPointLogicModule"/> must register its named <see cref="HttpClient"/> with
    /// an INFINITE <c>Timeout</c> — any finite value here would silently reintroduce defect #1
    /// (the client bounding the whole retried operation) no matter how correct
    /// <see cref="PlHttpPolicy"/> is. Exercises the REAL registration path (not a
    /// reimplementation of it): builds a minimal container, calls
    /// <see cref="IHSPointLogicModule.RegisterServices"/>, and asks the resulting
    /// <see cref="IHttpClientFactory"/> for the named client exactly as the pipelines do.
    /// </summary>
    [Fact]
    public void Registered_client_has_an_infinite_timeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:IHSPointLogic:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
                // Real (non-"SEE_DB") values so resolving IOptions<IHSPointLogicSettings> below
                // never reaches ISeeDbParamStore / the platform database (same reasoning as
                // EvolutionMarkets.Tests.ModuleTests.RegisterServices_WiresTheClientAndTheHubWithoutTouchingTheNetwork).
                ["Loaders:IHSPointLogic:ClientId"] = "dummy-client",
                ["Loaders:IHSPointLogic:ClientSecret"] = "dummy-secret",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        new IHSPointLogicModule().RegisterServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(IHSPointLogicModule.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    // ------------------------------------------------- the shipped appsettings.json coupling

    /// <summary>
    /// The worst-case wall time ONE request can occupy —
    /// <c>(RetryCount + 1) × HttpTimeoutSeconds</c> plus exponential backoff — must fit inside
    /// <c>WorkUnitTimeoutSeconds</c>, read from the REAL, SHIPPED
    /// <c>src/DataLoader.Host/appsettings.json</c> rather than from
    /// <see cref="IHSPointLogicSettings"/>'s class defaults. Class defaults can drift from what
    /// is actually deployed without this test noticing; the file cannot.
    ///
    /// <para>
    /// At the shipped values (<c>RetryCount=3</c>, <c>RetryDelayMs=1000</c>,
    /// <c>HttpTimeoutSeconds=120</c>) this is 4 × 120s + (1+2+4)s backoff = 487s, comfortably
    /// under the shipped <c>WorkUnitTimeoutSeconds=900</c>. Raising <c>HttpTimeoutSeconds</c>
    /// alone without also raising <c>WorkUnitTimeoutSeconds</c> would have made the ORIGINAL
    /// bug worse, not better: 4 × 120s = 480s already exceeds the old 300s budget on its own,
    /// before any backoff.
    /// </para>
    /// </summary>
    [Fact]
    public void Shipped_retry_budget_fits_inside_the_shipped_work_unit_budget()
    {
        var (retryCount, retryDelayMs, httpTimeoutSeconds, workUnitTimeoutSeconds) = ReadShippedSettings();

        var worstCaseSeconds = PerAttemptHttpTimeout.WorstCaseSeconds(
            retryCount, httpTimeoutSeconds, attempt => ExponentialBackoff(attempt, retryDelayMs));

        Assert.True(worstCaseSeconds < workUnitTimeoutSeconds,
            $"(RetryCount {retryCount} + 1) x HttpTimeoutSeconds {httpTimeoutSeconds} + backoff = " +
            $"{worstCaseSeconds:N0}s, which does not fit under WorkUnitTimeoutSeconds " +
            $"{workUnitTimeoutSeconds}s in src/DataLoader.Host/appsettings.json's Loaders:IHSPointLogic section.");
    }

    /// <summary>
    /// The same exponential-backoff shape <see cref="PlHttpPolicy.Build"/>'s private
    /// <c>ComputeDelay</c> uses for the deterministic (no <c>Retry-After</c> header) case — kept
    /// here rather than reflected out of the loader because a <c>Retry-After</c>-driven delay is
    /// vendor-controlled and cannot be bounded generically; the exponential floor is the number
    /// this coupling test can meaningfully assert against.
    /// </summary>
    private static TimeSpan ExponentialBackoff(int attempt, int retryDelayMs) =>
        TimeSpan.FromMilliseconds(retryDelayMs * Math.Pow(2, attempt - 1));

    private static (int RetryCount, int RetryDelayMs, int HttpTimeoutSeconds, int WorkUnitTimeoutSeconds) ReadShippedSettings()
    {
        var path = Path.Combine(RepoRoot(), "src", "DataLoader.Host", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var section = doc.RootElement.GetProperty("Loaders").GetProperty("IHSPointLogic");

        return (
            section.GetProperty("RetryCount").GetInt32(),
            section.GetProperty("RetryDelayMs").GetInt32(),
            section.GetProperty("HttpTimeoutSeconds").GetInt32(),
            section.GetProperty("WorkUnitTimeoutSeconds").GetInt32());
    }

    /// <summary>Repo root, found by walking up from the test binaries to the folder holding the .sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DataLoaderPlatform.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
