using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Extensions.Logging;

namespace DataLoader.MISO;

// =============================================================================
// The four SQL sinks (design §2). Each MERGEs one unit — a single batch — into
// miso.<table> via miso.usp_MergeMiso<X>(@Records miso.Miso<X>Type). BuildTable's
// columns match sql/MISO/002 by NAME + ORDER + TYPE + nullability (tvp-contract-check):
// DATETIME2(0) → DateTime, DATE → DateTime, VARCHAR → string, DECIMAL(18,4) → decimal.
// Source columns only. NO de-duplication: the reader fails a duplicate first and the
// proc THROWs 50001 on one as defence in depth.
// =============================================================================

public abstract class MisoSqlSinkBase<TRow> : SqlSinkBase<TRow> where TRow : IMisoIntervalRow
{
    private readonly MisoSettings _settings;

    protected MisoSqlSinkBase(MisoSettings settings, ILogger logger) : base(logger) => _settings = settings;

    protected override string GetConnectionString() => _settings.ConnectionString;

    /// <summary>The procs return one INT <c>RecordsProcessed</c> = changed/inserted rows; a no-op re-read records 0.</summary>
    protected override bool ProcedureReturnsRowCount => true;

    internal DataTable BuildTableForTests(IReadOnlyList<TRow> rows) => BuildTable(rows);

    protected static DataColumn Col(DataTable t, string name, Type type, bool allowNull)
    {
        var c = t.Columns.Add(name, type);
        c.AllowDBNull = allowNull;
        return c;
    }

    /// <summary>The four interval columns every table starts with.</summary>
    protected static void IntervalColumns(DataTable t)
    {
        Col(t, "interval_start_local", typeof(DateTime), allowNull: false);   // DATETIME2(0) NOT NULL
        Col(t, "interval_start_utc", typeof(DateTime), allowNull: false);     // DATETIME2(0) NOT NULL
        Col(t, "interval_end_local", typeof(DateTime), allowNull: false);     // DATETIME2(0) NOT NULL
        Col(t, "interval_end_utc", typeof(DateTime), allowNull: false);       // DATETIME2(0) NOT NULL
    }

    protected static void Measures(DataTable t, params string[] names)
    {
        foreach (var n in names) Col(t, n, typeof(decimal), allowNull: true);   // DECIMAL(18,4) NULL
    }
}

/// <summary><c>miso.miso_zonal_load_hourly</c> via <c>miso.usp_MergeMisoZonalLoadHourly</c> / <c>miso.MisoZonalLoadHourlyType</c>.</summary>
public sealed class MisoZonalLoadHourlySqlSink : MisoSqlSinkBase<MisoZonalLoadHourlyRow>
{
    public MisoZonalLoadHourlySqlSink(MisoSettings settings, ILogger<MisoZonalLoadHourlySqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "miso.usp_MergeMisoZonalLoadHourly";
    protected override string TableValuedParameterType => "miso.MisoZonalLoadHourlyType";

    protected override DataTable BuildTable(IReadOnlyList<MisoZonalLoadHourlyRow> rows)
    {
        var t = new DataTable();
        IntervalColumns(t);
        Measures(t, "lrz1", "lrz2_7", "lrz3_5", "lrz4", "lrz6", "lrz8_9_10", "miso");
        foreach (var r in rows)
            t.Rows.Add(r.IntervalStartLocal, r.IntervalStartUtc, r.IntervalEndLocal, r.IntervalEndUtc,
                DbNullable(r.Lrz1), DbNullable(r.Lrz2_7), DbNullable(r.Lrz3_5), DbNullable(r.Lrz4),
                DbNullable(r.Lrz6), DbNullable(r.Lrz8_9_10), DbNullable(r.Miso));
        return t;
    }
}

/// <summary><c>miso.miso_load_hourly</c> via <c>miso.usp_MergeMisoLoadHourly</c> / <c>miso.MisoLoadHourlyType</c>.</summary>
public sealed class MisoLoadHourlySqlSink : MisoSqlSinkBase<MisoLoadHourlyRow>
{
    public MisoLoadHourlySqlSink(MisoSettings settings, ILogger<MisoLoadHourlySqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "miso.usp_MergeMisoLoadHourly";
    protected override string TableValuedParameterType => "miso.MisoLoadHourlyType";

    protected override DataTable BuildTable(IReadOnlyList<MisoLoadHourlyRow> rows)
    {
        var t = new DataTable();
        IntervalColumns(t);
        Measures(t, "north", "central", "south", "miso");
        foreach (var r in rows)
            t.Rows.Add(r.IntervalStartLocal, r.IntervalStartUtc, r.IntervalEndLocal, r.IntervalEndUtc,
                DbNullable(r.North), DbNullable(r.Central), DbNullable(r.South), DbNullable(r.Miso));
        return t;
    }
}

/// <summary><c>miso.miso_fuel_mix_hourly</c> via <c>miso.usp_MergeMisoFuelMixHourly</c> / <c>miso.MisoFuelMixHourlyType</c>.</summary>
public sealed class MisoFuelMixHourlySqlSink : MisoSqlSinkBase<MisoFuelMixHourlyRow>
{
    public MisoFuelMixHourlySqlSink(MisoSettings settings, ILogger<MisoFuelMixHourlySqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "miso.usp_MergeMisoFuelMixHourly";
    protected override string TableValuedParameterType => "miso.MisoFuelMixHourlyType";

    protected override DataTable BuildTable(IReadOnlyList<MisoFuelMixHourlyRow> rows)
    {
        var t = new DataTable();
        IntervalColumns(t);
        Col(t, "region", typeof(string), allowNull: false);                    // VARCHAR(20) NOT NULL
        Measures(t, "coal", "gas", "nuclear", "water", "wind", "solar", "other", "storage", "total_mw");
        foreach (var r in rows)
            t.Rows.Add(r.IntervalStartLocal, r.IntervalStartUtc, r.IntervalEndLocal, r.IntervalEndUtc, r.Region,
                DbNullable(r.Coal), DbNullable(r.Gas), DbNullable(r.Nuclear), DbNullable(r.Water),
                DbNullable(r.Wind), DbNullable(r.Solar), DbNullable(r.Other), DbNullable(r.Storage), DbNullable(r.TotalMw));
        return t;
    }
}

/// <summary><c>miso.miso_load_forecast_mtlf</c> via <c>miso.usp_MergeMisoLoadForecastMtlf</c> / <c>miso.MisoLoadForecastMtlfType</c>.</summary>
public sealed class MisoLoadForecastMtlfSqlSink : MisoSqlSinkBase<MisoLoadForecastMtlfRow>
{
    public MisoLoadForecastMtlfSqlSink(MisoSettings settings, ILogger<MisoLoadForecastMtlfSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "miso.usp_MergeMisoLoadForecastMtlf";
    protected override string TableValuedParameterType => "miso.MisoLoadForecastMtlfType";

    protected override DataTable BuildTable(IReadOnlyList<MisoLoadForecastMtlfRow> rows)
    {
        var t = new DataTable();
        Col(t, "init_date", typeof(DateTime), allowNull: false);               // DATE NOT NULL
        IntervalColumns(t);
        Col(t, "region", typeof(string), allowNull: false);                    // VARCHAR(20) NOT NULL
        Col(t, "local_resource_zone", typeof(string), allowNull: false);       // VARCHAR(10) NOT NULL
        Measures(t, "load_forecast");
        foreach (var r in rows)
            t.Rows.Add(r.InitDate.ToDateTime(TimeOnly.MinValue), r.IntervalStartLocal, r.IntervalStartUtc, r.IntervalEndLocal, r.IntervalEndUtc,
                r.Region, r.LocalResourceZone, DbNullable(r.LoadForecast));
        return t;
    }
}
