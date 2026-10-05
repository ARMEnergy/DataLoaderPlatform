-- =============================================================================
-- 005_CreateAdditionalProcesses.sql
-- Post-run dispatch hook. Lives in the platform database (whichever connection
-- string Platform:LoadLogConnectionString points at).
--
-- The host calls core.usp_RunAdditionalProcesses once per loader, immediately
-- after that loader finishes and before the run row is closed. It is the single
-- place to hang post-load work — refreshing a reporting table, rebuilding an
-- aggregate, kicking a downstream proc in another database.
--
-- Additive: this script does NOT modify 001-004. Run it against the platform
-- database AFTER 001-004. Idempotent / re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- core.usp_RunAdditionalProcesses
--
-- @LoaderName  the LoaderId that just finished, e.g. 'Platts'. Matches
--              core.LoadLog.LoaderId and core.Param.LoaderName (same
--              VARCHAR(150) convention as core.Param).
--
-- @RunId       the host run the loader ran under. NOTE THE GRAIN: this is one
--              id per host INVOCATION, not per loader — every loader in the
--              same run is passed the SAME @RunId with a different
--              @LoaderName. So @RunId alone does not identify this loader's
--              work; the PAIR does, and that pair is exactly the grain of
--              core.LoadLog, which carries both LoaderId and RunId. To see
--              what this loader just did:
--
--                  SELECT WorkUnitKey, RecordsProcessed, IsComplete, ErrorMessage
--                    FROM core.LoadLog
--                   WHERE LoaderId = @LoaderName AND RunId = @RunId;
--
--              core.LoaderRun holds the run itself, though its CompletedAtUtc
--              is still NULL here — this proc runs BEFORE the run is closed.
--
-- WHEN IT IS CALLED. After the loader succeeded AND after it failed (partial
-- data still landed, so post-processing is usually still wanted). It is NOT
-- called when the overlap guard skipped the loader (another process holds the
-- lock and is already doing this work), nor when the run was cancelled, nor
-- when Platform:RunAdditionalProcesses is false.
--
-- CONTRACT.
--   * Returns nothing. The host calls ExecuteNonQuery and ignores result sets.
--   * Failures are LOGGED AND SWALLOWED by the host — raising an error here
--     will not fail the loader or change the exit code. If post-processing
--     must be seen to fail, surface it somewhere this proc writes to.
--   * Bounded by Platform:AdditionalProcessesTimeoutSeconds (default 600).
--     Exceeding it cancels the command and is logged, nothing more.
--   * MAY RUN CONCURRENTLY WITH ITSELF for different loaders when
--     Platform:MaxConcurrentLoaders > 1. Never for the same @LoaderName —
--     the overlap guard prevents that.
--   * Must tolerate being called for a loader it has no work for. The
--     shipped body is a deliberate no-op, so this is the default behaviour.
--
-- The body below is a SKELETON. Replace it with the real dispatch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE core.usp_RunAdditionalProcesses
    @LoaderName VARCHAR(150),
    @RunId      UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- No-op until real post-run work is added. Unknown loader names must fall
    -- through here quietly: the host calls this for EVERY loader it runs.
    --
    -- Example of the shape intended, kept commented so the shipped proc stays
    -- a no-op:
    --
    -- IF @LoaderName = 'Platts'
    --     EXEC SomeOtherDatabase.dbo.usp_RebuildPlattsAggregates @RunId = @RunId;
    -- ELSE IF @LoaderName = 'IHSPointLogic'
    --     EXEC IHSPointLogic.arm.usp_RefreshSomething @RunId = @RunId;

    RETURN;
END
GO
