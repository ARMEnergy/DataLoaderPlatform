-- =============================================================================
-- 003_CreateNgiProcedures.sql
-- Loader:   NGI  (NGI Data Services - Bidweek natural-gas price survey)
-- Database: NGI            Schema: arm
-- Creates:  the 4 stored procedures the loader calls:
--   * arm.usp_UpsertFileLog             - per-request audit-hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMergeBidWeekLocation  - TVP bulk UPSERT into arm.BidWeekLocation.
--   * arm.usp_BulkMergeBidWeekData      - TVP bulk UPSERT into arm.BidWeekData.
--   * arm.usp_ValidateLoad              - post-load OBSERVATIONAL report (19 checks).
--
-- Design of record: docs/design/NGI.md (SS6 FileLog, SS8.3 TVP contract,
--                   SS9/SS9.1 validation catalogue, SS11 concurrency/idempotency,
--                   SS13 items 8/9/10).
-- Template:         sql/AGSI/003_CreateAgsiProcedures.sql.
-- Run 001 and 002 first. This script assumes NGI is the current database.
--
-- -----------------------------------------------------------------------------
-- PER-REQUEST CALL ORDER the procs imply
-- -----------------------------------------------------------------------------
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId. Called for EVERY outcome:
--      Success, NotAvailable (which includes the ~58-of-60 legitimate 404s) and
--      Failed.
--   2) When the read produced rows, the reader stamps that FileLogId onto every
--      row and calls the matching arm.usp_BulkMerge... proc with @Records.
--
-- -----------------------------------------------------------------------------
-- RULES BOTH MERGE PROCS FOLLOW
-- -----------------------------------------------------------------------------
--   * Parameter name is @Records - that is what the platform's SqlSinkBase passes
--     (NOT IIR's @Rows, which required a custom sink base NGI does not use).
--   * DE-DUP BEFORE MERGE. The source SELECT applies
--     ROW_NUMBER() OVER (PARTITION BY <merge key> ORDER BY FileLogId DESC) and
--     keeps rn = 1. A MERGE errors outright if the same target row is matched
--     twice, so this is not optional even where a duplicate "cannot happen".
--     "Last wins" is implemented as HIGHEST FileLogId WINS, i.e. the most recent
--     pull for that key: a TVP has no inherent row order, so an ORDER BY over a
--     meaningful column beats ORDER BY (SELECT NULL). (DESC also puts a NULL
--     FileLogId last, so a row carrying provenance is preferred over one without.)
--     Note both batches are structurally single-valued anyway: each response node
--     is a JSON OBJECT, and a JSON object cannot express a duplicate key - and the
--     C# sink de-dups in memory first. This is insurance, correctly placed.
--   * MERGE on the NATURAL KEY only (PointCode; (IssueDate, PointCode)) - NEVER on
--     FileLogId, which is provenance and is UPDATEd on match.
--   * WHEN MATCHED updates the payload AND FileLogId AND re-stamps ModifiedAtUtc;
--     WHEN NOT MATCHED BY TARGET inserts.
--   * UPSERT-ONLY. NEITHER proc has a WHEN NOT MATCHED BY SOURCE / DELETE branch.
--     A truncated or partial snapshot must never be able to wipe either table
--     (design SS5.2 step 7). Vanished keys are reported observationally by
--     arm.usp_ValidateLoad, never deleted.
--   * SELECT @@ROWCOUNT AS RecordsProcessed so the sink's
--     ProcedureReturnsRowCount = true can read the count.
--   * Merge semantics are plain LAST-WINS. Whether NGI ever REVISES a published
--     issue is unknown - the feed carries no revision/version/status field at all
--     (design SS12 item 1). If a prior print ever has to be preserved, the pattern
--     to reach for is OPIS's arm.LPReportHistory (a record-status code inside the
--     key). Deliberately NOT built now.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable.
-- =============================================================================

USE NGI;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog
-- Upserts ONE arm.FileLog hub row per request and RETURNS its FileLogId. Called
-- once per request for ALL outcomes (Success / NotAvailable / Failed), so the hub
-- records the 404s that dominate this monthly feed rather than losing them.
--
-- Endpoint and Status are passed BY NAME and resolved to their surrogate
-- arm.Endpoint / arm.Status .Id here, server-side, so the C# writer keeps a
-- name-string signature (the CWG/AGSI posture). A miss RAISERRORs: both are fixed
-- catalogs seeded in 001, so an unknown name is a bug and must not be allowed to
-- feed a NULL into a NOT NULL FK column.
--
-- *** NO @Region PARAMETER. *** AGSI's hub carries a region axis because its
-- REQUEST axis is the country. NGI has no region request axis - one request returns
-- every region - and an audit column named RegionId would sit confusingly beside
-- arm.BidWeekData.Region, a PAYLOAD column. Do not add it (design SS6).
--
-- *** NULL-EQUALITY IS LOAD-BEARING. *** @RepresentativeDate is the requested
-- ISSUE DATE for BidWeekData and NULL for the undated BidWeekLocations snapshot.
-- UQ_FileLog_Endpoint_RepDate (001) relies on SQL Server treating NULLs as EQUAL
-- inside a UNIQUE constraint, which collapses Locations to ONE stable hub row.
-- But NULL = NULL is UNKNOWN in a JOIN/ON predicate, so the MERGE below MUST match
-- the date with the explicit
--     (tgt.RepresentativeDate = src.RepresentativeDate
--      OR (tgt.RepresentativeDate IS NULL AND src.RepresentativeDate IS NULL))
-- form. Simplify that away and every run tries to INSERT a second undated
-- Locations row and violates the unique constraint.
--
-- RETURN CONTRACT: exactly one result set, one row, one column "FileLogId" (the C#
-- reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id, which
-- yields the Id for BOTH the inserted and the updated branch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @Endpoint           VARCHAR(40),
    @RepresentativeDate DATE          = NULL,
    @StatusLabel        VARCHAR(20),
    @HttpStatus         INT           = NULL,
    @RequestPath        NVARCHAR(400),
    @RowCount           INT           = 0
AS
BEGIN
    SET NOCOUNT ON;

    -- ---- Validate required scalars up front ----------------------------------
    IF @Endpoint IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @Endpoint, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- ---- Resolve the lookup surrogate Ids ------------------------------------
    DECLARE @EndpointId INT = (SELECT Id FROM arm.Endpoint WHERE [Name] = @Endpoint);
    IF @EndpointId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Endpoint ''%s'' (expected BidWeekLocations or BidWeekData).', 16, 1, @Endpoint);
        RETURN;
    END

    DECLARE @StatusId INT = (SELECT Id FROM arm.Status WHERE [Name] = @StatusLabel);
    IF @StatusId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Status ''%s'' (expected Success, NotAvailable or Failed).', 16, 1, @StatusLabel);
        RETURN;
    END

    -- ---- Upsert the hub row, capturing the resulting Id ----------------------
    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE arm.FileLog AS tgt
    USING (SELECT @EndpointId         AS EndpointId,
                  @RepresentativeDate AS RepresentativeDate) AS src
       ON  tgt.EndpointId = src.EndpointId
       AND (tgt.RepresentativeDate = src.RepresentativeDate
            OR (tgt.RepresentativeDate IS NULL AND src.RepresentativeDate IS NULL))
    WHEN MATCHED THEN UPDATE SET
        StatusId       = @StatusId,
        HttpStatus     = @HttpStatus,
        RequestPath    = @RequestPath,
        [RowCount]     = @RowCount,
        LastCheckedUtc = SYSUTCDATETIME(),
        ModifiedAtUtc  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointId, RepresentativeDate, StatusId, HttpStatus, [RowCount],
                RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.RepresentativeDate, @StatusId, @HttpStatus, @RowCount,
                @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeBidWeekLocation
-- Merge key: PointCode. Endpoint 2's full name<->code crosswalk snapshot.
-- TVP column order (3): FileLogId, PointCode, LocationName - see 002.
--
-- *** UPSERT-ONLY: NEVER DELETE A VANISHED POINT CODE. ***
-- There is deliberately no WHEN NOT MATCHED BY SOURCE branch. A retired code stays
-- in arm.BidWeekLocation, and a truncated/partial snapshot therefore cannot wipe
-- the crosswalk. Codes that disappear are reported by arm.usp_ValidateLoad's
-- LocationCodesMissingFromFact check - informationally, never as an error.
--
-- *** ModifiedAtUtc IS RE-STAMPED UNCONDITIONALLY ON MATCHED. ***
-- The MATCHED branch has NO "AND something changed" guard, on purpose: every code
-- present in the current snapshot gets a fresh ModifiedAtUtc every run, so the
-- column doubles as a LAST-SEEN marker. A delisted point simply stops being
-- refreshed and its ModifiedAtUtc freezes at the last run that saw it, which is
-- exactly the "when did this code disappear?" answer. THIS IS WHY THERE IS NO
-- SEPARATE LastSeenUtc COLUMN - do not add one, and do not add a change-detection
-- guard here, because that would break the marker.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeBidWeekLocation
    @Records arm.BidWeekLocationTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.BidWeekLocation AS tgt
    USING (
        -- De-dup on the merge key before the MERGE (last wins = highest FileLogId).
        SELECT FileLogId, PointCode, LocationName
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY PointCode
                                      ORDER BY FileLogId DESC) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PointCode = src.PointCode
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        LocationName  = src.LocationName,
        ModifiedAtUtc = SYSUTCDATETIME()          -- unconditional: doubles as last-seen
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, PointCode, LocationName, ModifiedAtUtc)
        VALUES (src.FileLogId, src.PointCode, src.LocationName, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeBidWeekData
-- Merge key: (IssueDate, PointCode) - the user-specified natural key, and the
-- exact grain of the response (`data` is a JSON object keyed by point code, so it
-- structurally holds one record per point per issue).
-- TVP column order (12): FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd,
-- Region, PricingPoint, [Low], [High], [Average], [Volume], Deals - see 002. The
-- SELECT / UPDATE / INSERT lists below mirror that order exactly; keep them in
-- lockstep with 001, 002 and the C# sink's BuildTable.
--
-- NEVER merge on FileLogId (provenance, UPDATEd on match). Both key columns are
-- NOT NULL, and the reader drops-and-counts any record whose key is unusable.
--
-- Upsert-only: no DELETE / WHEN NOT MATCHED BY SOURCE branch. The daily hot
-- re-pull of the whole 60-day window therefore upserts in place - safe to
-- over-schedule, no duplicates (design SS11).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeBidWeekData
    @Records arm.BidWeekDataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.BidWeekData AS tgt
    USING (
        -- De-dup on (IssueDate, PointCode) before the MERGE (last wins = highest
        -- FileLogId). Required: an un-deduped MERGE errors if one target row is
        -- matched by two source rows.
        SELECT FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd, Region,
               PricingPoint, [Low], [High], [Average], [Volume], Deals
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY IssueDate, PointCode
                                      ORDER BY FileLogId DESC) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.IssueDate = src.IssueDate
       AND tgt.PointCode = src.PointCode
    WHEN MATCHED THEN UPDATE SET
        FileLogId     = src.FileLogId,
        SurveyStart   = src.SurveyStart,
        SurveyEnd     = src.SurveyEnd,
        Region        = src.Region,
        PricingPoint  = src.PricingPoint,
        [Low]         = src.[Low],
        [High]        = src.[High],
        [Average]     = src.[Average],
        [Volume]      = src.[Volume],
        Deals         = src.Deals,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd, Region,
                PricingPoint, [Low], [High], [Average], [Volume], Deals, ModifiedAtUtc)
        VALUES (src.FileLogId, src.IssueDate, src.PointCode, src.SurveyStart, src.SurveyEnd,
                src.Region, src.PricingPoint, src.[Low], src.[High], src.[Average],
                src.[Volume], src.Deals, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad(@DateFrom, @DateTo)
-- Post-load anomaly report over an ISSUE-DATE window (arm.BidWeekData.IssueDate).
-- Returns ONE result set with the repo's uniform shape so the C# NgiLoadValidator
-- can log it generically:
--   CheckName      VARCHAR(48)   what was measured
--   Scope          NVARCHAR(200) the grouping key the count applies to
--   ExpectedCount  INT           the expected value where one exists, else NULL
--   ActualCount    BIGINT        the measured count
--   Detail         NVARCHAR(400) extra context
-- The validator treats a row with a NON-NULL ExpectedCount that differs from
-- ActualCount as an anomaly (LogWarning) and every other row as informational
-- (LogInformation).
--
-- *** OBSERVATIONAL ONLY. NO SIDE EFFECTS. IT MUST NEVER THROW AND NEVER
-- RAISERROR. *** The caller decides what is a hard failure. With ~58 of 60 work
-- units legitimately returning 404 on this MONTHLY feed, a legitimately sparse
-- window must not be able to fail an otherwise-good load. Invalid arguments are
-- therefore reported as an ArgumentsInvalid ROW (ExpectedCount 0 vs ActualCount 1,
-- so the validator logs a warning) instead of raising an error - deliberately
-- unlike AGSI's usp_ValidateLoad, which RAISERRORs on bad arguments.
--
-- Window: the caller passes NgiTime.ResolveWindow's From/To - the SAME call the
-- work-unit provider makes - so the validation window can never drift from the
-- load window.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @DateFrom DATE,
    @DateTo   DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- ---- Argument report (NOT an error - see the header) ---------------------
    IF @DateFrom IS NULL OR @DateTo IS NULL OR @DateFrom > @DateTo
    BEGIN
        SELECT CAST('ArgumentsInvalid' AS VARCHAR(48)) AS CheckName,
               CAST(CONCAT('DateFrom=', ISNULL(CONVERT(VARCHAR(10), @DateFrom, 23), '(null)'),
                           ' DateTo=',   ISNULL(CONVERT(VARCHAR(10), @DateTo,   23), '(null)'))
                    AS NVARCHAR(200)) AS Scope,
               CAST(0 AS INT)         AS ExpectedCount,
               CAST(1 AS BIGINT)      AS ActualCount,
               CAST('both dates are required and @DateFrom must be <= @DateTo; no checks were run'
                    AS NVARCHAR(400)) AS Detail;
        RETURN;
    END

    -- ---- Pre-computed scalars used by several checks -------------------------
    DECLARE @LocCount    INT  = (SELECT COUNT(1) FROM arm.BidWeekLocation);
    DECLARE @WindowDays  INT  = DATEDIFF(DAY, @DateFrom, @DateTo) + 1;
    DECLARE @LatestIssue DATE = (SELECT MAX(IssueDate) FROM arm.BidWeekData
                                 WHERE IssueDate BETWEEN @DateFrom AND @DateTo);

    -- Diagnostic SAMPLE strings (best-effort, at most 5 values each) for Detail.
    -- Built with the classic assignment-concatenation over a TOP-N ordered set -
    -- version-safe (no STRING_AGG dependency) and purely cosmetic: nothing branches
    -- on these values.
    DECLARE @SampleFactMissing NVARCHAR(200) = NULL;
    SELECT @SampleFactMissing = ISNULL(@SampleFactMissing + ',', '') + s.PointCode
    FROM (
        SELECT TOP (5) d.PointCode
        FROM (SELECT DISTINCT PointCode
              FROM arm.BidWeekData
              WHERE IssueDate BETWEEN @DateFrom AND @DateTo) AS d
        WHERE NOT EXISTS (SELECT 1 FROM arm.BidWeekLocation AS l WHERE l.PointCode = d.PointCode)
        ORDER BY d.PointCode
    ) AS s;

    DECLARE @SampleLocMissing NVARCHAR(200) = NULL;
    SELECT @SampleLocMissing = ISNULL(@SampleLocMissing + ',', '') + s.PointCode
    FROM (
        SELECT TOP (5) l.PointCode
        FROM arm.BidWeekLocation AS l
        WHERE @LatestIssue IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.BidWeekData AS d
                          WHERE d.IssueDate = @LatestIssue AND d.PointCode = l.PointCode)
        ORDER BY l.PointCode
    ) AS s;

    DECLARE @UsavgRegions NVARCHAR(200) = NULL;
    SELECT @UsavgRegions = ISNULL(@UsavgRegions + ',', '') + s.Region
    FROM (
        SELECT TOP (5) d.Region
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
          AND d.PointCode = 'USAVG'
          AND d.Region IS NOT NULL
        GROUP BY d.Region
        ORDER BY d.Region
    ) AS s;

    -- Regions in the window that are NOT among the 14 values observed on
    -- 2026-08-01. This is a SNAPSHOT, not an enum: report a new value, NEVER fail
    -- on it (which is also why there is no lookup table and no CHECK on Region).
    DECLARE @NewRegionSample NVARCHAR(200) = NULL;
    DECLARE @NewRegionCount  INT           = 0;

    SELECT @NewRegionCount = COUNT(1)
    FROM (
        SELECT DISTINCT d.Region
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
          AND d.Region IS NOT NULL
          AND d.Region NOT IN ('South Texas','East Texas','West Texas/SE New Mexico','Midwest',
                               'Midcontinent','North Louisiana/Arkansas','South Louisiana',
                               'Southeast','Appalachia','Northeast','Rocky Mountains',
                               'Arizona/Nevada','California','Canada')
    ) AS n;

    SELECT @NewRegionSample = ISNULL(@NewRegionSample + ',', '') + s.Region
    FROM (
        SELECT TOP (5) d.Region
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
          AND d.Region IS NOT NULL
          AND d.Region NOT IN ('South Texas','East Texas','West Texas/SE New Mexico','Midwest',
                               'Midcontinent','North Louisiana/Arkansas','South Louisiana',
                               'Southeast','Appalachia','Northeast','Rocky Mountains',
                               'Arizona/Nevada','California','Canada')
        GROUP BY d.Region
        ORDER BY d.Region
    ) AS s;

    DECLARE @SampleDupName NVARCHAR(200) = NULL;
    SELECT @SampleDupName = ISNULL(@SampleDupName + ',', '') + s.LocationName
    FROM (
        SELECT TOP (5) l.LocationName
        FROM arm.BidWeekLocation AS l
        WHERE l.LocationName IS NOT NULL
        GROUP BY l.LocationName
        HAVING COUNT(DISTINCT l.PointCode) > 1
        ORDER BY l.LocationName
    ) AS s;

    ;WITH Report AS
    (
        -- 1) Crosswalk size. INFORMATIONAL: 163 is a same-day snapshot, and NGI
        --    adding or retiring a point must never raise an anomaly.
        SELECT
            CAST('LocationRowCount' AS VARCHAR(48))   AS CheckName,
            CAST(NULL AS NVARCHAR(200))               AS Scope,
            CAST(NULL AS INT)                         AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)                  AS ActualCount,
            CAST('total rows in arm.BidWeekLocation; observed baseline 163 - a SNAPSHOT, not a contract'
                 AS NVARCHAR(400))                    AS Detail
        FROM arm.BidWeekLocation

        UNION ALL
        -- 2) Crosswalk rows with an unusable key or a missing name. Expect 0.
        --    PointCode cannot be NULL (it is the PK); LocationName is NULLable by
        --    design (tolerant parse), so a non-zero here means a parse degraded.
        SELECT
            CAST('LocationBlankKey' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN LEN(ISNULL(l.PointCode, '')) = 0
                                   OR LEN(ISNULL(l.LocationName, '')) = 0
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1),
                        '; PointCode is the PK so it cannot be NULL; a hit means LocationName degraded to NULL/blank')
                 AS NVARCHAR(400))
        FROM arm.BidWeekLocation AS l

        UNION ALL
        -- 3) Rows per published issue date in the window. INFORMATIONAL:
        --    ExpectedCount stays NULL ON PURPOSE - 163 is a same-day snapshot.
        SELECT
            CAST('IssueDateRowCount' AS VARCHAR(48)),
            CAST(CONCAT('IssueDate=', CONVERT(VARCHAR(10), d.IssueDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('observed baseline 163 rows per issue; ExpectedCount is NULL on purpose (a point added/retired by NGI is not an anomaly)'
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
        GROUP BY d.IssueDate

        UNION ALL
        -- 4) Issue dates whose row count falls OUTSIDE +/-20% of the crosswalk
        --    size. This flags a JUMP, not the level. When the crosswalk is empty
        --    (a perfectly valid BidWeekData-only run) the band is meaningless, so
        --    the row degrades to informational (ExpectedCount NULL) and cannot
        --    false-positive.
        SELECT
            CAST('IssueDateRowCountOutOfBand' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(CASE WHEN @LocCount = 0 THEN NULL ELSE 0 END AS INT),
            CAST(ISNULL(SUM(CASE WHEN @LocCount > 0
                                  AND (c.RowsForDate < (@LocCount * 80 / 100)
                                    OR c.RowsForDate > (@LocCount * 120 / 100))
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CASE WHEN @LocCount = 0
                      THEN 'location table empty - band check skipped'
                      ELSE CONCAT('locations=', @LocCount,
                                  '; +/-20% band=[', @LocCount * 80 / 100, '..', @LocCount * 120 / 100,
                                  ']; issueDatesInWindow=', COUNT(1))
                 END AS NVARCHAR(400))
        FROM (
            SELECT IssueDate, COUNT(1) AS RowsForDate
            FROM arm.BidWeekData
            WHERE IssueDate BETWEEN @DateFrom AND @DateTo
            GROUP BY IssueDate
        ) AS c

        UNION ALL
        -- 5) Price ordering Low <= Average <= High, checked ONLY where all three
        --    are non-NULL. 0 violations in the live sample.
        --    *** do NOT add a non-negative price check - negative gas prices are
        --    real *** (Waha has printed negative cash prices; the loader parses a
        --    leading minus on purpose). Low = High is also VALID on a thin point
        --    (a zero-width range), so this checks ordering only, never width.
        SELECT
            CAST('PriceRangeOrdering' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN d.[Low] IS NOT NULL AND d.[High] IS NOT NULL AND d.[Average] IS NOT NULL
                                  AND NOT (d.[Low] <= d.[Average] AND d.[Average] <= d.[High])
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1),
                        '; ordering only, and only where Low/High/Average are ALL non-NULL; NO non-negative check by design')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 6) Survey-window ordering SurveyStart <= SurveyEnd < IssueDate, checked
        --    only where all three are non-NULL. Observed start offsets were 5/7/10
        --    days, so a strict < on the issue date is safe. The offset and the
        --    window LENGTH both vary month to month - which is exactly why the
        --    loader READS these dates and never computes them.
        SELECT
            CAST('SurveyWindowOrdering' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN d.SurveyStart IS NOT NULL AND d.SurveyEnd IS NOT NULL
                                  AND NOT (d.SurveyStart <= d.SurveyEnd AND d.SurveyEnd < d.IssueDate)
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1), '; checked only where SurveyStart and SurveyEnd are both non-NULL')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 7) The four columns that were NEVER null across 163/163 live records are
        --    NULLable in the DDL so a tolerant parse can degrade instead of
        --    dropping a row (001 header). THIS is where that decision is policed:
        --    expect 0. A non-zero strongly suggests NGI renamed a field.
        SELECT
            CAST('UnexpectedNulls' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN d.SurveyStart IS NULL
                                   OR d.SurveyEnd IS NULL
                                   OR LEN(ISNULL(d.Region, '')) = 0
                                   OR LEN(ISNULL(d.PricingPoint, '')) = 0
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1),
                        '; SurveyStart/SurveyEnd/Region/PricingPoint are NULLable so a tolerant parse degrades instead of dropping;',
                        ' IssueDate+PointCode are NOT NULL via the PK, making six never-null-observed columns in total')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 8) Fact point codes absent from the crosswalk.
        --    *** INFORMATIONAL ONLY, NEVER AN ERROR. *** The two pipelines are
        --    INDEPENDENT: a new code can legitimately appear in a datafeed before
        --    the locations snapshot is refreshed, and the 163-code equality is a
        --    same-day observation, not a contract. This is precisely why there is
        --    NO FK between the tables. ExpectedCount stays NULL.
        SELECT
            CAST('FactCodesMissingFromLocation' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('INFORMATIONAL ONLY - never an error (independent pipelines, no FK). sample: ',
                        ISNULL(@SampleFactMissing, '(none)')) AS NVARCHAR(400))
        FROM (
            SELECT DISTINCT d.PointCode
            FROM arm.BidWeekData AS d
            WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
              AND NOT EXISTS (SELECT 1 FROM arm.BidWeekLocation AS l WHERE l.PointCode = d.PointCode)
        ) AS m

        UNION ALL
        -- 9) The inverse, scoped to the LATEST issue date in the window (scoping it
        --    to the whole window would just be noise on a monthly feed). Surfaces
        --    retired codes that the upsert-only merge deliberately never purges.
        --    *** INFORMATIONAL ONLY, NEVER AN ERROR. ***
        SELECT
            CAST('LocationCodesMissingFromFact' AS VARCHAR(48)),
            CAST(CASE WHEN @LatestIssue IS NULL
                      THEN 'IssueDate=(no published issue in window)'
                      ELSE CONCAT('IssueDate=', CONVERT(VARCHAR(10), @LatestIssue, 23))
                 END AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('INFORMATIONAL ONLY - never an error; retired codes are kept on purpose (upsert-only merge). sample: ',
                        ISNULL(@SampleLocMissing, '(none)')) AS NVARCHAR(400))
        FROM (
            SELECT l.PointCode
            FROM arm.BidWeekLocation AS l
            WHERE @LatestIssue IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM arm.BidWeekData AS d
                              WHERE d.IssueDate = @LatestIssue AND d.PointCode = l.PointCode)
        ) AS m

        UNION ALL
        -- 10) Calendar dates in the window with NO rows at all. Needs no tally
        --     table (DATEDIFF + COUNT DISTINCT).
        --     *** INFORMATIONAL ONLY, NEVER AN ERROR. *** Bidweek is a MONTHLY
        --     feed: over the default 60-day window roughly 58 of 60 dates
        --     legitimately have no publication (HTTP 404, which completes its work
        --     unit as a SUCCESSFUL empty read). ExpectedCount MUST stay NULL.
        SELECT
            CAST('IssueDatesWithZeroRows' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(@WindowDays - COUNT(DISTINCT d.IssueDate) AS BIGINT),
            CAST(CONCAT('windowDays=', @WindowDays, ' datesWithRows=', COUNT(DISTINCT d.IssueDate),
                        '; INFORMATIONAL ONLY - a MONTHLY feed leaves ~58 of 60 dates unpublished')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 11) Unpriced rows. Watch for a JUMP, not the level: ~28% unpriced is a
        --     NORMAL issue (Low/High/Average move together as a group).
        SELECT
            CAST('PriceNullRate' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN d.[Average] IS NULL THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1), ' pct=',
                        ISNULL(CAST(CAST(100.0 * SUM(CASE WHEN d.[Average] IS NULL THEN 1 ELSE 0 END)
                                         / NULLIF(COUNT(1), 0) AS DECIMAL(5,1)) AS VARCHAR(8)), '0.0'),
                        '; observed baseline 27.6% (45/163) - flag a JUMP, not the level')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 12) Rows with no reported activity. Baseline 28.8% (47/163).
        SELECT
            CAST('ActivityNullRate' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN d.[Volume] IS NULL THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1), ' pct=',
                        ISNULL(CAST(CAST(100.0 * SUM(CASE WHEN d.[Volume] IS NULL THEN 1 ELSE 0 END)
                                         / NULLIF(COUNT(1), 0) AS DECIMAL(5,1)) AS VARCHAR(8)), '0.0'),
                        '; observed baseline 28.8% (47/163) - flag a JUMP, not the level')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 13) QUIRK A (verified live): rows that ARE priced but carry NULL
        --     Volume/Deals. Exactly 2 of 163 on the 2026-08-01 issue - STXAGUAD
        --     (Agua Dulce) and CALSPGE (Southern Border, PG&E) - because the 45
        --     price-NULL rows are a STRICT SUBSET of the 47 activity-NULL rows.
        --     An assessed / rolled-up price with no reported deals is LEGITIMATE.
        --     *** do NOT set ExpectedCount = 0 here *** - and do not write a
        --     "price implies volume" rule anywhere: it would false-positive on
        --     every issue.
        SELECT
            CAST('PricedWithoutActivity' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN d.[Average] IS NOT NULL AND d.[Volume] IS NULL
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST('INFORMATIONAL ONLY - Quirk A: 2 of 163 observed (STXAGUAD, CALSPGE) are priced with NULL Volume/Deals; a price with no reported deals is legitimate'
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 14) QUIRK B (verified live): the NATIONAL aggregate USAVG ("National
        --     Avg.") is filed under Region = 'California'. That is a vendor data
        --     quirk, so a GROUP BY Region attributes the US national average to
        --     California.
        --     *** PERSIST AS PUBLISHED. NEVER CORRECT IT IN THE LOADER OR HERE.
        --     INFORMATIONAL ONLY, NEVER AN ERROR. ***
        SELECT
            CAST('UsavgRegionQuirk' AS VARCHAR(48)),
            CAST('PointCode=USAVG' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('INFORMATIONAL ONLY - never an error. Region(s) carried: ',
                        ISNULL(@UsavgRegions, '(none)'),
                        '; observed quirk: the national average is filed under California - persist as published')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
          AND d.PointCode = 'USAVG'

        UNION ALL
        -- 15) Distinct Region values in the window, and how many fall outside the
        --     14 values observed on 2026-08-01. REPORT a new region, NEVER fail on
        --     it: Region is not an enum in any vendor document (hence no lookup
        --     table and no CHECK constraint on the column).
        SELECT
            CAST('RegionValueSet' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('outsideThe14Observed=', @NewRegionCount,
                        ' sample=', ISNULL(@NewRegionSample, '(none)'),
                        '; a new region is reported, NEVER failed on')
                 AS NVARCHAR(400))
        FROM (
            SELECT DISTINCT d.Region
            FROM arm.BidWeekData AS d
            WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
              AND d.Region IS NOT NULL
        ) AS r

        UNION ALL
        -- 16) In-band AGGREGATE rows (regional averages *RAVG, the sub-aggregates
        --     SEREGAVG / APPREGAVG / CALSAVG, and the national USAVG). The feed
        --     carries NO flag distinguishing them from granular points, and they
        --     are persisted as ordinary rows on purpose. Informational.
        SELECT
            CAST('AggregateRowsPresent' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('INFORMATIONAL - aggregate rows are stored as ordinary rows; ANY downstream aggregation DOUBLE-COUNTS unless it excludes %RAVG, SEREGAVG, APPREGAVG, CALSAVG, USAVG'
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo
          AND (d.PointCode LIKE '%RAVG'
               OR d.PointCode IN ('SEREGAVG','APPREGAVG','CALSAVG','USAVG'))

        UNION ALL
        -- 17) Consistency of the fact's IssueDate against the issue date the hub
        --     recorded for the request that produced it. Asserts the
        --     record / meta / request agreement the reader falls back through.
        --     Expect 0. (Rows whose hub row is undated cannot arise here - only
        --     BidWeekLocations writes a NULL RepresentativeDate - but the filter is
        --     explicit so the check can never compare against NULL.)
        SELECT
            CAST('IssueDateMatchesRequest' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN f.RepresentativeDate IS NOT NULL
                                  AND d.IssueDate <> f.RepresentativeDate
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsJoinedToFileLog=', COUNT(1),
                        '; compares arm.BidWeekData.IssueDate with arm.FileLog.RepresentativeDate')
                 AS NVARCHAR(400))
        FROM arm.BidWeekData AS d
        INNER JOIN arm.FileLog AS f ON f.Id = d.FileLogId
        WHERE d.IssueDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 18) Hub outcome tally over the window, per status. This is the audit
        --     answer to "did those dates really not publish?" - an operator should
        --     expect to read something like NotAvailable=58, Success=2.
        --     NotAvailable being the MAJORITY is correct and expected.
        SELECT
            CAST('FileLogOutcomeSummary' AS VARCHAR(48)),
            CAST(CONCAT('Status=', s.[Name]) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('INFORMATIONAL - NotAvailable is the EXPECTED MAJORITY on a monthly feed; includes the undated BidWeekLocations hub row'
                 AS NVARCHAR(400))
        FROM arm.FileLog AS f
        INNER JOIN arm.Status AS s ON s.Id = f.StatusId
        WHERE f.RepresentativeDate BETWEEN @DateFrom AND @DateTo
           OR f.RepresentativeDate IS NULL
        GROUP BY s.[Name]

        UNION ALL
        -- 19) Location names attached to more than one point code. Structurally
        --     impossible WITHIN one snapshot (the name is the JSON object key) but
        --     reachable ACROSS snapshots after an NGI rename, because the merge is
        --     upsert-only and never deletes. Informational.
        SELECT
            CAST('DuplicateLocationName' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('INFORMATIONAL - reachable only across snapshots (upsert-only merge). sample: ',
                        ISNULL(@SampleDupName, '(none)')) AS NVARCHAR(400))
        FROM (
            SELECT l.LocationName
            FROM arm.BidWeekLocation AS l
            WHERE l.LocationName IS NOT NULL
            GROUP BY l.LocationName
            HAVING COUNT(DISTINCT l.PointCode) > 1
        ) AS dn
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName, Scope;
END
GO

-- =============================================================================
-- DELIBERATE ABSENCES - do not "add for parity with AGSI".
--   * NO usp_Get... reference-list read proc. NOTHING reads arm.BidWeekLocation
--     back at run time: BidWeekData work units come from the trailing DATE WINDOW
--     ALONE, so the crosswalk is a plain lookup snapshot, not a discovery tier
--     (design SS2, SS0.1, SS13 item 9). AGSI needs
--     usp_GetGasStorageEntities because its storage work units ARE the discovered
--     country list; NGI has no such dependency and no tier barrier.
--   * NO @Region parameter on usp_UpsertFileLog and no arm.Region table (design
--     SS6) - NGI has no region request axis, and the name would collide with the
--     Region PAYLOAD column on arm.BidWeekData.
--   * NO DELETE / WHEN NOT MATCHED BY SOURCE branch in either merge proc.
--   * NO history table. A revision would currently overwrite last-wins; the feed
--     carries no revision/version/status field to key on (design SS12 item 1).
-- =============================================================================
