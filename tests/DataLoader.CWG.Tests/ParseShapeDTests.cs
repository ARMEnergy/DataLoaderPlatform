using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape D — stacked sub-region matrix blocks → unpivot per block (docs/apis §10,
/// design §4). A block starts at a <c>&lt;SubRegion&gt; region,,,,,</c> label row,
/// followed by a <c>Date (EST)</c> header whose width (15 here, NOT hardcoded 16)
/// drives the matrix, then 24 hour rows. The <c>" region"</c> suffix is stripped,
/// keeping numeric prefixes / hyphens.
/// </summary>
public class ParseShapeDTests
{
    private static readonly ShapeDParser Parser = new();

    private static CwgWorkUnit Unit(string region, DateOnly init, string filename) => new()
    {
        EndpointId = "WindForecastSubRegion",
        Region = region,
        RepresentativeDate = init,
        Filename = filename,
        KeyValue = "k"
    };

    // ---------------------------------------------------------------- ERCOT (8 blocks; 15 date cols; init..init+14; prefixes/hyphens kept)

    [Fact]
    public void WindForecastSubRegion_Ercot_8Blocks_15DateCols_SuffixStripped_KeepsHyphenatedPrefixes()
    {
        var csv = SampleData.Read("ERCOTwind_regions_08112026.csv");
        var init = new DateOnly(2026, 8, 11);
        var unit = Unit("ERCOT", init, "ERCOTwind_regions_08112026.csv");

        var cells = Parser.Parse(CwgDescriptors.WindForecastSubRegion, unit, csv, NullLogger.Instance);

        // 8 sub-region blocks, ' region' stripped, hyphenated/prefixed labels preserved.
        var subRegions = cells.Select(c => c.SubRegion).Distinct().ToList();
        Assert.Equal(
            new[] { "North", "South", "West", "Geo-North", "Geo-South", "Geo-West", "Geo-Panhandle", "Geo-Coastal" }
                .OrderBy(s => s),
            subRegions.OrderBy(s => s));

        // Width is read from the block's Date (EST) header: 15 forecast dates, init..init+14.
        var northDates = cells.Where(c => c.SubRegion == "North").Select(c => c.ForecastDate).Distinct().OrderBy(d => d).ToList();
        Assert.Equal(15, northDates.Count);
        Assert.Equal(init, northDates.First());            // first column IS the file date (no init-1 here)
        Assert.Equal(init.AddDays(14), northDates.Last());

        // Every block is a full 15 x 24 matrix.
        foreach (var sr in subRegions)
        {
            var block = cells.Where(c => c.SubRegion == sr).ToList();
            Assert.Equal(15, block.Select(c => c.ForecastDate).Distinct().Count());
            Assert.Equal(24, block.Select(c => c.HourOfDay).Distinct().Count());
        }

        // Row factory carries parent Region + InitDate from the unit, SubRegion from the block.
        var rows = cells.Select(c => WindForecastSubRegionRow.From(c, unit)).Where(r => r is not null).Cast<WindForecastSubRegionRow>().ToList();
        var northMidnightFirst = Assert.Single(rows, r =>
            r.SubRegion == "North" && r.HourOfDay == 0 && r.ForecastDate == init);
        Assert.Equal("ERCOT", northMidnightFirst.Region);
        Assert.Equal(init, northMidnightFirst.InitDate);
        Assert.Equal(2120m, northMidnightFirst.ValueMw);
    }

    // ---------------------------------------------------------------- CAISO (SP-15 / NP-15)

    [Fact]
    public void WindForecastSubRegion_Caiso_SubRegionsAreSp15Np15()
    {
        var csv = SampleData.Read("CAISOwind_regions_08112026.csv");
        var init = new DateOnly(2026, 8, 11);
        var unit = Unit("CAISO", init, "CAISOwind_regions_08112026.csv");

        var cells = Parser.Parse(CwgDescriptors.WindForecastSubRegion, unit, csv, NullLogger.Instance);

        Assert.Equal(new[] { "SP-15", "NP-15" }.OrderBy(s => s),
            cells.Select(c => c.SubRegion).Distinct().OrderBy(s => s));
        Assert.All(cells, c => Assert.Equal(15, cells.Where(x => x.SubRegion == c.SubRegion).Select(x => x.ForecastDate).Distinct().Count()));
    }

    // ---------------------------------------------------------------- width read from header (not hardcoded 16) + 3-blank tolerance

    [Fact]
    public void ShapeD_WidthReadFromHeader_And3BlankSeparatorsTolerated()
    {
        // Two blocks each with a DELIBERATE 3-column header (not 15/16); 3 blank separator
        // rows between them. The parser must honor the header-derived width per block.
        var csv =
            "Alpha region,,,,,\n" +
            "Date (EST),8/11/2026,8/12/2026,8/13/2026\n" +
            "12:00 AM,10,11,12\n" +
            "1:00 AM,13,14,15\n" +
            ",,,,,\n" +
            ",,,,,\n" +
            ",,,,,\n" +
            "Beta-1 region,,,,,\n" +
            "Date (EST),8/11/2026,8/12/2026\n" +
            "12:00 AM,20,21\n";
        var init = new DateOnly(2026, 8, 11);
        var unit = Unit("ERCOT", init, "x.csv");

        var cells = Parser.Parse(CwgDescriptors.WindForecastSubRegion, unit, csv, NullLogger.Instance);

        Assert.Equal(new[] { "Alpha", "Beta-1" }, cells.Select(c => c.SubRegion).Distinct().ToArray());
        Assert.Equal(3, cells.Count(c => c.SubRegion == "Alpha" && c.HourOfDay == 0)); // width 3
        Assert.Equal(2, cells.Count(c => c.SubRegion == "Beta-1" && c.HourOfDay == 0)); // width 2 (independent)
        // Alpha midnight, first date -> 10.
        Assert.Equal("10", Assert.Single(cells, c => c.SubRegion == "Alpha" && c.HourOfDay == 0 && c.ForecastDate == init).RawValue);
    }
}
