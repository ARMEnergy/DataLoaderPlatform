-- =============================================================================
-- 003_CreateAgsiProcedures.sql
-- Stored procedures for the GIE AGSI loader (schema [arm]):
--   * arm.usp_UpsertFileLog              — per-request hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMergeGasStorageEntity  — TVP bulk upsert into arm.GasStorageEntity.
--   * arm.usp_BulkMergeGasStorage        — TVP bulk upsert into arm.GasStorage.
--   * arm.usp_ValidateLoad               — post-load observational report (StormVista
--                                          usp_ValidateLoad precedent; design §8/§11).
--   * arm.usp_GetGasStorageEntities      — read proc: DISTINCT (Id, Code) pairs from the
--                                          entity dimension (country-reference provider).
--                                          Renamed from usp_GetGasStorageEntityCodes;
--                                          the old name is dropped if present.
--
-- LOAD ORDER the procs imply (per request, CWG/StormVista posture):
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId (called for EVERY outcome:
--      Success / NotAvailable / Failed).
--   2) On rows > 0, stamp that FileLogId onto every TVP row and call the matching
--      arm.usp_BulkMerge... proc.
--
-- Each bulk-merge proc:
--   * MERGEs on the target's key (Entity: Code; Storage: (EntityId, GasDayStart)) —
--     NEVER on FileLogId (provenance) and NEVER on Date / Gas_Day (retained non-key
--     attributes; Gas_Day is a constant latest-available marker).
--   * De-dups the batch on that merge key (ROW_NUMBER over the TVP) before MERGE,
--     so an in-batch duplicate key cannot trigger "MERGE ... same row more than
--     once". For the entity, duplicate Code rows carry identical payloads (Austria's
--     6 SSOs → one tuple), so which row is kept is immaterial; ORDER BY (SELECT NULL)
--     keeps an arbitrary one, per CWG.
--   * On MATCHED updates the data columns AND FileLogId (provenance refresh) AND
--     ModifiedAtUtc; on NOT MATCHED inserts.
--   * Returns SELECT @@ROWCOUNT AS RecordsProcessed so the platform's SqlSinkBase
--     (ProcedureReturnsRowCount = true) can read the count.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable, and validate inputs.
-- Run 001 and 002 first. This script assumes AGSI is the current database.
-- =============================================================================

