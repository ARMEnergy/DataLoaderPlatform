-- =============================================================================
-- 003_CreateStormVistaProcedures.sql
-- Stored procedures for the StormVista loader (schema [dbo]):
--   * dbo.usp_GetReference       — 6-result-set reference read (enumeration).
--   * dbo.usp_UpsertFileLog      — per-request hub upsert; RETURNS the FileLogId.
--   * dbo.usp_BulkMergeDailyWdd  — TVP bulk upsert into dbo.DailyWdd.
--   * dbo.usp_BulkMergeRegionalWdd — TVP bulk upsert into dbo.RegionalWdd.
--   * dbo.usp_ValidateLoad       — post-load observational report.
--
-- LOAD ORDER the procs imply (per request/file):
--   1) dbo.usp_UpsertFileLog(...)  -> returns FileLogId  (called for EVERY
--      outcome: Success / NotAvailable(404) / Failed).
--   2) On Success, stamp that FileLogId onto every TVP row and call the matching
--      bulk-merge proc (dbo.usp_BulkMergeDailyWdd / dbo.usp_BulkMergeRegionalWdd).
--
-- The two bulk-merge procs return  SELECT @@ROWCOUNT AS RecordsProcessed  so the
-- platform's SqlSinkBase (ProcedureReturnsRowCount = true) can read the count.
-- Each de-dups its batch on the merge key before MERGE so a duplicate natural key
-- inside one file cannot trigger the "MERGE attempted to UPDATE/DELETE the same
-- row more than once" error. Each file is an immutable snapshot, so an arbitrary
-- row per key is kept; well-formed files have no intra-file duplicate keys.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable and validate inputs.
-- Run 001 and 002 first. This script assumes StormVista is the current database.
-- =============================================================================

USE StormVista;
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_GetReference — the single read proc the loader's reference provider
-- calls ONCE per run to load all six dimension tables. Returns SIX result sets
-- in a FIXED order (the C# reader consumes them positionally, then advances with
-- NextResult), each ORDER BY'd so the load is deterministic:
--   1) Models         (ModelSlug, DisplayName, SupportsDaily, SupportsRegional, IsExperimental)
--   2) Cycles         (CycleCode, SupportsDaily, SupportsRegional)
--   3) WddTypes       (TypeSlug, Weighting, Metric)
--   4) RegionSets     (RegionSetCode, Kind, Description)
--   5) RegionSetWddType bridge (RegionSetCode, TypeSlug)
--   6) Regions        (RegionSetCode, RegionName, Ordinal)
-- No parameters, no side effects; read-only. Column names/types match the tables
-- created in 001, so the C# maps positionally without casts.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_GetReference
AS
BEGIN
    SET NOCOUNT ON;

    -- 1) Models
    SELECT ModelSlug, DisplayName, SupportsDaily, SupportsRegional, IsExperimental
    FROM dbo.Model
    ORDER BY ModelSlug;

    -- 2) Cycles
    SELECT CycleCode, SupportsDaily, SupportsRegional
    FROM dbo.Cycle
    ORDER BY CycleCode;

    -- 3) WDD types
    SELECT TypeSlug, Weighting, Metric
    FROM dbo.WddType
    ORDER BY TypeSlug;

    -- 4) Region sets
    SELECT RegionSetCode, Kind, [Description]
    FROM dbo.RegionSet
    ORDER BY RegionSetCode;

    -- 5) Region-set <-> WDD-type applicability bridge
    SELECT RegionSetCode, TypeSlug
    FROM dbo.RegionSetWddType
    ORDER BY RegionSetCode, TypeSlug;

    -- 6) Regions (ordered within each set)
    SELECT RegionSetCode, RegionName, Ordinal
    FROM dbo.Region
    ORDER BY RegionSetCode, Ordinal;
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_UpsertFileLog — upsert one FileLog (hub) row per request and RETURN
-- its FileLogId. Called once per request for ALL outcomes.
--
-- Parameters are natural-key strings; the proc resolves each *Id by joining the
-- lookups and RAISERRORs if any required one fails to resolve (a reference-table
-- gap — the vendor added a model/type/etc. and the seeds need updating).
--
-- @RegionSetCode is NULL for daily requests -> RegionSetId stays NULL. The MERGE
-- matches the natural key with an ISNULL(-1) sentinel so daily rows collapse to
-- one row per (Endpoint, Model, Cycle, WddType, InitDate).
--
-- RETURN CONTRACT: emits exactly one result set, one row, one column named
-- "FileLogId". The C# reads it with ExecuteScalar. Captured via MERGE ... OUTPUT
-- inserted.Id, which yields the Id for BOTH the inserted and the updated branch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_UpsertFileLog
    @EndpointName  VARCHAR(20),
    @ModelSlug     VARCHAR(40),
    @CycleCode     CHAR(2),
    @WddTypeSlug   VARCHAR(10),
    @RegionSetCode VARCHAR(4)    = NULL,
    @InitDate      DATE,
    @StatusLabel   VARCHAR(20),
    @RequestPath   NVARCHAR(400),
    @HttpStatus    INT           = NULL,
    @RowCount      INT           = 0
