-- =============================================================================
-- 999_DropArgusObjects.sql
-- Database : Argus
-- Schema   : dlp
--
-- Tear down everything 001-003 create, in dependency order:
--     procedures -> table types -> tables -> schema
--
-- A table type cannot be ALTERed, so changing a TVP column means running this
-- (or at least its procedure + type sections) and then re-running 002 and 003.
--
-- DESTRUCTIVE. This drops the loaded data. Guarded with IF ... IS NOT NULL so it
-- is re-runnable, but nothing here asks for confirmation.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- 1) Procedures — must go first: they hold references to the table types.
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS dlp.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeTimeSeriesDetail;
DROP PROCEDURE IF EXISTS dlp.usp_ReplaceQuoteLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeRvpCodeReference;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeNewsCategoryLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeQuoteHolidayRegion;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeHoliday;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeHolidayRegionLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeUnitCodeConversion;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeUnitLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeTimingLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeTimestampTypeLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergePriceTypeLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeModuleLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeModuleDetailLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeCodeLookup;
DROP PROCEDURE IF EXISTS dlp.usp_BulkMergeCategoryLookup;
DROP PROCEDURE IF EXISTS dlp.usp_UpsertFileLog;
GO

-- ----------------------------------------------------------------------------
-- 2) Table types.
-- ----------------------------------------------------------------------------
DROP TYPE IF EXISTS dlp.TimeSeriesDetailTvp;
DROP TYPE IF EXISTS dlp.RvpCodeReferenceTvp;
DROP TYPE IF EXISTS dlp.NewsCategoryLookupTvp;
DROP TYPE IF EXISTS dlp.QuoteHolidayRegionTvp;
DROP TYPE IF EXISTS dlp.HolidayTvp;
DROP TYPE IF EXISTS dlp.HolidayRegionLookupTvp;
DROP TYPE IF EXISTS dlp.UnitCodeConversionTvp;
DROP TYPE IF EXISTS dlp.UnitLookupTvp;
DROP TYPE IF EXISTS dlp.TimingLookupTvp;
DROP TYPE IF EXISTS dlp.TimestampTypeLookupTvp;
DROP TYPE IF EXISTS dlp.QuoteLookupTvp;
DROP TYPE IF EXISTS dlp.PriceTypeLookupTvp;
DROP TYPE IF EXISTS dlp.ModuleLookupTvp;
DROP TYPE IF EXISTS dlp.ModuleDetailLookupTvp;
DROP TYPE IF EXISTS dlp.CodeLookupTvp;
DROP TYPE IF EXISTS dlp.CategoryLookupTvp;
GO

-- ----------------------------------------------------------------------------
-- 3) Tables. FileLog before Status (FK), everything else is independent —
--    this schema has no FKs between the lookup and fact tables by design.
-- ----------------------------------------------------------------------------
DROP TABLE IF EXISTS dlp.TimeSeriesDetailHistory;
DROP TABLE IF EXISTS dlp.TimeSeriesDetail;
DROP TABLE IF EXISTS dlp.RvpCodeReference;
DROP TABLE IF EXISTS dlp.NewsCategoryLookup;
DROP TABLE IF EXISTS dlp.QuoteHolidayRegion;
DROP TABLE IF EXISTS dlp.Holiday;
DROP TABLE IF EXISTS dlp.HolidayRegionLookup;
DROP TABLE IF EXISTS dlp.UnitCodeConversion;
DROP TABLE IF EXISTS dlp.UnitLookup;
DROP TABLE IF EXISTS dlp.TimingLookup;
DROP TABLE IF EXISTS dlp.TimestampTypeLookup;
DROP TABLE IF EXISTS dlp.QuoteLookup;
DROP TABLE IF EXISTS dlp.PriceTypeLookup;
DROP TABLE IF EXISTS dlp.ModuleLookup;
DROP TABLE IF EXISTS dlp.ModuleDetailLookup;
DROP TABLE IF EXISTS dlp.CodeLookup;
DROP TABLE IF EXISTS dlp.CategoryLookup;
DROP TABLE IF EXISTS dlp.FileLog;
DROP TABLE IF EXISTS dlp.Status;
GO

-- ----------------------------------------------------------------------------
-- 4) Schema — only when nothing else lives in it.
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'dlp')
   AND NOT EXISTS (SELECT 1 FROM sys.objects AS o
                   JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                   WHERE s.[name] = 'dlp')
    EXEC ('DROP SCHEMA dlp;');
GO
