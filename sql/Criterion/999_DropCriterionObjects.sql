-- =============================================================================
-- 999_DropCriterionObjects.sql
-- Database : Criterion
-- Schema   : arm
--
-- DESTRUCTIVE. Drops every object created by 001-003, including all data.
-- Intended for a clean rebuild in a development database.
--
-- ORDER MATTERS: procedures first (they reference the table types), then the
-- types (a type cannot be dropped while a proc references it), then the tables.
-- There are no foreign keys between these tables, so table order is free.
-- =============================================================================

-- ---- procedures -------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeFinancialMetadata;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeFinancialSeries;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeFinancialSeriesData;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeMiscPeriod;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeMiscUnit;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergePipelinesMetadata;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergePipelinesNominationPoint;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergePipelinesPointflows;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergePipelinesRegion;
GO

-- ---- table types ------------------------------------------------------------
DROP TYPE IF EXISTS arm.FinancialMetadataTvp;
DROP TYPE IF EXISTS arm.FinancialSeriesTvp;
DROP TYPE IF EXISTS arm.FinancialSeriesDataTvp;
DROP TYPE IF EXISTS arm.MiscPeriodTvp;
DROP TYPE IF EXISTS arm.MiscUnitTvp;
DROP TYPE IF EXISTS arm.PipelinesMetadataTvp;
DROP TYPE IF EXISTS arm.PipelinesNominationPointTvp;
DROP TYPE IF EXISTS arm.PipelinesPointflowsTvp;
DROP TYPE IF EXISTS arm.PipelinesRegionTvp;
GO

-- ---- tables -----------------------------------------------------------------
DROP TABLE IF EXISTS arm.Financial_SeriesData;
DROP TABLE IF EXISTS arm.Financial_Series;
DROP TABLE IF EXISTS arm.Financial_Metadata;
DROP TABLE IF EXISTS arm.Misc_Period;
DROP TABLE IF EXISTS arm.Misc_Unit;
DROP TABLE IF EXISTS arm.Pipelines_Pointflows;
DROP TABLE IF EXISTS arm.Pipelines_NominationPoint;
DROP TABLE IF EXISTS arm.Pipelines_Metadata;
DROP TABLE IF EXISTS arm.Pipelines_Region;
GO
