namespace DataLoader.AGSI.Tests;

/// <summary>
/// The real AGSI JSON response samples from <c>docs/apis/AGSI.md</c> — the ground
/// truth for the parse/mapping tests. Held as string constants (both endpoints
/// return small JSON) so the tests stay fully offline: no network, no fixture I/O.
///
/// <para><see cref="AboutNested"/> reproduces the documented
/// <c>SSO → region → country → [entities]</c> object tree, faithfully carrying the
/// full entity field set (image/short_name/operator name/publication_link/eic/
/// facilities/…) so the reader is proven to ignore everything but <c>data</c>. It
/// exercises the three documented cases in one body: Austria with TWO SSO entities
/// that must collapse to one <c>AT</c> row (Austria alone returns 6 SSOs live),
/// Germany with one <c>DE</c> entity, and two entities that MUST be skipped (one
/// with a missing <c>country</c>, one with a blank <c>country.code</c>).</para>
///
/// <para><see cref="StorageDe"/> is the verbatim <c>country=de&amp;date=2026-08-13</c>
/// sample from the API doc (§2), all measures string-encoded.</para>
/// </summary>
internal static class Samples
{
    // GET /api/about — documented nested-object shape. The map keys ("Europe",
    // "Austria", …) are display labels; the authoritative values live in each
    // entity's `data`. Two Austria SSOs share the identical data → collapse to one
    // AT row; the "Nowhere" (missing country) and "Blank" (empty code) entities are
    // skipped defensively.
    public const string AboutNested = """
    {
      "SSO": {
        "Europe": {
          "Austria": [
            {
              "image": "<base64 PNG>",
              "short_name": "GSA",
              "name": "GSA LLC",
              "publication_link": [{ "url": "http://www.gsa-services.ru/", "description": "" }],
              "transparency_template": [],
              "operational_information": [{ "url": "http://www.gsa-services.ru", "description": "" }],
              "available_capacities": [],
              "tariffs": [],
              "eic": "25X-GSALLC-----E",
              "facilities": [
                { "eic": "25W-SPHAID-GAZ-M", "name": "UGS Haidach (GSA)",
                  "country": { "code": "AT", "name": "Austria" },
                  "type": "DSR", "operational_start_date": "2011-01-01",
                  "operational_end_date": "2022-10-07" }
              ],
              "data": { "type": "SSO", "country": { "code": "AT", "name": "Austria" },
                        "code": "EU", "name": "Europe" }
            },
            {
              "short_name": "OMV",
              "name": "OMV Gas Storage GmbH",
              "eic": "25X-OMVGSG------G",
              "data": { "type": "SSO", "country": { "code": "AT", "name": "Austria" },
                        "code": "EU", "name": "Europe" }
            }
          ],
          "Germany": [
            {
              "short_name": "SEFE",
              "name": "SEFE Storage GmbH",
              "eic": "21X0000000013287",
              "data": { "type": "SSO", "country": { "code": "DE", "name": "Germany" },
                        "code": "EU", "name": "Europe" }
            }
          ],
          "Nowhere": [
            {
              "short_name": "NOCTRY",
              "name": "Missing-country operator",
              "data": { "type": "SSO", "code": "EU", "name": "Europe" }
            }
          ],
          "Blankland": [
            {
              "short_name": "BLANK",
              "name": "Blank-code operator",
              "data": { "type": "SSO", "country": { "code": "", "name": "" },
                        "code": "EU", "name": "Europe" }
            }
          ]
        }
      }
    }
    """;

    // A 2xx body with a well-formed root but ZERO parseable country entities
    // (SSO present, but the only entity has no country). ParseEntities → 0 rows.
    public const string AboutZeroEntities = """
    {
      "SSO": {
        "Europe": {
          "Nowhere": [
            { "short_name": "X", "name": "no country", "data": { "type": "SSO", "code": "EU", "name": "Europe" } }
          ]
        }
      }
    }
    """;

    // A 2xx body that is valid JSON but has NO "SSO" key at all → 0 rows.
    public const string AboutNoSso = """{ "unexpected": true }""";

    // GET /api?country=de&date=2026-08-13 — verbatim from docs/apis/AGSI.md §2.
    public const string StorageDe = """
    { "last_page":1, "total":1, "dataset":"", "gas_day":"2026-08-16",
      "data":[ { "name":"Germany", "code":"DE", "url":"DE", "updatedAt":"2026-08-17 08:00:55",
        "gasDayStart":"2026-08-13", "gasDayEnd":"2026-08-14", "gasInStorage":"121.1238",
        "consumption":"903.9000", "consumptionFull":"13.4", "injection":"543.71",
        "withdrawal":"6.5", "netWithdrawal":"-537.3", "workingGasVolume":"246.489",
        "injectionCapacity":"4292.58", "withdrawalCapacity":"7067.36",
        "contractedCapacity":"194.1057", "availableCapacity":"58.6043",
        "coveredCapacity":"100", "status":"C", "trend":"0.24", "full":"49.14", "info":[] } ] }
    """;

    // No-data shape A: 200 with total:0 and an empty data[].
    public const string StorageEmptyData = """
    { "last_page":1, "total":0, "dataset":"", "gas_day":"2026-08-16", "data":[] }
    """;

    // No-data shape B: 200 with a lone status:"N" record and all measures blank.
    public const string StorageStatusN = """
    { "last_page":1, "total":1, "dataset":"", "gas_day":"2026-08-16",
      "data":[ { "name":"Germany", "code":"DE", "url":"DE", "updatedAt":"",
        "gasDayStart":"2026-08-13", "gasDayEnd":"2026-08-14", "gasInStorage":"",
        "consumption":"", "consumptionFull":"", "injection":"", "withdrawal":"",
        "netWithdrawal":"", "workingGasVolume":"", "injectionCapacity":"",
        "withdrawalCapacity":"", "contractedCapacity":"", "availableCapacity":"",
        "coveredCapacity":"", "status":"N", "trend":"", "full":"", "info":[] } ] }
    """;

    // -------------------------------------------------------------------------
    // The aggregate responses (verified live 2026-09-28 with the real x-key).
    // BOTH `?country=eu` and `?country=ne` return this IDENTICAL two-element body:
    // AGSI ignores WHICH aggregate was asked for and always answers with both.
    // Single countries always answer total:1. Captured verbatim for
    // country=eu&date=2026-09-01 (`children` elided — the reader never reads it).
    //
    // This is the body that produced the 2026-09-28 defect: the reader stamped
    // unit.EntityId onto BOTH elements, they collided on (EntityId, GasDayStart),
    // and the dedupe kept the LAST one — so the `eu` entity stored the Non-EU
    // aggregate and the EU aggregate was never persisted at all.
    // -------------------------------------------------------------------------
    public const string StorageAggregateEuAndNe = """
    { "last_page":1, "total":2, "dataset":"", "gas_day":"2026-09-27",
      "data":[
        { "name":"EU", "code":"eu", "url":"eu", "updatedAt":"2026-09-28 12:36:31",
          "gasDayStart":"2026-09-01", "gasDayEnd":"2026-09-02", "gasInStorage":"741.9174",
          "consumption":"3519", "consumptionFull":"21.08", "injection":"2702.34",
          "withdrawal":"143.3", "netWithdrawal":"-2559.1", "workingGasVolume":"1130.6108",
          "injectionCapacity":"12218.54", "withdrawalCapacity":"20027.89",
          "contractedCapacity":"1038.1606", "availableCapacity":"98.0543",
          "coveredCapacity":"100", "status":"C", "trend":"0.24", "full":"65.62", "info":[] },
        { "name":"Non-EU", "code":"ne", "url":"ne", "updatedAt":"2026-09-02 06:58:51",
          "gasDayStart":"2026-09-01", "gasDayEnd":"2026-09-02", "gasInStorage":"110.6828",
          "consumption":"228.85", "consumptionFull":"0", "injection":"452.3",
          "withdrawal":"1.4", "netWithdrawal":"-450.9", "workingGasVolume":"331.0191",
          "injectionCapacity":"2889.49", "withdrawalCapacity":"2325.88",
          "contractedCapacity":"-", "availableCapacity":"-",
          "coveredCapacity":"93.09", "status":"E", "trend":"0.14", "full":"33.44", "info":[] }
      ] }
    """;

    // An unpublished / future `date` is silently CLAMPED by AGSI to the latest
    // available gas day — it never 404s and never returns an empty data[].
    // Verified live: `?country=at&date=2026-10-15` answers gasDayStart 2026-09-27.
    // Captured here for a request of date=2026-09-27 answered with gas day 2026-09-26.
    public const string StorageClampedToEarlierGasDay = """
    { "last_page":1, "total":1, "dataset":"", "gas_day":"2026-09-26",
      "data":[ { "name":"Austria", "code":"AT", "url":"AT", "updatedAt":"2026-09-27 16:10:02",
        "gasDayStart":"2026-09-26", "gasDayEnd":"2026-09-27", "gasInStorage":"68.1234",
        "consumption":"200.1", "consumptionFull":"12.0", "injection":"150.0",
        "withdrawal":"1.0", "netWithdrawal":"-149.0", "workingGasVolume":"100.2789",
        "injectionCapacity":"1000.0", "withdrawalCapacity":"2000.0",
        "contractedCapacity":"-", "availableCapacity":"-",
        "coveredCapacity":"100", "status":"E", "trend":"0.15", "full":"67.93", "info":[] } ] }
    """;

    // A missing / invalid / revoked x-key answers HTTP **200**, not 401 — the failure
    // is only in the body. Captured verbatim 2026-09-28. Modelled as total:0 + an
    // empty data[], so without the `error` probe it is indistinguishable from a
    // legitimate no-data day and would be recorded as NotAvailable + unit success.
    public const string StorageAccessDenied = """
    { "last_page":0, "total":0, "dataset":"storage ERROR", "error":"access denied",
      "message":"Invalid or missing API key", "data":[] }
    """;

    // ⚠ AGSI ECHOES THE SECRET x-key BACK IN ITS ERROR BODY. Observed live 2026-09-28 on
    // 11 of 1551 backfill requests: a transient vendor fault answers HTTP 200 with
    // `"error":"Try/Catch error"` and `"message":"API key: <the key verbatim>"`. Anything
    // quoted out of an error body must therefore be redacted before it reaches a log, an
    // exception message or core.LoadLog. The literal below is the tests' own fake key
    // (StorageSourceReaderTests.ApiKey) so the redaction path is genuinely exercised.
    public const string StorageVendorFaultEchoingKey = """
    { "last_page":0, "total":0, "dataset":"storage ERROR", "error":"Try/Catch error",
      "message":"API key: SECRET-XKEY-123", "data":[] }
    """;

    // The same hazard, but with the key echoed inside `error` itself rather than `message`
    // — proves the redaction is applied to every quoted field, not just the suppressed one.
    public const string StorageVendorFaultKeyInErrorField = """
    { "last_page":0, "total":0, "dataset":"storage ERROR",
      "error":"bad request for key SECRET-XKEY-123", "message":"nope", "data":[] }
    """;

    // A single element carrying NO `code` at all. Unambiguous (nothing else to
    // confuse it with), so the reader accepts it and stamps the work unit's EntityId.
    public const string StorageSingleRecordNoCode = """
    { "last_page":1, "total":1, "dataset":"", "gas_day":"2026-08-16",
      "data":[ { "name":"Germany", "updatedAt":"2026-08-17 08:00:55",
        "gasDayStart":"2026-08-13", "gasDayEnd":"2026-08-14", "gasInStorage":"121.1238",
        "workingGasVolume":"246.489", "status":"C", "full":"49.14", "info":[] } ] }
    """;

    // A single element whose `code` is some OTHER entity than the one requested —
    // the shape that must never be attributed to the requesting entity.
    public const string StorageSingleRecordForeignCode = """
    { "last_page":1, "total":1, "dataset":"", "gas_day":"2026-08-16",
      "data":[ { "name":"Ukraine", "code":"UA", "url":"UA", "updatedAt":"2026-08-17 08:00:55",
        "gasDayStart":"2026-08-13", "gasDayEnd":"2026-08-14", "gasInStorage":"107.5948",
        "workingGasVolume":"321.1555", "status":"C", "full":"33.5", "info":[] } ] }
    """;
}
