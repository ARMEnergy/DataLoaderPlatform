namespace DataLoader.CWG;

// =============================================================================
// Target rows — the source reader maps each shape record to one of these via the
// per-endpoint row factory (the ONLY typed mapping code). Each row carries only
// its FileLogId (stamped after the FileLog upsert) + its own natural/data
// columns; property order documents (and each sink's BuildTable mirrors) the TVP
// column contract in sql/CWG/002 — FileLogId first. A factory returns null to
// DROP a record (sentinel/footer/unparseable required cell).
// =============================================================================

/// <summary>A fact row whose parent <c>arm.FileLog</c> id is stamped by the source reader.</summary>
public interface ICwgFactRow
{
    int FileLogId { get; set; }
}

// -------------------------------------------------------------------- 1. CityForecast (Shape A)
/// <summary>arm.CityForecast — key (Region, Station, ProductionDate, ForecastDate).</summary>
public sealed class CityForecastRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required DateOnly ProductionDate { get; init; }
    public required DateOnly ForecastDate { get; init; }
    public required string Station { get; init; }
    public required decimal FcstMin { get; init; }
    public required decimal FcstMax { get; init; }
    public required decimal FcstAvg { get; init; }
    public required decimal NormMin { get; init; }
    public required decimal NormMax { get; init; }
    public required short Hdd { get; init; }
    public required short Cdd { get; init; }

    // Fields: 0 Production Date, 1 Date, 2 Station, 3 Fcst Mn, 4 Fcst Mx, 5 Fcst Avg, 6 Norm Mn, 7 Norm Max, 8 HDD, 9 CDD.
    public static CityForecastRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var region = unit.Region;
        if (region is null) return null;
        var f = rec.Fields;
        if (f.Length < 10) return null;
        if (!CwgParse.DateMdyy(f[0], out var prod)) return null;
        if (!CwgParse.DateMdyy(f[1], out var fdate)) return null;
        var station = f[2].Trim();
        if (station.Length == 0) return null;
        if (!CwgParse.Decimal(f[3], out var fmin)) return null;
        if (!CwgParse.Decimal(f[4], out var fmax)) return null;
        if (!CwgParse.Decimal(f[5], out var favg)) return null;
        if (!CwgParse.Decimal(f[6], out var nmin)) return null;
        if (!CwgParse.Decimal(f[7], out var nmax)) return null;
        if (!CwgParse.Short(f[8], out var hdd)) return null;
        if (!CwgParse.Short(f[9], out var cdd)) return null;
        return new CityForecastRow
        {
            Region = region, ProductionDate = prod, ForecastDate = fdate, Station = station,
            FcstMin = fmin, FcstMax = fmax, FcstAvg = favg, NormMin = nmin, NormMax = nmax, Hdd = hdd, Cdd = cdd
        };
    }
}

// -------------------------------------------------------------------- 2. CityGasForecast (Shape A)
/// <summary>arm.CityGasForecast — key (Station, ProductionDate, ForecastDate). No Region.</summary>
public sealed class CityGasForecastRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ProductionDate { get; init; }
    public required DateOnly ForecastDate { get; init; }
    public required string Station { get; init; }
    public required decimal FcstMin { get; init; }
    public required decimal FcstMax { get; init; }
    public required decimal FcstAvg { get; init; }
    public required decimal NormMin { get; init; }
    public required decimal NormMax { get; init; }
    public required short Hdd { get; init; }
    public required short Cdd { get; init; }

    public static CityGasForecastRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var f = rec.Fields;
        if (f.Length < 10) return null;
        if (!CwgParse.DateMdyy(f[0], out var prod)) return null;
        if (!CwgParse.DateMdyy(f[1], out var fdate)) return null;
        var station = f[2].Trim();
        if (station.Length == 0) return null;
        if (!CwgParse.Decimal(f[3], out var fmin)) return null;
        if (!CwgParse.Decimal(f[4], out var fmax)) return null;
        if (!CwgParse.Decimal(f[5], out var favg)) return null;
        if (!CwgParse.Decimal(f[6], out var nmin)) return null;
        if (!CwgParse.Decimal(f[7], out var nmax)) return null;
        if (!CwgParse.Short(f[8], out var hdd)) return null;
        if (!CwgParse.Short(f[9], out var cdd)) return null;
        return new CityGasForecastRow
        {
            ProductionDate = prod, ForecastDate = fdate, Station = station,
            FcstMin = fmin, FcstMax = fmax, FcstAvg = favg, NormMin = nmin, NormMax = nmax, Hdd = hdd, Cdd = cdd
        };
    }
}

// -------------------------------------------------------------------- 3. CityObservation (Shape A)
/// <summary>arm.CityObservation — key (Region, Station, ObsDate).</summary>
public sealed class CityObservationRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required DateOnly ObsDate { get; init; }
    public required string Station { get; init; }
    public required decimal MinTemp { get; init; }
    public required decimal MaxTemp { get; init; }
    public required short Hdd { get; init; }
    public required short Cdd { get; init; }

    // Fields: 0 date (YYYY-MM-DD), 1 station, 2 MinTemp, 3 MaxTemp, 4 HDD, 5 CDD.
    public static CityObservationRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var region = unit.Region;
        if (region is null) return null;
        var f = rec.Fields;
        if (f.Length < 6) return null;
        if (!CwgParse.DateIso(f[0], out var obs)) return null;
        var station = f[1].Trim();
        if (station.Length == 0) return null;
        if (!CwgParse.Decimal(f[2], out var min)) return null;
        if (!CwgParse.Decimal(f[3], out var max)) return null;
        if (!CwgParse.Short(f[4], out var hdd)) return null;
        if (!CwgParse.Short(f[5], out var cdd)) return null;
        return new CityObservationRow
        {
            Region = region, ObsDate = obs, Station = station, MinTemp = min, MaxTemp = max, Hdd = hdd, Cdd = cdd
        };
    }
}

// -------------------------------------------------------------------- 4. DailyNormal (Shape B)
/// <summary>arm.DailyNormal — key (MonthDay, Region). MonthDay kept literal 'MM-DD'.</summary>
public sealed class DailyNormalRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string MonthDay { get; init; }
    public required string Region { get; init; }
    public required decimal NormalMw { get; init; }

    public static DailyNormalRow? From(CwgUnpivotCell cell, CwgWorkUnit unit)
    {
        var md = cell.KeyCell.Trim();
        if (md.Length == 0) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null; // DailyNormal has no sentinel
        return new DailyNormalRow { MonthDay = md, Region = cell.Region, NormalMw = mw };
    }
}

// -------------------------------------------------------------------- 5. SolarForecast (Shape C)
/// <summary>arm.SolarForecast — key (Region, InitDate, ForecastDate, HourOfDay).</summary>
public sealed class SolarForecastRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required DateOnly InitDate { get; init; }
    public required DateOnly ForecastDate { get; init; }
    public required string HourLabel { get; init; }
    public required byte HourOfDay { get; init; }
    public required decimal ValueMw { get; init; }

    public static SolarForecastRow? From(CwgMatrixCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null;
        return new SolarForecastRow
        {
            Region = region, InitDate = init.Value, ForecastDate = cell.ForecastDate,
            HourLabel = cell.HourLabel, HourOfDay = (byte)cell.HourOfDay, ValueMw = mw
        };
    }
}

// -------------------------------------------------------------------- 6. SolarForecastChange (Shape C)
/// <summary>arm.SolarForecastChange — key (Region, InitDate, ForecastDate, HourOfDay). Signed ChangeMw.</summary>
public sealed class SolarForecastChangeRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required DateOnly InitDate { get; init; }
    public required DateOnly ForecastDate { get; init; }
    public required string HourLabel { get; init; }
    public required byte HourOfDay { get; init; }
    public required decimal ChangeMw { get; init; }

    public static SolarForecastChangeRow? From(CwgMatrixCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null;
        return new SolarForecastChangeRow
        {
            Region = region, InitDate = init.Value, ForecastDate = cell.ForecastDate,
            HourLabel = cell.HourLabel, HourOfDay = (byte)cell.HourOfDay, ChangeMw = mw
        };
    }
}

// -------------------------------------------------------------------- 7. SolarHourly (Shape B)
/// <summary>arm.SolarHourly — key (HourEndingUtc, Region). "NULL"/blank cells dropped.</summary>
public sealed class SolarHourlyRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateTime HourEndingUtc { get; init; }
    public required string Region { get; init; }
    public required decimal ActualMw { get; init; }

    public static SolarHourlyRow? From(CwgUnpivotCell cell, CwgWorkUnit unit)
    {
        if (!CwgParse.DateTimeIso(cell.KeyCell, out var he)) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null; // "NULL"/blank → skip the cell (decision 4)
        return new SolarHourlyRow { HourEndingUtc = he, Region = cell.Region, ActualMw = mw };
    }
}

// -------------------------------------------------------------------- 8. NationalDegreeDays (Shape A)
/// <summary>arm.NationalDegreeDays — key (RunDate, Dates). Digit-leading source headers mapped by position.</summary>
public sealed class NationalDegreeDaysRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly RunDate { get; init; }
    public required DateOnly Dates { get; init; }
    public required decimal NgHdd { get; init; }
    public required decimal NgHdd30y { get; init; }
    public required decimal NgHdd10y { get; init; }
    public required decimal NgHddLastY { get; init; }
    public required decimal PopCdd { get; init; }
    public required decimal PopCdd30y { get; init; }
    public required decimal PopCdd10y { get; init; }
    public required decimal PopCddLastY { get; init; }
    public required decimal ElecCdd { get; init; }
    public required decimal ElecCdd30y { get; init; }
    public required decimal ElecCdd10y { get; init; }
    public required decimal ElecCddLastY { get; init; }
    public required bool IsForecast { get; init; }

    // Fields: 0 DATES, 1 NG_HDD, 2 30Y_NG_HDD, 3 10Y_NG_HDD, 4 LAST_Y_NG_HDD, 5 POP_CDD, 6 30Y_POP_CDD,
    // 7 10Y_POP_CDD, 8 LAST_Y_POP_CDD, 9 ELEC_CDD, 10 30Y_ELEC_CDD, 11 10Y_ELEC_CDD, 12 LAST_Y_ELEC_CDD, 13 IS_FORECAST.
    public static NationalDegreeDaysRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var run = unit.RepresentativeDate;
        if (run is null) return null;
        var f = rec.Fields;
        if (f.Length < 14) return null;
        if (!CwgParse.DateIso(f[0], out var dates)) return null;
        if (!CwgParse.Decimal(f[1], out var ngHdd)) return null;
        if (!CwgParse.Decimal(f[2], out var ngHdd30)) return null;
        if (!CwgParse.Decimal(f[3], out var ngHdd10)) return null;
        if (!CwgParse.Decimal(f[4], out var ngHddL)) return null;
        if (!CwgParse.Decimal(f[5], out var popCdd)) return null;
        if (!CwgParse.Decimal(f[6], out var popCdd30)) return null;
        if (!CwgParse.Decimal(f[7], out var popCdd10)) return null;
        if (!CwgParse.Decimal(f[8], out var popCddL)) return null;
        if (!CwgParse.Decimal(f[9], out var elecCdd)) return null;
        if (!CwgParse.Decimal(f[10], out var elecCdd30)) return null;
        if (!CwgParse.Decimal(f[11], out var elecCdd10)) return null;
        if (!CwgParse.Decimal(f[12], out var elecCddL)) return null;
        if (!CwgParse.Bit(f[13], out var isForecast)) return null;
        return new NationalDegreeDaysRow
        {
            RunDate = run.Value, Dates = dates,
            NgHdd = ngHdd, NgHdd30y = ngHdd30, NgHdd10y = ngHdd10, NgHddLastY = ngHddL,
            PopCdd = popCdd, PopCdd30y = popCdd30, PopCdd10y = popCdd10, PopCddLastY = popCddL,
            ElecCdd = elecCdd, ElecCdd30y = elecCdd30, ElecCdd10y = elecCdd10, ElecCddLastY = elecCddL,
            IsForecast = isForecast
        };
    }
}

// -------------------------------------------------------------------- 9. WindForecast (Shape C)
/// <summary>arm.WindForecast — key (Region, InitDate, ForecastDate, HourOfDay).</summary>
public sealed class WindForecastRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required DateOnly InitDate { get; init; }
    public required DateOnly ForecastDate { get; init; }
    public required string HourLabel { get; init; }
    public required byte HourOfDay { get; init; }
    public required decimal ValueMw { get; init; }

    public static WindForecastRow? From(CwgMatrixCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null;
        return new WindForecastRow
        {
            Region = region, InitDate = init.Value, ForecastDate = cell.ForecastDate,
            HourLabel = cell.HourLabel, HourOfDay = (byte)cell.HourOfDay, ValueMw = mw
        };
    }
}

// -------------------------------------------------------------------- 10. WindForecastSubRegion (Shape D)
/// <summary>arm.WindForecastSubRegion — key (Region, SubRegion, InitDate, ForecastDate, HourOfDay).</summary>
public sealed class WindForecastSubRegionRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required string SubRegion { get; init; }
    public required DateOnly InitDate { get; init; }
    public required DateOnly ForecastDate { get; init; }
    public required string HourLabel { get; init; }
    public required byte HourOfDay { get; init; }
    public required decimal ValueMw { get; init; }

    public static WindForecastSubRegionRow? From(CwgSubRegionCell cell, CwgWorkUnit unit)
    {
        var region = unit.Region;
        var init = unit.RepresentativeDate;
        if (region is null || init is null) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null;
        return new WindForecastSubRegionRow
        {
            Region = region, SubRegion = cell.SubRegion, InitDate = init.Value, ForecastDate = cell.ForecastDate,
            HourLabel = cell.HourLabel, HourOfDay = (byte)cell.HourOfDay, ValueMw = mw
        };
    }
}

// -------------------------------------------------------------------- 11. WindHourly (Shape B)
/// <summary>arm.WindHourly — key (HourEndingUtc, Region). "NULL"/blank cells dropped.</summary>
public sealed class WindHourlyRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateTime HourEndingUtc { get; init; }
    public required string Region { get; init; }
    public required decimal ActualMw { get; init; }

    public static WindHourlyRow? From(CwgUnpivotCell cell, CwgWorkUnit unit)
    {
        if (!CwgParse.DateTimeIso(cell.KeyCell, out var he)) return null;
        if (!CwgParse.Decimal(cell.RawValue, out var mw)) return null; // "NULL"/blank → skip
        return new WindHourlyRow { HourEndingUtc = he, Region = cell.Region, ActualMw = mw };
    }
}

// -------------------------------------------------------------------- 12. WindTotalCapacityClimatology (Shape E, 1 block)
/// <summary>arm.WindTotalCapacityClimatology — key (ProductionDate, Region). No Block column. Avg columns are %.</summary>
public sealed class WindTotalCapacityClimatologyRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ProductionDate { get; init; }
    public required string Region { get; init; }
    public required decimal TotalCapacityMw { get; init; }
    public required decimal Avg_1_5 { get; init; }
    public required decimal Avg_6_10 { get; init; }
    public required decimal Avg_11_15 { get; init; }

    public static WindTotalCapacityClimatologyRow? From(CwgCapacityRow cap, CwgWorkUnit unit)
    {
        var prod = unit.RepresentativeDate;
        if (prod is null) return null;
        if (!CwgParse.Decimal(cap.TotalCapacityRaw, out var tot)) return null; // climo TotalCapacity is never blank
        if (!CwgParse.Percent(cap.Avg1_5Raw, out var a1)) return null;
        if (!CwgParse.Percent(cap.Avg6_10Raw, out var a2)) return null;
        if (!CwgParse.Percent(cap.Avg11_15Raw, out var a3)) return null;
        return new WindTotalCapacityClimatologyRow
        {
            ProductionDate = prod.Value, Region = cap.Region, TotalCapacityMw = tot,
            Avg_1_5 = a1, Avg_6_10 = a2, Avg_11_15 = a3
        };
    }
}

// -------------------------------------------------------------------- 13. WindTotalCapacityMW (Shape E, 3 blocks)
/// <summary>arm.WindTotalCapacityMW — key (ProductionDate, Block, Region). Avg columns MW (signed in Change).</summary>
public sealed class WindTotalCapacityMWRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ProductionDate { get; init; }
    public required string Block { get; init; }
    public required string Region { get; init; }
    public required decimal? TotalCapacityMw { get; init; } // blank in Change block → NULL
    public required decimal Avg_1_5 { get; init; }
    public required decimal Avg_6_10 { get; init; }
    public required decimal Avg_11_15 { get; init; }

    public static WindTotalCapacityMWRow? From(CwgCapacityRow cap, CwgWorkUnit unit)
    {
        var prod = unit.RepresentativeDate;
        if (prod is null) return null;
        decimal? tot = CwgParse.Decimal(cap.TotalCapacityRaw, out var t) ? t : null;
        if (!CwgParse.Decimal(cap.Avg1_5Raw, out var a1)) return null;
        if (!CwgParse.Decimal(cap.Avg6_10Raw, out var a2)) return null;
        if (!CwgParse.Decimal(cap.Avg11_15Raw, out var a3)) return null;
        return new WindTotalCapacityMWRow
        {
            ProductionDate = prod.Value, Block = cap.Block, Region = cap.Region, TotalCapacityMw = tot,
            Avg_1_5 = a1, Avg_6_10 = a2, Avg_11_15 = a3
        };
    }
}

// -------------------------------------------------------------------- 14. WindTotalCapacityPct (Shape E, 3 blocks)
/// <summary>arm.WindTotalCapacityPct — key (ProductionDate, Block, Region). Avg columns % (signed in Change).</summary>
public sealed class WindTotalCapacityPctRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required DateOnly ProductionDate { get; init; }
    public required string Block { get; init; }
    public required string Region { get; init; }
    public required decimal? TotalCapacityMw { get; init; } // blank in Change block → NULL
    public required decimal Avg_1_5 { get; init; }
    public required decimal Avg_6_10 { get; init; }
    public required decimal Avg_11_15 { get; init; }

    public static WindTotalCapacityPctRow? From(CwgCapacityRow cap, CwgWorkUnit unit)
    {
        var prod = unit.RepresentativeDate;
        if (prod is null) return null;
        decimal? tot = CwgParse.Decimal(cap.TotalCapacityRaw, out var t) ? t : null;
        if (!CwgParse.Percent(cap.Avg1_5Raw, out var a1)) return null;
        if (!CwgParse.Percent(cap.Avg6_10Raw, out var a2)) return null;
        if (!CwgParse.Percent(cap.Avg11_15Raw, out var a3)) return null;
        return new WindTotalCapacityPctRow
        {
            ProductionDate = prod.Value, Block = cap.Block, Region = cap.Region, TotalCapacityMw = tot,
            Avg_1_5 = a1, Avg_6_10 = a2, Avg_11_15 = a3
        };
    }
}

// -------------------------------------------------------------------- 15. Station (Shape A)
/// <summary>arm.Station — key (Region, Identifier). wban kept literal; ghcnd/state empty → NULL.</summary>
public sealed class StationRow : ICwgFactRow
{
    public int FileLogId { get; set; }
    public required string Region { get; init; }
    public required string Identifier { get; init; }
    public string? WmoId { get; init; }
    public string? Wban { get; init; }
    public string? Ghcnd { get; init; }
    public decimal? Lat { get; init; }
    public decimal? Lon { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }

    // Fields: 0 identifier, 1 wmoid, 2 wban, 3 ghcnd, 4 lat, 5 lon, 6 name, 7 state, 8 country.
    public static StationRow? From(CwgTabularRecord rec, CwgWorkUnit unit)
    {
        var region = unit.Region;
        if (region is null) return null;
        var f = rec.Fields;
        // Short rows are tolerated (descriptor AllowShortRows): any missing/blank field → NULL.
        // Only the identifier (half the PK) is required; without it the row cannot be keyed.
        string At(int i) => i < f.Length ? f[i] : string.Empty;
        var id = At(0).Trim();
        if (id.Length == 0) return null;
        return new StationRow
        {
            Region = region, Identifier = id,
            WmoId = CwgParse.NullIfBlank(At(1)),
            Wban = CwgParse.NullIfBlank(At(2)),   // keep 99999 literal (no sentinel remap)
            Ghcnd = CwgParse.NullIfBlank(At(3)),
            Lat = CwgParse.Decimal(At(4), out var lat) ? lat : null,
            Lon = CwgParse.Decimal(At(5), out var lon) ? lon : null,
            Name = CwgParse.NullIfBlank(At(6)),
            State = CwgParse.NullIfBlank(At(7)),
            Country = CwgParse.NullIfBlank(At(8))
        };
    }
}
