-- =============================================================================
-- 001_CreateIHSPointLogicSchema.sql
-- Database, [arm] schema, the Endpoint/Status lookup tables, the arm.FileLog hub,
-- and the 25 flat dimension/fact tables for the IHSPointLogic (S&P Global /
-- IHS Markit PointLogic gas) loader. Runs against this loader's OWN database
-- (Loaders:IHSPointLogic:ConnectionString -> Database=IHSPointLogic).
--
-- Design of record: docs/design/IHSPointLogic.md (§2.1 endpoint list, §6 FileLog
-- hub, §7 the SINGLE full-field/TVP/PK contract for all 25 tables, §10 validator,
-- §11 open items) and docs/apis/IHSPointLogic.md (verified per-column types,
-- nullability, DECIMAL sizing, natural keys).
--
-- RUN ORDER: 001 (this) -> 002 (TVP types) -> 003 (procedures). 999 tears down.
-- IDEMPOTENT: every create is guarded (IF OBJECT_ID(...) IS NULL / IF NOT EXISTS
-- with SELECT 1 probes); lookup seeds use MERGE so re-execution keeps the catalog
-- current without duplicating rows. Safe to re-run.
--
-- MODEL (mirrors CWG/AGSI flat arm.* posture + FileLog hub with normalized lookups):
--   * arm.Endpoint / arm.Status are small lookup tables, each a surrogate
--     Id INT IDENTITY PK + a UNIQUE natural Name (CWG/AGSI shape). Endpoint (25)
--     and Status (3) are pre-seeded (MERGE, re-runnable). FileLog.EndpointId /
--     StatusId FK to them by surrogate Id. arm.Endpoint also carries RunHoursCST
--     (VARCHAR(80), design §B.1) — the per-endpoint hourly run schedule ('*' = every
--     hour, else a CSV of Central (CST) hours) added idempotently and seeded INSERT-ONLY, read
--     by arm.usp_GetEndpointSchedule (003) for the loader's hourly cadence gate.
--   * arm.FileLog is the single hub: one row per request/outcome across all 25
--     endpoints. It KEEPS its surrogate Id PK (documented hub exception): its
--     natural key (Endpoint, ParamKey, Variant, RepresentativeDate) contains
--     NULL-able columns a PRIMARY KEY cannot hold, so the natural key is enforced
--     by a UNIQUE constraint whose SQL NULL-equality collapses undated / no-param
--     rows to one stable hub row (design §6).
--     ParamKey (numeric discovery id / batch token) and Variant (param-kind /
--     batch label) are KEPT INLINE as VARCHAR columns on the hub, NOT normalized
--     to a lookup: the ParamKey slot holds an unbounded, heterogeneous set (region/
--     state/pointtype ids AND PointVolume batch tokens like '0001'/'689-738'), so a
--     get-or-create label lookup would balloon with low-value rows. Design §6/§11.7
--     explicitly permits inline VARCHAR here ("the ParamKey slot holds numeric ids /
--     batch tokens, not geography strings"). This is the one deliberate divergence
--     from CWG's get-or-create arm.Region for that slot.
--   * All 25 data tables use the CWG/AGSI FACT posture: NO surrogate Id — the
--     endpoint's natural key (§7) IS the PRIMARY KEY (all key columns NOT NULL).
--     DateCreated leads physically; FileLogId (INT NOT NULL provenance FK -> the
--     hub) is SECOND; then the business columns in the EXACT §7 order; ModifiedAtUtc
--     (DATETIME2(3) default) trails. FileLogId is NOT a key column (UPDATEd on
--     MERGE match, 003). Because FileLogId does not lead the PK, each table carries
--     an explicit IX_<Table>_FileLogId for the hub join / FK check.
--     TVP column order = FileLogId first, then the row in §7 order (002 / the 003
--     merge proc SELECT/INSERT / the C# sink BuildTable all mirror it).
--
-- DECIMAL sizing (design §11.2 / api doc): measures/volumes/flows -> DECIMAL(18,6);
-- temperatures (departurefromnormalf, normaltemperaturef) -> DECIMAL(6,2); lat/lon
-- -> DECIMAL(9,6). Ids -> INT, EXCEPT PipelineNoticeSearch.Id -> BIGINT. Bit flags
-- (IsCritical, PointIsActive) -> BIT. Notice posteddate/effectivedate/enddate ->
-- DATETIMEOFFSET(3). gasproduction.reporteddate -> DATETIME2(0) (kept at datetime
-- granularity in the PK, design §11.5). Other dates -> DATE.
--
-- FK ENFORCEMENT SUMMARY (design §11.4 resolution) --------------------------------
--   ENFORCED (hard FOREIGN KEY; parent loads first by the tier order, so no
--   spurious violation is possible):
--     * every table   FileLogId              -> arm.FileLog(Id)
--     * County       (StateId)             -> State(StateId)         [T1->T0]
--     * Facility     (PointTypeId)         -> PointType(PointTypeId) [T1->T0]
--     * Subregion    (RegionId)            -> Region(RegionId)       [T1->T0]
--     * SupplyAndDemandByRegion    (RegionId)              -> Region(RegionId)              [T2->T0]
--     * SupplyAndDemandBySubRegion (SubRegionId, RegionId) -> Subregion(SubRegionId,RegionId) [T2->T1]
--     * SupplyAndDemandBySubRegion (RegionId)              -> Region(RegionId)              [T2->T0]
--     * PointVolume    (PointId)            -> PointMetadata(PointId)  [T2->T0]
--         ^ deliberately to PointMetadata, NOT Point: the PointVolume scope
--           source is arm.usp_GetPointIds reading PointMetadata WHERE
--           PointIsActive=1 (design §11.3), a SUPERSET of Point (>40k vs ~25k),
--           so a FK to Point could be violated by an active id absent from
--           Point. Every PointVolume PointId is a subset of PointMetadata's
--           active ids, so the FK to PointMetadata cannot spuriously fail.
--
--   LOGICAL-ONLY (no hard FK; the §10 validator checks these as WARN-not-fail —
--   they either reference a parent loaded in a LATER tier, or the source is known
--   to carry ids/labels not guaranteed present in the small dimension):
--     * Point.PipelineId / PointTypeId / PointStatusId
--     * PointMetadata.PointTypeId / StateId / PipelineId / CountyId / RegionId
--       (CountyId -> County is T0->T1: the parent loads AFTER, so a hard FK is
--        structurally impossible on a first load)
--     * PipelineNoticeSearch.CategoryId / PipelineId
--     * all name-string fact references (Pipeline/PipelineName/PointName/State/
--       FromState/ToState/Region/Subregion/ProducingArea/EiaRegion strings)
-- --------------------------------------------------------------------------------
--
-- Parents precede children below: Endpoint + Status (lookups) -> FileLog (hub) ->
-- Tier-0 dimensions -> Tier-0 facts -> Tier-1 lookups -> Tier-2 facts, so every FK
-- target exists before its referencing table is created.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'IHSPointLogic')
    CREATE DATABASE IHSPointLogic;
GO

USE IHSPointLogic;
GO

-- ----------------------------------------------------------------------------
-- Schema arm — CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (CWG/AGSI posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Keyed by their natural NAME (surrogate Id PK). FileLog FKs to
-- them by surrogate Id. Created and seeded BEFORE FileLog so its FKs are valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint — the fixed catalog of the 25 endpoints. Name is the loader's
-- EndpointId (design §2.1); Url is the endpoint's relative path template under
-- https://api.connect.ihsmarkit.com/ ({id}/{date}/{ids} placeholders kept literal;
-- no secret). VARCHAR(40) fits the longest id ('UsImportsExportsByPointsAggregate',
-- 33 chars). Url VARCHAR(200).
--   RunHoursCST (VARCHAR(80), design §B.1) — the per-endpoint hourly run schedule:
--   '*' = run every hour, else a CSV of Central (CST) hours 0-23 (e.g. '6', '0,6,12,18').
--   Added idempotently by the ALTER below (COL_LENGTH guard) so an already-seeded
--   catalog gains it without a rebuild. Parse tolerance (blank/invalid -> '*') lives
--   on the C# side (design §B.2); this table only stores the raw string.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Endpoint', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Endpoint
    (
        Id          INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Endpoint PRIMARY KEY,
        DateCreated DATETIME     NOT NULL CONSTRAINT DF_Endpoint_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(40)  NOT NULL CONSTRAINT UQ_Endpoint_Name UNIQUE,
        Url         VARCHAR(200) NOT NULL
    );
END
GO

-- Per-endpoint hourly run schedule (design §B.1). Added idempotently so a previously
-- deployed arm.Endpoint gains the column without a rebuild: a fresh CREATE above adds
-- the base columns and this guarded ALTER adds RunHoursCST. VARCHAR(80) NOT NULL
-- DEFAULT '*' (= run every hour) until an endpoint is retuned by the seed below (a
-- one-time INSERT value) or later by an operator editing the row in place.
IF COL_LENGTH('arm.Endpoint', 'RunHoursCST') IS NULL
    ALTER TABLE arm.Endpoint
        ADD RunHoursCST VARCHAR(80) NOT NULL
            CONSTRAINT DF_Endpoint_RunHoursCST DEFAULT '*';
GO

-- Seed / refresh the 25 endpoints (MERGE -> re-runnable). Url keeps updating on
-- MATCH; RunHoursCST is seeded INSERT-ONLY (design §B.1) — set on first insert
-- (PointVolume '*' = hourly; the other 24 '6' = 06:00 CST) and NEVER overwritten on a
-- re-run, so an operator's retuned schedule survives re-execution.
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('Region',                            'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_region',                                    '6'),
    ('State',                             'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_state',                                     '6'),
    ('PointStatus',                       'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_pointStatus',                               '6'),
    ('PointType',                         'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_pointType',                                 '6'),
    ('PipelineNoticeCategory',            'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_pipelineNoticeCategory',                    '6'),
    ('Pipeline',                          'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_pipeline',                                  '6'),
    ('Point',                             'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_point',                                     '6'),
    ('PointMetadata',                     'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/pointmetadata_withids',                       '6'),
    ('PipelineNoticeSearch',              'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/pipelinenotice_search',                       '6'),
    ('DemandForecastRegion',             'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/demandforecast_region',                        '6'),
    ('DemandForecastUsLower48',          'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/demandforecast_uslower48',                     '6'),
    ('GasProductionProducingArea',       'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/gasproduction_producingarea',                  '6'),
    ('MarketBalancesUsLower48',          'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/marketbalances_uslower48',                     '6'),
    ('ModeledDemandRegionType',          'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/modeleddemand_region_type',                    '6'),
    ('PipelineFlowThroughput',           'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/pipelineflow_throughputs',                     '6'),
    ('UsImportsExportsByPointsAggregate','https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/us_importsexportsby_points_aggregate',         '6'),
    ('UsSampleStorageFacility',          'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/us_samplestorage_facility',                    '6'),
    ('StateFlowsThroughputAggregate',    'https://api.connect.ihsmarkit.com/cs/v1/plview/retrieve/stateflows_throughputaggregates',              '6'),
    ('SupplyAndDemand',                  'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/supplyDemand/marketsHistory',                        '6'),
    ('County',                           'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_county/{id}',                                 '6'),
    ('Facility',                         'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_facility/{id}',                               '6'),
    ('Subregion',                        'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_subregion/{id}',                              '6'),
    ('SupplyAndDemandByRegion',          'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/supplyDemand/region/{id}?reportDate={date}',         '6'),
    ('SupplyAndDemandBySubRegion',       'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/supplyDemand/region/{id}?reportDate={date}',         '6'),
    ('PointVolume',                      'https://api.connect.ihsmarkit.com/cs/v1/pointlogic/volumeHistory/point?pointIds={ids}',                 '*')
) AS src ([Name], Url, RunHoursCST)
   ON tgt.[Name] = src.[Name]
