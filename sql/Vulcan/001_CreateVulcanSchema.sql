-- =============================================================================
-- 001_CreateVulcanSchema.sql
-- Vulcan loader tables (dbo schema) + incremental watermark table.
-- Runs against the Vulcan database (Loaders:Vulcan:ConnectionString).
-- =============================================================================

-- Incremental resume state: one row per source table.
IF OBJECT_ID('dbo.LoadWatermark', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadWatermark
    (
        TableName      NVARCHAR(100) NOT NULL CONSTRAINT PK_LoadWatermark PRIMARY KEY,
        WatermarkValue DATE          NULL,
        UpdatedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_LoadWatermark_Updated DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID('dbo.UnderConstruction', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UnderConstruction
    (
        SynmaxId                 NVARCHAR(100) NOT NULL CONSTRAINT PK_UnderConstruction PRIMARY KEY,
        PlantId                  NVARCHAR(100) NULL,
        PlantName                NVARCHAR(400) NULL,
        Technology               NVARCHAR(200) NULL,
        NameplateCapacity        DECIMAL(18,4) NULL,
        VulcanStatus             NVARCHAR(50)  NULL,
        ProjectRank              DECIMAL(5,2)  NULL,
        DateVulcanEarliestOnline DATE          NULL,
        DateVulcanLatestOnline   DATE          NULL,
        StateCode                NVARCHAR(50)  NULL,
        BalancingAuthority       NVARCHAR(100) NULL,
        Latitude                 FLOAT         NULL,
        Longitude                FLOAT         NULL,
        DateVulcanStatusChange   DATE          NULL,
        DateImage                DATE          NULL,
        LoadedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_UnderConstruction_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID('dbo.DataCenters', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DataCenters
    (
        SynmaxId                 NVARCHAR(100) NOT NULL CONSTRAINT PK_DataCenters PRIMARY KEY,
        PlantId                  NVARCHAR(100) NULL,
        PlantName                NVARCHAR(400) NULL,
        UnitId                   NVARCHAR(100) NULL,
        UnitName                 NVARCHAR(400) NULL,
        OwnerName                NVARCHAR(400) NULL,
        DataCenterType           NVARCHAR(100) NULL,
        UnitCapacity             DECIMAL(18,4) NULL,
        VulcanStatus             NVARCHAR(50)  NULL,
        StateCode                NVARCHAR(50)  NULL,
        BalancingAuthority       NVARCHAR(100) NULL,
        DateVulcanEarliestOnline DATE          NULL,
        DateVulcanLatestOnline   DATE          NULL,
        ModifiedAt               DATE          NULL,
        LoadedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_DataCenters_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID('dbo.LngProjects', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LngProjects
    (
        PlantName                NVARCHAR(400) NOT NULL,
        PhaseNumber              INT           NOT NULL,
        EntityName               NVARCHAR(400) NULL,
        Technology               NVARCHAR(200) NULL,
        NameplateCapacity        FLOAT         NULL,
        CapacityUnit             NVARCHAR(50)  NULL,
        Trains                   INT           NULL,
        VulcanStatus             NVARCHAR(50)  NULL,
        DateVulcanEarliestOnline DATE          NULL,
        DateVulcanLatestOnline   DATE          NULL,
        Latitude                 FLOAT         NULL,
        Longitude                FLOAT         NULL,
        Observation              NVARCHAR(MAX) NULL,
        DateVulcanStatusChange   DATE          NULL,
        DateImage                DATE          NULL,
        ModifiedAt               DATE          NULL,
        LoadedAtUtc              DATETIME2(3)  NOT NULL CONSTRAINT DF_LngProjects_Loaded DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LngProjects PRIMARY KEY (PlantName, PhaseNumber)
    );
END
GO

IF OBJECT_ID('dbo.ProjectRankings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProjectRankings
    (
        SynmaxId                    NVARCHAR(100) NOT NULL CONSTRAINT PK_ProjectRankings PRIMARY KEY,
        PlantId                     BIGINT        NULL,
        GeneratorId                 NVARCHAR(100) NULL,
        FinalRank                   DECIMAL(5,2)  NULL,
        DateVulcanProposedV2Online  DATE          NULL,
        DateVulcanProposedOnline    DATE          NULL,
        DateUpdated                 DATE          NULL,
        LoadedAtUtc                 DATETIME2(3)  NOT NULL CONSTRAINT DF_ProjectRankings_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID('dbo.MetadataHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MetadataHistory
    (
        SynmaxId                                              NVARCHAR(100) NOT NULL CONSTRAINT PK_MetadataHistory PRIMARY KEY,
        PlantName                                             NVARCHAR(400) NULL,
        Technology                                            NVARCHAR(200) NULL,
        NameplateCapacity                                     FLOAT         NULL,
        StateCode                                             NVARCHAR(50)  NULL,
        County                                                NVARCHAR(200) NULL,
        DatePlannedOperations                                 DATE          NULL,
        PlantStatus                                           NVARCHAR(100) NULL,
        DateEiaUpdated                                        DATE          NULL,
        DaysPlannedOperationMinusFirstSeenPlannedOperation    FLOAT         NULL,
        Latitude                                              FLOAT         NULL,
        Longitude                                             FLOAT         NULL,
        BalancingAuthorityCode                                NVARCHAR(100) NULL,
        SectorName                                            NVARCHAR(200) NULL,
        LoadedAtUtc                                           DATETIME2(3)  NOT NULL CONSTRAINT DF_MetadataHistory_Loaded DEFAULT SYSUTCDATETIME()
    );
END
GO
