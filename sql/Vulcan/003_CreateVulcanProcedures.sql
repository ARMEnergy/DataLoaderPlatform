-- =============================================================================
-- 003_CreateVulcanProcedures.sql
-- Watermark accessors + per-table bulk-merge procedures.
-- Each merge proc: MERGE into the target, SELECT the merged row count,
-- then advance dbo.LoadWatermark to MAX(watermark column) of the incoming rows.
-- Procedures use CREATE OR ALTER so the script is re-runnable.
-- The MERGE INSERT lists omit Id and DateCreated: those are populated by the
-- table's IDENTITY and DEFAULT.
-- =============================================================================

CREATE OR ALTER PROCEDURE dbo.usp_GetVulcanWatermark @TableName NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT WatermarkValue FROM dbo.LoadWatermark WHERE TableName = @TableName;
END
GO

-- Helper used by every merge proc to advance a table's watermark.
CREATE OR ALTER PROCEDURE dbo.usp_SetVulcanWatermark @TableName NVARCHAR(100), @Value DATE
AS
BEGIN
    SET NOCOUNT ON;
    IF @Value IS NULL RETURN;
    MERGE dbo.LoadWatermark AS tgt
    USING (SELECT @TableName AS TableName, @Value AS WatermarkValue) AS src
       ON tgt.TableName = src.TableName
    WHEN MATCHED AND (tgt.WatermarkValue IS NULL OR src.WatermarkValue > tgt.WatermarkValue) THEN
        UPDATE SET WatermarkValue = src.WatermarkValue, UpdatedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TableName, WatermarkValue) VALUES (src.TableName, src.WatermarkValue);
END
GO

-- ---- UnderConstruction (watermark: DateImage) ----
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeUnderConstruction @Records dbo.UnderConstructionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.UnderConstruction AS tgt
    USING @Records AS src ON tgt.SynmaxId = src.SynmaxId
    WHEN MATCHED THEN UPDATE SET
        PlantId = src.PlantId, PlantName = src.PlantName, Technology = src.Technology,
        NameplateCapacity = src.NameplateCapacity, VulcanStatus = src.VulcanStatus, ProjectRank = src.ProjectRank,
        DateVulcanEarliestOnline = src.DateVulcanEarliestOnline, DateVulcanLatestOnline = src.DateVulcanLatestOnline,
        StateCode = src.StateCode, BalancingAuthority = src.BalancingAuthority, Latitude = src.Latitude,
        Longitude = src.Longitude, DateVulcanStatusChange = src.DateVulcanStatusChange, DateImage = src.DateImage,
        LoadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SynmaxId, PlantId, PlantName, Technology, NameplateCapacity, VulcanStatus, ProjectRank,
                DateVulcanEarliestOnline, DateVulcanLatestOnline, StateCode, BalancingAuthority, Latitude,
                Longitude, DateVulcanStatusChange, DateImage)
        VALUES (src.SynmaxId, src.PlantId, src.PlantName, src.Technology, src.NameplateCapacity, src.VulcanStatus,
                src.ProjectRank, src.DateVulcanEarliestOnline, src.DateVulcanLatestOnline, src.StateCode,
                src.BalancingAuthority, src.Latitude, src.Longitude, src.DateVulcanStatusChange, src.DateImage);
    DECLARE @merged INT = @@ROWCOUNT;
    DECLARE @Value DATE
    SET @Value = (SELECT MAX(DateImage) FROM @Records)
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'under_construction', @Value = @Value;
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- DataCenters (watermark: ModifiedAt) ----
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeDataCenters @Records dbo.DataCentersTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.DataCenters AS tgt
    USING @Records AS src ON tgt.SynmaxId = src.SynmaxId
    WHEN MATCHED THEN UPDATE SET
        PlantId = src.PlantId, PlantName = src.PlantName, UnitId = src.UnitId, UnitName = src.UnitName,
        OwnerName = src.OwnerName, StateCode = src.StateCode, BalancingAuthority = src.BalancingAuthority,
        Country = src.Country, MarketRegion = src.MarketRegion, DataCenterType = src.DataCenterType,
        UnitCapacity = src.UnitCapacity, UnitStatus = src.UnitStatus, PlantStatus = src.PlantStatus,
        VulcanStatus = src.VulcanStatus, Source = src.Source, BtmGeneration = src.BtmGeneration,
        BtmClassification = src.BtmClassification, Observation = src.Observation,
        DatePlannedOperation = src.DatePlannedOperation, DateVulcanStatusChange = src.DateVulcanStatusChange,
        DateImage = src.DateImage, DateImageReviewed = src.DateImageReviewed,
        DateConstructionStart = src.DateConstructionStart, DateLandCleared = src.DateLandCleared,
        DateFirstStructures = src.DateFirstStructures,
        DateConstruction50PercentComplete = src.DateConstruction50PercentComplete,
        DateConstructionCompleted = src.DateConstructionCompleted,
        DateVulcanEarliestPlus7 = src.DateVulcanEarliestPlus7, DateVulcanEarliestOnline = src.DateVulcanEarliestOnline,
        DateVulcanLatestOnline = src.DateVulcanLatestOnline, DateVulcanMedianOnline = src.DateVulcanMedianOnline,
        DaysIirMinusVulcanEarliestOnline = src.DaysIirMinusVulcanEarliestOnline,
        DaysIirMinusVulcanLatestOnline = src.DaysIirMinusVulcanLatestOnline,
        DateProjectedEarliestLandClear = src.DateProjectedEarliestLandClear,
        DateProjectedMedianLandClear = src.DateProjectedMedianLandClear,
        DateProjectedEarliestFirstStructures = src.DateProjectedEarliestFirstStructures,
        DateProjectedMedianFirstStructures = src.DateProjectedMedianFirstStructures,
        WeeklyProgressIndicator = src.WeeklyProgressIndicator, TotalWpi = src.TotalWpi,
        WpiOnlineDate = src.WpiOnlineDate, CreatedAt = src.CreatedAt, ModifiedAt = src.ModifiedAt,
        LoadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SynmaxId, PlantId, PlantName, UnitId, UnitName, OwnerName, StateCode, BalancingAuthority,
                Country, MarketRegion, DataCenterType, UnitCapacity, UnitStatus, PlantStatus, VulcanStatus,
                Source, BtmGeneration, BtmClassification, Observation, DatePlannedOperation,
                DateVulcanStatusChange, DateImage, DateImageReviewed, DateConstructionStart, DateLandCleared,
                DateFirstStructures, DateConstruction50PercentComplete, DateConstructionCompleted,
                DateVulcanEarliestPlus7, DateVulcanEarliestOnline, DateVulcanLatestOnline, DateVulcanMedianOnline,
                DaysIirMinusVulcanEarliestOnline, DaysIirMinusVulcanLatestOnline, DateProjectedEarliestLandClear,
                DateProjectedMedianLandClear, DateProjectedEarliestFirstStructures, DateProjectedMedianFirstStructures,
                WeeklyProgressIndicator, TotalWpi, WpiOnlineDate, CreatedAt, ModifiedAt)
        VALUES (src.SynmaxId, src.PlantId, src.PlantName, src.UnitId, src.UnitName, src.OwnerName, src.StateCode,
                src.BalancingAuthority, src.Country, src.MarketRegion, src.DataCenterType, src.UnitCapacity,
                src.UnitStatus, src.PlantStatus, src.VulcanStatus, src.Source, src.BtmGeneration,
                src.BtmClassification, src.Observation, src.DatePlannedOperation, src.DateVulcanStatusChange,
                src.DateImage, src.DateImageReviewed, src.DateConstructionStart, src.DateLandCleared,
                src.DateFirstStructures, src.DateConstruction50PercentComplete, src.DateConstructionCompleted,
                src.DateVulcanEarliestPlus7, src.DateVulcanEarliestOnline, src.DateVulcanLatestOnline,
                src.DateVulcanMedianOnline, src.DaysIirMinusVulcanEarliestOnline, src.DaysIirMinusVulcanLatestOnline,
                src.DateProjectedEarliestLandClear, src.DateProjectedMedianLandClear,
                src.DateProjectedEarliestFirstStructures, src.DateProjectedMedianFirstStructures,
                src.WeeklyProgressIndicator, src.TotalWpi, src.WpiOnlineDate, src.CreatedAt, src.ModifiedAt);
    DECLARE @merged INT = @@ROWCOUNT;
    DECLARE @Value DATE
    SET @Value = (SELECT MAX(ModifiedAt) FROM @Records)
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'datacenters', @Value = @Value;
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- LngProjects (watermark: ModifiedAt; natural key PlantName+PhaseNumber) ----
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeLngProjects @Records dbo.LngProjectsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.LngProjects AS tgt
    USING @Records AS src ON tgt.PlantName = src.PlantName AND tgt.PhaseNumber = src.PhaseNumber
    WHEN MATCHED THEN UPDATE SET
        EntityName = src.EntityName, Technology = src.Technology, NameplateCapacity = src.NameplateCapacity,
        CapacityUnit = src.CapacityUnit, Trains = src.Trains, VulcanStatus = src.VulcanStatus,
        DateVulcanEarliestOnline = src.DateVulcanEarliestOnline, DateVulcanLatestOnline = src.DateVulcanLatestOnline,
        Latitude = src.Latitude, Longitude = src.Longitude, Observation = src.Observation,
        DateVulcanStatusChange = src.DateVulcanStatusChange, DateImage = src.DateImage, ModifiedAt = src.ModifiedAt,
        LoadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PlantName, PhaseNumber, EntityName, Technology, NameplateCapacity, CapacityUnit, Trains,
                VulcanStatus, DateVulcanEarliestOnline, DateVulcanLatestOnline, Latitude, Longitude, Observation,
                DateVulcanStatusChange, DateImage, ModifiedAt)
        VALUES (src.PlantName, src.PhaseNumber, src.EntityName, src.Technology, src.NameplateCapacity, src.CapacityUnit,
                src.Trains, src.VulcanStatus, src.DateVulcanEarliestOnline, src.DateVulcanLatestOnline, src.Latitude,
                src.Longitude, src.Observation, src.DateVulcanStatusChange, src.DateImage, src.ModifiedAt);
    DECLARE @merged INT = @@ROWCOUNT;
    DECLARE @Value DATE
    SET @Value = (SELECT MAX(ModifiedAt) FROM @Records)
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'lng_projects', @Value = @Value;
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- ProjectRankings (watermark: DateUpdated) ----
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeProjectRankings @Records dbo.ProjectRankingsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.ProjectRankings AS tgt
    USING @Records AS src ON tgt.SynmaxId = src.SynmaxId
    WHEN MATCHED THEN UPDATE SET
        PlantId = src.PlantId, GeneratorId = src.GeneratorId, FinalRank = src.FinalRank,
        DateVulcanProposedV2Online = src.DateVulcanProposedV2Online, DateVulcanProposedOnline = src.DateVulcanProposedOnline,
        DateUpdated = src.DateUpdated, LoadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SynmaxId, PlantId, GeneratorId, FinalRank, DateVulcanProposedV2Online, DateVulcanProposedOnline, DateUpdated)
        VALUES (src.SynmaxId, src.PlantId, src.GeneratorId, src.FinalRank, src.DateVulcanProposedV2Online,
                src.DateVulcanProposedOnline, src.DateUpdated);
    DECLARE @merged INT = @@ROWCOUNT;
    DECLARE @Value DATE
    SET @Value = (SELECT MAX(DateUpdated) FROM @Records)
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'project_rankings', @Value = @Value;
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- MetadataHistory (watermark: DateEiaUpdated) ----
CREATE OR ALTER PROCEDURE dbo.usp_BulkMergeMetadataHistory @Records dbo.MetadataHistoryTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.MetadataHistory AS tgt
    USING @Records AS src ON tgt.SynmaxId = src.SynmaxId
    WHEN MATCHED THEN UPDATE SET
        PlantName = src.PlantName, Technology = src.Technology, NameplateCapacity = src.NameplateCapacity,
        StateCode = src.StateCode, County = src.County, DatePlannedOperations = src.DatePlannedOperations,
        PlantStatus = src.PlantStatus, DateEiaUpdated = src.DateEiaUpdated,
        DaysPlannedOperationMinusFirstSeenPlannedOperation = src.DaysPlannedOperationMinusFirstSeenPlannedOperation,
        Latitude = src.Latitude, Longitude = src.Longitude, BalancingAuthorityCode = src.BalancingAuthorityCode,
        SectorName = src.SectorName, LoadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SynmaxId, PlantName, Technology, NameplateCapacity, StateCode, County, DatePlannedOperations,
                PlantStatus, DateEiaUpdated, DaysPlannedOperationMinusFirstSeenPlannedOperation, Latitude, Longitude,
                BalancingAuthorityCode, SectorName)
        VALUES (src.SynmaxId, src.PlantName, src.Technology, src.NameplateCapacity, src.StateCode, src.County,
                src.DatePlannedOperations, src.PlantStatus, src.DateEiaUpdated,
                src.DaysPlannedOperationMinusFirstSeenPlannedOperation, src.Latitude, src.Longitude,
                src.BalancingAuthorityCode, src.SectorName);
    DECLARE @merged INT = @@ROWCOUNT;
    DECLARE @Value DATE
    SET @Value = (SELECT MAX(DateEiaUpdated) FROM @Records)
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'metadata_history', @Value = @Value;
    SELECT @merged AS RecordsProcessed;
END
GO
