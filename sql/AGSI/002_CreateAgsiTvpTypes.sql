-- =============================================================================
-- 002_CreateAgsiTvpTypes.sql
-- Table-valued parameter types for the 2 AGSI bulk merges (schema [arm]).
--
-- Column ORDER here is the C# sink contract — the sink's BuildTable mirrors each
-- type's order EXACTLY. Do NOT reorder either side without updating the other
-- (a load-bearing contract). The two tables use DIFFERENT FileLogId positions,
-- per the task's Script-002 directive:
--   * arm.GasStorageEntityTvp — FileLogId is the LAST column
--     (Code, Name, ParentCode, ParentName, FileLogId).
--   * arm.GasStorageTvp       — FileLogId is the FIRST column, EntityId the SECOND,
--     then the remaining business columns in the SAME order as arm.GasStorage in 001
--     (22 columns total).
--
-- The TVPs intentionally do NOT carry Id / DateCreated / ModifiedAtUtc: those are
-- populated by the target table's IDENTITY / DEFAULTs. There is no PK on either
-- type — the merge procs (003) de-dup the batch on the merge key before MERGE, so
-- a same-batch duplicate natural key cannot break the MERGE.
--
-- NULL-ability mirrors the target columns: all GasStorage measures are NULLable
-- (status 'E'/'N' rows may blank them) plus UpdatedAt; the rest are NOT NULL.
-- The entity TVP's FileLogId is NULLable (dimension provenance may be absent).
--
-- Run 001 first. This script assumes AGSI is the current database.
-- =============================================================================

USE AGSI;
GO

-- ----------------------------------------------------------------------------
-- arm.GasStorageEntityTvp — feeds arm.usp_BulkMergeGasStorageEntity.
-- Order: (Code, Name, ParentCode, ParentName, FileLogId).  FileLogId LAST
-- (matches the sink's BuildTable order — load-bearing).
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.GasStorageEntityTvp') IS NULL
BEGIN
    CREATE TYPE arm.GasStorageEntityTvp AS TABLE
    (
        Code       VARCHAR(16)  NOT NULL,
        [Name]     NVARCHAR(64) NOT NULL,
        ParentCode VARCHAR(16)  NOT NULL,
        ParentName NVARCHAR(64) NOT NULL,
        FileLogId  INT          NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.GasStorageTvp — feeds arm.usp_BulkMergeGasStorage.
-- Order: FileLogId FIRST, EntityId SECOND, then the remaining business columns in
-- the SAME order as the arm.GasStorage table (001) — 22 columns total:
--   (FileLogId, EntityId, Date, Gas_Day, UpdatedAt, GasDayStart, GasDayEnd,
--    GasInStorage, Consumption, ConsumptionFull, Injection, Withdrawal,
--    NetWithdrawal, WorkingGasVolume, InjectionCapacity, WithdrawalCapacity,
--    ContractedCapacity, AvailableCapacity, CoveredCapacity, Status, Trend, [Full]).
-- Name/Code/Url were dropped from arm.GasStorage (normalized to EntityId → the
-- entity dimension). Column widths equal the table widths in 001 so the TVP cannot
-- truncate.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.GasStorageTvp') IS NULL
BEGIN
    CREATE TYPE arm.GasStorageTvp AS TABLE
    (
        FileLogId          INT           NOT NULL,
        EntityId           INT           NOT NULL,
        [Date]             DATE          NOT NULL,
        Gas_Day            DATE          NOT NULL,
        UpdatedAt          DATETIME2(0)  NULL,
        GasDayStart        DATE          NOT NULL,
        GasDayEnd          DATE          NOT NULL,
        GasInStorage       DECIMAL(18,4) NULL,
        Consumption        DECIMAL(18,4) NULL,
        ConsumptionFull    DECIMAL(18,4) NULL,
        Injection          DECIMAL(18,4) NULL,
        Withdrawal         DECIMAL(18,4) NULL,
        NetWithdrawal      DECIMAL(18,4) NULL,
        WorkingGasVolume   DECIMAL(18,4) NULL,
        InjectionCapacity  DECIMAL(18,4) NULL,
        WithdrawalCapacity DECIMAL(18,4) NULL,
        ContractedCapacity DECIMAL(18,4) NULL,
        AvailableCapacity  DECIMAL(18,4) NULL,
        CoveredCapacity    DECIMAL(9,4)  NULL,
        [Status]           VARCHAR(4)    NOT NULL,
        Trend              DECIMAL(9,4)  NULL,
        [Full]             DECIMAL(9,4)  NULL
    );
END
GO
