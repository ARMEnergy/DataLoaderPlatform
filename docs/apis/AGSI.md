# AGSI (GIE Aggregated Gas Storage Inventory) loader — API field reference

European gas-storage transparency data from **Gas Infrastructure Europe (GIE)**:
a country/region → storage-operator entity map (endpoint 1) that drives per-country
daily storage-inventory queries (endpoint 2). This is the **mandatory full-field-set
gate**: every field of both endpoints is enumerated below with its exact source path,
recommended SQL Server type, nullability, and format. Nothing downstream
(model → sink → TVP → table → merge proc) may ship a partial column set.

- **DB name:** `AGSI` — **schema:** `arm` — audit hub: `arm.FileLog` (per the platform
  convention used by Platts/CWG).
- **Targets:** `arm.GasStorageEntity` (endpoint 1) and `arm.GasStorage` (endpoint 2).

## Verification provenance (READ THIS)

| Item | Status |
|------|--------|
| Endpoint 1 (`/api/about`) container shape + full entity/`data` field set | **Live-probed** 2026-08-17 via `GET https://agsi.gie.eu/api/about` (no key). |
| Endpoint 1 "every entity has a non-null `country`; multiple SSOs per country; all `data.code="EU"`" | **Live-confirmed** from the same probe. |
| Endpoint 2 (`/api`) response **shape** + field list | From the **task-supplied `country=de&date=2026-08-13` sample** + GIE's published API user manual (v006/v007) and third-party field references. |
| Endpoint 2 **units / status codes / `netWithdrawal` sign** | Cross-checked against GIE docs + the sample arithmetic (see §2). |
| Endpoint 2 **no-data behaviour, pagination, rate limits, `consumptionFull`/`coveredCapacity` exact meaning** | **NOT live-verifiable without a real `x-key`** — documented from published docs + sample, and flagged in "Open questions" below. |

> **A real `x-key` is required to fully close the endpoint-2 field set** — specifically
> the no-data response body (404 vs 200-with-empty-`data[]` vs a `status:"N"` row) and
> the exact definition/units of `consumptionFull` and `coveredCapacity`. Everything
> structural (field names, order, types, `updatedAt`/date formats, the persisted set) is
> pinned from the sample; the items above are marked ⚠ inline. Escalate to the user if a
> key can be obtained for a one-shot live confirmation.

## API

- **Base host:** `https://agsi.gie.eu`
- **Two endpoints, two auth regimes:**
  - **Endpoint 1 — `GET /api/about`** — **NO key.** Public. Returns the entity map.
  - **Endpoint 2 — `GET /api?country={code}&date={yyyy-MM-dd}`** — **requires an API key
    sent as the HTTP request header `x-key: <key>`** (it is a *header*, not a query-string
    param). The key is stored in config as `Loaders:AGSI:ApiKey`
    (env `DATALOADER_Loaders__AGSI__ApiKey`), defaulting to the `SEE_DB` sentinel per the
    platform secret convention. **Never hard-code the key or place it in this file;** refer
    to it only by its config variable name.
- **Responses:** JSON (`application/json`) for both endpoints.
- **HTTP semantics:** `200` = OK. `401/403` = missing/invalid `x-key` (endpoint 2).
  The **no-data-for-a-valid-country+date** case is ⚠ not live-verifiable without a key —
  see Open questions #1; the loader should tolerate **both** a 404 **and** a
  200-with-empty-`data[]` and treat either as "nothing to load for this (country, date)"
  → skip, not hard-fail (mirrors the CWG/StormVista 404-tolerant pattern).
- **History:** AGSI+ daily history runs back to ~2011 for many countries; availability
  varies per country and per SSO onboarding date. There is no single hard horizon.
- **Rate limits:** GIE does not publish a hard numeric limit in the docs; the platform
  (spring-2022 rewrite) is rate-limited and can throttle heavy callers. **Pace
  conservatively** (a low, StormVista-style requests-per-second cap) and honour any
  `429` with backoff. ⚠ exact limit not live-verifiable.

## Country/region codes (the `{code}` used by endpoint 2)

`{code}` is a two-letter code taken from endpoint 1's persisted `Code` column
(`data.country.code`, e.g. `at`, `de`, `fr`). Input is **case-insensitive** (the sample
requested `de` and got `"code":"DE"`). GIE also exposes region-aggregate codes such as
`eu` (Europe total) and `ne` (non-EU) — these correspond to endpoint 1's `ParentCode`
(`data.code`, e.g. `EU`). The work-unit set is therefore **the distinct `Code` values
from `arm.GasStorageEntity`** (optionally plus the `ParentCode` aggregate `eu`), each
crossed with the date window.

---

## 1. Endpoint 1 — `GET /api/about` → table `arm.GasStorageEntity`

- **Request:** `GET https://agsi.gie.eu/api/about` — no params, no key.
- **Represents:** the catalogue of storage-system operators (SSOs), grouped
  region → country, from which we derive the country→region map that seeds endpoint 2.
- **Cadence:** static-ish reference; refresh occasionally (overwrite in place).

### Exact container shape (live-probed 2026-08-17)

The root is an **object**, not a top-level array. It has a **single top-level key `SSO`**,
whose value is an **object keyed by region name**, whose value is an **object keyed by
country name**, whose value is an **array of entity objects**:

```
root (object)
└── "SSO" (object)
    └── "<Region Name>"  e.g. "Europe"      (object)
        └── "<Country Name>" e.g. "Austria" (array)
            └── [ {entity}, {entity}, … ]   one per storage operator
```

Trimmed real sample (Austria has **6** entities — GSA, OMV, RAG, SEFE, Uniper, …; one
shown):

```json
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
            { "eic": "25W-SPHAID-GAZ-M", "name": "UGS Haidach (GSA) …",
              "country": { "code": "AT", "name": "Austria" },
              "type": "DSR", "operational_start_date": "2011-01-01",
              "operational_end_date": "2022-10-07" }
          ],
          "data": { "type": "SSO", "country": { "code": "AT", "name": "Austria" },
                    "code": "EU", "name": "Europe" }
        }
      ]
    }
  }
}
```

The loader must **walk `SSO → region → country → array`** and read each entity's `data`
object. Do **not** rely on the region/country map keys themselves — the authoritative
values live inside `data` (the map keys are display labels).

### Full entity field set (every field, not just the kept ones)

| Entity-level field | Type | Kept? | Notes |
|--------------------|------|:-----:|-------|
| `image` | string (base64 PNG) | **drop** | operator logo; not data |
| `short_name` | string | **drop** | operator short name (e.g. `GSA`) |
| `name` | string | **drop** | **operator** legal name (e.g. `GSA LLC`) — distinct from `data.name` |
| `publication_link` | array of `{url,description}` | **drop** | links |
| `transparency_template` | array | **drop** | often empty |
| `operational_information` | array of `{url,description}` | **drop** | links |
| `available_capacities` | array | **drop** | often empty |
| `tariffs` | array | **drop** | often empty |
| `eic` | string | **drop** | operator EIC code |
| `facilities` | array of objects | **drop** | per-facility detail (own `eic`, `name`, `country`, `type`, `operational_start/end_date`) — not needed for the country map |
| `data` | object | **keep (4 fields)** | the country→region mapping — see below |

### `data` object — full field set

| `data` field | Type | Kept as | Notes |
|--------------|------|---------|-------|
| `data.type` | string | **drop** | entity type; **only `"SSO"` observed** across the whole response |
| `data.country.code` | string (2-letter) | **`Code`** | e.g. `"AT"` — the endpoint-2 query key |
| `data.country.name` | string | **`Name`** | e.g. `"Austria"` |
| `data.code` | string | **`ParentCode`** | region-group code; **only `"EU"` observed** |
| `data.name` | string | **`ParentName`** | region-group name; **only `"Europe"` observed** |

### Persisted columns → `arm.GasStorageEntity`

| Column | Source path | SQL type | Null? | Example |
|--------|-------------|----------|-------|---------|
| `Code` | `data.country.code` | `VARCHAR(4)` | No | `AT` |
| `Name` | `data.country.name` | `NVARCHAR(64)` | No | `Austria` |
| `ParentCode` | `data.code` | `VARCHAR(4)` | No | `EU` |
| `ParentName` | `data.name` | `NVARCHAR(64)` | No | `Europe` |

**Dropped** (with reason): `type` (constant `"SSO"`, no discriminating value);
`image`/`short_name`/`name`(operator)/`publication_link`/`transparency_template`/
`operational_information`/`available_capacities`/`tariffs`/`eic`/`facilities`
(operator- and facility-level metadata not needed — we persist only the country→region
map that seeds endpoint 2).

### Dedup-to-unique-country rule (build the MERGE around this — precise)

- **Multiple entities map to one country: YES.** Austria alone returns **6** SSO entities
  (GSA, OMV, RAG, SEFE, Uniper, …), **all** carrying the identical
  `data = { type:"SSO", country:{code:"AT",name:"Austria"}, code:"EU", name:"Europe" }`.
  Projected to the four persisted fields they **collapse to one tuple**
  `("AT","Austria","EU","Europe")`. The loader must **distinct/dedup** before the MERGE.
- **Null/missing `country`: NOT observed.** The live probe confirmed *every* entity has a
  non-null `data.country` (both `code` and `name` present). The loader should still
  defensively skip an entity whose `data.country.code` is null/empty rather than insert a
  blank key.
- **Is `data.country.code` unique on its own?** In the current data **yes** — because every
  entity shares `data.code = "EU"`, the pair `(country.code, data.code)` collapses to
  `country.code` alone. The recommended **unique key is `Code`** (= `data.country.code`),
  which is also mandatory because endpoint 2 is queried by exactly this single code.
- **Defensive alternative:** if GIE ever introduces a second region group (a `data.code`
  other than `"EU"`, e.g. a non-EU grouping) such that one country code could appear under
  two parents, the safe composite unique key becomes **`(Code, ParentCode)`**. This is not
  needed for the observed data; flag it for DATABASE_DEVELOPER as the fallback if a
  duplicate-`Code` MERGE collision ever surfaces.

**Recommended unique/natural key for `arm.GasStorageEntity`: `Code`.**

---

## 2. Endpoint 2 — `GET /api?country={code}&date={yyyy-MM-dd}` → table `arm.GasStorage`

- **Request pattern:** `GET https://agsi.gie.eu/api?country={{EntityCode}}&date={{Date}}`
  with header `x-key: <Loaders:AGSI:ApiKey>`.
  - `{{EntityCode}}` = a `Code` from endpoint 1 (e.g. `de`), case-insensitive.
  - `{{Date}}` = `yyyy-MM-dd` (e.g. `2026-08-13`). This is the **gas day requested**.
- **Example** (`country=de&date=2026-08-13`):

```json
{ "last_page":1, "total":1, "dataset":"", "gas_day":"2026-08-16",
  "data":[ { "name":"Germany", "code":"DE", "url":"DE", "updatedAt":"2026-08-17 08:00:55",
    "gasDayStart":"2026-08-13", "gasDayEnd":"2026-08-14", "gasInStorage":"121.1238",
    "consumption":"903.9000", "consumptionFull":"13.4", "injection":"543.71",
    "withdrawal":"6.5", "netWithdrawal":"-537.3", "workingGasVolume":"246.489",
    "injectionCapacity":"4292.58", "withdrawalCapacity":"7067.36",
    "contractedCapacity":"194.1057", "availableCapacity":"58.6043",
    "coveredCapacity":"100", "status":"C", "trend":"0.24", "full":"49.14", "info":[] } ] }
```

> **All numeric measures arrive as JSON *strings*** (e.g. `"121.1238"`, `"-537.3"`). The
> transformer must parse them (invariant culture, `.` decimal) to numeric before the sink.

### Top-level (envelope) fields

| Field | Type | SQL type (if persisted) | Notes |
|-------|------|-------------------------|-------|
| `last_page` | integer | *(not persisted)* | pagination — total number of pages. `1` for a single-date query |
| `total` | integer | *(not persisted)* | total number of `data[]` records across all pages; `1` for a single (country, date) |
| `dataset` | string | *(not persisted)* | dataset label; **empty string `""`** in the sample (default dataset) |
| `gas_day` | string `yyyy-MM-dd` | **`Gas_Day` `DATE`** | ⚠ **response-level "latest available gas day" marker — NOT the requested date.** In the sample the request was `date=2026-08-13` but `gas_day="2026-08-16"` (the newest gas day GIE holds, ~run-date−1). It is the **same value regardless of which historical `date` you query** → see the natural-key warning below |

### `data[]` element — every field

Persisted to `arm.GasStorage` unless marked **drop** (and see the note on rows 1–3 below —
`name`/`code`/`url` are **not** stored on the fact table). Recommended SQL types follow the
two measure families: **volume/rate/capacity → `DECIMAL(18,4)`** (very safe headroom; the
`eu` aggregate row carries the largest magnitudes — `DECIMAL(12,4)` is the practical
minimum), **percent/ratio → `DECIMAL(9,4)`**.

| # | Source field | SQL type | Null? | Sign | Unit | Meaning / notes | Example |
|---|-------------|----------|-------|------|------|-----------------|---------|
| 1 | `name` | `NVARCHAR(64)` | No | — | — | country/region display name. **Not on the fact table** — normalized into `arm.GasStorageEntity` (`Name`), referenced via `EntityId` | `Germany` |
| 2 | `code` | `VARCHAR(4)` | No | — | — | **echoes the requested country code, UPPERCASED** (request `de` → `"DE"`). **Not on the fact table** — matched to `arm.GasStorageEntity` (`Code`) to resolve `EntityId` (the fact key) | `DE` |
| 3 | `url` | `VARCHAR(16)` | No | — | — | URL slug for the entity (equals `code` for a country; can differ for sub-entities). **Dropped entirely** — redundant with `Code`; not stored anywhere | `DE` |
| 4 | `updatedAt` | `DATETIME2(0)` | Yes* | — | — | last-update timestamp, format **`yyyy-MM-dd HH:mm:ss`** (GIE server time, CET/CEST; ⚠ tz not stated in docs) | `2026-08-17 08:00:55` |
| 5 | `gasDayStart` | `DATE` | No | — | — | **start of the gas day this row describes** — `yyyy-MM-dd`. For a single-date query **equals the request `date`** (`2026-08-13`) | `2026-08-13` |
| 6 | `gasDayEnd` | `DATE` | No | — | — | end of the gas day = `gasDayStart + 1 day` | `2026-08-14` |
| 7 | `gasInStorage` | `DECIMAL(18,4)` | Yes* | ≥0 | **TWh** | gas in storage at end of gas day (4-dp accuracy) | `121.1238` |
| 8 | `consumption` | `DECIMAL(18,4)` | Yes* | ≥0 | **GWh/d** | national gas consumption during the gas day | `903.9000` |
| 9 | `consumptionFull` | `DECIMAL(9,4)` | Yes* | ≥0 | **% ⚠** | consumption-coverage metric; exact definition/units **not confirmed in published docs** — treat as percentage/ratio | `13.4` |
| 10 | `injection` | `DECIMAL(18,4)` | Yes* | ≥0 | **GWh/d** | injection into storage during the gas day (2-dp accuracy) | `543.71` |
| 11 | `withdrawal` | `DECIMAL(18,4)` | Yes* | ≥0 | **GWh/d** | withdrawal from storage during the gas day | `6.5` |
| 12 | `netWithdrawal` | `DECIMAL(18,4)` | Yes* | **± signed** | **GWh/d** | `withdrawal − injection`; **negative = net injection** (sample: `6.5 − 543.71 = −537.21 ≈ −537.3`) | `-537.3` |
| 13 | `workingGasVolume` | `DECIMAL(18,4)` | Yes* | ≥0 | **TWh** | technical (maximum) working gas volume / capacity (4-dp) | `246.489` |
| 14 | `injectionCapacity` | `DECIMAL(18,4)` | Yes* | ≥0 | **GWh/d** | technical injection capacity | `4292.58` |
| 15 | `withdrawalCapacity` | `DECIMAL(18,4)` | Yes* | ≥0 | **GWh/d** | technical withdrawal capacity | `7067.36` |
| 16 | `contractedCapacity` | `DECIMAL(18,4)` | Yes* | ≥0 | **TWh** | contracted storage capacity | `194.1057` |
| 17 | `availableCapacity` | `DECIMAL(18,4)` | Yes* | ≥0 | **TWh** | available (uncontracted) storage capacity | `58.6043` |
| 18 | `coveredCapacity` | `DECIMAL(9,4)` | Yes* | ≥0 | **% ⚠** | share of capacity for which data is reported/covered; exact definition **not confirmed in docs** | `100` |
| 19 | `status` | `VARCHAR(1)` | No | — | — | data-quality code: **`C` = Confirmed, `E` = Estimated, `N` = No data** | `C` |
| 20 | `trend` | `DECIMAL(9,4)` | Yes* | **± signed** | ratio/% | daily fill trend ≈ `(injection − withdrawal) / workingGasVolume`, signed | `0.24` |
| 21 | `full` | `DECIMAL(9,4)` | Yes* | ≥0 | **%** | fill level = `gasInStorage / workingGasVolume × 100` (sample `121.1238/246.489 ≈ 49.14`); **can slightly exceed 100** | `49.14` |
| — | `info` | array | — | — | — | **drop** — array of service-announcement/flag objects; empty `[]` in sample; not persisted |

> **\* Nullability of the measures (7–18, 20–21):** in the sample all are populated
> (`status:"C"`). ⚠ It is **not live-verifiable without a key** what an `E` (estimated) or
> especially an `N` (no-data) row looks like — GIE typically emits blank/null measures for
> `status:"N"`. **DATABASE_DEVELOPER should make every measure column NULLable**
> defensively; on `arm.GasStorage` only `EntityId`, `gasDayStart`, `gasDayEnd`, `status`,
> `Date`, and `Gas_Day` are safely NOT NULL (`code`/`name` are now NOT NULL on
> `arm.GasStorageEntity`, not the fact table; `url` is dropped).

### Persisted column set for `arm.GasStorage` (normalized)

**21 business columns** = the request **`Date`** param + the top-level **`Gas_Day`** +
the FK **`EntityId`** + **18 of the 21 `data[]` fields** (all EXCEPT `info` and the three
now handled by the entity dimension — `name`, `code`, `url`):

```
Date, Gas_Day, EntityId,
updatedAt, gasDayStart, gasDayEnd,
gasInStorage, consumption, consumptionFull, injection, withdrawal, netWithdrawal,
workingGasVolume, injectionCapacity, withdrawalCapacity, contractedCapacity,
availableCapacity, coveredCapacity, status, trend, full
```

**API-returned but NOT persisted in `arm.GasStorage`:**
- `name`, `code` — normalized into **`arm.GasStorageEntity`** (`Name`/`Code`) and
  referenced from the fact table via the new **`EntityId`** FK; not duplicated here.
- `url` — **dropped entirely** (redundant with `Code`; not added anywhere).
- `info` — dropped (empty service-announcement array).

The corresponding TVP carries **22** columns — these 21 business columns plus `FileLogId`.

### Behaviours

1. **No data for a valid country+date** — ⚠ **NOT live-verifiable without a key.** GIE's
   pagination-era API generally returns **`200` with `total:0` and an empty `data[]`**, or a
   single row with `status:"N"` and blank measures; a `404` is also possible for an unknown
   country. The loader must tolerate all three: empty `data[]` / `status:"N"` / `404` → skip
   this (country, date), not fail. (Open question #1.)
2. **Pagination / multiple `data[]` elements** — for a **single `(country, date)`** the
   envelope is `last_page:1, total:1` and `data[]` has **exactly one element** (confirmed by
   the sample). More than one element / `last_page`>1 only arises with **date-range** queries
   (`from`/`to` + `page`/`size`), which this loader does **not** use (it iterates one date at
   a time). If a range mode is ever added, follow `last_page` and pass `page=`. (Open
   question #2 — cannot fully confirm the multi-page envelope without a key.)
3. **Does `data[].code` echo the requested code?** **Yes, UPPERCASED.** Request `country=de`
   → response `"code":"DE"`, `"url":"DE"`. Input is case-insensitive; output is uppercase.
   The loader should compare case-insensitively when matching back to `arm.GasStorageEntity`.
4. **Rate limits** — no published hard number; pace conservatively and back off on `429`
   (Open question #4).
5. **`Date` vs `gas_day` vs `gasDayStart`/`gasDayEnd` — do NOT conflate:**
   - **`Date`** = the request parameter (the gas day you asked for). For single-date queries
     it **equals `gasDayStart`**.
   - **`gasDayStart` / `gasDayEnd`** = the gas day the returned row actually describes
     (`gasDayStart` … `gasDayStart+1`). Equals `Date` in single-date mode.
   - **`gas_day`** = a **response-level "latest available gas day" marker**, the **same for
     every query regardless of the `date` asked** (sample: asked `2026-08-13`, got
     `gas_day:"2026-08-16"`). **It must NOT be part of the natural key** — using it would
     collapse all historical rows for a country onto one key and destroy history.

### Recommended natural key for `arm.GasStorage`

**`(EntityId, GasDayStart)`** — where `EntityId` is the FK into `arm.GasStorageEntity`
(replacing the former per-country `code`) and `GasDayStart` is the gas day the row
describes. `Date` (the request parameter) **equals `gasDayStart` in single-date mode**.
Rationale:

- `GasDayStart` uniquely identifies the gas day the row describes, per entity.
- **Do NOT use `(EntityId, Gas_Day)`** — `Gas_Day` is the global latest-gas-day marker and
  is identical across all historical queries, so it would collide on every backfilled date.
  `Gas_Day` is persisted as a non-key attribute only.
- DATABASE_DEVELOPER should confirm the invariant `Date == gasDayStart` holds in practice
  (it does in the sample); `Date` is retained as the audit-of-request column.

---

## Coverage checklist

| Endpoint | Target | Fields in source | Fields persisted | Dropped | Key |
|----------|--------|:----------------:|:----------------:|---------|-----|
| 1 `GET /api/about` | `arm.GasStorageEntity` | 11 entity-level (+4 in `data`) | **4** (`Code`, `Name`, `ParentCode`, `ParentName`) | `type`, `image`, `short_name`, operator `name`, `publication_link`, `transparency_template`, `operational_information`, `available_capacities`, `tariffs`, `eic`, `facilities` | **`Code`** (dedup SSOs→country; `(Code,ParentCode)` fallback) |
| 2 `GET /api?country=&date=` | `arm.GasStorage` | 4 envelope + 22 `data[]` | **21** (`Date` + `Gas_Day` + `EntityId` + 18 `data[]` fields) | envelope `last_page`/`total`/`dataset`; `data[].info`; `data[].name`/`code` → `arm.GasStorageEntity` via `EntityId`; `data[].url` dropped | **`(EntityId, GasDayStart)`** (Date≡gasDayStart; **never** `Gas_Day`) |

**Gate status: PASS (with 4 flagged ⚠ items requiring a live `x-key` to close).** Both
endpoints have a complete field list, per-field SQL type, nullability/sign/units, and a
precise unique/natural key. The endpoint-1 container walk (`SSO→region→country→[]`) and
dedup rule, and the endpoint-2 `Date`/`gas_day`/`gasDayStart` distinction, are called out
explicitly for DATABASE_DEVELOPER. Items that a real key would confirm are listed under
"Open questions".

## Open questions (for DATABASE_DEVELOPER / user)

1. **No-data response body (needs a key):** confirm whether a valid country with no data
   for a date returns `404`, `200`+empty `data[]`, or a `status:"N"` row with blank
   measures — this decides whether the sink writes an `N` row or skips. Loader is built to
   tolerate all three.
2. **Multi-page envelope (needs a key):** confirm `last_page`/`total` behaviour if a
   date-range mode is ever enabled (single-date is confirmed 1 row / 1 page).
3. **`consumptionFull` and `coveredCapacity` exact definition/units (needs docs or a key):**
   modeled as `DECIMAL(9,4)` percentages pending confirmation; adjust precision if they turn
   out to be counts/days.
4. **Rate limit / throttle numbers (needs a key or GIE confirmation):** set a conservative
   `RequestsPerSecond` in `Loaders:AGSI` and back off on `429`.
5. **`updatedAt` timezone:** documented as GIE server time (CET/CEST); confirm whether to
   store as-is (`DATETIME2`) or normalize to UTC.
