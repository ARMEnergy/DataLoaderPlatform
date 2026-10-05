-- =============================================================================
-- 002_CreateOilXTvpTypes.sql
-- Database : OilX
-- Schema   : arm
--
-- One table type per target table (8).
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in sql/OilX/001 and the descriptor's column list in
-- src/DataLoader.OilX/OilXDescriptors.cs EXACTLY; a silent reorder corrupts every
-- loaded row without raising an error.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.OilX.Tests/OilXTvpContractTests.cs parses THIS FILE, pulls
-- each CREATE TYPE body out, and asserts name + order + type + nullability
-- against the descriptors. Editing a type here without editing the descriptor
-- (or the other way round) fails the test suite.
--
-- Conventions:
--   * TVP column order = target table column order.
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * NOT NULL marks a column the reader treats as REQUIRED: a row missing it is
--     DROPPED and counted rather than merged under a blank key.
--
-- Four columns are NOT NULL in every type:
--     RunDate, RowId    -- the primary key
--     FileName          -- provenance; also what names the winning snapshot
--     Checksum          -- the merge's change guard; a NULL would make every
--                          row compare unequal and re-stamp ModifiedAtUtc
--                          forever, quietly destroying the column's meaning
-- Every other column is NULLable, matching 001, because the vendor leaves most
-- of them blank on some rows -- including several KEY components (Flow's two
-- subcountry columns are usually blank). A blank key component is allowed: the
-- loader converts a blank cell to NULL and feeds RowId a dedicated null token,
-- which is a stable, intentional identity rather than a blank key. That is also
-- why no key column is NOT NULL here -- marking one would drop every row whose
-- key is legitimately blank, which for Flow is most of them.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.CargoTrackingTvp  ->  arm.CargoTracking
-- 35 columns: the table's 36 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.CargoTrackingTvp') IS NULL
BEGIN
    CREATE TYPE arm.CargoTrackingTvp AS TABLE
    (
        [RunDate]                 DATE             NOT NULL,
        [RowId]                   UNIQUEIDENTIFIER NOT NULL,
        [IMO]                     VARCHAR(250)     NULL,
        [VesselName]              VARCHAR(250)     NULL,
        [VesselClass]             VARCHAR(250)     NULL,
        [LoadDate]                DATETIME2(7)     NULL,
        [LoadArea]                VARCHAR(250)     NULL,
        [LoadCountry]             VARCHAR(250)     NULL,
        [LoadSubcountryArea]      VARCHAR(250)     NULL,
        [LoadPort]                VARCHAR(250)     NULL,
        [LoadSTSIndicator]        VARCHAR(250)     NULL,
        [LoadSTSIMO]              VARCHAR(250)     NULL,
        [OriginCountry]           VARCHAR(250)     NULL,
        [OriginCountryGroup]      VARCHAR(250)     NULL,
        [GradeName]               VARCHAR(250)     NULL,
        [DischargeDate]           DATETIME2(7)     NULL,
        [DischargeArea]           VARCHAR(250)     NULL,
        [DischargeCountry]        VARCHAR(250)     NULL,
        [DischargeSubcountryArea] VARCHAR(250)     NULL,
        [DischargePort]           VARCHAR(250)     NULL,
        [DischargeSTSIndicator]   VARCHAR(250)     NULL,
        [DischargeSTSIMO]         VARCHAR(250)     NULL,
        [DestinationCountry]      VARCHAR(250)     NULL,
        [DestinationCountryGroup] VARCHAR(250)     NULL,
        [LoadQuantity_KBBL]       FLOAT            NULL,
        [LoadQuantity_KT]         FLOAT            NULL,
        [SulphurContent]          VARCHAR(MAX)     NULL,
        [APIGravity]              FLOAT            NULL,
        [Charterer]               VARCHAR(MAX)     NULL,
        [Supplier]                VARCHAR(MAX)     NULL,
        [Buyer]                   VARCHAR(MAX)     NULL,
        [LastUpdateDate]          DATETIME2(7)     NULL,
        [FlowID]                  VARCHAR(64)      NULL,
        [FileName]                VARCHAR(100)     NOT NULL,
        [Checksum]                INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FloatingStorageTvp  ->  arm.FloatingStorage
-- 12 columns: the table's 13 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FloatingStorageTvp') IS NULL
BEGIN
    CREATE TYPE arm.FloatingStorageTvp AS TABLE
    (
        [RunDate]             DATE             NOT NULL,
        [RowId]               UNIQUEIDENTIFIER NOT NULL,
        [IMO]                 VARCHAR(50)      NULL,
        [ReferenceDate]       DATE             NULL,
        [VesselName]          VARCHAR(256)     NULL,
        [VesselClass]         VARCHAR(50)      NULL,
        [StartDate]           DATETIME2(7)     NULL,
        [EndDate]             DATETIME2(7)     NULL,
        [QuantityKiloBarrels] DECIMAL(18,8)    NULL,
        [Area]                VARCHAR(256)     NULL,
        [FileName]            VARCHAR(100)     NOT NULL,
        [Checksum]            INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.FlowTvp  ->  arm.Flow
-- 16 columns: the table's 17 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.FlowTvp') IS NULL
BEGIN
    CREATE TYPE arm.FlowTvp AS TABLE
    (
        [RunDate]                DATE             NOT NULL,
        [RowId]                  UNIQUEIDENTIFIER NOT NULL,
        [ReferenceDate]          DATE             NULL,
        [OriginCountryName]      VARCHAR(50)      NULL,
        [DestinationCountryName] VARCHAR(50)      NULL,
        [GroupByDateIndicator]   VARCHAR(50)      NULL,
        [OriginSubCountry]       VARCHAR(50)      NULL,
        [DestinationSubCountry]  VARCHAR(50)      NULL,
        [GradeName]              VARCHAR(50)      NULL,
        [GradeCategory]          VARCHAR(50)      NULL,
        [ApiGravity]             DECIMAL(18,8)    NULL,
        [SulphurContent]         DECIMAL(18,8)    NULL,
        [QuantityKBD]            DECIMAL(18,8)    NULL,
        [QuantityKBBL]           DECIMAL(18,8)    NULL,
        [FileName]               VARCHAR(100)     NOT NULL,
        [Checksum]               INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.GlobalBalanceTvp  ->  arm.GlobalBalance
-- 9 columns: the table's 10 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.GlobalBalanceTvp') IS NULL
BEGIN
    CREATE TYPE arm.GlobalBalanceTvp AS TABLE
    (
        [RunDate]       DATE             NOT NULL,
        [RowId]         UNIQUEIDENTIFIER NOT NULL,
        [GroupName]     VARCHAR(50)      NULL,
        [ReferenceDate] DATETIME2(7)     NULL,
        [FlowBreakdown] VARCHAR(50)      NULL,
        [UnitMeasure]   VARCHAR(50)      NULL,
        [ObservedValue] DECIMAL(28,8)    NULL,
        [FileName]      VARCHAR(100)     NOT NULL,
        [Checksum]      INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.OilFieldProductionTvp  ->  arm.OilFieldProduction
-- 10 columns: the table's 11 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.OilFieldProductionTvp') IS NULL
BEGIN
    CREATE TYPE arm.OilFieldProductionTvp AS TABLE
    (
        [RunDate]       DATE             NOT NULL,
        [RowId]         UNIQUEIDENTIFIER NOT NULL,
        [ReferenceDate] DATE             NULL,
        [OilFieldName]  VARCHAR(50)      NULL,
        [UnitMeasure]   VARCHAR(50)      NULL,
        [PortName]      VARCHAR(256)     NULL,
        [CountryName]   VARCHAR(50)      NULL,
        [ObservedValue] DECIMAL(18,8)    NULL,
        [FileName]      VARCHAR(100)     NOT NULL,
        [Checksum]      INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.RegionalBalanceTvp  ->  arm.RegionalBalance
-- 10 columns: the table's 11 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.RegionalBalanceTvp') IS NULL
BEGIN
    CREATE TYPE arm.RegionalBalanceTvp AS TABLE
    (
        [RunDate]           DATE             NOT NULL,
        [RowId]             UNIQUEIDENTIFIER NOT NULL,
        [GroupName]         VARCHAR(50)      NULL,
        [ReferenceDate]     DATETIME2(7)     NULL,
        [FlowBreakdown]     VARCHAR(50)      NULL,
        [UnitMeasure]       VARCHAR(50)      NULL,
        [ObservedValue]     DECIMAL(28,8)    NULL,
        [GeneralizedSource] VARCHAR(50)      NULL,
        [FileName]          VARCHAR(100)     NOT NULL,
        [Checksum]          INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.SupplyDemandTvp  ->  arm.SupplyDemand
-- 11 columns: the table's 12 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.SupplyDemandTvp') IS NULL
BEGIN
    CREATE TYPE arm.SupplyDemandTvp AS TABLE
    (
        [RunDate]           DATE             NOT NULL,
        [RowId]             UNIQUEIDENTIFIER NOT NULL,
        [CountryISOCode]    VARCHAR(50)      NULL,
        [ReferenceDate]     DATETIME2(7)     NULL,
        [FlowBreakdown]     VARCHAR(50)      NULL,
        [CountryName]       VARCHAR(250)     NULL,
        [UnitMeasure]       VARCHAR(50)      NULL,
        [ObservedValue]     DECIMAL(28,8)    NULL,
        [GeneralizedSource] VARCHAR(50)      NULL,
        [FileName]          VARCHAR(100)     NOT NULL,
        [Checksum]          INT              NOT NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.TerminalTvp  ->  arm.Terminal
-- 10 columns: the table's 11 less ModifiedAtUtc.
--
-- ReferenceDate is DATETIME2(2) here, matching 001 -- the other balance feeds
-- use (7). That is as supplied, and the checksum renders this column at the
-- stored precision so an unchanged row does not look changed.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.TerminalTvp') IS NULL
BEGIN
    CREATE TYPE arm.TerminalTvp AS TABLE
    (
        [RunDate]       DATE             NOT NULL,
        [RowId]         UNIQUEIDENTIFIER NOT NULL,
        [TerminalName]  VARCHAR(50)      NULL,
        [ReferenceDate] DATETIME2(2)     NULL,
        [FlowBreakdown] VARCHAR(50)      NULL,
        [UnitMeasure]   VARCHAR(50)      NULL,
        [ObservedValue] DECIMAL(18,8)    NULL,
        [CountryName]   VARCHAR(50)      NULL,
        [FileName]      VARCHAR(100)     NOT NULL,
        [Checksum]      INT              NOT NULL
    );
END
GO
