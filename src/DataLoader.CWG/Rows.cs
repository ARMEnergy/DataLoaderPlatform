namespace DataLoader.CWG;

// =============================================================================
// Target rows — the source reader maps each shape record to one of these via the
// per-endpoint row factory (the ONLY typed mapping code). Property order
// documents (and each sink's BuildTable mirrors) the TVP column contract in
// sql/CWG/002. A factory returns null to DROP a record (unkeyable row, garbage
// measure, or a deliberately-skipped block).
//
// 2026-10-05 reshape: these rows carry NO FileLogId. arm.FileLog is still written
// once per request (it is the run/status log), but a fact row no longer holds a
// provenance FK — the dbo tables these now mirror have no such column.
//
// Measure columns are decimal? / bool?: blank or the literal "NULL" sentinel maps
// to SQL NULL instead of discarding the row. A measure that is PRESENT but
// unparseable still drops the row, so a real format change stays visible — see
// the contract block in CwgParse.
// =============================================================================

// -------------------------------------------------------------------- 1. CityForecast (Shape A)
/// <summary>
/// arm.CityForecast — key (ProductionDate, Date, Station).
/// The table has no Region and no Units column: every enumerated geography merges into it,
/// kept apart by the station in the key (descriptor's RegionMergedTarget flag), mirroring
/// dbo.CityForecast. ⚠ Temperatures are °F for northamerica and °C for europe with nothing
/// on the row recording which — join arm.Station.Country to resolve the unit.
/// </summary>
public sealed class CityForecastRow
{
    public required DateOnly ProductionDate { get; init; }
    public required DateOnly Date { get; init; }          // the forecast/valid day
    public required string Station { get; init; }
    public decimal? ForecastMin { get; init; }
    public decimal? ForecastMax { get; init; }
    public decimal? ForecastAvg { get; init; }
    public decimal? NormMin { get; init; }
    public decimal? NormMax { get; init; }
    public decimal? HDD { get; init; }
    public decimal? CDD { get; init; }

    // Fields: 0 Production Date, 1 Date, 2 Station, 3 Fcst Mn, 4 Fcst Mx, 5 Fcst Avg,
    //         6 Norm Mn, 7 Norm Max, 8 HDD, 9 CDD. Both dates are M/d/yy (2-digit year).
    public static CityForecastRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var f = rec.Fields;
        if (f.Length < 10) return null;
        if (!CwgParse.DateMdyy(f[0], out var prod)) return null;
        if (!CwgParse.DateMdyy(f[1], out var date)) return null;
        var station = f[2].Trim();
        if (station.Length == 0) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var fmin)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var fmax)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var favg)) return null;
        if (!CwgParse.MeasureOrNull(f[6], out var nmin)) return null;
        if (!CwgParse.MeasureOrNull(f[7], out var nmax)) return null;
        if (!CwgParse.MeasureOrNull(f[8], out var hdd)) return null;
        if (!CwgParse.MeasureOrNull(f[9], out var cdd)) return null;
        return new CityForecastRow
        {
            ProductionDate = prod, Date = date, Station = station,
            ForecastMin = fmin, ForecastMax = fmax, ForecastAvg = favg,
            NormMin = nmin, NormMax = nmax, HDD = hdd, CDD = cdd
        };
    }
}

// -------------------------------------------------------------------- 2. CityGasForecast (Shape A)
/// <summary>arm.CityGasForecast — key (ProductionDate, Date, Station). Same layout as #1.</summary>
public sealed class CityGasForecastRow
{
    public required DateOnly ProductionDate { get; init; }
    public required DateOnly Date { get; init; }
    public required string Station { get; init; }
    public decimal? ForecastMin { get; init; }
    public decimal? ForecastMax { get; init; }
    public decimal? ForecastAvg { get; init; }
    public decimal? NormMin { get; init; }
    public decimal? NormMax { get; init; }
    public decimal? HDD { get; init; }
    public decimal? CDD { get; init; }

    public static CityGasForecastRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var f = rec.Fields;
        if (f.Length < 10) return null;
        if (!CwgParse.DateMdyy(f[0], out var prod)) return null;
        if (!CwgParse.DateMdyy(f[1], out var date)) return null;
        var station = f[2].Trim();
        if (station.Length == 0) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var fmin)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var fmax)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var favg)) return null;
        if (!CwgParse.MeasureOrNull(f[6], out var nmin)) return null;
        if (!CwgParse.MeasureOrNull(f[7], out var nmax)) return null;
        if (!CwgParse.MeasureOrNull(f[8], out var hdd)) return null;
        if (!CwgParse.MeasureOrNull(f[9], out var cdd)) return null;
        return new CityGasForecastRow
        {
            ProductionDate = prod, Date = date, Station = station,
            ForecastMin = fmin, ForecastMax = fmax, ForecastAvg = favg,
            NormMin = nmin, NormMax = nmax, HDD = hdd, CDD = cdd
        };
    }
}

// -------------------------------------------------------------------- 3. CityObservation (Shape A)
/// <summary>arm.CityObservation — key (Date, Station). No Region column (northamerica only).</summary>
public sealed class CityObservationRow
{
    public required DateOnly Date { get; init; }
    public required string Station { get; init; }
    public decimal? MinTemp { get; init; }
    public decimal? MaxTemp { get; init; }
    public decimal? HDD { get; init; }
    public decimal? CDD { get; init; }

    // Fields: 0 date (YYYY-MM-DD), 1 station, 2 MinTemp, 3 MaxTemp, 4 HDD, 5 CDD.
    public static CityObservationRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var f = rec.Fields;
        if (f.Length < 6) return null;
        if (!CwgParse.DateIso(f[0], out var date)) return null;
        var station = f[1].Trim();
        if (station.Length == 0) return null;
        if (!CwgParse.MeasureOrNull(f[2], out var min)) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var max)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var hdd)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var cdd)) return null;
        return new CityObservationRow
        {
            Date = date, Station = station, MinTemp = min, MaxTemp = max, HDD = hdd, CDD = cdd
        };
    }
}

// -------------------------------------------------------------------- 4. DailyNormal (Shape B, WIDE)
/// <summary>
/// arm.DailyNormal — key (Month, Day). WIDE: one column per region.
/// <see cref="RegionColumns"/> is the single source of truth for which regions are written,
/// in target-column order; the sink names its DataTable columns from it and a registry test
/// asserts it equals the descriptor's TargetRegionColumns. The source file also publishes
/// NW and SW — deliberately not written.
/// </summary>
public sealed class DailyNormalRow
{
    public static readonly string[] RegionColumns =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO" };

    public required byte Month { get; init; }
    public required byte Day { get; init; }

    /// <summary>Index-aligned 1:1 with <see cref="RegionColumns"/>.</summary>
    public required decimal?[] Values { get; init; }

    public static DailyNormalRow? From(CwgWideRow row, CwgWorkUnit unit)
    {
        if (!CwgParse.MonthDay(row.KeyCell, out var month, out var day)) return null;
        var values = CwgWide.Read(row, RegionColumns);
        if (values is null) return null;
        return new DailyNormalRow { Month = month, Day = day, Values = values };
    }
}

// -------------------------------------------------------------------- 5. SolarForecast (Shape C)
/// <summary>
/// arm.SolarForecast — key (ForecastDate, UTCHourEnding, Region).
/// ForecastDate is the INIT date (from the filename); UTCHourEnding is the valid hour
/// (ForecastLabel + HourLabel + 4h). ForecastLabel/HourLabel are kept un-shifted for the record.
/// </summary>
public sealed class SolarForecastRow
{
    public required DateOnly ForecastDate { get; init; }
    public required DateTime UTCHourEnding { get; init; }
    public required string Region { get; init; }
    public required DateOnly ForecastLabel { get; init; }
    public required string HourLabel { get; init; }
    public decimal? Value { get; init; }

    public static SolarForecastRow? From(CwgMatrixCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.MeasureOrNull(cell.RawValue, out var mw)) return null;
        return new SolarForecastRow
        {
            ForecastDate = init.Value,
            UTCHourEnding = CwgHours.ToUtcHourEnding(cell.ForecastDate, cell.HourOfDay, cell.UtcOffsetHours),
            Region = region,
            ForecastLabel = cell.ForecastDate,
            HourLabel = cell.HourLabel,
            Value = mw
        };
    }
}

// -------------------------------------------------------------------- 6. SolarForecastChange (Shape C)
/// <summary>arm.SolarForecastChange — key (ForecastDate, UTCHourEnding, Region). Value is SIGNED.</summary>
public sealed class SolarForecastChangeRow
{
    public required DateOnly ForecastDate { get; init; }
    public required DateTime UTCHourEnding { get; init; }
    public required string Region { get; init; }
    public required DateOnly ForecastLabel { get; init; }
    public required string HourLabel { get; init; }
    public decimal? Value { get; init; }

    public static SolarForecastChangeRow? From(CwgMatrixCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.MeasureOrNull(cell.RawValue, out var mw)) return null;
        return new SolarForecastChangeRow
        {
            ForecastDate = init.Value,
            UTCHourEnding = CwgHours.ToUtcHourEnding(cell.ForecastDate, cell.HourOfDay, cell.UtcOffsetHours),
            Region = region,
            ForecastLabel = cell.ForecastDate,
            HourLabel = cell.HourLabel,
            Value = mw
        };
    }
}

// -------------------------------------------------------------------- 7. SolarHourly (Shape B, WIDE)
/// <summary>
/// arm.SolarHourly — key (UTCHourEnding). WIDE. The source publishes 14 region columns;
/// ERCOT and CAISO are written. The literal "NULL" sentinel becomes a NULL column — early
/// history (2020) has only ERCOT populated, which is exactly that case.
/// </summary>
public sealed class SolarHourlyRow
{
    public static readonly string[] RegionColumns = { "ERCOT", "CAISO" };

