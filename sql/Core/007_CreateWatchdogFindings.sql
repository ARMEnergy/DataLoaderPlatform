-- =============================================================================
-- 007_CreateWatchdogFindings.sql
-- Durable record of what the watchdog (DataLoader.Host.exe --watchdog) found:
--
--   core.WatchdogPass                  one row per pass -- also the heartbeat
--   core.WatchdogFinding               one row per finding, first detection -> resolution
--   core.WatchdogFindingType           TVP: every finding detected in one pass
--   core.WatchdogResolutionHintType    TVP: closing reason for open findings
--   core.usp_RecordWatchdogPass        the ONLY write path (one transaction per pass)
--   core.vw_WatchdogFinding_Open       the read surface for the DBA Agent
--
-- Requirements: 2026-11-12-watchdog-findings-requirements.md (DBA Agent).
-- This records DETECTION ONLY. Acknowledgement, assignment and notification
-- state belong to the DBA Agent's own database (DBAdmin), never here.
--
-- Rules the proc enforces
-- -----------------------------------------------------------------------------
-- * ONE open finding per FindingKey (filtered unique index). A key that clears
--   and later comes back is a NEW row -- a new incident with its own history.
-- * A DEGRADED SIGNAL NEVER RESOLVES ANYTHING. A finding is closed only when its
--   Signal is in @SignalsChecked and NOT in @SignalsDegraded. Silence from a
--   check that could not run is not health.
-- * AGEING OUT IS NOT BEING FIXED. A STUCK run whose core.LoaderRun row is still
--   open, or a FAILED run with no confirmed recovery, closes as
--   AgedOutUnresolved -- never NotDetected. A FAILED close with no hint from the
--   caller defaults to AgedOutUnresolved: never claim a fix nobody confirmed.
-- * DEGRADED findings: the caller puts 'DEGRADED' in @SignalsChecked whenever the
--   pass got far enough to run every check (i.e. not a Status = 'Failed' pass)
--   and lists every check that could not run as a DEGRADED|<code> finding. So a
--   DEGRADED|<code> missing from such a pass means that check ran -> NotDetected.
-- * Suppressed (already self-healed) FAILED rows are inserted ALREADY RESOLVED,
--   and only if NO row with that key exists yet -- a self-healed failure stays
--   in the look-back window for ~2 passes and must be counted once, not twice.
--   If the key is OPEN (it was unresolved last pass and has healed since), the
--   open row is closed as SelfHealed instead.
--
-- ⚠ Collation: this database's default (Latin1_General_CI_AS_KS_WS) differs
--   from the server's (SQL_Latin1_General_CP1_CI_AS). The proc uses TABLE
--   VARIABLES, which take the database collation; a #temp table would take
--   tempdb's and fail every string comparison against these tables.
-- ⚠ Filtered indexes require ANSI_NULLS and QUOTED_IDENTIFIER ON for any
--   module that writes to the table; both are set explicitly before the proc.
-- ⚠ The TVP types cannot be altered while the proc references them. To change a
--   type: drop the proc, drop and recreate the type, re-run this script.
--
-- Re-runnable: tables/types/indexes are creation-guarded, the proc and view use
-- CREATE OR ALTER (which keeps their grants), and the grants are idempotent.
-- =============================================================================

USE DataLoaderPlatform;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

-- ----------------------------------------------------------------------------
-- core.WatchdogPass -- one row per watchdog run. MAX(StartedAtUtc) is the
-- heartbeat the off-box check on ARMH-DEVDB01 reads.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.WatchdogPass', 'U') IS NULL
BEGIN
    CREATE TABLE core.WatchdogPass
    (
        PassId          BIGINT IDENTITY(1,1) NOT NULL,
        StartedAtUtc    DATETIME2(3)         NOT NULL,
        CompletedAtUtc  DATETIME2(3)         NULL,       -- NULL = the pass died mid-write
        HostName        NVARCHAR(128)        NOT NULL,
        Status          VARCHAR(12)          NOT NULL,
        SignalsChecked  VARCHAR(200)         NOT NULL,   -- CSV of signals evaluated successfully
        SignalsDegraded VARCHAR(200)         NULL,       -- CSV of signals that could not be checked
        OpenCount       INT                  NOT NULL CONSTRAINT DF_WatchdogPass_OpenCount DEFAULT (0),
        AppVersion      VARCHAR(50)          NULL,       -- Releases\<folder> the exe ran from

        CONSTRAINT PK_WatchdogPass PRIMARY KEY CLUSTERED (PassId),
        CONSTRAINT CK_WatchdogPass_Status CHECK (Status IN ('Complete', 'Degraded', 'Failed'))
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('core.WatchdogPass') AND name = 'IX_WatchdogPass_StartedAtUtc')
BEGIN
    CREATE NONCLUSTERED INDEX IX_WatchdogPass_StartedAtUtc
        ON core.WatchdogPass (StartedAtUtc DESC);
END
GO

-- ----------------------------------------------------------------------------
-- core.WatchdogFinding -- one row per finding, from first detection to
-- resolution. Every *Utc column is UTC; Central-time source values appear only
-- inside DetailJson, under a *Central name.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('core.WatchdogFinding', 'U') IS NULL
BEGIN
    CREATE TABLE core.WatchdogFinding
    (
        FindingId          BIGINT IDENTITY(1,1) NOT NULL,
        FindingKey         NVARCHAR(300)        NOT NULL,
        Signal             VARCHAR(20)          NOT NULL,
        LoaderName         NVARCHAR(128)        NULL,
        RunId              UNIQUEIDENTIFIER     NULL,
        Severity           VARCHAR(10)          NOT NULL,
        Summary            NVARCHAR(400)        NOT NULL,
        DetailJson         NVARCHAR(MAX)        NOT NULL,
        FirstDetectedAtUtc DATETIME2(3)         NOT NULL,
        LastDetectedAtUtc  DATETIME2(3)         NOT NULL,
        DetectionCount     INT                  NOT NULL CONSTRAINT DF_WatchdogFinding_DetectionCount DEFAULT (1),
        FirstPassId        BIGINT               NOT NULL,
        LastPassId         BIGINT               NOT NULL,
        ResolvedAtUtc      DATETIME2(3)         NULL,    -- NULL = open
        ResolvedPassId     BIGINT               NULL,
        ResolutionReason   VARCHAR(20)          NULL,

        CONSTRAINT PK_WatchdogFinding PRIMARY KEY CLUSTERED (FindingId),
        CONSTRAINT FK_WatchdogFinding_FirstPass    FOREIGN KEY (FirstPassId)    REFERENCES core.WatchdogPass (PassId),
        CONSTRAINT FK_WatchdogFinding_LastPass     FOREIGN KEY (LastPassId)     REFERENCES core.WatchdogPass (PassId),
        CONSTRAINT FK_WatchdogFinding_ResolvedPass FOREIGN KEY (ResolvedPassId) REFERENCES core.WatchdogPass (PassId),
        CONSTRAINT CK_WatchdogFinding_Signal CHECK (Signal IN
            ('OVERDUE', 'STUCK', 'FAILED', 'MISCONFIGURED', 'DEGRADED', 'DATA_VOLUME', 'DATA_STALE')),
        CONSTRAINT CK_WatchdogFinding_Severity CHECK (Severity IN ('Critical', 'Warning', 'Info')),
        CONSTRAINT CK_WatchdogFinding_DetailJson CHECK (ISJSON(DetailJson) = 1),
        CONSTRAINT CK_WatchdogFinding_ResolutionReason CHECK (ResolutionReason IS NULL OR ResolutionReason IN
            ('NotDetected', 'SelfHealed', 'AgedOutUnresolved', 'LoaderDisabled', 'ConfigRemoved')),
        -- Open: all three resolution columns NULL. Resolved: all three set.
        CONSTRAINT CK_WatchdogFinding_Resolution CHECK (
            (ResolvedAtUtc IS NULL     AND ResolvedPassId IS NULL     AND ResolutionReason IS NULL) OR
            (ResolvedAtUtc IS NOT NULL AND ResolvedPassId IS NOT NULL AND ResolutionReason IS NOT NULL)),
        CONSTRAINT CK_WatchdogFinding_DetectionCount CHECK (DetectionCount >= 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('core.WatchdogFinding') AND name = 'UX_WatchdogFinding_OpenKey')
BEGIN
    -- One open finding per key. Backs the proc's idempotency.
    CREATE UNIQUE NONCLUSTERED INDEX UX_WatchdogFinding_OpenKey
        ON core.WatchdogFinding (FindingKey)
        WHERE ResolvedAtUtc IS NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('core.WatchdogFinding') AND name = 'IX_WatchdogFinding_Key')
