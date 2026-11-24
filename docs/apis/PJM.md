# PJM (Data Miner 2 API v1) loader — source field reference

**Vendor:** PJM Interconnection
**Product:** Data Miner 2 (`dataminer2.pjm.com`), data API `api.pjm.com/api/v1`
**Source type:** HTTPS REST, JSON, `Ocp-Apim-Subscription-Key` header (Azure API Management)
**In-scope feeds:** 8: `gen_by_fuel` and `inst_load` (§1–§11), plus six load and load-forecast feeds added 2026-11-23 (§12)
**Target:** database `Misc`, schema `dbo`, tables `dbo.pjm_fuel_mix` and `dbo.pjm_load`, plus the six `dbo.pjm_load_*` tables listed in §12

---

## Gate status: **PARTLY LIVE-VERIFIED 2026-11-23** (fields, keys, timestamps, envelope, gzip, retention)

### ✅ LIVE-VERIFIED 2026-11-23 (real API, user-approved)

| Fact | Result |
|---|---|
| `gen_by_fuel` fields | Exactly `datetime_beginning_utc`, `datetime_beginning_ept`, `fuel_type`, `mw`, `fuel_percentage_of_total`, `is_renewable`. These match the build. |
| `fuel_percentage_of_total` | **Exists.** It is a **fraction 0..1** (e.g. `0.210`), so `DECIMAL(9,6)` fits. |
| `is_renewable` | A JSON **bool**. |
| `inst_load` fields | Exactly `datetime_beginning_utc`, `datetime_beginning_ept`, `area`, `instantaneous_load`, with no other columns. These match the build. |
| Timestamp format | ISO `yyyy-MM-ddTHH:mm:ss`, with **no zone and no fraction**. |
| Envelope | `items`, `totalRows`, `links` (camelCase). |
| Compression | The response is **gzip** when requested. |
| `inst_load` retention | **"30 days"**, from the live catalogue (`GET /api/v1/` returns `retentionTime` per feed). The loader's `today − 28` cap stands. |
| Keys | **VERIFIED over the full UTC day 2026-11-01, the DST fall-back.** `(datetime_beginning_utc, area)`: 0 duplicates across 7,200 rows (25 areas × 288). The EPT-based key had **200** duplicates. `(datetime_beginning_utc, fuel_type)`: 0 duplicates across 230 rows. |
| Bound | `00:00to23:59` UTC returned exactly **7,200 = 25 × 288** `inst_load` rows, the last at 23:55:01. This is consistent with the inclusive minute bound and the design's window. |

### ⚠ Two behaviours found live, both handled by the loader

1. **`inst_load` timestamps carry 0–2 SECONDS of posting jitter.**
   - On 2026-11-01: 6,075 rows were at :00, 1,075 at :01 and 50 at :02 (e.g. `00:15:01`, `23:55:01`).
   - The minute is **always** a multiple of 5, and every area has exactly one row per 5-minute slot.
   - All areas in a slot share the same seconds.
   - The loader truncates both timestamps to the minute, so it stores the slot start. It fails
     only on seconds above `TimestampJitterToleranceSeconds` (10) or an off-boundary minute.
     A re-post with different seconds therefore merges onto the same row (design §4.4).
   - `gen_by_fuel` showed 0 jitter, and the same rule applies to it.
2. **`gen_by_fuel` drops the repeated fall-back hour.**
   - 2026-11-01 has **23 hours**: UTC 06:00 (EST 01:00, the second copy of EPT 01:00) is
     **absent**.
   - The completeness warning is DST-aware and expects 23 on such a day.
   - `inst_load` keeps all 288 slots on the same day.

Everything not listed above is still unverified: status codes, the rate tier, `isActiveMetadata`
semantics and paging. The rest of this document was written before any live call. Its labels:

| Tag | Meaning |
|---|---|
| **[PJM-DOC]** | Stated by PJM on a public page: the Data Miner 2 release notes, the API portal widget, the public RSS syndication feed, or `config/settings.json`. It is documented but **not live-verified**. |
| **[PJM-RSS]** | Observed on PJM's **public, unauthenticated** RSS syndication feed (`api.pjm.com/syndication/v1/<feed>/rss`) on 2026-11-23. It is live, but it shows posting times only and no data fields. |
| **[3P]** | Taken from third-party open-source clients that call this API: `gridstatus` (`gridstatus/pjm.py`, main branch), a Home Assistant PJM sensor gist, and an api-evangelist skill file citing *PJM Data Miner API Guide v15, 2026-02-10*. These are corroborating evidence and **not authoritative**. |
| **[RECON]** | Reconstructed from the lead's recollection or by inference. No public source was found. **Treat as unknown until §11 is run.** |

The official guides are *Data Miner 2 API Guide*, `pjm.com/-/media/etools/data-miner-2/data-miner-2-api-guide.ashx`
(v15, 2026-02-10), and *Historic Data Guide*. Both are hosted publicly, but their text could not be extracted in
this session because they are compressed PDF streams. **A person should read the API guide's parameter and
error sections and reconcile them with this document.** Note that the guide's PDF metadata carries a stale
Microsoft sensitivity label, "Confidential - PJM Personnel Only", even though PJM publishes it openly on pjm.com.

**Not verified:** any status code, the key's rate tier, multi-page paging, and any SQL. (Fields, keys,
timestamps, envelope, gzip and retention: see the LIVE-VERIFIED table above.)

---

## ⚠⚠ THE SIX THINGS MOST LIKELY TO BITE

| # | Trap | Consequence if missed | Source |
|---|------|----------------------|--------|
| 1 | **`inst_load` keeps only 30 calendar days.** Anything older is gone from Data Miner, permanently. | A 30-day initial backfill sits exactly on the retention edge, so its oldest day may be partly purged before the run reaches it. Any outage longer than about 30 days loses data with no way to recover it. | [PJM-DOC] release 19.07, [3P] gridstatus "Must be in last 30 days" |
| 2 | **The `datetime_beginning_ept` wall clock repeats in the fall-back hour, and an EPT *filter* cannot select the daylight copy of that hour.** Data Miner reads the repeated wall time as standard time. | Keying or filtering on EPT drops or collides an hour on every DST fall-back day. Key and filter on **UTC**. | [3P] gridstatus comment |
| 3 | **The date filter's upper bound is INCLUSIVE, at minute resolution.** `… to 11/02/2026 00:00` returns rows stamped exactly 00:00 on 11/02. | Adjacent `[D 00:00 to D+1 00:00]` windows each read the boundary interval, so it is fetched twice. Harmless under MERGE, but double-counted in any per-unit reconciliation. | [3P] gridstatus "PJM API is inclusive of end" |
| 4 | **Paging is not snapshot-stable**, and Data Miner caches an identical query for about 20 seconds. Pages of one query can disagree on `totalRows` when data posts between them. A `next` link can replay the query's original snapshot. | Rows silently skipped or duplicated across pages. This is the same failure class as NUCORE's unsorted paging, which lost about 2.6% per day. | [3P] gridstatus docstring and constants |
| 5 | **Missed `inst_load` intervals are normally NOT replaced.** If PJM's posting stops, "PJM typically won't replace the missed intervals." | A day with fewer than 288 intervals per area can be the final truth, not a loader fault. A completeness check must warn, not fail. | [PJM-RSS] channel description |
| 6 | **The key is Data Miner 2's shared *public* key** (the `subscriptionKey` in `dataminer2.pjm.com/config/settings.json`), and its rate tier is unknown. Non-members are documented at **6 data connections/min**. | The loader shares a quota with every anonymous DM2 web user. PJM can rotate the key without notice, which turns every run into 401s. | [PJM-DOC] API portal widget, [3P] HA gist |

---

## 1. Connection

| Setting | Value | Source |
|---|---|---|
| Base URL | `https://api.pjm.com/api/v1` | [PJM-DOC] `config/settings.json` |
| Syndication base | `https://api.pjm.com/syndication/v1` (RSS change notifications, **unauthenticated**) | [PJM-DOC] `settings.json`, [PJM-RSS] |
| Training sandbox | `https://api-train.pjm.com/api/v1` | [3P] apis.io |
| Auth | `Ocp-Apim-Subscription-Key: <key>` **header**. Without it the API answers **HTTP 401** (established by the lead). | [PJM-DOC], [3P] |
| Key acquisition | Fetched at the start of each run from `https://dataminer2.pjm.com/config/settings.json` (`subscriptionKey`), exactly as the Data Miner 2 website does. An optional ARM override (env var, then `core.Param(LoaderName='PJM', ParamName='ApiKey')`) takes precedence when present. Never logged, never in a URL, never in this repo. See `docs/design/PJM.md`. | User decision (2026-11-23) |
| Key provenance | The Data Miner 2 **public** subscription key, the `subscriptionKey` field of `https://dataminer2.pjm.com/config/settings.json`. **No value is reproduced here.** | User decision, [3P] HA gist |
| TLS | TLS 1.2+ (TLS 1.0/1.1 dropped in release 22.04) | [PJM-DOC] |
| Use terms | DM2 data is "for internal use only". Redistribution requires a PJM-issued Redistribution License. | [PJM-DOC] |

The `Ocp-Apim-*` header name means the gateway is Azure API Management. APIM conventionally throttles with
**429** and a `Retry-After` header and answers a bad key with **401**. That convention is [RECON] for this
tenant; see §8.

---

## 2. Endpoints

```
GET {base}/gen_by_fuel?rowCount=…&startRow=…&sort=datetime_beginning_utc&order=Asc
                      &datetime_beginning_utc=<from>to<to>[&fields=…]
GET {base}/inst_load  ?rowCount=…&startRow=…&sort=datetime_beginning_utc&order=Asc
                      &datetime_beginning_utc=<from>to<to>[&fields=…]
GET {base}/<feed>/metadata                # column list, types, filterable/sortable flags
```

`/metadata` is documented as the discovery call ([PJM-DOC] release 18.01 "feed_metadata API response"; [3P]).
Its response shape is [RECON].

---

## 3. Query parameters

| Parameter | Behaviour | Source |
|---|---|---|
| `rowCount` | Page size. **Max 50,000.** Required whenever any other parameter is present. | [PJM-DOC] roadmap slides "Up to 50K Rows per fetch", [3P] skill file and gridstatus `DATA_MINER_MAX_ROW_COUNT = 50000` |
| `startRow` | **1-based.** Required whenever any other parameter is present. | [3P] skill file and gridstatus |
| `sort` | Column name. **Sort on the same clock you filter on** (UTC with UTC). UTC sorting was fixed in release 19.10. | [3P] skill file, [PJM-DOC] 19.10 |
| `order` | `Asc` / `Desc` | [3P] gridstatus and HA gist |
| `fields` | Comma-separated projection, e.g. `area,datetime_beginning_utc,instantaneous_load`. **The loader should NOT project.** It should request every column so that a new or renamed column surfaces instead of being silently ignored. | [3P] |
| `datetime_beginning_utc` | Range filter `MM/dd/yyyy HH:mmtoMM/dd/yyyy HH:mm`, with a **literal `to`** and no spaces in both third-party clients. Whether a spaced `" to "` is accepted is [RECON]. | [3P] gridstatus `strftime("%m/%d/%Y %H:%M") + "to" + …`, HA gist |
| `datetime_beginning_ept` | The same syntax, in Eastern Prevailing Time. **Do not use it**: it cannot select the daylight copy of the fall-back hour (§6). | [3P] |
| `isActiveMetadata` | Both third-party clients send `true` with `inst_load`. Its semantics are **undocumented here** [RECON]. Verify with and without it in §11. | [3P] |
| `format` / `download` | JSON is the default. With `download=true`, `links`, `totalRows` and `searchSpecification` move into **response headers** (`X-TotalRows`). **Do not use `download`.** | [3P] skill file |
| Date aliases | e.g. `CurrentHour`, `LastHour`, `Last Week`. The 18.02 alias was "API only". Not used by the loader. | [PJM-DOC] 18.02, 22.08 |

### 3.1 Filter bound semantics

| Fact | Detail | Source |
|---|---|---|
| Resolution | **Minute.** "Data Miner reads an end of 12:00 as 12:00:00." | [3P] gridstatus |
| Lower bound | Inclusive | [3P] inferred from gridstatus |
| **Upper bound** | **INCLUSIVE.** gridstatus drops rows whose start equals the requested end, because "PJM API is inclusive of end". | [3P] |
| Max span | Under 366 days per request | [3P] skill file and gridstatus "max of 365 days at a time" |
| Same-UTC-calendar-year rule, no sort/order | Applies **only to archived (historic) data**. Archiving covers the LMP feeds (`rt_fivemin_hrl_lmps`, `rt_hrl_lmps`, `da_hrl_lmps`), **not** these two feeds. | [PJM-DOC] release 20.01, [3P] |
| UTC filter supported? | **Yes.** Every filter field except `forecast_execution_date` and `generated_at` has a UTC counterpart. | [3P] gridstatus `DATA_MINER_EPT_ONLY_FILTER_FIELDS` |

**Recommended request window** [RECON, design proposal]: one work unit is one **UTC** calendar day `D`, sent as
`datetime_beginning_utc=MM/dd/yyyy 00:00toMM/dd/yyyy 23:59`. Because the bound is inclusive, `23:59` captures the
`23:55` five-minute interval and the `23:00` hour, and never touches `D+1 00:00`. A UTC day is always 24 h /
288 five-minute intervals, so there is no 23/25-hour special case. If §11.4 shows the upper bound is actually
exclusive, this window still works because nothing is stamped `23:59`.

**Interval boundary (LIVE-VERIFIED 2026-11-23, with a correction).**
- The **minute** is always on the boundary: a multiple of 5 for `inst_load`, `:00` for `gen_by_fuel`.
- The **seconds are NOT always zero.** `inst_load` carries 0–2 s of posting jitter (see the gate status at the top).
- The loader therefore truncates to the minute and fails only an off-boundary minute, or seconds above
  `TimestampJitterToleranceSeconds` (10) (design §4.4 item 3).
- After truncation nothing can sit inside `23:59:xx`, so the inclusive `23:59` bound stays lossless. A live
  `00:00to23:59` read returned all 7,200 = 25 × 288 rows, the last at `23:55:01`.

---

## 4. Response envelope

```json
{
  "items":      [ { … }, … ],
  "totalRows":  1234,
  "links":      [ { "rel": "next", "href": "https://api.pjm.com/api/v1/…&startRow=…" }, … ],
  "searchSpecification": { … }
}
```

| Property | Status | Source |
|---|---|---|
| `items` | Array of row objects | [3P] gridstatus `r["items"]` |
| `totalRows` | Integer, the total over the whole query, not the page | [3P] gridstatus `r["totalRows"]` |
| `links` | Array of `{rel, href}`. `rel="next"` carries the next page URL. | [3P] older gridstatus, skill file |
| `searchSpecification` | Echo of the query | [3P] skill file (download-mode header list) |
| `errors` | Present on a failed query. gridstatus raises if the key exists. | [3P] |

The exact casing (`totalRows` vs `TotalRows`) is [RECON]. gridstatus reads camelCase, while the skill file
writes PascalCase. An absent `items` or `totalRows` must **throw** (a contract change), never read as "no rows".

### 4.1 Paging and termination

| Rule | Source |
|---|---|
| Request explicit `startRow = 1, 1+rowCount, …` rather than following `links[rel=next]`. A `next` link can replay a stale snapshot, and on archived data it was once buggy (release 20.03). | [3P] gridstatus, [PJM-DOC] 20.03 |
| Stop when `startRow > totalRows`. | [3P] |
| Assert every page reports the **same `totalRows`**, and that rows read equal `totalRows`. Otherwise **re-read the unit once**, then fail. | [3P] gridstatus `DataMinerSnapshotMismatch` |
| Always send `sort=datetime_beginning_utc&order=Asc`. Unsorted paging lost rows on NUCORE; whether DM2 is stable without `sort` is unknown. | Repo lesson (NUCORE), [RECON] |

**Expected volume per UTC-day unit** [RECON from the field and enum counts below]: `gen_by_fuel` ≈ 24 h × ~11
fuel types ≈ **264 rows**, and `inst_load` ≈ 288 intervals × ~25 areas ≈ **7,200 rows**. Each fits in **one
page** at `rowCount=50000`, so in practice the snapshot problem only appears if someone widens the window.

---

## 5. Feeds and fields

Types are as they appear in JSON. Column names in the target tables are proposed to mirror the field names (the
DATABASE_DEVELOPER stage owns the final DDL). **No field may be omitted** because it looks unused.

### 5.1 `gen_by_fuel` → `dbo.pjm_fuel_mix`

"Shows the fuel mix of generation resources operating under PJM direction for each hour." [PJM-RSS]

| Field | JSON type (expected) | Nullable | Proposed column | Status | Notes |
|---|---|---|---|---|---|
| `datetime_beginning_utc` | string, ISO-8601 without offset (e.g. `2026-11-23T15:00:00`) | No | `datetime_beginning_utc DATETIME2(0)` | **Field [3P]**, format [RECON] | Hour start, UTC. **Key component.** |
| `datetime_beginning_ept` | string, ISO-8601 without offset | No | `datetime_beginning_ept DATETIME2(0)` | **Field [PJM-DOC]** (19.07: "Datetime UTC and EPT stamps have been added [to] all the feeds"), format [RECON] | **Repeats on fall-back. Never a key.** |
| `fuel_type` | string | No | `fuel_type VARCHAR(50)` | **Field [3P]** | **Key component.** Enum below. |
| `mw` | number | [RECON] | `mw DECIMAL(18,3)` or `FLOAT` | **Field [3P]** | Since 2023-02-16, the **average over the 12 five-minute intervals of the hour**. Before that it was the interval closest to the top of the hour. "Some hourly values may not be the average of full 12 intervals" because of SCED delays. [PJM-DOC] 23.02 |
| `fuel_percentage_of_total` | number | [RECON] | `fuel_percentage_of_total DECIMAL(9,6)` | **LIVE-VERIFIED 2026-11-23** | A **fraction 0..1** (e.g. `0.210`), stored as received. |
| `is_renewable` | boolean (`true`/`false`) | [RECON] | `is_renewable BIT` | **LIVE-VERIFIED 2026-11-23** (JSON bool) | |

**`fuel_type` values** are **[RECON]**. No public PJM source enumerates them. The expected set from the lead's
and common recollection is `Coal`, `Gas`, `Hydro`, `Multiple Fuels`, `Nuclear`, `Oil`, `Other`,
`Other Renewables`, `Solar`, `Storage`, `Wind`. The list is not closed: PJM's eMKT fuel taxonomy "may be subject to change".
**The loader must not validate against a fixed enum.** Store whatever arrives and widen the column if needed.

**History floor:** "earliest date available appears to be 1/1/2016" [3P gridstatus]. `gen_by_fuel` is **not**
among the archived feeds [PJM-DOC 20.01], so a 30-day backfill is safe on retention.

**Posting latency:** hourly, about **12–13 minutes after the hour** (RSS `pubDate` 14:12:34, 15:13:26, 16:12:33 EST
on 2026-11-23) [PJM-RSS]. Which hour each posting fills is [RECON]: probably the hour that just ended.

**Revisions:** not documented. The 23.02 note about partial 12-interval averages suggests a late hour *could*
be recomputed, but nothing says it is. **[RECON]: assume restatable.** The 10-day rolling re-read with MERGE
covers either case.

### 5.2 `inst_load` → `dbo.pjm_load`

"Load MW for zones and areas, calculated from raw telemetry ... approximate. The displayed values are NOT
official PJM Loads." Since release 22.07 (2022-07-28) the source is **Generation Control** system load, not
State Estimator. [PJM-DOC], [PJM-RSS]

| Field | JSON type (expected) | Nullable | Proposed column | Status | Notes |
|---|---|---|---|---|---|
| `datetime_beginning_utc` | string, ISO-8601 without offset | No | `datetime_beginning_utc DATETIME2(0)` | **Field [3P]**, format [RECON] | Interval start, UTC. Since 2023-10-26, timestamps "end on 5s and 0s" (on :00/:05 minute marks) [PJM-DOC 23.10]. **Key component.** |
| `datetime_beginning_ept` | string, ISO-8601 without offset | No | `datetime_beginning_ept DATETIME2(0)` | **Field [PJM-DOC]** (19.07), format [RECON] | **Repeats on fall-back. Never a key.** |
| `area` | string | No | `area VARCHAR(50)` | **Field [3P]** | **Key component.** Enum below. |
| `instantaneous_load` | number | [RECON] | `instantaneous_load DECIMAL(18,3)` or `FLOAT` | **Field [3P]** | MW |
| *(other columns)* | — | — | — | **[RECON]: unknown** | Neither third-party client reads more than these. The `/metadata` call (§11.1) is the only way to get the full set, for example to find a `datetime_ending_*`, an `area_type`, or a zone/region flag. |

**`area` values** [3P gridstatus docstring, list likely predates 2023]: `AE`, `AEP`, `APS`, `ATSI`, `BC`, `COMED`,
`DAYTON`, `DEOK`, `DOM`, `DPL`, `DUQ`, `EKPC`, `JC`, `ME`, `PE`, `PEP`, `PJM MID ATLANTIC REGION`, `PJM RTO`,
`PJM SOUTHERN REGION`, `PJM WESTERN REGION`, `PL`, `PN`, `PS`, `RECO`, plus **`UG`**, added in release 23.10
[PJM-DOC]. That makes about 25 values. The **region and RTO rows are sums of zones**, and 23.10 promised the feed
description would "indicate the region to zone summation". **Never sum all areas.** Zonal load "is not
backfilled before the migration date" [PJM-RSS]. That date is outside any window this loader reads.

**Granularity and latency:** five-minute, posted about **2–4 seconds after each five-minute mark** (RSS `pubDate`
16:15:04, 16:20:02, 16:25:02 EST) [PJM-RSS].

**Revisions:** none documented, and missed intervals are "typically" **not replaced** [PJM-RSS]. A re-read of
the last 10 days is still cheap (about 72k rows) and catches anything PJM does correct.

### ⚠ 5.3 `inst_load` retention: 30 calendar days. A blocker unless the backfill is sized for it.

| Evidence | Source |
|---|---|
| `retentionTime` "30 days" for `inst_load` in the live feed catalogue (`GET /api/v1/`) | **LIVE-VERIFIED 2026-11-23** |
| "Data will be retained for 30 calendar days except dispatch rates, which will be retained for 15 days", in the release that introduced Instantaneous Load as a feed split from the Operational Data page | [PJM-DOC] release 19.07 (2019-07-17) |
| `get_load(date)`: "Must be in last 30 days" | [3P] gridstatus |
| Sibling operational feeds: "Only available in past 30 days" (solar/wind forecast), "past 15 days" (operational reserves) | [3P] gridstatus |

Consequences for the requested design (a 30-day initial backfill, then rolling 10-day re-reads):

1. **A 30-day backfill is AT the edge, not inside it.** Whether "30 calendar days" counts today, whether it
   is EPT- or UTC-day based, and when the purge runs are all [RECON]. **Recommendation:** start the backfill at
   `today − 28` (UTC), run it the day it is enabled, and accept that the oldest 1–2 days may be partial.
   Better still, measure the floor first (§11.6).
2. **A request older than retention almost certainly answers `200` with `totalRows: 0`** [RECON]. That looks
   exactly like a legitimate empty read. Inside the retention window an empty UTC day for `inst_load` is
   **never legitimate**, because the feed posts every five minutes. Outside it, the data is gone. The loader
   should fail a zero-row `inst_load` unit whose date is inside `[today − 25, today − 1]`, and log
   (not fail) one older than that.
3. **The recovery horizon is about 30 days.** An outage longer than the 10-day re-read is recovered only if
   the resume logic re-attempts failed or missing units back to the retention floor. One longer than about 30
   days is permanent loss. The runbook must say so.
4. **The next DST fall-back that can be verified for `inst_load` is 2026-11-01, and it leaves retention around
   2026-12-01.** After that the next one is 2027-11-07. **Run §11.3 before about 2026-11-28.**

---

## 6. DST and keys

* `datetime_beginning_ept` repeats the 01:00–01:59 hour on fall-back (2026-11-01). On `inst_load` that is
  12 duplicated wall-clock timestamps per area, and on `gen_by_fuel` one duplicated hour per fuel type. On
  spring-forward (2026-03-08) the 02:00 hour does not exist. **[PJM-DOC] the field exists; the repeat follows
  from prevailing time.**
* Data Miner reads an EPT filter value in the repeated hour **as standard time**, so an EPT filter cannot
  select the daylight copy [3P gridstatus]. **Filter and sort on `datetime_beginning_utc` only.**
* Store both columns, but key on UTC.

### Natural keys: ✅ **LIVE-VERIFIED 2026-11-23 on the DST fall-back day**

| Table | Key | Evidence | What would still break it |
|---|---|---|---|
| `dbo.pjm_fuel_mix` | `(datetime_beginning_utc, fuel_type)` | 0 duplicates over the full UTC day 2026-11-01 (230 rows; 23 hours because the repeated EPT hour is dropped) | `fuel_type` case or trailing-space variants (the loader fails these as duplicates, compared as SQL will) |
| `dbo.pjm_load` | `(datetime_beginning_utc, area)` | 0 duplicates over the full UTC day 2026-11-01 (7,200 rows = 25 areas × 288). The EPT-based key had **200** duplicates. | Seconds jitter, unless it is truncated to the slot start (the loader does this). A repeated slot on a delayed post fails as a duplicate. |

This repo has had a primary-key outage from a key "verified" on a sample (NUCORE `PositionDetails`) and one from a
local-time key on a DST day (NUCORE SPIP). The PJM check therefore covered a **complete** fall-back day, keyed on
UTC. The loader still fails any in-unit duplicate rather than de-duplicating it, as defence in depth.

---

## 7. Rate limits

