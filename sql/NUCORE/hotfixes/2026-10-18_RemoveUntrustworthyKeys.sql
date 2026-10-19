-- =====================================================================================
-- NUCORE hotfix 2026-10-18 - remove the two primary keys that do not identify a row
--
-- WHY
--   PositionDetails failed in production with:
--       Violation of PRIMARY KEY constraint 'PK_PositionDetails'.
--       duplicate key value is (2026-08-24, 1202, 2529, 202510)
--
--   Its key (SystemDate, TradeId, DetailId, TradeMonth) had been "verified unique" over
--   100,000 rows - which were the first page of a TRADE-ORDERED response and covered only
--   130 distinct trades. Re-verified over five COMPLETE trade months (443,477 rows) that
--   key has 45,151 duplicates, and no extension rescues it:
--       + specId                              2,189 duplicates
--       + specId, priceType, settlementIndex    129 duplicates
--       + productId as well                     129 duplicates
--   The survivors are NOT exact duplicates - one group holds 17 distinct settlement rows
--   at 17 different contract prices, differing only in measures. De-duplicating would
--   destroy real realized P&L.
--
--   PnlExplain's key (CurDate, TradeId, DetailId, TradeMonth, ProductSpec) fails the same
--   way, though less often: 2025-10-15 carries 19 BYTE-IDENTICAL copies of one row.
--
--   Both are therefore written by REPLACE now, not MERGE:
--       PnlExplain       delete + insert per (CurDate)
--       PositionDetails  delete + insert per (TradeMonth, SystemDate)
--
--   VarResults and Volatilities KEEP their keys - re-verified over 7 and 5 complete days
--   respectively (712,004 rows for Volatilities) with zero duplicates. Their tables and
--   data are untouched by this script.
--
-- A SECOND, SEPARATE BUG - SILENT THIS TIME
--   The loader sent SortFields only for PositionDetails. Paging this API WITHOUT a sort is
--   not stable: pages 1 and 2 of one Volatilities day at pageSize=20000 shared 111 rows,
--   which also means ~111 rows of that day were never returned at all. Sorted, the same two
--   pages overlap by zero.
--
--   Measured against the live API, the loaded Volatilities data is short by ~2.6% per day:
--       2026-10-14   DB 144,107   API 147,908   missing 3,801
--       2026-09-30   DB 144,007   API 147,770   missing 3,763
--   across all 64 days loaded. Nothing failed; the rows were simply never fetched.
--
--   Volatilities is the only table affected, because it is the only one that needs more
--   than one page per unit (147,908 rows/day at pageSize=20000). VarResults (9,378/day) and
--   PositionSummary (13,555/day) are single-page and were verified complete against the API.
--
-- WHAT THIS DOES
--   * drops and recreates dbo.PnlExplain and dbo.PositionDetails, which change shape
--     (natural-key PK -> surrogate identity PK + clustered index on the replace scope);
--   * TRUNCATEs dbo.Volatilities, whose shape is fine but whose data is incomplete;
--   * renames PositionSummary's clustered index to the name the generator now emits;
--   * leaves dbo.VarResults and dbo.PositionSummary data completely alone - both were
--     verified complete against the API.
--
-- ⚠ THIS DELETES THE LOADED PnlExplain, PositionDetails AND Volatilities DATA. All three
--   must be reloaded. For the first two that is unavoidable - the tables change shape. For
--   Volatilities it is a choice, but the alternative is keeping 64 days that are each
--   missing ~2.6% of their rows, with nothing in the data to show which ones.
--
-- ⚠ Re-running sql/NUCORE/001 WITHOUT this script is a silent no-op: 001 is creation-
--   guarded with IF OBJECT_ID(...) IS NULL, so it will not reshape an existing table.
--
-- AFTER RUNNING THIS
--   1. sql/NUCORE/001_CreateNucoreTables.sql      (recreates the two tables)
--   2. sql/NUCORE/002_CreateNucoreTvpTypes.sql    (no-op unless a TVP changed)
--   3. sql/NUCORE/003_CreateNucoreProcedures.sql  (ALTERs every proc - always re-run)
--   4. delete the PnlExplain / PositionDetails rows from core.LoadLog on the platform
--      database, or their settled work units will be skipped as "already loaded" and the
--      tables will stay empty. See the final comment block.
-- =====================================================================================

USE [NUCORE];
GO

SET XACT_ABORT ON;
GO

-- -------------------------------------------------------------------------------------
-- 1. Record what is about to be lost, so the reload can be checked against it.
-- -------------------------------------------------------------------------------------
PRINT '--- row counts before the hotfix ---';

SELECT  t.name                        AS TableName,
        SUM(p.rows)                   AS Rows
FROM    sys.tables     AS t
JOIN    sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE   t.name IN (N'PnlExplain', N'PositionDetails', N'PositionSummary',
                   N'VarResults', N'Volatilities')
GROUP BY t.name
ORDER BY t.name;
GO

-- -------------------------------------------------------------------------------------
-- 2. PnlExplain and PositionDetails: drop. 001 recreates them in the new shape.
--
--    Every statement names [dbo] explicitly. This database holds only this loader's
--    tables today, but the habit is what stops a drop script reaching past its own
--    objects (see sql/CWG/999 in CLAUDE.md for what that costs when it goes wrong).
-- -------------------------------------------------------------------------------------
PRINT '--- dropping dbo.PnlExplain and dbo.PositionDetails ---';

DROP TABLE IF EXISTS [dbo].[PnlExplain];
DROP TABLE IF EXISTS [dbo].[PositionDetails];
GO

-- -------------------------------------------------------------------------------------
-- 2b. Volatilities: the TABLE is correct, the DATA is not. Every day loaded so far was
--     paged without a sort and is missing roughly 2.6% of its rows. TRUNCATE rather than
--     DROP, so the table, its key and its indexes stay exactly as they are.
-- -------------------------------------------------------------------------------------
IF OBJECT_ID(N'[dbo].[Volatilities]', N'U') IS NOT NULL
BEGIN
    PRINT '--- truncating dbo.Volatilities (incomplete: unsorted paging) ---';
    TRUNCATE TABLE [dbo].[Volatilities];
END
GO

-- -------------------------------------------------------------------------------------
-- 3. PositionSummary: the table is already correct, but its clustered index carries the
--    old name. 001 would try to create a SECOND clustered index under the new name and
--    fail with "Cannot create more than one clustered index on table ...", so rename it
--    rather than leaving 001 to collide.
-- -------------------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'CIX_PositionSummary_SystemDate'
             AND object_id = OBJECT_ID(N'[dbo].[PositionSummary]'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'CIX_PositionSummary'
                     AND object_id = OBJECT_ID(N'[dbo].[PositionSummary]'))
BEGIN
    PRINT '--- renaming CIX_PositionSummary_SystemDate -> CIX_PositionSummary ---';
    EXEC sp_rename
        @objname = N'[dbo].[PositionSummary].[CIX_PositionSummary_SystemDate]',
        @newname = N'CIX_PositionSummary',
        @objtype = N'INDEX';
END
GO

-- -------------------------------------------------------------------------------------
-- 4. The two procedures are re-created by 003, but drop them here as well so that a
--    partially-applied migration cannot leave a proc referencing a table that no longer
--    matches it.
-- -------------------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS [dbo].[usp_MergePnlExplain];
DROP PROCEDURE IF EXISTS [dbo].[usp_MergePositionDetails];
DROP PROCEDURE IF EXISTS [dbo].[usp_GetPositionDetailsWatermark];
GO

-- The TVP types are unchanged (the column list did not move), so they are deliberately
-- NOT dropped - dropping a type that a surviving proc still references would fail.
-- 002 is idempotent and re-running it is a no-op.

PRINT '--- hotfix complete. Now run 001, then 002, then 003. ---';
GO

-- =====================================================================================
-- 5. AFTERWARDS, on the PLATFORM database (DataLoaderPlatform), not this one.
--
--    The loader skips a settled work unit whose key is already recorded as loaded. The
--    PnlExplain and PositionDetails units from the failed run are recorded, so without
--    this the reload would skip almost everything and leave both tables near-empty.
--
--    Run this against DataLoaderPlatform, NOT against NUCORE:
--
--        USE [DataLoaderPlatform];
--        GO
--        DELETE FROM core.LoadLog
--        WHERE  LoaderId = 'NUCORE'
--          AND (WorkUnitKey LIKE 'PnlExplain;%'
--            OR WorkUnitKey LIKE 'PositionDetails;%'
--            OR WorkUnitKey LIKE 'Volatilities;%');
--
--    VarResults units are deliberately NOT cleared: that table is single-page per unit,
--    was verified complete against the API, and re-reading ~470 days of it would buy
--    nothing. PositionSummary likewise.
-- =====================================================================================
