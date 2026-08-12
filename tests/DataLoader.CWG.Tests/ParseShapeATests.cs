using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape A — simple tabular (docs/apis/CWG.md §1/§3/§8/§15, design §4). One data
/// row = one record, parsed by ORDERED POSITION (so the digit-leading
/// <c>30Y_/10Y_</c> National-DD headers and the <c>Norm Max</c> quirk are handled).
/// Driven by the real captured CSVs; the shape parser is pure (no HTTP/DB).
/// </summary>
public class ParseShapeATests
{
    private static readonly ShapeAParser Parser = new();

    private static CwgWorkUnit Unit(string endpointId, string? region, DateOnly? date, string filename) => new()
    {
        EndpointId = endpointId,
        Region = region,
        RepresentativeDate = date,
        Filename = filename,
        KeyValue = "k"
    };

    // ---------------------------------------------------------------- CityForecast

    [Fact]
    public void CityForecast_RealCsv_MapsByPosition_SmallIntHddCdd_NormMaxQuirk()
    {
        var csv = SampleData.Read("city15dfcst_northamerica_20260811_F.csv");
        var unit = Unit("CityForecast", "northamerica", new DateOnly(2026, 8, 11),
            "city15dfcst_northamerica_20260811_F.csv");

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityForecastRow.From(r, unit)).Where(r => r is not null).Cast<CityForecastRow>().ToList();

        Assert.NotEmpty(rows);
        var first = rows[0];
        Assert.Equal("northamerica", first.Region);          // region comes from the unit, not the CSV
        Assert.Equal(new DateOnly(2026, 8, 11), first.ProductionDate); // M/d/yy: 26 -> 2026
        Assert.Equal(new DateOnly(2026, 8, 11), first.ForecastDate);
        Assert.Equal("KABR", first.Station);
        Assert.Equal(62m, first.FcstMin);
        Assert.Equal(83m, first.FcstMax);
        Assert.Equal(72.5m, first.FcstAvg);                  // Fcst Avg carries a .5
        Assert.Equal(57.9m, first.NormMin);
        Assert.Equal(83.8m, first.NormMax);                  // col 7 = 'Norm Max' header quirk, mapped by position
        Assert.Equal((short)0, first.Hdd);                   // SMALLINT
        Assert.Equal((short)8, first.Cdd);
        Assert.IsType<short>(first.Hdd);
    }

    [Fact]
    public void CityForecast_MissingRegionOnUnit_DropsAllRows()
    {
        var csv = SampleData.Read("city15dfcst_northamerica_20260811_F.csv");
        var unit = Unit("CityForecast", region: null, new DateOnly(2026, 8, 11), "f.csv");

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityForecastRow.From(r, unit)).Where(r => r is not null).ToList();

        Assert.NotEmpty(records); // the parser still emits the raw records...
        Assert.Empty(rows);       // ...but the factory drops them (Region is a key column)
    }

    [Fact]
    public void ShapeA_ExpectedColumnsGuard_TrailingHeaderComma_DoesNotDropDataRows()
    {
        // Fix 3: a stray trailing comma inflates header.Length to 11, but the descriptor's
        // ExpectedColumns=10 is the width guard, so the 10-field data row still survives.
        var csv =
            "Production Date,Date,Station,Fcst Mn,Fcst Mx,Fcst Avg,Norm Mn,Norm Max,HDD,CDD,\n" +
            "8/11/26,8/12/26,KABR,61,82,71.5,57.7,83.7,0,7\n";
        var unit = Unit("CityForecast", "northamerica", new DateOnly(2026, 8, 11), "f.csv");

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);

        var rec = Assert.Single(records);
        var row = CityForecastRow.From(rec, unit);
        Assert.NotNull(row);
        Assert.Equal("KABR", row!.Station);
    }

    // ---------------------------------------------------------------- CityGasForecast (undated, in-row production date)

    [Fact]
    public void CityGasForecast_RealCsv_NoRegion_ProductionDateFromRow()
    {
        var csv = SampleData.Read("city_gasday_fcst.csv");
        var unit = Unit("CityGasForecast", region: null, date: null, "city_gasday_fcst.csv");

        var records = Parser.Parse(CwgDescriptors.CityGasForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityGasForecastRow.From(r, unit)).Where(r => r is not null).Cast<CityGasForecastRow>().ToList();

        Assert.NotEmpty(rows);
        Assert.Equal(new DateOnly(2026, 8, 11), rows[0].ProductionDate);
        Assert.Equal("KABR", rows[0].Station);
    }

    // ---------------------------------------------------------------- NationalDegreeDays (14 cols by position; digit-leading headers; BIT)

    [Fact]
    public void NationalDegreeDays_RealCsv_14ColsByPosition_IsForecastTrueFalseToBit()
    {
        var csv = SampleData.Read("northamerica_national_wdd_20260811.csv");
        var runDate = new DateOnly(2026, 8, 11);
        var unit = Unit("NationalDegreeDays", "northamerica", runDate, "northamerica_national_wdd_20260811.csv");

        var records = Parser.Parse(CwgDescriptors.NationalDegreeDays, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => NationalDegreeDaysRow.From(r, unit)).Where(r => r is not null).Cast<NationalDegreeDaysRow>().ToList();

        // 22 dated data rows; the trailing 'END.' marker line is gracefully dropped by the
        // ExpectedColumns=14 width guard (1 field < 14), never reaching the row factory.
        Assert.Equal(22, rows.Count);

        var first = rows[0];
        Assert.Equal(runDate, first.RunDate);                 // RunDate from the filename/unit
        Assert.Equal(new DateOnly(2026, 8, 4), first.Dates);  // DATES YYYY-MM-DD
        Assert.Equal(0.2262m, first.NgHdd);
        Assert.Equal(0.1381m, first.NgHdd30y);                // col 2 '30Y_NG_HDD' by position
        Assert.Equal(0.0000m, first.NgHdd10y);                // col 3 '10Y_NG_HDD' — 0.0000 is a valid value, not null
        Assert.False(first.IsForecast);                       // "False" -> bit 0

        // Both True and False flags are present and correctly mapped.
        Assert.Contains(rows, r => r.IsForecast);
        Assert.Contains(rows, r => !r.IsForecast);
        var fcst = Assert.Single(rows, r => r.Dates == new DateOnly(2026, 8, 11));
        Assert.True(fcst.IsForecast); // "True" -> bit 1
    }

    // ---------------------------------------------------------------- Station (9 cols; nullable wban(literal)/ghcnd/state)

    [Fact]
    public void Station_RealCsv_KeepsWbanLiteral_GhcndBlankToNull()
    {
        var csv = SampleData.Read("northamerica_station_information.csv");
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StationRow.From(r, unit)).Where(r => r is not null).Cast<StationRow>().ToList();

        Assert.Equal(39, rows.Count); // 40 lines - 1 header
        var first = rows[0];
        Assert.Equal("northamerica", first.Region);
        Assert.Equal("CWKD", first.Identifier);
        Assert.Equal("71383", first.WmoId);
        Assert.Equal("99999", first.Wban);   // kept LITERAL — no 99999 -> NULL remap
        Assert.Null(first.Ghcnd);            // empty ghcnd -> NULL
        Assert.Equal(50.733m, first.Lat);
        Assert.Equal(-71.017m, first.Lon);   // signed
        Assert.Equal("Bonnard", first.Name);
        Assert.Equal("NB", first.State);
        Assert.Equal("CA", first.Country);
    }

    [Fact]
    public void Station_BlankState_MapsToNull()
    {
        // NA sample has no blank state; prove the empty->NULL path with a synthetic row.
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "LFPG,07157,,,49.012,2.550,Paris CDG,,FR\n";
        var unit = Unit("Station", "europe", date: null, "europe_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Null(row!.State);   // blank state -> NULL
        Assert.Null(row.Ghcnd);
        Assert.Null(row.Wban);     // blank wban -> NULL (only the literal 99999 is kept)
        Assert.Equal("FR", row.Country);
    }

    // ---------------------------------------------------------------- Station short-row tolerance (AllowShortRows)

    [Fact]
    public void Station_ShortRow_MissingTrailingFields_NotDropped_MissingToNull()
    {
        // Station tolerates < 9 columns: missing trailing fields -> NULL, row still processed.
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KXYZ,71234,99999,,44.5,-93.2\n";   // 6 fields: name/state/country missing
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Equal("KXYZ", row!.Identifier);
        Assert.Equal("71234", row.WmoId);
        Assert.Equal(44.5m, row.Lat);
        Assert.Equal(-93.2m, row.Lon);
        Assert.Null(row.Name);      // missing -> NULL
        Assert.Null(row.State);     // missing -> NULL
        Assert.Null(row.Country);   // missing -> NULL
    }

    [Fact]
    public void Station_ShortRow_MissingLatLon_MapsToNull()
    {
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KABC,71000\n";   // 2 fields: everything after wmoid missing
        var unit = Unit("Station", "asia", date: null, "asia_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Equal("KABC", row!.Identifier);
        Assert.Equal("71000", row.WmoId);
        Assert.Null(row.Lat);       // missing -> NULL
        Assert.Null(row.Lon);       // missing -> NULL
        Assert.Null(row.Country);
    }

    [Fact]
    public void Station_EmptyIdentifier_Skipped()
    {
        // Identifier is half the PK; a row without it cannot be keyed -> dropped even when short-rows are allowed.
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            ",71999,99999,,10.0,20.0,No Id,ST,US\n";
        var unit = Unit("Station", "europe", date: null, "europe_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        Assert.Null(StationRow.From(rec, unit));   // empty identifier -> skipped
    }

    [Fact]
    public void Station_MixedFullAndShortRows_BothProcessed()
    {
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KFUL,71001,99999,,40.0,-80.0,Full Station,PA,US\n" +
            "KSHT,71002,99999,,41.0,-81.0\n";   // short: no name/state/country
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StationRow.From(r, unit)).Where(r => r is not null).Cast<StationRow>().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Full Station", rows[0].Name);
        Assert.Equal("US", rows[0].Country);
        Assert.Null(rows[1].Name);       // short row -> NULL
        Assert.Null(rows[1].Country);
        Assert.Equal(41.0m, rows[1].Lat);
    }
}
