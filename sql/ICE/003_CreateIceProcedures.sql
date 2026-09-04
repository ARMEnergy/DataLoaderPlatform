-- =============================================================================
-- 003_CreateIceProcedures.sql
-- Database : ICE
-- Schema   : arm
--
--   arm.usp_UpsertFileLog          audit hub upsert, returns FileLogId
--   arm.usp_BulkMerge<Table>       x12, one per target table
--   arm.usp_ValidateLoad           post-load observational anomaly report
--
-- Shared conventions across every merge proc:
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY <pk>), keep row 1. SQL MERGE raises
--   "attempted to update the same row more than once" on a duplicated source
--   key, and ICE_Crude_Oil_Index_Trades genuinely ships exact duplicate rows
--   (two per file on 2026-08-25; see docs/apis/ICE.md 5.3). The duplicates are
--   byte-identical, so keeping row 1 loses nothing.
--
--   NO DELETE-BY-ABSENCE. None of the merges has WHEN NOT MATCHED BY SOURCE
--   THEN DELETE. Each work unit merges ONE file; a short or partial download
--   would otherwise wipe good rows for the whole trade date.
--
--   NO ORDERING GUARD IS NEEDED. Six feeds write arm.Futures and two write
--   arm.Options, so the same PK genuinely arrives from more than one work unit
--   in a single run. Unlike the Argus DCRDEUS feed, those overlaps were verified
--   to carry IDENTICAL payloads once ProductId is part of the arm.Futures key
--   (0 conflicting payloads — docs/apis/ICE.md 5.1/5.2), so merge order cannot
--   change the final state and no SourceFileDate-style guard is required.
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
-- arm.usp_UpsertFileLog — one hub row per (feed, trade date), for EVERY outcome
-- (Success / NotAvailable / Failed / AuthExpired / Malformed).
--
-- NotAvailable is a NORMAL outcome, not an error: ICE serves HTTP 200 with an
-- "Index of ... No Files Available" HTML page for weekends, holidays and future
-- dates (docs/apis/ICE.md 2).
--
-- @RequestPath carries the https URL and must NEVER carry the SSO token.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @FeedId       VARCHAR(50),
    @TradeDate    DATE,
    @FileName     VARCHAR(200),
    @TargetTable  VARCHAR(128)  = NULL,
    @StatusLabel  VARCHAR(20),
    @SizeBytes    BIGINT        = NULL,
    @RowCount     INT           = 0,
    @RowsDropped  INT           = 0,
    @ErrorMessage NVARCHAR(400) = NULL,
    @RequestPath  NVARCHAR(400)
