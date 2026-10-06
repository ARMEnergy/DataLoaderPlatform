using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// <see cref="CwgWorkUnitProvider"/> — DB-free work-unit construction & resume
/// keying (design §3). Dated endpoints enumerate the trailing <c>DaysBack</c>
/// window and resolve each unit's key with the StormVista TWO-ZONE rule:
/// <c>ageDays = runDate − representedDate</c>; <c>ageDays &gt; SettledAfterDays</c>
/// SETTLES (bare base key, no <c>:run=</c>), otherwise it stays HOT
/// (<c>baseKey:run=&lt;token&gt;</c>). Undated units are always HOT. Geography
/// descriptors are filtered by <c>Geographies</c>; None/ISO descriptors are not
/// (Fix 2). Dates are deterministic via a fixed
/// <see cref="LoaderRunContext.StartedAtUtc"/> (chosen at 16:00 UTC so the US
/// Eastern calendar date matches the intended day regardless of DST, Fix 4).
/// </summary>
public class WorkUnitProviderTests
{
    // 16:00 UTC -> 12:00 EDT the same day -> Eastern runDate = that calendar day.
    private static readonly DateTime StartedAt = new(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);   // runDate 2026-08-11
    private static readonly DateTime StartedAtNextDay = new(2026, 8, 12, 16, 0, 0, DateTimeKind.Utc); // runDate 2026-08-12

    private static LoaderRunContext Context(Guid? runId = null, DateTime? startedAt = null) => new()
    {
        RunId = runId ?? Guid.NewGuid(),
        StartedAtUtc = startedAt ?? StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static CwgSettings Settings(
        int daysBack = 3,
        HotKeyStrategy strategy = HotKeyStrategy.RunDate,
        string[]? geographies = null,
        int settledAfterDays = 7) => new()
    {
        DaysBack = daysBack,
        SettledAfterDays = settledAfterDays,
        HotZoneKeyStrategy = strategy,
        Geographies = geographies ?? new[] { "northamerica" }
    };

    private static async Task<IReadOnlyList<CwgWorkUnit>> Enumerate(
        CwgEndpointDescriptor descriptor, CwgSettings settings, LoaderRunContext? context = null)
    {
        var provider = new CwgWorkUnitProvider(descriptor, settings, NullLogger.Instance);
        return await provider.GetWorkUnitsAsync(context ?? Context());
    }

    // ---------------------------------------------------------------- dated: window + run-date hot suffix + filename substitution

    [Fact]
    public async Task Dated_CityForecast_EnumeratesDaysBackWindow_NorthAmericaOnly_WithRunDateHotKey()
    {
        var units = await Enumerate(CwgDescriptors.CityForecast, Settings(daysBack: 3));

        // 3 days x ONE region. arm.CityForecast has no Region and no Units column, so exactly
        // one (region, units) pair may be enumerated — northamerica in °F.
        Assert.Equal(3, units.Count);
        Assert.Equal(
            new[] { new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 11) },
            units.Select(u => u.RepresentativeDate!.Value).Distinct().OrderBy(d => d).ToArray());
        Assert.Equal(new[] { "northamerica" }, units.Select(u => u.Region).Distinct().ToArray());

        // The {units} token still fills the filename AND the resume-key Variant slot.
        var naToday = Assert.Single(units, u => u.RepresentativeDate == new DateOnly(2026, 8, 11));
        Assert.Equal("city15dfcst_northamerica_20260811_F.csv", naToday.Filename);           // {region}/{yyyyMMdd}/{units}
        Assert.Equal("F", naToday.Units);
        Assert.Equal("cwg:CityForecast:northamerica:F:20260811:run=20260811", naToday.Key);  // Variant = units token 'F' (HOT)
        Assert.Contains(":run=20260811", naToday.Key);
    }

    [Fact]
    public async Task Dated_RunDateStrategy_SameDay_DifferentRunIds_ProduceIdenticalKeys()
    {
        var settings = Settings(daysBack: 1, strategy: HotKeyStrategy.RunDate);

        var a = await Enumerate(CwgDescriptors.CityForecast, settings, Context(Guid.NewGuid()));
        var b = await Enumerate(CwgDescriptors.CityForecast, settings, Context(Guid.NewGuid()));

        Assert.Equal(
            a.Select(u => u.Key).OrderBy(k => k),
            b.Select(u => u.Key).OrderBy(k => k)); // RunDate token collapses different run ids on the same day
    }

