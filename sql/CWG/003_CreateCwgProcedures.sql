-- =============================================================================
-- 003_CreateCwgProcedures.sql
-- Stored procedures for the CWG loader (schema [arm]):
--   * arm.usp_UpsertFileLog                 — per-request hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMerge<Table>  (×18)       — TVP bulk upsert into each fact table.
--
-- LOAD ORDER the procs imply (per request/file, StormVista posture):
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId (called for EVERY outcome:
--      Success / NotAvailable / Failed).
--   2) On rows > 0, stamp that FileLogId onto every TVP row and call the matching
--      arm.usp_BulkMerge<Table>.
--
-- Each bulk-merge proc:
--   * MERGEs on the endpoint's NATURAL key (§6) — NOT on FileLogId.
--   * De-dups the batch on that merge key (ROW_NUMBER over the TVP) before MERGE,
--     so an in-file duplicate key cannot trigger "MERGE ... same row more than
--     once". An arbitrary row per key is kept (each file is an immutable snapshot).
--   * On MATCHED updates the data columns AND FileLogId (provenance refresh) AND
--     ModifiedAtUtc; on NOT MATCHED inserts.
--   * Returns SELECT @@ROWCOUNT AS RecordsProcessed so the platform's SqlSinkBase
--     (ProcedureReturnsRowCount = true) can read the count.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable, and validate inputs.
-- Run 001 and 002 first. This script assumes CWG is the current database.
-- =============================================================================

USE CWG;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog — upsert one FileLog (hub) row per request and RETURN its
-- FileLogId. Called once per request for ALL outcomes. Region / Variant /
-- RepresentativeDate are NULL-able; the MERGE matches them with explicit
-- NULL-equality (col = col OR (col IS NULL AND col IS NULL)) so undated /
-- no-region requests reuse a single stable hub row. Endpoint / Status are still
-- passed BY NAME (unchanged signature) and resolved here to their surrogate
-- arm.Endpoint / arm.Status .Id (a miss RAISERRORs — fixed catalogs seeded in
-- 001); Region is passed by name and get-or-created in arm.Region (non-NULL
-- only), its Id stored as RegionId. No proc signature change.
--
-- RETURN CONTRACT: emits exactly one result set, one row, one column "FileLogId"
-- (the C# reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id
-- which yields the Id for BOTH the inserted and the updated branch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @Endpoint           VARCHAR(40),
    @Region             VARCHAR(16)   = NULL,
    @Variant            VARCHAR(16)   = NULL,
    @RepresentativeDate DATE          = NULL,
    @StatusLabel        VARCHAR(20),
    @HttpStatus         INT           = NULL,
    @RequestPath        NVARCHAR(400),
    @RowCount           INT           = 0
AS
BEGIN
    SET NOCOUNT ON;

    -- ---- Validate required inputs -------------------------------------------
    IF @Endpoint IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @Endpoint, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- ---- Resolve the lookup surrogate Ids ------------------------------------
    -- Endpoint / Status are fixed catalogs (seeded in 001) — a miss is a bug, so
    -- fail loudly rather than feed a NULL into the NOT NULL FK columns.
    DECLARE @EndpointId INT = (SELECT Id FROM arm.Endpoint WHERE [Name] = @Endpoint);
    IF @EndpointId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Endpoint ''%s''.', 16, 1, @Endpoint);
        RETURN;
    END

    DECLARE @StatusId INT = (SELECT Id FROM arm.Status WHERE [Name] = @StatusLabel);
    IF @StatusId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Status ''%s''.', 16, 1, @StatusLabel);
        RETURN;
    END

    -- Region is get-or-create, and only for a non-NULL filename region; a NULL
    -- region stays a NULL RegionId (never registers a row). Re-select after the
    -- guarded insert so a concurrent insert of the same name is tolerated.
    DECLARE @RegionId INT = NULL;
    IF @Region IS NOT NULL
    BEGIN
        SELECT @RegionId = Id FROM arm.Region WHERE [Name] = @Region;
        IF @RegionId IS NULL
        BEGIN
            INSERT arm.Region ([Name])
            SELECT @Region WHERE NOT EXISTS (SELECT 1 FROM arm.Region WHERE [Name] = @Region);
            SELECT @RegionId = Id FROM arm.Region WHERE [Name] = @Region;
        END
    END

    -- ---- Upsert the hub row, capturing the resulting Id ----------------------
    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE arm.FileLog AS tgt
    USING (SELECT @EndpointId AS EndpointId,
                  @RegionId   AS RegionId,
                  @Variant    AS Variant,
                  @RepresentativeDate AS RepresentativeDate) AS src
       ON  tgt.EndpointId = src.EndpointId
       AND (tgt.RegionId = src.RegionId OR (tgt.RegionId IS NULL AND src.RegionId IS NULL))
       AND (tgt.Variant  = src.Variant  OR (tgt.Variant  IS NULL AND src.Variant  IS NULL))
       AND (tgt.RepresentativeDate = src.RepresentativeDate
            OR (tgt.RepresentativeDate IS NULL AND src.RepresentativeDate IS NULL))
    WHEN MATCHED THEN UPDATE SET
        StatusId       = @StatusId,
        HttpStatus     = @HttpStatus,
        RequestPath    = @RequestPath,
        [RowCount]     = @RowCount,
        LastCheckedUtc = SYSUTCDATETIME(),
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointId, RegionId, Variant, RepresentativeDate,
                StatusId, HttpStatus, [RowCount], RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.RegionId, src.Variant, src.RepresentativeDate,
                @StatusId, @HttpStatus, @RowCount, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- 1. arm.usp_BulkMergeCityForecast — key (Region, Station, ProductionDate, ForecastDate).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCityForecast
    @Records arm.CityForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.CityForecast AS tgt
    USING (
        SELECT FileLogId, Region, ProductionDate, ForecastDate, Station,
               FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd, Units
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, Station, ProductionDate, ForecastDate
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region         = src.Region
       AND tgt.Station        = src.Station
       AND tgt.ProductionDate = src.ProductionDate
       AND tgt.ForecastDate   = src.ForecastDate
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        FcstMin       = src.FcstMin,
        FcstMax       = src.FcstMax,
        FcstAvg       = src.FcstAvg,
        NormMin       = src.NormMin,
        NormMax       = src.NormMax,
        Hdd           = src.Hdd,
        Cdd           = src.Cdd,
        Units         = src.Units,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, ProductionDate, ForecastDate, Station,
                FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd, Units)
        VALUES (src.FileLogId, src.Region, src.ProductionDate, src.ForecastDate, src.Station,
                src.FcstMin, src.FcstMax, src.FcstAvg, src.NormMin, src.NormMax, src.Hdd, src.Cdd, src.Units);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 2. arm.usp_BulkMergeCityGasForecast — key (Station, ProductionDate, ForecastDate).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCityGasForecast
    @Records arm.CityGasForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.CityGasForecast AS tgt
    USING (
        SELECT FileLogId, ProductionDate, ForecastDate, Station,
               FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Station, ProductionDate, ForecastDate
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Station        = src.Station
       AND tgt.ProductionDate = src.ProductionDate
       AND tgt.ForecastDate   = src.ForecastDate
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        FcstMin       = src.FcstMin,
        FcstMax       = src.FcstMax,
        FcstAvg       = src.FcstAvg,
        NormMin       = src.NormMin,
        NormMax       = src.NormMax,
        Hdd           = src.Hdd,
        Cdd           = src.Cdd,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ProductionDate, ForecastDate, Station,
                FcstMin, FcstMax, FcstAvg, NormMin, NormMax, Hdd, Cdd)
        VALUES (src.FileLogId, src.ProductionDate, src.ForecastDate, src.Station,
                src.FcstMin, src.FcstMax, src.FcstAvg, src.NormMin, src.NormMax, src.Hdd, src.Cdd);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 3. arm.usp_BulkMergeCityObservation — key (Region, Station, ObsDate).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCityObservation
    @Records arm.CityObservationTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.CityObservation AS tgt
    USING (
        SELECT FileLogId, Region, ObsDate, Station, MinTemp, MaxTemp, Hdd, Cdd
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, Station, ObsDate
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region  = src.Region
       AND tgt.Station = src.Station
       AND tgt.ObsDate = src.ObsDate
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        MinTemp       = src.MinTemp,
        MaxTemp       = src.MaxTemp,
        Hdd           = src.Hdd,
        Cdd           = src.Cdd,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, ObsDate, Station, MinTemp, MaxTemp, Hdd, Cdd)
        VALUES (src.FileLogId, src.Region, src.ObsDate, src.Station, src.MinTemp, src.MaxTemp, src.Hdd, src.Cdd);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 4. arm.usp_BulkMergeDailyNormal — key (MonthDay, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeDailyNormal
    @Records arm.DailyNormalTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.DailyNormal AS tgt
    USING (
        SELECT FileLogId, MonthDay, Region, NormalMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY MonthDay, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.MonthDay = src.MonthDay
       AND tgt.Region   = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        NormalMw      = src.NormalMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, MonthDay, Region, NormalMw)
        VALUES (src.FileLogId, src.MonthDay, src.Region, src.NormalMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 5. arm.usp_BulkMergeSolarForecast — key (Region, InitDate, ForecastDate, HourOfDay).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSolarForecast
    @Records arm.SolarForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.SolarForecast AS tgt
    USING (
        SELECT FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, InitDate, ForecastDate, HourOfDay
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region       = src.Region
       AND tgt.InitDate     = src.InitDate
       AND tgt.ForecastDate = src.ForecastDate
       AND tgt.HourOfDay    = src.HourOfDay
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        HourLabel     = src.HourLabel,
        ValueMw       = src.ValueMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw)
        VALUES (src.FileLogId, src.Region, src.InitDate, src.ForecastDate, src.HourLabel, src.HourOfDay, src.ValueMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 6. arm.usp_BulkMergeSolarForecastChange — key (Region, InitDate, ForecastDate, HourOfDay).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSolarForecastChange
    @Records arm.SolarForecastChangeTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.SolarForecastChange AS tgt
    USING (
        SELECT FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ChangeMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, InitDate, ForecastDate, HourOfDay
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region       = src.Region
       AND tgt.InitDate     = src.InitDate
       AND tgt.ForecastDate = src.ForecastDate
       AND tgt.HourOfDay    = src.HourOfDay
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        HourLabel     = src.HourLabel,
        ChangeMw      = src.ChangeMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ChangeMw)
        VALUES (src.FileLogId, src.Region, src.InitDate, src.ForecastDate, src.HourLabel, src.HourOfDay, src.ChangeMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 7. arm.usp_BulkMergeSolarHourly — key (HourEndingUtc, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSolarHourly
    @Records arm.SolarHourlyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.SolarHourly AS tgt
    USING (
        SELECT FileLogId, HourEndingUtc, Region, ActualMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY HourEndingUtc, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.HourEndingUtc = src.HourEndingUtc
       AND tgt.Region        = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        ActualMw      = src.ActualMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, HourEndingUtc, Region, ActualMw)
        VALUES (src.FileLogId, src.HourEndingUtc, src.Region, src.ActualMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 8. arm.usp_BulkMergeNationalDegreeDays — key (RunDate, Dates).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeNationalDegreeDays
    @Records arm.NationalDegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.NationalDegreeDays AS tgt
    USING (
        SELECT FileLogId, RunDate, Dates, NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
               PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
               ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY, IsForecast
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY RunDate, Dates ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate = src.RunDate
       AND tgt.Dates   = src.Dates
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        NgHdd         = src.NgHdd,
        NgHdd30y      = src.NgHdd30y,
        NgHdd10y      = src.NgHdd10y,
        NgHddLastY    = src.NgHddLastY,
        PopCdd        = src.PopCdd,
        PopCdd30y     = src.PopCdd30y,
        PopCdd10y     = src.PopCdd10y,
        PopCddLastY   = src.PopCddLastY,
        ElecCdd       = src.ElecCdd,
        ElecCdd30y    = src.ElecCdd30y,
        ElecCdd10y    = src.ElecCdd10y,
        ElecCddLastY  = src.ElecCddLastY,
        IsForecast    = src.IsForecast,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RunDate, Dates, NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
                PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
                ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY, IsForecast)
        VALUES (src.FileLogId, src.RunDate, src.Dates, src.NgHdd, src.NgHdd30y, src.NgHdd10y, src.NgHddLastY,
                src.PopCdd, src.PopCdd30y, src.PopCdd10y, src.PopCddLastY,
                src.ElecCdd, src.ElecCdd30y, src.ElecCdd10y, src.ElecCddLastY, src.IsForecast);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 16. arm.usp_BulkMergeRegions5DegreeDays — key (RunDate, Dates, RegionName).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeRegions5DegreeDays
    @Records arm.Regions5DegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Regions5DegreeDays AS tgt
    USING (
        SELECT FileLogId, RunDate, Dates, RegionName,
               NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
               PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
               ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY,
               IsForecast, GasWeight, ElctWeight, PopWeight
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY RunDate, Dates, RegionName ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate    = src.RunDate
       AND tgt.Dates      = src.Dates
       AND tgt.RegionName = src.RegionName
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        NgHdd         = src.NgHdd,
        NgHdd30y      = src.NgHdd30y,
        NgHdd10y      = src.NgHdd10y,
        NgHddLastY    = src.NgHddLastY,
        PopCdd        = src.PopCdd,
        PopCdd30y     = src.PopCdd30y,
        PopCdd10y     = src.PopCdd10y,
        PopCddLastY   = src.PopCddLastY,
        ElecCdd       = src.ElecCdd,
        ElecCdd30y    = src.ElecCdd30y,
        ElecCdd10y    = src.ElecCdd10y,
        ElecCddLastY  = src.ElecCddLastY,
        IsForecast    = src.IsForecast,
        GasWeight     = src.GasWeight,
        ElctWeight    = src.ElctWeight,
        PopWeight     = src.PopWeight,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RunDate, Dates, RegionName,
                NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
                PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
                ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY,
                IsForecast, GasWeight, ElctWeight, PopWeight)
        VALUES (src.FileLogId, src.RunDate, src.Dates, src.RegionName,
                src.NgHdd, src.NgHdd30y, src.NgHdd10y, src.NgHddLastY,
                src.PopCdd, src.PopCdd30y, src.PopCdd10y, src.PopCddLastY,
                src.ElecCdd, src.ElecCdd30y, src.ElecCdd10y, src.ElecCddLastY,
                src.IsForecast, src.GasWeight, src.ElctWeight, src.PopWeight);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 17. arm.usp_BulkMergeRegions9DegreeDays — key (RunDate, Dates, RegionName).
--     Identical column set to #16 (Regions5), only the RegionName value set differs.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeRegions9DegreeDays
    @Records arm.Regions9DegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Regions9DegreeDays AS tgt
    USING (
        SELECT FileLogId, RunDate, Dates, RegionName,
               NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
               PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
               ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY,
               IsForecast, GasWeight, ElctWeight, PopWeight
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY RunDate, Dates, RegionName ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate    = src.RunDate
       AND tgt.Dates      = src.Dates
       AND tgt.RegionName = src.RegionName
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        NgHdd         = src.NgHdd,
        NgHdd30y      = src.NgHdd30y,
        NgHdd10y      = src.NgHdd10y,
        NgHddLastY    = src.NgHddLastY,
        PopCdd        = src.PopCdd,
        PopCdd30y     = src.PopCdd30y,
        PopCdd10y     = src.PopCdd10y,
        PopCddLastY   = src.PopCddLastY,
        ElecCdd       = src.ElecCdd,
        ElecCdd30y    = src.ElecCdd30y,
        ElecCdd10y    = src.ElecCdd10y,
        ElecCddLastY  = src.ElecCddLastY,
        IsForecast    = src.IsForecast,
        GasWeight     = src.GasWeight,
        ElctWeight    = src.ElctWeight,
        PopWeight     = src.PopWeight,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RunDate, Dates, RegionName,
                NgHdd, NgHdd30y, NgHdd10y, NgHddLastY,
                PopCdd, PopCdd30y, PopCdd10y, PopCddLastY,
                ElecCdd, ElecCdd30y, ElecCdd10y, ElecCddLastY,
                IsForecast, GasWeight, ElctWeight, PopWeight)
        VALUES (src.FileLogId, src.RunDate, src.Dates, src.RegionName,
                src.NgHdd, src.NgHdd30y, src.NgHdd10y, src.NgHddLastY,
                src.PopCdd, src.PopCdd30y, src.PopCdd10y, src.PopCddLastY,
                src.ElecCdd, src.ElecCdd30y, src.ElecCdd10y, src.ElecCddLastY,
                src.IsForecast, src.GasWeight, src.ElctWeight, src.PopWeight);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 18. arm.usp_BulkMergeISODegreeDays — key (RunDate, Dates, RegionName).
--     DIVERGENT 11-col shape: POP_HDD family, no ELEC family, no weight columns.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeISODegreeDays
    @Records arm.ISODegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.ISODegreeDays AS tgt
    USING (
        SELECT FileLogId, RunDate, Dates, RegionName,
               PopHdd, PopHdd30y, PopHdd10y, PopHddLastY,
               PopCdd, PopCdd30y, PopCdd10y, PopCddLastY, IsForecast
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY RunDate, Dates, RegionName ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate    = src.RunDate
       AND tgt.Dates      = src.Dates
       AND tgt.RegionName = src.RegionName
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        PopHdd        = src.PopHdd,
        PopHdd30y     = src.PopHdd30y,
        PopHdd10y     = src.PopHdd10y,
        PopHddLastY   = src.PopHddLastY,
        PopCdd        = src.PopCdd,
        PopCdd30y     = src.PopCdd30y,
        PopCdd10y     = src.PopCdd10y,
        PopCddLastY   = src.PopCddLastY,
        IsForecast    = src.IsForecast,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RunDate, Dates, RegionName,
                PopHdd, PopHdd30y, PopHdd10y, PopHddLastY,
                PopCdd, PopCdd30y, PopCdd10y, PopCddLastY, IsForecast)
        VALUES (src.FileLogId, src.RunDate, src.Dates, src.RegionName,
                src.PopHdd, src.PopHdd30y, src.PopHdd10y, src.PopHddLastY,
                src.PopCdd, src.PopCdd30y, src.PopCdd10y, src.PopCddLastY, src.IsForecast);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 9. arm.usp_BulkMergeWindForecast — key (Region, InitDate, ForecastDate, HourOfDay).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindForecast
    @Records arm.WindForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindForecast AS tgt
    USING (
        SELECT FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, InitDate, ForecastDate, HourOfDay
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region       = src.Region
       AND tgt.InitDate     = src.InitDate
       AND tgt.ForecastDate = src.ForecastDate
       AND tgt.HourOfDay    = src.HourOfDay
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        HourLabel     = src.HourLabel,
        ValueMw       = src.ValueMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw)
        VALUES (src.FileLogId, src.Region, src.InitDate, src.ForecastDate, src.HourLabel, src.HourOfDay, src.ValueMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 10. arm.usp_BulkMergeWindForecastSubRegion —
--     key (Region, SubRegion, InitDate, ForecastDate, HourOfDay).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindForecastSubRegion
    @Records arm.WindForecastSubRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindForecastSubRegion AS tgt
    USING (
        SELECT FileLogId, Region, SubRegion, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, SubRegion, InitDate, ForecastDate, HourOfDay
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region       = src.Region
       AND tgt.SubRegion    = src.SubRegion
       AND tgt.InitDate     = src.InitDate
       AND tgt.ForecastDate = src.ForecastDate
       AND tgt.HourOfDay    = src.HourOfDay
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        HourLabel     = src.HourLabel,
        ValueMw       = src.ValueMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, SubRegion, InitDate, ForecastDate, HourLabel, HourOfDay, ValueMw)
        VALUES (src.FileLogId, src.Region, src.SubRegion, src.InitDate, src.ForecastDate,
                src.HourLabel, src.HourOfDay, src.ValueMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 11. arm.usp_BulkMergeWindHourly — key (HourEndingUtc, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindHourly
    @Records arm.WindHourlyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindHourly AS tgt
    USING (
        SELECT FileLogId, HourEndingUtc, Region, ActualMw
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY HourEndingUtc, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.HourEndingUtc = src.HourEndingUtc
       AND tgt.Region        = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        ActualMw      = src.ActualMw,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, HourEndingUtc, Region, ActualMw)
        VALUES (src.FileLogId, src.HourEndingUtc, src.Region, src.ActualMw);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 12. arm.usp_BulkMergeWindTotalCapacityClimatology — key (ProductionDate, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindTotalCapacityClimatology
    @Records arm.WindTotalCapacityClimatologyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindTotalCapacityClimatology AS tgt
    USING (
        SELECT FileLogId, ProductionDate, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY ProductionDate, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ProductionDate = src.ProductionDate
       AND tgt.Region         = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId       = src.FileLogId,
        TotalCapacityMw = src.TotalCapacityMw,
        Avg_1_5         = src.Avg_1_5,
        Avg_6_10        = src.Avg_6_10,
        Avg_11_15       = src.Avg_11_15,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ProductionDate, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15)
        VALUES (src.FileLogId, src.ProductionDate, src.Region, src.TotalCapacityMw,
                src.Avg_1_5, src.Avg_6_10, src.Avg_11_15);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 13. arm.usp_BulkMergeWindTotalCapacityMW — key (ProductionDate, Block, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindTotalCapacityMW
    @Records arm.WindTotalCapacityMWTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindTotalCapacityMW AS tgt
    USING (
        SELECT FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY ProductionDate, Block, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ProductionDate = src.ProductionDate
       AND tgt.Block          = src.Block
       AND tgt.Region         = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId       = src.FileLogId,
        TotalCapacityMw = src.TotalCapacityMw,
        Avg_1_5         = src.Avg_1_5,
        Avg_6_10        = src.Avg_6_10,
        Avg_11_15       = src.Avg_11_15,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15)
        VALUES (src.FileLogId, src.ProductionDate, src.Block, src.Region, src.TotalCapacityMw,
                src.Avg_1_5, src.Avg_6_10, src.Avg_11_15);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 14. arm.usp_BulkMergeWindTotalCapacityPct — key (ProductionDate, Block, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindTotalCapacityPct
    @Records arm.WindTotalCapacityPctTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindTotalCapacityPct AS tgt
    USING (
        SELECT FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY ProductionDate, Block, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ProductionDate = src.ProductionDate
       AND tgt.Block          = src.Block
       AND tgt.Region         = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId       = src.FileLogId,
        TotalCapacityMw = src.TotalCapacityMw,
        Avg_1_5         = src.Avg_1_5,
        Avg_6_10        = src.Avg_6_10,
        Avg_11_15       = src.Avg_11_15,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ProductionDate, Block, Region, TotalCapacityMw, Avg_1_5, Avg_6_10, Avg_11_15)
        VALUES (src.FileLogId, src.ProductionDate, src.Block, src.Region, src.TotalCapacityMw,
                src.Avg_1_5, src.Avg_6_10, src.Avg_11_15);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 15. arm.usp_BulkMergeStation — key (Region, Identifier).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStation
    @Records arm.StationTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Station AS tgt
    USING (
        SELECT FileLogId, Region, Identifier, WmoId, Wban, Ghcnd, Lat, Lon, [Name], [State], Country
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Region, Identifier ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.Region     = src.Region
       AND tgt.Identifier = src.Identifier
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        WmoId         = src.WmoId,
        Wban          = src.Wban,
        Ghcnd         = src.Ghcnd,
        Lat           = src.Lat,
        Lon           = src.Lon,
        [Name]        = src.[Name],
        [State]       = src.[State],
        Country       = src.Country,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Region, Identifier, WmoId, Wban, Ghcnd, Lat, Lon, [Name], [State], Country)
        VALUES (src.FileLogId, src.Region, src.Identifier, src.WmoId, src.Wban, src.Ghcnd,
                src.Lat, src.Lon, src.[Name], src.[State], src.Country);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO
