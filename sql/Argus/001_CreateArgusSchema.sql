-- =============================================================================
-- 001_CreateArgusSchema.sql
-- Database : Argus          (create/select it before running this script)
-- Schema   : dlp
--
-- Argus Media daily FTP drop (ftp.argusmedia.com:21, plain FTP).
-- Source contract verified live 2026-08-27 — see docs/apis/Argus.md.
-- Loader design — see docs/design/Argus.md.
--
-- Two feeds:
--   DOCUMENTATION/latest*.csv       15 full-snapshot reference files
--   DCRDEUS/<yyyyMMdd><sfx>.csv     daily price time series
--
-- The nine tables specified by the requester are reproduced VERBATIM — column
-- names, types, nullability, primary keys, default-constraint names and the four
-- TimeSeriesDetail indexes — with exactly one approved addition:
--     dlp.CodeLookup.Specification   (latestCodes.csv publishes a 6th column)
--
-- Two oddities in the supplied DDL are kept ON PURPOSE rather than silently
-- "fixed" (design §2.1):
--   * ModifiedAtUtc DEFAULT is sysdatetime() — server LOCAL time in a column
--     named ...Utc. Every row this loader writes gets SYSUTCDATETIME() set
--     explicitly by the merge procs, so the DEFAULT only affects rows inserted
--     by something else.
--   * dlp.TimeSeriesDetail.RecordStatus is NULL-able while the History table's
--     is a NOT NULL PK component. The source always populates it.
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'dlp')
    EXEC ('CREATE SCHEMA dlp AUTHORIZATION dbo;');
GO

-- =============================================================================
-- AUDIT HUB
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.Status — fixed outcome catalog for the FileLog hub.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.Status', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.Status
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_DLP_Status PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Status_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20) NOT NULL,
        CONSTRAINT UQ_DLP_Status_Name UNIQUE ([Name])
    );
END
GO

INSERT dlp.Status ([Name])
SELECT v.[Name]
FROM (VALUES ('Success'), ('NotAvailable'), ('Failed')) AS v([Name])
WHERE NOT EXISTS (SELECT 1 FROM dlp.Status AS s WHERE s.[Name] = v.[Name]);
GO

-- ----------------------------------------------------------------------------
-- dlp.FileLog — one row per source FILE, for every outcome (Success /
-- NotAvailable / Failed). FileName is the natural key, so a re-download of the
-- same file updates its hub row rather than adding one.
--
-- RemoteLastModifiedUtc + SizeBytes are what the FTP server reported, and are
-- what the work-unit resume key embeds: a changed file gets a new key and is
-- reprocessed (design §3.1).
--
-- RequestPath stores ftp://host:port/path and NEVER credentials.
--
-- The fact tables carry no FileLogId — the supplied DDL has none and one was not
-- added. Provenance is dlp.TimeSeriesDetail.SourcePath, which contains the file
-- name and therefore joins back to here.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.FileLog
    (
        Id                    INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_DLP_FileLog PRIMARY KEY,
        DateCreated           DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        FileName              VARCHAR(200)  NOT NULL,
        FeedId                VARCHAR(50)   NULL,      -- descriptor id, e.g. 'Codes' / 'TimeSeries'
        RemoteFolder          VARCHAR(100)  NULL,      -- 'DOCUMENTATION' | 'DCRDEUS'
        SourceFileDate        DATE          NULL,      -- yyyyMMdd from the name (DCRDEUS only)
        StatusId              INT           NOT NULL,
        RemoteLastModifiedUtc DATETIME2(3)  NULL,
        SizeBytes             BIGINT        NULL,
        [RowCount]            INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        ErrorMessage          NVARCHAR(400) NULL,
        RequestPath           NVARCHAR(400) NOT NULL,
        LastCheckedUtc        DATETIME2(3)  NULL,
        ModifiedAtUtc         DATETIME2(3)  NULL,
        CONSTRAINT UQ_DLP_FileLog_FileName UNIQUE (FileName),
        CONSTRAINT FK_DLP_FileLog_Status FOREIGN KEY (StatusId) REFERENCES dlp.Status (Id)
    );
END
GO

