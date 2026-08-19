-- =============================================================================
-- 999_DropAgsiObjects.sql
-- TEARDOWN for the GIE AGSI loader. Drops every object created by
--   001_CreateAgsiSchema.sql, 002_CreateAgsiTvpTypes.sql, 003_CreateAgsiProcedures.sql
-- (all in the [arm] schema) and finally the [arm] schema itself.
--
-- *** DESTRUCTIVE ***  This permanently DROPS tables and ALL their data
-- (arm.GasStorage, arm.GasStorageEntity, arm.FileLog, arm.Endpoint, arm.Status,
-- arm.Region), the two TVP types, the five/six stored procedures, and the [arm]
-- schema. There is no recovery short of a database restore. Run only when you
-- intend to fully remove the AGSI loader's objects.
--
-- SCOPE / SAFETY:
--   * Targets the AGSI DATABASE ONLY (Loaders:AGSI:ConnectionString -> Database=AGSI).
--     A top-of-script guard makes the whole run a NO-OP (via SET NOEXEC ON) when the
--     AGSI database does not exist, so it is safe to run against a fresh server.
--   * Does NOT drop the AGSI database itself (out of scope).
--   * Does NOT touch anything in the platform-owned [core] schema.
--   * Drops ONLY AGSI's own [arm]-schema objects. The final DROP SCHEMA is GUARDED
--     to fire only when [arm] holds no remaining objects or types, so a shared schema
--     (not the case in the AGSI DB, where [arm] is AGSI-only) is never disturbed.
--
-- RUN ORDER:
--   * To fully UNDO 001-003: run THIS script (999).
--   * To REBUILD afterwards:  run 001 -> 002 -> 003 in order.
--   The 999 prefix sorts AFTER the ordered create scripts and is never mistaken for one.
--
-- IDEMPOTENT / RE-RUNNABLE: every drop is guarded
--   (IF OBJECT_ID(...,'P'/'U') IS NOT NULL / IF TYPE_ID(...) IS NOT NULL / schema
--   emptiness check). Running it twice, or against a DB where AGSI/arm was never
--   created, completes with no error.
--
-- DEPENDENCY-ORDERED DROP (reverse of the create order):
--   1) Procedures      (must precede the TVP types they reference as parameters)
--   2) TVP types       (arm.GasStorageTvp, arm.GasStorageEntityTvp)
--   3) Tables, FK-child first:
--        arm.GasStorage        (FK -> GasStorageEntity, FK -> FileLog)
--        arm.GasStorageEntity  (FK -> FileLog)
--        arm.FileLog           (FK -> Endpoint, Region, Status)
--        arm.Endpoint / arm.Status / arm.Region   (lookups; no outbound FKs)
--   4) Schema [arm]    (LAST; guarded on emptiness)
-- DROP TABLE removes each table's own FK constraints, so dropping children before
-- their parents keeps every drop unblocked without an explicit ALTER TABLE ... DROP
-- CONSTRAINT step.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Guard: if the AGSI database is absent, turn the rest of the script into a
-- compile-only no-op. SET NOEXEC ON persists across GO for the session; every
-- following batch (including USE AGSI) is parsed but not executed, so nothing
-- errors. SET NOEXEC OFF at the very end restores the session.
-- ----------------------------------------------------------------------------
IF DB_ID(N'AGSI') IS NULL
BEGIN
    RAISERROR('AGSI database not found - teardown is a no-op; nothing to drop.', 10, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

USE AGSI;
GO

-- ============================================================================
-- 1) PROCEDURES. Dropped BEFORE the TVP types because the two bulk-merge procs
--    take those types as READONLY parameters (a type cannot be dropped while a
--    proc references it). Includes the pre-rename read proc name for older deploys.
-- ============================================================================
RAISERROR('AGSI teardown: dropping [arm] stored procedures...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.usp_BulkMergeGasStorage', 'P')       IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeGasStorage;
IF OBJECT_ID(N'arm.usp_BulkMergeGasStorageEntity', 'P') IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeGasStorageEntity;
IF OBJECT_ID(N'arm.usp_UpsertFileLog', 'P')             IS NOT NULL DROP PROCEDURE arm.usp_UpsertFileLog;
IF OBJECT_ID(N'arm.usp_ValidateLoad', 'P')              IS NOT NULL DROP PROCEDURE arm.usp_ValidateLoad;
IF OBJECT_ID(N'arm.usp_GetGasStorageEntities', 'P')     IS NOT NULL DROP PROCEDURE arm.usp_GetGasStorageEntities;
-- Pre-rename name (superseded by usp_GetGasStorageEntities) - dropped for completeness
-- on an older deploy that still has it.
IF OBJECT_ID(N'arm.usp_GetGasStorageEntityCodes', 'P')  IS NOT NULL DROP PROCEDURE arm.usp_GetGasStorageEntityCodes;
GO

-- ============================================================================
-- 2) TVP TYPES. Safe to drop now that no procedure references them.
-- ============================================================================
RAISERROR('AGSI teardown: dropping [arm] TVP types...', 10, 1) WITH NOWAIT;
GO
IF TYPE_ID(N'arm.GasStorageTvp')       IS NOT NULL DROP TYPE arm.GasStorageTvp;
IF TYPE_ID(N'arm.GasStorageEntityTvp') IS NOT NULL DROP TYPE arm.GasStorageEntityTvp;
GO

-- ============================================================================
-- 3) TABLES. FK-child first so no surviving table still references a table being
--    dropped:
--      GasStorage       -> (GasStorageEntity, FileLog)
--      GasStorageEntity -> (FileLog)
--      FileLog          -> (Endpoint, Region, Status)
--      Endpoint / Status / Region  (no outbound FKs)
-- ============================================================================
RAISERROR('AGSI teardown: dropping [arm] tables (FK-child first)...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.GasStorage', 'U')       IS NOT NULL DROP TABLE arm.GasStorage;
IF OBJECT_ID(N'arm.GasStorageEntity', 'U') IS NOT NULL DROP TABLE arm.GasStorageEntity;
IF OBJECT_ID(N'arm.FileLog', 'U')          IS NOT NULL DROP TABLE arm.FileLog;
IF OBJECT_ID(N'arm.Endpoint', 'U')         IS NOT NULL DROP TABLE arm.Endpoint;
IF OBJECT_ID(N'arm.Status', 'U')           IS NOT NULL DROP TABLE arm.Status;
IF OBJECT_ID(N'arm.Region', 'U')           IS NOT NULL DROP TABLE arm.Region;
GO

-- ============================================================================
-- 4) SCHEMA [arm]. Dropped LAST and only when it exists AND holds no remaining
--    objects (sys.objects) or user-defined types (sys.types - TVP table types do
--    NOT appear in sys.objects). This protects a shared schema from being dropped
--    with foreign objects still bound to it. In the AGSI DB, [arm] is AGSI-only,
--    so after steps 1-3 it drops cleanly.
-- ============================================================================
RAISERROR('AGSI teardown: evaluating [arm] schema drop...', 10, 1) WITH NOWAIT;
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
    RAISERROR('AGSI teardown: schema [arm] dropped.', 10, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('AGSI teardown: schema [arm] not dropped (absent, or still holds objects/types).', 10, 1) WITH NOWAIT;
END
GO

-- ----------------------------------------------------------------------------
-- Restore execution for the session (undoes the no-op guard if it fired).
-- ----------------------------------------------------------------------------
SET NOEXEC OFF;
GO

RAISERROR('AGSI teardown complete.', 10, 1) WITH NOWAIT;
GO