| Fact | Source |
|---|---|
| **PJM Members: max 600 data connections per minute** | [PJM-DOC] apiportal.pjm.com widget |
| **Non-members: max 6 data connections per minute** | [PJM-DOC] apiportal.pjm.com widget |
| No `X-RateLimit-*` headers, no documented throttle code | [3P] skill file |
| gridstatus retries on **429** with exponential backoff, so 429 is what it has seen in practice | [3P] |
| Identical queries are served from a ~20-second cache | [3P] gridstatus |

Which tier the **DM2 public key** falls under is [RECON]. Assume the **non-member 6/min** and throttle on the
client to **≤ 5 requests/min across both feeds** (one shared gate, as ICE does for its account-wide 30/min). The
budget is comfortable: a daily run of 2 feeds × 11 UTC days (10 re-read + today) = 22 requests ≈ 4.5 min. The
30-day backfill is 2 × 30 = 60 requests ≈ 12 min. A wider window per request (e.g. 10 days ≈ 72k `inst_load`
rows, 2 pages) would cut the request count but brings back the cross-page snapshot risk (§4.1). Prefer day
units.

Because the key is shared with every anonymous DM2 web session, the shared pool may throttle the loader even
when it is under its own budget. 429 must be retried, honouring `Retry-After` if present.

---

## 8. Status matrix ([RECON] except where marked)

| Status | Body | Meaning | Loader behaviour |
|---|---|---|---|
| `200` | `items` non-empty, `totalRows` > 0 | Rows | Merge, after asserting `totalRows` is consistent across pages and equals rows read |
| `200` | `items: []`, `totalRows: 0` | `gen_by_fuel`: a **future** hour, or a not-yet-posted current hour. `inst_load`: a future interval **or a date past the 30-day retention**. | **Success, 0 rows**, except a past `inst_load` UTC day inside the retention window, which **fails** (§5.3) |
| `200` | contains `errors` | Query rejected in-band [3P] | **Throw, no retry.** Carry the error text, redacted. |
| `400` | [RECON] | Malformed filter, an unknown field in `fields` or `sort`, `rowCount` > 50,000, or a span over 365 days | **Throw, no retry** |
| `401` | [RECON] empty or APIM JSON | Missing, wrong or **rotated** key. **The missing-key case is established by the lead.** | **Re-fetch `settings.json` once and retry** (PJM may have rotated the public key); a second 401, or any 401 while an ARM override is in use, fails the run. The message never contains the value. |
| `403` | [RECON] | Key not entitled, or quota exceeded (APIM) | **Throw, no retry.** Distinguish it from 401 in the message. |
| `404` | [RECON] | Bad feed path | **Throw.** The loader built a bad URL. |
| `429` | [RECON] (seen by [3P]) | Throttled | **Retry** with backoff, honour `Retry-After` |
| `408` / `500` / `502` / `503` / `504` | [RECON] | Transient | **Retry.** Re-check after §11.5: on NUCORE and EIA, 500 turned out to mean "malformed request". |

The repo invariant is that a legitimate empty read and a malformed request must not share a status. If §11.5
shows a malformed filter answers **200 with zero rows** instead of 400 (as several vendors here do), the loader
must validate its own request shape and echo-check returned timestamps against the requested window, like
NUCORE's date-echo guard.

Redaction: the key travels in a header, never the URL, so URL logging is safe. Use `RemoveAllLoggers()` on the
`HttpClient` anyway, and send all error text through `ErrorTextRedactor` (APIM error bodies have been known to
echo header names).

---

## 9. Correction horizon (user default: DaysBack=10 / SettledAfterDays=10)

| Feed | Proposal | Reason |
|---|---|---|
| `gen_by_fuel` | `DaysBack=10`, `SettledAfterDays=10`, initial backfill 30 days | Restatement is undocumented (§5.1). The default horizon is the hedge. |
| `inst_load` | `DaysBack=10`, `SettledAfterDays=10`, initial backfill **≤ 28 days** (§5.3). Failed or missing units are re-attempted back to the retention floor. | The hard floor is retention, not revision |

Today's UTC day is always hot. A same-day re-read inside about 20 seconds of a previous identical query may be
served from cache [3P]. The run cadence (≥ 1 h) makes that moot.

---

## 10. Open questions

1. The full `inst_load` field list. Only `datetime_beginning_utc`, `datetime_beginning_ept`, `area` and
   `instantaneous_load` are evidenced.
2. Whether `fuel_percentage_of_total` exists at all, and its scale. No public source names it.
3. The `fuel_type` enum and the JSON type of `is_renewable`.
4. The timestamp string format: ISO with no offset, a trailing `Z`, or something else.
5. Whether the upper bound is inclusive. Third-party evidence says yes.
6. The exact `inst_load` retention floor (calendar-day basis, purge time), and whether a request past it is
   `200`/empty or an error.
7. The rate tier of the DM2 public key, and the throttle response code and body.
8. Status codes for a malformed filter, an unknown field, `rowCount` > 50,000, and a span > 365 days.
9. The semantics of `isActiveMetadata=true`.
10. Envelope property casing (`totalRows` vs `TotalRows`), and whether `searchSpecification` is present.
11. Whether `gen_by_fuel` hours are ever restated after first posting.
12. Key-rotation policy for the public key. Mitigated: the loader re-reads `settings.json` every run and once on a 401.
13. Licensing: the data is internal use only. Confirm that ARM's use of the public DM2 key for an automated
    loader is acceptable under PJM's terms. The apiportal widget says automated queries "require a pjm.com
    account".

---

## 11. Live verification checklist

Run these with the key in a header only, for example `-H "Ocp-Apim-Subscription-Key: $env:PJM_KEY"`, **never on
the command line history or in a URL**. Record results back into this document and flip the tags to
**VERIFIED**. `{base}` = `https://api.pjm.com/api/v1`.

**11.1 Metadata and field list (both feeds)**
```
GET {base}/gen_by_fuel/metadata
GET {base}/inst_load/metadata
```
Record every column with its type, nullability, filterable and sortable flags. Diff against §5.1/§5.2 and add
any missing column. Confirm a UTC filter exists for `datetime_beginning_utc`.

> ✅ **2026-11-23: field lists SETTLED.** Both feeds carry exactly the columns in §5.1/§5.2; none are missing and
> none are extra. The UTC filter works.

**11.2 Payload shape (both feeds), one complete UTC day**
```
GET {base}/gen_by_fuel?rowCount=50000&startRow=1&sort=datetime_beginning_utc&order=Asc&datetime_beginning_utc=11/20/2026 00:00to11/20/2026 23:59
GET {base}/inst_load?rowCount=50000&startRow=1&sort=datetime_beginning_utc&order=Asc&datetime_beginning_utc=11/20/2026 00:00to11/20/2026 23:59
```
Record the envelope property names and casing, `totalRows`, the timestamp string format, the JSON type of every
field, the distinct `fuel_type` and `area` values, the `fuel_percentage_of_total` scale, the `is_renewable` type,
and any NULLs. Repeat `inst_load` with and without `isActiveMetadata=true` and diff the results.

> ✅ **2026-11-23: mostly SETTLED.**
> - Envelope: `items`, `totalRows`, `links` (camelCase). The response is gzip when requested.
> - Timestamps: `yyyy-MM-ddTHH:mm:ss`, with no zone and no fraction.
> - `inst_load` timestamps carry **0–2 s of seconds jitter**.
> - `fuel_percentage_of_total` is a fraction 0..1, and `is_renewable` is a JSON bool.
> - ⬜ Still open: the `isActiveMetadata` diff, and recording the distinct `fuel_type`/`area` lists.

**11.3 Key uniqueness: complete days, their union, and the DST fall-back** ⏰ **`inst_load` 2026-11-01 must be
read before about 2026-11-28**
- Read the UTC days **2026-11-01 and 2026-11-02** (the fall-back is 2026-11-01 06:00 UTC; the EPT 01:00 hour
  repeats), plus at least three other complete days, e.g. 2026-11-18, 19 and 20.
- For each day, and for the **union** of all days, count duplicates of `(datetime_beginning_utc, fuel_type)` and
  `(datetime_beginning_utc, area)`. Compare as SQL will: trim, case-insensitive, and timestamps truncated to the
  target precision.
- On 2026-11-01, confirm there are 25 distinct EPT hours' worth of rows and that `datetime_beginning_ept`
  repeats while `datetime_beginning_utc` does not. `inst_load` should have 288 distinct UTC intervals per area on
  every UTC day.
- For `gen_by_fuel`, also check the 2025-11-02 fall-back, since retention allows it.
- **A key is verified only if all of these show zero duplicates.**

> ✅ **2026-11-23: the DST fall-back key check is SETTLED, before the retention deadline.** Over the full UTC day
> 2026-11-01:
> - `(datetime_beginning_utc, area)`: 0 duplicates across 7,200 rows (25 × 288). The EPT-based key had 200
>   duplicates.
> - `(datetime_beginning_utc, fuel_type)`: 0 duplicates across 230 rows.
> - `gen_by_fuel` has **23 hours** that day, because UTC 06:00 (the repeated EPT 01:00) is absent.
> - The keys are accepted as verified.

**11.4 Bound semantics**
```
…&datetime_beginning_utc=11/20/2026 00:00to11/20/2026 01:00     (gen_by_fuel)
…&datetime_beginning_utc=11/20/2026 00:00to11/20/2026 00:05     (inst_load)
```
If the 01:00 row (respectively the 00:05 interval) is present, the upper bound is inclusive. Also try a spaced
`" to "` separator, and an EPT filter across the repeated hour on 2026-11-01, to confirm it returns only one copy.

> ◐ **2026-11-23: consistent with the design.** `00:00to23:59` returned exactly 7,200 = 25 × 288 `inst_load` rows,
> the last at `23:55:01`. The two-row probes above have not been run.

**11.5 Status matrix**
No key (401, already established). A wrong key. A malformed date (`13/45/2026 00:00to…`). An unknown `fields`
column. `rowCount=50001`. A 400-day span. A future day. A bad feed path. For each, record the status and body
shape, with any echoed key redacted. Provoke throttling only gently: 7 cheap requests within 60 s, to record the
throttle status, `Retry-After` and the window.

**11.6 Retention floor (`inst_load`) and history floor (`gen_by_fuel`)**
For `inst_load`, read single UTC days at `today − 27 … today − 33` and record `totalRows` for each. The last
non-empty day and the first empty one bracket the floor. Record whether the floor day is partial and what an
out-of-retention response looks like (status and body). For `gen_by_fuel`, read 2016-01-01 to confirm history,
then pick the backfill start.

> ◐ **2026-11-23:** the live catalogue (`GET /api/v1/`, `retentionTime`) says **"30 days"** for `inst_load`, so the
> `today − 28` cap stands. The exact floor day and the shape of an out-of-retention response are still unmeasured.

**11.7 Paging**
Read one `inst_load` day with `rowCount=1000`, which gives about 8 pages. Confirm `startRow` is 1-based (page 1
starts at row 1, with no overlap or gap at page boundaries). Confirm that `totalRows` is constant across pages and
that rows read equal `totalRows`. Confirm that `links[rel=next].href` matches the explicit next `startRow`. Repeat
without `sort` and compare the row sets.

> ◐ **2026-11-23 (from the §12 work):** a past `very_short_load_frcst` UTC day read as 4 pages at `rowCount=50000`
> reported the **same `totalRows` on every page**. Paging over a past day is therefore stable. The 1-based offset
> and `links` checks above are still open.

---

## 12. Six additional feeds (added 2026-11-23): load actuals and load forecasts

These six feeds use the same base URL, header key, key acquisition, envelope (§4), paging rules (§4.1), status
matrix (§8) and the one shared 5/min limiter as the two original feeds. They are loaded by the same `PJM` loader
into `Misc`, schema `dbo`. The design is in `docs/design/PJM.md` §14.

### 12.0 How it was verified (✅ LIVE-VERIFIED 2026-11-23)

The checks were run against the real API, with the key in a header only:
- the feed catalogue, `GET /api/v1/`: history start, `retentionTime` and posting cadence for each feed;
- `GET /<feed>/metadata`: the field list in metadata order, with types;
- for every feed except `load_frcstd_7_day`, two **complete UTC days**: **2026-11-01** (the DST fall-back) and
  **2026-11-18**;
- for `load_frcstd_7_day`, the current snapshot. The feed has no history to read (§12.7).

These facts hold for all six feeds:

| Fact | Result |
|---|---|
| Timestamp format | ISO `yyyy-MM-ddTHH:mm:ss`, with **no zone and no fraction**. This is the same as §11.2. |
| Metadata `Number` | The value can arrive as an **integer or a decimal** (e.g. `1234` and `1234.5`). |
| Metadata `Boolean` | A JSON bool (`is_verified`) |
| Metadata `String` / `DateTime` | Strings and ISO timestamps, as above |
| Compression | gzip is honoured |
| Envelope | `items`, `totalRows`, `links`, as in §4 |
| Field lists | **Exactly** the lists in §12.2–§12.7, in metadata order. There are no other columns. |

### 12.1 Summary

| Feed | Target table | History since | Retention | Posts | Rows per UTC day (verified) | Filter / sort column | Natural key (0 duplicates on both days) |
|---|---|---|---|---|---|---|---|
| `hrl_load_metered` | `dbo.pjm_load_metered` | 1993 | indefinite | daily 05:30 | **720** = 30 `load_area` × 24 | `datetime_beginning_utc` | `(datetime_beginning_utc, load_area)` |
| `hrl_load_prelim` | `dbo.pjm_load_prelim` | 2011 | indefinite | daily 04:55 | **240** = 10 `load_area` × 24 | `datetime_beginning_utc` | `(datetime_beginning_utc, load_area)` |
| `hrl_load_estimated` | `dbo.pjm_load_estimated` | 1998 | indefinite | **weekly, Tue + Fri 04:00** | **240** on 11-01; only **50** on 11-18 (a partial recent day) | `datetime_beginning_utc` | `(datetime_beginning_utc, load_area)` |
| `load_frcstd_hist` | `dbo.pjm_load_forecast_hist` | 2011 | indefinite | daily 00:05 | **1,991** / **1,980** | `evaluated_at_utc` | `(evaluated_at_utc, forecast_hour_beginning_utc, forecast_area)` |
| `very_short_load_frcst` | `dbo.pjm_load_forecast_very_short` | rolling | ⚠ **30 days** | every 5 min | **172,800** = 288 evaluations × 25 areas × 24 steps | `evaluated_at_utc` | `(evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area)` |
| `load_frcstd_7_day` | `dbo.pjm_load_forecast_7day` | none | ⚠ **"None"**: the latest evaluation only | every 30 min, at :15 / :45 | one snapshot of **4,200** = 25 areas × 168 hours | none (no date filter; sorted on `forecast_datetime_beginning_utc`) | `(evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area)` |

Posting times are as the catalogue states them, which is presumably Eastern prevailing time. The catalogue does
not say which day each posting fills (see V-6 and V-10).

### 12.2 `hrl_load_metered` → `dbo.pjm_load_metered`

| # | Field | Metadata type | Column | Notes |
|---|---|---|---|---|
| 1 | `datetime_beginning_utc` | DateTime | `DATETIME2(0)` | **Key.** Hour start, UTC. **All 24 UTC hours are present on 2026-11-01** (unlike `gen_by_fuel`, §11.3). |
| 2 | `datetime_beginning_ept` | DateTime | `DATETIME2(0)` | Repeats on fall-back. Never a key. |
| 3 | `nerc_region` | String | `VARCHAR(50)` | |
| 4 | `mkt_region` | String | `VARCHAR(50)` | |
| 5 | `zone` | String | `VARCHAR(50)` | |
| 6 | `load_area` | String | `VARCHAR(50)` | **Key.** 30 values. |
| 7 | `mw` | Number | `DECIMAL(18,4)` | Integer or decimal |
| 8 | `is_verified` | Boolean | `BIT` | ⚠ **The data is restated**, from preliminary to verified. The horizon is **unknown** (V-1). |

### 12.3 `hrl_load_prelim` → `dbo.pjm_load_prelim`

| # | Field | Metadata type | Column | Notes |
|---|---|---|---|---|
| 1 | `datetime_beginning_utc` | DateTime | `DATETIME2(0)` | **Key** |
| 2 | `datetime_beginning_ept` | DateTime | `DATETIME2(0)` | |
| 3 | `datetime_ending_utc` | DateTime | `DATETIME2(0)` | Expected to be beginning + 1 h (V-2) |
| 4 | `datetime_ending_ept` | DateTime | `DATETIME2(0)` | |
| 5 | `load_area` | String | `VARCHAR(50)` | **Key.** 10 values. |
| 6 | `prelim_load_avg_hourly` | Number | `DECIMAL(18,4)` | |

240 rows on both days, so all 24 UTC hours are present on the fall-back day.

### 12.4 `hrl_load_estimated` → `dbo.pjm_load_estimated`

| # | Field | Metadata type | Column | Notes |
|---|---|---|---|---|
| 1–4 | `datetime_beginning_utc`, `datetime_beginning_ept`, `datetime_ending_utc`, `datetime_ending_ept` | DateTime | `DATETIME2(0)` | As in §12.3. `datetime_beginning_utc` is a **key**. |
| 5 | `load_area` | String | `VARCHAR(50)` | **Key** |
| 6 | `estimated_load_hourly` | Number | `DECIMAL(18,4)` | |

⚠ **This feed lags by up to about a week.** It posts twice a week (Tue and Fri at 04:00). On 2026-11-23 the day
2026-11-18 held **50 of 240** rows. An **empty or partial recent day is the normal state** of this feed, not an
error. Nothing yet says whether a later post restates days that an earlier post filled (V-6).

### 12.5 `load_frcstd_hist` → `dbo.pjm_load_forecast_hist`

| # | Field | Metadata type | Column | Notes |
|---|---|---|---|---|
| 1 | `evaluated_at_utc` | DateTime | `DATETIME2(0)` | **Key.** The **filter works on this column**: one UTC day of evaluations returns 1,991 / 1,980 rows. The seconds are unverified (V-4). |
| 2 | `evaluated_at_ept` | DateTime | `DATETIME2(0)` | |
| 3 | `forecast_hour_beginning_utc` | DateTime | `DATETIME2(0)` | **Key.** Note the name is `forecast_hour_…`, not `forecast_datetime_…`. |
| 4 | `forecast_hour_beginning_ept` | DateTime | `DATETIME2(0)` | |
| 5 | `forecast_area` | String | `VARCHAR(50)` | **Key** |
| 6 | `forecast_load_mw` | Number | `DECIMAL(18,4)` | |

⚠ **`(forecast_hour_beginning_utc, forecast_area)` alone has 484 duplicates** in one day: several evaluations
forecast the same hour. `evaluated_at_utc` must be part of the key. **A forecast that has been evaluated is
immutable.**

### 12.6 `very_short_load_frcst` → `dbo.pjm_load_forecast_very_short`

| # | Field | Metadata type | Column | Notes |
|---|---|---|---|---|
| 1 | `evaluated_at_utc` | DateTime | `DATETIME2(0)` | **Key.** The filter works on it. The **seconds are always `:00`**, and the minute is on the 5-minute mark (288 a day). |
| 2 | `evaluated_at_ept` | DateTime | `DATETIME2(0)` | |
| 3 | `forecast_datetime_beginning_utc` | DateTime | `DATETIME2(0)` | **Key.** 24 steps per evaluation. Their spacing is **inferred** as 5 minutes, i.e. a 2-hour horizon (V-3). |
| 4 | `forecast_datetime_beginning_ept` | DateTime | `DATETIME2(0)` | |
| 5 | `forecast_datetime_ending_utc` | DateTime | `DATETIME2(0)` | |
| 6 | `forecast_datetime_ending_ept` | DateTime | `DATETIME2(0)` | |
| 7 | `forecast_area` | String | `VARCHAR(50)` | **Key.** 25 values. |
| 8 | `forecast_load_mw` | Number | `DECIMAL(18,4)` | |

- ⚠ **Retention is 30 days**, the same exposure as `inst_load` (§5.3). Data older than that is gone permanently.
- **Volume:** 172,800 rows per UTC day, i.e. **4 pages** at `rowCount=50000`. `totalRows` was stable across the
  pages of a past day.
- Immutable once evaluated.
- A **6-hour** `evaluated_at_utc` window holds 72 × 25 × 24 = **43,200 rows, one page**. The design relies on this
  (design §14.3), and it is confirmed live under V-5.

### 12.7 `load_frcstd_7_day` → `dbo.pjm_load_forecast_7day` (a SNAPSHOT feed)

| # | Field | Metadata type | Column | Notes |
|---|---|---|---|---|
| 1 | `evaluated_at_datetime_utc` | DateTime | `DATETIME2(0)` | **Key.** Note the name is `evaluated_at_datetime_…`, not `evaluated_at_…`. It has **REAL, non-zero seconds** (e.g. `22:47:12`) and **must not be truncated**. |
| 2 | `evaluated_at_datetime_ept` | DateTime | `DATETIME2(0)` | |
| 3 | `forecast_datetime_beginning_utc` | DateTime | `DATETIME2(0)` | **Key.** 168 hourly steps. |
| 4 | `forecast_datetime_beginning_ept` | DateTime | `DATETIME2(0)` | |
| 5 | `forecast_datetime_ending_utc` | DateTime | `DATETIME2(0)` | |
| 6 | `forecast_datetime_ending_ept` | DateTime | `DATETIME2(0)` | |
| 7 | `forecast_area` | String | `VARCHAR(50)` | **Key.** 25 values. |
| 8 | `forecast_load_mw` | Number | `DECIMAL(18,4)` | |

⚠⚠ **Retention is "None": the feed holds ONLY the latest evaluation.**
- One read returned one `evaluated_at_datetime_utc` and 4,200 rows (25 × 168).
- There is no history to backfill, and a date filter has nothing to select.
- **History exists only from what the loader captures.** An evaluation that is not read before the next one
  replaces it (about 30 minutes) is **lost forever**.
- A loader that runs hourly captures about **one of every two** evaluations.
- An **empty snapshot is never legitimate.**

### 12.8 Traps (six feeds)

| # | Trap | Consequence if missed |
|---|---|---|
| 1 | `very_short_load_frcst` keeps **30 days** | The same permanent-loss exposure as `inst_load`. It must run early in the run and be capped at the same `today − 28`. |
| 2 | `load_frcstd_7_day` holds **only the latest evaluation** | A day-filtered or backfill design reads nothing. Every missed evaluation is unrecoverable. |
| 3 | The `load_frcstd_7_day` `evaluated_at` has **real seconds** | Truncating it to the minute (the `inst_load` jitter rule) would merge distinct evaluations, or fail them. |
| 4 | `hrl_load_estimated` **lags up to about a week**, and its recent days are partial | A "zero rows on a past day = failure" rule would fail it every run and trip the run guard. |
| 5 | `load_frcstd_hist`: `(forecast_hour, area)` is **not** unique (484 duplicates a day) | A key without `evaluated_at_utc` causes a PK outage. |
| 6 | `hrl_load_metered` `is_verified` means **restatement with an unknown horizon** | Settling at 10 days could freeze preliminary values permanently. |
| 7 | **The field names are inconsistent across the forecast feeds:** `evaluated_at_utc` vs `evaluated_at_datetime_utc`, and `forecast_hour_beginning_*` vs `forecast_datetime_beginning_*` | Code that uses one shared name for all three gets a missing required field (the unit fails loudly). Mirror each feed exactly. |
| 8 | The hourly actuals (`hrl_*`) keep **all 24 UTC hours** on the fall-back day, unlike `gen_by_fuel` | The completeness expectation is 24 for them, and they need no `DropsRepeatedFallBackHour`. |
| 9 | `very_short_load_frcst` is **4 pages per day**, and data posts every 5 minutes | A multi-page read of *today* races the 5-minute posts, so `totalRows` changes between pages. A single-page window avoids this (design §14.3). |

### 12.9 Still unverified. Run these before enabling, then fold the results into the design.

| # | Check | Closes |
|---|---|---|
| V-1 | `hrl_load_metered` restatement horizon: read single UTC days at ages 10, 20, 30, 45 and 60, and count rows with `is_verified = false` at each age | the metered settle floor (design §14.4, decision D2) |
| V-2 | `datetime_ending_utc − datetime_beginning_utc` on every row of `hrl_load_prelim` / `hrl_load_estimated` (60 min expected); `forecast_datetime_ending_utc − forecast_datetime_beginning_utc` on `very_short_load_frcst` (5 min expected) and `load_frcstd_7_day` (60 min expected) | the end-offset check (design §14.6) |
| V-3 | `very_short_load_frcst`: the step spacing, and that the `forecast_datetime_*` seconds are `:00` | `Slot(5)` on its forecast stamps |
| V-4 | `load_frcstd_hist`: `evaluated_at_utc` seconds, and the number of evaluations per UTC day (1,991 and 1,980 are not a round multiple of 25 areas) | `Exact` vs `Slot` for its `evaluated_at` (§14.6); a completeness rule |
| V-5 | metadata `sortable` / `filterable` flags for `evaluated_at_utc` and `forecast_datetime_beginning_utc`; a 6-hour `evaluated_at_utc` window on `very_short_load_frcst` returns 43,200 rows in one page | the request shapes of design §14.3 |
| V-6 | `hrl_load_estimated`: which days each Tue/Fri post fills, and whether a later post changes days an earlier post filled | the estimated empty band and settle floor (D5) |
| V-7 | `load_frcstd_7_day`: the posting lag relative to `evaluated_at`, and whether re-reading the same evaluation ever returns different values | the immutability assumption behind the MERGE no-op |
| V-8 | `very_short_load_frcst` retention floor, read as in §11.6 (ages 27–33) | `VeryShortMaxDaysBack` |
| V-9 | Whether `nerc_region` / `mkt_region` / `zone` are ever JSON `null` | they are nullable columns either way. This only confirms it. |
| V-10 | `hrl_load_metered` / `hrl_load_prelim`: which day the daily post fills, and at what UTC time a UTC day is complete | `MinRecentHours` (design §14.3) |
