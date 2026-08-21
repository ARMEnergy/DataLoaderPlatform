-- =============================================================================
-- 002_CreateIirTvpTypes.sql
-- Table-valued parameter types for the IIR bulk merges (schema [arm]):
--   arm.PlantTvp               -> arm.usp_UpsertPlant
--   arm.UnitTvp                -> arm.usp_UpsertUnit
--   arm.OfflineEventTvp        -> arm.usp_UpsertOfflineEvent
--   arm.PlantSummaryTvp        -> arm.usp_UpsertPlantSummary          (id-catalog census)
--   arm.UnitSummaryTvp         -> arm.usp_UpsertUnitSummary           (id-catalog census)
--   arm.OfflineEventSummaryTvp -> arm.usp_UpsertOfflineEventSummary   (id-catalog census)
--
-- COLUMN ORDER here is the load-bearing C# sink contract: the sink's BuildTable,
-- this TVP type, and the 003 merge proc SELECT/INSERT lists must mirror each other
-- EXACTLY. Do NOT reorder one side without the others. The rules (design §7.1/§7.2):
--   * FileLogId INT is the FIRST column of every TVP (AGSI/IHS convention — the sink
--     stamps the FileLog row id). NOTE: unlike AGSI/IHS, the three IIR target tables
--     were supplied VERBATIM by the user and carry NO FileLogId column, so the merge
--     procs (003) do NOT persist it to a data row — it is carried in the TVP purely for
--     sink-contract uniformity; per-pull provenance lives only in arm.FileLog. Do NOT
--     add a FileLogId column to the target tables (the DDL is authoritative).
--   * Then the target table's columns in their DDL order, EXCEPT:
--       - PlantPoint (GEOGRAPHY)  -> OMITTED; built in-proc from the lat/long FLOATs
--         (003, §7.3). No geography value crosses the TVP.
--       - ModifiedAtUtc           -> OMITTED; stamped in-proc (SYSUTCDATETIME()).
--       - OfflineEvent.RunDate     -> OMITTED; passed as the @RunDate SCALAR proc param
--         (all rows in one call share it, part of the PK). EventId REMAINS (merge key).
--   * Latitude/Longitude are carried as plain FLOAT (Plant: Longitude/Latitude;
--     Unit & OfflineEvent: PlantLatitude/PlantLongitude) per the DDL — NO geography column.
--
-- NULL-ability mirrors the target table: the PK id column (PlantId/UnitId/EventId) and
-- FileLogId are NOT NULL; every other business column is NULL-able. There is no PK on
-- the type itself — the merge procs (003) de-dup the batch on the merge key before MERGE.
--
-- Run 001 first. This script assumes IIR is the current database.
-- =============================================================================

USE IIR;
GO

