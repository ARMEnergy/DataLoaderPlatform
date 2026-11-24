using System.Net;
using System.Text.Json;
using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>Design §12.2 tests 1-5 (settings and shipped config) plus the §10.2 budget coupling.</summary>
public sealed class SettingsConfigTests
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static JsonElement AppSettingsRoot() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.AppSettings), Lenient).RootElement.Clone();

    private static JsonElement PjmSection() =>
        AppSettingsRoot().GetProperty("Loaders").GetProperty("PJM");

    private static PjmSettings ShippedSettings()
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(File.ReadAllBytes(RepoPaths.AppSettings)))
            .Build();
        var s = new PjmSettings();
        config.GetSection("Loaders:PJM").Bind(s);
        return s;
    }

    // ---- 1 ------------------------------------------------------------------------

    [Fact]
    public void T01_Shipped_appsettings_sets_DaysBack_and_SettledAfterDays_to_10_explicitly()
    {
        var pjm = PjmSection();
        Assert.Equal(10, pjm.GetProperty("DaysBack").GetInt32());
        Assert.Equal(10, pjm.GetProperty("SettledAfterDays").GetInt32());
    }

    // ---- 2 ------------------------------------------------------------------------

    [Fact]
    public void T02_Shipped_PJM_section_has_no_SEE_DB_and_no_key_value()
    {
        var raw = PjmSection().GetRawText();
        Assert.DoesNotContain("SEE_DB", raw, StringComparison.OrdinalIgnoreCase);

        foreach (var p in PjmSection().EnumerateObject())
        {
            Assert.False(p.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase), "Loaders:PJM must not carry ApiKey");
            Assert.False(p.Name.Equals("ApiKeyOverride", StringComparison.OrdinalIgnoreCase), "Loaders:PJM must not carry ApiKeyOverride");
        }
    }

    [Fact]
    public void T02_No_PjmSettings_string_property_defaults_to_SEE_DB()
    {
        var s = new PjmSettings();
        foreach (var p in typeof(PjmSettings).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanRead))
            Assert.NotEqual("SEE_DB", p.GetValue(s) as string);
    }

    [Fact]
    public void T02_SeeDb_resolver_never_touches_the_store_for_the_shipped_PJM_settings()
    {
        var store = FakeParamStore.Throwing("the store must never be called for PJM");
        var resolver = new SeeDbSettingsResolver<PjmSettings>("PJM", store, NullLogger<SeeDbSettingsResolver<PjmSettings>>.Instance);

        var shipped = ShippedSettings();
        var ex = Record.Exception(() => resolver.PostConfigure(null, shipped));
        Assert.Null(ex);

        var defaults = new PjmSettings();
        Assert.Null(Record.Exception(() => resolver.PostConfigure(null, defaults)));
        Assert.Equal(0, store.Calls);
    }

    // ---- 3 ------------------------------------------------------------------------

    [Fact]
    public void T03_PJM_is_not_enabled_and_has_no_watchdog_entry()
    {
        var platform = AppSettingsRoot().GetProperty("Platform");
        var enabled = platform.GetProperty("EnabledLoaders").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain(enabled, n => string.Equals(n, "PJM", StringComparison.OrdinalIgnoreCase));

        var watchdogLoaders = platform.GetProperty("Watchdog").GetProperty("Loaders");
        Assert.DoesNotContain(watchdogLoaders.EnumerateObject(), p => p.Name.Equals("PJM", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 4 ------------------------------------------------------------------------

    [Theory]
    [InlineData(50001)]
    [InlineData(0)]
    public void T04_Validation_rejects_PageSize_out_of_range(int pageSize)
    {
        var errors = new PjmSettings { PageSize = pageSize }.Validate();
        Assert.Contains(errors, e => e.Contains("PageSize"));
    }

    [Theory]
    [InlineData(28, 28)]
    [InlineData(29, 28)]
    public void T04_Validation_rejects_expectation_band_not_below_retention_ceiling(int expect, int max)
    {
        var errors = new PjmSettings { InstLoadExpectRowsWithinDays = expect, InstLoadMaxDaysBack = max }.Validate();
        Assert.Contains(errors, e => e.Contains("InstLoadExpectRowsWithinDays"));
    }

    [Fact]
    public void T04_Validation_rejects_RequestsPerMinute_below_1()
    {
        var errors = new PjmSettings { RequestsPerMinute = 0 }.Validate();
        Assert.Contains(errors, e => e.Contains("RequestsPerMinute"));
    }

    [Fact]
    public void T04_Validation_accepts_the_shipped_appsettings_values()
    {
        Assert.Empty(ShippedSettings().Validate());
    }

    [Fact]
    public async Task T04_Invalid_configuration_fails_the_run_before_any_request()
    {
        var data = new StubHttpMessageHandler((r, _) => Samples.OneRowFor(r));
        var settings = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson("k"));
        using var h = PjmHarness.Create(data, settings, config: new() { ["Loaders:PJM:PageSize"] = "60000" });

        var result = await h.RunAsync();

        Assert.False(result.Success);
        Assert.Contains("PageSize", result.ErrorMessage);
        Assert.Equal(0, data.Count);
        Assert.Equal(0, settings.Count);
    }

    // ---- 5: timeout coupling (design §10.2, the EIA lesson) -----------------------

    /// <summary>
    /// <c>(RetryCount+1) × (HttpTimeoutSeconds + limiterWait) + Σ max(backoff_i, MaxRetryAfter)
    /// + (HttpTimeoutSeconds + limiterWait) &lt; WorkUnitTimeoutSeconds</c>, where the last term is
    /// the one 401 re-send and limiterWait = 60 × MaxConcurrentWorkUnits / RequestsPerMinute.
    /// </summary>
    private static double WorstCaseSeconds(PjmSettings s)
    {
        var limiterWait = 60.0 * s.MaxConcurrentWorkUnits / s.RequestsPerMinute;
        var perAttempt = s.HttpTimeoutSeconds + limiterWait;
        var backoff = Enumerable.Range(1, s.RetryCount)
            .Sum(i => Math.Max(PjmHttpPolicy.Backoff(i, s.RetryDelayMs).TotalSeconds, PjmHttpPolicy.MaxRetryAfter.TotalSeconds));
        return (s.RetryCount + 1) * perAttempt + backoff + perAttempt;
    }

    [Fact]
    public void T05_Retry_budget_fits_inside_the_work_unit_budget_for_class_defaults()
    {
        var s = new PjmSettings();
        var worst = WorstCaseSeconds(s);
        Assert.True(worst < s.WorkUnitTimeoutSeconds,
            $"Worst case {worst:N0}s across {s.RetryCount + 1} attempts + a 401 re-send exceeds WorkUnitTimeoutSeconds {s.WorkUnitTimeoutSeconds}s.");
    }

    [Fact]
    public void T05_Retry_budget_fits_inside_the_work_unit_budget_for_shipped_appsettings()
    {
        var s = ShippedSettings();
        Assert.Equal(3, s.RetryCount);
        Assert.Equal(15000, s.RetryDelayMs);
        Assert.Equal(60, s.HttpTimeoutSeconds);
        Assert.Equal(900, s.WorkUnitTimeoutSeconds);
        var worst = WorstCaseSeconds(s);
        Assert.True(worst < s.WorkUnitTimeoutSeconds, $"Worst case {worst:N0}s >= {s.WorkUnitTimeoutSeconds}s.");
    }

    /// <summary>The inequality assumes no single wait exceeds 60s: a huge Retry-After must be capped.</summary>
    [Fact]
    public void T05_Retry_After_is_capped_so_one_header_cannot_blow_the_unit_budget()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
        var delay = PjmHttpPolicy.ComputeDelay(1, 15000, new DelegateResult<HttpResponseMessage>(response));
        Assert.Equal(PjmHttpPolicy.MaxRetryAfter, delay);

        var shorter = new HttpResponseMessage((HttpStatusCode)429);
        shorter.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(40));
        Assert.Equal(TimeSpan.FromSeconds(40), PjmHttpPolicy.ComputeDelay(1, 15000, new DelegateResult<HttpResponseMessage>(shorter)));
    }

    // ---- EnabledFeeds binding (the feed toggle) ------------------------------------

    /// <summary>
    /// ⚠ The configuration binder APPENDS to a pre-populated collection (the ICE lesson in
    /// CLAUDE.md). <see cref="PjmSettings.EnabledFeeds"/> defaults to both feeds, so an operator who
    /// sets <c>"EnabledFeeds": ["gen_by_fuel"]</c> to switch <c>inst_load</c> off must get exactly
    /// that — otherwise the "feed toggle" (design §10) cannot turn a feed off through config.
    /// </summary>
    [Fact]
    public void Config_EnabledFeeds_replaces_the_class_default_rather_than_appending()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Loaders:PJM:EnabledFeeds:0"] = "gen_by_fuel",
        }).Build();
        var s = new PjmSettings();
        config.GetSection("Loaders:PJM").Bind(s);

        Assert.Equal(new[] { "gen_by_fuel" }, s.EnabledFeeds);
        Assert.Equal(new[] { "gen_by_fuel" }, s.EffectiveEnabledFeeds);
    }
}
