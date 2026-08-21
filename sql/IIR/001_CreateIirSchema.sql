-- =============================================================================
-- 001_CreateIirSchema.sql
-- Database, [arm] schema, the Endpoint/Status lookup tables, the arm.FileLog hub,
-- and the three flat data tables for the IIR (Industrial Info Resources — IDB
-- API v2.7) loader:
--   * arm.Plant         (POST /idb/{ver}/plants/summary        -> plant catalogue)
--   * arm.Unit          (POST /idb/{ver}/units/summary         -> unit catalogue)
--   * arm.OfflineEvent  (POST /idb/{ver}/offlineevents/summary -> daily outage snapshot)
-- Runs against this loader's OWN database (Loaders:IIR:ConnectionString ->
-- Database=IIR).
--
-- Design of record: docs/design/IIR.md (§7 full-field/TVP contract, §7.3 in-proc
-- geography, §8 FileLog hub, §15 open items) and docs/apis/IIR.md (per-column
-- types, nullability, PKs; §5 Plant / §6 Unit / §7 OfflineEvent).
--
-- MODEL (mirrors CWG/AGSI flat arm.* posture + FileLog hub with normalized lookups):
--   * arm.Endpoint / arm.Status are small lookup tables, each with the standing
--     surrogate Id INT IDENTITY PK + DateCreated + a UNIQUE natural Name (CWG/AGSI
--     shape). Endpoint (3: Plant, Unit, OfflineEvent) and Status (3:
--     Success/NotAvailable/Failed) are pre-seeded (MERGE, re-runnable). FileLog
--     EndpointId / StatusId FK to them by surrogate Id. arm.Endpoint additionally
--     carries a Url column (the relative summary path template) for AGSI parity.
--   * arm.FileLog is the single hub: one row per endpoint pull per Central capture
--     day (Success / NotAvailable / Failed). It KEEPS its standing surrogate Id PK
--     (documented hub exception): its natural key (EndpointId, RepresentativeDate)
--     holds a NULL-able column (RepresentativeDate) a PRIMARY KEY cannot hold, so the
--     natural key is enforced by a UNIQUE constraint whose SQL NULL-equality collapses
--     any undated row to a single stable hub row (design §8). No Region/Variant slot —
--     IIR has no sub-variant.
--
--   * arm.Plant / arm.Unit / arm.OfflineEvent are the three PRODUCTION target tables
--     supplied VERBATIM by the user (they already exist in the user's DB). They are
--     created EXACTLY as given — column names, types, nullability, defaults and PK
--     definitions unchanged; NO surrogate Id / DateCreated is prepended and NO foreign
--     keys are added (deliberate deviation from the standing Id/DateCreated convention:
--     the DDL is authoritative and must byte-match production). Each keeps only its
--     own PK: arm.Plant(PlantId), arm.Unit(UnitId), arm.OfflineEvent(RunDate,EventId).
--     PlantPoint (GEOGRAPHY) is built in the merge proc (003, §7.3) — never fed by the
--     TVP; ModifiedAtUtc is stamped by the merge proc (SYSUTCDATETIME()); the table's
--     SYSDATETIME() default only applies if a direct insert ever omits it.
--     The Plant/Unit/OfflineEvent PlantId/UnitId FK relationships are ADVISORY (design
--     §15.5) — intentionally NOT enforced, because the three pulls are independent full
--     snapshots with possibly-different scoping and a coverage gap must not fail a MERGE.
--
-- Standing conventions applied to the platform-owned objects (lookups + hub):
--   * Named constraints (PK_/FK_/UQ_/CK_/DF_); re-runnable guarded creates
--     (IF OBJECT_ID(...) IS NULL / IF NOT EXISTS with SELECT 1 probes). Lookup seeds
--     use MERGE so re-execution keeps the catalog current without duplicating rows.
--   * No TINYINT; COUNT is not used here. Minimal SELECT 1 probes inside guards.
--
--   * arm.PlantSummary / arm.UnitSummary / arm.OfflineEventSummary are the per-run
--     STEP-1 id-catalog CENSUS tables from the two-step summary->detail rework
--     (design §7.5). Each is RunDate-partitioned (PK (RunDate, <Id>)) and minimal
--     (id + STEP-1 lat/long + DB-stamped DiscoveredAtUtc/ModifiedAtUtc); NO FileLogId,
--     NO geography, NO FKs. They retain the full daily discovered-id history.
--
-- Parents precede children: Endpoint + Status (lookups) -> FileLog (hub) ->
-- Plant + Unit + OfflineEvent (verbatim data tables) -> PlantSummary + UnitSummary +
-- OfflineEventSummary (id-catalog census; independent, no FKs).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'IIR')
    CREATE DATABASE IIR;
GO

USE IIR;
GO

-- ----------------------------------------------------------------------------
-- Schema arm — CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (CWG/AGSI/Platts posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Keyed by their natural NAME (standing surrogate Id PK). FileLog
-- FKs to them by surrogate Id. Created and seeded BEFORE FileLog so its FKs valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint — the fixed catalog of the 3 IIR endpoints. Name is the loader's
-- EndpointId ('Plant' / 'Unit' / 'OfflineEvent'); Url is the endpoint's relative
-- summary-path template ({ver} kept literal, no secret) — AGSI parity, informational.
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

-- Seed / refresh the 3 endpoints (MERGE -> re-runnable; keeps Url current).
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('Plant',        'idb/{ver}/plants/summary'),
    ('Unit',         'idb/{ver}/units/summary'),
    ('OfflineEvent', 'idb/{ver}/offlineevents/summary')
) AS src ([Name], Url)
   ON tgt.[Name] = src.[Name]
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url) VALUES (src.[Name], src.Url);
GO

-- ----------------------------------------------------------------------------
-- arm.Status — the fixed set of request outcomes (AGSI shape). VARCHAR(20).
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
-- HUB TABLE. One row per endpoint pull per Central capture day. Endpoint / Status
-- are normalized to surrogate-Id FKs; usp_UpsertFileLog resolves the passed names
-- to those Ids. RepresentativeDate is NULL-able and kept inline; the UNIQUE
-- constraint relies on SQL NULL-equality so an undated request collapses to one
-- stable hub row (upserted each run). FileLog KEEPS its standing surrogate Id PK
-- because its natural key holds a NULL-able column a PK cannot contain.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId         INT           NOT NULL,               -- FK to arm.Endpoint(Id)
        RepresentativeDate DATE          NULL,                   -- Central capture day; NULL tolerated (NULL-equality collapse)
        StatusId           INT           NOT NULL,               -- FK to arm.Status(Id)
        HttpStatus         INT           NULL,
        [RowCount]         INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath        NVARCHAR(400) NOT NULL,               -- sanitized relative path/params, NO Bearer token (unique per row)
        LastCheckedUtc     DATETIME2(3)  NULL,
        ModifiedAtUtc      DATETIME2(3)  NULL,
        -- NULL-equality in a UNIQUE constraint collapses an undated row to a single
        -- hub row per (EndpointId[, RepresentativeDate]).
        CONSTRAINT UQ_FileLog_Endpoint_RepDate
            UNIQUE (EndpointId, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- DATA TABLES — created VERBATIM per the user-supplied production DDL. Each is
-- guarded with IF OBJECT_ID(...) IS NULL so a re-run against a DB that already has
-- the production table is a NO-OP (the existing table is left untouched).
-- No surrogate Id / DateCreated, no FKs — only the given PKs. PlantPoint (GEOGRAPHY)
-- and ModifiedAtUtc are populated by the 003 merge procs (never via the TVP).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Plant — plant catalogue. PK PlantId. (71 columns, docs/apis/IIR.md §5.)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Plant', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Plant(
        PlantId INT NOT NULL, PlantName VARCHAR(8000) NULL, PlantStatusDesc VARCHAR(8000) NULL,
        NoEmployees INT NULL, StartupDate DATETIME2(0) NULL, LiveDate DATETIME2(0) NULL, ReleaseDate DATETIME2(0) NULL,
        OperationsLaborPreference INT NULL, PrimaryFuel VARCHAR(8000) NULL, SecondaryFuel VARCHAR(8000) NULL,
        IndustryCode VARCHAR(8000) NULL, IndustryCodeDesc VARCHAR(8000) NULL, PrimarySicId VARCHAR(8000) NULL,
        PrimarySicDesc VARCHAR(8000) NULL, PecZone VARCHAR(8000) NULL, MarketRegionId VARCHAR(8000) NULL,
        MarketRegionName VARCHAR(8000) NULL, ConfirmationStatus VARCHAR(8000) NULL, NercRegion VARCHAR(8000) NULL,
        NercSubRegionName VARCHAR(8000) NULL, ElectricalConnectionName VARCHAR(8000) NULL, TradingRegionId INT NULL,
        TradingRegionName VARCHAR(8000) NULL, CogenChp INT NULL, Metallurgical INT NULL, Thermal INT NULL,
        Placer INT NULL, OpenPit INT NULL, Quarry INT NULL, Strip INT NULL, Auger INT NULL, Dredging INT NULL,
        Drift INT NULL, Shaft INT NULL, Slope INT NULL, Longwall INT NULL, RoomPillar INT NULL, CutFill INT NULL,
        Caving INT NULL, Stoping INT NULL, InSituSolution INT NULL, Longitude FLOAT NULL, Latitude FLOAT NULL,
        PlantPoint GEOGRAPHY NULL, WorldRegionId INT NULL, WorldRegionName VARCHAR(8000) NULL, Offshore INT NULL,
        MailingAddressLine1 VARCHAR(250) NULL, MailingCity VARCHAR(250) NULL, MailingStateName VARCHAR(250) NULL,
        MailingPostalCode VARCHAR(250) NULL, MailingCountryName VARCHAR(250) NULL, PhysicalAddressLine1 VARCHAR(250) NULL,
        PhysicalCity VARCHAR(250) NULL, PhysicalStateName VARCHAR(250) NULL, PhysicalPostalCode VARCHAR(250) NULL,
        PhysicalCountryName VARCHAR(250) NULL, PhysicalCountyName VARCHAR(250) NULL, PhoneCC VARCHAR(250) NULL,
        PhoneNumber VARCHAR(250) NULL, ParentCompanyId VARCHAR(250) NULL, ParentCompanyName VARCHAR(250) NULL,
        ParentCompanyWebsite VARCHAR(250) NULL, OperatorCompanyId VARCHAR(250) NULL, OperatorCompanyName VARCHAR(250) NULL,
        OperatorCompanyWebsite VARCHAR(250) NULL, ModifiedAtUtc DATETIME2(3) NULL DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Plant PRIMARY KEY CLUSTERED (PlantId ASC));
END
GO

-- ----------------------------------------------------------------------------
-- arm.Unit — unit catalogue. PK UnitId. (47 columns, docs/apis/IIR.md §6.)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Unit', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Unit(
        UnitId INT NOT NULL, UnitName VARCHAR(8000) NULL, PlantId INT NULL, PlantName VARCHAR(8000) NULL,
        PlantStatusDesc VARCHAR(8000) NULL, PlantAddressLine1 VARCHAR(250) NULL, PlantCity VARCHAR(250) NULL,
        PlantStateName VARCHAR(250) NULL, PlantPostalCode VARCHAR(250) NULL, PlantCountryName VARCHAR(250) NULL,
        PlantCountyName VARCHAR(250) NULL, MarketRegionId VARCHAR(8000) NULL, MarketRegionName VARCHAR(8000) NULL,
        WorldRegionId INT NULL, WorldRegionName VARCHAR(8000) NULL, TradingRegionId INT NULL, TradingRegionName VARCHAR(8000) NULL,
        UnitStatusDesc VARCHAR(8000) NULL, UnitStatusGroup VARCHAR(8000) NULL, HeaterCount INT NULL, UnitTypeId VARCHAR(8000) NULL,
        UnitTypeDesc VARCHAR(8000) NULL, UnitTypeGroup VARCHAR(8000) NULL, CapacityProductId VARCHAR(8000) NULL,
        Capacity FLOAT NULL, CapacityUom VARCHAR(8000) NULL, PrimarySicId VARCHAR(8000) NULL, PrimarySicDesc VARCHAR(8000) NULL,
        AreaId INT NULL, AreaName VARCHAR(8000) NULL, PlantLatitude FLOAT NULL, PlantLongitude FLOAT NULL, PlantPoint GEOGRAPHY NULL,
        Offshore INT NULL, IndustryCode VARCHAR(8000) NULL, IndustryCodeDesc VARCHAR(8000) NULL, Technology VARCHAR(8000) NULL,
        Renewable INT NULL, CogenChp INT NULL, PlantOperatorName VARCHAR(8000) NULL, PlantOwnerName VARCHAR(8000) NULL,
        PlantParentName VARCHAR(8000) NULL, PlantPhone VARCHAR(8000) NULL, ReleaseDate DATETIME2(0) NULL, LiveDate DATETIME2(0) NULL,
        ModifiedAtUtc DATETIME2(3) NULL DEFAULT (SYSDATETIME()), CONSTRAINT PK_Unit PRIMARY KEY CLUSTERED (UnitId ASC));
END
GO

-- ----------------------------------------------------------------------------
-- arm.OfflineEvent — daily outage snapshot. PK (RunDate, EventId). RunDate is the
-- Central run date stamped by the loader (a work-unit value, not from the API);
-- history accretes as one partition per Central day. (63 columns, docs/apis/IIR.md §7.)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.OfflineEvent', 'U') IS NULL
BEGIN
    CREATE TABLE arm.OfflineEvent(
        RunDate DATE NOT NULL, EventId INT NOT NULL, EventKind VARCHAR(8000) NULL, EventType VARCHAR(8000) NULL,
        EventCause VARCHAR(8000) NULL, EventStatusDesc VARCHAR(8000) NULL, UnitId INT NULL, UnitName VARCHAR(8000) NULL,
        UnitStatusDesc VARCHAR(8000) NULL, IndustryCode VARCHAR(8000) NULL, IndustryCodeDesc VARCHAR(8000) NULL, PlantId INT NULL,
        PlantName VARCHAR(8000) NULL, PlantParentName VARCHAR(8000) NULL, PlantOwnerName VARCHAR(8000) NULL,
        PlantOperatorName VARCHAR(8000) NULL, PlantAddressLine1 VARCHAR(8000) NULL, PlantCity VARCHAR(8000) NULL,
        PlantState VARCHAR(8000) NULL, PlantPostalCode VARCHAR(8000) NULL, PlantCountry VARCHAR(8000) NULL, PlantCounty VARCHAR(8000) NULL,
        PlantLatitude FLOAT NULL, PlantLongitude FLOAT NULL, PlantPoint GEOGRAPHY NULL, AreaId INT NULL, AreaName VARCHAR(8000) NULL,
        Offshore INT NULL, GasRegionId VARCHAR(8000) NULL, GasRegionName VARCHAR(8000) NULL, MarketRegionId VARCHAR(8000) NULL,
        MarketRegionName VARCHAR(8000) NULL, TradingRegionId INT NULL, TradingRegionName VARCHAR(8000) NULL,
        PowerTradeRegion VARCHAR(8000) NULL, WorldRegionId INT NULL, WorldRegionName VARCHAR(8000) NULL, PecZone VARCHAR(8000) NULL,
        PrimarySicId VARCHAR(8000) NULL, UnitClassification VARCHAR(8000) NULL, Derate FLOAT NULL, IsDerated INT NULL,
        ProductId INT NULL, ProductDescription VARCHAR(8000) NULL, UnitCapacity FLOAT NULL, OfflineCapacity FLOAT NULL,
        OfflineCapacityUOM VARCHAR(8000) NULL, EventStartDate DATETIME2(0) NULL, EventEndDate DATETIME2(0) NULL, EventDuration INT NULL,
        PrevStartDate DATETIME2(0) NULL, PrevEndDate DATETIME2(0) NULL, UnitTypeId VARCHAR(8000) NULL, UnitTypeDesc VARCHAR(8000) NULL,
        EventConfirmationStatus VARCHAR(8000) NULL, CogenChp INT NULL, EventDatePrecision VARCHAR(8000) NULL, KickoffSlippage INT NULL,
        EventComments VARCHAR(8000) NULL, LiveDate DATETIME2(0) NULL, ReleaseDate DATETIME2(0) NULL,
        ModifiedAtUtc DATETIME2(3) NULL DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_OfflineEventDetail PRIMARY KEY CLUSTERED (RunDate ASC, EventId ASC));
END
GO

-- =============================================================================
-- ID-CATALOG CENSUS TABLES (two-step summary->detail rework, design §7.5).
-- STEP 1 of each pipeline (the summary page) persists a per-run census of the
-- discovered ids, written by the reader as a side-write. One table per endpoint,
-- RunDate-partitioned (PK (RunDate, <Id>)) so the full daily discovered-id history
-- is retained (NO pruning). These are minimal by design — ID-ONLY: NO lat/long, NO
-- FileLogId, NO geography column, NO FKs (the fact side owns coordinates, geography
-- and provenance). DiscoveredAtUtc/ModifiedAtUtc are DB-stamped defaults (NOT in
-- the census TVPs — matching the fact-table ModifiedAtUtc posture); the summary
-- MERGE procs (003) also stamp them explicitly. Guarded IF OBJECT_ID(...) IS NULL.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.PlantSummary — STEP-1 plant id census. PK (RunDate, PlantId).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PlantSummary', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PlantSummary
    (
        RunDate         DATE         NOT NULL,
        PlantId         INT          NOT NULL,
        DiscoveredAtUtc DATETIME2(3) NULL DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_PlantSummary PRIMARY KEY CLUSTERED (RunDate ASC, PlantId ASC)
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.UnitSummary — STEP-1 unit id census. PK (RunDate, UnitId).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.UnitSummary', 'U') IS NULL
BEGIN
    CREATE TABLE arm.UnitSummary
    (
        RunDate         DATE         NOT NULL,
        UnitId          INT          NOT NULL,
        DiscoveredAtUtc DATETIME2(3) NULL DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_UnitSummary PRIMARY KEY CLUSTERED (RunDate ASC, UnitId ASC)
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.OfflineEventSummary — STEP-1 offline-event id census. PK (RunDate, EventId).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.OfflineEventSummary', 'U') IS NULL
BEGIN
    CREATE TABLE arm.OfflineEventSummary
    (
        RunDate         DATE         NOT NULL,
        EventId         INT          NOT NULL,
        DiscoveredAtUtc DATETIME2(3) NULL DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_OfflineEventSummary PRIMARY KEY CLUSTERED (RunDate ASC, EventId ASC)
    );
END
GO
