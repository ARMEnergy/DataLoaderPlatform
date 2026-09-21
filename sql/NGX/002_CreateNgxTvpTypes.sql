-- =============================================================================
-- 002_CreateNgxTvpTypes.sql
-- Database : NGX
-- Schema   : arm
--
-- One table type per target table (2).
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in src/DataLoader.NGX/NgxDescriptors.cs EXACTLY; a
-- silent reorder corrupts every loaded row without raising an error. The risk
-- is unusually real here because arm.IndexPrice has four consecutive
-- DECIMAL(18,8) columns (TradedAmount, TradedTotalAmount, AlternateTradedAmount,
-- AlternateTradedTotalAmount) and three consecutive VARCHAR(50) columns around
-- them -- a one-position slip would type-check perfectly and load garbage.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.NGX.Tests/NgxTvpContractTests.cs parses THIS FILE, pulls
-- each CREATE TYPE body out, and asserts name + order + type against the
-- descriptors. Editing a type here without editing the descriptor (or the
-- other way round) fails the test suite.
--
-- Conventions:
--   * TVP column order = target table COLUMN order (not primary key order --
--     see the strip type below, where they differ).
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * NOT NULL marks a column the reader treats as REQUIRED: a record missing
--     it is DROPPED rather than merged under a blank key. Every primary key
--     component is required.
--
-- ExecutionDate and CommodityType ARE TVP columns even though neither is read
-- from the response. ExecutionDate is the snapshot stamp and a primary key
-- component, so the server has to receive it; stamping it in the proc with
-- CAST(SYSDATETIME() AS DATE) instead would put the key on the SERVER's clock
-- and time zone rather than the loader's, and would fork the key for a run
-- that straddles midnight. CommodityType is a loader-supplied constant. See
-- 001 and docs/design/NGX.md.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in
-- order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.IndexPriceTvp
--   -> arm.IndexPrice
-- 23 columns: the table's 24 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.IndexPriceTvp') IS NULL
BEGIN
    CREATE TYPE arm.IndexPriceTvp AS TABLE
    (
        ExecutionDate               DATE            NOT NULL,
        IndexId                     INT             NOT NULL,
        PriceEffectiveStart         DATE            NOT NULL,
        PriceEffectiveEnd           DATE            NOT NULL,
        SourceDataDeliveryStart     DATE            NULL,
        SourceDataDeliveryEnd       DATE            NULL,
        CommodityType               VARCHAR(50)     NULL,
        Id                          VARCHAR(50)     NULL,
        IndexName                   VARCHAR(500)    NULL,
        PriceAmount                 DECIMAL(18, 8)  NULL,
        PriceCurrency               VARCHAR(50)     NULL,
        Duration                    INT             NULL,
        TradedAmount                DECIMAL(18, 8)  NULL,
        TradedUnit                  VARCHAR(50)     NULL,
        TradedContractUnit          VARCHAR(50)     NULL,
        TradedTotalAmount           DECIMAL(18, 8)  NULL,
        AlternateTradedAmount       DECIMAL(18, 8)  NULL,
        AlternateTradedUnit         VARCHAR(50)     NULL,
        AlternateTradedContractUnit VARCHAR(50)     NULL,
        AlternateTradedTotalAmount  DECIMAL(18, 8)  NULL,
        NumberOfTrades              INT             NULL,
        SettlementState             VARCHAR(50)     NULL,
        LastUpdatedDate             DATETIME2(7)    NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.StripTradingSummaryTvp
--   -> arm.StripTradingSummary
-- 21 columns: the table's 22 less ModifiedAtUtc.
--
-- Column order follows the TABLE's declaration order (TradeDateTime first),
-- NOT the primary key's order (HubId first). A TVP binds by position against
-- the DataTable, and the DataTable is built from the same descriptor list.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.StripTradingSummaryTvp') IS NULL
BEGIN
    CREATE TYPE arm.StripTradingSummaryTvp AS TABLE
    (
        TradeDateTime            DATETIME2(0)   NOT NULL,
        HubId                    INT            NOT NULL,
        MarketId                 INT            NOT NULL,
        StripType                VARCHAR(256)   NOT NULL,
        ExchangeReference        VARCHAR(256)   NOT NULL,
        BeginDate                DATE           NOT NULL,
        EndDate                  DATE           NOT NULL,
        HubName                  VARCHAR(50)    NULL,
        MarketName               VARCHAR(256)   NULL,
        SettlementTitle          VARCHAR(256)   NULL,
        Cleared                  BIT            NULL,
        TradedVolumeAmount       DECIMAL(18, 8) NULL,
        TradedVolumeUnit         VARCHAR(50)    NULL,
        TotalVolumeAmount        DECIMAL(18, 8) NULL,
        TotalVolumeUnit          VARCHAR(50)    NULL,
        TotalVolumeinTJ          DECIMAL(18, 8) NULL,
        PriceAmount              DECIMAL(18, 8) NULL,
        PriceCurrency            VARCHAR(50)    NULL,
        BrokerCompanyName        VARCHAR(256)   NULL,
        RequestForQuoteIndicator BIT            NULL,
        IncludeInIndexIndicator  BIT            NULL
    );
END
GO
