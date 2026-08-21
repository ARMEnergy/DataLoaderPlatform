using System.Text.Json;
using Xunit;

namespace DataLoader.IIR.Tests;

/// <summary>
/// The three per-endpoint row factories (<c>Rows.cs</c> <c>From(elem, unit)</c>) — the ONLY typed
/// mapping code (design §6). Each maps a representative JSON element to its target row: every populated
/// field to the right property, nested objects flattened (address / phone / parent / operator / owner),
/// dates <c>Z[UTC]</c>-stripped, flags normalized to 0/1, the OfflineEvent <c>RunDate</c> stamped from
/// the work unit. A missing/unparseable PK drops the row; absent lat/long stays null (geography is
/// built in the proc). Includes the <b>Fix #3</b> float-formatted-integer PK regression.
/// </summary>
public class RowFactoryTests
{
    private static JsonElement E(string json) => Json.Element(json);

    private static IirWorkUnit Unit(DateOnly? runDate = null) => new()
    {
        EndpointId = "X",
        RunDate = runDate ?? new DateOnly(2026, 8, 20),
        QueryString = "",
        KeyValue = "k",
        DisplayNameValue = "d"
    };

    // ================================================================ Plant

    private const string PlantJson = """
    {
      "plantId": 3207542,
      "plantName": "Acme Refinery",
      "plantStatusDesc": "Operating",
      "noEmployees": 250,
      "startupDate": "1998-05-01T00:00:00Z[UTC]",
      "liveDate": "2019-01-29T22:39:21Z[UTC]",
      "releaseDate": "2020-06-15",
      "primaryFuel": "Natural Gas",
      "industryCode": "01",
      "industryCodeDesc": "Power",
      "pecZone": "TX*05",
      "marketRegionId": "ERCOT",
      "marketRegionName": "Texas",
      "tradingRegionId": 7,
      "tradingRegionName": "Gulf",
      "cogenChp": 1,
      "offshore": 0,
      "metallurgical": "Y",
      "longitude": -95.363,
      "latitude": 29.760,
      "worldRegionId": 1,
      "worldRegionName": "North America",
      "mailingAddress": { "addressLine1": "PO Box 5", "city": "Dallas", "stateName": "TX", "postalCode": "75001", "countryName": "USA" },
      "physicalAddress": { "address1": "123 Main St", "city": "Houston", "state": "TX", "zip": "77001", "country": "USA", "countyName": "Harris" },
      "phone": { "cc": 1, "number": "555-1234" },
      "parent": { "companyId": "P100", "companyName": "Acme Holdings", "companyWebsite": "acme.example" },
      "operator": { "id": "O200", "name": "Acme Operations", "website": "ops.example" }
    }
    """;

    [Fact]
    public void Plant_MapsScalars_Flags_Dates_LatLong()
    {
        var r = IirPlantRow.From(E(PlantJson), Unit())!;
        Assert.Equal(3207542, r.PlantId);
        Assert.Equal("Acme Refinery", r.PlantName);
        Assert.Equal(250, r.NoEmployees);
        Assert.Equal(new DateTime(1998, 5, 1), r.StartupDate);
        Assert.Equal(new DateTime(2019, 1, 29, 22, 39, 21), r.LiveDate);   // Z[UTC] stripped
        Assert.Equal(new DateTime(2020, 6, 15), r.ReleaseDate!.Value.Date); // plain date accepted
        Assert.Equal(7, r.TradingRegionId);
        Assert.Equal(1, r.CogenChp);          // JSON 1
        Assert.Equal(0, r.Offshore);          // JSON 0
        Assert.Equal(1, r.Metallurgical);     // "Y" → 1
        Assert.Equal(-95.363, r.Longitude);
        Assert.Equal(29.760, r.Latitude);
        Assert.Equal(0, r.FileLogId);         // stamped by the reader, not the factory
    }

    [Fact]
    public void Plant_FlattensNestedAddressPhoneCompanyObjects_TolerantFieldNames()
    {
        var r = IirPlantRow.From(E(PlantJson), Unit())!;
        // mailingAddress → Mailing*
        Assert.Equal("PO Box 5", r.MailingAddressLine1);
        Assert.Equal("Dallas", r.MailingCity);
        Assert.Equal("TX", r.MailingStateName);
        // physicalAddress with VARIANT field names (address1/state/zip/country)
        Assert.Equal("123 Main St", r.PhysicalAddressLine1); // from address1
        Assert.Equal("Houston", r.PhysicalCity);
        Assert.Equal("TX", r.PhysicalStateName);             // from state
        Assert.Equal("77001", r.PhysicalPostalCode);         // from zip
        Assert.Equal("USA", r.PhysicalCountryName);          // from country
        Assert.Equal("Harris", r.PhysicalCountyName);
        // phone (cc is a JSON number → cast to text)
        Assert.Equal("1", r.PhoneCC);
        Assert.Equal("555-1234", r.PhoneNumber);
        // parent + operator (operator uses id/name/website variants)
        Assert.Equal("P100", r.ParentCompanyId);
        Assert.Equal("Acme Holdings", r.ParentCompanyName);
        Assert.Equal("acme.example", r.ParentCompanyWebsite);
        Assert.Equal("O200", r.OperatorCompanyId);
        Assert.Equal("Acme Operations", r.OperatorCompanyName);
        Assert.Equal("ops.example", r.OperatorCompanyWebsite);
    }

    [Fact]
    public void Plant_AbsentLatLong_StayNull_NoGeographyAttempted()
    {
        // Geography is built in-proc; a row with no coordinates simply carries null lat/long.
        var r = IirPlantRow.From(E("""{ "plantId": 1, "plantName": "No Coords" }"""), Unit())!;
        Assert.Null(r.Latitude);
        Assert.Null(r.Longitude);
    }

    [Theory]
    [InlineData("""{ "plantId": 3207542.0, "plantName": "Float PK" }""")]     // JSON number 3207542.0
    [InlineData("""{ "plantId": "3207542.0", "plantName": "Float PK" }""")]   // string "3207542.0"
    public void Plant_Fix3_FloatFormattedIntegerId_ParsesToPk_RowNotDropped(string json)
    {
        var r = IirPlantRow.From(E(json), Unit());
        Assert.NotNull(r);
        Assert.Equal(3207542, r!.PlantId);
    }

    [Fact]
    public void Plant_MissingPlantId_DropsRow() =>
        Assert.Null(IirPlantRow.From(E("""{ "plantName": "keyless" }"""), Unit()));

    // ================================================================ Unit

    private const string UnitJson = """
    {
      "unitId": 55001,
      "unitName": "CDU-1",
      "plantId": 3207542,
      "plantName": "Acme Refinery",
      "plantPhysicalAddress": { "addressLine1": "123 Main St", "city": "Houston", "stateName": "TX", "postalCode": "77001", "countryName": "USA", "countyName": "Harris" },
      "unitCapacity": { "capacityProductId": "CP1", "capacity": 125000.5, "capacityUom": "BPD" },
      "plantLatitude": 29.76,
      "plantLongitude": -95.36,
      "renewable": "false",
      "cogenChp": true,
      "plantOperator": { "companyName": "Acme Operations" },
      "plantOwner": { "name": "Acme Owner" },
      "plantParentName": "Acme Holdings",
      "phone": { "number": "555-9999" },
      "releaseDate": "2021-03-01T00:00:00Z[UTC]"
    }
    """;

    [Fact]
    public void Unit_MapsScalars_NestedCapacity_Address_Company_Flags()
    {
        var r = IirUnitRow.From(E(UnitJson), Unit())!;
        Assert.Equal(55001, r.UnitId);
        Assert.Equal(3207542, r.PlantId);
        Assert.Equal("CDU-1", r.UnitName);
        // plantPhysicalAddress flattened
        Assert.Equal("123 Main St", r.PlantAddressLine1);
        Assert.Equal("Houston", r.PlantCity);
        Assert.Equal("Harris", r.PlantCountyName);
        // nested unitCapacity object
        Assert.Equal("CP1", r.CapacityProductId);
        Assert.Equal(125000.5, r.Capacity);
        Assert.Equal("BPD", r.CapacityUom);
        Assert.Equal(29.76, r.PlantLatitude);
        Assert.Equal(-95.36, r.PlantLongitude);
        // flags
        Assert.Equal(0, r.Renewable);   // "false" → 0
        Assert.Equal(1, r.CogenChp);    // true → 1
        // company names via nested objects / direct field / phone fallback
        Assert.Equal("Acme Operations", r.PlantOperatorName); // plantOperator.companyName
        Assert.Equal("Acme Owner", r.PlantOwnerName);         // plantOwner.name
        Assert.Equal("Acme Holdings", r.PlantParentName);     // direct field
        Assert.Equal("555-9999", r.PlantPhone);               // phone.number fallback
        Assert.Equal(new DateTime(2021, 3, 1), r.ReleaseDate);
    }

    [Fact]
    public void Unit_MissingUnitId_DropsRow() =>
        Assert.Null(IirUnitRow.From(E("""{ "plantId": 1 }"""), Unit()));

    // ================================================================ OfflineEvent

    private const string OfflineEventJson = """
    {
      "eventId": 987654,
      "eventKind": "O",
      "eventType": "Turnaround",
      "eventStatusDesc": "Ongoing",
      "unitId": 55001,
      "plantId": 3207542,
      "plantPhysicalAddress": { "addressLine1": "123 Main St", "city": "Houston", "stateName": "TX", "postalCode": "77001", "countryName": "USA", "countyName": "Harris" },
      "plantParentName": "Acme Holdings",
      "plantLatitude": 29.76,
      "plantLongitude": -95.36,
      "offshore": 0,
      "derate": 12.5,
      "isDerated": 1,
      "unitCapacity": 125000,
      "offlineCapacity": 50000,
      "eventStartDate": "2026-08-01T00:00:00Z[UTC]",
      "eventEndDate": "2026-08-15T00:00:00Z[UTC]",
      "eventDuration": 14,
      "cogenChp": "N"
    }
    """;

    [Fact]
    public void OfflineEvent_MapsScalars_Address_Measures_ScalarUnitCapacity_Flags()
    {
        var r = IirOfflineEventRow.From(E(OfflineEventJson), Unit(new DateOnly(2026, 8, 19)))!;
        Assert.Equal(987654, r.EventId);
        Assert.Equal("O", r.EventKind);
        Assert.Equal("Turnaround", r.EventType);
        Assert.Equal("Ongoing", r.EventStatusDesc);
        Assert.Equal(55001, r.UnitId);
        Assert.Equal(3207542, r.PlantId);
        Assert.Equal("123 Main St", r.PlantAddressLine1);
        Assert.Equal("TX", r.PlantState);          // plantPhysicalAddress.stateName
        Assert.Equal("Harris", r.PlantCounty);
        Assert.Equal("Acme Holdings", r.PlantParentName);
        Assert.Equal(29.76, r.PlantLatitude);
        Assert.Equal(-95.36, r.PlantLongitude);
        Assert.Equal(12.5, r.Derate);
        Assert.Equal(1, r.IsDerated);
        Assert.Equal(125000d, r.UnitCapacity);     // here a scalar (contrast the Unit nested object)
        Assert.Equal(50000d, r.OfflineCapacity);
        Assert.Equal(new DateTime(2026, 8, 1), r.EventStartDate);
        Assert.Equal(new DateTime(2026, 8, 15), r.EventEndDate);
        Assert.Equal(14, r.EventDuration);
        Assert.Equal(0, r.CogenChp);               // "N" → 0
    }

    [Fact]
    public void OfflineEvent_StampsRunDateFromWorkUnit()
    {
        var runDate = new DateOnly(2026, 8, 19);
        var r = IirOfflineEventRow.From(E(OfflineEventJson), Unit(runDate))!;
        Assert.Equal(runDate, r.RunDate); // stamped from the work unit, not the API payload
    }

    [Fact]
    public void OfflineEvent_MissingEventId_DropsRow() =>
        Assert.Null(IirOfflineEventRow.From(E("""{ "unitId": 1 }"""), Unit()));
}
