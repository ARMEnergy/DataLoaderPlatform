-- =============================================================================
-- 002_CreateIceTvpTypes.sql
-- Database : ICE
-- Schema   : arm
--
-- One table type per TARGET TABLE (12), not per feed (18): the six feeds that
-- land in arm.Futures and the two that land in arm.Options share their table's
-- type, which is what guarantees they produce identically-shaped rows.
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in src/DataLoader.ICE/IceDescriptors.cs EXACTLY; a
-- silent reorder corrupts every loaded row without raising an error.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.ICE.Tests/IceTvpContractTests.cs parses THIS FILE, pulls each
-- CREATE TYPE body out, and asserts name + order + type against the descriptors.
-- Editing a type here without editing the descriptor (or the other way round)
-- fails the test suite.
--
-- Conventions:
--   * TVP column order = target table column order.
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * NOT NULL marks a column the parser treats as REQUIRED (a row missing it is
--     DROPPED rather than merged under a blank key). This is how the ~2-26 % of
--     options rows that describe the underlying future — 'F'/'D' rows with no
--     STRIKE — are excluded. See docs/apis/ICE.md 5.4.
--   * SourcePath is the loader-derived provenance column and is always last.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed — to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.EnvFuturesTvp -> arm.EnvFutures   (feed: EnvFutures)
-- Strip is VARCHAR: month codes and daily contracts, not dates.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.EnvFuturesTvp') IS NULL
BEGIN
    CREATE TYPE arm.EnvFuturesTvp AS TABLE
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
        SourcePath      VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.EnvOptionsTvp -> arm.EnvOptions   (feed: EnvOptions)
-- Strike NOT NULL: drops the feed's blank-strike 'F' rows.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.EnvOptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.EnvOptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FuturesTvp -> arm.Futures   (SIX feeds share this type)
--
-- ProductId is NOT NULL here because it is a PK column (the one approved
-- deviation — see 001 and docs/apis/ICE.md 5.1). Verified never blank across all
-- six feeds, so requiring it drops nothing.
-- Strip is DATE: all six feeds publish date-shaped strips.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FuturesTvp') IS NULL
BEGIN
    CREATE TYPE arm.FuturesTvp AS TABLE
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
        SourcePath      VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.CrudeOilIndexTvp -> arm.ICE_Crude_Oil_Index   (feed: CrudeIndex)
-- Order follows the TABLE, which differs from the file's header order.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CrudeOilIndexTvp') IS NULL
BEGIN
    CREATE TYPE arm.CrudeOilIndexTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.CrudeOilIndexTradesTvp -> arm.ICE_Crude_Oil_Index_Trades
-- DEAL_ID is BIGINT — 297636150009 overflows INT.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CrudeOilIndexTradesTvp') IS NULL
BEGIN
    CREATE TYPE arm.CrudeOilIndexTradesTvp AS TABLE
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
        SourcePath          VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PowerFuturesTvp -> arm.ICEClearedPowerFutures   (feed: PowerFutures)
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PowerFuturesTvp') IS NULL
BEGIN
    CREATE TYPE arm.PowerFuturesTvp AS TABLE
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
        SourcePath      VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PowerOptionsTvp -> arm.ICEClearedPowerOptions   (feed: PowerOptions)
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PowerOptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.PowerOptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- The four GREEKS types. Identical shape; four distinct types so each proc binds
-- its own target table and a future divergence in one feed cannot silently
-- widen the other three.
--   arm.FcaOptionsTvp     -> arm.ICEFCA_Options       (feed: FcaOptions)
--   arm.FusFinOptionsTvp  -> arm.ICEFUS_FinOptions    (feed: FusFinOptions)
--   arm.FusSoftOptionsTvp -> arm.ICEFUS_SoftOptions   (feed: FusSoftOptions)
--   arm.IfllOptionsTvp    -> arm.IFLL_Options         (feed: IfllOptions, XLSX)
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FcaOptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.FcaOptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

IF TYPE_ID('arm.FusFinOptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.FusFinOptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

IF TYPE_ID('arm.FusSoftOptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.FusSoftOptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

IF TYPE_ID('arm.IfllOptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.IfllOptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.OptionsTvp -> arm.Options   (TWO feeds share this type)
-- Strip is DATE here, unlike EnvOptions/PowerOptions.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.OptionsTvp') IS NULL
BEGIN
    CREATE TYPE arm.OptionsTvp AS TABLE
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
        SourcePath       VARCHAR(500)  NULL
    );
END
GO
