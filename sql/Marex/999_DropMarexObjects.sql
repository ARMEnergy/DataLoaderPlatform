-- =============================================================================
-- 999_DropMarexObjects.sql
-- Database : Marex
-- Schema   : arm
--
-- DESTRUCTIVE. Drops every object created by 001-003, including all data.
-- Intended for a clean rebuild in a development database.
--
-- ORDER MATTERS: procedures first (they reference the table types), then the
-- types (a type cannot be dropped while a proc references it), then the tables.
-- There are no foreign keys between these tables, so table order is free.
--
-- Note that dropping arm.Product / arm.Period / arm.PeriodGroup is NOT
-- recoverable from the gateway for retired entities: the snapshot only carries
-- what is live today, so a rebuild repopulates the dimensions with the CURRENT
-- catalogue and any row describing a product or period the vendor has since
-- retired is gone for good, leaving historical arm.ClosingPrice and
-- arm.MarketStatistic rows pointing at ids with no description. Drop the two
-- fact tables freely in development; think twice about the three dimensions.
-- =============================================================================

-- ---- procedures -------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeClosingPrice;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeMarketStatistic;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergePeriod;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergePeriodGroup;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeProduct;
GO

-- ---- table types ------------------------------------------------------------
DROP TYPE IF EXISTS arm.ClosingPriceTvp;
DROP TYPE IF EXISTS arm.MarketStatisticTvp;
DROP TYPE IF EXISTS arm.PeriodTvp;
DROP TYPE IF EXISTS arm.PeriodGroupTvp;
DROP TYPE IF EXISTS arm.ProductTvp;
GO

-- ---- tables -----------------------------------------------------------------
DROP TABLE IF EXISTS arm.ClosingPrice;
DROP TABLE IF EXISTS arm.MarketStatistic;
DROP TABLE IF EXISTS arm.Period;
DROP TABLE IF EXISTS arm.PeriodGroup;
DROP TABLE IF EXISTS arm.Product;
GO
