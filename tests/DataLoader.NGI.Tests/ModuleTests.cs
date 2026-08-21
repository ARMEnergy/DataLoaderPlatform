using DataLoader.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// <see cref="NgiModule"/> orchestration guards reachable with no network and no database
/// (design §1.5). The service provider carries only <c>IOptions&lt;NgiSettings&gt;</c> + logging - no
/// HTTP factory, no <see cref="INgiFileLog"/>, no validator - so any attempt to reach the network or a
/// DB would throw, which is itself the proof that these paths do neither.
/// </summary>
public class ModuleTests
{
    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static (IServiceProvider sp, ListLoggerProvider logs) Provider(NgiSettings settings)
    {
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton(Options.Create(settings));
        return (services.BuildServiceProvider(), logs);
    }

    private static NgiSettings Valid() => new()
    {
        Username = ReaderHarness.DummyUser,
        Password = ReaderHarness.DummyPassword
    };

    // ================================================== identity

    [Fact]
    public void Identity_IsStableAndMatchesTheConfigSectionName()
    {
        var module = new NgiModule();
        Assert.Equal("NGI", NgiModule.Id);
        Assert.Equal("NGI", module.LoaderId);
        Assert.Contains("NGI", module.DisplayName);
        Assert.Equal("NGI", NgiModule.HttpClientName);
        Assert.Equal("NGI.Token", NgiModule.TokenClientName);
    }

    [Fact]
    public void DefaultEnabledEndpoints_AreTheTwoRealEndpointIds()
    {
        var settings = new NgiSettings();
        Assert.Equal(new[] { NgiEndpoints.BidWeekLocations, NgiEndpoints.BidWeekData }, settings.EnabledEndpoints);
    }

    [Fact]
    public void SensitiveSettings_DefaultToTheSeeDbSentinel_NeverALiteralSecret()
    {
        var settings = new NgiSettings();
        Assert.Equal("SEE_DB", settings.Username);
        Assert.Equal("SEE_DB", settings.Password);
        Assert.Equal("https://api.ngidata.com", settings.BaseUrl);
        Assert.Equal("/auth", settings.AuthPath);
        Assert.Equal(2, settings.RequestsPerSecond);
    }

    // ================================================== the credential guard

    [Theory]
    [InlineData("SEE_DB", "dummy-password")]
    [InlineData("user@example.test", "SEE_DB")]
    [InlineData("SEE_DB", "SEE_DB")]
    [InlineData("", "dummy-password")]
    [InlineData("user@example.test", "   ")]
    public async Task RunAsync_UnresolvedCredentials_ReturnsFailed_WithoutTouchingHttpOrTheDatabase(
        string user, string pass)
    {
        var (sp, logs) = Provider(new NgiSettings { Username = user, Password = pass });

        var result = await new NgiModule().RunAsync(sp, Context());

        Assert.False(result.Success);
        Assert.Equal("NGI", result.LoaderId);
        Assert.Contains("not configured", result.ErrorMessage);
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Error), e => e.Message.Contains("credentials are not configured"));

        // The values themselves are never logged or echoed.
        Assert.All(logs.Logger.Messages, m => Assert.DoesNotContain(ReaderHarness.DummyPassword, m));
        Assert.DoesNotContain(ReaderHarness.DummyPassword, result.ErrorMessage);
    }

    // ================================================== endpoint selection

    [Fact]
    public async Task RunAsync_UnknownEnabledEndpoint_WarnsAndSkips_NothingRunnable_ReturnsSuccess()
    {
        var settings = Valid();
        settings.EnabledEndpoints = new[] { "Bogus" };
        var (sp, logs) = Provider(settings);

        var result = await new NgiModule().RunAsync(sp, Context());

        Assert.True(result.Success);
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("Bogus"));
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("No NGI endpoints enabled"));
    }

    [Fact]
    public async Task RunAsync_NoEndpointsEnabled_ReturnsSuccess()
    {
        var settings = Valid();
        settings.EnabledEndpoints = Array.Empty<string>();
        var (sp, logs) = Provider(settings);

        var result = await new NgiModule().RunAsync(sp, Context());

        Assert.True(result.Success);
        Assert.Contains(logs.Logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("No NGI endpoints enabled"));
    }

    // ================================================== the two pipelines are INDEPENDENT

    [Fact]
    public void Pipelines_CarryTheEndpointIdAndAreNotCoupled()
    {
        // The critical divergence from AGSI: no reference provider, no read proc, no tier barrier and
        // no FK between the two tables. NgiPipeline's constructor is the whole dependency list, and it
        // contains nothing that could couple the two instances.
        var ctor = typeof(NgiPipeline<NgiLocationsWorkUnit, BidWeekLocationRow>).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType.Name).ToArray();

        Assert.Equal(
            new[] { "String", "IWorkUnitProvider`1", "ISourceReader`2", "ISink`1", "ILoadLogRepository", "NgiSettings", "ILogger" },
            parameterTypes);
    }

    [Fact]
    public void EndpointIds_MatchTheFileLogContextValues()
    {
        // arm.usp_UpsertFileLog resolves the endpoint NAME server-side and rejects anything else, so
        // these literals must be the same set everywhere.
        var locations = new NgiFileContext(NgiEndpoints.BidWeekLocations, null, "/bidweekLocations?format=json");
        var data = new NgiFileContext(NgiEndpoints.BidWeekData, new DateOnly(2026, 8, 1), "/bidweekDatafeed.json?issue_date=2026-08-01");

        Assert.Equal("BidWeekLocations", locations.Endpoint);
        Assert.Null(locations.RepresentativeDate);   // the undated snapshot collapses to one hub row
        Assert.Equal("BidWeekData", data.Endpoint);
        Assert.Equal(new DateOnly(2026, 8, 1), data.RepresentativeDate);
    }

    [Fact]
    public void FileLogContext_HasNoRegionSlot()
    {
        // Deliberate divergence from AGSI: NGI has no region REQUEST axis (one call returns every
        // region), and a FileLog.RegionId next to arm.BidWeekData.Region (a PAYLOAD column) would mean
        // two unrelated things under one name.
        var properties = typeof(NgiFileContext).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Endpoint", "RepresentativeDate", "RequestPath" }, properties);
        Assert.DoesNotContain("Region", properties);
        Assert.DoesNotContain("RegionId", properties);
    }
}
