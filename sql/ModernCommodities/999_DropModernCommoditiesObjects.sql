-- =============================================================================
-- 999_DropModernCommoditiesObjects.sql
-- Loader:   ModernCommodities  (Modern Commodities - trades & settlement prices)
-- Database: ModernCommodities            Schema: arm
-- TEARDOWN for every object created by
--   001_CreateModernCommoditiesSchema.sql,
--   002_CreateModernCommoditiesTvpTypes.sql,
--   003_CreateModernCommoditiesProcedures.sql
-- (all in the [arm] schema) and finally the [arm] schema itself.
--
-- *****************************************************************************
-- *** DESTRUCTIVE AND IRREVERSIBLE ***
-- This script PERMANENTLY DELETES ALL LOADED TRADE AND SETTLEMENT DATA.
-- DROP TABLE removes the rows with the table: every executed trade in
-- arm.AllTrades and arm.MyTrades, every settlement price in arm.Settlements,
-- and the whole arm.FileLog audit trail of which pulls ran and what they
-- returned. None of it is recoverable from within SQL Server - the only way
-- back is a database RESTORE, or a full re-load from the vendor API (and the
-- API's own history window may no longer reach far enough back to reproduce
-- what you dropped).
-- Run this ONLY when you deliberately intend to fully remove the
-- ModernCommodities loader's objects and their data.
-- *****************************************************************************
--
-- SCOPE / SAFETY:
--   * Targets the ModernCommodities DATABASE ONLY
--     (Loaders:ModernCommodities:ConnectionString -> Database=ModernCommodities).
--     A top-of-script guard turns the whole run into a NO-OP (via SET NOEXEC ON)
--     when that database does not exist, so it is safe on a fresh server. This
--     database-existence guard IS the house safety switch (matching
--     sql/NGI/999_DropNgiObjects.sql): connect to the wrong server and the
--     script does nothing rather than dropping someone else's objects.
--   * Does NOT drop the ModernCommodities database itself (out of scope,
--     deliberately).
--   * Does NOT touch anything in the platform-owned [core] schema. core.LoadLog,
--     core.LoaderRun and core.Param live in the PLATFORM database and are never
--     dropped, altered or emptied here. Clearing this loader's core.LoadLog rows
--     is a SEPARATE, MANUAL step - see the commented-out DELETE at the bottom of
--     this script. Likewise the loader's core.Param credential rows are the
--     operator's to remove, not this script's (no credential value appears in any
--     sql/ file).
--   * Drops ONLY ModernCommodities' own [arm]-schema objects. The final
--     DROP SCHEMA is GUARDED to fire only when [arm] holds no remaining objects
--     or types, so a shared schema is never disturbed (in the
--     ModernCommodities DB, [arm] is this loader's only).
--
-- RUN ORDER:
--   * To fully UNDO 001-003: run THIS script (999).
--   * To REBUILD afterwards:  run 001 -> 002 -> 003 in order.
--   The 999 prefix sorts AFTER the ordered create scripts and is never mistaken
--   for one.
--
-- IDEMPOTENT / RE-RUNNABLE: every drop is guarded
--   (IF OBJECT_ID(...,'P'/'U') IS NOT NULL / IF TYPE_ID(...) IS NOT NULL / a
--   schema emptiness check). Running it twice, running it against a partially
--   created database (say 001 ran but 003 did not), or running it where
--   ModernCommodities/[arm] never existed at all, completes with no error.
--
-- DEPENDENCY-ORDERED DROP (exact reverse of the create order):
--   1) Procedures   (must precede the TVP types they take as READONLY parameters)
--        arm.usp_ValidateLoad
--        arm.usp_BulkMergeSettlements
--        arm.usp_BulkMergeMyTrades
--        arm.usp_BulkMergeAllTrades
--        arm.usp_UpsertFileLog
--   2) TVP types
--        arm.SettlementsTvp
--        arm.TradesTvp
--   3) Tables, FK-child first:
--        arm.FileLog     (FK -> arm.Endpoint, arm.Status)  <- the only FK child
--        arm.Settlements (no FKs)
--        arm.MyTrades    (no FKs)
--        arm.AllTrades   (no FKs)
--        arm.Endpoint    (lookup; no outbound FKs)
--        arm.Status      (lookup; no outbound FKs)
--      NOTE: the three fact tables carry NO FileLogId and NO foreign keys (see
--      001), so their position relative to arm.FileLog is immaterial; only
--      arm.FileLog must precede arm.Endpoint and arm.Status.
--   4) Schema [arm] (LAST; guarded on emptiness)
--
-- DROP TABLE removes each table's own PRIMARY KEY, UNIQUE, CHECK, DEFAULT and
-- FOREIGN KEY constraints and all of its indexes along with it, so there is
-- deliberately NO separate drop for any of the following - they go with their
-- table:
--   arm.FileLog     : PK_FileLog, UQ_FileLog_Endpoint_Window_RunToken,
--                     FK_FileLog_Endpoint, FK_FileLog_Status,
--                     DF_FileLog_DateCreated, DF_FileLog_RowCount,
--                     DF_FileLog_DroppedRowCount,
--                     IX_FileLog_WindowEnd_EndpointId, IX_FileLog_LastCheckedUtc
--   arm.Settlements : PK_Settlements, DF_Settlements_ModifiedAtUtc,
--                     IX_Settlements_ModifiedAtUtc
--   arm.MyTrades    : PK_MyTrades, DF_MyTrades_ModifiedAtUtc,
--                     IX_MyTrades_LastUpdated, IX_MyTrades_SpreadTradeNumber,
--                     IX_MyTrades_ModifiedAtUtc
--   arm.AllTrades   : PK_AllTrades, DF_AllTrades_ModifiedAtUtc,
--                     IX_AllTrades_LastUpdated, IX_AllTrades_SpreadTradeNumber,
--                     IX_AllTrades_ModifiedAtUtc
--   arm.Endpoint    : PK_Endpoint, UQ_Endpoint_Name, DF_Endpoint_DateCreated
--   arm.Status      : PK_Status, UQ_Status_Name, CK_Status_Name,
--                     DF_Status_DateCreated
-- Dropping FK children before their parents keeps every drop unblocked with no
-- explicit ALTER TABLE ... DROP CONSTRAINT step.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Guard: if the ModernCommodities database is absent, turn the rest of the
-- script into a compile-only no-op. SET NOEXEC ON persists across GO for the
-- session, so every following batch (including USE ModernCommodities) is parsed
-- but not executed and nothing errors. SET NOEXEC OFF at the very end restores
-- the session.
-- ----------------------------------------------------------------------------
IF DB_ID(N'ModernCommodities') IS NULL
BEGIN
    RAISERROR('ModernCommodities database not found - teardown is a no-op; nothing to drop.', 10, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

USE ModernCommodities;
GO

RAISERROR('*** ModernCommodities teardown: DESTRUCTIVE - this permanently deletes ALL loaded trade and settlement data. ***', 10, 1) WITH NOWAIT;
GO

-- ============================================================================
-- 1) PROCEDURES. Dropped BEFORE the TVP types, because the three bulk-merge
--    procs take those types as READONLY parameters and a type cannot be dropped
--    while a procedure still references it.
-- ============================================================================
RAISERROR('ModernCommodities teardown: dropping [arm] stored procedures...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.usp_ValidateLoad', 'P')         IS NOT NULL DROP PROCEDURE arm.usp_ValidateLoad;
IF OBJECT_ID(N'arm.usp_BulkMergeSettlements', 'P') IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeSettlements;
IF OBJECT_ID(N'arm.usp_BulkMergeMyTrades', 'P')    IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeMyTrades;
IF OBJECT_ID(N'arm.usp_BulkMergeAllTrades', 'P')   IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeAllTrades;
IF OBJECT_ID(N'arm.usp_UpsertFileLog', 'P')        IS NOT NULL DROP PROCEDURE arm.usp_UpsertFileLog;
GO

