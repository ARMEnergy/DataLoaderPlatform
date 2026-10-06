-- =============================================================================
-- 002_CreateCwgTvpTypes.sql
-- Table-valued parameter types for the 19 CWG bulk merges (schema [arm]).
--
-- Column ORDER here is the C# sink contract: the target table's key columns
-- first, then its value columns, in the SAME ORDER as 001. Each sink's
-- BuildTable mirrors this order exactly, by NAME + ORDER + TYPE — do NOT reorder
-- either side without updating the other (a load-bearing contract).
--
-- The TVPs intentionally do NOT carry RecordHash or ModifiedAtUtc. The merge
-- proc (003) COMPUTES RecordHash from the row's non-key value columns and sets
-- ModifiedAtUtc, so the hash definition lives in exactly one place and a sink
-- cannot drift from it. arm.Station's Point (derived from Latitude/Longitude in
-- the proc) and MappingId (IDENTITY) are absent for the same reason.
--
-- There is no PK on any type — the merge procs de-dup the batch on the merge key
-- before MERGE, so a same-file duplicate natural key cannot break the MERGE.
--
-- NULL-ability mirrors the target columns: key columns NOT NULL, measures NULL.
--
-- Run 001 first. This script assumes CWG is the current database.
--
-- NOTE: a TVP type cannot be ALTERed and cannot be dropped while a procedure
-- references it. To change one, drop the referencing arm.usp_BulkMerge* proc,
-- drop the type, then re-run 002 and 003.
-- =============================================================================

USE CWG;
GO

-- ----------------------------------------------------------------------------
-- 1. arm.CityForecastTvp → arm.usp_BulkMergeCityForecast.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CityForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.CityForecastTvp AS TABLE
    (
        ProductionDate DATE         NOT NULL,
        [Date]         DATE         NOT NULL,
        Station        VARCHAR(50)  NOT NULL,
        ForecastMin    DECIMAL(9,4) NULL,
        ForecastMax    DECIMAL(9,4) NULL,
        ForecastAvg    DECIMAL(9,4) NULL,
        NormMin        DECIMAL(9,4) NULL,
        NormMax        DECIMAL(9,4) NULL,
        HDD            DECIMAL(9,4) NULL,
        CDD            DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 2. arm.CityGasForecastTvp → arm.usp_BulkMergeCityGasForecast.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CityGasForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.CityGasForecastTvp AS TABLE
    (
        ProductionDate DATE         NOT NULL,
        [Date]         DATE         NOT NULL,
        Station        VARCHAR(50)  NOT NULL,
        ForecastMin    DECIMAL(9,4) NULL,
        ForecastMax    DECIMAL(9,4) NULL,
        ForecastAvg    DECIMAL(9,4) NULL,
        NormMin        DECIMAL(9,4) NULL,
        NormMax        DECIMAL(9,4) NULL,
        HDD            DECIMAL(9,4) NULL,
        CDD            DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 3. arm.CityObservationTvp → arm.usp_BulkMergeCityObservation.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CityObservationTvp') IS NULL
BEGIN
    CREATE TYPE arm.CityObservationTvp AS TABLE
    (
        [Date]  DATE         NOT NULL,
        Station VARCHAR(50)  NOT NULL,
        MinTemp DECIMAL(9,4) NULL,
        MaxTemp DECIMAL(9,4) NULL,
        HDD     DECIMAL(9,4) NULL,
        CDD     DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 4. arm.DailyNormalTvp → arm.usp_BulkMergeDailyNormal. WIDE (one column per
--    written region); NW/SW from the source file are not carried.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.DailyNormalTvp') IS NULL
BEGIN
    CREATE TYPE arm.DailyNormalTvp AS TABLE
    (
        [Month] TINYINT      NOT NULL,
        [Day]   TINYINT      NOT NULL,
        CAISO   DECIMAL(9,4) NULL,
        SPP     DECIMAL(9,4) NULL,
        ERCOT   DECIMAL(9,4) NULL,
        MISO    DECIMAL(9,4) NULL,
        PJM     DECIMAL(9,4) NULL,
        NEPOOL  DECIMAL(9,4) NULL,
        NYISO   DECIMAL(9,4) NULL,
        BPA     DECIMAL(9,4) NULL,
        IESO    DECIMAL(9,4) NULL,
        AESO    DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 5. arm.SolarForecastTvp → arm.usp_BulkMergeSolarForecast.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SolarForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.SolarForecastTvp AS TABLE
    (
        ForecastDate  DATE         NOT NULL,
        UTCHourEnding DATETIME2(0) NOT NULL,
        Region        VARCHAR(50)  NOT NULL,
        ForecastLabel DATE         NULL,
        HourLabel     VARCHAR(8)   NULL,
        [Value]       DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 6. arm.SolarForecastChangeTvp → arm.usp_BulkMergeSolarForecastChange.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SolarForecastChangeTvp') IS NULL
BEGIN
    CREATE TYPE arm.SolarForecastChangeTvp AS TABLE
    (
        ForecastDate  DATE         NOT NULL,
        UTCHourEnding DATETIME2(0) NOT NULL,
        Region        VARCHAR(50)  NOT NULL,
        ForecastLabel DATE         NULL,
        HourLabel     VARCHAR(8)   NULL,
        [Value]       DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 7. arm.SolarHourlyTvp → arm.usp_BulkMergeSolarHourly. WIDE.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SolarHourlyTvp') IS NULL
BEGIN
    CREATE TYPE arm.SolarHourlyTvp AS TABLE
    (
        UTCHourEnding DATETIME2(0) NOT NULL,
        ERCOT         DECIMAL(9,4) NULL,
        CAISO         DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 8. arm.StateDegreeDayObservationTvp → arm.usp_BulkMergeStateDegreeDayObservation.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.StateDegreeDayObservationTvp') IS NULL
BEGIN
    CREATE TYPE arm.StateDegreeDayObservationTvp AS TABLE
    (
        ObservationDate DATE         NOT NULL,
        RegionName      VARCHAR(50)  NOT NULL,
        [State]         VARCHAR(50)  NOT NULL,
        POP_HDD         DECIMAL(9,4) NULL,
        [30Y_POP_HDD]   DECIMAL(9,4) NULL,
        [10Y_POP_HDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_HDD  DECIMAL(9,4) NULL,
        POP_CDD         DECIMAL(9,4) NULL,
        [30Y_POP_CDD]   DECIMAL(9,4) NULL,
        [10Y_POP_CDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_CDD  DECIMAL(9,4) NULL,
        IS_FORECAST     BIT          NULL,
        GAS_WEIGHT      DECIMAL(9,4) NULL,
        ELCT_WEIGHT     DECIMAL(9,4) NULL,
        POP_WEIGHT      DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 9. arm.StationTvp → arm.usp_BulkMergeStation.
--    No Point (derived in the proc from Latitude/Longitude) and no MappingId
--    (IDENTITY on the target).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.StationTvp') IS NULL
BEGIN
    CREATE TYPE arm.StationTvp AS TABLE
    (
        Identifier VARCHAR(50) NOT NULL,
        WMOID      VARCHAR(50) NULL,
        WBAN       VARCHAR(50) NULL,
        GHCND      VARCHAR(50) NULL,
        Latitude   FLOAT       NULL,
        Longitude  FLOAT       NULL,
        [Name]     VARCHAR(50) NULL,
        [State]    VARCHAR(50) NULL,
        Country    VARCHAR(50) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 10. arm.Regions5DegreeDaysTvp → arm.usp_BulkMergeRegions5DegreeDays.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.Regions5DegreeDaysTvp') IS NULL
BEGIN
    CREATE TYPE arm.Regions5DegreeDaysTvp AS TABLE
    (
        ObservationDate DATE         NOT NULL,
        [Date]          DATE         NOT NULL,
        Region          VARCHAR(50)  NOT NULL,
        NG_HDD          DECIMAL(9,4) NULL,
        [30Y_NG_HDD]    DECIMAL(9,4) NULL,
        [10Y_NG_HDD]    DECIMAL(9,4) NULL,
        LAST_Y_NG_HDD   DECIMAL(9,4) NULL,
        POP_CDD         DECIMAL(9,4) NULL,
        [30Y_POP_CDD]   DECIMAL(9,4) NULL,
        [10Y_POP_CDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_CDD  DECIMAL(9,4) NULL,
        ELEC_CDD        DECIMAL(9,4) NULL,
        [30Y_ELEC_CDD]  DECIMAL(9,4) NULL,
        [10Y_ELEC_CDD]  DECIMAL(9,4) NULL,
        LAST_Y_ELEC_CDD DECIMAL(9,4) NULL,
        IS_FORECAST     BIT          NULL,
        GAS_WEIGHT      DECIMAL(9,4) NULL,
        ELCT_WEIGHT     DECIMAL(9,4) NULL,
        POP_WEIGHT      DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 11. arm.Regions9DegreeDaysTvp → arm.usp_BulkMergeRegions9DegreeDays.
--     Identical layout to #10.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.Regions9DegreeDaysTvp') IS NULL
BEGIN
    CREATE TYPE arm.Regions9DegreeDaysTvp AS TABLE
    (
        ObservationDate DATE         NOT NULL,
        [Date]          DATE         NOT NULL,
        Region          VARCHAR(50)  NOT NULL,
        NG_HDD          DECIMAL(9,4) NULL,
        [30Y_NG_HDD]    DECIMAL(9,4) NULL,
        [10Y_NG_HDD]    DECIMAL(9,4) NULL,
        LAST_Y_NG_HDD   DECIMAL(9,4) NULL,
        POP_CDD         DECIMAL(9,4) NULL,
        [30Y_POP_CDD]   DECIMAL(9,4) NULL,
        [10Y_POP_CDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_CDD  DECIMAL(9,4) NULL,
        ELEC_CDD        DECIMAL(9,4) NULL,
        [30Y_ELEC_CDD]  DECIMAL(9,4) NULL,
        [10Y_ELEC_CDD]  DECIMAL(9,4) NULL,
        LAST_Y_ELEC_CDD DECIMAL(9,4) NULL,
        IS_FORECAST     BIT          NULL,
        GAS_WEIGHT      DECIMAL(9,4) NULL,
        ELCT_WEIGHT     DECIMAL(9,4) NULL,
        POP_WEIGHT      DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 12. arm.ISODegreeDaysTvp → arm.usp_BulkMergeISODegreeDays.
--     POP_HDD family (not NG_HDD); no ELEC family, no weights.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.ISODegreeDaysTvp') IS NULL
BEGIN
    CREATE TYPE arm.ISODegreeDaysTvp AS TABLE
    (
        ObservationDate DATE         NOT NULL,
        [Date]          DATE         NOT NULL,
        Region          VARCHAR(50)  NOT NULL,
        POP_HDD         DECIMAL(9,4) NULL,
        [30Y_POP_HDD]   DECIMAL(9,4) NULL,
        [10Y_POP_HDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_HDD  DECIMAL(9,4) NULL,
        POP_CDD         DECIMAL(9,4) NULL,
        [30Y_POP_CDD]   DECIMAL(9,4) NULL,
        [10Y_POP_CDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_CDD  DECIMAL(9,4) NULL,
        IS_FORECAST     BIT          NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 13. arm.NationalDegreeDaysTvp → arm.usp_BulkMergeNationalDegreeDays.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.NationalDegreeDaysTvp') IS NULL
BEGIN
    CREATE TYPE arm.NationalDegreeDaysTvp AS TABLE
    (
        ObservationDate DATE         NOT NULL,
        [Date]          DATE         NOT NULL,
        NG_HDD          DECIMAL(9,4) NULL,
        [30Y_NG_HDD]    DECIMAL(9,4) NULL,
        [10Y_NG_HDD]    DECIMAL(9,4) NULL,
        LAST_Y_NG_HDD   DECIMAL(9,4) NULL,
        POP_CDD         DECIMAL(9,4) NULL,
        [30Y_POP_CDD]   DECIMAL(9,4) NULL,
        [10Y_POP_CDD]   DECIMAL(9,4) NULL,
        LAST_Y_POP_CDD  DECIMAL(9,4) NULL,
        ELEC_CDD        DECIMAL(9,4) NULL,
        [30Y_ELEC_CDD]  DECIMAL(9,4) NULL,
        [10Y_ELEC_CDD]  DECIMAL(9,4) NULL,
        LAST_Y_ELEC_CDD DECIMAL(9,4) NULL,
        IS_FORECAST     BIT          NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 14. arm.WindForecastTvp → arm.usp_BulkMergeWindForecast.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindForecastTvp AS TABLE
    (
        ForecastDate  DATE         NOT NULL,
        UTCHourEnding DATETIME2(0) NOT NULL,
        Region        VARCHAR(50)  NOT NULL,
        ForecastLabel DATE         NULL,
        HourLabel     VARCHAR(8)   NULL,
        [Value]       DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 15. arm.WindForecastSubRegionTvp → arm.usp_BulkMergeWindForecastSubRegion.
--     [Value] is DECIMAL(19,4) here (matches the target column).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindForecastSubRegionTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindForecastSubRegionTvp AS TABLE
    (
        ForecastDate  DATE          NOT NULL,
        UTCHourEnding DATETIME2(0)  NOT NULL,
        Region        VARCHAR(50)   NOT NULL,
        SubRegion     VARCHAR(50)   NOT NULL,
        ForecastLabel DATE          NULL,
        HourLabel     VARCHAR(8)    NULL,
        [Value]       DECIMAL(19,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 16. arm.WindHourlyTvp → arm.usp_BulkMergeWindHourly. WIDE.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindHourlyTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindHourlyTvp AS TABLE
    (
        UTCHourEnding DATETIME2(0) NOT NULL,
        CAISO         DECIMAL(9,4) NULL,
        SPP           DECIMAL(9,4) NULL,
        ERCOT         DECIMAL(9,4) NULL,
        MISO          DECIMAL(9,4) NULL,
        PJM           DECIMAL(9,4) NULL,
        NEPOOL        DECIMAL(9,4) NULL,
        NYISO         DECIMAL(9,4) NULL,
        BPA           DECIMAL(9,4) NULL,
        IESO          DECIMAL(9,4) NULL,
        AESO          DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 17. arm.WindTotalCapacityClimatologyTvp → arm.usp_BulkMergeWindTotalCapacityClimatology.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindTotalCapacityClimatologyTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindTotalCapacityClimatologyTvp AS TABLE
    (
        ForecastDate              DATE         NOT NULL,
        Region                    VARCHAR(50)  NOT NULL,
        TotalCapacity             DECIMAL(9,4) NULL,
        OneToFiveDayAverage       DECIMAL(9,4) NULL,
        SixToTenDayAverage        DECIMAL(9,4) NULL,
        ElevenToFifteenDayAverage DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 18. arm.WindTotalCapacityMWTvp → arm.usp_BulkMergeWindTotalCapacityMW.
--     Carries CURRENT-block rows only (the sink never emits the other blocks).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindTotalCapacityMWTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindTotalCapacityMWTvp AS TABLE
    (
        ForecastDate              DATE         NOT NULL,
        Region                    VARCHAR(50)  NOT NULL,
        TotalCapacity             DECIMAL(9,4) NULL,
        OneToFiveDayAverage       DECIMAL(9,4) NULL,
        SixToTenDayAverage        DECIMAL(9,4) NULL,
        ElevenToFifteenDayAverage DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- 19. arm.WindTotalCapacityPctTvp → arm.usp_BulkMergeWindTotalCapacityPct.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindTotalCapacityPctTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindTotalCapacityPctTvp AS TABLE
    (
        ForecastDate              DATE         NOT NULL,
        Region                    VARCHAR(50)  NOT NULL,
        TotalCapacity             DECIMAL(9,4) NULL,
        OneToFiveDayAverage       DECIMAL(9,4) NULL,
        SixToTenDayAverage        DECIMAL(9,4) NULL,
        ElevenToFifteenDayAverage DECIMAL(9,4) NULL
    );
END
GO
