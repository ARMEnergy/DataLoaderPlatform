using System.Net.Http.Headers;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the module's identity, its settings defaults, the auth handler's exact header shape, and the
/// guards that run before any request goes out.
/// </summary>
public class ModuleTests
{
    // ---- identity ------------------------------------------------------------------------------

    [Fact]
    public void Module_IsParameterlessConstructible_AsTheHostRequires()
    {
        // The host instantiates modules with Activator.CreateInstance BEFORE the DI container exists.
        var module = (ILoaderModule)Activator.CreateInstance(typeof(EvolutionMarketsModule))!;
        Assert.Equal("EvolutionMarkets", module.LoaderId);
    }

    [Fact]
    public void LoaderId_MatchesTheConfigurationSectionAndTheDatabaseName()
    {
        // Loaders:EvolutionMarkets in appsettings.json, core.Param(LoaderName='EvolutionMarkets'),
        // and the arm.Endpoint seed all key off this one literal.
        Assert.Equal("EvolutionMarkets", EvolutionMarketsModule.Id);
        Assert.Equal("EvolutionMarkets", new EvolutionMarketsModule().LoaderId);
    }

    [Fact]
    public void DisplayName_IsSet()
    {
        Assert.False(string.IsNullOrWhiteSpace(new EvolutionMarketsModule().DisplayName));
    }

    [Fact]
    public void EndpointId_IsOneSetOfLiterals()
    {
        // EnabledEndpoints, IEvoPipeline.EndpointId and the arm.Endpoint seed name must not drift;
        // arm.usp_UpsertFileLog RAISERRORs on an unknown name.
        Assert.Equal("MarketDataHistory", EvoEndpoints.MarketDataHistory);
        Assert.Equal(new[] { EvoEndpoints.MarketDataHistory }, new EvoSettings().EnabledEndpoints);
    }

    [Fact]
    public void StatusLabels_MatchTheCheckConstraintIn001()
    {
        // arm.Status has CHECK ([Name] IN ('Success','NotAvailable','Failed')).
        Assert.Equal("Success", EvoFileStatus.Success);
        Assert.Equal("NotAvailable", EvoFileStatus.NotAvailable);
        Assert.Equal("Failed", EvoFileStatus.Failed);
    }

    // ---- settings defaults ---------------------------------------------------------------------

    [Fact]
    public void Defaults_MatchTheLoaderSpecAndTheVendorContract()
    {
        var s = new EvoSettings();

        Assert.Equal("https://evolve-api.evomarkets.com", s.BaseUrl);
        Assert.Equal("SEE_DB", s.ApiKey);
        Assert.Equal(30, s.DaysBack);              // "go back 30 days by default"
        Assert.Equal(30, s.SettledAfterDays);      // == DaysBack => all-hot => revisions land
        Assert.Equal(5000, s.PageSize);
        Assert.Equal(EvoHotKeyStrategy.RunDate, s.HotZoneKeyStrategy);
        Assert.Null(s.DatasetName);                // no filter => every permissioned dataset
        Assert.Equal(60, EvoSettings.VendorLookbackDays);
        Assert.Equal(10000, EvoSettings.MaxVendorPageSize);
    }

