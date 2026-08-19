-- =============================================================================
-- 003_CreateIHSPointLogicProcedures.sql
-- Stored procedures for the IHSPointLogic loader (schema [arm]):
--   * arm.usp_UpsertFileLog                    — per-request hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMerge<Table>  (x25)          — TVP bulk upsert into each data table.
--   * arm.usp_GetRegionIds / GetStateIds / GetPointTypeIds / GetSubRegionIds /
--     GetPointIds  (5 read procs)              — reference-provider discovery reads (design §3).
--   * arm.usp_GetEndpointSchedule              — per-endpoint hourly run schedule read (design §B.1/§B.2).
--   * arm.usp_ValidateLoad(@DateFrom,@DateTo)  — post-load observational anomaly report (§10).
--
-- Design of record: docs/design/IHSPointLogic.md §3 (read procs), §6 (FileLog),
-- §7 (the field/TVP/PK contract), §10 (validator). RUN ORDER: 001, 002 first.
-- IDEMPOTENT: every proc is CREATE OR ALTER, so the script is fully re-runnable.
--
-- LOAD ORDER the procs imply (per request, CWG/AGSI posture):
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId (called for EVERY outcome:
--      Success / NotAvailable / Failed), under SqlWriteGate on the C# side.
--   2) On rows > 0, stamp that FileLogId onto every TVP row and call the matching
--      arm.usp_BulkMerge<Table> (also under SqlWriteGate via SqlSinkBase).
--
-- Each bulk-merge proc:
--   * MERGEs on the target's PK (§7) — NEVER on FileLogId (provenance, UPDATEd on
--     match) and never on any injected/stamped non-key column.
--   * De-dups the batch on that PK (ROW_NUMBER over the TVP) before MERGE, so an
--     in-batch duplicate key cannot trigger "MERGE ... same row more than once".
--     An arbitrary row per key is kept (each snapshot is immutable within a run).
--   * On MATCHED updates the non-key columns AND FileLogId (provenance refresh) AND
--     ModifiedAtUtc; on NOT MATCHED inserts the full FileLogId-first column list.
--   * The SELECT/INSERT/VALUES column order mirrors the arm.<Table>Tvp (002) and the
--     arm.<Table> body (001) EXACTLY — the load-bearing sink contract.
--   * Returns SELECT @@ROWCOUNT AS RecordsProcessed so SqlSinkBase
--     (ProcedureReturnsRowCount = true) can read the count.
-- =============================================================================

USE IHSPointLogic;
GO

-- ============================================================================
-- arm.usp_UpsertFileLog — upsert one FileLog (hub) row per request and RETURN its
-- FileLogId. Called once per request for ALL outcomes. ParamKey / Variant /
-- RepresentativeDate are NULL-able and kept inline; the MERGE matches them with
-- explicit NULL-equality (col = col OR (col IS NULL AND col IS NULL)) so undated /
-- no-param requests reuse a single stable hub row. Endpoint / Status are passed BY
-- NAME and resolved to their surrogate arm.Endpoint / arm.Status .Id (a miss
-- RAISERRORs — fixed catalogs seeded in 001). Unlike CWG's Region slot, ParamKey is
-- NOT get-or-created into a lookup (it holds an unbounded set of numeric ids / batch
-- tokens; design §6/§11.7 permits inline VARCHAR).
--
-- RETURN CONTRACT: exactly one result set, one row, one column "FileLogId" (the C#
-- reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id (the Id
-- for BOTH the inserted and the updated branch).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @Endpoint           VARCHAR(40),
    @ParamKey           VARCHAR(32)   = NULL,
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

    -- ---- Resolve the lookup surrogate Ids (fixed catalogs seeded in 001) -----
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

    -- ---- Upsert the hub row, capturing the resulting Id ----------------------
    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE arm.FileLog AS tgt
    USING (SELECT @EndpointId AS EndpointId,
                  @ParamKey   AS ParamKey,
                  @Variant    AS Variant,
                  @RepresentativeDate AS RepresentativeDate) AS src
       ON  tgt.EndpointId = src.EndpointId
       AND (tgt.ParamKey = src.ParamKey OR (tgt.ParamKey IS NULL AND src.ParamKey IS NULL))
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
        INSERT (EndpointId, ParamKey, Variant, RepresentativeDate,
                StatusId, HttpStatus, [RowCount], RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.ParamKey, src.Variant, src.RepresentativeDate,
                @StatusId, @HttpStatus, @RowCount, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- =============================================================================
-- TIER 0 — DIMENSION MERGE PROCS (archetype A).
-- =============================================================================

-- §5 arm.usp_BulkMergeRegion — key (RegionId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeRegion
    @Records arm.RegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.Region AS tgt
    USING (
        SELECT FileLogId, RegionId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY RegionId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.RegionId = src.RegionId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RegionId, [Name])
        VALUES (src.FileLogId, src.RegionId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §6a arm.usp_BulkMergeState — key (StateId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeState
    @Records arm.StateTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.State AS tgt
    USING (
        SELECT FileLogId, StateId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY StateId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.StateId = src.StateId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, StateId, [Name])
        VALUES (src.FileLogId, src.StateId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §6b arm.usp_BulkMergePointStatus — key (PointStatusId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePointStatus
    @Records arm.PointStatusTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PointStatus AS tgt
    USING (
        SELECT FileLogId, PointStatusId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PointStatusId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PointStatusId = src.PointStatusId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PointStatusId, [Name])
        VALUES (src.FileLogId, src.PointStatusId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §6c arm.usp_BulkMergePointType — key (PointTypeId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePointType
    @Records arm.PointTypeTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PointType AS tgt
    USING (
        SELECT FileLogId, PointTypeId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PointTypeId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PointTypeId = src.PointTypeId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PointTypeId, [Name])
        VALUES (src.FileLogId, src.PointTypeId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §6d arm.usp_BulkMergePipelineNoticeCategory — key (PipelineNoticeCategoryId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelineNoticeCategory
    @Records arm.PipelineNoticeCategoryTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PipelineNoticeCategory AS tgt
    USING (
        SELECT FileLogId, PipelineNoticeCategoryId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PipelineNoticeCategoryId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PipelineNoticeCategoryId = src.PipelineNoticeCategoryId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PipelineNoticeCategoryId, [Name])
        VALUES (src.FileLogId, src.PipelineNoticeCategoryId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §6e arm.usp_BulkMergePipeline — key (PipelineId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipeline
    @Records arm.PipelineTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.Pipeline AS tgt
    USING (
        SELECT FileLogId, PipelineId, [Name], LegacyName
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PipelineId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PipelineId = src.PipelineId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        LegacyName    = src.LegacyName,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PipelineId, [Name], LegacyName)
        VALUES (src.FileLogId, src.PipelineId, src.[Name], src.LegacyName);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §7 arm.usp_BulkMergePoint — key (PointId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePoint
    @Records arm.PointTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.Point AS tgt
    USING (
        SELECT FileLogId, PointId, [Name], PipelineId, PointTypeId, PointStatusId
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PointId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PointId = src.PointId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        PipelineId    = src.PipelineId,
        PointTypeId   = src.PointTypeId,
        PointStatusId = src.PointStatusId,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PointId, [Name], PipelineId, PointTypeId, PointStatusId)
        VALUES (src.FileLogId, src.PointId, src.[Name], src.PipelineId, src.PointTypeId, src.PointStatusId);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §12 arm.usp_BulkMergePointMetadata — key (PointId). 22 business cols.
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePointMetadata
    @Records arm.PointMetadataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PointMetadata AS tgt
    USING (
        SELECT FileLogId, PointId, PointLciId, Drn, PointName, PointTypeId, PointType, StateId, [State],
               County, Region, PipelineId, PipelineDisplayName, PointIsActive, DesignCapacity,
               FlowDirectionId, FlowDirection, DisplayName, LocProp, PointLatitude, PointLongitude,
               CountyId, RegionId
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PointId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PointId = src.PointId
    WHEN MATCHED THEN UPDATE SET
        FileLogId           = src.FileLogId,
        PointLciId          = src.PointLciId,
        Drn                 = src.Drn,
        PointName           = src.PointName,
        PointTypeId         = src.PointTypeId,
        PointType           = src.PointType,
        StateId             = src.StateId,
        [State]             = src.[State],
        County              = src.County,
        Region              = src.Region,
        PipelineId          = src.PipelineId,
        PipelineDisplayName = src.PipelineDisplayName,
        PointIsActive       = src.PointIsActive,
        DesignCapacity      = src.DesignCapacity,
        FlowDirectionId     = src.FlowDirectionId,
        FlowDirection       = src.FlowDirection,
        DisplayName         = src.DisplayName,
        LocProp             = src.LocProp,
        PointLatitude       = src.PointLatitude,
        PointLongitude      = src.PointLongitude,
        CountyId            = src.CountyId,
        RegionId            = src.RegionId,
        ModifiedAtUtc       = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PointId, PointLciId, Drn, PointName, PointTypeId, PointType, StateId, [State],
                County, Region, PipelineId, PipelineDisplayName, PointIsActive, DesignCapacity,
                FlowDirectionId, FlowDirection, DisplayName, LocProp, PointLatitude, PointLongitude,
                CountyId, RegionId)
        VALUES (src.FileLogId, src.PointId, src.PointLciId, src.Drn, src.PointName, src.PointTypeId,
                src.PointType, src.StateId, src.[State], src.County, src.Region, src.PipelineId,
                src.PipelineDisplayName, src.PointIsActive, src.DesignCapacity, src.FlowDirectionId,
                src.FlowDirection, src.DisplayName, src.LocProp, src.PointLatitude, src.PointLongitude,
                src.CountyId, src.RegionId);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §15 arm.usp_BulkMergePipelineNoticeSearch — key (Id).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelineNoticeSearch
    @Records arm.PipelineNoticeSearchTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PipelineNoticeSearch AS tgt
    USING (
        SELECT FileLogId, Id, Subject, Format, PostedDate, ExternalId, CategoryId, IsCritical,
               ContentLink, PipelineId, EffectiveDate, EndDate
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.Id = src.Id
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        Subject       = src.Subject,
        Format        = src.Format,
        PostedDate    = src.PostedDate,
        ExternalId    = src.ExternalId,
        CategoryId    = src.CategoryId,
        IsCritical    = src.IsCritical,
        ContentLink   = src.ContentLink,
        PipelineId    = src.PipelineId,
        EffectiveDate = src.EffectiveDate,
        EndDate       = src.EndDate,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, Id, Subject, Format, PostedDate, ExternalId, CategoryId, IsCritical,
                ContentLink, PipelineId, EffectiveDate, EndDate)
        VALUES (src.FileLogId, src.Id, src.Subject, src.Format, src.PostedDate, src.ExternalId,
                src.CategoryId, src.IsCritical, src.ContentLink, src.PipelineId, src.EffectiveDate,
                src.EndDate);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- =============================================================================
-- TIER 0 — FACT SNAPSHOT MERGE PROCS (archetype B).
-- =============================================================================

-- §8 arm.usp_BulkMergeDemandForecastRegion — key (ForecastDate, Date, Region, Subregion).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeDemandForecastRegion
    @Records arm.DemandForecastRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.DemandForecastRegion AS tgt
    USING (
        SELECT FileLogId, ForecastDate, [Date], Region, Subregion, DepartureFromNormalF,
               NormalTemperatureF, TotalConsumption, Power, Industrial, ResCom, DepartureFromNormal
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, [Date], Region, Subregion ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate = src.ForecastDate
       AND tgt.[Date]       = src.[Date]
       AND tgt.Region       = src.Region
       AND tgt.Subregion    = src.Subregion
    WHEN MATCHED THEN UPDATE SET
        FileLogId            = src.FileLogId,
        DepartureFromNormalF = src.DepartureFromNormalF,
        NormalTemperatureF   = src.NormalTemperatureF,
        TotalConsumption     = src.TotalConsumption,
        Power                = src.Power,
        Industrial           = src.Industrial,
        ResCom               = src.ResCom,
        DepartureFromNormal  = src.DepartureFromNormal,
        ModifiedAtUtc        = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ForecastDate, [Date], Region, Subregion, DepartureFromNormalF,
                NormalTemperatureF, TotalConsumption, Power, Industrial, ResCom, DepartureFromNormal)
        VALUES (src.FileLogId, src.ForecastDate, src.[Date], src.Region, src.Subregion,
                src.DepartureFromNormalF, src.NormalTemperatureF, src.TotalConsumption, src.Power,
                src.Industrial, src.ResCom, src.DepartureFromNormal);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §9 arm.usp_BulkMergeDemandForecastUsLower48 — key (ForecastDate, Date, Region).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeDemandForecastUsLower48
    @Records arm.DemandForecastUsLower48Tvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.DemandForecastUsLower48 AS tgt
    USING (
        SELECT FileLogId, ForecastDate, [Date], Region, Power, Industrial, ResidentialCommercial, Subtotal
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY ForecastDate, [Date], Region ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ForecastDate = src.ForecastDate
       AND tgt.[Date]       = src.[Date]
       AND tgt.Region       = src.Region
    WHEN MATCHED THEN UPDATE SET
        FileLogId             = src.FileLogId,
        Power                 = src.Power,
        Industrial            = src.Industrial,
        ResidentialCommercial = src.ResidentialCommercial,
        Subtotal              = src.Subtotal,
        ModifiedAtUtc         = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ForecastDate, [Date], Region, Power, Industrial, ResidentialCommercial, Subtotal)
        VALUES (src.FileLogId, src.ForecastDate, src.[Date], src.Region, src.Power, src.Industrial,
                src.ResidentialCommercial, src.Subtotal);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §10 arm.usp_BulkMergeGasProductionProducingArea
--     key (ReportedDate, ReferenceDate, Region, ProducingArea, State).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeGasProductionProducingArea
    @Records arm.GasProductionProducingAreaTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.GasProductionProducingArea AS tgt
    USING (
        SELECT FileLogId, ReportedDate, ReferenceDate, Region, ProducingArea, [State],
               DryFactoredValue, WellheadValue
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY ReportedDate, ReferenceDate, Region, ProducingArea, [State] ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ReportedDate  = src.ReportedDate
       AND tgt.ReferenceDate = src.ReferenceDate
       AND tgt.Region        = src.Region
       AND tgt.ProducingArea = src.ProducingArea
       AND tgt.[State]       = src.[State]
    WHEN MATCHED THEN UPDATE SET
        FileLogId        = src.FileLogId,
        DryFactoredValue = src.DryFactoredValue,
        WellheadValue    = src.WellheadValue,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ReportedDate, ReferenceDate, Region, ProducingArea, [State],
                DryFactoredValue, WellheadValue)
        VALUES (src.FileLogId, src.ReportedDate, src.ReferenceDate, src.Region, src.ProducingArea,
                src.[State], src.DryFactoredValue, src.WellheadValue);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §11 arm.usp_BulkMergeMarketBalancesUsLower48 — key (TimePeriod). 16 measures.
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeMarketBalancesUsLower48
    @Records arm.MarketBalancesUsLower48Tvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.MarketBalancesUsLower48 AS tgt
    USING (
        SELECT FileLogId, TimePeriod, Wellhead, ProductionLoss, DryGas, CanadaImports, LngSendout,
               TotalSupply, Power, Industrial, ResidentialCommercial, Subtotal, MexicoExports,
               LngFeedGas, PipeLoss, TotalDemand, Storage, BalancingItem
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY TimePeriod ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.TimePeriod = src.TimePeriod
    WHEN MATCHED THEN UPDATE SET
        FileLogId             = src.FileLogId,
        Wellhead              = src.Wellhead,
        ProductionLoss        = src.ProductionLoss,
        DryGas                = src.DryGas,
        CanadaImports         = src.CanadaImports,
        LngSendout            = src.LngSendout,
        TotalSupply           = src.TotalSupply,
        Power                 = src.Power,
        Industrial            = src.Industrial,
        ResidentialCommercial = src.ResidentialCommercial,
        Subtotal              = src.Subtotal,
        MexicoExports         = src.MexicoExports,
        LngFeedGas            = src.LngFeedGas,
        PipeLoss              = src.PipeLoss,
        TotalDemand           = src.TotalDemand,
        Storage               = src.Storage,
        BalancingItem         = src.BalancingItem,
        ModifiedAtUtc         = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, TimePeriod, Wellhead, ProductionLoss, DryGas, CanadaImports, LngSendout,
                TotalSupply, Power, Industrial, ResidentialCommercial, Subtotal, MexicoExports,
                LngFeedGas, PipeLoss, TotalDemand, Storage, BalancingItem)
        VALUES (src.FileLogId, src.TimePeriod, src.Wellhead, src.ProductionLoss, src.DryGas,
                src.CanadaImports, src.LngSendout, src.TotalSupply, src.Power, src.Industrial,
                src.ResidentialCommercial, src.Subtotal, src.MexicoExports, src.LngFeedGas,
                src.PipeLoss, src.TotalDemand, src.Storage, src.BalancingItem);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §13 arm.usp_BulkMergeModeledDemandRegionType — key (ReferenceDate, PLEProductName, RegionName).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeModeledDemandRegionType
    @Records arm.ModeledDemandRegionTypeTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.ModeledDemandRegionType AS tgt
    USING (
        SELECT FileLogId, ReferenceDate, PLEProductName, RegionName, Volume
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY ReferenceDate, PLEProductName, RegionName ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ReferenceDate  = src.ReferenceDate
       AND tgt.PLEProductName = src.PLEProductName
       AND tgt.RegionName     = src.RegionName
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        Volume        = src.Volume,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ReferenceDate, PLEProductName, RegionName, Volume)
        VALUES (src.FileLogId, src.ReferenceDate, src.PLEProductName, src.RegionName, src.Volume);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §14 arm.usp_BulkMergePipelineFlowThroughput
--     key (FlowDate, Region, Pipeline, Throughput, FlowType).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelineFlowThroughput
    @Records arm.PipelineFlowThroughputTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PipelineFlowThroughput AS tgt
    USING (
        SELECT FileLogId, FlowDate, Region, Pipeline, Throughput, FlowType, Volume
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY FlowDate, Region, Pipeline, Throughput, FlowType ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.FlowDate   = src.FlowDate
       AND tgt.Region     = src.Region
       AND tgt.Pipeline   = src.Pipeline
       AND tgt.Throughput = src.Throughput
       AND tgt.FlowType   = src.FlowType
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        Volume        = src.Volume,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, FlowDate, Region, Pipeline, Throughput, FlowType, Volume)
        VALUES (src.FileLogId, src.FlowDate, src.Region, src.Pipeline, src.Throughput, src.FlowType, src.Volume);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §16 arm.usp_BulkMergeUsImportsExportsByPointsAggregate
--     key (RunDate, FlowDate, PointName, PipelineName, LedgerSide).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeUsImportsExportsByPointsAggregate
    @Records arm.UsImportsExportsByPointsAggregateTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.UsImportsExportsByPointsAggregate AS tgt
    USING (
        SELECT FileLogId, RunDate, FlowDate, PointName, PipelineName, LedgerSide, Volume, [State],
               County, [Type], PointGroupName, DistrictName
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY RunDate, FlowDate, PointName, PipelineName, LedgerSide ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate      = src.RunDate
       AND tgt.FlowDate     = src.FlowDate
       AND tgt.PointName    = src.PointName
       AND tgt.PipelineName = src.PipelineName
       AND tgt.LedgerSide   = src.LedgerSide
    WHEN MATCHED THEN UPDATE SET
        FileLogId      = src.FileLogId,
        Volume         = src.Volume,
        [State]        = src.[State],
        County         = src.County,
        [Type]         = src.[Type],
        PointGroupName = src.PointGroupName,
        DistrictName   = src.DistrictName,
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RunDate, FlowDate, PointName, PipelineName, LedgerSide, Volume, [State],
                County, [Type], PointGroupName, DistrictName)
        VALUES (src.FileLogId, src.RunDate, src.FlowDate, src.PointName, src.PipelineName,
                src.LedgerSide, src.Volume, src.[State], src.County, src.[Type], src.PointGroupName,
                src.DistrictName);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §17 arm.usp_BulkMergeUsSampleStorageFacility
--     key (ReportDate, FlowDate, Name, EiaRegion, State, FieldType).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeUsSampleStorageFacility
    @Records arm.UsSampleStorageFacilityTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.UsSampleStorageFacility AS tgt
    USING (
        SELECT FileLogId, ReportDate, FlowDate, [Name], EiaRegion, [State], FieldType, Volume
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY ReportDate, FlowDate, [Name], EiaRegion, [State], FieldType ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.ReportDate = src.ReportDate
       AND tgt.FlowDate   = src.FlowDate
       AND tgt.[Name]     = src.[Name]
       AND tgt.EiaRegion  = src.EiaRegion
       AND tgt.[State]    = src.[State]
       AND tgt.FieldType  = src.FieldType
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        Volume        = src.Volume,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, ReportDate, FlowDate, [Name], EiaRegion, [State], FieldType, Volume)
        VALUES (src.FileLogId, src.ReportDate, src.FlowDate, src.[Name], src.EiaRegion, src.[State],
                src.FieldType, src.Volume);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §18 arm.usp_BulkMergeStateFlowsThroughputAggregate
--     key (FlowDate, Region, FromState, ToState, FlowType).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStateFlowsThroughputAggregate
    @Records arm.StateFlowsThroughputAggregateTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.StateFlowsThroughputAggregate AS tgt
    USING (
        SELECT FileLogId, FlowDate, Region, FromState, ToState, FlowType, Volume
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY FlowDate, Region, FromState, ToState, FlowType ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.FlowDate  = src.FlowDate
       AND tgt.Region    = src.Region
       AND tgt.FromState = src.FromState
       AND tgt.ToState   = src.ToState
       AND tgt.FlowType  = src.FlowType
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        Volume        = src.Volume,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, FlowDate, Region, FromState, ToState, FlowType, Volume)
        VALUES (src.FileLogId, src.FlowDate, src.Region, src.FromState, src.ToState, src.FlowType, src.Volume);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §19 arm.usp_BulkMergeSupplyAndDemand — key (Date). 16 measures (marketsHistory).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSupplyAndDemand
    @Records arm.SupplyAndDemandTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.SupplyAndDemand AS tgt
    USING (
        SELECT FileLogId, [Date], Wellhead, ProductionLoss, DryGas, CanadaImports, LngSendout,
               TotalSupply, Power, Industrial, ResidentialCommercial, Subtotal, MexicoExports,
               LngFeedGas, PipeLoss, TotalDemand, Storage, BalancingItem
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY [Date] ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.[Date] = src.[Date]
    WHEN MATCHED THEN UPDATE SET
        FileLogId             = src.FileLogId,
        Wellhead              = src.Wellhead,
        ProductionLoss        = src.ProductionLoss,
        DryGas                = src.DryGas,
        CanadaImports         = src.CanadaImports,
        LngSendout            = src.LngSendout,
        TotalSupply           = src.TotalSupply,
        Power                 = src.Power,
        Industrial            = src.Industrial,
        ResidentialCommercial = src.ResidentialCommercial,
        Subtotal              = src.Subtotal,
        MexicoExports         = src.MexicoExports,
        LngFeedGas            = src.LngFeedGas,
        PipeLoss              = src.PipeLoss,
        TotalDemand           = src.TotalDemand,
        Storage               = src.Storage,
        BalancingItem         = src.BalancingItem,
        ModifiedAtUtc         = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, [Date], Wellhead, ProductionLoss, DryGas, CanadaImports, LngSendout,
                TotalSupply, Power, Industrial, ResidentialCommercial, Subtotal, MexicoExports,
                LngFeedGas, PipeLoss, TotalDemand, Storage, BalancingItem)
        VALUES (src.FileLogId, src.[Date], src.Wellhead, src.ProductionLoss, src.DryGas,
                src.CanadaImports, src.LngSendout, src.TotalSupply, src.Power, src.Industrial,
                src.ResidentialCommercial, src.Subtotal, src.MexicoExports, src.LngFeedGas,
                src.PipeLoss, src.TotalDemand, src.Storage, src.BalancingItem);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- =============================================================================
-- TIER 1 — DISCOVERY-FED LOOKUP MERGE PROCS (archetype C).
-- =============================================================================

-- §20 arm.usp_BulkMergeCounty — key (CountyId, StateId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCounty
    @Records arm.CountyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.County AS tgt
    USING (
        SELECT FileLogId, CountyId, StateId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY CountyId, StateId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.CountyId = src.CountyId
       AND tgt.StateId  = src.StateId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, CountyId, StateId, [Name])
        VALUES (src.FileLogId, src.CountyId, src.StateId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §21 arm.usp_BulkMergeFacility — key (FacilityId, PointTypeId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFacility
    @Records arm.FacilityTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.Facility AS tgt
    USING (
        SELECT FileLogId, FacilityId, PointTypeId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY FacilityId, PointTypeId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.FacilityId  = src.FacilityId
       AND tgt.PointTypeId = src.PointTypeId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, FacilityId, PointTypeId, [Name])
        VALUES (src.FileLogId, src.FacilityId, src.PointTypeId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §22 arm.usp_BulkMergeSubregion — key (SubRegionId, RegionId).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSubregion
    @Records arm.SubregionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.Subregion AS tgt
    USING (
        SELECT FileLogId, SubRegionId, RegionId, [Name]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY SubRegionId, RegionId ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.SubRegionId = src.SubRegionId
       AND tgt.RegionId    = src.RegionId
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        [Name]        = src.[Name],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, SubRegionId, RegionId, [Name])
        VALUES (src.FileLogId, src.SubRegionId, src.RegionId, src.[Name]);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- =============================================================================
-- TIER 2 — PARAMETRIZED FACT MERGE PROCS (archetypes D, E).
-- =============================================================================

-- §23 arm.usp_BulkMergeSupplyAndDemandByRegion — key (RegionId, Date, Product).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSupplyAndDemandByRegion
    @Records arm.SupplyAndDemandByRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.SupplyAndDemandByRegion AS tgt
    USING (
        SELECT FileLogId, RegionId, [Date], Product, VolumeMmcfd
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY RegionId, [Date], Product ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RegionId = src.RegionId
       AND tgt.[Date]   = src.[Date]
       AND tgt.Product  = src.Product
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        VolumeMmcfd   = src.VolumeMmcfd,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, RegionId, [Date], Product, VolumeMmcfd)
        VALUES (src.FileLogId, src.RegionId, src.[Date], src.Product, src.VolumeMmcfd);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §24 arm.usp_BulkMergeSupplyAndDemandBySubRegion
--     key (SubRegionId, RegionId, Date, Product).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSupplyAndDemandBySubRegion
    @Records arm.SupplyAndDemandBySubRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.SupplyAndDemandBySubRegion AS tgt
    USING (
        SELECT FileLogId, SubRegionId, RegionId, [Date], Product, VolumeMmcfd
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY SubRegionId, RegionId, [Date], Product ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.SubRegionId = src.SubRegionId
       AND tgt.RegionId    = src.RegionId
       AND tgt.[Date]      = src.[Date]
       AND tgt.Product     = src.Product
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        VolumeMmcfd   = src.VolumeMmcfd,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, SubRegionId, RegionId, [Date], Product, VolumeMmcfd)
        VALUES (src.FileLogId, src.SubRegionId, src.RegionId, src.[Date], src.Product, src.VolumeMmcfd);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- §25 arm.usp_BulkMergePointVolume — key (PointId, Date).
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePointVolume
    @Records arm.PointVolumeTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE arm.PointVolume AS tgt
    USING (
        SELECT FileLogId, PointId, [Date], Volume
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PointId, [Date] ORDER BY (SELECT NULL)) AS rn FROM @Records) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.PointId = src.PointId
       AND tgt.[Date]  = src.[Date]
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        Volume        = src.Volume,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PointId, [Date], Volume)
        VALUES (src.FileLogId, src.PointId, src.[Date], src.Volume);
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- =============================================================================
-- READ PROCS (5) — reference-provider discovery reads (design §3). Each returns
-- DISTINCT, non-null ids ordered ascending from its just-refreshed table. No
-- parameters; a plain read (no SqlWriteGate needed). Keeping all DB access behind
-- procedures — no inline SQL in the loader.
-- =============================================================================

-- arm.usp_GetRegionIds — RegionId list (feeds Subregion T1 + SD-by-region T2).
CREATE OR ALTER PROCEDURE arm.usp_GetRegionIds
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT RegionId
    FROM arm.Region
    WHERE RegionId IS NOT NULL
    ORDER BY RegionId;
END
GO

-- arm.usp_GetStateIds — StateId list (feeds County T1).
CREATE OR ALTER PROCEDURE arm.usp_GetStateIds
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT StateId
    FROM arm.State
    WHERE StateId IS NOT NULL
    ORDER BY StateId;
END
GO

-- arm.usp_GetPointTypeIds — PointTypeId list (feeds Facility T1).
CREATE OR ALTER PROCEDURE arm.usp_GetPointTypeIds
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT PointTypeId
    FROM arm.PointType
    WHERE PointTypeId IS NOT NULL
    ORDER BY PointTypeId;
END
GO

-- arm.usp_GetSubRegionIds — DISTINCT (SubRegionId, RegionId) pairs (feeds
-- SD-by-subregion T2; also surfaces the SubRegionId -> RegionId map for the
-- injected RegionId PK column). Read from arm.Subregion (refreshed in T1).
CREATE OR ALTER PROCEDURE arm.usp_GetSubRegionIds
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT SubRegionId, RegionId
    FROM arm.Subregion
    WHERE SubRegionId IS NOT NULL
      AND RegionId IS NOT NULL
    ORDER BY SubRegionId, RegionId;
END
GO

-- arm.usp_GetPointIds — ACTIVE PointId list (feeds PointVolume T2 batching). Scope
-- = active points only: reads arm.PointMetadata WHERE PointIsActive = 1 (design
-- §11.3 / decision 3). This table is the FK target for arm.PointVolume(PointId),
-- so every returned id is a valid FK parent.
CREATE OR ALTER PROCEDURE arm.usp_GetPointIds
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT PointId
    FROM arm.PointMetadata
    WHERE PointIsActive = 1
      AND PointId IS NOT NULL
    ORDER BY PointId;
END
GO

-- =============================================================================
-- arm.usp_GetEndpointSchedule — per-endpoint hourly run schedule (design §B.1/§B.2).
-- Returns (Name, RunHoursCST) for ALL arm.Endpoint rows (Name = the EndpointId). No
-- parameters; a plain read (no SqlWriteGate). Feeds the load-once IPlEndpointSchedule
-- cache, which parses RunHoursCST into a per-endpoint hour set (fail-open to
-- every-hour on blank / invalid / out-of-range / a missing row) and gates each
-- enabled endpoint's per-hour cadence. Keeps all DB access behind a proc.
-- =============================================================================
CREATE OR ALTER PROCEDURE arm.usp_GetEndpointSchedule
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [Name], RunHoursCST
    FROM arm.Endpoint
    ORDER BY [Name];
END
GO

-- =============================================================================
-- arm.usp_ValidateLoad — post-load observational anomaly report over a fact-date
-- window (design §10). Emits ONE result set with a uniform shape so the C#
-- IHSPointLogicLoadValidator can log it generically. OBSERVATIONAL ONLY (no side
-- effects); the caller decides what is a hard failure — a legitimately sparse day
-- with many NotAvailable outcomes must NOT fail the run.
--   CheckName      what was measured
--   Scope          the grouping key the count applies to (NULL when table-wide)
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
-- Dimensions are counted in full (no date axis); facts are windowed on their
-- representative date column. Referential-sanity checks target the LOGICAL-only
-- references (the enforced FKs make their orphans structurally 0); domain checks
-- assert small documented value sets. Every orphan/domain check EXPECTS 0.
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
        -- ---- 1) Per-table row counts: DIMENSIONS (full, no date axis) ---------
        SELECT CAST('RowCount_Region'                  AS VARCHAR(64)) AS CheckName, CAST(NULL AS NVARCHAR(200)) AS Scope, CAST(NULL AS INT) AS ExpectedCount, CAST(COUNT(1) AS BIGINT) AS ActualCount, CAST(NULL AS NVARCHAR(400)) AS Detail FROM arm.Region
        UNION ALL SELECT 'RowCount_State',                  NULL, NULL, COUNT(1), NULL FROM arm.State
        UNION ALL SELECT 'RowCount_PointStatus',            NULL, NULL, COUNT(1), NULL FROM arm.PointStatus
        UNION ALL SELECT 'RowCount_PointType',              NULL, NULL, COUNT(1), NULL FROM arm.PointType
        UNION ALL SELECT 'RowCount_PipelineNoticeCategory', NULL, NULL, COUNT(1), NULL FROM arm.PipelineNoticeCategory
        UNION ALL SELECT 'RowCount_Pipeline',               NULL, NULL, COUNT(1), NULL FROM arm.Pipeline
        UNION ALL SELECT 'RowCount_Point',                  NULL, NULL, COUNT(1), NULL FROM arm.Point
        UNION ALL SELECT 'RowCount_PointMetadata',          NULL, NULL, COUNT(1), NULL FROM arm.PointMetadata
        UNION ALL SELECT 'RowCount_County',                 NULL, NULL, COUNT(1), NULL FROM arm.County
        UNION ALL SELECT 'RowCount_Facility',               NULL, NULL, COUNT(1), NULL FROM arm.Facility
        UNION ALL SELECT 'RowCount_Subregion',              NULL, NULL, COUNT(1), NULL FROM arm.Subregion
        UNION ALL SELECT 'RowCount_PipelineNoticeSearch',     NULL, NULL, COUNT(1), CAST('unwindowed (no clean date axis)' AS NVARCHAR(400)) FROM arm.PipelineNoticeSearch

        -- ---- 2) Per-table row counts: FACTS (windowed on representative date) --
        UNION ALL SELECT 'RowCount_DemandForecastRegion',             NULL, NULL, COUNT(1), NULL FROM arm.DemandForecastRegion             WHERE ForecastDate  BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_DemandForecastUsLower48',          NULL, NULL, COUNT(1), NULL FROM arm.DemandForecastUsLower48          WHERE ForecastDate  BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_GasProductionProducingArea',       NULL, NULL, COUNT(1), NULL FROM arm.GasProductionProducingArea       WHERE ReferenceDate BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_MarketBalancesUsLower48',          NULL, NULL, COUNT(1), NULL FROM arm.MarketBalancesUsLower48          WHERE TimePeriod    BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_ModeledDemandRegionType',          NULL, NULL, COUNT(1), NULL FROM arm.ModeledDemandRegionType          WHERE ReferenceDate BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_PipelineFlowThroughput',           NULL, NULL, COUNT(1), NULL FROM arm.PipelineFlowThroughput           WHERE FlowDate      BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_UsImportsExportsByPointsAggregate', NULL, NULL, COUNT(1), NULL FROM arm.UsImportsExportsByPointsAggregate WHERE FlowDate     BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_UsSampleStorageFacility',          NULL, NULL, COUNT(1), NULL FROM arm.UsSampleStorageFacility          WHERE FlowDate      BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_StateFlowsThroughputAggregate',    NULL, NULL, COUNT(1), NULL FROM arm.StateFlowsThroughputAggregate    WHERE FlowDate      BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_SupplyAndDemand',                  NULL, NULL, COUNT(1), NULL FROM arm.SupplyAndDemand                  WHERE [Date]        BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_SupplyAndDemandByRegion',          NULL, NULL, COUNT(1), NULL FROM arm.SupplyAndDemandByRegion          WHERE [Date]        BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_SupplyAndDemandBySubRegion',       NULL, NULL, COUNT(1), NULL FROM arm.SupplyAndDemandBySubRegion       WHERE [Date]        BETWEEN @DateFrom AND @DateTo
        UNION ALL SELECT 'RowCount_PointVolume',                      NULL, NULL, COUNT(1), NULL FROM arm.PointVolume                      WHERE [Date]        BETWEEN @DateFrom AND @DateTo

        -- ---- 3) Referential sanity (LOGICAL-only refs; expect 0 orphans) ------
        UNION ALL
        SELECT 'Orphan_Point_PipelineId', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('Point.PipelineId not in Pipeline' AS NVARCHAR(400))
        FROM arm.Point p
        WHERE p.PipelineId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Pipeline pl WHERE pl.PipelineId = p.PipelineId)

        UNION ALL
        SELECT 'Orphan_Point_PointTypeId', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('Point.PointTypeId not in PointType' AS NVARCHAR(400))
        FROM arm.Point p
        WHERE p.PointTypeId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.PointType t WHERE t.PointTypeId = p.PointTypeId)

        UNION ALL
        SELECT 'Orphan_Point_PointStatusId', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('Point.PointStatusId not in PointStatus' AS NVARCHAR(400))
        FROM arm.Point p
        WHERE p.PointStatusId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.PointStatus s WHERE s.PointStatusId = p.PointStatusId)

        UNION ALL
        SELECT 'Orphan_PointMetadata_CountyId', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('PointMetadata.CountyId not in County (County is a Tier-1 table)' AS NVARCHAR(400))
        FROM arm.PointMetadata m
        WHERE m.CountyId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.County c WHERE c.CountyId = m.CountyId)

        UNION ALL
        SELECT 'Orphan_Notice_CategoryId', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('PipelineNoticeSearch.CategoryId not in PipelineNoticeCategory' AS NVARCHAR(400))
        FROM arm.PipelineNoticeSearch n
        WHERE n.CategoryId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.PipelineNoticeCategory c WHERE c.PipelineNoticeCategoryId = n.CategoryId)

        UNION ALL
        SELECT 'Orphan_Notice_PipelineId', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('PipelineNoticeSearch.PipelineId not in Pipeline' AS NVARCHAR(400))
        FROM arm.PipelineNoticeSearch n
        WHERE n.PipelineId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Pipeline pl WHERE pl.PipelineId = n.PipelineId)

        -- ---- 4) Domain checks (documented small value sets; expect 0) ---------
        UNION ALL
        SELECT 'Domain_LedgerSide', NULL, CAST(0 AS INT),
               CAST(SUM(CASE WHEN LedgerSide NOT IN ('Receipt','Delivery') THEN 1 ELSE 0 END) AS BIGINT),
               CAST(CONCAT('rows outside {Receipt,Delivery}; total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.UsImportsExportsByPointsAggregate
        WHERE FlowDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        SELECT 'Domain_PipelineFlow_FlowType', NULL, CAST(0 AS INT),
               CAST(SUM(CASE WHEN FlowType NOT IN ('Inflow','Outflow') THEN 1 ELSE 0 END) AS BIGINT),
               CAST(CONCAT('rows outside {Inflow,Outflow}; total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.PipelineFlowThroughput
        WHERE FlowDate BETWEEN @DateFrom AND @DateTo

        -- ---- 5) PointVolume active-scope sanity (NOT guaranteed by the FK) ----
        --      The PointVolume FK only asserts PointId exists in PointMetadata;
        --      the loader further scopes to ACTIVE points (usp_GetPointIds WHERE
        --      PointIsActive=1). A non-zero here means PointVolume holds a point
        --      that is not (or no longer) active in the metadata dimension. Expect 0.
        UNION ALL
        SELECT 'PointVolume_ActiveScope', NULL, CAST(0 AS INT),
               CAST(COUNT(1) AS BIGINT),
               CAST('PointVolume PointIds whose PointMetadata.PointIsActive <> 1' AS NVARCHAR(400))
        FROM arm.PointVolume pv
        WHERE pv.[Date] BETWEEN @DateFrom AND @DateTo
          AND NOT EXISTS (SELECT 1 FROM arm.PointMetadata m
                          WHERE m.PointId = pv.PointId AND m.PointIsActive = 1)
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName, Scope;
END
GO
