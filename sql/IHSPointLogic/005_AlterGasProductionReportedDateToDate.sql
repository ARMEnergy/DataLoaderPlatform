-- =============================================================================
-- 005_AlterGasProductionReportedDateToDate.sql
--
-- Migrates arm.GasProductionProducingArea.ReportedDate from DATETIME2(0) to DATE.
-- ReportedDate leads the PRIMARY KEY, so this rebuilds the clustered PK and the
-- table's TVP type.
--
-- WHY. The vendor sends 'yyyy-MM-dd HH:mm', but the time is a PUBLICATION STAMP
-- (observed 22:02, 00:02, 12:02 — clock-like, not business data). Keeping it in
-- the key meant two publications of the SAME reported day landed as two rows
-- instead of one updating the other. DATE also aligns this column with the
-- incumbent dbo.GasProduction_ProducingArea, whose ReportedDate is ALREADY DATE
-- — so 004's comparison join was matching DATE against DATETIME2 through an
-- implicit conversion.
--
-- THIS IS A ONE-TIME MIGRATION for a database that already has 001-003 applied.
-- A fresh install does NOT need it: 001 and 002 now declare DATE directly.
--
-- RUN ORDER (all four steps, loader DISABLED for the duration):
--     1. this script  (005)
--     2. 002_CreateIHSPointLogicTvpTypes.sql    -- recreates the dropped TVP type
--     3. 003_CreateIHSPointLogicProcedures.sql  -- recreates the dropped merge proc
--     4. scripts\Generate-ArmToDboComparison.ps1 -- regenerates 004 from the catalog
--
-- Between step 1 and step 3 the merge proc does not exist and the loader WILL
-- fail if it runs. Run the steps back to back.
--
-- IDEMPOTENT. Every mutating batch re-tests the column type for itself. RETURN
-- would NOT be enough: it exits only its own batch, so the batches after the
-- next GO would still run and drop the proc and rebuild the PK a second time.
-- =============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

-- ----------------------------------------------------------------------------
-- Step 1 — pre-flight. Collapsing the time component merges any rows that share
-- (day, ReferenceDate, Region, ProducingArea, State) but differ only by HH:mm.
-- Those rows CANNOT survive the new PK. Abort rather than silently lose them.
--
-- Verified 0 collisions against production on 2026-10-05 (24,005 rows,
-- 24,005 distinct keys), but that is a property of the data on a given day, not
-- a guarantee — so it is checked here every time.
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('arm.GasProductionProducingArea')
              AND c.name = 'ReportedDate' AND t.name = 'datetime2')
BEGIN
    DECLARE @Collisions INT, @RowsLost INT;

    SELECT @Collisions = COUNT(*), @RowsLost = ISNULL(SUM(Cnt - 1), 0)
      FROM (
            SELECT COUNT(*) AS Cnt
              FROM arm.GasProductionProducingArea
             GROUP BY CAST(ReportedDate AS DATE), ReferenceDate, Region, ProducingArea, [State]
            HAVING COUNT(*) > 1
           ) AS g;

    IF @Collisions > 0
    BEGIN
        DECLARE @msg NVARCHAR(400) = CONCAT(
            'ABORTED: collapsing ReportedDate to DATE would merge ', @Collisions,
            ' key group(s) and destroy ', @RowsLost, ' row(s). Decide which publication wins ',
            '(normally the latest ReportedDate) and de-duplicate BEFORE re-running this script.');
        THROW 50005, @msg, 1;
    END

    PRINT CONCAT('Pre-flight OK — no key collisions. Rows: ',
                 (SELECT COUNT(*) FROM arm.GasProductionProducingArea));
END
ELSE
    PRINT 'ReportedDate is not datetime2 — migration already applied or table absent. Skipping.';
GO

-- ----------------------------------------------------------------------------
-- Step 2 — drop the merge proc, then the TVP type it binds.
-- A table type cannot be dropped while a procedure references it, so the order
-- here is mandatory. Both are recreated by re-running 002 and 003.
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('arm.GasProductionProducingArea')
              AND c.name = 'ReportedDate' AND t.name = 'datetime2')
BEGIN
    IF OBJECT_ID('arm.usp_BulkMergeGasProductionProducingArea', 'P') IS NOT NULL
        DROP PROCEDURE arm.usp_BulkMergeGasProductionProducingArea;

    IF TYPE_ID('arm.GasProductionProducingAreaTvp') IS NOT NULL
        DROP TYPE arm.GasProductionProducingAreaTvp;

    PRINT 'Dropped arm.usp_BulkMergeGasProductionProducingArea and arm.GasProductionProducingAreaTvp.';
END
GO

-- ----------------------------------------------------------------------------
-- Step 3 — rebuild the key around the narrowed column.
--
-- The nonclustered IX_GasProductionProducingArea_FileLogId is rebuilt
-- automatically when the clustered PK is dropped and recreated — it needs no
-- action. FK_GasProductionProducingArea_FileLog is an OUTGOING key on FileLogId
-- and does not reference the PK, so it does not block the ALTER.
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('arm.GasProductionProducingArea')
              AND c.name = 'ReportedDate' AND t.name = 'datetime2')
BEGIN
    BEGIN TRANSACTION;

    ALTER TABLE arm.GasProductionProducingArea
        DROP CONSTRAINT PK_GasProductionProducingArea;

    ALTER TABLE arm.GasProductionProducingArea
        ALTER COLUMN ReportedDate DATE NOT NULL;

    ALTER TABLE arm.GasProductionProducingArea
        ADD CONSTRAINT PK_GasProductionProducingArea
            PRIMARY KEY CLUSTERED (ReportedDate, ReferenceDate, Region, ProducingArea, [State]);

    COMMIT TRANSACTION;

    PRINT 'ReportedDate altered to DATE and PK_GasProductionProducingArea rebuilt.';
END
GO

-- ----------------------------------------------------------------------------
-- Step 4 — verify, and remind the operator the job is not finished.
-- ----------------------------------------------------------------------------
SELECT t.name                                                AS ReportedDateType,
       (SELECT COUNT(*) FROM arm.GasProductionProducingArea)  AS RowsRetained
  FROM sys.columns c
  JOIN sys.types   t ON t.user_type_id = c.user_type_id
 WHERE c.object_id = OBJECT_ID('arm.GasProductionProducingArea')
   AND c.name      = 'ReportedDate';

PRINT 'NOW RUN: 002 (recreates the TVP type), then 003 (recreates the merge proc),';
PRINT 'then scripts\Generate-ArmToDboComparison.ps1 (regenerates 004).';
PRINT 'The loader cannot merge until 002 and 003 have been re-run.';
GO
