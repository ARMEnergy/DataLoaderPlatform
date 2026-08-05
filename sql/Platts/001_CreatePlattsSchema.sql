-- =============================================================================
-- 001_CreatePlattsSchema.sql
-- Schema, tables, and indexes for the Platts SFTP loader.
-- Lives in this loader's OWN database (Loaders:Platts:ConnectionString).
-- The Platts database is assumed to already exist; this script does NOT
-- create the database (same convention as EnergyAspects).
-- All objects live in schema [arm].
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Schema arm — CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- ----------------------------------------------------------------------------
-- arm.SymbolData — daily .ftp market values, one row per (Symbol, Bate, Date, Action).
-- MDC and SourcePath are payload attributes, NOT part of the key.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SymbolData', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SymbolData
    (
        MDC           NVARCHAR(10)   NOT NULL,
        Symbol        NVARCHAR(20)   NOT NULL,
        Bate          NCHAR(1)       NOT NULL,
        [Date]        DATETIME2(0)   NOT NULL,
        [Action]      NVARCHAR(5)    NOT NULL,
        [Value]       DECIMAL(38,10) NULL,
        ActionDate    DATETIME2(0)   NOT NULL,
        SourcePath    NVARCHAR(500)  NOT NULL,
        ModifiedAtUtc DATETIME2(3)   NOT NULL CONSTRAINT DF_SymbolData_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SymbolData PRIMARY KEY (Symbol, Bate, [Date], [Action])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.Symbol — reference metadata, one row per Symbol (14 data columns + audit).
-- Flag maps to the CSV "*/" column.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Symbol', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Symbol
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
        [Description] NVARCHAR(500)  NULL,
        ModifiedAtUtc DATETIME2(3)   NOT NULL CONSTRAINT DF_Symbol_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Symbol PRIMARY KEY (Symbol)
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FileLog — per-file audit row, upserted by (Feed, SourcePath).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        FileLogId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        Feed            NVARCHAR(20)  NOT NULL,
        SourcePath      NVARCHAR(500) NOT NULL,
        FileName        NVARCHAR(260) NOT NULL,
        LastModifiedUtc DATETIME2(3)  NOT NULL,
        SizeBytes       BIGINT        NULL,
        [RowCount]      INT           NOT NULL,
        Status          NVARCHAR(20)  NOT NULL,
        ProcessedAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_FileLog_ProcessedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

-- Upsert key for arm.FileLog: (Feed, SourcePath).
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UQ_FileLog_Feed_SourcePath'
      AND object_id = OBJECT_ID('arm.FileLog')
)
    CREATE UNIQUE INDEX UQ_FileLog_Feed_SourcePath ON arm.FileLog (Feed, SourcePath);
GO
