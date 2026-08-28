-- =============================================================================
-- 001_CreateEvolutionMarketsSchema.sql
-- Loader:   EvolutionMarkets  (EVO DataPipeline API - market-data history)
-- Database: EvolutionMarkets      Schema: arm            (NOT dbo - see the note below)
-- Creates:  database, [arm] schema, the Endpoint/Status lookups, the arm.FileLog
--           audit hub, and the single data table:
--             * arm.MarketData   (GET /v1/market-data/history?dateFrom=&dateTo=)
--
-- Design of record: docs/design/EvolutionMarkets.md  (SS6 FileLog, SS8 full-field
--                   mapping, SS8.4 Checksum, SS9 validation)
-- Field reference:  docs/apis/EvolutionMarkets.md    (SS4 per-field types,
--                   nullability, observed maxima and the field-vocabulary trap)
-- Template:         sql/NGI/001_CreateNgiSchema.sql (structure, guards, idempotency,
--                   lookup + hub posture)
--
-- SCHEMA NOTE (deliberate override of a standing default): DATABASE_DEVELOPER's
-- default target schema is [dbo]; this loader is explicitly [arm], matching
-- Platts/CWG/AGSI/IHSPointLogic/IIR/OPIS/NGI/ModernCommodities. StormVista is the
-- cautionary precedent where [dbo] was taken by default and then had to be documented
-- as a deviation. EVERY object in this loader is arm.*.
--
-- -----------------------------------------------------------------------------
-- MODEL
-- -----------------------------------------------------------------------------
--   * arm.Endpoint / arm.Status - small seeded lookups (NGI/AGSI/CWG shape):
--     surrogate Id INT IDENTITY PK first, DateCreated second, UNIQUE natural [Name].
--     arm.Endpoint also carries the endpoint URL template. Both are seeded by a
--     re-runnable MERGE. arm.FileLog FKs to them by surrogate Id and
--     arm.usp_UpsertFileLog resolves the passed NAMES to those Ids server-side, so
--     the C# writer keeps a name-string signature.
--
--   * arm.FileLog is the single audit hub: one row per business date pulled, for
--     EVERY outcome (Success / NotAvailable / Failed). FileLogId is stamped onto the
--     fact rows that pull produced.
--
--     *** WHY THE HUB IS LOAD-BEARING FOR THIS LOADER IN PARTICULAR. ***
--     A non-publishing day returns HTTP 200 with an EMPTY ARRAY - not a 404, not an
--     error. So "that date published nothing" and "we never asked about that date"
--     are indistinguishable in arm.MarketData: both are simply an absence of rows.
--     The hub is the ONLY place the distinction is recorded. Any question of the form
--     "is 2026-08-20 missing because the vendor skipped it, or because our load
--     broke?" is answered by
--         SELECT * FROM arm.FileLog WHERE RepresentativeDate = '2026-08-20';
--     A [RowCount] of 0 with Status NotAvailable means the vendor published nothing.
--     No hub row at all means the loader never got there.
--
--   * *** NO arm.Instrument / arm.Dataset TABLE, AND NO REGION AXIS ON THE HUB. ***
--     - No region axis: AGSI keys its hub (EndpointId, RegionId, RepresentativeDate)
--       because its REQUEST axis is the country. This loader's only request axis is
--       the DATE - one request returns every instrument, every term and every
--       permissioned dataset.
--     - No instrument dimension: the loader spec pins exactly ONE table, and the API
--       exposes no instrument-attribute endpoint this loader is in scope to read
--       (/v1/instruments exists but was NOT taken - see design SS12 item 3).
--       InstrumentId / InstrumentName / Market therefore stay DENORMALISED INLINE on
--       the fact. That is a deliberate, recorded deviation from "repeated per-entity
--       strings normalise out into a dimension", for three reasons: (a) volume is
--       tiny (~205 rows/business date, ~75k rows/year, 41 distinct instruments);
--       (b) inlining preserves the AS-PUBLISHED name if the vendor ever relabels an
--       instrument; (c) there is nothing to join to and nothing that reads a
--       dimension back at load time. Reconciled OBSERVATIONALLY by
--       arm.usp_ValidateLoad, never enforced by an FK.
--
--   * arm.MarketData is the FACT. Its PRIMARY KEY is the VENDOR-SUPPLIED surrogate
--     MarketDataId (UNIQUEIDENTIFIER) - see that table's own header block for why
--     that is safe and what guards it.
--
-- -----------------------------------------------------------------------------
-- COLUMN SET: THE USER-SUPPLIED DDL IS HONOURED VERBATIM
-- -----------------------------------------------------------------------------
-- All 23 payload columns, their names, their types and their order are EXACTLY as
-- specified in the loader request, and Checksum/ModifiedAtUtc keep their specified
-- types too. Three additions and one rename were made, all recorded here:
--
--   + DateCreated DATETIME NOT NULL DEFAULT GETDATE()   (leading)
--   + FileLogId   INT NULL, FK -> arm.FileLog(Id)
--       Repo convention: every loader fact in this platform carries these two. Without
--       FileLogId the fact cannot be traced to the request that produced it, and the
--       audit hub described above becomes unjoinable. Both are ADDITIVE - no
--       user-specified column changed.
--   + three NONCLUSTERED indexes (see the table body).
--   ~ DF_AllTrades_ModifiedAtUtc  ->  DF_MarketData_ModifiedAtUtc
--       The supplied name referenced a different table ("AllTrades"). Renamed with the
--       user's explicit confirmation so it matches its own table and the
--       PK_ARM_MarketData naming already in the supplied DDL.
--
-- Nine of the 23 payload columns are ALWAYS NULL for the only currently permissioned
-- dataset - see the nullability block below. They are kept, by explicit user
-- decision, because they are real columns of the vendor's TransformedDataItem
-- contract that other datasets populate.
--
-- -----------------------------------------------------------------------------
-- TYPE CALLS
-- -----------------------------------------------------------------------------
-- Every type below is the USER-SPECIFIED type. The live-observed data is recorded
-- here so the sizing can be reviewed rather than re-derived:
--   * MarketDataId / InstrumentId -> UNIQUEIDENTIFIER. The feed sends canonical
--     hyphenated 8-4-4-4-12 GUIDs. 8,118 distinct MarketDataId over the 60-day
--     retention window; 41 distinct InstrumentId.
--   * Ask / Bid / Mid / Price / Change / PctRetDaily -> DECIMAL(18,8), NEVER FLOAT.
--     A published price must round-trip exactly. Observed grain is 4 dp
--     (e.g. -0.0263, 0.0013); 8 dp is generous headroom, and the 10 integer digits
--     (18 - 8) are far more than a basis differential will ever need.
--     *** NO non-negative CHECK on any price column: THESE ARE BASIS
--     DIFFERENTIALS AND NEGATIVE VALUES ARE ROUTINE, not exceptional. *** Observed
--     negatives on many points (e.g. Socal-Needles ask -0.10 / bid -0.25).
--   * Size / Depth / AskSize / BidSize / MidSize -> INT. Never populated for this
--     dataset, so there is no observed range to size against; INT is the user's call
--     and the safe one (TINYINT is banned by repo convention).
--   * PriceTS -> DATETIME2(0), stored as UTC. The source is an ISO-8601 instant with
--     an explicit Z and always .000 milliseconds, so scale 0 loses nothing.
--   * BusinessDate -> DATE, not DATETIME2. It is a bare YYYY-MM-DD with no time
--     component and no timezone anywhere in the feed.
--   * Market / InstrumentSourceName / InstrumentName / PriceType -> VARCHAR(250);
--     Term / Term2 / Tenor / Currency -> VARCHAR(50). Observed maxima are far below:
--     longest InstrumentName 63, Market 20, Term 13, Tenor 2, Currency 3, PriceType
--     10. VARCHAR not NVARCHAR: every observed character is ASCII.
--   * NO lookup table and NO CHECK constraint on PriceType, Currency, Market, Term or
--     Tenor. None of them is an enum in any vendor document. PriceType was the single
--     value 'Indicative' and Currency the single value 'USD' across all 8,118 observed
--     rows - but a constrained domain would fail the load the day the vendor adds a
--     second value. Reported by arm.usp_ValidateLoad instead.
--
-- -----------------------------------------------------------------------------
-- NULLABILITY - READ BEFORE "FIXING"
-- -----------------------------------------------------------------------------
-- EVERY non-PK payload column is NULLable, exactly as the supplied DDL specifies.
-- That is also the correct call on the merits:
--
--   (a) NINE COLUMNS ARE ALWAYS NULL TODAY. Across all 8,118 rows available for
--       EVOID/USNaturalGasIndex the API returned no value for:
--           Term2, InstrumentSourceName, Size, Depth, Price,
--           AskSize, BidSize, MidSize, PctRetDaily
--       and Tenor is populated on only ~47% of rows. A NOT NULL on any of these
--       would reject 100% of the feed.
--   (b) The loader parses TOLERANTLY - an unrecognised or unparseable field degrades
--       to NULL, and only an unusable KEY (MarketDataId) drops a record.
--   (c) A vendor-side rename would be SILENT. The response spelling already differs
--       from the request spelling for four fields, so renames are demonstrably in
--       this vendor's repertoire.
--   => arm.usp_ValidateLoad asserts the interesting cases (a whole date with no
--      Change, Bid > Ask, PriceTS not matching BusinessDate) so anomalies are LOUD
--      without being fatal. That proc is where unexpected NULLs get flagged - not the
--      DDL.
--
-- MarketDataId is NOT NULL structurally (it is the PK). DateCreated / Checksum /
-- ModifiedAtUtc are stamped by the table default or the merge proc and are never
-- sourced from the API. ModifiedAtUtc is left NULLable per the supplied DDL; the
-- merge proc always writes it, so in practice it is never NULL.
--
-- Standing conventions applied: named constraints (PK_/FK_/UQ_/CK_/DF_/IX_), no
-- TINYINT, minimal SELECT 1 probes inside EXISTS, guarded re-runnable creates
-- (IF OBJECT_ID(...) IS NULL / IF NOT EXISTS), seed MERGEs so re-execution keeps the
-- catalog current without duplicating rows.
--
-- Parents precede children: Endpoint + Status -> FileLog -> MarketData.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'EvolutionMarkets')
    CREATE DATABASE EvolutionMarkets;
GO

USE EvolutionMarkets;
GO

-- ----------------------------------------------------------------------------
-- Schema arm. CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (the CWG/AGSI/Platts/NGI posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Tiny fixed catalogs keyed by their natural NAME with a surrogate
-- Id PK; arm.FileLog FKs to them by Id and arm.usp_UpsertFileLog resolves
-- name -> Id server-side. Created and seeded BEFORE arm.FileLog so its FKs are valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint - the fixed catalog of in-scope endpoints. [Name] is the loader's
-- EndpointId, matched case-insensitively against EvoSettings.EnabledEndpoints and
-- IEvoPipeline.EndpointId. Url is the endpoint URL template with its parameter
-- placeholders kept literal.
--
-- NOTE the {field} placeholder stands for the PINNED 23-name projection the loader
-- always sends (EvoRequestFields.All). It is not optional and it is not
-- customisable per request: omitting it silently drops Market, PriceType, Currency
-- and Change, and trimming it silently breaks Change. See docs/apis SS4.3.
--
-- No credential appears in this or any other Evolution Markets URL: the API key is
-- sent as the raw Authorization request HEADER.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Endpoint', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Endpoint
    (
        Id          INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Endpoint PRIMARY KEY,
        DateCreated DATETIME     NOT NULL CONSTRAINT DF_Endpoint_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(40)  NOT NULL CONSTRAINT UQ_Endpoint_Name UNIQUE,
        Url         VARCHAR(400) NOT NULL
    );
END
GO

-- Seed / refresh the endpoint (MERGE -> re-runnable; keeps Url current).
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('MarketDataHistory',
     'https://evolve-api.evomarkets.com/v1/market-data/history?dateFrom={date}&dateTo={date}&field={field}&limit={limit}&offset={offset}')
) AS src ([Name], Url)
   ON tgt.[Name] = src.[Name]
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url) VALUES (src.[Name], src.Url);
GO

