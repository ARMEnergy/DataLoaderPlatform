-- =============================================================================
-- 003_CreateEnergyAspectsProcedures.sql
-- Stored procedures called by EnergyAspectsSink.
-- =============================================================================

USE EnergyAspects;
GO

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
    -- Deliberately NOT filtered on Licensed. That column is the vendor's
    -- per-MAPPING flag and it does not match per-DATASET entitlement: a census
    -- of the catalogue on 2026-10-14 found that datasets appearing only in
    -- Licensed='no' mappings are served normally (2,473 of a random 5,000, and
    -- 366 of 400 id-ordered), e.g. mapping 13 'US regional refinery slates'.
    -- Filtering on it excluded ~246k dataset ids, a large share of which the
    -- API will happily return.
    --
    -- The API is the authority instead: a request for ids we are not entitled
    -- to answers 417, which the source reader records as a legitimate empty
    -- read, so asking costs a request and nothing else. Licensed is still
    -- SELECTed above because the reader uses it as a corroborating hint — see
    -- EnergyAspectsApiSource.ReadAsync.
    --
    -- IsActive remains the manual off-switch, though it does not currently
    -- discriminate: usp_BulkUpsertDatasetMappings stamps IsActive = 1 on every
    -- upsert, so all rows are active until someone clears one by hand.
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

    -- The vendor can return the SAME dataset_id more than once in one response.
    -- Mapping 122 (Global refinery maintenance) answers with 547 items for 546
    -- requested ids because dataset 7019 arrives twice: once as the current
    -- revision (release_date 2026-10-09, forecast_start_date 2025-03-28) and
    -- once superseded (release_date 2025-09-12, no forecast start). Everything
    -- else on the two rows is identical.
    --
    -- Both copies reach the TVP, both miss on DatasetId, and MERGE's NOT
    -- MATCHED branch inserts both — which the unique index on DatasetId then
    -- rejects. Note this is the NOT MATCHED side, so SQL Server does NOT raise
    -- its usual "MERGE statement attempted to UPDATE or DELETE the same row
    -- more than once" (error 8672); it surfaces as a plain unique-key violation
    -- that reads like a concurrency problem and is not one. Live effect: every
    -- window of mapping 122 failed on every run.
    --
    -- So collapse to one row per DatasetId before the MERGE. Newest ReleaseDate
    -- wins — that is the vendor's own marker for which revision is current, and
    -- under DESC a NULL sorts last, so a dated row always beats an undated one.
    ;WITH Fingerprinted AS
    (
        SELECT r.*,
               -- Pure tiebreak: makes the pick a function of the row's content
               -- rather than of whatever order the TVP happened to arrive in.
               -- Only reached when ReleaseDate and ForecastStartDate both tie.
               -- CONCAT, not CONCAT_WS: CONCAT_WS *skips* NULL arguments, which
               -- would slide later columns into their place and let two
               -- different rows fingerprint the same. CONCAT substitutes ''.
               -- The leading CAST forces the whole expression to NVARCHAR(MAX)
               -- so a long Description cannot be silently truncated at 4000.
               RowFingerprint = HASHBYTES('MD5', CONCAT(
                   CAST(r.Country AS NVARCHAR(MAX)), N'|', r.CountryIso, N'|', r.Region, N'|',
                   r.SubRegion, N'|', r.[Description], N'|', r.Aspect, N'|', r.AspectSubtype, N'|',
                   r.Category, N'|', r.CategorySubtype, N'|', r.ForecastStartDate, N'|',
                   r.Frequency, N'|', r.LifecycleStage, N'|', r.[Source], N'|', r.Unit, N'|',
                   r.ReleaseDate, N'|', r.Basin, N'|', r.PartnerSubRegion, N'|', r.Pipeline, N'|',
                   r.EaIsoRto))
        FROM @Records AS r
    ),
    Ranked AS
    (
        SELECT f.*,
               RowRank = ROW_NUMBER() OVER (
                   PARTITION BY f.DatasetId
                   ORDER BY f.ReleaseDate DESC, f.ForecastStartDate DESC, f.RowFingerprint)
        FROM Fingerprinted AS f
    )
    MERGE dbo.DatasetMetadata AS tgt
    USING (SELECT * FROM Ranked WHERE RowRank = 1) AS src
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
