-- =============================================================================
-- 002_CreateIHSPointLogicTvpTypes.sql
-- The 25 table-valued parameter types for the IHSPointLogic bulk merges
-- (schema [arm]). One TVP per data table (arm.<Table>Tvp), feeding
-- arm.usp_BulkMerge<Table> (003).
--
-- Design of record: docs/design/IHSPointLogic.md §7 (the single field/TVP/PK
-- contract). RUN ORDER: 001 first, then this (002), then 003.
-- IDEMPOTENT: each type is guarded (IF TYPE_ID(...) IS NULL) so re-execution is a
-- no-op. (A type cannot be ALTERed while a proc references it; 999 drops procs
-- before types on teardown, and a clean rebuild drops via 999 then re-runs 001-003.)
--
-- LOAD-BEARING CONTRACT: each TVP's column ORDER is FileLogId FIRST, then the row's
-- business columns in the EXACT §7 order (identical to the arm.<Table> body in 001,
-- minus the DateCreated / ModifiedAtUtc table defaults). The C# sink's BuildTable
-- and the 003 merge proc SELECT/INSERT lists MUST mirror this order EXACTLY. Column
-- widths equal the 001 table widths so the TVP cannot truncate.
--
-- The TVPs intentionally carry NO DateCreated / ModifiedAtUtc (populated by the
-- target table DEFAULTs) and NO PK/unique (the merge procs de-dup the batch on the
-- merge key before MERGE, so an in-batch duplicate natural key cannot break it).
-- NULL-ability mirrors the target columns.
-- =============================================================================

USE IHSPointLogic;
GO

-- =============================================================================
-- TIER 0 — DIMENSIONS (archetype A).
-- =============================================================================

-- §5  arm.RegionTvp
IF TYPE_ID('arm.RegionTvp') IS NULL
    CREATE TYPE arm.RegionTvp AS TABLE
    (
        FileLogId INT           NOT NULL,
        RegionId  INT           NOT NULL,
        [Name]    NVARCHAR(128) NOT NULL
    );
GO

-- §6a arm.StateTvp
IF TYPE_ID('arm.StateTvp') IS NULL
    CREATE TYPE arm.StateTvp AS TABLE
    (
        FileLogId INT           NOT NULL,
        StateId   INT           NOT NULL,
        [Name]    NVARCHAR(128) NOT NULL
    );
GO

-- §6b arm.PointStatusTvp
IF TYPE_ID('arm.PointStatusTvp') IS NULL
    CREATE TYPE arm.PointStatusTvp AS TABLE
    (
        FileLogId     INT           NOT NULL,
        PointStatusId INT           NOT NULL,
        [Name]        NVARCHAR(128) NOT NULL
    );
GO

-- §6c arm.PointTypeTvp
IF TYPE_ID('arm.PointTypeTvp') IS NULL
    CREATE TYPE arm.PointTypeTvp AS TABLE
    (
        FileLogId   INT           NOT NULL,
        PointTypeId INT           NOT NULL,
        [Name]      NVARCHAR(128) NOT NULL
    );
GO

-- §6d arm.PipelineNoticeCategoryTvp
IF TYPE_ID('arm.PipelineNoticeCategoryTvp') IS NULL
    CREATE TYPE arm.PipelineNoticeCategoryTvp AS TABLE
    (
        FileLogId                INT           NOT NULL,
        PipelineNoticeCategoryId INT           NOT NULL,
        [Name]                   NVARCHAR(128) NOT NULL
    );
GO

-- §6e arm.PipelineTvp
IF TYPE_ID('arm.PipelineTvp') IS NULL
    CREATE TYPE arm.PipelineTvp AS TABLE
    (
        FileLogId  INT           NOT NULL,
        PipelineId INT           NOT NULL,
        [Name]     NVARCHAR(200) NOT NULL,
        LegacyName NVARCHAR(200) NULL
    );
GO

-- §7  arm.PointTvp
IF TYPE_ID('arm.PointTvp') IS NULL
    CREATE TYPE arm.PointTvp AS TABLE
    (
        FileLogId     INT           NOT NULL,
        PointId       INT           NOT NULL,
        [Name]        NVARCHAR(200) NOT NULL,
        PipelineId    INT           NULL,
        PointTypeId   INT           NULL,
        PointStatusId INT           NULL
    );
GO