-- ----------------------------------------------------------------------------
-- arm.Status - the fixed set of request outcomes (NGI/AGSI/CWG shape).
--
-- For this loader NotAvailable is a ROUTINE outcome, not a fault: it is what every
-- weekend, every US market holiday, every pre-retention date and the not-yet-
-- published current date returns - as HTTP 200 with an empty array. Over a 30-day
-- window roughly 10 of 30 units legitimately land here (the live 60-day window held
-- 38 published dates out of 60 calendar days). RowCount 0, work unit SUCCEEDS.
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
-- AUDIT HUB.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.FileLog - the audit hub (design SS6). One row per business date pulled:
-- which endpoint, which date, the outcome, the HTTP status, the row count, how many
-- records were dropped, how many pages were fetched, the request path and the error
-- message if any.
--
-- NATURAL KEY: (EndpointId, RepresentativeDate).
--
-- *** RepresentativeDate IS NOT NULL HERE - A DELIBERATE DIVERGENCE FROM AGSI/NGI. ***
--   Those loaders have an UNDATED snapshot endpoint, so their hub's RepresentativeDate
--   is NULLable and their upsert MERGE must carry an explicit
--       (tgt.RepresentativeDate = src.RepresentativeDate
--        OR (tgt.RepresentativeDate IS NULL AND src.RepresentativeDate IS NULL))
--   predicate, because a UNIQUE constraint treats NULLs as EQUAL while a JOIN
--   predicate evaluates NULL = NULL as UNKNOWN.
--   THIS loader has no undated endpoint: every work unit is exactly one business date.
--   RepresentativeDate is therefore NOT NULL, and arm.usp_UpsertFileLog's MERGE uses a
--   plain equality join. Do NOT copy the OR-branch in from AGSI/NGI - it would be dead
--   code implying a NULL case that cannot occur here.
--
-- The surrogate Id PK is KEPT: arm.MarketData references the hub by Id, and a
-- narrow INT FK is cheaper on a ~75k-rows/year fact than a composite one.
-- (With RepresentativeDate NOT NULL a composite PRIMARY KEY would now be legal -
-- it is simply not wanted.)
--
-- PageCount is recorded because this endpoint's response is a bare JSON array with a
-- silent 10,000-row cap: a PageCount > 1 is the audit trail proving the pager ran,
-- and an unexpectedly high one is the first sign of a vendor-side paging change.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME       NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId         INT            NOT NULL,        -- FK -> arm.Endpoint(Id)
        RepresentativeDate DATE           NOT NULL,        -- the requested business date; NEVER NULL (see above)
        StatusId           INT            NOT NULL,        -- FK -> arm.Status(Id)
        HttpStatus         INT            NULL,            -- 200 / 400 / 401 / 403 / 404 / 429 / 5xx
        [RowCount]         INT            NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        DroppedRowCount    INT            NOT NULL CONSTRAINT DF_FileLog_DroppedRowCount DEFAULT 0,
        PageCount          INT            NOT NULL CONSTRAINT DF_FileLog_PageCount DEFAULT 0,
        RequestPath        NVARCHAR(1000) NOT NULL,         -- relative path; carries NO credential
        ErrorMessage       NVARCHAR(400)  NULL,             -- populated on Failed only
        LastCheckedUtc     DATETIME2(3)   NULL,             -- freshness of the LAST pull on this stable hub row
        ModifiedAtUtc      DATETIME2(3)   NULL,
        CONSTRAINT UQ_FileLog_Endpoint_RepDate
            UNIQUE (EndpointId, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- DATA TABLE.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.MarketData  (GET /v1/market-data/history?dateFrom=&dateTo=)
-- The Evolution Markets price fact: one row per instrument x term x tenor x business
-- date. ~205 rows per published business date (287 on some dates - the count is NOT
-- constant, so no validation check may assert a fixed number), ~75k rows/year.
--
-- *** PRIMARY KEY (MarketDataId) - A VENDOR-SUPPLIED SURROGATE. STATED DEVIATION. ***
--   The repo's fact shape is a COMPOSITE NATURAL key with no surrogate Id. Here the
--   PK is a single UNIQUEIDENTIFIER the VENDOR generates, because the loader spec
--   says so ("Merge data by PK") and because it is genuinely safe:
--     1. It is UNIQUE. All 8,118 rows in the 60-day retention window carry 8,118
--        distinct values.
--     2. It is STABLE. Re-pulling the same date returns the SAME id for the same
--        (instrument, term, tenor, business date) - which is what makes "merge by PK"
--        idempotent and what makes a REVISION land as an UPDATE rather than a new row.
--     3. The composite alternative (InstrumentId, Term, Tenor, BusinessDate) was ALSO
--        verified unique over the same 8,118 rows.
--   Point 3 is the guard: arm.usp_ValidateLoad's DuplicateCompositeKey check counts
--   composite keys carrying more than one MarketDataId. If the vendor ever starts
--   minting a fresh id per pull, that check goes non-zero and the table would be
--   silently accumulating duplicate prices - the ONE failure mode this key choice
--   admits. Do not remove that check.
--
--   Trusting a vendor id as a PK is a real dependency. It is recorded in design
--   SS12 item 1 alongside the fallback (switch the PK to the composite key, keep
--   MarketDataId as a UNIQUE attribute) should that check ever fire.
--
-- CLUSTERED on a random GUID is the user-specified index shape and is retained. The
-- usual objection (page splits and fragmentation from non-sequential inserts) is
-- immaterial at this volume - ~205 rows per daily load into a ~75k-row/year table -
-- and the alternative (a sequential surrogate, or clustering on BusinessDate) would
-- deviate from the supplied DDL for no measurable gain. Revisit only if the
-- permissioned dataset set grows by orders of magnitude.
--
-- FileLogId is PROVENANCE ONLY: it is UPDATEd on match and is NEVER part of the merge
-- key.
--
-- Column ORDER below (DateCreated, FileLogId, then the 23 payload columns, then
-- Checksum, then ModifiedAtUtc) puts columns 2..25 in the EXACT order of
-- arm.MarketDataTvp (002), so the TVP contract can be diffed against the table by
-- eye. The TVP binds BY POSITION: a silent reorder on either side corrupts every
-- loaded row without any error. See the contract block in 002.
--
-- Indexes:
--   * The PK covers the merge (which is by MarketDataId alone) but is USELESS for
--     every analytical query, because a random GUID has no useful ordering. Hence:
--   * IX on BusinessDate - the window predicate used by arm.usp_ValidateLoad and by
--     essentially every downstream query.
--   * IX on (InstrumentId, BusinessDate) - the per-instrument time series, and the
--     support for the DuplicateCompositeKey reconciliation check.
--   * IX on FileLogId - the provenance/hub join.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.MarketData', 'U') IS NULL
BEGIN
    CREATE TABLE arm.MarketData
    (
        DateCreated          DATETIME         NOT NULL CONSTRAINT DF_MarketData_DateCreated DEFAULT GETDATE(),
        FileLogId            INT              NULL,           -- provenance FK -> arm.FileLog(Id); NEVER a merge key
        MarketDataId         UNIQUEIDENTIFIER NOT NULL,        -- KEY. JSON "priceId" == CSV/XML "marketDataId"
        Market               VARCHAR(250)     NULL,            -- dataset display name, e.g. 'US Natural Gas Index'
        Term                 VARCHAR(50)      NULL,            -- AS PUBLISHED: "Sep'26-Oct'26" / '2026-Winter' / '2026-Oct'.
                                                               --   Free text in several shapes: never parse, never normalise
        Term2                VARCHAR(50)      NULL,            -- ALWAYS NULL for this dataset
        Tenor                VARCHAR(50)      NULL,            -- populated on ~47% of rows ('2m'..'5m'); absence is NORMAL
        InstrumentSourceName VARCHAR(250)     NULL,            -- ALWAYS NULL for this dataset
        InstrumentId         UNIQUEIDENTIFIER NULL,            -- 41 distinct values observed
        InstrumentName       VARCHAR(250)     NULL,            -- JSON "instrument"; observed max len 63
        PriceTS              DATETIME2(0)     NULL,            -- UTC. Always midnight Z and equal to BusinessDate
        BusinessDate         DATE             NULL,            -- JSON "date". The value this work unit requested
        PriceType            VARCHAR(250)     NULL,            -- observed: 'Indicative' only. NOT an enum: no CHECK
        [Size]               INT              NULL,            -- ALWAYS NULL for this dataset
        Depth                INT              NULL,            -- ALWAYS NULL for this dataset
        Price                DECIMAL(18, 8)   NULL,            -- ALWAYS NULL for this dataset. DO NOT backfill from Mid
        Ask                  DECIMAL(18, 8)   NULL,            -- SIGNED. NEGATIVE VALUES ARE ROUTINE. No CHECK
        AskSize              INT              NULL,            -- ALWAYS NULL for this dataset
        Bid                  DECIMAL(18, 8)   NULL,            -- SIGNED; may EQUAL Ask on a locked market
        BidSize              INT              NULL,            -- ALWAYS NULL for this dataset
        Mid                  DECIMAL(18, 8)   NULL,            -- READ, never computed (vendor rounds to 4 dp)
        MidSize              INT              NULL,            -- ALWAYS NULL for this dataset
        [Change]             DECIMAL(18, 8)   NULL,            -- day-over-day move; legitimately 0. See docs/apis SS4.3
        PctRetDaily          DECIMAL(18, 8)   NULL,            -- ALWAYS NULL for this dataset
        Currency             VARCHAR(50)      NULL,            -- observed: 'USD' only. NOT an enum: no CHECK
        [Checksum]           INT              NOT NULL CONSTRAINT DF_MarketData_Checksum DEFAULT 0,
                                                               -- FNV-1a/32 over the 22 payload columns, computed in C#
                                                               --   (EvoChecksum). Drives the merge's change short-circuit
                                                               --   so ModifiedAtUtc means "price last changed", not
                                                               --   "loader last ran". NOT an integrity checksum
        ModifiedAtUtc        DATETIME2(3)     NULL CONSTRAINT DF_MarketData_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
                                                               -- renamed from the supplied DF_AllTrades_ModifiedAtUtc,
                                                               --   which referenced a different table (user-confirmed)
        CONSTRAINT PK_ARM_MarketData PRIMARY KEY CLUSTERED (MarketDataId ASC),
        CONSTRAINT FK_MarketData_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_MarketData_BusinessDate           NONCLUSTERED (BusinessDate),
        INDEX IX_MarketData_InstrumentId_BusinessDate NONCLUSTERED (InstrumentId, BusinessDate),
        INDEX IX_MarketData_FileLogId              NONCLUSTERED (FileLogId)
    );
END
GO

-- =============================================================================
-- CONFIGURATION REMINDER (no secret is stored in this or any other sql/ file).
-- Before the loader can run, the platform DB needs one core.Param row:
--     LoaderName = 'EvolutionMarkets', ParamName = 'ApiKey', ParamValue = '<the key>'
--
-- *** THE KEY IS SENT AS THE RAW Authorization HEADER VALUE, NOT AS HTTP BASIC. ***
-- Store the bare token exactly as the vendor issued it - do NOT base64-encode it and
-- do NOT prefix it with 'Basic ' or 'Bearer '. A Basic-encoded value is rejected with
-- HTTP 403.
--
-- The value above is a PLACEHOLDER - never commit a real credential to sql/, to
-- appsettings.json, to a log line or to a test fixture. SeeDbSettingsResolver THROWS
-- if the row is missing, and the module's own guard then rejects a value left as the
-- literal 'SEE_DB' sentinel. Insert it by hand (or via sql/Core/004) on the platform
-- database, not here.
-- =============================================================================
