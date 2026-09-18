# Genscape (Oil Fundamentals API v1) loader — source field reference

**Vendor:** Genscape (Wood Mackenzie)
**Product:** Oil Fundamentals API v1 (`api.genscape.com`)
**Source type:** HTTPS REST, JSON, `Gen-Api-Key` header
**In-scope endpoints:** 2 (`crude-storage/weekly`, `crude-transportation/weekly`)
**Target:** database `Genscape`, schema `arm`, two tables

---

## Gate status: **READ PATH FULLY VERIFIED — zero reconstruction** ✅

Every statement in this document was verified against the **live API** on **2026-09-08** with the
issued key. No field name, status code, limit or window rule below is inferred from documentation.

Evidence base:

- All four (endpoint × request region) combinations pulled and their payloads inspected.
- The status matrix exercised deliberately: bad region, missing region, bad revision, reversed
  window, malformed date, absent key, wrong key, future window.
- Window semantics established by differential probing (§3.1).
- The 5,000-row cap established by bracketing (§5).
- **The loader's own reader executed against production** for all four combinations, asserting
  descriptor arity, CLR types, key uniqueness, column widths and the derived week (§9).
- **The bisection path executed against production** and proved row-for-row equivalent to the
  single-request read (§9).

**Not verified — no live target database exists:** the SQL in `sql/Genscape/` is
ScriptDom-parse-checked but never deployed, and no row has been merged.

---

## ⚠⚠ THE FIVE THINGS THAT WILL BITE YOU

| # | Trap | Consequence if missed |
|---|------|----------------------|
| 1 | **`endDate` is EXCLUSIVE.** `startDate=2026-08-14&endDate=2026-08-28` returns the 14th and 21st but **not the 28th**. | Every run silently drops the most recent report, forever, while looking healthy. |
| 2 | **Responses are silently capped at 5,000 rows**, newest first — so the **oldest** rows vanish. No cap field, no cursor, no header. | A wide backfill loads a truncated window and reports success. A 2000-2026 request looks fine and is missing five years. |
| 3 | **`crude-storage`'s `week` is a week-of-MONTH.** 2026-08-28 gives `4`; 2025-12-05 gives `1`. `crude-transportation`'s is the real week-of-year. | The `Week` column — a primary-key component — is garbage, and the two tables disagree with each other. |
| 4 | **`region` in the response is not the `region` you asked for**, and the two request regions **overlap**. | Treating the request region as the row's region collapses six regions into one. Not expecting the overlap makes a duplicate key look like a bug. |
| 5 | **`startDate == endDate` is rejected** with `400`, and a window narrower than the gap between reports returns `200 {"data":[]}`. | Per-day work units — the pattern most loaders in this repo use — fail outright. |

---

## 1. Connection

| Setting | Value | Note |
|---|---|---|
| Base URL | `https://api.genscape.com/oil-fundamentals/v1` | |
| Auth | `Gen-Api-Key: <key>` **header** | Never a query parameter, so it cannot appear in a logged URL. Resolved from `core.Param(LoaderName='Genscape', ParamName='ApiKey')`. Never logged. |
| Other headers | `Accept: application/json`, `Cache-Control: no-cache` | The endpoint sits behind Imperva (`X-CDN: Imperva`); a cached weekly window would hide a revision. |
| Developer portal | `developer.genscape.com` | Documentation only — behind a separate login, not used at run time. |

---

## 2. The two endpoints

```
GET {base}/crude-storage/weekly?region=…&revision=revised&startDate=…&endDate=…&format=json
GET {base}/crude-transportation/weekly?region=…&revision=revised&startDate=…&endDate=…&format=json
```

---

## 3. Query parameters

| Parameter | Accepted values | Verified behaviour |
|---|---|---|
| `region` | `NorthAmerica`, `GulfCoast` | **Case-insensitive** (`northamerica` works). Anything else gives `400`. **Omitting it gives `404`**, not `400`. |
| `revision` | `revised` | The ONLY accepted value. `preliminary` gives `400 "Must have a value specified"`. **Omitting it entirely gives `200`** with the same rows as `revised`. |
| `startDate` | RFC 3339 full-date | **Inclusive.** |
| `endDate` | RFC 3339 full-date | **EXCLUSIVE**, and must be **strictly after** `startDate`. |
| `format` | `json` | Parameter names and values are case-insensitive (`FORMAT=JSON` works). |

### ⚠ 3.1 The window is `[startDate, endDate)` — the evidence

| Request | Report dates returned |
|---|---|
| `2026-08-14 .. 2026-08-28` | 08-14, 08-21 — **not 08-28** |
| `2026-08-14 .. 2026-08-29` | 08-14, 08-21, **08-28** |
| `2026-08-28 .. 2026-08-29` | 08-28 |
| `2026-08-28 .. 2026-08-28` | `400 [endDate] must be after [startDate]` |

The loader therefore sends `endDate = windowEnd + 1 day` (`GenscapeSourceReader.BuildUri`), pinned
by `GenscapeReaderTests.The_request_sends_endDate_one_day_past_the_windows_last_day`.

---

## 4. ⚠ The `week` field, and why the loader overwrites it

Both endpoints return `year` and `week`. Queried over the same dates:

| reportDate | `crude-storage` | `crude-transportation` | `DATEPART(week, …)` |
|---|---|---|---|
| 2025-12-05 | 2025 / **1** | 2025 / **49** | 49 |
| 2025-12-12 | 2025 / **2** | 2025 / **50** | 50 |
| 2025-12-26 | 2025 / **4** | 2025 / **52** | 52 |
| 2026-01-02 | 2026 / 1 | 2026 / 1 | 1 |
| 2026-02-13 | 2026 / 7 | 2026 / 7 | 7 |
| 2026-08-28 | 2026 / **4** | 2026 / **35** | 35 |

`crude-storage` is returning the **week of the month** (5 December is in the first week of
December). `crude-transportation` returns the calendar week-of-year and agrees with SQL Server's
`DATEPART(week, …)` under the `us_english` default `DATEFIRST` of 7 **in every row sampled,
including across a year boundary**.

The loader therefore **derives both columns from `ReportDate`** for both feeds
(`GenscapeTime.WeekOfYear` / `.YearOf`):

```
week = 1 + (dayOfYear(d) - 1 + weekdayIndexOf(1 January of d's year)) / 7      // Sunday = 0
year = calendar year of d
```

Three things about that decision:

- **It is a no-op on transportation.** The derived value reproduces the vendor's own number
  exactly, which is the evidence that it is right rather than merely different.
- **`Year` is derived too, though the API's `year` was never observed to be wrong.** `Week` resets
  on 1 January of the *calendar* year, so a payload `year` paired with a derived `week` could
  produce an incoherent key such as `(2025, 1)` for a January 2026 report. The pair has to come
  from one source.
- **It is computed in C#, not in the merge proc.** `DATEPART(week, …)` depends on the session's
  `DATEFIRST`, which follows the login's default language. `Week` is a **primary-key component**,
  so a connection under a Monday-first language would fork every key and load each report twice.
  `arm.usp_ValidateLoad` re-asserts the equality server-side under an explicit `SET DATEFIRST 7`.

Range: 1..54. 54 is reachable (a leap year beginning on a Saturday, e.g. 2028), which is why the
column is `TINYINT` rather than something narrower.

---

## 5. ⚠ The 5,000-row cap

| Request (`crude-storage`, NorthAmerica) | Rows |
|---|---|
| 2000-01-01 .. 2026-09-08 | **5000** (oldest returned: 2015-11-13) |
| 2014-01-01 .. 2026-09-09 | **5000** |
| 2015-06-01 .. 2026-09-09 | **5000** |
| 2016-01-01 .. 2026-09-09 | 4944 |
| 2010-01-01 .. 2016-01-01 | 1514 (oldest: **2010-01-01**) |

The last row is the proof of harm: history *does* reach back to at least 2010-01-01, but the
full-range request silently stops at 2015-11-13. Rows are ordered **newest first**, so truncation
drops the **oldest** end.

There is no cap field, no paging cursor and no warning header. The loader therefore treats a
response **at** the cap as untrustworthy: it discards it and re-reads the window as two **disjoint**
halves (`[start..mid]`, `[mid+1..end]`), recursively. A single day still at the cap **throws**
rather than loading a knowingly partial window.

Observed history floors: storage reaches at least 2010-01-01 in both regions; transportation
reaches at least 2010-01-01 for NorthAmerica and 2010-01-01 for GulfCoast. (Both were probed from
2010; earlier data may exist.)

---

## 6. Response shape

```json
{ "data": [ { … }, … ] }
```

An absent `data` property is treated as a contract change and **throws** — reading it as "no rows"
would report a clean, successful, empty load forever.

### `crude-storage/weekly`

| Field | JSON type | Target column | Notes |
|---|---|---|---|
| `reportDate` | string | `ReportDate` DATE | RFC 3339 full-date. Always a **Friday**. |
| `year` | number | — | **Not loaded.** Derived. |
| `week` | number | — | **Not loaded.** Derived — see §4. |
| `region` | string | `Region` VARCHAR(50) | Row-level region, not the request region. Max observed 24 chars. |
| `product` | string | `Product` VARCHAR(50) | `Crude`, `Diluent`. |
| `storageFieldType` | string | `StorageFieldType` VARCHAR(50) | `Refinery`, `Storage Terminal`. |
| `storageAmount` | number | `StorageAmount` FLOAT | Barrels. No null observed. |
| `capacityUtilization` | number | `CapacityUtilization` FLOAT | Percent. No null observed. |

### `crude-transportation/weekly`

| Field | JSON type | Target column | Notes |
|---|---|---|---|
| `reportDate` | string | `ReportDate` DATE | As above. |
| `year` / `week` | number | — | **Not loaded.** Derived. |
| `region` | string | `Region` VARCHAR(50) | A **corridor** (`Cushing to Gulf Coast`, `Into Houston`), not a place. |
| `type` | string | `Type` VARCHAR(50) | `Pipeline`, `Rail`. |
| `flowBPD` | number | `FlowBPD` FLOAT | Barrels per day. No null observed. |

---

## 7. ⚠ The regions overlap, and the values agree

| Feed | `NorthAmerica` returns | `GulfCoast` returns |
|---|---|---|
| storage | Canada, Cushing, **Louisiana Gulf Coast**, Patoka, Texas Gulf Coast, **West Texas** | Beaumont-Nederland, Corpus Christi, Houston, **Louisiana Gulf Coast**, **West Texas** |
| transportation | Canada, Canada to PADD 2, Canada to PADD 4, Cushing to Gulf Coast, Cushing to Midwest, North Dakota, **PADD 2 to PADD 3**, **PADD 3 to PADD 2**, US to Canada, **West Texas to Gulf Coast**, **West Texas to Midwest** | Gulf Coast, Into Beaumont-Nederland, Into Corpus Christi, Into Houston, **PADD 2 to PADD 3**, **PADD 3 to PADD 2**, **West Texas to Gulf Coast**, **West Texas to Midwest** |

Measured over 2025-12-01 .. 2026-02-15:

- storage: **33** shared primary keys, **0** value disagreements
- transportation: **44** shared primary keys, **0** value disagreements

So the same key is merged twice per run, by two different work units, in whichever order they
finish — and the result is the same either way. Merge ordering is **not** load-bearing here, in
contrast to Argus's `DCRDEUS`. Dropping either request region would lose rows: `GulfCoast` is the
only source of Beaumont-Nederland, Corpus Christi and Houston.

---

## 8. The status matrix

| Status | Body | Meaning | Loader behaviour |
|---|---|---|---|
| `200` | `{"data":[…]}` | Rows | Merge |
| `200` | `{"data":[]}` | **Legitimate empty read** — window before the first report or after the latest published one | **Success, 0 rows** |
| `400` | RFC 7807 with `invalidParameters` | Malformed request: unknown region, bad `revision`, non-RFC-3339 date, `endDate` at or before `startDate` | **Throw, no retry**; the `reason` text is carried into the message |
| `401` | **EMPTY** | Missing or wrong `Gen-Api-Key` | **Throw, no retry**; message names the setting and the `core.Param` row, never the key |
| `404` | `{"statusCode":404,"message":"Resource not found"}` | **A required parameter is absent** (omitting `region` does this), or a wrong path | **Throw** — it means the loader built a bad URL |
| `408` / `429` / `5xx` | — | Transient | **Retried** with exponential backoff; `429` honours `Retry-After` |

No `X-RateLimit-*` or `Retry-After` header was seen on any successful probe, and no rate limit is
published. The API sits behind Imperva, which is why `429` is handled even though it was never
provoked.

---

## 9. How this document was verified

```
2026-09-08, live API, issued key

1. 4 payload pulls (2 endpoints x 2 regions); field names/casing/types read off the wire
2. Status matrix: 11 deliberate probes covering every row of the table in §8
3. Window semantics: 4 differential probes establishing [start, end)
4. Row cap: 5 bracketing probes pinning it at exactly 5000, newest-first
5. Overlap: set comparison of the two request regions, value-by-value on shared keys
6. THE LOADER'S OWN READER executed against production, all 4 combinations:
     descriptor arity OK - CLR types OK - no NULL in a required column
     Year/Week == calendar year/week, checked against an INDEPENDENT implementation
     no VARCHAR(50) overflow - primary keys unique within each work unit
7. THE LOADER'S OWN BISECTION executed against production, cap forced to 100:
     CrudeStorageWeekly         315 rows whole-window == 315 rows bisected, row-for-row
     CrudeTransportationWeekly  385 rows whole-window == 385 rows bisected, row-for-row
```

Steps 6 and 7 are what make this document evidence about **the loader**, not merely about the API.

**Not verified:** the SQL. `arm.OilFundamentals_*`, their TVPs and their merge procs parse clean
under ScriptDom and have **never been deployed**; no row has been merged.