    [Fact]
    public async Task Dated_RunIdStrategy_DifferentRunIds_ProduceDifferentKeys()
    {
        var settings = Settings(daysBack: 1, strategy: HotKeyStrategy.RunId);

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var a = await Enumerate(CwgDescriptors.CityForecast, settings, Context(id1));
        var b = await Enumerate(CwgDescriptors.CityForecast, settings, Context(id2));

        var ka = a.First(u => u.Region == "northamerica").Key;
        var kb = b.First(u => u.Region == "northamerica").Key;
        Assert.NotEqual(ka, kb);
        Assert.Contains(":run=" + id1.ToString("N"), ka);
        Assert.Contains(":run=" + id2.ToString("N"), kb);
    }

    // ---------------------------------------------------------------- CityObservation date = runDate - 1

    [Fact]
    public async Task Dated_CityObservation_NewestRepresentedDate_IsRunDateMinus1()
    {
        var units = await Enumerate(CwgDescriptors.CityObservation, Settings(daysBack: 3));

        var newest = units.Max(u => u.RepresentativeDate!.Value);
        Assert.Equal(new DateOnly(2026, 8, 10), newest); // runDate(08-11) - 1
        Assert.Equal(
            new[] { new DateOnly(2026, 8, 8), new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 10) },
            units.Select(u => u.RepresentativeDate!.Value).Distinct().OrderBy(d => d).ToArray());

