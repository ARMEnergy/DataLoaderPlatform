-- =============================================================================
-- 003_CreateNgxProcedures.sql
-- Database : NGX
-- Schema   : arm
--
--   arm.usp_BulkMergeIndexPrice           -> arm.IndexPrice
--   arm.usp_BulkMergeStripTradingSummary  -> arm.StripTradingSummary
--   arm.usp_ValidateLoad                  post-load observational anomaly report
--
-- Shared conventions across both merge procs:
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY <key>), keep row 1. It should never fire:
--   both primary keys were verified unique WITHIN a response (1,237/1,237
--   distinct index keys over a 9-month, 10-index pull; 1,601/1,601 distinct
--   strip keys over a trade day), and work units carve disjoint windows. The
--   guard stays because MERGE does not degrade gracefully: a single duplicated
--   source key aborts the whole batch with "attempted to update the same row
--   more than once", and a vendor that starts emitting a repeated row would
--   otherwise take the loader down rather than merely repeat itself.
--
--   ORDER-INDEPENDENT DE-DUP. ORDER BY (SELECT 1) makes the surviving row
--   arbitrary, which is only safe because a duplicate is not expected to exist
--   at all. It is NOT a "last write wins" policy -- if duplicates ever do
--   appear, arm.usp_ValidateLoad will not catch them (they collapse silently),
--   so a vendor change here needs a real tie-break rule, not a bigger batch.
--
--   NO DELETE-BY-ABSENCE. Neither merge has WHEN NOT MATCHED BY SOURCE THEN
--   DELETE. Each work unit carries one window of one batch of indices (or one
--   date chunk); deleting rows absent from it would wipe every other unit's
--   rows -- including, for the index table, every prior ExecutionDate.
--
--   MATCHING ON ALL KEY COLUMNS. Both MERGE predicates list the full declared
--   primary key. For the strip table that is seven columns, which looks
--   excessive until you know ExchangeReference is RECYCLED across dates and
--   markets (see 001 note 4) -- matching on fewer would collapse genuinely
--   distinct trades onto each other.
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
-- arm.usp_BulkMergeIndexPrice
--   -> arm.IndexPrice
--
-- Key: (ExecutionDate, IndexId, PriceEffectiveStart, PriceEffectiveEnd).
-- ExecutionDate leads, so a re-run on the SAME day updates that day's snapshot
-- in place (the loader's hot resume key re-pulls it), while the next day's run
-- inserts a new generation rather than overwriting.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIndexPrice
    @Records arm.IndexPriceTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY ExecutionDate, IndexId, PriceEffectiveStart, PriceEffectiveEnd
                   ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.IndexPrice AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.ExecutionDate       = s.ExecutionDate
       AND tgt.IndexId             = s.IndexId
       AND tgt.PriceEffectiveStart = s.PriceEffectiveStart
       AND tgt.PriceEffectiveEnd   = s.PriceEffectiveEnd
    WHEN MATCHED THEN UPDATE SET
        SourceDataDeliveryStart     = s.SourceDataDeliveryStart,
        SourceDataDeliveryEnd       = s.SourceDataDeliveryEnd,
        CommodityType               = s.CommodityType,
        Id                          = s.Id,
        IndexName                   = s.IndexName,
        PriceAmount                 = s.PriceAmount,
        PriceCurrency               = s.PriceCurrency,
        Duration                    = s.Duration,
        TradedAmount                = s.TradedAmount,
        TradedUnit                  = s.TradedUnit,
        TradedContractUnit          = s.TradedContractUnit,
        TradedTotalAmount           = s.TradedTotalAmount,
        AlternateTradedAmount       = s.AlternateTradedAmount,
        AlternateTradedUnit         = s.AlternateTradedUnit,
        AlternateTradedContractUnit = s.AlternateTradedContractUnit,
        AlternateTradedTotalAmount  = s.AlternateTradedTotalAmount,
        NumberOfTrades              = s.NumberOfTrades,
        SettlementState             = s.SettlementState,
        LastUpdatedDate             = s.LastUpdatedDate,
        ModifiedAtUtc               = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ExecutionDate, IndexId, PriceEffectiveStart, PriceEffectiveEnd,
                SourceDataDeliveryStart, SourceDataDeliveryEnd, CommodityType, Id, IndexName,
                PriceAmount, PriceCurrency, Duration,
                TradedAmount, TradedUnit, TradedContractUnit, TradedTotalAmount,
                AlternateTradedAmount, AlternateTradedUnit, AlternateTradedContractUnit,
                AlternateTradedTotalAmount, NumberOfTrades, SettlementState, LastUpdatedDate,
                ModifiedAtUtc)
        VALUES (s.ExecutionDate, s.IndexId, s.PriceEffectiveStart, s.PriceEffectiveEnd,
                s.SourceDataDeliveryStart, s.SourceDataDeliveryEnd, s.CommodityType, s.Id, s.IndexName,
                s.PriceAmount, s.PriceCurrency, s.Duration,
                s.TradedAmount, s.TradedUnit, s.TradedContractUnit, s.TradedTotalAmount,
                s.AlternateTradedAmount, s.AlternateTradedUnit, s.AlternateTradedContractUnit,
                s.AlternateTradedTotalAmount, s.NumberOfTrades, s.SettlementState, s.LastUpdatedDate,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeStripTradingSummary
--   -> arm.StripTradingSummary
--
-- Key: (HubId, MarketId, StripType, TradeDateTime, BeginDate, EndDate,
--       ExchangeReference) -- all seven. See 001 note 4.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStripTradingSummary
    @Records arm.StripTradingSummaryTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY HubId, MarketId, StripType, TradeDateTime,
                                BeginDate, EndDate, ExchangeReference
                   ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.StripTradingSummary AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.HubId             = s.HubId
       AND tgt.MarketId          = s.MarketId
       AND tgt.StripType         = s.StripType
       AND tgt.TradeDateTime     = s.TradeDateTime
       AND tgt.BeginDate         = s.BeginDate
       AND tgt.EndDate           = s.EndDate
       AND tgt.ExchangeReference = s.ExchangeReference
    WHEN MATCHED THEN UPDATE SET
        HubName                  = s.HubName,
        MarketName               = s.MarketName,
        SettlementTitle          = s.SettlementTitle,
        Cleared                  = s.Cleared,
        TradedVolumeAmount       = s.TradedVolumeAmount,
        TradedVolumeUnit         = s.TradedVolumeUnit,
        TotalVolumeAmount        = s.TotalVolumeAmount,
        TotalVolumeUnit          = s.TotalVolumeUnit,
        TotalVolumeinTJ          = s.TotalVolumeinTJ,
        PriceAmount              = s.PriceAmount,
        PriceCurrency            = s.PriceCurrency,
        BrokerCompanyName        = s.BrokerCompanyName,
        RequestForQuoteIndicator = s.RequestForQuoteIndicator,
        IncludeInIndexIndicator  = s.IncludeInIndexIndicator,
        ModifiedAtUtc            = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDateTime, HubId, MarketId, StripType, ExchangeReference,
                BeginDate, EndDate, HubName, MarketName, SettlementTitle, Cleared,
                TradedVolumeAmount, TradedVolumeUnit, TotalVolumeAmount, TotalVolumeUnit,
                TotalVolumeinTJ, PriceAmount, PriceCurrency, BrokerCompanyName,
                RequestForQuoteIndicator, IncludeInIndexIndicator, ModifiedAtUtc)
        VALUES (s.TradeDateTime, s.HubId, s.MarketId, s.StripType, s.ExchangeReference,
                s.BeginDate, s.EndDate, s.HubName, s.MarketName, s.SettlementTitle, s.Cleared,
                s.TradedVolumeAmount, s.TradedVolumeUnit, s.TotalVolumeAmount, s.TotalVolumeUnit,
                s.TotalVolumeinTJ, s.PriceAmount, s.PriceCurrency, s.BrokerCompanyName,
                s.RequestForQuoteIndicator, s.IncludeInIndexIndicator, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad
--
-- OBSERVATIONAL ONLY. Returns one row per anomaly; returns NOTHING when the
-- load looks healthy. It never throws and never changes data -- the loader
-- logs whatever comes back and the run's success is decided by the pipeline,
-- not by this proc.
--
-- @ExecutionDate is the snapshot the caller just wrote (the loader passes its
-- own US-Central "today", NOT the server's, so that a validate running either
-- side of midnight still checks the rows the run actually produced).
--
-- @IndexFeedRan / @StripFeedRan say which pipelines actually executed. A check
-- that fires because a feed was DISABLED is noise, and noise is what teaches an
-- operator to stop reading the channel that carries the real regressions:
-- without these flags, running only StripTradingSummary would report
-- IndexPriceNoSnapshot on every single run, forever.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @ExecutionDate DATE = NULL,
    @IndexFeedRan  BIT  = 1,
    @StripFeedRan  BIT  = 1
AS
BEGIN
    SET NOCOUNT ON;

    IF @ExecutionDate IS NULL
        SET @ExecutionDate = CAST(SYSDATETIME() AS DATE);

    DECLARE @findings TABLE
    (
        Check_  VARCHAR(60)   NOT NULL,
        Detail  NVARCHAR(400) NOT NULL,
        Metric  BIGINT        NULL
    );

    ------------------------------------------------------------------ index --
IF @IndexFeedRan = 1
BEGIN
    -- Nothing landed at all for the snapshot. Distinguishes "the run wrote
    -- nothing" from "the run wrote something odd" -- every other index check
    -- below is vacuously clean on an empty snapshot, so without this one an
    -- entirely failed load would report as healthy.
    IF NOT EXISTS (SELECT 1 FROM arm.IndexPrice WHERE ExecutionDate = @ExecutionDate)
        INSERT @findings VALUES
            ('IndexPriceNoSnapshot',
             CONCAT('No arm.IndexPrice rows for ExecutionDate ', CONVERT(CHAR(10), @ExecutionDate, 23)), 0);

    -- Row count moved sharply against the previous snapshot. The window is a
    -- rolling 3-months-back..6-months-forward span, so consecutive snapshots
    -- should differ by a handful of rows, not a third.
    --
    -- Bounded to the last 14 days: an unbounded "everything before today"
    -- aggregate scans the whole clustered index, which at ~3,800 rows per
    -- ExecutionDate is well over a million rows a year -- paid on every run, to
    -- find one number.
    ;WITH prev AS
    (
        SELECT TOP 1 ExecutionDate, COUNT_BIG(*) AS n
        FROM arm.IndexPrice
        WHERE ExecutionDate < @ExecutionDate
          AND ExecutionDate >= DATEADD(DAY, -14, @ExecutionDate)
        GROUP BY ExecutionDate
        ORDER BY ExecutionDate DESC
    ),
    cur AS
    (
        SELECT COUNT_BIG(*) AS n FROM arm.IndexPrice WHERE ExecutionDate = @ExecutionDate
    )
    INSERT @findings
    SELECT 'IndexPriceRowCountDrift',
           CONCAT('Snapshot has ', cur.n, ' row(s) vs ', prev.n, ' on ',
                  CONVERT(CHAR(10), prev.ExecutionDate, 23)),
           cur.n - prev.n
    FROM cur CROSS JOIN prev
    WHERE prev.n > 0
      AND ABS(cur.n - prev.n) * 10 > prev.n;   -- more than a 10% move

    -- Indices configured for loading that produced no rows.
    --
    -- Reported as ONE finding carrying a count, and only when that count MOVES
    -- against the previous snapshot. About 8 of the 47 are routinely empty --
    -- they simply have no trades in the window -- so a row-per-index report
    -- would put 8 warnings in the log on a perfectly healthy run and train
    -- everyone to scroll past this whole section. What actually matters is the
    -- number changing, which is what an entitlement revocation or a catalogue
    -- edit looks like.
    ;WITH missing_now AS
    (
        SELECT COUNT_BIG(*) AS n
        FROM dbo.[Index] i
        WHERE i.IndexType = 'IndexPrice'
          AND NOT EXISTS (SELECT 1 FROM arm.IndexPrice p
                          WHERE p.IndexId = i.IndexId AND p.ExecutionDate = @ExecutionDate)
    ),
    prev_date AS
    (
        SELECT TOP 1 ExecutionDate
        FROM arm.IndexPrice
        WHERE ExecutionDate < @ExecutionDate
          AND ExecutionDate >= DATEADD(DAY, -14, @ExecutionDate)
        GROUP BY ExecutionDate
        ORDER BY ExecutionDate DESC
    ),
    missing_prev AS
    (
        SELECT COUNT_BIG(*) AS n
        FROM dbo.[Index] i
        CROSS JOIN prev_date d
        WHERE i.IndexType = 'IndexPrice'
          AND NOT EXISTS (SELECT 1 FROM arm.IndexPrice p
                          WHERE p.IndexId = i.IndexId AND p.ExecutionDate = d.ExecutionDate)
    )
    INSERT @findings
    SELECT 'IndexPriceIndexMissingDrift',
           CONCAT(missing_now.n, ' configured index(es) produced no rows this snapshot, vs ',
                  missing_prev.n, ' on the previous one'),
           missing_now.n - missing_prev.n
    FROM missing_now CROSS JOIN missing_prev
    WHERE missing_now.n <> missing_prev.n;

    -- LastUpdatedDate is converted from the vendor's Mountain offset to US
    -- Central by the loader. A value in the future is the signature of that
    -- conversion being applied twice, or of a raw UTC value slipping through.
    INSERT @findings
    SELECT 'IndexPriceFutureLastUpdate',
           CONCAT(COUNT_BIG(*), ' row(s) have LastUpdatedDate beyond the snapshot date + 1 day'),
           COUNT_BIG(*)
    FROM arm.IndexPrice
    WHERE ExecutionDate = @ExecutionDate
      AND LastUpdatedDate > DATEADD(DAY, 1, CAST(@ExecutionDate AS DATETIME2(7)))
    HAVING COUNT_BIG(*) > 0;

    -- The four AlternateTraded* columns have no source element on this
    -- endpoint. A non-NULL one means either a vendor change worth knowing
    -- about or a column-shift bug in the TVP -- both want eyes on them.
    INSERT @findings
    SELECT 'IndexPriceUnexpectedAlternate',
           CONCAT(COUNT_BIG(*), ' row(s) carry a non-NULL AlternateTraded* value, which indexPrice.xml does not emit'),
           COUNT_BIG(*)
    FROM arm.IndexPrice
    WHERE ExecutionDate = @ExecutionDate
      AND (AlternateTradedAmount IS NOT NULL
        OR AlternateTradedUnit IS NOT NULL
        OR AlternateTradedContractUnit IS NOT NULL
        OR AlternateTradedTotalAmount IS NOT NULL)
    HAVING COUNT_BIG(*) > 0;

    -- Partial quantityTraded. The element supplies amount/unit/contractUnit/
    -- totalAmount together; a row with some but not all of them means the
    -- parser dropped a child rather than the vendor omitting the block.
    INSERT @findings
    SELECT 'IndexPricePartialQuantity',
           CONCAT(COUNT_BIG(*), ' row(s) have TradedAmount without TradedUnit (or the reverse)'),
           COUNT_BIG(*)
    FROM arm.IndexPrice
    WHERE ExecutionDate = @ExecutionDate
      AND (CASE WHEN TradedAmount IS NULL THEN 1 ELSE 0 END)
       <> (CASE WHEN TradedUnit   IS NULL THEN 1 ELSE 0 END)
    HAVING COUNT_BIG(*) > 0;
END

    ------------------------------------------------------------------ strip --
IF @StripFeedRan = 1
BEGIN

    -- TradeDateTime is converted to US Central. NGX trades roughly 06:00-16:00
    -- Central (observed 06:09:28..15:59:58 over a fortnight). A cluster outside
    -- that band is the signature of a time zone regression -- which matters
    -- doubly because TradeDateTime is a primary key component, so a wrong hour
    -- forks the key rather than merely mis-stamping the row.
    INSERT @findings
    SELECT 'StripTradeTimeOutOfBand',
           CONCAT(COUNT_BIG(*), ' trade(s) in the last 7 days fall outside 05:00-18:00 US Central'),
           COUNT_BIG(*)
    FROM arm.StripTradingSummary
    WHERE TradeDateTime >= DATEADD(DAY, -7, CAST(@ExecutionDate AS DATETIME2(0)))
      AND (CAST(TradeDateTime AS TIME) < '05:00:00' OR CAST(TradeDateTime AS TIME) > '18:00:00')
    HAVING COUNT_BIG(*) > 0;

    -- Trades stamped in the future. Same failure family as above.
    INSERT @findings
    SELECT 'StripFutureTradeDateTime',
           CONCAT(COUNT_BIG(*), ' trade(s) stamped beyond the snapshot date + 1 day'),
           COUNT_BIG(*)
    FROM arm.StripTradingSummary
    WHERE TradeDateTime > DATEADD(DAY, 1, CAST(@ExecutionDate AS DATETIME2(0)))
    HAVING COUNT_BIG(*) > 0;

    -- A weekday inside the loader's own re-pull window with no trades at all.
    -- Weekends and holidays are genuinely sparse, so only Mon-Fri is checked.
    --
    -- The weekday test is DATEFIRST- AND LANGUAGE-INDEPENDENT on purpose.
    -- DATEPART(WEEKDAY, ...) is relative to the session's DATEFIRST and
    -- DATENAME(WEEKDAY, ...) to its language, so under a login whose language
    -- is not us_english the same script would silently check a different five
    -- days and label them in another tongue. Counting days from a known Monday
    -- (1900-01-01) removes both dependencies: 0..4 is Mon..Fri for every
    -- session, and the name comes from a CASE rather than the server.
    --
    -- The day list is generated from a VALUES constructor rather than
    -- master.dbo.spt_values, which is an undocumented system table and a
    -- cross-database dependency in an otherwise self-contained script.
    -- (The CTE is named days_back, not "offsets": OFFSETS is a reserved T-SQL
    -- keyword -- the legacy SET OFFSETS statement -- and using it as a CTE name
    -- is a parse error.)
    ;WITH n(i) AS
    (
        SELECT v.i FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS v(i)
    ),
    days_back(days_back) AS
    (
        SELECT (tens.i * 10 + ones.i) + 1
        FROM n AS tens CROSS JOIN n AS ones
        WHERE (tens.i * 10 + ones.i) + 1 <= 30
    ),
    days AS
    (
        SELECT CAST(DATEADD(DAY, -o.days_back, @ExecutionDate) AS DATE) AS d
        FROM days_back o
    )
    INSERT @findings
    SELECT 'StripEmptyWeekday',
           CONCAT('No trades recorded for ', CONVERT(CHAR(10), days.d, 23), ' (',
                  CASE DATEDIFF(DAY, '19000101', days.d) % 7
                       WHEN 0 THEN 'Monday'    WHEN 1 THEN 'Tuesday'
                       WHEN 2 THEN 'Wednesday' WHEN 3 THEN 'Thursday'
                       WHEN 4 THEN 'Friday'    WHEN 5 THEN 'Saturday'
                       ELSE 'Sunday' END, ')'),
           NULL
    FROM days
    WHERE DATEDIFF(DAY, '19000101', days.d) % 7 < 5      -- 1900-01-01 was a Monday
      AND NOT EXISTS (SELECT 1 FROM arm.StripTradingSummary s
                      WHERE s.TradeDateTime >= CAST(days.d AS DATETIME2(0))
                        AND s.TradeDateTime <  DATEADD(DAY, 1, CAST(days.d AS DATETIME2(0))));
END

    SELECT Check_ AS [Check], Detail, Metric FROM @findings ORDER BY [Check];
END
GO
