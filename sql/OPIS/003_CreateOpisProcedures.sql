-- =============================================================================
-- 003_CreateOpisProcedures.sql
-- Database : OPIS
-- Schema   : arm
--
--   arm.usp_UpsertFileLog      audit hub upsert, returns FileLogId
--   arm.usp_BulkMergeLPReport  fans one TVP into arm.LPReportHistory + arm.LPReport
--   arm.usp_ValidateLoad       post-load observational anomaly report
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog — one hub row per source FILE, every outcome
-- (Success / NotAvailable / Failed). FileName is the natural key, so a
-- re-download updates the existing row. Returns the FileLogId that the caller
-- stamps onto every fact row from that file.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @FileName              VARCHAR(200),
    @SourceFileDate        DATE          = NULL,
    @StatusLabel           VARCHAR(20),
    @RemoteLastModifiedUtc DATETIME2(3)  = NULL,
    @SizeBytes             BIGINT        = NULL,
    @RowCount              INT           = 0,
    @ErrorMessage          NVARCHAR(400) = NULL,
    @RequestPath           NVARCHAR(400)
AS
BEGIN
    SET NOCOUNT ON;

    IF @FileName IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @FileName, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- Status is a fixed catalog seeded in 001 — a miss is a bug, so fail loudly
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
        SourceFileDate        = @SourceFileDate,
        StatusId              = @StatusId,
        RemoteLastModifiedUtc = @RemoteLastModifiedUtc,
        SizeBytes             = @SizeBytes,
        [RowCount]            = @RowCount,
        ErrorMessage          = @ErrorMessage,
        RequestPath           = @RequestPath,
        LastCheckedUtc        = SYSUTCDATETIME(),
        ModifiedAtUtc         = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileName, SourceFileDate, StatusId, RemoteLastModifiedUtc, SizeBytes,
                [RowCount], ErrorMessage, RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.FileName, @SourceFileDate, @StatusId, @RemoteLastModifiedUtc, @SizeBytes,
                @RowCount, @ErrorMessage, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeLPReport — the single write path for the LP feed.
