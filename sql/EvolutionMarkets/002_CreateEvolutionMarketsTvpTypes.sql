-- =============================================================================
-- 002_CreateEvolutionMarketsTvpTypes.sql
-- Loader:   EvolutionMarkets  (EVO DataPipeline API - market-data history)
-- Database: EvolutionMarkets      Schema: arm
-- Creates:  the 1 table-valued parameter type the bulk merge takes:
--             * arm.MarketDataTvp  (25 columns) -> arm.usp_BulkMergeMarketData
--
-- Design of record: docs/design/EvolutionMarkets.md SS8.3 (the TVP / column-order
--                   contract).
-- Run 001 first. This script assumes EvolutionMarkets is the current database.
--
-- =============================================================================
-- *** THE LOAD-BEARING CONTRACT - READ THIS BEFORE TOUCHING THE TYPE ***
-- =============================================================================
-- A TVP binds BY POSITION, not by name. The C# sink's BuildTable DataTable must match
-- this type by column NAME + ORDER + TYPE, exactly. A silent reorder on either side
-- produces NO compile error, NO SQL error and NO warning - it just writes every value
-- into the wrong column of every loaded row.
--
-- *** THIS PARTICULAR TVP IS UNUSUALLY EASY TO CORRUPT SILENTLY. ***
-- It carries four interleaved decimal/int pairs:
--       Ask DECIMAL / AskSize INT / Bid DECIMAL / BidSize INT /
--       Mid DECIMAL / MidSize INT
-- Swapping Ask with Bid, or AskSize with BidSize, is TYPE-COMPATIBLE. Nothing
-- anywhere would raise. The prices would simply be inverted in every row, forever,
-- and a bid/ask inversion is exactly the kind of error that looks plausible in a
-- spot check. The ONLY defence is that the five column lists below stay in lockstep,
-- and the SinkTests unit test that pins BuildTable against this file.
--
-- The SAME order must appear in all five places:
--   1) the arm.MarketData table body in 001,
--   2) the type body here in 002,
--   3) the merge proc's source SELECT / UPDATE / INSERT lists in 003,
--   4) the C# sink's BuildTable column order (Sinks.cs),
--   5) the CODE_TESTER unit test that pins (4) against this file.
--
-- ORDERED COLUMN LIST (this is what CODER and CODE_TESTER pin against):
--
--   arm.MarketDataTvp - 25 columns, in this order:
--       1. FileLogId             INT              NULL       <- provenance, NEVER a merge key
--       2. MarketDataId          UNIQUEIDENTIFIER NOT NULL   <- MERGE KEY / PK
--       3. Market                VARCHAR(250)     NULL
--       4. Term                  VARCHAR(50)      NULL
--       5. Term2                 VARCHAR(50)      NULL
--       6. Tenor                 VARCHAR(50)      NULL
--       7. InstrumentSourceName  VARCHAR(250)     NULL
--       8. InstrumentId          UNIQUEIDENTIFIER NULL
--       9. InstrumentName        VARCHAR(250)     NULL
--      10. PriceTS               DATETIME2(0)     NULL
--      11. BusinessDate          DATE             NULL
--      12. PriceType             VARCHAR(250)     NULL
--      13. [Size]                INT              NULL
--      14. Depth                 INT              NULL
--      15. Price                 DECIMAL(18,8)    NULL
--      16. Ask                   DECIMAL(18,8)    NULL
--      17. AskSize               INT              NULL
--      18. Bid                   DECIMAL(18,8)    NULL
--      19. BidSize               INT              NULL
--      20. Mid                   DECIMAL(18,8)    NULL
--      21. MidSize               INT              NULL
--      22. [Change]              DECIMAL(18,8)    NULL
--      23. PctRetDaily           DECIMAL(18,8)    NULL
--      24. Currency              VARCHAR(50)      NULL
--      25. [Checksum]            INT              NOT NULL   <- change-detection hash
--
-- FileLogId IS FIRST. This follows the cross-loader convention ("FileLogId first
-- where the table carries provenance"), matching NGI and deliberately not copying
-- AGSI's internal inconsistency (its entity TVP puts FileLogId LAST while its storage
-- TVP puts it first).
--
-- Checksum IS LAST, matching its position in the user-supplied table DDL (between
-- Currency and ModifiedAtUtc). It DOES cross the TVP - unlike ModifiedAtUtc - because
-- it is computed in C# by EvoChecksum, not by SQL. See the note below.
--
-- WHAT NEVER CROSSES THIS TVP:
--   * ModifiedAtUtc - stamped by the MERGE proc (SYSUTCDATETIME()), and only on a row
--     whose Checksum actually changed.
--   * DateCreated   - supplied by the target table's DEFAULT GETDATE().
--   * any computed column (there are none: this feed carries no coordinates, so there
--     is no GEOGRAPHY to build - contrast IIR's PlantPoint).
--   * any value that is CONSTANT across the batch. The merge proc takes NO scalar
--     alongside its TVP: BusinessDate is per-row, and the run date is not persisted by
--     this loader at all.
--
-- WHY Checksum IS A TVP COLUMN AND NOT COMPUTED SERVER-SIDE:
--   It is deliberately NOT BINARY_CHECKSUM/CHECKSUM over the source row. Neither is
--   guaranteed stable across SQL Server versions or collations, both are documented as
--   collision-prone, and CHECKSUM ignores trailing-whitespace differences - so a
--   server-side value could silently change meaning after an upgrade and either mask
--   every real change or report every row as changed. Computing FNV-1a/32 in C#
--   (EvoChecksum) makes the value fixed, portable and - crucially - assertable in a
--   unit test. See design SS8.4.
--
-- NULLABILITY mirrors the target columns in 001 exactly, with one deliberate
-- asymmetry worth stating:
--   * MarketDataId is NOT NULL here even though a tolerant reader can produce NULL
--     for anything else. That is on purpose: the reader DROPS-AND-COUNTS a record
--     whose key is unusable, so a NULL key reaching the TVP is a loader bug, and
--     failing loudly at bind time is far better than inserting an unmergeable row.
--   * Checksum is NOT NULL because the reader always computes it. A NULL arriving here
--     would mean the reader skipped that step, which the merge's change-detection
--     guard would then interpret as "changed" on every run.
--   * EVERY other payload column is NULLable - including the nine that are ALWAYS NULL
--     for the only currently permissioned dataset (Term2, InstrumentSourceName, Size,
--     Depth, Price, AskSize, BidSize, MidSize, PctRetDaily). See the nullability block
--     in 001 for the full rationale.
--   * FileLogId is NULLable to match the target column; in practice the reader always
--     writes its arm.FileLog row FIRST and stamps the returned Id, so a NULL here
--     means the provenance write was skipped, not that the row is bad.
--
-- Column WIDTHS and PRECISIONS equal the target widths in 001 so a TVP can never
-- silently truncate or round a value on its way into the table. The C# reader also
-- caps every string at these exact widths (EvoParse.StrCapped) and counts any
-- truncation, so an over-long value degrades visibly instead of failing the bind.
--
-- There is no PRIMARY KEY / UNIQUE on the type: the merge proc (003) de-dups the batch
-- on MarketDataId (ROW_NUMBER, last wins) BEFORE the MERGE, so a same-batch duplicate
-- key cannot break the MERGE and must not be rejected at bind time either.
--
-- SqlSinkBase passes the TVP as the parameter @Records - the merge proc in 003 names
-- it @Records for that reason (NOT IIR's @Rows, which needed a custom sink base this
-- loader deliberately does not use).
-- =============================================================================

USE EvolutionMarkets;
GO

-- ----------------------------------------------------------------------------
-- arm.MarketDataTvp - feeds arm.usp_BulkMergeMarketData (merge key MarketDataId).
-- 25 columns, identical in name/order/type to the arm.MarketData payload columns in
-- 001 (columns 2..25 of that table, i.e. everything except DateCreated and
-- ModifiedAtUtc).
--
-- *** DO NOT REORDER. *** See the interleaved decimal/int warning in the header:
-- Ask/AskSize/Bid/BidSize/Mid/MidSize are mutually type-compatible and a swap would
-- invert prices silently.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.MarketDataTvp') IS NULL
BEGIN
    CREATE TYPE arm.MarketDataTvp AS TABLE
    (
        FileLogId            INT              NULL,
        MarketDataId         UNIQUEIDENTIFIER NOT NULL,
        Market               VARCHAR(250)     NULL,
        Term                 VARCHAR(50)      NULL,
        Term2                VARCHAR(50)      NULL,
        Tenor                VARCHAR(50)      NULL,
        InstrumentSourceName VARCHAR(250)     NULL,
        InstrumentId         UNIQUEIDENTIFIER NULL,
        InstrumentName       VARCHAR(250)     NULL,
        PriceTS              DATETIME2(0)     NULL,
        BusinessDate         DATE             NULL,
        PriceType            VARCHAR(250)     NULL,
        [Size]               INT              NULL,
        Depth                INT              NULL,
        Price                DECIMAL(18, 8)   NULL,
        Ask                  DECIMAL(18, 8)   NULL,
        AskSize              INT              NULL,
        Bid                  DECIMAL(18, 8)   NULL,
        BidSize              INT              NULL,
        Mid                  DECIMAL(18, 8)   NULL,
        MidSize              INT              NULL,
        [Change]             DECIMAL(18, 8)   NULL,
        PctRetDaily          DECIMAL(18, 8)   NULL,
        Currency             VARCHAR(50)      NULL,
        [Checksum]           INT              NOT NULL
    );
END
GO
