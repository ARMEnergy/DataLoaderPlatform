using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// <see cref="PlBasicAuthHandler"/> — stamps the static HTTP Basic header
/// <c>Authorization: Basic base64(ClientId:ClientSecret)</c> on EVERY outgoing request (design §5.1), and
/// re-stamps on each send (so a retry carries it too). The credential is a HEADER (never on the URL) and
/// the handler takes NO logger, so it structurally cannot leak the secret to a log. No network — the inner
/// handler is a capturing stub.
/// </summary>
public class BasicAuthHandlerTests
{
    private const string ClientId = "pat-user-1";
    private const string ClientSecret = "s3cr3t-pat-value-xyz";

    /// <summary>Records what the inner handler saw (i.e. what actually goes on the wire) and returns 200.</summary>
    private sealed class CapturingInnerHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient client, CapturingInnerHandler inner) NewClient()
    {
        var settings = new IHSPointLogicSettings { ClientId = ClientId, ClientSecret = ClientSecret };
        var inner = new CapturingInnerHandler();
        var handler = new PlBasicAuthHandler(Options.Create(settings)) { InnerHandler = inner };
        return (new HttpClient(handler), inner);
    }

    [Fact]
    public async Task SetsBasicHeader_EncodingClientIdColonSecret()
    {
        var (client, inner) = NewClient();

        await client.GetAsync("https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_region?pageIndex=0");

        var auth = Assert.Single(inner.Requests).Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal($"{ClientId}:{ClientSecret}", decoded);
    }

    [Fact]
    public async Task ReStampsHeader_OnEverySend()
    {
        var (client, inner) = NewClient();

        await client.GetAsync("https://api.connect.ihsmarkit.com/a");
        await client.GetAsync("https://api.connect.ihsmarkit.com/b");

        Assert.Equal(2, inner.Requests.Count);
        Assert.All(inner.Requests, r =>
        {
            Assert.Equal("Basic", r.Headers.Authorization!.Scheme);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(r.Headers.Authorization!.Parameter!));
            Assert.Equal($"{ClientId}:{ClientSecret}", decoded);
        });
    }

    [Fact]
    public async Task Secret_NeverAppearsOnTheUrl_OnlyInsideTheBase64Header()
    {
        var (client, inner) = NewClient();

        await client.GetAsync("https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_region?pageIndex=0");

        var req = Assert.Single(inner.Requests);
        // The raw secret is never in the URI (the URL carries no credential at all).
        Assert.DoesNotContain(ClientSecret, req.RequestUri!.ToString());
        // It rides ONLY as base64 inside the Authorization header (not the raw string).
        Assert.DoesNotContain(ClientSecret, req.Headers.Authorization!.Parameter!);
    }

    [Fact]
    public void Handler_HasNoLoggerDependency_SoItCannotLeakToALog()
    {
        // The handler's only constructor parameter is IOptions<IHSPointLogicSettings> — there is no ILogger
        // seam through which the credential could ever be written to a log.
        var ctor = Assert.Single(typeof(PlBasicAuthHandler).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(new[] { typeof(IOptions<IHSPointLogicSettings>) }, paramTypes);
        Assert.DoesNotContain(typeof(PlBasicAuthHandler).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            f => typeof(Microsoft.Extensions.Logging.ILogger).IsAssignableFrom(f.FieldType));
    }
}
