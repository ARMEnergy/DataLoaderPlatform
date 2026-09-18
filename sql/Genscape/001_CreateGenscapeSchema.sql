-- =============================================================================
-- 001_CreateGenscapeSchema.sql
-- Database : Genscape         (create/select it before running this script)
-- Schema   : arm
--
-- Genscape (Wood Mackenzie) Oil Fundamentals API v1
-- (https://api.genscape.com/oil-fundamentals/v1, Gen-Api-Key header).
-- Source contract verified live 2026-09-08 -- see docs/apis/Genscape.md.
-- Loader design -- see docs/design/Genscape.md.
--
-- 2 pipelines land in the 2 tables below, one each:
--
--   GET crude-storage/weekly        -> arm.OilFundamentals_CrudeStorage_Weekly
--   GET crude-transportation/weekly -> arm.OilFundamentals_CrudeTransportation_Weekly
--
-- ---------------------------------------------------------------------------
-- THREE THINGS TO KNOW BEFORE READING THE DDL
-- ---------------------------------------------------------------------------
--
--   1. Year and Week are DERIVED BY THE LOADER, not taken from the API.
--      The crude-storage endpoint returns a WEEK-OF-MONTH in its "week" field
--      (2026-08-28 -> 4, 2025-12-05 -> 1), which is not what the column means.
--      The loader overwrites both with the calendar week-of-year and the
--      calendar year of ReportDate -- the value SQL Server's
--      DATEPART(week, ReportDate) produces under the us_english default
--      DATEFIRST of 7. It is computed in C# (GenscapeTime.WeekOfYear) rather
--      than in SQL precisely BECAUSE DATEPART(week) is DATEFIRST-dependent:
--      Week is part of the primary key, so a session whose login language set
--      DATEFIRST to 1 would fork every key. The crude-transportation endpoint
--      already returns exactly this value, which is how the derivation was
--      validated -- see docs/apis/Genscape.md 4.
--
--   2. Region here is the RESPONSE region, not the request region. The API is
--      queried with region=NorthAmerica and region=GulfCoast, but each row
--      carries its own finer-grained region ('Cushing', 'Houston',
--      'Canada to PADD 2'). The two request regions OVERLAP -- 'Louisiana Gulf
--      Coast' and 'West Texas' come back from both -- so the same primary key
--      is written by two different work units. Verified live: the overlapping
--      rows are value-identical (0 disagreements over 33 shared storage keys
--      and 44 shared transportation keys), so merge order does not matter.
--
--   3. TINYINT is wide enough for Week. DATEPART(week) maxes out at 54, which
--      is reached only in a leap year beginning on a Saturday (e.g. 2028).
--
-- The supplied DDL is reproduced VERBATIM -- column names, types, nullability,
-- the primary key column ORDER (note that transportation keys Type BEFORE
-- Region, unlike the column order) and the ModifiedAtUtc default. The only
-- additions are a NAME for that default constraint -- an unnamed default gets a
-- random system name that cannot later be dropped by name -- and one
-- nonclustered index per table.
--
-- One oddity is kept ON PURPOSE rather than silently "fixed":
-- ModifiedAtUtc DEFAULT (sysdatetime()) is server LOCAL time in a column named
-- ...Utc. Every row this loader writes gets SYSUTCDATETIME() set explicitly by
-- the merge procs, so the DEFAULT only affects rows inserted by something else.
-- (Same posture as sql/ICE/001 and sql/Criterion/001.)
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- ----------------------------------------------------------------------------
-- arm.OilFundamentals_CrudeStorage_Weekly
--   <- GET /oil-fundamentals/v1/crude-storage/weekly
--
-- Weekly crude and diluent stocks by region, product and field type, with tank
-- capacity utilisation. Report dates are Fridays. Live history reaches back to
-- at least 2010-01-01; a 31-day window yields ~27 rows per request region.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.OilFundamentals_CrudeStorage_Weekly', 'U') IS NULL
BEGIN
    CREATE TABLE arm.OilFundamentals_CrudeStorage_Weekly
    (
        ReportDate          DATE         NOT NULL,
        [Year]              SMALLINT     NOT NULL,
        [Week]              TINYINT      NOT NULL,
        Region              VARCHAR(50)  NOT NULL,
        Product             VARCHAR(50)  NOT NULL,
        StorageFieldType    VARCHAR(50)  NOT NULL,
        StorageAmount       FLOAT        NULL,
        CapacityUtilization FLOAT        NULL,
        ModifiedAtUtc       DATETIME2(3) NULL
            CONSTRAINT DF_GEN_OilFundamentals_CrudeStorage_Weekly_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_OilFundamentals_CrudeStorage_Weekly PRIMARY KEY CLUSTERED
        (
            ReportDate       ASC,
            [Year]           ASC,
            [Week]           ASC,
            Region           ASC,
            Product          ASC,
            StorageFieldType ASC
        )
    );
END
GO

-- The clustered PK already serves date-range scans (ReportDate leads). This
-- covers the other common slice -- one region's history for one product.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_OilFund_CrudeStorage_Region' AND object_id = OBJECT_ID('arm.OilFundamentals_CrudeStorage_Weekly'))
    CREATE NONCLUSTERED INDEX IX_ARM_OilFund_CrudeStorage_Region
        ON arm.OilFundamentals_CrudeStorage_Weekly (Region, Product, ReportDate)
        INCLUDE (StorageAmount, CapacityUtilization);
GO

-- ----------------------------------------------------------------------------
-- arm.OilFundamentals_CrudeTransportation_Weekly
--   <- GET /oil-fundamentals/v1/crude-transportation/weekly
--
-- Weekly crude movements in barrels per day by corridor and transport mode
-- (Pipeline / Rail). Region here names a CORRIDOR ('Cushing to Gulf Coast',
-- 'Into Houston') rather than a place.
--
-- NOTE the primary key column order: (ReportDate, Year, Week, Type, Region).
-- Type precedes Region, which is NOT the column declaration order. That is what
-- the supplied DDL asks for and it is reproduced as-is; the merge below matches
-- on the same five-column set, so the ordering is a physical-layout choice only.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.OilFundamentals_CrudeTransportation_Weekly', 'U') IS NULL
BEGIN
    CREATE TABLE arm.OilFundamentals_CrudeTransportation_Weekly
    (
        ReportDate    DATE         NOT NULL,
        [Year]        SMALLINT     NOT NULL,
        [Week]        TINYINT      NOT NULL,
        Region        VARCHAR(50)  NOT NULL,
        [Type]        VARCHAR(50)  NOT NULL,
        FlowBPD       FLOAT        NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_GEN_OilFundamentals_CrudeTransportation_Weekly_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_OilFundamentals_CrudeTransportation_Weekly PRIMARY KEY CLUSTERED
        (
            ReportDate ASC,
            [Year]     ASC,
            [Week]     ASC,
            [Type]     ASC,
            Region     ASC
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_OilFund_CrudeTransportation_Region' AND object_id = OBJECT_ID('arm.OilFundamentals_CrudeTransportation_Weekly'))
    CREATE NONCLUSTERED INDEX IX_ARM_OilFund_CrudeTransportation_Region
        ON arm.OilFundamentals_CrudeTransportation_Weekly (Region, [Type], ReportDate)
        INCLUDE (FlowBPD);
GO
