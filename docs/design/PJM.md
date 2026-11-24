# PJM (Data Miner 2 API v1) loader — design & processing flow

Design/flow spec for the PJM loader. Input of record is the field reference at
`docs/apis/PJM.md` (two feeds, envelope, paging, status matrix, retention, DST). **Live verification on
2026-11-23 settled the fields, keys, timestamp format, envelope, gzip and retention.** Status codes, the
rate tier and paging remain unverified. This document is the CODER and
DATABASE_DEVELOPER hand-off. It contains **no code and no SQL**. SQL objects named here are
*proposed* to DATABASE_DEVELOPER, and the open items are collected in §13.

> **Extended 2026-11-23 to EIGHT feeds.** §14 adds six load and load-forecast feeds and generalises the
> descriptor, row, unit and reader. Where §14 conflicts with §0–§13, §14 wins. That covers the execution order,
> the request budget, the SQL object count and the test list. §0–§13 remain the spec for everything §14 does
> not change.

Locked decisions this design is built around (user-approved, not re-opened here):
- **Two feeds** (originally; see §14 for the other six). `gen_by_fuel` → `dbo.pjm_fuel_mix` and `inst_load` → `dbo.pjm_load`, in database
  **`Misc`**, schema **`dbo`**, using Integrated Security.
- **`Misc` does not exist on `ARMH-OPSDB01` yet.** IT creates it, and the SQL scripts never do.
- **Source.** `https://api.pjm.com/api/v1/<feed>`, JSON, header `Ocp-Apim-Subscription-Key`.
- **Key acquisition.** Each run reads `subscriptionKey` from
  `https://dataminer2.pjm.com/config/settings.json`, as the Data Miner 2 website does. An optional
  ARM-owned override in `core.Param` takes precedence (§5).
- **Throttle.** One limiter of **5 requests/min**, shared by both feeds. 429 is retried with
  backoff.
- **Window.** The first run backfills 30 days. Steady state is `DaysBack = 10` /
  `SettledAfterDays = 10`. `inst_load` is clamped inside its ~30-day retention (§3.3).
- **Work unit.** One **UTC day** per feed, filtered on `datetime_beginning_utc`. Paging uses an
  explicit `startRow`/`rowCount`.
- **Keys (LIVE-VERIFIED 2026-11-23** over the full UTC fall-back day 2026-11-01: 0 duplicates in 7,200
  `inst_load` and 230 `gen_by_fuel` rows; the EPT key had 200). `(datetime_beginning_utc, fuel_type)` and
  `(datetime_beginning_utc, area)`, on the **slot-start** timestamp (§4.4), merged with MERGE. A duplicate key inside one unit **fails the unit** and is never de-duplicated.
- **Build-only.** `"PJM"` is **not** in `Platform:EnabledLoaders`.

> **Build-only posture (read first).** This is the same posture AGSI and CWG shipped in.
> Everything is coded, unit-tested and buildable, but the loader is disabled, its SQL is not
> deployed, and no live call is made by the loader in this pass. Enabling it requires all of the
> following, in order:
> 1. IT creates `Misc`.
> 2. The DLP service account is granted rights in it.
> 3. Someone runs §11 of the API doc and folds the results back into both documents.
> 4. 001–003 are run against `Misc` by hand. That is a production write, so it is handed to the
>    user and never run by an agent.
> 5. A release adds `"PJM"` to `EnabledLoaders`, and a `\DLP\PJM` task is created.
>
> ⏰ **Time-critical:** the DST key check of API doc §11.3 must read `inst_load` for 2026-11-01
> **before about 2026-11-28**, when that day leaves retention. The next fall-back that can be
> verified for `inst_load` after that is 2027-11-07. See §12.

Reference implementations mirrored:
- **`src/DataLoader.AGSI/`.** Small REST/JSON loader. Closed per-endpoint pipelines built
  explicitly, `LoaderPipelineBase` used directly, singleton rate limiter + transient delegating
  handler, Polly retry with a per-attempt timeout, `.RemoveAllLoggers()`, and a key sent as a
  **header**.
- **`src/DataLoader.NUCORE/` + `sql/NUCORE/`.** Own database, schema `dbo`, `ModifiedAtUtc`
  default, an `EXCEPT`-based NULL-safe change test in the MERGE, and a proc that returns the
  **changed** row count. It is also the source of the paging lessons: always sort, reconcile rows
  read against `totalRows`, and echo-check every row's date against the request.
- **`src/DataLoader.ICE/Throttling.cs`.** Account-wide per-minute limit shared by every pipeline,
  with the limiter inner of the retry.

---

## 0. Class / structure inventory

New projects are `src/DataLoader.PJM/` and `tests/DataLoader.PJM.Tests/`. Wire them into the
solution and the Host exactly as AGSI is wired.

| Concern | Type(s) | Notes |
|---|---|---|
| Settings | `PjmSettings : LoaderSettingsBase` | §10 |
| Module | `PjmModule : ILoaderModule` (`LoaderId = "PJM"`) | builds both pipelines explicitly (§1) |
| Pipeline marker | `IPjmPipeline : ILoaderPipeline { string FeedId }` | enumerate, toggle, order |
| Pipeline | `PjmPipeline<TRow> : LoaderPipelineBase<PjmDayWorkUnit, TRow, TRow>, IPjmPipeline` | one generic class, two closed instances, identity transform |
| Feed descriptor | `PjmFeed` (immutable record) | `Name`, `BackfillDays` floor, `MaxDaysBack` ceiling, `ExpectRowsWithinDays`, required/optional/known field sets, `ExtraQuery` (§2) |
| Work unit | `PjmDayWorkUnit : WorkUnit` | `(Feed, Day: DateOnly, Zone)`; precomputed `KeyValue` (§3) |
| Provider | `PjmWorkUnitProvider : IWorkUnitProvider<PjmDayWorkUnit>` | one class, constructed per feed |
| **Key provider** | `IPjmKeyProvider` / `PjmKeyProvider` (singleton) | override, then settings.json; one guarded re-fetch on 401 (§5) |
| **Run guard** | `PjmRunGuard` (singleton, per process = per run) | circuit breaker that stops the run on key-level refusals (§6.3) |
| API client | `PjmApiClient` | builds the request, attaches the header, pages, classifies status, handles 401 → re-fetch → retry once (§4, §6) |
| Day reader | `PjmDayReader<TRow>` + `IPjmRowMapper<TRow>` | shared paging/reconciliation/echo/dup logic; per-feed mapper |
| Source readers | `PjmFuelMixSourceReader`, `PjmLoadSourceReader` : `ISourceReader<PjmDayWorkUnit, TRow>` | thin wrappers over `PjmDayReader<TRow>` |
| JSON | `PjmEnvelope` (`items`, `totalRows`, `errors`); items read as `JsonElement` | case-insensitive envelope, item fields mapped by name (§4.3) |
| Rows | `PjmFuelMixRow`, `PjmLoadRow` | no `FileLogId` (§7.3) |
| Sinks | `PjmFuelMixSqlSink`, `PjmLoadSqlSink` : `SqlSinkBase<TRow>` | TVP MERGE; **no de-dup** (§8) |
| HTTP plumbing | `PjmRateLimiter` (sliding 60 s window) + `PjmRateLimitingHandler` + `PjmHttpPolicy` | API client only. The settings.json client is **not** throttled (§1.3). |
| Time | `PjmTime` | UTC run date/hour; Eastern zone resolution for the EPT echo check (§4.4) |

There is **no FileLog hub, no reference provider and no post-load SQL validator** (§7.3, §9).

---

## 1. Module topology & pipeline strategy

### 1.1 One module, two closed pipelines, `inst_load` first

The two feeds share everything except the row type, the field mapping and three retention
numbers. They are still two closed pipelines, as in AGSI and CWG, because `TRow` differs and the
shared generics must never be resolved from DI. Each pipeline is built in a factory closure and
handed to `PjmPipeline<TRow>`.

The feeds have **no data dependency**, but they run **sequentially, `inst_load` first**. Order
matters because `inst_load` is the *perishable* feed (§3.3). If a run is cut short by the
5/min budget, a throttle trip (§6.3) or a timeout, the data that disappears in 28 days should be
the data that got read. `gen_by_fuel` history goes back to 2016, so anything it misses is
recoverable on the next run. Running the pipelines one after the other also keeps the shared
limiter's queue short, so per-unit timeouts are not spent waiting behind the other feed.

### 1.2 `LoaderPipelineBase` directly

Per run there are about 31 + 29 units on a first run, and about 12 per feed on a daily run (§3.4).
That materializes trivially, so there is no windowed orchestrator, chunking or backfill mode. The
vetted per-unit loop is reused unchanged:
1. `BeginAsync` idempotency skip.
2. `ReadAsync`.
3. Identity transform.
4. `WriteAsync`.
5. `CompleteSuccess` or `CompleteFailure`.

The loop runs under `ParallelRunner` at `MaxConcurrentWorkUnits`, with a per-unit timeout, and
one failed unit does not fail the run. The one addition is the run guard (§6.3). After it trips,
every remaining unit fails at once **without an HTTP request**.

### 1.3 DI registration (`RegisterServices`)

1. **Settings.** `services.AddLoaderSettings<PjmSettings>(configuration, Id);`. No property ships as
   `SEE_DB`, so the resolver never touches the store for PJM (§5.2). The call still registers the
   shared `ISeeDbParamStore` through `TryAddSingleton`, and the key provider uses that store.
2. **Throttle.** `PjmRateLimiter` (singleton) + `PjmRateLimitingHandler` (transient).
3. **Named `HttpClient` `"PJM"`**, used for the data API:
   - `client.Timeout = Timeout.InfiniteTimeSpan`. The budget is per attempt, inside the policy (the
     EIA/IHSPointLogic lesson; copy AGSI's comment).
   - `Accept: application/json`.
   - `.RemoveAllLoggers()`. The key is a header, so the URL is not secret, but the factory's
     default handlers also log headers at Trace. Removing them keeps the log surface identical to
     AGSI/OilX.
   - 🔒 **Redirects are never followed.** The primary handler is a `SocketsHttpHandler` with
     `AllowAutoRedirect = false`, because HttpClient forwards custom headers, and so the key, to
     a redirect target. `PjmApiClient` treats any 3xx as a non-retried **contract** failure. The
     message is redacted and quotes the `Location` path only.
   - Handler order, outer → inner:
     1. **retry** (`PjmHttpPolicy.BuildRetry`);
     2. **limiter** (`PjmRateLimitingHandler`), so every attempt, retries included, is paced;
     3. **per-attempt timeout** (`PjmHttpPolicy.BuildPerAttemptTimeout`), which covers only the
        request, never a limiter wait;
     4. **body buffering** (`PjmBufferingHandler`), so the body is read *inside* the timeout and
        the retry scope. A stalled body times out, and a truncated one surfaces as
        `HttpRequestException`. Both are retried like a stalled request.
   - **The post-401 re-send is ONE attempt.** It carries the `PjmHttpPolicy.SingleAttempt`
     request option, and the retry selector then returns a no-op policy (§5.3, §10.2).
   - **No default `Ocp-Apim-Subscription-Key` header.** The key is attached per request by
     `PjmApiClient` because it can change mid-run (§5.3).
4. **Named `HttpClient` `"PJM.Settings"`**, used for `settings.json` only:
   - `.RemoveAllLoggers()`, no redirects, and the body is buffered inside the timeout.
   - Timeout of `SettingsFetchTimeoutSeconds`, transient retry with a backoff of
     `min(RetryDelayMs, 2000)`, and **no rate limiter**. The fetch targets
     `dataminer2.pjm.com`, not the API gateway, so it does not count against the 5/min (locked
     decision).
   - **The response body contains the key.** It is never logged, never quoted and never put in an
     exception.
5. **Singletons.** `IPjmKeyProvider → PjmKeyProvider` and `PjmRunGuard`.
6. **Pipelines.** Two pipelines, one line each, through a `BuildPipeline<TRow>(feed, mapper, sinkFactory)`
   helper:
   - `inst_load` → `PjmFeeds.InstLoad`, `PjmLoadRowMapper`, `PjmLoadSqlSink`.
   - `gen_by_fuel` → `PjmFeeds.GenByFuel`, `PjmFuelMixRowMapper`, `PjmFuelMixSqlSink`.

### 1.4 `RunAsync`

1. Read `PjmSettings` and build `enabled = HashSet(EnabledFeeds, OrdinalIgnoreCase)`. Warn about
   any enabled id that has no pipeline. If no feed is enabled, warn and return `Success = true`
   **without** fetching the key.
2. **Acquire the key once, before any unit** (`IPjmKeyProvider.InitializeAsync`, §5). On failure,
   log one error (§5.4 wording, never the value) and return `LoaderRunResult.Failed`. With no key,
   every unit would 401, and the run should fail once rather than 60 times.
3. Run `inst_load`, then `gen_by_fuel`, for those that are enabled. Check the cancellation token
   between them. If the run guard has tripped, the second pipeline's units fail fast with the trip
   reason. The pipeline is still executed so `core.LoadLog` records each unit as failed and the
   next run re-attempts it.
4. Aggregate the results with `LoaderRunResult.Aggregate` (sum of totals; `Success` = all
   succeeded). If the guard tripped, log its reason once more at Error at the end of the run, so
   the last log line explains the red run.

---

## 2. Feed descriptors

Retention and expectation behaviour lives on a descriptor, not in `if (feed == …)` branches.
This follows the NUCORE `PublishesOnWeekends`/`MinSettledAfterDays` precedent.

| Descriptor field | `gen_by_fuel` | `inst_load` | Meaning |
|---|---|---|---|
| `Name` (path segment, `FeedId`) | `gen_by_fuel` | `inst_load` | URL path and pipeline id |
| `BackfillDays` (enumeration **floor**) | `settings.BackfillDays` (30) | `settings.BackfillDays` (30), then capped by the next row | Units are enumerated back to `today − effectiveDaysBack` (§3.2) |
| `MaxDaysBack` (enumeration **ceiling**) | none | `settings.InstLoadMaxDaysBack` (28) | Vendor retention. Never request beyond it. |
| `ExpectRowsWithinDays` | = effective enumeration depth (every past day) | `settings.InstLoadExpectRowsWithinDays` (25) | A past day this recent that returns 0 rows **fails** (§6.2) |
| `ExpectedPerDay` (warning only) | 24 distinct hours | 288 distinct intervals per `area` | Completeness **warning** threshold; never fails (§4.5) |
| `RequiredFields` | `datetime_beginning_utc`, `datetime_beginning_ept`, `fuel_type`, `mw`, `is_renewable` | `datetime_beginning_utc`, `datetime_beginning_ept`, `area`, `instantaneous_load` | Missing on any item → **fail the unit** |
| `OptionalFields` | `fuel_percentage_of_total` | none | May be absent; absent → NULL |
| `ExtraQuery` | none | `isActiveMetadata=true` when `settings.InstLoadIsActiveMetadata` | Both third-party clients send it ([3P]); semantics are undocumented (API doc §3, §11.2) |

**The `MaxDaysBack` ceiling does not conflict with the "floors, never overrides" rule.** The rule
is that an operator can widen a correction horizon but never narrow one the vendor needs. The
ceiling narrows nothing the vendor can serve. A day older than retention can only return zero
rows, so requesting it would spend the shared 5/min budget on guaranteed-empty answers. The
effective depth is `min(max(DaysBack, BackfillDays), MaxDaysBack ?? ∞)`. That is the floor rule,
followed by a hard vendor limit.

---

## 3. Work units & resume keys

### 3.1 The unit

`PjmDayWorkUnit` has these members:
- `Feed` (`PjmFeed`).
- `Day` (`DateOnly`, a **UTC** calendar day).
- `Zone` (`Recent` | `Hot` | `Settled`).
- `FilterValue`: the precomputed `MM/dd/yyyy 00:00toMM/dd/yyyy 23:59`, invariant culture, literal
  `to`, no spaces.
- `KeyValue`, with `Key => KeyValue`.
- `DisplayName`, e.g. `"PJM inst_load 2026-11-20 (hot)"`.

**Why UTC days, and why `23:59`:**
- A UTC day is always 24 h / 288 intervals, so there is no 23/25-hour DST special case.
- The UTC filter is the only one that can select both copies of the fall-back hour (API doc §6).
- The upper bound is **inclusive** at minute resolution ([3P]). `23:59` captures the 23:55
  interval and the 23:00 hour, and never touches `D+1 00:00`, so adjacent units never overlap.
  If §11.4 shows the bound is actually exclusive, the window is still correct, because nothing is
  stamped `23:59`.

### 3.2 Enumeration (`PjmWorkUnitProvider.GetWorkUnitsAsync`)

```
nowUtc    = context.StartedAtUtc
today     = DateOnly(nowUtc)                                  // UTC calendar day
depth     = min(max(DaysBack, feed.BackfillDays), feed.MaxDaysBack ?? int.Max)
runDate   = today:yyyyMMdd
runHour   = nowUtc:yyyyMMddHH

for D in today down to today − depth:                         // newest first
    base = $"pjm:{feed.Name}:{D:yyyyMMdd}"
    if nowUtc < (D + 1 day at 00:00 UTC) + RecentHours:        // today, and yesterday for the first RecentHours of a UTC day
        zone = Recent;  key = $"{base};run={runHour}"
    else if D > today − SettledAfterDays:
        zone = Hot;     key = $"{base};run={runDate}"
    else:
        zone = Settled; key = base
```

With the shipped settings (10/10/30/28, `RecentHours = 2`) a run at 14:20 UTC on 2026-11-23 builds:

| Feed | Recent | Hot | Settled |
|---|---|---|---|
| `gen_by_fuel` | 11-23 | 11-14 … 11-22 (9 days) | 10-24 … 11-13 (21 days) |
| `inst_load` | 11-23 | 11-14 … 11-22 | 10-26 … 11-13 (19 days, the ceiling at today − 28) |

### 3.3 Why the window is shaped this way (correction-horizon check)

- **The default horizon applies unchanged.** `DaysBack = 10` and `SettledAfterDays = 10` are
  loader-level settings with class defaults of 10 **and** explicit entries in `appsettings.json`,
  and a test pins both. Restatement is undocumented for both feeds (API doc §5.1, §5.2). The
  default horizon is the hedge, and a re-read is cheap (one page per unit).
- **"Backfill 30 days" is an enumeration floor, not a mode.** There is no first-run flag and no
  state. Every run enumerates back to the floor. Days older than `SettledAfterDays` carry a
  **stable** key, so once one succeeds, `core.usp_BeginLoadLog` returns −1 and every later run
  skips it **before any HTTP request**. The steady-state cost of the floor is therefore zero
  requests. The floor has three effects:
  1. The first run backfills automatically and is resumable if it is cut short.
  2. The day that crosses out of the hot zone gets its one final settled read. This is the skill's
     "one final settled read", and it happens at age 10 to 11.
  3. **A unit that failed, or a run that never happened, is re-attempted on every run until it
     succeeds or ages past the floor.** This answers API doc §5.3 point 3. Outage recovery reaches
     back to the retention floor (28 days) for `inst_load`, not just to `DaysBack`.
- **`inst_load` ceiling = 28 days (`today − 28`).** Retention is "30 calendar days" ([PJM-DOC]
  19.07), but three things about it are unknown:
  1. whether it counts today;
  2. whether the day is EPT- or UTC-based (a UTC day D straddles EPT days D−1 and D, offset by
     4–5 h);
  3. what time the purge runs.

  `today − 28` keeps **one full day of margin** for the counting ambiguity and one for the EPT/UTC
  straddle. `today − 29` would already depend on all three being favourable. The cost of being
  conservative is one or two days of the first backfill. The cost of being optimistic is a
  **zero-row settled read recorded as done forever**, or a noisy edge failure every run.
  §11.6 of the API doc measures the real floor. If it shows 29 or 30 is safe, raise
  `InstLoadMaxDaysBack`; no code change is needed.
- **`ExpectRowsWithinDays = 25` for `inst_load`** leaves a three-day "uncertain edge" band (ages
  26–28). In that band a zero-row answer is a **warning plus success**, because the data may
  legitimately have been purged. Younger than that, zero rows is a failure (§6.2).
- **The `Recent` zone is the skill's hourly-cadence rule.** Both feeds post sub-daily:
  `gen_by_fuel` hourly at about :12, and `inst_load` every five minutes. So the
  current UTC day is keyed on the **run hour** and re-read by every run.
  - Yesterday stays `Recent` for the first `RecentHours` (2) of a UTC day. This catches
    `gen_by_fuel`'s 23:00 hour, which posts at about 00:13 UTC. Without it, the hour would wait
    for the next day's hot read.
  - Units are still whole UTC days and never one-per-hour. Days past `Recent` get the daily `Hot`
    key, which serves as the "daily correction pass".
- **Keys never carry the API key**, a URL or a run id: only feed, day, and a date or hour token.

### 3.4 Request budget (check against 5/min before shipping)

Every unit is expected to be **one page**: about 264 `gen_by_fuel` rows or about 7,200
`inst_load` rows, against `rowCount = 50000`.

| Run | `inst_load` | `gen_by_fuel` | Requests | Time at 5/min |
|---|---|---|---|---|
| First run (empty `core.LoadLog`) | 29 | 31 | **60** | ~12 min |
| First run of each UTC day | 1 recent + 9 hot + 1 newly settled (+1 recent yesterday if before 02:00 UTC) | same | **~22–24** | ~5 min |
| Every other hourly run | 1 recent (+1) | 1 recent (+1) | **2–4** | < 1 min |
| **Day total at an hourly cadence** | | | **~70–75** | |

Retries add to this. Every retry is paced by the same limiter. The settings.json fetch (once per
run, plus at most one 401 re-fetch) is outside the budget. The key is shared with every anonymous
DM2 web user, so the pool may throttle the loader even when the loader is within its own budget.
That is what the 429 handling and the trip rule (§6.3) are for.

---

## 4. Source reading (`PjmApiClient` + `PjmDayReader<TRow>`)

Readers implement `ISourceReader` **directly**. They do not use `HttpJsonSourceReaderBase`,
because status classification, the 401 re-fetch and the run guard are loader-specific.

### 4.1 Request

```
GET {BaseUrl}/{feed}?rowCount={PageSize}&startRow={n}&sort=datetime_beginning_utc&order=Asc
                    &datetime_beginning_utc={FilterValue}[&isActiveMetadata=true]
Header: Ocp-Apim-Subscription-Key: <current key>
```

- **`sort`/`order` are always sent.** Unsorted paging silently lost rows on NUCORE, and DM2
  stability without a sort is unknown.
- **No `fields` projection.** Every column is requested so that a new or renamed column surfaces
  (§4.3) instead of being silently ignored.
- **`startRow` is 1-based** and explicit: 1, 1 + PageSize, and so on. `links[rel=next]` is
  **never followed**, because it can replay a stale snapshot ([3P], [PJM-DOC] 20.03).
- `FilterValue` is URL-encoded by `Uri`/`QueryString`. The space in `00:00` becomes `%20`. The
  `to` literal is unchanged.
- **Logging:** the feed, the day and `startRow` at Debug. The full URL is safe to log, since the
  key is in the header, but the reader logs the sanitized path + query only, for parity.

### 4.2 Paging & reconciliation (per unit)

1. `startRow = 1`, `rowsRead = 0`, `firstTotal = null`, `seen = HashSet<key>`.
2. Loop:
   - GET the page.
   - `totalRows` is **required**. If it is absent, throw `PjmContractException("envelope has no
     totalRows")`. This is a contract change, never read as "no rows".
   - If `firstTotal` is null, set it to `totalRows`. Otherwise, if `totalRows != firstTotal`, raise
     a **snapshot mismatch**.
   - Map the items (§4.3) and run the duplicate check (§4.6).
   - `rowsRead += items.Count`.
   - Stop when `items.Count == 0` **or** `rowsRead >= totalRows`.
   - If `items.Count == 0 && rowsRead < totalRows`, raise a **shortfall**.
   - Otherwise `startRow += items.Count`. The loop advances by the rows **received**, not by
     `PageSize`, so a server that caps a page below `rowCount` cannot make the loader skip rows.
3. After the loop: if `rowsRead != totalRows`, raise a **shortfall**.
4. **A snapshot mismatch or shortfall re-reads the whole unit once from `startRow = 1`** (API doc
   §4.1). A second mismatch fails the unit with both totals and both row counts in the message.
   Data posts every five minutes, so a multi-page read can legitimately race a post. One re-read
   absorbs that, and a second one would be masking something.
5. **A duplicate key is not retried** (§4.6). It fails at once.

### 4.3 Field mapping & schema-drift policy

- **Envelope.** Deserialize with `PropertyNameCaseInsensitive = true`. API doc §4 notes
  `totalRows` vs `TotalRows` is unverified, so both are accepted.
  - `items` is required. If it is absent, throw. `[]` is valid.
  - If `errors` is present and non-empty, throw. See §6.1.
- **Items** are read as `JsonElement` objects. Field names are matched **case-insensitively**
  against the descriptor's field sets.
- **Required field missing** on any item → **fail the unit**. The message names the field and the
  feed, never a value. This applies to the feed's `RequiredFields`, listed in §2.
- **Optional field** (`fuel_percentage_of_total`): absent or JSON `null` → NULL column, accepted
  silently.
  - **Decision: include it as a nullable column, and tolerate absence.** It is [RECON] with no
    public source, but the lead recalls it. Including it costs one nullable column, while leaving
    it out would silently discard data if it exists.
  - Its scale (0–1 or 0–100) is unknown. `DECIMAL(9,6)` holds either without a decision, and the
    value is stored **as received**, never rescaled.
  - After §11.2: if the field exists on every row, promote it to Required. If it never appears,
    DATABASE_DEVELOPER may drop the column.
- **Unexpected extra field** (a name in no field set): log a **Warning once per feed per process**.
  The warning lists the extra field **names**, sorted and comma-separated, **never values**, and
  says that "the API doc §11.1 metadata and the DDL may need a new column". Rows still load.
  Static `ConcurrentDictionary<feed, bool>` once-flag.
- **Values:**
  - `datetime_beginning_utc` / `_ept`: ISO-8601 `yyyy-MM-ddTHH:mm:ss[.fff…]`, with **no
    offset**.
    - A trailing `Z` is accepted on the UTC field only. Any other offset → fail (contract change).
    - The value is converted to **whole seconds**. A non-zero fractional part → fail, with the
      field name, because `DATETIME2(0)` would otherwise round two distinct instants onto one key.
    - LIVE-VERIFIED 2026-11-23: the format is `yyyy-MM-ddTHH:mm:ss`, with no zone and no fraction.
    - Whole seconds are then truncated to the **slot start** minute (§4.4 item 3), because
      `inst_load` carries 0–2 s of jitter.
  - `fuel_type` / `area`: JSON string, **non-null and non-empty**, stored **as received**. It is
    **not trimmed** and **not case-folded**. Silent normalization is how two distinct source keys
    become one. Length above the column width (50) → fail with the field name, rather than an
    opaque TVP truncation error.
  - `mw` / `instantaneous_load` / `fuel_percentage_of_total`:
    - Accepted forms: a JSON number, or a numeric string (EIA's number→string flip). Parse with
      invariant culture.
    - `null` → NULL.
    - Anything else, or a value beyond the column's integer digits → **fail**.
    - Extra scale is rounded to the column scale, which is deterministic and so harmless to the
      `EXCEPT` change test.
  - `is_renewable`:
    - Accepted forms: JSON `true`/`false`, the strings `"true"`/`"false"` (case-insensitive), or
      the numbers `1`/`0`.
    - `null` → NULL.
    - Anything else → **fail**. Its JSON type is unverified, so the accepted set is deliberately
      small and explicit.
  - **No `fuel_type` or `area` enum check.** The vendor taxonomy is open (API doc §5.1). Whatever
    arrives is stored.

### 4.4 Echo guards (the NUCORE lesson)

Every row is checked against the request. Any violation **fails the unit**.

1. **Window echo.** `datetime_beginning_utc ∈ [D 00:00:00, D 23:59:59]`. A row outside the day
   means the filter was ignored or misread, and storing it would write another unit's day under
   this unit's key.
2. **UTC/EPT consistency.** `datetime_beginning_ept == ConvertTimeFromUtc(datetime_beginning_utc,
   Eastern)`.
   - **This is the check that proves the key column really is UTC.** If PJM's "utc" were mislabelled
     or shifted, every key would still be unique, but on the wrong instant, and nothing else would
     notice.
   - On the fall-back day, both EPT copies of 01:xx map from distinct UTC instants, so the check
     holds there too.
   - `PjmTime` resolves `America/New_York` or `Eastern Standard Time`. If neither resolves, it
     **fails the run** rather than falling back to UTC: unlike AGSI's enumeration, a UTC fallback
     here would fail every row.
   - Unverified live. If §11.2 shows the EPT field is something other than prevailing wall-clock,
     this check is revised before enabling. It is not downgraded silently.
3. **Slot normalization + interval boundary (LIVE-VERIFIED 2026-11-23).** This step runs **first**,
   before the window echo, the EPT check, the duplicate check and the write.
   - **The jitter.** `inst_load` stamps carry **0–2 s of posting jitter**. On 2026-11-01 there were
     6,075 rows at :00, 1,075 at :01 and 50 at :02. The minute is always a multiple of 5, each area
     has one row per slot, and every area in a slot shares the same seconds.
   - **The rule.** For any feed with `IntervalMinutes` (5 for `inst_load`, 60 for `gen_by_fuel`),
     **both** `datetime_beginning_utc` and `datetime_beginning_ept` are truncated to the minute.
     The **slot start** is what gets keyed and stored, so a re-post with different seconds merges
     onto the same row.
   - **Failures.** The unit fails only when:
     - the dropped seconds exceed `TimestampJitterToleranceSeconds` (default 10, valid 0..59); or
     - the truncated minute is off the interval boundary.
   - **Duplicates.** The duplicate check (§4.6) runs on the truncated value, so two rows in one slot
     still fail.
   - **The bound.** After truncation nothing can sit inside `23:59:xx`, so the inclusive `23:59`
     bound (§3.1) stays lossless. Live, `00:00to23:59` returned all 7,200 = 25 × 288 rows, the
     last at `23:55:01`.
   - `gen_by_fuel` showed 0 jitter, and the same rule applies to it.
   - A failure here is a **data** failure, so it resets the run guard's streak (§6.3).

### 4.5 Completeness (warnings, never failures)

These checks run only on past days (`D < today`) with at least one row:
- **`gen_by_fuel`:** fewer than the expected distinct hours → Warning. Message: `"{feed} {D}: {n}/{expected} hours"`.
  - The expectation is **DST-aware** (LIVE-VERIFIED 2026-11-23). On a fall-back day PJM **drops the
    repeated EPT 01:00 hour**: 2026-11-01 has 23 hours and UTC 06:00 is absent.
  - So `expected = 24 − PjmTime.RepeatedEasternHours(D)`, which is 23 on fall-back and 24 otherwise. The
    descriptor flag is `DropsRepeatedFallBackHour`.
  - `inst_load` keeps all 288 slots on that day, so its threshold is unchanged.
  - No other row-count logic depends on hours per day: the empty-read rule (§6.2) only distinguishes
    zero from non-zero.
- **`inst_load`:** any `area` with fewer than 288 distinct intervals → **one** Warning per unit.
  The message gives the count of short areas, the minimum count, and the first three area names.

They never fail the unit. PJM "typically won't replace the missed intervals" (API doc trap 5), so
a short day can be the final truth. Failing it would make it re-attempt every run until it aged
out, and that wastes the shared key.

### 4.6 Duplicate key inside a unit → FAIL, never de-duplicate

- The key is `(datetime_beginning_utc, fuel_type)` or `(datetime_beginning_utc, area)`, compared
  **the way SQL will compare it**:
  - the timestamp at whole-second precision;
  - the string under `StringComparer.OrdinalIgnoreCase` after **trimming trailing spaces only**,
    which mirrors a CI collation with ANSI padding.
- The check therefore catches a `Gas`/`gas` or `"RECO "`/`"RECO"` pair **before** the
  primary-key violation, with a better message: the feed, the day, the duplicated key, and
  the count of duplicates. The key value is not secret and may be logged.
- If `Misc` turns out to have a case-sensitive collation, the C# check is stricter than SQL. That
  direction is safe: it fails loudly instead of silently.
- **Why fail and not de-duplicate:** this repo has had two PK outages from keys "verified" on
  samples (NUCORE `PositionDetails`, and SPIP on DST). A silent de-dup would either destroy a real
  row or hide that the key is wrong. Neither the sink nor the proc de-duplicates either (§8).

### 4.7 Today and the partial day

`D == today` (always `Recent`) may return any number of rows, including zero. That is success,
because the hour or interval may not be posted yet. No completeness warning runs for it. The
echo, duplicate and contract checks all still apply.

---

## 5. Key acquisition (`PjmKeyProvider`)

### 5.1 Precedence

Evaluated once per run, in `InitializeAsync`:

1. **Environment override:** `ApiKeyOverride` (set only through the environment variable
   `DATALOADER_Loaders__PJM__ApiKeyOverride`). It is used when it is non-blank **and not the
   literal `SEE_DB`**. Org policy forbids putting the value in `appsettings.json`, and a test
   asserts the shipped file has no `ApiKeyOverride` value.
2. **`core.Param` override:** `ISeeDbParamStore.GetParam("PJM", "ApiKey")`. It is used when the
   call returns a **non-blank** value. `null` (no row) or blank means "no override". This is for
   when ARM registers its own PJM key: insert the row and the next run uses it, with no release
   and no config change.
3. **Fetched public key:** GET `SettingsUrl`, parse JSON, read `subscriptionKey` (case-sensitive
   property name, string, non-blank).
   - `baseUrl` is also read, **only to compare**. If it differs from the configured `BaseUrl`,
     log one Warning naming both URLs (they are public, not secret). The configured URL is
     always the one used.

The provider records **which source** won as an enum (`EnvOverride` / `ParamOverride` /
`PublicSettings`) and logs that name at Information. The value is never logged.

### 5.2 How SEE_DB behaves when the row is absent, and why the override bypasses it

Read from `SeeDbSettingsResolver<TSettings>`:
- The resolver post-configures every writable `string` property whose bound value is exactly
  `SEE_DB`.
- For each, it calls `core.usp_GetParam`. That proc returns **no row** when the pair is absent, and
  `SqlParamStore.GetParam` then returns `null`.
- **The resolver then throws** `InvalidOperationException("… set to SEE_DB but core.usp_GetParam
  … returned no value")`. It does this the first time `IOptions<PjmSettings>` is materialized,
  which is before `RunAsync` gets control (AGSI design §1.4 documents the same trap).

So **"absent means use the fetched key" cannot be expressed through the `SEE_DB` sentinel.**
Shipping `ApiKeyOverride: "SEE_DB"` would make every run fail until ARM has its own key. The
design therefore:
- ships **no `SEE_DB` property at all** in `PjmSettings`. The resolver finds no sentinel and never
  touches the store, and a test pins this;
- has `PjmKeyProvider` call **`ISeeDbParamStore.GetParam` directly**. That interface already
  returns `null` for "no row", so absent cleanly means "fall through to settings.json";
- **treats a store failure as a run failure**, not as "absent": a SQL error, or
  `Platform:LoadLogConnectionString` not configured. An unreadable override must not silently
  switch the loader onto a different identity and quota tier.

### 5.3 401 mid-run: one guarded re-fetch

- `PjmApiClient` reads the key through `IPjmKeyProvider.Current`, which returns `(string Key, int
  Version)`.
- On **401**, it calls `RefreshAsync(seenVersion)`:
  - The refresh is serialized with a `SemaphoreSlim`. Only the **first** caller whose
    `seenVersion == Version` re-fetches settings.json, and it bumps `Version`. Concurrent units
    that saw the same version wait and reuse the result, so one rotation causes **one** re-fetch.
  - A run allows **at most one** refresh (`MaxRefreshesPerRun = 1`).
- The client then **re-sends the same request once** with the new key. The re-send is paced by the
  limiter.
  - It is a **single attempt, not a Polly retry cycle**. The `PjmHttpPolicy.SingleAttempt` option
    makes the retry selector return a no-op policy.
  - A transient failure on the re-send fails the unit, and the next run re-attempts it. This
    bounds the unit budget (§10.2).
- **A second 401** fails the run: on the re-sent request, or any 401 after the one refresh was
  spent.
  - The run guard trips with the reason `"PJM refused the subscription key twice (HTTP 401)"`.
  - The message adds: `"the Data Miner 2 public key in {SettingsUrl} may have been rotated or
    withdrawn, or the core.Param(PJM, ApiKey) override is invalid"`.
  - The current unit fails, and every later unit fails without an HTTP request.
- **When an override is in force** (`EnvOverride` or `ParamOverride`), a 401 does **not** re-fetch
  settings.json, and it does **not** fall back to the public key. It trips the guard immediately
  with the reason `"the ARM-registered PJM key (core.Param PJM/ApiKey or
  DATALOADER_Loaders__PJM__ApiKeyOverride) was refused (HTTP 401); update it, or delete the row
  to fall back to the public Data Miner 2 key"`. Switching identity silently would hide a
  credential problem ARM owns.
- 401 is **not** in the Polly retry predicate. The handling above is the only retry it gets.

### 5.4 Secret hygiene (🔒)

- **The key is never logged, never included in an exception or `core.LoadLog.ErrorMessage`, never
  put in a resume key, and never put in a URL.**
- **settings.json body.** It is never logged or quoted. A parse failure reports only `"settings.json
  had no non-blank string 'subscriptionKey'"` plus the HTTP status. A JSON exception's message
  can carry a fragment of the body, so it is **not** wrapped as an inner exception.
  A new `InvalidOperationException` is thrown instead, with the exception *type name* only.
- **Error text.** Every piece of vendor text quoted into an exception goes through
  `ErrorTextRedactor`, plus an explicit replace of the current key (and the pre-refresh key)
  with `***`. That covers `errors[]` items and any APIM error body. APIM bodies have been known
  to echo header names, and AGSI's vendor echoed the key itself.
- **Headers.** `.RemoveAllLoggers()` on both named clients. No handler logs request headers.
- **Tests** assert all of this with a sentinel key value (§12.2).

---

## 6. Status matrix & run guard

### 6.1 Per response (API doc §8; unknowns marked [RECON])

| Status | Body | Classification | Behaviour |
|---|---|---|---|
| 200 | `items` non-empty | Rows | Map, echo-check, dup-check, reconcile (§4) |
| 200 | `items: []`, `totalRows: 0` | Empty read | See §6.2. Legitimate or failure depending on day and feed. |
| 200 | `errors` present and non-empty | Query rejected in-band [3P] | **Throw, no retry.** Carry the redacted error text (first 3 messages, each truncated to 300 chars). Counts toward the consecutive-failure trip (§6.3). |
| 200 | `items` or `totalRows` absent, or not JSON | Contract change | **Throw** `PjmContractException`, no retry |
| 3xx | any | Redirect | **Contract failure, no retry.** Redirects are never followed, because the key header would be forwarded (§1.3). The message is redacted and gives the `Location` path only. It counts toward the trip as `contract`. |
| 400 | [RECON] | Malformed request | **Throw, no retry.** The request shape is constant, so this counts toward the trip. |
| 401 | [RECON] APIM JSON / empty (missing-key case established) | Key refused | §5.3: one guarded re-fetch + re-send; a second 401 **trips the run** |
| 403 | [RECON] | Key not entitled / quota (APIM) | **Trip the run, no retry.** The message distinguishes it from 401: "refused by entitlement or quota, not authentication". |
| 404 | [RECON] | Bad feed path (the loader built a bad URL) | **Throw, no retry.** Counts toward the trip. |
| 429 | [RECON] (seen by [3P]) | Throttled | **Retry** via Polly, honouring `Retry-After` (delta or date) when it is longer than the backoff. **When retries are exhausted on 429, trip the run** (§6.3). |
| 408 / 500 / 502 / 503 / 504, `HttpRequestException` (including a truncated body), per-attempt timeout (including a stalled body) | [RECON] | Transient | **Retry** with exponential backoff. When exhausted, the unit fails and the run continues, subject to the consecutive-failure trip. |
| any retryable status with `Retry-After` > 60 s | — | Pool wants more patience than a unit has | **Trip the run immediately, no retry.** Retrying early would only deepen the throttle (§6.3). |

⚠ **500 is classified transient only provisionally.** On NUCORE and EIA, 500 turned out to mean
"malformed request". API doc §11.5 must test a malformed filter. If that answers 500, drop 500
from the retry predicate before enabling. Until then, the consecutive-failure trip bounds the
damage: at most 3 units × 4 attempts.

### 6.2 Empty-read rule (legitimate empty vs failure)

`age = today.DayNumber − D.DayNumber`:

| Feed | `age = 0` (today) | `1 ≤ age ≤ ExpectRowsWithinDays` | `age > ExpectRowsWithinDays` |
|---|---|---|---|
| `gen_by_fuel` | Success, 0 rows | **FAIL**: `"gen_by_fuel {D}: 0 rows for a past day; history is continuous since 2016, so the filter or the API is wrong"` | n/a (the threshold equals the full depth) |
| `inst_load` | Success, 0 rows | **FAIL**: `"inst_load {D}: 0 rows inside retention (age {age} ≤ 25); likely a bad filter or a retention change"` | **Warning + success** (ages 26–28, the retention edge, §3.3) |

**Why the failure matters.** A failed past day with a settled key re-attempts on every run, as the
skill requires: "a settled day that reads zero rows from a dataset that always publishes should
FAIL". A success would be recorded done forever.

**The repo invariant** is that a legitimate empty read and a malformed request must not be
indistinguishable. It holds here as follows:
- A malformed filter that answers 200/empty fails, because past days are expected to have rows.
- One that answers 200 with *the wrong rows* fails the window echo (§4.4).
- Today's legitimate empty is the only zero-row success inside the expectation band.

### 6.3 `PjmRunGuard` (stop burning a shared key: skill check 4a)

`PjmRunGuard` is a process singleton with `Trip(reason)`, `IsTripped` and `Reason`. Before **every**
request, `PjmApiClient` checks it. If it has tripped, the client throws
`PjmRunHaltedException(reason)` without sending, so the unit fails fast and is re-attempted
next run. It trips on any of these:

1. A second 401, or any 401 under an override (§5.3).
2. 403.
3. **429 still returned after all Polly retries.** The pool for the public key is exhausted.
   Continuing would fail every later unit and lengthen the throttle for every DM2 user.
   **A `Retry-After` above 60 s on any retryable status** trips immediately, for the same reason.
4. **`ConsecutiveFailureTripCount` (3) consecutive unit failures with the same HTTP status or
   failure class**, counted across both feeds. The classes are:
   - `HTTP 400` and `HTTP 404`;
   - `errors`;
   - `transient-exhausted`: 408/5xx, network, timeouts and truncated bodies, once retries are spent;
   - `contract`, which includes 3xx;
   - **`empty-past-day`**.

   This bounds a deterministic failure the matrix misclassified as transient (the 500 case). It
   also bounds a bad or ignored filter that answers 200/empty for every past day, which would
   otherwise never trip and would hit the public pool every hour.

   Echo, boundary, duplicate and snapshot failures are **data** failures and do not count,
   because they are day-specific. They **reset** the streak, since PJM accepted that request.

The trip reason is logged once at Error when it trips, and again at run end. The run's
`LoaderRunResult` is a failure, so the in-process notifier emails it.

---

## 7. Target tables

### 7.1 `dbo.pjm_fuel_mix` (PK `(datetime_beginning_utc, fuel_type)`, ✅ verified 2026-11-23)

| # | Source field | Row prop | Column | Type | Null? | Notes |
|---|---|---|---|---|---|---|
| 1 | `datetime_beginning_utc` | DatetimeBeginningUtc | `datetime_beginning_utc` | DATETIME2(0) | No | **Key**; hour start, UTC |
| 2 | `datetime_beginning_ept` | DatetimeBeginningEpt | `datetime_beginning_ept` | DATETIME2(0) | No | Repeats on fall-back. **Never a key.** |
| 3 | `fuel_type` | FuelType | `fuel_type` | VARCHAR(50) | No | **Key**; open taxonomy, stored as received |
| 4 | `mw` | Mw | `mw` | DECIMAL(18,4) | Yes | Hourly average of 12 intervals since 2023-02-16 |
| 5 | `fuel_percentage_of_total` | FuelPercentageOfTotal | `fuel_percentage_of_total` | DECIMAL(9,6) | Yes | Optional in code; LIVE-VERIFIED present as a **fraction 0..1** (e.g. 0.210), stored as received |
| 6 | `is_renewable` | IsRenewable | `is_renewable` | BIT | Yes | JSON type unverified (§4.3) |
| — | (proc) | — | `CreatedAtUtc` | DATETIME2(3) | No | `DEFAULT SYSUTCDATETIME()`; set on INSERT only, **never in the UPDATE list** |
| — | (proc) | — | `ModifiedAtUtc` | DATETIME2(3) | No | `DEFAULT SYSUTCDATETIME()`; refreshed only when a value really changed |

### 7.2 `dbo.pjm_load` (PK `(datetime_beginning_utc, area)`, ✅ verified 2026-11-23)

| # | Source field | Row prop | Column | Type | Null? | Notes |
|---|---|---|---|---|---|---|
| 1 | `datetime_beginning_utc` | DatetimeBeginningUtc | `datetime_beginning_utc` | DATETIME2(0) | No | **Key**; 5-minute interval start, UTC |
| 2 | `datetime_beginning_ept` | DatetimeBeginningEpt | `datetime_beginning_ept` | DATETIME2(0) | No | Repeats on fall-back. **Never a key.** |
| 3 | `area` | Area | `area` | VARCHAR(50) | No | **Key**; zones **and** region/RTO sums. Never sum all areas. |
| 4 | `instantaneous_load` | InstantaneousLoad | `instantaneous_load` | DECIMAL(18,4) | Yes | MW; "NOT official PJM Loads" |
| — | (proc) | — | `CreatedAtUtc` | DATETIME2(3) | No | as above |
| — | (proc) | — | `ModifiedAtUtc` | DATETIME2(3) | No | as above |

Further `inst_load` columns are expected but unknown (API doc §5.2, §10 Q1). They are added
after the §11.1 metadata call, **before enabling**, through the normal full-lane DATABASE_DEVELOPER
step. Until then, the reader's extra-field warning names them.

### 7.3 Audit-column convention: what was applied and why

- **`ModifiedAtUtc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()`.** This is the repo's universal
  audit column on fact tables (NUCORE, AGSI, CWG). It is refreshed only when the `EXCEPT` test
  finds a real difference (§8), so it tracks real change, and a no-op re-merge reports 0 changed
  rows.
- **`CreatedAtUtc`: added deliberately; the repo has no precedent on fact tables.**
  Restatement is the one behaviour the 10-day horizon exists to catch, and it is undocumented
  for both feeds (API doc §10 Q11). Comparing `ModifiedAtUtc > CreatedAtUtc` on
  rows older than a day answers "does PJM restate?" from production data at the cost of one column.
  This follows the skill's rule for first-seen columns: insert-only, never in the UPDATE list.
- **No `RecordHash`.** That is CWG-specific, chosen to mirror an incumbent `dbo` table. NUCORE's
  `EXCEPT` compare is NULL-safe and needs no extra column (the skill accepts either).
- **No `FileLogId` and no FileLog hub.** AGSI/CWG keep an `arm.FileLog` per request. NUCORE carries
  a `FileLogId BIGINT NULL` column, but its sink never passes one (it is always NULL), and it has no
  FileLog table. For PJM, one request outcome = one unit = one `core.LoadLog` row, which already
  records key, status, row count and error. A hub would duplicate it in a shared database that IT
  owns.

**No `004` comparison file.** There is no incumbent `dbo` dataset to shadow (same posture as
OilX). The `Generate-ArmToDboComparison.ps1` pair list must not gain a PJM entry.

### 7.4 TVP column contract (NAME + ORDER + TYPE)

The TVPs carry **only source columns**. `CreatedAtUtc`/`ModifiedAtUtc` are set by the table
default or the proc and are never TVP columns (NUCORE/CWG posture).

`dbo.PjmFuelMixType`, 6 columns, in this exact order:

```
1 datetime_beginning_utc    DATETIME2(0)  NOT NULL
2 datetime_beginning_ept    DATETIME2(0)  NOT NULL
3 fuel_type                 VARCHAR(50)   NOT NULL
4 mw                        DECIMAL(18,4) NULL
5 fuel_percentage_of_total  DECIMAL(9,6)  NULL
6 is_renewable              BIT           NULL
```

`dbo.PjmLoadType`, 4 columns, in this exact order:

```
1 datetime_beginning_utc    DATETIME2(0)  NOT NULL
2 datetime_beginning_ept    DATETIME2(0)  NOT NULL
3 area                      VARCHAR(50)   NOT NULL
4 instantaneous_load        DECIMAL(18,4) NULL
```

The same order and types must appear in all of these:
- the `001` table body (source columns first, then the two audit columns);
- the `002` TVP;
- the `003` MERGE `INSERT`/`VALUES`/`EXCEPT` lists;
- the sink's `BuildTable`, whose `DataColumn` types match too (`DateTime`, `string`, `decimal`,
  `bool`, with `AllowDBNull` per the Null column);
- the sink unit test, which asserts names, order and CLR types against a literal copy of the
  lists above (`tvp-contract-check`).

---

## 8. Sinks, procs, concurrency & idempotency

- **Sinks.** `PjmFuelMixSqlSink` and `PjmLoadSqlSink` derive from `SqlSinkBase<TRow>`:
  - `StoredProcedureName` = `dbo.usp_MergePjmFuelMix` / `dbo.usp_MergePjmLoad`.
  - `TableValuedParameterType` = `dbo.PjmFuelMixType` / `dbo.PjmLoadType`.
  - `GetConnectionString` = `settings.ConnectionString` (the `Misc` database).
  - `ProcedureReturnsRowCount = true`.
  - **They do not de-duplicate** (§4.6).
  - One unit is a single batch (≤ ~7,200 rows), and is never split.
- **Procs** (`003`), NUCORE shape:
  1. `SET NOCOUNT ON; SET XACT_ABORT ON;`.
  2. **Duplicate guard:** `IF EXISTS (SELECT key FROM @Records GROUP BY key HAVING COUNT(*) > 1)`
     then `THROW 50001` with a message naming the table and the first duplicated key. This is
     defence in depth behind the reader. The proc must never let a MERGE pick an arbitrary row.
  3. `MERGE dbo.<table> AS t USING @Records AS s ON <key>`.
     - `WHEN MATCHED AND EXISTS (SELECT t.<non-key> EXCEPT SELECT s.<non-key>)` → `UPDATE` the
       non-key source columns + `ModifiedAtUtc = SYSUTCDATETIME()`. `datetime_beginning_ept` is a
       non-key column, so it is compared.
     - `WHEN NOT MATCHED BY TARGET` → `INSERT` the source columns. Both audit columns take their
       defaults.
     - **No `WHEN NOT MATCHED BY SOURCE`.** A row PJM withdraws is not deleted (§13 item 6).
  4. `SELECT @@ROWCOUNT` (changed rows). A re-read that changes nothing records
     `RecordsProcessed = 0`, which is correct.
  5. Table variables only, never `#temp`, because `Misc`'s collation is unknown and may differ from
     `tempdb`'s (the `core.usp_RecordWatchdogPass` lesson).
- **`SqlWriteGate`.** `SqlSinkBase` acquires it, keyed per `{server}/{db}::{proc}`. The two procs
  are two keys. With the feeds sequential (§1.1) and `MaxConcurrentWorkUnits = 2`, only same-feed
  units contend, and that contention is serialized correctly.
- **Overlap.** The platform's `core.usp_TryAcquireLoaderLock` app lock (`DataLoader:PJM`) stops two
  host processes running PJM at once.
- **Idempotency.** Hot and recent re-reads upsert in place. There are no duplicates and
  over-scheduling is safe. The resume key is per UTC day and the MERGE key is per instant + member.
  Because the window echo (§4.4) guarantees every row of unit D lies inside D, a unit can never
  write another unit's rows. That is the invariant AGSI had to retrofit after its clamp incident.

---

## 9. Per-database infrastructure: what lives where

This was established from `sql/Core/001–008`, `SqlLoadLogRepository`, `SqlLoaderOverlapGuard`,
`SqlAdditionalProcessRunner`, `SqlParamStore` and `sql/NUCORE/` (the precedent for an own database
with schema `dbo`).

**Every platform object** lives in **`DataLoaderPlatform`**, reached through
`Platform:LoadLogConnectionString`. **None** is created per loader database. PJM needs nothing
new there:

| Object | Purpose for PJM | Already exists in `DataLoaderPlatform` |
|---|---|---|
| `core.LoaderRun` + `core.usp_BeginLoaderRun` / `usp_CompleteLoaderRun` | run rows, watchdog history | yes |
| `core.LoadLog` + `core.usp_BeginLoadLog` / `usp_CompleteLoadLog` | per-unit resume/idempotency | yes |
| `core.usp_TryAcquireLoaderLock` / `usp_ReleaseLoaderLock` | overlap app lock | yes |
| `core.Param` + `core.usp_GetParam` | optional `PJM`/`ApiKey` override (**no row is needed**; §5) | yes |
| `core.usp_RunAdditionalProcesses` | post-run hook (no-op for PJM) | yes |

**`Misc` needs exactly six PJM objects, all `dbo`.** There is no FileLog, no lookup tables, no
`core` schema and no validator proc:

| Script | Objects |
|---|---|
| `001_CreatePjmSchema.sql` | `dbo.pjm_fuel_mix`, `dbo.pjm_load` |
| `002_CreatePjmTvpTypes.sql` | `dbo.PjmFuelMixType`, `dbo.PjmLoadType` |
| `003_CreatePjmProcedures.sql` | `dbo.usp_MergePjmFuelMix`, `dbo.usp_MergePjmLoad` |

**Access the DLP service account needs in `Misc`**, granted by IT. This is the account the `\DLP\`
scheduled tasks run as on `ARMH-OPSDB01`:
- `EXECUTE` on the two procs;
- `EXECUTE` on the two table types (TVP use requires it);
- `SELECT` on the two tables, for the post-enable data-quality checks.

The procs own the writes, so no table `INSERT`/`UPDATE` grant is needed if ownership chaining
holds, which it does in one schema with one owner. The account already has whatever it holds in
`DataLoaderPlatform`.

**Differences from NUCORE that matter:**
- NUCORE's `001` runs `CREATE DATABASE` if the database is missing. **PJM's must not.**
  - `Misc` is an IT-assigned shared database (org policy).
  - `001` opens with `USE Misc;` + `GO`. If IT has not created it, that statement **fails, which is
    the intended stop**.
- `Misc` is shared ("misc") and may hold other applications' tables. Every PJM object is
  therefore **`dbo.`-qualified and `pjm`/`Pjm`-named**, and `999` drops **only** these six objects.

---

## 10. Config surface: `PjmSettings : LoaderSettingsBase`

Inherited settings, with the PJM values: `ConnectionString` (the `Misc` database),
`MaxConcurrentWorkUnits` (**2**), `RetryCount` (**3**), `RetryDelayMs` (**15000**) and
`WorkUnitTimeoutSeconds` (**900**). Added:

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `BaseUrl` | string | `https://api.pjm.com/api/v1` | Data API base. Always used, even if settings.json says otherwise. |
| `SettingsUrl` | string | `https://dataminer2.pjm.com/config/settings.json` | Public key source (§5) |
| `ApiKeyOverride` | string | `""` | **Environment only** (`DATALOADER_Loaders__PJM__ApiKeyOverride`). Never `SEE_DB` and never a value in appsettings (§5.2). |
| `HttpTimeoutSeconds` | int | 60 | Per-attempt budget (inside the policy) |
| `SettingsFetchTimeoutSeconds` | int | 30 | settings.json fetch, per attempt |
| `EnabledFeeds` | string[]? | `null` → both (`EffectiveEnabledFeeds`) | Feed toggle. Execution order is fixed (`inst_load` first) regardless of list order. The class default is `null` because the config binder **appends** to a pre-populated array (the ICE lesson). An empty or absent list means both feeds; to stop PJM entirely, leave it out of `EnabledLoaders`. |
| `DaysBack` | int | **10** | Correction horizon (skill default; pinned by test) |
| `SettledAfterDays` | int | **10** | Hot/settled boundary (skill default; pinned by test) |
| `BackfillDays` | int | 30 | Enumeration floor, settled-keyed (§3.3) |
| `InstLoadMaxDaysBack` | int | 28 | Retention ceiling for `inst_load` (§3.3); raise after §11.6 if justified |
| `InstLoadExpectRowsWithinDays` | int | 25 | Zero-row failure band for `inst_load` (§6.2). Must be < `InstLoadMaxDaysBack`; validated at startup. |
| `RecentHours` | int | 2 | How long yesterday stays run-hour keyed after UTC midnight (§3.3) |
| `RequestsPerMinute` | int | 5 | Shared sliding-window limit for the data API (§10.1) |
| `PageSize` | int | 50000 | `rowCount`; ≤ 50,000 (validated) |
| `InstLoadIsActiveMetadata` | bool | true | Sends `isActiveMetadata=true` on `inst_load`, as both [3P] clients do; revisit after §11.2 |
| `ConsecutiveFailureTripCount` | int | 3 | Run-guard trip on repeated same-class unit failures (§6.3); ≤ 0 disables |
| `TimestampJitterToleranceSeconds` | int | 10 | Seconds past the minute a slot stamp may carry before the row fails. The seconds are truncated away (§4.4). Valid range 0..59. The class default applies; it is not listed in the shipped `appsettings.json` block. |

**Startup validation** fails the run, with a clear message, when any of these holds:
- `PageSize` is outside 1..50000;
- `RequestsPerMinute` is below 1;
- `InstLoadExpectRowsWithinDays ≥ InstLoadMaxDaysBack`;
- `SettledAfterDays > max(DaysBack, BackfillDays)`;
- `SettledAfterDays > InstLoadMaxDaysBack`, because `inst_load` would never get its final settled
  read;
- `DaysBack < 0`, or `RecentHours` is outside 0..24;
- `WorstCaseUnitSeconds() ≥ WorkUnitTimeoutSeconds` (§10.2).

### 10.1 Limiter

`PjmRateLimiter` is a **sliding-window log**: at most `RequestsPerMinute` sends in any rolling 60
seconds. It keeps a queue of the last N send instants under a lock, and waits until
`oldest + 60 s` (+ a 250 ms edge margin) when the queue is full. The instants are **monotonic**
(`TimeProvider.GetTimestamp` / `GetElapsedTime`), so a wall-clock step cannot open or close the
window.

It deliberately **differs from ICE's and AGSI's minimum-interval spacer**. A 12-second spacer can
admit **six** requests inside one closed 60-second window (t = 0, 12, 24, 36, 48, 60). Against a
documented limit of 6/min ([PJM-DOC]), with a pool shared with anonymous users, the margin
should not hinge on how PJM rounds its window edge.

The limiter is a singleton shared by both pipelines. It sits inside the retry and outside the
per-attempt timeout (§1.3), so a limiter wait never counts against a request's own 60 s. The
settings.json client bypasses it.

### 10.2 Timeout coupling (enforced by startup validation, the EIA lesson)

`PjmSettings.WorstCaseUnitSeconds()` sums these terms for one unit's single read
(`L = 60 × MaxConcurrentWorkUnits / RequestsPerMinute` = 24 s is the limiter wait):
- **Read cycle:** `(RetryCount+1) × (HttpTimeoutSeconds + L)` = 4 × 84 = 336 s, plus
  `Σ max(backoff_i, 60)` = 180 s.
  - The backoffs are 15 + 30 + 60 s. A `Retry-After` can stretch each wait to 60 s at most;
    above 60 s it trips the run instead of waiting.
- **One post-401 re-send:** a SINGLE attempt (no retry cycle), `HttpTimeoutSeconds + L` = 84 s.
- **The settings.json re-fetch** inside the unit: `(RetryCount+1) × SettingsFetchTimeoutSeconds`
  plus its backoff `Σ min(RetryDelayMs, 2000) × 2^(i−1)` = 120 + 14 = 134 s.

With the shipped defaults the total is **734 s, under `WorkUnitTimeoutSeconds` = 900**.
`Validate()` fails the run when the total reaches the unit timeout, so a config change cannot
silently break the coupling.

One snapshot re-read is **not** included, because it is rare. A re-read on top of a throttled
read can still hit the unit timeout. That is acceptable: the unit fails and the next run
re-attempts it.

### 10.3 `appsettings.json` block (ship exactly this; `"PJM"` NOT in `Platform:EnabledLoaders`)

```json
"PJM": {
  "ConnectionString": "Server=ARMH-OPSDB01;Database=Misc;Integrated Security=SSPI;TrustServerCertificate=True;",
  "MaxConcurrentWorkUnits": 2,
  "RetryCount": 3,
  "RetryDelayMs": 15000,
  "WorkUnitTimeoutSeconds": 900,
  "BaseUrl": "https://api.pjm.com/api/v1",
  "SettingsUrl": "https://dataminer2.pjm.com/config/settings.json",
  "HttpTimeoutSeconds": 60,
  "SettingsFetchTimeoutSeconds": 30,
  "EnabledFeeds": [ "inst_load", "gen_by_fuel" ],
  "DaysBack": 10,
  "SettledAfterDays": 10,
  "BackfillDays": 30,
  "InstLoadMaxDaysBack": 28,
  "InstLoadExpectRowsWithinDays": 25,
  "RecentHours": 2,
  "RequestsPerMinute": 5,
  "PageSize": 50000,
  "InstLoadIsActiveMetadata": true,
  "ConsecutiveFailureTripCount": 3
}
```

The block has no `ApiKey` or `ApiKeyOverride` entry, on purpose (§5.2). A test asserts there is no
`SEE_DB` anywhere in the `Loaders:PJM` section.

### 10.4 Watchdog

**Add no entry to `Platform:Watchdog:Loaders` while the loader is build-only.** An entry for a
never-run loader reports OVERDUE (`neverRun: true`) on every hourly pass.

**When enabling:**
- Add `"PJM": { "MaxGapHours": 4 }` in the same release that adds it to `EnabledLoaders`, assuming
  the hourly schedule below. `inst_load`'s 28-day recovery horizon makes the alert a
  hours-scale concern, not a minutes-scale one. Four hours tolerates the 05:00 Central host stall
  (memory: OPSDB01 11:00 UTC) without paging.
- If `DATA_VOLUME` is ever enabled, note that it counts rows **written**. After day one, hot
  re-reads write only real changes, so PJM's daily volume is dominated by new rows: about 264
  `gen_by_fuel` and about 7,200 `inst_load` per day. This is stable enough for the same-weekday
  median. Do not compare it with the first day's backfill volume.

**Schedule recommendation** (task created at enable time, not by this pass): `\DLP\PJM`
hourly at **:20**. That is after `gen_by_fuel`'s ~:12–13 posting, and not on the hour, when
anonymous DM2 traffic, and the shared pool with it, is likeliest to peak.

---

## 11. SQL file list (`sql/PJM/`)

Every file opens with `USE Misc;` + `GO`, after the header comment only (`sql-use-database`). Each
is parse-checked with `scripts\Test-SqlParses.ps1 -Path sql\PJM`.

| File | Contents | Re-runnable? |
|---|---|---|
| `001_CreatePjmSchema.sql` | The two tables of §7.1/§7.2 with named constraints `PK_pjm_fuel_mix`, `PK_pjm_load`, `DF_pjm_*_CreatedAtUtc`, `DF_pjm_*_ModifiedAtUtc`, `IF OBJECT_ID(...) IS NULL`-guarded. **No `CREATE DATABASE`.** Optionally `PRINT`s `DATABASEPROPERTYEX('Misc','Collation')` for §13 item 3. | yes (guarded) |
| `002_CreatePjmTvpTypes.sql` | `dbo.PjmFuelMixType`, `dbo.PjmLoadType` (§7.4), `IF TYPE_ID(...) IS NULL`-guarded | yes (guarded; a shape change needs 999 first, because procs bind the type) |
| `003_CreatePjmProcedures.sql` | `dbo.usp_MergePjmFuelMix`, `dbo.usp_MergePjmLoad` (§8), `CREATE OR ALTER` (AGSI shape). Every file also carries a `DB_NAME() <> N'Misc'` → `SET NOEXEC ON` guard (CWG/NGI 999 precedent), because a failed `USE Misc` in SSMS fails only its own batch | yes |
| `999_DropPjmObjects.sql` | Drops the six objects in dependency order (procs → types → tables), each `dbo.`-qualified and existence-guarded. **Touches nothing else in `Misc`.** The header warns that it destroys loaded data once the loader runs. | yes |

There is no `004` (no incumbent) and no hotfix folder yet.

---

## 12. Tests and live verification

### 12.1 Before enabling: live verification (API doc §11)

Run every item of `docs/apis/PJM.md` §11 with the key in a **header only**. Write the results back
into the API doc (flip the tags to VERIFIED), then fold them into this design.

| API doc § | Closes in this design |
|---|---|
| 11.1 metadata | the full `inst_load` column list (§7.2), and whether `fuel_percentage_of_total` exists (§4.3) |
| 11.2 payload | envelope casing; timestamp format (§4.3); `is_renewable` type; the percent scale; whether the EPT echo (§4.4) holds; the `isActiveMetadata` diff |
| **11.3 keys + DST** ⏰ | **whether the PKs of §7 may be declared as-is.** Read `inst_load` for **2026-11-01 and 2026-11-02 before about 2026-11-28**. Check the union of five or more complete days, compared as SQL compares (§4.6). For `gen_by_fuel`, also check the 2025-11-02 fall-back. |
| 11.4 bounds | the `23:59` window (§3.1) |
| 11.5 status | §6.1, especially whether a malformed filter is 400, 500 or 200/empty, and the throttle status + `Retry-After` |
| 11.6 retention | `InstLoadMaxDaysBack` / `InstLoadExpectRowsWithinDays` (§3.3), and the shape of an out-of-retention response |
| 11.7 paging | 1-based `startRow`, `totalRows` stability, sorted vs unsorted (§4.2) |

The following are also needed before enabling. They are not agent actions:
- §13 item 1 (licensing) is answered;
- IT has created `Misc` and granted the access listed in §9;
- the user has run `001`–`003` against `Misc`, which is a production write;
- a release adds `"PJM"` to `EnabledLoaders` plus the watchdog entry;
- `Generate-Launchers.ps1` and the `\DLP\PJM` task have been set up;
- a runbook `docs/runbooks/PJM.md` exists, covering key rotation (§5.3 message), the 28-day
  recovery horizon, and how to switch to an ARM key (insert `core.Param` PJM/ApiKey).

### 12.2 Test list for CODER / CODE_TESTER (`tests/DataLoader.PJM.Tests`)

**Settings & config**
1. Class defaults `DaysBack == 10` and `SettledAfterDays == 10`, **and** the shipped
   `appsettings.json` `Loaders:PJM` sets both to 10 explicitly.
2. The shipped `Loaders:PJM` section contains no `SEE_DB` and no `ApiKey`/`ApiKeyOverride` value.
   `PjmSettings` has no property defaulting to `SEE_DB`. Running `SeeDbSettingsResolver` with a
   store that throws on any call does not throw, which proves the store is never hit.
3. `"PJM"` is not in `Platform:EnabledLoaders` and not in `Platform:Watchdog:Loaders`.
4. Startup validation rejects `PageSize > 50000`, `InstLoadExpectRowsWithinDays ≥ InstLoadMaxDaysBack`,
   and `RequestsPerMinute < 1`.
5. Timeout coupling inequality (§10.2) holds for the shipped values.

**Work units & keys**
6. Enumeration at a fixed `StartedAtUtc` (14:20 UTC) gives exactly the §3.2 table: 31 / 29 units,
   zones as listed.
7. Settled key = `pjm:{feed}:{yyyyMMdd}`, stable across two runs on different days. Hot key has
   `;run={yyyyMMdd}` and the same value for two runs on one day. Recent key has `;run={yyyyMMddHH}`
   and differs between hours.
8. Yesterday is `Recent` at 01:59 UTC and `Hot` at 02:00 UTC (`RecentHours = 2`).
9. The `inst_load` ceiling holds: `DaysBack = 40` still enumerates nothing older than `today − 28`.
   The floor holds: `DaysBack = 5` still enumerates to `today − 30` for `gen_by_fuel`.
10. `FilterValue` is `MM/dd/yyyy 00:00toMM/dd/yyyy 23:59` under a non-invariant current culture
    (e.g. `de-DE`).
11. No key ever contains the API key (sentinel value), and keys contain no `?`/URL.

**Reader: paging, contract, echo, dup**
12. Single page: rows mapped, `rowsRead == totalRows`.
13. Multi-page (`PageSize = 2`): `startRow` = 1, 3, 5…; stops at `totalRows`; `links.next` is
    ignored.
14. `totalRows` changes between pages → one re-read → success if the re-read is consistent. A
    second mismatch fails.
15. Shortfall (an empty page before `totalRows` is reached) fails after one re-read.
16. `items` absent, `totalRows` absent, or a non-JSON body → contract failure. `TotalRows`
    (PascalCase) is accepted.
17. A required field missing → failure naming the field. A sentinel value in that item does not
    appear in the message.
18. `fuel_percentage_of_total` absent → NULL with no warning; present → stored as received (0.25
    and 25.0 both round-trip).
19. An unexpected extra field → exactly one Warning per feed per process, containing the field
    **name** and not its value.
20. Timestamp: ISO with no offset is accepted, a trailing `Z` is accepted on UTC; `+01:00` fails;
    fractional seconds fail.
21. `is_renewable`: `true` / `"False"` / `1` / `null` are accepted; `"Y"` fails.
22. A numeric string `"123.4"` is accepted for `mw`; `"abc"` fails; an overflow fails.
23. A string longer than 50 characters for `area` fails with the field name.
24. Window echo: a row stamped `D+1 00:00` fails the unit.
25. EPT echo: correct on a normal day, on the **2026-11-01 fall-back** (two UTC instants → the same
    EPT 01:xx wall clock, both pass), and on the 2026-03-08 spring-forward. A shifted EPT fails.
26. **Duplicate key fails the unit and nothing is written.** Exact duplicate; `Gas`/`gas`;
    `"RECO "`/`"RECO"`; the same instant at whole-second precision. The sink is never called.
27. Completeness: 23/24 hours → Warning and success; an `inst_load` area with 287 intervals → one
    Warning and success; no warning for today.

**Status matrix & empty reads**
28. `gen_by_fuel`: 0 rows today → success; 0 rows yesterday → failure; 0 rows at age 30 → failure.
29. `inst_load`: 0 rows today → success; 0 rows at age 1 and age 25 → failure; 0 rows at age 26 and
    age 28 → Warning + success.
30. `errors` in a 200 body → failure, no retry, text redacted (a sentinel key inside the error text
    is replaced by `***`).
31. 400 and 404 → failure, exactly one request (no retry).
32. 500/502/503/504/408 → retried `RetryCount` times, then the unit fails and the run continues.
33. 429 with `Retry-After: 2` → waits ≥ 2 s, then succeeds. 429 on every attempt → the run guard
    trips and later units send **zero** requests.
34. 403 → guard trips, no retry, and the message says entitlement/quota, not authentication.
35. Consecutive-failure trip: three units 400 in a row → guard trips. Three echo failures in a row
    → no trip.

**Key acquisition (fakes for the store and the settings endpoint)**
36. Store returns `null` → the fetched key is used, and the source logged is `PublicSettings`.
37. Store returns a value → that value is used and settings.json is **never requested**.
    Env override beats the store.
38. Store throws → the run fails before any unit, and settings.json is not used as a fallback.
39. settings.json: non-200, malformed JSON, missing or blank `subscriptionKey` → the run fails
    before any unit, and **no part of the body** appears in the logs or the exception (sentinel
    body). A different `baseUrl` → one Warning; requests still go to the configured `BaseUrl`.
40. 401 → exactly one re-fetch → re-send with the new key → success. Two concurrent units both
    getting 401 → still **one** re-fetch.
41. 401 → re-fetch → 401 again → guard trips, the run fails, and the message names settings.json
    and `core.Param(PJM, ApiKey)`.
42. 401 under an override → no re-fetch, the guard trips at once, and the message says how to fall
    back.
43. **Secret hygiene sweep:** with a sentinel key, a captured `ILogger` sink, every exception
    message, every `core.LoadLog` error string the pipeline would record, and every resume key
    contain no sentinel. The request carries it only in the `Ocp-Apim-Subscription-Key` header.
44. The settings.json request does not pass through `PjmRateLimiter` (the limiter count is
    unchanged).

**Limiter & orchestration**
45. Sliding window: on a fake clock, the 6th request within 60 s waits until the 1st ages out. Six
    requests never fall inside any 60 s window.
46. `inst_load` runs before `gen_by_fuel` whatever the `EnabledFeeds` order. With no feeds
    enabled → success and no settings.json request.
47. Key acquisition failure → `LoaderRunResult.Failed` with zero units attempted.

**Sinks / SQL contract**
48. `BuildTable` column names, order, CLR types and `AllowDBNull` equal the §7.4 literal lists,
    for both sinks (`tvp-contract-check`).
49. Every `sql/PJM/*.sql` first statement is `USE Misc;`, and `001` contains no `CREATE DATABASE`.
50. `999` references only the six PJM objects, each `dbo.`-qualified. No other `DROP` target is
    allowed.
51. The `003` MERGE `UPDATE` list excludes `CreatedAtUtc` and the key columns. The procs contain the
    duplicate-key `THROW`.

Scoped test command:
`dotnet test tests/DataLoader.PJM.Tests/DataLoader.PJM.Tests.csproj -c Release`. Plus
`.\scripts\Test-SqlParses.ps1 -Path sql\PJM`. Escalate to solution-wide tests only if Core or Host
changes, which this design does not require.

---

## 13. Open decisions & risks

1. **Licensing and IT.16 (blocking for enabling, not for building).** PJM says DM2 data is
   "internal use only". Its API portal says automated queries "require a pjm.com account". This
   loader automates the **public website key**. Someone at ARM should confirm this is acceptable,
   or register ARM's own key. Switching then only needs a `core.Param(PJM, ApiKey)` row (§5.1).
2. **Keys: VERIFIED 2026-11-23** over the full UTC fall-back day 2026-11-01 (API doc §11.3), before the
   DST deadline. The PKs stand. A collision in production would still fail loudly (reader and proc),
   and the full lane would then re-open.
3. **`Misc` collation is unknown.** IT chooses it. The C# duplicate check assumes CI + trailing-space
   padding, and is stricter if the collation is CS. Ask IT for it, or read
   `DATABASEPROPERTYEX` after creation.
4. **The `inst_load` column list is incomplete** until §11.1. Expect a DDL addition before
   enabling.
5. **500 is provisionally transient** (§6.1). If §11.5 shows it is deterministic, remove it from
   the predicate.
6. **No deletes.** A row PJM withdraws on restatement stays in the table. Day-replace would fix
   that, but it would also turn any partial re-read into data loss. Revisit only if §11 or
   production shows withdrawals.
7. **Retention edge.** A zero-row read at ages 26–28 is recorded done (§6.2). If it was a transient
   PJM glitch rather than a purge, that day is lost. The cost is bounded to the three oldest days
   of the first backfill or of an outage recovery.
8. **Shared public key.** Anonymous DM2 traffic shares its quota. The trip rule protects PJM's pool
   but means a busy day can leave PJM's run red. Hourly runs and the 28-day resume make that
   self-healing.
9. **Key rotation.** One re-fetch per run handles a rotation. If PJM withdraws the public key
   entirely, settings.json may still serve a key that the API refuses, which gives a guard trip
   every run. The runbook must point at §5.3's message.
10. **API doc drift.** `docs/apis/PJM.md` §1 ("Key storage: `SEE_DB` → core.Param") and §8 (the 401
    message naming only `core.Param`) predate the settings.json decision. API_DOCUMENTATION_EXPERT
    should align them with §5 here.
11. **Org README/RUNBOOK requirement.** The platform docs cover the loader, but a
    `docs/runbooks/PJM.md` is listed as an enable-time deliverable (§12.1).

---

## Coverage checklist

| Feed | Pipeline (order) | Work unit / key | Reader tolerance | Target (key) |
|---|---|---|---|---|
| `inst_load` | 1st | UTC day; recent `;run=yyyyMMddHH` / hot `;run=yyyyMMdd` / settled stable; depth ≤ 28 | 0 rows: today OK, age 1–25 FAIL, age 26–28 warn; short areas warn; dup/echo/contract FAIL | `dbo.pjm_load` (`datetime_beginning_utc, area`) |
| `gen_by_fuel` | 2nd | same; depth 30 | 0 rows: today OK, any past day FAIL; short hours warn; dup/echo/contract FAIL | `dbo.pjm_fuel_mix` (`datetime_beginning_utc, fuel_type`) |

The following are written once and shared by both feeds: key acquisition (override → settings.json,
one 401 re-fetch), the run guard, the 5/min sliding limiter, the Polly policy and the paging/
reconciliation loop.

---

## 14. Extension: six more feeds (2026-11-23)

The input of record is `docs/apis/PJM.md` §12 (LIVE-VERIFIED 2026-11-23). As before, this section
contains **no code and no SQL**.

### 14.0 What is fixed and what changes

**Fixed (not re-opened):**
- the same `PJM` loader and module, DB `Misc`, schema `dbo`;
- key acquisition (§5), the run guard (§6.3), the one shared 5/min limiter (§10.1), the Polly policy and
  timeout coupling (§10.2), explicit 1-based paging with `totalRows` reconciliation and one re-read (§4.2),
  the field/contract policy (§4.3), and duplicate = FAIL (§4.6);
- a 30-day initial backfill as an enumeration floor;
- the platform horizon `DaysBack = 10` / `SettledAfterDays = 10`, with per-dataset exceptions as **floors**;
- the table names, and columns that mirror the API names in metadata order, plus `CreatedAtUtc`/`ModifiedAtUtc`.

**What changes:**
1. The descriptor grows. The feed's filter/sort column, its key shape, its timestamp rules, its unit size
   and its zone/empty-read behaviour all become **data** (§14.2).
2. `very_short_load_frcst` is read in **6-hour blocks**, not days (§14.3).
3. `load_frcstd_7_day` is a **snapshot** unit with no filter (§14.8).
4. The execution order has eight entries (§14.1).

### 14.1 Feed inventory and execution order

| Order | Feed | Target | Kind / unit | Why here |
|---|---|---|---|---|
| 1 | `load_frcstd_7_day` | `dbo.pjm_load_forecast_7day` | **snapshot**, one per run | **No retention at all** (perishable within ~30 min); costs 1 request |
| 2 | `inst_load` | `dbo.pjm_load` | window, UTC day | 30-day retention; cheap (≈29 requests on the first run) |
| 3 | `very_short_load_frcst` | `dbo.pjm_load_forecast_very_short` | window, **6-h UTC block** | 30-day retention; the largest feed (≈115 requests on the first run) |
| 4 | `gen_by_fuel` | `dbo.pjm_fuel_mix` | window, UTC day | history since 2016, so a miss is recoverable |
| 5 | `hrl_load_metered` | `dbo.pjm_load_metered` | window, UTC day | indefinite history |
| 6 | `hrl_load_prelim` | `dbo.pjm_load_prelim` | window, UTC day | indefinite history |
| 7 | `load_frcstd_hist` | `dbo.pjm_load_forecast_hist` | window, UTC day of `evaluated_at` | indefinite history |
| 8 | `hrl_load_estimated` | `dbo.pjm_load_estimated` | window, UTC day | **last**: the feed most likely to produce empty-past-day failures (§14.7), so a guard trip it causes costs nothing else |

- **The snapshot goes first** (changed by code review, 2026-11-23; it was 3rd). It is one request and the only feed
  with no retention at all, so first place secures the capture even against a guard trip during the 30-day feeds,
  at no cost to them. Its position does not change how many evaluations are captured: a run captures one
  evaluation, whichever is current when its turn comes.
- The two 30-day feeds come next. `inst_load` goes before `very_short` because it finishes in about 6 minutes,
  which means a run cut short in its first half hour still completes one perishable feed.
- `PjmFeeds.ExecutionOrder`, `PjmSettings.DefaultEnabledFeeds` and the shipped `EnabledFeeds` all carry all
  eight names in this order. The order stays fixed regardless of the order of the list.

### 14.2 How the code generalises (CODER hand-off)

Today the reader hard-codes several things: `datetime_beginning_utc` as the filter, sort, echo and key column;
one UTC/EPT pair per row; a key of `(timestamp, member)`; and completeness keyed on `MemberField == fuel_type`.
That does not cover the six new feeds. The generalisation **moves those facts onto the descriptor** and keeps
**one** reader and **one** pipeline class.

**`PjmFeed`: new or changed fields**

| Field | Type | Meaning | Replaces |
|---|---|---|---|
| `Kind` | `Window` \| `Snapshot` | A snapshot has no filter and one unit per run (§14.8) | — |
| `FilterField` | `string?` | The UTC column filtered **and** sorted on, and the one the window echo runs on. `null` for a snapshot. | hard-coded `datetime_beginning_utc` in `PjmApiClient.BuildPathAndQuery` and `PjmDayReader` |
| `SortField` | `string` | `= FilterField` for window feeds; `forecast_datetime_beginning_utc` for `load_frcstd_7_day` | hard-coded `sort=` |
| `UnitHours` | `int` (24) | Window size. It **divides 24**. Units of under 24 hours are keyed per block (§14.3). ⚠ **FROZEN once data exists**: it is baked into settled keys (the EIA `WindowSteps` lesson). | — |
| `IncludeToday` | `bool` (true) | Whether age 0 is enumerated. It is false for feeds that post past days only. | — |
| `MinRecentHours` | `int?` | **Floor** on the loader-level `RecentHours` for this feed | — |
| `MinSettledAfterDays` | `int?` | **Floor** on `SettledAfterDays` (the NUCORE SPIP precedent) | — |
| `MaxSettledAfterDays` | `int?` | **Ceiling.** Used **only if decision D1 = B** (§14.4). It is an explicit, documented deviation. | — |
| `EmptyOkThroughAge` | `int` (0) | A window this young may read 0 rows, or a partial day, as success. Above it, the existing `ExpectRowsWithinDays` / `MaxDaysBack` rules apply. | the implicit "only today" |
| `Stamps` | `IReadOnlyList<PjmStampRule>` | Each `(UtcField, EptField, Mode, IntervalMinutes)`; see §14.6 | `IntervalMinutes` + the hard-coded pair |
| `EndOffsets` | `IReadOnlyList<(BeginUtc, EndUtc, Minutes)>` | `end − begin == Minutes` on every row (§14.6) | — |
| `KeyFields` | ordered `string[]` | Two or three fields, the timestamps first and the member last; used in messages | `MemberField`, which stays as the member element |
| `Completeness` | `(Mode, SlotField, ExpectedPerUnit, FromAge)` | `None` \| `DistinctSlotsTotal` (gen_by_fuel) \| `DistinctSlotsPerMember` | the `MemberField == FuelType` branch + `ExpectedPerDay` |
| `SnapshotStaleAfterHours` | `int?` | Snapshot only: warn if `evaluated_at` is older than this (§14.8) | — |

These fields keep their meaning: `Name`, `BackfillDays`, `MaxDaysBack`, `ExpectRowsWithinDays`, `MemberField`,
`RequiredFields`, `OptionalFields`, `ExtraQuery`, `EmptyPastDayReason` and `DropsRepeatedFallBackHour`. **The two
shipped descriptors must express exactly today's behaviour** under the new fields (see the mapping table at the
end of this subsection), and every existing test must pass unchanged, apart from mechanical renames.

**Rows and keys**
- `IPjmRow` becomes `{ PjmRowKey Key; DateTime EchoUtc; DateTime SlotUtc; }`.
  - `PjmRowKey` is `(DateTime K1, DateTime? K2, string Member)`.
  - `EchoUtc` is the value of `FilterField`, or `evaluated_at` for the snapshot.
  - `SlotUtc` is what completeness counts.
- The reader's `KeyComparer` compares `K1`/`K2` exactly. It compares `Member` under `OrdinalIgnoreCase` after
  trimming trailing spaces only, which is unchanged (§4.6).
- The duplicate message lists `KeyFields` with their values.
- **Timestamp normalisation moves from the reader into the mapper**, through a shared helper
  `PjmStamps.Read(item, rule, ctx)`. The helper returns the normalised `(utc, ept)` after running the mode rule
  and the EPT check (§14.6).
  - Rows are built once, already normalised, so **`IPjmSlotRow.WithTimestamps` is deleted.**
  - The mapper signature becomes `TRow Map(PjmItem item, PjmMapContext ctx)`. `ctx` carries the feed, a unit
    label for messages, and `TimestampJitterToleranceSeconds`.
  - The window echo and duplicate check stay in the reader, after mapping.
  - Existing message texts are preserved verbatim, because tests match on them.
- **Six new row classes**, each with its properties in TVP order (§14.9): `PjmLoadMeteredRow`,
  `PjmLoadPrelimRow`, `PjmLoadEstimatedRow`, `PjmLoadForecastHistRow`, `PjmLoadForecastVeryShortRow` and
  `PjmLoadForecast7DayRow`. Each has a mapper of the same stem plus `Mapper`.
- **`PjmValueParser.NullableString`** is new, for the non-key strings `nerc_region`, `mkt_region` and `zone`:
  - JSON `null` → NULL;
  - a non-string → fail;
  - more than 50 characters → fail with the field name;
  - never trimmed.

  `is_verified` reuses `Boolean`. All the loads reuse `Decimal(…, 18, 4)`.

**Work unit.** `PjmDayWorkUnit` gains these members:
- `WindowStartUtc` and `WindowEndUtc`, the inclusive minute: `HH:00` and `HH+UnitHours−1:59`;
- `FilterValue` = `MM/dd/yyyy HH:mmtoMM/dd/yyyy HH:mm` (`null` for a snapshot);
- `Zone.Snapshot`;
- a `DisplayName` that carries the block hour.

`Day` (the UTC day of `WindowStartUtc`) still drives `Age`. Renaming the class to `PjmWorkUnit` is recommended
but optional; it is mechanical.

