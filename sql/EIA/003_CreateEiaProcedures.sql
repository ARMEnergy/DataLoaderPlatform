-- =============================================================================
-- 003_CreateEiaProcedures.sql
-- Database : EIAv2
-- Schema   : arm
--
--   -- catalog reads (the loader hard-codes NO SQL; it calls these) ------------
--   arm.usp_GetActiveSeries         the enabled, data-bearing series to load
--   arm.usp_GetSeriesFrequencies    one series' frequencies  (@SeriesId)
--   arm.usp_GetSeriesDataTypes      one series' data types   (@SeriesId)
--
--   -- writes ------------------------------------------------------------------
--   arm.usp_BulkMergeSeriesData     TVP merge into arm.SeriesData
--   arm.usp_UpdateSeriesEndPeriod   advance-only EndPeriod stamp
--
--   -- observation --------------------------------------------------------------
--   arm.usp_ValidateLoad            post-load anomaly report
--
-- Design of record: docs/design/EIA.md
--
-- All CREATE OR ALTER, so the script is re-runnable.
--
-- =============================================================================
-- ⚠ THE CATALOG-READ PROCS ARE THE CONTRACT
-- =============================================================================
-- The loader never embeds a SELECT against arm.Series / arm.SeriesFrequency /
-- arm.SeriesDataType. It calls the three procs below, which carry the queries
-- exactly as specified in the loader request. Changing WHICH series load is
-- therefore a change to THIS FILE (or to the data), never a rebuild.
--
-- ⚠ TWO FACTS ABOUT THE LIVE CATALOG THAT THESE PROCS DO NOT HIDE:
--
--   1. arm.SeriesFrequency.Enabled and arm.SeriesDataType.Enabled are NULL on
--      EVERY row (371 and 390 respectively, verified 2026-10-16). The requested
--      per-series queries do NOT filter on Enabled, and these procs do not add
--      such a filter -- adding one would load NOTHING. The column is selected
--      and returned so the loader can log it, but it is not a predicate. If the
--      column is ever populated, add the filter HERE.
--
--   2. arm.SeriesDataType's audit column is named [ModifedAtUtc] -- the typo is
--      in the live table. The loader request listed it as ConduitLastUpdate,
--      which is the name the INCUMBENT dbo.SeriesDataType uses; the arm copy
--      renamed it. These procs return the real arm column names, so they run.
-- =============================================================================

USE EIAv2;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_GetActiveSeries
--
-- The list of series to load. Query exactly as supplied in the loader request.
--
-- Returns 95 rows today (of 260). Note TotalRecordCount and AllowIncremental
-- are NULL on every row; the loader treats a NULL AllowIncremental as the
-- configured default (Loaders:EIA:DefaultAllowIncremental) rather than as 0,
-- because a literal 0 would mean "re-pull all ~90M rows every run".
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_GetActiveSeries
AS
BEGIN
    SET NOCOUNT ON;

    SELECT SeriesId,
           Name,
           Description,
           ParentSeriesId,
           StartPeriod,
           EndPeriod,
           DefaultDateFormat,
           DefaultFrequency,
           IsDataSeries,
           TotalRecordCount,
           Enabled,
           Checksum,
           ModifiedAtUtc,
           AllowIncremental
        FROM arm.Series
        WHERE Enabled = 1 AND IsDataSeries = 1
        ORDER BY SeriesId;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_GetSeriesFrequencies  (@SeriesId)
--
-- Query exactly as supplied. [Format] is the period mask for this frequency --
-- it is what the loader renders the start/end request parameters in, and what
-- the API echoes back as response.dateFormat. The six masks in use (8 frequencies share them):
--
--     YYYY                      annual
--     YYYY-"Q"Q                 quarterly
--     YYYY-MM                   monthly
--     YYYY-MM-DD                weekly, four-week-average, daily
--     YYYY-MM-DD"T"HH24         hourly
--     YYYY-MM-DD"T"HH24TZH      local-hourly   <-- see docs/apis/EIA.md S5
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_GetSeriesFrequencies
    @SeriesId VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT SeriesId,
           FrequencyId,
           Description,
           Query,
           Format,
           Enabled,
           Checksum,
           ModifedAtUtc          -- sic: the live column name carries the typo
        FROM arm.SeriesFrequency
        WHERE SeriesId = @SeriesId
        ORDER BY FrequencyId;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_GetSeriesDataTypes  (@SeriesId)
