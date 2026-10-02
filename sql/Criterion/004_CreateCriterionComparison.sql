-- =============================================================================
-- 004_CreateCriterionComparison.sql
-- arm.usp_CompareArmToDbo - the arm/dbo reconciliation every other loader gets.
--
-- Criterion HAS NO COMPARISON TO MAKE. Verified against the live catalog on
-- 2026-09-28: the Criterion database contains 9 [arm] tables and **zero [dbo]
-- tables**. There is no incumbent dataset here to reconcile against, because this
-- loader did not replace an existing Conduit feed - it is the first thing ever to
-- write this database.
--
--   SELECT s.name, COUNT(*)
--   FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
--   GROUP BY s.name;
--   -->  arm  9      (dbo returns no row at all)
--
-- The proc is still created, so that `EXEC arm.usp_CompareArmToDbo` behaves the
-- same way in every loader database rather than failing with "could not find stored
-- procedure" here. It returns one row in the standard shape explaining the position.
--
-- If a dbo dataset is ever introduced into this database, regenerate this file with
--   scripts\Generate-ArmToDboComparison.ps1
-- after adding Criterion to the $Loaders list in that script. Do not hand-write the
-- pair list - the generator resolves it from the live catalog so it cannot be
-- invented.
--
-- Run 001-003 first. This script assumes Criterion is the current database.
-- =============================================================================

USE Criterion;
GO

CREATE OR ALTER PROCEDURE arm.usp_CompareArmToDbo
    @Date            DATE    = NULL,
    @TablePair       SYSNAME = NULL,
    @MaxRowsPerIssue INT     = 1000
AS
BEGIN
    SET NOCOUNT ON;

    -- Re-checked at run time rather than asserted from a comment: if someone adds a
    -- dbo table later, this proc says so instead of quietly continuing to claim there
    -- is nothing to compare.
    DECLARE @DboTables INT =
    (
        SELECT COUNT(*)
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'dbo'
    );

    SELECT
        CAST('(none)' AS SYSNAME)         AS TablePair,
        CAST('NOT_APPLICABLE' AS VARCHAR(16)) AS Issue,
        CAST(CASE
                WHEN @DboTables = 0
                    THEN N'The Criterion database has no [dbo] tables - there is no incumbent dataset to reconcile against.'
                ELSE CONCAT(N'The [dbo] schema now holds ', @DboTables,
                            N' table(s). Regenerate this file with scripts\Generate-ArmToDboComparison.ps1.')
             END AS NVARCHAR(512))        AS KeyValues,
        CAST(NULL AS SYSNAME)             AS ColumnName,
        CAST(CONCAT((SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'arm'),
                    N' arm table(s)') AS NVARCHAR(256)) AS ArmValue,
        CAST(CONCAT(@DboTables, N' dbo table(s)') AS NVARCHAR(256)) AS DboValue;
END
GO
