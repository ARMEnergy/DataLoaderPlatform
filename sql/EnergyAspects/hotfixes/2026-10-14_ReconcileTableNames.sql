/* ---------------------------------------------------------------------------
   ARMH-OPSDB01 / EnergyAspects
   Reconcile prod's table names with the ones sql/EnergyAspects/001 declares,
   so the procedures 003 creates can actually resolve their targets.

   Fixes: Invalid object name 'dbo.DatasetMapping'
          (thrown by usp_BulkUpsertDatasetMappings and usp_GetActiveMappings,
           which is on the work-unit provider's critical path — the loader
           currently fails with 0/0 units before doing any work)

   prod (created 2026-04-30)        repo 001 expects
   -------------------------------  ---------------------------
   dbo.DatasetMappings      (plural) dbo.DatasetMapping
   dbo.DatasetMappingIds    (plural) dbo.DatasetMappingId
   (does not exist)                  dbo.ApiResponseLog

   Verified safe before writing this:
     - DatasetMappings: 472 rows, 472 distinct MappingId, so the repo's
       MERGE ... ON tgt.MappingId = src.MappingId cannot hit error 8672.
     - DatasetMappingIds: 279,350 rows, 0 duplicate (MappingId, DatasetId),
       so the repo's MERGE on that pair is safe too.
     - prod's extra columns all tolerate the repo procs' narrower INSERT lists:
       Id is IDENTITY, DateCreated defaults to getdate(), and LastRefreshed /
       LatestResponseCode are NULLable.

   sp_rename is metadata-only and instant. Indexes and constraints keep their
   existing names (PK_DatasetMappings etc.) — cosmetic only, nothing resolves
   them by name.

   Run section by section and read the output of each.
   --------------------------------------------------------------------------- */

USE EnergyAspects;
GO

SET XACT_ABORT ON;
GO

-- ===========================================================================
-- 0. PRE-FLIGHT — confirm the starting state before changing anything.
-- ===========================================================================
SELECT  TableName        = t.name,
        Rows             = SUM(p.rows),
        ExpectedByRepo   = CASE t.name WHEN 'DatasetMappings'   THEN 'rename -> DatasetMapping'
                                       WHEN 'DatasetMappingIds' THEN 'rename -> DatasetMappingId'
                                       ELSE 'unchanged' END
FROM    sys.tables t
JOIN    sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY t.name
ORDER BY t.name;
GO

-- ===========================================================================
-- 1. Rename the two mapping tables.
-- ===========================================================================
IF OBJECT_ID('dbo.DatasetMappings', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.DatasetMapping', 'U') IS NULL
BEGIN
    EXEC sp_rename 'dbo.DatasetMappings', 'DatasetMapping';
    PRINT 'renamed dbo.DatasetMappings -> dbo.DatasetMapping';
END
ELSE
    PRINT 'skipped: DatasetMapping already exists or DatasetMappings is gone';
GO

IF OBJECT_ID('dbo.DatasetMappingIds', 'U') IS NOT NULL
   AND OBJECT_ID('dbo.DatasetMappingId', 'U') IS NULL
BEGIN
    EXEC sp_rename 'dbo.DatasetMappingIds', 'DatasetMappingId';
    PRINT 'renamed dbo.DatasetMappingIds -> dbo.DatasetMappingId';
END
ELSE
    PRINT 'skipped: DatasetMappingId already exists or DatasetMappingIds is gone';
GO

-- ===========================================================================
-- 2. ModifiedAtUtc on dbo.DatasetMapping (472 rows).
--    Same approach as 2026-10-14_AddModifiedAtUtc.sql: add NULL, backfill a
--    truthful value, then make it NOT NULL + defaulted. LastRefreshed is the
--    closest thing prod has to "last modified"; fall back to DateCreated.
--    Both are GETDATE() (server LOCAL), so convert to UTC via AT TIME ZONE.
-- ===========================================================================
IF COL_LENGTH('dbo.DatasetMapping', 'ModifiedAtUtc') IS NULL
BEGIN
    ALTER TABLE dbo.DatasetMapping ADD ModifiedAtUtc DATETIME2(3) NULL;
END
GO

UPDATE dbo.DatasetMapping
SET    ModifiedAtUtc = CAST(COALESCE(LastRefreshed, DateCreated)
                                AT TIME ZONE 'Central Standard Time'
                                AT TIME ZONE 'UTC' AS DATETIME2(3))
WHERE  ModifiedAtUtc IS NULL;
PRINT CONCAT('DatasetMapping backfilled: ', @@ROWCOUNT, ' rows');
GO

ALTER TABLE dbo.DatasetMapping ALTER COLUMN ModifiedAtUtc DATETIME2(3) NOT NULL;
GO

IF OBJECT_ID('dbo.DF_DatasetMapping_Modified', 'D') IS NULL
BEGIN
    ALTER TABLE dbo.DatasetMapping
        ADD CONSTRAINT DF_DatasetMapping_Modified DEFAULT SYSUTCDATETIME() FOR ModifiedAtUtc;
END
GO

-- ===========================================================================
-- 3. dbo.ApiResponseLog — new table, exactly as 001 declares it.
--
--    ⚠ BEHAVIOUR CHANGE. The repo's usp_LogApiResponse (already deployed by
--    your 003 run) APPENDS to this table and does NOT touch
--    DatasetMapping.LatestResponseCode, which the previous prod proc
--    maintained. That column will freeze at its current values from now on.
--    Nothing in the loader reads it, but check reports/queries before relying
--    on it. Its live successor is:
--        SELECT TOP 1 ResponseCode FROM dbo.ApiResponseLog
--        WHERE MappingId = @m ORDER BY LoggedAtUtc DESC;
-- ===========================================================================
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
    PRINT 'created dbo.ApiResponseLog';
END
GO

-- ===========================================================================
-- 4. Rebuild dbo.V_TimeseriesData against the new names.
--    sp_rename does NOT rewrite views: the existing one still says
--    dbo.DatasetMappings / dbo.DatasetMappingIds and would fail at runtime.
--    Body is otherwise byte-identical to what was deployed, NOLOCK hints and
--    column order included, so anything selecting from it is unaffected.
-- ===========================================================================
IF OBJECT_ID('dbo.V_TimeseriesData', 'V') IS NOT NULL
    DROP VIEW dbo.V_TimeseriesData;
GO
CREATE VIEW dbo.V_TimeseriesData
AS
SELECT  T.Id,
        M.MappingId,
        M.MappingName,
        M.Category MappingCategory,
        D.DatasetId,
        D.Country,
        D.CountryIso,
        D.Region,
        D.SubRegion,
        D.Description,
        D.Aspect,
        D.AspectSubtype,
        D.Category,
        D.CategorySubtype,
        D.ForecastStartDate,
        D.Frequency,
        D.LifecycleStage,
        D.Source,
        D.Unit,
        D.ReleaseDate,
        D.Basin,
        D.PartnerSubRegion,
        D.Pipeline,
        D.EaIsoRto,
        T.DataDate,
        T.Value
    FROM dbo.DatasetMapping M (NOLOCK)
         JOIN dbo.DatasetMappingId DI (NOLOCK) ON M.MappingId = DI.MappingId
         JOIN dbo.DatasetMetadata D (NOLOCK) ON DI.DatasetId = D.DatasetId
         JOIN dbo.TimeseriesData T (NOLOCK) ON D.DatasetId = T.DatasetId;
GO

-- ===========================================================================
-- 5. REPORT the legacy procs the rename leaves dangling.
--
--    usp_UpsertDatasetMapping, usp_UpsertDatasetMappingId and
--    usp_GetLoadLogSummary still reference the plural names. NONE of them is
--    called by the loader — the C# calls only the six Bulk*/GetActiveMappings/
--    LogApiResponse procs — so they are pre-platform leftovers. They are
--    REPORTED, not dropped: deleting someone else's procedure is your call,
--    not this script's.
-- ===========================================================================
SELECT  DanglingProc   = o.name,
        Referencing    = ed.referenced_entity_name,
        CalledByLoader = 'no'
FROM    sys.sql_expression_dependencies ed
JOIN    sys.objects o ON o.object_id = ed.referencing_id
WHERE   ed.referenced_entity_name IN ('DatasetMappings', 'DatasetMappingIds')
  AND   o.type = 'P'
ORDER BY o.name;
GO

-- ===========================================================================
-- 6. VERIFY — every object the six loader procs need must now resolve.
-- ===========================================================================
SELECT  ObjectName = n.nm,
        Status     = CASE WHEN OBJECT_ID('dbo.' + n.nm) IS NULL THEN 'MISSING' ELSE 'ok' END
FROM   (VALUES ('DatasetMapping'), ('DatasetMappingId'), ('TimeseriesData'),
               ('DatasetMetadata'), ('ApiResponseLog'), ('V_TimeseriesData'),
               ('usp_BulkUpsertDatasetMappings'), ('usp_BulkUpsertDatasetMappingIds'),
               ('usp_GetActiveMappings'), ('usp_BulkMergeTimeseriesData'),
               ('usp_BulkUpsertDatasetMetadata'), ('usp_LogApiResponse')) AS n(nm)
ORDER BY Status DESC, ObjectName;

-- Row counts must be unchanged by the rename: 472 and 279,350.
SELECT TableName = t.name, Rows = SUM(p.rows)
FROM   sys.tables t
JOIN   sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE  t.name IN ('DatasetMapping', 'DatasetMappingId')
GROUP BY t.name;

-- The view must return rows (proves all four joins resolve).
SELECT ViewRowSample = COUNT(*) FROM (SELECT TOP 100 1 AS x FROM dbo.V_TimeseriesData) v;
GO
