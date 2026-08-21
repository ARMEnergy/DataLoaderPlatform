using System.Net;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirTokenAuthHandler"/> — stamps <c>Authorization: Bearer &lt;jwt&gt;</c> on every outgoing
/// data request and, on a first <c>401</c>, re-mints once (via the provider) and replays the request
/// exactly once; a second consecutive 401 propagates (a genuine credential failure). The credential is a
/// HEADER (never on the URL). Inner handler is a scripted, capturing stub — no network.
/// </summary>
public class TokenAuthHandlerTests
{
    /// <summary>A fake provider that hands out a starting token and a fresh one per re-mint (counted).</summary>
    private sealed class FakeTokenProvider : IIirTokenProvider
    {
        private string _current = "tok0";
        public int GetCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(_current);
        }

        public Task<string> RefreshTokenAsync(string staleToken, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            _current = "tok" + RefreshCalls; // tok1, tok2, …
            return Task.FromResult(_current);
        }
    }

    /// <summary>Records the Bearer parameter seen ON THE WIRE at each send, then returns the scripted status.</summary>
    private sealed class ScriptedInner : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;
        public List<string?> SeenBearer { get; } = new();
        public int Sends { get; private set; }
        public ScriptedInner(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            SeenBearer.Add(request.Headers.Authorization?.Parameter); // capture now — the handler mutates it later
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static (HttpClient client, FakeTokenProvider provider, ScriptedInner inner) NewClient(params HttpStatusCode[] statuses)
    {
        var provider = new FakeTokenProvider();
        var inner = new ScriptedInner(statuses);
        var handler = new IirTokenAuthHandler(provider) { InnerHandler = inner };
        return (new HttpClient(handler), provider, inner);
    }

    [Fact]
    public async Task StampsBearerHeader_OnEveryRequest()
    {
        var (client, _, inner) = NewClient(HttpStatusCode.OK);

        var resp = await client.PostAsync("https://api.industrialinfo.com/idb/v2.7/plants/summary", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("tok0", Assert.Single(inner.SeenBearer)); // Bearer tok0 stamped
    }

    [Fact]
    public async Task On401_ReMintsOnce_ReplaysWithFreshToken_ReturnsSuccess()
    {
        var (client, provider, inner) = NewClient(HttpStatusCode.Unauthorized, HttpStatusCode.OK);

        var resp = await client.PostAsync("https://api.industrialinfo.com/idb/v2.7/plants/summary", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, provider.RefreshCalls);           // re-minted exactly once
        Assert.Equal(2, inner.Sends);                     // original + single replay
        Assert.Equal(new[] { "tok0", "tok1" }, inner.SeenBearer); // replay carried the refreshed token
    }

    [Fact]
    public async Task On401Twice_SecondUnauthorizedPropagates_NoSecondReMint()
    {
        var (client, provider, inner) = NewClient(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);

        var resp = await client.PostAsync("https://api.industrialinfo.com/idb/v2.7/plants/summary", null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode); // the second 401 propagates to the caller
        Assert.Equal(1, provider.RefreshCalls);                     // re-mint attempted only once
        Assert.Equal(2, inner.Sends);                               // no third attempt
    }

    [Fact]
    public async Task NonUnauthorizedResponse_IsReturnedWithoutReMint()
    {
        var (client, provider, inner) = NewClient(HttpStatusCode.InternalServerError);

        var resp = await client.PostAsync("https://api.industrialinfo.com/idb/v2.7/plants/summary", null);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode); // 5xx is Polly's job, not the auth handler's
        Assert.Equal(0, provider.RefreshCalls);
        Assert.Equal(1, inner.Sends);
    }
}
