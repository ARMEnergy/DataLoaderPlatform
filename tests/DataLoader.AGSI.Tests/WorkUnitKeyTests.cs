using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.AGSI.Tests;

/// <summary>
/// The StormVista two-zone resume key (design §3.3) — the heart of resumability. A
/// SETTLED storage date (age &gt; <c>SettledAfterDays</c>) gets a STABLE key
/// <c>agsi:storage:{country}:{yyyyMMdd}</c> (loaded once, then a cheap
/// <c>core.LoadLog</c> skip forever); a HOT date (age ≤ <c>SettledAfterDays</c>) gets a
/// run-varying key so the hot window is re-pulled and upserted idempotently. The
/// undated entities unit is ALWAYS hot. Boundary is exactly at <c>SettledAfterDays</c>
/// (age &gt; boundary settles; age == boundary stays hot — no off-by-one).
/// </summary>
public class WorkUnitKeyTests
{
    // 12:00 UTC keeps the CET calendar date stable → runDate 2026-08-17, newest 2026-08-16.
    private static readonly DateTime StartedAt = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
    private const int SettledAfterDays = 7;

    private static LoaderRunContext Context(Guid runId) => new()
    {
        RunId = runId,
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static AgsiSettings StorageSettings(AgsiHotKeyStrategy strategy = AgsiHotKeyStrategy.RunId) => new()
    {
        DaysBack = 21,
        SettledAfterDays = SettledAfterDays,
        HotZoneKeyStrategy = strategy
    };

    private static async Task<AgsiStorageWorkUnit> StorageUnitForDate(
        AgsiSettings settings, DateOnly date, Guid runId)
    {
        var provider = new AgsiStorageWorkUnitProvider(
            settings, new FakeAgsiCountryProvider("de"), NullLogger.Instance);
        var units = await provider.GetWorkUnitsAsync(Context(runId));
        return Assert.Single(units, u => u.Date == date);
    }

    // ---------------------------------------------------------------- settled = stable, hot = run-varying

    [Fact]
    public async Task Storage_SettledDate_StableKey_AcrossRuns_NoRunSuffix()
    {
        // age 8 (> 7) → settled. d = runDate − 8 = 2026-08-09.
        var settled = new DateOnly(2026, 8, 9);
        var settings = StorageSettings();

        var u1 = await StorageUnitForDate(settings, settled, Guid.NewGuid());
        var u2 = await StorageUnitForDate(settings, settled, Guid.NewGuid());

        Assert.Equal(u1.Key, u2.Key);
        Assert.Equal("agsi:storage:de:20260809", u1.Key); // stable, no :run=
        Assert.DoesNotContain(":run=", u1.Key);
    }

    [Fact]
    public async Task Storage_HotDate_RunVaryingKey_AcrossRuns()
    {
        // age 1 (<= 7) → hot. Newest date d = 2026-08-16.
        var hot = new DateOnly(2026, 8, 16);
        var settings = StorageSettings();

        var u1 = await StorageUnitForDate(settings, hot, Guid.NewGuid());
        var u2 = await StorageUnitForDate(settings, hot, Guid.NewGuid());

        Assert.NotEqual(u1.Key, u2.Key);
        Assert.Contains("agsi:storage:de:20260816:run=", u1.Key);
        Assert.Contains(":run=", u2.Key);
    }

    // ---------------------------------------------------------------- exact boundary — no off-by-one

    [Fact]
    public async Task Storage_AgeBoundary_ExactlySettledAfterDays_IsHot_OneMoreDayIsSettled()
    {
        var settings = StorageSettings();

        // age == SettledAfterDays (7) → still HOT (condition is strictly age > SettledAfterDays).
        var atBoundary = new DateOnly(2026, 8, 10); // runDate − 7
        var b1 = await StorageUnitForDate(settings, atBoundary, Guid.NewGuid());
        var b2 = await StorageUnitForDate(settings, atBoundary, Guid.NewGuid());
        Assert.NotEqual(b1.Key, b2.Key);
        Assert.Contains(":run=", b1.Key);

        // age == SettledAfterDays + 1 (8) → SETTLED.
        var pastBoundary = new DateOnly(2026, 8, 9); // runDate − 8
        var s1 = await StorageUnitForDate(settings, pastBoundary, Guid.NewGuid());
        var s2 = await StorageUnitForDate(settings, pastBoundary, Guid.NewGuid());
        Assert.Equal(s1.Key, s2.Key);
        Assert.DoesNotContain(":run=", s1.Key);
    }

    [Fact]
    public async Task Storage_RunDateStrategy_HotKeyIsSameWithinCalendarDay_ButSettledStillBare()
    {
        var settings = StorageSettings(AgsiHotKeyStrategy.RunDate);

        // Hot date, two different run ids on the same CET day → RunDate suffix collapses them.
        var hot = new DateOnly(2026, 8, 16);
        var h1 = await StorageUnitForDate(settings, hot, Guid.NewGuid());
        var h2 = await StorageUnitForDate(settings, hot, Guid.NewGuid());
        Assert.Equal(h1.Key, h2.Key);
        Assert.Equal("agsi:storage:de:20260816:run=20260817", h1.Key); // suffix is the CET run DATE

        var settled = await StorageUnitForDate(settings, new DateOnly(2026, 8, 9), Guid.NewGuid());
        Assert.Equal("agsi:storage:de:20260809", settled.Key);
    }

    // ---------------------------------------------------------------- entities unit is ALWAYS hot

    [Fact]
    public async Task Entities_Key_IsAlwaysHot_RunIdStrategy_VariesAcrossRuns()
    {
        var settings = new AgsiSettings { HotZoneKeyStrategy = AgsiHotKeyStrategy.RunId };
        var provider = new AgsiEntitiesWorkUnitProvider(settings, NullLogger.Instance);

        var k1 = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;
        var k2 = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;

        Assert.NotEqual(k1, k2);
        Assert.StartsWith("agsi:entities:run=", k1); // no settled variant exists
        Assert.Contains(":run=", k1);
    }

    [Fact]
    public async Task Entities_Key_RunDateStrategy_IsCetRunDate()
    {
        var settings = new AgsiSettings { HotZoneKeyStrategy = AgsiHotKeyStrategy.RunDate };
        var provider = new AgsiEntitiesWorkUnitProvider(settings, NullLogger.Instance);

        var k = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;
        Assert.Equal("agsi:entities:run=20260817", k);
    }
}
