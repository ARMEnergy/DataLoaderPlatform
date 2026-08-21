-- =============================================================================
-- 003_CreateIirProcedures.sql
-- Stored procedures for the IIR (Industrial Info Resources — IDB API v2.7) loader
-- (schema [arm]):
--   * arm.usp_UpsertFileLog       — per-pull hub upsert; RETURNS FileLogId.
--   * arm.usp_UpsertPlant         — TVP bulk upsert into arm.Plant        (key PlantId).
--   * arm.usp_UpsertUnit          — TVP bulk upsert into arm.Unit         (key UnitId).
--   * arm.usp_UpsertOfflineEvent  — TVP bulk upsert into arm.OfflineEvent (key (RunDate, EventId)).
--   * arm.usp_UpsertPlantSummary         — id-catalog census upsert (key (RunDate, PlantId)).
--   * arm.usp_UpsertUnitSummary          — id-catalog census upsert (key (RunDate, UnitId)).
--   * arm.usp_UpsertOfflineEventSummary  — id-catalog census upsert (key (RunDate, EventId)).
--   * arm.usp_ValidateLoad        — post-load observational anomaly report (design §9,
--                                   AGSI usp_ValidateLoad precedent; called by IirLoadValidator).
--
-- LOAD ORDER the procs imply (per pull, CWG/AGSI posture):
--   1) arm.usp_UpsertFileLog(...) -> returns FileLogId (called for EVERY outcome:
--      Success / NotAvailable / Failed).
--   2) On rows > 0, stamp that FileLogId onto every TVP row and call the matching
--      arm.usp_Upsert... proc.
--
-- Each bulk-upsert proc (design §7):
--   * MERGEs on the target's key (Plant: PlantId; Unit: UnitId; OfflineEvent:
--     (RunDate, EventId) where target RunDate = @RunDate and source EventId is the TVP
--     key). The TVP's leading FileLogId is NOT persisted: the three target tables were
--     supplied VERBATIM by the user and carry NO FileLogId column, so the procs read it
--     from the TVP (sink-contract uniformity) but map it to NO target column — per-pull
--     provenance lives only in arm.FileLog. (This differs from AGSI/IHS, whose fact
--     rows carry a FileLogId FK; the authoritative IIR DDL has no such column.)
--   * De-dups the batch on that merge key (ROW_NUMBER over the TVP) before MERGE, so an
--     in-batch duplicate key cannot trigger "MERGE ... same row more than once".
--   * Builds PlantPoint IN-PROC via geography::Point(<lat>, <long>, 4326) on BOTH the
--     INSERT and UPDATE branches, guarded so an out-of-range/missing coordinate yields
--     PlantPoint = NULL rather than failing the whole MERGE batch (§7.3). SRID 4326
--     (WGS 84), argument order (lat, long). No geography value crosses the TVP.
--   * Stamps ModifiedAtUtc = SYSUTCDATETIME() explicitly on INSERT and UPDATE (matches
--     the existing arm procs — AGSI/IHS all stamp SYSUTCDATETIME(); the table's
--     SYSDATETIME() default is only a fallback for a direct insert that omits it).
--   * Maps EVERY remaining TVP column to its table column (full column coverage).
--   * Returns SELECT @@ROWCOUNT AS RecordsProcessed so the platform's SqlSinkBase
--     (ProcedureReturnsRowCount = true) can read the count.
--   * No HOLDLOCK hint — matching the existing arm procs (AGSI/IHS); the platform's
--     SqlWriteGate already serializes concurrent calls per target proc.
--
-- Procedures use CREATE OR ALTER so the script is re-runnable, and validate inputs.
-- Run 001 and 002 first. This script assumes IIR is the current database.
-- =============================================================================

USE IIR;
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertFileLog — upsert one FileLog (hub) row per endpoint pull and RETURN
-- its FileLogId. Called once per pull for ALL outcomes. RepresentativeDate is
-- NULL-able; the MERGE matches it with explicit NULL-equality so an undated request
-- reuses a single stable hub row. Endpoint / Status are passed BY NAME and resolved to
-- their surrogate arm.Endpoint / arm.Status .Id (a miss RAISERRORs — fixed catalogs
-- seeded in 001). No Region/Variant slot (IIR has no sub-variant, design §8).
--
-- RETURN CONTRACT: exactly one result set, one row, one column "FileLogId" (the C#
-- reads it with ExecuteScalar), captured via MERGE ... OUTPUT inserted.Id (the Id for
-- BOTH the inserted and the updated branch).
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

    -- ---- Validate required inputs -------------------------------------------
    IF @Endpoint IS NULL OR @StatusLabel IS NULL OR @RequestPath IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: @Endpoint, @StatusLabel and @RequestPath are required.', 16, 1);
        RETURN;
    END

    -- ---- Resolve the lookup surrogate Ids (fixed catalogs seeded in 001) -----
    DECLARE @EndpointId INT = (SELECT Id FROM arm.Endpoint WHERE [Name] = @Endpoint);
    IF @EndpointId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Endpoint ''%s''.', 16, 1, @Endpoint);
        RETURN;
    END

    DECLARE @StatusId INT = (SELECT Id FROM arm.Status WHERE [Name] = @StatusLabel);
    IF @StatusId IS NULL
    BEGIN
        RAISERROR('usp_UpsertFileLog: unknown Status ''%s''.', 16, 1, @StatusLabel);
        RETURN;
    END

    -- ---- Upsert the hub row, capturing the resulting Id ----------------------
    DECLARE @Out TABLE (FileLogId INT NOT NULL);

    MERGE arm.FileLog AS tgt
    USING (SELECT @EndpointId AS EndpointId,
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
        INSERT (EndpointId, RepresentativeDate,
                StatusId, HttpStatus, [RowCount], RequestPath, LastCheckedUtc, ModifiedAtUtc)
        VALUES (src.EndpointId, src.RepresentativeDate,
                @StatusId, @HttpStatus, @RowCount, @RequestPath, SYSUTCDATETIME(), SYSUTCDATETIME())
    OUTPUT inserted.Id INTO @Out (FileLogId);

    SELECT TOP (1) FileLogId FROM @Out;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertPlant — key (PlantId). De-dup the batch on PlantId before MERGE.
-- PlantPoint built in-proc from Latitude/Longitude (range-guarded). All 67 TVP
-- columns mapped; ModifiedAtUtc + PlantPoint set on both branches.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertPlant
    @Rows arm.PlantTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Plant AS tgt
    USING (
        SELECT *
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY PlantId ORDER BY (SELECT NULL)) AS rn
            FROM @Rows
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.PlantId = src.PlantId
    WHEN MATCHED THEN UPDATE SET
        PlantName                 = src.PlantName,
        PlantStatusDesc           = src.PlantStatusDesc,
        NoEmployees               = src.NoEmployees,
        StartupDate               = src.StartupDate,
        LiveDate                  = src.LiveDate,
        ReleaseDate               = src.ReleaseDate,
        OperationsLaborPreference = src.OperationsLaborPreference,
        PrimaryFuel               = src.PrimaryFuel,
        SecondaryFuel             = src.SecondaryFuel,
        IndustryCode              = src.IndustryCode,
        IndustryCodeDesc          = src.IndustryCodeDesc,
        PrimarySicId              = src.PrimarySicId,
        PrimarySicDesc            = src.PrimarySicDesc,
        PecZone                   = src.PecZone,
        MarketRegionId            = src.MarketRegionId,
        MarketRegionName          = src.MarketRegionName,
        ConfirmationStatus        = src.ConfirmationStatus,
        NercRegion                = src.NercRegion,
        NercSubRegionName         = src.NercSubRegionName,
        ElectricalConnectionName  = src.ElectricalConnectionName,
        TradingRegionId           = src.TradingRegionId,
        TradingRegionName         = src.TradingRegionName,
        CogenChp                  = src.CogenChp,
        Metallurgical             = src.Metallurgical,
        Thermal                   = src.Thermal,
        Placer                    = src.Placer,
        OpenPit                   = src.OpenPit,
        Quarry                    = src.Quarry,
        Strip                     = src.Strip,
        Auger                     = src.Auger,
        Dredging                  = src.Dredging,
        Drift                     = src.Drift,
        Shaft                     = src.Shaft,
        Slope                     = src.Slope,
        Longwall                  = src.Longwall,
        RoomPillar                = src.RoomPillar,
        CutFill                   = src.CutFill,
        Caving                    = src.Caving,
        Stoping                   = src.Stoping,
        InSituSolution            = src.InSituSolution,
        Longitude                 = src.Longitude,
        Latitude                  = src.Latitude,
        PlantPoint                = CASE
                                        WHEN src.Latitude  IS NOT NULL AND src.Longitude IS NOT NULL
                                         AND src.Latitude  BETWEEN  -90 AND  90
                                         AND src.Longitude BETWEEN -180 AND 180
                                        THEN geography::Point(src.Latitude, src.Longitude, 4326)
                                        ELSE NULL
                                    END,
        WorldRegionId             = src.WorldRegionId,
        WorldRegionName           = src.WorldRegionName,
        Offshore                  = src.Offshore,
        MailingAddressLine1       = src.MailingAddressLine1,
        MailingCity               = src.MailingCity,
        MailingStateName          = src.MailingStateName,
        MailingPostalCode         = src.MailingPostalCode,
        MailingCountryName        = src.MailingCountryName,
        PhysicalAddressLine1      = src.PhysicalAddressLine1,
        PhysicalCity              = src.PhysicalCity,
        PhysicalStateName         = src.PhysicalStateName,
        PhysicalPostalCode        = src.PhysicalPostalCode,
        PhysicalCountryName       = src.PhysicalCountryName,
        PhysicalCountyName        = src.PhysicalCountyName,
        PhoneCC                   = src.PhoneCC,
        PhoneNumber               = src.PhoneNumber,
        ParentCompanyId           = src.ParentCompanyId,
        ParentCompanyName         = src.ParentCompanyName,
        ParentCompanyWebsite      = src.ParentCompanyWebsite,
        OperatorCompanyId         = src.OperatorCompanyId,
        OperatorCompanyName       = src.OperatorCompanyName,
        OperatorCompanyWebsite    = src.OperatorCompanyWebsite,
        ModifiedAtUtc             = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PlantId, PlantName, PlantStatusDesc, NoEmployees, StartupDate, LiveDate, ReleaseDate,
                OperationsLaborPreference, PrimaryFuel, SecondaryFuel, IndustryCode, IndustryCodeDesc,
                PrimarySicId, PrimarySicDesc, PecZone, MarketRegionId, MarketRegionName, ConfirmationStatus,
                NercRegion, NercSubRegionName, ElectricalConnectionName, TradingRegionId, TradingRegionName,
                CogenChp, Metallurgical, Thermal, Placer, OpenPit, Quarry, Strip, Auger, Dredging, Drift,
                Shaft, Slope, Longwall, RoomPillar, CutFill, Caving, Stoping, InSituSolution,
                Longitude, Latitude, PlantPoint, WorldRegionId, WorldRegionName, Offshore,
                MailingAddressLine1, MailingCity, MailingStateName, MailingPostalCode, MailingCountryName,
                PhysicalAddressLine1, PhysicalCity, PhysicalStateName, PhysicalPostalCode, PhysicalCountryName,
                PhysicalCountyName, PhoneCC, PhoneNumber, ParentCompanyId, ParentCompanyName, ParentCompanyWebsite,
                OperatorCompanyId, OperatorCompanyName, OperatorCompanyWebsite, ModifiedAtUtc)
        VALUES (src.PlantId, src.PlantName, src.PlantStatusDesc, src.NoEmployees, src.StartupDate, src.LiveDate,
                src.ReleaseDate, src.OperationsLaborPreference, src.PrimaryFuel, src.SecondaryFuel, src.IndustryCode,
                src.IndustryCodeDesc, src.PrimarySicId, src.PrimarySicDesc, src.PecZone, src.MarketRegionId,
                src.MarketRegionName, src.ConfirmationStatus, src.NercRegion, src.NercSubRegionName,
                src.ElectricalConnectionName, src.TradingRegionId, src.TradingRegionName, src.CogenChp,
                src.Metallurgical, src.Thermal, src.Placer, src.OpenPit, src.Quarry, src.Strip, src.Auger,
                src.Dredging, src.Drift, src.Shaft, src.Slope, src.Longwall, src.RoomPillar, src.CutFill,
                src.Caving, src.Stoping, src.InSituSolution, src.Longitude, src.Latitude,
                CASE
                    WHEN src.Latitude  IS NOT NULL AND src.Longitude IS NOT NULL
                     AND src.Latitude  BETWEEN  -90 AND  90
                     AND src.Longitude BETWEEN -180 AND 180
                    THEN geography::Point(src.Latitude, src.Longitude, 4326)
                    ELSE NULL
                END,
                src.WorldRegionId, src.WorldRegionName, src.Offshore, src.MailingAddressLine1, src.MailingCity,
                src.MailingStateName, src.MailingPostalCode, src.MailingCountryName, src.PhysicalAddressLine1,
                src.PhysicalCity, src.PhysicalStateName, src.PhysicalPostalCode, src.PhysicalCountryName,
                src.PhysicalCountyName, src.PhoneCC, src.PhoneNumber, src.ParentCompanyId, src.ParentCompanyName,
                src.ParentCompanyWebsite, src.OperatorCompanyId, src.OperatorCompanyName, src.OperatorCompanyWebsite,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertUnit — key (UnitId). De-dup the batch on UnitId before MERGE.
-- PlantPoint built in-proc from PlantLatitude/PlantLongitude (range-guarded). All 45
-- TVP columns mapped; ModifiedAtUtc + PlantPoint on both branches. PlantId is an
-- advisory attribute (NOT an enforced FK; design §15.5).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertUnit
    @Rows arm.UnitTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE arm.Unit AS tgt
    USING (
        SELECT *
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY UnitId ORDER BY (SELECT NULL)) AS rn
            FROM @Rows
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON tgt.UnitId = src.UnitId
    WHEN MATCHED THEN UPDATE SET
        UnitName          = src.UnitName,
        PlantId           = src.PlantId,
        PlantName         = src.PlantName,
        PlantStatusDesc   = src.PlantStatusDesc,
        PlantAddressLine1 = src.PlantAddressLine1,
        PlantCity         = src.PlantCity,
        PlantStateName    = src.PlantStateName,
        PlantPostalCode   = src.PlantPostalCode,
        PlantCountryName  = src.PlantCountryName,
        PlantCountyName   = src.PlantCountyName,
        MarketRegionId    = src.MarketRegionId,
        MarketRegionName  = src.MarketRegionName,
        WorldRegionId     = src.WorldRegionId,
        WorldRegionName   = src.WorldRegionName,
        TradingRegionId   = src.TradingRegionId,
        TradingRegionName = src.TradingRegionName,
        UnitStatusDesc    = src.UnitStatusDesc,
        UnitStatusGroup   = src.UnitStatusGroup,
        HeaterCount       = src.HeaterCount,
        UnitTypeId        = src.UnitTypeId,
        UnitTypeDesc      = src.UnitTypeDesc,
        UnitTypeGroup     = src.UnitTypeGroup,
        CapacityProductId = src.CapacityProductId,
        Capacity          = src.Capacity,
        CapacityUom       = src.CapacityUom,
        PrimarySicId      = src.PrimarySicId,
        PrimarySicDesc    = src.PrimarySicDesc,
        AreaId            = src.AreaId,
        AreaName          = src.AreaName,
        PlantLatitude     = src.PlantLatitude,
        PlantLongitude    = src.PlantLongitude,
        PlantPoint        = CASE
                                WHEN src.PlantLatitude  IS NOT NULL AND src.PlantLongitude IS NOT NULL
                                 AND src.PlantLatitude  BETWEEN  -90 AND  90
                                 AND src.PlantLongitude BETWEEN -180 AND 180
                                THEN geography::Point(src.PlantLatitude, src.PlantLongitude, 4326)
                                ELSE NULL
                            END,
        Offshore          = src.Offshore,
        IndustryCode      = src.IndustryCode,
        IndustryCodeDesc  = src.IndustryCodeDesc,
        Technology        = src.Technology,
        Renewable         = src.Renewable,
        CogenChp          = src.CogenChp,
        PlantOperatorName = src.PlantOperatorName,
        PlantOwnerName    = src.PlantOwnerName,
        PlantParentName   = src.PlantParentName,
        PlantPhone        = src.PlantPhone,
        ReleaseDate       = src.ReleaseDate,
        LiveDate          = src.LiveDate,
        ModifiedAtUtc     = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (UnitId, UnitName, PlantId, PlantName, PlantStatusDesc, PlantAddressLine1, PlantCity,
                PlantStateName, PlantPostalCode, PlantCountryName, PlantCountyName, MarketRegionId,
                MarketRegionName, WorldRegionId, WorldRegionName, TradingRegionId, TradingRegionName,
                UnitStatusDesc, UnitStatusGroup, HeaterCount, UnitTypeId, UnitTypeDesc, UnitTypeGroup,
                CapacityProductId, Capacity, CapacityUom, PrimarySicId, PrimarySicDesc, AreaId, AreaName,
                PlantLatitude, PlantLongitude, PlantPoint, Offshore, IndustryCode, IndustryCodeDesc,
                Technology, Renewable, CogenChp, PlantOperatorName, PlantOwnerName, PlantParentName,
                PlantPhone, ReleaseDate, LiveDate, ModifiedAtUtc)
        VALUES (src.UnitId, src.UnitName, src.PlantId, src.PlantName, src.PlantStatusDesc, src.PlantAddressLine1,
                src.PlantCity, src.PlantStateName, src.PlantPostalCode, src.PlantCountryName, src.PlantCountyName,
                src.MarketRegionId, src.MarketRegionName, src.WorldRegionId, src.WorldRegionName, src.TradingRegionId,
                src.TradingRegionName, src.UnitStatusDesc, src.UnitStatusGroup, src.HeaterCount, src.UnitTypeId,
                src.UnitTypeDesc, src.UnitTypeGroup, src.CapacityProductId, src.Capacity, src.CapacityUom,
                src.PrimarySicId, src.PrimarySicDesc, src.AreaId, src.AreaName, src.PlantLatitude, src.PlantLongitude,
                CASE
                    WHEN src.PlantLatitude  IS NOT NULL AND src.PlantLongitude IS NOT NULL
                     AND src.PlantLatitude  BETWEEN  -90 AND  90
                     AND src.PlantLongitude BETWEEN -180 AND 180
                    THEN geography::Point(src.PlantLatitude, src.PlantLongitude, 4326)
                    ELSE NULL
                END,
                src.Offshore, src.IndustryCode, src.IndustryCodeDesc, src.Technology, src.Renewable, src.CogenChp,
                src.PlantOperatorName, src.PlantOwnerName, src.PlantParentName, src.PlantPhone, src.ReleaseDate,
                src.LiveDate, SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_UpsertOfflineEvent — key (RunDate, EventId). @RunDate is the Central run
-- date (a scalar, not in the TVP): every source row is upserted into the @RunDate
-- partition, keyed by its own EventId, so the daily snapshot lands as one partition
-- per Central day. De-dup the batch on EventId before MERGE. PlantPoint built in-proc
-- from PlantLatitude/PlantLongitude (range-guarded). All 60 TVP columns mapped;
-- RunDate = @RunDate on insert; ModifiedAtUtc + PlantPoint on both branches.
-- UnitId/PlantId are advisory attributes (NOT enforced FKs; design §15.5).
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertOfflineEvent
    @RunDate DATE,
    @Rows    arm.OfflineEventTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    IF @RunDate IS NULL
    BEGIN
        RAISERROR('usp_UpsertOfflineEvent: @RunDate is required.', 16, 1);
        RETURN;
    END

    MERGE arm.OfflineEvent AS tgt
    USING (
        SELECT *
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY EventId ORDER BY (SELECT NULL)) AS rn
            FROM @Rows
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate = @RunDate
       AND tgt.EventId = src.EventId
    WHEN MATCHED THEN UPDATE SET
        EventKind               = src.EventKind,
        EventType               = src.EventType,
        EventCause              = src.EventCause,
        EventStatusDesc         = src.EventStatusDesc,
        UnitId                  = src.UnitId,
        UnitName                = src.UnitName,
        UnitStatusDesc          = src.UnitStatusDesc,
        IndustryCode            = src.IndustryCode,
        IndustryCodeDesc        = src.IndustryCodeDesc,
        PlantId                 = src.PlantId,
        PlantName               = src.PlantName,
        PlantParentName         = src.PlantParentName,
        PlantOwnerName          = src.PlantOwnerName,
        PlantOperatorName       = src.PlantOperatorName,
        PlantAddressLine1       = src.PlantAddressLine1,
        PlantCity               = src.PlantCity,
        PlantState              = src.PlantState,
        PlantPostalCode         = src.PlantPostalCode,
        PlantCountry            = src.PlantCountry,
        PlantCounty             = src.PlantCounty,
        PlantLatitude           = src.PlantLatitude,
        PlantLongitude          = src.PlantLongitude,
        PlantPoint              = CASE
                                      WHEN src.PlantLatitude  IS NOT NULL AND src.PlantLongitude IS NOT NULL
                                       AND src.PlantLatitude  BETWEEN  -90 AND  90
                                       AND src.PlantLongitude BETWEEN -180 AND 180
                                      THEN geography::Point(src.PlantLatitude, src.PlantLongitude, 4326)
                                      ELSE NULL
                                  END,
        AreaId                  = src.AreaId,
        AreaName                = src.AreaName,
        Offshore                = src.Offshore,
        GasRegionId             = src.GasRegionId,
        GasRegionName           = src.GasRegionName,
        MarketRegionId          = src.MarketRegionId,
        MarketRegionName        = src.MarketRegionName,
        TradingRegionId         = src.TradingRegionId,
        TradingRegionName       = src.TradingRegionName,
        PowerTradeRegion        = src.PowerTradeRegion,
        WorldRegionId           = src.WorldRegionId,
        WorldRegionName         = src.WorldRegionName,
        PecZone                 = src.PecZone,
        PrimarySicId            = src.PrimarySicId,
        UnitClassification      = src.UnitClassification,
        Derate                  = src.Derate,
        IsDerated               = src.IsDerated,
        ProductId               = src.ProductId,
        ProductDescription      = src.ProductDescription,
        UnitCapacity            = src.UnitCapacity,
        OfflineCapacity         = src.OfflineCapacity,
        OfflineCapacityUOM      = src.OfflineCapacityUOM,
        EventStartDate          = src.EventStartDate,
        EventEndDate            = src.EventEndDate,
        EventDuration           = src.EventDuration,
        PrevStartDate           = src.PrevStartDate,
        PrevEndDate             = src.PrevEndDate,
        UnitTypeId              = src.UnitTypeId,
        UnitTypeDesc            = src.UnitTypeDesc,
        EventConfirmationStatus = src.EventConfirmationStatus,
        CogenChp                = src.CogenChp,
        EventDatePrecision      = src.EventDatePrecision,
        KickoffSlippage         = src.KickoffSlippage,
        EventComments           = src.EventComments,
        LiveDate                = src.LiveDate,
        ReleaseDate             = src.ReleaseDate,
        ModifiedAtUtc           = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RunDate, EventId, EventKind, EventType, EventCause, EventStatusDesc, UnitId, UnitName,
                UnitStatusDesc, IndustryCode, IndustryCodeDesc, PlantId, PlantName, PlantParentName,
                PlantOwnerName, PlantOperatorName, PlantAddressLine1, PlantCity, PlantState, PlantPostalCode,
                PlantCountry, PlantCounty, PlantLatitude, PlantLongitude, PlantPoint, AreaId, AreaName, Offshore,
                GasRegionId, GasRegionName, MarketRegionId, MarketRegionName, TradingRegionId, TradingRegionName,
                PowerTradeRegion, WorldRegionId, WorldRegionName, PecZone, PrimarySicId, UnitClassification,
                Derate, IsDerated, ProductId, ProductDescription, UnitCapacity, OfflineCapacity, OfflineCapacityUOM,
                EventStartDate, EventEndDate, EventDuration, PrevStartDate, PrevEndDate, UnitTypeId, UnitTypeDesc,
                EventConfirmationStatus, CogenChp, EventDatePrecision, KickoffSlippage, EventComments, LiveDate,
                ReleaseDate, ModifiedAtUtc)
        VALUES (@RunDate, src.EventId, src.EventKind, src.EventType, src.EventCause, src.EventStatusDesc, src.UnitId,
                src.UnitName, src.UnitStatusDesc, src.IndustryCode, src.IndustryCodeDesc, src.PlantId, src.PlantName,
                src.PlantParentName, src.PlantOwnerName, src.PlantOperatorName, src.PlantAddressLine1, src.PlantCity,
                src.PlantState, src.PlantPostalCode, src.PlantCountry, src.PlantCounty, src.PlantLatitude,
                src.PlantLongitude,
                CASE
                    WHEN src.PlantLatitude  IS NOT NULL AND src.PlantLongitude IS NOT NULL
                     AND src.PlantLatitude  BETWEEN  -90 AND  90
                     AND src.PlantLongitude BETWEEN -180 AND 180
                    THEN geography::Point(src.PlantLatitude, src.PlantLongitude, 4326)
                    ELSE NULL
                END,
                src.AreaId, src.AreaName, src.Offshore, src.GasRegionId, src.GasRegionName, src.MarketRegionId,
                src.MarketRegionName, src.TradingRegionId, src.TradingRegionName, src.PowerTradeRegion,
                src.WorldRegionId, src.WorldRegionName, src.PecZone, src.PrimarySicId, src.UnitClassification,
                src.Derate, src.IsDerated, src.ProductId, src.ProductDescription, src.UnitCapacity, src.OfflineCapacity,
                src.OfflineCapacityUOM, src.EventStartDate, src.EventEndDate, src.EventDuration, src.PrevStartDate,
                src.PrevEndDate, src.UnitTypeId, src.UnitTypeDesc, src.EventConfirmationStatus, src.CogenChp,
                src.EventDatePrecision, src.KickoffSlippage, src.EventComments, src.LiveDate, src.ReleaseDate,
                SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- =============================================================================
-- ID-CATALOG CENSUS MERGE PROCS (two-step summary->detail rework, design §7.5).
-- Mirror arm.usp_UpsertOfflineEvent's scalar-RunDate shape: @RunDate is passed as a
-- scalar (part of the PK, NOT in the TVP); every source row is upserted into the
-- @RunDate partition keyed by its own <Id>. Batch-dedup on <Id> before MERGE (last
-- wins — ROW_NUMBER, arbitrary tie-break since the reader emits one row per id per
-- run, so an in-batch duplicate carries an identical payload). The census is ID-ONLY,
-- so the whole row IS the key: the MATCHED branch has nothing to copy and only
-- re-stamps ModifiedAtUtc. DiscoveredAtUtc is stamped on INSERT only (first-seen this
-- run). No lat/long, FileLogId or geography (the fact side owns coordinates).
-- Returns SELECT @@ROWCOUNT AS RecordsProcessed.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_UpsertPlantSummary
    @RunDate DATE,
    @Rows    arm.PlantSummaryTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    IF @RunDate IS NULL
    BEGIN
        RAISERROR('usp_UpsertPlantSummary: @RunDate is required.', 16, 1);
        RETURN;
    END

    MERGE arm.PlantSummary AS tgt
    USING (
        SELECT PlantId
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY PlantId ORDER BY (SELECT NULL)) AS rn
            FROM @Rows
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate = @RunDate
       AND tgt.PlantId = src.PlantId
    WHEN MATCHED THEN UPDATE SET
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RunDate, PlantId, DiscoveredAtUtc, ModifiedAtUtc)
        VALUES (@RunDate, src.PlantId, SYSUTCDATETIME(), SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

CREATE OR ALTER PROCEDURE arm.usp_UpsertUnitSummary
    @RunDate DATE,
    @Rows    arm.UnitSummaryTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    IF @RunDate IS NULL
    BEGIN
        RAISERROR('usp_UpsertUnitSummary: @RunDate is required.', 16, 1);
        RETURN;
    END

    MERGE arm.UnitSummary AS tgt
    USING (
        SELECT UnitId
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY UnitId ORDER BY (SELECT NULL)) AS rn
            FROM @Rows
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate = @RunDate
       AND tgt.UnitId  = src.UnitId
    WHEN MATCHED THEN UPDATE SET
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RunDate, UnitId, DiscoveredAtUtc, ModifiedAtUtc)
        VALUES (@RunDate, src.UnitId, SYSUTCDATETIME(), SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

CREATE OR ALTER PROCEDURE arm.usp_UpsertOfflineEventSummary
    @RunDate DATE,
    @Rows    arm.OfflineEventSummaryTvp READONLY
AS
BEGIN
    SET NOCOUNT ON;

    IF @RunDate IS NULL
    BEGIN
        RAISERROR('usp_UpsertOfflineEventSummary: @RunDate is required.', 16, 1);
        RETURN;
    END

    MERGE arm.OfflineEventSummary AS tgt
    USING (
        SELECT EventId
        FROM (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY EventId ORDER BY (SELECT NULL)) AS rn
            FROM @Rows
        ) AS d
        WHERE d.rn = 1
    ) AS src
       ON  tgt.RunDate = @RunDate
       AND tgt.EventId = src.EventId
    WHEN MATCHED THEN UPDATE SET
        ModifiedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RunDate, EventId, DiscoveredAtUtc, ModifiedAtUtc)
        VALUES (@RunDate, src.EventId, SYSUTCDATETIME(), SYSUTCDATETIME());

    SELECT @@ROWCOUNT AS RecordsProcessed;
END
GO

-- ----------------------------------------------------------------------------
-- arm.usp_ValidateLoad — post-load OBSERVATIONAL anomaly report (design §9). Mirrors
-- AGSI/StormVista usp_ValidateLoad: ONE result set with a uniform shape so the C#
-- IirLoadValidator can log it generically. Observational only (no side effects); the
-- caller decides what is a hard failure (a legitimately sparse OfflineEvent day must
-- not fail the run). Plant/Unit are full-snapshot (checked over the whole table);
-- OfflineEvent is scoped to @RunDate (the day's partition).
--   CheckName      what was measured
--   Scope          the grouping key the count applies to
--   ExpectedCount  expected value where one exists, else NULL
--   ActualCount    the measured count
--   Detail         extra context
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE arm.usp_ValidateLoad
    @RunDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    IF @RunDate IS NULL
    BEGIN
        RAISERROR('usp_ValidateLoad: @RunDate is required.', 16, 1);
        RETURN;
    END

    ;WITH Report AS
    (
        -- 1) Plant row count (expect > 0 after a Plant load).
        SELECT
            CAST('PlantRowCount' AS VARCHAR(48))  AS CheckName,
            CAST(NULL AS NVARCHAR(200))           AS Scope,
            CAST(NULL AS INT)                     AS ExpectedCount,
            CAST(COUNT(1) AS BIGINT)              AS ActualCount,
            CAST(NULL AS NVARCHAR(400))           AS Detail
        FROM arm.Plant

        UNION ALL
        -- 2) Unit row count (expect > 0 after a Unit load).
        SELECT
            CAST('UnitRowCount' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.Unit

        UNION ALL
        -- 3) OfflineEvent row count for this run date (may legitimately be 0 — warn, don't fail).
        SELECT
            CAST('OfflineEventRowCount' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST(NULL AS NVARCHAR(400))
        FROM arm.OfflineEvent
        WHERE RunDate = @RunDate

        UNION ALL
        -- 4) Plant lat/long out of range (expect 0; these become PlantPoint = NULL, §7.3).
        SELECT
            CAST('PlantLatLongOutOfRange' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN (p.Latitude  IS NOT NULL AND (p.Latitude  < -90  OR p.Latitude  > 90))
                            OR (p.Longitude IS NOT NULL AND (p.Longitude < -180 OR p.Longitude > 180))
                          THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.Plant AS p

        UNION ALL
        -- 5) Plant geography consistency: PlantPoint non-null iff both coords present and in range (expect 0 mismatches).
        SELECT
            CAST('PlantGeographyConsistency' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE
                        WHEN (p.Latitude  IS NOT NULL AND p.Longitude IS NOT NULL
                              AND p.Latitude  BETWEEN  -90 AND  90
                              AND p.Longitude BETWEEN -180 AND 180)
                             AND p.PlantPoint IS NULL THEN 1
                        WHEN NOT (p.Latitude  IS NOT NULL AND p.Longitude IS NOT NULL
                                  AND p.Latitude  BETWEEN  -90 AND  90
                                  AND p.Longitude BETWEEN -180 AND 180)
                             AND p.PlantPoint IS NOT NULL THEN 1
                        ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.Plant AS p

        UNION ALL
        -- 6) Advisory FK coverage: Unit.PlantId values absent from arm.Plant (informational — may
        --    legitimately differ if the summary scoping differs between products; design §15.5).
        SELECT
            CAST('UnitPlantIdCoverageGap' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('Unit.PlantId not present in arm.Plant' AS NVARCHAR(400))
        FROM arm.Unit AS u
        WHERE u.PlantId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Plant AS p WHERE p.PlantId = u.PlantId)

        UNION ALL
        -- 7) Advisory FK coverage: OfflineEvent.PlantId absent from arm.Plant, this run date.
        SELECT
            CAST('OfflineEventPlantIdCoverageGap' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('OfflineEvent.PlantId not present in arm.Plant' AS NVARCHAR(400))
        FROM arm.OfflineEvent AS oe
        WHERE oe.RunDate = @RunDate
          AND oe.PlantId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Plant AS p WHERE p.PlantId = oe.PlantId)

        UNION ALL
        -- 8) Advisory FK coverage: OfflineEvent.UnitId absent from arm.Unit, this run date.
        SELECT
            CAST('OfflineEventUnitIdCoverageGap' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST(COUNT(1) AS BIGINT),
            CAST('OfflineEvent.UnitId not present in arm.Unit' AS NVARCHAR(400))
        FROM arm.OfflineEvent AS oe
        WHERE oe.RunDate = @RunDate
          AND oe.UnitId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM arm.Unit AS u WHERE u.UnitId = oe.UnitId)

        UNION ALL
        -- 9) Plant flag domain: every INT flag column should be in {0, 1, NULL} (catches a
        --    mis-mapped boolean encoding). Counts Plant rows with ANY flag outside {0,1}.
        SELECT
            CAST('PlantFlagDomain' AS VARCHAR(48)),
            CAST(NULL AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN p.CogenChp NOT IN (0,1) OR p.Metallurgical NOT IN (0,1) OR p.Thermal NOT IN (0,1)
                            OR p.Placer NOT IN (0,1) OR p.OpenPit NOT IN (0,1) OR p.Quarry NOT IN (0,1)
                            OR p.Strip NOT IN (0,1) OR p.Auger NOT IN (0,1) OR p.Dredging NOT IN (0,1)
                            OR p.Drift NOT IN (0,1) OR p.Shaft NOT IN (0,1) OR p.Slope NOT IN (0,1)
                            OR p.Longwall NOT IN (0,1) OR p.RoomPillar NOT IN (0,1) OR p.CutFill NOT IN (0,1)
                            OR p.Caving NOT IN (0,1) OR p.Stoping NOT IN (0,1) OR p.InSituSolution NOT IN (0,1)
                            OR p.Offshore NOT IN (0,1)
                          THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.Plant AS p

        UNION ALL
        -- 10) OfflineEvent flag domain for this run date: IsDerated/Offshore/CogenChp in {0,1,NULL}.
        SELECT
            CAST('OfflineEventFlagDomain' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(0 AS INT),
            CAST(SUM(CASE WHEN oe.IsDerated NOT IN (0,1) OR oe.Offshore NOT IN (0,1) OR oe.CogenChp NOT IN (0,1)
                          THEN 1 ELSE 0 END) AS BIGINT),
            CAST(CONCAT('total=', COUNT(1)) AS NVARCHAR(400))
        FROM arm.OfflineEvent AS oe
        WHERE oe.RunDate = @RunDate

        UNION ALL
        -- 11) Discovered-vs-persisted census (two-step, design §7.5/§9): STEP-1 census
        --     count vs persisted detail/fact count. ActualCount = census ids discovered
        --     for @RunDate; Detail carries the fact count. Plant/Unit facts are a full
        --     snapshot (not RunDate-partitioned), so their fact count is the whole table;
        --     OfflineEvent facts are compared within the same @RunDate partition. A large
        --     census>fact shortfall flags a STEP-2 coverage gap (informational).
        SELECT
            CAST('PlantCensusVsFact' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST((SELECT COUNT(1) FROM arm.PlantSummary WHERE RunDate = @RunDate) AS BIGINT),
            CAST(CONCAT('factRows=', (SELECT COUNT(1) FROM arm.Plant)) AS NVARCHAR(400))

        UNION ALL
        SELECT
            CAST('UnitCensusVsFact' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST((SELECT COUNT(1) FROM arm.UnitSummary WHERE RunDate = @RunDate) AS BIGINT),
            CAST(CONCAT('factRows=', (SELECT COUNT(1) FROM arm.Unit)) AS NVARCHAR(400))

        UNION ALL
        SELECT
            CAST('OfflineEventCensusVsFact' AS VARCHAR(48)),
            CAST(CONCAT('RunDate=', CONVERT(VARCHAR(10), @RunDate, 23)) AS NVARCHAR(200)),
            CAST(NULL AS INT),
            CAST((SELECT COUNT(1) FROM arm.OfflineEventSummary WHERE RunDate = @RunDate) AS BIGINT),
            CAST(CONCAT('factRows=', (SELECT COUNT(1) FROM arm.OfflineEvent WHERE RunDate = @RunDate)) AS NVARCHAR(400))
    )
    SELECT CheckName, Scope, ExpectedCount, ActualCount, Detail
    FROM Report
    ORDER BY CheckName, Scope;
END
GO
