using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CWG;

// =============================================================================
// Per-endpoint SQL sinks (×19). Each bulk-merges its rows into arm.<Table> via
// arm.usp_BulkMerge<Table>(@Records arm.<Table>Tvp).
//
// Column NAME + ORDER + TYPE in every BuildTable mirrors sql/CWG/002 EXACTLY —
// a load-bearing contract. The TVPs carry key columns then value columns and
// deliberately carry NEITHER RecordHash NOR ModifiedAtUtc: the merge proc
// computes the hash from the value columns and stamps the timestamp, so no sink
// can drift from the hash definition.
//
// Each sink de-dups the batch on its merge key before building the TVP so an
// in-file duplicate cannot break the MERGE (the proc de-dups again server-side).
// Concurrent-MERGE serialization is applied automatically by SqlSinkBase
// (SqlWriteGate).
// =============================================================================

/// <summary>Common base wiring for a CWG fact sink.</summary>
public abstract class CwgSqlSinkBase<TRow> : SqlSinkBase<TRow>
{
    private readonly CwgSettings _settings;

    protected CwgSqlSinkBase(IOptions<CwgSettings> settings, ILogger logger) : base(logger)
    {
        _settings = settings.Value;
    }

    protected override string GetConnectionString() => _settings.ConnectionString;
    protected override bool ProcedureReturnsRowCount => true;

    /// <summary>DateOnly → the DateTime a DATE TVP column expects.</summary>
    protected static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    /// <summary>Nullable DateOnly → DateTime or DBNull.</summary>
    protected static object DN(DateOnly? d) => d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

    /// <summary>Nullable measure → value or DBNull (shorthand for the very frequent case).</summary>
    protected static object V(decimal? v) => v.HasValue ? v.Value : DBNull.Value;

    /// <summary>Nullable BIT → value or DBNull.</summary>
    protected static object B(bool? v) => v.HasValue ? v.Value : DBNull.Value;

    /// <summary>Nullable FLOAT → value or DBNull.</summary>
    protected static object F(double? v) => v.HasValue ? v.Value : DBNull.Value;

    /// <summary>
    /// Adds the wide region value columns in the row type's own RegionColumns order, so the
    /// DataTable column NAMES are literally the target table's region column names and cannot
    /// drift from them.
    /// </summary>
    protected static void AddRegionColumns(DataTable t, string[] regionColumns)
    {
        foreach (var c in regionColumns) t.Columns.Add(c, typeof(decimal));
    }

    /// <summary>Boxes a wide row's values (NULL → DBNull) for DataTable.Rows.Add(params object[]).</summary>
    protected static object[] WideCells(object key, decimal?[] values)
    {
        var cells = new object[values.Length + 1];
        cells[0] = key;
        for (var i = 0; i < values.Length; i++) cells[i + 1] = V(values[i]);
        return cells;
    }
}

// -------------------------------------------------------------------- 1. CityForecast
public sealed class CityForecastSqlSink : CwgSqlSinkBase<CityForecastRow>
{
    public CityForecastSqlSink(IOptions<CwgSettings> settings, ILogger<CityForecastSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeCityForecast";
    protected override string TableValuedParameterType => "arm.CityForecastTvp";

    protected override DataTable BuildTable(IReadOnlyList<CityForecastRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ProductionDate, r.Date, r.Station))
            .Select(g => g.Last());

        // Order: ProductionDate, Date, Station, ForecastMin, ForecastMax, ForecastAvg,
        //        NormMin, NormMax, HDD, CDD — mirrors arm.CityForecastTvp exactly.
        var t = new DataTable();
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Station", typeof(string));
        t.Columns.Add("ForecastMin", typeof(decimal));
        t.Columns.Add("ForecastMax", typeof(decimal));
        t.Columns.Add("ForecastAvg", typeof(decimal));
        t.Columns.Add("NormMin", typeof(decimal));
        t.Columns.Add("NormMax", typeof(decimal));
        t.Columns.Add("HDD", typeof(decimal));
        t.Columns.Add("CDD", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ProductionDate), D(r.Date), r.Station,
                V(r.ForecastMin), V(r.ForecastMax), V(r.ForecastAvg),
                V(r.NormMin), V(r.NormMax), V(r.HDD), V(r.CDD));
        return t;
    }
}