    [Fact]
    public void SettledAfterDays_DefaultsToAtLeastDaysBack_SoTheSettledZoneStartsEmpty()
    {
        // The invariant that keeps the two silent losses (revisions, late publication) closed by
        // default. If a future edit lowers one without the other, this fails.
        var s = new EvoSettings();
        Assert.True(s.SettledAfterDays >= s.DaysBack,
            "the shipped default must leave the settled zone empty — see EvoSettings.SettledAfterDays");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BaseUrlRoot_CoalescesABlankBinding(string? configured)
    {
        // A JSON null in appsettings would overwrite the initialiser on a non-nullable property, and
        // every request site would then NullReferenceException instead of failing diagnosably.
        var s = new EvoSettings { BaseUrl = configured! };
        Assert.Equal("https://evolve-api.evomarkets.com", s.BaseUrlRoot());
    }

    [Fact]
    public void BaseUrlRoot_StripsATrailingSlash()
    {
        var s = new EvoSettings { BaseUrl = "https://example.test/" };
        Assert.Equal("https://example.test", s.BaseUrlRoot());
    }

    // ---- the auth handler ----------------------------------------------------------------------

    /// <summary>
    /// *** THE AUTH SHAPE. ***
    /// The key goes out as the RAW <c>Authorization</c> header value. It is NOT HTTP Basic, despite
    /// the loader request describing it that way — the vendor's OpenAPI declares
    /// <c>apiKey</c>/<c>Authorization</c>/<c>header</c>, and a Basic-encoded value is rejected with
    /// 403.
    /// </summary>
    [Fact]
    public async Task AuthHandler_SendsTheRawKey_NotHttpBasic()
    {
        var captured = await SendThroughAuthHandlerAsync("my-raw-key");

        Assert.Equal("my-raw-key", captured);
        Assert.DoesNotContain("Basic", captured);
        Assert.DoesNotContain("Bearer", captured);
    }

    [Fact]
    public async Task AuthHandler_DoesNotBase64EncodeTheKey()
    {
        // A base64 of "my-raw-key" would be "bXktcmF3LWtleQ==". Its absence is the assertion.
        var captured = await SendThroughAuthHandlerAsync("my-raw-key");
        Assert.DoesNotContain("bXktcmF3LWtleQ==", captured);
    }

    [Fact]
    public async Task AuthHandler_ReplacesAnyPreexistingHeader_SoARetryIsNeverDoubleStamped()
    {
        var settings = Options.Create(new EvoSettings { ApiKey = "second-key" });
        var handler = new EvoApiKeyAuthHandler(settings) { InnerHandler = new CapturingHandler() };
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/x");
        request.Headers.TryAddWithoutValidation("Authorization", "stale-key");

        using var response = await client.SendAsync(request);

        var values = request.Headers.GetValues("Authorization").ToArray();
        Assert.Single(values);
        Assert.Equal("second-key", values[0]);
    }

    private static async Task<string> SendThroughAuthHandlerAsync(string apiKey)
    {
        var capture = new CapturingHandler();
        var handler = new EvoApiKeyAuthHandler(Options.Create(new EvoSettings { ApiKey = apiKey }))
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://example.test/x");
        return capture.AuthorizationHeader ?? string.Empty;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? AuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeader = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(",", values)
                : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
        }
    }

    // ---- the credential guard ------------------------------------------------------------------

    [Theory]
    [InlineData("SEE_DB")]
    [InlineData("see_db")]
    [InlineData("  SEE_DB  ")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnUnresolvedApiKey_FailsTheRunWithoutMakingARequest(string apiKey)
    {
        // The sentinel comparison is trimmed and case-insensitive because the value can arrive from a
        // hand-edited core.Param row or an env var. Letting it through would surface as an opaque 403
        // on every work unit instead of one actionable message.
        var result = await RunModuleAsync(apiKey);

        Assert.False(result.Success);
        Assert.Contains("ApiKey is not configured", result.ErrorMessage);
    }

    [Fact]
    public async Task TheFailureMessage_NeverEchoesTheKey()
    {
        var result = await RunModuleAsync("a-real-looking-secret-value");

        // A configured key gets past the guard, so this run fails later (no database) — the assertion
        // is only that the secret never appears in any message the module produces.
        Assert.DoesNotContain("a-real-looking-secret-value", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task NoEnabledEndpoints_IsAWarningAndASuccess()
    {
        var result = await RunModuleAsync(ReaderHarness.DummyApiKey, enabledEndpoints: Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Equal(0, result.WorkUnitsTotal);
    }

    [Fact]
    public async Task ANullEnabledEndpointsBinding_DoesNotThrow()
    {
        // The property is non-nullable, so a JSON null in appsettings overwrites the initialiser. The
        // module coalesces it to "run nothing" rather than ArgumentNullException.
        var result = await RunModuleAsync(ReaderHarness.DummyApiKey, enabledEndpoints: null, bindNull: true);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AnUnknownEnabledEndpoint_IsSkippedNotFatal()
    {
        var result = await RunModuleAsync(ReaderHarness.DummyApiKey, enabledEndpoints: new[] { "NoSuchEndpoint" });
        Assert.True(result.Success);
        Assert.Equal(0, result.WorkUnitsTotal);
    }

    /// <summary>
    /// Builds a real DI container from the module's own registrations and runs it, with the pipeline
    /// list left empty so nothing touches the network or a database. Only the guards that run before
    /// pipeline execution are under test here.
    /// </summary>
    private static async Task<LoaderRunResult> RunModuleAsync(
        string apiKey, string[]? enabledEndpoints = null, bool bindNull = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Loaders:EvolutionMarkets:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
            ["Loaders:EvolutionMarkets:ApiKey"] = apiKey
        };

        if (!bindNull)
        {
            var list = enabledEndpoints ?? new[] { EvoEndpoints.MarketDataHistory };
            for (var i = 0; i < list.Length; i++)
                settings[$"Loaders:EvolutionMarkets:EnabledEndpoints:{i}"] = list[i];
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Bind the settings directly rather than via AddLoaderSettings: the SEE_DB resolver would try
        // to reach the platform database, which these tests must never do.
        services.Configure<EvoSettings>(configuration.GetSection("Loaders:EvolutionMarkets"));
        services.AddSingleton<EvolutionMarketsLoadValidator>();

        var provider = services.BuildServiceProvider();

        var module = new EvolutionMarketsModule();
        return await module.RunAsync(provider, new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = new DateTime(2026, 8, 25, 18, 0, 0, DateTimeKind.Utc),
            CancellationToken = CancellationToken.None
        });
    }

    // ---- registration smoke test ---------------------------------------------------------------

    [Fact]
    public void RegisterServices_WiresTheClientAndTheHubWithoutTouchingTheNetwork()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:EvolutionMarkets:ConnectionString"] = "Server=(local);Database=unused;Integrated Security=SSPI;",
                ["Loaders:EvolutionMarkets:ApiKey"] = ReaderHarness.DummyApiKey
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILoadLogRepository>(new ScriptedLoadLog());

        // AddLoaderSettings registers SqlParamStore, which takes IConfiguration from DI.
        services.AddSingleton<IConfiguration>(configuration);

        new EvolutionMarketsModule().RegisterServices(services, configuration);

        // Resolving IOptions<EvoSettings> here is safe and does NOT reach the platform database:
        // SeeDbSettingsResolver short-circuits and never touches the param store unless some string
        // property still holds the literal SEE_DB sentinel, and the configuration above supplies a
        // real value for both ApiKey and ConnectionString.
        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IHttpClientFactory>());
        Assert.NotNull(provider.GetService<EvoRateLimiter>());
        Assert.NotNull(provider.GetService<EvoRateLimitingHandler>());
        Assert.NotNull(provider.GetService<EvoApiKeyAuthHandler>());
    }

    // ---- the rate limiter ----------------------------------------------------------------------

    [Fact]
    public async Task RateLimiter_IsANoOpWhenUnlimited()
    {
        foreach (double? rps in new double?[] { null, 0, -1 })
        {
            var limiter = new EvoRateLimiter(Options.Create(new EvoSettings { RequestsPerSecond = rps }));
            var started = DateTime.UtcNow;
            for (var i = 0; i < 5; i++) await limiter.WaitAsync(CancellationToken.None);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(500));
        }
    }

    [Fact]
    public async Task RateLimiter_PacesRequests()
    {
        // 20 rps => 50ms apart. Three calls should take at least ~100ms in total.
        var limiter = new EvoRateLimiter(Options.Create(new EvoSettings { RequestsPerSecond = 20 }));

        var started = DateTime.UtcNow;
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(80),
            "the limiter did not pace the requests");
    }
}
