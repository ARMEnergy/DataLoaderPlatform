-- =============================================================================
-- 999_DropIirObjects.sql
-- TEARDOWN for the IIR (Industrial Info Resources) loader. Drops every object
-- created by
--   001_CreateIirSchema.sql, 002_CreateIirTvpTypes.sql, 003_CreateIirProcedures.sql
-- (all in the [arm] schema) and finally the [arm] schema itself.
--
-- *** DESTRUCTIVE ***  This permanently DROPS tables and ALL their data
-- (arm.Plant, arm.Unit, arm.OfflineEvent, the three id-catalog census tables
-- arm.PlantSummary / arm.UnitSummary / arm.OfflineEventSummary, arm.FileLog,
-- arm.Endpoint, arm.Status), the six TVP types, the eight stored procedures, and the
-- [arm] schema. There is no recovery short of a database restore. NOTE that arm.Plant
-- / arm.Unit / arm.OfflineEvent may be the user's PRODUCTION tables — run only when
-- you intend to fully remove the IIR loader's objects.
--
-- SCOPE / SAFETY:
--   * Targets the IIR DATABASE ONLY (Loaders:IIR:ConnectionString -> Database=IIR).
--     A top-of-script guard makes the whole run a NO-OP (via SET NOEXEC ON) when the
--     IIR database does not exist, so it is safe to run against a fresh server.
--   * Does NOT drop the IIR database itself (out of scope).
--   * Does NOT touch anything in the platform-owned [core] schema.
--   * Drops ONLY IIR's own [arm]-schema objects. The final DROP SCHEMA is GUARDED to
--     fire only when [arm] holds no remaining objects or types, so a shared schema is
--     never disturbed.
--
-- RUN ORDER:
--   * To fully UNDO 001-003: run THIS script (999).
--   * To REBUILD afterwards:  run 001 -> 002 -> 003 in order.
--   The 999 prefix sorts AFTER the ordered create scripts and is never mistaken for one.
--
-- IDEMPOTENT / RE-RUNNABLE: every drop is guarded
--   (IF OBJECT_ID(...,'P'/'U') IS NOT NULL / IF TYPE_ID(...) IS NOT NULL / schema
--   emptiness check). Running it twice, or against a DB where IIR/arm was never
--   created, completes with no error.
--
-- DEPENDENCY-ORDERED DROP (reverse of the create order):
--   1) Procedures      (must precede the TVP types they reference as parameters) —
--        the 3 fact upserts, the 3 census upserts, usp_UpsertFileLog, usp_ValidateLoad
--   2) TVP types       (fact: OfflineEventTvp/UnitTvp/PlantTvp; census:
--        OfflineEventSummaryTvp/UnitSummaryTvp/PlantSummaryTvp)
--   3) Tables, FK-child first:
--        arm.Plant / arm.Unit / arm.OfflineEvent                       (data; no outbound FKs)
--        arm.PlantSummary / arm.UnitSummary / arm.OfflineEventSummary  (census; no outbound FKs)
--        arm.FileLog                                                   (FK -> Endpoint, Status)
--        arm.Endpoint / arm.Status                                     (lookups; no outbound FKs)
--   4) Schema [arm]    (LAST; guarded on emptiness)
-- DROP TABLE removes each table's own FK constraints, so dropping children before
-- their parents keeps every drop unblocked without an explicit ALTER TABLE ... DROP
-- CONSTRAINT step.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Guard: if the IIR database is absent, turn the rest of the script into a
-- compile-only no-op. SET NOEXEC ON persists across GO for the session; every
-- following batch (including USE IIR) is parsed but not executed, so nothing
-- errors. SET NOEXEC OFF at the very end restores the session.
-- ----------------------------------------------------------------------------
IF DB_ID(N'IIR') IS NULL
BEGIN
    RAISERROR('IIR database not found - teardown is a no-op; nothing to drop.', 10, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

USE IIR;
GO

-- ============================================================================
-- 1) PROCEDURES. Dropped BEFORE the TVP types because the three upsert procs take
--    those types as READONLY parameters (a type cannot be dropped while a proc
--    references it).
-- ============================================================================
RAISERROR('IIR teardown: dropping [arm] stored procedures...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.usp_UpsertPlant', 'P')               IS NOT NULL DROP PROCEDURE arm.usp_UpsertPlant;
IF OBJECT_ID(N'arm.usp_UpsertUnit', 'P')                IS NOT NULL DROP PROCEDURE arm.usp_UpsertUnit;
IF OBJECT_ID(N'arm.usp_UpsertOfflineEvent', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_UpsertOfflineEvent;
IF OBJECT_ID(N'arm.usp_UpsertPlantSummary', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_UpsertPlantSummary;
IF OBJECT_ID(N'arm.usp_UpsertUnitSummary', 'P')         IS NOT NULL DROP PROCEDURE arm.usp_UpsertUnitSummary;
IF OBJECT_ID(N'arm.usp_UpsertOfflineEventSummary', 'P') IS NOT NULL DROP PROCEDURE arm.usp_UpsertOfflineEventSummary;
IF OBJECT_ID(N'arm.usp_UpsertFileLog', 'P')             IS NOT NULL DROP PROCEDURE arm.usp_UpsertFileLog;
IF OBJECT_ID(N'arm.usp_ValidateLoad', 'P')              IS NOT NULL DROP PROCEDURE arm.usp_ValidateLoad;
GO

-- ============================================================================
-- 2) TVP TYPES. Safe to drop now that no procedure references them.
-- ============================================================================
RAISERROR('IIR teardown: dropping [arm] TVP types...', 10, 1) WITH NOWAIT;
GO
IF TYPE_ID(N'arm.OfflineEventTvp')        IS NOT NULL DROP TYPE arm.OfflineEventTvp;
IF TYPE_ID(N'arm.UnitTvp')                IS NOT NULL DROP TYPE arm.UnitTvp;
IF TYPE_ID(N'arm.PlantTvp')               IS NOT NULL DROP TYPE arm.PlantTvp;
IF TYPE_ID(N'arm.OfflineEventSummaryTvp') IS NOT NULL DROP TYPE arm.OfflineEventSummaryTvp;
IF TYPE_ID(N'arm.UnitSummaryTvp')         IS NOT NULL DROP TYPE arm.UnitSummaryTvp;
IF TYPE_ID(N'arm.PlantSummaryTvp')        IS NOT NULL DROP TYPE arm.PlantSummaryTvp;
GO

-- ============================================================================
-- 3) TABLES. FK-child first so no surviving table still references a table being
--    dropped:
--      Plant / Unit / OfflineEvent  (no outbound FKs)
--      FileLog                      -> (Endpoint, Status)
--      Endpoint / Status            (no outbound FKs)
-- ============================================================================
RAISERROR('IIR teardown: dropping [arm] tables (FK-child first)...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.Plant', 'U')                IS NOT NULL DROP TABLE arm.Plant;
IF OBJECT_ID(N'arm.Unit', 'U')                 IS NOT NULL DROP TABLE arm.Unit;
IF OBJECT_ID(N'arm.OfflineEvent', 'U')         IS NOT NULL DROP TABLE arm.OfflineEvent;
IF OBJECT_ID(N'arm.PlantSummary', 'U')         IS NOT NULL DROP TABLE arm.PlantSummary;
IF OBJECT_ID(N'arm.UnitSummary', 'U')          IS NOT NULL DROP TABLE arm.UnitSummary;
IF OBJECT_ID(N'arm.OfflineEventSummary', 'U')  IS NOT NULL DROP TABLE arm.OfflineEventSummary;
IF OBJECT_ID(N'arm.FileLog', 'U')              IS NOT NULL DROP TABLE arm.FileLog;
IF OBJECT_ID(N'arm.Endpoint', 'U')             IS NOT NULL DROP TABLE arm.Endpoint;
IF OBJECT_ID(N'arm.Status', 'U')               IS NOT NULL DROP TABLE arm.Status;
GO

-- ============================================================================
-- 4) SCHEMA [arm]. Dropped LAST and only when it exists AND holds no remaining
--    objects (sys.objects) or user-defined types (sys.types - TVP table types do
--    NOT appear in sys.objects). This protects a shared schema from being dropped
--    with foreign objects still bound to it. In the IIR DB, [arm] is IIR-only, so
--    after steps 1-3 it drops cleanly.
-- ============================================================================
RAISERROR('IIR teardown: evaluating [arm] schema drop...', 10, 1) WITH NOWAIT;
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
    RAISERROR('IIR teardown: schema [arm] dropped.', 10, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('IIR teardown: schema [arm] not dropped (absent, or still holds objects/types).', 10, 1) WITH NOWAIT;
END
GO

-- ----------------------------------------------------------------------------
-- Restore execution for the session (undoes the no-op guard if it fired).
-- ----------------------------------------------------------------------------
SET NOEXEC OFF;
GO

RAISERROR('IIR teardown complete.', 10, 1) WITH NOWAIT;
GO
