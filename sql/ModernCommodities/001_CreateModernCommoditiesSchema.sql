-- =============================================================================
-- 001_CreateModernCommoditiesSchema.sql
-- Loader:   ModernCommodities  (vendor "ModCom" - trade tape + daily settlements)
-- Database: ModernCommodities        Schema: arm
-- Creates:  database, [arm] schema, the Endpoint/Status lookups, the arm.FileLog
--           audit hub, and the three fact tables:
--             * arm.AllTrades   (GET allTrades/v1    - anonymised market tape)
--             * arm.MyTrades    (GET myTrades/v1     - fully attributed)
--             * arm.Settlements (GET settlements/v1  - daily settlement curves)
--
-- Design of record: docs/design/ModernCommodities.md
--                   (SS6 FileLog, SS8 full-field mapping, SS9 validation,
--                    SS11 merge semantics, SS13 DATABASE_DEVELOPER coordination)
-- Field reference:  docs/apis/ModernCommodities.md (SS10.2 / SS11.2 per-field
--                   types, observed maxima, blank behaviour)
-- Template:         sql/NGI/001_CreateNgiSchema.sql (structure, guards,
--                   idempotency, lookup + hub posture)
--
-- SCHEMA NOTE (deliberate override of a standing default): DATABASE_DEVELOPER's
-- default target schema is [dbo]; this loader is explicitly [arm], matching
-- Platts/CWG/AGSI/IHSPointLogic/IIR/OPIS/NGI. StormVista is the cautionary
-- precedent where [dbo] was taken by default and then had to be documented as a
-- deviation. EVERY object in this loader is arm.*.
--
-- =============================================================================
-- *** THE THREE FACT TABLES ARE THE USER'S OWN AUTHORITATIVE DDL. ***
-- =============================================================================
-- arm.AllTrades / arm.MyTrades / arm.Settlements below reproduce the script the
-- user supplied (scratchpad MODCOM_DDL.sql) - column NAMES, ORDER, WIDTHS,
-- NULLABILITY and PRIMARY KEYS character-for-character - with exactly the
-- deviations listed here, every one of them deliberate and stated:
--
--   1. `Checksum INT` is REMOVED from all three tables. Explicit user decision
--      (MODCOM_DECISIONS U2: "Ignore that column. Remove it from all 3 tables").
--      Consequently the MERGEs do a straight WHEN MATCHED update with NO checksum
--      short-circuit (003).
--
--   2. `ModifiedAtUtc` DEFAULT is SYSUTCDATETIME(), where the user's script said
--      SYSDATETIME() (decision D3). The column is named ...Utc but SYSDATETIME()
--      returns server LOCAL time, and every other loader here stamps UTC. The
--      merge procs stamp this column EXPLICITLY on every insert and every update,
--      so the DEFAULT is effectively unreachable - this change only stops the two
--      write paths from disagreeing in basis.  *** FLAGGED TO THE USER. ***
--
--   3. The DEFAULT constraints are NAMED (DF_<table>_ModifiedAtUtc) where the
--      user's script left them unnamed. This changes no column name, width, order
--      or key - only the constraint's own name - and it satisfies the standing
--      "no auto-generated constraint names" rule so a teardown/ALTER can find it.
--      Cosmetic, but stated rather than silent.
--
--   4. Indexes are added in a SEPARATE, GUARDED block AFTER the CREATE TABLEs
--      (design SS13 item 11), deliberately NOT inside the table bodies, so each
--      CREATE TABLE stays a verbatim copy of the user's text. Indexes change no
--      shape and no consumer.
--
-- *** `PieplineTerminal` IN arm.Settlements IS MISSPELLED - ON PURPOSE. ***
--   The misspelling is the user's, and it is PART OF THAT TABLE'S PRIMARY KEY
--   (decision D2). It is reproduced EXACTLY here, in arm.SettlementsTvp (002), in
--   arm.usp_BulkMergeSettlements (003) and in the C# property name. It may already
--   have downstream consumers. *** DO NOT "FIX" IT. *** Note that the two trades
--   tables spell the same concept CORRECTLY (`PipelineTerminal`), so both
--   spellings coexist on purpose - that is not a bug either.
--
-- *** NO surrogate `Id`, NO `DateCreated`, NO `FileLogId`, NO `Checksum` on any of
--   the three fact tables. *** The composite/natural PK with no surrogate Id is
--   the repo's normal high-volume-fact shape; the ABSENT `DateCreated` is a STATED
--   DEVIATION from the house fact shape (which leads with it) - we follow the
--   user's DDL rather than adding a column they did not ask for (design SS13 #1).
--   Do not "restore the house shape": a reviewer seeing this file should read this
--   header, not open a finding.
--
-- =============================================================================
-- *** arm.FileLog IS THE ONLY PROVENANCE THIS LOADER HAS. ***
-- =============================================================================
-- Because the three fact tables carry NO FileLogId (decision D1), you CANNOT ask
-- "which pull produced this row?" - only "which pulls covered a window containing
-- it?". Two consequences a future reader most needs and cannot infer from the DDL:
--   * every check in arm.usp_ValidateLoad is WINDOW-based or ModifiedAtUtc-based;
--     there is NO fact -> hub join anywhere (contrast NGI's IssueDateMatchesRequest);
--   * the TVPs in 002 begin at the FIRST PAYLOAD COLUMN, not at FileLogId - a
--     deliberate break from the cross-loader "FileLogId is column 1" rule, because
--     the column does not exist. Anyone "restoring the convention" must first add
--     the column to the user's three tables, which is out of scope.
-- The hub therefore gets ONE ROW PER PULL, keyed (EndpointId, WindowStart,
-- WindowEnd, RunToken) (decision D7) - see the arm.FileLog block below.
--
-- =============================================================================
-- CONSTRAINTS THAT MUST NEVER BE ADDED (design SS13 item 5)
-- =============================================================================
--   * NO `CHECK (Price >= 0)`, NO `CHECK (Volume >= 0)`, no non-negative check on
--     any price or commission column. 74% of settlement prices (1,071 of 1,443)
--     and 21% of allTrades prices are NEGATIVE - they are location/quality
--     DIFFERENTIALS, and 0.00 is a real value. A leading minus is a SIGN, never a
--     parse failure and never a sentinel.
--   * NO lookup table, CHECK constraint or enum on State / TradeType / ProductType
--     / UnitOfMeasure / Side / SettlementCurrency / PriceBasis. No vendor document
--     enumerates any of them; a new product type or a CAD settlement MUST load and
--     be REPORTED (usp_ValidateLoad UnknownEnumValues), never rejected.
--   * NO FOREIGN KEY between arm.AllTrades, arm.MyTrades and arm.Settlements, and
--     none from a fact to arm.FileLog. The three pipelines are mutually
--     INDEPENDENT (no discovery tier, no reference provider, no barrier): a
--     Settlements-only or MyTrades-only run is fully valid. The SAME TradeNumber
--     legitimately exists in BOTH trades tables - they are two views of the venue
--     at different disclosure levels, not a parent/child pair (design SS1.2).
--   * NO `-` -> NULL normalisation anywhere. arm.Settlements.Location and
--     .PieplineTerminal carry the LITERAL one-character string '-' on 82 of 1,443
--     rows (always as a PAIR, all Product = 'Sweet Guernsey Blend'), and both are
--     NOT NULL columns INSIDE THE PRIMARY KEY. '-' is a KEY VALUE, not a sentinel.
--
-- =============================================================================
-- TYPE CALLS (all live-observed 2026-08-24 - docs/apis/ModernCommodities.md)
-- =============================================================================
--   * DECIMAL(9,2), never FLOAT, for Price / Volume / BidCommission /
--     OfferCommission. A traded price must round-trip EXACTLY; this repo reserves
--     FLOAT for genuinely approximate measures (coordinates), and this feed has
--     none. Observed scale is exactly 2 in 100% of rows across all three captures.
--   * DATETIME2(0) for Executed / LastUpdated. Seconds precision only - no
--     fractional second was ever observed. The source is 12-hour AM/PM text
--     (`2026-08-24 01:44:41 PM` = 13:44:41) with *** NO TIMEZONE STATED ANYWHERE ***
--     (not in the PDF, not in the body, not in a response header). Stored EXACTLY
--     AS GIVEN and never shifted. Do NOT join these to a UTC column and assume
--     they align (design SS7, SS12 item 3).
--   * DATE for TermStart / TermEnd / SettlementDate - no time component exists.
--     TermEnd reaches 2031-12-31 (the settlement curve runs ~5.4 years forward,
--     192 rows already in the 2030s), so no tighter date-range assumption anywhere.
--   * VARCHAR, not NVARCHAR, throughout: every observed character in all three
--     live captures is ASCII. Revisit only if counterparties with accented legal
--     names are onboarded.
--   * SpreadTradeNumber stays VARCHAR(50) per the user's DDL - it is a REFERENCE,
--     not a measure. *** Do not "fix" it to INT *** even though it looks numeric.
--   * BIT for ApportionmentProtected / InIndex / ClickAndTrade: the source carries
--     the literal text True/False, and *** BLANK -> NULL, NOT false ***. Conflating
--     them would destroy the anonymisation signal (all 98 allTrades rows would read
--     "not a click trade" instead of "unknown").
--   * ModifiedAtUtc DATETIME2(3).
--
-- -----------------------------------------------------------------------------
-- OBSERVED MAX vs DECLARED WIDTH  (live 2026-08-24: 98 allTrades rows,
-- 37 myTrades rows, 1,443 settlements rows. Observed maxima are SNAPSHOTS, not
-- vendor-declared limits - the vendor publishes no CSV specification at all.)
-- -----------------------------------------------------------------------------
-- TRADES - identical 34-column shape on arm.AllTrades and arm.MyTrades:
--   #  Column                  Declared        Observed max          Note
--   1  TradeNumber             INT             5 digits 57112..68043 PK; INT to 2.1e9
--   2  State                   VARCHAR(50)     9  'Finalized'
--   3  Product                 VARCHAR(50)     28                    TIGHTEST ratio; '/'-joined on spreads
--   4  Location                VARCHAR(50)     23                    2nd tightest; '/'-joined on spreads
--   5  PipelineTerminal        VARCHAR(256)    19
--   6  PriceBasis              VARCHAR(256)    45                    ' + '-joined index expression
--   7  Term                    VARCHAR(256)    13                    4 shapes, 2 separators ('/' and '~')
--   8  TermStart               DATE            -                     always the 1st of a month
--   9  TermEnd                 DATE            -                     always a month end (2028-02-29 seen)
--  10  Price                   DECIMAL(9, 2)   89.05 / min -16.65    SIGNED; 2 int digits observed
--  11  Volume                  DECIMAL(9, 2)   *** 300,000 ***       SEE THE VOLUME FLAG BELOW
--  12  UnitOfMeasure           VARCHAR(50)     15 'contracts/month'
--  13  Executed                DATETIME2(0)    -                     12-hour AM/PM source, no timezone
--  14  LastUpdated             DATETIME2(0)    -                     THE window predicate + merge recency guard
--  15  TradeType               VARCHAR(50)     10 'Second Leg'
--  16  Side                    VARCHAR(256)    4  'Sell'             0/98 in allTrades (anonymised)
--  17  BidTrader               VARCHAR(256)    19                    0/98; PII
--  18  BidLegalName            VARCHAR(256)    41                    0/98; CONTAINS COMMAS
--  19  BidAddress              VARCHAR(256)    53                    0/98; up to 4 embedded commas
--  20  BidCommission           DECIMAL(9, 2)   0.01                  0/98, and only 16/37 in myTrades
--  21  OfferTrader             VARCHAR(256)    19                    0/98; PII
--  22  OfferLegalName          VARCHAR(256)    41                    0/98; contains commas
--  23  OfferAddress            VARCHAR(256)    53                    0/98; contains commas
--  24  OfferCommission         DECIMAL(9, 2)   0.01                  0/98, 21/37 - COMPLEMENTARY to #20
--  25  SpreadTradeNumber       VARCHAR(50)     5 digits              42/98; stays TEXT
--  26  ApportionmentProtected  BIT             'False' only          True NEVER observed
--  27  ClearingID              VARCHAR(50)     *** 0 - NEVER POPULATED, 0/98 AND 0/37 ***
--  28  SettlementCurrency      VARCHAR(50)     3  'USD'              0/98; do NOT constrain to USD
--  29  ContractTerms           VARCHAR(256)    41                    0/98; contains commas
--  30  GTandC                  VARCHAR(256)    41                    0/98; source header is 'GT&C'
--  31  Notes                   VARCHAR(8000)   31                    0/98, 3/37; generous width is right
--  32  InIndex                 BIT             True 11 / False 87
--  33  ClickAndTrade           BIT             'False' only          0/98; BLANK -> NULL, not false
--  34  ProductType             VARCHAR(50)     9  'Financial'        discriminator for #4/#5/#6 blankness
--
-- SETTLEMENTS - arm.Settlements:
--   #  Column                  Declared        Observed max          Note
--   1  SettlementDate          DATE            -                     PK 1/6; business days only
--   2  Product                 VARCHAR(50)     22                    PK 2/6
--   3  Location                VARCHAR(50)     19                    PK 3/6; literal '-' on 82 rows
--   4  PieplineTerminal        VARCHAR(50)     21                    PK 4/6; MISSPELLED ON PURPOSE
--   5  PriceBasis              VARCHAR(100)    7                     PK 5/6; only 'WTI CMA' / 'USD $'
--   6  Term                    VARCHAR(50)     6  'AUG-26'           PK 6/6; always a single MMM-YY
--   7  TermStart               DATE            -                     never blank, still NULLable
--   8  TermEnd                 DATE            2031-12-31            ~5.4 years forward
--   9  Price                   DECIMAL(9, 2)   2 int digits + 2 dp   NEGATIVE in 1,071/1,443 (74%)
--
-- *** THE VOLUME FLAG (decision D8 - kept as the user specified). ***
--   Volume DECIMAL(9, 2) has a ceiling of 9,999,999.99 against an OBSERVED MAXIMUM
--   of 300,000 (TradeNumber 68020, a bbls/month row). That is only ~33x headroom,
--   NOT the ~1,000x an early reading assumed. It matters because an over-range
--   value is a HARD ARITHMETIC OVERFLOW ERROR (msg 8115) that fails the ENTIRE
--   batch - not a silent truncation - and a monthly bbls/month cargo deal an order
--   of magnitude larger than today's biggest would breach it. Volume has also never
--   carried a decimal point in any observed row, so scale 2 is pure headroom.
--   Mitigations in place: the C# `Dec92` parse helper range-guards each value and
--   degrades a breach to NULL with an error-level warning (so one bad row cannot
--   fail a batch), and usp_ValidateLoad's NumericHeadroom check reports MAX(ABS())
--   against the ceiling on every run.
--   *** RECOMMENDATION TO THE USER: DECIMAL(13,2) removes the risk entirely at
--   negligible cost. Their call - NOT changed here. ***
--
-- -----------------------------------------------------------------------------
-- NULLABILITY - READ BEFORE "FIXING"
-- -----------------------------------------------------------------------------
-- On the trades tables ONLY TradeNumber is NOT NULL; every other column is
-- NULLable. That is both the user's DDL and the correct call: 14 counterparty
-- columns are 100% NULL in arm.AllTrades BY DESIGN (the vendor anonymises them
-- there), ClearingID is 100% NULL in BOTH tables, and Location /
-- PipelineTerminal / PriceBasis are legitimately blank on every
-- ProductType = 'Financial' row (a financial contract has no delivery location).
-- The loader also parses TOLERANTLY - an unrecognised field degrades to NULL
-- rather than failing the run - so a NOT NULL here would convert a silent vendor
-- rename into a hard failure of every row. Unexpected NULLs are flagged by
-- arm.usp_ValidateLoad, not by the DDL.
-- On arm.Settlements the six PK columns are NOT NULL (never blank in 1,443 rows,
-- so this is safe) and TermStart / TermEnd / Price stay NULLable per the user's
-- DDL even though they too were never blank - which keeps the tolerant-parse
-- contract expressible end to end.
--
-- Standing conventions applied: named constraints (PK_/FK_/UQ_/CK_/DF_/IX_), no
-- TINYINT, minimal SELECT 1 probes inside EXISTS, guarded re-runnable creates,
-- seed MERGEs so re-execution keeps the catalog current without duplicating rows.
--
-- Parents precede children: Endpoint + Status -> FileLog -> the three fact tables
-- (which have no FKs at all, so their order among themselves is immaterial).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'ModernCommodities')
    CREATE DATABASE ModernCommodities;
GO

USE ModernCommodities;
GO

-- ----------------------------------------------------------------------------
-- Schema arm. CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (the CWG/AGSI/NGI posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Tiny fixed catalogs keyed by their natural NAME with a surrogate
-- Id PK; arm.FileLog FKs to them by Id and arm.usp_UpsertFileLog resolves
-- name -> Id server-side, so the C# writer keeps a name-string signature.
-- Created and seeded BEFORE arm.FileLog so its FKs are valid.
-- These two ARE house-shaped dimensions (Id IDENTITY first, DateCreated second,
-- UNIQUE natural key) - the deviation described in this file's header applies only
-- to the three user-specified FACT tables.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint - the fixed catalog of ModCom's 3 in-scope endpoints. [Name] is the
-- loader's EndpointId, matched case-insensitively against
-- ModComSettings.EnabledEndpoints and IModComPipeline.EndpointId. Url is the
-- endpoint URL template with its parameter placeholders kept literal.
-- NO CREDENTIAL APPEARS IN ANY ModCom URL: auth is HTTP Basic in an Authorization
-- HEADER (contrast CWG/StormVista's ?apikey=), which is why the request URI is
-- safe to store here at all. The header itself is never logged and never stored.
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
-- Order matches the loader's cosmetic execution order (AllTrades -> MyTrades ->
-- Settlements), which is a LOGGING convenience only and NOT load-bearing: the
-- three pipelines are mutually independent and any subset is a valid run.
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('AllTrades',   'https://app.modcom.inc/api/integration/allTrades/v1?startDate={startDate}&endDate={endDate}'),
    ('MyTrades',    'https://app.modcom.inc/api/integration/myTrades/v1?startDate={startDate}&endDate={endDate}&legalEntityName={legalEntityName}'),
    ('Settlements', 'https://app.modcom.inc/api/integration/settlements/v1?startDate={startDate}&endDate={endDate}')
) AS src ([Name], Url)
   ON tgt.[Name] = src.[Name]
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url) VALUES (src.[Name], src.Url);
GO

-- ----------------------------------------------------------------------------
-- arm.Status - the fixed set of request outcomes (AGSI/CWG/NGI shape).
--   Success      - HTTP 200 with a header + at least one data row.
--   NotAvailable - HTTP 200 with a HEADER-ONLY body: a LEGITIMATE EMPTY READ (a
--                  quiet myTrades window, a weekend / not-yet-published
--                  settlements window). The work unit SUCCEEDS with zero rows.
--   Failed       - any non-2xx. *** Unlike NGI, this API has NO legitimate
--                  non-2xx status: every 400 is our bug (malformed date, over-cap
--                  window, inverted range) and every 401 is a bad credential.
--                  A Failed row here always merits attention. ***
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
-- arm.FileLog - the audit hub, and *** THE ONLY PROVENANCE THIS LOADER HAS ***
-- (decision D1: no FileLogId on any fact table). One row per PULL: which
-- endpoint, which window, which run token, the outcome, the HTTP status, the rows
-- PARSED, the rows DROPPED, and the sanitised request path.
--
-- NATURAL KEY: (EndpointId, WindowStart, WindowEnd, RunToken)  [decision D7]
--   *** ONE ROW PER PULL - NOT an upsert over a stable per-window key. ***
--   FileLog is the only provenance, so collapsing pulls would ERASE the fact that
--   the 03:00 pull returned 0 rows after the 02:00 pull returned 20. Every hour's
--   outcome for every window must survive. Volume is negligible: ~24 rows/day/
--   endpoint at the hourly cadence, ~26k rows/year/endpoint.
--
--   It is still a MERGE (003), not a blind INSERT: a FAILED unit is retried by the
--   next run within the SAME hour (core.LoadLog only skips successes), and that
--   retry carries the SAME (EndpointId, WindowStart, WindowEnd, RunToken). The
--   MERGE lets the retry's outcome REPLACE the failure in place, so the hub records
--   the LAST outcome per pull identity. Distinct hours and distinct windows never
--   collapse.
--
--   All four key columns are NOT NULL, so a plain UNIQUE constraint suffices and
--   *** NO NULL-EQUALITY TRICK IS NEEDED HERE *** - contrast NGI, whose undated
--   Locations hub row forces an explicit
--   (tgt.X = src.X OR (tgt.X IS NULL AND src.X IS NULL)) branch in its upsert.
--   Do not copy that branch into arm.usp_UpsertFileLog; it would be dead code.
--
-- The surrogate Id PK is KEPT (the house FileLog shape: Id first, DateCreated
-- second). Nothing references it - no fact table has a FileLogId - but
-- arm.usp_UpsertFileLog returns it so the reader can log "FileLog #N" and so a
-- future schema change has the value available without a signature change.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id              INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated     DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId      INT           NOT NULL,     -- FK -> arm.Endpoint(Id)
        WindowStart     DATE          NOT NULL,     -- the ACTUAL startDate sent, after clamping + chunking
        WindowEnd       DATE          NOT NULL,     -- the ACTUAL endDate sent (may be "today" or later; a
                                                    --   future endDate is verified harmless)
        RunToken        VARCHAR(40)   NOT NULL,     -- the resume key's {hot} token, always UTC:
                                                    --   'yyyyMMddHH' (RunHour, the default) / 'yyyyMMdd'
                                                    --   (RunDate) / a 32-char GUID 'N' (RunId).
                                                    --   *** WIDTH NOTE: design SS6.2 proposed VARCHAR(32),
                                                    --   which is EXACTLY a GUID 'N' with zero headroom.
                                                    --   Widened to 40 - it is not in any TVP, so widening
                                                    --   breaks no positional contract, and a longer future
                                                    --   token would otherwise be a hard truncation error.
        StatusId        INT           NOT NULL,     -- FK -> arm.Status(Id)
        HttpStatus      INT           NULL,         -- 200 / 400 / 401 / 403 / 404 / 429 / 5xx
        [RowCount]      INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
                                                    -- *** rows PARSED and handed to the sink - NOT rows
                                                    --   MERGED. The hub is written by the READER, before the
                                                    --   sink runs. parsed != persisted is NORMAL (the trades
                                                    --   recency guard legitimately skips updates).
        DroppedRowCount INT           NOT NULL CONSTRAINT DF_FileLog_DroppedRowCount DEFAULT 0,
                                                    -- unkeyable rows + over-width-KEY rows. Without a
                                                    -- fact-side provenance column this is the ONLY durable
                                                    -- record that rows were discarded, which is why it is a
                                                    -- first-class column and a validated check (expect 0).
        ScopeLabel      VARCHAR(100)  NULL,         -- the myTrades legalEntityName when set, else NULL.
                                                    --   Observed valid values are 26 and 32 chars.
        RequestPath     NVARCHAR(400) NOT NULL,     -- sanitised: path + startDate/endDate (+legalEntityName)
        ErrorMessage    NVARCHAR(400) NULL,         -- DUAL USE, both intentional:
                                                    --   (a) the VERBATIM non-2xx body, truncated - the four
                                                    --       distinct 400s are distinguishable ONLY by body
                                                    --       text, and this is the only place it survives;
                                                    --   (b) a NON-FATAL note on a Success row, e.g.
                                                    --       'HeaderDrift: unexpected column ''X'''.
                                                    --   Never contains a credential (auth is a header).
        LastCheckedUtc  DATETIME2(3)  NULL,         -- when this pull was made (the validator's freshness axis)
        ModifiedAtUtc   DATETIME2(3)  NULL,
        CONSTRAINT UQ_FileLog_Endpoint_Window_RunToken
            UNIQUE (EndpointId, WindowStart, WindowEnd, RunToken),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- FACT TABLES - the user's authoritative DDL (see this file's header for the four
-- stated deviations). Column names, order, widths, nullability and PRIMARY KEYS
-- are reproduced character-for-character.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.AllTrades  (GET allTrades/v1?startDate=&endDate=)
-- The ANONYMISED market tape: every trade on the venue, with the counterparty
-- block suppressed by the vendor. 34 payload columns + ModifiedAtUtc.
--
-- PRIMARY KEY CLUSTERED (TradeNumber) - the merge key, and TradeNumber ALONE.
--   *** A revision arrives as the SAME TradeNumber with a LATER
--   Last Updated Timestamp and a changed payload. *** A key including the
--   timestamp or a date would INSERT A DUPLICATE instead of correcting the
--   existing row, which is the whole reason the window is re-pulled at all.
--
-- WHY THE WINDOW IS RE-PULLED EVERY HOUR (the fact that shapes this loader):
--   the trades window filters on Last Updated Timestamp, NOT Executed Timestamp.
--   Of 98 live rows for 2026-08-20..24, FIVE were executed BEFORE the window -
--   including three legs executed 2026-08-06 and revised 18 days later - and ZERO
--   fell outside it on last-updated. So nothing in this feed is ever final: a
--   State can flip Finalized -> Cancelled months later. The resume key is
--   HOT-ONLY at hour granularity, with NO settled zone, precisely so those
--   revisions are seen.
--
-- THE 14 ANONYMISED COLUMNS (Side, BidTrader, BidLegalName, BidAddress,
-- BidCommission, OfferTrader, OfferLegalName, OfferAddress, OfferCommission,
-- SettlementCurrency, ContractTerms, GTandC, Notes, ClickAndTrade) are 100% NULL
-- HERE and populated in arm.MyTrades - that asymmetry is the whole reason two
-- tables exist. *** Do NOT "optimise" them off this table: *** the venue could
-- begin populating some of them and a narrower table would silently discard data.
-- usp_ValidateLoad reports them as INFORMATIONAL, EXPECTED - 100% NULL is CORRECT.
-- ClearingID is NOT one of them: it is 0/98 here AND 0/37 in myTrades, i.e. never
-- populated by anything, and must never be asserted populated anywhere.
--
-- *** DOWNSTREAM WARNING, kept on the table on purpose: SPREAD VOLUME IS NOT
-- ADDITIVE. *** A Spread arrives as a 3-row group sharing one SpreadTradeNumber
-- (the parent, whose SpreadTradeNumber equals its own TradeNumber, plus a
-- 'First Leg' and a 'Second Leg'). The parent's Volume is NOT additive with its
-- legs' and its Price is a DIFFERENTIAL while the legs carry outright prices, so
-- naively summing Volume over this table TRIPLE-COUNTS spread volume. The loader
-- persists all three rows exactly as published and filters nothing.
--
-- Column ORDER below is the LOAD-BEARING 34-column TVP contract - mirrored
-- EXACTLY by arm.TradesTvp (002), the merge procs' SELECT/UPDATE/INSERT lists
-- (003) and the C# sink's BuildTable. The TVP binds BY POSITION: a silent reorder
-- on any side corrupts every loaded row with no error of any kind.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.AllTrades', 'U') IS NULL
BEGIN
    CREATE TABLE arm.AllTrades(
        TradeNumber INT NOT NULL,
        State VARCHAR(50) NULL,
        Product VARCHAR(50) NULL,
        Location VARCHAR(50) NULL,
        PipelineTerminal VARCHAR(256) NULL,
        PriceBasis VARCHAR(256) NULL,
        Term VARCHAR(256) NULL,
        TermStart DATE NULL,
        TermEnd DATE NULL,
        Price DECIMAL(9, 2) NULL,
        Volume DECIMAL(9, 2) NULL,
        UnitOfMeasure VARCHAR(50) NULL,
        Executed DATETIME2(0) NULL,
        LastUpdated DATETIME2(0) NULL,
        TradeType VARCHAR(50) NULL,
        Side VARCHAR(256) NULL,
        BidTrader VARCHAR(256) NULL,
        BidLegalName VARCHAR(256) NULL,
        BidAddress VARCHAR(256) NULL,
        BidCommission DECIMAL(9, 2) NULL,
        OfferTrader VARCHAR(256) NULL,
        OfferLegalName VARCHAR(256) NULL,
        OfferAddress VARCHAR(256) NULL,
        OfferCommission DECIMAL(9, 2) NULL,
        SpreadTradeNumber VARCHAR(50) NULL,
        ApportionmentProtected BIT NULL,
        ClearingID VARCHAR(50) NULL,
        SettlementCurrency VARCHAR(50) NULL,
        ContractTerms VARCHAR(256) NULL,
        GTandC VARCHAR(256) NULL,
        Notes VARCHAR(8000) NULL,
        InIndex BIT NULL,
        ClickAndTrade BIT NULL,
        ProductType VARCHAR(50) NULL,
        ModifiedAtUtc DATETIME2(3) NULL CONSTRAINT DF_AllTrades_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
     CONSTRAINT PK_AllTrades PRIMARY KEY CLUSTERED
    (
        TradeNumber ASC
    ));