**Remaining classes**
- **Reader** (`PjmDayReader<TRow>`): the window echo runs on `row.EchoUtc ∈ [WindowStartUtc, WindowEndUtc:59]`.
  Snapshot units replace the echo with the §14.8 checks. The empty-read rule follows §14.7, and completeness
  follows the descriptor.
- **API client**: `sort={SortField}`. The filter parameter is `{FilterField}={FilterValue}`, and it is omitted
  when `FilterValue` is null.
- **Run guard**: a new failure class, `EmptySnapshot`, which counts toward the consecutive-failure trip like
  `empty-past-day`.
- **Sinks**: six new subclasses of `PjmSqlSinkBase<TRow>` (§14.9). The base is unchanged.
- **Source readers**: replace the per-feed subclasses with one sealed generic `PjmSourceReader<TRow>`. The
  existing two subclasses have no behaviour of their own. Keeping them is acceptable.
- **Module**: eight one-line `BuildPipeline` calls.

**The two shipped feeds under the new descriptor** (behaviour must be identical)

| Field | `inst_load` | `gen_by_fuel` |
|---|---|---|
| `FilterField` / `SortField` | `datetime_beginning_utc` | same |
| `UnitHours` / `IncludeToday` | 24 / true | 24 / true |
| `Stamps` | `(datetime_beginning_utc, datetime_beginning_ept, Slot, 5)` | `(…, Slot, 60)` |
| `KeyFields` | `datetime_beginning_utc, area` | `datetime_beginning_utc, fuel_type` |
| `EmptyOkThroughAge` | 0 | 0 |
| `Completeness` | `PerMember`, 288, `FromAge` 1 | `Total` (DST-aware), 24, `FromAge` 1 |

### 14.3 Work units and resume keys

**Generalised enumeration** (window feeds). For the two shipped feeds it reduces exactly to §3.2.

```
depth        = min(max(DaysBack, BackfillDays), feed.MaxDaysBack ?? ∞)
settledAfter = max(SettledAfterDays, feed.MinSettledAfterDays ?? 0)
               then min(…, feed.MaxSettledAfterDays ?? ∞)          // only if D1 = B
recentHours  = max(RecentHours, feed.MinRecentHours ?? 0)
for age = (IncludeToday ? 0 : 1) .. depth:   D = today − age
  for each block start s in D at 0, UnitHours, … (skip blocks with s > now):
     end  = s + UnitHours
     base = UnitHours == 24 ? $"pjm:{feed}:{D:yyyyMMdd}" : $"pjm:{feed}:{D:yyyyMMdd}T{s:HH}"
     now < end + recentHours      → Recent   base;run={yyyyMMddHH}
     D > today − settledAfter     → Hot      base;run={yyyyMMdd}
     else                         → Settled  base
```

The keys of the two shipped feeds are byte-identical to §3.2.

| Feed | Key (settled form) | Filter column | `UnitHours` | `IncludeToday` | `MinRecentHours` | Settle (see §14.4) | Depth |
|---|---|---|---|---|---|---|---|
| `hrl_load_metered` | `pjm:hrl_load_metered:20261118` | `datetime_beginning_utc` | 24 | false | **12** | floor **30** (D2) | 30 |
| `hrl_load_prelim` | `pjm:hrl_load_prelim:20261118` | `datetime_beginning_utc` | 24 | false | **12** | 10 | 30 |
| `hrl_load_estimated` | `pjm:hrl_load_estimated:20261118` | `datetime_beginning_utc` | 24 | false | — | floor **14** (D5) | 30 |
| `load_frcstd_hist` | `pjm:load_frcstd_hist:20261118` | `evaluated_at_utc` | 24 | false | **7** | 10, or **2** (D1) | 30 |
| `very_short_load_frcst` | `pjm:very_short_load_frcst:20261118T06` | `evaluated_at_utc` | **6** | true | — | 10, or **1** (D1) | **28** (retention cap) |
| `load_frcstd_7_day` | `pjm:load_frcstd_7_day:pub=202611232245` | none | — | — | — | n/a (snapshot) | — |

Why each choice:
- **`IncludeToday = false` for metered, prelim, estimated and hist.** These feeds publish past days once a day,
  or twice a week. An hourly re-read of today would return the same zero rows about 24 times a day.
- **`MinRecentHours`** keeps *yesterday* run-hour keyed until the vendor's daily post has certainly landed. The
  data then arrives the same day instead of about 24 hours later.
  - The posts are hrl_metered 05:30 and hrl_prelim 04:55. Eastern time is UTC−5 in winter, which puts them at
    about 10:30 and 09:55 UTC. So 12 h covers both with margin.
  - `load_frcstd_hist` posts at 00:05 (about 05:05 UTC), so 7 h covers it.
  - A run before the post reads 0 rows, which `EmptyOkThroughAge = 1` accepts as success.
  - V-10 confirms which day each post fills. If it shows a later completion, raise the floor; no code change is
    needed.
- **`very_short_load_frcst` uses 6-hour blocks: 4 units a day, each exactly one page** (72 evaluations × 25 × 24 =
  43,200 rows, under 50,000). A UTC day would be 4 pages per unit, and that breaks three things:
  1. **The timeout coupling.** §10.2 sizes `WorkUnitTimeoutSeconds = 900` for **one** page's worst case
     (734 s). Four pages could need about 4 × that.
  2. **Today's read would race the 5-minute posts.** Pages are at least 12 s apart under the limiter, so a
     4-page read spans at least 36 s. With a post every 300 s, roughly one read in eight would see `totalRows`
     move. The second mismatch fails the unit (§4.2), so today's unit would fail intermittently.
  3. **Recent cost.** Re-reading all of today hourly costs about 2.4 pages per run. The current block costs 1.

  Blocks cost the **same** for backfill and settled reads (4 requests a day either way), so they are not the
  "one unit per hour across days" anti-pattern of the skill. Other notes on blocks:
  - The block bound `HH+5:59` is lossless, because evaluations sit on `:00` seconds at 5-minute marks (API doc
    §12.6).
  - **The single page is enforced** (code review, 2026-11-23). The descriptor carries `MaxPagesPerUnit = 1` and
    `MaxRowsPerUnit = 43,200`. A first page whose `totalRows > PageSize × MaxPagesPerUnit` fails the unit as a
    **contract** failure, before any further page is read. If PJM adds areas, the unit fails loudly instead of
    paging a live block that races the 5-minute posts; the unit size and timeout then need review.
    `Validate` rejects `PageSize < 43,200` while very_short is enabled.
  - **Why one sort column is safe.** The API documents `sort` as a single column name, and a multi-column sort
    is unverified. `evaluated_at_utc` alone is not a total order, because 600 rows share each value, so paging
    on it could skip or repeat rows. The single-page guard above is what makes it safe: no block is ever read
    across pages.
  - ⚠ `UnitHours = 6` is **frozen** once data exists (§14.2).
- **The `load_frcstd_7_day` key is the publication slot, not a day.** `pub=` is the latest `:15` or `:45` UTC at
  or before `StartedAtUtc`, formatted `yyyyMMddHHmm`. One read per publication slot follows from this:
  - A manual re-run in the same slot is skipped (`usp_BeginLoadLog` returns −1).
  - A run in the next slot reads again.
  - The key never carries a run id, the API key or a URL (§3.3).

**Recent-zone side effect worth knowing.** Recent keys are `;run=yyyyMMddHH`, so a **second run in the same
hour skips every Recent and Hot unit** already done that hour. A second trigger at `:50` (decision D3) therefore
costs about **one request** (the new 7_day slot), plus any failed units it retries.

### 14.4 Correction horizon and settle decisions

Default `DaysBack = 10` / `SettledAfterDays = 10`, starting point per the skill. Per-feed reasoning:

| Feed | Proposal | Reason |
|---|---|---|
| `hrl_load_prelim` | **10 / 10** (default) | Restatement is undocumented. The default horizon is the hedge. Each unit is one page. |
| `hrl_load_metered` | **`MinSettledAfterDays = 30`**, provisional (D2) | `is_verified` proves restatement, from preliminary to verified, and the horizon is unknown. 30 is the widest floor that needs no extra depth (`depth = max(DaysBack, BackfillDays) = 30`; the startup rule `settledAfter ≤ depth` holds). Settling at 10 risks freezing `is_verified = 0` rows permanently. **V-1** (5 requests) settles it. If unverified rows exist at age 30, widen `BackfillDays` for this feed and the floor together. A DQ query runs after enable: `is_verified = 0` rows older than 30 days. Any result means the floor is too low. |
| `hrl_load_estimated` | **`MinSettledAfterDays = 14`** (D5) | The feed lags up to about a week and posts Tue/Fri. 14 keeps a day hot for at least one missed vendor post beyond the worst normal lag, so its final settled read happens on a complete day. Cost: +4 requests on the first run of each day. |
| `load_frcstd_hist` | **D1** | Evaluated forecasts are immutable (API doc §12.5) |
| `very_short_load_frcst` | **D1** | Immutable once evaluated (API doc §12.6) |
| `load_frcstd_7_day` | n/a | A snapshot. Nothing is re-read by date. |

**Decision D1 (for the user): `SettledAfterDays` for the two immutable forecast feeds.**

Note first how the hot zone is charged. **The hot zone is NOT re-read every hourly run.** Hot keys are
`;run=yyyyMMdd`, so each hot unit is read **once per UTC day**, on the first run after 00:00 UTC. Only the
Recent zone is re-read hourly. The ~40-request cost of a 10-day very_short hot zone is therefore paid once a day,
at the 00:20 run (about 8 minutes), not every hour.

| Option | very_short | hist | Floor rule | very_short requests: 00:20 run / other hourly run / day | hist requests: 00:20 run / day | Minutes per day at 5/min (these two feeds) |
|---|---|---|---|---|---|---|
| **A: default** | 10 | 10 | intact | **41** / 1–2 / **76** | **10** / **17** | 18.6 |
| **B: immutable** | **1** | **2** | **deviates**: needs the per-feed ceiling `MaxSettledAfterDays` | **5** / 1–2 / **39** | **2** / **9** | 9.6 |

Each extra settle day costs 4 requests a day for very_short and 1 for hist, so values in between scale
linearly.

Why the B values are 1 and 2:
- **very_short = 1.** Evaluations post within minutes. A block is read about 8 times while Recent, once more
  under today's Hot key, and once settled the next day, at least 2 h after it ends.
- **hist = 2, not 1.** Its data posts once a day (about 05:05 UTC). With 1, yesterday would settle on its first
  read after the Recent window (07:20 UTC). A post running late by a few hours would then freeze a **partial**
  day permanently. With 2, yesterday stays Hot all day, and the final settled read comes the next day.

What B costs:
- **The skill's floor rule.** B narrows. The skill explicitly allows a deviation "with a reason written down"
  and cites OilX `SettledAfterDays = 1` for immutable days. This paragraph is that reason.
- A ceiling also means an operator who widens `SettledAfterDays` does not widen these two feeds. That is
  intended, since they cannot change.

**Recommendation: B.** It saves about 45 requests (about 9 minutes) a day and roughly halves the 00:20 run, with
no correctness cost for immutable data. If the user prefers the floor rule intact, A is safe and fits the budget.

### 14.5 Request budget (5/min shared, 12 s per request; supersedes §3.4)

All units are one page each. Recommended settings: D1 = B, D2 = metered floor 30, D5 = estimated floor 14,
hourly at :20. The figures in brackets are for D1 = A, and then also for metered = 10.

| Run | inst | vshort | 7day | gen | metered | prelim | hist | est | **Total** | **Minutes** |
|---|---|---|---|---|---|---|---|---|---|---|
| **First run** (empty `core.LoadLog`, at 14:20 UTC) | 29 | 115 | 1 | 31 | 30 | 30 | 30 | 30 | **296** | **≈ 59** |
| First run of each UTC day (00:20) | 11 | 5 [41] | 1 | 11 | 30 [10] | 10 | 2 [10] | 14 | **84** [A: 128; A + metered 10: 108] | **≈ 17** [26 / 22] |
| Other hourly run, 01:20–11:20 | 1 | 1–2 | 1 | 1 | 0–1 | 0–1 | 0–1 | 0–1 | **4–9** | < 2 |
| Other hourly run, 12:20–23:20 | 1 | 1–2 | 1 | 1 | 0–1 | 0–1 | 0 | 0 | **4–7** | < 1.5 |
| **Day total** (24 runs) | 36 | 39 [76] | 24 | 36 | 42 [22] | 22 | 9 [17] | 16 | **224** [A: 269; A + metered 10: 249] | ≈ 45 [54 / 50] |

Notes on the table:
- **The first run takes about an hour.** Very_short alone is 115 requests (about 4.8M rows over 28 days, which is
  at least 97 pages at 50k a page, whatever the unit shape).
  - The next hourly trigger finds the overlap lock held and exits 0, which is correct.
  - Every unit is resumable. Settled units are skipped before any request.
  - Within a feed, enumeration is newest-first (§3.2). In a first run cut short, the *oldest* perishable days
    (ages 26–28) are read last. The next hourly run picks them up, so they are at risk only if the outage lasts
    more than about 2 days (§14.13 R3).
- **Retries add to all of this**, paced by the same limiter.
- **Request counts are derived as follows.**
  - Very_short day total under B: each block is read 8 times while Recent (6 as the current block, plus 2 in
    the 2-hour tail), then once under today's Hot key, then once settled. That is about 39 a day.
  - Metered and prelim: 12 Recent reads of yesterday, 1 Hot read, the hot days, and 1 settled read.
- **Headroom.** The limiter allows 300 requests an hour. The busiest steady run (00:20) uses about 84. The
  public pool is shared with anonymous DM2 users (§13 item 8), so lower is better, and that is another argument
  for D1 = B.
- **D3 = :50 trigger** adds about 24 requests a day. That is 1 per extra run, because same-hour Recent and Hot
  keys skip.

### 14.6 Timestamp rules: jitter/boundary, window echo, EPT pairs, end offsets

**Stamp modes** (`PjmStampRule.Mode`):
- **`Slot(n)`**: the existing §4.4 rule. Truncate UTC and EPT to the minute. Fail if the dropped seconds exceed
  `TimestampJitterToleranceSeconds` (10) or the minute is off the `n`-minute boundary.
- **`Exact`**: whole seconds kept **as received** (fractions still fail, §4.3). No truncation and no boundary
  check.

| Feed | Pair (UTC / EPT) | Mode | Basis |
|---|---|---|---|
| `inst_load` | `datetime_beginning` | Slot(5) | unchanged (0–2 s jitter, verified) |
| `gen_by_fuel` | `datetime_beginning` | Slot(60) | unchanged |
| `hrl_load_metered` | `datetime_beginning` | Slot(60) | Hourly actuals; same rule as `gen_by_fuel` |
| `hrl_load_prelim` / `hrl_load_estimated` | `datetime_beginning`, `datetime_ending` | Slot(60) each | Hourly actuals |
| `load_frcstd_hist` | `evaluated_at` | **Exact** | Seconds unverified (V-4). Exempting is **never lossy**: an immutable evaluation is never re-posted with other seconds, while truncating real seconds could merge two evaluations. |
| | `forecast_hour_beginning` | Slot(60) | Hourly forecast target |
| `very_short_load_frcst` | `evaluated_at` | **Slot(5)** | Verified: seconds always `:00`, on 5-minute marks. Truncation is a no-op, and the boundary check guards the contract. |
| | `forecast_datetime_beginning`, `forecast_datetime_ending` | Slot(5) | 24 steps, inferred 5-minute (**V-3**). If V-3 disproves it, the unit fails loudly before enabling, as intended. |
| `load_frcstd_7_day` | `evaluated_at_datetime` | **Exact. MUST be exempt.** | **Real non-zero seconds** (e.g. `22:47:12`). Truncating them would rewrite the key, and a 5- or 60-minute boundary check would fail every snapshot. |
| | `forecast_datetime_beginning`, `forecast_datetime_ending` | Slot(60) | 168 hourly steps (verified count) |

**Window echo: which column.** Every row's **`FilterField`** value must lie inside the unit's window
`[WindowStartUtc, WindowEndUtc:59]`.
- `datetime_beginning_utc` for `inst_load`, `gen_by_fuel` and the three `hrl_*` feeds.
- **`evaluated_at_utc`** for `load_frcstd_hist` and `very_short_load_frcst`. Forecast target times
  legitimately fall outside the window, and are **not** echo-checked.
- `load_frcstd_7_day` has no window. Its echo is replaced by the single-evaluation check (§14.8).

**EPT consistency on EVERY pair.** For every `PjmStampRule`, `ept == ToEastern(utc)` after the mode rule (§4.4
item 2). That covers 1 pair on `hrl_load_metered`; 2 on `prelim`, `estimated` and `hist`; and 3 on
`very_short` and `7_day`.
- The check is what proves each "utc" column really is UTC, including the forecast target columns, which
  are key components.
- On the fall-back day the hrl feeds keep all 24 UTC hours (API doc §12.8 trap 8), and the check holds as it does
  for `inst_load`.
- `Exact` pairs compare to the second.

**End offsets** (`EndOffsets`): `datetime_ending_utc − datetime_beginning_utc == 60 min` on prelim and estimated;
`forecast_datetime_ending_utc − forecast_datetime_beginning_utc` `== 5 min` on very_short and `== 60 min` on
7_day.
- A violation is a **data** failure, so it resets the guard streak, like the echo check.
- This is inferred, **not verified** (V-2). It is a cheap contract check that catches a shifted or relabelled
  column. If V-2 disproves it, drop that entry before enabling.
- No offset check runs on EPT, because EPT is already pinned to its UTC twin.

### 14.7 Status matrix and empty reads per feed

The HTTP matrix (§6.1) and the run guard (§6.3) are unchanged. The empty-read rule (§6.2) generalises as follows,
with `age` = today − `Day`:

| Feed | 0 rows / partial = **success** | 0 rows = **FAIL** (class `empty-past-day`) | 0 rows = **warn + success** |
|---|---|---|---|
| `inst_load`, `gen_by_fuel` | unchanged (§6.2) | unchanged | unchanged |
| `hrl_load_metered` | age 1 (`EmptyOkThroughAge = 1`; before the daily post) | age 2–30 | — |
| `hrl_load_prelim` | age 1 | age 2–30 | — |
| `hrl_load_estimated` | **age 1–10** (`EmptyOkThroughAge = 10`), empty *or* partial | age 11–30 | — |
| `load_frcstd_hist` | age 1 | age 2–30 | — |
| `very_short_load_frcst` | a Recent block (current, or in its 2-hour tail) | a past block at age ≤ `VeryShortExpectRowsWithinDays` (25) | a block at age 26–28 (the retention edge, as for `inst_load`) |
| `load_frcstd_7_day` | **never** | **an empty snapshot**, class **`EmptySnapshot`** (counts toward the trip) | — |

⚠ **For block feeds, the exemption is "the unit is Recent", not "age 0".** `PjmDayReader.ApplyEmptyReadRule`
returns early for `age <= 0`. That is correct for day units, where today is always Recent. A very_short block of
*today* that has left its Recent tail (e.g. block 00 at 14:20) is Hot, and 0 rows there must **fail**. Keep
`age <= 0` as the day-unit rule, and use `Zone == Recent` for units of under 24 hours.

⚠ **Block feeds count toward the run guard once per UTC day** (code review, 2026-11-23). very_short fails empty
past blocks one by one, so an 18-hour PJM gap would put three or more `empty-past-day` failures in a row. That
would trip the guard on **every** run for about 25 days (until the gap ages past the expectation band), and every
feed after very_short would fail without a request. The source reader therefore passes a count-once scope
(`{feed}:{yyyyMMdd}`) for units under 24 hours. `PjmRunGuard.RecordUnitFailure` counts each scope once per
streak: a repeat neither increments nor resets the streak. An 18-hour gap spans at most two UTC days, so it
counts at most 2 and does not trip on its own. Each block is still recorded as failed in `core.LoadLog` and is
re-attempted. `EmptySnapshot` shares the `empty-past-day` streak.

**Why estimated has a 10-day band.** The feed lags up to about a week (2026-11-18 had 50/240 rows at age 5).
Posts come Tue/Fri, a gap of up to 4 days, on top of about 5–6 days of lag. Ten days absorbs that plus one late
post. Without the band, the three youngest empty days would count three consecutive `empty-past-day` failures and
**trip the run guard every run**. Running estimated last (§14.1) bounds the damage if PJM stops posting
estimated altogether. The resulting failures at age 11+ are real, and are re-attempted daily while hot (floor 14).

**Completeness** (warnings, never failures):

| Feed | Rule | Runs when |
|---|---|---|
| metered, prelim | each `load_area` has < 24 distinct hours | age ≥ 2 |
| estimated | each `load_area` has < 24 distinct hours | age ≥ 11 (younger days are partial by design) |
| very_short | any `forecast_area` with < 72 distinct `evaluated_at` in a block | the block is not Recent |
| hist | none | evaluations per day are not yet characterised (V-4) |
| 7_day | any `forecast_area` with ≠ 168 rows | every snapshot |

The fall-back flag `DropsRepeatedFallBackHour` stays `false` for all six (all 24 UTC hours verified on
2026-11-01).

### 14.8 The snapshot feed (`load_frcstd_7_day`)

**Request:** `GET /load_frcstd_7_day?rowCount=50000&startRow=1&sort=forecast_datetime_beginning_utc&order=Asc`.
- There is **never** a date filter.
- The sort is sent per the NUCORE rule. It is moot for a single page, but V-5 must confirm the column is sortable.
  A 400 would fail loudly.

**Unit:** one per run, keyed on the publication slot (§14.3). It is always `Zone.Snapshot`.

**Checks** (in addition to the contract, stamp, EPT, end-offset and duplicate checks):
1. **0 rows → FAIL**, class `EmptySnapshot`. A feed that holds the latest evaluation can never be legitimately
   empty.
2. **More than one distinct `evaluated_at_datetime_utc` → FAIL** (contract). The feed is no longer a single
   snapshot, and the key and unit model must be revisited.
3. **`evaluated_at_datetime_utc` > the read time + 5 min → FAIL** (contract or clock). The read time is the
   instant the response arrived (`TimeProvider.GetUtcNow()`), **not** the run's `StartedAtUtc` (changed by code
   review, 2026-11-23). A run can reach this feed well after it starts. On a first run before the reorder it
   came about 29 min in, so a legitimate issue stamped 14:47:12 and read at 14:49 by a run started at 14:20
   would have failed, and that issue would have been lost for good.
4. **`evaluated_at_datetime_utc` older than `SnapshotStaleAfterHours` (6), measured from the read time →
   Warning**, and the snapshot is still
   stored. PJM has stopped refreshing, but the rows are real, and re-storing them is a no-op (below). It is a
   warning rather than a failure because a failure would make every hourly run red over PJM's outage.
   Revisit if an alert is wanted (§14.13 R5).
5. A `totalRows` above `PageSize` is impossible for one snapshot of about 4,200 rows. Paging handles it anyway,
   and check 2 then fails it.

**"Skip the write if that evaluated_at is already stored": met by the MERGE, with no pre-check.** The proc
MERGEs on the full key with the `EXCEPT` change test (§8). Re-reading an evaluation already stored therefore
changes **0 rows** and returns `RecordsProcessed = 0`, so nothing is rewritten and `ModifiedAtUtc` does not move.
A separate `IF EXISTS (… evaluated_at …)` pre-check was rejected for two reasons:
- It would add a second SQL read path.
- It would **hide** a re-published evaluation with changed values (V-7). The MERGE records that as an update,
  visible via `ModifiedAtUtc > CreatedAtUtc`.

When the merge reports 0 changes, the pipeline logs at Information: `"7_day evaluation {ts} already stored (0
changes)"`.

**Capture rate.**
- At hourly :20, the run captures about **1 of every 2** evaluations (posts land at :15 and :45).
- Adding a `:50` trigger (D3) captures about **all** of them, for about 1 extra request per run (§14.3). A :50
  run that overlaps a long :20 run (the 00:20 run, or a first run) exits on the overlap lock, which is harmless.

### 14.9 Target tables and TVP contracts (NAME + ORDER + TYPE)

The types follow §7.4 and are unchanged:
- `DATETIME2(0)` for every timestamp. It holds 7_day's real seconds exactly, and fractions are rejected
  upstream.
- `VARCHAR(50)` for strings.
- `DECIMAL(18,4)` for every MW or load value. Integers and decimals both fit, extra scale is rounded
  deterministically, and the RTO peak is about 1.6 × 10⁵ against a capacity of 10¹⁴.
- `BIT` for `is_verified`.

**No type change is needed.** Key columns are `NOT NULL`. Every timestamp is `NOT NULL` (required fields). Non-key
strings and measures are `NULL` (V-9). Each table adds
`CreatedAtUtc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()` and `ModifiedAtUtc` (same), and neither is ever a
TVP column. The PK is **clustered in key order** (time first, which suits the append pattern and time-range
reads).

`dbo.PjmLoadMeteredType` → `dbo.pjm_load_metered`, PK `(datetime_beginning_utc, load_area)`:
```
1 datetime_beginning_utc  DATETIME2(0)  NOT NULL
2 datetime_beginning_ept  DATETIME2(0)  NOT NULL
3 nerc_region             VARCHAR(50)   NULL
4 mkt_region              VARCHAR(50)   NULL
5 zone                    VARCHAR(50)   NULL
6 load_area               VARCHAR(50)   NOT NULL
7 mw                      DECIMAL(18,4) NULL
8 is_verified             BIT           NULL
```
`dbo.PjmLoadPrelimType` → `dbo.pjm_load_prelim`, PK `(datetime_beginning_utc, load_area)`:
```
1 datetime_beginning_utc  DATETIME2(0)  NOT NULL
2 datetime_beginning_ept  DATETIME2(0)  NOT NULL
3 datetime_ending_utc     DATETIME2(0)  NOT NULL
4 datetime_ending_ept     DATETIME2(0)  NOT NULL
5 load_area               VARCHAR(50)   NOT NULL
6 prelim_load_avg_hourly  DECIMAL(18,4) NULL
```
`dbo.PjmLoadEstimatedType` → `dbo.pjm_load_estimated`, PK `(datetime_beginning_utc, load_area)`:
```
1 datetime_beginning_utc  DATETIME2(0)  NOT NULL
2 datetime_beginning_ept  DATETIME2(0)  NOT NULL
3 datetime_ending_utc     DATETIME2(0)  NOT NULL
4 datetime_ending_ept     DATETIME2(0)  NOT NULL
5 load_area               VARCHAR(50)   NOT NULL
6 estimated_load_hourly   DECIMAL(18,4) NULL
```
`dbo.PjmLoadForecastHistType` → `dbo.pjm_load_forecast_hist`, PK `(evaluated_at_utc, forecast_hour_beginning_utc, forecast_area)`:
```
1 evaluated_at_utc             DATETIME2(0)  NOT NULL
2 evaluated_at_ept             DATETIME2(0)  NOT NULL
3 forecast_hour_beginning_utc  DATETIME2(0)  NOT NULL
4 forecast_hour_beginning_ept  DATETIME2(0)  NOT NULL
5 forecast_area                VARCHAR(50)   NOT NULL
6 forecast_load_mw             DECIMAL(18,4) NULL
```
`dbo.PjmLoadForecastVeryShortType` → `dbo.pjm_load_forecast_very_short`, PK `(evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area)`:
```
1 evaluated_at_utc                 DATETIME2(0)  NOT NULL
2 evaluated_at_ept                 DATETIME2(0)  NOT NULL
3 forecast_datetime_beginning_utc  DATETIME2(0)  NOT NULL
4 forecast_datetime_beginning_ept  DATETIME2(0)  NOT NULL
5 forecast_datetime_ending_utc     DATETIME2(0)  NOT NULL
6 forecast_datetime_ending_ept     DATETIME2(0)  NOT NULL
7 forecast_area                    VARCHAR(50)   NOT NULL
8 forecast_load_mw                 DECIMAL(18,4) NULL
```
`dbo.PjmLoadForecast7DayType` → `dbo.pjm_load_forecast_7day`, PK `(evaluated_at_datetime_utc, forecast_datetime_beginning_utc, forecast_area)`:
```
1 evaluated_at_datetime_utc        DATETIME2(0)  NOT NULL
2 evaluated_at_datetime_ept        DATETIME2(0)  NOT NULL
3 forecast_datetime_beginning_utc  DATETIME2(0)  NOT NULL
4 forecast_datetime_beginning_ept  DATETIME2(0)  NOT NULL
5 forecast_datetime_ending_utc     DATETIME2(0)  NOT NULL
6 forecast_datetime_ending_ept     DATETIME2(0)  NOT NULL
7 forecast_area                    VARCHAR(50)   NOT NULL
8 forecast_load_mw                 DECIMAL(18,4) NULL
```

**Procs:** `dbo.usp_MergePjmLoadMetered`, `…LoadPrelim`, `…LoadEstimated`, `…LoadForecastHist`,
`…LoadForecastVeryShort` and `…LoadForecast7Day`. They have the §8 shape:
- a duplicate guard on the full 2- or 3-column key → `THROW 50001`;
- MERGE with the NULL-safe `EXCEPT` on all non-key source columns (EPT and ending columns included) →
  `ModifiedAtUtc`;
- no `NOT MATCHED BY SOURCE`;
- `SELECT @@ROWCOUNT`;
- table variables only.

**DATABASE_DEVELOPER hint for the three forecast procs:** scope the MERGE target to the batch's
`evaluated_at` range (a CTE filtered on `MIN`/`MAX` of `@Records`). The plan then seeks the clustered PK instead
of considering the whole table. These tables grow fast (§14.13 R1). The bounds are local variables
(`@MinEval`/`@MaxEval`), so each of the three MERGEs ends `OPTION (RECOMPILE)` to plan the seek on their real
values (code review, 2026-11-23).

The sinks set `StoredProcedureName` and `TableValuedParameterType` to the names above. `BuildTable` mirrors each
list (`DateTime` / `string` / `decimal` / `bool`, `AllowDBNull` per the Null column).

### 14.10 SQL file plan

**Extend `001`/`002`/`003`/`999` in place. Do not add new numbered files.**
- The four files have **never been deployed anywhere**, because `Misc` does not exist yet (§0 build-only
  posture). So there is no migration to protect, and one run order (001 → 002 → 003) stays the whole deployment.
- New numbered files would collide with the repo-wide meaning of **`004`** (the arm-to-dbo comparison, which
  PJM deliberately lacks, §7.3) and would split one deployment across seven files.

| File | Change |
|---|---|
| `001_CreatePjmSchema.sql` | Add the six tables, with named constraints `PK_pjm_load_metered`, `DF_pjm_load_metered_CreatedAtUtc`, … and `IF OBJECT_ID(…) IS NULL` guards. Also **correct the stale "KEYS ARE PROVISIONAL" comment block**: all eight keys are verified, so cite API doc §11.3/§12.1. |
| `002_CreatePjmTvpTypes.sql` | Add the six types (§14.9), `IF TYPE_ID(…) IS NULL`-guarded |
| `003_CreatePjmProcedures.sql` | Add the six procs, `CREATE OR ALTER`, keeping the `DB_NAME() <> N'Misc'` guard |
| `999_DropPjmObjects.sql` | Drop **24** objects (8 procs → 8 types → 8 tables), each `dbo.`-qualified and existence-guarded. Header: very_short and 7_day history is **unrecoverable** once dropped (7_day was never retrievable again; very_short after 30 days). |

Every file still opens with `USE Misc;` + `GO`, and is parse-checked with
`.\scripts\Test-SqlParses.ps1 -Path sql\PJM`.

**IT access in `Misc` (update §9):** `EXECUTE` on 8 procs and 8 types, and `SELECT` on 8 tables. Under org
policy, IT assigns tables in `Misc`, so the **six new table names must be added to the IT request** before
deployment.

### 14.11 Settings, appsettings and validation

**New settings** (class defaults; listed in the shipped block):
- `VeryShortMaxDaysBack = 28` and `VeryShortExpectRowsWithinDays = 25`. These mirror `InstLoad*`, so each
  retention-capped feed can be tuned after its own §11.6-style measurement (V-8).
- `EnabledFeeds`: all eight names, in §14.1 order.

**Descriptor constants, not settings** (vendor facts, the NUCORE descriptor precedent):
- `UnitHours`, `IncludeToday`, `MinRecentHours`, `MinSettledAfterDays`, `MaxSettledAfterDays`;
- `EmptyOkThroughAge`, `Stamps`, `EndOffsets`, `Completeness` and `SnapshotStaleAfterHours`.

**Validation additions** (fail the run before any request):
- For every enabled window feed, `effectiveSettledAfter(feed) ≤ effectiveDepth(feed)`. This generalises the two
  existing `SettledAfterDays` rules, so very_short gets its final settled read inside retention.
- `VeryShortExpectRowsWithinDays < VeryShortMaxDaysBack`.
- `EmptyOkThroughAge < effectiveDepth` for every feed.
- `24 % UnitHours == 0`.
- Added by code review (2026-11-23):
  - `PageSize × MaxPagesPerUnit ≥ MaxRowsPerUnit` for every enabled feed that sets both. For very_short this
    means `PageSize ≥ 43,200`.
  - **No feed may set both a settle floor and a ceiling.**
  - **A ceiling (`SettledAfterDaysCeiling`) is allowed only on a feed whose descriptor sets
    `IssuedForecastsImmutable = true`.** Only very_short and hist set it, so the OilX-style exception cannot
    reach a restating feed by accident.

The timeout coupling (§10.2) is unchanged: every unit is still one page.

**Transfer compression** (code review, 2026-11-23). Both `SocketsHttpHandler`s set
`AutomaticDecompression = GZip | Deflate`. PJM serves gzip (verified live); without it the first run downloads
about 520 MB of very_short alone.
- `PjmBufferingHandler` no longer copies the wire `Content-Length` onto the buffered content, because that
  header counts the compressed bytes.
- Truncation is still caught by the connection, which enforces `Content-Length` and the chunked terminator on
  the raw stream and throws during the buffered read. That throw surfaces as `HttpRequestException` and is
  retried.
- The JSON parse and the `totalRows` reconciliation catch anything that slips through.

**Explicit setting.** The shipped `Loaders:PJM` block now sets `TimestampJitterToleranceSeconds: 10`.

**Watchdog (at enable time):** keep `MaxGapHours: 4`. The first run takes about 1 h. `DATA_VOLUME` (if enabled)
sees a much larger daily volume, dominated by very_short (172,800 rows a day) and 7_day (about 100,800 a day at an
hourly capture). Both are stable enough for the same-weekday median.

### 14.12 Test additions (`tests/DataLoader.PJM.Tests`, continuing §12.2)

**Generalisation, behaviour-preserving**
52. The two shipped descriptors expressed in the new fields (the table at the end of §14.2) produce
    **byte-identical** keys, filters, `sort` and messages. All of tests 1–51 still pass.

**Descriptors and enumeration**
53. `ExecutionOrder`, `DefaultEnabledFeeds` and the shipped `EnabledFeeds` all equal the §14.1 list.
    Execution follows it whatever the order of the configured list.
54. At a fixed `StartedAtUtc` (14:20 UTC, empty LoadLog): unit counts per feed are 29/115/1/31/30/30/30/30
    (§14.5), and the zones match the §14.3 pseudo-code.
55. `IncludeToday = false` feeds never enumerate age 0. `MinRecentHours` keeps yesterday Recent for
    metered/prelim at 11:59 UTC and makes it Hot at 12:00. For hist, the same switch happens at 06:59/07:00.
56. very_short blocks:
    - 4 per past day; today's blocks only up to `now`;
    - key `…:yyyyMMddTHH`, filter `MM/dd/yyyy 06:00toMM/dd/yyyy 11:59` under `de-DE`;
    - a block is Recent until `end + RecentHours`;
    - nothing older than `today − VeryShortMaxDaysBack`.
57. Settle floors and ceilings:
    - the metered floor 30 holds with `SettledAfterDays = 10`; the estimated floor is 14;
    - with D1 = B, very_short is 1 and hist is 2 even when `SettledAfterDays = 20`, and both stay stable across
      two days;
    - the startup rule rejects `effectiveSettledAfter > depth`.
58. Snapshot key:
    - `pub=` is the latest :15/:45 at or before now (14:14 → `1345`, 14:15 → `1415`, 14:44 → `1415`);
    - two runs in the same slot use the same key, and the next slot gives a new key;
    - the key never contains the API key, a URL or a run id.

**Reader**
59. Filter, sort and echo use `FilterField`. The `very_short` and `hist` URLs filter and sort on
    `evaluated_at_utc`. A row whose `evaluated_at_utc` is outside the block or day fails, while forecast targets
    outside the window are accepted.
60. Stamp modes:
    - 7_day `evaluated_at_datetime_utc = …22:47:12` is stored **unchanged**, and passes the EPT check;
    - very_short `evaluated_at` at `:03:00` (off the 5-minute mark) fails;
    - hist `evaluated_at` with seconds is kept exact;
    - an `hrl_*` stamp at `:00:02` is truncated to `:00:00`.
61. EPT is checked on **every** pair (1/2/2/2/3/3). A wrong `forecast_datetime_ending_ept` alone fails the unit.
    The fall-back day passes for hrl_metered with 24 UTC hours.
62. End offsets: prelim ending ≠ beginning + 60 min fails, and so does very_short ending ≠ beginning + 5 min.
    Both are data failures that do not trip the guard.
63. Three-column duplicate key: a duplicate on hist's `(evaluated_at, forecast_hour, area)` fails, nothing is
    written, and the message names all three fields. The same `(forecast_hour, area)` with different
    `evaluated_at` is accepted, which is the 484 case.
64. `NullableString`: `zone` null → NULL, `zone` 51 characters → fail, `zone` a number → fail.
    `is_verified` accepts `true`/`"false"`/`1`/`null`.

**Empty reads and status**
65. Empty-read matrix (§14.7), one case per cell:
    - metered age 1 → success, age 2 → fail;
    - estimated age 10 → success, partial at age 5 → success with no completeness warning, age 11 → fail;
    - hist age 1/2;
    - very_short: a Recent block → success, age 25 → fail, age 27 → warn;
    - 7_day empty → fail with class `EmptySnapshot`.
66. Three consecutive `EmptySnapshot` or `empty-past-day` failures across feeds trip the guard. Estimated ages
    1–10 empty never count.
67. Snapshot checks: two distinct `evaluated_at` → contract failure; `evaluated_at` in the future (+10 min) →
    failure; stale (7 h) → Warning + success. A re-read of a stored evaluation (sink reports 0) logs
    "already stored".
68. Completeness: very_short area with 71 evaluations in a past block → one Warning. 7_day area with 167 rows →
    Warning. No warning for estimated at age ≤ 10 or for hist.

**Sinks and SQL contract**
69. `BuildTable` for the six new sinks equals the §14.9 literal lists (name, order, CLR type, `AllowDBNull`).
70. `999` references exactly the 24 PJM objects, all `dbo.`-qualified. `001` contains six new `PK_`/`DF_`
    names and no `CREATE DATABASE`. Each new proc carries the duplicate `THROW`, and none puts `CreatedAtUtc` or
    a key column in its `UPDATE` list.
71. Settings: `VeryShortMaxDaysBack = 28` and `VeryShortExpectRowsWithinDays = 25` (class and shipped), with the
    validation additions of §14.11 each rejected once.

Scoped command unchanged. **Core and Host are not touched**, so no solution-wide escalation is needed.

### 14.13 Decisions for the user, and risks

**Decisions**
- **D1: immutable-forecast settle.** A (10/10, floor rule intact; very_short 76 and hist 17 requests a day) or
  **B (very_short 1 / hist 2 via a documented per-feed ceiling; 39 and 9 a day). Recommended: B** (§14.4).
- **D2: metered settle floor.** 30, provisional (recommended), or 10. V-1 decides, at a cost of 5 requests.
- **D3: 7_day capture rate.** Hourly at :20 (about 50% of evaluations), or add a :50 trigger (about 100%, +24
  requests a day). The same choice doubles 7_day storage (R1).
- **D4: very_short unit shape.** 6-hour blocks (recommended, §14.3), or day units. Day units need a higher
  `WorkUnitTimeoutSeconds` and accept intermittent same-day snapshot-mismatch failures.
- **D5: estimated window.** Empty or partial is OK through age 10, with a settle floor of 14 (recommended). V-6
  may tighten both.

**Risks**
- **R1: volume in a shared database.** very_short is about **63M rows a year** (about 5–6 GB with the audit
  columns). 7_day is about **37M rows a year at hourly capture** (74M with D3). `Misc` is IT-owned and shared.
  IT should be told the growth when the tables are requested. A retention or purge policy for these two
  tables is an **open question for the user and IT**, and is not designed here.
- **R2: unverified inferences** (V-2, V-3, V-4, V-5): the end offsets, the very_short step spacing, the hist
  `evaluated_at` seconds, and sortability of the new sort columns. Each one fails **loudly** if wrong, and must
  be closed before enabling.
- **R3: newest-first enumeration on the perishable feeds.** In a first run or outage recovery cut short, the
  oldest days (ages 26–28, about 2 days from purge) are read last. Hourly re-runs recover them unless the
  outage lasts more than about 2 days. Oldest-first for retention-capped feeds is a possible later refinement.
- **R4: metered restatement beyond 30 days.** If V-1 shows it, both the settle floor and the depth for that feed
  must grow. Otherwise preliminary values freeze silently. The post-enable DQ query (§14.4) is the backstop.
- **R5: 7_day staleness is only a Warning.** If PJM stops refreshing, nothing emails. Revisit whether stale
  should fail once operating experience exists.
- **R6: estimated vendor stall.** If PJM stops posting estimated, its age-11+ empty days trip the guard each run.
  Estimated runs last, so the damage stays local.

### 14.14 Decisions taken and checks settled (2026-11-23, user + live data)

**User decisions (these supersede the options in §14.13):**
- **D1 = B.** `very_short_load_frcst` SettledAfterDays = **1**, `load_frcstd_hist` SettledAfterDays = **2**. This is a documented per-feed **exception** to the user's 10/10 floor rule, justified the same way as OilX: an issued forecast is immutable. Implement it as an explicit per-feed `SettledAfterDaysCeiling`, separate from the floor, so the exception is visible in code and config and can't be applied by accident to a feed that restates.
- **D2 = 30.** `hrl_load_metered` settle floor = 30, because `is_verified` restates it.
- **D3 = hourly only.** No :50 trigger, so `load_frcstd_7_day` captures about half of PJM's forecast issues.
- **D4 / D5:** accepted as designed. `very_short` runs in 6-hour UTC blocks (one page each). `hrl_load_estimated` treats an empty or partial day as success through age 10, with a settle floor of 14.
- **Retention:** keep everything, with no pruning (about 100M rows/year from `very_short` + `7_day` in `Misc`).

**Checks V-2 to V-5, settled from live data (2026-11-01 and 2026-11-18):**
- **V-2, ending − beginning:** in UTC it is always exactly the interval. `hrl_load_prelim`/`hrl_load_estimated` give 60 min ×240. `load_frcstd_7_day` gives 60 min ×4,200. `very_short` gives 5 min ×50,000. In EPT it is **not**: on the fall-back hour, prelim/estimated show **0 min** (10 rows) and `very_short` shows **−55 min** (600 rows), because EPT is naive wall-clock time. So any ending−beginning check runs on the **UTC pair only**. EPT columns are still checked individually with EPT == UTC→Eastern.
- **V-3, `very_short` steps:** each evaluation forecasts 24 steps at evaluated_at + 0, 5, …, 115 min, all on 5-minute boundaries with zero seconds.
- **V-4, `hist` evaluated_at:** about 10 evaluations per UTC day, at minute **:45** with zero seconds (e.g. 03:45, 10:45, 14:45). Truncating to the minute is harmless, but **no hourly boundary check on evaluated_at**. `forecast_hour_beginning_utc` is always on the hour, so the hourly boundary check applies there.
- **V-5, sortable columns** (from metadata `enableSorting`): metered: datetime_beginning_utc, load_area. prelim/estimated: datetime_beginning_utc, load_area. hist: evaluated_at_utc, forecast_hour_beginning_utc, forecast_area. very_short: evaluated_at_utc, forecast_datetime_beginning_utc, forecast_area. 7_day: forecast_datetime_beginning_utc, forecast_area only, so **evaluated_at_datetime is neither sortable nor filterable**, which is consistent with §14.8's unfiltered snapshot read. Every feed's sort must use only these columns.
- **V-1** (metered `is_verified` age) is not measured. The floor of 30 stands until it is.