    public required DateTime UTCHourEnding { get; init; }

    /// <summary>Index-aligned 1:1 with <see cref="RegionColumns"/>.</summary>
    public required decimal?[] Values { get; init; }

    public static SolarHourlyRow? From(CwgWideRow row, CwgWorkUnit unit)
    {
        if (!CwgParse.DateTimeIso(row.KeyCell, out var he)) return null;
        var values = CwgWide.Read(row, RegionColumns);
        if (values is null) return null;
        return new SolarHourlyRow { UTCHourEnding = he, Values = values };
    }
}

// -------------------------------------------------------------------- 8. StateDegreeDayObservation (Shape A)
/// <summary>
/// arm.StateDegreeDayObservation — key (ObservationDate, RegionName, State).
/// One file = one observation day (DATES equals the filename date), one row per
/// (census region × state). Verified live 2026-10-05: 15 columns, 48 data rows, no END. footer.
/// </summary>
public sealed class StateDegreeDayObservationRow
{
    public required DateOnly ObservationDate { get; init; }
    public required string RegionName { get; init; }
    public required string State { get; init; }
    public decimal? PopHdd { get; init; }
    public decimal? PopHdd30y { get; init; }
    public decimal? PopHdd10y { get; init; }
    public decimal? PopHddLastY { get; init; }
    public decimal? PopCdd { get; init; }
    public decimal? PopCdd30y { get; init; }
    public decimal? PopCdd10y { get; init; }
    public decimal? PopCddLastY { get; init; }
    public bool? IsForecast { get; init; }
    public decimal? GasWeight { get; init; }
    public decimal? ElctWeight { get; init; }
    public decimal? PopWeight { get; init; }

    // Fields: 0 DATES, 1 REGION_NAME, 2 STATE_NAME, 3 POP_HDD, 4 30Y_POP_HDD, 5 10Y_POP_HDD,
    //         6 LAST_Y_POP_HDD, 7 POP_CDD, 8 30Y_POP_CDD, 9 10Y_POP_CDD, 10 LAST_Y_POP_CDD,
    //         11 IS_FORECAST, 12 GAS_WEIGHT, 13 ELCT_WEIGHT, 14 POP_WEIGHT.
    public static StateDegreeDayObservationRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var f = rec.Fields;
        if (f.Length < 15) return null;
        if (!CwgParse.DateIso(f[0], out var obs)) return null;
        var region = f[1].Trim();
        if (region.Length == 0) return null;
        var state = f[2].Trim();
        if (state.Length == 0) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var popHdd)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var popHdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var popHdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[6], out var popHddL)) return null;
        if (!CwgParse.MeasureOrNull(f[7], out var popCdd)) return null;
        if (!CwgParse.MeasureOrNull(f[8], out var popCdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[9], out var popCdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[10], out var popCddL)) return null;
        if (!CwgParse.BitOrNull(f[11], out var isForecast)) return null;
        if (!CwgParse.MeasureOrNull(f[12], out var gasW)) return null;
        if (!CwgParse.MeasureOrNull(f[13], out var elctW)) return null;
        if (!CwgParse.MeasureOrNull(f[14], out var popW)) return null;
        return new StateDegreeDayObservationRow
        {
            ObservationDate = obs, RegionName = region, State = state,
            PopHdd = popHdd, PopHdd30y = popHdd30, PopHdd10y = popHdd10, PopHddLastY = popHddL,
            PopCdd = popCdd, PopCdd30y = popCdd30, PopCdd10y = popCdd10, PopCddLastY = popCddL,
            IsForecast = isForecast, GasWeight = gasW, ElctWeight = elctW, PopWeight = popW
        };
    }
}

// -------------------------------------------------------------------- 9. Station (Shape A)
/// <summary>
/// arm.Station — key (Identifier). No Region column (northamerica only). Wban is kept
/// literal (incl. the '99999' sentinel). The geography Point is built in the merge proc
/// from Latitude/Longitude — never in C#.
/// </summary>
public sealed class StationRow
{
    public required string Identifier { get; init; }
    public string? WmoId { get; init; }
    public string? Wban { get; init; }
    public string? Ghcnd { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }

    // Documented northamerica order, used ONLY as the fallback for a file with no usable header:
    // 0 identifier, 1 wmoid, 2 wban, 3 ghcnd, 4 lat, 5 lon, 6 name, 7 state, 8 country.
    //
    // Fields are normally resolved BY HEADER NAME, because the two geographies publish different
    // widths: europe_station_information.csv omits `ghcnd` (8 columns vs northamerica's 9). Reading
    // by ordinal shifted every field from index 3 on one place left for every european station —
    // e.g. LTCI stored GHCND='38.467' (its latitude), Latitude=43.35 (its longitude),
    // Longitude/Name NULL and State='TURKEY' (its country). AllowShortRows made the vanished 9th
    // column a silent NULL instead of an error, so nothing failed. Resolving by name makes an
    // absent column correctly NULL and leaves every other field on its own value.
    public static StationRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var f = rec.Fields;
        string AtOrdinal(int i) => i < f.Length ? f[i] : string.Empty;

