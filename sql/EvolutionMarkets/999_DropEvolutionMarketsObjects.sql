-- =============================================================================
-- 999_DropEvolutionMarketsObjects.sql
-- Loader:   EvolutionMarkets  (EVO DataPipeline API - market-data history)
-- Database: EvolutionMarkets      Schema: arm
--
-- *** DESTRUCTIVE. THIS DROPS EVERY LOADED PRICE. ***
-- It is a DEVELOPMENT teardown so 001-003 can be re-run from clean. It is NOT part of
-- any deployment or upgrade path, and it must never run against a database whose
-- contents matter.
--
-- *** THE DATA IS NOT FULLY RECOVERABLE BY RE-RUNNING THE LOADER. ***
-- The vendor serves a ROLLING 60-DAY retention window (GET /v1/datasets ->
-- lookbackWindow = 60). Anything older than 60 days that this database had accumulated
-- CANNOT be re-fetched from the API - it is gone from the source. Take a backup first
-- if the table holds more than 60 days of history.
--
-- Order is the reverse of creation, children before parents, so no FK blocks a drop:
--   arm.MarketData -> arm.FileLog -> arm.Status / arm.Endpoint
-- Procedures and the TVP type are dropped first: a TYPE cannot be dropped while a
-- procedure references it.
--
-- The DATABASE itself is deliberately NOT dropped (that is a manual, deliberate act),
-- and neither is the [arm] SCHEMA - dropping it would fail while any other object in it
-- survives, and leaving it costs nothing on a re-run of 001.
--
-- Every statement is guarded, so this script is re-runnable and safe on a partially
-- created database.
-- =============================================================================

USE EvolutionMarkets;
GO

-- ---- Procedures first: they reference the TVP type -------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
GO
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeMarketData;
GO
DROP PROCEDURE IF EXISTS arm.usp_UpsertFileLog;
GO

-- ---- Then the TVP type ----------------------------------------------------------
IF TYPE_ID('arm.MarketDataTvp') IS NOT NULL
    DROP TYPE arm.MarketDataTvp;
GO

-- ---- Then the fact (child of arm.FileLog) ---------------------------------------
DROP TABLE IF EXISTS arm.MarketData;
GO

-- ---- Then the hub (child of arm.Endpoint / arm.Status) --------------------------
DROP TABLE IF EXISTS arm.FileLog;
GO

-- ---- Then the lookups -----------------------------------------------------------
DROP TABLE IF EXISTS arm.Status;
GO
DROP TABLE IF EXISTS arm.Endpoint;
GO
