-- =============================================================================
-- 002_CreateOpisTvpTypes.sql
-- Database : OPIS
-- Schema   : arm
--
-- ONE table type feeds BOTH fact tables. arm.LPReport and arm.LPReportHistory
-- take an identical payload and differ only in grain, so a single TVP carries
-- the parsed file and arm.usp_BulkMergeLPReport (003) fans it into both in one
-- transaction — the two tables can never drift apart.
--
-- LOAD-BEARING: the TVP binds BY POSITION. Column NAME + ORDER + TYPE must match
-- OpisLpReportSqlSink.BuildTable in src/DataLoader.OPIS/Sinks.cs EXACTLY; a
-- silent reorder corrupts every loaded row without raising an error.
--
--   arm.LPReportTvp : (FileLogId, Mkt_Prod, [Date], Timing, Price,
--                      [Low], [High], [Avg], Country, [Unit], Freq,
--                      SourceFileDate)
--
--   * FileLogId FIRST (repo convention — provenance leads the fact TVPs).
--   * DateCreated / ModifiedAtUtc are DB-stamped defaults and are NOT columns
--     here, matching every other loader's TVP posture.
--   * Mkt_Prod arrives ALREADY TRIMMED — the source pads it with spaces and the
--     C# parser strips them, so the key never carries trailing blanks.
--   * [Low]/[High]/[Avg] are NULL-able: basket rows publish only Avg.
--   * SourceFileDate is the ordering guard used by the merge proc, not a key.
--
-- Guarded with IF TYPE_ID(...) IS NULL so the script is re-runnable. NOTE: a
-- table type cannot be ALTERed — to change a column, drop the procedures that
-- reference it, drop the type, and re-run 002 then 003 (999 does this in order).
-- =============================================================================

IF TYPE_ID('arm.LPReportTvp') IS NULL
BEGIN
    CREATE TYPE arm.LPReportTvp AS TABLE
    (
        FileLogId      INT           NOT NULL,
        Mkt_Prod       VARCHAR(50)   NOT NULL,
        [Date]         DATE          NOT NULL,
        Timing         VARCHAR(10)   NOT NULL,
        Price          VARCHAR(10)   NOT NULL,
        [Low]          DECIMAL(12,4) NULL,
        [High]         DECIMAL(12,4) NULL,
        [Avg]          DECIMAL(12,4) NULL,
        Country        VARCHAR(10)   NULL,
        [Unit]         VARCHAR(10)   NULL,
        Freq           VARCHAR(10)   NULL,
        SourceFileDate DATE          NOT NULL
    );
END
GO
