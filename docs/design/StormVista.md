# StormVista loader — application design (end-to-end flow)

> **SCHEMA v2 — normalization redesign (2026-08-06). This supersedes the data-model
> details below; `sql/StormVista/*.sql` is the authoritative contract.**
> - **Schema is `dbo`** (not `sv`) for every object.
> - New lookup tables: **`dbo.Endpoint`** (Daily/Regional), **`dbo.Status`**
>   (Success/NotAvailable/Failed), **`dbo.Flag`** (0=obs/1=fcst/2=norm). Existing
>   dimensions (Model, Cycle, WddType, RegionSet, RegionSetWddType, Region) unchanged
>   except moved to `dbo`.
> - **`dbo.FileLog` is now the normalized "file/request" hub** — one row per downloaded
>   file, holding FK IDs (EndpointId, ModelId, CycleId, WddTypeId, RegionSetId NULL for
>   daily, StatusId) + InitDate + HttpStatus + RowCount + RequestPath. Natural key =
>   (Endpoint, Model, Cycle, WddType, RegionSet, InitDate). FileLog is **mandatory** now
>   (facts FK to it) — the old `EnableFileLog` toggle is removed. This supersedes §13.5:
>   the per-request `usp_UpsertFileLog` is a single-row gated MERGE, and at the configured
>   `RequestsPerSecond` (~25/s ⇒ ~9 h for a full backfill) the network throttle dominates
>   wall-clock, so the mandatory FileLog write is not a practical backfill bottleneck. If
>   that ever changes, batch the FileLog upserts (multi-row TVP) rather than reinstating a
>   toggle (facts require the FileLogId).
> - **Facts are fully normalized and hang off FileLog:** `dbo.DailyWdd`
>   (FileLogId, ValidDate, FlagId, Value) and `dbo.RegionalWdd` (FileLogId, RegionId,
>   ValidDate, Value). Model/Cycle/Type/InitDate/Endpoint are reached via FileLogId —
>   **no duplication** (confirmed choice). RegionName/Flag are normalized to RegionId/FlagId.
> - **Load flow (per file):** the source reader calls `dbo.usp_UpsertFileLog(...natural
>   keys..., @Status, @HttpStatus, @RequestPath, @RowCount)` for EVERY outcome
>   (Success / NotAvailable(404) / Failed) → gets back the **FileLogId** → tags each
>   produced row with it → the sink bulk-merges facts by FileLogId
>   (`usp_BulkMergeDailyWdd`/`usp_BulkMergeRegionalWdd`, TVPs carry FileLogId + FlagCode
>   or RegionName as natural keys, resolved to IDs in the proc). Both DB calls are under
>   `SqlWriteGate`. 404 → FileLog NotAvailable row, no facts; Failed → FileLog Failed row.


