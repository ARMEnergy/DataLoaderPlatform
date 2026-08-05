-- =============================================================================
-- 002_CreateVulcanTvpTypes.sql
-- Table-valued parameter types for the Vulcan bulk merges.
-- These mirror the incoming record shape (business columns only); the Id and
-- DateCreated columns live on the tables and are populated by IDENTITY / DEFAULT
-- during the merge, so they are intentionally absent here. Column order must
-- match the DataTable built in DataLoader.Vulcan.Sinks.
-- Numeric types match 001: coordinates DECIMAL(9,6), capacities DECIMAL(18,4),
-- ranks DECIMAL(18,10).
--
-- NOTE: the guards create types only when absent. To change a type on an
-- existing database, drop the dependent merge procedures (003) and these types,
-- then re-run 002-003 (or re-provision the database).
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = 'UnderConstructionTvp' AND schema_id = SCHEMA_ID('dbo'))
CREATE TYPE dbo.UnderConstructionTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantId NVARCHAR(100) NULL, PlantName NVARCHAR(400) NULL,
    Technology NVARCHAR(200) NULL, NameplateCapacity DECIMAL(18,4) NULL, VulcanStatus NVARCHAR(50) NULL,
    ProjectRank DECIMAL(18,10) NULL, DateVulcanEarliestOnline DATE NULL, DateVulcanLatestOnline DATE NULL,
    StateCode NVARCHAR(50) NULL, BalancingAuthority NVARCHAR(100) NULL, Latitude DECIMAL(9,6) NULL, Longitude DECIMAL(9,6) NULL,
    DateVulcanStatusChange DATE NULL, DateImage DATE NULL, PRIMARY KEY (SynmaxId)
);
GO

-- DataCentersTvp is dropped and recreated to match the expanded DataCenters
-- table. The merge proc that consumes it must be dropped first (it depends on
-- the type); 003 recreates the proc.
DROP PROCEDURE IF EXISTS dbo.usp_BulkMergeDataCenters;
GO
DROP TYPE IF EXISTS dbo.DataCentersTvp;
GO
CREATE TYPE dbo.DataCentersTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantId NVARCHAR(100) NULL, PlantName NVARCHAR(400) NULL,
    UnitId NVARCHAR(100) NULL, UnitName NVARCHAR(400) NULL, OwnerName NVARCHAR(400) NULL,
    StateCode NVARCHAR(50) NULL, BalancingAuthority NVARCHAR(100) NULL, Country NVARCHAR(100) NULL,
    MarketRegion NVARCHAR(100) NULL, DataCenterType NVARCHAR(100) NULL, UnitCapacity DECIMAL(18,4) NULL,
    UnitStatus NVARCHAR(50) NULL, PlantStatus NVARCHAR(50) NULL, VulcanStatus NVARCHAR(50) NULL,
    Source NVARCHAR(100) NULL, BtmGeneration BIT NULL, BtmClassification NVARCHAR(50) NULL,
    Observation NVARCHAR(MAX) NULL, DatePlannedOperation DATE NULL, DateVulcanStatusChange DATE NULL,
    DateImage DATE NULL, DateImageReviewed DATE NULL, DateConstructionStart DATE NULL,
    DateLandCleared DATE NULL, DateFirstStructures DATE NULL, DateConstruction50PercentComplete DATE NULL,
    DateConstructionCompleted DATE NULL, DateVulcanEarliestPlus7 DATE NULL, DateVulcanEarliestOnline DATE NULL,
    DateVulcanLatestOnline DATE NULL, DateVulcanMedianOnline DATE NULL, DaysIirMinusVulcanEarliestOnline DECIMAL(18,4) NULL,
    DaysIirMinusVulcanLatestOnline DECIMAL(18,4) NULL, DateProjectedEarliestLandClear DATE NULL,
    DateProjectedMedianLandClear DATE NULL, DateProjectedEarliestFirstStructures DATE NULL,
    DateProjectedMedianFirstStructures DATE NULL, WeeklyProgressIndicator DECIMAL(18,4) NULL,
    TotalWpi DECIMAL(18,4) NULL, WpiOnlineDate DATE NULL, CreatedAt DATE NULL, ModifiedAt DATE NULL,
    PRIMARY KEY (SynmaxId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = 'LngProjectsTvp' AND schema_id = SCHEMA_ID('dbo'))
CREATE TYPE dbo.LngProjectsTvp AS TABLE
(
    PlantName NVARCHAR(400) NOT NULL, PhaseNumber INT NOT NULL, EntityName NVARCHAR(400) NULL,
    Technology NVARCHAR(200) NULL, NameplateCapacity DECIMAL(18,4) NULL, CapacityUnit NVARCHAR(50) NULL,
    Trains INT NULL, VulcanStatus NVARCHAR(50) NULL, DateVulcanEarliestOnline DATE NULL,
    DateVulcanLatestOnline DATE NULL, Latitude DECIMAL(9,6) NULL, Longitude DECIMAL(9,6) NULL, Observation NVARCHAR(MAX) NULL,
    DateVulcanStatusChange DATE NULL, DateImage DATE NULL, ModifiedAt DATE NULL,
    PRIMARY KEY (PlantName, PhaseNumber)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = 'ProjectRankingsTvp' AND schema_id = SCHEMA_ID('dbo'))
CREATE TYPE dbo.ProjectRankingsTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantId BIGINT NULL, GeneratorId NVARCHAR(100) NULL,
    FinalRank DECIMAL(18,10) NULL, DateVulcanProposedV2Online DATE NULL, DateVulcanProposedOnline DATE NULL,
    DateUpdated DATE NULL, PRIMARY KEY (SynmaxId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = 'MetadataHistoryTvp' AND schema_id = SCHEMA_ID('dbo'))
CREATE TYPE dbo.MetadataHistoryTvp AS TABLE
(
    SynmaxId NVARCHAR(100) NOT NULL, PlantName NVARCHAR(400) NULL, Technology NVARCHAR(200) NULL,
    NameplateCapacity DECIMAL(18,4) NULL, StateCode NVARCHAR(50) NULL, County NVARCHAR(200) NULL,
    DatePlannedOperations DATE NULL, PlantStatus NVARCHAR(100) NULL, DateEiaUpdated DATE NULL,
    DaysPlannedOperationMinusFirstSeenPlannedOperation DECIMAL(18,4) NULL, Latitude DECIMAL(9,6) NULL, Longitude DECIMAL(9,6) NULL,
    BalancingAuthorityCode NVARCHAR(100) NULL, SectorName NVARCHAR(200) NULL, PRIMARY KEY (SynmaxId)
);
GO
