# Energy Aspects loader

## API
- Reference: https://developer.energyaspects.com/reference/overview
- Auth: API key. Key is stored in config as ENERGY_ASPECTS_API_KEY
  (do NOT hard-code it). Never place the key in this file.

## Discovery step (run first)
- Dataset mappings: https://developer.energyaspects.com/reference/dataset_mappings_get
- Returns a list of mappings; each has a name and a list of dataset IDs.
- Each record has a `request_string` showing an example GET query.
- Use the dataset IDs to call the individual dataset endpoints.

## Datasets required (current scope)
- Canada oil production
- Canada balances
- Mexican production
- China crude data
- India demand, import, crude, production, runs
- US crude balances
- Cushing crude microbalance
- US crude production & upstream activity
- Canada gas balances
- North America basis price forecasts
- Monthly product inventories
- Europe diesel balances
- Europe gasoline balances
- Europe jet balances
- US gasoline balances
- US diesel balances
- US weekly product stock forecast
- China oil products data
- Middle distillate price forecasts
- Fuel oil price forecasts
- Light ends price forecasts
- Dispatch costs by ISO
- US installed capacity by ISO and fuel type
- Monthly load by ISO
## Request-URI limit on `/data/timeseries/` (measured 2026-10-13)

The timeseries endpoint takes its dataset ids as a comma-separated `dataset_id`
query parameter, and two licensed mappings carry far more ids than fit in one
request:

| Mapping | Name | Dataset ids | Ids rendered |
|---|---|---|---|
| 375 | OilX supplydemand | 7,092 | 49,643 chars |
| 371 | OilX terminalsweekly | 2,496 | 17,471 chars |

Both returned **`414 Request-URI Too Large`** on every window of every run — 8
failures a day in `core.LoadLog`, unbroken since at least 2026-10-04. No other
licensed mapping is affected; the next largest (515, 1,470 ids / 10,289 chars)
answers 200.

**The limit is 16,384 characters** on path + query. Bracketed against the live
API: 16,312 → `200`, 16,592 → `414`.

**`POST` is not an alternative.** `/data/timeseries/` answers **`405 Method Not
Allowed`** for both `application/x-www-form-urlencoded` and `application/json`
bodies, so the id list has to travel in the URL and the only fix is to send
fewer ids per request.

**Latency tracks id count, not window width.** 1,000 ids over 30 days returns in
~12s; 2,310 ids over 2 days takes ~34s. So a budget sized to keep the URI short
also keeps a request inside `HttpTimeoutSeconds` — and conversely, chunking
right up against the 16,384 limit would trade the 414 for a client timeout,
which the shared Polly policy does **not** retry (`HandleTransientHttpError`
covers 5xx/408/`HttpRequestException`, not the `TaskCanceledException` an
`HttpClient.Timeout` expiry throws).

The loader therefore splits a mapping's id list on **measured URI length**
(`Loaders:EnergyAspects:MaxRequestUriLength`, default 8,000 — see
`EnergyAspectsRequest`). At that budget only 3 of the 207 licensed mappings
split (375 → 7 parts, 371 → 3, 515 → 2) and requests per window go 207 → 216.

Live-verified 2026-10-13 with the shipping chunker, window 2026-09-11–2026-10-10:
all 7 parts of mapping 375 and all 3 of mapping 371 returned **200** (8 MB of
data that had never loaded), every part's URI 7,995 characters or less, every id
covered exactly once and in order, slowest part 17.4s.

Note the three unlicensed OilX mappings are larger still — 377 `OilX flows` has
**118,889** ids (832,222 chars) and 376 has 58,976 — but `usp_GetActiveMappings`
filters on `Licensed = 'yes'`, so they are never requested. If any of them is
ever licensed, it will split into roughly 106 and 53 parts respectively, and the
per-window request count should be re-checked against the vendor's rate limits
before enabling it.
