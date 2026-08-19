# AGSI (GIE Aggregated Gas Storage Inventory) loader — design & processing flow

Design/flow spec for the AGSI loader. Input of record is the field reference at
`docs/apis/AGSI.md` (two endpoints, full field sets, per-field SQL types, natural keys,
DECIMAL sizing). This document is the CODER hand-off; it contains **no code and no SQL**.
SQL objects named here are *proposed* to DATABASE_DEVELOPER (open items collected in §11).

Locked decisions this design is built around: DB `AGSI`, schema `arm`, Integrated Security;
`ApiKey` via `SEE_DB`, sent to endpoint 2 as the HTTP request **header `x-key`** (endpoint 1
sends **no** key); **two** endpoints modelled as **two explicitly-built closed pipelines**
run **sequentially, entities first** (pipeline 2's work units are driven by pipeline 1's
country list); one `arm.FileLog` hub + two flat fact tables (`arm.GasStorageEntity`,
`arm.GasStorage`) carrying `FileLogId`; go-forward-only trailing-window resume with the
StormVista two-zone (settled/hot) key **borrowed, not the windowing orchestrator**; a
**CET/Europe** run-date basis for "newest gas day"; **build-only pass** — the loader is wired
but left **out of `Platform:EnabledLoaders`**, and no live deploy/load/validation is exercised
this pass.

> **Build-only posture (read first).** This is the same posture CWG shipped in: everything is
> coded, unit-tested, and buildable, but the loader is **disabled by default** (`"AGSI"` is
> **not** in `Platform:EnabledLoaders`) and there is no live DB deploy or data load. Live
> deploy + first load + `DATA_QUALITY_VALIDATOR` are deferred to a later pass, when a real
> `x-key` is available to close the four ⚠ items the API doc flagged (no-data body,
> multi-page envelope, `updatedAt` timezone, rate-limit numbers). The design must **tolerate**
> all four unknowns without a live probe.

Reference implementations mirrored: `src/DataLoader.StormVista/` (HTTP source that returns a
body we parse ourselves — here JSON, not CSV; 404-tolerant custom `ISourceReader` that does
**not** go through `HttpJsonSourceReaderBase`; per-request `arm.FileLog` upsert returning a
`FileLogId` that stamps rows; `.RemoveAllLoggers()`; sanitized-path logging; a load-once
reference provider; a module-level post-load validator; explicit per-pipeline DI wiring) and
`src/DataLoader.CWG/` (`arm` schema + `arm.FileLog` hub; closed per-endpoint pipelines built
explicitly so the shared generics are never resolved from DI; **`LoaderPipelineBase` used
directly** because the unit count is tiny; disabled build-only posture).

> **Discovery-first note (this loader HAS a real discovery step).** Unlike CWG/StormVista
> (no discovery endpoint), AGSI's endpoint 1 `GET /api/about` **is** the discovery/mapping
> step: it yields the country codes that endpoint 2's per-country queries need. The platform's
> "discovery first / refresh the stored list before processing" principle is therefore honored
> literally — the entities pipeline runs first, refreshes `arm.GasStorageEntity` in place, and
> the storage pipeline enumerates its work units from that just-refreshed list (§2).

---

## 0. Class / structure inventory

| Concern | Type(s) | Notes |
|--------|---------|-------|
| Settings | `AgsiSettings : LoaderSettingsBase` | shared (§9) |
| Module | `AgsiModule : ILoaderModule` (`LoaderId = "AGSI"`) | builds both pipelines explicitly |
| Pipeline marker | `IAgsiPipeline : ILoaderPipeline { string EndpointId }` | so `RunAsync` can enumerate + toggle + order |
| Pipeline | `AgsiPipeline<TUnit,TRow> : LoaderPipelineBase<TUnit,TRow,TRow>, IAgsiPipeline` | one generic class, two closed instances; identity transform |
| Work unit (1) | `AgsiEntitiesWorkUnit : WorkUnit` | single undated unit, always hot |
| Work unit (2) | `AgsiStorageWorkUnit : WorkUnit` | one `(EntityId, countryCode, date)`; two-zone key |
| Provider (1) | `AgsiEntitiesWorkUnitProvider : IWorkUnitProvider<AgsiEntitiesWorkUnit>` | returns one unit/run |
| Provider (2) | `AgsiStorageWorkUnitProvider : IWorkUnitProvider<AgsiStorageWorkUnit>` | country list × date window (§3) |
| **Reference handoff** | `IAgsiCountryProvider` (+ `SqlAgsiCountryProvider`) | load-once-per-run distinct `(Id, Code)` from `arm.GasStorageEntity` via `usp_GetGasStorageEntities` (§2) |
| Source reader (1) | `AgsiEntitiesSourceReader : ISourceReader<AgsiEntitiesWorkUnit, GasStorageEntityRow>` | walks the `SSO→region→country→[]` tree, dedups → `Code` |
| Source reader (2) | `AgsiStorageSourceReader : ISourceReader<AgsiStorageWorkUnit, GasStorageRow>` | `x-key` header; 404/empty/`status:"N"`-tolerant |
| JSON models | `AgsiStorageEnvelope` (`last_page`,`total`,`dataset`,`gas_day`,`data[]`), `AgsiStorageRecord` (the 22 `data[]` fields) | endpoint 2 deserialization target |
| Fact rows | `GasStorageEntityRow`, `GasStorageRow` (both carry `int FileLogId`) | one per table |
| Sinks | `GasStorageEntitySqlSink : SqlSinkBase<GasStorageEntityRow>`, `GasStorageSqlSink : SqlSinkBase<GasStorageRow>` | TVP bulk MERGE on the natural key |
| FileLog | `AgsiFileContext` (readonly struct), `IAgsiFileLog`, `SqlAgsiFileLog` (`arm.usp_UpsertFileLog`, `SqlWriteGate`, returns `FileLogId`) | shared hub (§5) |
| Post-load validation | `AgsiLoadValidator` (module-level hook) | coded now, not exercised live (§8) |
| HTTP plumbing | rate limiter + delegating handler + Polly policy (mirror StormVista/CWG) | shared |

The only endpoint-specific C# is the two `TUnit`s, the two providers, the two source readers,
the two `TRow`s + their sinks, and the JSON models. Everything else (the pipeline loop,
idempotency, FileLog, throttle, retry) is written once or inherited from Core.

---

## 1. Module topology & pipeline strategy

**One module, two closed pipelines**, built explicitly (Platts / StormVista / CWG pattern),
toggled by `EnabledEndpoints[]` (`"About"`, `"Storage"`), and run **sequentially with
entities (About) first**.

### 1.1 Why two closed pipelines, entities first

The two endpoints have different work-unit types (`AgsiEntitiesWorkUnit` vs
`AgsiStorageWorkUnit`), different readers, different tables, and a **hard ordering
dependency**: the storage pipeline's work units are the distinct country `Code`s written by
the entities pipeline. So the two are assembled as separate closed pipelines and **About runs
to completion before Storage starts**. We **never register a generic
`IWorkUnitProvider<T>` / `ISourceReader<T>` in DI** (the collision Platts/StormVista/CWG warn
about); each pipeline is `new`ed inside a module factory closure and handed to
`AgsiPipeline<TUnit,TRow>`'s constructor, so its generics are never resolved by the container.

### 1.2 Why `LoaderPipelineBase` directly (CWG-style), NOT StormVista's windowed orchestrator

StormVista needed a custom windowed `ILoaderPipeline` because a full backfill is ~828k units
(materializing them all at once is untenable). AGSI is the opposite extreme: **entities = 1
unit; storage ≈ dozens of countries × `DaysBack` (21) days ≈ a few hundred units per run.**
The whole unit list materializes cheaply, so AGSI uses **`LoaderPipelineBase` directly** (like
CWG) and reuses the vetted per-unit loop unchanged — `BeginAsync` idempotency skip → `ReadAsync`
→ identity transform → `WriteAsync` → `CompleteSuccess/Failure`, bounded by `ParallelRunner`
at `MaxConcurrentWorkUnits`, per-unit timeout, fail-one-not-the-run. **AGSI borrows
StormVista's two-zone resume key (§3.3), not its orchestrator.** No backfill mode, no
`ChunkDays`, no windowing.

### 1.3 DI registration (`RegisterServices`)

1. `services.AddLoaderSettings<AgsiSettings>(configuration, Id);` (binds `Loaders:AGSI`;
   resolves `ApiKey="SEE_DB"` from `core.Param` lazily at run time).
2. Shared throttle: `AgsiRateLimiter` (singleton) + `AgsiRateLimitingHandler` (transient).
3. Named `HttpClient` `"AGSI"`:
   - `Timeout = HttpTimeoutSeconds`, `Accept: application/json`;
   - `.RemoveAllLoggers()` — suppress `IHttpClientFactory` default logging (it logs the full
     URI). Endpoint-2 auth is a **header**, so the URL carries no secret, but we still remove
     the default loggers for parity and to keep the log surface identical to CWG/StormVista;
   - `.AddPolicyHandler(...)` retry **outer** (Polly; `RetryCount`/`RetryDelayMs`; treat
     `429`/`5xx`/transient as retryable and honor `Retry-After` per the API doc's rate-limit
     warning);
   - `.AddHttpMessageHandler<AgsiRateLimitingHandler>()` throttle **inner** so every attempt is
     paced by `RequestsPerSecond`.
4. `services.AddSingleton<IAgsiFileLog, SqlAgsiFileLog>();`
5. `services.AddSingleton<IAgsiCountryProvider, SqlAgsiCountryProvider>();` (§2).
6. Register the two pipelines with a small factory helper, one line each:
   - `About`  → provider `AgsiEntitiesWorkUnitProvider`, reader `AgsiEntitiesSourceReader`
     (**no** `x-key`), sink `GasStorageEntitySqlSink`.
   - `Storage`→ provider `AgsiStorageWorkUnitProvider` (needs `IAgsiCountryProvider`), reader
     `AgsiStorageSourceReader` (`x-key`), sink `GasStorageSqlSink`.
   Each helper constructs the provider/reader/sink bound to that endpoint and wraps them in
   an `AgsiPipeline<TUnit,TRow>(endpointId, provider, source, sink, loadLog, settings, logger)`
   — exactly the CWG `BuildPipeline` shape.

### 1.4 `RunAsync` fan-out (mirror `CwgModule.RunAsync` + `StormVistaModule` ordering)

1. Read `AgsiSettings`. Resolve `enabled = HashSet(EnabledEndpoints, OrdinalIgnoreCase)`.
2. **Fail fast on the key only when it is actually needed.** Endpoint 1 needs no key; endpoint
   2 does. So: if `enabled` contains `"Storage"` **and** `ApiKey` is blank or still the
   `"SEE_DB"` placeholder → log an error (never the key value) and return
   `LoaderRunResult.Failed`. If only `"About"` is enabled, an absent key is fine at *this* check.
   **Implementation caveat (build-only, confirmed in review):** materializing
   `IOptions<AgsiSettings>` runs the shared `SeeDbSettingsResolver`, which **throws if
   `core.Param(LoaderName='AGSI', ParamName='ApiKey')` is missing** — and that happens *before*
   this endpoint-aware check. So in practice **any** AGSI run (even About-only) requires that
   `core.Param` row to exist; a **placeholder value is fine for an About-only run** (About sends
   no key). This §1.4 check is therefore a **secondary guard** that catches a value left as the
   literal `SEE_DB` placeholder when `"Storage"` is enabled. Deferring SEE_DB resolution to make
   About-only truly key-free is a larger change, deliberately **not** taken this pass.
3. Enumerate `services.GetServices<IAgsiPipeline>()`; warn for any enabled id with no matching
   pipeline; if none enabled → warn, return `Success = true`.
4. **Run in a fixed order: About first, then Storage** (not just "sequential" — the order is
   load-bearing because Storage reads the country list Entities writes). Concretely: run the
   `About` pipeline if enabled; then run the `Storage` pipeline if enabled. Within each,
   `ExecuteAsync` fans its work units out concurrently via `ParallelRunner`
   (`MaxConcurrentWorkUnits`); the single shared rate-limited client bounds global RPS.
5. After both complete, run the **module-level post-load validation** (§8) scoped to the run's
   date window.
6. Aggregate the per-pipeline `LoaderRunResult`s (sum totals; `Success = all succeeded`)
   exactly as CWG/StormVista/Platts do.

> **Storage-only runs.** If an operator enables only `"Storage"`, the entities pipeline does
> not run this invocation and the storage provider reads whatever `arm.GasStorageEntity`
> already holds (from a prior run). If that table is empty, `IAgsiCountryProvider` fails fast
> (§2) — a storage-only first run with no prior entities is a configuration error, surfaced
> loudly rather than silently loading nothing.

---

## 2. Reference handoff — country codes from pipeline 1 → pipeline 2 (central design point)

This is the AGSI analog of StormVista's `IStormVistaReferenceProvider`: a **load-once-per-run**
reference cache. The country codes flow **through the database**, not through an in-memory
hand-off between the two pipelines:

- The entities pipeline MERGEs the distinct country tuples into `arm.GasStorageEntity`.
- The storage provider reads the **distinct `(Id, Code)`** back out of that table (once per run)
  via `usp_GetGasStorageEntities`, crosses it with the date window, and emits the storage work
  units — each unit stamped with its `EntityId` (the `Id`), which the reader then copies onto
  every fact row.

**Interface & lifetime.**

```
// distinct (Id, Code) pairs — Id is arm.GasStorageEntity.Id (the FK target that
// arm.GasStorage.EntityId points at); Code is the endpoint-2 query code.
interface IAgsiCountryProvider
{
    Task<IReadOnlyList<AgsiCountry>> GetCountriesAsync(CancellationToken ct);
}

readonly record struct AgsiCountry(int Id, string Code);
```

(Recommended: expose the cache additionally as a `Code→Id` map so the storage provider can
resolve each country's `EntityId` cheaply when it stamps the work unit.)

- **`SqlAgsiCountryProvider`** — registered **singleton** (= one instance per process = per
  run). First call executes `arm.usp_GetGasStorageEntities` (which returns distinct `(Id, Code)`
  pairs ordered by `Code`) against `settings.ConnectionString`, **caches** the list behind a
  `SemaphoreSlim` double-checked lock (the exact shape of
  `SqlStormVistaReferenceProvider.GetAsync`), and serves the cache thereafter.
- **Lazy + sequenced correctly.** `GetCountriesAsync` is first invoked when
  `AgsiStorageWorkUnitProvider.GetWorkUnitsAsync` runs — which, per the §1.4 ordering, is
  **after** the entities pipeline has committed its MERGE. So the read sees the freshly
  refreshed list within the same run. (The cache means the whole storage run reads the list
  exactly once even across many parallel work units.)
- **Fail fast if empty.** If `usp_GetGasStorageEntities` returns **zero rows**, throw
  `InvalidOperationException` ("`arm.GasStorageEntity` is empty — run the About pipeline first,
  or check the `/api/about` load"). A missing country list is a hard error, not a silent
  no-op — identical posture to StormVista's `RequireNonEmpty`.

**Why decouple via the DB rather than pass a list in memory.** (a) It keeps the two pipelines
truly independent closed units (no shared mutable state, no ordering coupling beyond "run
About first"); (b) it lets a storage-only re-run reuse a previously-loaded catalogue without
re-hitting `/api/about`; (c) it matches the platform's "refresh the stored list, then read
the stored list" discovery pattern. The only coupling is the run **order** enforced in §1.4.

**Codes are lowercased for the URL/key; the fact persists `EntityId`, not a code.**
`arm.GasStorageEntity.Code` is stored as the API's 2-letter code (uppercase, e.g. `AT`, `DE`).
The storage provider lowercases it for the `country=` query param and the resume key (the API is
case-insensitive), but the fact row **no longer persists any code** — it carries the `EntityId`
the provider stamped onto the work unit from the same `(Id, Code)` row. The reader therefore
**never has to match the response's `data[].code` echo** against the entity `Code`, so the
UPPERCASE-echo-vs-entity-`Code` case mismatch that the old design reconciled is **eliminated by
design**; referential integrity is instead enforced by the `arm.GasStorage.EntityId →
arm.GasStorageEntity(Id)` FK (§7.2, §10).

**Optional aggregate rows (open item, §11).** GIE also serves region aggregates (`eu` total,
`ne` non-EU) via the `ParentCode` axis. Default scope is **distinct `Code` only** (the
per-country rows). Enumerating the aggregate(s) would mean also reading distinct `ParentCode`
(lowercased) — flagged as an open decision, off by default.

---

## 3. Work-unit definitions & two-zone resume keying

### 3.1 Work units

- **`AgsiEntitiesWorkUnit`** — a single undated unit per run. No payload beyond a run token.
  `Key = "agsi:entities:run={hot}"` (always hot — §3.3). `DisplayName = "AGSI entities /api/about"`.
- **`AgsiStorageWorkUnit`** — `EntityId` (int, stamped from the provider's `(Id, Code)` row —
  the value copied onto every fact row and half the merge key), `CountryCode` (lowercased),
  `Date` (`DateOnly`, the requested gas day), `Filename`/request descriptor for logging,
  `KeyValue` (precomputed, §3.3), `Key => KeyValue`. `DisplayName`, e.g.
  `"AGSI storage de 2026-08-13"`. One CSV-equivalent = one `(country, date)` request = one unit
  (agreed granularity).

### 3.2 Enumeration (`AgsiStorageWorkUnitProvider.GetWorkUnitsAsync`)

```
runDate   = CetToday(context.StartedAtUtc)                 // Europe/CET calendar date (§6), NOT UTC
countries = await countryProvider.GetCountriesAsync(ct)    // distinct (Id, Code); fail-fast if empty
newest    = runDate.AddDays(DateOffsetDays)                // DateOffsetDays = -1 (recommended, see below)
hot       = HotZoneKeyStrategy == RunDate ? runDate:yyyyMMdd(CET) : context.RunId:N

for k in 0 .. DaysBack-1:                                  // trailing window, go-forward only
    d       = newest.AddDays(-k)
    ageDays = runDate.DayNumber - d.DayNumber
    for (id, code) in countries:                           // code lowercased for the URL/key
        baseKey  = $"agsi:storage:{code}:{d:yyyyMMdd}"      // resume key still uses Code + request Date
        unit = {
            EntityId = id, CountryCode = code, Date = d,
            KeyValue = ageDays > SettledAfterDays ? baseKey            // SETTLED → stable
                                                  : $"{baseKey}:run={hot}"  // HOT → run-varying
        }
```

The entities provider simply returns `[ new AgsiEntitiesWorkUnit { KeyValue = $"agsi:entities:run={hot}" } ]`.

**`DateOffsetDays` (recommended `-1`, a design constant — NOT a config field this pass; open
item §11).** A gas day `D` is published the following morning (sample: run 2026-08-17 08:00
CET returned `gas_day=2026-08-16`, i.e. yesterday). Requesting **today's** CET gas day would
reliably return no-data. So the newest *requestable* gas day = `runDate − 1`. With `DaysBack=21`
this enumerates the dates `[runDate−21 … runDate−1]`. The reader tolerates a no-data response
anyway (§4), so `0` would merely add one guaranteed-empty request/country/run; `-1` avoids it.
Confirm with a reviewer (a real `x-key` would settle it precisely).

### 3.3 Two-zone resume key (StormVista §4 semantics, borrowed)

`DaysBack` sets the **enumeration** window; `SettledAfterDays` splits it into two zones by the
requested date's age (`ageDays = runDate.DayNumber − d.DayNumber`, both on the CET run date):

- **Settled zone (`ageDays > SettledAfterDays`) — STABLE key** `agsi:storage:{country}:{yyyyMMdd}`
  (no `:run=` suffix). Once `core.LoadLog` records success for that key, every later run's
  `BeginAsync` returns `null` → **cheap skip, no HTTP** (the Platts/CWG settled behavior).
- **Hot zone (`ageDays ≤ SettledAfterDays`) — run-varying key**
  `agsi:storage:{country}:{yyyyMMdd}:run={hot}`. With `HotZoneKeyStrategy=RunDate` (default),
  `{hot}` = the CET `yyyyMMdd`, so the hot window is **re-pulled once per calendar day** and
  skipped on a second same-day run; `RunId` re-pulls every invocation. The re-pull upserts
  idempotently via the natural-key MERGE (§10), catching late/revised recent data without
  duplication.
- **Entities pipeline** is an undated single unit → **always hot**
  (`agsi:entities:run={hot}`), so `/api/about` is refreshed each run (RunDate cadence by
  default) — the discovery-refresh the platform principle asks for.

**Intended posture: effectively all-hot with the requested defaults.** The requested defaults
are `DaysBack = 21` and `SettledAfterDays = 21`. Because the storage window ends at
`runDate − 1`, every enumerated date has `ageDays ∈ [1 … 21] ≤ SettledAfterDays`, so **the
whole 21-day window is hot and re-pulled each run**, and the idempotent MERGE keeps re-runs
safe. This is the deliberate incremental posture for a small, cheap-to-refresh European daily
feed: no settled zone, no permanent-skip, just a rolling re-pull of the last three weeks. (If
an operator later wants a cheap settled tail — e.g. to stop re-hitting month-old dates — set
`SettledAfterDays < DaysBack`, exactly as CWG does with `21/7`; the mechanism is already there,
unused at the default.)

The `arm.FileLog` hub row is keyed independently of the run token (§5), so a daily hot re-pull
**upserts one stable hub row** (refreshing `LastCheckedUtc`/`RowCount`); the audit never
fragments across runs.

---

## 4. Source readers (custom `ISourceReader`, JSON, tolerant)

Both readers implement `ISourceReader<TUnit,TRow>.ReadAsync` **directly** — they do **NOT** use
`HttpJsonSourceReaderBase`, because that base calls `EnsureSuccessStatusCode()` and would throw
on the `404`/no-data cases the loader must tolerate. They reuse the registered rate-limited
`HttpClient` + Polly policy, deserialize JSON with `System.Text.Json` (invariant culture),
write the `arm.FileLog` outcome row themselves (they hold the HTTP status + row count), and
**never log the `x-key` value** (and never log a URL that could carry a secret). The `x-key`
is attached as a **request header** on endpoint 2 only.

**Tolerant numeric parse (both readers).** Every measure arrives as a JSON **string**
(`"121.1238"`, `"-537.3"`). Parse with `decimal.TryParse(InvariantCulture, NumberStyles.Float
| AllowLeadingSign)`; a blank / missing / unparseable numeric → **NULL for that column**, never
a row failure (the API doc marks every measure NULLable for `status:"E"`/`"N"`). Keep the sign
(`netWithdrawal`, `trend` are signed). Dates parse `yyyy-MM-dd`; `updatedAt` parses
`yyyy-MM-dd HH:mm:ss` → `DATETIME2(0)` (stored as-received GIE server time, CET/CEST — §6/§11).

### 4.1 `AgsiEntitiesSourceReader` (endpoint 1 — `GET /api/about`, no key)

1. `GET {BaseUrl}/api/about` — **no `x-key` header**. Log only the sanitized path `/api/about`.
2. `httpStatus = (int)response.StatusCode`.
   - non-success (any non-2xx) → best-effort `FileLog "Failed"` then **throw** (this is the
     public discovery call; a failure here should be loud, and it will cascade into the storage
     provider's fail-fast when the country list is empty).
   - **200** → parse the JSON.
3. **Walk the object tree, do not trust the map keys.** Deserialize the root with
   `JsonDocument`/`JsonNode`. Navigate `root["SSO"]` → enumerate its properties (region-name
   keys) → for each, enumerate its properties (country-name keys) → each value is an **array of
   entity objects**. For each entity, read `entity["data"]`:
   `Code ← data.country.code`, `Name ← data.country.name`, `ParentCode ← data.code`,
   `ParentName ← data.name`. **Skip defensively** any entity whose `data.country` (or
   `country.code`) is null/empty (all observed entities have it, but do not insert a blank key).
4. **Dedup to one tuple per `Code`** (many SSO entities collapse to one country — Austria has 6,
   all `("AT","Austria","EU","Europe")`). Emit **distinct** `GasStorageEntityRow`s. (The sink
   also de-dups the batch on `Code` before the TVP, belt-and-suspenders.)
5. `FileLog` outcome: `rows.Count == 0 ? "NotAvailable" : "Success"`; upsert
   `AgsiFileContext(Endpoint="About", Region=null, RepresentativeDate=null)`; stamp the returned
   `FileLogId` onto every row.

### 4.2 `AgsiStorageSourceReader` (endpoint 2 — `GET /api?country=&date=`, `x-key` header)

1. Build the URI `{BaseUrl}/api?country={code}&date={yyyy-MM-dd}`; attach header
   `x-key: {ApiKey}`. Log only the sanitized path + params `country`/`date` — **never the
   header value**.
2. `GET`. `httpStatus = (int)response.StatusCode`.
   - **404** → `FileLog "NotAvailable" (404)`, return empty — **no unit failure** (unknown
     country / no data for date).
   - **401/403** → **throw** (bad/absent `x-key` — loud; almost certainly a config error).
   - **429 / 5xx** after Polly retries exhausted → throw (unit fails, run continues).
   - **200** → deserialize `AgsiStorageEnvelope`.
3. **No-data tolerance (three shapes, all → NotAvailable, no failure — API doc Open question #1).**
   Treat **any** of these as "nothing to load for this `(country,date)`": `total == 0`;
   `data[]` empty/absent; a single `data[]` element with `status == "N"` and blank measures.
   Emit **zero rows** (do not write an `N` row this pass — flagged for a live-key decision, §11).
4. **200 with data** → for each `data[]` element (single-date returns exactly one;
   `last_page:1,total:1`), build a `GasStorageRow`:
   `EntityId` ← the work unit's `EntityId` (copied straight through — the reader does **not**
   read or match the response's `data[].code` echo); `Date` ← the request date param;
   `Gas_Day` ← top-level `gas_day`; the **18** persisted `data[]` fields (every `data[]` field
   except `name`, `code`, `url`, and `info` — §7.2). Numerics via the tolerant parser above.
5. **Multi-page tolerance (API doc Open question #2).** Single-date mode is confirmed 1 page /
   1 row. If `last_page > 1` ever appears, **log a warning** and process the returned page's
   `data[]` only — this loader does **not** paginate (it iterates one date at a time). Do not
   fail on it.
6. `FileLog` outcome: `rows.Count == 0 ? "NotAvailable" : "Success"`; upsert
   `AgsiFileContext(Endpoint="Storage", Region=countryCode, RepresentativeDate=requestDate)`;
   stamp `FileLogId` onto every row.
7. `catch (OperationCanceledException) → throw;` (no FileLog write on a spent token — `LoadLog`
   already records cancellation). `catch (Exception)` → best-effort `FileLog "Failed"` (with
   `CancellationToken.None`), then **rethrow** so `LoaderPipelineBase` records the `LoadLog`
   failure and the run continues to the next unit (fail-a-block-not-the-run).

---

## 5. FileLog flow (`arm.FileLog` hub)

Reuse the CWG/StormVista `arm.FileLog` hub posture: **one row per request outcome**
(`Success` / `NotAvailable` / `Failed`) for every request, with `FileLogId` stamped onto the
fact rows it produced. Natural key:

```
UNIQUE (Endpoint, Region, RepresentativeDate)     -- SQL NULL-equality collapses the undated/no-region rows
```

| Endpoint | Region | RepresentativeDate |
|----------|--------|--------------------|
| `About`   | NULL (no country axis) | NULL (undated) → **one stable hub row**, upserted each run |
| `Storage` | the requested country code | the requested date (`Date` ≡ `gasDayStart`) |

- `AgsiFileContext(string Endpoint, string? Region, DateOnly? RepresentativeDate, string RequestPath)`
  — the CWG `CwgFileContext` shape minus the `Variant` slot (AGSI has no sub-variant). (If
  DATABASE_DEVELOPER prefers to keep the hub template byte-identical to CWG's, a nullable,
  always-NULL `Variant` column is harmless — their call, §11.)
- `IAgsiFileLog.UpsertAsync(file, status, httpStatus, requestPath, rowCount, ct) → FileLogId`.
  `SqlAgsiFileLog` calls `arm.usp_UpsertFileLog` under
  `SqlWriteGate.AcquireAsync(KeyFor(ConnectionString, "arm.usp_UpsertFileLog"))` (one shared key
  across both endpoints; a fast single-row upsert, so serializing it is cheap and deadlock-proof),
  passing explicit-typed params and reading the returned scalar `FileLogId` — the exact
  `SqlCwgFileLog` shape.
- Following CWG's resolved item 7, `Endpoint` / `Region` / `Status` may be normalized to seeded
  surrogate-Id lookups (`arm.Endpoint {About, Storage}`, `arm.Status {Success, NotAvailable,
  Failed}`, `arm.Region` get-or-create for country codes); `usp_UpsertFileLog` keeps its
  name-string signature and resolves names → Ids server-side. Inline `VARCHAR + CHECK` is an
  acceptable build-only alternative — DATABASE_DEVELOPER's call (§11).
- **No-data (empty `data[]` / `404` / `status:"N"`) → `NotAvailable`, no unit failure** — the
  fact tables simply gain no rows for that `(country, date)`.

---

## 6. Timezone basis — CET/Europe

AGSI is **European gas-day** data and GIE publishes on the CET/CEST calendar (`updatedAt` is
GIE server time, CET/CEST per the API doc). The "newest gas day" enumeration (§3.2) therefore
uses the **Central European** calendar date, not UTC and not US Eastern:

- `runDate = CetToday(context.StartedAtUtc)` — convert the run's UTC start to CET/CEST via
  `TimeZoneInfo` (Windows id `"Central European Standard Time"`, IANA `"Europe/Brussels"`; the
  loader should look up whichever the host OS exposes, mirroring how CWG resolves US Eastern),
  then take `.Date`.
- `ageDays` and the `{hot}` RunDate token are computed on this CET date, so a run that straddles
  UTC midnight does not target a not-yet-existent future gas day, and the daily hot re-pull
  key flips over on the same calendar boundary the vendor publishes on.

**Contrast (documented on purpose):** CWG uses **US Eastern** (CWG publishes on the Eastern
day), StormVista uses **UTC** (same-day global publication). AGSI uses **CET/Europe** for the
same reason each of them picked theirs — align the loader's day boundary with the source's.

`updatedAt` values are stored **as received** (CET/CEST wall-clock, `DATETIME2(0)`) this pass;
whether to normalize to UTC is a flagged open item (§11 / API doc Open question #5) pending a
live-key confirmation of the exact offset semantics.

---

## 7. Full-field mapping

Types and nullability are the `docs/apis/AGSI.md` recommendations — see §11 for the sizing
confirmations to DATABASE_DEVELOPER. Every fact row also carries `FileLogId` (provenance;
stamped in §5, UPDATEd on MERGE, **not** a merge-key column) and a table-default
`ModifiedAtUtc`. "Key" marks the MERGE natural key.

### 7.1 Endpoint 1 → `arm.GasStorageEntity` (merge/natural key `Code`)

| Source path | Row prop | Column | Type | Null? | Notes |
|-------------|----------|--------|------|-------|-------|
| `data.country.code` | Code | Code | VARCHAR(4) | No | **Key**; e.g. `AT` — the endpoint-2 query code |
| `data.country.name` | Name | Name | NVARCHAR(64) | No | e.g. `Austria` |
| `data.code` | ParentCode | ParentCode | VARCHAR(4) | No | region-group code; only `EU` observed |
| `data.name` | ParentName | ParentName | NVARCHAR(64) | No | region-group name; only `Europe` observed |

Dedup to one tuple per `Code` (§4.1). **Fallback composite key `(Code, ParentCode)`** only if a
duplicate-`Code` MERGE collision ever surfaces (would require GIE to introduce a second
`ParentCode` — not in the observed data; flag for DATABASE_DEVELOPER, §11).

### 7.2 Endpoint 2 → `arm.GasStorage` (PK / merge key `(EntityId, GasDayStart)`) — 21 persisted business columns (22 TVP columns incl. `FileLogId`)

`EntityId` (stamped from the work unit, FK → `arm.GasStorageEntity(Id)`) + `Date` (request
param) + `Gas_Day` (top-level `gas_day`) + the 18 persisted `data[]` fields (all `data[]` fields
except `name`, `code`, `url`, and `info`). The dropped `name`/`code` are per-country constants
already carried by `arm.GasStorageEntity`; `url` is dropped **entirely** (redundant with `Code`,
not sourced from `/api/about`, NOT added to the entity table). **PK = `(EntityId, GasDayStart)`**;
`Date` and `Gas_Day` are retained as ordinary **non-key** columns.

| # | Source | Row prop | Column | Type | Null? | Notes |
|---|--------|----------|--------|------|-------|-------|
| — | work unit `EntityId` | EntityId | EntityId | INT | No | **Key**; FK → `arm.GasStorageEntity(Id)`; stamped from the provider; replaces the dropped `name`/`code`/`url` |
| — | request `date` param | Date | Date | DATE | No | retained **non-key**; ≡ `gasDayStart` in single-date mode (the load-bearing invariant, §10) |
| — | top-level `gas_day` | GasDay | Gas_Day | DATE | No | retained **non-key**; "latest available gas day" marker (constant across backfilled dates) |
| 4 | `updatedAt` | UpdatedAt | updatedAt | DATETIME2(0) | Yes | `yyyy-MM-dd HH:mm:ss`, GIE CET/CEST (§6/§11) |
| 5 | `gasDayStart` | GasDayStart | gasDayStart | DATE | No | **Key**; = `Date` in single-date mode |
| 6 | `gasDayEnd` | GasDayEnd | gasDayEnd | DATE | No | = `gasDayStart + 1` |
| 7 | `gasInStorage` | GasInStorage | gasInStorage | DECIMAL(18,4) | Yes | TWh |
| 8 | `consumption` | Consumption | consumption | DECIMAL(18,4) | Yes | GWh/d |
| 9 | `consumptionFull` | ConsumptionFull | consumptionFull | DECIMAL(9,4) | Yes | % ⚠ (def. unconfirmed) |
| 10 | `injection` | Injection | injection | DECIMAL(18,4) | Yes | GWh/d |
| 11 | `withdrawal` | Withdrawal | withdrawal | DECIMAL(18,4) | Yes | GWh/d |
| 12 | `netWithdrawal` | NetWithdrawal | netWithdrawal | DECIMAL(18,4) | Yes | **signed** (`withdrawal − injection`) |
| 13 | `workingGasVolume` | WorkingGasVolume | workingGasVolume | DECIMAL(18,4) | Yes | TWh |
| 14 | `injectionCapacity` | InjectionCapacity | injectionCapacity | DECIMAL(18,4) | Yes | GWh/d |
| 15 | `withdrawalCapacity` | WithdrawalCapacity | withdrawalCapacity | DECIMAL(18,4) | Yes | GWh/d |
| 16 | `contractedCapacity` | ContractedCapacity | contractedCapacity | DECIMAL(18,4) | Yes | TWh |
| 17 | `availableCapacity` | AvailableCapacity | availableCapacity | DECIMAL(18,4) | Yes | TWh |
| 18 | `coveredCapacity` | CoveredCapacity | coveredCapacity | DECIMAL(9,4) | Yes | % ⚠ (def. unconfirmed) |
| 19 | `status` | Status | status | VARCHAR(1) | No | `C`=Confirmed, `E`=Estimated, `N`=No data |
| 20 | `trend` | Trend | trend | DECIMAL(9,4) | Yes | **signed** ratio/% |
| 21 | `full` | Full | full | DECIMAL(9,4) | Yes | fill %; can slightly exceed 100 — SQL column `[Full]` (reserved-ish) is DATABASE_DEVELOPER's call |
| — | `name` | — | — | — | — | **dropped** from fact — per-country constant in `arm.GasStorageEntity.Name` |
| — | `code` | — | — | — | — | **dropped** from fact — per-country constant in `arm.GasStorageEntity.Code`; `EntityId` replaces it |
| — | `url` | — | — | — | — | **dropped entirely** — redundant with `Code`, not sourced from `/api/about`, NOT added to the entity table |
| — | `info` | — | — | — | — | **dropped** (array of announcements; empty in sample) |
| — | (envelope) `last_page`,`total`,`dataset` | — | — | — | — | **dropped** (pagination/label) |

**Finalized TVP / column order — the single contract for DATABASE_DEVELOPER and CODER**
(`FileLogId` first, then `EntityId`, then the retained/attribute columns, then the 18 measures):

```
FileLogId, EntityId, Date, Gas_Day, UpdatedAt, GasDayStart, GasDayEnd, GasInStorage,
Consumption, ConsumptionFull, Injection, Withdrawal, NetWithdrawal, WorkingGasVolume,
InjectionCapacity, WithdrawalCapacity, ContractedCapacity, AvailableCapacity,
CoveredCapacity, Status, Trend, Full
```

This exact **22-column** order MUST be mirrored **identically** across: the `001` table body,
the `002` TVP type, the `003` merge proc `SELECT`/`INSERT` lists, and the C# sink's `BuildTable`
(plus its unit test). `FileLogId` is UPDATEd-on-match provenance, **not** a merge-key column; the
merge keys on `(EntityId, GasDayStart)` only (§10).

Both sinks derive from `SqlSinkBase<TRow>`, set `StoredProcedureName` /
`TableValuedParameterType` / `GetConnectionString`, and **de-dup the batch on the merge key**
(`Code` for the entity table, `(EntityId, GasDayStart)` for storage) before building the TVP
(Platts/CWG posture). `FileLogId` is the **first** TVP column on both tables; the sink's
`BuildTable` column order must match the TVP exactly (the load-bearing `SqlSinkBase` contract).

---

## 8. Post-load validation (module-level hook, coded now / not exercised live)

`LoaderPipelineBase` has no post-load hook and `AgsiModule` owns orchestration, so — following
the StormVista `StormVistaLoadValidator` precedent — an **`AgsiLoadValidator`** runs as a
module-level step in `RunAsync` **after both pipelines complete**, scoped to the run's date
window. It is **observational** (logs warnings + counters; fails the run only on a hard
invariant breach) so a legitimately sparse day (many `NotAvailable`s) does not fail an
otherwise-good load. Recommended checks (a `arm.usp_ValidateLoad` proc or a handful of SQL
counts — DATABASE_DEVELOPER):

- `arm.GasStorageEntity` non-empty and every `Code` non-blank; `ParentCode` present.
- For each requested country, storage-row coverage over the window is within a sane bound
  (allowing legitimate no-data days). The old orphan-`code` cross-check (a `code` in
  `arm.GasStorage` absent from `arm.GasStorageEntity`) is now **superseded by the
  `arm.GasStorage.EntityId → arm.GasStorageEntity(Id)` FK** — orphans are impossible by
  construction, so this check is dropped/relaxed (DATABASE_DEVELOPER, §11 item 11).
- `status` domain ⊆ `{C,E,N}`; `full`/`trend` within sane ranges; measure NULL-rate bounded;
  `Gas_Day` consistent with the run's newest date; `gasDayEnd = gasDayStart + 1`.

**Build-only:** this validator is written and unit-testable but **not exercised against live
data** this pass; the deeper reconciliation is the deferred `DATA_QUALITY_VALIDATOR` step.

---

## 9. Config surface — `AgsiSettings : LoaderSettingsBase`

Inherited: `ConnectionString` (the `AGSI` DB), `MaxConcurrentWorkUnits`, `RetryCount`,
`RetryDelayMs`, `WorkUnitTimeoutSeconds`. Added:

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `BaseUrl` | string | `https://agsi.gie.eu` | API host (paths `/api/about`, `/api`) |
| `ApiKey` | string | `SEE_DB` | resolved from `core.Param` by `AddLoaderSettings`; sent as header `x-key` to endpoint 2 only; **never logged** |
| `HttpTimeoutSeconds` | int | 60 | per-request timeout on the shared client |
| `EnabledEndpoints` | string[] | `["About","Storage"]` | endpoint toggle (matched case-insensitively to `IAgsiPipeline.EndpointId`) |
| `DaysBack` | int | 21 | storage trailing-window length (About ignores it) |
| `SettledAfterDays` | int | 21 | hot/settled boundary (§3.3); `= DaysBack` ⇒ all-hot (intended) |
| `HotZoneKeyStrategy` | `RunDate`\|`RunId` | `RunDate` | hot-zone + entities re-pull cadence |
| `RequestsPerSecond` | double? | conservative (e.g. `1`–`2`) | global client-side throttle; null/≤0 = unlimited. GIE publishes no hard number — pace gently, back off on `429` (§11) |

`appsettings.json` `Loaders:AGSI` mirrors the CWG/StormVista block: `ConnectionString`
(`Server=…;Database=AGSI;Integrated Security=SSPI;TrustServerCertificate=True;`),
`ApiKey:"SEE_DB"`, the tuning knobs above, `EnabledEndpoints`, `DaysBack:21`,
`SettledAfterDays:21`, `HotZoneKeyStrategy:"RunDate"`, `RequestsPerSecond`. **Leave `"AGSI"`
OUT of `Platform:EnabledLoaders`** (build-only pass — loader disabled by default). The real key
lives in `core.Param(LoaderName='AGSI', ParamName='ApiKey')` (or env
`DATALOADER_Loaders__AGSI__ApiKey`), never in the file. **Fail fast** at run start if `"Storage"`
is enabled and `ApiKey` is still `"SEE_DB"` (§1.4). **Note (review Finding 1):** because the
`SEE_DB` resolver runs when `IOptions<AgsiSettings>` is first materialized and throws on a missing
`core.Param` row, **any** AGSI run — including `"About"`-only — needs an `ApiKey` `core.Param` row
to exist; a **placeholder value suffices for an About-only run** (About sends no key over the
wire). The §1.4 fail-fast is a secondary guard for a still-`SEE_DB` placeholder on a Storage run.

`DateOffsetDays` (`-1`, §3.2) is intentionally **not** a config field this pass — it is a design
constant pending a live-key confirmation; promote it to config only if that confirmation
requires it (§11).

---

## 10. Concurrency & idempotency

- **Sinks** derive from `SqlSinkBase<TRow>`, which auto-acquires `SqlWriteGate` keyed
  `{server}/{db}::{proc}`. The two fact procs are two distinct keys, so the entity and storage
  merges never serialize against each other; two concurrent storage units serialize on the one
  storage proc key (correct — prevents the parallel-MERGE deadlock / insert-race). With the two
  pipelines run sequentially (§1.4), only within-storage units actually contend.
- **FileLog** is a **direct** proc writer, so `SqlAgsiFileLog` acquires `SqlWriteGate` explicitly
  on `arm.usp_UpsertFileLog` (one shared key across both endpoints; a fast single-row upsert) —
  the Platts/StormVista/CWG posture. `core.LoadLog` is intentionally not gated (keyed per work
  unit).
- **Idempotent MERGE.** `arm.usp_BulkMergeGasStorageEntity` MERGEs on `Code`;
  `arm.usp_BulkMergeGasStorage` batch-dedups `PARTITION BY (EntityId, GasDayStart)` and then
  `MERGE ... ON (EntityId, GasDayStart)` — **never** on `FileLogId` (provenance, UPDATEd on
  match) and **never** on `Date`/`Gas_Day` (retained non-key attributes; `Gas_Day` is a constant
  marker that would collapse all backfilled dates onto one key). The `arm.GasStorage.EntityId →
  arm.GasStorageEntity(Id)` FK now enforces entity referential integrity (the reader stamps
  `EntityId` from the resolved country, so orphan rows are impossible). So the daily hot re-pull
  of the 21-day window and the once-per-run entities refresh both upsert in place — no
  duplicates, safe over-scheduling. The platform overlap guard (`DataLoader:AGSI` app-lock)
  prevents two host processes running the loader at once.
- **Load-bearing single-date invariant.** The resume / work-unit key still uses the request
  `Date` (`agsi:storage:{code}:{yyyyMMdd}`, §3.2) while the MERGE now keys on `GasDayStart`.
  Idempotency of a re-pull therefore holds **only while `Date == gasDayStart`** (the documented
  single-date invariant — confirmed in the sample, §11 item 2). If a future response ever
  returned `gasDayStart != Date`, the resume key and the merge key would address different rows
  and a re-pull could double-write; the validator (§8) and DATABASE_DEVELOPER item 2 track this.

---

## 11. Open items for DATABASE_DEVELOPER / reviewer

Two tables + one FileLog hub + supporting objects. Coordinate on:

1. **`arm.GasStorageEntity`** — 4 persisted columns (§7.1) + a surrogate **`Id INT` (identity)** +
   `FileLogId` (FK, provenance) + `ModifiedAtUtc` default. **`Id` is now the FK target of
   `arm.GasStorage.EntityId`** and the first column the read proc returns. Confirm whether `Id` is
   a fresh identity PK with a `UNIQUE(Code)` alternate key (recommended) or `Code` stays PK with
   `Id` a `UNIQUE` identity — either satisfies the FK; `Name`/`ParentName` `NVARCHAR(64)`,
   `ParentCode` VARCHAR(4). **The read proc is renamed `usp_GetGasStorageEntityCodes` →
   `usp_GetGasStorageEntities`** (guarded drop of the old name) and now returns **`(Id, Code)`**
   pairs ordered by `Code`. **Fallback `(Code, ParentCode)`** for the `Code` uniqueness only if a
   duplicate-`Code` collision ever appears (not in the observed data).
2. **`arm.GasStorage`** — 21 persisted business columns (§7.2) + `FileLogId` (22-col TVP) +
   `ModifiedAtUtc`. **PK / merge key `(EntityId, GasDayStart)`** (LOCKED — was `(Code, Date)`);
   `EntityId INT NOT NULL` FK → `arm.GasStorageEntity(Id)`; `name`/`code`/`url` dropped;
   `Date`/`Gas_Day` retained as non-key columns; batch dedup `PARTITION BY (EntityId,
   GasDayStart)`, `MERGE ... ON (EntityId, GasDayStart)` (§10). **Load-bearing invariant (now
   promoted):** the resume key still uses request `Date` while the MERGE keys on `GasDayStart`, so
   idempotency holds **only while `Date == gasDayStart`** (holds in the sample). If a future
   response ever returns `gasDayStart != Date`, the resume-key/merge-key split could double-write —
   surface it in the validator and revisit the resume-key basis.
3. **DECIMAL sizing (from the API doc):** volumes/rates/capacities → `DECIMAL(18,4)` (the `eu`
   aggregate carries the largest magnitudes; `DECIMAL(12,4)` is the practical minimum);
   percents/ratios (`consumptionFull`, `coveredCapacity`, `trend`, `full`) → `DECIMAL(9,4)`;
   `updatedAt` → `DATETIME2(0)`; `status` → `VARCHAR(1)`; `Code`/`ParentCode` → short `VARCHAR`;
   `Name`/`ParentName` → `NVARCHAR`. **All measure columns NULLable**; in `arm.GasStorage` only
   `EntityId`, `Date`, `Gas_Day`, `gasDayStart`, `gasDayEnd`, `status` are NOT NULL (the dropped
   `name`/`code`/`url` no longer figure). Confirm the `consumptionFull` / `coveredCapacity`
   precision (⚠ modeled as `%`; adjust if a live key shows counts/days). Confirm `[Full]` as a
   bracketed column name.
4. **Column order / TVP contract** — for `arm.GasStorage` the order is now **fixed and published**
   in §7.2 (the 22-column list, `FileLogId` first, `EntityId` second). Mirror it **identically**
   across `001` table body, `002` TVP, `003` merge proc `SELECT`/`INSERT`, and the C# sink
   `BuildTable` + its test. The entity table keeps `FileLogId` first likewise; confirm the exact
   `UPDATE` list ordering you want in the merge procs.
5. **`arm.FileLog` hub + `arm.usp_UpsertFileLog`** — natural key `(Endpoint, Region,
   RepresentativeDate)` with SQL NULL-equality (§5). Confirm: (a) normalize `Endpoint`/`Region`/
   `Status` to seeded lookups (`arm.Endpoint {About, Storage}`, `arm.Status {Success,
   NotAvailable, Failed}`, `arm.Region` get-or-create for country codes) per CWG's resolved item
   7 — recommended — vs inline `VARCHAR + CHECK` for the build-only pass; (b) whether to keep an
   always-NULL `Variant` column so the hub template is byte-identical to CWG's.
6. **`DateOffsetDays = -1`** (§3.2) — confirm that today's CET gas day is not requestable (a real
   `x-key` settles it); decide whether to promote it to a config field.
7. **No-data row policy (API doc Open question #1, needs a key)** — confirm whether a valid
   country with no data returns `404`, `200`+empty `data[]`, or a `status:"N"` row. Loader
   tolerates all three as `NotAvailable` and writes **no** fact row; confirm we should NOT
   persist an `N` row (vs. storing it with NULL measures).
8. **`updatedAt` timezone (API doc Open question #5)** — stored as-received (CET/CEST) this pass;
   confirm store-as-is vs normalize-to-UTC once the offset is verified.
9. **Rate limit (API doc Open question #4)** — set a conservative `RequestsPerSecond` default and
   confirm `429` back-off / `Retry-After` handling in the Polly policy.
10. **Optional aggregate rows** (§2) — confirm whether to also enumerate the `eu`/`ne`
    `ParentCode` aggregates (off by default; would read distinct `ParentCode` too).
11. **`arm.usp_ValidateLoad`** (§8) — the anomaly counts for the run window (coded now, run
    later). **The orphan-country cross-check it used to run is now superseded by the
    `arm.GasStorage.EntityId → arm.GasStorageEntity(Id)` FK** (orphans impossible by
    construction) — adjust or drop that check accordingly. Also fold in the now load-bearing
    `Date == gasDayStart` invariant (§10, item 2) as an explicit assertion.

---

## Coverage checklist

| Endpoint | Pipeline | Work-unit / key rule | Reader tolerance | Target table (key) | FileLog identity |
|----------|----------|----------------------|------------------|--------------------|------------------|
| 1 `GET /api/about` (no key) | About (runs 1st) | single undated unit, always hot | non-2xx → throw; tree-walk + dedup→`Code`; skip null `country` | `arm.GasStorageEntity` (`Code`) | `About` / NULL / NULL — one stable hub row |
| 2 `GET /api?country=&date=` (`x-key` header) | Storage (runs 2nd) | `(EntityId, country, date)`; two-zone key (all-hot at defaults) | 404 / empty `data[]` / `status:"N"` → NotAvailable, no fail; 401/403 → throw; tolerant numeric parse | `arm.GasStorage` (`EntityId, GasDayStart`) | `Storage` / country / request date |

Both endpoints have a complete persisted field set (4 / 21), a per-field SQL type +
nullability, a natural key, a resume-key rule, a FileLog identity, and a tolerance policy for
every ⚠ item the API doc could not close without a live key. Ready for DATABASE_DEVELOPER (two
tables + TVPs + merge procs + `arm.FileLog`/`arm.usp_UpsertFileLog` [+ optional lookups] +
optional `arm.usp_ValidateLoad`) and CODER (module + two closed pipelines + two providers +
`IAgsiCountryProvider` + two readers + JSON models + two rows + two sinks + FileLog writer +
throttle + validator; loader left **disabled** in `Platform:EnabledLoaders`).
