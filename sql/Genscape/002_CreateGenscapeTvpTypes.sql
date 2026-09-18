-- =============================================================================
-- 002_CreateGenscapeTvpTypes.sql
-- Database : Genscape
-- Schema   : arm
--
-- One table type per target table (2).
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the table's column list in src/DataLoader.Genscape/GenscapeDescriptors.cs
-- EXACTLY; a silent reorder corrupts every loaded row without raising an error.
--
-- That contract is enforced by the BUILD, not by review alone:
-- tests/DataLoader.Genscape.Tests/GenscapeTvpContractTests.cs parses THIS FILE,
-- pulls each CREATE TYPE body out, and asserts name + order + type against the
-- descriptors. Editing a type here without editing the descriptor (or the other
-- way round) fails the test suite.
--
-- Conventions:
--   * TVP column order = target table column order.
--   * ModifiedAtUtc is a DB-stamped default and is NEVER a TVP column.
--   * NOT NULL marks a column the reader treats as REQUIRED: a record missing
--     it is DROPPED rather than merged under a blank key. Every primary key
--     component is required.
--
-- Year and Week ARE TVP columns even though the loader derives them rather than
-- reading them from the API. They are primary-key components, so the server has
-- to receive them; deriving them in the proc instead would make the key depend
-- on the session's DATEFIRST. See 001 and 003.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed -- to change a column, drop the procedures that
-- reference it, drop the type, then re-run 002 and 003 (999 does this in order).
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.OilFundamentalsCrudeStorageWeeklyTvp
--   -> arm.OilFundamentals_CrudeStorage_Weekly
-- 8 columns: the table's 9 less ModifiedAtUtc.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.OilFundamentalsCrudeStorageWeeklyTvp') IS NULL
BEGIN
    CREATE TYPE arm.OilFundamentalsCrudeStorageWeeklyTvp AS TABLE
    (
        ReportDate          DATE        NOT NULL,
        [Year]              SMALLINT    NOT NULL,
        [Week]              TINYINT     NOT NULL,
        Region              VARCHAR(50) NOT NULL,
        Product             VARCHAR(50) NOT NULL,
        StorageFieldType    VARCHAR(50) NOT NULL,
        StorageAmount       FLOAT       NULL,
        CapacityUtilization FLOAT       NULL
    );
END
GO

-- ----------------------------------------------------------------------------
-- arm.OilFundamentalsCrudeTransportationWeeklyTvp
--   -> arm.OilFundamentals_CrudeTransportation_Weekly
-- 6 columns: the table's 7 less ModifiedAtUtc.
--
-- Column order follows the TABLE's declaration order (Region then Type), NOT
-- the primary key's order (Type then Region). A TVP binds by position against
-- the DataTable, and the DataTable is built from the same descriptor list.
-- ----------------------------------------------------------------------------
IF TYPE_ID('arm.OilFundamentalsCrudeTransportationWeeklyTvp') IS NULL
BEGIN
    CREATE TYPE arm.OilFundamentalsCrudeTransportationWeeklyTvp AS TABLE
    (
        ReportDate DATE        NOT NULL,
        [Year]     SMALLINT    NOT NULL,
        [Week]     TINYINT     NOT NULL,
        Region     VARCHAR(50) NOT NULL,
        [Type]     VARCHAR(50) NOT NULL,
        FlowBPD    FLOAT       NULL
    );
END
GO
