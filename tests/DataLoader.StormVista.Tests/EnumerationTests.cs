using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// The reference-driven enumeration (design §5): the daily/regional providers turn
/// the seeded matrix into work units for a date window. Fed by an in-memory
/// reference double — no database.
/// </summary>
public class EnumerationTests
{
    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static readonly DateOnly Day = new(2026, 8, 5); // one hot day inside the window

    private static DailyWorkUnitProvider Daily(StormVistaSettings s) =>
        new(new FakeReferenceProvider(ReferenceBuilder.Full()), Options.Create(s),
            NullLogger<DailyWorkUnitProvider>.Instance);

    private static RegionalWorkUnitProvider Regional(StormVistaSettings s) =>
        new(new FakeReferenceProvider(MixedRegionalReference()), Options.Create(s),
            NullLogger<RegionalWorkUnitProvider>.Instance);

    /// <summary>
    /// Regional-feed reference double covering BOTH model kinds now that the daily-type
    /// models also serve regional. It seeds:
    ///  - a daily-type regional model (<c>SupportsDaily=1, SupportsRegional=1</c>) → all four cycles,
    ///  - a weekly-only regional model (<c>SupportsDaily=0, SupportsRegional=1</c>) → 00/12 only,
    ///  - one experimental model of each kind (to prove <c>ExcludeExperimental</c> covers both),
    ///  - a daily-only model (<c>SupportsRegional=0</c>) that must never surface in the regional feed.
    /// Cycles/types/region-sets mirror <see cref="ReferenceBuilder.Full"/> so the type×region-set
    /// bridge assertions carry over unchanged.
    /// </summary>
    private static StormVistaReference MixedRegionalReference() => new()
    {
        Models = new List<ModelRef>
        {
            new("gfs", SupportsDaily: true, SupportsRegional: false, IsExperimental: false),                      // daily-only — excluded from regional
            new("gfs-ens-bc", SupportsDaily: true, SupportsRegional: true, IsExperimental: false),               // daily-type regional
            new("ai-gfs-ens", SupportsDaily: true, SupportsRegional: true, IsExperimental: true),                // daily-type regional (experimental)
            new("ecmwf-weekly", SupportsDaily: false, SupportsRegional: true, IsExperimental: false),            // weekly-only regional
            new("ai-fourcastnetv2-gfs-ens-weekly", SupportsDaily: false, SupportsRegional: true, IsExperimental: true) // weekly-only regional (experimental)
        },
        Cycles = new List<CycleRef>
        {
            new("00", SupportsDaily: true, SupportsRegional: true),
            new("06", SupportsDaily: true, SupportsRegional: false),
            new("12", SupportsDaily: true, SupportsRegional: true),
            new("18", SupportsDaily: true, SupportsRegional: false)
        },
        WddTypes = new List<string> { "ew_cdd", "gw_hdd", "pw_cdd", "pw_hdd" },
        RegionSets = new List<RegionSetRef> { new("3", "EIA"), new("iso", "ISO") },
        RegionSetTypes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "ew_cdd", "gw_hdd", "pw_cdd" },
            ["iso"] = new[] { "pw_cdd", "pw_hdd" }
        },
        Regions = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "West", "East", "Producing" },
            ["iso"] = new[] { "bpa", "miso", "nyiso" }
        }
    };

    private static Task<IReadOnlyList<DailyWorkUnit>> EnumDaily(StormVistaSettings s) =>
        Daily(s).EnumerateAsync(Day, Day, Context(), CancellationToken.None);

    private static Task<IReadOnlyList<RegionalWorkUnit>> EnumRegional(StormVistaSettings s) =>
        Regional(s).EnumerateAsync(Day, Day, Context(), CancellationToken.None);

    // ------------------------------------------------------------------ Daily

    [Fact]
    public async Task Daily_UsesOnlySupportsDailyModels_ExcludesWeeklyModels()
    {
        var units = await EnumDaily(new StormVistaSettings());

        var models = units.Select(u => u.Model).Distinct().ToHashSet();
        Assert.Equal(new[] { "gfs", "ecmwf", "mlr15" }.ToHashSet(), models); // the 3 SupportsDaily models
        Assert.DoesNotContain("ecmwf-weekly", models);
        Assert.DoesNotContain("ai-fourcastnetv2-gfs-ens-weekly", models);
    }

    [Fact]
    public async Task Daily_UsesAllFourCyclesAndThreeDailyTypes()
    {
        var units = await EnumDaily(new StormVistaSettings());

        // 3 models x 4 cycles x 3 types (ew_cdd, gw_hdd, pw_cdd; no pw_hdd for daily).
        Assert.Equal(3 * 4 * 3, units.Count);
        Assert.Equal(new[] { "00", "06", "12", "18" }.ToHashSet(), units.Select(u => u.Cycle).ToHashSet());
        Assert.Equal(new[] { "ew_cdd", "gw_hdd", "pw_cdd" }.ToHashSet(), units.Select(u => u.WddType).ToHashSet());
        Assert.DoesNotContain("pw_hdd", units.Select(u => u.WddType)); // pw_hdd is ISO-regional only
    }

    [Fact]
    public async Task Daily_ExcludeExperimental_DropsExperimentalModels()
    {
        var kept = await EnumDaily(new StormVistaSettings { ExcludeExperimental = false });
        var filtered = await EnumDaily(new StormVistaSettings { ExcludeExperimental = true });

        Assert.Contains(kept, u => u.Model == "mlr15");       // default keeps experimental
        Assert.DoesNotContain(filtered, u => u.Model == "mlr15");
        Assert.Equal(2 * 4 * 3, filtered.Count);              // only gfs + ecmwf remain
    }

    // ------------------------------------------------------------------ Regional

    [Fact]
    public async Task Regional_UsesOnlySupportsRegionalModels_ExcludesDailyOnlyModel()
    {
        var units = await EnumRegional(new StormVistaSettings());

        // Every SupportsRegional model appears (both kinds); the daily-only model never does.
        Assert.Equal(
            new[] { "gfs-ens-bc", "ai-gfs-ens", "ecmwf-weekly", "ai-fourcastnetv2-gfs-ens-weekly" }.ToHashSet(),
            units.Select(u => u.WkModel).ToHashSet());
        Assert.DoesNotContain(units, u => u.WkModel == "gfs"); // daily-only (SupportsRegional = 0)
    }

    [Fact]
    public async Task Regional_CycleSet_IsModelKindDependent()
    {
        var units = await EnumRegional(new StormVistaSettings());

        // Daily-type regional model → all four cycles (verified: e.g. gfs-ens-bc reg3 at 00/06/12/18z).
        var dailyTypeCycles = units.Where(u => u.WkModel == "gfs-ens-bc").Select(u => u.Cycle).ToHashSet();
        Assert.Equal(new[] { "00", "06", "12", "18" }.ToHashSet(), dailyTypeCycles);

        // Weekly-only regional model → 00/12 only; 06/18 would be a guaranteed 404.
        var weeklyCycles = units.Where(u => u.WkModel == "ecmwf-weekly").Select(u => u.Cycle).ToHashSet();
        Assert.Equal(new[] { "00", "12" }.ToHashSet(), weeklyCycles);
        Assert.DoesNotContain(units, u => u.WkModel == "ecmwf-weekly" && (u.Cycle == "06" || u.Cycle == "18"));
    }

    [Fact]
    public async Task Regional_TypesComeFromTheRegionSetBridge()
    {
        var units = await EnumRegional(new StormVistaSettings());

        // EIA set "3" -> {ew_cdd, gw_hdd, pw_cdd}; ISO set "iso" -> {pw_cdd, pw_hdd}.
        // The bridge is independent of model kind: it holds for daily-type and weekly-only models alike.
        var eiaTypes = units.Where(u => u.RegionSetCode == "3").Select(u => u.WddType).ToHashSet();
        var isoTypes = units.Where(u => u.RegionSetCode == "iso").Select(u => u.WddType).ToHashSet();

        Assert.Equal(new[] { "ew_cdd", "gw_hdd", "pw_cdd" }.ToHashSet(), eiaTypes);
        Assert.Equal(new[] { "pw_cdd", "pw_hdd" }.ToHashSet(), isoTypes);
        Assert.DoesNotContain(units, u => u.RegionSetCode == "3" && u.WddType == "pw_hdd"); // never a guaranteed 404

        // (model,cycle) pairs: 2 daily-type x 4 cycles + 2 weekly-only x 2 cycles = 12;
        // each pair x (3 EIA types + 2 ISO types) = 12 x 5 = 60.
        Assert.Equal(12 * (3 + 2), units.Count);
    }

    [Fact]
    public async Task Regional_ExcludeExperimental_DropsExperimentalModels_OfBothKinds()
    {
        var filtered = await EnumRegional(new StormVistaSettings { ExcludeExperimental = true });

        Assert.DoesNotContain(filtered, u => u.WkModel == "ai-gfs-ens");                     // daily-type experimental
        Assert.DoesNotContain(filtered, u => u.WkModel == "ai-fourcastnetv2-gfs-ens-weekly"); // weekly-only experimental
        Assert.Equal(new[] { "gfs-ens-bc", "ecmwf-weekly" }.ToHashSet(), filtered.Select(u => u.WkModel).ToHashSet());

        // gfs-ens-bc (daily-type → 4 cycles) + ecmwf-weekly (weekly-only → 2 cycles) = 6 (model,cycle) pairs x 5 types.
        Assert.Equal(6 * (3 + 2), filtered.Count);
    }

    [Fact]
    public async Task Regional_RegionSetFilter_NarrowsToRequestedSet()
    {
        var units = await EnumRegional(new StormVistaSettings { RegionSets = new[] { "iso" } });

        Assert.All(units, u => Assert.Equal("iso", u.RegionSetCode));

        // Still 12 (model,cycle) pairs, each now emitting only the 2 ISO types.
        Assert.Equal(12 * 2, units.Count);
    }
}
