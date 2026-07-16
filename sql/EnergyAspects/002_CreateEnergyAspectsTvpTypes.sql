-- =============================================================================
-- 002_CreateEnergyAspectsTvpTypes.sql
-- Table-valued parameter types for the bulk merges.
-- =============================================================================

IF TYPE_ID('dbo.MappingDatasetTvp') IS NULL
BEGIN
    CREATE TYPE dbo.MappingDatasetTvp AS TABLE
    (
        MappingId     INT           NOT NULL,
        MappingName   NVARCHAR(500) NOT NULL,
        Category      NVARCHAR(200) NOT NULL,
        RequestString NVARCHAR(MAX) NULL,
        Licensed      NVARCHAR(20)  NULL,
        IsActive      BIT           NOT NULL
    );
END
GO

IF TYPE_ID('dbo.MappingDatasetIdTvp') IS NULL
BEGIN
    CREATE TYPE dbo.MappingDatasetIdTvp AS TABLE
    (
        MappingId INT NOT NULL,
        DatasetId INT NOT NULL,
        PRIMARY KEY (MappingId, DatasetId)
    );
END
GO

IF TYPE_ID('dbo.TimeseriesDataTvp') IS NULL
BEGIN
    CREATE TYPE dbo.TimeseriesDataTvp AS TABLE
    (
        DatasetId INT           NOT NULL,
        DataDate  DATE          NOT NULL,
        [Value]   DECIMAL(38,8) NULL,
        PRIMARY KEY (DatasetId, DataDate)
    );
END
GO

IF TYPE_ID('dbo.DatasetMetadataTvp') IS NULL
BEGIN
    CREATE TYPE dbo.DatasetMetadataTvp AS TABLE
    (
        DatasetId         INT           NOT NULL,
        Country           NVARCHAR(200) NULL,
        CountryIso        NVARCHAR(10)  NULL,
        Region            NVARCHAR(200) NULL,
        SubRegion         NVARCHAR(200) NULL,
        [Description]     NVARCHAR(1000) NULL,
        Aspect            NVARCHAR(200) NULL,
        AspectSubtype     NVARCHAR(200) NULL,
        Category          NVARCHAR(200) NULL,
        CategorySubtype   NVARCHAR(200) NULL,
        ForecastStartDate DATE          NULL,
        Frequency         NVARCHAR(50)  NULL,
        LifecycleStage    NVARCHAR(50)  NULL,
        [Source]          NVARCHAR(200) NULL,
        Unit              NVARCHAR(100) NULL,
        ReleaseDate       DATETIME2(3)  NULL,
        Basin             NVARCHAR(200) NULL,
        PartnerSubRegion  NVARCHAR(200) NULL,
        Pipeline          NVARCHAR(200) NULL,
        EaIsoRto          NVARCHAR(200) NULL,
        PRIMARY KEY (DatasetId)
    );
END
GO
