using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// The europe wind matrices (Shape C and Shape D). CWG publishes the SAME matrix in two zones and
/// two clock formats, and every europe region silently produced ZERO rows on an HTTP 200 until
/// 2026-10-12:
///
///   northamerica   "Date (EST)"   hour rows "12:00 AM" … "11:00 PM"   UTCHourEnding = label + 4h
///   europe         "Date (UTC)"   hour rows "00:00"    … "23:00"      UTCHourEnding = label + 0h
///
/// Three independent defects, each of which alone zeroed the read: the header was matched as the
/// literal "Date (EST)"; CwgHours knew only the 12-hour labels; and the +4h Eastern shift was
/// hardcoded. The offsets are verified against the incumbent dbo.WindForecast — for ForecastDate
/// 2026-10-12 ERCOT spans 10-11 04:00 → 10-27 03:00 while UK spans 10-11 00:00 → 10-26 23:00.
/// </summary>
public class EuropeanMatrixTests
{
    private static readonly ShapeCParser ShapeC = new();
    private static readonly ShapeDParser ShapeD = new();

    private static CwgWorkUnit Unit(string endpointId, string region, DateOnly init, string filename) => new()
    {
        EndpointId = endpointId,
        Region = region,
        RepresentativeDate = init,
        Filename = filename,
        KeyValue = "k"
    };

    // ---------------------------------------------------------------- Shape C: UKwind (Date (UTC), 24-hour labels)

    [Fact]
    public void ShapeC_EuropeFile_IsParsed_NotSilentlyEmpty()
    {
        var csv = SampleData.Read("UKwind_10122026.csv");
        var unit = Unit("WindForecast", "UK", new DateOnly(2026, 10, 12), "UKwind_10122026.csv");

        var cells = ShapeC.Parse(CwgDescriptors.WindForecast, unit, csv, NullLogger.Instance);

        // The whole bug: this used to be 0 on a perfectly good HTTP 200.
        Assert.NotEmpty(cells);
        Assert.Equal(Enumerable.Range(0, 24), cells.Select(c => c.HourOfDay).Distinct().OrderBy(h => h));
        Assert.All(cells, c => Assert.Equal(0, c.UtcOffsetHours));   // Date (UTC) -> no shift
    }

    [Fact]
    public void ShapeC_EuropeFile_KeepsThe24HourLabelVerbatim()
    {
        var csv = SampleData.Read("UKwind_10122026.csv");
        var unit = Unit("WindForecast", "UK", new DateOnly(2026, 10, 12), "UKwind_10122026.csv");

        var cells = ShapeC.Parse(CwgDescriptors.WindForecast, unit, csv, NullLogger.Instance);

        // HourLabel is an arm-ONLY column (dbo has none), kept so the un-shifted source value stays
        // recoverable — so it must be the file's own label, not normalised to the 12-hour form.
        Assert.Equal("00:00", cells.First(c => c.HourOfDay == 0).HourLabel);
        Assert.Equal("23:00", cells.First(c => c.HourOfDay == 23).HourLabel);
    }

