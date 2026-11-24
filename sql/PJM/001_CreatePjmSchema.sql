-- =============================================================================
-- 001_CreatePjmSchema.sql
-- Target tables for the PJM (Data Miner 2 API v1) loader.
--   * dbo.pjm_fuel_mix                  <- feed gen_by_fuel            (hourly, by fuel type)
--   * dbo.pjm_load                      <- feed inst_load              (5-minute, by area)
--   * dbo.pjm_load_metered              <- feed hrl_load_metered       (hourly, by load_area)
--   * dbo.pjm_load_prelim               <- feed hrl_load_prelim        (hourly, by load_area)
--   * dbo.pjm_load_estimated            <- feed hrl_load_estimated     (hourly, by load_area)
--   * dbo.pjm_load_forecast_hist        <- feed load_frcstd_hist       (per evaluation, hourly targets)
--   * dbo.pjm_load_forecast_very_short  <- feed very_short_load_frcst  (per evaluation, 5-minute targets)
--   * dbo.pjm_load_forecast_7day        <- feed load_frcstd_7_day      (SNAPSHOT, hourly targets)
--
-- Target: Loaders:PJM:ConnectionString -> Database=Misc, schema [dbo].
-- Spec:   docs/design/PJM.md §7.1, §7.2, §9, §11, §14.9, §14.10.
--
-- `Misc` is an IT-assigned SHARED database. This script NEVER creates it. If IT
-- has not created it yet, `USE Misc` fails and the guard below turns every later
-- batch into a no-op, which is the intended stop. Every object is dbo-qualified
-- and pjm-named because other applications may own tables in `Misc`. The six
-- tables added 2026-11-23 must be on the IT table request before deployment.
--
-- Re-runnable: each table is IF OBJECT_ID(...) IS NULL guarded. A guarded re-run
-- does NOT alter an existing table; a shape change needs a migration (or 999 on
-- an empty database) first.
--
-- PRODUCTION WRITE when run against ARMH-OPSDB01. Run by hand, by a person.
-- Run order: 001 -> 002 -> 003.
-- =============================================================================

USE Misc;
GO

-- Guard: if USE Misc failed (database not created yet), SSMS would otherwise go
-- on running the next batches in whatever database the window points at.
-- SET NOEXEC ON persists across GO for the session; SET NOEXEC OFF at the end
-- restores it.
DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'Misc'
BEGIN
    RAISERROR('001_CreatePjmSchema: current database is %s, not Misc. Nothing was created.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- Collation is chosen by IT and unknown at design time (design §13 item 3).
-- The C# duplicate check assumes a case-insensitive collation with ANSI padding.
DECLARE @Collation NVARCHAR(128) = CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(N'Misc', 'Collation'));
PRINT N'Misc collation: ' + ISNULL(@Collation, N'(unknown)');
GO

-- -----------------------------------------------------------------------------
-- KEYS ARE VERIFIED LIVE (2026-11-23; API doc §6, §11.3, §12.1).
--   dbo.pjm_fuel_mix                  PK (datetime_beginning_utc, fuel_type)
--   dbo.pjm_load                      PK (datetime_beginning_utc, area)
--   dbo.pjm_load_metered              PK (datetime_beginning_utc, load_area)
--   dbo.pjm_load_prelim               PK (datetime_beginning_utc, load_area)
--   dbo.pjm_load_estimated            PK (datetime_beginning_utc, load_area)
--   dbo.pjm_load_forecast_hist        PK (evaluated_at_utc, forecast_hour_beginning_utc, forecast_area)
--   dbo.pjm_load_forecast_very_short  PK (evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area)
--   dbo.pjm_load_forecast_7day        PK (evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area)
-- Each key showed 0 duplicates over complete UTC days including the DST
-- fall-back day 2026-11-01 (and 2026-11-18 for the six newer feeds; the 7day
-- snapshot over the current snapshot). A PK violation still fails loudly; if one
-- ever appears, the full lane re-opens (a different key, or replace-by-scope).
--
-- DST: every *_ept column is prevailing Eastern wall-clock time and REPEATS on
-- the fall-back day (two distinct UTC instants -> the same EPT 01:xx). EPT is
-- therefore NEVER part of a key. Keys use the *_utc columns only: a UTC day is
-- always 24 h and never repeats.
--
-- Timestamps are DATETIME2(0). The reader rejects fractional seconds rather than
-- letting DATETIME2(0) round two distinct instants onto one key.
--
-- Audit columns: CreatedAtUtc is set by its default on INSERT only and is never
-- updated; ModifiedAtUtc is refreshed by the merge proc only when a value really
-- changed. ModifiedAtUtc > CreatedAtUtc on older rows answers "does PJM restate?".
-- -----------------------------------------------------------------------------

-- -----------------------------------------------------------------------------
-- dbo.pjm_fuel_mix  (gen_by_fuel)
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_fuel_mix', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_fuel_mix
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,  -- KEY; hour start, UTC
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        fuel_type                 VARCHAR(50)   NOT NULL,  -- KEY; open taxonomy, stored as received
        mw                        DECIMAL(18,4) NULL,
        fuel_percentage_of_total  DECIMAL(9,6)  NULL,      -- [RECON], optional; scale unknown, stored as received
        is_renewable              BIT           NULL,
        CreatedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_fuel_mix_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc             DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_fuel_mix_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_fuel_mix PRIMARY KEY CLUSTERED (datetime_beginning_utc, fuel_type)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load  (inst_load)
-- `area` holds zones AND region/RTO sums: never sum all areas.
-- Further inst_load columns are expected after the API doc §11.1 metadata call;
-- they are added before enabling, through a full-lane DATABASE_DEVELOPER step.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,  -- KEY; 5-minute interval start, UTC
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        area                      VARCHAR(50)   NOT NULL,  -- KEY; zones and region/RTO sums
        instantaneous_load        DECIMAL(18,4) NULL,      -- MW; "NOT official PJM Loads"
        CreatedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc             DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load PRIMARY KEY CLUSTERED (datetime_beginning_utc, area)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load_metered  (hrl_load_metered)        design §14.9, API doc §12.2
-- PK (datetime_beginning_utc, load_area) - VERIFIED live 2026-11-23: 0 duplicates
-- over the complete UTC days 2026-11-01 (DST fall-back) and 2026-11-18, 720 rows
-- each (30 load_area x 24; all 24 UTC hours present on the fall-back day).
-- is_verified flips preliminary -> verified, i.e. PJM RESTATES this feed with an
-- unknown horizon (V-1); the loader keeps it hot for 30 days (D2).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load_metered', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load_metered
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,  -- KEY; hour start, UTC
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        nerc_region               VARCHAR(50)   NULL,
        mkt_region                VARCHAR(50)   NULL,
        zone                      VARCHAR(50)   NULL,
        load_area                 VARCHAR(50)   NOT NULL,  -- KEY; 30 values
        mw                        DECIMAL(18,4) NULL,
        is_verified               BIT           NULL,      -- restated preliminary -> verified
        CreatedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_metered_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc             DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_metered_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load_metered PRIMARY KEY CLUSTERED (datetime_beginning_utc, load_area)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load_prelim  (hrl_load_prelim)          design §14.9, API doc §12.3
-- PK (datetime_beginning_utc, load_area) - VERIFIED live 2026-11-23: 0 duplicates
-- over the complete UTC days 2026-11-01 (DST fall-back) and 2026-11-18, 240 rows
-- each (10 load_area x 24). UTC ending - beginning is always 60 min; the EPT pair
-- shows 0 min on the fall-back hour (naive wall clock), so never diff EPT.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load_prelim', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load_prelim
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,  -- KEY; hour start, UTC
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        datetime_ending_utc       DATETIME2(0)  NOT NULL,  -- beginning + 60 min
        datetime_ending_ept       DATETIME2(0)  NOT NULL,
        load_area                 VARCHAR(50)   NOT NULL,  -- KEY; 10 values
        prelim_load_avg_hourly    DECIMAL(18,4) NULL,
        CreatedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_prelim_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc             DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_prelim_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load_prelim PRIMARY KEY CLUSTERED (datetime_beginning_utc, load_area)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load_estimated  (hrl_load_estimated)    design §14.9, API doc §12.4
