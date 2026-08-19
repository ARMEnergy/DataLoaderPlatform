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
        var rows = caps.Select(c => WindTotalCapacityPctRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityPctRow>().ToList();

        // 3 blocks x 9 regions, footers dropped.
        Assert.Equal(new[] { "Current", "Yesterday", "Change" }, rows.Select(r => r.Block).Distinct().ToArray());
        Assert.Equal(27, rows.Count);
        foreach (var block in new[] { "Current", "Yesterday", "Change" })
            Assert.Equal(NineRegions.OrderBy(s => s), rows.Where(r => r.Block == block).Select(r => r.Region).OrderBy(s => s));

        // No footer region leaked in.
        Assert.DoesNotContain(rows, r => r.Region.StartsWith("Total", StringComparison.OrdinalIgnoreCase));

        // Current MISO: capacity + %-stripped avgs.
        var curMiso = Assert.Single(rows, r => r.Block == "Current" && r.Region == "MISO");
        Assert.Equal(33687m, curMiso.TotalCapacityMw);
        Assert.Equal(21.00m, curMiso.Avg_1_5);
        Assert.Equal(22.00m, curMiso.Avg_6_10);
        Assert.Equal(20.00m, curMiso.Avg_11_15);

        // Change block: TotalCapacity blank -> NULL; signs preserved (ERCOT 6-10 = -1%).
        var chgErcot = Assert.Single(rows, r => r.Block == "Change" && r.Region == "ERCOT");
        Assert.Null(chgErcot.TotalCapacityMw);
        Assert.Equal(0.00m, chgErcot.Avg_1_5);
        Assert.Equal(-1.00m, chgErcot.Avg_6_10);   // -4%->-4.00 style sign preservation
        Assert.Equal(3.00m, chgErcot.Avg_11_15);
        var chgNyiso = Assert.Single(rows, r => r.Block == "Change" && r.Region == "NYISO");
        Assert.Equal(-2.00m, chgNyiso.Avg_1_5);
        Assert.All(rows.Where(r => r.Block == "Change"), r => Assert.Null(r.TotalCapacityMw));
    }

    // ---------------------------------------------------------------- MW (3 blocks; signed MW in Change; Change TotalCapacity -> NULL)

    [Fact]
    public void WindTotalCapacityMW_RealCsv_3Blocks_SignedMw_ChangeCapacityNull()
    {
        var csv = SampleData.Read("Total_Capacity_vals_08112026.csv");
        var prod = new DateOnly(2026, 8, 11);
        var unit = Unit("WindTotalCapacityMW", prod, "Total_Capacity_vals_08112026.csv");

        var caps = Parser.Parse(CwgDescriptors.WindTotalCapacityMW, unit, csv, NullLogger.Instance);
        var rows = caps.Select(c => WindTotalCapacityMWRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityMWRow>().ToList();

        Assert.Equal(27, rows.Count);
        Assert.Equal(new[] { "Current", "Yesterday", "Change" }, rows.Select(r => r.Block).Distinct().ToArray());

        var curMiso = Assert.Single(rows, r => r.Block == "Current" && r.Region == "MISO");
        Assert.Equal(33687m, curMiso.TotalCapacityMw);

        // Change block: capacity blank -> NULL; signed MW deltas (ERCOT 1-5 = -337).
        var chgErcot = Assert.Single(rows, r => r.Block == "Change" && r.Region == "ERCOT");
        Assert.Null(chgErcot.TotalCapacityMw);
        Assert.Equal(-337m, chgErcot.Avg_1_5);
        Assert.Equal(-334m, chgErcot.Avg_6_10);
        Assert.Equal(1233m, chgErcot.Avg_11_15);
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
        Assert.Equal(prod, miso.ProductionDate);
        Assert.Equal(33687m, miso.TotalCapacityMw);   // climo TotalCapacity is never blank
        Assert.Equal(23.00m, miso.Avg_1_5);           // %-stripped
        Assert.Equal(24.00m, miso.Avg_6_10);
        Assert.Equal(25.00m, miso.Avg_11_15);

        // The climo row type carries NO Block property (single-block contract) — assert by type shape.
        Assert.DoesNotContain("Block", typeof(WindTotalCapacityClimatologyRow).GetProperties().Select(p => p.Name));
    }

    // ---------------------------------------------------------------- weekday titles + short Change block

    [Fact]
    public void WindTotalCapacityPct_WeekdayTitles_AndShortChangeBlock_LoadWithNullHorizon()
    {
        // Two real-world variations from a Monday file: CWG names the actual weekday
        // ("Friday's Forecast" / "Change from Friday's Forecast") instead of "Yesterday",
        // and the Change block omits the trailing 11-15 horizon (4 cols, not 5). Both must
        // load: weekday titles map to Current/Yesterday/Change, and the missing horizon -> NULL.
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
        var rows = caps.Select(c => WindTotalCapacityPctRow.From(c, unit)).Where(r => r is not null).Cast<WindTotalCapacityPctRow>().ToList();

        // Weekday titles resolved to the three canonical blocks; 2 regions each; footers dropped.
        Assert.Equal(new[] { "Current", "Yesterday", "Change" }, rows.Select(r => r.Block).Distinct().ToArray());
        Assert.Equal(6, rows.Count);
        Assert.DoesNotContain(rows, r => r.Region.StartsWith("Total", StringComparison.OrdinalIgnoreCase));

        // The "Friday's Forecast" middle block is stamped 'Yesterday' (the schema's Block value).
        var yMiso = Assert.Single(rows, r => r.Block == "Yesterday" && r.Region == "MISO");
        Assert.Equal(19.00m, yMiso.Avg_1_5);

        // Short Change rows load (not skipped, not mislabeled); the absent 11-15 horizon -> NULL.
        var cMiso = Assert.Single(rows, r => r.Block == "Change" && r.Region == "MISO");
        Assert.Null(cMiso.TotalCapacityMw);
        Assert.Equal(0.00m, cMiso.Avg_1_5);
        Assert.Equal(7.00m, cMiso.Avg_6_10);
        Assert.Null(cMiso.Avg_11_15);   // missing 5th column -> NULL
        var cErcot = Assert.Single(rows, r => r.Block == "Change" && r.Region == "ERCOT");
        Assert.Equal(3.00m, cErcot.Avg_1_5);
        Assert.Equal(7.00m, cErcot.Avg_6_10);
        Assert.Null(cErcot.Avg_11_15);
    }
}
