-- =============================================================================
-- 001_CreateCsvExampleSchema.sql
-- Demo schema for the CSV-drop loader. Lives in its own database
-- (Loaders:CsvExample:ConnectionString).
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'csv')
    EXEC ('CREATE SCHEMA csv');
GO

IF OBJECT_ID('csv.RawRows', 'U') IS NULL
BEGIN
    CREATE TABLE csv.RawRows
    (
        RawRowId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RawRows PRIMARY KEY,
        SourceFile    NVARCHAR(500) NOT NULL,
        RowNumber     INT           NOT NULL,
        RawLine       NVARCHAR(MAX) NULL,
        IngestedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_RawRows_Ingested DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_RawRows_SourceFile ON csv.RawRows(SourceFile, RowNumber);
END
GO

IF TYPE_ID('csv.RawRowTvp') IS NULL
BEGIN
    CREATE TYPE csv.RawRowTvp AS TABLE
    (
        SourceFile NVARCHAR(500) NULL,
        RowNumber  INT           NOT NULL,
        RawLine    NVARCHAR(MAX) NULL
    );
END
GO

IF OBJECT_ID('csv.usp_BulkInsertRawRows', 'P') IS NOT NULL
    DROP PROCEDURE csv.usp_BulkInsertRawRows;
GO
CREATE PROCEDURE csv.usp_BulkInsertRawRows
    @Records csv.RawRowTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO csv.RawRows (SourceFile, RowNumber, RawLine)
    SELECT SourceFile, RowNumber, RawLine FROM @Records;
    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO
