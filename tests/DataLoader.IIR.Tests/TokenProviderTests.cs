using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirTokenProvider"/> — mints, caches and thread-safely re-mints the JWT (design §3). The
/// token is read TOLERANTLY from the response body (a field named token/access_token/accessToken/jwt,
/// case-insensitive) OR a response header; a leading <c>Bearer </c> is stripped; a non-2xx or a
/// token-less response throws. Concurrency: the double-checked <see cref="System.Threading.SemaphoreSlim"/>
/// mints exactly once for a burst of first callers, and <c>RefreshTokenAsync</c> avoids a thundering herd.
/// HTTP is a counting fake handler — no network.
/// </summary>
public class TokenProviderTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.body.sig";

    private static IirSettings Settings() => new()
    {
        BaseUrl = "https://apitest.industrialinfo.com",
        Version = "v2.7",
        Username = "user",
        Password = "pass",
        TokenEndpoint = "",
        TokenLifetimeDays = 1
    };

    private static IirTokenProvider Provider(HttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), Options.Create(Settings()), NullLogger<IirTokenProvider>.Instance);

    /// <summary>A scripted, call-counting mint handler.</summary>
    private sealed class MintHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _byCall;
        private readonly int _delayMs;
        private int _calls;
        public int Calls => _calls;
        public MintHandler(Func<int, HttpResponseMessage> byCall, int delayMs = 0) { _byCall = byCall; _delayMs = delayMs; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _calls);
            if (_delayMs > 0) await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            return _byCall(n);
        }
    }

    private static HttpResponseMessage BodyToken(string field, string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{{ \"{field}\": \"{value}\" }}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage HeaderToken(string headerName, string value)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ \"message\": \"Token Created successfully.\" }", Encoding.UTF8, "application/json") };
        r.Headers.TryAddWithoutValidation(headerName, value);
        return r;
    }

    // ---------------------------------------------------------------- body-field extraction (tolerant)

    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("accessToken")]
    [InlineData("jwt")]
    [InlineData("ACCESS_TOKEN")] // case-insensitive
    public async Task GetToken_ExtractsFromAnyBodyFieldName_CaseInsensitive(string field)
    {
        var provider = Provider(new MintHandler(_ => BodyToken(field, Jwt)));
        Assert.Equal(Jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_StripsBearerPrefixFromBody()
    {
        var provider = Provider(new MintHandler(_ => BodyToken("token", "Bearer " + Jwt)));
        Assert.Equal(Jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- header extraction (tolerant)

    [Fact]
    public async Task GetToken_ExtractsFromAuthorizationResponseHeader_StripsBearer()
    {
        var provider = Provider(new MintHandler(_ => HeaderToken("Authorization", "Bearer " + Jwt)));
        Assert.Equal(Jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_ExtractsFromAnyHeaderLookingLikeAJwt()
    {
        // A custom header whose value starts with "ey…" is accepted as the token.
        var provider = Provider(new MintHandler(_ => HeaderToken("X-Access-Token", Jwt)));
        Assert.Equal(Jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public async Task GetToken_NonSuccessStatus_Throws()
    {
        var provider = Provider(new MintHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("bad creds") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetToken_NoTokenInBodyOrHeader_Throws()
    {
        var provider = Provider(new MintHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{ \"message\": \"ok\" }", Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- caching + concurrency

    [Fact]
    public async Task GetToken_CachesAfterFirstMint_SecondCallDoesNotReMint()
    {
        var handler = new MintHandler(_ => BodyToken("token", Jwt));
        var provider = Provider(handler);

        await provider.GetTokenAsync(CancellationToken.None);
        await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls); // cached — minted once
    }

    [Fact]
    public async Task GetToken_ConcurrentFirstCallers_MintExactlyOnce()
    {
        // A small delay makes the 32 callers pile up on the semaphore so a missing double-check would
        // mint many times; the guard means exactly one mint happens.
        var handler = new MintHandler(_ => BodyToken("token", Jwt), delayMs: 25);
        var provider = Provider(handler);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => provider.GetTokenAsync(CancellationToken.None)));

        Assert.Equal(1, handler.Calls);
        Assert.All(tokens, t => Assert.Equal(Jwt, t));
    }

    [Fact]
    public async Task RefreshToken_OnlyReMintsWhenCachedStillEqualsStale_ThunderingHerdMintsOnce()
    {
        // Each mint returns a distinct token so we can see how many mints occurred.
        var handler = new MintHandler(n => BodyToken("token", $"{Jwt}-{n}"), delayMs: 25);
        var provider = Provider(handler);

        var first = await provider.GetTokenAsync(CancellationToken.None);   // mint #1
        Assert.Equal($"{Jwt}-1", first);
        Assert.Equal(1, handler.Calls);

        // 16 parallel callers that all saw the same stale token re-mint EXACTLY once between them.
        var refreshed = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => provider.RefreshTokenAsync(first, CancellationToken.None)));

        Assert.Equal(2, handler.Calls);                         // one additional mint only
        Assert.All(refreshed, t => Assert.Equal($"{Jwt}-2", t)); // all got the single fresh token
    }
}
