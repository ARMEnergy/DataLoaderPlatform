using DataLoader.Core.Abstractions;
using DataLoader.Marex;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// Work units, resume keys, and the startup warnings. Every warning here names a
/// configuration that "works" but silently does less than the operator expects.
/// </summary>
public class MarexConfigurationTests
{
    private static LoaderRunContext Context(DateTime startedAtUtc, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid(),
        StartedAtUtc = startedAtUtc,
        CancellationToken = CancellationToken.None
    };

    // --------------------------------------------------------------- resume keys

    [Fact]
    public async Task EachFeed_ProducesExactlyOneWorkUnit()
    {
        // The gateway pushes one complete set per entity; there is no window to carve.
        var provider = new MarexSnapshotWorkUnitProvider(
            MarexDescriptors.ClosingPriceFeedId, new MarexSettings());

        var units = await provider.GetWorkUnitsAsync(Context(new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc)));

        Assert.Single(units);
        Assert.Equal("ClosingPrice", units[0].FeedId);
    }

    [Fact]
    public async Task WorkUnitKey_CarriesTheFeedId_SoTwoFeedsNeverShareALoadLogRow()
    {
        var at = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        var settings = new MarexSettings();

        var keys = new List<string>();
        foreach (var table in MarexDescriptors.All)
        {
            var units = await new MarexSnapshotWorkUnitProvider(table.FeedId, settings)
                .GetWorkUnitsAsync(Context(at));
            keys.Add(units[0].Key);
        }

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void RunHour_IsTheDefault()
    {
        // The gateway serves a LIVE snapshot: prices and statistics move intraday, so a
        // date-only key would let the first run of the day suppress every later one.
        Assert.Equal(MarexResumeKeyStrategy.RunHour, new MarexSettings().ResumeKeyStrategy);
    }

    [Fact]
    public void ResumeToken_RunHour_ChangesEveryHour_AndIsStableWithinOne()
    {
        var first = MarexSnapshotWorkUnitProvider.ResumeToken(
            MarexResumeKeyStrategy.RunHour, Context(new DateTime(2026, 9, 21, 16, 5, 0, DateTimeKind.Utc)));
        var sameHour = MarexSnapshotWorkUnitProvider.ResumeToken(
            MarexResumeKeyStrategy.RunHour, Context(new DateTime(2026, 9, 21, 16, 55, 0, DateTimeKind.Utc)));
        var nextHour = MarexSnapshotWorkUnitProvider.ResumeToken(
            MarexResumeKeyStrategy.RunHour, Context(new DateTime(2026, 9, 21, 17, 5, 0, DateTimeKind.Utc)));

        Assert.Equal("2026092116", first);
        Assert.Equal(first, sameHour);
        Assert.NotEqual(first, nextHour);
    }

    [Fact]
    public void ResumeToken_IsStampedInUtc_NotLocalTime()
    {
        // ⚠ A local-time hour token goes BACKWARDS on a fall-back night, which would let
        // an already-recorded success suppress a legitimate reload for an hour.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        var sameInstantAsLocal = utc.ToLocalTime();

        Assert.Equal(
            MarexSnapshotWorkUnitProvider.ResumeToken(MarexResumeKeyStrategy.RunHour, Context(utc)),
            MarexSnapshotWorkUnitProvider.ResumeToken(MarexResumeKeyStrategy.RunHour, Context(sameInstantAsLocal)));
    }

    [Fact]
    public void ResumeToken_RunDate_IsStableAcrossTheWholeUtcDay()
    {
        var morning = MarexSnapshotWorkUnitProvider.ResumeToken(
            MarexResumeKeyStrategy.RunDate, Context(new DateTime(2026, 9, 21, 1, 0, 0, DateTimeKind.Utc)));
        var evening = MarexSnapshotWorkUnitProvider.ResumeToken(
            MarexResumeKeyStrategy.RunDate, Context(new DateTime(2026, 9, 21, 23, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("20260921", morning);
        Assert.Equal(morning, evening);
    }

    [Fact]
    public void ResumeToken_RunId_DiffersEveryInvocation()
    {
        var at = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);

        Assert.NotEqual(
            MarexSnapshotWorkUnitProvider.ResumeToken(MarexResumeKeyStrategy.RunId, Context(at, Guid.NewGuid())),
            MarexSnapshotWorkUnitProvider.ResumeToken(MarexResumeKeyStrategy.RunId, Context(at, Guid.NewGuid())));
    }

    // ------------------------------------------------------------------ warnings

    private static RecordingLogger Warn(Action<MarexSettings> configure)
    {
        var settings = new MarexSettings
        {
            ClientId = "id", Username = "user", Password = "pw"
        };
        configure(settings);
        var log = new RecordingLogger();
        MarexModule.WarnAboutConfiguration(settings, log);
        return log;
    }

    [Fact]
    public void UnresolvedSeeDbSentinel_IsWarnedAbout_ForEachCredential()
    {
        var log = Warn(s => { s.ClientId = "SEE_DB"; s.Username = "SEE_DB"; s.Password = "SEE_DB"; });

        Assert.Contains(log.Records, r => r.Message.Contains("Marex ClientId is still the unresolved"));
        Assert.Contains(log.Records, r => r.Message.Contains("Marex Username is still the unresolved"));
        Assert.Contains(log.Records, r => r.Message.Contains("Marex Password is still the unresolved"));
    }

    [Fact]
    public void ResolvedCredentials_ProduceNoCredentialWarning()
    {
        var log = Warn(_ => { });
        Assert.DoesNotContain(log.Records, r => r.Message.Contains("unresolved"));
    }

    [Fact]
    public void UnknownFeedId_IsWarnedAbout()
    {
        var log = Warn(s => s.EnabledFeeds = new[] { "ClosingPrice", "Trades" });

        Assert.Contains(log.Records, r => r.Message.Contains("unknown feed id 'Trades'"));
    }

    [Fact]
    public void EveryDefaultFeedId_IsKnown()
    {
        var log = Warn(_ => { });
        Assert.DoesNotContain(log.Records, r => r.Message.Contains("unknown feed id"));
    }

    [Fact]
    public void DefaultEnabledFeeds_CoverEveryDescriptor()
    {
        Assert.Equal(
            MarexDescriptors.All.Select(t => t.FeedId).OrderBy(x => x).ToArray(),
            new MarexSettings().EnabledFeeds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void AnUnexpectedHubName_IsWarnedAbout()
    {
        // This is the failure the vendor's own integration note would have caused:
        // Name is the SignalR HUB name, not a client label.
        var log = Warn(s => s.HubName = "ARM-DataLoader");

        Assert.Contains(log.Records, r =>
            r.Level == LogLevel.Warning && r.Message.Contains("HUB NAME"));
    }

    [Fact]
    public void TheCorrectHubName_IsNotWarnedAbout()
    {
        Assert.DoesNotContain(Warn(s => s.HubName = "Gateway.Crude").Records, r => r.Message.Contains("HUB NAME"));
        Assert.DoesNotContain(Warn(s => s.HubName = "").Records, r => r.Message.Contains("HUB NAME"));
    }

    [Fact]
    public void RunDateStrategy_IsWarnedAbout_BecauseTheSourceIsLive()
    {
        var log = Warn(s => s.ResumeKeyStrategy = MarexResumeKeyStrategy.RunDate);

        Assert.Contains(log.Records, r => r.Message.Contains("suppress every later run"));
    }

    [Fact]
    public void TightSnapshotTimeout_IsWarnedAbout()
    {
        Assert.Contains(Warn(s => s.SnapshotTimeoutSeconds = 5).Records,
            r => r.Message.Contains("SnapshotTimeoutSeconds"));
        Assert.DoesNotContain(Warn(_ => { }).Records,
            r => r.Message.Contains("SnapshotTimeoutSeconds"));
    }

    [Fact]
    public void TracingWithoutSdkLogging_IsWarnedAbout()
    {
        var log = Warn(s => { s.EnableSignalRTracing = true; s.EnableSdkLogging = false; });

        Assert.Contains(log.Records, r => r.Message.Contains("nowhere"));
    }

    [Fact]
    public void Defaults_AreClean()
    {
        // Out of the box, with credentials resolved, nothing should be warned about.
        Assert.Empty(Warn(_ => { }).Records);
    }

    [Fact]
    public void SdkLogging_IsOffByDefault()
    {
        // The SDK logs the full bearer token in its connection-config line at INFO.
        Assert.False(new MarexSettings().EnableSdkLogging);
        Assert.False(new MarexSettings().EnableSignalRTracing);
    }

    [Fact]
    public void CredentialSettings_DefaultToTheSeeDbSentinel()
    {
        var settings = new MarexSettings();

        Assert.Equal("SEE_DB", settings.ClientId);
        Assert.Equal("SEE_DB", settings.Username);
        Assert.Equal("SEE_DB", settings.Password);
    }

    [Fact]
    public void ModuleIdentity_IsStable()
    {
        var module = new MarexModule();

        Assert.Equal("Marex", module.LoaderId);
        Assert.Equal("Marex", MarexModule.Id);
        Assert.False(string.IsNullOrWhiteSpace(module.DisplayName));
    }
}

/// <summary>
/// The contract between this loader and the host's <c>ModuleDiscovery</c>, which finds
/// modules by reflection and instantiates them with <c>Activator.CreateInstance</c>
/// BEFORE the DI container exists. A module that cannot be built that way is not
/// discovered — <c>ModuleDiscovery</c> logs a warning and moves on, so the loader would
/// simply never run, with no error anywhere.
/// </summary>
public class MarexModuleDiscoveryTests
{
    [Fact]
    public void MarexModule_IsDiscoverableExactlyAsTheHostDoesIt()
    {
        var types = typeof(MarexModule).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ILoaderModule).IsAssignableFrom(t))
            .ToList();

        // Exactly one per loader assembly, as ILoaderModule's own contract requires.
        var moduleType = Assert.Single(types);

        var module = Assert.IsAssignableFrom<ILoaderModule>(Activator.CreateInstance(moduleType));
        Assert.Equal("Marex", module.LoaderId);
    }

    [Fact]
    public void MarexModule_HoldsNoStateOfItsOwn()
    {
        // ILoaderModule: "Hold no state in the module itself; everything goes through DI."
        // The host builds one instance and reuses it.
        var fields = typeof(MarexModule)
            .GetFields(System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.NonPublic);

        Assert.Empty(fields);
    }
}
