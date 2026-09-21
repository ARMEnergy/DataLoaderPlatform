-- =============================================================================
-- 001_CreateNgxSchema.sql
-- Database : NGX              (create/select it before running this script)
-- Schema   : arm
--
-- ICE NGX Clearing legacy XML web services (https://ngxclearing.ice.com/ngxcs).
-- Source contract verified live 2026-09-18 -- see docs/apis/NGX.md.
-- Loader design -- see docs/design/NGX.md.
--
-- 2 pipelines land in the 2 tables below, one each:
--
--   GET /ngxcs/indexPrice.xml              -> arm.IndexPrice
--   GET /ngxcs/stripTradingSummaryXml.xml  -> arm.StripTradingSummary
--
-- ---------------------------------------------------------------------------
-- FIVE THINGS TO KNOW BEFORE READING THE DDL
-- ---------------------------------------------------------------------------
--
--   1. THESE TABLES SHADOW LIVE LEGACY ONES. This database already contains
--      dbo.IndexPrice (2.9M rows) and dbo.StripTradingSummary (8.6M rows),
--      both still being written TODAY by the incumbent Conduit process. The
--      arm.* tables below are a re-platforming target and are column-identical
--      to their dbo.* counterparts except that the Conduit bookkeeping columns
--      (dbo.IndexPrice.ConduitLastUpdate, dbo.StripTradingSummary.Checksum and
--      .ConduitLastUpdate) are replaced by the platform's ModifiedAtUtc.
--      Nothing here reads, writes or drops the dbo.* tables.
--
--   2. ExecutionDate IS A SNAPSHOT STAMP, NOT SOURCE DATA. The index endpoint
--      returns the current state of a delivery window; ExecutionDate records
--      WHEN we asked. It leads the primary key, so each run adds a fresh
--      generation of the whole window rather than overwriting the previous
--      one -- which is what makes the settlement walk (Projected -> Pending ->
--      Settled) observable after the fact. The incumbent writes ~3,800 rows
--      per ExecutionDate across 39 active indices; expect the same order here.
--
--   3. ALL TIMESTAMPS ARE US CENTRAL, converted from the source's OWN offset.
--      The XML stamps Mountain (-07:00 in winter, -06:00 in summer);
--      LastUpdatedDate and TradeDateTime are both converted to America/Chicago
--      by the LOADER before they reach these tables, matching the incumbent
--      byte-for-byte (verified on both sides of the DST boundary -- see
--      docs/apis/NGX.md section 6). This is a silent one-hour error if you get
--      it wrong, and TradeDateTime is in the strip primary key.
--
--   4. ExchangeReference IS RECYCLED. It is not a globally unique trade id --
--      the same value (e.g. 48000000003842) was observed on 2025-05-20,
--      2025-08-11 and 2026-09-01 against different hubs and markets. That is
--      precisely why the strip primary key carries all seven columns instead
--      of keying on the reference alone. Do not "simplify" this key.
--
--   5. FOUR INDEX COLUMNS ARE STRUCTURALLY NULL on this endpoint.
--      CommodityType is a CONSTANT supplied by the loader -- indexPrice.xml
--      emits no such element -- and the four AlternateTraded* columns have no
--      source element at all here. They are not dead weight: they belong to
--      the CRUDE index endpoint, which the retired dbo.IndexPrice_2
--      (2022-03..2023-11) shows populated them for its Crude Oil rows
--      (45,046 of 177,145) and never for a Natural Gas row (0 of 1,189,358).
--      The columns are kept so a future crude pipeline can land in this same
--      table. See docs/design/NGX.md section 7.
--
-- The supplied DDL is reproduced VERBATIM -- column names, types, nullability,
-- primary key column ORDER and the ModifiedAtUtc default. The only additions
-- are a NAME for that default constraint (an unnamed default gets a random
-- system name that cannot later be dropped by name) and one nonclustered index
-- per table.
--
-- One oddity is kept ON PURPOSE rather than silently "fixed":
-- ModifiedAtUtc DEFAULT (sysdatetime()) is server LOCAL time in a column named
-- ...Utc. Every row this loader writes gets SYSUTCDATETIME() set explicitly by
-- the merge procs, so the DEFAULT only affects rows inserted by something else.
-- (Same posture as sql/Genscape/001, sql/ICE/001 and sql/Criterion/001.)
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- ----------------------------------------------------------------------------
-- arm.IndexPrice
--   <- GET /ngxcs/indexPrice.xml?indexId=..&effectiveStart=..&effectiveEnd=..
--
-- One row per (snapshot date, index, delivery window). The loader requests the
-- indices listed in dbo.[Index] WHERE IndexType = 'IndexPrice' (47 of the 219
-- rows there; the other 172 are CrudeIndexPrice and are NOT entitled on this
-- endpoint -- a request naming one returns 403 for the WHOLE batch).
--
-- Duration is the delivery window length in days as the vendor counts it, not
-- a derived value. TradedAmount/Unit/ContractUnit/TotalAmount arrive together
-- inside an OPTIONAL quantityTraded element and are all NULL together when it
-- is absent (observed on 45% of rows; the incumbent shows the same split).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.IndexPrice', 'U') IS NULL
BEGIN
    CREATE TABLE arm.IndexPrice
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
        LastUpdatedDate             DATETIME2(7)    NULL,
        ModifiedAtUtc               DATETIME2(3)    NULL
            CONSTRAINT DF_ARM_IndexPrice_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_IndexPrice PRIMARY KEY CLUSTERED
        (
            ExecutionDate       ASC,
            IndexId             ASC,
            PriceEffectiveStart ASC,
            PriceEffectiveEnd   ASC
        )
    );
END
GO

-- The clustered PK leads with ExecutionDate, which serves "what did today's
-- snapshot look like". This covers the other common slice -- one index's price
-- history for a delivery date across snapshots.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_IndexPrice_Index_Effective' AND object_id = OBJECT_ID('arm.IndexPrice'))
    CREATE NONCLUSTERED INDEX IX_ARM_IndexPrice_Index_Effective
        ON arm.IndexPrice (IndexId, PriceEffectiveStart, ExecutionDate)
        INCLUDE (PriceAmount, PriceCurrency, SettlementState, NumberOfTrades);
GO

-- ----------------------------------------------------------------------------
-- arm.StripTradingSummary
--   <- GET /ngxcs/stripTradingSummaryXml.xml?grouping=Hub&tradeStartDate=..
--
-- One row per cleared/reported trade. ~1,300-1,600 rows per weekday and
-- ~250-400 per weekend day; a full calendar month is ~36,000 rows / 29 MB of
-- XML, which is why the loader chunks the window rather than asking for the
-- whole thing at once.
--
-- NOTE the primary key column order: (HubId, MarketId, StripType,
-- TradeDateTime, BeginDate, EndDate, ExchangeReference). That is NOT the
-- column declaration order -- TradeDateTime is declared first but keys fourth.
-- That is what the supplied DDL asks for and it is reproduced as-is; the merge
-- matches on the same seven-column set, so the ordering is a physical-layout
-- choice only. The TVP in 002 follows the COLUMN order, not this one.
--
-- BrokerCompanyName is nullable and, on current data, always NULL: the element
-- is absent from every response sampled in 2026 and from a 2023 replay, while
-- the incumbent holds non-NULL values only for bilateral broker trades in and
-- before 2023. The loader reads the element when present rather than assuming.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.StripTradingSummary', 'U') IS NULL
BEGIN
    CREATE TABLE arm.StripTradingSummary
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
        IncludeInIndexIndicator  BIT            NULL,
        ModifiedAtUtc            DATETIME2(3)   NULL
            CONSTRAINT DF_ARM_StripTradingSummary_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_StripTradingSummary PRIMARY KEY CLUSTERED
        (
            HubId             ASC,
            MarketId          ASC,
            StripType         ASC,
            TradeDateTime     ASC,
            BeginDate         ASC,
            EndDate           ASC,
            ExchangeReference ASC
        )
    );
END
GO

-- The clustered PK leads with HubId, so a plain "what traded on day X across
-- all hubs" query -- the loader's own re-pull window, and the most common
-- analyst slice -- would otherwise scan. This covers it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_StripTradingSummary_TradeDateTime' AND object_id = OBJECT_ID('arm.StripTradingSummary'))
    CREATE NONCLUSTERED INDEX IX_ARM_StripTradingSummary_TradeDateTime
        ON arm.StripTradingSummary (TradeDateTime)
        INCLUDE (HubId, MarketId, StripType, PriceAmount, PriceCurrency, TradedVolumeAmount);
GO
