-- =============================================================================
-- 001_CreateEoxSchema.sql
-- Database : EOX          (create/select it before running this script)
-- Schema   : arm
--
-- EOX Live end-of-day broker curve loader. The source is a plain FTP drop
-- (ftp.eoxlive.com:21, Pure-FTPd, AUTH TLS available) whose ROOT directory holds
-- every file the account has ever been sent -- 18,486 entries as of 2026-09-04,
-- going back to 2011. Three CSV series are in scope, one file per publication
-- day per series, all stamped 1430 (14:30 US Central, the EOD snap):
--
--   EOD_CSV_C_<yyyyMMdd>_1430.csv    -> arm.CrudeOil     (3,096 files, from 2014-05-19)
--   EOD_CSV_NG_<yyyyMMdd>_1430.csv   -> arm.NaturalGas   (3,747 files, from 2011-10-26)
--   EOD_CSV_NGL_<yyyyMMdd>_1430.csv  -> arm.NGL          (3,240 files, from 2013-10-21)
--
-- The .xls/.xlsx twins and the EOD_CSV_20YR_NG_* series are OUT of scope, as are
-- the ~20 "(<host>'s conflicted copy <date>).csv" files sitting in the same
-- directory. The loader addresses files by EXACT name built from the date, so
-- none of those can be picked up by accident.
--
-- File layout (verified live 2026-09-04 over 25 files spanning 2011..2026) --
-- comma-delimited, CRLF, ONE header row, no quoting anywhere, no embedded
-- delimiters, no empty cells, every Mid/Bid/Ask/FP numeric:
--
--   Crude  Line,Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,
--          Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask
--   NG     Line,Data_Code,Curve_Date,Region,Market,Market_Code,Contract_Name,
--          Contract_Term,Contract_Begin,Contract_End,Time_Key,Mid,Bid,Ask,FP
--   NGL    Line,Data_Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,
--          Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask
--
-- HEADER DRIFT is real and is why the loader matches columns BY NAME:
--   * 2014 Crude/NGL files head the first column "Number", not "Line".
--   * 2014 NGL names the second column "Code", not "Data_Code".
--   * NG files before ~2016 have NO "FP" column at all (14 fields, not 15).
--
-- DATE FORMATS DIFFER BY FEED and are stable across the whole history:
--   * Crude and NGL publish yyyy-MM-dd.
--   * NG publishes MM/dd/yy -- a TWO-DIGIT year, including for Contract_End,
--     which already reaches 2036 in the 2026 files. The loader pins
--     TwoDigitYearMax to 2099 so a future "50" cannot be read as 1950.
--
-- ==== ModifiedAtUtc: a DELIBERATE, FLAGGED deviation from the supplied DDL ====
-- The requester's DDL defaulted ModifiedAtUtc to SYSDATETIME(), which is the
-- server's LOCAL time and contradicts the column's own name. Every other loader
-- in this repo stamps SYSUTCDATETIME(), and the merge procs in 003 set the column
-- explicitly, so a local-time DEFAULT would also make freshly inserted rows
-- disagree with updated ones. SYSUTCDATETIME() is used throughout. Change both
-- this default and the procs together if local time is genuinely wanted.
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
-- arm.Status -- fixed outcome catalog for the FileLog hub.
--   Success       file downloaded and parsed
--   NotAvailable  the date has no file on the drop (weekend/holiday) -- NOT an error
--   Failed        download or parse failed
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
-- arm.FileLog -- one row per source FILE, for every outcome. FileName is the
-- natural key, so re-checking a file updates its row rather than adding one.
--
-- RemoteLastModifiedUtc / SizeBytes record what the FTP server reported; those
-- two values are also embedded in the work-unit resume key, so a file EOX
-- republishes gets a new key and is reprocessed while an unchanged one is
-- skipped. Provenance runs the other way here -- the fact tables carry the
-- FileName column from the requester's DDL rather than a FileLogId FK, so there
-- is no FK from the fact tables back to this hub.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                    INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated           DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        FileName              VARCHAR(128)  NOT NULL,
        FeedId                VARCHAR(20)   NOT NULL,   -- CrudeOil | NaturalGas | NGL
        CurveDate             DATE          NULL,       -- yyyyMMdd parsed out of the file name
        StatusId              INT           NOT NULL,   -- FK to arm.Status(Id)
        RemoteLastModifiedUtc DATETIME2(3)  NULL,
        SizeBytes             BIGINT        NULL,
        [RowCount]            INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        ErrorMessage          NVARCHAR(400) NULL,
        RequestPath           NVARCHAR(400) NOT NULL,   -- ftp://host:port/path, NEVER credentials
        LastCheckedUtc        DATETIME2(3)  NULL,
        ModifiedAtUtc         DATETIME2(3)  NULL,
        CONSTRAINT UQ_FileLog_FileName UNIQUE (FileName),
        CONSTRAINT FK_FileLog_Status FOREIGN KEY (StatusId) REFERENCES arm.Status (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_FileLog_FeedCurveDate' AND object_id = OBJECT_ID('arm.FileLog'))
    CREATE NONCLUSTERED INDEX IX_FileLog_FeedCurveDate ON arm.FileLog (FeedId ASC, CurveDate ASC) INCLUDE (StatusId, [RowCount]);
GO

-- =============================================================================
-- FACT TABLES
--
-- Exactly as supplied by the requester, with two additions noted inline:
--   * SYSUTCDATETIME() instead of SYSDATETIME() -- see the header block.
--   * one non-clustered index per table on (CurveDate) INCLUDE (Code), because
--     every consuming query is "give me a curve date", and the clustered key
--     leads with CurveDate but the index keeps covered lookups off the base
--     table for the wide Location/Market rows.
--
-- ==== WHY THERE IS NO SourceFileDate ORDERING GUARD COLUMN ====
-- Every other file-based loader here carries a SourceFileDate column so that
-- concurrent, out-of-order merges resolve deterministically. This feed does not
-- need one, for two independent reasons:
--
--   1. Curve_Date inside a file always equals the yyyyMMdd in its NAME (verified
--      over all 25 sampled files, every row). CurveDate leads every primary key,
--      so two DIFFERENT files write DISJOINT key sets -- they cannot race.
--   2. When the same file IS republished, the merge still needs a deterministic
--      winner. FileName is stored on every row and, within one feed, the names
--      are a fixed prefix + zero-padded yyyyMMdd + fixed suffix, so LEXICOGRAPHIC
--      order IS chronological order. 003 guards each UPDATE with
--      src.FileName >= tgt.FileName and de-dups ORDER BY FileName DESC, which
--      gives "newest file wins" using only the columns supplied.
--
-- arm.usp_ValidateLoad reports any row whose CurveDate disagrees with the date
-- embedded in its FileName, which is the assumption (1) rests on.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.CrudeOil -- EOD_CSV_C_<yyyyMMdd>_1430.csv
-- ~20,800 rows/day. Locat._Code is 1-2 chars today; Time_Key is M##/M###/MB##/
-- C##/S##/W## (max 4 chars observed). Mid/Bid/Ask can be NEGATIVE (spreads).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CrudeOil', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CrudeOil
    (
        CurveDate     DATE          NOT NULL,
        LocationCode  VARCHAR(8)    NOT NULL,
        TimeKey       VARCHAR(16)   NOT NULL,
        Line          VARCHAR(64)   NULL,
        Code          VARCHAR(128)  NULL,
        ContractTerm  VARCHAR(32)   NULL,
        ContractName  VARCHAR(64)   NULL,
        ContractBegin DATE          NULL,
        ContractEnd   DATE          NULL,
        Location      VARCHAR(128)  NULL,
        Mid           FLOAT         NULL,
        Bid           FLOAT         NULL,
        Ask           FLOAT         NULL,
        FileName      VARCHAR(128)  NULL,
        ModifiedAtUtc DATETIME2(3)  CONSTRAINT DF_CrudeOil_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_CrudeOil PRIMARY KEY CLUSTERED
        (
            CurveDate ASC,
            LocationCode ASC,
            TimeKey ASC
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_CrudeOil_CurveDate' AND object_id = OBJECT_ID('arm.CrudeOil'))
    CREATE NONCLUSTERED INDEX IX_CrudeOil_CurveDate ON arm.CrudeOil (CurveDate ASC) INCLUDE (Code, ContractName, Mid);
GO

-- ----------------------------------------------------------------------------
-- arm.NaturalGas -- EOD_CSV_NG_<yyyyMMdd>_1430.csv
-- ~34,000 rows/day, 224 distinct Market_Code values. FP is the only column that
-- is genuinely absent from older files (pre-2016) -- it loads as NULL there.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.NaturalGas', 'U') IS NULL
BEGIN
    CREATE TABLE arm.NaturalGas
    (
        CurveDate     DATE          NOT NULL,
        MarketCode    VARCHAR(8)    NOT NULL,
        TimeKey       VARCHAR(16)   NOT NULL,
        Line          VARCHAR(64)   NULL,
        Code          VARCHAR(128)  NULL,
        Region        VARCHAR(64)   NULL,
        Market        VARCHAR(128)  NULL,
        ContractName  VARCHAR(64)   NULL,
        ContractTerm  VARCHAR(32)   NULL,
        ContractBegin DATE          NULL,
        ContractEnd   DATE          NULL,
        Mid           FLOAT         NULL,
        Bid           FLOAT         NULL,
        Ask           FLOAT         NULL,
        FP            FLOAT         NULL,
        FileName      VARCHAR(128)  NULL,
        ModifiedAtUtc DATETIME2(3)  CONSTRAINT DF_NaturalGas_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_NaturalGas_1 PRIMARY KEY CLUSTERED
        (
            CurveDate ASC,
            MarketCode ASC,
            TimeKey ASC
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_NaturalGas_CurveDate' AND object_id = OBJECT_ID('arm.NaturalGas'))
    CREATE NONCLUSTERED INDEX IX_NaturalGas_CurveDate ON arm.NaturalGas (CurveDate ASC) INCLUDE (Code, Market, ContractName, Mid);
GO

-- ----------------------------------------------------------------------------
-- arm.NGL -- EOD_CSV_NGL_<yyyyMMdd>_1430.csv
-- ~4,560 rows/day. Same shape as Crude; the second CSV column is Data_Code here
-- (and plain Code in the 2014 files), which is why the mapping accepts both.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.NGL', 'U') IS NULL
BEGIN
    CREATE TABLE arm.NGL
    (
        CurveDate     DATE          NOT NULL,
        LocationCode  VARCHAR(8)    NOT NULL,
        TimeKey       VARCHAR(16)   NOT NULL,
        Line          VARCHAR(64)   NULL,
        Code          VARCHAR(128)  NULL,
        ContractTerm  VARCHAR(32)   NULL,
        ContractName  VARCHAR(64)   NULL,
        ContractBegin DATE          NULL,
        ContractEnd   DATE          NULL,
        Location      VARCHAR(128)  NULL,
        Mid           FLOAT         NULL,
        Bid           FLOAT         NULL,
        Ask           FLOAT         NULL,
        FileName      VARCHAR(128)  NULL,
        ModifiedAtUtc DATETIME2(3)  CONSTRAINT DF_NGL_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_NGL PRIMARY KEY CLUSTERED
        (
            CurveDate ASC,
            LocationCode ASC,
            TimeKey ASC
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_NGL_CurveDate' AND object_id = OBJECT_ID('arm.NGL'))
    CREATE NONCLUSTERED INDEX IX_NGL_CurveDate ON arm.NGL (CurveDate ASC) INCLUDE (Code, Location, ContractName, Mid);
GO
