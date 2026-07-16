-- =============================================================================
-- 001_CreateFtpExampleSchema.sql
-- Demo schema for the FTP feed loader. Lives in its own database
-- (Loaders:FtpExample:ConnectionString).
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'ftp')
    EXEC ('CREATE SCHEMA ftp');
GO

IF OBJECT_ID('ftp.RawRows', 'U') IS NULL
BEGIN
    CREATE TABLE ftp.RawRows
    (
        RawRowId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FtpRawRows PRIMARY KEY,
        SourceFile    NVARCHAR(500) NOT NULL,
        IngestedAtUtc DATETIME2(3)  NOT NULL,
        RowNumber     INT           NOT NULL,
        RawLine       NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_FtpRawRows_SourceFile ON ftp.RawRows(SourceFile, RowNumber);
END
GO

IF TYPE_ID('ftp.RawRowTvp') IS NULL
BEGIN
    CREATE TYPE ftp.RawRowTvp AS TABLE
    (
        SourceFile    NVARCHAR(500) NULL,
        IngestedAtUtc DATETIME2(3)  NOT NULL,
        RowNumber     INT           NOT NULL,
        RawLine       NVARCHAR(MAX) NULL
    );
END
GO

IF OBJECT_ID('ftp.usp_BulkInsertRawRows', 'P') IS NOT NULL
    DROP PROCEDURE ftp.usp_BulkInsertRawRows;
GO
CREATE PROCEDURE ftp.usp_BulkInsertRawRows
    @Records ftp.RawRowTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO ftp.RawRows (SourceFile, IngestedAtUtc, RowNumber, RawLine)
    SELECT SourceFile, IngestedAtUtc, RowNumber, RawLine FROM @Records;
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO
