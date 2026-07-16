-- =============================================================================
-- 002_CreateCoreProcedures.sql
-- Stored procedures the platform's SqlLoadLogRepository calls.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- core.usp_BeginLoaderRun — open a run row.
-- Idempotent: if the row already exists (re-run with the same id) it just
-- resets the completion fields.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.usp_BeginLoaderRun', 'P') IS NOT NULL DROP PROCEDURE core.usp_BeginLoaderRun;
GO
CREATE PROCEDURE core.usp_BeginLoaderRun
    @RunId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    MERGE core.LoaderRun AS target
    USING (SELECT @RunId AS RunId) AS src
       ON target.RunId = src.RunId
    WHEN MATCHED THEN
        UPDATE SET CompletedAtUtc = NULL, Success = NULL
    WHEN NOT MATCHED THEN
        INSERT (RunId, StartedAtUtc, HostName)
        VALUES (@RunId, SYSUTCDATETIME(), HOST_NAME());
END
GO

-- ----------------------------------------------------------------------------
-- core.usp_CompleteLoaderRun — close a run row.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.usp_CompleteLoaderRun', 'P') IS NOT NULL DROP PROCEDURE core.usp_CompleteLoaderRun;
GO
CREATE PROCEDURE core.usp_CompleteLoaderRun
    @RunId   UNIQUEIDENTIFIER,
    @Success BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE core.LoaderRun
       SET CompletedAtUtc = SYSUTCDATETIME(),
           Success        = @Success
     WHERE RunId = @RunId;
END
GO

-- ----------------------------------------------------------------------------
-- core.usp_BeginLoadLog — open a load-log row for one work unit.
--
-- Returns @LoadLogId OUTPUT:
--   > 0  → new row created; caller does the work and calls usp_CompleteLoadLog
--   -1   → this unit was already successfully loaded — caller should skip
--
-- The check looks at all previous rows for the (LoaderId, WorkUnitKey) pair;
-- if any of them has IsComplete = 1, we treat the unit as done.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.usp_BeginLoadLog', 'P') IS NOT NULL DROP PROCEDURE core.usp_BeginLoadLog;
GO
CREATE PROCEDURE core.usp_BeginLoadLog
    @LoaderId        NVARCHAR(100),
    @RunId           UNIQUEIDENTIFIER,
    @WorkUnitKey     NVARCHAR(450),
    @WorkUnitDisplay NVARCHAR(500),
    @LoadLogId       BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    -- Already successfully loaded? Short-circuit.
    IF EXISTS (
        SELECT 1
          FROM core.LoadLog
         WHERE LoaderId    = @LoaderId
           AND WorkUnitKey = @WorkUnitKey
           AND IsComplete  = 1
    )
    BEGIN
        SET @LoadLogId = -1;
        RETURN;
    END

    INSERT INTO core.LoadLog (LoaderId, RunId, WorkUnitKey, WorkUnitDisplay, StartedAtUtc, IsComplete)
    VALUES (@LoaderId, @RunId, @WorkUnitKey, @WorkUnitDisplay, SYSUTCDATETIME(), 0);

    SET @LoadLogId = SCOPE_IDENTITY();
END
GO

-- ----------------------------------------------------------------------------
-- core.usp_CompleteLoadLog — close a load-log row.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.usp_CompleteLoadLog', 'P') IS NOT NULL DROP PROCEDURE core.usp_CompleteLoadLog;
GO
CREATE PROCEDURE core.usp_CompleteLoadLog
    @LoadLogId        BIGINT,
    @IsComplete       BIT,
    @RecordsProcessed INT           = NULL,
    @ErrorMessage     NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE core.LoadLog
       SET CompletedAtUtc   = SYSUTCDATETIME(),
           IsComplete       = @IsComplete,
           RecordsProcessed = @RecordsProcessed,
           ErrorMessage     = @ErrorMessage
     WHERE LoadLogId = @LoadLogId;
END
GO
