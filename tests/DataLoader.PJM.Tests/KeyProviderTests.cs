using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>Design §12.2 tests 36-42 and 44: key acquisition, the guarded 401 re-fetch, and the limiter bypass.</summary>
public sealed class KeyProviderTests
{
    private const string Old = "CANARY-OLD-KEY-36-9f8e7d6c";
    private const string New = "CANARY-NEW-KEY-36-1a2b3c4d";
    private const string Body = "CANARY-BODY-39-secret-fragment";
    private static readonly DateOnly Day = new(2026, 11, 20);

    private static (PjmKeyProvider Provider, StubHttpMessageHandler Settings, CapturingLoggerFactory Logs) Provider(
        StubHttpMessageHandler settingsStub, FakeParamStore? store = null, string? envOverride = null)
    {
        var logs = new CapturingLoggerFactory();
        var s = Samples.Settings(x => x.ApiKeyOverride = envOverride ?? string.Empty);
        var p = new PjmKeyProvider(s, new HttpClient(settingsStub), store ?? FakeParamStore.Returning(null), logs.CreateLogger("PJM.Key"));
        return (p, settingsStub, logs);
    }

    private static string OkPage() => Samples.Envelope(new[] { Samples.Fuel(Day.ToDateTime(new TimeOnly(1, 0))) });

    // ---- 36 -----------------------------------------------------------------------

    [Fact]
    public async Task T36_Store_null_uses_the_fetched_key_and_logs_the_source_name_only()
    {
        var (p, settings, logs) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old)));

        await p.InitializeAsync(CancellationToken.None);

        Assert.Equal(Old, p.Current.Value);
        Assert.Equal(PjmKeySource.PublicSettings, p.Source);
        Assert.False(p.IsOverride);
        Assert.Equal(1, settings.Count);
        Assert.Contains(logs.OfLevel(LogLevel.Information), e => e.Message.Contains("PublicSettings"));
        Secrets.AssertAbsent(logs.AllText(), Old);
        Assert.DoesNotContain(Old, p.Current.ToString());
    }

    [Fact]
    public async Task T36_Blank_store_value_means_no_override()
    {
        var (p, settings, _) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old)),
            FakeParamStore.Returning("   "));
        await p.InitializeAsync(CancellationToken.None);
        Assert.Equal(PjmKeySource.PublicSettings, p.Source);
        Assert.Equal(1, settings.Count);
    }

    // ---- 37 -----------------------------------------------------------------------

    [Fact]
    public async Task T37_Store_value_is_used_and_settings_json_is_never_requested()
    {
        var store = FakeParamStore.Returning(New);
        var (p, settings, logs) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old)), store);

        await p.InitializeAsync(CancellationToken.None);

        Assert.Equal(New, p.Current.Value);
        Assert.Equal(PjmKeySource.ParamOverride, p.Source);
        Assert.True(p.IsOverride);
        Assert.Equal(0, settings.Count);
        Assert.Equal(1, store.Calls);
        Secrets.AssertAbsent(logs.AllText(), New);
    }

    [Fact]
    public async Task T37_Env_override_beats_the_store()
    {
        var store = FakeParamStore.Returning(New);
        var (p, settings, logs) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old)), store, envOverride: Old);

        await p.InitializeAsync(CancellationToken.None);

        Assert.Equal(Old, p.Current.Value);
        Assert.Equal(PjmKeySource.EnvOverride, p.Source);
        Assert.Equal(0, settings.Count);
        Secrets.AssertAbsent(logs.AllText(), Old, New);
    }

    [Fact]
    public async Task T37_Env_override_of_literal_SEE_DB_is_ignored()
    {
        var (p, _, _) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old)), envOverride: "SEE_DB");
        await p.InitializeAsync(CancellationToken.None);
        Assert.Equal(PjmKeySource.PublicSettings, p.Source);
    }

    // ---- 38 -----------------------------------------------------------------------

    [Fact]
    public async Task T38_Store_failure_fails_acquisition_and_never_falls_back_to_settings_json()
    {
        var (p, settings, _) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old)),
            FakeParamStore.Throwing("Login failed"));

        var ex = await Assert.ThrowsAsync<PjmKeyAcquisitionException>(() => p.InitializeAsync(CancellationToken.None));
        Assert.Contains("core.Param(PJM, ApiKey)", ex.Message);
        Assert.Equal(0, settings.Count);
        Assert.Throws<InvalidOperationException>(() => p.Current);
    }

    // ---- 39 -----------------------------------------------------------------------

    public static IEnumerable<object[]> BadSettingsResponses() => new[]
    {
        new object[] { HttpStatusCode.InternalServerError, "{\"subscriptionKey\":\"" + Body + "\"}" },
        new object[] { HttpStatusCode.NotFound, "<html>" + Body + "</html>" },
        new object[] { HttpStatusCode.OK, "{\"subscriptionKey\":\"" + Body },                // malformed JSON
        new object[] { HttpStatusCode.OK, "not json at all " + Body },
        new object[] { HttpStatusCode.OK, "{\"other\":\"" + Body + "\"}" },                 // missing
        new object[] { HttpStatusCode.OK, "{\"subscriptionKey\":\"   \",\"x\":\"" + Body + "\"}" },  // blank
        new object[] { HttpStatusCode.OK, "{\"subscriptionKey\":42,\"x\":\"" + Body + "\"}" },     // not a string
        new object[] { HttpStatusCode.OK, "{\"SubscriptionKey\":\"" + Body + "\"}" },        // case-sensitive name
        new object[] { HttpStatusCode.OK, "[\"" + Body + "\"]" },
    };

    [Theory]
    [MemberData(nameof(BadSettingsResponses))]
    public async Task T39_Bad_settings_json_fails_acquisition_and_never_quotes_the_body(HttpStatusCode status, string body)
    {
        var settingsStub = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(status, body));
        var (p, _, logs) = Provider(settingsStub);

        var ex = await Assert.ThrowsAsync<PjmKeyAcquisitionException>(() => p.InitializeAsync(CancellationToken.None));

        Assert.Null(ex.InnerException);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Body);
    }

    [Fact]
    public async Task T39_Bad_settings_json_fails_the_run_before_any_unit()
    {
        var data = new StubHttpMessageHandler((r, _) => Samples.OneRowFor(r));
        var settings = StubHttpMessageHandler.Always(HttpStatusCode.OK, "{\"nope\":\"" + Body + "\"}");
        using var h = PjmHarness.Create(data, settings);

        var result = await h.RunAsync();

        Assert.False(result.Success);
        Assert.Equal(0, data.Count);
        Assert.Empty(h.LoadLog.BegunKeys);
        Secrets.AssertAbsent(h.AllRunText(result), Body);
    }

    [Fact]
    public async Task T39_Different_advertised_baseUrl_warns_once_and_the_configured_BaseUrl_is_used()
    {
        var settingsStub = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old, "https://evil.example.com/api/v9"));
        var (p, _, logs) = Provider(settingsStub);
        await p.InitializeAsync(CancellationToken.None);

        var w = Assert.Single(logs.OfLevel(LogLevel.Warning));
        Assert.Contains("evil.example.com", w.Message);
        Assert.Contains("api.pjm.com", w.Message);

        var data = StubHttpMessageHandler.Always(HttpStatusCode.OK, OkPage());
        var (client, _, _, _) = ClientFactory.Build(data, p);
        await client.GetPageAsync(ClientFactory.GenUnit(Day, 3), 1, CancellationToken.None);
        var sent = Assert.Single(data.Requests);
        Assert.Equal("api.pjm.com", sent.Uri.Host);
        Assert.StartsWith("/api/v1/gen_by_fuel", sent.Uri.AbsolutePath);
    }

    [Fact]
    public async Task T39_Same_baseUrl_with_trailing_slash_does_not_warn()
    {
        var (p, _, logs) = Provider(StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(Old, "https://api.pjm.com/api/v1/")));
        await p.InitializeAsync(CancellationToken.None);
        Assert.Empty(logs.OfLevel(LogLevel.Warning));
    }

    // ---- 40 -----------------------------------------------------------------------

    [Fact]
    public async Task T40_401_refetches_once_and_resends_with_the_new_key()
    {
        var settingsStub = StubHttpMessageHandler.Sequence((HttpStatusCode.OK, Samples.SettingsJson(Old)), (HttpStatusCode.OK, Samples.SettingsJson(New)));
        var (p, _, keyLogs) = Provider(settingsStub);
        await p.InitializeAsync(CancellationToken.None);

        var data = new StubHttpMessageHandler((req, _) =>
            req.Headers.GetValues(PjmApiClient.KeyHeader).Single() == New
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, OkPage())
                : StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "{\"statusCode\":401,\"message\":\"Access denied due to invalid subscription key.\"}"));
        var (client, guard, _, logs) = ClientFactory.Build(data, p);

        var page = await client.GetPageAsync(ClientFactory.GenUnit(Day, 3), 1, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(2, settingsStub.Count);
        Assert.Equal(new[] { Old, New }, data.Requests.Select(r => r.KeyHeader));
        Assert.Equal(2, p.Current.Version);
        Assert.False(guard.IsTripped);
        Secrets.AssertAbsent(logs.AllText().Concat(keyLogs.AllText()), Old, New);
    }

    [Fact]
    public async Task T40_Two_concurrent_units_both_getting_401_cause_exactly_one_refetch()
    {
        var settingsStub = StubHttpMessageHandler.Sequence(
            (HttpStatusCode.OK, Samples.SettingsJson(Old)),
            (HttpStatusCode.OK, Samples.SettingsJson(New)),
            (HttpStatusCode.OK, Samples.SettingsJson("CANARY-THIRD-KEY")));
        var (p, _, _) = Provider(settingsStub);
        await p.InitializeAsync(CancellationToken.None);

        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCount = 0;
        var data = new StubHttpMessageHandler(async (req, _) =>
        {
            var key = req.Headers.GetValues(PjmApiClient.KeyHeader).Single();
            if (key == New) return StubHttpMessageHandler.Json(HttpStatusCode.OK, OkPage());
            if (Interlocked.Increment(ref oldCount) >= 2) bothArrived.TrySetResult();
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));   // hold both until both have sent with the OLD key
            return StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "");
        });
        var (client, guard, _, _) = ClientFactory.Build(data, p);

        var a = client.GetPageAsync(ClientFactory.GenUnit(Day, 3), 1, CancellationToken.None);
        var b = client.GetPageAsync(ClientFactory.GenUnit(Day.AddDays(-1), 4), 1, CancellationToken.None);
        await Task.WhenAll(a, b);

        Assert.Equal(2, settingsStub.Count);   // initial fetch + ONE re-fetch
        Assert.Equal(2, data.Requests.Count(r => r.KeyHeader == Old));
        Assert.Equal(2, data.Requests.Count(r => r.KeyHeader == New));
        Assert.False(guard.IsTripped);
    }

    // ---- 41 -----------------------------------------------------------------------

    [Fact]
    public async Task T41_401_then_refetch_then_401_trips_and_names_settings_json_and_core_Param()
    {
        var settingsStub = StubHttpMessageHandler.Sequence((HttpStatusCode.OK, Samples.SettingsJson(Old)), (HttpStatusCode.OK, Samples.SettingsJson(New)));
        var (p, _, _) = Provider(settingsStub);
        await p.InitializeAsync(CancellationToken.None);
        var data = StubHttpMessageHandler.Always(HttpStatusCode.Unauthorized);
        var (client, guard, _, logs) = ClientFactory.Build(data, p);

        var ex = await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(ClientFactory.GenUnit(Day, 3), 1, CancellationToken.None));

        Assert.True(guard.IsTripped);
        Assert.Contains("twice (HTTP 401)", ex.Message);
        Assert.Contains("settings.json", ex.Message);
        Assert.Contains("core.Param(PJM, ApiKey)", ex.Message);
        Assert.Equal(2, settingsStub.Count);
        Assert.Equal(2, data.Count);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()).Append(guard.Reason), Old, New);

        // A later unit sends nothing and does not re-fetch again.
        await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(ClientFactory.GenUnit(Day.AddDays(-1), 4), 1, CancellationToken.None));
        Assert.Equal(2, data.Count);
        Assert.Equal(2, settingsStub.Count);
    }

    [Fact]
    public async Task T41_Refetch_failure_after_401_trips_without_quoting_the_settings_body()
    {
        var settingsStub = StubHttpMessageHandler.Sequence(
            (HttpStatusCode.OK, Samples.SettingsJson(Old)),
            (HttpStatusCode.OK, "{\"subscriptionKey\":\"" + Body));   // malformed on re-fetch
        var (p, _, keyLogs) = Provider(settingsStub);
        await p.InitializeAsync(CancellationToken.None);
        var (client, guard, _, logs) = ClientFactory.Build(StubHttpMessageHandler.Always(HttpStatusCode.Unauthorized), p);

        var ex = await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(ClientFactory.GenUnit(Day, 3), 1, CancellationToken.None));
        Assert.True(guard.IsTripped);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()).Concat(keyLogs.AllText()).Append(guard.Reason), Body, Old);
    }

    // ---- 42 -----------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T42_401_under_an_override_trips_at_once_without_refetch(bool env)
    {
        var settingsStub = StubHttpMessageHandler.Always(HttpStatusCode.OK, Samples.SettingsJson(New));
        var (p, _, _) = env
            ? Provider(settingsStub, envOverride: Old)
            : Provider(settingsStub, FakeParamStore.Returning(Old));
        await p.InitializeAsync(CancellationToken.None);
        var data = StubHttpMessageHandler.Always(HttpStatusCode.Unauthorized);
        var (client, guard, _, logs) = ClientFactory.Build(data, p);

        var ex = await Assert.ThrowsAsync<PjmRunHaltedException>(() => client.GetPageAsync(ClientFactory.GenUnit(Day, 3), 1, CancellationToken.None));

        Assert.True(guard.IsTripped);
        Assert.Equal(1, data.Count);
        Assert.Equal(0, settingsStub.Count);
        Assert.Contains("delete the row to fall back", ex.Message);
        Assert.Null(await p.RefreshAsync(1, CancellationToken.None));
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Old, New);
    }

    // ---- 44 -----------------------------------------------------------------------

    [Fact]
    public async Task T44_settings_json_fetch_and_refetch_bypass_the_rate_limiter()
    {
        var settingsStub = StubHttpMessageHandler.Sequence((HttpStatusCode.OK, Samples.SettingsJson(Old)), (HttpStatusCode.OK, Samples.SettingsJson(New)));
        var data = new StubHttpMessageHandler((req, i) => i == 0
            ? StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "")
            : Samples.OneRowFor(req));
        using var h = PjmHarness.Create(data, settingsStub);
        var limiter = h.Services.GetRequiredService<PjmRateLimiter>();
        var keys = h.Services.GetRequiredService<IPjmKeyProvider>();

        await keys.InitializeAsync(CancellationToken.None);
        Assert.Equal(1, settingsStub.Count);
        Assert.Equal(0, limiter.AdmittedCount);

        var result = await h.RunAsync();   // re-initializes (2nd settings call) then 401 -> one re-fetch (3rd)
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(data.Count, limiter.AdmittedCount);   // every data request paced; no settings request was
        Assert.Equal(3, settingsStub.Count);
    }
}
