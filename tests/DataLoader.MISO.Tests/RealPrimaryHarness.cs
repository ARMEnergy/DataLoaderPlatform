using DataLoader.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.MISO.Tests;

/// <summary>
/// The module's wiring with its REAL primary handler (<c>SocketsHttpHandler { AllowAutoRedirect = false }</c>),
/// pointed at a loopback test server. Used only where the primary's own behaviour is under test.
/// </summary>
internal sealed class MisoHarnessWithRealPrimary
{
    private readonly ServiceProvider _sp;

    private MisoHarnessWithRealPrimary(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<ISeeDbParamStore>(FakeParamStore.Returning(Samples.Key));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Loaders:MISO:BaseUrl"] = baseUrl,
            ["Loaders:MISO:ConnectionString"] = "Server=unused.invalid;Database=Misc;",
            ["Loaders:MISO:RetryDelayMs"] = "1",
            ["Loaders:MISO:HttpTimeoutSeconds"] = "5",
        }).Build();
        new MisoModule().RegisterServices(services, config);
        _sp = services.BuildServiceProvider();
    }

    public static MisoHarnessWithRealPrimary Create(string baseUrl) => new(baseUrl);

    public CapturingLoggerFactory Logs { get; } = new();

    public MisoDayReader<TRow> Reader<TRow>(IMisoRowMapper<TRow> mapper)
    {
        var settings = _sp.GetRequiredService<IOptions<MisoSettings>>().Value;
        var guard = _sp.GetRequiredService<MisoRunGuard>();
        var http = _sp.GetRequiredService<IHttpClientFactory>().CreateClient(MisoModule.HttpClientName);
        return new MisoDayReader<TRow>(new MisoApiClient(http, settings, guard, Logs.CreateLogger("t")), mapper, settings, Logs.CreateLogger("t"));
    }
}
