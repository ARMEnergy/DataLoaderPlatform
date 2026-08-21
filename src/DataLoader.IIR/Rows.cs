using System.Text.Json;

namespace DataLoader.IIR;

/// <summary>
/// Common shape for every IIR fact row. The source reader stamps the <c>arm.FileLog</c> id onto each
/// row after mapping (design §4.5) — <c>FileLogId</c> is the FIRST TVP column for all three fact tables
/// (a provenance value, not a merge key). <see cref="EntityId"/> is the entity id (plantId/unitId/
/// eventId) used to key the STEP-1 lat/long carry-forward (§6.3), and <see cref="Latitude"/>/
/// <see cref="Longitude"/> are <b>settable</b> so the reader can fill them from the STEP-1 summary map
/// when the DETAIL element omits the coordinates. For Unit/OfflineEvent these alias the
/// <c>PlantLatitude</c>/<c>PlantLongitude</c> TVP columns.
/// </summary>
public interface IIirFactRow
{
    int FileLogId { get; set; }
    int EntityId { get; }
    double? Latitude { get; set; }
    double? Longitude { get; set; }
}

// =============================================================================
// The three per-endpoint row types + their row factories — the ONLY per-endpoint
// mapping code. Every business field is read TOLERANTLY (case-insensitive, nested
// objects flattened, dates Z[UTC]-stripped, flags normalized to 0/1); a blank/
// missing/unparseable business value degrades to NULL. A missing/unparseable PK
// (plantId/unitId/eventId) DROPS the row (returns null). Latitude/Longitude are
// carried as plain FLOAT; PlantPoint is built in the merge proc, never in C#.
// The property order below mirrors the sql/IIR/002 TVP column order exactly.
// =============================================================================

// -------------------------------------------------------------------- Plant
public sealed class IirPlantRow : IIirFactRow
{
    public int FileLogId { get; set; }
    public int EntityId => PlantId; // carry-forward key (§6.3)
    public int PlantId { get; init; }
    public string? PlantName { get; init; }
    public string? PlantStatusDesc { get; init; }
    public int? NoEmployees { get; init; }
    public DateTime? StartupDate { get; init; }
    public DateTime? LiveDate { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public int? OperationsLaborPreference { get; init; }
    public string? PrimaryFuel { get; init; }
    public string? SecondaryFuel { get; init; }
    public string? IndustryCode { get; init; }
    public string? IndustryCodeDesc { get; init; }
    public string? PrimarySicId { get; init; }
    public string? PrimarySicDesc { get; init; }
    public string? PecZone { get; init; }
    public string? MarketRegionId { get; init; }
    public string? MarketRegionName { get; init; }
    public string? ConfirmationStatus { get; init; }
    public string? NercRegion { get; init; }
    public string? NercSubRegionName { get; init; }
    public string? ElectricalConnectionName { get; init; }
    public int? TradingRegionId { get; init; }
    public string? TradingRegionName { get; init; }
    public int? CogenChp { get; init; }
    public int? Metallurgical { get; init; }
    public int? Thermal { get; init; }
    public int? Placer { get; init; }
    public int? OpenPit { get; init; }
    public int? Quarry { get; init; }
    public int? Strip { get; init; }
    public int? Auger { get; init; }
    public int? Dredging { get; init; }
    public int? Drift { get; init; }
    public int? Shaft { get; init; }
    public int? Slope { get; init; }
    public int? Longwall { get; init; }
    public int? RoomPillar { get; init; }
    public int? CutFill { get; init; }
    public int? Caving { get; init; }
    public int? Stoping { get; init; }
    public int? InSituSolution { get; init; }
    public double? Longitude { get; set; } // settable: STEP-1 lat/long carry-forward (§6.3)
    public double? Latitude { get; set; }
    public int? WorldRegionId { get; init; }
    public string? WorldRegionName { get; init; }
    public int? Offshore { get; init; }
    public string? MailingAddressLine1 { get; init; }
    public string? MailingCity { get; init; }
    public string? MailingStateName { get; init; }
    public string? MailingPostalCode { get; init; }
    public string? MailingCountryName { get; init; }
    public string? PhysicalAddressLine1 { get; init; }
    public string? PhysicalCity { get; init; }
    public string? PhysicalStateName { get; init; }
    public string? PhysicalPostalCode { get; init; }
    public string? PhysicalCountryName { get; init; }
    public string? PhysicalCountyName { get; init; }
    public string? PhoneCC { get; init; }
    public string? PhoneNumber { get; init; }
    public string? ParentCompanyId { get; init; }
    public string? ParentCompanyName { get; init; }
    public string? ParentCompanyWebsite { get; init; }
    public string? OperatorCompanyId { get; init; }
    public string? OperatorCompanyName { get; init; }
    public string? OperatorCompanyWebsite { get; init; }

    private static readonly string[] Physical = { "physicalAddress", "plantPhysicalAddress", "plantAddress" };
    private static readonly string[] Mailing = { "mailingAddress" };
    private static readonly string[] Phone = { "phone" };
    private static readonly string[] Parent = { "parent", "plantParent" };
    private static readonly string[] Operator = { "operator", "plantOperator" };

    public static IirPlantRow? From(JsonElement el, IirWorkUnit unit)
    {
        var plantId = IirParse.Int(el, "plantId");
        if (plantId is null) return null; // keyless row cannot be MERGEd

        return new IirPlantRow
        {
            PlantId = plantId.Value,
            PlantName = IirParse.Str(el, "plantName"),
            PlantStatusDesc = IirParse.Str(el, "plantStatusDesc"),
            NoEmployees = IirParse.Int(el, "noEmployees"),
            StartupDate = IirParse.DateStripZ(el, "startupDate"),
            LiveDate = IirParse.DateStripZ(el, "liveDate"),
            ReleaseDate = IirParse.DateStripZ(el, "releaseDate"),
            OperationsLaborPreference = IirParse.Int(el, "operationsLaborPreference", "operationsLaborPreferenceId"),
            PrimaryFuel = IirParse.Str(el, "primaryFuel"),
            SecondaryFuel = IirParse.Str(el, "secondaryFuel"),
            IndustryCode = IirParse.Str(el, "industryCode"),
            IndustryCodeDesc = IirParse.Str(el, "industryCodeDesc"),
            PrimarySicId = IirParse.Str(el, "primarySicId"),
            PrimarySicDesc = IirParse.Str(el, "primarySicDesc"),
            PecZone = IirParse.Str(el, "pecZone"),
            MarketRegionId = IirParse.Str(el, "marketRegionId"),
            MarketRegionName = IirParse.Str(el, "marketRegionName"),
            ConfirmationStatus = IirParse.Str(el, "confirmationStatus"),
            NercRegion = IirParse.Str(el, "nercRegion"),
            NercSubRegionName = IirParse.Str(el, "nercSubRegionName"),
            ElectricalConnectionName = IirParse.Str(el, "electricalConnectionName"),
            TradingRegionId = IirParse.Int(el, "tradingRegionId"),
            TradingRegionName = IirParse.Str(el, "tradingRegionName"),
            CogenChp = IirParse.Flag(el, "cogenChp"),
            Metallurgical = IirParse.Flag(el, "metallurgical"),
            Thermal = IirParse.Flag(el, "thermal"),
            Placer = IirParse.Flag(el, "placer"),
            OpenPit = IirParse.Flag(el, "openPit"),
            Quarry = IirParse.Flag(el, "quarry"),
            Strip = IirParse.Flag(el, "strip"),
            Auger = IirParse.Flag(el, "auger"),
            Dredging = IirParse.Flag(el, "dredging"),
            Drift = IirParse.Flag(el, "drift"),
            Shaft = IirParse.Flag(el, "shaft"),
            Slope = IirParse.Flag(el, "slope"),
            Longwall = IirParse.Flag(el, "longwall"),
            RoomPillar = IirParse.Flag(el, "roomPillar"),
            CutFill = IirParse.Flag(el, "cutFill"),
            Caving = IirParse.Flag(el, "caving"),
            Stoping = IirParse.Flag(el, "stoping"),
            InSituSolution = IirParse.Flag(el, "inSituSolution"),
            Longitude = IirParse.Float(el, "longitude", "plantLongitude", "plantlongitude"),
            Latitude = IirParse.Float(el, "latitude", "plantLatitude"),
            WorldRegionId = IirParse.Int(el, "worldRegionId"),
            WorldRegionName = IirParse.Str(el, "worldRegionName"),
            Offshore = IirParse.Flag(el, "offshore"),
            MailingAddressLine1 = IirParse.Str(IirParse.Nested(el, Mailing, "addressLine1", "address1", "addressLine")),
            MailingCity = IirParse.Str(IirParse.Nested(el, Mailing, "city")),
            MailingStateName = IirParse.Str(IirParse.Nested(el, Mailing, "stateName", "state")),
            MailingPostalCode = IirParse.Str(IirParse.Nested(el, Mailing, "postalCode", "zip", "zipCode")),
            MailingCountryName = IirParse.Str(IirParse.Nested(el, Mailing, "countryName", "country")),
            PhysicalAddressLine1 = IirParse.Str(IirParse.Nested(el, Physical, "addressLine1", "address1", "addressLine")),
            PhysicalCity = IirParse.Str(IirParse.Nested(el, Physical, "city")),
            PhysicalStateName = IirParse.Str(IirParse.Nested(el, Physical, "stateName", "state")),
            PhysicalPostalCode = IirParse.Str(IirParse.Nested(el, Physical, "postalCode", "zip", "zipCode")),
            PhysicalCountryName = IirParse.Str(IirParse.Nested(el, Physical, "countryName", "country")),
            PhysicalCountyName = IirParse.Str(IirParse.Nested(el, Physical, "countyName", "county")),
            PhoneCC = IirParse.Str(IirParse.Nested(el, Phone, "cc", "countryCode")),
            PhoneNumber = IirParse.Str(IirParse.Nested(el, Phone, "number", "phoneNumber")),
            ParentCompanyId = IirParse.Str(IirParse.Nested(el, Parent, "companyId", "id")),
            ParentCompanyName = IirParse.Str(IirParse.Nested(el, Parent, "companyName", "name")),
            ParentCompanyWebsite = IirParse.Str(IirParse.Nested(el, Parent, "companyWebsite", "website")),
            OperatorCompanyId = IirParse.Str(IirParse.Nested(el, Operator, "companyId", "id")),
            OperatorCompanyName = IirParse.Str(IirParse.Nested(el, Operator, "companyName", "name")),
            OperatorCompanyWebsite = IirParse.Str(IirParse.Nested(el, Operator, "companyWebsite", "website"))
        };
    }
}

// -------------------------------------------------------------------- Unit
public sealed class IirUnitRow : IIirFactRow
{
    public int FileLogId { get; set; }
    public int EntityId => UnitId; // carry-forward key (§6.3)

    /// <summary>Interface lat/long alias the PlantLatitude/PlantLongitude TVP columns (settable for §6.3 carry-forward).</summary>
    public double? Latitude { get => PlantLatitude; set => PlantLatitude = value; }
    public double? Longitude { get => PlantLongitude; set => PlantLongitude = value; }

    public int UnitId { get; init; }
    public string? UnitName { get; init; }
    public int? PlantId { get; init; }
    public string? PlantName { get; init; }
    public string? PlantStatusDesc { get; init; }
    public string? PlantAddressLine1 { get; init; }
    public string? PlantCity { get; init; }
    public string? PlantStateName { get; init; }
    public string? PlantPostalCode { get; init; }
    public string? PlantCountryName { get; init; }
    public string? PlantCountyName { get; init; }
    public string? MarketRegionId { get; init; }
    public string? MarketRegionName { get; init; }
    public int? WorldRegionId { get; init; }
    public string? WorldRegionName { get; init; }
    public int? TradingRegionId { get; init; }
    public string? TradingRegionName { get; init; }
    public string? UnitStatusDesc { get; init; }
    public string? UnitStatusGroup { get; init; }
    public int? HeaterCount { get; init; }
    public string? UnitTypeId { get; init; }
    public string? UnitTypeDesc { get; init; }
    public string? UnitTypeGroup { get; init; }
    public string? CapacityProductId { get; init; }
    public double? Capacity { get; init; }
    public string? CapacityUom { get; init; }
    public string? PrimarySicId { get; init; }
    public string? PrimarySicDesc { get; init; }
    public int? AreaId { get; init; }
    public string? AreaName { get; init; }
    public double? PlantLatitude { get; set; } // settable: STEP-1 lat/long carry-forward (§6.3)
    public double? PlantLongitude { get; set; }
    public int? Offshore { get; init; }
    public string? IndustryCode { get; init; }
    public string? IndustryCodeDesc { get; init; }
    public string? Technology { get; init; }
    public int? Renewable { get; init; }
    public int? CogenChp { get; init; }
    public string? PlantOperatorName { get; init; }
    public string? PlantOwnerName { get; init; }
    public string? PlantParentName { get; init; }
    public string? PlantPhone { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public DateTime? LiveDate { get; init; }

    private static readonly string[] PlantAddress = { "plantPhysicalAddress", "plantAddress", "physicalAddress" };
    private static readonly string[] Capacity_ = { "unitCapacity" };
    private static readonly string[] PlantOperator = { "plantOperator", "operator" };
    private static readonly string[] PlantOwner = { "plantOwner", "owner" };
    private static readonly string[] PlantParent = { "plantParent", "parent" };
    private static readonly string[] Phone = { "phone" };

    public static IirUnitRow? From(JsonElement el, IirWorkUnit unit)
    {
        var unitId = IirParse.Int(el, "unitId");
        if (unitId is null) return null;

        return new IirUnitRow
        {
            UnitId = unitId.Value,
            UnitName = IirParse.Str(el, "unitName"),
            PlantId = IirParse.Int(el, "plantId"),
            PlantName = IirParse.Str(el, "plantName"),
            PlantStatusDesc = IirParse.Str(el, "plantStatusDesc"),
            PlantAddressLine1 = IirParse.Str(IirParse.Nested(el, PlantAddress, "addressLine1", "address1", "addressLine")),
            PlantCity = IirParse.Str(IirParse.Nested(el, PlantAddress, "city")),
            PlantStateName = IirParse.Str(IirParse.Nested(el, PlantAddress, "stateName", "state")),
            PlantPostalCode = IirParse.Str(IirParse.Nested(el, PlantAddress, "postalCode", "zip", "zipCode")),
            PlantCountryName = IirParse.Str(IirParse.Nested(el, PlantAddress, "countryName", "country")),
            PlantCountyName = IirParse.Str(IirParse.Nested(el, PlantAddress, "countyName", "county")),
            MarketRegionId = IirParse.Str(el, "marketRegionId"),
            MarketRegionName = IirParse.Str(el, "marketRegionName"),
            WorldRegionId = IirParse.Int(el, "worldRegionId"),
            WorldRegionName = IirParse.Str(el, "worldRegionName"),
            TradingRegionId = IirParse.Int(el, "tradingRegionId"),
            TradingRegionName = IirParse.Str(el, "tradingRegionName"),
            UnitStatusDesc = IirParse.Str(el, "unitStatusDesc"),
            UnitStatusGroup = IirParse.Str(el, "unitStatusGroup"),
            HeaterCount = IirParse.Int(el, "heaterCount"),
            UnitTypeId = IirParse.Str(el, "unitTypeId"),
            UnitTypeDesc = IirParse.Str(el, "unitTypeDesc"),
            UnitTypeGroup = IirParse.Str(el, "unitTypeGroup"),
            CapacityProductId = IirParse.Str(IirParse.Nested(el, Capacity_, "capacityProductId", "productId"))
                                ?? IirParse.Str(el, "capacityProductId"),
            Capacity = IirParse.Float(IirParse.Nested(el, Capacity_, "capacity")) ?? IirParse.Float(el, "capacity"),
            CapacityUom = IirParse.Str(IirParse.Nested(el, Capacity_, "capacityUom", "uom"))
                          ?? IirParse.Str(el, "capacityUom"),
            PrimarySicId = IirParse.Str(el, "primarySicId"),
            PrimarySicDesc = IirParse.Str(el, "primarySicDesc"),
            AreaId = IirParse.Int(el, "areaId"),
            AreaName = IirParse.Str(el, "areaName"),
            PlantLatitude = IirParse.Float(el, "plantLatitude", "latitude"),
            PlantLongitude = IirParse.Float(el, "plantLongitude", "plantlongitude", "longitude"),
            Offshore = IirParse.Flag(el, "offshore"),
            IndustryCode = IirParse.Str(el, "industryCode"),
            IndustryCodeDesc = IirParse.Str(el, "industryCodeDesc"),
            Technology = IirParse.Str(el, "technology"),
            Renewable = IirParse.Flag(el, "renewable"),
            CogenChp = IirParse.Flag(el, "cogenChp"),
            PlantOperatorName = IirParse.Str(el, "plantOperatorName")
                                ?? IirParse.Str(IirParse.Nested(el, PlantOperator, "companyName", "name")),
            PlantOwnerName = IirParse.Str(el, "plantOwnerName")
                             ?? IirParse.Str(IirParse.Nested(el, PlantOwner, "companyName", "name")),
            PlantParentName = IirParse.Str(el, "plantParentName")
                              ?? IirParse.Str(IirParse.Nested(el, PlantParent, "companyName", "name")),
            PlantPhone = IirParse.Str(el, "plantPhone")
                         ?? IirParse.Str(IirParse.Nested(el, Phone, "number", "phoneNumber")),
            ReleaseDate = IirParse.DateStripZ(el, "releaseDate"),
            LiveDate = IirParse.DateStripZ(el, "liveDate")
        };
    }
}

// -------------------------------------------------------------------- OfflineEvent
public sealed class IirOfflineEventRow : IIirFactRow
{
    public int FileLogId { get; set; }
    public int EntityId => EventId; // carry-forward key (§6.3)

    /// <summary>Interface lat/long alias the PlantLatitude/PlantLongitude TVP columns (settable for §6.3 carry-forward).</summary>
    public double? Latitude { get => PlantLatitude; set => PlantLatitude = value; }
    public double? Longitude { get => PlantLongitude; set => PlantLongitude = value; }

    /// <summary>The Central run date (design §5.4). NOT a TVP column — passed as the scalar @RunDate proc param (part of the PK).</summary>
    public DateOnly RunDate { get; init; }

    public int EventId { get; init; }
    public string? EventKind { get; init; }
    public string? EventType { get; init; }
    public string? EventCause { get; init; }
    public string? EventStatusDesc { get; init; }
    public int? UnitId { get; init; }
    public string? UnitName { get; init; }
    public string? UnitStatusDesc { get; init; }
    public string? IndustryCode { get; init; }
    public string? IndustryCodeDesc { get; init; }
    public int? PlantId { get; init; }
    public string? PlantName { get; init; }
    public string? PlantParentName { get; init; }
    public string? PlantOwnerName { get; init; }
    public string? PlantOperatorName { get; init; }
    public string? PlantAddressLine1 { get; init; }
    public string? PlantCity { get; init; }
    public string? PlantState { get; init; }
    public string? PlantPostalCode { get; init; }
    public string? PlantCountry { get; init; }
    public string? PlantCounty { get; init; }
    public double? PlantLatitude { get; set; } // settable: STEP-1 lat/long carry-forward (§6.3)
    public double? PlantLongitude { get; set; }
    public int? AreaId { get; init; }
    public string? AreaName { get; init; }
    public int? Offshore { get; init; }
    public string? GasRegionId { get; init; }
    public string? GasRegionName { get; init; }
    public string? MarketRegionId { get; init; }
    public string? MarketRegionName { get; init; }
    public int? TradingRegionId { get; init; }
    public string? TradingRegionName { get; init; }
    public string? PowerTradeRegion { get; init; }
    public int? WorldRegionId { get; init; }
    public string? WorldRegionName { get; init; }
    public string? PecZone { get; init; }
    public string? PrimarySicId { get; init; }
    public string? UnitClassification { get; init; }
    public double? Derate { get; init; }
    public int? IsDerated { get; init; }
    public int? ProductId { get; init; }
    public string? ProductDescription { get; init; }
    public double? UnitCapacity { get; init; }
    public double? OfflineCapacity { get; init; }
    public string? OfflineCapacityUOM { get; init; }
    public DateTime? EventStartDate { get; init; }
    public DateTime? EventEndDate { get; init; }
    public int? EventDuration { get; init; }
    public DateTime? PrevStartDate { get; init; }
    public DateTime? PrevEndDate { get; init; }
    public string? UnitTypeId { get; init; }
    public string? UnitTypeDesc { get; init; }
    public string? EventConfirmationStatus { get; init; }
    public int? CogenChp { get; init; }
    public string? EventDatePrecision { get; init; }
    public int? KickoffSlippage { get; init; }
    public string? EventComments { get; init; }
    public DateTime? LiveDate { get; init; }
    public DateTime? ReleaseDate { get; init; }

    private static readonly string[] PlantAddress = { "plantPhysicalAddress", "plantAddress", "physicalAddress" };
    private static readonly string[] PlantParent = { "plantParent", "parent" };
    private static readonly string[] PlantOwner = { "plantOwner", "owner" };
    private static readonly string[] PlantOperator = { "plantOperator", "operator" };

    public static IirOfflineEventRow? From(JsonElement el, IirWorkUnit unit)
    {
        var eventId = IirParse.Int(el, "eventId");
        if (eventId is null) return null;

        return new IirOfflineEventRow
        {
            RunDate = unit.RunDate, // stamped (§5.4) — not from the API
            EventId = eventId.Value,
            EventKind = IirParse.Str(el, "eventKind", "eventKindDesc"),
            EventType = IirParse.Str(el, "eventType", "eventTypeDesc"),
            EventCause = IirParse.Str(el, "eventCause", "eventCauseDesc"),
            EventStatusDesc = IirParse.Str(el, "eventStatusDesc", "derivedEventStatusDesc"),
            UnitId = IirParse.Int(el, "unitId"),
            UnitName = IirParse.Str(el, "unitName"),
            UnitStatusDesc = IirParse.Str(el, "unitStatusDesc"),
            IndustryCode = IirParse.Str(el, "industryCode"),
            IndustryCodeDesc = IirParse.Str(el, "industryCodeDesc"),
            PlantId = IirParse.Int(el, "plantId"),
            PlantName = IirParse.Str(el, "plantName"),
            PlantParentName = IirParse.Str(el, "plantParentName")
                              ?? IirParse.Str(IirParse.Nested(el, PlantParent, "companyName", "name")),
            PlantOwnerName = IirParse.Str(el, "plantOwnerName")
                             ?? IirParse.Str(IirParse.Nested(el, PlantOwner, "companyName", "name")),
            PlantOperatorName = IirParse.Str(el, "plantOperatorName")
                                ?? IirParse.Str(IirParse.Nested(el, PlantOperator, "companyName", "name")),
            PlantAddressLine1 = IirParse.Str(IirParse.Nested(el, PlantAddress, "addressLine1", "address1", "addressLine")),
            PlantCity = IirParse.Str(IirParse.Nested(el, PlantAddress, "city")),
            PlantState = IirParse.Str(IirParse.Nested(el, PlantAddress, "stateName", "state")),
            PlantPostalCode = IirParse.Str(IirParse.Nested(el, PlantAddress, "postalCode", "zip", "zipCode")),
            PlantCountry = IirParse.Str(IirParse.Nested(el, PlantAddress, "countryName", "country")),
            PlantCounty = IirParse.Str(IirParse.Nested(el, PlantAddress, "countyName", "county")),
            PlantLatitude = IirParse.Float(el, "plantLatitude", "latitude"),
            PlantLongitude = IirParse.Float(el, "plantLongitude", "plantlongitude", "longitude"),
            AreaId = IirParse.Int(el, "areaId"),
            AreaName = IirParse.Str(el, "areaName"),
            Offshore = IirParse.Flag(el, "offshore"),
            GasRegionId = IirParse.Str(el, "gasRegionId"),
            GasRegionName = IirParse.Str(el, "gasRegionName"),
            MarketRegionId = IirParse.Str(el, "marketRegionId"),
            MarketRegionName = IirParse.Str(el, "marketRegionName"),
            TradingRegionId = IirParse.Int(el, "tradingRegionId"),
            TradingRegionName = IirParse.Str(el, "tradingRegionName"),
            PowerTradeRegion = IirParse.Str(el, "powerTradeRegion"),
            WorldRegionId = IirParse.Int(el, "worldRegionId"),
            WorldRegionName = IirParse.Str(el, "worldRegionName"),
            PecZone = IirParse.Str(el, "pecZone"),
            PrimarySicId = IirParse.Str(el, "primarySicId"),
            UnitClassification = IirParse.Str(el, "unitClassification"),
            Derate = IirParse.Float(el, "derate"),
            IsDerated = IirParse.Flag(el, "isDerated"),
            ProductId = IirParse.Int(el, "productId"),
            ProductDescription = IirParse.Str(el, "productDescription", "productDesc"),
            UnitCapacity = IirParse.Float(el, "unitCapacity") ?? IirParse.Float(IirParse.Nested(el, new[] { "unitCapacity" }, "capacity")),
            OfflineCapacity = IirParse.Float(el, "offlineCapacity"),
            OfflineCapacityUOM = IirParse.Str(el, "offlineCapacityUom", "offlineCapacityUOM"),
            EventStartDate = IirParse.DateStripZ(el, "eventStartDate"),
            EventEndDate = IirParse.DateStripZ(el, "eventEndDate"),
            EventDuration = IirParse.Int(el, "eventDuration"),
            PrevStartDate = IirParse.DateStripZ(el, "prevStartDate", "previousStartDate"),
            PrevEndDate = IirParse.DateStripZ(el, "prevEndDate", "previousEndDate"),
            UnitTypeId = IirParse.Str(el, "unitTypeId"),
            UnitTypeDesc = IirParse.Str(el, "unitTypeDesc"),
            EventConfirmationStatus = IirParse.Str(el, "eventConfirmationStatus", "confirmationStatus"),
            CogenChp = IirParse.Flag(el, "cogenChp"),
            EventDatePrecision = IirParse.Str(el, "eventDatePrecision"),
            KickoffSlippage = IirParse.Int(el, "kickoffSlippage"),
            EventComments = IirParse.Str(el, "eventComments", "comments"),
            LiveDate = IirParse.DateStripZ(el, "liveDate"),
            ReleaseDate = IirParse.DateStripZ(el, "releaseDate")
        };
    }
}

// -------------------------------------------------------------------- Summary (id-catalog census)
/// <summary>
/// One id-catalog census row written by STEP 1 of the two-step pull (design §4.4/§7.5). A single row
/// type serves all three endpoints because the census TVPs are positionally identical and ID-ONLY
/// (<c>(&lt;Id&gt; INT)</c>, sql/IIR/002); the per-endpoint proc/TVP binding lives in the census sink.
/// <see cref="RunDate"/> is the scalar <c>@RunDate</c> proc param (NOT a TVP column); there is NO
/// <c>FileLogId</c> column on the census TVP.
/// <para>
/// <see cref="Latitude"/>/<see cref="Longitude"/> are read from the STEP-1 payload but are NOT
/// persisted to the census table — they exist only to seed the reader's in-memory §6.3 carry-forward
/// map, which fills a detail row's coordinates when the STEP-2 detail record omits them.
/// </para>
/// </summary>
public sealed class IirSummaryRow
{
    public DateOnly RunDate { get; init; }
    public int EntityId { get; init; }

    // In-memory only (§6.3 carry-forward into the fact rows) — NOT census TVP columns.
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    // Lat/long candidate names shared by STEP 1 across all three endpoints (⚠ casing/presence, design §4).
    private static readonly string[] LatNames = { "plantLatitude", "latitude" };
    private static readonly string[] LonNames = { "plantLongitude", "plantlongitude", "longitude" };

    /// <summary>
    /// Tolerant STEP-1 extraction: reads the entity id (<paramref name="idField"/>, tolerant) and the
    /// summary lat/long (case-insensitive candidate names). Returns null when the id is missing/unparseable
    /// (a keyless summary record cannot seed the census or the id list).
    /// </summary>
    public static IirSummaryRow? From(JsonElement el, string idField, DateOnly runDate)
    {
        var id = IirParse.Int(el, idField);
        if (id is null) return null;

        return new IirSummaryRow
        {
            RunDate = runDate,
            EntityId = id.Value,
            Latitude = IirParse.Float(el, LatNames),
            Longitude = IirParse.Float(el, LonNames)
        };
    }
}
