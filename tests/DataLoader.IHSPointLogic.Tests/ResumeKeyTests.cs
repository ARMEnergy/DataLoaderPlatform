using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// The hour-granular hot resume key (design §B.4). Covers the shared token derivation
/// <see cref="PlResumeKey.HotToken"/> (the 3-way <see cref="PlHotKeyStrategy"/> switch) and its effect
/// through the work-unit providers: under the new default <see cref="PlHotKeyStrategy.RunHour"/> a
/// scheduled hourly endpoint re-pulls each UTC hour (hour-granular key) while a same-hour re-run
/// idempotently skips; and the archetype-D <b>settled</b>-zone key stays byte-identical (no hour, no
/// <c>run=</c>). The <see cref="PlHotKeyStrategy.RunDate"/>/<see cref="PlHotKeyStrategy.RunId"/> tokens
/// are asserted byte-identical to the prior format to guard the existing keying tests. DB-free.
/// </summary>
public class ResumeKeyTests
{
    private static readonly Guid FixedRunId = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private static LoaderRunContext Context(DateTime startedAtUtc, Guid? runId = null) => new()
    {
        RunId = runId ?? FixedRunId,
        StartedAtUtc = startedAtUtc,
        CancellationToken = CancellationToken.None
    };

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static IHSPointLogicSettings Settings(PlHotKeyStrategy strategy, int daysBack = 21, int settledAfterDays = 7) => new()
    {
        DaysBack = daysBack,
        SettledAfterDays = settledAfterDays,
        HotZoneKeyStrategy = strategy,
        PointVolumeBatchSize = 50
    };

    // ================================================================ HotToken — the 3-way switch

    [Fact]
    public void HotToken_RunHour_IsUtcYyyyMMddHH()
    {
        var ctx = Context(Utc(2026, 8, 18, 12, 34));
        Assert.Equal("2026081812", PlResumeKey.HotToken(PlHotKeyStrategy.RunHour, ctx));
    }

    [Fact]
    public void HotToken_RunDate_IsByteIdenticalToPriorYyyyMMdd()
    {
        var ctx = Context(Utc(2026, 8, 18, 12, 34));
        var token = PlResumeKey.HotToken(PlHotKeyStrategy.RunDate, ctx);
        Assert.Equal("20260818", token);        // unchanged daily cadence — no hour appended
        Assert.Equal(8, token.Length);
    }

    [Fact]
    public void HotToken_RunId_IsByteIdenticalToPriorRunIdN()
    {
        var ctx = Context(Utc(2026, 8, 18, 12, 34));
        var token = PlResumeKey.HotToken(PlHotKeyStrategy.RunId, ctx);
        Assert.Equal(FixedRunId.ToString("N"), token);
        Assert.Equal(32, token.Length);          // "N" format: 32 hex chars, no dashes
    }

    [Fact]
    public void HotToken_RunHour_SameHourIsStable_DifferentHourDiffers()
    {
        var sameHourA = PlResumeKey.HotToken(PlHotKeyStrategy.RunHour, Context(Utc(2026, 8, 18, 12, 0)));
        var sameHourB = PlResumeKey.HotToken(PlHotKeyStrategy.RunHour, Context(Utc(2026, 8, 18, 12, 59)));
        var nextHour = PlResumeKey.HotToken(PlHotKeyStrategy.RunHour, Context(Utc(2026, 8, 18, 13, 0)));

        Assert.Equal(sameHourA, sameHourB);      // same UTC hour → same token → idempotent same-hour skip
        Assert.NotEqual(sameHourA, nextHour);    // next hour → distinct token → re-pull
    }

    [Fact]
    public void HotToken_RunHour_DiffersFromRunDate_ByTheHourSuffix()
    {
        var ctx = Context(Utc(2026, 8, 18, 12, 0));
        var hour = PlResumeKey.HotToken(PlHotKeyStrategy.RunHour, ctx);
        var date = PlResumeKey.HotToken(PlHotKeyStrategy.RunDate, ctx);
        Assert.StartsWith(date, hour);           // yyyyMMdd + HH
        Assert.Equal(date + "12", hour);
    }

    // ================================================================ Provider keys under RunHour (archetype A/B)

    [Fact]
    public void SnapshotProvider_ArchetypeA_UnderRunHour_ProducesHourGranularKey()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.Region, Settings(PlHotKeyStrategy.RunHour), NullLogger.Instance);

        var noon = SingleKey(provider, Context(Utc(2026, 8, 18, 12, 5)));
        var noonAgain = SingleKey(provider, Context(Utc(2026, 8, 18, 12, 55)));
        var onePm = SingleKey(provider, Context(Utc(2026, 8, 18, 13, 5)));

        Assert.Equal("pl:Region:run=2026081812", noon);
        Assert.Equal(noon, noonAgain);           // same UTC hour → idempotent skip
        Assert.NotEqual(noon, onePm);            // new hour → re-pull
        Assert.Equal("pl:Region:run=2026081813", onePm);
    }

    [Fact]
    public void SnapshotProvider_ArchetypeB_UnderRunHour_KeepsRunDatePrefix_AddsHourHotSuffix()
    {
        var provider = new PlSnapshotWorkUnitProvider(PlDescriptors.SupplyAndDemand, Settings(PlHotKeyStrategy.RunHour), NullLogger.Instance);

        var key = SingleKey(provider, Context(Utc(2026, 8, 18, 12, 5)));
        Assert.Equal("pl:SupplyAndDemand:20260818:run=2026081812", key);
    }

    // ================================================================ Archetype D — hot hour-granular, settled UNCHANGED

    private static PlDiscoveryDatedFactWorkUnitProvider RegionDatedProvider(IHSPointLogicSettings settings)
    {
        Func<CancellationToken, Task<IReadOnlyList<int>>> ids = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 10 });
        return new PlDiscoveryDatedFactWorkUnitProvider(
            PlDescriptors.SupplyAndDemandByRegion, settings, ids, null, "Region", NullLogger.Instance);
    }

    [Fact]
    public async Task ArchetypeD_UnderRunHour_HotZoneIsHourGranular_SettledZoneUnchanged()
    {
        var runHourUnits = await RegionDatedProvider(Settings(PlHotKeyStrategy.RunHour))
            .GetWorkUnitsAsync(Context(Utc(2026, 8, 18, 12, 0)));

        // Hot (age 0) → run-varying key gains hour granularity.
        var hot = Assert.Single(runHourUnits, u => u.ReportDate == new DateOnly(2026, 8, 18));
        Assert.Equal("pl:SupplyAndDemandByRegion:10:20260818:run=2026081812", hot.Key);

        // Settled (age 8 > SettledAfterDays 7) → stable bare key: no hour, no run= suffix.
        var settled = Assert.Single(runHourUnits, u => u.ReportDate == new DateOnly(2026, 8, 10));
        Assert.Equal("pl:SupplyAndDemandByRegion:10:20260810", settled.Key);
        Assert.DoesNotContain(":run=", settled.Key);
    }

    [Fact]
    public async Task ArchetypeD_SettledKey_IsIdenticalRegardlessOfHotStrategy()
    {
        DateOnly settledDate = new(2026, 8, 10); // runDate − 8, settled

        async Task<string> SettledKey(PlHotKeyStrategy strategy)
        {
            var units = await RegionDatedProvider(Settings(strategy)).GetWorkUnitsAsync(Context(Utc(2026, 8, 18, 12, 0)));
            return Assert.Single(units, u => u.ReportDate == settledDate).Key;
        }

        var underRunHour = await SettledKey(PlHotKeyStrategy.RunHour);
        var underRunId = await SettledKey(PlHotKeyStrategy.RunId);
        var underRunDate = await SettledKey(PlHotKeyStrategy.RunDate);

        // §B.4: only the HOT suffix gains hour granularity — the settled-zone key is untouched.
        Assert.Equal("pl:SupplyAndDemandByRegion:10:20260810", underRunHour);
        Assert.Equal(underRunHour, underRunId);
        Assert.Equal(underRunHour, underRunDate);
    }

    // ================================================================ Archetype E — batched fact under RunHour

    [Fact]
    public async Task ArchetypeE_UnderRunHour_HotKeyIsHourGranular_SameHourStable()
    {
        var points = new CountingPointProvider(Enumerable.Range(1, 50)); // one batch
        var provider = new PlBatchedFactWorkUnitProvider(PlDescriptors.PointVolume, Settings(PlHotKeyStrategy.RunHour), points, NullLogger.Instance);

        var noon = (await provider.GetWorkUnitsAsync(Context(Utc(2026, 8, 18, 12, 0)))).Single().Key;
        var noonAgain = (await provider.GetWorkUnitsAsync(Context(Utc(2026, 8, 18, 12, 40)))).Single().Key;
        var onePm = (await provider.GetWorkUnitsAsync(Context(Utc(2026, 8, 18, 13, 0)))).Single().Key;

        Assert.Equal("pl:PointVolume:20200101-0000:run=2026081812", noon); // NULL watermarks → default 2020-01-01 floor
        Assert.Equal(noon, noonAgain);
        Assert.NotEqual(noon, onePm);
    }

    private static string SingleKey(IWorkUnitProvider<PlWorkUnit> provider, LoaderRunContext context) =>
        provider.GetWorkUnitsAsync(context).GetAwaiter().GetResult().Single().Key;
}
