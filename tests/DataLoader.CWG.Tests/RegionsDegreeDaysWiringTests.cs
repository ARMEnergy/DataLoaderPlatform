using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Descriptor + work-unit wiring for the three new degree-day endpoints (#16–#18).
/// Proves each is discoverable (<see cref="CwgDescriptors.All"/>/<see cref="CwgDescriptors.AllIds"/>),
/// carries the right subregion token (5region/9region/iso), ExpectedColumns (18/18/11),
/// targets the correct <c>arm</c> table/TVP/proc, and (via the DB-free provider) produces
/// the expected <c>northamerica_&lt;token&gt;_wdd_&lt;date&gt;.csv</c> filename and resume key.
/// Like NationalDegreeDays these are RegionKind.None with a fixed <c>northamerica</c> — NOT
/// subject to the <c>Geographies</c> filter (Fix 2).
/// </summary>
public class RegionsDegreeDaysWiringTests
{
    // 16:00 UTC → 12:00 EDT the same day → Eastern runDate = 2026-08-11 (matches the fixture date).
    private static readonly DateTime StartedAt = new(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static CwgSettings Settings(string[]? geographies = null) => new()
    {
        DaysBack = 1,
        SettledAfterDays = 7,
        HotZoneKeyStrategy = HotKeyStrategy.RunDate,
        Geographies = geographies ?? new[] { "northamerica", "asia", "europe" }
    };

    private static CwgEndpointDescriptor Descriptor(string id) =>
        CwgDescriptors.All.Single(d => d.EndpointId == id);

    private static async Task<IReadOnlyList<CwgWorkUnit>> Enumerate(string id, CwgSettings settings) =>
        await new CwgWorkUnitProvider(Descriptor(id), settings, NullLogger.Instance).GetWorkUnitsAsync(Context());

    // ---------------------------------------------------------------- discovery

    [Fact]
    public void All_And_AllIds_Contain_The_Three_Endpoints_ExactlyOnce()
    {
        Assert.Equal(18, CwgDescriptors.All.Length);
        Assert.Equal(18, CwgDescriptors.AllIds.Length);

        foreach (var id in new[] { "Regions5DegreeDays", "Regions9DegreeDays", "ISODegreeDays" })
        {
            Assert.Contains(id, CwgDescriptors.AllIds);
            Assert.Single(CwgDescriptors.All, d => d.EndpointId == id);
        }
    }

    // ---------------------------------------------------------------- descriptor contract

    [Theory]
    [InlineData("Regions5DegreeDays", "5region", 18, "arm.Regions5DegreeDays", "arm.Regions5DegreeDaysTvp", "arm.usp_BulkMergeRegions5DegreeDays")]
    [InlineData("Regions9DegreeDays", "9region", 18, "arm.Regions9DegreeDays", "arm.Regions9DegreeDaysTvp", "arm.usp_BulkMergeRegions9DegreeDays")]
    [InlineData("ISODegreeDays", "iso", 11, "arm.ISODegreeDays", "arm.ISODegreeDaysTvp", "arm.usp_BulkMergeISODegreeDays")]
    public void Descriptor_Wiring_ShapeA_Dated_Ymd_None_SubregionToken_ExpectedColumns_TargetNames(
        string id, string subregion, int expectedColumns, string table, string tvp, string proc)
    {
        var d = Descriptor(id);

        Assert.Equal(CwgParseShape.A, d.ParseShape);
        Assert.True(d.Dated);
        Assert.Equal(CwgDateToken.Ymd, d.DateToken);
        Assert.Equal(CwgRegionKind.None, d.RegionKind);          // fixed northamerica, NOT a Geography axis
        Assert.Equal(0, d.DateOffsetDays);
        Assert.Equal(expectedColumns, d.ExpectedColumns);
        Assert.False(d.AllowShortRows);

        // {subregion} = 5region / 9region / iso, and the filename family is shared with National.
        var extra = Assert.Single(d.ExtraPlaceholders);
        Assert.Equal("subregion", extra.Name);
        Assert.Equal(new[] { subregion }, extra.Values);
        Assert.Equal("northamerica_{subregion}_wdd_{date}.csv", d.FilenameTemplate);

        Assert.Equal(table, d.TargetTable);
        Assert.Equal(tvp, d.TargetTvp);
        Assert.Equal(proc, d.TargetProc);
    }

    // ---------------------------------------------------------------- filename + resume key

    [Theory]
    [InlineData("Regions5DegreeDays", "5region")]
    [InlineData("Regions9DegreeDays", "9region")]
    [InlineData("ISODegreeDays", "iso")]
    public async Task Provider_ProducesExpectedFilename_Variant_Region_And_HotKey(string id, string subregion)
    {
        var units = await Enumerate(id, Settings());

        // DaysBack=1 → one unit (single fixed northamerica region, single subregion, offset 0).
        var unit = Assert.Single(units);
        Assert.Equal("northamerica", unit.Region);
        Assert.Equal(subregion, unit.Variant);
        Assert.Equal(new DateOnly(2026, 8, 11), unit.RepresentativeDate);
        Assert.Equal($"northamerica_{subregion}_wdd_20260811.csv", unit.Filename);
        Assert.Equal($"cwg:{id}:northamerica:{subregion}:20260811:run=20260811", unit.Key); // age 0 → HOT
    }

    // ---------------------------------------------------------------- NOT filtered by Geographies (Fix 2)

    [Theory]
    [InlineData("Regions5DegreeDays", "5region")]
    [InlineData("Regions9DegreeDays", "9region")]
    [InlineData("ISODegreeDays", "iso")]
    public async Task Provider_NotFilteredByGeographies_EnumeratesNorthamerica_EvenWhenExcluded(string id, string subregion)
    {
        // Geographies excludes northamerica — must NOT zero these out (RegionKind.None, Fix 2).
        var units = await Enumerate(id, Settings(geographies: new[] { "asia", "europe" }));

        var unit = Assert.Single(units);
        Assert.Equal("northamerica", unit.Region);
        Assert.Equal($"northamerica_{subregion}_wdd_20260811.csv", unit.Filename);
    }
}
