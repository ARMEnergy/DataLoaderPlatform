-- =============================================================================
-- 003_CreateCwgProcedures.sql
-- Stored procedures for the CWG loader (schema [arm]):
--   * arm.usp_UpsertFileLog                 — per-request hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMerge<Table>  (×19)       — TVP bulk upsert into each fact table.
--
-- LOAD ORDER the procs imply (per request/file):
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId (called for EVERY outcome:
--      Success / NotAvailable / Failed). The facts no longer store that id — the
--      hub is the run/status log only — but it is still written for every request.
--   2) On rows > 0, call the matching arm.usp_BulkMerge<Table> with the TVP.
--
-- -----------------------------------------------------------------------------
-- RecordHash — the one new thing in this file (2026-10-05 reshape)
-- -----------------------------------------------------------------------------
-- Every fact carries RecordHash CHAR(32): an uppercase-hex MD5 over that row's
-- NON-KEY value columns. It is computed HERE and only here, so no sink can drift
-- from the definition and no TVP has to carry it.
--
--   * Field separator  = CHAR(31) (ASCII unit separator)
--   * NULL placeholder = CHAR(30) (ASCII record separator)
-- Both are control characters that cannot occur in these CSV sources, so a NULL
-- is never confusable with an empty string and no value can impersonate a field
-- boundary. CONCAT silently turns NULL into '', which is exactly why every term
-- is wrapped in ISNULL(...) first.
--
-- Canonical text per type: DECIMAL via CONVERT(VARCHAR(40), col) — the column's
-- fixed scale makes that stable; BIT via VARCHAR(1); DATE via style 23
-- (yyyy-mm-dd); FLOAT via style 3 (17 significant digits — a lossless
-- round-trip, so an unchanged coordinate never re-hashes).
--
-- The MERGE then updates ONLY when the hash actually differs:
--     WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash)
-- so ModifiedAtUtc moves on real change and IX_ModifiedAtUtc is a usable
-- incremental-pull index. A consequence worth knowing: re-merging an unchanged
-- file reports RecordsProcessed = 0. That is correct — nothing changed — and the
-- row count the loader logs for the READ side is unaffected.
--
-- Each bulk-merge proc also:
--   * MERGEs on the table's PRIMARY KEY (never on a surrogate).
--   * De-dups the batch on that key (ROW_NUMBER over the TVP) before MERGE, so an
--     in-file duplicate key cannot trigger "MERGE ... same row more than once".
--     An arbitrary row per key is kept — each file is an immutable snapshot, and
--     the sinks already de-dup in C# before building the TVP.
--   * Returns SELECT @@ROWCOUNT AS RecordsProcessed so SqlSinkBase
--     (ProcedureReturnsRowCount = true) can read the count.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable.
-- Run 001 and 002 first. This script assumes CWG is the current database.
-- =============================================================================

USE CWG;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog — upsert one FileLog (hub) row per request and RETURN its
-- FileLogId. Called once per request for ALL outcomes. UNCHANGED by the reshape.
-- Region / Variant / RepresentativeDate are NULL-able; the MERGE matches them
-- with explicit NULL-equality so undated / no-region requests reuse a single
-- stable hub row. Endpoint / Status are resolved to their surrogate
-- arm.Endpoint / arm.Status .Id (a miss RAISERRORs — fixed catalogs seeded in
-- 001); Region is get-or-created in arm.Region (non-NULL only).
--
-- RETURN CONTRACT: exactly one result set, one row, one column "FileLogId" (the
-- C# reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id,
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
-- 1. arm.usp_BulkMergeCityForecast — key (ProductionDate, Date, Station).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCityForecast
    @Records arm.CityForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.CityForecast AS tgt
    USING (
        SELECT ProductionDate, [Date], Station,
               ForecastMin, ForecastMax, ForecastAvg, NormMin, NormMax, HDD, CDD,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), ForecastMin), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ForecastMax), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ForecastAvg), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NormMin),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NormMax),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), HDD),         CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), CDD),         CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ProductionDate, [Date], Station
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ProductionDate = src.ProductionDate
       AND tgt.[Date]         = src.[Date]
       AND tgt.Station        = src.Station
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ForecastMin   = src.ForecastMin,
        ForecastMax   = src.ForecastMax,
        ForecastAvg   = src.ForecastAvg,
        NormMin       = src.NormMin,
        NormMax       = src.NormMax,
        HDD           = src.HDD,
        CDD           = src.CDD,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProductionDate, [Date], Station,
                ForecastMin, ForecastMax, ForecastAvg, NormMin, NormMax, HDD, CDD,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ProductionDate, src.[Date], src.Station,
                src.ForecastMin, src.ForecastMax, src.ForecastAvg, src.NormMin, src.NormMax, src.HDD, src.CDD,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 2. arm.usp_BulkMergeCityGasForecast — key (ProductionDate, Date, Station).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCityGasForecast
    @Records arm.CityGasForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.CityGasForecast AS tgt
    USING (
        SELECT ProductionDate, [Date], Station,
               ForecastMin, ForecastMax, ForecastAvg, NormMin, NormMax, HDD, CDD,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), ForecastMin), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ForecastMax), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ForecastAvg), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NormMin),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NormMax),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), HDD),         CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), CDD),         CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ProductionDate, [Date], Station
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ProductionDate = src.ProductionDate
       AND tgt.[Date]         = src.[Date]
       AND tgt.Station        = src.Station
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ForecastMin   = src.ForecastMin,
        ForecastMax   = src.ForecastMax,
        ForecastAvg   = src.ForecastAvg,
        NormMin       = src.NormMin,
        NormMax       = src.NormMax,
        HDD           = src.HDD,
        CDD           = src.CDD,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProductionDate, [Date], Station,
                ForecastMin, ForecastMax, ForecastAvg, NormMin, NormMax, HDD, CDD,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ProductionDate, src.[Date], src.Station,
                src.ForecastMin, src.ForecastMax, src.ForecastAvg, src.NormMin, src.NormMax, src.HDD, src.CDD,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 3. arm.usp_BulkMergeCityObservation — key (Date, Station).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCityObservation
    @Records arm.CityObservationTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.CityObservation AS tgt
    USING (
        SELECT [Date], Station, MinTemp, MaxTemp, HDD, CDD,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), MinTemp), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), MaxTemp), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), HDD),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), CDD),     CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY [Date], Station ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.[Date]  = src.[Date]
       AND tgt.Station = src.Station
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        MinTemp       = src.MinTemp,
        MaxTemp       = src.MaxTemp,
        HDD           = src.HDD,
        CDD           = src.CDD,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([Date], Station, MinTemp, MaxTemp, HDD, CDD, RecordHash, ModifiedAtUtc)
        VALUES (src.[Date], src.Station, src.MinTemp, src.MaxTemp, src.HDD, src.CDD,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 4. arm.usp_BulkMergeDailyNormal — key (Month, Day). WIDE.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeDailyNormal
    @Records arm.DailyNormalTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.DailyNormal AS tgt
    USING (
        SELECT [Month], [Day], CAISO, SPP, ERCOT, MISO, PJM, NEPOOL, NYISO, BPA, IESO, AESO,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), CAISO),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), SPP),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ERCOT),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), MISO),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), PJM),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NEPOOL), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NYISO),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), BPA),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), IESO),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), AESO),   CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY [Month], [Day] ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.[Month] = src.[Month]
       AND tgt.[Day]   = src.[Day]
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        CAISO         = src.CAISO,
        SPP           = src.SPP,
        ERCOT         = src.ERCOT,
        MISO          = src.MISO,
        PJM           = src.PJM,
        NEPOOL        = src.NEPOOL,
        NYISO         = src.NYISO,
        BPA           = src.BPA,
        IESO          = src.IESO,
        AESO          = src.AESO,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([Month], [Day], CAISO, SPP, ERCOT, MISO, PJM, NEPOOL, NYISO, BPA, IESO, AESO,
                RecordHash, ModifiedAtUtc)
        VALUES (src.[Month], src.[Day], src.CAISO, src.SPP, src.ERCOT, src.MISO, src.PJM,
                src.NEPOOL, src.NYISO, src.BPA, src.IESO, src.AESO,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 5. arm.usp_BulkMergeSolarForecast — key (ForecastDate, UTCHourEnding, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSolarForecast
    @Records arm.SolarForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.SolarForecast AS tgt
    USING (
        SELECT ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, [Value],
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(10), ForecastLabel, 23), CHAR(30)), CHAR(31),
                   ISNULL(HourLabel,                               CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [Value]),           CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, UTCHourEnding, Region
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate  = src.ForecastDate
       AND tgt.UTCHourEnding = src.UTCHourEnding
       AND tgt.Region        = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ForecastLabel = src.ForecastLabel,
        HourLabel     = src.HourLabel,
        [Value]       = src.[Value],
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, [Value],
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.UTCHourEnding, src.Region, src.ForecastLabel, src.HourLabel, src.[Value],
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 6. arm.usp_BulkMergeSolarForecastChange — key (ForecastDate, UTCHourEnding, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSolarForecastChange
    @Records arm.SolarForecastChangeTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.SolarForecastChange AS tgt
    USING (
        SELECT ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, [Value],
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(10), ForecastLabel, 23), CHAR(30)), CHAR(31),
                   ISNULL(HourLabel,                               CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [Value]),           CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, UTCHourEnding, Region
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate  = src.ForecastDate
       AND tgt.UTCHourEnding = src.UTCHourEnding
       AND tgt.Region        = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ForecastLabel = src.ForecastLabel,
        HourLabel     = src.HourLabel,
        [Value]       = src.[Value],
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, [Value],
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.UTCHourEnding, src.Region, src.ForecastLabel, src.HourLabel, src.[Value],
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 7. arm.usp_BulkMergeSolarHourly — key (UTCHourEnding). WIDE.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSolarHourly
    @Records arm.SolarHourlyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.SolarHourly AS tgt
    USING (
        SELECT UTCHourEnding, ERCOT, CAISO,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), ERCOT), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), CAISO), CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY UTCHourEnding ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.UTCHourEnding = src.UTCHourEnding
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ERCOT         = src.ERCOT,
        CAISO         = src.CAISO,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (UTCHourEnding, ERCOT, CAISO, RecordHash, ModifiedAtUtc)
        VALUES (src.UTCHourEnding, src.ERCOT, src.CAISO, src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 8. arm.usp_BulkMergeStateDegreeDayObservation — key (ObservationDate, RegionName, State).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStateDegreeDayObservation
    @Records arm.StateDegreeDayObservationTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.StateDegreeDayObservation AS tgt
    USING (
        SELECT ObservationDate, RegionName, [State],
               POP_HDD, [30Y_POP_HDD], [10Y_POP_HDD], LAST_Y_POP_HDD,
               POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
               IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), POP_HDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_HDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_HDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_HDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_CDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_CDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(1),  IS_FORECAST),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), GAS_WEIGHT),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ELCT_WEIGHT),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_WEIGHT),     CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ObservationDate, RegionName, [State]
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ObservationDate = src.ObservationDate
       AND tgt.RegionName      = src.RegionName
       AND tgt.[State]         = src.[State]
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        POP_HDD        = src.POP_HDD,
        [30Y_POP_HDD]  = src.[30Y_POP_HDD],
        [10Y_POP_HDD]  = src.[10Y_POP_HDD],
        LAST_Y_POP_HDD = src.LAST_Y_POP_HDD,
        POP_CDD        = src.POP_CDD,
        [30Y_POP_CDD]  = src.[30Y_POP_CDD],
        [10Y_POP_CDD]  = src.[10Y_POP_CDD],
        LAST_Y_POP_CDD = src.LAST_Y_POP_CDD,
        IS_FORECAST    = src.IS_FORECAST,
        GAS_WEIGHT     = src.GAS_WEIGHT,
        ELCT_WEIGHT    = src.ELCT_WEIGHT,
        POP_WEIGHT     = src.POP_WEIGHT,
        RecordHash     = src.RecordHash,
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ObservationDate, RegionName, [State],
                POP_HDD, [30Y_POP_HDD], [10Y_POP_HDD], LAST_Y_POP_HDD,
                POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
                IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ObservationDate, src.RegionName, src.[State],
                src.POP_HDD, src.[30Y_POP_HDD], src.[10Y_POP_HDD], src.LAST_Y_POP_HDD,
                src.POP_CDD, src.[30Y_POP_CDD], src.[10Y_POP_CDD], src.LAST_Y_POP_CDD,
                src.IS_FORECAST, src.GAS_WEIGHT, src.ELCT_WEIGHT, src.POP_WEIGHT,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 9. arm.usp_BulkMergeStation — key (Identifier).
--    Point is DERIVED here from Latitude/Longitude (geography::Point, SRID 4326)
--    and is NULL when either is NULL or out of range — geography::Point raises a
--    hard error on an out-of-range latitude/longitude, so the CASE guards it
--    rather than letting one bad station row abort the whole batch.
--    MappingId is IDENTITY and is never written.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStation
    @Records arm.StationTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Station AS tgt
    USING (
        SELECT Identifier, WMOID, WBAN, GHCND, Latitude, Longitude, [Name], [State], Country,
               CASE WHEN Latitude IS NOT NULL AND Longitude IS NOT NULL
                     AND Latitude  BETWEEN -90  AND 90
                     AND Longitude BETWEEN -180 AND 180
                    THEN geography::Point(Latitude, Longitude, 4326)
                    ELSE NULL
               END AS Point,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(WMOID,                            CHAR(30)), CHAR(31),
                   ISNULL(WBAN,                             CHAR(30)), CHAR(31),
                   ISNULL(GHCND,                            CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), Latitude, 3),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), Longitude, 3), CHAR(30)), CHAR(31),
                   ISNULL([Name],                           CHAR(30)), CHAR(31),
                   ISNULL([State],                          CHAR(30)), CHAR(31),
                   ISNULL(Country,                          CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY Identifier ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.Identifier = src.Identifier
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        WMOID         = src.WMOID,
        WBAN          = src.WBAN,
        GHCND         = src.GHCND,
        Latitude      = src.Latitude,
        Longitude     = src.Longitude,
        Point         = src.Point,
        [Name]        = src.[Name],
        [State]       = src.[State],
        Country       = src.Country,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Identifier, WMOID, WBAN, GHCND, Latitude, Longitude, Point, [Name], [State], Country,
                RecordHash, ModifiedAtUtc)
        VALUES (src.Identifier, src.WMOID, src.WBAN, src.GHCND, src.Latitude, src.Longitude, src.Point,
                src.[Name], src.[State], src.Country,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 10. arm.usp_BulkMergeRegions5DegreeDays — key (ObservationDate, Date, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeRegions5DegreeDays
    @Records arm.Regions5DegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Regions5DegreeDays AS tgt
    USING (
        SELECT ObservationDate, [Date], Region,
               NG_HDD, [30Y_NG_HDD], [10Y_NG_HDD], LAST_Y_NG_HDD,
               POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
               ELEC_CDD, [30Y_ELEC_CDD], [10Y_ELEC_CDD], LAST_Y_ELEC_CDD,
               IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), NG_HDD),          CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_NG_HDD]),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_NG_HDD]),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_NG_HDD),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_CDD),         CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_CDD]),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_CDD]),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_CDD),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ELEC_CDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_ELEC_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_ELEC_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_ELEC_CDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(1),  IS_FORECAST),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), GAS_WEIGHT),      CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ELCT_WEIGHT),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_WEIGHT),      CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ObservationDate, [Date], Region
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ObservationDate = src.ObservationDate
       AND tgt.[Date]          = src.[Date]
       AND tgt.Region          = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        NG_HDD          = src.NG_HDD,
        [30Y_NG_HDD]    = src.[30Y_NG_HDD],
        [10Y_NG_HDD]    = src.[10Y_NG_HDD],
        LAST_Y_NG_HDD   = src.LAST_Y_NG_HDD,
        POP_CDD         = src.POP_CDD,
        [30Y_POP_CDD]   = src.[30Y_POP_CDD],
        [10Y_POP_CDD]   = src.[10Y_POP_CDD],
        LAST_Y_POP_CDD  = src.LAST_Y_POP_CDD,
        ELEC_CDD        = src.ELEC_CDD,
        [30Y_ELEC_CDD]  = src.[30Y_ELEC_CDD],
        [10Y_ELEC_CDD]  = src.[10Y_ELEC_CDD],
        LAST_Y_ELEC_CDD = src.LAST_Y_ELEC_CDD,
        IS_FORECAST     = src.IS_FORECAST,
        GAS_WEIGHT      = src.GAS_WEIGHT,
        ELCT_WEIGHT     = src.ELCT_WEIGHT,
        POP_WEIGHT      = src.POP_WEIGHT,
        RecordHash      = src.RecordHash,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ObservationDate, [Date], Region,
                NG_HDD, [30Y_NG_HDD], [10Y_NG_HDD], LAST_Y_NG_HDD,
                POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
                ELEC_CDD, [30Y_ELEC_CDD], [10Y_ELEC_CDD], LAST_Y_ELEC_CDD,
                IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ObservationDate, src.[Date], src.Region,
                src.NG_HDD, src.[30Y_NG_HDD], src.[10Y_NG_HDD], src.LAST_Y_NG_HDD,
                src.POP_CDD, src.[30Y_POP_CDD], src.[10Y_POP_CDD], src.LAST_Y_POP_CDD,
                src.ELEC_CDD, src.[30Y_ELEC_CDD], src.[10Y_ELEC_CDD], src.LAST_Y_ELEC_CDD,
                src.IS_FORECAST, src.GAS_WEIGHT, src.ELCT_WEIGHT, src.POP_WEIGHT,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 11. arm.usp_BulkMergeRegions9DegreeDays — key (ObservationDate, Date, Region).
--     Identical body to #10 against the 9-region table.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeRegions9DegreeDays
    @Records arm.Regions9DegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Regions9DegreeDays AS tgt
    USING (
        SELECT ObservationDate, [Date], Region,
               NG_HDD, [30Y_NG_HDD], [10Y_NG_HDD], LAST_Y_NG_HDD,
               POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
               ELEC_CDD, [30Y_ELEC_CDD], [10Y_ELEC_CDD], LAST_Y_ELEC_CDD,
               IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), NG_HDD),          CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_NG_HDD]),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_NG_HDD]),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_NG_HDD),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_CDD),         CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_CDD]),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_CDD]),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_CDD),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ELEC_CDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_ELEC_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_ELEC_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_ELEC_CDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(1),  IS_FORECAST),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), GAS_WEIGHT),      CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ELCT_WEIGHT),     CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_WEIGHT),      CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ObservationDate, [Date], Region
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ObservationDate = src.ObservationDate
       AND tgt.[Date]          = src.[Date]
       AND tgt.Region          = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        NG_HDD          = src.NG_HDD,
        [30Y_NG_HDD]    = src.[30Y_NG_HDD],
        [10Y_NG_HDD]    = src.[10Y_NG_HDD],
        LAST_Y_NG_HDD   = src.LAST_Y_NG_HDD,
        POP_CDD         = src.POP_CDD,
        [30Y_POP_CDD]   = src.[30Y_POP_CDD],
        [10Y_POP_CDD]   = src.[10Y_POP_CDD],
        LAST_Y_POP_CDD  = src.LAST_Y_POP_CDD,
        ELEC_CDD        = src.ELEC_CDD,
        [30Y_ELEC_CDD]  = src.[30Y_ELEC_CDD],
        [10Y_ELEC_CDD]  = src.[10Y_ELEC_CDD],
        LAST_Y_ELEC_CDD = src.LAST_Y_ELEC_CDD,
        IS_FORECAST     = src.IS_FORECAST,
        GAS_WEIGHT      = src.GAS_WEIGHT,
        ELCT_WEIGHT     = src.ELCT_WEIGHT,
        POP_WEIGHT      = src.POP_WEIGHT,
        RecordHash      = src.RecordHash,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ObservationDate, [Date], Region,
                NG_HDD, [30Y_NG_HDD], [10Y_NG_HDD], LAST_Y_NG_HDD,
                POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
                ELEC_CDD, [30Y_ELEC_CDD], [10Y_ELEC_CDD], LAST_Y_ELEC_CDD,
                IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ObservationDate, src.[Date], src.Region,
                src.NG_HDD, src.[30Y_NG_HDD], src.[10Y_NG_HDD], src.LAST_Y_NG_HDD,
                src.POP_CDD, src.[30Y_POP_CDD], src.[10Y_POP_CDD], src.LAST_Y_POP_CDD,
                src.ELEC_CDD, src.[30Y_ELEC_CDD], src.[10Y_ELEC_CDD], src.LAST_Y_ELEC_CDD,
                src.IS_FORECAST, src.GAS_WEIGHT, src.ELCT_WEIGHT, src.POP_WEIGHT,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 12. arm.usp_BulkMergeISODegreeDays — key (ObservationDate, Date, Region).
--     POP_HDD family (not NG_HDD); no ELEC family, no weights.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeISODegreeDays
    @Records arm.ISODegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.ISODegreeDays AS tgt
    USING (
        SELECT ObservationDate, [Date], Region,
               POP_HDD, [30Y_POP_HDD], [10Y_POP_HDD], LAST_Y_POP_HDD,
               POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
               IS_FORECAST,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), POP_HDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_HDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_HDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_HDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_CDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_CDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(1),  IS_FORECAST),    CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ObservationDate, [Date], Region
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ObservationDate = src.ObservationDate
       AND tgt.[Date]          = src.[Date]
       AND tgt.Region          = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        POP_HDD        = src.POP_HDD,
        [30Y_POP_HDD]  = src.[30Y_POP_HDD],
        [10Y_POP_HDD]  = src.[10Y_POP_HDD],
        LAST_Y_POP_HDD = src.LAST_Y_POP_HDD,
        POP_CDD        = src.POP_CDD,
        [30Y_POP_CDD]  = src.[30Y_POP_CDD],
        [10Y_POP_CDD]  = src.[10Y_POP_CDD],
        LAST_Y_POP_CDD = src.LAST_Y_POP_CDD,
        IS_FORECAST    = src.IS_FORECAST,
        RecordHash     = src.RecordHash,
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ObservationDate, [Date], Region,
                POP_HDD, [30Y_POP_HDD], [10Y_POP_HDD], LAST_Y_POP_HDD,
                POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
                IS_FORECAST, RecordHash, ModifiedAtUtc)
        VALUES (src.ObservationDate, src.[Date], src.Region,
                src.POP_HDD, src.[30Y_POP_HDD], src.[10Y_POP_HDD], src.LAST_Y_POP_HDD,
                src.POP_CDD, src.[30Y_POP_CDD], src.[10Y_POP_CDD], src.LAST_Y_POP_CDD,
                src.IS_FORECAST, src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 13. arm.usp_BulkMergeNationalDegreeDays — key (ObservationDate, Date).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeNationalDegreeDays
    @Records arm.NationalDegreeDaysTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.NationalDegreeDays AS tgt
    USING (
        SELECT ObservationDate, [Date],
               NG_HDD, [30Y_NG_HDD], [10Y_NG_HDD], LAST_Y_NG_HDD,
               POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
               ELEC_CDD, [30Y_ELEC_CDD], [10Y_ELEC_CDD], LAST_Y_ELEC_CDD,
               IS_FORECAST,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), NG_HDD),          CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_NG_HDD]),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_NG_HDD]),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_NG_HDD),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), POP_CDD),         CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_POP_CDD]),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_POP_CDD]),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_POP_CDD),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ELEC_CDD),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [30Y_ELEC_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [10Y_ELEC_CDD]),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), LAST_Y_ELEC_CDD), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(1),  IS_FORECAST),     CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ObservationDate, [Date]
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ObservationDate = src.ObservationDate
       AND tgt.[Date]          = src.[Date]
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        NG_HDD          = src.NG_HDD,
        [30Y_NG_HDD]    = src.[30Y_NG_HDD],
        [10Y_NG_HDD]    = src.[10Y_NG_HDD],
        LAST_Y_NG_HDD   = src.LAST_Y_NG_HDD,
        POP_CDD         = src.POP_CDD,
        [30Y_POP_CDD]   = src.[30Y_POP_CDD],
        [10Y_POP_CDD]   = src.[10Y_POP_CDD],
        LAST_Y_POP_CDD  = src.LAST_Y_POP_CDD,
        ELEC_CDD        = src.ELEC_CDD,
        [30Y_ELEC_CDD]  = src.[30Y_ELEC_CDD],
        [10Y_ELEC_CDD]  = src.[10Y_ELEC_CDD],
        LAST_Y_ELEC_CDD = src.LAST_Y_ELEC_CDD,
        IS_FORECAST     = src.IS_FORECAST,
        RecordHash      = src.RecordHash,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ObservationDate, [Date],
                NG_HDD, [30Y_NG_HDD], [10Y_NG_HDD], LAST_Y_NG_HDD,
                POP_CDD, [30Y_POP_CDD], [10Y_POP_CDD], LAST_Y_POP_CDD,
                ELEC_CDD, [30Y_ELEC_CDD], [10Y_ELEC_CDD], LAST_Y_ELEC_CDD,
                IS_FORECAST, RecordHash, ModifiedAtUtc)
        VALUES (src.ObservationDate, src.[Date],
                src.NG_HDD, src.[30Y_NG_HDD], src.[10Y_NG_HDD], src.LAST_Y_NG_HDD,
                src.POP_CDD, src.[30Y_POP_CDD], src.[10Y_POP_CDD], src.LAST_Y_POP_CDD,
                src.ELEC_CDD, src.[30Y_ELEC_CDD], src.[10Y_ELEC_CDD], src.LAST_Y_ELEC_CDD,
                src.IS_FORECAST, src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 14. arm.usp_BulkMergeWindForecast — key (ForecastDate, UTCHourEnding, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindForecast
    @Records arm.WindForecastTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindForecast AS tgt
    USING (
        SELECT ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, [Value],
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(10), ForecastLabel, 23), CHAR(30)), CHAR(31),
                   ISNULL(HourLabel,                               CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [Value]),           CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, UTCHourEnding, Region
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate  = src.ForecastDate
       AND tgt.UTCHourEnding = src.UTCHourEnding
       AND tgt.Region        = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ForecastLabel = src.ForecastLabel,
        HourLabel     = src.HourLabel,
        [Value]       = src.[Value],
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, UTCHourEnding, Region, ForecastLabel, HourLabel, [Value],
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.UTCHourEnding, src.Region, src.ForecastLabel, src.HourLabel, src.[Value],
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 15. arm.usp_BulkMergeWindForecastSubRegion
--     — key (ForecastDate, UTCHourEnding, Region, SubRegion).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindForecastSubRegion
    @Records arm.WindForecastSubRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindForecastSubRegion AS tgt
    USING (
        SELECT ForecastDate, UTCHourEnding, Region, SubRegion, ForecastLabel, HourLabel, [Value],
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(10), ForecastLabel, 23), CHAR(30)), CHAR(31),
                   ISNULL(HourLabel,                               CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), [Value]),           CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, UTCHourEnding, Region, SubRegion
                                         ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate  = src.ForecastDate
       AND tgt.UTCHourEnding = src.UTCHourEnding
       AND tgt.Region        = src.Region
       AND tgt.SubRegion     = src.SubRegion
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        ForecastLabel = src.ForecastLabel,
        HourLabel     = src.HourLabel,
        [Value]       = src.[Value],
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, UTCHourEnding, Region, SubRegion, ForecastLabel, HourLabel, [Value],
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.UTCHourEnding, src.Region, src.SubRegion,
                src.ForecastLabel, src.HourLabel, src.[Value],
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 16. arm.usp_BulkMergeWindHourly — key (UTCHourEnding). WIDE.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindHourly
    @Records arm.WindHourlyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindHourly AS tgt
    USING (
        SELECT UTCHourEnding, CAISO, SPP, ERCOT, MISO, PJM, NEPOOL, NYISO, BPA, IESO, AESO,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), CAISO),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), SPP),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ERCOT),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), MISO),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), PJM),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NEPOOL), CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), NYISO),  CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), BPA),    CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), IESO),   CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), AESO),   CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY UTCHourEnding ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.UTCHourEnding = src.UTCHourEnding
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        CAISO         = src.CAISO,
        SPP           = src.SPP,
        ERCOT         = src.ERCOT,
        MISO          = src.MISO,
        PJM           = src.PJM,
        NEPOOL        = src.NEPOOL,
        NYISO         = src.NYISO,
        BPA           = src.BPA,
        IESO          = src.IESO,
        AESO          = src.AESO,
        RecordHash    = src.RecordHash,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (UTCHourEnding, CAISO, SPP, ERCOT, MISO, PJM, NEPOOL, NYISO, BPA, IESO, AESO,
                RecordHash, ModifiedAtUtc)
        VALUES (src.UTCHourEnding, src.CAISO, src.SPP, src.ERCOT, src.MISO, src.PJM,
                src.NEPOOL, src.NYISO, src.BPA, src.IESO, src.AESO,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 17. arm.usp_BulkMergeWindTotalCapacityClimatology — key (ForecastDate, Region).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindTotalCapacityClimatology
    @Records arm.WindTotalCapacityClimatologyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindTotalCapacityClimatology AS tgt
    USING (
        SELECT ForecastDate, Region,
               TotalCapacity, OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), TotalCapacity),             CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), OneToFiveDayAverage),       CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), SixToTenDayAverage),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ElevenToFifteenDayAverage), CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate = src.ForecastDate
       AND tgt.Region       = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        TotalCapacity             = src.TotalCapacity,
        OneToFiveDayAverage       = src.OneToFiveDayAverage,
        SixToTenDayAverage        = src.SixToTenDayAverage,
        ElevenToFifteenDayAverage = src.ElevenToFifteenDayAverage,
        RecordHash                = src.RecordHash,
        ModifiedAtUtc             = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, Region,
                TotalCapacity, OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.Region,
                src.TotalCapacity, src.OneToFiveDayAverage, src.SixToTenDayAverage, src.ElevenToFifteenDayAverage,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 18. arm.usp_BulkMergeWindTotalCapacityMW — key (ForecastDate, Region).
