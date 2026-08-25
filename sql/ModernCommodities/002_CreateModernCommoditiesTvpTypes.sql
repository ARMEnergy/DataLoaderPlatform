-- =============================================================================
-- 002_CreateModernCommoditiesTvpTypes.sql
-- Loader:   ModernCommodities  (vendor "ModCom")
-- Database: ModernCommodities        Schema: arm
-- Creates:  the 2 table-valued parameter types the bulk merges take:
--             * arm.TradesTvp      (34 columns) -> arm.usp_BulkMergeAllTrades
--                                               -> arm.usp_BulkMergeMyTrades
--             * arm.SettlementsTvp ( 9 columns) -> arm.usp_BulkMergeSettlements
--
-- Design of record: docs/design/ModernCommodities.md SS8.3 (the TVP / column-order
--                   contract), SS6.1 (why there is no FileLogId), SS13 item 3.
-- Run 001 first. This script assumes ModernCommodities is the current database.
--
-- =============================================================================
-- *** THE LOAD-BEARING CONTRACT - READ THIS BEFORE TOUCHING EITHER TYPE ***
-- =============================================================================
-- A TVP binds BY POSITION, not by name. The C# sink's BuildTable DataTable must
-- match its type here by column NAME + ORDER + TYPE, exactly. A silent reorder on
-- either side produces NO compile error, NO SQL error and NO warning - it just
-- writes every value into the wrong column of every loaded row.
--
-- The SAME order must appear in all five places:
--   1) the table body in 001 (which is the USER'S OWN DDL - it is the anchor),
--   2) the type body here in 002,
--   3) each merge proc's source SELECT / UPDATE / INSERT / VALUES lists in 003,
--   4) the C# sink's BuildTable column order,
--   5) the CODE_TESTER unit test that pins (4) against this file.
--
-- The order below IS THE CSV SOURCE ORDER, which is also the target column order:
-- the vendor's 34-column trades header and 9-column settlements header map 1:1,
-- in order, onto the user's tables. Nothing is dropped, derived or renamed except
-- where the user's DDL renames it.
--
-- =============================================================================
-- *** NEITHER TYPE BEGINS WITH FileLogId - AND THAT IS DELIBERATE. ***
-- =============================================================================
-- The cross-loader rule is "FileLogId is column 1 where the table carries
-- provenance". Here the column DOES NOT EXIST: the user's authoritative DDL gives
-- arm.AllTrades / arm.MyTrades / arm.Settlements no FileLogId, no surrogate Id and
-- no DateCreated (decision D1), so provenance lives in arm.FileLog ALONE.
-- Therefore:
--     arm.TradesTvp      starts at TradeNumber     (payload column 1)
--     arm.SettlementsTvp starts at SettlementDate  (payload column 1)
-- There is also NO FileLogId property on the C# TradeRow / SettlementRow, and no
-- ICwgFactRow-style provenance marker interface. IModComFileLog.UpsertAsync still
-- RETURNS the FileLogId, but nothing consumes it as data.
-- *** Anyone "restoring the convention" must first add the column to the user's
-- three tables, which is out of scope - do not add it to a TVP alone, or the
-- positional binding shifts by one and every value lands in the wrong column. ***
--
-- WHAT NEVER CROSSES A TVP:
--   * ModifiedAtUtc - stamped by the MERGE proc (SYSUTCDATETIME()) on insert AND
--     on match. Its table DEFAULT is effectively unreachable.
--   * DateCreated / a surrogate Id - these columns do not exist on any of the
--     three fact tables (see 001).
--   * any computed column - there are none here (contrast IIR's GEOGRAPHY
--     PlantPoint, which the proc builds from plain FLOATs in the TVP).
--   * any value CONSTANT across the batch. *** There are none in this loader: no
--     @RunDate, no @WindowStart, no @RunToken. *** Nothing about the request window
--     is persisted on a fact row - that is arm.FileLog's job. Both merge procs
--     therefore take the TVP and NOTHING ELSE.
--
-- =============================================================================
-- ORDERED COLUMN LISTS (this is what CODER and CODE_TESTER pin against)
-- =============================================================================
--
--   arm.TradesTvp - 34 columns, in this order:
--        1. TradeNumber            INT             NOT NULL   <- merge key
--        2. State                  VARCHAR(50)     NULL
--        3. Product                VARCHAR(50)     NULL
--        4. Location               VARCHAR(50)     NULL
--        5. PipelineTerminal       VARCHAR(256)    NULL       (spelled CORRECTLY here)
--        6. PriceBasis             VARCHAR(256)    NULL
--        7. Term                   VARCHAR(256)    NULL
--        8. TermStart              DATE            NULL
--        9. TermEnd                DATE            NULL
--       10. Price                  DECIMAL(9, 2)   NULL       SIGNED
--       11. Volume                 DECIMAL(9, 2)   NULL       observed max 300,000
--       12. UnitOfMeasure          VARCHAR(50)     NULL
--       13. Executed               DATETIME2(0)    NULL
--       14. LastUpdated            DATETIME2(0)    NULL       <- MERGE recency guard
--       15. TradeType              VARCHAR(50)     NULL
--       16. Side                   VARCHAR(256)    NULL
--       17. BidTrader              VARCHAR(256)    NULL
--       18. BidLegalName           VARCHAR(256)    NULL
--       19. BidAddress             VARCHAR(256)    NULL
--       20. BidCommission          DECIMAL(9, 2)   NULL
--       21. OfferTrader            VARCHAR(256)    NULL
--       22. OfferLegalName         VARCHAR(256)    NULL
--       23. OfferAddress           VARCHAR(256)    NULL
--       24. OfferCommission        DECIMAL(9, 2)   NULL
--       25. SpreadTradeNumber      VARCHAR(50)     NULL       TEXT, not INT
--       26. ApportionmentProtected BIT             NULL
--       27. ClearingID             VARCHAR(50)     NULL       never populated by either endpoint
--       28. SettlementCurrency     VARCHAR(50)     NULL
--       29. ContractTerms          VARCHAR(256)    NULL
--       30. GTandC                 VARCHAR(256)    NULL       source header is 'GT&C'
--       31. Notes                  VARCHAR(8000)   NULL
--       32. InIndex                BIT             NULL
--       33. ClickAndTrade          BIT             NULL
--       34. ProductType            VARCHAR(50)     NULL
--
--   arm.SettlementsTvp - 9 columns, in this order:
--        1. SettlementDate         DATE            NOT NULL   <- merge key 1/6
--        2. Product                VARCHAR(50)     NOT NULL   <- merge key 2/6
--        3. Location               VARCHAR(50)     NOT NULL   <- merge key 3/6
--        4. PieplineTerminal       VARCHAR(50)     NOT NULL   <- merge key 4/6  *** SIC ***
--        5. PriceBasis             VARCHAR(100)    NOT NULL   <- merge key 5/6
--        6. Term                   VARCHAR(50)     NOT NULL   <- merge key 6/6
--        7. TermStart              DATE            NULL
--        8. TermEnd                DATE            NULL
--        9. Price                  DECIMAL(9, 2)   NULL       SIGNED (74% negative)
--
-- C# DataTable CLR types: int, string, DateTime (for BOTH the DATE and the
-- DATETIME2(0) columns), decimal, bool - nullables via SqlSinkBase.DbNullable /
-- NullIfEmpty.
--
-- NULLABILITY mirrors the target columns in 001 exactly. The key columns are
-- NOT NULL even though a tolerant reader can produce NULL for anything else:
-- that is on purpose - the reader DROPS-AND-COUNTS a record whose key is unusable
-- (a blank/unparseable Trade Number; a blank or over-width settlements key), so a
-- NULL key reaching the TVP is a LOADER BUG and failing loudly at bind time is far
-- better than inserting an unkeyable row. Every non-key column is NULLable,
-- including the 14 that arm.AllTrades leaves 100% NULL by design and the three
-- that are legitimately blank on a 'Financial' row.
--
-- Column WIDTHS equal the target widths in 001 so a TVP can never silently
-- truncate a value on its way into the table. (The C# `Str` helper truncates an
-- over-width NON-KEY value with a warning and drops an over-width KEY row, so the
-- server should never see one.)
--
-- There is no PRIMARY KEY / UNIQUE on either type: the merge procs (003) de-dup
-- the batch on the merge key (ROW_NUMBER, last wins) BEFORE the MERGE, so a
-- same-batch duplicate key cannot break the MERGE and must not be rejected at
-- bind time either.
--
-- SqlSinkBase passes the TVP as the parameter @Records - all three merge procs in
-- 003 name it @Records for that reason (NOT IIR's @Rows, which required a custom
-- sink base this loader does not use).
-- =============================================================================

