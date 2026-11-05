/*
    FileDownloader loader - 001 - tables.

    Target database: Mako (Loaders:FileDownloader:ConnectionString -> Database=Mako).

    SCOPE: this script creates exactly ONE object, download.FileDownloader_File.

    It deliberately does NOT create, alter or drop any of the objects the standalone
    etl-tools/FileDownloader app already uses, all of which keep working unchanged:

        download.FileDownloader_Info              17 source definitions (config)
        download.v_*_Not_Downloaded               15 work-queue views
        download.FileDownloader_Document          4,373-row manifest for Link mode
        download.<Source>_*_Document              per-source document tables
        download.FileDownloader_Info_Error_Log    error log
        download.usp_Sync_Download_Links          seeds the work queue
        download.usp_FileDownloader_Info_Error_Log

    WHY A NEW TABLE RATHER THAN REUSING download.FileDownloader_Document:
      1. Three of the four acquisition modes (Browser/Sftp/Ftp) write NOTHING to SQL
         today, so there is no existing row to extend - that absence is exactly why a
         two-month Click-mode outage went unnoticed.
      2. FileDownloader_Document's unique key is (FD_Info_Id, FileDate): one row per
         source per DATE. Listing-driven feeds do not fit that shape - Platts pulls
         'Pipeline Flows_2021_May.csv', which has no meaningful FileDate, and any source
         publishing two files for one date would collide. Widening that key would mean
         altering a live table that Link mode depends on.

    *** Mako is a large shared production database (43 schemas, incl. vendor/, staging/,
    *** fundamentals/, conduit/). Every statement below is explicitly download.-qualified
    *** and must stay that way.
*/

USE Mako;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID('download') IS NULL
    THROW 50001, 'Schema [download] does not exist in this database - check you are connected to Mako.', 1;
GO

/*
    The manifest. One row per (SourceName, Mode, FileName) - what was fetched, how big it
    was, and when. Written by every mode, so core.LoadLog and the platform watchdog can
    see all four.
*/
IF OBJECT_ID('download.FileDownloader_File', 'U') IS NULL
BEGIN
    CREATE TABLE download.FileDownloader_File
    (
        FileId          INT            IDENTITY(1,1) NOT NULL,

        -- Logical source: 'Platts', 'Argus', 'ERCOT Documents', ...
        SourceName      VARCHAR(200)   NOT NULL,

        -- 'Link' | 'Browser' | 'Sftp' | 'Ftp'. The NAME, not the original app's argv
        -- number, which means nothing to a reader of this table.
        [Mode]          VARCHAR(20)    NOT NULL,

        [FileName]      NVARCHAR(260)  NOT NULL,
        DestinationPath NVARCHAR(400)  NOT NULL,

        -- Publication date where the source has one; NULL for listing-driven feeds.
        FileDate        DATE           NULL,

        SizeBytes       BIGINT         NOT NULL,

        -- 'Downloaded' | 'AlreadyPresent' | 'NotFound'. NotFound is a legitimate empty
        -- read (the report is not published yet), not a failure - it mirrors the original
        -- app's IsNotFound flag and must never alert.
        [Status]        VARCHAR(20)    NOT NULL,

        DownloadedAtUtc DATETIME2(3)   NOT NULL,

        -- download.FileDownloader_Info.Id for Link rows; NULL otherwise. Intentionally NOT
        -- a FOREIGN KEY: the three non-Link modes have no Info row, and a FK here would
        -- make this table fail whenever a source is retired from the catalogue.
        InfoId          INT            NULL,

        -- Scheme/host/path only. The loader strips query strings before writing, because
        -- Link URLs are built from an operator-supplied template and a vendor that
        -- authenticates by query string would otherwise land a live key in this column.
        SourceLocator   NVARCHAR(1000) NOT NULL,

        ModifiedAtUtc   DATETIME2(3)   NOT NULL
            CONSTRAINT DF_FileDownloader_File_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_FileDownloader_File PRIMARY KEY CLUSTERED (FileId)
    );

    /*
        Merge key. (SourceName, Mode, FileName) = 1,020 bytes, inside the 1,700-byte
        nonclustered index key limit. DestinationPath is deliberately NOT in the key: it
        is a function of the source, and including it would push the key to ~1,820 bytes
        and fail to create.
    */
    CREATE UNIQUE NONCLUSTERED INDEX UX_FileDownloader_File_Identity
        ON download.FileDownloader_File (SourceName, [Mode], [FileName]);

    -- Supports the watchdog/freshness question this table exists to answer:
    -- "what has this source fetched lately?"
    CREATE NONCLUSTERED INDEX IX_FileDownloader_File_Source_Downloaded
        ON download.FileDownloader_File (SourceName, DownloadedAtUtc DESC);
END
GO
