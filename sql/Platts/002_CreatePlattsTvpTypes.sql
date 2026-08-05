-- =============================================================================
-- 002_CreatePlattsTvpTypes.sql
-- Table-valued parameter types for the bulk merges.
-- Column ORDER here is load-bearing: it must match the DataTable order the C#
-- sinks build. Do NOT reorder without updating the sinks.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.SymbolDataTvp — feeds arm.usp_BulkMergeSymbolData.
-- NO primary key: the sink de-dups the batch (keeping latest ActionDate) before
-- sending, and a PK here would reject legitimate same-file duplicate keys.
-- Does NOT carry ModifiedAtUtc (populated by the target's DEFAULT / MERGE).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SymbolDataTvp') IS NULL
BEGIN
    CREATE TYPE arm.SymbolDataTvp AS TABLE
    (
        MDC        NVARCHAR(10)   NOT NULL,
        Symbol     NVARCHAR(20)   NOT NULL,
        Bate       NCHAR(1)       NOT NULL,
        [Date]     DATETIME2(0)   NOT NULL,
        [Action]   NVARCHAR(5)    NOT NULL,
        [Value]    DECIMAL(38,10) NULL,
        ActionDate DATETIME2(0)   NOT NULL,
        SourcePath NVARCHAR(500)  NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SymbolTvp — feeds arm.usp_BulkMergeSymbol.
-- Does NOT carry ModifiedAtUtc (populated by the target's DEFAULT / MERGE).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SymbolTvp') IS NULL
BEGIN
    CREATE TYPE arm.SymbolTvp AS TABLE
    (
        MDC           NVARCHAR(10)   NULL,
        Trans         NVARCHAR(50)   NULL,
        Symbol        NVARCHAR(20)   NOT NULL,
        Bates         NVARCHAR(20)   NULL,
        Freq          NVARCHAR(10)   NULL,
        Curr          NVARCHAR(10)   NULL,
        UOM           NVARCHAR(20)   NULL,
        [DEC]         INT            NULL,
        Conv          DECIMAL(38,10) NULL,
        Flag          NVARCHAR(20)   NULL,
        To_UOM        NVARCHAR(20)   NULL,
        Earliest      DATE           NULL,
        Latest        DATE           NULL,
        [Description] NVARCHAR(500)  NULL
    );
END
GO