-- §12 arm.PointMetadataTvp (22 business cols)
IF TYPE_ID('arm.PointMetadataTvp') IS NULL
    CREATE TYPE arm.PointMetadataTvp AS TABLE
    (
        FileLogId           INT           NOT NULL,
        PointId             INT           NOT NULL,
        PointLciId          VARCHAR(16)   NULL,
        Drn                 NVARCHAR(64)  NULL,
        PointName           NVARCHAR(200) NOT NULL,
        PointTypeId         INT           NULL,
        PointType           NVARCHAR(64)  NULL,
        StateId             INT           NULL,
        [State]             NVARCHAR(64)  NULL,
        County              NVARCHAR(128) NULL,
        Region              NVARCHAR(128) NULL,
        PipelineId          INT           NULL,
        PipelineDisplayName NVARCHAR(200) NULL,
        PointIsActive       BIT           NULL,
        DesignCapacity      DECIMAL(18,6) NULL,
        FlowDirectionId     INT           NULL,
        FlowDirection       NVARCHAR(32)  NULL,
        DisplayName         NVARCHAR(200) NULL,
        LocProp             NVARCHAR(500)  NULL,
        PointLatitude       DECIMAL(9,6)  NULL,
        PointLongitude      DECIMAL(9,6)  NULL,
        CountyId            INT           NULL,
        RegionId            INT           NULL
    );
GO

-- §15 arm.PipelineNoticeSearchTvp
IF TYPE_ID('arm.PipelineNoticeSearchTvp') IS NULL
    CREATE TYPE arm.PipelineNoticeSearchTvp AS TABLE
    (
        FileLogId     INT               NOT NULL,
        Id            BIGINT            NOT NULL,
        Subject       NVARCHAR(400)     NULL,
        Format        VARCHAR(16)       NULL,
        PostedDate    DATETIMEOFFSET(3) NULL,
        ExternalId    VARCHAR(32)       NULL,
        CategoryId    INT               NULL,
        IsCritical    BIT               NULL,
        ContentLink   NVARCHAR(400)     NULL,
        PipelineId    INT               NULL,
        EffectiveDate DATETIMEOFFSET(3) NULL,
        EndDate       DATETIMEOFFSET(3) NULL
    );
GO

-- =============================================================================
-- TIER 0 — FACT SNAPSHOTS (archetype B).
-- =============================================================================

-- §8  arm.DemandForecastRegionTvp
IF TYPE_ID('arm.DemandForecastRegionTvp') IS NULL
    CREATE TYPE arm.DemandForecastRegionTvp AS TABLE
    (
        FileLogId            INT           NOT NULL,
        ForecastDate         DATE          NOT NULL,
        [Date]               DATE          NOT NULL,
        Region               NVARCHAR(128) NOT NULL,
        Subregion            NVARCHAR(128) NOT NULL,
        DepartureFromNormalF DECIMAL(6,2)  NULL,
        NormalTemperatureF   DECIMAL(6,2)  NULL,
        TotalConsumption     DECIMAL(18,6) NULL,
        Power                DECIMAL(18,6) NULL,
        Industrial           DECIMAL(18,6) NULL,
        ResCom               DECIMAL(18,6) NULL,
        DepartureFromNormal  DECIMAL(18,6) NULL
    );
GO

-- §9  arm.DemandForecastUsLower48Tvp
IF TYPE_ID('arm.DemandForecastUsLower48Tvp') IS NULL
    CREATE TYPE arm.DemandForecastUsLower48Tvp AS TABLE
    (
        FileLogId             INT           NOT NULL,
        ForecastDate          DATE          NOT NULL,
        [Date]                DATE          NOT NULL,
        Region                NVARCHAR(128) NOT NULL,
        Power                 DECIMAL(18,6) NULL,
        Industrial            DECIMAL(18,6) NULL,
        ResidentialCommercial DECIMAL(18,6) NULL,
        Subtotal              DECIMAL(18,6) NULL
    );
GO

-- §10 arm.GasProductionProducingAreaTvp
IF TYPE_ID('arm.GasProductionProducingAreaTvp') IS NULL
    CREATE TYPE arm.GasProductionProducingAreaTvp AS TABLE
    (
        FileLogId        INT           NOT NULL,
        ReportedDate     DATETIME2(0)  NOT NULL,
        ReferenceDate    DATE          NOT NULL,
        Region           NVARCHAR(128) NOT NULL,
        ProducingArea    NVARCHAR(128) NOT NULL,
        [State]          NVARCHAR(64)  NOT NULL,
        DryFactoredValue DECIMAL(18,6) NULL,
        WellheadValue    DECIMAL(18,6) NULL
    );
GO

-- §11 arm.MarketBalancesUsLower48Tvp (16 measures)
IF TYPE_ID('arm.MarketBalancesUsLower48Tvp') IS NULL
    CREATE TYPE arm.MarketBalancesUsLower48Tvp AS TABLE
    (
        FileLogId             INT           NOT NULL,
        TimePeriod            DATE          NOT NULL,
        Wellhead              DECIMAL(18,6) NULL,
        ProductionLoss        DECIMAL(18,6) NULL,
        DryGas                DECIMAL(18,6) NULL,
        CanadaImports         DECIMAL(18,6) NULL,
        LngSendout            DECIMAL(18,6) NULL,
        TotalSupply           DECIMAL(18,6) NULL,
        Power                 DECIMAL(18,6) NULL,
        Industrial            DECIMAL(18,6) NULL,
        ResidentialCommercial DECIMAL(18,6) NULL,
        Subtotal              DECIMAL(18,6) NULL,
        MexicoExports         DECIMAL(18,6) NULL,
        LngFeedGas            DECIMAL(18,6) NULL,
        PipeLoss              DECIMAL(18,6) NULL,
        TotalDemand           DECIMAL(18,6) NULL,
        Storage               DECIMAL(18,6) NULL,
        BalancingItem         DECIMAL(18,6) NULL
    );
GO

-- §13 arm.ModeledDemandRegionTypeTvp
IF TYPE_ID('arm.ModeledDemandRegionTypeTvp') IS NULL
    CREATE TYPE arm.ModeledDemandRegionTypeTvp AS TABLE
    (
        FileLogId      INT           NOT NULL,
        ReferenceDate  DATE          NOT NULL,
        PLEProductName NVARCHAR(128) NOT NULL,
        RegionName     NVARCHAR(128) NOT NULL,
        Volume         DECIMAL(18,6) NULL
    );
GO

-- §14 arm.PipelineFlowThroughputTvp
IF TYPE_ID('arm.PipelineFlowThroughputTvp') IS NULL
    CREATE TYPE arm.PipelineFlowThroughputTvp AS TABLE
    (
        FileLogId  INT           NOT NULL,
        FlowDate   DATE          NOT NULL,
        Region     NVARCHAR(128) NOT NULL,
        Pipeline   NVARCHAR(200) NOT NULL,
        Throughput NVARCHAR(200) NOT NULL,
        FlowType   NVARCHAR(32)  NOT NULL,
        Volume     DECIMAL(18,6) NULL
    );
GO

-- §16 arm.UsImportsExportsByPointsAggregateTvp
IF TYPE_ID('arm.UsImportsExportsByPointsAggregateTvp') IS NULL
    CREATE TYPE arm.UsImportsExportsByPointsAggregateTvp AS TABLE
    (
        FileLogId      INT           NOT NULL,
        RunDate        DATE          NOT NULL,
        FlowDate       DATE          NOT NULL,
        PointName      NVARCHAR(200) NOT NULL,
        PipelineName   NVARCHAR(200) NOT NULL,
        LedgerSide     NVARCHAR(16)  NOT NULL,
        Volume         DECIMAL(18,6) NULL,
        [State]        VARCHAR(8)    NULL,
        County         NVARCHAR(128) NULL,
        [Type]         NVARCHAR(64)  NULL,
        PointGroupName NVARCHAR(128) NULL,
        DistrictName   NVARCHAR(128) NULL
    );
GO

-- §17 arm.UsSampleStorageFacilityTvp
IF TYPE_ID('arm.UsSampleStorageFacilityTvp') IS NULL
    CREATE TYPE arm.UsSampleStorageFacilityTvp AS TABLE
    (
        FileLogId  INT           NOT NULL,
        ReportDate DATE          NOT NULL,
        FlowDate   DATE          NOT NULL,
        [Name]     NVARCHAR(200) NOT NULL,
        EiaRegion  NVARCHAR(64)  NOT NULL,
        [State]    VARCHAR(4)    NOT NULL,
        FieldType  NVARCHAR(32)  NOT NULL,
        Volume     DECIMAL(18,6) NULL
    );
GO

-- §18 arm.StateFlowsThroughputAggregateTvp
IF TYPE_ID('arm.StateFlowsThroughputAggregateTvp') IS NULL
    CREATE TYPE arm.StateFlowsThroughputAggregateTvp AS TABLE
    (
        FileLogId INT           NOT NULL,
        FlowDate  DATE          NOT NULL,
        Region    NVARCHAR(128) NOT NULL,
        FromState NVARCHAR(64)  NOT NULL,
        ToState   NVARCHAR(64)  NOT NULL,
        FlowType  NVARCHAR(32)  NOT NULL,
        Volume    DECIMAL(18,6) NULL
    );
GO

-- §19 arm.SupplyAndDemandTvp (marketsHistory; 16 measures, keyed by Date)
IF TYPE_ID('arm.SupplyAndDemandTvp') IS NULL
    CREATE TYPE arm.SupplyAndDemandTvp AS TABLE
    (
        FileLogId             INT           NOT NULL,
        [Date]                DATE          NOT NULL,
        Wellhead              DECIMAL(18,6) NULL,
        ProductionLoss        DECIMAL(18,6) NULL,
        DryGas                DECIMAL(18,6) NULL,
        CanadaImports         DECIMAL(18,6) NULL,
        LngSendout            DECIMAL(18,6) NULL,
        TotalSupply           DECIMAL(18,6) NULL,
        Power                 DECIMAL(18,6) NULL,
        Industrial            DECIMAL(18,6) NULL,
        ResidentialCommercial DECIMAL(18,6) NULL,
        Subtotal              DECIMAL(18,6) NULL,
        MexicoExports         DECIMAL(18,6) NULL,
        LngFeedGas            DECIMAL(18,6) NULL,
        PipeLoss              DECIMAL(18,6) NULL,
        TotalDemand           DECIMAL(18,6) NULL,
        Storage               DECIMAL(18,6) NULL,
        BalancingItem         DECIMAL(18,6) NULL
    );
GO

-- =============================================================================
-- TIER 1 — DISCOVERY-FED LOOKUPS (archetype C).
-- =============================================================================

-- §20 arm.CountyTvp
IF TYPE_ID('arm.CountyTvp') IS NULL
    CREATE TYPE arm.CountyTvp AS TABLE
    (
        FileLogId INT           NOT NULL,
        CountyId  INT           NOT NULL,
        StateId   INT           NOT NULL,
        [Name]    NVARCHAR(128) NOT NULL
    );
GO

-- §21 arm.FacilityTvp (facilitytypeid echo dropped; PointTypeId injected key)
IF TYPE_ID('arm.FacilityTvp') IS NULL
    CREATE TYPE arm.FacilityTvp AS TABLE
    (
        FileLogId   INT           NOT NULL,
        FacilityId  INT           NOT NULL,
        PointTypeId INT           NOT NULL,
        [Name]      NVARCHAR(200) NOT NULL
    );
GO

-- §22 arm.SubregionTvp
IF TYPE_ID('arm.SubregionTvp') IS NULL
    CREATE TYPE arm.SubregionTvp AS TABLE
    (
        FileLogId   INT           NOT NULL,
        SubRegionId INT           NOT NULL,
        RegionId    INT           NOT NULL,
        [Name]      NVARCHAR(128) NOT NULL
    );
GO

-- =============================================================================
-- TIER 2 — PARAMETRIZED FACTS (archetypes D, E).
-- =============================================================================

-- §23 arm.SupplyAndDemandByRegionTvp
IF TYPE_ID('arm.SupplyAndDemandByRegionTvp') IS NULL
    CREATE TYPE arm.SupplyAndDemandByRegionTvp AS TABLE
    (
        FileLogId   INT           NOT NULL,
        RegionId    INT           NOT NULL,
        [Date]      DATE          NOT NULL,
        Product     NVARCHAR(128) NOT NULL,
        VolumeMmcfd DECIMAL(18,6) NULL
    );
GO

-- §24 arm.SupplyAndDemandBySubRegionTvp
IF TYPE_ID('arm.SupplyAndDemandBySubRegionTvp') IS NULL
    CREATE TYPE arm.SupplyAndDemandBySubRegionTvp AS TABLE
    (
        FileLogId   INT           NOT NULL,
        SubRegionId INT           NOT NULL,
        RegionId    INT           NOT NULL,
        [Date]      DATE          NOT NULL,
        Product     NVARCHAR(128) NOT NULL,
        VolumeMmcfd DECIMAL(18,6) NULL
    );
GO

-- §25 arm.PointVolumeTvp
IF TYPE_ID('arm.PointVolumeTvp') IS NULL
    CREATE TYPE arm.PointVolumeTvp AS TABLE
    (
        FileLogId INT           NOT NULL,
        PointId   INT           NOT NULL,
        [Date]    DATE          NOT NULL,
        Volume    DECIMAL(18,6) NULL
    );
GO
