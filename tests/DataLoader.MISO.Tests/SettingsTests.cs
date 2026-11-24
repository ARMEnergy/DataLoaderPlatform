using System.Net;
using System.Text.Json;
using DataLoader.Core.Resilience;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>Design §8: defaults, appsettings, startup validation and the retry/timeout budget coupling.</summary>
public sealed class SettingsTests
{
    [Fact]
    public void Class_defaults_match_design_section_8()
    {
        var s = new MisoSettings();
        Assert.Equal("https://apim.misoenergy.org/lgi/v1", s.BaseUrl);
        Assert.Equal("SEE_DB", s.ApiKey);
        Assert.Equal(60, s.HttpTimeoutSeconds);
        Assert.Null(s.EnabledFeeds);
        Assert.Equal(new[] { "zonal_load_hourly", "load_hourly", "fuel_mix_hourly", "load_forecast_mtlf" }, s.EffectiveEnabledFeeds);
        Assert.Equal(new DateOnly(2023, 11, 20), s.StartDate);
        Assert.Equal(1095, s.MaxLookbackDays);
        Assert.Equal(10, s.DaysBack);
        Assert.Equal(10, s.SettledAfterDays);
        Assert.Equal(3, s.ExpectRowsWithinDays);
        Assert.Equal(7, s.MtlfHorizonDays);
        Assert.Equal(90, s.RequestsPerMinute);
        Assert.True(s.RequestsPerMinute < MisoSettings.VendorRequestsPerMinute, "default must stay under the vendor quota");
        Assert.Equal(4, s.MaxConcurrentWorkUnits);
        Assert.Equal(3, s.RetryCount);
        Assert.Equal(2000, s.RetryDelayMs);
        Assert.Equal(600, s.WorkUnitTimeoutSeconds);
    }

    private static JsonElement AppSettings() => JsonDocument.Parse(File.ReadAllText(RepoPaths.AppSettings)).RootElement;

