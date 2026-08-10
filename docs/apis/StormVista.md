# StormVista loader — API field reference

Weighted Degree Day (WDD) forecast data from StormVista Wx Models. Enumerations here
are taken from the **authoritative OpenAPI spec** (`stormvista.yaml`, obtained from the
authenticated docs at `www.stormvistawxmodels.com/api` → `/files/api-docs/stormvista.yaml`)
and **cross-checked against live `200 text/csv` responses**. The full API is large
(244 paths); this loader's scope is the **two** WDD endpoints below. Note the spec also
exposes `-raw` / `-bc` / `_members` / `_states` / `realtime-` / `history/` variants and a
separate `{model}` regional template — all **out of scope** (only the two endpoints the
user linked are loaded: daily grid-corrected `{type}-daily.csv` and the weekly/regional
grid-corrected `{wkmodel}` `{type}_reg{reg}.csv`).

## API

- **Base URL:** `https://api.stormvistawxmodels.com/v1`
  - Note: this is the dedicated `api.` subdomain with a `/v1` prefix — NOT the
    `www.stormvistawxmodels.com/api` docs site.
- **Auth:** API key as a query-string parameter `?apikey=<key>` on every request.
  No header / Basic / cookie form works. Key is stored in config as
  `Loaders:StormVista:ApiKey` (env `DATALOADER_Loaders__StormVista__ApiKey`).
  **Never hard-code the key or place it in this file.**
- **Invalid combinations** (bad model/type/region/cycle, or a date/cycle not yet
  published) return **HTTP 404 with an HTML body**, not an empty CSV. The loader
  must treat 404 as "not available for this combination" → skip, not hard-fail.
- **History:** earliest archive is **2018-07-08** (per the spec's Archive Notes; each
  file is present from when it entered operations, so AI/MLR models start much later
  than 2018). "Best-effort" archive — some files may be missing due to modeling-center
  issues. Current-day init dates are live. `429` is returned when a rate limit is
  exceeded (with a header naming the violated limit).

## Path parameters

`date` = `YYYYMMDD` (model init date, UTC), e.g. `20240804`. The remaining parameters
(`model`/`wkmodel`, `cycle`, `type`, `reg`) **differ between the two endpoints** — see
each endpoint below. `type` slug meanings: `ew_cdd` = energy-weighted CDD, `gw_hdd` =
gas-weighted HDD, `pw_cdd` = population-weighted CDD, `pw_hdd` = population-weighted HDD.

## Endpoint 1 — Daily (national), grid-corrected

```
GET /model-data/{model}/{date}/{cycle}z/wdd/{type}-daily.csv?apikey=<key>
```
Example: `/model-data/gfs/20240804/00z/wdd/ew_cdd-daily.csv`

Parameters (from spec, all live-verified):
- `model` — **17 values**: `gfs`, `gfs-ens`, `ecmwf`, `ecmwf-eps`, `gfs-ens-bc`, `cmc-ens`,
  `mlr15`, `mlr30`, `mlr45`, `ai-fourcastnetv2-gfs-ens`, `ai-fourcastnetv2-ecmwf-eps`,
  `ai-graphcast-gdas-ecmwf-eps`, `aifs`, `aifs-ens`, `ai-gfs`, `ai-gfs-ens`, `ai-weathernext2`.
  (`mlr*` publish only for `00`/`12`; AI models are recent — sparse history. Absent
  combos 404 → skip.)
- `cycle` — `00`, `06`, `12`, `18`.
- `type` — `ew_cdd`, `gw_hdd`, `pw_cdd` (3; **no `pw_hdd`** for daily).

CSV columns:

| Column | Type | Notes |
|--------|------|-------|
| `Date` | date (`YYYY-MM-DD`) | forecast valid day |
| `Value` | decimal | national WDD value |
| `Flag (0=obs 1=fcst 2=norm)` | int | 0 = observed/actual, 1 = forecast, 2 = climate-normal |

~30 rows/file, centered on the init date: ~7 observed days (flag 0) before/at init,
~15 forecast days (flag 1), ~7 climate-normal days (flag 2). Example (gfs/20240804/00z):

```
Date,Value,"Flag (0=obs 1=fcst 2=norm)"
2024-07-28,12.105,0
...
2024-08-04,<fcst>,1
...
2024-08-25,11.488,2
```

Natural key: `model + date + cycle + type + ValidDate`.

## Endpoint 2 — Regional / weekly (EIA & ISO), grid-corrected

```
GET /model-data/{wkmodel}/{date}/{cycle}z/wdd/{type}_reg{reg}.csv?apikey=<key>
```
Example: `/model-data/ecmwf-weekly/20240804/00z/wdd/ew_cdd_reg3.csv`

The URL path shape is the same whether the model slug is a weekly (`{wkmodel}`) or a
daily model: `/model-data/{model}/{date}/{cycle}z/wdd/{type}_reg{reg}.csv`.

Parameters (from spec + live verification):
- **model — 23 values** (scope-expanded 2026-08-06 to load ALL, not just the weekly ones):
  - **6 weekly models:** `cfs-weekly`, `gfs-ens-weekly`, `ecmwf-weekly`,
    `ai-fourcastnetv2-gfs-ens-weekly`, `ai-fourcastnetv2-ecmwf-eps-weekly`,
    `ai-graphcast-gdas-ecmwf-eps-weekly`.
  - **the 17 daily models too** — every daily model (§Endpoint 1) also serves regional
    WDD, verified live (e.g. `gfs-ens-bc` returns valid reg3/5/9/iso CSVs), and with
    **deeper history** than the weekly models (daily models have 2023 regional data;
    `ecmwf-weekly` 404s that far back). So the daily and regional model sets **overlap**.
- `cycle` — **model-kind-dependent**: daily-type models serve regional at **all 4 cycles**
  `00/06/12/18` (verified — `gfs-ens-bc` reg3 works at every cycle); weekly-only models at
  **`00/12`** (06z/18z → 404; some weekly models are effectively 00z-only — handled by skip).
  The loader enumerates `model.SupportsDaily ? {00,06,12,18} : {00,12}`.
- `reg` — `3`, `5`, `9` (EIA) and `iso` (ISO/RTO). Path token is literally `reg{reg}`
  → `reg3`, `reg5`, `reg9`, `regiso`.
- `type` — **applicability depends on `reg`**:
  - EIA (`reg3`/`reg5`/`reg9`): `ew_cdd`, `gw_hdd`, `pw_cdd` (3). `pw_hdd` → 404.
  - ISO (`regiso`): `pw_cdd`, `pw_hdd` (2). `ew_cdd`/`gw_hdd` → 404.

**Wide** CSV: `Date` + one value column per region. **Forecast-only** (starts at the
init date, no observed/normal rows, no flag column). Region columns per set:

| `reg{reg}` | Regions (column headers, in order) | # |
|----------|-------------------------------------|---|
| `reg3` | `West`, `East`, `Producing` (gas-market regions) | 3 |
| `reg5` | `Mountain`, `East`, `Midwest`, `South Central`, `Pacific` (EIA-5) | 5 |
| `reg9` | `Mountain`, `W S Central`, `South Atlantic`, `W N Central`, `New England`, `Pacific`, `Middle Atlantic`, `E N Central`, `E S Central` (census divisions) | 9 |
| `regiso` | `bpa`, `miso`, `nyiso`, `west-pjm`, `ercot`, `spp`, `caiso`, `aeso`, `ieso`, `upmiso`, `nepool`, `wecc`, `serc`, `pjm`, `east-pjm`, `tva`, `quebec`, `lowmiso`, `southwest`, `caisonorth`, `caisosouth` (ISO/RTO) | up to 21 |

Region names are **scoped to their set** (`East` in `reg3` ≠ `East` in `reg5`; ISO uses
lowercase ISO/RTO codes). On load this file must be **unpivoted** to long rows: one
(ValidDate, region, value) per region column.

> **Region sets grow over time.** StormVista added `southwest`, `caisonorth`,
> `caisosouth` to the ISO set between 2024 and 2026, so the ISO set was **18 regions**
> for 2023–2024 init dates and **21** by 2026. Historical files legitimately carry a
> **subset** of the latest set. Seed the latest/full set; the loader's regional header
> validation **tolerates missing** seeded regions (maps the columns that are present)
> and fails only on an **unknown/extra** column (a new region → reseed `dbo.Region`) or
> a duplicate. Older files are NOT an error.

Example (ecmwf-weekly/20240804/00z, reg3):
```
Date,West,East,Producing
2024-08-04,12.96,12.81,18.92
...
```

Natural key (after unpivot): `wkmodel + date + cycle + type + reg + region + ValidDate`.

## Load scope (agreed 2026-08-05; regional expanded to daily models 2026-08-06)

- Load **both endpoints**, **all** valid combinations, incl. the experimental AI/MLR
  models (user-confirmed "everything"). The loader enumerates only spec-valid combos to
  avoid guaranteed 404s; unpublished dates still 404 → skip.
- **Full available matrix per init date:**
  - Daily national: 17 models × 4 cycles × 3 types = **204** (upper bound; `mlr*` only
    00/12, AI models sparse history → many skip).
  - Regional: **23** models serve regional (17 daily-type + 6 weekly), with
    per-region-set type applicability `[EIA 3 sets × 3 types = 9] + [ISO 1 × 2 types = 2] = 11`:
    - daily-type (17) at **4 cycles**: 17 × 4 × 11 = **748**
    - weekly-only (6) at **2 cycles** (00/12): 6 × 2 × 11 = **132**
  - ≈ **1,084 files/init date** (upper bound; many AI/MLR/older combos 404 → skip).
- **Initial backfill:** full history (earliest 2018-07-08 → today); daily models carry
  deeper regional history than the weekly models.
- **Ongoing:** re-pull a rolling recent window to catch new cycles (idempotent MERGE).

## Delivery, caching & rate limits (from live response headers)

- Files are **static objects on Amazon S3 behind CloudFront** (`server: AmazonS3`,
  `Via: … cloudfront`). Data is pre-generated, not computed per request → each
  `(model, date, cycle, type[, reg])` file is an **immutable snapshot** once published.
- **No rate-limit headers** are returned (`X-RateLimit-*`, `Retry-After` all absent),
  but the docs publish explicit limits:
  | Type | Limit | Period |
  |------|-------|--------|
  | Total requests | 3000 | minute |
  | Non-'realtime' requests | 2000 | minute |
  | Hourly City Extraction | 5000 | day (N/A — not used) |
  | Same URL | 1 | second |
  | On-demand | 1 | 5 seconds |
  | User data transfer | 1 TB | month |
  | Company data transfer | 6 TB | month |
  - **Backfill governor:** historical pulls are "non-realtime" → keep under **2000 req/min**
    (≈33/s); overall stay under 3000/min. "Same URL 1/s" is not a constraint (we request
    distinct URLs). Data transfer is negligible (~600 B/file; full history ≈ 0.5 GB).
    Pacing is **configurable** (requests/min throttle + `MaxConcurrentWorkUnits`); default
    conservatively below the non-realtime cap.
- **`Last-Modified` and `ETag` are present** on every response (e.g. the
  `gfs/20240804/00z/ew_cdd-daily` file → `Last-Modified: 2024-08-04 05:12 UTC`,
  ~5h after the 00z cycle). So publication lag is a few hours (same-day), and a
  reliable change signal IS available (conditional GET / `If-Modified-Since` /
  `If-None-Match`) if the resume design wants it — though the two-zone key
  (settled-skip + hot re-pull, see design doc) avoids re-fetching settled files
  entirely and is the primary optimization.

## Recommended SQL types

- `Value` → `DECIMAL(9,4)` (samples show up to 3 decimals; headroom for precision/negatives).
- `Flag` → `TINYINT` (0/1/2) with a lookup or check constraint (daily only).
- `Date`/`ValidDate` → `DATE`.
- `model`/`wkmodel` `VARCHAR(40)` (longest slug `ai-fourcastnetv2-ecmwf-eps-weekly` = 33 chars;
  `ai-graphcast-gdas-ecmwf-eps-weekly` = 34), `type` `VARCHAR(10)`, `cycle` `CHAR(2)`,
  region name `VARCHAR(30)`, **region-set code `VARCHAR(4)`** (`'3'`,`'5'`,`'9'`,`'iso'` —
  NOT a TINYINT, because of `iso`). Derive `InitDatetimeUtc DATETIME2(0)` from date+cycle if useful.
- Type×region-set applicability is a fact worth a **bridge/reference table** (EIA sets →
  {ew_cdd,gw_hdd,pw_cdd}; ISO → {pw_cdd,pw_hdd}), and cycles differ by feed (daily 4;
  regional {00,12}) — model these in reference tables so enumeration stays data-driven.
