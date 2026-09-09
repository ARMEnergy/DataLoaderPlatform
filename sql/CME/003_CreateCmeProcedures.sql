-- =============================================================================
-- 003_CreateCmeProcedures.sql
-- Database : CMEGroup
-- Schema   : arm
--
--   arm.usp_UpsertFileLog            audit hub upsert, returns FileLogId
--   arm.usp_BulkMergeStlbasicOption  one bulletin's option rows -> arm.STLBASIC_Option
--   arm.usp_BulkMergeStlbasicFuture  one bulletin's future rows -> arm.STLBASIC_Future
--   arm.usp_ValidateLoad             post-load observational anomaly report
--
-- ==== WHY THERE IS NO CROSS-FILE ORDERING GUARD ====
-- The OPIS/Argus/EOX merges guard every UPDATE with
-- "src.FileName >= tgt.FileName" because those feeds can deliver overlapping
-- rows in several files. Here they cannot: ExchangeCode, ProductCode AND
-- TradeDate all sit in BOTH primary keys, and one bulletin carries exactly one
-- (exchange, product, trade date). So two different files write DISJOINT key
-- sets and can never contend for the same row -- there is nothing to order. The
-- supplied DDL also has no provenance column to order BY, so inventing one would
-- be a schema change for a race that cannot happen.
--
-- What CAN repeat is the SAME file: CME republishes a bulletin in place when
-- clearing revises it (the header even distinguishes FINAL PRE-CLEARING from
-- FINAL POST-CLEARING). That is handled by re-running the merge, which updates
-- every matched row -- and it is why the UPDATE branch is unconditional rather
-- than guarded.
--
-- ==== BATCH DE-DUPLICATION ====
-- Each merge de-dups its input by primary key with
-- ROW_NUMBER() ... ORDER BY RowOrdinal DESC, so the LAST occurrence in the
-- bulletin wins. MERGE raises "cannot UPDATE/INSERT the same row more than once"
-- if the source has duplicate keys, so this is required for safety even though
-- live data has none: 701,341 option rows and 47,192 future rows across 16
-- bulletins produced ZERO duplicate keys.
--
-- ==== NO MERGE DELETES BY ABSENCE ====
-- Each work unit carries ONE bulletin -- one exchange, one trade date -- so a
-- "NOT MATCHED BY SOURCE" branch would wipe every other exchange and date in the
-- table. Neither proc has one. Rows are only ever inserted or updated.
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog -- one hub row per source bulletin, every outcome
-- (Success / Failed). FileName is the natural key, so re-checking a file updates
-- its row. Returns the FileLogId, which the loader logs.
--
-- The parse counters are parameters rather than derived, because they describe
-- what the PARSER did -- including the rows it deliberately dropped, which no
-- query against the fact tables could reconstruct.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @FileName              VARCHAR(128),
    @FeedId                VARCHAR(50),
    @ExchangeCode          VARCHAR(50)   = NULL,
    @ProductCode           VARCHAR(50)   = NULL,
    @TradeDate             DATE          = NULL,
    @StatusLabel           VARCHAR(20),
    @RemoteLastModifiedUtc DATETIME2(3)  = NULL,
    @SizeBytes             BIGINT        = NULL,
    @RowCount              INT           = 0,
    @OptionRowCount        INT           = 0,
    @FutureRowCount        INT           = 0,
    @DayLabelRowsSkipped   INT           = 0,
    @UnclassifiedLines     INT           = 0,
    @ReportHeader          VARCHAR(200)  = NULL,
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
        ExchangeCode          = @ExchangeCode,
        ProductCode           = @ProductCode,
        TradeDate             = @TradeDate,
        StatusId              = @StatusId,
        RemoteLastModifiedUtc = @RemoteLastModifiedUtc,
        SizeBytes             = @SizeBytes,
        [RowCount]            = @RowCount,
        OptionRowCount        = @OptionRowCount,
        FutureRowCount        = @FutureRowCount,
        DayLabelRowsSkipped   = @DayLabelRowsSkipped,
        UnclassifiedLines     = @UnclassifiedLines,
        ReportHeader          = @ReportHeader,
        ErrorMessage          = @ErrorMessage,
        RequestPath           = @RequestPath,
        LastCheckedUtc        = SYSUTCDATETIME(),
        ModifiedAtUtc         = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileName, FeedId, ExchangeCode, ProductCode, TradeDate, StatusId,
                RemoteLastModifiedUtc, SizeBytes, [RowCount], OptionRowCount, FutureRowCount,
                DayLabelRowsSkipped, UnclassifiedLines, ReportHeader, ErrorMessage, RequestPath,
                LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.FileName, @FeedId, @ExchangeCode, @ProductCode, @TradeDate, @StatusId,
                @RemoteLastModifiedUtc, @SizeBytes, @RowCount, @OptionRowCount, @FutureRowCount,
                @DayLabelRowsSkipped, @UnclassifiedLines, @ReportHeader, @ErrorMessage, @RequestPath,
                SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeStlbasicOption
-- PK (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ProductDescription,
--     ContractYear, ContractMonth, PutCall, Strike)
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStlbasicOption
    @Records arm.STLBASIC_OptionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Affected INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH Src AS
        (
            SELECT ExchangeCode, ProductCode, TradeDate, ProductSymbol, ProductDescription,
                   ContractYear, ContractMonth, PutCall, Strike,
                   [Open], High, HighABIndicator, Low, LowABIndicator, [Last], LastABIndicator,
                   Settle, PctChange, EstVol, PriorSettle, PriorVol, PriorInt
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY ExchangeCode, ProductCode, TradeDate, ProductSymbol,
                                        ProductDescription, ContractYear, ContractMonth, PutCall, Strike
                           ORDER BY RowOrdinal DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.STLBASIC_Option AS tgt
        USING Src AS src
           ON  tgt.ExchangeCode       = src.ExchangeCode
           AND tgt.ProductCode        = src.ProductCode
           AND tgt.TradeDate          = src.TradeDate
           AND tgt.ProductSymbol      = src.ProductSymbol
           AND tgt.ProductDescription = src.ProductDescription
           AND tgt.ContractYear       = src.ContractYear
           AND tgt.ContractMonth      = src.ContractMonth
           AND tgt.PutCall            = src.PutCall
           AND tgt.Strike             = src.Strike
        WHEN MATCHED THEN UPDATE SET
            [Open]          = src.[Open],
            High            = src.High,
            HighABIndicator = src.HighABIndicator,
            Low             = src.Low,
            LowABIndicator  = src.LowABIndicator,
            [Last]          = src.[Last],
            LastABIndicator = src.LastABIndicator,
            Settle          = src.Settle,
            PctChange       = src.PctChange,
            EstVol          = src.EstVol,
            PriorSettle     = src.PriorSettle,
            PriorVol        = src.PriorVol,
            PriorInt        = src.PriorInt,
            ModifiedAtUtc   = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ProductDescription,
                    ContractYear, ContractMonth, PutCall, Strike,
                    [Open], High, HighABIndicator, Low, LowABIndicator, [Last], LastABIndicator,
                    Settle, PctChange, EstVol, PriorSettle, PriorVol, PriorInt, ModifiedAtUtc)
            VALUES (src.ExchangeCode, src.ProductCode, src.TradeDate, src.ProductSymbol,
                    src.ProductDescription, src.ContractYear, src.ContractMonth, src.PutCall, src.Strike,
                    src.[Open], src.High, src.HighABIndicator, src.Low, src.LowABIndicator,
                    src.[Last], src.LastABIndicator, src.Settle, src.PctChange, src.EstVol,
                    src.PriorSettle, src.PriorVol, src.PriorInt, SYSUTCDATETIME());

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
-- arm.usp_BulkMergeStlbasicFuture
-- PK (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ContractYear, ContractMonth)
--
-- ProductDescription is NOT part of this key (unlike the option table), so it is
-- an updatable attribute here -- a product renamed by CME updates in place
-- instead of creating a second row.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeStlbasicFuture
    @Records arm.STLBASIC_FutureTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Affected INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH Src AS
        (
            SELECT ExchangeCode, ProductCode, TradeDate, ProductSymbol, ContractYear, ContractMonth,
                   ProductDescription,
                   [Open], High, HighABIndicator, Low, LowABIndicator, [Last], LastABIndicator,
                   Settle, PctChange, EstVol, PriorSettle, PriorVol, PriorInt
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY ExchangeCode, ProductCode, TradeDate, ProductSymbol,
                                        ContractYear, ContractMonth
                           ORDER BY RowOrdinal DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE arm.STLBASIC_Future AS tgt
        USING Src AS src
           ON  tgt.ExchangeCode  = src.ExchangeCode
           AND tgt.ProductCode   = src.ProductCode
           AND tgt.TradeDate     = src.TradeDate
           AND tgt.ProductSymbol = src.ProductSymbol
           AND tgt.ContractYear  = src.ContractYear
           AND tgt.ContractMonth = src.ContractMonth
        WHEN MATCHED THEN UPDATE SET
            ProductDescription = src.ProductDescription,
            [Open]             = src.[Open],
            High               = src.High,
            HighABIndicator    = src.HighABIndicator,
            Low                = src.Low,
            LowABIndicator     = src.LowABIndicator,
            [Last]             = src.[Last],
            LastABIndicator    = src.LastABIndicator,
            Settle             = src.Settle,
            PctChange          = src.PctChange,
            EstVol             = src.EstVol,
            PriorSettle        = src.PriorSettle,
            PriorVol           = src.PriorVol,
            PriorInt           = src.PriorInt,
            ModifiedAtUtc      = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (ExchangeCode, ProductCode, TradeDate, ProductSymbol, ContractYear, ContractMonth,
                    ProductDescription,
                    [Open], High, HighABIndicator, Low, LowABIndicator, [Last], LastABIndicator,
                    Settle, PctChange, EstVol, PriorSettle, PriorVol, PriorInt, ModifiedAtUtc)
            VALUES (src.ExchangeCode, src.ProductCode, src.TradeDate, src.ProductSymbol,
                    src.ContractYear, src.ContractMonth, src.ProductDescription,
                    src.[Open], src.High, src.HighABIndicator, src.Low, src.LowABIndicator,
                    src.[Last], src.LastABIndicator, src.Settle, src.PctChange, src.EstVol,
                    src.PriorSettle, src.PriorVol, src.PriorInt, SYSUTCDATETIME());

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
-- arm.usp_ValidateLoad -- observational only. Returns (CheckName, Severity,
-- Detail) rows; the loader logs them and NEVER changes its outcome based on
-- them.
--
-- Scoped to @TradeDate when supplied (the newest date the run enumerated), or the
-- whole table when NULL.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @TradeDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- 1) Bulletins the loader could not process at all.
    SELECT 'FailedFiles' AS CheckName, 'Warning' AS Severity,
           CONCAT(COUNT(*), ' bulletin(s) recorded Failed: ',
                  STRING_AGG(CONVERT(VARCHAR(128), f.FileName), ', ')) AS Detail
    FROM arm.FileLog f
    JOIN arm.Status s ON s.Id = f.StatusId
    WHERE s.[Name] = 'Failed'
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate)
    HAVING COUNT(*) > 0

    UNION ALL

    -- 2) Rows the parser deliberately dropped (BALMO/day-label futures). Expected
    --    and non-zero for STLCPC and STLEQT -- surfaced so the omission stays
    --    visible rather than becoming folklore.
    SELECT 'SkippedDayLabelRows', 'Info',
           CONCAT(SUM(f.DayLabelRowsSkipped), ' BALMO/day-label future row(s) skipped across ',
                  COUNT(*), ' bulletin(s); arm.STLBASIC_Future has no day component in its key')
    FROM arm.FileLog f
    WHERE f.DayLabelRowsSkipped > 0
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate)
    HAVING SUM(f.DayLabelRowsSkipped) > 0

    UNION ALL

    -- 3) Unclassified lines. ONE per STLEQT bulletin is expected (the
    --    "KYP11 <br />" artifact); more than that suggests a layout change.
    SELECT 'UnclassifiedLines', 'Warning',
           CONCAT(SUM(f.UnclassifiedLines), ' unclassified line(s) across ', COUNT(*),
                  ' bulletin(s) — investigate if this exceeds one per STLEQT file')
    FROM arm.FileLog f
    WHERE f.UnclassifiedLines > 0
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate)
    HAVING SUM(f.UnclassifiedLines) > 0

    UNION ALL

    -- 4) A bulletin that parsed to zero rows. The loader already throws on this,
    --    so a row here means something wrote a Success with no data.
    SELECT 'EmptySuccess', 'Warning',
           CONCAT(COUNT(*), ' bulletin(s) recorded Success with zero rows: ',
                  STRING_AGG(CONVERT(VARCHAR(128), f.FileName), ', '))
    FROM arm.FileLog f
    JOIN arm.Status s ON s.Id = f.StatusId
    WHERE s.[Name] = 'Success'
      AND f.[RowCount] = 0
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate)
    HAVING COUNT(*) > 0

    UNION ALL

    -- 5) FileLog says it loaded rows that are not in the fact tables. Catches a
    --    merge that silently affected nothing.
    SELECT 'HubVersusFacts', 'Warning',
           CONCAT('Trade date ', CONVERT(VARCHAR(10), f.TradeDate, 23), ' feed ', f.FeedId,
                  ': FileLog claims ', f.OptionRowCount, ' option + ', f.FutureRowCount,
                  ' future row(s) but the tables hold ',
                  (SELECT COUNT(*) FROM arm.STLBASIC_Option o
                    WHERE o.TradeDate = f.TradeDate AND o.ExchangeCode = f.ExchangeCode), ' + ',
                  (SELECT COUNT(*) FROM arm.STLBASIC_Future u
                    WHERE u.TradeDate = f.TradeDate AND u.ExchangeCode = f.ExchangeCode))
    FROM arm.FileLog f
    JOIN arm.Status s ON s.Id = f.StatusId
    WHERE s.[Name] = 'Success'
      AND f.ExchangeCode IS NOT NULL
      AND (@TradeDate IS NULL OR f.TradeDate = @TradeDate)
      AND (f.OptionRowCount <> (SELECT COUNT(*) FROM arm.STLBASIC_Option o
                                 WHERE o.TradeDate = f.TradeDate AND o.ExchangeCode = f.ExchangeCode)
        OR f.FutureRowCount <> (SELECT COUNT(*) FROM arm.STLBASIC_Future u
                                 WHERE u.TradeDate = f.TradeDate AND u.ExchangeCode = f.ExchangeCode))

    UNION ALL

    -- 6) Rows where every measure is NULL. Legal (a listed contract with no
    --    activity settles to nothing), but a whole feed like this means the
    --    fixed-width geometry has shifted -- the failure mode positional parsing
    --    has to be watched for.
    SELECT 'AllMeasuresNull', 'Warning',
           CONCAT(COUNT(*), ' option row(s) have every price and volume NULL for ',
                  CONVERT(VARCHAR(10), o.TradeDate, 23), ' ', o.ExchangeCode)
    FROM arm.STLBASIC_Option o
    WHERE (@TradeDate IS NULL OR o.TradeDate = @TradeDate)
      AND o.[Open] IS NULL AND o.High IS NULL AND o.Low IS NULL AND o.[Last] IS NULL
      AND o.Settle IS NULL AND o.PctChange IS NULL AND o.EstVol IS NULL
      AND o.PriorSettle IS NULL AND o.PriorVol IS NULL AND o.PriorInt IS NULL
    GROUP BY o.TradeDate, o.ExchangeCode
    HAVING COUNT(*) > 0

    UNION ALL

    -- 7) Indicator letters outside the observed set. Live data only ever shows
    --    High=B, Low=A, Last=A and Last=B.
    SELECT 'UnexpectedIndicator', 'Warning',
           CONCAT('Indicator value(s) outside {A,B} present for ',
                  CONVERT(VARCHAR(10), o.TradeDate, 23), ' ', o.ExchangeCode)
    FROM arm.STLBASIC_Option o
    WHERE (@TradeDate IS NULL OR o.TradeDate = @TradeDate)
      AND (o.HighABIndicator NOT IN ('A', 'B')
        OR o.LowABIndicator  NOT IN ('A', 'B')
        OR o.LastABIndicator NOT IN ('A', 'B'))
    GROUP BY o.TradeDate, o.ExchangeCode
    HAVING COUNT(*) > 0

    UNION ALL

    -- 8) PutCall outside {C,P} -- the parser only ever writes those two, so this
    --    catches a hand-edit or a partial deployment.
    SELECT 'UnexpectedPutCall', 'Warning',
           CONCAT(COUNT(*), ' option row(s) have PutCall outside {C,P} for ',
                  CONVERT(VARCHAR(10), o.TradeDate, 23))
    FROM arm.STLBASIC_Option o
    WHERE (@TradeDate IS NULL OR o.TradeDate = @TradeDate)
      AND o.PutCall NOT IN ('C', 'P')
    GROUP BY o.TradeDate
    HAVING COUNT(*) > 0

    UNION ALL

    -- 9) Contract months outside 1..12. TINYINT allows 0 and 13+, and a bad
    --    month-token parse would land here.
    SELECT 'BadContractMonth', 'Warning',
           CONCAT(COUNT(*), ' row(s) have ContractMonth outside 1..12 for ',
                  CONVERT(VARCHAR(10), x.TradeDate, 23))
    FROM (
        SELECT TradeDate, ContractMonth FROM arm.STLBASIC_Option
        UNION ALL
        SELECT TradeDate, ContractMonth FROM arm.STLBASIC_Future
    ) AS x
    WHERE (@TradeDate IS NULL OR x.TradeDate = @TradeDate)
      AND (x.ContractMonth < 1 OR x.ContractMonth > 12)
    GROUP BY x.TradeDate
    HAVING COUNT(*) > 0;
END
GO
