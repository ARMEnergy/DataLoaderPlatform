using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape A — the three per-region weighted-degree-day siblings of NationalDegreeDays
/// (#16 Regions5, #17 Regions9, #18 ISO), all in the shared
/// <c>northamerica_{subregion}_wdd_{date}.csv</c> family. Driven by the REAL captured
/// wdd CSVs. Covers field→property mapping (REGION_NAME from Fields[1]; RunDate from
/// the work unit, NOT the CSV; IS_FORECAST True/False→bool), the trailing <c>END.</c>
/// footer exclusion (data-row COUNT = regions × dates), and the ISO 11-column divergence
/// (POP_HDD family + POP_CDD family + IsForecast only — no NG_HDD, ELEC or weight fields).
/// The shape parser and row factories are pure (no HTTP/DB).
/// </summary>
public class RegionsDegreeDaysParseTests
{
    private static readonly ShapeAParser Parser = new();

    // Filename date 2026-08-11 → RunDate. The fixtures' CSV DATES span 2026-08-04..2026-08-25
    // (22 dates), so RunDate (08-11) is deliberately different from the first data row's date.
    private static readonly DateOnly RunDate = new(2026, 8, 11);

    private static CwgWorkUnit Unit(string endpointId, string subregion) => new()
    {
        EndpointId = endpointId,
        Region = "northamerica",
        Variant = subregion,
        RepresentativeDate = RunDate,
        Filename = $"northamerica_{subregion}_wdd_20260811.csv",
        KeyValue = "k"
    };

    private static List<Regions5DegreeDaysRow> Parse5()
    {
        var unit = Unit("Regions5DegreeDays", "5region");
        var csv = SampleData.Read("northamerica_5region_wdd_20260811.csv");
        return Parser.Parse(CwgDescriptors.Regions5DegreeDays, unit, csv, NullLogger.Instance)
            .Select(r => Regions5DegreeDaysRow.From(r, unit))
            .Where(r => r is not null).Cast<Regions5DegreeDaysRow>().ToList();
    }

    private static List<Regions9DegreeDaysRow> Parse9()
    {
        var unit = Unit("Regions9DegreeDays", "9region");
        var csv = SampleData.Read("northamerica_9region_wdd_20260811.csv");
        return Parser.Parse(CwgDescriptors.Regions9DegreeDays, unit, csv, NullLogger.Instance)
            .Select(r => Regions9DegreeDaysRow.From(r, unit))
            .Where(r => r is not null).Cast<Regions9DegreeDaysRow>().ToList();
    }

    private static List<ISODegreeDaysRow> ParseIso()
    {
        var unit = Unit("ISODegreeDays", "iso");
        var csv = SampleData.Read("northamerica_iso_wdd_20260811.csv");
        return Parser.Parse(CwgDescriptors.ISODegreeDays, unit, csv, NullLogger.Instance)
            .Select(r => ISODegreeDaysRow.From(r, unit))
            .Where(r => r is not null).Cast<ISODegreeDaysRow>().ToList();
    }

    // ================================================================ 5region (18 cols; +REGION_NAME, +weights)

    [Fact]
    public void Regions5_RealCsv_MapsFields_RegionNameFromCol1_RunDateFromUnit_ExactDecimals()
    {
        var rows = Parse5();

        // 5 regions × 22 dates; the trailing 'END.' footer is NOT counted.
        Assert.Equal(110, rows.Count);

        var first = rows[0];
        Assert.Equal(RunDate, first.RunDate);                    // RunDate from the unit (08-11)…
        Assert.Equal(new DateOnly(2026, 8, 4), first.Dates);     // …NOT the CSV DATES (08-04)
        Assert.NotEqual(first.RunDate, first.Dates);
        Assert.Equal("Pacific", first.RegionName);               // REGION_NAME = Fields[1]

        // Exact fixture spot-checks (row 1: 2026-08-04, Pacific).
        Assert.Equal(0.0071m, first.NgHdd);
        Assert.Equal(0.1000m, first.NgHdd30y);
        Assert.Equal(0.0211m, first.NgHdd10y);
        Assert.Equal(0.1927m, first.NgHddLastY);
        Assert.Equal(9.8312m, first.PopCdd);
        Assert.Equal(9.0070m, first.ElecCdd);
        Assert.False(first.IsForecast);                          // "False" → bool false
        Assert.Equal(0.1715m, first.GasWeight);
        Assert.Equal(0.1100m, first.ElctWeight);
        Assert.Equal(0.1564m, first.PopWeight);
    }

    [Fact]
    public void Regions5_DistinctRegions_AreFive_AndBothForecastFlagsPresent()
    {
        var rows = Parse5();

        var regions = rows.Select(r => r.RegionName).Distinct().ToList();
        Assert.Equal(5, regions.Count);
        Assert.Contains("Pacific", regions);
        Assert.Contains("East", regions);
        Assert.Contains("South Central", regions);

        // Observed (False) and forecast (True) rows both present; a late/forecast date is True.
        Assert.Contains(rows, r => !r.IsForecast);
        Assert.Contains(rows, r => r.IsForecast);
        var eastForecast = Assert.Single(rows, r => r.RegionName == "East" && r.Dates == new DateOnly(2026, 8, 25));
        Assert.True(eastForecast.IsForecast);
        Assert.Equal(0.0532m, eastForecast.NgHdd);
    }

    // ================================================================ 9region (18 cols; identical layout to 5region)

    [Fact]
    public void Regions9_RealCsv_MapsFields_NineRegions_ExactDecimals()
    {
        var rows = Parse9();

        // 9 regions × 22 dates; 'END.' footer excluded.
        Assert.Equal(198, rows.Count);

        var regions = rows.Select(r => r.RegionName).Distinct().ToList();
        Assert.Equal(9, regions.Count);
        Assert.Contains("NEW ENGLAND", regions);   // in-file names carry spaces
        Assert.Contains("PACIFIC", regions);

        // First data row: 2026-08-04, NEW ENGLAND, NG_HDD 0.0000, POP_CDD 6.7232, ELEC_CDD 6.7062, weights.
        var first = rows[0];
        Assert.Equal(RunDate, first.RunDate);
        Assert.Equal(new DateOnly(2026, 8, 4), first.Dates);
        Assert.Equal("NEW ENGLAND", first.RegionName);
        Assert.Equal(0.0000m, first.NgHdd);
        Assert.Equal(6.7232m, first.PopCdd);
        Assert.Equal(6.7062m, first.ElecCdd);
        Assert.False(first.IsForecast);
        Assert.Equal(0.0396m, first.GasWeight);
        Assert.Equal(0.0349m, first.ElctWeight);
        Assert.Equal(0.0459m, first.PopWeight);

        Assert.Contains(rows, r => r.IsForecast);
        Assert.Contains(rows, r => !r.IsForecast);
    }

    // ================================================================ ISO (divergent 11 cols; POP_HDD family; no ELEC/weights)

    [Fact]
    public void Iso_RealCsv_MapsFields_TwentyOneRegions_ExactDecimals()
    {
        var rows = ParseIso();

        // 21 regions × 22 dates; 'END.' footer excluded.
        Assert.Equal(462, rows.Count);

        var regions = rows.Select(r => r.RegionName).Distinct().ToList();
        Assert.Equal(21, regions.Count);
        Assert.Contains("ERCOT", regions);
        Assert.Contains("CAISO NORTH", regions);

        // First data row: 2026-08-04, ERCOT, POP_HDD 0.0000 … POP_CDD 23.9600.
        var first = rows[0];
        Assert.Equal(RunDate, first.RunDate);
        Assert.Equal(new DateOnly(2026, 8, 4), first.Dates);
        Assert.Equal("ERCOT", first.RegionName);
        Assert.Equal(0.0000m, first.PopHdd);          // ISO HDD family is POP_HDD (not NG_HDD)
        Assert.Equal(0.0175m, first.PopHdd30y);
        Assert.Equal(0.0000m, first.PopHdd10y);
        Assert.Equal(0.0000m, first.PopHddLastY);
        Assert.Equal(23.9600m, first.PopCdd);
        Assert.Equal(21.2684m, first.PopCdd30y);
        Assert.Equal(22.5266m, first.PopCdd10y);
        Assert.Equal(17.2050m, first.PopCddLastY);
        Assert.False(first.IsForecast);

        Assert.Contains(rows, r => r.IsForecast);
        Assert.Contains(rows, r => !r.IsForecast);
    }

    [Fact]
    public void Iso_RowShape_HasPopHddAndPopCddFamiliesAndIsForecastOnly_NoNgHddElecOrWeights()
    {
        var props = typeof(ISODegreeDaysRow).GetProperties().Select(p => p.Name).ToHashSet();

        // Present: the POP_HDD family + POP_CDD family + IsForecast (+ the natural key columns).
        foreach (var p in new[]
                 {
                     "FileLogId", "RunDate", "Dates", "RegionName",
                     "PopHdd", "PopHdd30y", "PopHdd10y", "PopHddLastY",
                     "PopCdd", "PopCdd30y", "PopCdd10y", "PopCddLastY", "IsForecast"
                 })
            Assert.Contains(p, props);

        // Absent: the ISO layout has NO NG_HDD family, NO ELEC family, NO weight columns.
        Assert.DoesNotContain("NgHdd", props);
        Assert.DoesNotContain("NgHdd30y", props);
        Assert.DoesNotContain("ElecCdd", props);
        Assert.DoesNotContain("GasWeight", props);
        Assert.DoesNotContain("ElctWeight", props);
        Assert.DoesNotContain("PopWeight", props);

        // Exactly the 13 expected columns — proves nothing extra leaked in.
        Assert.Equal(13, props.Count);
    }

    // ================================================================ END. footer skip (ShapeAParser branch)

    [Fact]
    public void ShapeA_EndFooter_WithTrailingComma_ReachesParser_AndIsSkippedAsDebug_NotWidthWarning()
    {
        // A bare `END.` line is dropped by CwgCsv (treated as EOF); with a trailing comma it
        // instead surfaces to ShapeAParser as an ["END.",""] record, exercising the parser's
        // own `END.` guard directly (ShapeParsers.cs). It must be skipped (Debug), and must NOT
        // trip the short-row width Warning.
        var log = new ListLogger();
        var unit = Unit("Regions5DegreeDays", "5region");
        var csv =
            "DATES,REGION_NAME,NG_HDD,30Y_NG_HDD,10Y_NG_HDD,LAST_Y_NG_HDD,POP_CDD,30Y_POP_CDD,10Y_POP_CDD,LAST_Y_POP_CDD,ELEC_CDD,30Y_ELEC_CDD,10Y_ELEC_CDD,LAST_Y_ELEC_CDD,IS_FORECAST,GAS_WEIGHT,ELCT_WEIGHT,POP_WEIGHT\n" +
            "2026-08-04,Pacific,0.0071,0.1000,0.0211,0.1927,9.8312,6.7685,8.1944,3.9454,9.0070,6.3534,7.7700,3.5287,False,0.1715,0.1100,0.1564\n" +
            "END.,\n";

        var records = Parser.Parse(CwgDescriptors.Regions5DegreeDays, unit, csv, log);

        var rec = Assert.Single(records);                       // only the real data row survives
        var row = Regions5DegreeDaysRow.From(rec, unit);
        Assert.Equal("Pacific", row!.RegionName);
        Assert.Empty(log.OfLevel(LogLevel.Warning));                               // width guard never fired
        Assert.Contains(log.OfLevel(LogLevel.Debug), e => e.Message.Contains("END."));
    }
}
