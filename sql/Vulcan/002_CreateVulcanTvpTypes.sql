-- =============================================================================
-- 002_CreateVulcanTvpTypes.sql
-- Table-valued parameter types for the Vulcan bulk merges.
-- =============================================================================

IF TYPE_ID('dbo.UnderConstructionTvp') IS NULL
CREATE TYPE dbo.UnderConstructionTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantId NVARCHAR(100) NULL, PlantName NVARCHAR(400) NULL,
    Technology NVARCHAR(200) NULL, NameplateCapacity DECIMAL(18,4) NULL, VulcanStatus NVARCHAR(50) NULL,
    ProjectRank DECIMAL(5,2) NULL, DateVulcanEarliestOnline DATE NULL, DateVulcanLatestOnline DATE NULL,
    StateCode NVARCHAR(50) NULL, BalancingAuthority NVARCHAR(100) NULL, Latitude FLOAT NULL, Longitude FLOAT NULL,
    DateVulcanStatusChange DATE NULL, DateImage DATE NULL, PRIMARY KEY (SynmaxId)
);
GO

IF TYPE_ID('dbo.DataCentersTvp') IS NULL
CREATE TYPE dbo.DataCentersTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantId NVARCHAR(100) NULL, PlantName NVARCHAR(400) NULL,
    UnitId NVARCHAR(100) NULL, UnitName NVARCHAR(400) NULL, OwnerName NVARCHAR(400) NULL,
    DataCenterType NVARCHAR(100) NULL, UnitCapacity DECIMAL(18,4) NULL, VulcanStatus NVARCHAR(50) NULL,
    StateCode NVARCHAR(50) NULL, BalancingAuthority NVARCHAR(100) NULL, DateVulcanEarliestOnline DATE NULL,
    DateVulcanLatestOnline DATE NULL, ModifiedAt DATE NULL, PRIMARY KEY (SynmaxId)
);
GO

IF TYPE_ID('dbo.LngProjectsTvp') IS NULL
CREATE TYPE dbo.LngProjectsTvp AS TABLE
(
    PlantName NVARCHAR(400) NOT NULL, PhaseNumber INT NOT NULL, EntityName NVARCHAR(400) NULL,
    Technology NVARCHAR(200) NULL, NameplateCapacity FLOAT NULL, CapacityUnit NVARCHAR(50) NULL,
    Trains INT NULL, VulcanStatus NVARCHAR(50) NULL, DateVulcanEarliestOnline DATE NULL,
    DateVulcanLatestOnline DATE NULL, Latitude FLOAT NULL, Longitude FLOAT NULL, Observation NVARCHAR(MAX) NULL,
    DateVulcanStatusChange DATE NULL, DateImage DATE NULL, ModifiedAt DATE NULL,
    PRIMARY KEY (PlantName, PhaseNumber)
);
GO

IF TYPE_ID('dbo.ProjectRankingsTvp') IS NULL
CREATE TYPE dbo.ProjectRankingsTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantId BIGINT NULL, GeneratorId NVARCHAR(100) NULL,
    FinalRank DECIMAL(5,2) NULL, DateVulcanProposedV2Online DATE NULL, DateVulcanProposedOnline DATE NULL,
    DateUpdated DATE NULL, PRIMARY KEY (SynmaxId)
);
GO

IF TYPE_ID('dbo.MetadataHistoryTvp') IS NULL
CREATE TYPE dbo.MetadataHistoryTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantName NVARCHAR(400) NULL, Technology NVARCHAR(200) NULL,
    NameplateCapacity FLOAT NULL, StateCode NVARCHAR(50) NULL, County NVARCHAR(200) NULL,
    DatePlannedOperations DATE NULL, PlantStatus NVARCHAR(100) NULL, DateEiaUpdated DATE NULL,
    DaysPlannedOperationMinusFirstSeenPlannedOperation FLOAT NULL, Latitude FLOAT NULL, Longitude FLOAT NULL,
    BalancingAuthorityCode NVARCHAR(100) NULL, SectorName NVARCHAR(200) NULL, PRIMARY KEY (SynmaxId)
);
GO
