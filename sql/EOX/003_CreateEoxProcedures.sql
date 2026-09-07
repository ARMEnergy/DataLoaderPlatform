-- =============================================================================
-- 003_CreateEoxProcedures.sql
-- Database : EOX
-- Schema   : arm
--
--   arm.usp_UpsertFileLog        audit hub upsert, returns FileLogId
--   arm.usp_BulkMergeCrudeOil    one file's rows -> arm.CrudeOil
--   arm.usp_BulkMergeNaturalGas  one file's rows -> arm.NaturalGas
--   arm.usp_BulkMergeNGL         one file's rows -> arm.NGL
--   arm.usp_ValidateLoad         post-load observational anomaly report
--
-- ==== THE ORDERING GUARD, ONCE, FOR ALL THREE MERGES ====
-- Work units run concurrently and the loader is re-runnable, so rows can reach a
-- table in any order. Every UPDATE branch is guarded by
--
--     src.FileName >= ISNULL(tgt.FileName, '')
--
-- and every batch de-dup orders FileName DESC, so the newest file wins and the
-- result does not depend on arrival order.
--
-- Why comparing FILE NAMES is a valid chronological comparison here: within one
-- feed every name is <fixed prefix><zero-padded yyyyMMdd><fixed suffix>
-- (EOD_CSV_NG_20260904_1430.csv), so the names sort lexicographically in exactly
-- publication-date order. The loader only ever addresses files by that exact
-- shape, so a name of any other shape cannot reach these procs.
--
-- The comparison is >= and not >, so a same-name republish (EOX overwriting a
-- file in place, which is the revision mechanism for this feed) still applies.
-- ISNULL(...,'') makes a pre-existing row with a NULL FileName always lose,
-- rather than leaving it permanently un-updatable on a three-valued comparison.
--
-- In practice cross-file collisions cannot happen at all: CurveDate leads every
-- primary key and always equals the date in the file name, so two different files
-- write disjoint key sets. The guard exists so that assumption failing degrades
-- to "newest file wins" instead of to a race. usp_ValidateLoad reports any row
-- where the assumption is violated.
--
-- ==== NO MERGE DELETES BY ABSENCE ====
-- Each work unit carries ONE day's snapshot for ONE feed, so a
-- "NOT MATCHED BY SOURCE" branch would wipe every other day in the table. No proc
-- here has one. Rows are only ever inserted or updated.
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog -- one hub row per source FILE, every outcome
-- (Success / NotAvailable / Failed). FileName is the natural key, so re-checking
-- a file updates its row. Returns the FileLogId, which the loader logs but does
-- not stamp onto fact rows -- the fact tables carry FileName instead.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @FileName              VARCHAR(128),
    @FeedId                VARCHAR(20),
    @CurveDate             DATE          = NULL,
    @StatusLabel           VARCHAR(20),
    @RemoteLastModifiedUtc DATETIME2(3)  = NULL,
    @SizeBytes             BIGINT        = NULL,
    @RowCount              INT           = 0,
    @ErrorMessage          NVARCHAR(400) = NULL,
    @RequestPath           NVARCHAR(400)
