-- =============================================================================
-- 001_CreateCoreSchema.sql
-- Shared platform schema. Lives in the platform database (whichever
-- connection string Platform:LoadLogConnectionString points at).
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'core')
    EXEC ('CREATE SCHEMA core');
GO

-- ----------------------------------------------------------------------------
-- core.LoaderRun — one row per host invocation. Ties together every load-log
-- row produced by that host run.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.LoaderRun', 'U') IS NULL
BEGIN
    CREATE TABLE core.LoaderRun
    (
        RunId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_LoaderRun PRIMARY KEY,
        StartedAtUtc     DATETIME2(3)     NOT NULL CONSTRAINT DF_LoaderRun_StartedAtUtc DEFAULT SYSUTCDATETIME(),
        CompletedAtUtc   DATETIME2(3)     NULL,
        Success          BIT              NULL,
        HostName         NVARCHAR(256)    NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- core.LoadLog — one row per work unit per attempt, across every loader.
-- LoaderId discriminates between loaders sharing this table.
--
-- The (LoaderId, WorkUnitKey) pair is the idempotency key: a successful row
-- here means "this unit is already done, skip on re-run".
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.LoadLog', 'U') IS NULL
BEGIN
    CREATE TABLE core.LoadLog
    (
        LoadLogId        BIGINT IDENTITY(1,1) NOT NULL
                         CONSTRAINT PK_LoadLog PRIMARY KEY,
        LoaderId         NVARCHAR(100)    NOT NULL,
        RunId            UNIQUEIDENTIFIER NOT NULL,
        WorkUnitKey      NVARCHAR(450)    NOT NULL,
        WorkUnitDisplay  NVARCHAR(500)    NULL,
        StartedAtUtc     DATETIME2(3)     NOT NULL CONSTRAINT DF_LoadLog_StartedAtUtc DEFAULT SYSUTCDATETIME(),
        CompletedAtUtc   DATETIME2(3)     NULL,
        IsComplete       BIT              NOT NULL CONSTRAINT DF_LoadLog_IsComplete   DEFAULT 0,
        RecordsProcessed INT              NULL,
        ErrorMessage     NVARCHAR(MAX)    NULL,
        CONSTRAINT FK_LoadLog_LoaderRun FOREIGN KEY (RunId) REFERENCES core.LoaderRun(RunId)
    );

    -- Idempotency lookup index: find the most recent attempt for this unit.
    CREATE INDEX IX_LoadLog_LoaderId_WorkUnitKey
        ON core.LoadLog (LoaderId, WorkUnitKey)
        INCLUDE (IsComplete, CompletedAtUtc);

    -- Run-level reporting
    CREATE INDEX IX_LoadLog_RunId ON core.LoadLog (RunId);
END
GO
