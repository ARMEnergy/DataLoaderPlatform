-- =============================================================================
-- 999_DropCmeObjects.sql
-- Database : CMEGroup
-- Schema   : arm
--
-- Tear down everything 001-003 create, in dependency order:
--     procedures -> table types -> tables -> schema
--
-- A table type cannot be ALTERed, so changing a TVP column means running this
-- (or at least its procedure + type sections) and then re-running 002 and 003.
--
-- DESTRUCTIVE. This drops the loaded data. Guarded with IF EXISTS so it is
-- re-runnable, but nothing here asks for confirmation.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- 1) Procedures -- must go first: they hold references to the table types.
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeStlbasicFuture;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeStlbasicOption;
DROP PROCEDURE IF EXISTS arm.usp_UpsertFileLog;
GO

-- ----------------------------------------------------------------------------
-- 2) Table types.
-- ----------------------------------------------------------------------------
DROP TYPE IF EXISTS arm.STLBASIC_FutureTvp;
DROP TYPE IF EXISTS arm.STLBASIC_OptionTvp;
GO

-- ----------------------------------------------------------------------------
-- 3) Tables -- fact tables first, then the audit hub, then the lookup it
--    references. The fact tables carry no FK to arm.FileLog (the supplied DDL
--    has no provenance column at all), so only the FileLog -> Status FK
--    constrains the order here.
-- ----------------------------------------------------------------------------
DROP TABLE IF EXISTS arm.STLBASIC_Future;
DROP TABLE IF EXISTS arm.STLBASIC_Option;
DROP TABLE IF EXISTS arm.FileLog;
DROP TABLE IF EXISTS arm.Status;
GO

-- ----------------------------------------------------------------------------
-- 4) Schema -- only if this script emptied it. Another loader sharing the same
--    database would leave objects behind, and dropping a non-empty schema fails
--    anyway, so the guard keeps the script re-runnable rather than protective.
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
   AND NOT EXISTS (
        SELECT 1
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE s.[name] = 'arm'
   )
   AND NOT EXISTS (
        SELECT 1
        FROM sys.types t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.[name] = 'arm' AND t.is_table_type = 1
   )
    EXEC ('DROP SCHEMA arm;');
GO
