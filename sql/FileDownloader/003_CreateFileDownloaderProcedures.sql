/*
    FileDownloader loader - 003 - merge procedure.

    Target database: Mako.

    Three procedures:
      usp_MergeFileDownloaderFile     merges a run's manifest into FileDownloader_File
      usp_GetFileDownloaderQueue      reads one source's pending-download queue
      usp_StampFileDownloaderRow      marks one queue row downloaded / not-found

    *** HOW THE TABLE NAME FROM CONFIGURATION IS MADE SAFE ***

    download.FileDownloader_Info stores ListTable and UpdateTable as table/view NAMES,
    and the standalone etl-tools app concatenates them straight into SQL text at run
    time. That cannot be ported as-is: this platform compiles no SQL from configuration,
    and a name taken from a row and pasted into a statement is an injection vector.

    The fix is NOT a hardcoded InfoId -> table mapping, which would mean a code change
    every time an operator adds a source and would throw away the data-driven design that
    is the best thing about the original. Instead each procedure below:

      1. resolves the configured name with OBJECT_ID(), rejecting anything that does not
         resolve to a real object;
      2. rejects any object outside the allowed schemas (download, vendor - the only two
         the 17 active sources use);
      3. rebuilds the identifier from OBJECT_SCHEMA_NAME()/OBJECT_NAME() of the RESOLVED
         id, wrapped in QUOTENAME - never from the raw configured string;
      4. passes row ids and flags to sp_executesql as PARAMETERS, never concatenated.

    So the only text interpolated is the canonical, quoted name of an object that is
    already known to exist in an allowed schema. A hostile value can fail to resolve, but
    it cannot become executable SQL.

    *** Mako is a large shared production database. Every statement is download.-qualified.
*/

USE Mako;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('download.usp_MergeFileDownloaderFile', 'P') IS NULL
    EXEC('CREATE PROCEDURE download.usp_MergeFileDownloaderFile AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE download.usp_MergeFileDownloaderFile
    @Records download.FileDownloaderFileTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /*
        De-duplicate within the batch before the MERGE.

        A batch should not contain one key twice, but "should not" is not a guarantee:
        work units run in parallel at MaxConcurrentWorkUnits, and a listing that returns
        the same remote file under two paths would produce two rows for one
        (SourceName, Mode, FileName). MERGE answers that with error 8672 - "attempted to
        UPDATE or DELETE the same row more than once" - which fails the whole batch. Keep
        the most recent fetch.
    */
    WITH Deduped AS
    (
        SELECT
            SourceName, [Mode], [FileName], DestinationPath, FileDate, SizeBytes,
            [Status], DownloadedAtUtc, InfoId, SourceLocator,
            rn = ROW_NUMBER() OVER (
                     PARTITION BY SourceName, [Mode], [FileName]
                     ORDER BY DownloadedAtUtc DESC)
        FROM @Records
    )
    MERGE download.FileDownloader_File AS target
    USING (SELECT * FROM Deduped WHERE rn = 1) AS source
        ON  target.SourceName = source.SourceName
        AND target.[Mode]     = source.[Mode]
        AND target.[FileName] = source.[FileName]

    WHEN MATCHED
         /*
             Only touch a row that actually changed, so ModifiedAtUtc tracks real change
             and a no-op re-run reports 0 rows processed. That is correct, not a bug -
             the same convention CWG's merge procs use.

             AlreadyPresent must NOT overwrite a Downloaded row's size and timestamp with
             a later skip: that would erase the record of when the file was actually
             fetched. Status only moves forward to Downloaded.
         */
         AND source.[Status] <> 'AlreadyPresent'
         AND (target.SizeBytes       <> source.SizeBytes
           OR target.[Status]        <> source.[Status]
           OR target.DestinationPath <> source.DestinationPath
           OR target.SourceLocator   <> source.SourceLocator
           OR ISNULL(target.FileDate, '1900-01-01') <> ISNULL(source.FileDate, '1900-01-01'))
    THEN UPDATE SET
            target.DestinationPath = source.DestinationPath,
            target.FileDate        = source.FileDate,
            target.SizeBytes       = source.SizeBytes,
            target.[Status]        = source.[Status],
            target.DownloadedAtUtc = source.DownloadedAtUtc,
            target.InfoId          = source.InfoId,
            target.SourceLocator   = source.SourceLocator,
            target.ModifiedAtUtc   = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceName, [Mode], [FileName], DestinationPath, FileDate, SizeBytes,
                [Status], DownloadedAtUtc, InfoId, SourceLocator, ModifiedAtUtc)
        VALUES (source.SourceName, source.[Mode], source.[FileName], source.DestinationPath,
                source.FileDate, source.SizeBytes, source.[Status], source.DownloadedAtUtc,
                source.InfoId, source.SourceLocator, SYSUTCDATETIME());

    -- SqlSinkBase reads this with ExecuteScalar (ProcedureReturnsRowCount = true).
    SELECT @@ROWCOUNT;
END
GO

/* ===========================================================================
   usp_GetFileDownloaderQueue - one source's pending downloads.

   Returns exactly three columns: Id, Value, FileName.

   *** Id/Value/FileName are the ONLY columns every queue view has. ***
   Verified 2026-11-06 against the live catalog: all 17 download.v_*_Not_Downloaded
   views expose those three at ordinals 1-3, but DateDownloaded exists on only 15 -
   v_ERCOT_Documents_Info_Not_Downloaded and V_GasQuest_Gulf_South_Document_Info_Not_Downloaded
   lack it, being the two whose update table lives in the vendor schema. Selecting
   DateDownloaded here would therefore fail for 2 of the 17 sources. It is not needed
   anyway: each view already filters to DateDownloaded IS NULL.

   Value is the ';'-delimited argument list for the source's BaseURL string.Format
   template; FileName is pre-composed by the view from FileDownloader_Info.DocumentName.
   =========================================================================== */
IF OBJECT_ID('download.usp_GetFileDownloaderQueue', 'P') IS NULL
    EXEC('CREATE PROCEDURE download.usp_GetFileDownloaderQueue AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE download.usp_GetFileDownloaderQueue
    @InfoId INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @configured NVARCHAR(300) =
        (SELECT ListTable FROM download.FileDownloader_Info WHERE Id = @InfoId AND IsActive = 1);

    IF @configured IS NULL
        THROW 50020, 'No active download.FileDownloader_Info row with that Id, or its ListTable is NULL.', 1;

    DECLARE @objectId INT = OBJECT_ID(@configured);
    IF @objectId IS NULL
        THROW 50021, 'FileDownloader_Info.ListTable does not resolve to an existing object.', 1;

    DECLARE @schema SYSNAME = OBJECT_SCHEMA_NAME(@objectId);
    IF @schema NOT IN ('download', 'vendor')
        THROW 50022, 'FileDownloader_Info.ListTable resolves outside the allowed schemas.', 1;

    -- Rebuilt from the RESOLVED id, not from @configured. See the header note.
    DECLARE @sql NVARCHAR(MAX) =
        N'SELECT Id, [Value], [FileName] FROM '
        + QUOTENAME(@schema) + N'.' + QUOTENAME(OBJECT_NAME(@objectId)) + N';';

    EXEC sys.sp_executesql @sql;
END
GO

/* ===========================================================================
   usp_StampFileDownloaderRow - mark one queue row as handled.

   This is what drains the queue: the v_*_Not_Downloaded views filter on
   DateDownloaded IS NULL, so a row that is fetched but never stamped is offered again
   on every run, forever.

   @IsNotFound = 1 records a legitimate absence (the report is not published for that
   date yet). The views also exclude ISNULL(IsNotFound,0) = 1, so that likewise stops
   the row being re-offered.

   ⚠ Unlike the standalone app, a row is stamped ONLY after the file is on disk. The
   original treats "file already exists" as downloaded and stamps DateDownloaded even
   though nothing was fetched that run; keeping that behaviour would make the stamp mean
   "someone once saw a file here", which is not what the column is read as.
   =========================================================================== */
IF OBJECT_ID('download.usp_StampFileDownloaderRow', 'P') IS NULL
    EXEC('CREATE PROCEDURE download.usp_StampFileDownloaderRow AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE download.usp_StampFileDownloaderRow
    @InfoId     INT,
    @RowId      INT,
    @IsNotFound BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @configured NVARCHAR(300) =
        (SELECT UpdateTable FROM download.FileDownloader_Info WHERE Id = @InfoId AND IsActive = 1);

    IF @configured IS NULL
        THROW 50023, 'No active download.FileDownloader_Info row with that Id, or its UpdateTable is NULL.', 1;

    DECLARE @objectId INT = OBJECT_ID(@configured);
    IF @objectId IS NULL
        THROW 50024, 'FileDownloader_Info.UpdateTable does not resolve to an existing object.', 1;

    DECLARE @schema SYSNAME = OBJECT_SCHEMA_NAME(@objectId);
    IF @schema NOT IN ('download', 'vendor')
        THROW 50025, 'FileDownloader_Info.UpdateTable resolves outside the allowed schemas.', 1;

    /*
        All four live update targets carry Id / DateDownloaded / IsNotFound
        (verified 2026-11-06: download.FileDownloader_Document,
        download.Vector_Planned_Outage_Document, vendor.ERCOT_Documents_Info,
        vendor.GasQuest_Gulf_South_Document_Info). Checked rather than assumed, so a
        newly configured target missing a column fails here with a clear message instead
        of inside dynamic SQL.
    */
    IF COL_LENGTH(QUOTENAME(@schema) + '.' + QUOTENAME(OBJECT_NAME(@objectId)), 'DateDownloaded') IS NULL
    OR COL_LENGTH(QUOTENAME(@schema) + '.' + QUOTENAME(OBJECT_NAME(@objectId)), 'IsNotFound') IS NULL
    OR COL_LENGTH(QUOTENAME(@schema) + '.' + QUOTENAME(OBJECT_NAME(@objectId)), 'Id') IS NULL
        THROW 50026, 'UpdateTable lacks one of Id / DateDownloaded / IsNotFound.', 1;

    DECLARE @sql NVARCHAR(MAX) =
        N'UPDATE ' + QUOTENAME(@schema) + N'.' + QUOTENAME(OBJECT_NAME(@objectId))
        + N' SET DateDownloaded = GETDATE(), IsNotFound = @pIsNotFound WHERE Id = @pRowId;';

    -- Row id and flag travel as PARAMETERS, never concatenated.
    EXEC sys.sp_executesql @sql,
         N'@pRowId INT, @pIsNotFound BIT',
         @pRowId = @RowId, @pIsNotFound = @IsNotFound;

    SELECT @@ROWCOUNT;
END
GO
