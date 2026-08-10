using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Regression coverage for the DI wiring in
/// <see cref="LoaderServiceCollectionExtensions.AddLoaderSettings{TSettings}"/>.
/// A prior "hardening" used <c>TryAddEnumerable</c> with a factory descriptor,
/// which threw <see cref="System.ArgumentException"/> ("indistinguishable from
/// other services") the first time a module registered its settings — crashing
/// the host at startup. The build and the resolver unit tests didn't catch it
/// because neither exercised the registration path. These tests do.
/// </summary>
public class AddLoaderSettingsTests
{
    private sealed class DemoSettings : LoaderSettingsBase
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    private sealed class OtherSettings : LoaderSettingsBase
    {
    }

    /// <summary>Never returns a value — the tests use only non-sentinel config, so it is never called.</summary>
    private sealed class FakeStore : ISeeDbParamStore
    {
        public string? GetParam(string loaderName, string paramName) => null;
    }

    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:LoadLogConnectionString"] = "Server=x;Database=y;Integrated Security=SSPI;",
            ["Loaders:Demo:ApiKey"] = "plain-value",
            ["Loaders:Demo:MaxConcurrentWorkUnits"] = "7",
        })
        .Build();

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // Pre-register the store so AddLoaderSettings' TryAddSingleton keeps this
        // fake (no real SqlParamStore / no database in the test).
        services.AddSingleton<ISeeDbParamStore>(new FakeStore());
        return services;
    }

    [Fact]
    public void AddLoaderSettings_RegistersWithoutThrowing_AndBindsSection()
    {
        var services = NewServices();

        // The regression: this call threw ArgumentException from TryAddEnumerable.
        var ex = Record.Exception(() => services.AddLoaderSettings<DemoSettings>(BuildConfig(), "Demo"));
        Assert.Null(ex);

        using var sp = services.BuildServiceProvider();
        // Resolving Value triggers the SEE_DB post-configure; no sentinel here, so
        // it short-circuits without touching the store.
        var opts = sp.GetRequiredService<IOptions<DemoSettings>>().Value;
        Assert.Equal("plain-value", opts.ApiKey);
        Assert.Equal(7, opts.MaxConcurrentWorkUnits);
    }

    [Fact]
    public void AddLoaderSettings_MultipleLoaders_AndRepeatedSameType_DoNotThrow()
    {
        var services = NewServices();
        var cfg = BuildConfig();

        var ex = Record.Exception(() =>
        {
            services.AddLoaderSettings<DemoSettings>(cfg, "Demo");
            services.AddLoaderSettings<OtherSettings>(cfg, "Other");
            services.AddLoaderSettings<DemoSettings>(cfg, "Demo"); // duplicate same TSettings must be benign
        });
        Assert.Null(ex);

        using var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<IOptions<DemoSettings>>().Value;
        _ = sp.GetRequiredService<IOptions<OtherSettings>>().Value;
    }
}
