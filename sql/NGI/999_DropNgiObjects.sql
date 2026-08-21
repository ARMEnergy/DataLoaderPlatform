-- =============================================================================
-- 999_DropNgiObjects.sql
-- Loader:   NGI  (NGI Data Services - Bidweek natural-gas price survey)
-- Database: NGI            Schema: arm
-- TEARDOWN for every object created by
--   001_CreateNgiSchema.sql, 002_CreateNgiTvpTypes.sql, 003_CreateNgiProcedures.sql
-- (all in the [arm] schema) and finally the [arm] schema itself.
--
-- *** DESTRUCTIVE ***  This permanently DROPS tables and ALL their data
-- (arm.BidWeekData, arm.BidWeekLocation, arm.FileLog, arm.Endpoint, arm.Status),
-- the two TVP types and the four stored procedures. There is no recovery short of
-- a database restore. Run only when you intend to fully remove the NGI loader's
-- objects.
--
-- SCOPE / SAFETY:
--   * Targets the NGI DATABASE ONLY (Loaders:NGI:ConnectionString -> Database=NGI).
--     A top-of-script guard turns the whole run into a NO-OP (via SET NOEXEC ON)
--     when the NGI database does not exist, so it is safe on a fresh server.
--   * Does NOT drop the NGI database itself (out of scope, deliberately).
--   * Does NOT touch anything in the platform-owned [core] schema, and does not
--     touch core.Param - the NGI Username/Password rows there are the operator's
--     to remove, not this script's (and no credential value appears in any sql/
--     file).
--   * Drops ONLY NGI's own [arm]-schema objects. The final DROP SCHEMA is GUARDED
--     to fire only when [arm] holds no remaining objects or types, so a shared
--     schema is never disturbed (in the NGI DB, [arm] is NGI-only).
--
-- RUN ORDER:
--   * To fully UNDO 001-003: run THIS script (999).
--   * To REBUILD afterwards:  run 001 -> 002 -> 003 in order.
--   The 999 prefix sorts AFTER the ordered create scripts and is never mistaken
--   for one.
--
-- IDEMPOTENT / RE-RUNNABLE: every drop is guarded
--   (IF OBJECT_ID(...,'P'/'U') IS NOT NULL / IF TYPE_ID(...) IS NOT NULL / a schema
--   emptiness check). Running it twice, or against a server where NGI/arm was never
--   created, completes with no error.
--
-- DEPENDENCY-ORDERED DROP (reverse of the create order):
--   1) Procedures   (must precede the TVP types they take as READONLY parameters)
--   2) TVP types    (arm.BidWeekDataTvp, arm.BidWeekLocationTvp)
--   3) Tables, FK-child first:
--        arm.BidWeekData      (FK -> FileLog)
--        arm.BidWeekLocation  (FK -> FileLog)
--        arm.FileLog          (FK -> Endpoint, Status)
--        arm.Endpoint / arm.Status  (lookups; no outbound FKs)
--      NOTE: there is deliberately NO FK between arm.BidWeekData and
--      arm.BidWeekLocation (independent pipelines - see 001), so their relative
--      order here is immaterial; both simply precede arm.FileLog.
--   4) Schema [arm] (LAST; guarded on emptiness)
-- DROP TABLE removes each table's own FK constraints, so dropping children before
-- their parents keeps every drop unblocked with no explicit ALTER TABLE ... DROP
-- CONSTRAINT step.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Guard: if the NGI database is absent, turn the rest of the script into a
-- compile-only no-op. SET NOEXEC ON persists across GO for the session, so every
-- following batch (including USE NGI) is parsed but not executed and nothing
-- errors. SET NOEXEC OFF at the very end restores the session.
-- ----------------------------------------------------------------------------
IF DB_ID(N'NGI') IS NULL
BEGIN
    RAISERROR('NGI database not found - teardown is a no-op; nothing to drop.', 10, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

USE NGI;
GO

-- ============================================================================
-- 1) PROCEDURES. Dropped BEFORE the TVP types, because the two bulk-merge procs
--    take those types as READONLY parameters and a type cannot be dropped while a
--    procedure still references it.
-- ============================================================================
RAISERROR('NGI teardown: dropping [arm] stored procedures...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.usp_BulkMergeBidWeekData', 'P')     IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeBidWeekData;
IF OBJECT_ID(N'arm.usp_BulkMergeBidWeekLocation', 'P') IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeBidWeekLocation;
IF OBJECT_ID(N'arm.usp_UpsertFileLog', 'P')            IS NOT NULL DROP PROCEDURE arm.usp_UpsertFileLog;
IF OBJECT_ID(N'arm.usp_ValidateLoad', 'P')             IS NOT NULL DROP PROCEDURE arm.usp_ValidateLoad;
GO

-- ============================================================================
-- 2) TVP TYPES. Safe to drop now that no procedure references them.
-- ============================================================================
RAISERROR('NGI teardown: dropping [arm] TVP types...', 10, 1) WITH NOWAIT;
GO
IF TYPE_ID(N'arm.BidWeekDataTvp')     IS NOT NULL DROP TYPE arm.BidWeekDataTvp;
IF TYPE_ID(N'arm.BidWeekLocationTvp') IS NOT NULL DROP TYPE arm.BidWeekLocationTvp;
GO

-- ============================================================================
-- 3) TABLES. FK-child first, so no surviving table still references a table being
--    dropped:
--      BidWeekData     -> (FileLog)
--      BidWeekLocation -> (FileLog)
--      FileLog         -> (Endpoint, Status)
--      Endpoint / Status  (no outbound FKs)
-- ============================================================================
RAISERROR('NGI teardown: dropping [arm] tables (FK-child first)...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.BidWeekData', 'U')     IS NOT NULL DROP TABLE arm.BidWeekData;
IF OBJECT_ID(N'arm.BidWeekLocation', 'U') IS NOT NULL DROP TABLE arm.BidWeekLocation;
IF OBJECT_ID(N'arm.FileLog', 'U')         IS NOT NULL DROP TABLE arm.FileLog;
IF OBJECT_ID(N'arm.Endpoint', 'U')        IS NOT NULL DROP TABLE arm.Endpoint;
IF OBJECT_ID(N'arm.Status', 'U')          IS NOT NULL DROP TABLE arm.Status;
GO

-- ============================================================================
-- 4) SCHEMA [arm]. Dropped LAST, and only when it exists AND holds no remaining
--    objects (sys.objects) or user-defined types (sys.types - TVP table types do
--    NOT appear in sys.objects). This protects a shared schema from being dropped
--    with foreign objects still bound to it. In the NGI DB, [arm] is NGI-only, so
--    after steps 1-3 it drops cleanly.
-- ============================================================================
RAISERROR('NGI teardown: evaluating [arm] schema drop...', 10, 1) WITH NOWAIT;
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
    RAISERROR('NGI teardown: schema [arm] dropped.', 10, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('NGI teardown: schema [arm] not dropped (absent, or still holds objects/types).', 10, 1) WITH NOWAIT;
END
GO

-- ----------------------------------------------------------------------------
-- Restore execution for the session (undoes the no-op guard if it fired).
-- ----------------------------------------------------------------------------
SET NOEXEC OFF;
GO

RAISERROR('NGI teardown complete.', 10, 1) WITH NOWAIT;
GO
