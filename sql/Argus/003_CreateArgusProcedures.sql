-- =============================================================================
-- 003_CreateArgusProcedures.sql
-- Database : Argus
-- Schema   : dlp
--
--   dlp.usp_UpsertFileLog                audit hub upsert, returns FileLogId
--   dlp.usp_BulkMerge<Feed>              x14, one per merged reference feed
--   dlp.usp_ReplaceQuoteLookup           the ONE destructive path (full replace)
--   dlp.usp_BulkMergeTimeSeriesDetail    fans one TVP into both fact tables
--   dlp.usp_ValidateLoad                 post-load observational anomaly report
--
-- Shared conventions across every merge proc:
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY <pk> ...), keep row 1. SQL MERGE raises
--   "attempted to update the same row more than once" on a duplicated source
--   key, and latestHoliday.csv genuinely ships 11 exact duplicates.
--
--   NO DELETE-BY-ABSENCE. None of the merges has WHEN NOT MATCHED BY SOURCE
--   THEN DELETE. Each work unit merges one snapshot; a short download would
--   otherwise wipe good rows. The one table that must mirror removals
--   (QuoteLookup) uses an explicit, guarded full-replace proc instead.
--
--   ModifiedAtUtc is set explicitly with SYSUTCDATETIME(), so rows written by
--   this loader hold true UTC even though the table DEFAULT is sysdatetime().
--
--   Each returns SELECT ... AS RecordsProcessed, which SqlSinkBase surfaces to
--   core.LoadLog.
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.usp_UpsertFileLog — one hub row per source FILE, for EVERY outcome
-- (Success / NotAvailable / Failed). FileName is the natural key, so a
-- re-download updates the existing row. Returns the FileLogId.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_UpsertFileLog
    @FileName              VARCHAR(200),
    @FeedId                VARCHAR(50)   = NULL,
    @RemoteFolder          VARCHAR(100)  = NULL,
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
    DECLARE @StatusId INT = (SELECT Id FROM dlp.Status WHERE [Name] = @StatusLabel);
    IF @StatusId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Status ''%s''.', 16, 1, @StatusLabel);
        RETURN;
    END

    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE dlp.FileLog AS tgt
    USING (SELECT @FileName AS FileName) AS src
       ON tgt.FileName = src.FileName
    WHEN MATCHED THEN UPDATE SET
        FeedId                = @FeedId,
        RemoteFolder          = @RemoteFolder,
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
        INSERT (FileName, FeedId, RemoteFolder, SourceFileDate, StatusId,
                RemoteLastModifiedUtc, SizeBytes, [RowCount], ErrorMessage,
                RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.FileName, @FeedId, @RemoteFolder, @SourceFileDate, @StatusId,
                @RemoteLastModifiedUtc, @SizeBytes, @RowCount, @ErrorMessage,
                @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- =============================================================================
-- REFERENCE FEEDS — merged by primary key
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeCategoryLookup  <- latestCategory.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeCategoryLookup
    @Records dlp.CategoryLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT Code, Category, DisplayName
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Code, Category ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.CategoryLookup AS tgt
    USING Src AS src
       ON tgt.Code = src.Code AND tgt.Category = src.Category
    WHEN MATCHED THEN UPDATE SET
        DisplayName   = src.DisplayName,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Code, Category, DisplayName, ModifiedAtUtc)
        VALUES (src.Code, src.Category, src.DisplayName, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeCodeLookup  <- latestCodes.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeCodeLookup
    @Records dlp.CodeLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT Code, DisplayName, DeliveryMode, [Unit], Frequency, Specification
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Code ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.CodeLookup AS tgt
    USING Src AS src
       ON tgt.Code = src.Code
    WHEN MATCHED THEN UPDATE SET
        DisplayName   = src.DisplayName,
        DeliveryMode  = src.DeliveryMode,
        [Unit]        = src.[Unit],
        Frequency     = src.Frequency,
        Specification = src.Specification,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Code, DisplayName, DeliveryMode, [Unit], Frequency, Specification, ModifiedAtUtc)
        VALUES (src.Code, src.DisplayName, src.DeliveryMode, src.[Unit], src.Frequency,
                src.Specification, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeModuleDetailLookup  <- latestModuleDetails.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeModuleDetailLookup
    @Records dlp.ModuleDetailLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT Module, Code, TimestampTypeID, PriceTypeID, ContinuousForwardPeriod, StartDate, EndDate
        FROM (SELECT *,
                     ROW_NUMBER() OVER (
                         PARTITION BY Module, Code, TimestampTypeID, PriceTypeID, ContinuousForwardPeriod
                         ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.ModuleDetailLookup AS tgt
    USING Src AS src
       ON  tgt.Module                  = src.Module
       AND tgt.Code                    = src.Code
       AND tgt.TimestampTypeID         = src.TimestampTypeID
       AND tgt.PriceTypeID             = src.PriceTypeID
       AND tgt.ContinuousForwardPeriod = src.ContinuousForwardPeriod
    WHEN MATCHED THEN UPDATE SET
        StartDate     = src.StartDate,
        EndDate       = src.EndDate,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Module, Code, TimestampTypeID, PriceTypeID, ContinuousForwardPeriod,
                StartDate, EndDate, ModifiedAtUtc)
        VALUES (src.Module, src.Code, src.TimestampTypeID, src.PriceTypeID,
                src.ContinuousForwardPeriod, src.StartDate, src.EndDate, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeModuleLookup  <- latestModules.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeModuleLookup
    @Records dlp.ModuleLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT Module, [Path], FileName, [Description], Folder, [Time], LocalTime, LocalTimeZone
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Module ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.ModuleLookup AS tgt
    USING Src AS src
       ON tgt.Module = src.Module
    WHEN MATCHED THEN UPDATE SET
        [Path]        = src.[Path],
        FileName      = src.FileName,
        [Description] = src.[Description],
        Folder        = src.Folder,
        [Time]        = src.[Time],
        LocalTime     = src.LocalTime,
        LocalTimeZone = src.LocalTimeZone,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Module, [Path], FileName, [Description], Folder, [Time], LocalTime,
                LocalTimeZone, ModifiedAtUtc)
        VALUES (src.Module, src.[Path], src.FileName, src.[Description], src.Folder,
                src.[Time], src.LocalTime, src.LocalTimeZone, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergePriceTypeLookup  <- latestPricetype.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergePriceTypeLookup
    @Records dlp.PriceTypeLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT PriceTypeID, [Description]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY PriceTypeID ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.PriceTypeLookup AS tgt
    USING Src AS src
       ON tgt.PriceTypeID = src.PriceTypeID
    WHEN MATCHED THEN UPDATE SET
        [Description] = src.[Description],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PriceTypeID, [Description], ModifiedAtUtc)
        VALUES (src.PriceTypeID, src.[Description], SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeTimestampTypeLookup  <- latestTimestamp.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeTimestampTypeLookup
    @Records dlp.TimestampTypeLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT TimestampTypeID, [Description]
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY TimestampTypeID ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.TimestampTypeLookup AS tgt
    USING Src AS src
       ON tgt.TimestampTypeID = src.TimestampTypeID
    WHEN MATCHED THEN UPDATE SET
        [Description] = src.[Description],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TimestampTypeID, [Description], ModifiedAtUtc)
        VALUES (src.TimestampTypeID, src.[Description], SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeTimingLookup  <- latestTiming.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeTimingLookup
    @Records dlp.TimingLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT TimingID, [Description], MinForwardPeriod, MaxForwardPeriod, ForwardPeriodDescription
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY TimingID ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.TimingLookup AS tgt
    USING Src AS src
       ON tgt.TimingID = src.TimingID
    WHEN MATCHED THEN UPDATE SET
        [Description]            = src.[Description],
        MinForwardPeriod         = src.MinForwardPeriod,
        MaxForwardPeriod         = src.MaxForwardPeriod,
        ForwardPeriodDescription = src.ForwardPeriodDescription,
        ModifiedAtUtc            = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TimingID, [Description], MinForwardPeriod, MaxForwardPeriod,
                ForwardPeriodDescription, ModifiedAtUtc)
        VALUES (src.TimingID, src.[Description], src.MinForwardPeriod, src.MaxForwardPeriod,
                src.ForwardPeriodDescription, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeUnitLookup  <- latestUnits.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeUnitLookup
    @Records dlp.UnitLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT UnitID, [Description], UnitDetails
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY UnitID ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.UnitLookup AS tgt
    USING Src AS src
       ON tgt.UnitID = src.UnitID
    WHEN MATCHED THEN UPDATE SET
        [Description] = src.[Description],
        UnitDetails   = src.UnitDetails,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (UnitID, [Description], UnitDetails, ModifiedAtUtc)
        VALUES (src.UnitID, src.[Description], src.UnitDetails, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeUnitCodeConversion  <- latestUnitCodeConv.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeUnitCodeConversion
    @Records dlp.UnitCodeConversionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT UnitID, BaseUnitID, CodeID, ValidFrom, ValidTo, Ratio
        FROM (SELECT *,
                     ROW_NUMBER() OVER (PARTITION BY UnitID, BaseUnitID, CodeID, ValidFrom
                                        ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.UnitCodeConversion AS tgt
    USING Src AS src
       ON  tgt.UnitID     = src.UnitID
       AND tgt.BaseUnitID = src.BaseUnitID
       AND tgt.CodeID     = src.CodeID
       AND tgt.ValidFrom  = src.ValidFrom
    WHEN MATCHED THEN UPDATE SET
        ValidTo       = src.ValidTo,
        Ratio         = src.Ratio,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (UnitID, BaseUnitID, CodeID, ValidFrom, ValidTo, Ratio, ModifiedAtUtc)
        VALUES (src.UnitID, src.BaseUnitID, src.CodeID, src.ValidFrom, src.ValidTo,
                src.Ratio, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeHolidayRegionLookup  <- latestHolidayRegion.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeHolidayRegionLookup
    @Records dlp.HolidayRegionLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT HolidayRegionID, HolidayRegionDescription
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY HolidayRegionID ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.HolidayRegionLookup AS tgt
    USING Src AS src
       ON tgt.HolidayRegionID = src.HolidayRegionID
    WHEN MATCHED THEN UPDATE SET
        HolidayRegionDescription = src.HolidayRegionDescription,
        ModifiedAtUtc            = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (HolidayRegionID, HolidayRegionDescription, ModifiedAtUtc)
        VALUES (src.HolidayRegionID, src.HolidayRegionDescription, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeHoliday  <- latestHoliday.csv
--
-- The de-dup here is NOT defensive: the source ships 11 exact duplicate rows.
-- Both columns are the key and there is nothing else to update, so a match is a
-- no-op apart from the ModifiedAtUtc touch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeHoliday
    @Records dlp.HolidayTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT HolidayRegionID, HolidayDate
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY HolidayRegionID, HolidayDate
                                           ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.Holiday AS tgt
    USING Src AS src
       ON tgt.HolidayRegionID = src.HolidayRegionID AND tgt.HolidayDate = src.HolidayDate
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (HolidayRegionID, HolidayDate, ModifiedAtUtc)
        VALUES (src.HolidayRegionID, src.HolidayDate, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeQuoteHolidayRegion  <- latestQuoteHolidayRegion.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeQuoteHolidayRegion
    @Records dlp.QuoteHolidayRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID,
               HolidayRegionID1, HolidayRegionID2, HolidayRegionID3
        FROM (SELECT *,
                     ROW_NUMBER() OVER (PARTITION BY Code, ContinuousForwardPeriod,
                                                     TimestampTypeID, PriceTypeID
                                        ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.QuoteHolidayRegion AS tgt
    USING Src AS src
       ON  tgt.Code                    = src.Code
       AND tgt.ContinuousForwardPeriod = src.ContinuousForwardPeriod
       AND tgt.TimestampTypeID         = src.TimestampTypeID
       AND tgt.PriceTypeID             = src.PriceTypeID
    WHEN MATCHED THEN UPDATE SET
        HolidayRegionID1 = src.HolidayRegionID1,
        HolidayRegionID2 = src.HolidayRegionID2,
        HolidayRegionID3 = src.HolidayRegionID3,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID,
                HolidayRegionID1, HolidayRegionID2, HolidayRegionID3, ModifiedAtUtc)
        VALUES (src.Code, src.ContinuousForwardPeriod, src.TimestampTypeID, src.PriceTypeID,
                src.HolidayRegionID1, src.HolidayRegionID2, src.HolidayRegionID3, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeNewsCategoryLookup  <- latestNewsCategory.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeNewsCategoryLookup
    @Records dlp.NewsCategoryLookupTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT CategoryType, CategoryID, ParentID, [Description], Active
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY CategoryType, CategoryID
                                           ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.NewsCategoryLookup AS tgt
    USING Src AS src
       ON tgt.CategoryType = src.CategoryType AND tgt.CategoryID = src.CategoryID
    WHEN MATCHED THEN UPDATE SET
        ParentID      = src.ParentID,
        [Description] = src.[Description],
        Active        = src.Active,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (CategoryType, CategoryID, ParentID, [Description], Active, ModifiedAtUtc)
        VALUES (src.CategoryType, src.CategoryID, src.ParentID, src.[Description],
                src.Active, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeRvpCodeReference  <- latestRVP_Code_reference.csv
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeRvpCodeReference
    @Records dlp.RvpCodeReferenceTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Rows INT = 0;

    ;WITH Src AS
    (
        SELECT CodeID, RvpCodeID
        FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY CodeID, RvpCodeID
                                           ORDER BY (SELECT 1)) AS rn
              FROM @Records) AS d
        WHERE d.rn = 1
    )
    MERGE dlp.RvpCodeReference AS tgt
    USING Src AS src
       ON tgt.CodeID = src.CodeID AND tgt.RvpCodeID = src.RvpCodeID
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (CodeID, RvpCodeID, ModifiedAtUtc)
        VALUES (src.CodeID, src.RvpCodeID, SYSUTCDATETIME());

    SET @Rows = @@ROWCOUNT;
    SELECT @Rows AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dlp.usp_ReplaceQuoteLookup  <- latestQuotes.csv
--
-- THE ONLY DESTRUCTIVE PATH IN THIS LOADER. dlp.QuoteLookup has no primary key
-- by specification and its rows are validity-window versions of a quote
-- definition, so it is loaded as a FULL REPLACE: the table ends up an exact
-- mirror of the snapshot, including rows Argus has REMOVED — which a merge would
-- never apply.
--
-- Because it deletes, it is guarded twice:
--
--   1. EMPTY SOURCE -> abort. Never wipe the table with nothing to put back.
--      (SqlSinkBase already skips the call at 0 rows; this is belt-and-braces.)
--   2. SHORT SOURCE -> abort. If the incoming count is below @MinRowFraction of
--      what is already stored, the download was probably truncated. A truncated
--      CSV yields well-formed rows, just fewer of them, so no row-level parser
--      can detect this — only the count can.
--
-- Both raise, which rolls the transaction back and fails just this work unit.
--
-- DELETE rather than TRUNCATE so the loader needs only db_datareader /
-- db_datawriter / EXECUTE, not ALTER. At ~150k rows the cost is immaterial; swap
-- in TRUNCATE TABLE if the deployment grants ALTER.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_ReplaceQuoteLookup
    @Records         dlp.QuoteLookupTvp READONLY,
    @MinRowFraction  DECIMAL(5,4) = 0.5000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Incoming INT = (SELECT COUNT(1) FROM @Records);
    DECLARE @Existing INT = (SELECT COUNT(1) FROM dlp.QuoteLookup);

    IF @Incoming = 0
    BEGIN
        RAISERROR('usp_ReplaceQuoteLookup: refusing to replace %d stored row(s) with an EMPTY source.',
                  16, 1, @Existing);
        RETURN;
    END

    DECLARE @Floor INT = CAST(@Existing * @MinRowFraction AS INT);

    IF @Existing > 0 AND @Incoming < @Floor
    BEGIN
        -- Report the floor that was actually applied, derived from @MinRowFraction,
        -- so a caller that overrides the default is not told the wrong threshold.
        DECLARE @Pct INT = CAST(@MinRowFraction * 100 AS INT);

        RAISERROR('usp_ReplaceQuoteLookup: refusing to replace %d stored row(s) with only %d incoming row(s) - below the %d%% floor of %d row(s). Suspect a truncated download.',
                  16, 1, @Existing, @Incoming, @Pct, @Floor);
        RETURN;
    END

    BEGIN TRY
        BEGIN TRANSACTION;

        DELETE FROM dlp.QuoteLookup;

        INSERT dlp.QuoteLookup
        (
            Code, ContinuousForwardPeriod, Timing, ForwardPeriodDescription,
            TimestampTypeID, PriceTypeID, DifferentialBasis, DifferentialBasisTiming,
            StartDate, EndDate, OldCode, DecimalPlaces, ModifiedAtUtc
        )
        SELECT
            Code, ContinuousForwardPeriod, Timing, ForwardPeriodDescription,
            TimestampTypeID, PriceTypeID, DifferentialBasis, DifferentialBasisTiming,
            StartDate, EndDate, OldCode, DecimalPlaces, SYSUTCDATETIME()
        FROM @Records;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT @Incoming AS RecordsProcessed;
END
GO

-- =============================================================================
-- FACT FEED
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.usp_BulkMergeTimeSeriesDetail — the single write path for DCRDEUS.
--
-- Takes one file's parsed rows and merges them into BOTH fact tables inside one
-- transaction, so the current table and the history table can never disagree:
--
--   dlp.TimeSeriesDetailHistory  keyed (..., [Date], RecordStatus) — keeps the
--        'N' original AND any later 'C' correction of it.
--   dlp.TimeSeriesDetail         keyed (..., [Date])               — one current
--        row; a 'C' correction supersedes the 'N' it corrects.
--
-- ORDERING GUARD — the load-bearing part. A DCRDEUS file carries TWO publication
-- dates and consecutive files overlap, so the same key genuinely arrives from
-- more than one file in a single run (docs/apis/Argus.md 5.5). Work units run
-- concurrently, so they can arrive in either order. Every UPDATE branch is
-- guarded by
--     src.SourceFileDate >= ISNULL(tgt.RecordStatusDate, '19000101')
-- so an older file can never overwrite a newer one, and the outcome is
-- independent of arrival order and of how often the loader re-runs.
-- tgt.RecordStatusDate IS the stored file date, which is what lets the guard
-- work without adding a column to the specified DDL.
--
-- BATCH DE-DUP. Within one file the key is unique (verified live), so this is
-- defensive. The tie-break is deliberate rather than arbitrary: newest file
-- first, then a 'C' correction ahead of an 'N' original.
--
-- Returns the distinct rows merged into dlp.TimeSeriesDetail.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_BulkMergeTimeSeriesDetail
    @Records dlp.TimeSeriesDetailTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @CurrentRows INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- ---- 1) HISTORY: one row per (key + RecordStatus) -------------------
        ;WITH SrcHistory AS
        (
            SELECT Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date],
                   RecordStatus, RecordStatusDate, FwdPeriod, [Value], DiffBaseRoll,
                   [Year], SourcePath, SourceFileDate
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY Module, Code, TimestampTypeID, PriceTypeID,
                                        ContFwd, [Date], RecordStatus
                           ORDER BY SourceFileDate DESC) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE dlp.TimeSeriesDetailHistory AS tgt
        USING SrcHistory AS src
           ON  tgt.Module          = src.Module
           AND tgt.Code            = src.Code
           AND tgt.TimestampTypeID = src.TimestampTypeID
           AND tgt.PriceTypeID     = src.PriceTypeID
           AND tgt.ContFwd         = src.ContFwd
           AND tgt.[Date]          = src.[Date]
           AND tgt.RecordStatus    = src.RecordStatus
        WHEN MATCHED AND src.SourceFileDate >= ISNULL(tgt.RecordStatusDate, '19000101') THEN UPDATE SET
            RecordStatusDate = src.RecordStatusDate,
            FwdPeriod        = src.FwdPeriod,
            [Value]          = src.[Value],
            DiffBaseRoll     = src.DiffBaseRoll,
            [Year]           = src.[Year],
            SourcePath       = src.SourcePath,
            ModifiedAtUtc    = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date],
                    RecordStatus, RecordStatusDate, FwdPeriod, [Value], DiffBaseRoll,
                    [Year], SourcePath, ModifiedAtUtc)
            VALUES (src.Module, src.Code, src.TimestampTypeID, src.PriceTypeID, src.ContFwd,
                    src.[Date], src.RecordStatus, src.RecordStatusDate, src.FwdPeriod,
                    src.[Value], src.DiffBaseRoll, src.[Year], src.SourcePath, SYSUTCDATETIME());

        -- ---- 2) CURRENT: one row per key ------------------------------------
        ;WITH SrcCurrent AS
        (
            SELECT Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date],
                   RecordStatus, RecordStatusDate, FwdPeriod, [Value], DiffBaseRoll,
                   [Year], SourcePath, SourceFileDate
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY Module, Code, TimestampTypeID, PriceTypeID,
                                        ContFwd, [Date]
                           ORDER BY SourceFileDate DESC,
                                    CASE RecordStatus WHEN 'C' THEN 0 ELSE 1 END) AS rn
                FROM @Records
            ) AS d
            WHERE d.rn = 1
        )
        MERGE dlp.TimeSeriesDetail AS tgt
        USING SrcCurrent AS src
           ON  tgt.Module          = src.Module
           AND tgt.Code            = src.Code
           AND tgt.TimestampTypeID = src.TimestampTypeID
           AND tgt.PriceTypeID     = src.PriceTypeID
           AND tgt.ContFwd         = src.ContFwd
           AND tgt.[Date]          = src.[Date]
        WHEN MATCHED AND src.SourceFileDate >= ISNULL(tgt.RecordStatusDate, '19000101') THEN UPDATE SET
            FwdPeriod        = src.FwdPeriod,
            [Value]          = src.[Value],
            DiffBaseRoll     = src.DiffBaseRoll,
            [Year]           = src.[Year],
            RecordStatus     = src.RecordStatus,
            RecordStatusDate = src.RecordStatusDate,
            SourcePath       = src.SourcePath,
            ModifiedAtUtc    = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date],
                    FwdPeriod, [Value], DiffBaseRoll, [Year], RecordStatus,
                    RecordStatusDate, SourcePath, ModifiedAtUtc)
            VALUES (src.Module, src.Code, src.TimestampTypeID, src.PriceTypeID, src.ContFwd,
                    src.[Date], src.FwdPeriod, src.[Value], src.DiffBaseRoll, src.[Year],
                    src.RecordStatus, src.RecordStatusDate, src.SourcePath, SYSUTCDATETIME());

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

