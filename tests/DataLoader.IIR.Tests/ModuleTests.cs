using DataLoader.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// <see cref="IirModule.RunAsync"/> orchestration guards (design §1.4). Two behaviours are reachable
/// with no network or DB: (1) the fail-fast credential check returns a FAILED
/// <see cref="LoaderRunResult"/> — before any HTTP client is even resolved — when Username/Password is
/// blank or still the <c>SEE_DB</c> sentinel; (2) an enabled endpoint with no matching pipeline is
/// warned and skipped, and a run with nothing runnable returns success. The service provider carries
/// only <c>IOptions&lt;IirSettings&gt;</c> + logging (no HTTP factory), so any attempt to make a call
/// would throw — proving the fail-fast path never touches the network.
/// </summary>
public class ModuleTests
{
    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static (IServiceProvider sp, ListLoggerProvider logs) Provider(IirSettings settings)
    {
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton(Options.Create(settings));
        return (services.BuildServiceProvider(), logs);
    }

    // ---------------------------------------------------------------- fail-fast on missing/placeholder creds

    [Theory]
    [InlineData("SEE_DB", "pass")]
    [InlineData("user", "SEE_DB")]
    [InlineData("SEE_DB", "SEE_DB")]
    [InlineData("", "pass")]
    [InlineData("user", "  ")]
    public async Task RunAsync_MissingOrPlaceholderCredentials_ReturnsFailed_NoHttp(string user, string pass)
    {
        var (sp, logs) = Provider(new IirSettings { Username = user, Password = pass });

        var result = await new IirModule().RunAsync(sp, Context());

        Assert.False(result.Success);
        Assert.Equal("IIR", result.LoaderId);
        Assert.Contains("not configured", result.ErrorMessage);
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Error), e => e.Message.Contains("credentials are not configured"));
    }

    // ---------------------------------------------------------------- unknown enabled endpoint + nothing runnable

    [Fact]
    public async Task RunAsync_UnknownEnabledEndpoint_WarnsAndSkips_NoPipelines_ReturnsSuccess()
    {
        // Valid creds so we pass the fail-fast gate; no IIirPipeline is registered, and only an unknown
        // endpoint is enabled → the module warns about it, finds nothing runnable, and returns success
        // WITHOUT reaching the validator/HTTP (both would need a DB/network that isn't wired here).
        var settings = new IirSettings
        {
            Username = "user",
            Password = "pass",
            EnabledEndpoints = new[] { "Bogus" }
        };
        var (sp, logs) = Provider(settings);

        var result = await new IirModule().RunAsync(sp, Context());

        Assert.True(result.Success);
        Assert.Equal("IIR", result.LoaderId);
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("Bogus"));
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("No IIR endpoints enabled"));
    }
}
