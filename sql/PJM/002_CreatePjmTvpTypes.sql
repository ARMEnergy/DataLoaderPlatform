-- =============================================================================
-- 002_CreatePjmTvpTypes.sql
-- Table-valued parameter types for the PJM loader's merge procs.
--   * dbo.PjmFuelMixType                -> dbo.usp_MergePjmFuelMix
--   * dbo.PjmLoadType                   -> dbo.usp_MergePjmLoad
--   * dbo.PjmLoadMeteredType            -> dbo.usp_MergePjmLoadMetered
--   * dbo.PjmLoadPrelimType             -> dbo.usp_MergePjmLoadPrelim
--   * dbo.PjmLoadEstimatedType          -> dbo.usp_MergePjmLoadEstimated
--   * dbo.PjmLoadForecastHistType       -> dbo.usp_MergePjmLoadForecastHist
--   * dbo.PjmLoadForecastVeryShortType  -> dbo.usp_MergePjmLoadForecastVeryShort
--   * dbo.PjmLoadForecast7DayType       -> dbo.usp_MergePjmLoadForecast7Day
--
-- Target: Loaders:PJM:ConnectionString -> Database=Misc, schema [dbo].
-- Spec:   docs/design/PJM.md §7.4, §14.9.
--
-- TVP CONTRACT: column NAME + ORDER + TYPE must match, exactly, the DataTable
-- built by each Pjm*SqlSink.BuildTable, the 001 table bodies
-- and the 003 MERGE column lists. Source columns only: CreatedAtUtc and
-- ModifiedAtUtc are never TVP columns.
--
-- Re-runnable: IF TYPE_ID(...) IS NULL guarded. A guarded re-run does NOT change
-- an existing type. A shape change needs the procs dropped first (they bind the
-- type), i.e. 999 on an empty database, then 001-003.
--
-- PRODUCTION WRITE when run against ARMH-OPSDB01. Run by hand, by a person.
-- =============================================================================

USE Misc;
GO

DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'Misc'
BEGIN
    RAISERROR('002_CreatePjmTvpTypes: current database is %s, not Misc. Nothing was created.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- dbo.PjmFuelMixType: 6 columns, this exact order.
IF TYPE_ID(N'dbo.PjmFuelMixType') IS NULL
    CREATE TYPE dbo.PjmFuelMixType AS TABLE
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,
        fuel_type                 VARCHAR(50)   NOT NULL,
        mw                        DECIMAL(18,4) NULL,
        fuel_percentage_of_total  DECIMAL(9,6)  NULL,
        is_renewable              BIT           NULL
    );
GO

-- dbo.PjmLoadType: 4 columns, this exact order.
IF TYPE_ID(N'dbo.PjmLoadType') IS NULL
    CREATE TYPE dbo.PjmLoadType AS TABLE
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,
        area                      VARCHAR(50)   NOT NULL,
        instantaneous_load        DECIMAL(18,4) NULL
    );
GO

-- dbo.PjmLoadMeteredType: 8 columns, this exact order (design §14.9).
IF TYPE_ID(N'dbo.PjmLoadMeteredType') IS NULL
    CREATE TYPE dbo.PjmLoadMeteredType AS TABLE
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,
        nerc_region               VARCHAR(50)   NULL,
        mkt_region                VARCHAR(50)   NULL,
        zone                      VARCHAR(50)   NULL,
        load_area                 VARCHAR(50)   NOT NULL,
        mw                        DECIMAL(18,4) NULL,
        is_verified               BIT           NULL
    );
GO

-- dbo.PjmLoadPrelimType: 6 columns, this exact order (design §14.9).
IF TYPE_ID(N'dbo.PjmLoadPrelimType') IS NULL
    CREATE TYPE dbo.PjmLoadPrelimType AS TABLE
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,
        datetime_ending_utc       DATETIME2(0)  NOT NULL,
        datetime_ending_ept       DATETIME2(0)  NOT NULL,
        load_area                 VARCHAR(50)   NOT NULL,
        prelim_load_avg_hourly    DECIMAL(18,4) NULL
    );
GO

-- dbo.PjmLoadEstimatedType: 6 columns, this exact order (design §14.9).
IF TYPE_ID(N'dbo.PjmLoadEstimatedType') IS NULL
    CREATE TYPE dbo.PjmLoadEstimatedType AS TABLE
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,
        datetime_ending_utc       DATETIME2(0)  NOT NULL,
        datetime_ending_ept       DATETIME2(0)  NOT NULL,
        load_area                 VARCHAR(50)   NOT NULL,
        estimated_load_hourly     DECIMAL(18,4) NULL
    );
GO

-- dbo.PjmLoadForecastHistType: 6 columns, this exact order (design §14.9).
IF TYPE_ID(N'dbo.PjmLoadForecastHistType') IS NULL
    CREATE TYPE dbo.PjmLoadForecastHistType AS TABLE
    (
        evaluated_at_utc             DATETIME2(0)  NOT NULL,
        evaluated_at_ept             DATETIME2(0)  NOT NULL,
        forecast_hour_beginning_utc  DATETIME2(0)  NOT NULL,
        forecast_hour_beginning_ept  DATETIME2(0)  NOT NULL,
        forecast_area                VARCHAR(50)   NOT NULL,
        forecast_load_mw             DECIMAL(18,4) NULL
    );
GO

-- dbo.PjmLoadForecastVeryShortType: 8 columns, this exact order (design §14.9).
IF TYPE_ID(N'dbo.PjmLoadForecastVeryShortType') IS NULL
    CREATE TYPE dbo.PjmLoadForecastVeryShortType AS TABLE
    (
        evaluated_at_utc                 DATETIME2(0)  NOT NULL,
        evaluated_at_ept                 DATETIME2(0)  NOT NULL,
        forecast_datetime_beginning_utc  DATETIME2(0)  NOT NULL,
        forecast_datetime_beginning_ept  DATETIME2(0)  NOT NULL,
        forecast_datetime_ending_utc     DATETIME2(0)  NOT NULL,
        forecast_datetime_ending_ept     DATETIME2(0)  NOT NULL,
        forecast_area                    VARCHAR(50)   NOT NULL,
        forecast_load_mw                 DECIMAL(18,4) NULL
    );
GO

-- dbo.PjmLoadForecast7DayType: 8 columns, this exact order (design §14.9).
IF TYPE_ID(N'dbo.PjmLoadForecast7DayType') IS NULL
    CREATE TYPE dbo.PjmLoadForecast7DayType AS TABLE
    (
        evaluated_at_datetime_utc        DATETIME2(0)  NOT NULL,
        evaluated_at_datetime_ept        DATETIME2(0)  NOT NULL,
        forecast_datetime_beginning_utc  DATETIME2(0)  NOT NULL,
        forecast_datetime_beginning_ept  DATETIME2(0)  NOT NULL,
        forecast_datetime_ending_utc     DATETIME2(0)  NOT NULL,
        forecast_datetime_ending_ept     DATETIME2(0)  NOT NULL,
        forecast_area                    VARCHAR(50)   NOT NULL,
        forecast_load_mw                 DECIMAL(18,4) NULL
    );
GO

SET NOEXEC OFF;
GO
