-- =============================================================================
-- 001_CreateCwgSchema.sql
-- Database, [arm] schema, the Endpoint/Status lookup tables, the arm.FileLog hub,
-- and the 15 flat fact tables for the CWG (Commodity Weather Group) loader. Runs
-- against this loader's OWN database (Loaders:CWG:ConnectionString → Database=CWG).
--
-- Design of record: docs/design/CWG.md (§6 full-field mapping + §9 resolved
-- decisions) and docs/apis/CWG.md (per-column types, nullability, DECIMAL sizing).
--
-- Model (mirrors Platts' flat arm.* posture + StormVista's FileLog hub):
--   * arm.Endpoint / arm.Status are small lookup tables keyed by their natural
--     NAME (no surrogate Id). arm.Endpoint additionally carries the endpoint's
--     full URL template (base + filename template from CwgDescriptors.cs). Both
--     are pre-seeded (MERGE, re-runnable) with the fixed catalog: 15 endpoints,
--     3 statuses. FileLog.Endpoint / FileLog.Status FK to them by name.
--   * arm.FileLog is the single hub: one row per request/outcome (Success /
--     NotAvailable / Failed) across all 15 endpoints. It KEEPS its surrogate
--     Id PK (documented hub exception): its natural key (Endpoint, Region,
--     Variant, RepresentativeDate) contains NULL-able columns (Region / Variant /
--     RepresentativeDate) that a PRIMARY KEY cannot contain, so the natural key
--     is enforced by a UNIQUE constraint whose SQL NULL-equality collapses
--     undated / no-region rows to a single hub row (resolved decision 7).
--     NationalDegreeDays uses Region='northamerica', Variant='national' (§5).
--   * Every fact carries FileLogId (provenance FK to arm.FileLog(Id)). Unlike
--     the hub, the FACTS now use their NATURAL key AS THE PRIMARY KEY — there is
--     NO surrogate Id on any fact table (design-directed; nothing references a
--     fact by Id). All 15 fact natural keys are all-NOT-NULL, so a PK is valid.
--     FileLogId is NOT part of the fact PK; it is UPDATEd on MERGE match (003).
--   * Because FileLogId does NOT lead the fact PKs, each fact carries an explicit
--     nonclustered IX_<Table>_FileLogId for the FileLog→facts join / FK check.
--
-- Standing conventions applied:
--   * Lookup tables (Endpoint/Status) are keyed by their natural NAME per task
--     directive — a DELIBERATE exception to the "Id first, DateCreated second"
--     convention (they are tiny fixed catalogs, referenced by name via FK).
--   * FACT tables OMIT the surrogate Id and PK on the natural key — a
--     design-directed exception to the "Id first" convention (facts are never
--     referenced by a surrogate key). DateCreated + ModifiedAtUtc remain.
--   * Numeric measures are DECIMAL (never FLOAT), sized by measure family per
--     docs/apis/CWG.md: MW = DECIMAL(12,4) (NOT (9,4) which overflows at ~164k);
--     pct = DECIMAL(6,2); national DD = DECIMAL(9,4); temps = DECIMAL(5,1);
--     city HDD/CDD = SMALLINT; lat/lon = DECIMAL(9,6).
--   * HourOfDay is TINYINT — a DELIBERATE, design-directed exception to the
--     "no TINYINT" standing convention (resolved decision 3 / docs/apis §5,§9):
--     the value is a fixed 0–23 clock index and the field reference specifies
--     TINYINT. Stored ALONGSIDE the literal HourLabel VARCHAR(8).
--   * Named constraints (PK_/FK_/UQ_/CK_/DF_) and re-runnable guarded creates
--     (IF OBJECT_ID(...) IS NULL with SELECT 1 probes). Inline nonclustered
--     indexes are created atomically with each guarded table. Lookup seeds use
--     MERGE so re-execution keeps the catalog current without duplicating rows.
--
-- Parents precede children: Endpoint + Status (lookups) → FileLog (hub) → the 15
-- facts. FKs are satisfied because the lookups are seeded before FileLog exists.
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
-- LOOKUP TABLES. Keyed by their natural NAME (no surrogate Id). FileLog FKs to
-- them by name. Created and seeded BEFORE FileLog so the FKs are always valid.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- arm.Endpoint — the fixed catalog of 15 endpoints. Name is the exact EndpointId
-- from CwgDescriptors.cs; Url is the full endpoint URL template
-- (https://api.commoditywx.com/v1/ + that endpoint's filename template, with the
-- {region}/{subregion}/{date}/{datemmddyyyy} placeholders kept literal).
-- VARCHAR(40): longest EndpointId is 'WindTotalCapacityClimatology' (28 chars).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Endpoint', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Endpoint
    (
        [Name]      VARCHAR(40)  NOT NULL CONSTRAINT PK_Endpoint PRIMARY KEY,
        Url         VARCHAR(200) NOT NULL,
        DateCreated DATETIME     NOT NULL CONSTRAINT DF_Endpoint_DateCreated DEFAULT GETDATE()
    );
END
GO

-- Seed / refresh the 15 endpoints (MERGE → re-runnable; keeps Url current).
MERGE arm.Endpoint AS tgt
USING (VALUES
    ('CityForecast',                 'https://api.commoditywx.com/v1/city15dfcst_{region}_{date}_F.csv'),
    ('CityGasForecast',              'https://api.commoditywx.com/v1/city_gasday_fcst.csv'),
    ('CityObservation',              'https://api.commoditywx.com/v1/{region}_observations_final_{date}.csv'),
    ('DailyNormal',                  'https://api.commoditywx.com/v1/daily_normals.csv'),
    ('SolarForecast',                'https://api.commoditywx.com/v1/{region}solar_{datemmddyyyy}.csv'),
    ('SolarForecastChange',          'https://api.commoditywx.com/v1/{region}solarchanges_{datemmddyyyy}.csv'),
    ('SolarHourly',                  'https://api.commoditywx.com/v1/Gen_hrly_solar.csv'),
    ('NationalDegreeDays',           'https://api.commoditywx.com/v1/northamerica_{subregion}_wdd_{date}.csv'),
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
        [Name]      VARCHAR(20) NOT NULL CONSTRAINT PK_Status PRIMARY KEY,
        DateCreated DATETIME    NOT NULL CONSTRAINT DF_Status_DateCreated DEFAULT GETDATE()
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

-- =============================================================================
-- HUB TABLE. One row per request/outcome across all 15 endpoints. Endpoint and
-- Status are now normalized: they FK to arm.Endpoint(Name) / arm.Status(Name)
-- (both catalogs pre-seeded above, so the FKs are always satisfied). Region /
-- Variant / RepresentativeDate are NULL-able; the UNIQUE constraint relies on
-- SQL NULL-equality so undated / no-region requests collapse to one stable hub
-- row (upserted each run). FileLog KEEPS its surrogate Id PK because its natural
-- key contains NULL-able columns a PK cannot hold (documented hub exception).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.FileLog', 'U') IS NULL
BEGIN
    CREATE TABLE arm.FileLog
    (
        Id                 INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_FileLog PRIMARY KEY,
        DateCreated        DATETIME      NOT NULL CONSTRAINT DF_FileLog_DateCreated DEFAULT GETDATE(),
        Endpoint           VARCHAR(40)   NOT NULL,               -- FK to arm.Endpoint(Name); width holds 'WindTotalCapacityClimatology'
        Region             VARCHAR(16)   NULL,                   -- geography (northamerica/asia/europe) or ISO region; NULL for capacity/undated
        Variant            VARCHAR(16)   NULL,                   -- subregion ('national'); NULL otherwise
        RepresentativeDate DATE          NULL,                   -- file date; NULL for undated "latest" files
        [Status]           VARCHAR(20)   NOT NULL,               -- FK to arm.Status(Name)
        HttpStatus         INT           NULL,
        [RowCount]         INT           NOT NULL CONSTRAINT DF_FileLog_RowCount DEFAULT 0,
        RequestPath        NVARCHAR(400) NOT NULL,               -- sanitized path, NO apikey (unique per row; stays inline)
        LastCheckedUtc     DATETIME2(3)  NULL,
        ModifiedAtUtc      DATETIME2(3)  NULL,
        -- NULL-equality in a UNIQUE constraint collapses undated / no-region rows
        -- to a single hub row per (Endpoint[, Region][, Variant][, Date]).
        CONSTRAINT UQ_FileLog_Endpoint_Region_Variant_RepDate
            UNIQUE (Endpoint, Region, Variant, RepresentativeDate),
        CONSTRAINT FK_FileLog_Endpoint FOREIGN KEY (Endpoint) REFERENCES arm.Endpoint ([Name]),
        CONSTRAINT FK_FileLog_Status   FOREIGN KEY ([Status])  REFERENCES arm.Status ([Name])
    );
END
GO

-- =============================================================================
-- FACT TABLES. Each fact's PRIMARY KEY is its endpoint's NATURAL key (§6) — no
-- surrogate Id. Idempotent MERGE (003) upserts on that same natural key.
-- FileLogId is provenance (FK to arm.FileLog) — NOT part of the PK — and is
-- UPDATEd on match. Each fact adds IX_<Table>_FileLogId for the hub join.
-- Column ORDER (FileLogId first, then the mapped data columns) is mirrored by the
-- matching TVP in 002 — a load-bearing contract with the C# sink BuildTable.
-- =============================================================================

-- ----------------------------------------------------------------------------
-- 1. arm.CityForecast (Shape A) — PK (Region, Station, ProductionDate, ForecastDate).
--    15-day city forecast; units fixed to F (not stored). Region = geography.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CityForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CityForecast
    (
        DateCreated    DATETIME     NOT NULL CONSTRAINT DF_CityForecast_DateCreated DEFAULT GETDATE(),
        FileLogId      INT          NOT NULL,
        Region         VARCHAR(16)  NOT NULL,   -- geography literal from filename
        ProductionDate DATE         NOT NULL,   -- M/D/YY (2-digit year)
        ForecastDate   DATE         NOT NULL,   -- M/D/YY
        Station        VARCHAR(8)   NOT NULL,
        FcstMin        DECIMAL(5,1) NOT NULL,
        FcstMax        DECIMAL(5,1) NOT NULL,
        FcstAvg        DECIMAL(5,1) NOT NULL,
        NormMin        DECIMAL(5,1) NOT NULL,
        NormMax        DECIMAL(5,1) NOT NULL,   -- header quirk 'Norm Max'
        Hdd            SMALLINT     NOT NULL,
        Cdd            SMALLINT     NOT NULL,
        ModifiedAtUtc  DATETIME2(3) NOT NULL CONSTRAINT DF_CityForecast_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_CityForecast PRIMARY KEY (Region, Station, ProductionDate, ForecastDate),
        CONSTRAINT FK_CityForecast_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_CityForecast_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 2. arm.CityGasForecast (Shape A) — PK (Station, ProductionDate, ForecastDate).
--    Identical columns to CityForecast minus Region (undated hub; production date
--    comes from the CSV, not the filename).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CityGasForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CityGasForecast
    (
        DateCreated    DATETIME     NOT NULL CONSTRAINT DF_CityGasForecast_DateCreated DEFAULT GETDATE(),
        FileLogId      INT          NOT NULL,
        ProductionDate DATE         NOT NULL,   -- M/D/YY, in-row
        ForecastDate   DATE         NOT NULL,   -- M/D/YY
        Station        VARCHAR(8)   NOT NULL,
        FcstMin        DECIMAL(5,1) NOT NULL,
        FcstMax        DECIMAL(5,1) NOT NULL,
        FcstAvg        DECIMAL(5,1) NOT NULL,
        NormMin        DECIMAL(5,1) NOT NULL,
        NormMax        DECIMAL(5,1) NOT NULL,
        Hdd            SMALLINT     NOT NULL,
        Cdd            SMALLINT     NOT NULL,
        ModifiedAtUtc  DATETIME2(3) NOT NULL CONSTRAINT DF_CityGasForecast_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_CityGasForecast PRIMARY KEY (Station, ProductionDate, ForecastDate),
        CONSTRAINT FK_CityGasForecast_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_CityGasForecast_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 3. arm.CityObservation (Shape A) — PK (Region, Station, ObsDate).
--    Finalized observed temps/DD; ObsDate = runDate − 1 (YYYY-MM-DD).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.CityObservation', 'U') IS NULL
BEGIN
    CREATE TABLE arm.CityObservation
    (
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_CityObservation_DateCreated DEFAULT GETDATE(),
        FileLogId     INT          NOT NULL,
        Region        VARCHAR(16)  NOT NULL,   -- geography literal from filename
        ObsDate       DATE         NOT NULL,   -- YYYY-MM-DD
        Station       VARCHAR(8)   NOT NULL,
        MinTemp       DECIMAL(5,1) NOT NULL,
        MaxTemp       DECIMAL(5,1) NOT NULL,
        Hdd           SMALLINT     NOT NULL,
        Cdd           SMALLINT     NOT NULL,
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_CityObservation_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_CityObservation PRIMARY KEY (Region, Station, ObsDate),
        CONSTRAINT FK_CityObservation_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_CityObservation_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 4. arm.DailyNormal (Shape B, unpivot) — PK (MonthDay, Region).
--    Climatological normal MW per region per calendar day; 366 rows × 12 regions.
--    MonthDay is CHAR(5) 'MM-DD' kept literal (resolved decision 6 — do NOT coerce
--    to DATE; includes '02-29').
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.DailyNormal', 'U') IS NULL
BEGIN
    CREATE TABLE arm.DailyNormal
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_DailyNormal_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        MonthDay      CHAR(5)       NOT NULL,   -- 'MM-DD' literal
        Region        VARCHAR(10)   NOT NULL,   -- one of 12 ISO/region names
        NormalMw      DECIMAL(12,4) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_DailyNormal_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_DailyNormal PRIMARY KEY (MonthDay, Region),
        CONSTRAINT FK_DailyNormal_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_DailyNormal_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 5. arm.SolarForecast (Shape C, unpivot) — PK (Region, InitDate, ForecastDate, HourOfDay).
--    Forecast solar MW by clock-hour × forecast-day. Region = ISO.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SolarForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SolarForecast
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_SolarForecast_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        Region        VARCHAR(10)   NOT NULL,   -- ISO region from filename
        InitDate      DATE          NOT NULL,   -- from filename MMDDYYYY
        ForecastDate  DATE          NOT NULL,   -- from 'Date (EST)' header cell
        HourLabel     VARCHAR(8)    NOT NULL,   -- literal e.g. '12:00 AM'
        HourOfDay     TINYINT       NOT NULL,   -- derived 0–23 (design-directed TINYINT)
        ValueMw       DECIMAL(12,4) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_SolarForecast_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SolarForecast PRIMARY KEY (Region, InitDate, ForecastDate, HourOfDay),
        CONSTRAINT FK_SolarForecast_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_SolarForecast_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 6. arm.SolarForecastChange (Shape C, unpivot) — PK (Region, InitDate, ForecastDate, HourOfDay).
--    Run-over-run change in forecast solar MW; measure is SIGNED ChangeMw.
--    Two footer rows (sum/average change) are skipped by the parser.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SolarForecastChange', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SolarForecastChange
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_SolarForecastChange_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        Region        VARCHAR(10)   NOT NULL,
        InitDate      DATE          NOT NULL,
        ForecastDate  DATE          NOT NULL,   -- MM/DD/YYYY, starting at init
        HourLabel     VARCHAR(8)    NOT NULL,
        HourOfDay     TINYINT       NOT NULL,
        ChangeMw      DECIMAL(12,4) NOT NULL,   -- signed
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_SolarForecastChange_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SolarForecastChange PRIMARY KEY (Region, InitDate, ForecastDate, HourOfDay),
        CONSTRAINT FK_SolarForecastChange_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_SolarForecastChange_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 7. arm.SolarHourly (Shape B, unpivot) — PK (HourEndingUtc, Region).
--    Actual hourly solar MW; 14-region set. '"NULL"'/blank cells are skipped in
--    C# (resolved decision 4), so ActualMw is simply NULL-able.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.SolarHourly', 'U') IS NULL
BEGIN
    CREATE TABLE arm.SolarHourly
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_SolarHourly_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        HourEndingUtc DATETIME2(0)  NOT NULL,   -- YYYY-MM-DD HH:MM:SS UTC, hour-ending
        Region        VARCHAR(10)   NOT NULL,   -- one of 14 regions
        ActualMw      DECIMAL(12,4) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_SolarHourly_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SolarHourly PRIMARY KEY (HourEndingUtc, Region),
        CONSTRAINT FK_SolarHourly_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_SolarHourly_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 8. arm.NationalDegreeDays (Shape A) — PK (RunDate, Dates).
--    Observed + forecast national weighted degree-days. Digit-leading source
--    headers (30Y_/10Y_) are renamed by ordered position. RunDate = filename date.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.NationalDegreeDays', 'U') IS NULL
BEGIN
    CREATE TABLE arm.NationalDegreeDays
    (
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_NationalDegreeDays_DateCreated DEFAULT GETDATE(),
        FileLogId     INT          NOT NULL,
        RunDate       DATE         NOT NULL,   -- from filename YYYYMMDD
        Dates         DATE         NOT NULL,   -- 'DATES' column, YYYY-MM-DD
        NgHdd         DECIMAL(9,4) NOT NULL,   -- NG_HDD
        NgHdd30y      DECIMAL(9,4) NOT NULL,   -- 30Y_NG_HDD
        NgHdd10y      DECIMAL(9,4) NOT NULL,   -- 10Y_NG_HDD
        NgHddLastY    DECIMAL(9,4) NOT NULL,   -- LAST_Y_NG_HDD
        PopCdd        DECIMAL(9,4) NOT NULL,   -- POP_CDD
        PopCdd30y     DECIMAL(9,4) NOT NULL,   -- 30Y_POP_CDD
        PopCdd10y     DECIMAL(9,4) NOT NULL,   -- 10Y_POP_CDD
        PopCddLastY   DECIMAL(9,4) NOT NULL,   -- LAST_Y_POP_CDD
        ElecCdd       DECIMAL(9,4) NOT NULL,   -- ELEC_CDD
        ElecCdd30y    DECIMAL(9,4) NOT NULL,   -- 30Y_ELEC_CDD
        ElecCdd10y    DECIMAL(9,4) NOT NULL,   -- 10Y_ELEC_CDD
        ElecCddLastY  DECIMAL(9,4) NOT NULL,   -- LAST_Y_ELEC_CDD
        IsForecast    BIT          NOT NULL,   -- IS_FORECAST True/False
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_NationalDegreeDays_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_NationalDegreeDays PRIMARY KEY (RunDate, Dates),
        CONSTRAINT FK_NationalDegreeDays_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_NationalDegreeDays_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 9. arm.WindForecast (Shape C, unpivot) — PK (Region, InitDate, ForecastDate, HourOfDay).
--    Forecast wind MW by clock-hour × forecast-day; 16-region filename axis.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindForecast', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindForecast
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_WindForecast_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        Region        VARCHAR(10)   NOT NULL,
        InitDate      DATE          NOT NULL,
        ForecastDate  DATE          NOT NULL,   -- M/D/YYYY, init−1…+14
        HourLabel     VARCHAR(8)    NOT NULL,
        HourOfDay     TINYINT       NOT NULL,
        ValueMw       DECIMAL(12,4) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_WindForecast_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WindForecast PRIMARY KEY (Region, InitDate, ForecastDate, HourOfDay),
        CONSTRAINT FK_WindForecast_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_WindForecast_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 10. arm.WindForecastSubRegion (Shape D, unpivot) —
--     PK (Region, SubRegion, InitDate, ForecastDate, HourOfDay).
--     Forecast wind MW per data-driven sub-region (label ' region' stripped;
--     keeps prefixes/hyphens, e.g. 'Geo-Panhandle', '1:SP-15'); 15-col matrix.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindForecastSubRegion', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindForecastSubRegion
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_WindForecastSubRegion_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        Region        VARCHAR(10)   NOT NULL,   -- parent region from filename
        SubRegion     VARCHAR(40)   NOT NULL,   -- data-driven, free text
        InitDate      DATE          NOT NULL,
        ForecastDate  DATE          NOT NULL,   -- M/D/YYYY, init…+14
        HourLabel     VARCHAR(8)    NOT NULL,
        HourOfDay     TINYINT       NOT NULL,
        ValueMw       DECIMAL(12,4) NOT NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_WindForecastSubRegion_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WindForecastSubRegion PRIMARY KEY (Region, SubRegion, InitDate, ForecastDate, HourOfDay),
        CONSTRAINT FK_WindForecastSubRegion_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_WindForecastSubRegion_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 11. arm.WindHourly (Shape B, unpivot) — PK (HourEndingUtc, Region).
--     Actual hourly wind MW; 12-region set. '"NULL"'/blank skipped in C#
--     (resolved decision 4) → ActualMw NULL-able.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindHourly', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindHourly
    (
        DateCreated   DATETIME      NOT NULL CONSTRAINT DF_WindHourly_DateCreated DEFAULT GETDATE(),
        FileLogId     INT           NOT NULL,
        HourEndingUtc DATETIME2(0)  NOT NULL,
        Region        VARCHAR(10)   NOT NULL,   -- one of 12 regions
        ActualMw      DECIMAL(12,4) NULL,
        ModifiedAtUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_WindHourly_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WindHourly PRIMARY KEY (HourEndingUtc, Region),
        CONSTRAINT FK_WindHourly_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_WindHourly_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 12. arm.WindTotalCapacityClimatology (Shape E, 1 block) — PK (ProductionDate, Region).
--     Climatological % of capacity across three horizons. SINGLE block → no Block
--     column (resolved decision 1). Avg columns are % → DECIMAL(6,2). Footers skipped.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindTotalCapacityClimatology', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindTotalCapacityClimatology
    (
        DateCreated     DATETIME      NOT NULL CONSTRAINT DF_WindTotalCapacityClimatology_DateCreated DEFAULT GETDATE(),
        FileLogId       INT           NOT NULL,
        ProductionDate  DATE          NOT NULL,   -- from filename MMDDYYYY
        Region          VARCHAR(10)   NOT NULL,   -- data-driven region label
        TotalCapacityMw DECIMAL(12,4) NOT NULL,
        Avg_1_5         DECIMAL(6,2)  NOT NULL,   -- '1-5 Day Avg (%)' (% stripped)
        Avg_6_10        DECIMAL(6,2)  NOT NULL,   -- '6-10 Day Avg (%)'
        Avg_11_15       DECIMAL(6,2)  NOT NULL,   -- '11-15 Day Avg (%)'
        ModifiedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_WindTotalCapacityClimatology_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WindTotalCapacityClimatology PRIMARY KEY (ProductionDate, Region),
        CONSTRAINT FK_WindTotalCapacityClimatology_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_WindTotalCapacityClimatology_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 13. arm.WindTotalCapacityMW (Shape E, 3 blocks) — PK (ProductionDate, Block, Region).
--     Forecast wind averaged over three horizons, in MW, across Current /
--     Yesterday / Change blocks. TotalCapacityMw is NULL in the Change block.
--     Avg columns are MW → DECIMAL(12,4) (signed in Change). Footers skipped.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindTotalCapacityMW', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindTotalCapacityMW
    (
        DateCreated     DATETIME      NOT NULL CONSTRAINT DF_WindTotalCapacityMW_DateCreated DEFAULT GETDATE(),
        FileLogId       INT           NOT NULL,
        ProductionDate  DATE          NOT NULL,
        Block           VARCHAR(12)   NOT NULL CONSTRAINT CK_WindTotalCapacityMW_Block
                                      CHECK (Block IN ('Current','Yesterday','Change')),
        Region          VARCHAR(10)   NOT NULL,
        TotalCapacityMw DECIMAL(12,4) NULL,       -- blank in Change block → NULL
        Avg_1_5         DECIMAL(12,4) NOT NULL,   -- '1-5 Day Avg (MW)' (signed in Change)
        Avg_6_10        DECIMAL(12,4) NOT NULL,   -- '6-10 Day Avg (MW)'
        Avg_11_15       DECIMAL(12,4) NOT NULL,   -- '11-15 Day Avg (MW)'
        ModifiedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_WindTotalCapacityMW_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WindTotalCapacityMW PRIMARY KEY (ProductionDate, Block, Region),
        CONSTRAINT FK_WindTotalCapacityMW_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_WindTotalCapacityMW_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 14. arm.WindTotalCapacityPct (Shape E, 3 blocks) — PK (ProductionDate, Block, Region).
--     Same three-block structure as MW but avg columns are % of capacity →
--     DECIMAL(6,2) (% stripped; signed in Change). Footers skipped.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.WindTotalCapacityPct', 'U') IS NULL
BEGIN
    CREATE TABLE arm.WindTotalCapacityPct
    (
        DateCreated     DATETIME      NOT NULL CONSTRAINT DF_WindTotalCapacityPct_DateCreated DEFAULT GETDATE(),
        FileLogId       INT           NOT NULL,
        ProductionDate  DATE          NOT NULL,
        Block           VARCHAR(12)   NOT NULL CONSTRAINT CK_WindTotalCapacityPct_Block
                                      CHECK (Block IN ('Current','Yesterday','Change')),
        Region          VARCHAR(10)   NOT NULL,
        TotalCapacityMw DECIMAL(12,4) NULL,       -- blank in Change block → NULL
        Avg_1_5         DECIMAL(6,2)  NOT NULL,   -- '1-5 Day Avg (%)' (signed in Change)
        Avg_6_10        DECIMAL(6,2)  NOT NULL,   -- '6-10 Day Avg (%)'
        Avg_11_15       DECIMAL(6,2)  NOT NULL,   -- '11-15 Day Avg (%)'
        ModifiedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_WindTotalCapacityPct_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WindTotalCapacityPct PRIMARY KEY (ProductionDate, Block, Region),
        CONSTRAINT FK_WindTotalCapacityPct_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_WindTotalCapacityPct_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO

-- ----------------------------------------------------------------------------
-- 15. arm.Station (Shape A) — PK (Region, Identifier).
--     Static forecast-station reference metadata; undated (RunDate hot key).
--     wban kept literal (no 99999→NULL mapping); ghcnd/state empty → NULL.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('arm.Station', 'U') IS NULL
BEGIN
    CREATE TABLE arm.Station
    (
        DateCreated   DATETIME     NOT NULL CONSTRAINT DF_Station_DateCreated DEFAULT GETDATE(),
        FileLogId     INT          NOT NULL,
        Region        VARCHAR(16)  NOT NULL,   -- geography literal from filename
        Identifier    VARCHAR(8)   NOT NULL,   -- station id (ICAO-style)
        WmoId         VARCHAR(11)  NULL,
        Wban          VARCHAR(11)  NULL,       -- literal, incl. '99999' sentinel
        Ghcnd         VARCHAR(16)  NULL,       -- empty → NULL
        Lat           DECIMAL(9,6) NULL,       -- nullable: partial rows (< 9 fields) load with missing tail as NULL
        Lon           DECIMAL(9,6) NULL,       -- signed; nullable (see Lat)
        [Name]        NVARCHAR(64) NULL,       -- source truncates ~16 chars; nullable (see Lat)
        [State]       VARCHAR(8)   NULL,       -- may be blank → NULL
        Country       VARCHAR(4)   NULL,       -- nullable (see Lat)
        ModifiedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Station_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Station PRIMARY KEY (Region, Identifier),
        CONSTRAINT FK_Station_FileLog FOREIGN KEY (FileLogId) REFERENCES arm.FileLog (Id),
        INDEX IX_Station_FileLogId NONCLUSTERED (FileLogId)
    );
END
GO
