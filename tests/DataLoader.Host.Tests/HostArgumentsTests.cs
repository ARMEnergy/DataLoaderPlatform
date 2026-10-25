using DataLoader.Host;
using Xunit;

namespace DataLoader.Host.Tests;

public class HostArgumentsTests
{
    private static readonly string[] Known = { "Platts", "Vulcan", "EnergyAspects" };

    [Fact]
    public void NoArguments_MeansUseConfig()
    {
        var parsed = HostArguments.Parse(Array.Empty<string>(), Known);

        Assert.True(parsed.IsValid);
        Assert.False(parsed.WatchdogMode);
        Assert.Empty(parsed.Loaders);
    }

    [Theory]
    [InlineData("--watchdog")]
    [InlineData("/watchdog")]
    [InlineData("--WATCHDOG")]
    public void WatchdogFlag_SelectsWatchdogMode(string flag)
    {
        var parsed = HostArguments.Parse(new[] { flag }, Known);

        Assert.True(parsed.IsValid);
        Assert.True(parsed.WatchdogMode);
        Assert.Empty(parsed.Loaders);
    }

    [Fact]
    public void WatchdogFlag_IsNotTreatedAsAnUnknownLoader()
    {
        // Before this existed, every unrecognised argument was fatal, so the
        // flag would have exited 1 with "Unknown loader '--watchdog'".
        var parsed = HostArguments.Parse(new[] { "--watchdog" }, Known);

        Assert.True(parsed.IsValid);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void LoaderNames_ResolveToCanonicalCasing()
    {
        var parsed = HostArguments.Parse(new[] { "platts", "VULCAN" }, Known);

        Assert.True(parsed.IsValid);
        Assert.Equal(new[] { "Platts", "Vulcan" }, parsed.Loaders);
    }

    [Fact]
    public void ATypoIsStillFatal()
    {
        var parsed = HostArguments.Parse(new[] { "Pltats" }, Known);

        Assert.False(parsed.IsValid);
        Assert.Contains("Pltats", parsed.Error);
        Assert.Contains("Platts", parsed.Error);   // lists what IS known
    }

    [Fact]
    public void WatchdogCombinedWithALoaderName_IsRejected()
    {
        // They mean different things; silently honouring one would be worse.
        var parsed = HostArguments.Parse(new[] { "--watchdog", "Platts" }, Known);

        Assert.False(parsed.IsValid);
        Assert.Contains("watchdog", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeveralLoadersAreAllResolved()
    {
        var parsed = HostArguments.Parse(new[] { "Platts", "Vulcan", "EnergyAspects" }, Known);

        Assert.True(parsed.IsValid);
        Assert.Equal(3, parsed.Loaders.Count);
    }
}
