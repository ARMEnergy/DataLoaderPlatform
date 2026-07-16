-- =============================================================================
-- 001_CreateEnergyAspectsSchema.sql
-- Tables, types, and indexes for the EnergyAspects loader.
-- Lives in this loader's OWN database (Loaders:EnergyAspects:ConnectionString).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- DatasetMapping — the EA catalogue, refreshed every run.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.DatasetMapping', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DatasetMapping
    (
        MappingId      INT           NOT NULL CONSTRAINT PK_DatasetMapping PRIMARY KEY,
        MappingName    NVARCHAR(500) NOT NULL,
        Category       NVARCHAR(200) NOT NULL,
        RequestString  NVARCHAR(MAX) NULL,
        Licensed       NVARCHAR(20)  NULL,
        IsActive       BIT           NOT NULL CONSTRAINT DF_DatasetMapping_IsActive DEFAULT 1,
        ModifiedAtUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_DatasetMapping_Modified DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ----------------------------------------------------------------------------
-- DatasetMappingId — the 1-to-many between mappings and dataset_ids.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.DatasetMappingId', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DatasetMappingId
    (
        MappingId INT NOT NULL,
        DatasetId INT NOT NULL,
        CONSTRAINT PK_DatasetMappingId PRIMARY KEY (MappingId, DatasetId),
        CONSTRAINT FK_DatasetMappingId_Mapping FOREIGN KEY (MappingId)
            REFERENCES dbo.DatasetMapping(MappingId) ON DELETE CASCADE
    );
END
GO

-- ----------------------------------------------------------------------------
-- TimeseriesData — the actual values, one row per (dataset, date).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.TimeseriesData', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TimeseriesData
    (
        DatasetId  INT          NOT NULL,
        DataDate   DATE         NOT NULL,
        [Value]    DECIMAL(38,8) NULL,
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_TSData_Modified DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TimeseriesData PRIMARY KEY (DatasetId, DataDate)
    );
END
GO

-- ----------------------------------------------------------------------------
-- DatasetMetadata — one row per dataset_id with descriptive attributes.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.DatasetMetadata', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DatasetMetadata
    (
        DatasetId          INT NOT NULL CONSTRAINT PK_DatasetMetadata PRIMARY KEY,
        Country            NVARCHAR(200) NULL,
        CountryIso         NVARCHAR(10)  NULL,
        Region             NVARCHAR(200) NULL,
        SubRegion          NVARCHAR(200) NULL,
        [Description]      NVARCHAR(1000) NULL,
        Aspect             NVARCHAR(200) NULL,
        AspectSubtype      NVARCHAR(200) NULL,
        Category           NVARCHAR(200) NULL,
        CategorySubtype    NVARCHAR(200) NULL,
        ForecastStartDate  DATE          NULL,
        Frequency          NVARCHAR(50)  NULL,
        LifecycleStage     NVARCHAR(50)  NULL,
        [Source]           NVARCHAR(200) NULL,
        Unit               NVARCHAR(100) NULL,
        ReleaseDate        DATETIME2(3)  NULL,
        Basin              NVARCHAR(200) NULL,
        PartnerSubRegion   NVARCHAR(200) NULL,
        Pipeline           NVARCHAR(200) NULL,
        EaIsoRto           NVARCHAR(200) NULL,
        ModifiedAtUtc      DATETIME2(3)  NOT NULL CONSTRAINT DF_DatasetMetadata_Modified DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ----------------------------------------------------------------------------
-- ApiResponseLog — every API response code we get, per mapping.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.ApiResponseLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ApiResponseLog
    (
        LogId         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ApiResponseLog PRIMARY KEY,
        MappingId     INT           NOT NULL,
        ResponseCode  INT           NOT NULL,
        LoggedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_ApiResponseLog_LoggedAt DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_ApiResponseLog_Mapping ON dbo.ApiResponseLog (MappingId, LoggedAtUtc);
END
GO