BEGIN
    -- Unfiltered, for the proc's "does ANY row with this key exist" check (step 2d):
    -- the filtered unique index above cannot serve it, and without this that check is
    -- a serializable scan that range-locks the whole table for the transaction.
    CREATE NONCLUSTERED INDEX IX_WatchdogFinding_Key
        ON core.WatchdogFinding (FindingKey)
        INCLUDE (ResolvedAtUtc);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('core.WatchdogFinding') AND name = 'IX_WatchdogFinding_Open')
BEGIN
    CREATE NONCLUSTERED INDEX IX_WatchdogFinding_Open
        ON core.WatchdogFinding (Signal, LoaderName)
        INCLUDE (Severity, LastDetectedAtUtc)
        WHERE ResolvedAtUtc IS NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('core.WatchdogFinding') AND name = 'IX_WatchdogFinding_LastDetected')
BEGIN
    CREATE NONCLUSTERED INDEX IX_WatchdogFinding_LastDetected
        ON core.WatchdogFinding (LastDetectedAtUtc DESC)
        INCLUDE (Signal, LoaderName, ResolvedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- TVP types. Column NAME + ORDER + TYPE must match
-- SqlWatchdogFindingSink.BuildFindingsTable / BuildHintsTable exactly.
-- ----------------------------------------------------------------------------
IF TYPE_ID('core.WatchdogFindingType') IS NULL
BEGIN
    CREATE TYPE core.WatchdogFindingType AS TABLE
    (
        FindingKey   NVARCHAR(300)    NOT NULL PRIMARY KEY,
        Signal       VARCHAR(20)      NOT NULL,
        LoaderName   NVARCHAR(128)    NULL,
        RunId        UNIQUEIDENTIFIER NULL,
        Severity     VARCHAR(10)      NOT NULL,
        Summary      NVARCHAR(400)    NOT NULL,
        DetailJson   NVARCHAR(MAX)    NOT NULL,
        IsSuppressed BIT              NOT NULL
    );
END
GO

IF TYPE_ID('core.WatchdogResolutionHintType') IS NULL
BEGIN
    CREATE TYPE core.WatchdogResolutionHintType AS TABLE
    (
        FindingKey       NVARCHAR(300) NOT NULL PRIMARY KEY,
        ResolutionReason VARCHAR(20)   NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- core.usp_RecordWatchdogPass -- called once per pass by SqlWatchdogFindingSink.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE core.usp_RecordWatchdogPass
    @StartedAtUtc    DATETIME2(3),
    @HostName        NVARCHAR(128),
    @Status          VARCHAR(12),
    @SignalsChecked  VARCHAR(200),
    @SignalsDegraded VARCHAR(200) = NULL,
    @AppVersion      VARCHAR(50)  = NULL,
    @Findings        core.WatchdogFindingType        READONLY,
    @ResolvedHints   core.WatchdogResolutionHintType READONLY,
    @PassId          BIGINT        = NULL OUTPUT,
    @RetentionError  NVARCHAR(400) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    -- Table variables, NOT #temp tables -- see the collation note in the header.
    DECLARE @Checked  TABLE (Signal VARCHAR(20) NOT NULL PRIMARY KEY);
    DECLARE @Degraded TABLE (Signal VARCHAR(20) NOT NULL PRIMARY KEY);

    INSERT @Degraded (Signal)
    SELECT DISTINCT CAST(LTRIM(RTRIM(s.value)) AS VARCHAR(20))
    FROM STRING_SPLIT(ISNULL(@SignalsDegraded, ''), ',') AS s
    WHERE LTRIM(RTRIM(s.value)) <> '';

    -- A signal listed as both checked and degraded counts as DEGRADED: the
    -- conservative reading, since only a checked signal may close anything.
    INSERT @Checked (Signal)
    SELECT DISTINCT CAST(LTRIM(RTRIM(s.value)) AS VARCHAR(20))
    FROM STRING_SPLIT(ISNULL(@SignalsChecked, ''), ',') AS s
    WHERE LTRIM(RTRIM(s.value)) <> ''
      AND NOT EXISTS (SELECT 1 FROM @Degraded d WHERE d.Signal = CAST(LTRIM(RTRIM(s.value)) AS VARCHAR(20)));

    BEGIN TRANSACTION;

    -- 1. The pass row (CompletedAtUtc stays NULL until step 6).
    INSERT core.WatchdogPass (StartedAtUtc, HostName, Status, SignalsChecked, SignalsDegraded, OpenCount, AppVersion)
    VALUES (@StartedAtUtc, @HostName, @Status, ISNULL(@SignalsChecked, ''), NULLIF(@SignalsDegraded, ''), 0, @AppVersion);

    SET @PassId = CAST(SCOPE_IDENTITY() AS BIGINT);

    -- 2a. Detected again: refresh the open row.
    UPDATE w
       SET LastDetectedAtUtc = @Now,
           LastPassId        = @PassId,
           DetectionCount    = w.DetectionCount + 1,
           LoaderName        = f.LoaderName,   -- follows a case-only config rename
           Severity          = f.Severity,
           Summary           = f.Summary,
           DetailJson        = f.DetailJson
      FROM core.WatchdogFinding AS w WITH (UPDLOCK, HOLDLOCK)
      JOIN @Findings AS f ON f.FindingKey = w.FindingKey
     WHERE w.ResolvedAtUtc IS NULL
       AND f.IsSuppressed = 0;

    -- 2b. Suppressed (self-healed) while OPEN: it was unresolved on an earlier
    --     pass and every unit has succeeded since. Close it; keep its Severity,
    --     because it WAS a real unresolved failure while it was open.
    UPDATE w
       SET LastDetectedAtUtc = @Now,
           LastPassId        = @PassId,
           DetectionCount    = w.DetectionCount + 1,
           Summary           = f.Summary,
           DetailJson        = f.DetailJson,
           ResolvedAtUtc     = @Now,
           ResolvedPassId    = @PassId,
           ResolutionReason  = 'SelfHealed'
      FROM core.WatchdogFinding AS w WITH (UPDLOCK, HOLDLOCK)
      JOIN @Findings AS f ON f.FindingKey = w.FindingKey
     WHERE w.ResolvedAtUtc IS NULL
       AND f.IsSuppressed = 1;

    -- 2c. Newly detected: insert open.
    INSERT core.WatchdogFinding
        (FindingKey, Signal, LoaderName, RunId, Severity, Summary, DetailJson,
         FirstDetectedAtUtc, LastDetectedAtUtc, DetectionCount, FirstPassId, LastPassId)
    SELECT f.FindingKey, f.Signal, f.LoaderName, f.RunId, f.Severity, f.Summary, f.DetailJson,
           @Now, @Now, 1, @PassId, @PassId
      FROM @Findings AS f
     WHERE f.IsSuppressed = 0
       AND NOT EXISTS (SELECT 1 FROM core.WatchdogFinding AS w WITH (UPDLOCK, HOLDLOCK)
                       WHERE w.FindingKey = f.FindingKey AND w.ResolvedAtUtc IS NULL);

    -- 2d. Suppressed and never seen before: insert ALREADY RESOLVED, for trend
    --     history only. Skipped when ANY row with the key exists (open rows were
    --     closed in 2b; a resolved row means an earlier pass already counted it).
    INSERT core.WatchdogFinding
        (FindingKey, Signal, LoaderName, RunId, Severity, Summary, DetailJson,
         FirstDetectedAtUtc, LastDetectedAtUtc, DetectionCount, FirstPassId, LastPassId,
         ResolvedAtUtc, ResolvedPassId, ResolutionReason)
    SELECT f.FindingKey, f.Signal, f.LoaderName, f.RunId, f.Severity, f.Summary, f.DetailJson,
           @Now, @Now, 1, @PassId, @PassId,
           @Now, @PassId, 'SelfHealed'
      FROM @Findings AS f
     WHERE f.IsSuppressed = 1
       AND NOT EXISTS (SELECT 1 FROM core.WatchdogFinding AS w WITH (UPDLOCK, HOLDLOCK)
                       WHERE w.FindingKey = f.FindingKey);

    -- 3-5. Close open findings that were NOT detected this pass -- only for
    --      signals that were actually checked (degraded ones are absent from
    --      @Checked, so step 4 "never close a degraded signal" holds by
    --      construction). Hints are honoured only where they make sense for
    --      the signal; anything else falls back to the conservative reason.
    UPDATE w
       SET ResolvedAtUtc    = @Now,
           ResolvedPassId   = @PassId,
           ResolutionReason =
               CASE
                   WHEN w.Signal = 'FAILED' THEN
                       CASE WHEN h.ResolutionReason IN ('SelfHealed', 'AgedOutUnresolved')
                            THEN h.ResolutionReason ELSE 'AgedOutUnresolved' END
                   WHEN w.Signal = 'STUCK' THEN
                       CASE WHEN EXISTS (SELECT 1 FROM core.LoaderRun AS lr
                                         WHERE lr.RunId = w.RunId AND lr.CompletedAtUtc IS NULL)
                            THEN 'AgedOutUnresolved' ELSE 'NotDetected' END
                   WHEN w.Signal IN ('OVERDUE', 'MISCONFIGURED') THEN
                       CASE WHEN h.ResolutionReason IN ('LoaderDisabled', 'ConfigRemoved')
                            THEN h.ResolutionReason ELSE 'NotDetected' END
                   ELSE 'NotDetected'      -- DEGRADED (check ran this pass), DATA_* (reserved)
               END
      FROM core.WatchdogFinding AS w WITH (UPDLOCK, HOLDLOCK)
      JOIN @Checked AS c ON c.Signal = w.Signal
      LEFT JOIN @ResolvedHints AS h ON h.FindingKey = w.FindingKey
     WHERE w.ResolvedAtUtc IS NULL
       AND NOT EXISTS (SELECT 1 FROM @Findings AS f WHERE f.FindingKey = w.FindingKey);

    -- 6. Close the pass row.
    UPDATE core.WatchdogPass
       SET CompletedAtUtc = SYSUTCDATETIME(),
           OpenCount      = (SELECT COUNT(*) FROM core.WatchdogFinding WHERE ResolvedAtUtc IS NULL)
     WHERE PassId = @PassId;

    COMMIT TRANSACTION;

    -- 8. Retention -- AFTER the commit and in its own TRY/CATCH, so a retention
    --    problem can never undo or misreport the pass that was just recorded.
    --    The caller logs @RetentionError at Warning.
    BEGIN TRY
        DECLARE @Batch INT = 5000, @Rows INT = 1, @Loops INT = 0;

        WHILE @Rows > 0 AND @Loops < 50
        BEGIN
            DELETE TOP (@Batch) FROM core.WatchdogFinding
             WHERE ResolvedAtUtc IS NOT NULL
               AND ResolvedAtUtc < DATEADD(DAY, -180, @Now);
            SET @Rows = @@ROWCOUNT;
            SET @Loops += 1;
        END

        SELECT @Rows = 1, @Loops = 0;
        WHILE @Rows > 0 AND @Loops < 50
        BEGIN
            DELETE TOP (@Batch) p
              FROM core.WatchdogPass AS p
             WHERE p.StartedAtUtc < DATEADD(DAY, -90, @Now)
               AND p.PassId <> @PassId
               AND NOT EXISTS (SELECT 1 FROM core.WatchdogFinding AS w
                               WHERE w.FirstPassId = p.PassId
                                  OR w.LastPassId = p.PassId
                                  OR w.ResolvedPassId = p.PassId);
            SET @Rows = @@ROWCOUNT;
            SET @Loops += 1;
        END
    END TRY
    BEGIN CATCH
        -- No ROLLBACK here: retention opens no transaction of its own (each DELETE
        -- autocommits). Inside a CALLER's transaction (e.g. the rollback-only test
        -- script) XACT_ABORT has already doomed it, and ending it is the caller's job --
        -- rolling it back here would silently undo the caller's work, pass included.
        SET @RetentionError = LEFT(CONCAT(N'Retention failed (', ERROR_NUMBER(), N'): ', ERROR_MESSAGE()), 400);
    END CATCH
END
GO

-- ----------------------------------------------------------------------------
-- core.vw_WatchdogFinding_Open -- the DBA Agent's main read surface.
-- ----------------------------------------------------------------------------
CREATE OR ALTER VIEW core.vw_WatchdogFinding_Open
AS
SELECT FindingId,
       FindingKey,
       Signal,
       LoaderName,
       RunId,
       Severity,
       Summary,
       DetailJson,
       FirstDetectedAtUtc,
       LastDetectedAtUtc,
       DetectionCount,
       FirstPassId,
       LastPassId,
       DATEDIFF(MINUTE, FirstDetectedAtUtc, SYSUTCDATETIME()) AS OpenMinutes
  FROM core.WatchdogFinding
 WHERE ResolvedAtUtc IS NULL;
GO

-- ----------------------------------------------------------------------------
-- Grants. Each is applied only if the principal exists in this database, so the
-- script never fails half-way on a missing user; a missing one is PRINTed.
--
-- ARM\sqltask_svc runs the \DLP\Watchdog task (verified 2026-11-12). It is
-- db_owner today, which makes these grants redundant for it -- they are stated
-- anyway so the intended least privilege survives a future role cleanup.
-- ----------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID(N'ARM\sqltask_svc') IS NOT NULL
BEGIN
    GRANT EXECUTE ON OBJECT::core.usp_RecordWatchdogPass       TO [ARM\sqltask_svc];
    GRANT EXECUTE ON TYPE::core.WatchdogFindingType            TO [ARM\sqltask_svc];
    GRANT EXECUTE ON TYPE::core.WatchdogResolutionHintType     TO [ARM\sqltask_svc];
    GRANT SELECT  ON OBJECT::core.vw_WatchdogFinding_Open      TO [ARM\sqltask_svc];
END
ELSE
    PRINT 'ARM\sqltask_svc has no user in this database -- watchdog grants NOT applied.';
GO

-- The DBA Agent reads as the SQL login/user [DBAgent] (SQL authentication, not a
-- Windows account; verified on ARMH-OPSDB01 2026-11-13). Read-only: SELECT only.
IF DATABASE_PRINCIPAL_ID(N'DBAgent') IS NOT NULL
BEGIN
    GRANT SELECT ON OBJECT::core.WatchdogPass            TO [DBAgent];
    GRANT SELECT ON OBJECT::core.WatchdogFinding         TO [DBAgent];
    GRANT SELECT ON OBJECT::core.vw_WatchdogFinding_Open TO [DBAgent];
END
ELSE
    PRINT 'DBAgent has no user in this database -- DBA Agent read grants NOT applied (create the user for the DBAgent login, then re-run this script).';
GO