END
GO

-- ----------------------------------------------------------------------------
-- arm.MyTrades  (GET myTrades/v1?startDate=&endDate=[&legalEntityName=])
-- The FULLY ATTRIBUTED view of the company's own trades: byte-identical 34-column
-- header (497 bytes, verified across three captures), identical column list, and
-- the 14 columns arm.AllTrades leaves NULL are populated here.
--
-- Identical shape is exactly why *** BOTH TABLES SHARE ONE TVP TYPE
-- (arm.TradesTvp, 002), ONE C# row type and ONE BuildTable *** - two 34-column
-- definitions kept in sync by hand is precisely the drift a POSITIONAL contract
-- cannot survive. Only the target table and the merge proc differ.
--
-- *** NO FK to arm.AllTrades, and no cross-table dedup. *** The SAME TradeNumber
-- legitimately exists in both tables (e.g. 66986); they are two views of the venue
-- at different disclosure levels, not a parent/child pair. usp_ValidateLoad
-- reports the overlap INFORMATIONALLY (expected non-zero), never as a defect.
--
-- Two things differ operationally, neither of them in the schema: this endpoint
-- serves ALL-TIME history (no 6-calendar-month clamp - only the 10,000-row cap
-- applies) and it accepts an optional legalEntityName scope, recorded in
-- arm.FileLog.ScopeLabel. Omitting the scope returns BOTH ARM legal entities
-- (verified), which is the shipped default.
--
-- Sparse-but-real population pattern the validator must NOT turn into a rule:
-- BidCommission 16/37 and OfferCommission 21/37 are COMPLEMENTARY (16 + 21 = 37)
-- because a commission is populated on the company's OWN side only. A "commission
-- always present" or "both sides present" rule must never ship.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.MyTrades', 'U') IS NULL
BEGIN
    CREATE TABLE arm.MyTrades(
        TradeNumber INT NOT NULL,
        State VARCHAR(50) NULL,
        Product VARCHAR(50) NULL,
        Location VARCHAR(50) NULL,
        PipelineTerminal VARCHAR(256) NULL,
        PriceBasis VARCHAR(256) NULL,
        Term VARCHAR(256) NULL,
        TermStart DATE NULL,
        TermEnd DATE NULL,
        Price DECIMAL(9, 2) NULL,
        Volume DECIMAL(9, 2) NULL,
        UnitOfMeasure VARCHAR(50) NULL,
        Executed DATETIME2(0) NULL,
        LastUpdated DATETIME2(0) NULL,
        TradeType VARCHAR(50) NULL,
        Side VARCHAR(256) NULL,
        BidTrader VARCHAR(256) NULL,
        BidLegalName VARCHAR(256) NULL,
        BidAddress VARCHAR(256) NULL,
        BidCommission DECIMAL(9, 2) NULL,
        OfferTrader VARCHAR(256) NULL,
        OfferLegalName VARCHAR(256) NULL,
        OfferAddress VARCHAR(256) NULL,
        OfferCommission DECIMAL(9, 2) NULL,
        SpreadTradeNumber VARCHAR(50) NULL,
        ApportionmentProtected BIT NULL,
        ClearingID VARCHAR(50) NULL,
        SettlementCurrency VARCHAR(50) NULL,
        ContractTerms VARCHAR(256) NULL,
        GTandC VARCHAR(256) NULL,
        Notes VARCHAR(8000) NULL,
        InIndex BIT NULL,
        ClickAndTrade BIT NULL,
        ProductType VARCHAR(50) NULL,
        ModifiedAtUtc DATETIME2(3) NULL CONSTRAINT DF_MyTrades_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
     CONSTRAINT PK_MyTrades PRIMARY KEY CLUSTERED
    (
        TradeNumber ASC
    ));
