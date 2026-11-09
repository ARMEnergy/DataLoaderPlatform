-- =====================================================================================
-- NUCORE hotfix 2026-11-09 - add BeginningUtc to SettlementPublicationIndexPrices' key
--
-- WHY
--   Every NUCORE run since the 2026-11-06 release has exited 1 on ONE work unit:
--       SettlementPublicationIndexPrices 2026-11-01: two rows share the declared primary
--       key (LocalTime, SpiSettlementPublicationIndexId,
--       SpifSettlementPublicationIndexFrequencyId) = 2026-11-01T01:00:00 ...
--
--   2026-11-01 is the US DST fall-back day. LocalTime is Central wall-clock with no
--   offset, so 01:00-01:45 occurs TWICE: the API returns 1,900 rows for that day instead
--   of 1,823, and 75 groups collide on the old key. The loader's duplicate guard fails
--   the unit before anything is written, so the day is ABSENT from this table. The key
--   had been verified over five complete days, none of them a fall-back day.
--
--   BeginningUtc separates the two hours (06:xx vs 07:xx UTC) on 2026-11-01, but is NOT a
--   replacement for LocalTime: on 2024-11-03 and 2025-11-02 the vendor maps local 00:00
--   AND local 01:00 of some indexes to the same 06:00Z. Only the pair is unique.
--
--   New key (LocalTime, BeginningUtc, SpiSettlementPublicationIndexId,
--   SpifSettlementPublicationIndexFrequencyId), verified unique 2026-11-09 against the
--   live API over 9 complete days (every fall-back and spring-forward day since history
--   starts, plus four ordinary days) and across their 14,637-row UNION.
--
-- WHAT THIS DOES
--   Replaces PK_SettlementPublicationIndexPrices in place (same name, still CLUSTERED,
--   still led by LocalTime). NO DATA IS CHANGED OR DELETED. The new key is a strict
--   superset of the old one, so every row already stored stays unique; step 2 proves it
--   before touching the constraint anyway.
--
--   Idempotent: if the key already has BeginningUtc in position 2 it does nothing.
--   The rebuild is one transaction; a failure leaves the old key in place.
--
-- RUN ORDER
--   1. this script
--   2. sql/NUCORE/003_CreateNucoreProcedures.sql - re-ALTERs every proc; the MERGE in
--      usp_MergeSettlementPublicationIndexPrices now joins on BeginningUtc too. Its other
--      six procs are unchanged by this fix. 001 and 002 need NOT be re-run (001 is
--      creation-guarded; 002's TVP did not change).
--   3. THEN deploy the release (scripts\Release.ps1). The old release keeps failing on
--      2026-11-01 against this schema, exactly as today, and loads every other day
--      normally. The NEW release against the OLD schema would try to MERGE the duplicate
--      hour on the old key and fail - so the SQL must go first.
--
--   No core.LoadLog cleanup is needed: the 2026-11-01 unit never completed, so the next
--   run re-attempts it on its own.
--
--   Run it OUTSIDE a NUCORE run window (scheduled ~06:03 Central): the clustered rebuild
--   holds a schema lock for a few seconds and would block a concurrent merge.
--
-- ⚠ FORWARD-ONLY once the new release has loaded 2026-11-01. From then on the table holds
--   rows that differ only in BeginningUtc, so the OLD key cannot be restored and the OLD
--   003 would fail loudly on that day. Rolling back the BINARY (Release.ps1 -Rollback) is
--   still safe: the old guard simply refuses 2026-11-01 again.
--
-- ⚠ KNOWN TRADE-OFF: BeginningUtc is now identity, not a value. If the vendor ever
--   CORRECTS a row's BeginningUtc (its mapping is already wrong on 2024-11-03 and
--   2025-11-02), the MERGE inserts a second row instead of updating the first, with no
--   error. Detect it with:
--       SELECT LocalTime, SpiSettlementPublicationIndexId, SpifSettlementPublicationIndexFrequencyId,
--              COUNT(*) AS n
--       FROM   dbo.SettlementPublicationIndexPrices
--       GROUP BY LocalTime, SpiSettlementPublicationIndexId, SpifSettlementPublicationIndexFrequencyId
--       HAVING COUNT(*) > 2
--           OR (COUNT(*) = 2 AND DATEPART(HOUR, LocalTime) <> 1);
--   which should return nothing (only the repeated 01:xx hour of a fall-back day may hold 2).
-- =====================================================================================

USE [NUCORE];
GO

SET XACT_ABORT ON;
GO

-- -------------------------------------------------------------------------------------
-- 1. Before.
-- -------------------------------------------------------------------------------------
PRINT '--- PK_SettlementPublicationIndexPrices before ---';

SELECT  ic.key_ordinal, c.name AS ColumnName
FROM    sys.indexes       AS i
JOIN    sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN    sys.columns       AS c  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE   i.object_id = OBJECT_ID(N'[dbo].[SettlementPublicationIndexPrices]')
  AND   i.is_primary_key = 1
ORDER BY ic.key_ordinal;

IF OBJECT_ID(N'[dbo].[SettlementPublicationIndexPrices]', N'U') IS NOT NULL
    SELECT COUNT_BIG(*) AS RowCountBefore FROM [dbo].[SettlementPublicationIndexPrices];
GO

-- -------------------------------------------------------------------------------------
-- 2 + 3. Prove the new key is unique over the stored rows, then swap the constraint.
-- -------------------------------------------------------------------------------------
IF OBJECT_ID(N'[dbo].[SettlementPublicationIndexPrices]', N'U') IS NULL
BEGIN
    PRINT '--- dbo.SettlementPublicationIndexPrices does not exist; nothing to do (001 creates it with the new key) ---';
END
ELSE IF EXISTS (
        SELECT 1
        FROM   sys.indexes       AS i
        JOIN   sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN   sys.columns       AS c  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE  i.object_id = OBJECT_ID(N'[dbo].[SettlementPublicationIndexPrices]')
          AND  i.is_primary_key = 1
          AND  ic.key_ordinal = 2
          AND  c.name = N'BeginningUtc')
BEGIN
    PRINT '--- key already includes BeginningUtc; nothing to do ---';
END
ELSE
BEGIN
    IF EXISTS (
            SELECT 1
            FROM   [dbo].[SettlementPublicationIndexPrices]
            GROUP BY [LocalTime], [BeginningUtc],
                     [SpiSettlementPublicationIndexId], [SpifSettlementPublicationIndexFrequencyId]
            HAVING COUNT(*) > 1)
    BEGIN
        -- Cannot happen while the old key holds (superset), but never rebuild blind.
        THROW 50001, N'Stored rows are not unique on the new key; PK left unchanged.', 1;
    END;

    BEGIN TRANSACTION;

    PRINT '--- replacing PK_SettlementPublicationIndexPrices ---';

    ALTER TABLE [dbo].[SettlementPublicationIndexPrices]
        DROP CONSTRAINT [PK_SettlementPublicationIndexPrices];

    ALTER TABLE [dbo].[SettlementPublicationIndexPrices]
        ADD CONSTRAINT [PK_SettlementPublicationIndexPrices]
        PRIMARY KEY CLUSTERED ([LocalTime], [BeginningUtc],
                               [SpiSettlementPublicationIndexId],
                               [SpifSettlementPublicationIndexFrequencyId]);

    COMMIT TRANSACTION;
END
GO

-- -------------------------------------------------------------------------------------
-- 4. After. Expect four rows: LocalTime, BeginningUtc, SpiSettlementPublicationIndexId,
--    SpifSettlementPublicationIndexFrequencyId - and the same row count as before.
-- -------------------------------------------------------------------------------------
PRINT '--- PK_SettlementPublicationIndexPrices after ---';

SELECT  ic.key_ordinal, c.name AS ColumnName
FROM    sys.indexes       AS i
JOIN    sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN    sys.columns       AS c  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE   i.object_id = OBJECT_ID(N'[dbo].[SettlementPublicationIndexPrices]')
  AND   i.is_primary_key = 1
ORDER BY ic.key_ordinal;

IF OBJECT_ID(N'[dbo].[SettlementPublicationIndexPrices]', N'U') IS NOT NULL
    SELECT COUNT_BIG(*) AS RowCountAfter FROM [dbo].[SettlementPublicationIndexPrices];
GO

-- REMINDER: now run sql/NUCORE/003_CreateNucoreProcedures.sql, then deploy the release.
