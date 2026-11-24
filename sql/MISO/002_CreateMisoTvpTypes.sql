-- =============================================================================
-- 002_CreateMisoTvpTypes.sql
-- Table-valued parameter types for the MISO loader's merge procs.
--   * miso.MisoZonalLoadHourlyType   -> miso.usp_MergeMisoZonalLoadHourly
--   * miso.MisoLoadHourlyType        -> miso.usp_MergeMisoLoadHourly
--   * miso.MisoFuelMixHourlyType     -> miso.usp_MergeMisoFuelMixHourly
--   * miso.MisoLoadForecastMtlfType  -> miso.usp_MergeMisoLoadForecastMtlf
--
-- Target: Loaders:MISO:ConnectionString -> Database=Misc, schema [miso].
-- Spec:   docs/design/MISO.md §2.
--
-- TVP CONTRACT: column NAME + ORDER + TYPE must match, exactly, the DataTable
-- built by each Miso*SqlSink.BuildTable, the 001 table bodies and the 003 MERGE
-- column lists. Source columns only: CreatedAtUtc and ModifiedAtUtc are never
-- TVP columns.
--
-- Re-runnable: IF TYPE_ID(...) IS NULL guarded. A guarded re-run does NOT change
-- an existing type. A shape change needs the procs dropped first (they bind the
-- type), i.e. 999 on an empty schema, then 001-003.
-- Requires the [miso] schema from 001.
--
-- PRODUCTION WRITE when run against ARMH-OPSDB01. Run by hand, by a person.
-- =============================================================================

USE Misc;
GO

DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'Misc'
BEGIN
    RAISERROR('002_CreateMisoTvpTypes: current database is %s, not Misc. Nothing was created.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- miso.MisoZonalLoadHourlyType: 11 columns, this exact order (design §2.1).
IF TYPE_ID(N'miso.MisoZonalLoadHourlyType') IS NULL
    CREATE TYPE miso.MisoZonalLoadHourlyType AS TABLE
    (
        interval_start_local  DATETIME2(0)  NOT NULL,
        interval_start_utc    DATETIME2(0)  NOT NULL,
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        lrz1                  DECIMAL(18,4) NULL,
        lrz2_7                DECIMAL(18,4) NULL,
        lrz3_5                DECIMAL(18,4) NULL,
        lrz4                  DECIMAL(18,4) NULL,
        lrz6                  DECIMAL(18,4) NULL,
        lrz8_9_10             DECIMAL(18,4) NULL,
        miso                  DECIMAL(18,4) NULL
    );
GO

-- miso.MisoLoadHourlyType: 8 columns, this exact order (design §2.2).
IF TYPE_ID(N'miso.MisoLoadHourlyType') IS NULL
    CREATE TYPE miso.MisoLoadHourlyType AS TABLE
    (
        interval_start_local  DATETIME2(0)  NOT NULL,
        interval_start_utc    DATETIME2(0)  NOT NULL,
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        north                 DECIMAL(18,4) NULL,
        central               DECIMAL(18,4) NULL,
        south                 DECIMAL(18,4) NULL,
        miso                  DECIMAL(18,4) NULL
    );
GO

-- miso.MisoFuelMixHourlyType: 14 columns, this exact order (design §2.3).
IF TYPE_ID(N'miso.MisoFuelMixHourlyType') IS NULL
    CREATE TYPE miso.MisoFuelMixHourlyType AS TABLE
    (
        interval_start_local  DATETIME2(0)  NOT NULL,
        interval_start_utc    DATETIME2(0)  NOT NULL,
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        region                VARCHAR(20)   NOT NULL,
        coal                  DECIMAL(18,4) NULL,
        gas                   DECIMAL(18,4) NULL,
        nuclear               DECIMAL(18,4) NULL,
        water                 DECIMAL(18,4) NULL,
        wind                  DECIMAL(18,4) NULL,
        solar                 DECIMAL(18,4) NULL,
        other                 DECIMAL(18,4) NULL,
        storage               DECIMAL(18,4) NULL,
        total_mw              DECIMAL(18,4) NULL
    );
GO

-- miso.MisoLoadForecastMtlfType: 8 columns, this exact order (design §2.4).
IF TYPE_ID(N'miso.MisoLoadForecastMtlfType') IS NULL
    CREATE TYPE miso.MisoLoadForecastMtlfType AS TABLE
    (
        init_date             DATE          NOT NULL,
        interval_start_local  DATETIME2(0)  NOT NULL,
        interval_start_utc    DATETIME2(0)  NOT NULL,
        interval_end_local    DATETIME2(0)  NOT NULL,
        interval_end_utc      DATETIME2(0)  NOT NULL,
        region                VARCHAR(20)   NOT NULL,
        local_resource_zone   VARCHAR(10)   NOT NULL,
        load_forecast         DECIMAL(18,4) NULL
    );
GO

SET NOEXEC OFF;
GO
