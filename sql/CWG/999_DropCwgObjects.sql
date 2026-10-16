-- =============================================================================
-- 999_DropCwgObjects.sql
-- Database : CWG
-- Schema   : arm   (dbo is NEVER touched -- see the warning below)
--
-- Tear-down, in dependency order, so 001-003 can rebuild the loader's schema
-- from scratch. Needed because 001/002 are creation-guarded
-- (IF OBJECT_ID(...) IS NULL / IF TYPE_ID(...) IS NULL): re-running them over
-- an existing table or table type is a silent NO-OP, so the 2026-10-05 reshape
-- cannot land until the old objects are gone.
--
-- -----------------------------------------------------------------------------
-- ⚠ DESTRUCTIVE -- READ THIS BEFORE RUNNING
-- -----------------------------------------------------------------------------
-- As measured on ARMH-OPSDB01 on 2026-10-06 this drops **~1.91 million loaded
-- rows** from the 18 arm fact tables. There is no undo. Back up first if any of
-- it matters:
--
--     arm.CityForecast             600,030    arm.SolarHourly             498,213
--     arm.WindForecastSubRegion    232,560    arm.WindForecast            175,104
--     arm.SolarForecast            145,920    arm.SolarForecastChange     123,600
--     arm.CityObservation           62,892    arm.CityGasForecast          24,111
--     arm.ISODegreeDays             22,176    arm.Regions9DegreeDays        9,504
--     arm.Regions5DegreeDays         5,280    arm.WindHourly                4,536
--     arm.DailyNormal                4,392    arm.Station                   1,158
--     arm.NationalDegreeDays         1,056    arm.WindTotalCapacityMW       1,026
--     arm.WindTotalCapacityPct       1,026    arm.WindTotalCapacityClimatology 342
--
-- 🔒 **THE dbo SCHEMA IS NOT TOUCHED.** This matters more here than in any other
-- loader: dbo holds the live incumbent dataset -- 35.5M rows in dbo.CityForecast
-- alone, ~95M across the schema -- in tables with THE SAME NAMES as the arm ones
-- (dbo.CityForecast, dbo.SolarForecast, dbo.Station, ...). Every statement below
-- is explicitly `arm.`-qualified. If you edit this file, never drop the prefix:
-- an unqualified DROP under a dbo-defaulting login would destroy production.
--
-- What is KEPT by default (and why):
--   arm.FileLog (3,088 rows), arm.Endpoint, arm.Status, arm.Region
-- Their shapes did NOT change in the reshape, so 001 skips re-creating them and
-- its seed MERGEs simply top the catalogs up (adding the 19th endpoint,
-- StateDegreeDayObservation). Keeping them preserves the per-request run/status
-- history. The facts no longer carry FileLogId, so nothing references them any
-- more. Drop them only if you want a bare database -- see the OPTIONAL section
-- at the end, which is commented out deliberately.
--
-- -----------------------------------------------------------------------------
-- Order matters
-- -----------------------------------------------------------------------------
--   1. procedures  -- a table type cannot be dropped while a procedure
--                     references it, and a table type cannot be ALTERed at all
--   2. table types
--   3. fact tables -- children of arm.FileLog via the old FK_<Table>_FileLog
--                     (dropping the table drops its FKs and indexes with it)
--   4. hub/lookups -- OPTIONAL, and only after the facts
--
-- The object lists below are the UNION of the pre- and post-reshape names, so
-- this script tears down whichever generation is currently installed and is
-- safely re-runnable. StateDegreeDayObservation* exists only after the reshape;
-- the IF EXISTS guards make its absence a no-op.
--
-- Then run, in order:  001_CreateCwgSchema.sql
--                      002_CreateCwgTvpTypes.sql
--                      003_CreateCwgProcedures.sql
-- =============================================================================

USE CWG;
GO

