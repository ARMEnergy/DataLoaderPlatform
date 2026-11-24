-- =============================================================================
-- 003_CreateMisoProcedures.sql
-- Merge procedures for the MISO loader.
--   * miso.usp_MergeMisoZonalLoadHourly  (@Records miso.MisoZonalLoadHourlyType)  -> miso.miso_zonal_load_hourly
--   * miso.usp_MergeMisoLoadHourly       (@Records miso.MisoLoadHourlyType)       -> miso.miso_load_hourly
--   * miso.usp_MergeMisoFuelMixHourly    (@Records miso.MisoFuelMixHourlyType)    -> miso.miso_fuel_mix_hourly
--   * miso.usp_MergeMisoLoadForecastMtlf (@Records miso.MisoLoadForecastMtlfType) -> miso.miso_load_forecast_mtlf
--
-- Target: Loaders:MISO:ConnectionString -> Database=Misc, schema [miso].
-- Spec:   docs/design/MISO.md §2.5 (exactly PJM's pattern, sql/PJM/003).
--
-- Each proc:
--   1. THROWs 50001 if @Records holds a duplicate key (compared under the
--      database collation, i.e. exactly as the MERGE would). It never
--      de-duplicates: a silent de-dup either destroys a real row or hides that
--      the key no longer holds. The reader fails the unit first (design §4 rule
--      6); this is defence in depth so MERGE can never pick an arbitrary row.
--   2. MERGEs on the PK. The MTLF proc MERGEs into a CTE scoped to the batch's
--      init_date MIN..MAX, so the plan seeks the clustered PK instead of
--      considering the whole table; every batch row lies inside that range, so
--      the scoping cannot hide a match. WHEN MATCHED updates only if a non-key
--      value really differs, using a NULL-safe EXCEPT test, and then refreshes
--      ModifiedAtUtc. CreatedAtUtc is never in the UPDATE list (insert default
--      only). WHEN NOT MATCHED BY TARGET inserts the source columns; both audit
--      columns take their defaults. No WHEN NOT MATCHED BY SOURCE: nothing is
--      ever deleted.
--   3. Returns ONE result set, one row, one INT column RecordsProcessed = rows
--      actually inserted or changed. SqlSinkBase (ProcedureReturnsRowCount =
--      true) reads it with ExecuteScalar and needs an INT. A re-read that changes
--      nothing returns 0, which is correct.
--
-- No #temp tables (Misc's collation may differ from tempdb's).
-- CREATE OR ALTER keeps the script re-runnable. Run 001 and 002 first.
-- PRODUCTION WRITE when run against ARMH-OPSDB01. Run by hand, by a person.
-- =============================================================================

USE Misc;
GO

DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'Misc'
BEGIN
    RAISERROR('003_CreateMisoProcedures: current database is %s, not Misc. Nothing was created.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- -----------------------------------------------------------------------------
-- miso.usp_MergeMisoZonalLoadHourly   key (interval_start_utc)
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE miso.usp_MergeMisoZonalLoadHourly
    @Records miso.MisoZonalLoadHourlyType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.interval_start_utc,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.interval_start_utc
    HAVING COUNT(*) > 1
    ORDER BY r.interval_start_utc;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'miso.miso_zonal_load_hourly: duplicate key in batch (interval_start_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE miso.miso_zonal_load_hourly AS t
    USING @Records AS s
       ON t.interval_start_utc = s.interval_start_utc
    WHEN MATCHED AND EXISTS (
             -- EXCEPT treats NULL as equal to NULL, so this yields a row only when
             -- a value genuinely differs (including into and out of NULL).
             SELECT t.interval_start_local, t.interval_end_local, t.interval_end_utc,
                    t.lrz1, t.lrz2_7, t.lrz3_5, t.lrz4, t.lrz6, t.lrz8_9_10, t.miso
             EXCEPT
             SELECT s.interval_start_local, s.interval_end_local, s.interval_end_utc,
                    s.lrz1, s.lrz2_7, s.lrz3_5, s.lrz4, s.lrz6, s.lrz8_9_10, s.miso
         )
    THEN UPDATE SET
             t.interval_start_local = s.interval_start_local,
             t.interval_end_local   = s.interval_end_local,
             t.interval_end_utc     = s.interval_end_utc,
             t.lrz1                 = s.lrz1,
             t.lrz2_7               = s.lrz2_7,
             t.lrz3_5               = s.lrz3_5,
             t.lrz4                 = s.lrz4,
             t.lrz6                 = s.lrz6,
             t.lrz8_9_10            = s.lrz8_9_10,
             t.miso                 = s.miso,
             t.ModifiedAtUtc        = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (interval_start_local, interval_start_utc, interval_end_local, interval_end_utc,
                lrz1, lrz2_7, lrz3_5, lrz4, lrz6, lrz8_9_10, miso)
        VALUES (s.interval_start_local, s.interval_start_utc, s.interval_end_local, s.interval_end_utc,
                s.lrz1, s.lrz2_7, s.lrz3_5, s.lrz4, s.lrz6, s.lrz8_9_10, s.miso);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- miso.usp_MergeMisoLoadHourly   key (interval_start_utc)
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE miso.usp_MergeMisoLoadHourly
    @Records miso.MisoLoadHourlyType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.interval_start_utc,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.interval_start_utc
    HAVING COUNT(*) > 1
    ORDER BY r.interval_start_utc;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'miso.miso_load_hourly: duplicate key in batch (interval_start_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE miso.miso_load_hourly AS t
    USING @Records AS s
       ON t.interval_start_utc = s.interval_start_utc
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergeMisoZonalLoadHourly.
             SELECT t.interval_start_local, t.interval_end_local, t.interval_end_utc,
                    t.north, t.central, t.south, t.miso
             EXCEPT
             SELECT s.interval_start_local, s.interval_end_local, s.interval_end_utc,
                    s.north, s.central, s.south, s.miso
         )
    THEN UPDATE SET
             t.interval_start_local = s.interval_start_local,
             t.interval_end_local   = s.interval_end_local,
             t.interval_end_utc     = s.interval_end_utc,
             t.north                = s.north,
             t.central              = s.central,
             t.south                = s.south,
             t.miso                 = s.miso,
             t.ModifiedAtUtc        = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (interval_start_local, interval_start_utc, interval_end_local, interval_end_utc,
                north, central, south, miso)
        VALUES (s.interval_start_local, s.interval_start_utc, s.interval_end_local, s.interval_end_utc,
                s.north, s.central, s.south, s.miso);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- miso.usp_MergeMisoFuelMixHourly   key (interval_start_utc, region)
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE miso.usp_MergeMisoFuelMixHourly
    @Records miso.MisoFuelMixHourlyType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(20),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.interval_start_utc,
           @DupKey   = r.region,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.interval_start_utc, r.region
    HAVING COUNT(*) > 1
    ORDER BY r.interval_start_utc, r.region;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'miso.miso_fuel_mix_hourly: duplicate key in batch (interval_start_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', region=''' + CONVERT(NVARCHAR(20), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE miso.miso_fuel_mix_hourly AS t
    USING @Records AS s
       ON t.interval_start_utc = s.interval_start_utc
      AND t.region             = s.region
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergeMisoZonalLoadHourly.
             SELECT t.interval_start_local, t.interval_end_local, t.interval_end_utc,
                    t.coal, t.gas, t.nuclear, t.water, t.wind, t.solar, t.other, t.storage, t.total_mw
             EXCEPT
             SELECT s.interval_start_local, s.interval_end_local, s.interval_end_utc,
                    s.coal, s.gas, s.nuclear, s.water, s.wind, s.solar, s.other, s.storage, s.total_mw
         )
    THEN UPDATE SET
             t.interval_start_local = s.interval_start_local,
             t.interval_end_local   = s.interval_end_local,
             t.interval_end_utc     = s.interval_end_utc,
             t.coal                 = s.coal,
             t.gas                  = s.gas,
             t.nuclear              = s.nuclear,
             t.water                = s.water,
             t.wind                 = s.wind,
             t.solar                = s.solar,
             t.other                = s.other,
             t.storage              = s.storage,
             t.total_mw             = s.total_mw,
             t.ModifiedAtUtc        = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (interval_start_local, interval_start_utc, interval_end_local, interval_end_utc, region,
                coal, gas, nuclear, water, wind, solar, other, storage, total_mw)
        VALUES (s.interval_start_local, s.interval_start_utc, s.interval_end_local, s.interval_end_utc, s.region,
                s.coal, s.gas, s.nuclear, s.water, s.wind, s.solar, s.other, s.storage, s.total_mw);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- miso.usp_MergeMisoLoadForecastMtlf
--   key (init_date, interval_start_utc, local_resource_zone)
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE miso.usp_MergeMisoLoadForecastMtlf
    @Records miso.MisoLoadForecastMtlfType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupInit  DATE,
            @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(10),
            @DupCount INT,
            @MinInit  DATE,
            @MaxInit  DATE,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupInit  = r.init_date,
           @DupUtc   = r.interval_start_utc,
           @DupKey   = r.local_resource_zone,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.init_date, r.interval_start_utc, r.local_resource_zone
    HAVING COUNT(*) > 1
    ORDER BY r.init_date, r.interval_start_utc, r.local_resource_zone;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'miso.miso_load_forecast_mtlf: duplicate key in batch (init_date='
                 + CONVERT(NVARCHAR(10), @DupInit, 23)
                 + N', interval_start_utc=' + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', local_resource_zone=''' + CONVERT(NVARCHAR(10), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    -- Scope the target to the batch's init_date range (design §2.5).
    SELECT @MinInit = MIN(r.init_date),
           @MaxInit = MAX(r.init_date)
    FROM @Records AS r;

    WITH tgt AS
    (
        SELECT f.init_date, f.interval_start_local, f.interval_start_utc,
               f.interval_end_local, f.interval_end_utc,
               f.region, f.local_resource_zone, f.load_forecast, f.ModifiedAtUtc
        FROM miso.miso_load_forecast_mtlf AS f
        WHERE f.init_date >= @MinInit
          AND f.init_date <= @MaxInit
    )
    MERGE tgt AS t
    USING @Records AS s
       ON t.init_date           = s.init_date
      AND t.interval_start_utc  = s.interval_start_utc
      AND t.local_resource_zone = s.local_resource_zone
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergeMisoZonalLoadHourly.
             SELECT t.interval_start_local, t.interval_end_local, t.interval_end_utc,
                    t.region, t.load_forecast
             EXCEPT
             SELECT s.interval_start_local, s.interval_end_local, s.interval_end_utc,
                    s.region, s.load_forecast
         )
    THEN UPDATE SET
             t.interval_start_local = s.interval_start_local,
             t.interval_end_local   = s.interval_end_local,
             t.interval_end_utc     = s.interval_end_utc,
             t.region               = s.region,
             t.load_forecast        = s.load_forecast,
             t.ModifiedAtUtc        = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (init_date, interval_start_local, interval_start_utc, interval_end_local, interval_end_utc,
                region, local_resource_zone, load_forecast)
        VALUES (s.init_date, s.interval_start_local, s.interval_start_utc, s.interval_end_local, s.interval_end_utc,
                s.region, s.local_resource_zone, s.load_forecast)
    -- @MinInit/@MaxInit are locals: recompile so the scoped seek is planned on their real values.
    OPTION (RECOMPILE);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

SET NOEXEC OFF;
GO