END
GO

-- ----------------------------------------------------------------------------
-- arm.Settlements  (GET settlements/v1?startDate=&endDate=)
-- The daily settlement curve: for each SettlementDate, one row per
-- (product, location, pipeline/terminal, price basis, TERM MONTH). ~721 rows per
-- PUBLISHED date (720 Thu / 723 Fri observed - the grid is NOT fixed), and the
-- curve runs ~5.4 years forward (TermEnd to 2031-12-31).
--
-- PRIMARY KEY CLUSTERED (SettlementDate, Product, Location, PieplineTerminal,
--                        PriceBasis, Term)   - all six NOT NULL, zero duplicates
-- across 1,443 live rows.
--
-- *** `PieplineTerminal` IS MISSPELLED, IT IS IN THE PRIMARY KEY, AND THAT IS
-- DELIBERATE. *** It is the user's own schema (decision D2) and may already have
-- downstream consumers, so it is reproduced EXACTLY here, in arm.SettlementsTvp
-- (002), in arm.usp_BulkMergeSettlements (003) and in the C# property name.
-- Misspelling the C# property too is intentional: it makes the intent obvious so
-- nobody later "tidies" the name and silently breaks the POSITIONAL TVP binding.
-- The two trades tables spell the same concept CORRECTLY (PipelineTerminal), so
-- both spellings coexist on purpose. FLAGGED to the user, NOT fixed.
--
-- *** THE LITERAL '-' IS A KEY VALUE, NOT A NULL SENTINEL. *** Location and
-- PieplineTerminal carry the one-character string '-' on 82 of 1,443 rows, always
-- as a PAIR (never one alone), all Product = 'Sweet Guernsey Blend' (41 term
-- months x 2 settlement dates): one blended product with no single physical
-- location or pipeline. Both columns are NOT NULL and both are IN THE PK, so
-- mapping '-' to NULL would violate the key and - if applied inconsistently -
-- create duplicate logical rows the MERGE cannot reconcile. There is NO '-',
-- 'None', 'N/A' or 'NULL' sentinel anywhere in this API; the only absent-value
-- representation is the empty string. (A '-' PREFIXING DIGITS in Price is a SIGN.)
--
-- Term here is ALWAYS a single MMM-YY month - no '/', no '~', no quarters. That is
-- UNLIKE the trades Term (max 13 chars, four shapes, two separators), so do not
-- share one Term parser or validator across the two shapes. The loader parses
-- neither: Term is persisted as published text.
--
-- *** A SETTLEMENT PRICE REVISION OVERWRITES - THERE IS NO HISTORY. *** Price sits
-- OUTSIDE the PK, and the payload carries no revision, version, status or as-of
-- field of any kind, so a restatement is indistinguishable from the original
-- except by comparing values. That is correct IF settlements are never restated,
-- which is UNVERIFIED (the highest-value open question on this loader). If it
-- proves false, the pattern to reach for is OPIS's arm.LPReportHistory, which puts
-- the distinguishing value INSIDE the key so both prints survive
-- (sql/OPIS/001, sql/OPIS/003). Deliberately NOT built now.
--
-- A weekend, a holiday or a not-yet-published current day simply contributes no
-- rows - that is a legitimate gap inside an HTTP 200, not a defect, so there is no
-- business-day calendar anywhere in this loader.
--
-- Column ORDER below is the LOAD-BEARING 9-column TVP contract - mirrored EXACTLY
-- by arm.SettlementsTvp (002), the merge proc (003) and the C# BuildTable.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Settlements', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Settlements(
        SettlementDate DATE NOT NULL,
        Product VARCHAR(50) NOT NULL,
        Location VARCHAR(50) NOT NULL,
        PieplineTerminal VARCHAR(50) NOT NULL,   -- misspelling is the user's, and is IN THE PK. Keep it.
        PriceBasis VARCHAR(100) NOT NULL,
        Term VARCHAR(50) NOT NULL,
        TermStart DATE NULL,
        TermEnd DATE NULL,
        Price DECIMAL(9, 2) NULL,
        ModifiedAtUtc DATETIME2(3) NULL CONSTRAINT DF_Settlements_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
     CONSTRAINT PK_Settlements PRIMARY KEY CLUSTERED
    (
        SettlementDate ASC,
        Product ASC,
        Location ASC,
        PieplineTerminal ASC,
        PriceBasis ASC,
        Term ASC
    ));
