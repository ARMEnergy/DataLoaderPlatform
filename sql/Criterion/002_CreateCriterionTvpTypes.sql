-- =============================================================================
-- 002_CreateCriterionTvpTypes.sql
-- Database : Criterion
-- Schema   : arm
--
-- One table type per target table (9).
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in src/DataLoader.Criterion/CriterionDescriptors.cs
-- EXACTLY; a silent reorder corrupts every loaded row without raising an error.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.Criterion.Tests/CriterionTvpContractTests.cs parses THIS
-- FILE, pulls each CREATE TYPE body out, and asserts name + order + type
-- against the descriptors. Editing a type here without editing the descriptor
-- (or the other way round) fails the test suite.
--
-- Conventions:
--   * TVP column order = target table column order.
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * Columns with NO SOURCE COLUMN are NEVER TVP columns, so the merge cannot
--     overwrite an ARM-curated value. Three of them:
--         arm.Financial_Metadata.Enabled
--         arm.Pipelines_NominationPoint.IsLatest
--         arm.Pipelines_Metadata.MappingId       (IDENTITY)
--     A fourth, arm.Pipelines_Metadata.Point, is DERIVED in the proc from the
--     Latitude/Longitude TVP columns rather than sent over the wire -- SqlClient
--     cannot bind a GEOGRAPHY into a TVP from a DataTable without the spatial
--     types assembly, and the point is trivially reconstructible server-side.
--     arm.Pipelines_Pointflows.Id is IDENTITY and is likewise absent.
--   * NOT NULL marks a column the reader treats as REQUIRED: a source row
--     missing it is DROPPED rather than merged under a blank key. Every primary
--     key component is required.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.FinancialMetadataTvp -> arm.Financial_Metadata
-- 19 columns: the table's 21 less Enabled (no source) and ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FinancialMetadataTvp') IS NULL
BEGIN
    CREATE TYPE arm.FinancialMetadataTvp AS TABLE
    (
        MetadataId   UNIQUEIDENTIFIER NOT NULL,
        EntityName   VARCHAR(100)     NULL,
        MetadataDesc VARCHAR(100)     NULL,
        CmdtyClass   VARCHAR(50)      NULL,
        SubCmdtyDesc VARCHAR(150)     NULL,
        AssetName    VARCHAR(150)     NULL,
        RegionName   VARCHAR(200)     NULL,
        CountryName  VARCHAR(50)      NULL,
        StateName    VARCHAR(50)      NULL,
        ProvinceName VARCHAR(100)     NULL,
        MongoId      VARCHAR(24)      NULL,
        [Status]     BIT              NULL,
        SeriesDesc   VARCHAR(150)     NULL,
        SeriesId     VARCHAR(50)      NULL,
        TableName    VARCHAR(100)     NULL,
        EntityId     VARCHAR(24)      NULL,
        SeriesType   VARCHAR(50)      NULL,
        SubRegion    VARCHAR(100)     NULL,
        Ticker       VARCHAR(100)     NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FinancialSeriesTvp -> arm.Financial_Series
-- 11 columns: the table's 12 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FinancialSeriesTvp') IS NULL
BEGIN
    CREATE TYPE arm.FinancialSeriesTvp AS TABLE
    (
        FinancialJsonId  UNIQUEIDENTIFIER NOT NULL,
        MetadataId       UNIQUEIDENTIFIER NULL,
        PostDate         DATE             NULL,
        ForecastDate     DATE             NULL,
        LoadDate         DATETIME2(7)     NULL,
        Filename         VARCHAR(255)     NULL,
        [Version]        INT              NULL,
        PeriodId         VARCHAR(24)      NULL,
        UnitId           VARCHAR(24)      NULL,
        Active           BIT              NULL,
        ForecastDateTime DATETIME2(7)     NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FinancialSeriesDataTvp -> arm.Financial_SeriesData
-- 3 columns. This is the hot path: hundreds of thousands of rows per TVP call,
-- so it is kept as narrow as the table allows.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FinancialSeriesDataTvp') IS NULL
BEGIN
    CREATE TYPE arm.FinancialSeriesDataTvp AS TABLE
    (
        FinancialJsonId UNIQUEIDENTIFIER NOT NULL,
        [Date]          DATE             NOT NULL,
        [Value]         FLOAT            NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.MiscPeriodTvp -> arm.Misc_Period
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.MiscPeriodTvp') IS NULL
BEGIN
    CREATE TYPE arm.MiscPeriodTvp AS TABLE
    (
        PeriodId    UNIQUEIDENTIFIER NOT NULL,
        MongoId     VARCHAR(24)      NULL,
        PeriodDesc  VARCHAR(75)      NULL,
        PeriodShort VARCHAR(20)      NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.MiscUnitTvp -> arm.Misc_Unit
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.MiscUnitTvp') IS NULL
BEGIN
    CREATE TYPE arm.MiscUnitTvp AS TABLE
    (
        UnitId   UNIQUEIDENTIFIER NOT NULL,
        MongoId  VARCHAR(24)      NULL,
        UnitDesc VARCHAR(50)      NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PipelinesMetadataTvp -> arm.Pipelines_Metadata
--
-- 37 columns: the table's 40 less Point (derived in the proc from Latitude and
-- Longitude), MappingId (IDENTITY) and ModifiedAtUtc.
--
-- Latitude/Longitude keep the source's exact DECIMAL(13,10). Widening them
-- would silently round coordinates.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PipelinesMetadataTvp') IS NULL
BEGIN
    CREATE TYPE arm.PipelinesMetadataTvp AS TABLE
    (
        MetadataId                     VARCHAR(30)    NOT NULL,
        AssetId                        VARCHAR(24)    NULL,
        Tsp                            VARCHAR(15)    NULL,
        PipelineName                   VARCHAR(50)    NULL,
        LocName                        VARCHAR(100)   NULL,
        Loc                            VARCHAR(15)    NULL,
        RecDelSign                     SMALLINT       NULL,
        LocQtiId                       SMALLINT       NULL,
        LocQtiShort                    CHAR(3)        NULL,
        CategoryShort                  VARCHAR(25)    NULL,
        SubCategoryDesc                VARCHAR(50)    NULL,
        SubCategory2Desc               VARCHAR(100)   NULL,
        CountryName                    VARCHAR(50)    NULL,
        StateName                      VARCHAR(50)    NULL,
        CountyName                     VARCHAR(100)   NULL,
        OffshoreBlockName              VARCHAR(50)    NULL,
        ConnectingPipeline             VARCHAR(50)    NULL,
        ConnectingEntity               VARCHAR(100)   NULL,
        StorageName                    VARCHAR(100)   NULL,
        StorageCalcFlag                CHAR(1)        NULL,
        Latitude                       DECIMAL(13,10) NULL,
        Longitude                      DECIMAL(13,10) NULL,
        UpdateDate                     DATETIME2(7)   NULL,
        TspShort                       CHAR(3)        NULL,
        LocPurpDesc                    VARCHAR(50)    NULL,
        StateAbb                       VARCHAR(7)     NULL,
        FercPipelineId                 VARCHAR(7)     NULL,
        FercPointId                    VARCHAR(17)    NULL,
        TransportationMaxDailyQuantity INT            NULL,
        StorageMaxDailyQuantity        INT            NULL,
        LocZone                        VARCHAR(25)    NULL,
        UpdnLoc                        VARCHAR(MAX)   NULL,
        BasinName                      VARCHAR(50)    NULL,
        Units                          VARCHAR(30)    NULL,
        PointType                      VARCHAR(75)    NULL,
        ProvinceName                   VARCHAR(100)   NULL,
        Ticker                         VARCHAR(100)   NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PipelinesNominationPointTvp -> arm.Pipelines_NominationPoint
-- 13 columns: the table's 15 less ModifiedAtUtc and IsLatest (no source).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PipelinesNominationPointTvp') IS NULL
BEGIN
    CREATE TYPE arm.PipelinesNominationPointTvp AS TABLE
    (
        MetadataId             VARCHAR(30)  NOT NULL,
        EffGasDay              DATE         NOT NULL,
        CycleId                SMALLINT     NOT NULL,
        HourlyCycleId          INT          NOT NULL,
        EndEffGasDay           DATE         NULL,
        TspShort               CHAR(3)      NULL,
        CycleDesc              VARCHAR(30)  NULL,
        DesignCapacity         FLOAT        NULL,
        OperatingCapacity      FLOAT        NULL,
        ScheduledQuantity      FLOAT        NULL,
        OperationallyAvailable FLOAT        NULL,
        Tbl                    VARCHAR(10)  NULL,
        Ticker                 VARCHAR(100) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PipelinesPointflowsTvp -> arm.Pipelines_Pointflows
-- 13 columns: the table's 15 less Id (IDENTITY) and ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PipelinesPointflowsTvp') IS NULL
BEGIN
    CREATE TYPE arm.PipelinesPointflowsTvp AS TABLE
    (
        MetadataId        VARCHAR(30)  NOT NULL,
        EffGasDay         DATE         NOT NULL,
        CycleId           SMALLINT     NOT NULL,
        CycleDesc         VARCHAR(30)  NULL,
        PipelineName      VARCHAR(50)  NULL,
        LocName           VARCHAR(100) NULL,
        CategoryShort     VARCHAR(25)  NULL,
        LocZone           VARCHAR(25)  NULL,
        LocPurpDesc       VARCHAR(50)  NULL,
        StateName         VARCHAR(50)  NULL,
        StateAbb          VARCHAR(7)   NULL,
        CountryName       VARCHAR(50)  NULL,
        ScheduledQuantity FLOAT        NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PipelinesRegionTvp -> arm.Pipelines_Region
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PipelinesRegionTvp') IS NULL
BEGIN
    CREATE TYPE arm.PipelinesRegionTvp AS TABLE
    (
        RegionId         VARCHAR(24) NOT NULL,
        StateId          CHAR(24)    NOT NULL,
        RegionName       VARCHAR(30) NULL,
        StateAbb         VARCHAR(5)  NULL,
        StateName        VARCHAR(50) NULL,
        EIA_NG_Regions   VARCHAR(30) NULL,
        EIA_PADD_Regions VARCHAR(10) NULL
    );
END
GO