    [Fact]
    public void Appsettings_MISO_block_binds_to_the_design_values_with_the_key_left_SEE_DB()
    {
        var config = new ConfigurationBuilder().AddJsonFile(RepoPaths.AppSettings).Build();
        var s = config.GetSection("Loaders:MISO").Get<MisoSettings>()!;
        Assert.Equal("Server=ARMH-OPSDB01;Database=Misc;Integrated Security=SSPI;TrustServerCertificate=True;", s.ConnectionString);
        Assert.Equal("SEE_DB", s.ApiKey);
        Assert.Equal(new DateOnly(2023, 11, 20), s.StartDate);
        Assert.Equal(MisoFeeds.ExecutionOrder, s.EffectiveEnabledFeeds);
        Assert.True(s.RequestsPerMinute < MisoSettings.VendorRequestsPerMinute,
            "shipped RequestsPerMinute must stay under the vendor quota of 100/60s, or most calls draw 429");
        Assert.Equal((60, 1095, 10, 10, 3, 7, 90, 4, 3, 2000, 600),
            (s.HttpTimeoutSeconds, s.MaxLookbackDays, s.DaysBack, s.SettledAfterDays, s.ExpectRowsWithinDays, s.MtlfHorizonDays,
             s.RequestsPerMinute, s.MaxConcurrentWorkUnits, s.RetryCount, s.RetryDelayMs, s.WorkUnitTimeoutSeconds));

        // With the key resolved, the shipped config is valid (including the budget coupling).
        s.ApiKey = Samples.Key;
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void MISO_is_build_only_not_enabled_and_has_no_watchdog_entry()
    {
        var root = AppSettings();
        var platform = root.GetProperty("Platform");
        var enabled = platform.GetProperty("EnabledLoaders").EnumerateArray().Select(e => e.GetString());
        Assert.DoesNotContain("MISO", enabled);

        if (platform.TryGetProperty("Watchdog", out var wd) && wd.TryGetProperty("Loaders", out var loaders))
        {
            if (loaders.ValueKind == JsonValueKind.Object)
                Assert.DoesNotContain(loaders.EnumerateObject(), p => p.Name == "MISO");
            else if (loaders.ValueKind == JsonValueKind.Array)
                Assert.DoesNotContain("\"MISO\"", loaders.GetRawText());
        }
    }

    [Fact]
    public void Appsettings_holds_no_key_value()
    {
        var miso = AppSettings().GetProperty("Loaders").GetProperty("MISO");
        Assert.Equal("SEE_DB", miso.GetProperty("ApiKey").GetString());
    }

    [Fact]
    public void EnabledFeeds_binding_replaces_rather_than_appends()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Loaders:MISO:EnabledFeeds:0"] = "fuel_mix_hourly",
        }).Build();
        var s = config.GetSection("Loaders:MISO").Get<MisoSettings>()!;
        Assert.Equal(new[] { "fuel_mix_hourly" }, s.EffectiveEnabledFeeds);
    }

    [Fact]
    public void Valid_baseline_has_no_errors() => Assert.Empty(Samples.Settings().Validate());

    public static IEnumerable<object[]> Invalid() => new (Action<MisoSettings> Mutate, string Fragment)[]
    {
        (s => s.BaseUrl = "http://apim.misoenergy.org/lgi/v1", "absolute https URL"),
        (s => s.BaseUrl = "lgi/v1", "absolute https URL"),
        (s => s.BaseUrl = "", "absolute https URL"),
        (s => s.ApiKey = "SEE_DB", "ApiKey is not configured"),
        (s => s.ApiKey = " ", "ApiKey is not configured"),
        (s => s.ConnectionString = "", "ConnectionString"),
        (s => s.MtlfHorizonDays = 0, "MtlfHorizonDays must be between 1 and 7"),
        (s => s.MtlfHorizonDays = 8, "MtlfHorizonDays must be between 1 and 7"),
        (s => s.HttpTimeoutSeconds = 0, "HttpTimeoutSeconds must be at least 1"),
        (s => s.MaxLookbackDays = 0, "MaxLookbackDays must be at least 1"),
        (s => s.DaysBack = 0, "DaysBack must be at least 1"),
        (s => s.SettledAfterDays = 0, "SettledAfterDays must be at least 1"),
        (s => s.ExpectRowsWithinDays = 0, "ExpectRowsWithinDays must be at least 1"),
        (s => s.RequestsPerMinute = 0, "RequestsPerMinute must be at least 1"),
        (s => s.MaxConcurrentWorkUnits = 0, "MaxConcurrentWorkUnits must be at least 1"),
        (s => s.WorkUnitTimeoutSeconds = 0, "WorkUnitTimeoutSeconds must be at least 1"),
        (s => s.RetryCount = -1, "RetryCount must be 0 or more"),
        (s => s.RetryDelayMs = -1, "RetryDelayMs must be 0 or more"),
        (s => { s.SettledAfterDays = 2; s.ExpectRowsWithinDays = 3; }, "must be at least ExpectRowsWithinDays"),
        (s => { s.DaysBack = 40; s.MaxLookbackDays = 30; }, "must not exceed MaxLookbackDays"),
        (s => s.EnabledFeeds = new[] { "zonal_load_hourly", "lmp_day_ahead" }, "unknown feed 'lmp_day_ahead'"),
    }.Select(x => new object[] { x.Mutate, x.Fragment });

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Each_rule_has_a_failing_case(Action<MisoSettings> mutate, string fragment)
    {
        var errors = Samples.Settings(mutate).Validate();
        Assert.Contains(errors, e => e.Contains(fragment, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("  abc123  ", "abc123")]
    [InlineData("abc123\r\n", "abc123")]
    [InlineData("\tabc123", "abc123")]
    public void ApiKey_is_trimmed_on_assignment(string raw, string expected)
    {
        var s = Samples.Settings(x => x.ApiKey = raw);
        Assert.Equal(expected, s.ApiKey);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void ApiKey_trimming_also_applies_to_values_from_core_Param()
    {
        using var h = MisoHarness.Create(StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}"),
            store: FakeParamStore.Returning("  " + Samples.Key + "\r\n"));
        Assert.Equal(Samples.Key, h.Settings.ApiKey);
    }

    [Theory]
    [InlineData("abc\n123")]
    [InlineData("abc\u0000123")]
    [InlineData("abc\u007f123")]
    [InlineData("k3y-SENTINEL\r\n-9f8e7d6c5b4a")]
    public void ApiKey_with_control_characters_is_rejected_without_quoting_it(string key)
    {
        var errors = Samples.Settings(x => x.ApiKey = key).Validate();
        var error = Assert.Single(errors);
        Assert.Contains("ApiKey contains control characters", error);
        Assert.DoesNotContain("abc", error);
        Assert.DoesNotContain("SENTINEL", error);
        Assert.DoesNotContain("123", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(" SEE_DB ")]
    public void ApiKey_empty_or_unresolved_is_rejected(string? key)
    {
        var errors = Samples.Settings(x => x.ApiKey = key!).Validate();
        Assert.Contains(errors, e => e.Contains("ApiKey is not configured"));
    }

    [Fact]
    public void Validation_messages_never_quote_the_key()
    {
        var errors = Samples.Settings(s => { s.BaseUrl = "http://x"; s.MtlfHorizonDays = 9; s.WorkUnitTimeoutSeconds = 1; }).Validate();
        Secrets.AssertAbsent(errors, Samples.Key);
    }

    // ---- the retry/timeout budget coupling (design §7) ----------------------------------

    [Fact]
    public void Shipped_budget_fits_inside_the_work_unit_timeout_by_the_design_formula_and_the_stricter_one()
    {
        var s = new MisoSettings();
        // Design literal: (RetryCount+1) × HttpTimeoutSeconds + total backoff < WorkUnitTimeoutSeconds.
        var literal = PerAttemptHttpTimeout.WorstCaseSeconds(s.RetryCount, s.HttpTimeoutSeconds, a => MisoHttpPolicy.Backoff(a, s.RetryDelayMs));
        Assert.Equal(4 * 60 + 2 + 4 + 8, literal);
        Assert.True(literal < s.WorkUnitTimeoutSeconds);

        // Stricter (limiter wait + Retry-After cap per retry): 4 × (60 + 60×4/90) + 3 × 60 ≈ 430.667.
        Assert.Equal(430.667, s.WorstCaseUnitSeconds(), 3);
        Assert.True(s.WorstCaseUnitSeconds() < s.WorkUnitTimeoutSeconds);
    }

    [Theory]
    [InlineData(150, 3, 600)]   // raising the per-attempt budget alone breaks the coupling
    [InlineData(60, 8, 600)]    // so does raising the retry count alone
    [InlineData(60, 3, 400)]    // or lowering the unit budget
    public void Budget_violations_fail_validation(int httpTimeout, int retryCount, int unitTimeout)
    {
        var errors = Samples.Settings(s => { s.HttpTimeoutSeconds = httpTimeout; s.RetryCount = retryCount; s.WorkUnitTimeoutSeconds = unitTimeout; s.RetryDelayMs = 2000; }).Validate();
        Assert.Contains(errors, e => e.Contains("worst-case unit read"));
    }

    [Fact]
    public void Retry_policy_and_backoff_shape_are_what_the_budget_assumes()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2000), MisoHttpPolicy.Backoff(1, 2000));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), MisoHttpPolicy.Backoff(2, 2000));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), MisoHttpPolicy.Backoff(3, 2000));
        Assert.Equal(TimeSpan.FromSeconds(60), MisoHttpPolicy.MaxRetryAfter);
    }

    // ---- module-level: invalid config / unresolved key stops the run before any request -------

    [Fact]
    public async Task Invalid_configuration_fails_the_run_before_any_request()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}");
        using var h = MisoHarness.Create(stub, new Dictionary<string, string?> { ["Loaders:MISO:MtlfHorizonDays"] = "8" });
        var result = await h.RunAsync(new DateTime(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc));
        Assert.False(result.Success);
        Assert.Contains("MtlfHorizonDays", result.ErrorMessage);
        Assert.Equal(0, stub.Count);
        Assert.Empty(h.LoadLog.BegunKeys);
    }

    [Fact]
    public async Task Missing_core_Param_key_fails_the_run_with_a_key_free_message_and_no_request()
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}");
        using var h = MisoHarness.Create(stub, store: FakeParamStore.Returning(null));
        var result = await h.RunAsync(new DateTime(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc));
        Assert.False(result.Success);
        Assert.Contains("SEE_DB", result.ErrorMessage);
        Assert.Contains("ApiKey", result.ErrorMessage);
        Assert.Equal(0, stub.Count);
        Assert.Contains(("MISO", "ApiKey"), h.Store.Asked);
    }

    [Fact]
    public void Environment_override_wins_over_SEE_DB_and_the_store_is_not_asked()
    {
        var store = FakeParamStore.Returning("from-db");
        using var h = MisoHarness.Create(StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}"),
            new Dictionary<string, string?> { ["Loaders:MISO:ApiKey"] = "from-env" }, store);
        Assert.Equal("from-env", h.Settings.ApiKey);
        Assert.DoesNotContain(store.Asked, a => a.Param == "ApiKey");
    }
}
