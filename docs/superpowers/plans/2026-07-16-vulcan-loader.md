# Vulcan Loader Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `DataLoader.Vulcan` plugin that incrementally pulls all five SynMax Vulcan datalink tables (POST SQL query) and MERGEs each into its own `dbo` table in a dedicated `Vulcan` database.

**Architecture:** One `ILoaderModule` ("Vulcan") composed of five closed per-table pipelines that share one `VulcanWorkUnit`, one generic POST source reader, one generic work-unit provider, and `IdentityTransformer<TRow>`. Each table supplies only a row model, a `SqlSinkBase<TRow>` subclass, and a `VulcanTableSpec` (source table, watermark column, dedup rule, natural key). Incremental resumption uses a dedicated `dbo.LoadWatermark` table; the merge proc advances the watermark atomically with the data write.

**Tech Stack:** C# / .NET 8, Microsoft.Data.SqlClient, Microsoft.Extensions.* DI/Http/Options, Polly (via the platform's `RetryPolicyFactory`), SQL Server (TVP bulk merge), xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-07-16-vulcan-loader-design.md`

**Conventions to mirror:** `src/DataLoader.EnergyAspects/` (module wiring, `SqlSinkBase`, settings binding) and `sql/EnergyAspects/` (TVP + MERGE proc style).

---

## Notes for the implementer

- **`dotnet` is the build tool.** From repo root: `dotnet build DataLoaderPlatform.sln -c Release`.
- **No existing test project.** Task 12 creates `tests/DataLoader.Vulcan.Tests` (xUnit) and adds it to the solution. Run tests with `dotnet test`.
- **Row → JSON mapping:** the platform's `HttpJsonSourceReaderBase.DefaultJsonOptions` uses `JsonNamingPolicy.SnakeCaseLower`, so a C# property `SynmaxId` binds to JSON `synmax_id`, `DateImage` → `date_image`, etc. Name model properties in PascalCase accordingly — no `[JsonPropertyName]` attributes needed.
- **Response envelope is an open item (spec §11.1).** Task 6 assumes `{ "data": [ {row}, ... ] }` and also tolerates a bare top-level array. If a live sample shows a different shape, only `VulcanQuerySourceReader.DeserializeAsync` changes.
- **Per-row audit:** tables carry `LoadedAtUtc DEFAULT SYSUTCDATETIME()` only. Run-level audit (`RunId`) already lives in `core.LoadLog`; the sink signature (`WriteAsync(rows, ct)`) has no run context to pass down, so no per-row `RunId`.
- **Dates** arrive as ISO `"YYYY-MM-DD"` strings and bind to `DateTime?`. Numerics bind to `double?`. The DB narrows them to `date` / `decimal` / `float`.
- **Watermark safety:** the watermark value comes from our own DB and is formatted as an ISO date literal in the query. Values are never user-supplied.

---

## Task 0: Scaffold the project

**Files:**
- Create: `src/DataLoader.Vulcan/DataLoader.Vulcan.csproj`
- Modify: `src/DataLoader.Host/DataLoader.Host.csproj` (add ProjectReference)
- Modify: `DataLoaderPlatform.sln` (add project)

- [ ] **Step 1: Create the csproj**

`src/DataLoader.Vulcan/DataLoader.Vulcan.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DataLoader.Vulcan</RootNamespace>
    <AssemblyName>DataLoader.Vulcan</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DataLoader.Core\DataLoader.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add ProjectReference from the host** so the plugin dll deploys next to the host for reflection discovery.

In `src/DataLoader.Host/DataLoader.Host.csproj`, add inside the existing loader `<ItemGroup>` (after the FtpExample reference):
```xml
    <ProjectReference Include="..\DataLoader.Vulcan\DataLoader.Vulcan.csproj" />
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln DataLoaderPlatform.sln add src/DataLoader.Vulcan/DataLoader.Vulcan.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 4: Verify it builds (empty project)**

Run: `dotnet build DataLoaderPlatform.sln -c Release`
Expected: Build succeeded (Vulcan project compiles with no source files yet).

- [ ] **Step 5: Commit**

```bash
git add src/DataLoader.Vulcan/DataLoader.Vulcan.csproj src/DataLoader.Host/DataLoader.Host.csproj DataLoaderPlatform.sln
git commit -m "chore: scaffold DataLoader.Vulcan project"
```

---

## Task 1: SQL — tables, watermark, TVP types, merge procs

**Files:**
- Create: `sql/Vulcan/001_CreateVulcanSchema.sql`
- Create: `sql/Vulcan/002_CreateVulcanTvpTypes.sql`
- Create: `sql/Vulcan/003_CreateVulcanProcedures.sql`

All objects are in the `dbo` schema of the `Vulcan` database (per `Loaders:Vulcan:ConnectionString`). These scripts are the input/output contract for the C# sinks; keep column names and TVP column order in sync with the `BuildTable` methods in Task 8.

- [ ] **Step 1: Write `001_CreateVulcanSchema.sql`** (five data tables + watermark table)

```sql
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
```

- [ ] **Step 2: Write `002_CreateVulcanTvpTypes.sql`** (one TVP per table; column order MUST match the `BuildTable` DataTables in Task 8; no `LoadedAtUtc` column — the table default fills it)

```sql
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
```

- [ ] **Step 3: Write `003_CreateVulcanProcedures.sql`** (watermark accessors + five merge procs; each proc MERGEs on the natural key, returns the merged count, and advances `dbo.LoadWatermark`)

```sql
-- =============================================================================
-- 003_CreateVulcanProcedures.sql
-- Watermark accessors + per-table bulk-merge procedures.
-- Each merge proc: MERGE into the target, SELECT the merged row count,
-- then advance dbo.LoadWatermark to MAX(watermark column) of the incoming rows.
-- =============================================================================

IF OBJECT_ID('dbo.usp_GetVulcanWatermark', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_GetVulcanWatermark;
GO
CREATE PROCEDURE dbo.usp_GetVulcanWatermark @TableName NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT WatermarkValue FROM dbo.LoadWatermark WHERE TableName = @TableName;
END
GO

-- Helper used by every merge proc to advance a table's watermark.
IF OBJECT_ID('dbo.usp_SetVulcanWatermark', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_SetVulcanWatermark;
GO
CREATE PROCEDURE dbo.usp_SetVulcanWatermark @TableName NVARCHAR(100), @Value DATE
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
IF OBJECT_ID('dbo.usp_BulkMergeUnderConstruction', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_BulkMergeUnderConstruction;
GO
CREATE PROCEDURE dbo.usp_BulkMergeUnderConstruction @Records dbo.UnderConstructionTvp READONLY
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
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'under_construction', @Value = (SELECT MAX(DateImage) FROM @Records);
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- DataCenters (watermark: ModifiedAt) ----
IF OBJECT_ID('dbo.usp_BulkMergeDataCenters', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_BulkMergeDataCenters;
GO
CREATE PROCEDURE dbo.usp_BulkMergeDataCenters @Records dbo.DataCentersTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.DataCenters AS tgt
    USING @Records AS src ON tgt.SynmaxId = src.SynmaxId
    WHEN MATCHED THEN UPDATE SET
        PlantId = src.PlantId, PlantName = src.PlantName, UnitId = src.UnitId, UnitName = src.UnitName,
        OwnerName = src.OwnerName, DataCenterType = src.DataCenterType, UnitCapacity = src.UnitCapacity,
        VulcanStatus = src.VulcanStatus, StateCode = src.StateCode, BalancingAuthority = src.BalancingAuthority,
        DateVulcanEarliestOnline = src.DateVulcanEarliestOnline, DateVulcanLatestOnline = src.DateVulcanLatestOnline,
        ModifiedAt = src.ModifiedAt, LoadedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SynmaxId, PlantId, PlantName, UnitId, UnitName, OwnerName, DataCenterType, UnitCapacity,
                VulcanStatus, StateCode, BalancingAuthority, DateVulcanEarliestOnline, DateVulcanLatestOnline, ModifiedAt)
        VALUES (src.SynmaxId, src.PlantId, src.PlantName, src.UnitId, src.UnitName, src.OwnerName, src.DataCenterType,
                src.UnitCapacity, src.VulcanStatus, src.StateCode, src.BalancingAuthority,
                src.DateVulcanEarliestOnline, src.DateVulcanLatestOnline, src.ModifiedAt);
    DECLARE @merged INT = @@ROWCOUNT;
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'datacenters', @Value = (SELECT MAX(ModifiedAt) FROM @Records);
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- LngProjects (watermark: ModifiedAt; natural key PlantName+PhaseNumber) ----
IF OBJECT_ID('dbo.usp_BulkMergeLngProjects', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_BulkMergeLngProjects;
GO
CREATE PROCEDURE dbo.usp_BulkMergeLngProjects @Records dbo.LngProjectsTvp READONLY
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
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'lng_projects', @Value = (SELECT MAX(ModifiedAt) FROM @Records);
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- ProjectRankings (watermark: DateUpdated) ----
IF OBJECT_ID('dbo.usp_BulkMergeProjectRankings', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_BulkMergeProjectRankings;
GO
CREATE PROCEDURE dbo.usp_BulkMergeProjectRankings @Records dbo.ProjectRankingsTvp READONLY
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
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'project_rankings', @Value = (SELECT MAX(DateUpdated) FROM @Records);
    SELECT @merged AS RecordsProcessed;
END
GO

-- ---- MetadataHistory (watermark: DateEiaUpdated) ----
IF OBJECT_ID('dbo.usp_BulkMergeMetadataHistory', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_BulkMergeMetadataHistory;
GO
CREATE PROCEDURE dbo.usp_BulkMergeMetadataHistory @Records dbo.MetadataHistoryTvp READONLY
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
    EXEC dbo.usp_SetVulcanWatermark @TableName = N'metadata_history', @Value = (SELECT MAX(DateEiaUpdated) FROM @Records);
    SELECT @merged AS RecordsProcessed;
END
GO
```

- [ ] **Step 4: Commit** (SQL is not executed by the build; correctness is validated when the DB is provisioned + by Task 12 tests where a test DB is available)

```bash
git add sql/Vulcan/
git commit -m "feat(vulcan): SQL schema, TVP types, and merge procs"
```

---

## Task 2: Settings + table specs

**Files:**
- Create: `src/DataLoader.Vulcan/VulcanSettings.cs`
- Create: `src/DataLoader.Vulcan/VulcanTableSpec.cs`

- [ ] **Step 1: Write `VulcanSettings.cs`**

```csharp
using DataLoader.Core.Configuration;

namespace DataLoader.Vulcan;

/// <summary>
/// Vulcan loader settings, bound from <c>Loaders:Vulcan</c>. Inherits the common
/// bits (connection string, retry, concurrency) and adds the SynMax API fields.
/// </summary>
public sealed class VulcanSettings : LoaderSettingsBase
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://hyperion.api.synmax.com";
    public string QueryEndpoint { get; set; } = "/v4/beta/query_datalinks";
    public int HttpTimeoutSeconds { get; set; } = 60;
    public List<string> EnabledTables { get; set; } = new()
    {
        "under_construction", "datacenters", "lng_projects", "project_rankings", "metadata_history"
    };
}
```

- [ ] **Step 2: Write `VulcanTableSpec.cs`** (describes one source table so the shared provider can build its query)

```csharp
namespace DataLoader.Vulcan;

/// <summary>
/// Static description of one Vulcan source table: where to read it, which column
/// drives incremental loading, and whether latest-row-per-id deduplication is
/// needed. One instance per table; consumed by the work-unit provider to build
/// the SQL query sent to the API.
/// </summary>
public sealed class VulcanTableSpec
{
    /// <summary>Loader-facing table id (matches EnabledTables and dbo.LoadWatermark.TableName), e.g. "datacenters".</summary>
    public required string TableId { get; init; }

    /// <summary>Fully-qualified source datalink table, e.g. "vdl.datacenters".</summary>
    public required string SourceTable { get; init; }

    /// <summary>Column used as the incremental watermark, e.g. "modified_at".</summary>
    public required string WatermarkColumn { get; init; }

    /// <summary>When set, wrap with ROW_NUMBER() PARTITION BY this column to keep the latest row per id.</summary>
    public string? DedupPartitionColumn { get; init; }

    public static readonly VulcanTableSpec UnderConstruction = new()
    {
        TableId = "under_construction", SourceTable = "vdl.under_construction", WatermarkColumn = "date_image"
    };
    public static readonly VulcanTableSpec DataCenters = new()
    {
        TableId = "datacenters", SourceTable = "vdl.datacenters", WatermarkColumn = "modified_at",
        DedupPartitionColumn = "synmax_id"
    };
    public static readonly VulcanTableSpec LngProjects = new()
    {
        TableId = "lng_projects", SourceTable = "vdl.lng_projects", WatermarkColumn = "modified_at"
    };
    public static readonly VulcanTableSpec ProjectRankings = new()
    {
        TableId = "project_rankings", SourceTable = "vdl.project_rankings", WatermarkColumn = "date_updated",
        DedupPartitionColumn = "synmax_id"
    };
    public static readonly VulcanTableSpec MetadataHistory = new()
    {
        TableId = "metadata_history", SourceTable = "vdl.metadata_history", WatermarkColumn = "date_eia_updated",
        DedupPartitionColumn = "synmax_id"
    };
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/DataLoader.Vulcan/DataLoader.Vulcan.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/DataLoader.Vulcan/VulcanSettings.cs src/DataLoader.Vulcan/VulcanTableSpec.cs
git commit -m "feat(vulcan): settings and table specs"
```

---

## Task 3: Work unit + SQL query builder

**Files:**
- Create: `src/DataLoader.Vulcan/VulcanWorkUnit.cs`
- Test: `tests/DataLoader.Vulcan.Tests/VulcanQueryBuilderTests.cs` (test project created in Task 12; write the test file now, it will compile once the project exists)

The query builder is a pure static function — the ideal first thing to test.

- [ ] **Step 1: Write `VulcanWorkUnit.cs`**

```csharp
using System.Globalization;
using DataLoader.Core.Abstractions;

namespace DataLoader.Vulcan;

/// <summary>
/// One "load this Vulcan table for this run date" unit. Shared by all five
/// per-table pipelines; the resolved SQL query it carries is what differs.
/// </summary>
public sealed class VulcanWorkUnit : WorkUnit
{
    public required string TableId { get; init; }
    public required DateOnly RunDate { get; init; }
    public required string Query { get; init; }

    public override string Key => $"vulcan:table={TableId};date={RunDate:yyyy-MM-dd}";
    public override string DisplayName => $"Vulcan {TableId} @ {RunDate:yyyy-MM-dd}";
}

/// <summary>
/// Builds the SQL query sent to query_datalinks for one table. Incremental filter
/// is inclusive (&gt;=) so day-granularity watermarks never skip same-day updates;
/// re-pulled boundary rows are harmless because the sink MERGEs by natural key.
/// </summary>
public static class VulcanQueryBuilder
{
    public static string Build(VulcanTableSpec spec, DateOnly? watermark)
    {
        var where = watermark is null
            ? string.Empty
            : $" WHERE {spec.WatermarkColumn} >= '{watermark.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'";

        if (spec.DedupPartitionColumn is null)
            return $"SELECT * FROM {spec.SourceTable}{where}";

        // Keep only the latest row per id among the (optionally filtered) rows.
        return
            $"SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY {spec.DedupPartitionColumn} " +
            $"ORDER BY {spec.WatermarkColumn} DESC) AS rn FROM {spec.SourceTable}{where}) t WHERE t.rn = 1";
    }
}
```

- [ ] **Step 2: Write the failing test** `tests/DataLoader.Vulcan.Tests/VulcanQueryBuilderTests.cs`

```csharp
using DataLoader.Vulcan;
using Xunit;

namespace DataLoader.Vulcan.Tests;

public class VulcanQueryBuilderTests
{
    [Fact]
    public void FirstRun_NoWatermark_NoDedup_SelectsWholeTable()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.UnderConstruction, watermark: null);
        Assert.Equal("SELECT * FROM vdl.under_construction", sql);
    }

    [Fact]
    public void Incremental_NoDedup_AddsInclusiveWatermarkFilter()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.LngProjects, new DateOnly(2026, 1, 15));
        Assert.Equal("SELECT * FROM vdl.lng_projects WHERE modified_at >= '2026-01-15'", sql);
    }

    [Fact]
    public void Incremental_Dedup_WrapsRowNumberAndFilters()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.DataCenters, new DateOnly(2026, 1, 15));
        Assert.Equal(
            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY modified_at DESC) AS rn " +
            "FROM vdl.datacenters WHERE modified_at >= '2026-01-15') t WHERE t.rn = 1",
            sql);
    }

    [Fact]
    public void FirstRun_Dedup_WrapsRowNumberWithoutWhere()
    {
        var sql = VulcanQueryBuilder.Build(VulcanTableSpec.ProjectRankings, watermark: null);
        Assert.Equal(
            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY date_updated DESC) AS rn " +
            "FROM vdl.project_rankings) t WHERE t.rn = 1",
            sql);
    }
}
```

- [ ] **Step 3: (After Task 12 exists) run the tests**

Run: `dotnet test tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj --filter VulcanQueryBuilderTests`
Expected: 4 passed. (If running before Task 12, defer this step.)

- [ ] **Step 4: Commit**

```bash
git add src/DataLoader.Vulcan/VulcanWorkUnit.cs tests/DataLoader.Vulcan.Tests/VulcanQueryBuilderTests.cs
git commit -m "feat(vulcan): work unit and incremental query builder"
```

---

## Task 4: Watermark store

**Files:**
- Create: `src/DataLoader.Vulcan/VulcanWatermarkStore.cs`

Reads the stored watermark (writing is done inside the merge procs). Kept as a small interface so the provider is testable.

- [ ] **Step 1: Write `VulcanWatermarkStore.cs`**

```csharp
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

public interface IVulcanWatermarkStore
{
    /// <summary>Returns the stored watermark for a table, or null if none (=> full backfill).</summary>
    Task<DateOnly?> GetAsync(string tableId, CancellationToken ct);
}

public sealed class VulcanWatermarkStore : IVulcanWatermarkStore
{
    private readonly VulcanSettings _settings;
    public VulcanWatermarkStore(IOptions<VulcanSettings> settings) => _settings = settings.Value;

    public async Task<DateOnly?> GetAsync(string tableId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("dbo.usp_GetVulcanWatermark", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@TableName", tableId);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null || result is DBNull) return null;
        return DateOnly.FromDateTime((DateTime)result);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DataLoader.Vulcan/DataLoader.Vulcan.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DataLoader.Vulcan/VulcanWatermarkStore.cs
git commit -m "feat(vulcan): watermark store"
```

---

## Task 5: Row models

**Files:**
- Create: `src/DataLoader.Vulcan/Models.cs`

Property names are PascalCase equivalents of the source snake_case columns (bound via `SnakeCaseLower` naming policy). Dates are `DateTime?`, numerics `double?`, ids/text `string?`.

- [ ] **Step 1: Write `Models.cs`**

```csharp
namespace DataLoader.Vulcan.Models;

public sealed class UnderConstructionRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public string? PlantName { get; set; }
    public string? Technology { get; set; }
    public double? NameplateCapacity { get; set; }
    public string? VulcanStatus { get; set; }
    public double? ProjectRank { get; set; }
    public DateTime? DateVulcanEarliestOnline { get; set; }
    public DateTime? DateVulcanLatestOnline { get; set; }
    public string? StateCode { get; set; }
    public string? BalancingAuthority { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime? DateVulcanStatusChange { get; set; }
    public DateTime? DateImage { get; set; }
}

public sealed class DataCenterRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public string? PlantName { get; set; }
    public string? UnitId { get; set; }
    public string? UnitName { get; set; }
    public string? OwnerName { get; set; }
    public string? DataCenterType { get; set; }
    public double? UnitCapacity { get; set; }
    public string? VulcanStatus { get; set; }
    public string? StateCode { get; set; }
    public string? BalancingAuthority { get; set; }
    public DateTime? DateVulcanEarliestOnline { get; set; }
    public DateTime? DateVulcanLatestOnline { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class LngProjectRow
{
    public string PlantName { get; set; } = string.Empty;
    public int PhaseNumber { get; set; }
    public string? EntityName { get; set; }
    public string? Technology { get; set; }
    public double? NameplateCapacity { get; set; }
    public string? CapacityUnit { get; set; }
    public int? Trains { get; set; }
    public string? VulcanStatus { get; set; }
    public DateTime? DateVulcanEarliestOnline { get; set; }
    public DateTime? DateVulcanLatestOnline { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Observation { get; set; }
    public DateTime? DateVulcanStatusChange { get; set; }
    public DateTime? DateImage { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class ProjectRankingRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public long? PlantId { get; set; }
    public string? GeneratorId { get; set; }
    public double? FinalRank { get; set; }
    public DateTime? DateVulcanProposedV2Online { get; set; }
    public DateTime? DateVulcanProposedOnline { get; set; }
    public DateTime? DateUpdated { get; set; }
}

public sealed class MetadataHistoryRow
{
    public string SynmaxId { get; set; } = string.Empty;
    public string? PlantName { get; set; }
    public string? Technology { get; set; }
    public double? NameplateCapacity { get; set; }
    public string? StateCode { get; set; }
    public string? County { get; set; }
    public DateTime? DatePlannedOperations { get; set; }
    public string? PlantStatus { get; set; }
    public DateTime? DateEiaUpdated { get; set; }
    public double? DaysPlannedOperationMinusFirstSeenPlannedOperation { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? BalancingAuthorityCode { get; set; }
    public string? SectorName { get; set; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DataLoader.Vulcan/DataLoader.Vulcan.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DataLoader.Vulcan/Models.cs
git commit -m "feat(vulcan): row models for the five tables"
```

---

## Task 6: Generic POST source reader

**Files:**
- Create: `src/DataLoader.Vulcan/VulcanQuerySourceReader.cs`
- Test: `tests/DataLoader.Vulcan.Tests/VulcanQuerySourceReaderTests.cs`

Extends the platform's `HttpJsonSourceReaderBase<VulcanWorkUnit, TRow>` to reuse `DefaultJsonOptions` + `SanitiseForLog`, but overrides `ReadAsync` to POST the SQL body with the `Access-Key` header. Deserializes `{ "data": [...] }` (and tolerates a bare array).

- [ ] **Step 1: Write `VulcanQuerySourceReader.cs`**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using DataLoader.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

/// <summary>
/// Reads one Vulcan table (one work unit) by POSTing its SQL query to
/// query_datalinks. Generic over the row model; the same code serves all five
/// tables. The Access-Key header and base address are configured on the injected
/// HttpClient (named client "Vulcan") in <see cref="VulcanModule"/>.
/// </summary>
public sealed class VulcanQuerySourceReader<TRow> : HttpJsonSourceReaderBase<VulcanWorkUnit, TRow>
{
    private readonly VulcanSettings _settings;

    public VulcanQuerySourceReader(HttpClient httpClient, IOptions<VulcanSettings> settings, ILogger logger)
        : base(httpClient, logger)
    {
        _settings = settings.Value;
    }

    // Base class is GET-oriented; Vulcan needs a POST body, so BuildRequestUri is unused.
    protected override Uri BuildRequestUri(VulcanWorkUnit unit) =>
        new(_settings.QueryEndpoint, UriKind.Relative);

    public override async Task<IReadOnlyList<TRow>> ReadAsync(VulcanWorkUnit unit, CancellationToken cancellationToken)
    {
        Logger.LogInformation("POST {Endpoint} for {Table}", _settings.QueryEndpoint, unit.TableId);
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.QueryEndpoint)
        {
            Content = JsonContent.Create(new { query = unit.Query })
        };
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<IReadOnlyList<TRow>> DeserializeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseRows(json);
    }

    /// <summary>
    /// Parses the response body. Assumes { "data": [ {row}, ... ] }; also tolerates a
    /// bare top-level array. See spec §11.1 — confirm against a live sample.
    /// </summary>
    internal static IReadOnlyList<TRow> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement arrayElement;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            arrayElement = doc.RootElement;
        else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            arrayElement = data;
        else
            return Array.Empty<TRow>();

        var rows = JsonSerializer.Deserialize<List<TRow>>(arrayElement.GetRawText(), DefaultJsonOptions);
        return rows ?? new List<TRow>();
    }
}
```

- [ ] **Step 2: Write the failing test** `tests/DataLoader.Vulcan.Tests/VulcanQuerySourceReaderTests.cs`

```csharp
using DataLoader.Vulcan;
using DataLoader.Vulcan.Models;
using Xunit;

namespace DataLoader.Vulcan.Tests;

public class VulcanQuerySourceReaderTests
{
    [Fact]
    public void ParseRows_DataEnvelope_MapsSnakeCaseToProperties()
    {
        var json = """
        { "data": [
            { "synmax_id": "P1-U1", "plant_name": "Alpha", "unit_capacity": 123.5,
              "modified_at": "2026-01-10", "date_vulcan_earliest_online": "2027-06-01" }
        ] }
        """;

        var rows = VulcanQuerySourceReader<DataCenterRow>.ParseRows(json);

        var row = Assert.Single(rows);
        Assert.Equal("P1-U1", row.SynmaxId);
        Assert.Equal("Alpha", row.PlantName);
        Assert.Equal(123.5, row.UnitCapacity);
        Assert.Equal(new DateTime(2026, 1, 10), row.ModifiedAt);
        Assert.Equal(new DateTime(2027, 6, 1), row.DateVulcanEarliestOnline);
    }

    [Fact]
    public void ParseRows_BareArray_IsTolerated()
    {
        var json = """[ { "synmax_id": "X" } ]""";
        var rows = VulcanQuerySourceReader<ProjectRankingRow>.ParseRows(json);
        Assert.Equal("X", Assert.Single(rows).SynmaxId);
    }

    [Fact]
    public void ParseRows_UnknownShape_ReturnsEmpty()
    {
        var rows = VulcanQuerySourceReader<ProjectRankingRow>.ParseRows("""{ "error": "nope" }""");
        Assert.Empty(rows);
    }
}
```

- [ ] **Step 3: (After Task 12) run the tests**

Run: `dotnet test tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj --filter VulcanQuerySourceReaderTests`
Expected: 3 passed.

- [ ] **Step 4: Commit**

```bash
git add src/DataLoader.Vulcan/VulcanQuerySourceReader.cs tests/DataLoader.Vulcan.Tests/VulcanQuerySourceReaderTests.cs
git commit -m "feat(vulcan): generic POST source reader + response parsing"
```

---

## Task 7: Work-unit provider

**Files:**
- Create: `src/DataLoader.Vulcan/VulcanWorkUnitProvider.cs`

Generic over `TRow` only so DI has a distinct closed source-reader per table; the provider itself just reads the watermark and yields one unit. It is constructed explicitly in the module (Task 9), so no interface-keyed registration is needed.

- [ ] **Step 1: Write `VulcanWorkUnitProvider.cs`**

```csharp
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Vulcan;

/// <summary>
/// Yields exactly one work unit for one Vulcan table: reads the stored watermark,
/// builds the incremental (or full-backfill) query, and stamps the run date.
/// </summary>
public sealed class VulcanWorkUnitProvider : IWorkUnitProvider<VulcanWorkUnit>
{
    private readonly VulcanTableSpec _spec;
    private readonly IVulcanWatermarkStore _watermarks;
    private readonly ILogger _logger;

    public VulcanWorkUnitProvider(VulcanTableSpec spec, IVulcanWatermarkStore watermarks, ILogger logger)
    {
        _spec = spec;
        _watermarks = watermarks;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VulcanWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var watermark = await _watermarks.GetAsync(_spec.TableId, context.CancellationToken).ConfigureAwait(false);
        var query = VulcanQueryBuilder.Build(_spec, watermark);

        _logger.LogInformation(
            "[{Table}] {Mode} load (watermark={Watermark})",
            _spec.TableId, watermark is null ? "full backfill" : "incremental",
            watermark?.ToString("yyyy-MM-dd") ?? "none");

        return new[]
        {
            new VulcanWorkUnit
            {
                TableId = _spec.TableId,
                RunDate = DateOnly.FromDateTime(context.StartedAtUtc),
                Query = query
            }
        };
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DataLoader.Vulcan/DataLoader.Vulcan.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DataLoader.Vulcan/VulcanWorkUnitProvider.cs
git commit -m "feat(vulcan): work-unit provider with watermark-driven query"
```

---

## Task 8: Sinks

**Files:**
- Create: `src/DataLoader.Vulcan/Sinks.cs`

Each sink extends `SqlSinkBase<TRow>`: supply connection string, proc name, TVP type, and a `BuildTable` whose column order matches the TVP in Task 1/Step 2. Use the base helpers `NullIfEmpty`, `DbNullable<T>`, `DbNullableObj`.

- [ ] **Step 1: Write `Sinks.cs`**

```csharp
using System.Data;
using DataLoader.Core.Sinks;
using DataLoader.Vulcan.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

public sealed class UnderConstructionSink : SqlSinkBase<UnderConstructionRow>
{
    private readonly VulcanSettings _s;
    public UnderConstructionSink(IOptions<VulcanSettings> s, ILogger<UnderConstructionSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeUnderConstruction";
    protected override string TableValuedParameterType => "dbo.UnderConstructionTvp";
    protected override DataTable BuildTable(IReadOnlyList<UnderConstructionRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantId", typeof(string));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("NameplateCapacity", typeof(decimal));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("ProjectRank", typeof(decimal));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("BalancingAuthority", typeof(string));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantId), NullIfEmpty(r.PlantName), NullIfEmpty(r.Technology),
                DbNullable((decimal?)r.NameplateCapacity), NullIfEmpty(r.VulcanStatus), DbNullable((decimal?)r.ProjectRank),
                DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline), NullIfEmpty(r.StateCode),
                NullIfEmpty(r.BalancingAuthority), DbNullable(r.Latitude), DbNullable(r.Longitude),
                DbNullable(r.DateVulcanStatusChange), DbNullable(r.DateImage));
        return t;
    }
}

public sealed class DataCentersSink : SqlSinkBase<DataCenterRow>
{
    private readonly VulcanSettings _s;
    public DataCentersSink(IOptions<VulcanSettings> s, ILogger<DataCentersSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeDataCenters";
    protected override string TableValuedParameterType => "dbo.DataCentersTvp";
    protected override DataTable BuildTable(IReadOnlyList<DataCenterRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantId", typeof(string));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("UnitId", typeof(string));
        t.Columns.Add("UnitName", typeof(string));
        t.Columns.Add("OwnerName", typeof(string));
        t.Columns.Add("DataCenterType", typeof(string));
        t.Columns.Add("UnitCapacity", typeof(decimal));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("BalancingAuthority", typeof(string));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("ModifiedAt", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantId), NullIfEmpty(r.PlantName), NullIfEmpty(r.UnitId),
                NullIfEmpty(r.UnitName), NullIfEmpty(r.OwnerName), NullIfEmpty(r.DataCenterType),
                DbNullable((decimal?)r.UnitCapacity), NullIfEmpty(r.VulcanStatus), NullIfEmpty(r.StateCode),
                NullIfEmpty(r.BalancingAuthority), DbNullable(r.DateVulcanEarliestOnline),
                DbNullable(r.DateVulcanLatestOnline), DbNullable(r.ModifiedAt));
        return t;
    }
}

public sealed class LngProjectsSink : SqlSinkBase<LngProjectRow>
{
    private readonly VulcanSettings _s;
    public LngProjectsSink(IOptions<VulcanSettings> s, ILogger<LngProjectsSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeLngProjects";
    protected override string TableValuedParameterType => "dbo.LngProjectsTvp";
    protected override DataTable BuildTable(IReadOnlyList<LngProjectRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("PhaseNumber", typeof(int));
        t.Columns.Add("EntityName", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("NameplateCapacity", typeof(double));
        t.Columns.Add("CapacityUnit", typeof(string));
        t.Columns.Add("Trains", typeof(int));
        t.Columns.Add("VulcanStatus", typeof(string));
        t.Columns.Add("DateVulcanEarliestOnline", typeof(DateTime));
        t.Columns.Add("DateVulcanLatestOnline", typeof(DateTime));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("Observation", typeof(string));
        t.Columns.Add("DateVulcanStatusChange", typeof(DateTime));
        t.Columns.Add("DateImage", typeof(DateTime));
        t.Columns.Add("ModifiedAt", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(NullIfEmpty(r.PlantName), r.PhaseNumber, NullIfEmpty(r.EntityName), NullIfEmpty(r.Technology),
                DbNullable(r.NameplateCapacity), NullIfEmpty(r.CapacityUnit), DbNullable(r.Trains),
                NullIfEmpty(r.VulcanStatus), DbNullable(r.DateVulcanEarliestOnline), DbNullable(r.DateVulcanLatestOnline),
                DbNullable(r.Latitude), DbNullable(r.Longitude), NullIfEmpty(r.Observation),
                DbNullable(r.DateVulcanStatusChange), DbNullable(r.DateImage), DbNullable(r.ModifiedAt));
        return t;
    }
}

public sealed class ProjectRankingsSink : SqlSinkBase<ProjectRankingRow>
{
    private readonly VulcanSettings _s;
    public ProjectRankingsSink(IOptions<VulcanSettings> s, ILogger<ProjectRankingsSink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeProjectRankings";
    protected override string TableValuedParameterType => "dbo.ProjectRankingsTvp";
    protected override DataTable BuildTable(IReadOnlyList<ProjectRankingRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantId", typeof(long));
        t.Columns.Add("GeneratorId", typeof(string));
        t.Columns.Add("FinalRank", typeof(decimal));
        t.Columns.Add("DateVulcanProposedV2Online", typeof(DateTime));
        t.Columns.Add("DateVulcanProposedOnline", typeof(DateTime));
        t.Columns.Add("DateUpdated", typeof(DateTime));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, DbNullable(r.PlantId), NullIfEmpty(r.GeneratorId), DbNullable((decimal?)r.FinalRank),
                DbNullable(r.DateVulcanProposedV2Online), DbNullable(r.DateVulcanProposedOnline), DbNullable(r.DateUpdated));
        return t;
    }
}

public sealed class MetadataHistorySink : SqlSinkBase<MetadataHistoryRow>
{
    private readonly VulcanSettings _s;
    public MetadataHistorySink(IOptions<VulcanSettings> s, ILogger<MetadataHistorySink> log) : base(log) => _s = s.Value;
    protected override string GetConnectionString() => _s.ConnectionString;
    protected override string StoredProcedureName => "dbo.usp_BulkMergeMetadataHistory";
    protected override string TableValuedParameterType => "dbo.MetadataHistoryTvp";
    protected override DataTable BuildTable(IReadOnlyList<MetadataHistoryRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("SynmaxId", typeof(string));
        t.Columns.Add("PlantName", typeof(string));
        t.Columns.Add("Technology", typeof(string));
        t.Columns.Add("NameplateCapacity", typeof(double));
        t.Columns.Add("StateCode", typeof(string));
        t.Columns.Add("County", typeof(string));
        t.Columns.Add("DatePlannedOperations", typeof(DateTime));
        t.Columns.Add("PlantStatus", typeof(string));
        t.Columns.Add("DateEiaUpdated", typeof(DateTime));
        t.Columns.Add("DaysPlannedOperationMinusFirstSeenPlannedOperation", typeof(double));
        t.Columns.Add("Latitude", typeof(double));
        t.Columns.Add("Longitude", typeof(double));
        t.Columns.Add("BalancingAuthorityCode", typeof(string));
        t.Columns.Add("SectorName", typeof(string));
        foreach (var r in rows)
            t.Rows.Add(r.SynmaxId, NullIfEmpty(r.PlantName), NullIfEmpty(r.Technology), DbNullable(r.NameplateCapacity),
                NullIfEmpty(r.StateCode), NullIfEmpty(r.County), DbNullable(r.DatePlannedOperations),
                NullIfEmpty(r.PlantStatus), DbNullable(r.DateEiaUpdated),
                DbNullable(r.DaysPlannedOperationMinusFirstSeenPlannedOperation), DbNullable(r.Latitude),
                DbNullable(r.Longitude), NullIfEmpty(r.BalancingAuthorityCode), NullIfEmpty(r.SectorName));
        return t;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DataLoader.Vulcan/DataLoader.Vulcan.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/DataLoader.Vulcan/Sinks.cs
git commit -m "feat(vulcan): SQL sinks for the five tables"
```

---

## Task 9: Pipeline + module wiring

**Files:**
- Create: `src/DataLoader.Vulcan/VulcanModule.cs`

Defines a per-table pipeline marker so `RunAsync` can enumerate all five, constructs each closed pipeline explicitly (avoiding interface-keyed DI collisions on the shared `VulcanWorkUnit`), and aggregates results.

- [ ] **Step 1: Write `VulcanModule.cs`**

```csharp
using DataLoader.Core.Abstractions;
using DataLoader.Core.Pipeline;
using DataLoader.Core.Resilience;
using DataLoader.Core.Transforms;
using DataLoader.Vulcan.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Vulcan;

/// <summary>Marker so VulcanModule.RunAsync can enumerate all per-table pipelines.</summary>
public interface IVulcanTablePipeline : ILoaderPipeline
{
    string TableId { get; }
}

/// <summary>One closed per-table pipeline. Reuses the platform's standard ETL loop.</summary>
public sealed class VulcanTablePipeline<TRow> : LoaderPipelineBase<VulcanWorkUnit, TRow, TRow>, IVulcanTablePipeline
{
    public VulcanTablePipeline(
        string tableId,
        IWorkUnitProvider<VulcanWorkUnit> workUnits,
        ISourceReader<VulcanWorkUnit, TRow> source,
        ISink<TRow> sink,
        ILoadLogRepository loadLog,
        VulcanSettings settings,
        ILogger logger)
        : base(VulcanModule.Id, workUnits, source, new IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
    {
        TableId = tableId;
    }

    public string TableId { get; }
}

public sealed class VulcanModule : ILoaderModule
{
    public const string Id = "Vulcan";
    public const string HttpClientName = "Vulcan";

    public string LoaderId => Id;
    public string DisplayName => "SynMax Vulcan (POST SQL query_datalinks, incremental)";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VulcanSettings>(configuration.GetSection($"Loaders:{Id}"));

        services.AddHttpClient(HttpClientName, (sp, client) =>
        {
            var s = sp.GetRequiredService<IOptions<VulcanSettings>>().Value;
            if (!string.IsNullOrWhiteSpace(s.BaseUrl))
                client.BaseAddress = new Uri(s.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(s.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(s.ApiKey))
                client.DefaultRequestHeaders.Add("Access-Key", s.ApiKey);
        })
        .AddPolicyHandler((sp, _) =>
        {
            var s = sp.GetRequiredService<IOptions<VulcanSettings>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Vulcan.Http");
            return RetryPolicyFactory.BuildHttpRetryPolicy(
                s.RetryCount, s.RetryDelayMs,
                (outcome, delay, attempt) =>
                    log.LogWarning("HTTP retry {Attempt}: status {Status}, waiting {Delay}ms",
                        attempt, outcome.Result?.StatusCode, delay.TotalMilliseconds));
        });

        services.AddSingleton<IVulcanWatermarkStore, VulcanWatermarkStore>();

        services.AddSingleton<UnderConstructionSink>();
        services.AddSingleton<DataCentersSink>();
        services.AddSingleton<LngProjectsSink>();
        services.AddSingleton<ProjectRankingsSink>();
        services.AddSingleton<MetadataHistorySink>();

        // Build the five closed pipelines explicitly (the shared VulcanWorkUnit type
        // means an interface-keyed IWorkUnitProvider<VulcanWorkUnit> would collide).
        RegisterTablePipeline<UnderConstructionRow>(services, VulcanTableSpec.UnderConstruction,
            sp => sp.GetRequiredService<UnderConstructionSink>());
        RegisterTablePipeline<DataCenterRow>(services, VulcanTableSpec.DataCenters,
            sp => sp.GetRequiredService<DataCentersSink>());
        RegisterTablePipeline<LngProjectRow>(services, VulcanTableSpec.LngProjects,
            sp => sp.GetRequiredService<LngProjectsSink>());
        RegisterTablePipeline<ProjectRankingRow>(services, VulcanTableSpec.ProjectRankings,
            sp => sp.GetRequiredService<ProjectRankingsSink>());
        RegisterTablePipeline<MetadataHistoryRow>(services, VulcanTableSpec.MetadataHistory,
            sp => sp.GetRequiredService<MetadataHistorySink>());
    }

    private static void RegisterTablePipeline<TRow>(
        IServiceCollection services, VulcanTableSpec spec, Func<IServiceProvider, ISink<TRow>> sinkFactory)
    {
        services.AddSingleton<IVulcanTablePipeline>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<VulcanSettings>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

            var provider = new VulcanWorkUnitProvider(
                spec, sp.GetRequiredService<IVulcanWatermarkStore>(),
                loggerFactory.CreateLogger($"Vulcan.{spec.TableId}.Provider"));

            var source = new VulcanQuerySourceReader<TRow>(
                httpClient, sp.GetRequiredService<IOptions<VulcanSettings>>(),
                loggerFactory.CreateLogger($"Vulcan.{spec.TableId}.Source"));

            return new VulcanTablePipeline<TRow>(
                spec.TableId, provider, source, sinkFactory(sp),
                sp.GetRequiredService<ILoadLogRepository>(), settings,
                loggerFactory.CreateLogger($"Vulcan.{spec.TableId}.Pipeline"));
        });
    }

    public async Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context)
    {
        var settings = services.GetRequiredService<IOptions<VulcanSettings>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Vulcan.Module");
        var enabled = new HashSet<string>(settings.EnabledTables, StringComparer.OrdinalIgnoreCase);

        var pipelines = services.GetServices<IVulcanTablePipeline>()
            .Where(p => enabled.Contains(p.TableId))
            .ToList();

        if (pipelines.Count == 0)
        {
            logger.LogWarning("No Vulcan tables enabled");
            return new LoaderRunResult { LoaderId = Id, Success = true };
        }

        var results = new List<LoaderRunResult>();
        foreach (var pipeline in pipelines)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await pipeline.ExecuteAsync(context).ConfigureAwait(false));
        }

        return new LoaderRunResult
        {
            LoaderId = Id,
            Success = results.All(r => r.Success),
            WorkUnitsTotal = results.Sum(r => r.WorkUnitsTotal),
            WorkUnitsSucceeded = results.Sum(r => r.WorkUnitsSucceeded),
            WorkUnitsSkipped = results.Sum(r => r.WorkUnitsSkipped),
            WorkUnitsFailed = results.Sum(r => r.WorkUnitsFailed),
            RecordsProcessed = results.Sum(r => r.RecordsProcessed),
            ErrorMessage = string.Join("; ", results.Where(r => !r.Success && r.ErrorMessage != null).Select(r => r.ErrorMessage)),
            Duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration)
        };
    }
}
```

- [ ] **Step 2: Verify `RetryPolicyFactory` namespace/signature.**

Run: `grep -rn "class RetryPolicyFactory\|BuildHttpRetryPolicy" src/DataLoader.Core`
Expected: shows `DataLoader.Core.Resilience.RetryPolicyFactory.BuildHttpRetryPolicy(int, int, Action<...>)`. If the namespace differs, fix the `using` in `VulcanModule.cs`. (The EnergyAspects module uses the same call, so this should match.)

- [ ] **Step 3: Build the whole solution**

Run: `dotnet build DataLoaderPlatform.sln -c Release`
Expected: Build succeeded. The module is now discoverable (assembly `DataLoader.Vulcan.dll` matches the `DataLoader.*.dll` discovery pattern).

- [ ] **Step 4: Commit**

```bash
git add src/DataLoader.Vulcan/VulcanModule.cs
git commit -m "feat(vulcan): pipeline and module wiring"
```

---

## Task 10: Configuration

**Files:**
- Modify: `src/DataLoader.Host/appsettings.json`

- [ ] **Step 1: Add the `Vulcan` loader section.** Inside `Loaders`, after the `FtpExample` block (add a comma after the `FtpExample` closing brace):

```json
    "Vulcan": {
      "ConnectionString": "Server=ARMH-OPSDB01;Database=Vulcan;Integrated Security=SSPI;TrustServerCertificate=True;",
      "MaxConcurrentWorkUnits": 5,
      "RetryCount": 3,
      "RetryDelayMs": 1000,
      "WorkUnitTimeoutSeconds": 300,
      "ApiKey": "REPLACE-VIA-ENV-VAR",
      "BaseUrl": "https://hyperion.api.synmax.com",
      "QueryEndpoint": "/v4/beta/query_datalinks",
      "HttpTimeoutSeconds": 60,
      "EnabledTables": [ "under_construction", "datacenters", "lng_projects", "project_rankings", "metadata_history" ]
    }
```

- [ ] **Step 2: Add `"Vulcan"` to `Platform:EnabledLoaders`.** Change:
```json
    "EnabledLoaders": [ "EnergyAspects", "CsvExample", "FtpExample" ],
```
to:
```json
    "EnabledLoaders": [ "EnergyAspects", "CsvExample", "FtpExample", "Vulcan" ],
```

- [ ] **Step 3: Validate JSON + build**

Run: `dotnet build DataLoaderPlatform.sln -c Release`
Expected: Build succeeded (appsettings.json copies to output; malformed JSON fails at runtime, so also eyeball the commas).

- [ ] **Step 4: Commit**

```bash
git add src/DataLoader.Host/appsettings.json
git commit -m "feat(vulcan): appsettings config + enable loader"
```

---

## Task 11: Discovery smoke test

**Files:** none (runtime check).

- [ ] **Step 1: Confirm the host discovers the loader without running it against a real DB/API.**

Run (from repo root, after Release build):
```
dotnet run --project src/DataLoader.Host -c Release -- NoSuchLoader
```
Expected: exits non-zero and logs `Unknown loader 'NoSuchLoader'. Known: EnergyAspects, CsvExample, FtpExample, Vulcan` — proving `Vulcan` is discovered and registered. (We use a bad arg deliberately so nothing actually connects to SQL/HTTP.)

- [ ] **Step 2: No commit** (observation only). If `Vulcan` is absent from the "Known" list, the ProjectReference (Task 0/Step 2) or module discovery is wrong — fix before proceeding.

---

## Task 12: Test project

**Files:**
- Create: `tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj`
- Modify: `DataLoaderPlatform.sln`
- (Test files `VulcanQueryBuilderTests.cs` and `VulcanQuerySourceReaderTests.cs` were written in Tasks 3 and 6.)

- [ ] **Step 1: Create the test csproj**

`tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DataLoader.Vulcan\DataLoader.Vulcan.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to solution**

Run: `dotnet sln DataLoaderPlatform.sln add tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 3: Run all Vulcan tests** (the query-builder and parser tests from Tasks 3 & 6)

Run: `dotnet test tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 7` (4 query-builder + 3 parser).

- [ ] **Step 4: Commit**

```bash
git add tests/DataLoader.Vulcan.Tests/DataLoader.Vulcan.Tests.csproj DataLoaderPlatform.sln
git commit -m "test(vulcan): xunit project for query builder and response parsing"
```

---

## Task 13: Docs + provisioning notes

**Files:**
- Modify: `CLAUDE.md` (add Vulcan to reference implementations list — optional but keeps docs honest)

- [ ] **Step 1: Add a bullet** under "Reference implementations:" in `CLAUDE.md`:
```markdown
- `DataLoader.Vulcan/` — incremental REST loader (POST SQL to SynMax query_datalinks; 5 tables via 5 closed pipelines; watermark-based resume)
```

- [ ] **Step 2: Record provisioning order** — before the first run, an operator must:
  1. Create the `Vulcan` database.
  2. Run `sql/Vulcan/001_…`, `002_…`, `003_…` in order.
  3. Set the API key: `DATALOADER_Loaders__Vulcan__ApiKey=<key>` (env var).
  4. Run `DataLoader.Host.exe Vulcan`.

  Add these four steps as a comment block at the top of `sql/Vulcan/001_CreateVulcanSchema.sql` if not already implied.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md sql/Vulcan/001_CreateVulcanSchema.sql
git commit -m "docs(vulcan): reference entry and provisioning notes"
```

---

## Post-implementation verification (before declaring done)

- [ ] `dotnet build DataLoaderPlatform.sln -c Release` — succeeds.
- [ ] `dotnet test` — all Vulcan tests pass.
- [ ] `dotnet run --project src/DataLoader.Host -c Release -- Vulcan` against a provisioned DB + valid API key — first run backfills all five tables; `dbo.LoadWatermark` has five rows; a second immediate run **skips** (same-day work-unit key already succeeded in `core.LoadLog`).
- [ ] **Open item (spec §11.1):** confirm the real JSON response envelope. If it is not `{ "data": [...] }` or a bare array, update only `VulcanQuerySourceReader.ParseRows` and its test. Consider dispatching `API_DOCUMENTATION_EXPERT` with a captured sample.
- [ ] **Data validation:** dispatch `DATA_QUALITY_VALIDATOR` after the first successful load (row counts vs. source, null/range checks on capacities and dates, dedup correctness).
```
