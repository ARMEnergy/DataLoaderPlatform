-- =============================================================================
-- 002_CreateEoxTvpTypes.sql
-- Database : EOX
-- Schema   : arm
--
-- One table type per fact table. Each carries the whole row EXCEPT the DB-stamped
-- ModifiedAtUtc, in the same order as the table's own column list:
--
--   arm.CrudeOilTvp    (CurveDate, LocationCode, TimeKey, Line, Code, ContractTerm,
--                       ContractName, ContractBegin, ContractEnd, Location,
--                       Mid, Bid, Ask, FileName)
--   arm.NaturalGasTvp  (CurveDate, MarketCode, TimeKey, Line, Code, Region, Market,
--                       ContractName, ContractTerm, ContractBegin, ContractEnd,
--                       Mid, Bid, Ask, FP, FileName)
--   arm.NGLTvp         (CurveDate, LocationCode, TimeKey, Line, Code, ContractTerm,
--                       ContractName, ContractBegin, ContractEnd, Location,
--                       Mid, Bid, Ask, FileName)
--
-- LOAD-BEARING: a TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- the DataTable that EoxTableSink builds, which it builds by walking the feed's
-- EoxDescriptors column list. EoxTvpContractTests PARSES THIS FILE and asserts
-- name, order, SQL type and nullability against those descriptors, so editing one
-- side alone fails the build instead of silently writing every row one column out
-- of place.
--
-- FileName is LAST in every type and is NOT NULL here even though the fact tables
-- allow NULL. It is the merge's ordering guard (003: src.FileName >= tgt.FileName),
-- and a NULL would make the guard indeterminate, so the loader must always supply
-- it. Widening the table column to NOT NULL was left alone because the requester
-- specified it nullable.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a table
-- type cannot be ALTERed -- to change a column, drop the procedures that reference
-- it, drop the type, then re-run 002 and 003 (999 does this in the right order).
-- =============================================================================

IF TYPE_ID('arm.CrudeOilTvp') IS NULL
BEGIN
    CREATE TYPE arm.CrudeOilTvp AS TABLE
    (
        CurveDate     DATE         NOT NULL,
        LocationCode  VARCHAR(8)   NOT NULL,
        TimeKey       VARCHAR(16)  NOT NULL,
        Line          VARCHAR(64)  NULL,
        Code          VARCHAR(128) NULL,
        ContractTerm  VARCHAR(32)  NULL,
        ContractName  VARCHAR(64)  NULL,
        ContractBegin DATE         NULL,
        ContractEnd   DATE         NULL,
        Location      VARCHAR(128) NULL,
        Mid           FLOAT        NULL,
        Bid           FLOAT        NULL,
        Ask           FLOAT        NULL,
        FileName      VARCHAR(128) NOT NULL
    );
END
GO

IF TYPE_ID('arm.NaturalGasTvp') IS NULL
BEGIN
    CREATE TYPE arm.NaturalGasTvp AS TABLE
    (
        CurveDate     DATE         NOT NULL,
        MarketCode    VARCHAR(8)   NOT NULL,
        TimeKey       VARCHAR(16)  NOT NULL,
        Line          VARCHAR(64)  NULL,
        Code          VARCHAR(128) NULL,
        Region        VARCHAR(64)  NULL,
        Market        VARCHAR(128) NULL,
        ContractName  VARCHAR(64)  NULL,
        ContractTerm  VARCHAR(32)  NULL,
        ContractBegin DATE         NULL,
        ContractEnd   DATE         NULL,
        Mid           FLOAT        NULL,
        Bid           FLOAT        NULL,
        Ask           FLOAT        NULL,
        FP            FLOAT        NULL,
        FileName      VARCHAR(128) NOT NULL
    );
END
GO

IF TYPE_ID('arm.NGLTvp') IS NULL
BEGIN
    CREATE TYPE arm.NGLTvp AS TABLE
    (
        CurveDate     DATE         NOT NULL,
        LocationCode  VARCHAR(8)   NOT NULL,
        TimeKey       VARCHAR(16)  NOT NULL,
        Line          VARCHAR(64)  NULL,
        Code          VARCHAR(128) NULL,
        ContractTerm  VARCHAR(32)  NULL,
        ContractName  VARCHAR(64)  NULL,
        ContractBegin DATE         NULL,
        ContractEnd   DATE         NULL,
        Location      VARCHAR(128) NULL,
        Mid           FLOAT        NULL,
        Bid           FLOAT        NULL,
        Ask           FLOAT        NULL,
        FileName      VARCHAR(128) NOT NULL
    );
END
GO
