-- =============================================================================
-- 999_DropOilXObjects.sql
-- Database : OilX
-- Schema   : arm
--
-- Tear-down, in dependency order. DESTRUCTIVE -- it drops the fact tables and
-- every row in them. Run it only to rebuild the loader's schema from scratch.
--
-- Order matters: a table type cannot be dropped while a procedure references it,
-- so procedures go first, then the types, then the tables. (Re-running 002 after
-- a type change requires exactly this sequence -- a table type cannot be
-- ALTERed.)
--
-- There are no foreign keys between these eight tables -- the feeds are
-- mutually independent -- so the table drops have no order requirement among
-- themselves.
-- =============================================================================

-- ---- procedures -------------------------------------------------------------
DROP PROCEDURE IF EXISTS arm.usp_ValidateLoad;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeCargoTracking;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeFloatingStorage;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeFlow;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeGlobalBalance;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeOilFieldProduction;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeRegionalBalance;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeSupplyDemand;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeTerminal;
GO

-- ---- table types ------------------------------------------------------------
DROP TYPE IF EXISTS arm.CargoTrackingTvp;
DROP TYPE IF EXISTS arm.FloatingStorageTvp;
DROP TYPE IF EXISTS arm.FlowTvp;
DROP TYPE IF EXISTS arm.GlobalBalanceTvp;
DROP TYPE IF EXISTS arm.OilFieldProductionTvp;
DROP TYPE IF EXISTS arm.RegionalBalanceTvp;
DROP TYPE IF EXISTS arm.SupplyDemandTvp;
DROP TYPE IF EXISTS arm.TerminalTvp;
GO

-- ---- tables -----------------------------------------------------------------
DROP TABLE IF EXISTS arm.CargoTracking;
DROP TABLE IF EXISTS arm.FloatingStorage;
DROP TABLE IF EXISTS arm.Flow;
DROP TABLE IF EXISTS arm.GlobalBalance;
DROP TABLE IF EXISTS arm.OilFieldProduction;
DROP TABLE IF EXISTS arm.RegionalBalance;
DROP TABLE IF EXISTS arm.SupplyDemand;
DROP TABLE IF EXISTS arm.Terminal;
GO
