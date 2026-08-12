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
}
