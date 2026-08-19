-- =============================================================================
-- CompareLegacyDboVsArm.sql
--   READ-ONLY reconciliation of the LEGACY CWG tables ([dbo].*) against the NEW
--   CWG loader tables ([arm].*). BOTH schemas live in the SAME database, CWG, so
--   every reference is two-part (dbo.* / arm.*) — no cross-database names.
--
--   This is a QA / reconciliation TOOL, NOT part of the ordered 001-003 deploy
--   (hence NO numeric prefix). It performs SELECTs only. The ONLY objects it
--   writes are session-scoped #temp tables used to accumulate results; it issues
--   NO INSERT/UPDATE/DELETE/DDL against any dbo.* or arm.* table.
--
--   Run it top-to-bottom in SSMS or sqlcmd against the CWG database. It emits a
--   per-table SUMMARY result set (last) and, when @Detail=1, per-table DETAIL
--   result sets for drill-down.
--
-- -----------------------------------------------------------------------------
-- TWO CHECKS PER TABLE
--   (1) ROW PRESENCE — join dbo's PK to the corresponding arm columns and count:
--       * MissingInArm  = dbo rows with NO matching arm row   (the PRIMARY concern)
--       * ExtraInArm    = arm rows with NO matching dbo row    (informational —
--         arm legitimately has finer grain / more regions / more blocks; see notes)
--       * MatchedKeys   = matched dbo x arm join PAIRS (for the geography tables
--         arm adds a Region key, so one dbo row can pair with several arm rows).
--   (2) DECIMAL VALUE MATCH ignoring rounding error. dbo numeric columns are
--       DECIMAL(18,0) (integer-rounded) or float; arm columns are higher precision
--       (DECIMAL(5,1)/(9,4)/(12,4)/(6,2)). A column PAIR matches when BOTH are NULL,
--       or BOTH non-NULL AND ABS(dbo - arm) <= @Tol; one-side-NULL = mismatch.
--       ValueMismatches = matched join pairs with >= 1 offending column.
--
-- @Tol RATIONALE (default 0.5)
--   dbo stored measures as DECIMAL(18,0) — rounded to the nearest integer. A
--   correctly-reproduced high-precision arm value rounds to that same integer if
--   and only if ABS(dbo - arm) <= 0.5. So @Tol = 0.5 exactly absorbs integer
--   rounding and nothing more. Boundary (exactly 0.5) counts as a MATCH (<=).
--   Raise @Tol only to probe looser agreement; lower it to find any non-integer
--   drift. NOTE: IS_FORECAST (bit) is folded into the same tolerance test as 0/1,
--   which is correct for any @Tol < 1 (True vs False => |1-0|=1 > 0.5 = mismatch);
--   do not raise @Tol to >= 1 if you care about the IsForecast comparison.
--   NOTE: lat/lon (dbo.Station) are also compared with @Tol; 0.5 DEGREES is very
--   loose for coordinates (~55 km) — tighten @Tol (or read the DETAIL diffs) if you
--   need coordinate-grade agreement for Station.
--
-- @Date SCOPE (default NULL = ALL dates)
--   Optional single-date scope so a run can compare just ONE representative/run date
--   (e.g. to reconcile a single day quickly, or to isolate a suspect date).
--     * @Date IS NULL  => compare ALL dates exactly as before (a true no-op; this is
--       the default and every predicate short-circuits on `@Date IS NULL OR ...`).
--     * @Date non-NULL => restrict BOTH sides of each dated table to that date, on
--       EVERY summary metric (DboRows/ArmRows/MatchedKeys/MissingInArm/ExtraInArm/
--       ValueMismatches) AND every DETAIL query, using each side's own date column.
--   Per-table representative-date column (dbo col / arm col):
--     1  CityForecast                  ProductionDate / ProductionDate
--     2  CityGasForecast               ProductionDate / ProductionDate
--     3  CityObservation               [Date]         / ObsDate
--     4  DailyNormal                   UNDATED reference -> NOT filtered (Note flags it)
--     5  SolarForecast                 ForecastDate   / ForecastDate  (see forecast note)
--     6  SolarForecastChange           ForecastDate   / ForecastDate  (see forecast note)
--     7  SolarHourly                   CAST(UTCHourEnding AS DATE) / CAST(HourEndingUtc AS DATE)
--     8  USNationalDegreeDay           ObservationDate / RunDate  (run date)
--        US5RegionDegreeDay            ObservationDate / RunDate  (+ Region->RegionName join key)
--        US9RegionDegreeDay            ObservationDate / RunDate  (+ Region->RegionName join key)
--        USISODegreeDay                ObservationDate / RunDate  (+ Region->RegionName join key)
--        StateDegreeDayObservation     ObservationDate (dbo count only; no arm counterpart)
--     9  WindForecast                  ForecastDate   / ForecastDate  (see forecast note)
--    10  WindForecastSubRegion         ForecastDate   / ForecastDate  (see forecast note)
--    11  WindHourly                    CAST(UTCHourEnding AS DATE) / CAST(HourEndingUtc AS DATE)
--    12  WindTotalCapacityClimatology  ForecastDate   / ProductionDate
--    13  WindTotalCapacityMW           ForecastDate   / ProductionDate  (Block='Current' kept)
--    14  WindTotalCapacityPct          ForecastDate   / ProductionDate  (Block='Current' kept)
--    15  Station                       UNDATED reference -> NOT filtered (Note flags it)
--   FORECAST-TABLE scoping caveat (SolarForecast / SolarForecastChange / WindForecast /
--   WindForecastSubRegion): dbo carries NO InitDate, so @Date scopes by ForecastDate
--   (the VALID/forecast date), NOT by init/run date. These remain GRAIN MISMATCH -
--   REVIEW (counts + coarse presence only); @Date just narrows those counts to one
--   forecast date.
--   DailyNormal and Station are UNDATED reference tables: @Date does not apply and
--   they are always compared in full (their Notes say so when @Date is set).
--
-- HOW TO CONFIGURE
--   Edit the single "INSERT #Cfg VALUES (...)" line below:
--     Tol        DECIMAL(9,4)  tolerance (default 0.5)
--     Detail     BIT           0 = summary only; 1 = also emit per-table DETAIL selects
--     FilterDate DATE          NULL = all dates (default); a date = scope to that date
--   (@Tol/@Detail/@Date are read from #Cfg inside every batch so they survive GO.)
--
-- =============================================================================
-- PER-TABLE MAPPING STATUS (what this script checks, and how)
-- -----------------------------------------------------------------------------
--   dbo.CityForecast            -> arm.CityForecast          MAPPED (arm SCOPED to Units='F' AND Region='northamerica')
--   dbo.CityGasForecast         -> arm.CityGasForecast       MAPPED (clean 1:1; arm has no Region)
--   dbo.CityObservation         -> arm.CityObservation       MAPPED (arm adds Region; 1:N possible)
--   dbo.DailyNormal             -> arm.DailyNormal           MAPPED (WIDE dbo -> TALL arm, unpivot)
--   dbo.SolarForecast           -> arm.SolarForecast         GRAIN MISMATCH - REVIEW (see below)
--   dbo.SolarForecastChange     -> arm.SolarForecastChange   GRAIN MISMATCH - REVIEW (see below)
--   dbo.SolarHourly             -> arm.SolarHourly           MAPPED (WIDE -> TALL, unpivot)
--   dbo.USNationalDegreeDay     -> arm.NationalDegreeDays    MAPPED (the ONLY degree-day counterpart)
--   dbo.WindForecast            -> arm.WindForecast          GRAIN MISMATCH - REVIEW (see below)
--   dbo.WindForecastSubRegion   -> arm.WindForecastSubRegion GRAIN MISMATCH - REVIEW (see below)
--   dbo.WindHourly              -> arm.WindHourly            MAPPED (WIDE -> TALL, unpivot)
--   dbo.WindTotalCapacityClimatology -> arm.WindTotalCapacityClimatology  MAPPED (single block)
--   dbo.WindTotalCapacityMW     -> arm.WindTotalCapacityMW   MAPPED (dbo <-> arm Block='Current')
--   dbo.WindTotalCapacityPct    -> arm.WindTotalCapacityPct  MAPPED (dbo <-> arm Block='Current')
--   dbo.Station                 -> arm.Station               MAPPED (arm adds Region; lat/lon decimal check)
--   dbo.StateDegreeDayObservation -> (none)                  NO ARM COUNTERPART
--   dbo.US5RegionDegreeDay        -> arm.Regions5DegreeDays  MAPPED (join +RegionName)
--   dbo.US9RegionDegreeDay        -> arm.Regions9DegreeDays  MAPPED (join +RegionName)
--   dbo.USISODegreeDay            -> arm.ISODegreeDays        MAPPED (join +RegionName)
--
-- DEGREE-DAY RESOLUTION (the big one)
--   The arm loader now pulls FOUR subregion variants of
--   northamerica_{subregion}_wdd_{date}.csv into four arm tables (the subregion is
--   carried on arm.FileLog.Variant):
--     * subregion=national -> arm.NationalDegreeDays (PK RunDate, Dates; NO region col).
--       Column family NG_HDD / POP_CDD / ELEC_CDD (each 30Y/10Y/LAST_Y) + IS_FORECAST,
--       NO weights. Matches dbo.USNationalDegreeDay EXACTLY. Join ObservationDate->RunDate,
--       Date->Dates; compare 12 DD cols + IS_FORECAST.
--     * subregion=5region -> arm.Regions5DegreeDays (PK RunDate, Dates, RegionName). Same
--       NG/POP/ELEC families PLUS per-region GasWeight/ElctWeight/PopWeight. Matches
--       dbo.US5RegionDegreeDay. Join +RegionName (dbo.Region->arm.RegionName); 15 DD/weight
--       cols + IS_FORECAST compared (ALL dbo cols map, incl. the three weights).
--     * subregion=9region -> arm.Regions9DegreeDays (identical layout to Regions5). Matches
--       dbo.US9RegionDegreeDay. Same join / column set as 5region.
--     * subregion=iso -> arm.ISODegreeDays (PK RunDate, Dates, RegionName). DIVERGENT: the
--       HDD family is POP_HDD (NOT NG_HDD), and there is NO ELEC family and NO weights.
--       Matches dbo.USISODegreeDay. Join +RegionName; 8 DD cols + IS_FORECAST compared.
--   IS_FORECAST is compared as 0/1 within @Tol (valid for @Tol < 1), as for national.
--     * ONLY dbo.StateDegreeDayObservation now remains WITHOUT an arm counterpart
--       (subregion 'state' is still NOT requested by the loader) -> reported
--       'NO ARM COUNTERPART', row count only; NO join fabricated.
--   REGION-LABEL RISK (US5/US9/ISO): dbo.Region is joined DIRECTLY to arm.RegionName.
--   SQL Server's default CI collation absorbs case, but any spelling / spacing /
--   abbreviation / punctuation difference in the legacy labels surfaces as
--   MissingInArm/ExtraInArm (NOT a value mismatch). arm labels are the raw CWG REGION_NAME
--   values (5region: Pacific / Mountain / South Central / Midwest / East; 9region:
--   NEW ENGLAND / MIDDLE ATLANTIC / E N CENTRAL / W N CENTRAL / SOUTH ATLANTIC /
--   E S CENTRAL / W S CENTRAL / MOUNTAIN / PACIFIC; iso: 21 incl. 'CAISO NORTH' /
--   'CAISO SOUTH' / 'EAST PJM' / 'WEST PJM' / 'UPPER MISO' / 'LOWER MISO'). Reviewer
--   should confirm label equivalence.
--
-- FORECAST GRAIN/TIMEZONE MISMATCH (SolarForecast, SolarForecastChange,
--                                   WindForecast, WindForecastSubRegion)
--   dbo keys on (ForecastDate, UTCHourEnding [datetime2, a UTC hour-ending], Region
--   [,SubRegion]) with NO InitDate and retains (apparently) ONE forecast per
--   forecast-hour. arm keys on (Region [,SubRegion], InitDate, ForecastDate, HourOfDay
--   0-23 derived from an EST clock HourLabel) and keeps EVERY InitDate. Reconciling
--   the two requires mapping arm's EST clock hour to dbo's UTC hour-ending, but:
--     * The CWG source (and the arm model / design doc, docs/design/CWG.md sec 4/6)
--       carries NO UTC anywhere for these files - only 'Date (EST)' + a 24-label EST
--       clock. The legacy dbo UTCHourEnding is a LEGACY-side conversion whose rules
--       (fixed EST -5 vs America/New_York with DST, and hour-ending vs hour-beginning)
--       are NOT documented and cannot be recovered from these files, AND
--     * dbo has no InitDate to line up against arm's InitDate.
--   Per the task's instruction ("if the timezone/hour mapping cannot be established
--   from the docs, mark GRAIN MISMATCH - REVIEW rather than emit misleading diffs"),
--   these four tables are reported with row counts ONLY (DboRows/ArmRows) and
--   MappingStatus='GRAIN MISMATCH - REVIEW'; the join-based metrics are left NULL.
--   A COARSE presence probe (distinct Region[,SubRegion],ForecastDate in dbo but not
--   in arm at ANY init/hour) is available in the DETAIL section for triage.
--   ==> REVIEWER MUST DECIDE, before any per-hour reconciliation: (a) the EST->UTC
--       offset rule and DST handling the legacy loader used; (b) hour-ending vs
--       hour-beginning; (c) which arm InitDate the single dbo forecast corresponds to
--       (most likely the LATEST/MAX InitDate per (Region,ForecastDate)).
--
-- UNMAPPED COLUMNS / STRUCTURES (present in dbo, NO arm equivalent -> NOT checked)
--   * dbo.StateDegreeDayObservation (WHOLE table): subregion 'state' is not loaded, so
--     none of its columns are checked - POP_HDD family, POP_CDD family, IS_FORECAST,
--     GAS_WEIGHT / ELCT_WEIGHT / POP_WEIGHT, RegionName, State. (US5/US9 weights and
--     USISO's POP_HDD family are NOW mapped - see DEGREE-DAY RESOLUTION.)
--   * dbo.Station.MappingId: no arm column.
--   * dbo.WindTotalCapacityMW / Pct: dbo has NO block dimension; arm's Block=
--     'Yesterday'/'Change' rows are arm-only (compared block is 'Current' only).
--   * arm-only (informational, NOT a dbo gap): Region on the city/station tables,
--     Block on the capacity MW/Pct tables, InitDate/HourLabel/HourOfDay on the
--     forecast tables, NW/SW (and other) extra regions on the wide->tall tables,
--     and FileLogId / DateCreated / ModifiedAtUtc on every arm fact.
--
-- ASSUMPTIONS the reader should confirm
--   * dbo.WindTotalCapacity{Climatology,MW,Pct} avg columns are in the SAME unit as
--     arm (Climo/Pct = %, MW = MW). A systematic unit difference would surface as a
--     large, uniform ValueMismatches count.
--   * The wide->tall (SolarHourly/WindHourly/DailyNormal) join treats a dbo NULL cell
--     as "absent": only dbo NON-NULL cells count toward MissingInArm, and arm skips
--     '"NULL"'/blank cells by design (resolved decision 4), so a dbo-NULL / arm-absent
--     pair is NOT flagged. DboRows for those three tables = dbo NON-NULL cell count
--     (comparable to arm's tall row count), not the wide row count.
--   * UTC hour-ending timestamps (SolarHourly/WindHourly) are second-aligned; the
--     join casts both sides to DATETIME2(0).
-- =============================================================================

USE CWG;
GO

SET NOCOUNT ON;

-- --- config + accumulator (session #temp; survive GO; dropped/rebuilt each run) ---
DROP TABLE IF EXISTS #Cfg;
DROP TABLE IF EXISTS #Summary;

CREATE TABLE #Cfg (Tol DECIMAL(9,4) NOT NULL, Detail BIT NOT NULL, FilterDate DATE NULL);
-- >>> EDIT HERE to change tolerance / turn on DETAIL drill-down (1) / scope to one date <<<
-- FilterDate: scope the compare to ONE representative date (e.g. '2026-08-11'); NULL = all dates.
INSERT #Cfg (Tol, Detail, FilterDate) VALUES (0.5, 0, NULL);

CREATE TABLE #Summary
(
    Ord             INT           NOT NULL,
    TableName       VARCHAR(60)   NOT NULL,
    MappingStatus   VARCHAR(40)   NOT NULL,
    DboRows         INT           NULL,
    ArmRows         INT           NULL,
    MatchedKeys     INT           NULL,
    MissingInArm    INT           NULL,
    ExtraInArm      INT           NULL,
    ValueMismatches INT           NULL,
    Notes           NVARCHAR(400) NULL
);
GO

-- =============================================================================
-- 1. dbo.CityForecast  ->  arm.CityForecast
--    dbo PK (ProductionDate, Date, Station); arm PK (Region, Station, ProductionDate,
--    ForecastDate). Join on the 3 SHARED dbo keys (dbo.Date -> arm.ForecastDate).
--    SCOPING (per-region units): arm.CityForecast now holds europe '_C' rows (°C) and
--    no asia rows, while legacy dbo.CityForecast is northamerica/°F only. EVERY arm-side
--    predicate here is therefore scoped to a.Units='F' AND a.Region='northamerica' so
--    the europe/°C rows are NOT miscounted as ExtraInArm and °C values are NOT diffed
--    against °F. With that scope the arm side is 1:1 on the shared key (single geography).
--    Values: ForecastMin/Max/Avg -> FcstMin/Max/Avg, NormMin/Max, HDD/CDD -> Hdd/Cdd.
--    @Date scopes both sides by ProductionDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 1, 'CityForecast', 'MAPPED (arm SCOPED to NA/F)',
  (SELECT COUNT(1) FROM dbo.CityForecast d WHERE (@Date IS NULL OR d.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM arm.CityForecast a WHERE (@Date IS NULL OR a.ProductionDate = @Date) AND a.Units = 'F' AND a.Region = 'northamerica'),
  (SELECT COUNT(1) FROM dbo.CityForecast d JOIN arm.CityForecast a
        ON a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date] AND a.Station = d.Station
        WHERE (@Date IS NULL OR d.ProductionDate = @Date) AND a.Units = 'F' AND a.Region = 'northamerica'),
  (SELECT COUNT(1) FROM dbo.CityForecast d WHERE (@Date IS NULL OR d.ProductionDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.CityForecast a WHERE a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date] AND a.Station = d.Station AND a.Units = 'F' AND a.Region = 'northamerica')),
  (SELECT COUNT(1) FROM arm.CityForecast a WHERE (@Date IS NULL OR a.ProductionDate = @Date) AND a.Units = 'F' AND a.Region = 'northamerica' AND NOT EXISTS
        (SELECT 1 FROM dbo.CityForecast d WHERE d.ProductionDate = a.ProductionDate AND d.[Date] = a.ForecastDate AND d.Station = a.Station)),
  (SELECT COUNT(1) FROM (
        SELECT d.ProductionDate, d.[Date], d.Station, a.Region
        FROM dbo.CityForecast d JOIN arm.CityForecast a
             ON a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date] AND a.Station = d.Station
        CROSS APPLY (VALUES
             (CAST(d.ForecastMin AS DECIMAL(38,6)), CAST(a.FcstMin AS DECIMAL(38,6))),
             (CAST(d.ForecastMax AS DECIMAL(38,6)), CAST(a.FcstMax AS DECIMAL(38,6))),
             (CAST(d.ForecastAvg AS DECIMAL(38,6)), CAST(a.FcstAvg AS DECIMAL(38,6))),
             (CAST(d.NormMin      AS DECIMAL(38,6)), CAST(a.NormMin AS DECIMAL(38,6))),
             (CAST(d.NormMax      AS DECIMAL(38,6)), CAST(a.NormMax AS DECIMAL(38,6))),
             (CAST(d.HDD          AS DECIMAL(38,6)), CAST(a.Hdd     AS DECIMAL(38,6))),
             (CAST(d.CDD          AS DECIMAL(38,6)), CAST(a.Cdd     AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ProductionDate = @Date)
          AND a.Units = 'F' AND a.Region = 'northamerica'
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ProductionDate, d.[Date], d.Station, a.Region
   ) g),
  N'Join on (ProductionDate, Date->ForecastDate, Station); arm side SCOPED to Units=''F'' AND Region=''northamerica'' (like-for-like NA/°F). europe _C rows and dropped asia are EXCLUDED so they are neither counted ExtraInArm nor diffed against °F. With the NA scope the arm side is 1:1 on the shared key (single geography).';

IF @Detail = 1
BEGIN
    -- 1a. dbo rows missing in arm (arm side scoped to the NA/F subset)
    SELECT 'CityForecast' AS T, 'MissingInArm' AS Issue, d.ProductionDate, d.[Date] AS ForecastDate, d.Station
    FROM dbo.CityForecast d
    WHERE (@Date IS NULL OR d.ProductionDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.CityForecast a WHERE a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date] AND a.Station = d.Station AND a.Units = 'F' AND a.Region = 'northamerica');
    -- 1b. arm rows extra (no dbo) — scoped to NA/F so europe _C rows are NOT reported here
    SELECT 'CityForecast' AS T, 'ExtraInArm' AS Issue, a.Region, a.ProductionDate, a.ForecastDate, a.Station
    FROM arm.CityForecast a
    WHERE (@Date IS NULL OR a.ProductionDate = @Date)
      AND a.Units = 'F' AND a.Region = 'northamerica'
      AND NOT EXISTS (SELECT 1 FROM dbo.CityForecast d WHERE d.ProductionDate = a.ProductionDate AND d.[Date] = a.ForecastDate AND d.Station = a.Station);
    -- 1c. per-column value mismatches (arm side scoped to NA/F)
    SELECT 'CityForecast' AS T, d.ProductionDate, d.[Date] AS ForecastDate, d.Station, a.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.CityForecast d JOIN arm.CityForecast a
         ON a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date] AND a.Station = d.Station
    CROSS APPLY (VALUES
        ('FcstMin', CAST(d.ForecastMin AS DECIMAL(38,6)), CAST(a.FcstMin AS DECIMAL(38,6))),
        ('FcstMax', CAST(d.ForecastMax AS DECIMAL(38,6)), CAST(a.FcstMax AS DECIMAL(38,6))),
        ('FcstAvg', CAST(d.ForecastAvg AS DECIMAL(38,6)), CAST(a.FcstAvg AS DECIMAL(38,6))),
        ('NormMin', CAST(d.NormMin AS DECIMAL(38,6)), CAST(a.NormMin AS DECIMAL(38,6))),
        ('NormMax', CAST(d.NormMax AS DECIMAL(38,6)), CAST(a.NormMax AS DECIMAL(38,6))),
        ('Hdd',     CAST(d.HDD AS DECIMAL(38,6)),     CAST(a.Hdd AS DECIMAL(38,6))),
        ('Cdd',     CAST(d.CDD AS DECIMAL(38,6)),     CAST(a.Cdd AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ProductionDate = @Date)
      AND a.Units = 'F' AND a.Region = 'northamerica'
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ProductionDate, d.[Date], d.Station, x.ColName;
END
GO

-- =============================================================================
-- 2. dbo.CityGasForecast  ->  arm.CityGasForecast   (clean 1:1 - arm has NO Region)
--    Join (Station, ProductionDate, dbo.Date -> arm.ForecastDate). Same 7 values.
--    @Date scopes both sides by ProductionDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 2, 'CityGasForecast', 'MAPPED (clean 1:1)',
  (SELECT COUNT(1) FROM dbo.CityGasForecast d WHERE (@Date IS NULL OR d.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM arm.CityGasForecast a WHERE (@Date IS NULL OR a.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM dbo.CityGasForecast d JOIN arm.CityGasForecast a
        ON a.Station = d.Station AND a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date]
        WHERE (@Date IS NULL OR d.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM dbo.CityGasForecast d WHERE (@Date IS NULL OR d.ProductionDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.CityGasForecast a WHERE a.Station = d.Station AND a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date])),
  (SELECT COUNT(1) FROM arm.CityGasForecast a WHERE (@Date IS NULL OR a.ProductionDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.CityGasForecast d WHERE d.Station = a.Station AND d.ProductionDate = a.ProductionDate AND d.[Date] = a.ForecastDate)),
  (SELECT COUNT(1) FROM (
        SELECT d.Station, d.ProductionDate, d.[Date]
        FROM dbo.CityGasForecast d JOIN arm.CityGasForecast a
             ON a.Station = d.Station AND a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date]
        CROSS APPLY (VALUES
             (CAST(d.ForecastMin AS DECIMAL(38,6)), CAST(a.FcstMin AS DECIMAL(38,6))),
             (CAST(d.ForecastMax AS DECIMAL(38,6)), CAST(a.FcstMax AS DECIMAL(38,6))),
             (CAST(d.ForecastAvg AS DECIMAL(38,6)), CAST(a.FcstAvg AS DECIMAL(38,6))),
             (CAST(d.NormMin      AS DECIMAL(38,6)), CAST(a.NormMin AS DECIMAL(38,6))),
             (CAST(d.NormMax      AS DECIMAL(38,6)), CAST(a.NormMax AS DECIMAL(38,6))),
             (CAST(d.HDD          AS DECIMAL(38,6)), CAST(a.Hdd     AS DECIMAL(38,6))),
             (CAST(d.CDD          AS DECIMAL(38,6)), CAST(a.Cdd     AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ProductionDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.Station, d.ProductionDate, d.[Date]
   ) g),
  N'Join on (Station, ProductionDate, Date->ForecastDate). No Region on either side -> strict 1:1.';

IF @Detail = 1
BEGIN
    SELECT 'CityGasForecast' AS T, 'MissingInArm' AS Issue, d.Station, d.ProductionDate, d.[Date] AS ForecastDate
    FROM dbo.CityGasForecast d
    WHERE (@Date IS NULL OR d.ProductionDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.CityGasForecast a WHERE a.Station = d.Station AND a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date]);
    SELECT 'CityGasForecast' AS T, 'ExtraInArm' AS Issue, a.Station, a.ProductionDate, a.ForecastDate
    FROM arm.CityGasForecast a
    WHERE (@Date IS NULL OR a.ProductionDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.CityGasForecast d WHERE d.Station = a.Station AND d.ProductionDate = a.ProductionDate AND d.[Date] = a.ForecastDate);
    SELECT 'CityGasForecast' AS T, d.Station, d.ProductionDate, d.[Date] AS ForecastDate,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.CityGasForecast d JOIN arm.CityGasForecast a
         ON a.Station = d.Station AND a.ProductionDate = d.ProductionDate AND a.ForecastDate = d.[Date]
    CROSS APPLY (VALUES
        ('FcstMin', CAST(d.ForecastMin AS DECIMAL(38,6)), CAST(a.FcstMin AS DECIMAL(38,6))),
        ('FcstMax', CAST(d.ForecastMax AS DECIMAL(38,6)), CAST(a.FcstMax AS DECIMAL(38,6))),
        ('FcstAvg', CAST(d.ForecastAvg AS DECIMAL(38,6)), CAST(a.FcstAvg AS DECIMAL(38,6))),
        ('NormMin', CAST(d.NormMin AS DECIMAL(38,6)), CAST(a.NormMin AS DECIMAL(38,6))),
        ('NormMax', CAST(d.NormMax AS DECIMAL(38,6)), CAST(a.NormMax AS DECIMAL(38,6))),
        ('Hdd',     CAST(d.HDD AS DECIMAL(38,6)),     CAST(a.Hdd AS DECIMAL(38,6))),
        ('Cdd',     CAST(d.CDD AS DECIMAL(38,6)),     CAST(a.Cdd AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ProductionDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.Station, d.ProductionDate, d.[Date], x.ColName;
END
GO

-- =============================================================================
-- 3. dbo.CityObservation  ->  arm.CityObservation
--    dbo PK (Date, Station); arm PK (Region, Station, ObsDate). Join dbo.Date->ObsDate
--    + Station. arm adds Region (1:N possible). Values MinTemp/MaxTemp, HDD/CDD->Hdd/Cdd.
--    @Date scopes dbo by [Date], arm by ObsDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 3, 'CityObservation', 'MAPPED (arm adds Region)',
  (SELECT COUNT(1) FROM dbo.CityObservation d WHERE (@Date IS NULL OR d.[Date] = @Date)),
  (SELECT COUNT(1) FROM arm.CityObservation a WHERE (@Date IS NULL OR a.ObsDate = @Date)),
  (SELECT COUNT(1) FROM dbo.CityObservation d JOIN arm.CityObservation a
        ON a.ObsDate = d.[Date] AND a.Station = d.Station
        WHERE (@Date IS NULL OR d.[Date] = @Date)),
  (SELECT COUNT(1) FROM dbo.CityObservation d WHERE (@Date IS NULL OR d.[Date] = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.CityObservation a WHERE a.ObsDate = d.[Date] AND a.Station = d.Station)),
  (SELECT COUNT(1) FROM arm.CityObservation a WHERE (@Date IS NULL OR a.ObsDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.CityObservation d WHERE d.[Date] = a.ObsDate AND d.Station = a.Station)),
  (SELECT COUNT(1) FROM (
        SELECT d.[Date], d.Station, a.Region
        FROM dbo.CityObservation d JOIN arm.CityObservation a
             ON a.ObsDate = d.[Date] AND a.Station = d.Station
        CROSS APPLY (VALUES
             (CAST(d.MinTemp AS DECIMAL(38,6)), CAST(a.MinTemp AS DECIMAL(38,6))),
             (CAST(d.MaxTemp AS DECIMAL(38,6)), CAST(a.MaxTemp AS DECIMAL(38,6))),
             (CAST(d.HDD     AS DECIMAL(38,6)), CAST(a.Hdd     AS DECIMAL(38,6))),
             (CAST(d.CDD     AS DECIMAL(38,6)), CAST(a.Cdd     AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.[Date] = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.[Date], d.Station, a.Region
   ) g),
  N'Join on (Date->ObsDate, Station). arm adds Region; pairs counted per dbo x arm.';

IF @Detail = 1
BEGIN
    SELECT 'CityObservation' AS T, 'MissingInArm' AS Issue, d.[Date] AS ObsDate, d.Station
    FROM dbo.CityObservation d
    WHERE (@Date IS NULL OR d.[Date] = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.CityObservation a WHERE a.ObsDate = d.[Date] AND a.Station = d.Station);
    SELECT 'CityObservation' AS T, 'ExtraInArm' AS Issue, a.Region, a.ObsDate, a.Station
    FROM arm.CityObservation a
    WHERE (@Date IS NULL OR a.ObsDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.CityObservation d WHERE d.[Date] = a.ObsDate AND d.Station = a.Station);
    SELECT 'CityObservation' AS T, d.[Date] AS ObsDate, d.Station, a.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.CityObservation d JOIN arm.CityObservation a
         ON a.ObsDate = d.[Date] AND a.Station = d.Station
    CROSS APPLY (VALUES
        ('MinTemp', CAST(d.MinTemp AS DECIMAL(38,6)), CAST(a.MinTemp AS DECIMAL(38,6))),
        ('MaxTemp', CAST(d.MaxTemp AS DECIMAL(38,6)), CAST(a.MaxTemp AS DECIMAL(38,6))),
        ('Hdd',     CAST(d.HDD AS DECIMAL(38,6)),     CAST(a.Hdd AS DECIMAL(38,6))),
        ('Cdd',     CAST(d.CDD AS DECIMAL(38,6)),     CAST(a.Cdd AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.[Date] = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.[Date], d.Station, x.ColName;
END
GO

-- =============================================================================
-- 4. dbo.DailyNormal (WIDE: Month/Day + 10 region cols)  ->  arm.DailyNormal (TALL)
--    Unpivot dbo to (MonthDay 'MM-DD', Region, Val); join arm on (MonthDay, Region).
--    arm has 12 regions (dbo 10 + NW, SW) -> NW/SW rows are EXPECTED ExtraInArm.
--    Value: dbo cell -> arm.NormalMw.
--    UNDATED reference table -> @Date does NOT apply (always compared in full).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

DROP TABLE IF EXISTS #dnTall;
SELECT
    MonthDay = RIGHT('0' + CONVERT(VARCHAR(2), d.[Month]), 2) + '-' + RIGHT('0' + CONVERT(VARCHAR(2), d.[Day]), 2),
    u.Region,
    Val = CAST(u.Val AS DECIMAL(38,6))
INTO #dnTall
FROM dbo.DailyNormal d
CROSS APPLY (VALUES
    ('CAISO', d.CAISO), ('SPP', d.SPP), ('ERCOT', d.ERCOT), ('MISO', d.MISO), ('PJM', d.PJM),
    ('NEPOOL', d.NEPOOL), ('NYISO', d.NYISO), ('BPA', d.BPA), ('IESO', d.IESO), ('AESO', d.AESO)
) u(Region, Val);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 4, 'DailyNormal', 'MAPPED (wide->tall)',
  (SELECT COUNT(1) FROM #dnTall WHERE Val IS NOT NULL),
  (SELECT COUNT(1) FROM arm.DailyNormal),
  (SELECT COUNT(1) FROM #dnTall t JOIN arm.DailyNormal a ON a.MonthDay = t.MonthDay AND a.Region = t.Region WHERE t.Val IS NOT NULL),
  (SELECT COUNT(1) FROM #dnTall t WHERE t.Val IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM arm.DailyNormal a WHERE a.MonthDay = t.MonthDay AND a.Region = t.Region)),
  (SELECT COUNT(1) FROM arm.DailyNormal a WHERE NOT EXISTS
        (SELECT 1 FROM #dnTall t WHERE t.MonthDay = a.MonthDay AND t.Region = a.Region AND t.Val IS NOT NULL)),
  (SELECT COUNT(1) FROM #dnTall t JOIN arm.DailyNormal a ON a.MonthDay = t.MonthDay AND a.Region = t.Region
        WHERE t.Val IS NOT NULL AND (a.NormalMw IS NULL OR ABS(t.Val - a.NormalMw) > @Tol)),
  N'dbo WIDE (10 regions) unpivoted to MM-DD. arm has 12 regions (adds NW,SW) -> EXPECTED ExtraInArm (~732 rows). DboRows = non-NULL cells.'
  + CASE WHEN @Date IS NOT NULL THEN N' (@Date not applicable - undated reference; compared in full)' ELSE N'' END;

IF @Detail = 1
BEGIN
    SELECT 'DailyNormal' AS T, 'MissingInArm' AS Issue, t.MonthDay, t.Region, t.Val AS DboVal
    FROM #dnTall t
    WHERE t.Val IS NOT NULL AND NOT EXISTS (SELECT 1 FROM arm.DailyNormal a WHERE a.MonthDay = t.MonthDay AND a.Region = t.Region);
    SELECT 'DailyNormal' AS T, 'ExtraInArm' AS Issue, a.MonthDay, a.Region, a.NormalMw AS ArmVal
    FROM arm.DailyNormal a
    WHERE NOT EXISTS (SELECT 1 FROM #dnTall t WHERE t.MonthDay = a.MonthDay AND t.Region = a.Region AND t.Val IS NOT NULL);
    SELECT 'DailyNormal' AS T, t.MonthDay, t.Region, t.Val AS DboVal, a.NormalMw AS ArmVal, ABS(t.Val - a.NormalMw) AS AbsDiff
    FROM #dnTall t JOIN arm.DailyNormal a ON a.MonthDay = t.MonthDay AND a.Region = t.Region
    WHERE t.Val IS NOT NULL AND (a.NormalMw IS NULL OR ABS(t.Val - a.NormalMw) > @Tol)
    ORDER BY t.MonthDay, t.Region;
END
GO

-- =============================================================================
-- 5. dbo.SolarForecast  ->  arm.SolarForecast     GRAIN MISMATCH - REVIEW
--    dbo (ForecastDate, UTCHourEnding[UTC], Region) vs arm (Region, InitDate,
--    ForecastDate, HourOfDay[EST clock]). No documented EST->UTC rule; dbo has no
--    InitDate. Row counts only; join metrics NULL. COARSE presence probe in DETAIL.
--    @Date scopes counts by ForecastDate (valid date; dbo has no InitDate).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 5, 'SolarForecast', 'GRAIN MISMATCH - REVIEW',
  (SELECT COUNT(1) FROM dbo.SolarForecast d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.SolarForecast a WHERE (@Date IS NULL OR a.ForecastDate = @Date)),
  NULL, NULL, NULL, NULL,
  N'NOT joined: dbo UTCHourEnding (UTC hour-ending, no InitDate) vs arm EST HourOfDay + InitDate. TZ+InitDate undocumented. @Date scopes by ForecastDate (valid date). See DETAIL for coarse (Region,ForecastDate) presence.';

IF @Detail = 1
BEGIN
    -- COARSE presence only: dbo (Region, ForecastDate) with NO arm row at any Init/Hour.
    SELECT 'SolarForecast' AS T, 'CoarseMissingInArm (Region,ForecastDate)' AS Issue, d.Region, d.ForecastDate
    FROM dbo.SolarForecast d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.SolarForecast a WHERE a.Region = d.Region AND a.ForecastDate = d.ForecastDate)
    GROUP BY d.Region, d.ForecastDate
    ORDER BY d.Region, d.ForecastDate;
END
GO

-- =============================================================================
-- 6. dbo.SolarForecastChange  ->  arm.SolarForecastChange   GRAIN MISMATCH - REVIEW
--    Same grain problem as #5; dbo.Value -> arm.ChangeMw (signed). Counts only.
--    @Date scopes counts by ForecastDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 6, 'SolarForecastChange', 'GRAIN MISMATCH - REVIEW',
  (SELECT COUNT(1) FROM dbo.SolarForecastChange d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.SolarForecastChange a WHERE (@Date IS NULL OR a.ForecastDate = @Date)),
  NULL, NULL, NULL, NULL,
  N'NOT joined (same TZ/InitDate ambiguity as SolarForecast). dbo.Value -> arm.ChangeMw (signed). @Date scopes by ForecastDate. See DETAIL for coarse (Region,ForecastDate) presence.';

IF @Detail = 1
BEGIN
    SELECT 'SolarForecastChange' AS T, 'CoarseMissingInArm (Region,ForecastDate)' AS Issue, d.Region, d.ForecastDate
    FROM dbo.SolarForecastChange d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.SolarForecastChange a WHERE a.Region = d.Region AND a.ForecastDate = d.ForecastDate)
    GROUP BY d.Region, d.ForecastDate
    ORDER BY d.Region, d.ForecastDate;
END
GO

-- =============================================================================
-- 7. dbo.SolarHourly (WIDE: UTCHourEnding + ERCOT, CAISO)  ->  arm.SolarHourly (TALL)
--    Unpivot dbo 2 regions; join dbo.UTCHourEnding -> arm.HourEndingUtc (cast to
--    DATETIME2(0)) + Region. arm has 14 regions -> 12 extra EXPECTED. Value -> ActualMw.
--    @Date scopes dbo at #shTall build and arm subqueries by CAST(<ts> AS DATE).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

DROP TABLE IF EXISTS #shTall;
SELECT
    HourEndingUtc = CAST(d.UTCHourEnding AS DATETIME2(0)),
    u.Region,
    Val = CAST(u.Val AS DECIMAL(38,6))
INTO #shTall
FROM dbo.SolarHourly d
CROSS APPLY (VALUES ('ERCOT', d.ERCOT), ('CAISO', d.CAISO)) u(Region, Val)
WHERE (@Date IS NULL OR CAST(d.UTCHourEnding AS DATE) = @Date);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 7, 'SolarHourly', 'MAPPED (wide->tall)',
  (SELECT COUNT(1) FROM #shTall WHERE Val IS NOT NULL),
  (SELECT COUNT(1) FROM arm.SolarHourly a WHERE (@Date IS NULL OR CAST(a.HourEndingUtc AS DATE) = @Date)),
  (SELECT COUNT(1) FROM #shTall t JOIN arm.SolarHourly a ON a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region WHERE t.Val IS NOT NULL),
  (SELECT COUNT(1) FROM #shTall t WHERE t.Val IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM arm.SolarHourly a WHERE a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region)),
  (SELECT COUNT(1) FROM arm.SolarHourly a WHERE (@Date IS NULL OR CAST(a.HourEndingUtc AS DATE) = @Date) AND NOT EXISTS
        (SELECT 1 FROM #shTall t WHERE t.HourEndingUtc = a.HourEndingUtc AND t.Region = a.Region AND t.Val IS NOT NULL)),
  (SELECT COUNT(1) FROM #shTall t JOIN arm.SolarHourly a ON a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region
        WHERE t.Val IS NOT NULL AND (a.ActualMw IS NULL OR ABS(t.Val - a.ActualMw) > @Tol)),
  N'dbo WIDE (ERCOT,CAISO) unpivoted. arm has 14 regions -> 12 EXPECTED ExtraInArm. dbo NULL cells ignored (arm skips NULL cells by design). DboRows = non-NULL cells.';

IF @Detail = 1
BEGIN
    SELECT 'SolarHourly' AS T, 'MissingInArm' AS Issue, t.HourEndingUtc, t.Region, t.Val AS DboVal
    FROM #shTall t
    WHERE t.Val IS NOT NULL AND NOT EXISTS (SELECT 1 FROM arm.SolarHourly a WHERE a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region);
    SELECT 'SolarHourly' AS T, 'ExtraInArm' AS Issue, a.HourEndingUtc, a.Region, a.ActualMw AS ArmVal
    FROM arm.SolarHourly a
    WHERE (@Date IS NULL OR CAST(a.HourEndingUtc AS DATE) = @Date)
      AND NOT EXISTS (SELECT 1 FROM #shTall t WHERE t.HourEndingUtc = a.HourEndingUtc AND t.Region = a.Region AND t.Val IS NOT NULL);
    SELECT 'SolarHourly' AS T, t.HourEndingUtc, t.Region, t.Val AS DboVal, a.ActualMw AS ArmVal, ABS(t.Val - a.ActualMw) AS AbsDiff
    FROM #shTall t JOIN arm.SolarHourly a ON a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region
    WHERE t.Val IS NOT NULL AND (a.ActualMw IS NULL OR ABS(t.Val - a.ActualMw) > @Tol)
    ORDER BY t.HourEndingUtc, t.Region;
END
GO

-- =============================================================================
-- 8. dbo.USNationalDegreeDay  ->  arm.NationalDegreeDays   (the ONLY DD counterpart)
--    Join dbo.ObservationDate -> arm.RunDate, dbo.Date -> arm.Dates. Compare 12 DD
--    columns + IS_FORECAST (folded into tolerance as 0/1). Column-name renames per
--    design sec 6/8 (NG_HDD->NgHdd, 30Y_->*30y, 10Y_->*10y, LAST_Y_->*LastY, etc.).
--    @Date scopes dbo by ObservationDate, arm by RunDate (run date).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 8, 'USNationalDegreeDay -> NationalDegreeDays', 'MAPPED',
  (SELECT COUNT(1) FROM dbo.USNationalDegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM arm.NationalDegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date)),
  (SELECT COUNT(1) FROM dbo.USNationalDegreeDay d JOIN arm.NationalDegreeDays a
        ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date]
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM dbo.USNationalDegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.NationalDegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date])),
  (SELECT COUNT(1) FROM arm.NationalDegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.USNationalDegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates)),
  (SELECT COUNT(1) FROM (
        SELECT d.ObservationDate, d.[Date]
        FROM dbo.USNationalDegreeDay d JOIN arm.NationalDegreeDays a
             ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date]
        CROSS APPLY (VALUES
             (CAST(d.NG_HDD           AS DECIMAL(38,6)), CAST(a.NgHdd        AS DECIMAL(38,6))),
             (CAST(d.[30Y_NG_HDD]     AS DECIMAL(38,6)), CAST(a.NgHdd30y     AS DECIMAL(38,6))),
             (CAST(d.[10Y_NG_HDD]     AS DECIMAL(38,6)), CAST(a.NgHdd10y     AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_NG_HDD]  AS DECIMAL(38,6)), CAST(a.NgHddLastY   AS DECIMAL(38,6))),
             (CAST(d.POP_CDD          AS DECIMAL(38,6)), CAST(a.PopCdd       AS DECIMAL(38,6))),
             (CAST(d.[30Y_POP_CDD]    AS DECIMAL(38,6)), CAST(a.PopCdd30y    AS DECIMAL(38,6))),
             (CAST(d.[10Y_POP_CDD]    AS DECIMAL(38,6)), CAST(a.PopCdd10y    AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_POP_CDD] AS DECIMAL(38,6)), CAST(a.PopCddLastY  AS DECIMAL(38,6))),
             (CAST(d.ELEC_CDD         AS DECIMAL(38,6)), CAST(a.ElecCdd      AS DECIMAL(38,6))),
             (CAST(d.[30Y_ELEC_CDD]   AS DECIMAL(38,6)), CAST(a.ElecCdd30y   AS DECIMAL(38,6))),
             (CAST(d.[10Y_ELEC_CDD]   AS DECIMAL(38,6)), CAST(a.ElecCdd10y   AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_ELEC_CDD] AS DECIMAL(38,6)), CAST(a.ElecCddLastY AS DECIMAL(38,6))),
             (CAST(d.IS_FORECAST      AS DECIMAL(38,6)), CAST(a.IsForecast   AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ObservationDate, d.[Date]
   ) g),
  N'Only degree-day table with an arm counterpart (loader pulls subregion=national only). IS_FORECAST compared as 0/1 within @Tol (valid for @Tol<1). dbo weight/POP_HDD columns do not exist here.';

IF @Detail = 1
BEGIN
    SELECT 'USNationalDegreeDay' AS T, 'MissingInArm' AS Issue, d.ObservationDate, d.[Date] AS Dates
    FROM dbo.USNationalDegreeDay d
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.NationalDegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date]);
    SELECT 'USNationalDegreeDay' AS T, 'ExtraInArm' AS Issue, a.RunDate, a.Dates
    FROM arm.NationalDegreeDays a
    WHERE (@Date IS NULL OR a.RunDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.USNationalDegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates);
    SELECT 'USNationalDegreeDay' AS T, d.ObservationDate, d.[Date] AS Dates,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.USNationalDegreeDay d JOIN arm.NationalDegreeDays a
         ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date]
    CROSS APPLY (VALUES
        ('NgHdd',       CAST(d.NG_HDD AS DECIMAL(38,6)),           CAST(a.NgHdd AS DECIMAL(38,6))),
        ('NgHdd30y',    CAST(d.[30Y_NG_HDD] AS DECIMAL(38,6)),     CAST(a.NgHdd30y AS DECIMAL(38,6))),
        ('NgHdd10y',    CAST(d.[10Y_NG_HDD] AS DECIMAL(38,6)),     CAST(a.NgHdd10y AS DECIMAL(38,6))),
        ('NgHddLastY',  CAST(d.[LAST_Y_NG_HDD] AS DECIMAL(38,6)),  CAST(a.NgHddLastY AS DECIMAL(38,6))),
        ('PopCdd',      CAST(d.POP_CDD AS DECIMAL(38,6)),          CAST(a.PopCdd AS DECIMAL(38,6))),
        ('PopCdd30y',   CAST(d.[30Y_POP_CDD] AS DECIMAL(38,6)),    CAST(a.PopCdd30y AS DECIMAL(38,6))),
        ('PopCdd10y',   CAST(d.[10Y_POP_CDD] AS DECIMAL(38,6)),    CAST(a.PopCdd10y AS DECIMAL(38,6))),
        ('PopCddLastY', CAST(d.[LAST_Y_POP_CDD] AS DECIMAL(38,6)), CAST(a.PopCddLastY AS DECIMAL(38,6))),
        ('ElecCdd',     CAST(d.ELEC_CDD AS DECIMAL(38,6)),         CAST(a.ElecCdd AS DECIMAL(38,6))),
        ('ElecCdd30y',  CAST(d.[30Y_ELEC_CDD] AS DECIMAL(38,6)),   CAST(a.ElecCdd30y AS DECIMAL(38,6))),
        ('ElecCdd10y',  CAST(d.[10Y_ELEC_CDD] AS DECIMAL(38,6)),   CAST(a.ElecCdd10y AS DECIMAL(38,6))),
        ('ElecCddLastY',CAST(d.[LAST_Y_ELEC_CDD] AS DECIMAL(38,6)),CAST(a.ElecCddLastY AS DECIMAL(38,6))),
        ('IsForecast',  CAST(d.IS_FORECAST AS DECIMAL(38,6)),      CAST(a.IsForecast AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ObservationDate, d.[Date], x.ColName;
END
GO

-- =============================================================================
-- 8b. dbo.StateDegreeDayObservation  ->  (none)   NO ARM COUNTERPART
--     subregion 'state' is still NOT requested by the loader. Row count only; NO join
--     fabricated. @Date scopes the dbo count by ObservationDate.
-- =============================================================================
DECLARE @Date DATE = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 9, 'StateDegreeDayObservation', 'NO ARM COUNTERPART',
  (SELECT COUNT(1) FROM dbo.StateDegreeDayObservation d WHERE (@Date IS NULL OR d.ObservationDate = @Date)), NULL, NULL, NULL, NULL, NULL,
  N'subregion=state NOT requested by loader. Unmapped: POP_HDD family, POP_CDD family, IS_FORECAST, GAS_WEIGHT, ELCT_WEIGHT, POP_WEIGHT, RegionName, State.';
GO

-- =============================================================================
-- 10. dbo.US5RegionDegreeDay  ->  arm.Regions5DegreeDays
--     Join dbo.ObservationDate->arm.RunDate, dbo.Date->arm.Dates, dbo.Region->arm.RegionName.
--     ALL dbo cols map: NG/POP/ELEC families (30Y/10Y/LAST_Y), IS_FORECAST (0/1 within @Tol),
--     GAS_WEIGHT->GasWeight, ELCT_WEIGHT->ElctWeight, POP_WEIGHT->PopWeight.
--     @Date scopes dbo by ObservationDate, arm by RunDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 10, 'US5RegionDegreeDay -> Regions5DegreeDays', 'MAPPED',
  (SELECT COUNT(1) FROM dbo.US5RegionDegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM arm.Regions5DegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date)),
  (SELECT COUNT(1) FROM dbo.US5RegionDegreeDay d JOIN arm.Regions5DegreeDays a
        ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM dbo.US5RegionDegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.Regions5DegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region)),
  (SELECT COUNT(1) FROM arm.Regions5DegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.US5RegionDegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates AND d.Region = a.RegionName)),
  (SELECT COUNT(1) FROM (
        SELECT d.ObservationDate, d.[Date], d.Region
        FROM dbo.US5RegionDegreeDay d JOIN arm.Regions5DegreeDays a
             ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
        CROSS APPLY (VALUES
             (CAST(d.NG_HDD            AS DECIMAL(38,6)), CAST(a.NgHdd        AS DECIMAL(38,6))),
             (CAST(d.[30Y_NG_HDD]      AS DECIMAL(38,6)), CAST(a.NgHdd30y     AS DECIMAL(38,6))),
             (CAST(d.[10Y_NG_HDD]      AS DECIMAL(38,6)), CAST(a.NgHdd10y     AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_NG_HDD]   AS DECIMAL(38,6)), CAST(a.NgHddLastY   AS DECIMAL(38,6))),
             (CAST(d.POP_CDD           AS DECIMAL(38,6)), CAST(a.PopCdd       AS DECIMAL(38,6))),
             (CAST(d.[30Y_POP_CDD]     AS DECIMAL(38,6)), CAST(a.PopCdd30y    AS DECIMAL(38,6))),
             (CAST(d.[10Y_POP_CDD]     AS DECIMAL(38,6)), CAST(a.PopCdd10y    AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_POP_CDD]  AS DECIMAL(38,6)), CAST(a.PopCddLastY  AS DECIMAL(38,6))),
             (CAST(d.ELEC_CDD          AS DECIMAL(38,6)), CAST(a.ElecCdd      AS DECIMAL(38,6))),
             (CAST(d.[30Y_ELEC_CDD]    AS DECIMAL(38,6)), CAST(a.ElecCdd30y   AS DECIMAL(38,6))),
             (CAST(d.[10Y_ELEC_CDD]    AS DECIMAL(38,6)), CAST(a.ElecCdd10y   AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_ELEC_CDD] AS DECIMAL(38,6)), CAST(a.ElecCddLastY AS DECIMAL(38,6))),
             (CAST(d.IS_FORECAST       AS DECIMAL(38,6)), CAST(a.IsForecast   AS DECIMAL(38,6))),
             (CAST(d.GAS_WEIGHT        AS DECIMAL(38,6)), CAST(a.GasWeight    AS DECIMAL(38,6))),
             (CAST(d.ELCT_WEIGHT       AS DECIMAL(38,6)), CAST(a.ElctWeight   AS DECIMAL(38,6))),
             (CAST(d.POP_WEIGHT        AS DECIMAL(38,6)), CAST(a.PopWeight    AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ObservationDate, d.[Date], d.Region
   ) g),
  N'Join +RegionName (dbo.Region->arm.RegionName). ALL dbo cols map incl. GAS/ELCT/POP weights; IS_FORECAST as 0/1 within @Tol. REGION-LABEL RISK: label spelling/format diffs surface as Missing/Extra (arm=raw CWG REGION_NAME: Pacific/Mountain/South Central/Midwest/East). Confirm label equivalence.';

IF @Detail = 1
BEGIN
    SELECT 'US5RegionDegreeDay' AS T, 'MissingInArm' AS Issue, d.ObservationDate, d.[Date] AS Dates, d.Region
    FROM dbo.US5RegionDegreeDay d
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.Regions5DegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region);
    SELECT 'US5RegionDegreeDay' AS T, 'ExtraInArm' AS Issue, a.RunDate, a.Dates, a.RegionName
    FROM arm.Regions5DegreeDays a
    WHERE (@Date IS NULL OR a.RunDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.US5RegionDegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates AND d.Region = a.RegionName);
    SELECT 'US5RegionDegreeDay' AS T, d.ObservationDate, d.[Date] AS Dates, d.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.US5RegionDegreeDay d JOIN arm.Regions5DegreeDays a
         ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
    CROSS APPLY (VALUES
        ('NgHdd',        CAST(d.NG_HDD AS DECIMAL(38,6)),            CAST(a.NgHdd AS DECIMAL(38,6))),
        ('NgHdd30y',     CAST(d.[30Y_NG_HDD] AS DECIMAL(38,6)),      CAST(a.NgHdd30y AS DECIMAL(38,6))),
        ('NgHdd10y',     CAST(d.[10Y_NG_HDD] AS DECIMAL(38,6)),      CAST(a.NgHdd10y AS DECIMAL(38,6))),
        ('NgHddLastY',   CAST(d.[LAST_Y_NG_HDD] AS DECIMAL(38,6)),   CAST(a.NgHddLastY AS DECIMAL(38,6))),
        ('PopCdd',       CAST(d.POP_CDD AS DECIMAL(38,6)),           CAST(a.PopCdd AS DECIMAL(38,6))),
        ('PopCdd30y',    CAST(d.[30Y_POP_CDD] AS DECIMAL(38,6)),     CAST(a.PopCdd30y AS DECIMAL(38,6))),
        ('PopCdd10y',    CAST(d.[10Y_POP_CDD] AS DECIMAL(38,6)),     CAST(a.PopCdd10y AS DECIMAL(38,6))),
        ('PopCddLastY',  CAST(d.[LAST_Y_POP_CDD] AS DECIMAL(38,6)),  CAST(a.PopCddLastY AS DECIMAL(38,6))),
        ('ElecCdd',      CAST(d.ELEC_CDD AS DECIMAL(38,6)),          CAST(a.ElecCdd AS DECIMAL(38,6))),
        ('ElecCdd30y',   CAST(d.[30Y_ELEC_CDD] AS DECIMAL(38,6)),    CAST(a.ElecCdd30y AS DECIMAL(38,6))),
        ('ElecCdd10y',   CAST(d.[10Y_ELEC_CDD] AS DECIMAL(38,6)),    CAST(a.ElecCdd10y AS DECIMAL(38,6))),
        ('ElecCddLastY', CAST(d.[LAST_Y_ELEC_CDD] AS DECIMAL(38,6)), CAST(a.ElecCddLastY AS DECIMAL(38,6))),
        ('IsForecast',   CAST(d.IS_FORECAST AS DECIMAL(38,6)),       CAST(a.IsForecast AS DECIMAL(38,6))),
        ('GasWeight',    CAST(d.GAS_WEIGHT AS DECIMAL(38,6)),        CAST(a.GasWeight AS DECIMAL(38,6))),
        ('ElctWeight',   CAST(d.ELCT_WEIGHT AS DECIMAL(38,6)),       CAST(a.ElctWeight AS DECIMAL(38,6))),
        ('PopWeight',    CAST(d.POP_WEIGHT AS DECIMAL(38,6)),        CAST(a.PopWeight AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ObservationDate, d.[Date], d.Region, x.ColName;
END
GO

-- =============================================================================
-- 11. dbo.US9RegionDegreeDay  ->  arm.Regions9DegreeDays
--     IDENTICAL column set / join to #10 (Regions9 mirrors Regions5). Join
--     dbo.ObservationDate->arm.RunDate, dbo.Date->arm.Dates, dbo.Region->arm.RegionName.
--     @Date scopes dbo by ObservationDate, arm by RunDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 11, 'US9RegionDegreeDay -> Regions9DegreeDays', 'MAPPED',
  (SELECT COUNT(1) FROM dbo.US9RegionDegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM arm.Regions9DegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date)),
  (SELECT COUNT(1) FROM dbo.US9RegionDegreeDay d JOIN arm.Regions9DegreeDays a
        ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM dbo.US9RegionDegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.Regions9DegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region)),
  (SELECT COUNT(1) FROM arm.Regions9DegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.US9RegionDegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates AND d.Region = a.RegionName)),
  (SELECT COUNT(1) FROM (
        SELECT d.ObservationDate, d.[Date], d.Region
        FROM dbo.US9RegionDegreeDay d JOIN arm.Regions9DegreeDays a
             ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
        CROSS APPLY (VALUES
             (CAST(d.NG_HDD            AS DECIMAL(38,6)), CAST(a.NgHdd        AS DECIMAL(38,6))),
             (CAST(d.[30Y_NG_HDD]      AS DECIMAL(38,6)), CAST(a.NgHdd30y     AS DECIMAL(38,6))),
             (CAST(d.[10Y_NG_HDD]      AS DECIMAL(38,6)), CAST(a.NgHdd10y     AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_NG_HDD]   AS DECIMAL(38,6)), CAST(a.NgHddLastY   AS DECIMAL(38,6))),
             (CAST(d.POP_CDD           AS DECIMAL(38,6)), CAST(a.PopCdd       AS DECIMAL(38,6))),
             (CAST(d.[30Y_POP_CDD]     AS DECIMAL(38,6)), CAST(a.PopCdd30y    AS DECIMAL(38,6))),
             (CAST(d.[10Y_POP_CDD]     AS DECIMAL(38,6)), CAST(a.PopCdd10y    AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_POP_CDD]  AS DECIMAL(38,6)), CAST(a.PopCddLastY  AS DECIMAL(38,6))),
             (CAST(d.ELEC_CDD          AS DECIMAL(38,6)), CAST(a.ElecCdd      AS DECIMAL(38,6))),
             (CAST(d.[30Y_ELEC_CDD]    AS DECIMAL(38,6)), CAST(a.ElecCdd30y   AS DECIMAL(38,6))),
             (CAST(d.[10Y_ELEC_CDD]    AS DECIMAL(38,6)), CAST(a.ElecCdd10y   AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_ELEC_CDD] AS DECIMAL(38,6)), CAST(a.ElecCddLastY AS DECIMAL(38,6))),
             (CAST(d.IS_FORECAST       AS DECIMAL(38,6)), CAST(a.IsForecast   AS DECIMAL(38,6))),
             (CAST(d.GAS_WEIGHT        AS DECIMAL(38,6)), CAST(a.GasWeight    AS DECIMAL(38,6))),
             (CAST(d.ELCT_WEIGHT       AS DECIMAL(38,6)), CAST(a.ElctWeight   AS DECIMAL(38,6))),
             (CAST(d.POP_WEIGHT        AS DECIMAL(38,6)), CAST(a.PopWeight    AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ObservationDate, d.[Date], d.Region
   ) g),
  N'Join +RegionName (dbo.Region->arm.RegionName). ALL dbo cols map incl. GAS/ELCT/POP weights; IS_FORECAST as 0/1 within @Tol. REGION-LABEL RISK: arm=raw CWG REGION_NAME (9 Census divisions UPPERCASE: NEW ENGLAND/MIDDLE ATLANTIC/E N CENTRAL/W N CENTRAL/SOUTH ATLANTIC/E S CENTRAL/W S CENTRAL/MOUNTAIN/PACIFIC). Confirm label equivalence.';

IF @Detail = 1
BEGIN
    SELECT 'US9RegionDegreeDay' AS T, 'MissingInArm' AS Issue, d.ObservationDate, d.[Date] AS Dates, d.Region
    FROM dbo.US9RegionDegreeDay d
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.Regions9DegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region);
    SELECT 'US9RegionDegreeDay' AS T, 'ExtraInArm' AS Issue, a.RunDate, a.Dates, a.RegionName
    FROM arm.Regions9DegreeDays a
    WHERE (@Date IS NULL OR a.RunDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.US9RegionDegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates AND d.Region = a.RegionName);
    SELECT 'US9RegionDegreeDay' AS T, d.ObservationDate, d.[Date] AS Dates, d.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.US9RegionDegreeDay d JOIN arm.Regions9DegreeDays a
         ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
    CROSS APPLY (VALUES
        ('NgHdd',        CAST(d.NG_HDD AS DECIMAL(38,6)),            CAST(a.NgHdd AS DECIMAL(38,6))),
        ('NgHdd30y',     CAST(d.[30Y_NG_HDD] AS DECIMAL(38,6)),      CAST(a.NgHdd30y AS DECIMAL(38,6))),
        ('NgHdd10y',     CAST(d.[10Y_NG_HDD] AS DECIMAL(38,6)),      CAST(a.NgHdd10y AS DECIMAL(38,6))),
        ('NgHddLastY',   CAST(d.[LAST_Y_NG_HDD] AS DECIMAL(38,6)),   CAST(a.NgHddLastY AS DECIMAL(38,6))),
        ('PopCdd',       CAST(d.POP_CDD AS DECIMAL(38,6)),           CAST(a.PopCdd AS DECIMAL(38,6))),
        ('PopCdd30y',    CAST(d.[30Y_POP_CDD] AS DECIMAL(38,6)),     CAST(a.PopCdd30y AS DECIMAL(38,6))),
        ('PopCdd10y',    CAST(d.[10Y_POP_CDD] AS DECIMAL(38,6)),     CAST(a.PopCdd10y AS DECIMAL(38,6))),
        ('PopCddLastY',  CAST(d.[LAST_Y_POP_CDD] AS DECIMAL(38,6)),  CAST(a.PopCddLastY AS DECIMAL(38,6))),
        ('ElecCdd',      CAST(d.ELEC_CDD AS DECIMAL(38,6)),          CAST(a.ElecCdd AS DECIMAL(38,6))),
        ('ElecCdd30y',   CAST(d.[30Y_ELEC_CDD] AS DECIMAL(38,6)),    CAST(a.ElecCdd30y AS DECIMAL(38,6))),
        ('ElecCdd10y',   CAST(d.[10Y_ELEC_CDD] AS DECIMAL(38,6)),    CAST(a.ElecCdd10y AS DECIMAL(38,6))),
        ('ElecCddLastY', CAST(d.[LAST_Y_ELEC_CDD] AS DECIMAL(38,6)), CAST(a.ElecCddLastY AS DECIMAL(38,6))),
        ('IsForecast',   CAST(d.IS_FORECAST AS DECIMAL(38,6)),       CAST(a.IsForecast AS DECIMAL(38,6))),
        ('GasWeight',    CAST(d.GAS_WEIGHT AS DECIMAL(38,6)),        CAST(a.GasWeight AS DECIMAL(38,6))),
        ('ElctWeight',   CAST(d.ELCT_WEIGHT AS DECIMAL(38,6)),       CAST(a.ElctWeight AS DECIMAL(38,6))),
        ('PopWeight',    CAST(d.POP_WEIGHT AS DECIMAL(38,6)),        CAST(a.PopWeight AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ObservationDate, d.[Date], d.Region, x.ColName;
END
GO

-- =============================================================================
-- 12. dbo.USISODegreeDay  ->  arm.ISODegreeDays
--     DIVERGENT layout: HDD family is POP_HDD (NOT NG_HDD); NO ELEC family, NO weights.
--     Join dbo.ObservationDate->arm.RunDate, dbo.Date->arm.Dates, dbo.Region->arm.RegionName.
--     Map: POP_HDD->PopHdd (+30Y/10Y/LAST_Y), POP_CDD->PopCdd (+30Y/10Y/LAST_Y), IS_FORECAST.
--     @Date scopes dbo by ObservationDate, arm by RunDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 12, 'USISODegreeDay -> ISODegreeDays', 'MAPPED',
  (SELECT COUNT(1) FROM dbo.USISODegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM arm.ISODegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date)),
  (SELECT COUNT(1) FROM dbo.USISODegreeDay d JOIN arm.ISODegreeDays a
        ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)),
  (SELECT COUNT(1) FROM dbo.USISODegreeDay d WHERE (@Date IS NULL OR d.ObservationDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.ISODegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region)),
  (SELECT COUNT(1) FROM arm.ISODegreeDays a WHERE (@Date IS NULL OR a.RunDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.USISODegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates AND d.Region = a.RegionName)),
  (SELECT COUNT(1) FROM (
        SELECT d.ObservationDate, d.[Date], d.Region
        FROM dbo.USISODegreeDay d JOIN arm.ISODegreeDays a
             ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
        CROSS APPLY (VALUES
             (CAST(d.POP_HDD           AS DECIMAL(38,6)), CAST(a.PopHdd       AS DECIMAL(38,6))),
             (CAST(d.[30Y_POP_HDD]     AS DECIMAL(38,6)), CAST(a.PopHdd30y    AS DECIMAL(38,6))),
             (CAST(d.[10Y_POP_HDD]     AS DECIMAL(38,6)), CAST(a.PopHdd10y    AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_POP_HDD]  AS DECIMAL(38,6)), CAST(a.PopHddLastY  AS DECIMAL(38,6))),
             (CAST(d.POP_CDD           AS DECIMAL(38,6)), CAST(a.PopCdd       AS DECIMAL(38,6))),
             (CAST(d.[30Y_POP_CDD]     AS DECIMAL(38,6)), CAST(a.PopCdd30y    AS DECIMAL(38,6))),
             (CAST(d.[10Y_POP_CDD]     AS DECIMAL(38,6)), CAST(a.PopCdd10y    AS DECIMAL(38,6))),
             (CAST(d.[LAST_Y_POP_CDD]  AS DECIMAL(38,6)), CAST(a.PopCddLastY  AS DECIMAL(38,6))),
             (CAST(d.IS_FORECAST       AS DECIMAL(38,6)), CAST(a.IsForecast   AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ObservationDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ObservationDate, d.[Date], d.Region
   ) g),
  N'DIVERGENT: HDD family is POP_HDD (not NG); NO ELEC / weights. Join +RegionName (dbo.Region->arm.RegionName). IS_FORECAST as 0/1 within @Tol. REGION-LABEL RISK: arm=raw CWG REGION_NAME (21 ISO labels incl. CAISO NORTH/CAISO SOUTH/EAST PJM/WEST PJM/UPPER MISO/LOWER MISO). Confirm label equivalence.';

IF @Detail = 1
BEGIN
    SELECT 'USISODegreeDay' AS T, 'MissingInArm' AS Issue, d.ObservationDate, d.[Date] AS Dates, d.Region
    FROM dbo.USISODegreeDay d
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.ISODegreeDays a WHERE a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region);
    SELECT 'USISODegreeDay' AS T, 'ExtraInArm' AS Issue, a.RunDate, a.Dates, a.RegionName
    FROM arm.ISODegreeDays a
    WHERE (@Date IS NULL OR a.RunDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.USISODegreeDay d WHERE d.ObservationDate = a.RunDate AND d.[Date] = a.Dates AND d.Region = a.RegionName);
    SELECT 'USISODegreeDay' AS T, d.ObservationDate, d.[Date] AS Dates, d.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.USISODegreeDay d JOIN arm.ISODegreeDays a
         ON a.RunDate = d.ObservationDate AND a.Dates = d.[Date] AND a.RegionName = d.Region
    CROSS APPLY (VALUES
        ('PopHdd',       CAST(d.POP_HDD AS DECIMAL(38,6)),           CAST(a.PopHdd AS DECIMAL(38,6))),
        ('PopHdd30y',    CAST(d.[30Y_POP_HDD] AS DECIMAL(38,6)),     CAST(a.PopHdd30y AS DECIMAL(38,6))),
        ('PopHdd10y',    CAST(d.[10Y_POP_HDD] AS DECIMAL(38,6)),     CAST(a.PopHdd10y AS DECIMAL(38,6))),
        ('PopHddLastY',  CAST(d.[LAST_Y_POP_HDD] AS DECIMAL(38,6)),  CAST(a.PopHddLastY AS DECIMAL(38,6))),
        ('PopCdd',       CAST(d.POP_CDD AS DECIMAL(38,6)),           CAST(a.PopCdd AS DECIMAL(38,6))),
        ('PopCdd30y',    CAST(d.[30Y_POP_CDD] AS DECIMAL(38,6)),     CAST(a.PopCdd30y AS DECIMAL(38,6))),
        ('PopCdd10y',    CAST(d.[10Y_POP_CDD] AS DECIMAL(38,6)),     CAST(a.PopCdd10y AS DECIMAL(38,6))),
        ('PopCddLastY',  CAST(d.[LAST_Y_POP_CDD] AS DECIMAL(38,6)),  CAST(a.PopCddLastY AS DECIMAL(38,6))),
        ('IsForecast',   CAST(d.IS_FORECAST AS DECIMAL(38,6)),       CAST(a.IsForecast AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ObservationDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ObservationDate, d.[Date], d.Region, x.ColName;
END
GO

-- =============================================================================
-- 9. dbo.WindForecast  ->  arm.WindForecast     GRAIN MISMATCH - REVIEW  (see #5)
--    @Date scopes counts by ForecastDate (valid date; dbo has no InitDate).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 13, 'WindForecast', 'GRAIN MISMATCH - REVIEW',
  (SELECT COUNT(1) FROM dbo.WindForecast d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.WindForecast a WHERE (@Date IS NULL OR a.ForecastDate = @Date)),
  NULL, NULL, NULL, NULL,
  N'NOT joined: dbo UTCHourEnding (UTC, no InitDate) vs arm EST HourOfDay + InitDate. Same TZ/InitDate ambiguity as SolarForecast. @Date scopes by ForecastDate. See DETAIL for coarse (Region,ForecastDate) presence.';

IF @Detail = 1
BEGIN
    SELECT 'WindForecast' AS T, 'CoarseMissingInArm (Region,ForecastDate)' AS Issue, d.Region, d.ForecastDate
    FROM dbo.WindForecast d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.WindForecast a WHERE a.Region = d.Region AND a.ForecastDate = d.ForecastDate)
    GROUP BY d.Region, d.ForecastDate
    ORDER BY d.Region, d.ForecastDate;
END
GO

-- =============================================================================
-- 10. dbo.WindForecastSubRegion  ->  arm.WindForecastSubRegion  GRAIN MISMATCH - REVIEW
--     Same as #9, plus a SubRegion axis (present on both sides). Counts only; coarse
--     (Region, SubRegion, ForecastDate) presence probe in DETAIL.
--     @Date scopes counts by ForecastDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 14, 'WindForecastSubRegion', 'GRAIN MISMATCH - REVIEW',
  (SELECT COUNT(1) FROM dbo.WindForecastSubRegion d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.WindForecastSubRegion a WHERE (@Date IS NULL OR a.ForecastDate = @Date)),
  NULL, NULL, NULL, NULL,
  N'NOT joined: same UTC/EST + InitDate ambiguity as WindForecast. arm strips trailing " region" from SubRegion (keeps prefixes/hyphens) - confirm label equivalence too. @Date scopes by ForecastDate. See DETAIL for coarse presence.';

IF @Detail = 1
BEGIN
    SELECT 'WindForecastSubRegion' AS T, 'CoarseMissingInArm (Region,SubRegion,ForecastDate)' AS Issue, d.Region, d.SubRegion, d.ForecastDate
    FROM dbo.WindForecastSubRegion d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.WindForecastSubRegion a
                      WHERE a.Region = d.Region AND a.SubRegion = d.SubRegion AND a.ForecastDate = d.ForecastDate)
    GROUP BY d.Region, d.SubRegion, d.ForecastDate
    ORDER BY d.Region, d.SubRegion, d.ForecastDate;
END
GO

-- =============================================================================
-- 11. dbo.WindHourly (WIDE: UTCHourEnding + 10 region cols)  ->  arm.WindHourly (TALL)
--     Unpivot dbo 10 regions; join (UTCHourEnding->HourEndingUtc [DATETIME2(0)], Region).
--     arm has 12 regions (dbo 10 + NW, SW) -> NW/SW EXPECTED ExtraInArm. Value->ActualMw.
--     @Date scopes dbo at #whTall build and arm subqueries by CAST(<ts> AS DATE).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

DROP TABLE IF EXISTS #whTall;
SELECT
    HourEndingUtc = CAST(d.UTCHourEnding AS DATETIME2(0)),
    u.Region,
    Val = CAST(u.Val AS DECIMAL(38,6))
INTO #whTall
FROM dbo.WindHourly d
CROSS APPLY (VALUES
    ('CAISO', d.CAISO), ('SPP', d.SPP), ('ERCOT', d.ERCOT), ('MISO', d.MISO), ('PJM', d.PJM),
    ('NEPOOL', d.NEPOOL), ('NYISO', d.NYISO), ('BPA', d.BPA), ('IESO', d.IESO), ('AESO', d.AESO)
) u(Region, Val)
WHERE (@Date IS NULL OR CAST(d.UTCHourEnding AS DATE) = @Date);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 15, 'WindHourly', 'MAPPED (wide->tall)',
  (SELECT COUNT(1) FROM #whTall WHERE Val IS NOT NULL),
  (SELECT COUNT(1) FROM arm.WindHourly a WHERE (@Date IS NULL OR CAST(a.HourEndingUtc AS DATE) = @Date)),
  (SELECT COUNT(1) FROM #whTall t JOIN arm.WindHourly a ON a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region WHERE t.Val IS NOT NULL),
  (SELECT COUNT(1) FROM #whTall t WHERE t.Val IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM arm.WindHourly a WHERE a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region)),
  (SELECT COUNT(1) FROM arm.WindHourly a WHERE (@Date IS NULL OR CAST(a.HourEndingUtc AS DATE) = @Date) AND NOT EXISTS
        (SELECT 1 FROM #whTall t WHERE t.HourEndingUtc = a.HourEndingUtc AND t.Region = a.Region AND t.Val IS NOT NULL)),
  (SELECT COUNT(1) FROM #whTall t JOIN arm.WindHourly a ON a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region
        WHERE t.Val IS NOT NULL AND (a.ActualMw IS NULL OR ABS(t.Val - a.ActualMw) > @Tol)),
  N'dbo WIDE (10 regions) unpivoted. arm has 12 regions (adds NW,SW) -> EXPECTED ExtraInArm. dbo NULL cells ignored. DboRows = non-NULL cells.';

IF @Detail = 1
BEGIN
    SELECT 'WindHourly' AS T, 'MissingInArm' AS Issue, t.HourEndingUtc, t.Region, t.Val AS DboVal
    FROM #whTall t
    WHERE t.Val IS NOT NULL AND NOT EXISTS (SELECT 1 FROM arm.WindHourly a WHERE a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region);
    SELECT 'WindHourly' AS T, 'ExtraInArm' AS Issue, a.HourEndingUtc, a.Region, a.ActualMw AS ArmVal
    FROM arm.WindHourly a
    WHERE (@Date IS NULL OR CAST(a.HourEndingUtc AS DATE) = @Date)
      AND NOT EXISTS (SELECT 1 FROM #whTall t WHERE t.HourEndingUtc = a.HourEndingUtc AND t.Region = a.Region AND t.Val IS NOT NULL);
    SELECT 'WindHourly' AS T, t.HourEndingUtc, t.Region, t.Val AS DboVal, a.ActualMw AS ArmVal, ABS(t.Val - a.ActualMw) AS AbsDiff
    FROM #whTall t JOIN arm.WindHourly a ON a.HourEndingUtc = t.HourEndingUtc AND a.Region = t.Region
    WHERE t.Val IS NOT NULL AND (a.ActualMw IS NULL OR ABS(t.Val - a.ActualMw) > @Tol)
    ORDER BY t.HourEndingUtc, t.Region;
END
GO

-- =============================================================================
-- 12. dbo.WindTotalCapacityClimatology  ->  arm.WindTotalCapacityClimatology
--     Single block both sides. Join dbo.ForecastDate -> arm.ProductionDate + Region.
--     TotalCapacity->TotalCapacityMw; OneToFive/SixToTen/ElevenToFifteen -> Avg_1_5/6_10/11_15.
--     @Date scopes dbo by ForecastDate, arm by ProductionDate.
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 16, 'WindTotalCapacityClimatology', 'MAPPED (single block)',
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityClimatology d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.WindTotalCapacityClimatology a WHERE (@Date IS NULL OR a.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityClimatology d JOIN arm.WindTotalCapacityClimatology a
        ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region
        WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityClimatology d WHERE (@Date IS NULL OR d.ForecastDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.WindTotalCapacityClimatology a WHERE a.ProductionDate = d.ForecastDate AND a.Region = d.Region)),
  (SELECT COUNT(1) FROM arm.WindTotalCapacityClimatology a WHERE (@Date IS NULL OR a.ProductionDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.WindTotalCapacityClimatology d WHERE d.ForecastDate = a.ProductionDate AND d.Region = a.Region)),
  (SELECT COUNT(1) FROM (
        SELECT d.ForecastDate, d.Region
        FROM dbo.WindTotalCapacityClimatology d JOIN arm.WindTotalCapacityClimatology a
             ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region
        CROSS APPLY (VALUES
             (CAST(d.TotalCapacity              AS DECIMAL(38,6)), CAST(a.TotalCapacityMw AS DECIMAL(38,6))),
             (CAST(d.OneToFiveDayAverage        AS DECIMAL(38,6)), CAST(a.Avg_1_5   AS DECIMAL(38,6))),
             (CAST(d.SixToTenDayAverage         AS DECIMAL(38,6)), CAST(a.Avg_6_10  AS DECIMAL(38,6))),
             (CAST(d.ElevenToFifteenDayAverage  AS DECIMAL(38,6)), CAST(a.Avg_11_15 AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ForecastDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ForecastDate, d.Region
   ) g),
  N'Join (ForecastDate->ProductionDate, Region). Assumes dbo avg cols are % (as arm). Systematic unit diff -> uniform ValueMismatches.';

IF @Detail = 1
BEGIN
    SELECT 'WindTotalCapacityClimatology' AS T, 'MissingInArm' AS Issue, d.ForecastDate, d.Region
    FROM dbo.WindTotalCapacityClimatology d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.WindTotalCapacityClimatology a WHERE a.ProductionDate = d.ForecastDate AND a.Region = d.Region);
    SELECT 'WindTotalCapacityClimatology' AS T, 'ExtraInArm' AS Issue, a.ProductionDate, a.Region
    FROM arm.WindTotalCapacityClimatology a
    WHERE (@Date IS NULL OR a.ProductionDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.WindTotalCapacityClimatology d WHERE d.ForecastDate = a.ProductionDate AND d.Region = a.Region);
    SELECT 'WindTotalCapacityClimatology' AS T, d.ForecastDate, d.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.WindTotalCapacityClimatology d JOIN arm.WindTotalCapacityClimatology a
         ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region
    CROSS APPLY (VALUES
        ('TotalCapacityMw', CAST(d.TotalCapacity AS DECIMAL(38,6)),             CAST(a.TotalCapacityMw AS DECIMAL(38,6))),
        ('Avg_1_5',         CAST(d.OneToFiveDayAverage AS DECIMAL(38,6)),       CAST(a.Avg_1_5 AS DECIMAL(38,6))),
        ('Avg_6_10',        CAST(d.SixToTenDayAverage AS DECIMAL(38,6)),        CAST(a.Avg_6_10 AS DECIMAL(38,6))),
        ('Avg_11_15',       CAST(d.ElevenToFifteenDayAverage AS DECIMAL(38,6)), CAST(a.Avg_11_15 AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ForecastDate, d.Region, x.ColName;
END
GO

-- =============================================================================
-- 13. dbo.WindTotalCapacityMW  ->  arm.WindTotalCapacityMW   (dbo <-> arm Block='Current')
--     dbo has NO block; assume dbo == arm Block='Current'. Join (ForecastDate->
--     ProductionDate, Region, Block='Current'). arm Yesterday/Change blocks are
--     arm-only (informational). Values are MW (DECIMAL(12,4)).
--     @Date scopes dbo by ForecastDate, arm by ProductionDate (Block='Current' kept).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 17, 'WindTotalCapacityMW', 'MAPPED (Block=Current)',
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityMW d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.WindTotalCapacityMW a WHERE (@Date IS NULL OR a.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityMW d JOIN arm.WindTotalCapacityMW a
        ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current'
        WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityMW d WHERE (@Date IS NULL OR d.ForecastDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.WindTotalCapacityMW a WHERE a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current')),
  (SELECT COUNT(1) FROM arm.WindTotalCapacityMW a WHERE a.Block = 'Current' AND (@Date IS NULL OR a.ProductionDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.WindTotalCapacityMW d WHERE d.ForecastDate = a.ProductionDate AND d.Region = a.Region)),
  (SELECT COUNT(1) FROM (
        SELECT d.ForecastDate, d.Region
        FROM dbo.WindTotalCapacityMW d JOIN arm.WindTotalCapacityMW a
             ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current'
        CROSS APPLY (VALUES
             (CAST(d.TotalCapacity              AS DECIMAL(38,6)), CAST(a.TotalCapacityMw AS DECIMAL(38,6))),
             (CAST(d.OneToFiveDayAverage        AS DECIMAL(38,6)), CAST(a.Avg_1_5   AS DECIMAL(38,6))),
             (CAST(d.SixToTenDayAverage         AS DECIMAL(38,6)), CAST(a.Avg_6_10  AS DECIMAL(38,6))),
             (CAST(d.ElevenToFifteenDayAverage  AS DECIMAL(38,6)), CAST(a.Avg_11_15 AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ForecastDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ForecastDate, d.Region
   ) g),
  N'ExtraInArm counts arm Block=Current only. arm Block=Yesterday/Change are arm-only grain (not in dbo) and intentionally NOT compared. ArmRows = all blocks.';

IF @Detail = 1
BEGIN
    SELECT 'WindTotalCapacityMW' AS T, 'MissingInArm' AS Issue, d.ForecastDate, d.Region
    FROM dbo.WindTotalCapacityMW d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.WindTotalCapacityMW a WHERE a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current');
    SELECT 'WindTotalCapacityMW' AS T, 'ExtraInArm (Block=Current)' AS Issue, a.ProductionDate, a.Region
    FROM arm.WindTotalCapacityMW a
    WHERE a.Block = 'Current' AND (@Date IS NULL OR a.ProductionDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.WindTotalCapacityMW d WHERE d.ForecastDate = a.ProductionDate AND d.Region = a.Region);
    SELECT 'WindTotalCapacityMW' AS T, d.ForecastDate, d.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.WindTotalCapacityMW d JOIN arm.WindTotalCapacityMW a
         ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current'
    CROSS APPLY (VALUES
        ('TotalCapacityMw', CAST(d.TotalCapacity AS DECIMAL(38,6)),             CAST(a.TotalCapacityMw AS DECIMAL(38,6))),
        ('Avg_1_5',         CAST(d.OneToFiveDayAverage AS DECIMAL(38,6)),       CAST(a.Avg_1_5 AS DECIMAL(38,6))),
        ('Avg_6_10',        CAST(d.SixToTenDayAverage AS DECIMAL(38,6)),        CAST(a.Avg_6_10 AS DECIMAL(38,6))),
        ('Avg_11_15',       CAST(d.ElevenToFifteenDayAverage AS DECIMAL(38,6)), CAST(a.Avg_11_15 AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ForecastDate, d.Region, x.ColName;
END
GO

-- =============================================================================
-- 14. dbo.WindTotalCapacityPct  ->  arm.WindTotalCapacityPct   (dbo <-> arm Block='Current')
--     Same structure as #13; avg columns are % (DECIMAL(6,2)); TotalCapacity is MW.
--     @Date scopes dbo by ForecastDate, arm by ProductionDate (Block='Current' kept).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 18, 'WindTotalCapacityPct', 'MAPPED (Block=Current)',
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityPct d WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM arm.WindTotalCapacityPct a WHERE (@Date IS NULL OR a.ProductionDate = @Date)),
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityPct d JOIN arm.WindTotalCapacityPct a
        ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current'
        WHERE (@Date IS NULL OR d.ForecastDate = @Date)),
  (SELECT COUNT(1) FROM dbo.WindTotalCapacityPct d WHERE (@Date IS NULL OR d.ForecastDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM arm.WindTotalCapacityPct a WHERE a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current')),
  (SELECT COUNT(1) FROM arm.WindTotalCapacityPct a WHERE a.Block = 'Current' AND (@Date IS NULL OR a.ProductionDate = @Date) AND NOT EXISTS
        (SELECT 1 FROM dbo.WindTotalCapacityPct d WHERE d.ForecastDate = a.ProductionDate AND d.Region = a.Region)),
  (SELECT COUNT(1) FROM (
        SELECT d.ForecastDate, d.Region
        FROM dbo.WindTotalCapacityPct d JOIN arm.WindTotalCapacityPct a
             ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current'
        CROSS APPLY (VALUES
             (CAST(d.TotalCapacity              AS DECIMAL(38,6)), CAST(a.TotalCapacityMw AS DECIMAL(38,6))),
             (CAST(d.OneToFiveDayAverage        AS DECIMAL(38,6)), CAST(a.Avg_1_5   AS DECIMAL(38,6))),
             (CAST(d.SixToTenDayAverage         AS DECIMAL(38,6)), CAST(a.Avg_6_10  AS DECIMAL(38,6))),
             (CAST(d.ElevenToFifteenDayAverage  AS DECIMAL(38,6)), CAST(a.Avg_11_15 AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (@Date IS NULL OR d.ForecastDate = @Date)
          AND ((v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
            OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol))
        GROUP BY d.ForecastDate, d.Region
   ) g),
  N'Avg cols are % (arm DECIMAL(6,2)); TotalCapacity is MW. ExtraInArm = arm Block=Current only; Yesterday/Change are arm-only. ArmRows = all blocks.';

IF @Detail = 1
BEGIN
    SELECT 'WindTotalCapacityPct' AS T, 'MissingInArm' AS Issue, d.ForecastDate, d.Region
    FROM dbo.WindTotalCapacityPct d
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM arm.WindTotalCapacityPct a WHERE a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current');
    SELECT 'WindTotalCapacityPct' AS T, 'ExtraInArm (Block=Current)' AS Issue, a.ProductionDate, a.Region
    FROM arm.WindTotalCapacityPct a
    WHERE a.Block = 'Current' AND (@Date IS NULL OR a.ProductionDate = @Date)
      AND NOT EXISTS (SELECT 1 FROM dbo.WindTotalCapacityPct d WHERE d.ForecastDate = a.ProductionDate AND d.Region = a.Region);
    SELECT 'WindTotalCapacityPct' AS T, d.ForecastDate, d.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.WindTotalCapacityPct d JOIN arm.WindTotalCapacityPct a
         ON a.ProductionDate = d.ForecastDate AND a.Region = d.Region AND a.Block = 'Current'
    CROSS APPLY (VALUES
        ('TotalCapacityMw', CAST(d.TotalCapacity AS DECIMAL(38,6)),             CAST(a.TotalCapacityMw AS DECIMAL(38,6))),
        ('Avg_1_5',         CAST(d.OneToFiveDayAverage AS DECIMAL(38,6)),       CAST(a.Avg_1_5 AS DECIMAL(38,6))),
        ('Avg_6_10',        CAST(d.SixToTenDayAverage AS DECIMAL(38,6)),        CAST(a.Avg_6_10 AS DECIMAL(38,6))),
        ('Avg_11_15',       CAST(d.ElevenToFifteenDayAverage AS DECIMAL(38,6)), CAST(a.Avg_11_15 AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (@Date IS NULL OR d.ForecastDate = @Date)
      AND ((x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
        OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol))
    ORDER BY d.ForecastDate, d.Region, x.ColName;
END
GO

-- =============================================================================
-- 15. dbo.Station  ->  arm.Station
--     dbo PK (Identifier); arm PK (Region, Identifier). Join on Identifier only
--     (arm adds Region -> 1:N if an Identifier appears in >1 geography). DECIMAL
--     check: Latitude->Lat, Longitude->Lon (@Tol; loose for coordinates - see header).
--     Text columns (WMOID/WBAN/GHCND/Name/State/Country) shown in DETAIL only;
--     dbo.MappingId has no arm column (unmapped).
--     UNDATED reference table -> @Date does NOT apply (always compared in full).
-- =============================================================================
DECLARE @Tol DECIMAL(9,4) = (SELECT Tol FROM #Cfg);
DECLARE @Detail BIT       = (SELECT Detail FROM #Cfg);
DECLARE @Date DATE        = (SELECT FilterDate FROM #Cfg);

INSERT #Summary (Ord, TableName, MappingStatus, DboRows, ArmRows, MatchedKeys, MissingInArm, ExtraInArm, ValueMismatches, Notes)
SELECT 19, 'Station', 'MAPPED (arm adds Region)',
  (SELECT COUNT(1) FROM dbo.Station),
  (SELECT COUNT(1) FROM arm.Station),
  (SELECT COUNT(1) FROM dbo.Station d JOIN arm.Station a ON a.Identifier = d.Identifier),
  (SELECT COUNT(1) FROM dbo.Station d WHERE NOT EXISTS (SELECT 1 FROM arm.Station a WHERE a.Identifier = d.Identifier)),
  (SELECT COUNT(1) FROM arm.Station a WHERE NOT EXISTS (SELECT 1 FROM dbo.Station d WHERE d.Identifier = a.Identifier)),
  (SELECT COUNT(1) FROM (
        SELECT d.Identifier, a.Region
        FROM dbo.Station d JOIN arm.Station a ON a.Identifier = d.Identifier
        CROSS APPLY (VALUES
             (CAST(d.Latitude  AS DECIMAL(38,6)), CAST(a.Lat AS DECIMAL(38,6))),
             (CAST(d.Longitude AS DECIMAL(38,6)), CAST(a.Lon AS DECIMAL(38,6)))
        ) v(DboVal, ArmVal)
        WHERE (v.DboVal IS NULL AND v.ArmVal IS NOT NULL)
           OR (v.DboVal IS NOT NULL AND v.ArmVal IS NULL)
           OR (v.DboVal IS NOT NULL AND v.ArmVal IS NOT NULL AND ABS(v.DboVal - v.ArmVal) > @Tol)
        GROUP BY d.Identifier, a.Region
   ) g),
  N'Join on Identifier (arm adds Region; 1:N if id in >1 geography). ValueMismatches = lat/lon (@Tol degrees - LOOSE; tighten @Tol or read DETAIL). Text cols compared in DETAIL. dbo.MappingId has no arm column.'
  + CASE WHEN @Date IS NOT NULL THEN N' (@Date not applicable - undated reference; compared in full)' ELSE N'' END;

IF @Detail = 1
BEGIN
    SELECT 'Station' AS T, 'MissingInArm' AS Issue, d.Identifier
    FROM dbo.Station d WHERE NOT EXISTS (SELECT 1 FROM arm.Station a WHERE a.Identifier = d.Identifier);
    SELECT 'Station' AS T, 'ExtraInArm' AS Issue, a.Region, a.Identifier
    FROM arm.Station a WHERE NOT EXISTS (SELECT 1 FROM dbo.Station d WHERE d.Identifier = a.Identifier);
    -- lat/lon decimal mismatches
    SELECT 'Station' AS T, d.Identifier, a.Region,
           x.ColName, x.DboVal, x.ArmVal, ABS(x.DboVal - x.ArmVal) AS AbsDiff
    FROM dbo.Station d JOIN arm.Station a ON a.Identifier = d.Identifier
    CROSS APPLY (VALUES
        ('Lat', CAST(d.Latitude AS DECIMAL(38,6)),  CAST(a.Lat AS DECIMAL(38,6))),
        ('Lon', CAST(d.Longitude AS DECIMAL(38,6)), CAST(a.Lon AS DECIMAL(38,6)))
    ) x(ColName, DboVal, ArmVal)
    WHERE (x.DboVal IS NULL AND x.ArmVal IS NOT NULL)
       OR (x.DboVal IS NOT NULL AND x.ArmVal IS NULL)
       OR (x.DboVal IS NOT NULL AND x.ArmVal IS NOT NULL AND ABS(x.DboVal - x.ArmVal) > @Tol)
    ORDER BY d.Identifier, x.ColName;
    -- text-column differences (informational; NULL-insensitive compare)
    SELECT 'Station' AS T, d.Identifier, a.Region, x.ColName,
           x.DboVal, x.ArmVal
    FROM dbo.Station d JOIN arm.Station a ON a.Identifier = d.Identifier
    CROSS APPLY (VALUES
        ('WmoId',   CONVERT(NVARCHAR(64), d.WMOID),   CONVERT(NVARCHAR(64), a.WmoId)),
        ('Wban',    CONVERT(NVARCHAR(64), d.WBAN),    CONVERT(NVARCHAR(64), a.Wban)),
        ('Ghcnd',   CONVERT(NVARCHAR(64), d.GHCND),   CONVERT(NVARCHAR(64), a.Ghcnd)),
        ('Name',    CONVERT(NVARCHAR(64), d.[Name]),  CONVERT(NVARCHAR(64), a.[Name])),
        ('State',   CONVERT(NVARCHAR(64), d.[State]), CONVERT(NVARCHAR(64), a.[State])),
        ('Country', CONVERT(NVARCHAR(64), d.Country), CONVERT(NVARCHAR(64), a.Country))
    ) x(ColName, DboVal, ArmVal)
    WHERE ISNULL(x.DboVal, N'\0') <> ISNULL(x.ArmVal, N'\0')
    ORDER BY d.Identifier, x.ColName;
END
GO

-- =============================================================================
-- SUMMARY (final result set). MissingInArm is the primary signal. NULL join metrics
-- mean the table was NOT joined (GRAIN MISMATCH / NO ARM COUNTERPART) - see Notes.
-- =============================================================================
SELECT
    TableName,
    MappingStatus,
    DboRows,
    ArmRows,
    MatchedKeys,
    MissingInArm,
    ExtraInArm,
    ValueMismatches,
    Notes
FROM #Summary
ORDER BY Ord;
GO
