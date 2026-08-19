-- =============================================================================
-- 999_DropIHSPointLogicObjects.sql
-- TEARDOWN for the IHSPointLogic (S&P Global / IHS Markit PointLogic) loader. Drops
-- every object created by
--   001_CreateIHSPointLogicSchema.sql, 002_CreateIHSPointLogicTvpTypes.sql,
--   003_CreateIHSPointLogicProcedures.sql
-- (all in the [arm] schema) and finally the [arm] schema itself.
--
-- *** DESTRUCTIVE ***  Permanently DROPS the 25 data tables and ALL their data, the
-- arm.FileLog hub, the arm.Endpoint/arm.Status lookups, the 25 TVP types, the 33
-- stored procedures (usp_UpsertFileLog + 25 usp_BulkMerge<Table> + 5 usp_Get*Ids +
-- usp_GetEndpointSchedule + usp_ValidateLoad), and the [arm] schema. No recovery
-- short of a database restore.
--
-- SCOPE / SAFETY:
--   * Targets the IHSPointLogic DATABASE ONLY (Loaders:IHSPointLogic:ConnectionString
--     -> Database=IHSPointLogic). A top-of-script guard turns the whole run into a
--     NO-OP (SET NOEXEC ON) when the IHSPointLogic database does not exist, so it is
--     safe to run against a fresh server.
--   * Does NOT drop the IHSPointLogic database itself (out of scope).
--   * Does NOT touch anything in the platform-owned [core] schema.
--   * Drops ONLY IHSPointLogic's own [arm]-schema objects. The final DROP SCHEMA is
--     GUARDED to fire only when [arm] holds no remaining objects or types.
--
-- RUN ORDER:
--   * To fully UNDO 001-003: run THIS script (999).
--   * To REBUILD afterwards:  run 001 -> 002 -> 003 in order.
--   The 999 prefix sorts AFTER the ordered create scripts.
--
-- IDEMPOTENT / RE-RUNNABLE: every drop is guarded (IF OBJECT_ID(...,'P'/'U') IS NOT
-- NULL / IF TYPE_ID(...) IS NOT NULL / schema-emptiness check). Running it twice, or
-- against a DB where the objects were never created, completes with no error.
--
-- DEPENDENCY-ORDERED DROP (reverse of the create order):
--   1) Procedures      (must precede the TVP types they reference as parameters)
--   2) TVP types       (25)
--   3) Tables, FK-child first: Tier-2 facts -> Tier-1 lookups -> Tier-0 facts ->
--      Tier-0 dimensions -> FileLog -> Endpoint/Status lookups. DROP TABLE removes
--      each table's own FK constraints, so dropping children before parents keeps
--      every drop unblocked without an explicit ALTER TABLE ... DROP CONSTRAINT.
--   4) Schema [arm]    (LAST; guarded on emptiness)
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Guard: if the IHSPointLogic database is absent, compile-only no-op for the rest.
-- SET NOEXEC ON persists across GO for the session; SET NOEXEC OFF at the end
-- restores it.
-- ----------------------------------------------------------------------------
IF DB_ID(N'IHSPointLogic') IS NULL
BEGIN
    RAISERROR('IHSPointLogic database not found - teardown is a no-op; nothing to drop.', 10, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

USE IHSPointLogic;
GO

-- ============================================================================
-- 1) PROCEDURES. Dropped BEFORE the TVP types because the 25 bulk-merge procs take
--    those types as READONLY parameters (a type cannot be dropped while referenced).
-- ============================================================================
RAISERROR('IHSPointLogic teardown: dropping [arm] stored procedures...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.usp_UpsertFileLog', 'P')                            IS NOT NULL DROP PROCEDURE arm.usp_UpsertFileLog;
IF OBJECT_ID(N'arm.usp_ValidateLoad', 'P')                            IS NOT NULL DROP PROCEDURE arm.usp_ValidateLoad;
-- Read procs
IF OBJECT_ID(N'arm.usp_GetRegionIds', 'P')                            IS NOT NULL DROP PROCEDURE arm.usp_GetRegionIds;
IF OBJECT_ID(N'arm.usp_GetStateIds', 'P')                             IS NOT NULL DROP PROCEDURE arm.usp_GetStateIds;
IF OBJECT_ID(N'arm.usp_GetPointTypeIds', 'P')                         IS NOT NULL DROP PROCEDURE arm.usp_GetPointTypeIds;
IF OBJECT_ID(N'arm.usp_GetSubRegionIds', 'P')                         IS NOT NULL DROP PROCEDURE arm.usp_GetSubRegionIds;
IF OBJECT_ID(N'arm.usp_GetPointIds', 'P')                             IS NOT NULL DROP PROCEDURE arm.usp_GetPointIds;
IF OBJECT_ID(N'arm.usp_GetEndpointSchedule', 'P')                     IS NOT NULL DROP PROCEDURE arm.usp_GetEndpointSchedule;
-- Bulk-merge procs (25)
IF OBJECT_ID(N'arm.usp_BulkMergeRegion', 'P')                       IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeRegion;
IF OBJECT_ID(N'arm.usp_BulkMergeState', 'P')                        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeState;
IF OBJECT_ID(N'arm.usp_BulkMergePointStatus', 'P')                  IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePointStatus;
IF OBJECT_ID(N'arm.usp_BulkMergePointType', 'P')                    IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePointType;
IF OBJECT_ID(N'arm.usp_BulkMergePipelineNoticeCategory', 'P')       IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePipelineNoticeCategory;
IF OBJECT_ID(N'arm.usp_BulkMergePipeline', 'P')                     IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePipeline;
IF OBJECT_ID(N'arm.usp_BulkMergePoint', 'P')                        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePoint;
IF OBJECT_ID(N'arm.usp_BulkMergePointMetadata', 'P')                IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePointMetadata;
IF OBJECT_ID(N'arm.usp_BulkMergePipelineNoticeSearch', 'P')           IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePipelineNoticeSearch;
IF OBJECT_ID(N'arm.usp_BulkMergeDemandForecastRegion', 'P')           IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeDemandForecastRegion;
IF OBJECT_ID(N'arm.usp_BulkMergeDemandForecastUsLower48', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeDemandForecastUsLower48;
IF OBJECT_ID(N'arm.usp_BulkMergeGasProductionProducingArea', 'P')     IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeGasProductionProducingArea;
IF OBJECT_ID(N'arm.usp_BulkMergeMarketBalancesUsLower48', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeMarketBalancesUsLower48;
IF OBJECT_ID(N'arm.usp_BulkMergeModeledDemandRegionType', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeModeledDemandRegionType;
IF OBJECT_ID(N'arm.usp_BulkMergePipelineFlowThroughput', 'P')         IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePipelineFlowThroughput;
IF OBJECT_ID(N'arm.usp_BulkMergeUsImportsExportsByPointsAggregate','P') IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeUsImportsExportsByPointsAggregate;
IF OBJECT_ID(N'arm.usp_BulkMergeUsSampleStorageFacility', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeUsSampleStorageFacility;
IF OBJECT_ID(N'arm.usp_BulkMergeStateFlowsThroughputAggregate', 'P')  IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeStateFlowsThroughputAggregate;
IF OBJECT_ID(N'arm.usp_BulkMergeSupplyAndDemand', 'P')                IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeSupplyAndDemand;
IF OBJECT_ID(N'arm.usp_BulkMergeCounty', 'P')                       IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeCounty;
IF OBJECT_ID(N'arm.usp_BulkMergeFacility', 'P')                     IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeFacility;
IF OBJECT_ID(N'arm.usp_BulkMergeSubregion', 'P')                    IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeSubregion;
IF OBJECT_ID(N'arm.usp_BulkMergeSupplyAndDemandByRegion', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeSupplyAndDemandByRegion;
IF OBJECT_ID(N'arm.usp_BulkMergeSupplyAndDemandBySubRegion', 'P')     IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeSupplyAndDemandBySubRegion;
IF OBJECT_ID(N'arm.usp_BulkMergePointVolume', 'P')                    IS NOT NULL DROP PROCEDURE arm.usp_BulkMergePointVolume;
GO

-- ============================================================================
-- 2) TVP TYPES (25). Safe to drop now that no procedure references them.
-- ============================================================================
RAISERROR('IHSPointLogic teardown: dropping [arm] TVP types...', 10, 1) WITH NOWAIT;
GO
IF TYPE_ID(N'arm.RegionTvp')                          IS NOT NULL DROP TYPE arm.RegionTvp;
IF TYPE_ID(N'arm.StateTvp')                           IS NOT NULL DROP TYPE arm.StateTvp;
IF TYPE_ID(N'arm.PointStatusTvp')                     IS NOT NULL DROP TYPE arm.PointStatusTvp;
IF TYPE_ID(N'arm.PointTypeTvp')                       IS NOT NULL DROP TYPE arm.PointTypeTvp;
IF TYPE_ID(N'arm.PipelineNoticeCategoryTvp')          IS NOT NULL DROP TYPE arm.PipelineNoticeCategoryTvp;
IF TYPE_ID(N'arm.PipelineTvp')                        IS NOT NULL DROP TYPE arm.PipelineTvp;
IF TYPE_ID(N'arm.PointTvp')                           IS NOT NULL DROP TYPE arm.PointTvp;
IF TYPE_ID(N'arm.PointMetadataTvp')                   IS NOT NULL DROP TYPE arm.PointMetadataTvp;
IF TYPE_ID(N'arm.PipelineNoticeSearchTvp')              IS NOT NULL DROP TYPE arm.PipelineNoticeSearchTvp;
IF TYPE_ID(N'arm.DemandForecastRegionTvp')              IS NOT NULL DROP TYPE arm.DemandForecastRegionTvp;
IF TYPE_ID(N'arm.DemandForecastUsLower48Tvp')           IS NOT NULL DROP TYPE arm.DemandForecastUsLower48Tvp;
IF TYPE_ID(N'arm.GasProductionProducingAreaTvp')        IS NOT NULL DROP TYPE arm.GasProductionProducingAreaTvp;
IF TYPE_ID(N'arm.MarketBalancesUsLower48Tvp')           IS NOT NULL DROP TYPE arm.MarketBalancesUsLower48Tvp;
IF TYPE_ID(N'arm.ModeledDemandRegionTypeTvp')           IS NOT NULL DROP TYPE arm.ModeledDemandRegionTypeTvp;
IF TYPE_ID(N'arm.PipelineFlowThroughputTvp')            IS NOT NULL DROP TYPE arm.PipelineFlowThroughputTvp;
IF TYPE_ID(N'arm.UsImportsExportsByPointsAggregateTvp') IS NOT NULL DROP TYPE arm.UsImportsExportsByPointsAggregateTvp;
IF TYPE_ID(N'arm.UsSampleStorageFacilityTvp')           IS NOT NULL DROP TYPE arm.UsSampleStorageFacilityTvp;
IF TYPE_ID(N'arm.StateFlowsThroughputAggregateTvp')     IS NOT NULL DROP TYPE arm.StateFlowsThroughputAggregateTvp;
IF TYPE_ID(N'arm.SupplyAndDemandTvp')                   IS NOT NULL DROP TYPE arm.SupplyAndDemandTvp;
IF TYPE_ID(N'arm.CountyTvp')                          IS NOT NULL DROP TYPE arm.CountyTvp;
IF TYPE_ID(N'arm.FacilityTvp')                        IS NOT NULL DROP TYPE arm.FacilityTvp;
IF TYPE_ID(N'arm.SubregionTvp')                       IS NOT NULL DROP TYPE arm.SubregionTvp;
IF TYPE_ID(N'arm.SupplyAndDemandByRegionTvp')           IS NOT NULL DROP TYPE arm.SupplyAndDemandByRegionTvp;
IF TYPE_ID(N'arm.SupplyAndDemandBySubRegionTvp')        IS NOT NULL DROP TYPE arm.SupplyAndDemandBySubRegionTvp;
IF TYPE_ID(N'arm.PointVolumeTvp')                       IS NOT NULL DROP TYPE arm.PointVolumeTvp;
GO

-- ============================================================================
-- 3) TABLES, FK-child first (Tier-2 facts -> Tier-1 lookups -> Tier-0 facts ->
--    Tier-0 dimensions -> FileLog -> Endpoint/Status).
-- ============================================================================
RAISERROR('IHSPointLogic teardown: dropping [arm] tables (FK-child first)...', 10, 1) WITH NOWAIT;
GO
-- Tier 2 facts (reference dimensions + FileLog)
IF OBJECT_ID(N'arm.PointVolume', 'U')                      IS NOT NULL DROP TABLE arm.PointVolume;
IF OBJECT_ID(N'arm.SupplyAndDemandBySubRegion', 'U')       IS NOT NULL DROP TABLE arm.SupplyAndDemandBySubRegion;
IF OBJECT_ID(N'arm.SupplyAndDemandByRegion', 'U')          IS NOT NULL DROP TABLE arm.SupplyAndDemandByRegion;
-- Tier 1 lookups (reference Tier-0 dimensions + FileLog)
IF OBJECT_ID(N'arm.Subregion', 'U')                      IS NOT NULL DROP TABLE arm.Subregion;
IF OBJECT_ID(N'arm.Facility', 'U')                       IS NOT NULL DROP TABLE arm.Facility;
IF OBJECT_ID(N'arm.County', 'U')                         IS NOT NULL DROP TABLE arm.County;
-- Tier 0 facts (reference FileLog only)
IF OBJECT_ID(N'arm.DemandForecastRegion', 'U')            IS NOT NULL DROP TABLE arm.DemandForecastRegion;
IF OBJECT_ID(N'arm.DemandForecastUsLower48', 'U')         IS NOT NULL DROP TABLE arm.DemandForecastUsLower48;
IF OBJECT_ID(N'arm.GasProductionProducingArea', 'U')      IS NOT NULL DROP TABLE arm.GasProductionProducingArea;
IF OBJECT_ID(N'arm.MarketBalancesUsLower48', 'U')         IS NOT NULL DROP TABLE arm.MarketBalancesUsLower48;
IF OBJECT_ID(N'arm.ModeledDemandRegionType', 'U')         IS NOT NULL DROP TABLE arm.ModeledDemandRegionType;
IF OBJECT_ID(N'arm.PipelineFlowThroughput', 'U')          IS NOT NULL DROP TABLE arm.PipelineFlowThroughput;
IF OBJECT_ID(N'arm.UsImportsExportsByPointsAggregate', 'U') IS NOT NULL DROP TABLE arm.UsImportsExportsByPointsAggregate;
IF OBJECT_ID(N'arm.UsSampleStorageFacility', 'U')         IS NOT NULL DROP TABLE arm.UsSampleStorageFacility;
IF OBJECT_ID(N'arm.StateFlowsThroughputAggregate', 'U')   IS NOT NULL DROP TABLE arm.StateFlowsThroughputAggregate;
IF OBJECT_ID(N'arm.SupplyAndDemand', 'U')                 IS NOT NULL DROP TABLE arm.SupplyAndDemand;
IF OBJECT_ID(N'arm.PipelineNoticeSearch', 'U')            IS NOT NULL DROP TABLE arm.PipelineNoticeSearch;
-- Tier 0 dimensions (reference FileLog only; PointVolume/County/Facility/
-- Subregion above already dropped, so these free to go)
IF OBJECT_ID(N'arm.PointMetadata', 'U')                 IS NOT NULL DROP TABLE arm.PointMetadata;
IF OBJECT_ID(N'arm.Point', 'U')                         IS NOT NULL DROP TABLE arm.Point;
IF OBJECT_ID(N'arm.Pipeline', 'U')                      IS NOT NULL DROP TABLE arm.Pipeline;
IF OBJECT_ID(N'arm.PointType', 'U')                     IS NOT NULL DROP TABLE arm.PointType;
IF OBJECT_ID(N'arm.PointStatus', 'U')                   IS NOT NULL DROP TABLE arm.PointStatus;
IF OBJECT_ID(N'arm.State', 'U')                         IS NOT NULL DROP TABLE arm.State;
IF OBJECT_ID(N'arm.Region', 'U')                        IS NOT NULL DROP TABLE arm.Region;
IF OBJECT_ID(N'arm.PipelineNoticeCategory', 'U')        IS NOT NULL DROP TABLE arm.PipelineNoticeCategory;
-- Hub (referenced by every data table above) then the name lookups it references
IF OBJECT_ID(N'arm.FileLog', 'U')                         IS NOT NULL DROP TABLE arm.FileLog;
IF OBJECT_ID(N'arm.Endpoint', 'U')                        IS NOT NULL DROP TABLE arm.Endpoint;
IF OBJECT_ID(N'arm.Status', 'U')                          IS NOT NULL DROP TABLE arm.Status;
GO

-- ============================================================================
-- 4) SCHEMA [arm]. Dropped LAST and only when it exists AND holds no remaining
--    objects (sys.objects) or user-defined types (sys.types). Protects a shared
--    schema from being dropped with foreign objects still bound to it. In the
--    IHSPointLogic DB, [arm] is loader-only, so after steps 1-3 it drops cleanly.
-- ============================================================================
RAISERROR('IHSPointLogic teardown: evaluating [arm] schema drop...', 10, 1) WITH NOWAIT;
GO
IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
   AND NOT EXISTS (SELECT 1 FROM sys.objects o
                   JOIN sys.schemas s ON o.schema_id = s.schema_id
                   WHERE s.name = 'arm')
   AND NOT EXISTS (SELECT 1 FROM sys.types t
                   JOIN sys.schemas s ON t.schema_id = s.schema_id
                   WHERE s.name = 'arm')
BEGIN
    DROP SCHEMA arm;
    RAISERROR('IHSPointLogic teardown: schema [arm] dropped.', 10, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('IHSPointLogic teardown: schema [arm] not dropped (absent, or still holds objects/types).', 10, 1) WITH NOWAIT;
END
GO

-- ----------------------------------------------------------------------------
-- Restore execution for the session (undoes the no-op guard if it fired).
-- ----------------------------------------------------------------------------
SET NOEXEC OFF;
GO

RAISERROR('IHSPointLogic teardown complete.', 10, 1) WITH NOWAIT;
GO
