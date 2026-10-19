# EIA loader — design of record

Database **`EIAv2`** · schema **`arm`** · loader id **`EIA`** ·
`src/DataLoader.EIA/` · `sql/EIA/` · field reference `docs/apis/EIA.md`.

---

## 0. Summary, and the six decisions that shape everything

The U.S. Energy Information Administration's API v2 exposes ~2M data series behind one
RESTful tree. This loader pulls the 95 series the business has flagged in `arm.Series` and
lands all of them in **one generic table**, `arm.SeriesData`, as canonical JSON plus two
hashes.

1. **Scope lives in the DATABASE, read through stored procedures.** `arm.Series`,
   `arm.SeriesFrequency` and `arm.SeriesDataType` decide which routes, periodicities and
   data columns are requested. No SQL text is compiled into the binary (§2).
2. **One table for 95 structurally different datasets**, via a facet/data JSON split (§5).
3. **`FacetKey` and `DataHash` are not a free choice** — the columns already exist,
   populated, in the incumbent `dbo.SeriesData`. The algorithm was recovered and verified
   on 20,000 live rows (§6).
4. **Numeric strings are normalised**, which is what the request specifies and what stops
   a vendor type-flip from forking the table (§6.3).
5. **Work units are (series × frequency × period window)** with settled/hot resume keys,
   so the first run backfills ~90M rows and every later run is cheap (§4).
6. **The pipeline streams**, because single windows run to hundreds of thousands of rows
   (§3).

### Gate status

| | |
|---|---|
| Build | ✅ Release, 0 warnings / 0 errors |
| Tests | ✅ 220 EIA tests; 3,665 solution-wide, 0 failures |
| SQL parse | ✅ 4 files, ScriptDom TSql160, clean |
| **Read path** | ✅ **live-verified end to end** — 10,002 rows, 8 frequencies, 11 series |
| **vs the incumbent** | ✅ **`FacetKey` 8,568/8,568 exact, zero misses** |
| SQL deployed | ⛔ **never run.** `arm.SeriesData` does not exist yet |
| Data loaded | ⛔ none |
| In `Platform:EnabledLoaders` | ⛔ no |

---

## 1. Component layout

| File | Role |
|---|---|
| `EiaModule.cs` | DI wiring, `RemoveAllLoggers()`, expensive-config warnings |
| `EiaSettings.cs` | config, bound from `Loaders:EIA` |
| `EiaCatalogRepository.cs` | the only SQL caller — proc names and parameters, no SQL text |
| `EiaApiClient.cs` | URL construction, the throttle, the status matrix |
| `EiaHttp.cs` | 🔒 redaction, retry policy, `EiaRateLimiter` |
| `EiaPeriod.cs` | the six period masks and window arithmetic |
| `EiaResponseParser.cs` | response → rows, and the facet/data partition |
| `EiaCanonicalJson.cs` | the canonical text that is stored AND hashed |
| `EiaLegacyHash.cs` | the pinned 32-bit hash behind `FacetKey` / `DataHash` |
| `EiaWorkUnits.cs` | the unit, and the provider that tiles the axes |
| `EiaSourceReader.cs` | pages one window, streams batches to the sink |
| `EiaSink.cs` | TVP merge into `arm.SeriesData` |
| `EiaPipeline.cs` | the ETL loop |
| `EiaLoadValidator.cs` | post-load observational report |

---

## 2. The catalog is the contract

The loader request asked for stored procedures "so the actual queries are not hardcoded in
the software". Taken literally: `EiaCatalogRepository` names procs and binds parameters,
nothing more.

| Proc | Purpose |
|---|---|
| `arm.usp_GetActiveSeries` | `WHERE Enabled = 1 AND IsDataSeries = 1` — 95 rows |
| `arm.usp_GetSeriesFrequencies @SeriesId` | frequency id + period mask |
| `arm.usp_GetSeriesDataTypes @SeriesId` | the `data[n]=` columns |
| `arm.usp_BulkMergeSeriesData @Records` | the TVP merge |
| `arm.usp_UpdateSeriesEndPeriod @SeriesId, @EndPeriod` | advance-only stamp |
| `arm.usp_ValidateLoad @SeriesId = NULL` | observational anomaly report |

Changing *what loads* is an edit to `sql/EIA/003` and a re-run of that script — never a
rebuild.

### Two facts about the live catalog the procs do not hide

* **`Enabled` is NULL on all 371 `arm.SeriesFrequency` rows and all 390
  `arm.SeriesDataType` rows.** The supplied per-series queries have no `Enabled` filter and
  the procs add none; one would load nothing. The column is still returned so the loader
  can log it.
* **`arm.SeriesDataType.ConduitLastUpdate` does not exist.** The request listed it — that
  is the incumbent `dbo` spelling. The `arm` copies renamed it to **`ModifedAtUtc`**, typo
  included (both `SeriesFrequency` and `SeriesDataType`; `arm.Series` has the correct
  `ModifiedAtUtc`). The procs return the real names.

### `StartPeriod` is read-only, `EndPeriod` is advance-only

`StartPeriod` is **never written** — the request is explicit, and it is also what settled
resume keys are derived from, so changing it would re-tile every axis. If the API reports
an earlier start than the catalog, the loader logs a warning and does not widen the range.

`EndPeriod` is stamped from the API's metadata route, and only ever forward. Advance-only
matters because a partial run, a vendor hiccup, or a forecast series that briefly retracts
its horizon must not walk the column backwards.

---

## 3. Why there is a hand-written pipeline

> **⚠ Addendum, 2026-10-16 — the first full run.** Almost every
> `electricity/rto/daily-fuel-type-data` unit failed with *"HTTP request timed out
> (HttpTimeoutSeconds); the work unit budget was not reached"*. Two independent faults,
> both now fixed; §3.1 and §4.1 record them because each failed in a way that pointed at
> the wrong thing.

### ⚠ 3.1 The per-attempt timeout (the cause of the message above)

A policy registered with `AddPolicyHandler` runs as a `DelegatingHandler` **inside**
`HttpClient.Timeout`, and that timeout bounds the **whole operation** — every retry
attempt and every backoff wait together. `HttpTimeoutSeconds = 120` with `RetryCount = 4`
therefore did not give each attempt 120s; it gave all five attempts 120s *between them*.

On an endpoint whose gateway abandons slow requests at ~60s with a 503/504 — which
`electricity/rto/*` does routinely (`docs/apis/EIA.md` §7.2) — attempt 1 burned 60s,
attempt 2 crossed 120s, and the client timeout fired mid-retry. The surfaced error was a
client timeout, which **hid the fact that the vendor had 504'd** and pointed the
investigation at window size instead.

The fix is the standard one:

* `HttpClient.Timeout = Timeout.InfiniteTimeSpan`;
* a Polly **timeout policy of `HttpTimeoutSeconds`, wrapped by the retry**, so the budget
  is per attempt (`EiaHttp.BuildResiliencePipeline`);
* `TimeoutRejectedException` added to the retry predicate, or a timed-out attempt would
  be abandoned rather than retried.

Nothing can hang: the optimistic timeout cancels through the token `HttpClient` already
flows into the handler, and `WorkUnitTimeoutSeconds` still bounds the unit.
`HttpTimeoutSeconds` dropped 120 → **90**, which brackets the gateway's own cutoff — a
budget far above 60s only delays retrying a request the vendor has already given up on.

⚠ The budgets are coupled: one request's worst case is
`(RetryCount + 1) × HttpTimeoutSeconds` plus backoff = **465s** at the shipped values, so
`WorkUnitTimeoutSeconds` is 3600. `EiaResilienceTests` asserts that inequality — and
that assertion is what caught `EiaSettings` inheriting `LoaderSettingsBase`'s 300s default
while `appsettings.json` shipped 3600, a mismatch that would have cancelled units
mid-retry whenever the config key was absent. The class now sets its own defaults.

## 3.2 The streaming loop

`LoaderPipelineBase` is read-all → transform-all → write-once. One window of
`international` annual is ~557,000 rows and one of `electricity/rto/fuel-type-data` hourly
is ~296,000, with `MaxConcurrentWorkUnits` in flight. `EiaPipeline` keeps the base's exact
structure — `ILoadLogRepository` idempotency, `ParallelRunner` concurrency, per-unit
timeout, the same counters — but streams pages into the sink in `MergeBatchRows` batches.
Same reasoning, same shape, as `OilXPipeline`.

**`RecordsProcessed` is rows CHANGED, not rows read.** The merge is guarded on `DataHash`,
so re-verifying a window where nothing moved reports 0. That is a healthy success.

---

## 4. Work units, windows and resume keys

**Unit = (series × frequency × period window).**

* Not one unit per series: the hourly series is 27.7M rows ≈ 5,540 requests — not
  resumable, reportable, or fit for a timeout.
* Not one unit per period: most series are small and a unit costs a `core.LoadLog` round
  trip either way.

### Window boundaries must not move

Boundaries appear verbatim in the resume key, so they are a pure function of
(`arm.Series.StartPeriod`, the frequency's mask, the configured step count). Nothing in
them depends on today's date, the API's current end period, or a row count.

In particular the final window is **not clamped** to the series' end. Clamping would make
that window's key move every time the series grew. Asking the API for a range that runs
past the available data simply returns fewer rows.

Two things do invalidate settled keys, and both cost a one-off re-download (nothing is
lost — the merge is idempotent): changing `WindowSteps` for that frequency, and changing
`arm.Series.StartPeriod`.

### ⚠ 4.1 Sizing — rewritten 2026-10-16

The original defaults were sized for "a unit is 10⁵–10⁶ rows", which was the wrong target.
What actually matters is **how deep `offset` goes**. A controlled run on an idle API with
no cached URLs (`docs/apis/EIA.md` §7.1) found a **cliff, not a gradient**: offsets of 0,
15,000 and 100,000 all failed **0%** with no latency penalty between them, and offset
400,000 failed **100% (4/4 HTTP 503)**. The old `daily: 365` built 704,880-row windows
needing 141 pages and reaching offset 700,000, so their later pages could never have
succeeded at any timeout.

Defaults are sized so a window is ~25,000 rows ≈ 5 pages, keeping `offset` at or below
~20,000:

| Frequency | Steps | ≈ | note |
|---|---|---|---|
| `annual` | 25 | 25 years | widened — see §4.3 |
| `quarterly` | 40 | 10 years | widened |
| `monthly` | 60 | 5 years | widened |
| `weekly`, `four-week-average` | 1825 | 5 years of days | series are tiny |
| `daily` | 12 | 12 days | ⚠ **frozen** — 9.7M rows settled under it |
| `hourly`, `local-hourly` | 48 | 2 days | ⚠ **frozen** — 24.1M rows settled under it |

### ⚠ 4.3 The second sizing pass, 2026-10-16 — density is the wrong target on its own

The first pass sized every frequency for its DENSEST member, which over-fragmented the
sparse ones. Measured on the real backfill:

| frequency | axes | units at first-pass sizing |
|---|---|---|
| `daily` | 7 | 4,593 |
| **`monthly`** | 49 | **4,398** |
| `hourly` | 1 | 1,460 |
| `local-hourly` | 1 | 1,460 |
| `annual` | 77 | 703 |
| everything else | 19 | 141 |
| | | **12,755 total** |

`monthly` and `annual` together were **40% of the backfill** across 126 mostly-tiny axes,
most fetching a few thousand rows in a single request — a `core.LoadLog` round trip and a
unit of scheduling overhead each. That was the right trade *before* `MaxRowsPerWindow`
subdivision existed. Now that a too-big window splits itself (verified live on a
704,880-row window), the sparse frequencies can be wide and the dense outliers subdivide:
`monthly` 6→60, `annual` 5→25, `quarterly` 12→40, cutting **~4,550 units**.

⚠ **`daily` and `hourly` were deliberately left alone.** 9.7M and 24.1M rows are already
loaded under their boundaries; widening them would invalidate those settled keys and
re-download everything. Window sizing is not a free knob once data exists.

### ⚠ 4.4 Throughput collapses under sustained load

The burst experiment in §4.2 ran 12-request cells against an idle API and found no
concurrency effect. A real multi-hour backfill behaves differently:

| 30-min bucket | units completed | failures | avg unit |
|---|---|---|---|
| +0:30 | 38 | 0 | 21s |
| +1:00 | 321 | 0 | 22s |
| +1:30 | 252 | 0 | 26s |
| +2:00 | 272 | 0 | 28s |
| +2:30 | 237 | 0 | 29s |
| **+3:00** | **13** | 0 | **250s** |

Throughput fell ~20× and per-unit time rose 11×, with **zero failures** — the retries
absorbed everything, so the only visible symptom was the log filling with 504s. Errors
shifted from 503 (backend) to mostly 504 with some 502, consistent with the gateway
giving up rather than the origin.

Two things follow:

* **A burst benchmark cannot clear a loader of load problems.** §4.2's "concurrency is
  fine" result holds for short bursts and says nothing about hour three.
* **The backfill is resumable at zero cost**, so the right response to degradation is to
  stop and resume off-peak rather than grind. Settled units are skipped before any
  request is made; the run that collapsed had 1,133 units permanently banked.

**Density varies by orders of magnitude *within* a frequency** — `international` annual is
55,711 rows/year against `coal/market-sales-price`'s 156 — so no per-frequency number fits
everything. Two mechanisms close the gap:

* **`WindowStepsBySeries`** overrides one axis, keyed `"<SeriesId>:<FrequencyId>"`.
  Shipped with `"international:annual": 1`, the one measured outlier.
* **`MaxRowsPerWindow` (25,000) is the automatic backstop.** The reader reads
  `response.total` from the first response — free, no probe request — and if the range
  holds more, it **splits the range in half and reads each half**, recursively, down to a
  single period. Below that it falls back to offset paging, which is correct for
  `international` annual (87,342 rows in one year, on an endpoint where offset is flat).

Subdivision is **internal to the work unit**: the resume key names the top-level window
and the unit still succeeds or fails as a whole, so idempotency is untouched. It is a
safety net, not the normal path — with the sizes above it should rarely fire, and it costs
one discarded request per split when it does.

Verified live on the series that was failing (2026-10-16): the three 12-day windows that
had timed out now complete in **5 requests each, 0 splits**, with row counts reconciling
**exactly** against the API's own `total`; and a deliberately oversized 365-day window is
rescued by subdivision instead of failing (704,880 rows, 191 requests, 31 splits, exact).

### 4.2 What was ruled OUT as a cause — and what that means for tuning

Same controlled run, 136 cold requests, **only 4 failures — all at offset 400,000**:

| variable | tested over | failures |
|---|---|---|
| concurrency 1 / 2 / 4, forwards then backwards | 72 requests | **0** |
| `sort[0][column]=period` on vs off, with a repeat control | 36 requests | **0** |
| query cost — cheap vs heavy endpoint, both at concurrency 8 | 24 requests | **0** |

So **do not throttle this loader to fix 503s.** Concurrency 4 delivers ~4× the throughput
of concurrency 1 with no extra failures; lowering `MaxConcurrentWorkUnits` or
`RequestsPerSecond` would cost backfill speed and buy nothing. `SortByPeriod` likewise
costs nothing and stays on.

⚠ Two traps for anyone re-measuring this:

* **api-umbrella caches successful responses** — cold ≈7–13s, same URL again ≈0.2s. Any
  benchmark that reuses URLs measures the cache. Assert `X-Cache` has no `HIT`.
* **Latency wanders 7.5s–14.4s run to run on identical workloads with zero failures.** A
  slow cell is not a failing one, and a single pair of cells is not a result — the sort
  "effect" disappeared the moment a repeat control was added.

### ⚠ 4.5 The incremental run, and narrowing the hot tail

Every run regenerates all ~7,000 tiles and asks `core.LoadLog` about each; the skip test
is `WorkUnitKey = @key AND IsComplete = 1`. So a run does three things and nothing else:

1. **re-attempts any unit that failed** (not banked, so not skipped);
2. **re-fetches every HOT window** — its key carries a fresh `;run=<UTC date>` token;
3. **skips everything settled and banked**, before any HTTP request.

Measured 2026-10-17, the steady-state daily cost was **186 hot units ≈ 1.4 hours** at
4-way concurrency:

| frequency | hot units/day |
|---|---|
| annual | **67** |
| monthly | 43 |
| daily | 33 |
| hourly | 24 |
| weekly | 10 |
| four-week-average | 7 |
| quarterly | 2 |

⚠ **Annual dominating is a side effect of §4.1.** Widening the sparse frequencies cut the
backfill from 12,755 units to 6,884, but the FINAL window of every axis ends in the
future and so can never settle — meaning each annual series was re-reading **25 years of
data daily** to catch changes in the current year. The hot tail was as wide as the
window.

`NarrowHotWindowReads` (default on) fixes that: a hot window's read starts at
`max(WindowStart, today − SettledAfterDays)` instead of at the window's start. It rests on
exactly the assumption that already governs settling — nothing older than
`SettledAfterDays` gets revised — so if it is wrong, it is already wrong for every settled
window in the table.

**The key is built from `WindowStart`, never from `ReadStart`**, so narrowing does not
change a window's identity: turning it on or off re-downloads nothing and invalidates no
settled key.

⚠ **Three guards, each failing safe toward reading MORE:**

* the feature must be enabled;
* **the window must have completed at least once before.** A first read is always full —
  otherwise the older half would be skipped today, skipped again tomorrow under a fresh
  token, and the window would settle having never loaded it. `EiaLoadHistory` answers
  this from `core.LoadLog` in one query per run, and returns an EMPTY set on any failure,
  which disables narrowing entirely rather than guessing;
* the computed start must fall inside the window, and never past its end.

`EiaLoadHistory` reads the PLATFORM database rather than `EIAv2`, so it carries its own
connection string from `Platform:LoadLogConnectionString`. The alternative — a method on
`ILoadLogRepository` — would have meant a new procedure in the shared, deployed
`sql/Core` scripts for one loader's benefit.

### Settled vs hot

A window whose last period is older than `SettledAfterDays` (default **45**) is SETTLED:
stable key, loaded exactly once ever, skipped by `core.LoadLog` before a single request is
made. That is the entire mechanism that makes run 1 a full backfill and run 2 cheap.

45 rather than OilX's 1 because EIA restates: monthly series revise the previous two
publications and weekly petroleum series revise the prior few weeks. It is deliberately
*not* sized for multi-year revisions — those are picked up with `ReloadAll` on demand,
which is far cheaper than keeping years hot on every run.

A hot window's key carries a run token (`RunDate` by default). An unmapped strategy
**throws** rather than falling back to a stable key, because that fallback would freeze the
hot zone and silently stop catching the revisions it exists for.

Forecast series fall out correctly: `steo` publishes to `2027-Q4`, those windows are in the
future, so they are never older than the horizon and stay hot.

`AllowIncremental = 0` on a series makes all its windows hot, as `ReloadAll` does globally.
**NULL means "use the configured default" (true), never false** — the column is NULL on
every row, and reading it as false would put all 95 series into full-reload mode.

### The metadata pass

The provider calls the metadata route once per series (~95 requests at the head of a run).
Accepted because it is the only authority for how far a series goes — "today" is not an
upper bound for a forecast series — and because the request requires `EndPeriod` to track
the latest available period. Against tens of thousands of data requests it rounds to zero.
A metadata failure is logged and the series falls back to the catalog's own `EndPeriod`
rather than failing the run.

---

## 5. The facet/data partition

Each field of a data row goes to exactly one place:

| Field | Destination |
|---|---|
| `period` | the `Period` column, verbatim |
| a `DataTypeId`, a `<DataTypeId>-units`, or the bare `units` | `DataJSON` |
| anything else | `FacetJSON` |

**Verified, not assumed**: checked against one sampled row from each of the 134 series in
the incumbent — 134/134 had every `DataJSON` key inside that set.

Two spellings matter. `natural-gas/*` and `petroleum/*` name their units column plainly
`units`. The SINGULAR `unit` / `unitName` are **facets** — `international` carries both
alongside a `value` data field.

One incumbent row set disagrees, and it is a Conduit defect rather than a rule:
`electricity/rto/daily-region-data` rows refreshed 2026-06-27 put `value-units` in the
FACET object while rows refreshed 2026-03-30 put it with the data. Moving a field between
the two objects changes `FacetJSON`, hence `FacetKey`, hence the primary key — so that
defect **forked those rows**. The API sends it as a data-units field; this loader always
places it there.

---

## 6. ⚠ `FacetKey` and `DataHash`

### 6.1 The algorithm, and where it came from

Both columns already exist, populated, in `dbo.SeriesData`, and the loader request supplies
a worked example that must be reproduced exactly. Candidates were tested against the stored
values; SQL `CHECKSUM` / `BINARY_CHECKSUM`, Java's `h*31+c`, FNV-1a, CRC32 and
MD5-truncation were all ruled out.

The answer is the **non-randomised .NET Framework (x64) `String.GetHashCode`**:

```
hash1 = hash2 = 5381
for each pair of chars:
    hash1 = ((hash1 << 5) + hash1) ^ c[i]
    hash2 = ((hash2 << 5) + hash2) ^ c[i+1]
return hash1 + hash2 * 1566083941
```

Confirmed on a **20,000-row sample of the live incumbent: 20,000/20,000 exact on
`FacetKey` and 20,000/20,000 on `DataHash`**, zero mismatches.

### 6.2 ⚠ Why it is hand-written and never `GetHashCode()`

This platform targets .NET 8, where string hashing is **Marvin with a per-process random
seed**. Calling `GetHashCode()` would give a different `FacetKey` every run: every row
would miss its merge target, and each run would append a fresh copy of ~90M rows instead of
updating in place. The repo already bans seed-dependent hashing for persisted values
(`OilXChecksum`); this is the one place where the *required* algorithm happens to be an old
runtime's internal one, so it is reimplemented in full and pinned by test —
`EiaLegacyHashTests` asserts the worked example, 16 live incumbent vectors, and explicitly
that the result does **not** equal `string.GetHashCode()`.

### 6.3 ⚠ Numeric normalisation — the one deliberate divergence from Conduit

Conduit preserved whatever JSON token type the API sent. EIA changed numbers to strings
between 2022 and 2024, and because the key is a hash of the TEXT, the same business row was
stored **twice** under two keys. The fork is measurable: for `international` 1949 the API
returns 49 rows and `dbo` holds 98.

With `CoerceNumericStrings` (default **true**), a string whose content is a number is
written unquoted using its own digits verbatim. `"44163"` and `44163` both render `44163`,
so a vendor type flip re-keys nothing. This is also exactly what the request specifies.

Setting it false restores Conduit's byte-for-byte behaviour. The live comparison shows the
two modes are complementary, and that **the choice does not affect the key for any series
checked** — `FacetKey` matched 8,568/8,568 in *both* modes:

| Mode | Matches `dbo.DataJSON` for |
|---|---|
| ON (default) | series Conduit captured in the NUMERIC era — `coal/market-sales-price` 143/143, `coal/aggregate-production` 400/400, `densified-biomass` 3/3 |
| OFF | series Conduit refreshed in the STRING era — `natural-gas/cons/heat`, `international`, `rto/daily-region-data`, `hourly`, `steo`, `stor/wkly`, `wpsup`, all 0 diffs |

### 6.4 ⚠ `".43"` — a decimal JSON would reject

`coal/aggregate-production` sends `"productivity":".43"`: 5 of 400 rows for 2001. JSON
requires an integer part. Emitting `.43` unquoted would put invalid JSON in the column and
break `ISJSON`/`OPENJSON`; leaving it a string would make one row in eighty non-numeric.
The loader inserts the `0` by **text surgery, never arithmetic**, so no digit changes, and
re-validates the result against the strict grammar. A trailing dot (`"5."` → `5`) is
handled the same way. A leading `+` and leading zeros are deliberately not repaired:
`"007"` is an identifier, not the number 7.

### 6.5 Canonical form

No whitespace · keys sorted **ordinal** ascending (culture-independent: the key must not
depend on the server's locale) · minimal escaping with the five short control escapes ·
`/` **not** escaped · non-ASCII literal · no facets renders `{}` (hash `1947253782`).

Collision posture: 32 bits over a facet object is a *key*, not a digest. Within one
(SeriesId, FrequencyId, Period) there are at most a few thousand distinct facet
combinations. The merge additionally re-writes `FacetJSON` when the stored text disagrees
with the incoming text under an equal key, so a collision would show as a flip-flopping
row rather than silent corruption. This matches the incumbent exactly, which is the
requirement.

---

## 7. The status matrix

See `docs/apis/EIA.md` §6 for the measured table. The three that matter most:

* **404 = catalog drift, not a blip.** Empty body, no retry; the message says so.
* **403 is never retried** — the gateway rate-limits per key, so hammering it makes a key
  problem worse.
* **A 500 on a `local-hourly` window is probably not transient** — see §5.1 of the field
  reference. The loader cannot produce it (bounds always carry `-00`), but the exception
  names the trap so a future regression is diagnosed in one line.

An empty window (`total: 0`, `data: []`) is a **legitimate empty read** and is recorded as
a success with 0 rows. Most series have frequencies whose earliest windows are simply
empty.

---

## 8. Safety properties

* 🔒 **The api_key is a QUERY PARAMETER** — EIA offers no header form. Three layers: the
  named client has `RemoveAllLoggers()` (the factory's own handlers log raw URIs at
  Information level, which is on by default); nothing in the loader logs a URI; and
  anything derived from an exception goes through `EiaHttp.Redact`, because
  `HttpRequestException.Message` can carry the URI. The key resolves from
  `core.Param(LoaderName='EIA', ParamName='ApiKey')` via the `SEE_DB` sentinel and is never
  in `appsettings.json`.
* 🔒 **One account-wide rate limiter**, registered as a singleton. api.data.gov limits per
  KEY and *suspends the key* when exceeded, which would stop every other consumer of it. A
  per-unit limiter would multiply the real rate by the concurrency.
* 🔒 **`dbo` is live and next door.** `dbo.SeriesData` holds ~94M rows in a table with the
  identical name. Every statement in every EIA script is `arm.`-qualified; `sql/EIA/999`
  drops nothing else, and `EiaTvpContractTests` asserts that every `DROP` in that file
  names an `arm.` object.
* **The catalog tables are INPUT.** `001` does not create them and `999` does not drop
  them; both facts are asserted by test.
* **No delete-by-absence** in the merge. A batch is one window; deleting rows absent from
  it would wipe every other window.
* **The TVP contract is enforced by the build.** `EiaTvpContractTests` parses the real
  `.sql` files and asserts name + order + type + nullability against the sink's DataTable.

---

## 9. Post-load validation

`arm.usp_ValidateLoad` is observational and never changes a run's outcome.
`EiaLoadValidator` logs what it reports and swallows its own failures.

⚠ **Every check is bounded by `SeriesId`**, which leads the clustered primary key, and the
command timeout is `ValidationTimeoutSeconds` (300) rather than `SqlCommand`'s default 30.
Both halves come from Criterion, where one unbounded check in a validator expired at the
default timeout and — because the validator swallows its own failures — took **every other
check in the proc** down with it while the run still reported success.

Checks: `SeriesLoadedNothing`, `FacetJsonNull`, `DataHashNull`, `UnknownFrequency`,
`JsonAtVarcharLimit`, `EndPeriodStale`, `PeriodMaskUnknown`, `DefaultMaskNotSortable`.

`DefaultMaskNotSortable` exists because the advance-only `EndPeriod` comparison is a string
comparison, which is chronological only for the four masks any series currently defaults
to. The local-hourly mask `YYYY-MM-DD"T"HH24TZH` is **not** lexicographically ordered (the
per-row offset varies), and no series defaults to it today. The check makes the day one
does loud rather than silent.

---

## 10. Configuration

`Loaders:EIA` in `src/DataLoader.Host/appsettings.json`. The settings that change
behaviour most:

| Setting | Default | Effect |
|---|---|---|
| `SettledAfterDays` | 45 | the hot zone. 0 means nothing settles and the backfill repeats every run |
| `WindowSteps` | per frequency | ⚠ changing one re-tiles that frequency and re-downloads it once |
| `CoerceNumericStrings` | true | ⚠ flipping it on a populated table is a migration, not a toggle |
| `RequestsPerSecond` | 5 | 🔒 the throttle that keeps the key from being suspended |
| `ReloadAll` | false | ⚠ every window hot — a full ~90M-row re-verify |
| `DefaultAllowIncremental` | true | ⚠ false puts all 95 series into full-reload mode |
| `PageSize` | 5000 | the API's hard ceiling; a larger value is refused at startup |
| `EnabledSeries` | `[]` | targeted backfill / reproducing one series' problem |
| `ExcludedFrequencies` | `["local-hourly"]` | ⚠ skips an entire frequency — see below |

### `ExcludedFrequencies` and why `local-hourly` ships off

`local-hourly` covers the **same instants** as `hourly` for
`electricity/rto/fuel-type-data` — 27.7M rows, 1,460 units, ~29% of the backfill —
differing only in that each row is labelled in its respondent's local offset
(`2026-10-15T02-04`) rather than UTC (`2026-10-15T06`). Excluded by operator decision on
2026-10-16 as duplicate for current needs. Removing it from the list loads it on the next
run; its `WindowSteps` entry is retained so the boundaries are unchanged when it does.

⚠ **This is an operational lever, not the architectural one.** Scope belongs in the
catalog — `arm.SeriesFrequency.Enabled` — and the permanent fix is to populate that
column (NULL on all 371 rows today) and add the predicate to
`arm.usp_GetSeriesFrequencies`. That is a change to the shared catalog and to the query
the loader request specified, which should not happen as a side effect of a throughput
decision.

`EiaModule` warns at the top of a run when `ReloadAll`, `DefaultAllowIncremental = false`
or `SettledAfterDays <= 0` would turn a cheap incremental run into a full re-pull.

---

## 11. Verification status

**Verified live (2026-10-16)**

* All 8 frequencies and 11 series, 10,002 rows, 0 API failures, 0 dropped rows, 0 colliding
  keys.
* `FacetKey` vs the incumbent: **8,568/8,568 exact, zero misses**, in both coercion modes.
* Multi-page read through the real `EiaSourceReader`: `steo` 5,190 rows over 2 pages,
  batches `[5000, 190]`, exact against the vendor's own total.
* Every error path in the status matrix provoked deliberately.
* The hash on 20,000 incumbent rows; the partition on 134 series.

**NOT verified**

* ⛔ **No SQL has been deployed.** `arm.SeriesData`, the TVP and all six procedures exist
  only as scripts. They parse clean under ScriptDom, which is syntax only — not object
  resolution, column counts or type matching.
* ⛔ **Nothing has been merged.** `arm.usp_BulkMergeSeriesData`, the TVP binding, the
  `@@ROWCOUNT` contract and `core.LoadLog` integration are unexercised against a server.
* ⛔ **No full run.** Work-unit enumeration across all 95 series, the ~95-request metadata
  pass, `EndPeriod` stamping and the settled-skip have not run end to end.
* ⛔ Backfill cost (~90M rows) is estimated from `total` counts, not measured.

### To deploy

```text
# 1. in SSMS against ARMH-OPSDB01, in order:
     sql/EIA/001_CreateEiaSchema.sql       -- creates arm.SeriesData only
     sql/EIA/002_CreateEiaTvpTypes.sql
     sql/EIA/003_CreateEiaProcedures.sql

# 2. store the API key (never in appsettings.json):
     core.Param(LoaderName='EIA', ParamName='ApiKey', ParamValue='<key>')
     -- in the DataLoaderPlatform database

# 3. first run, ONE small series, to prove the write path:
     Loaders:EIA:EnabledSeries = [ "densified-biomass/export-sales-and-price" ]
     DataLoader.Host.exe EIA

# 4. widen to all 95 and let the backfill run. Expect it to take several runs:
#    settled windows that complete are never re-fetched.

# 5. add "EIA" to Platform:EnabledLoaders only once the backfill is complete.
```

### Follow-ups

* A `004_CreateEiaComparison.sql` arm↔dbo reconciliation is **not** shipped. The generator
  (`scripts/Generate-ArmToDboComparison.ps1`) reads the live catalog and `arm.SeriesData`
  does not exist yet. When it does, note that a naive compare will report large
  `VALUE_DIFF` counts by design: `dbo` holds duplicate rows from the two forks described in
  §6.3 and §5, and its `DataJSON` text differs from ours for every series the vendor
  restated into strings.
* `local-hourly` doubles the largest series (27.7M rows for the same instants as `hourly`,
  relabelled). It is in the catalog so it loads. If that is not wanted, the lever is
  `arm.SeriesFrequency` — but note `Enabled` is NULL on every row today, so a filter would
  have to be added to `arm.usp_GetSeriesFrequencies` as well as the data populated.
* `arm.SeriesData.FrequencyId` is `VARCHAR(50)` per the supplied DDL while
  `arm.SeriesFrequency.FrequencyId` is `VARCHAR(100)`. The longest id in use is
  `four-week-average` (17), so there is headroom, but the two are not the same width.
