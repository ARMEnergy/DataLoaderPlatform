-- =============================================================================
-- 999_DropOpisObjects.sql
-- Database : OPIS
-- Schema   : arm
--
-- Guarded, idempotent teardown of EVERYTHING 001-003 create, in reverse
-- dependency order:
--      procedures -> TVP types -> tables (FK-child first) -> schema
--
--   * Every drop is guarded, so this is safe to re-run and safe to run where
--     nothing was ever created.
--   * Targets the OPIS database ONLY. It does NOT drop the database itself and
--     does NOT touch anything in the platform-owned [core] schema.
--   * The schema drop fires only when arm holds no remaining objects or types.
--
-- DESTRUCTIVE: this deletes loaded data. Intended for a rebuild of a dev/test
-- database, not for production use.
-- =============================================================================

-- ---- 1) Procedures ----------------------------------------------------------
IF OBJECT_ID(N'arm.usp_ValidateLoad', 'P')      IS NOT NULL DROP PROCEDURE arm.usp_ValidateLoad;
IF OBJECT_ID(N'arm.usp_BulkMergeLPReport', 'P') IS NOT NULL DROP PROCEDURE arm.usp_BulkMergeLPReport;
IF OBJECT_ID(N'arm.usp_UpsertFileLog', 'P')     IS NOT NULL DROP PROCEDURE arm.usp_UpsertFileLog;
GO

-- ---- 2) TVP types (must come after the procedures that reference them) ------
IF TYPE_ID(N'arm.LPReportTvp') IS NOT NULL DROP TYPE arm.LPReportTvp;
GO

-- ---- 3) Tables, FK-children first -------------------------------------------
--        LPReport / LPReportHistory -> arm.FileLog -> arm.Status
IF OBJECT_ID(N'arm.LPReportHistory', 'U') IS NOT NULL DROP TABLE arm.LPReportHistory;
IF OBJECT_ID(N'arm.LPReport', 'U')        IS NOT NULL DROP TABLE arm.LPReport;
IF OBJECT_ID(N'arm.FileLog', 'U')         IS NOT NULL DROP TABLE arm.FileLog;
IF OBJECT_ID(N'arm.Status', 'U')          IS NOT NULL DROP TABLE arm.Status;
GO

-- ---- 4) Schema, only once it is empty ---------------------------------------
IF EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
   AND NOT EXISTS (SELECT 1 FROM sys.objects AS o
                   JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                   WHERE s.[name] = 'arm')
   AND NOT EXISTS (SELECT 1 FROM sys.types AS t
                   JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                   WHERE s.[name] = 'arm')
BEGIN
    EXEC ('DROP SCHEMA arm;');
END
GO
