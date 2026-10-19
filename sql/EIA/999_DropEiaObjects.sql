-- =============================================================================
-- 999_DropEiaObjects.sql
-- Database : EIAv2
-- Schema   : arm
--
-- Tear-down, in dependency order. DESTRUCTIVE. Run it only to rebuild this
-- loader's objects from scratch.
--
-- Order matters: a table type cannot be dropped while a procedure references
-- it, so procedures go first, then the type, then the table. (Re-running 002
-- after a type change requires exactly this sequence -- a table type cannot be
-- ALTERed.)
--
-- =============================================================================
-- 🔒 READ THIS BEFORE RUNNING. TWO WAYS TO DESTROY LIVE DATA.
-- =============================================================================
--
-- 1. THE dbo SCHEMA NEXT DOOR HOLDS THE INCUMBENT DATASET, IN A TABLE WITH THE
--    SAME NAME. dbo.SeriesData has ~94,001,592 rows and is still written by
--    Conduit. arm.SeriesData is the shadow this loader builds. Every statement
--    below is explicitly [arm].-qualified and MUST stay that way -- an
--    unqualified DROP TABLE SeriesData resolves to dbo for most logins and
--    destroys the live dataset. The same applies to dbo.Series,
--    dbo.SeriesFrequency and dbo.SeriesDataType.
--
-- 2. THE arm CATALOG TABLES ARE NOT THIS LOADER'S TO DROP. arm.Series,
--    arm.SeriesFrequency and arm.SeriesDataType are INPUT -- they are
--    maintained outside this loader, sql/EIA/001 does not create them, and
--    nothing here drops them. Re-running 001 after this script restores
--    arm.SeriesData alone; the catalog is untouched and no reseed is needed.
--    They are listed here only to say, explicitly, that they are excluded.
--
-- Dropping arm.SeriesData discards the loaded history. A full re-backfill is
-- ~90M rows and tens of thousands of API requests against a rate-limited key;
-- it is not a quick rebuild. Consider TRUNCATE of a single series' range, or
-- simply re-running the loader with Loaders:EIA:ReloadAll = true, before
-- reaching for this file.
-- =============================================================================

USE EIAv2;
GO

-- ---- procedures -------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_UpdateSeriesEndPeriod;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeSeriesData;
DROP PROCEDURE IF EXISTS arm.usp_GetSeriesDataTypes;
DROP PROCEDURE IF EXISTS arm.usp_GetSeriesFrequencies;
DROP PROCEDURE IF EXISTS arm.usp_GetActiveSeries;
GO

-- ---- table type -------------------------------------------------------------
DROP TYPE IF EXISTS arm.SeriesDataTvp;
GO

-- ---- table ------------------------------------------------------------------
-- ⚠ arm. ONLY. dbo.SeriesData is the live incumbent and is NEVER touched here.
DROP TABLE IF EXISTS arm.SeriesData;
GO

-- ---- deliberately NOT dropped -------------------------------------------------
--   arm.Series
--   arm.SeriesFrequency
--   arm.SeriesDataType
--   dbo.*   (everything)
-- ------------------------------------------------------------------------------

PRINT 'sql/EIA/999 complete: arm.SeriesData, arm.SeriesDataTvp and 6 procedures dropped. Catalog and dbo untouched.';
GO