        // A file whose header does not even carry `identifier` is not one we can map by name;
        // fall back to the documented ordinals rather than returning a row of NULLs.
        var byName = rec.HasHeaderColumn("identifier");

        string At(string headerName, int fallbackOrdinal)
        {
            if (!byName) return AtOrdinal(fallbackOrdinal);
            var i = rec.IndexOf(headerName);
            return i >= 0 ? AtOrdinal(i) : string.Empty;   // column absent for this geography → NULL
        }

        // Short rows are tolerated (descriptor AllowShortRows): any missing/blank field → NULL.
        // Only the identifier (the whole PK now) is required; without it the row cannot be keyed.
        var id = At("identifier", 0).Trim();
        if (id.Length == 0) return null;
        return new StationRow
        {
            Identifier = id,
            WmoId = CwgParse.NullIfBlank(At("wmoid", 1)),
            Wban = CwgParse.NullIfBlank(At("wban", 2)),   // keep 99999 literal (no sentinel remap)
            Ghcnd = CwgParse.NullIfBlank(At("ghcnd", 3)),
            Latitude = CwgParse.LenientDouble(At("lat", 4)),
            Longitude = CwgParse.LenientDouble(At("lon", 5)),
            Name = CwgParse.NullIfBlank(At("name", 6)),
            State = CwgParse.NullIfBlank(At("state", 7)),
            Country = CwgParse.NullIfBlank(At("country", 8))
        };
    }
}

// -------------------------------------------------------------------- 10. Regions5DegreeDays (Shape A)
/// <summary>
/// arm.Regions5DegreeDays — key (ObservationDate, Date, Region). ObservationDate is the RUN
/// date from the filename; Region is the in-file REGION_NAME (free text, NOT a filename axis).
/// </summary>
public sealed class Regions5DegreeDaysRow
{
    public required DateOnly ObservationDate { get; init; }
    public required DateOnly Date { get; init; }
    public required string Region { get; init; }
    public decimal? NgHdd { get; init; }
    public decimal? NgHdd30y { get; init; }
    public decimal? NgHdd10y { get; init; }
    public decimal? NgHddLastY { get; init; }
    public decimal? PopCdd { get; init; }
    public decimal? PopCdd30y { get; init; }
    public decimal? PopCdd10y { get; init; }
    public decimal? PopCddLastY { get; init; }
    public decimal? ElecCdd { get; init; }
    public decimal? ElecCdd30y { get; init; }
    public decimal? ElecCdd10y { get; init; }
    public decimal? ElecCddLastY { get; init; }
    public bool? IsForecast { get; init; }
    public decimal? GasWeight { get; init; }
    public decimal? ElctWeight { get; init; }
    public decimal? PopWeight { get; init; }

    // Fields: 0 DATES, 1 REGION_NAME, then the National layout shifted +1, then the 3 weights.
    public static Regions5DegreeDaysRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var p = CwgDegreeDays.ParseRegional(rec, unit);
        if (p is null) return null;
        var v = p.Value;
        return new Regions5DegreeDaysRow
        {
            ObservationDate = v.ObservationDate, Date = v.Date, Region = v.Region,
            NgHdd = v.Hdd, NgHdd30y = v.Hdd30y, NgHdd10y = v.Hdd10y, NgHddLastY = v.HddLastY,
            PopCdd = v.PopCdd, PopCdd30y = v.PopCdd30y, PopCdd10y = v.PopCdd10y, PopCddLastY = v.PopCddLastY,
            ElecCdd = v.ElecCdd, ElecCdd30y = v.ElecCdd30y, ElecCdd10y = v.ElecCdd10y, ElecCddLastY = v.ElecCddLastY,
            IsForecast = v.IsForecast, GasWeight = v.GasWeight, ElctWeight = v.ElctWeight, PopWeight = v.PopWeight
        };
    }
}

// -------------------------------------------------------------------- 11. Regions9DegreeDays (Shape A)
/// <summary>arm.Regions9DegreeDays — identical layout to #10; only the Region value set differs.</summary>
public sealed class Regions9DegreeDaysRow
{
    public required DateOnly ObservationDate { get; init; }
    public required DateOnly Date { get; init; }
    public required string Region { get; init; }
    public decimal? NgHdd { get; init; }
    public decimal? NgHdd30y { get; init; }
    public decimal? NgHdd10y { get; init; }
    public decimal? NgHddLastY { get; init; }
    public decimal? PopCdd { get; init; }
    public decimal? PopCdd30y { get; init; }
    public decimal? PopCdd10y { get; init; }
    public decimal? PopCddLastY { get; init; }
    public decimal? ElecCdd { get; init; }
    public decimal? ElecCdd30y { get; init; }
    public decimal? ElecCdd10y { get; init; }
    public decimal? ElecCddLastY { get; init; }
    public bool? IsForecast { get; init; }
    public decimal? GasWeight { get; init; }
    public decimal? ElctWeight { get; init; }
    public decimal? PopWeight { get; init; }

    public static Regions9DegreeDaysRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var p = CwgDegreeDays.ParseRegional(rec, unit);
        if (p is null) return null;
        var v = p.Value;
        return new Regions9DegreeDaysRow
        {
            ObservationDate = v.ObservationDate, Date = v.Date, Region = v.Region,
            NgHdd = v.Hdd, NgHdd30y = v.Hdd30y, NgHdd10y = v.Hdd10y, NgHddLastY = v.HddLastY,
            PopCdd = v.PopCdd, PopCdd30y = v.PopCdd30y, PopCdd10y = v.PopCdd10y, PopCddLastY = v.PopCddLastY,
            ElecCdd = v.ElecCdd, ElecCdd30y = v.ElecCdd30y, ElecCdd10y = v.ElecCdd10y, ElecCddLastY = v.ElecCddLastY,
            IsForecast = v.IsForecast, GasWeight = v.GasWeight, ElctWeight = v.ElctWeight, PopWeight = v.PopWeight
        };
    }
}

// -------------------------------------------------------------------- 12. ISODegreeDays (Shape A)
/// <summary>
/// arm.ISODegreeDays — key (ObservationDate, Date, Region). DIVERGENT 11-column layout:
/// the HDD family is POP_HDD (not NG_HDD), there is no ELEC family and no weights.
/// </summary>
public sealed class ISODegreeDaysRow
{
    public required DateOnly ObservationDate { get; init; }
    public required DateOnly Date { get; init; }
    public required string Region { get; init; }
    public decimal? PopHdd { get; init; }
    public decimal? PopHdd30y { get; init; }
    public decimal? PopHdd10y { get; init; }
    public decimal? PopHddLastY { get; init; }
    public decimal? PopCdd { get; init; }
    public decimal? PopCdd30y { get; init; }
    public decimal? PopCdd10y { get; init; }
    public decimal? PopCddLastY { get; init; }
    public bool? IsForecast { get; init; }

    // Fields: 0 DATES, 1 REGION_NAME, 2 POP_HDD, 3 30Y_POP_HDD, 4 10Y_POP_HDD, 5 LAST_Y_POP_HDD,
    //         6 POP_CDD, 7 30Y_POP_CDD, 8 10Y_POP_CDD, 9 LAST_Y_POP_CDD, 10 IS_FORECAST.
    public static ISODegreeDaysRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var run = unit.RepresentativeDate;
        if (run is null) return null;
        var f = rec.Fields;
        if (f.Length < 11) return null;
        if (!CwgParse.DateIso(f[0], out var date)) return null;
        var region = f[1].Trim();
        if (region.Length == 0) return null;
        if (!CwgParse.MeasureOrNull(f[2], out var popHdd)) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var popHdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var popHdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var popHddL)) return null;
        if (!CwgParse.MeasureOrNull(f[6], out var popCdd)) return null;
        if (!CwgParse.MeasureOrNull(f[7], out var popCdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[8], out var popCdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[9], out var popCddL)) return null;
        if (!CwgParse.BitOrNull(f[10], out var isForecast)) return null;
        return new ISODegreeDaysRow
        {
            ObservationDate = run.Value, Date = date, Region = region,
            PopHdd = popHdd, PopHdd30y = popHdd30, PopHdd10y = popHdd10, PopHddLastY = popHddL,
            PopCdd = popCdd, PopCdd30y = popCdd30, PopCdd10y = popCdd10, PopCddLastY = popCddL,
            IsForecast = isForecast
        };
    }
}

// -------------------------------------------------------------------- 13. NationalDegreeDays (Shape A)
/// <summary>arm.NationalDegreeDays — key (ObservationDate, Date). No region column.</summary>
public sealed class NationalDegreeDaysRow
{
    public required DateOnly ObservationDate { get; init; }
    public required DateOnly Date { get; init; }
    public decimal? NgHdd { get; init; }
    public decimal? NgHdd30y { get; init; }
    public decimal? NgHdd10y { get; init; }
    public decimal? NgHddLastY { get; init; }
    public decimal? PopCdd { get; init; }
    public decimal? PopCdd30y { get; init; }
    public decimal? PopCdd10y { get; init; }
    public decimal? PopCddLastY { get; init; }
    public decimal? ElecCdd { get; init; }
    public decimal? ElecCdd30y { get; init; }
    public decimal? ElecCdd10y { get; init; }
    public decimal? ElecCddLastY { get; init; }
    public bool? IsForecast { get; init; }

