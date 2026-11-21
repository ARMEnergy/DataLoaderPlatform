-- =============================================================================
-- 008_WatchdogDataVolume.sql
-- Teaches core.usp_RecordWatchdogPass how to close DATA_VOLUME findings, and
-- grants the watchdog identity SELECT on core.WatchdogFinding.
--
-- Design: docs/superpowers/specs/2026-11-20-watchdog-data-volume-design.md
--         (section D8; the closure rules come from amendments A3 and A4).
--
-- What changes, and ONLY this (everything else is 007's proc, copied verbatim):
--   1. The step 3-5 closing CASE gains a DATA_VOLUME branch. It honours the
--      hints AgedOutUnresolved (the day left the window unconfirmed),
--      LoaderDisabled (loader / its volume check / the master switch is off --
--      A4) and ConfigRemoved (loader gone from Platform:Watchdog:Loaders); any
--      other hint, or none, closes NotDetected (the day was re-judged in range:
--      a rerun fixed it, or BaselineFromCentral declared that level normal).
--   2. The step 3-5 WHERE skips a DATA_VOLUME finding hinted 'KeepOpen' -- its
--      day is still in the window but could not be judged this pass (too few
--      samples, median under MinMedianRows, invalid per-loader config, no
--      loader-day), and closing it as NotDetected would claim a fix nobody saw.
--      'KeepOpen' is a HINT only, never a stored ResolutionReason (it is not in
--      CK_WatchdogFinding_ResolutionReason), and it has no effect on any other
--      signal.
--   3. GRANT SELECT ON core.WatchdogFinding TO [ARM\sqltask_svc] -- the sink
--      reads open DATA_VOLUME findings and the evaluator reads excluded days
--      (design D3, R.8).
--
-- No table, TVP type, constraint, index or view changes: 'DATA_VOLUME' is
-- already allowed by CK_WatchdogFinding_Signal (007).
--
-- ⚠ ORDER: 007 must already be deployed. Re-running 007 AFTER this script
--   reverts the proc to its 007 body (DATA_VOLUME would then close NotDetected
--   regardless of hints and KeepOpen would be ignored) -- re-run 008 after it.
-- ⚠ Collation and ANSI settings: see 007's header. The proc uses table
--   variables, not #temp tables, and ANSI_NULLS / QUOTED_IDENTIFIER are set ON
--   before it because the table carries filtered indexes.
--
-- Re-runnable: CREATE OR ALTER keeps the proc's existing grants; the grant is
-- idempotent and guarded on the principal existing.
-- =============================================================================

USE DataLoaderPlatform;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

-- ----------------------------------------------------------------------------
-- core.usp_RecordWatchdogPass -- called once per pass by SqlWatchdogFindingSink.
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
-- * DATA_VOLUME (008): a finding not re-detected closes with the caller's hint
--   when it is AgedOutUnresolved (day left the window), LoaderDisabled or
--   ConfigRemoved; otherwise NotDetected (the day was re-judged and is in
--   range). A 'KeepOpen' hint leaves it OPEN: the day is in the window but was
--   not judgeable this pass, and silence is not health. It ages out later.
--   'KeepOpen' is ignored for every other signal.
-- * DATA_STALE is reserved: closes NotDetected.
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
    --      A DATA_VOLUME finding hinted 'KeepOpen' is not closed at all (008).
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
                   WHEN w.Signal = 'DATA_VOLUME' THEN
                       CASE WHEN h.ResolutionReason IN ('AgedOutUnresolved', 'LoaderDisabled', 'ConfigRemoved')
                            THEN h.ResolutionReason ELSE 'NotDetected' END
                   ELSE 'NotDetected'      -- DEGRADED (check ran this pass), DATA_STALE (reserved)
               END
      FROM core.WatchdogFinding AS w WITH (UPDLOCK, HOLDLOCK)
      JOIN @Checked AS c ON c.Signal = w.Signal
      LEFT JOIN @ResolvedHints AS h ON h.FindingKey = w.FindingKey
     WHERE w.ResolvedAtUtc IS NULL
       AND NOT EXISTS (SELECT 1 FROM @Findings AS f WHERE f.FindingKey = w.FindingKey)
       -- ISNULL: h is a LEFT JOIN, and a NULL comparison inside NOT (...) would
       -- make the predicate UNKNOWN and silently skip every un-hinted DATA_VOLUME row.
       AND NOT (w.Signal = 'DATA_VOLUME' AND ISNULL(h.ResolutionReason, '') = 'KeepOpen');

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
-- Grant. Applied only if the principal exists in this database, so the script
-- never fails half-way on a missing user; a missing one is PRINTed.
--
-- ARM\sqltask_svc runs the \DLP\Watchdog task. It is db_owner today, which
-- makes this grant redundant for it -- stated anyway so the intended least
-- privilege survives a future role cleanup (design R.8). The volume check reads
-- core.WatchdogFinding directly (excluded sample days include RESOLVED rows,
-- which core.vw_WatchdogFinding_Open does not expose).
-- ----------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID(N'ARM\sqltask_svc') IS NOT NULL
BEGIN
    GRANT SELECT ON OBJECT::core.WatchdogFinding TO [ARM\sqltask_svc];
END
ELSE
    PRINT 'ARM\sqltask_svc has no user in this database -- watchdog SELECT on core.WatchdogFinding NOT applied.';
GO
