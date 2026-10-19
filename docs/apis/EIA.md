# EIA (U.S. Energy Information Administration, API v2) — source field reference

Vendor documentation: `docs/Dev Documentation/EIAv2.pdf` (API v2.0.1, February 2022).
The live API reports `apiVersion: "2.1.14"`, and several behaviours below are **not** in
that PDF — they were measured.

Loader: `src/DataLoader.EIA/` · Design of record: `docs/design/EIA.md` ·
SQL: `sql/EIA/` · Database `EIAv2`, schema `arm`.

---

## Gate status: **READ PATH FULLY VERIFIED AGAINST THE LIVE API** ✅ · SQL never deployed ⛔

Verified 2026-10-16 against `https://api.eia.gov/v2`, across **all eight frequencies** and
eleven series — **10,002 real rows**, zero API failures, zero dropped rows, zero colliding
primary keys. Every produced row was then compared against the incumbent
`EIAv2.dbo.SeriesData` (~94M rows, written by Conduit):

| Check | Result |
|---|---|
| `FacetKey` reproduced exactly | **8,568 / 8,568 — zero misses** |
| Rows whose period is absent from `dbo` | 1,434 — all `local-hourly`, a frequency the incumbent never loaded |
| Multi-page read (`steo`, total 5,190) | 2 pages, 5,190 rows, **exact** against the API's own total, 0 duplicates, 0 gaps |
| `DataJSON` | matches `dbo` exactly for every series in whichever mode Conduit wrote it — see §8 |

`sql/EIA/*.sql` parse clean under ScriptDom (TSql160) but **have never been run**.
`arm.SeriesData` does not exist on `ARMH-OPSDB01` yet.

---

## ⚠⚠ THE SIX THINGS THAT WILL BITE YOU

1. **`local-hourly` start/end need a `±HH` suffix or the API answers HTTP 500** — not 400.
   A 500 reads as transient, so a retry policy hammers it and the window never loads. §5
2. **An empty page still carries the `"incomplete return"` warning.** The warning is not a
   "there is more" signal; the stop condition is an EMPTY PAGE. §7
3. **404 has an EMPTY BODY.** No JSON, no message. For this loader it means the catalog
   lists a route the API no longer serves. §6
4. **`response.total` is a STRING** (`"total":"143"`), and `warnings` sits at the ROOT,
   *outside* `response`. §6
5. **The API changed its JSON value types from numbers to strings between 2022 and 2024.**
   Because the stored key is a hash of the JSON text, that silently **forked the incumbent
   table into duplicate rows**. §8
6. **The API sends `".43"`** — a decimal with no integer part, which is legal almost
   everywhere except in JSON. §8.2

---

## 1. Connection

| | |
|---|---|
| Base URL | `https://api.eia.gov/v2/` |
| Auth | 🔒 `api_key=` **in the query string**. There is no header form. |
| Gateway | `api-umbrella` (api.data.gov) — rate-limits **per key** |
| Protocol | HTTPS only |

🔒 Because the key is a query parameter, one logged URL leaks the organisation's
credential. The loader never logs a URI, the named `HttpClient` calls
`RemoveAllLoggers()`, and anything derived from an exception goes through
`EiaHttp.Redact`. Same posture as OilX and EnergyAspects.

Unlike AGSI, **EIA does not echo the key back** in an error body — a bad key returns
`{"error":{"code":"API_KEY_INVALID","message":"An invalid api_key was supplied. Get one at
https://api.eia.gov:443"}}`, with no key in it (verified 2026-10-16).

**Rate limits.** The PDF says only that programs "must throttle" and that exceeding the
tolerance **temporarily suspends the key**. No `X-RateLimit-*` headers are returned. The
penalty lands on the *key*, not the request, so an unthrottled backfill would stop every
other consumer of that key. The loader ships a shared, account-wide
`RequestsPerSecond = 5` gate.

---

## 2. The two routes this loader uses