-- ----------------------------------------------------------------------------
-- Guard. USE above already pins the database; this is belt-and-braces for the
-- case where someone runs fragments of this file, or pastes it into a window
-- pointed at another database. SET NOEXEC ON persists across GO batches in the
-- same session, so every batch below is skipped rather than just this one.
-- ----------------------------------------------------------------------------
-- (RAISERROR substitution arguments must be constants or variables -- a function
--  call like DB_NAME() straight in the argument list is a syntax error.)
DECLARE @CurrentDb VARCHAR(128) = DB_NAME();
IF @CurrentDb <> 'CWG'
BEGIN
    RAISERROR('999_DropCwgObjects: current database is %s, not CWG. Nothing was dropped.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- ----------------------------------------------------------------------------
-- Inventory BEFORE the drop -- read this result set, then let the rest run.
-- ----------------------------------------------------------------------------
SELECT  s.name                                   AS [schema],
        t.name                                   AS [table],
        SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS [rows],
        CASE WHEN s.name = 'arm'
                  AND t.name NOT IN ('FileLog','Endpoint','Status','Region')
             THEN 'WILL BE DROPPED'
             WHEN s.name = 'arm' THEN 'kept (hub/lookup)'
             ELSE 'NOT TOUCHED'
        END                                      AS [action]
FROM        sys.tables   t
JOIN        sys.schemas  s ON s.schema_id = t.schema_id
LEFT JOIN   sys.partitions p ON p.object_id = t.object_id
GROUP BY s.name, t.name
ORDER BY [action], s.name, t.name;
GO

-- ---- 1. procedures ---------------------------------------------------------
-- usp_UpsertFileLog is included for a complete tear-down; 003 recreates it
-- (CREATE OR ALTER), and its signature did not change in the reshape.
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeCityForecast;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeCityGasForecast;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeCityObservation;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeDailyNormal;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeSolarForecast;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeSolarForecastChange;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeSolarHourly;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeStateDegreeDayObservation;  -- post-reshape only
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeStation;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeRegions5DegreeDays;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeRegions9DegreeDays;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeISODegreeDays;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeNationalDegreeDays;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeWindForecast;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeWindForecastSubRegion;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeWindHourly;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeWindTotalCapacityClimatology;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeWindTotalCapacityMW;
DROP PROCEDURE IF EXISTS arm.usp_BulkMergeWindTotalCapacityPct;
DROP PROCEDURE IF EXISTS arm.usp_UpsertFileLog;
GO

-- ---- 2. table types --------------------------------------------------------
-- These MUST go, even though their names are unchanged: CREATE TYPE in 002 is
-- IF TYPE_ID(...) IS NULL-guarded, so an old type would survive and the sink's
-- DataTable would then mismatch it by column count/order/type at run time.
DROP TYPE IF EXISTS arm.CityForecastTvp;
DROP TYPE IF EXISTS arm.CityGasForecastTvp;
DROP TYPE IF EXISTS arm.CityObservationTvp;
DROP TYPE IF EXISTS arm.DailyNormalTvp;
DROP TYPE IF EXISTS arm.SolarForecastTvp;
DROP TYPE IF EXISTS arm.SolarForecastChangeTvp;
DROP TYPE IF EXISTS arm.SolarHourlyTvp;
DROP TYPE IF EXISTS arm.StateDegreeDayObservationTvp;                 -- post-reshape only
DROP TYPE IF EXISTS arm.StationTvp;
DROP TYPE IF EXISTS arm.Regions5DegreeDaysTvp;
DROP TYPE IF EXISTS arm.Regions9DegreeDaysTvp;
DROP TYPE IF EXISTS arm.ISODegreeDaysTvp;
DROP TYPE IF EXISTS arm.NationalDegreeDaysTvp;
DROP TYPE IF EXISTS arm.WindForecastTvp;
DROP TYPE IF EXISTS arm.WindForecastSubRegionTvp;
DROP TYPE IF EXISTS arm.WindHourlyTvp;
DROP TYPE IF EXISTS arm.WindTotalCapacityClimatologyTvp;
DROP TYPE IF EXISTS arm.WindTotalCapacityMWTvp;
DROP TYPE IF EXISTS arm.WindTotalCapacityPctTvp;
GO

-- ---- 3. fact tables --------------------------------------------------------
-- Children of arm.FileLog in the pre-reshape model (FK_<Table>_FileLog); the
-- drop takes those FKs and every index with it. No fact references another
-- fact, so the order among them does not matter.
DROP TABLE IF EXISTS arm.CityForecast;
DROP TABLE IF EXISTS arm.CityGasForecast;
DROP TABLE IF EXISTS arm.CityObservation;
DROP TABLE IF EXISTS arm.DailyNormal;
DROP TABLE IF EXISTS arm.SolarForecast;
DROP TABLE IF EXISTS arm.SolarForecastChange;
DROP TABLE IF EXISTS arm.SolarHourly;
DROP TABLE IF EXISTS arm.StateDegreeDayObservation;                   -- post-reshape only
DROP TABLE IF EXISTS arm.Station;
DROP TABLE IF EXISTS arm.Regions5DegreeDays;
DROP TABLE IF EXISTS arm.Regions9DegreeDays;
DROP TABLE IF EXISTS arm.ISODegreeDays;
DROP TABLE IF EXISTS arm.NationalDegreeDays;
DROP TABLE IF EXISTS arm.WindForecast;
DROP TABLE IF EXISTS arm.WindForecastSubRegion;
DROP TABLE IF EXISTS arm.WindHourly;
DROP TABLE IF EXISTS arm.WindTotalCapacityClimatology;
DROP TABLE IF EXISTS arm.WindTotalCapacityMW;
DROP TABLE IF EXISTS arm.WindTotalCapacityPct;
GO

-- ---- 4. OPTIONAL: hub + lookups --------------------------------------------
-- COMMENTED OUT ON PURPOSE. You do NOT need this to deploy the reshape:
-- these four tables are unchanged, 001 leaves them alone, and its seed MERGEs
-- add the 19th endpoint. Uncommenting discards arm.FileLog's run/status history
-- (3,088 rows as of 2026-10-06). Order matters -- FileLog FKs to all three
-- lookups, so it goes first, and it can only go after the facts above.
--
-- DROP TABLE IF EXISTS arm.FileLog;
-- GO
-- DROP TABLE IF EXISTS arm.Endpoint;
-- DROP TABLE IF EXISTS arm.Status;
-- DROP TABLE IF EXISTS arm.Region;
-- GO

-- ---- verification ----------------------------------------------------------
-- Expect: zero arm fact tables, zero arm table types, zero arm procedures, and
-- the dbo count UNCHANGED at whatever it was before (19 tables / ~95M rows).
SELECT 'arm tables remaining'  AS [check],
       COUNT(*)                AS [count]
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'arm'
UNION ALL
SELECT 'arm table types remaining', COUNT(*)
FROM sys.table_types tt JOIN sys.schemas s ON s.schema_id = tt.schema_id
WHERE s.name = 'arm'
UNION ALL
SELECT 'arm procedures remaining', COUNT(*)
FROM sys.procedures p JOIN sys.schemas s ON s.schema_id = p.schema_id
WHERE s.name = 'arm'
UNION ALL
SELECT 'dbo tables (must be UNCHANGED)', COUNT(*)
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'dbo';
GO

SET NOEXEC OFF;
GO
