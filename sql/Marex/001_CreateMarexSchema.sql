-- =============================================================================
-- 001_CreateMarexSchema.sql
-- Database : Marex             (create/select it before running this script)
-- Schema   : arm
--
-- Marex "Neon" crude market gateway, reached through the vendor's own .NET SDK
-- (NeonMarkets.NeonAPI 3.15.1.22, vendored in lib/neon).
-- Source contract verified live 2026-09-21 -- see docs/apis/Marex.md.
-- Loader design -- see docs/design/Marex.md.
--
-- 5 pipelines land in the 5 tables below, one each. All five arrive in a single
-- opening SNAPSHOT pushed down one SignalR websocket when the client connects:
--
--   PeriodGroupSnapshot     -> arm.PeriodGroup
--   PeriodSnapshot          -> arm.Period
--   ProductSnapshot         -> arm.Product
--   ClosingPriceSnapshot    -> arm.ClosingPrice
--   MarketStatisticSnapshot -> arm.MarketStatistic
--
-- ---------------------------------------------------------------------------
-- FOUR THINGS TO KNOW BEFORE READING THE DDL
-- ---------------------------------------------------------------------------
--
--   1. ExchangeDate / TradeDate ARE SOURCE DATA, NOT A LOAD STAMP. Neither
--      ClosingPriceDto nor MarketStatisticsDto carries a date. The gateway
--      pushes the trading day SEPARATELY, on its own ExchangeDateSnapshot
--      event, and the loader stamps it onto every row of both tables. It leads
--      both primary keys, so each trading day accumulates its own generation
--      of the whole set rather than overwriting the previous one.
--
--      Do NOT be tempted to default these in the proc with
--      CAST(SYSDATETIME() AS DATE). That would put a primary key component on
--      the SERVER's clock and time zone instead of the exchange's, forking the
--      key whenever the two disagree -- which they do for any run outside the
--      vendor's own day boundary, and for any run that straddles midnight.
--
--   2. THE DIMENSIONS HAVE NO DATE IN THEIR KEY, ON PURPOSE. arm.Product,
--      arm.Period and arm.PeriodGroup are keyed on the vendor's own id alone,
--      so a run REFRESHES them in place. They are current-state reference
--      data (1,666 products, 724 periods, 6 period groups when measured), not
--      a time series; keying them by date would multiply them by every run and
--      give the fact tables nothing to join to.
--
--   3. FIVE COLUMNS HOLD ENUM NAMES, NOT THE WIRE INTEGERS. The gateway sends
--      MarketStatistic.State, .LastTradeEventSource, .LastTradeAggressorSide
--      and .UpdateReason, and Product.ProductType and .TradeChartDataSource,
--      as integers. They are VARCHAR here because the supplied DDL made them
--      VARCHAR, so the loader writes the enum NAME ('Open', 'TradeMatched',
--      'Buy', 'Snapshot', 'Outright', 'Nbos'). A value the vendored SDK does
--      not define stringifies to its number, which lands a visible '37' rather
--      than silently mapping onto a neighbouring name.
--
--   4. NULLABLE TIMESTAMPS REALLY ARE NULLABLE. ClosingPriceDto.Time and
--      .PreviousTime are non-nullable DateTime in the SDK even though the
--      values are optional, so the vendor sends 0001-01-01T00:00:00Z for
--      "none" -- observed live on exactly the rows whose PreviousPrice is
--      NULL. DATETIME2's range starts at year 1, so such a value would store
--      silently and read back as a real timestamp. The loader converts year <= 1
--      to NULL (see MarexMapper.NullIfUnset).
--
-- The supplied DDL is reproduced VERBATIM -- column names, types, nullability,
-- primary key column ORDER, the ModifiedAtUtc default, and the supplied
-- constraint names (including PK_ARM_MarketStatisticSnapshot, whose name does
-- not match its table -- kept as given rather than silently "corrected", since
-- it may already exist in a sibling environment). The only additions are a NAME
-- for each ModifiedAtUtc default constraint (an unnamed default gets a random
-- system name that cannot later be dropped by name) and one nonclustered index
-- per table.
--
-- One oddity is kept ON PURPOSE rather than silently "fixed":
-- ModifiedAtUtc DEFAULT (sysdatetime()) is server LOCAL time in a column named
-- ...Utc. Every row this loader writes gets SYSUTCDATETIME() set explicitly by
-- the merge procs, so the DEFAULT only affects rows inserted by something else.
-- (Same posture as sql/NGX/001, sql/Genscape/001, sql/ICE/001 and
-- sql/Criterion/001.)
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- ----------------------------------------------------------------------------
-- arm.ClosingPrice
--   <- ClosingPriceSnapshot / ClosingPriceDto
--
-- ~4,227 rows per trading day when measured.
--
-- ClosingPriceId is the vendor's own Id, a composite "{productId}:{periodId}"
-- string (widest observed: 9 characters against VARCHAR(50)). It is NOT
-- globally unique over time -- the same pair recurs every trading day -- which
-- is exactly why ExchangeDate leads the key.
--
-- ProductId is INT here while arm.Product.ProductId is BIGINT. That is the
-- vendor's own inconsistency (ClosingPriceDto.ProductId is Int32,
-- ProductDto.Id is Int64), reproduced as supplied. Live ids are ~4 digits, so
-- neither is at risk; a join between the two will widen the INT side.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ClosingPrice', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ClosingPrice
    (
        ExchangeDate    DATE            NOT NULL,
        ClosingPriceId  VARCHAR(50)     NOT NULL,
        ProductId       INT             NULL,
        PeriodId        BIGINT          NULL,
        Price           DECIMAL(18, 8)  NULL,
        [Time]          DATETIME2(7)    NULL,
        PreviousPrice   DECIMAL(18, 8)  NULL,
        PreviousTime    DATETIME2(7)    NULL,
        ModifiedAtUtc   DATETIME2(3)    NULL
            CONSTRAINT DF_ARM_ClosingPrice_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_ClosingPrice PRIMARY KEY CLUSTERED (ExchangeDate, ClosingPriceId)
    );

    -- The clustered key leads on ExchangeDate, which serves "the whole book on
    -- day X" but nothing else. This one serves the other obvious question --
    -- one product/period's closing price over time.
    CREATE NONCLUSTERED INDEX IX_ARM_ClosingPrice_Product_Period_Date
        ON arm.ClosingPrice (ProductId, PeriodId, ExchangeDate)
        INCLUDE (Price, [Time]);
END
GO

-- ----------------------------------------------------------------------------
-- arm.MarketStatistic
--   <- MarketStatisticSnapshot / MarketStatisticsDto
--
-- ~266 rows per trading day when measured: one per live instrument, not one
-- per product (1,666 products exist; only the instruments actually quoted
-- appear here).
--
-- Id is the vendor's instrument id and recurs daily, so TradeDate leads the key
-- for the same reason as arm.ClosingPrice.
--
-- The DTO's Bids, Asks (OtcPriceDto[]), PeriodIds (BIGINT[]) and
-- ExtensionValues collections have NO columns in the supplied schema and are
-- dropped by the loader. Period and Product are carried as the vendor's own
-- display strings; the joinable key is ProductId.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.MarketStatistic', 'U') IS NULL
BEGIN
    CREATE TABLE arm.MarketStatistic
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
        UpdateReason            VARCHAR(50)     NULL,
        ModifiedAtUtc           DATETIME2(3)    NULL
            CONSTRAINT DF_ARM_MarketStatistic_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        -- Constraint name as supplied. It says "Snapshot" while the table does
        -- not; kept verbatim rather than corrected.
        CONSTRAINT PK_ARM_MarketStatisticSnapshot PRIMARY KEY CLUSTERED (TradeDate, Id)
    );

    CREATE NONCLUSTERED INDEX IX_ARM_MarketStatistic_Product_Date
        ON arm.MarketStatistic (ProductId, TradeDate)
        INCLUDE (SettlementPrice, LastTradePrice, CumQty, TransactionCount);
END
GO

-- ----------------------------------------------------------------------------
-- arm.Period
--   <- PeriodSnapshot / PeriodDto
--
-- 724 rows when measured. A dimension: keyed on the vendor id, refreshed in
-- place by every run.
--
-- The four DATE columns come from DTO properties typed DateTime. Truncating to
-- the date part is safe against this source and was CHECKED rather than
-- assumed: across all 724 live periods (2,896 date values) the only
-- non-midnight values are intraday trading stamps and three DeliveryEnds at
-- 23:59, whose date part is already the intended day. Nothing sits at 23:00Z,
-- which is the pattern that would betray a London-midnight-expressed-as-UTC
-- value and make truncation off by one.
--
-- NoticeOfShipment is DATETIME2(7) and keeps its time -- the live values are
-- all 23:59:59, which a DATE column would have thrown away.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Period', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Period
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
        ExternalSourceId    INT             NULL,
        ModifiedAtUtc       DATETIME2(3)    NULL
            CONSTRAINT DF_ARM_Period_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Period PRIMARY KEY CLUSTERED (PeriodId)
    );

    CREATE NONCLUSTERED INDEX IX_ARM_Period_PeriodGroup_Delivery
        ON arm.Period (PeriodGroupId, DeliveryStart);
END
GO

-- ----------------------------------------------------------------------------
-- arm.PeriodGroup
--   <- PeriodGroupSnapshot / PeriodGroupDto
--
-- 6 rows when measured (Months, Quarters, Day, ...). A dimension.
--
-- PeriodGroupId is BIGINT here (PeriodGroupDto.Id is Int64) while the
-- referencing arm.Period.PeriodGroupId is INT (PeriodDto.PeriodGroupId is
-- Int32). Another vendor inconsistency reproduced as supplied; live ids are
-- 4 digits. No FK is declared -- see docs/design/Marex.md.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.PeriodGroup', 'U') IS NULL
BEGIN
    CREATE TABLE arm.PeriodGroup
    (
        PeriodGroupId       BIGINT          NOT NULL,
        [Name]              VARCHAR(256)    NULL,
        DisplayName         VARCHAR(256)    NULL,
        IsHidden            BIT             NULL,
        ExternalId          BIGINT          NULL,
        ExternalSourceId    INT             NULL,
        ModifiedAtUtc       DATETIME2(3)    NULL
            CONSTRAINT DF_ARM_PeriodGroup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_PeriodGroup PRIMARY KEY CLUSTERED (PeriodGroupId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.Product
--   <- ProductSnapshot / ProductDto
--
-- 1,666 rows when measured. A dimension, and the join target for
-- arm.ClosingPrice.ProductId and arm.MarketStatistic.ProductId.
--
-- Only the DTO's scalars have columns here. Its nested ClearingHouse,
-- MarketProperties (a 30-field trading-rules object), TradingUnit,
-- StrategyLegs, ExtensionValues and PeriodGroupIds are dropped by the loader;
-- ClearingHouseId comes from the scalar property, which is populated even when
-- the nested ClearingHouse object is null.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Product', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Product
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
        TradeChartDataSource        VARCHAR(256)    NULL,
        ModifiedAtUtc               DATETIME2(3)    NULL
            CONSTRAINT DF_ARM_Product_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Product PRIMARY KEY CLUSTERED (ProductId)
    );

    CREATE NONCLUSTERED INDEX IX_ARM_Product_Market_Index
        ON arm.Product (MarketId, IndexId)
        INCLUDE ([Name], ProductType);
END
GO
