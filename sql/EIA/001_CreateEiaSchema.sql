-- =============================================================================
-- 001_CreateEiaSchema.sql
-- Database : EIAv2
-- Schema   : arm
--
-- Creates the ONE table this loader writes:
--
--   arm.SeriesData   <- https://api.eia.gov/v2/<SeriesId>/data/
--
-- Design of record: docs/design/EIA.md
-- Field reference:  docs/apis/EIA.md
--
-- -----------------------------------------------------------------------------
-- ⚠ WHAT THIS SCRIPT DOES **NOT** CREATE
-- -----------------------------------------------------------------------------
-- The three catalog tables the loader READS already exist in EIAv2 and are
-- maintained outside this loader:
--
--   arm.Series            260 rows (95 with Enabled = 1 AND IsDataSeries = 1)
--   arm.SeriesFrequency   371 rows
--   arm.SeriesDataType    390 rows
--
-- They are deliberately absent here. This script creates nothing it does not
-- own, so re-running it can never reshape or reseed the catalog.
--
-- -----------------------------------------------------------------------------
-- 🔒 THE dbo SCHEMA NEXT DOOR IS LIVE AND HUGE
-- -----------------------------------------------------------------------------
-- The same database holds the INCUMBENT dataset that arm.SeriesData shadows:
--
--   dbo.SeriesData   ~94,001,592 rows   (identical column list except that its
--                                        audit column is ConduitLastUpdate)
--   dbo.Series, dbo.SeriesFrequency, dbo.SeriesDataType, dbo.SeriesFacet, ...
--
-- Every statement in every EIA script is explicitly [arm].-qualified and must
-- stay that way. An unqualified CREATE/DROP here lands on the live Conduit
-- dataset. See sql/EIA/999_DropEiaObjects.sql for the same warning with teeth.
--
-- -----------------------------------------------------------------------------
-- THE USER-SUPPLIED DDL IS HONOURED VERBATIM
-- -----------------------------------------------------------------------------
-- Column names, types, nullability, ORDER, the DEFAULT and the constraint name
-- below are exactly as supplied in the loader request. Nothing was added --
-- no FileLogId/DateCreated audit hub, and no secondary indexes. The clustered
-- primary key leads with SeriesId, which is the access path every merge and
-- every validation check in sql/EIA/003 uses, so no extra index is needed to
-- keep them bounded (the lesson from Criterion's unbounded validator).
--
-- ⚠ TWO PROPERTIES OF THE SUPPLIED DDL WORTH KNOWING:
--
--   1. DEFAULT (SYSDATETIME()) is LOCAL server time, not UTC, despite the
--      column being named ModifiedAtUtc. The default therefore only applies to
--      rows inserted by something other than this loader: the merge proc in
--      sql/EIA/003 sets ModifiedAtUtc EXPLICITLY with SYSUTCDATETIME(), so
--      every row this loader writes holds true UTC. Same posture as OilX.
--
--   2. FacetJSON / DataJSON are VARCHAR(8000), not NVARCHAR. Non-ASCII text in
--      a facet description would be transcoded to the database's code page.
--      arm.usp_ValidateLoad reports rows whose JSON round-trips lossily rather
--      than letting it pass silently.
-- =============================================================================

USE EIAv2;
GO

IF SCHEMA_ID('arm') IS NULL
    EXEC('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- ----------------------------------------------------------------------------
-- arm.SeriesData
--
--   Grain : one API data row -- (SeriesId, FrequencyId, Period, FacetKey)
--
--   FacetKey  INT NOT NULL  hash of FacetJSON   -- part of the primary key
--   DataHash  INT NULL      hash of DataJSON    -- the merge's change guard
--
--   Both are the SAME pinned 32-bit string hash (src/DataLoader.EIA/
--   EiaLegacyHash.cs), reverse-engineered from dbo.SeriesData and verified on
--   20,000 live rows -- see docs/design/EIA.md S6. FacetKey is NOT NULL because
--   it is a key column: a row with no facets at all hashes the literal text
--   '{}' (= 1947253782) rather than storing NULL, which is exactly what the
--   incumbent does for its 7,301 facet-less rows.
--
--   There is no row-level provenance column (no FileLogId, no run id). The
--   supplied DDL has none, and core.LoadLog already records every work unit.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SeriesData') IS NULL
BEGIN
    CREATE TABLE [arm].[SeriesData]
    (
        [SeriesId]      VARCHAR(100)  NOT NULL,
        [FrequencyId]   VARCHAR(50)   NOT NULL,
        [Period]        VARCHAR(50)   NOT NULL,
        [FacetKey]      INT           NOT NULL,
        [FacetJSON]     VARCHAR(8000) NULL,
        [DataJSON]      VARCHAR(8000) NULL,
        [DataHash]      INT           NULL,
        [ModifiedAtUtc] DATETIME2(3)  NULL DEFAULT (SYSDATETIME()),
        CONSTRAINT [PK_ARM_SeriesData] PRIMARY KEY CLUSTERED
        (
            [SeriesId]    ASC,
            [FrequencyId] ASC,
            [Period]      ASC,
            [FacetKey]    ASC
        )
    );
END
GO

-- ----------------------------------------------------------------------------
-- Sanity: the catalog tables the loader reads must be present. This script does
-- not create them -- it fails loudly if they are missing, rather than letting
-- 003's procedures be created against tables that do not exist.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Series') IS NULL
    THROW 50001, 'arm.Series is missing. The EIA loader READS the catalog; it does not create it. Restore arm.Series / arm.SeriesFrequency / arm.SeriesDataType before continuing.', 1;
IF OBJECT_ID('arm.SeriesFrequency') IS NULL
    THROW 50001, 'arm.SeriesFrequency is missing. See the note on arm.Series above.', 1;
IF OBJECT_ID('arm.SeriesDataType') IS NULL
    THROW 50001, 'arm.SeriesDataType is missing. See the note on arm.Series above.', 1;
GO

PRINT 'sql/EIA/001 complete: arm.SeriesData present; catalog tables verified.';
GO