### 2.1 Metadata — `GET /v2/{SeriesId}`  (no `/data`)

```
https://api.eia.gov/v2/coal/market-sales-price?api_key=…
```
```json
{"response":{"id":"market-sales-price","name":"…","frequency":[…],"facets":[…],
             "data":{…},"startPeriod":"2001","endPeriod":"2024",
             "defaultDateFormat":"YYYY","defaultFrequency":"annual"}}
```

This is the **authority for `endPeriod`**, and the loader calls it once per series per run
for exactly that. It matters for forecast series: `steo`'s newest quarterly period is
`2027-Q4`, so an upper bound of "today" would silently drop six quarters of published
forecast.

### 2.2 Data — `GET /v2/{SeriesId}/data/`

```
https://api.eia.gov/v2/coal/market-sales-price/data/
  ?frequency=annual&data[0]=price&data[1]=sales&start=2020&end=2026&api_key=…
```

`SeriesId` is a **route, not a value** — ids such as `natural-gas/cons/heat` carry path
separators that must stay separators. Escaping the id whole gives `%2F` and a 404.

---

## 3. Parameters

| Parameter | Notes |
|---|---|
| `frequency=` | one of the series' own frequency ids |
| `data[0]=`, `data[1]=`, … | **required to get values.** Without it rows come back with facets and no data. Index from 0. |
| `start=`, `end=` | inclusive, in the FREQUENCY's period mask — see §5 |
| `facets[<id>][]=` | not used by this loader: it loads whole series |
| `sort[0][column]=period&sort[0][direction]=asc` | optional; the loader sends it by default |
| `offset=`, `length=` | paging — see §7 |

---

## 4. The catalog drives everything

Scope lives in the DATABASE, not in configuration. Three `arm` tables, read through
stored procedures (`sql/EIA/003`):

| Table | Rows (2026-10-16) | Supplies |
|---|---|---|
| `arm.Series` | 260, of which **95** are `Enabled = 1 AND IsDataSeries = 1` | the route, `StartPeriod`, `EndPeriod`, `DefaultDateFormat`, `DefaultFrequency` |
| `arm.SeriesFrequency` | 371 | `FrequencyId` + the period mask in `Format` |
| `arm.SeriesDataType` | 390 | `DataTypeId` → `data[n]=` |

That yields **154 (series × frequency) axes**.

⚠ **`Enabled` is NULL on every row of `arm.SeriesFrequency` and `arm.SeriesDataType`.**
The per-series queries supplied with the loader request do not filter on it, and the procs
do not add such a filter — one would load nothing at all. Populate the column first if you
ever want to use it.

⚠ **`arm.SeriesDataType` has no `ConduitLastUpdate` column.** The loader request listed
one; that is the name the incumbent `dbo.SeriesDataType` uses. The `arm` copy renamed it
to **`ModifedAtUtc`** — with the typo. `arm.SeriesFrequency` likewise. (`arm.Series` has
the correctly-spelled `ModifiedAtUtc`.) The procs return the real column names, so they
run as written.

---

## 5. ⚠ Period masks, and the `local-hourly` trap

`arm.SeriesFrequency.Format` carries an Oracle-style mask. Six exist across eight
frequencies:

| Frequency | Mask | Example | Step |
|---|---|---|---|
| `annual` | `YYYY` | `2021` | 1 year |
| `quarterly` | `YYYY-"Q"Q` | `2026-Q2` | 3 months |
| `monthly` | `YYYY-MM` | `2026-07` | 1 month |
| `weekly`, `four-week-average`, `daily` | `YYYY-MM-DD` | `2026-10-15` | 1 day |
| `hourly` | `YYYY-MM-DD"T"HH24` | `2026-10-15T06` | 1 hour |
| `local-hourly` | `YYYY-MM-DD"T"HH24TZH` | `2026-10-15T02-04` | 1 hour |

**`StartPeriod`/`EndPeriod` are written in the SERIES' `DefaultDateFormat`, not the
frequency's.** `crude-oil-imports` has `StartPeriod = 2009-01` (its default is monthly) but
also publishes an ANNUAL frequency, whose axis must start at `2009`. The loader
re-expresses across masks; it never assumes they agree.

### ⚠ 5.1 `local-hourly` bounds — measured 2026-10-16

| `start` / `end` form | Result |
|---|---|
| `2026-10-01T00` (no zone) | **HTTP 500** `"Something unexpected happened."` |
| `2026-10-01T00Z` | **HTTP 500** |
| `2026-10-01T00-0400` (4-digit) | **HTTP 500** |
| `2026-10-01T00+00` with a raw `+` | **HTTP 500** (a `+` decodes as a space) |
| `2026-10-01T00-00` | ✅ 200 — UTC |
| `2026-10-01T00-04` | ✅ 200 — shifts the bound by the offset |

Only an exact two-digit `±HH` is accepted, and the bound is interpreted as an **absolute
instant** (`-00` and `+00` return identical results). The loader therefore renders every
local-hourly bound as `-00`: unambiguous, and it sidesteps the `+`-encoding hazard
entirely. Rows still come back labelled in each respondent's own local offset, which is
fine — `Period` stores the vendor's label verbatim.

⚠ A 500 is normally retryable, and this one is not. If a future change drops the suffix,
the symptom is a window that retries to exhaustion, so `EiaApiClient` names the trap
explicitly in the exception it raises for a 500 on a zoned frequency.

---

## 6. The status matrix

All rows measured against the live API on 2026-10-16.

| Status | Body | Meaning | Loader |
|---|---|---|---|
| **200** | `data[]` non-empty | data | merge |
| **200** | `data[]` empty, `total: 0` | **legitimate empty window** | success, 0 rows |
| **200** | `warnings[].warning = "incomplete return"` | truncated — or just an offset past the end | see §7 |
| **200** | `warnings[].warning = "parameter out of range: length"` | we sent `length > 5000`; server silently used 5000 | **throw** — a loader bug |
| **400** | `{"error":"Invalid data 'x' provided. The only valid data are 'sales', and 'price'.","code":400}` | malformed request; catalog disagrees with the API | **throw, no retry** |
| **403** | `{"error":{"code":"API_KEY_INVALID","message":"…"}}` | bad/missing key | **throw, no retry** (the gateway limits per key) |
| **404** | **EMPTY** | the route does not exist → catalog drift | **throw, no retry** |
| **429** | — | rate limited | retry with backoff |
| **5xx** | — | transient — except §5.1 | retry with backoff |

Response envelope:

```json
{ "warnings":[{"warning":"…","description":"…"}],          <-- AT THE ROOT
  "response":{ "total":"143",                               <-- A STRING
               "dateFormat":"YYYY", "frequency":"annual",
               "data":[ … ], "description":"…" },
  "request":{…}, "apiVersion":"2.1.14" }
```

Two shapes catch people out: `warnings` is a sibling of `response`, not a child of it; and
`total` is a quoted string. The loader tolerates `total` as a number too.

---

## 7. ⚠ Paging — and why `offset` does not scale

* The page ceiling is **5,000 rows**. Asking for more returns 5,000 anyway plus a
  `"parameter out of range: length"` warning.
* `response.total` is the count for the whole window, independent of `offset`/`length`.

### ⚠⚠ 7.0 READ THIS FIRST — most of §7's performance findings were measured during an outage and are RETRACTED

On 2026-10-16 EIA's `electricity/rto/*` endpoints spent several hours returning 502/503/504
at high rates. Nearly every performance measurement taken that day was an artefact of
that, and was wrongly attributed to properties of our requests. Once the endpoint
recovered, **56 consecutive requests succeeded across every shape tested**, with 2–9s
medians:

| claim | status |
|---|---|
| "Deep offsets hit a cliff; 400,000 fails 100%" | ❌ **RETRACTED** — offset 400,000 and 700,000 both answer in 6–9s |
| "1-hour windows are more reliable than 48-hour" | ❌ **RETRACTED** — 7/8 vs 6/8, noise |
| "Faceting reduces backend cost" | ❌ **RETRACTED** — 48 interleaved requests, all six shapes 100% at 2.1–2.5s |
| "Sustained load degrades throughput ~20×" | ⚠ **UNPROVEN** — more likely the outage than our load |
| api-umbrella caches successful responses | ✅ holds |
| Cross-product facet combinatorics | ✅ holds (structural, not measured) |
| The per-attempt timeout bug | ✅ holds (a code defect, independent of the vendor) |

**The lesson for anyone measuring this API: establish that the endpoint is healthy before
attributing anything to your own request shape.** A quick baseline — a handful of plain
requests — costs seconds and would have saved a day here. Interleave cells rather than
running them as sequential blocks, and use unique URLs so the gateway cache cannot
flatter you (§7.3).

The sections below are kept for the record, each marked with what survived.

### ⚠ 7.1 Deep offsets — a "cliff" that turned out to be an outage

Controlled run on `electricity/rto/daily-fuel-type-data`, 2026-10-16: API otherwise idle,
every URL unique so nothing was served from cache (`cacheHits=0` on every cell), identical
window and page size, only `offset` varying.

| `offset` | failure rate | median of successes |
|---|---|---|
| 0 | 0% | 13.4s |
| 15,000 | 0% | 8.6s |
| 100,000 | 0% | 9.5s |
| **400,000** | **100% — 4/4 HTTP 503** | — |

⚠ **RETRACTED.** Re-tested the same evening once the endpoint recovered, two distinct
365-day windows per offset:

| `offset` | during the outage | after recovery |
|---|---|---|
| 0 | ✅ | ✅ 6.6s / 6.7s |
| 100,000 | ✅ | ✅ 7.7s / 4.1s |
| 400,000 | **503, 4/4** | ✅ **6.1s / 6.2s** |
| 700,000 | **503, 3/3** | ✅ **8.6s / 7.5s** |

There is no cliff. Deep offsets are fine on a healthy endpoint, and the 503s were the
outage.

`MaxRowsPerWindow = 25000` and the subdivision machinery are **kept anyway**, on different
grounds: bounded windows limit the blast radius of a failed request and keep unit sizes
predictable. They are not load-bearing against a vendor limit that does not exist.

> An even earlier version of this section reported a *gradient* — 1.4s at offset 0 rising
> to 11.6s at 100,000. That was also wrong, for a second and unrelated reason: the 1.4s
> figure was a **cache hit** from repeating one URL, measured against cold responses at
> the deeper offsets. Two different measurement errors, same section. See §7.3.

### ⚠ 7.2 The endpoint is slow and flaky regardless of parameters

Same request shape, repeated: 1.4s, 3.0s, 7.0s, 13.6s, 49.2s, and a 60s **504 Gateway
Timeout**. Window size does not change this — a 2-day window 504'd too. **Retries are
normal operation on `electricity/rto/*`**, not an exception, so the retry budget has to be
per attempt rather than shared (`docs/design/EIA.md` §3.1).

### ⚠ 7.2a The 503 is EIA's BACKEND, not the gateway throttling you

Easy to misread as rate limiting, and the fix is different. Captured 2026-10-16:

```
HTTP 503 after 49.0s / 49.9s / 41.6s   (three identical requests — reproducible 3/3)
  Content-Type:     text/html; charset=iso-8859-1
  Content-Length:   299
  X-Frame-Options:  DENY
  Via:              https/1.1 api-umbrella (ApacheTrafficServer)
  (no Retry-After, no X-RateLimit-*, no JSON error body)
```

Three things identify it as an origin error rather than a gateway refusal:

* the body is a **299-byte HTML page**, where api.data.gov's rate-limit refusal is JSON
  carrying `{"error":{"code":"OVER_RATE_LIMIT"}}`;
* **`X-Frame-Options: DENY`**, where a successful 200 from this API carries `SAMEORIGIN` —
  a different component is answering;
* it takes **~45 seconds to produce**. A throttle rejects in milliseconds; a backend
  timing out on an expensive query takes exactly this long.

The practical consequence: **retrying it one second later asks the same overloaded backend
the same expensive question before it has recovered**. The loader therefore backs a
503/504 off 5× further than a 408/429, with ±25% jitter so concurrent units sharing the
key do not retry in lockstep (`EiaHttp.Backoff`).

EIA publishes **no numeric rate limits** — their documentation says only that programs
"must throttle the number of the requests they make per second and per hour", with no
figures, and that exceeding the tolerance suspends the key temporarily. So there is
nothing to tune against; the shipped `RequestsPerSecond` is a judgement call, not a
documented ceiling.

### ⚠ 7.3 api-umbrella CACHES successful responses — mind this when benchmarking

Cold ≈ 7–13s; the identical URL again ≈ **0.2s**. A 20× difference, and it silently
invalidates any measurement that reuses URLs. It produced two wrong conclusions before it
was caught: the "offset gradient" in §7.1, and an apparent concurrency effect that
vanished entirely once every request got a fresh window.

* **Benchmark with unique URLs** and assert `X-Cache` has no `HIT`, or the numbers are
  meaningless.
* Errors are **not** cached — the offset-400,000 503 re-ran the failing query every time
  (3/3, then 4/4, all ~45s).
* Operationally this is in your favour: re-reading a window the gateway has already served
  is nearly free, so `ReloadAll` costs less than its request count suggests. A first-time
  backfill gets no such help.

### 7.2b Faceting — tested, and it does NOT help

`arm.SeriesFacet` and `arm.SeriesFacetValue` let a request be narrowed with
`facets[<id>][]=<value>`. Tested as a fix for the 504s, **48 requests round-robin across
six shapes** so drift hit every variant equally, all URLs unique:

| variant | success | median | rows in slice |
|---|---|---|---|
| 1 day, no facet | 100% | 2.5s | 11,472 |
| 1 day, fueltype (1 of 16) | 100% | 2.1s | 1,536 |
| 1 day, respondent (1 of 83) | 100% | 2.3s | 232 |
| 1 day, both | 100% | 2.1s | 24 |
| 8 days, no facet | 100% | 2.4s | **91,637** |
| 8 days, respondent | 100% | 2.1s | 1,920 |

**No difference.** An unfaceted 8-day query covering 91,637 rows is as fast and as
reliable as a faceted one covering 24. Faceting is not implemented in the loader.

⚠ An earlier *sequential* version of this test appeared to show a strong effect
(17% → 50% → 100% as slices shrank). It was confounded: the cells ran as blocks over ~20
minutes while the endpoint was recovering, and the final cell — 8 days unfaceted, the
largest query in the matrix — came back 83%/4.8s and broke the pattern entirely.

Two structural facts, if faceting is ever revisited:

* **The cross-product is not viable.** `fuel-type-data` is 16 × 83 = 1,328 combinations;
  `petroleum/sum/sndw` is **15,032,800**; `natural-gas/move/ist` 12.8M. Any facet strategy
  must pick one dimension, never the product.
* **`arm.SeriesFacetValue` contains duplicate `ValueId`s** — `fueltype` has 19 rows for 16
  distinct values (BAT, SNB, UES each appear twice) — so any query must `SELECT DISTINCT`
  or it will issue duplicate requests. `arm.SeriesFacet.Enabled` is NULL on all 899 rows,
  like every other catalog `Enabled` column.

### ⚠ 7.3a Latency is per-REQUEST, not per-row — narrowing a window does not make it cheaper

Measured 2026-10-16 on `electricity/rto/fuel-type-data` hourly, 8 distinct windows each,
interleaved, no cached URLs:

| window | success | median | rows the query covers |
|---|---|---|---|
| 1-hour | 7/8 (88%) | **52.1s** | 478 |
| 48-hour | 6/8 (75%) | **25.0s** | 22,944 |

**The 1-hour request returning 478 rows took twice as long as the 48-hour request
returning 5,000.** Per-request overhead dominates; the payload is almost free. The
apparent reliability edge is 7/8 versus 6/8 — one request — and is not meaningful at
this sample size.

The consequence for sizing: to cover the same 48 hours, 1-hour windows need 48 requests
at ~52s where 48-hour windows need ~5 at ~25s — roughly **20× the wall time for the same
data**. Narrow windows buy failure GRANULARITY (a lost request costs one hour instead of
two days), not speed and not, on this evidence, reliability.

⚠ This was also measured while the endpoint was unusually degraded: 504s were returning
at a uniform **~10.2s**, against the ~45–60s seen earlier the same day. Absolute numbers
here are a snapshot, not a steady state.

### 7.4 What does NOT cause 503

Same controlled run, 136 cold requests. **Only 4 failed, all of them at offset 400,000.**

| variable | tested | failures |
|---|---|---|
| concurrency 1 / 2 / 4 (forwards and backwards) | 72 requests | **0** |
| `sort[0][column]=period` on vs off | 36 requests | **0** |
| query cost — cheap vs heavy endpoint, both at concurrency 8 | 24 requests | **0** |

Concurrency 4 delivers ~4× the throughput of concurrency 1 with no extra failures, so
throttling it down buys nothing. Sort is free: sorted 13.5s, unsorted 9.7s, sorted again
9.8s — the apparent cost was drift, and the repeat control removed it.

⚠ Latency does wander a lot run to run (medians 7.5s–14.4s on identical workloads) with no
failures attached. Do not read a slow cell as a failing one.

### 7.5 Stop condition
* **An `offset` past the end returns HTTP 200 with ZERO rows — and still carries the
  `"incomplete return"` warning.** So that warning cannot be used as "there is more"; the
  loader's stop condition is an empty page (or a short page).
* Paging was measured **stable** — `coal/market-sales-price` over four 1,000-row pages
  returned 3,739 distinct rows with zero duplicates and zero gaps, both with and without
  an explicit sort. The loader still sends `sort[0][column]=period` by default and still
  reconciles the keys it collected against `total`, because an undefined order is only
  safe until the vendor changes its default.

Live multi-page check through the real reader (2026-10-16): `steo` quarterly 2027,
`total = 5190` → 2 pages, 5,190 rows read, batches `[5000, 190]`, 5,190 distinct primary
keys, **exact** against the vendor's own count.

### Scale

| Series | Frequency | `total` |
|---|---|---|
| `electricity/rto/fuel-type-data` | hourly | 27,692,383 |
| `electricity/rto/fuel-type-data` | local-hourly | 27,692,383 |
| `electricity/rto/daily-fuel-type-data` | daily | 5,721,699 |
| `international` | annual | 4,289,760 |
| `electricity/rto/daily-region-data` | daily | 4,031,645 |
| `crude-oil-imports` | monthly | 559,004 |
| `natural-gas/stor/wkly` | weekly | 7,008 |

A full backfill is on the order of **90 million rows and tens of thousands of requests**.

---

## 8. ⚠ How a row becomes `arm.SeriesData`

Every field of a data row goes to exactly one of three places:

* **`period`** → the `Period` column, stored exactly as the API spelled it.
* **a data field** → `DataJSON`. The set is every `DataTypeId`, every
  `<DataTypeId>-units`, and the bare **`units`**.
* **everything else** → `FacetJSON`.

Verified against one sampled row from each of the 134 series present in the incumbent: in
**134/134** every `DataJSON` key fell inside that set.

Two spellings matter. The `natural-gas/*` and `petroleum/*` routes name their units column
plainly **`units`** (`{"units":"BTU/CF","value":"1028"}`). The SINGULAR **`unit`** and
**`unitName`** are *facets* — `international` carries both alongside a `value` data field.

`FacetKey` and `DataHash` are a 32-bit hash of the respective JSON text; `FacetKey` is part
of the primary key. See `docs/design/EIA.md` §6 for the algorithm and its provenance.

### 8.1 ⚠ The vendor changed its JSON value types, and it forked the incumbent table

The same logical row exists **twice** in `dbo.SeriesData` under two different `FacetKey`s:

```
ConduitLastUpdate 2022-09-16   {"activityId":8,  …,"productId":4002, …}   {"value":1117.538985}
ConduitLastUpdate 2024-01-26   {"activityId":"8",…,"productId":"4002",…}  {"value":"1117.538985"}
```

EIA flipped numbers to strings somewhere between those dates. Conduit preserved whatever
token type arrived; the key is a hash of the TEXT; so the row forked. The scale is visible
in a single query — for `international` 1949 the API returns **49** rows and `dbo` holds
**98**; for `electricity/rto/daily-region-data` on 2019-01-01 the API returns **1,456** and
`dbo` holds **2,912** (that second fork has a different cause — see §8.3).

The loader **normalises**: a string whose content is a number is written unquoted, so both
wire forms produce one key and a future type flip re-keys nothing. That is also exactly
what the loader request specifies — its worked example takes `"sales":"1023740"` off the
wire and stores `"sales":44163`.

### 8.2 ⚠ `".43"` — a decimal JSON itself would reject

`coal/aggregate-production` sends `"productivity":".43"` — no integer part, which JSON does
not permit. Live-verified: **5 of the 400 rows** for 2001. The incumbent stores `0.43`.

Emitting `.43` unquoted would put *invalid JSON* in the column and break `ISJSON` /
`OPENJSON` for every consumer; leaving it a string would make one row in eighty
non-numeric for no reason. The loader inserts the `0` — pure text surgery, no arithmetic,
so no digit changes. A trailing dot (`"5."` → `5`) is handled the same way. A leading `+`
and leading zeros are deliberately **not** repaired: `"007"` is an identifier.

### 8.3 ⚠ A Conduit defect that also forks rows

`electricity/rto/daily-region-data` rows refreshed on 2026-06-27 put **`value-units` in the
FACET object**, while rows refreshed on 2026-03-30 correctly put it with the data. The API
sends it as a data-units field. This loader always places it with the data and does not
reproduce the defect — which is why it writes 1,456 rows where `dbo` holds 2,912.

### 8.4 Canonical form

No whitespace; keys sorted **ordinal** ascending; minimal JSON escaping with the five short
control escapes; **`/` is NOT escaped** (the wire sends `BTU\/CF`, the incumbent stores
`BTU/CF`); non-ASCII left literal. A row with no facets stores the literal `{}`, whose hash
is `1947253782` — the value 7,301 facet-less rows in the incumbent carry.

---

## 9. How this document was verified

* Vendor PDF read in full (`docs/Dev Documentation/EIAv2.pdf`, v2.0.1).
* Live calls to `api.eia.gov/v2` on **2026-10-16** across all 8 frequencies / 11 series,
  10,002 rows.
* Error behaviours provoked deliberately: bad key (403), unknown route (404), unknown data
  column (400), unknown frequency (400), `length=5001` (200 + warning), `offset` past the
  end (200 + 0 rows + warning), and all six `local-hourly` bound forms in §5.1.
* The hash algorithm was recovered by testing candidates against stored values and then
  checked on a **20,000-row sample of `dbo.SeriesData`: 20,000/20,000 exact** on both
  `FacetKey` and `DataHash`.
* The facet/data partition was checked against one sampled row from each of the **134**
  series present in the incumbent.
* Every produced row in the live run was compared against `dbo.SeriesData`:
  **8,568/8,568 `FacetKey` matches, zero misses.**