AS
BEGIN
    SET NOCOUNT ON;

    -- ---- Validate required inputs -------------------------------------------
    IF @EndpointName IS NULL OR @ModelSlug IS NULL OR @CycleCode IS NULL
       OR @WddTypeSlug IS NULL OR @InitDate IS NULL OR @StatusLabel IS NULL
       OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @EndpointName, @ModelSlug, @CycleCode, @WddTypeSlug, @InitDate, @StatusLabel and @RequestPath are all required.', 16, 1);
        RETURN;
    END

    -- ---- Resolve the dimension IDs ------------------------------------------
    DECLARE @EndpointId  INT = (SELECT Id FROM dbo.Endpoint  WHERE [Name]         = @EndpointName),
            @ModelId     INT = (SELECT Id FROM dbo.Model     WHERE ModelSlug      = @ModelSlug),
            @CycleId     INT = (SELECT Id FROM dbo.Cycle     WHERE CycleCode      = @CycleCode),
            @WddTypeId   INT = (SELECT Id FROM dbo.WddType   WHERE TypeSlug       = @WddTypeSlug),
            @StatusId    INT = (SELECT Id FROM dbo.[Status]  WHERE Label          = @StatusLabel),
            @RegionSetId INT = CASE WHEN @RegionSetCode IS NULL THEN NULL
                                    ELSE (SELECT Id FROM dbo.RegionSet WHERE RegionSetCode = @RegionSetCode) END;

    IF @EndpointId IS NULL
    BEGIN RAISERROR('usp_UpsertFileLog: @EndpointName "%s" did not resolve in dbo.Endpoint.', 16, 1, @EndpointName); RETURN; END
    IF @ModelId IS NULL
    BEGIN RAISERROR('usp_UpsertFileLog: @ModelSlug "%s" did not resolve in dbo.Model.', 16, 1, @ModelSlug); RETURN; END
    IF @CycleId IS NULL
    BEGIN RAISERROR('usp_UpsertFileLog: @CycleCode "%s" did not resolve in dbo.Cycle.', 16, 1, @CycleCode); RETURN; END
    IF @WddTypeId IS NULL
    BEGIN RAISERROR('usp_UpsertFileLog: @WddTypeSlug "%s" did not resolve in dbo.WddType.', 16, 1, @WddTypeSlug); RETURN; END
    IF @StatusId IS NULL
    BEGIN RAISERROR('usp_UpsertFileLog: @StatusLabel "%s" did not resolve in dbo.Status.', 16, 1, @StatusLabel); RETURN; END
    IF @RegionSetCode IS NOT NULL AND @RegionSetId IS NULL
    BEGIN RAISERROR('usp_UpsertFileLog: @RegionSetCode "%s" did not resolve in dbo.RegionSet.', 16, 1, @RegionSetCode); RETURN; END

    -- ---- Upsert the hub row, capturing the resulting Id ---------------------
    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE dbo.FileLog AS tgt
    USING (SELECT @EndpointId  AS EndpointId,
                  @ModelId     AS ModelId,
                  @CycleId     AS CycleId,
                  @WddTypeId   AS WddTypeId,
                  @RegionSetId AS RegionSetId,
                  @InitDate    AS InitDate) AS src
       ON  tgt.EndpointId = src.EndpointId
       AND tgt.ModelId    = src.ModelId
       AND tgt.CycleId    = src.CycleId
       AND tgt.WddTypeId  = src.WddTypeId
       AND ISNULL(tgt.RegionSetId, -1) = ISNULL(src.RegionSetId, -1)   -- NULL-safe (daily)
       AND tgt.InitDate   = src.InitDate
    WHEN MATCHED THEN UPDATE SET
        StatusId       = @StatusId,
        HttpStatus     = @HttpStatus,
        RequestPath    = @RequestPath,
        [RowCount]     = @RowCount,
        LastCheckedUtc = SYSUTCDATETIME(),
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointId, ModelId, CycleId, WddTypeId, RegionSetId, InitDate,
                StatusId, HttpStatus, [RowCount], RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.ModelId, src.CycleId, src.WddTypeId, src.RegionSetId, src.InitDate,
                @StatusId, @HttpStatus, @RowCount, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_BulkMergeDailyWdd — TVP bulk upsert into dbo.DailyWdd.
-- Merge key: (FileLogId, ValidDate). Resolves FlagId from FlagCode via dbo.Flag.
-- De-dups the batch on the merge key, then MERGEs. Returns RecordsProcessed.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeDailyWdd
    @Records dbo.DailyWddTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    -- De-dup on the merge key (keep one arbitrary row per key).
    DECLARE @Src TABLE
    (
        FileLogId INT          NOT NULL,
        ValidDate DATE         NOT NULL,
        FlagCode  INT          NOT NULL,
        [Value]   DECIMAL(9,4) NULL
    );
    INSERT INTO @Src (FileLogId, ValidDate, FlagCode, [Value])
    SELECT FileLogId, ValidDate, FlagCode, [Value]
    FROM (
        SELECT FileLogId, ValidDate, FlagCode, [Value],
               ROW_NUMBER() OVER (PARTITION BY FileLogId, ValidDate ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    ) AS d
    WHERE d.rn = 1;

    -- Strict: every FlagCode must resolve (domain is seeded {0,1,2}).
    IF EXISTS (SELECT 1 FROM @Src s WHERE NOT EXISTS (SELECT 1 FROM dbo.Flag f WHERE f.FlagCode = s.FlagCode))
    BEGIN
        RAISERROR('usp_BulkMergeDailyWdd: one or more FlagCode values did not resolve in dbo.Flag.', 16, 1);
        RETURN;
    END

    MERGE dbo.DailyWdd AS tgt
    USING (
        SELECT s.FileLogId, s.ValidDate, f.Id AS FlagId, s.[Value]
        FROM @Src AS s
        JOIN dbo.Flag AS f ON f.FlagCode = s.FlagCode
    ) AS src
       ON tgt.FileLogId = src.FileLogId
      AND tgt.ValidDate = src.ValidDate
    WHEN MATCHED THEN UPDATE SET
        [Value]       = src.[Value],
        FlagId        = src.FlagId,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ValidDate, FlagId, [Value])
        VALUES (src.FileLogId, src.ValidDate, src.FlagId, src.[Value]);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_BulkMergeRegionalWdd — TVP bulk upsert into dbo.RegionalWdd.
