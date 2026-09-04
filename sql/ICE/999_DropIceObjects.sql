-- =============================================================================
-- 999_DropIceObjects.sql
-- Database : ICE
-- Schema   : arm
--
-- DESTRUCTIVE. Drops every object created by 001-003, including all data.
-- Intended for a clean rebuild in a development database.
--
-- ORDER MATTERS: procedures first (they reference the table types), then the
-- types (a type cannot be dropped while a proc references it), then the tables.
-- arm.FileLog before arm.Status because of the FK.
-- =============================================================================

-- ---- procedures -------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_UpsertFileLog;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeEnvFutures;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeEnvOptions;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeFutures;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeCrudeOilIndex;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeCrudeOilIndexTrades;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIceClearedPowerFutures;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIceClearedPowerOptions;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIcefcaOptions;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIcefusFinOptions;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIcefusSoftOptions;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeIfllOptions;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeOptions;
GO

-- ---- table types ------------------------------------------------------------
DROP TYPE IF EXISTS arm.EnvFuturesTvp;
DROP TYPE IF EXISTS arm.EnvOptionsTvp;
DROP TYPE IF EXISTS arm.FuturesTvp;
DROP TYPE IF EXISTS arm.CrudeOilIndexTvp;
DROP TYPE IF EXISTS arm.CrudeOilIndexTradesTvp;
DROP TYPE IF EXISTS arm.PowerFuturesTvp;
DROP TYPE IF EXISTS arm.PowerOptionsTvp;
DROP TYPE IF EXISTS arm.FcaOptionsTvp;
DROP TYPE IF EXISTS arm.FusFinOptionsTvp;
DROP TYPE IF EXISTS arm.FusSoftOptionsTvp;
DROP TYPE IF EXISTS arm.IfllOptionsTvp;
DROP TYPE IF EXISTS arm.OptionsTvp;
GO

-- ---- fact tables ------------------------------------------------------------
DROP TABLE IF EXISTS arm.EnvFutures;
DROP TABLE IF EXISTS arm.EnvOptions;
DROP TABLE IF EXISTS arm.Futures;
DROP TABLE IF EXISTS arm.ICE_Crude_Oil_Index;
DROP TABLE IF EXISTS arm.ICE_Crude_Oil_Index_Trades;
DROP TABLE IF EXISTS arm.ICEClearedPowerFutures;
DROP TABLE IF EXISTS arm.ICEClearedPowerOptions;
DROP TABLE IF EXISTS arm.ICEFCA_Options;
DROP TABLE IF EXISTS arm.ICEFUS_FinOptions;
DROP TABLE IF EXISTS arm.ICEFUS_SoftOptions;
DROP TABLE IF EXISTS arm.IFLL_Options;
DROP TABLE IF EXISTS arm.Options;
GO

-- ---- audit hub (FileLog first: FK to Status) --------------------------------
DROP TABLE IF EXISTS arm.FileLog;
DROP TABLE IF EXISTS arm.Status;
GO
