-- =============================================================================
-- 003_CreateOilXProcedures.sql
-- Database : OilX
-- Schema   : arm
--
--   arm.usp_BulkMergeCargoTracking
--   arm.usp_BulkMergeFloatingStorage
--   arm.usp_BulkMergeFlow
--   arm.usp_BulkMergeGlobalBalance
--   arm.usp_BulkMergeOilFieldProduction
--   arm.usp_BulkMergeRegionalBalance
--   arm.usp_BulkMergeSupplyDemand
--   arm.usp_BulkMergeTerminal
--   arm.usp_ValidateLoad            post-load observational anomaly report
--
-- Design of record: docs/design/OilX.md (S4 ordering, S7 Checksum, S10 merges,
--                   S11 validation)
--
-- =============================================================================
-- SHARED CONVENTIONS ACROSS ALL EIGHT MERGE PROCS
-- =============================================================================
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY RunDate, RowId), keep row 1. It should never
--   fire: every feed's business key was verified unique against a live file
--   (sql/OilX/001 lists the counts), and one TVP batch always comes from ONE
--   file. The guard stays because MERGE does not degrade gracefully -- a single
--   duplicated source key aborts the whole batch with "attempted to update the
--   same row more than once", so a vendor that started repeating a row would take
--   the loader down rather than merely repeat itself.
--
--   ORDERING BETWEEN A DAY'S SNAPSHOTS IS NOT THIS PROC'S JOB. A day publishes
--   up to four snapshots, all carrying the SAME in-file RunDate, so they collide
--   on (RunDate, RowId) and the newest must win. That ordering is enforced by the
--   LOADER, which processes a day's files sequentially in ascending uploaded_at
--   and sends each as its own TVP batch (docs/design/OilX.md S4). There is
--   nothing in a batch for the proc to order.
--
--   NO DELETE-BY-ABSENCE. No merge has WHEN NOT MATCHED BY SOURCE THEN DELETE.
--   A batch carries 20,000 rows of one day; deleting rows absent from it would
--   wipe the rest of that day and every other day in the table. Vanished keys are
--   reported observationally by arm.usp_ValidateLoad, never deleted.
--
--   MATCHING ON THE PRIMARY KEY ONLY -- (RunDate, RowId). RowId is a
--   deterministic UUIDv5 of the feed's business key (OilXRowId.cs), so matching
--   the PK IS matching the business key -- as long as the derivation never
--   changes. If it ever does, rows written under the old derivation will NOT be
--   matched and will remain alongside the new ones; that needs a one-off cleanup,
--   and usp_ValidateLoad's OrphanPort / RunDateGap checks exist to make such a
--   fork visible rather than silent.
--
--   FileName IS PROVENANCE, NEVER A KEY. It is UPDATEd on match so it always
--   names the snapshot that last wrote the row, and it is deliberately EXCLUDED
--   from Checksum -- a later snapshot that changed nothing must not count as a
--   change.
--
--   CHECKSUM-GUARDED UPDATE:
--       WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> src.[Checksum])
--   so an unchanged row is NOT updated and its ModifiedAtUtc is NOT re-stamped.
--   That is the whole point of the Checksum column here, and it matters more in
--   this loader than anywhere else in the repo: four snapshots a day of a
--   396,866-row file means almost every row is byte-identical to the one before
--   it. Without the guard, ModifiedAtUtc would degrade into "time of last run"
--   across ~2M rows per day and answer nothing. With it, ModifiedAtUtc means
--   "when this row's values last actually changed".
--
--   *** CONSEQUENCE THE OPERATOR MUST KNOW: @@ROWCOUNT COUNTS CHANGED ROWS, NOT
--   ROWS SENT. *** Re-merging a snapshot where nothing moved reports
--   RecordsProcessed = 0 while having verified every row. THAT IS A HEALTHY
--   SUCCESS, not an empty read -- the same convention EvolutionMarkets uses.
--   Genuine emptiness is a 422 "No data for files" at the API and never reaches
--   a merge at all (docs/apis/OilX.md S2 Behaviour 4).
--
--   ModifiedAtUtc is set explicitly with SYSUTCDATETIME(), so rows written by
--   this loader hold true UTC even though the table DEFAULT is sysdatetime().
--
--   Each returns SELECT @@ROWCOUNT AS RecordsProcessed, which SqlSinkBase
--   surfaces to core.LoadLog.
--
-- All CREATE OR ALTER, so the script is re-runnable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeCargoTracking  ->  arm.CargoTracking
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeCargoTracking
    @Records arm.CargoTrackingTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.CargoTracking AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [IMO]                     = s.[IMO],
        [VesselName]              = s.[VesselName],
        [VesselClass]             = s.[VesselClass],
        [LoadDate]                = s.[LoadDate],
        [LoadArea]                = s.[LoadArea],
        [LoadCountry]             = s.[LoadCountry],
        [LoadSubcountryArea]      = s.[LoadSubcountryArea],
        [LoadPort]                = s.[LoadPort],
        [LoadSTSIndicator]        = s.[LoadSTSIndicator],
        [LoadSTSIMO]              = s.[LoadSTSIMO],
        [OriginCountry]           = s.[OriginCountry],
        [OriginCountryGroup]      = s.[OriginCountryGroup],
        [GradeName]               = s.[GradeName],
        [DischargeDate]           = s.[DischargeDate],
        [DischargeArea]           = s.[DischargeArea],
        [DischargeCountry]        = s.[DischargeCountry],
        [DischargeSubcountryArea] = s.[DischargeSubcountryArea],
        [DischargePort]           = s.[DischargePort],
        [DischargeSTSIndicator]   = s.[DischargeSTSIndicator],
        [DischargeSTSIMO]         = s.[DischargeSTSIMO],
        [DestinationCountry]      = s.[DestinationCountry],
        [DestinationCountryGroup] = s.[DestinationCountryGroup],
        [LoadQuantity_KBBL]       = s.[LoadQuantity_KBBL],
        [LoadQuantity_KT]         = s.[LoadQuantity_KT],
        [SulphurContent]          = s.[SulphurContent],
        [APIGravity]              = s.[APIGravity],
        [Charterer]               = s.[Charterer],
        [Supplier]                = s.[Supplier],
        [Buyer]                   = s.[Buyer],
        [LastUpdateDate]          = s.[LastUpdateDate],
        [FlowID]                  = s.[FlowID],
        [FileName]                = s.[FileName],
        [Checksum]                = s.[Checksum],
        [ModifiedAtUtc]           = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [IMO], [VesselName], [VesselClass], [LoadDate],
                [LoadArea], [LoadCountry], [LoadSubcountryArea], [LoadPort],
                [LoadSTSIndicator], [LoadSTSIMO], [OriginCountry], [OriginCountryGroup],
                [GradeName], [DischargeDate], [DischargeArea], [DischargeCountry],
                [DischargeSubcountryArea], [DischargePort], [DischargeSTSIndicator],
                [DischargeSTSIMO], [DestinationCountry], [DestinationCountryGroup],
                [LoadQuantity_KBBL], [LoadQuantity_KT], [SulphurContent], [APIGravity],
                [Charterer], [Supplier], [Buyer], [LastUpdateDate], [FlowID],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[IMO], s.[VesselName], s.[VesselClass], s.[LoadDate],
                s.[LoadArea], s.[LoadCountry], s.[LoadSubcountryArea], s.[LoadPort],
                s.[LoadSTSIndicator], s.[LoadSTSIMO], s.[OriginCountry], s.[OriginCountryGroup],
                s.[GradeName], s.[DischargeDate], s.[DischargeArea], s.[DischargeCountry],
                s.[DischargeSubcountryArea], s.[DischargePort], s.[DischargeSTSIndicator],
                s.[DischargeSTSIMO], s.[DestinationCountry], s.[DestinationCountryGroup],
                s.[LoadQuantity_KBBL], s.[LoadQuantity_KT], s.[SulphurContent], s.[APIGravity],
                s.[Charterer], s.[Supplier], s.[Buyer], s.[LastUpdateDate], s.[FlowID],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeFloatingStorage  ->  arm.FloatingStorage
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFloatingStorage
    @Records arm.FloatingStorageTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.FloatingStorage AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [IMO]                 = s.[IMO],
        [ReferenceDate]       = s.[ReferenceDate],
        [VesselName]          = s.[VesselName],
        [VesselClass]         = s.[VesselClass],
        [StartDate]           = s.[StartDate],
        [EndDate]             = s.[EndDate],
        [QuantityKiloBarrels] = s.[QuantityKiloBarrels],
        [Area]                = s.[Area],
        [FileName]            = s.[FileName],
        [Checksum]            = s.[Checksum],
        [ModifiedAtUtc]       = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [IMO], [ReferenceDate], [VesselName], [VesselClass],
                [StartDate], [EndDate], [QuantityKiloBarrels], [Area],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[IMO], s.[ReferenceDate], s.[VesselName], s.[VesselClass],
                s.[StartDate], s.[EndDate], s.[QuantityKiloBarrels], s.[Area],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeFlow  ->  arm.Flow
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFlow
    @Records arm.FlowTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.Flow AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [ReferenceDate]          = s.[ReferenceDate],
        [OriginCountryName]      = s.[OriginCountryName],
        [DestinationCountryName] = s.[DestinationCountryName],
        [GroupByDateIndicator]   = s.[GroupByDateIndicator],
        [OriginSubCountry]       = s.[OriginSubCountry],
        [DestinationSubCountry]  = s.[DestinationSubCountry],
        [GradeName]              = s.[GradeName],
        [GradeCategory]          = s.[GradeCategory],
        [ApiGravity]             = s.[ApiGravity],
        [SulphurContent]         = s.[SulphurContent],
        [QuantityKBD]            = s.[QuantityKBD],
        [QuantityKBBL]           = s.[QuantityKBBL],
        [FileName]               = s.[FileName],
        [Checksum]               = s.[Checksum],
        [ModifiedAtUtc]          = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [ReferenceDate], [OriginCountryName],
                [DestinationCountryName], [GroupByDateIndicator], [OriginSubCountry],
                [DestinationSubCountry], [GradeName], [GradeCategory], [ApiGravity],
                [SulphurContent], [QuantityKBD], [QuantityKBBL],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[ReferenceDate], s.[OriginCountryName],
                s.[DestinationCountryName], s.[GroupByDateIndicator], s.[OriginSubCountry],
                s.[DestinationSubCountry], s.[GradeName], s.[GradeCategory], s.[ApiGravity],
                s.[SulphurContent], s.[QuantityKBD], s.[QuantityKBBL],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeGlobalBalance  ->  arm.GlobalBalance
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeGlobalBalance
    @Records arm.GlobalBalanceTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.GlobalBalance AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [GroupName]     = s.[GroupName],
        [ReferenceDate] = s.[ReferenceDate],
        [FlowBreakdown] = s.[FlowBreakdown],
        [UnitMeasure]   = s.[UnitMeasure],
        [ObservedValue] = s.[ObservedValue],
        [FileName]      = s.[FileName],
        [Checksum]      = s.[Checksum],
        [ModifiedAtUtc] = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [GroupName], [ReferenceDate], [FlowBreakdown],
                [UnitMeasure], [ObservedValue], [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[GroupName], s.[ReferenceDate], s.[FlowBreakdown],
                s.[UnitMeasure], s.[ObservedValue], s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeOilFieldProduction  ->  arm.OilFieldProduction
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeOilFieldProduction
    @Records arm.OilFieldProductionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.OilFieldProduction AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [ReferenceDate] = s.[ReferenceDate],
        [OilFieldName]  = s.[OilFieldName],
        [UnitMeasure]   = s.[UnitMeasure],
        [PortName]      = s.[PortName],
        [CountryName]   = s.[CountryName],
        [ObservedValue] = s.[ObservedValue],
        [FileName]      = s.[FileName],
        [Checksum]      = s.[Checksum],
        [ModifiedAtUtc] = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [ReferenceDate], [OilFieldName], [UnitMeasure],
                [PortName], [CountryName], [ObservedValue],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[ReferenceDate], s.[OilFieldName], s.[UnitMeasure],
                s.[PortName], s.[CountryName], s.[ObservedValue],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeRegionalBalance  ->  arm.RegionalBalance
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeRegionalBalance
    @Records arm.RegionalBalanceTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.RegionalBalance AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [GroupName]         = s.[GroupName],
        [ReferenceDate]     = s.[ReferenceDate],
        [FlowBreakdown]     = s.[FlowBreakdown],
        [UnitMeasure]       = s.[UnitMeasure],
        [ObservedValue]     = s.[ObservedValue],
        [GeneralizedSource] = s.[GeneralizedSource],
        [FileName]          = s.[FileName],
        [Checksum]          = s.[Checksum],
        [ModifiedAtUtc]     = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [GroupName], [ReferenceDate], [FlowBreakdown],
                [UnitMeasure], [ObservedValue], [GeneralizedSource],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[GroupName], s.[ReferenceDate], s.[FlowBreakdown],
                s.[UnitMeasure], s.[ObservedValue], s.[GeneralizedSource],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeSupplyDemand  ->  arm.SupplyDemand
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeSupplyDemand
    @Records arm.SupplyDemandTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.SupplyDemand AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [CountryISOCode]    = s.[CountryISOCode],
        [ReferenceDate]     = s.[ReferenceDate],
        [FlowBreakdown]     = s.[FlowBreakdown],
        [CountryName]       = s.[CountryName],
        [UnitMeasure]       = s.[UnitMeasure],
        [ObservedValue]     = s.[ObservedValue],
        [GeneralizedSource] = s.[GeneralizedSource],
        [FileName]          = s.[FileName],
        [Checksum]          = s.[Checksum],
        [ModifiedAtUtc]     = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [CountryISOCode], [ReferenceDate], [FlowBreakdown],
                [CountryName], [UnitMeasure], [ObservedValue], [GeneralizedSource],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[CountryISOCode], s.[ReferenceDate], s.[FlowBreakdown],
                s.[CountryName], s.[UnitMeasure], s.[ObservedValue], s.[GeneralizedSource],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeTerminal  ->  arm.Terminal
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeTerminal
    @Records arm.TerminalTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY [RunDate], [RowId] ORDER BY (SELECT NULL)) AS rn
        FROM @Records
    )
    MERGE arm.Terminal AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.[RunDate] = s.[RunDate]
       AND tgt.[RowId]   = s.[RowId]

    WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> s.[Checksum]) THEN UPDATE SET
        [TerminalName]  = s.[TerminalName],
        [ReferenceDate] = s.[ReferenceDate],
        [FlowBreakdown] = s.[FlowBreakdown],
        [UnitMeasure]   = s.[UnitMeasure],
        [ObservedValue] = s.[ObservedValue],
        [CountryName]   = s.[CountryName],
        [FileName]      = s.[FileName],
        [Checksum]      = s.[Checksum],
        [ModifiedAtUtc] = SYSUTCDATETIME()

    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([RunDate], [RowId], [TerminalName], [ReferenceDate], [FlowBreakdown],
                [UnitMeasure], [ObservedValue], [CountryName],
                [FileName], [Checksum], [ModifiedAtUtc])
        VALUES (s.[RunDate], s.[RowId], s.[TerminalName], s.[ReferenceDate], s.[FlowBreakdown],
                s.[UnitMeasure], s.[ObservedValue], s.[CountryName],
                s.[FileName], s.[Checksum], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- =============================================================================
-- arm.usp_ValidateLoad
--
-- Post-load OBSERVATIONAL anomaly report. It never changes a run's outcome --
-- OilXLoadValidator logs what comes back and swallows its own failures. One
-- uniform result set: Check, TableName, Detail, RowCount.
--
--   @Days  how far back to look, in RunDates. NULL = the whole table.
--
-- The checks, and why each exists:
--
--   RunDateGap          a RunDate inside the window with NO rows for a feed.
--                       The API answers 422 "No data for files" for a day the
--                       vendor never published, which the loader records as a
--                       legitimate empty success -- so a gap is normal history,
--                       not necessarily a fault. It is reported so an operator
--                       can tell "vendor published nothing" from "our work unit
--                       silently did nothing".
--
--   SnapshotShrink      a RunDate whose row count is below 90% of the previous
--                       populated RunDate's. Each file is a FULL snapshot of all
--                       history, so day-to-day counts move by fractions of a
--                       percent; a real drop means a truncated download or a
--                       work unit that failed part-way through its batches.
--
--   OrphanPort          OilFieldProduction only. The RowId key includes PortName
--                       (sql/OilX/001 explains the trade-off), so a PortName the
--                       vendor later fills in on a previously-blank row produces
--                       a SECOND row rather than updating the first. This finds
--                       (field, date, unit) groups appearing under both a blank
--                       and a non-blank port on the same RunDate.
--
--   BlankKeyComponent   a key column that is present but EMPTY. Empty is a legal,
--                       stable identity here (Flow's subcountries are usually
--                       empty), so this is informational -- it exists so a feed
--                       that suddenly starts blanking a column it never blanked
--                       shows up before anyone wonders why a series forked.
--
--   StaleModified       ModifiedAtUtc earlier than RunDate. Should be impossible
--                       -- the merge stamps SYSUTCDATETIME() at write time and
--                       RunDate comes from a file published that day -- so a hit
--                       means a clock problem or a hand-edited row.
-- =============================================================================
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @Days INT = 35
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Since DATE = CASE WHEN @Days IS NULL THEN NULL
                               ELSE DATEADD(DAY, -ABS(@Days), CAST(SYSUTCDATETIME() AS DATE)) END;

    CREATE TABLE #Counts
    (
        TableName VARCHAR(50) NOT NULL,
        RunDate   DATE        NOT NULL,
        Rows_     BIGINT      NOT NULL
    );

    INSERT INTO #Counts (TableName, RunDate, Rows_)
    SELECT 'CargoTracking',      [RunDate], COUNT_BIG(*) FROM arm.CargoTracking
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'FloatingStorage',    [RunDate], COUNT_BIG(*) FROM arm.FloatingStorage
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'Flow',               [RunDate], COUNT_BIG(*) FROM arm.Flow
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'GlobalBalance',      [RunDate], COUNT_BIG(*) FROM arm.GlobalBalance
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'OilFieldProduction', [RunDate], COUNT_BIG(*) FROM arm.OilFieldProduction
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'RegionalBalance',    [RunDate], COUNT_BIG(*) FROM arm.RegionalBalance
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'SupplyDemand',       [RunDate], COUNT_BIG(*) FROM arm.SupplyDemand
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate]
    UNION ALL
    SELECT 'Terminal',           [RunDate], COUNT_BIG(*) FROM arm.Terminal
        WHERE @Since IS NULL OR [RunDate] >= @Since GROUP BY [RunDate];

    -- ---- RunDateGap -------------------------------------------------------
    ;WITH tables_ AS (SELECT DISTINCT TableName FROM #Counts),
          days_   AS (SELECT DISTINCT RunDate   FROM #Counts)
    SELECT 'RunDateGap' AS [Check],
           t.TableName,
           CONVERT(VARCHAR(10), d.RunDate, 23) AS Detail,
           CAST(0 AS BIGINT) AS [RowCount]
    FROM tables_ t
    CROSS JOIN days_ d
    LEFT JOIN #Counts c ON c.TableName = t.TableName AND c.RunDate = d.RunDate
    WHERE c.TableName IS NULL

    UNION ALL

    -- ---- SnapshotShrink ---------------------------------------------------
    SELECT 'SnapshotShrink',
           x.TableName,
           CONVERT(VARCHAR(10), x.RunDate, 23)
               + ' has ' + CAST(x.Rows_ AS VARCHAR(20))
               + ' row(s) vs ' + CAST(x.PrevRows AS VARCHAR(20))
               + ' on ' + CONVERT(VARCHAR(10), x.PrevRunDate, 23),
           x.Rows_
    FROM (
        SELECT TableName, RunDate, Rows_,
               LAG(Rows_)   OVER (PARTITION BY TableName ORDER BY RunDate) AS PrevRows,
               LAG(RunDate) OVER (PARTITION BY TableName ORDER BY RunDate) AS PrevRunDate
        FROM #Counts
    ) x
    WHERE x.PrevRows IS NOT NULL
      AND x.PrevRows > 0
      AND x.Rows_ < x.PrevRows * 0.90

    UNION ALL

    -- ---- OrphanPort -------------------------------------------------------
    SELECT 'OrphanPort',
           'OilFieldProduction',
           CONVERT(VARCHAR(10), o.RunDate, 23) + ' ' + o.OilFieldName,
           COUNT_BIG(*)
    FROM arm.OilFieldProduction o
    WHERE (@Since IS NULL OR o.[RunDate] >= @Since)
    GROUP BY o.RunDate, o.OilFieldName, o.ReferenceDate, o.UnitMeasure
    HAVING COUNT(DISTINCT CASE WHEN o.PortName IS NULL OR o.PortName = '' THEN 0 ELSE 1 END) > 1

    UNION ALL

    -- ---- BlankKeyComponent ------------------------------------------------
    SELECT 'BlankKeyComponent', 'CargoTracking', 'FlowID is blank', COUNT_BIG(*)
    FROM arm.CargoTracking
    WHERE (@Since IS NULL OR [RunDate] >= @Since) AND ([FlowID] IS NULL OR [FlowID] = '')
    HAVING COUNT_BIG(*) > 0

    UNION ALL

    SELECT 'BlankKeyComponent', 'FloatingStorage', 'IMO is blank', COUNT_BIG(*)
    FROM arm.FloatingStorage
    WHERE (@Since IS NULL OR [RunDate] >= @Since) AND ([IMO] IS NULL OR [IMO] = '')
    HAVING COUNT_BIG(*) > 0

    UNION ALL

    SELECT 'BlankKeyComponent', 'SupplyDemand', 'CountryISOCode is blank', COUNT_BIG(*)
    FROM arm.SupplyDemand
    WHERE (@Since IS NULL OR [RunDate] >= @Since)
      AND ([CountryISOCode] IS NULL OR [CountryISOCode] = '')
    HAVING COUNT_BIG(*) > 0

    UNION ALL

    SELECT 'BlankKeyComponent', 'Terminal', 'TerminalName is blank', COUNT_BIG(*)
    FROM arm.Terminal
    WHERE (@Since IS NULL OR [RunDate] >= @Since)
      AND ([TerminalName] IS NULL OR [TerminalName] = '')
    HAVING COUNT_BIG(*) > 0

    UNION ALL

    -- ---- StaleModified ----------------------------------------------------
    SELECT 'StaleModified', 'CargoTracking', 'ModifiedAtUtc precedes RunDate', COUNT_BIG(*)
    FROM arm.CargoTracking
    WHERE (@Since IS NULL OR [RunDate] >= @Since)
      AND [ModifiedAtUtc] IS NOT NULL AND CAST([ModifiedAtUtc] AS DATE) < [RunDate]
    HAVING COUNT_BIG(*) > 0

    UNION ALL

    SELECT 'StaleModified', 'Terminal', 'ModifiedAtUtc precedes RunDate', COUNT_BIG(*)
    FROM arm.Terminal
    WHERE (@Since IS NULL OR [RunDate] >= @Since)
      AND [ModifiedAtUtc] IS NOT NULL AND CAST([ModifiedAtUtc] AS DATE) < [RunDate]
    HAVING COUNT_BIG(*) > 0

    ORDER BY [Check], TableName, Detail;

    DROP TABLE #Counts;
END
GO
