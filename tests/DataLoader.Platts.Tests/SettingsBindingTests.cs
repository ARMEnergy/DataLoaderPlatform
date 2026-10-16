using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Platts.Tests;

/// <summary>
/// Pins that <c>Loaders:Platts:ReloadAllFiles</c> in appsettings.json (or the
/// matching environment override) actually reaches
/// <see cref="PlattsSettings.ReloadAllFiles"/>. A silent binding typo here would
/// look exactly like the flag being off, which is the failure mode that matters:
/// the operator sets it, nothing changes, and every file is still skipped.
/// </summary>
public class SettingsBindingTests
{
    private static PlattsSettings Bind(params (string Key, string? Value)[] entries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        // Registered before AddLoaderSettings so its TryAddSingleton keeps this
        // stub: the real SqlParamStore would reach for the database, and nothing
        // in these cases is a SEE_DB sentinel anyway.
        services.AddSingleton<ISeeDbParamStore>(new NoSeeDbParams());
        services.AddLoaderSettings<PlattsSettings>(configuration, PlattsModule.Id);

        return services.BuildServiceProvider().GetRequiredService<IOptions<PlattsSettings>>().Value;
    }

    private sealed class NoSeeDbParams : ISeeDbParamStore
    {
        public string? GetParam(string loaderName, string paramName) => null;
    }

    [Fact]
    public void ReloadAllFiles_DefaultsToFalse_WhenAbsentFromConfiguration()
    {
        // Absent must mean "keep the normal load-once behaviour", never "reload all".
        Assert.False(Bind(("Loaders:Platts:RootDirectory", "/")).ReloadAllFiles);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void ReloadAllFiles_BindsFromTheConfiguredKeyPath(string value)
    {
        Assert.True(Bind(("Loaders:Platts:ReloadAllFiles", value)).ReloadAllFiles);
    }

    [Fact]
    public void ReloadAllFiles_BindsFalseExplicitly()
    {
        Assert.False(Bind(("Loaders:Platts:ReloadAllFiles", "false")).ReloadAllFiles);
    }

    [Fact]
    public void DaysBack_And_SettledAfterDays_DefaultToZero_WhenAbsent()
    {
        var settings = Bind(("Loaders:Platts:RootDirectory", "/"));
        Assert.Equal(0, settings.DaysBack);           // 0 = no discovery limit
        Assert.Equal(0, settings.SettledAfterDays);   // 0 = only today's folder is hot
    }

    [Fact]
    public void DaysBack_And_SettledAfterDays_BindFromTheConfiguredKeyPaths()
    {
        var settings = Bind(
            ("Loaders:Platts:DaysBack", "30"),
            ("Loaders:Platts:SettledAfterDays", "1"));

        Assert.Equal(30, settings.DaysBack);
        Assert.Equal(1, settings.SettledAfterDays);
    }

    [Fact]
    public void SettledAfterDays_BindsANegativeValue()
    {
        // -1 is the documented way to leave nothing hot; it must survive binding.
        Assert.Equal(-1, Bind(("Loaders:Platts:SettledAfterDays", "-1")).SettledAfterDays);
    }

    [Fact]
    public void ReloadAllFiles_IsNotConfusedWithAnotherLoadersSection()
    {
        // Guards against the section name drifting away from PlattsModule.Id.
        Assert.False(Bind(("Loaders:StormVista:ReloadAllFiles", "true")).ReloadAllFiles);
    }
}
