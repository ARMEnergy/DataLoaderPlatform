# IIR (Industrial Info Resources) IDB API v2.7 loader — API field reference

Industrial asset, unit, and offline/outage-event intelligence from **Industrial Info
Resources (IIR)** — the **IDB (Industrial Database) API**, a REST/JSON gateway over IIR's
GMI (Global Market Intelligence) database (the same data behind PECWeb). This file is the
**full-field-set gate** for the loader: three data pulls — **Plant**, **Unit**, and
**OfflineEvent** — each mapped end-to-end to its target table. Nothing downstream
(model → sink → TVP → table → merge proc) may ship a partial column set.

- **Recommended DB name / schema:** `arm` schema (per the platform convention used by
  Platts/CWG/AGSI/IHSPointLogic). Audit hub `arm.FileLog`. Final names are
  DATABASE_DEVELOPER's call.
- **Loader id:** `IIR`.
- **Targets:** `arm.Plant` (`/plants`), `arm.Unit` (`/units`), `arm.OfflineEvent` (`/offlineevents`).

---

## Gate status: **PARTIALLY VERIFIED — spec-derived, three field schemas RECONSTRUCTED** ⚠

Read this before trusting the field tables. The provenance splits cleanly in two:

| Item | Status |
|------|--------|
| Base URL / versions / endpoint-path structure | **From spec + official IIR docs** (idb.json `servers`, `/idb/index.html`, "Tips for IDB API Clients" PDF). |
| **Auth flow** (`POST /token`, JWT Bearer, 1-day/30-day lifetime, no refresh) | **From spec + the client-tips & Power BI PDFs.** Token *delivery channel* (response header vs body) has one ⚠ (see §2). |
| **Request transport** (POST + query-string params + empty body), **multi-value params** (repeated keys) | **From spec + client-tips curl + Power Query M sample.** |
| **Pagination** (`limit`/`offset`; Summary 100/1000, Detail 5/50, References 500/1000; envelope `{limit,offset,resultCount,totalCount,<array>}`) | **From the client-tips PDF §4 + the visible `companies`/`projects` schemas in idb.json.** |
| **Mandatory two-step pull** (`summary` id-discovery → `detail` enrichment; DDL from detail) | **Corrected by the user/coordinator** (2026-08-20); consistent with the documented summary/detail split. |
| **OfflineEvent query scope** (default **country-filter-only** via `physicalAddressCountryName`; status/kind optional) | **Corrected by the user** (2026-08-20); the `eventKind`/`eventStatusDesc` filters from the client-tips Power Query sample are now optional, not default (see §7). |
| **Field naming conventions** (camelCase, nested `mailingAddress`/`physicalAddress`/`phone`/owner objects, `…Z[UTC]` date suffix) | **From the visible `companies/detail` + `projects` schemas/examples in idb.json.** |
| **Per-field response schemas for `/plants`, `/units`, `/offlineevents`** | ⚠ **RECONSTRUCTED — NOT read from the v2.7 spec.** See the box below. |

> ### ⚠ Why the three field tables are reconstructed, not spec-read
> `idb.json` (the RapiDoc `Download Spec` file, `https://api.industrialinfo.com/RapiDoc/idb/v2.7/idb.json`)
> is a single large OpenAPI 3.0 document whose **paths are ordered
> `token → equipmentTypes → hsProducts → pecZones → sicCodes → sicProducts → unitTypes →
> projectdurations → controlAreas → pipeline* → companies/summary → companies/detail → (plants,
> units, offlineevents, boilers, …)`**. Every fetch path available in this environment
> (direct fetch, reader proxy, Swagger validator, v2.5/v2.6 specs) **truncates on an oversized
> inline example inside `/companies/detail`**, i.e. *before* the plant/unit/offlineevent path
> objects. The endpoints are **confirmed to exist** (RapiDoc sidebar tags "Plant Search",
> "Unit Search", "Offline Event Search"; `/idb/index.html` changelog names `/plants/summary`,
> `/plants/detail`, `/units/summary`, `/units/detail`, `/offlineevents/summary`,
> `/offlineevents/detail`) and several of their fields are changelog-confirmed
> (`plantLatitude`, `plantlongitude`, `existingSqMeters`, `sectors.plantSectorDesc`,
> `unitCapacity.capacity`, `heatRate`, `derivedEventStatusDesc`), but the **complete per-field
> response schema for these three endpoints was not machine-readable here.**
>
> The §5/§6/§7 tables are therefore **anchored on the authoritative target DDL** (supplied by
> the user) and cross-walked to **inferred JSON field names** using the confirmed IIR naming
> conventions. Each inferred field is marked. **Every one requires a one-shot live
> confirmation in the user's environment** — the fastest route is RapiDoc → the target
> endpoint → *Try it out* → *Execute* with the real PAT, then read the returned JSON keys
> (or run `API_DOCUMENTATION_EXPERT` again with network access to the authenticated API).

> **Secret handling:** credentials (username `SEE_DB`, password `SEE_DB`) are **never written
> here**. They are referred to only by config-variable name and default to the `SEE_DB`
> sentinel (real values live in `core.Param` / env vars). They are never logged.

---

## 1. Base URL(s), versions, environments

- **Production host:** `https://api.industrialinfo.com` (server `iirapi01live`).
- **Test host:** `https://apitest.industrialinfo.com` (server `iirapi01test`).
- **Base path (OpenAPI `servers`):** `/idb/{major.minor}` — current documented version **`v2.7`**
  (released 28-Jul-2026; prior `v2.6` 17-Mar-2026, `v2.5` 28-Oct-2025).
- **Endpoint-path structure** (from the client-tips PDF §3.1):

  ```
  https://api.industrialinfo.com / idb / v2.7 / plants / summary
  └── host                          └IDB  └ver  └product └ summary|detail
  ```

- **Version pinning:** IIR supports specifying **major-only** (`/idb/v2`) to auto-upgrade to
  the latest **minor** version, or **major.minor** (`/idb/v2.7`) to pin. **Recommendation:**
  pin `v2.7` in config (`Loaders:IIR:BaseUrl` or a `Version` setting) for reproducibility; the
  DDL was authored against this version. Revisit on IIR's version-support notices.
- **Payload:** all responses `application/json`. Send `Accept: application/json`.

---

## 2. Authentication — JWT Bearer via `POST /token`

