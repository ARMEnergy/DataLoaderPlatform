using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiTokenProvider"/> - mints, caches and thread-safely re-mints the JWT from
/// <c>POST /auth</c> (design §4).
///
/// <para><b>The secret is in the request BODY, not the URL</b> - the one substantive difference from
/// the IIR provider this class is modelled on. So the tests below assert (a) the body field is
/// <c>email</c> (NOT <c>username</c>; a body posted with <c>username</c> does not authenticate),
/// (b) nothing secret is ever logged, and (c) <c>refresh</c> is read and immediately DISCARDED (it is
/// a credential and there is no refresh endpoint anywhere in the spec).</para>
///
/// <para>No real credential appears anywhere: the dummies are <c>user@example.test</c> /
/// <c>dummy-password</c>.</para>
/// </summary>
public class TokenProviderTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.body.sig";
    private const string RefreshValue = "eyJyZWZyZXNoIjoidGhpcy1pcy1hLWNyZWRlbnRpYWwifQ.refresh.sig";

    private static NgiSettings Settings() => new()
    {
        BaseUrl = "https://api.ngidata.com",
        AuthPath = "/auth",
        Username = ReaderHarness.DummyUser,
        Password = ReaderHarness.DummyPassword
    };

    private static NgiTokenProvider Provider(HttpMessageHandler handler, ILogger<NgiTokenProvider>? logger = null) =>
        new(new FakeHttpClientFactory(handler), Options.Create(Settings()),
            logger ?? NullLogger<NgiTokenProvider>.Instance);

    /// <summary>A scripted, call-counting mint handler that also captures the request body.</summary>
    private sealed class MintHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _byCall;
        private readonly int _delayMs;
        private int _calls;

        public int Calls => _calls;
        public List<string> Bodies { get; } = new();
        public List<Uri> Uris { get; } = new();

        public MintHandler(Func<int, HttpResponseMessage> byCall, int delayMs = 0)
        {
            _byCall = byCall;
            _delayMs = delayMs;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _calls);
            lock (Uris) Uris.Add(request.RequestUri!);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lock (Bodies) Bodies.Add(body);
            }
            if (_delayMs > 0) await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            return _byCall(n);
        }
    }

    private static HttpResponseMessage BodyToken(string field, string value) =>
        FakeHttpMessageHandler.Json(HttpStatusCode.OK, $"{{\"{field}\":\"{value}\"}}");

    /// <summary>
    /// The documented LIVE body shape - <c>{"refresh": "...", "access_token": "&lt;JWT&gt;"}</c> - plus one
    /// unknown member, because the spec's <c>Token</c> schema declares ONLY <c>access_token</c> and
    /// unknown members must be ignored (never <c>JsonUnmappedMemberHandling.Disallow</c>).
    /// </summary>
    private static HttpResponseMessage LiveShapedResponse(string accessToken = Jwt, string refresh = RefreshValue) =>
        FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $"{{\"refresh\":\"{refresh}\",\"unknown_future_member\":\"ignored\",\"access_token\":\"{accessToken}\"}}");

    // ================================================== tolerant extraction

    [Theory]
    [InlineData("access_token")]
    [InlineData("accessToken")]
    [InlineData("token")]
    [InlineData("jwt")]
    [InlineData("ACCESS_TOKEN")]   // case-insensitive
    [InlineData("Access_Token")]
    public async Task GetToken_ExtractsFromAnyCandidateBodyField(string field)
    {
        var provider = Provider(new MintHandler(_ => BodyToken(field, Jwt)));
        Assert.Equal(Jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_CandidateOrder_PrefersAccessToken()
    {
        // access_token is FIRST in the candidate list, so it wins over a same-body `token`.
        var handler = new MintHandler(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $"{{\"token\":\"wrong-one\",\"access_token\":\"{Jwt}\"}}"));

        Assert.Equal(Jwt, await Provider(handler).GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_StripsALeadingBearerPrefix()
    {
        var provider = Provider(new MintHandler(_ => BodyToken("access_token", "Bearer " + Jwt)));
        Assert.Equal(Jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_FallsBackToTheAuthorizationResponseHeader()
    {
        var handler = new MintHandler(_ =>
        {
            var r = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"message":"ok"}""");
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Jwt);
            return r;
        });

        Assert.Equal(Jwt, await Provider(handler).GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_AcceptsABareJsonStringBody()
    {
        var handler = new MintHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"\"{Jwt}\"", Encoding.UTF8, "application/json")
        });

        Assert.Equal(Jwt, await Provider(handler).GetTokenAsync(CancellationToken.None));
    }

    // ================================================== refresh is captured and IGNORED

    [Fact]
    public async Task Mint_ReadsRefresh_ButNeverReturnsOrCachesIt()
    {
        var handler = new MintHandler(_ => LiveShapedResponse());
        var provider = Provider(handler);

        var token = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(Jwt, token);
        Assert.NotEqual(RefreshValue, token);
        // The refresh credential is discarded immediately - there is no refresh endpoint in the spec,
        // and a re-mint is a fresh POST /auth (proved by the extra call below, not by a refresh grant).
        Assert.Equal(1, handler.Calls);
        await provider.RefreshTokenAsync(token, CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, b => Assert.DoesNotContain(RefreshValue, b)); // never replayed anywhere
    }

    // ================================================== the credential travels in the BODY as "email"

    [Fact]
    public void BuildAuthBody_UsesEmailNotUsername_AndIsValidJson()
    {
        var body = Provider(new MintHandler(_ => LiveShapedResponse())).BuildAuthBody();

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ReaderHarness.DummyUser, doc.RootElement.GetProperty("email").GetString());
        Assert.Equal(ReaderHarness.DummyPassword, doc.RootElement.GetProperty("password").GetString());

        // NGITokenObtainPair is required:[email,password] - a body posted with `username` will not
        // authenticate, so the setting name (Username) must NOT leak into the wire field name.
        Assert.False(doc.RootElement.TryGetProperty("username", out _));
        Assert.DoesNotContain("username", body);
    }

    [Fact]
    public void BuildAuthBody_EscapesAwkwardCredentialCharacters()
    {
        var settings = Settings();
        settings.Password = "dummy\"pass\\word";
        var provider = new NgiTokenProvider(
            new FakeHttpClientFactory(new MintHandler(_ => LiveShapedResponse())),
            Options.Create(settings), NullLogger<NgiTokenProvider>.Instance);

        using var doc = JsonDocument.Parse(provider.BuildAuthBody()); // must not be malformed JSON
        Assert.Equal("dummy\"pass\\word", doc.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Mint_PostsTheCredentialInTheBody_NeverInTheUri()
    {
        var handler = new MintHandler(_ => LiveShapedResponse());

        await Provider(handler).GetTokenAsync(CancellationToken.None);

        var uri = Assert.Single(handler.Uris);
        Assert.Equal("https://api.ngidata.com/auth", uri.ToString());
        Assert.DoesNotContain(ReaderHarness.DummyUser, uri.ToString());
        Assert.DoesNotContain(ReaderHarness.DummyPassword, uri.ToString());

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"email\"", body);
        Assert.Contains(ReaderHarness.DummyUser, body);   // it really did reach the wire ...
    }

    [Fact]
    public async Task Mint_ResolvesTheDedicatedUnAuthedTokenClient()
    {
        var factory = new FakeHttpClientFactory(new MintHandler(_ => LiveShapedResponse()));
        var provider = new NgiTokenProvider(factory, Options.Create(Settings()), NullLogger<NgiTokenProvider>.Instance);

        await provider.GetTokenAsync(CancellationToken.None);

        // "NGI.Token" must NOT carry the auth handler (that would recurse).
        Assert.Equal(NgiModule.TokenClientName, Assert.Single(factory.NamesRequested));
        Assert.NotEqual(NgiModule.HttpClientName, NgiModule.TokenClientName);
    }

    // ================================================== NOTHING SECRET IS EVER LOGGED

    [Fact]
    public async Task Mint_LogsNothingSecret_NotTheCredentials_NotTheToken_NotTheRefresh()
    {
        var captured = new ListLogger();
        var provider = Provider(new MintHandler(_ => LiveShapedResponse()), new TypedListLogger<NgiTokenProvider>(captured));

        var token = await provider.GetTokenAsync(CancellationToken.None);
        await provider.RefreshTokenAsync(token, CancellationToken.None);

        Assert.NotEmpty(captured.Entries);   // it does log SOMETHING (a fixed sanitised line) ...

        foreach (var message in captured.Messages)
        {
            Assert.DoesNotContain(ReaderHarness.DummyUser, message);
            Assert.DoesNotContain(ReaderHarness.DummyPassword, message);
            Assert.DoesNotContain(Jwt, message);
            Assert.DoesNotContain(RefreshValue, message);
            Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MintFailure_ThrowsWithRemediationButNeverTheCredentialValues()
    {
        var captured = new ListLogger();
        var provider = Provider(
            new MintHandler(_ => FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"detail":"no"}""")),
            new TypedListLogger<NgiTokenProvider>(captured));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(CancellationToken.None));

        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("core.Param", ex.Message);                       // names the rows to fix ...
        Assert.DoesNotContain(ReaderHarness.DummyUser, ex.Message);      // ... never the values
        Assert.DoesNotContain(ReaderHarness.DummyPassword, ex.Message);
        Assert.All(captured.Messages, m => Assert.DoesNotContain(ReaderHarness.DummyPassword, m));
    }

    [Fact]
    public async Task Mint_TokenlessSuccessResponse_Throws()
    {
        var provider = Provider(new MintHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"message":"ok"}""")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(CancellationToken.None));
        Assert.Contains("access_token", ex.Message);
    }

    // ================================================== caching + thread safety

    [Fact]
    public async Task GetToken_CachesAfterTheFirstMint()
    {
        var handler = new MintHandler(_ => LiveShapedResponse());
        var provider = Provider(handler);

        await provider.GetTokenAsync(CancellationToken.None);
        await provider.GetTokenAsync(CancellationToken.None);
        await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);   // one mint per process (exp - iat = 24h, far beyond any run)
    }

    [Fact]
    public async Task GetToken_ConcurrentFirstCallers_MintExactlyOnce()
    {
        // The delay makes 64 callers pile up on the semaphore, so a missing double-check would mint
        // many times. Both pipelines share this singleton, which is why this matters.
        var handler = new MintHandler(_ => LiveShapedResponse(), delayMs: 25);
        var provider = Provider(handler);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => provider.GetTokenAsync(CancellationToken.None)));

        Assert.Equal(1, handler.Calls);
        Assert.All(tokens, t => Assert.Equal(Jwt, t));
    }

    [Fact]
    public async Task RefreshToken_ThunderingHerd_MintsExactlyOnceMore()
    {
        // Each mint returns a distinct token so the number of mints is observable.
        var handler = new MintHandler(n => LiveShapedResponse($"{Jwt}-{n}"), delayMs: 25);
        var provider = Provider(handler);

        var first = await provider.GetTokenAsync(CancellationToken.None);
        Assert.Equal($"{Jwt}-1", first);

        var refreshed = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => provider.RefreshTokenAsync(first, CancellationToken.None)));

        Assert.Equal(2, handler.Calls);                          // exactly ONE additional mint
        Assert.All(refreshed, t => Assert.Equal($"{Jwt}-2", t)); // everyone got the same fresh token
    }

    [Fact]
    public async Task RefreshToken_WhenTheCacheAlreadyMovedOn_DoesNotReMint()
    {
        var handler = new MintHandler(n => LiveShapedResponse($"{Jwt}-{n}"));
        var provider = Provider(handler);

        await provider.GetTokenAsync(CancellationToken.None);            // mint #1 -> "…-1"
        var fresh = await provider.RefreshTokenAsync($"{Jwt}-1", CancellationToken.None); // mint #2 -> "…-2"
        Assert.Equal(2, handler.Calls);

        // A late 401 caller still holding the ORIGINAL stale token gets the cached fresh one, no mint.
        var late = await provider.RefreshTokenAsync($"{Jwt}-1", CancellationToken.None);
        Assert.Equal(fresh, late);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AuthPath_IsConfigurableAndNormalised()
    {
        var settings = Settings();
        settings.AuthPath = "auth";  // no leading slash
        var handler = new MintHandler(_ => LiveShapedResponse());
        var provider = new NgiTokenProvider(new FakeHttpClientFactory(handler), Options.Create(settings),
            NullLogger<NgiTokenProvider>.Instance);

        await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal("https://api.ngidata.com/auth", Assert.Single(handler.Uris).ToString());
    }
}
