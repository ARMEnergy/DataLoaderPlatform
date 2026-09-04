-- =============================================================================
-- 001_CreateIceSchema.sql
-- Database : ICE            (create/select it before running this script)
-- Schema   : arm
--
-- Intercontinental Exchange settlement & index file downloads
-- (https://downloads.ice.com, SSO via https://sso.theice.com).
-- Source contract verified live 2026-09-01 — see docs/apis/ICE.md.
-- Loader design — see docs/design/ICE.md.
--
-- 18 file feeds land in the 12 fact tables below.
--
-- The tables specified by the requester are reproduced VERBATIM — column names,
-- types, nullability, primary keys, default-constraint semantics and the
-- ModifiedAtUtc indexes — with exactly ONE approved deviation (approved
-- 2026-09-01, see docs/apis/ICE.md 5.1):
--
--     arm.Futures.ProductId is NOT NULL and joins the primary key.
--
--     Six files merge into arm.Futures and ICE REUSES CONTRACT CODES ACROSS
--     MARKETS: on 2026-08-28 'OLD' is both "Ontario - Essa DA / NGX Fin Extended
--     Peak Futures" (ProductId 30948) in ngxcleared_power AND "NYH LD / Heating
--     Oil Futures" (ProductId 26755) in icecleared_oil. Under the supplied PK
--     those are the same row and ~73 rows/day are lost to whichever file merges
--     last. Adding ProductId removes all 73 conflicts (verified 0 remaining) and
--     PRODUCT_ID is never blank in any of the six feeds (verified 0).
--
-- One oddity in the supplied DDL is kept ON PURPOSE rather than silently "fixed":
--   * ModifiedAtUtc DEFAULT is sysdatetime() — server LOCAL time in a column
--     named ...Utc. Every row this loader writes gets SYSUTCDATETIME() set
--     explicitly by the merge procs, so the DEFAULT only affects rows inserted by
--     something else.
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- =============================================================================
-- AUDIT HUB
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Status — fixed outcome catalog for the FileLog hub.
--
-- 'NotAvailable' is a FIRST-CLASS, NON-ERROR outcome here, not a failure: ICE
-- answers HTTP 200 with an "Index of ... No Files Available" HTML page for a
-- weekend, a holiday, a future date, or a feed that did not exist yet
-- (docs/apis/ICE.md 2). Those days are expected and must not raise alarms.
--
-- 'AuthExpired' is separated from 'Failed' because it is the one failure mode
-- that is self-healing (the loader re-authenticates and retries) AND the one
-- that must never be mistaken for a clean zero-row load.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Status', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Status
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_ARM_Status PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_ICE_Status_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20) NOT NULL,
        CONSTRAINT UQ_ARM_Status_Name UNIQUE ([Name])
    );
END
GO

INSERT arm.Status ([Name])
SELECT v.[Name]
FROM (VALUES ('Success'), ('NotAvailable'), ('Failed'), ('AuthExpired'), ('Malformed')) AS v([Name])
WHERE NOT EXISTS (SELECT 1 FROM arm.Status AS s WHERE s.[Name] = v.[Name]);
GO

