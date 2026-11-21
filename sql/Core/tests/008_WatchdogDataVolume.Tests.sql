-- =============================================================================
-- 008_WatchdogDataVolume.Tests.sql
-- Behaviour tests for the DATA_VOLUME closure rules that sql/Core/008 adds to
-- core.usp_RecordWatchdogPass (design: docs/superpowers/specs/
-- 2026-11-20-watchdog-data-volume-design.md, A3/A4/D8). The proc's closure
-- rules cannot be exercised by `dotnet test` (no SQL Server in the test run),
-- so this script is their test surface. 007's tests still cover every other
-- signal and should pass unchanged against the 008 proc.
--
-- 🔒 ROLLBACK-ONLY. Everything runs in ONE batch inside ONE transaction that
-- is always rolled back, success or failure. It still takes locks and runs the
-- proc's writes inside that transaction, so it is a WRITE for the purposes of
-- CLAUDE.md's production rule: run it on a DEV/test copy of DataLoaderPlatform
-- with 007 AND 008 deployed, never on ARMH-OPSDB01 unless specifically asked.
--
-- Note the proc closes EVERY open finding of a checked signal that a pass does
-- not report — real ones included. That is fine here (all rolled back), and is
-- why every assertion filters to this script's own 'zzTest' keys.
--
-- Output: one 'PASS <n>' line per scenario, then 'ALL WATCHDOG DATA_VOLUME
-- PROC TESTS PASSED'. Any failure THROWs 50100+<n> naming the scenario.
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
    DECLARE @All      VARCHAR(200) = 'OVERDUE,MISCONFIGURED,STUCK,FAILED,DEGRADED,DATA_VOLUME';
    DECLARE @NoVolume VARCHAR(200) = 'OVERDUE,MISCONFIGURED,STUCK,FAILED,DEGRADED';
    DECLARE @First DATETIME2(3), @PrevPass BIGINT;

    DECLARE @kRedetect   NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-05';
    DECLARE @kNoHint     NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-06';
    DECLARE @kAgedOut    NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-07';
    DECLARE @kDisabled   NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-08';
    DECLARE @kRemoved    NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-09';
    DECLARE @kKeepOpen   NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-10';
    DECLARE @kSelfHealed NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-11';
    DECLARE @kDegraded   NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-12';
    DECLARE @kUnchecked  NVARCHAR(300) = N'DATA_VOLUME|zzTestLoader|2026-01-13';
    DECLARE @kOverdueKO  NVARCHAR(300) = N'OVERDUE|zzTestLoader';

    ---------------------------------------------------------------------------
    -- 1. Open DATA_VOLUME finding re-detected with identical input -> one row,
    --    still open, DetectionCount 2, FirstDetectedAtUtc unchanged.
    --    (Also: identical input twice never makes a second open row.)
    ---------------------------------------------------------------------------
    INSERT @F VALUES (@kRedetect, 'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'zzTestLoader low', N'{"dayCentral":"2026-01-05"}', 0);

    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    IF @@TRANCOUNT <> 1 THROW 50199, 'proc left the test transaction', 1;
    SELECT @First = FirstDetectedAtUtc FROM core.WatchdogFinding WHERE FindingKey = @kRedetect AND ResolvedAtUtc IS NULL;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;

    IF (SELECT COUNT(*) FROM core.WatchdogFinding WHERE FindingKey = @kRedetect) <> 1
        THROW 50101, '1: expected exactly one row for the re-detected DATA_VOLUME key', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding
                   WHERE FindingKey = @kRedetect AND ResolvedAtUtc IS NULL
                     AND DetectionCount = 2 AND FirstDetectedAtUtc = @First AND LastPassId = @PassId)
        THROW 50101, '1: expected open, DetectionCount = 2, FirstDetectedAtUtc unchanged', 1;
    PRINT 'PASS 1 (re-detected -> refreshed, one open row per key)';

    ---------------------------------------------------------------------------
    -- 2. Closure reasons. Open seven DATA_VOLUME findings and one OVERDUE, then
    --    a clean pass with hints:
    --      no hint            -> NotDetected
    --      AgedOutUnresolved  -> AgedOutUnresolved
    --      LoaderDisabled     -> LoaderDisabled
    --      ConfigRemoved      -> ConfigRemoved
    --      KeepOpen           -> stays OPEN, untouched
    --      SelfHealed         -> not honoured for DATA_VOLUME -> NotDetected
    --      KeepOpen on OVERDUE-> ignored, closes per OVERDUE's rule -> NotDetected
    ---------------------------------------------------------------------------
    DELETE @F;
    INSERT @F VALUES (@kNoHint,     'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0),
                     (@kAgedOut,    'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0),
                     (@kDisabled,   'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0),
                     (@kRemoved,    'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0),
                     (@kKeepOpen,   'DATA_VOLUME', N'zzTestLoader', NULL, 'Info',    N'v', N'{}', 0),
                     (@kSelfHealed, 'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0),
                     (@kOverdueKO,  'OVERDUE',     N'zzTestLoader', NULL, 'Critical', N'o', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    SET @PrevPass = @PassId;

    DELETE @F;
    INSERT @H VALUES (@kAgedOut,    'AgedOutUnresolved'),
                     (@kDisabled,   'LoaderDisabled'),
                     (@kRemoved,    'ConfigRemoved'),
                     (@kKeepOpen,   'KeepOpen'),
                     (@kSelfHealed, 'SelfHealed'),        -- not a DATA_VOLUME reason: must be ignored
                     (@kOverdueKO,  'KeepOpen');          -- stray: KeepOpen is DATA_VOLUME-only
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kRedetect AND ResolutionReason = 'NotDetected' AND ResolvedPassId = @PrevPass)
        THROW 50102, '2: scenario-1 finding, absent from a checked pass with no hint, must close NotDetected', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kNoHint AND ResolutionReason = 'NotDetected' AND ResolvedPassId = @PassId)
        THROW 50102, '2: DATA_VOLUME with no hint must close NotDetected', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kAgedOut AND ResolutionReason = 'AgedOutUnresolved' AND ResolvedPassId = @PassId)
        THROW 50102, '2: hint AgedOutUnresolved must be honoured', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kDisabled AND ResolutionReason = 'LoaderDisabled' AND ResolvedPassId = @PassId)
        THROW 50102, '2: hint LoaderDisabled must be honoured', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kRemoved AND ResolutionReason = 'ConfigRemoved' AND ResolvedPassId = @PassId)
        THROW 50102, '2: hint ConfigRemoved must be honoured', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kKeepOpen AND ResolvedAtUtc IS NULL
                     AND ResolvedPassId IS NULL AND ResolutionReason IS NULL
                     AND DetectionCount = 1 AND LastPassId = @PrevPass)
        THROW 50102, '2: hint KeepOpen must leave the DATA_VOLUME finding open and untouched', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kSelfHealed AND ResolutionReason = 'NotDetected')
        THROW 50102, '2: hint SelfHealed is not a DATA_VOLUME reason and must fall back to NotDetected', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kOverdueKO AND ResolutionReason = 'NotDetected' AND ResolvedPassId = @PassId)
        THROW 50102, '2: a stray KeepOpen on OVERDUE must be ignored (closes NotDetected)', 1;
    PRINT 'PASS 2 (DATA_VOLUME closure reasons, KeepOpen, invalid hints ignored)';

    ---------------------------------------------------------------------------
    -- 3. The kept-open finding later leaves the window -> AgedOutUnresolved.
    ---------------------------------------------------------------------------
    INSERT @H VALUES (@kKeepOpen, 'AgedOutUnresolved');
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kKeepOpen AND ResolutionReason = 'AgedOutUnresolved' AND ResolvedPassId = @PassId)
        THROW 50103, '3: a kept-open finding that ages out must close AgedOutUnresolved', 1;
    IF (SELECT COUNT(*) FROM core.WatchdogFinding WHERE FindingKey = @kKeepOpen) <> 1
        THROW 50103, '3: expected exactly one row for the kept-open key', 1;
    PRINT 'PASS 3 (KeepOpen later ages out)';

    ---------------------------------------------------------------------------
    -- 4. DATA_VOLUME degraded (listed in BOTH checked and degraded, as the
    --    conservative case) -> open finding untouched even with a hint.
    ---------------------------------------------------------------------------
    INSERT @F VALUES (@kDegraded, 'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    SET @PrevPass = @PassId;

    DELETE @F;
    INSERT @F VALUES (N'DEGRADED|VOLUME_QUERY', 'DEGRADED', NULL, NULL, 'Critical', N'row volume (x)', N'{"checkCode":"VOLUME_QUERY"}', 0);
    INSERT @H VALUES (@kDegraded, 'LoaderDisabled');
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Degraded', @All, 'DATA_VOLUME', NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kDegraded AND ResolvedAtUtc IS NULL
                     AND DetectionCount = 1 AND LastPassId = @PrevPass)
        THROW 50104, '4: DATA_VOLUME finding must be untouched while DATA_VOLUME is degraded, hint or not', 1;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogPass WHERE PassId = @PassId AND Status = 'Degraded' AND SignalsDegraded = 'DATA_VOLUME')
        THROW 50104, '4: pass must be Degraded with SignalsDegraded = DATA_VOLUME', 1;
    PRINT 'PASS 4 (degraded DATA_VOLUME resolves nothing)';

    -- Recovery: the check runs again and the day is re-judged in range -> the
    -- degraded finding and the DATA_VOLUME finding both close NotDetected.
    DELETE @F;
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kDegraded AND ResolutionReason = 'NotDetected' AND ResolvedPassId = @PassId)
        THROW 50104, '4: after recovery the DATA_VOLUME finding must close NotDetected', 1;
    PRINT 'PASS 4b (recovered check closes it)';

    ---------------------------------------------------------------------------
    -- 5. DATA_VOLUME absent from @SignalsChecked (volume check off / not run)
    --    -> open finding untouched even with a hint.
    ---------------------------------------------------------------------------
    INSERT @F VALUES (@kUnchecked, 'DATA_VOLUME', N'zzTestLoader', NULL, 'Warning', N'v', N'{}', 0);
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    SET @PrevPass = @PassId;

    DELETE @F;
    INSERT @H VALUES (@kUnchecked, 'LoaderDisabled');
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @NoVolume, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kUnchecked AND ResolvedAtUtc IS NULL
                     AND DetectionCount = 1 AND LastPassId = @PrevPass)
        THROW 50105, '5: DATA_VOLUME finding must be untouched when DATA_VOLUME is not checked', 1;
    PRINT 'PASS 5 (unchecked DATA_VOLUME resolves nothing)';

    ---------------------------------------------------------------------------
    -- 6. Master switch turned off with leftovers (A4): caller adds DATA_VOLUME
    --    to @SignalsChecked and hints LoaderDisabled -> closed LoaderDisabled.
    ---------------------------------------------------------------------------
    INSERT @H VALUES (@kUnchecked, 'LoaderDisabled');
    EXEC core.usp_RecordWatchdogPass @Now, N'zzTest', 'Complete', @All, NULL, NULL, @F, @H, @PassId OUTPUT, @Ret OUTPUT;
    DELETE @H;

    IF NOT EXISTS (SELECT 1 FROM core.WatchdogFinding WHERE FindingKey = @kUnchecked AND ResolutionReason = 'LoaderDisabled' AND ResolvedPassId = @PassId)
        THROW 50106, '6: master-switch-off leftover must close LoaderDisabled', 1;
    PRINT 'PASS 6 (master switch off closes leftovers LoaderDisabled)';

    -- No zzTest DATA_VOLUME key may have more than one open row, and nothing
    -- stored a hint-only value as a ResolutionReason.
    IF EXISTS (SELECT FindingKey FROM core.WatchdogFinding
               WHERE FindingKey LIKE N'DATA\_VOLUME|zzTestLoader|%' ESCAPE N'\' AND ResolvedAtUtc IS NULL
               GROUP BY FindingKey HAVING COUNT(*) > 1)
        THROW 50120, 'invariant: more than one open row for a zzTest DATA_VOLUME key', 1;
    IF EXISTS (SELECT 1 FROM core.WatchdogFinding
               WHERE FindingKey LIKE N'DATA\_VOLUME|zzTestLoader|%' ESCAPE N'\' AND ResolutionReason = 'KeepOpen')
        THROW 50120, 'invariant: KeepOpen stored as a ResolutionReason', 1;
    PRINT 'PASS invariants';

    IF @Ret IS NOT NULL PRINT CONCAT('NOTE retention reported: ', @Ret);

    ROLLBACK TRANSACTION;
    PRINT 'ALL WATCHDOG DATA_VOLUME PROC TESTS PASSED (rolled back)';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
