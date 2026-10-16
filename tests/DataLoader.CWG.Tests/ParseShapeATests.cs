using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Shape A — simple tabular (docs/apis/CWG.md §1/§3/§8/§15/§19, design §4). One data
/// row = one record, parsed by ORDERED POSITION (so the digit-leading
/// <c>30Y_/10Y_</c> degree-day headers and the <c>Norm Max</c> quirk are handled).
/// Driven by the real captured CSVs; the shape parser is pure (no HTTP/DB).
/// </summary>
public class ParseShapeATests
{
    private static readonly ShapeAParser Parser = new();

    private static CwgWorkUnit Unit(string endpointId, string? region, DateOnly? date, string filename, string? units = "F") => new()
    {
        EndpointId = endpointId,
        Region = region,
        Units = units,
        RepresentativeDate = date,
        Filename = filename,
        KeyValue = "k"
    };

    // ---------------------------------------------------------------- CityForecast

    [Fact]
    public void CityForecast_RealCsv_MapsByPosition_NormMaxQuirk()
    {
        var csv = SampleData.Read("city15dfcst_northamerica_20260811_F.csv");
        var unit = Unit("CityForecast", "northamerica", new DateOnly(2026, 8, 11),
            "city15dfcst_northamerica_20260811_F.csv");

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityForecastRow.From(r, unit)).Where(r => r is not null).Cast<CityForecastRow>().ToList();