--
-- Takes one file's parsed rows and merges them into BOTH fact tables inside one
-- transaction, so the current table and the history table can never disagree:
--
--   arm.LPReportHistory  keyed (Mkt_Prod, [Date], Timing, Price)  — keeps the
--        'I' quote AND any later 'U' revision of it: the price-change history.
--   arm.LPReport         keyed (Mkt_Prod, [Date], Timing)         — one current
--        row; a 'U' revision supersedes the 'I' it revises.
--
-- ORDERING GUARD. Work units (files) run concurrently and the loader is
-- re-runnable, so rows can arrive in any order. Every UPDATE branch is guarded
-- by  src.SourceFileDate >= tgt.SourceFileDate,  so an older file can never
-- overwrite a newer one and the outcome is independent of arrival order.
--
-- BATCH DEDUP. Standard repo posture: ROW_NUMBER, last wins. A single OPIS file
-- contains no duplicate keys (verified over the live month), but the guard
-- costs nothing and keeps a merged/multi-file batch safe. The current-table
-- dedup breaks a same-file tie on Price DESC so 'U' beats 'I' deterministically.
--
-- Returns SELECT ... AS RecordsProcessed — the distinct rows merged into
-- arm.LPReport, which SqlSinkBase surfaces to core.LoadLog.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeLPReport
    @Records arm.LPReportTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @CurrentRows INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- ---- 1) HISTORY: one row per (Mkt_Prod, Date, Timing, Price) --------
        ;WITH SrcHistory AS
        (
            SELECT FileLogId, Mkt_Prod, [Date], Timing, Price,
                   [Low], [High], [Avg], Country, [Unit], Freq, SourceFileDate
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY Mkt_Prod, [Date], Timing, Price
                           ORDER BY SourceFileDate DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.LPReportHistory AS tgt
        USING SrcHistory AS src
           ON  tgt.Mkt_Prod = src.Mkt_Prod
           AND tgt.[Date]   = src.[Date]
           AND tgt.Timing   = src.Timing
           AND tgt.Price    = src.Price
        WHEN MATCHED AND src.SourceFileDate >= tgt.SourceFileDate THEN UPDATE SET
            [Low]          = src.[Low],
            [High]         = src.[High],
            [Avg]          = src.[Avg],
            Country        = src.Country,
            [Unit]         = src.[Unit],
            Freq           = src.Freq,
            SourceFileDate = src.SourceFileDate,
            FileLogId      = src.FileLogId,
            ModifiedAtUtc  = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (Mkt_Prod, [Date], Timing, Price, [Low], [High], [Avg],
                    Country, [Unit], Freq, SourceFileDate, FileLogId, ModifiedAtUtc)
            VALUES (src.Mkt_Prod, src.[Date], src.Timing, src.Price, src.[Low], src.[High], src.[Avg],
                    src.Country, src.[Unit], src.Freq, src.SourceFileDate, src.FileLogId, SYSUTCDATETIME());

        -- ---- 2) CURRENT: one row per (Mkt_Prod, Date, Timing) ---------------
        ;WITH SrcCurrent AS
        (
            SELECT FileLogId, Mkt_Prod, [Date], Timing, Price,
                   [Low], [High], [Avg], Country, [Unit], Freq, SourceFileDate
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY Mkt_Prod, [Date], Timing
                           ORDER BY SourceFileDate DESC, Price DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.LPReport AS tgt
        USING SrcCurrent AS src
           ON  tgt.Mkt_Prod = src.Mkt_Prod
           AND tgt.[Date]   = src.[Date]
           AND tgt.Timing   = src.Timing
        WHEN MATCHED AND src.SourceFileDate >= tgt.SourceFileDate THEN UPDATE SET
            Price          = src.Price,
            [Low]          = src.[Low],
            [High]         = src.[High],
            [Avg]          = src.[Avg],
            Country        = src.Country,
            [Unit]         = src.[Unit],
            Freq           = src.Freq,
            SourceFileDate = src.SourceFileDate,
            FileLogId      = src.FileLogId,
            ModifiedAtUtc  = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (Mkt_Prod, [Date], Timing, Price, [Low], [High], [Avg],
                    Country, [Unit], Freq, SourceFileDate, FileLogId, ModifiedAtUtc)
            VALUES (src.Mkt_Prod, src.[Date], src.Timing, src.Price, src.[Low], src.[High], src.[Avg],
                    src.Country, src.[Unit], src.Freq, src.SourceFileDate, src.FileLogId, SYSUTCDATETIME());

        SET @CurrentRows = @@ROWCOUNT;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT @CurrentRows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad — post-load OBSERVATIONAL anomaly report. ONE result set
-- with the repo's uniform shape so OpisLoadValidator can log it generically.
-- Observational only (no side effects); the caller decides what is a hard
-- failure. @ReportDate scopes the day-level checks; pass NULL to check the
-- whole table.
--   CheckName      what was measured
--   Scope          the grouping key the count applies to
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @ReportDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH Report AS
    (
        -- 1) Current-table row count (expect > 0 after any load).
        SELECT
            CAST('LPReportRowCount' AS VARCHAR(48))  AS CheckName,
            CAST(NULL AS NVARCHAR(200))              AS Scope,
            CAST(NULL AS INT)                        AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)                 AS ActualCount,
            CAST(NULL AS NVARCHAR(400))              AS Detail
        FROM arm.LPReport

        UNION ALL
        -- 2) History row count — must be >= the current count by construction.
        SELECT
            CAST('LPReportHistoryRowCount' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('>= LPReportRowCount by construction' AS NVARCHAR(400))
        FROM arm.LPReportHistory

        UNION ALL
        -- 3) Every current row must have a matching history row (expect 0 gaps).
        SELECT
            CAST('CurrentRowsMissingFromHistory' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('arm.LPReport rows with no (Mkt_Prod,Date,Timing,Price) in history' AS NVARCHAR(400))
        FROM arm.LPReport AS c
        WHERE NOT EXISTS (
            SELECT 1 FROM arm.LPReportHistory AS h
            WHERE h.Mkt_Prod = c.Mkt_Prod AND h.[Date] = c.[Date]
              AND h.Timing = c.Timing AND h.Price = c.Price)

        UNION ALL
        -- 4) Revisions actually captured: keys whose history holds >1 price code.
        --    Informational — this is the feature the history table exists for.
        SELECT
            CAST('RevisedKeys' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('(Mkt_Prod,Date,Timing) with more than one Price code' AS NVARCHAR(400))
        FROM (
            SELECT Mkt_Prod, [Date], Timing
            FROM arm.LPReportHistory
            GROUP BY Mkt_Prod, [Date], Timing
            HAVING COUNT(DISTINCT Price) > 1
        ) AS r

        UNION ALL
        -- 5) Low/High/Avg ordering sanity (expect 0 violations).
        SELECT
            CAST('PriceBandOutOfOrder' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN c.[Low] IS NOT NULL AND c.[High] IS NOT NULL AND c.[Low] > c.[High] THEN 1
                          WHEN c.[Avg] IS NOT NULL AND c.[Low]  IS NOT NULL AND c.[Avg] < c.[Low]  THEN 1
                          WHEN c.[Avg] IS NOT NULL AND c.[High] IS NOT NULL AND c.[Avg] > c.[High] THEN 1
                          ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.LPReport AS c

        UNION ALL
        -- 6) Rows with no price at all (expect 0 — Avg was always present live).
        SELECT
            CAST('RowsWithNoPriceAtAll' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Low, High and Avg all NULL' AS NVARCHAR(400))
        FROM arm.LPReport AS c
        WHERE c.[Low] IS NULL AND c.[High] IS NULL AND c.[Avg] IS NULL

        UNION ALL
        -- 7) Untrimmed key values (expect 0 — the source space-pads Mkt_Prod).
        SELECT
            CAST('UntrimmedMktProd' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Mkt_Prod with leading/trailing whitespace' AS NVARCHAR(400))
        FROM arm.LPReport AS c
        WHERE c.Mkt_Prod <> LTRIM(RTRIM(c.Mkt_Prod))

        UNION ALL
        -- 8) FileLog outcomes not Success (warn — a failed file leaves a gap).
        SELECT
            CAST('FileLogNotSuccess' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('arm.FileLog rows whose Status <> Success' AS NVARCHAR(400))
        FROM arm.FileLog AS f
        JOIN arm.Status AS s ON s.Id = f.StatusId
        WHERE s.[Name] <> 'Success'

        UNION ALL
        -- 9) FileLog RowCount vs. rows actually present in history, per file.
        SELECT
            CAST('FileLogRowCountMismatch' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Success files whose [RowCount] <> history rows stamped with that FileLogId' AS NVARCHAR(400))
        FROM arm.FileLog AS f
        JOIN arm.Status AS s ON s.Id = f.StatusId
        WHERE s.[Name] = 'Success'
          AND f.[RowCount] <> (SELECT COUNT(1) FROM arm.LPReportHistory AS h WHERE h.FileLogId = f.Id)

        UNION ALL
        -- 10) Row count for the requested report date (may legitimately be 0 on
        --     a weekend/holiday — warn, don't fail). NULL @ReportDate -> whole table.
        SELECT
            CAST('LPReportRowCountForDate' AS VARCHAR(48)),
            CAST(CONCAT('Date=', ISNULL(CONVERT(VARCHAR(10), @ReportDate, 23), 'ALL')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.LPReport AS c
        WHERE @ReportDate IS NULL OR c.[Date] = @ReportDate
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report;
END
GO
