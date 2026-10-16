using DataLoader.EnergyAspects.Models;
using Xunit;

namespace DataLoader.EnergyAspects.Tests;

/// <summary>
/// Settled vs hot resume keys.
///
/// A settled window keeps a stable key and loads once. A hot window gets a
/// run-varying token so revisions are re-fetched — without it, whichever run
/// goes first each day claims today's window and every later run, the scheduled
/// one included, skips it and never sees what the vendor published in between.
///
/// Energy Aspects restates across the whole window (measured 2026-10-15: of
/// 33,939 changed rows only 0.8% were within a day, 61.6% were 8-30 days old),
/// which is why the shipped horizon equals DaysBackStart.
/// </summary>
public class SettledAndHotKeyTests
{
    private static DatasetMapping Mapping(int id = 515) => new()
    {
        MappingId = id,
        Name = "North America planned refinery outages",
        Category = "oil",
        Licensed = "yes",
        DatasetIds = new List<int> { 1, 2, 3 }
    };

    private static EnergyAspectsWorkUnit Unit(
        bool settled, string? hotToken, int part = 1, int partCount = 1) => new()
    {
        Mapping = Mapping(),
        DatasetIds = new[] { 1, 2, 3 },
        WindowStart = new DateTime(2026, 9, 16),
        WindowEnd = new DateTime(2026, 10, 15),
        PartNumber = part,
        PartCount = partCount,
        Settled = settled,
        HotToken = hotToken
    };

    // ---- the settled key must not move ----

    [Fact]
    public void SettledSinglePartKey_IsUnchangedFromBeforeHotZonesExisted()
    {
        // Byte-identical to what this loader has always written, so the existing
        // core.LoadLog history stays valid and nothing reloads.
        Assert.Equal(
            "ea:mapping=515;from=2026-09-16;to=2026-10-15",
            Unit(settled: true, hotToken: null).Key);
    }

    [Fact]
    public void SettledMultiPartKey_KeepsTheChunkingFormat()
    {
        Assert.Equal(
            "ea:mapping=515;part=3of7;from=2026-09-16;to=2026-10-15",
            Unit(settled: true, hotToken: null, part: 3, partCount: 7).Key);
    }

    [Fact]
    public void SettledKey_CarriesNoRunToken() =>
        Assert.DoesNotContain(";run=", Unit(settled: true, hotToken: null).Key);

    // ---- the hot key must move ----

    [Fact]
    public void HotKey_AppendsTheRunToken()
    {
        Assert.Equal(
            "ea:mapping=515;from=2026-09-16;to=2026-10-15;run=2026101512",
            Unit(settled: false, hotToken: "2026101512").Key);
    }

    [Fact]
    public void HotKey_AppendsTheRunTokenAfterThePart()
    {
        Assert.Equal(
            "ea:mapping=515;part=3of7;from=2026-09-16;to=2026-10-15;run=2026101512",
            Unit(settled: false, hotToken: "2026101512", part: 3, partCount: 7).Key);
    }

    [Fact]
    public void HotKey_DiffersBetweenRuns()
    {
        // The whole point: a later run must not match the earlier one's completed row.
        Assert.NotEqual(
            Unit(settled: false, hotToken: "2026101501").Key,
            Unit(settled: false, hotToken: "2026101512").Key);
    }

    [Fact]
    public void HotKey_IsStableWithinTheSameToken()
    {
        // Two invocations in the same hour SHOULD collapse — that is what stops a
        // retry storm from re-pulling the whole window repeatedly.
        Assert.Equal(
            Unit(settled: false, hotToken: "2026101512").Key,
            Unit(settled: false, hotToken: "2026101512").Key);
    }

    [Fact]
    public void HotAndSettledKeysNeverCollide() =>
        Assert.NotEqual(
            Unit(settled: true, hotToken: null).Key,
            Unit(settled: false, hotToken: "2026101512").Key);

    [Fact]
    public void HotKeyFitsTheLoadLogColumn()
    {
        // core.LoadLog.WorkUnitKey is nvarchar(450).
        Assert.True(Unit(settled: false, hotToken: "2026101512", part: 106, partCount: 106)
            .Key.Length <= 450);
    }

    // ---- the horizon decides which is which ----

    [Theory]
    // (daysSinceWindowEnd, settledAfterDays, expectedSettled)
    [InlineData(0, 90, false)]    // today's window, shipped horizon -> hot
    [InlineData(90, 90, false)]   // exactly at the horizon -> still hot
    [InlineData(91, 90, true)]    // past it -> settled
    [InlineData(2, 1, true)]      // a short horizon settles almost everything
    [InlineData(1, 1, false)]
    [InlineData(0, 0, false)]     // 0 keeps only today hot
    [InlineData(1, 0, true)]
    public void SettledHorizonBoundary(int daysSinceWindowEnd, int settledAfterDays, bool expectedSettled)
    {
        // Mirrors the provider: settled when (today - windowEnd).Days > horizon.
        var today = new DateTime(2026, 10, 15);
        var windowEnd = today.AddDays(-daysSinceWindowEnd);

        var settled = (today - windowEnd).Days > Math.Max(0, settledAfterDays);

        Assert.Equal(expectedSettled, settled);
    }

    [Fact]
    public void ShippedHorizonKeepsTheWholeWindowHot()
    {
        // SettledAfterDays == DaysBackStart means nothing inside the window ever
        // settles — the correct posture for a vendor that restates at 8-30 days.
        var settings = new EnergyAspectsSettings();
        Assert.Equal(settings.DaysBackStart, settings.SettledAfterDays);

        var today = new DateTime(2026, 10, 15);
        var oldestWindowEnd = today.AddDays(-settings.DaysBackStart);
        Assert.False((today - oldestWindowEnd).Days > settings.SettledAfterDays);
    }

    [Fact]
    public void DefaultHotKeyStrategyIsRunHour() =>
        // RunDate would let whichever run goes first claim the day — the exact gap
        // this feature exists to close.
        Assert.Equal(EnergyAspectsHotKeyStrategy.RunHour, new EnergyAspectsSettings().HotKeyStrategy);
}
