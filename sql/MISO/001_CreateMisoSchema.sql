-- =============================================================================
-- 001_CreateMisoSchema.sql
-- Schema and target tables for the MISO (Data Exchange, LGI API) loader.
--   * schema miso                    (created here if missing)
--   * miso.miso_zonal_load_hourly    <- feed zonal_load_hourly   (hourly, 6 LRZ pivoted wide)
--   * miso.miso_load_hourly          <- feed load_hourly         (hourly, 3 regions pivoted wide)
--   * miso.miso_fuel_mix_hourly      <- feed fuel_mix_hourly     (hourly, one row per region)
--   * miso.miso_load_forecast_mtlf   <- feed load_forecast_mtlf  (per init date, hourly, one row per zone)
--
-- Target: Loaders:MISO:ConnectionString -> Database=Misc, schema [miso].
-- Spec:   docs/design/MISO.md §2 (binding: names, column ORDER, types, PKs).
--
-- `Misc` is an IT-assigned SHARED database and already holds PJM's LIVE tables
-- in [dbo]. This script NEVER creates the database and never touches anything
-- outside the [miso] schema. If IT has not created `Misc`, `USE Misc` fails and
-- the guard below turns every later batch into a no-op, which is the intended
-- stop. The four tables must be on the IT table request before deployment.
--
-- Re-runnable: the schema is IF SCHEMA_ID(...) IS NULL guarded and each table is
-- IF OBJECT_ID(...) IS NULL guarded. A guarded re-run does NOT alter an existing
-- table; a shape change needs a migration (or 999 on an empty schema) first.
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
    RAISERROR('001_CreateMisoSchema: current database is %s, not Misc. Nothing was created.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- Collation is chosen by IT. The C# duplicate check assumes a case-insensitive
-- collation with ANSI padding (the reader matches members case-insensitively).
DECLARE @Collation NVARCHAR(128) = CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(N'Misc', 'Collation'));
PRINT N'Misc collation: ' + ISNULL(@Collation, N'(unknown)');
GO

-- CREATE SCHEMA must be the only statement in its batch, hence dynamic SQL.
IF SCHEMA_ID(N'miso') IS NULL EXEC(N'CREATE SCHEMA miso AUTHORIZATION dbo');
GO

-- -----------------------------------------------------------------------------
-- Conventions (design §2, §3):
--   * Every timestamp is DATETIME2(0) NOT NULL. MISO's start/end are a FIXED
--     EST (UTC-5, no DST) wall clock: *_local = value as sent, *_utc = value + 5h.
--     Because EST never shifts, *_local does not repeat on a DST day either, but
--     keys still use *_utc only, as PJM does.
--   * Every measure is DECIMAL(18,4) NULL.
--   * Audit columns (table only, never in a TVP): CreatedAtUtc is set by its
--     default on INSERT and never updated; ModifiedAtUtc is refreshed by the
--     merge proc only when a value really changed. ModifiedAtUtc > CreatedAtUtc
--     on older rows answers "does MISO restate?" (design §12).
--   * Column order below is the TVP order (002), then the two audit columns.
-- -----------------------------------------------------------------------------

-- -----------------------------------------------------------------------------
-- miso.miso_zonal_load_hourly  (zonal_load_hourly)     design §2.1
-- PK (interval_start_utc). Same name and columns as GridStatusIO's table, minus
-- its Id/RecordDate/API_Info_Log_Id. `miso` = decimal sum of the six LRZ
-- columns, NULL if any member is NULL (computed by the loader).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'miso.miso_zonal_load_hourly', N'U') IS NULL
BEGIN
    CREATE TABLE miso.miso_zonal_load_hourly
    (
        interval_start_local  DATETIME2(0)  NOT NULL,  -- EST wall clock, as sent
        interval_start_utc    DATETIME2(0)  NOT NULL,  -- KEY; start + 5h
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        lrz1                  DECIMAL(18,4) NULL,
        lrz2_7                DECIMAL(18,4) NULL,
        lrz3_5                DECIMAL(18,4) NULL,
        lrz4                  DECIMAL(18,4) NULL,
        lrz6                  DECIMAL(18,4) NULL,
        lrz8_9_10             DECIMAL(18,4) NULL,
        miso                  DECIMAL(18,4) NULL,      -- sum of the six LRZ
        CreatedAtUtc          DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_zonal_load_hourly_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_zonal_load_hourly_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_miso_zonal_load_hourly PRIMARY KEY CLUSTERED (interval_start_utc)
    );
END
GO

-- -----------------------------------------------------------------------------
-- miso.miso_load_hourly  (load_hourly)                 design §2.2
-- PK (interval_start_utc). `miso` = decimal sum of north/central/south, NULL if
-- any member is NULL (computed by the loader).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'miso.miso_load_hourly', N'U') IS NULL
BEGIN
    CREATE TABLE miso.miso_load_hourly
    (
        interval_start_local  DATETIME2(0)  NOT NULL,  -- EST wall clock, as sent
        interval_start_utc    DATETIME2(0)  NOT NULL,  -- KEY; start + 5h
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        north                 DECIMAL(18,4) NULL,
        central               DECIMAL(18,4) NULL,
        south                 DECIMAL(18,4) NULL,
        miso                  DECIMAL(18,4) NULL,      -- sum of the three regions
        CreatedAtUtc          DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_load_hourly_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_load_hourly_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_miso_load_hourly PRIMARY KEY CLUSTERED (interval_start_utc)
    );
END
GO

-- -----------------------------------------------------------------------------
-- miso.miso_fuel_mix_hourly  (fuel_mix_hourly)         design §2.3
-- PK (interval_start_utc, region). region stored UPPER (NORTH/CENTRAL/SOUTH).
-- A fuel key the vendor omits is NULL; an unknown fuel key fails the unit.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'miso.miso_fuel_mix_hourly', N'U') IS NULL
BEGIN
    CREATE TABLE miso.miso_fuel_mix_hourly
    (
        interval_start_local  DATETIME2(0)  NOT NULL,  -- EST wall clock, as sent
        interval_start_utc    DATETIME2(0)  NOT NULL,  -- KEY; start + 5h
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        region                VARCHAR(20)   NOT NULL,  -- KEY; UPPER
        coal                  DECIMAL(18,4) NULL,
        gas                   DECIMAL(18,4) NULL,
        nuclear               DECIMAL(18,4) NULL,
        water                 DECIMAL(18,4) NULL,
        wind                  DECIMAL(18,4) NULL,
        solar                 DECIMAL(18,4) NULL,
        other                 DECIMAL(18,4) NULL,
        storage               DECIMAL(18,4) NULL,
        total_mw              DECIMAL(18,4) NULL,
        CreatedAtUtc          DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_fuel_mix_hourly_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_fuel_mix_hourly_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_miso_fuel_mix_hourly PRIMARY KEY CLUSTERED (interval_start_utc, region)
    );
END
GO

-- -----------------------------------------------------------------------------
-- miso.miso_load_forecast_mtlf  (load_forecast_mtlf)   design §2.4
-- PK (init_date, interval_start_utc, local_resource_zone). One forecast run per
-- init date; several init dates forecast the same hour, so init_date leads the
-- key. local_resource_zone stored as sent (Z1..Z10); region stored UPPER.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'miso.miso_load_forecast_mtlf', N'U') IS NULL
BEGIN
    CREATE TABLE miso.miso_load_forecast_mtlf
    (
        init_date             DATE          NOT NULL,  -- KEY; forecast init date (echo-checked)
        interval_start_local  DATETIME2(0)  NOT NULL,  -- EST wall clock, as sent
        interval_start_utc    DATETIME2(0)  NOT NULL,  -- KEY; start + 5h
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        region                VARCHAR(20)   NOT NULL,  -- UPPER
        local_resource_zone   VARCHAR(10)   NOT NULL,  -- KEY; Z1..Z10 as sent
        load_forecast         DECIMAL(18,4) NULL,
        CreatedAtUtc          DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_load_forecast_mtlf_CreatedAtUtc  DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_miso_load_forecast_mtlf_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_miso_load_forecast_mtlf PRIMARY KEY CLUSTERED (init_date, interval_start_utc, local_resource_zone)
    );
END
GO

SET NOEXEC OFF;
GO
