-- =============================================================================
-- 003_CreateOverlapGuard.sql
-- Prevent two scheduled runs of the same loader from overlapping.
--
-- A loader that takes 45 min on a 30-min schedule will start a second
-- process while the first is still running. Without a guard, both would
-- enumerate the same work units and race to call usp_BeginLoadLog. The
-- load-log check is racy (read-then-insert) so both could open rows for
-- the same unit before either completes.
--
-- This proc uses SQL Server's session-scoped sp_getapplock to give one
-- process exclusive ownership of a (LoaderId) name for the lifetime of
-- its connection. The lock is automatic-release on disconnect, so a
-- crashed runner doesn't leave it stuck.
-- =============================================================================

IF OBJECT_ID('core.usp_TryAcquireLoaderLock', 'P') IS NOT NULL
    DROP PROCEDURE core.usp_TryAcquireLoaderLock;
GO
CREATE PROCEDURE core.usp_TryAcquireLoaderLock
    @LoaderId      NVARCHAR(100),
    @TimeoutMillis INT          = 0,   -- 0 = return immediately if not free
    @Acquired      BIT          OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @result INT;
    DECLARE @resource NVARCHAR(255) = N'DataLoader:' + @LoaderId;

    -- sp_getapplock return values:
    --    0  Lock acquired synchronously
    --    1  Lock acquired after waiting
    --   -1  Lock request timed out
    --   -2  Lock request cancelled
    --   -3  Lock request deadlocked
    -- -999  Parameter validation / other error
    EXEC @result = sp_getapplock
        @Resource     = @resource,
        @LockMode     = 'Exclusive',
        @LockOwner    = 'Session',
        @LockTimeout  = @TimeoutMillis;

    SET @Acquired = CASE WHEN @result IN (0, 1) THEN 1 ELSE 0 END;
END
GO

-- ----------------------------------------------------------------------------
-- core.usp_ReleaseLoaderLock — explicit release for graceful shutdown.
-- The Session-scoped lock auto-releases on disconnect anyway, but calling
-- this lets the next scheduled run start immediately instead of waiting
-- for connection pooling to time out the dead session.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.usp_ReleaseLoaderLock', 'P') IS NOT NULL
    DROP PROCEDURE core.usp_ReleaseLoaderLock;
GO
CREATE PROCEDURE core.usp_ReleaseLoaderLock
    @LoaderId NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @resource NVARCHAR(255) = N'DataLoader:' + @LoaderId;
    -- Suppress "lock not held" errors — the lock may already have been
    -- auto-released if the connection dropped.
    BEGIN TRY
        EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';
    END TRY
    BEGIN CATCH
        -- swallow
    END CATCH
END
GO
