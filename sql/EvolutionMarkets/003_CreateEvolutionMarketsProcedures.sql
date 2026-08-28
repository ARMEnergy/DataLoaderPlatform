-- =============================================================================
-- 003_CreateEvolutionMarketsProcedures.sql
-- Loader:   EvolutionMarkets  (EVO DataPipeline API - market-data history)
-- Database: EvolutionMarkets      Schema: arm
-- Creates:  the 3 stored procedures the loader calls:
--   * arm.usp_UpsertFileLog        - per-request audit-hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMergeMarketData  - TVP bulk UPSERT into arm.MarketData.
--   * arm.usp_ValidateLoad         - post-load OBSERVATIONAL report (17 checks).
--
-- Design of record: docs/design/EvolutionMarkets.md (SS6 FileLog, SS8.3 TVP contract,
--                   SS8.4 Checksum, SS9 validation catalogue, SS11 concurrency/
--                   idempotency).
-- Template:         sql/NGI/003_CreateNgiProcedures.sql.
-- Run 001 and 002 first. This script assumes EvolutionMarkets is the current database.
--
-- -----------------------------------------------------------------------------
-- PER-REQUEST CALL ORDER the procs imply
-- -----------------------------------------------------------------------------
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId. Called for EVERY outcome:
--      Success, NotAvailable (the routine weekend/holiday/pre-retention empty read)
--      and Failed.
--   2) When the read produced rows, the reader stamps that FileLogId onto every row,
--      computes each row's Checksum, and calls arm.usp_BulkMergeMarketData with
--      @Records.
--   3) After all units, the module calls arm.usp_ValidateLoad(@DateFrom, @DateTo)
--      over the SAME window the work-unit provider used.
--
-- -----------------------------------------------------------------------------
-- RULES THE MERGE PROC FOLLOWS
-- -----------------------------------------------------------------------------
--   * Parameter name is @Records - that is what the platform's SqlSinkBase passes
--     (NOT IIR's @Rows, which required a custom sink base this loader does not use).
--   * DE-DUP BEFORE MERGE. The source SELECT applies
--     ROW_NUMBER() OVER (PARTITION BY MarketDataId ORDER BY FileLogId DESC) and keeps
--     rn = 1. A MERGE errors outright if the same target row is matched twice, so this
--     is not optional even where a duplicate "cannot happen". "Last wins" is
--     implemented as HIGHEST FileLogId WINS, i.e. the most recent pull for that key: a
--     TVP has no inherent row order, so an ORDER BY over a meaningful column beats
--     ORDER BY (SELECT NULL). (DESC also puts a NULL FileLogId last, so a row carrying
--     provenance is preferred over one without.)
--   * MERGE on the PRIMARY KEY MarketDataId only - NEVER on FileLogId, which is
--     provenance and is UPDATEd on match.
--   * UPSERT-ONLY. There is NO WHEN NOT MATCHED BY SOURCE / DELETE branch. A truncated
--     or partial read must never be able to wipe history - and it easily could: this
--     endpoint returns 200 with an EMPTY ARRAY for a non-publishing day, so a DELETE
--     branch would erase a whole date's prices every weekend. Vanished keys are
--     reported observationally by arm.usp_ValidateLoad, never deleted.
--   * SELECT @@ROWCOUNT AS RecordsProcessed so the sink's
--     ProcedureReturnsRowCount = true can read the count.
--
-- -----------------------------------------------------------------------------
-- *** THE CHECKSUM CHANGE-DETECTION GUARD - AND WHAT IT DOES TO @@ROWCOUNT ***
-- -----------------------------------------------------------------------------
-- The WHEN MATCHED branch is guarded:
--       WHEN MATCHED AND (tgt.[Checksum] <> src.[Checksum]
--                         OR tgt.[Checksum] IS NULL)   <- defensive; column is NOT NULL
-- so an unchanged row is NOT updated and its ModifiedAtUtc is NOT re-stamped. That is
-- the entire point of the Checksum column: the loader re-pulls the whole 30-day window
-- every run, and without the guard ModifiedAtUtc would degrade into "time of last run"
-- and answer nothing. With it, ModifiedAtUtc means "when this price last actually
-- changed" - which is the only way to see a vendor REVISION, since this feed carries
-- no revision/version/status field at all.
--
-- *** CONSEQUENCE THE OPERATOR MUST KNOW: @@ROWCOUNT COUNTS CHANGED ROWS, NOT ROWS
-- SENT. *** A re-run over a window where nothing moved reports
-- RecordsProcessed = 0 while having verified ~4,400 rows. THAT IS A HEALTHY SUCCESS,
-- not a silent failure. core.LoadLog will show 0 records for that unit. Do not "fix"
-- this by removing the guard; if a rows-sent count is ever needed, add a separate
-- OUTPUT-based counter rather than making every unchanged row dirty.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable.
-- =============================================================================

USE EvolutionMarkets;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog
-- Upserts ONE arm.FileLog hub row per business date and RETURNS its FileLogId. Called
-- once per work unit for ALL outcomes (Success / NotAvailable / Failed), so the hub
-- records the routine empty reads that dominate a sparse window rather than losing
-- them.
--
-- Endpoint and Status are passed BY NAME and resolved to their surrogate
-- arm.Endpoint / arm.Status .Id here, server-side, so the C# writer keeps a
-- name-string signature (the CWG/AGSI/NGI posture). A miss RAISERRORs: both are fixed
-- catalogs seeded in 001, so an unknown name is a bug and must not be allowed to feed
-- a NULL into a NOT NULL FK column.
--
-- *** @RepresentativeDate IS REQUIRED AND THE MERGE JOIN IS PLAIN EQUALITY. ***
-- Unlike AGSI/NGI there is no undated snapshot endpoint here: every work unit is
-- exactly one business date, and arm.FileLog.RepresentativeDate is NOT NULL (001).
-- So this proc deliberately does NOT carry their
--     (tgt.RepresentativeDate = src.RepresentativeDate
--      OR (tgt.RepresentativeDate IS NULL AND src.RepresentativeDate IS NULL))
-- predicate. That form exists in those loaders only to reconcile SQL Server's
-- NULL-as-equal behaviour in a UNIQUE constraint with NULL-as-unknown in a join
-- predicate. Adding it here would be dead code implying a NULL case that cannot occur.
--
-- RETURN CONTRACT: exactly one result set, one row, one column "FileLogId" (the C#
-- reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id, which
-- yields the Id for BOTH the inserted and the updated branch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @Endpoint           VARCHAR(40),
    @RepresentativeDate DATE,
    @StatusLabel        VARCHAR(20),
    @HttpStatus         INT            = NULL,
    @RequestPath        NVARCHAR(1000),
    @RowCount           INT            = 0,
    @DroppedRowCount    INT            = 0,
    @PageCount          INT            = 0,
    @ErrorMessage       NVARCHAR(400)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- ---- Validate required scalars up front ----------------------------------
    IF @Endpoint IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL OR @RepresentativeDate IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @Endpoint, @RepresentativeDate, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- ---- Resolve the lookup surrogate Ids ------------------------------------
    DECLARE @EndpointId INT = (SELECT Id FROM arm.Endpoint WHERE [Name] = @Endpoint);
    IF @EndpointId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Endpoint ''%s'' (expected MarketDataHistory).', 16, 1, @Endpoint);
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
       ON  tgt.EndpointId         = src.EndpointId
       AND tgt.RepresentativeDate = src.RepresentativeDate
    WHEN MATCHED THEN UPDATE SET
        StatusId        = @StatusId,
        HttpStatus      = @HttpStatus,
        RequestPath     = @RequestPath,
        [RowCount]      = @RowCount,
        DroppedRowCount = @DroppedRowCount,
        PageCount       = @PageCount,
        ErrorMessage    = @ErrorMessage,
        LastCheckedUtc  = SYSUTCDATETIME(),
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointId, RepresentativeDate, StatusId, HttpStatus, [RowCount],
                DroppedRowCount, PageCount, RequestPath, ErrorMessage,
                LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.RepresentativeDate, @StatusId, @HttpStatus, @RowCount,
                @DroppedRowCount, @PageCount, @RequestPath, @ErrorMessage,
                SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeMarketData
-- Merge key: MarketDataId - the vendor-supplied surrogate, verified unique AND stable
-- across re-pulls (see the arm.MarketData header in 001 for the evidence and for the
-- DuplicateCompositeKey guard that protects the assumption).
--
-- TVP column order (25): FileLogId, MarketDataId, Market, Term, Term2, Tenor,
-- InstrumentSourceName, InstrumentId, InstrumentName, PriceTS, BusinessDate,
-- PriceType, [Size], Depth, Price, Ask, AskSize, Bid, BidSize, Mid, MidSize,
-- [Change], PctRetDaily, Currency, [Checksum] - see 002. The SELECT / UPDATE / INSERT
-- lists below mirror that order EXACTLY; keep them in lockstep with 001, 002 and the
-- C# sink's BuildTable. The Ask/Bid/Mid + *Size columns are mutually type-compatible,
-- so a reorder here would invert prices with no error anywhere.
--
-- NEVER merge on FileLogId (provenance, UPDATEd on match). MarketDataId is NOT NULL,
-- and the reader drops-and-counts any record whose key is unusable.
--
-- Upsert-only: no DELETE / WHEN NOT MATCHED BY SOURCE branch - mandatory here, because
-- an empty 200 is a routine non-publishing day and a DELETE branch would wipe a date's
-- prices every weekend. The daily hot re-pull of the whole 30-day window therefore
-- upserts in place - safe to over-schedule, no duplicates (design SS11).
--
-- See the header block for why @@ROWCOUNT reports CHANGED rows, not rows sent.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeMarketData
    @Records arm.MarketDataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.MarketData AS tgt
    USING (
        -- De-dup on the merge key before the MERGE (last wins = highest FileLogId).
        -- Required: an un-deduped MERGE errors if one target row is matched twice.
        SELECT FileLogId, MarketDataId, Market, Term, Term2, Tenor, InstrumentSourceName,
               InstrumentId, InstrumentName, PriceTS, BusinessDate, PriceType,
               [Size], Depth, Price, Ask, AskSize, Bid, BidSize, Mid, MidSize,
               [Change], PctRetDaily, Currency, [Checksum]
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY MarketDataId
                                      ORDER BY FileLogId DESC) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.MarketDataId = src.MarketDataId

    -- CHANGE-DETECTION GUARD. An unchanged row is skipped entirely, so ModifiedAtUtc
    -- keeps meaning "when this price last changed" across the daily full-window
    -- re-pull. The IS NULL arm is defensive only (the column is NOT NULL in 001) and
    -- costs nothing. See the header for the @@ROWCOUNT consequence.
    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> src.[Checksum]) THEN UPDATE SET
        FileLogId            = src.FileLogId,
        Market               = src.Market,
        Term                 = src.Term,
        Term2                = src.Term2,
        Tenor                = src.Tenor,
        InstrumentSourceName = src.InstrumentSourceName,
        InstrumentId         = src.InstrumentId,
        InstrumentName       = src.InstrumentName,
        PriceTS              = src.PriceTS,
        BusinessDate         = src.BusinessDate,
        PriceType            = src.PriceType,
        [Size]               = src.[Size],
        Depth                = src.Depth,
        Price                = src.Price,
        Ask                  = src.Ask,
        AskSize              = src.AskSize,
        Bid                  = src.Bid,
        BidSize              = src.BidSize,
        Mid                  = src.Mid,
        MidSize              = src.MidSize,
        [Change]             = src.[Change],
        PctRetDaily          = src.PctRetDaily,
        Currency             = src.Currency,
        [Checksum]           = src.[Checksum],
        ModifiedAtUtc        = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FileLogId, MarketDataId, Market, Term, Term2, Tenor, InstrumentSourceName,
                InstrumentId, InstrumentName, PriceTS, BusinessDate, PriceType,
                [Size], Depth, Price, Ask, AskSize, Bid, BidSize, Mid, MidSize,
                [Change], PctRetDaily, Currency, [Checksum], ModifiedAtUtc)
        VALUES (src.FileLogId, src.MarketDataId, src.Market, src.Term, src.Term2, src.Tenor,
                src.InstrumentSourceName, src.InstrumentId, src.InstrumentName, src.PriceTS,
                src.BusinessDate, src.PriceType, src.[Size], src.Depth, src.Price, src.Ask,
                src.AskSize, src.Bid, src.BidSize, src.Mid, src.MidSize, src.[Change],
                src.PctRetDaily, src.Currency, src.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad(@DateFrom, @DateTo)
-- Post-load anomaly report over a BUSINESS-DATE window (arm.MarketData.BusinessDate).
-- Returns ONE result set with the repo's uniform shape so the C#
-- EvolutionMarketsLoadValidator can log it generically:
--   CheckName      VARCHAR(48)   what was measured
--   Scope          NVARCHAR(200) the grouping key the count applies to
--   ExpectedCount  INT           the expected value where one exists, else NULL
--   ActualCount    BIGINT        the measured count
--   Detail         NVARCHAR(400) extra context
-- The validator treats a row with a NON-NULL ExpectedCount that differs from
-- ActualCount as an anomaly (LogWarning) and every other row as informational
-- (LogInformation).
--
-- *** OBSERVATIONAL ONLY. NO SIDE EFFECTS. IT MUST NEVER THROW AND NEVER RAISERROR. ***
-- The caller decides what is a hard failure. A legitimately sparse window (weekends,
-- the July 4 cluster, the unexplained 2026-08-20 gap) must not be able to fail an
-- otherwise-good load. Invalid arguments are therefore reported as an ArgumentsInvalid
-- ROW (ExpectedCount 0 vs ActualCount 1, so the validator logs a warning) instead of
-- raising - deliberately unlike AGSI's usp_ValidateLoad, which RAISERRORs on bad
-- arguments.
--
-- Window: the caller passes EvoTime.ResolveWindow's From/To - the SAME call the
-- work-unit provider makes - so the validation window can never drift from the load
-- window.
--
-- *** CHECKS THAT ARE INFORMATIONAL ON PURPOSE - DO NOT "PROMOTE" THEM. ***
--   * AlwaysNullColumns: nine payload columns are ALWAYS NULL for the only
--     permissioned dataset. A NON-ZERO count here is GOOD NEWS (a new entitlement
--     started populating them), not a fault.
--   * DistinctPriceType / DistinctCurrency / DistinctMarket: single values observed
--     ('Indicative' / 'USD' / 'US Natural Gas Index'), but none is an enum in any
--     vendor document. A new value is news, never an error.
--   * RowsPerDate / ZeroRowDates / DatesNotPulled: the per-date row count is NOT
--     constant (205 on most dates, 287 on 2026-07-21..24) and non-publishing days are
--     routine.
--   * MidOutsideBidAsk: the vendor rounds Mid to 4 dp, so a rounded Mid can sit a hair
--     outside the bid/ask range legitimately.
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
    DECLARE @WindowDays INT = DATEDIFF(DAY, @DateFrom, @DateTo) + 1;

    -- Diagnostic SAMPLE strings (best-effort, at most 5 values each) for Detail.
    -- Built with the classic assignment-concatenation over a TOP-N ordered set -
    -- version-safe (no STRING_AGG dependency) and purely cosmetic: nothing branches on
    -- these values.
    DECLARE @SamplePriceType NVARCHAR(200) = NULL;
    SELECT @SamplePriceType = ISNULL(@SamplePriceType + ',', '') + s.PriceType
    FROM (
        SELECT TOP (5) d.PriceType
        FROM arm.MarketData AS d
        WHERE d.BusinessDate BETWEEN @DateFrom AND @DateTo
          AND d.PriceType IS NOT NULL
        GROUP BY d.PriceType
        ORDER BY d.PriceType
    ) AS s;

    DECLARE @SampleCurrency NVARCHAR(200) = NULL;
    SELECT @SampleCurrency = ISNULL(@SampleCurrency + ',', '') + s.Currency
    FROM (
        SELECT TOP (5) d.Currency
        FROM arm.MarketData AS d
        WHERE d.BusinessDate BETWEEN @DateFrom AND @DateTo
          AND d.Currency IS NOT NULL
        GROUP BY d.Currency
        ORDER BY d.Currency
    ) AS s;

    DECLARE @SampleMarket NVARCHAR(200) = NULL;
    SELECT @SampleMarket = ISNULL(@SampleMarket + ',', '') + s.Market
    FROM (
        SELECT TOP (5) d.Market
        FROM arm.MarketData AS d
        WHERE d.BusinessDate BETWEEN @DateFrom AND @DateTo
          AND d.Market IS NOT NULL
        GROUP BY d.Market
        ORDER BY d.Market
    ) AS s;

    -- Dates in the window that produced rows but where EVERY row has a NULL [Change].
    -- THIS IS THE PROJECTION-DRIFT TRIPWIRE: the vendor returns `change` correctly only
    -- when the full pinned 23-name `field` list is sent, and substitutes the `term`
    -- STRING otherwise - which the tolerant decimal parse turns into NULL. A whole date
    -- of NULL Change with rows present is therefore the exact signature of a trimmed
    -- projection. Expected 0.
    DECLARE @AllNullChangeDates INT = 0;
    SELECT @AllNullChangeDates = COUNT(1)
    FROM (
        SELECT d.BusinessDate
        FROM arm.MarketData AS d
        WHERE d.BusinessDate BETWEEN @DateFrom AND @DateTo
        GROUP BY d.BusinessDate
        HAVING COUNT([Change]) = 0        -- COUNT(col) ignores NULLs
    ) AS x;

    ;WITH Report AS
    (
        -- 1) Total rows in the window. INFORMATIONAL: the per-date count is not
        --    constant and the number of publishing dates varies.
        SELECT
            CAST('RowCount' AS VARCHAR(48))         AS CheckName,
            CAST(NULL AS NVARCHAR(200))             AS Scope,
            CAST(NULL AS INT)                       AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)                AS ActualCount,
            CAST('total arm.MarketData rows in the window; ~205 per published date (287 on some dates) - a SNAPSHOT, not a contract'
                 AS NVARCHAR(400))                  AS Detail
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 2) Distinct business dates carrying rows. INFORMATIONAL: over the live 60-day
        --    window only 38 of 60 calendar days published.
        SELECT
            CAST('DistinctBusinessDates' AS VARCHAR(48)),
            CAST(CONCAT('window=', @WindowDays, ' calendar day(s)') AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(DISTINCT BusinessDate) AS BIGINT),
            CAST('dates with at least one row; weekends/holidays legitimately publish nothing'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 3) Hub rows recording an empty read. INFORMATIONAL and IMPORTANT: this is how
        --    "the vendor published nothing" is distinguished from "we never asked".
        SELECT
            CAST('ZeroRowDates' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('arm.FileLog rows in the window with RowCount = 0 (weekend / holiday / pre-retention / not yet published) - ROUTINE, not a fault'
                 AS NVARCHAR(400))
        FROM arm.FileLog
        WHERE RepresentativeDate BETWEEN @DateFrom AND @DateTo
          AND [RowCount] = 0

        UNION ALL
        -- 4) Calendar dates in the window with NO hub row at all. Expect 0: the provider
        --    enumerates every calendar day, so a gap means a unit never ran (or the run
        --    was cancelled part-way).
        SELECT
            CAST('DatesNotPulled' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(@WindowDays - (SELECT COUNT(DISTINCT RepresentativeDate)
                                FROM arm.FileLog
                                WHERE RepresentativeDate BETWEEN @DateFrom AND @DateTo) AS BIGINT),
            CAST('calendar dates in the window with no arm.FileLog row - every day should be probed, so a gap means a work unit never ran'
                 AS NVARCHAR(400))

        UNION ALL
        -- 5) Failed hub rows. Expect 0.
        SELECT
            CAST('FailedRequests' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('arm.FileLog rows with Status = Failed in the window; see their ErrorMessage'
                 AS NVARCHAR(400))
        FROM arm.FileLog AS f
        JOIN arm.Status  AS s ON s.Id = f.StatusId
        WHERE f.RepresentativeDate BETWEEN @DateFrom AND @DateTo
          AND s.[Name] = 'Failed'

        UNION ALL
        -- 6) Records the reader dropped. Expect 0. Non-zero means an unkeyable record,
        --    a duplicate id within one read, a non-object array element, or a truncated
        --    string - all shape drift worth investigating.
        SELECT
            CAST('DroppedRecords' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(DroppedRowCount), 0) AS BIGINT),
            CAST('records the reader dropped (unkeyable / duplicate id / not a JSON object) across the window'
                 AS NVARCHAR(400))
        FROM arm.FileLog
        WHERE RepresentativeDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 7) *** THE KEY-STABILITY GUARD. *** Composite natural keys carrying more than
        --    one MarketDataId. Expect 0.
        --    The PK is a VENDOR-generated id, trusted because it is stable across
        --    re-pulls. If the vendor ever starts minting a fresh id per pull, this goes
        --    non-zero and the table is silently accumulating duplicate prices for the
        --    same instrument/term/tenor/date. That is the ONE failure mode the PK choice
        --    admits. DO NOT REMOVE THIS CHECK (see arm.MarketData header in 001).
        SELECT
            CAST('DuplicateCompositeKey' AS VARCHAR(48)),
            CAST('(InstrumentId, Term, Tenor, BusinessDate)' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('composite keys holding >1 MarketDataId - would mean the vendor stopped reusing its row id, so the PK no longer de-duplicates. Fallback: re-key on the composite and keep MarketDataId as a UNIQUE attribute'
                 AS NVARCHAR(400))
        FROM (
            SELECT d.InstrumentId, d.Term, d.Tenor, d.BusinessDate
            FROM arm.MarketData AS d
            WHERE d.BusinessDate BETWEEN @DateFrom AND @DateTo
            GROUP BY d.InstrumentId, d.Term, d.Tenor, d.BusinessDate
            HAVING COUNT(DISTINCT d.MarketDataId) > 1
        ) AS dup

        UNION ALL
        -- 8) Rows whose BusinessDate is NULL. Expect 0: it is the analytical key of the
        --    table and the value the request specified, so a NULL means the response
        --    omitted or renamed the `date` field.
        SELECT
            CAST('NullBusinessDate' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('rows with a NULL BusinessDate - the response omitted or renamed the JSON "date" field'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate IS NULL

        UNION ALL
        -- 9) *** THE PROJECTION-DRIFT TRIPWIRE. *** Expect 0. See the @AllNullChangeDates
        --    computation above for why a whole date of NULL [Change] means the `field`
        --    list was trimmed.
        SELECT
            CAST('AllNullChangeDates' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(@AllNullChangeDates AS BIGINT),
            CAST('dates with rows but NO non-NULL [Change] at all - the signature of a TRIMMED `field` projection: this vendor returns `change` correctly ONLY in the full pinned 23-name list and substitutes `term` otherwise'
                 AS NVARCHAR(400))

        UNION ALL
        -- 10) Bid strictly above Ask. Expect 0 - a crossed market is not a thing this
        --     index feed publishes, so it would indicate inverted columns (the TVP
        --     reorder hazard) or vendor drift.
        SELECT
            CAST('BidAboveAsk' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('rows where Bid > Ask (both non-NULL) - suspect INVERTED Ask/Bid TVP columns before suspecting the data'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo
          AND Bid IS NOT NULL AND Ask IS NOT NULL
          AND Bid > Ask

        UNION ALL
        -- 11) Mid outside [Bid, Ask]. INFORMATIONAL: the vendor rounds Mid to 4 dp, so a
        --     rounded value can sit a hair outside the range legitimately.
        SELECT
            CAST('MidOutsideBidAsk' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('rows where Mid is outside [Bid, Ask] - EXPECTED in small numbers: the vendor rounds Mid to 4 dp'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo
          AND Bid IS NOT NULL AND Ask IS NOT NULL AND Mid IS NOT NULL
          AND (Mid < Bid OR Mid > Ask)

        UNION ALL
        -- 12) PriceTS's date part disagreeing with BusinessDate. Expect 0: every observed
        --     priceTs is exactly midnight UTC on the business date. A non-zero count is
        --     the signature of a TIMEZONE bug - most likely the instant being parsed as
        --     local time instead of UTC, which on a US-Central host shifts it onto the
        --     previous day.
        SELECT
            CAST('PriceTsDateMismatch' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('rows where CAST(PriceTS AS DATE) <> BusinessDate - suspect a UTC/local parsing bug (EvoParse.Instant)'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo
          AND PriceTS IS NOT NULL AND BusinessDate IS NOT NULL
          AND CAST(PriceTS AS DATE) <> BusinessDate

        UNION ALL
        -- 13) Rows whose BusinessDate falls outside the requested window. Expect 0 - each
        --     request pins dateFrom = dateTo, so a stray date means the vendor ignored the
        --     range or the wrong unit wrote the row.
        SELECT
            CAST('BusinessDateOutsideWindow' AS VARCHAR(48)),
            CAST(CONCAT(CONVERT(VARCHAR(10), @DateFrom, 23), '..', CONVERT(VARCHAR(10), @DateTo, 23)) AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('rows written by this window whose BusinessDate lies outside it (joined via arm.FileLog) - each request pins dateFrom = dateTo'
                 AS NVARCHAR(400))
        FROM arm.MarketData AS d
        JOIN arm.FileLog    AS f ON f.Id = d.FileLogId
        WHERE f.RepresentativeDate BETWEEN @DateFrom AND @DateTo
          AND (d.BusinessDate IS NULL OR d.BusinessDate <> f.RepresentativeDate)

        UNION ALL
        -- 14) The ALWAYS-NULL column census. INFORMATIONAL, and a NON-ZERO count is GOOD
        --     NEWS: it means a newly permissioned dataset has started populating columns
        --     that EVOID/USNaturalGasIndex leaves empty. Do NOT promote this to an
        --     anomaly.
        SELECT
            CAST('PopulatedNullableExtras' AS VARCHAR(48)),
            CAST('Term2/InstrumentSourceName/Size/Depth/Price/AskSize/BidSize/MidSize/PctRetDaily' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('rows with ANY of the 9 normally-empty columns populated. 0 is expected for EVOID/USNaturalGasIndex; NON-ZERO IS GOOD NEWS (a new entitlement), never a fault'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo
          AND (Term2 IS NOT NULL OR InstrumentSourceName IS NOT NULL OR [Size] IS NOT NULL
               OR Depth IS NOT NULL OR Price IS NOT NULL OR AskSize IS NOT NULL
               OR BidSize IS NOT NULL OR MidSize IS NOT NULL OR PctRetDaily IS NOT NULL)

        UNION ALL
        -- 15) Distinct PriceType values. INFORMATIONAL: not an enum. Observed: 'Indicative'.
        SELECT
            CAST('DistinctPriceType' AS VARCHAR(48)),
            CAST(ISNULL(@SamplePriceType, '(none)') AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(DISTINCT PriceType) AS BIGINT),
            CAST('distinct PriceType values; observed baseline is the single value ''Indicative'' - NOT an enum, so a new value is news, never an error'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 16) Distinct Currency values. INFORMATIONAL: not an enum. Observed: 'USD'.
        SELECT
            CAST('DistinctCurrency' AS VARCHAR(48)),
            CAST(ISNULL(@SampleCurrency, '(none)') AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(DISTINCT Currency) AS BIGINT),
            CAST('distinct Currency values; observed baseline is the single value ''USD'' - NOT an enum'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo

        UNION ALL
        -- 17) Distinct Market / instrument counts. INFORMATIONAL: 1 market and 41
        --     instruments observed, both a snapshot of current entitlements.
        SELECT
            CAST('DistinctInstruments' AS VARCHAR(48)),
            CAST(CONCAT('markets: ', ISNULL(@SampleMarket, '(none)')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(DISTINCT InstrumentId) AS BIGINT),
            CAST('distinct InstrumentId in the window; observed baseline 41 for EVOID/USNaturalGasIndex - a SNAPSHOT of entitlements, not a contract'
                 AS NVARCHAR(400))
        FROM arm.MarketData
        WHERE BusinessDate BETWEEN @DateFrom AND @DateTo
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName;
END
GO
