namespace DataLoader.IHSPointLogic.Tests;

/// <summary>
/// Verbatim JSON element shapes captured from the real IHSPointLogic API (see docs/apis/IHSPointLogic.md
/// and the probe payloads). Kept as single-object literals so each row-factory / source-reader test uses
/// the true field names, casing and value forms (string-numbers, mixed date formats, JSON nulls).
/// </summary>
internal static class Samples
{
    // ---- Tier 0 dimensions (archetype A) ----

    // lookup_region / lookup_state / lookup_pointType / lookup_pointStatus / lookup_pipelineNoticeCategory
    public const string RegionElem = """{ "name": "Midcon", "id": 26105 }""";
    public const string PipelineElem = """{ "name": "Algonquin Gas Transmission", "id": 2, "legacyname": "Algonquin" }""";

    // lookup_point — note the verified pipelineid_0 suffix; served inside a {PagingInfo,Data} wrapper.
    public const string PointElem = """{ "pipelineid_0": 59, "id": 691, "pointtypeid": 3, "pointstatusid": 2, "name": "Amoco" }""";

    // pointmetadata_withids — designcapacity is a JSON STRING-number; drn/county/region are " " (blank),
    // locprop is "" (empty); latitude/longitude present; the 3rd real row has drn "10399" + non-zero coords.
    public const string PointMetadataElem = """
    {
        "pointid": 33071, "pointlciid": "ACADA0001", "drn": " ", "pointname": "LAReg112",
        "pointtypeid": 9, "pointtype": "Not Classified", "stateid": 4536, "state": "Louisiana",
        "county": " ", "region": " ", "pipelineid": 1, "pipelinedisplayname": "Acadian Pipeline",
        "pointisactive": false, "designcapacity": "0.00000000", "flowdirectionid": 2,
        "flowdirection": "Delivery", "displayname": "Total 112", "locprop": "",
        "pointlatitude": 0, "pointlongitude": 0
    }
    """;

    // pipelinenotice_search — id is a long; posteddate/effectivedate carry -05:00 offsets; 2nd real row has
    // NO effectivedate/enddate (→ null).
    public const string NoticeElem = """
    {
        "subject": "2026 Annual Facility Outage Update as of 08/18/2026", "format": "PDF",
        "posteddate": "2026-08-18 15:27:29.544 -05:00", "externalid": "1013038", "categoryid": 14,
        "iscritical": false, "contentlink": "https://connect.spglobal.com/pointlogic/get-notice/635182",
        "id": 635182, "pipelineid": 521, "effectivedate": "2026-08-18 15:27:27.638 -05:00"
    }
    """;

    public const string NoticeElemNoDates = """
    {
        "subject": "Maintenance Schedule Update", "format": "HTML", "posteddate": "2026-08-18 09:12:00 -08:00",
        "externalid": "34024~tpdflore~N", "categoryid": 14, "iscritical": false,
        "contentlink": "https://connect.spglobal.com/pointlogic/get-notice/635183", "id": 635183, "pipelineid": 189
    }
    """;

    // ---- Tier 0 fact snapshots (archetype B) ----

    // demandforecast_region — carries NO report date (stamped from the run's UTC date).
    public const string DemandForecastRegionElem = """
    { "date": "2026-08-19", "region": "Canada", "subregion": "Eastern Canada",
      "departurefromnormalf": -0.015251, "normaltemperaturef": 68.352891 }
    """;

    // demandforecast_uslower48 — Region from regionname, Date from the long pointreadingaggregate_endingdate.
    public const string DemandForecastUsLower48Elem = """
    { "regionname": "United States of America", "pointreadingaggregate_endingdate": "2026-08-18",
      "power": 48.05198169, "industrial": 21.16350847, "residentialcommercial": 8.49916755, "subtotal": 77.71465771 }
    """;

    // gasproduction_producingarea — reporteddate is a DATETIME2 "yyyy-MM-dd HH:mm".
    public const string GasProductionElem = """
    { "reporteddate": "2026-08-18 13:33", "referencedate": "2026-07-19", "region": "Gulf of Mexico",
      "producingarea": "Gulf of Mexico", "state": "Gulf of Mexico", "dryfactoredvalue": 1825.3466644,
      "wellheadvalue": 2194.11088 }
    """;

    // marketbalances_uslower48 — 16 measures keyed by timeperiod; balancingitem is negative.
    public const string MarketBalancesElem = """
    { "timeperiod": "2026-08-18", "wellhead": 123.850395, "productionloss": 13.682683, "drygas": 110.167711,
      "canadaimports": 5.377167, "lngsendout": 0.008712, "totalsupply": 115.55359, "power": 48.703569,
      "industrial": 21.652997, "residentialcommercial": 8.713889, "subtotal": 79.070457, "mexicoexports": 7.63877,
      "lngfeedgas": 18.306489, "pipeloss": 7.22124, "totaldemand": 112.236958, "storage": 3.346618,
      "balancingitem": -0.029986 }
    """;

    // supplyDemand/marketsHistory — 16 measures keyed by Date; served inside a {PagingInfo,Data} wrapper.
    public const string SupplyAndDemandElem = """
    { "date": "2026-06-19", "wellhead": 124.063469, "productionloss": 13.699278, "drygas": 110.364191,
      "canadaimports": 5.526654, "lngsendout": 0.01075, "totalsupply": 115.901596, "power": 38.96085,
      "industrial": 21.971936, "residentialcommercial": 7.468552, "subtotal": 68.401339, "mexicoexports": 7.733265,
      "lngfeedgas": 18.436361, "pipeloss": 6.902588, "totaldemand": 101.473554, "storage": 14.075205,
      "balancingitem": 0.352836 }
    """;

    public const string ModeledDemandElem = """
    { "referencedate": "2026-07-19", "pleproductname": "Electric Power Plants", "regionname": "Northeast",
      "volume": 10968.2019 }
    """;

    // pipelineflow_throughputs — flowdate is MM/dd/yyyy; throughput is a LABEL (part of the key), NOT a measure;
    // reporteddate is present in the payload but MUST be excluded (not persisted).
    public const string PipelineFlowElem = """
    { "flowdate": "07/19/2026", "reporteddate": "2026-08-18 13:33", "region": "Southeast",
      "pipeline": "Southern Natural Gas", "throughput": "Scheduled", "flowtype": "Receipt", "volume": 1234.567 }
    """;

    public const string UsImportsExportsElem = """
    { "rundate": "2026-08-18", "flowdate": "2026-08-18", "volume": -1113.0385,
      "pointname": "Freeport modeled feedgas activity", "pipelinename": "Fossil Energy", "ledgerside": "Delivery",
      "state": "TX", "county": "Brazoria", "type": "LNG", "pointgroupname": "Freeport modeled feedgas activity",
      "districtname": "LNG - Texas" }
    """;

    // us_samplestorage_facility — FieldType from field_type; volume can be negative.
    public const string UsSampleStorageElem = """
    { "reportdate": "2026-08-18", "flowdate": "2026-08-18", "name": "Arcadia", "eiaregion": "South Central",
      "state": "LA", "field_type": "Salt Dome", "volume": -48.25 }
    """;

    public const string StateFlowsElem = """
    { "flowdate": "07/19/2026", "region": "Southeast", "fromstate": "Alabama", "tostate": "Florida",
      "flowtype": "Inflow", "volume": 2092.671 }
    """;

    // ---- Tier 1 discovery-fed lookups (archetype C) ----

    public const string CountyElem = """{ "id": 14117, "name": "Autauga" }""";

    // facility — carries a redundant facilitytypeid echo ("1", a string) that is DROPPED; PointTypeId is injected.
    public const string FacilityElem = """{ "id": 24854, "name": "PWR - Oregon Clean Energy", "facilitytypeid": "1" }""";

    public const string SubregionElem = """{ "id": 26252, "name": "Lower Midcon" }""";

    // ---- Tier 2 parametrized facts (archetypes D, E) ----

    // supplyDemand/region — body carries ONLY product + volume_mmcfd; RegionId (path) & Date (param) injected.
    public const string SdRegionElem = """{ "product": "Plains Production", "volume_mmcfd": 2947 }""";

    // volumeHistory/point — id IS the PointId; volume can be negative.
    public const string PointVolumeElem = """{ "id": 691, "volume": -8.699, "date": "2026-08-17" }""";
}
