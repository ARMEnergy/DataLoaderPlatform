/*
    FileDownloader loader - 002 - table-valued parameter types.

    Target database: Mako.

    *** TVP CONTRACT: the column NAME, ORDER and TYPE here must match
    *** FileDownloaderFileSink.BuildTable() exactly. SQL Server binds a TVP positionally;
    *** a reordered column is NOT an error, it is a silent mis-write (a path landing in
    *** the status column, a size landing in a date). Change one side and you must change
    *** the other in the same commit.

    Creation-guarded, like every other 002 in this repo: re-running over an existing type
    is a no-op, NOT an upgrade. A type cannot be ALTERed while a procedure references it,
    so a shape change means 999 (drop) then 001-003 again.
*/

USE Mako;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF TYPE_ID('download.FileDownloaderFileTvp') IS NULL
BEGIN
    CREATE TYPE download.FileDownloaderFileTvp AS TABLE
    (
        --  1
        SourceName      VARCHAR(200)   NOT NULL,
        --  2
        [Mode]          VARCHAR(20)    NOT NULL,
        --  3
        [FileName]      NVARCHAR(260)  NOT NULL,
        --  4
        DestinationPath NVARCHAR(400)  NOT NULL,
        --  5
        FileDate        DATE           NULL,
        --  6
        SizeBytes       BIGINT         NOT NULL,
        --  7
        [Status]        VARCHAR(20)    NOT NULL,
        --  8
        DownloadedAtUtc DATETIME2(3)   NOT NULL,
        --  9
        InfoId          INT            NULL,
        -- 10
        SourceLocator   NVARCHAR(1000) NOT NULL
    );
END
GO