// -------------------------------------------------------------------- 2. CityGasForecast
public sealed class CityGasForecastSqlSink : CwgSqlSinkBase<CityGasForecastRow>
{
    public CityGasForecastSqlSink(IOptions<CwgSettings> settings, ILogger<CityGasForecastSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeCityGasForecast";
    protected override string TableValuedParameterType => "arm.CityGasForecastTvp";

    protected override DataTable BuildTable(IReadOnlyList<CityGasForecastRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ProductionDate, r.Date, r.Station))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Station", typeof(string));
        t.Columns.Add("ForecastMin", typeof(decimal));
        t.Columns.Add("ForecastMax", typeof(decimal));
        t.Columns.Add("ForecastAvg", typeof(decimal));
        t.Columns.Add("NormMin", typeof(decimal));
        t.Columns.Add("NormMax", typeof(decimal));
        t.Columns.Add("HDD", typeof(decimal));
        t.Columns.Add("CDD", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ProductionDate), D(r.Date), r.Station,
                V(r.ForecastMin), V(r.ForecastMax), V(r.ForecastAvg),
                V(r.NormMin), V(r.NormMax), V(r.HDD), V(r.CDD));
        return t;
    }
}

// -------------------------------------------------------------------- 3. CityObservation
public sealed class CityObservationSqlSink : CwgSqlSinkBase<CityObservationRow>
{
    public CityObservationSqlSink(IOptions<CwgSettings> settings, ILogger<CityObservationSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeCityObservation";
    protected override string TableValuedParameterType => "arm.CityObservationTvp";

    protected override DataTable BuildTable(IReadOnlyList<CityObservationRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.Date, r.Station))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Station", typeof(string));
        t.Columns.Add("MinTemp", typeof(decimal));
        t.Columns.Add("MaxTemp", typeof(decimal));
        t.Columns.Add("HDD", typeof(decimal));
        t.Columns.Add("CDD", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.Date), r.Station, V(r.MinTemp), V(r.MaxTemp), V(r.HDD), V(r.CDD));
        return t;
    }
}

// -------------------------------------------------------------------- 4. DailyNormal (WIDE)
public sealed class DailyNormalSqlSink : CwgSqlSinkBase<DailyNormalRow>
{
    public DailyNormalSqlSink(IOptions<CwgSettings> settings, ILogger<DailyNormalSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeDailyNormal";
    protected override string TableValuedParameterType => "arm.DailyNormalTvp";

    protected override DataTable BuildTable(IReadOnlyList<DailyNormalRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.Month, r.Day))
            .Select(g => g.Last());

        // Order: Month, Day, then DailyNormalRow.RegionColumns.
        var t = new DataTable();
        t.Columns.Add("Month", typeof(byte));
        t.Columns.Add("Day", typeof(byte));
        AddRegionColumns(t, DailyNormalRow.RegionColumns);

        foreach (var r in deduped)
        {
            var cells = new object[2 + r.Values.Length];
            cells[0] = r.Month;
            cells[1] = r.Day;
            for (var i = 0; i < r.Values.Length; i++) cells[i + 2] = V(r.Values[i]);
            t.Rows.Add(cells);
        }
        return t;
    }
}

// -------------------------------------------------------------------- 5. SolarForecast
public sealed class SolarForecastSqlSink : CwgSqlSinkBase<SolarForecastRow>
{
    public SolarForecastSqlSink(IOptions<CwgSettings> settings, ILogger<SolarForecastSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSolarForecast";
    protected override string TableValuedParameterType => "arm.SolarForecastTvp";

    protected override DataTable BuildTable(IReadOnlyList<SolarForecastRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.UTCHourEnding, r.Region))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("UTCHourEnding", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ForecastLabel", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("Value", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.UTCHourEnding, r.Region, D(r.ForecastLabel), r.HourLabel, V(r.Value));
        return t;
    }
}

// -------------------------------------------------------------------- 6. SolarForecastChange
public sealed class SolarForecastChangeSqlSink : CwgSqlSinkBase<SolarForecastChangeRow>
{
    public SolarForecastChangeSqlSink(IOptions<CwgSettings> settings, ILogger<SolarForecastChangeSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSolarForecastChange";
    protected override string TableValuedParameterType => "arm.SolarForecastChangeTvp";

    protected override DataTable BuildTable(IReadOnlyList<SolarForecastChangeRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.UTCHourEnding, r.Region))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("UTCHourEnding", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ForecastLabel", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("Value", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.UTCHourEnding, r.Region, D(r.ForecastLabel), r.HourLabel, V(r.Value));
        return t;
    }
}

// -------------------------------------------------------------------- 7. SolarHourly (WIDE)
public sealed class SolarHourlySqlSink : CwgSqlSinkBase<SolarHourlyRow>
{
    public SolarHourlySqlSink(IOptions<CwgSettings> settings, ILogger<SolarHourlySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSolarHourly";
    protected override string TableValuedParameterType => "arm.SolarHourlyTvp";

    protected override DataTable BuildTable(IReadOnlyList<SolarHourlyRow> rows)
    {
        var deduped = rows
            .GroupBy(r => r.UTCHourEnding)
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("UTCHourEnding", typeof(DateTime));
        AddRegionColumns(t, SolarHourlyRow.RegionColumns);

        foreach (var r in deduped)
            t.Rows.Add(WideCells(r.UTCHourEnding, r.Values));
        return t;
    }
}

// -------------------------------------------------------------------- 8. StateDegreeDayObservation
public sealed class StateDegreeDayObservationSqlSink : CwgSqlSinkBase<StateDegreeDayObservationRow>
{
    public StateDegreeDayObservationSqlSink(IOptions<CwgSettings> settings, ILogger<StateDegreeDayObservationSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeStateDegreeDayObservation";
    protected override string TableValuedParameterType => "arm.StateDegreeDayObservationTvp";

    protected override DataTable BuildTable(IReadOnlyList<StateDegreeDayObservationRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ObservationDate, r.RegionName, r.State))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ObservationDate", typeof(DateTime));
        t.Columns.Add("RegionName", typeof(string));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("POP_HDD", typeof(decimal));
        t.Columns.Add("30Y_POP_HDD", typeof(decimal));
        t.Columns.Add("10Y_POP_HDD", typeof(decimal));
        t.Columns.Add("LAST_Y_POP_HDD", typeof(decimal));
        t.Columns.Add("POP_CDD", typeof(decimal));
        t.Columns.Add("30Y_POP_CDD", typeof(decimal));
        t.Columns.Add("10Y_POP_CDD", typeof(decimal));
        t.Columns.Add("LAST_Y_POP_CDD", typeof(decimal));
        t.Columns.Add("IS_FORECAST", typeof(bool));
        t.Columns.Add("GAS_WEIGHT", typeof(decimal));
        t.Columns.Add("ELCT_WEIGHT", typeof(decimal));
        t.Columns.Add("POP_WEIGHT", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ObservationDate), r.RegionName, r.State,
                V(r.PopHdd), V(r.PopHdd30y), V(r.PopHdd10y), V(r.PopHddLastY),
                V(r.PopCdd), V(r.PopCdd30y), V(r.PopCdd10y), V(r.PopCddLastY),
                B(r.IsForecast), V(r.GasWeight), V(r.ElctWeight), V(r.PopWeight));
        return t;
    }
}

// -------------------------------------------------------------------- 9. Station
public sealed class StationSqlSink : CwgSqlSinkBase<StationRow>
{
    public StationSqlSink(IOptions<CwgSettings> settings, ILogger<StationSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeStation";
    protected override string TableValuedParameterType => "arm.StationTvp";

    protected override DataTable BuildTable(IReadOnlyList<StationRow> rows)
    {
        var deduped = rows
            .GroupBy(r => r.Identifier)
            .Select(g => g.Last());

        // No Point column (the merge proc derives it from Latitude/Longitude) and no
        // MappingId (IDENTITY on the target) — both deliberately absent from the TVP.
        var t = new DataTable();
        t.Columns.Add("Identifier", typeof(string));
        t.Columns.Add("WMOID", typeof(string));
        t.Columns.Add("WBAN", typeof(string));
        t.Columns.Add("GHCND", typeof(string));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("Country", typeof(string));

        foreach (var r in deduped)
            t.Rows.Add(r.Identifier, NullIfEmpty(r.WmoId), NullIfEmpty(r.Wban), NullIfEmpty(r.Ghcnd),
                F(r.Latitude), F(r.Longitude),
                NullIfEmpty(r.Name), NullIfEmpty(r.State), NullIfEmpty(r.Country));
        return t;
    }
}

// -------------------------------------------------------------------- 10. Regions5DegreeDays
public sealed class Regions5DegreeDaysSqlSink : CwgSqlSinkBase<Regions5DegreeDaysRow>
{
    public Regions5DegreeDaysSqlSink(IOptions<CwgSettings> settings, ILogger<Regions5DegreeDaysSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeRegions5DegreeDays";
    protected override string TableValuedParameterType => "arm.Regions5DegreeDaysTvp";

    protected override DataTable BuildTable(IReadOnlyList<Regions5DegreeDaysRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ObservationDate, r.Date, r.Region))
            .Select(g => g.Last());

        var t = CwgDegreeDayTables.RegionalTable();
        foreach (var r in deduped)
            t.Rows.Add(D(r.ObservationDate), D(r.Date), r.Region,
                V(r.NgHdd), V(r.NgHdd30y), V(r.NgHdd10y), V(r.NgHddLastY),
                V(r.PopCdd), V(r.PopCdd30y), V(r.PopCdd10y), V(r.PopCddLastY),
                V(r.ElecCdd), V(r.ElecCdd30y), V(r.ElecCdd10y), V(r.ElecCddLastY),
                B(r.IsForecast), V(r.GasWeight), V(r.ElctWeight), V(r.PopWeight));
        return t;
    }
}

// -------------------------------------------------------------------- 11. Regions9DegreeDays
public sealed class Regions9DegreeDaysSqlSink : CwgSqlSinkBase<Regions9DegreeDaysRow>
{
    public Regions9DegreeDaysSqlSink(IOptions<CwgSettings> settings, ILogger<Regions9DegreeDaysSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeRegions9DegreeDays";
    protected override string TableValuedParameterType => "arm.Regions9DegreeDaysTvp";

    protected override DataTable BuildTable(IReadOnlyList<Regions9DegreeDaysRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ObservationDate, r.Date, r.Region))
            .Select(g => g.Last());

        var t = CwgDegreeDayTables.RegionalTable();
        foreach (var r in deduped)
            t.Rows.Add(D(r.ObservationDate), D(r.Date), r.Region,
                V(r.NgHdd), V(r.NgHdd30y), V(r.NgHdd10y), V(r.NgHddLastY),
                V(r.PopCdd), V(r.PopCdd30y), V(r.PopCdd10y), V(r.PopCddLastY),
                V(r.ElecCdd), V(r.ElecCdd30y), V(r.ElecCdd10y), V(r.ElecCddLastY),
                B(r.IsForecast), V(r.GasWeight), V(r.ElctWeight), V(r.PopWeight));
        return t;
    }
}

// -------------------------------------------------------------------- 12. ISODegreeDays
public sealed class ISODegreeDaysSqlSink : CwgSqlSinkBase<ISODegreeDaysRow>
{
    public ISODegreeDaysSqlSink(IOptions<CwgSettings> settings, ILogger<ISODegreeDaysSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeISODegreeDays";
    protected override string TableValuedParameterType => "arm.ISODegreeDaysTvp";

    protected override DataTable BuildTable(IReadOnlyList<ISODegreeDaysRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ObservationDate, r.Date, r.Region))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ObservationDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("POP_HDD", typeof(decimal));
        t.Columns.Add("30Y_POP_HDD", typeof(decimal));
        t.Columns.Add("10Y_POP_HDD", typeof(decimal));
        t.Columns.Add("LAST_Y_POP_HDD", typeof(decimal));
        t.Columns.Add("POP_CDD", typeof(decimal));
        t.Columns.Add("30Y_POP_CDD", typeof(decimal));
        t.Columns.Add("10Y_POP_CDD", typeof(decimal));
        t.Columns.Add("LAST_Y_POP_CDD", typeof(decimal));
        t.Columns.Add("IS_FORECAST", typeof(bool));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ObservationDate), D(r.Date), r.Region,
                V(r.PopHdd), V(r.PopHdd30y), V(r.PopHdd10y), V(r.PopHddLastY),
                V(r.PopCdd), V(r.PopCdd30y), V(r.PopCdd10y), V(r.PopCddLastY),
                B(r.IsForecast));
        return t;
    }
}

// -------------------------------------------------------------------- 13. NationalDegreeDays
public sealed class NationalDegreeDaysSqlSink : CwgSqlSinkBase<NationalDegreeDaysRow>
{
    public NationalDegreeDaysSqlSink(IOptions<CwgSettings> settings, ILogger<NationalDegreeDaysSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeNationalDegreeDays";
    protected override string TableValuedParameterType => "arm.NationalDegreeDaysTvp";

    protected override DataTable BuildTable(IReadOnlyList<NationalDegreeDaysRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ObservationDate, r.Date))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ObservationDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("NG_HDD", typeof(decimal));
        t.Columns.Add("30Y_NG_HDD", typeof(decimal));
        t.Columns.Add("10Y_NG_HDD", typeof(decimal));
        t.Columns.Add("LAST_Y_NG_HDD", typeof(decimal));
        t.Columns.Add("POP_CDD", typeof(decimal));
        t.Columns.Add("30Y_POP_CDD", typeof(decimal));
        t.Columns.Add("10Y_POP_CDD", typeof(decimal));
        t.Columns.Add("LAST_Y_POP_CDD", typeof(decimal));
        t.Columns.Add("ELEC_CDD", typeof(decimal));
        t.Columns.Add("30Y_ELEC_CDD", typeof(decimal));
        t.Columns.Add("10Y_ELEC_CDD", typeof(decimal));
        t.Columns.Add("LAST_Y_ELEC_CDD", typeof(decimal));
        t.Columns.Add("IS_FORECAST", typeof(bool));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ObservationDate), D(r.Date),
                V(r.NgHdd), V(r.NgHdd30y), V(r.NgHdd10y), V(r.NgHddLastY),
                V(r.PopCdd), V(r.PopCdd30y), V(r.PopCdd10y), V(r.PopCddLastY),
                V(r.ElecCdd), V(r.ElecCdd30y), V(r.ElecCdd10y), V(r.ElecCddLastY),
                B(r.IsForecast));
        return t;
    }
}

// -------------------------------------------------------------------- 14. WindForecast
public sealed class WindForecastSqlSink : CwgSqlSinkBase<WindForecastRow>
{
    public WindForecastSqlSink(IOptions<CwgSettings> settings, ILogger<WindForecastSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindForecast";
    protected override string TableValuedParameterType => "arm.WindForecastTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindForecastRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.UTCHourEnding, r.Region))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("UTCHourEnding", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ForecastLabel", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("Value", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.UTCHourEnding, r.Region, D(r.ForecastLabel), r.HourLabel, V(r.Value));
        return t;
    }
}