-- Merge key: (FileLogId, RegionId, ValidDate). RegionId is resolved by joining
-- dbo.Region on (the parent FileLog's RegionSetCode, RegionName) — the set is
-- read from FileLog -> RegionSet for each row's FileLogId. De-dups on
-- (FileLogId, RegionName, ValidDate). Returns RecordsProcessed.
--
-- Strict-header posture: if any RegionName fails to resolve for its set (or the
-- parent FileLog is missing/daily), that is a data-integrity error -> RAISERROR.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeRegionalWdd
    @Records dbo.RegionalWddTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    -- De-dup on the merge key (keep one arbitrary row per key).
    DECLARE @Src TABLE
    (
        FileLogId  INT          NOT NULL,
        RegionName VARCHAR(30)  NOT NULL,
        ValidDate  DATE         NOT NULL,
        [Value]    DECIMAL(9,4) NULL
    );
    INSERT INTO @Src (FileLogId, RegionName, ValidDate, [Value])
    SELECT FileLogId, RegionName, ValidDate, [Value]
    FROM (
        SELECT FileLogId, RegionName, ValidDate, [Value],
               ROW_NUMBER() OVER (PARTITION BY FileLogId, RegionName, ValidDate ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    ) AS d
    WHERE d.rn = 1;

    -- Strict: every (parent FileLog's set, RegionName) must resolve to dbo.Region.
    IF EXISTS (
        SELECT 1
        FROM @Src AS s
        LEFT JOIN dbo.FileLog   AS fl ON fl.Id           = s.FileLogId
        LEFT JOIN dbo.RegionSet AS rs ON rs.Id           = fl.RegionSetId
        LEFT JOIN dbo.Region    AS rg ON rg.RegionSetCode = rs.RegionSetCode
                                     AND rg.RegionName     = s.RegionName
        WHERE rg.Id IS NULL
    )
    BEGIN
        RAISERROR('usp_BulkMergeRegionalWdd: one or more RegionName values did not resolve to dbo.Region for the parent FileLog''s RegionSet.', 16, 1);
        RETURN;
    END

    MERGE dbo.RegionalWdd AS tgt
    USING (
        SELECT s.FileLogId, rg.Id AS RegionId, s.ValidDate, s.[Value]
        FROM @Src AS s
        JOIN dbo.FileLog   AS fl ON fl.Id            = s.FileLogId
        JOIN dbo.RegionSet AS rs ON rs.Id            = fl.RegionSetId
        JOIN dbo.Region    AS rg ON rg.RegionSetCode = rs.RegionSetCode
                                AND rg.RegionName     = s.RegionName
    ) AS src
       ON tgt.FileLogId = src.FileLogId
      AND tgt.RegionId  = src.RegionId
      AND tgt.ValidDate = src.ValidDate
    WHEN MATCHED THEN UPDATE SET
        [Value]       = src.[Value],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RegionId, ValidDate, [Value])
        VALUES (src.FileLogId, src.RegionId, src.ValidDate, src.[Value]);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_ValidateLoad — post-load anomaly report for an InitDate range.
-- Facts now reach InitDate / Model / Endpoint THROUGH FileLog, so every check
-- joins fact -> dbo.FileLog and filters on FileLog.InitDate. Returns ONE result
-- set with a uniform shape so the C# validator can log it generically:
--   CheckName      what was measured
--   Feed           'Daily' | 'Regional' | NULL
--   Scope          the grouping key the count applies to
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
-- Observational only (no side effects); the caller decides what is a hard
-- failure (a sparse day with many legitimate 404s must not fail the run).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.usp_ValidateLoad
    @InitDateFrom DATE,
    @InitDateTo   DATE
AS
BEGIN
    SET NOCOUNT ON;

    IF @InitDateFrom IS NULL OR @InitDateTo IS NULL
    BEGIN
        RAISERROR('usp_ValidateLoad: @InitDateFrom and @InitDateTo are required.', 16, 1);
        RETURN;
    END
    IF @InitDateFrom > @InitDateTo
    BEGIN
        RAISERROR('usp_ValidateLoad: @InitDateFrom must be <= @InitDateTo.', 16, 1);
        RETURN;
    END

    ;WITH Report AS
    (
        -- 1) Row counts by model (Daily), via FileLog.
        SELECT
            CAST('RowCountByModel' AS VARCHAR(40)) AS CheckName,
            CAST('Daily' AS VARCHAR(20))           AS Feed,
            CAST(m.ModelSlug AS NVARCHAR(200))     AS Scope,
            CAST(NULL AS INT)                      AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)               AS ActualCount,
            CAST(NULL AS NVARCHAR(400))            AS Detail
        FROM dbo.DailyWdd AS d
        JOIN dbo.FileLog  AS fl ON fl.Id = d.FileLogId
        JOIN dbo.Model    AS m  ON m.Id  = fl.ModelId
        WHERE fl.InitDate BETWEEN @InitDateFrom AND @InitDateTo
        GROUP BY m.ModelSlug

        UNION ALL
        -- 2) Row counts by model (Regional), via FileLog.
        SELECT
            CAST('RowCountByModel' AS VARCHAR(40)),
            CAST('Regional' AS VARCHAR(20)),
            CAST(m.ModelSlug AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM dbo.RegionalWdd AS r
        JOIN dbo.FileLog     AS fl ON fl.Id = r.FileLogId
        JOIN dbo.Model       AS m  ON m.Id  = fl.ModelId
        WHERE fl.InitDate BETWEEN @InitDateFrom AND @InitDateTo
        GROUP BY m.ModelSlug

        UNION ALL
        -- 3) Value null count vs total (Daily).
        SELECT
            CAST('ValueNullCount' AS VARCHAR(40)),
            CAST('Daily' AS VARCHAR(20)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(SUM(CASE WHEN d.[Value] IS NULL THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM dbo.DailyWdd AS d
        JOIN dbo.FileLog  AS fl ON fl.Id = d.FileLogId
        WHERE fl.InitDate BETWEEN @InitDateFrom AND @InitDateTo

        UNION ALL
        -- 4) Value null count vs total (Regional).
        SELECT
            CAST('ValueNullCount' AS VARCHAR(40)),
            CAST('Regional' AS VARCHAR(20)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(SUM(CASE WHEN r.[Value] IS NULL THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM dbo.RegionalWdd AS r
        JOIN dbo.FileLog     AS fl ON fl.Id = r.FileLogId
        WHERE fl.InitDate BETWEEN @InitDateFrom AND @InitDateTo

        UNION ALL
        -- 5) Regional region-coverage mismatch: distinct regions loaded per file
        --    vs the expected count for that file's set.
        SELECT
            CAST('RegionCoverageMismatch' AS VARCHAR(40)),
            CAST('Regional' AS VARCHAR(20)),
            CAST(CONCAT(m.ModelSlug, '/', CONVERT(VARCHAR(10), fl.InitDate, 23), '/', c.CycleCode, '/',
                        w.TypeSlug, '/', rs.RegionSetCode) AS NVARCHAR(200)),
            CAST(e.ExpectedCount AS INT),
            CAST(g.ActualCount AS BIGINT),
            CAST(CONCAT('expected=', e.ExpectedCount, ' actual=', g.ActualCount) AS NVARCHAR(400))
        FROM (
            SELECT r.FileLogId, COUNT(DISTINCT r.RegionId) AS ActualCount
            FROM dbo.RegionalWdd AS r
            JOIN dbo.FileLog     AS fl ON fl.Id = r.FileLogId
            WHERE fl.InitDate BETWEEN @InitDateFrom AND @InitDateTo
            GROUP BY r.FileLogId
        ) AS g
        JOIN dbo.FileLog   AS fl ON fl.Id = g.FileLogId
        JOIN dbo.Model     AS m  ON m.Id  = fl.ModelId
        JOIN dbo.Cycle     AS c  ON c.Id  = fl.CycleId
        JOIN dbo.WddType   AS w  ON w.Id  = fl.WddTypeId
        JOIN dbo.RegionSet AS rs ON rs.Id = fl.RegionSetId
        JOIN (
            SELECT RegionSetCode, COUNT(1) AS ExpectedCount
            FROM dbo.Region
            GROUP BY RegionSetCode
        ) AS e ON e.RegionSetCode = rs.RegionSetCode
        WHERE g.ActualCount <> e.ExpectedCount

        UNION ALL
        -- 6) Orphan cross-check (Daily): fact rows with no parent FileLog in
        --    range. FK makes this structurally impossible -> expected 0.
        SELECT
            CAST('OrphanFileLog' AS VARCHAR(40)),
            CAST('Daily' AS VARCHAR(20)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM dbo.DailyWdd AS d
        WHERE NOT EXISTS (SELECT 1 FROM dbo.FileLog AS fl WHERE fl.Id = d.FileLogId)

        UNION ALL
        -- 7) Orphan cross-check (Regional): fact rows with no parent FileLog.
        --    Expected 0.
        SELECT
            CAST('OrphanFileLog' AS VARCHAR(40)),
            CAST('Regional' AS VARCHAR(20)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM dbo.RegionalWdd AS r
        WHERE NOT EXISTS (SELECT 1 FROM dbo.FileLog AS fl WHERE fl.Id = r.FileLogId)
    )
    SELECT CheckName, Feed, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName, Feed, Scope;
END
GO
