using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// The two-zone resume key (design §4) — the heart of resumability. A SETTLED
/// init date (age &gt; SettledAfterDays) gets a stable key (skipped forever once
/// loaded); a HOT init date (age ≤ SettledAfterDays) gets a run-varying key (always
/// re-pulled). Verified both directly on <see cref="StormVistaKeys"/> and through the
/// enumerators, which compute ageDays + hotSuffix from the run context.
/// </summary>
public class WorkUnitKeyTests
{
    // Fixed "today" so age is deterministic and computed in UTC.
    private static readonly DateTime StartedAt = new(2026, 8, 6, 3, 0, 0, DateTimeKind.Utc);
    private const int SettledAfterDays = 21;

    private static LoaderRunContext Context(Guid runId) => new()
    {
        RunId = runId,
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static DailyWorkUnitProvider DailyProvider(StormVistaSettings settings) =>
        new(new FakeReferenceProvider(ReferenceBuilder.Full()), Options.Create(settings),
            NullLogger<DailyWorkUnitProvider>.Instance);

    private static RegionalWorkUnitProvider RegionalProvider(StormVistaSettings settings) =>
        new(new FakeReferenceProvider(ReferenceBuilder.Full()), Options.Create(settings),
            NullLogger<RegionalWorkUnitProvider>.Instance);

    private static StormVistaSettings DailySingleUnitSettings() => new()
    {
        SettledAfterDays = SettledAfterDays,
        HotZoneKeyStrategy = HotZoneKeyStrategy.RunId,
        Models = new[] { "gfs" },
        Cycles = new[] { "00" },
        Types = new[] { "ew_cdd" }
    };

    private static async Task<string> SingleDailyKeyAsync(StormVistaSettings settings, DateOnly initDate, Guid runId)
    {
        var units = await DailyProvider(settings).EnumerateAsync(initDate, initDate, Context(runId), CancellationToken.None);
        return Assert.Single(units).Key;
    }

    // ---------------------------------------------------------------- direct formula

    [Fact]
    public void DailyKey_Settled_HasNoRunSuffix()
    {
        var key = StormVistaKeys.DailyKey("gfs", new DateOnly(2024, 8, 4), "00", "ew_cdd",
            ageDays: 100, settledAfterDays: 21, hotSuffix: "RUN");
        Assert.Equal("sv:daily:gfs:20240804:00:ew_cdd", key);
    }

    [Fact]
    public void DailyKey_Hot_AppendsRunSuffix()
    {
        var key = StormVistaKeys.DailyKey("gfs", new DateOnly(2024, 8, 4), "00", "ew_cdd",
            ageDays: 5, settledAfterDays: 21, hotSuffix: "RUN");
        Assert.Equal("sv:daily:gfs:20240804:00:ew_cdd:run=RUN", key);
    }

    [Fact]
    public void RegionalKey_IncludesRegionSetCode_IncludingIso()
    {
        var reg3 = StormVistaKeys.RegionalKey("ecmwf-weekly", new DateOnly(2024, 8, 4), "00", "ew_cdd", "3",
            ageDays: 100, settledAfterDays: 21, hotSuffix: "RUN");
        var regIso = StormVistaKeys.RegionalKey("ecmwf-weekly", new DateOnly(2024, 8, 4), "00", "pw_cdd", "iso",
            ageDays: 100, settledAfterDays: 21, hotSuffix: "RUN");

        Assert.Equal("sv:regional:ecmwf-weekly:20240804:00:ew_cdd:reg3", reg3);
        Assert.Equal("sv:regional:ecmwf-weekly:20240804:00:pw_cdd:regiso", regIso);
    }

    // ---------------------------------------------------------------- via enumerators

    [Fact]
    public async Task Daily_SettledInitDate_YieldsStableKey_AcrossDifferentRunIds()
    {
        var settled = DateOnly.FromDateTime(StartedAt).AddDays(-(SettledAfterDays + 1)); // age 22 > 21
        var settings = DailySingleUnitSettings();

        var k1 = await SingleDailyKeyAsync(settings, settled, Guid.NewGuid());
        var k2 = await SingleDailyKeyAsync(settings, settled, Guid.NewGuid());

        Assert.Equal(k1, k2);
        Assert.DoesNotContain(":run=", k1); // settled keys carry no run suffix
    }

    [Fact]
    public async Task Daily_HotInitDate_YieldsDifferentKeys_AcrossDifferentRunIds()
    {
        var hot = DateOnly.FromDateTime(StartedAt).AddDays(-5); // age 5 <= 21
        var settings = DailySingleUnitSettings();

        var k1 = await SingleDailyKeyAsync(settings, hot, Guid.NewGuid());
        var k2 = await SingleDailyKeyAsync(settings, hot, Guid.NewGuid());

        Assert.NotEqual(k1, k2);
        Assert.Contains(":run=", k1);
        Assert.Contains(":run=", k2);
    }

    [Fact]
    public async Task Daily_AgeBoundary_ExactlySettledAfterDays_IsHot_OneMoreDayIsSettled()
    {
        var settings = DailySingleUnitSettings();
        var today = DateOnly.FromDateTime(StartedAt);

        // age == SettledAfterDays -> still HOT (condition is strictly age > SettledAfterDays).
        var atBoundary = today.AddDays(-SettledAfterDays);
        var b1 = await SingleDailyKeyAsync(settings, atBoundary, Guid.NewGuid());
        var b2 = await SingleDailyKeyAsync(settings, atBoundary, Guid.NewGuid());
        Assert.NotEqual(b1, b2);            // hot -> run-varying
        Assert.Contains(":run=", b1);

        // age == SettledAfterDays + 1 -> SETTLED.
        var pastBoundary = today.AddDays(-(SettledAfterDays + 1));
        var s1 = await SingleDailyKeyAsync(settings, pastBoundary, Guid.NewGuid());
        var s2 = await SingleDailyKeyAsync(settings, pastBoundary, Guid.NewGuid());
        Assert.Equal(s1, s2);               // settled -> stable
        Assert.DoesNotContain(":run=", s1);
    }

    [Fact]
    public async Task Daily_RunDateStrategy_SettledStable_HotVariesByDateNotRunId()
    {
        var settings = DailySingleUnitSettings();
        settings.HotZoneKeyStrategy = HotZoneKeyStrategy.RunDate;
        var hot = DateOnly.FromDateTime(StartedAt).AddDays(-5);

        // Same calendar day, two different RunIds -> RunDate suffix collapses them.
        var k1 = await SingleDailyKeyAsync(settings, hot, Guid.NewGuid());
        var k2 = await SingleDailyKeyAsync(settings, hot, Guid.NewGuid());

        Assert.Equal(k1, k2);
        Assert.Contains(":run=20260806", k1); // suffix is the run DATE, not the run id
    }

    [Fact]
    public async Task Regional_SettledKey_IncludesRegionSetCode_AndIsStableAcrossRuns()
    {
        var settled = DateOnly.FromDateTime(StartedAt).AddDays(-(SettledAfterDays + 1));
        var settings = new StormVistaSettings
        {
            SettledAfterDays = SettledAfterDays,
            HotZoneKeyStrategy = HotZoneKeyStrategy.RunId,
            Models = new[] { "ecmwf-weekly" },
            Cycles = new[] { "00" },
            RegionSets = new[] { "iso" },
            Types = new[] { "pw_cdd" }
        };

        var units1 = await RegionalProvider(settings).EnumerateAsync(settled, settled, Context(Guid.NewGuid()), CancellationToken.None);
        var units2 = await RegionalProvider(settings).EnumerateAsync(settled, settled, Context(Guid.NewGuid()), CancellationToken.None);

        var k1 = Assert.Single(units1).Key;
        var k2 = Assert.Single(units2).Key;
        Assert.Equal(k1, k2);
        Assert.Contains(":regiso", k1);
        Assert.DoesNotContain(":run=", k1);
    }
}