-- Url refreshes on MATCH; RunHoursCST is INSERT-ONLY (design §B.1) so it is set once
-- on first insert and never clobbered on a re-run (operator retunes survive).
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url, RunHoursCST) VALUES (src.[Name], src.Url, src.RunHoursCST);
GO

-- ----------------------------------------------------------------------------
-- arm.Status — the fixed set of request outcomes (CWG/AGSI shape). VARCHAR(20).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Status', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Status
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Status PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Status_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20) NOT NULL CONSTRAINT UQ_Status_Name UNIQUE
                                CONSTRAINT CK_Status_Name CHECK ([Name] IN ('Success','NotAvailable','Failed'))
    );
END
GO

-- Seed the 3 statuses (MERGE -> re-runnable).
MERGE arm.Status AS tgt
USING (VALUES
    ('Success'),
    ('NotAvailable'),
    ('Failed')
) AS src ([Name])
   ON tgt.[Name] = src.[Name]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name]) VALUES (src.[Name]);
GO

-- =============================================================================
-- HUB TABLE. One row per request/outcome across all 25 endpoints. Endpoint /
-- Status are normalized to surrogate-Id FKs; usp_UpsertFileLog resolves the passed
-- names to those Ids. ParamKey / Variant / RepresentativeDate are NULL-able and
-- kept inline; the UNIQUE constraint relies on SQL NULL-equality so undated /
-- no-param requests collapse to one stable hub row (upserted each run). FileLog
-- KEEPS its surrogate Id PK because its natural key holds NULL-able columns a PK
-- cannot contain (documented hub exception).
--   ParamKey slot (design §6): the discovery id ('4520'/'26105') or PointVolume
--     batch token ('0001'/'689-738'). VARCHAR(32).
--   Variant  slot: the param-kind / batch label ('State'/'PointType'/'Region'/
--     'SubRegion'/'Batch'). VARCHAR(16).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId         INT           NOT NULL,               -- FK to arm.Endpoint(Id)
        ParamKey           VARCHAR(32)   NULL,                   -- discovery id / batch token; NULL for A/B
        Variant            VARCHAR(16)   NULL,                   -- param-kind / batch label; NULL for A/B
        RepresentativeDate DATE          NULL,                   -- report/flow/capture date; NULL for the A dimensions
        StatusId           INT           NOT NULL,               -- FK to arm.Status(Id)
        HttpStatus         INT           NULL,
        [RowCount]         INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath        NVARCHAR(400) NOT NULL,               -- sanitized relative path/params, NO Basic header (unique per row)
        LastCheckedUtc     DATETIME2(3)  NULL,
        ModifiedAtUtc      DATETIME2(3)  NULL,
        -- NULL-equality in a UNIQUE constraint collapses undated / no-param rows
        -- to a single hub row per (EndpointId[, ParamKey][, Variant][, Date]).
        CONSTRAINT UQ_FileLog_Endpoint_ParamKey_Variant_RepDate
            UNIQUE (EndpointId, ParamKey, Variant, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- TIER 0 — DIMENSIONS (archetype A). Independent lookups; natural id PK.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- §5  arm.Region — PK RegionId.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Region', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Region
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_Region_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        RegionId      INT           NOT NULL,
        [Name]        NVARCHAR(128) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_Region_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Region PRIMARY KEY (RegionId),
        CONSTRAINT FK_Region_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_Region_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §6a arm.State — PK StateId.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.State', 'U') IS NULL
BEGIN
    CREATE TABLE arm.State
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_State_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        StateId       INT           NOT NULL,
        [Name]        NVARCHAR(128) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_State_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_State PRIMARY KEY (StateId),
        CONSTRAINT FK_State_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_State_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §6b arm.PointStatus — PK PointStatusId.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PointStatus', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PointStatus
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_PointStatus_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        PointStatusId INT           NOT NULL,
        [Name]        NVARCHAR(128) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_PointStatus_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PointStatus PRIMARY KEY (PointStatusId),
        CONSTRAINT FK_PointStatus_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_PointStatus_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §6c arm.PointType — PK PointTypeId.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PointType', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PointType
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_PointType_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        PointTypeId   INT           NOT NULL,
        [Name]        NVARCHAR(128) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_PointType_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PointType PRIMARY KEY (PointTypeId),
        CONSTRAINT FK_PointType_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_PointType_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §6d arm.PipelineNoticeCategory — PK PipelineNoticeCategoryId.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PipelineNoticeCategory', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PipelineNoticeCategory
    (
        DateCreated              DATETIME      NOT NULL CONSTRAINT DF_PipelineNoticeCategory_DateCreated DEFAULT GETDATE(),
        FileLogId                INT           NOT NULL,
        PipelineNoticeCategoryId INT           NOT NULL,
        [Name]                   NVARCHAR(128) NOT NULL,
        ModifiedAtUtc            DATETIME2(3)  NOT NULL CONSTRAINT DF_PipelineNoticeCategory_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PipelineNoticeCategory PRIMARY KEY (PipelineNoticeCategoryId),
        CONSTRAINT FK_PipelineNoticeCategory_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_PipelineNoticeCategory_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §6e arm.Pipeline — PK PipelineId. LegacyName nullable.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Pipeline', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Pipeline
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_Pipeline_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        PipelineId    INT           NOT NULL,
        [Name]        NVARCHAR(200) NOT NULL,
        LegacyName    NVARCHAR(200) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_Pipeline_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Pipeline PRIMARY KEY (PipelineId),
        CONSTRAINT FK_Pipeline_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_Pipeline_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §7  arm.Point (wrapper, paged) — PK PointId. PipelineId/PointTypeId/
--     PointStatusId are LOGICAL-only references (no hard FK — data gaps / no
--     guaranteed within-tier load order; see header FK summary).
--     NB: JSON name of PipelineId is 'pipelineid_0' (the _0 suffix), mapped to
--     PipelineId by the C# row factory.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Point', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Point
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_Point_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        PointId       INT           NOT NULL,
        [Name]        NVARCHAR(200) NOT NULL,
        PipelineId    INT           NULL,
        PointTypeId   INT           NULL,
        PointStatusId INT           NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_Point_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Point PRIMARY KEY (PointId),
        CONSTRAINT FK_Point_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_Point_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §12 arm.PointMetadata (flat, paged) — PK PointId. The authoritative point
--     dimension (22 business cols). PointTypeId/StateId/PipelineId/CountyId/
--     RegionId are LOGICAL-only (CountyId's parent County loads in a LATER
--     tier). PointVolume(PointId) FKs to THIS table's PK (scope source).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PointMetadata', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PointMetadata
    (
        DateCreated         DATETIME      NOT NULL CONSTRAINT DF_PointMetadata_DateCreated DEFAULT GETDATE(),
        FileLogId           INT           NOT NULL,
        PointId             INT           NOT NULL,
        PointLciId          VARCHAR(16)   NULL,
        Drn                 NVARCHAR(64)  NULL,        -- blank ' ' normalized to NULL by the loader
        PointName           NVARCHAR(200) NOT NULL,
        PointTypeId         INT           NULL,
        PointType           NVARCHAR(64)  NULL,
        StateId             INT           NULL,
        [State]             NVARCHAR(64)  NULL,
        County              NVARCHAR(128) NULL,        -- blank -> NULL
        Region              NVARCHAR(128) NULL,        -- blank -> NULL
        PipelineId          INT           NULL,
        PipelineDisplayName NVARCHAR(200) NULL,
        PointIsActive       BIT           NULL,
        DesignCapacity      DECIMAL(18,6) NULL,        -- arrives as JSON STRING '0.00000000' -> parsed
        FlowDirectionId     INT           NULL,
        FlowDirection       NVARCHAR(32)  NULL,
        DisplayName         NVARCHAR(200) NULL,
        LocProp             NVARCHAR(64)  NULL,
        PointLatitude       DECIMAL(9,6)  NULL,
        PointLongitude      DECIMAL(9,6)  NULL,
        CountyId            INT           NULL,
        RegionId            INT           NULL,
        ModifiedAtUtc       DATETIME2(3)  NOT NULL CONSTRAINT DF_PointMetadata_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PointMetadata PRIMARY KEY (PointId),
        CONSTRAINT FK_PointMetadata_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_PointMetadata_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §15 arm.PipelineNoticeSearch (flat, paged) — PK Id (BIGINT). Notice snapshot;
--     CategoryId/PipelineId LOGICAL-only. posteddate/effectivedate/enddate are
--     DATETIMEOFFSET(3).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PipelineNoticeSearch', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PipelineNoticeSearch
    (
        DateCreated   DATETIME           NOT NULL CONSTRAINT DF_PipelineNoticeSearch_DateCreated DEFAULT GETDATE(),
        FileLogId     INT                NOT NULL,
        Id            BIGINT             NOT NULL,
        Subject       NVARCHAR(400)      NULL,
        Format        VARCHAR(16)        NULL,
        PostedDate    DATETIMEOFFSET(3)  NULL,
        ExternalId    VARCHAR(32)        NULL,
        CategoryId    INT                NULL,
        IsCritical    BIT                NULL,
        ContentLink   NVARCHAR(400)      NULL,
        PipelineId    INT                NULL,
        EffectiveDate DATETIMEOFFSET(3)  NULL,
        EndDate       DATETIMEOFFSET(3)  NULL,
        ModifiedAtUtc DATETIME2(3)       NOT NULL CONSTRAINT DF_PipelineNoticeSearch_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PipelineNoticeSearch PRIMARY KEY (Id),
        CONSTRAINT FK_PipelineNoticeSearch_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_PipelineNoticeSearch_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- =============================================================================
-- TIER 0 — FACT SNAPSHOTS (archetype B). Report date participates in the PK.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- §8  arm.DemandForecastRegion — PK (ForecastDate, Date, Region, Subregion).
--     ForecastDate STAMPED = run's UTC date (payload carries no forecast date).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.DemandForecastRegion', 'U') IS NULL
BEGIN
    CREATE TABLE arm.DemandForecastRegion
    (
        DateCreated          DATETIME      NOT NULL CONSTRAINT DF_DemandForecastRegion_DateCreated DEFAULT GETDATE(),
        FileLogId            INT           NOT NULL,
        ForecastDate         DATE          NOT NULL,   -- [stamped] run's UTC date
        [Date]               DATE          NOT NULL,
        Region               NVARCHAR(128) NOT NULL,
        Subregion            NVARCHAR(128) NOT NULL,
        DepartureFromNormalF DECIMAL(6,2)  NULL,        -- temperature departure degF (signed)
        NormalTemperatureF   DECIMAL(6,2)  NULL,
        TotalConsumption     DECIMAL(18,6) NULL,
        Power                DECIMAL(18,6) NULL,
        Industrial           DECIMAL(18,6) NULL,
        ResCom               DECIMAL(18,6) NULL,
        DepartureFromNormal  DECIMAL(18,6) NULL,        -- demand departure (signed)
        ModifiedAtUtc        DATETIME2(3)  NOT NULL CONSTRAINT DF_DemandForecastRegion_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_DemandForecastRegion PRIMARY KEY (ForecastDate, [Date], Region, Subregion),
        CONSTRAINT FK_DemandForecastRegion_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_DemandForecastRegion_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §9  arm.DemandForecastUsLower48 — PK (ForecastDate, Date, Region).
--     ForecastDate STAMPED = run's UTC date. Date <- pointreadingaggregate_endingdate;
--     Region <- regionname.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.DemandForecastUsLower48', 'U') IS NULL
BEGIN
    CREATE TABLE arm.DemandForecastUsLower48
    (
        DateCreated           DATETIME      NOT NULL CONSTRAINT DF_DemandForecastUsLower48_DateCreated DEFAULT GETDATE(),
        FileLogId             INT           NOT NULL,
        ForecastDate          DATE          NOT NULL,   -- [stamped] run's UTC date
        [Date]                DATE          NOT NULL,
        Region                NVARCHAR(128) NOT NULL,
        Power                 DECIMAL(18,6) NULL,
        Industrial            DECIMAL(18,6) NULL,
        ResidentialCommercial DECIMAL(18,6) NULL,
        Subtotal              DECIMAL(18,6) NULL,
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_DemandForecastUsLower48_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_DemandForecastUsLower48 PRIMARY KEY (ForecastDate, [Date], Region),
        CONSTRAINT FK_DemandForecastUsLower48_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_DemandForecastUsLower48_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §10 arm.GasProductionProducingArea — PK (ReportedDate, ReferenceDate, Region,
--     ProducingArea, State). ReportedDate DATETIME2(0) (carries HH:mm; kept at
--     datetime granularity in the PK — design §11.5). State can be a NAME.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.GasProductionProducingArea', 'U') IS NULL
BEGIN
    CREATE TABLE arm.GasProductionProducingArea
    (
        DateCreated      DATETIME      NOT NULL CONSTRAINT DF_GasProductionProducingArea_DateCreated DEFAULT GETDATE(),
        FileLogId        INT           NOT NULL,
        ReportedDate     DATETIME2(0)  NOT NULL,   -- 'yyyy-MM-dd HH:mm'
        ReferenceDate    DATE          NOT NULL,
        Region           NVARCHAR(128) NOT NULL,
        ProducingArea    NVARCHAR(128) NOT NULL,
        [State]          NVARCHAR(64)  NOT NULL,   -- can be a NAME (e.g. 'Gulf of Mexico')
        DryFactoredValue DECIMAL(18,6) NULL,
        WellheadValue    DECIMAL(18,6) NULL,
        ModifiedAtUtc    DATETIME2(3)  NOT NULL CONSTRAINT DF_GasProductionProducingArea_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_GasProductionProducingArea PRIMARY KEY (ReportedDate, ReferenceDate, Region, ProducingArea, [State]),
        CONSTRAINT FK_GasProductionProducingArea_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_GasProductionProducingArea_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §11 arm.MarketBalancesUsLower48 — PK (TimePeriod). One wide national balance row
--     per timeperiod; 16 measures, all DECIMAL(18,6) nullable (BalancingItem signed).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.MarketBalancesUsLower48', 'U') IS NULL
BEGIN
    CREATE TABLE arm.MarketBalancesUsLower48
    (
        DateCreated           DATETIME      NOT NULL CONSTRAINT DF_MarketBalancesUsLower48_DateCreated DEFAULT GETDATE(),
        FileLogId             INT           NOT NULL,
        TimePeriod            DATE          NOT NULL,
        Wellhead              DECIMAL(18,6) NULL,
        ProductionLoss        DECIMAL(18,6) NULL,
        DryGas                DECIMAL(18,6) NULL,
        CanadaImports         DECIMAL(18,6) NULL,
        LngSendout            DECIMAL(18,6) NULL,
        TotalSupply           DECIMAL(18,6) NULL,
        Power                 DECIMAL(18,6) NULL,
        Industrial            DECIMAL(18,6) NULL,
        ResidentialCommercial DECIMAL(18,6) NULL,
        Subtotal              DECIMAL(18,6) NULL,
        MexicoExports         DECIMAL(18,6) NULL,
        LngFeedGas            DECIMAL(18,6) NULL,
        PipeLoss              DECIMAL(18,6) NULL,
        TotalDemand           DECIMAL(18,6) NULL,
        Storage               DECIMAL(18,6) NULL,
        BalancingItem         DECIMAL(18,6) NULL,        -- signed
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_MarketBalancesUsLower48_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_MarketBalancesUsLower48 PRIMARY KEY (TimePeriod),
        CONSTRAINT FK_MarketBalancesUsLower48_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_MarketBalancesUsLower48_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §13 arm.ModeledDemandRegionType — PK (ReferenceDate, PLEProductName, RegionName).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ModeledDemandRegionType', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ModeledDemandRegionType
    (
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_ModeledDemandRegionType_DateCreated DEFAULT GETDATE(),
        FileLogId      INT           NOT NULL,
        ReferenceDate  DATE          NOT NULL,
        PLEProductName NVARCHAR(128) NOT NULL,
        RegionName     NVARCHAR(128) NOT NULL,
        Volume         DECIMAL(18,6) NULL,
        ModifiedAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_ModeledDemandRegionType_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ModeledDemandRegionType PRIMARY KEY (ReferenceDate, PLEProductName, RegionName),
        CONSTRAINT FK_ModeledDemandRegionType_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_ModeledDemandRegionType_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §14 arm.PipelineFlowThroughput — PK (FlowDate, Region, Pipeline, Throughput,
--     FlowType). Throughput is a LOCATION/SEGMENT label, NOT a measure (correct in
--     the PK). reporteddate is EXCLUDED (not persisted, per task).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PipelineFlowThroughput', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PipelineFlowThroughput
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_PipelineFlowThroughput_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        FlowDate      DATE          NOT NULL,   -- 'MM/dd/yyyy'
        Region        NVARCHAR(128) NOT NULL,
        Pipeline      NVARCHAR(200) NOT NULL,
        Throughput    NVARCHAR(200) NOT NULL,   -- location/segment label
        FlowType      NVARCHAR(32)  NOT NULL,   -- 'Inflow'/'Outflow'
        Volume        DECIMAL(18,6) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_PipelineFlowThroughput_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        -- Natural key kept as the enforced PK, but NONCLUSTERED: its 1123-byte width
        -- exceeds the 900-byte CLUSTERED index limit (nonclustered limit is 1700 bytes).
        CONSTRAINT PK_PipelineFlowThroughput PRIMARY KEY NONCLUSTERED (FlowDate, Region, Pipeline, Throughput, FlowType),
        CONSTRAINT FK_PipelineFlowThroughput_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        -- Narrow time-series clustering key (non-unique) for storage/scan locality.
        INDEX IX_PipelineFlowThroughput_Cluster CLUSTERED (FlowDate),
        INDEX IX_PipelineFlowThroughput_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §16 arm.UsImportsExportsByPointsAggregate — PK (RunDate, FlowDate, PointName,
--     PipelineName, LedgerSide). PointName/PipelineName are name-string references
--     (LOGICAL-only).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.UsImportsExportsByPointsAggregate', 'U') IS NULL
BEGIN
    CREATE TABLE arm.UsImportsExportsByPointsAggregate
    (
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_UsImportsExportsByPointsAggregate_DateCreated DEFAULT GETDATE(),
        FileLogId      INT           NOT NULL,
        RunDate        DATE          NOT NULL,
        FlowDate       DATE          NOT NULL,
        PointName      NVARCHAR(200) NOT NULL,
        PipelineName   NVARCHAR(200) NOT NULL,
        LedgerSide     NVARCHAR(16)  NOT NULL,   -- 'Receipt'/'Delivery'
        Volume         DECIMAL(18,6) NULL,
        [State]        VARCHAR(8)    NULL,
        County         NVARCHAR(128) NULL,
        [Type]         NVARCHAR(64)  NULL,
        PointGroupName NVARCHAR(128) NULL,
        DistrictName   NVARCHAR(128) NULL,
        ModifiedAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_UsImportsExportsByPointsAggregate_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_UsImportsExportsByPointsAggregate PRIMARY KEY (RunDate, FlowDate, PointName, PipelineName, LedgerSide),
        CONSTRAINT FK_UsImportsExportsByPointsAggregate_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_UsImportsExportsByPointsAggregate_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §17 arm.UsSampleStorageFacility (paged) — PK (ReportDate, FlowDate, Name,
--     EiaRegion, State, FieldType). State is a 2-char code (VARCHAR(4)).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.UsSampleStorageFacility', 'U') IS NULL
BEGIN
    CREATE TABLE arm.UsSampleStorageFacility
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_UsSampleStorageFacility_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        ReportDate    DATE          NOT NULL,
        FlowDate      DATE          NOT NULL,
        [Name]        NVARCHAR(200) NOT NULL,
        EiaRegion     NVARCHAR(64)  NOT NULL,
        [State]       VARCHAR(4)    NOT NULL,   -- 2-char code
        FieldType     NVARCHAR(32)  NOT NULL,   -- <- field_type
        Volume        DECIMAL(18,6) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_UsSampleStorageFacility_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_UsSampleStorageFacility PRIMARY KEY (ReportDate, FlowDate, [Name], EiaRegion, [State], FieldType),
        CONSTRAINT FK_UsSampleStorageFacility_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_UsSampleStorageFacility_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §18 arm.StateFlowsThroughputAggregate — PK (FlowDate, Region, FromState, ToState,
--     FlowType). FromState/ToState are NAMEs.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.StateFlowsThroughputAggregate', 'U') IS NULL
BEGIN
    CREATE TABLE arm.StateFlowsThroughputAggregate
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_StateFlowsThroughputAggregate_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        FlowDate      DATE          NOT NULL,   -- 'MM/dd/yyyy'
        Region        NVARCHAR(128) NOT NULL,
        FromState     NVARCHAR(64)  NOT NULL,   -- a NAME
        ToState       NVARCHAR(64)  NOT NULL,   -- a NAME
        FlowType      NVARCHAR(32)  NOT NULL,
        Volume        DECIMAL(18,6) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_StateFlowsThroughputAggregate_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_StateFlowsThroughputAggregate PRIMARY KEY (FlowDate, Region, FromState, ToState, FlowType),
        CONSTRAINT FK_StateFlowsThroughputAggregate_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_StateFlowsThroughputAggregate_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §19 arm.SupplyAndDemand (marketsHistory, wrapper, full history) — PK (Date).
--     SEPARATE table from MarketBalancesUsLower48 (§11): same 16-measure shape but
--     a different key (Date vs TimePeriod) and a different endpoint/family — do NOT
--     union (design §7 / §11.8).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SupplyAndDemand', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SupplyAndDemand
    (
        DateCreated           DATETIME      NOT NULL CONSTRAINT DF_SupplyAndDemand_DateCreated DEFAULT GETDATE(),
        FileLogId             INT           NOT NULL,
        [Date]                DATE          NOT NULL,
        Wellhead              DECIMAL(18,6) NULL,
        ProductionLoss        DECIMAL(18,6) NULL,
        DryGas                DECIMAL(18,6) NULL,
        CanadaImports         DECIMAL(18,6) NULL,
        LngSendout            DECIMAL(18,6) NULL,
        TotalSupply           DECIMAL(18,6) NULL,
        Power                 DECIMAL(18,6) NULL,
        Industrial            DECIMAL(18,6) NULL,
        ResidentialCommercial DECIMAL(18,6) NULL,
        Subtotal              DECIMAL(18,6) NULL,
        MexicoExports         DECIMAL(18,6) NULL,
        LngFeedGas            DECIMAL(18,6) NULL,
        PipeLoss              DECIMAL(18,6) NULL,
        TotalDemand           DECIMAL(18,6) NULL,
        Storage               DECIMAL(18,6) NULL,
        BalancingItem         DECIMAL(18,6) NULL,        -- signed
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_SupplyAndDemand_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SupplyAndDemand PRIMARY KEY ([Date]),
        CONSTRAINT FK_SupplyAndDemand_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_SupplyAndDemand_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- =============================================================================
-- TIER 1 — DISCOVERY-FED LOOKUPS (archetype C). Composite (childId, parentId) PK;
-- HARD FK on the parent id -> the Tier-0 dimension (parent loads first).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- §20 arm.County — PK (CountyId, StateId); FK StateId -> State. StateId injected
--     from the URL path.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.County', 'U') IS NULL
BEGIN
    CREATE TABLE arm.County
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_County_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        CountyId      INT           NOT NULL,
        StateId       INT           NOT NULL,   -- [path] injected
        [Name]        NVARCHAR(128) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_County_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_County PRIMARY KEY (CountyId, StateId),
        CONSTRAINT FK_County_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        CONSTRAINT FK_County_State   FOREIGN KEY (StateId)   REFERENCES arm.State (StateId),
        INDEX IX_County_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §21 arm.Facility — PK (FacilityId, PointTypeId); FK PointTypeId -> PointType.
--     PointTypeId injected from the URL path (= facilitytypeid). The redundant
--     facilitytypeid string echo is DROPPED (design §11.6 / decision 5): the key
--     PointTypeId already carries that value, so no separate FacilityTypeId column.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Facility', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Facility
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_Facility_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        FacilityId    INT           NOT NULL,
        PointTypeId   INT           NOT NULL,   -- [path] injected (= facilitytypeid)
        [Name]        NVARCHAR(200) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_Facility_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Facility PRIMARY KEY (FacilityId, PointTypeId),
        CONSTRAINT FK_Facility_FileLog   FOREIGN KEY (FileLogId)   REFERENCES arm.FileLog (Id),
        CONSTRAINT FK_Facility_PointType FOREIGN KEY (PointTypeId) REFERENCES arm.PointType (PointTypeId),
        INDEX IX_Facility_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §22 arm.Subregion — PK (SubRegionId, RegionId); FK RegionId -> Region. RegionId
--     injected from the URL path. This table is ALSO the SubRegionId -> RegionId map
--     read by usp_GetSubRegionIds for the SD-by-subregion fact (§24).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Subregion', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Subregion
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_Subregion_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        SubRegionId   INT           NOT NULL,
        RegionId      INT           NOT NULL,   -- [path] injected
        [Name]        NVARCHAR(128) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_Subregion_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Subregion PRIMARY KEY (SubRegionId, RegionId),
        CONSTRAINT FK_Subregion_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        CONSTRAINT FK_Subregion_Region  FOREIGN KEY (RegionId)  REFERENCES arm.Region (RegionId),
        INDEX IX_Subregion_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- =============================================================================
-- TIER 2 — PARAMETRIZED FACTS (archetypes D, E). Injected key columns; HARD FKs
-- on the id references (parents load in earlier tiers).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- §23 arm.SupplyAndDemandByRegion (wrapper) — PK (RegionId, Date, Product); FK
--     RegionId -> Region. RegionId (path) + Date (reportDate param) injected.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SupplyAndDemandByRegion', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SupplyAndDemandByRegion
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_SupplyAndDemandByRegion_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        RegionId      INT           NOT NULL,   -- [path] injected
        [Date]        DATE          NOT NULL,   -- [reportDate param] injected
        Product       NVARCHAR(128) NOT NULL,
        VolumeMmcfd   DECIMAL(18,6) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_SupplyAndDemandByRegion_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SupplyAndDemandByRegion PRIMARY KEY (RegionId, [Date], Product),
        CONSTRAINT FK_SupplyAndDemandByRegion_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        CONSTRAINT FK_SupplyAndDemandByRegion_Region  FOREIGN KEY (RegionId)  REFERENCES arm.Region (RegionId),
        INDEX IX_SupplyAndDemandByRegion_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §24 arm.SupplyAndDemandBySubRegion (wrapper; reuses /region/{id}) — PK
--     (SubRegionId, RegionId, Date, Product); composite FK (SubRegionId, RegionId)
--     -> Subregion AND FK RegionId -> Region. SubRegionId (path) + RegionId
--     (subregion->region map) + Date (reportDate param) injected.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SupplyAndDemandBySubRegion', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SupplyAndDemandBySubRegion
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_SupplyAndDemandBySubRegion_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        SubRegionId   INT           NOT NULL,   -- [path] injected
        RegionId      INT           NOT NULL,   -- [map] injected
        [Date]        DATE          NOT NULL,   -- [reportDate param] injected
        Product       NVARCHAR(128) NOT NULL,
        VolumeMmcfd   DECIMAL(18,6) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_SupplyAndDemandBySubRegion_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SupplyAndDemandBySubRegion PRIMARY KEY (SubRegionId, RegionId, [Date], Product),
        CONSTRAINT FK_SupplyAndDemandBySubRegion_FileLog    FOREIGN KEY (FileLogId)             REFERENCES arm.FileLog (Id),
        CONSTRAINT FK_SupplyAndDemandBySubRegion_Subregion  FOREIGN KEY (SubRegionId, RegionId) REFERENCES arm.Subregion (SubRegionId, RegionId),
        CONSTRAINT FK_SupplyAndDemandBySubRegion_Region     FOREIGN KEY (RegionId)              REFERENCES arm.Region (RegionId),
        INDEX IX_SupplyAndDemandBySubRegion_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- §25 arm.PointVolume (wrapper, batched) — PK (PointId, Date); FK PointId ->
--     PointMetadata(PointId). Volume can be negative. See header FK summary for
--     why the FK targets PointMetadata (the active-scope source) rather than
--     Point.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PointVolume', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PointVolume
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_PointVolume_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        PointId       INT           NOT NULL,
        [Date]        DATE          NOT NULL,
        Volume        DECIMAL(18,6) NULL,        -- can be negative
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_PointVolume_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PointVolume PRIMARY KEY (PointId, [Date]),
        CONSTRAINT FK_PointVolume_FileLog        FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        CONSTRAINT FK_PointVolume_PointMetadata  FOREIGN KEY (PointId)   REFERENCES arm.PointMetadata (PointId),
        INDEX IX_PointVolume_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO
