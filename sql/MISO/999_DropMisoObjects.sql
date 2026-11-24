-- =============================================================================
-- 999_DropMisoObjects.sql
-- Drops the 12 objects the MISO loader owns in `Misc`, then the [miso] schema
-- only if it is empty, and nothing else:
--   procs:  miso.usp_MergeMisoZonalLoadHourly, miso.usp_MergeMisoLoadHourly,
--           miso.usp_MergeMisoFuelMixHourly,   miso.usp_MergeMisoLoadForecastMtlf
--   types:  miso.MisoZonalLoadHourlyType, miso.MisoLoadHourlyType,
--           miso.MisoFuelMixHourlyType,   miso.MisoLoadForecastMtlfType
--   tables: miso.miso_zonal_load_hourly, miso.miso_load_hourly,
--           miso.miso_fuel_mix_hourly,   miso.miso_load_forecast_mtlf
--   schema: miso (only when nothing at all remains in it)
--
-- Target: Loaders:MISO:ConnectionString -> Database=Misc, schema [miso].
--
-- DESTRUCTIVE. Once the loader has run, dropping the tables DESTROYS LOADED DATA.
-- MISO's lookback is rolling (~1,100 days), so anything older is gone for good.
-- `Misc.dbo` holds PJM's LIVE tables (dbo.pjm_*). Every statement here names
-- one MISO object explicitly, miso-qualified and existence-guarded. Never add a
-- wildcard, a dbo object, or any object not listed above.
--
-- Dependency order: procedures -> TVP types -> tables -> schema.
-- PRODUCTION WRITE when run against ARMH-OPSDB01. Run by hand, by a person.
-- =============================================================================

USE Misc;
GO

DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'Misc'
BEGIN
    RAISERROR('999_DropMisoObjects: current database is %s, not Misc. Nothing was dropped.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

DROP PROCEDURE IF EXISTS miso.usp_MergeMisoZonalLoadHourly;
DROP PROCEDURE IF EXISTS miso.usp_MergeMisoLoadHourly;
DROP PROCEDURE IF EXISTS miso.usp_MergeMisoFuelMixHourly;
DROP PROCEDURE IF EXISTS miso.usp_MergeMisoLoadForecastMtlf;
GO

DROP TYPE IF EXISTS miso.MisoZonalLoadHourlyType;
DROP TYPE IF EXISTS miso.MisoLoadHourlyType;
DROP TYPE IF EXISTS miso.MisoFuelMixHourlyType;
DROP TYPE IF EXISTS miso.MisoLoadForecastMtlfType;
GO

DROP TABLE IF EXISTS miso.miso_zonal_load_hourly;
DROP TABLE IF EXISTS miso.miso_load_hourly;
DROP TABLE IF EXISTS miso.miso_fuel_mix_hourly;
DROP TABLE IF EXISTS miso.miso_load_forecast_mtlf;
GO

-- Drop the schema only if NOTHING remains in it (objects, types, XML schema
-- collections). If anything else was added to [miso], it stays and is reported.
IF SCHEMA_ID(N'miso') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.objects                WHERE schema_id = SCHEMA_ID(N'miso'))
       AND NOT EXISTS (SELECT 1 FROM sys.types                 WHERE schema_id = SCHEMA_ID(N'miso'))
       AND NOT EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE schema_id = SCHEMA_ID(N'miso'))
        EXEC(N'DROP SCHEMA miso');
    ELSE
        PRINT N'999_DropMisoObjects: schema miso is not empty; it was left in place.';
END
GO

SET NOEXEC OFF;
GO
