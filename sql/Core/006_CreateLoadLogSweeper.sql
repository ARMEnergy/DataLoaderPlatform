-- =============================================================================
-- 006_CreateLoadLogSweeper.sql
-- core.usp_AbandonStaleLoadLogs — closes core.LoadLog rows that were opened but
-- never closed, so the table has no permanently-open rows.
--
-- Why this exists
-- -----------------------------------------------------------------------------
-- core.LoadLog has THREE terminal states, not two. usp_CompleteLoadLog stamps
-- CompletedAtUtc on BOTH outcomes, so a normal FAILURE is (IsComplete = 0,
-- CompletedAtUtc set, ErrorMessage set). The anomaly is CompletedAtUtc IS NULL:
--
--   success   IsComplete = 1   CompletedAtUtc set    ErrorMessage NULL
--   failure   IsComplete = 0   CompletedAtUtc set    ErrorMessage set
--   ORPHAN    IsComplete = 0   CompletedAtUtc NULL   ErrorMessage NULL
--
-- An orphan is produced when usp_BeginLoadLog's INSERT commits but the CLIENT
-- never receives the SCOPE_IDENTITY() output -- a command timeout, a dropped
-- connection, or the work unit's token firing mid-call. LoaderPipelineBase
-- catches that, logs, and returns WITHOUT a handle, so no code path can ever
-- close the row. A killed process leaves the same state. Measured 2026-10-13:
-- 25 orphans against 1,051,475 successes over 30 days, across 7 loaders.
--
-- No data is lost by an orphan -- usp_BeginLoadLog short-circuits only on
-- IsComplete = 1, so it never blocks a retry -- but the row misreports the unit
-- as "in flight" forever, which is what this proc cleans up.
--
-- 🔒 Safety: never touches a run that is still going
-- -----------------------------------------------------------------------------
-- Separate host invocations DO overlap (LoaderRunId 1430 ran 13:00:05-13:18:32
-- while 1431 ran 13:00:05-13:00:49), so "close every open row" would mark another
-- process's LIVE units as abandoned. A row is therefore only closed when its own
-- run is definitively over:
--
--   * the run row has CompletedAtUtc set                      -> it finished, or
--   * the run started longer ago than @StaleAfterHours        -> process killed
--                                                                 (its LoaderRun
--                                                                  never closed)
--
-- and never for @CurrentRunId itself.
-- =============================================================================

USE DataLoaderPlatform;
GO

-- ----------------------------------------------------------------------------
-- Filtered index: the sweep runs at the start of every invocation and
-- core.LoadLog holds millions of rows, so the open-row predicate must not become
-- a scan. Filtered on CompletedAtUtc IS NULL, so the index holds only the handful
-- of open rows (25 in a representative 30-day window).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('core.LoadLog') AND name = 'IX_LoadLog_Open')
BEGIN
    CREATE NONCLUSTERED INDEX IX_LoadLog_Open
        ON core.LoadLog (RunId) INCLUDE (StartedAtUtc)
        WHERE CompletedAtUtc IS NULL;
END
GO

IF OBJECT_ID('core.usp_AbandonStaleLoadLogs', 'P') IS NOT NULL
    DROP PROCEDURE core.usp_AbandonStaleLoadLogs;
GO

CREATE PROCEDURE core.usp_AbandonStaleLoadLogs
    @CurrentRunId    UNIQUEIDENTIFIER,
    @StaleAfterHours INT            = 24,
    @Message         NVARCHAR(400)  = N'Abandoned - the run ended without closing this unit',
    @Closed          INT            OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @StaleAfterHours IS NULL OR @StaleAfterHours < 1 SET @StaleAfterHours = 24;

    DECLARE @Cutoff DATETIME2(7) = DATEADD(HOUR, -@StaleAfterHours, SYSUTCDATETIME());

    UPDATE ll
       SET CompletedAtUtc   = SYSUTCDATETIME(),
           IsComplete       = 0,
           RecordsProcessed = NULL,
           ErrorMessage     = @Message
      FROM core.LoadLog ll
      LEFT JOIN core.LoaderRun lr ON lr.RunId = ll.RunId
     WHERE ll.CompletedAtUtc IS NULL
       AND ll.RunId <> @CurrentRunId
       AND (
             lr.CompletedAtUtc IS NOT NULL
          OR COALESCE(lr.StartedAtUtc, ll.StartedAtUtc) < @Cutoff
           );

    SET @Closed = @@ROWCOUNT;
END
GO
