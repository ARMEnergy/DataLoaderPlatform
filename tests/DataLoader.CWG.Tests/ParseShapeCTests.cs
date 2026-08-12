using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape C — pivoted hour×forecast-day matrix → unpivot (docs/apis §5/§6, design
/// §4). Skips a leading spacer row, finds the <c>Date (EST)</c> header, reads the
/// forecast-date width DYNAMICALLY, and emits one cell per (hour × forecast-date);
/// non-hour footer rows (<c>sum/average change for the day</c>) are dropped.
/// Region + InitDate come from the unit.
/// </summary>
public class ParseShapeCTests
{
    private static readonly ShapeCParser Parser = new();

    private static CwgWorkUnit Unit(string endpointId, string region, DateOnly init, string filename) => new()
    {
        EndpointId = endpointId,
        Region = region,
        RepresentativeDate = init,
        Filename = filename,
        KeyValue = "k"
    };

    // ---------------------------------------------------------------- SolarForecast (leading blank; 16 cols; init-1..+14; 24 hours)

    [Fact]
    public void SolarForecast_RealCsv_LeadingBlankSkipped_16Dates_24Hours_InitMinus1ToPlus14()
    {
        var csv = SampleData.Read("ERCOTsolar_08112026.csv");
        var init = new DateOnly(2026, 8, 11);
        var unit = Unit("SolarForecast", "ERCOT", init, "ERCOTsolar_08112026.csv");

        var cells = Parser.Parse(CwgDescriptors.SolarForecast, unit, csv, NullLogger.Instance);

        // 16 forecast-date columns x 24 hour rows.
        var dates = cells.Select(c => c.ForecastDate).Distinct().OrderBy(d => d).ToList();
        Assert.Equal(16, dates.Count);
        Assert.Equal(init.AddDays(-1), dates.First()); // first column is the day BEFORE the file date
        Assert.Equal(init.AddDays(14), dates.Last());  // ...through init+14

        // 24 hour labels -> HourOfDay 0..23, exact CwgHours order.
        var hours = cells.Select(c => c.HourOfDay).Distinct().OrderBy(h => h).ToList();
        Assert.Equal(Enumerable.Range(0, 24), hours);
        Assert.All(cells, c => Assert.Equal(CwgHours.Labels[c.HourOfDay], c.HourLabel));

        Assert.Equal(16 * 24, cells.Count);

        // Row factory: noon row, first forecast-date column -> 30147 MW.
        var rows = cells.Select(c => SolarForecastRow.From(c, unit)).Where(r => r is not null).Cast<SolarForecastRow>().ToList();
        var noonFirstCol = Assert.Single(rows, r =>
            r.HourOfDay == 12 && r.ForecastDate == init.AddDays(-1));
        Assert.Equal("12:00 PM", noonFirstCol.HourLabel);
        Assert.Equal("ERCOT", noonFirstCol.Region);          // from the unit
        Assert.Equal(init, noonFirstCol.InitDate);           // from the unit
        Assert.Equal(30147m, noonFirstCol.ValueMw);
    }

    // ---------------------------------------------------------------- SolarForecastChange (no lead blank; 14 cols + trailing comma; MM/DD/YYYY; signed; footers skipped)

    [Fact]
    public void SolarForecastChange_RealCsv_NoLeadBlank_14Dates_TrailingCommaExcluded_FootersSkipped_Signed()
    {
        var csv = SampleData.Read("ERCOTsolarchanges_08112026.csv");
        var init = new DateOnly(2026, 8, 11);
        var unit = Unit("SolarForecastChange", "ERCOT", init, "ERCOTsolarchanges_08112026.csv");

        var cells = Parser.Parse(CwgDescriptors.SolarForecastChange, unit, csv, NullLogger.Instance);

        // 14 forecast dates (trailing empty column from the trailing comma is excluded).
        var dates = cells.Select(c => c.ForecastDate).Distinct().OrderBy(d => d).ToList();
        Assert.Equal(14, dates.Count);
        Assert.Equal(init, dates.First());              // change file starts at the init date itself (MM/DD/YYYY)
        Assert.Equal(init.AddDays(13), dates.Last());   // ...through init+13

        // Only the 24 hour rows survive; the two footer rows are dropped.
        Assert.Equal(24, cells.Select(c => c.HourOfDay).Distinct().Count());
        Assert.Equal(14 * 24, cells.Count);

        // Signed change: 8:00 AM, first forecast-date column -> -532.
        var rows = cells.Select(c => SolarForecastChangeRow.From(c, unit)).Where(r => r is not null).Cast<SolarForecastChangeRow>().ToList();
        var eightAmFirst = Assert.Single(rows, r => r.HourOfDay == 8 && r.ForecastDate == init);
        Assert.Equal(-532m, eightAmFirst.ChangeMw);     // negative preserved

        // Every surviving record is a real hour row (footers can't reach the factory).
        Assert.All(rows, r => Assert.InRange(r.HourOfDay, (byte)0, (byte)23));
    }

    [Fact]
    public void ShapeC_NoDateEstHeader_YieldsEmpty()
    {
        var csv = "just,some,garbage\n1,2,3\n";
        var init = new DateOnly(2026, 8, 11);
        var unit = Unit("SolarForecast", "ERCOT", init, "x.csv");

        var cells = Parser.Parse(CwgDescriptors.SolarForecast, unit, csv, NullLogger.Instance);

        Assert.Empty(cells);
    }
}