END
GO

-- =============================================================================
-- INDEXES - added SEPARATELY so each CREATE TABLE above stays a verbatim copy of
-- the user's DDL. Each PK already covers its own MERGE; these support the
-- validation checks and ordinary operational queries (design SS13 item 11).
-- All guarded on sys.indexes, so the script is re-runnable.
-- =============================================================================

-- LastUpdated: the trades window predicate. EVERY window-scoped validation check
-- and any "what changed" operational query filters on it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AllTrades_LastUpdated' AND object_id = OBJECT_ID('arm.AllTrades'))
    CREATE NONCLUSTERED INDEX IX_AllTrades_LastUpdated ON arm.AllTrades (LastUpdated);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_MyTrades_LastUpdated' AND object_id = OBJECT_ID('arm.MyTrades'))
    CREATE NONCLUSTERED INDEX IX_MyTrades_LastUpdated ON arm.MyTrades (LastUpdated);
GO

-- SpreadTradeNumber: the SpreadGroupIntegrity check self-joins on it (and it is
-- the only way to reassemble a 3-row spread group).
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AllTrades_SpreadTradeNumber' AND object_id = OBJECT_ID('arm.AllTrades'))
    CREATE NONCLUSTERED INDEX IX_AllTrades_SpreadTradeNumber ON arm.AllTrades (SpreadTradeNumber);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_MyTrades_SpreadTradeNumber' AND object_id = OBJECT_ID('arm.MyTrades'))
    CREATE NONCLUSTERED INDEX IX_MyTrades_SpreadTradeNumber ON arm.MyTrades (SpreadTradeNumber);
