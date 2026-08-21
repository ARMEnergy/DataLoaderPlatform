# IIR (Industrial Info Resources — IDB API v2.7) loader — design & processing flow

Design/flow spec for the **IIR** loader. Input of record is the field reference at
`docs/apis/IIR.md` — now documenting a **MANDATORY two-step `summary` → `detail` pull** for all
three targets (Plant, Unit, OfflineEvent), the `physicalAddressCountryName` discovery filter, the
per-field column mappings (the DDL is a **detail-level** record), the `⚠`
detail-schema/casing gaps, and the **load-bearing summary-only lat/long finding** (§8.0). This
document is the CODER + DATABASE_DEVELOPER hand-off; it contains **no code and no SQL**. SQL
objects named here are *proposed* to DATABASE_DEVELOPER (open items in §15); C# structures are
*proposed* to CODER. Every column in `docs/apis/IIR.md` §5/§6/§7 must map end-to-end (detail model
→ sink → TVP → table → merge proc) — no partial column set may ship.

Locked decisions this design is built around:

1. **Build-only / disabled posture** (CWG/AGSI/IHSPointLogic style): everything is coded,
   unit-tested and buildable, but the loader is **left OUT of `Platform:EnabledLoaders`** and no
   live DB deploy / data load / `DATA_QUALITY_VALIDATOR` runs this pass. DB `IIR`, schema `arm`,
   Integrated Security in-file.
2. **Three independent, descriptor-registered pipelines — each internally TWO-STEP; NO discovery
   tiers.** Plant → `arm.Plant`, Unit → `arm.Unit`, OfflineEvent → `arm.OfflineEvent`, built
   explicitly with a CWG-style `Add(descriptor, rowFactory, sinkFactory)` registrar and toggled by
   `EnabledEndpoints[]`. Every pipeline runs a **mandatory** `summary` (id discovery, country
   filter) → `detail` (≤50-id batches) pull **inside its single work unit** (§4); the **detail
   response populates the fact table**. **The IHSPointLogic 3-tier discovery graph,
   reference-provider machinery (`IPl*Provider`), and cross-pipeline tier barriers are explicitly
   NOT used** — the two steps are *intra-pipeline* (self-contained within one endpoint's reader),
   not a discovery tier that feeds another pipeline. The three pipelines remain independent and
   unordered.
3. **Per-run id-catalog census (new).** STEP 1 also persists an **ID-ONLY** `(RunDate, id)`
   census to a per-endpoint id-catalog table (`arm.PlantSummary` / `arm.UnitSummary` /
   `arm.OfflineEventSummary`), written by the reader as a **side-write** through an injected summary
   sink (§4.4). This satisfies the platform "refresh the stored list before processing" principle.
   The summary-only lat/long is **not** persisted on the census — it is captured in memory and
   carried forward into the *fact* row instead (§6.3).
4. **Secrets via `SEE_DB`.** `Username` and `Password` default to the `SEE_DB` sentinel and are
   resolved at run time from `core.Param` by `AddLoaderSettings<IirSettings>` (or env-var
   overrides); the connection string uses Integrated Security and stays in the file. Credentials
   and the minted token are **never logged**.
5. **Geography is built in the MERGE proc, never in C#.** The fact TVP carries `Latitude FLOAT` /
   `Longitude FLOAT` (and NO geography column); the merge proc computes
   `PlantPoint = geography::Point(Latitude, Longitude, 4326)` per source row when both are present
   and in range, else `NULL` (§7.3). No geography marshalling crosses the TVP.
6. **Overlap protection is automatic.** The platform's `SqlLoaderOverlapGuard`
   (`core.usp_TryAcquireLoaderLock`, app-lock `DataLoader:IIR`) already guarantees a second host
   invocation exits `0` while one is running. **No new lock code is added** by this loader.

> **Build-only posture (read first).** Same posture CWG/AGSI/IHSPointLogic shipped in. Because
> `docs/apis/IIR.md` is **PARTIALLY VERIFIED** (auth / transport / two-step / paging / envelope from
> official IIR docs; the three per-field **detail** schemas **reconstructed** and marked `⚠`), the
> design must **tolerate** every `⚠` unknown without a live probe — the token delivery channel, the
> data-array key casing, the exact **detail** field casing/nesting, the boolean-flag encoding, the
> summary id field names, the detail max-ids-per-call, and (load-bearing) **whether lat/long is a
> detail field or summary-only** (§13 checklist). Live deploy + first load + validation are
> deferred to a later pass when a real PAT is available.

Reference implementations mirrored: **`src/DataLoader.CWG/`** (descriptor registry driving N
closed per-endpoint pipelines built explicitly so the shared generics are never resolved from DI;
one shared tolerant source reader + per-endpoint row factory + per-endpoint sink; `arm.FileLog`
hub + `arm.usp_UpsertFileLog` under `SqlWriteGate`; `.RemoveAllLoggers()`; sanitized-path logging;
`CwgModule` fan-out; US-Eastern TZ resolution in `CwgWorkUnitProvider.ResolveEastern` — reused here
for **US Central**) and **`src/DataLoader.IHSPointLogic/`** (the auth **delegating handler** +
throttle handler + Polly policy wiring and handler ordering; the tolerant paging `ISourceReader`
that does NOT go through `HttpJsonSourceReaderBase`; the **`BatchedFact` ≤50-id `pointIds` batch**
pattern reused for STEP 2) and **`src/DataLoader.AGSI/`** (the `arm.FileLog` hub; the load-once
reference/side-write posture; the module-level `AgsiLoadValidator` calling `arm.usp_ValidateLoad`).

> **Discovery-first note (the two-step pull is intra-pipeline, not a discovery tier).** Unlike
> AGSI (`/api/about` → country list feeding a *second* pipeline) and IHSPointLogic (three cross-
> pipeline tiers), IIR's discovery is **inside each pipeline's single reader**: STEP 1 (`summary`)
> discovers the ids that STEP 2 (`detail`) of the *same* pipeline immediately enriches. No pipeline
> reads another pipeline's table; there is no reference provider, no tier barrier. The descriptor
> registry (§2) is the static endpoint list; enumeration is DB-free and unit tests enumerate with
> no live DB. The one dynamic "discovery" per pipeline is STEP 1 returning the id set + `totalCount`
> that drives STEP 1's own paging and STEP 2's batching (§4).

---

## 0. Class / structure inventory (shared vs per-endpoint)

| Concern | Type(s) | Shared or per-endpoint |
|--------|---------|------------------------|
| Settings | `IirSettings : LoaderSettingsBase` | shared (§10) |
| Enum | `IirHotKeyStrategy {RunDate, RunId}` (default `RunDate`) | shared (§5) |
| Descriptor | `IirEndpointDescriptor` (record), `IirDescriptors` (static registry of **3**) | shared (§2) |
| Work unit | `IirWorkUnit : WorkUnit` (ONE type for all 3 endpoints, CWG-style) | shared (§5) |
| Work-unit provider | `IirWorkUnitProvider : IWorkUnitProvider<IirWorkUnit>` (per descriptor; emits **one** unit/run) | shared class, per-descriptor instance |
| **Token auth** | `IIirTokenProvider` / `IirTokenProvider` (singleton — mint + cache + thread-safe re-mint, tolerant token extraction) + `IirTokenAuthHandler : DelegatingHandler` (stamps Bearer per attempt, re-mints once on 401) | shared (§3) |
| Throttle / retry | `IirRateLimiter` (singleton) + `IirRateLimitingHandler` (transient) + `IirHttpPolicy` (Polly) | shared (§3/§4) |
| **Two-step reader** | `IirSourceReader<TRow> : ISourceReader<IirWorkUnit, TRow>` (STEP 1 summary+paging → id-catalog side-write → STEP 2 detail batches → lat/long carry-forward → fact rows) | shared (§4) |
| Summary (id-catalog) row | `IirSummaryRow` (shared: `RunDate`, `EntityId`; plus in-memory-only `Latitude`/`Longitude` for the §6.3 carry-forward — NOT census columns) | shared (§4.4/§7.5) |
| Summary (id-catalog) sink | `SqlSinkBase<IirSummaryRow>` subclass **×3** (`IirPlantSummarySink`/`…Unit…`/`…OfflineEvent…`; per-endpoint proc + TVP + table) — the reader's injected side-write | **per-endpoint** |
| In-memory step-1 entry | `IirSummaryEntry` (readonly struct: `long Id`, `double? Latitude`, `double? Longitude`) | shared |
| Value/date parsers | `IirParse.*` (number / string-number / mixed-date `Z[UTC]`-strip / bit-from-0/1/bool/"Y"/"N" / blank→null / nested-object accessor / case-insensitive `Prop`) | shared (§6) |
| FileLog | `IirFileContext` (readonly struct), `IIirFileLog`, `SqlIirFileLog` (`arm.usp_UpsertFileLog`, `SqlWriteGate`, returns `FileLogId`) | shared (§8) |
| Fact row | `IIirFactRow { int FileLogId {get;set;}  long EntityId {get;}  double? Latitude {get;set;}  double? Longitude {get;set;} }` + **3 row types** (`IirPlantRow`, `IirUnitRow`, `IirOfflineEventRow`) | interface shared, 3 rows per-endpoint |
| Row factory | `Func<JsonElement, IirWorkUnit, TRow?>` (a `static TRow? From(elem, unit)` per row type — maps **detail** fields) | **per-endpoint** (the only mapping code) |
| Fact sink | `SqlSinkBase<TRow>` subclass **×3** (per-endpoint proc + TVP; lat/long FLOAT, no geography) | **per-endpoint** |
| Pipeline | `IIirPipeline : ILoaderPipeline { string EndpointId; }`, `IirPipeline<TRow> : LoaderPipelineBase<IirWorkUnit,TRow,TRow>` | shared |
| Module | `IirModule : ILoaderModule` (`LoaderId = "IIR"`) | shared (§1) |
| Post-load validation | `IirLoadValidator` (module-level hook) | coded now, not exercised live (§9) |

The **only** per-endpoint C# is: the `TRow` class + its `From(elem, unit)` row factory, the fact
`SqlSinkBase<TRow>` subclass, the summary `SqlSinkBase<IirSummaryRow>` subclass, and the descriptor
entry. Everything above the row factory (JWT mint/cache/re-mint, HTTP, retry, throttle, the two-step
summary→detail orchestration, offset/limit paging, id batching, id-catalog side-write, lat/long
carry-forward, tolerant envelope/parse, FileLog, 404/no-data tolerance, work-unit enumeration,
idempotency, the pipeline loop) is written once or inherited from Core.

---

## 1. Module topology & pipeline strategy

**One module (`IirModule`, `LoaderId = "IIR"`), 3 closed per-endpoint pipelines**, built
explicitly (CWG pattern), toggled by `EnabledEndpoints[]`, executed **sequentially** (no tiers, no
ordering dependency). Each pipeline is internally two-step (§4).

### 1.1 Why closed per-endpoint pipelines (never register the shared generics)

All 3 endpoints share the `IirWorkUnit` type and the `IirSourceReader<TRow>` reader. If we
registered a single `IWorkUnitProvider<IirWorkUnit>` (or `ISourceReader<IirWorkUnit,TRow>`) in DI,
every pipeline would resolve the *same* service and drive the wrong endpoint — exactly the
collision the CWG/AGSI/Platts headers warn about. We therefore **never register the generic
provider/reader in DI**. Each pipeline is assembled inside a module factory closure that `new`s a
descriptor-bound provider + reader + per-endpoint fact sink + per-endpoint summary sink and passes
them to `IirPipeline<TRow>`'s constructor; `LoaderPipelineBase` takes its provider/source/sink as
constructor arguments, so those generics are never resolved by the container. This is the exact
`CwgModule.BuildPipeline` shape.

### 1.2 The generic pipeline class (CWG-style, `LoaderPipelineBase` directly)

```
IIirPipeline : ILoaderPipeline { string EndpointId { get; } }

IirPipeline<TRow> : LoaderPipelineBase<IirWorkUnit, TRow, TRow>, IIirPipeline
    // ctor(endpointId, IWorkUnitProvider<IirWorkUnit>, ISourceReader<IirWorkUnit,TRow>,
    //      ISink<TRow>, ILoadLogRepository, IirSettings, ILogger)
    //   → base("IIR", provider, source, IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
```

This reuses the platform's vetted per-unit loop unchanged: `BeginAsync` idempotency skip →
`ReadAsync` → identity transform → `WriteAsync` → `CompleteSuccess/Failure`, bounded by
`ParallelRunner` at `MaxConcurrentWorkUnits`, per-unit timeout, fail-one-not-the-run. **No custom
windowed orchestrator** (unlike StormVista): each endpoint materializes exactly **one** work unit
per run (§5) that runs the whole two-step pull internally. The pipeline's single `ISink<TRow>` is
the **fact** sink (arm.Plant/Unit/OfflineEvent); the **id-catalog** is a reader side-write (§4.4),
so LoaderPipelineBase's single-ISink shape is preserved.

### 1.3 DI registration (`RegisterServices`)

1. `services.AddLoaderSettings<IirSettings>(configuration, Id);` (binds `Loaders:IIR`; resolves
   `Username`/`Password` = `"SEE_DB"` from `core.Param` lazily at run time).
2. Shared throttle: `IirRateLimiter` (singleton) + `IirRateLimitingHandler` (transient).
3. Token machinery: `IIirTokenProvider` → `IirTokenProvider` (**singleton** — the shared JWT cache
   across all pipelines and both steps, §3), and `IirTokenAuthHandler` (transient delegating
   handler that consults the provider).
4. A **dedicated, un-authed token `HttpClient`** named `"IIR.Token"` used ONLY by the token
   provider to call `POST …/token` (§3): `Accept: application/json`, `Timeout = HttpTimeoutSeconds`,
   `.RemoveAllLoggers()`. **`RemoveAllLoggers()` is load-bearing here** — the mint URL carries
   `username`/`password` in the **query string** (§3.1), so default `IHttpClientFactory` request
   logging would leak them (the CWG `?apikey=` rationale). This client has **no** auth handler.
5. The shared **data** `HttpClient` named `"IIR"` (used for both summary and detail calls):
   - `Timeout = HttpTimeoutSeconds`, `Accept: application/json`;
   - `.RemoveAllLoggers()` — parity + the Bearer header must never be logged;
   - `.AddPolicyHandler(...)` retry **outer** (Polly `IirHttpPolicy`; `RetryCount`/`RetryDelayMs`;
     retry `5xx`/`408`/network/`429`, honor `Retry-After`; **do NOT retry `401`** — the auth handler
     owns the 401 re-mint, §3.3 — nor `403`/`404`);
   - `.AddHttpMessageHandler<IirTokenAuthHandler>()` — auth **inner of retry, outer of throttle**;
   - `.AddHttpMessageHandler<IirRateLimitingHandler>()` throttle **innermost**.
   **Handler order (locked): retry (OUTER) → auth (re-stamps each attempt, re-mints on 401) →
   throttle (INNER).**
6. `services.AddSingleton<IIirFileLog, SqlIirFileLog>();`
7. `services.AddSingleton<IirLoadValidator>();`
8. Register the 3 pipelines with a factory helper, one line each — `Add(services, descriptor,
   rowFactory, factSinkFactory, summarySinkFactory)`; the helper builds
   `IirWorkUnitProvider(descriptor, settings, log)`, the two-step
   `IirSourceReader<TRow>(http("IIR"), settings, fileLog, descriptor, rowFactory,
   summarySink, log)` and the per-endpoint fact sink, wrapping them in
   `IirPipeline<TRow>(descriptor.EndpointId, …)`.

### 1.4 `RunAsync` fan-out (mirror `CwgModule.RunAsync`)

1. Read `IirSettings`. **Fail fast** (log an error — never the secret — return
   `LoaderRunResult.Failed`) if `Username` **or** `Password` is blank or still the `"SEE_DB"`
   placeholder. Every IIR run must mint a token, so both are required.
   **Implementation caveat (AGSI/IHS parity):** materializing `IOptions<IirSettings>` runs the
   shared `SeeDbSettingsResolver`, which **throws** if `core.Param(LoaderName='IIR',
   ParamName='Username'|'Password')` is missing — so any run requires both rows; this §1.4 check is
   the secondary guard catching a value left as the literal `SEE_DB`.
2. `enabled = HashSet(EnabledEndpoints, OrdinalIgnoreCase)`;
   `all = services.GetServices<IIirPipeline>()`;
   `pipelines = all.Where(p => enabled.Contains(p.EndpointId))`. Warn for any enabled id with no
   matching pipeline; if none enabled → warn, return `Success = true`.
3. **Run the enabled pipelines sequentially in registration order** (Plant → Unit →
   OfflineEvent). The order is **cosmetic, not load-bearing** — the three pulls are independent (no
   pipeline reads another's table). Each pipeline's single work unit runs the full two-step pull
   internally; the shared `IirTokenProvider` mints the JWT lazily on the first call and every
   subsequent call (across pipelines and both steps) reuses the cached token; the one shared
   rate-limited client bounds global RPS.
4. After all three complete, run the **module-level post-load validation** (§9), scoped to the
   run's Central date (observational).
5. Aggregate the per-pipeline `LoaderRunResult`s (sum totals; `Success = all succeeded`) exactly as
   CWG/AGSI do.

---

## 2. The endpoint descriptor schema

```
record IirEndpointDescriptor(
    string EndpointId,        // stable id: "Plant" | "Unit" | "OfflineEvent"
    string DisplayName,
    string Product,           // path segment: "plants" | "units" | "offlineevents"
    string SummaryPath,       // relative, e.g. "idb/{version}/plants/summary"  (STEP 1)
    string DetailPath,        // relative, e.g. "idb/{version}/plants/detail"   (STEP 2 — ALWAYS used)
    string DataArrayKey,      // EXPECTED envelope array key ("plants"/"units"/"offlineEvents") — read TOLERANTLY (⚠ casing, §4.5)
    string IdField,           // "plantId"/"unitId"/"eventId" — the summary-row id field (⚠) + the fact PK source
    string IdParam,           // "plantId"/"unitId"/"eventId" — the detail query-param name (repeated key)
    bool   StampsRunDate,     // TRUE only for OfflineEvent (RunDate is a fact PK part; §5)
    bool   StatusScoped,      // TRUE only for OfflineEvent (optionally append eventKind + eventStatusDesc on STEP 1; §5.2)
    string TargetTable,       // arm.<Table>            (fact; informational — sink is per-endpoint)
    string TargetTvp,         // arm.<Table>Tvp
    string TargetProc,        // arm.usp_BulkMerge<Table>
    string SummaryTable,      // arm.<Table>Summary     (id-catalog; informational)
    string SummaryTvp,        // arm.<Table>SummaryTvp
    string SummaryProc);      // arm.usp_BulkMerge<Table>Summary
```

Derived at run time: the fully-substituted summary/detail paths (`{version}` substituted; STEP-1
filters appended by the provider); the reader appends `&limit=&offset=` per page and `{idParam}=…`
per detail batch.

### 2.1 The 3 concrete descriptors

| EndpointId | Product | STEP 1 SummaryPath | STEP 2 DetailPath | DataArrayKey ⚠ | IdField/IdParam ⚠ | StampsRunDate | StatusScoped | Fact / id-catalog |
|-----------|---------|--------------------|-------------------|:--------------:|-------------------|:-------------:|:------------:|-------------------|
| Plant | plants | `idb/{ver}/plants/summary` | `idb/{ver}/plants/detail` | `plants` | `plantId` | no | no | `arm.Plant` (`PlantId`) / `arm.PlantSummary` |
| Unit | units | `idb/{ver}/units/summary` | `idb/{ver}/units/detail` | `units` | `unitId` | no | no | `arm.Unit` (`UnitId`) / `arm.UnitSummary` |
| OfflineEvent | offlineevents | `idb/{ver}/offlineevents/summary` | `idb/{ver}/offlineevents/detail` | `offlineEvents` ⚠ | `eventId` | **yes** | **yes** | `arm.OfflineEvent` (`RunDate,EventId`) / `arm.OfflineEventSummary` |

`{ver}` = `IirSettings.Version` (default `v2.7`). TVP/proc names follow `arm.<Table>Tvp` /
`arm.usp_BulkMerge<Table>` and `arm.<Table>SummaryTvp` / `arm.usp_BulkMerge<Table>Summary` (§7 fixes
the column contracts).

### 2.2 Registry invariants (`IirDescriptors` static ctor — fail fast at startup, CWG precedent)

- Exactly **3** descriptors; every `EndpointId` unique.
- `StampsRunDate == true` **iff** `EndpointId == "OfflineEvent"`; `StatusScoped == StampsRunDate`.
- `Product`, `IdField`, `IdParam` non-blank; `SummaryPath` contains `{ver}` and ends `/summary`;
  `DetailPath` contains `{ver}` and ends `/detail`; all six SQL-object names non-blank.

Asserted once at first static access (mirroring `CwgDescriptors`), so a mis-wired descriptor fails
at startup rather than mis-building a request later.

---

## 3. JWT token auth — mint, cache, thread-safe re-mint (unchanged by the two-step rework)

IIR uses a **JWT presented as a Bearer token**. There is **no OAuth2 grant, no scope, no refresh
token** — a login endpoint mints a JWT, then `Authorization: Bearer <jwt>` is stamped on every
data call (summary **and** detail), and on expiry/401 the loader mints a new one (`docs/apis/IIR.md`
§2).

### 3.1 Minting (`IirTokenProvider.MintAsync`, over the un-authed `"IIR.Token"` client)

```
POST {TokenEndpoint}?username={Username}&password={Password}&tokenLifeTime={TokenLifetimeDays}
Accept: application/json         (empty body)
```

- `TokenEndpoint` = `IirSettings.TokenEndpoint` if set, else derived `{BaseUrl}/idb/{Version}/token`.
- **Credentials are in the query string** → the `"IIR.Token"` client `.RemoveAllLoggers()` and the
  provider **never logs the mint URI** (only a fixed sanitized string like `POST …/token`). This is
  the one place a secret would appear in a URL; both guards are load-bearing.
- `tokenLifeTime` is in **days** (default 1, max 30). Re-minting handles expiry, so a short lifetime
  is safe; a longer one only reduces mint frequency (relevant given the mandatory-detail request
  volume, §11).

### 3.2 Tolerant token extraction (⚠ delivery channel — `docs/apis/IIR.md` §2.2)

The doc is **not certain** whether the JWT comes back in the response **body** or a **header**, so
the provider reads it **tolerantly, in order**:

1. **Body:** parse the JSON; take the first non-blank string property whose name matches
   (case-insensitively) `token`, `access_token`, `accessToken`, or `jwt`. Strip a leading `Bearer `.
2. **Header:** else read the response `Authorization` header (`Bearer eyJ…`, strip the prefix); else
   any response header whose value starts with `Bearer ey` or `ey`.
3. If found, cache it; if **neither** channel yields one → **throw** a clear error ("could not
   extract a JWT from the /token response — verify the token delivery channel per `docs/apis/IIR.md`
   §2.2"). Never log the token or the body.

Optionally decode the JWT `exp` claim to pre-expire the cache proactively — **not required** (the
401 re-mint is the correctness mechanism). On a non-2xx from `/token`, throw (a 401/403 here is a
**credential** failure, surfaced loudly).

### 3.3 The delegating handler (`IirTokenAuthHandler`) — stamp per attempt, re-mint once on 401

Per outgoing data request (summary or detail):

1. `token = await provider.GetTokenAsync(ct)` — cached JWT, minted once on first use. The provider
   is **thread-safe**: a double-checked `SemaphoreSlim` guards the mint so the first of many
   concurrent callers mints and the rest await the same result (shared across all pipelines/steps).
2. Stamp `request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token)`.
3. `response = await base.SendAsync(request, ct)`.
4. **If `response.StatusCode == 401`:** dispose it, call
   `token = await provider.RefreshTokenAsync(staleToken, ct)` (re-mint), re-stamp, and `return await
   base.SendAsync(request, ct)` **once**. A second consecutive 401 propagates (loud — a genuine
   credential failure, not an expiry).
   - **RefreshTokenAsync avoids a thundering herd:** it takes the `staleToken` the caller saw and,
     under the lock, re-mints **only if** the cached token still equals `staleToken`; otherwise it
     returns the already-refreshed cached token. So when the JWT expires mid-run and many requests
     401 at once, exactly **one** mint happens.
5. Return the response. The handler **never retries** anything other than the single 401 re-mint;
   `429`/`5xx` are the outer Polly policy's job. **401 is excluded from the Polly policy** (§1.3) so
   the handler owns it.

Because the request body is empty (§4.1) the replay is trivially safe to re-send.

---

## 4. The mandatory two-step reader (`IirSourceReader<TRow>`)

Implements `ISourceReader<IirWorkUnit,TRow>` **directly** (NOT `HttpJsonSourceReaderBase`, which
`EnsureSuccessStatusCode()` + single-shot deserializes and would throw on the 404/no-data cases and
cannot page or batch). Constructed per endpoint with `(HttpClient["IIR"], settings, fileLog,
descriptor, Func<JsonElement,IirWorkUnit,TRow?> rowFactory, ISink<IirSummaryRow> summarySink,
logger)`. `ReadAsync(unit)` runs the **whole two-step pull** and returns the fact rows for the
pipeline's fact sink.

### 4.0 Step diagram (one work unit → one endpoint's full pull)

```
STEP 1 — DISCOVER (summary, paged, country-filtered)
  limit = clamp(SummaryPageSize, 1, 1000);  offset = 0
  coords = {}                                        // Dictionary<long,(double? Lat, double? Long)>
  ids    = []                                        // ordered, de-duplicated
  loop:
     POST {BaseUrl}/{SummaryPath}?{unit.SummaryQuery}&limit&offset   (empty body, Bearer)
       404 / empty array / totalCount==0 → stop (NotAvailable path, §4.5)
       200 → parse envelope (tolerant array key, §4.5)
     for each summary row:
        id   = IirParse.Long(Prop(row, descriptor.IdField))          // plantId/unitId/eventId (⚠)
        lat  = IirParse.Float(Prop(row, "plantLatitude","latitude"))      // ⚠ casing/presence
        long = IirParse.Float(Prop(row, "plantLongitude","plantlongitude","longitude"))
        if id != null: ids.Add(id); coords[id] = (lat, long)
     offset += limit; stop when offset >= totalCount OR resultCount < limit OR page empty
  → write the ID-ONLY id-catalog CENSUS: summaryRows = ids.Select(id => IirSummaryRow{
        RunDate = unit.RunDate, EntityId = id })   // lat/long stay in `coords` (in-memory), not persisted here
     await summarySink.WriteAsync(summaryRows, ct)   // side-write; own SqlWriteGate proc key (§4.4)

STEP 2 — ENRICH (detail, batched ≤ DetailBatchSize, paged if needed)
  facts = []
  for batch in Chunk(ids, clamp(DetailBatchSize, 1, 50)):
     POST {BaseUrl}/{DetailPath}?{idParam}=id&{idParam}=id…&limit={batch.Count}&offset=0   (empty body, Bearer)
       (page the batch with limit/offset only if the batch could exceed the detail limit; ≤50 fits one page)
       404 / empty → skip this batch (log; not a unit failure)
       200 → for each detail element:
          row = rowFactory(elem, unit)                 // FACTS come from DETAIL (§6/§7)
          if row is null: continue                     // dropped (see §4.5 classification)
          // LAT/LONG CARRY-FORWARD (§6.3): fill from the STEP-1 summary when detail omits it
          if row.Latitude  is null && coords.TryGetValue(row.EntityId, out var c): row.Latitude  = c.Lat
          if row.Longitude is null && coords.TryGetValue(row.EntityId, out var c): row.Longitude = c.Long
          facts.Add(row)
  → FileLog the DETAIL outcome (§8); stamp FileLogId on every fact row; return facts
```

### 4.1 Transport

All calls: **`POST` + query-string params + empty request body** (`docs/apis/IIR.md` §3.1).
Multi-value filters (STEP-1 `physicalAddressCountryName`, optional OfflineEvent `eventKind`/
`eventStatusDesc`) and STEP-2 id lists are **repeated query keys**. The Bearer header is added by
the auth handler (§3); the reader logs only the sanitized relative path + non-secret params.

### 4.2 STEP 1 paging (offset/limit)

`totalCount` is known only **after** the first summary page — this is exactly why the pull lives in
a single work unit (§5) rather than one-unit-per-page. Page `limit = clamp(SummaryPageSize,1,1000)`,
`offset += limit`, stop when `offset >= totalCount` (primary) or `resultCount < limit` / empty page
(safety nets) or a `MaxPagesSafety` guard trips. Each page's `JsonDocument` is disposed after its id
+ lat/long are read into `ids`/`coords` (the IHS pager memory posture).

### 4.3 STEP 2 batching (≤50 ids/call)

Chunk `ids` into `clamp(DetailBatchSize,1,50)`-id batches (`docs/apis/IIR.md` §3.2: detail `limit`
max = 50; ≤50 ids ≈ 800 chars of query string — safely under URL limits). One detail call per batch,
`fields` **not** passed (so detail returns its full default set). Detail returns one record per id,
so a batch of ≤50 fits one page; page the batch with `limit`/`offset` only defensively.

### 4.4 The id-catalog side-write (keeps the pipeline single-ISink — likely review finding)

`LoaderPipelineBase` has exactly **one** `ISink` (the fact sink). The id-catalog census is therefore
a **reader side-write**, not the pipeline sink — the same posture as the `arm.FileLog` direct
writer:

- The reader is injected with a per-endpoint `ISink<IirSummaryRow>` (`IirPlantSummarySink`, etc., a
  `SqlSinkBase<IirSummaryRow>` bound to `arm.usp_BulkMerge<Table>Summary` / `arm.<Table>SummaryTvp`).
- After STEP 1 completes, the reader calls `summarySink.WriteAsync(summaryRows, ct)` **before** STEP
  2. `SqlSinkBase.WriteAsync` already **acquires `SqlWriteGate` under its own proc key**
  (`{server}/{db}::arm.usp_BulkMerge<Table>Summary`) — a distinct key from the fact sink and from
  `arm.usp_UpsertFileLog`, so the census write never deadlocks against the fact merge, and its batch
  is de-duped on `(RunDate, EntityId)` by `SqlSinkBase` before the TVP MERGE.
- This is stated explicitly because a reviewer will otherwise ask "how does a single-ISink pipeline
  also persist the summary census?" — the answer is the reader owns the census as a side-write, the
  fact rows flow through the pipeline's ISink as normal, and the two writes hit different procs under
  different gate keys.

### 4.5 No-data / error tolerance (both steps)

- **STEP 1** 404 / empty array / `totalCount == 0` → `NotAvailable`, no unit failure, no ids → the
  census is empty and STEP 2 is skipped (the CWG/AGSI 404-tolerant pattern).
- **STEP 2** a 404/empty batch → skip that batch (log at Debug), continue the remaining batches; a
  batch that maps zero rows is not a unit failure.
- **Tolerant envelope reading** (shared by both steps): parse with `JsonDocument`; read scalar
  envelope fields (`limit`/`offset`/`resultCount`/`totalCount`) case-insensitively/defensively; find
  the data array by (a) the descriptor's `DataArrayKey` case-insensitively, else (b) the first
  array-valued property not in `{limit,offset,resultCount,totalCount}`, else (c) treat as empty.
- **401** → auth handler re-mints (§3.3); a persistent 401 throws (loud credential failure).
- **403** → throw (subscription/entitlement gap — distinct from a 401).
- **429/5xx** after Polly retries exhausted → throw (unit fails, run continues).
- **Row-drop classification** (CWG-style): a `From(...)` returning null because a business cell was a
  sentinel/blank → Debug; a null because the **id/PK** was missing/unparseable → Warning (a keyless
  detail record cannot be persisted).
- **FileLog outcome** = `facts.Count == 0 ? "NotAvailable" : "Success"`; upsert the hub row (§8),
  stamp `FileLogId` on every fact row, return them (or empty).
- **Exceptions:** `catch (OperationCanceledException) → throw;` (no FileLog write on a spent token —
  `LoadLog` already records cancellation). `catch (Exception)` → best-effort `FileLog "Failed"` (with
  `CancellationToken.None`), then **rethrow** so `LoaderPipelineBase` records the `LoadLog` failure
  and the run continues (fail-a-block-not-the-run). A failure in STEP 1 or in the census side-write
  fails the whole unit (both are prerequisites for STEP 2).

---

## 5. Work-unit modeling, RunDate / timezone & resume keying

All three pulls are **full-snapshot / current-state** (no per-date backfill), so the natural
granularity is **one work unit per endpoint per run**, running the full two-step pull internally
(§4). One-unit-per-page/-per-batch is rejected: `totalCount` (and thus the id set) is unknown until
STEP 1's first call, and the census + carry-forward must span STEP 1 and STEP 2 within one unit.

```
IirWorkUnit : WorkUnit
    EndpointId       // "Plant" | "Unit" | "OfflineEvent"
    RunDate          // DateOnly — the US-CENTRAL calendar date of the run (§5.1); stamped into facts + id-catalog
    SummaryQuery     // the fully-built STEP-1 filter query (§5.2)
    KeyValue         // precomputed resume key (§5.3)
    Key => KeyValue
    DisplayName      // e.g. "IIR OfflineEvent 2026-08-20 (two-step)"
```

`IirWorkUnitProvider.GetWorkUnitsAsync(context)` returns **exactly one** `IirWorkUnit` per endpoint
(DB-free). The detail queries are built by the reader (§4.3), not the provider.

### 5.1 Central run date & timezone conversion

`RunDate` = the **US Central** calendar date of the run, converted from `context.StartedAtUtc`:

```
runDate = DateOnly.FromDateTime(
    TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.SpecifyKind(context.StartedAtUtc, DateTimeKind.Utc), CentralTz))
```

`CentralTz` is resolved exactly as IHSPointLogic's `PlCentralTimeZone` / CWG's `ResolveEastern`: try
the Windows id `"Central Standard Time"` then IANA `"America/Chicago"`, cache once, last-resort fall
back to UTC. **US Central (DST-aware)** is chosen deliberately: ARM Energy operates on the
US-Central business day; the OfflineEvent daily snapshot's partition boundary and the id-catalog
census date should flip on that business boundary (not UTC), so a run that straddles UTC midnight
still stamps the intended business date. (Contrast: CWG US-Eastern, AGSI CET, IHS UTC — each aligned
to its source/consumer; IIR aligns to the Central business day.)

### 5.2 Per-endpoint STEP-1 query strings

- **All three** — `physicalAddressCountryName=<each PhysicalAddressCountryNames>` as repeated keys
  (default `U.S.A.` + `Canada`; ⚠ exact U.S. spelling per `docs/apis/IIR.md` §3.2). `limit`/`offset`
  are appended by the reader per page (§4.2), not the provider.
- **OfflineEvent** (`StatusScoped`) — the default is **country-filter-only** (the status/kind
  filters default to empty, §10). If `OfflineEventKinds`/`OfflineEventStatuses` are configured, the
  provider appends `eventKind=<each>` + `eventStatusDesc=<each>` as repeated keys for optional
  narrowing (e.g. `&eventKind=O&eventStatusDesc=Ongoing`). **This is a status/country snapshot, not
  a date-window/backfill** — there is no confirmed `modifiedSince`/date filter; the daily `RunDate`
  partition builds history (§5.4).

### 5.3 Resume keying (idempotent, per Central day)

All three are undated "current-state" pulls, so they follow CWG's **undated-endpoint** keying with
the Central date as the hot token:

```
hot = HotKeyStrategy == RunDate ? runDate:yyyyMMdd(Central) : context.RunId:N
Plant        key = "iir:Plant:{runDate:yyyyMMdd}"           (+ ":run={hot}" only when HotKeyStrategy=RunId)
Unit         key = "iir:Unit:{runDate:yyyyMMdd}"
OfflineEvent key = "iir:OfflineEvent:{runDate:yyyyMMdd}"
```

- Default `HotKeyStrategy = RunDate`: the key is the Central date, so a host scheduled **daily**
  runs each endpoint's full two-step pull **once per Central calendar day**; a **same-day rerun
  idempotently skips** via `core.LoadLog` (`BeginAsync` returns null) — **avoiding the entire
  expensive two-step run**, not just the write (§11 makes this matter).
- `HotKeyStrategy = RunId`: key gains `:run={RunId}`, so every invocation re-pulls; the idempotent
  MERGEs (facts and id-catalog, §12) keep re-runs safe.
- The Central date is baked into the key for all three; **OfflineEvent's stamped `RunDate` fact-PK
  column and the id-catalog `RunDate` equal that same Central date**, so the resume key, the fact PK
  partition, and the census partition all agree by construction.

### 5.4 RunDate stamping — facts and census

- `arm.OfflineEvent` fact PK = **`(RunDate DATE, EventId INT)`**; the row factory stamps
  `RunDate = unit.RunDate` onto every OfflineEvent fact row (a **daily snapshot** of the active/
  upcoming outage set — history accrues as a new partition each Central day).
- `arm.Plant` / `arm.Unit` facts have **no RunDate** — single-row-per-id upserts (PK `PlantId` /
  `UnitId`); the catalogue accretes across runs.
- **All three id-catalog tables are `RunDate`-partitioned** (`PK (RunDate, id)`) — a per-run census
  of *what was discovered by STEP 1 that run*, including Plant/Unit (whose facts are not
  RunDate-partitioned). So the census records the daily discovered id set + lat/long for all three,
  independent of the fact tables' keying.

---

## 6. Tolerant mapping contract + per-table NULL / derived / dropped columns

### 6.1 The tolerant mapping contract (every row factory obeys it)

The three **detail** schemas are reconstructed / `⚠` (`docs/apis/IIR.md` §5–§7 populated from
`detail`), so the row factories are **tolerant by construction**: a wrong-cased key, missing field,
or unexpected flag encoding degrades to `NULL` rather than failing the row/run. `IirParse.*` (shared,
invariant culture):

| Helper | Handles | Target |
|--------|---------|--------|
| `Prop(elem, name…)` | **case-insensitive** lookup over candidate names (e.g. `"plantLatitude","plantlongitude","latitude"`), first present wins | `JsonElement?` |
| `Nested(elem, obj, field)` | walks a nested object case-insensitively (`mailingAddress.city`, `phone.number`, `parent.companyName`, `plantPhysicalAddress.stateName`); missing → null | `JsonElement?` |
| `Int/Long(elem)` | JSON number **or** string-number | `int?`/`long?` |
| `Float/Decimal(elem)` | JSON number **or** string-number | `double?`/`decimal?` |
| `String(elem)` | trims; blank / `" "` → NULL; casts a numeric (`phone.cc`) to text | `string?` |
| `DateStripZ(elem)` | **strips a trailing `Z[UTC]` / `[UTC]` suffix**, then parses ISO-8601 as UTC | `DateTime?`→`DATETIME2(0)` / `DateOnly?`→`DATE` |
| `Flag(elem)` | tolerates **all three** encodings — `0/1`, `true/false`, `"Y"/"N"` (also `"1"/"0"`, `"true"/"false"`, case-insensitive) → normalizes to the DDL `INT` `0/1`; else NULL | `int?` |

Rules: a blank / missing / unparseable **business** value → **NULL for that column**, never a row
failure. A missing/unparseable **PK** value (`plantId`/`unitId`/`eventId`) → **drop the row** (warn)
— it cannot be MERGEd. Nested objects are flattened per the DDL, tolerant to the `⚠` object-name
variants (`parent`/`plantParent`, `plantPhysicalAddress`/`plantAddress`, `unitCapacity`
object-vs-scalar) via candidate names.

### 6.2 Facts come from DETAIL

The row factory reads business fields from the **detail** element (`docs/apis/IIR.md` §5/§6/§7 are
the detail↔column contract) and the injected/stamped fields from `unit` (OfflineEvent's `RunDate`).
It emits `Latitude`/`Longitude` as plain `FLOAT` columns (subject to §6.3 carry-forward);
**`PlantPoint` is NOT produced in C#** — the merge proc builds it (§7.3).

### 6.3 Lat/long carry-forward (the load-bearing summary-only finding — `docs/apis/IIR.md` §8.0)

IIR's changelog added `plantLatitude`/`plantlongitude` to the **`summary`** endpoints; lat/long may
be **summary-only** (absent from `detail`), which would leave `Latitude`/`Longitude`/`PlantPoint`
NULL under a detail-only persist. The design is robust **regardless of which endpoint returns
coordinates**:

1. **STEP 1 captures** each summary row's `(id, lat, long)` into the in-memory `coords` map (the
   id-catalog table itself stores only the id, §7.5).
2. **STEP 2's row factory** populates `Latitude`/`Longitude` from the **detail** element **if
   present**.
3. **The reader then fills any still-null coordinate** from `coords[row.EntityId]` (the STEP-1
   value) — carry-forward (§4.0). `EntityId` is the row's own id (`PlantId`/`UnitId`/`EventId`, which
   for OfflineEvent keys the plant geo carried onto the event, §8.0).
4. **`PlantPoint` is built in-proc** from whichever `Latitude`/`Longitude` is non-null (§7.3) — so
   the geography is correct whether the coordinate came from detail or summary.

Precedence (detail-first, summary-fallback) means that when detail *does* carry coordinates they win
(freshest), and when it does not the STEP-1 `coords` value fills in; if neither has them the columns and
`PlantPoint` are NULL (and the validator counts it, §9). This is implemented once in the reader
(generic across all three endpoints) via the `IIirFactRow.Latitude/Longitude` setters.

### 6.4 Columns with NO API field (deliberately NULL / derived / stamped) — `docs/apis/IIR.md` §8.1

| Table | Column(s) | Source |
|-------|-----------|--------|
| `arm.Plant` | `PlantPoint` | **derived in the merge proc** from `Latitude`+`Longitude` (§7.3) — never in the TVP |
| `arm.Plant` | `ModifiedAtUtc` | **DB-stamped** default `SYSDATETIME()` — never in the TVP |
| `arm.Plant` | the 18 mining-method flags + any `⚠` field absent from detail (and not summary-carried) | **NULL by absence** — mining flags meaningful only for coal/mineral plants |
| `arm.Unit` | `PlantPoint`, `ModifiedAtUtc` | derived / DB-stamped |
| `arm.OfflineEvent` | `RunDate` | **stamped** = the Central run date (fact PK part, §5.4) |
| `arm.OfflineEvent` | `PlantPoint`, `ModifiedAtUtc` | derived / DB-stamped |
| **All three** | `Latitude`/`Longitude` (Unit/OfflineEvent: `PlantLatitude`/`PlantLongitude`) | **detail-first, summary-carry-forward** (§6.3) — NULL only if neither step returns them |

### 6.5 API fields with NO target column (returned but intentionally dropped) — `docs/apis/IIR.md` §8.2

Documented-and-dropped (no column exists — deliberately not persisted): `/plants` **owner** company
object, `sectors[]`, `existingSqMeters`, the `*Id` counterparts of persisted `*Desc` fields,
`plantStatusId`; `/units` `sectors[]` and area metrics (`existingBuildingArea*`,
`existingProductArea*`); `/offlineevents` `heatRate` and `sectors[]`. Any **newly-observed** live
field must be flagged to the MANAGER before dropping (platform rule) — a §13 checklist item.

---

## 7. Full-field mapping (the DATABASE_DEVELOPER + CODER contract)

The **per-column field→column contract is `docs/apis/IIR.md` §5 (Plant, 71 cols), §6 (Unit, 47
cols), §7 (OfflineEvent, 63 cols)** — verbatim, populated from **`detail`**, including SQL types and
nullability. This design adds the cross-cutting rules:

### 7.1 Fact TVP / column-order contract

Each fact table's **TVP column order** is `FileLogId` **first**, then the columns in the API-doc
table order, with derived/stamped columns handled specially (§7.2). The sink's `BuildTable`, the
`002` TVP type, and the `003` merge proc `SELECT`/`INSERT` lists must mirror it **identically** (the
load-bearing `SqlSinkBase` contract). Each sink **de-dups its batch on the MERGE key** before
building the TVP (Platts/CWG/AGSI posture).

### 7.2 What is / isn't in the fact TVP

- `FileLogId` — **first** TVP column on all three; UPDATEd-on-match provenance, **not** a merge key.
- `Latitude`/`Longitude` (Plant) and `PlantLatitude`/`PlantLongitude` (Unit, OfflineEvent) — plain
  **`FLOAT`** columns, present in the TVP (populated detail-first / summary-carry-forward, §6.3).
- `PlantPoint` (`GEOGRAPHY`) — **NOT** in the TVP; built in-proc (§7.3).
- `ModifiedAtUtc` — **NOT** in the TVP; DB default.
- `RunDate` (OfflineEvent) — **present** in the TVP (stamped by C#, PK part).

### 7.3 In-proc geography build (locked decision 5)

No geography crosses the TVP. The merge proc computes `PlantPoint` per source row, in both the
INSERT and UPDATE branches:

```
PlantPoint = CASE
    WHEN src.Latitude  IS NOT NULL AND src.Longitude IS NOT NULL
     AND src.Latitude  BETWEEN  -90 AND  90
     AND src.Longitude BETWEEN -180 AND 180
    THEN geography::Point(src.Latitude, src.Longitude, 4326)
    ELSE NULL END
```

(Unit/OfflineEvent substitute `PlantLatitude`/`PlantLongitude`.) **The range guard is load-bearing:**
`geography::Point` raises on an out-of-range coordinate, which would fail the whole MERGE batch; an
out-of-range or missing coordinate yields `PlantPoint = NULL` (validator counts it, §9). SRID 4326
(WGS 84), argument order `(lat, long)`.

### 7.4 Fact merge keys

- `arm.Plant` — MERGE on `PlantId`.
- `arm.Unit` — MERGE on `UnitId` (`PlantId` an **advisory FK**, §15).
- `arm.OfflineEvent` — batch-dedup `PARTITION BY (RunDate, EventId)`, MERGE on `(RunDate, EventId)`
  (`UnitId`/`PlantId` advisory FKs).

### 7.5 The id-catalog (summary) tables — for DATABASE_DEVELOPER (new, per decision 3)

Three minimal per-run census tables, one per endpoint, written by the reader side-write (§4.4). The
row type `IirSummaryRow` is shared; the sink/TVP/proc/table differ per endpoint. Shape:

| Column | Type | Null | Key | Notes |
|--------|------|:----:|:---:|-------|
| `RunDate` | `DATE` | No | **K** | the US-Central run date (§5.1) — same value the facts use |
| `<Id>` (`PlantId`/`UnitId`/`EventId`) | `INT` | No | **K** | the STEP-1 discovered id (`descriptor.IdField`) |
| `DiscoveredAtUtc` | `DATETIME2(3)` | Yes | | DB default `SYSDATETIME()` — first-seen this run (not in the TVP) |
| `ModifiedAtUtc` | `DATETIME2(3)` | Yes | | DB default (not in the TVP) |

- **PK / MERGE key `(RunDate, <Id>)`**; TVP columns = `(<Id>)` only — the census is **ID-ONLY**
  (`RunDate` is the scalar `@RunDate` proc param, and `DiscoveredAtUtc`/`ModifiedAtUtc` are DB
  defaults, not in the TVP — matching the fact-table `ModifiedAtUtc` posture). One TVP + one
  `arm.usp_Upsert<Table>Summary` MERGE proc per endpoint; because the whole row is the key, the
  MATCHED branch has nothing to copy and only re-stamps `ModifiedAtUtc`.
- **No `FileLogId`** on the census by default (decision 3's minimal shape); DATABASE_DEVELOPER may
  add one for provenance parity if preferred (§15).
- **No lat/long** either: the census records only *which* ids STEP 1 returned. The summary-only
  coordinates are still read from the STEP-1 payload into `IirSummaryRow` and carried forward into
  the FACT row (§6.3) — the fact tables (`Latitude`/`Longitude`/`PlantPoint`) are their durable
  home; the census does not duplicate them.
- These tables are the platform's "refresh the stored list" artifact.

---

## 8. FileLog hub (`arm.FileLog`)

Reuse the AGSI/CWG `arm.FileLog` hub: **one row per endpoint pull per run**, recording the DETAIL
(fact) outcome + rows, with `FileLogId` stamped onto the fact rows. Natural key (AGSI shape, no
`Variant` slot needed):

```
UNIQUE (Endpoint, RepresentativeDate)     -- SQL NULL-equality collapses undated rows
```

`IirFileContext(string Endpoint, DateOnly? RepresentativeDate, string RequestPath)`.

| Endpoint | RepresentativeDate | RowCount | Hub-row cardinality |
|----------|--------------------|----------|---------------------|
| Plant | the Central run date | persisted `arm.Plant` detail rows | one row per **capture day** |
| Unit | the Central run date | persisted `arm.Unit` detail rows | one per capture day |
| OfflineEvent | the Central run date (= the fact `RunDate` / census `RunDate`) | persisted `arm.OfflineEvent` detail rows | one per capture day |

- `RowCount` audits the **detail/fact** rows; the STEP-1 census count is auditable directly from the
  `arm.*Summary` table (and logged by the reader). (If DATABASE_DEVELOPER wants an explicit
  per-step audit, an optional `Variant`/`Step` slot `{Summary, Detail}` would yield two hub rows per
  endpoint per run — off by default, §15.)
- `IIirFileLog.UpsertAsync(file, status, httpStatus, requestPath, rowCount, ct) → FileLogId`.
  `SqlIirFileLog` calls `arm.usp_UpsertFileLog` under
  `SqlWriteGate.AcquireAsync(KeyFor(ConnectionString, "arm.usp_UpsertFileLog"))` (one shared key
  across all endpoints; a fast single-row upsert) — the exact `SqlAgsiFileLog`/`SqlCwgFileLog` shape.
- **DATABASE_DEVELOPER shape:** seeded `arm.Endpoint {Plant, Unit, OfflineEvent}` + seeded
  `arm.Status {Success, NotAvailable, Failed}` + `arm.FileLog` (`FileLogId` identity PK, `EndpointId`
  FK, `StatusId` FK, `RepresentativeDate DATE NULL`, `HttpStatus INT NULL`, `RequestPath
  NVARCHAR(400)`, `RowCount INT`, `FirstSeenUtc`, `LastCheckedUtc`, unique `(EndpointId,
  RepresentativeDate)`) + `arm.usp_UpsertFileLog` (name-string signature, MERGE on the unique key,
  returns `FileLogId`). Inline `VARCHAR + CHECK` is an acceptable build-only alternative.
- **No-data (empty / 404) → `NotAvailable`, no unit failure.**

---

## 9. Post-load validation (`IirLoadValidator`, module-level hook — coded now / not exercised live)

Following the AGSI `AgsiLoadValidator` precedent, `IirLoadValidator` runs as a module-level step in
`RunAsync` **after all three pipelines complete**, scoped to the run's Central date; it calls
`arm.usp_ValidateLoad(@RunDate)` and logs the anomaly report (`CheckName, Scope, ExpectedCount,
ActualCount, Detail`). **Observational** — warnings + counters, never throws (except cancellation).
Recommended checks:

- **Row counts > 0** for each enabled endpoint this run — Plant/Unit non-empty expected;
  OfflineEvent may be legitimately sparse (warn, don't fail).
- **Discovered-vs-persisted census check (new, two-step-specific):** for each endpoint, compare the
  STEP-1 census count (`arm.<Table>Summary WHERE RunDate = @RunDate`) with the persisted **detail**
  count (fact rows for that run). A large shortfall (many discovered ids that produced no detail row)
  flags a STEP-2 coverage gap (detail 404s, dropped rows, batch failures) — informational, since some
  ids may legitimately return no detail record.
- **PK non-null / no duplicates:** fact `PlantId`/`UnitId`/`(RunDate,EventId)` and census
  `(RunDate,<Id>)` not null and unique.
- **Lat/long range sanity:** count fact rows with `Latitude` outside `[-90,90]` or `Longitude`
  outside `[-180,180]` (expect 0 → those become `PlantPoint = NULL`).
- **Geography consistency:** `PlantPoint` non-null count equals rows with both coords present +
  in-range.
- **Lat/long source coverage (carry-forward health):** count fact rows whose coordinates are NULL
  but whose census row for the same id has coordinates (would indicate the carry-forward did not
  fire) — expect 0.
- **Advisory FK coverage:** `arm.Unit.PlantId` absent from `arm.Plant`; `arm.OfflineEvent`
  `PlantId`/`UnitId` absent from parents (informational — scoping may differ).
- **Flag domain:** the `INT` flag columns ⊆ `{0,1,NULL}`.

**Build-only:** written and unit-testable, **not exercised against live data** this pass.

---

## 10. Config surface — `IirSettings : LoaderSettingsBase`

Inherited: `ConnectionString` (the `IIR` DB), `MaxConcurrentWorkUnits`, `RetryCount`,
`RetryDelayMs`, `WorkUnitTimeoutSeconds`. Added / changed for the two-step rework:

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `BaseUrl` | string | `https://api.industrialinfo.com` | API host (test host `https://apitest.industrialinfo.com`) |
| `Version` | string | `v2.7` | pins `/idb/{version}/…` for reproducibility |
| `TokenEndpoint` | string | `""` | explicit `/token` URL; empty ⇒ derive `{BaseUrl}/idb/{Version}/token` |
| `Username` | string | `SEE_DB` | login user; from `core.Param`; **never logged** |
| `Password` | string | `SEE_DB` | login password; from `core.Param`; **never logged** |
| `TokenLifetimeDays` | int | 1 | `tokenLifeTime` (days, max 30); re-mint handles expiry |
| `HttpTimeoutSeconds` | int | 60 | per-request timeout on both HttpClients |
| **`RequestsPerSecond`** | double? | **5** | global client-side throttle; null/≤0 = unlimited. **Bumped from 2** to keep the mandatory-detail volume tractable (§11); still conservative. **No IIR rate limit is published — verify live and back off on 429** |
| **`PhysicalAddressCountryNames`** | string[] | **`["U.S.A.","Canada"]`** | **NEW** — STEP-1 `physicalAddressCountryName` filter (repeated key) applied to **all three** summaries (⚠ exact U.S. spelling) |
| `EnabledEndpoints` | string[] | `["Plant","Unit","OfflineEvent"]` | endpoint toggle (case-insensitive to `IIirPipeline.EndpointId`) |
| `SummaryPageSize` | int | 1000 | STEP-1 page size (`limit`), clamped `[1,1000]` |
| **`OfflineEventKinds`** | string[] | **`[]`** (was `["O"]`) | **optional** OfflineEvent `eventKind` narrowing; empty ⇒ country-only STEP 1 |
| **`OfflineEventStatuses`** | string[] | **`[]`** (was `["Ongoing","Future"]`) | **optional** OfflineEvent `eventStatusDesc` narrowing; empty ⇒ country-only STEP 1 |
| `DetailBatchSize` | int | 50 | STEP-2 ids/batch, clamped `[1,50]` (detail `limit` max = 50) |
| **`WorkUnitTimeoutSeconds`** (inherited) | int | **5400** | **raised from 900** to cover the largest two-step endpoint (§11) |
| `HotKeyStrategy` | `RunDate`\|`RunId` | `RunDate` | resume cadence (§5.3): `RunDate` = one full two-step pull per Central day; `RunId` = re-pull each run |

> **Retired: `UseDetailEnrichment`.** Detail is now **unconditional** (mandatory two-step), so the
> previous optional per-endpoint enrichment toggle is removed. `DetailBatchSize` remains (it governs
> STEP-2 batching, which now always runs).

`appsettings.json` `Loaders:IIR` block (**leave `"IIR"` OUT of `Platform:EnabledLoaders`**):

```json
"IIR": {
  "ConnectionString": "Server=ARMH-OPSDB01;Database=IIR;Integrated Security=SSPI;TrustServerCertificate=True;",
  "MaxConcurrentWorkUnits": 3,
  "RetryCount": 3,
  "RetryDelayMs": 1000,
  "WorkUnitTimeoutSeconds": 5400,
  "BaseUrl": "https://api.industrialinfo.com",
  "Version": "v2.7",
  "TokenEndpoint": "",
  "Username": "SEE_DB",
  "Password": "SEE_DB",
  "TokenLifetimeDays": 1,
  "HttpTimeoutSeconds": 60,
  "RequestsPerSecond": 5,
  "PhysicalAddressCountryNames": [ "U.S.A.", "Canada" ],
  "EnabledEndpoints": [ "Plant", "Unit", "OfflineEvent" ],
  "SummaryPageSize": 1000,
  "OfflineEventKinds": [],
  "OfflineEventStatuses": [],
  "DetailBatchSize": 50,
  "HotKeyStrategy": "RunDate"
}
```

Secrets live in `core.Param(LoaderName='IIR', ParamName IN ('Username','Password'))` (or env
`DATALOADER_Loaders__IIR__Username` / `__Password`), never in the file.

---

## 11. Two-step throughput & timeout budget (MANAGER-flagged — addressed here)

Mandatory detail turns each endpoint into `⌈totalCount / SummaryPageSize⌉` summary pages **plus**
`⌈totalCount / DetailBatchSize⌉` detail POSTs. The **plants catalogue is the sizing driver** (the
envelope example shows `totalCount ≈ 111,644`):

| Step | Count (plants) | Formula |
|------|---------------:|---------|
| STEP-1 summary pages | ≈ 112 | `⌈111,644 / 1000⌉` |
| STEP-2 detail calls | ≈ 2,233 | `⌈111,644 / 50⌉` |
| **Total requests** | **≈ 2,345** | summary + detail |

Wall-clock is pacing-bound (each request ≥ `1/RequestsPerSecond`):

| RequestsPerSecond | Plants wall-clock (pacing only) |
|------------------:|--------------------------------:|
| 2 (old default) | ≈ 1,172 s ≈ **~19.5 min** — **exceeds the old 900 s timeout** |
| **5 (new default)** | ≈ 469 s ≈ **~7.8 min** |
| 10 (if the live limit allows) | ≈ 235 s ≈ **~3.9 min** |

**Chosen defaults and reasoning:**
- **`RequestsPerSecond = 5`** — a deliberate, still-conservative bump from 2 that brings the plants
  pull to ~8 min. IIR publishes no numeric rate limit and documents no `Retry-After`, so this MUST
  be verified live and the Polly policy already backs off on any `429` (§1.3). Do **not** raise
  further without confirming the live limit.
- **`WorkUnitTimeoutSeconds = 5400` (90 min)** — the safe lever, correct regardless of RPS: it
  covers plants comfortably at RPS = 5 (~8 min) with wide margin for per-request latency, retries,
  the id-catalog side-write, a larger-than-sampled catalogue, and even the fallback RPS = 2 (~20 min).
- **Units / OfflineEvents** are expected smaller than plants; plants sizes the timeout, so both are
  covered.

**Two throughput mitigations already in the design** (no extra config):
- The **per-Central-day resume key** (§5.3, default `RunDate`) makes a same-day rerun skip the
  entire ~2,345-request pull via `core.LoadLog` — the expensive work runs at most once per business
  day.
- The **detail-first / summary-carry-forward** lat/long (§6.3) means no extra requests are needed to
  obtain coordinates even if detail omits them (they are already in the STEP-1 census).

**Memory / batch note:** a single unit accumulates the whole detail catalogue (~112k rich rows) in
memory before the one fact-sink TVP MERGE. Flag to DATABASE_DEVELOPER/CODER to **chunk the fact TVP
MERGE** (e.g. 5–10k rows/batch inside the sink) and, if needed, stream detail batches to the sink
rather than accumulating all rows — an open item (§15).

---

## 12. Concurrency, idempotency & overlap protection

- **Sinks** derive from `SqlSinkBase<TRow>`, which auto-acquires `SqlWriteGate` keyed
  `{server}/{db}::{proc}`. The **six** procs (3 fact + 3 summary) are six distinct keys, so no two
  merges serialize against each other; within one endpoint the fact merge and the census merge use
  different keys and never deadlock. Within a single unit there is one fact MERGE and one census
  MERGE (no self-contention).
- **FileLog** is a **direct** proc writer, so `SqlIirFileLog` acquires `SqlWriteGate` explicitly on
  `arm.usp_UpsertFileLog` (one shared key) — the Platts/AGSI/CWG posture. `core.LoadLog` is not gated
  (keyed per work unit).
- **Idempotent MERGE.** `arm.usp_BulkMergePlant` on `PlantId`, `…Unit` on `UnitId`, `…OfflineEvent`
  on `(RunDate, EventId)`; the three `…Summary` procs on `(RunDate, <Id>)` — never on `FileLogId`.
  So a same-day re-pull (or `RunId` re-pull) upserts facts **and** census in place — no duplicates.
- **Overlap protection is automatic (locked decision 6):** `SqlLoaderOverlapGuard` acquires the
  app-lock `DataLoader:IIR` at run start; a second host invocation while one runs logs a warning and
  exits `0`. **This loader adds no lock code.**
- **Token cache** is process-wide (§3), so all pipelines and both steps share one JWT and one
  re-mint path — critical given the ~thousands of detail requests per run.

---

## 13. Deploy / operations + live-verification checklist

**SQL run order (deploy kit):**
1. `sql/Core/001`–`004` (platform DB) — once per environment, before any loader.
2. `sql/IIR/001…` in numbered dependency order: schema + `arm.Endpoint`/`arm.Status` lookups
   (seeded) → `arm.FileLog` + `arm.usp_UpsertFileLog` → the three **fact** tables
   (`arm.Plant`/`arm.Unit`/`arm.OfflineEvent`, each with `PlantPoint GEOGRAPHY` + `ModifiedAtUtc`
   default) → the three **id-catalog** tables (`arm.PlantSummary`/`arm.UnitSummary`/
   `arm.OfflineEventSummary`, PK `(RunDate,<Id>)`, ID-ONLY, `DiscoveredAtUtc`/`ModifiedAtUtc`
   defaults) → the six TVP types → the six `arm.usp_BulkMerge*` procs (fact procs build
   `geography::Point` in-proc, §7.3) → optional `arm.usp_ValidateLoad` → optional guarded
   `999_DropIirObjects.sql` teardown (AGSI precedent).

**Secrets (before the first run):**
- Insert `core.Param` rows `(LoaderName='IIR', ParamName='Username'|'Password', ParamValue=…)`; both
  must exist (the SEE_DB resolver throws on a missing row, §1.4). Or override via env vars
  `DATALOADER_Loaders__IIR__Username` / `DATALOADER_Loaders__IIR__Password`.
- Then add `"IIR"` to `Platform:EnabledLoaders` (deferred to the live pass — build-only ships
  disabled).

**Operational note (throughput/timeout, §11):** the first live plants run is ~2,345 requests
(~8 min at RPS = 5). Watch for `429`s (none documented — the throttle + Polly back-off handle them);
if the live rate limit is confirmed higher, raise `RequestsPerSecond`; if the catalogue is far
larger than sampled, raise `WorkUnitTimeoutSeconds`. The daily `RunDate` key means a same-day rerun
after a partial failure skips the whole pull unless you set `HotKeyStrategy=RunId` (which forces a
full idempotent re-pull).

**Live-verification checklist (the `⚠` items from `docs/apis/IIR.md` §10 — confirm on the first
authenticated run; several are blocking before the merge procs are frozen):**

| # | Item (`docs/apis/IIR.md` ref) | What to confirm | Design lever if it differs |
|---|-------------------------------|-----------------|----------------------------|
| 1 | Token delivery channel (§2.2) | JWT in the `/token` response **header** or a **body** field? | `IirTokenProvider` reads both (§3.2); confirm which fires |
| 2 | **Detail** field names / casing / nesting (§5–§7, §8.0) | Exact `detail` keys for the nested company objects, plant-address object name, `unitCapacity` object-vs-scalar, lat/long casing, mining flags | Add the observed name to the `Prop`/`Nested` candidate list (§6.1) — no schema change if the DDL column is unchanged |
| 3 | **Summary-only lat/long (§8.0 — load-bearing)** | Is `Latitude`/`Longitude` returned by `detail`, or **summary-only**? | Already handled: detail-first with STEP-1 summary carry-forward (§6.3) + census persist (§7.5). Confirm the carry-forward is exercised |
| 4 | Summary id field names (§3.2a) | Are `plantId`/`unitId`/`eventId` the exact summary-row id keys? + the stable summary field set | Adjust `descriptor.IdField` / the STEP-1 `Prop` candidates |
| 5 | Detail max ids per call (§3.2) | Does `detail` accept > 50 ids, or does `limit=50` also cap id count? | Tune `DetailBatchSize` (≤50 is safe now) |
| 6 | Boolean flag encoding (§3.5) | `0/1`, `true/false`, or `"Y"/"N"`? | `IirParse.Flag` tolerates all three (§6.1) |
| 7 | OfflineEvent optional narrowing (§7) | Full `eventKind`/`eventStatusDesc` set; do date-window filters exist? | Set `OfflineEventKinds`/`OfflineEventStatuses` (default empty = country-only) |
| 8 | Envelope data-array key casing (§3.3) | `offlineEvents` vs `offlineevents`; `plants`; `units` | Reader reads it tolerantly (§4.5) |
| 9 | `physicalAddressCountryName` values (§3.2) | Exact U.S. spelling (`U.S.A.`) + full country vocabulary | Adjust `PhysicalAddressCountryNames` (§10) |
| 10 | Rate limits (§3.4) | None published — confirm a safe `RequestsPerSecond`; any `429`/`Retry-After`? | Tune `RequestsPerSecond` (§11); Polly honors `Retry-After` |
| 11 | Newly-observed unmapped fields (§8.2) | Any returned field with no target column | Flag to MANAGER before dropping (platform rule) |

---

## 14. What's NOT included / deferred

- **Live deploy / first load / `DATA_QUALITY_VALIDATOR`** — deferred to a later pass with a real PAT
  (`"IIR"` stays out of `Platform:EnabledLoaders`).
- **The two-step summary→detail pull is MANDATORY** — there is no summary-only mode and no optional
  enrichment toggle (`UseDetailEnrichment` retired, §10).
- **No date-window / backfill** — OfflineEvent is a country (optionally status) snapshot (§5.2);
  history accrues via the daily `RunDate` partition (§5.4). A confirmed event date filter (§13 item 7)
  could justify a future backfill mode — not designed now.
- **No `fields` param** on detail — endpoints return their full default set (§4.3).
- **No 3-tier discovery graph / reference providers / cross-pipeline tier barriers** — the two steps
  are intra-pipeline (locked decision 2); the IHSPointLogic machinery is deliberately unused.
- **The IIR reference endpoints** (`equipmentTypes`, `sicCodes`, `unitTypes`, `pipeline*`,
  `companies`, `boilers`, …) are **out of scope** — this loader targets exactly Plant / Unit /
  OfflineEvent.
- **Intentionally dropped API fields** (owner company, `sectors[]`, area metrics, `heatRate`, `*Id`
  counterparts) — §6.5 / `docs/apis` §8.2, not persisted.
- **Proactive JWT `exp` pre-expiry** — optional optimization only; the 401 re-mint is the
  correctness mechanism (§3.2).

---

## 15. Open items for DATABASE_DEVELOPER / reviewer

Three fact tables + three id-catalog tables + one FileLog hub + supporting objects. Coordinate on:

1. **`arm.Plant`** — the 71-column detail contract of `docs/apis/IIR.md` §5: PK `PlantId`;
   `PlantPoint GEOGRAPHY` **in-proc** (§7.3, NOT in the TVP); `Latitude`/`Longitude FLOAT` in the
   TVP; `ModifiedAtUtc` DB default (not in the TVP); `FileLogId` first TVP column.
2. **`arm.Unit`** — the 47-column contract of §6: PK `UnitId`; same lat/long / in-proc PlantPoint /
   ModifiedAtUtc / FileLogId-first rules; `PlantId` **advisory FK** (item 6).
3. **`arm.OfflineEvent`** — the 63-column contract of §7: **PK `(RunDate DATE, EventId INT)`**;
   `RunDate` in the TVP (stamped); same lat/long / PlantPoint / ModifiedAtUtc / FileLogId rules;
   batch-dedup + MERGE on `(RunDate, EventId)`; `UnitId`/`PlantId` advisory FKs.
4. **Id-catalog tables (§7.5, new)** — `arm.PlantSummary` / `arm.UnitSummary` /
   `arm.OfflineEventSummary`, PK `(RunDate, <Id>)`, ID-ONLY TVP `(<Id>)` + scalar `@RunDate`,
   `DiscoveredAtUtc`/`ModifiedAtUtc` DB defaults, one MERGE proc + one TVP + one `SqlSinkBase` sink
   each. Confirm: (a) the minimal shape (no `FileLogId`) vs adding a `FileLogId` for provenance; (b)
   whether the census should retain **all** runs (history) or be pruned/retained-N-days.
5. **In-proc geography (§7.3)** — the range-guarded `geography::Point(lat, long, 4326)` in both the
   INSERT and UPDATE branches of all three **fact** procs (not the summary procs). Confirm SRID/order
   and that an out-of-range coordinate yields `NULL`, not a batch failure.
6. **Advisory vs enforced FKs** — the three pulls are independent snapshots with possibly-different
   scoping, so a `Unit`/`OfflineEvent` row may reference a `Plant` absent from `arm.Plant` this run.
   **Recommendation: keep the FK columns but do NOT enforce them as hard constraints** (validator §9
   reports coverage). Confirm.
7. **`arm.FileLog` hub + `arm.usp_UpsertFileLog`** — key `(Endpoint, RepresentativeDate)` (§8).
   Confirm: (a) seeded lookups vs inline `VARCHAR+CHECK`; (b) whether to add an optional `Step`/
   `Variant` slot `{Summary, Detail}` for a per-step audit (off by default); (c) CWG-parity NULL
   `Region`/`Variant` columns.
8. **`arm.usp_ValidateLoad(@RunDate)`** — the §9 checks incl. the **discovered-vs-persisted census**
   and **carry-forward-health** checks (coded now, run later).
9. **Large-batch handling (§11)** — chunk the fact TVP MERGE (e.g. 5–10k rows) and/or stream detail
   batches to the fact sink, given the ~112k-row plants detail. Confirm the chunking strategy.
10. **Throughput defaults (§11)** — `RequestsPerSecond = 5` and `WorkUnitTimeoutSeconds = 5400`;
    confirm, and revisit once the live IIR rate limit is known.
11. **`TokenLifetimeDays` default** — 1 day (re-mint handles expiry). Given the ~thousands of detail
    requests per run, confirm 1 day is fine or raise to reduce mid-run mints.

---

## Coverage checklist (two-step summary → detail is MANDATORY for all three)

| Endpoint | Pipeline (internally two-step) | STEP 1 (discover) | STEP 2 (persist) | Work-unit / key | Fact table (key) / id-catalog | FileLog |
|----------|-------------------------------|-------------------|------------------|-----------------|-------------------------------|---------|
| Plant | one unit/run | `POST /plants/summary?physicalAddressCountryName=…` (paged) → ids → `arm.PlantSummary` | `POST /plants/detail?plantId=…` (≤50) → detail record | key `iir:Plant:{centralDate}` | `arm.Plant` (`PlantId`) / `arm.PlantSummary` (`RunDate,PlantId`) | Plant / central date |
| Unit | one unit/run | `…/units/summary` → ids → `arm.UnitSummary` | `…/units/detail?unitId=…` (≤50) | key `iir:Unit:{centralDate}` | `arm.Unit` (`UnitId`) / `arm.UnitSummary` | Unit / central date |
| OfflineEvent | one unit/run | `…/offlineevents/summary` (country-only default) → ids → `arm.OfflineEventSummary` | `…/offlineevents/detail?eventId=…` (≤50) | key `iir:OfflineEvent:{centralDate}`; RunDate=central date stamped into fact PK | `arm.OfflineEvent` (`RunDate,EventId`) / `arm.OfflineEventSummary` | OfflineEvent / central date |

All calls: `POST` + query-string params + empty body; ids/filters as repeated keys;
`Authorization: Bearer <jwt>`. Each pipeline runs STEP 1 (paged, country-filtered) → id-catalog
census side-write → STEP 2 (≤50-id detail batches) → lat/long carry-forward → fact MERGE, all inside
one idempotent, per-Central-day work unit. Facts are populated from **detail**; lat/long is
**detail-first with STEP-1 summary carry-forward**; `PlantPoint` is built in-proc from whichever
coordinate survives. Ready for DATABASE_DEVELOPER (3 fact tables + 3 id-catalog tables + 6 TVPs + 6
merge procs [fact procs with in-proc geography] + `arm.FileLog`/`arm.usp_UpsertFileLog` [+ lookups] +
optional `arm.usp_ValidateLoad`) and CODER (module + 3 closed pipelines + provider + token
provider/handler + two-step reader + 3 fact rows/factories/sinks + shared summary row + 3 summary
sinks + FileLog writer + throttle + validator; loader left **disabled** in `Platform:EnabledLoaders`).
