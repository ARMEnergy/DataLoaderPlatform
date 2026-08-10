-- =============================================================================
-- 001_CreateStormVistaSchema.sql
-- Database, lookup / dimension / hub / fact tables, convenience views, and the
-- reference-data seeds for the StormVista WDD loader. Runs against this loader's
-- OWN database (Loaders:StormVista:ConnectionString).
--
-- NORMALIZATION REDESIGN (supersedes the earlier denormalized [sv] schema):
--   * ALL objects live in schema [dbo] (standing default). The old [sv] schema
--     is gone — drop the database (or the old sv.* objects) before re-running.
--   * dbo.FileLog is the "file/request" HUB: one row per downloaded file/request,
--     carrying the FK IDs to every dimension (Endpoint/Model/Cycle/WddType/
--     RegionSet/Status) + InitDate + audit columns. The facts REACH model/cycle/
--     type/init-date/endpoint THROUGH FileLog — those dimensions are NOT repeated
--     on the fact rows.
--   * dbo.DailyWdd and dbo.RegionalWdd carry only FileLogId (provenance) + their
--     own leaf attributes (ValidDate, Flag/Region, Value). No raw dimension
--     strings; FK IDs only.
--
-- Standing conventions applied:
--   * Every table starts with  Id INT IDENTITY(1,1) (PK)  then
--     DateCreated DATETIME NOT NULL DEFAULT GETDATE().
--   * No TINYINT: Flag/Ordinal/enum-like values are INT.
--   * Numeric measure (Value) is DECIMAL(9,4), never FLOAT.
--   * Named constraints (PK_/FK_/UQ_/CK_/DF_) throughout.
--   * Re-runnable: guarded creates (IF OBJECT_ID(...) IS NULL with SELECT 1
--     probes where a probe is used); seeds via non-deleting MERGE; views via
--     CREATE OR ALTER.
--
-- Parents precede children: lookups -> dimensions -> FileLog (hub) -> facts.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'StormVista')
    CREATE DATABASE StormVista;
GO

USE StormVista;
GO

-- =============================================================================
-- LOOKUP TABLES (small closed enumerations resolved server-side by the procs).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dbo.Endpoint — which of the two WDD endpoints produced a file.
-- Replaces FileLog's old denormalized Feed string.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.Endpoint', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Endpoint
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Endpoint PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Endpoint_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20) NOT NULL CONSTRAINT UQ_Endpoint_Name UNIQUE
                                CONSTRAINT CK_Endpoint_Name CHECK ([Name] IN ('Daily','Regional'))
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.Status — per-request outcome domain. Replaces FileLog's old Status string.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.Status', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Status
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Status PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Status_DateCreated DEFAULT GETDATE(),
        Label       VARCHAR(20) NOT NULL CONSTRAINT UQ_Status_Label UNIQUE
                                CONSTRAINT CK_Status_Label CHECK (Label IN ('Success','NotAvailable','Failed'))
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.Flag — daily WDD row classification (0=obs, 1=fcst, 2=norm). The daily
-- fact references FlagId; the TVP/loader still speak the numeric FlagCode.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.Flag', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Flag
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Flag PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Flag_DateCreated DEFAULT GETDATE(),
        FlagCode    INT         NOT NULL CONSTRAINT UQ_Flag_FlagCode UNIQUE
                                CONSTRAINT CK_Flag_FlagCode CHECK (FlagCode IN (0,1,2)),
        Label       VARCHAR(10) NOT NULL CONSTRAINT UQ_Flag_Label UNIQUE
    );
END
GO

