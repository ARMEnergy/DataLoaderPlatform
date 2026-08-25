-- =============================================================================
-- 003_CreateModernCommoditiesProcedures.sql
-- Loader:   ModernCommodities  (vendor "ModCom")
-- Database: ModernCommodities        Schema: arm
-- Creates:  the 5 stored procedures the loader calls:
--   * arm.usp_UpsertFileLog          - per-PULL audit-hub upsert; RETURNS FileLogId.
--   * arm.usp_BulkMergeAllTrades     - TVP bulk UPSERT into arm.AllTrades.
--   * arm.usp_BulkMergeMyTrades      - TVP bulk UPSERT into arm.MyTrades.
--   * arm.usp_BulkMergeSettlements   - TVP bulk UPSERT into arm.Settlements.
--   * arm.usp_ValidateLoad           - post-load OBSERVATIONAL report (24 checks).
--
-- Design of record: docs/design/ModernCommodities.md
--                   (SS6 FileLog, SS8.3 TVP contract, SS9/SS9.1 validation
--                    catalogue, SS11 merge semantics, SS13 items 4/8/9/10)
-- Template:         sql/NGI/003_CreateNgiProcedures.sql (structure) and
--                   sql/OPIS/003_CreateOpisProcedures.sql (the order-independent
--                   WHEN MATCHED recency guard).
-- Run 001 and 002 first. This script assumes ModernCommodities is the current DB.
--
-- -----------------------------------------------------------------------------
-- PER-REQUEST CALL ORDER the procs imply
-- -----------------------------------------------------------------------------
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId. Called for EVERY outcome:
--      Success, NotAvailable (a header-only HTTP 200 = a LEGITIMATE EMPTY READ)
--      and Failed. Written by the READER, BEFORE the sink runs - which is why
--      [RowCount] is rows PARSED, not rows merged.
--   2) When the read produced rows, the sink calls the endpoint's
--      arm.usp_BulkMerge... proc with @Records.
--   *** Nothing is stamped onto the fact rows in between: the three fact tables
--   have NO FileLogId (decision D1). The returned FileLogId is logged, not
--   persisted. ***
--
-- -----------------------------------------------------------------------------
-- RULES ALL THREE MERGE PROCS FOLLOW
-- -----------------------------------------------------------------------------
--   * Parameter name is @Records - that is what the platform's SqlSinkBase passes
--     (NOT IIR's @Rows, which required a custom sink base this loader does not
--     use). No proc takes any other parameter: nothing about the request window is
--     persisted on a fact row.
--   * DE-DUP BEFORE MERGE. The source SELECT applies ROW_NUMBER() OVER
--     (PARTITION BY <merge key> ORDER BY ...) and keeps rn = 1. A MERGE errors
--     outright if the same target row is matched twice, so this is not optional
--     even where a duplicate "cannot happen".
--   * MERGE on the NATURAL KEY only - TradeNumber, or the settlements 6-column PK.
--   * WHEN MATCHED updates the payload and re-stamps ModifiedAtUtc;
--     WHEN NOT MATCHED BY TARGET inserts. ModifiedAtUtc = SYSUTCDATETIME() on
--     BOTH branches (its table DEFAULT is therefore unreachable - decision D3).
--   * -- upsert-only: NEVER add a DELETE / WHEN NOT MATCHED BY SOURCE branch.
--     A truncated, empty or header-only response must never be able to wipe a
--     table. The three windows are partial views by construction.
--   * -- no checksum short-circuit: the Checksum column was removed from all three
--     tables by explicit user decision (U2), so WHEN MATCHED is a straight update.
--   * SELECT @@ROWCOUNT AS RecordsProcessed so the sink's
--     ProcedureReturnsRowCount = true can read the count.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable.
-- =============================================================================

USE ModernCommodities;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog
-- Upserts ONE arm.FileLog hub row per PULL and RETURNS its FileLogId.
--
-- *** THIS IS THE LOADER'S ONLY PROVENANCE. *** No fact table carries a
-- FileLogId (decision D1), so if an outcome is not recorded here it is not
-- recorded anywhere. Called for ALL outcomes - Success, NotAvailable (header-only
-- 200) and Failed - and the non-2xx BODY is stored verbatim in @ErrorMessage,
-- because the four distinct 400s this API returns (row cap / history cap /
-- Invalid <param> / Invalid legalEntityName) are distinguishable ONLY by body
-- text and each has a different fix.
--
-- GRAIN: one row per pull, natural key
--        (EndpointId, WindowStart, WindowEnd, RunToken)          [decision D7]
--   NOT an upsert over a stable per-window key: collapsing pulls would erase the
--   fact that the 03:00 pull returned 0 rows after the 02:00 pull returned 20.
--   It is still a MERGE rather than a blind INSERT because a FAILED unit is
--   retried by the next run inside the SAME hour (core.LoadLog only skips
--   successes) with the SAME four key values, and that retry's outcome should
--   REPLACE the failure in place. Distinct hours/windows never collapse.
--
-- *** ALL FOUR KEY COLUMNS ARE NOT NULL, so the ON clause is plain equality. ***
-- Do NOT copy NGI's
--   (tgt.X = src.X OR (tgt.X IS NULL AND src.X IS NULL))
-- NULL-equality branch here: NGI needs it for its undated Locations hub row, and
-- in this loader it would be dead code that only obscures the key.
--
-- Endpoint and Status are passed BY NAME and resolved to their surrogate
-- arm.Endpoint / arm.Status .Id here, server-side, so the C# writer keeps a
-- name-string signature (the CWG/AGSI/NGI posture). A miss RAISERRORs: both are
-- fixed catalogs seeded in 001, so an unknown name is a bug and must not feed a
-- NULL into a NOT NULL FK column.
--
-- RETURN CONTRACT: exactly one result set, one row, one column "FileLogId" (the C#
-- reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id, which
-- yields the Id for BOTH the inserted and the updated branch.
--
-- NO CREDENTIAL EVER REACHES THIS PROC. Auth is HTTP Basic in a request HEADER,
-- so @RequestPath (path + startDate/endDate [+ legalEntityName]) is safe; it is
-- kept sanitised on principle regardless.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertFileLog
    @Endpoint        VARCHAR(40),
    @WindowStart     DATE,
    @WindowEnd       DATE,
    @RunToken        VARCHAR(40),
    @StatusLabel     VARCHAR(20),
    @HttpStatus      INT           = NULL,
    @RowCount        INT           = 0,
    @DroppedRowCount INT           = 0,
    @ScopeLabel      VARCHAR(100)  = NULL,
    @RequestPath     NVARCHAR(400),
    @ErrorMessage    NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- ---- Validate required scalars up front ----------------------------------
    IF @Endpoint IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL
       OR @WindowStart IS NULL OR @WindowEnd IS NULL OR @RunToken IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @Endpoint, @WindowStart, @WindowEnd, @RunToken, @StatusLabel and @RequestPath are all required.', 16, 1);
        RETURN;
    END

    -- ---- Resolve the lookup surrogate Ids ------------------------------------
    DECLARE @EndpointId INT = (SELECT Id FROM arm.Endpoint WHERE [Name] = @Endpoint);
    IF @EndpointId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Endpoint ''%s'' (expected AllTrades, MyTrades or Settlements).', 16, 1, @Endpoint);
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
    USING (SELECT @EndpointId  AS EndpointId,
                  @WindowStart AS WindowStart,
                  @WindowEnd   AS WindowEnd,
                  @RunToken    AS RunToken) AS src
       ON  tgt.EndpointId  = src.EndpointId
       AND tgt.WindowStart = src.WindowStart
       AND tgt.WindowEnd   = src.WindowEnd
       AND tgt.RunToken    = src.RunToken
    WHEN MATCHED THEN UPDATE SET
        StatusId        = @StatusId,
        HttpStatus      = @HttpStatus,
        [RowCount]      = @RowCount,
        DroppedRowCount = @DroppedRowCount,
        ScopeLabel      = @ScopeLabel,
        RequestPath     = @RequestPath,
        ErrorMessage    = @ErrorMessage,
        LastCheckedUtc  = SYSUTCDATETIME(),
        ModifiedAtUtc   = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (EndpointId, WindowStart, WindowEnd, RunToken, StatusId, HttpStatus,
                [RowCount], DroppedRowCount, ScopeLabel, RequestPath, ErrorMessage,
                LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.WindowStart, src.WindowEnd, src.RunToken, @StatusId, @HttpStatus,
                @RowCount, @DroppedRowCount, @ScopeLabel, @RequestPath, @ErrorMessage,
                SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeAllTrades
-- Merge key: TradeNumber (the whole PK). TVP column order (34): see 002 - the
-- SELECT / UPDATE / INSERT lists below mirror it EXACTLY, and must stay in
-- lockstep with 001, 002 and the C# sink's BuildTable.
--
-- =========================================================================
-- WHY  ORDER BY LastUpdated DESC  IN THE DEDUP  (not ORDER BY (SELECT NULL))
-- =========================================================================
-- A revision arrives as the SAME TradeNumber with a LATER Last Updated Timestamp,
-- so "last wins" here must mean NEWEST REVISION WINS, not "whichever row the TVP
-- happened to yield last". A TVP has no inherent row order, so ORDER BY
-- (SELECT NULL) would make the winner ARBITRARY and could keep a STALE copy of a
-- revised trade. T-SQL sorts NULL LOWEST, so DESC also places a
-- NULL-timestamped duplicate LAST - a dated copy always beats an undated one,
-- which is exactly the precedence we want. The C# sink de-dups the batch with the
-- same rule before building the DataTable, so the two can never disagree.
--
-- =========================================================================
-- WHY THE  WHEN MATCHED  RECENCY GUARD EXISTS  (decision D14, superseding D4)
-- =========================================================================
-- This is the OPIS  src.SourceFileDate >= tgt.SourceFileDate  pattern
-- (sql/OPIS/003_CreateOpisProcedures.sql), and here is the concrete race it
-- closes. Chunked windows partition by LAST-UPDATED, so a trade normally appears
-- in exactly one chunk - BUT A TRADE REVISED *BETWEEN* TWO CHUNK REQUESTS APPEARS
-- IN BOTH. Chunk A [Jul 1..Jul 30] is requested at 10:00:00 and returns trade
-- 67183 with LastUpdated = Jul 29; the venue revises it at 10:00:30; chunk B
-- [Jul 31..Aug 24] is requested at 10:01:00 and returns the SAME TradeNumber with
-- LastUpdated = Aug 24. The two chunks are independent work units that may merge
-- in EITHER order. Without the guard, whichever lands last wins and the loader can
-- persist the PRE-REVISION copy - silently, on a clean run. With it, the outcome
-- is INDEPENDENT OF ARRIVAL ORDER AND OF RE-RUN COUNT, which is the whole point.
-- The same applies to the hourly re-pull and to any concurrent replay.
--
-- The guard is
--     tgt.LastUpdated IS NULL
--     OR (src.LastUpdated IS NOT NULL AND src.LastUpdated >= tgt.LastUpdated)
-- i.e. *** an UNPARSEABLE-timestamp source row (src.LastUpdated IS NULL) may
-- INSERT but can NEVER CLOBBER a target row we know is newer. *** "Unknown
-- recency" must not beat "known newer" - that was the single branch of the
-- original D4 predicate that could still regress a revision, and D14 closes it.
-- Cost is nil: Last Updated Timestamp was populated in 135/135 observed rows, so
-- a NULL here means a parse failure, which the reader already logs.
-- *** >= (not >) is deliberate: *** an identical re-pull of an unchanged trade
-- must still refresh ModifiedAtUtc, so the freshness axis stays meaningful.
--
-- -- upsert-only: NEVER add a DELETE / WHEN NOT MATCHED BY SOURCE branch. One
--    window is a partial view of the tape; deleting what it omits would wipe the
--    table.
-- -- no checksum short-circuit: the Checksum column was removed by explicit user
--    decision (U2).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeAllTrades
    @Records arm.TradesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.AllTrades AS tgt
    USING (
        -- De-dup on TradeNumber before the MERGE: NEWEST REVISION WINS.
        -- (Required regardless: an un-deduped MERGE errors if one target row is
        -- matched by two source rows.)
        SELECT TradeNumber, State, Product, Location, PipelineTerminal, PriceBasis, Term,
               TermStart, TermEnd, Price, Volume, UnitOfMeasure, Executed, LastUpdated,
               TradeType, Side, BidTrader, BidLegalName, BidAddress, BidCommission,
               OfferTrader, OfferLegalName, OfferAddress, OfferCommission, SpreadTradeNumber,
               ApportionmentProtected, ClearingID, SettlementCurrency, ContractTerms, GTandC,
               Notes, InIndex, ClickAndTrade, ProductType
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY TradeNumber
                                      ORDER BY LastUpdated DESC) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.TradeNumber = src.TradeNumber
    WHEN MATCHED AND (tgt.LastUpdated IS NULL
                      OR (src.LastUpdated IS NOT NULL AND src.LastUpdated >= tgt.LastUpdated))
    THEN UPDATE SET
        State                  = src.State,
        Product                = src.Product,
        Location               = src.Location,
        PipelineTerminal       = src.PipelineTerminal,
        PriceBasis             = src.PriceBasis,
        Term                   = src.Term,
        TermStart              = src.TermStart,
        TermEnd                = src.TermEnd,
        Price                  = src.Price,
        Volume                 = src.Volume,
        UnitOfMeasure          = src.UnitOfMeasure,
        Executed               = src.Executed,
        LastUpdated            = src.LastUpdated,
        TradeType              = src.TradeType,
        Side                   = src.Side,
        BidTrader              = src.BidTrader,
        BidLegalName           = src.BidLegalName,
        BidAddress             = src.BidAddress,
        BidCommission          = src.BidCommission,
        OfferTrader            = src.OfferTrader,
        OfferLegalName         = src.OfferLegalName,
        OfferAddress           = src.OfferAddress,
        OfferCommission        = src.OfferCommission,
        SpreadTradeNumber      = src.SpreadTradeNumber,
        ApportionmentProtected = src.ApportionmentProtected,
        ClearingID             = src.ClearingID,
        SettlementCurrency     = src.SettlementCurrency,
        ContractTerms          = src.ContractTerms,
        GTandC                 = src.GTandC,
        Notes                  = src.Notes,
        InIndex                = src.InIndex,
        ClickAndTrade          = src.ClickAndTrade,
        ProductType            = src.ProductType,
        ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeNumber, State, Product, Location, PipelineTerminal, PriceBasis, Term,
                TermStart, TermEnd, Price, Volume, UnitOfMeasure, Executed, LastUpdated,
                TradeType, Side, BidTrader, BidLegalName, BidAddress, BidCommission,
                OfferTrader, OfferLegalName, OfferAddress, OfferCommission, SpreadTradeNumber,
                ApportionmentProtected, ClearingID, SettlementCurrency, ContractTerms, GTandC,
                Notes, InIndex, ClickAndTrade, ProductType, ModifiedAtUtc)
        VALUES (src.TradeNumber, src.State, src.Product, src.Location, src.PipelineTerminal,
                src.PriceBasis, src.Term, src.TermStart, src.TermEnd, src.Price, src.Volume,
                src.UnitOfMeasure, src.Executed, src.LastUpdated, src.TradeType, src.Side,
                src.BidTrader, src.BidLegalName, src.BidAddress, src.BidCommission,
                src.OfferTrader, src.OfferLegalName, src.OfferAddress, src.OfferCommission,
                src.SpreadTradeNumber, src.ApportionmentProtected, src.ClearingID,
                src.SettlementCurrency, src.ContractTerms, src.GTandC, src.Notes, src.InIndex,
                src.ClickAndTrade, src.ProductType, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeMyTrades
-- IDENTICAL in every respect to arm.usp_BulkMergeAllTrades except the target
-- table - same merge key (TradeNumber), same shared TVP type (arm.TradesTvp), same
-- ORDER BY LastUpdated DESC dedup, same D14 recency guard, same 34-column lists.
-- The two are kept as separate procs (rather than one proc with a table parameter)
-- so each sink gets its own SqlWriteGate key: {server}/{db}::<proc>. That is
-- deliberate - the two tables' merges must NOT serialise against one another.
--
-- *** No cross-table logic of any kind. *** The same TradeNumber legitimately
-- exists in arm.AllTrades and arm.MyTrades; there is no FK, no cross-table dedup
-- and no attempt to reconcile the two. They are two views of the venue at
-- different disclosure levels (design SS1.2).
--
-- The 14 counterparty columns that arrive empty from allTrades are populated here.
-- They are read and written the SAME WAY for both endpoints - the reader does not
-- branch on the endpoint - so this proc's column list is byte-for-byte the one
-- above.
--
-- -- upsert-only: NEVER add a DELETE / WHEN NOT MATCHED BY SOURCE branch.
-- -- no checksum short-circuit (Checksum removed by user decision U2).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeMyTrades
    @Records arm.TradesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.MyTrades AS tgt
    USING (
        -- De-dup on TradeNumber before the MERGE: NEWEST REVISION WINS. See the
        -- arm.usp_BulkMergeAllTrades header for why the ORDER BY column matters.
        SELECT TradeNumber, State, Product, Location, PipelineTerminal, PriceBasis, Term,
               TermStart, TermEnd, Price, Volume, UnitOfMeasure, Executed, LastUpdated,
               TradeType, Side, BidTrader, BidLegalName, BidAddress, BidCommission,
               OfferTrader, OfferLegalName, OfferAddress, OfferCommission, SpreadTradeNumber,
               ApportionmentProtected, ClearingID, SettlementCurrency, ContractTerms, GTandC,
               Notes, InIndex, ClickAndTrade, ProductType
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY TradeNumber
                                      ORDER BY LastUpdated DESC) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.TradeNumber = src.TradeNumber
    WHEN MATCHED AND (tgt.LastUpdated IS NULL
                      OR (src.LastUpdated IS NOT NULL AND src.LastUpdated >= tgt.LastUpdated))
    THEN UPDATE SET
        State                  = src.State,
        Product                = src.Product,
        Location               = src.Location,
        PipelineTerminal       = src.PipelineTerminal,
        PriceBasis             = src.PriceBasis,
        Term                   = src.Term,
        TermStart              = src.TermStart,
        TermEnd                = src.TermEnd,
        Price                  = src.Price,
        Volume                 = src.Volume,
        UnitOfMeasure          = src.UnitOfMeasure,
        Executed               = src.Executed,
        LastUpdated            = src.LastUpdated,
        TradeType              = src.TradeType,
        Side                   = src.Side,
        BidTrader              = src.BidTrader,
        BidLegalName           = src.BidLegalName,
        BidAddress             = src.BidAddress,
        BidCommission          = src.BidCommission,
        OfferTrader            = src.OfferTrader,
        OfferLegalName         = src.OfferLegalName,
        OfferAddress           = src.OfferAddress,
        OfferCommission        = src.OfferCommission,
        SpreadTradeNumber      = src.SpreadTradeNumber,
        ApportionmentProtected = src.ApportionmentProtected,
        ClearingID             = src.ClearingID,
        SettlementCurrency     = src.SettlementCurrency,
        ContractTerms          = src.ContractTerms,
        GTandC                 = src.GTandC,
        Notes                  = src.Notes,
        InIndex                = src.InIndex,
        ClickAndTrade          = src.ClickAndTrade,
        ProductType            = src.ProductType,
        ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TradeNumber, State, Product, Location, PipelineTerminal, PriceBasis, Term,
                TermStart, TermEnd, Price, Volume, UnitOfMeasure, Executed, LastUpdated,
                TradeType, Side, BidTrader, BidLegalName, BidAddress, BidCommission,
                OfferTrader, OfferLegalName, OfferAddress, OfferCommission, SpreadTradeNumber,
                ApportionmentProtected, ClearingID, SettlementCurrency, ContractTerms, GTandC,
                Notes, InIndex, ClickAndTrade, ProductType, ModifiedAtUtc)
        VALUES (src.TradeNumber, src.State, src.Product, src.Location, src.PipelineTerminal,
                src.PriceBasis, src.Term, src.TermStart, src.TermEnd, src.Price, src.Volume,
                src.UnitOfMeasure, src.Executed, src.LastUpdated, src.TradeType, src.Side,
                src.BidTrader, src.BidLegalName, src.BidAddress, src.BidCommission,
                src.OfferTrader, src.OfferLegalName, src.OfferAddress, src.OfferCommission,
                src.SpreadTradeNumber, src.ApportionmentProtected, src.ClearingID,
                src.SettlementCurrency, src.ContractTerms, src.GTandC, src.Notes, src.InIndex,
                src.ClickAndTrade, src.ProductType, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeSettlements
-- Merge key: ALL SIX PK columns
--   (SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term).
-- TVP column order (9): see 002.
--
-- *** `PieplineTerminal` IS MISSPELLED ON PURPOSE *** - the user's spelling, part
-- of the PK, reproduced verbatim in 001, 002, here and in the C# property name
-- (decision D2). Do not "tidy" it: the TVP binds by POSITION and the rename would
-- fail loudly here but silently corrupt nothing-in-particular elsewhere.
--
-- =========================================================================
-- WHY THERE IS NO  ORDER BY  COLUMN IN THE DEDUP
-- =========================================================================
-- The trades procs order the dedup by LastUpdated DESC because a trade revision
-- is identified by its timestamp. *** SETTLEMENTS HAS NO SUCH COLUMN: the payload
-- carries NO revision, version, status or as-of field of any kind. *** There is
-- nothing meaningful to order by, so the tie-break is ORDER BY (SELECT NULL) -
-- plain last-wins - and that is acceptable HERE (and not in the trades procs)
-- because a duplicate 6-column key WITHIN one request would mean the vendor
-- published two prices for the same (date, product, location, pipeline, basis,
-- term). That never occurred in 1,443 live rows; it would be a VENDOR DEFECT to
-- report (usp_ValidateLoad PkDuplicates), not something for this proc to
-- arbitrate. The C# sink de-dups last-in-file-order first, so this is insurance,
-- correctly placed.
--
-- CROSS-CHUNK COLLISIONS ARE IMPOSSIBLE HERE, which is the other half of why no
-- recency guard is needed: chunks partition by SettlementDate, which is PK
-- column 1, so two concurrent settlements units always write DISJOINT key sets.
-- (The trades window predicate, LastUpdated, is NOT a key column - hence the
-- guard there.)
--
-- *** A PRICE REVISION OVERWRITES - THERE IS NO HISTORY. *** Price sits OUTSIDE
-- the PK, so a restatement replaces the prior print and the old value is gone.
-- That is correct IF settlements are never restated, which is UNVERIFIED (each
-- settlement date was captured exactly once, and the payload has no field that
-- would distinguish a revision from an original). If it proves false, the pattern
-- to reach for is OPIS's arm.LPReportHistory (sql/OPIS/001, sql/OPIS/003), which
-- puts the distinguishing value INSIDE the key so both prints survive.
-- Deliberately NOT built now - and the note lives here so whoever revisits it
-- finds the precedent.
--
-- -- upsert-only: NEVER add a DELETE / WHEN NOT MATCHED BY SOURCE branch. A
--    weekend or not-yet-published window returns NOTHING inside an HTTP 200;
--    deleting what it omits would wipe every settled curve.
-- -- no checksum short-circuit (Checksum removed by user decision U2).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSettlements
    @Records arm.SettlementsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Settlements AS tgt
    USING (
        -- De-dup on the 6-column key before the MERGE (plain last-wins - there is
        -- no recency column to order by; see the header).
        SELECT SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term,
               TermStart, TermEnd, Price
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY SettlementDate, Product, Location,
                                                   PieplineTerminal, PriceBasis, Term
                                      ORDER BY (SELECT NULL)) AS rn
            FROM @Records
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.SettlementDate   = src.SettlementDate
       AND tgt.Product          = src.Product
       AND tgt.Location         = src.Location
       AND tgt.PieplineTerminal = src.PieplineTerminal
       AND tgt.PriceBasis       = src.PriceBasis
       AND tgt.Term             = src.Term
    WHEN MATCHED THEN UPDATE SET
        TermStart     = src.TermStart,
        TermEnd       = src.TermEnd,
        Price         = src.Price,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term,
                TermStart, TermEnd, Price, ModifiedAtUtc)
        VALUES (src.SettlementDate, src.Product, src.Location, src.PieplineTerminal,
                src.PriceBasis, src.Term, src.TermStart, src.TermEnd, src.Price,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad(@DateFrom, @DateTo, @ModifiedSinceUtc)
-- Post-load anomaly report over the LOAD WINDOW. Returns ONE result set with the
-- repo's uniform shape so the C# ModernCommoditiesLoadValidator can log it
-- generically:
--   CheckName      VARCHAR(48)   what was measured
--   Scope          NVARCHAR(200) the grouping key the count applies to
--   ExpectedCount  INT           the expected value where one exists, else NULL
--   ActualCount    BIGINT        the measured count
--   Detail         NVARCHAR(400) extra context
-- The validator treats a row with a NON-NULL ExpectedCount that differs from
-- ActualCount as an anomaly (LogWarning) and every other row as informational
-- (LogInformation).
--
-- =========================================================================
-- *** OBSERVATIONAL ONLY. NO SIDE EFFECTS. IT MUST NEVER THROW AND NEVER
-- RAISERROR. *** The caller decides what is a hard failure.
-- =========================================================================
-- A legitimately sparse window MUST NOT be able to fail an otherwise-good load: a
-- quiet myTrades month, a weekend-only settlements window, a not-yet-published
-- current day, or a run whose recency guard correctly updated nothing are all
-- NORMAL. Invalid arguments are therefore reported as an ArgumentsInvalid ROW
-- (ExpectedCount 0 vs ActualCount 1, so the validator logs a warning) instead of
-- raising an error - the NGI posture, and deliberately NOT AGSI's RAISERROR.
--   NOTE: design SS9 says "argument validation mirrors AGSI: RAISERROR". That
--   sentence is superseded here by the standing rule that a validation proc never
--   RAISERRORs, and by the explicit instruction for this loader. Flagged, not
--   silently reconciled.
--
-- =========================================================================
-- SCOPING - THERE IS NO FACT -> HUB JOIN ANYWHERE IN THIS PROC
-- =========================================================================
-- No fact table has a FileLogId (decision D1), so NGI's IssueDateMatchesRequest
-- (which joins facts to the hub) HAS NO ANALOGUE HERE. Every check uses exactly
-- one of three mechanisms:
--   WINDOW     trades:      LastUpdated >= @DateFrom AND LastUpdated < @DateTo + 1
--              settlements: SettlementDate BETWEEN @DateFrom AND @DateTo
--   FRESHNESS  ModifiedAtUtc >= @ModifiedSinceUtc - the rows THIS RUN wrote. This
--              is the substitute for the lost join. @ModifiedSinceUtc is passed as
--              (run start - ValidationClockSkewMinutes, default 5) because the
--              fact rows are stamped by the SQL SERVER's clock while the run start
--              comes from the HOST's; without the tolerance every freshness check
--              would read zero. When it is NULL the freshness checks fall back to
--              the whole table and say so in Detail.
--   HUB-ONLY   aggregates over arm.FileLog alone, filtered on
--              WindowEnd BETWEEN @DateFrom AND @DateTo (+ LastCheckedUtc).
--
-- The window itself comes from ModComTime.ResolveWindow - the SAME helper the
-- work-unit providers call - so the validation window can never drift from the
-- load window. *** NOTE THE 31-DAY FIGURE: DaysBack = 30 means
-- start = end - 30, INCLUSIVE BOTH ENDS, so the shipped default window is 31
-- CALENDAR DAYS (decision D13). That extra day is deliberate free overlap; do NOT
-- "fix" any expected-count arithmetic here into 30 days. ***
--
-- Checks that carry a real expectation (ExpectedCount = 0): UnkeyableDrops,
-- PkDuplicates, TermOrdering, SettlementSentinelPairing, FileLogFailuresInWindow.
-- EVERYTHING ELSE IS INFORMATIONAL BY DESIGN - do not "promote" any of them.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @DateFrom         DATE,
    @DateTo           DATE,
    @ModifiedSinceUtc DATETIME2(3) = NULL
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

    -- =========================================================================
    -- Pre-computed scalars / sets used by several checks.
    -- =========================================================================
    DECLARE @WindowDays   INT          = DATEDIFF(DAY, @DateFrom, @DateTo) + 1;
    DECLARE @TradesFrom   DATETIME2(0) = CAST(@DateFrom AS DATETIME2(0));
    DECLARE @TradesToExcl DATETIME2(0) = CAST(DATEADD(DAY, 1, @DateTo) AS DATETIME2(0));
    DECLARE @Since        DATETIME2(3) = @ModifiedSinceUtc;   -- NULL = no freshness filter
    DECLARE @Ceiling      DECIMAL(11,2) = 9999999.99;         -- the DECIMAL(9,2) ceiling

    DECLARE @FreshNote NVARCHAR(200) =
        CASE WHEN @Since IS NULL
             THEN N'no @ModifiedSinceUtc supplied - counted over the WHOLE table'
             ELSE CONCAT(N'ModifiedAtUtc >= ', CONVERT(VARCHAR(23), @Since, 126)) END;

    -- ---- HUB aggregates for this run's pulls, per endpoint -------------------
    -- Scoped by window AND (when supplied) by freshness, so a re-run of the same
    -- window in a later hour does not double-count an earlier hour's pulls.
    DECLARE @HubStats TABLE
    (
        Endpoint    VARCHAR(40) NOT NULL PRIMARY KEY,
        Pulls       INT         NOT NULL,
        ParsedRows  BIGINT      NOT NULL,
        DroppedRows BIGINT      NOT NULL
    );

    INSERT @HubStats (Endpoint, Pulls, ParsedRows, DroppedRows)
    SELECT e.[Name],
           COUNT(1),
           SUM(CAST(f.[RowCount] AS BIGINT)),
           SUM(CAST(f.DroppedRowCount AS BIGINT))
    FROM arm.FileLog AS f
    INNER JOIN arm.Endpoint AS e ON e.Id = f.EndpointId
    WHERE f.WindowEnd BETWEEN @DateFrom AND @DateTo
      AND (@Since IS NULL OR f.LastCheckedUtc >= @Since)
    GROUP BY e.[Name];

    DECLARE @ParsedAllTrades   BIGINT = (SELECT ParsedRows FROM @HubStats WHERE Endpoint = 'AllTrades');
    DECLARE @ParsedMyTrades    BIGINT = (SELECT ParsedRows FROM @HubStats WHERE Endpoint = 'MyTrades');
    DECLARE @ParsedSettlements BIGINT = (SELECT ParsedRows FROM @HubStats WHERE Endpoint = 'Settlements');

    -- ---- Settlements rows-per-date MEDIAN, for the +/-20% band check ---------
    -- *** A BAND, NEVER AN EQUALITY. *** The curve grid is not fixed (720 rows
    -- Thursday vs 723 Friday on consecutive live dates) and the curve depth grows
    -- over time, so this flags a JUMP, not the level. Median via ROW_NUMBER (no
    -- PERCENTILE_CONT / STRING_AGG dependency, matching the NGI posture).
    DECLARE @MedianRowsPerDate DECIMAL(18,4) = NULL;

    SELECT @MedianRowsPerDate = AVG(1.0 * y.RowsForDate)
    FROM (
        SELECT x.RowsForDate,
               ROW_NUMBER() OVER (ORDER BY x.RowsForDate) AS rn,
               COUNT(1) OVER ()                           AS n
        FROM (
            SELECT s.SettlementDate, COUNT(1) AS RowsForDate
            FROM arm.Settlements AS s
            WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo
            GROUP BY s.SettlementDate
        ) AS x
    ) AS y
    WHERE y.rn IN ((y.n + 1) / 2, (y.n + 2) / 2);

    DECLARE @OutOfBandDates INT = 0;

    IF @MedianRowsPerDate IS NOT NULL
    BEGIN
        SELECT @OutOfBandDates = COUNT(1)
        FROM (
            SELECT s.SettlementDate, COUNT(1) AS RowsForDate
            FROM arm.Settlements AS s
            WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo
            GROUP BY s.SettlementDate
        ) AS c
        WHERE c.RowsForDate < @MedianRowsPerDate * 0.8
           OR c.RowsForDate > @MedianRowsPerDate * 1.2;
    END

    -- ---- SPREAD-GROUP integrity, precomputed ---------------------------------
    -- *** REPORT, NEVER ENFORCE. *** A 3-row spread group can legitimately
    -- STRADDLE the window boundary (each leg carries its own LastUpdated), so a
    -- non-zero count here is not necessarily a defect.
    DECLARE @Spread TABLE
    (
        TableName VARCHAR(20) NOT NULL,
        Measure   VARCHAR(40) NOT NULL,
        Cnt       BIGINT      NOT NULL,
        PRIMARY KEY (TableName, Measure)
    );

    INSERT @Spread (TableName, Measure, Cnt)
    SELECT 'AllTrades', 'OrphanSpreadRef', COUNT(1)
    FROM (
        SELECT DISTINCT t.SpreadTradeNumber
        FROM arm.AllTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
          AND t.SpreadTradeNumber IS NOT NULL AND LEN(t.SpreadTradeNumber) > 0
          AND NOT EXISTS (SELECT 1 FROM arm.AllTrades AS p
                          WHERE p.TradeNumber = TRY_CONVERT(INT, t.SpreadTradeNumber)
                            AND p.TradeType   = 'Spread')
    ) AS o;

    INSERT @Spread (TableName, Measure, Cnt)
    SELECT 'AllTrades', 'GroupSizeNot3', COUNT(1)
    FROM (
        SELECT t.SpreadTradeNumber
        FROM arm.AllTrades AS t
        WHERE t.SpreadTradeNumber IS NOT NULL AND LEN(t.SpreadTradeNumber) > 0
          AND EXISTS (SELECT 1 FROM arm.AllTrades AS w
                      WHERE w.SpreadTradeNumber = t.SpreadTradeNumber
                        AND w.LastUpdated >= @TradesFrom AND w.LastUpdated < @TradesToExcl)
        GROUP BY t.SpreadTradeNumber
        HAVING COUNT(1) <> 3
    ) AS g;

    INSERT @Spread (TableName, Measure, Cnt)
    SELECT 'MyTrades', 'OrphanSpreadRef', COUNT(1)
    FROM (
        SELECT DISTINCT t.SpreadTradeNumber
        FROM arm.MyTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
          AND t.SpreadTradeNumber IS NOT NULL AND LEN(t.SpreadTradeNumber) > 0
          AND NOT EXISTS (SELECT 1 FROM arm.MyTrades AS p
                          WHERE p.TradeNumber = TRY_CONVERT(INT, t.SpreadTradeNumber)
                            AND p.TradeType   = 'Spread')
    ) AS o;

    INSERT @Spread (TableName, Measure, Cnt)
    SELECT 'MyTrades', 'GroupSizeNot3', COUNT(1)
    FROM (
        SELECT t.SpreadTradeNumber
        FROM arm.MyTrades AS t
        WHERE t.SpreadTradeNumber IS NOT NULL AND LEN(t.SpreadTradeNumber) > 0
          AND EXISTS (SELECT 1 FROM arm.MyTrades AS w
                      WHERE w.SpreadTradeNumber = t.SpreadTradeNumber
                        AND w.LastUpdated >= @TradesFrom AND w.LastUpdated < @TradesToExcl)
        GROUP BY t.SpreadTradeNumber
        HAVING COUNT(1) <> 3
    ) AS g;

    -- ---- UNKNOWN enum values -------------------------------------------------
    -- The "known" sets below are LIVE SNAPSHOTS from 2026-08-24, *** NOT an enum
    -- and NOT a contract. *** No vendor document enumerates any of these columns,
    -- which is exactly why there is no lookup table and no CHECK constraint on
    -- them (001). A new State, a third ProductType, a fifth UnitOfMeasure or a CAD
    -- SettlementCurrency MUST load and be REPORTED here - never rejected.
    DECLARE @Known TABLE (ColName VARCHAR(30) NOT NULL, Val VARCHAR(256) NOT NULL,
                          PRIMARY KEY (ColName, Val));

    INSERT @Known (ColName, Val) VALUES
        ('State',                 'Finalized'),
        ('State',                 'Cancelled'),
        ('TradeType',             'Outright'),
        ('TradeType',             'Spread'),
        ('TradeType',             'First Leg'),
        ('TradeType',             'Second Leg'),
        ('ProductType',           'Physical'),
        ('ProductType',           'Financial'),
        ('UnitOfMeasure',         'm3/month'),
        ('UnitOfMeasure',         'bbls/day'),
        ('UnitOfMeasure',         'bbls/month'),
        ('UnitOfMeasure',         'contracts/month'),
        ('Side',                  'Buy'),
        ('Side',                  'Sell'),
        ('SettlementCurrency',    'USD'),
        ('SettlementsPriceBasis', 'WTI CMA'),
        ('SettlementsPriceBasis', 'USD $');

    DECLARE @UnknownEnum TABLE (TableName VARCHAR(20) NOT NULL, ColName VARCHAR(30) NOT NULL,
                                Val VARCHAR(256) NOT NULL, Rws BIGINT NOT NULL);

    INSERT @UnknownEnum (TableName, ColName, Val, Rws)
    SELECT o.TableName, o.ColName, o.Val, o.Rws
    FROM (
        SELECT 'AllTrades' AS TableName, v.ColName, v.Val, COUNT(1) AS Rws
        FROM arm.AllTrades AS t
        CROSS APPLY (VALUES ('State',              CAST(t.State AS VARCHAR(256))),
                            ('TradeType',          CAST(t.TradeType AS VARCHAR(256))),
                            ('ProductType',        CAST(t.ProductType AS VARCHAR(256))),
                            ('UnitOfMeasure',      CAST(t.UnitOfMeasure AS VARCHAR(256))),
                            ('Side',               CAST(t.Side AS VARCHAR(256))),
                            ('SettlementCurrency', CAST(t.SettlementCurrency AS VARCHAR(256)))
                    ) AS v(ColName, Val)
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
          AND v.Val IS NOT NULL AND LEN(v.Val) > 0
        GROUP BY v.ColName, v.Val

        UNION ALL
        SELECT 'MyTrades', v.ColName, v.Val, COUNT(1)
        FROM arm.MyTrades AS t
        CROSS APPLY (VALUES ('State',              CAST(t.State AS VARCHAR(256))),
                            ('TradeType',          CAST(t.TradeType AS VARCHAR(256))),
                            ('ProductType',        CAST(t.ProductType AS VARCHAR(256))),
                            ('UnitOfMeasure',      CAST(t.UnitOfMeasure AS VARCHAR(256))),
                            ('Side',               CAST(t.Side AS VARCHAR(256))),
                            ('SettlementCurrency', CAST(t.SettlementCurrency AS VARCHAR(256)))
                    ) AS v(ColName, Val)
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
          AND v.Val IS NOT NULL AND LEN(v.Val) > 0
        GROUP BY v.ColName, v.Val

        UNION ALL
        -- Settlements PriceBasis is tracked under its OWN key: it shares the column
        -- NAME with the trades PriceBasis but NOT its value space (2 short values
        -- here vs compound index expressions to 45 chars there), which is also why
        -- there is deliberately no shared PriceBasis dimension.
        SELECT 'Settlements', 'SettlementsPriceBasis', CAST(s.PriceBasis AS VARCHAR(256)), COUNT(1)
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo
        GROUP BY s.PriceBasis
    ) AS o
    WHERE NOT EXISTS (SELECT 1 FROM @Known AS k
                      WHERE k.ColName = o.ColName AND k.Val = o.Val);

    DECLARE @UnknownEnumCount INT = (SELECT COUNT(1) FROM @UnknownEnum);

    -- =========================================================================
    -- THE REPORT. ONE result set, uniform shape, ORDER BY CheckName, Scope.
    -- =========================================================================
    ;WITH Report AS
    (
        -- ================= 1) RowCountInWindow (INFORMATIONAL) ===============
        -- Rows in the window per target. Baselines are SNAPSHOTS, not contracts:
        -- allTrades ~58 rows/CALENDAR day (the 170-day measurement - NOT the 5-day
        -- one, which implies ~19.6 and does not reconcile), myTrades ~0.21/day,
        -- settlements ~721 rows per PUBLISHED date (~505/calendar day).
        SELECT
            CAST('RowCountInWindow' AS VARCHAR(48))  AS CheckName,
            CAST('AllTrades' AS NVARCHAR(200))       AS Scope,
            CAST(NULL AS INT)                        AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)                 AS ActualCount,
            CAST(CONCAT('windowDays=', @WindowDays,
                        ' (DaysBack=30 spans 31 days by design - decision D13);',
                        ' scoped by LastUpdated; baseline ~58 rows/calendar day is a SNAPSHOT, not a contract')
                 AS NVARCHAR(400))                   AS Detail
        FROM arm.AllTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        UNION ALL
        SELECT
            CAST('RowCountInWindow' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('windowDays=', @WindowDays,
                        '; scoped by LastUpdated; baseline ~0.21 rows/day (37 rows per 180 days)',
                        ' - a QUIET window is normal and is NOT an anomaly')
                 AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        UNION ALL
        SELECT
            CAST('RowCountInWindow' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('windowDays=', @WindowDays,
                        '; scoped by SettlementDate; baseline ~721 rows per PUBLISHED date',
                        ' (weekends/holidays publish NOTHING - that is normal)')
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo

        -- ================= 2) RowsPersistedThisRun (INFORMATIONAL) ===========
        -- The SUBSTITUTE for the lost fact -> hub join: rows this run actually
        -- WROTE (freshness), next to the rows the hub says were PARSED.
        -- *** do NOT set ExpectedCount here: the trades recency guard legitimately
        -- skips updates, so pulled != persisted is NORMAL. *** A run can pull
        -- 1,798 rows and persist 0 changes and be perfectly healthy.
        UNION ALL
        SELECT
            CAST('RowsPersistedThisRun' AS VARCHAR(48)),
            CAST('AllTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('hubParsedRows=', ISNULL(CAST(@ParsedAllTrades AS VARCHAR(20)), '(no pulls)'),
                        '; ', @FreshNote,
                        '; do NOT expect equality - the recency guard skips updates')
                 AS NVARCHAR(400))
        FROM arm.AllTrades AS t
        WHERE (@Since IS NULL OR t.ModifiedAtUtc >= @Since)

        UNION ALL
        SELECT
            CAST('RowsPersistedThisRun' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('hubParsedRows=', ISNULL(CAST(@ParsedMyTrades AS VARCHAR(20)), '(no pulls)'),
                        '; ', @FreshNote,
                        '; do NOT expect equality - the recency guard skips updates')
                 AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE (@Since IS NULL OR t.ModifiedAtUtc >= @Since)

        UNION ALL
        SELECT
            CAST('RowsPersistedThisRun' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('hubParsedRows=', ISNULL(CAST(@ParsedSettlements AS VARCHAR(20)), '(no pulls)'),
                        '; ', @FreshNote,
                        '; settlements MATCHED is unconditional, so persisted should track parsed closely')
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE (@Since IS NULL OR s.ModifiedAtUtc >= @Since)

        -- ================= 3) UnkeyableDrops (EXPECT 0) ======================
        -- SUM(DroppedRowCount) over this run's hub rows. A drop means a blank or
        -- unparseable Trade Number, a blank settlements key column, or an
        -- OVER-WIDTH KEY (which is dropped rather than truncated, because a
        -- truncated key silently MERGEs onto a DIFFERENT logical entity).
        -- Observed 0 across all 1,578 live rows, so 0 is a genuine expectation.
        -- *** With no fact-side provenance column this is the ONLY durable record
        -- that rows were discarded. ***
        UNION ALL
        SELECT
            CAST('UnkeyableDrops' AS VARCHAR(48)),
            CAST(h.Endpoint AS NVARCHAR(200)),
            CAST(0 AS INT),
            h.DroppedRows,
            CAST(CONCAT('pulls=', h.Pulls, ' parsedRows=', h.ParsedRows,
                        '; a non-zero value is ACTIONABLE (observed 0 across 1,578 live rows)')
                 AS NVARCHAR(400))
        FROM @HubStats AS h

        -- ================= 4) PkDuplicates (EXPECT 0) ========================
        -- Structurally guaranteed by each PRIMARY KEY. Kept as a cheap honest
        -- assertion that stays meaningful if anyone ever disables a constraint for
        -- a bulk load. Verified 0 in every capture (98/98, 37/37, and 1,443 rows
        -- with zero collisions on the 6-column settlements key).
        UNION ALL
        SELECT
            CAST('PkDuplicates' AS VARCHAR(48)),
            CAST('AllTrades' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('duplicate TradeNumber values; guaranteed 0 by PK_AllTrades' AS NVARCHAR(400))
        FROM (
            SELECT t.TradeNumber
            FROM arm.AllTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
            GROUP BY t.TradeNumber
            HAVING COUNT(1) > 1
        ) AS d

        UNION ALL
        SELECT
            CAST('PkDuplicates' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('duplicate TradeNumber values; guaranteed 0 by PK_MyTrades' AS NVARCHAR(400))
        FROM (
            SELECT t.TradeNumber
            FROM arm.MyTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
            GROUP BY t.TradeNumber
            HAVING COUNT(1) > 1
        ) AS d

        UNION ALL
        SELECT
            CAST('PkDuplicates' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('duplicate 6-column keys; guaranteed 0 by PK_Settlements. A duplicate would mean the vendor published two prices for one (date,product,location,pipeline,basis,term)'
                 AS NVARCHAR(400))
        FROM (
            SELECT s.SettlementDate, s.Product, s.Location, s.PieplineTerminal, s.PriceBasis, s.Term
            FROM arm.Settlements AS s
            WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo
            GROUP BY s.SettlementDate, s.Product, s.Location, s.PieplineTerminal, s.PriceBasis, s.Term
            HAVING COUNT(1) > 1
        ) AS d

        -- ================= 5) TermOrdering (EXPECT 0) ========================
        -- Rows with TermStart > TermEnd, checked only where BOTH are non-NULL.
        -- Zero violations observed across all three captures. Note this is an
        -- ORDERING check only - a single-month term where start and end sit inside
        -- the same month is perfectly valid.
        UNION ALL
        SELECT
            CAST('TermOrdering' AS VARCHAR(48)),
            CAST('AllTrades' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.TermStart IS NOT NULL AND t.TermEnd IS NOT NULL
                                  AND t.TermStart > t.TermEnd THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1), '; checked only where both dates are non-NULL')
                 AS NVARCHAR(400))
        FROM arm.AllTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        UNION ALL
        SELECT
            CAST('TermOrdering' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.TermStart IS NOT NULL AND t.TermEnd IS NOT NULL
                                  AND t.TermStart > t.TermEnd THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1), '; checked only where both dates are non-NULL')
                 AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        UNION ALL
        SELECT
            CAST('TermOrdering' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN s.TermStart IS NOT NULL AND s.TermEnd IS NOT NULL
                                  AND s.TermStart > s.TermEnd THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1),
                        '; TermEnd legitimately reaches 2031-12-31 (~5.4 years forward) - no tighter range assumption')
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo

        -- ================= 6) SettlementSentinelRows (INFORMATIONAL) =========
        -- *** NEVER normalise the '-' sentinel to NULL: Location and
        -- PieplineTerminal are PRIMARY KEY values. *** Baseline 82 of 1,443 rows
        -- (~5.7%), all Product = 'Sweet Guernsey Blend' (41 term months x 2
        -- settlement dates): one blended product with no single physical location
        -- or pipeline. INFORMATIONAL - the sentinel is CORRECT DATA.
        UNION ALL
        SELECT
            CAST('SettlementSentinelRows' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN s.Location = '-' OR s.PieplineTerminal = '-'
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rows=', COUNT(1),
                        '; baseline 82/1443 (~5.7%), all Product=''Sweet Guernsey Blend''.',
                        ' INFORMATIONAL - ''-'' is a PK VALUE, never a null sentinel')
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo

        -- ================= 7) SettlementSentinelPairing (EXPECT 0) ===========
        -- Rows where EXACTLY ONE of Location / PieplineTerminal is '-'. The two
        -- were ALWAYS '-' together in 82/82 rows, so a one-sided sentinel would
        -- signal a vendor change or a parse bug. This is the one sentinel-related
        -- check with a real expectation.
        UNION ALL
        SELECT
            CAST('SettlementSentinelPairing' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(ISNULL(SUM(CASE WHEN (s.Location = '-' AND s.PieplineTerminal <> '-')
                                   OR (s.Location <> '-' AND s.PieplineTerminal = '-')
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST('the ''-'' sentinel appeared as a PAIR in 82/82 live rows; a one-sided value means a vendor change or a parse bug'
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo

        -- ================= 8) StateDistribution (INFORMATIONAL) =============
        -- Baselines: AllTrades Finalized 95 / Cancelled 3; MyTrades 35 / 2.
        -- *** Watch for a JUMP in Cancelled, never FAIL on one *** - a
        -- cancellation is exactly what the hourly re-pull exists to capture.
        UNION ALL
        SELECT
            CAST('StateDistribution' AS VARCHAR(48)),
            CAST(CONCAT('AllTrades State=', ISNULL(t.State, '(null)')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('INFORMATIONAL - baseline Finalized 95 / Cancelled 3; a rise in Cancelled is a captured revision, not a defect'
                 AS NVARCHAR(400))
        FROM arm.AllTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        GROUP BY t.State

        UNION ALL
        SELECT
            CAST('StateDistribution' AS VARCHAR(48)),
            CAST(CONCAT('MyTrades State=', ISNULL(t.State, '(null)')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('INFORMATIONAL - baseline Finalized 35 / Cancelled 2' AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        GROUP BY t.State

        -- ================= 9) AnonymisedColumnsNull ==========================
        -- *** INFORMATIONAL ONLY, NEVER AN ERROR. 100% NULL IN arm.AllTrades IS
        -- CORRECT. *** The vendor anonymises these 14 columns on that endpoint;
        -- they are kept on the table (per the user's DDL) so that if the venue ever
        -- starts populating one, the data is captured rather than discarded.
        -- ClearingID is deliberately NOT in this list - see check 11.
        UNION ALL
        SELECT
            CAST('AnonymisedColumnsNull' AS VARCHAR(48)),
            CAST(CONCAT('AllTrades.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.NullRows AS BIGINT),
            CAST(CONCAT('rowsInWindow=', a.RowsInWindow,
                        ' - INFORMATIONAL ONLY, NEVER an error: 100% NULL here is CORRECT (vendor anonymisation)')
                 AS NVARCHAR(400))
        FROM (
            SELECT COUNT(1) AS RowsInWindow,
                   ISNULL(SUM(CASE WHEN t.Side               IS NULL THEN 1 ELSE 0 END), 0) AS c01,
                   ISNULL(SUM(CASE WHEN t.BidTrader          IS NULL THEN 1 ELSE 0 END), 0) AS c02,
                   ISNULL(SUM(CASE WHEN t.BidLegalName       IS NULL THEN 1 ELSE 0 END), 0) AS c03,
                   ISNULL(SUM(CASE WHEN t.BidAddress         IS NULL THEN 1 ELSE 0 END), 0) AS c04,
                   ISNULL(SUM(CASE WHEN t.BidCommission      IS NULL THEN 1 ELSE 0 END), 0) AS c05,
                   ISNULL(SUM(CASE WHEN t.OfferTrader        IS NULL THEN 1 ELSE 0 END), 0) AS c06,
                   ISNULL(SUM(CASE WHEN t.OfferLegalName     IS NULL THEN 1 ELSE 0 END), 0) AS c07,
                   ISNULL(SUM(CASE WHEN t.OfferAddress       IS NULL THEN 1 ELSE 0 END), 0) AS c08,
                   ISNULL(SUM(CASE WHEN t.OfferCommission    IS NULL THEN 1 ELSE 0 END), 0) AS c09,
                   ISNULL(SUM(CASE WHEN t.SettlementCurrency IS NULL THEN 1 ELSE 0 END), 0) AS c10,
                   ISNULL(SUM(CASE WHEN t.ContractTerms      IS NULL THEN 1 ELSE 0 END), 0) AS c11,
                   ISNULL(SUM(CASE WHEN t.GTandC             IS NULL THEN 1 ELSE 0 END), 0) AS c12,
                   ISNULL(SUM(CASE WHEN t.Notes              IS NULL THEN 1 ELSE 0 END), 0) AS c13,
                   ISNULL(SUM(CASE WHEN t.ClickAndTrade      IS NULL THEN 1 ELSE 0 END), 0) AS c14
            FROM arm.AllTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('Side',               a.c01),
                            ('BidTrader',          a.c02),
                            ('BidLegalName',       a.c03),
                            ('BidAddress',         a.c04),
                            ('BidCommission',      a.c05),
                            ('OfferTrader',        a.c06),
                            ('OfferLegalName',     a.c07),
                            ('OfferAddress',       a.c08),
                            ('OfferCommission',    a.c09),
                            ('SettlementCurrency', a.c10),
                            ('ContractTerms',      a.c11),
                            ('GTandC',             a.c12),
                            ('Notes',              a.c13),
                            ('ClickAndTrade',      a.c14)
                    ) AS v(ColName, NullRows)

        -- ================= 10) MyTradesAttributionRate =======================
        -- Population rate of the same 14 columns on arm.MyTrades. Baselines:
        -- 37/37 for most, but BidCommission 16/37, OfferCommission 21/37 and
        -- Notes 3/37.
        -- *** do NOT set ExpectedCount here, and do NOT require both commissions
        -- on a row: 16 + 21 = 37, they are COMPLEMENTARY (a commission is
        -- populated on the company's OWN side only). *** A "commission always
        -- present" or "both sides present" rule must never ship.
        UNION ALL
        SELECT
            CAST('MyTradesAttributionRate' AS VARCHAR(48)),
            CAST(CONCAT('MyTrades.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.PopulatedRows AS BIGINT),
            CAST(CONCAT('rowsInWindow=', a.RowsInWindow,
                        '; baselines 37/37 for most, BidCommission 16/37 + OfferCommission 21/37 are COMPLEMENTARY, Notes 3/37')
                 AS NVARCHAR(400))
        FROM (
            SELECT COUNT(1) AS RowsInWindow,
                   ISNULL(SUM(CASE WHEN t.Side               IS NOT NULL THEN 1 ELSE 0 END), 0) AS c01,
                   ISNULL(SUM(CASE WHEN t.BidTrader          IS NOT NULL THEN 1 ELSE 0 END), 0) AS c02,
                   ISNULL(SUM(CASE WHEN t.BidLegalName       IS NOT NULL THEN 1 ELSE 0 END), 0) AS c03,
                   ISNULL(SUM(CASE WHEN t.BidAddress         IS NOT NULL THEN 1 ELSE 0 END), 0) AS c04,
                   ISNULL(SUM(CASE WHEN t.BidCommission      IS NOT NULL THEN 1 ELSE 0 END), 0) AS c05,
                   ISNULL(SUM(CASE WHEN t.OfferTrader        IS NOT NULL THEN 1 ELSE 0 END), 0) AS c06,
                   ISNULL(SUM(CASE WHEN t.OfferLegalName     IS NOT NULL THEN 1 ELSE 0 END), 0) AS c07,
                   ISNULL(SUM(CASE WHEN t.OfferAddress       IS NOT NULL THEN 1 ELSE 0 END), 0) AS c08,
                   ISNULL(SUM(CASE WHEN t.OfferCommission    IS NOT NULL THEN 1 ELSE 0 END), 0) AS c09,
                   ISNULL(SUM(CASE WHEN t.SettlementCurrency IS NOT NULL THEN 1 ELSE 0 END), 0) AS c10,
                   ISNULL(SUM(CASE WHEN t.ContractTerms      IS NOT NULL THEN 1 ELSE 0 END), 0) AS c11,
                   ISNULL(SUM(CASE WHEN t.GTandC             IS NOT NULL THEN 1 ELSE 0 END), 0) AS c12,
                   ISNULL(SUM(CASE WHEN t.Notes              IS NOT NULL THEN 1 ELSE 0 END), 0) AS c13,
                   ISNULL(SUM(CASE WHEN t.ClickAndTrade      IS NOT NULL THEN 1 ELSE 0 END), 0) AS c14
            FROM arm.MyTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('Side',               a.c01),
                            ('BidTrader',          a.c02),
                            ('BidLegalName',       a.c03),
                            ('BidAddress',         a.c04),
                            ('BidCommission',      a.c05),
                            ('OfferTrader',        a.c06),
                            ('OfferLegalName',     a.c07),
                            ('OfferAddress',       a.c08),
                            ('OfferCommission',    a.c09),
                            ('SettlementCurrency', a.c10),
                            ('ContractTerms',      a.c11),
                            ('GTandC',             a.c12),
                            ('Notes',              a.c13),
                            ('ClickAndTrade',      a.c14)
                    ) AS v(ColName, PopulatedRows)

        -- ================= 11) ClearingIdPopulated ===========================
        -- *** INFORMATIONAL ONLY. ClearingID has NEVER been populated by EITHER
        -- endpoint (0/98 allTrades AND 0/37 myTrades) - it is NOT an anonymised
        -- column, it is expected-NULL EVERYWHERE. MUST NOT be asserted populated
        -- anywhere: a rule expecting a value in arm.MyTrades would fail EVERY
        -- row. *** A first non-zero value here is a DISCOVERY (re-check the
        -- column's real format and width, which are unknown - VARCHAR(50) is the
        -- user's allocation, not an observed maximum), never a failure.
        UNION ALL
        SELECT
            CAST('ClearingIdPopulated' AS VARCHAR(48)),
            CAST('AllTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.ClearingID IS NOT NULL AND LEN(t.ClearingID) > 0
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsInWindow=', COUNT(1),
                        '; baseline 0 on BOTH endpoints - do NOT assert populated anywhere')
                 AS NVARCHAR(400))
        FROM arm.AllTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        UNION ALL
        SELECT
            CAST('ClearingIdPopulated' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.ClearingID IS NOT NULL AND LEN(t.ClearingID) > 0
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsInWindow=', COUNT(1),
                        '; baseline 0 - a first non-zero value is a DISCOVERY (unknown format/width), never a failure')
                 AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        -- ================= 12) FinancialBlankInvariant =======================
        -- Reported BOTH WAYS: (a) ProductType='Financial' rows that nonetheless
        -- carry a Location / PipelineTerminal / PriceBasis, and (b) rows with all
        -- three NULL that are NOT 'Financial'. Verified 0/0 on allTrades (19 <=> 19,
        -- all also 'contracts/month').
        -- *** do NOT set ExpectedCount = 0 here: myTrades contained NO Financial
        -- rows at all (37/37 Physical), so the invariant is UNVERIFIED there. ***
        -- The blanks are SEMANTIC - a financial contract has no delivery location -
        -- so a "Location must be populated" rule would false-positive on ~19% of
        -- the tape.
        UNION ALL
        SELECT
            CAST('FinancialBlankInvariant' AS VARCHAR(48)),
            CAST(CONCAT('AllTrades.', v.Direction) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.Cnt AS BIGINT),
            CAST('INFORMATIONAL - verified 0/0 on allTrades (19 Financial <=> 19 all-blank); UNVERIFIED on myTrades, which had no Financial rows'
                 AS NVARCHAR(400))
        FROM (
            SELECT
                ISNULL(SUM(CASE WHEN t.ProductType = 'Financial'
                                 AND (t.Location IS NOT NULL OR t.PipelineTerminal IS NOT NULL
                                      OR t.PriceBasis IS NOT NULL)
                                THEN 1 ELSE 0 END), 0) AS FinancialWithDelivery,
                ISNULL(SUM(CASE WHEN t.Location IS NULL AND t.PipelineTerminal IS NULL
                                 AND t.PriceBasis IS NULL
                                 AND ISNULL(t.ProductType, '') <> 'Financial'
                                THEN 1 ELSE 0 END), 0) AS AllBlankNotFinancial
            FROM arm.AllTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('FinancialWithDelivery', a.FinancialWithDelivery),
                            ('AllBlankNotFinancial',  a.AllBlankNotFinancial)
                    ) AS v(Direction, Cnt)

        UNION ALL
        SELECT
            CAST('FinancialBlankInvariant' AS VARCHAR(48)),
            CAST(CONCAT('MyTrades.', v.Direction) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.Cnt AS BIGINT),
            CAST('INFORMATIONAL - UNVERIFIED on this table: myTrades carried no Financial rows (37/37 Physical)'
                 AS NVARCHAR(400))
        FROM (
            SELECT
                ISNULL(SUM(CASE WHEN t.ProductType = 'Financial'
                                 AND (t.Location IS NOT NULL OR t.PipelineTerminal IS NOT NULL
                                      OR t.PriceBasis IS NOT NULL)
                                THEN 1 ELSE 0 END), 0) AS FinancialWithDelivery,
                ISNULL(SUM(CASE WHEN t.Location IS NULL AND t.PipelineTerminal IS NULL
                                 AND t.PriceBasis IS NULL
                                 AND ISNULL(t.ProductType, '') <> 'Financial'
                                THEN 1 ELSE 0 END), 0) AS AllBlankNotFinancial
            FROM arm.MyTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('FinancialWithDelivery', a.FinancialWithDelivery),
                            ('AllBlankNotFinancial',  a.AllBlankNotFinancial)
                    ) AS v(Direction, Cnt)

        -- ================= 13) SpreadGroupIntegrity ==========================
        -- *** INFORMATIONAL ONLY, NEVER AN ERROR. *** OrphanSpreadRef = populated
        -- SpreadTradeNumber values with no 'Spread' parent row in the same table;
        -- GroupSizeNot3 = groups whose member count is not exactly 3 (parent +
        -- First Leg + Second Leg). Both can be non-zero LEGITIMATELY, because a
        -- group can STRADDLE the window boundary (each leg has its own
        -- LastUpdated) - hence report, never enforce.
        -- *** DOWNSTREAM WARNING: summing Volume over a trades table TRIPLE-COUNTS
        -- spread volume (the parent's Volume is not additive with its legs'), and
        -- the parent's Price is a DIFFERENTIAL while the legs carry outright
        -- prices. ***
        UNION ALL
        SELECT
            CAST('SpreadGroupIntegrity' AS VARCHAR(48)),
            CAST(CONCAT(sp.TableName, '.', sp.Measure) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            sp.Cnt,
            CAST('INFORMATIONAL ONLY - a spread group can legitimately straddle the window. WARNING: SUM(Volume) TRIPLE-COUNTS spread volume; a Spread parent Price is a DIFFERENTIAL'
                 AS NVARCHAR(400))
        FROM @Spread AS sp

        -- ================= 14) SettlementDateCoverage ========================
        -- *** INFORMATIONAL ONLY, NEVER AN ERROR. *** Settlements publish on
        -- BUSINESS DAYS ONLY and the CURRENT day's curve is routinely not yet
        -- published when the loader runs, so weekend/holiday/today gaps are
        -- EXPECTED. Business days are ~0.7 of calendar days. Because the resume key
        -- is hot-only, a missing day is re-probed automatically an hour later.
        UNION ALL
        SELECT
            CAST('SettlementDateCoverage' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(DISTINCT s.SettlementDate) AS BIGINT),
            CAST(CONCAT('windowDays=', @WindowDays,
                        ' distinctSettlementDates=', COUNT(DISTINCT s.SettlementDate),
                        ' (~0.7 x calendar days expected).',
                        ' INFORMATIONAL ONLY - weekend gaps and an unpublished current day are NORMAL')
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo

        -- ================= 15) SettlementRowsPerDateBand =====================
        -- Count of settlement dates whose row count falls outside +/-20% of the
        -- window's MEDIAN rows-per-date. *** A BAND, NEVER AN EQUALITY *** - the
        -- grid shifts day to day (720 vs 723 on consecutive live dates) and the
        -- curve depth grows as products are added or terms extended. Flags a JUMP,
        -- not the level. Informational: ExpectedCount stays NULL because a genuine
        -- product addition is not a defect.
        UNION ALL
        SELECT
            CAST('SettlementRowsPerDateBand' AS VARCHAR(48)),
            CAST('Settlements' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(@OutOfBandDates AS BIGINT),
            CAST(CASE WHEN @MedianRowsPerDate IS NULL
                      THEN 'no settlement rows in the window - band check skipped (a weekend-only window is NORMAL)'
                      ELSE CONCAT('medianRowsPerDate=', CAST(CAST(@MedianRowsPerDate AS DECIMAL(12,1)) AS VARCHAR(20)),
                                  ' band=[', CAST(CAST(@MedianRowsPerDate * 0.8 AS DECIMAL(12,1)) AS VARCHAR(20)),
                                  '..', CAST(CAST(@MedianRowsPerDate * 1.2 AS DECIMAL(12,1)) AS VARCHAR(20)),
                                  ']; a BAND, never an equality - re-measure before widening any window')
                 END AS NVARCHAR(400))

        -- ================= 16) LastUpdatedOutsideWindow ======================
        -- Rows THIS RUN WROTE whose LastUpdated falls outside [@DateFrom, @DateTo].
        -- Scoped by FRESHNESS, not by the window - the window predicate IS
        -- LastUpdated, so a window-scoped version would read 0 by construction.
        -- *** do NOT set ExpectedCount = 0 here: the payload clock is UNVERIFIED
        -- (no timezone is stated anywhere) while our window is UTC, so a boundary
        -- row can legitimately fall a few hours out. *** Live-verified 0 of 98 on a
        -- literal reading, but a strict-zero assertion would false-positive at
        -- every window boundary.
        UNION ALL
        SELECT
            CAST('LastUpdatedOutsideWindow' AS VARCHAR(48)),
            CAST('AllTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.LastUpdated IS NULL
                                   OR t.LastUpdated < @TradesFrom
                                   OR t.LastUpdated >= @TradesToExcl
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsWrittenThisRun=', COUNT(1), '; ', @FreshNote,
                        '; INFORMATIONAL - payload timezone is UNVERIFIED, window is UTC')
                 AS NVARCHAR(400))
        FROM arm.AllTrades AS t
        WHERE (@Since IS NULL OR t.ModifiedAtUtc >= @Since)

        UNION ALL
        SELECT
            CAST('LastUpdatedOutsideWindow' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.LastUpdated IS NULL
                                   OR t.LastUpdated < @TradesFrom
                                   OR t.LastUpdated >= @TradesToExcl
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsWrittenThisRun=', COUNT(1), '; ', @FreshNote,
                        '; INFORMATIONAL - payload timezone is UNVERIFIED, window is UTC')
                 AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE (@Since IS NULL OR t.ModifiedAtUtc >= @Since)

        -- ================= 17) ExecutedOutsideWindow =========================
        -- Rows IN the window (by LastUpdated) whose Executed timestamp falls
        -- OUTSIDE it. *** A NON-ZERO COUNT IS HEALTHY HERE - this IS the revision
        -- signal. *** 5 of 98 live rows qualified, including three legs executed
        -- 2026-08-06 and revised 18 days later. A ZERO on a long-running deployment
        -- would suggest the hourly re-pull has STOPPED surfacing revisions, which
        -- is the failure mode worth watching for.
        UNION ALL
        SELECT
            CAST('ExecutedOutsideWindow' AS VARCHAR(48)),
            CAST('AllTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.Executed IS NOT NULL
                                  AND (t.Executed < @TradesFrom OR t.Executed >= @TradesToExcl)
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsInWindow=', COUNT(1),
                        '; a NON-ZERO count is HEALTHY - it is the revision signal (5/98 live). Executed is NOT the window predicate')
                 AS NVARCHAR(400))
        FROM arm.AllTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        UNION ALL
        SELECT
            CAST('ExecutedOutsideWindow' AS VARCHAR(48)),
            CAST('MyTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(SUM(CASE WHEN t.Executed IS NOT NULL
                                  AND (t.Executed < @TradesFrom OR t.Executed >= @TradesToExcl)
                                 THEN 1 ELSE 0 END), 0) AS BIGINT),
            CAST(CONCAT('rowsInWindow=', COUNT(1),
                        '; a NON-ZERO count is HEALTHY - it is the revision signal')
                 AS NVARCHAR(400))
        FROM arm.MyTrades AS t
        WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl

        -- ================= 18) TradeNumberOverlapAcrossTables ================
        -- TradeNumbers present in BOTH arm.AllTrades and arm.MyTrades.
        -- *** INFORMATIONAL ONLY, NEVER AN ERROR - expected NON-ZERO and
        -- INTENDED. *** The two tables are two views of the venue at different
        -- disclosure levels; there is no FK and no cross-table dedup by design.
        UNION ALL
        SELECT
            CAST('TradeNumberOverlapAcrossTables' AS VARCHAR(48)),
            CAST('AllTrades INTERSECT MyTrades' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('INFORMATIONAL ONLY, NEVER an error - expected non-zero and intended (no FK, no cross-table dedup)'
                 AS NVARCHAR(400))
        FROM (
            SELECT a.TradeNumber
            FROM arm.AllTrades AS a
            WHERE EXISTS (SELECT 1 FROM arm.MyTrades AS m WHERE m.TradeNumber = a.TradeNumber)
        ) AS ov

        -- ================= 19) UnknownEnumValues =============================
        -- A summary row plus one row per unexpected value.
        -- *** REPORT a new value, NEVER FAIL on it. *** No vendor document
        -- enumerates State / TradeType / ProductType / UnitOfMeasure / Side /
        -- SettlementCurrency / settlements PriceBasis, which is exactly why there
        -- is no lookup table and no CHECK constraint on any of them. A CAD
        -- settlement or a third product type MUST load.
        UNION ALL
        SELECT
            CAST('UnknownEnumValues' AS VARCHAR(48)),
            CAST('(summary)' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(@UnknownEnumCount AS BIGINT),
            CAST('distinct (table, column, value) combinations outside the 2026-08-24 observed sets. REPORT, never fail - the observed sets are SNAPSHOTS, not enums'
                 AS NVARCHAR(400))

        UNION ALL
        SELECT
            CAST('UnknownEnumValues' AS VARCHAR(48)),
            CAST(CONCAT(u.TableName, '.', u.ColName, '=', LEFT(u.Val, 120)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            u.Rws,
            CAST('a value outside the observed set - REPORT it, never fail on it; re-check the column width if the value is long'
                 AS NVARCHAR(400))
        FROM @UnknownEnum AS u

        -- ================= 20) NumericHeadroom ===============================
        -- MAX(ABS(value)) against the DECIMAL(9,2) ceiling 9,999,999.99.
        -- *** An over-range value is a HARD ARITHMETIC OVERFLOW (msg 8115) that
        -- fails the ENTIRE batch - not a truncation. *** Volume's observed max is
        -- 300,000, i.e. only ~33x headroom (decision D8 keeps DECIMAL(9,2) as the
        -- user specified; DECIMAL(13,2) was recommended). Detail flags any column
        -- above 50% of ceiling.
        -- *** do NOT add a non-negative price check anywhere: negative prices are
        -- DIFFERENTIALS and are 74% of settlements. *** ABS() here is about
        -- MAGNITUDE, not sign.
        UNION ALL
        SELECT
            CAST('NumericHeadroom' AS VARCHAR(48)),
            CAST(CONCAT('AllTrades.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.MaxAbs AS BIGINT),
            CAST(CONCAT('maxAbs=', CAST(v.MaxAbs AS VARCHAR(24)),
                        ' ceiling=9999999.99 pctOfCeiling=',
                        CAST(CAST(100.0 * v.MaxAbs / @Ceiling AS DECIMAL(6,2)) AS VARCHAR(12)),
                        CASE WHEN v.MaxAbs > @Ceiling / 2 THEN ' *** ABOVE 50% OF CEILING - WIDEN THE COLUMN ***' ELSE '' END)
                 AS NVARCHAR(400))
        FROM (
            SELECT ISNULL(MAX(ABS(t.Volume)),          0) AS mVolume,
                   ISNULL(MAX(ABS(t.Price)),           0) AS mPrice,
                   ISNULL(MAX(ABS(t.BidCommission)),   0) AS mBidComm,
                   ISNULL(MAX(ABS(t.OfferCommission)), 0) AS mOfferComm
            FROM arm.AllTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('Volume',          a.mVolume),
                            ('Price',           a.mPrice),
                            ('BidCommission',   a.mBidComm),
                            ('OfferCommission', a.mOfferComm)
                    ) AS v(ColName, MaxAbs)

        UNION ALL
        SELECT
            CAST('NumericHeadroom' AS VARCHAR(48)),
            CAST(CONCAT('MyTrades.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.MaxAbs AS BIGINT),
            CAST(CONCAT('maxAbs=', CAST(v.MaxAbs AS VARCHAR(24)),
                        ' ceiling=9999999.99 pctOfCeiling=',
                        CAST(CAST(100.0 * v.MaxAbs / @Ceiling AS DECIMAL(6,2)) AS VARCHAR(12)),
                        CASE WHEN v.MaxAbs > @Ceiling / 2 THEN ' *** ABOVE 50% OF CEILING - WIDEN THE COLUMN ***' ELSE '' END)
                 AS NVARCHAR(400))
        FROM (
            SELECT ISNULL(MAX(ABS(t.Volume)),          0) AS mVolume,
                   ISNULL(MAX(ABS(t.Price)),           0) AS mPrice,
                   ISNULL(MAX(ABS(t.BidCommission)),   0) AS mBidComm,
                   ISNULL(MAX(ABS(t.OfferCommission)), 0) AS mOfferComm
            FROM arm.MyTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('Volume',          a.mVolume),
                            ('Price',           a.mPrice),
                            ('BidCommission',   a.mBidComm),
                            ('OfferCommission', a.mOfferComm)
                    ) AS v(ColName, MaxAbs)

        UNION ALL
        SELECT
            CAST('NumericHeadroom' AS VARCHAR(48)),
            CAST('Settlements.Price' AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(ISNULL(MAX(ABS(s.Price)), 0) AS BIGINT),
            CAST(CONCAT('maxAbs=', CAST(ISNULL(MAX(ABS(s.Price)), 0) AS VARCHAR(24)),
                        ' ceiling=9999999.99; 74% of settlement prices are NEGATIVE (differentials) - no non-negative check, ever')
                 AS NVARCHAR(400))
        FROM arm.Settlements AS s
        WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo

        -- ================= 21) WidthHeadroom =================================
        -- MAX(LEN()) vs the declared width for the TIGHT columns. Baselines: trades
        -- Product 28/50 and Location 23/50 - and BOTH are '/'-joined on spread
        -- rows, so a three-leg product or a longer location pair could approach 50.
        -- Settlements Product 22/50, PieplineTerminal 21/50, PriceBasis 7/100,
        -- Term 6/50. An over-width value fails the whole batch with "String or
        -- binary data would be truncated", which is why the C# truncates a non-key
        -- value with a warning and DROPS an over-width KEY row.
        UNION ALL
        SELECT
            CAST('WidthHeadroom' AS VARCHAR(48)),
            CAST(CONCAT('AllTrades.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.MaxLen AS BIGINT),
            CAST(CONCAT('maxLen=', v.MaxLen, ' declared=', v.Declared,
                        CASE WHEN v.MaxLen > v.Declared * 8 / 10 THEN ' *** ABOVE 80% OF DECLARED WIDTH ***' ELSE '' END)
                 AS NVARCHAR(400))
        FROM (
            SELECT ISNULL(MAX(LEN(t.Product)),  0) AS mProduct,
                   ISNULL(MAX(LEN(t.Location)), 0) AS mLocation,
                   ISNULL(MAX(LEN(t.Term)),     0) AS mTerm,
                   ISNULL(MAX(LEN(t.PriceBasis)), 0) AS mPriceBasis
            FROM arm.AllTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('Product',    a.mProduct,    50),
                            ('Location',   a.mLocation,   50),
                            ('Term',       a.mTerm,       256),
                            ('PriceBasis', a.mPriceBasis, 256)
                    ) AS v(ColName, MaxLen, Declared)

        UNION ALL
        SELECT
            CAST('WidthHeadroom' AS VARCHAR(48)),
            CAST(CONCAT('MyTrades.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.MaxLen AS BIGINT),
            CAST(CONCAT('maxLen=', v.MaxLen, ' declared=', v.Declared,
                        CASE WHEN v.MaxLen > v.Declared * 8 / 10 THEN ' *** ABOVE 80% OF DECLARED WIDTH ***' ELSE '' END)
                 AS NVARCHAR(400))
        FROM (
            SELECT ISNULL(MAX(LEN(t.Product)),  0) AS mProduct,
                   ISNULL(MAX(LEN(t.Location)), 0) AS mLocation,
                   ISNULL(MAX(LEN(t.Term)),     0) AS mTerm,
                   ISNULL(MAX(LEN(t.PriceBasis)), 0) AS mPriceBasis
            FROM arm.MyTrades AS t
            WHERE t.LastUpdated >= @TradesFrom AND t.LastUpdated < @TradesToExcl
        ) AS a
        CROSS APPLY (VALUES ('Product',    a.mProduct,    50),
                            ('Location',   a.mLocation,   50),
                            ('Term',       a.mTerm,       256),
                            ('PriceBasis', a.mPriceBasis, 256)
                    ) AS v(ColName, MaxLen, Declared)

        UNION ALL
        SELECT
            CAST('WidthHeadroom' AS VARCHAR(48)),
            CAST(CONCAT('Settlements.', v.ColName) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(v.MaxLen AS BIGINT),
            CAST(CONCAT('maxLen=', v.MaxLen, ' declared=', v.Declared,
                        ' (KEY column: an over-width value DROPS the row rather than truncating a key)',
                        CASE WHEN v.MaxLen > v.Declared * 8 / 10 THEN ' *** ABOVE 80% OF DECLARED WIDTH ***' ELSE '' END)
                 AS NVARCHAR(400))
        FROM (
            SELECT ISNULL(MAX(LEN(s.Product)),          0) AS mProduct,
                   ISNULL(MAX(LEN(s.Location)),         0) AS mLocation,
                   ISNULL(MAX(LEN(s.PieplineTerminal)), 0) AS mPipeline,
                   ISNULL(MAX(LEN(s.PriceBasis)),       0) AS mPriceBasis,
                   ISNULL(MAX(LEN(s.Term)),             0) AS mTerm
            FROM arm.Settlements AS s
            WHERE s.SettlementDate BETWEEN @DateFrom AND @DateTo
        ) AS a
        CROSS APPLY (VALUES ('Product',          a.mProduct,    50),
                            ('Location',         a.mLocation,   50),
                            ('PieplineTerminal', a.mPipeline,   50),   -- [sic] - the user's spelling
                            ('PriceBasis',       a.mPriceBasis, 100),
                            ('Term',             a.mTerm,       50)
                    ) AS v(ColName, MaxLen, Declared)

        -- ================= 22) FileLogOutcomeSummary =========================
        -- Hub row counts per status over the window - lets an operator read
        -- "Success=3, NotAvailable=0, Failed=0" at a glance. NotAvailable is a
        -- LEGITIMATE EMPTY READ (header-only HTTP 200), never a fault.
        UNION ALL
        SELECT
            CAST('FileLogOutcomeSummary' AS VARCHAR(48)),
            CAST(CONCAT('Status=', st.[Name]) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('INFORMATIONAL - NotAvailable is a header-only HTTP 200, i.e. a LEGITIMATE EMPTY READ whose work unit SUCCEEDS'
                 AS NVARCHAR(400))
        FROM arm.FileLog AS f
        INNER JOIN arm.Status AS st ON st.Id = f.StatusId
        WHERE f.WindowEnd BETWEEN @DateFrom AND @DateTo
        GROUP BY st.[Name]

        -- ================= 23) FileLogHttpStatusSummary ======================
        -- Counts per HTTP status plus a SAMPLE of the stored response body.
        -- *** Because arm.FileLog is the ONLY provenance, this is how an operator
        -- distinguishes the FOUR different 400s after the fact: *** row cap
        -- ('more than the limit of'), history cap ('within the last six months'),
        -- a malformed date ('Invalid ...') and a bad legalEntityName. The row-cap
        -- and history-cap messages MASK ONE ANOTHER, so never infer one limit from
        -- the other's failure. ErrorMessage never contains a credential (auth is a
        -- header) and also carries non-fatal header-drift notes on Success rows.
        UNION ALL
        SELECT
            CAST('FileLogHttpStatusSummary' AS VARCHAR(48)),
            CAST(CONCAT('HttpStatus=', ISNULL(CAST(f.HttpStatus AS VARCHAR(10)), '(null)')) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('sample body/note: ', LEFT(ISNULL(MIN(f.ErrorMessage), N'(none)'), 240))
                 AS NVARCHAR(400))
        FROM arm.FileLog AS f
        WHERE f.WindowEnd BETWEEN @DateFrom AND @DateTo
        GROUP BY f.HttpStatus

        -- ================= 24) FileLogFailuresInWindow (EXPECT 0) ============
        -- The ONE hub check with a real expectation. *** This API has NO
        -- legitimate non-2xx status *** - emptiness is expressed INSIDE a 200 as a
        -- header-only body - so every Failed row is either our bug (a malformed
        -- date, an over-cap window, an inverted range) or a configuration failure
        -- (a wrong/revoked credential), and always merits attention. Contrast NGI,
        -- where a 404 is the normal case ~58 times out of 60.
        UNION ALL
        SELECT
            CAST('FileLogFailuresInWindow' AS VARCHAR(48)),
            CAST('arm.FileLog' AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(CONCAT('scope: WindowEnd in [', CONVERT(VARCHAR(10), @DateFrom, 23), '..',
                        CONVERT(VARCHAR(10), @DateTo, 23), '] AND ', @FreshNote,
                        '. There is NO legitimate non-2xx for this API - see check 23 for the body')
                 AS NVARCHAR(400))
        FROM arm.FileLog AS f
        INNER JOIN arm.Status AS st ON st.Id = f.StatusId
        WHERE st.[Name] = 'Failed'
          AND f.WindowEnd BETWEEN @DateFrom AND @DateTo
          AND (@Since IS NULL OR f.LastCheckedUtc >= @Since)
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName, Scope;
END
GO

-- =============================================================================
-- DELIBERATE ABSENCES - do not "add for parity with another loader".
--   * NO usp_Get... read proc. *** NOTHING reads a ModCom table back at run
--     time. *** All three endpoints are parameterised by DATES ALONE; there is no
--     discovery tier, no reference provider and no barrier, so a Settlements-only
--     or MyTrades-only run is fully valid. Do not add one "for parity with AGSI",
--     whose usp_GetGasStorageEntities exists only because its fact work units ARE
--     the discovered country list.
--   * NO FK anywhere: none between the three fact tables, and none from a fact to
--     arm.FileLog (the fact tables have no FileLogId at all).
--   * NO DELETE / WHEN NOT MATCHED BY SOURCE branch in any merge proc.
--   * NO checksum column and no checksum short-circuit (user decision U2).
--   * NO history table. A settlement price revision currently OVERWRITES; the
--     payload carries no revision/version/status field to key on. The OPIS
--     arm.LPReportHistory pattern is the documented remedy if that proves wrong.
--   * NO RAISERROR in arm.usp_ValidateLoad. It is OBSERVATIONAL: even invalid
--     arguments come back as a row in the single uniform result set, so the C#
--     validator logs a warning and the run continues.
-- =============================================================================