-- =============================================================================
-- REFERENCE TABLES — as specified by the requester (VERBATIM)
--
-- Source: DOCUMENTATION/latest*.csv, full snapshots republished daily.
-- Loaded by MERGE on the primary key. There is deliberately no
-- "WHEN NOT MATCHED BY SOURCE THEN DELETE": a short download would otherwise
-- wipe good rows (design §4.1).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.CategoryLookup  <- latestCategory.csv  (Code,DisplayName,Category)
--
-- NOTE the source publishes its columns in the order Code, DisplayName, Category
-- while this table is (Code, Category, DisplayName). The loader maps by header
-- NAME; a position-based load would swap the two 500-char columns and corrupt
-- the primary key.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.CategoryLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.CategoryLookup
    (
        Code          VARCHAR(50)  NOT NULL,
        Category      VARCHAR(500) NOT NULL,
        DisplayName   VARCHAR(500) NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_CategoryType_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_CategoryType PRIMARY KEY CLUSTERED (Code, Category)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.CodeLookup  <- latestCodes.csv
--     (Code,DisplayName,DeliveryMode,Unit,Frequency,Specification)
--
-- Specification is the one column added to the supplied DDL: the source
-- publishes it (max observed width 33) and it would otherwise be discarded.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.CodeLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.CodeLookup
    (
        Code          VARCHAR(50)   NOT NULL,
        DisplayName   VARCHAR(2000) NULL,
        DeliveryMode  VARCHAR(50)   NULL,
        [Unit]        VARCHAR(50)   NULL,
        Frequency     VARCHAR(50)   NULL,
        Specification VARCHAR(50)   NULL,
        ModifiedAtUtc DATETIME2(3)  NULL
            CONSTRAINT DF_Codes_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_Codes PRIMARY KEY CLUSTERED (Code)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.ModuleDetailLookup  <- latestModuleDetails.csv
--     (Module,Code,TimeStampID,PriceTypeID,ContinuousForwardPeriod,
--      StartDateInModule,EndDateInModule)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.ModuleDetailLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.ModuleDetailLookup
    (
        Module                  VARCHAR(50)  NOT NULL,
        Code                    VARCHAR(50)  NOT NULL,
        TimestampTypeID         SMALLINT     NOT NULL,
        PriceTypeID             SMALLINT     NOT NULL,
        ContinuousForwardPeriod SMALLINT     NOT NULL,
        StartDate               DATE         NULL,
        EndDate                 DATE         NULL,
        ModifiedAtUtc           DATETIME2(3) NULL
            CONSTRAINT DF_ModuleDetailLookup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_ModuleDetailLookup PRIMARY KEY CLUSTERED
        (
            Module, Code, TimestampTypeID, PriceTypeID, ContinuousForwardPeriod
        )
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.ModuleLookup  <- latestModules.csv
--     (Module,Path,FileName,Description,Folder,Time,LocalTime,LocalTimeZone)
--
-- This is the table that maps a DCRDEUS file-name suffix to a Module value:
--     DHC  / DATA\DCRDEUS / dhc  / Argus US crude
--     DHCA / DATA\DCRDEUS / dhca / Argus US crude - 17:00 section
-- FileName is NOT simply LOWER(Module) in general (124 of 200 modules differ),
-- which is why the loader carries an explicit suffix map — see design §3.3.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.ModuleLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.ModuleLookup
    (
        Module        VARCHAR(50)  NOT NULL,
        [Path]        VARCHAR(250) NULL,
        FileName      VARCHAR(50)  NULL,
        [Description] VARCHAR(500) NULL,
        Folder        VARCHAR(250) NULL,
        [Time]        VARCHAR(50)  NULL,
        LocalTime     TIME(0)      NULL,
        LocalTimeZone VARCHAR(250) NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_ModuleLookup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_ModuleLookup PRIMARY KEY CLUSTERED (Module)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.PriceTypeLookup  <- latestPricetype.csv  (PriceTypeID,Description)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.PriceTypeLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.PriceTypeLookup
    (
        PriceTypeID   SMALLINT     NOT NULL,
        [Description] VARCHAR(250) NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_PriceType_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_PriceTypeLookup PRIMARY KEY CLUSTERED (PriceTypeID)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.QuoteLookup  <- latestQuotes.csv
--
-- NO PRIMARY KEY and every column NULL-able, exactly as specified. This table is
-- the loader's only FULL REPLACE: dlp.usp_ReplaceQuoteLookup deletes and
-- reinserts in one transaction so the table mirrors the snapshot including
-- removals (design §4.2).
--
-- For the record, the natural key verified live over all 150 752 rows is
--     (Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID, StartDate)
-- with 0 duplicates and 0 NULLs. The shorter 4-column key WITHOUT StartDate has
-- 2 313 duplicate groups, because rows are validity-window versions of a quote
-- definition. The key is documented but NOT enforced, per the supplied DDL.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.QuoteLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.QuoteLookup
    (
        Code                     VARCHAR(50)  NULL,
        ContinuousForwardPeriod  SMALLINT     NULL,
        Timing                   VARCHAR(50)  NULL,
        ForwardPeriodDescription VARCHAR(500) NULL,
        TimestampTypeID          SMALLINT     NULL,
        PriceTypeID              SMALLINT     NULL,
        DifferentialBasis        VARCHAR(500) NULL,
        DifferentialBasisTiming  VARCHAR(50)  NULL,
        StartDate                DATE         NULL,
        EndDate                  DATE         NULL,
        OldCode                  VARCHAR(50)  NULL,
        DecimalPlaces            TINYINT      NULL,
        ModifiedAtUtc            DATETIME2(3) NULL
            CONSTRAINT DF_QuoteLookup_ModifiedAtUtc DEFAULT (SYSDATETIME())
    );
END
GO

-- Non-unique, because the table is heap-shaped by specification. Supports the
-- documented natural key for consumers joining on it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE [name] = 'IX_DLP_QuoteLookup_NaturalKey'
                 AND object_id = OBJECT_ID('dlp.QuoteLookup'))
    CREATE NONCLUSTERED INDEX IX_DLP_QuoteLookup_NaturalKey
        ON dlp.QuoteLookup (Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID, StartDate);
GO

-- ----------------------------------------------------------------------------
-- dlp.TimestampTypeLookup  <- latestTimestamp.csv  (TimestampID,Description)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.TimestampTypeLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.TimestampTypeLookup
    (
        TimestampTypeID SMALLINT     NOT NULL,
        [Description]   VARCHAR(250) NULL,
        ModifiedAtUtc   DATETIME2(3) NULL
            CONSTRAINT DF_TimestampType_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_TimestampType PRIMARY KEY CLUSTERED (TimestampTypeID)
    );
END
GO

-- =============================================================================
-- REFERENCE TABLES — added by this loader for the remaining DOCUMENTATION files
--
-- Same pattern as above: source columns, ModifiedAtUtc DATETIME2(3) NULL with a
-- sysdatetime() default, clustered PK named PK_DLP_<Table>. Widths come from
-- measured live maxima (docs/apis/Argus.md §4) with headroom.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.TimingLookup  <- latestTiming.csv
--     (TimingId,Description,MinForwardPeriod,MaxForwardPeriod,
--      ForwardPeriodDescription)
-- Min/MaxForwardPeriod reach 2099 (they double as year values) — SMALLINT.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.TimingLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.TimingLookup
    (
        TimingID                 SMALLINT     NOT NULL,
        [Description]            VARCHAR(250) NULL,
        MinForwardPeriod         SMALLINT     NULL,
        MaxForwardPeriod         SMALLINT     NULL,
        ForwardPeriodDescription VARCHAR(500) NULL,
        ModifiedAtUtc            DATETIME2(3) NULL
            CONSTRAINT DF_TimingLookup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_TimingLookup PRIMARY KEY CLUSTERED (TimingID)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.UnitLookup  <- latestUnits.csv  (UNIT_ID,DESCRIPTION,UNIT_DETAILS)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.UnitLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.UnitLookup
    (
        UnitID        SMALLINT     NOT NULL,
        [Description] VARCHAR(250) NULL,
        UnitDetails   VARCHAR(250) NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_UnitLookup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_UnitLookup PRIMARY KEY CLUSTERED (UnitID)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.UnitCodeConversion  <- latestUnitCodeConv.csv
--     (UnitID,BaseUnitID,ValidFrom,ValidTo,CodeID,Ratio)
--
--   * CodeID here is an INTEGER (1 .. 6 999 999) — NOT a 'PAxxxxxxx' string like
--     Code elsewhere in this schema. Different domain, similar name.
--   * Ratio carries up to 17 decimal places (0.00334112930170398) with at most
--     5 integer digits -> DECIMAL(28,17). Rounding it to a smaller scale would
--     silently change conversion factors.
--   * ValidTo is blank on 42 577 of 54 303 rows (open-ended) -> NULL.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.UnitCodeConversion', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.UnitCodeConversion
    (
        UnitID        SMALLINT       NOT NULL,
        BaseUnitID    SMALLINT       NOT NULL,
        CodeID        INT            NOT NULL,
        ValidFrom     DATE           NOT NULL,
        ValidTo       DATE           NULL,
        Ratio         DECIMAL(28,17) NULL,
        ModifiedAtUtc DATETIME2(3)   NULL
            CONSTRAINT DF_UnitCodeConversion_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_UnitCodeConversion PRIMARY KEY CLUSTERED
        (
            UnitID, BaseUnitID, CodeID, ValidFrom
        )
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.HolidayRegionLookup  <- latestHolidayRegion.csv
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.HolidayRegionLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.HolidayRegionLookup
    (
        HolidayRegionID          SMALLINT     NOT NULL,
        HolidayRegionDescription VARCHAR(250) NULL,
        ModifiedAtUtc            DATETIME2(3) NULL
            CONSTRAINT DF_HolidayRegionLookup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_HolidayRegionLookup PRIMARY KEY CLUSTERED (HolidayRegionID)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.Holiday  <- latestHoliday.csv  (HolidayRegionID,HolidayDate)
--
-- The source contains 11 EXACT whole-row duplicates (e.g. '2,01-Jan-2024' twice).
-- They are byte-identical, so the merge's ROW_NUMBER de-duplication is lossless.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.Holiday', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.Holiday
    (
        HolidayRegionID SMALLINT     NOT NULL,
        HolidayDate     DATE         NOT NULL,
        ModifiedAtUtc   DATETIME2(3) NULL
            CONSTRAINT DF_Holiday_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_Holiday PRIMARY KEY CLUSTERED (HolidayRegionID, HolidayDate)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.QuoteHolidayRegion  <- latestQuoteHolidayRegion.csv
--     (Code,ContinuousForwardPeriod,TimeStampID,PriceTypeID,
--      HolidayRegionID1,HolidayRegionID2,HolidayRegionID3)
--
-- HolidayRegionID2 is blank on 44 of 133 998 rows -> NULL. ID1 and ID3 never are,
-- but all three are modelled NULL-able for symmetry.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.QuoteHolidayRegion', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.QuoteHolidayRegion
    (
        Code                    VARCHAR(50)  NOT NULL,
        ContinuousForwardPeriod SMALLINT     NOT NULL,
        TimestampTypeID         SMALLINT     NOT NULL,
        PriceTypeID             SMALLINT     NOT NULL,
        HolidayRegionID1        SMALLINT     NULL,
        HolidayRegionID2        SMALLINT     NULL,
        HolidayRegionID3        SMALLINT     NULL,
        ModifiedAtUtc           DATETIME2(3) NULL
            CONSTRAINT DF_QuoteHolidayRegion_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_QuoteHolidayRegion PRIMARY KEY CLUSTERED
        (
            Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID
        )
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.NewsCategoryLookup  <- latestNewsCategory.csv
--     (CATEGORY_TYPE,CATEGORY_ID,PARENT_ID,DESCRIPTION,ACTIVE)
--
-- CategoryID / ParentID are BIGINT and that is LOAD-BEARING: the live data
-- reaches 10 000 004 936, which overflows INT.
-- CategoryType is part of the key: the same CategoryID may appear under more
-- than one of {Content stream, News Category, News Region, News context}.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.NewsCategoryLookup', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.NewsCategoryLookup
    (
        CategoryType  VARCHAR(50)  NOT NULL,
        CategoryID    BIGINT       NOT NULL,
        ParentID      BIGINT       NULL,
        [Description] VARCHAR(500) NULL,
        Active        CHAR(1)      NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_NewsCategoryLookup_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_NewsCategoryLookup PRIMARY KEY CLUSTERED (CategoryType, CategoryID)
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.RvpCodeReference  <- latestRVP_Code_reference.csv  (CODE_ID,RVP_CODE_ID)
-- Both sides are 'PAxxxxxxx' strings; the pair is the key.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.RvpCodeReference', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.RvpCodeReference
    (
        CodeID        VARCHAR(50)  NOT NULL,
        RvpCodeID     VARCHAR(50)  NOT NULL,
        ModifiedAtUtc DATETIME2(3) NULL
            CONSTRAINT DF_RvpCodeReference_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_RvpCodeReference PRIMARY KEY CLUSTERED (CodeID, RvpCodeID)
    );
END
GO

-- =============================================================================
-- FACT TABLES — as specified by the requester (VERBATIM)
--
-- Source: DCRDEUS/<yyyyMMdd><suffix>.csv. Both tables take the same payload and
-- differ only in grain:
--
--   dlp.TimeSeriesDetail         PK (..., Date)                — current value
--   dlp.TimeSeriesDetailHistory  PK (..., Date, RecordStatus)  — keeps the 'N'
--        original AND any later 'C' correction of it
--
-- Module is NOT in the source file — it is the file-name suffix (dhc -> DHC).
-- RecordStatusDate is NOT in the source file either — it is the file-name date,
-- i.e. when Argus published that status. It doubles as the merge ORDERING GUARD:
-- the same key arrives from more than one file in a single run (a file carries
-- two dates and consecutive files overlap), work units run concurrently, and the
-- merge only overwrites when the incoming file is at least as new as the one
-- already stored. See docs/apis/Argus.md §5.5.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dlp.TimeSeriesDetail — one current row per quote and date.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.TimeSeriesDetail', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.TimeSeriesDetail
    (
        Module           VARCHAR(50)   NOT NULL,
        Code             VARCHAR(50)   NOT NULL,
        TimestampTypeID  SMALLINT      NOT NULL,
        PriceTypeID      SMALLINT      NOT NULL,
        ContFwd          SMALLINT      NOT NULL,
        [Date]           DATE          NOT NULL,
        FwdPeriod        SMALLINT      NULL,
        [Value]          DECIMAL(18,6) NULL,
        DiffBaseRoll     SMALLINT      NULL,
        [Year]           SMALLINT      NULL,
        RecordStatus     CHAR(1)       NULL,
        RecordStatusDate DATE          NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL
            CONSTRAINT DF_TimeSeriesDetail_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_TimeSeriesDetail PRIMARY KEY CLUSTERED
        (
            Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date]
        )
    );
END
GO

-- ----------------------------------------------------------------------------
-- dlp.TimeSeriesDetailHistory — RecordStatus joins the key, so an 'N' original
-- and its later 'C' correction both survive.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dlp.TimeSeriesDetailHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dlp.TimeSeriesDetailHistory
    (
        Module           VARCHAR(50)   NOT NULL,
        Code             VARCHAR(50)   NOT NULL,
        TimestampTypeID  SMALLINT      NOT NULL,
        PriceTypeID      SMALLINT      NOT NULL,
        ContFwd          SMALLINT      NOT NULL,
        [Date]           DATE          NOT NULL,
        RecordStatus     CHAR(1)       NOT NULL,
        RecordStatusDate DATE          NULL,
        FwdPeriod        SMALLINT      NULL,
        [Value]          DECIMAL(18,6) NULL,
        DiffBaseRoll     SMALLINT      NULL,
        [Year]           SMALLINT      NULL,
        SourcePath       VARCHAR(500)  NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL
            CONSTRAINT DF_TimeSeriesDetailHistory_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_DLP_TimeSeriesDetailHistory PRIMARY KEY CLUSTERED
        (
            Module, Code, TimestampTypeID, PriceTypeID, ContFwd, [Date], RecordStatus
        )
    );
END
GO

-- =============================================================================
-- INDEXES — the four requested on dlp.TimeSeriesDetail, plus one on the history
-- table's Date to support the validator's current-vs-history join.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE [name] = 'IX_DLP_Code' AND object_id = OBJECT_ID('dlp.TimeSeriesDetail'))
    CREATE NONCLUSTERED INDEX IX_DLP_Code ON dlp.TimeSeriesDetail (Code);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE [name] = 'IX_DLP_ModifiedAtUtc' AND object_id = OBJECT_ID('dlp.TimeSeriesDetail'))
    CREATE NONCLUSTERED INDEX IX_DLP_ModifiedAtUtc ON dlp.TimeSeriesDetail (ModifiedAtUtc);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE [name] = 'IX_DLP_PriceTypeID' AND object_id = OBJECT_ID('dlp.TimeSeriesDetail'))
    CREATE NONCLUSTERED INDEX IX_DLP_PriceTypeID ON dlp.TimeSeriesDetail (PriceTypeID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE [name] = 'IX_DLP_TimestampTypeID' AND object_id = OBJECT_ID('dlp.TimeSeriesDetail'))
    CREATE NONCLUSTERED INDEX IX_DLP_TimestampTypeID ON dlp.TimeSeriesDetail (TimestampTypeID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE [name] = 'IX_DLP_History_Date' AND object_id = OBJECT_ID('dlp.TimeSeriesDetailHistory'))
    CREATE NONCLUSTERED INDEX IX_DLP_History_Date ON dlp.TimeSeriesDetailHistory ([Date]);
GO
