-- =============================================================================
-- 003_CreateGenscapeProcedures.sql
-- Database : Genscape
-- Schema   : arm
--
--   arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly
--   arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly
--   arm.usp_ValidateLoad                 post-load observational anomaly report
--
-- Shared conventions across both merge procs:
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY <key>), keep row 1. It should never fire:
--   each work unit reads ONE request region over ONE window; the primary key
--   was verified unique within a response (0 duplicate key groups across all
--   four feed/region pulls on 2026-09-08), and when the reader BISECTS its
--   window on the API's undocumented 5,000-row cap the two halves are DISJOINT
--   by construction ([start..mid] and [mid+1..end]), so bisection cannot
--   reintroduce a key either. The guard stays because MERGE does not degrade
--   gracefully: a single duplicated source key aborts the whole batch with
--   "attempted to update the same row more than once", and a vendor that starts
--   emitting a repeated row would otherwise take the loader down rather than
--   merely repeat itself.
--
--   Note that the OVERLAP between the two request regions is a different thing
--   and needs no guard here: NorthAmerica and GulfCoast both return 'Louisiana
--   Gulf Coast' and 'West Texas', but they are separate work units and
--   therefore separate TVPs. They write the same key twice, in whichever order
--   the units finish, with values verified identical -- see 001.
--
--   NO DELETE-BY-ABSENCE. Neither merge has WHEN NOT MATCHED BY SOURCE THEN
--   DELETE. Each work unit carries one window of one region; deleting rows
--   absent from it would wipe every other window's and region's rows.
--
--   MATCHING ON ALL KEY COLUMNS. Year and Week are derived from ReportDate by
--   the loader, so matching on the full declared primary key is equivalent to
--   matching on the business key -- as long as the derivation never changes. If
--   it ever does, rows written under the old derivation will NOT be matched and
--   will remain alongside the new ones; that would need a one-off cleanup, and
--   arm.usp_ValidateLoad's OilFundWeekDrift and OilFundForkedWeek checks below
--   exist to make it visible rather than silent.
--
--   ModifiedAtUtc is set explicitly with SYSUTCDATETIME(), so rows written by
--   this loader hold true UTC even though the table DEFAULT is sysdatetime().
--
--   Each returns SELECT ... AS RecordsProcessed, which SqlSinkBase surfaces to
--   core.LoadLog.
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly
--   -> arm.OilFundamentals_CrudeStorage_Weekly
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly
    @Records arm.OilFundamentalsCrudeStorageWeeklyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY ReportDate, [Year], [Week], Region, Product, StorageFieldType
                   ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.OilFundamentals_CrudeStorage_Weekly AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.ReportDate       = s.ReportDate
       AND tgt.[Year]           = s.[Year]
       AND tgt.[Week]           = s.[Week]
       AND tgt.Region           = s.Region
       AND tgt.Product          = s.Product
       AND tgt.StorageFieldType = s.StorageFieldType
    WHEN MATCHED THEN UPDATE SET
        StorageAmount       = s.StorageAmount,
        CapacityUtilization = s.CapacityUtilization,
        ModifiedAtUtc       = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ReportDate, [Year], [Week], Region, Product, StorageFieldType,
                StorageAmount, CapacityUtilization, ModifiedAtUtc)
        VALUES (s.ReportDate, s.[Year], s.[Week], s.Region, s.Product, s.StorageFieldType,
                s.StorageAmount, s.CapacityUtilization, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly
--   -> arm.OilFundamentals_CrudeTransportation_Weekly
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly
    @Records arm.OilFundamentalsCrudeTransportationWeeklyTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY ReportDate, [Year], [Week], Region, [Type]
                   ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.OilFundamentals_CrudeTransportation_Weekly AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.ReportDate = s.ReportDate
       AND tgt.[Year]     = s.[Year]
       AND tgt.[Week]     = s.[Week]
       AND tgt.Region     = s.Region
       AND tgt.[Type]     = s.[Type]
    WHEN MATCHED THEN UPDATE SET
        FlowBPD       = s.FlowBPD,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ReportDate, [Year], [Week], Region, [Type], FlowBPD, ModifiedAtUtc)
        VALUES (s.ReportDate, s.[Year], s.[Week], s.Region, s.[Type], s.FlowBPD, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad -- post-load anomaly report.
--
-- OBSERVATIONAL ONLY. It never fails a run; the loader logs whatever comes back
-- and carries on. @AsOfDate scopes the staleness checks.
--
-- SET DATEFIRST 7 makes DATEPART(week, ...) deterministic for the duration of
-- this procedure. It is set HERE and nowhere else: the loader computes Week in
-- C# precisely so the stored value does not depend on the session, and this is
-- the check that proves the two agree.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @AsOfDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET DATEFIRST 7;

    IF @AsOfDate IS NULL SET @AsOfDate = CAST(SYSUTCDATETIME() AS DATE);

    DECLARE @Findings TABLE
    (
        Severity  VARCHAR(10)  NOT NULL,
        Check_    VARCHAR(60)  NOT NULL,
        Detail    VARCHAR(400) NOT NULL
    );

    -- An empty table means every work unit for that feed failed or was disabled.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyOilFundamentals', 'arm.OilFundamentals_CrudeStorage_Weekly is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.OilFundamentals_CrudeStorage_Weekly);

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyOilFundamentals', 'arm.OilFundamentals_CrudeTransportation_Weekly is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.OilFundamentals_CrudeTransportation_Weekly);

    -- Year/Week must be the calendar year and week-of-year of ReportDate. A
    -- non-zero count means the loader stopped deriving them and started
    -- trusting the API's own "week", which is a week-of-MONTH on the storage
    -- endpoint. This is the single most important check on these two tables.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'ERROR', 'OilFundWeekDrift',
           CONCAT(COUNT(*), ' arm.OilFundamentals_CrudeStorage_Weekly row(s) have Year/Week that are not ',
                  'YEAR(ReportDate)/DATEPART(week, ReportDate)')
    FROM arm.OilFundamentals_CrudeStorage_Weekly
    WHERE [Year] <> YEAR(ReportDate) OR [Week] <> DATEPART(week, ReportDate)
    HAVING COUNT(*) > 0;

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'ERROR', 'OilFundWeekDrift',
           CONCAT(COUNT(*), ' arm.OilFundamentals_CrudeTransportation_Weekly row(s) have Year/Week that are ',
                  'not YEAR(ReportDate)/DATEPART(week, ReportDate)')
    FROM arm.OilFundamentals_CrudeTransportation_Weekly
    WHERE [Year] <> YEAR(ReportDate) OR [Week] <> DATEPART(week, ReportDate)
    HAVING COUNT(*) > 0;

    -- Year and Week are in the primary key but are FUNCTIONS of ReportDate, so
    -- one business key must never carry two (Year, Week) pairs. If it does, the
    -- derivation changed at some point and the merge inserted beside the old
    -- rows instead of updating them -- the duplicates need a one-off cleanup.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'ERROR', 'OilFundForkedWeek',
           CONCAT(COUNT(*), ' arm.OilFundamentals_CrudeStorage_Weekly business key(s) carry more than one ',
                  '(Year, Week) pair -- the week derivation changed and left duplicate rows behind')
    FROM (SELECT ReportDate, Region, Product, StorageFieldType
          FROM arm.OilFundamentals_CrudeStorage_Weekly
          GROUP BY ReportDate, Region, Product, StorageFieldType
          HAVING COUNT(*) > 1) AS x
    HAVING COUNT(*) > 0;

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'ERROR', 'OilFundForkedWeek',
           CONCAT(COUNT(*), ' arm.OilFundamentals_CrudeTransportation_Weekly business key(s) carry more than ',
                  'one (Year, Week) pair -- the week derivation changed and left duplicate rows behind')
    FROM (SELECT ReportDate, Region, [Type]
          FROM arm.OilFundamentals_CrudeTransportation_Weekly
          GROUP BY ReportDate, Region, [Type]
          HAVING COUNT(*) > 1) AS x
    HAVING COUNT(*) > 0;

    -- The feeds publish weekly (report dates are Fridays). Nothing inside three
    -- weeks means the source stopped, the credentials lapsed, or the window
    -- shrank -- none of which the merge itself would report.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'StaleOilFundamentals',
           CONCAT('arm.OilFundamentals_CrudeStorage_Weekly has no ReportDate after ',
                  CONVERT(VARCHAR(10), DATEADD(day, -21, @AsOfDate), 23),
                  ' (latest is ', CONVERT(VARCHAR(10), (SELECT MAX(ReportDate) FROM arm.OilFundamentals_CrudeStorage_Weekly), 23), ')')
    WHERE EXISTS (SELECT 1 FROM arm.OilFundamentals_CrudeStorage_Weekly)
      AND NOT EXISTS (SELECT 1 FROM arm.OilFundamentals_CrudeStorage_Weekly
                      WHERE ReportDate > DATEADD(day, -21, @AsOfDate));

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'StaleOilFundamentals',
           CONCAT('arm.OilFundamentals_CrudeTransportation_Weekly has no ReportDate after ',
                  CONVERT(VARCHAR(10), DATEADD(day, -21, @AsOfDate), 23),
                  ' (latest is ', CONVERT(VARCHAR(10), (SELECT MAX(ReportDate) FROM arm.OilFundamentals_CrudeTransportation_Weekly), 23), ')')
    WHERE EXISTS (SELECT 1 FROM arm.OilFundamentals_CrudeTransportation_Weekly)
      AND NOT EXISTS (SELECT 1 FROM arm.OilFundamentals_CrudeTransportation_Weekly
                      WHERE ReportDate > DATEADD(day, -21, @AsOfDate));

    -- The two request regions overlap, so a region present in one table's recent
    -- window and absent from the other's is not itself an error -- but a report
    -- date present in one feed and missing from the other usually means one of
    -- the two pipelines failed while the run still reported success.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'INFO', 'FeedDateGap',
           CONCAT(COUNT(*), ' report date(s) in the last 90 days appear in one oil-fundamentals table but ',
                  'not the other')
    FROM
    (
        SELECT ReportDate FROM arm.OilFundamentals_CrudeStorage_Weekly
        WHERE ReportDate > DATEADD(day, -90, @AsOfDate)
        EXCEPT
        SELECT ReportDate FROM arm.OilFundamentals_CrudeTransportation_Weekly
        WHERE ReportDate > DATEADD(day, -90, @AsOfDate)

        UNION

        SELECT ReportDate FROM arm.OilFundamentals_CrudeTransportation_Weekly
        WHERE ReportDate > DATEADD(day, -90, @AsOfDate)
        EXCEPT
        SELECT ReportDate FROM arm.OilFundamentals_CrudeStorage_Weekly
        WHERE ReportDate > DATEADD(day, -90, @AsOfDate)
    ) AS x
    HAVING COUNT(*) > 0;

    SELECT Severity, Check_ AS [Check], Detail FROM @Findings ORDER BY Severity, Check_;
END
GO