-- ----------------------------------------------------------------------------
-- arm.PlantTvp — feeds arm.usp_UpsertPlant. Order:
--   FileLogId, PlantId, PlantName, PlantStatusDesc, NoEmployees, StartupDate,
--   LiveDate, ReleaseDate, OperationsLaborPreference, PrimaryFuel, SecondaryFuel,
--   IndustryCode, IndustryCodeDesc, PrimarySicId, PrimarySicDesc, PecZone,
--   MarketRegionId, MarketRegionName, ConfirmationStatus, NercRegion,
--   NercSubRegionName, ElectricalConnectionName, TradingRegionId, TradingRegionName,
--   CogenChp, Metallurgical, Thermal, Placer, OpenPit, Quarry, Strip, Auger,
--   Dredging, Drift, Shaft, Slope, Longwall, RoomPillar, CutFill, Caving, Stoping,
--   InSituSolution, Longitude, Latitude, WorldRegionId, WorldRegionName, Offshore,
--   MailingAddressLine1, MailingCity, MailingStateName, MailingPostalCode,
--   MailingCountryName, PhysicalAddressLine1, PhysicalCity, PhysicalStateName,
--   PhysicalPostalCode, PhysicalCountryName, PhysicalCountyName, PhoneCC, PhoneNumber,
--   ParentCompanyId, ParentCompanyName, ParentCompanyWebsite, OperatorCompanyId,
--   OperatorCompanyName, OperatorCompanyWebsite
-- (PlantPoint + ModifiedAtUtc omitted; 66 columns = FileLogId + 65 business).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PlantTvp') IS NULL
BEGIN
    CREATE TYPE arm.PlantTvp AS TABLE
    (
        FileLogId                 INT           NOT NULL,
        PlantId                   INT           NOT NULL,
        PlantName                 VARCHAR(8000) NULL,
        PlantStatusDesc           VARCHAR(8000) NULL,
        NoEmployees               INT           NULL,
        StartupDate               DATETIME2(0)  NULL,
        LiveDate                  DATETIME2(0)  NULL,
        ReleaseDate               DATETIME2(0)  NULL,
        OperationsLaborPreference INT           NULL,
        PrimaryFuel               VARCHAR(8000) NULL,
        SecondaryFuel             VARCHAR(8000) NULL,
        IndustryCode              VARCHAR(8000) NULL,
        IndustryCodeDesc          VARCHAR(8000) NULL,
        PrimarySicId              VARCHAR(8000) NULL,
        PrimarySicDesc            VARCHAR(8000) NULL,
        PecZone                   VARCHAR(8000) NULL,
        MarketRegionId            VARCHAR(8000) NULL,
        MarketRegionName          VARCHAR(8000) NULL,
        ConfirmationStatus        VARCHAR(8000) NULL,
        NercRegion                VARCHAR(8000) NULL,
        NercSubRegionName         VARCHAR(8000) NULL,
        ElectricalConnectionName  VARCHAR(8000) NULL,
        TradingRegionId           INT           NULL,
        TradingRegionName         VARCHAR(8000) NULL,
        CogenChp                  INT           NULL,
        Metallurgical             INT           NULL,
        Thermal                   INT           NULL,
        Placer                    INT           NULL,
        OpenPit                   INT           NULL,
        Quarry                    INT           NULL,
        Strip                     INT           NULL,
        Auger                     INT           NULL,
        Dredging                  INT           NULL,
        Drift                     INT           NULL,
        Shaft                     INT           NULL,
        Slope                     INT           NULL,
        Longwall                  INT           NULL,
        RoomPillar                INT           NULL,
        CutFill                   INT           NULL,
        Caving                    INT           NULL,
        Stoping                   INT           NULL,
        InSituSolution            INT           NULL,
        Longitude                 FLOAT         NULL,
        Latitude                  FLOAT         NULL,
        WorldRegionId             INT           NULL,
        WorldRegionName           VARCHAR(8000) NULL,
        Offshore                  INT           NULL,
        MailingAddressLine1       VARCHAR(250)  NULL,
        MailingCity               VARCHAR(250)  NULL,
        MailingStateName          VARCHAR(250)  NULL,
        MailingPostalCode         VARCHAR(250)  NULL,
        MailingCountryName        VARCHAR(250)  NULL,
        PhysicalAddressLine1      VARCHAR(250)  NULL,
        PhysicalCity              VARCHAR(250)  NULL,
        PhysicalStateName         VARCHAR(250)  NULL,
        PhysicalPostalCode        VARCHAR(250)  NULL,
        PhysicalCountryName       VARCHAR(250)  NULL,
        PhysicalCountyName        VARCHAR(250)  NULL,
        PhoneCC                   VARCHAR(250)  NULL,
        PhoneNumber               VARCHAR(250)  NULL,
        ParentCompanyId           VARCHAR(250)  NULL,
        ParentCompanyName         VARCHAR(250)  NULL,
        ParentCompanyWebsite      VARCHAR(250)  NULL,
        OperatorCompanyId         VARCHAR(250)  NULL,
        OperatorCompanyName       VARCHAR(250)  NULL,
        OperatorCompanyWebsite    VARCHAR(250)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.UnitTvp — feeds arm.usp_UpsertUnit. Order:
--   FileLogId, UnitId, UnitName, PlantId, PlantName, PlantStatusDesc,
--   PlantAddressLine1, PlantCity, PlantStateName, PlantPostalCode, PlantCountryName,
--   PlantCountyName, MarketRegionId, MarketRegionName, WorldRegionId, WorldRegionName,
--   TradingRegionId, TradingRegionName, UnitStatusDesc, UnitStatusGroup, HeaterCount,
--   UnitTypeId, UnitTypeDesc, UnitTypeGroup, CapacityProductId, Capacity, CapacityUom,
--   PrimarySicId, PrimarySicDesc, AreaId, AreaName, PlantLatitude, PlantLongitude,
--   Offshore, IndustryCode, IndustryCodeDesc, Technology, Renewable, CogenChp,
--   PlantOperatorName, PlantOwnerName, PlantParentName, PlantPhone, ReleaseDate, LiveDate
-- (PlantPoint + ModifiedAtUtc omitted; 45 columns = FileLogId + 44 business).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.UnitTvp') IS NULL
BEGIN
    CREATE TYPE arm.UnitTvp AS TABLE
    (
        FileLogId         INT           NOT NULL,
        UnitId            INT           NOT NULL,
        UnitName          VARCHAR(8000) NULL,
        PlantId           INT           NULL,
        PlantName         VARCHAR(8000) NULL,
        PlantStatusDesc   VARCHAR(8000) NULL,
        PlantAddressLine1 VARCHAR(250)  NULL,
        PlantCity         VARCHAR(250)  NULL,
        PlantStateName    VARCHAR(250)  NULL,
        PlantPostalCode   VARCHAR(250)  NULL,
        PlantCountryName  VARCHAR(250)  NULL,
        PlantCountyName   VARCHAR(250)  NULL,
        MarketRegionId    VARCHAR(8000) NULL,
        MarketRegionName  VARCHAR(8000) NULL,
        WorldRegionId     INT           NULL,
        WorldRegionName   VARCHAR(8000) NULL,
        TradingRegionId   INT           NULL,
        TradingRegionName VARCHAR(8000) NULL,
        UnitStatusDesc    VARCHAR(8000) NULL,
        UnitStatusGroup   VARCHAR(8000) NULL,
        HeaterCount       INT           NULL,
        UnitTypeId        VARCHAR(8000) NULL,
        UnitTypeDesc      VARCHAR(8000) NULL,
        UnitTypeGroup     VARCHAR(8000) NULL,
        CapacityProductId VARCHAR(8000) NULL,
        Capacity          FLOAT         NULL,
        CapacityUom       VARCHAR(8000) NULL,
        PrimarySicId      VARCHAR(8000) NULL,
        PrimarySicDesc    VARCHAR(8000) NULL,
        AreaId            INT           NULL,
        AreaName          VARCHAR(8000) NULL,
        PlantLatitude     FLOAT         NULL,
        PlantLongitude    FLOAT         NULL,
        Offshore          INT           NULL,
        IndustryCode      VARCHAR(8000) NULL,
        IndustryCodeDesc  VARCHAR(8000) NULL,
        Technology        VARCHAR(8000) NULL,
        Renewable         INT           NULL,
        CogenChp          INT           NULL,
        PlantOperatorName VARCHAR(8000) NULL,
        PlantOwnerName    VARCHAR(8000) NULL,
        PlantParentName   VARCHAR(8000) NULL,
        PlantPhone        VARCHAR(8000) NULL,
        ReleaseDate       DATETIME2(0)  NULL,
        LiveDate          DATETIME2(0)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.OfflineEventTvp — feeds arm.usp_UpsertOfflineEvent(@RunDate, @Rows). Order:
--   FileLogId, EventId, EventKind, EventType, EventCause, EventStatusDesc, UnitId,
--   UnitName, UnitStatusDesc, IndustryCode, IndustryCodeDesc, PlantId, PlantName,
--   PlantParentName, PlantOwnerName, PlantOperatorName, PlantAddressLine1, PlantCity,
--   PlantState, PlantPostalCode, PlantCountry, PlantCounty, PlantLatitude,
--   PlantLongitude, AreaId, AreaName, Offshore, GasRegionId, GasRegionName,
--   MarketRegionId, MarketRegionName, TradingRegionId, TradingRegionName,
--   PowerTradeRegion, WorldRegionId, WorldRegionName, PecZone, PrimarySicId,
--   UnitClassification, Derate, IsDerated, ProductId, ProductDescription, UnitCapacity,
--   OfflineCapacity, OfflineCapacityUOM, EventStartDate, EventEndDate, EventDuration,
--   PrevStartDate, PrevEndDate, UnitTypeId, UnitTypeDesc, EventConfirmationStatus,
--   CogenChp, EventDatePrecision, KickoffSlippage, EventComments, LiveDate, ReleaseDate
-- (RunDate is the scalar @RunDate param; PlantPoint + ModifiedAtUtc omitted;
--  60 columns = FileLogId + 59 business incl. the EventId merge key).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.OfflineEventTvp') IS NULL
BEGIN
    CREATE TYPE arm.OfflineEventTvp AS TABLE
    (
        FileLogId               INT           NOT NULL,
        EventId                 INT           NOT NULL,
        EventKind               VARCHAR(8000) NULL,
        EventType               VARCHAR(8000) NULL,
        EventCause              VARCHAR(8000) NULL,
        EventStatusDesc         VARCHAR(8000) NULL,
        UnitId                  INT           NULL,
        UnitName                VARCHAR(8000) NULL,
        UnitStatusDesc          VARCHAR(8000) NULL,
        IndustryCode            VARCHAR(8000) NULL,
        IndustryCodeDesc        VARCHAR(8000) NULL,
        PlantId                 INT           NULL,
        PlantName               VARCHAR(8000) NULL,
        PlantParentName         VARCHAR(8000) NULL,
        PlantOwnerName          VARCHAR(8000) NULL,
        PlantOperatorName       VARCHAR(8000) NULL,
        PlantAddressLine1       VARCHAR(8000) NULL,
        PlantCity               VARCHAR(8000) NULL,
        PlantState              VARCHAR(8000) NULL,
        PlantPostalCode         VARCHAR(8000) NULL,
        PlantCountry            VARCHAR(8000) NULL,
        PlantCounty             VARCHAR(8000) NULL,
        PlantLatitude           FLOAT         NULL,
        PlantLongitude          FLOAT         NULL,
        AreaId                  INT           NULL,
        AreaName                VARCHAR(8000) NULL,
        Offshore                INT           NULL,
        GasRegionId             VARCHAR(8000) NULL,
        GasRegionName           VARCHAR(8000) NULL,
        MarketRegionId          VARCHAR(8000) NULL,
        MarketRegionName        VARCHAR(8000) NULL,
        TradingRegionId         INT           NULL,
        TradingRegionName       VARCHAR(8000) NULL,
        PowerTradeRegion        VARCHAR(8000) NULL,
        WorldRegionId           INT           NULL,
        WorldRegionName         VARCHAR(8000) NULL,
        PecZone                 VARCHAR(8000) NULL,
        PrimarySicId            VARCHAR(8000) NULL,
        UnitClassification      VARCHAR(8000) NULL,
        Derate                  FLOAT         NULL,
        IsDerated               INT           NULL,
        ProductId               INT           NULL,
        ProductDescription      VARCHAR(8000) NULL,
        UnitCapacity            FLOAT         NULL,
        OfflineCapacity         FLOAT         NULL,
        OfflineCapacityUOM      VARCHAR(8000) NULL,
        EventStartDate          DATETIME2(0)  NULL,
        EventEndDate            DATETIME2(0)  NULL,
        EventDuration           INT           NULL,
        PrevStartDate           DATETIME2(0)  NULL,
        PrevEndDate             DATETIME2(0)  NULL,
        UnitTypeId              VARCHAR(8000) NULL,
        UnitTypeDesc            VARCHAR(8000) NULL,
        EventConfirmationStatus VARCHAR(8000) NULL,
        CogenChp                INT           NULL,
        EventDatePrecision      VARCHAR(8000) NULL,
        KickoffSlippage         INT           NULL,
        EventComments           VARCHAR(8000) NULL,
        LiveDate                DATETIME2(0)  NULL,
        ReleaseDate             DATETIME2(0)  NULL
    );
END
GO

-- =============================================================================
-- ID-CATALOG CENSUS TVP TYPES (two-step summary->detail rework, design §7.5).
-- Feed the STEP-1 census merge procs (003). Each follows the OfflineEvent pattern:
-- RunDate is passed as the @RunDate SCALAR proc param (NOT in the TVP); the census is
-- minimal so — unlike the fact TVPs — there is NO FileLogId column either. DiscoveredAtUtc
-- and ModifiedAtUtc are DB-stamped (not in the TVP). The <Id> column is NOT NULL (merge
-- key source) and is the ONLY census column — NO lat/long (the fact side owns the
-- coordinates, sql/IIR/001). Column order = the sink contract; the C# sink's
-- BuildTable and the 003 proc SELECT/INSERT lists mirror it EXACTLY.
--   arm.PlantSummaryTvp        : (PlantId)
--   arm.UnitSummaryTvp         : (UnitId)
--   arm.OfflineEventSummaryTvp : (EventId)
-- =============================================================================

IF TYPE_ID('arm.PlantSummaryTvp') IS NULL
BEGIN
    CREATE TYPE arm.PlantSummaryTvp AS TABLE
    (
        PlantId INT NOT NULL
    );
END
GO

IF TYPE_ID('arm.UnitSummaryTvp') IS NULL
BEGIN
    CREATE TYPE arm.UnitSummaryTvp AS TABLE
    (
        UnitId INT NOT NULL
    );
END
GO

IF TYPE_ID('arm.OfflineEventSummaryTvp') IS NULL
BEGIN
    CREATE TYPE arm.OfflineEventSummaryTvp AS TABLE
    (
        EventId INT NOT NULL
    );
END
GO
