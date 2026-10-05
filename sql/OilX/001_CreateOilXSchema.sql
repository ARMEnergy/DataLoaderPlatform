-- =============================================================================
-- 001_CreateOilXSchema.sql
-- Database : OilX
-- Schema   : arm
--
-- Eight fact tables, one per in-scope OilX CSV feed:
--
--   arm.CargoTracking        <- CargoTracking.<ts>.csv
--   arm.FloatingStorage      <- FloatingStorageVessels.<ts>.csv
--   arm.Flow                 <- Flows.<ts>.csv
--   arm.GlobalBalance        <- GlobalBalance.<ts>.csv
--   arm.OilFieldProduction   <- OilFieldsProduction.<ts>.csv
--   arm.RegionalBalance      <- Regional_Balance.<ts>.csv
--   arm.SupplyDemand         <- SupplyDemand.<ts>.csv
--   arm.Terminal             <- Terminals.<ts>.csv
--
-- Design of record: docs/design/OilX.md
-- Field reference:  docs/apis/OilX.md
-- Template:         sql/EOX/001, sql/Genscape/001
--
-- -----------------------------------------------------------------------------
-- THE USER-SUPPLIED DDL IS HONOURED VERBATIM
-- -----------------------------------------------------------------------------
-- Every column name, type, nullability, ORDER, default and constraint name below
-- is exactly as supplied in the loader request. Exactly ONE additive change was
-- made, and it was explicitly requested:
--
--   + arm.CargoTracking.FlowID  VARCHAR(64) NULL
--       The vendor's own per-row sha256 identity. Verified UNIQUE across all
--       396,865 rows of CargoTracking.2026-09-30T03-39.csv, which is what makes
--       it this feed's RowId source (docs/design/OilX.md S5). Placed AFTER the
--       last supplied payload column (LastUpdateDate) and BEFORE FileName, so no
--       supplied column changed position.
--
-- Deliberately NOT added: a DateCreated/FileLogId audit hub of the kind
-- arm.FileLog gives EvolutionMarkets. The supplied DDL already carries its own
-- provenance pair (FileName + Checksum), the request did not ask for a hub, and
-- an extra parent table on a ~64M-row load is cost without a stated need.
--
-- -----------------------------------------------------------------------------
-- WHY RunDate LEADS EVERY PRIMARY KEY
-- -----------------------------------------------------------------------------
-- Each daily CSV is a FULL snapshot of all history, not that day's increment
-- (docs/apis/OilX.md S2 Behaviour 7): one day's GlobalBalance file carries
-- ReferenceDate values from 2010 onward. A row is therefore identified by WHICH
-- PUBLICATION it came from (RunDate) plus WHICH SERIES it is (RowId). RunDate is
-- read FROM THE FILE, never from the loader's clock -- all of a day's snapshots
-- carry the same RunDate, which is precisely why they merge over one another.
--
-- RunDate leading the clustered key also makes days disjoint, which is what lets
-- work units for different days run in parallel without contending.
--
-- -----------------------------------------------------------------------------
-- RowId IS DERIVED, NEVER RANDOM
-- -----------------------------------------------------------------------------
-- RowId is a UUIDv5-style SHA-1 of the feed's business key under a fixed loader
-- namespace (OilXRowId.cs). It is deterministic and depends on the KEY columns
-- only -- never on the row's values, or a changed price would insert a second row
-- instead of updating the first. A random GUID would turn every run into an
-- append of ~2M rows. The business keys are listed per table below, each verified
-- unique against a live file.
--
-- Standing conventions applied: named constraints (PK_/DF_/IX_), guarded
-- re-runnable creates, no TINYINT, [arm] schema throughout (NOT the dbo default).
-- =============================================================================

IF SCHEMA_ID('arm') IS NULL
    EXEC('CREATE SCHEMA arm AUTHORIZATION dbo;');
GO

