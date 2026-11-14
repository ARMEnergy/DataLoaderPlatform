-- =============================================================================
-- 007_WatchdogFindings.Tests.sql
-- Behaviour tests for core.usp_RecordWatchdogPass (sql/Core/007). The proc's
-- closure rules cannot be exercised by `dotnet test` (no SQL Server in the
-- test run), so this script is their test surface.
--
-- 🔒 ROLLBACK-ONLY. Everything runs in ONE batch inside ONE transaction that
-- is always rolled back, success or failure. It still takes locks and runs the
-- proc's writes inside that transaction, so it is a WRITE for the purposes of
-- CLAUDE.md's production rule: run it on a DEV/test copy of DataLoaderPlatform
-- with 007 deployed, never on ARMH-OPSDB01 unless specifically asked.
--
-- Note the proc closes EVERY open finding of a checked signal that a pass does
-- not report — real ones included. That is fine here (all rolled back), and is
-- why every assertion filters to this script's own 'zzTest' keys.
--
-- Output: one 'PASS <n>' line per scenario, then 'ALL WATCHDOG PROC TESTS
-- PASSED'. Any failure THROWs 50000+<n> naming the scenario.
-- =============================================================================

USE DataLoaderPlatform;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @F core.WatchdogFindingType;
    DECLARE @H core.WatchdogResolutionHintType;
    DECLARE @PassId BIGINT, @Ret NVARCHAR(400), @Now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @All VARCHAR(200) = 'OVERDUE,MISCONFIGURED,STUCK,FAILED,DEGRADED';
    DECLARE @First DATETIME2(3), @PrevPass BIGINT;

    DECLARE @StuckOpen   UNIQUEIDENTIFIER = NEWID();   -- core.LoaderRun row stays open
    DECLARE @StuckClosed UNIQUEIDENTIFIER = NEWID();   -- core.LoaderRun row completes
    DECLARE @FailHealed  UNIQUEIDENTIFIER = NEWID();
    DECLARE @FailAged    UNIQUEIDENTIFIER = NEWID();
    DECLARE @FailInWin   UNIQUEIDENTIFIER = NEWID();
    DECLARE @FailSupp    UNIQUEIDENTIFIER = NEWID();

    DECLARE @kStuckOpen   NVARCHAR(300) = N'STUCK|'  + CONVERT(NVARCHAR(36), @StuckOpen);
    DECLARE @kStuckClosed NVARCHAR(300) = N'STUCK|'  + CONVERT(NVARCHAR(36), @StuckClosed);
    DECLARE @kFailHealed  NVARCHAR(300) = N'FAILED|' + CONVERT(NVARCHAR(36), @FailHealed);
    DECLARE @kFailAged    NVARCHAR(300) = N'FAILED|' + CONVERT(NVARCHAR(36), @FailAged);
    DECLARE @kFailInWin   NVARCHAR(300) = N'FAILED|' + CONVERT(NVARCHAR(36), @FailInWin);
    DECLARE @kFailSupp    NVARCHAR(300) = N'FAILED|' + CONVERT(NVARCHAR(36), @FailSupp);

    INSERT core.LoaderRun (RunId, StartedAtUtc, CompletedAtUtc, Success, HostName)
    VALUES (@StuckOpen,   DATEADD(HOUR, -8, @Now), NULL, NULL, N'zzTest'),
           (@StuckClosed, DATEADD(HOUR, -8, @Now), NULL, NULL, N'zzTest');

    ---------------------------------------------------------------------------
    -- 1. Overdue for 3 consecutive passes -> one open row, DetectionCount 3,
    --    FirstDetectedAtUtc unchanged.  (+ 9: identical input never makes a
    --    second open row.)
    ---------------------------------------------------------------------------
    INSERT @F VALUES (N'OVERDUE|zzTest_A', 'OVERDUE', N'zzTest_A', NULL, 'Critical', N'zzTest_A overdue', N'{"neverRun":true}', 0);

    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    IF @@TRANCOUNT <> 1 THROW 50099, 'proc left the test transaction', 1;
    SELECT @First = FirstDetectedAtUtc FROM core.WatchdogFinding WHERE FindingKey = N'OVERDUE|zzTest_A' AND ResolvedAtUtc IS NULL;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF (SELECT COUNT(*) FROM core.WatchdogFinding WHERE FindingKey = N'OVERDUE|zzTest_A') <> 1
        THROW 50001, '1: expected exactly one row for OVERDUE|zzTest_A', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding
                   WHERE FindingKey = N'OVERDUE|zzTest_A' AND ResolvedAtUtc IS NULL
                     AND DetectionCount = 3 AND FirstDetectedAtUtc = @First AND LastPassId = @PassId)
        THROW 50001, '1: expected open, DetectionCount = 3, FirstDetectedAtUtc unchanged', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogPass WHERE PassId = @PassId AND CompletedAtUtc IS NOT NULL
                     AND OpenCount = (SELECT COUNT(*) FROM core.WatchdogFinding WHERE ResolvedAtUtc IS NULL))
        THROW 50001, '1: pass row not completed or OpenCount wrong', 1;
    PRINT 'PASS 1 + 9 (repeat detection, idempotent per key, pass row completed)';

    ---------------------------------------------------------------------------
    -- 2. Overdue loader ran; next pass is clean -> closed NotDetected.
    ---------------------------------------------------------------------------
    DELETE @F;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding
                   WHERE FindingKey = N'OVERDUE|zzTest_A' AND ResolutionReason = 'NotDetected' AND ResolvedPassId = @PassId)
        THROW 50002, '2: expected OVERDUE|zzTest_A closed NotDetected', 1;
    PRINT 'PASS 2 (cleared -> NotDetected)';

    ---------------------------------------------------------------------------
    -- 3. Stuck query throws while a STUCK finding is open -> STUCK untouched,
    --    DEGRADED|STUCK_QUERY opened, pass Status = Degraded.
    ---------------------------------------------------------------------------
    INSERT @F VALUES (@kStuckOpen,   'STUCK', NULL, @StuckOpen,   'Warning', N'stuck', N'{}', 0),
                     (@kStuckClosed, 'STUCK', NULL, @StuckClosed, 'Warning', N'stuck', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    SET @PrevPass = @PassId;

    DELETE @F;
    INSERT @F VALUES (N'DEGRADED|STUCK_QUERY', 'DEGRADED', NULL, NULL, 'Critical', N'stuck runs (x)', N'{"checkCode":"STUCK_QUERY"}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Degraded', 'OVERDUE,MISCONFIGURED,FAILED,DEGRADED', 'STUCK', NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kStuckOpen AND ResolvedAtUtc IS NULL AND LastPassId = @PrevPass AND DetectionCount = 1)
        THROW 50003, '3: STUCK finding must be untouched while STUCK is degraded', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'DEGRADED|STUCK_QUERY' AND ResolvedAtUtc IS NULL)
        THROW 50003, '3: DEGRADED|STUCK_QUERY must be open', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogPass WHERE PassId = @PassId AND Status = 'Degraded' AND SignalsDegraded = 'STUCK')
        THROW 50003, '3: pass must be Degraded with SignalsDegraded = STUCK', 1;
    PRINT 'PASS 3 (degraded signal resolves nothing)';

    ---------------------------------------------------------------------------
    -- 4. Next pass, stuck query works and the run is still open -> DEGRADED
    --    closed NotDetected, STUCK still open.
    ---------------------------------------------------------------------------
    DELETE @F;
    INSERT @F VALUES (@kStuckOpen,   'STUCK', NULL, @StuckOpen,   'Warning', N'stuck', N'{}', 0),
                     (@kStuckClosed, 'STUCK', NULL, @StuckClosed, 'Warning', N'stuck', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'DEGRADED|STUCK_QUERY' AND ResolutionReason = 'NotDetected')
        THROW 50004, '4: DEGRADED|STUCK_QUERY must close NotDetected', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kStuckOpen AND ResolvedAtUtc IS NULL AND DetectionCount = 2)
        THROW 50004, '4: STUCK must still be open with DetectionCount = 2', 1;
    PRINT 'PASS 4 (check recovered -> DEGRADED closed, STUCK still open)';

    ---------------------------------------------------------------------------
    -- 4b. STUCK leaves the look-back: still-open run -> AgedOutUnresolved;
    --     run that completed -> NotDetected.
    ---------------------------------------------------------------------------
    UPDATE core.LoaderRun SET CompletedAtUtc = @Now, Success = 1 WHERE RunId = @StuckClosed;
    DELETE @F;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kStuckOpen AND ResolutionReason = 'AgedOutUnresolved')
        THROW 50014, '4b: still-open run must close AgedOutUnresolved, never NotDetected', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kStuckClosed AND ResolutionReason = 'NotDetected')
        THROW 50014, '4b: completed run must close NotDetected', 1;
    PRINT 'PASS 4b (STUCK aged out vs fixed)';

    ---------------------------------------------------------------------------
    -- 5 / 6 / 6b. FAILED: hinted SelfHealed; no hint -> AgedOutUnresolved (never
    --  claim a fix nobody confirmed); healed while still in the window (arrives
    --  as a suppressed row) -> the open row closes SelfHealed, no second row.
    ---------------------------------------------------------------------------
    INSERT @F VALUES (@kFailHealed, 'FAILED', N'zzTest_A', @FailHealed, 'Warning', N'f', N'{}', 0),
                     (@kFailAged,   'FAILED', N'zzTest_A', @FailAged,   'Warning', N'f', N'{}', 0),
                     (@kFailInWin,  'FAILED', N'zzTest_A', @FailInWin,  'Warning', N'f', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    DELETE @F;
    INSERT @F VALUES (@kFailInWin, 'FAILED', N'zzTest_A', @FailInWin, 'Info', N'f', N'{}', 1);
    INSERT @H VALUES (@kFailHealed, 'SelfHealed'),
                     (@kFailAged,   'LoaderDisabled');      -- nonsense for FAILED: must be ignored
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kFailHealed AND ResolutionReason = 'SelfHealed')
        THROW 50005, '5: hinted FAILED must close SelfHealed', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kFailAged AND ResolutionReason = 'AgedOutUnresolved')
        THROW 50006, '6: FAILED with no valid hint must close AgedOutUnresolved', 1;
    IF (SELECT COUNT(*) FROM core.WatchdogFinding WHERE FindingKey = @kFailInWin) <> 1
       OR NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kFailInWin
                      AND ResolutionReason = 'SelfHealed' AND Severity = 'Warning')
        THROW 50016, '6b: open FAILED arriving suppressed must close SelfHealed in place (one row, severity kept)', 1;
    PRINT 'PASS 5 + 6 + 6b (FAILED closure reasons)';

    ---------------------------------------------------------------------------
    -- 7. Already self-healed when first seen -> inserted resolved, Info,
    --    SelfHealed. Seen suppressed again next pass -> still ONE row.
    ---------------------------------------------------------------------------
    DELETE @F;
    INSERT @F VALUES (@kFailSupp, 'FAILED', N'zzTest_A', @FailSupp, 'Info', N'f', N'{}', 1);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF (SELECT COUNT(*) FROM core.WatchdogFinding WHERE FindingKey = @kFailSupp) <> 1
        THROW 50007, '7: a suppressed failure seen on two passes must be recorded once', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kFailSupp AND Severity = 'Info'
                   AND ResolutionReason = 'SelfHealed' AND ResolvedAtUtc = FirstDetectedAtUtc)
        THROW 50007, '7: expected resolved-on-insert, Info, SelfHealed', 1;
    PRINT 'PASS 7 (suppressed recorded once, already resolved)';

    ---------------------------------------------------------------------------
    -- 8. OVERDUE open, loader disabled -> LoaderDisabled; removed -> ConfigRemoved;
    --    a FAILED-only hint on OVERDUE is ignored -> NotDetected.
    ---------------------------------------------------------------------------
    DELETE @F;
    INSERT @F VALUES (N'OVERDUE|zzTest_B', 'OVERDUE', N'zzTest_B', NULL, 'Critical', N'b', N'{}', 0),
                     (N'OVERDUE|zzTest_C', 'OVERDUE', N'zzTest_C', NULL, 'Critical', N'c', N'{}', 0),
                     (N'MISCONFIGURED|zzTest_D', 'MISCONFIGURED', N'zzTest_D', NULL, 'Warning', N'd', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    DELETE @F;
    INSERT @H VALUES (N'OVERDUE|zzTest_B', 'LoaderDisabled'),
                     (N'OVERDUE|zzTest_C', 'SelfHealed'),
                     (N'MISCONFIGURED|zzTest_D', 'ConfigRemoved');
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'OVERDUE|zzTest_B' AND ResolutionReason = 'LoaderDisabled')
        THROW 50008, '8: expected LoaderDisabled', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'OVERDUE|zzTest_C' AND ResolutionReason = 'NotDetected')
        THROW 50008, '8: a hint invalid for OVERDUE must fall back to NotDetected', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'MISCONFIGURED|zzTest_D' AND ResolutionReason = 'ConfigRemoved')
        THROW 50008, '8: expected ConfigRemoved', 1;
    PRINT 'PASS 8 (LoaderDisabled / ConfigRemoved / invalid hint ignored)';

    ---------------------------------------------------------------------------
    -- 13. A signal listed as BOTH checked and degraded counts as degraded:
    --     its open findings must not close. (This is how NO_LOADERS_CONFIGURED
    --     protects OVERDUE even if a caller lists it in both.)
    ---------------------------------------------------------------------------
    DELETE @F;
    INSERT @F VALUES (N'OVERDUE|zzTest_F', 'OVERDUE', N'zzTest_F', NULL, 'Critical', N'f', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @F;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Degraded', @All, 'OVERDUE,MISCONFIGURED', NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'OVERDUE|zzTest_F' AND ResolvedAtUtc IS NULL)
        THROW 50013, '13: a signal both checked and degraded must close nothing', 1;
    PRINT 'PASS 13 (degraded wins over checked)';

    ---------------------------------------------------------------------------
    -- 12. Primary history query failed: Status Failed, nothing checked ->
    --     nothing closes; DEGRADED|HISTORY_QUERY opened.
    ---------------------------------------------------------------------------
    INSERT @F VALUES (N'OVERDUE|zzTest_E', 'OVERDUE', N'zzTest_E', NULL, 'Critical', N'e', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    DELETE @F;
    INSERT @F VALUES (N'DEGRADED|HISTORY_QUERY', 'DEGRADED', NULL, NULL, 'Critical', N'run history (x)', N'{"checkCode":"HISTORY_QUERY"}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Failed', '', @All, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'OVERDUE|zzTest_E' AND ResolvedAtUtc IS NULL)
        THROW 50012, '12: a Failed pass must close nothing', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = N'DEGRADED|HISTORY_QUERY' AND ResolvedAtUtc IS NULL)
        THROW 50012, '12: DEGRADED|HISTORY_QUERY must be open', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogPass WHERE PassId = @PassId AND Status = 'Failed' AND SignalsChecked = '')
        THROW 50012, '12: pass row must be Failed with SignalsChecked empty', 1;
    PRINT 'PASS 12 (Failed pass is a heartbeat that closes nothing)';

    -- The view exposes exactly the open rows.
    IF EXISTS (SELECT 1 FROM core.vw_WatchdogFinding_Open v
               JOIN core.WatchdogFinding w ON w.FindingId = v.FindingId
               WHERE w.ResolvedAtUtc IS NOT NULL)
        THROW 50020, 'view: returned a resolved finding', 1;
    PRINT 'PASS view';

    IF @Ret IS NOT NULL PRINT CONCAT('NOTE retention reported: ', @Ret);

    ROLLBACK TRANSACTION;
    PRINT 'ALL WATCHDOG PROC TESTS PASSED (rolled back)';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
