-- =============================================================================
-- 002_CreateMarexTvpTypes.sql
-- Database : Marex
-- Schema   : arm
--
-- One table type per target table (5).
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in src/DataLoader.Marex/MarexDescriptors.cs EXACTLY;
-- a silent reorder corrupts every loaded row without raising an error. The risk
-- is unusually real for arm.MarketStatisticTvp, which has TEN consecutive
-- interchangeable DECIMAL(18,8) columns (Average .. SettlementPrice) and then
-- four consecutive VARCHAR(50)s (State, Pipeline, Location) around ProductId --
-- a one-position slip anywhere in either run type-checks perfectly and loads
-- garbage.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.Marex.Tests/MarexTvpContractTests.cs parses THIS FILE, pulls
-- each CREATE TYPE body out, and asserts name + order + type against the
-- descriptors. Editing a type here without editing the descriptor (or the other
-- way round) fails the test suite.
--
-- Conventions:
--   * TVP column order = target table COLUMN order.
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * NOT NULL marks a column the loader treats as REQUIRED. Every primary key
--     component is required; everything else is nullable, because on this
--     source almost everything genuinely is (a market with no trades today has
--     nulls in every price column).
--
-- ExchangeDate and TradeDate ARE TVP columns even though they are not fields of
-- their own DTOs. They are the gateway's own trading day, delivered on a
-- separate ExchangeDateSnapshot event, and they are primary key components --
-- so the server has to receive them. Stamping them in the proc with
-- CAST(SYSDATETIME() AS DATE) instead would put a key component on the SERVER's
-- clock and time zone rather than the exchange's. See 001 note 1.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in
-- order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.ClosingPriceTvp
--   -> arm.ClosingPrice
-- 8 columns: the table's 9 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.ClosingPriceTvp') IS NULL
BEGIN
    CREATE TYPE arm.ClosingPriceTvp AS TABLE
    (
        ExchangeDate    DATE            NOT NULL,
        ClosingPriceId  VARCHAR(50)     NOT NULL,
        ProductId       INT             NULL,
        PeriodId        BIGINT          NULL,
        Price           DECIMAL(18, 8)  NULL,
        [Time]          DATETIME2(7)    NULL,
        PreviousPrice   DECIMAL(18, 8)  NULL,
        PreviousTime    DATETIME2(7)    NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.MarketStatisticTvp
--   -> arm.MarketStatistic
-- 26 columns: the table's 27 less ModifiedAtUtc.
--
-- Read the DECIMAL run below against MarexDescriptors.MarketStatistic before
-- changing ANY line in it. The order is:
--   Average, PcChangeOnDay, ChangeOnDay, Change, CumQty, Open, Low, High,
--   MidPrice, SettlementPrice
-- which is the supplied DDL's order, and is NOT alphabetical, NOT the DTO's
-- property order, and NOT grouped by meaning.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.MarketStatisticTvp') IS NULL
BEGIN
    CREATE TYPE arm.MarketStatisticTvp AS TABLE
    (
        TradeDate               DATE            NOT NULL,
        Id                      BIGINT          NOT NULL,
        CciIndex                VARCHAR(50)     NULL,
        [Index]                 VARCHAR(50)     NULL,
        TransactionCount        BIGINT          NULL,
        [Average]               DECIMAL(18, 8)  NULL,
        PcChangeOnDay           DECIMAL(18, 8)  NULL,
        ChangeOnDay             DECIMAL(18, 8)  NULL,
        [Change]                DECIMAL(18, 8)  NULL,
        CumQty                  DECIMAL(18, 8)  NULL,
        [Open]                  DECIMAL(18, 8)  NULL,
        [Low]                   DECIMAL(18, 8)  NULL,
        [High]                  DECIMAL(18, 8)  NULL,
        MidPrice                DECIMAL(18, 8)  NULL,
        SettlementPrice         DECIMAL(18, 8)  NULL,
        LastTradeTime           DATETIME2(7)    NULL,
        LastTradeEventSource    VARCHAR(50)     NULL,
        LastTradeAggressorSide  VARCHAR(50)     NULL,
        LastTradePrice          DECIMAL(18, 8)  NULL,
        [State]                 VARCHAR(50)     NULL,
        Pipeline                VARCHAR(50)     NULL,
        [Location]              VARCHAR(50)     NULL,
        ProductId               BIGINT          NULL,
        [Period]                VARCHAR(50)     NULL,
        Product                 VARCHAR(50)     NULL,
        UpdateReason            VARCHAR(50)     NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PeriodTvp
--   -> arm.Period
-- 10 columns: the table's 11 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PeriodTvp') IS NULL
BEGIN
    CREATE TYPE arm.PeriodTvp AS TABLE
    (
        PeriodId            BIGINT          NOT NULL,
        [Name]              VARCHAR(256)    NULL,
        DeliveryStart       DATE            NULL,
        DeliveryEnd         DATE            NULL,
        TradingStart        DATE            NULL,
        TradingEnd          DATE            NULL,
        NoticeOfShipment    DATETIME2(7)    NULL,
        PeriodGroupId       INT             NULL,
        ExternalId          BIGINT          NULL,
        ExternalSourceId    INT             NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.PeriodGroupTvp
--   -> arm.PeriodGroup
-- 6 columns: the table's 7 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.PeriodGroupTvp') IS NULL
BEGIN
    CREATE TYPE arm.PeriodGroupTvp AS TABLE
    (
        PeriodGroupId       BIGINT          NOT NULL,
        [Name]              VARCHAR(256)    NULL,
        DisplayName         VARCHAR(256)    NULL,
        IsHidden            BIT             NULL,
        ExternalId          BIGINT          NULL,
        ExternalSourceId    INT             NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.ProductTvp
--   -> arm.Product
-- 20 columns: the table's 21 less ModifiedAtUtc.
--
-- The Name/Id pairs below alternate name-then-id for Pipeline, Index, Location,
-- Grade and Group, then MarketName precedes MarketId, and ClearingHouseId
-- stands alone with no name column. That is the supplied DDL's order. Follow
-- the list, not the pattern.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.ProductTvp') IS NULL
BEGIN
    CREATE TYPE arm.ProductTvp AS TABLE
    (
        ProductId                   BIGINT          NOT NULL,
        ExternalSourceId            INT             NULL,
        ExternalId                  BIGINT          NULL,
        PipelineName                VARCHAR(256)    NULL,
        PipelineId                  INT             NULL,
        IndexName                   VARCHAR(256)    NULL,
        IndexId                     INT             NULL,
        LocationName                VARCHAR(256)    NULL,
        LocationId                  INT             NULL,
        GradeName                   VARCHAR(256)    NULL,
        GradeId                     INT             NULL,
        CounterpartyDetailsVisible  BIT             NULL,
        GroupName                   VARCHAR(256)    NULL,
        GroupId                     INT             NULL,
        ClearingHouseId             INT             NULL,
        MarketName                  VARCHAR(256)    NULL,
        MarketId                    BIGINT          NULL,
        ProductType                 VARCHAR(256)    NULL,
        [Name]                      VARCHAR(256)    NULL,
        TradeChartDataSource        VARCHAR(256)    NULL
    );
END
GO
