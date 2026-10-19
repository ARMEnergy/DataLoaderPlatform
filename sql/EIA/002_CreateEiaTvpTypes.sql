-- =============================================================================
-- 002_CreateEiaTvpTypes.sql
-- Database : EIAv2
-- Schema   : arm
--
-- One table type, for the one target table.
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in sql/EIA/001 and the DataTable built by
-- src/DataLoader.EIA/EiaSink.cs EXACTLY; a silent reorder would put FacetJSON
-- into DataJSON on every row without raising an error.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.EIA.Tests/EiaTvpContractTests.cs parses THIS FILE, pulls the
-- CREATE TYPE body out, and asserts name + order + type + nullability against
-- the sink's DataTable. Editing one side without the other fails the suite.
--
-- Conventions:
--   * TVP column order = target table column order.
--   * ModifiedAtUtc is DB-stamped and is NEVER a TVP column.
--   * NOT NULL marks a column the reader treats as REQUIRED: a row missing it
--     is DROPPED and counted rather than merged under a blank key.
--
-- The four key columns are NOT NULL, matching the table's primary key:
--     SeriesId, FrequencyId, Period, FacetKey
-- FacetKey has no NULL case by construction -- a row with no facets hashes the
-- literal '{}' (see sql/EIA/001).
--
-- FacetJSON / DataJSON / DataHash are NULLable, matching 001. In practice the
-- loader always writes all three (a facet-less row gets '{}', a value-less row
-- gets its data keys with null values), but the table permits NULL and the type
-- must not be stricter than the table.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in order).
-- =============================================================================

USE EIAv2;
GO

-- ----------------------------------------------------------------------------
-- arm.SeriesDataTvp  ->  arm.SeriesData
-- 7 columns: the table's 8 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SeriesDataTvp') IS NULL
BEGIN
    CREATE TYPE arm.SeriesDataTvp AS TABLE
    (
        [SeriesId]    VARCHAR(100)  NOT NULL,
        [FrequencyId] VARCHAR(50)   NOT NULL,
        [Period]      VARCHAR(50)   NOT NULL,
        [FacetKey]    INT           NOT NULL,
        [FacetJSON]   VARCHAR(8000) NULL,
        [DataJSON]    VARCHAR(8000) NULL,
        [DataHash]    INT           NULL
    );
END
GO

PRINT 'sql/EIA/002 complete: arm.SeriesDataTvp present.';
GO
