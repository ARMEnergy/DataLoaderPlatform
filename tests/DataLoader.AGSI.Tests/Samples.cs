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
}