--
-- Query exactly as supplied, except that the audit column is returned under its
-- real arm name [ModifedAtUtc] rather than the dbo name ConduitLastUpdate the
-- request listed -- see the header note. The loader does not read it.
--
-- DataTypeId is what becomes data[0], data[1], ... in the request URL, and
-- (together with "<DataTypeId>-units" and the bare "units") is what separates a
-- DataJSON field from a FacetJSON field. See docs/design/EIA.md S5.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_GetSeriesDataTypes
    @SeriesId VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT SeriesId,
           DataTypeId,
           Description,
           Units,
           Alias,
           AggregationMethod,
           Enabled,
           Checksum,
           ModifedAtUtc          -- sic: the live column name carries the typo
        FROM arm.SeriesDataType
        WHERE SeriesId = @SeriesId
        ORDER BY DataTypeId;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeSeriesData  ->  arm.SeriesData
--
-- BATCH DE-DUP. ROW_NUMBER() OVER (PARTITION BY the four key columns), keep
-- row 1. This is not decoration: MERGE does not degrade gracefully -- one
-- duplicated source key aborts the whole batch with "attempted to update the
-- same row more than once". Two things can legitimately produce a duplicate
-- here: EIA's offset paging re-serving a row when the underlying order shifts
-- mid-window, and a genuine vendor repeat. The loader counts collisions before
-- they get here and logs the colliding facets; this guard stops either from
-- taking the run down.
--
-- NO DELETE-BY-ABSENCE. A batch is one window of one series, 5,000 rows at a
-- time. Deleting rows absent from it would wipe every other window.
--
-- CHANGE-GUARDED UPDATE:
--     WHEN MATCHED AND (DataHash differs OR FacetJSON text drifted)
-- so an unchanged row is NOT updated and its ModifiedAtUtc is NOT re-stamped.
-- The FacetJSON clause catches the (vanishingly unlikely) case of two different
-- facet objects colliding on the same 32-bit FacetKey: the key matched, so the
-- row IS matched, and without this clause the stored FacetJSON would silently
-- disagree with the key stored beside it.
--
-- *** CONSEQUENCE THE OPERATOR MUST KNOW: @@ROWCOUNT COUNTS CHANGED ROWS, NOT
-- ROWS SENT. *** Re-merging a settled window where nothing moved reports
-- RecordsProcessed = 0 while having verified every row. THAT IS A HEALTHY
-- SUCCESS, not an empty read -- the same convention OilX and EvolutionMarkets
-- use. A genuinely empty window is an HTTP 200 with total = 0 and never reaches
-- a merge at all (docs/apis/EIA.md S6).
--
-- ModifiedAtUtc is set EXPLICITLY with SYSUTCDATETIME(). The table DEFAULT is
-- sysdatetime() (local), which the supplied DDL specifies and which is left
-- alone; rows written by this loader hold true UTC regardless.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSeriesData
    @Records arm.SeriesDataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (
                   PARTITION BY [SeriesId], [FrequencyId], [Period], [FacetKey]
                   ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.SeriesData AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[SeriesId]    = s.[SeriesId]
       AND tgt.[FrequencyId] = s.[FrequencyId]
       AND tgt.[Period]      = s.[Period]
       AND tgt.[FacetKey]    = s.[FacetKey]

    WHEN MATCHED AND (    tgt.[DataHash] IS NULL
                      OR  s.[DataHash]   IS NULL
                      OR  tgt.[DataHash] <> s.[DataHash]
                      OR  ISNULL(tgt.[FacetJSON], '') <> ISNULL(s.[FacetJSON], '')
                     ) THEN UPDATE SET
        [FacetJSON]     = s.[FacetJSON],
        [DataJSON]      = s.[DataJSON],
        [DataHash]      = s.[DataHash],
        [ModifiedAtUtc] = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN INSERT
    (
        [SeriesId], [FrequencyId], [Period], [FacetKey],
        [FacetJSON], [DataJSON], [DataHash], [ModifiedAtUtc]
    )
    VALUES
    (
        s.[SeriesId], s.[FrequencyId], s.[Period], s.[FacetKey],
        s.[FacetJSON], s.[DataJSON], s.[DataHash], SYSUTCDATETIME()
    );

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpdateSeriesEndPeriod  (@SeriesId, @EndPeriod)
--
-- Stamps arm.Series.EndPeriod with the most recent period available for the
-- series, as reported by the API's own metadata route (/v2/<SeriesId>, no
-- /data). The value is in the series' DefaultDateFormat -- which is exactly
-- what the column already holds, verified against the live catalog:
--
--     crude-oil-imports               defaultFrequency monthly   EndPeriod 2026-07
--     electricity/rto/fuel-type-data  defaultFrequency hourly    EndPeriod 2026-10-10T06
--
-- ⚠ ADVANCE-ONLY. The update fires only when @EndPeriod sorts ABOVE the stored
-- value, which is a CHRONOLOGICAL comparison only because every mask a series
-- can default to is zero-padded and most-significant-first:
--
--     YYYY   YYYY-"Q"Q   YYYY-MM   YYYY-MM-DD   YYYY-MM-DD"T"HH24
--
-- Those four are the only DefaultDateFormat values in the live catalog, and all
-- are lexicographically ordered.
--
-- ⚠ THE SIXTH MASK IS NOT. local-hourly's YYYY-MM-DD"T"HH24TZH carries a
-- per-row UTC offset, so '2026-10-15T01-07' (08:00Z) sorts BELOW
-- '2026-10-15T02-04' (06:00Z) while being an hour LATER. That is harmless today
-- only because no series uses local-hourly as its DEFAULT frequency, so no
-- EndPeriod is ever written in that mask. arm.usp_ValidateLoad's
-- DefaultMaskNotSortable check exists to fail loudly if one ever does, rather
-- than letting this comparison start lying.
--
-- Advance-only matters because a partial run, a vendor hiccup or a forecast
-- series that briefly retracts its horizon must never walk EndPeriod backwards:
-- the column is read as "how far the data goes", and a regression would make
-- the next run re-request a range it already has.
--
-- StartPeriod is NEVER written. The request is explicit that it must not change.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpdateSeriesEndPeriod
    @SeriesId  VARCHAR(100),
    @EndPeriod VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;

    IF @EndPeriod IS NULL OR LTRIM(RTRIM(@EndPeriod)) = ''
    BEGIN
        SELECT 0 AS RowsUpdated;
        RETURN;
    END

    UPDATE arm.Series
       SET EndPeriod     = @EndPeriod,
           ModifiedAtUtc = SYSUTCDATETIME()
     WHERE SeriesId = @SeriesId
       AND (EndPeriod IS NULL OR EndPeriod < @EndPeriod);

    SELECT @@ROWCOUNT AS RowsUpdated;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad  (@SeriesId = NULL)
--
-- Post-load OBSERVATIONAL anomaly report. It never changes a run's outcome --
-- EiaLoadValidator logs what comes back and swallows its own failures. One
-- uniform result set: Check, TableName, Detail, RowCount.
--
--   @SeriesId  limit every check to one series. NULL = all enabled series.
--
-- ⚠ EVERY CHECK IS BOUNDED. arm.SeriesData is expected to reach ~90M rows (the
-- incumbent dbo.SeriesData is at 94,001,592), and the Criterion loader proved
-- that ONE unbounded check in a validator is silent and total: it expires at
-- SqlCommand's default timeout and takes every OTHER check in the proc down
-- with it while the run still reports success. So:
--   * every check joins or filters on SeriesId, which LEADS the clustered
--     primary key, so each is a seek-and-range rather than a scan;
--   * the two checks that must aggregate (RowCount, EndPeriodStale) are scoped
--     to one series at a time by the same key;
--   * nothing here scans the table unfiltered, and nothing orders it.
-- A deliberately expensive full-history sweep does not belong in this proc.
--
-- The checks, and why each exists:
--
--   SeriesLoadedNothing   an enabled data series with ZERO rows in arm.SeriesData.
--                         Either it has never run, or every window came back
--                         empty -- which for a live EIA series means the route
--                         moved and our catalog is stale. The API answers 404
--                         with an EMPTY BODY for a route that no longer exists
--                         (docs/apis/EIA.md S6), so catalog drift is the first
--                         thing to suspect.
--
--   FacetJsonNull         FacetJSON IS NULL. The loader never writes NULL -- a
--                         row with no facets gets the literal '{}' (FacetKey
--                         1947253782). A NULL means the row came from somewhere
--                         else, or from a loader version that predates the rule.
--
--   DataHashNull          DataHash IS NULL while DataJSON is not. The hash is
--                         the merge's change guard; a NULL one makes the row
--                         compare unequal forever and re-stamps ModifiedAtUtc on
--                         every run, quietly destroying the column's meaning.
--
--   UnknownFrequency      a FrequencyId in arm.SeriesData that arm.SeriesFrequency
--                         does not list for that series. Catalog drift, or a
--                         frequency that was loaded and later removed.
--
--   JsonAtVarcharLimit    FacetJSON or DataJSON exactly 8000 characters long --
--                         the VARCHAR(8000) ceiling. Not proof of truncation
--                         (an over-long value raises an error rather than
--                         truncating), but the only cheap canary for a series
--                         whose descriptions have grown toward the limit.
--
--   EndPeriodStale        arm.Series.EndPeriod is BELOW the newest period loaded
--                         for that series' default frequency. The stamp is
--                         advance-only, so this means the stamp step did not run
--                         (or the metadata route disagreed with the data route).
--
--   PeriodMaskUnknown     a frequency whose Format is not one of the six masks
--                         the loader can parse and step. An unknown mask means
--                         the loader cannot build that frequency's windows at
--                         all, and it refuses rather than guessing.
--
--   DefaultMaskNotSortable  a series whose DefaultDateFormat is the local-hourly
--                         mask YYYY-MM-DD"T"HH24TZH. String order is NOT
--                         chronological order in that mask (the per-row UTC
--                         offset varies), so the advance-only comparison in
--                         arm.usp_UpdateSeriesEndPeriod would start silently
--                         refusing legitimate advances. No series does this
--                         today; the check is here so the day one does is loud.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @SeriesId VARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @findings TABLE
    (
        [Check]     VARCHAR(50)   NOT NULL,
        [TableName] VARCHAR(128)  NOT NULL,
        [Detail]    VARCHAR(1000) NOT NULL,
        [RowCount]  BIGINT        NOT NULL
    );

    -- The series in scope. Always a small set (95 today), and every check below
    -- drives off it so nothing ever touches arm.SeriesData without a SeriesId.
    DECLARE @scope TABLE
    (
        SeriesId         VARCHAR(100) NOT NULL PRIMARY KEY,
        DefaultFrequency VARCHAR(50)  NULL,
        EndPeriod        VARCHAR(50)  NULL
    );

    INSERT INTO @scope (SeriesId, DefaultFrequency, EndPeriod)
    SELECT s.SeriesId, s.DefaultFrequency, s.EndPeriod
      FROM arm.Series s
     WHERE s.Enabled = 1
       AND s.IsDataSeries = 1
       AND (@SeriesId IS NULL OR s.SeriesId = @SeriesId);

    -- ---- SeriesLoadedNothing --------------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'SeriesLoadedNothing',
           'arm.SeriesData',
           'Enabled data series "' + sc.SeriesId + '" has no rows. Check for a 404 (route moved) or a never-run backfill.',
           0
      FROM @scope sc
     WHERE NOT EXISTS (SELECT 1 FROM arm.SeriesData d WHERE d.SeriesId = sc.SeriesId);

    -- ---- FacetJsonNull --------------------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'FacetJsonNull',
           'arm.SeriesData',
           'Series "' + sc.SeriesId + '" has rows with FacetJSON NULL; the loader always writes at least ''{}''.',
           x.n
      FROM @scope sc
     CROSS APPLY (SELECT COUNT_BIG(*) AS n
                    FROM arm.SeriesData d
                   WHERE d.SeriesId = sc.SeriesId
                     AND d.FacetJSON IS NULL) x
     WHERE x.n > 0;

    -- ---- DataHashNull ---------------------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'DataHashNull',
           'arm.SeriesData',
           'Series "' + sc.SeriesId + '" has rows with DataJSON present but DataHash NULL; the merge guard cannot work on them.',
           x.n
      FROM @scope sc
     CROSS APPLY (SELECT COUNT_BIG(*) AS n
                    FROM arm.SeriesData d
                   WHERE d.SeriesId = sc.SeriesId
                     AND d.DataJSON IS NOT NULL
                     AND d.DataHash IS NULL) x
     WHERE x.n > 0;

    -- ---- UnknownFrequency -----------------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'UnknownFrequency',
           'arm.SeriesData',
           'Series "' + sc.SeriesId + '" has rows under FrequencyId "' + x.FrequencyId + '", which arm.SeriesFrequency does not list for it.',
           x.n
      FROM @scope sc
     CROSS APPLY (SELECT d.FrequencyId, COUNT_BIG(*) AS n
                    FROM arm.SeriesData d
                   WHERE d.SeriesId = sc.SeriesId
                     AND NOT EXISTS (SELECT 1
                                       FROM arm.SeriesFrequency f
                                      WHERE f.SeriesId = d.SeriesId
                                        AND f.FrequencyId = d.FrequencyId)
                   GROUP BY d.FrequencyId) x
     WHERE x.n > 0;

    -- ---- JsonAtVarcharLimit ---------------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'JsonAtVarcharLimit',
           'arm.SeriesData',
           'Series "' + sc.SeriesId + '" has rows whose FacetJSON or DataJSON is exactly 8000 characters -- at the VARCHAR(8000) ceiling.',
           x.n
      FROM @scope sc
     CROSS APPLY (SELECT COUNT_BIG(*) AS n
                    FROM arm.SeriesData d
                   WHERE d.SeriesId = sc.SeriesId
                     AND (DATALENGTH(d.FacetJSON) = 8000 OR DATALENGTH(d.DataJSON) = 8000)) x
     WHERE x.n > 0;

    -- ---- EndPeriodStale -------------------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'EndPeriodStale',
           'arm.Series',
           'Series "' + sc.SeriesId + '" EndPeriod=' + ISNULL(sc.EndPeriod, '<null>')
             + ' but the newest loaded ' + ISNULL(sc.DefaultFrequency, '<null>') + ' period is ' + x.MaxPeriod + '.',
           1
      FROM @scope sc
     CROSS APPLY (SELECT MAX(d.Period) AS MaxPeriod
                    FROM arm.SeriesData d
                   WHERE d.SeriesId = sc.SeriesId
                     AND d.FrequencyId = sc.DefaultFrequency) x
     WHERE x.MaxPeriod IS NOT NULL
       AND (sc.EndPeriod IS NULL OR sc.EndPeriod < x.MaxPeriod);

    -- ---- PeriodMaskUnknown ----------------------------------------------------
    -- Catalog-only; does not touch arm.SeriesData at all.
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'PeriodMaskUnknown',
           'arm.SeriesFrequency',
           'Series "' + f.SeriesId + '" frequency "' + f.FrequencyId + '" has Format "' + ISNULL(f.Format, '<null>')
             + '", which is not one of the six masks the loader can parse and step.',
           1
      FROM arm.SeriesFrequency f
      JOIN @scope sc ON sc.SeriesId = f.SeriesId
     WHERE ISNULL(f.Format, '') NOT IN
           ('YYYY',
            'YYYY-"Q"Q',
            'YYYY-MM',
            'YYYY-MM-DD',
            'YYYY-MM-DD"T"HH24',
            'YYYY-MM-DD"T"HH24TZH');

    -- ---- DefaultMaskNotSortable -----------------------------------------------
    INSERT INTO @findings ([Check], [TableName], [Detail], [RowCount])
    SELECT 'DefaultMaskNotSortable',
           'arm.Series',
           'Series "' + s.SeriesId + '" has DefaultDateFormat "' + ISNULL(s.DefaultDateFormat, '<null>')
             + '". String order is not chronological order in that mask, so the advance-only EndPeriod comparison '
             + 'in arm.usp_UpdateSeriesEndPeriod is no longer reliable for it.',
           1
      FROM arm.Series s
      JOIN @scope sc ON sc.SeriesId = s.SeriesId
     WHERE s.DefaultDateFormat = 'YYYY-MM-DD"T"HH24TZH';

    SELECT [Check], [TableName], [Detail], [RowCount]
      FROM @findings
     ORDER BY [Check], [Detail];
END
GO

PRINT 'sql/EIA/003 complete: 6 procedures created (3 catalog reads, 2 writes, 1 validator).';
GO