USE AGSI;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog — upsert one FileLog (hub) row per request and RETURN its
-- FileLogId. Called once per request for ALL outcomes. Region / RepresentativeDate
-- are NULL-able; the MERGE matches them with explicit NULL-equality
-- (col = col OR (col IS NULL AND col IS NULL)) so the undated / no-region About
-- request reuses a single stable hub row. Endpoint / Status are passed BY NAME and
-- resolved to their surrogate arm.Endpoint / arm.Status .Id (a miss RAISERRORs —
-- fixed catalogs seeded in 001); Region is passed by name and get-or-created in
-- arm.Region (non-NULL only), its Id stored as RegionId. NO @Variant parameter —
-- AGSI has no sub-variant (design §5; task natural key omits it).
--
-- RETURN CONTRACT: emits exactly one result set, one row, one column "FileLogId"
-- (the C# reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id
-- which yields the Id for BOTH the inserted and the updated branch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @Endpoint           VARCHAR(40),
    @Region             VARCHAR(16)   = NULL,
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

    -- Region is get-or-create, and only for a non-NULL country code; a NULL region
    -- stays a NULL RegionId (never registers a row). Re-select after the guarded
    -- insert so a concurrent insert of the same name is tolerated.
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
                  @RepresentativeDate AS RepresentativeDate) AS src
       ON  tgt.EndpointId = src.EndpointId
       AND (tgt.RegionId = src.RegionId OR (tgt.RegionId IS NULL AND src.RegionId IS NULL))
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
        INSERT (EndpointId, RegionId, RepresentativeDate,
                StatusId, HttpStatus, [RowCount], RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.RegionId, src.RepresentativeDate,
                @StatusId, @HttpStatus, @RowCount, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeGasStorageEntity — key (Code). Endpoint 1 country dimension.
-- De-dup the batch on Code before MERGE (Austria's 6 SSOs collapse to one identical
-- tuple). FileLogId is NULLable provenance.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeGasStorageEntity
    @Records arm.GasStorageEntityTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.GasStorageEntity AS tgt
    USING (
        SELECT Code, [Name], ParentCode, ParentName, FileLogId
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY Code ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.Code = src.Code
    WHEN MATCHED THEN UPDATE SET
        [Name]        = src.[Name],
        ParentCode    = src.ParentCode,
        ParentName    = src.ParentName,
        FileLogId     = src.FileLogId,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Code, [Name], ParentCode, ParentName, FileLogId)
        VALUES (src.Code, src.[Name], src.ParentCode, src.ParentName, src.FileLogId);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeGasStorage — key (EntityId, GasDayStart). Endpoint 2 daily fact.
-- De-dup the batch on (EntityId, GasDayStart) before MERGE. NEVER key on Date or
-- Gas_Day (retained non-key attributes; Gas_Day is a constant latest-available
-- marker) or FileLogId (provenance, UPDATEd on match). Date and Gas_Day are now
-- non-key retained columns, so they move into the UPDATE-on-match set. The 22-column
-- SELECT/INSERT order mirrors arm.GasStorageTvp (002) and arm.GasStorage (001).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeGasStorage
    @Records arm.GasStorageTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.GasStorage AS tgt
    USING (
        SELECT FileLogId, EntityId, [Date], Gas_Day, UpdatedAt, GasDayStart, GasDayEnd,
               GasInStorage, Consumption, ConsumptionFull, Injection, Withdrawal, NetWithdrawal,
               WorkingGasVolume, InjectionCapacity, WithdrawalCapacity, ContractedCapacity,
               AvailableCapacity, CoveredCapacity, [Status], Trend, [Full]
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY EntityId, GasDayStart ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.EntityId    = src.EntityId
       AND tgt.GasDayStart = src.GasDayStart
    WHEN MATCHED THEN UPDATE SET
        FileLogId          = src.FileLogId,
        [Date]             = src.[Date],
        Gas_Day            = src.Gas_Day,
        UpdatedAt          = src.UpdatedAt,
        GasDayEnd          = src.GasDayEnd,
        GasInStorage       = src.GasInStorage,
        Consumption        = src.Consumption,
        ConsumptionFull    = src.ConsumptionFull,
        Injection          = src.Injection,
        Withdrawal         = src.Withdrawal,
        NetWithdrawal      = src.NetWithdrawal,
        WorkingGasVolume   = src.WorkingGasVolume,
        InjectionCapacity  = src.InjectionCapacity,
        WithdrawalCapacity = src.WithdrawalCapacity,
        ContractedCapacity = src.ContractedCapacity,
        AvailableCapacity  = src.AvailableCapacity,
        CoveredCapacity    = src.CoveredCapacity,
        [Status]           = src.[Status],
        Trend              = src.Trend,
        [Full]             = src.[Full],
        ModifiedAtUtc      = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, EntityId, [Date], Gas_Day, UpdatedAt, GasDayStart, GasDayEnd,
                GasInStorage, Consumption, ConsumptionFull, Injection, Withdrawal, NetWithdrawal,
                WorkingGasVolume, InjectionCapacity, WithdrawalCapacity, ContractedCapacity,
                AvailableCapacity, CoveredCapacity, [Status], Trend, [Full])
        VALUES (src.FileLogId, src.EntityId, src.[Date], src.Gas_Day, src.UpdatedAt,
                src.GasDayStart, src.GasDayEnd,
                src.GasInStorage, src.Consumption, src.ConsumptionFull, src.Injection, src.Withdrawal,
                src.NetWithdrawal, src.WorkingGasVolume, src.InjectionCapacity, src.WithdrawalCapacity,
                src.ContractedCapacity, src.AvailableCapacity, src.CoveredCapacity, src.[Status],
                src.Trend, src.[Full]);

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad — post-load anomaly report for a request-date range
-- (arm.GasStorage.Date). Mirrors StormVista's dbo.usp_ValidateLoad: returns ONE
-- result set with a uniform shape so the C# AgsiLoadValidator (design §8) can log
-- it generically. Observational only (no side effects); the caller decides what is
-- a hard failure (a legitimately sparse day with many NotAvailable outcomes must
-- not fail the run).
--   CheckName      what was measured
--   Scope          the grouping key the count applies to
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @DateFrom DATE,
    @DateTo   DATE
AS
BEGIN
    SET NOCOUNT ON;

    IF @DateFrom IS NULL OR @DateTo IS NULL
    BEGIN
        RAISERROR('usp_ValidateLoad: @DateFrom and @DateTo are required.', 16, 1);
        RETURN;
    END
    IF @DateFrom > @DateTo
    BEGIN
        RAISERROR('usp_ValidateLoad: @DateFrom must be <= @DateTo.', 16, 1);
        RETURN;
    END

    ;WITH Report AS
    (
        -- 1) Entity dimension size (endpoint 1). Expect > 0 after an About load.
        SELECT
            CAST('EntityRowCount' AS VARCHAR(40))  AS CheckName,
            CAST(NULL AS NVARCHAR(200))            AS Scope,
            CAST(NULL AS INT)                      AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)               AS ActualCount,
            CAST(NULL AS NVARCHAR(400))            AS Detail
        FROM arm.GasStorageEntity

        UNION ALL
        -- 2) Entity rows with a blank/NULL Code or ParentCode (should be 0).
        SELECT
            CAST('EntityBlankKey' AS VARCHAR(40)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN LEN(ISNULL(e.Code, '')) = 0
                            OR LEN(ISNULL(e.ParentCode, '')) = 0 THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.GasStorageEntity AS e

        UNION ALL
        -- 3) Storage row count per entity over the window (via Date). Grouped by
        --    EntityId (arm.GasStorage no longer carries Code — it was normalized out
        --    to arm.GasStorageEntity); the entity Code is surfaced in Scope via a
        --    LEFT JOIN to the dimension so the report stays human-readable.
        SELECT
            CAST('StorageRowCountByEntity' AS VARCHAR(40)),
            CAST(CONCAT('EntityId=', g.EntityId, ISNULL(' Code=' + e.Code, '')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.GasStorage AS g
        LEFT JOIN arm.GasStorageEntity AS e ON e.Id = g.EntityId
        WHERE g.[Date] BETWEEN @DateFrom AND @DateTo
        GROUP BY g.EntityId, e.Code

        UNION ALL
        -- 4) status values outside the documented domain {C,E,N} (should be 0).
        SELECT
            CAST('StorageStatusDomain' AS VARCHAR(40)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN g.[Status] NOT IN ('C','E','N') THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.GasStorage AS g
        WHERE g.[Date] BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 5) gasDayEnd should equal gasDayStart + 1 (should be 0 mismatches).
        SELECT
            CAST('GasDayEndOffByOne' AS VARCHAR(40)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN g.GasDayEnd <> DATEADD(DAY, 1, g.GasDayStart) THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.GasStorage AS g
        WHERE g.[Date] BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 6) Orphan-entity guard. Formerly an "OrphanCountryCode" cross-check on
        --    Code; the storage table no longer carries Code, so this is re-expressed
        --    as a Storage EntityId absent from the entity dimension. It is now
        --    STRUCTURALLY IMPOSSIBLE — FK_GasStorage_Entity enforces
        --    arm.GasStorage.EntityId -> arm.GasStorageEntity(Id) — so it is always 0
        --    (retained as an assertion; a non-zero here would mean the FK was
        --    disabled/dropped). ISNULL keeps an empty window reading 0, not NULL.
        SELECT
            CAST('OrphanEntityId' AS VARCHAR(40)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN E.Id IS NULL THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST('enforced by FK_GasStorage_Entity' AS NVARCHAR(400))
        FROM arm.GasStorage AS g
             LEFT JOIN arm.GasStorageEntity E ON G.EntityId = E.Id
        WHERE g.[Date] BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 7) Load-bearing single-date invariant (design §10 / §11 item 2): the
        --    resume key uses the request Date while the MERGE keys on GasDayStart, so
        --    idempotency holds ONLY while Date == GasDayStart. A non-zero here means a
        --    response returned gasDayStart <> the request Date — revisit the
        --    resume-key basis. Should be 0.
        SELECT
            CAST('DateEqualsGasDayStart' AS VARCHAR(40)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN g.[Date] <> g.GasDayStart THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.GasStorage AS g
        WHERE g.[Date] BETWEEN @DateFrom AND @DateTo
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName, Scope;
END
GO

-- ----------------------------------------------------------------------------
-- Drop the OLD read-proc name so a re-run does not leave a stale proc behind
-- (renamed usp_GetGasStorageEntityCodes -> usp_GetGasStorageEntities this pass,
-- now that the caller needs the surrogate Id as well as the Code).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.usp_GetGasStorageEntityCodes', 'P') IS NOT NULL
    DROP PROCEDURE arm.usp_GetGasStorageEntityCodes;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_GetGasStorageEntities — read proc backing the C# country-reference
-- provider (IAgsiCountryProvider; keeps all DB access behind procedures — no inline
-- SQL in the loader). No parameters. Emits exactly one result set of two columns:
--   Id   INT          — arm.GasStorageEntity.Id, the FK target arm.GasStorage.EntityId
--                       points at; the provider stamps it onto every storage work
--                       unit / fact row.
--   Code VARCHAR(16)  — the endpoint-2 query code (e.g. 'AT'); the C# provider
--                       lowercases it for the URL / resume key and maps Code -> Id.
-- The DISTINCT non-blank countries, ordered by Code for a stable enumeration.
-- Because arm.GasStorageEntity has UNIQUE(Code), each Code maps to exactly one Id,
-- so DISTINCT Id, Code yields exactly one deterministic row per entity (no MIN(Id)
-- tie-break needed).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_GetGasStorageEntities
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT Id, Code
    FROM arm.GasStorageEntity
    WHERE Code IS NOT NULL
      AND Code <> ''
    ORDER BY Code;
END
GO