AS
BEGIN
    SET NOCOUNT ON;

    IF @FileName IS NULL OR @FeedId IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @FileName, @FeedId, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- Status is a fixed catalog seeded in 001 -- a miss is a bug, so fail loudly
    -- rather than feed a NULL into the NOT NULL FK column.
    DECLARE @StatusId INT = (SELECT Id FROM arm.Status WHERE [Name] = @StatusLabel);
    IF @StatusId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Status ''%s''.', 16, 1, @StatusLabel);
        RETURN;
    END

    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE arm.FileLog AS tgt
    USING (SELECT @FileName AS FileName) AS src
       ON tgt.FileName = src.FileName
    WHEN MATCHED THEN UPDATE SET
        FeedId                = @FeedId,
        CurveDate             = @CurveDate,
        StatusId              = @StatusId,
        RemoteLastModifiedUtc = @RemoteLastModifiedUtc,
        SizeBytes             = @SizeBytes,
        [RowCount]            = @RowCount,
        ErrorMessage          = @ErrorMessage,
        RequestPath           = @RequestPath,
        LastCheckedUtc        = SYSUTCDATETIME(),
        ModifiedAtUtc         = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileName, FeedId, CurveDate, StatusId, RemoteLastModifiedUtc, SizeBytes,
                [RowCount], ErrorMessage, RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.FileName, @FeedId, @CurveDate, @StatusId, @RemoteLastModifiedUtc, @SizeBytes,
                @RowCount, @ErrorMessage, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeCrudeOil -- PK (CurveDate, LocationCode, TimeKey)
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCrudeOil
    @Records arm.CrudeOilTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Affected INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH Src AS
        (
            SELECT CurveDate, LocationCode, TimeKey, Line, Code, ContractTerm, ContractName,
                   ContractBegin, ContractEnd, Location, Mid, Bid, Ask, FileName
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY CurveDate, LocationCode, TimeKey
                           ORDER BY FileName DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.CrudeOil AS tgt
        USING Src AS src
           ON  tgt.CurveDate    = src.CurveDate
           AND tgt.LocationCode = src.LocationCode
           AND tgt.TimeKey      = src.TimeKey
        WHEN MATCHED AND src.FileName >= ISNULL(tgt.FileName, '') THEN UPDATE SET
            Line          = src.Line,
            Code          = src.Code,
            ContractTerm  = src.ContractTerm,
            ContractName  = src.ContractName,
            ContractBegin = src.ContractBegin,
            ContractEnd   = src.ContractEnd,
            Location      = src.Location,
            Mid           = src.Mid,
            Bid           = src.Bid,
            Ask           = src.Ask,
            FileName      = src.FileName,
            ModifiedAtUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (CurveDate, LocationCode, TimeKey, Line, Code, ContractTerm, ContractName,
                    ContractBegin, ContractEnd, Location, Mid, Bid, Ask, FileName, ModifiedAtUtc)
            VALUES (src.CurveDate, src.LocationCode, src.TimeKey, src.Line, src.Code,
                    src.ContractTerm, src.ContractName, src.ContractBegin, src.ContractEnd,
                    src.Location, src.Mid, src.Bid, src.Ask, src.FileName, SYSUTCDATETIME());

        SET @Affected = @@ROWCOUNT;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT @Affected AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeNaturalGas -- PK (CurveDate, MarketCode, TimeKey)
-- FP is NULL for files published before EOX added the column (~2016).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeNaturalGas
    @Records arm.NaturalGasTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Affected INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH Src AS
        (
            SELECT CurveDate, MarketCode, TimeKey, Line, Code, Region, Market, ContractName,
                   ContractTerm, ContractBegin, ContractEnd, Mid, Bid, Ask, FP, FileName
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY CurveDate, MarketCode, TimeKey
                           ORDER BY FileName DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.NaturalGas AS tgt
        USING Src AS src
           ON  tgt.CurveDate  = src.CurveDate
           AND tgt.MarketCode = src.MarketCode
           AND tgt.TimeKey    = src.TimeKey
        WHEN MATCHED AND src.FileName >= ISNULL(tgt.FileName, '') THEN UPDATE SET
            Line          = src.Line,
            Code          = src.Code,
            Region        = src.Region,
            Market        = src.Market,
            ContractName  = src.ContractName,
            ContractTerm  = src.ContractTerm,
            ContractBegin = src.ContractBegin,
            ContractEnd   = src.ContractEnd,
            Mid           = src.Mid,
            Bid           = src.Bid,
            Ask           = src.Ask,
            FP            = src.FP,
            FileName      = src.FileName,
            ModifiedAtUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (CurveDate, MarketCode, TimeKey, Line, Code, Region, Market, ContractName,
                    ContractTerm, ContractBegin, ContractEnd, Mid, Bid, Ask, FP, FileName, ModifiedAtUtc)
            VALUES (src.CurveDate, src.MarketCode, src.TimeKey, src.Line, src.Code, src.Region,
                    src.Market, src.ContractName, src.ContractTerm, src.ContractBegin, src.ContractEnd,
                    src.Mid, src.Bid, src.Ask, src.FP, src.FileName, SYSUTCDATETIME());

        SET @Affected = @@ROWCOUNT;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT @Affected AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeNGL -- PK (CurveDate, LocationCode, TimeKey)
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeNGL
    @Records arm.NGLTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Affected INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH Src AS
        (
            SELECT CurveDate, LocationCode, TimeKey, Line, Code, ContractTerm, ContractName,
                   ContractBegin, ContractEnd, Location, Mid, Bid, Ask, FileName
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY CurveDate, LocationCode, TimeKey
                           ORDER BY FileName DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.NGL AS tgt
        USING Src AS src
           ON  tgt.CurveDate    = src.CurveDate
           AND tgt.LocationCode = src.LocationCode
           AND tgt.TimeKey      = src.TimeKey
        WHEN MATCHED AND src.FileName >= ISNULL(tgt.FileName, '') THEN UPDATE SET
            Line          = src.Line,
            Code          = src.Code,
            ContractTerm  = src.ContractTerm,
            ContractName  = src.ContractName,
            ContractBegin = src.ContractBegin,
            ContractEnd   = src.ContractEnd,
            Location      = src.Location,
            Mid           = src.Mid,
            Bid           = src.Bid,
            Ask           = src.Ask,
            FileName      = src.FileName,
            ModifiedAtUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (CurveDate, LocationCode, TimeKey, Line, Code, ContractTerm, ContractName,
                    ContractBegin, ContractEnd, Location, Mid, Bid, Ask, FileName, ModifiedAtUtc)
            VALUES (src.CurveDate, src.LocationCode, src.TimeKey, src.Line, src.Code,
                    src.ContractTerm, src.ContractName, src.ContractBegin, src.ContractEnd,
                    src.Location, src.Mid, src.Bid, src.Ask, src.FileName, SYSUTCDATETIME());

        SET @Affected = @@ROWCOUNT;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT @Affected AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad -- post-load OBSERVATIONAL anomaly report. ONE result set
-- with the repo's uniform shape so EoxLoadValidator can log it generically.
-- Observational only (no side effects); the caller decides what is a hard
-- failure. @CurveDate scopes the day-level checks; pass NULL for the whole table.
--
--   CheckName      what was measured
--   Scope          the grouping key the count applies to
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
--
-- A row whose ExpectedCount is non-NULL and differs from ActualCount is logged as
-- an anomaly; the rest are counters. Zero rows for a weekend or holiday CurveDate
-- is NORMAL for this feed and is deliberately reported as a counter, not an
-- anomaly.
--
-- The FileName-date extraction below assumes the shipped naming shape
-- <prefix><yyyyMMdd>_1430.csv, i.e. an 8-digit date sitting 16 characters from
-- the end. If EoxSettings.FileNameTimeToken is ever changed away from '1430',
-- the CurveDateDoesNotMatchFileName checks report every row as unparseable
-- rather than silently passing -- that is why the unparseable count is its own
-- check rather than being folded into the mismatch count.
--
-- NOTE ON THE PER-FEED CHECKS (4, 5, 6): they GROUP BY feed over the rows that
-- violate the rule, so a healthy table produces NO ROW for them at all. Absence
-- is the healthy signal; a row appearing at all is the anomaly. They are written
-- this way so the report names the offending feed rather than a bare total.
--
-- COST: this proc scans all three fact tables. It is observational and runs once
-- per loader run, but on a full backfill (thousands of curve dates, ~59k rows
-- each) it is not cheap. Every check is a single pass -- there are no correlated
-- subqueries over the fact tables -- so the cost is linear, not quadratic.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @CurveDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH FileDates AS
    (
        SELECT 'CrudeOil' AS Feed, c.CurveDate, c.FileName,
               TRY_CONVERT(DATE, SUBSTRING(c.FileName, LEN(c.FileName) - 16, 8), 112) AS NameDate
        FROM arm.CrudeOil AS c
        UNION ALL
        SELECT 'NaturalGas', n.CurveDate, n.FileName,
               TRY_CONVERT(DATE, SUBSTRING(n.FileName, LEN(n.FileName) - 16, 8), 112)
        FROM arm.NaturalGas AS n
        UNION ALL
        SELECT 'NGL', g.CurveDate, g.FileName,
               TRY_CONVERT(DATE, SUBSTRING(g.FileName, LEN(g.FileName) - 16, 8), 112)
        FROM arm.NGL AS g
    ),
    -- One pass over the fact rows, grouped by file. Check 11 joins to this rather
    -- than running a COUNT(*) subquery per FileLog row, which would rescan all
    -- three tables once for every file in the log.
    FileRowCounts AS
    (
        SELECT f.FileName, COUNT(1) AS FactRows
        FROM FileDates AS f
        GROUP BY f.FileName
    ),
    Report AS
    (
        -- 1..3) Row counts per table (expect > 0 after any load).
        SELECT
            CAST('RowCount' AS VARCHAR(48))          AS CheckName,
            CAST('arm.CrudeOil' AS NVARCHAR(200))    AS Scope,
            CAST(NULL AS INT)                        AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)                 AS ActualCount,
            CAST(NULL AS NVARCHAR(400))              AS Detail
        FROM arm.CrudeOil

        UNION ALL
        SELECT CAST('RowCount' AS VARCHAR(48)), CAST('arm.NaturalGas' AS NVARCHAR(200)),
               CAST(NULL AS INT), CAST(COUNT(1) AS BIGINT), CAST(NULL AS NVARCHAR(400))
        FROM arm.NaturalGas

        UNION ALL
        SELECT CAST('RowCount' AS VARCHAR(48)), CAST('arm.NGL' AS NVARCHAR(200)),
               CAST(NULL AS INT), CAST(COUNT(1) AS BIGINT), CAST(NULL AS NVARCHAR(400))
        FROM arm.NGL

        UNION ALL
        -- 4) The assumption the whole no-ordering-column design rests on: a row's
        --    CurveDate must equal the date embedded in its file name. Expect 0.
        SELECT
            CAST('CurveDateDoesNotMatchFileName' AS VARCHAR(48)),
            CAST(f.Feed AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('CurveDate <> yyyyMMdd in FileName -- two files can then collide on one PK' AS NVARCHAR(400))
        FROM FileDates AS f
        WHERE f.NameDate IS NOT NULL AND f.NameDate <> f.CurveDate
        GROUP BY f.Feed

        UNION ALL
        -- 5) Rows whose file name does not carry a parseable date at the expected
        --    offset. Expect 0; a non-zero count invalidates check 4 above.
        SELECT
            CAST('FileNameDateUnparseable' AS VARCHAR(48)),
            CAST(f.Feed AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('FileName has no yyyyMMdd 16 chars from its end' AS NVARCHAR(400))
        FROM FileDates AS f
        WHERE f.NameDate IS NULL
        GROUP BY f.Feed

        UNION ALL
        -- 6) Rows with no FileName at all. Expect 0 -- the TVP forbids it, so a hit
        --    means something wrote these tables outside the loader.
        SELECT
            CAST('RowsWithoutFileName' AS VARCHAR(48)),
            CAST(f.Feed AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('FileName IS NULL -- the merge ordering guard cannot order these' AS NVARCHAR(400))
        FROM FileDates AS f
        WHERE f.FileName IS NULL
        GROUP BY f.Feed

        UNION ALL
        -- 7) Bid/Ask ordering sanity across all three tables. Informational, NOT an
        --    expectation: these are broker curves and a crossed market (Bid > Ask)
        --    does occur in thin back months, so this is a counter to watch, not a
        --    rule to enforce.
        SELECT
            CAST('CrossedMarkets' AS VARCHAR(48)),
            CAST('all tables' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(SUM(CASE WHEN q.Bid IS NOT NULL AND q.Ask IS NOT NULL AND q.Bid > q.Ask THEN 1 ELSE 0 END) AS BIGINT),
            CAST('Bid > Ask -- informational, thin back months really do cross' AS NVARCHAR(400))
        FROM (
            SELECT Bid, Ask FROM arm.CrudeOil
            UNION ALL SELECT Bid, Ask FROM arm.NaturalGas
            UNION ALL SELECT Bid, Ask FROM arm.NGL
        ) AS q

        UNION ALL
        -- 8) Rows carrying no price at all. Expect 0 -- every sampled file had all
        --    three price cells populated on every row.
        SELECT
            CAST('RowsWithNoPriceAtAll' AS VARCHAR(48)),
            CAST('all tables' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Mid, Bid and Ask all NULL' AS NVARCHAR(400))
        FROM (
            SELECT Mid, Bid, Ask FROM arm.CrudeOil
            UNION ALL SELECT Mid, Bid, Ask FROM arm.NaturalGas
            UNION ALL SELECT Mid, Bid, Ask FROM arm.NGL
        ) AS p
        WHERE p.Mid IS NULL AND p.Bid IS NULL AND p.Ask IS NULL

        UNION ALL
        -- 9) FileLog outcomes that are Failed. Expect 0. NotAvailable is EXCLUDED
        --    on purpose -- a weekend or holiday legitimately has no file, and
        --    counting those as anomalies would cry wolf on every Monday run.
        SELECT
            CAST('FileLogFailed' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('arm.FileLog rows with Status = Failed' AS NVARCHAR(400))
        FROM arm.FileLog AS fl
        JOIN arm.Status AS s ON s.Id = fl.StatusId
        WHERE s.[Name] = 'Failed'

        UNION ALL
        -- 10) Counter: dates the drop had no file for. Normal on weekends/holidays.
        SELECT
            CAST('FileLogNotAvailable' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('dates with no file on the drop -- weekends and holidays are normal' AS NVARCHAR(400))
        FROM arm.FileLog AS fl
        JOIN arm.Status AS s ON s.Id = fl.StatusId
        WHERE s.[Name] = 'NotAvailable'

        UNION ALL
        -- 11) FileLog [RowCount] vs rows actually stamped with that file name.
        --     Expect 0 mismatches among Success files.
        --
        --     This is the check that catches a file whose rows were PARSED but never
        --     MERGED: the reader writes its Success hub row after parsing, before the
        --     sink runs, so a merge that fails afterwards leaves [RowCount] > 0 with
        --     no fact rows to match. core.LoadLog records that unit as failed too,
        --     but this makes the data-side gap visible from the database alone.
        SELECT
            CAST('FileLogRowCountMismatch' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Success files whose [RowCount] <> fact rows carrying that FileName' AS NVARCHAR(400))
        FROM arm.FileLog AS fl
        JOIN arm.Status AS s ON s.Id = fl.StatusId
        LEFT JOIN FileRowCounts AS frc ON frc.FileName = fl.FileName
        WHERE s.[Name] = 'Success'
          AND fl.[RowCount] <> ISNULL(frc.FactRows, 0)

        UNION ALL
        -- 12..14) Row counts for the requested curve date. May legitimately be 0 on
        --     a weekend or holiday, so these are counters and never anomalies.
        SELECT
            CAST('RowCountForCurveDate' AS VARCHAR(48)),
            CAST(CONCAT('arm.CrudeOil / ', ISNULL(CONVERT(VARCHAR(10), @CurveDate, 23), 'ALL')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.CrudeOil AS c
        WHERE @CurveDate IS NULL OR c.CurveDate = @CurveDate

        UNION ALL
        SELECT
            CAST('RowCountForCurveDate' AS VARCHAR(48)),
            CAST(CONCAT('arm.NaturalGas / ', ISNULL(CONVERT(VARCHAR(10), @CurveDate, 23), 'ALL')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.NaturalGas AS n
        WHERE @CurveDate IS NULL OR n.CurveDate = @CurveDate

        UNION ALL
        SELECT
            CAST('RowCountForCurveDate' AS VARCHAR(48)),
            CAST(CONCAT('arm.NGL / ', ISNULL(CONVERT(VARCHAR(10), @CurveDate, 23), 'ALL')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.NGL AS g
        WHERE @CurveDate IS NULL OR g.CurveDate = @CurveDate
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report;
END
GO
