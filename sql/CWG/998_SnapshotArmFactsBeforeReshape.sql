-- =============================================================================
-- 998_SnapshotArmFactsBeforeReshape.sql
-- Copies every arm FACT table that 999_DropCwgObjects.sql is about to drop into
-- a parallel armbak schema, so the pre-reshape data survives the tear-down.
--
-- Run order:
--     998_SnapshotArmFactsBeforeReshape.sql   <-- this file
--     999_DropCwgObjects.sql
--     001_CreateCwgSchema.sql
--     002_CreateCwgTvpTypes.sql
--     003_CreateCwgProcedures.sql
--
-- Why this exists
-- -----------------------------------------------------------------------------
-- The arm facts hold ~1.92M rows that got there by MANUAL loader runs -- CWG is
-- not in Platform:EnabledLoaders, so nothing reloads them on a schedule. The
-- reshape has to drop those tables (001/002 are creation-guarded, so they cannot
-- alter an existing table), and without this snapshot that data is simply gone.
--
-- 🔒 SAFETY
-- -----------------------------------------------------------------------------
-- The same database's dbo schema holds the LIVE incumbent dataset -- ~96.6M rows
-- in tables with IDENTICAL names (dbo.CityForecast vs arm.CityForecast). Every
-- statement below reads from arm. and writes to armbak. and NOTHING here reads,
-- writes or drops anything in dbo. Keep it that way.
--
-- This script only ever CREATES tables. It issues no DROP, DELETE or TRUNCATE.
--
-- Re-runnable, and deliberately refuses to overwrite
-- -----------------------------------------------------------------------------
-- If a backup table already exists it is SKIPPED, not replaced. That is the
-- point: re-running this AFTER 999 (or after the new empty tables exist) must
-- never overwrite a good backup with an empty one.
--
-- Restoring later
-- -----------------------------------------------------------------------------
-- SELECT * INTO carries columns and data but NOT keys, indexes, defaults or FKs.
-- These are a data archive, not drop-in replacements. Restoring into the new
-- shape means an explicit column mapping, because the reshape renamed columns
-- (e.g. FcstMin/FcstMax/FcstAvg -> ForecastMin/ForecastMax/ForecastAvg,
-- ForecastDate -> [Date]) and dropped FileLogId/DateCreated.
-- =============================================================================

USE CWG;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

-- Guard against running this in the wrong database. RAISERROR substitution args
-- must be constants or variables, so DB_NAME() is captured first.
DECLARE @CurrentDb SYSNAME = DB_NAME();
IF @CurrentDb <> N'CWG'
BEGIN
    RAISERROR('998: wrong database (%s). Run this against CWG.', 16, 1, @CurrentDb);
    SET NOEXEC ON;
END
GO

IF SCHEMA_ID('armbak') IS NULL
    EXEC ('CREATE SCHEMA armbak AUTHORIZATION dbo;');
GO

-- -----------------------------------------------------------------------------
-- The 19 fact tables 999 drops. The four it KEEPS (FileLog, Endpoint, Status,
-- Region) are deliberately absent -- they survive the tear-down untouched.
-- -----------------------------------------------------------------------------
DECLARE @Suffix       SYSNAME = CONVERT(CHAR(8), SYSUTCDATETIME(), 112);  -- yyyymmdd
DECLARE @Table        SYSNAME,
        @Target       SYSNAME,
        @Sql          NVARCHAR(MAX),
        @Copied       INT = 0,
        @Skipped      INT = 0,
        @Absent       INT = 0;

DECLARE @Tables TABLE (Name SYSNAME PRIMARY KEY);
INSERT INTO @Tables (Name) VALUES
    ('CityForecast'),
    ('CityGasForecast'),
    ('CityObservation'),
    ('DailyNormal'),
    ('ISODegreeDays'),
    ('NationalDegreeDays'),
    ('Regions5DegreeDays'),
    ('Regions9DegreeDays'),
    ('SolarForecast'),
    ('SolarForecastChange'),
    ('SolarHourly'),
    ('StateDegreeDayObservation'),
    ('Station'),
    ('WindForecast'),
    ('WindForecastSubRegion'),
    ('WindHourly'),
    ('WindTotalCapacityClimatology'),
    ('WindTotalCapacityMW'),
    ('WindTotalCapacityPct');

DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT Name FROM @Tables ORDER BY Name;

OPEN table_cursor;
FETCH NEXT FROM table_cursor INTO @Table;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Target = @Table + N'_' + @Suffix;

    IF OBJECT_ID(QUOTENAME('arm') + '.' + QUOTENAME(@Table), 'U') IS NULL
    BEGIN
        PRINT 'SKIP (source absent): arm.' + @Table;
        SET @Absent = @Absent + 1;
    END
    ELSE IF OBJECT_ID(QUOTENAME('armbak') + '.' + QUOTENAME(@Target), 'U') IS NOT NULL
    BEGIN
        -- Never clobber an existing backup; see header.
        PRINT 'SKIP (backup exists): armbak.' + @Target;
        SET @Skipped = @Skipped + 1;
    END
    ELSE
    BEGIN
        SET @Sql = N'SELECT * INTO ' + QUOTENAME('armbak') + N'.' + QUOTENAME(@Target)
                 + N' FROM ' + QUOTENAME('arm') + N'.' + QUOTENAME(@Table) + N';';
        EXEC sp_executesql @Sql;
        PRINT 'COPIED: arm.' + @Table + ' -> armbak.' + @Target
            + ' (' + CAST(@@ROWCOUNT AS VARCHAR(20)) + ' rows)';
        SET @Copied = @Copied + 1;
    END

    FETCH NEXT FROM table_cursor INTO @Table;
END

CLOSE table_cursor;
DEALLOCATE table_cursor;

PRINT '---------------------------------------------------------------';
PRINT '998 done. Copied: ' + CAST(@Copied AS VARCHAR(10))
    + ', skipped (already backed up): ' + CAST(@Skipped AS VARCHAR(10))
    + ', source absent: ' + CAST(@Absent AS VARCHAR(10));
GO

-- -----------------------------------------------------------------------------
-- Verification. Source vs backup, per table. Check this BEFORE running 999:
-- every row must read MATCH, and the totals must agree.
-- -----------------------------------------------------------------------------
WITH RowCounts AS (
    SELECT s.name AS SchemaName,
           t.name AS TableName,
           SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS Rows_
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.partitions p ON p.object_id = t.object_id
    WHERE s.name IN ('arm', 'armbak')
    GROUP BY s.name, t.name
),
Pairs AS (
    SELECT src.TableName,
           src.Rows_ AS SourceRows,
           bak.TableName AS BackupTable,
           bak.Rows_ AS BackupRows
    FROM RowCounts src
    LEFT JOIN RowCounts bak
           ON bak.SchemaName = 'armbak'
          AND bak.TableName LIKE src.TableName + '[_]2%'
    WHERE src.SchemaName = 'arm'
      AND src.TableName NOT IN ('FileLog', 'Endpoint', 'Status', 'Region')
)
SELECT TableName,
       SourceRows,
       BackupTable,
       BackupRows,
       CASE WHEN BackupTable IS NULL      THEN 'NO BACKUP'
            WHEN BackupRows = SourceRows  THEN 'MATCH'
            ELSE 'MISMATCH'
       END AS Status
FROM Pairs
ORDER BY CASE WHEN BackupTable IS NULL OR BackupRows <> SourceRows THEN 0 ELSE 1 END,
         TableName;
GO

SET NOEXEC OFF;
GO