-- ============================================================================
-- 2) TVP TYPES. Safe to drop now that no procedure references them.
--    arm.TradesTvp is shared by usp_BulkMergeMyTrades and usp_BulkMergeAllTrades
--    (identical payload shape), so BOTH had to go first - step 1 handled that.
-- ============================================================================
RAISERROR('ModernCommodities teardown: dropping [arm] TVP types...', 10, 1) WITH NOWAIT;
GO
IF TYPE_ID(N'arm.SettlementsTvp') IS NOT NULL DROP TYPE arm.SettlementsTvp;
IF TYPE_ID(N'arm.TradesTvp')      IS NOT NULL DROP TYPE arm.TradesTvp;
GO

-- ============================================================================
-- 3) TABLES. FK-child first, so no surviving table still references a table
--    being dropped:
--      FileLog                        -> (Endpoint, Status)
--      Settlements / MyTrades / AllTrades  (facts; no FileLogId, no FKs)
--      Endpoint / Status                   (lookups; no outbound FKs)
--    This is the step that destroys the loaded data.
-- ============================================================================
RAISERROR('ModernCommodities teardown: dropping [arm] tables (FK-child first) - DATA LOSS...', 10, 1) WITH NOWAIT;
GO
IF OBJECT_ID(N'arm.FileLog', 'U')     IS NOT NULL DROP TABLE arm.FileLog;
IF OBJECT_ID(N'arm.Settlements', 'U') IS NOT NULL DROP TABLE arm.Settlements;
IF OBJECT_ID(N'arm.MyTrades', 'U')    IS NOT NULL DROP TABLE arm.MyTrades;
IF OBJECT_ID(N'arm.AllTrades', 'U')   IS NOT NULL DROP TABLE arm.AllTrades;
IF OBJECT_ID(N'arm.Endpoint', 'U')    IS NOT NULL DROP TABLE arm.Endpoint;
IF OBJECT_ID(N'arm.Status', 'U')      IS NOT NULL DROP TABLE arm.Status;
GO

-- ============================================================================
-- 4) SCHEMA [arm]. Dropped LAST, and only when it exists AND holds no remaining
--    objects (sys.objects) or user-defined types (sys.types - TVP table types do
--    NOT appear in sys.objects). This protects a shared schema from being
--    dropped with foreign objects still bound to it. In the ModernCommodities
--    DB, [arm] belongs to this loader alone, so after steps 1-3 it drops
--    cleanly.
-- ============================================================================
RAISERROR('ModernCommodities teardown: evaluating [arm] schema drop...', 10, 1) WITH NOWAIT;
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
    RAISERROR('ModernCommodities teardown: schema [arm] dropped.', 10, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('ModernCommodities teardown: schema [arm] not dropped (absent, or still holds objects/types).', 10, 1) WITH NOWAIT;
END
GO

-- ----------------------------------------------------------------------------
-- Restore execution for the session (undoes the no-op guard if it fired).
-- ----------------------------------------------------------------------------
SET NOEXEC OFF;
GO

RAISERROR('ModernCommodities teardown complete.', 10, 1) WITH NOWAIT;
GO

-- ============================================================================
-- OPTIONAL, MANUAL, SEPARATE STEP - platform [core] bookkeeping.
--
-- This script deliberately does NOT touch the platform-owned [core] schema:
-- core.LoadLog, core.LoaderRun and core.Param live in the PLATFORM database
-- (Platform:LoadLogConnectionString), not in ModernCommodities, and they are
-- shared by every loader.
--
-- After a teardown, this loader's core.LoadLog rows are stale: they still record
-- work units as SUCCEEDED, so a rebuilt-and-re-run loader would SKIP them as
-- already loaded and the dropped tables would stay empty. If you intend to
-- re-load from scratch, connect to the PLATFORM database and run the DELETE
-- below by hand. Uncomment deliberately - it discards this loader's resume
-- history for every work unit it has ever run.
--
--   DELETE FROM core.LoadLog WHERE LoaderId = 'ModernCommodities';
--
-- core.LoaderRun rows (one per host invocation) are shared history and are best
-- left alone. core.Param credential rows for ModernCommodities are the
-- operator's to remove if the loader is being retired for good.
-- ============================================================================
