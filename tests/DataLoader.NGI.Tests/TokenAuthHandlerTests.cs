using System.Net;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiTokenAuthHandler"/> - stamps <c>Authorization: Bearer &lt;jwt&gt;</c> on every
/// outgoing data request and, on a first <c>401</c>, re-mints <b>once</b> and replays the request
/// <b>once</b>; a second consecutive <c>401</c> propagates (a genuine credential failure, not an
/// expiry) and the reader turns it into a thrown unit failure. Replay is safe because both NGI data
/// calls are body-less GETs. The inner handler is a scripted, capturing stub - no network.
/// </summary>
public class TokenAuthHandlerTests
{
    /// <summary>Records the Bearer parameter seen ON THE WIRE at each send, then returns the scripted status.</summary>
    private sealed class ScriptedInner : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;

        public List<string?> SeenBearer { get; } = new();
        public List<string?> SeenScheme { get; } = new();
        public int Sends { get; private set; }

        public ScriptedInner(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            SeenBearer.Add(request.Headers.Authorization?.Parameter); // capture NOW - the handler mutates it later
            SeenScheme.Add(request.Headers.Authorization?.Scheme);
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private const string Url = "https://api.ngidata.com/bidweekDatafeed.json?issue_date=2026-08-01";

    private static (HttpClient client, FakeNgiTokenProvider provider, ScriptedInner inner) NewClient(
        params HttpStatusCode[] statuses)
    {
        var provider = new FakeNgiTokenProvider();
        var inner = new ScriptedInner(statuses);
        var handler = new NgiTokenAuthHandler(provider) { InnerHandler = inner };
        return (new HttpClient(handler), provider, inner);
    }

    [Fact]
    public async Task StampsBearerSchemeAndToken_OnEveryRequest()
    {
        var (client, provider, inner) = NewClient(HttpStatusCode.OK);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tok0", Assert.Single(inner.SeenBearer));
        Assert.Equal("Bearer", Assert.Single(inner.SeenScheme));
        Assert.Equal(1, provider.GetCalls);
        Assert.Equal(0, provider.RefreshCalls);
    }

    [Fact]
    public async Task On401_ReMintsExactlyOnce_ReplaysWithTheFreshToken_ReturnsSuccess()
    {
        var (client, provider, inner) = NewClient(HttpStatusCode.Unauthorized, HttpStatusCode.OK);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, provider.RefreshCalls);                    // re-minted EXACTLY once
        Assert.Equal(2, inner.Sends);                              // the original + a single replay
        Assert.Equal(new[] { "tok0", "tok1" }, inner.SeenBearer);   // the replay carried the fresh token
    }

    [Fact]
    public async Task On401Twice_TheSecondUnauthorizedPropagates_WithNoSecondReMint()
    {
        var (client, provider, inner) = NewClient(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // reaches the reader -> it throws
        Assert.Equal(1, provider.RefreshCalls);                        // one re-mint attempt only
        Assert.Equal(2, inner.Sends);                                  // no third attempt
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]           // the NORMAL BidWeekData outcome
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyOtherStatus_IsReturnedUntouched_WithNoReMint(HttpStatusCode status)
    {
        var (client, provider, inner) = NewClient(status);

        var response = await client.GetAsync(Url);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(0, provider.RefreshCalls);   // 404 is data, 5xx/429 are Polly's job, 400/403 are loud
        Assert.Equal(1, inner.Sends);
    }
}