AS
BEGIN
    SET NOCOUNT ON;

    IF @FeedId IS NULL OR @TradeDate IS NULL OR @FileName IS NULL
       OR @StatusLabel IS NULL OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @FeedId, @TradeDate, @FileName, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- Status is a fixed catalog seeded in 001 — a miss is a bug, so fail loudly
    -- rather than feed a NULL into the NOT NULL FK column.
    DECLARE @StatusId INT = (SELECT Id FROM arm.Status WHERE [Name] = @StatusLabel);
    IF @StatusId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Status ''%s''.', 16, 1, @StatusLabel);
        RETURN;
    END

    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE arm.FileLog AS tgt
    USING (SELECT @FeedId AS FeedId, @TradeDate AS TradeDate) AS src
       ON tgt.FeedId = src.FeedId AND tgt.TradeDate = src.TradeDate
    WHEN MATCHED THEN UPDATE SET
        FileName       = @FileName,
        TargetTable    = @TargetTable,
        StatusId       = @StatusId,
        SizeBytes      = @SizeBytes,
        [RowCount]     = @RowCount,
        RowsDropped    = @RowsDropped,
        ErrorMessage   = @ErrorMessage,
        RequestPath    = @RequestPath,
        LastCheckedUtc = SYSUTCDATETIME(),
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FeedId, TradeDate, FileName, TargetTable, StatusId, SizeBytes,
                [RowCount], RowsDropped, ErrorMessage, RequestPath,
                LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.FeedId, src.TradeDate, @FileName, @TargetTable, @StatusId, @SizeBytes,
                @RowCount, @RowsDropped, @ErrorMessage, @RequestPath,
                SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeEnvFutures -> arm.EnvFutures
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeEnvFutures
    @Records arm.EnvFuturesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TradeDate, Contract, ContractType, Strip
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.EnvFutures AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate    = s.TradeDate
       AND tgt.Contract     = s.Contract
       AND tgt.ContractType = s.ContractType
       AND tgt.Strip        = s.Strip
    WHEN MATCHED THEN UPDATE SET
        ProductId       = s.ProductId,
        Hub             = s.Hub,
        Product         = s.Product,
        Strike          = s.Strike,
        SettlementPrice = s.SettlementPrice,
        NetChange       = s.NetChange,
        ExpirationDate  = s.ExpirationDate,
        SourcePath      = s.SourcePath,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Contract, ContractType, Strip, ProductId, Hub, Product,
                Strike, SettlementPrice, NetChange, ExpirationDate, SourcePath, ModifiedAtUtc)
        VALUES (s.TradeDate, s.Contract, s.ContractType, s.Strip, s.ProductId, s.Hub, s.Product,
                s.Strike, s.SettlementPrice, s.NetChange, s.ExpirationDate, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeEnvOptions -> arm.EnvOptions
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeEnvOptions
    @Records arm.EnvOptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TradeDate, Contract, ContractType, Strike, Strip
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.EnvOptions AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate    = s.TradeDate
       AND tgt.Contract     = s.Contract
       AND tgt.ContractType = s.ContractType
       AND tgt.Strike       = s.Strike
       AND tgt.Strip        = s.Strip
    WHEN MATCHED THEN UPDATE SET
        ProductId        = s.ProductId,
        Hub              = s.Hub,
        Product          = s.Product,
        SettlementPrice  = s.SettlementPrice,
        NetChange        = s.NetChange,
        ExpirationDate   = s.ExpirationDate,
        OptionVolatility = s.OptionVolatility,
        DeltaFactor      = s.DeltaFactor,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Contract, ContractType, Strike, Strip, ProductId, Hub, Product,
                SettlementPrice, NetChange, ExpirationDate, OptionVolatility, DeltaFactor,
                SourcePath, ModifiedAtUtc)
        VALUES (s.TradeDate, s.Contract, s.ContractType, s.Strike, s.Strip, s.ProductId, s.Hub, s.Product,
                s.SettlementPrice, s.NetChange, s.ExpirationDate, s.OptionVolatility, s.DeltaFactor,
                s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeFutures -> arm.Futures
--
-- ProductId is part of the key here (the approved deviation). Six feeds call
-- this proc; their overlaps carry identical payloads, so order is irrelevant.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFutures
    @Records arm.FuturesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TradeDate, Contract, ContractType, Strip, ProductId
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Futures AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate    = s.TradeDate
       AND tgt.Contract     = s.Contract
       AND tgt.ContractType = s.ContractType
       AND tgt.Strip        = s.Strip
       AND tgt.ProductId    = s.ProductId
    WHEN MATCHED THEN UPDATE SET
        Hub             = s.Hub,
        Product         = s.Product,
        Strike          = s.Strike,
        SettlementPrice = s.SettlementPrice,
        NetChange       = s.NetChange,
        ExpirationDate  = s.ExpirationDate,
        SourcePath      = s.SourcePath,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Contract, ContractType, Strip, ProductId, Hub, Product,
                Strike, SettlementPrice, NetChange, ExpirationDate, SourcePath, ModifiedAtUtc)
        VALUES (s.TradeDate, s.Contract, s.ContractType, s.Strip, s.ProductId, s.Hub, s.Product,
                s.Strike, s.SettlementPrice, s.NetChange, s.ExpirationDate, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeCrudeOilIndex -> arm.ICE_Crude_Oil_Index
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCrudeOilIndex
    @Records arm.CrudeOilIndexTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY MKTID, PCC, INDEX_ID, INDEX_DATE
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICE_Crude_Oil_Index AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.MKTID      = s.MKTID
       AND tgt.PCC        = s.PCC
       AND tgt.INDEX_ID   = s.INDEX_ID
       AND tgt.INDEX_DATE = s.INDEX_DATE
    WHEN MATCHED THEN UPDATE SET
        INDEX_PRICE      = s.INDEX_PRICE,
        INDEX_DATE_RANGE = s.INDEX_DATE_RANGE,
        VOLUME           = s.VOLUME,
        MKT_DESC         = s.MKT_DESC,
        CREATION_TIME    = s.CREATION_TIME,
        LAST_UPDATE_TIME = s.LAST_UPDATE_TIME,
        BBL              = s.BBL,
        DAILY_PRICE      = s.DAILY_PRICE,
        DAILY_VOLUME     = s.DAILY_VOLUME,
        DAILY_BBL        = s.DAILY_BBL,
        NUM_OF_TRADES    = s.NUM_OF_TRADES,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MKTID, PCC, INDEX_ID, INDEX_DATE, INDEX_PRICE, INDEX_DATE_RANGE, VOLUME,
                MKT_DESC, CREATION_TIME, LAST_UPDATE_TIME, BBL, DAILY_PRICE, DAILY_VOLUME,
                DAILY_BBL, NUM_OF_TRADES, SourcePath, ModifiedAtUtc)
        VALUES (s.MKTID, s.PCC, s.INDEX_ID, s.INDEX_DATE, s.INDEX_PRICE, s.INDEX_DATE_RANGE, s.VOLUME,
                s.MKT_DESC, s.CREATION_TIME, s.LAST_UPDATE_TIME, s.BBL, s.DAILY_PRICE, s.DAILY_VOLUME,
                s.DAILY_BBL, s.NUM_OF_TRADES, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeCrudeOilIndexTrades -> arm.ICE_Crude_Oil_Index_Trades
--
-- The de-dup here is LOAD-BEARING, not defensive: this feed ships exact
-- duplicate rows (same DEAL_ID twice) and MERGE would otherwise error out on the
-- whole batch. See docs/apis/ICE.md 5.3.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCrudeOilIndexTrades
    @Records arm.CrudeOilIndexTradesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TRADE_DATE, PCC_TRADE, PCC_INDEX, STRIP, INDEX_ID, DEAL_ID
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICE_Crude_Oil_Index_Trades AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TRADE_DATE = s.TRADE_DATE
       AND tgt.PCC_TRADE  = s.PCC_TRADE
       AND tgt.PCC_INDEX  = s.PCC_INDEX
       AND tgt.STRIP      = s.STRIP
       AND tgt.INDEX_ID   = s.INDEX_ID
       AND tgt.DEAL_ID    = s.DEAL_ID
    WHEN MATCHED THEN UPDATE SET
        PRODUCT_NAME        = s.PRODUCT_NAME,
        HUB_NAME            = s.HUB_NAME,
        DEAL_EXECUTION_TIME = s.DEAL_EXECUTION_TIME,
        DEAL_PRICE          = s.DEAL_PRICE,
        DEAL_QUANTITY       = s.DEAL_QUANTITY,
        DEAL_QTY_IN_BBL     = s.DEAL_QTY_IN_BBL,
        MARKET_TYPE_ID      = s.MARKET_TYPE_ID,
        SourcePath          = s.SourcePath,
        ModifiedAtUtc       = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TRADE_DATE, PCC_TRADE, PCC_INDEX, STRIP, INDEX_ID, DEAL_ID, PRODUCT_NAME,
                HUB_NAME, DEAL_EXECUTION_TIME, DEAL_PRICE, DEAL_QUANTITY, DEAL_QTY_IN_BBL,
                MARKET_TYPE_ID, SourcePath, ModifiedAtUtc)
        VALUES (s.TRADE_DATE, s.PCC_TRADE, s.PCC_INDEX, s.STRIP, s.INDEX_ID, s.DEAL_ID, s.PRODUCT_NAME,
                s.HUB_NAME, s.DEAL_EXECUTION_TIME, s.DEAL_PRICE, s.DEAL_QUANTITY, s.DEAL_QTY_IN_BBL,
                s.MARKET_TYPE_ID, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeIceClearedPowerFutures -> arm.ICEClearedPowerFutures
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIceClearedPowerFutures
    @Records arm.PowerFuturesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TradeDate, Contract, ContractType, Strip
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICEClearedPowerFutures AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate    = s.TradeDate
       AND tgt.Contract     = s.Contract
       AND tgt.ContractType = s.ContractType
       AND tgt.Strip        = s.Strip
    WHEN MATCHED THEN UPDATE SET
        ProductId       = s.ProductId,
        Hub             = s.Hub,
        Product         = s.Product,
        Strike          = s.Strike,
        SettlementPrice = s.SettlementPrice,
        NetChange       = s.NetChange,
        ExpirationDate  = s.ExpirationDate,
        SourcePath      = s.SourcePath,
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Contract, ContractType, Strip, ProductId, Hub, Product,
                Strike, SettlementPrice, NetChange, ExpirationDate, SourcePath, ModifiedAtUtc)
        VALUES (s.TradeDate, s.Contract, s.ContractType, s.Strip, s.ProductId, s.Hub, s.Product,
                s.Strike, s.SettlementPrice, s.NetChange, s.ExpirationDate, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeIceClearedPowerOptions -> arm.ICEClearedPowerOptions
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIceClearedPowerOptions
    @Records arm.PowerOptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TradeDate, Contract, ContractType, Strike, Strip
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICEClearedPowerOptions AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate    = s.TradeDate
       AND tgt.Contract     = s.Contract
       AND tgt.ContractType = s.ContractType
       AND tgt.Strike       = s.Strike
       AND tgt.Strip        = s.Strip
    WHEN MATCHED THEN UPDATE SET
        ProductId        = s.ProductId,
        Hub              = s.Hub,
        Product          = s.Product,
        SettlementPrice  = s.SettlementPrice,
        NetChange        = s.NetChange,
        ExpirationDate   = s.ExpirationDate,
        OptionVolatility = s.OptionVolatility,
        DeltaFactor      = s.DeltaFactor,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Contract, ContractType, Strike, Strip, ProductId, Hub, Product,
                SettlementPrice, NetChange, ExpirationDate, OptionVolatility, DeltaFactor,
                SourcePath, ModifiedAtUtc)
        VALUES (s.TradeDate, s.Contract, s.ContractType, s.Strike, s.Strip, s.ProductId, s.Hub, s.Product,
                s.SettlementPrice, s.NetChange, s.ExpirationDate, s.OptionVolatility, s.DeltaFactor,
                s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeIcefcaOptions -> arm.ICEFCA_Options
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIcefcaOptions
    @Records arm.FcaOptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICEFCA_Options AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TRADE_DATE      = s.TRADE_DATE
       AND tgt.CONTRACT        = s.CONTRACT
       AND tgt.STRIP           = s.STRIP
       AND tgt.EXPIRATION_DATE = s.EXPIRATION_DATE
       AND tgt.STRIKE          = s.STRIKE
       AND tgt.PUT_CALL        = s.PUT_CALL
    WHEN MATCHED THEN UPDATE SET
        SETTLEMENT_PRICE = s.SETTLEMENT_PRICE,
        VOLATILITY       = s.VOLATILITY,
        DELTA            = s.DELTA,
        GAMMA            = s.GAMMA,
        THETA            = s.THETA,
        VEGA             = s.VEGA,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL,
                SETTLEMENT_PRICE, VOLATILITY, DELTA, GAMMA, THETA, VEGA, SourcePath, ModifiedAtUtc)
        VALUES (s.TRADE_DATE, s.CONTRACT, s.STRIP, s.EXPIRATION_DATE, s.STRIKE, s.PUT_CALL,
                s.SETTLEMENT_PRICE, s.VOLATILITY, s.DELTA, s.GAMMA, s.THETA, s.VEGA, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeIcefusFinOptions -> arm.ICEFUS_FinOptions
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIcefusFinOptions
    @Records arm.FusFinOptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICEFUS_FinOptions AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TRADE_DATE      = s.TRADE_DATE
       AND tgt.CONTRACT        = s.CONTRACT
       AND tgt.STRIP           = s.STRIP
       AND tgt.EXPIRATION_DATE = s.EXPIRATION_DATE
       AND tgt.STRIKE          = s.STRIKE
       AND tgt.PUT_CALL        = s.PUT_CALL
    WHEN MATCHED THEN UPDATE SET
        SETTLEMENT_PRICE = s.SETTLEMENT_PRICE,
        VOLATILITY       = s.VOLATILITY,
        DELTA            = s.DELTA,
        GAMMA            = s.GAMMA,
        THETA            = s.THETA,
        VEGA             = s.VEGA,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL,
                SETTLEMENT_PRICE, VOLATILITY, DELTA, GAMMA, THETA, VEGA, SourcePath, ModifiedAtUtc)
        VALUES (s.TRADE_DATE, s.CONTRACT, s.STRIP, s.EXPIRATION_DATE, s.STRIKE, s.PUT_CALL,
                s.SETTLEMENT_PRICE, s.VOLATILITY, s.DELTA, s.GAMMA, s.THETA, s.VEGA, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeIcefusSoftOptions -> arm.ICEFUS_SoftOptions
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIcefusSoftOptions
    @Records arm.FusSoftOptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.ICEFUS_SoftOptions AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TRADE_DATE      = s.TRADE_DATE
       AND tgt.CONTRACT        = s.CONTRACT
       AND tgt.STRIP           = s.STRIP
       AND tgt.EXPIRATION_DATE = s.EXPIRATION_DATE
       AND tgt.STRIKE          = s.STRIKE
       AND tgt.PUT_CALL        = s.PUT_CALL
    WHEN MATCHED THEN UPDATE SET
        SETTLEMENT_PRICE = s.SETTLEMENT_PRICE,
        VOLATILITY       = s.VOLATILITY,
        DELTA            = s.DELTA,
        GAMMA            = s.GAMMA,
        THETA            = s.THETA,
        VEGA             = s.VEGA,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL,
                SETTLEMENT_PRICE, VOLATILITY, DELTA, GAMMA, THETA, VEGA, SourcePath, ModifiedAtUtc)
        VALUES (s.TRADE_DATE, s.CONTRACT, s.STRIP, s.EXPIRATION_DATE, s.STRIKE, s.PUT_CALL,
                s.SETTLEMENT_PRICE, s.VOLATILITY, s.DELTA, s.GAMMA, s.THETA, s.VEGA, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeIfllOptions -> arm.IFLL_Options   (the XLSX feed)
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeIfllOptions
    @Records arm.IfllOptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.IFLL_Options AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TRADE_DATE      = s.TRADE_DATE
       AND tgt.CONTRACT        = s.CONTRACT
       AND tgt.STRIP           = s.STRIP
       AND tgt.EXPIRATION_DATE = s.EXPIRATION_DATE
       AND tgt.STRIKE          = s.STRIKE
       AND tgt.PUT_CALL        = s.PUT_CALL
    WHEN MATCHED THEN UPDATE SET
        SETTLEMENT_PRICE = s.SETTLEMENT_PRICE,
        VOLATILITY       = s.VOLATILITY,
        DELTA            = s.DELTA,
        GAMMA            = s.GAMMA,
        THETA            = s.THETA,
        VEGA             = s.VEGA,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TRADE_DATE, CONTRACT, STRIP, EXPIRATION_DATE, STRIKE, PUT_CALL,
                SETTLEMENT_PRICE, VOLATILITY, DELTA, GAMMA, THETA, VEGA, SourcePath, ModifiedAtUtc)
        VALUES (s.TRADE_DATE, s.CONTRACT, s.STRIP, s.EXPIRATION_DATE, s.STRIKE, s.PUT_CALL,
                s.SETTLEMENT_PRICE, s.VOLATILITY, s.DELTA, s.GAMMA, s.THETA, s.VEGA, s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeOptions -> arm.Options   (gas + oil options feeds)
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeOptions
    @Records arm.OptionsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY TradeDate, Contract, ContractType, Strike, Strip
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Options AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.TradeDate    = s.TradeDate
       AND tgt.Contract     = s.Contract
       AND tgt.ContractType = s.ContractType
       AND tgt.Strike       = s.Strike
       AND tgt.Strip        = s.Strip
    WHEN MATCHED THEN UPDATE SET
        ProductId        = s.ProductId,
        Hub              = s.Hub,
        Product          = s.Product,
        SettlementPrice  = s.SettlementPrice,
        NetChange        = s.NetChange,
        ExpirationDate   = s.ExpirationDate,
        OptionVolatility = s.OptionVolatility,
        DeltaFactor      = s.DeltaFactor,
        SourcePath       = s.SourcePath,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeDate, Contract, ContractType, Strike, Strip, ProductId, Hub, Product,
                SettlementPrice, NetChange, ExpirationDate, OptionVolatility, DeltaFactor,
                SourcePath, ModifiedAtUtc)
        VALUES (s.TradeDate, s.Contract, s.ContractType, s.Strike, s.Strip, s.ProductId, s.Hub, s.Product,
                s.SettlementPrice, s.NetChange, s.ExpirationDate, s.OptionVolatility, s.DeltaFactor,
                s.SourcePath, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad — OBSERVATIONAL post-load anomaly report.
--
-- Returns rows; raises nothing. IceLoadValidator logs whatever comes back and
-- swallows its own failures, so validation can never change a run's outcome.
--
-- @TradeDate NULL validates the whole table; otherwise it scopes to one day.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @TradeDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Findings TABLE
    (
        Severity VARCHAR(10)  NOT NULL,
        Check_   VARCHAR(60)  NOT NULL,
        Detail   NVARCHAR(400) NOT NULL,
        Cnt      BIGINT       NOT NULL
    );

    -- 1. Feeds that reported a hard failure. AuthExpired is called out
    --    separately because it is the one status that can masquerade as a clean
    --    zero-row day if the classifier ever regresses (docs/apis/ICE.md 2).
    INSERT @Findings
    SELECT 'ERROR', 'FileLogFailures',
           CONCAT('Feed ', f.FeedId, ' / ', CONVERT(VARCHAR(10), f.TradeDate, 23), ' -> ', s.[Name]), 1
    FROM arm.FileLog AS f
    JOIN arm.Status  AS s ON s.Id = f.StatusId
    WHERE s.[Name] IN ('Failed', 'AuthExpired', 'Malformed')
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate);

    -- 2. A Success row that wrote nothing. Legitimate for the crude-index trades
    --    feed on a closed window, suspicious anywhere else.
    INSERT @Findings
    SELECT 'WARN', 'SuccessWithZeroRows',
           CONCAT('Feed ', f.FeedId, ' / ', CONVERT(VARCHAR(10), f.TradeDate, 23)), 1
    FROM arm.FileLog AS f
    JOIN arm.Status  AS s ON s.Id = f.StatusId
    WHERE s.[Name] = 'Success'
      AND f.[RowCount] = 0
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate);

    -- 3. Drop ratio. Options feeds legitimately drop their blank-strike 'F'/'D'
    --    underlying-future rows — 1.3 % to 25.9 % depending on the feed. Report
    --    the ratio rather than a threshold verdict: the number is only
    --    meaningful against that feed's own history.
    INSERT @Findings
    SELECT 'INFO', 'RowsDroppedRatio',
           CONCAT('Feed ', f.FeedId, ' / ', CONVERT(VARCHAR(10), f.TradeDate, 23),
                  ' dropped ', f.RowsDropped, ' of ',
                  f.RowsDropped + f.[RowCount], ' parsed row(s)'),
           f.RowsDropped
    FROM arm.FileLog AS f
    WHERE f.RowsDropped > 0
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate);

    -- 4. A feed that dropped EVERY row it parsed is a parser fault, not a shape.
    INSERT @Findings
    SELECT 'ERROR', 'AllRowsDropped',
           CONCAT('Feed ', f.FeedId, ' / ', CONVERT(VARCHAR(10), f.TradeDate, 23),
                  ' dropped all ', f.RowsDropped, ' row(s)'),
           f.RowsDropped
    FROM arm.FileLog AS f
    WHERE f.RowsDropped > 0
      AND f.[RowCount] = 0
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate);

    -- 5. Width watch: any value at 90 % or more of its column width, so a
    --    widening feed surfaces before it starts erroring.
    INSERT @Findings
    SELECT 'WARN', 'NearColumnWidthLimit', CONCAT('arm.Futures.Hub max length ', MAX(LEN(Hub)), ' of 100'), COUNT_BIG(*)
    FROM arm.Futures
    WHERE LEN(Hub) >= 90 AND (@TradeDate IS NULL OR TradeDate = @TradeDate)
    HAVING COUNT_BIG(*) > 0;

    INSERT @Findings
    SELECT 'WARN', 'NearColumnWidthLimit', CONCAT('arm.Futures.Product max length ', MAX(LEN(Product)), ' of 100'), COUNT_BIG(*)
    FROM arm.Futures
    WHERE LEN(Product) >= 90 AND (@TradeDate IS NULL OR TradeDate = @TradeDate)
    HAVING COUNT_BIG(*) > 0;

    -- 6. Contract-code ambiguity watch. This is the condition that forced
    --    ProductId into the arm.Futures key: the same (date, contract, type,
    --    strip) describing two different products. It CANNOT corrupt data any
    --    more, but a rising count means ICE is reusing more codes and is worth
    --    knowing about.
    INSERT @Findings
    SELECT 'INFO', 'AmbiguousContractCodes',
           CONCAT('Contract ', Contract, ' maps to ', COUNT(DISTINCT ProductId), ' product ids'),
           COUNT_BIG(*)
    FROM arm.Futures
    WHERE (@TradeDate IS NULL OR TradeDate = @TradeDate)
    GROUP BY Contract
    HAVING COUNT(DISTINCT ProductId) > 1;

    -- 7. Orphan sanity: rows whose SourcePath has no FileLog row. Indicates a
    --    merge that ran without its audit row being written.
    INSERT @Findings
    SELECT 'WARN', 'FactRowsWithoutFileLog', 'arm.Futures rows with a NULL SourcePath', COUNT_BIG(*)
    FROM arm.Futures
    WHERE SourcePath IS NULL AND (@TradeDate IS NULL OR TradeDate = @TradeDate)
    HAVING COUNT_BIG(*) > 0;

    SELECT Severity, Check_ AS [Check], Detail, Cnt AS [Count]
    FROM @Findings
    ORDER BY CASE Severity WHEN 'ERROR' THEN 1 WHEN 'WARN' THEN 2 ELSE 3 END, Check_, Detail;
END
GO
