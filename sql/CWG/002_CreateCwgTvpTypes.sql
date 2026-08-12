-- =============================================================================
-- 002_CreateCwgTvpTypes.sql
-- Table-valued parameter types for the 15 CWG bulk merges (schema [arm]).
--
-- Column ORDER here is the C# sink contract: FileLogId is ALWAYS the first
-- column, then the target table's data columns in the SAME ORDER as 001. The
-- sink's BuildTable mirrors this order exactly — do NOT reorder either side
-- without updating the other (a load-bearing contract).
--
-- The TVPs intentionally do NOT carry Id / DateCreated / ModifiedAtUtc: those are
-- populated by the target table's IDENTITY / DEFAULTs. There is no PK on the type
-- — the merge procs (003) de-dup the batch on the merge key before MERGE, so a
-- same-file duplicate natural key cannot break the MERGE.
--
-- NULL-ability mirrors the target columns: fact measures are NOT NULL except the
-- hourly ActualMw and the capacity Change-block TotalCapacityMw.
--
-- Run 001 first. This script assumes CWG is the current database.
-- =============================================================================

USE CWG;
GO

-- ----------------------------------------------------------------------------
-- arm.CityForecastTvp — feeds arm.usp_BulkMergeCityForecast.
-- Order: (FileLogId, Region, ProductionDate, ForecastDate, Station,
--         FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CityForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.CityForecastTvp AS TABLE
    (
        FileLogId      INT          NOT NULL,
        Region         VARCHAR(16)  NOT NULL,
        ProductionDate DATE         NOT NULL,
        ForecastDate   DATE         NOT NULL,
        Station        VARCHAR(8)   NOT NULL,
        FcstMin        DECIMAL(5,1) NOT NULL,
        FcstMax        DECIMAL(5,1) NOT NULL,
        FcstAvg        DECIMAL(5,1) NOT NULL,
        NormMin        DECIMAL(5,1) NOT NULL,
        NormMax        DECIMAL(5,1) NOT NULL,
        Hdd            SMALLINT     NOT NULL,
        Cdd            SMALLINT     NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.CityGasForecastTvp — feeds arm.usp_BulkMergeCityGasForecast.
-- Order: (FileLogId, ProductionDate, ForecastDate, Station,
--         FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd). No Region.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CityGasForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.CityGasForecastTvp AS TABLE
    (
        FileLogId      INT          NOT NULL,
        ProductionDate DATE         NOT NULL,
        ForecastDate   DATE         NOT NULL,
        Station        VARCHAR(8)   NOT NULL,
        FcstMin        DECIMAL(5,1) NOT NULL,
        FcstMax        DECIMAL(5,1) NOT NULL,
        FcstAvg        DECIMAL(5,1) NOT NULL,
        NormMin        DECIMAL(5,1) NOT NULL,
        NormMax        DECIMAL(5,1) NOT NULL,
        Hdd            SMALLINT     NOT NULL,
        Cdd            SMALLINT     NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.CityObservationTvp — feeds arm.usp_BulkMergeCityObservation.
-- Order: (FileLogId, Region, ObsDate, Station, MinTemp, MaxTemp, Hdd, Cdd).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CityObservationTvp') IS NULL
BEGIN
    CREATE TYPE arm.CityObservationTvp AS TABLE
    (
        FileLogId INT          NOT NULL,
        Region    VARCHAR(16)  NOT NULL,
        ObsDate   DATE         NOT NULL,
        Station   VARCHAR(8)   NOT NULL,
        MinTemp   DECIMAL(5,1) NOT NULL,
        MaxTemp   DECIMAL(5,1) NOT NULL,
        Hdd       SMALLINT     NOT NULL,
        Cdd       SMALLINT     NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.DailyNormalTvp — feeds arm.usp_BulkMergeDailyNormal.
-- Order: (FileLogId, MonthDay, Region, NormalMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.DailyNormalTvp') IS NULL
BEGIN
    CREATE TYPE arm.DailyNormalTvp AS TABLE
    (
        FileLogId INT           NOT NULL,
        MonthDay  CHAR(5)       NOT NULL,
        Region    VARCHAR(10)   NOT NULL,
        NormalMw  DECIMAL(12,4) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SolarForecastTvp — feeds arm.usp_BulkMergeSolarForecast.
-- Order: (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SolarForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.SolarForecastTvp AS TABLE
    (
        FileLogId    INT           NOT NULL,
        Region       VARCHAR(10)   NOT NULL,
        InitDate     DATE          NOT NULL,
        ForecastDate DATE          NOT NULL,
        HourLabel    VARCHAR(8)    NOT NULL,
        HourOfDay    TINYINT       NOT NULL,
        ValueMw      DECIMAL(12,4) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SolarForecastChangeTvp — feeds arm.usp_BulkMergeSolarForecastChange.
-- Order: (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ChangeMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SolarForecastChangeTvp') IS NULL
BEGIN
    CREATE TYPE arm.SolarForecastChangeTvp AS TABLE
    (
        FileLogId    INT           NOT NULL,
        Region       VARCHAR(10)   NOT NULL,
        InitDate     DATE          NOT NULL,
        ForecastDate DATE          NOT NULL,
        HourLabel    VARCHAR(8)    NOT NULL,
        HourOfDay    TINYINT       NOT NULL,
        ChangeMw     DECIMAL(12,4) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SolarHourlyTvp — feeds arm.usp_BulkMergeSolarHourly.
-- Order: (FileLogId, HourEndingUtc, Region, ActualMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SolarHourlyTvp') IS NULL
BEGIN
    CREATE TYPE arm.SolarHourlyTvp AS TABLE
    (
        FileLogId     INT           NOT NULL,
        HourEndingUtc DATETIME2(0)  NOT NULL,
        Region        VARCHAR(10)   NOT NULL,
        ActualMw      DECIMAL(12,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.NationalDegreeDaysTvp — feeds arm.usp_BulkMergeNationalDegreeDays.
-- Order: (FileLogId, RunDate, Dates, NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
--         PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
--         ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY, IsForecast).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.NationalDegreeDaysTvp') IS NULL
BEGIN
    CREATE TYPE arm.NationalDegreeDaysTvp AS TABLE
    (
        FileLogId    INT          NOT NULL,
        RunDate      DATE         NOT NULL,
        Dates        DATE         NOT NULL,
        NgHdd        DECIMAL(9,4) NOT NULL,
        NgHdd30y     DECIMAL(9,4) NOT NULL,
        NgHdd10y     DECIMAL(9,4) NOT NULL,
        NgHddLastY   DECIMAL(9,4) NOT NULL,
        PopCdd       DECIMAL(9,4) NOT NULL,
        PopCdd30y    DECIMAL(9,4) NOT NULL,
        PopCdd10y    DECIMAL(9,4) NOT NULL,
        PopCddLastY  DECIMAL(9,4) NOT NULL,
        ElecCdd      DECIMAL(9,4) NOT NULL,
        ElecCdd30y   DECIMAL(9,4) NOT NULL,
        ElecCdd10y   DECIMAL(9,4) NOT NULL,
        ElecCddLastY DECIMAL(9,4) NOT NULL,
        IsForecast   BIT          NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.WindForecastTvp — feeds arm.usp_BulkMergeWindForecast.
-- Order: (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindForecastTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindForecastTvp AS TABLE
    (
        FileLogId    INT           NOT NULL,
        Region       VARCHAR(10)   NOT NULL,
        InitDate     DATE          NOT NULL,
        ForecastDate DATE          NOT NULL,
        HourLabel    VARCHAR(8)    NOT NULL,
        HourOfDay    TINYINT       NOT NULL,
        ValueMw      DECIMAL(12,4) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.WindForecastSubRegionTvp — feeds arm.usp_BulkMergeWindForecastSubRegion.
-- Order: (FileLogId, Region, SubRegion, InitDate, ForecastDate,
--         HourLabel, HourOfDay, ValueMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindForecastSubRegionTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindForecastSubRegionTvp AS TABLE
    (
        FileLogId    INT           NOT NULL,
        Region       VARCHAR(10)   NOT NULL,
        SubRegion    VARCHAR(40)   NOT NULL,
        InitDate     DATE          NOT NULL,
        ForecastDate DATE          NOT NULL,
        HourLabel    VARCHAR(8)    NOT NULL,
        HourOfDay    TINYINT       NOT NULL,
        ValueMw      DECIMAL(12,4) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.WindHourlyTvp — feeds arm.usp_BulkMergeWindHourly.
-- Order: (FileLogId, HourEndingUtc, Region, ActualMw).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindHourlyTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindHourlyTvp AS TABLE
    (
        FileLogId     INT           NOT NULL,
        HourEndingUtc DATETIME2(0)  NOT NULL,
        Region        VARCHAR(10)   NOT NULL,
        ActualMw      DECIMAL(12,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.WindTotalCapacityClimatologyTvp — feeds arm.usp_BulkMergeWindTotalCapacityClimatology.
-- Order: (FileLogId, ProductionDate, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15).
-- No Block column (single block).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindTotalCapacityClimatologyTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindTotalCapacityClimatologyTvp AS TABLE
    (
        FileLogId       INT           NOT NULL,
        ProductionDate  DATE          NOT NULL,
        Region          VARCHAR(10)   NOT NULL,
        TotalCapacityMw DECIMAL(12,4) NOT NULL,
        Avg_1_5         DECIMAL(6,2)  NOT NULL,
        Avg_6_10        DECIMAL(6,2)  NOT NULL,
        Avg_11_15       DECIMAL(6,2)  NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.WindTotalCapacityMWTvp — feeds arm.usp_BulkMergeWindTotalCapacityMW.
-- Order: (FileLogId, ProductionDate, Block, Region, TotalCapacityMw,
--         Avg_1_5, Avg_6_10, Avg_11_15). Avg columns are MW.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindTotalCapacityMWTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindTotalCapacityMWTvp AS TABLE
    (
        FileLogId       INT           NOT NULL,
        ProductionDate  DATE          NOT NULL,
        Block           VARCHAR(12)   NOT NULL,
        Region          VARCHAR(10)   NOT NULL,
        TotalCapacityMw DECIMAL(12,4) NULL,
        Avg_1_5         DECIMAL(12,4) NOT NULL,
        Avg_6_10        DECIMAL(12,4) NOT NULL,
        Avg_11_15       DECIMAL(12,4) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.WindTotalCapacityPctTvp — feeds arm.usp_BulkMergeWindTotalCapacityPct.
-- Order: (FileLogId, ProductionDate, Block, Region, TotalCapacityMw,
--         Avg_1_5, Avg_6_10, Avg_11_15). Avg columns are % (DECIMAL(6,2)).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.WindTotalCapacityPctTvp') IS NULL
BEGIN
    CREATE TYPE arm.WindTotalCapacityPctTvp AS TABLE
    (
        FileLogId       INT           NOT NULL,
        ProductionDate  DATE          NOT NULL,
        Block           VARCHAR(12)   NOT NULL,
        Region          VARCHAR(10)   NOT NULL,
        TotalCapacityMw DECIMAL(12,4) NULL,
        Avg_1_5         DECIMAL(6,2)  NOT NULL,
        Avg_6_10        DECIMAL(6,2)  NOT NULL,
        Avg_11_15       DECIMAL(6,2)  NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.StationTvp — feeds arm.usp_BulkMergeStation.
-- Order: (FileLogId, Region, Identifier, WmoId, Wban, Ghcnd, Lat, Lon,
--         Name, State, Country).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.StationTvp') IS NULL
BEGIN
    CREATE TYPE arm.StationTvp AS TABLE
    (
        FileLogId  INT          NOT NULL,
        Region     VARCHAR(16)  NOT NULL,
        Identifier VARCHAR(8)   NOT NULL,
        WmoId      VARCHAR(11)  NULL,
        Wban       VARCHAR(11)  NULL,
        Ghcnd      VARCHAR(16)  NULL,
        Lat        DECIMAL(9,6) NULL,
        Lon        DECIMAL(9,6) NULL,
        [Name]     NVARCHAR(64) NULL,
        [State]    VARCHAR(8)   NULL,
        Country    VARCHAR(4)   NULL
    );
END
GO
