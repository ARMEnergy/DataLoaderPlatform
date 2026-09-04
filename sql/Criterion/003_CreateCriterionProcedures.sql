-- =============================================================================
-- 003_CreateCriterionProcedures.sql
-- Database : Criterion
-- Schema   : arm
--
--   arm.usp_BulkMerge<Table>   x9, one per target table
--   arm.usp_ValidateLoad       post-load observational anomaly report
--
-- Shared conventions across every merge proc:
--
--   BATCH DE-DUP. Each merge de-duplicates its source with
--   ROW_NUMBER() OVER (PARTITION BY <key>), keep row 1. SQL MERGE raises
--   "attempted to update the same row more than once" on a duplicated source
--   key, and this source genuinely ships duplicates:
--     * pipelines.nomination_points -- 118 exact duplicate rows on the full
--       four-column key in a 30-day window, identical payloads.
--     * data_series.financial_json  -- 2,320 duplicate UUIDs in 30 days (the
--       reason the loader reads financial_json_LATEST instead; the guard stays
--       because it costs nothing and the source could regress).
--     * the JSON unpivot for Financial_SeriesData -- an intraday array holds up
--       to 288 observations per calendar day and the PK admits one, so the
--       collapse happens HERE, deterministically, rather than by whichever row
--       MERGE happened to see last. See usp_BulkMergeFinancialSeriesData.
--
--   NO DELETE-BY-ABSENCE. None of the merges has WHEN NOT MATCHED BY SOURCE
--   THEN DELETE. Each work unit carries one slice (one gas day, one post date,
--   one page of series); deleting rows absent from a slice would wipe every
--   other slice's rows.
--
--   COLUMNS WITH NO SOURCE ARE NEVER WRITTEN. arm.Financial_Metadata.Enabled,
--   arm.Pipelines_NominationPoint.IsLatest and arm.Pipelines_Metadata.MappingId
--   appear in no INSERT list and no UPDATE SET, so an ARM-curated value
--   survives every reload. This is a correctness requirement, not a style
--   choice -- see 001 and 002.
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
-- arm.usp_BulkMergeFinancialMetadata -> arm.Financial_Metadata
--
-- Full snapshot of the series catalog (2,265 rows). Enabled is deliberately
-- absent from both column lists.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFinancialMetadata
    @Records arm.FinancialMetadataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY MetadataId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Financial_Metadata AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.MetadataId = s.MetadataId
    WHEN MATCHED THEN UPDATE SET
        EntityName    = s.EntityName,
        MetadataDesc  = s.MetadataDesc,
        CmdtyClass    = s.CmdtyClass,
        SubCmdtyDesc  = s.SubCmdtyDesc,
        AssetName     = s.AssetName,
        RegionName    = s.RegionName,
        CountryName   = s.CountryName,
        StateName     = s.StateName,
        ProvinceName  = s.ProvinceName,
        MongoId       = s.MongoId,
        [Status]      = s.[Status],
        SeriesDesc    = s.SeriesDesc,
        SeriesId      = s.SeriesId,
        TableName     = s.TableName,
        EntityId      = s.EntityId,
        SeriesType    = s.SeriesType,
        SubRegion     = s.SubRegion,
        Ticker        = s.Ticker,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MetadataId, EntityName, MetadataDesc, CmdtyClass, SubCmdtyDesc, AssetName,
                RegionName, CountryName, StateName, ProvinceName, MongoId, [Status],
                SeriesDesc, SeriesId, TableName, EntityId, SeriesType, SubRegion, Ticker,
                ModifiedAtUtc)
        VALUES (s.MetadataId, s.EntityName, s.MetadataDesc, s.CmdtyClass, s.SubCmdtyDesc, s.AssetName,
                s.RegionName, s.CountryName, s.StateName, s.ProvinceName, s.MongoId, s.[Status],
                s.SeriesDesc, s.SeriesId, s.TableName, s.EntityId, s.SeriesType, s.SubRegion, s.Ticker,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeFinancialSeries -> arm.Financial_Series
--
-- One row per publication. When the same FinancialJsonId appears twice in a
-- batch the HIGHEST Version wins (then the latest LoadDate) rather than an
-- arbitrary row -- financial_json_latest should never send a duplicate, but if
-- the loader is ever repointed at financial_json this ordering is what makes
-- the result correct instead of merely non-erroring.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFinancialSeries
    @Records arm.FinancialSeriesTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY FinancialJsonId
                    ORDER BY [Version] DESC, LoadDate DESC) AS rn
        FROM @Records
    )
    MERGE arm.Financial_Series AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.FinancialJsonId = s.FinancialJsonId
    WHEN MATCHED THEN UPDATE SET
        MetadataId       = s.MetadataId,
        PostDate         = s.PostDate,
        ForecastDate     = s.ForecastDate,
        LoadDate         = s.LoadDate,
        Filename         = s.Filename,
        [Version]        = s.[Version],
        PeriodId         = s.PeriodId,
        UnitId           = s.UnitId,
        Active           = s.Active,
        ForecastDateTime = s.ForecastDateTime,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FinancialJsonId, MetadataId, PostDate, ForecastDate, LoadDate, Filename,
                [Version], PeriodId, UnitId, Active, ForecastDateTime, ModifiedAtUtc)
        VALUES (s.FinancialJsonId, s.MetadataId, s.PostDate, s.ForecastDate, s.LoadDate, s.Filename,
                s.[Version], s.PeriodId, s.UnitId, s.Active, s.ForecastDateTime, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeFinancialSeriesData -> arm.Financial_SeriesData
--
-- ⚠ THE INTRADAY COLLAPSE HAPPENS HERE AND IS DELIBERATE.
--   PK (FinancialJsonId, [Date]) admits one row per calendar day, but ~3% of
--   series are five-minute intraday and carry up to 288 observations for a
--   single date (docs/apis/Criterion.md 4.3). The requester chose last-wins over
--   widening the key.
--
--   "Last" must mean something stable, so the loader stamps each observation
--   with its zero-based position in the source JSON array and sends the rows in
--   that order; this proc keeps the HIGHEST-ordinal observation for a date.
--   Because a DATE cannot carry the ordinal, the loader instead pre-collapses
--   each array in memory (keeping the last element per date) and the
--   ROW_NUMBER below is the second line of defence -- it makes the proc safe to
--   call with an uncollapsed batch, and it is what stops MERGE from erroring if
--   one ever arrives.
--
--   ORDER BY (SELECT 1) is NOT used here, unlike the other procs: with a real
--   collapse in play an arbitrary winner would make the loaded value depend on
--   plan choice. Ordering by [Value] is arbitrary in content but DETERMINISTIC,
--   so re-running a work unit cannot change what is stored. The loader's own
--   collapse is what decides the business answer.
--
-- This is the highest-volume proc in the loader -- hundreds of thousands of
-- rows per call. It is kept to a single MERGE with no OUTPUT clause for that
-- reason.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeFinancialSeriesData
    @Records arm.FinancialSeriesDataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY FinancialJsonId, [Date]
                    ORDER BY [Value] DESC) AS rn
        FROM @Records
    )
    MERGE arm.Financial_SeriesData AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.FinancialJsonId = s.FinancialJsonId
       AND tgt.[Date]          = s.[Date]
    WHEN MATCHED THEN UPDATE SET
        [Value]       = s.[Value],
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (FinancialJsonId, [Date], [Value], ModifiedAtUtc)
        VALUES (s.FinancialJsonId, s.[Date], s.[Value], SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeMiscPeriod -> arm.Misc_Period
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeMiscPeriod
    @Records arm.MiscPeriodTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY PeriodId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Misc_Period AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.PeriodId = s.PeriodId
    WHEN MATCHED THEN UPDATE SET
        MongoId       = s.MongoId,
        PeriodDesc    = s.PeriodDesc,
        PeriodShort   = s.PeriodShort,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PeriodId, MongoId, PeriodDesc, PeriodShort, ModifiedAtUtc)
        VALUES (s.PeriodId, s.MongoId, s.PeriodDesc, s.PeriodShort, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergeMiscUnit -> arm.Misc_Unit
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergeMiscUnit
    @Records arm.MiscUnitTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY UnitId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Misc_Unit AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON tgt.UnitId = s.UnitId
    WHEN MATCHED THEN UPDATE SET
        MongoId       = s.MongoId,
        UnitDesc      = s.UnitDesc,
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (UnitId, MongoId, UnitDesc, ModifiedAtUtc)
        VALUES (s.UnitId, s.MongoId, s.UnitDesc, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergePipelinesMetadata -> arm.Pipelines_Metadata
--
-- Point is DERIVED here rather than shipped in the TVP:
--
--     geography::Point(Latitude, Longitude, 4326)
--
-- guarded so that a missing or out-of-range coordinate yields NULL instead of
-- raising. geography::Point THROWS on a latitude outside [-90, 90] or a
-- longitude outside [-180, 180], which would abort the whole batch, so the
-- range test is load-bearing rather than decorative. SRID 4326 (WGS 84) matches
-- arm.PlantPoint in the IIR loader.
--
-- As of 2026-09-03 the source has NULL latitude and longitude for all 42,446
-- rows, so Point lands NULL for every row -- correct behaviour, not a bug.
--
-- MappingId (IDENTITY) is written by neither branch.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelinesMetadata
    @Records arm.PipelinesMetadataTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY MetadataId ORDER BY UpdateDate DESC) AS rn
        FROM @Records
    ),
    pt AS
    (
        SELECT s.*,
               CASE
                   WHEN s.Latitude  IS NOT NULL
                    AND s.Longitude IS NOT NULL
                    AND s.Latitude  BETWEEN -90  AND 90
                    AND s.Longitude BETWEEN -180 AND 180
                   THEN geography::Point(s.Latitude, s.Longitude, 4326)
               END AS BuiltPoint
        FROM src AS s
        WHERE s.rn = 1
    )
    MERGE arm.Pipelines_Metadata AS tgt
    USING pt AS s
       ON tgt.MetadataId = s.MetadataId
    WHEN MATCHED THEN UPDATE SET
        AssetId                        = s.AssetId,
        Tsp                            = s.Tsp,
        PipelineName                   = s.PipelineName,
        LocName                        = s.LocName,
        Loc                            = s.Loc,
        RecDelSign                     = s.RecDelSign,
        LocQtiId                       = s.LocQtiId,
        LocQtiShort                    = s.LocQtiShort,
        CategoryShort                  = s.CategoryShort,
        SubCategoryDesc                = s.SubCategoryDesc,
        SubCategory2Desc               = s.SubCategory2Desc,
        CountryName                    = s.CountryName,
        StateName                      = s.StateName,
        CountyName                     = s.CountyName,
        OffshoreBlockName              = s.OffshoreBlockName,
        ConnectingPipeline             = s.ConnectingPipeline,
        ConnectingEntity               = s.ConnectingEntity,
        StorageName                    = s.StorageName,
        StorageCalcFlag                = s.StorageCalcFlag,
        Latitude                       = s.Latitude,
        Longitude                      = s.Longitude,
        [Point]                        = s.BuiltPoint,
        UpdateDate                     = s.UpdateDate,
        TspShort                       = s.TspShort,
        LocPurpDesc                    = s.LocPurpDesc,
        StateAbb                       = s.StateAbb,
        FercPipelineId                 = s.FercPipelineId,
        FercPointId                    = s.FercPointId,
        TransportationMaxDailyQuantity = s.TransportationMaxDailyQuantity,
        StorageMaxDailyQuantity        = s.StorageMaxDailyQuantity,
        LocZone                        = s.LocZone,
        UpdnLoc                        = s.UpdnLoc,
        BasinName                      = s.BasinName,
        Units                          = s.Units,
        PointType                      = s.PointType,
        ProvinceName                   = s.ProvinceName,
        Ticker                         = s.Ticker,
        ModifiedAtUtc                  = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MetadataId, AssetId, Tsp, PipelineName, LocName, Loc, RecDelSign, LocQtiId,
                LocQtiShort, CategoryShort, SubCategoryDesc, SubCategory2Desc, CountryName,
                StateName, CountyName, OffshoreBlockName, ConnectingPipeline, ConnectingEntity,
                StorageName, StorageCalcFlag, Latitude, Longitude, [Point], UpdateDate, TspShort,
                LocPurpDesc, StateAbb, FercPipelineId, FercPointId, TransportationMaxDailyQuantity,
                StorageMaxDailyQuantity, LocZone, UpdnLoc, BasinName, Units, PointType,
                ProvinceName, Ticker, ModifiedAtUtc)
        VALUES (s.MetadataId, s.AssetId, s.Tsp, s.PipelineName, s.LocName, s.Loc, s.RecDelSign, s.LocQtiId,
                s.LocQtiShort, s.CategoryShort, s.SubCategoryDesc, s.SubCategory2Desc, s.CountryName,
                s.StateName, s.CountyName, s.OffshoreBlockName, s.ConnectingPipeline, s.ConnectingEntity,
                s.StorageName, s.StorageCalcFlag, s.Latitude, s.Longitude, s.BuiltPoint, s.UpdateDate, s.TspShort,
                s.LocPurpDesc, s.StateAbb, s.FercPipelineId, s.FercPointId, s.TransportationMaxDailyQuantity,
                s.StorageMaxDailyQuantity, s.LocZone, s.UpdnLoc, s.BasinName, s.Units, s.PointType,
                s.ProvinceName, s.Ticker, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergePipelinesNominationPoint -> arm.Pipelines_NominationPoint
--
-- IsLatest is written by neither branch -- it has no source column.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelinesNominationPoint
    @Records arm.PipelinesNominationPointTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY MetadataId, EffGasDay, CycleId, HourlyCycleId
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Pipelines_NominationPoint AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.MetadataId    = s.MetadataId
       AND tgt.EffGasDay     = s.EffGasDay
       AND tgt.CycleId       = s.CycleId
       AND tgt.HourlyCycleId = s.HourlyCycleId
    WHEN MATCHED THEN UPDATE SET
        EndEffGasDay           = s.EndEffGasDay,
        TspShort               = s.TspShort,
        CycleDesc              = s.CycleDesc,
        DesignCapacity         = s.DesignCapacity,
        OperatingCapacity      = s.OperatingCapacity,
        ScheduledQuantity      = s.ScheduledQuantity,
        OperationallyAvailable = s.OperationallyAvailable,
        Tbl                    = s.Tbl,
        Ticker                 = s.Ticker,
        ModifiedAtUtc          = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MetadataId, EffGasDay, CycleId, HourlyCycleId, EndEffGasDay, TspShort, CycleDesc,
                DesignCapacity, OperatingCapacity, ScheduledQuantity, OperationallyAvailable,
                Tbl, Ticker, ModifiedAtUtc)
        VALUES (s.MetadataId, s.EffGasDay, s.CycleId, s.HourlyCycleId, s.EndEffGasDay, s.TspShort, s.CycleDesc,
                s.DesignCapacity, s.OperatingCapacity, s.ScheduledQuantity, s.OperationallyAvailable,
                s.Tbl, s.Ticker, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergePipelinesPointflows -> arm.Pipelines_Pointflows
--
-- ⚠ MERGES ON THE NATURAL KEY (MetadataId, EffGasDay, CycleId), NOT the
--   declared primary key. Id is IDENTITY: the server generates it, so no
--   inbound row can carry one and matching on it would make every run insert a
--   fresh duplicate. The natural key was verified unique in the source
--   (19,431 rows = 19,431 distinct triples on 2026-09-02), and
--   UQ_ARM_Pointflows_NaturalKey in 001 keeps it that way.
--
-- Id is written by neither branch -- IDENTITY assigns it on insert and it must
-- never change on update, or downstream references would break.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelinesPointflows
    @Records arm.PipelinesPointflowsTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY MetadataId, EffGasDay, CycleId
                    ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Pipelines_Pointflows AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.MetadataId = s.MetadataId
       AND tgt.EffGasDay  = s.EffGasDay
       AND tgt.CycleId    = s.CycleId
    WHEN MATCHED THEN UPDATE SET
        CycleDesc         = s.CycleDesc,
        PipelineName      = s.PipelineName,
        LocName           = s.LocName,
        CategoryShort     = s.CategoryShort,
        LocZone           = s.LocZone,
        LocPurpDesc       = s.LocPurpDesc,
        StateName         = s.StateName,
        StateAbb          = s.StateAbb,
        CountryName       = s.CountryName,
        ScheduledQuantity = s.ScheduledQuantity,
        ModifiedAtUtc     = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MetadataId, EffGasDay, CycleId, CycleDesc, PipelineName, LocName, CategoryShort,
                LocZone, LocPurpDesc, StateName, StateAbb, CountryName, ScheduledQuantity,
                ModifiedAtUtc)
        VALUES (s.MetadataId, s.EffGasDay, s.CycleId, s.CycleDesc, s.PipelineName, s.LocName, s.CategoryShort,
                s.LocZone, s.LocPurpDesc, s.StateName, s.StateAbb, s.CountryName, s.ScheduledQuantity,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_BulkMergePipelinesRegion -> arm.Pipelines_Region
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_BulkMergePipelinesRegion
    @Records arm.PipelinesRegionTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH src AS
    (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY RegionId, StateId ORDER BY (SELECT 1)) AS rn
        FROM @Records
    )
    MERGE arm.Pipelines_Region AS tgt
    USING (SELECT * FROM src WHERE rn = 1) AS s
       ON  tgt.RegionId = s.RegionId
       AND tgt.StateId  = s.StateId
    WHEN MATCHED THEN UPDATE SET
        RegionName       = s.RegionName,
        StateAbb         = s.StateAbb,
        StateName        = s.StateName,
        EIA_NG_Regions   = s.EIA_NG_Regions,
        EIA_PADD_Regions = s.EIA_PADD_Regions,
        ModifiedAtUtc    = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RegionId, StateId, RegionName, StateAbb, StateName, EIA_NG_Regions,
                EIA_PADD_Regions, ModifiedAtUtc)
        VALUES (s.RegionId, s.StateId, s.RegionName, s.StateAbb, s.StateName, s.EIA_NG_Regions,
                s.EIA_PADD_Regions, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad — post-load anomaly report.
--
-- OBSERVATIONAL ONLY. It never fails a run; the loader logs whatever comes back
-- and carries on. @AsOfDate scopes the day-windowed checks; the dimension
-- checks are global because those tables are snapshots.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @AsOfDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @AsOfDate IS NULL SET @AsOfDate = CAST(SYSUTCDATETIME() AS DATE);

    DECLARE @Findings TABLE
    (
        Severity  VARCHAR(10)  NOT NULL,
        Check_    VARCHAR(60)  NOT NULL,
        Detail    VARCHAR(400) NOT NULL
    );

    -- An empty dimension means the snapshot pipeline failed or was disabled.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyDimension', 'arm.Financial_Metadata is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.Financial_Metadata);

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyDimension', 'arm.Misc_Period is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.Misc_Period);

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyDimension', 'arm.Misc_Unit is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.Misc_Unit);

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyDimension', 'arm.Pipelines_Metadata is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.Pipelines_Metadata);

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'EmptyDimension', 'arm.Pipelines_Region is empty'
    WHERE NOT EXISTS (SELECT 1 FROM arm.Pipelines_Region);

    -- No nomination rows for the as-of gas day. Expected on a fresh database and
    -- on a day the source has not published yet; suspicious otherwise.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'INFO', 'NoNominationsForDay',
           CONCAT('arm.Pipelines_NominationPoint has no rows for EffGasDay ', CONVERT(VARCHAR(10), @AsOfDate, 23))
    WHERE NOT EXISTS (SELECT 1 FROM arm.Pipelines_NominationPoint WHERE EffGasDay = @AsOfDate);

    -- Pointflows is derived from the same source rows as NominationPoint, so a
    -- day present in one and absent from the other means one pipeline failed.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'PointflowsGap',
           CONCAT('EffGasDay ', CONVERT(VARCHAR(10), @AsOfDate, 23),
                  ' has ', (SELECT COUNT(*) FROM arm.Pipelines_NominationPoint WHERE EffGasDay = @AsOfDate),
                  ' nomination row(s) but 0 pointflow row(s)')
    WHERE EXISTS (SELECT 1 FROM arm.Pipelines_NominationPoint WHERE EffGasDay = @AsOfDate)
      AND NOT EXISTS (SELECT 1 FROM arm.Pipelines_Pointflows   WHERE EffGasDay = @AsOfDate);

    -- Series publications with no observations at all. A handful is normal (an
    -- empty source array); a large share means the unpivot is broken.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'SeriesWithoutData',
           CONCAT(COUNT(*), ' of the publications for PostDate ', CONVERT(VARCHAR(10), @AsOfDate, 23),
                  ' have no arm.Financial_SeriesData rows')
    FROM arm.Financial_Series AS s
    WHERE s.PostDate = @AsOfDate
      AND NOT EXISTS (SELECT 1 FROM arm.Financial_SeriesData AS d WHERE d.FinancialJsonId = s.FinancialJsonId)
    HAVING COUNT(*) > 0;

    -- Observations whose parent publication is missing. Non-zero means the two
    -- financial pipelines have drifted -- they must read the same source rows.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'WARN', 'OrphanSeriesData',
           CONCAT(COUNT(*), ' arm.Financial_SeriesData FinancialJsonId value(s) have no arm.Financial_Series row')
    FROM (SELECT DISTINCT d.FinancialJsonId
          FROM arm.Financial_SeriesData AS d
          WHERE NOT EXISTS (SELECT 1 FROM arm.Financial_Series AS s WHERE s.FinancialJsonId = d.FinancialJsonId)) AS x
    HAVING COUNT(*) > 0;

    -- PeriodId/UnitId are mongo ids and join the dimensions on MongoId, not on
    -- their PKs. A few unmatched values are expected (~1.4% / ~0.8% at the
    -- source); a spike means a dimension snapshot is stale.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'INFO', 'UnmatchedPeriodId',
           CONCAT(COUNT(*), ' publication(s) for PostDate ', CONVERT(VARCHAR(10), @AsOfDate, 23),
                  ' carry a PeriodId with no arm.Misc_Period.MongoId match')
    FROM arm.Financial_Series AS s
    WHERE s.PostDate = @AsOfDate
      AND s.PeriodId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM arm.Misc_Period AS p WHERE p.MongoId = s.PeriodId)
    HAVING COUNT(*) > 0;

    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'INFO', 'UnmatchedUnitId',
           CONCAT(COUNT(*), ' publication(s) for PostDate ', CONVERT(VARCHAR(10), @AsOfDate, 23),
                  ' carry a UnitId with no arm.Misc_Unit.MongoId match')
    FROM arm.Financial_Series AS s
    WHERE s.PostDate = @AsOfDate
      AND s.UnitId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM arm.Misc_Unit AS u WHERE u.MongoId = s.UnitId)
    HAVING COUNT(*) > 0;

    -- Flows referencing a point that is not in the catalog. Expected to be small
    -- (25 of 19,431 at the source on 2026-09-02) because Pointflows LEFT JOINs.
    INSERT @Findings (Severity, Check_, Detail)
    SELECT 'INFO', 'FlowsWithoutCatalogPoint',
           CONCAT(COUNT(*), ' pointflow row(s) for EffGasDay ', CONVERT(VARCHAR(10), @AsOfDate, 23),
                  ' reference a MetadataId absent from arm.Pipelines_Metadata')
    FROM arm.Pipelines_Pointflows AS f
    WHERE f.EffGasDay = @AsOfDate
      AND NOT EXISTS (SELECT 1 FROM arm.Pipelines_Metadata AS m WHERE m.MetadataId = f.MetadataId)
    HAVING COUNT(*) > 0;

    SELECT Severity, Check_ AS [Check], Detail FROM @Findings ORDER BY Severity, Check_;
END
GO
