-- =============================================================================
-- 001_CreateCmeSchema.sql
-- Database : CMEGroup    (create/select it before running this script)
-- Schema   : arm
--
-- CME settlement-bulletin loader. The source is an SSH SFTP drop
-- (sftp.cmeprod.datahex.rozettatech.com:22) whose tree is
--
--     <PRODUCT>_<EXCHANGE> / EOD_<EXCHANGE> / yyyy / MM / dd / <EXCHANGE>_yyyyMMdd.txt
--     BAS_STLAGS           / EOD_STLAGS     / 2026 / 09 / 04 / STLAGS_20260904.txt
--
-- The feed folder is split on its FIRST underscore: the text before it is the
-- ProductCode (EOD) and the text after it is the ExchangeCode (STLAGS). Eight
-- feeds were entitled to this account when the loader was built, holding a
-- rolling window of 10 business days each:
--
--     EOD_STLAGS  EOD_STLALT  EOD_STLCOMEX  EOD_STLCPC
--     EOD_STLCUR  EOD_STLEQT  EOD_STLINT    EOD_STLNYMEX
--
-- Note BAS_STLNYMEX_STLCPC holds TWO feed folders (EOD_STLNYMEX and EOD_STLCPC),
-- which is why only the FEED-level name is split and only on the first underscore.
--
-- ==== FILE LAYOUT (verified live over 16 bulletins, 757,360 data rows) ====
-- Fixed-width, LF line endings, ONE 3-line header per file (no page repeats):
--
--          FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)
--   MTH/                       -------  DAILY  ------ ...
--   STRIKE            OPEN         HIGH          LOW  ...
--   0CJ TEST PLATINUM FUTURE                                  <- future section
--   SEP26             ----         ----   ...   1821.0 ...     <-   label = contract month
--   0PO OCT26 TEST PLATINUM OPTION CALL                       <- option section
--   1540            294.00       294.00   ...                  <-   label = strike
--   TOTAL                          ...  EST.VOL  VOLUME  OPEN INT
--   TOTAL                               175315   153289    612748
--
-- Column right edges: 22 35 48 61 74 87 99 114 126 138. The A/B indicator sits
-- ONE character past the numeric edge of High/Low/Last (36, 49, 62) -- and only
-- those three, which is exactly the three indicator columns the DDL provides.
--
-- ⚠ FIELDS ARE POSITIONAL, NOT WHITESPACE-DELIMITED. A missing value is
-- sometimes "----" and sometimes BLANK in the same file, so a whitespace split
-- slides later values into the wrong columns (a prior-day settle would load as a
-- volume). This matters here only as background, but it is the reason the loader
-- can be trusted to fill these columns correctly.
--
-- ==== THINGS THE DATA DOES THAT THE TABLES HAVE TO ABSORB ====
--   * CAB ("cabinet", a nominal-minimum settlement) appears as a PRICE token
--     80,917 times across STLAGS/STLCUR/STLEQT/STLINT. It has no numeric form and
--     these tables have no marker column, so it is stored NULL and counted on the
--     arm.FileLog row.
--   * TICK prices: STLAGS quotes eighths (491'4 = 491.5); STLINT quotes 64ths
--     ('635 = 63.5/64). The denominator is a per-FEED property -- reading '4 as
--     4/64 in a grain bulletin silently corrupts the price -- so the loader keys
--     it on ExchangeCode. Both are converted to decimal here; the display form is
--     not preserved because these columns are DECIMAL(18,8).
--   * Observed value ranges, all comfortably inside these types: ProductSymbol
--     <= 5 chars, option description <= 92, future description <= 90,
--     ContractYear 2014..2099 (both ends legitimate -- Eris swap futures list past
--     effective dates, and DEC99 is a far-dated test contract),
--     Strike -550000.00 .. 700000.00, PutCall in (C, P).
--
-- ==== BALMO / EVENT FUTURES ARE NOT LOADED (requester's decision) ====
-- A minority of futures sections put the contract month on the HEADER and use a
-- DAY OF MONTH as the row label:
--
--   1D AUG26 RBOB Gasoline BALMO Futures
--   10 ... 3.2375 ...   17 ... 3.2784 ...   20 ... 3.2795 ...
--
-- PK_ARM_STLBASIC_Future has no day component, so all of a section's day rows
-- collapse onto one key. Offered the choice between adding a ContractDay column
-- and skipping these rows, the requester chose to SKIP them. That is 8,827 of
-- 56,019 futures rows per trade date, all in STLCPC and STLEQT. They are counted
-- per file in arm.FileLog.DayLabelRowsSkipped and logged at warning level, so the
-- omission is auditable rather than silent. To start loading them, add
-- ContractDay to this table AND to its primary key AND to the TVP in 002 AND to
-- the merge in 003 -- all four, together.
--
-- ==== ModifiedAtUtc: a DELIBERATE, FLAGGED deviation from the supplied DDL ====
-- The requester's DDL defaulted ModifiedAtUtc to SYSDATETIME(), which is the
-- server's LOCAL time and contradicts the column's own name. Every other loader
-- in this repo stamps SYSUTCDATETIME(), and the merge procs in 003 set the column
-- explicitly, so a local-time DEFAULT would also make freshly inserted rows
-- disagree with updated ones. SYSUTCDATETIME() is used throughout. Change both
-- this default and the procs together if local time is genuinely wanted.
--
-- Everything else about the two fact tables -- column names, order, types,
-- nullability, key order, index -- is exactly as supplied.
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- =============================================================================
-- LOOKUPS
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Status -- fixed outcome catalog for the FileLog hub.
--   Success       bulletin downloaded and parsed
--   Failed        download or parse failed
--   NotAvailable  reserved: the loader enumerates what the drop HOLDS rather than
--                 constructing candidate dates, so a weekend is simply an absence
--                 of work and no row is written. Kept so the catalog matches the
--                 other loaders and so a future date-window mode has a value.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Status', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Status
    (
        Id          INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_ARM_Status PRIMARY KEY,
        DateCreated DATETIME     NOT NULL CONSTRAINT DF_ARM_Status_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20)  NOT NULL,
        CONSTRAINT UQ_ARM_Status_Name UNIQUE ([Name])
    );
END
GO

INSERT arm.Status ([Name])
SELECT v.[Name]
FROM (VALUES ('Success'), ('NotAvailable'), ('Failed')) AS v([Name])
WHERE NOT EXISTS (SELECT 1 FROM arm.Status s WHERE s.[Name] = v.[Name]);
GO

-- =============================================================================
-- AUDIT HUB
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.FileLog -- one row per source bulletin, every outcome.
--
-- The two fact tables carry NO provenance column (the supplied DDL has none), so
-- this hub is the ONLY place a partially-loaded or partially-skipped bulletin is
-- visible. That is why it carries the parse counters and not just a status:
-- DayLabelRowsSkipped is data the loader deliberately dropped, and
-- UnclassifiedLines is the early warning that CME changed the layout.
--
-- FileName alone is the natural key: within this account a bulletin name
-- (STLAGS_20260904.txt) is unique across the whole drop, because the exchange
-- code is part of the name.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                    INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_ARM_FileLog PRIMARY KEY,
        FileName              VARCHAR(128)  NOT NULL,
        FeedId                VARCHAR(50)   NOT NULL,
        ExchangeCode          VARCHAR(50)   NULL,
        ProductCode           VARCHAR(50)   NULL,
        TradeDate             DATE          NULL,
        StatusId              INT           NOT NULL,
        RemoteLastModifiedUtc DATETIME2(3)  NULL,
        SizeBytes             BIGINT        NULL,
        [RowCount]            INT           NOT NULL CONSTRAINT DF_ARM_FileLog_RowCount DEFAULT (0),
        OptionRowCount        INT           NOT NULL CONSTRAINT DF_ARM_FileLog_OptionRowCount DEFAULT (0),
        FutureRowCount        INT           NOT NULL CONSTRAINT DF_ARM_FileLog_FutureRowCount DEFAULT (0),
        DayLabelRowsSkipped   INT           NOT NULL CONSTRAINT DF_ARM_FileLog_DayLabelRowsSkipped DEFAULT (0),
        UnclassifiedLines     INT           NOT NULL CONSTRAINT DF_ARM_FileLog_UnclassifiedLines DEFAULT (0),
        ReportHeader          VARCHAR(200)  NULL,
        ErrorMessage          NVARCHAR(400) NULL,
        RequestPath           NVARCHAR(400) NOT NULL,
        LastCheckedUtc        DATETIME2(3)  NOT NULL CONSTRAINT DF_ARM_FileLog_LastCheckedUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc         DATETIME2(3)  NOT NULL CONSTRAINT DF_ARM_FileLog_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_ARM_FileLog_FileName UNIQUE (FileName),
        CONSTRAINT FK_ARM_FileLog_Status FOREIGN KEY (StatusId) REFERENCES arm.Status (Id)
    );

    CREATE NONCLUSTERED INDEX IX_ARM_FileLog_TradeDate ON arm.FileLog (TradeDate, FeedId);
    CREATE NONCLUSTERED INDEX IX_ARM_FileLog_ModifiedAtUtc ON arm.FileLog (ModifiedAtUtc);
END
GO

-- =============================================================================
-- FACT TABLES -- exactly as supplied, except the ModifiedAtUtc default
-- (see the header note).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.STLBASIC_Option -- one row per
--   (exchange, product, trade date, symbol, description, contract, put/call, strike)
--
-- ProductDescription is part of the key. SQL Server promotes every PRIMARY KEY
-- column to NOT NULL regardless of the DDL, so ExchangeCode, ProductCode,
-- ProductSymbol, ProductDescription and PutCall are all effectively NOT NULL
-- here -- the loader sends '' rather than NULL for an empty description, which is
-- what keeps a description-less section loadable.
--
-- Verified against 701,341 live option rows: ZERO duplicate keys.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.STLBASIC_Option', 'U') IS NULL
BEGIN
    CREATE TABLE arm.STLBASIC_Option
    (
        ExchangeCode       VARCHAR(50),
        ProductCode        VARCHAR(50),
        TradeDate          DATE NOT NULL,
        ProductSymbol      VARCHAR(50),
        ProductDescription VARCHAR(250),
        ContractYear       SMALLINT NOT NULL,
        ContractMonth      TINYINT NOT NULL,
        PutCall            VARCHAR(50),
        Strike             DECIMAL(18, 8) NOT NULL,
        [Open]             DECIMAL(18, 8) NULL,
        High               DECIMAL(18, 8) NULL,
        HighABIndicator    CHAR(1),
        Low                DECIMAL(18, 8) NULL,
        LowABIndicator     CHAR(1),
        [Last]             DECIMAL(18, 8) NULL,
        LastABIndicator    CHAR(1),
        Settle             DECIMAL(18, 8) NULL,
        PctChange          DECIMAL(18, 8) NULL,
        EstVol             DECIMAL(18, 8) NULL,
        PriorSettle        DECIMAL(18, 8) NULL,
        PriorVol           DECIMAL(18, 8) NULL,
        PriorInt           DECIMAL(18, 8) NULL,
        ModifiedAtUtc      DATETIME2(3) CONSTRAINT DF_ARM_STLBASIC_Option_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_STLBASIC_Option PRIMARY KEY CLUSTERED
            (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ProductDescription,
             ContractYear, ContractMonth, PutCall, Strike)
    );

    CREATE NONCLUSTERED INDEX IX_ARM_STLBASIC_Option_ModifiedAtUtc
        ON arm.STLBASIC_Option (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- arm.STLBASIC_Future -- one row per
--   (exchange, product, trade date, symbol, contract)
--
-- Note two deliberate differences from the option table, both as supplied:
-- ProductDescription sits AFTER ContractMonth, and it is NOT part of the key --
-- so unlike the option table it is genuinely nullable here.
--
-- Verified against 47,192 live futures rows (contract-month labels): ZERO
-- duplicate keys. The 8,827 day-label BALMO rows per trade date are NOT loaded --
-- see the header note.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.STLBASIC_Future', 'U') IS NULL
BEGIN
    CREATE TABLE arm.STLBASIC_Future
    (
        ExchangeCode       VARCHAR(50),
        ProductCode        VARCHAR(50),
        TradeDate          DATE NOT NULL,
        ProductSymbol      VARCHAR(50),
        ContractYear       SMALLINT NOT NULL,
        ContractMonth      TINYINT NOT NULL,
        ProductDescription VARCHAR(2000),
        [Open]             DECIMAL(18, 8) NULL,
        High               DECIMAL(18, 8) NULL,
        HighABIndicator    CHAR(1),
        Low                DECIMAL(18, 8) NULL,
        LowABIndicator     CHAR(1),
        [Last]             DECIMAL(18, 8) NULL,
        LastABIndicator    CHAR(1),
        Settle             DECIMAL(18, 8) NULL,
        PctChange          DECIMAL(18, 8) NULL,
        EstVol             DECIMAL(18, 8) NULL,
        PriorSettle        DECIMAL(18, 8) NULL,
        PriorVol           DECIMAL(18, 8) NULL,
        PriorInt           DECIMAL(18, 8) NULL,
        ModifiedAtUtc      DATETIME2(3) CONSTRAINT DF_ARM_STLBASIC_Future_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_STLBASIC_Future PRIMARY KEY CLUSTERED
            (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ContractYear, ContractMonth)
    );

    CREATE NONCLUSTERED INDEX IX_ARM_STLBASIC_Future_ModifiedAtUtc
        ON arm.STLBASIC_Future (ModifiedAtUtc);
END
GO