-- =============================================================================
-- VALIDATION
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.usp_ValidateLoad — post-load OBSERVATIONAL anomaly report. ONE result set
-- in the repo's uniform shape so ArgusLoadValidator can log it generically.
-- Observational only (no side effects); the caller decides what matters.
-- @ReportDate scopes the day-level checks; pass NULL for the whole table.
--
--   CheckName      what was measured
--   Scope          the grouping key the count applies to
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
--
-- The orphan checks (4-7) are the ones no FK enforces. They are EXPECTED to be
-- non-zero at times: the fact feed and the reference feed load independently, so
-- a fact row can legitimately arrive before the lookup snapshot that explains it.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dlp.usp_ValidateLoad
    @ReportDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH Report AS
    (
        -- 1) Current-table row count (expect > 0 after any fact load).
        SELECT
            CAST('TimeSeriesDetailRowCount' AS VARCHAR(48)) AS CheckName,
            CAST(NULL AS NVARCHAR(200))                     AS Scope,
            CAST(NULL AS INT)                               AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)                        AS ActualCount,
            CAST(NULL AS NVARCHAR(400))                     AS Detail
        FROM dlp.TimeSeriesDetail

        UNION ALL
        -- 2) History count — must be >= the current count by construction.
        SELECT
            CAST('TimeSeriesDetailHistoryRowCount' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('>= TimeSeriesDetailRowCount by construction' AS NVARCHAR(400))
        FROM dlp.TimeSeriesDetailHistory

        UNION ALL
        -- 3) Every current row must have a matching history row (expect 0 gaps).
        SELECT
            CAST('CurrentRowsMissingFromHistory' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('TimeSeriesDetail rows with no matching history row' AS NVARCHAR(400))
        FROM dlp.TimeSeriesDetail AS c
        WHERE NOT EXISTS (
            SELECT 1 FROM dlp.TimeSeriesDetailHistory AS h
            WHERE h.Module = c.Module AND h.Code = c.Code
              AND h.TimestampTypeID = c.TimestampTypeID AND h.PriceTypeID = c.PriceTypeID
              AND h.ContFwd = c.ContFwd AND h.[Date] = c.[Date]
              AND h.RecordStatus = c.RecordStatus)

        UNION ALL
        -- 4) Fact Codes with no CodeLookup row. Non-zero is possible and NOT an
        --    error: the two feeds load independently. All 267 live codes matched.
        SELECT
            CAST('FactCodesNotInCodeLookup' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('distinct Code in TimeSeriesDetail absent from CodeLookup' AS NVARCHAR(400))
        FROM (SELECT DISTINCT Code FROM dlp.TimeSeriesDetail) AS f
        WHERE NOT EXISTS (SELECT 1 FROM dlp.CodeLookup AS l WHERE l.Code = f.Code)

        UNION ALL
        -- 5) Fact Modules with no ModuleLookup row. This is the check that would
        --    catch a bad suffix-to-Module mapping for a NEW DCRDEUS file type.
        SELECT
            CAST('FactModulesNotInModuleLookup' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('distinct Module in TimeSeriesDetail absent from ModuleLookup - suspect the suffix map' AS NVARCHAR(400))
        FROM (SELECT DISTINCT Module FROM dlp.TimeSeriesDetail) AS f
        WHERE NOT EXISTS (SELECT 1 FROM dlp.ModuleLookup AS l WHERE l.Module = f.Module)

        UNION ALL
        -- 6) Fact TimestampTypeIDs with no lookup row.
        SELECT
            CAST('FactTimestampTypesNotInLookup' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('distinct TimestampTypeID in TimeSeriesDetail absent from TimestampTypeLookup' AS NVARCHAR(400))
        FROM (SELECT DISTINCT TimestampTypeID FROM dlp.TimeSeriesDetail) AS f
        WHERE NOT EXISTS (SELECT 1 FROM dlp.TimestampTypeLookup AS l
                          WHERE l.TimestampTypeID = f.TimestampTypeID)

        UNION ALL
        -- 7) Fact PriceTypeIDs with no lookup row.
        SELECT
            CAST('FactPriceTypesNotInLookup' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('distinct PriceTypeID in TimeSeriesDetail absent from PriceTypeLookup' AS NVARCHAR(400))
        FROM (SELECT DISTINCT PriceTypeID FROM dlp.TimeSeriesDetail) AS f
        WHERE NOT EXISTS (SELECT 1 FROM dlp.PriceTypeLookup AS l WHERE l.PriceTypeID = f.PriceTypeID)

        UNION ALL
        -- 8) Corrected keys: history entries holding more than one RecordStatus.
        --    Informational — this is the feature the history table exists for.
        SELECT
            CAST('CorrectedKeys' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('keys whose history holds more than one RecordStatus' AS NVARCHAR(400))
        FROM (
            SELECT Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date]
            FROM dlp.TimeSeriesDetailHistory
            GROUP BY Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date]
            HAVING COUNT(DISTINCT RecordStatus) > 1
        ) AS r

        UNION ALL
        -- 9) Fact rows with no Value at all (expect 0 — never observed live).
        SELECT
            CAST('FactRowsWithNullValue' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('TimeSeriesDetail rows whose Value is NULL' AS NVARCHAR(400))
        FROM dlp.TimeSeriesDetail
        WHERE [Value] IS NULL

        UNION ALL
        -- 10) Untrimmed key values (expect 0 — the source quotes space-padded
        --     values, and the parser trims after unquoting).
        SELECT
            CAST('UntrimmedFactKeys' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Module or Code with leading/trailing whitespace' AS NVARCHAR(400))
        FROM dlp.TimeSeriesDetail
        WHERE Module <> LTRIM(RTRIM(Module)) OR Code <> LTRIM(RTRIM(Code))

        UNION ALL
        SELECT
            CAST('UntrimmedLookupKeys' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('CategoryLookup Code/Category with leading/trailing whitespace' AS NVARCHAR(400))
        FROM dlp.CategoryLookup
        WHERE Code <> LTRIM(RTRIM(Code)) OR Category <> LTRIM(RTRIM(Category))

        UNION ALL
        -- 11) Reference table sizes. QuoteLookup leads because it is the one
        --     table a bad replace could empty.
        SELECT CAST('QuoteLookupRowCount'        AS VARCHAR(48)), CAST(NULL AS NVARCHAR(200)),
               CAST(NULL AS INT), CAST(COUNT(1) AS BIGINT),
               CAST('full-replace target - a sharp drop means a truncated download' AS NVARCHAR(400))
        FROM dlp.QuoteLookup
        UNION ALL
        SELECT CAST('CodeLookupRowCount'         AS VARCHAR(48)), NULL, NULL, CAST(COUNT(1) AS BIGINT), NULL FROM dlp.CodeLookup
        UNION ALL
        SELECT CAST('CategoryLookupRowCount'     AS VARCHAR(48)), NULL, NULL, CAST(COUNT(1) AS BIGINT), NULL FROM dlp.CategoryLookup
        UNION ALL
        SELECT CAST('ModuleLookupRowCount'       AS VARCHAR(48)), NULL, NULL, CAST(COUNT(1) AS BIGINT), NULL FROM dlp.ModuleLookup
        UNION ALL
        SELECT CAST('ModuleDetailLookupRowCount' AS VARCHAR(48)), NULL, NULL, CAST(COUNT(1) AS BIGINT), NULL FROM dlp.ModuleDetailLookup

        UNION ALL
        -- 12) FileLog outcomes not Success (warn — a failed file leaves a gap).
        SELECT
            CAST('FileLogNotSuccess' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('dlp.FileLog rows whose Status <> Success' AS NVARCHAR(400))
        FROM dlp.FileLog AS f
        JOIN dlp.Status AS s ON s.Id = f.StatusId
        WHERE s.[Name] <> 'Success'

        UNION ALL
        -- 13) Row count for the requested report date (may legitimately be 0 on a
        --     weekend or holiday — warn, do not fail). NULL -> whole table.
        SELECT
            CAST('TimeSeriesRowCountForDate' AS VARCHAR(48)),
            CAST(CONCAT('Date=', ISNULL(CONVERT(VARCHAR(10), @ReportDate, 23), 'ALL')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM dlp.TimeSeriesDetail AS c
        WHERE @ReportDate IS NULL OR c.[Date] = @ReportDate
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report;
END
GO
