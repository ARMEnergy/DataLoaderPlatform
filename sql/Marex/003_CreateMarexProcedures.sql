-- =============================================================================
-- 003_CreateMarexProcedures.sql
-- Database : Marex
-- Schema   : arm
--
--   arm.usp_BulkMergeClosingPrice     -> arm.ClosingPrice
--   arm.usp_BulkMergeMarketStatistic  -> arm.MarketStatistic
--   arm.usp_BulkMergePeriod           -> arm.Period
--   arm.usp_BulkMergePeriodGroup      -> arm.PeriodGroup
--   arm.usp_BulkMergeProduct          -> arm.Product
--   arm.usp_ValidateLoad              post-load observational anomaly report
--
-- Shared conventions across all five merge procs:
--
--   MERGE BY PRIMARY KEY. Each predicate lists the full declared primary key
--   and nothing else, which is what the loader spec asks for. For the two fact
--   tables that is (date, vendor id); for the three dimensions it is the vendor
--   id alone, so a run REFRESHES them in place.
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY <key>), keep row 1. It should never fire:
--   all five keys were verified unique WITHIN a live snapshot (4,227/4,227
--   distinct ClosingPriceIds, 266/266 MarketStatistic ids, 1,666/1,666
--   products, 724/724 periods, 6/6 period groups), and each feed produces
--   exactly one work unit per run, so there is no second batch to collide
--   with. The guard stays because MERGE does not degrade gracefully: a single
--   duplicated source key aborts the whole batch with "attempted to update the
--   same row more than once", and a vendor that starts emitting a repeated row
--   would otherwise take the loader down rather than merely repeat itself.
--
--   ORDER-INDEPENDENT DE-DUP. ORDER BY (SELECT 1) makes the surviving row
--   arbitrary, which is only safe because a duplicate is not expected to exist
--   at all. It is NOT a "last write wins" policy -- if duplicates ever do
--   appear they collapse silently, so a vendor change here needs a real
--   tie-break rule, not a bigger batch.
--
--   NO DELETE-BY-ABSENCE. None of the merges has WHEN NOT MATCHED BY SOURCE
--   THEN DELETE.
--     * For the fact tables it would be actively destructive: each batch is ONE
--       trading day's snapshot, so deleting rows absent from it would wipe every
--       other ExchangeDate in the table.
--     * For the dimensions it is a deliberate choice rather than an oversight.
--       A product the vendor retires stops appearing in the snapshot, and this
--       loader LEAVES it, because arm.ClosingPrice and arm.MarketStatistic rows
--       from previous days still reference that ProductId and would otherwise
--       lose the only row that says what it was. The cost is that the dimension
--       accumulates dead rows; ModifiedAtUtc is how you tell -- a product that
--       is still live has a ModifiedAtUtc from the most recent run.
--
--   ModifiedAtUtc is set explicitly with SYSUTCDATETIME(), so rows written by
--   this loader hold true UTC even though the table DEFAULT is sysdatetime().
--
--   Each returns SELECT ... AS RecordsProcessed, which SqlSinkBase surfaces to
--   core.LoadLog.
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeClosingPrice
--   -> arm.ClosingPrice
--
-- Key: (ExchangeDate, ClosingPriceId).
-- ExchangeDate leads, so a re-run during the SAME trading day updates that
-- day's snapshot in place (the loader's hourly resume key re-reads it), while
-- the next trading day inserts a new generation rather than overwriting.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeClosingPrice
    @Records arm.ClosingPriceTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY ExchangeDate, ClosingPriceId
                   ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ClosingPrice AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.ExchangeDate   = s.ExchangeDate
       AND tgt.ClosingPriceId = s.ClosingPriceId
    WHEN MATCHED THEN UPDATE SET
        ProductId     = s.ProductId,
        PeriodId      = s.PeriodId,
        Price         = s.Price,
        [Time]        = s.[Time],
        PreviousPrice = s.PreviousPrice,
        PreviousTime  = s.PreviousTime,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ExchangeDate, ClosingPriceId, ProductId, PeriodId,
                Price, [Time], PreviousPrice, PreviousTime, ModifiedAtUtc)
        VALUES (s.ExchangeDate, s.ClosingPriceId, s.ProductId, s.PeriodId,
                s.Price, s.[Time], s.PreviousPrice, s.PreviousTime, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeMarketStatistic
--   -> arm.MarketStatistic
--
-- Key: (TradeDate, Id).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeMarketStatistic
    @Records arm.MarketStatisticTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY TradeDate, Id
                   ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.MarketStatistic AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate = s.TradeDate
       AND tgt.Id        = s.Id
    WHEN MATCHED THEN UPDATE SET
        CciIndex               = s.CciIndex,
        [Index]                = s.[Index],
        TransactionCount       = s.TransactionCount,
        [Average]              = s.[Average],
        PcChangeOnDay          = s.PcChangeOnDay,
        ChangeOnDay            = s.ChangeOnDay,
        [Change]               = s.[Change],
        CumQty                 = s.CumQty,
        [Open]                 = s.[Open],
        [Low]                  = s.[Low],
        [High]                 = s.[High],
        MidPrice               = s.MidPrice,
        SettlementPrice        = s.SettlementPrice,
        LastTradeTime          = s.LastTradeTime,
        LastTradeEventSource   = s.LastTradeEventSource,
        LastTradeAggressorSide = s.LastTradeAggressorSide,
        LastTradePrice         = s.LastTradePrice,
        [State]                = s.[State],
        Pipeline               = s.Pipeline,
        [Location]             = s.[Location],
        ProductId              = s.ProductId,
        [Period]               = s.[Period],
        Product                = s.Product,
        UpdateReason           = s.UpdateReason,
        ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Id, CciIndex, [Index], TransactionCount,
                [Average], PcChangeOnDay, ChangeOnDay, [Change], CumQty,
                [Open], [Low], [High], MidPrice, SettlementPrice,
                LastTradeTime, LastTradeEventSource, LastTradeAggressorSide, LastTradePrice,
                [State], Pipeline, [Location], ProductId, [Period], Product, UpdateReason,
                ModifiedAtUtc)
        VALUES (s.TradeDate, s.Id, s.CciIndex, s.[Index], s.TransactionCount,
                s.[Average], s.PcChangeOnDay, s.ChangeOnDay, s.[Change], s.CumQty,
                s.[Open], s.[Low], s.[High], s.MidPrice, s.SettlementPrice,
                s.LastTradeTime, s.LastTradeEventSource, s.LastTradeAggressorSide, s.LastTradePrice,
                s.[State], s.Pipeline, s.[Location], s.ProductId, s.[Period], s.Product, s.UpdateReason,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergePeriod
--   -> arm.Period
--
-- Key: (PeriodId). A dimension refresh -- see the DELETE-BY-ABSENCE note above.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePeriod
    @Records arm.PeriodTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY PeriodId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Period AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.PeriodId = s.PeriodId
    WHEN MATCHED THEN UPDATE SET
        [Name]           = s.[Name],
        DeliveryStart    = s.DeliveryStart,
        DeliveryEnd      = s.DeliveryEnd,
        TradingStart     = s.TradingStart,
        TradingEnd       = s.TradingEnd,
        NoticeOfShipment = s.NoticeOfShipment,
        PeriodGroupId    = s.PeriodGroupId,
        ExternalId       = s.ExternalId,
        ExternalSourceId = s.ExternalSourceId,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PeriodId, [Name], DeliveryStart, DeliveryEnd, TradingStart, TradingEnd,
                NoticeOfShipment, PeriodGroupId, ExternalId, ExternalSourceId, ModifiedAtUtc)
        VALUES (s.PeriodId, s.[Name], s.DeliveryStart, s.DeliveryEnd, s.TradingStart, s.TradingEnd,
                s.NoticeOfShipment, s.PeriodGroupId, s.ExternalId, s.ExternalSourceId, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergePeriodGroup
--   -> arm.PeriodGroup
--
-- Key: (PeriodGroupId).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePeriodGroup
    @Records arm.PeriodGroupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY PeriodGroupId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.PeriodGroup AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.PeriodGroupId = s.PeriodGroupId
    WHEN MATCHED THEN UPDATE SET
        [Name]           = s.[Name],
        DisplayName      = s.DisplayName,
        IsHidden         = s.IsHidden,
        ExternalId       = s.ExternalId,
        ExternalSourceId = s.ExternalSourceId,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PeriodGroupId, [Name], DisplayName, IsHidden, ExternalId, ExternalSourceId, ModifiedAtUtc)
        VALUES (s.PeriodGroupId, s.[Name], s.DisplayName, s.IsHidden, s.ExternalId, s.ExternalSourceId,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeProduct
--   -> arm.Product
--
-- Key: (ProductId).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeProduct
    @Records arm.ProductTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY ProductId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Product AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.ProductId = s.ProductId
    WHEN MATCHED THEN UPDATE SET
        ExternalSourceId           = s.ExternalSourceId,
        ExternalId                 = s.ExternalId,
        PipelineName               = s.PipelineName,
        PipelineId                 = s.PipelineId,
        IndexName                  = s.IndexName,
        IndexId                    = s.IndexId,
        LocationName               = s.LocationName,
        LocationId                 = s.LocationId,
        GradeName                  = s.GradeName,
        GradeId                    = s.GradeId,
        CounterpartyDetailsVisible = s.CounterpartyDetailsVisible,
        GroupName                  = s.GroupName,
        GroupId                    = s.GroupId,
        ClearingHouseId            = s.ClearingHouseId,
        MarketName                 = s.MarketName,
        MarketId                   = s.MarketId,
        ProductType                = s.ProductType,
        [Name]                     = s.[Name],
        TradeChartDataSource       = s.TradeChartDataSource,
        ModifiedAtUtc              = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProductId, ExternalSourceId, ExternalId,
                PipelineName, PipelineId, IndexName, IndexId,
                LocationName, LocationId, GradeName, GradeId,
                CounterpartyDetailsVisible, GroupName, GroupId, ClearingHouseId,
                MarketName, MarketId, ProductType, [Name], TradeChartDataSource,
                ModifiedAtUtc)
        VALUES (s.ProductId, s.ExternalSourceId, s.ExternalId,
                s.PipelineName, s.PipelineId, s.IndexName, s.IndexId,
                s.LocationName, s.LocationId, s.GradeName, s.GradeId,
                s.CounterpartyDetailsVisible, s.GroupName, s.GroupId, s.ClearingHouseId,
                s.MarketName, s.MarketId, s.ProductType, s.[Name], s.TradeChartDataSource,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad
--
-- Observational only: it reports, it never changes anything and never fails a
-- run. @ExchangeDate defaults to the most recent date present in
-- arm.ClosingPrice.
--
-- Each check below is something that would NOT raise an error on its own but
-- would mean the load is wrong.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @ExchangeDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @ExchangeDate IS NULL
        SELECT @ExchangeDate = MAX(ExchangeDate) FROM arm.ClosingPrice;

    SELECT Anomaly, Detail, RowCnt
    FROM (
        -- Volumes. Measured live 2026-09-21: 4,227 closing prices, 266 market
        -- statistics, 1,666 products, 724 periods, 6 period groups.
        SELECT 'RowCount: ClosingPrice'    AS Anomaly,
               CONVERT(VARCHAR(30), @ExchangeDate) AS Detail,
               COUNT_BIG(*) AS RowCnt
        FROM arm.ClosingPrice WHERE ExchangeDate = @ExchangeDate
        UNION ALL
        SELECT 'RowCount: MarketStatistic', CONVERT(VARCHAR(30), @ExchangeDate), COUNT_BIG(*)
        FROM arm.MarketStatistic WHERE TradeDate = @ExchangeDate
        UNION ALL
        SELECT 'RowCount: Product', 'all', COUNT_BIG(*) FROM arm.Product
        UNION ALL
        SELECT 'RowCount: Period', 'all', COUNT_BIG(*) FROM arm.Period
        UNION ALL
        SELECT 'RowCount: PeriodGroup', 'all', COUNT_BIG(*) FROM arm.PeriodGroup

        UNION ALL
        -- A year-1 timestamp means the loader's NullIfUnset guard has been
        -- bypassed: the vendor sends 0001-01-01 for "no value" and DATETIME2
        -- accepts it silently. See 001 note 4.
        SELECT 'Year-1 timestamp (should be NULL)', 'ClosingPrice.Time/PreviousTime', COUNT_BIG(*)
        FROM arm.ClosingPrice
        WHERE YEAR(ISNULL([Time], '2000-01-01')) <= 1
           OR YEAR(ISNULL(PreviousTime, '2000-01-01')) <= 1
        UNION ALL
        SELECT 'Year-1 timestamp (should be NULL)', 'MarketStatistic.LastTradeTime', COUNT_BIG(*)
        FROM arm.MarketStatistic
        WHERE YEAR(ISNULL(LastTradeTime, '2000-01-01')) <= 1

        UNION ALL
        -- An enum column holding digits means the gateway sent a value the
        -- vendored SDK does not define -- i.e. lib/neon is behind the server.
        SELECT 'Numeric enum name (SDK behind gateway)', 'MarketStatistic.State', COUNT_BIG(*)
        FROM arm.MarketStatistic WHERE [State] NOT LIKE '%[A-Za-z]%' AND [State] IS NOT NULL
        UNION ALL
        SELECT 'Numeric enum name (SDK behind gateway)', 'MarketStatistic.UpdateReason', COUNT_BIG(*)
        FROM arm.MarketStatistic WHERE UpdateReason NOT LIKE '%[A-Za-z]%' AND UpdateReason IS NOT NULL
        UNION ALL
        SELECT 'Numeric enum name (SDK behind gateway)', 'Product.ProductType', COUNT_BIG(*)
        FROM arm.Product WHERE ProductType NOT LIKE '%[A-Za-z]%' AND ProductType IS NOT NULL

        UNION ALL
        -- Referential drift. No FKs are declared (see docs/design/Marex.md), so
        -- these are the only thing that would notice a fact row pointing at a
        -- product or period the dimension load missed.
        SELECT 'Orphan: ClosingPrice.ProductId not in arm.Product',
               CONVERT(VARCHAR(30), @ExchangeDate), COUNT_BIG(*)
        FROM arm.ClosingPrice c
        WHERE c.ExchangeDate = @ExchangeDate
          AND c.ProductId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Product p WHERE p.ProductId = c.ProductId)
        UNION ALL
        SELECT 'Orphan: ClosingPrice.PeriodId not in arm.Period',
               CONVERT(VARCHAR(30), @ExchangeDate), COUNT_BIG(*)
        FROM arm.ClosingPrice c
        WHERE c.ExchangeDate = @ExchangeDate
          AND c.PeriodId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Period p WHERE p.PeriodId = c.PeriodId)
        UNION ALL
        SELECT 'Orphan: MarketStatistic.ProductId not in arm.Product',
               CONVERT(VARCHAR(30), @ExchangeDate), COUNT_BIG(*)
        FROM arm.MarketStatistic m
        WHERE m.TradeDate = @ExchangeDate
          AND m.ProductId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Product p WHERE p.ProductId = m.ProductId)
        UNION ALL
        SELECT 'Orphan: Period.PeriodGroupId not in arm.PeriodGroup', 'all', COUNT_BIG(*)
        FROM arm.Period pr
        WHERE pr.PeriodGroupId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.PeriodGroup pg WHERE pg.PeriodGroupId = pr.PeriodGroupId)

        UNION ALL
        -- A whole snapshot of nothing but nulls means the mapper is writing to
        -- the wrong columns -- the TVP-shift failure mode 002 warns about.
        SELECT 'All-NULL price columns (possible TVP shift)',
               CONVERT(VARCHAR(30), @ExchangeDate), COUNT_BIG(*)
        FROM arm.ClosingPrice
        WHERE ExchangeDate = @ExchangeDate AND Price IS NULL AND PreviousPrice IS NULL
    ) AS checks
    WHERE RowCnt > 0
    ORDER BY Anomaly, Detail;
END
GO
