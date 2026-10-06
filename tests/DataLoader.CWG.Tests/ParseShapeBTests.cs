using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape B — wide-by-region, kept WIDE (docs/apis §4/§7/§11, design §4). Validates the
/// region column set against the descriptor's FULL in-file set, emits one record per
/// source row, and the per-endpoint row factory projects the descriptor's narrower
/// TARGET set into the row's fixed column order. The literal <c>"NULL"</c>/blank
/// sentinel becomes a NULL column on a kept row.
/// </summary>
public class ParseShapeBTests
{
    private static readonly ShapeBParser Parser = new();

    private static CwgWorkUnit Unit(string endpointId, string filename) => new()
    {
        EndpointId = endpointId,
        RepresentativeDate = null,
        Filename = filename,
        KeyValue = "k"
    };

    /// <summary>Reads one target column out of a wide row by name.</summary>
    private static decimal? Col(string[] columns, decimal?[] values, string name) =>
        values[Array.IndexOf(columns, name)];

    // ---------------------------------------------------------------- Descriptor invariants

    [Fact]
    public void Descriptors_WideSetsAreTheFullInFileSets_TargetSetsAreNarrower()
    {
        // The VALIDATION set must stay complete or a real new region column stops being fatal.
        Assert.Equal(
            new[] { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" },
            CwgDescriptors.DailyNormal.WideRegionColumns);
        Assert.Equal(
            new[] { "ERCOT", "CAISO", "PJM", "AESO", "MISO", "IESO", "NEPOOL", "NW", "SW", "SPP", "FRCC", "CAR", "SE", "TVA" },
            CwgDescriptors.SolarHourly.WideRegionColumns);
        Assert.Equal(
            new[] { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" },
            CwgDescriptors.WindHourly.WideRegionColumns);

        // The WRITE set is deliberately narrower: NW/SW dropped, SolarHourly down to 2 of 14.
        Assert.Equal(new[] { "NW", "SW" },
            CwgDescriptors.DailyNormal.WideRegionColumns!
                .Except(CwgDescriptors.DailyNormal.TargetRegionColumns!).ToArray());
        Assert.Equal(new[] { "NW", "SW" },
            CwgDescriptors.WindHourly.WideRegionColumns!
                .Except(CwgDescriptors.WindHourly.TargetRegionColumns!).ToArray());
        Assert.Equal(new[] { "ERCOT", "CAISO" }, CwgDescriptors.SolarHourly.TargetRegionColumns);
    }

    [Fact]
    public void Descriptors_TargetRegionColumns_MatchTheRowTypesTheyFeed()
    {
        // The row type's RegionColumns drives BOTH the projection and the sink's DataTable
        // column names, so a drift between it and the descriptor would silently write the
        // wrong regions into the wrong columns.
        Assert.Equal(CwgDescriptors.DailyNormal.TargetRegionColumns, DailyNormalRow.RegionColumns);
        Assert.Equal(CwgDescriptors.SolarHourly.TargetRegionColumns, SolarHourlyRow.RegionColumns);
        Assert.Equal(CwgDescriptors.WindHourly.TargetRegionColumns, WindHourlyRow.RegionColumns);
    }

    // ---------------------------------------------------------------- DailyNormal (MM-DD -> Month/Day, 366 rows incl 02-29)

    [Fact]
    public void DailyNormal_RealCsv_OneWideRowPerDay_366DaysIncl0229()
    {
        var csv = SampleData.Read("daily_normals.csv");
        var unit = Unit("DailyNormal", "daily_normals.csv");

        var records = Parser.Parse(CwgDescriptors.DailyNormal, unit, csv, NullLogger.Instance);
        var rows = records.Select(c => DailyNormalRow.From(c, unit)).Where(r => r is not null).Cast<DailyNormalRow>().ToList();

        // ONE row per calendar day now (was 366 x 12 unpivoted cells).
        Assert.Equal(366, rows.Count);
        Assert.Equal(366, rows.Select(r => (r.Month, r.Day)).Distinct().Count());
        Assert.Contains(rows, r => r.Month == 2 && r.Day == 29);   // leap day survives the MM-DD split

        var jan1 = Assert.Single(rows, r => r.Month == 1 && r.Day == 1);
        Assert.Equal(10, jan1.Values.Length);
        Assert.Equal(3371m, Col(DailyNormalRow.RegionColumns, jan1.Values, "CAISO"));
        Assert.Equal(14560m, Col(DailyNormalRow.RegionColumns, jan1.Values, "SPP"));
        Assert.Equal(15156m, Col(DailyNormalRow.RegionColumns, jan1.Values, "ERCOT"));
        Assert.Equal(2238m, Col(DailyNormalRow.RegionColumns, jan1.Values, "AESO"));

        // NW/SW are present in the file and read by the parser, but are NOT written.
        var jan1Record = records.First(r => r.KeyCell.Trim() == "01-01");
        Assert.True(jan1Record.ByRegion.ContainsKey("NW"));
        Assert.True(jan1Record.ByRegion.ContainsKey("SW"));
        Assert.DoesNotContain("NW", DailyNormalRow.RegionColumns);
        Assert.DoesNotContain("SW", DailyNormalRow.RegionColumns);
    }

    [Fact]
    public void DailyNormal_UnparseableMonthDay_DropsRow()
    {
        var csv =
            "DATE,CAISO,SPP,ERCOT,MISO,PJM,NEPOOL,NYISO,BPA,IESO,AESO,NW,SW\n" +
            "not-a-day,1,2,3,4,5,6,7,8,9,10,11,12\n";
        var unit = Unit("DailyNormal", "daily_normals.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.DailyNormal, unit, csv, NullLogger.Instance));
        Assert.Null(DailyNormalRow.From(rec, unit));
    }

    // ---------------------------------------------------------------- SolarHourly ("NULL" sentinel -> NULL column)

    [Fact]
    public void SolarHourly_RealCsv_KeepsEveryHour_NullSentinelBecomesNullColumn()
    {
        var csv = SampleData.Read("Gen_hrly_solar.csv");
        var unit = Unit("SolarHourly", "Gen_hrly_solar.csv");

        var records = Parser.Parse(CwgDescriptors.SolarHourly, unit, csv, NullLogger.Instance);
        var rows = records.Select(c => SolarHourlyRow.From(c, unit)).Where(r => r is not null).Cast<SolarHourlyRow>().ToList();

        // All 14 region columns are recognized (no unknown-column exception)...
        Assert.Equal(14, records[0].ByRegion.Count);
        // ...and every source row becomes exactly one wide row — nothing is dropped for being NULL.
        Assert.Equal(records.Count, rows.Count);

        var first = Assert.Single(rows, r => r.UTCHourEnding == new DateTime(2020, 7, 26, 15, 0, 0));
        Assert.Equal(2, first.Values.Length);
        Assert.Equal(3100m, Col(SolarHourlyRow.RegionColumns, first.Values, "ERCOT"));
        Assert.Null(Col(SolarHourlyRow.RegionColumns, first.Values, "CAISO")); // literal "NULL" -> NULL
    }

    [Fact]
    public void SolarHourly_RowFactory_BadKeyCell_Dropped()
    {
        var unit = Unit("SolarHourly", "Gen_hrly_solar.csv");
        var good = new CwgWideRow("2020-07-26 15:00:00",
            new Dictionary<string, string> { ["ERCOT"] = "3100", ["CAISO"] = "NULL" });
        var badTs = new CwgWideRow("not-a-timestamp",
            new Dictionary<string, string> { ["ERCOT"] = "3100" });

        var row = SolarHourlyRow.From(good, unit);
        Assert.NotNull(row);
        Assert.Equal(new DateTime(2020, 7, 26, 15, 0, 0), row!.UTCHourEnding);
        Assert.Equal(3100m, Col(SolarHourlyRow.RegionColumns, row.Values, "ERCOT"));
        Assert.Null(Col(SolarHourlyRow.RegionColumns, row.Values, "CAISO"));

        Assert.Null(SolarHourlyRow.From(badTs, unit)); // unparseable key cell -> row dropped
    }

    [Fact]
    public void SolarHourly_AbsentTargetColumn_BecomesNull()
    {
        // A region the file simply does not publish is absent from ByRegion -> NULL, not a drop.
        var unit = Unit("SolarHourly", "Gen_hrly_solar.csv");
        var row = SolarHourlyRow.From(
            new CwgWideRow("2020-07-26 15:00:00", new Dictionary<string, string> { ["ERCOT"] = "3100" }),
            unit);

        Assert.NotNull(row);
        Assert.Equal(3100m, Col(SolarHourlyRow.RegionColumns, row!.Values, "ERCOT"));
        Assert.Null(Col(SolarHourlyRow.RegionColumns, row.Values, "CAISO"));
    }

    [Fact]
    public void SolarHourly_GarbageTargetCell_DropsRow()
    {
        var unit = Unit("SolarHourly", "Gen_hrly_solar.csv");
        var row = SolarHourlyRow.From(
            new CwgWideRow("2020-07-26 15:00:00",
                new Dictionary<string, string> { ["ERCOT"] = "3100", ["CAISO"] = "n/a" }),
            unit);

        Assert.Null(row); // present but unparseable -> drop, so the reader warns
    }

    // ---------------------------------------------------------------- WindHourly

    [Fact]
    public void WindHourly_RealCsv_OneWideRowPerHour_TenWrittenRegions()
    {
        var csv = SampleData.Read("Gen_hrly_5day.csv");
        var unit = Unit("WindHourly", "Gen_hrly_5day.csv");

        var records = Parser.Parse(CwgDescriptors.WindHourly, unit, csv, NullLogger.Instance);
        var rows = records.Select(c => WindHourlyRow.From(c, unit)).Where(r => r is not null).Cast<WindHourlyRow>().ToList();

        Assert.Equal(12, records[0].ByRegion.Count);       // 12 read from the file
        Assert.Equal(records.Count, rows.Count);           // every hour kept
        Assert.All(rows, r => Assert.Equal(10, r.Values.Length)); // 10 written

        var first = Assert.Single(rows, r => r.UTCHourEnding == new DateTime(2026, 8, 6, 22, 0, 0));
        Assert.Equal(1785m, Col(WindHourlyRow.RegionColumns, first.Values, "CAISO"));
    }

    [Fact]
    public void ShapeB_UnknownRegionColumn_Throws_UpdateDescriptorSignal()
    {
        // Still fatal — narrowing the WRITE set must not relax the validation set.
        var csv =
            "UTC_HOUR_ENDING,ERCOT,ZZZ\n" +
            "2026-08-06 22:00:00,100,200\n";
        var unit = Unit("WindHourly", "Gen_hrly_5day.csv");

        Assert.Throws<FormatException>(() => Parser.Parse(CwgDescriptors.WindHourly, unit, csv, NullLogger.Instance));
    }

    [Fact]
    public void ShapeB_NwAndSwColumns_StillAccepted_NotFatal()
    {
        // The complement of the test above: NW/SW are not written, but they must still be
        // ACCEPTED by the parser — they are legitimately in the file.
        var csv =
            "UTC_HOUR_ENDING,CAISO,SPP,ERCOT,MISO,PJM,NEPOOL,NYISO,BPA,IESO,AESO,NW,SW\n" +
            "2026-08-06 22:00:00,1,2,3,4,5,6,7,8,9,10,11,12\n";
        var unit = Unit("WindHourly", "Gen_hrly_5day.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.WindHourly, unit, csv, NullLogger.Instance));
        var row = WindHourlyRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Equal(10, row!.Values.Length);
        Assert.Equal(1m, Col(WindHourlyRow.RegionColumns, row.Values, "CAISO"));
        Assert.Equal(10m, Col(WindHourlyRow.RegionColumns, row.Values, "AESO"));
    }
}