-- ----------------------------------------------------------------------------
-- arm.CargoTracking
--   Business key -> RowId : FlowID
--   Verified UNIQUE 396,865 / 396,865 rows (2026-09-30T03-39).
--
--   Note VoyageID is NOT an identity and is not loaded: those same 396,865 rows
--   carry only 681 distinct VoyageID values.
--
--   LoadSTSIndicator / DischargeSTSIndicator are '0'/'1' and the matching
--   *STSIMO columns are '-1' when the leg is not a ship-to-ship transfer. Both
--   are VARCHAR per the supplied DDL rather than coerced to BIT/INT.
--
--   SulphurContent is VARCHAR(MAX) per the supplied DDL. The live values look
--   numeric ('5.40'), but the DDL is honoured and the text is stored as-is.
--
--   The CSV publishes LoadQuantity(KT) BEFORE LoadQuantity(KBBL); this table
--   lists KBBL first, as supplied. Mapping is BY HEADER NAME, never by position
--   (docs/apis/OilX.md S4).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CargoTracking') IS NULL
BEGIN
    CREATE TABLE [arm].[CargoTracking]
    (
        [RunDate]                 date             NOT NULL,
        [RowId]                   uniqueidentifier NOT NULL,
        [IMO]                     varchar(250)     NULL,
        [VesselName]              varchar(250)     NULL,
        [VesselClass]             varchar(250)     NULL,
        [LoadDate]                datetime2(7)     NULL,
        [LoadArea]                varchar(250)     NULL,
        [LoadCountry]             varchar(250)     NULL,
        [LoadSubcountryArea]      varchar(250)     NULL,
        [LoadPort]                varchar(250)     NULL,
        [LoadSTSIndicator]        varchar(250)     NULL,
        [LoadSTSIMO]              varchar(250)     NULL,
        [OriginCountry]           varchar(250)     NULL,
        [OriginCountryGroup]      varchar(250)     NULL,
        [GradeName]               varchar(250)     NULL,
        [DischargeDate]           datetime2(7)     NULL,
        [DischargeArea]           varchar(250)     NULL,
        [DischargeCountry]        varchar(250)     NULL,
        [DischargeSubcountryArea] varchar(250)     NULL,
        [DischargePort]           varchar(250)     NULL,
        [DischargeSTSIndicator]   varchar(250)     NULL,
        [DischargeSTSIMO]         varchar(250)     NULL,
        [DestinationCountry]      varchar(250)     NULL,
        [DestinationCountryGroup] varchar(250)     NULL,
        [LoadQuantity_KBBL]       float            NULL,
        [LoadQuantity_KT]         float            NULL,
        [SulphurContent]          varchar(max)     NULL,
        [APIGravity]              float            NULL,
        [Charterer]               varchar(max)     NULL,
        [Supplier]                varchar(max)     NULL,
        [Buyer]                   varchar(max)     NULL,
        [LastUpdateDate]          datetime2(7)     NULL,
        [FlowID]                  varchar(64)      NULL,
        [FileName]                varchar(100)     NULL,
        [Checksum]                int              NULL,
        [ModifiedAtUtc]           datetime2(2)     NULL
            CONSTRAINT [DF_ARM_CargoTracking_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_CargoTracking] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FloatingStorage
--   Business key -> RowId : IMO, ReferenceDate
--   Verified UNIQUE 6,072 / 6,072 rows (2026-09-30T21-10).
--
--   IMO alone is NOT unique -- 1,474 distinct vessels over 6,072 rows, one of
--   them appearing 68 times -- because the feed is one row per vessel per
--   reference date.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FloatingStorage') IS NULL
BEGIN
    CREATE TABLE [arm].[FloatingStorage]
    (
        [RunDate]             date             NOT NULL,
        [RowId]               uniqueidentifier NOT NULL,
        [IMO]                 varchar(50)      NULL,
        [ReferenceDate]       date             NULL,
        [VesselName]          varchar(256)     NULL,
        [VesselClass]         varchar(50)      NULL,
        [StartDate]           datetime2(7)     NULL,
        [EndDate]             datetime2(7)     NULL,
        [QuantityKiloBarrels] decimal(18,8)    NULL,
        [Area]                varchar(256)     NULL,
        [FileName]            varchar(100)     NULL,
        [Checksum]            int              NULL,
        [ModifiedAtUtc]       datetime2(2)     NULL
            CONSTRAINT [DF_ARM_FloatingStorage_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_FloatingStorage] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.Flow
--   Business key -> RowId : OriginCountryName, DestinationCountryName,
--                           GroupByDateIndicator, OriginSubCountry,
--                           DestinationSubCountry, ReferenceDate, GradeName
--   Verified UNIQUE 232,311 / 232,311 rows (2026-09-30T21-10).
--
--   /!\ THE TWO SUBCOUNTRY COLUMNS ARE LOAD-BEARING IN THE KEY. Dropping them
--   collapses 28,887 of those 232,311 rows onto shared keys -- measured, not
--   hypothetical. They are very often EMPTY, which is exactly what makes them
--   look droppable. Removing either one silently loses an eighth of the feed.
--
--   GroupByDateIndicator is 'Exports'/'Imports'; the same physical cargo is
--   published under both, so it too is a key component. The CSV header spells it
--   'GroupbyDateIndicator' with a lower-case b (docs/apis/OilX.md S4).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Flow') IS NULL
BEGIN
    CREATE TABLE [arm].[Flow]
    (
        [RunDate]                date             NOT NULL,
        [RowId]                  uniqueidentifier NOT NULL,
        [ReferenceDate]          date             NULL,
        [OriginCountryName]      varchar(50)      NULL,
        [DestinationCountryName] varchar(50)      NULL,
        [GroupByDateIndicator]   varchar(50)      NULL,
        [OriginSubCountry]       varchar(50)      NULL,
        [DestinationSubCountry]  varchar(50)      NULL,
        [GradeName]              varchar(50)      NULL,
        [GradeCategory]          varchar(50)      NULL,
        [ApiGravity]             decimal(18,8)    NULL,
        [SulphurContent]         decimal(18,8)    NULL,
        [QuantityKBD]            decimal(18,8)    NULL,
        [QuantityKBBL]           decimal(18,8)    NULL,
        [FileName]               varchar(100)     NULL,
        [Checksum]               int              NULL,
        [ModifiedAtUtc]          datetime2(2)     NULL
            CONSTRAINT [DF_ARM_Flow_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_Flow] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.GlobalBalance
--   Business key -> RowId : GroupName, ReferenceDate, FlowBreakdown, UnitMeasure
--   Verified UNIQUE 4,663 / 4,663 rows (2026-09-30T09-55).
--
--   ObservedValue is DECIMAL(28,8) per the supplied DDL -- wider than the
--   DECIMAL(18,8) used by the other value columns, and kept as specified.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.GlobalBalance') IS NULL
BEGIN
    CREATE TABLE [arm].[GlobalBalance]
    (
        [RunDate]       date             NOT NULL,
        [RowId]         uniqueidentifier NOT NULL,
        [GroupName]     varchar(50)      NULL,
        [ReferenceDate] datetime2(7)     NULL,
        [FlowBreakdown] varchar(50)      NULL,
        [UnitMeasure]   varchar(50)      NULL,
        [ObservedValue] decimal(28,8)    NULL,
        [FileName]      varchar(100)     NULL,
        [Checksum]      int              NULL,
        [ModifiedAtUtc] datetime2(2)     NULL
            CONSTRAINT [DF_ARM_GlobalBalance_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_GlobalBalance] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.OilFieldProduction
--   Business key -> RowId : OilFieldName, PortName, CountryName, ReferenceDate,
--                           UnitMeasure
--   Verified UNIQUE 252,130 / 252,130 rows (2026-09-30T09-55).
--
--   The minimal key (OilFieldName, ReferenceDate, UnitMeasure) is ALSO unique in
--   live data. The fuller grain is used deliberately: a field served by a second
--   port then becomes a SECOND ROW rather than a value silently discarded by the
--   merge's de-dup guard. The cost is that a PortName the vendor later populates
--   on a previously-blank row changes that row's RowId and strands the old one --
--   arm.usp_ValidateLoad's OrphanPort check reports exactly that.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.OilFieldProduction') IS NULL
BEGIN
    CREATE TABLE [arm].[OilFieldProduction]
    (
        [RunDate]       date             NOT NULL,
        [RowId]         uniqueidentifier NOT NULL,
        [ReferenceDate] date             NULL,
        [OilFieldName]  varchar(50)      NULL,
        [UnitMeasure]   varchar(50)      NULL,
        [PortName]      varchar(256)     NULL,
        [CountryName]   varchar(50)      NULL,
        [ObservedValue] decimal(18,8)    NULL,
        [FileName]      varchar(100)     NULL,
        [Checksum]      int              NULL,
        [ModifiedAtUtc] datetime2(2)     NULL
            CONSTRAINT [DF_ARM_OilFieldProduction_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_OilFieldProduction] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.RegionalBalance
--   Business key -> RowId : GroupName, ReferenceDate, FlowBreakdown, UnitMeasure
--   Verified UNIQUE 22,090 / 22,090 rows (2026-09-30T09-55).
--
--   GeneralizedSource ('OilX') is a VALUE column, not a key component: the key
--   above is already unique, and including a near-constant provenance string
--   would make every row's identity hostage to the vendor relabelling its own
--   source.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.RegionalBalance') IS NULL
BEGIN
    CREATE TABLE [arm].[RegionalBalance]
    (
        [RunDate]           date             NOT NULL,
        [RowId]             uniqueidentifier NOT NULL,
        [GroupName]         varchar(50)      NULL,
        [ReferenceDate]     datetime2(7)     NULL,
        [FlowBreakdown]     varchar(50)      NULL,
        [UnitMeasure]       varchar(50)      NULL,
        [ObservedValue]     decimal(28,8)    NULL,
        [GeneralizedSource] varchar(50)      NULL,
        [FileName]          varchar(100)     NULL,
        [Checksum]          int              NULL,
        [ModifiedAtUtc]     datetime2(2)     NULL
            CONSTRAINT [DF_ARM_RegionalBalance_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_RegionalBalance] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SupplyDemand
--   Business key -> RowId : CountryISOCode, ReferenceDate, FlowBreakdown,
--                           UnitMeasure
--   Verified UNIQUE 364,543 / 364,543 rows (2026-09-30T09-55).
--
--   CountryName and GeneralizedSource are VALUE columns. CountryName is
--   functionally dependent on CountryISOCode, so adding it to the key would buy
--   nothing and would fork a country's history the day the vendor renames it.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SupplyDemand') IS NULL
BEGIN
    CREATE TABLE [arm].[SupplyDemand]
    (
        [RunDate]           date             NOT NULL,
        [RowId]             uniqueidentifier NOT NULL,
        [CountryISOCode]    varchar(50)      NULL,
        [ReferenceDate]     datetime2(7)     NULL,
        [FlowBreakdown]     varchar(50)      NULL,
        [CountryName]       varchar(250)     NULL,
        [UnitMeasure]       varchar(50)      NULL,
        [ObservedValue]     decimal(28,8)    NULL,
        [GeneralizedSource] varchar(50)      NULL,
        [FileName]          varchar(100)     NULL,
        [Checksum]          int              NULL,
        [ModifiedAtUtc]     datetime2(2)     NULL
            CONSTRAINT [DF_ARM_SupplyDemand_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_SupplyDemand] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.Terminal
--   Business key -> RowId : TerminalName, ReferenceDate, FlowBreakdown,
--                           UnitMeasure
--   Verified UNIQUE 786,798 / 786,798 rows (2026-09-30T09-55) -- the largest
--   single file in the loader by row count.
--
--   ReferenceDate here is DATETIME2(2), not (7) as on the other balance feeds,
--   and CountryName follows ObservedValue. Both are as supplied.
--
--   The CSV's IsForwardFilled flag is not loaded (no column was specified).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Terminal') IS NULL
BEGIN
    CREATE TABLE [arm].[Terminal]
    (
        [RunDate]       date             NOT NULL,
        [RowId]         uniqueidentifier NOT NULL,
        [TerminalName]  varchar(50)      NULL,
        [ReferenceDate] datetime2(2)     NULL,
        [FlowBreakdown] varchar(50)      NULL,
        [UnitMeasure]   varchar(50)      NULL,
        [ObservedValue] decimal(18,8)    NULL,
        [CountryName]   varchar(50)      NULL,
        [FileName]      varchar(100)     NULL,
        [Checksum]      int              NULL,
        [ModifiedAtUtc] datetime2(2)     NULL
            CONSTRAINT [DF_ARM_Terminal_ModifiedAtUtc] DEFAULT (sysdatetime()),
        CONSTRAINT [PK_ARM_Terminal] PRIMARY KEY CLUSTERED ([RunDate], [RowId])
    );
END
GO

-- =============================================================================
-- INDEXES
--
-- The clustered PK (RunDate, RowId) serves the MERGE perfectly and almost no
-- analytical query: RowId is a hash, so it has no useful ordering and nobody
-- filters on it. Every real query is "this series, over time", which means
-- ReferenceDate plus the feed's leading dimension.
--
-- ONE nonclustered index per table, deliberately. At ~2M rows per RunDate and a
-- 31-day window, each additional index is real time on every merge; these are
-- the ones that turn a scan of a day's partition into a seek. All are filtered
-- by nothing and lead with RunDate so they stay aligned with the clustering.
-- =============================================================================

IF OBJECT_ID('arm.CargoTracking') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.CargoTracking'), 'IX_ARM_CargoTracking_RunDate_LoadDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_CargoTracking_RunDate_LoadDate]
        ON [arm].[CargoTracking] ([RunDate], [LoadDate]) INCLUDE ([IMO], [GradeName]);
GO

IF OBJECT_ID('arm.FloatingStorage') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.FloatingStorage'), 'IX_ARM_FloatingStorage_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_FloatingStorage_RunDate_ReferenceDate]
        ON [arm].[FloatingStorage] ([RunDate], [ReferenceDate]) INCLUDE ([IMO], [Area]);
GO

IF OBJECT_ID('arm.Flow') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.Flow'), 'IX_ARM_Flow_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_Flow_RunDate_ReferenceDate]
        ON [arm].[Flow] ([RunDate], [ReferenceDate])
        INCLUDE ([OriginCountryName], [DestinationCountryName], [GradeName]);
GO

IF OBJECT_ID('arm.GlobalBalance') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.GlobalBalance'), 'IX_ARM_GlobalBalance_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_GlobalBalance_RunDate_ReferenceDate]
        ON [arm].[GlobalBalance] ([RunDate], [ReferenceDate]) INCLUDE ([GroupName], [FlowBreakdown]);