// -------------------------------------------------------------------- 15. WindForecastSubRegion
public sealed class WindForecastSubRegionSqlSink : CwgSqlSinkBase<WindForecastSubRegionRow>
{
    public WindForecastSubRegionSqlSink(IOptions<CwgSettings> settings, ILogger<WindForecastSubRegionSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindForecastSubRegion";
    protected override string TableValuedParameterType => "arm.WindForecastSubRegionTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindForecastSubRegionRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.UTCHourEnding, r.Region, r.SubRegion))
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("UTCHourEnding", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("SubRegion", typeof(string));
        t.Columns.Add("ForecastLabel", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("Value", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.UTCHourEnding, r.Region, r.SubRegion,
                D(r.ForecastLabel), r.HourLabel, V(r.Value));
        return t;
    }
}

// -------------------------------------------------------------------- 16. WindHourly (WIDE)
public sealed class WindHourlySqlSink : CwgSqlSinkBase<WindHourlyRow>
{
    public WindHourlySqlSink(IOptions<CwgSettings> settings, ILogger<WindHourlySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindHourly";
    protected override string TableValuedParameterType => "arm.WindHourlyTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindHourlyRow> rows)
    {
        var deduped = rows
            .GroupBy(r => r.UTCHourEnding)
            .Select(g => g.Last());

        var t = new DataTable();
        t.Columns.Add("UTCHourEnding", typeof(DateTime));
        AddRegionColumns(t, WindHourlyRow.RegionColumns);

        foreach (var r in deduped)
            t.Rows.Add(WideCells(r.UTCHourEnding, r.Values));
        return t;
    }
}

// -------------------------------------------------------------------- 17. WindTotalCapacityClimatology
public sealed class WindTotalCapacityClimatologySqlSink : CwgSqlSinkBase<WindTotalCapacityClimatologyRow>
{
    public WindTotalCapacityClimatologySqlSink(IOptions<CwgSettings> settings, ILogger<WindTotalCapacityClimatologySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindTotalCapacityClimatology";
    protected override string TableValuedParameterType => "arm.WindTotalCapacityClimatologyTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindTotalCapacityClimatologyRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.Region))
            .Select(g => g.Last());

        var t = CwgCapacityTables.Table();
        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.Region, V(r.TotalCapacity),
                V(r.OneToFiveDayAverage), V(r.SixToTenDayAverage), V(r.ElevenToFifteenDayAverage));
        return t;
    }
}

// -------------------------------------------------------------------- 18. WindTotalCapacityMW
public sealed class WindTotalCapacityMWSqlSink : CwgSqlSinkBase<WindTotalCapacityMWRow>
{
    public WindTotalCapacityMWSqlSink(IOptions<CwgSettings> settings, ILogger<WindTotalCapacityMWSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindTotalCapacityMW";
    protected override string TableValuedParameterType => "arm.WindTotalCapacityMWTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindTotalCapacityMWRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.Region))
            .Select(g => g.Last());

        var t = CwgCapacityTables.Table();
        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.Region, V(r.TotalCapacity),
                V(r.OneToFiveDayAverage), V(r.SixToTenDayAverage), V(r.ElevenToFifteenDayAverage));
        return t;
    }
}

// -------------------------------------------------------------------- 19. WindTotalCapacityPct
public sealed class WindTotalCapacityPctSqlSink : CwgSqlSinkBase<WindTotalCapacityPctRow>
{
    public WindTotalCapacityPctSqlSink(IOptions<CwgSettings> settings, ILogger<WindTotalCapacityPctSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindTotalCapacityPct";
    protected override string TableValuedParameterType => "arm.WindTotalCapacityPctTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindTotalCapacityPctRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ForecastDate, r.Region))
            .Select(g => g.Last());

        var t = CwgCapacityTables.Table();
        foreach (var r in deduped)
            t.Rows.Add(D(r.ForecastDate), r.Region, V(r.TotalCapacity),
                V(r.OneToFiveDayAverage), V(r.SixToTenDayAverage), V(r.ElevenToFifteenDayAverage));
        return t;
    }
}

// =============================================================================
// Shared DataTable shapes — the three degree-day regional TVPs and the three
// capacity TVPs are column-for-column identical, so the shape is declared once.
// =============================================================================

internal static class CwgDegreeDayTables
{
    /// <summary>arm.Regions5DegreeDaysTvp / arm.Regions9DegreeDaysTvp (identical column lists).</summary>
    public static DataTable RegionalTable()
    {
        var t = new DataTable();
        t.Columns.Add("ObservationDate", typeof(DateTime));
        t.Columns.Add("Date", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("NG_HDD", typeof(decimal));
        t.Columns.Add("30Y_NG_HDD", typeof(decimal));
        t.Columns.Add("10Y_NG_HDD", typeof(decimal));
        t.Columns.Add("LAST_Y_NG_HDD", typeof(decimal));
        t.Columns.Add("POP_CDD", typeof(decimal));
        t.Columns.Add("30Y_POP_CDD", typeof(decimal));
        t.Columns.Add("10Y_POP_CDD", typeof(decimal));
        t.Columns.Add("LAST_Y_POP_CDD", typeof(decimal));
        t.Columns.Add("ELEC_CDD", typeof(decimal));
        t.Columns.Add("30Y_ELEC_CDD", typeof(decimal));
        t.Columns.Add("10Y_ELEC_CDD", typeof(decimal));
        t.Columns.Add("LAST_Y_ELEC_CDD", typeof(decimal));
        t.Columns.Add("IS_FORECAST", typeof(bool));
        t.Columns.Add("GAS_WEIGHT", typeof(decimal));
        t.Columns.Add("ELCT_WEIGHT", typeof(decimal));
        t.Columns.Add("POP_WEIGHT", typeof(decimal));
        return t;
    }
}

internal static class CwgCapacityTables
{
    /// <summary>
    /// arm.WindTotalCapacityClimatologyTvp / MWTvp / PctTvp — identical column lists.
    /// No Block column: only the Current block is written (see CwgCapacity).
    /// </summary>
    public static DataTable Table()
    {
        var t = new DataTable();
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("TotalCapacity", typeof(decimal));
        t.Columns.Add("OneToFiveDayAverage", typeof(decimal));
        t.Columns.Add("SixToTenDayAverage", typeof(decimal));
        t.Columns.Add("ElevenToFifteenDayAverage", typeof(decimal));
        return t;
    }
}
