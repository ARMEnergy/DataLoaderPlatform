-- =============================================================================
-- 001_CreateCwgSchema.sql
-- Database, [arm] schema, the Endpoint/Region/Status lookup tables, the arm.FileLog
-- hub, and the 19 flat fact tables for the CWG (Commodity Weather Group) loader.
-- Runs against this loader's OWN database (Loaders:CWG:ConnectionString → Database=CWG).
--
-- Design of record: docs/design/CWG.md and docs/apis/CWG.md.
--
-- -----------------------------------------------------------------------------
-- 2026-10-05 RESHAPE — the arm facts now MIRROR the incumbent dbo.* CWG tables
-- -----------------------------------------------------------------------------
-- Every fact table below matches its legacy dbo twin by NAME, PRIMARY KEY and
-- column name/type, so arm.* is a drop-in for dbo.* and the two reconcile 1:1.
-- What changed versus the previous arm model, and why it is safe:
--
--   * FileLogId / DateCreated are GONE from every fact. arm.FileLog itself stays
--     (it is the per-request run/status log and the status matrix depends on it),
--     but a fact row no longer carries a provenance FK — dbo has no such column.
--   * Every fact gains RecordHash CHAR(32) — an MD5 over that row's NON-KEY value
--     columns, computed inside the merge proc (003). The MERGE updates ONLY when
--     the hash differs, so ModifiedAtUtc moves on real change and IX_ModifiedAtUtc
--     is a usable incremental-pull index for downstream consumers.
--   * ModifiedAtUtc is DATETIME2(3) NULL DEFAULT SYSUTCDATETIME().
--
-- Four dimensions the loader used to key on have NO column here. Each one is
-- handled by NARROWING what the loader fetches/writes, never by letting two
-- different things collide on one primary key (decided 2026-10-05):
--
--   1. Region/Units on CityForecast, CityObservation and Station. The loader now
--      enumerates northamerica ONLY (CityForecast in its native _F). Loading a
--      second geography would silently overwrite rows here, and mixing °C into
--      a °F table has no marker — so Geographies is pinned to one value and the
--      work-unit provider FAILS FAST if it is widened (CwgWorkUnitProvider).
--   2. Block on WindTotalCapacityMW / WindTotalCapacityPct. The source file
--      carries three blocks (Current / Yesterday / Change); only 'Current' is
--      written. The other two are dropped in the row factory, not merged.
--   3. NW / SW on DailyNormal and WindHourly. The source carries 12 region
--      columns; the 10 below are written and NW/SW are dropped. The shape parser
--      STILL validates all 12 against the descriptor, so an unknown column is
--      still fatal — only the WRITE set narrowed.
--   4. SolarHourly keeps ERCOT and CAISO of the source's 14 region columns.
--
-- Solar/Wind forecast key (the one genuine re-derivation):
--      ForecastDate  = the file's INIT/production date (filename MMDDYYYY)
--      UTCHourEnding = the VALID date (header 'Date (EST)' cell) + the hour-row
--                      label as a time-of-day, PLUS 4 HOURS
--      ForecastLabel = that valid date, kept for the record (NOT a key column)
--      HourLabel     = the literal clock label, kept for the record
-- (ForecastLabel, HourLabel) → UTCHourEnding is a bijection, so this key is
-- exactly as selective as the old (Region, InitDate, ForecastDate, HourOfDay) —
-- no forecast run is lost and nothing collapses. ForecastLabel/HourLabel are
-- additive to the agreed dbo column set so the un-shifted source values stay
-- recoverable without re-deriving them.
--
-- NOTE ON INDEXES: the agreed DDL omitted IX_ModifiedAtUtc on arm.Station and
-- arm.StateDegreeDayObservation while carrying it on the other 17. Read as an
-- oversight and added here for consistency — it is additive and costs one
-- narrow nonclustered index each. Say the word and they come out.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- Database. Idempotent CREATE DATABASE guard (legal inside an IF block).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'CWG')
    CREATE DATABASE CWG;
GO

USE CWG;
GO

-- ----------------------------------------------------------------------------
-- Schema arm — CREATE SCHEMA must be the first statement in its batch, so it is
-- wrapped in EXEC and followed by GO (Platts posture).
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'arm')
    EXEC('CREATE SCHEMA arm');
GO

-- =============================================================================
-- LOOKUP TABLES. Each carries a surrogate Id INT IDENTITY PK and a UNIQUE natural
-- Name. Created and seeded BEFORE FileLog so its FKs are always valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint — the fixed catalog of 19 endpoints. Name is the exact EndpointId
-- from CwgDescriptors.cs; Url is the full endpoint URL template.
-- VARCHAR(40): longest EndpointId is 'StateDegreeDayObservation' (25 chars).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Endpoint', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Endpoint
    (
        Id          INT          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Endpoint PRIMARY KEY,
        DateCreated DATETIME     NOT NULL CONSTRAINT DF_Endpoint_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(40)  NOT NULL CONSTRAINT UQ_Endpoint_Name UNIQUE,
        Url         VARCHAR(200) NOT NULL
    );
END
GO

-- Seed / refresh the 19 endpoints (MERGE → re-runnable; keeps Url current).
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('CityForecast',                 'https://api.commoditywx.com/v1/city15dfcst_{region}_{date}_{units}.csv'),
    ('CityGasForecast',              'https://api.commoditywx.com/v1/city_gasday_fcst.csv'),
    ('CityObservation',              'https://api.commoditywx.com/v1/{region}_observations_final_{date}.csv'),
    ('DailyNormal',                  'https://api.commoditywx.com/v1/daily_normals.csv'),
    ('SolarForecast',                'https://api.commoditywx.com/v1/{region}solar_{datemmddyyyy}.csv'),
    ('SolarForecastChange',          'https://api.commoditywx.com/v1/{region}solarchanges_{datemmddyyyy}.csv'),
    ('SolarHourly',                  'https://api.commoditywx.com/v1/Gen_hrly_solar.csv'),
    ('NationalDegreeDays',           'https://api.commoditywx.com/v1/northamerica_{subregion}_wdd_{date}.csv'),
    ('Regions5DegreeDays',           'https://api.commoditywx.com/v1/northamerica_{subregion}_wdd_{date}.csv'),
    ('Regions9DegreeDays',           'https://api.commoditywx.com/v1/northamerica_{subregion}_wdd_{date}.csv'),
    ('ISODegreeDays',                'https://api.commoditywx.com/v1/northamerica_{subregion}_wdd_{date}.csv'),
    ('StateDegreeDayObservation',    'https://api.commoditywx.com/v1/northamerica_state_wdd_observations_{date}.csv'),
    ('WindForecast',                 'https://api.commoditywx.com/v1/{region}wind_{datemmddyyyy}.csv'),
    ('WindForecastSubRegion',        'https://api.commoditywx.com/v1/{region}wind_regions_{datemmddyyyy}.csv'),
    ('WindHourly',                   'https://api.commoditywx.com/v1/Gen_hrly_5day.csv'),
    ('WindTotalCapacityClimatology', 'https://api.commoditywx.com/v1/Total_Capacity_climo_{datemmddyyyy}.csv'),
    ('WindTotalCapacityMW',          'https://api.commoditywx.com/v1/Total_Capacity_vals_{datemmddyyyy}.csv'),
    ('WindTotalCapacityPct',         'https://api.commoditywx.com/v1/Total_Capacity_{datemmddyyyy}.csv'),
    ('Station',                      'https://api.commoditywx.com/v1/{region}_station_information.csv')
) AS src ([Name], Url)
   ON tgt.[Name] = src.[Name]
WHEN MATCHED AND tgt.Url <> src.Url THEN
    UPDATE SET Url = src.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name], Url) VALUES (src.[Name], src.Url);
GO

-- ----------------------------------------------------------------------------
-- arm.Status — the fixed set of request outcomes. VARCHAR(20).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Status', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Status
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Status PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Status_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(20) NOT NULL CONSTRAINT UQ_Status_Name UNIQUE
                                CONSTRAINT CK_Status_Name CHECK ([Name] IN ('Success','NotAvailable','Failed'))
    );
END
GO

-- Seed the 3 statuses (MERGE → re-runnable).
MERGE arm.Status AS tgt
USING (VALUES
    ('Success'),
    ('NotAvailable'),
    ('Failed')
) AS src ([Name])
   ON tgt.[Name] = src.[Name]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name]) VALUES (src.[Name]);
GO

-- ----------------------------------------------------------------------------
-- arm.Region — the closed catalog of FILENAME region axes CwgDescriptors.cs can
-- emit: the 3 geographies + the ISO union (Solar ∪ Wind ∪ Wind sub-region
-- parents). Kept whole even though the loader now enumerates northamerica only
-- for the geography endpoints — the ISO axes are unaffected, and
-- usp_UpsertFileLog get-or-creates, so nothing breaks if a region returns.
-- A NULL filename region (capacity/hourly/normals/gasday) is a NULL RegionId in
-- FileLog, never a row here.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Region', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Region
    (
        Id          INT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Region_DateCreated DEFAULT GETDATE(),
        [Name]      VARCHAR(16) NOT NULL CONSTRAINT UQ_Region_Name UNIQUE
    );
END
GO

-- Seed the 19 known region strings (MERGE → re-runnable).
MERGE arm.Region AS tgt
USING (VALUES
    ('northamerica'), ('asia'), ('europe'),
    ('ERCOT'), ('CAISO'), ('MISO'), ('PJM'), ('SPP'), ('NEPOOL'), ('IESO'),
    ('AESO'), ('NW'), ('SW'), ('NYISO'), ('BPA'), ('UK'), ('GERMANY'),
    ('FRANCE'), ('SPAIN')
) AS src ([Name])
   ON tgt.[Name] = src.[Name]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Name]) VALUES (src.[Name]);
GO

-- =============================================================================
-- HUB TABLE. One row per request/outcome across all 19 endpoints. UNCHANGED by
-- the 2026-10-05 reshape: the facts no longer FK to it, but it remains the
-- per-request run/status log (Success / NotAvailable / Failed) that the status
-- matrix reads. Endpoint, Region and Status are normalized to surrogate-Id FKs;
-- usp_UpsertFileLog resolves the passed names to those Ids. Region / Variant /
-- RepresentativeDate are NULL-able; the UNIQUE constraint relies on SQL
-- NULL-equality so undated / no-region requests collapse to one stable hub row.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        EndpointId         INT           NOT NULL,               -- FK to arm.Endpoint(Id)
        RegionId           INT           NULL,                   -- FK to arm.Region(Id); NULL when the filename has no region
        Variant            VARCHAR(16)   NULL,                   -- subregion ('national'); NULL otherwise
        RepresentativeDate DATE          NULL,                   -- file date; NULL for undated "latest" files
        StatusId           INT           NOT NULL,               -- FK to arm.Status(Id)
        HttpStatus         INT           NULL,
        [RowCount]         INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath        NVARCHAR(400) NOT NULL,               -- sanitized path, NO apikey
        LastCheckedUtc     DATETIME2(3)  NULL,
        ModifiedAtUtc      DATETIME2(3)  NULL,
        CONSTRAINT UQ_FileLog_Endpoint_Region_Variant_RepDate
            UNIQUE (EndpointId, RegionId, Variant, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (EndpointId) REFERENCES arm.Endpoint (Id),
        CONSTRAINT FK_FileLog_Region   FOREIGN KEY (RegionId)   REFERENCES arm.Region (Id),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY (StatusId)   REFERENCES arm.Status (Id)
    );
END
GO

-- =============================================================================
-- FACT TABLES (19). Each PRIMARY KEY is CLUSTERED on the endpoint's natural key;
-- the MERGE in 003 upserts on that same key. Column ORDER (keys, then values) is
-- mirrored by the matching TVP in 002 and by each sink's BuildTable — a
-- load-bearing contract. RecordHash / ModifiedAtUtc are NOT in the TVPs: the
-- merge proc computes the hash and sets the timestamp.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- 1. arm.CityForecast — PK (ProductionDate, Date, Station).
--    15-day city forecast, northamerica in °F. [Date] is the forecast/valid day.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CityForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CityForecast
    (
        ProductionDate DATE          NOT NULL,
        [Date]         DATE          NOT NULL,
        Station        VARCHAR(50)   NOT NULL,
        ForecastMin    DECIMAL(9,4)  NULL,
        ForecastMax    DECIMAL(9,4)  NULL,
        ForecastAvg    DECIMAL(9,4)  NULL,
        NormMin        DECIMAL(9,4)  NULL,
        NormMax        DECIMAL(9,4)  NULL,
        HDD            DECIMAL(9,4)  NULL,
        CDD            DECIMAL(9,4)  NULL,
        RecordHash     CHAR(32)      NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_CityForecast_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_CityForecast
            PRIMARY KEY CLUSTERED (ProductionDate, [Date], Station)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.CityForecast (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 2. arm.CityGasForecast — PK (ProductionDate, Date, Station).
--    Gas-day city forecast; ProductionDate comes from the CSV, not the filename.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CityGasForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CityGasForecast
    (
        ProductionDate DATE          NOT NULL,
        [Date]         DATE          NOT NULL,
        Station        VARCHAR(50)   NOT NULL,
        ForecastMin    DECIMAL(9,4)  NULL,
        ForecastMax    DECIMAL(9,4)  NULL,
        ForecastAvg    DECIMAL(9,4)  NULL,
        NormMin        DECIMAL(9,4)  NULL,
        NormMax        DECIMAL(9,4)  NULL,
        HDD            DECIMAL(9,4)  NULL,
        CDD            DECIMAL(9,4)  NULL,
        RecordHash     CHAR(32)      NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_CityGasForecast_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_CityGasDayForecast
            PRIMARY KEY CLUSTERED (ProductionDate, [Date], Station)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.CityGasForecast (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 3. arm.CityObservation — PK (Date, Station).
--    Finalized observed temps/DD; [Date] = runDate − 1 (filename offset −1).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CityObservation', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CityObservation
    (
        [Date]        DATE          NOT NULL,
        Station       VARCHAR(50)   NOT NULL,
        MinTemp       DECIMAL(9,4)  NULL,
        MaxTemp       DECIMAL(9,4)  NULL,
        HDD           DECIMAL(9,4)  NULL,
        CDD           DECIMAL(9,4)  NULL,
        RecordHash    CHAR(32)      NULL,
        ModifiedAtUtc DATETIME2(3)  NULL CONSTRAINT DF_CityObservation_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_CityObservation_1
            PRIMARY KEY CLUSTERED ([Date], Station)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.CityObservation (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 4. arm.DailyNormal — PK (Month, Day). WIDE: one column per region.
--    Climatological normal MW per calendar day; 366 rows (incl. 02-29, which is
--    why the source key stays a literal 'MM-DD' and is split into two TINYINTs
--    rather than coerced to a DATE). The source file carries 12 region columns;
--    NW and SW are deliberately not written (see the header note).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.DailyNormal', 'U') IS NULL
BEGIN
    CREATE TABLE arm.DailyNormal
    (
        [Month]       TINYINT       NOT NULL,
        [Day]         TINYINT       NOT NULL,
        CAISO         DECIMAL(9,4)  NULL,
        SPP           DECIMAL(9,4)  NULL,
        ERCOT         DECIMAL(9,4)  NULL,
        MISO          DECIMAL(9,4)  NULL,
        PJM           DECIMAL(9,4)  NULL,
        NEPOOL        DECIMAL(9,4)  NULL,
        NYISO         DECIMAL(9,4)  NULL,
        BPA           DECIMAL(9,4)  NULL,
        IESO          DECIMAL(9,4)  NULL,
        AESO          DECIMAL(9,4)  NULL,
        RecordHash    CHAR(32)      NULL,
        ModifiedAtUtc DATETIME2(3)  NULL CONSTRAINT DF_DailyNormal_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_DailyNormal
            PRIMARY KEY CLUSTERED ([Month], [Day])
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.DailyNormal (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 5. arm.SolarForecast — PK (ForecastDate, UTCHourEnding, Region).
--    ForecastDate  = INIT date (filename MMDDYYYY).
--    UTCHourEnding = ForecastLabel + HourLabel-as-time + 4 hours.
--    ForecastLabel = the valid day from the 'Date (EST)' header cell (record only).
--    HourLabel     = the literal clock label, e.g. '7:00 AM' (record only).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SolarForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SolarForecast
    (
        ForecastDate   DATE          NOT NULL,
        UTCHourEnding  DATETIME2(0)  NOT NULL,
        Region         VARCHAR(50)   NOT NULL,
        ForecastLabel  DATE          NULL,
        HourLabel      VARCHAR(8)    NULL,
        [Value]        DECIMAL(9,4)  NULL,
        RecordHash     CHAR(32)      NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_SolarForecast_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_SolarForecast
            PRIMARY KEY CLUSTERED (ForecastDate, UTCHourEnding, Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.SolarForecast (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 6. arm.SolarForecastChange — PK (ForecastDate, UTCHourEnding, Region).
--    Same key derivation as #5; [Value] is the SIGNED run-over-run change.
--    The two footer rows (sum/average change for the day) are skipped in C#.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SolarForecastChange', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SolarForecastChange
    (
        ForecastDate   DATE          NOT NULL,
        UTCHourEnding  DATETIME2(0)  NOT NULL,
        Region         VARCHAR(50)   NOT NULL,
        ForecastLabel  DATE          NULL,
        HourLabel      VARCHAR(8)    NULL,
        [Value]        DECIMAL(9,4)  NULL,
        RecordHash     CHAR(32)      NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_SolarForecastChange_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_SolarForecastChange
            PRIMARY KEY CLUSTERED (ForecastDate, UTCHourEnding, Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.SolarForecastChange (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 7. arm.SolarHourly — PK (UTCHourEnding). WIDE: one column per region.
--    Actual hourly solar MW. The source carries 14 region columns; ERCOT and
--    CAISO are written. The literal '"NULL"' sentinel maps to SQL NULL (early
--    history has only ERCOT populated), so both value columns are NULL-able.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SolarHourly', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SolarHourly
    (
        UTCHourEnding DATETIME2(0)  NOT NULL,
        ERCOT         DECIMAL(9,4)  NULL,
        CAISO         DECIMAL(9,4)  NULL,
        RecordHash    CHAR(32)      NULL,
        ModifiedAtUtc DATETIME2(3)  NULL CONSTRAINT DF_SolarHourly_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_SolarHourly
            PRIMARY KEY CLUSTERED (UTCHourEnding)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.SolarHourly (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 8. arm.StateDegreeDayObservation — PK (ObservationDate, RegionName, State).
--    State-level population-weighted degree days.
--    Source: northamerica_state_wdd_observations_{date}.csv — verified live
--    2026-10-05 across 2026-08-11 / 2026-10-03 / 2026-10-04: 15 columns, 48 data
--    rows (48 states × 9 census regions), NO 'END.' footer (unlike the rest of
--    the wdd family), IS_FORECAST always 'False', and DATES is a SINGLE date
--    equal to the filename date — which is why this table keys on
--    ObservationDate alone and has no separate valid-day column.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.StateDegreeDayObservation', 'U') IS NULL
BEGIN
    CREATE TABLE arm.StateDegreeDayObservation
    (
        ObservationDate  DATE          NOT NULL,
        RegionName       VARCHAR(50)   NOT NULL,
        [State]          VARCHAR(50)   NOT NULL,
        POP_HDD          DECIMAL(9,4)  NULL,
        [30Y_POP_HDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_HDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_HDD   DECIMAL(9,4)  NULL,
        POP_CDD          DECIMAL(9,4)  NULL,
        [30Y_POP_CDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_CDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_CDD   DECIMAL(9,4)  NULL,
        IS_FORECAST      BIT           NULL,
        GAS_WEIGHT       DECIMAL(9,4)  NULL,
        ELCT_WEIGHT      DECIMAL(9,4)  NULL,
        POP_WEIGHT       DECIMAL(9,4)  NULL,
        RecordHash       CHAR(32)      NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_StateDegreeDayObservation_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_StateDegreeDayObservation
            PRIMARY KEY CLUSTERED (ObservationDate, RegionName, [State])
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.StateDegreeDayObservation (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 9. arm.Station — PK (Identifier). Static forecast-station reference metadata.
--    Point is derived IN THE MERGE PROC from Latitude/Longitude (geography::Point,
--    SRID 4326) and is NULL when either is NULL or out of range — the loader
--    never builds geography in C#. MappingId is a surrogate IDENTITY for
--    downstream joins; it is NOT the key and the merge never writes it.
--    Wban is kept literal (incl. the '99999' sentinel); blank ghcnd/state → NULL.
--    Rows truncated below 9 fields load with their missing tail as NULL.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Station', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Station
    (
        Identifier    VARCHAR(50)  NOT NULL,
        WMOID         VARCHAR(50)  NULL,
        WBAN          VARCHAR(50)  NULL,
        GHCND         VARCHAR(50)  NULL,
        Latitude      FLOAT        NULL,
        Longitude     FLOAT        NULL,
        Point         GEOGRAPHY    NULL,
        [Name]        VARCHAR(50)  NULL,
        [State]       VARCHAR(50)  NULL,
        Country       VARCHAR(50)  NULL,
        MappingId     INT          IDENTITY(1,1) NOT NULL,
        RecordHash    CHAR(32)     NULL,
        ModifiedAtUtc DATETIME2(3) NULL CONSTRAINT DF_Station_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_Station
            PRIMARY KEY CLUSTERED (Identifier)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.Station (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 10. arm.Regions5DegreeDays — PK (ObservationDate, Date, Region).
--     ObservationDate = RUN date (filename YYYYMMDD); [Date] = the DATES cell;
--     Region = the in-file REGION_NAME (free text — NOT a filename axis, so it
--     is NOT an arm.Region row). Source headers kept verbatim, including the
--     digit-leading 30Y_/10Y_ names and the 'ELCT' spelling of ELCT_WEIGHT.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Regions5DegreeDays', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Regions5DegreeDays
    (
        ObservationDate  DATE          NOT NULL,
        [Date]           DATE          NOT NULL,
        Region           VARCHAR(50)   NOT NULL,
        NG_HDD           DECIMAL(9,4)  NULL,
        [30Y_NG_HDD]     DECIMAL(9,4)  NULL,
        [10Y_NG_HDD]     DECIMAL(9,4)  NULL,
        LAST_Y_NG_HDD    DECIMAL(9,4)  NULL,
        POP_CDD          DECIMAL(9,4)  NULL,
        [30Y_POP_CDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_CDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_CDD   DECIMAL(9,4)  NULL,
        ELEC_CDD         DECIMAL(9,4)  NULL,
        [30Y_ELEC_CDD]   DECIMAL(9,4)  NULL,
        [10Y_ELEC_CDD]   DECIMAL(9,4)  NULL,
        LAST_Y_ELEC_CDD  DECIMAL(9,4)  NULL,
        IS_FORECAST      BIT           NULL,
        GAS_WEIGHT       DECIMAL(9,4)  NULL,
        ELCT_WEIGHT      DECIMAL(9,4)  NULL,
        POP_WEIGHT       DECIMAL(9,4)  NULL,
        RecordHash       CHAR(32)      NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_Regions5DegreeDays_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_US5RegionDegreeDay
            PRIMARY KEY CLUSTERED (ObservationDate, [Date], Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.Regions5DegreeDays (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 11. arm.Regions9DegreeDays — PK (ObservationDate, Date, Region).
--     IDENTICAL column set to #10; only the Region value set differs (the 9 U.S.
--     Census divisions, UPPERCASE).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Regions9DegreeDays', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Regions9DegreeDays
    (
        ObservationDate  DATE          NOT NULL,
        [Date]           DATE          NOT NULL,
        Region           VARCHAR(50)   NOT NULL,
        NG_HDD           DECIMAL(9,4)  NULL,
        [30Y_NG_HDD]     DECIMAL(9,4)  NULL,
        [10Y_NG_HDD]     DECIMAL(9,4)  NULL,
        LAST_Y_NG_HDD    DECIMAL(9,4)  NULL,
        POP_CDD          DECIMAL(9,4)  NULL,
        [30Y_POP_CDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_CDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_CDD   DECIMAL(9,4)  NULL,
        ELEC_CDD         DECIMAL(9,4)  NULL,
        [30Y_ELEC_CDD]   DECIMAL(9,4)  NULL,
        [10Y_ELEC_CDD]   DECIMAL(9,4)  NULL,
        LAST_Y_ELEC_CDD  DECIMAL(9,4)  NULL,
        IS_FORECAST      BIT           NULL,
        GAS_WEIGHT       DECIMAL(9,4)  NULL,
        ELCT_WEIGHT      DECIMAL(9,4)  NULL,
        POP_WEIGHT       DECIMAL(9,4)  NULL,
        RecordHash       CHAR(32)      NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_Regions9DegreeDays_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_US9RegionDegreeDay
            PRIMARY KEY CLUSTERED (ObservationDate, [Date], Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.Regions9DegreeDays (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 12. arm.ISODegreeDays — PK (ObservationDate, Date, Region).
--     DIVERGENT 11-column source layout: the HDD family is POP_HDD (population
--     weighted), NOT NG_HDD; there is NO ELEC family and NO weight columns.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.ISODegreeDays', 'U') IS NULL
BEGIN
    CREATE TABLE arm.ISODegreeDays
    (
        ObservationDate  DATE          NOT NULL,
        [Date]           DATE          NOT NULL,
        Region           VARCHAR(50)   NOT NULL,
        POP_HDD          DECIMAL(9,4)  NULL,
        [30Y_POP_HDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_HDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_HDD   DECIMAL(9,4)  NULL,
        POP_CDD          DECIMAL(9,4)  NULL,
        [30Y_POP_CDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_CDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_CDD   DECIMAL(9,4)  NULL,
        IS_FORECAST      BIT           NULL,
        RecordHash       CHAR(32)      NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_ISODegreeDays_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_USISODegreeDay
            PRIMARY KEY CLUSTERED (ObservationDate, [Date], Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.ISODegreeDays (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 13. arm.NationalDegreeDays — PK (ObservationDate, Date). No region column:
--     the 'national' subregion is the single-region aggregate.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.NationalDegreeDays', 'U') IS NULL
BEGIN
    CREATE TABLE arm.NationalDegreeDays
    (
        ObservationDate  DATE          NOT NULL,
        [Date]           DATE          NOT NULL,
        NG_HDD           DECIMAL(9,4)  NULL,
        [30Y_NG_HDD]     DECIMAL(9,4)  NULL,
        [10Y_NG_HDD]     DECIMAL(9,4)  NULL,
        LAST_Y_NG_HDD    DECIMAL(9,4)  NULL,
        POP_CDD          DECIMAL(9,4)  NULL,
        [30Y_POP_CDD]    DECIMAL(9,4)  NULL,
        [10Y_POP_CDD]    DECIMAL(9,4)  NULL,
        LAST_Y_POP_CDD   DECIMAL(9,4)  NULL,
        ELEC_CDD         DECIMAL(9,4)  NULL,
        [30Y_ELEC_CDD]   DECIMAL(9,4)  NULL,
        [10Y_ELEC_CDD]   DECIMAL(9,4)  NULL,
        LAST_Y_ELEC_CDD  DECIMAL(9,4)  NULL,
        IS_FORECAST      BIT           NULL,
        RecordHash       CHAR(32)      NULL,
        ModifiedAtUtc    DATETIME2(3)  NULL CONSTRAINT DF_NationalDegreeDays_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_USNationalDegreeDay
            PRIMARY KEY CLUSTERED (ObservationDate, [Date])
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.NationalDegreeDays (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 14. arm.WindForecast — PK (ForecastDate, UTCHourEnding, Region).
--     Same key derivation as arm.SolarForecast (#5); 16-region filename axis.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindForecast
    (
        ForecastDate   DATE          NOT NULL,
        UTCHourEnding  DATETIME2(0)  NOT NULL,
        Region         VARCHAR(50)   NOT NULL,
        ForecastLabel  DATE          NULL,
        HourLabel      VARCHAR(8)    NULL,
        [Value]        DECIMAL(9,4)  NULL,
        RecordHash     CHAR(32)      NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_WindForecast_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_WindForecast
            PRIMARY KEY CLUSTERED (ForecastDate, UTCHourEnding, Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.WindForecast (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 15. arm.WindForecastSubRegion — PK (ForecastDate, UTCHourEnding, Region, SubRegion).
--     Region is the parent from the filename; SubRegion is data-driven (the
--     block label with its trailing ' region' stripped, keeping prefixes and
--     hyphens, e.g. 'Geo-Panhandle', '1:SP-15'). [Value] is DECIMAL(19,4) here.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindForecastSubRegion', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindForecastSubRegion
    (
        ForecastDate   DATE          NOT NULL,
        UTCHourEnding  DATETIME2(0)  NOT NULL,
        Region         VARCHAR(50)   NOT NULL,
        SubRegion      VARCHAR(50)   NOT NULL,
        ForecastLabel  DATE          NULL,
        HourLabel      VARCHAR(8)    NULL,
        [Value]        DECIMAL(19,4) NULL,
        RecordHash     CHAR(32)      NULL,
        ModifiedAtUtc  DATETIME2(3)  NULL CONSTRAINT DF_WindForecastSubRegion_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_WindForecastSubRegion
            PRIMARY KEY CLUSTERED (ForecastDate, UTCHourEnding, Region, SubRegion)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.WindForecastSubRegion (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 16. arm.WindHourly — PK (UTCHourEnding). WIDE: one column per region.
--     Actual hourly wind MW. The source carries 12 region columns; NW and SW are
--     deliberately not written. '"NULL"'/blank cells map to SQL NULL.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindHourly', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindHourly
    (
        UTCHourEnding DATETIME2(0)  NOT NULL,
        CAISO         DECIMAL(9,4)  NULL,
        SPP           DECIMAL(9,4)  NULL,
        ERCOT         DECIMAL(9,4)  NULL,
        MISO          DECIMAL(9,4)  NULL,
        PJM           DECIMAL(9,4)  NULL,
        NEPOOL        DECIMAL(9,4)  NULL,
        NYISO         DECIMAL(9,4)  NULL,
        BPA           DECIMAL(9,4)  NULL,
        IESO          DECIMAL(9,4)  NULL,
        AESO          DECIMAL(9,4)  NULL,
        RecordHash    CHAR(32)      NULL,
        ModifiedAtUtc DATETIME2(3)  NULL CONSTRAINT DF_WindHourly_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_WindHourly
            PRIMARY KEY CLUSTERED (UTCHourEnding)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.WindHourly (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 17. arm.WindTotalCapacityClimatology — PK (ForecastDate, Region).
--     ForecastDate = the production date from the filename (MMDDYYYY).
--     The three averages are % of capacity (the source's trailing '%' stripped).
--     SINGLE-block file, so there is nothing to narrow here.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindTotalCapacityClimatology', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindTotalCapacityClimatology
    (
        ForecastDate              DATE          NOT NULL,
        Region                    VARCHAR(50)   NOT NULL,
        TotalCapacity             DECIMAL(9,4)  NULL,
        OneToFiveDayAverage       DECIMAL(9,4)  NULL,
        SixToTenDayAverage        DECIMAL(9,4)  NULL,
        ElevenToFifteenDayAverage DECIMAL(9,4)  NULL,
        RecordHash                CHAR(32)      NULL,
        ModifiedAtUtc             DATETIME2(3)  NULL CONSTRAINT DF_WindTotalCapacityClimatology_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_WindTotalCapacityClimatology
            PRIMARY KEY CLUSTERED (ForecastDate, Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.WindTotalCapacityClimatology (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 18. arm.WindTotalCapacityMW — PK (ForecastDate, Region). Averages in MW.
--     The source file has THREE blocks (Current / <weekday>'s Forecast /
--     Change from <weekday>'s Forecast). Only the CURRENT block is written —
--     there is no Block column to tell them apart, so the row factory drops the
--     other two rather than letting them overwrite each other on this key.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindTotalCapacityMW', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindTotalCapacityMW
    (
        ForecastDate              DATE          NOT NULL,
        Region                    VARCHAR(50)   NOT NULL,
        TotalCapacity             DECIMAL(9,4)  NULL,
        OneToFiveDayAverage       DECIMAL(9,4)  NULL,
        SixToTenDayAverage        DECIMAL(9,4)  NULL,
        ElevenToFifteenDayAverage DECIMAL(9,4)  NULL,
        RecordHash                CHAR(32)      NULL,
        ModifiedAtUtc             DATETIME2(3)  NULL CONSTRAINT DF_WindTotalCapacityMW_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_WindTotalCapacityMW
            PRIMARY KEY CLUSTERED (ForecastDate, Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.WindTotalCapacityMW (ModifiedAtUtc);
END
GO

-- ----------------------------------------------------------------------------
-- 19. arm.WindTotalCapacityPct — PK (ForecastDate, Region). Averages in % of
--     capacity. Same three-block source and same Current-only rule as #18.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindTotalCapacityPct', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindTotalCapacityPct
    (
        ForecastDate              DATE          NOT NULL,
        Region                    VARCHAR(50)   NOT NULL,
        TotalCapacity             DECIMAL(9,4)  NULL,
        OneToFiveDayAverage       DECIMAL(9,4)  NULL,
        SixToTenDayAverage        DECIMAL(9,4)  NULL,
        ElevenToFifteenDayAverage DECIMAL(9,4)  NULL,
        RecordHash                CHAR(32)      NULL,
        ModifiedAtUtc             DATETIME2(3)  NULL CONSTRAINT DF_WindTotalCapacityPct_ModifiedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ARM_WindTotalCapacityPct
            PRIMARY KEY CLUSTERED (ForecastDate, Region)
    ) ON [PRIMARY];

    CREATE NONCLUSTERED INDEX IX_ModifiedAtUtc ON arm.WindTotalCapacityPct (ModifiedAtUtc);
END
GO