GO

IF OBJECT_ID('arm.OilFieldProduction') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.OilFieldProduction'), 'IX_ARM_OilFieldProduction_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_OilFieldProduction_RunDate_ReferenceDate]
        ON [arm].[OilFieldProduction] ([RunDate], [ReferenceDate]) INCLUDE ([OilFieldName], [CountryName]);
GO

IF OBJECT_ID('arm.RegionalBalance') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.RegionalBalance'), 'IX_ARM_RegionalBalance_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_RegionalBalance_RunDate_ReferenceDate]
        ON [arm].[RegionalBalance] ([RunDate], [ReferenceDate]) INCLUDE ([GroupName], [FlowBreakdown]);
GO

IF OBJECT_ID('arm.SupplyDemand') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.SupplyDemand'), 'IX_ARM_SupplyDemand_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_SupplyDemand_RunDate_ReferenceDate]
        ON [arm].[SupplyDemand] ([RunDate], [ReferenceDate]) INCLUDE ([CountryISOCode], [FlowBreakdown]);
GO

IF OBJECT_ID('arm.Terminal') IS NOT NULL
   AND INDEXPROPERTY(OBJECT_ID('arm.Terminal'), 'IX_ARM_Terminal_RunDate_ReferenceDate', 'IndexID') IS NULL
    CREATE NONCLUSTERED INDEX [IX_ARM_Terminal_RunDate_ReferenceDate]
        ON [arm].[Terminal] ([RunDate], [ReferenceDate]) INCLUDE ([TerminalName], [FlowBreakdown]);
GO
