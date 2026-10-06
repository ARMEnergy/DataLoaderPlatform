using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape E — region-row summary, single- or multi-block (docs/apis §12–§14, design
/// §4). Title rows drive the current Block (Current/Yesterday/Change); header +
/// <c>Total All (MW)</c>/<c>Total Percent</c>/<c>Total Change</c> footers are
/// skipped; the row factory strips <c>%</c> (keeping sign) or reads MW, and leaves
/// <c>TotalCapacityMw</c> null when the Change block blanks it.
/// </summary>
public class ParseShapeETests
{
    private static readonly ShapeEParser Parser = new();

    private static CwgWorkUnit Unit(string endpointId, DateOnly prod, string filename) => new()
    {
        EndpointId = endpointId,
        RepresentativeDate = prod,
        Filename = filename,
        KeyValue = "k"
    };

    private static readonly string[] NineRegions =
        { "MISO", "ERCOT", "CAISO", "SPP", "PJM", "NYISO", "NEPOOL", "NW", "SW" };

    // ---------------------------------------------------------------- Pct (3 blocks; %-strip keeps sign; Change TotalCapacity -> NULL)

    [Fact]
    public void WindTotalCapacityPct_RealCsv_3Blocks_BlockStamped_FootersSkipped_SignedPercent_ChangeCapacityNull()
    {
        var csv = SampleData.Read("Total_Capacity_08112026.csv");
        var prod = new DateOnly(2026, 8, 11);
        var unit = Unit("WindTotalCapacityPct", prod, "Total_Capacity_08112026.csv");

        var caps = Parser.Parse(CwgDescriptors.WindTotalCapacityPct, unit, csv, NullLogger.Instance);

        // The PARSER still sees 3 blocks x 9 regions, footers dropped. (Block lives on the
        // parser record; it no longer survives into the row — arm.WindTotalCapacityPct keys
        // on (ForecastDate, Region) only, so just the Current block is written.)
        Assert.Equal(new[] { "Current", "Yesterday", "Change" }, caps.Select(c => c.Block).Distinct().ToArray());
        Assert.Equal(27, caps.Count);
        foreach (var block in new[] { "Current", "Yesterday", "Change" })
            Assert.Equal(NineRegions.OrderBy(s => s), caps.Where(c => c.Block == block).Select(c => c.Region).OrderBy(s => s));

        // No footer region leaked in.
        Assert.DoesNotContain(caps, c => c.Region.StartsWith("Total", StringComparison.OrdinalIgnoreCase));

        // Sign preservation and the blank Change capacity are properties of the RAW cells,
        // so they are still observable even though those blocks are not written.
        var chgErcotCap = Assert.Single(caps, c => c.Block == "Change" && c.Region == "ERCOT");
        Assert.Equal(string.Empty, chgErcotCap.TotalCapacityRaw.Trim());
        Assert.True(CwgParse.PercentOrNull(chgErcotCap.Avg6_10Raw, out var ercot610));
        Assert.Equal(-1.00m, ercot610);            // sign preserved through the %-strip

        var rows = caps.Select(c => WindTotalCapacityPctRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityPctRow>().ToList();

        // Only the Current block is written — 9 rows, key unique.
        Assert.Equal(9, rows.Count);
        Assert.Equal(9, rows.Select(r => (r.ForecastDate, r.Region)).Distinct().Count());
        Assert.Equal(NineRegions.OrderBy(s => s), rows.Select(r => r.Region).OrderBy(s => s));

        var miso = Assert.Single(rows, r => r.Region == "MISO");
        Assert.Equal(prod, miso.ForecastDate);
        Assert.Equal(33687m, miso.TotalCapacity);
        Assert.Equal(21.00m, miso.OneToFiveDayAverage);   // %-stripped
        Assert.Equal(22.00m, miso.SixToTenDayAverage);
        Assert.Equal(20.00m, miso.ElevenToFifteenDayAverage);
    }

    // ---------------------------------------------------------------- MW (3 blocks parsed, Current written)

    [Fact]
    public void WindTotalCapacityMW_RealCsv_3BlocksParsed_SignedMwInChange_OnlyCurrentWritten()
    {
        var csv = SampleData.Read("Total_Capacity_vals_08112026.csv");
        var prod = new DateOnly(2026, 8, 11);
        var unit = Unit("WindTotalCapacityMW", prod, "Total_Capacity_vals_08112026.csv");

        var caps = Parser.Parse(CwgDescriptors.WindTotalCapacityMW, unit, csv, NullLogger.Instance);

        Assert.Equal(27, caps.Count);
        Assert.Equal(new[] { "Current", "Yesterday", "Change" }, caps.Select(c => c.Block).Distinct().ToArray());

        // Signed MW deltas in the Change block are still parseable from the raw cells.
        var chgErcot = Assert.Single(caps, c => c.Block == "Change" && c.Region == "ERCOT");
        Assert.Equal(string.Empty, chgErcot.TotalCapacityRaw.Trim());
        Assert.True(CwgParse.MeasureOrNull(chgErcot.Avg1_5Raw, out var d1));
        Assert.Equal(-337m, d1);

        var rows = caps.Select(c => WindTotalCapacityMWRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityMWRow>().ToList();

        Assert.Equal(9, rows.Count);
        var miso = Assert.Single(rows, r => r.Region == "MISO");
        Assert.Equal(33687m, miso.TotalCapacity);
        Assert.Equal(prod, miso.ForecastDate);
    }

    // ---------------------------------------------------------------- Climatology (single block; NO Block column; title '...Climo...')

    [Fact]
    public void WindTotalCapacityClimatology_RealCsv_SingleBlock_NoBlockColumn_9Regions()
    {
        var csv = SampleData.Read("Total_Capacity_climo_08112026.csv");
        var prod = new DateOnly(2026, 8, 11);
        var unit = Unit("WindTotalCapacityClimatology", prod, "Total_Capacity_climo_08112026.csv");

        var caps = Parser.Parse(CwgDescriptors.WindTotalCapacityClimatology, unit, csv, NullLogger.Instance);
        var rows = caps.Select(c => WindTotalCapacityClimatologyRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityClimatologyRow>().ToList();

        // Single block ('...Climo Across All Regions' still counts as one block); 9 regions; footers skipped.
        Assert.Single(caps.Select(c => c.Block).Distinct());
        Assert.Equal(9, rows.Count);
        Assert.Equal(NineRegions.OrderBy(s => s), rows.Select(r => r.Region).OrderBy(s => s));

        var miso = Assert.Single(rows, r => r.Region == "MISO");
        Assert.Equal(prod, miso.ForecastDate);              // the file date (dbo calls it ForecastDate)
        Assert.Equal(33687m, miso.TotalCapacity);           // climo TotalCapacity is never blank
        Assert.Equal(23.00m, miso.OneToFiveDayAverage);     // %-stripped
        Assert.Equal(24.00m, miso.SixToTenDayAverage);
        Assert.Equal(25.00m, miso.ElevenToFifteenDayAverage);

        // No Block property on ANY capacity row type now — the key is (ForecastDate, Region).
        Assert.DoesNotContain("Block", typeof(WindTotalCapacityClimatologyRow).GetProperties().Select(p => p.Name));
    }

    // ---------------------------------------------------------------- weekday titles + short Change block

    [Fact]
    public void WindTotalCapacityPct_WeekdayTitles_ParsedIntoThreeBlocks_OnlyCurrentIsWritten()
    {
        // Two real-world variations from a Monday file: CWG names the actual weekday
        // ("Friday's Forecast" / "Change from Friday's Forecast") instead of "Yesterday",
        // and the Change block omits the trailing 11-15 horizon (4 cols, not 5).
        //
        // The PARSER still resolves all three blocks (that is what ExpectedBlocks=3 checks).
        // The ROW FACTORY then keeps only 'Current': arm.WindTotalCapacityPct keys on
        // (ForecastDate, Region) with no Block column, so the other two blocks would land on
        // the same key and overwrite it. They are dropped, and the module's sentinel predicate
        // claims that drop so it is logged at Debug rather than counted as a parse failure.
        var csv =
            "Total Wind Generation Across All Regions\n" +
            ",Total Capacity (MW),1-5 Day Avg (%),6-10 Day Avg (%),11-15 Day Avg (%)\n" +
            "MISO,33687,21%,22%,20%\n" +
            "ERCOT,40661,54%,30%,23%\n" +
            "Total Percent,,36%,25%,22%\n" +
            "\n" +
            "Friday's Forecast\n" +
            "MISO,33687,19%,20%,20%\n" +
            "ERCOT,40661,54%,31%,20%\n" +
            "Total Percent,,36%,25%,21%\n" +
            "\n" +
            "Change from Friday's Forecast\n" +
            "MISO,,0%,7%\n" +
            "ERCOT,,3%,7%\n" +
            "Total Percent,,2%,7%\n";
        var unit = Unit("WindTotalCapacityPct", new DateOnly(2026, 7, 27), "Total_Capacity_07272026.csv");

        var caps = Parser.Parse(CwgDescriptors.WindTotalCapacityPct, unit, csv, NullLogger.Instance);

        // The parser still resolves all three blocks from the weekday titles.
        Assert.Equal(new[] { "Current", "Yesterday", "Change" }, caps.Select(c => c.Block).Distinct().ToArray());
        Assert.Equal(6, caps.Count);  // 2 regions x 3 blocks; footers dropped

        var rows = caps.Select(c => WindTotalCapacityPctRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityPctRow>().ToList();

        // ...but only the Current block is written.
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(r => (r.ForecastDate, r.Region)).Distinct().Count()); // key is unique
        Assert.DoesNotContain(rows, r => r.Region.StartsWith("Total", StringComparison.OrdinalIgnoreCase));

        var miso = Assert.Single(rows, r => r.Region == "MISO");
        Assert.Equal(21.00m, miso.OneToFiveDayAverage);   // the CURRENT block's value, not Friday's 19%
        Assert.Equal(22.00m, miso.SixToTenDayAverage);
        Assert.Equal(20.00m, miso.ElevenToFifteenDayAverage);
        Assert.Equal(33687m, miso.TotalCapacity);

        // The dropped blocks are claimed by the module's sentinel predicate, so the source
        // reader logs them at Debug instead of warning about unparseable records.
        var dropped = caps.Where(c => WindTotalCapacityPctRow.From(c, unit) is null).ToList();
        Assert.Equal(4, dropped.Count);
        Assert.All(dropped, c => Assert.False(CwgCapacity.IsCurrent(c)));
    }

    [Fact]
    public void WindTotalCapacityMW_OnlyCurrentBlockIsWritten()
    {
        var csv = SampleData.Read("Total_Capacity_vals_08112026.csv");
        var prod = new DateOnly(2026, 8, 11);
        var unit = Unit("WindTotalCapacityMW", prod, "Total_Capacity_vals_08112026.csv");

        var caps = Parser.Parse(CwgDescriptors.WindTotalCapacityMW, unit, csv, NullLogger.Instance);
        var rows = caps.Select(c => WindTotalCapacityMWRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityMWRow>().ToList();

        Assert.Equal(3, caps.Select(c => c.Block).Distinct().Count());        // file still has 3 blocks
        Assert.Equal(caps.Count(CwgCapacity.IsCurrent), rows.Count);          // only Current written
        Assert.Equal(rows.Count, rows.Select(r => (r.ForecastDate, r.Region)).Distinct().Count());
        Assert.All(rows, r => Assert.Equal(prod, r.ForecastDate));
    }
}
