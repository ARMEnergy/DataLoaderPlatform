/*
    FileDownloader loader - 003 - merge procedure.

    Target database: Mako.

    One procedure: merges the manifest produced by a run into
    download.FileDownloader_File.

    NOT IN SCOPE HERE (next increment): stamping DateDownloaded / IsNotFound back onto
    the legacy per-source update tables for Link mode. The standalone app does that by
    injecting download.FileDownloader_Info.UpdateTable straight into SQL text at run time.
    That cannot be ported as-is - this platform compiles no SQL from configuration, and a
    table name taken from a row and concatenated into a statement is an injection vector.
    The replacement is a dispatch procedure holding the InfoId -> table mapping as static
    literals, so the name stays configuration while the statement stays fixed.

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
