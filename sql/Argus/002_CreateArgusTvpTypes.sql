-- =============================================================================
-- 002_CreateArgusTvpTypes.sql
-- Database : Argus
-- Schema   : dlp
--
-- One table type per feed: 15 reference TVPs + 1 fact TVP.
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the feed's column list in src/DataLoader.Argus/ArgusDescriptors.cs EXACTLY; a
-- silent reorder corrupts every loaded row without raising an error.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.Argus.Tests/ArgusTvpContractTests.cs parses THIS FILE, pulls
-- each CREATE TYPE body out, and asserts name + order + type against the
-- descriptors. Editing a type here without editing the descriptor (or the other
-- way round) fails the test suite.
--
-- Conventions:
--   * TVP column order = target table column order.
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * NOT NULL marks a column the parser treats as REQUIRED (a row missing it is
--     dropped rather than merged under a blank key).
--   * dlp.TimeSeriesDetailTvp carries one extra trailing column, SourceFileDate,
--     which is the merge ORDERING GUARD and is not a column of either fact table.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed — to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Reference feeds
-- ----------------------------------------------------------------------------

-- latestCategory.csv. Source column ORDER is (Code, DisplayName, Category);
-- this TVP is (Code, Category, DisplayName) to match the table. The loader maps
-- by header NAME, so the swap is handled there, not here.
IF TYPE_ID('dlp.CategoryLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.CategoryLookupTvp AS TABLE
    (
        Code        VARCHAR(50)  NOT NULL,
        Category    VARCHAR(500) NOT NULL,
        DisplayName VARCHAR(500) NULL
    );
END
GO

-- latestCodes.csv. Specification is the column added to the supplied DDL.
IF TYPE_ID('dlp.CodeLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.CodeLookupTvp AS TABLE
    (
        Code          VARCHAR(50)   NOT NULL,
        DisplayName   VARCHAR(2000) NULL,
        DeliveryMode  VARCHAR(50)   NULL,
        [Unit]        VARCHAR(50)   NULL,
        Frequency     VARCHAR(50)   NULL,
        Specification VARCHAR(50)   NULL
    );
END
GO

-- latestModuleDetails.csv. Source headers TimeStampID / StartDateInModule /
-- EndDateInModule map to TimestampTypeID / StartDate / EndDate.
IF TYPE_ID('dlp.ModuleDetailLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.ModuleDetailLookupTvp AS TABLE
    (
        Module                  VARCHAR(50) NOT NULL,
        Code                    VARCHAR(50) NOT NULL,
        TimestampTypeID         SMALLINT    NOT NULL,
        PriceTypeID             SMALLINT    NOT NULL,
        ContinuousForwardPeriod SMALLINT    NOT NULL,
        StartDate               DATE        NULL,
        EndDate                 DATE        NULL
    );
END
GO

-- latestModules.csv.
IF TYPE_ID('dlp.ModuleLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.ModuleLookupTvp AS TABLE
    (
        Module        VARCHAR(50)  NOT NULL,
        [Path]        VARCHAR(250) NULL,
        FileName      VARCHAR(50)  NULL,
        [Description] VARCHAR(500) NULL,
        Folder        VARCHAR(250) NULL,
        [Time]        VARCHAR(50)  NULL,
        LocalTime     TIME(0)      NULL,
        LocalTimeZone VARCHAR(250) NULL
    );
END
GO

-- latestPricetype.csv.
IF TYPE_ID('dlp.PriceTypeLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.PriceTypeLookupTvp AS TABLE
    (
        PriceTypeID   SMALLINT     NOT NULL,
        [Description] VARCHAR(250) NULL
    );
END
GO

-- latestQuotes.csv. Feeds the loader's only FULL REPLACE proc. Every column
-- except Code is NULL-able, matching the supplied table DDL; Code is required
-- because a quote row with no code cannot be used (0 blanks observed live).
IF TYPE_ID('dlp.QuoteLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.QuoteLookupTvp AS TABLE
    (
        Code                     VARCHAR(50)  NOT NULL,
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
        DecimalPlaces            TINYINT      NULL
    );
END
GO

-- latestTimestamp.csv. Source header is TimestampID.
IF TYPE_ID('dlp.TimestampTypeLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.TimestampTypeLookupTvp AS TABLE
    (
        TimestampTypeID SMALLINT     NOT NULL,
        [Description]   VARCHAR(250) NULL
    );
END
GO

-- latestTiming.csv.
IF TYPE_ID('dlp.TimingLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.TimingLookupTvp AS TABLE
    (
        TimingID                 SMALLINT     NOT NULL,
        [Description]            VARCHAR(250) NULL,
        MinForwardPeriod         SMALLINT     NULL,
        MaxForwardPeriod         SMALLINT     NULL,
        ForwardPeriodDescription VARCHAR(500) NULL
    );
END
GO

-- latestUnits.csv. Source headers are UNIT_ID / DESCRIPTION / UNIT_DETAILS.
IF TYPE_ID('dlp.UnitLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.UnitLookupTvp AS TABLE
    (
        UnitID        SMALLINT     NOT NULL,
        [Description] VARCHAR(250) NULL,
        UnitDetails   VARCHAR(250) NULL
    );
END
GO

-- latestUnitCodeConv.csv. CodeID is an INT here (not a 'PAxxxxxxx' string).
-- Ratio needs the full 17-place scale — see 001.
IF TYPE_ID('dlp.UnitCodeConversionTvp') IS NULL
BEGIN
    CREATE TYPE dlp.UnitCodeConversionTvp AS TABLE
    (
        UnitID     SMALLINT       NOT NULL,
        BaseUnitID SMALLINT       NOT NULL,
        CodeID     INT            NOT NULL,
        ValidFrom  DATE           NOT NULL,
        ValidTo    DATE           NULL,
        Ratio      DECIMAL(28,17) NULL
    );
END
GO

-- latestHolidayRegion.csv.
IF TYPE_ID('dlp.HolidayRegionLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.HolidayRegionLookupTvp AS TABLE
    (
        HolidayRegionID          SMALLINT     NOT NULL,
        HolidayRegionDescription VARCHAR(250) NULL
    );
END
GO

-- latestHoliday.csv. Both columns are the key; the source has 11 exact dupes.
IF TYPE_ID('dlp.HolidayTvp') IS NULL
BEGIN
    CREATE TYPE dlp.HolidayTvp AS TABLE
    (
        HolidayRegionID SMALLINT NOT NULL,
        HolidayDate     DATE     NOT NULL
    );
END
GO

-- latestQuoteHolidayRegion.csv. Source header TimeStampID -> TimestampTypeID.
IF TYPE_ID('dlp.QuoteHolidayRegionTvp') IS NULL
BEGIN
    CREATE TYPE dlp.QuoteHolidayRegionTvp AS TABLE
    (
        Code                    VARCHAR(50) NOT NULL,
        ContinuousForwardPeriod SMALLINT    NOT NULL,
        TimestampTypeID         SMALLINT    NOT NULL,
        PriceTypeID             SMALLINT    NOT NULL,
        HolidayRegionID1        SMALLINT    NULL,
        HolidayRegionID2        SMALLINT    NULL,
        HolidayRegionID3        SMALLINT    NULL
    );
END
GO

-- latestNewsCategory.csv. BIGINT is load-bearing (ids exceed INT).
IF TYPE_ID('dlp.NewsCategoryLookupTvp') IS NULL
BEGIN
    CREATE TYPE dlp.NewsCategoryLookupTvp AS TABLE
    (
        CategoryType  VARCHAR(50)  NOT NULL,
        CategoryID    BIGINT       NOT NULL,
        ParentID      BIGINT       NULL,
        [Description] VARCHAR(500) NULL,
        Active        CHAR(1)      NULL
    );
END
GO

-- latestRVP_Code_reference.csv.
IF TYPE_ID('dlp.RvpCodeReferenceTvp') IS NULL
BEGIN
    CREATE TYPE dlp.RvpCodeReferenceTvp AS TABLE
    (
        CodeID    VARCHAR(50) NOT NULL,
        RvpCodeID VARCHAR(50) NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- Fact feed — ONE type feeds BOTH fact tables.
--
-- dlp.TimeSeriesDetail and dlp.TimeSeriesDetailHistory take an identical payload
-- and differ only in grain, so a single TVP carries the parsed file and
-- dlp.usp_BulkMergeTimeSeriesDetail (003) fans it into both in one transaction —
-- the two tables can never drift apart.
--
--   * Module, RecordStatusDate and SourcePath are DERIVED in C# (file-name
--     suffix, file-name date, remote path) — they are not columns of the CSV.
--   * RecordStatus is NOT NULL here because it is a PK component of the history
--     table, even though it is NULL-able in dlp.TimeSeriesDetail.
--   * SourceFileDate is the ORDERING GUARD and is NOT a column of either table.
--     It is the last column so the payload prefix stays aligned with the tables.
-- ----------------------------------------------------------------------------
IF TYPE_ID('dlp.TimeSeriesDetailTvp') IS NULL
BEGIN
    CREATE TYPE dlp.TimeSeriesDetailTvp AS TABLE
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
        SourceFileDate   DATE          NOT NULL
    );
END
GO
