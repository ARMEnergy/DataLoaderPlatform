/* ---------------------------------------------------------------------------
   ARMH-OPSDB01 / EnergyAspects
   Add ModifiedAtUtc to the two tables that 003 needs it on.

   Clears both errors:
     Msg 207 ... Procedure usp_BulkMergeTimeseriesData  -> dbo.TimeseriesData
     Msg 207 ... Procedure usp_BulkUpsertDatasetMetadata -> dbo.DatasetMetadata

   Ends with exactly the column 001 would have created — same type, same
   nullability, same DEFAULT constraint NAME (DF_TSData_Modified /
   DF_DatasetMetadata_Modified) — so prod and the repo agree afterwards.

   Added NULL first, backfilled, then made NOT NULL + defaulted. The obvious
   one-liner (ADD ... NOT NULL DEFAULT SYSUTCDATETIME()) would stamp all
   833,366 rows with the moment of the ALTER, asserting every row was modified
   today. DateCreated already records the truth, so backfill from it.

   DateCreated is written by GETDATE() = server LOCAL time (US Central), while
   ModifiedAtUtc means UTC. AT TIME ZONE does the conversion DST-correctly; a
   fixed -5 would be wrong for every row loaded outside daylight time.
   Caveat: during the fall-back hour an ambiguous local time resolves to the
   standard-time reading, and a spring-forward time that never existed shifts
   forward. Acceptable for an audit column.

   Run one section at a time and check the row counts it prints.
   --------------------------------------------------------------------------- */

USE EnergyAspects;
GO

SET XACT_ABORT ON;
GO

-- ===========================================================================
-- 1. dbo.TimeseriesData   (833,366 rows)
-- ===========================================================================
IF COL_LENGTH('dbo.TimeseriesData', 'ModifiedAtUtc') IS NULL
BEGIN
    ALTER TABLE dbo.TimeseriesData ADD ModifiedAtUtc DATETIME2(3) NULL;
END
GO

UPDATE dbo.TimeseriesData
SET    ModifiedAtUtc = CAST(DateCreated AT TIME ZONE 'Central Standard Time'
                                        AT TIME ZONE 'UTC' AS DATETIME2(3))
WHERE  ModifiedAtUtc IS NULL;
PRINT CONCAT('TimeseriesData backfilled: ', @@ROWCOUNT, ' rows');
GO

ALTER TABLE dbo.TimeseriesData ALTER COLUMN ModifiedAtUtc DATETIME2(3) NOT NULL;
GO

IF OBJECT_ID('dbo.DF_TSData_Modified', 'D') IS NULL
BEGIN
    ALTER TABLE dbo.TimeseriesData
        ADD CONSTRAINT DF_TSData_Modified DEFAULT SYSUTCDATETIME() FOR ModifiedAtUtc;
END
GO

-- ===========================================================================
-- 2. dbo.DatasetMetadata   (18,737 rows)
--    Has both DateCreated and DateUpdated. ModifiedAtUtc means "last
--    modified", so prefer DateUpdated and fall back to DateCreated.
-- ===========================================================================
IF COL_LENGTH('dbo.DatasetMetadata', 'ModifiedAtUtc') IS NULL
BEGIN
    ALTER TABLE dbo.DatasetMetadata ADD ModifiedAtUtc DATETIME2(3) NULL;
END
GO

UPDATE dbo.DatasetMetadata
SET    ModifiedAtUtc = CAST(COALESCE(DateUpdated, DateCreated)
                                AT TIME ZONE 'Central Standard Time'
                                AT TIME ZONE 'UTC' AS DATETIME2(3))
WHERE  ModifiedAtUtc IS NULL;
PRINT CONCAT('DatasetMetadata backfilled: ', @@ROWCOUNT, ' rows');
GO

ALTER TABLE dbo.DatasetMetadata ALTER COLUMN ModifiedAtUtc DATETIME2(3) NOT NULL;
GO

IF OBJECT_ID('dbo.DF_DatasetMetadata_Modified', 'D') IS NULL
BEGIN
    ALTER TABLE dbo.DatasetMetadata
        ADD CONSTRAINT DF_DatasetMetadata_Modified DEFAULT SYSUTCDATETIME() FOR ModifiedAtUtc;
END
GO

-- ===========================================================================
-- 3. Verify, then re-run sql/EnergyAspects/003.
-- ===========================================================================
SELECT t.name AS TableName, c.name AS ColumnName, ty.name AS DataType,
       c.is_nullable, dc.name AS DefaultConstraint, dc.definition
FROM   sys.tables t
JOIN   sys.columns c  ON c.object_id = t.object_id AND c.name = 'ModifiedAtUtc'
JOIN   sys.types  ty  ON ty.user_type_id = c.user_type_id
LEFT   JOIN sys.default_constraints dc
          ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE  t.name IN ('TimeseriesData', 'DatasetMetadata');

-- Sanity: backfilled values must be ~5h AHEAD of DateCreated (UTC-5 today),
-- and no row may sit in the future.
SELECT TOP 5
       DateCreated, ModifiedAtUtc,
       OffsetHours = DATEDIFF(hour, DateCreated, ModifiedAtUtc)
FROM   dbo.TimeseriesData
ORDER  BY Id DESC;

SELECT FutureRows = COUNT(*)
FROM   dbo.TimeseriesData
WHERE  ModifiedAtUtc > SYSUTCDATETIME();
GO
