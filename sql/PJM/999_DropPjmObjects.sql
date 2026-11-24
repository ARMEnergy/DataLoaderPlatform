-- =============================================================================
-- 999_DropPjmObjects.sql
-- Drops the 24 objects the PJM loader owns in `Misc`, and nothing else:
--   procs:  dbo.usp_MergePjmFuelMix, dbo.usp_MergePjmLoad,
--           dbo.usp_MergePjmLoadMetered, dbo.usp_MergePjmLoadPrelim,
--           dbo.usp_MergePjmLoadEstimated, dbo.usp_MergePjmLoadForecastHist,
--           dbo.usp_MergePjmLoadForecastVeryShort, dbo.usp_MergePjmLoadForecast7Day
--   types:  dbo.PjmFuelMixType, dbo.PjmLoadType,
--           dbo.PjmLoadMeteredType, dbo.PjmLoadPrelimType,
--           dbo.PjmLoadEstimatedType, dbo.PjmLoadForecastHistType,
--           dbo.PjmLoadForecastVeryShortType, dbo.PjmLoadForecast7DayType
--   tables: dbo.pjm_fuel_mix, dbo.pjm_load,
--           dbo.pjm_load_metered, dbo.pjm_load_prelim,
--           dbo.pjm_load_estimated, dbo.pjm_load_forecast_hist,
--           dbo.pjm_load_forecast_very_short, dbo.pjm_load_forecast_7day
--
-- Target: Loaders:PJM:ConnectionString -> Database=Misc, schema [dbo].
--
-- DESTRUCTIVE. Once the loader has run, dropping the tables DESTROYS LOADED DATA.
-- Some of it is UNRECOVERABLE from PJM:
--   * dbo.pjm_load_forecast_7day      - PJM keeps only the latest evaluation;
--                                       no stored row can ever be re-read.
--   * dbo.pjm_load_forecast_very_short, dbo.pjm_load
--                                     - PJM keeps 30 days; anything older is gone.
-- `Misc` is a SHARED database that may hold other applications' objects, so every
-- statement names one PJM object explicitly, dbo-qualified and existence-guarded.
-- Never add a wildcard, a schema-wide drop, or any object not listed above.
--
-- Dependency order: procedures -> TVP types -> tables.
-- PRODUCTION WRITE when run against ARMH-OPSDB01. Run by hand, by a person.
-- =============================================================================

USE Misc;
GO

DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'Misc'
BEGIN
    RAISERROR('999_DropPjmObjects: current database is %s, not Misc. Nothing was dropped.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

DROP PROCEDURE IF EXISTS dbo.usp_MergePjmFuelMix;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoad;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoadMetered;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoadPrelim;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoadEstimated;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoadForecastHist;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoadForecastVeryShort;
DROP PROCEDURE IF EXISTS dbo.usp_MergePjmLoadForecast7Day;
GO

DROP TYPE IF EXISTS dbo.PjmFuelMixType;
DROP TYPE IF EXISTS dbo.PjmLoadType;
DROP TYPE IF EXISTS dbo.PjmLoadMeteredType;
DROP TYPE IF EXISTS dbo.PjmLoadPrelimType;
DROP TYPE IF EXISTS dbo.PjmLoadEstimatedType;
DROP TYPE IF EXISTS dbo.PjmLoadForecastHistType;
DROP TYPE IF EXISTS dbo.PjmLoadForecastVeryShortType;
DROP TYPE IF EXISTS dbo.PjmLoadForecast7DayType;
GO

DROP TABLE IF EXISTS dbo.pjm_fuel_mix;
DROP TABLE IF EXISTS dbo.pjm_load;
DROP TABLE IF EXISTS dbo.pjm_load_metered;
DROP TABLE IF EXISTS dbo.pjm_load_prelim;
DROP TABLE IF EXISTS dbo.pjm_load_estimated;
DROP TABLE IF EXISTS dbo.pjm_load_forecast_hist;
DROP TABLE IF EXISTS dbo.pjm_load_forecast_very_short;
DROP TABLE IF EXISTS dbo.pjm_load_forecast_7day;
GO

SET NOEXEC OFF;
GO