    // Fields: 0 DATES, 1 NG_HDD, 2 30Y_NG_HDD, 3 10Y_NG_HDD, 4 LAST_Y_NG_HDD, 5 POP_CDD,
    //         6 30Y_POP_CDD, 7 10Y_POP_CDD, 8 LAST_Y_POP_CDD, 9 ELEC_CDD, 10 30Y_ELEC_CDD,
    //         11 10Y_ELEC_CDD, 12 LAST_Y_ELEC_CDD, 13 IS_FORECAST.
    public static NationalDegreeDaysRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var run = unit.RepresentativeDate;
        if (run is null) return null;
        var f = rec.Fields;
        if (f.Length < 14) return null;
        if (!CwgParse.DateIso(f[0], out var date)) return null;
        if (!CwgParse.MeasureOrNull(f[1], out var ngHdd)) return null;
        if (!CwgParse.MeasureOrNull(f[2], out var ngHdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var ngHdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var ngHddL)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var popCdd)) return null;
        if (!CwgParse.MeasureOrNull(f[6], out var popCdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[7], out var popCdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[8], out var popCddL)) return null;
        if (!CwgParse.MeasureOrNull(f[9], out var elecCdd)) return null;
        if (!CwgParse.MeasureOrNull(f[10], out var elecCdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[11], out var elecCdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[12], out var elecCddL)) return null;
        if (!CwgParse.BitOrNull(f[13], out var isForecast)) return null;
        return new NationalDegreeDaysRow
        {
            ObservationDate = run.Value, Date = date,
            NgHdd = ngHdd, NgHdd30y = ngHdd30, NgHdd10y = ngHdd10, NgHddLastY = ngHddL,
            PopCdd = popCdd, PopCdd30y = popCdd30, PopCdd10y = popCdd10, PopCddLastY = popCddL,
            ElecCdd = elecCdd, ElecCdd30y = elecCdd30, ElecCdd10y = elecCdd10, ElecCddLastY = elecCddL,
            IsForecast = isForecast
        };
    }
}

// -------------------------------------------------------------------- 14. WindForecast (Shape C)
/// <summary>arm.WindForecast — key (ForecastDate, UTCHourEnding, Region). Same derivation as #5.</summary>
public sealed class WindForecastRow
{
    public required DateOnly ForecastDate { get; init; }
    public required DateTime UTCHourEnding { get; init; }
    public required string Region { get; init; }
    public required DateOnly ForecastLabel { get; init; }
    public required string HourLabel { get; init; }
    public decimal? Value { get; init; }

    public static WindForecastRow? From(CwgMatrixCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.MeasureOrNull(cell.RawValue, out var mw)) return null;
        return new WindForecastRow
        {
            ForecastDate = init.Value,
            UTCHourEnding = CwgHours.ToUtcHourEnding(cell.ForecastDate, cell.HourOfDay, cell.UtcOffsetHours),
            Region = region,
            ForecastLabel = cell.ForecastDate,
            HourLabel = cell.HourLabel,
            Value = mw
        };
    }
}

// -------------------------------------------------------------------- 15. WindForecastSubRegion (Shape D)
/// <summary>arm.WindForecastSubRegion — key (ForecastDate, UTCHourEnding, Region, SubRegion).</summary>
public sealed class WindForecastSubRegionRow
{
    public required DateOnly ForecastDate { get; init; }
    public required DateTime UTCHourEnding { get; init; }
    public required string Region { get; init; }
    public required string SubRegion { get; init; }
    public required DateOnly ForecastLabel { get; init; }
    public required string HourLabel { get; init; }
    public decimal? Value { get; init; }

    public static WindForecastSubRegionRow? From(CwgSubRegionCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.MeasureOrNull(cell.RawValue, out var mw)) return null;
        return new WindForecastSubRegionRow
        {
            ForecastDate = init.Value,
            UTCHourEnding = CwgHours.ToUtcHourEnding(cell.ForecastDate, cell.HourOfDay, cell.UtcOffsetHours),
            Region = region,
            SubRegion = cell.SubRegion,
            ForecastLabel = cell.ForecastDate,
            HourLabel = cell.HourLabel,
            Value = mw
        };
    }
}

// -------------------------------------------------------------------- 16. WindHourly (Shape B, WIDE)
/// <summary>
/// arm.WindHourly — key (UTCHourEnding). WIDE. The source publishes 12 region columns;
/// NW and SW are deliberately not written.
/// </summary>
public sealed class WindHourlyRow
{
    public static readonly string[] RegionColumns =
        { "CAISO", "SPP", "ERCOT", "MISO", "PJM", "NEPOOL", "NYISO", "BPA", "IESO", "AESO" };

    public required DateTime UTCHourEnding { get; init; }

    /// <summary>Index-aligned 1:1 with <see cref="RegionColumns"/>.</summary>
    public required decimal?[] Values { get; init; }

    public static WindHourlyRow? From(CwgWideRow row, CwgWorkUnit unit)
    {
        if (!CwgParse.DateTimeIso(row.KeyCell, out var he)) return null;
        var values = CwgWide.Read(row, RegionColumns);
        if (values is null) return null;
        return new WindHourlyRow { UTCHourEnding = he, Values = values };
    }
}

// -------------------------------------------------------------------- 17. WindTotalCapacityClimatology (Shape E)
/// <summary>
/// arm.WindTotalCapacityClimatology — key (ForecastDate, Region). ForecastDate is the
/// production date from the filename. Averages are % of capacity. Single-block file.
/// </summary>
public sealed class WindTotalCapacityClimatologyRow
{
    public required DateOnly ForecastDate { get; init; }
    public required string Region { get; init; }
    public decimal? TotalCapacity { get; init; }
    public decimal? OneToFiveDayAverage { get; init; }
    public decimal? SixToTenDayAverage { get; init; }
    public decimal? ElevenToFifteenDayAverage { get; init; }

    public static WindTotalCapacityClimatologyRow? From(CwgCapacityRow cap, CwgWorkUnit unit)
    {
        var prod = unit.RepresentativeDate;
        if (prod is null) return null;
        if (!CwgParse.MeasureOrNull(cap.TotalCapacityRaw, out var tot)) return null;
        if (!CwgParse.PercentOrNull(cap.Avg1_5Raw, out var a1)) return null;
        if (!CwgParse.PercentOrNull(cap.Avg6_10Raw, out var a2)) return null;
        if (!CwgParse.PercentOrNull(cap.Avg11_15Raw, out var a3)) return null;
        return new WindTotalCapacityClimatologyRow
        {
            ForecastDate = prod.Value, Region = cap.Region, TotalCapacity = tot,
            OneToFiveDayAverage = a1, SixToTenDayAverage = a2, ElevenToFifteenDayAverage = a3
        };
    }
}

// -------------------------------------------------------------------- 18. WindTotalCapacityMW (Shape E)
/// <summary>
/// arm.WindTotalCapacityMW — key (ForecastDate, Region). Averages in MW.
/// The file carries three blocks; this row type holds only <see cref="CwgCapacity.CurrentBlock"/>.
/// The other two are dropped here (and claimed by the module's sentinel predicate, so the drop
/// is logged at Debug rather than counted as a parse failure).
/// </summary>
public sealed class WindTotalCapacityMWRow
{
    public required DateOnly ForecastDate { get; init; }
    public required string Region { get; init; }
    public decimal? TotalCapacity { get; init; }
    public decimal? OneToFiveDayAverage { get; init; }
    public decimal? SixToTenDayAverage { get; init; }
    public decimal? ElevenToFifteenDayAverage { get; init; }

    public static WindTotalCapacityMWRow? From(CwgCapacityRow cap, CwgWorkUnit unit)
    {
        var prod = unit.RepresentativeDate;
        if (prod is null) return null;
        if (!CwgCapacity.IsCurrent(cap)) return null;   // Yesterday / Change have no slot in the key
        if (!CwgParse.MeasureOrNull(cap.TotalCapacityRaw, out var tot)) return null;
        if (!CwgParse.MeasureOrNull(cap.Avg1_5Raw, out var a1)) return null;
        if (!CwgParse.MeasureOrNull(cap.Avg6_10Raw, out var a2)) return null;
        if (!CwgParse.MeasureOrNull(cap.Avg11_15Raw, out var a3)) return null;
        return new WindTotalCapacityMWRow
        {
            ForecastDate = prod.Value, Region = cap.Region, TotalCapacity = tot,
            OneToFiveDayAverage = a1, SixToTenDayAverage = a2, ElevenToFifteenDayAverage = a3
        };
    }
}

// -------------------------------------------------------------------- 19. WindTotalCapacityPct (Shape E)
/// <summary>arm.WindTotalCapacityPct — key (ForecastDate, Region). Averages in %. Current block only.</summary>
public sealed class WindTotalCapacityPctRow
{
    public required DateOnly ForecastDate { get; init; }
    public required string Region { get; init; }
    public decimal? TotalCapacity { get; init; }
    public decimal? OneToFiveDayAverage { get; init; }
    public decimal? SixToTenDayAverage { get; init; }
    public decimal? ElevenToFifteenDayAverage { get; init; }

    public static WindTotalCapacityPctRow? From(CwgCapacityRow cap, CwgWorkUnit unit)
    {
        var prod = unit.RepresentativeDate;
        if (prod is null) return null;
        if (!CwgCapacity.IsCurrent(cap)) return null;   // Yesterday / Change have no slot in the key
        if (!CwgParse.MeasureOrNull(cap.TotalCapacityRaw, out var tot)) return null;
        if (!CwgParse.PercentOrNull(cap.Avg1_5Raw, out var a1)) return null;
        if (!CwgParse.PercentOrNull(cap.Avg6_10Raw, out var a2)) return null;
        if (!CwgParse.PercentOrNull(cap.Avg11_15Raw, out var a3)) return null;
        return new WindTotalCapacityPctRow
        {
            ForecastDate = prod.Value, Region = cap.Region, TotalCapacity = tot,
            OneToFiveDayAverage = a1, SixToTenDayAverage = a2, ElevenToFifteenDayAverage = a3
        };
    }
}

