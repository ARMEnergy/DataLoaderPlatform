-- =============================================================================
-- 002_CreateNgiTvpTypes.sql
-- Loader:   NGI  (NGI Data Services - Bidweek natural-gas price survey)
-- Database: NGI            Schema: arm
-- Creates:  the 2 table-valued parameter types the bulk merges take:
--             * arm.BidWeekLocationTvp   ( 3 columns) -> arm.usp_BulkMergeBidWeekLocation
--             * arm.BidWeekDataTvp       (12 columns) -> arm.usp_BulkMergeBidWeekData
--
-- Design of record: docs/design/NGI.md SS8.3 (the TVP / column-order contract).
-- Run 001 first. This script assumes NGI is the current database.
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
--   1) the table body in 001,
--   2) the type body here in 002,
--   3) the merge proc's source SELECT / INSERT / VALUES lists in 003,
--   4) the C# sink's BuildTable column order,
--   5) the CODE_TESTER unit test that pins (4) against this file.
--
-- ORDERED COLUMN LISTS (this is what CODER and CODE_TESTER pin against):
--
--   arm.BidWeekLocationTvp - 3 columns, in this order:
--       1. FileLogId      INT           NULL
--       2. PointCode      VARCHAR(20)   NOT NULL     <- merge key
--       3. LocationName   VARCHAR(100)  NULL
--
--   arm.BidWeekDataTvp - 12 columns, in this order:
--       1. FileLogId      INT           NULL
--       2. IssueDate      DATE          NOT NULL     <- merge key part 1
--       3. PointCode      VARCHAR(20)   NOT NULL     <- merge key part 2
--       4. SurveyStart    DATE          NULL
--       5. SurveyEnd      DATE          NULL
--       6. Region         VARCHAR(64)   NULL
--       7. PricingPoint   VARCHAR(100)  NULL
--       8. [Low]          DECIMAL(13,6) NULL
--       9. [High]         DECIMAL(13,6) NULL
--      10. [Average]      DECIMAL(13,6) NULL
--      11. [Volume]       INT           NULL
--      12. Deals          INT           NULL
--
-- FileLogId IS FIRST IN BOTH TYPES. This follows the cross-loader convention
-- ("FileLogId first where the table carries provenance") and deliberately does NOT
-- copy AGSI, whose GasStorageEntityTvp puts FileLogId LAST while its GasStorageTvp
-- puts it first. That internal inconsistency is not reproduced here: both NGI
-- types put it first, so there is one rule to remember instead of two.
--
-- WHAT NEVER CROSSES A TVP:
--   * ModifiedAtUtc - stamped by the MERGE proc (SYSUTCDATETIME()).
--   * DateCreated   - supplied by the target table's DEFAULT GETDATE().
--   * any computed column (there are none here: the locations feed carries no
--     coordinates, so there is no GEOGRAPHY to build - contrast IIR's PlantPoint).
--   * any value that is CONSTANT across the batch. Neither merge proc takes a
--     scalar alongside its TVP: IssueDate is per-row (it is a key), and the run
--     date is not persisted by this loader at all.
--
-- NULLABILITY mirrors the target columns in 001 exactly, with one deliberate
-- asymmetry worth stating:
--   * The KEY columns (PointCode; IssueDate + PointCode) are NOT NULL here even
--     though a tolerant reader can produce NULL for anything else. That is on
--     purpose: the reader DROPS-AND-COUNTS a record whose key is unusable, so a
--     NULL key reaching the TVP is a loader bug, and failing loudly at bind time
--     is far better than inserting an unkeyable row.
--   * Every non-key payload column is NULLable, including the four that were never
--     null in the live sample (SurveyStart, SurveyEnd, Region, PricingPoint) and
--     LocationName - see the nullability block in 001 for the full rationale.
--   * FileLogId is NULLable to match the target column; in practice the reader
--     always writes its arm.FileLog row FIRST and stamps the returned Id, so a
--     NULL here means the provenance write was skipped, not that the row is bad.
--
-- Column WIDTHS equal the target widths in 001 so a TVP can never silently
-- truncate a value on its way into the table.
--
-- There is no PRIMARY KEY / UNIQUE on either type: the merge procs (003) de-dup
-- the batch on the merge key (ROW_NUMBER, last wins) BEFORE the MERGE, so a
-- same-batch duplicate key cannot break the MERGE and must not be rejected at
-- bind time either.
--
-- SqlSinkBase passes the TVP as the parameter @Records - both merge procs in 003
-- name it @Records for that reason (NOT IIR's @Rows, which needed a custom sink).
-- =============================================================================

USE NGI;
GO

-- ----------------------------------------------------------------------------
-- arm.BidWeekLocationTvp - feeds arm.usp_BulkMergeBidWeekLocation (key PointCode).
-- Order: (FileLogId, PointCode, LocationName).
--
-- *** DIRECTION WARNING carried down from the reader (docs/apis/NGI.md SS5.1) ***
-- The source JSON map runs NAME -> CODE:   "Agua Dulce" : "STXAGUAD"
--   the JSON KEY   is LocationName,
--   the JSON VALUE is PointCode.
-- Inverting the two produces no exception and no warning - just 163 rows with the
-- name in PointCode and the code in LocationName, after which every join and every
-- reconciliation check silently misses. Mnemonic: codes are UPPERCASE and
-- unspaced; the uppercase side is always the VALUE. PointCode is column 2 here,
-- LocationName is column 3.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.BidWeekLocationTvp') IS NULL
BEGIN
    CREATE TYPE arm.BidWeekLocationTvp AS TABLE
    (
        FileLogId    INT          NULL,
        PointCode    VARCHAR(20)  NOT NULL,
        LocationName VARCHAR(100) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.BidWeekDataTvp - feeds arm.usp_BulkMergeBidWeekData
-- (key (IssueDate, PointCode)).
-- Order: (FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd, Region,
--         PricingPoint, [Low], [High], [Average], [Volume], Deals) - 12 columns,
-- identical to the arm.BidWeekData payload order in 001.
--
-- Type notes (full rationale in the 001 header):
--   * [Low]/[High]/[Average] are DECIMAL(13,6), never FLOAT - a published price
--     must round-trip exactly. 6 dp stores the observed 3-dp grain exactly; the
--     7 integer digits are spike headroom (Feb 2021 printed far above
--     $200/MMBtu). SIGNED - negative gas prices are real.
--   * [Volume]/Deals are INT (not SMALLINT: no vendor ceiling is documented;
--     TINYINT is banned by repo convention).
--   * The three dates are DATE, not DATETIME2 - this feed has no time component
--     anywhere.
--   * VARCHAR, not NVARCHAR - every observed character in both live fixtures is
--     ASCII.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.BidWeekDataTvp') IS NULL
BEGIN
    CREATE TYPE arm.BidWeekDataTvp AS TABLE
    (
        FileLogId    INT           NULL,
        IssueDate    DATE          NOT NULL,
        PointCode    VARCHAR(20)   NOT NULL,
        SurveyStart  DATE          NULL,
        SurveyEnd    DATE          NULL,
        Region       VARCHAR(64)   NULL,
        PricingPoint VARCHAR(100)  NULL,
        [Low]        DECIMAL(13,6) NULL,
        [High]       DECIMAL(13,6) NULL,
        [Average]    DECIMAL(13,6) NULL,
        [Volume]     INT           NULL,
        Deals        INT           NULL
    );
END
GO
