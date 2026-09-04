-- =============================================================================
-- 001_CreateCriterionSchema.sql
-- Database : Criterion         (create/select it before running this script)
-- Schema   : arm
--
-- Criterion Research data-delivery PostgreSQL replica
-- (dda.criterionrsch.com:443, database "production", SSL required).
-- Source contract verified live 2026-09-03 -- see docs/apis/Criterion.md.
-- Loader design -- see docs/design/Criterion.md.
--
-- 9 pipelines land in the 9 tables below. Unlike every other loader in this
-- repo the source is a RELATIONAL DATABASE, not an HTTP/FTP feed, so the
-- column types below are not guesses: they are the PostgreSQL column types
-- read out of pg_attribute and widened only where SQL Server has no exact
-- equivalent.
--
-- The tables specified by the requester are reproduced VERBATIM -- column
-- names, types, nullability, primary keys, IDENTITY, the geography column and
-- the ModifiedAtUtc default -- with NO deviations. Where the supplied DDL and
-- the source disagree, the loader (not the schema) adapts; each such case is
-- recorded in docs/apis/Criterion.md 5 and repeated in comments here.
--
-- Four oddities in the supplied DDL are kept ON PURPOSE rather than silently
-- "fixed":
--
--   * ModifiedAtUtc DEFAULT is sysdatetime() -- server LOCAL time in a column
--     named ...Utc. Every row this loader writes gets SYSUTCDATETIME() set
--     explicitly by the merge procs, so the DEFAULT only affects rows inserted
--     by something else. (Same posture as sql/ICE/001.)
--
--   * arm.Financial_Metadata.Enabled and arm.Pipelines_NominationPoint.IsLatest
--     have NO source column in PostgreSQL. They are ARM-local curation flags.
--     They are NOT in the TVPs and the merge procs NEVER write them, so a value
--     set by hand survives every subsequent load.
--
--   * arm.Pipelines_Metadata.Point (geography) has no source column either --
--     pipelines.metadata carries latitude/longitude only. The merge builds the
--     point in-proc from those two columns (the IIR arm.Plant posture). NOTE
--     that as of 2026-09-03 latitude and longitude are NULL for all 42,446
--     source rows, so Point lands NULL until Criterion populates them.
--
--   * arm.Pipelines_Pointflows.Id is IDENTITY *and* part of the primary key.
--     A merge cannot match on a server-generated surrogate, so the proc merges
--     on the NATURAL key (MetadataId, EffGasDay, CycleId) -- verified unique in
--     the source (19,431 rows = 19,431 distinct triples on 2026-09-02). Id
--     stays the surrogate the supplied DDL asks for. See 003 for the detail.
--
-- Guarded with IF OBJECT_ID(...) IS NULL so the script is re-runnable.
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = 'arm')
    EXEC ('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- =============================================================================
-- FINANCIAL SERIES  (source schema: data_series)
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Financial_Metadata  <- data_series.financial_metadata  (full snapshot)
--
-- The series catalog: 2,265 rows, one per time series. metadata_uuid is unique
-- and never NULL (verified 2,265/2,265), so it is a safe clustered PK.
--
-- Enabled has no source column -- see the header note.
-- TableName is present in the source but NULL for every row today; it is kept
-- because the supplied DDL asks for it and Criterion may start populating it.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Financial_Metadata', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Financial_Metadata
    (
        MetadataId    UNIQUEIDENTIFIER NOT NULL,
        EntityName    VARCHAR(100)     NULL,
        MetadataDesc  VARCHAR(100)     NULL,
        CmdtyClass    VARCHAR(50)      NULL,
        SubCmdtyDesc  VARCHAR(150)     NULL,
        AssetName     VARCHAR(150)     NULL,
        RegionName    VARCHAR(200)     NULL,
        CountryName   VARCHAR(50)      NULL,
        StateName     VARCHAR(50)      NULL,
        ProvinceName  VARCHAR(100)     NULL,
        MongoId       VARCHAR(24)      NULL,
        [Status]      BIT              NULL,
        SeriesDesc    VARCHAR(150)     NULL,
        SeriesId      VARCHAR(50)      NULL,
        TableName     VARCHAR(100)     NULL,
        EntityId      VARCHAR(24)      NULL,
        SeriesType    VARCHAR(50)      NULL,
        SubRegion     VARCHAR(100)     NULL,
        Ticker        VARCHAR(100)     NULL,
        [Enabled]     BIT              NULL,
        ModifiedAtUtc DATETIME2(3)     NULL CONSTRAINT DF_CRIT_Financial_Metadata_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_financial_Metadata PRIMARY KEY CLUSTERED (MetadataId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Financial_Metadata_Ticker' AND object_id = OBJECT_ID('arm.Financial_Metadata'))
    CREATE NONCLUSTERED INDEX IX_ARM_Financial_Metadata_Ticker ON arm.Financial_Metadata (Ticker);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Financial_Metadata_ModifiedAtUtc' AND object_id = OBJECT_ID('arm.Financial_Metadata'))
    CREATE NONCLUSTERED INDEX IX_ARM_Financial_Metadata_ModifiedAtUtc ON arm.Financial_Metadata (ModifiedAtUtc);
GO

-- ----------------------------------------------------------------------------
-- arm.Financial_Series  <- data_series.financial_json_latest  (DaysBack window)
--
-- One row per PUBLICATION of a series: (series, post_date) with its version.
--
-- ⚠ SOURCE SUBSTITUTION (approved 2026-09-03, docs/apis/Criterion.md 5.1).
--   The request named data_series.financial_json. That table's LATEST-version
--   view, data_series.financial_json_latest, is used instead because it holds
--   exactly one row per (series, post_date) -- 46,032 rows over a 30-day window
--   against 165,859 in financial_json, whose extra 119,827 rows are superseded
--   republications of the same series/day plus 2,320 byte-identical duplicate
--   UUIDs.
--
--   data_series.financial_json_partitioned, named in the request for
--   Financial_SeriesData, is NOT used: its last write was 2024-10-07 and it
--   shares no financial_json_uuid values with the live table, so it would have
--   loaded 0 rows on any DaysBack window AND would never have joined this table.
--
-- financial_json_uuid is the source PK's first column and was verified unique
-- on its own across a 60-day window (0 UUIDs seen under two post_dates), so the
-- supplied single-column PK is safe. The merge still de-duplicates defensively.
--
-- PeriodId/UnitId are Criterion MONGO IDs (24-char), not the UUIDs in
-- arm.Misc_Period/arm.Misc_Unit -- they join those tables on MongoId, which is
-- why the supplied VARCHAR(24) type is correct and no FK is declared. About
-- 1.4% of PeriodId and 0.8% of UnitId values have no matching dimension row.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Financial_Series', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Financial_Series
    (
        FinancialJsonId  UNIQUEIDENTIFIER NOT NULL,
        MetadataId       UNIQUEIDENTIFIER NULL,
        PostDate         DATE             NULL,
        ForecastDate     DATE             NULL,
        LoadDate         DATETIME2(7)     NULL,
        Filename         VARCHAR(255)     NULL,
        [Version]        INT              NULL,
        PeriodId         VARCHAR(24)      NULL,
        UnitId           VARCHAR(24)      NULL,
        Active           BIT              NULL,
        ForecastDateTime DATETIME2(7)     NULL,
        ModifiedAtUtc    DATETIME2(3)     NULL CONSTRAINT DF_CRIT_Financial_Series_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Series PRIMARY KEY CLUSTERED (FinancialJsonId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Financial_Series_Metadata_PostDate' AND object_id = OBJECT_ID('arm.Financial_Series'))
    CREATE NONCLUSTERED INDEX IX_ARM_Financial_Series_Metadata_PostDate
        ON arm.Financial_Series (MetadataId, PostDate) INCLUDE ([Version], Active);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Financial_Series_PostDate' AND object_id = OBJECT_ID('arm.Financial_Series'))
    CREATE NONCLUSTERED INDEX IX_ARM_Financial_Series_PostDate ON arm.Financial_Series (PostDate);
GO

-- ----------------------------------------------------------------------------
-- arm.Financial_SeriesData  <- data_series.financial_json_latest.data
--                              (the same rows as arm.Financial_Series, unpivoted)
--
-- The source `data` column is a JSON ARRAY of observations. It has TWO shapes
-- in production (docs/apis/Criterion.md 4.3):
--
--     daily    (~97% of rows): {"date":"...","value":...}
--     intraday (~3%  of rows): {"date":"...","timestamp":"...",
--                               "time_zone":"Central Time","value":...}
--
-- ⚠ THE INTRADAY SHAPE IS LOSSY UNDER THIS PRIMARY KEY, BY DECISION.
--   PK (FinancialJsonId, [Date]) admits ONE row per calendar day, but an
--   intraday series carries up to 288 five-minute observations per day (ERCOT
--   Real-time Fuel Mix: 17,496 elements across 61 dates). The requester chose
--   2026-09-03 to keep the supplied PK and accept last-wins rather than widen
--   the key with a time column. The merge is therefore DETERMINISTIC about
--   which observation survives -- it keeps the LAST element for a date in
--   source array order, matching the "last-wins" intent -- and the loader logs
--   the collapse per work unit so the loss is visible, never silent.
--
-- This table is by far the largest: a 30-day window is roughly 46,000 source
-- rows x ~2,400 observations = on the order of 110 million rows. See
-- docs/design/Criterion.md 6 for the paging and sizing that follows from that.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Financial_SeriesData', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Financial_SeriesData
    (
        FinancialJsonId UNIQUEIDENTIFIER NOT NULL,
        [Date]          DATE             NOT NULL,
        [Value]         FLOAT            NULL,
        ModifiedAtUtc   DATETIME2(3)     NULL CONSTRAINT DF_CRIT_Financial_SeriesData_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_SeriesData PRIMARY KEY CLUSTERED (FinancialJsonId, [Date])
    );
END
GO

-- =============================================================================
-- MISC DIMENSIONS  (source schema: misc)
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Misc_Period  <- misc.periods  (full snapshot, 12 rows)
--
-- ⚠ SOURCE NAME CORRECTED: the request said misc.period; the table is
--   misc.periods (plural). Likewise its key column is period_uuid, not
--   period_id. Verified 2026-09-03 -- docs/apis/Criterion.md 5.2.
--
-- MongoId is what arm.Financial_Series.PeriodId actually carries, so it is the
-- join column even though PeriodId is the PK.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Misc_Period', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Misc_Period
    (
        PeriodId      UNIQUEIDENTIFIER NOT NULL,
        MongoId       VARCHAR(24)      NULL,
        PeriodDesc    VARCHAR(75)      NULL,
        PeriodShort   VARCHAR(20)      NULL,
        ModifiedAtUtc DATETIME2(3)     NULL CONSTRAINT DF_CRIT_Misc_Period_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Period PRIMARY KEY CLUSTERED (PeriodId ASC)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Misc_Period_MongoId' AND object_id = OBJECT_ID('arm.Misc_Period'))
    CREATE NONCLUSTERED INDEX IX_ARM_Misc_Period_MongoId ON arm.Misc_Period (MongoId);
GO

-- ----------------------------------------------------------------------------
-- arm.Misc_Unit  <- misc.units  (full snapshot, 101 rows)
--
-- ⚠ SOURCE NAME CORRECTED: the request said misc.unit; the table is misc.units
--   and its key column is unit_uuid. Same note as arm.Misc_Period.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Misc_Unit', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Misc_Unit
    (
        UnitId        UNIQUEIDENTIFIER NOT NULL,
        MongoId       VARCHAR(24)      NULL,
        UnitDesc      VARCHAR(50)      NULL,
        ModifiedAtUtc DATETIME2(3)     NULL CONSTRAINT DF_CRIT_Misc_Unit_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Unit PRIMARY KEY CLUSTERED (UnitId ASC)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Misc_Unit_MongoId' AND object_id = OBJECT_ID('arm.Misc_Unit'))
    CREATE NONCLUSTERED INDEX IX_ARM_Misc_Unit_MongoId ON arm.Misc_Unit (MongoId);
GO

-- =============================================================================
-- PIPELINES  (source schema: pipelines)
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Pipelines_Metadata  <- pipelines.metadata  (full snapshot, 42,446 rows)
--
-- The pipeline-point catalog. metadata_id is unique and never NULL.
--
-- Point is built in the merge proc from Latitude/Longitude -- see the header
-- note and 003. MappingId is a local IDENTITY surrogate with no source column;
-- it is not in the TVP and the merge never writes it, so a row keeps the same
-- MappingId across reloads.
--
-- UpdnLoc is VARCHAR(MAX) as supplied even though the source column (text) is
-- at most 48 characters today -- the supplied DDL is reproduced verbatim.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Pipelines_Metadata', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Pipelines_Metadata
    (
        MetadataId                       VARCHAR(30)     NOT NULL,
        AssetId                          VARCHAR(24)     NULL,
        Tsp                              VARCHAR(15)     NULL,
        PipelineName                     VARCHAR(50)     NULL,
        LocName                          VARCHAR(100)    NULL,
        Loc                              VARCHAR(15)     NULL,
        RecDelSign                       SMALLINT        NULL,
        LocQtiId                         SMALLINT        NULL,
        LocQtiShort                      CHAR(3)         NULL,
        CategoryShort                    VARCHAR(25)     NULL,
        SubCategoryDesc                  VARCHAR(50)     NULL,
        SubCategory2Desc                 VARCHAR(100)    NULL,
        CountryName                      VARCHAR(50)     NULL,
        StateName                        VARCHAR(50)     NULL,
        CountyName                       VARCHAR(100)    NULL,
        OffshoreBlockName                VARCHAR(50)     NULL,
        ConnectingPipeline               VARCHAR(50)     NULL,
        ConnectingEntity                 VARCHAR(100)    NULL,
        StorageName                      VARCHAR(100)    NULL,
        StorageCalcFlag                  CHAR(1)         NULL,
        Latitude                         DECIMAL(13,10)  NULL,
        Longitude                        DECIMAL(13,10)  NULL,
        [Point]                          GEOGRAPHY       NULL,
        UpdateDate                       DATETIME2(7)    NULL,
        TspShort                         CHAR(3)         NULL,
        LocPurpDesc                      VARCHAR(50)     NULL,
        StateAbb                         VARCHAR(7)      NULL,
        FercPipelineId                   VARCHAR(7)      NULL,
        FercPointId                      VARCHAR(17)     NULL,
        TransportationMaxDailyQuantity   INT             NULL,
        StorageMaxDailyQuantity          INT             NULL,
        LocZone                          VARCHAR(25)     NULL,
        UpdnLoc                          VARCHAR(MAX)    NULL,
        BasinName                        VARCHAR(50)     NULL,
        Units                            VARCHAR(30)     NULL,
        PointType                        VARCHAR(75)     NULL,
        ProvinceName                     VARCHAR(100)    NULL,
        Ticker                           VARCHAR(100)    NULL,
        MappingId                        INT IDENTITY(1,1) NOT NULL,
        ModifiedAtUtc                    DATETIME2(3)    NULL CONSTRAINT DF_CRIT_Pipelines_Metadata_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Metadata PRIMARY KEY CLUSTERED (MetadataId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Pipelines_Metadata_Ticker' AND object_id = OBJECT_ID('arm.Pipelines_Metadata'))
    CREATE NONCLUSTERED INDEX IX_ARM_Pipelines_Metadata_Ticker ON arm.Pipelines_Metadata (Ticker);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Pipelines_Metadata_TspShort' AND object_id = OBJECT_ID('arm.Pipelines_Metadata'))
    CREATE NONCLUSTERED INDEX IX_ARM_Pipelines_Metadata_TspShort ON arm.Pipelines_Metadata (TspShort);
GO

-- ----------------------------------------------------------------------------
-- arm.Pipelines_NominationPoint  <- pipelines.nomination_points  (DaysBack window)
--
-- Daily nominated/scheduled quantities per point per cycle. 75.5 million rows in
-- the source, ~602,500 in a 30-day window, so this table is ALWAYS windowed --
-- never a snapshot. EffGasDay is the window column and is indexed at the source.
--
-- The four PK columns are never NULL in the source (verified 0/602,533). The
-- source does, however, ship a small number of EXACT duplicate rows on the full
-- key (118 in a 30-day window, all with identical payloads), which is why the
-- merge de-duplicates -- SQL MERGE errors the whole batch on a duplicated
-- source key.
--
-- IsLatest has no source column -- see the header note.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Pipelines_NominationPoint', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Pipelines_NominationPoint
    (
        MetadataId              VARCHAR(30)  NOT NULL,
        EffGasDay               DATE         NOT NULL,
        CycleId                 SMALLINT     NOT NULL,
        HourlyCycleId           INT          NOT NULL,
        EndEffGasDay            DATE         NULL,
        TspShort                CHAR(3)      NULL,
        CycleDesc               VARCHAR(30)  NULL,
        DesignCapacity          FLOAT        NULL,
        OperatingCapacity       FLOAT        NULL,
        ScheduledQuantity       FLOAT        NULL,
        OperationallyAvailable  FLOAT        NULL,
        Tbl                     VARCHAR(10)  NULL,
        Ticker                  VARCHAR(100) NULL,
        ModifiedAtUtc           DATETIME2(3) NULL CONSTRAINT DF_CRIT_Pipelines_NominationPoint_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        IsLatest                BIT          NULL,
        CONSTRAINT PK_ARM_NominationPoint PRIMARY KEY CLUSTERED (MetadataId, EffGasDay, CycleId, HourlyCycleId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Pipelines_NominationPoint_EffGasDay' AND object_id = OBJECT_ID('arm.Pipelines_NominationPoint'))
    CREATE NONCLUSTERED INDEX IX_ARM_Pipelines_NominationPoint_EffGasDay
        ON arm.Pipelines_NominationPoint (EffGasDay) INCLUDE (ScheduledQuantity);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Pipelines_NominationPoint_Ticker' AND object_id = OBJECT_ID('arm.Pipelines_NominationPoint'))
    CREATE NONCLUSTERED INDEX IX_ARM_Pipelines_NominationPoint_Ticker ON arm.Pipelines_NominationPoint (Ticker);
GO

-- ----------------------------------------------------------------------------
-- arm.Pipelines_Pointflows  <- pipelines.nomination_points
--                              LEFT JOIN pipelines.metadata   (DaysBack window)
--
-- ⚠ DERIVED TABLE (approved 2026-09-03, docs/apis/Criterion.md 5.3).
--   The request left this table's source blank, and no pointflows-shaped table
--   exists anywhere in the Criterion database. Every one of its columns is
--   present in exactly one of two tables, so it is populated by the join:
--
--     nomination_points -> MetadataId, EffGasDay, CycleId, CycleDesc,
--                          ScheduledQuantity
--     metadata          -> PipelineName, LocName, CategoryShort, LocZone,
--                          LocPurpDesc, StateName, StateAbb, CountryName
--
--   The join is a LEFT JOIN on purpose: 25 of 19,431 nomination rows on
--   2026-09-02 reference a metadata_id with no catalog row. An INNER JOIN would
--   silently drop those flows; a LEFT JOIN keeps the flow and leaves the
--   descriptive columns NULL.
--
-- ⚠ Id is IDENTITY *and* a PK column, so the merge matches on the NATURAL key
--   (MetadataId, EffGasDay, CycleId) instead -- see 003 and the header note.
--   That triple was verified unique in the source. Note this table therefore
--   holds ONE row per (point, gas day, cycle) while
--   arm.Pipelines_NominationPoint holds one per (point, gas day, cycle, hourly
--   cycle); the two row counts are not expected to match.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Pipelines_Pointflows', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Pipelines_Pointflows
    (
        Id                INT IDENTITY(1,1) NOT NULL,
        MetadataId        VARCHAR(30)  NOT NULL,
        EffGasDay         DATE         NOT NULL,
        CycleId           SMALLINT     NOT NULL,
        CycleDesc         VARCHAR(30)  NULL,
        PipelineName      VARCHAR(50)  NULL,
        LocName           VARCHAR(100) NULL,
        CategoryShort     VARCHAR(25)  NULL,
        LocZone           VARCHAR(25)  NULL,
        LocPurpDesc       VARCHAR(50)  NULL,
        StateName         VARCHAR(50)  NULL,
        StateAbb          VARCHAR(7)   NULL,
        CountryName       VARCHAR(50)  NULL,
        ScheduledQuantity FLOAT        NULL,
        ModifiedAtUtc     DATETIME2(3) NULL CONSTRAINT DF_CRIT_Pipelines_Pointflows_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Pointflows PRIMARY KEY CLUSTERED (MetadataId, EffGasDay, CycleId, Id)
    );
END
GO

-- The merge matches on (MetadataId, EffGasDay, CycleId). The clustered PK above
-- leads with those three columns so the match is a seek, but it does NOT enforce
-- their uniqueness (Id is in the key). This unique index does -- without it, a
-- bug that inserted a second row for the same triple would go unnoticed and the
-- merge would then error with "attempted to update the same row more than once".
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'UQ_ARM_Pointflows_NaturalKey' AND object_id = OBJECT_ID('arm.Pipelines_Pointflows'))
    CREATE UNIQUE NONCLUSTERED INDEX UQ_ARM_Pointflows_NaturalKey
        ON arm.Pipelines_Pointflows (MetadataId, EffGasDay, CycleId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Pipelines_Pointflows_EffGasDay' AND object_id = OBJECT_ID('arm.Pipelines_Pointflows'))
    CREATE NONCLUSTERED INDEX IX_ARM_Pipelines_Pointflows_EffGasDay
        ON arm.Pipelines_Pointflows (EffGasDay) INCLUDE (ScheduledQuantity);
GO

-- ----------------------------------------------------------------------------
-- arm.Pipelines_Region  <- pipelines.regions  (full snapshot, 51 rows)
--
-- Region/state cross-reference with EIA region and PADD labels. The source
-- columns region_id, state_id and state_abb are CHARACTER(n) and therefore
-- BLANK-PADDED ('IA   '); the loader trims them, which matters because
-- StateAbb is VARCHAR(5) here and an untrimmed value would compare unequal to
-- the same abbreviation elsewhere.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Pipelines_Region', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Pipelines_Region
    (
        RegionId         VARCHAR(24)  NOT NULL,
        StateId          CHAR(24)     NOT NULL,
        RegionName       VARCHAR(30)  NULL,
        StateAbb         VARCHAR(5)   NULL,
        StateName        VARCHAR(50)  NULL,
        EIA_NG_Regions   VARCHAR(30)  NULL,
        EIA_PADD_Regions VARCHAR(10)  NULL,
        ModifiedAtUtc    DATETIME2(3) NULL CONSTRAINT DF_CRIT_Pipelines_Region_ModifiedAtUtc DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ARM_Region PRIMARY KEY CLUSTERED (RegionId, StateId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = 'IX_ARM_Pipelines_Region_StateAbb' AND object_id = OBJECT_ID('arm.Pipelines_Region'))
    CREATE NONCLUSTERED INDEX IX_ARM_Pipelines_Region_StateAbb ON arm.Pipelines_Region (StateAbb);
GO
