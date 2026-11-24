-- =============================================================================
-- 003_CreatePjmProcedures.sql
-- Merge procedures for the PJM loader.
--   * dbo.usp_MergePjmFuelMix                (@Records dbo.PjmFuelMixType)               -> dbo.pjm_fuel_mix
--   * dbo.usp_MergePjmLoad                   (@Records dbo.PjmLoadType)                  -> dbo.pjm_load
--   * dbo.usp_MergePjmLoadMetered            (@Records dbo.PjmLoadMeteredType)           -> dbo.pjm_load_metered
--   * dbo.usp_MergePjmLoadPrelim             (@Records dbo.PjmLoadPrelimType)            -> dbo.pjm_load_prelim
--   * dbo.usp_MergePjmLoadEstimated          (@Records dbo.PjmLoadEstimatedType)         -> dbo.pjm_load_estimated
--   * dbo.usp_MergePjmLoadForecastHist       (@Records dbo.PjmLoadForecastHistType)      -> dbo.pjm_load_forecast_hist
--   * dbo.usp_MergePjmLoadForecastVeryShort  (@Records dbo.PjmLoadForecastVeryShortType) -> dbo.pjm_load_forecast_very_short
--   * dbo.usp_MergePjmLoadForecast7Day       (@Records dbo.PjmLoadForecast7DayType)      -> dbo.pjm_load_forecast_7day
--
-- Target: Loaders:PJM:ConnectionString -> Database=Misc, schema [dbo].
-- Spec:   docs/design/PJM.md §8, §14.9.
--
-- Each proc:
--   1. THROWs 50001 if @Records holds a duplicate key (compared under the
--      database collation, i.e. exactly as the MERGE would). It never
--      de-duplicates: a silent de-dup either destroys a real row or hides that
--      the key no longer holds. The reader fails the unit first; this is
--      defence in depth so MERGE can never pick an arbitrary row.
--   2. MERGEs on the key (all eight verified live 2026-11-23; see 001). The
--      three forecast procs MERGE into a CTE scoped to the batch's evaluated_at
--      MIN..MAX, so the plan seeks the clustered PK instead of considering the
--      whole (fast-growing) table; every batch row lies inside that range, so
--      the scoping cannot hide a match. WHEN MATCHED updates only if a non-key
--      value really differs, using a NULL-safe EXCEPT test, and then refreshes
--      ModifiedAtUtc. CreatedAtUtc is never in the UPDATE list (insert default
--      only). WHEN NOT MATCHED BY TARGET inserts the source columns; both audit
--      columns take their defaults. No WHEN NOT MATCHED BY SOURCE: a row PJM
--      withdraws is not deleted (design §13 item 6).
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
    RAISERROR('003_CreatePjmProcedures: current database is %s, not Misc. Nothing was created.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmFuelMix   key (datetime_beginning_utc, fuel_type) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmFuelMix
    @Records dbo.PjmFuelMixType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.datetime_beginning_utc,
           @DupKey   = r.fuel_type,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.datetime_beginning_utc, r.fuel_type
    HAVING COUNT(*) > 1
    ORDER BY r.datetime_beginning_utc, r.fuel_type;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_fuel_mix: duplicate key in batch (datetime_beginning_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', fuel_type=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE dbo.pjm_fuel_mix AS t
    USING @Records AS s
       ON t.datetime_beginning_utc = s.datetime_beginning_utc
      AND t.fuel_type              = s.fuel_type
    WHEN MATCHED AND EXISTS (
             -- EXCEPT treats NULL as equal to NULL, so this yields a row only when
             -- a value genuinely differs (including into and out of NULL).
             SELECT t.datetime_beginning_ept, t.mw, t.fuel_percentage_of_total, t.is_renewable
             EXCEPT
             SELECT s.datetime_beginning_ept, s.mw, s.fuel_percentage_of_total, s.is_renewable
         )
    THEN UPDATE SET
             t.datetime_beginning_ept   = s.datetime_beginning_ept,
             t.mw                       = s.mw,
             t.fuel_percentage_of_total = s.fuel_percentage_of_total,
             t.is_renewable             = s.is_renewable,
             t.ModifiedAtUtc            = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (datetime_beginning_utc, datetime_beginning_ept, fuel_type, mw, fuel_percentage_of_total, is_renewable)
        VALUES (s.datetime_beginning_utc, s.datetime_beginning_ept, s.fuel_type, s.mw, s.fuel_percentage_of_total, s.is_renewable);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoad   key (datetime_beginning_utc, area) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoad
    @Records dbo.PjmLoadType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.datetime_beginning_utc,
           @DupKey   = r.area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.datetime_beginning_utc, r.area
    HAVING COUNT(*) > 1
    ORDER BY r.datetime_beginning_utc, r.area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load: duplicate key in batch (datetime_beginning_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE dbo.pjm_load AS t
    USING @Records AS s
       ON t.datetime_beginning_utc = s.datetime_beginning_utc
      AND t.area                   = s.area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.datetime_beginning_ept, t.instantaneous_load
             EXCEPT
             SELECT s.datetime_beginning_ept, s.instantaneous_load
         )
    THEN UPDATE SET
             t.datetime_beginning_ept = s.datetime_beginning_ept,
             t.instantaneous_load     = s.instantaneous_load,
             t.ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (datetime_beginning_utc, datetime_beginning_ept, area, instantaneous_load)
        VALUES (s.datetime_beginning_utc, s.datetime_beginning_ept, s.area, s.instantaneous_load);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoadMetered   key (datetime_beginning_utc, load_area) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoadMetered
    @Records dbo.PjmLoadMeteredType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.datetime_beginning_utc,
           @DupKey   = r.load_area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.datetime_beginning_utc, r.load_area
    HAVING COUNT(*) > 1
    ORDER BY r.datetime_beginning_utc, r.load_area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load_metered: duplicate key in batch (datetime_beginning_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', load_area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE dbo.pjm_load_metered AS t
    USING @Records AS s
       ON t.datetime_beginning_utc = s.datetime_beginning_utc
      AND t.load_area              = s.load_area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.datetime_beginning_ept, t.nerc_region, t.mkt_region, t.zone, t.mw, t.is_verified
             EXCEPT
             SELECT s.datetime_beginning_ept, s.nerc_region, s.mkt_region, s.zone, s.mw, s.is_verified
         )
    THEN UPDATE SET
             t.datetime_beginning_ept = s.datetime_beginning_ept,
             t.nerc_region            = s.nerc_region,
             t.mkt_region             = s.mkt_region,
             t.zone                   = s.zone,
             t.mw                     = s.mw,
             t.is_verified            = s.is_verified,
             t.ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (datetime_beginning_utc, datetime_beginning_ept, nerc_region, mkt_region, zone, load_area, mw, is_verified)
        VALUES (s.datetime_beginning_utc, s.datetime_beginning_ept, s.nerc_region, s.mkt_region, s.zone, s.load_area, s.mw, s.is_verified);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoadPrelim   key (datetime_beginning_utc, load_area) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoadPrelim
    @Records dbo.PjmLoadPrelimType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.datetime_beginning_utc,
           @DupKey   = r.load_area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.datetime_beginning_utc, r.load_area
    HAVING COUNT(*) > 1
    ORDER BY r.datetime_beginning_utc, r.load_area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load_prelim: duplicate key in batch (datetime_beginning_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', load_area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE dbo.pjm_load_prelim AS t
    USING @Records AS s
       ON t.datetime_beginning_utc = s.datetime_beginning_utc
      AND t.load_area              = s.load_area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.datetime_beginning_ept, t.datetime_ending_utc, t.datetime_ending_ept, t.prelim_load_avg_hourly
             EXCEPT
             SELECT s.datetime_beginning_ept, s.datetime_ending_utc, s.datetime_ending_ept, s.prelim_load_avg_hourly
         )
    THEN UPDATE SET
             t.datetime_beginning_ept = s.datetime_beginning_ept,
             t.datetime_ending_utc    = s.datetime_ending_utc,
             t.datetime_ending_ept    = s.datetime_ending_ept,
             t.prelim_load_avg_hourly = s.prelim_load_avg_hourly,
             t.ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (datetime_beginning_utc, datetime_beginning_ept, datetime_ending_utc, datetime_ending_ept, load_area, prelim_load_avg_hourly)
        VALUES (s.datetime_beginning_utc, s.datetime_beginning_ept, s.datetime_ending_utc, s.datetime_ending_ept, s.load_area, s.prelim_load_avg_hourly);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoadEstimated   key (datetime_beginning_utc, load_area) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoadEstimated
    @Records dbo.PjmLoadEstimatedType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupUtc   DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupUtc   = r.datetime_beginning_utc,
           @DupKey   = r.load_area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.datetime_beginning_utc, r.load_area
    HAVING COUNT(*) > 1
    ORDER BY r.datetime_beginning_utc, r.load_area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load_estimated: duplicate key in batch (datetime_beginning_utc='
                 + CONVERT(NVARCHAR(19), @DupUtc, 120)
                 + N', load_area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    MERGE dbo.pjm_load_estimated AS t
    USING @Records AS s
       ON t.datetime_beginning_utc = s.datetime_beginning_utc
      AND t.load_area              = s.load_area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.datetime_beginning_ept, t.datetime_ending_utc, t.datetime_ending_ept, t.estimated_load_hourly
             EXCEPT
             SELECT s.datetime_beginning_ept, s.datetime_ending_utc, s.datetime_ending_ept, s.estimated_load_hourly
         )
    THEN UPDATE SET
             t.datetime_beginning_ept = s.datetime_beginning_ept,
             t.datetime_ending_utc    = s.datetime_ending_utc,
             t.datetime_ending_ept    = s.datetime_ending_ept,
             t.estimated_load_hourly  = s.estimated_load_hourly,
             t.ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (datetime_beginning_utc, datetime_beginning_ept, datetime_ending_utc, datetime_ending_ept, load_area, estimated_load_hourly)
        VALUES (s.datetime_beginning_utc, s.datetime_beginning_ept, s.datetime_ending_utc, s.datetime_ending_ept, s.load_area, s.estimated_load_hourly);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoadForecastHist
--   key (evaluated_at_utc, forecast_hour_beginning_utc, forecast_area) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoadForecastHist
    @Records dbo.PjmLoadForecastHistType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupEval  DATETIME2(0),
            @DupFcst  DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @MinEval  DATETIME2(0),
            @MaxEval  DATETIME2(0),
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupEval  = r.evaluated_at_utc,
           @DupFcst  = r.forecast_hour_beginning_utc,
           @DupKey   = r.forecast_area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.evaluated_at_utc, r.forecast_hour_beginning_utc, r.forecast_area
    HAVING COUNT(*) > 1
    ORDER BY r.evaluated_at_utc, r.forecast_hour_beginning_utc, r.forecast_area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load_forecast_hist: duplicate key in batch (evaluated_at_utc='
                 + CONVERT(NVARCHAR(19), @DupEval, 120)
                 + N', forecast_hour_beginning_utc=' + CONVERT(NVARCHAR(19), @DupFcst, 120)
                 + N', forecast_area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    -- Scope the target to the batch's evaluation range (design §14.9 hint).
    SELECT @MinEval = MIN(r.evaluated_at_utc),
           @MaxEval = MAX(r.evaluated_at_utc)
    FROM @Records AS r;

    WITH tgt AS
    (
        SELECT f.evaluated_at_utc, f.evaluated_at_ept,
               f.forecast_hour_beginning_utc, f.forecast_hour_beginning_ept,
               f.forecast_area, f.forecast_load_mw, f.ModifiedAtUtc
        FROM dbo.pjm_load_forecast_hist AS f
        WHERE f.evaluated_at_utc >= @MinEval
          AND f.evaluated_at_utc <= @MaxEval
    )
    MERGE tgt AS t
    USING @Records AS s
       ON t.evaluated_at_utc            = s.evaluated_at_utc
      AND t.forecast_hour_beginning_utc = s.forecast_hour_beginning_utc
      AND t.forecast_area               = s.forecast_area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.evaluated_at_ept, t.forecast_hour_beginning_ept, t.forecast_load_mw
             EXCEPT
             SELECT s.evaluated_at_ept, s.forecast_hour_beginning_ept, s.forecast_load_mw
         )
    THEN UPDATE SET
             t.evaluated_at_ept            = s.evaluated_at_ept,
             t.forecast_hour_beginning_ept = s.forecast_hour_beginning_ept,
             t.forecast_load_mw            = s.forecast_load_mw,
             t.ModifiedAtUtc               = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (evaluated_at_utc, evaluated_at_ept, forecast_hour_beginning_utc, forecast_hour_beginning_ept, forecast_area, forecast_load_mw)
        VALUES (s.evaluated_at_utc, s.evaluated_at_ept, s.forecast_hour_beginning_utc, s.forecast_hour_beginning_ept, s.forecast_area, s.forecast_load_mw)
    -- @MinEval/@MaxEval are locals: recompile so the scoped seek is planned on their real values.
    OPTION (RECOMPILE);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoadForecastVeryShort
--   key (evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area) - VERIFIED 2026-11-23
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoadForecastVeryShort
    @Records dbo.PjmLoadForecastVeryShortType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupEval  DATETIME2(0),
            @DupFcst  DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @MinEval  DATETIME2(0),
            @MaxEval  DATETIME2(0),
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupEval  = r.evaluated_at_utc,
           @DupFcst  = r.forecast_datetime_beginning_utc,
           @DupKey   = r.forecast_area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.evaluated_at_utc, r.forecast_datetime_beginning_utc, r.forecast_area
    HAVING COUNT(*) > 1
    ORDER BY r.evaluated_at_utc, r.forecast_datetime_beginning_utc, r.forecast_area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load_forecast_very_short: duplicate key in batch (evaluated_at_utc='
                 + CONVERT(NVARCHAR(19), @DupEval, 120)
                 + N', forecast_datetime_beginning_utc=' + CONVERT(NVARCHAR(19), @DupFcst, 120)
                 + N', forecast_area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    -- Scope the target to the batch's evaluation range (design §14.9 hint).
    SELECT @MinEval = MIN(r.evaluated_at_utc),
           @MaxEval = MAX(r.evaluated_at_utc)
    FROM @Records AS r;

    WITH tgt AS
    (
        SELECT f.evaluated_at_utc, f.evaluated_at_ept,
               f.forecast_datetime_beginning_utc, f.forecast_datetime_beginning_ept,
               f.forecast_datetime_ending_utc, f.forecast_datetime_ending_ept,
               f.forecast_area, f.forecast_load_mw, f.ModifiedAtUtc
        FROM dbo.pjm_load_forecast_very_short AS f
        WHERE f.evaluated_at_utc >= @MinEval
          AND f.evaluated_at_utc <= @MaxEval
    )
    MERGE tgt AS t
    USING @Records AS s
       ON t.evaluated_at_utc                = s.evaluated_at_utc
      AND t.forecast_datetime_beginning_utc = s.forecast_datetime_beginning_utc
      AND t.forecast_area                   = s.forecast_area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.evaluated_at_ept, t.forecast_datetime_beginning_ept,
                    t.forecast_datetime_ending_utc, t.forecast_datetime_ending_ept, t.forecast_load_mw
             EXCEPT
             SELECT s.evaluated_at_ept, s.forecast_datetime_beginning_ept,
                    s.forecast_datetime_ending_utc, s.forecast_datetime_ending_ept, s.forecast_load_mw
         )
    THEN UPDATE SET
             t.evaluated_at_ept                = s.evaluated_at_ept,
             t.forecast_datetime_beginning_ept = s.forecast_datetime_beginning_ept,
             t.forecast_datetime_ending_utc    = s.forecast_datetime_ending_utc,
             t.forecast_datetime_ending_ept    = s.forecast_datetime_ending_ept,
             t.forecast_load_mw                = s.forecast_load_mw,
             t.ModifiedAtUtc                   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (evaluated_at_utc, evaluated_at_ept, forecast_datetime_beginning_utc, forecast_datetime_beginning_ept,
                forecast_datetime_ending_utc, forecast_datetime_ending_ept, forecast_area, forecast_load_mw)
        VALUES (s.evaluated_at_utc, s.evaluated_at_ept, s.forecast_datetime_beginning_utc, s.forecast_datetime_beginning_ept,
                s.forecast_datetime_ending_utc, s.forecast_datetime_ending_ept, s.forecast_area, s.forecast_load_mw)
    -- @MinEval/@MaxEval are locals: recompile so the scoped seek is planned on their real values.
    OPTION (RECOMPILE);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

-- -----------------------------------------------------------------------------
-- dbo.usp_MergePjmLoadForecast7Day
--   key (evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area) - VERIFIED 2026-11-23
-- Re-reading an evaluation already stored changes 0 rows and returns
-- RecordsProcessed = 0 (design §14.8); no separate "already stored" pre-check.
-- -----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_MergePjmLoadForecast7Day
    @Records dbo.PjmLoadForecast7DayType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DupEval  DATETIME2(0),
            @DupFcst  DATETIME2(0),
            @DupKey   VARCHAR(50),
            @DupCount INT,
            @MinEval  DATETIME2(0),
            @MaxEval  DATETIME2(0),
            @Msg      NVARCHAR(2048);

    SELECT TOP (1)
           @DupEval  = r.evaluated_at_datetime_utc,
           @DupFcst  = r.forecast_datetime_beginning_utc,
           @DupKey   = r.forecast_area,
           @DupCount = COUNT(*)
    FROM @Records AS r
    GROUP BY r.evaluated_at_datetime_utc, r.forecast_datetime_beginning_utc, r.forecast_area
    HAVING COUNT(*) > 1
    ORDER BY r.evaluated_at_datetime_utc, r.forecast_datetime_beginning_utc, r.forecast_area;

    IF @DupCount IS NOT NULL
    BEGIN
        SET @Msg = N'dbo.pjm_load_forecast_7day: duplicate key in batch (evaluated_at_datetime_utc='
                 + CONVERT(NVARCHAR(19), @DupEval, 120)
                 + N', forecast_datetime_beginning_utc=' + CONVERT(NVARCHAR(19), @DupFcst, 120)
                 + N', forecast_area=''' + CONVERT(NVARCHAR(50), @DupKey)
                 + N''') occurs ' + CONVERT(NVARCHAR(10), @DupCount)
                 + N' times; nothing was written. The verified key no longer holds.';
        THROW 50001, @Msg, 1;
    END;

    -- Scope the target to the batch's evaluation range (design §14.9 hint).
    SELECT @MinEval = MIN(r.evaluated_at_datetime_utc),
           @MaxEval = MAX(r.evaluated_at_datetime_utc)
    FROM @Records AS r;

    WITH tgt AS
    (
        SELECT f.evaluated_at_datetime_utc, f.evaluated_at_datetime_ept,
               f.forecast_datetime_beginning_utc, f.forecast_datetime_beginning_ept,
               f.forecast_datetime_ending_utc, f.forecast_datetime_ending_ept,
               f.forecast_area, f.forecast_load_mw, f.ModifiedAtUtc
        FROM dbo.pjm_load_forecast_7day AS f
        WHERE f.evaluated_at_datetime_utc >= @MinEval
          AND f.evaluated_at_datetime_utc <= @MaxEval
    )
    MERGE tgt AS t
    USING @Records AS s
       ON t.evaluated_at_datetime_utc       = s.evaluated_at_datetime_utc
      AND t.forecast_datetime_beginning_utc = s.forecast_datetime_beginning_utc
      AND t.forecast_area                   = s.forecast_area
    WHEN MATCHED AND EXISTS (
             -- NULL-safe change test; see usp_MergePjmFuelMix.
             SELECT t.evaluated_at_datetime_ept, t.forecast_datetime_beginning_ept,
                    t.forecast_datetime_ending_utc, t.forecast_datetime_ending_ept, t.forecast_load_mw
             EXCEPT
             SELECT s.evaluated_at_datetime_ept, s.forecast_datetime_beginning_ept,
                    s.forecast_datetime_ending_utc, s.forecast_datetime_ending_ept, s.forecast_load_mw
         )
    THEN UPDATE SET
             t.evaluated_at_datetime_ept       = s.evaluated_at_datetime_ept,
             t.forecast_datetime_beginning_ept = s.forecast_datetime_beginning_ept,
             t.forecast_datetime_ending_utc    = s.forecast_datetime_ending_utc,
             t.forecast_datetime_ending_ept    = s.forecast_datetime_ending_ept,
             t.forecast_load_mw                = s.forecast_load_mw,
             t.ModifiedAtUtc                   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (evaluated_at_datetime_utc, evaluated_at_datetime_ept, forecast_datetime_beginning_utc, forecast_datetime_beginning_ept,
                forecast_datetime_ending_utc, forecast_datetime_ending_ept, forecast_area, forecast_load_mw)
        VALUES (s.evaluated_at_datetime_utc, s.evaluated_at_datetime_ept, s.forecast_datetime_beginning_utc, s.forecast_datetime_beginning_ept,
                s.forecast_datetime_ending_utc, s.forecast_datetime_ending_ept, s.forecast_area, s.forecast_load_mw)
    -- @MinEval/@MaxEval are locals: recompile so the scoped seek is planned on their real values.
    OPTION (RECOMPILE);

    SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed;
END
GO

SET NOEXEC OFF;
GO
