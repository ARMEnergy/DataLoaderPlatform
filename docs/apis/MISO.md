# MISO Data Exchange API — field reference (Load, Generation & Interchange)

Live-verified 2026-11-24 against `https://apim.misoenergy.org/lgi/v1` with ARM's subscription key.
Every fact marked **[LIVE]** was observed on that date; fixtures are in
`tests/DataLoader.MISO.Tests/Fixtures/live/`.

## 1. Portal, products, auth

- Developer portal: `https://data-exchange.misoenergy.org` (Azure API Management). The portal's own
  JSON backs the catalogue and needs no key:
  - `GET /developer/apis?api-version=2022-04-01-preview` — two APIs: `load-generation-and-interchange-api`
    (gateway path `lgi`) and `pricing-api` (gateway path `pricing`).
  - `GET /developer/apis/{api}/operations?api-version=2022-04-01-preview&$top=200` — 22 LGI and 10 Pricing operations.
  - `GET /developer/apis/{api}/operations/{operationId}?api-version=2022-04-01-preview` — parameters and enums.
  - The OpenAPI export (`?export=true&format=openapi%2Bjson`) returns **empty `paths`**; use the
    operations endpoints above instead.
- Gateway: `https://apim.misoenergy.org/{lgi|pricing}/v1/...`.
- Auth: header `Ocp-Apim-Subscription-Key: <key>`. **[LIVE]**
- 🔑 **Each API is a separate APIM product with its own subscription.** ARM's key is subscribed to
  **LGI only**: every Pricing call answers **401** `Access denied due to invalid subscription key`
  **[LIVE]**. LMP (`miso_lmp_day_ahead_hourly`) is deferred until a Pricing subscription exists
  (the portal says that product needs no approval).
- The key is stored in `core.Param` (`LoaderName='MISO'`, `ParamName='ApiKey'`), never in the repo.

## 2. Envelope and paging [LIVE]

```json
{ "data": [ ...rows... ],
  "page": { "pageNumber": 1, "pageSize": 1000, "totalElements": 144, "totalPages": 1, "lastPage": true } }
```

- `pageSize` is fixed at **1000**. A `pageSize` query parameter is **silently ignored**.
- Paging is `pageNumber=N` (1-based). Requesting a page **past the end** answers
  **HTTP 404 `{"status":404,"reason":"Empty data returned for date"}`**, the same body as an empty day.
  Stop on `lastPage == true`; never probe past it.
- Every feed in scope fits on one page per day (72–240 rows). Reconcile rows read against `totalElements` anyway.

## 3. Time

- `timeInterval.start` / `.end` are **wall-clock Eastern STANDARD time (fixed UTC−05:00, no DST)**,
  ISO without an offset, e.g. `"2026-11-20T00:00:00"`. `timeInterval.value` is hour-ending `"1".."24"`.
- Every market day has **exactly 24 hourly intervals**, including the DST days 2026-03-08 and 2026-11-01 **[LIVE]**.
- GridStatusIO's `interval_start_local` for MISO is the same fixed EST clock: `local = utc − 5h` in
  both summer and winter (verified on dbo rows in July and November).
- So `interval_start_utc = start + 5h`, and no key ever repeats.

## 4. Status matrix [LIVE]

All refusals are JSON `{"status":<code>,"reason":"<text>"}`; **400 covers several distinct cases,
told apart only by `reason`**.

| HTTP | `reason` (prefix) | Meaning | Loader action |
|---|---|---|---|
| 200 | — | data | load |
| 400 | `Invalid date: data not available yet for this market date` (+ `Retry-After` header) | not published yet (today, future) | **not-yet**: 0 rows, unit stays hot |
| 400 | `Init date must be before today` | MTLF init = today | **not-yet** |
| 400 | `Invalid date: market date beyond the allowed lookback period` | older than the rolling lookback | **out of retention**: 0 rows, warn, unit completes |
| 400 | `Invalid date: expected format: YYYY-mm-dd` | malformed date | **fail unit**, no retry |
| 400 | `must be one of the acceptableValues` | bad enum (e.g. `geoResolution`) | **fail unit**, no retry |
| 400 | `Forecast date must be within 7 days of the init date` / `... after init date` | bad MTLF pair | **fail unit**, no retry |
| 400 | anything else | unknown | **fail unit**, no retry |
| 401 | `Access denied due to invalid subscription key...` (APIM shape `{"statusCode":401,"message":...}`) | bad key / not subscribed | **stop the run**, no retry |
| 404 | `Empty data returned for date` | page 1: empty day; page > 1: past the end | page 1: empty read (see design §5); page > 1: fail |
| 429, 408, 5xx | — | transient | retry with backoff, honouring `Retry-After` |

- The rolling lookback is **~1,100 days**: on 2026-11-24 the earliest accepted market date was
  **2023-11-20** for actual load, fuel type and MTLF alike (binary search) **[LIVE]**.
- 🔑 **Quota: 100 calls per 60 s per subscription key** **[LIVE 2026-11-24]**. A burst of distinct
  dates at concurrency 4 got 99 × 200 then **HTTP 429**
  `{ "statusCode": 429, "message": "Rate limit is exceeded. Try again in 36 seconds." }` with
  `Retry-After: 36`, 36 s being exactly 60 s after the window's first call. No `X-RateLimit-*`
  headers are sent, so the count is invisible until you hit it. An earlier 40-concurrent test stayed
  under 100 and wrongly suggested there was no limit. The first real run (2026-11-24,
  `RequestsPerMinute=300`) loaded 400 units in 4 min, i.e. it crawled at exactly the quota with most
  calls drawing a 429 first. The loader now limits itself to 90/min (design §7). The quota is per
  KEY: a dev run beside the scheduled one shares it.
- Publication (per vendor descriptions): actual load at **2am EST** the day after; fuel type and
  MTLF at **7am EST** the day after. The `Retry-After` on a not-yet 400 matched this
  (`07:00 GMT` = 02:00 EST for actual load, `12:00 GMT` = 07:00 EST for fuel type).

## 5. Operations in scope

### 5.1 Actual load — `GET /real-time/{date}/demand/actual`

Query: `geoResolution` = `region` (default) | `localResourceZone`; `timeResolution` = `hourly` (default) | `daily`.

`geoResolution=localResourceZone` → 144 rows/day:

```json
{"timeInterval":{"resolution":"hourly","start":"2026-11-20T00:00:00","end":"2026-11-20T01:00:00","value":"1"},
 "localResourceZone":"LRZ1","load":10631.6}
```
`localResourceZone` ∈ `LRZ1, LRZ2_7, LRZ3_5, LRZ4, LRZ6, LRZ8_9_10` (6, every hour).

Default (`region`) → 72 rows/day: `"region":"Central"|"North"|"South"` (**title case** here), `"load"`.

✅ **Matches GridStatusIO `miso_zonal_load_hourly` exactly**: 2026-11-18 HE1–HE3, all six LRZ values
identical, and GridStatusIO's `miso` column equals the sum of the six (68,923.0) **[LIVE]**. The
region sum equals the LRZ sum for every hour checked.

❌ Does **not** match GridStatusIO `miso_load` (5-minute real-time load): 2026-11-18 00:00 EST hour,
dbo average 71,305.3 vs API 68,923.0.

### 5.2 Real-time generation fuel type — `GET /real-time/{date}/generation/fuel-type`

72 rows/day (3 regions × 24):
```json
{"timeInterval":{...},"region":"CENTRAL",
 "fuelTypes":{"coal":9548.1,"gas":10531.4,"nuclear":4073.3,"water":401.3,"wind":4750.9,"solar":2.9,"other":183.9,"storage":38.6},
 "totalMw":29530.4,"interval":"1"}
```
- `region` is **UPPER case** here (`CENTRAL|NORTH|SOUTH`), unlike actual load.
- `region=MISO` answers **404 Empty data** — there is no MISO-total row; totals are sums.
- Fuel keys: `coal, gas, nuclear, water, wind, solar, other, storage` (`storage` includes pumped
  storage, ESR and batteries; `solar` from 2022-01-01).

❌ Does **not** match GridStatusIO `miso_fuel_mix` (5-minute real-time fuel mix, other categories,
has `imports`, no `water`): 2026-11-18 00:00 EST hour, coal 16,930 (dbo) vs 13,902 (API), gas
21,458 vs 24,668.

### 5.3 Medium-term load forecast — `GET /forecast/{date}/load`

Query: `init` = forecast-run (creation) date. Rules **[LIVE]**: `init < date ≤ init + 7`;
`init < today`; default `init = date − 1`. Old vintages stay available inside the lookback.

240 rows/day (10 zones × 24):
```json
{"timeInterval":{...},"region":"CENTRAL","localResourceZone":"Z4","loadForecast":6092,"init":"2026-11-19"}
```
`localResourceZone` ∈ `Z1..Z10`; `region` ∈ `NORTH (Z1, Z3) | CENTRAL (Z2, Z4..Z7) | SOUTH (Z8..Z10)`
**[LIVE]**, consistent on every row of 2026-11-01 and 2026-11-18. The non-contiguous split matches
the actual-load LRZ groups `LRZ2_7` / `LRZ3_5`.

❌ Does **not** match GridStatusIO `miso_load_forecast` (a 5-minute interpolated series): vintage
2026-11-17 for 2026-11-18 00:00 EST, dbo 71,178 vs API 68,845.

### 5.4 Not available from either API
`miso_solar_forecast_hourly`, `miso_wind_forecast_hourly`: no operation publishes them.

## 6. Other LGI operations (not loaded)

`day-ahead/{date}/demand`, `.../generation/cleared/{physical|virtual}`, `.../generation/fuel-type`,
`.../interchange/net-scheduled`, `.../generation/offered/{ecomax|ecomin}`,
`historical/{date}/interchange/net-scheduled` (revised up to 105 days), `forecast/{date}/outage`
(no lookback), `real-time/{date}/binding-constraint`, `.../demand/forecast`,
`.../generation/cleared/supply` (5min|hourly), `.../generation/committed/ecomax`,
`.../generation/fuel-on-the-margin` (5-min), `.../interchange/net-actual` (by adjacent BA, 9/hour),
`.../interchange/net-scheduled`, `.../generation/offered/ecomax`, `.../outage`,
`.../demand/load-state-estimator` (by zone group, 7/hour).

## 7. Pricing API (deferred — no subscription)

`aggregated-pnode` (node → type), `day-ahead/{date}/lmp-{exante|expost}`,
`real-time/{date}/lmp-{exante|expost}`, and the ASM MCP equivalents. GridStatusIO's
`miso_lmp_day_ahead_hourly` holds 424 fixed locations per hour (all labelled `Hub` by the legacy
loader) with `lmp, energy, congestion, loss`. The 5-minute RT endpoints had their interval labels
shifted by 5 minutes on 2026-05-06, including history (vendor note).