        Assert.NotEmpty(rows);
        var first = rows[0];
        Assert.Equal(new DateOnly(2026, 8, 11), first.ProductionDate); // M/d/yy: 26 -> 2026
        Assert.Equal(new DateOnly(2026, 8, 11), first.Date);           // the forecast/valid day
        Assert.Equal("KABR", first.Station);
        Assert.Equal(62m, first.ForecastMin);
        Assert.Equal(83m, first.ForecastMax);
        Assert.Equal(72.5m, first.ForecastAvg);                        // Fcst Avg carries a .5
        Assert.Equal(57.9m, first.NormMin);
        Assert.Equal(83.8m, first.NormMax);                            // col 7 = 'Norm Max' header quirk, by position
        Assert.Equal(0m, first.HDD);
        Assert.Equal(8m, first.CDD);
    }

    [Fact]
    public void CityForecast_PreservesFullDecimalScale_NotRounded()
    {
        // The DB columns are DECIMAL(9,4); the factory must not pre-round. Proven here with
        // the europe _C fixture, whose normals carry more scale than the °F file ever does.
        // (europe is no longer ENUMERATED — arm.CityForecast has no Region/Units column — but
        // the fixture is still the best available probe of decimal-scale preservation.)
        var csv = SampleData.Read("city15dfcst_europe_20260811_C.csv");
        var unit = Unit("CityForecast", "europe", new DateOnly(2026, 8, 11),
            "city15dfcst_europe_20260811_C.csv", units: "C");

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityForecastRow.From(r, unit)).Where(r => r is not null).Cast<CityForecastRow>().ToList();

        Assert.NotEmpty(rows);
        var first = rows[0];
        Assert.Equal("BIAR", first.Station);
        Assert.Equal(7.74478m, first.NormMin);
        Assert.Equal(15.4283m, first.NormMax);
        Assert.NotEqual(Math.Round(7.74478m, 1), first.NormMin); // 7.7m would be the clamped value
    }

    [Fact]
    public void CityForecast_NoLongerDependsOnUnitRegionOrUnits()
    {
        // arm.CityForecast has neither a Region nor a Units column, so the factory must not
        // gate on them any more — a unit with both null still produces rows. Multiple geographies
        // merge into the one table upstream (RegionMergedTarget), kept apart by
        // (ProductionDate, Date, Station); rows are never dropped here to achieve that.
        var csv = SampleData.Read("city15dfcst_northamerica_20260811_F.csv");
        var unit = Unit("CityForecast", region: null, new DateOnly(2026, 8, 11), "f.csv", units: null);

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityForecastRow.From(r, unit)).Where(r => r is not null).ToList();

        Assert.NotEmpty(records);
        Assert.Equal(records.Count, rows.Count);
    }

    [Fact]
    public void CityForecast_BlankMeasure_BecomesNull_RowKept()
    {
        // Every measure column is NULL-able now: a blank cell yields NULL instead of
        // discarding the nine good values beside it.
        var csv =
            "Production Date,Date,Station,Fcst Mn,Fcst Mx,Fcst Avg,Norm Mn,Norm Max,HDD,CDD\n" +
            "8/11/26,8/12/26,KABR,61,82,71.5,57.7,83.7,,7\n";
        var unit = Unit("CityForecast", "northamerica", new DateOnly(2026, 8, 11), "f.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance));
        var row = CityForecastRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Null(row!.HDD);          // blank -> NULL
        Assert.Equal(7m, row.CDD);      // the rest survives
        Assert.Equal(61m, row.ForecastMin);
    }

    [Fact]
    public void CityForecast_GarbageMeasure_DropsRow()
    {
        // The other half of the contract: a cell that is PRESENT but unparseable is a real
        // format change, so the row is dropped and the reader counts/warns about it.
        var csv =
            "Production Date,Date,Station,Fcst Mn,Fcst Mx,Fcst Avg,Norm Mn,Norm Max,HDD,CDD\n" +
            "8/11/26,8/12/26,KABR,61,82,71.5,57.7,83.7,n/a,7\n";
        var unit = Unit("CityForecast", "northamerica", new DateOnly(2026, 8, 11), "f.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance));
        Assert.Null(CityForecastRow.From(rec, unit));
    }

    [Fact]
    public void ShapeA_ExpectedColumnsGuard_TrailingHeaderComma_DoesNotDropDataRows()
    {
        // A stray trailing comma inflates header.Length to 11, but the descriptor's
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

    // ---------------------------------------------------------------- CityGasForecast

    [Fact]
    public void CityGasForecast_RealCsv_ProductionDateFromRow()
    {
        var csv = SampleData.Read("city_gasday_fcst.csv");
        var unit = Unit("CityGasForecast", region: null, date: null, "city_gasday_fcst.csv");

        var records = Parser.Parse(CwgDescriptors.CityGasForecast, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityGasForecastRow.From(r, unit)).Where(r => r is not null).Cast<CityGasForecastRow>().ToList();

        Assert.NotEmpty(rows);
        Assert.Equal(new DateOnly(2026, 8, 11), rows[0].ProductionDate);
        Assert.Equal("KABR", rows[0].Station);
    }

    // ---------------------------------------------------------------- CityObservation

    [Fact]
    public void CityObservation_RealCsv_KeyedByDateAndStationOnly()
    {
        var csv = SampleData.Read("northamerica_observations_final_20260810.csv");
        var unit = Unit("CityObservation", "northamerica", new DateOnly(2026, 8, 10),
            "northamerica_observations_final_20260810.csv");

        var records = Parser.Parse(CwgDescriptors.CityObservation, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => CityObservationRow.From(r, unit)).Where(r => r is not null).Cast<CityObservationRow>().ToList();

        Assert.NotEmpty(rows);
        var first = rows[0];
        Assert.Equal(new DateOnly(2026, 8, 10), first.Date);   // in-row YYYY-MM-DD
        Assert.Equal("KABR", first.Station);
        Assert.Equal(54m, first.MinTemp);
        Assert.Equal(84m, first.MaxTemp);
        Assert.Equal(0m, first.HDD);
        Assert.Equal(4m, first.CDD);
    }

    // ---------------------------------------------------------------- NationalDegreeDays

    [Fact]
    public void NationalDegreeDays_RealCsv_14ColsByPosition_IsForecastTrueFalseToBit()
    {
        var csv = SampleData.Read("northamerica_national_wdd_20260811.csv");
        var runDate = new DateOnly(2026, 8, 11);
        var unit = Unit("NationalDegreeDays", "northamerica", runDate, "northamerica_national_wdd_20260811.csv");

        var records = Parser.Parse(CwgDescriptors.NationalDegreeDays, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => NationalDegreeDaysRow.From(r, unit)).Where(r => r is not null).Cast<NationalDegreeDaysRow>().ToList();

        // 22 dated data rows; the trailing 'END.' terminator line is dropped by CwgCsv
        // (treated as EOF), never reaching the width guard or the row factory.
        Assert.Equal(22, rows.Count);

        var first = rows[0];
        Assert.Equal(runDate, first.ObservationDate);          // run date, from the filename/unit
        Assert.Equal(new DateOnly(2026, 8, 4), first.Date);    // DATES YYYY-MM-DD
        Assert.Equal(0.2262m, first.NgHdd);
        Assert.Equal(0.1381m, first.NgHdd30y);                 // col 2 '30Y_NG_HDD' by position
        Assert.Equal(0.0000m, first.NgHdd10y);                 // 0.0000 is a valid value, not null
        Assert.False(first.IsForecast);                        // "False" -> bit 0

        Assert.Contains(rows, r => r.IsForecast == true);
        Assert.Contains(rows, r => r.IsForecast == false);
        var fcst = Assert.Single(rows, r => r.Date == new DateOnly(2026, 8, 11));
        Assert.True(fcst.IsForecast);                          // "True" -> bit 1
    }

    // ---------------------------------------------------------------- StateDegreeDayObservation

    [Fact]
    public void StateDegreeDayObservation_RealCsv_48Rows_15ColsByPosition()
    {
        // The live file (verified 2026-10-05) is one observation DAY: DATES equals the
        // filename date on every row, which is why the table keys on ObservationDate alone.
        // The fixture carries a UTF-8 BOM, so this also exercises CwgCsv's BOM strip.
        var csv = SampleData.Read("northamerica_state_wdd_observations_20260811.csv");
        var runDate = new DateOnly(2026, 8, 11);
        var unit = Unit("StateDegreeDayObservation", "northamerica", runDate,
            "northamerica_state_wdd_observations_20260811.csv");

        var records = Parser.Parse(CwgDescriptors.StateDegreeDayObservation, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StateDegreeDayObservationRow.From(r, unit))
                          .Where(r => r is not null).Cast<StateDegreeDayObservationRow>().ToList();

        Assert.Equal(48, rows.Count);                                   // 48 states
        Assert.All(rows, r => Assert.Equal(runDate, r.ObservationDate)); // single day per file
        Assert.Equal(9, rows.Select(r => r.RegionName).Distinct().Count()); // 9 census divisions
        Assert.Equal(48, rows.Select(r => (r.RegionName, r.State)).Distinct().Count()); // key is unique

        var ct = Assert.Single(rows, r => r.State == "CONNECTICUT");
        Assert.Equal("NEW ENGLAND", ct.RegionName);
        Assert.Equal(0.0000m, ct.PopHdd);
        Assert.Equal(0.0257m, ct.PopHdd30y);
        Assert.Equal(0.1166m, ct.PopHdd10y);
        Assert.Equal(0.0000m, ct.PopHddLastY);
        Assert.Equal(10.0358m, ct.PopCdd);
        Assert.Equal(7.1107m, ct.PopCdd30y);
        Assert.Equal(8.0242m, ct.PopCdd10y);
        Assert.Equal(9.6741m, ct.PopCddLastY);
        Assert.False(ct.IsForecast);                 // observations file: always False
        Assert.Equal(0.2159m, ct.GasWeight);
        Assert.Equal(0.2519m, ct.ElctWeight);        // source spells it ELCT
        Assert.Equal(0.2385m, ct.PopWeight);
    }

    [Fact]
    public void StateDegreeDayObservation_BlankRegionOrState_Dropped()
    {
        // Both are key columns; a row missing either cannot be keyed.
        var header = "DATES,REGION_NAME,STATE_NAME,POP_HDD,30Y_POP_HDD,10Y_POP_HDD,LAST_Y_POP_HDD," +
                     "POP_CDD,30Y_POP_CDD,10Y_POP_CDD,LAST_Y_POP_CDD,IS_FORECAST,GAS_WEIGHT,ELCT_WEIGHT,POP_WEIGHT\n";
        var csv = header +
            "2026-08-11,,CONNECTICUT,0,0,0,0,0,0,0,0,False,0,0,0\n" +
            "2026-08-11,NEW ENGLAND,,0,0,0,0,0,0,0,0,False,0,0,0\n" +
            "2026-08-11,NEW ENGLAND,MAINE,0,0,0,0,0,0,0,0,False,0,0,0\n";
        var unit = Unit("StateDegreeDayObservation", "northamerica", new DateOnly(2026, 8, 11), "s.csv");

        var records = Parser.Parse(CwgDescriptors.StateDegreeDayObservation, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StateDegreeDayObservationRow.From(r, unit))
                          .Where(r => r is not null).Cast<StateDegreeDayObservationRow>().ToList();

        var row = Assert.Single(rows);
        Assert.Equal("MAINE", row.State);
    }

    // ---------------------------------------------------------------- Station

    [Fact]
    public void Station_RealCsv_KeyedByIdentifierOnly_KeepsWbanLiteral_GhcndBlankToNull()
    {
        var csv = SampleData.Read("northamerica_station_information.csv");
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StationRow.From(r, unit)).Where(r => r is not null).Cast<StationRow>().ToList();

        Assert.Equal(39, rows.Count); // 40 lines - 1 header
        var first = rows[0];
        Assert.Equal("CWKD", first.Identifier);
        Assert.Equal("71383", first.WmoId);
        Assert.Equal("99999", first.Wban);   // kept LITERAL — no 99999 -> NULL remap
        Assert.Null(first.Ghcnd);            // empty ghcnd -> NULL
        Assert.Equal(50.733, first.Latitude);
        Assert.Equal(-71.017, first.Longitude);   // signed
        Assert.Equal("Bonnard", first.Name);
        Assert.Equal("NB", first.State);
        Assert.Equal("CA", first.Country);

        // Identifier is the WHOLE primary key now — it must be unique within a file.
        Assert.Equal(rows.Count, rows.Select(r => r.Identifier).Distinct().Count());
    }

    [Fact]
    public void Station_EuropeCsv_OmitsGhcnd_AndIsStillMappedByHeaderNotPosition()
    {
        // REGRESSION: the europe file has 8 columns — it has no `ghcnd` — while northamerica has 9.
        // Reading by ordinal shifted every field from index 3 one place left for all 468 european
        // stations: LTCI landed GHCND='38.467' (its latitude), Latitude=43.35 (its longitude),
        // Longitude/Name NULL and State='TURKEY' (its country). Verified against dbo.Station.
        var csv = SampleData.Read("europe_station_information.csv");
        var unit = Unit("Station", "europe", date: null, "europe_station_information.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StationRow.From(r, unit)).Where(r => r is not null).Cast<StationRow>().ToList();

        Assert.Equal(5, rows.Count);

        var ltci = rows.Single(r => r.Identifier == "LTCI");
        Assert.Equal("17170", ltci.WmoId);
        Assert.Equal("99999", ltci.Wban);
        Assert.Null(ltci.Ghcnd);              // column absent for europe -> NULL, NOT the latitude
        Assert.Equal(38.467, ltci.Latitude);  // the real latitude, not shifted into Ghcnd
        Assert.Equal(43.350, ltci.Longitude); // the real longitude, not NULL
        Assert.Equal("VAN", ltci.Name);       // the real name, not NULL
        Assert.Null(ltci.State);              // europe carries no state
        Assert.Equal("TURKEY", ltci.Country); // the country, not shifted into State

        var bihn = rows.Single(r => r.Identifier == "BIHN");
        Assert.Equal(64.300, bihn.Latitude);
        Assert.Equal(-15.217, bihn.Longitude);
        Assert.Equal("AKURNES", bihn.Name);
        Assert.Equal("ICELAND", bihn.Country);

        // Nothing may leak the country into State on any row.
        Assert.All(rows, r => Assert.Null(r.State));
        Assert.All(rows, r => Assert.Null(r.Ghcnd));
    }

    [Fact]
    public void Station_ColumnOrderIsIrrelevant_BecauseMappingIsByHeaderName()
    {
        // Same data, columns deliberately shuffled — a positional reader would scramble this.
        var csv =
            "country,identifier,lon,lat,name,wmoid,wban\n" +
            "TURKEY,LTCI,43.350,38.467,VAN,17170,99999\n";
        var unit = Unit("Station", "europe", date: null, "shuffled.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var row = StationRow.From(Assert.Single(records), unit);

        Assert.NotNull(row);
        Assert.Equal("LTCI", row!.Identifier);
        Assert.Equal(38.467, row.Latitude);
        Assert.Equal(43.350, row.Longitude);
        Assert.Equal("VAN", row.Name);
        Assert.Equal("TURKEY", row.Country);
        Assert.Null(row.Ghcnd);
        Assert.Null(row.State);
    }

    [Fact]
    public void Station_UnusableHeader_FallsBackToTheDocumentedOrdinals()
    {
        // If the header does not even carry `identifier` we cannot map by name; the documented
        // northamerica order is the fallback, so such a file still loads instead of yielding NULLs.
        var csv =
            "a,b,c,d,e,f,g,h,i\n" +
            "CWKD,71383,99999,,50.733,-71.017,Bonnard,NB,CA\n";
        var unit = Unit("Station", "northamerica", date: null, "noheader.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var row = StationRow.From(Assert.Single(records), unit);

        Assert.NotNull(row);
        Assert.Equal("CWKD", row!.Identifier);
        Assert.Equal(50.733, row.Latitude);
        Assert.Equal(-71.017, row.Longitude);
        Assert.Equal("Bonnard", row.Name);
        Assert.Equal("CA", row.Country);
    }

    [Fact]
    public void Station_BlankState_MapsToNull()
    {
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "LFPG,07157,,,49.012,2.550,Paris CDG,,FR\n";
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Null(row!.State);   // blank state -> NULL
        Assert.Null(row.Ghcnd);
        Assert.Null(row.Wban);     // blank wban -> NULL (only the literal 99999 is kept)
        Assert.Equal("FR", row.Country);
    }

    [Fact]
    public void Station_ShortRow_MissingTrailingFields_NotDropped_MissingToNull()
    {
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KXYZ,71234,99999,,44.5,-93.2\n";   // 6 fields: name/state/country missing
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Equal("KXYZ", row!.Identifier);
        Assert.Equal("71234", row.WmoId);
        Assert.Equal(44.5, row.Latitude);
        Assert.Equal(-93.2, row.Longitude);
        Assert.Null(row.Name);      // missing -> NULL
        Assert.Null(row.State);
        Assert.Null(row.Country);
    }

    [Fact]
    public void Station_ShortRow_MissingLatLon_MapsToNull()
    {
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KABC,71000\n";   // 2 fields: everything after wmoid missing
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Equal("KABC", row!.Identifier);
        Assert.Null(row.Latitude);
        Assert.Null(row.Longitude);
        Assert.Null(row.Country);
    }

    [Fact]
    public void Station_GarbageLatLon_MapsToNull_RowKept()
    {
        // Station is the one deliberately lenient endpoint (AllowShortRows): a malformed
        // coordinate must not cost the whole station. The merge proc separately refuses to
        // build a geography Point from a NULL/out-of-range coordinate.
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KBAD,71005,99999,,n/a,--,Bad Coords,TX,US\n";
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var rec = Assert.Single(Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance));
        var row = StationRow.From(rec, unit);

        Assert.NotNull(row);
        Assert.Equal("KBAD", row!.Identifier);
        Assert.Null(row.Latitude);
        Assert.Null(row.Longitude);
        Assert.Equal("Bad Coords", row.Name);   // the rest of the row survives
    }

    [Fact]
    public void Station_EmptyIdentifier_Skipped()
    {
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            ",71999,99999,,10.0,20.0,No Id,ST,US\n";
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

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
        Assert.Equal(41.0, rows[1].Latitude);
    }

    // ---------------------------------------------------------------- "END." terminator (CwgCsv)

    [Fact]
    public void EndTerminator_NotEmittedAsRecord_ContentAfterIgnored()
    {
        var csv =
            "Production Date,Date,Station,Fcst Mn,Fcst Mx,Fcst Avg,Norm Mn,Norm Max,HDD,CDD\n" +
            "8/11/26,8/12/26,KABR,61,82,71.5,57.7,83.7,0,7\n" +
            "END.\n" +
            "8/11/26,8/13/26,KZZZ,60,80,70.0,57.0,83.0,0,6\n"; // after END. -> ignored
        var unit = Unit("CityForecast", "northamerica", new DateOnly(2026, 8, 11), "f.csv");

        var records = Parser.Parse(CwgDescriptors.CityForecast, unit, csv, NullLogger.Instance);

        var rec = Assert.Single(records);                 // only the row before END.
        var row = CityForecastRow.From(rec, unit);
        Assert.Equal("KABR", row!.Station);
    }

    [Fact]
    public void EndTerminator_Station_NotLoadedAsBogusRow()
    {
        // Regression: Station has AllowShortRows, so before END. handling the terminator
        // was kept and mapped to a bogus Identifier='END.' row. It must be dropped now.
        var csv =
            "identifier,wmoid,wban,ghcnd,lat,lon,name,state,country\n" +
            "KFUL,71001,99999,,40.0,-80.0,Full Station,PA,US\n" +
            "END.\n";
        var unit = Unit("Station", "northamerica", date: null, "northamerica_station_information.csv");

        var records = Parser.Parse(CwgDescriptors.Station, unit, csv, NullLogger.Instance);
        var rows = records.Select(r => StationRow.From(r, unit)).Where(r => r is not null).Cast<StationRow>().ToList();

        var row = Assert.Single(rows);                    // END. dropped, not a 2nd row
        Assert.Equal("KFUL", row.Identifier);
        Assert.DoesNotContain(rows, r => r.Identifier == "END.");
    }
}
