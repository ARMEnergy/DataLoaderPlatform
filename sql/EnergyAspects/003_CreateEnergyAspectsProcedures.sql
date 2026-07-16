-- =============================================================================
-- 003_CreateEnergyAspectsProcedures.sql
-- Stored procedures called by EnergyAspectsSink.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- dbo.usp_BulkUpsertDatasetMappings
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.usp_BulkUpsertDatasetMappings', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_BulkUpsertDatasetMappings;
GO
CREATE PROCEDURE dbo.usp_BulkUpsertDatasetMappings
    @Records dbo.MappingDatasetTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.DatasetMapping AS tgt
    USING @Records AS src
       ON tgt.MappingId = src.MappingId
    WHEN MATCHED THEN UPDATE SET
        MappingName   = src.MappingName,
        Category      = src.Category,
        RequestString = src.RequestString,
        Licensed      = src.Licensed,
        IsActive      = src.IsActive,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MappingId, MappingName, Category, RequestString, Licensed, IsActive)
        VALUES (src.MappingId, src.MappingName, src.Category, src.RequestString, src.Licensed, src.IsActive);
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_BulkUpsertDatasetMappingIds — replace child rows for each parent.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.usp_BulkUpsertDatasetMappingIds', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_BulkUpsertDatasetMappingIds;
GO
CREATE PROCEDURE dbo.usp_BulkUpsertDatasetMappingIds
    @Records dbo.MappingDatasetIdTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    -- Insert any missing pairs (the parent mapping must exist from the prior call)
    MERGE dbo.DatasetMappingId AS tgt
    USING @Records AS src
       ON tgt.MappingId = src.MappingId AND tgt.DatasetId = src.DatasetId
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MappingId, DatasetId) VALUES (src.MappingId, src.DatasetId);
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_GetActiveMappings — read back active mappings with comma-joined ids.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.usp_GetActiveMappings', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_GetActiveMappings;
GO
CREATE PROCEDURE dbo.usp_GetActiveMappings
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        m.MappingId,
        m.MappingName,
        m.Category,
        m.RequestString,
        m.Licensed,
        DatasetIds = STUFF((
            SELECT ',' + CAST(d.DatasetId AS NVARCHAR(20))
              FROM dbo.DatasetMappingId d
             WHERE d.MappingId = m.MappingId
             ORDER BY d.DatasetId
               FOR XML PATH(''), TYPE
        ).value('.', 'NVARCHAR(MAX)'), 1, 1, '')
    FROM dbo.DatasetMapping m
    WHERE m.IsActive = 1
    ORDER BY m.MappingId;
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_BulkMergeTimeseriesData — bulk upsert values, return count merged.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.usp_BulkMergeTimeseriesData', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_BulkMergeTimeseriesData;
GO
CREATE PROCEDURE dbo.usp_BulkMergeTimeseriesData
    @Records dbo.TimeseriesDataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @merged INT = 0;

    MERGE dbo.TimeseriesData AS tgt
    USING @Records AS src
       ON tgt.DatasetId = src.DatasetId AND tgt.DataDate = src.DataDate
    WHEN MATCHED AND (
            (tgt.[Value] IS NULL AND src.[Value] IS NOT NULL) OR
            (tgt.[Value] IS NOT NULL AND src.[Value] IS NULL) OR
            (tgt.[Value] <> src.[Value])
        ) THEN UPDATE SET
            [Value]       = src.[Value],
            ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (DatasetId, DataDate, [Value]) VALUES (src.DatasetId, src.DataDate, src.[Value]);

    SET @merged = @@ROWCOUNT;
    SELECT @merged AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_BulkUpsertDatasetMetadata — upsert per-dataset descriptive fields.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.usp_BulkUpsertDatasetMetadata', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_BulkUpsertDatasetMetadata;
GO
CREATE PROCEDURE dbo.usp_BulkUpsertDatasetMetadata
    @Records dbo.DatasetMetadataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.DatasetMetadata AS tgt
    USING @Records AS src
       ON tgt.DatasetId = src.DatasetId
    WHEN MATCHED THEN UPDATE SET
        Country           = src.Country,
        CountryIso        = src.CountryIso,
        Region            = src.Region,
        SubRegion         = src.SubRegion,
        [Description]     = src.[Description],
        Aspect            = src.Aspect,
        AspectSubtype     = src.AspectSubtype,
        Category          = src.Category,
        CategorySubtype   = src.CategorySubtype,
        ForecastStartDate = src.ForecastStartDate,
        Frequency         = src.Frequency,
        LifecycleStage    = src.LifecycleStage,
        [Source]          = src.[Source],
        Unit              = src.Unit,
        ReleaseDate       = src.ReleaseDate,
        Basin             = src.Basin,
        PartnerSubRegion  = src.PartnerSubRegion,
        Pipeline          = src.Pipeline,
        EaIsoRto          = src.EaIsoRto,
        ModifiedAtUtc     = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (DatasetId, Country, CountryIso, Region, SubRegion, [Description], Aspect, AspectSubtype,
                Category, CategorySubtype, ForecastStartDate, Frequency, LifecycleStage, [Source], Unit,
                ReleaseDate, Basin, PartnerSubRegion, Pipeline, EaIsoRto)
        VALUES (src.DatasetId, src.Country, src.CountryIso, src.Region, src.SubRegion, src.[Description],
                src.Aspect, src.AspectSubtype, src.Category, src.CategorySubtype, src.ForecastStartDate,
                src.Frequency, src.LifecycleStage, src.[Source], src.Unit, src.ReleaseDate,
                src.Basin, src.PartnerSubRegion, src.Pipeline, src.EaIsoRto);
END
GO

-- ----------------------------------------------------------------------------
-- dbo.usp_LogApiResponse — append-only audit row.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.usp_LogApiResponse', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_LogApiResponse;
GO
CREATE PROCEDURE dbo.usp_LogApiResponse
    @MappingId    INT,
    @ResponseCode INT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.ApiResponseLog (MappingId, ResponseCode) VALUES (@MappingId, @ResponseCode);
END
GO
