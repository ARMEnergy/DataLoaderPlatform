namespace DataLoader.PJM;

// =============================================================================
// Target rows (design §7, §14.9). Property order documents — and each sink's
// BuildTable mirrors — the TVP column contract of design §7.4 / §14.9 /
// sql/PJM/002. Source columns only: CreatedAtUtc/ModifiedAtUtc are set by the table
// default / proc and are never TVP columns. No FileLogId (design §7.3).
//
// Rows are built ONCE, already normalised, by their mapper via PjmStamps (§14.2),
// so there is no re-stamping step.
// =============================================================================

/// <summary>
/// A row's key as SQL compares it (design §4.6, §14.2): <c>K1</c>/<c>K2</c> exact, <c>Member</c>
/// under OrdinalIgnoreCase after trimming trailing spaces only. <c>K2</c> is <c>null</c> for
/// two-column keys.
/// </summary>
public readonly record struct PjmRowKey(DateTime K1, DateTime? K2, string Member);

/// <summary>Common shape the reader needs for the echo, duplicate and completeness checks (design §14.2).</summary>
public interface IPjmRow
{
    /// <summary>The full natural key.</summary>
    PjmRowKey Key { get; }

    /// <summary>The <see cref="PjmFeed.FilterField"/> value (window echo), or <c>evaluated_at</c> for the snapshot.</summary>
    DateTime EchoUtc { get; }

    /// <summary>What completeness counts.</summary>
    DateTime SlotUtc { get; }
}

/// <summary><c>dbo.pjm_fuel_mix</c> — key <c>(datetime_beginning_utc, fuel_type)</c>. TVP <c>dbo.PjmFuelMixType</c>, 6 columns.</summary>
public sealed class PjmFuelMixRow : IPjmRow
{
    public required DateTime DatetimeBeginningUtc { get; init; }   // 1 DATETIME2(0) NOT NULL
    public required DateTime DatetimeBeginningEpt { get; init; }   // 2 DATETIME2(0) NOT NULL
    public required string FuelType { get; init; }                 // 3 VARCHAR(50)  NOT NULL
    public decimal? Mw { get; init; }                              // 4 DECIMAL(18,4) NULL
    public decimal? FuelPercentageOfTotal { get; init; }           // 5 DECIMAL(9,6)  NULL — [RECON], optional
    public bool? IsRenewable { get; init; }                        // 6 BIT           NULL

    PjmRowKey IPjmRow.Key => new(DatetimeBeginningUtc, null, FuelType);
    DateTime IPjmRow.EchoUtc => DatetimeBeginningUtc;
    DateTime IPjmRow.SlotUtc => DatetimeBeginningUtc;
}

/// <summary><c>dbo.pjm_load</c> — key <c>(datetime_beginning_utc, area)</c>. TVP <c>dbo.PjmLoadType</c>, 4 columns.</summary>
public sealed class PjmLoadRow : IPjmRow
{
    public required DateTime DatetimeBeginningUtc { get; init; }   // 1 DATETIME2(0) NOT NULL
    public required DateTime DatetimeBeginningEpt { get; init; }   // 2 DATETIME2(0) NOT NULL
    public required string Area { get; init; }                     // 3 VARCHAR(50)  NOT NULL
    public decimal? InstantaneousLoad { get; init; }               // 4 DECIMAL(18,4) NULL

    PjmRowKey IPjmRow.Key => new(DatetimeBeginningUtc, null, Area);
    DateTime IPjmRow.EchoUtc => DatetimeBeginningUtc;
    DateTime IPjmRow.SlotUtc => DatetimeBeginningUtc;
}

/// <summary><c>dbo.pjm_load_metered</c> — key <c>(datetime_beginning_utc, load_area)</c>. TVP <c>dbo.PjmLoadMeteredType</c>, 8 columns.</summary>
public sealed class PjmLoadMeteredRow : IPjmRow
{
    public required DateTime DatetimeBeginningUtc { get; init; }   // 1 DATETIME2(0) NOT NULL
    public required DateTime DatetimeBeginningEpt { get; init; }   // 2 DATETIME2(0) NOT NULL
    public string? NercRegion { get; init; }                       // 3 VARCHAR(50)  NULL
    public string? MktRegion { get; init; }                        // 4 VARCHAR(50)  NULL
    public string? Zone { get; init; }                             // 5 VARCHAR(50)  NULL
    public required string LoadArea { get; init; }                 // 6 VARCHAR(50)  NOT NULL
    public decimal? Mw { get; init; }                              // 7 DECIMAL(18,4) NULL
    public bool? IsVerified { get; init; }                         // 8 BIT           NULL

    PjmRowKey IPjmRow.Key => new(DatetimeBeginningUtc, null, LoadArea);
    DateTime IPjmRow.EchoUtc => DatetimeBeginningUtc;
    DateTime IPjmRow.SlotUtc => DatetimeBeginningUtc;
}

/// <summary><c>dbo.pjm_load_prelim</c> — key <c>(datetime_beginning_utc, load_area)</c>. TVP <c>dbo.PjmLoadPrelimType</c>, 6 columns.</summary>
public sealed class PjmLoadPrelimRow : IPjmRow
{
    public required DateTime DatetimeBeginningUtc { get; init; }   // 1 DATETIME2(0) NOT NULL
    public required DateTime DatetimeBeginningEpt { get; init; }   // 2 DATETIME2(0) NOT NULL
    public required DateTime DatetimeEndingUtc { get; init; }      // 3 DATETIME2(0) NOT NULL
    public required DateTime DatetimeEndingEpt { get; init; }      // 4 DATETIME2(0) NOT NULL
    public required string LoadArea { get; init; }                 // 5 VARCHAR(50)  NOT NULL
    public decimal? PrelimLoadAvgHourly { get; init; }             // 6 DECIMAL(18,4) NULL

    PjmRowKey IPjmRow.Key => new(DatetimeBeginningUtc, null, LoadArea);
    DateTime IPjmRow.EchoUtc => DatetimeBeginningUtc;
    DateTime IPjmRow.SlotUtc => DatetimeBeginningUtc;
}

/// <summary><c>dbo.pjm_load_estimated</c> — key <c>(datetime_beginning_utc, load_area)</c>. TVP <c>dbo.PjmLoadEstimatedType</c>, 6 columns.</summary>
public sealed class PjmLoadEstimatedRow : IPjmRow
{
    public required DateTime DatetimeBeginningUtc { get; init; }   // 1 DATETIME2(0) NOT NULL
    public required DateTime DatetimeBeginningEpt { get; init; }   // 2 DATETIME2(0) NOT NULL
    public required DateTime DatetimeEndingUtc { get; init; }      // 3 DATETIME2(0) NOT NULL
    public required DateTime DatetimeEndingEpt { get; init; }      // 4 DATETIME2(0) NOT NULL
    public required string LoadArea { get; init; }                 // 5 VARCHAR(50)  NOT NULL
    public decimal? EstimatedLoadHourly { get; init; }             // 6 DECIMAL(18,4) NULL

    PjmRowKey IPjmRow.Key => new(DatetimeBeginningUtc, null, LoadArea);
    DateTime IPjmRow.EchoUtc => DatetimeBeginningUtc;
    DateTime IPjmRow.SlotUtc => DatetimeBeginningUtc;
}

/// <summary>
/// <c>dbo.pjm_load_forecast_hist</c> — key <c>(evaluated_at_utc, forecast_hour_beginning_utc, forecast_area)</c>.
/// TVP <c>dbo.PjmLoadForecastHistType</c>, 6 columns. ⚠ <c>(forecast_hour, area)</c> alone has 484 duplicates a day.
/// </summary>
public sealed class PjmLoadForecastHistRow : IPjmRow
{
    public required DateTime EvaluatedAtUtc { get; init; }             // 1 DATETIME2(0) NOT NULL
    public required DateTime EvaluatedAtEpt { get; init; }             // 2 DATETIME2(0) NOT NULL
    public required DateTime ForecastHourBeginningUtc { get; init; }   // 3 DATETIME2(0) NOT NULL
    public required DateTime ForecastHourBeginningEpt { get; init; }   // 4 DATETIME2(0) NOT NULL
    public required string ForecastArea { get; init; }                 // 5 VARCHAR(50)  NOT NULL
    public decimal? ForecastLoadMw { get; init; }                      // 6 DECIMAL(18,4) NULL

    PjmRowKey IPjmRow.Key => new(EvaluatedAtUtc, ForecastHourBeginningUtc, ForecastArea);
    DateTime IPjmRow.EchoUtc => EvaluatedAtUtc;
    DateTime IPjmRow.SlotUtc => EvaluatedAtUtc;
}

/// <summary>
/// <c>dbo.pjm_load_forecast_very_short</c> — key <c>(evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area)</c>.
/// TVP <c>dbo.PjmLoadForecastVeryShortType</c>, 8 columns.
/// </summary>
public sealed class PjmLoadForecastVeryShortRow : IPjmRow
{
    public required DateTime EvaluatedAtUtc { get; init; }                 // 1 DATETIME2(0) NOT NULL
    public required DateTime EvaluatedAtEpt { get; init; }                 // 2 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeBeginningUtc { get; init; }   // 3 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeBeginningEpt { get; init; }   // 4 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeEndingUtc { get; init; }      // 5 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeEndingEpt { get; init; }      // 6 DATETIME2(0) NOT NULL
    public required string ForecastArea { get; init; }                     // 7 VARCHAR(50)  NOT NULL
    public decimal? ForecastLoadMw { get; init; }                          // 8 DECIMAL(18,4) NULL

    PjmRowKey IPjmRow.Key => new(EvaluatedAtUtc, ForecastDatetimeBeginningUtc, ForecastArea);
    DateTime IPjmRow.EchoUtc => EvaluatedAtUtc;
    DateTime IPjmRow.SlotUtc => EvaluatedAtUtc;
}

/// <summary>
/// <c>dbo.pjm_load_forecast_7day</c> — key <c>(evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area)</c>.
/// TVP <c>dbo.PjmLoadForecast7DayType</c>, 8 columns. <c>evaluated_at_datetime_*</c> keeps its REAL seconds.
/// </summary>
public sealed class PjmLoadForecast7DayRow : IPjmRow
{
    public required DateTime EvaluatedAtDatetimeUtc { get; init; }         // 1 DATETIME2(0) NOT NULL
    public required DateTime EvaluatedAtDatetimeEpt { get; init; }         // 2 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeBeginningUtc { get; init; }   // 3 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeBeginningEpt { get; init; }   // 4 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeEndingUtc { get; init; }      // 5 DATETIME2(0) NOT NULL
    public required DateTime ForecastDatetimeEndingEpt { get; init; }      // 6 DATETIME2(0) NOT NULL
    public required string ForecastArea { get; init; }                     // 7 VARCHAR(50)  NOT NULL
    public decimal? ForecastLoadMw { get; init; }                          // 8 DECIMAL(18,4) NULL

    PjmRowKey IPjmRow.Key => new(EvaluatedAtDatetimeUtc, ForecastDatetimeBeginningUtc, ForecastArea);
    DateTime IPjmRow.EchoUtc => EvaluatedAtDatetimeUtc;
    DateTime IPjmRow.SlotUtc => ForecastDatetimeBeginningUtc;
}
