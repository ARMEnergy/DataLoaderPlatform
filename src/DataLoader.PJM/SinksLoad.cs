using System.Data;
using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.PJM;

// =============================================================================
// The six sinks added by design §14.9. Each MERGEs one unit — a single batch, never
// split — via dbo.usp_MergePjm<X>(@Records dbo.Pjm<X>Type). Each BuildTable mirrors
// its §14.9 literal list by NAME + ORDER + TYPE + nullability (tvp-contract-check):
// DATETIME2(0) → DateTime, VARCHAR(50) → string, DECIMAL(18,4) → decimal, BIT → bool.
// Source columns only; CreatedAtUtc/ModifiedAtUtc are never TVP columns. No de-dup
// (design §4.6): the reader fails a duplicate first, and the proc THROWs on one.
// =============================================================================

/// <summary>
/// <c>dbo.pjm_load_metered</c> via <c>dbo.usp_MergePjmLoadMetered</c> / <c>dbo.PjmLoadMeteredType</c>. Column order:
/// <c>(datetime_beginning_utc, datetime_beginning_ept, nerc_region, mkt_region, zone, load_area, mw, is_verified)</c>.
/// </summary>
public sealed class PjmLoadMeteredSqlSink : PjmSqlSinkBase<PjmLoadMeteredRow>
{
    public PjmLoadMeteredSqlSink(PjmSettings settings, ILogger<PjmLoadMeteredSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoadMetered";
    protected override string TableValuedParameterType => "dbo.PjmLoadMeteredType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadMeteredRow> rows)
    {
        var t = new DataTable();
        Col(t, "datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "nerc_region", typeof(string), allowNull: true);                 // VARCHAR(50)   NULL
        Col(t, "mkt_region", typeof(string), allowNull: true);                  // VARCHAR(50)   NULL
        Col(t, "zone", typeof(string), allowNull: true);                        // VARCHAR(50)   NULL
        Col(t, "load_area", typeof(string), allowNull: false);                  // VARCHAR(50)   NOT NULL
        Col(t, "mw", typeof(decimal), allowNull: true);                         // DECIMAL(18,4) NULL
        Col(t, "is_verified", typeof(bool), allowNull: true);                   // BIT           NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.DatetimeBeginningUtc,
                r.DatetimeBeginningEpt,
                DbNullableObj(r.NercRegion),
                DbNullableObj(r.MktRegion),
                DbNullableObj(r.Zone),
                r.LoadArea,
                DbNullable(r.Mw),
                DbNullable(r.IsVerified));
        return t;
    }
}

/// <summary>
/// <c>dbo.pjm_load_prelim</c> via <c>dbo.usp_MergePjmLoadPrelim</c> / <c>dbo.PjmLoadPrelimType</c>. Column order:
/// <c>(datetime_beginning_utc, datetime_beginning_ept, datetime_ending_utc, datetime_ending_ept, load_area, prelim_load_avg_hourly)</c>.
/// </summary>
public sealed class PjmLoadPrelimSqlSink : PjmSqlSinkBase<PjmLoadPrelimRow>
{
    public PjmLoadPrelimSqlSink(PjmSettings settings, ILogger<PjmLoadPrelimSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoadPrelim";
    protected override string TableValuedParameterType => "dbo.PjmLoadPrelimType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadPrelimRow> rows)
    {
        var t = new DataTable();
        Col(t, "datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_ending_utc", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "datetime_ending_ept", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "load_area", typeof(string), allowNull: false);                  // VARCHAR(50)   NOT NULL
        Col(t, "prelim_load_avg_hourly", typeof(decimal), allowNull: true);     // DECIMAL(18,4) NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.DatetimeBeginningUtc,
                r.DatetimeBeginningEpt,
                r.DatetimeEndingUtc,
                r.DatetimeEndingEpt,
                r.LoadArea,
                DbNullable(r.PrelimLoadAvgHourly));
        return t;
    }
}

/// <summary>
/// <c>dbo.pjm_load_estimated</c> via <c>dbo.usp_MergePjmLoadEstimated</c> / <c>dbo.PjmLoadEstimatedType</c>. Column order:
/// <c>(datetime_beginning_utc, datetime_beginning_ept, datetime_ending_utc, datetime_ending_ept, load_area, estimated_load_hourly)</c>.
/// </summary>
public sealed class PjmLoadEstimatedSqlSink : PjmSqlSinkBase<PjmLoadEstimatedRow>
{
    public PjmLoadEstimatedSqlSink(PjmSettings settings, ILogger<PjmLoadEstimatedSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoadEstimated";
    protected override string TableValuedParameterType => "dbo.PjmLoadEstimatedType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadEstimatedRow> rows)
    {
        var t = new DataTable();
        Col(t, "datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "datetime_ending_utc", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "datetime_ending_ept", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "load_area", typeof(string), allowNull: false);                  // VARCHAR(50)   NOT NULL
        Col(t, "estimated_load_hourly", typeof(decimal), allowNull: true);      // DECIMAL(18,4) NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.DatetimeBeginningUtc,
                r.DatetimeBeginningEpt,
                r.DatetimeEndingUtc,
                r.DatetimeEndingEpt,
                r.LoadArea,
                DbNullable(r.EstimatedLoadHourly));
        return t;
    }
}

/// <summary>
/// <c>dbo.pjm_load_forecast_hist</c> via <c>dbo.usp_MergePjmLoadForecastHist</c> / <c>dbo.PjmLoadForecastHistType</c>. Column order:
/// <c>(evaluated_at_utc, evaluated_at_ept, forecast_hour_beginning_utc, forecast_hour_beginning_ept, forecast_area, forecast_load_mw)</c>.
/// </summary>
public sealed class PjmLoadForecastHistSqlSink : PjmSqlSinkBase<PjmLoadForecastHistRow>
{
    public PjmLoadForecastHistSqlSink(PjmSettings settings, ILogger<PjmLoadForecastHistSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoadForecastHist";
    protected override string TableValuedParameterType => "dbo.PjmLoadForecastHistType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadForecastHistRow> rows)
    {
        var t = new DataTable();
        Col(t, "evaluated_at_utc", typeof(DateTime), allowNull: false);              // DATETIME2(0)  NOT NULL
        Col(t, "evaluated_at_ept", typeof(DateTime), allowNull: false);              // DATETIME2(0)  NOT NULL
        Col(t, "forecast_hour_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "forecast_hour_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "forecast_area", typeof(string), allowNull: false);                   // VARCHAR(50)   NOT NULL
        Col(t, "forecast_load_mw", typeof(decimal), allowNull: true);                // DECIMAL(18,4) NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.EvaluatedAtUtc,
                r.EvaluatedAtEpt,
                r.ForecastHourBeginningUtc,
                r.ForecastHourBeginningEpt,
                r.ForecastArea,
                DbNullable(r.ForecastLoadMw));
        return t;
    }
}

/// <summary>
/// <c>dbo.pjm_load_forecast_very_short</c> via <c>dbo.usp_MergePjmLoadForecastVeryShort</c> / <c>dbo.PjmLoadForecastVeryShortType</c>.
/// Column order: <c>(evaluated_at_utc, evaluated_at_ept, forecast_datetime_beginning_utc, forecast_datetime_beginning_ept,
/// forecast_datetime_ending_utc, forecast_datetime_ending_ept, forecast_area, forecast_load_mw)</c>.
/// </summary>
public sealed class PjmLoadForecastVeryShortSqlSink : PjmSqlSinkBase<PjmLoadForecastVeryShortRow>
{
    public PjmLoadForecastVeryShortSqlSink(PjmSettings settings, ILogger<PjmLoadForecastVeryShortSqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoadForecastVeryShort";
    protected override string TableValuedParameterType => "dbo.PjmLoadForecastVeryShortType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadForecastVeryShortRow> rows)
    {
        var t = new DataTable();
        Col(t, "evaluated_at_utc", typeof(DateTime), allowNull: false);                  // DATETIME2(0)  NOT NULL
        Col(t, "evaluated_at_ept", typeof(DateTime), allowNull: false);                  // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_ending_utc", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_ending_ept", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "forecast_area", typeof(string), allowNull: false);                       // VARCHAR(50)   NOT NULL
        Col(t, "forecast_load_mw", typeof(decimal), allowNull: true);                    // DECIMAL(18,4) NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.EvaluatedAtUtc,
                r.EvaluatedAtEpt,
                r.ForecastDatetimeBeginningUtc,
                r.ForecastDatetimeBeginningEpt,
                r.ForecastDatetimeEndingUtc,
                r.ForecastDatetimeEndingEpt,
                r.ForecastArea,
                DbNullable(r.ForecastLoadMw));
        return t;
    }
}

/// <summary>
/// <c>dbo.pjm_load_forecast_7day</c> via <c>dbo.usp_MergePjmLoadForecast7Day</c> / <c>dbo.PjmLoadForecast7DayType</c>.
/// Column order: <c>(evaluated_at_datetime_utc, evaluated_at_datetime_ept, forecast_datetime_beginning_utc,
/// forecast_datetime_beginning_ept, forecast_datetime_ending_utc, forecast_datetime_ending_ept, forecast_area, forecast_load_mw)</c>.
/// </summary>
public sealed class PjmLoadForecast7DaySqlSink : PjmSqlSinkBase<PjmLoadForecast7DayRow>
{
    public PjmLoadForecast7DaySqlSink(PjmSettings settings, ILogger<PjmLoadForecast7DaySqlSink> logger) : base(settings, logger) { }

    protected override string StoredProcedureName => "dbo.usp_MergePjmLoadForecast7Day";
    protected override string TableValuedParameterType => "dbo.PjmLoadForecast7DayType";

    protected override DataTable BuildTable(IReadOnlyList<PjmLoadForecast7DayRow> rows)
    {
        var t = new DataTable();
        Col(t, "evaluated_at_datetime_utc", typeof(DateTime), allowNull: false);         // DATETIME2(0)  NOT NULL
        Col(t, "evaluated_at_datetime_ept", typeof(DateTime), allowNull: false);         // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_beginning_utc", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_beginning_ept", typeof(DateTime), allowNull: false);   // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_ending_utc", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "forecast_datetime_ending_ept", typeof(DateTime), allowNull: false);      // DATETIME2(0)  NOT NULL
        Col(t, "forecast_area", typeof(string), allowNull: false);                       // VARCHAR(50)   NOT NULL
        Col(t, "forecast_load_mw", typeof(decimal), allowNull: true);                    // DECIMAL(18,4) NULL

        foreach (var r in rows)
            t.Rows.Add(
                r.EvaluatedAtDatetimeUtc,
                r.EvaluatedAtDatetimeEpt,
                r.ForecastDatetimeBeginningUtc,
                r.ForecastDatetimeBeginningEpt,
                r.ForecastDatetimeEndingUtc,
                r.ForecastDatetimeEndingEpt,
                r.ForecastArea,
                DbNullable(r.ForecastLoadMw));
        return t;
    }
}

/// <summary>
/// Decorator for a SNAPSHOT feed's sink (design §14.8). "Skip the write if that evaluation is
/// already stored" is met by the proc's MERGE + <c>EXCEPT</c> change test with no pre-check, so a
/// re-read of a stored evaluation reports 0 changed rows; this wrapper only makes that visible at
/// Information. It never alters the rows or the count, so <c>core.LoadLog</c> records the true 0.
/// </summary>
public sealed class PjmSnapshotSink<TRow> : ISink<TRow> where TRow : IPjmRow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ISink<TRow> _inner;
    private readonly string _feed;
    private readonly ILogger _logger;

    public PjmSnapshotSink(ISink<TRow> inner, string feed, ILogger logger)
    {
        _inner = inner;
        _feed = feed;
        _logger = logger;
    }

    public async Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
    {
        var written = await _inner.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
        if (written == 0 && rows.Count > 0)
            _logger.LogInformation("[PJM {Feed}] 7_day evaluation {EvaluatedAt} already stored (0 changes)",
                _feed, rows[0].EchoUtc.ToString("yyyy-MM-ddTHH:mm:ss", Inv));
        return written;
    }
}
