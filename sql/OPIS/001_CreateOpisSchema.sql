-- =============================================================================
-- 001_CreateOpisSchema.sql
-- Database : OPIS          (create/select it before running this script)
-- Schema   : arm
--
-- OPIS "LP" daily price report loader. The source is a plain FTP drop
-- (ftp.opisnet.com:21) holding roughly one month of rolling daily CSV files
-- named <yyyyMMdd>LP.csv, one file per publication day.
--
-- File layout (verified against the live feed 2026-08-21) — comma-delimited,
-- CRLF, ONE header row, ten fields, values space-padded to fixed widths:
--
--   Price,Mkt_Prod,Date,Low,High,Avg,Country,Unit,Timing,Freq
--   I,LOS ANGELES PRO     ,08/20/26, 85.8750, 89.8750, 87.8750,US,GAL,A,D
--
--   * Price      record-status code: 'I' = initial, 'U' = updated/revision.
--   * Mkt_Prod   market+product label, RIGHT-SPACE-PADDED (trim on load).
--   * Date       MM/dd/yy — TWO-DIGIT year.
--   * Low/High   may be EMPTY (basket rows carry only Avg) -> NULL.
--   * Avg        always present in the observed month, still modelled NULL-able.
--
-- Two fact tables, per the loader spec:
--   arm.LPReport         PK (Mkt_Prod, [Date], Timing)          — current value
--   arm.LPReportHistory  PK (Mkt_Prod, [Date], Timing, Price)   — price history
--
-- Why the history table's extra Price key column is load-bearing: OPIS
-- republishes a prior day's row under Price='U' when it revises it. Observed
-- live for SARNIA PRO / 07/30/26 / Timing='O':
--       20260730LP.csv   I   Low 81.2500  High 81.5000  Avg 81.3750
--       20260803LP.csv   U   Low 81.2500  High 82.0000  Avg 81.6250
-- arm.LPReport keeps only the superseding 'U' row; arm.LPReportHistory keeps
-- both, which is the price-change history.
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- =============================================================================
-- LOOKUPS
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Status — fixed outcome catalog for the FileLog hub.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Status', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Status
    (
        Id          INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Status PRIMARY KEY,
        DateCreated DATETIME     NOT NULL CONSTRAINT DF_Status_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20)  NOT NULL,
        CONSTRAINT UQ_Status_Name UNIQUE ([Name])
    );
END
GO

INSERT arm.Status ([Name])
SELECT v.[Name]
FROM (VALUES ('Success'), ('NotAvailable'), ('Failed')) AS v([Name])
WHERE NOT EXISTS (SELECT 1 FROM arm.Status AS s WHERE s.[Name] = v.[Name]);
GO

-- =============================================================================
-- AUDIT HUB
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.FileLog — one row per source FILE (the natural unit of this feed), for
-- every outcome. FileName is the natural key, so a re-download of the same file
-- updates its hub row rather than adding one. RemoteLastModifiedUtc/SizeBytes
-- record what the FTP server reported, which is also what the work-unit resume
-- key embeds (a changed file gets a new key and is reprocessed).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                    INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated           DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        FileName              VARCHAR(200)  NOT NULL,
        SourceFileDate        DATE          NULL,      -- yyyyMMdd parsed out of the file name
        StatusId              INT           NOT NULL,  -- FK to arm.Status(Id)
        RemoteLastModifiedUtc DATETIME2(3)  NULL,
        SizeBytes             BIGINT        NULL,
        [RowCount]            INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        ErrorMessage          NVARCHAR(400) NULL,
        RequestPath           NVARCHAR(400) NOT NULL,  -- ftp path, NEVER credentials
        LastCheckedUtc        DATETIME2(3)  NULL,
        ModifiedAtUtc         DATETIME2(3)  NULL,
        CONSTRAINT UQ_FileLog_FileName UNIQUE (FileName),
        CONSTRAINT FK_FileLog_Status FOREIGN KEY (StatusId) REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- FACT TABLES
--
-- Both carry the same payload; they differ only in grain (their PK). Column
-- names deliberately mirror the CSV header so the mapping stays obvious.
-- [Date] is bracketed throughout (it collides with the type name).
--
-- SourceFileDate is not part of either key — it is the ORDERING GUARD. Work
-- units run concurrently, so files can merge out of order; the merge procs only
-- overwrite when the incoming row is from a file at least as new as the one
-- already stored. That makes the result independent of arrival order and of
-- how often the loader re-runs.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.LPReport — current value per market/product, report date and timing.
-- A Price='U' revision supersedes the 'I' row it revises.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.LPReport', 'U') IS NULL
BEGIN
    CREATE TABLE arm.LPReport
    (
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_LPReport_DateCreated DEFAULT GETDATE(),
        Mkt_Prod       VARCHAR(50)   NOT NULL,
        [Date]         DATE          NOT NULL,
        Timing         VARCHAR(10)   NOT NULL,
        Price          VARCHAR(10)   NOT NULL,
        [Low]          DECIMAL(12,4) NULL,
        [High]         DECIMAL(12,4) NULL,
        [Avg]          DECIMAL(12,4) NULL,
        Country        VARCHAR(10)   NULL,
        [Unit]         VARCHAR(10)   NULL,
        Freq           VARCHAR(10)   NULL,
        SourceFileDate DATE          NOT NULL,
        FileLogId      INT           NOT NULL,
        ModifiedAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_LPReport_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LPReport PRIMARY KEY CLUSTERED (Mkt_Prod ASC, [Date] ASC, Timing ASC),
        CONSTRAINT FK_LPReport_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_LPReport_Date' AND object_id = OBJECT_ID('arm.LPReport'))
    CREATE NONCLUSTERED INDEX IX_LPReport_Date ON arm.LPReport ([Date] ASC) INCLUDE (Mkt_Prod, Timing);
GO

-- ----------------------------------------------------------------------------
-- arm.LPReportHistory — one row per (market/product, report date, timing,
-- price-status code), so an 'I' quote and its later 'U' revision both survive.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.LPReportHistory', 'U') IS NULL
BEGIN
    CREATE TABLE arm.LPReportHistory
    (
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_LPReportHistory_DateCreated DEFAULT GETDATE(),
        Mkt_Prod       VARCHAR(50)   NOT NULL,
        [Date]         DATE          NOT NULL,
        Timing         VARCHAR(10)   NOT NULL,
        Price          VARCHAR(10)   NOT NULL,
        [Low]          DECIMAL(12,4) NULL,
        [High]         DECIMAL(12,4) NULL,
        [Avg]          DECIMAL(12,4) NULL,
        Country        VARCHAR(10)   NULL,
        [Unit]         VARCHAR(10)   NULL,
        Freq           VARCHAR(10)   NULL,
        SourceFileDate DATE          NOT NULL,
        FileLogId      INT           NOT NULL,
        ModifiedAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_LPReportHistory_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LPReportHistory PRIMARY KEY CLUSTERED (Mkt_Prod ASC, [Date] ASC, Timing ASC, Price ASC),
        CONSTRAINT FK_LPReportHistory_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_LPReportHistory_Date' AND object_id = OBJECT_ID('arm.LPReportHistory'))
    CREATE NONCLUSTERED INDEX IX_LPReportHistory_Date ON arm.LPReportHistory ([Date] ASC) INCLUDE (Mkt_Prod, Timing, Price);
GO
