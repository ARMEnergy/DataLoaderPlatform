-- =============================================================================
-- 001_CreateNgiSchema.sql
-- Loader:   NGI  (NGI Data Services - Bidweek natural-gas price survey)
-- Database: NGI            Schema: arm            (NOT dbo - see the note below)
-- Creates:  database, [arm] schema, the Endpoint/Status lookups, the arm.FileLog
--           audit hub, and the two data tables:
--             * arm.BidWeekData     (endpoint 1  GET /bidweekDatafeed.json?issue_date=)
--             * arm.BidWeekLocation (endpoint 2  GET /bidweekLocations?format=json)
--
-- Design of record: docs/design/NGI.md  (SS6 FileLog, SS8 full-field mapping,
--                   SS9 validation, SS13 DATABASE_DEVELOPER coordination)
-- Field reference:  docs/apis/NGI.md   (SS4/SS5 per-field types, nullability,
--                   observed maxima; SS7 type recommendations)
-- Template:         sql/AGSI/001_CreateAgsiSchema.sql (structure, guards,
--                   idempotency, lookup + hub posture)
--
-- SCHEMA NOTE (deliberate override of a standing default): DATABASE_DEVELOPER's
-- default target schema is [dbo]; this loader is explicitly [arm], matching
-- Platts/CWG/AGSI/IHSPointLogic/IIR/OPIS. StormVista is the cautionary precedent
-- where [dbo] was taken by default and then had to be documented as a deviation.
-- EVERY object in this loader is arm.*.
--
-- -----------------------------------------------------------------------------
-- MODEL
-- -----------------------------------------------------------------------------
--   * arm.Endpoint / arm.Status - small seeded lookups (AGSI/CWG shape): surrogate
--     Id INT IDENTITY PK first, DateCreated second, UNIQUE natural [Name].
--     arm.Endpoint also carries the endpoint URL template. Both are seeded by a
--     re-runnable MERGE. arm.FileLog FKs to them by surrogate Id and
--     arm.usp_UpsertFileLog resolves the passed NAMES to those Ids server-side
--     (design SS6 / SS13 item 7a).
--
--   * *** NO arm.Region TABLE, NO Region AXIS ON THE HUB. ***  AGSI keys its hub
--     (EndpointId, RegionId, RepresentativeDate) because its REQUEST axis is the
--     country. NGI has no region request axis - one request returns every region -
--     and worse, an audit column named RegionId would sit next to
--     arm.BidWeekData.Region, a PAYLOAD column, meaning something completely
--     different under nearly the same name. Do not add it back (design SS6, SS13 #7).
--
--   * arm.FileLog is the single audit hub: one row per endpoint pull per run
--     (Success / NotAvailable / Failed), for EVERY request including the ~58-of-60
--     legitimate 404s. FileLogId is stamped onto the fact rows the pull produced.
--     It KEEPS its surrogate Id PK (documented hub exception) because its natural
--     key (EndpointId, RepresentativeDate) contains a NULL-able column that a
--     PRIMARY KEY cannot hold.
--
--   * arm.BidWeekData is the FACT: composite natural PRIMARY KEY
--     (IssueDate, PointCode), NO surrogate Id (repo fact shape), DateCreated
--     leading, FileLogId provenance, ModifiedAtUtc trailing.
--
--   * arm.BidWeekLocation is the name<->code crosswalk: PRIMARY KEY (PointCode),
--     NO surrogate Id - a STATED DEVIATION from the dimension convention, argued
--     in its own header block below.
--
--   * *** NO FOREIGN KEY between arm.BidWeekData and arm.BidWeekLocation. ***
--     The two pipelines are INDEPENDENT (no discovery tier, no barrier) so arrival
--     order is not guaranteed: a brand-new point code can legitimately appear in a
--     datafeed before the locations snapshot is refreshed, and the 163-code
--     equality observed on 2026-08-21 is a same-day observation, NOT a contract
--     (docs/apis/NGI.md SS5.4). An FK would fail a perfectly valid fact load.
--     Reconciled OBSERVATIONALLY and BOTH WAYS by arm.usp_ValidateLoad checks
--     FactCodesMissingFromLocation / LocationCodesMissingFromFact - warn, never
--     error (design SS8.2).
--
-- -----------------------------------------------------------------------------
-- TYPE CALLS (all from live-observed data - docs/apis/NGI.md SS4.4 / SS5.2)
-- -----------------------------------------------------------------------------
--   * [Low] / [High] / [Average] -> DECIMAL(13,6), NEVER FLOAT. A published price
--     must round-trip exactly, so FLOAT is out (this repo reserves FLOAT for
--     genuinely approximate measures such as coordinates - and this feed has none).
--     Sizing rationale:
--       - 6 decimal places: the source grain is exactly 3 dp today; 6 stores that
--         exactly and absorbs a finer grain (or a unit change) with no migration.
--       - 7 INTEGER digits (13 - 6): deliberate spike headroom. US gas cash prices
--         printed far above $200/MMBtu during Winter Storm Uri (Feb 2021), with
--         four-figure prints at some hubs. A DECIMAL(9,4)-style 5 integer digits
--         would survive that, but 7 costs nothing here (9 bytes; ~2,000 rows/year)
--         and removes the question entirely.
--     *** NO non-negative CHECK constraint on any price column: NEGATIVE GAS
--     PRICES ARE REAL (Waha has printed negative cash prices). ***
--   * [Volume] / Deals -> INT. Observed max 4 digits (8647 / 1663 on USAVG) but the
--     vendor documents NO ceiling, so SMALLINT's 32,767 is a real risk as coverage
--     grows. TINYINT is banned by repo convention.
--   * IssueDate / SurveyStart / SurveyEnd -> DATE, not DATETIME2. These are
--     calendar dates: every date in both payloads is a bare YYYY-MM-DD with NO
--     time component and no timezone anywhere in the feed. (ModifiedAtUtc stays
--     DATETIME2(3) per convention.)
--   * PointCode -> VARCHAR(20). Observed max length 12, uppercase A-Z + digits,
--     all ASCII; modest headroom.
--   * LocationName / PricingPoint -> VARCHAR(100) (observed max 33 for both);
--     Region -> VARCHAR(64) (observed max 24). VARCHAR not NVARCHAR: every
--     observed character in both live fixtures is ASCII. Revisit only if a
--     mexico* feed (accented names) is ever folded in.
--   * NO lookup table and NO CHECK constraint on Region. The 14 observed values
--     are not an enum in any vendor document - a constrained domain would fail the
--     load the day NGI adds a region (design SS13 item 5).
--
-- -----------------------------------------------------------------------------
-- NULLABILITY (design SS8.2 dagger-note, SS12.1 item 1) - READ BEFORE "FIXING"
-- -----------------------------------------------------------------------------
-- EVERY non-PK payload column is NULLable, INCLUDING the four that were never null
-- in the live sample (SurveyStart, SurveyEnd, Region, PricingPoint) and
-- LocationName. This deviates from docs/apis/NGI.md SS7.2's NOT NULL
-- recommendation, ON PURPOSE:
--   * The loader parses TOLERANTLY - an unrecognised or unparseable field degrades
--     to NULL, and only an unusable KEY drops a record.
--   * NGI publishes NO response contract at all (its own OpenAPI spec literally
--     declares "No response body" for both data endpoints), so any NGI-side field
--     rename is SILENT.
--   * Therefore a NOT NULL here would convert a silent rename into ~163 DROPPED
--     priced rows per issue. Storing four NULLs and raising a validation warning is
--     strictly better.
--   * arm.usp_ValidateLoad asserts ExpectedCount = 0 for the UnexpectedNulls check,
--     so the anomaly is LOUD without being fatal. That proc is where unexpected
--     NULLs get flagged - not the DDL.
-- IssueDate and PointCode remain NOT NULL structurally (they are the PK).
-- DateCreated / ModifiedAtUtc stay NOT NULL with defaults: they are stamped by the
-- table default / the merge proc and are never sourced from the API.
--
-- Standing conventions applied: named constraints (PK_/FK_/UQ_/CK_/DF_/IX_), no
-- TINYINT, minimal SELECT 1 probes inside EXISTS, guarded re-runnable creates
-- (IF OBJECT_ID(...) IS NULL / IF NOT EXISTS), seed MERGEs so re-execution keeps
-- the catalog current without duplicating rows.
--
-- Parents precede children: Endpoint + Status -> FileLog -> BidWeekLocation +
-- BidWeekData.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'NGI')
    CREATE DATABASE NGI;
GO

USE NGI;
GO

-- ----------------------------------------------------------------------------
-- Schema arm. CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (the CWG/AGSI/Platts posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Tiny fixed catalogs keyed by their natural NAME with a surrogate
-- Id PK; arm.FileLog FKs to them by Id and arm.usp_UpsertFileLog resolves
-- name -> Id server-side, so the C# writer keeps a name-string signature.
-- Created and seeded BEFORE arm.FileLog so its FKs are valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint - the fixed catalog of NGI's 2 in-scope data endpoints. [Name] is
-- the loader's EndpointId, matched case-insensitively against
-- NgiSettings.EnabledEndpoints and INgiPipeline.EndpointId. Url is the endpoint
-- URL template with its parameter placeholder kept literal.
-- NOTE the deliberate path-style inconsistency preserved from the vendor: the
-- datafeed carries a FORMAT EXTENSION in the path (.json) while locations selects
-- format via a QUERY parameter. Do not "normalise" them.
-- No credential appears in any NGI URL (the secret is in the POST /auth body).
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

-- Seed / refresh the 2 endpoints (MERGE -> re-runnable; keeps Url current).
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('BidWeekLocations', 'https://api.ngidata.com/bidweekLocations?format=json'),
    ('BidWeekData',      'https://api.ngidata.com/bidweekDatafeed.json?issue_date={issue_date}')
) AS src ([Name], Url)
   ON tgt.[Name] = src.[Name]
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url) VALUES (src.[Name], src.Url);
GO

-- ----------------------------------------------------------------------------
-- arm.Status - the fixed set of request outcomes (AGSI/CWG shape).
-- For NGI, NotAvailable is the EXPECTED MAJORITY outcome: Bidweek is a MONTHLY
-- feed, so over the default 60-day window roughly 58 of 60 units legitimately
-- return HTTP 404 -> NotAvailable, RowCount 0, work unit SUCCEEDS. That is normal
-- operation, not a fault (design SS5.4).
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
-- AUDIT HUB. One row per endpoint pull per run, for every outcome.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.FileLog - the audit hub (design SS6). One row per request: which endpoint,
-- which issue date, the outcome, the HTTP status, the row count and the sanitised
-- request path. This is what makes "no publication that day" AUDITABLE rather than
-- invisible, and it is the standing mitigation for the settled-zone-404 invariant
-- (design SS3.5): SELECT ... FROM arm.FileLog WHERE HttpStatus = 404 lists every
-- never-published date.
--
-- NATURAL KEY: (EndpointId, RepresentativeDate).
--   RepresentativeDate = the requested ISSUE DATE for BidWeekData, and NULL for
--   BidWeekLocations (an undated as-of-now snapshot).
--
-- *** WHY A PLAIN UNIQUE CONSTRAINT AND NOT A FILTERED UNIQUE INDEX ***
--   SQL Server's UNIQUE constraint/index treats NULLs as EQUAL for uniqueness
--   purposes (a deliberate deviation from the ANSI standard). So
--   UNIQUE (EndpointId, RepresentativeDate) already permits EXACTLY ONE NULL-dated
--   row per endpoint - which is precisely the requirement: the undated Locations
--   pull collapses to ONE stable hub row that every run upserts in place, while
--   BidWeekData gets one row per issue date.
--   A filtered unique index (WHERE RepresentativeDate IS NOT NULL) would be the
--   WRONG tool here: it would exclude the Locations row from uniqueness altogether
--   and let a fresh hub row accumulate on every single run.
--   The one cost of NULL-equality: it holds for the CONSTRAINT but NOT for a join
--   or WHERE predicate, where NULL = NULL evaluates to UNKNOWN. That is why
--   arm.usp_UpsertFileLog (003) must match the date with an explicit
--       (tgt.RepresentativeDate = src.RepresentativeDate
--        OR (tgt.RepresentativeDate IS NULL AND src.RepresentativeDate IS NULL))
--   Removing that OR-branch would silently insert a duplicate Locations hub row
--   attempt on every run and violate the constraint. Do not simplify it.
--
-- The surrogate Id PK is KEPT (documented hub exception): a PRIMARY KEY cannot
-- contain the NULL-able RepresentativeDate, and the fact tables reference the hub
-- by Id.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId         INT           NOT NULL,        -- FK -> arm.Endpoint(Id)
        RepresentativeDate DATE          NULL,            -- requested issue date; NULL for BidWeekLocations
        StatusId           INT           NOT NULL,        -- FK -> arm.Status(Id)
        HttpStatus         INT           NULL,            -- 200 / 404 / 400 / 401 / 429 / 5xx
        [RowCount]         INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath        NVARCHAR(400) NOT NULL,        -- sanitised path + issue_date/format only
        LastCheckedUtc     DATETIME2(3)  NULL,            -- freshness of the LAST pull on a stable hub row
        ModifiedAtUtc      DATETIME2(3)  NULL,
        -- NULL-equality in a UNIQUE constraint collapses the undated
        -- BidWeekLocations pull to a single stable hub row. See the block above.
        CONSTRAINT UQ_FileLog_Endpoint_RepDate
            UNIQUE (EndpointId, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- DATA TABLES.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.BidWeekLocation  (endpoint 2  GET /bidweekLocations?format=json)
-- The NGI point-code <-> location-name crosswalk. Exactly 2 payload columns -
-- the endpoint returns nothing else (docs/apis/NGI.md SS5.3): no region, no
-- state/country, NO latitude/longitude or any coordinate (so no FLOAT coordinate
-- columns and NO GEOGRAPHY column - contrast IIR's PlantPoint), no pipeline or
-- operator metadata, no active flag, no first/last-published date, no sort order
-- and no parent-aggregate linkage. Do not model columns the API cannot fill.
--
-- *** PRIMARY KEY (PointCode) - A STATED DEVIATION FROM THE DIMENSION SHAPE ***
--   The standing convention for a dimension/lookup is Id INT IDENTITY(1,1) as the
--   PK first, with the natural key carrying a UNIQUE constraint that the MERGE
--   targets. Here the natural key IS the PK and there is NO surrogate Id, because:
--     1. NOTHING references this table. There is deliberately no FK from
--        arm.BidWeekData (see the 001 header), and no other table or proc joins to
--        it at load time - so a surrogate key would be dead weight that every
--        merge must maintain and no reader would ever use.
--     2. Nothing reads it back at run time either: there is deliberately NO
--        usp_Get... reference-list proc, because this endpoint is NOT a discovery
--        tier - BidWeekData work units come from the date window alone (design
--        SS2 / SS0.1). Its only consumers are humans and the observational
--        reconciliation checks in arm.usp_ValidateLoad.
--     3. The resulting shape is much closer to an id-catalog than to a dimension:
--        a natural key plus one attribute, upserted in place from a full snapshot.
--   The convention's other halves are KEPT: DateCreated leads, ModifiedAtUtc
--   trails, FileLogId carries provenance. (The hybrid Id IDENTITY UNIQUE + PK
--   (PointCode) offered in design SS13 item 2 was declined: it adds an IDENTITY
--   nobody consumes.)
--
-- MERGE key: PointCode. The merge is UPSERT-ONLY and NEVER deletes - see
-- arm.usp_BulkMergeBidWeekLocation in 003 for why (a truncated snapshot must not
-- be able to wipe the crosswalk, and ModifiedAtUtc doubles as a last-seen marker).
--
-- Column ORDER (DateCreated, then FileLogId, PointCode, LocationName, then
-- ModifiedAtUtc) intentionally mirrors arm.BidWeekLocationTvp in 002 for the
-- middle three columns, so the TVP contract can be diffed against the table by eye.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.BidWeekLocation', 'U') IS NULL
BEGIN
    CREATE TABLE arm.BidWeekLocation
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_BidWeekLocation_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NULL,          -- provenance FK -> arm.FileLog(Id); UPDATEd on match
        PointCode     VARCHAR(20)   NOT NULL,      -- KEY. JSON object VALUE. Observed max len 12, ASCII
        LocationName  VARCHAR(100)  NULL,          -- JSON object KEY. Observed max len 33, ASCII
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_BidWeekLocation_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_BidWeekLocation PRIMARY KEY (PointCode),
        CONSTRAINT FK_BidWeekLocation_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_BidWeekLocation_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.BidWeekData  (endpoint 1  GET /bidweekDatafeed.json?issue_date=YYYY-MM-DD)
-- The Bidweek price/volume survey fact: one row per pricing point per published
-- issue. ~163 rows per monthly issue (~2,000 rows/year).
--
-- PRIMARY KEY (IssueDate, PointCode) - composite natural key, NO surrogate Id
-- (repo fact shape; user-specified key). The vendor's response `data` node is a
-- JSON OBJECT KEYED BY POINT CODE, so it structurally cannot express more than one
-- record per point per issue - this key is exact and collision-free. DateCreated
-- leads the table; ModifiedAtUtc trails.
--
-- FileLogId is PROVENANCE ONLY: it is UPDATEd on match and is NEVER part of the
-- merge key (design SS8.2).
--
-- Region and PricingPoint stay DENORMALISED INLINE on the fact - a deliberate,
-- recorded deviation from "repeated per-entity strings normalise out into a
-- dimension", for four reasons (design SS8.2):
--   (a) Region is NOT available from the locations endpoint AT ALL, so the
--       crosswalk physically cannot supply it;
--   (b) volume is tiny (~163 rows/month) - this is not a high-volume fact;
--   (c) the loader spec pins exactly two tables;
--   (d) inlining preserves the AS-PUBLISHED values if NGI ever re-labels a point.
-- Region is also NOT derivable from PointCode: the code prefix is a legacy
-- artefact that frequently disagrees (NEALEB is Midwest, MCWNIAGR is Northeast,
-- SLAFGTZ3 is Southeast). ALWAYS take Region from the field.
--
-- meta.issue_date / meta.start_date / meta.end_date are NOT persisted: they were
-- verified identical to the record fields on all 163 records. They serve as a
-- reader fallback source and a validation assertion only (design SS8.2).
--
-- IN-BAND AGGREGATE ROWS ARE PERSISTED AS ORDINARY ROWS. The feed mixes granular
-- points with regional averages (*RAVG), sub-aggregates (SEREGAVG, APPREGAVG,
-- CALSAVG) and the national average (USAVG), and there is NO flag field marking
-- them. ANY DOWNSTREAM AGGREGATION OVER THIS TABLE WILL DOUBLE-COUNT unless it
-- excludes those codes. Surfaced informationally by usp_ValidateLoad
-- (AggregateRowsPresent).
--
-- Column ORDER below (FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd,
-- Region, PricingPoint, [Low], [High], [Average], [Volume], Deals) is the
-- LOAD-BEARING 12-column TVP contract - mirrored EXACTLY by arm.BidWeekDataTvp
-- (002), the merge proc's SELECT/INSERT/UPDATE lists (003) and the C# sink's
-- BuildTable. The TVP binds BY POSITION: a silent reorder on either side corrupts
-- every loaded row without any error.
--
-- Indexes: the PK covers the merge and every window scan (it leads with
-- IssueDate). IX on FileLogId serves the provenance/hub join; IX on PointCode
-- serves the both-ways reconciliation checks and per-point time series.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.BidWeekData', 'U') IS NULL
BEGIN
    CREATE TABLE arm.BidWeekData
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_BidWeekData_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NULL,           -- provenance FK -> arm.FileLog(Id); NEVER a merge key
        IssueDate     DATE          NOT NULL,       -- KEY. "Issue Date" == meta.issue_date == requested issue_date
        PointCode     VARCHAR(20)   NOT NULL,       -- KEY. "Point Code" == the data map key (all 163 verified)
        SurveyStart   DATE          NULL,           -- "Survey Start". READ IT, NEVER COMPUTE IT: offset from the
                                                    --   issue date varies month to month (5/7/10 days observed)
        SurveyEnd     DATE          NULL,           -- "Survey End". Window length also varies (3/3/6 days)
        Region        VARCHAR(64)   NULL,           -- "Region". FREE TEXT, not an enum: no lookup, no CHECK.
                                                    --   Observed max len 24; 14 distinct values (a snapshot)
        PricingPoint  VARCHAR(100)  NULL,           -- "Pricing Point". Observed max len 33
        [Low]         DECIMAL(13,6) NULL,           -- USD/MMBtu. "None" -> NULL (45/163). SIGNED, NO CHECK
        [High]        DECIMAL(13,6) NULL,           -- may EQUAL [Low] on thin points - a zero-width range is valid
        [Average]     DECIMAL(13,6) NULL,           -- DEAL-WEIGHTED, NOT the Low/High midpoint. Never compute it
        [Volume]      INT           NULL,           -- "None" -> NULL (47/163). Unit undocumented by the vendor
        Deals         INT           NULL,           -- "None" -> NULL (47/163). Count of deals behind the price
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_BidWeekData_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_BidWeekData PRIMARY KEY (IssueDate, PointCode),
        CONSTRAINT FK_BidWeekData_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_BidWeekData_FileLogId NONCLUSTERED (FileLogId),
        INDEX IX_BidWeekData_PointCode NONCLUSTERED (PointCode)
    );
END
GO

-- =============================================================================
-- CONFIGURATION REMINDER (no secret is stored in this or any other sql/ file).
-- Before the loader can run, the platform DB needs two core.Param rows:
--     LoaderName = 'NGI', ParamName = 'Username', ParamValue = '<account e-mail>'
--     LoaderName = 'NGI', ParamName = 'Password', ParamValue = '<account password>'
-- The values above are PLACEHOLDERS - never commit a real credential to sql/, to
-- appsettings.json, to a log line or to a test fixture. SeeDbSettingsResolver
-- THROWS if either row is missing, and the module's own guard then rejects a value
-- left as the literal 'SEE_DB' sentinel. Insert them by hand (or via
-- sql/Core/004) on the platform database, not here.
-- =============================================================================
