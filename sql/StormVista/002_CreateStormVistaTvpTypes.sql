-- =============================================================================
-- 002_CreateStormVistaTvpTypes.sql
-- Table-valued parameter types for the bulk merges (schema [dbo]).
--
-- Column ORDER here is the C# sink contract: it must match the DataTable order
-- the sinks build. Do NOT reorder without updating the sinks.
--
-- Both TVPs now carry FileLogId (the parent file/request row resolved by
-- dbo.usp_UpsertFileLog before the facts are written) INSTEAD of the raw
-- Model/InitDate/Cycle/WddType[/RegionSetCode] dimension strings — the loader
-- gets the FileLogId back from usp_UpsertFileLog and stamps it onto every row.
--   * DailyWddTvp still carries the numeric FlagCode (resolved to FlagId in 003).
--   * RegionalWddTvp still carries the RegionName (resolved to RegionId in 003,
--     scoped by the parent FileLog's RegionSet).
--
-- The TVPs intentionally do NOT carry Id / DateCreated / ModifiedAtUtc: those are
-- populated by the target table's IDENTITY / DEFAULTs. There is no PK on the type
-- — the merge procs de-dup the batch on the merge key (see 003), so a same-file
-- duplicate key cannot break the MERGE.
--
-- Run 001 first. This script assumes StormVista is the current database.
-- =============================================================================

USE StormVista;
GO

-- ----------------------------------------------------------------------------
-- dbo.DailyWddTvp — feeds dbo.usp_BulkMergeDailyWdd.
-- Order: (FileLogId, ValidDate, FlagCode, Value).
-- ----------------------------------------------------------------------------
IF TYPE_ID('dbo.DailyWddTvp') IS NULL
BEGIN
    CREATE TYPE dbo.DailyWddTvp AS TABLE
    (
        FileLogId INT          NOT NULL,
        ValidDate DATE         NOT NULL,
        FlagCode  INT          NOT NULL,
        [Value]   DECIMAL(9,4) NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- dbo.RegionalWddTvp — feeds dbo.usp_BulkMergeRegionalWdd.
-- Order: (FileLogId, RegionName, ValidDate, Value). No Flag (forecast-only).
-- ----------------------------------------------------------------------------
IF TYPE_ID('dbo.RegionalWddTvp') IS NULL
BEGIN
    CREATE TYPE dbo.RegionalWddTvp AS TABLE
    (
        FileLogId  INT          NOT NULL,
        RegionName VARCHAR(30)  NOT NULL,
        ValidDate  DATE         NOT NULL,
        [Value]    DECIMAL(9,4) NULL
    );
END
GO