        var naNewest = Assert.Single(units, u => u.Region == "northamerica" && u.RepresentativeDate == new DateOnly(2026, 8, 10));
        Assert.Equal("northamerica_observations_final_20260810.csv", naNewest.Filename);
    }

    // ---------------------------------------------------------------- undated: single latest filename, no date, hot key

    [Fact]
    public async Task Undated_CityGasForecast_SingleLatestUnit_NoDate_HotKey()
    {
        var units = await Enumerate(CwgDescriptors.CityGasForecast, Settings());

        var unit = Assert.Single(units);
        Assert.Null(unit.RepresentativeDate);
        Assert.Equal("city_gasday_fcst.csv", unit.Filename);
        Assert.Equal("cwg:CityGasForecast:-:-:run=20260811", unit.Key);
    }

    [Fact]
    public async Task Undated_Station_IsGeographyFiltered_AndHot()
    {
        var units = await Enumerate(CwgDescriptors.Station, Settings());

        var na = Assert.Single(units); // arm.Station has no Region column -> exactly one geography
        Assert.Null(na.RepresentativeDate);
        Assert.Equal("northamerica", na.Region);
        Assert.Equal("northamerica_station_information.csv", na.Filename);
        Assert.Equal("cwg:Station:northamerica:-:run=20260811", na.Key);
    }

    // ---------------------------------------------------------------- RegionlessTarget guard

    [Theory]
    [InlineData("CityForecast")]
    [InlineData("CityObservation")]
    [InlineData("Station")]
    public async Task RegionlessTarget_MoreThanOneGeography_FailsLoudly_RatherThanOverwriting(string endpointId)
    {
        // These three write to tables with NO Region column. Enumerating two geographies would
        // put two different regions' rows on the SAME primary key, and the second would silently
        // overwrite the first (and for CityForecast, mix °C into a °F table with no marker).
        // The provider must refuse instead.
        var descriptor = CwgDescriptors.All.Single(d => d.EndpointId == endpointId);
        Assert.True(descriptor.RegionlessTarget);

        var settings = Settings(daysBack: 1, geographies: new[] { "northamerica", "europe" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Enumerate(descriptor, settings));

        Assert.Contains(endpointId, ex.Message);
        Assert.Contains("no region column", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Geographies", ex.Message);
    }

    [Fact]
    public async Task RegionlessTarget_IsNotSetOnEndpointsThatKeepARegionColumn()
    {
        // The ISO-axis forecast tables all keep a Region key column, so they are free to
        // enumerate their full region list — the guard must not touch them.
        Assert.False(CwgDescriptors.SolarForecast.RegionlessTarget);
        Assert.False(CwgDescriptors.WindForecast.RegionlessTarget);
        Assert.False(CwgDescriptors.WindForecastSubRegion.RegionlessTarget);

        var solar = await Enumerate(CwgDescriptors.SolarForecast, Settings(daysBack: 1));
        Assert.Equal(10, solar.Count);
    }

    // ---------------------------------------------------------------- Geographies filter: City*/Station only

    [Fact]
    public async Task Geographies_Filter_NarrowsGeographyDescriptors_ButNotIsoOrNoneDescriptors()
    {
        // Narrow Geographies to a geography none of the geography descriptors can satisfy.
        var settings = Settings(daysBack: 1, geographies: new[] { "asia" });

        // CityForecast's Regions list is {northamerica} only, so 'asia' filters it to nothing.
        Assert.Empty(await Enumerate(CwgDescriptors.CityForecast, settings));

        // CityObservation / Station take their regions FROM the setting, so 'asia' narrows them to asia.
        var obs = Assert.Single(await Enumerate(CwgDescriptors.CityObservation, settings));
        Assert.Equal("asia", obs.Region);
        var station = Assert.Single(await Enumerate(CwgDescriptors.Station, settings));
        Assert.Equal("asia", station.Region);

        // ISO descriptor (SolarForecast) is a DIFFERENT axis — not filtered by Geographies.
        var solar = await Enumerate(CwgDescriptors.SolarForecast, settings);
        Assert.Equal(10, solar.Count); // all 10 ISO regions regardless of Geographies
        Assert.Contains(solar, u => u.Region == "ERCOT");

        // None descriptor (NationalDegreeDays) is likewise NOT filtered by Geographies.
        var ndd = await Enumerate(CwgDescriptors.NationalDegreeDays, settings);
        Assert.Contains(ndd, u => u.Region == "northamerica");  // survives even though northamerica is excluded
    }

    [Fact]
    public async Task Geographies_Empty_YieldsNoCityUnits()
    {
        var settings = Settings(daysBack: 1, geographies: Array.Empty<string>());

        var city = await Enumerate(CwgDescriptors.CityForecast, settings);

        Assert.Empty(city);
    }

    // ---------------------------------------------------------------- CityForecast: structurally single-region

    [Fact]
    public async Task CityForecast_DropsAsiaStructurally_AndTheSettingPicksOneOfTheRest()
    {
        // 'asia' is dropped STRUCTURALLY (there is no CityForecast file for it), independent of
        // the setting. The remaining two are narrowed to exactly one by Geographies — and because
        // the target has no Region/Units column, the RegionlessTarget guard makes "both" an error
        // rather than a silent overwrite.
        Assert.Equal(new[] { "northamerica", "europe" }, CwgDescriptors.CityForecast.Regions);
        Assert.Equal(new[] { "F", "C" }, CwgDescriptors.CityForecast.RegionUnits);
        Assert.True(CwgDescriptors.CityForecast.RegionlessTarget);
        Assert.DoesNotContain("asia", CwgDescriptors.CityForecast.Regions);

        // Default setting -> northamerica in °F.
        var na = Assert.Single(await Enumerate(CwgDescriptors.CityForecast, Settings(daysBack: 1)));
        Assert.Equal("northamerica", na.Region);
        Assert.Equal("F", na.Units);

        // Repointed by config -> europe in °C; units stay index-aligned to the surviving region.
        var eu = Assert.Single(await Enumerate(
            CwgDescriptors.CityForecast, Settings(daysBack: 1, geographies: new[] { "europe" })));
        Assert.Equal("europe", eu.Region);
        Assert.Equal("C", eu.Units);
        Assert.Equal("C", eu.Variant);
        Assert.Equal("city15dfcst_europe_20260811_C.csv", eu.Filename);
    }

    // ---------------------------------------------------------------- Variant slot: units token vs subregion vs null

    [Fact]
    public async Task Variant_CityForecast_IsUnitsToken_DegreeDays_IsSubregion_PlainEndpoint_IsNull()
    {
        var settings = Settings(daysBack: 1);

        // CityForecast: the Variant sub-key slot carries the units token ('F' for northamerica),
        // equal to Units — no drift.
        var city = await Enumerate(CwgDescriptors.CityForecast, settings);
        var na = Assert.Single(city, u => u.Region == "northamerica");
        Assert.Equal("F", na.Variant);
        Assert.Equal("F", na.Units);

        // Regions5DegreeDays: Variant stays the {subregion} token; Units is null (no units drift).
        var dd = Assert.Single(await Enumerate(CwgDescriptors.Regions5DegreeDays, settings));
        Assert.Equal("5region", dd.Variant);
        Assert.Null(dd.Units);

        // NationalDegreeDays: Variant is 'national'; Units null.
        var ndd = Assert.Single(await Enumerate(CwgDescriptors.NationalDegreeDays, settings));
        Assert.Equal("national", ndd.Variant);
        Assert.Null(ndd.Units);

        // A plain endpoint (no region, no subregion, no units): Variant and Units are both null.
        var gas = Assert.Single(await Enumerate(CwgDescriptors.CityGasForecast, settings));
        Assert.Null(gas.Variant);
        Assert.Null(gas.Units);
    }

    // ---------------------------------------------------------------- Fix 2: NationalDegreeDays NOT filtered by Geographies

    [Fact]
    public async Task NationalDegreeDays_NotFilteredByGeographies_EnumeratesNorthamericaNational_EvenWhenExcluded()
    {
        // Geographies excludes northamerica — must NOT zero out NationalDegreeDays (Fix 2).
        var settings = Settings(daysBack: 1, geographies: new[] { "asia", "europe" });

        var units = await Enumerate(CwgDescriptors.NationalDegreeDays, settings);

        var unit = Assert.Single(units);
        Assert.Equal("northamerica", unit.Region);
        Assert.Equal("national", unit.Variant);
        Assert.Equal(new DateOnly(2026, 8, 11), unit.RepresentativeDate);
        Assert.Equal("northamerica_national_wdd_20260811.csv", unit.Filename);
        Assert.Equal("cwg:NationalDegreeDays:northamerica:national:20260811:run=20260811", unit.Key);
    }

    // ---------------------------------------------------------------- filename substitution: MMddyyyy token, ISO region

    [Fact]
    public async Task Iso_SolarForecast_SubstitutesUppercaseRegion_And_MMddyyyy_Token()
    {
        var units = await Enumerate(CwgDescriptors.SolarForecast, Settings(daysBack: 1));

        var ercot = Assert.Single(units, u => u.Region == "ERCOT");
        Assert.Equal("ERCOTsolar_08112026.csv", ercot.Filename);     // {region}{MMddyyyy}
        Assert.Equal("cwg:SolarForecast:ERCOT:-:20260811:run=20260811", ercot.Key);
    }

    [Fact]
    public async Task DaysBack_LessThan1_IsClampedTo1()
    {
        var units = await Enumerate(CwgDescriptors.NationalDegreeDays, Settings(daysBack: 0));

        Assert.Single(units); // Math.Max(1, DaysBack) -> at least one day
    }

    // ================================================================ two-zone resume keying (StormVista rule)

    // NationalDegreeDays yields exactly one unit per date (single region/variant, offset 0),
    // so it isolates the date->key mapping cleanly.
    private const string NddBase = "cwg:NationalDegreeDays:northamerica:national:";

    private static async Task<CwgWorkUnit> NddUnitForDate(CwgSettings settings, DateOnly date, LoaderRunContext context)
    {
        var units = await Enumerate(CwgDescriptors.NationalDegreeDays, settings, context);
        return Assert.Single(units, u => u.RepresentativeDate == date);
    }

    [Fact]
    public async Task TwoZone_DatedWindow_SettledDatesOmitRunSuffix_HotDatesCarryIt()
    {
        // runDate 2026-08-11, window 21 days, settle boundary 7. ageDays == k (offset 0).
        var settings = Settings(daysBack: 21, settledAfterDays: 7);
        var units = (await Enumerate(CwgDescriptors.NationalDegreeDays, settings)).ToList();

        Assert.Equal(21, units.Count);

        CwgWorkUnit ForDate(DateOnly d) => units.Single(u => u.RepresentativeDate == d);

        // Hot zone (age 0..7): base key + :run=<runDate>.
        Assert.Equal(NddBase + "20260811:run=20260811", ForDate(new DateOnly(2026, 8, 11)).Key); // age 0
        Assert.Equal(NddBase + "20260804:run=20260811", ForDate(new DateOnly(2026, 8, 4)).Key);  // age 7 (== boundary)

        // Settled zone (age > 7): bare base key, NO run token.
        Assert.Equal(NddBase + "20260803", ForDate(new DateOnly(2026, 8, 3)).Key);  // age 8
        Assert.Equal(NddBase + "20260722", ForDate(new DateOnly(2026, 7, 22)).Key); // age 20 (window edge)

        // Every date <= 08-04 (age<=7) is hot; every date <= 08-03 (age>7) is settled.
        Assert.All(units.Where(u => u.RepresentativeDate >= new DateOnly(2026, 8, 4)), u => Assert.Contains(":run=", u.Key));
        Assert.All(units.Where(u => u.RepresentativeDate <= new DateOnly(2026, 8, 3)), u => Assert.DoesNotContain(":run=", u.Key));
    }

    [Fact]
    public async Task TwoZone_Boundary_AgeEqualsSettledAfterDays_IsHot_OnePastIsSettled()
    {
        var settings = Settings(daysBack: 21, settledAfterDays: 7);

        // age == SettledAfterDays (7) -> HOT (rule is strictly ageDays > SettledAfterDays for settled).
        var atBoundary = await NddUnitForDate(settings, new DateOnly(2026, 8, 4), Context());
        Assert.Contains(":run=", atBoundary.Key);

        // age == SettledAfterDays + 1 (8) -> SETTLED.
        var pastBoundary = await NddUnitForDate(settings, new DateOnly(2026, 8, 3), Context());
        Assert.DoesNotContain(":run=", pastBoundary.Key);
    }

    [Fact]
    public async Task TwoZone_SettledKey_IsStableAcrossDifferentRunDates()
    {
        // RunDate strategy. Date 2026-08-01 is settled under both run dates
        // (age 10 on 08-11, age 11 on 08-12), so its bare key is identical.
        var settings = Settings(daysBack: 21, strategy: HotKeyStrategy.RunDate);
        var date = new DateOnly(2026, 8, 1);

        var k1 = (await NddUnitForDate(settings, date, Context(startedAt: StartedAt))).Key;
        var k2 = (await NddUnitForDate(settings, date, Context(startedAt: StartedAtNextDay))).Key;

        Assert.Equal(k1, k2);
        Assert.Equal(NddBase + "20260801", k1);
        Assert.DoesNotContain(":run=", k1); // settled -> no run token -> loaded once, then skipped forever
    }

    [Fact]
    public async Task TwoZone_HotKey_VariesAcrossDifferentRunDates()
    {
        // RunDate strategy. Date 2026-08-10 is hot under both run dates (age 1 on 08-11,
        // age 2 on 08-12), so both keys carry :run= but the run-date token differs.
        var settings = Settings(daysBack: 21, strategy: HotKeyStrategy.RunDate);
        var date = new DateOnly(2026, 8, 10);

        var k1 = (await NddUnitForDate(settings, date, Context(startedAt: StartedAt))).Key;
        var k2 = (await NddUnitForDate(settings, date, Context(startedAt: StartedAtNextDay))).Key;

        Assert.NotEqual(k1, k2);
        Assert.Equal(NddBase + "20260810:run=20260811", k1);
        Assert.Equal(NddBase + "20260810:run=20260812", k2); // hot zone re-pulls: token tracks the run date
    }

    [Fact]
    public async Task TwoZone_DefaultSettings_Window21_Settle7_SpansBothZones()
    {
        // Prove the shipped defaults produce a non-empty settled zone AND a non-empty hot zone
        // in a single run (DaysBack 21 > SettledAfterDays 7).
        var defaults = new CwgSettings();
        Assert.Equal(21, defaults.DaysBack);
        Assert.Equal(7, defaults.SettledAfterDays);

        var units = await Enumerate(CwgDescriptors.NationalDegreeDays, defaults);

        Assert.Contains(units, u => u.Key.Contains(":run="));       // hot dates exist
        Assert.Contains(units, u => !u.Key.Contains(":run="));      // settled dates exist
    }

    [Fact]
    public async Task TwoZone_CityObservation_OffsetMinus1_SettleBoundaryUsesRepresentedDate()
    {
        // CityObservation newest = runDate - 1, so ageDays already includes the -1 offset:
        // representedDate 08-10 -> age 1 (hot); 08-02 -> age 9 (settled).
        var settings = Settings(daysBack: 21, settledAfterDays: 7);
        var units = (await Enumerate(CwgDescriptors.CityObservation, settings))
            .Where(u => u.Region == "northamerica").ToList();

        var hot = units.Single(u => u.RepresentativeDate == new DateOnly(2026, 8, 10)); // age 1
        Assert.Contains(":run=", hot.Key);

        var settled = units.Single(u => u.RepresentativeDate == new DateOnly(2026, 8, 2)); // age 9
        Assert.DoesNotContain(":run=", settled.Key);
        Assert.Equal("cwg:CityObservation:northamerica:-:20260802", settled.Key);
    }
}