-- PK (datetime_beginning_utc, load_area) - VERIFIED live 2026-11-23: 0 duplicates
-- over the complete UTC day 2026-11-01 (DST fall-back, 240 rows) and 2026-11-18
-- (50 of 240 rows: this feed lags up to ~a week and posts Tue/Fri, so a partial
-- recent day is normal). UTC ending - beginning is always 60 min.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load_estimated', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load_estimated
    (
        datetime_beginning_utc    DATETIME2(0)  NOT NULL,  -- KEY; hour start, UTC
        datetime_beginning_ept    DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        datetime_ending_utc       DATETIME2(0)  NOT NULL,  -- beginning + 60 min
        datetime_ending_ept       DATETIME2(0)  NOT NULL,
        load_area                 VARCHAR(50)   NOT NULL,  -- KEY
        estimated_load_hourly     DECIMAL(18,4) NULL,
        CreatedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_estimated_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc             DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_estimated_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load_estimated PRIMARY KEY CLUSTERED (datetime_beginning_utc, load_area)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load_forecast_hist  (load_frcstd_hist)  design §14.9, API doc §12.5
-- PK (evaluated_at_utc, forecast_hour_beginning_utc, forecast_area) - VERIFIED
-- live 2026-11-23: 0 duplicates over the complete UTC days 2026-11-01 (DST
-- fall-back, 1,991 rows) and 2026-11-18 (1,980 rows). Without evaluated_at_utc
-- the key has 484 duplicates a day (several evaluations forecast one hour).
-- ~10 evaluations per UTC day, at minute :45. Evaluated forecasts are immutable.
-- Note the column names: forecast_hour_*, not forecast_datetime_*.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load_forecast_hist', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load_forecast_hist
    (
        evaluated_at_utc             DATETIME2(0)  NOT NULL,  -- KEY; evaluation time, UTC
        evaluated_at_ept             DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        forecast_hour_beginning_utc  DATETIME2(0)  NOT NULL,  -- KEY; forecast target hour, UTC
        forecast_hour_beginning_ept  DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        forecast_area                VARCHAR(50)   NOT NULL,  -- KEY
        forecast_load_mw             DECIMAL(18,4) NULL,
        CreatedAtUtc                 DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_forecast_hist_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc                DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_forecast_hist_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load_forecast_hist PRIMARY KEY CLUSTERED (evaluated_at_utc, forecast_hour_beginning_utc, forecast_area)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load_forecast_very_short  (very_short_load_frcst)  design §14.9, API doc §12.6
-- PK (evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area) - VERIFIED
-- live 2026-11-23: 0 duplicates over the complete UTC days 2026-11-01 (DST
-- fall-back) and 2026-11-18, 172,800 rows each (288 evaluations x 25 areas x 24
-- 5-minute steps). Immutable once evaluated.
-- RETENTION: PJM keeps only 30 DAYS of this feed. Anything not loaded within
-- that window is gone permanently, so this table is the only history there is.
-- Kept forever, no pruning (user decision 2026-11-23): ~63M rows/year.
-- UTC ending - beginning is always 5 min; the EPT pair shows -55 min on the
-- fall-back hour (naive wall clock), so never diff EPT.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load_forecast_very_short', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load_forecast_very_short
    (
        evaluated_at_utc                 DATETIME2(0)  NOT NULL,  -- KEY; evaluation time, UTC, 5-minute marks
        evaluated_at_ept                 DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        forecast_datetime_beginning_utc  DATETIME2(0)  NOT NULL,  -- KEY; 5-minute target start, UTC
        forecast_datetime_beginning_ept  DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        forecast_datetime_ending_utc     DATETIME2(0)  NOT NULL,  -- beginning + 5 min
        forecast_datetime_ending_ept     DATETIME2(0)  NOT NULL,
        forecast_area                    VARCHAR(50)   NOT NULL,  -- KEY; 25 values
        forecast_load_mw                 DECIMAL(18,4) NULL,
        CreatedAtUtc                     DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_forecast_very_short_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc                    DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_forecast_very_short_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load_forecast_very_short PRIMARY KEY CLUSTERED (evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area)
    );
END
GO

-- -----------------------------------------------------------------------------
-- dbo.pjm_load_forecast_7day  (load_frcstd_7_day)  design §14.8, §14.9, API doc §12.7
-- PK (evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area)
-- - VERIFIED live 2026-11-23 over the current snapshot: 0 duplicates in 4,200
-- rows (25 areas x 168 hourly steps, one evaluated_at_datetime_utc). The feed
-- has no history to read, so no complete-day / DST-day population exists for it.
-- SNAPSHOT: PJM's retention is "None" - the feed holds ONLY the latest
-- evaluation, replaced every ~30 min (:15/:45). History exists only from what
-- the loader captures; hourly runs capture about 1 in 2 evaluations (D3), and a
-- missed evaluation is unrecoverable. Kept forever, no pruning (user decision
-- 2026-11-23): ~37M rows/year.
-- evaluated_at_datetime_utc carries REAL non-zero seconds (e.g. 22:47:12) and is
-- stored as received, never truncated. Note the column name:
-- evaluated_at_datetime_*, not evaluated_at_*.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.pjm_load_forecast_7day', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.pjm_load_forecast_7day
    (
        evaluated_at_datetime_utc        DATETIME2(0)  NOT NULL,  -- KEY; evaluation time, UTC, real seconds
        evaluated_at_datetime_ept        DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        forecast_datetime_beginning_utc  DATETIME2(0)  NOT NULL,  -- KEY; hourly target start, UTC
        forecast_datetime_beginning_ept  DATETIME2(0)  NOT NULL,  -- repeats on fall-back; never a key
        forecast_datetime_ending_utc     DATETIME2(0)  NOT NULL,  -- beginning + 60 min
        forecast_datetime_ending_ept     DATETIME2(0)  NOT NULL,
        forecast_area                    VARCHAR(50)   NOT NULL,  -- KEY; 25 values
        forecast_load_mw                 DECIMAL(18,4) NULL,
        CreatedAtUtc                     DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_forecast_7day_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc                    DATETIME2(3)  NOT NULL CONSTRAINT DF_pjm_load_forecast_7day_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_pjm_load_forecast_7day PRIMARY KEY CLUSTERED (evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area)
    );
END
GO

SET NOEXEC OFF;
GO
