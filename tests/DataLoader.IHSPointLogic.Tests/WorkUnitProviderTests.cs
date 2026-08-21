using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The five archetype work-unit providers (design §4) — DB-free enumeration, path substitution, injected
/// fields and the two-zone resume key. Reference id lists are counting in-memory fakes/delegates (no DB).
/// runDate is the run's UTC calendar date; a fixed 12:00 UTC start keeps it stable.
/// </summary>
public class WorkUnitProviderTests
{
    private static readonly DateTime StartedAt = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc); // runDate 2026-08-18

    private static LoaderRunContext Context(Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid(),
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static IHSPointLogicSettings Settings(
        int daysBack = 21,
        int settledAfterDays = 7,
        PlHotKeyStrategy strategy = PlHotKeyStrategy.RunId,
        int batchSize = 50) => new()
    {
        DaysBack = daysBack,
        SettledAfterDays = settledAfterDays,
        HotZoneKeyStrategy = strategy,
        PointVolumeBatchSize = batchSize
    };

    // ================================================================ A — LatestLookup (one hot unit/run)

    [Fact]
    public async Task ArchetypeA_EmitsOneHotUnit_TemplatePath_NoDate()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.Region, Settings(strategy: PlHotKeyStrategy.RunId), NullLogger.Instance);

        var unit = Assert.Single(await provider.GetWorkUnitsAsync(Context()));

        Assert.Equal("Region", unit.EndpointId);
        Assert.Equal("cs/v1/pointlogic/lookup_region", unit.RequestPath); // plain template, no query
        Assert.Null(unit.RepresentativeDate);
        Assert.Null(unit.ReportDate);
        Assert.StartsWith("pl:Region:run=", unit.Key);
    }

    [Fact]
    public async Task ArchetypeA_RunIdStrategy_KeyVariesAcrossRuns()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.Region, Settings(strategy: PlHotKeyStrategy.RunId), NullLogger.Instance);
        var k1 = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;
        var k2 = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public async Task ArchetypeA_RunDateStrategy_KeyIsStableWithinCalendarDay()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.Region, Settings(strategy: PlHotKeyStrategy.RunDate), NullLogger.Instance);
        var k1 = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;
        var k2 = Assert.Single(await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()))).Key;
        Assert.Equal(k1, k2);
        Assert.Equal("pl:Region:run=20260818", k1);
    }

    // ================================================================ B — GoForwardSnapshot (keyed by run's UTC date)

    [Fact]
    public async Task ArchetypeB_Snapshot_KeyedByRunUtcDate_StampsRepresentativeDate_NoReportDate()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.SupplyAndDemand, Settings(strategy: PlHotKeyStrategy.RunDate), NullLogger.Instance);

        var unit = Assert.Single(await provider.GetWorkUnitsAsync(Context()));

        Assert.Equal(new DateOnly(2026, 8, 18), unit.RepresentativeDate);      // run's UTC date
        Assert.Null(unit.ReportDate);                                          // PayloadCarried, not stamped
        Assert.Equal("pl:SupplyAndDemand:20260818:run=20260818", unit.Key);
    }

    [Fact]
    public async Task ArchetypeB_DemandForecast_AdditionallyStampsReportDateAsRunUtcDate()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.DemandForecastRegion, Settings(strategy: PlHotKeyStrategy.RunDate), NullLogger.Instance);

        var unit = Assert.Single(await provider.GetWorkUnitsAsync(Context()));

        Assert.Equal(new DateOnly(2026, 8, 18), unit.ReportDate);        // StampUtcRunDate basis
        Assert.Equal(new DateOnly(2026, 8, 18), unit.RepresentativeDate);
        Assert.Equal("pl:DemandForecastRegion:20260818:run=20260818", unit.Key);
    }

    // ================================================================ C — DiscoveryLookup (one hot unit per parent id)

    [Fact]
    public async Task ArchetypeC_OneUnitPerParentId_SubstitutesPath_InjectsParamId_ReadsRefOnce()
    {
        var ids = new CountingIdList(1, 2, 3);
        var provider = new PlDiscoveryLookupWorkUnitProvider(
            PlDescriptors.County, Settings(strategy: PlHotKeyStrategy.RunId), ids.GetAsync, "State", NullLogger.Instance);

        var units = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(1, ids.Calls); // reference id list read exactly once per enumeration

        var u2 = Assert.Single(units, u => u.ParamId == 2);
        Assert.Equal("cs/v1/pointlogic/lookup_county/2", u2.RequestPath); // {id} substituted
        Assert.Equal("State", u2.Variant);
        Assert.Null(u2.RepresentativeDate);
        Assert.StartsWith("pl:County:2:run=", u2.Key);
        Assert.Equal("2", u2.ParamKey); // FileLog ParamKey slot = the numeric parent id
    }

    // ================================================================ D — DiscoveryDatedFact (id × 21-day window, two-zone)

    private static PlDiscoveryDatedFactWorkUnitProvider RegionDatedProvider(
        IHSPointLogicSettings settings, ILogger? logger = null) =>
        new(PlDescriptors.SupplyAndDemandByRegion, settings, new CountingIdList(10).GetAsync, null, "Region", logger ?? NullLogger.Instance);

    [Fact]
    public async Task ArchetypeD_CrossesIdsByWindow_InjectsReportDate_AppendsReportDateQuery()
    {
        var settings = Settings(daysBack: 21, settledAfterDays: 7, strategy: PlHotKeyStrategy.RunId);
        var provider = RegionDatedProvider(settings);

        var units = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(21, units.Count); // 1 id × 21-day window
        Assert.Equal(
            new DateOnly(2026, 8, 18), units.Max(u => u.ReportDate!.Value)); // newest = run's UTC date
        Assert.Equal(
            new DateOnly(2026, 7, 29), units.Min(u => u.ReportDate!.Value)); // 20 days back

        var newest = Assert.Single(units, u => u.ReportDate == new DateOnly(2026, 8, 18));
        Assert.Equal(10, newest.ParamId);
        Assert.Equal("cs/v1/pointlogic/supplyDemand/region/10?reportDate=2026-08-18", newest.RequestPath);
        Assert.Equal(new DateOnly(2026, 8, 18), newest.RepresentativeDate);
        Assert.Equal("Region", newest.Variant);
    }

    [Fact]
    public async Task ArchetypeD_TwoZoneKey_SettledStable_HotRunVarying_BoundaryAtExactlySettledAfterDays()
    {
        var settings = Settings(daysBack: 21, settledAfterDays: 7, strategy: PlHotKeyStrategy.RunId);

        async Task<PlWorkUnit> UnitFor(DateOnly date, Guid runId)
        {
            var provider = RegionDatedProvider(settings);
            var units = await provider.GetWorkUnitsAsync(Context(runId));
            return Assert.Single(units, u => u.ReportDate == date);
        }

        // age == SettledAfterDays (7) → still HOT (condition is strictly age > SettledAfterDays).
        var atBoundary = new DateOnly(2026, 8, 11); // runDate − 7
        var b1 = await UnitFor(atBoundary, Guid.NewGuid());
        var b2 = await UnitFor(atBoundary, Guid.NewGuid());
        Assert.NotEqual(b1.Key, b2.Key);
        Assert.Contains(":run=", b1.Key);

        // age == 8 → SETTLED: stable bare key across runs, no :run=.
        var settled = new DateOnly(2026, 8, 10); // runDate − 8
        var s1 = await UnitFor(settled, Guid.NewGuid());
        var s2 = await UnitFor(settled, Guid.NewGuid());
        Assert.Equal(s1.Key, s2.Key);
        Assert.Equal("pl:SupplyAndDemandByRegion:10:20260810", s1.Key);
        Assert.DoesNotContain(":run=", s1.Key);

        // newest (age 0) → HOT.
        var newest = await UnitFor(new DateOnly(2026, 8, 18), Guid.NewGuid());
        Assert.Contains("pl:SupplyAndDemandByRegion:10:20260818:run=", newest.Key);
    }

    [Fact]
    public async Task ArchetypeD_SubRegion_InjectsSecondaryRegionIdFromMap_SkipsUnmappedIdWithWarning()
    {
        var settings = Settings(daysBack: 1, settledAfterDays: 7, strategy: PlHotKeyStrategy.RunId);
        var logger = new ListLogger();

        // ids 26252 (mapped → region 26105) and 999 (unmapped → must be skipped loudly).
        Func<CancellationToken, Task<IReadOnlyList<int>>> ids = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 26252, 999 });
        Func<CancellationToken, Task<IReadOnlyDictionary<int, int>>> map =
            _ => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int> { [26252] = 26105 });

        var provider = new PlDiscoveryDatedFactWorkUnitProvider(
            PlDescriptors.SupplyAndDemandBySubRegion, settings, ids, map, "SubRegion", logger);

        var units = await provider.GetWorkUnitsAsync(Context());

        // Only the mapped sub-region survives (DaysBack=1 → one unit for it).
        var u = Assert.Single(units);
        Assert.Equal(26252, u.ParamId);         // sub-region id (path)
        Assert.Equal(26105, u.SecondaryId);     // parent region id (from the map)
        Assert.Equal("SubRegion", u.Variant);
        Assert.Contains(logger.OfLevel(LogLevel.Warning), e => e.Message.Contains("999"));
    }

    [Fact]
    public async Task ArchetypeD_DaysBackBelow1_ClampedTo1()
    {
        var settings = Settings(daysBack: 0, settledAfterDays: 7, strategy: PlHotKeyStrategy.RunId);
        var units = await RegionDatedProvider(settings).GetWorkUnitsAsync(Context());
        Assert.Single(units); // Math.Max(1, DaysBack)
    }

    // ================================================================ E — BatchedFact (≤N-id batches)

    [Fact]
    public async Task ArchetypeE_ChunksPointIds_50PerBatch_StableTokens_CsvBuilt_ReadsRefOnce()
    {
        var settings = Settings(strategy: PlHotKeyStrategy.RunId, batchSize: 50);
        var points = new CountingPointProvider(Enumerable.Range(1, 125)); // 125 ids → 50 / 50 / 25
        var provider = new PlBatchedFactWorkUnitProvider(PlDescriptors.PointVolume, settings, points, NullLogger.Instance);

        var units = await provider.GetWorkUnitsAsync(Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(1, points.Calls); // point reference read exactly once

        // Deterministic start-date-stamped batch tokens in order (all ids have a NULL watermark → the
        // default backfill floor 2020-01-01 → prefix 20200101, in-group sub-index 0000/0001/0002).
        Assert.Equal(new[] { "20200101-0000", "20200101-0001", "20200101-0002" }, units.Select(u => u.BatchToken).ToArray());
        Assert.All(units, u => Assert.Equal("Batch", u.Variant));
        Assert.All(units, u => Assert.Equal(new DateOnly(2026, 8, 18), u.RepresentativeDate));

        // First batch: pointIds CSV = 1..50 on the volumeHistory/point path + the &startDate= floor.
        var b0 = units[0];
        Assert.StartsWith("cs/v1/pointlogic/volumeHistory/point?pointIds=1,2,3,", b0.RequestPath);
        Assert.Equal("cs/v1/pointlogic/volumeHistory/point?pointIds=" + string.Join(",", Enumerable.Range(1, 50)) + "&startDate=2020-01-01", b0.RequestPath);
        Assert.StartsWith("pl:PointVolume:20200101-0000:run=", b0.Key);
        Assert.Equal("20200101-0000", b0.ParamKey);

        // Last batch: the 25 remaining ids (101..125).
        var b2 = units[2];
        Assert.Equal("cs/v1/pointlogic/volumeHistory/point?pointIds=" + string.Join(",", Enumerable.Range(101, 25)) + "&startDate=2020-01-01", b2.RequestPath);
    }

    [Fact]
    public async Task ArchetypeE_BatchTokens_AreStableAcrossRuns_ForTheSameOrderedIdList()
    {
        var settings = Settings(strategy: PlHotKeyStrategy.RunId, batchSize: 50);
        var points = new CountingPointProvider(Enumerable.Range(1, 125));
        var provider = new PlBatchedFactWorkUnitProvider(PlDescriptors.PointVolume, settings, points, NullLogger.Instance);

        var run1 = await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()));
        var run2 = await provider.GetWorkUnitsAsync(Context(Guid.NewGuid()));

        // Tokens (and thus the batch identity) are derived from the ordered id list, not the run id.
        Assert.Equal(run1.Select(u => u.BatchToken), run2.Select(u => u.BatchToken));
        Assert.Equal(run1.Select(u => u.RequestPath), run2.Select(u => u.RequestPath));
    }
}
