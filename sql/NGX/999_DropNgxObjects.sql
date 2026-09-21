-- =============================================================================
-- 999_DropNgxObjects.sql
-- Database : NGX
-- Schema   : arm
--
-- DESTRUCTIVE. Drops every object created by 001-003, including all data.
-- Intended for a clean rebuild in a development database.
--
-- ORDER MATTERS: procedures first (they reference the table types), then the
-- types (a type cannot be dropped while a proc references it), then the tables.
-- There are no foreign keys between these tables, so table order is free.
--
-- SCOPED TO arm ON PURPOSE. This database also holds the LIVE incumbent
-- dbo.IndexPrice, dbo.IndexPrice_2, dbo.StripTradingSummary and dbo.[Index],
-- which this loader reads (dbo.[Index]) but never writes. Nothing below names
-- a dbo object, and nothing below should ever be edited to.
-- =============================================================================

-- ---- procedures -------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIndexPrice;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeStripTradingSummary;
GO

-- ---- table types ------------------------------------------------------------
DROP TYPE IF EXISTS arm.IndexPriceTvp;
DROP TYPE IF EXISTS arm.StripTradingSummaryTvp;
GO

-- ---- tables -----------------------------------------------------------------
DROP TABLE IF EXISTS arm.IndexPrice;
DROP TABLE IF EXISTS arm.StripTradingSummary;
GO