    [Fact]
    public void ShapeC_NorthAmericaFile_StillUsesTheEasternOffsetAnd12HourLabels()
    {
        // Regression guard: the europe fix must not touch the northamerica path.
        var csv = SampleData.Read("ERCOTsolar_08112026.csv");
        var unit = Unit("SolarForecast", "ERCOT", new DateOnly(2026, 8, 11), "ERCOTsolar_08112026.csv");

        var cells = ShapeC.Parse(CwgDescriptors.SolarForecast, unit, csv, NullLogger.Instance);

        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.Equal(CwgHours.EasternOffsetHours, c.UtcOffsetHours));
        Assert.Equal("12:00 AM", cells.First(c => c.HourOfDay == 0).HourLabel);
    }

    [Fact]
    public void WindForecastRow_UtcHourEnding_IsUnshiftedForEurope_AndPlus4ForNorthAmerica()
    {
        // The end-to-end consequence, matching dbo: a europe midnight label stays at 00:00 UTC,
        // a northamerica midnight label lands at 04:00 UTC.
        var day = new DateOnly(2026, 10, 12);

        var europe = new CwgMatrixCell(day, "00:00", 0, "1234", UtcOffsetHours: 0);
        var na = new CwgMatrixCell(day, "12:00 AM", 0, "1234", UtcOffsetHours: CwgHours.EasternOffsetHours);

        var euRow = WindForecastRow.From(europe, Unit("WindForecast", "UK", day, "UKwind_10122026.csv"));
        var naRow = WindForecastRow.From(na, Unit("WindForecast", "ERCOT", day, "ERCOTwind_10122026.csv"));

        Assert.NotNull(euRow);
        Assert.NotNull(naRow);
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0), euRow!.UTCHourEnding);
        Assert.Equal(new DateTime(2026, 10, 12, 4, 0, 0), naRow!.UTCHourEnding);
    }

    // ---------------------------------------------------------------- Shape D: UKwind_regions

    [Fact]
    public void ShapeD_EuropeFile_IsParsed_WithItsOwnSubRegionBlocks()
    {
        var csv = SampleData.Read("UKwind_regions_10122026.csv");
        var unit = Unit("WindForecastSubRegion", "UK", new DateOnly(2026, 10, 12), "UKwind_regions_10122026.csv");

        var cells = ShapeD.Parse(CwgDescriptors.WindForecastSubRegion, unit, csv, NullLogger.Instance);

        Assert.NotEmpty(cells);
        var subRegions = cells.Select(c => c.SubRegion).Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Offshore", "Onshore" }, subRegions);
        Assert.All(cells, c => Assert.Equal(0, c.UtcOffsetHours));
        Assert.Equal(Enumerable.Range(0, 24), cells.Select(c => c.HourOfDay).Distinct().OrderBy(h => h));
    }

    // ---------------------------------------------------------------- zone handling

    [Theory]
    [InlineData("EST", 4)]
    [InlineData("est", 4)]
    [InlineData("UTC", 0)]
    [InlineData("utc", 0)]
    public void KnownZones_ResolveToTheirFixedOffset(string zone, int expected)
    {
        Assert.True(CwgHours.TryUtcOffsetForZone(zone, out var offset));
        Assert.Equal(expected, offset);
    }

    [Theory]
    [InlineData("CET")]
    [InlineData("PST")]
    [InlineData("")]
    public void UnknownZone_IsRejected_RatherThanGuessed(string zone)
    {
        Assert.False(CwgHours.TryUtcOffsetForZone(zone, out _));
    }

    [Fact]
    public void ShapeC_UnknownZoneHeader_DropsTheFileRatherThanTimestampItWrongly()
    {
        // A new zone must not quietly inherit the Eastern offset — every row would be wrong.
        var csv =
            ",,,\n" +
            "Date (CET),10/12/2026,10/13/2026\n" +
            "00:00,100,200\n";
        var unit = Unit("WindForecast", "GERMANY", new DateOnly(2026, 10, 12), "x.csv");

        var cells = ShapeC.Parse(CwgDescriptors.WindForecast, unit, csv, NullLogger.Instance);

        Assert.Empty(cells);
    }

    [Fact]
    public void HourIndex_AcceptsBothClockFormats()
    {
        Assert.Equal(0, CwgHours.IndexOf("00:00"));
        Assert.Equal(0, CwgHours.IndexOf("12:00 AM"));
        Assert.Equal(13, CwgHours.IndexOf("13:00"));
        Assert.Equal(13, CwgHours.IndexOf("1:00 PM"));
        Assert.Equal(23, CwgHours.IndexOf("23:00"));
        Assert.Equal(23, CwgHours.IndexOf("11:00 PM"));
        Assert.Equal(-1, CwgHours.IndexOf("sum change for the day"));
    }

    // ---------------------------------------------------------------- descriptor coverage

    [Fact]
    public void WindForecast_CoversEveryRegionTheIncumbentHolds()
    {
        // dbo.WindForecast carries 18 regions; ITALY and NETHERLANDS were missing from the
        // descriptor entirely, so they were never even requested.
        foreach (var region in new[] { "UK", "GERMANY", "FRANCE", "SPAIN", "ITALY", "NETHERLANDS" })
            Assert.Contains(region, CwgDescriptors.WindForecast.Regions);
    }

    [Fact]
    public void WindForecastSubRegion_CoversOnlyTheParentsThatPublishARegionsFile()
    {
        // Probed live 2026-10-12: UK/GERMANY/FRANCE/NETHERLANDS return 200;
        // SPAINwind_regions_ and ITALYwind_regions_ are genuine 404s.
        foreach (var region in new[] { "UK", "GERMANY", "FRANCE", "NETHERLANDS" })
            Assert.Contains(region, CwgDescriptors.WindForecastSubRegion.Regions);

        Assert.DoesNotContain("SPAIN", CwgDescriptors.WindForecastSubRegion.Regions);
        Assert.DoesNotContain("ITALY", CwgDescriptors.WindForecastSubRegion.Regions);
    }
}