// =============================================================================
// Shared row-factory helpers.
// =============================================================================

/// <summary>Shape B: read a wide source row into the target's fixed region-column order.</summary>
internal static class CwgWide
{
    /// <summary>
    /// Returns one nullable measure per entry of <paramref name="regionColumns"/>, in that order.
    /// A region the file did not publish is ABSENT → NULL (legitimate: early SolarHourly history
    /// has only ERCOT). A published but unparseable cell returns null for the WHOLE row, which
    /// drops it — the reader then counts and warns, so a format change stays visible.
    /// </summary>
    public static decimal?[]? Read(CwgWideRow row, string[] regionColumns)
    {
        var values = new decimal?[regionColumns.Length];
        for (var i = 0; i < regionColumns.Length; i++)
        {
            row.ByRegion.TryGetValue(regionColumns[i], out var raw);
            if (!CwgParse.MeasureOrNull(raw, out var v)) return null;
            values[i] = v;
        }
        return values;
    }
}

/// <summary>Shape E: which capacity block a row belongs to.</summary>
internal static class CwgCapacity
{
    /// <summary>
    /// The block label the Shape E parser assigns to the first (current-forecast) block.
    /// arm.WindTotalCapacityMW / Pct key on (ForecastDate, Region) with NO block column, so
    /// only this block can be stored — the prior-forecast and change blocks would land on the
    /// same key and overwrite it.
    /// </summary>
    public const string CurrentBlock = "Current";

    public static bool IsCurrent(CwgCapacityRow cap) =>
        string.Equals(cap.Block, CurrentBlock, StringComparison.Ordinal);
}

/// <summary>
/// The 18-column regional weighted degree-day layout shared by the 5region and 9region
/// files (identical columns, different REGION_NAME value sets). One parser, two row types.
/// </summary>
internal static class CwgDegreeDays
{
    internal readonly record struct Regional(
        DateOnly ObservationDate, DateOnly Date, string Region,
        decimal? Hdd, decimal? Hdd30y, decimal? Hdd10y, decimal? HddLastY,
        decimal? PopCdd, decimal? PopCdd30y, decimal? PopCdd10y, decimal? PopCddLastY,
        decimal? ElecCdd, decimal? ElecCdd30y, decimal? ElecCdd10y, decimal? ElecCddLastY,
        bool? IsForecast, decimal? GasWeight, decimal? ElctWeight, decimal? PopWeight);

    // Fields: 0 DATES, 1 REGION_NAME, 2 NG_HDD, 3 30Y_NG_HDD, 4 10Y_NG_HDD, 5 LAST_Y_NG_HDD,
    //         6 POP_CDD, 7 30Y_POP_CDD, 8 10Y_POP_CDD, 9 LAST_Y_POP_CDD, 10 ELEC_CDD,
    //         11 30Y_ELEC_CDD, 12 10Y_ELEC_CDD, 13 LAST_Y_ELEC_CDD, 14 IS_FORECAST,
    //         15 GAS_WEIGHT, 16 ELCT_WEIGHT, 17 POP_WEIGHT.
    public static Regional? ParseRegional(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var run = unit.RepresentativeDate;
        if (run is null) return null;
        var f = rec.Fields;
        if (f.Length < 18) return null;
        if (!CwgParse.DateIso(f[0], out var date)) return null;
        var region = f[1].Trim();
        if (region.Length == 0) return null;
        if (!CwgParse.MeasureOrNull(f[2], out var hdd)) return null;
        if (!CwgParse.MeasureOrNull(f[3], out var hdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[4], out var hdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[5], out var hddL)) return null;
        if (!CwgParse.MeasureOrNull(f[6], out var popCdd)) return null;
        if (!CwgParse.MeasureOrNull(f[7], out var popCdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[8], out var popCdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[9], out var popCddL)) return null;
        if (!CwgParse.MeasureOrNull(f[10], out var elecCdd)) return null;
        if (!CwgParse.MeasureOrNull(f[11], out var elecCdd30)) return null;
        if (!CwgParse.MeasureOrNull(f[12], out var elecCdd10)) return null;
        if (!CwgParse.MeasureOrNull(f[13], out var elecCddL)) return null;
        if (!CwgParse.BitOrNull(f[14], out var isForecast)) return null;
        if (!CwgParse.MeasureOrNull(f[15], out var gasW)) return null;
        if (!CwgParse.MeasureOrNull(f[16], out var elctW)) return null;
        if (!CwgParse.MeasureOrNull(f[17], out var popW)) return null;

        return new Regional(run.Value, date, region,
            hdd, hdd30, hdd10, hddL,
            popCdd, popCdd30, popCdd10, popCddL,
            elecCdd, elecCdd30, elecCdd10, elecCddL,
            isForecast, gasW, elctW, popW);
    }
}
