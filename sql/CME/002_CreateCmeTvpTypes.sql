-- =============================================================================
-- 002_CreateCmeTvpTypes.sql
-- Database : CMEGroup
-- Schema   : arm
--
-- One table type per fact table. Each carries the whole row EXCEPT the
-- DB-stamped ModifiedAtUtc, in the same order as the table's own column list,
-- PLUS a trailing RowOrdinal:
--
--   arm.STLBASIC_OptionTvp  (ExchangeCode, ProductCode, TradeDate, ProductSymbol,
--                            ProductDescription, ContractYear, ContractMonth,
--                            PutCall, Strike, Open, High, HighABIndicator, Low,
--                            LowABIndicator, Last, LastABIndicator, Settle,
--                            PctChange, EstVol, PriorSettle, PriorVol, PriorInt,
--                            RowOrdinal)
--
--   arm.STLBASIC_FutureTvp  (ExchangeCode, ProductCode, TradeDate, ProductSymbol,
--                            ContractYear, ContractMonth, ProductDescription,
--                            Open, High, HighABIndicator, Low, LowABIndicator,
--                            Last, LastABIndicator, Settle, PctChange, EstVol,
--                            PriorSettle, PriorVol, PriorInt, RowOrdinal)
--
-- ⚠ The two types are NOT the same shape. The option type places
-- ProductDescription between ProductSymbol and ContractYear; the future type
-- places it AFTER ContractMonth. That mirrors the supplied DDL exactly and is
-- the single easiest thing to get wrong here, because a TVP binds BY POSITION --
-- swapping them would load descriptions into ContractYear and fail on the
-- conversion, or worse, succeed with shifted data.
--
-- LOAD-BEARING: column NAME + ORDER + TYPE must match the DataTable that
-- CmeFactSink builds, which it builds by walking the descriptor's column list in
-- CmeDescriptors. CmeTvpContractTests PARSES THIS FILE and asserts name, order,
-- SQL type and nullability against those descriptors, so editing one side alone
-- fails the build instead of silently writing every row one column out of place.
--
-- ==== WHY RowOrdinal EXISTS (and is NOT a table column) ====
-- It is the position of the row within its bulletin, 1-based, and it gives the
-- merge's batch de-duplication in 003 a DETERMINISTIC tiebreak ("last occurrence
-- in the file wins") instead of depending on row order inside a table variable,
-- which is not guaranteed. Live data has zero duplicate keys within a bulletin,
-- so today this never actually breaks a tie -- it exists so that if CME ever
-- emits the same key twice, the outcome is defined rather than arbitrary.
--
-- Every key component is NOT NULL here. SQL Server promotes PRIMARY KEY columns
-- to NOT NULL whatever the table DDL said, so the TVP simply states what the
-- table already enforces -- and it makes the loader fail loudly rather than
-- letting a blank key component through. That is why the loader sends '' rather
-- than NULL for an empty ProductDescription.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in the
-- right order).
-- =============================================================================

IF TYPE_ID('arm.STLBASIC_OptionTvp') IS NULL
BEGIN
    CREATE TYPE arm.STLBASIC_OptionTvp AS TABLE
    (
        ExchangeCode       VARCHAR(50)    NOT NULL,
        ProductCode        VARCHAR(50)    NOT NULL,
        TradeDate          DATE           NOT NULL,
        ProductSymbol      VARCHAR(50)    NOT NULL,
        ProductDescription VARCHAR(250)   NOT NULL,
        ContractYear       SMALLINT       NOT NULL,
        ContractMonth      TINYINT        NOT NULL,
        PutCall            VARCHAR(50)    NOT NULL,
        Strike             DECIMAL(18,8)  NOT NULL,
        [Open]             DECIMAL(18,8)  NULL,
        High               DECIMAL(18,8)  NULL,
        HighABIndicator    CHAR(1)        NULL,
        Low                DECIMAL(18,8)  NULL,
        LowABIndicator     CHAR(1)        NULL,
        [Last]             DECIMAL(18,8)  NULL,
        LastABIndicator    CHAR(1)        NULL,
        Settle             DECIMAL(18,8)  NULL,
        PctChange          DECIMAL(18,8)  NULL,
        EstVol             DECIMAL(18,8)  NULL,
        PriorSettle        DECIMAL(18,8)  NULL,
        PriorVol           DECIMAL(18,8)  NULL,
        PriorInt           DECIMAL(18,8)  NULL,
        RowOrdinal         INT            NOT NULL
    );
END
GO

IF TYPE_ID('arm.STLBASIC_FutureTvp') IS NULL
BEGIN
    CREATE TYPE arm.STLBASIC_FutureTvp AS TABLE
    (
        ExchangeCode       VARCHAR(50)    NOT NULL,
        ProductCode        VARCHAR(50)    NOT NULL,
        TradeDate          DATE           NOT NULL,
        ProductSymbol      VARCHAR(50)    NOT NULL,
        ContractYear       SMALLINT       NOT NULL,
        ContractMonth      TINYINT        NOT NULL,
        ProductDescription VARCHAR(2000)  NULL,
        [Open]             DECIMAL(18,8)  NULL,
        High               DECIMAL(18,8)  NULL,
        HighABIndicator    CHAR(1)        NULL,
        Low                DECIMAL(18,8)  NULL,
        LowABIndicator     CHAR(1)        NULL,
        [Last]             DECIMAL(18,8)  NULL,
        LastABIndicator    CHAR(1)        NULL,
        Settle             DECIMAL(18,8)  NULL,
        PctChange          DECIMAL(18,8)  NULL,
        EstVol             DECIMAL(18,8)  NULL,
        PriorSettle        DECIMAL(18,8)  NULL,
        PriorVol           DECIMAL(18,8)  NULL,
        PriorInt           DECIMAL(18,8)  NULL,
        RowOrdinal         INT            NOT NULL
    );
END
GO