--     Receives CURRENT-block rows only (the sink drops the other two blocks).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindTotalCapacityMW
    @Records arm.WindTotalCapacityMWTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindTotalCapacityMW AS tgt
    USING (
        SELECT ForecastDate, Region,
               TotalCapacity, OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), TotalCapacity),             CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), OneToFiveDayAverage),       CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), SixToTenDayAverage),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ElevenToFifteenDayAverage), CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate = src.ForecastDate
       AND tgt.Region       = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        TotalCapacity             = src.TotalCapacity,
        OneToFiveDayAverage       = src.OneToFiveDayAverage,
        SixToTenDayAverage        = src.SixToTenDayAverage,
        ElevenToFifteenDayAverage = src.ElevenToFifteenDayAverage,
        RecordHash                = src.RecordHash,
        ModifiedAtUtc             = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, Region,
                TotalCapacity, OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.Region,
                src.TotalCapacity, src.OneToFiveDayAverage, src.SixToTenDayAverage, src.ElevenToFifteenDayAverage,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- 19. arm.usp_BulkMergeWindTotalCapacityPct — key (ForecastDate, Region).
--     Receives CURRENT-block rows only (the sink drops the other two blocks).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeWindTotalCapacityPct
    @Records arm.WindTotalCapacityPctTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.WindTotalCapacityPct AS tgt
    USING (
        SELECT ForecastDate, Region,
               TotalCapacity, OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage,
               CONVERT(CHAR(32), HASHBYTES('MD5', CONCAT(
                   ISNULL(CONVERT(VARCHAR(40), TotalCapacity),             CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), OneToFiveDayAverage),       CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), SixToTenDayAverage),        CHAR(30)), CHAR(31),
                   ISNULL(CONVERT(VARCHAR(40), ElevenToFifteenDayAverage), CHAR(30)))), 2) AS RecordHash
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, Region ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate = src.ForecastDate
       AND tgt.Region       = src.Region
    WHEN MATCHED AND (tgt.RecordHash IS NULL OR tgt.RecordHash <> src.RecordHash) THEN UPDATE SET
        TotalCapacity             = src.TotalCapacity,
        OneToFiveDayAverage       = src.OneToFiveDayAverage,
        SixToTenDayAverage        = src.SixToTenDayAverage,
        ElevenToFifteenDayAverage = src.ElevenToFifteenDayAverage,
        RecordHash                = src.RecordHash,
        ModifiedAtUtc             = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ForecastDate, Region,
                TotalCapacity, OneToFiveDayAverage, SixToTenDayAverage, ElevenToFifteenDayAverage,
                RecordHash, ModifiedAtUtc)
        VALUES (src.ForecastDate, src.Region,
                src.TotalCapacity, src.OneToFiveDayAverage, src.SixToTenDayAverage, src.ElevenToFifteenDayAverage,
                src.RecordHash, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO
