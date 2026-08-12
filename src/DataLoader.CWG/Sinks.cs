using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.CWG;

// =============================================================================
// Per-endpoint SQL sinks (×15). Each bulk-merges its FileLogId-stamped rows into
// arm.<Table> via arm.usp_BulkMerge<Table>(@Records arm.<Table>Tvp). Column ORDER
// in every BuildTable mirrors sql/CWG/002 EXACTLY — FileLogId first (a
// load-bearing contract). Each de-dups the batch on its merge key before building
// the TVP so an in-file duplicate cannot break the MERGE. Concurrent-MERGE
// serialization is applied automatically by SqlSinkBase (SqlWriteGate).
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

    protected static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
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
            .GroupBy(r => (r.Region, r.Station, r.ProductionDate, r.ForecastDate))
            .Select(g => g.Last());

        // Order: FileLogId, Region, ProductionDate, ForecastDate, Station, FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("Station", typeof(string));
        t.Columns.Add("FcstMin", typeof(decimal));
        t.Columns.Add("FcstMax", typeof(decimal));
        t.Columns.Add("FcstAvg", typeof(decimal));
        t.Columns.Add("NormMin", typeof(decimal));
        t.Columns.Add("NormMax", typeof(decimal));
        t.Columns.Add("Hdd", typeof(short));
        t.Columns.Add("Cdd", typeof(short));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, D(r.ProductionDate), D(r.ForecastDate), r.Station,
                r.FcstMin, r.FcstMax, r.FcstAvg, r.NormMin, r.NormMax, r.Hdd, r.Cdd);
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
            .GroupBy(r => (r.Station, r.ProductionDate, r.ForecastDate))
            .Select(g => g.Last());

        // Order: FileLogId, ProductionDate, ForecastDate, Station, FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("Station", typeof(string));
        t.Columns.Add("FcstMin", typeof(decimal));
        t.Columns.Add("FcstMax", typeof(decimal));
        t.Columns.Add("FcstAvg", typeof(decimal));
        t.Columns.Add("NormMin", typeof(decimal));
        t.Columns.Add("NormMax", typeof(decimal));
        t.Columns.Add("Hdd", typeof(short));
        t.Columns.Add("Cdd", typeof(short));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, D(r.ProductionDate), D(r.ForecastDate), r.Station,
                r.FcstMin, r.FcstMax, r.FcstAvg, r.NormMin, r.NormMax, r.Hdd, r.Cdd);
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
            .GroupBy(r => (r.Region, r.Station, r.ObsDate))
            .Select(g => g.Last());

        // Order: FileLogId, Region, ObsDate, Station, MinTemp, MaxTemp, Hdd, Cdd.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ObsDate", typeof(DateTime));
        t.Columns.Add("Station", typeof(string));
        t.Columns.Add("MinTemp", typeof(decimal));
        t.Columns.Add("MaxTemp", typeof(decimal));
        t.Columns.Add("Hdd", typeof(short));
        t.Columns.Add("Cdd", typeof(short));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, D(r.ObsDate), r.Station, r.MinTemp, r.MaxTemp, r.Hdd, r.Cdd);
        return t;
    }
}

// -------------------------------------------------------------------- 4. DailyNormal
public sealed class DailyNormalSqlSink : CwgSqlSinkBase<DailyNormalRow>
{
    public DailyNormalSqlSink(IOptions<CwgSettings> settings, ILogger<DailyNormalSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeDailyNormal";
    protected override string TableValuedParameterType => "arm.DailyNormalTvp";

    protected override DataTable BuildTable(IReadOnlyList<DailyNormalRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.MonthDay, r.Region))
            .Select(g => g.Last());

        // Order: FileLogId, MonthDay, Region, NormalMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("MonthDay", typeof(string));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("NormalMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.MonthDay, r.Region, r.NormalMw);
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
            .GroupBy(r => (r.Region, r.InitDate, r.ForecastDate, r.HourOfDay))
            .Select(g => g.Last());

        // Order: FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("InitDate", typeof(DateTime));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("HourOfDay", typeof(byte));
        t.Columns.Add("ValueMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, D(r.InitDate), D(r.ForecastDate), r.HourLabel, r.HourOfDay, r.ValueMw);
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
            .GroupBy(r => (r.Region, r.InitDate, r.ForecastDate, r.HourOfDay))
            .Select(g => g.Last());

        // Order: FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ChangeMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("InitDate", typeof(DateTime));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("HourOfDay", typeof(byte));
        t.Columns.Add("ChangeMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, D(r.InitDate), D(r.ForecastDate), r.HourLabel, r.HourOfDay, r.ChangeMw);
        return t;
    }
}

// -------------------------------------------------------------------- 7. SolarHourly
public sealed class SolarHourlySqlSink : CwgSqlSinkBase<SolarHourlyRow>
{
    public SolarHourlySqlSink(IOptions<CwgSettings> settings, ILogger<SolarHourlySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeSolarHourly";
    protected override string TableValuedParameterType => "arm.SolarHourlyTvp";

    protected override DataTable BuildTable(IReadOnlyList<SolarHourlyRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.HourEndingUtc, r.Region))
            .Select(g => g.Last());

        // Order: FileLogId, HourEndingUtc, Region, ActualMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("HourEndingUtc", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ActualMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.HourEndingUtc, r.Region, r.ActualMw);
        return t;
    }
}

// -------------------------------------------------------------------- 8. NationalDegreeDays
public sealed class NationalDegreeDaysSqlSink : CwgSqlSinkBase<NationalDegreeDaysRow>
{
    public NationalDegreeDaysSqlSink(IOptions<CwgSettings> settings, ILogger<NationalDegreeDaysSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeNationalDegreeDays";
    protected override string TableValuedParameterType => "arm.NationalDegreeDaysTvp";

    protected override DataTable BuildTable(IReadOnlyList<NationalDegreeDaysRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.RunDate, r.Dates))
            .Select(g => g.Last());

        // Order: FileLogId, RunDate, Dates, NgHdd, NgHdd30y, NgHdd10y, NgHddLastY, PopCdd, PopCdd30y,
        //        PopCdd10y, PopCddLastY, ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY, IsForecast.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("RunDate", typeof(DateTime));
        t.Columns.Add("Dates", typeof(DateTime));
        t.Columns.Add("NgHdd", typeof(decimal));
        t.Columns.Add("NgHdd30y", typeof(decimal));
        t.Columns.Add("NgHdd10y", typeof(decimal));
        t.Columns.Add("NgHddLastY", typeof(decimal));
        t.Columns.Add("PopCdd", typeof(decimal));
        t.Columns.Add("PopCdd30y", typeof(decimal));
        t.Columns.Add("PopCdd10y", typeof(decimal));
        t.Columns.Add("PopCddLastY", typeof(decimal));
        t.Columns.Add("ElecCdd", typeof(decimal));
        t.Columns.Add("ElecCdd30y", typeof(decimal));
        t.Columns.Add("ElecCdd10y", typeof(decimal));
        t.Columns.Add("ElecCddLastY", typeof(decimal));
        t.Columns.Add("IsForecast", typeof(bool));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, D(r.RunDate), D(r.Dates),
                r.NgHdd, r.NgHdd30y, r.NgHdd10y, r.NgHddLastY,
                r.PopCdd, r.PopCdd30y, r.PopCdd10y, r.PopCddLastY,
                r.ElecCdd, r.ElecCdd30y, r.ElecCdd10y, r.ElecCddLastY, r.IsForecast);
        return t;
    }
}

// -------------------------------------------------------------------- 9. WindForecast
public sealed class WindForecastSqlSink : CwgSqlSinkBase<WindForecastRow>
{
    public WindForecastSqlSink(IOptions<CwgSettings> settings, ILogger<WindForecastSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindForecast";
    protected override string TableValuedParameterType => "arm.WindForecastTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindForecastRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.Region, r.InitDate, r.ForecastDate, r.HourOfDay))
            .Select(g => g.Last());

        // Order: FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("InitDate", typeof(DateTime));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("HourOfDay", typeof(byte));
        t.Columns.Add("ValueMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, D(r.InitDate), D(r.ForecastDate), r.HourLabel, r.HourOfDay, r.ValueMw);
        return t;
    }
}

// -------------------------------------------------------------------- 10. WindForecastSubRegion
public sealed class WindForecastSubRegionSqlSink : CwgSqlSinkBase<WindForecastSubRegionRow>
{
    public WindForecastSubRegionSqlSink(IOptions<CwgSettings> settings, ILogger<WindForecastSubRegionSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindForecastSubRegion";
    protected override string TableValuedParameterType => "arm.WindForecastSubRegionTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindForecastSubRegionRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.Region, r.SubRegion, r.InitDate, r.ForecastDate, r.HourOfDay))
            .Select(g => g.Last());

        // Order: FileLogId, Region, SubRegion, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("SubRegion", typeof(string));
        t.Columns.Add("InitDate", typeof(DateTime));
        t.Columns.Add("ForecastDate", typeof(DateTime));
        t.Columns.Add("HourLabel", typeof(string));
        t.Columns.Add("HourOfDay", typeof(byte));
        t.Columns.Add("ValueMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, r.SubRegion, D(r.InitDate), D(r.ForecastDate), r.HourLabel, r.HourOfDay, r.ValueMw);
        return t;
    }
}

// -------------------------------------------------------------------- 11. WindHourly
public sealed class WindHourlySqlSink : CwgSqlSinkBase<WindHourlyRow>
{
    public WindHourlySqlSink(IOptions<CwgSettings> settings, ILogger<WindHourlySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindHourly";
    protected override string TableValuedParameterType => "arm.WindHourlyTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindHourlyRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.HourEndingUtc, r.Region))
            .Select(g => g.Last());

        // Order: FileLogId, HourEndingUtc, Region, ActualMw.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("HourEndingUtc", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("ActualMw", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.HourEndingUtc, r.Region, r.ActualMw);
        return t;
    }
}

// -------------------------------------------------------------------- 12. WindTotalCapacityClimatology
public sealed class WindTotalCapacityClimatologySqlSink : CwgSqlSinkBase<WindTotalCapacityClimatologyRow>
{
    public WindTotalCapacityClimatologySqlSink(IOptions<CwgSettings> settings, ILogger<WindTotalCapacityClimatologySqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindTotalCapacityClimatology";
    protected override string TableValuedParameterType => "arm.WindTotalCapacityClimatologyTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindTotalCapacityClimatologyRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ProductionDate, r.Region))
            .Select(g => g.Last());

        // Order: FileLogId, ProductionDate, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("TotalCapacityMw", typeof(decimal));
        t.Columns.Add("Avg_1_5", typeof(decimal));
        t.Columns.Add("Avg_6_10", typeof(decimal));
        t.Columns.Add("Avg_11_15", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, D(r.ProductionDate), r.Region, r.TotalCapacityMw, r.Avg_1_5, r.Avg_6_10, r.Avg_11_15);
        return t;
    }
}

// -------------------------------------------------------------------- 13. WindTotalCapacityMW
public sealed class WindTotalCapacityMWSqlSink : CwgSqlSinkBase<WindTotalCapacityMWRow>
{
    public WindTotalCapacityMWSqlSink(IOptions<CwgSettings> settings, ILogger<WindTotalCapacityMWSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindTotalCapacityMW";
    protected override string TableValuedParameterType => "arm.WindTotalCapacityMWTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindTotalCapacityMWRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ProductionDate, r.Block, r.Region))
            .Select(g => g.Last());

        // Order: FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("Block", typeof(string));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("TotalCapacityMw", typeof(decimal));
        t.Columns.Add("Avg_1_5", typeof(decimal));
        t.Columns.Add("Avg_6_10", typeof(decimal));
        t.Columns.Add("Avg_11_15", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, D(r.ProductionDate), r.Block, r.Region,
                DbNullable(r.TotalCapacityMw), r.Avg_1_5, r.Avg_6_10, r.Avg_11_15);
        return t;
    }
}

// -------------------------------------------------------------------- 14. WindTotalCapacityPct
public sealed class WindTotalCapacityPctSqlSink : CwgSqlSinkBase<WindTotalCapacityPctRow>
{
    public WindTotalCapacityPctSqlSink(IOptions<CwgSettings> settings, ILogger<WindTotalCapacityPctSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeWindTotalCapacityPct";
    protected override string TableValuedParameterType => "arm.WindTotalCapacityPctTvp";

    protected override DataTable BuildTable(IReadOnlyList<WindTotalCapacityPctRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.ProductionDate, r.Block, r.Region))
            .Select(g => g.Last());

        // Order: FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("ProductionDate", typeof(DateTime));
        t.Columns.Add("Block", typeof(string));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("TotalCapacityMw", typeof(decimal));
        t.Columns.Add("Avg_1_5", typeof(decimal));
        t.Columns.Add("Avg_6_10", typeof(decimal));
        t.Columns.Add("Avg_11_15", typeof(decimal));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, D(r.ProductionDate), r.Block, r.Region,
                DbNullable(r.TotalCapacityMw), r.Avg_1_5, r.Avg_6_10, r.Avg_11_15);
        return t;
    }
}

// -------------------------------------------------------------------- 15. Station
public sealed class StationSqlSink : CwgSqlSinkBase<StationRow>
{
    public StationSqlSink(IOptions<CwgSettings> settings, ILogger<StationSqlSink> logger) : base(settings, logger) { }
    protected override string StoredProcedureName => "arm.usp_BulkMergeStation";
    protected override string TableValuedParameterType => "arm.StationTvp";

    protected override DataTable BuildTable(IReadOnlyList<StationRow> rows)
    {
        var deduped = rows
            .GroupBy(r => (r.Region, r.Identifier))
            .Select(g => g.Last());

        // Order: FileLogId, Region, Identifier, WmoId, Wban, Ghcnd, Lat, Lon, Name, State, Country.
        var t = new DataTable();
        t.Columns.Add("FileLogId", typeof(int));
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("Identifier", typeof(string));
        t.Columns.Add("WmoId", typeof(string));
        t.Columns.Add("Wban", typeof(string));
        t.Columns.Add("Ghcnd", typeof(string));
        t.Columns.Add("Lat", typeof(decimal));
        t.Columns.Add("Lon", typeof(decimal));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("Country", typeof(string));

        foreach (var r in deduped)
            t.Rows.Add(r.FileLogId, r.Region, r.Identifier,
                DbNullableObj(r.WmoId), DbNullableObj(r.Wban), DbNullableObj(r.Ghcnd),
                DbNullable(r.Lat), DbNullable(r.Lon), DbNullableObj(r.Name), DbNullableObj(r.State), DbNullableObj(r.Country));
        return t;
    }
}