-- ----------------------------------------------------------------------------
-- arm.FileLog — one row per source FILE, for every outcome.
--
-- (FeedId, TradeDate) is the natural key: one file per feed per trade date, so a
-- re-download of the same day UPDATES its hub row rather than adding one.
--
-- RowsDropped is not decoration. Every options feed carries rows for the
-- underlying FUTURE with an empty STRIKE, and Strike is a NOT NULL PK component,
-- so those rows are dropped by design — between 1.3 % and 25.9 % of a file
-- (docs/apis/ICE.md 5.4). Recording the count is what separates "expected shape
-- of this feed" from "the parser just broke".
--
-- RequestPath stores the https URL and NEVER the SSO token.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id             INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_ARM_FileLog PRIMARY KEY,
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_ICE_FileLog_DateCreated DEFAULT GETDATE(),
        FeedId         VARCHAR(50)   NOT NULL,
        TradeDate      DATE          NOT NULL,
        FileName       VARCHAR(200)  NOT NULL,
        TargetTable    VARCHAR(128)  NULL,
        StatusId       INT           NOT NULL,
        SizeBytes      BIGINT        NULL,
        [RowCount]     INT           NOT NULL CONSTRAINT DF_ICE_FileLog_RowCount DEFAULT 0,
        RowsDropped    INT           NOT NULL CONSTRAINT DF_ICE_FileLog_RowsDropped DEFAULT 0,
        ErrorMessage   NVARCHAR(400) NULL,
        RequestPath    NVARCHAR(400) NOT NULL,
        LastCheckedUtc DATETIME2(3)  NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_ICE_FileLog_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_ARM_FileLog_Feed_TradeDate UNIQUE (FeedId, TradeDate),
        CONSTRAINT FK_ARM_FileLog_Status FOREIGN KEY (StatusId) REFERENCES arm.Status (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_FileLog_TradeDate' AND object_id = OBJECT_ID('arm.FileLog'))
    CREATE NONCLUSTERED INDEX IX_ARM_FileLog_TradeDate ON arm.FileLog (TradeDate) INCLUDE (StatusId, [RowCount]);
GO

-- =============================================================================
-- FACT TABLES
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.EnvFutures  <- icecleared_physenv_{yyyy_MM_dd}.dat
--
-- Strip is VARCHAR(50): this feed's strips are month codes and daily contracts
-- ('Aug19', 'Feb27', '01 Sep 26'), NOT dates.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.EnvFutures', 'U') IS NULL
BEGIN
    CREATE TABLE arm.EnvFutures
    (
        TradeDate       DATE          NOT NULL,
        Contract        VARCHAR(10)   NOT NULL,
        ContractType    CHAR(1)       NOT NULL,
        Strip           VARCHAR(50)   NOT NULL,
        ProductId       INT           NULL,
        Hub             VARCHAR(100)  NULL,
        Product         VARCHAR(100)  NULL,
        Strike          DECIMAL(18,6) NULL,
        SettlementPrice DECIMAL(18,6) NULL,
        NetChange       DECIMAL(18,6) NULL,
        ExpirationDate  DATE          NULL,
        SourcePath      VARCHAR(500)  NULL,
        ModifiedAtUtc   DATETIME2(3)  NULL CONSTRAINT DF_ICE_EnvFutures_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_EnvFutures PRIMARY KEY CLUSTERED
        (
            TradeDate, Contract, ContractType, Strip
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_EnvFutures_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.EnvFutures'))
    CREATE NONCLUSTERED INDEX IX_EnvFutures_ModifiedAtUtc ON arm.EnvFutures (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.EnvOptions  <- icecleared_physenvoptions_{yyyy_MM_dd}.dat
--
-- Strike is a NOT NULL PK component. The feed's 'F' (underlying future) rows have
-- a blank STRIKE and are dropped by the loader — 359 of 12,215 on 2026-08-28.
-- Strip is VARCHAR(50) and can hold a multi-leg spread ('BH26 Apr27 FH26').
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.EnvOptions', 'U') IS NULL
BEGIN
    CREATE TABLE arm.EnvOptions
    (
        TradeDate        DATE          NOT NULL,
        Contract         VARCHAR(10)   NOT NULL,
        ContractType     CHAR(1)       NOT NULL,
        Strike           DECIMAL(18,6) NOT NULL,
        Strip            VARCHAR(50)   NOT NULL,
        ProductId        INT           NULL,
        Hub              VARCHAR(100)  NULL,
        Product          VARCHAR(100)  NULL,
        SettlementPrice  DECIMAL(18,6) NULL,
        NetChange        DECIMAL(18,6) NULL,
        ExpirationDate   DATE          NULL,
        OptionVolatility DECIMAL(18,6) NULL,
        DeltaFactor      DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_EnvOptions_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_EnvOptions PRIMARY KEY CLUSTERED
        (
            TradeDate, Contract, ContractType, Strike, Strip
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_EnvOptions_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.EnvOptions'))
    CREATE NONCLUSTERED INDEX IX_EnvOptions_ModifiedAtUtc ON arm.EnvOptions (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.Futures  <- SIX feeds:
--     ngxcleared_gas, ngxcleared_power, icecleared_gas,
--     icecleared_ngl,  icecleared_oil,   iceclearedoil_ca
--
-- *** THE ONE DEVIATION FROM THE SUPPLIED DDL ***
-- ProductId is NOT NULL and is the 5th PK column. See the header of this file
-- and docs/apis/ICE.md 5.1 for the measured justification.
--
-- Strip is DATE here (unlike EnvFutures): all six feeds publish '9/1/2026'-style
-- strips and were verified 100 % date-parseable on 2026-08-28.
--
-- Note the 3,982 HARMLESS collisions/day that remain and are meant to: ICE
-- republishes the identical contract in more than one category file (LNG futures
-- appear in the gas, ngl AND oil files with identical hub, product, price and
-- product id). Re-merging identical content is a no-op.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Futures', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Futures
    (
        TradeDate       DATE          NOT NULL,
        Contract        VARCHAR(10)   NOT NULL,
        ContractType    CHAR(1)       NOT NULL,
        Strip           DATE          NOT NULL,
        ProductId       INT           NOT NULL,
        Hub             VARCHAR(100)  NULL,
        Product         VARCHAR(100)  NULL,
        Strike          DECIMAL(18,6) NULL,
        SettlementPrice DECIMAL(18,6) NULL,
        NetChange       DECIMAL(18,6) NULL,
        ExpirationDate  DATE          NULL,
        SourcePath      VARCHAR(500)  NULL,
        ModifiedAtUtc   DATETIME2(3)  NULL CONSTRAINT DF_ICE_Futures_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Futures PRIMARY KEY CLUSTERED
        (
            TradeDate, Contract, ContractType, Strip, ProductId
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_Futures_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.Futures'))
    CREATE NONCLUSTERED INDEX IX_Futures_ModifiedAtUtc ON arm.Futures (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICE_Crude_Oil_Index  <- ICE_Crude_Oil_Index_{yyyyMMdd}.csv
--
-- Column ORDER follows the supplied DDL, which differs from the file's header
-- order (INDEX_DATE_RANGE is 1st in the file, 6th here). The loader maps by
-- header NAME, so the difference is handled there.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICE_Crude_Oil_Index', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICE_Crude_Oil_Index
    (
        MKTID            INT           NOT NULL,
        PCC              VARCHAR(50)   NOT NULL,
        INDEX_ID         INT           NOT NULL,
        INDEX_DATE       DATE          NOT NULL,
        INDEX_PRICE      DECIMAL(18,6) NULL,
        INDEX_DATE_RANGE VARCHAR(50)   NULL,
        VOLUME           DECIMAL(18,6) NULL,
        MKT_DESC         VARCHAR(500)  NULL,
        CREATION_TIME    DATETIME2(7)  NULL,
        LAST_UPDATE_TIME DATETIME2(7)  NULL,
        BBL              DECIMAL(18,6) NULL,
        DAILY_PRICE      DECIMAL(18,6) NULL,
        DAILY_VOLUME     DECIMAL(18,6) NULL,
        DAILY_BBL        DECIMAL(18,6) NULL,
        NUM_OF_TRADES    INT           NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_CrudeIndex_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_ICE_Crude_Oil_Index PRIMARY KEY CLUSTERED
        (
            MKTID, PCC, INDEX_ID, INDEX_DATE
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICE_Crude_Oil_Index_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICE_Crude_Oil_Index'))
    CREATE NONCLUSTERED INDEX IX_ICE_Crude_Oil_Index_ModifiedAtUtc ON arm.ICE_Crude_Oil_Index (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICE_Crude_Oil_Index_Trades  <- ICE_Crude_Oil_Index_Trades_{yyyyMMdd}.csv
--
-- This feed is a CUMULATIVE ROLLING WINDOW, not a daily snapshot: the file name
-- is a publication date and each row carries its own TRADE_DATE, so the same deal
-- is republished across many files (docs/apis/ICE.md 4.3). Merging on the PK is
-- idempotent, which is exactly why no special handling is needed — but it does
-- mean the row count grows within an index month and then plateaus.
--
-- DEAL_ID is BIGINT: 297636150009 was observed, which overflows INT.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICE_Crude_Oil_Index_Trades', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICE_Crude_Oil_Index_Trades
    (
        TRADE_DATE          DATE          NOT NULL,
        PCC_TRADE           VARCHAR(50)   NOT NULL,
        PCC_INDEX           VARCHAR(50)   NOT NULL,
        STRIP               VARCHAR(50)   NOT NULL,
        INDEX_ID            INT           NOT NULL,
        DEAL_ID             BIGINT        NOT NULL,
        PRODUCT_NAME        VARCHAR(500)  NULL,
        HUB_NAME            VARCHAR(500)  NULL,
        DEAL_EXECUTION_TIME DATETIME2(7)  NULL,
        DEAL_PRICE          DECIMAL(18,6) NULL,
        DEAL_QUANTITY       DECIMAL(18,6) NULL,
        DEAL_QTY_IN_BBL     DECIMAL(18,6) NULL,
        MARKET_TYPE_ID      INT           NULL,
        SourcePath          VARCHAR(500)  NULL,
        ModifiedAtUtc       DATETIME2(3)  NULL CONSTRAINT DF_ICE_CrudeIndexTrades_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_ICE_Crude_Oil_Index_Trades PRIMARY KEY CLUSTERED
        (
            TRADE_DATE, PCC_TRADE, PCC_INDEX, STRIP, INDEX_ID, DEAL_ID
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICE_Crude_Oil_Index_Trades_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICE_Crude_Oil_Index_Trades'))
    CREATE NONCLUSTERED INDEX IX_ICE_Crude_Oil_Index_Trades_ModifiedAtUtc ON arm.ICE_Crude_Oil_Index_Trades (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICEClearedPowerFutures  <- icecleared_power_{yyyy_MM_dd}.dat
--
-- Same shape as arm.EnvFutures. Strip is VARCHAR(50) per the supplied DDL even
-- though this feed's strips ARE date-shaped ('1/1/2027') — the raw text is stored
-- verbatim rather than reformatted.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICEClearedPowerFutures', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICEClearedPowerFutures
    (
        TradeDate       DATE          NOT NULL,
        Contract        VARCHAR(10)   NOT NULL,
        ContractType    CHAR(1)       NOT NULL,
        Strip           VARCHAR(50)   NOT NULL,
        ProductId       INT           NULL,
        Hub             VARCHAR(100)  NULL,
        Product         VARCHAR(100)  NULL,
        Strike          DECIMAL(18,6) NULL,
        SettlementPrice DECIMAL(18,6) NULL,
        NetChange       DECIMAL(18,6) NULL,
        ExpirationDate  DATE          NULL,
        SourcePath      VARCHAR(500)  NULL,
        ModifiedAtUtc   DATETIME2(3)  NULL CONSTRAINT DF_ICE_PowerFutures_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_IceClearedPowerFutures PRIMARY KEY CLUSTERED
        (
            TradeDate, Contract, ContractType, Strip
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICEClearedPowerFutures_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICEClearedPowerFutures'))
    CREATE NONCLUSTERED INDEX IX_ICEClearedPowerFutures_ModifiedAtUtc ON arm.ICEClearedPowerFutures (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICEClearedPowerOptions  <- icecleared_poweroptions_{yyyy_MM_dd}.dat
--
-- 11,251 of 176,067 rows on 2026-08-28 are 'F' underlying-future rows with a
-- blank STRIKE and are dropped (6.4 %) — the highest ratio of the pipe feeds.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICEClearedPowerOptions', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICEClearedPowerOptions
    (
        TradeDate        DATE          NOT NULL,
        Contract         VARCHAR(10)   NOT NULL,
        ContractType     CHAR(1)       NOT NULL,
        Strike           DECIMAL(18,6) NOT NULL,
        Strip            VARCHAR(50)   NOT NULL,
        ProductId        INT           NULL,
        Hub              VARCHAR(100)  NULL,
        Product          VARCHAR(100)  NULL,
        SettlementPrice  DECIMAL(18,6) NULL,
        NetChange        DECIMAL(18,6) NULL,
        ExpirationDate   DATE          NULL,
        OptionVolatility DECIMAL(18,6) NULL,
        DeltaFactor      DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_PowerOptions_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_IceClearedPowerOptions PRIMARY KEY CLUSTERED
        (
            TradeDate, Contract, ContractType, Strike, Strip
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICEClearedPowerOptions_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICEClearedPowerOptions'))
    CREATE NONCLUSTERED INDEX IX_ICEClearedPowerOptions_ModifiedAtUtc ON arm.ICEClearedPowerOptions (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICEFCA_Options  <- ICEF_options_greeks/ICEFCA_Options_{yyyy_MM_dd}.dat
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICEFCA_Options', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICEFCA_Options
    (
        TRADE_DATE       DATE          NOT NULL,
        CONTRACT         VARCHAR(50)   NOT NULL,
        STRIP            VARCHAR(50)   NOT NULL,
        EXPIRATION_DATE  DATE          NOT NULL,
        STRIKE           DECIMAL(18,6) NOT NULL,
        PUT_CALL         CHAR(1)       NOT NULL,
        SETTLEMENT_PRICE DECIMAL(18,6) NULL,
        VOLATILITY       DECIMAL(18,6) NULL,
        DELTA            DECIMAL(18,6) NULL,
        GAMMA            DECIMAL(18,6) NULL,
        THETA            DECIMAL(18,6) NULL,
        VEGA             DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_FCAOptions_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_ICEFCA_Options PRIMARY KEY CLUSTERED
        (
            TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICEFCA_Options_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICEFCA_Options'))
    CREATE NONCLUSTERED INDEX IX_ICEFCA_Options_ModifiedAtUtc ON arm.ICEFCA_Options (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICEFUS_FinOptions  <- ICEF_options_greeks/ICEFUS_FinOptions_{yyyy_MM_dd}.dat
--
-- 760 of 2,932 rows (25.9 %) on 2026-08-28 are 'F'/'D' rows with a blank STRIKE
-- and are dropped. That is the expected shape of this file — it is largely
-- interest-rate futures — not a parser fault. arm.usp_ValidateLoad reports the
-- ratio so it stays visible.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICEFUS_FinOptions', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICEFUS_FinOptions
    (
        TRADE_DATE       DATE          NOT NULL,
        CONTRACT         VARCHAR(50)   NOT NULL,
        STRIP            VARCHAR(50)   NOT NULL,
        EXPIRATION_DATE  DATE          NOT NULL,
        STRIKE           DECIMAL(18,6) NOT NULL,
        PUT_CALL         CHAR(1)       NOT NULL,
        SETTLEMENT_PRICE DECIMAL(18,6) NULL,
        VOLATILITY       DECIMAL(18,6) NULL,
        DELTA            DECIMAL(18,6) NULL,
        GAMMA            DECIMAL(18,6) NULL,
        THETA            DECIMAL(18,6) NULL,
        VEGA             DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_FUSFinOptions_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_ICEFUS_FinOptions PRIMARY KEY CLUSTERED
        (
            TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICEFUS_FinOptions_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICEFUS_FinOptions'))
    CREATE NONCLUSTERED INDEX IX_ICEFUS_FinOptions_ModifiedAtUtc ON arm.ICEFUS_FinOptions (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.ICEFUS_SoftOptions  <- ICEF_options_greeks/ICEFUS_SoftOptions_{yyyy_MM_dd}.dat
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ICEFUS_SoftOptions', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ICEFUS_SoftOptions
    (
        TRADE_DATE       DATE          NOT NULL,
        CONTRACT         VARCHAR(50)   NOT NULL,
        STRIP            VARCHAR(50)   NOT NULL,
        EXPIRATION_DATE  DATE          NOT NULL,
        STRIKE           DECIMAL(18,6) NOT NULL,
        PUT_CALL         CHAR(1)       NOT NULL,
        SETTLEMENT_PRICE DECIMAL(18,6) NULL,
        VOLATILITY       DECIMAL(18,6) NULL,
        DELTA            DECIMAL(18,6) NULL,
        GAMMA            DECIMAL(18,6) NULL,
        THETA            DECIMAL(18,6) NULL,
        VEGA             DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_FUSSoftOptions_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_ICEFUS_SoftOptions PRIMARY KEY CLUSTERED
        (
            TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ICEFUS_SoftOptions_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.ICEFUS_SoftOptions'))
    CREATE NONCLUSTERED INDEX IX_ICEFUS_SoftOptions_ModifiedAtUtc ON arm.ICEFUS_SoftOptions (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.IFLL_Options  <- FixedIncome_Settlements/IFLL_Options_{yyyy_MM_dd}.xlsx
--
-- The ONLY XLSX feed. Same 12 columns as the greeks .dat feeds, but its header
-- spells the 6th column 'PUT/CALL' with a slash rather than 'PUT_CALL'
-- (docs/apis/ICE.md 4.4).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.IFLL_Options', 'U') IS NULL
BEGIN
    CREATE TABLE arm.IFLL_Options
    (
        TRADE_DATE       DATE          NOT NULL,
        CONTRACT         VARCHAR(50)   NOT NULL,
        STRIP            VARCHAR(50)   NOT NULL,
        EXPIRATION_DATE  DATE          NOT NULL,
        STRIKE           DECIMAL(18,6) NOT NULL,
        PUT_CALL         CHAR(1)       NOT NULL,
        SETTLEMENT_PRICE DECIMAL(18,6) NULL,
        VOLATILITY       DECIMAL(18,6) NULL,
        DELTA            DECIMAL(18,6) NULL,
        GAMMA            DECIMAL(18,6) NULL,
        THETA            DECIMAL(18,6) NULL,
        VEGA             DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_IFLLOptions_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_IFLL_Options PRIMARY KEY CLUSTERED
        (
            TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_IFLL_Options_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.IFLL_Options'))
    CREATE NONCLUSTERED INDEX IX_IFLL_Options_ModifiedAtUtc ON arm.IFLL_Options (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.Options  <- TWO feeds: icecleared_gasoptions, icecleared_oiloptions
--
-- Strip is DATE here (unlike EnvOptions / ICEClearedPowerOptions): both feeds
-- publish '8/1/2026'-style strips, verified 100 % date-parseable on 2026-08-28.
--
-- The two feeds share this table and were verified to produce ZERO conflicting
-- payloads on the declared PK, so — unlike arm.Futures — no PK change is needed.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Options', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Options
    (
        TradeDate        DATE          NOT NULL,
        Contract         VARCHAR(10)   NOT NULL,
        ContractType     CHAR(1)       NOT NULL,
        Strike           DECIMAL(18,6) NOT NULL,
        Strip            DATE          NOT NULL,
        ProductId        INT           NULL,
        Hub              VARCHAR(100)  NULL,
        Product          VARCHAR(100)  NULL,
        SettlementPrice  DECIMAL(18,6) NULL,
        NetChange        DECIMAL(18,6) NULL,
        ExpirationDate   DATE          NULL,
        OptionVolatility DECIMAL(18,6) NULL,
        DeltaFactor      DECIMAL(18,6) NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ICE_Options_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Options PRIMARY KEY CLUSTERED
        (
            TradeDate, Contract, ContractType, Strike, Strip
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_Options_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.Options'))
    CREATE NONCLUSTERED INDEX IX_Options_ModifiedAtUtc ON arm.Options (ModifiedAtUtc);
GO