IIR uses **JSON Web Tokens (RFC 7519) as OAuth2 Bearer tokens**. There is **no OAuth2
client-credentials grant, no `grant_type`, no scope, no refresh token** — it is a simple
**login endpoint that mints a JWT**, then a static `Authorization: Bearer <jwt>` header on
every data call.

### 2.1 Token request

```
POST https://api.industrialinfo.com/idb/v2.7/token?username=<USERNAME>&password=<PASSWORD>[&tokenLifeTime=<days>]
Accept: application/json
(empty body)
```

| Query param | Type | Required | Meaning |
|-------------|------|:--------:|---------|
| `username` | string | **Yes** | Login username (config `Loaders:IIR:Username`, default `SEE_DB`). |
| `password` | string (password) | **Yes** | Login password (config `Loaders:IIR:Password`, default `SEE_DB`). |
| `tokenLifeTime` | number (days) | No | Token validity in **days**. **Default 1, maximum 30.** |

> Parameters are **query-string** (the spec models them `in: query`), consistent with how IIR
> models all endpoints (POST + query string + empty body — see §3). ⚠ Whether the credentials
> may also be sent as an `x-www-form-urlencoded` body is not confirmed; the query-string form
> is what the docs show.

### 2.2 Token response — ⚠ delivery channel to confirm live

- **Response body (observed in the visible spec):** a minimal JSON object
  `{ "message": "Token Created successfully." }`.
- **The JWT itself:** the spec's `/token` description states *"Your token will be supplied via
  the Authorization header using the Bearer Token schema."* Read together with the
  message-only body, this means the **JWT is returned in a response header** (the `/token`
  response's `Authorization` header, value `Bearer eyJ…`), **not** in the JSON body. The Power
  BI guide corroborates: on a 200 you *"copy the access token… will often begin with the
  letters 'ey'"* (a JWT).
- ⚠ **Live-verify:** confirm the exact **response header name** carrying the token (expected
  `Authorization`, value `Bearer <jwt>`; possibly a custom header). A delegating handler must
  read the token from that header. If, instead, a future minor returns it in the body, adjust
  the field name accordingly. This is the single most important item for the auth handler.

### 2.3 Presenting the token on data calls

```
POST https://api.industrialinfo.com/idb/v2.7/plants/summary?<filters>
Authorization: Bearer <jwt>
Accept: application/json
(empty body)
```

- **All data endpoints require a valid token** (401 otherwise).
- **Expiry:** the JWT `exp` claim = mint time + `tokenLifeTime` (default 1 day). The token is
  self-contained (claims include `sub`, `aud="IIR_API"`, `iss`, `iat`, `exp`).
- **No refresh token.** On expiry (or a `401`), the loader **re-requests `POST /token`** with
  the stored credentials and swaps in the new JWT. Recommended handler behaviour: mint once at
  run start, cache for the process, and re-mint on any `401`.
- **`403`** would indicate a subscription/entitlement gap (data outside the account's coverage),
  not an auth failure — surface distinctly.

---

## 3. Shared request / pagination / envelope model

All three target endpoints share one shape (verified on the visible `companies`/`projects`
endpoints and documented in the client-tips PDF §4).

### 3.1 Transport

- **Method: `POST`** for every endpoint, but **all parameters go in the query string** and the
  **request body is empty** (the client-tips curl and the Power Query `Text.ToBinary("")`
  sample both send POST + query string + empty body).
- **Multi-value filters are repeated query keys**, e.g.
  `…?eventStatusDesc=Ongoing&eventStatusDesc=Future&worldRegionId=1&worldRegionId=4`.

### 3.2 MANDATORY two-step pull: summary (id-discovery) → detail (enrichment)

**All three targets use a two-step pull. This is REQUIRED, not optional.** `summary` is used
**only to discover entity ids**; the **`detail` response is what populates**
`arm.Plant` / `arm.Unit` / `arm.OfflineEvent` (the supplied DDL is a detail-level record).
Do **not** try to satisfy the DDL from `summary` alone.

| | **`/…/summary`** (STEP 1 — id discovery) | **`/…/detail`** (STEP 2 — the persisted record) |
|---|---|---|
| Purpose | search by filter → list of ids | fetch the **full record(s)** by id |
| Params | filter params (here: `physicalAddressCountryName`) + `limit`/`offset` | **only** `<entity>Id` (repeated), `fields`, `limit`, `offset` |
| Field set | a **lean predefined subset** (enough for id + a few stable attrs) | **full field set** → maps to the DDL |
| `limit` default / max | **100 / 1000** | **5 / 50** |
| Persisted to | (optional) an **id-catalog** table (id + `RunDate`) | **`arm.Plant` / `arm.Unit` / `arm.OfflineEvent`** |

**Step 1 — discover ids** (paged, one work unit per country value or all in one repeated param):

```
POST /idb/v2.7/{plants|units|offlineevents}/summary
     ?physicalAddressCountryName=U.S.A.&physicalAddressCountryName=Canada
Authorization: Bearer <jwt>          (empty body)
```

- **`physicalAddressCountryName`** is a **repeatable** filter (country **name** strings, e.g.
  `U.S.A.`, `Canada`; ⚠ exact spelling of the U.S. value — `U.S.A.` — matches the value seen in
  the `companies` sample). Page via `limit`/`offset` until `offset ≥ totalCount`. Collect the
  **id field** from each summary row (see §3.2a).

**Step 2 — batch-enrich by id** (the records that populate the table):

```
POST /idb/v2.7/{plants|units|offlineevents}/detail
     ?plantId=1018109&plantId=1018110&plantId=1018111&…
Authorization: Bearer <jwt>          (empty body)
```

- **Ids are repeated query params** (`plantId=…&plantId=…`, `unitId=…`, `eventId=…`), empty body.
- **`fields` (detail):** a repeated key that selects specific fields; **the loader should NOT
  pass `fields`** so detail returns its **full default set**.
- **Reference endpoints** (not used by these three pulls) default 500 / max 1000 with `&listAll=1`.

#### Detail batch size (max ids per call) — validate the current `50`

- The documented **detail `limit` max is `50`** (client-tips PDF §4). ⚠ It is **not explicitly
  documented** whether that `limit` also caps the number of **ids** accepted per `detail` call,
  but the safe reading is **≤ 50 ids per call** (one returned record per id, and `limit` caps
  returned records). **Batch ≤ 50 ids/request** and page the detail call too if a batch could
  exceed `limit`.
- **URL-length reality:** ids are ~7-digit integers; 50 ids ≈ `50 × ~16 chars` (`&plantId=1018109`)
  ≈ **800 chars** of query string — comfortably under typical ~2 KB URL limits. 50 is safe on
  both counts; do **not** raise it without confirming the server's `limit`/id cap live.

### 3.2a Summary id field + stable summary fields (feeds an optional id-catalog table)

The **id field name in each summary row** (⚠ inferred from the confirmed `companies`/`projects`
summary convention — `companyId`/`projectId`):

| Summary endpoint | Id field (JSON) | Feeds detail param |
|------------------|-----------------|--------------------|
| `plants/summary` | `plantId` ⚠ | `plants/detail?plantId=…` |
| `units/summary` | `unitId` ⚠ | `units/detail?unitId=…` |
| `offlineevents/summary` | `eventId` ⚠ | `offlineevents/detail?eventId=…` |

- **Id-catalog table minimum = `(id, RunDate)`.** That is the only guaranteed-stable content
  (the id is always present; `RunDate` is loader-stamped) and is all that is needed to drive
  step 2.
- **Extra clearly-stable summary fields** likely present (from the `companies`/`projects`
  summary shape) that a catalog table *may* also keep: `plantName`/`unitName`/event name,
  a `*StatusDesc`, `physicalAddress`/`plantPhysicalAddress` (`city`/`stateName`/`countryName`),
  `releaseDate`, and — changelog-confirmed **on summary** — `plantLatitude`/`plantlongitude`.
  ⚠ Treat all of these as **candidate** summary fields to confirm live; only `(id, RunDate)` is
  certain. See §8.0 for the load-bearing "is this column in detail too?" check on lat/long.

### 3.3 Response envelope

Every summary/detail response is an object:

```json
{ "limit": 100, "offset": 0, "resultCount": 100, "totalCount": 111644,
  "plants": [ { … }, { … } ] }
```

| Envelope field | Type | Meaning |
|----------------|------|---------|
| `limit` | integer | echo of the requested page size. |
| `offset` | integer | echo of the requested offset. |
| `resultCount` | integer | rows in **this** page. |
| `totalCount` | integer | total rows matching the query across all pages. |
| `<product>` | array | the data rows. Array key = the product name: **`plants`**, **`units`**, **`offlineEvents`** (⚠ exact casing of the offline-events array key — `offlineEvents` vs `offlineevents` — to confirm live). |

- **Paginate:** start `offset=0`, `limit=1000` (summary); loop `offset += limit` **while
  `offset < totalCount`** (equivalently until `resultCount < limit`).
- **No-data / error:** treat an empty array (or `404`) as "nothing for this work unit → skip,
  not fail" (the CWG/AGSI 404-tolerant pattern). `401` → re-mint token; `403` → entitlement gap.

### 3.4 Rate limits

- **No numeric rate limit is published** in the IIR docs; the brochure explicitly frames the
  API as pull-on-demand "as often and as frequently as needed." **No `Retry-After`/throttle
  header is documented.** ⚠ Pace conservatively (a low requests-per-second cap) and back off on
  any `429`. Not live-verifiable here.

### 3.5 Date/number formats to parse

| Format | Example | Where | Recommended handling |
|--------|---------|-------|----------------------|
| ISO-8601 with **`Z[UTC]` suffix** | `2019-01-29T22:39:21Z[UTC]` | all date/datetime fields (`releaseDate`, `liveDate`, `startupDate`, `eventStartDate`, `eventEndDate`, …) | **strip the trailing `[UTC]`** then parse as UTC → SQL `DATETIME2`/`DATE`. The DDL stores most as `DATETIME2(0)` (date+time, no fraction). |
| plain integer/decimal | `800000000`, `62250` | ids, counts, capacities, lat/long | parse invariant-culture; assume a measure *may* arrive as a JSON string and parse defensively. |
| flag as integer | `0` / `1` | the many `INT` boolean-style columns (`Offshore`, `CogenChp`, mining-method flags, `IsDerated`, `Renewable`) | ⚠ **confirm** whether IIR returns these as `0/1` integers, `true/false` booleans, or `"Y"/"N"` strings. DDL types them `INT`. |

---

# The three data pulls

> Field tables below use: **Column** = the exact target DDL column; **JSON (inferred)** = the
> inferred IIR field / path (⚠ = needs live confirmation of exact casing/nesting/presence);
> **SQL type** = the DDL type; **Null?** per the DDL. Fields nested in the JSON
> (`mailingAddress.city`, `phone.number`, `parent.companyName`) are **flattened** into columns
> by the loader, exactly as the DDL is already flattened.

## 5. Plant — MANDATORY two-step → `arm.Plant`

- **Step 1 (id discovery):** `POST /idb/v2.7/plants/summary?physicalAddressCountryName=U.S.A.&physicalAddressCountryName=Canada`
  — page via `limit`/`offset`, collect **`plantId`** from each row.
- **Step 2 (persisted record):** `POST /idb/v2.7/plants/detail?plantId=…&plantId=…` (≤ 50 ids/call,
  repeated param, empty body) → **the detail record populates every `arm.Plant` column below.**
- **Other `summary` filter params** available if wider/narrower scoping is ever needed (changelog;
  not exhaustive): `plantParentId`, `plantOwnerId`, `plantOperatorId`, `existingSqMMin/Max`,
  `worldRegionId`, `marketRegionId`, `industryCode`, `releaseDateMin/Max`, `liveDateMin/Max`.
- **PK:** `PlantId`.

| Column | JSON (inferred, from **detail**) | SQL type | Null? | Notes |
|--------|-----------------|----------|:-----:|-------|
| `PlantId` | `plantId` | `INT` | No | **PK.** integer id (e.g. 3207542). Also the summary id field. |
| `PlantName` | `plantName` | `VARCHAR(8000)` | Yes | |
| `PlantStatusDesc` | `plantStatusDesc` | `VARCHAR(8000)` | Yes | paired id `plantStatusId` likely also returned (no column). |
| `NoEmployees` | `noEmployees` ⚠ | `INT` | Yes | employee count. |
| `StartupDate` | `startupDate` ⚠ | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `LiveDate` | `liveDate` | `DATETIME2(0)` | Yes | first-live date. |
| `ReleaseDate` | `releaseDate` | `DATETIME2(0)` | Yes | last-released date. |
| `OperationsLaborPreference` | `operationsLaborPreference`/`…Id` ⚠ | `INT` | Yes | PECWeb "Operations Labor Preference"; DDL is `INT` → likely a coded id. |
| `PrimaryFuel` | `primaryFuel` ⚠ | `VARCHAR(8000)` | Yes | |
| `SecondaryFuel` | `secondaryFuel` ⚠ | `VARCHAR(8000)` | Yes | |
| `IndustryCode` | `industryCode` | `VARCHAR(8000)` | Yes | 2-digit code (e.g. `01`). ⚠ may be an array/object in detail (was an array in `companies/detail`). |
| `IndustryCodeDesc` | `industryCodeDesc` | `VARCHAR(8000)` | Yes | e.g. `Power`. |
| `PrimarySicId` | `primarySicId` ⚠ | `VARCHAR(8000)` | Yes | SIC code. |
| `PrimarySicDesc` | `primarySicDesc` ⚠ | `VARCHAR(8000)` | Yes | |
| `PecZone` | `pecZone` | `VARCHAR(8000)` | Yes | e.g. `TX*05`. |
| `MarketRegionId` | `marketRegionId` | `VARCHAR(8000)` | Yes | DDL is VARCHAR though id-like — keep as text. |
| `MarketRegionName` | `marketRegionName` | `VARCHAR(8000)` | Yes | |
| `ConfirmationStatus` | `confirmationStatus` ⚠ | `VARCHAR(8000)` | Yes | |
| `NercRegion` | `nercRegion` ⚠ | `VARCHAR(8000)` | Yes | |
| `NercSubRegionName` | `nercSubRegionName` ⚠ | `VARCHAR(8000)` | Yes | |
| `ElectricalConnectionName` | `electricalConnectionName` ⚠ | `VARCHAR(8000)` | Yes | |
| `TradingRegionId` | `tradingRegionId` ⚠ | `INT` | Yes | |
| `TradingRegionName` | `tradingRegionName` ⚠ | `VARCHAR(8000)` | Yes | |
| `CogenChp` | `cogenChp` ⚠ | `INT` | Yes | flag 0/1. |
| `Metallurgical` | `metallurgical` ⚠ | `INT` | Yes | flag. |
| `Thermal` | `thermal` ⚠ | `INT` | Yes | flag. |
| `Placer` | `placer` ⚠ | `INT` | Yes | **mining-method flag.** |
| `OpenPit` | `openPit` ⚠ | `INT` | Yes | mining-method flag. |
| `Quarry` | `quarry` ⚠ | `INT` | Yes | mining-method flag. |
| `Strip` | `strip` ⚠ | `INT` | Yes | mining-method flag. |
| `Auger` | `auger` ⚠ | `INT` | Yes | mining-method flag. |
| `Dredging` | `dredging` ⚠ | `INT` | Yes | mining-method flag. |
| `Drift` | `drift` ⚠ | `INT` | Yes | mining-method flag. |
| `Shaft` | `shaft` ⚠ | `INT` | Yes | mining-method flag. |
| `Slope` | `slope` ⚠ | `INT` | Yes | mining-method flag. |
| `Longwall` | `longwall` ⚠ | `INT` | Yes | mining-method flag. |
| `RoomPillar` | `roomPillar` ⚠ | `INT` | Yes | mining-method flag. |
| `CutFill` | `cutFill` ⚠ | `INT` | Yes | mining-method flag. |
| `Caving` | `caving` ⚠ | `INT` | Yes | mining-method flag. |
| `Stoping` | `stoping` ⚠ | `INT` | Yes | mining-method flag. |
| `InSituSolution` | `inSituSolution` ⚠ | `INT` | Yes | mining-method flag. |
| `Longitude` | `longitude` / `plantLongitude` ⚠ | `FLOAT` | Yes | summary uses `plantlongitude` (lowercase l); detail casing to confirm. |
| `Latitude` | `latitude` / `plantLatitude` ⚠ | `FLOAT` | Yes | see above. |
| `PlantPoint` | *(derived)* | `GEOGRAPHY` | Yes | **no API field** — computed by loader/DB from `Longitude`/`Latitude`. |
| `WorldRegionId` | `worldRegionId` | `INT` | Yes | |
| `WorldRegionName` | `worldRegionName` | `VARCHAR(8000)` | Yes | |
| `Offshore` | `offshore` ⚠ | `INT` | Yes | flag. |
| `MailingAddressLine1` | `mailingAddress.addressLine1` | `VARCHAR(250)` | Yes | nested address object. |
| `MailingCity` | `mailingAddress.city` | `VARCHAR(250)` | Yes | |
| `MailingStateName` | `mailingAddress.stateName` | `VARCHAR(250)` | Yes | |
| `MailingPostalCode` | `mailingAddress.postalCode` | `VARCHAR(250)` | Yes | |
| `MailingCountryName` | `mailingAddress.countryName` | `VARCHAR(250)` | Yes | |
| `PhysicalAddressLine1` | `physicalAddress.addressLine1` | `VARCHAR(250)` | Yes | nested address object. |
| `PhysicalCity` | `physicalAddress.city` | `VARCHAR(250)` | Yes | |
| `PhysicalStateName` | `physicalAddress.stateName` | `VARCHAR(250)` | Yes | |
| `PhysicalPostalCode` | `physicalAddress.postalCode` | `VARCHAR(250)` | Yes | |
| `PhysicalCountryName` | `physicalAddress.countryName` | `VARCHAR(250)` | Yes | |
| `PhysicalCountyName` | `physicalAddress.countyName` | `VARCHAR(250)` | Yes | present on physical address only. |
| `PhoneCC` | `phone.cc` | `VARCHAR(250)` | Yes | nested `phone` object (`cc` is numeric in companies — cast to text). |
| `PhoneNumber` | `phone.number` | `VARCHAR(250)` | Yes | |
| `ParentCompanyId` | `parent.companyId` ⚠ | `VARCHAR(250)` | Yes | nested parent-company object (name TBD: `parent`/`plantParent`). |
| `ParentCompanyName` | `parent.companyName` ⚠ | `VARCHAR(250)` | Yes | |
| `ParentCompanyWebsite` | `parent.companyWebsite` ⚠ | `VARCHAR(250)` | Yes | |
| `OperatorCompanyId` | `operator.companyId` ⚠ | `VARCHAR(250)` | Yes | nested operator-company object. |
| `OperatorCompanyName` | `operator.companyName` ⚠ | `VARCHAR(250)` | Yes | |
| `OperatorCompanyWebsite` | `operator.companyWebsite` ⚠ | `VARCHAR(250)` | Yes | |
| `ModifiedAtUtc` | *(stamped)* | `DATETIME2(3)` | Yes | **no API field** — DB default `SYSDATETIME()`. |

## 6. Unit — MANDATORY two-step → `arm.Unit`

- **Step 1 (id discovery):** `POST /idb/v2.7/units/summary?physicalAddressCountryName=U.S.A.&physicalAddressCountryName=Canada`
  — page via `limit`/`offset`, collect **`unitId`** from each row.
- **Step 2 (persisted record):** `POST /idb/v2.7/units/detail?unitId=…&unitId=…` (≤ 50 ids/call,
  repeated param, empty body) → **the detail record populates every `arm.Unit` column below.**
- **Other `summary` filter params** (changelog): `existBuildingAreaSqFtMin/Max`,
  `existBuildingAreaSqMMin/Max`, `existProductAreaSqFtMin/Max`, `existProductAreaSqMMin/Max`,
  `plantSectorDesc`, plus region/industry/address filters.
- **PK:** `UnitId`.

| Column | JSON (inferred, from **detail**) | SQL type | Null? | Notes |
|--------|-----------------|----------|:-----:|-------|
| `UnitId` | `unitId` | `INT` | No | **PK.** Also the summary id field. |
| `UnitName` | `unitName` | `VARCHAR(8000)` | Yes | |
| `PlantId` | `plantId` | `INT` | Yes | FK → `arm.Plant`. |
| `PlantName` | `plantName` | `VARCHAR(8000)` | Yes | |
| `PlantStatusDesc` | `plantStatusDesc` | `VARCHAR(8000)` | Yes | |
| `PlantAddressLine1` | `plantPhysicalAddress.addressLine1` ⚠ | `VARCHAR(250)` | Yes | plant address, nested (name TBD `plantPhysicalAddress`/`plantAddress`). |
| `PlantCity` | `plantPhysicalAddress.city` ⚠ | `VARCHAR(250)` | Yes | |
| `PlantStateName` | `plantPhysicalAddress.stateName` ⚠ | `VARCHAR(250)` | Yes | |
| `PlantPostalCode` | `plantPhysicalAddress.postalCode` ⚠ | `VARCHAR(250)` | Yes | |
| `PlantCountryName` | `plantPhysicalAddress.countryName` ⚠ | `VARCHAR(250)` | Yes | |
| `PlantCountyName` | `plantPhysicalAddress.countyName` ⚠ | `VARCHAR(250)` | Yes | |
| `MarketRegionId` | `marketRegionId` | `VARCHAR(8000)` | Yes | |
| `MarketRegionName` | `marketRegionName` | `VARCHAR(8000)` | Yes | |
| `WorldRegionId` | `worldRegionId` | `INT` | Yes | |
| `WorldRegionName` | `worldRegionName` | `VARCHAR(8000)` | Yes | |
| `TradingRegionId` | `tradingRegionId` ⚠ | `INT` | Yes | |
| `TradingRegionName` | `tradingRegionName` ⚠ | `VARCHAR(8000)` | Yes | |
| `UnitStatusDesc` | `unitStatusDesc` | `VARCHAR(8000)` | Yes | |
| `UnitStatusGroup` | `unitStatusGroup` ⚠ | `VARCHAR(8000)` | Yes | |
| `HeaterCount` | `heaterCount` ⚠ | `INT` | Yes | |
| `UnitTypeId` | `unitTypeId` | `VARCHAR(8000)` | Yes | DDL text (may arrive as number/string). |
| `UnitTypeDesc` | `unitTypeDesc` | `VARCHAR(8000)` | Yes | |
| `UnitTypeGroup` | `unitTypeGroup` ⚠ | `VARCHAR(8000)` | Yes | |
| `CapacityProductId` | `unitCapacity.capacityProductId` ⚠ | `VARCHAR(8000)` | Yes | nested `unitCapacity` object (changelog confirms `unitCapacity.capacity`). |
| `Capacity` | `unitCapacity.capacity` | `FLOAT` | Yes | changelog-confirmed nesting. |
| `CapacityUom` | `unitCapacity.capacityUom` ⚠ | `VARCHAR(8000)` | Yes | unit of measure. |
| `PrimarySicId` | `primarySicId` ⚠ | `VARCHAR(8000)` | Yes | |
| `PrimarySicDesc` | `primarySicDesc` ⚠ | `VARCHAR(8000)` | Yes | |
| `AreaId` | `areaId` ⚠ | `INT` | Yes | |
| `AreaName` | `areaName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantLatitude` | `plantLatitude` | `FLOAT` | Yes | changelog-confirmed (summary). |
| `PlantLongitude` | `plantLongitude` / `plantlongitude` | `FLOAT` | Yes | ⚠ changelog shows lowercase `plantlongitude` — confirm casing. |
| `PlantPoint` | *(derived)* | `GEOGRAPHY` | Yes | **no API field** — computed from lat/long. |
| `Offshore` | `offshore` ⚠ | `INT` | Yes | flag. |
| `IndustryCode` | `industryCode` | `VARCHAR(8000)` | Yes | |
| `IndustryCodeDesc` | `industryCodeDesc` | `VARCHAR(8000)` | Yes | |
| `Technology` | `technology` ⚠ | `VARCHAR(8000)` | Yes | |
| `Renewable` | `renewable` ⚠ | `INT` | Yes | flag. |
| `CogenChp` | `cogenChp` ⚠ | `INT` | Yes | flag. |
| `PlantOperatorName` | `plantOperatorName` / `plantOperator.companyName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantOwnerName` | `plantOwnerName` / `plantOwner.companyName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantParentName` | `plantParentName` / `plantParent.companyName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantPhone` | `plantPhone` / `phone.number` ⚠ | `VARCHAR(8000)` | Yes | |
| `ReleaseDate` | `releaseDate` | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `LiveDate` | `liveDate` | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `ModifiedAtUtc` | *(stamped)* | `DATETIME2(3)` | Yes | **no API field** — DB default. |

## 7. OfflineEvent — MANDATORY two-step → `arm.OfflineEvent`

- **Query scope — DEFAULT is COUNTRY-FILTER-ONLY (corrected).** The default run discovers ids
  by country, exactly like Plant/Unit — no status/kind scoping by default:

  ```
  STEP 1 (id discovery):
  POST /idb/v2.7/offlineevents/summary
       ?physicalAddressCountryName=U.S.A.&physicalAddressCountryName=Canada
  ```

  page via `limit`/`offset`, collect **`eventId`** from each row.

  ```
  STEP 2 (persisted record):
  POST /idb/v2.7/offlineevents/detail?eventId=…&eventId=…   (≤ 50 ids/call, empty body)
  ```

  → **the detail record populates every `arm.OfflineEvent` column below.** **Stamp `RunDate` =
  the run date** and **upsert** on `(RunDate, EventId)` — a daily snapshot, so the same event
  across days forms a history keyed by `RunDate`.

- **Optional / configurable narrowing (NOT the default):** the status/kind filters remain
  available on `summary` if a smaller active-set pull is ever wanted —
  `eventKind=O` (`O` = Offline, ⚠ enum unconfirmed), `eventStatusDesc=Ongoing|Future|Completed|Cancelled`
  (repeatable; `Ongoing`/`Future` confirmed, others ⚠; changelog also exposes a derived
  `derivedEventStatusDesc`), plus `worldRegionId`, `industryCode`. Keep these behind a config
  toggle; **do not apply them by default.**
- **No documented `modifiedSince`/`asOfDate` param.** ⚠ Event date-window params
  (`eventStartDateMin/Max`, `eventEndDateMin/Max`) are plausible but unconfirmed — verify live
  only if a date-bounded/history pull is needed.
- **PK:** `(RunDate, EventId)`.

| Column | JSON (inferred, from **detail**) | SQL type | Null? | Notes |
|--------|-----------------|----------|:-----:|-------|
| `RunDate` | *(stamped)* | `DATE` | No | **PK.** loader run date — **no API field.** |
| `EventId` | `eventId` | `INT` | No | **PK.** Also the summary id field. |
| `EventKind` | `eventKind` / `eventKindDesc` ⚠ | `VARCHAR(8000)` | Yes | e.g. `O`/`Offline`. |
| `EventType` | `eventType` / `eventTypeDesc` ⚠ | `VARCHAR(8000)` | Yes | |
| `EventCause` | `eventCause` / `eventCauseDesc` ⚠ | `VARCHAR(8000)` | Yes | |
| `EventStatusDesc` | `eventStatusDesc` (or `derivedEventStatusDesc`) ⚠ | `VARCHAR(8000)` | Yes | Ongoing/Future/Completed/Cancelled. |
| `UnitId` | `unitId` | `INT` | Yes | FK → `arm.Unit`. |
| `UnitName` | `unitName` | `VARCHAR(8000)` | Yes | |
| `UnitStatusDesc` | `unitStatusDesc` | `VARCHAR(8000)` | Yes | |
| `IndustryCode` | `industryCode` | `VARCHAR(8000)` | Yes | |
| `IndustryCodeDesc` | `industryCodeDesc` | `VARCHAR(8000)` | Yes | |
| `PlantId` | `plantId` | `INT` | Yes | FK → `arm.Plant`. |
| `PlantName` | `plantName` | `VARCHAR(8000)` | Yes | |
| `PlantParentName` | `plantParentName` / `plantParent.companyName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantOwnerName` | `plantOwnerName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantOperatorName` | `plantOperatorName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantAddressLine1` | `plantPhysicalAddress.addressLine1` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantCity` | `plantPhysicalAddress.city` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantState` | `plantPhysicalAddress.stateName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantPostalCode` | `plantPhysicalAddress.postalCode` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantCountry` | `plantPhysicalAddress.countryName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantCounty` | `plantPhysicalAddress.countyName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PlantLatitude` | `plantLatitude` | `FLOAT` | Yes | |
| `PlantLongitude` | `plantLongitude` ⚠ | `FLOAT` | Yes | casing per §6 note. |
| `PlantPoint` | *(derived)* | `GEOGRAPHY` | Yes | **no API field** — computed from lat/long. |
| `AreaId` | `areaId` ⚠ | `INT` | Yes | |
| `AreaName` | `areaName` ⚠ | `VARCHAR(8000)` | Yes | |
| `Offshore` | `offshore` ⚠ | `INT` | Yes | flag. |
| `GasRegionId` | `gasRegionId` ⚠ | `VARCHAR(8000)` | Yes | |
| `GasRegionName` | `gasRegionName` ⚠ | `VARCHAR(8000)` | Yes | |
| `MarketRegionId` | `marketRegionId` | `VARCHAR(8000)` | Yes | |
| `MarketRegionName` | `marketRegionName` | `VARCHAR(8000)` | Yes | |
| `TradingRegionId` | `tradingRegionId` ⚠ | `INT` | Yes | |
| `TradingRegionName` | `tradingRegionName` ⚠ | `VARCHAR(8000)` | Yes | |
| `PowerTradeRegion` | `powerTradeRegion` ⚠ | `VARCHAR(8000)` | Yes | |
| `WorldRegionId` | `worldRegionId` | `INT` | Yes | |
| `WorldRegionName` | `worldRegionName` | `VARCHAR(8000)` | Yes | |
| `PecZone` | `pecZone` | `VARCHAR(8000)` | Yes | |
| `PrimarySicId` | `primarySicId` ⚠ | `VARCHAR(8000)` | Yes | |
| `UnitClassification` | `unitClassification` ⚠ | `VARCHAR(8000)` | Yes | |
| `Derate` | `derate` ⚠ | `FLOAT` | Yes | partial-derate amount. |
| `IsDerated` | `isDerated` ⚠ | `INT` | Yes | flag 0/1. |
| `ProductId` | `productId` ⚠ | `INT` | Yes | |
| `ProductDescription` | `productDescription` / `productDesc` ⚠ | `VARCHAR(8000)` | Yes | |
| `UnitCapacity` | `unitCapacity` ⚠ | `FLOAT` | Yes | ⚠ here a **scalar** (contrast §6 where `unitCapacity` is an object) — confirm. |
| `OfflineCapacity` | `offlineCapacity` ⚠ | `FLOAT` | Yes | |
| `OfflineCapacityUOM` | `offlineCapacityUom` / `offlineCapacityUOM` ⚠ | `VARCHAR(8000)` | Yes | |
| `EventStartDate` | `eventStartDate` ⚠ | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `EventEndDate` | `eventEndDate` ⚠ | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `EventDuration` | `eventDuration` ⚠ | `INT` | Yes | days (unit ⚠). |
| `PrevStartDate` | `prevStartDate` / `previousStartDate` ⚠ | `DATETIME2(0)` | Yes | prior forecast start. |
| `PrevEndDate` | `prevEndDate` / `previousEndDate` ⚠ | `DATETIME2(0)` | Yes | prior forecast end. |
| `UnitTypeId` | `unitTypeId` | `VARCHAR(8000)` | Yes | |
| `UnitTypeDesc` | `unitTypeDesc` | `VARCHAR(8000)` | Yes | |
| `EventConfirmationStatus` | `eventConfirmationStatus` / `confirmationStatus` ⚠ | `VARCHAR(8000)` | Yes | |
| `CogenChp` | `cogenChp` ⚠ | `INT` | Yes | flag. |
| `EventDatePrecision` | `eventDatePrecision` ⚠ | `VARCHAR(8000)` | Yes | e.g. DAY/MONTH/QUARTER. |
| `KickoffSlippage` | `kickoffSlippage` ⚠ | `INT` | Yes | months of slippage. |
| `EventComments` | `eventComments` / `comments` ⚠ | `VARCHAR(8000)` | Yes | free text. |
| `LiveDate` | `liveDate` | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `ReleaseDate` | `releaseDate` | `DATETIME2(0)` | Yes | `…Z[UTC]`. |
| `ModifiedAtUtc` | *(stamped)* | `DATETIME2(3)` | Yes | **no API field** — DB default. |

---

## 8. Field → column coverage & gaps

### 8.0 Detail ↔ DDL / TVP reconciliation (LOAD-BEARING — do not assume clean)

Because the DDL rows are populated from the **`detail`** response, every non-derived/non-stamped
column in §5/§6/§7 must be a **detail** field. The full `detail` schema was **not readable
unauthenticated** (spec truncates ~200 KB in, before `/plants/detail`), so this reconciliation
is an **assumption + a targeted risk flag**, not a live confirmation:

- **Assumption:** `detail` returns the **full record**, so all §5/§6/§7 columns are detail fields
  (that is why they are persisted from detail). The TVP mirrors these columns **1:1 plus
  `FileLogId`** (and, for OfflineEvent, the stamped `RunDate`).
- **The risk:** IIR's changelog added several fields to the **`summary`** endpoints. If any of
  those are **summary-only** (absent from `detail`), a detail-only persist would leave the column
  **NULL**. The candidates below MUST be checked live before the merge procs are frozen.

| Table | Column(s) | Why flagged | If NOT in detail → mitigation |
|-------|-----------|-------------|-------------------------------|
| `arm.Plant` | `Longitude`, `Latitude`, `PlantPoint` | changelog added `plantLatitude`/`plantlongitude` to **`plants/summary`** — lat/long may be a **summary-only** field. `PlantPoint` derives from them. | **Capture lat/long from the STEP-1 summary row** (id-catalog) and carry it into the detail-populated row; derive `PlantPoint` from that. |
| `arm.Unit` | `PlantLatitude`, `PlantLongitude`, `PlantPoint` | same — `plantLatitude`/`plantlongitude` added to **`units/summary`**. | same — source lat/long from the summary row. |
| `arm.OfflineEvent` | `PlantLatitude`, `PlantLongitude`, `PlantPoint` | same lat/long pattern (plant geo carried onto the event). | same — source lat/long from the summary row. |
| `arm.Plant` / `arm.Unit` | `ReleaseDate` | `releaseDate` was changelog-added to **summary**. | low risk (a released record almost certainly carries `releaseDate` in detail); confirm live. |
| `arm.OfflineEvent` | `EventStatusDesc` | summary exposes `derivedEventStatusDesc`; detail may name it `eventStatusDesc` or omit the derived form. | confirm detail returns a status desc; if only the derived form exists on summary, carry it from summary. |

> **Every other §5/§6/§7 column** is *expected* to be a detail field but is **unverified** (all
> `⚠`-marked). The one-shot live read (§10) closes both this table and the per-field casing at
> once. **Recommendation to DATABASE_DEVELOPER:** design the STEP-1 **id-catalog** to persist at
> least `(id, RunDate, latitude, longitude)` so lat/long is available even if `detail` omits it.

### 8.1 Columns with NO corresponding API field (deliberately NULL / derived / stamped)

These are **not** fed by the API and must be **left NULL / derived**, not silently ignored:

| Table | Column(s) with no direct API field | Source |
|-------|-------------------------------------|--------|
| `arm.Plant` | `PlantPoint` | **derived** by loader/DB from `Longitude`+`Latitude` (`GEOGRAPHY::Point`). |
| `arm.Plant` | `ModifiedAtUtc` | **stamped** (DB default `SYSDATETIME()`). |
| `arm.Unit` | `PlantPoint` | derived from `PlantLatitude`+`PlantLongitude`. |
| `arm.Unit` | `ModifiedAtUtc` | stamped. |
| `arm.OfflineEvent` | `RunDate` | **stamped** = loader run date (PK part). |
| `arm.OfflineEvent` | `PlantPoint` | derived from `PlantLatitude`+`PlantLongitude`. |
| `arm.OfflineEvent` | `ModifiedAtUtc` | stamped. |

⚠ Additionally, any `⚠`-marked column whose inferred field turns out **not** to exist in the
live response (e.g. if IIR does not return `nercRegion` or the mining-method flags for
non-coal plants) will be **NULL by absence**. The mining-method flags (`Placer`…`InSituSolution`)
are only meaningful for coal/mineral-mining plants and will be NULL for power/industrial plants.

### 8.2 Detail/summary fields with NO target column (returned but not persisted)

These are known/likely to be returned by `detail` (or, where noted, `summary`) and are
**intentionally dropped** (no column exists):

| Endpoint | API field (dropped) | Why |
|----------|---------------------|-----|
| `/plants` | `owner` company (id/name/website) | DDL keeps only **parent** + **operator**, not owner. |
| `/plants` | `sectors[]` (`sectors.plantSectorDesc`) | array of sector objects — no column. |
| `/plants` | `existingSqMeters` | plant area — no column. |
| `/plants` | `plantStatusId`, `*Id` counterparts of `*Desc` fields | DDL keeps the `Desc`, not the paired id (except where a column explicitly exists). |
| `/units` | `sectors[]` (`sectors.plantSectorDesc`) | no column. |
| `/units` | `existingBuildingAreaSqFeet/SqMeters`, `existingProductAreaSqFeet/SqMeters` | area metrics — no column. |
| `/offlineevents` | `heatRate` | changelog-added measure — no column. |
| `/offlineevents` | `sectors[]` (`sectors.plantSectorDesc`) | no column. |

⚠ The full "returned but no column" list for these three endpoints can only be finalized once
the live response is read; the above are the ones the changelog/analogous endpoints make
certain. Flag any newly-observed field to the MANAGER before deciding to drop it (the platform
rule is: document every field; drop only deliberately).

---

## 9. Coverage checklist (two-step summary → detail is MANDATORY for all three)

| # | Target | STEP 1 (id discovery) | id field | STEP 2 (persisted record) | Envelope array key | PK | Columns |
|---|--------|-----------------------|----------|---------------------------|--------------------|----|:-------:|
| 5 | `arm.Plant` | `POST /plants/summary?physicalAddressCountryName=…` | `plantId` ⚠ | `POST /plants/detail?plantId=…` (≤50) | `plants` ⚠ | `PlantId` | 71 (69 detail-mapped ⚠ + `PlantPoint` derived + `ModifiedAtUtc` stamped) |
| 6 | `arm.Unit` | `POST /units/summary?physicalAddressCountryName=…` | `unitId` ⚠ | `POST /units/detail?unitId=…` (≤50) | `units` ⚠ | `UnitId` | 47 (45 detail-mapped ⚠ + `PlantPoint` derived + `ModifiedAtUtc` stamped) |
| 7 | `arm.OfflineEvent` | `POST /offlineevents/summary?physicalAddressCountryName=…` | `eventId` ⚠ | `POST /offlineevents/detail?eventId=…` (≤50) | `offlineEvents` ⚠ | `(RunDate, EventId)` | 63 (60 detail-mapped ⚠ + `RunDate` stamped + `PlantPoint` derived + `ModifiedAtUtc` stamped) |

All calls: **`POST` + query-string params + empty body**; ids as **repeated query params**;
`Authorization: Bearer <jwt>`. OfflineEvent STEP-1 default is **country-filter-only** (status/kind
filters optional/configurable, §7).

**Gate status: CONDITIONAL PASS.** Every target column is accounted for (mapped to an inferred
**detail** field, or explicitly marked derived/stamped), and no column is silently ignored. The
auth flow, request transport, **mandatory two-step pull**, pagination, envelope, and naming
conventions are documented from official IIR sources. **The per-field JSON casing/nesting/
nullability for the three `detail` endpoints are RECONSTRUCTED and must be confirmed live before
the merge procs are frozen — including the §8.0 summary-vs-detail lat/long check** (see §10).

---

## 10. Confidence / gaps — what needs live verification in the user's environment

**High confidence (spec / official IIR docs):**
1. Base URL, versions, `/idb/{ver}/{product}/{summary|detail}` path structure.
2. JWT Bearer auth via `POST /token` (username/password query params; `tokenLifeTime` 1–30
   days, default 1; no refresh — re-request on expiry/401).
3. POST + query-string params + empty body; multi-value / id lists = repeated keys.
4. `limit`/`offset` pagination (Summary 100/1000, Detail 5/50), envelope
   `{limit, offset, resultCount, totalCount, <array>}`, loop until `offset ≥ totalCount`.
5. **MANDATORY two-step pull** for all three: `summary` (id discovery, filtered by
   `physicalAddressCountryName`) → `detail` (by id, ≤50/call) → detail record populates the DDL.
6. OfflineEvent default STEP-1 scope = **country-filter-only**; loader stamps `RunDate` and
   upserts on `(RunDate, EventId)`. Status/kind filters are optional/configurable, not default.
7. camelCase fields; nested `mailingAddress`/`physicalAddress`/`phone`/company objects;
   dates as `…Z[UTC]` (strip `[UTC]`).

**Needs a one-shot live confirmation (blocking before merge procs are frozen):**
1. **`/token` token delivery channel** — response **header** name carrying the JWT
   (expected `Authorization: Bearer …`) vs a body field. (§2.2)
2. **Exact JSON field names / casing / nesting** for all `⚠`-marked fields in §5/§6/§7 (the
   **`detail`** responses) — especially the nested parent/operator/owner company objects, the
   plant-address object name (`plantPhysicalAddress` vs `plantAddress`), `unitCapacity` (object in
   §6 vs scalar in §7), lat/long casing (`plantlongitude`?), and the mining-method flags.
3. **§8.0 summary-vs-detail check (load-bearing)** — confirm the changelog "summary" fields,
   **especially lat/long (`Longitude`/`Latitude`/`PlantLatitude`/`PlantLongitude`)**, are ALSO
   returned by `detail`. If not, source them from the STEP-1 summary row (id-catalog) or the
   detail-only persist NULLs them (and `PlantPoint`).
4. **Summary id field names** — confirm `plantId` / `unitId` / `eventId` are the exact summary-row
   id keys, and the small stable summary field set for the id-catalog. (§3.2a)
5. **Detail max ids per call** — confirm whether `detail` accepts > 50 ids or the `limit`=50 cap
   also caps id count; validates the batch size. (§3.2)
6. **Boolean flag encoding** — are `Offshore`/`CogenChp`/mining flags/`IsDerated`/`Renewable`
   returned as `0/1`, `true/false`, or `"Y"/"N"`? (affects the transform.)
7. **OfflineEvent optional narrowing enum** — full `eventStatusDesc`/`eventKind` value set and
   whether event **date-window** filters exist (only if the optional narrowing/history is used). (§7)
8. **The array key casing** in the envelope (`offlineEvents` vs `offlineevents`, `plants`, `units`).
9. **`physicalAddressCountryName` values** — exact U.S. spelling (`U.S.A.`) and the full country
   value vocabulary.
10. **Rate limits** — none published; set a conservative RPS and back off on `429`.
11. **Full filter-param list** per summary endpoint (only the changelog-named subset is captured).

> **Fastest way to close all of the above:** re-run `API_DOCUMENTATION_EXPERT` with authenticated
> network access to `https://api.industrialinfo.com/idb/v2.7`, or read the JSON keys straight
> from RapiDoc (`https://api.industrialinfo.com/RapiDoc/idb/v2.7/#post-/plants/detail`, `…/units/detail`,
> `…/offlineevents/detail`) via *Try it out → Execute* with the real PAT. The v2.7 `idb.json`
> could not be read past `/companies/detail` (~200 KB in) by any unauthenticated fetch tool
> available here, and the RapiDoc anchors are client-rendered shells with no field detail.
