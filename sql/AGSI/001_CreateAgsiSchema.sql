-- =============================================================================
-- 001_CreateAgsiSchema.sql
-- Database, [arm] schema, the Endpoint/Region/Status lookup tables, the
-- arm.FileLog hub, and the two flat data tables for the GIE AGSI loader:
--   * arm.GasStorageEntity  (endpoint 1 GET /api/about  — country dimension)
--   * arm.GasStorage        (endpoint 2 GET /api?country=&date= — daily fact)
-- Runs against this loader's OWN database (Loaders:AGSI:ConnectionString →
-- Database=AGSI).
--
-- Design of record: docs/design/AGSI.md (§5 FileLog, §7 full-field mapping, §11
-- open items) and docs/apis/AGSI.md (per-column types, nullability, DECIMAL
-- sizing, natural keys).
--
-- Model (mirrors CWG's flat arm.* posture + FileLog hub with normalized lookups):
--   * arm.Endpoint / arm.Status / arm.Region are small lookup tables, each with a
--     surrogate Id INT IDENTITY PK and a UNIQUE natural Name (CWG shape).
--     arm.Endpoint additionally carries the endpoint's URL template. Endpoint (2:
--     About, Storage) and Status (3: Success/NotAvailable/Failed) are pre-seeded
--     (MERGE, re-runnable). arm.Region is NOT pre-seeded — AGSI regions are the
--     country codes discovered from endpoint 1 at run time, so usp_UpsertFileLog
--     get-or-creates them (design §5). FileLog.EndpointId / StatusId / RegionId FK
--     to them by surrogate Id.
--   * arm.FileLog is the single hub: one row per request/outcome (Success /
--     NotAvailable / Failed) across both endpoints. It KEEPS its surrogate Id PK
--     (documented hub exception): its natural key (Endpoint, Region,
--     RepresentativeDate) contains NULL-able columns (Region / RepresentativeDate)
--     a PRIMARY KEY cannot hold, so the natural key is enforced by a UNIQUE
--     constraint whose SQL NULL-equality collapses the undated / no-region About
--     request to a single stable hub row (design §5). NOTE: unlike CWG's hub this
--     has NO Variant column — the task's natural key is (EndpointId, RegionId,
--     RepresentativeDate) and the AgsiFileContext (design §5) drops the Variant
--     slot (AGSI has no sub-variant).
--
--   * arm.GasStorageEntity is a country DIMENSION and KEEPS the standing surrogate
--     Id PK (Id first, DateCreated second) — its MERGE target is the UNIQUE natural
--     key Code. FileLogId is NULLable here (a seeded/hand-loaded row may predate a
--     FileLog); provenance FK + IX for the hub join.
--   * arm.GasStorage is a daily FACT and — mirroring CWG's fact posture — OMITS the
--     surrogate Id and uses the composite key (EntityId, GasDayStart) AS the PRIMARY
--     KEY (both NOT NULL; nothing references a fact by a surrogate Id). EntityId is a
--     NOT NULL FK to arm.GasStorageEntity(Id) — it REPLACES the dropped per-country
--     Name/Code/Url constants (those live on the dimension) and makes orphan facts
--     structurally impossible. FileLogId is provenance (NOT part of the PK), UPDATEd
--     on MERGE, NOT NULL (no-data writes NO fact row — only a NotAvailable hub row).
--     Date and Gas_Day are retained NON-key attributes: Date is the request gas-day
--     param (≡ gasDayStart in single-date mode); Gas_Day is the global "latest
--     available gas day" marker — identical across backfilled dates, so keying on it
--     would collapse history (API doc §2). The MERGE keys on (EntityId, GasDayStart)
--     only (design §7.2/§10).
--
-- Standing conventions applied:
--   * Lookup tables (Endpoint/Status/Region) keyed by their natural NAME — the CWG
--     exception to "Id first" (tiny fixed catalogs referenced by name via FK).
--   * arm.GasStorage OMITS the surrogate Id and PKs the composite (EntityId,
--     GasDayStart) — the CWG fact exception to the "Id first" convention. EntityId
--     FKs to arm.GasStorageEntity(Id). DateCreated + ModifiedAtUtc kept.
--   * arm.GasStorageEntity FOLLOWS the standing convention (Id IDENTITY PK first,
--     DateCreated second) because it is a dimension with a surrogate referenced by
--     the entity load path.
--   * Numeric measures are DECIMAL (never FLOAT): volume/rate/capacity families →
--     DECIMAL(18,4) (safe headroom; the eu aggregate carries the largest magnitudes);
--     percent/ratio families → DECIMAL(9,4). consumptionFull is DECIMAL(18,4) (task
--     directive; wider than the API doc's DECIMAL(9,4) because its units are ⚠
--     unconfirmed — wider tolerates a count as well as a %). status → VARCHAR(4)
--     (source is 1 char 'C'/'E'/'N'; widened to tolerate a longer future code).
--   * [Full] is bracket-escaped (reserved-ish word).
--   * Named constraints (PK_/FK_/UQ_/CK_/DF_) and re-runnable guarded creates
--     (IF OBJECT_ID(...) IS NULL / IF NOT EXISTS with SELECT 1 probes). Lookup seeds
--     use MERGE so re-execution keeps the catalog current without duplicating rows.
--
-- Parents precede children: Endpoint + Status + Region (lookups) → FileLog (hub) →
-- GasStorageEntity + GasStorage (facts).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'AGSI')
    CREATE DATABASE AGSI;
GO

USE AGSI;
GO

-- ----------------------------------------------------------------------------
-- Schema arm — CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (CWG/Platts posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Keyed by their natural NAME (surrogate Id PK). FileLog FKs to
-- them by surrogate Id. Created and seeded BEFORE FileLog so its FKs are valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint — the fixed catalog of the 2 AGSI endpoints. Name is the loader's
-- EndpointId ('About' / 'Storage'); Url is the endpoint's URL template (the
-- {country}/{date} placeholders kept literal, no secret).
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

-- Seed / refresh the 2 endpoints (MERGE → re-runnable; keeps Url current).
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('About',   'https://agsi.gie.eu/api/about'),
    ('Storage', 'https://agsi.gie.eu/api?country={country}&date={date}')
) AS src ([Name], Url)
   ON tgt.[Name] = src.[Name]
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url) VALUES (src.[Name], src.Url);
GO

-- ----------------------------------------------------------------------------
-- arm.Status — the fixed set of request outcomes (CWG shape). VARCHAR(20).
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

-- Seed the 3 statuses (MERGE → re-runnable).
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

-- ----------------------------------------------------------------------------
-- arm.Region — country codes used as the endpoint-2 axis (e.g. 'de', 'at') and
-- the FileLog Region for Storage requests. INTENTIONALLY NOT pre-seeded: the
-- authoritative code set is discovered from endpoint 1 (/api/about) at run time,
-- so arm.usp_UpsertFileLog get-or-creates a Region row on first use (design §5).
-- A NULL filename region (the About request) is a NULL RegionId in FileLog, never
-- a row here. VARCHAR(16): comfortably fits a 2-letter code plus any 'eu'/'ne'
-- aggregate axis a future scope might add.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Region', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Region
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Region_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(16) NOT NULL CONSTRAINT UQ_Region_Name UNIQUE
    );
END
GO

-- =============================================================================
-- HUB TABLE. One row per request/outcome across both endpoints. Endpoint / Region
-- / Status are normalized to surrogate-Id FKs; usp_UpsertFileLog resolves the
-- passed names to those Ids. Region / RepresentativeDate are NULL-able; the UNIQUE
-- constraint relies on SQL NULL-equality so the undated / no-region About request
-- collapses to one stable hub row (upserted each run). FileLog KEEPS its surrogate
-- Id PK because its natural key contains NULL-able columns a PK cannot hold.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId         INT           NOT NULL,               -- FK to arm.Endpoint(Id)
        RegionId           INT           NULL,                   -- FK to arm.Region(Id); NULL for the undated About request
        RepresentativeDate DATE          NULL,                   -- requested gas day (Storage); NULL for About
        StatusId           INT           NOT NULL,               -- FK to arm.Status(Id)
        HttpStatus         INT           NULL,
        [RowCount]         INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath        NVARCHAR(400) NOT NULL,               -- sanitized path/params, NO x-key (unique per row; stays inline)
        LastCheckedUtc     DATETIME2(3)  NULL,
        ModifiedAtUtc      DATETIME2(3)  NULL,
        -- NULL-equality in a UNIQUE constraint collapses the undated / no-region
        -- About row to a single hub row per (EndpointId[, RegionId][, Date]).
        CONSTRAINT UQ_FileLog_Endpoint_Region_RepDate
            UNIQUE (EndpointId, RegionId, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Region   FOREIGN KEY (RegionId)   REFERENCES arm.Region (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- DATA TABLES.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.GasStorageEntity (endpoint 1 GET /api/about) — country dimension.
--   Natural/MERGE key: UNIQUE (Code). Surrogate Id PK kept (dimension).
--   Source: data.country.code → Code; data.country.name → Name;
--           data.code → ParentCode; data.name → ParentName.
--   Many SSO entities collapse to one (Code, Name, ParentCode, ParentName) tuple
--   (Austria alone has 6) — deduped to one row per Code by the loader + the merge
--   proc. FileLogId is NULLable provenance (a row may predate a FileLog).
--   Code VARCHAR(16): sized generously per task (the 2-letter code, e.g. 'AT').
--   Name/ParentName NVARCHAR(64): country/region display names may carry non-ASCII.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.GasStorageEntity', 'U') IS NULL
BEGIN
    CREATE TABLE arm.GasStorageEntity
    (
        Id            INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_GasStorageEntity PRIMARY KEY,
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_GasStorageEntity_DateCreated DEFAULT GETDATE(),
        Code          VARCHAR(16)  NOT NULL,   -- data.country.code (endpoint-2 query key)
        [Name]        NVARCHAR(64) NOT NULL,   -- data.country.name (e.g. 'Austria')
        ParentCode    VARCHAR(16)  NOT NULL,   -- data.code (region group; 'EU' observed)
        ParentName    NVARCHAR(64) NOT NULL,   -- data.name (region group; 'Europe' observed)
        FileLogId     INT          NULL,       -- provenance FK (NULLable dimension)
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_GasStorageEntity_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_GasStorageEntity_Code UNIQUE (Code),
        CONSTRAINT FK_GasStorageEntity_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_GasStorageEntity_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.GasStorage (endpoint 2 GET /api?country=&date=) — daily fact.
--   PRIMARY KEY (EntityId, GasDayStart) — CWG fact posture (no surrogate Id).
--     EntityId = FK to arm.GasStorageEntity(Id), stamped from the resolved country;
--       it REPLACES the dropped per-country Name/Code/Url (those live on the
--       dimension) and makes an orphan fact structurally impossible.
--     GasDayStart = the gas day this fact describes (≡ the request Date in
--       single-date mode — the load-bearing invariant, design §10).
--     Date = the request gas-day param, retained as a NON-key column.
--     Do NOT key on Gas_Day: it is the global "latest available gas day" marker,
--     identical across all backfilled dates — keying on it destroys history.
--   Column ORDER (FileLogId first, EntityId second, then the 20 remaining mapped
--   business columns in the exact order below — 22 TVP columns) is the load-bearing
--   TVP/sink contract — mirrored by arm.GasStorageTvp in 002, the 003 merge proc
--   SELECT/INSERT, and the sink's BuildTable. DateCreated leads and ModifiedAtUtc
--   trails physically (table defaults, not in the TVP).
--   All measures (GasInStorage…AvailableCapacity, CoveredCapacity, Trend, [Full])
--   are NULLable (status 'E'/'N' rows may blank them). UpdatedAt NULLable. Only
--   EntityId, Date, Gas_Day, GasDayStart, GasDayEnd, Status (plus FileLogId
--   provenance) are NOT NULL.
--   No separate nonclustered index on EntityId: the PK is (EntityId, GasDayStart),
--   so it already leads with EntityId and serves EntityId lookups/joins (the FK
--   IX_GasStorage_FileLogId is kept for the hub join only).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.GasStorage', 'U') IS NULL
BEGIN
    CREATE TABLE arm.GasStorage
    (
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_GasStorage_DateCreated DEFAULT GETDATE(),
        FileLogId          INT           NOT NULL,
        EntityId           INT           NOT NULL,   -- FK to arm.GasStorageEntity(Id); key; replaces dropped Name/Code/Url
        [Date]             DATE          NOT NULL,   -- request gas-day param (retained NON-key)
        Gas_Day            DATE          NOT NULL,   -- top-level gas_day (latest-available marker; NOT key)
        UpdatedAt          DATETIME2(0)  NULL,        -- data.updatedAt 'yyyy-MM-dd HH:mm:ss' (GIE CET/CEST)
        GasDayStart        DATE          NOT NULL,   -- data.gasDayStart (= Date single-date mode); key
        GasDayEnd          DATE          NOT NULL,   -- data.gasDayEnd (= gasDayStart + 1)
        GasInStorage       DECIMAL(18,4) NULL,        -- TWh
        Consumption        DECIMAL(18,4) NULL,        -- GWh/d
        ConsumptionFull    DECIMAL(18,4) NULL,        -- % (⚠ units unconfirmed) — widened per task
        Injection          DECIMAL(18,4) NULL,        -- GWh/d
        Withdrawal         DECIMAL(18,4) NULL,        -- GWh/d
        NetWithdrawal      DECIMAL(18,4) NULL,        -- GWh/d, SIGNED (withdrawal − injection)
        WorkingGasVolume   DECIMAL(18,4) NULL,        -- TWh
        InjectionCapacity  DECIMAL(18,4) NULL,        -- GWh/d
        WithdrawalCapacity DECIMAL(18,4) NULL,        -- GWh/d
        ContractedCapacity DECIMAL(18,4) NULL,        -- TWh
        AvailableCapacity  DECIMAL(18,4) NULL,        -- TWh
        CoveredCapacity    DECIMAL(9,4)  NULL,        -- % (⚠ units unconfirmed)
        [Status]           VARCHAR(4)    NOT NULL,   -- 'C'=Confirmed, 'E'=Estimated, 'N'=No data
        Trend              DECIMAL(9,4)  NULL,        -- SIGNED ratio/%
        [Full]             DECIMAL(9,4)  NULL,        -- fill %; can slightly exceed 100
        ModifiedAtUtc      DATETIME2(3)  NOT NULL CONSTRAINT DF_GasStorage_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_GasStorage PRIMARY KEY (EntityId, GasDayStart),
        CONSTRAINT FK_GasStorage_Entity  FOREIGN KEY (EntityId)  REFERENCES arm.GasStorageEntity (Id),
        CONSTRAINT FK_GasStorage_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_GasStorage_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO
