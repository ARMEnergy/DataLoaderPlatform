-- =============================================================================
-- 001_CreateVulcanSchema.sql
-- Vulcan loader tables (dbo schema) + incremental watermark table.
-- Runs against the Vulcan database (Loaders:Vulcan:ConnectionString).
--
-- Standing conventions (see .claude/agents/database-developer.md):
--   * Every table starts with  Id INT IDENTITY(1,1)  (PK) then
--     DateCreated DATETIME NOT NULL DEFAULT GETDATE().
--   * Natural/business keys are enforced with a named UNIQUE constraint.
--   * Numeric measures use DECIMAL (never FLOAT). Coordinates DECIMAL(9,6),
--     capacities DECIMAL(18,4), ranks DECIMAL(18,10).
-- =============================================================================
-- Provisioning (before the first run):
--   1. Create the Vulcan database.
--   2. Run sql/Vulcan/001_, 002_, 003_ in order.
--   3. Set the API key env var: DATALOADER_Loaders__Vulcan__ApiKey=<key>
--   4. Run DataLoader.Host.exe Vulcan
--
-- NOTE: the guards below create objects only when absent. To apply type/column
-- changes to an existing database, drop the Vulcan objects (or the database)
-- and re-run 001-003.
-- =============================================================================

-- Incremental resume state: one row per source table.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoadWatermark' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LoadWatermark
    (
        Id             INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_LoadWatermark PRIMARY KEY,
        DateCreated    DATETIME      NOT NULL CONSTRAINT DF_LoadWatermark_DateCreated DEFAULT GETDATE(),
        TableName      NVARCHAR(100) NOT NULL CONSTRAINT UQ_LoadWatermark_TableName UNIQUE,
        WatermarkValue DATE          NULL,
        UpdatedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_LoadWatermark_Updated DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UnderConstruction' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.UnderConstruction
    (
        Id                       INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_UnderConstruction PRIMARY KEY,
        DateCreated              DATETIME      NOT NULL CONSTRAINT DF_UnderConstruction_DateCreated DEFAULT GETDATE(),
        SynmaxId                 NVARCHAR(100) NOT NULL CONSTRAINT UQ_UnderConstruction_SynmaxId UNIQUE,
        PlantId                  NVARCHAR(100) NULL,
        PlantName                NVARCHAR(400) NULL,
        Technology               NVARCHAR(200) NULL,
        NameplateCapacity        DECIMAL(18,4) NULL,
        VulcanStatus             NVARCHAR(50)  NULL,
        ProjectRank              DECIMAL(18,10) NULL,
        DateVulcanEarliestOnline DATE          NULL,
        DateVulcanLatestOnline   DATE          NULL,
        StateCode                NVARCHAR(50)  NULL,
        BalancingAuthority       NVARCHAR(100) NULL,
        Latitude                 DECIMAL(9,6)  NULL,
        Longitude                DECIMAL(9,6)  NULL,
        DateVulcanStatusChange   DATE          NULL,
        DateImage                DATE          NULL,
        LoadedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_UnderConstruction_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO

-- DataCenters is re-created unconditionally: the source endpoint returns ~40
-- columns and earlier versions of this script captured only a subset. Dropping
-- and rebuilding is safe because the loader re-populates it from the API. The
-- dependent TVP + merge proc are dropped/recreated in 002/003.
DROP TABLE IF EXISTS dbo.DataCenters;
GO
CREATE TABLE dbo.DataCenters
(
    Id                                     INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_DataCenters PRIMARY KEY,
    DateCreated                            DATETIME       NOT NULL CONSTRAINT DF_DataCenters_DateCreated DEFAULT GETDATE(),
    SynmaxId                               NVARCHAR(100)  NOT NULL CONSTRAINT UQ_DataCenters_SynmaxId UNIQUE,
    PlantId                                NVARCHAR(100)  NULL,
    PlantName                              NVARCHAR(400)  NULL,
    UnitId                                 NVARCHAR(100)  NULL,
    UnitName                               NVARCHAR(400)  NULL,
    OwnerName                              NVARCHAR(400)  NULL,
    StateCode                              NVARCHAR(50)   NULL,
    BalancingAuthority                     NVARCHAR(100)  NULL,
    Country                                NVARCHAR(100)  NULL,
    MarketRegion                           NVARCHAR(100)  NULL,
    DataCenterType                         NVARCHAR(100)  NULL,
    UnitCapacity                           DECIMAL(18,4)  NULL,
    UnitStatus                             NVARCHAR(50)   NULL,
    PlantStatus                            NVARCHAR(50)   NULL,
    VulcanStatus                           NVARCHAR(50)   NULL,
    Source                                 NVARCHAR(100)  NULL,
    BtmGeneration                          BIT            NULL,
    BtmClassification                      NVARCHAR(50)   NULL,
    Observation                            NVARCHAR(MAX)  NULL,  -- free-text analyst note, unbounded length
    DatePlannedOperation                   DATE           NULL,
    DateVulcanStatusChange                 DATE           NULL,
    DateImage                              DATE           NULL,
    DateImageReviewed                      DATE           NULL,
    DateConstructionStart                  DATE           NULL,
    DateLandCleared                        DATE           NULL,
    DateFirstStructures                    DATE           NULL,
    DateConstruction50PercentComplete      DATE           NULL,
    DateConstructionCompleted              DATE           NULL,
    DateVulcanEarliestPlus7                DATE           NULL,
    DateVulcanEarliestOnline               DATE           NULL,
    DateVulcanLatestOnline                 DATE           NULL,
    DateVulcanMedianOnline                 DATE           NULL,
    DaysIirMinusVulcanEarliestOnline       DECIMAL(18,4)  NULL,
    DaysIirMinusVulcanLatestOnline         DECIMAL(18,4)  NULL,
    DateProjectedEarliestLandClear         DATE           NULL,
    DateProjectedMedianLandClear           DATE           NULL,
    DateProjectedEarliestFirstStructures   DATE           NULL,
    DateProjectedMedianFirstStructures     DATE           NULL,
    WeeklyProgressIndicator                DECIMAL(18,4)  NULL,
    TotalWpi                               DECIMAL(18,4)  NULL,
    WpiOnlineDate                          DATE           NULL,
    CreatedAt                              DATE           NULL,
    ModifiedAt                             DATE           NULL,
    LoadedAtUtc                            DATETIME2(3)   NOT NULL CONSTRAINT DF_DataCenters_Loaded DEFAULT SYSUTCDATETIME()
);
GO

-- The table was just rebuilt empty; clear its incremental watermark so the next
-- run performs a full backfill rather than resuming from a stale high-water mark.
DELETE FROM dbo.LoadWatermark WHERE TableName = N'datacenters';
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LngProjects' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LngProjects
    (
        Id                       INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_LngProjects PRIMARY KEY,
        DateCreated              DATETIME      NOT NULL CONSTRAINT DF_LngProjects_DateCreated DEFAULT GETDATE(),
        PlantName                NVARCHAR(400) NOT NULL,
        PhaseNumber              INT           NOT NULL,
        EntityName               NVARCHAR(400) NULL,
        Technology               NVARCHAR(200) NULL,
        NameplateCapacity        DECIMAL(18,4) NULL,
        CapacityUnit             NVARCHAR(50)  NULL,
        Trains                   INT           NULL,
        VulcanStatus             NVARCHAR(50)  NULL,
        DateVulcanEarliestOnline DATE          NULL,
        DateVulcanLatestOnline   DATE          NULL,
        Latitude                 DECIMAL(9,6)  NULL,
        Longitude                DECIMAL(9,6)  NULL,
        Observation              NVARCHAR(MAX) NULL,  -- free-text analyst note, unbounded length
        DateVulcanStatusChange   DATE          NULL,
        DateImage                DATE          NULL,
        ModifiedAt               DATE          NULL,
        LoadedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_LngProjects_Loaded DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_LngProjects_PlantName_PhaseNumber UNIQUE (PlantName, PhaseNumber)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProjectRankings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ProjectRankings
    (
        Id                          INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProjectRankings PRIMARY KEY,
        DateCreated                 DATETIME      NOT NULL CONSTRAINT DF_ProjectRankings_DateCreated DEFAULT GETDATE(),
        SynmaxId                    NVARCHAR(100) NOT NULL CONSTRAINT UQ_ProjectRankings_SynmaxId UNIQUE,
        PlantId                     BIGINT        NULL,
        GeneratorId                 NVARCHAR(100) NULL,
        FinalRank                   DECIMAL(18,10) NULL,
        DateVulcanProposedV2Online  DATE          NULL,
        DateVulcanProposedOnline    DATE          NULL,
        DateUpdated                 DATE          NULL,
        LoadedAtUtc                 DATETIME2(3)  NOT NULL CONSTRAINT DF_ProjectRankings_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MetadataHistory' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.MetadataHistory
    (
        Id                                                    INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_MetadataHistory PRIMARY KEY,
        DateCreated                                           DATETIME      NOT NULL CONSTRAINT DF_MetadataHistory_DateCreated DEFAULT GETDATE(),
        SynmaxId                                              NVARCHAR(100) NOT NULL CONSTRAINT UQ_MetadataHistory_SynmaxId UNIQUE,
        PlantName                                             NVARCHAR(400) NULL,
        Technology                                            NVARCHAR(200) NULL,
        NameplateCapacity                                     DECIMAL(18,4) NULL,
        StateCode                                             NVARCHAR(50)  NULL,
        County                                                NVARCHAR(200) NULL,
        DatePlannedOperations                                 DATE          NULL,
        PlantStatus                                           NVARCHAR(100) NULL,
        DateEiaUpdated                                        DATE          NULL,
        DaysPlannedOperationMinusFirstSeenPlannedOperation    DECIMAL(18,4) NULL,
        Latitude                                              DECIMAL(9,6)  NULL,
        Longitude                                             DECIMAL(9,6)  NULL,
        BalancingAuthorityCode                                NVARCHAR(100) NULL,
        SectorName                                            NVARCHAR(200) NULL,
        LoadedAtUtc                                           DATETIME2(3)  NOT NULL CONSTRAINT DF_MetadataHistory_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO
