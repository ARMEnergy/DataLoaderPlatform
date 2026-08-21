# IHSPointLogic (IHS Markit / S&P Global Connect — PointLogic gas data) loader — API field reference

Short-term North American natural-gas supply/demand, pipeline flows, production, storage, and
pipeline-notice analytics from **S&P Global Commodity Insights — PointLogic**, served through the
**IHS Markit Connect** gateway (`Server: Kestrel`). Two API families live under one host:
**`cs/v1/pointlogic/*`** (lookups + supplyDemand) and **`cs/v1/plview/retrieve/*`** (bulk fact
snapshots). This file is the **full-field-set gate** for the loader: every field of all
**25 endpoints** is enumerated below with exact source casing, recommended SQL Server type,
nullability, and format. Nothing downstream (model → sink → TVP → table → merge proc) may ship a
partial column set.

- **Recommended DB name:** `IHSPointLogic` — **schema:** `arm` — audit hub: `arm.FileLog` (per
  the platform convention used by Platts/CWG/AGSI). Final names are DATABASE_DEVELOPER's call.
- **Loader id:** `IHSPointLogic`.

---

## Gate status: **VERIFIED — live-observed 2026-08-18** ✅

All endpoints, the auth scheme, the envelope/paging model, date semantics, the three suspected
typos, and every response field were **confirmed by direct live calls** against
`https://api.connect.ihsmarkit.com` on **2026-08-18** using the PAT credentials (Basic auth). The
field tables below carry the **exact JSON field names as returned (all lowercase)**, observed row
counts, and observed value examples. Only a few non-blocking items remain (see the last section) —
none affect the column set.

### Verification provenance

| Item | Status |
|------|--------|
| Auth scheme (**HTTP Basic**, not OAuth2), `WWW-Authenticate` realm, 401 shape | **Live-observed 2026-08-18.** |
| Envelope shapes (flat array vs `PagingInfo`/`Data` wrapper) + `pageIndex` paging, 10000/page — **wrapper = 1-based (pages `1..page_count`), flat = 0-based** | **Live-observed** (wrapper 1-based confirmed on `lookup_point`, 2026-08-19). |
| Date semantics: `plview/retrieve/*` ignore date params; `supplyDemand/region|subregion` honour `reportDate`; `marketsHistory` full history | **Live-observed.** |
| The 3 typos (`pipelineNoticeCategory`, single `cs/v1/`, subregion reuses `/region/`) | **Live-observed** (distinct `/subregion/` path 404s; `/region/{subRegionId}` returns rows). |
| Every endpoint's full field set, casing, PK mapping, row counts | **Live-observed** per §5–§19. |

> **Secret handling:** the PAT credentials used for the probe are **never written here**. They are
> referred to only by config-variable name and default to the `SEE_DB` sentinel (real values live
> in `core.Param`). They are never logged.

---

## 1. Authentication — **plain HTTP Basic (RESOLVED: NOT OAuth2)**

A bare call returns **`401 Unauthorized`** with:

```
WWW-Authenticate: Basic realm="Please use your Personal Access Token (PAT) credentials.
                  For more information, visit https://myprofile.ihsmarkit.com/."
Server: Kestrel
```

So authentication is a **static HTTP Basic header on every data call** — **no OAuth2 token
exchange, no bearer token, no scope, no token endpoint** (every candidate token URL —
`/oauth2/token`, `/token`, `/connect/token`, … — returns 404/401). This **simplifies** the
loader's shared plumbing to a single constant header.

| Supplied credential | Basic-auth role | Config variable |
|---------------------|-----------------|-----------------|
| Username (the GUID `cbee80b2-…`) | **user id** (`client_id`) | `Loaders:IHSPointLogic:ClientId` |
| Password (the 16-char string) | **password** (`client_secret`) | `Loaders:IHSPointLogic:ClientSecret` |

Header on **every** `cs/v1/**` request:

```
GET https://api.connect.ihsmarkit.com/cs/v1/pointlogic/lookup_region
Authorization: Basic base64(<ClientId>:<ClientSecret>)
Accept: application/json
```

Both config values default to `SEE_DB` (env overrides `DATALOADER_Loaders__IHSPointLogic__ClientId`
/ `…__ClientSecret`), resolved from `core.Param` at run time. **Never hard-code, log, or place the
values in this file.** The same Basic header works for **both** families (one credential, one
gateway). A `401` = missing/wrong header → the loader should surface it as an auth failure (there is
no token to refresh); a `403` would indicate an entitlement gap.

---

## 2. Host, families, envelope, and paging

- **Base host:** `https://api.connect.ihsmarkit.com`
- **Family P — PointLogic:** `…/cs/v1/pointlogic/*` — lookups (`lookup_*`) + supplyDemand.
- **Family V — PLView retrieve:** `…/cs/v1/plview/retrieve/*` — bulk fact snapshots.
- **All responses:** JSON (`application/json`). **All data calls are `GET`.**

### Two envelope shapes (OBSERVED)

1. **Flat JSON array** — `[ {…}, {…} ]`. Used by **most lookups** and **all `plview/retrieve/*`
   facts**.
2. **Paged wrapper** — used by **`lookup_point`** and **all `supplyDemand/*`** and
   **`volumeHistory/point`**:

```json
{ "PagingInfo": { "page_size": 10000, "page_count": 3, "total_record_count": 25116 },
  "Data": [ { … }, { … } ] }
```

> Note the PascalCase envelope keys **`PagingInfo`** / **`Data`** (inner `page_size`,
> `page_count`, `total_record_count`), while every business field is **lowercase**.

### Paging — universal rule (OBSERVED)

- **Paging is by query param `?pageIndex=N`** (`page`/`pageNumber` are **ignored**). **`page_size` =
  10000** rows/page. **⚠ The two envelope shapes use DIFFERENT `pageIndex` bases — verified live 2026-08-19:**
  - **Wrapper** (`PagingInfo`/`Data`; the `pointlogic` family — `lookup_point`, `supplyDemand/*`,
    `volumeHistory/point`): **`pageIndex` is 1-BASED.** `pageIndex=0` returns the **same rows as
    `pageIndex=1`** (page 1); the real pages are at indices **`1 … page_count`**. Drive paging off
    **`page_count`** (read pages `1..page_count`). Evidence (`lookup_point`): pageIndex 0≡1 firstId 689,
    2 firstId 22043, 3 firstId 40776/5116 → `union(1,2,3)=25116` but `union(0,1,2)=20000`.
  - **Flat array** (the `plview/retrieve/*` family — `pointmetadata_withids`, etc.): **`pageIndex` is
    0-BASED.** Page `0,1,2,…` until a page returns `< 10000` rows or is empty.
- **Do NOT assume one base for both.** A 0-based reader silently drops the **last** page of a multi-page
  wrapper — the original bug loaded `arm.Point` with 20000 of 25116 rows.
- Multi-page confirmed: `pointmetadata_withids` **> 40 000** rows across **≥ 5** pages;
  `pipelinenotice_search` ~10 000 then an empty page; `us_samplestorage_facility`
  **10 000 + 5 982 = 15 982** over 2 pages; `lookup_point` **25 116** over 3 pages
  (`total_record_count` present in the wrapper).

### Rate limits / errors / no-data

- **Rate limits / 429 / `Retry-After`:** none surfaced during the probe — **pace conservatively**
  (a low requests-per-second cap) and back off on any `429`.
- **No-data / error:** an empty result is a **`200` + empty array** (or wrapper with empty `Data`);
  the reader should treat empty/`404` as "nothing to load for this work unit → skip, not fail"
  (the CWG/AGSI 404-tolerant pattern). `401` = auth failure (no token to refresh — fail clearly).

---

## 3. Date / backfill semantics (OBSERVED)

- **`plview/retrieve/*` IGNORE all date params.** `?date`, `?reportDate`, `?asOfDate` returned
  **byte-identical** snapshots → these are **current-snapshot-only**. The loader accumulates them
  **go-forward** (stamp the run date where the payload lacks one); **no historical backfill** is
  possible from these endpoints.
- **`supplyDemand/region|subregion/{id}` accept `?reportDate=yyyy-MM-dd`** — the only historical
  parametrized facts.
- **`supplyDemand/marketsHistory`** returns the **full history** (61 rows) with **no** date param.
- **`volumeHistory/point`** returns **recent** history rows for the requested points (a
  `startDate`/`endDate` range param was **not** confirmed — see open items).

### Mixed date/number formats to parse (OBSERVED — do NOT assume one format)

| Format | Where | Recommended SQL type |
|--------|-------|----------------------|
| `yyyy-MM-dd` | most `date`/`referencedate`/`timeperiod`/`flowdate` (retrieve facts), supplyDemand `date` | `DATE` |
| `yyyy-MM-dd HH:mm` | `gasproduction_producingarea.reporteddate` | `DATETIME2(0)` |
| `MM/dd/yyyy` | `pipelineflow_throughputs` & `stateflows_throughputaggregates` `flowdate`/`reporteddate` | `DATE` |
| `yyyy-MM-dd HH:mm:ss.fff -05:00` (DateTimeOffset) | `pipelinenotice_search` `posteddate`/`effectivedate`/`enddate` | `DATETIMEOFFSET(3)` |
| **Numerics as JSON strings** | `pointmetadata_withids.designcapacity` (`"0.00000000"`), `lookup_facility.facilitytypeid` (`"1"`) | parse invariant-culture before the sink |

---

## 4. The 3 suspected spec typos — RESOLVED (live)

| # | Given | Resolution (observed) |
|---|-------|-----------------------|
| 1 | `PipelieNoticeCategory` | **`cs/v1/pointlogic/lookup_pipelineNoticeCategory`** is correct — returns `{id,name}`, **23 rows**. |
| 2 | doubled `/cs/cs/v1/` on SD-by-region | **Single `cs/v1/`** — `cs/v1/pointlogic/supplyDemand/region/{RegionId}?reportDate=`. |
| 3 | SD-by-subregion reusing `/region/{id}` | **Confirmed reuse:** SD-by-subregion calls **`…/supplyDemand/region/{subRegionId}`**. A distinct **`…/supplyDemand/subregion/{id}` returns `404` (does not exist)**. Calling `/region/26252` (a real subregion id) returned 32 product rows. |

---

## Recommended type sizing (for DATABASE_DEVELOPER)

| Family | Recommended type | Rationale |
|--------|------------------|-----------|
| **Gas volume / flow / production / demand (`volume`, `volume_mmcfd`, sector measures)** | **`DECIMAL(18,6)`** | MMcf/d & Dth-scale headroom; several measures observed negative (`balancingitem`, `volume`). |
| **Temperature (°F)** (`normaltemperaturef`, `departurefromnormalf`) | **`DECIMAL(6,2)`** | signed °F. |
| **Latitude / longitude** (`pointlatitude`/`pointlongitude`) | **`DECIMAL(9,6)`** | geo precision. |
| **Ids** (`id`, `pointid`, `regionid`, `stateid`, `pipelineid`, `pointtypeid`, `pointstatusid`, `categoryid`, `countyid`, `flowdirectionid`) | **`INT`** | integers observed (e.g. 26105, 4520, 635182→`BIGINT` only for notice `id`). |
| **Notice id** (`pipelinenotice_search.id`) | **`BIGINT`** | e.g. `635182`. |
| **Bit flags** (`iscritical`, `pointisactive`) | **`BIT`** | boolean. |
| **Names / labels** | `NVARCHAR(128)`–`NVARCHAR(400)` | per field below. |

> **Numbers-as-strings:** `designcapacity` and `facilitytypeid` arrive as JSON **strings** — parse
> invariant-culture. Assume any measure *may* be a string and parse defensively.

---

# TIER 0 — Independent lookups (no path/query params)

Family P. `GET https://api.connect.ihsmarkit.com/cs/v1/pointlogic/<path>` + Basic header.

## 5. Region — `GET cs/v1/pointlogic/lookup_region` → `arm.Region`

Flat array of `{name, id}`. `id` e.g. `26105` (Mid-Continent), `26158` (Canada). **Discovery id**
feeding `lookup_subregion/{RegionId}` (§16) and `supplyDemand/region/{RegionId}` (§17).

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`RegionId`** | `id` | `INT` | No | **PK.** |
| `Name` | `name` | `NVARCHAR(128)` | No | region name. |

**PK:** `RegionId` (= `id`).

## 6. Independent lookups (same flat `{id,name}` shape unless noted)

| # | Entity → table | Path | Rows | Fields (observed casing) | PK |
|---|----------------|------|:----:|--------------------------|----|
| 6-a | State → `arm.State` | `lookup_state` | 52 | `{name, id}` (id e.g. 4520 Alabama, 4536 Louisiana) | `StateId`=`id` |
| 6-b | PointStatus → `arm.PointStatus` | `lookup_pointStatus` | 17 | `{name, id}` | `PointStatusId`=`id` |
| 6-c | PointType → `arm.PointType` | `lookup_pointType` | 17 | `{name, id}` (e.g. `1`=Power). **Discovery id → `lookup_facility/{PointTypeId}`** | `PointTypeId`=`id` |
| 6-d | PipelineNoticeCategory → `arm.PipelineNoticeCategory` | `lookup_pipelineNoticeCategory` | 23 | `{id, name}` | `PipelineNoticeCategoryId`=`id` |
| 6-e | Pipeline → `arm.Pipeline` | `lookup_pipeline` | 195 | `{name, id, legacyname}` — `legacyname` `NVARCHAR(200)` **nullable** | `PipelineId`=`id` |

For 6-a…6-c: `Name` `NVARCHAR(128)` No; `id` `INT` No (PK). State `Name` is a full state name;
`Abbreviation` is **not** returned by `lookup_state`.

## 7. Point (lean list) — `GET cs/v1/pointlogic/lookup_point` → `arm.Point`

**WRAPPER** (`PagingInfo`/`Data`); `total_record_count` **25 116**, `page_count` **3** — page it.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`PointId`** | `id` | `INT` | No | **PK.** |
| `Name` | `name` | `NVARCHAR(200)` | No | point name. |
| `PipelineId` | `pipelineid_0` | `INT` | Yes | **note the `_0` suffix** in the JSON name. FK → `arm.Pipeline`. |
| `PointTypeId` | `pointtypeid` | `INT` | Yes | FK → `arm.PointType`. |
| `PointStatusId` | `pointstatusid` | `INT` | Yes | FK → `arm.PointStatus`. |

**PK:** `PointId`. (The **rich** point dimension is `pointmetadata_withids`, §12.)

---

# TIER 0 — Independent fact snapshots (current-snapshot; report date participates in PK)

Family V (flat arrays) unless noted. Field casing is exactly as returned (lowercase).

## 8. DemandForecastRegion — `GET cs/v1/plview/retrieve/demandforecast_region` → `arm.DemandForecastRegion`

Flat array, **518 rows**. **No forecast/as-of date in the body → the loader STAMPS
`ForecastDate` = run date (UTC).**

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`ForecastDate`** | *(stamped)* | `DATE` | No | **PK.** run/report date (UTC), stamped by the loader. |
| **`Date`** | `date` | `DATE` | No | **PK.** forecast gas day. |
| **`Region`** | `region` | `NVARCHAR(128)` | No | **PK.** |
| **`Subregion`** | `subregion` | `NVARCHAR(128)` | No | **PK.** |
| `DepartureFromNormalF` | `departurefromnormalf` | `DECIMAL(6,2)` | Yes | temperature departure °F (signed). |
| `NormalTemperatureF` | `normaltemperaturef` | `DECIMAL(6,2)` | Yes | normal temperature °F. |
| `TotalConsumption` | `totalconsumption` | `DECIMAL(18,6)` | Yes | total demand. |
| `Power` | `power` | `DECIMAL(18,6)` | Yes | power-sector demand. |
| `Industrial` | `industrial` | `DECIMAL(18,6)` | Yes | industrial demand. |
| `ResCom` | `rescom` | `DECIMAL(18,6)` | Yes | residential/commercial demand. |
| `DepartureFromNormal` | `departurefromnormal` | `DECIMAL(18,6)` | Yes | demand departure from normal (signed). |

**PK:** `(ForecastDate[stamped], Date, Region, Subregion)`.

## 9. DemandForecastUSLower48 — `GET cs/v1/plview/retrieve/demandforecast_uslower48` → `arm.DemandForecastUsLower48`

Flat array, **15 rows**. **STAMP `ForecastDate` = run date (UTC).**

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`ForecastDate`** | *(stamped)* | `DATE` | No | **PK.** stamped run date (UTC). |
| **`Date`** | `pointreadingaggregate_endingdate` | `DATE` | No | **PK.** forecast gas day (**note the long JSON name**). |
| **`Region`** | `regionname` | `NVARCHAR(128)` | No | **PK.** |
| `Power` | `power` | `DECIMAL(18,6)` | Yes | |
| `Industrial` | `industrial` | `DECIMAL(18,6)` | Yes | |
| `ResidentialCommercial` | `residentialcommercial` | `DECIMAL(18,6)` | Yes | |
| `Subtotal` | `subtotal` | `DECIMAL(18,6)` | Yes | |

**PK:** `(ForecastDate[stamped], Date, Region)`.

## 10. GasProduction_ProducingArea — `GET cs/v1/plview/retrieve/gasproduction_producingarea` → `arm.GasProductionProducingArea`

Flat array (~thousands of rows).

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`ReportedDate`** | `reporteddate` | `DATETIME2(0)` | No | **PK.** format `yyyy-MM-dd HH:mm` (has time) → keep datetime granularity. |
| **`ReferenceDate`** | `referencedate` | `DATE` | No | **PK.** production gas day. |
| **`Region`** | `region` | `NVARCHAR(128)` | No | **PK.** |
| **`ProducingArea`** | `producingarea` | `NVARCHAR(128)` | No | **PK.** basin/producing-area. |
| **`State`** | `state` | `NVARCHAR(64)` | No | **PK.** **can be a NAME, incl. `"Gulf of Mexico"`** → NVARCHAR, not a 2-char code. |
| `DryFactoredValue` | `dryfactoredvalue` | `DECIMAL(18,6)` | Yes | modeled dry production. |
| `WellheadValue` | `wellheadvalue` | `DECIMAL(18,6)` | Yes | modeled wellhead production. |

**PK:** `(ReportedDate, ReferenceDate, Region, ProducingArea, State)`.

## 11. MarketBalancesUSLower48 — `GET cs/v1/plview/retrieve/marketbalances_uslower48` → `arm.MarketBalancesUsLower48`

Flat array, **21 rows**. One **wide national balance** row per `timeperiod`. **16 measures**, all
`DECIMAL(18,6)` nullable (`balancingitem` can be negative).

| Field | JSON | SQL type | Null? |
|-------|------|----------|-------|
| **`TimePeriod`** | `timeperiod` | `DATE` | No **(PK)** |
| `Wellhead` | `wellhead` | `DECIMAL(18,6)` | Yes |
| `ProductionLoss` | `productionloss` | `DECIMAL(18,6)` | Yes |
| `DryGas` | `drygas` | `DECIMAL(18,6)` | Yes |
| `CanadaImports` | `canadaimports` | `DECIMAL(18,6)` | Yes |
| `LngSendout` | `lngsendout` | `DECIMAL(18,6)` | Yes |
| `TotalSupply` | `totalsupply` | `DECIMAL(18,6)` | Yes |
| `Power` | `power` | `DECIMAL(18,6)` | Yes |
| `Industrial` | `industrial` | `DECIMAL(18,6)` | Yes |
| `ResidentialCommercial` | `residentialcommercial` | `DECIMAL(18,6)` | Yes |
| `Subtotal` | `subtotal` | `DECIMAL(18,6)` | Yes |
| `MexicoExports` | `mexicoexports` | `DECIMAL(18,6)` | Yes |
| `LngFeedGas` | `lngfeedgas` | `DECIMAL(18,6)` | Yes |
| `PipeLoss` | `pipeloss` | `DECIMAL(18,6)` | Yes |
| `TotalDemand` | `totaldemand` | `DECIMAL(18,6)` | Yes |
| `Storage` | `storage` | `DECIMAL(18,6)` | Yes |
| `BalancingItem` | `balancingitem` | `DECIMAL(18,6)` | Yes (signed) |

**PK:** `(TimePeriod)`.

## 12. PointMetadata (rich dimension) — `GET cs/v1/plview/retrieve/pointmetadata_withids` → `arm.PointMetadata`

**Flat array but paged** (`pageIndex`) — **> 40 000 rows across ≥ 5 pages.** The authoritative
point dimension (22 columns).

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`PointId`** | `pointid` | `INT` | No | **PK.** |
| `PointLciId` | `pointlciid` | `VARCHAR(16)` | Yes | e.g. `ACADA0001`. |
| `Drn` | `drn` | `NVARCHAR(64)` | Yes | can be a **blank string `" "`** → normalize to NULL. |
| `PointName` | `pointname` | `NVARCHAR(200)` | No | |
| `PointTypeId` | `pointtypeid` | `INT` | Yes | FK → `arm.PointType`. |
| `PointType` | `pointtype` | `NVARCHAR(64)` | Yes | type label. |
| `StateId` | `stateid` | `INT` | Yes | FK → `arm.State`. |
| `State` | `state` | `NVARCHAR(64)` | Yes | state label. |
| `County` | `county` | `NVARCHAR(128)` | Yes | can be blank `" "` → NULL. |
| `Region` | `region` | `NVARCHAR(128)` | Yes | can be blank `" "` → NULL. |
| `PipelineId` | `pipelineid` | `INT` | Yes | FK → `arm.Pipeline`. |
| `PipelineDisplayName` | `pipelinedisplayname` | `NVARCHAR(200)` | Yes | |
| `PointIsActive` | `pointisactive` | `BIT` | Yes | |
| `DesignCapacity` | `designcapacity` | `DECIMAL(18,6)` | Yes | **arrives as JSON STRING** `"0.00000000"` — parse. |
| `FlowDirectionId` | `flowdirectionid` | `INT` | Yes | |
| `FlowDirection` | `flowdirection` | `NVARCHAR(32)` | Yes | |
| `DisplayName` | `displayname` | `NVARCHAR(200)` | Yes | |
| `LocProp` | `locprop` | `NVARCHAR(64)` | Yes | |
| `PointLatitude` | `pointlatitude` | `DECIMAL(9,6)` | Yes | |
| `PointLongitude` | `pointlongitude` | `DECIMAL(9,6)` | Yes | |
| `CountyId` | `countyid` | `INT` | Yes | FK → `arm.County`. |
| `RegionId` | `regionid` | `INT` | Yes | FK → `arm.Region`. |

**PK:** `PointId`.

## 13. ModeledDemandRegionType — `GET cs/v1/plview/retrieve/modeleddemand_region_type` → `arm.ModeledDemandRegionType`

Flat array, **558 rows** (LONG/tall — product on the key).

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`ReferenceDate`** | `referencedate` | `DATE` | No | **PK.** |
| **`PLEProductName`** | `pleproductname` | `NVARCHAR(128)` | No | **PK.** product/sector name. |
| **`RegionName`** | `regionname` | `NVARCHAR(128)` | No | **PK.** |
| `Volume` | `volume` | `DECIMAL(18,6)` | Yes | modeled demand (values ~10 968). |

**PK:** `(ReferenceDate, PLEProductName, RegionName)`.

## 14. PipelineFlowThroughput — `GET cs/v1/plview/retrieve/pipelineflow_throughputs` → `arm.PipelineFlowThroughput`

Flat array (~2.3 MB). **`reporteddate` EXISTS but is EXCLUDED — do NOT persist it.**

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`FlowDate`** | `flowdate` | `DATE` | No | **PK.** format `MM/dd/yyyy`. |
| **`Region`** | `region` | `NVARCHAR(128)` | No | **PK.** |
| **`Pipeline`** | `pipeline` | `NVARCHAR(200)` | No | **PK.** |
| **`Throughput`** | `throughput` | `NVARCHAR(200)` | No | **PK.** — a **LOCATION/SEGMENT label** (e.g. `"CT to RI"`), **NOT a measure**. PK is correct as stated. |
| **`FlowType`** | `flowtype` | `NVARCHAR(32)` | No | **PK.** e.g. `Inflow`/`Outflow`. |
| `Volume` | `volume` | `DECIMAL(18,6)` | Yes | the flow measure. |
| ~~`reporteddate`~~ | `reporteddate` | *(not persisted)* | — | `MM/dd/yyyy`; **EXCLUDED per task**. |

**PK:** `(FlowDate, Region, Pipeline, Throughput, FlowType)`.

## 15. PipelineNoticeSearch — `GET cs/v1/plview/retrieve/pipelinenotice_search` → `arm.PipelineNoticeSearch`

**Flat array but paged** (`pageIndex`) — ~10 000 then an empty page.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`Id`** | `id` | `BIGINT` | No | **PK.** e.g. `635182`. |
| `Subject` | `subject` | `NVARCHAR(400)` | Yes | |
| `Format` | `format` | `VARCHAR(16)` | Yes | |
| `PostedDate` | `posteddate` | `DATETIMEOFFSET(3)` | Yes | DateTimeOffset `… -05:00`. |
| `ExternalId` | `externalid` | `VARCHAR(32)` | Yes | |
| `CategoryId` | `categoryid` | `INT` | Yes | FK → `arm.PipelineNoticeCategory`. |
| `IsCritical` | `iscritical` | `BIT` | Yes | |
| `ContentLink` | `contentlink` | `NVARCHAR(400)` | Yes | URL to the operator posting. |
| `PipelineId` | `pipelineid` | `INT` | Yes | FK → `arm.Pipeline`. |
| `EffectiveDate` | `effectivedate` | `DATETIMEOFFSET(3)` | Yes | |
| `EndDate` | `enddate` | `DATETIMEOFFSET(3)` | Yes | nullable. |

**PK:** `(Id)`.

## 16. USImportsExportsByPointsAggregate — `GET cs/v1/plview/retrieve/us_importsexportsby_points_aggregate` → `arm.UsImportsExportsByPointsAggregate`

Flat array, **3 296 rows**.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`RunDate`** | `rundate` | `DATE` | No | **PK.** |
| **`FlowDate`** | `flowdate` | `DATE` | No | **PK.** |
| **`PointName`** | `pointname` | `NVARCHAR(200)` | No | **PK.** → `arm.Point`. |
| **`PipelineName`** | `pipelinename` | `NVARCHAR(200)` | No | **PK.** → `arm.Pipeline`. |
| **`LedgerSide`** | `ledgerside` | `NVARCHAR(16)` | No | **PK.** `Receipt`/`Delivery`. |
| `Volume` | `volume` | `DECIMAL(18,6)` | Yes | flow volume. |
| `State` | `state` | `VARCHAR(8)` | Yes | |
| `County` | `county` | `NVARCHAR(128)` | Yes | |
| `Type` | `type` | `NVARCHAR(64)` | Yes | |
| `PointGroupName` | `pointgroupname` | `NVARCHAR(128)` | Yes | |
| `DistrictName` | `districtname` | `NVARCHAR(128)` | Yes | |

**PK:** `(RunDate, FlowDate, PointName, PipelineName, LedgerSide)`.

## 17. US_SampleStorage_Facility — `GET cs/v1/plview/retrieve/us_samplestorage_facility` → `arm.UsSampleStorageFacility`

**Flat array but paged** — **10 000 + 5 982 = 15 982** over 2 pages.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`ReportDate`** | `reportdate` | `DATE` | No | **PK.** *(user "RunDate" → `reportdate`)*. |
| **`FlowDate`** | `flowdate` | `DATE` | No | **PK.** |
| **`Name`** | `name` | `NVARCHAR(200)` | No | **PK.** facility name. |
| **`EiaRegion`** | `eiaregion` | `NVARCHAR(64)` | No | **PK.** EIA storage region. |
| **`State`** | `state` | `VARCHAR(4)` | No | **PK.** 2-char code (e.g. `MD`). |
| **`FieldType`** | `field_type` | `NVARCHAR(32)` | No | **PK.** *(user "FieldType" → `field_type`)*; e.g. `Depleted Field`. |
| `Volume` | `volume` | `DECIMAL(18,6)` | Yes | |

**PK:** `(ReportDate, FlowDate, Name, EiaRegion, State, FieldType)`.

## 18. StateFlowsThroughputAggregate — `GET cs/v1/plview/retrieve/stateflows_throughputaggregates` → `arm.StateFlowsThroughputAggregate`

Flat array, **9 772 rows**.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`FlowDate`** | `flowdate` | `DATE` | No | **PK.** format `MM/dd/yyyy`. |
| **`Region`** | `region` | `NVARCHAR(128)` | No | **PK.** |
| **`FromState`** | `fromstate` | `NVARCHAR(64)` | No | **PK.** a **NAME** (e.g. `Alabama`, `Gulf of Mexico`). |
| **`ToState`** | `tostate` | `NVARCHAR(64)` | No | **PK.** a NAME. |
| **`FlowType`** | `flowtype` | `NVARCHAR(32)` | No | **PK.** |
| `Volume` | `volume` | `DECIMAL(18,6)` | Yes | interstate flow. |

**PK:** `(FlowDate, Region, FromState, ToState, FlowType)`.

## 19. SupplyAndDemand (markets history) — `GET cs/v1/pointlogic/supplyDemand/marketsHistory` → `arm.SupplyAndDemand`

Family P. **WRAPPER** (`PagingInfo`/`Data`), **total 61 rows**, no date param (full history). Same
**16 measures** as MarketBalances (§11), keyed by `date`.

| Field | JSON | SQL type | Null? |
|-------|------|----------|-------|
| **`Date`** | `date` | `DATE` | No **(PK)** |
| `Wellhead`, `ProductionLoss`, `DryGas`, `CanadaImports`, `LngSendout`, `TotalSupply`, `Power`, `Industrial`, `ResidentialCommercial`, `Subtotal`, `MexicoExports`, `LngFeedGas`, `PipeLoss`, `TotalDemand`, `Storage`, `BalancingItem` | (same lowercase names as §11) | `DECIMAL(18,6)` | Yes |

**PK:** `(Date)`.

---

# TIER 1 — Discovery-fed lookups (parent id in path; flat `{id,name}` array)

## 20. County — `GET cs/v1/pointlogic/lookup_county/{StateId}` → `arm.County`

`{id, name}` — **`StateId` is NOT echoed in the body → inject it from the path.** (67 rows for
`StateId=4520`.)

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`CountyId`** | `id` | `INT` | No | **PK part.** |
| **`StateId`** | *(path)* | `INT` | No | **PK part** — injected from the URL. |
| `Name` | `name` | `NVARCHAR(128)` | No | county name. |

**PK:** `(CountyId, StateId)`. **Iterate every `StateId` from §6-a.**

## 21. Facility — `GET cs/v1/pointlogic/lookup_facility/{PointTypeId}` → `arm.Facility`

`{id, name, facilitytypeid}` — **`facilitytypeid` arrives as a STRING (`"1"`) and equals the path
`PointTypeId`.** (658 rows for `PointTypeId=1`.)

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`FacilityId`** | `id` | `INT` | No | **PK part.** |
| **`PointTypeId`** | *(path)* | `INT` | No | **PK part** — injected from the URL (equals `facilitytypeid`). |
| `Name` | `name` | `NVARCHAR(200)` | No | facility name. |
| `FacilityTypeId` | `facilitytypeid` | `INT` | Yes | JSON **string** `"1"` → parse; redundant with the path id (persist for audit or drop — DB's call). |

**PK:** `(FacilityId, PointTypeId)`. **Iterate every `PointTypeId` from §6-c.**

## 22. Subregion — `GET cs/v1/pointlogic/lookup_subregion/{RegionId}` → `arm.Subregion`

`{id, name}` — **`RegionId` is NOT echoed in the body → inject it from the path.** (3 rows for
`RegionId=26105`, e.g. `26252` `"Lower Midcon"`.)

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`SubRegionId`** | `id` | `INT` | No | **PK part.** discovery id (feeds §24). |
| **`RegionId`** | *(path)* | `INT` | No | **PK part** — injected from the URL. |
| `Name` | `name` | `NVARCHAR(128)` | No | subregion name. |

**PK:** `(SubRegionId, RegionId)`. **Iterate every `RegionId` from §5.** This table is also the
**SubRegionId → RegionId map** the loader needs for §24.

---

# TIER 2 — Parametrized facts (WRAPPER; parent id + report date)

## 23. SupplyAndDemandByRegion — `GET cs/v1/pointlogic/supplyDemand/region/{RegionId}?reportDate=yyyy-MM-dd` → `arm.SupplyAndDemandByRegion`

**WRAPPER**; `Data` rows `{product, volume_mmcfd}` — **neither `RegionId` nor `Date` is in the
body**; inject `RegionId` (path) and `Date` (the `reportDate` param). ~20 products/region. Work
unit = `RegionId` (§5) × `reportDate` window.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`RegionId`** | *(path)* | `INT` | No | **PK** — injected from the URL. |
| **`Date`** | *(`reportDate` param)* | `DATE` | No | **PK** — injected from the query. |
| **`Product`** | `product` | `NVARCHAR(128)` | No | **PK.** supply/demand product. |
| `VolumeMmcfd` | `volume_mmcfd` | `DECIMAL(18,6)` | Yes | MMcf/d. |

**PK:** `(RegionId, Date, Product)`.

## 24. SupplyAndDemandBySubRegion — `GET cs/v1/pointlogic/supplyDemand/region/{SubRegionId}?reportDate=yyyy-MM-dd` → `arm.SupplyAndDemandBySubRegion`

**Reuses the `/region/{id}` path** with a **sub-region id** (a distinct `/subregion/` path 404s).
**WRAPPER**; `Data` rows `{product, volume_mmcfd}`. **`RegionId` is NOT in the body → resolve it
from `arm.Subregion` (§22, the SubRegionId→RegionId map) and inject it**, along with
`SubRegionId` (path) and `Date` (`reportDate`). ~32 products/subregion.

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`SubRegionId`** | *(path)* | `INT` | No | **PK** — injected from the URL. |
| **`RegionId`** | *(from `arm.Subregion`)* | `INT` | No | **PK** — resolved via the subregion→region map. |
| **`Date`** | *(`reportDate` param)* | `DATE` | No | **PK** — injected from the query. |
| **`Product`** | `product` | `NVARCHAR(128)` | No | **PK.** |
| `VolumeMmcfd` | `volume_mmcfd` | `DECIMAL(18,6)` | Yes | MMcf/d. |

**PK:** `(SubRegionId, RegionId, Date, Product)`.

## 25. PointVolume — `GET cs/v1/pointlogic/volumeHistory/point?pointIds={csv, ≤50}[&startDate=yyyy-MM-dd]` → `arm.PointVolume`

**WRAPPER**; `Data` rows `{id, volume, date}`. `pointIds` is a **comma-separated list** —
confirmed format `pointIds=689,690,691` — **batch ≤ 50 point ids per request** (`id` in each row =
the `PointId`, so a multi-point batch splits cleanly). Without a start date the call returns
**recent** history rows.

**Query params:**

| Param | Type | Required | Notes |
|-------|------|----------|-------|
| `pointIds` | CSV of `INT` | Yes | ≤ 50 point ids per request; `id` in each row echoes the `PointId`. |
| `startDate` | `date` (`yyyy-MM-dd`) | No | **Confirmed.** Filters history to rows with `date >= startDate`; controls how far back volume history is returned. Combines with `pointIds` and `pageIndex`. |

| Field | JSON | SQL type | Null? | Notes |
|-------|------|----------|-------|-------|
| **`PointId`** | `id` | `INT` | No | **PK.** |
| **`Date`** | `date` | `DATE` | No | **PK.** gas day. |
| `Volume` | `volume` | `DECIMAL(18,6)` | Yes | can be **negative**. |

**PK:** `(PointId, Date)`.

---

## Discovery id inventory (the ids that feed the child tiers)

| Parent lookup (§) | Id field (JSON) | Feeds |
|-------------------|-----------------|-------|
| Region (§5) | `id` (`RegionId`) | `lookup_subregion/{RegionId}` (§22); `supplyDemand/region/{RegionId}` (§23) |
| State (§6-a) | `id` (`StateId`) | `lookup_county/{StateId}` (§20) |
| PointType (§6-c) | `id` (`PointTypeId`) | `lookup_facility/{PointTypeId}` (§21) |
| Point (§7 / §12) | `id`/`pointid` (`PointId`) | `volumeHistory/point?pointIds=…` (§25), batched ≤ 50 |
| Subregion (§22) | `id` (`SubRegionId`) + path `RegionId` | `supplyDemand/region/{SubRegionId}` (§24); RegionId supplies §24's PK |

---

## Coverage checklist (25 endpoints)

| # | Endpoint | Family | Path | Envelope | PK | Fields |
|---|----------|:------:|------|:--------:|----|:------:|
| 5 | Region | P | `lookup_region` | array | `RegionId` | 2 ✅ |
| 6-a | State | P | `lookup_state` | array | `StateId` | 2 ✅ |
| 6-b | PointStatus | P | `lookup_pointStatus` | array | `PointStatusId` | 2 ✅ |
| 6-c | PointType | P | `lookup_pointType` | array | `PointTypeId` | 2 ✅ |
| 6-d | PipelineNoticeCategory | P | `lookup_pipelineNoticeCategory` | array | `PipelineNoticeCategoryId` | 2 ✅ |
| 6-e | Pipeline | P | `lookup_pipeline` | array | `PipelineId` | 3 ✅ |
| 7 | Point | P | `lookup_point` | wrapper (3 pg) | `PointId` | 5 ✅ |
| 8 | DemandForecastRegion | V | `demandforecast_region` | array | `(ForecastDate*,Date,Region,Subregion)` | 11 ✅ (*stamped) |
| 9 | DemandForecastUSLower48 | V | `demandforecast_uslower48` | array | `(ForecastDate*,Date,Region)` | 7 ✅ (*stamped) |
| 10 | GasProduction_ProducingArea | V | `gasproduction_producingarea` | array | `(ReportedDate,ReferenceDate,Region,ProducingArea,State)` | 7 ✅ |
| 11 | MarketBalancesUSLower48 | V | `marketbalances_uslower48` | array | `(TimePeriod)` | 17 ✅ |
| 12 | PointMetadata | V | `pointmetadata_withids` | array (≥5 pg) | `PointId` | 22 ✅ |
| 13 | ModeledDemandRegionType | V | `modeleddemand_region_type` | array | `(ReferenceDate,PLEProductName,RegionName)` | 4 ✅ |
| 14 | PipelineFlowThroughput | V | `pipelineflow_throughputs` | array | `(FlowDate,Region,Pipeline,Throughput,FlowType)` | 6 ✅ (+reporteddate excluded) |
| 15 | PipelineNoticeSearch | V | `pipelinenotice_search` | array (paged) | `(Id)` | 11 ✅ |
| 16 | USImportsExportsByPointsAggregate | V | `us_importsexportsby_points_aggregate` | array | `(RunDate,FlowDate,PointName,PipelineName,LedgerSide)` | 11 ✅ |
| 17 | US_SampleStorage_Facility | V | `us_samplestorage_facility` | array (2 pg) | `(ReportDate,FlowDate,Name,EiaRegion,State,FieldType)` | 7 ✅ |
| 18 | StateFlowsThroughputAggregate | V | `stateflows_throughputaggregates` | array | `(FlowDate,Region,FromState,ToState,FlowType)` | 6 ✅ |
| 19 | SupplyAndDemand | P | `supplyDemand/marketsHistory` | wrapper | `(Date)` | 17 ✅ |
| 20 | County | P | `lookup_county/{StateId}` | array | `(CountyId,StateId)` | 3 ✅ (StateId injected) |
| 21 | Facility | P | `lookup_facility/{PointTypeId}` | array | `(FacilityId,PointTypeId)` | 4 ✅ (PointTypeId injected) |
| 22 | Subregion | P | `lookup_subregion/{RegionId}` | array | `(SubRegionId,RegionId)` | 3 ✅ (RegionId injected) |
| 23 | SupplyAndDemandByRegion | P | `supplyDemand/region/{RegionId}?reportDate=` | wrapper | `(RegionId,Date,Product)` | 4 ✅ (RegionId+Date injected) |
| 24 | SupplyAndDemandBySubRegion | P | `supplyDemand/region/{SubRegionId}?reportDate=` | wrapper | `(SubRegionId,RegionId,Date,Product)` | 5 ✅ (SubRegionId+RegionId+Date injected) |
| 25 | PointVolume | P | `volumeHistory/point?pointIds={≤50}` | wrapper | `(PointId,Date)` | 3 ✅ |

**Gate status: VERIFIED / PASS.** All 25 endpoints have a complete, live-observed field list with
per-field SQL type, nullability, source casing, and a confirmed PK. Injected key columns
(`ForecastDate` stamped; `StateId`/`PointTypeId`/`RegionId`/`Date` from path/param; `RegionId` from
the subregion map) are called out for DATABASE_DEVELOPER. `pipelineflow_throughputs.reporteddate`
is documented and **excluded**.

## Remaining minor open items (non-blocking)

1. **Max `page_size`:** observed fixed at **10 000/page**; whether it is client-adjustable is
   unconfirmed (the loader pages via `pageIndex` regardless, so this is cosmetic).
2. **`volumeHistory/point` date-range:** RESOLVED — `startDate` (`yyyy-MM-dd`) is confirmed and
   documented on §25; it filters history to `date >= startDate`. (`endDate` remains unconfirmed and
   is not currently needed.)
3. **Rate limits / 429:** none surfaced during the probe — pace conservatively and back off on
   `429` if it ever appears.
4. **`gasproduction_producingarea.reporteddate` key granularity:** it carries `HH:mm`; modeled as
   `DATETIME2(0)` and kept at datetime granularity (matches the user PK naming `ReportedDate`).
   DATABASE_DEVELOPER to confirm datetime-vs-date keying is acceptable.
5. **`facilitytypeid` on §21:** redundant with the path `PointTypeId` — persist for audit or drop
   (DB's call).