USE ModernCommodities;
GO

-- ----------------------------------------------------------------------------
-- arm.TradesTvp - 34 columns. Feeds BOTH arm.usp_BulkMergeAllTrades AND
-- arm.usp_BulkMergeMyTrades (merge key TradeNumber in both).
--
-- *** ONE SHARED TYPE FOR TWO TABLES - a deliberate naming deviation from the
-- house arm.<Table>Tvp pattern. *** The allTrades and myTrades CSV headers are
-- BYTE-IDENTICAL (497 bytes, 34 columns, same order - verified across three live
-- captures) and the user's DDL gives the two tables identical column lists. Two
-- 34-column type definitions kept in sync by hand is EXACTLY the drift a
-- positional contract cannot survive, so they are defined ONCE. The alternative
-- (identically-shaped arm.AllTradesTvp / arm.MyTradesTvp) is recorded in design
-- SS13 item 3 and would need only two TableValuedParameterType strings in the C#;
-- it was declined on drift grounds.
--
-- TYPE CALLS worth restating here (full rationale in the 001 header):
--   * Price / Volume / BidCommission / OfferCommission are DECIMAL(9, 2), NEVER
--     FLOAT - a traded price must round-trip exactly. All four are SIGNED: 21% of
--     allTrades prices are negative (differentials) and 0.00 is a real value, so
--     there is no non-negative CHECK anywhere.
--   * Executed / LastUpdated are DATETIME2(0) - the source is 12-hour AM/PM text
--     with NO TIMEZONE STATED ANYWHERE, stored exactly as given and never shifted.
--   * SpreadTradeNumber stays VARCHAR(50) - it is a reference, not a measure.
--   * ApportionmentProtected / InIndex / ClickAndTrade are BIT, and a BLANK source
--     value binds as NULL - *** never false ***.
--   * VARCHAR, not NVARCHAR: every observed character in all three captures is
--     ASCII.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.TradesTvp') IS NULL
BEGIN
    CREATE TYPE arm.TradesTvp AS TABLE
    (
        TradeNumber            INT           NOT NULL,
        State                  VARCHAR(50)   NULL,
        Product                VARCHAR(50)   NULL,
        Location               VARCHAR(50)   NULL,
        PipelineTerminal       VARCHAR(256)  NULL,
        PriceBasis             VARCHAR(256)  NULL,
        Term                   VARCHAR(256)  NULL,
        TermStart              DATE          NULL,
        TermEnd                DATE          NULL,
        Price                  DECIMAL(9, 2) NULL,
        Volume                 DECIMAL(9, 2) NULL,
        UnitOfMeasure          VARCHAR(50)   NULL,
        Executed               DATETIME2(0)  NULL,
        LastUpdated            DATETIME2(0)  NULL,
        TradeType              VARCHAR(50)   NULL,
        Side                   VARCHAR(256)  NULL,
        BidTrader              VARCHAR(256)  NULL,
        BidLegalName           VARCHAR(256)  NULL,
        BidAddress             VARCHAR(256)  NULL,
        BidCommission          DECIMAL(9, 2) NULL,
        OfferTrader            VARCHAR(256)  NULL,
        OfferLegalName         VARCHAR(256)  NULL,
        OfferAddress           VARCHAR(256)  NULL,
        OfferCommission        DECIMAL(9, 2) NULL,
        SpreadTradeNumber      VARCHAR(50)   NULL,
        ApportionmentProtected BIT           NULL,
        ClearingID             VARCHAR(50)   NULL,
        SettlementCurrency     VARCHAR(50)   NULL,
        ContractTerms          VARCHAR(256)  NULL,
        GTandC                 VARCHAR(256)  NULL,
        Notes                  VARCHAR(8000) NULL,
        InIndex                BIT           NULL,
        ClickAndTrade          BIT           NULL,
        ProductType            VARCHAR(50)   NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SettlementsTvp - 9 columns. Feeds arm.usp_BulkMergeSettlements
-- (merge key = all six of SettlementDate, Product, Location, PieplineTerminal,
-- PriceBasis, Term).
--
-- *** `PieplineTerminal` (column 4) IS MISSPELLED ON PURPOSE. *** It is the user's
-- spelling, it is part of arm.Settlements' PRIMARY KEY, and it is reproduced
-- verbatim in the table (001), here, in the merge proc (003) AND in the C#
-- property name (decision D2). The trades type above spells the same concept
-- CORRECTLY. Both spellings coexist deliberately. *** Renaming either one breaks
-- the positional binding silently - do not "tidy" it. ***
--
-- *** Location and PieplineTerminal carry the LITERAL '-' on 82 of 1,443 rows and
-- are NOT NULL KEY columns. *** Do not add any '-' -> NULL normalisation on the
-- way in; '-' is a key VALUE. Equally, PriceBasis 'USD $' contains a space and a
-- '$' and must not be trimmed, stripped or normalised - it is a key too.
--
-- Price is DECIMAL(9, 2) and SIGNED: negative in 1,071 of 1,443 live rows (74%).
-- No non-negative CHECK, here or on the table.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SettlementsTvp') IS NULL
BEGIN
    CREATE TYPE arm.SettlementsTvp AS TABLE
    (
        SettlementDate   DATE          NOT NULL,
        Product          VARCHAR(50)   NOT NULL,
        Location         VARCHAR(50)   NOT NULL,
        PieplineTerminal VARCHAR(50)   NOT NULL,   -- [sic] - the user's spelling, and it is IN THE PK
        PriceBasis       VARCHAR(100)  NOT NULL,
        Term             VARCHAR(50)   NOT NULL,
        TermStart        DATE          NULL,
        TermEnd          DATE          NULL,
        Price            DECIMAL(9, 2) NULL
    );
END
GO
