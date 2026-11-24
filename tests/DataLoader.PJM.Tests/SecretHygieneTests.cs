using System.Net;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// 🔒 Design §12.2 test 43 (design §5.4): with canary key values, no captured log line, no exception
/// message (inner exceptions and stack text included), no core.LoadLog error string, no resume key,
/// no run result and no URL contains a key. The key travels only in the
/// <c>Ocp-Apim-Subscription-Key</c> header. Exercised end to end through the real module wiring on
/// the 401 → re-fetch → 200 path and the 401 → 401 failure path, with vendor bodies that ECHO the key.
/// </summary>
public sealed class SecretHygieneTests
{
    private const string Old = "CANARY-43-OLD-7c1e2f0a9b";
    private const string New = "CANARY-43-NEW-4d5e6f7a8b";

    private static string Echo401(string? key) =>
        "{\"statusCode\":401,\"message\":\"Access denied due to invalid subscription key " + key + ". Make sure to provide a valid key.\"}";

    [Fact]
    public async Task T43_401_refetch_200_path_leaks_no_key_anywhere()
    {
        var settings = StubHttpMessageHandler.Sequence((HttpStatusCode.OK, Samples.SettingsJson(Old)), (HttpStatusCode.OK, Samples.SettingsJson(New)));
        var data = new StubHttpMessageHandler((req, _) =>
        {
            var key = req.Headers.GetValues(PjmApiClient.KeyHeader).Single();
            return key == New ? Samples.OneRowFor(req) : StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, Echo401(key));
        });
        using var h = PjmHarness.Create(data, settings);

        var result = await h.RunAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, settings.Count);
        Assert.Equal(Old, data.Requests.OrderBy(r => r.Index).First().KeyHeader);
        Assert.All(data.Requests.OrderBy(r => r.Index).Skip(1), r => Assert.Equal(New, r.KeyHeader));
        Assert.All(data.Requests, r => Assert.DoesNotContain("CANARY", r.Uri.ToString()));
        Secrets.AssertAbsent(h.AllRunText(result), Old, New);
    }

    [Fact]
    public async Task T43_401_then_401_failure_path_leaks_no_key_anywhere()
    {
        var settings = StubHttpMessageHandler.Sequence((HttpStatusCode.OK, Samples.SettingsJson(Old)), (HttpStatusCode.OK, Samples.SettingsJson(New)));
        var data = new StubHttpMessageHandler((req, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, Echo401(req.Headers.GetValues(PjmApiClient.KeyHeader).Single())));
        using var h = PjmHarness.Create(data, settings);

        var result = await h.RunAsync();

        Assert.False(result.Success);
        Assert.True(h.Guard.IsTripped);
        Assert.Contains("settings.json", h.Guard.Reason);
        Assert.Equal(2, data.Count);
        Assert.Equal(new[] { Old, New }, data.Requests.OrderBy(r => r.Index).Select(r => r.KeyHeader));
        Assert.NotEmpty(h.LoadLog.Failures);
        Assert.NotEmpty(result.FailedUnits);
        Secrets.AssertAbsent(h.AllRunText(result), Old, New);
    }

    /// <summary>Every request-level failure shape whose vendor text echoes the key, through the real client.</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(418)]
    [InlineData(503)]
    [InlineData(200)]   // errors[] in-band
    public async Task T43_Vendor_text_echoing_the_key_is_redacted_from_exceptions_and_logs(int status)
    {
        var body = status == 200
            ? "{\"items\":[],\"totalRows\":0,\"errors\":[\"rejected key=" + Old + "\"]}"
            : "{\"message\":\"key " + Old + " refused\",\"Ocp-Apim-Subscription-Key\":\"" + Old + "\"}";
        var stub = StubHttpMessageHandler.Always((HttpStatusCode)status, body);
        var (client, guard, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Old));

        var ex = await Record.ExceptionAsync(() => client.GetPageAsync(ClientFactory.GenUnit(new DateOnly(2026, 11, 20), 3), 1, CancellationToken.None));

        Assert.NotNull(ex);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()).Append(guard.Reason), Old);
    }

    /// <summary>A transport failure whose message quotes the key is redacted (the exception is not wrapped raw).</summary>
    [Fact]
    public async Task T43_Transport_exception_text_is_redacted_and_not_kept_as_an_inner_exception()
    {
        var stub = new StubHttpMessageHandler((Func<HttpRequestMessage, int, HttpResponseMessage>)((_, _) =>
            throw new HttpRequestException("connection reset while sending header value " + Old)));
        var (client, _, _, logs) = ClientFactory.Build(stub, new FakeKeyProvider(Old));

        var ex = await Assert.ThrowsAsync<PjmRequestException>(() =>
            client.GetPageAsync(ClientFactory.GenUnit(new DateOnly(2026, 11, 20), 3), 1, CancellationToken.None));

        Assert.Equal(PjmFailureClass.TransientExhausted, ex.FailureClass);
        Assert.Equal(4, stub.Count);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), Old);
    }

    [Fact]
    public async Task T43_Store_exception_text_is_redacted_from_the_acquisition_failure()
    {
        // A store failure message is not a PJM key, but a connection-string password in it must not leak either.
        var store = FakeParamStore.Throwing("Login failed; Password=" + Old);
        var p = new PjmKeyProvider(Samples.Settings(), new HttpClient(StubHttpMessageHandler.Always(HttpStatusCode.OK, "{}")), store, ClientFactory.Null);
        var ex = await Assert.ThrowsAsync<PjmKeyAcquisitionException>(() => p.InitializeAsync(CancellationToken.None));
        Secrets.AssertAbsent(Secrets.ExceptionText(ex), Old);
    }

    [Fact]
    public void T43_PjmKey_ToString_never_prints_the_value()
    {
        var k = new PjmKey(Old, 3);
        Assert.DoesNotContain(Old, k.ToString());
        Assert.DoesNotContain(Old, $"{k}");
    }

    [Fact]
    public void T43_Request_URL_never_carries_the_key()
    {
        var (client, _, _, _) = ClientFactory.Build(StubHttpMessageHandler.Always(HttpStatusCode.OK), new FakeKeyProvider(Old));
        var uri = client.BuildUri(ClientFactory.LoadUnit(new DateOnly(2026, 11, 1), 3), 1);
        Assert.DoesNotContain(Old, uri.ToString());
        Assert.DoesNotContain("subscription", uri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "https://api.pjm.com/api/v1/inst_load?rowCount=50000&startRow=1&sort=datetime_beginning_utc&order=Asc" +
            "&datetime_beginning_utc=11%2F01%2F2026%2000%3A00to11%2F01%2F2026%2023%3A59&isActiveMetadata=true",
            uri.AbsoluteUri);
    }
}