Design spec (APPLICATION_DESIGNER) → handed to CODER (C#) and DATABASE_DEVELOPER (SQL). **No code or SQL here.** All API facts come from `docs/apis/StormVista.md` (verified field reference). Platform conventions come from `CLAUDE.md`.

This is a **hybrid loader**: an HTTP source that returns **CSV bodies** (not JSON, not files). It reuses the platform's `HttpClient`/Polly wiring (EnergyAspects pattern) but parses CSV like a file loader (Platts pattern), and it uses a **two-zone (settled / hot) work-unit key** for resume — a model that sits between EnergyAspects' fixed re-pull window and Platts' file-mtime key.

> **Enum revision note (2026-08-05):** the enumerations in this spec were revised after
> reading the authoritative OpenAPI spec. `docs/apis/StormVista.md` is the source of
> truth for all enum values, region columns, type×region applicability, and per-feed
> cycles; if anything below disagrees, the API doc wins. Confirmed scope = **everything**
> (all **17** daily models, all **6** weekly models, all cycles, all types incl. `pw_hdd`,
> all region-sets incl. **`iso`**). Region-set identifier is a **string code**
> (`'3'|'5'|'9'|'iso'`), not a numeric `N`.

---

## 1. Module shape

`StormVista` follows the **Platts two-closed-pipelines + `EnabledFeeds` toggle** shape (`PlattsModule`), not the single-pipeline EnergyAspects shape. There are two CSV shapes with different keys, parse logic, and destination tables, so each is its own closed pipeline built explicitly (no shared `IWorkUnitProvider<T>` in DI, avoiding the collision Platts calls out).

- **`StormVistaModule : ILoaderModule`** — `LoaderId = "StormVista"`.
  - `RegisterServices`: bind `StormVistaSettings` from `Loaders:StormVista`; register the shared `HttpClient` (with the extended Polly policy + optional rate-limit handler, see §7), the reference provider (§5), the optional FileLog writer (§9), and **two** feed pipelines built explicitly (`IStormVistaFeedPipeline`), one per feed.
  - `RunAsync`: resolve `EnabledFeeds`; load reference once (§5); run the enabled feed pipelines **sequentially** (Daily then Regional — they share the one rate-limited `HttpClient`, so serial is simpler and gentler on the vendor); after both complete, run the **module-level post-load validation** (§10); aggregate per-feed `LoaderRunResult`s exactly like `PlattsModule.RunAsync` (sum totals, `Success = all succeeded`, warn on enabled-but-unknown feeds).
- **Feeds:** `"Daily"` and `"Regional"`. `EnabledFeeds` default `["Daily","Regional"]`.
- **`IStormVistaFeedPipeline : ILoaderPipeline`** — marker carrying `FeedId` (mirrors `IPlattsFeedPipeline`) so `RunAsync` can enumerate and toggle them.

### Why NOT `LoaderPipelineBase` directly

`LoaderPipelineBase` calls `GetWorkUnitsAsync` **once** and hands the whole `IReadOnlyList` to `ParallelRunner`, which eagerly creates one `Task` per unit (`items.Select(...)` + `Task.WhenAll`). A full backfill is **~828k units** (~324 files/init-date × ~7 years × 365 days), so a single pass would materialize ~828k `WorkUnit` objects **and** ~828k tasks — untenable.

Therefore each StormVista feed pipeline is a **custom windowed orchestrator** (`ILoaderPipeline` is explicitly allowed to be implemented directly per `CLAUDE.md`) that chunks the date range into windows and processes **one window at a time**, so peak memory is one window's units, never the whole range. Crucially it does **not** re-implement the vetted per-unit loop: per window it delegates to a standard `LoaderPipelineBase` instance bound to that window's precomputed unit list (see §6). This reuses the exact idempotency / skip / per-unit-timeout / fail-one-not-the-run behaviour without touching Core.

---

## 2. The four pipeline component types, per feed

Each feed supplies the standard four pipeline parts. Transform is identity (`IdentityTransformer<TRow>`) — the source reader emits target rows directly, exactly as Platts does.

| Part | Daily feed | Regional feed | Responsibility |
|------|-----------|---------------|----------------|
| Work-unit provider | `DailyWorkUnitProvider` | `RegionalWorkUnitProvider` | Enumerate init-date × valid enum combos for a **date window** (§6.3). Not a flat one-shot list — window-scoped. |
| Source reader | `DailySourceReader` | `RegionalSourceReader` | HTTP GET the CSV, **404 → 0 rows (skip, no throw, not retried)**, 200 → parse CSV to rows. Regional also **unpivots** wide→long and validates the region-column set (§8). |
| Transformer | `IdentityTransformer<DailyWddRow>` | `IdentityTransformer<RegionalWddRow>` | None; reader emits target rows. |
| Sink | `DailyWddSqlSink : SqlSinkBase<DailyWddRow>` | `RegionalWddSqlSink : SqlSinkBase<RegionalWddRow>` | TVP bulk **MERGE** on the natural key (idempotent). Inherits `SqlWriteGate` serialization automatically. |

Shared services (registered once, used by both feeds): the `HttpClient`, the reference provider (§5), the optional FileLog writer (§9), and the `ILoadLogRepository` (platform-shared).

---

## 3. Work-unit definitions

One CSV = one work unit (agreed granularity). Both derive from `WorkUnit`.

- **`DailyWorkUnit`** — fields: `Model`, `InitDate` (DATE, UTC), `Cycle` (`"00"|"06"|"12"|"18"`), `WddType`. Identifies one `/{model}/{date}/{cycle}z/wdd/{type}-daily.csv`.
- **`RegionalWorkUnit`** — fields: `WkModel`, `InitDate`, `Cycle` (`"00"|"12"`), `WddType`, `RegionSet` (string code `"3"|"5"|"9"|"iso"`). Identifies one `/{wkmodel}/{date}/{cycle}z/wdd/{type}_reg{code}.csv` (URL token is literally `reg` + code → `reg3`/`regiso`).

Both carry enough to build the URL, the natural key, the two-zone `Key`, and (if enabled) the FileLog audit row. `DisplayName` is human-readable, e.g. `Daily gfs 20240804 00z ew_cdd` / `Regional ecmwf-weekly 20240804 00z ew_cdd reg3`.

---

## 4. Two-zone resume key (the central design)

The HTTP source exposes **no per-resource change signal** (no mtime, no ETag we can rely on). So instead of Platts' "key on the file's `LastModified`" we use **init-date age** as a proxy for "might this file still change or newly appear?".

Config boundary **`SettledAfterDays`** (default **21**). For a unit whose model init date is `InitDate`, with `todayUtc = context.StartedAtUtc.Date` (UTC) and `ageDays = (todayUtc - InitDate).Days`:

- **SETTLED zone** — `ageDays > SettledAfterDays`. Key is **stable**, so once the LoadLog records success for that key it is **skipped forever** (`BeginAsync` returns null). This is what makes the ~828k backfill cheap and resumable: re-invocations skip everything already done in O(1) per unit.
  - Daily:    `sv:daily:{model}:{yyyyMMdd}:{cycle}:{type}`
  - Regional: `sv:regional:{model}:{yyyyMMdd}:{cycle}:{type}:reg{N}`
- **HOT zone** — `ageDays <= SettledAfterDays`. Key **varies per run** so the LoadLog never reports "already done" → the unit is **always re-pulled and MERGEd**, capturing cycles published after an earlier run and any late data.
  - Daily:    `sv:daily:{model}:{yyyyMMdd}:{cycle}:{type}:run={hotSuffix}`
  - Regional: `sv:regional:{model}:{yyyyMMdd}:{cycle}:{type}:reg{N}:run={hotSuffix}`

**`hotSuffix` = the run's `RunId`** (recommended default; see the decision in §13). RunId guarantees every invocation — including multiple runs on the same calendar day — re-pulls the hot window, which is required to catch a `12z`/`18z` cycle that publishes *between* an early-morning and an afternoon run. (A run-date suffix `yyyyMMdd` would collapse same-day re-runs and **miss** intraday cycles; choose it only for a strictly once-daily schedule.)

**Empirical rationale for 21 days (verified):** publication is same-day (all four of *today's* cycles were live within the day; the next day the same URL 404s), and a fixed run's file is its **own immutable snapshot** (observed values differ across init-date runs because each file is a per-run analysis, not a shared later-revised actual). So realistically a file is fixed within a day or two; 21 days is a **generous safety margin**. The design is robust to *either* interpretation of the source: if files are immutable, the hot window merely re-collects newly-published cycles; if the API doc's "late-arriving observations backfill into existing rows" is instead true, the hot window + idempotent MERGE catch the revisions. Both are safe.

**Idempotency does not depend on this key.** Correctness comes from the sink **MERGE on the natural key** (§8). The LoadLog skip is only a cost optimization: settled units skip the network+parse+merge; hot units always redo it (a redo is harmless because MERGE is idempotent).

---

## 5. Discovery / enumeration (applicability matrix)

The API has **no discovery endpoint** (the enum sets were derived empirically and confirmed against the OpenAPI spec). The platform's "discovery first / refresh the stored list" principle is honored by treating the **seeded `sv` reference tables as the source of truth** and loading them at run start:

- **`IStormVistaReferenceProvider`** (singleton, load-once-per-process = per-run, reads the loader's own `StormVista` DB via `settings.ConnectionString`) loads and caches:
  - **Models** with `SupportsDaily` / `SupportsRegional` flags.
  - **Cycles** (`00/06/12/18`).
  - **WddTypes** (`ew_cdd, gw_hdd, pw_cdd`).
  - **RegionSets** (`3, 5, 9`) and, per set, the **ordered list of region names** (used both to enumerate and to validate the wide regional header — §8).
  - **Fail fast** if any required reference table is empty (a missing seed is a deployment error, not a silent no-op).

The reader **already** needs the seeded region-set definitions to validate regional columns, so sourcing all enums from the same tables keeps one source of truth and lets ops add a model/region/set via a seed change without a code change. Optional config include/exclude filters may narrow the matrix; default is "everything the reference tables list".

**Applicability matrix** (drives enumeration; all values from the authoritative spec, see `docs/apis/StormVista.md`). The daily and weekly model *kinds* are still distinct, but the **feeds now overlap**: every daily-type model also serves the Regional feed (`SupportsRegional = 1`), so a daily model is enumerated in **both** feeds. The daily/weekly sets are therefore **no longer disjoint** across feeds.

| Dimension | Daily feed | Regional feed |
|-----------|-----------|---------------|
| Models | `SupportsDaily = 1` — **17**: `gfs, gfs-ens, ecmwf, ecmwf-eps, gfs-ens-bc, cmc-ens, mlr15, mlr30, mlr45, ai-fourcastnetv2-gfs-ens, ai-fourcastnetv2-ecmwf-eps, ai-graphcast-gdas-ecmwf-eps, aifs, aifs-ens, ai-gfs, ai-gfs-ens, ai-weathernext2` | `SupportsRegional = 1` — **23**: the **17 daily-type** models above (they now serve regional too) **+ 6 weekly-only**: `cfs-weekly, gfs-ens-weekly, ecmwf-weekly, ai-fourcastnetv2-gfs-ens-weekly, ai-fourcastnetv2-ecmwf-eps-weekly, ai-graphcast-gdas-ecmwf-eps-weekly` |
| Cycles | `00, 06, 12, 18` (`mlr*` only 00/12) | **model-kind-dependent**: daily-type regional models → `00, 06, 12, 18`; weekly-only regional models → `00, 12` |
| Region-sets | n/a (national) | `3, 5, 9` (EIA) + `iso` (ISO/RTO) |
| Types | `ew_cdd, gw_hdd, pw_cdd` | **by region-set**: EIA (3/5/9) → `ew_cdd, gw_hdd, pw_cdd`; ISO (`iso`) → `pw_cdd, pw_hdd` |
| **Per init date** | 17 × 4 × 3 = **204** | daily-type regional 17 × 4 × 11 = **748** + weekly-only regional 6 × 2 × 11 = **132** = **880** (where 11 = 3 EIA × 3 types + 1 ISO × 2 types) |

**Regional cycles are model-kind-dependent (empirically verified).** Daily-type models serve the Regional feed at **all four cycles** (00/06/12/18 — e.g. `gfs-ens-bc` reg3 was verified to return data at 00z/06z/12z/18z), so they are enumerated with the **daily** cycle set. Weekly-only models serve regional at **00/12 only** (verified); enumerating them at 06/18 would be a *guaranteed* 404 (~132 phantom units/init-date — wasted requests + `NotAvailable` audit noise every run). So the regional enumerator selects the cycle set **per model**: `SupportsDaily == true` ⇒ daily cycles (00/06/12/18), else regional cycles (00/12). This is the **confirmed** choice (scope = everything: all 23 regional models at their applicable cycles).

**Per-init-date total (confirmed scope = everything):** daily national **204** + regional **880** (daily-type 748 + weekly-only 132) = **~1,084 files/init-date**.

The **type×region-set applicability** and **cycle applicability** rules must be data-driven from reference tables (§14: a `sv.RegionSetWddType` bridge + a `sv.Cycle` table with per-feed support flags; the regional cycle choice keys off each model's own `SupportsDaily` flag), so enumeration never emits a *guaranteed* 404 (e.g. `pw_hdd` on an EIA set, `ew_cdd` on ISO, 06z/18z on a **weekly-only** regional model, `ecmwf-weekly` on daily). The reader still tolerates 404 for init-dates/cycles simply not published yet. AI models have short history (they 404 for old backfill dates → skip).

---

## 6. Run modes, windowing, and the per-window loop

Both modes use the **same** per-unit machinery; the mode only selects the date range and the chunk size.

### 6.1 Incremental (default, for scheduled runs)
- Range: `end = todayUtc`, `start = todayUtc - DaysBack` (default `DaysBack = SettledAfterDays = 21`, so the incremental window fully covers the hot zone).
- One manageable pass (~324 × 21 ≈ **6,800 units**). Every unit in this range is hot (or just crossing into settled at the edge) → effectively a full re-pull of the recent window each run, which is the intent.

### 6.2 Backfill (operator-supplied bounded range)
- Range: `start = BackfillStart` (required), `end = BackfillEnd ?? todayUtc`. Earliest archive = **2018-07-08**.
- The range is **chunked internally** into ordered windows so a single invocation walks the whole range without ever building the full list. Recommended chunk = **one calendar month** (or a configurable `ChunkDays`, default 30). Iterate **oldest → newest** for deterministic, restart-friendly progress.
- Resumability: settled units already loaded are LoadLog-skipped on re-invocation; the `SqlLoaderOverlapGuard` prevents two concurrent StormVista processes. So an interrupted backfill is simply **re-run** — it re-walks the windows but skips completed settled units cheaply, and picks up where it left off. Backfill is a long, resumable, **multi-invocation** operation (§13 residual risks).

### 6.3 Per-feed pipeline algorithm (`StormVistaFeedPipeline.ExecuteAsync`)

1. Ensure the reference is loaded (§5).
2. Resolve mode → `[start, end]` (UTC dates). `context.DateFrom/DateTo`, if set, may override the resolved range (optional; document precedence).
3. Build the ordered window list covering `[start, end]` (incremental → typically a single window; backfill → month-by-month). Streamed/lazy, not fully materialized.
4. **For each window, sequentially:**
   1. Honor `context.CancellationToken`.
   2. **Enumerate this window's units**: for each init date in the window × each applicable enum combo (from the reference) → construct the `TUnit`, computing its **two-zone `Key`** (§4) from `ageDays` and `hotSuffix`. Bounded to ~`72×days` (daily) / ~`252×days` (regional) — one month ≈ 2.2k / 7.5k units.
   3. **Process the window** by delegating to a standard `LoaderPipelineBase<TUnit, TRow, TRow>` instance whose `IWorkUnitProvider` is a trivial static provider returning exactly this window's precomputed list (`StaticWorkUnitProvider<TUnit>`). Reuse the same `source`, identity transformer, `sink`, `loadLog`, and `settings` (so `MaxConcurrentWorkUnits`, retry, per-unit timeout all apply). This runs the vetted loop: `BeginAsync` (skip settled-already-done) → read (404→0 rows) → MERGE → `CompleteSuccess/Failure`, with `ParallelRunner` bounding concurrency to `MaxConcurrentWorkUnits`.
   4. Accumulate that window's `LoaderRunResult` counters; log a window summary (useful backfill progress signal).
5. Return the aggregate `LoaderRunResult` for the feed.

> Implementation note for CODER: the per-window `LoaderPipelineBase` emits its own "Pipeline starting" log per window — acceptable and informative. This reuse path is preferred over re-implementing the per-unit loop so behaviour cannot drift from Core. If you instead inline the loop, it must replicate `LoaderPipelineBase.ProcessOneAsync` exactly (skip-on-null-handle, per-unit timeout via linked CTS, fail-one-not-the-run, run-cancellation rethrow).

---

## 7. HTTP wiring, 404-as-skip, and error semantics

HTTP plumbing is registered like EnergyAspects (`AddHttpClient<...>` + `AddPolicyHandler(RetryPolicyFactory.BuildHttpRetryPolicy(...))`), reusing the platform's `HttpClient`/Polly. **But the source readers do NOT go through `HttpJsonSourceReaderBase.ReadAsync`** (which does `EnsureSuccessStatusCode` + JSON deserialize). They implement `ISourceReader.ReadAsync` directly:

1. Build the URI (path + `?apikey=<key>`). **Log only the sanitized path** (strip the query string; never log the api key). Base URL, timeout, and retry come from the registered `HttpClient` + policy.
2. `await HttpClient.GetAsync(uri, ct)`. Polly retries **transient** failures (5xx / 408 / network) automatically.
3. **404 → return empty list** (success, 0 rows). Do **not** throw, do **not** wrap in a retryable exception. 404 is not transient, so `HandleTransientHttpError()` already excludes it from retry — keep it that way. (Optionally record a FileLog `NotAvailable` row — §9.)
4. **200 (success) →** read body as **text** (`ReadAsStringAsync`), parse the CSV per feed shape (§8), return rows. (Optionally record FileLog `Success`.)
5. **Any other non-success** (401/403 auth, 429 throttle, 5xx after retries exhausted) → **throw** → the unit is recorded as a failure in the LoadLog and the run continues to the next unit (fail-a-block-not-the-run). 401/403 almost certainly means a bad/absent api key and should be loud.

**Rate limiting (documented: 3000 req/min total, 2000/min non-realtime; 429 on exceed).** Two configurable controls:
- `MaxConcurrentWorkUnits` (bounded concurrency, existing base setting).
- Optional global **`RequestsPerSecond`** throttle implemented as a `DelegatingHandler` (token bucket) on the shared `HttpClient` so it applies to every request, retries included. Default it below the non-realtime cap (~33/s); `0`/absent = unlimited.
- **Extend the retry policy to treat 429 as transient and honor `Retry-After`** — `HandleTransientHttpError()` does **not** include 429 by default, so without this a throttled backfill would mass-fail units. The spec confirms 429 responses carry a header naming the violated limit. See §13.

---

## 8. CSV parsing, regional unpivot, and the MERGE keys

Use a quote-aware RFC-4180 splitter (the same approach as `SymbolSourceReader.ParseCsv`) — the daily flag header is quoted (`"Flag (0=obs 1=fcst 2=norm)"`). Parse dates as `yyyy-MM-dd`, decimals with `InvariantCulture`. Tolerant-parse philosophy: skip a bad **cell/row** with a warning; do **not** fail the whole file for a single malformed value.

### 8.1 Daily (national) → `DailyWddRow`
- Header row: 3 columns `Date, Value, Flag(...)`. Map **by position** after confirming ≥ 3 columns (the flag header text is verbose/quoted).
- Each data row → `(Model, InitDate, Cycle, WddType, ValidDate, Value, Flag)`:
  - `ValidDate` from column 0; unparseable/blank date → **skip the row** (warn).
  - `Value` from column 1; blank/`NaN`/unparseable → **skip the row** (warn).
  - `Flag` from column 2, integer in **{0,1,2}** (0=obs, 1=fcst, 2=norm); outside the domain → **skip the row** (warn) so it can't violate the DB check constraint. Confirm parse against the sample (obs rows before init, fcst around/after, norm at the tail).
- **Natural key (MERGE):** `(Model, InitDate, Cycle, WddType, ValidDate)`. Different init dates/cycles for the same `ValidDate` are distinct rows by design (this preserves the per-run/immutable-snapshot semantics — no collision).

### 8.2 Regional (wide → long) → `RegionalWddRow`
Forecast-only; **no flag column**. Flow:
1. Parse the header row. Column 0 must be `Date` (trim/case-insensitive). Remaining columns are region names, in order.
2. Look up the expected **ordered region name set** for `reg{N}` from the seeded reference (§5).
3. **Validate the column set** against the seeded set — fail on **unknown/extra** or **duplicate** columns, but **tolerate missing** seeded regions:
   - An **unexpected extra** (or duplicate) column → **fail this unit loudly** (throw → LoadLog failure, run continues). An unknown column can't be mapped to a `RegionId` and signals StormVista added a region we don't know about → reseed `dbo.Region`.
   - A **missing** seeded region is **NOT** a failure. **Region sets grow over time** (e.g. ISO grew 18 → 21 by adding `southwest`/`caisonorth`/`caisosouth` between 2024 and 2026), so a historical file legitimately carries a **subset** of the latest seeded set. Log once at Information and load the columns that **are** present; do not emit rows for the absent regions.
   - Build a column-index → region-name map from the present columns (every present column is a known region once extras are rejected). (Order: validate the *set* of present columns; warn on reorder by comparing against the seeded order **restricted to the present regions**.)
4. For each data row:
   - `ValidDate` from column 0; unparseable/blank → **skip the row** (warn).
   - For each region column: read the cell. Blank/`NaN`/unparseable value → **skip that cell** (warn), continue with the other regions (Platts-style tolerant). Otherwise emit `(Model, InitDate, Cycle, WddType, RegionSetN=N, Region, ValidDate, Value)`.
5. **Natural key (MERGE):** `(Model, InitDate, Cycle, WddType, RegionSetN, Region, ValidDate)`.

Region names are **scoped to their set** (`East` in reg3 ≠ `East` in reg5), so `RegionSetN` is part of the key and the FK target (§14).

---

## 9. Optional audit — `sv.FileLog`

Analogous to Platts `arm.FileLog`, for observability across the huge request volume. One row per request outcome, upserted by `(Feed, RequestPath)`: `Feed, RequestPath (sanitized — no apikey), Model, InitDate, Cycle, WddType, RegionSetN (NULL for daily), HttpStatus, Status ∈ {Success, NotAvailable, Failed}, RowCount, LastCheckedUtc`.

**Wiring nuance (differs from Platts):** Platts logs via a sink *decorator* because every unit yields rows and reaches the sink. Here a **404 yields 0 rows and never reaches the sink**, so the `NotAvailable` and `Success` audit rows must be written **from the source reader** (it holds the HTTP status + row count); the **`Failed`** row is written from the pipeline's catch path. Gate the whole thing behind `EnableFileLog` (see §13 for the backfill-scale caveat).

---

## 10. Post-load validation (in-pipeline, CLAUDE.md requirement)

`LoaderPipelineBase` has **no post-load hook**, and StormVista's module already owns orchestration, so the in-pipeline quality check runs as a **module-level step in `StormVistaModule.RunAsync`, after both feed pipelines complete** and before returning the aggregate result. Recommended implementation: a `StormVistaLoadValidator` that calls a validation proc (`sv.usp_ValidateLoad`, DATABASE_DEVELOPER) or runs a few SQL checks, scoped to the run's date range:

- Row counts by (feed, init date, model) are non-zero where expected.
- `DailyWdd.Flag` domain ⊆ {0,1,2}; `Value` null-rate within a sane bound.
- `RegionalWdd` region coverage per set matches the expected count (3/5/9).
- No orphan model/type/cycle/region vs. reference tables (FKs should already prevent this — this is a cross-check).

Keep it **observational** (log warnings + counters; optionally fail the run only on a hard invariant breach) so a normally-sparse day (many legitimate 404s) doesn't fail an otherwise-good load. The separate `DATA_QUALITY_VALIDATOR` agent runs its deeper reconciliation after a real load.

---

## 11. Config surface — `StormVistaSettings : LoaderSettingsBase`

Inherited: `ConnectionString` (the `StormVista` DB), `MaxConcurrentWorkUnits`, `RetryCount`, `RetryDelayMs`, `WorkUnitTimeoutSeconds`.

Added:

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `BaseUrl` | string | `https://api.stormvistawxmodels.com/v1` | API base. |
| `ApiKey` | string | *(env only)* | `DATALOADER_Loaders__StormVista__ApiKey`; never in JSON/logs. |
| `HttpTimeoutSeconds` | int | e.g. 60 | Per-request timeout on the `HttpClient`. |
| `EnabledFeeds` | string[] | `["Daily","Regional"]` | Feed toggle (Platts pattern). |
| `Mode` | enum `{Incremental,Backfill}` | `Incremental` | Selects date range + chunking. |
| `DaysBack` | int | 21 (= `SettledAfterDays`) | Incremental window length. |
| `SettledAfterDays` | int | 21 | Hot/settled boundary (§4). |
| `BackfillStart` | date? | — | Required in Backfill mode (archive ≈ 2018-07-08). |
| `BackfillEnd` | date? | `todayUtc` | Backfill range end. |
| `ChunkDays` / `ChunkBy` | int / enum | month (~30) | Backfill window size (§6.2). |
| `RequestsPerSecond` | double? | conservative (< 33/s) | Global client-side throttle (§7). |
| `HotZoneKeyStrategy` | enum `{RunId,RunDate}` | `RunId` | Hot-key suffix (§4 / decision below). |
| `EnableFileLog` | bool | `true` incremental / `false` backfill | Audit toggle (§9 / caveat below). |
| *(optional)* `Models`/`Cycles`/`Types`/`RegionSets` filters | string[]/int[] | reference tables | Narrow the matrix if ever needed. |

---

## 12. Resume-model contrast (why this design)

| Loader | Change signal from source | Key strategy | Revisit behaviour |
|--------|---------------------------|--------------|-------------------|
| **EnergyAspects** | none | stable `mapping + fixed date window`; skip if done | Re-pulls the configured rolling window each run; once a block succeeds it isn't revisited. No settled/hot distinction. |
| **Platts** | explicit `LastModified` (mtime) per file | key embeds `size:mtime`; a file is reprocessed **only when it changes** | Exact — driven by the file's own change signal. |
| **StormVista** | **none per resource** we key on (HTTP CSV; `Last-Modified`/`ETag` exist but we don't HEAD-check per unit) | **two-zone**: stable key for **settled** init dates (skip forever), volatile key for **hot** init dates (always re-pull) | Uses **init-date age (`SettledAfterDays`)** as a proxy for "still changing / not-yet-published". |

StormVista combines Platts' intent ("reprocess when the source could still change") with EnergyAspects' rolling re-pull, but implements it with a time-based heuristic. The settled zone makes the enormous one-time backfill cheap and idempotently resumable; the hot zone keeps the recent window fresh. (A `Last-Modified`/HEAD conditional-GET optimization is available per the spec and could later replace the hot-zone re-pull, but is not required.)

---

## 13. Residual risks / decisions for a reviewer

1. **Hot-key suffix (RunId vs RunDate)** — *recommended: RunId.* RunId re-pulls the hot window on every invocation, so multiple same-day runs catch cycles that publish later in the day. RunDate is cheaper (one LoadLog row per unit per day, collapses same-day re-runs) but **misses intraday cycles**. Confirm the schedule: if strictly once-daily, RunDate is equivalent and lighter.
2. **429 not retried by default** — `HandleTransientHttpError()` excludes 429. Under the documented rate limit, a throttled backfill would mass-fail units. **Recommend** extending the StormVista HTTP policy to treat 429 as transient and honor `Retry-After`, plus a conservative `RequestsPerSecond` default (< 33/s). Needs a reviewer decision on the default RPS.
3. **Backfill scale (~828k requests).** This is a long, resumable, multi-run operation, not a single pass. Rate-limit-bound wall-clock ≈ 828k / 2000 per min ≈ 7 h at the non-realtime ceiling; the overlap guard + settled-skip make repeated invocation safe. Set operator expectations and pick sensible concurrency/RPS.
4. **LoadLog growth (hot zone, RunId keying).** ~6,800 new `core.LoadLog` rows per incremental run (~2.5M/yr on a daily schedule). Acceptable for SQL Server, but agree a retention/prune policy with DATABASE_DEVELOPER (`core.LoadLog` is platform-shared, not owned by this loader).
5. **`sv.FileLog` at backfill scale.** A per-request gated upsert (`SqlWriteGate` serializes calls to one proc) would become a global bottleneck across ~828k requests. Hence `EnableFileLog` defaults **off for backfill / on for incremental**; alternatively batch FileLog writes. Confirm the preference.
6. **`SettledAfterDays = 21` is a heuristic.** If the vendor ever revises files older than 21 days (contra the verified immutability finding, but the API doc hints at late-arriving obs), those revisions are missed until an occasional wider backfill or a larger `SettledAfterDays`. Documented as a known trade-off.
7. **Region-set drift — asymmetric.** An **unknown/extra** column (StormVista added a region not in the seed) fails the affected units loudly (§8.2) until `dbo.Region`/`dbo.RegionSet` is reseeded — the correct data-integrity posture, treat a wave of these as a "reseed needed" signal. A **missing** seeded region is **tolerated**, not a failure: region sets grow over time (ISO 18 → 21), so historical files carry a subset and the present columns still load.
8. **UTC everywhere.** Init date, cycle, and the "today"/age boundary are UTC; compute `ageDays` and the incremental window in UTC to stay aligned with the vendor's same-day publication.
9. **Backfill window direction** — oldest→newest recommended for deterministic resume; a reviewer may prefer newest→oldest to prioritize recent data on a fresh backfill. Minor.

---

## 14. Handoff — concrete inputs for DATABASE_DEVELOPER and CODER

### For DATABASE_DEVELOPER (new `StormVista` DB, `sv` schema)
Everything below is what the loader relies on; exact column types follow the `docs/apis/StormVista.md` "Recommended SQL types" section.

- **Seeded reference tables** (loader reads these at run start; seed scripts required, FKs from facts recommended as defense-in-depth). Enum values are authoritative in `docs/apis/StormVista.md`:
  - `dbo.Model` — PK `ModelSlug VARCHAR(40)` (longest slug 34 chars), `SupportsDaily BIT`, `SupportsRegional BIT`, optional `DisplayName`, optional `IsExperimental BIT` (AI/MLR). Seed **23 rows** — the **17 daily** models (SupportsDaily 1 / **SupportsRegional 1** — they also serve regional, per the 2026-08-06 scope expansion; see the SCHEMA v2 banner + §5) and the **6 weekly** models (0/1). All 23 support regional; the daily set additionally supports daily, so the daily and regional sets **overlap** (not disjoint). *(Note: this §14 was the original v1 handoff — schema is now `dbo`, per the top banner which is authoritative where they differ.)*
  - `sv.Cycle` — PK `CycleCode CHAR(2)` in {`00`,`06`,`12`,`18`}, with `SupportsDaily BIT`/`SupportsRegional BIT` (00→1/1, 06→1/0, 12→1/1, 18→1/0) so per-feed cycle applicability is data-driven.
  - `sv.WddType` — PK `TypeSlug VARCHAR(10)` in {`ew_cdd`,`gw_hdd`,`pw_cdd`,`pw_hdd`}, plus `Weighting` (energy/gas/population) and `Metric` (CDD/HDD).
  - `sv.RegionSet` — PK `RegionSetCode VARCHAR(4)` in {`3`,`5`,`9`,`iso`} (string, **not** TINYINT — `iso` is non-numeric), optional `Description`/`Kind` (EIA vs ISO).
  - `sv.RegionSetWddType` — **bridge** (`RegionSetCode` FK, `TypeSlug` FK) encoding type×region applicability: EIA sets (3/5/9) × {ew_cdd,gw_hdd,pw_cdd} + iso × {pw_cdd,pw_hdd} = **11 rows**. Drives regional enumeration.
  - `sv.Region` — `RegionSetCode` (FK) + `RegionName VARCHAR(30)` + `Ordinal`; key on (`RegionSetCode`,`RegionName`). Seed the ordered names from the `docs/apis/StormVista.md` region-set table (reg3=3, reg5=5, reg9=9, **regiso=21 ISO/RTO codes**). The loader validates the wide header against these.
- **Fact tables + TVP types + merge procs** (idempotent MERGE on the natural key; procs return affected row count so `SqlSinkBase` reports it):
  - `sv.DailyWdd` — natural key `(Model VARCHAR(40), InitDate DATE, Cycle CHAR(2), WddType VARCHAR(10), ValidDate DATE)`; `Value DECIMAL(9,4) NULL`, `Flag TINYINT` check ∈ {0,1,2}; optional derived `InitDatetimeUtc DATETIME2(0)`. + `sv.DailyWddTvp` (column order fixed) + `sv.usp_BulkMergeDailyWdd(@Records sv.DailyWddTvp)`.
  - `sv.RegionalWdd` — natural key `(WkModel VARCHAR(40), InitDate DATE, Cycle CHAR(2), WddType VARCHAR(10), RegionSetCode VARCHAR(4), RegionName VARCHAR(30), ValidDate DATE)`; `Value DECIMAL(9,4)`; **no Flag**. + `sv.RegionalWddTvp` + `sv.usp_BulkMergeRegionalWdd`.
- **Optional `sv.FileLog`** + `sv.usp_UpsertFileLog` (upsert by `(Feed, RequestPath)`) — columns per §9.
- **Optional `sv.usp_ValidateLoad`** — returns the anomaly counts of §10 for the run's date range.
- Note on `core.LoadLog` retention (platform-shared) — §13.4.

### For CODER (project `src/DataLoader.StormVista/`, follow "Adding a New Loader")
- **Classes:** `StormVistaModule`, `StormVistaSettings`, `IStormVistaFeedPipeline` + `StormVistaFeedPipeline<TUnit,TRow>` (custom windowed `ILoaderPipeline`), `DailyWorkUnit`/`RegionalWorkUnit`, `DailyWorkUnitProvider`/`RegionalWorkUnitProvider` (window-scoped, §6.3) + a `StaticWorkUnitProvider<T>` window adapter, `DailySourceReader`/`RegionalSourceReader` (custom HTTP+CSV, §7–8), `DailyWddRow`/`RegionalWddRow`, `DailyWddSqlSink`/`RegionalWddSqlSink`, `IStormVistaReferenceProvider` (+ SQL impl), optional `IStormVistaFileLog` (+ writer) and a `RequestsPerSecond` `DelegatingHandler`, and `StormVistaLoadValidator`.
- **Two-zone key formulas** — §4 (compute `ageDays` in UTC; `hotSuffix` from `HotZoneKeyStrategy`).
- **Enumeration** from the reference matrix — §5; window chunking — §6.
- **404 = empty result, not throw, not retried; 429/5xx handling** — §7.
- **Regional unpivot + column-set validation (fail on unknown/extra or duplicate; tolerate missing seeded regions) + tolerant cell parse** — §8.2; **daily flag parse** — §8.1.
- **Module wiring:** two explicit pipelines + `EnabledFeeds` toggle (Platts pattern), reference loaded once, feeds run sequentially, **post-load validation runs module-level in `RunAsync` after both feeds** — §1, §10.
- **Config binding** from `Loaders:StormVista`; api key from env; never log the api key (sanitize URIs) — §7, §11.
- Register in `DataLoader.Host` project reference + `appsettings.json` `Loaders:StormVista` + add `"StormVista"` to `Platform:EnabledLoaders`.

---

## Notes

- Enum sets (models, cycles, types, region-sets) were verified against the authoritative OpenAPI spec (`stormvista.yaml`) in addition to empirical probing — see `docs/apis/StormVista.md`.
