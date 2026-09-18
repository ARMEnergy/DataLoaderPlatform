-- =============================================================================
-- 999_DropGenscapeObjects.sql
-- Database : Genscape
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
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly;
GO

-- ---- table types ------------------------------------------------------------
DROP TYPE IF EXISTS arm.OilFundamentalsCrudeStorageWeeklyTvp;
DROP TYPE IF EXISTS arm.OilFundamentalsCrudeTransportationWeeklyTvp;
GO

-- ---- tables -----------------------------------------------------------------
DROP TABLE IF EXISTS arm.OilFundamentals_CrudeStorage_Weekly;
DROP TABLE IF EXISTS arm.OilFundamentals_CrudeTransportation_Weekly;
GO
