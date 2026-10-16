using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace DataLoader.EnergyAspects.Tests;

/// <summary>
/// 🔒 The api_key travels in the QUERY STRING, so the HttpClientFactory's own
/// request-logging handlers would write it into the log on every call — at
/// Information level, which is on by default. EnergyAspectsApiSource.SanitiseForLog
/// strips the query string from the lines IT writes, but those handlers sit
/// underneath it and log the raw URI regardless.
///
/// This was live: a run's console output contained the full key. The fix is
/// RemoveAllLoggers() on the named client, and these tests stop it being
/// reintroduced by someone rewriting the registration.
/// </summary>
public class ApiKeyNotLoggedTests
{
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Loaders:EnergyAspects:ApiKey"] = "secret-key-value",
                ["Loaders:EnergyAspects:BaseUrl"] = "https://api.energyaspects.test",
                ["Loaders:EnergyAspects:ConnectionString"] = "Server=.;Database=x;Integrated Security=SSPI;"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        new EnergyAspectsModule().RegisterServices(services, config);
        return services.BuildServiceProvider();
    }

    /// <summary>Type names of every DelegatingHandler in a named client's pipeline.</summary>
    private static List<string> HandlerChain(ServiceProvider provider, string clientName)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        var names = new List<string>();
        for (HttpMessageHandler? h = handler; h is DelegatingHandler d; h = d.InnerHandler)
            names.Add(d.GetType().Name);
        return names;
    }

    [Fact]
    public void NamedClient_PipelineContainsNoLoggingHandler()
    {
        // Asserts the REAL pipeline rather than a config flag. RemoveAllLoggers()
        // stops the factory inserting LoggingHttpMessageHandler /
        // LoggingScopeHttpMessageHandler; if someone drops that call they come back
        // and the api_key is logged on every request.
        using var provider = BuildProvider();

        var chain = HandlerChain(provider, nameof(EnergyAspectsApiSource));

        Assert.DoesNotContain(chain, n => n.Contains("Logging", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRetryHandlerSurvivedTheLoggerRemoval()
    {
        // RemoveAllLoggers() must not have taken the Polly handler with it —
        // otherwise this test would pass for the wrong reason, with an empty pipeline.
        using var provider = BuildProvider();

        var chain = HandlerChain(provider, nameof(EnergyAspectsApiSource));

        Assert.NotEmpty(chain);
        Assert.Contains(chain, n => n.Contains("Policy", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitiseForLog_StripsTheQueryString()
    {
        // The loader's own log lines must never carry the key either.
        var settings = new EnergyAspectsSettings
        {
            ApiKey = "secret-key-value",
            TimeseriesEndpoint = "/data/timeseries/",
            MaxRequestUriLength = 8000
        };

        var uri = new Uri(
            EnergyAspectsRequest.BuildTimeseriesUri(
                settings, new[] { 1, 2, 3 }, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)),
            UriKind.Relative);

        var sanitised = TestableSource.Sanitise(uri);

        Assert.DoesNotContain("secret-key-value", sanitised);
        Assert.DoesNotContain("api_key", sanitised);
        Assert.Equal("/data/timeseries/", sanitised);
    }

    [Fact]
    public void BuiltUriDoesContainTheKey_SoTheGuardsAreNecessary()
    {
        // Guards the premise: the key really is in the URL, so suppressing the
        // factory logging and sanitising our own lines are both load-bearing.
        var settings = new EnergyAspectsSettings
        {
            ApiKey = "secret-key-value",
            TimeseriesEndpoint = "/data/timeseries/",
            MaxRequestUriLength = 8000
        };

        var uri = EnergyAspectsRequest.BuildTimeseriesUri(
            settings, new[] { 1 }, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));

        Assert.Contains("api_key=secret-key-value", uri);
    }

    /// <summary>Reaches the protected SanitiseForLog through a subclass.</summary>
    private sealed class TestableSource : DataLoader.Core.Sources.HttpJsonSourceReaderBase<
        EnergyAspectsWorkUnit, Models.TimeseriesApiItem>
    {
        private TestableSource() : base(new HttpClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { }

        public static string Sanitise(Uri uri) => SanitiseForLog(uri);

        protected override Uri BuildRequestUri(EnergyAspectsWorkUnit unit) => throw new NotSupportedException();
    }
}