GO

-- ModifiedAtUtc: the FRESHNESS axis. With no FileLogId on any fact table, the
-- "did this run persist anything?" check is ModifiedAtUtc >= @ModifiedSinceUtc -
-- the substitute for the fact -> hub join this loader deliberately cannot make.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AllTrades_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.AllTrades'))
    CREATE NONCLUSTERED INDEX IX_AllTrades_ModifiedAtUtc ON arm.AllTrades (ModifiedAtUtc);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_MyTrades_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.MyTrades'))
    CREATE NONCLUSTERED INDEX IX_MyTrades_ModifiedAtUtc ON arm.MyTrades (ModifiedAtUtc);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_Settlements_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.Settlements'))
    CREATE NONCLUSTERED INDEX IX_Settlements_ModifiedAtUtc ON arm.Settlements (ModifiedAtUtc);
GO
-- No index on arm.Settlements (SettlementDate): it is PK column 1, so the
-- clustered key already serves every window scan.

-- Hub: the outcome/HTTP-status/drop-count summaries scope by window, and the
-- current-run failure check scopes by LastCheckedUtc.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FileLog_WindowEnd_EndpointId' AND object_id = OBJECT_ID('arm.FileLog'))
    CREATE NONCLUSTERED INDEX IX_FileLog_WindowEnd_EndpointId ON arm.FileLog (WindowEnd, EndpointId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FileLog_LastCheckedUtc' AND object_id = OBJECT_ID('arm.FileLog'))
    CREATE NONCLUSTERED INDEX IX_FileLog_LastCheckedUtc ON arm.FileLog (LastCheckedUtc);
GO

-- =============================================================================
-- CONFIGURATION REMINDER (no secret is stored in this or any other sql/ file).
-- Before the loader can run, the PLATFORM database needs two core.Param rows:
--     LoaderName = 'ModernCommodities', ParamName = 'Username'
--     LoaderName = 'ModernCommodities', ParamName = 'Password'
-- SeeDbSettingsResolver THROWS if either row is missing, and the module's own
-- guard then rejects a value left as the literal 'SEE_DB' sentinel (compared
-- trimmed and case-insensitively). Insert them by hand (or via sql/Core/004) on
-- the platform database, NOT here.
-- *** NEVER commit a real credential to sql/, to appsettings.json, to a log line,
-- to an exception message or to a test fixture. The vendor PDF also prints the
-- credential PRE-ENCODED as a ready-made `Authorization: Basic ...` value - THAT
-- ENCODED FORM IS A CREDENTIAL TOO. ***
-- =============================================================================
