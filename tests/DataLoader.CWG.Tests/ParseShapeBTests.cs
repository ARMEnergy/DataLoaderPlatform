using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape B — wide-by-region → unpivot (docs/apis §4/§7/§11, design §4). Validates
/// the region column set against the descriptor, unpivots one (key, region, value)
/// cell per present region column, and the per-endpoint row factory drops the
/// literal <c>"NULL"</c> / blank sentinel cells.
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

    // ---------------------------------------------------------------- DailyNormal (12 regions, MM-DD CHAR(5), 366 rows incl 02-29)

    [Fact]
    public void DailyNormal_RealCsv_Unpivots12Regions_KeepsMonthDayLiteral_366DaysIncl0229()
    {
        var csv = SampleData.Read("daily_normals.csv");
        var unit = Unit("DailyNormal", "daily_normals.csv");

        var cells = Parser.Parse(CwgDescriptors.DailyNormal, unit, csv, NullLogger.Instance);
        var rows = cells.Select(c => DailyNormalRow.From(c, unit)).Where(r => r is not null).Cast<DailyNormalRow>().ToList();

        // 366 calendar days x 12 regions.
        var distinctDays = rows.Select(r => r.MonthDay).Distinct().ToList();
        Assert.Equal(366, distinctDays.Count);
        Assert.Contains("02-29", distinctDays);              // leap day is present
        Assert.Equal(366 * 12, rows.Count);

        // Region axis is exactly the descriptor's 12-set, resolved by header name.
        Assert.Equal(
            new[] { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" },
            rows.Where(r => r.MonthDay == "01-01").Select(r => r.Region).ToArray());

        var jan1Caiso = Assert.Single(rows, r => r.MonthDay == "01-01" && r.Region == "CAISO");
        Assert.Equal("01-01", jan1Caiso.MonthDay);           // MM-DD kept literal (CHAR(5))
        Assert.Equal(3371m, jan1Caiso.NormalMw);
    }

    // ---------------------------------------------------------------- SolarHourly (14 regions incl FRCC/CAR/SE/TVA; "NULL" sentinel)

    [Fact]
    public void SolarHourly_Descriptor_Has14RegionSet_InclFrccCarSeTva()
    {
        Assert.Equal(
            new[] { "ERCOT", "CAISO", "PJM", "AESO", "MISO", "IESO", "NEPOOL", "NW", "SW", "SPP", "FRCC", "CAR", "SE", "TVA" },
            CwgDescriptors.SolarHourly.WideRegionColumns);
    }

    [Fact]
    public void SolarHourly_RealCsv_Recognizes14Regions_NullCellsProduceNoRow()
    {
        var csv = SampleData.Read("Gen_hrly_solar.csv");
        var unit = Unit("SolarHourly", "Gen_hrly_solar.csv");

        var cells = Parser.Parse(CwgDescriptors.SolarHourly, unit, csv, NullLogger.Instance);

        // 14 region columns recognized (no unknown-column exception), one cell each per data row.
        Assert.Equal(14, cells.Select(c => c.Region).Distinct().Count());

        var rows = cells.Select(c => SolarHourlyRow.From(c, unit)).Where(r => r is not null).Cast<SolarHourlyRow>().ToList();

        // Early history is ERCOT-only: every non-ERCOT cell is the literal "NULL" -> dropped.
        Assert.All(rows, r => Assert.Equal("ERCOT", r.Region));
        var firstErcot = rows.First(r => r.HourEndingUtc == new DateTime(2020, 7, 26, 15, 0, 0));
        Assert.Equal(3100m, firstErcot.ActualMw);

        // A "NULL" cell yields no row (proven directly on a sentinel cell).
        var nullCell = cells.First(c => c.Region == "CAISO");
        Assert.Equal("NULL", nullCell.RawValue);
        Assert.Null(SolarHourlyRow.From(nullCell, unit));
    }

    [Fact]
    public void SolarHourly_RowFactory_ValidCell_Parses_BadDateCell_Dropped()
    {
        var unit = Unit("SolarHourly", "Gen_hrly_solar.csv");
        var good = new CwgUnpivotCell("2020-07-26 15:00:00", "ERCOT", "3100");
        var badTs = new CwgUnpivotCell("not-a-timestamp", "ERCOT", "3100");

        var row = SolarHourlyRow.From(good, unit);
        Assert.NotNull(row);
        Assert.Equal(new DateTime(2020, 7, 26, 15, 0, 0), row!.HourEndingUtc);

        Assert.Null(SolarHourlyRow.From(badTs, unit)); // unparseable key cell -> whole cell dropped
    }

    // ---------------------------------------------------------------- WindHourly (12 regions)

    [Fact]
    public void WindHourly_Descriptor_Has12RegionSet()
    {
        Assert.Equal(
            new[] { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO", "NW", "SW" },
            CwgDescriptors.WindHourly.WideRegionColumns);
    }

    [Fact]
    public void WindHourly_RealCsv_Unpivots12Regions_FullyPopulated()
    {
        var csv = SampleData.Read("Gen_hrly_5day.csv");
        var unit = Unit("WindHourly", "Gen_hrly_5day.csv");

        var cells = Parser.Parse(CwgDescriptors.WindHourly, unit, csv, NullLogger.Instance);
        var rows = cells.Select(c => WindHourlyRow.From(c, unit)).Where(r => r is not null).Cast<WindHourlyRow>().ToList();

        Assert.Equal(12, cells.Select(c => c.Region).Distinct().Count());
        // Mostly populated but the file does carry scattered "NULL" cells -> exactly the
        // non-sentinel cells become rows (WindHourly maps "NULL"/blank the same as SolarHourly).
        var expected = cells.Count(c => !CwgParse.IsSentinel(c.RawValue));
        Assert.Equal(expected, rows.Count);

        var firstCaiso = Assert.Single(rows, r =>
            r.HourEndingUtc == new DateTime(2026, 8, 6, 22, 0, 0) && r.Region == "CAISO");
        Assert.Equal(1785m, firstCaiso.ActualMw);
    }

    [Fact]
    public void ShapeB_UnknownRegionColumn_Throws_UpdateDescriptorSignal()
    {
        // StormVista-style: an unexpected/extra region column is fatal.
        var csv =
            "UTC_HOUR_ENDING,ERCOT,ZZZ\n" +
            "2026-08-06 22:00:00,100,200\n";
        var unit = Unit("WindHourly", "Gen_hrly_5day.csv");

        Assert.Throws<FormatException>(() => Parser.Parse(CwgDescriptors.WindHourly, unit, csv, NullLogger.Instance));
    }
}
