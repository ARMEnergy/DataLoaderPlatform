-- =============================================================================
-- 004_CreateArgusComparison.sql
-- arm.usp_CompareArmToDbo - the arm/dbo reconciliation every other loader gets.
--
-- Argus CANNOT BE RECONCILED BY PRIMARY KEY. This is not an omission; the two
-- schemas model the same vendor in fundamentally different shapes, verified against
-- the live catalog on 2026-09-28:
--
--   [arm]  NG_ImpliedVolatility (the fact, PK Id)  +  6 lookup tables
--          MarketLookup, OptionStrikeLookup, ProductLookup, TermLookup,
--          UnitLookup, ValuationTypeLookup
--
--   [dbo]  TimeSeriesDetail (3.3M rows) + TimeSeriesDetailHistory (1.1M)
--          CategoryLookup, CodeLookup, ModuleDetailLookup, ModuleLookup,
--          PriceTypeLookup, QuoteLookup, TimestampTypeLookup
--
-- Three separate blockers, any one of which is sufficient:
--
--   1. NO TABLE CORRESPONDS. Not one [arm] table name matches a [dbo] table, even
--      after normalising case and underscores. dbo is a GENERIC time-series model
--      (a quote is identified by a QuoteLookup row joined to ModuleLookup /
--      CodeLookup / CategoryLookup); arm is a SINGLE PURPOSE-BUILT fact table with
--      the dimensions flattened into it. There is no 1:1 pairing to join.
--
--   2. THE arm LOOKUPS HAVE NO PRIMARY KEY. All six carry no PK constraint at all,
--      so there is nothing to join them on even if a counterpart existed.
--
--   3. arm's ONLY KEYED TABLE KEYS ON A SURROGATE. NG_ImpliedVolatility's PK is a
--      bare identity Id, which by definition has no meaning on the dbo side.
--
-- A meaningful comparison here is a DATA MODELLING exercise, not a join: someone has
-- to state which (Module, Code, Category, PriceType, Timestamp) combination in
-- dbo.TimeSeriesDetail corresponds to a row of arm.NG_ImpliedVolatility. That
-- mapping is a business decision and is deliberately NOT guessed here - a fabricated
-- join would produce confident, wrong output, which is worse than none.
--
-- The proc is still created so that `EXEC arm.usp_CompareArmToDbo` behaves the same
-- way in every loader database. It returns the position in the standard shape.
--
-- Once the semantic mapping is agreed, write it here by hand. Do NOT add Argus to
-- scripts\Generate-ArmToDboComparison.ps1 - that generator matches tables by name
-- and has nothing to work with in this database.
--
-- Run 001-003 first. This script assumes Argus is the current database.
-- =============================================================================

USE Argus;
GO

CREATE OR ALTER PROCEDURE arm.usp_CompareArmToDbo
    @Date            DATE    = NULL,
    @TablePair       SYSNAME = NULL,
    @MaxRowsPerIssue INT     = 1000
AS
BEGIN
    SET NOCOUNT ON;

    -- Each arm table and why it cannot be paired, so the answer is auditable here
    -- rather than only in the header comment above.
    SELECT
        CAST(t.name AS SYSNAME)               AS TablePair,
        CAST('NOT_APPLICABLE' AS VARCHAR(16)) AS Issue,
        CAST(CASE
                WHEN pk.object_id IS NULL
                    THEN N'No primary key on arm.' + t.name + N', and no corresponding dbo table - nothing to join on.'
                ELSE N'No corresponding dbo table; arm.' + t.name
                     + N' keys on a surrogate that has no meaning in the dbo time-series model.'
             END AS NVARCHAR(512))            AS KeyValues,
        CAST(NULL AS SYSNAME)                 AS ColumnName,
        CAST(CONCAT(N'arm.', t.name) AS NVARCHAR(256))  AS ArmValue,
        CAST(N'(no counterpart)' AS NVARCHAR(256))      AS DboValue
    FROM sys.tables t
    JOIN sys.schemas s     ON s.schema_id = t.schema_id
    LEFT JOIN sys.indexes pk ON pk.object_id = t.object_id AND pk.is_primary_key = 1
    WHERE s.name = 'arm'
    ORDER BY t.name;
END
GO