-- =============================================================================
-- DIMENSION TABLES (seeded; source of truth for enumeration + FileLog FKs).
-- Kept from the earlier design, moved sv -> dbo, otherwise unchanged.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dbo.Model — one row per Wx model slug. The daily models also serve regional
-- WDD data, so they are both daily and regional (SupportsDaily=1,SupportsRegional=1);
-- the weekly models are regional-only (0,1). The daily and regional sets therefore
-- overlap (they are NOT disjoint). IsExperimental flags AI/MLR.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.Model', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Model
    (
        Id               INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_Model PRIMARY KEY,
        DateCreated      DATETIME      NOT NULL CONSTRAINT DF_Model_DateCreated DEFAULT GETDATE(),
        ModelSlug        VARCHAR(40)   NOT NULL CONSTRAINT UQ_Model_ModelSlug UNIQUE,
        DisplayName      NVARCHAR(100) NULL,
        SupportsDaily    BIT           NOT NULL,
        SupportsRegional BIT           NOT NULL,
        IsExperimental   BIT           NOT NULL CONSTRAINT DF_Model_IsExperimental DEFAULT 0,
        ModifiedAtUtc    DATETIME2(3)  NOT NULL CONSTRAINT DF_Model_ModifiedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.Cycle — model init cycles. Per-feed applicability is data-driven:
-- daily uses all four; regional only 00/12.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.Cycle', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Cycle
    (
        Id               INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Cycle PRIMARY KEY,
        DateCreated      DATETIME     NOT NULL CONSTRAINT DF_Cycle_DateCreated DEFAULT GETDATE(),
        CycleCode        CHAR(2)      NOT NULL CONSTRAINT UQ_Cycle_CycleCode UNIQUE
                                      CONSTRAINT CK_Cycle_CycleCode CHECK (CycleCode IN ('00','06','12','18')),
        SupportsDaily    BIT          NOT NULL,
        SupportsRegional BIT          NOT NULL,
        ModifiedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_Cycle_ModifiedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.WddType — weighted-degree-day type slugs, with weighting + metric.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.WddType', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WddType
    (
        Id            INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_WddType PRIMARY KEY,
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_WddType_DateCreated DEFAULT GETDATE(),
        TypeSlug      VARCHAR(10)  NOT NULL CONSTRAINT UQ_WddType_TypeSlug UNIQUE
                                   CONSTRAINT CK_WddType_TypeSlug CHECK (TypeSlug IN ('ew_cdd','gw_hdd','pw_cdd','pw_hdd')),
        Weighting     VARCHAR(12)  NOT NULL CONSTRAINT CK_WddType_Weighting CHECK (Weighting IN ('energy','gas','population')),
        Metric        CHAR(3)      NOT NULL CONSTRAINT CK_WddType_Metric CHECK (Metric IN ('CDD','HDD')),
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_WddType_ModifiedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.RegionSet — region-set codes. Code is a STRING ('3','5','9','iso'),
-- because 'iso' is non-numeric. Kind separates EIA from ISO/RTO sets.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.RegionSet', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RegionSet
    (
        Id            INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegionSet PRIMARY KEY,
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_RegionSet_DateCreated DEFAULT GETDATE(),
        RegionSetCode VARCHAR(4)    NOT NULL CONSTRAINT UQ_RegionSet_RegionSetCode UNIQUE
                                    CONSTRAINT CK_RegionSet_RegionSetCode CHECK (RegionSetCode IN ('3','5','9','iso')),
        Kind          VARCHAR(3)    NOT NULL CONSTRAINT CK_RegionSet_Kind CHECK (Kind IN ('EIA','ISO')),
        [Description] NVARCHAR(200) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_RegionSet_ModifiedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.RegionSetWddType — bridge encoding which WDD types apply to which region
-- set (EIA sets -> ew_cdd/gw_hdd/pw_cdd; iso -> pw_cdd/pw_hdd). Drives regional
-- enumeration. Keeps its natural-key FKs to the dimension UNIQUE keys.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.RegionSetWddType', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RegionSetWddType
    (
        Id            INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegionSetWddType PRIMARY KEY,
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_RegionSetWddType_DateCreated DEFAULT GETDATE(),
        RegionSetCode VARCHAR(4)   NOT NULL,
        TypeSlug      VARCHAR(10)  NOT NULL,
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_RegionSetWddType_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_RegionSetWddType_RegionSetCode_TypeSlug UNIQUE (RegionSetCode, TypeSlug),
        CONSTRAINT FK_RegionSetWddType_RegionSet FOREIGN KEY (RegionSetCode)
            REFERENCES dbo.RegionSet (RegionSetCode),
        CONSTRAINT FK_RegionSetWddType_WddType FOREIGN KEY (TypeSlug)
            REFERENCES dbo.WddType (TypeSlug)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.Region — ordered region names within each set. Names are scoped to their
-- set (East in reg3 != East in reg5), so the natural key is
-- (RegionSetCode, RegionName). Ordinal preserves the wide-CSV column order.
-- The regional merge proc resolves RegionId from (parent FileLog's set, name).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.Region', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Region
    (
        Id            INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_Region_DateCreated DEFAULT GETDATE(),
        RegionSetCode VARCHAR(4)   NOT NULL,
        RegionName    VARCHAR(30)  NOT NULL,
        Ordinal       INT          NOT NULL,   -- INT, not TINYINT (standing convention)
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Region_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Region_RegionSetCode_RegionName UNIQUE (RegionSetCode, RegionName),
        CONSTRAINT UQ_Region_RegionSetCode_Ordinal    UNIQUE (RegionSetCode, Ordinal),
        CONSTRAINT FK_Region_RegionSet FOREIGN KEY (RegionSetCode)
            REFERENCES dbo.RegionSet (RegionSetCode)
    );
END
GO

-- =============================================================================
-- HUB TABLE. One row per downloaded file/request (ALL outcomes: success, 404,
-- failure). Carries the FK IDs to every dimension + InitDate + audit columns.
-- RegionSetId is NULL for daily files. The facts hang off FileLogId.
-- =============================================================================
IF OBJECT_ID('dbo.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileLog
    (
        Id             INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId     INT           NOT NULL,
        ModelId        INT           NOT NULL,
        CycleId        INT           NOT NULL,
        WddTypeId      INT           NOT NULL,
        RegionSetId    INT           NULL,              -- NULL for daily
        InitDate       DATE          NOT NULL,
        StatusId       INT           NOT NULL,
        HttpStatus     INT           NULL,
        [RowCount]     INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath    NVARCHAR(400) NOT NULL,          -- sanitized path, NO apikey
        LastCheckedUtc DATETIME2(3)  NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL,
        -- One row per file. RegionSetId is NULL for daily; SQL Server treats NULL
        -- as equal for a UNIQUE constraint, so daily rows collapse to one row per
        -- (Endpoint, Model, Cycle, WddType, InitDate). Regional rows differ on
        -- the non-null RegionSetId.
        CONSTRAINT UQ_FileLog_File UNIQUE (EndpointId, ModelId, CycleId, WddTypeId, RegionSetId, InitDate),
        CONSTRAINT FK_FileLog_Endpoint  FOREIGN KEY (EndpointId)  REFERENCES dbo.Endpoint  (Id),
        CONSTRAINT FK_FileLog_Model     FOREIGN KEY (ModelId)     REFERENCES dbo.Model     (Id),
        CONSTRAINT FK_FileLog_Cycle     FOREIGN KEY (CycleId)     REFERENCES dbo.Cycle     (Id),
        CONSTRAINT FK_FileLog_WddType   FOREIGN KEY (WddTypeId)   REFERENCES dbo.WddType   (Id),
        CONSTRAINT FK_FileLog_RegionSet FOREIGN KEY (RegionSetId) REFERENCES dbo.RegionSet (Id),
        CONSTRAINT FK_FileLog_Status    FOREIGN KEY (StatusId)    REFERENCES dbo.Status    (Id)
    );
END
GO

-- =============================================================================
-- FACT TABLES. Idempotent MERGE (003) upserts on the UNIQUE natural key
-- (FileLogId + leaf key). FKs to FileLog give provenance; the leaf FKs (Flag,
-- Region) enforce the resolved lookups.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dbo.DailyWdd — national daily WDD. Reaches Model/Cycle/WddType/InitDate/
-- Endpoint through FileLog; carries only ValidDate, FlagId and Value.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.DailyWdd', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DailyWdd
    (
        Id            INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_DailyWdd PRIMARY KEY,
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_DailyWdd_DateCreated DEFAULT GETDATE(),
        FileLogId     INT          NOT NULL,
        ValidDate     DATE         NOT NULL,
        FlagId        INT          NOT NULL,
        [Value]       DECIMAL(9,4) NULL,
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_DailyWdd_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_DailyWdd_NaturalKey UNIQUE (FileLogId, ValidDate),
        CONSTRAINT FK_DailyWdd_FileLog FOREIGN KEY (FileLogId) REFERENCES dbo.FileLog (Id),
        CONSTRAINT FK_DailyWdd_Flag    FOREIGN KEY (FlagId)    REFERENCES dbo.Flag    (Id)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.RegionalWdd — weekly/regional WDD, unpivoted wide->long. Forecast-only:
-- NO Flag. Reaches WkModel/Cycle/WddType/RegionSet/InitDate through FileLog;
-- carries only RegionId, ValidDate and Value.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.RegionalWdd', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RegionalWdd
    (
        Id            INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegionalWdd PRIMARY KEY,
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_RegionalWdd_DateCreated DEFAULT GETDATE(),
        FileLogId     INT          NOT NULL,
        RegionId      INT          NOT NULL,
        ValidDate     DATE         NOT NULL,
        [Value]       DECIMAL(9,4) NULL,
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_RegionalWdd_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_RegionalWdd_NaturalKey UNIQUE (FileLogId, RegionId, ValidDate),
        CONSTRAINT FK_RegionalWdd_FileLog FOREIGN KEY (FileLogId) REFERENCES dbo.FileLog (Id),
        CONSTRAINT FK_RegionalWdd_Region  FOREIGN KEY (RegionId)  REFERENCES dbo.Region  (Id)
    );
END
GO

-- ----------------------------------------------------------------------------
-- NOTE on the "index on FileLogId" requirement: the UNIQUE constraints
-- UQ_DailyWdd_NaturalKey (FileLogId, ValidDate) and
-- UQ_RegionalWdd_NaturalKey (FileLogId, RegionId, ValidDate) each create a
-- composite index LEADING with FileLogId. That index already serves every
-- FileLogId lookup / FK-check / FileLog->facts join, so a separate single-column
-- index on FileLogId would be strictly redundant (extra write + storage cost)
-- and is deliberately not created. Add one only if a profiling need appears.
-- ----------------------------------------------------------------------------

-- =============================================================================
-- CONVENIENCE VIEWS. The old PERSISTED InitDatetimeUtc computed columns are gone
-- (InitDate now lives on FileLog). These read-only views rebuild the denormalized
-- shape and expose InitDatetimeUtc = InitDate + Cycle hours by joining FileLog +
-- the dimensions. CREATE OR ALTER so they are re-runnable. Optional convenience.
-- =============================================================================
GO
CREATE OR ALTER VIEW dbo.vw_DailyWdd
AS
    SELECT
        d.Id,
        d.DateCreated,
        d.FileLogId,
        m.ModelSlug                                                                    AS Model,
        fl.InitDate,
        c.CycleCode                                                                    AS Cycle,
        w.TypeSlug                                                                     AS WddType,
        d.ValidDate,
        f.FlagCode                                                                     AS Flag,
        f.Label                                                                        AS FlagLabel,
        d.[Value],
        DATEADD(HOUR, CONVERT(INT, c.CycleCode), CONVERT(DATETIME2(0), fl.InitDate))   AS InitDatetimeUtc,
        d.ModifiedAtUtc
    FROM dbo.DailyWdd  AS d
    JOIN dbo.FileLog   AS fl ON fl.Id = d.FileLogId
    JOIN dbo.Model     AS m  ON m.Id  = fl.ModelId
    JOIN dbo.Cycle     AS c  ON c.Id  = fl.CycleId
    JOIN dbo.WddType   AS w  ON w.Id  = fl.WddTypeId
    JOIN dbo.Flag      AS f  ON f.Id  = d.FlagId;
GO

CREATE OR ALTER VIEW dbo.vw_RegionalWdd
AS
    SELECT
        r.Id,
        r.DateCreated,
        r.FileLogId,
        m.ModelSlug                                                                    AS WkModel,
        fl.InitDate,
        c.CycleCode                                                                    AS Cycle,
        w.TypeSlug                                                                     AS WddType,
        rs.RegionSetCode,
        rg.RegionName,
        rg.Ordinal,
        r.ValidDate,
        r.[Value],
        DATEADD(HOUR, CONVERT(INT, c.CycleCode), CONVERT(DATETIME2(0), fl.InitDate))   AS InitDatetimeUtc,
        r.ModifiedAtUtc
    FROM dbo.RegionalWdd AS r
    JOIN dbo.FileLog     AS fl ON fl.Id = r.FileLogId
    JOIN dbo.Model       AS m  ON m.Id  = fl.ModelId
    JOIN dbo.Cycle       AS c  ON c.Id  = fl.CycleId
    JOIN dbo.WddType     AS w  ON w.Id  = fl.WddTypeId
    JOIN dbo.RegionSet   AS rs ON rs.Id = fl.RegionSetId
    JOIN dbo.Region      AS rg ON rg.Id = r.RegionId;
GO

-- =============================================================================
-- SEEDS. MERGE (INSERT + UPDATE, never DELETE) so a re-run inserts new rows and
-- re-syncs changed attributes without disturbing anything else. Parents first.
-- =============================================================================

-- ---- dbo.Endpoint : 2 ----
MERGE dbo.Endpoint AS tgt
USING (VALUES ('Daily'), ('Regional')) AS src ([Name])
   ON tgt.[Name] = src.[Name]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name]) VALUES (src.[Name]);
GO

-- ---- dbo.Status : 3 ----
MERGE dbo.Status AS tgt
USING (VALUES ('Success'), ('NotAvailable'), ('Failed')) AS src (Label)
   ON tgt.Label = src.Label
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Label) VALUES (src.Label);
GO

-- ---- dbo.Flag : 3 (0=obs, 1=fcst, 2=norm) ----
MERGE dbo.Flag AS tgt
USING (VALUES (0, 'obs'), (1, 'fcst'), (2, 'norm')) AS src (FlagCode, Label)
   ON tgt.FlagCode = src.FlagCode
WHEN MATCHED THEN UPDATE SET
    Label = src.Label
WHEN NOT MATCHED BY TARGET THEN
    INSERT (FlagCode, Label) VALUES (src.FlagCode, src.Label);
GO

-- ---- dbo.Model : 17 daily+regional (SupportsDaily=1,SupportsRegional=1) + 6 weekly regional-only (0,1) = 23 ----
-- ---- All 23 models support regional; the daily models additionally support daily (sets overlap, not disjoint). ----
MERGE dbo.Model AS tgt
USING (VALUES
    -- Daily models (operational; also serve regional WDD -> SupportsRegional=1)
    ('gfs',                          N'GFS',                                   1, 1, 0),
    ('gfs-ens',                      N'GFS Ensemble',                          1, 1, 0),
    ('ecmwf',                        N'ECMWF',                                 1, 1, 0),
    ('ecmwf-eps',                    N'ECMWF Ensemble (EPS)',                  1, 1, 0),
    ('gfs-ens-bc',                   N'GFS Ensemble (Bias-Corrected)',         1, 1, 0),
    ('cmc-ens',                      N'CMC Ensemble',                          1, 1, 0),
    -- Daily models (experimental: MLR + AI; also serve regional WDD -> SupportsRegional=1)
    ('mlr15',                        N'MLR 15-day',                            1, 1, 1),
    ('mlr30',                        N'MLR 30-day',                            1, 1, 1),
    ('mlr45',                        N'MLR 45-day',                            1, 1, 1),
    ('ai-fourcastnetv2-gfs-ens',     N'AI FourCastNetv2 (GFS-ENS)',            1, 1, 1),
    ('ai-fourcastnetv2-ecmwf-eps',   N'AI FourCastNetv2 (ECMWF-EPS)',          1, 1, 1),
    ('ai-graphcast-gdas-ecmwf-eps',  N'AI GraphCast (GDAS/ECMWF-EPS)',         1, 1, 1),
    ('aifs',                         N'ECMWF AIFS',                            1, 1, 1),
    ('aifs-ens',                     N'ECMWF AIFS Ensemble',                   1, 1, 1),
    ('ai-gfs',                       N'AI GFS',                                1, 1, 1),
    ('ai-gfs-ens',                   N'AI GFS Ensemble',                       1, 1, 1),
    ('ai-weathernext2',              N'AI WeatherNext 2',                      1, 1, 1),
    -- Weekly / regional models (operational)
    ('cfs-weekly',                   N'CFS Weekly',                            0, 1, 0),
    ('gfs-ens-weekly',               N'GFS Ensemble Weekly',                   0, 1, 0),
    ('ecmwf-weekly',                 N'ECMWF Weekly',                          0, 1, 0),
    -- Weekly / regional models (experimental: AI)
    ('ai-fourcastnetv2-gfs-ens-weekly',    N'AI FourCastNetv2 Weekly (GFS-ENS)',     0, 1, 1),
    ('ai-fourcastnetv2-ecmwf-eps-weekly',  N'AI FourCastNetv2 Weekly (ECMWF-EPS)',   0, 1, 1),
    ('ai-graphcast-gdas-ecmwf-eps-weekly', N'AI GraphCast Weekly (GDAS/ECMWF-EPS)',  0, 1, 1)
) AS src (ModelSlug, DisplayName, SupportsDaily, SupportsRegional, IsExperimental)
   ON tgt.ModelSlug = src.ModelSlug
WHEN MATCHED THEN UPDATE SET
    DisplayName      = src.DisplayName,
    SupportsDaily    = src.SupportsDaily,
    SupportsRegional = src.SupportsRegional,
    IsExperimental   = src.IsExperimental,
    ModifiedAtUtc    = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ModelSlug, DisplayName, SupportsDaily, SupportsRegional, IsExperimental)
    VALUES (src.ModelSlug, src.DisplayName, src.SupportsDaily, src.SupportsRegional, src.IsExperimental);
GO

-- ---- dbo.Cycle : daily 00/06/12/18; regional 00/12 only = 4 ----
MERGE dbo.Cycle AS tgt
USING (VALUES
    ('00', 1, 1),
    ('06', 1, 0),
    ('12', 1, 1),
    ('18', 1, 0)
) AS src (CycleCode, SupportsDaily, SupportsRegional)
   ON tgt.CycleCode = src.CycleCode
WHEN MATCHED THEN UPDATE SET
    SupportsDaily    = src.SupportsDaily,
    SupportsRegional = src.SupportsRegional,
    ModifiedAtUtc    = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (CycleCode, SupportsDaily, SupportsRegional)
    VALUES (src.CycleCode, src.SupportsDaily, src.SupportsRegional);
GO

-- ---- dbo.WddType : 4 ----
MERGE dbo.WddType AS tgt
USING (VALUES
    ('ew_cdd', 'energy',     'CDD'),
    ('gw_hdd', 'gas',        'HDD'),
    ('pw_cdd', 'population', 'CDD'),
    ('pw_hdd', 'population', 'HDD')
) AS src (TypeSlug, Weighting, Metric)
   ON tgt.TypeSlug = src.TypeSlug
WHEN MATCHED THEN UPDATE SET
    Weighting     = src.Weighting,
    Metric        = src.Metric,
    ModifiedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (TypeSlug, Weighting, Metric)
    VALUES (src.TypeSlug, src.Weighting, src.Metric);
GO

-- ---- dbo.RegionSet : 4 ----
MERGE dbo.RegionSet AS tgt
USING (VALUES
    ('3',   'EIA', N'Gas-market 3-region (West / East / Producing)'),
    ('5',   'EIA', N'EIA 5-region'),
    ('9',   'EIA', N'Census 9-division'),
    ('iso', 'ISO', N'ISO / RTO regions')
) AS src (RegionSetCode, Kind, [Description])
   ON tgt.RegionSetCode = src.RegionSetCode
WHEN MATCHED THEN UPDATE SET
    Kind          = src.Kind,
    [Description] = src.[Description],
    ModifiedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (RegionSetCode, Kind, [Description])
    VALUES (src.RegionSetCode, src.Kind, src.[Description]);
GO

-- ---- dbo.RegionSetWddType : EIA {3,5,9}x{ew_cdd,gw_hdd,pw_cdd} + iso x {pw_cdd,pw_hdd} = 11 ----
MERGE dbo.RegionSetWddType AS tgt
USING (VALUES
    ('3',   'ew_cdd'), ('3',   'gw_hdd'), ('3',   'pw_cdd'),
    ('5',   'ew_cdd'), ('5',   'gw_hdd'), ('5',   'pw_cdd'),
    ('9',   'ew_cdd'), ('9',   'gw_hdd'), ('9',   'pw_cdd'),
    ('iso', 'pw_cdd'), ('iso', 'pw_hdd')
) AS src (RegionSetCode, TypeSlug)
   ON tgt.RegionSetCode = src.RegionSetCode AND tgt.TypeSlug = src.TypeSlug
WHEN NOT MATCHED BY TARGET THEN
    INSERT (RegionSetCode, TypeSlug)
    VALUES (src.RegionSetCode, src.TypeSlug);
GO

-- ---- dbo.Region : ordered region names per set (reg3=3, reg5=5, reg9=9, regiso=21) = 38 ----
MERGE dbo.Region AS tgt
USING (VALUES
    -- reg3 (gas-market)
    ('3',   'West',            1),
    ('3',   'East',            2),
    ('3',   'Producing',       3),
    -- reg5 (EIA-5)
    ('5',   'Mountain',        1),
    ('5',   'East',            2),
    ('5',   'Midwest',         3),
    ('5',   'South Central',   4),
    ('5',   'Pacific',         5),
    -- reg9 (census divisions)
    ('9',   'Mountain',        1),
    ('9',   'W S Central',     2),
    ('9',   'South Atlantic',  3),
    ('9',   'W N Central',     4),
    ('9',   'New England',     5),
    ('9',   'Pacific',         6),
    ('9',   'Middle Atlantic', 7),
    ('9',   'E N Central',     8),
    ('9',   'E S Central',     9),
    -- regiso (ISO / RTO codes)
    ('iso', 'bpa',              1),
    ('iso', 'miso',             2),
    ('iso', 'nyiso',            3),
    ('iso', 'west-pjm',         4),
    ('iso', 'ercot',            5),
    ('iso', 'spp',              6),
    ('iso', 'caiso',            7),
    ('iso', 'aeso',             8),
    ('iso', 'ieso',             9),
    ('iso', 'upmiso',          10),
    ('iso', 'nepool',          11),
    ('iso', 'wecc',            12),
    ('iso', 'serc',            13),
    ('iso', 'pjm',             14),
    ('iso', 'east-pjm',        15),
    ('iso', 'tva',             16),
    ('iso', 'quebec',          17),
    ('iso', 'lowmiso',         18),
    ('iso', 'southwest',       19),
    ('iso', 'caisonorth',      20),
    ('iso', 'caisosouth',      21)
) AS src (RegionSetCode, RegionName, Ordinal)
   ON tgt.RegionSetCode = src.RegionSetCode AND tgt.RegionName = src.RegionName
WHEN MATCHED THEN UPDATE SET
    Ordinal       = src.Ordinal,
    ModifiedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (RegionSetCode, RegionName, Ordinal)
    VALUES (src.RegionSetCode, src.RegionName, src.Ordinal);
GO
