# IHSPointLogic (S&P Global / IHS Markit — PointLogic gas data) loader — design & processing flow

Design/flow spec for the IHSPointLogic loader. Input of record is the **VERIFIED, live-observed**
field reference at `docs/apis/IHSPointLogic.md` (25 endpoints, per-endpoint real field tables with
SQL types + PKs, auth, envelope/paging, date semantics). This document is the CODER + DATABASE_DEVELOPER
hand-off; it contains **no code and no SQL**. SQL objects named here are *proposed* to
DATABASE_DEVELOPER (open items in §11); C# structures are *proposed* to CODER. Every field in the API
doc must map end-to-end (model → sink → TVP → table → merge proc) — no partial column set may ship.

Locked decisions this design is built around: DB `IHSPointLogic`, schema `arm` for every object,
Integrated Security; **auth = static HTTP Basic** (`Authorization: Basic base64(ClientId:ClientSecret)`
stamped by a delegating handler on **every** call — NOT OAuth2/bearer/scope); `ClientId`/`ClientSecret`
via `SEE_DB` → `core.Param`, never logged; **descriptor/registry-driven single module** with **25
explicitly-built closed pipelines** (CWG pattern); **one `arm.FileLog` hub + `arm.usp_UpsertFileLog`**
under `SqlWriteGate`; 25 flat fact/dimension tables carrying `FileLogId`; **go-forward accumulation**
(each run captures the current report/snapshot and MERGEs by PK — `plview/retrieve/*` ignore date
params, so no historical backfill there); **report-date stamping basis = UTC** for the two endpoints
whose payload lacks a report date (`demandforecast_region`, `demandforecast_uslower48` → stamp
`ForecastDate` = run's UTC date), payload-carried dates used as-is elsewhere; the only date-param
endpoints (`supplyDemand/region` & the sub-region reuse of `/region/`) get a `DaysBack=21` /
`SettledAfterDays=7` **two-zone settled/hot resume key** (StormVista/AGSI semantics, NOT the windowing
orchestrator); a coded-but-not-exercised module-level `IHSPointLogicLoadValidator` (AGSI parity);
**build-only pass** — the loader is fully coded/tested/buildable but left **OUT of
`Platform:EnabledLoaders`** and not deployed/loaded this pass.

> **Build-only posture (read first).** The same posture CWG/AGSI shipped in: everything is coded,
> unit-tested, and buildable, but the loader is **disabled by default** (`"IHSPointLogic"` is **not** in
> `Platform:EnabledLoaders`) and there is no live DB deploy or data load this pass. (NB: the API was
> already probed live to verify the schemas, so unlike AGSI there are no ⚠ schema unknowns — but the
> loader still ships disabled.) Live deploy + first load + `DATA_QUALITY_VALIDATOR` are deferred.

Reference implementations mirrored: **`src/DataLoader.CWG/`** (descriptor registry driving N closed
per-endpoint pipelines built explicitly so the shared generics are never resolved from DI; one shared
tolerant source reader + per-endpoint row factory + per-endpoint sink; `arm.FileLog` hub +
`arm.usp_UpsertFileLog` under `SqlWriteGate`; `RemoveAllLoggers()`; sanitized-path logging;
`CwgModule` fan-out) and **`src/DataLoader.AGSI/`** (discovery reference-provider `IAgsiCountryProvider`
+ `SqlAgsiCountryProvider` — load-once-per-run behind a `SemaphoreSlim`, fail-fast if empty, backed by
an `arm.usp_Get…` read proc reading the just-refreshed table; custom tolerant `ISourceReader` that does
**not** go through `HttpJsonSourceReaderBase`; module-level `AgsiLoadValidator`; two-zone settled/hot
resume key borrowed without the windowing orchestrator).

> **Discovery-first note (this loader HAS real discovery steps).** The platform's "discovery first /
> refresh the stored list before processing" principle is honored literally and in **three tiers**:
> the independent lookups (`lookup_region`, `lookup_state`, `lookup_pointType`, `lookup_point`,
> `pointmetadata_withids`) are refreshed **first** (Tier 0); their ids then feed the discovery-fed
> lookups `lookup_county/{StateId}`, `lookup_facility/{PointTypeId}`, `lookup_subregion/{RegionId}`
> (Tier 1); and those in turn feed the parametrized facts `supplyDemand/region/{RegionId}`,
> `supplyDemand/region/{SubRegionId}`, `volumeHistory/point?pointIds=` (Tier 2). Each tier refreshes
> its stored tables in place, and the next tier's **reference providers read the just-refreshed
> tables** (§3). Region → Subregion → SD-by-SubRegion is a **3-deep chain** across the three tiers.

---

## 0. Class / structure inventory (shared vs per-endpoint)

| Concern | Type(s) | Shared or per-endpoint |
|--------|---------|------------------------|
| Settings | `IHSPointLogicSettings : LoaderSettingsBase` | shared (§8) |
| Descriptor | `PlEndpointDescriptor` (record), `PlDescriptors` (static registry of **25**) | shared (§2) |
| Enums | `PlFamily {P,V}`, `PlArchetype {LatestLookup, GoForwardSnapshot, DiscoveryLookup, DiscoveryDatedFact, BatchedFact}`, `PlPathParam {None, StateId, PointTypeId, RegionId, SubRegionId, PointBatch}`, `PlReportDateBasis {None, PayloadCarried, StampUtcRunDate, ParamInjected}`, `PlHotKeyStrategy {RunDate, RunId, RunHour}` (default `RunHour`, §B.4) | shared |
| Work unit | `PlWorkUnit : WorkUnit` (ONE type for **all 25** endpoints, CWG-style) | shared (§4) |
| Work-unit providers (4 classes, 5 archetypes) | `PlSnapshotWorkUnitProvider` (A **and** B), `PlDiscoveryLookupWorkUnitProvider` (C), `PlDiscoveryDatedFactWorkUnitProvider` (D), `PlBatchedFactWorkUnitProvider` (E) — each `IWorkUnitProvider<PlWorkUnit>`, constructed **per descriptor** in a factory closure | shared classes, per-descriptor instances |
| **Reference providers (5)** | `IPlRegionProvider`/`SqlPlRegionProvider`, `IPlStateProvider`, `IPlPointTypeProvider`, `IPlSubregionProvider` (+ SubRegionId→RegionId map), `IPlPointProvider` — AGSI load-once/fail-fast pattern, backed by `arm.usp_Get<X>Ids` read procs (§3) | shared |
| Basic-auth plumbing | `PlBasicAuthHandler : DelegatingHandler` (stamps `Authorization: Basic …`, never logs it) | shared (§5) |
| Throttle / retry | `PlRateLimiter` (singleton) + `PlRateLimitingHandler` (transient) + `PlHttpPolicy` (Polly) | shared (§5) |
| Tolerant JSON pager | `PlSourceReader<TRow> : ISourceReader<PlWorkUnit, TRow>` (pages both envelope shapes, tolerant parse, FileLog, stamp) | shared (§5) |
| Value/date parsers | `PlParse.*` (number/string-number/mixed-date/DateTimeOffset/bit/blank→null) | shared (§5) |
| FileLog | `PlFileContext` (readonly struct), `IPlFileLog`, `SqlPlFileLog` (`arm.usp_UpsertFileLog`, `SqlWriteGate`, returns `FileLogId`) | shared (§6) |
| Fact/dimension row | `IPlFactRow { int FileLogId { get; set; } }` + **25 row types** | interface shared, 25 rows per-endpoint |
| Row factory | `Func<JsonElement, PlWorkUnit, TRow?>` (a `static TRow? From(elem, unit)` per row type) | **per-endpoint** (the only mapping code) |
| Sink | `SqlSinkBase<TRow>` subclass **×25** (per-endpoint proc + TVP) | **per-endpoint** |
| Pipeline | `IPlPipeline : ILoaderPipeline { string EndpointId; int Tier; }`, `PlPipeline<TRow> : LoaderPipelineBase<PlWorkUnit,TRow,TRow>` | shared |
| Module | `IHSPointLogicModule : ILoaderModule` (`LoaderId = "IHSPointLogic"`) | shared (§1) |
| Post-load validation | `IHSPointLogicLoadValidator` (module-level hook) | coded now, not exercised live (§10) |

The **only** per-endpoint C# is: the `TRow` class, its `From(elem, unit)` row factory, the
`SqlSinkBase<TRow>` subclass, and the descriptor entry. Everything above the row factory (Basic auth,
HTTP, retry, throttle, paging, both-envelope handling, tolerant parse, FileLog, 404-tolerance,
work-unit enumeration per archetype, tier ordering, idempotency, the pipeline loop) is written once or
inherited from Core. There is **one** shared JSON pager (not five parse shapes) because every endpoint
returns JSON in one of two envelope shapes the reader handles uniformly.

---

## 1. Module topology & pipeline strategy

**One module (`IHSPointLogicModule`, `LoaderId = "IHSPointLogic"`), 25 closed per-endpoint pipelines**,
built explicitly (CWG pattern), toggled by `EnabledEndpoints[]`, and executed in **three tiers with
hard barriers between them** (deeper than AGSI's single About→Storage step).

### 1.1 Why closed per-endpoint pipelines (never register the shared generics)

All 25 endpoints share the `PlWorkUnit` type and the `PlSourceReader<TRow>` reader. If we registered a
single `IWorkUnitProvider<PlWorkUnit>` (or `ISourceReader<PlWorkUnit,TRow>`) in DI, every pipeline
would resolve the *same* service and drive the wrong endpoint — exactly the collision the
CWG/AGSI/Platts headers warn about. We therefore **never register the generic provider/reader in DI**.
Each pipeline is assembled inside a module factory closure that `new`s a descriptor-bound provider +
reader + per-endpoint sink and passes them to `PlPipeline<TRow>`'s constructor; `LoaderPipelineBase`
takes its provider/source/sink as constructor arguments, so those generics are never resolved by the
container. This is the exact `CwgModule.BuildPipeline` shape (`src/DataLoader.CWG/CwgModule.cs`).

### 1.2 The generic pipeline class (CWG-style, `LoaderPipelineBase` directly)

```
IPlPipeline : ILoaderPipeline { string EndpointId { get; }  int Tier { get; } }

PlPipeline<TRow> : LoaderPipelineBase<PlWorkUnit, TRow, TRow>, IPlPipeline
    // ctor(endpointId, tier, IWorkUnitProvider<PlWorkUnit>, ISourceReader<PlWorkUnit,TRow>,
    //      ISink<TRow>, ILoadLogRepository, IHSPointLogicSettings, ILogger)
    //   → base("IHSPointLogic", provider, source, IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
```

This reuses the platform's vetted per-unit loop unchanged: `BeginAsync` idempotency skip → `ReadAsync`
→ identity transform → `WriteAsync` → `CompleteSuccess/Failure`, bounded by `ParallelRunner` at
`MaxConcurrentWorkUnits`, per-unit timeout, fail-one-not-the-run. No custom windowed orchestrator is
needed (unlike StormVista): every endpoint's unit list materializes cheaply (§4). The `Tier` marker
lets `RunAsync` group and order the pipelines.

### 1.3 DI registration (`RegisterServices`)

1. `services.AddLoaderSettings<IHSPointLogicSettings>(configuration, Id);` (binds `Loaders:IHSPointLogic`;
   resolves `ClientId`/`ClientSecret` = `"SEE_DB"` from `core.Param` lazily at run time).
2. Shared throttle: `PlRateLimiter` (singleton) + `PlRateLimitingHandler` (transient).
3. `PlBasicAuthHandler` (transient) — the delegating handler that stamps
   `Authorization: Basic base64(ClientId:ClientSecret)` on every request (§5).
4. Named `HttpClient` `"IHSPointLogic"`:
   - `Timeout = HttpTimeoutSeconds`, `Accept: application/json`;
   - `.RemoveAllLoggers()` — suppress `IHttpClientFactory` default logging. The URL carries no secret
     (the credential is a **header**), but the Basic header **must never be logged**, and we keep the
     log surface identical to CWG/AGSI;
   - `.AddPolicyHandler(...)` retry **outer** (Polly; `RetryCount`/`RetryDelayMs`; retry `429`/`5xx`/
     transient and honor `Retry-After` — the API doc says none surfaced during the probe, so pace
     conservatively and back off if `429` ever appears);
   - `.AddHttpMessageHandler<PlBasicAuthHandler>()` — auth handler;
   - `.AddHttpMessageHandler<PlRateLimitingHandler>()` throttle **innermost** so every attempt is paced.
   (Handler order: retry outer → auth → throttle inner. Auth must run on every retry too.)
5. `services.AddSingleton<IPlFileLog, SqlPlFileLog>();`
6. `services.AddSingleton<IPlRegionProvider, SqlPlRegionProvider>();` and likewise the State, PointType,
   Subregion, Point reference providers (§3) — each a singleton (= load-once-per-run).
7. `services.AddSingleton<IHSPointLogicLoadValidator>();`
8. Register the 25 pipelines with a factory helper, one line each — `BuildPipeline(sp, descriptor,
   rowFactory, sinkFactory)`. The helper reads the descriptor's `Archetype` and constructs the matching
   provider (injecting the reference provider for C/D/E), the shared `PlSourceReader<TRow>` (descriptor
   + row factory), and the per-endpoint sink; wraps them in `PlPipeline<TRow>(descriptor.EndpointId,
   descriptor.Tier, provider, source, sink, loadLog, settings, logger)`. (Exactly the CWG `Add<…>` /
   `BuildPipeline<…>` shape, plus an archetype switch for the provider.)

### 1.4 `RunAsync` fan-out — tiered execution with barriers

Mirror `CwgModule.RunAsync` + `AgsiModule.RunAsync`, extended to three tiers:

1. Read `IHSPointLogicSettings`. **Fail fast** (log error — never the secret — return
   `LoaderRunResult.Failed`) if `ClientId` **or** `ClientSecret` is blank or still the `"SEE_DB"`
   placeholder. Unlike AGSI (where only the keyed endpoint needed the key), **every** IHSPointLogic
   call carries the Basic header, so both credentials are required for any run.
   **Implementation caveat (build-only, AGSI parity):** materializing `IOptions<IHSPointLogicSettings>`
   runs the shared `SeeDbSettingsResolver`, which **throws** if `core.Param(LoaderName='IHSPointLogic',
   ParamName='ClientId'|'ClientSecret')` is missing — so any run requires both `core.Param` rows to
   exist. This §1.4 check is the secondary guard catching a value left as the literal `SEE_DB`.
2. `enabled = HashSet(EnabledEndpoints, OrdinalIgnoreCase)`;
   `all = services.GetServices<IPlPipeline>()`;
   `pipelines = all.Where(p => enabled.Contains(p.EndpointId))`. Warn for any enabled id with no
   matching pipeline; if none enabled → warn, return `Success = true`.
3. **Run in tier order with a barrier between tiers.** `for tier in [0,1,2]:` run every enabled
   pipeline whose `Tier == tier` (endpoints **sequential within a tier**; each pipeline fans its work
   units out concurrently via `ParallelRunner` at `MaxConcurrentWorkUnits`, and the one shared
   rate-limited client bounds global RPS). **`await` all of a tier's pipelines before starting the
   next tier** — this is load-bearing: Tier 1's reference providers read tables Tier 0 wrote, and Tier
   2's read tables Tier 1 wrote (§3). (Tier-internal parallelism across endpoints is an available knob;
   sequential-within-tier keeps memory/log reasoning simple — open item §11.)
   - **Tier 0** — the 19 independent endpoints (9 lookups/dimensions + 10 snapshot facts): no
     path/query params. Refreshes `arm.Region`, `arm.State`, `arm.PointType`, `arm.Point`,
     `arm.PointMetadata`, … in place (renamed per §A.0).
   - **Tier 1** — the 3 discovery-fed lookups: `lookup_county/{StateId}` (needs `IPlStateProvider`),
     `lookup_facility/{PointTypeId}` (needs `IPlPointTypeProvider`), `lookup_subregion/{RegionId}`
     (needs `IPlRegionProvider`). Refreshes `arm.County`, `arm.Facility`, `arm.Subregion` (renamed per §A.0).
   - **Tier 2** — the 3 parametrized facts: `supplyDemand/region/{RegionId}` (needs `IPlRegionProvider`
     × reportDate window), `supplyDemand/region/{SubRegionId}` (needs `IPlSubregionProvider` + the
     SubRegionId→RegionId map × reportDate window), `volumeHistory/point?pointIds=` (needs
     `IPlPointProvider`, batched).
4. After all three tiers complete, run the **module-level post-load validation** (§10) scoped to the
   run's date window (observational).
5. Aggregate the per-pipeline `LoaderRunResult`s (sum totals; `Success = all succeeded`) exactly as
   CWG/AGSI do.

> **Enable-subset runs.** If an operator enables only Tier 2 endpoints, the reference providers read
> whatever the Tier-0/Tier-1 tables already hold (from a prior run). If a needed dimension is empty,
> the reference provider **fails fast** (§3) — a Tier-2-only first run with no prior discovery is a
> configuration error, surfaced loudly rather than silently loading nothing (AGSI storage-only posture).

---

## 2. The endpoint descriptor schema

```
record PlEndpointDescriptor(
    string             EndpointId,        // stable id: "Region" … "PointVolume"
    string             DisplayName,
    PlFamily           Family,            // P (cs/v1/pointlogic/*) | V (cs/v1/plview/retrieve/*)
    PlArchetype        Archetype,         // LatestLookup | GoForwardSnapshot | DiscoveryLookup | DiscoveryDatedFact | BatchedFact
    int                Tier,              // 0 | 1 | 2 (run order + barriers, §1.4)
    string             PathTemplate,      // relative, e.g. "cs/v1/pointlogic/lookup_region",
                                          //   "cs/v1/pointlogic/lookup_county/{id}",
                                          //   "cs/v1/pointlogic/supplyDemand/region/{id}"
    PlPathParam        PathParam,         // None | StateId | PointTypeId | RegionId | SubRegionId | PointBatch
    bool               UsesReportDate,    // append ?reportDate=yyyy-MM-dd (archetype D only)
    PlEnvelope         Envelope,          // FlatArray | Wrapper (informational — the reader auto-detects & pages BOTH)
    PlReportDateBasis  ReportDateBasis,   // None | PayloadCarried | StampUtcRunDate | ParamInjected
    string?            RefProvider,       // "Region" | "State" | "PointType" | "Subregion" | "Point" | null (which reference provider feeds C/D/E)
    string             TargetTable,       // arm.<Table>  (informational; sink is per-endpoint)
    string             TargetTvp,         // arm.<Table>Tvp
    string             TargetProc);       // arm.usp_BulkMerge<Table>
```

Derived at run time: `IsHot` (all archetypes except the settled zone of D are hot);
`RequestPath` per work unit is fully substituted by the provider (path param + `reportDate`/`pointIds`);
the reader appends `?pageIndex=N`.

### 2.1 The 25 concrete descriptors

| # | EndpointId | Fam | Tier | Arch | PathTemplate (relative) | PathParam | reportDate | Envelope | ReportDateBasis | Ref | Target table |
|---|-----------|:---:|:---:|:----:|-------------------------|-----------|:----------:|:--------:|-----------------|-----|--------------|
| 5 | Region | P | 0 | A | `cs/v1/pointlogic/lookup_region` | None | – | array | None | – | `arm.Region` |
| 6a | State | P | 0 | A | `cs/v1/pointlogic/lookup_state` | None | – | array | None | – | `arm.State` |
| 6b | PointStatus | P | 0 | A | `cs/v1/pointlogic/lookup_pointStatus` | None | – | array | None | – | `arm.PointStatus` |
| 6c | PointType | P | 0 | A | `cs/v1/pointlogic/lookup_pointType` | None | – | array | None | – | `arm.PointType` |
| 6d | PipelineNoticeCategory | P | 0 | A | `cs/v1/pointlogic/lookup_pipelineNoticeCategory` | None | – | array | None | – | `arm.PipelineNoticeCategory` |
| 6e | Pipeline | P | 0 | A | `cs/v1/pointlogic/lookup_pipeline` | None | – | array | None | – | `arm.Pipeline` |
| 7 | Point | P | 0 | A | `cs/v1/pointlogic/lookup_point` | None | – | **wrapper** (paged) | None | – | `arm.Point` |
| 12 | PointMetadata | V | 0 | A | `cs/v1/plview/retrieve/pointmetadata_withids` | None | – | array (paged ≥5) | None | – | `arm.PointMetadata` |
| 15 | PipelineNoticeSearch | V | 0 | A | `cs/v1/plview/retrieve/pipelinenotice_search` | None | – | array (paged) | PayloadCarried | – | `arm.PipelineNoticeSearch` |
| 8 | DemandForecastRegion | V | 0 | B | `cs/v1/plview/retrieve/demandforecast_region` | None | – | array | **StampUtcRunDate** | – | `arm.DemandForecastRegion` |
| 9 | DemandForecastUsLower48 | V | 0 | B | `cs/v1/plview/retrieve/demandforecast_uslower48` | None | – | array | **StampUtcRunDate** | – | `arm.DemandForecastUsLower48` |
| 10 | GasProductionProducingArea | V | 0 | B | `cs/v1/plview/retrieve/gasproduction_producingarea` | None | – | array | PayloadCarried | – | `arm.GasProductionProducingArea` |
| 11 | MarketBalancesUsLower48 | V | 0 | B | `cs/v1/plview/retrieve/marketbalances_uslower48` | None | – | array | PayloadCarried | – | `arm.MarketBalancesUsLower48` |
| 13 | ModeledDemandRegionType | V | 0 | B | `cs/v1/plview/retrieve/modeleddemand_region_type` | None | – | array | PayloadCarried | – | `arm.ModeledDemandRegionType` |
| 14 | PipelineFlowThroughput | V | 0 | B | `cs/v1/plview/retrieve/pipelineflow_throughputs` | None | – | array | PayloadCarried | – | `arm.PipelineFlowThroughput` |
| 16 | UsImportsExportsByPointsAggregate | V | 0 | B | `cs/v1/plview/retrieve/us_importsexportsby_points_aggregate` | None | – | array | PayloadCarried | – | `arm.UsImportsExportsByPointsAggregate` |
| 17 | UsSampleStorageFacility | V | 0 | B | `cs/v1/plview/retrieve/us_samplestorage_facility` | None | – | array (paged 2) | PayloadCarried | – | `arm.UsSampleStorageFacility` |
| 18 | StateFlowsThroughputAggregate | V | 0 | B | `cs/v1/plview/retrieve/stateflows_throughputaggregates` | None | – | array | PayloadCarried | – | `arm.StateFlowsThroughputAggregate` |
| 19 | SupplyAndDemand | P | 0 | B | `cs/v1/pointlogic/supplyDemand/marketsHistory` | None | – | **wrapper** | PayloadCarried | – | `arm.SupplyAndDemand` |
| 20 | County | P | 1 | C | `cs/v1/pointlogic/lookup_county/{id}` | StateId | – | array | None | State | `arm.County` |
| 21 | Facility | P | 1 | C | `cs/v1/pointlogic/lookup_facility/{id}` | PointTypeId | – | array | None | PointType | `arm.Facility` |
| 22 | Subregion | P | 1 | C | `cs/v1/pointlogic/lookup_subregion/{id}` | RegionId | – | array | None | Region | `arm.Subregion` |
| 23 | SupplyAndDemandByRegion | P | 2 | D | `cs/v1/pointlogic/supplyDemand/region/{id}` | RegionId | ✔ | **wrapper** | **ParamInjected** | Region | `arm.SupplyAndDemandByRegion` |
| 24 | SupplyAndDemandBySubRegion | P | 2 | D | `cs/v1/pointlogic/supplyDemand/region/{id}` | SubRegionId | ✔ | **wrapper** | **ParamInjected** | Subregion | `arm.SupplyAndDemandBySubRegion` |
| 25 | PointVolume | P | 2 | E | `cs/v1/pointlogic/volumeHistory/point` | PointBatch | – | **wrapper** | None | Point | `arm.PointVolume` |

TVP/proc names follow `arm.<Table>Tvp` / `arm.usp_BulkMerge<Table>` (informational; §7 fixes the
column contracts). Archetype legend (A LatestLookup, B GoForwardSnapshot, C DiscoveryLookup, D
DiscoveryDatedFact, E BatchedFact) is detailed in §4.

**Count:** Tier 0 = 19 (9 A + 10 B), Tier 1 = 3 (C), Tier 2 = 3 (2 D + 1 E) → **25**.

**Note the 3 verified quirks baked into the templates:** (a) `lookup_pipelineNoticeCategory` casing is
correct (#6d); (b) the SD-by-region path uses a **single** `cs/v1/`; (c) SD-by-subregion **reuses**
`supplyDemand/region/{id}` with a **sub-region** id (the distinct `/subregion/` path 404s), so #23 and
#24 share the same `PathTemplate` and differ only by `PathParam` (RegionId vs SubRegionId) and the
injected `RegionId` (path vs map).

### 2.2 Registry invariants (`PlDescriptors` static ctor, fail fast at startup — CWG precedent)

- Every `EndpointId` is unique; there are exactly 25 descriptors.
- `Archetype ∈ {LatestLookup, GoForwardSnapshot}` ⟹ `Tier == 0`, `PathParam == None`,
  `RefProvider == null`, `UsesReportDate == false`.
- `Archetype == DiscoveryLookup` ⟹ `Tier == 1`, `PathTemplate` contains `{id}`, `PathParam ∈
  {StateId, PointTypeId, RegionId}`, `RefProvider` set, `UsesReportDate == false`.
- `Archetype == DiscoveryDatedFact` ⟹ `Tier == 2`, `{id}` present, `PathParam ∈ {RegionId,
  SubRegionId}`, `UsesReportDate == true`, `RefProvider` set.
- `Archetype == BatchedFact` ⟹ `Tier == 2`, `PathParam == PointBatch`, `RefProvider == "Point"`.
- `ReportDateBasis == StampUtcRunDate` only on `DemandForecastRegion` / `DemandForecastUsLower48`.
- Any `PathTemplate` containing `{id}` ⟺ `PathParam != None`.

These are asserted once at first static access (mirroring `CwgDescriptors`' `RegionUnits`-length
check), so a mis-wired descriptor fails at startup rather than mis-building a request later.

---

## 3. Reference providers (5) — discovery hand-off Tier 0 → Tier 1 → Tier 2

Five load-once-per-run reference caches, each the AGSI `IAgsiCountryProvider` /
`SqlAgsiCountryProvider` pattern: registered **singleton** (= one instance per process = per run), the
first call executes an `arm.usp_Get<X>Ids` read proc against `settings.ConnectionString`, **caches** the
result behind a double-checked `SemaphoreSlim`, and serves the cache thereafter — so a whole tier reads
each list exactly once across all parallel work units. **Fail fast if empty** (throw
`InvalidOperationException`, "run the feeding tier first / check the discovery load"). The ids flow
**through the database**, not through an in-memory hand-off — each tier MERGEs its table, the next
tier's provider reads it back.

| Provider | Read proc | Returns | Feeds | Table read (refreshed in tier) |
|----------|-----------|---------|-------|--------------------------------|
| `IPlRegionProvider` | `arm.usp_GetRegionIds` | `RegionId INT` list | Subregion (T1, §22), SD-by-region (T2, §23) | `arm.Region` (T0) |
| `IPlStateProvider` | `arm.usp_GetStateIds` | `StateId INT` list | County (T1, §20) | `arm.State` (T0) |
| `IPlPointTypeProvider` | `arm.usp_GetPointTypeIds` | `PointTypeId INT` list | Facility (T1, §21) | `arm.PointType` (T0) |
| `IPlSubregionProvider` | `arm.usp_GetSubRegionIds` | `(SubRegionId INT, RegionId INT)` pairs | SD-by-subregion (T2, §24) | `arm.Subregion` (T1) |
| `IPlPointProvider` | `arm.usp_GetPointIds` | `PointId INT` list | PointVolume (T2, §25) batching | `arm.PointMetadata` (T0, `PointIsActive=1`) |

- **Lazy + sequenced correctly by the tier barriers (§1.4).** A provider's first call happens when its
  consuming pipeline's `GetWorkUnitsAsync` runs — which, per the barrier ordering, is **after** the
  feeding tier committed its MERGE. So each read sees the freshly-refreshed list within the same run.
- **`IPlSubregionProvider` exposes the SubRegionId → RegionId map.** SD-by-subregion (§24) needs the
  parent `RegionId` for its PK, but the response body does **not** carry it; the provider surfaces both
  the `SubRegionId` list (to enumerate work units) **and** a `Dictionary<int,int>` SubRegionId→RegionId
  (populated from `arm.Subregion`, which was written in Tier 1 with the injected path `RegionId`). The
  work-unit provider stamps that `RegionId` onto each unit (`unit.SecondaryId`), and the row factory
  copies it into every row.
- **`IPlPointProvider` scope (open item §11).** `arm.Point` holds ~25 116 point ids; batching all of
  them at 50/request is ~503 PointVolume requests/run. DATABASE_DEVELOPER/reviewer to confirm whether to
  batch **all** points, or scope to an active subset (e.g. `arm.PointMetadata` where `PointIsActive =
  1`, or a configured id list). The read proc is the single scoping seam — the loader batches whatever it
  returns.

Interface shape (all five identical, AGSI `IAgsiCountryProvider`):

```
interface IPlRegionProvider { Task<IReadOnlyList<int>> GetRegionIdsAsync(CancellationToken ct); }
// … State, PointType, Point analogous …
interface IPlSubregionProvider {
    Task<IReadOnlyList<int>> GetSubRegionIdsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<int,int>> GetSubRegionToRegionMapAsync(CancellationToken ct); // both from one cached read
}
```

**Why decouple via the DB** (AGSI rationale): the tiers stay truly independent closed units (no shared
mutable state beyond run order); a Tier-2-only re-run reuses a previously-loaded catalogue without
re-hitting the lookups; and it matches the platform's "refresh the stored list, then read the stored
list" discovery pattern. The only coupling is the **tier order** enforced in §1.4.

---

## 4. Work-unit archetypes (5) + keying / idempotency

There is **one** `PlWorkUnit : WorkUnit` class (CWG-style) carrying everything any archetype needs; the
archetype (a descriptor property) selects which provider fills it and how the resume key is built.

```
PlWorkUnit : WorkUnit
    EndpointId          // stable id (for logging / FileLog)
    RequestPath         // fully-substituted RELATIVE path incl. {id} + ?reportDate/?pointIds — NO host, NO pageIndex
    ParamId?            // int? — injected path param (StateId | PointTypeId | RegionId | SubRegionId); null for A/B/E
    SecondaryId?        // int? — SD-by-subregion's parent RegionId (from the map); null otherwise
    ReportDate?         // DateOnly? — D: the reportDate param; B-stamped: the run's UTC date; null otherwise
    BatchToken?         // string? — PointVolume batch label (e.g. "0001" or "689-738"); null otherwise
    Variant?            // string? — FileLog sub-slot (param-kind label / batch token); null for A/B
    RepresentativeDate? // DateOnly? — FileLog representative date (§6)
    KeyValue            // precomputed resume key
    Key => KeyValue
    DisplayName
```

`runDate` for every provider is the run's **UTC** calendar date
(`DateOnly.FromDateTime(context.StartedAtUtc)`) — the locked stamping basis. (Contrast CWG's US-Eastern
and AGSI's CET bases: PointLogic's `plview/retrieve/*` snapshots are current-as-of-request and the
report date is either payload-carried or stamped-UTC, so UTC is the correct, simplest basis here.)
`hot` is a 3-way switch on `HotZoneKeyStrategy` (§B.4, default `RunHour`): `RunHour →
StartedAtUtc:yyyyMMddHH` (default) | `RunDate → runDate:yyyyMMdd` | `RunId → context.RunId:N`.

### A — LatestLookup (Tier 0 dimensions: Region, State, PointStatus, PointType, PipelineNoticeCategory, Pipeline, Point, PointMetadata, PipelineNoticeSearch)

- **Provider:** `PlSnapshotWorkUnitProvider` emits **one hot unit per run**. `RequestPath` = the plain
  path (no param, no query). `ParamId/ReportDate/BatchToken/Variant = null`;
  `RepresentativeDate = null`.
- **Key:** `pl:{EndpointId}:run={hot}` — always hot, refreshed each run (RunDate cadence → one refresh
  per calendar day, skipped on a same-day re-run via `core.LoadLog`).
- **MERGE:** by the endpoint's **id PK** (`RegionId` / `StateId` / … / `PointId` / notice `Id`). A
  re-run overwrites each row in place; the catalogue **accretes** (new ids inserted, existing updated,
  ids dropped from the current API window are retained). No date participates.
- **Paged endpoints (Point, PointMetadata, PipelineNoticeSearch) are still one unit** — the reader pages
  internally (`pageIndex`), so a 40 000-row / 5-page pull is a single retryable work unit.
- *Note:* `PipelineNoticeSearch` is a fact-shaped snapshot with a natural single-column `Id` PK, so it
  behaves under A's mechanics (id-PK MERGE, one hot unit) even though its rows are notices, not a
  dimension. Its payload carries dates (`posteddate`/`effectivedate`/`enddate`) but they are **not** in
  the PK, so no date is injected/stamped.

### B — GoForwardSnapshot (Tier 0 facts: DemandForecast{Region,UsLower48}, GasProductionProducingArea, MarketBalancesUsLower48, ModeledDemandRegionType, PipelineFlowThroughput, UsImportsExportsByPointsAggregate, UsSampleStorageFacility, StateFlowsThroughputAggregate, SupplyAndDemand[marketsHistory])

- **Provider:** the same `PlSnapshotWorkUnitProvider`, emitting **one hot unit per run**, keyed by the
  run's UTC date. If `ReportDateBasis == StampUtcRunDate` it stamps `unit.ReportDate = runDate (UTC)`;
  otherwise `unit.ReportDate = null` (the report date is payload-carried). `RepresentativeDate =
  runDate` (the capture date → one audit hub row per capture day, §6).
- **Key:** `pl:{EndpointId}:{runDate:yyyyMMdd}:run={hot}` — hot, per-run-UTC-date.
- **The stamping nuance (state it precisely):** the **work-unit key** is per-run-UTC-date, so a same-day
  re-run is idempotent (skipped by `core.LoadLog`). The **MERGE key** is the full **row PK incl. the
  report/flow date** — stamped UTC for `demandforecast_*` (where the payload has no forecast/as-of
  date → `ForecastDate` = the run's UTC date), payload-carried otherwise. For the **stamped** pair the
  stamped `ForecastDate` equals the work-unit key date, so a given UTC day lands exactly one snapshot
  (idempotent same-day; the next day appends a new `ForecastDate` cohort → **history accrues one
  report/day**). For **payload-carried** endpoints the work-unit key is the run's UTC date (hot), while
  the MERGE keys on the payload's own dates — a same-day re-run is skipped; a later run pulls a fresh
  snapshot whose payload dates overlap prior loads (MERGE updates in place) and/or extend (new rows),
  so history accrues as the payload's dates advance.
- **Paged (UsSampleStorageFacility 2 pages) is still one unit** — reader pages internally.

### C — DiscoveryLookup (Tier 1: County/{StateId}, Facility/{PointTypeId}, Subregion/{RegionId})

- **Provider:** `PlDiscoveryLookupWorkUnitProvider`, given the descriptor's reference provider. Emits
  **one hot unit per parent id** (`ParamId = parentId`, `RequestPath` = path with `{id}` substituted,
  `Variant` = the param-kind label e.g. `"State"`, `RepresentativeDate = null`).
- **Key:** `pl:{EndpointId}:{ParamId}:run={hot}` — hot/refresh (the child sets change rarely but cheaply
  refreshed each run).
- **Injection:** the parent id is **not echoed in the body** — inject it from the path onto every row.
- **MERGE:** by the composite `(childId, parentId)` (`(CountyId, StateId)` / `(FacilityId,
  PointTypeId)` / `(SubRegionId, RegionId)`); FK on the parent id → the Tier-0 dimension.

### D — DiscoveryDatedFact (Tier 2: SupplyAndDemandByRegion, SupplyAndDemandBySubRegion)

- **Provider:** `PlDiscoveryDatedFactWorkUnitProvider`, given the reference provider, crosses **each
  parent id × each reportDate** in the trailing `DaysBack = 21` window (newest = runDate UTC), emitting
  **one unit per (parent id × reportDate)**. `ParamId = parentId`, `ReportDate = reportDate`,
  `RequestPath` = path with `{id}` + `?reportDate=yyyy-MM-dd`, `RepresentativeDate = reportDate`.
  For **SD-by-subregion** it also sets `SecondaryId = map[SubRegionId]` (the parent `RegionId`).
- **Two-zone resume key (StormVista/AGSI §3.3 semantics, borrowed — NOT the windowing orchestrator):**
  `ageDays = runDate.DayNumber − reportDate.DayNumber`. Settled (`ageDays > SettledAfterDays`) → **stable**
  key `pl:{EndpointId}:{ParamId}:{reportDate:yyyyMMdd}` (loaded once, then a cheap `core.LoadLog` skip,
  no HTTP). Hot (`ageDays ≤ SettledAfterDays`) → run-varying key `…:{reportDate:yyyyMMdd}:run={hot}`
  (re-pulled, idempotent MERGE catches revisions). Defaults `DaysBack=21 > SettledAfterDays=7`, so days
  8–21 settle and days 0–7 stay hot (CWG's non-empty-settled-zone posture).
- **Injection:** neither `RegionId`/`SubRegionId` nor `Date` is in the body — inject `ParamId` (path) +
  `Date` (the `reportDate` param); SD-by-subregion **also** injects `SecondaryId` → `RegionId` from the
  subregion→region map.
- **MERGE:** `(RegionId, Date, Product)` for SD-by-region; `(SubRegionId, RegionId, Date, Product)` for
  SD-by-subregion.

### E — BatchedFact (Tier 2: PointVolume)

- **Provider:** `PlBatchedFactWorkUnitProvider`, given `IPlPointProvider`. Chunks the point-id list into
  **≤ `PointVolumeBatchSize` (50)**-id batches, emits **one hot unit per batch**: `RequestPath =
  cs/v1/pointlogic/volumeHistory/point?pointIds={csv}`, `BatchToken` = a stable batch label (e.g. the
  zero-padded batch index or `firstId-lastId`), `Variant = BatchToken`, `RepresentativeDate = runDate`.
- **Key:** `pl:PointVolume:{BatchToken}:run={hot}` — hot per run (recent history is re-pulled and
  upserted idempotently). Since the batch composition is derived deterministically from the ordered
  point-id list, the `BatchToken` is stable across same-day runs (skippable), and shifts only when the
  point catalogue changes.
- **Mapping:** each `Data[]` row `{id, volume, date}` → one fact row; `id` = the `PointId`, so a
  multi-point batch splits cleanly per row (no unit-side attribution needed).
- **MERGE:** by `(PointId, Date)`; FK `PointId → arm.PointMetadata` (the active-scope source; §A.1).

> **Superseded for the incremental backfill by Section C.** PointVolume is now an *incremental*
> per-point fact: batches are grouped by the `arm.PointMetadata.MaxDateQueued` watermark and issue
> `&startDate=`, the batch token becomes `{startDate:yyyyMMdd}-{subIndex}`, and the merge proc advances
> the watermark. The one-hot-unit-per-batch shape, the `(PointId, Date)` MERGE, and the `RunHour` hot
> key are unchanged. See Section C.

---

## 5. Shared plumbing — Basic auth, the tolerant JSON pager, FileLog flow

Written **once**, reused by all 25 endpoints.

### 5.1 Basic-auth delegating handler (`PlBasicAuthHandler`)

- On **every** outgoing request, sets `request.Headers.Authorization = new
  AuthenticationHeaderValue("Basic", base64(ClientId + ":" + ClientSecret))` using the resolved
  `IHSPointLogicSettings` (both values from `core.Param`). Simpler than AGSI's per-request `x-key` and
  than any OAuth2 flow — **no token provider, no bearer, no scope, no token endpoint**.
- The base64 credential and the raw `ClientSecret` are **never logged**; combined with the client's
  `.RemoveAllLoggers()`, no request log line can leak the header. The base64 string is computed once and
  cached on the handler (or recomputed cheaply per request — either is fine; both credentials are
  effectively immutable for a run).
- Registered as a message handler on the named client **outer of the throttle, inner of retry** so it
  re-stamps on each retry attempt.

### 5.2 The tolerant JSON pager (`PlSourceReader<TRow> : ISourceReader<PlWorkUnit, TRow>`)

Implements `ISourceReader` **directly** (NOT `HttpJsonSourceReaderBase`, which calls
`EnsureSuccessStatusCode` + single-shot JSON deserialize and would throw on the 404/no-data cases and
cannot page). Constructed per endpoint with `(HttpClient["IHSPointLogic"], settings, fileLog, descriptor,
Func<JsonElement, PlWorkUnit, TRow?> rowFactory, logger)`. Per work unit:

1. **Build the absolute URI** = `{BaseUrl}/{unit.RequestPath}`. The Basic header is added by the handler.
   Log only the sanitized `unit.RequestPath` (no host, no secret).
2. **`file = new PlFileContext(EndpointId, ParamKey, Variant, RepresentativeDate, RequestPath)`** (§6).
3. **Page loop — the `pageIndex` base depends on the envelope shape (verified live 2026-08-19; NOT
   universal).** Append `&pageIndex=N` or `?pageIndex=N` (whether `RequestPath` already contains `?` — D/E
   carry a query, A/B/C do not). **Flat** responses (`plview/retrieve/*`) are **0-based**: page `0,1,2,…`
   until a page returns `< PageSize (10000)` rows or is empty. **Wrapper** responses (`PagingInfo`/`Data`;
   the `pointlogic` family) are **1-based** — `pageIndex=0` duplicates page 1 and the real pages are
   `1..page_count`; use the index-0 fetch as page 1, then fetch `pageIndex = 2 … page_count` (driving off
   `page_count`; do **not** fetch `pageIndex=1`, and do **not** use a 0-based `pageIndex >= page_count`
   stop — that dropped the last page). Per page:
   Per page:
   - `GET`. `httpStatus = (int)response.StatusCode`.
     - **404 or empty** → treat as "nothing here": stop paging; the unit becomes `NotAvailable` (no
       failure) — the CWG/AGSI 404-tolerant pattern.
     - **401** → **throw** (missing/wrong Basic header — there is no token to refresh; loud auth failure).
     - **403** → **throw** (entitlement gap; the unit fails, the run continues).
     - **429 / 5xx** after Polly retries exhausted → throw (unit fails, run continues).
     - **200** → parse the body.
   - **Envelope detection (handles both shapes):** parse with `JsonDocument`. If `RootElement.ValueKind
     == Array` → the page's data list is the root array (flat `plview`/most lookups). If `Object` and it
     has a `Data` property → the data list is `root.GetProperty("Data")` and `PagingInfo` (`page_size`,
     `page_count`, `total_record_count`) is read for logging/bounds (`lookup_point`, `supplyDemand/*`,
     `volumeHistory/point`, `marketsHistory`). **`.Clone()`** each data element into a persistent
     `List<JsonElement>` accumulator (the per-page `JsonDocument` is disposed before the next page).
   - **Flat:** stop when the page's element count `< PageSize` **or** `== 0`. **Wrapper:** fetch exactly
     `page_count` pages (index 0 = page 1, then `2..page_count`); a short/empty page and `MaxPagesSafety`
     stay as safety nets.
4. **Map** each accumulated element via `rowFactory(elem, unit)` → `TRow?`; drop nulls (classify sentinel
   vs invalid drops, CWG `sentinelPredicate` style: expected-null cells at Debug, genuine parse failures
   at Warning so data-quality regressions stay visible). The row factory reads business fields from the
   element (tolerant parsers, §5.3) and **injected/stamped** fields from `unit` (`ParamId`, `SecondaryId`,
   `ReportDate`).
5. **FileLog outcome** = `rows.Count == 0 ? "NotAvailable" : "Success"`; `fileLogId =
   fileLog.UpsertAsync(file, status, lastHttpStatus, RequestPath, rows.Count, ct)`; stamp `r.FileLogId =
   fileLogId` on every row; return them (or empty).
6. **Exceptions:** `catch (OperationCanceledException) → throw;` (no FileLog write on a spent token —
   `LoadLog` already records cancellation). `catch (Exception)` → best-effort `FileLog "Failed"` (with
   `CancellationToken.None`), then **rethrow** so `LoaderPipelineBase` records the `LoadLog` failure and
   the run continues to the next unit (fail-a-block-not-the-run).

### 5.3 Tolerant parsers (`PlParse.*`) — mixed formats verified in the API doc

Invariant culture throughout; a blank / `" "` / missing / unparseable value → **NULL for that column**,
never a row failure (assume any measure MAY arrive as a JSON string and parse defensively):

| Helper | Handles | Target |
|--------|---------|--------|
| `Decimal(elem)` | JSON number **or** string-number (`designcapacity "0.00000000"`, negative measures) | `decimal?` |
| `Int(elem)` | number or string (`facilitytypeid "1"`) | `int?` |
| `Long(elem)` | notice `id` (`635182`) | `long?` |
| `Bit(elem)` | JSON bool or `"true"/"false"` (`iscritical`, `pointisactive`) | `bool?` |
| `Date(elem)` | `yyyy-MM-dd` **and** `MM/dd/yyyy` (pipelineflow/stateflows `flowdate`) | `DateOnly?` |
| `DateTime2(elem)` | `yyyy-MM-dd HH:mm` (`gasproduction.reporteddate`) | `DateTime?` (`DATETIME2(0)`) |
| `DateTimeOffset(elem)` | `yyyy-MM-dd HH:mm:ss.fff -05:00` (notice `posteddate`/`effectivedate`/`enddate`) | `DateTimeOffset?` |
| `String(elem)` | trims; blank / `" "` → NULL (`drn`, `county`, `region`) | `string?` |

`Date` tries the two accepted formats in order; `String` normalizes the observed blank-string sentinels
to NULL.

---

## 6. FileLog hub identity (`arm.FileLog`, generalized across all 25 endpoints)

Reuse the CWG/AGSI `arm.FileLog` hub: **one row per request outcome** (`Success` / `NotAvailable` /
`Failed`), `FileLogId` stamped onto the rows it produced. Natural key generalizes CWG's
`(Endpoint, Region, Variant, RepresentativeDate)` — here the "Region" slot carries the **discovery
param id** (or PointVolume batch token), the "Variant" slot the param-kind label, and
`RepresentativeDate` the representative report/flow/capture date:

```
UNIQUE (Endpoint, ParamKey, Variant, RepresentativeDate)   -- SQL NULL-equality collapses the undated/no-param rows
```

`PlFileContext(string Endpoint, string? ParamKey, string? Variant, DateOnly? RepresentativeDate, string RequestPath)`.

| Archetype / endpoints | ParamKey | Variant | RepresentativeDate | Hub-row cardinality |
|-----------------------|----------|---------|--------------------|---------------------|
| **A** lookups/dimensions (Region, State, PointStatus, PointType, PipelineNoticeCategory, Pipeline, Point, PointMetadata, PipelineNoticeSearch) | NULL | NULL | **NULL** | one **stable** row/endpoint, upserted each run (refreshes `LastCheckedUtc`/`RowCount`) |
| **B** snapshot facts (§8–§11, §13–§14, §16–§19) | NULL | NULL | **run's UTC capture date** | one row per **capture day** → audits the daily go-forward accrual |
| **C** discovery lookups (County/{StateId}, Facility/{PointTypeId}, Subregion/{RegionId}) | the parent id (`"4520"`, `"1"`, `"26105"`) | param kind (`"State"`/`"PointType"`/`"Region"`) | NULL | one row per (endpoint, parent id) |
| **D** SD facts (SD-by-region/{RegionId}, SD-by-subregion/{SubRegionId}) | the parent id | `"Region"` / `"SubRegion"` | the **reportDate** | one row per (endpoint, parent id, reportDate) |
| **E** PointVolume | the **batch token** (`"0001"` / `"689-738"`) | `"Batch"` | run's UTC capture date | one row per (endpoint, batch, day) |

- `IPlFileLog.UpsertAsync(file, status, httpStatus, requestPath, rowCount, ct) → FileLogId`.
  `SqlPlFileLog` calls `arm.usp_UpsertFileLog` under
  `SqlWriteGate.AcquireAsync(KeyFor(ConnectionString, "arm.usp_UpsertFileLog"))` (one shared key across
  all 25 endpoints; a fast single-row upsert, cheap to serialize, deadlock-proof) with explicit-typed
  params, reading back the scalar `FileLogId` — the exact `SqlAgsiFileLog`/`SqlCwgFileLog` shape.
- Following CWG's resolved item 7, `Endpoint` / `ParamKey`(as a region-ish label) / `Status` **may** be
  normalized to seeded surrogate-Id lookups server-side (`arm.Endpoint`, `arm.Status`, and a generic
  label table for `ParamKey`), with `usp_UpsertFileLog` keeping its name-string signature and resolving
  names → Ids. Inline `VARCHAR + CHECK` is an acceptable build-only alternative — DATABASE_DEVELOPER's
  call (§11). Note the `ParamKey` slot here holds numeric ids / batch tokens, **not** geography strings,
  so if normalized it should be a generic label lookup rather than a CWG-style geography lookup.
  (After §A.0, `arm.Region` is this loader's region **dimension** — **not** a FileLog label table; the
  hub keeps `ParamKey`/`Variant` inline `VARCHAR` per the shipped `001`.)
- **No-data (empty / 404) → `NotAvailable`, no unit failure** — the target table simply gains no rows
  for that request.

---

## 7. Full-field mapping (all 25 endpoints)

The single contract for DATABASE_DEVELOPER + CODER. Types/nullability/PKs are the **verified** API-doc
values (`docs/apis/IHSPointLogic.md` §5–§25). Every row also carries `FileLogId` (provenance; stamped in
§5, UPDATEd on MERGE, **not** a merge-key column) and a table-default `ModifiedAtUtc`. **"K"** marks a
MERGE-key column; **[stamped]**/**[path]**/**[param]**/**[map]** mark injected/stamped columns. Each
table's **TVP column order** is `FileLogId` **first**, then the row in the order listed; the sink's
`BuildTable`, the `002` TVP, and the `003` merge proc `SELECT`/`INSERT` must mirror it identically (the
load-bearing `SqlSinkBase` contract). Each sink de-dups its batch on the MERGE key before building the
TVP (Platts/CWG/AGSI posture).

### TIER 0 — independent lookups (archetype A)

**§5 Region → `arm.Region`** — PK `RegionId`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `id` | RegionId | RegionId | INT | No | **K** |
| `name` | Name | Name | NVARCHAR(128) | No | |

TVP: `FileLogId, RegionId, Name`.

**§6a State → `arm.State`** · **§6b PointStatus → `arm.PointStatus`** · **§6c PointType →
`arm.PointType`** · **§6d PipelineNoticeCategory → `arm.PipelineNoticeCategory`** — identical
2-column shape: `{ <Id>Id(K, INT, `id`), Name(NVARCHAR(128), `name`) }`. PKs `StateId` / `PointStatusId`
/ `PointTypeId` / `PipelineNoticeCategoryId`. TVP each: `FileLogId, <Id>, Name`.

**§6e Pipeline → `arm.Pipeline`** — PK `PipelineId`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `id` | PipelineId | PipelineId | INT | No | **K** |
| `name` | Name | Name | NVARCHAR(200) | No | |
| `legacyname` | LegacyName | LegacyName | NVARCHAR(200) | Yes | |

TVP: `FileLogId, PipelineId, Name, LegacyName`.

**§7 Point → `arm.Point`** (WRAPPER, paged 3) — PK `PointId`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `id` | PointId | PointId | INT | No | **K** |
| `name` | Name | Name | NVARCHAR(200) | No | |
| `pipelineid_0` | PipelineId | PipelineId | INT | Yes | FK → arm.Pipeline (**note `_0` suffix**) |
| `pointtypeid` | PointTypeId | PointTypeId | INT | Yes | FK → arm.PointType |
| `pointstatusid` | PointStatusId | PointStatusId | INT | Yes | FK → arm.PointStatus |

TVP: `FileLogId, PointId, Name, PipelineId, PointTypeId, PointStatusId`.

**§12 PointMetadata → `arm.PointMetadata`** (flat, paged ≥5) — PK `PointId` (22 business cols)
| JSON | Row prop | Column | Type | Null | Notes |
|------|----------|--------|------|:----:|-------|
| `pointid` | PointId | PointId | INT | No | **K** |
| `pointlciid` | PointLciId | PointLciId | VARCHAR(16) | Yes | |
| `drn` | Drn | Drn | NVARCHAR(64) | Yes | blank `" "` → NULL |
| `pointname` | PointName | PointName | NVARCHAR(200) | No | |
| `pointtypeid` | PointTypeId | PointTypeId | INT | Yes | FK → arm.PointType |
| `pointtype` | PointType | PointType | NVARCHAR(64) | Yes | |
| `stateid` | StateId | StateId | INT | Yes | FK → arm.State |
| `state` | State | State | NVARCHAR(64) | Yes | |
| `county` | County | County | NVARCHAR(128) | Yes | blank → NULL |
| `region` | Region | Region | NVARCHAR(128) | Yes | blank → NULL |
| `pipelineid` | PipelineId | PipelineId | INT | Yes | FK → arm.Pipeline |
| `pipelinedisplayname` | PipelineDisplayName | PipelineDisplayName | NVARCHAR(200) | Yes | |
| `pointisactive` | PointIsActive | PointIsActive | BIT | Yes | |
| `designcapacity` | DesignCapacity | DesignCapacity | DECIMAL(18,6) | Yes | **JSON STRING** → parse |
| `flowdirectionid` | FlowDirectionId | FlowDirectionId | INT | Yes | |
| `flowdirection` | FlowDirection | FlowDirection | NVARCHAR(32) | Yes | |
| `displayname` | DisplayName | DisplayName | NVARCHAR(200) | Yes | |
| `locprop` | LocProp | LocProp | NVARCHAR(64) | Yes | |
| `pointlatitude` | PointLatitude | PointLatitude | DECIMAL(9,6) | Yes | |
| `pointlongitude` | PointLongitude | PointLongitude | DECIMAL(9,6) | Yes | |
| `countyid` | CountyId | CountyId | INT | Yes | FK → arm.County |
| `regionid` | RegionId | RegionId | INT | Yes | FK → arm.Region |

TVP: `FileLogId, PointId, PointLciId, Drn, PointName, PointTypeId, PointType, StateId, State, County,
Region, PipelineId, PipelineDisplayName, PointIsActive, DesignCapacity, FlowDirectionId, FlowDirection,
DisplayName, LocProp, PointLatitude, PointLongitude, CountyId, RegionId`.

**§15 PipelineNoticeSearch → `arm.PipelineNoticeSearch`** (flat, paged) — PK `Id`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `id` | Id | Id | BIGINT | No | **K** |
| `subject` | Subject | Subject | NVARCHAR(400) | Yes | |
| `format` | Format | Format | VARCHAR(16) | Yes | |
| `posteddate` | PostedDate | PostedDate | DATETIMEOFFSET(3) | Yes | |
| `externalid` | ExternalId | ExternalId | VARCHAR(32) | Yes | |
| `categoryid` | CategoryId | CategoryId | INT | Yes | FK → arm.PipelineNoticeCategory |
| `iscritical` | IsCritical | IsCritical | BIT | Yes | |
| `contentlink` | ContentLink | ContentLink | NVARCHAR(400) | Yes | |
| `pipelineid` | PipelineId | PipelineId | INT | Yes | FK → arm.Pipeline |
| `effectivedate` | EffectiveDate | EffectiveDate | DATETIMEOFFSET(3) | Yes | |
| `enddate` | EndDate | EndDate | DATETIMEOFFSET(3) | Yes | |

TVP: `FileLogId, Id, Subject, Format, PostedDate, ExternalId, CategoryId, IsCritical, ContentLink,
PipelineId, EffectiveDate, EndDate`.

### TIER 0 — snapshot facts (archetype B)

**§8 DemandForecastRegion → `arm.DemandForecastRegion`** — PK `(ForecastDate, Date, Region, Subregion)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| *[stamped UTC]* | ForecastDate | ForecastDate | DATE | No | **K** — run's UTC date |
| `date` | Date | Date | DATE | No | **K** |
| `region` | Region | Region | NVARCHAR(128) | No | **K** |
| `subregion` | Subregion | Subregion | NVARCHAR(128) | No | **K** |
| `departurefromnormalf` | DepartureFromNormalF | DepartureFromNormalF | DECIMAL(6,2) | Yes | signed |
| `normaltemperaturef` | NormalTemperatureF | NormalTemperatureF | DECIMAL(6,2) | Yes | |
| `totalconsumption` | TotalConsumption | TotalConsumption | DECIMAL(18,6) | Yes | |
| `power` | Power | Power | DECIMAL(18,6) | Yes | |
| `industrial` | Industrial | Industrial | DECIMAL(18,6) | Yes | |
| `rescom` | ResCom | ResCom | DECIMAL(18,6) | Yes | |
| `departurefromnormal` | DepartureFromNormal | DepartureFromNormal | DECIMAL(18,6) | Yes | signed |

TVP: `FileLogId, ForecastDate, Date, Region, Subregion, DepartureFromNormalF, NormalTemperatureF,
TotalConsumption, Power, Industrial, ResCom, DepartureFromNormal`.

**§9 DemandForecastUsLower48 → `arm.DemandForecastUsLower48`** — PK `(ForecastDate, Date, Region)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| *[stamped UTC]* | ForecastDate | ForecastDate | DATE | No | **K** — run's UTC date |
| `pointreadingaggregate_endingdate` | Date | Date | DATE | No | **K** — *long JSON name* |
| `regionname` | Region | Region | NVARCHAR(128) | No | **K** — *`regionname`* |
| `power` | Power | Power | DECIMAL(18,6) | Yes | |
| `industrial` | Industrial | Industrial | DECIMAL(18,6) | Yes | |
| `residentialcommercial` | ResidentialCommercial | ResidentialCommercial | DECIMAL(18,6) | Yes | |
| `subtotal` | Subtotal | Subtotal | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, ForecastDate, Date, Region, Power, Industrial, ResidentialCommercial, Subtotal`.

**§10 GasProductionProducingArea → `arm.GasProductionProducingArea`** — PK `(ReportedDate, ReferenceDate,
Region, ProducingArea, State)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `reporteddate` | ReportedDate | ReportedDate | DATETIME2(0) | No | **K** — `yyyy-MM-dd HH:mm` |
| `referencedate` | ReferenceDate | ReferenceDate | DATE | No | **K** |
| `region` | Region | Region | NVARCHAR(128) | No | **K** |
| `producingarea` | ProducingArea | ProducingArea | NVARCHAR(128) | No | **K** |
| `state` | State | State | NVARCHAR(64) | No | **K** — can be a NAME (`Gulf of Mexico`) |
| `dryfactoredvalue` | DryFactoredValue | DryFactoredValue | DECIMAL(18,6) | Yes | |
| `wellheadvalue` | WellheadValue | WellheadValue | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, ReportedDate, ReferenceDate, Region, ProducingArea, State, DryFactoredValue,
WellheadValue`. (Datetime-granularity key — open item §11.)

**§11 MarketBalancesUsLower48 → `arm.MarketBalancesUsLower48`** — PK `(TimePeriod)`
`TimePeriod (K, DATE, `timeperiod`)` + the 16 measures, all `DECIMAL(18,6)` Yes:
`Wellhead(`wellhead`), ProductionLoss(`productionloss`), DryGas(`drygas`), CanadaImports(`canadaimports`),
LngSendout(`lngsendout`), TotalSupply(`totalsupply`), Power(`power`), Industrial(`industrial`),
ResidentialCommercial(`residentialcommercial`), Subtotal(`subtotal`), MexicoExports(`mexicoexports`),
LngFeedGas(`lngfeedgas`), PipeLoss(`pipeloss`), TotalDemand(`totaldemand`), Storage(`storage`),
BalancingItem(`balancingitem`, signed)`.
TVP: `FileLogId, TimePeriod, Wellhead, ProductionLoss, DryGas, CanadaImports, LngSendout, TotalSupply,
Power, Industrial, ResidentialCommercial, Subtotal, MexicoExports, LngFeedGas, PipeLoss, TotalDemand,
Storage, BalancingItem`.

**§13 ModeledDemandRegionType → `arm.ModeledDemandRegionType`** — PK `(ReferenceDate, PLEProductName,
RegionName)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `referencedate` | ReferenceDate | ReferenceDate | DATE | No | **K** |
| `pleproductname` | PLEProductName | PLEProductName | NVARCHAR(128) | No | **K** |
| `regionname` | RegionName | RegionName | NVARCHAR(128) | No | **K** |
| `volume` | Volume | Volume | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, ReferenceDate, PLEProductName, RegionName, Volume`.

**§14 PipelineFlowThroughput → `arm.PipelineFlowThroughput`** — PK `(FlowDate, Region, Pipeline,
Throughput, FlowType)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `flowdate` | FlowDate | FlowDate | DATE | No | **K** — `MM/dd/yyyy` |
| `region` | Region | Region | NVARCHAR(128) | No | **K** |
| `pipeline` | Pipeline | Pipeline | NVARCHAR(200) | No | **K** |
| `throughput` | Throughput | Throughput | NVARCHAR(200) | No | **K** — a **LABEL** (`"CT to RI"`), NOT a measure |
| `flowtype` | FlowType | FlowType | NVARCHAR(32) | No | **K** — `Inflow`/`Outflow` |
| `volume` | Volume | Volume | DECIMAL(18,6) | Yes | the flow measure |
| ~~`reporteddate`~~ | — | — | — | — | **EXCLUDED — do NOT persist** |

TVP: `FileLogId, FlowDate, Region, Pipeline, Throughput, FlowType, Volume`.

**§16 UsImportsExportsByPointsAggregate → `arm.UsImportsExportsByPointsAggregate`** — PK `(RunDate,
FlowDate, PointName, PipelineName, LedgerSide)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `rundate` | RunDate | RunDate | DATE | No | **K** |
| `flowdate` | FlowDate | FlowDate | DATE | No | **K** |
| `pointname` | PointName | PointName | NVARCHAR(200) | No | **K** |
| `pipelinename` | PipelineName | PipelineName | NVARCHAR(200) | No | **K** |
| `ledgerside` | LedgerSide | LedgerSide | NVARCHAR(16) | No | **K** — `Receipt`/`Delivery` |
| `volume` | Volume | Volume | DECIMAL(18,6) | Yes | |
| `state` | State | State | VARCHAR(8) | Yes | |
| `county` | County | County | NVARCHAR(128) | Yes | |
| `type` | Type | Type | NVARCHAR(64) | Yes | |
| `pointgroupname` | PointGroupName | PointGroupName | NVARCHAR(128) | Yes | |
| `districtname` | DistrictName | DistrictName | NVARCHAR(128) | Yes | |

TVP: `FileLogId, RunDate, FlowDate, PointName, PipelineName, LedgerSide, Volume, State, County, Type,
PointGroupName, DistrictName`.

**§17 UsSampleStorageFacility → `arm.UsSampleStorageFacility`** (paged 2) — PK `(ReportDate, FlowDate,
Name, EiaRegion, State, FieldType)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `reportdate` | ReportDate | ReportDate | DATE | No | **K** — *`reportdate`* |
| `flowdate` | FlowDate | FlowDate | DATE | No | **K** |
| `name` | Name | Name | NVARCHAR(200) | No | **K** |
| `eiaregion` | EiaRegion | EiaRegion | NVARCHAR(64) | No | **K** |
| `state` | State | State | VARCHAR(4) | No | **K** — 2-char code |
| `field_type` | FieldType | FieldType | NVARCHAR(32) | No | **K** — *`field_type`* |
| `volume` | Volume | Volume | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, ReportDate, FlowDate, Name, EiaRegion, State, FieldType, Volume`.

**§18 StateFlowsThroughputAggregate → `arm.StateFlowsThroughputAggregate`** — PK `(FlowDate, Region,
FromState, ToState, FlowType)`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `flowdate` | FlowDate | FlowDate | DATE | No | **K** — `MM/dd/yyyy` |
| `region` | Region | Region | NVARCHAR(128) | No | **K** |
| `fromstate` | FromState | FromState | NVARCHAR(64) | No | **K** — a NAME |
| `tostate` | ToState | ToState | NVARCHAR(64) | No | **K** — a NAME |
| `flowtype` | FlowType | FlowType | NVARCHAR(32) | No | **K** |
| `volume` | Volume | Volume | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, FlowDate, Region, FromState, ToState, FlowType, Volume`.

**§19 SupplyAndDemand (marketsHistory) → `arm.SupplyAndDemand`** (WRAPPER, full history) — PK `(Date)`
`Date (K, DATE, `date`)` + the **same 16 measures** as §11 (identical lowercase JSON names / props /
`DECIMAL(18,6)` Yes). TVP: `FileLogId, Date, Wellhead, ProductionLoss, DryGas, CanadaImports,
LngSendout, TotalSupply, Power, Industrial, ResidentialCommercial, Subtotal, MexicoExports, LngFeedGas,
PipeLoss, TotalDemand, Storage, BalancingItem`. (Separate table from §11 — different key, different
endpoint/family.)

### TIER 1 — discovery-fed lookups (archetype C)

**§20 County → `arm.County`** — PK `(CountyId, StateId)`; FK `StateId → arm.State`
| JSON / source | Row prop | Column | Type | Null | Key |
|---------------|----------|--------|------|:----:|:---:|
| `id` | CountyId | CountyId | INT | No | **K** |
| *[path]* | StateId | StateId | INT | No | **K** — injected from the URL |
| `name` | Name | Name | NVARCHAR(128) | No | |

TVP: `FileLogId, CountyId, StateId, Name`.

**§21 Facility → `arm.Facility`** — PK `(FacilityId, PointTypeId)`; FK `PointTypeId → arm.PointType`
| JSON / source | Row prop | Column | Type | Null | Key |
|---------------|----------|--------|------|:----:|:---:|
| `id` | FacilityId | FacilityId | INT | No | **K** |
| *[path]* | PointTypeId | PointTypeId | INT | No | **K** — injected from the URL (= `facilitytypeid`) |
| `name` | Name | Name | NVARCHAR(200) | No | |
| `facilitytypeid` | FacilityTypeId | FacilityTypeId | INT | Yes | **JSON STRING** `"1"` → parse; redundant with the path id — **persist for audit or drop (open item §11)** |

TVP (if `FacilityTypeId` persisted): `FileLogId, FacilityId, PointTypeId, Name, FacilityTypeId`.

**§22 Subregion → `arm.Subregion`** — PK `(SubRegionId, RegionId)`; FK `RegionId → arm.Region`. Also the
**SubRegionId → RegionId map** for §24.
| JSON / source | Row prop | Column | Type | Null | Key |
|---------------|----------|--------|------|:----:|:---:|
| `id` | SubRegionId | SubRegionId | INT | No | **K** |
| *[path]* | RegionId | RegionId | INT | No | **K** — injected from the URL |
| `name` | Name | Name | NVARCHAR(128) | No | |

TVP: `FileLogId, SubRegionId, RegionId, Name`.

### TIER 2 — parametrized facts (archetypes D, E)

**§23 SupplyAndDemandByRegion → `arm.SupplyAndDemandByRegion`** (WRAPPER) — PK `(RegionId, Date,
Product)`; FK `RegionId → arm.Region`
| JSON / source | Row prop | Column | Type | Null | Key |
|---------------|----------|--------|------|:----:|:---:|
| *[path]* | RegionId | RegionId | INT | No | **K** — injected from the URL |
| *[`reportDate` param]* | Date | Date | DATE | No | **K** — injected from the query |
| `product` | Product | Product | NVARCHAR(128) | No | **K** |
| `volume_mmcfd` | VolumeMmcfd | VolumeMmcfd | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, RegionId, Date, Product, VolumeMmcfd`.

**§24 SupplyAndDemandBySubRegion → `arm.SupplyAndDemandBySubRegion`** (WRAPPER; reuses `/region/{id}`) —
PK `(SubRegionId, RegionId, Date, Product)`; FKs `SubRegionId,RegionId → arm.Subregion`, `RegionId →
arm.Region`
| JSON / source | Row prop | Column | Type | Null | Key |
|---------------|----------|--------|------|:----:|:---:|
| *[path]* | SubRegionId | SubRegionId | INT | No | **K** — injected from the URL |
| *[map]* | RegionId | RegionId | INT | No | **K** — resolved via the SubRegionId→RegionId map |
| *[`reportDate` param]* | Date | Date | DATE | No | **K** — injected from the query |
| `product` | Product | Product | NVARCHAR(128) | No | **K** |
| `volume_mmcfd` | VolumeMmcfd | VolumeMmcfd | DECIMAL(18,6) | Yes | |

TVP: `FileLogId, SubRegionId, RegionId, Date, Product, VolumeMmcfd`.

**§25 PointVolume → `arm.PointVolume`** (WRAPPER, batched) — PK `(PointId, Date)`; FK `PointId → arm.PointMetadata`
| JSON | Row prop | Column | Type | Null | Key |
|------|----------|--------|------|:----:|:---:|
| `id` | PointId | PointId | INT | No | **K** — the `PointId` (batch splits cleanly per row) |
| `date` | Date | Date | DATE | No | **K** |
| `volume` | Volume | Volume | DECIMAL(18,6) | Yes | can be **negative** |

TVP: `FileLogId, PointId, Date, Volume`.

---

## 8. Config surface — `IHSPointLogicSettings : LoaderSettingsBase`

Inherited: `ConnectionString` (the `IHSPointLogic` DB), `MaxConcurrentWorkUnits`, `RetryCount`,
`RetryDelayMs`, `WorkUnitTimeoutSeconds`. Added:

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `BaseUrl` | string | `https://api.connect.ihsmarkit.com` | API host (paths under `cs/v1/…`) |
| `ClientId` | string | `SEE_DB` | Basic-auth **username**; resolved from `core.Param`; **never logged** |
| `ClientSecret` | string | `SEE_DB` | Basic-auth **password**; resolved from `core.Param`; **never logged** |
| `HttpTimeoutSeconds` | int | 60 | per-request timeout on the shared client |
| `EnabledEndpoints` | string[] | all 25 EndpointIds | toggle; matched case-insensitively to `IPlPipeline.EndpointId` |
| `DaysBack` | int | 21 | reportDate window for the two SD-by-region/subregion endpoints (all others ignore it) |
| `SettledAfterDays` | int | 7 | two-zone settled/hot boundary for archetype D (§4); `< DaysBack` → non-empty settled tail |
| `HotZoneKeyStrategy` | `RunHour`\|`RunDate`\|`RunId` | `RunHour` | hot-zone + snapshot re-pull cadence; default flips to `RunHour` for the hourly schedule (§B.4) |
| `RequestsPerSecond` | double? | conservative (e.g. `2`) | global client-side throttle; null/≤0 = unlimited. Probe surfaced no limit — pace gently, back off on `429` |
| `PointVolumeBatchSize` | int | 50 | `pointIds` chunk size for PointVolume (§4 E) |
| `DefaultStartDateForPointVolume` | string (`yyyy-MM-dd`) | `"2020-01-01"` | `startDate` floor for any point whose `MaxDateQueued IS NULL` (PointVolume incremental backfill, Section C). **Not a secret — NOT `SEE_DB`.** |

(`PageSize = 10000` is a design constant in the reader, not a config field — the API fixes it; paging is
by `pageIndex` regardless — open item §11.)

`appsettings.json` `Loaders:IHSPointLogic` mirrors the CWG/AGSI block: `ConnectionString`
(`Server=…;Database=IHSPointLogic;Integrated Security=SSPI;TrustServerCertificate=True;`),
`ClientId:"SEE_DB"`, `ClientSecret:"SEE_DB"`, the tuning knobs above, `EnabledEndpoints`, `DaysBack:21`,
`SettledAfterDays:7`, `HotZoneKeyStrategy:"RunHour"` (§B.4), `RequestsPerSecond`, `PointVolumeBatchSize:50`,
`DefaultStartDateForPointVolume:"2020-01-01"` (Section C).
**Leave `"IHSPointLogic"` OUT of `Platform:EnabledLoaders`** (build-only pass — loader disabled by
default). Real credentials live in `core.Param(LoaderName='IHSPointLogic', ParamName='ClientId'|
'ClientSecret')` (or env `DATALOADER_Loaders__IHSPointLogic__ClientId` / `…__ClientSecret`), never in
the file. **Fail fast** at run start if either is still `"SEE_DB"` (§1.4) — both feed the Basic header
on every call.

---

## 9. Concurrency & idempotency

- **Sinks** derive from `SqlSinkBase<TRow>`, which auto-acquires `SqlWriteGate` keyed
  `{server}/{db}::{proc}`. The 25 fact/dimension procs are 25 distinct keys, so different endpoints
  never serialize against each other; two concurrent work units of the *same* endpoint (e.g. two
  SD-by-region reportDates, or two County StateIds) serialize on that one proc key (correct — prevents
  the parallel-MERGE deadlock / NOT-MATCHED insert-race). Endpoints run sequentially within a tier
  (§1.4), so only within-endpoint units contend.
- **FileLog** is a **direct** proc writer, so `SqlPlFileLog` acquires `SqlWriteGate` explicitly on
  `arm.usp_UpsertFileLog` (one shared key across all 25 endpoints; a fast single-row upsert). The 5
  `arm.usp_Get<X>Ids` reads and the validator's read are plain reads (no gate needed). `core.LoadLog` is
  intentionally not gated (keyed per work unit).
- **Idempotent MERGE.** Every proc MERGEs on the endpoint's **PK** (§7), never on `FileLogId`
  (provenance, UPDATEd on match). So snapshot re-pulls (A/B hot units), discovery refreshes (C), the
  hot zone of the dated facts (D), and PointVolume batches (E) all upsert in place — no duplicates, safe
  over-scheduling. Settled dated units (D, `ageDays > SettledAfterDays`) are cheap `core.LoadLog` skips.
- **Overlap guard** (`DataLoader:IHSPointLogic` app-lock) prevents two host processes running the loader
  at once; **`ParallelRunner`** bounds within-endpoint concurrency at `MaxConcurrentWorkUnits`; the one
  rate-limited client bounds global RPS.

---

## 10. Post-load validation (`IHSPointLogicLoadValidator`, coded now / not exercised live)

`LoaderPipelineBase` has no post-load hook and the module owns orchestration, so — following the
AGSI `AgsiLoadValidator` / StormVista precedent — a module-level `IHSPointLogicLoadValidator` runs in
`RunAsync` **after all three tiers complete**, scoped to the run's date window, calling a single
`arm.usp_ValidateLoad(@DateFrom, @DateTo)` that emits an anomaly report (`CheckName, Scope,
ExpectedCount, ActualCount, Detail`). **Observational** (logs warnings + counters; never fails the run —
a legitimately sparse day with many `NotAvailable`s must not fail an otherwise-good load). Recommended
checks:

- **Per-table counts** non-zero for the enabled endpoints (informational; a zero on a sparse fact is a
  warning, not a failure).
- **PK null/dup** checks per table (each `arm.<Table>` should have no NULL key column and no duplicate
  PK — expect 0).
- **Referential sanity:** the facts' foreign ids resolve against the dimensions — e.g. `Point`/
  `PointMetadata.PipelineId`, `PointTypeId`, `PointStatusId`, `RegionId`, `CountyId` present in the
  respective dimensions; `PipelineNoticeSearch.CategoryId → PipelineNoticeCategory`, `.PipelineId →
  Pipeline`; `PointVolume.PointId → PointMetadata`; SD facts' `RegionId`/`SubRegionId → Region`/
  `Subregion` (all renamed per §A.0). (Sparse/late dimension rows → warn, don't fail.)
- **Domain/status checks:** `LedgerSide ⊆ {Receipt, Delivery}`; `FlowType` in the observed small set;
  temperatures/percent-ish measures within sane ranges; `demandforecast_*` `ForecastDate` = the run's UTC
  date; SD facts' injected `Date` = the requested `reportDate`.

**Build-only:** written and unit-testable this pass but **not exercised against live data**; the deeper
reconciliation is the deferred `DATA_QUALITY_VALIDATOR` step.

---

## 11. Open items for DATABASE_DEVELOPER / reviewer

25 tables + one FileLog hub + 5 read procs + optional validator. Coordinate on:

1. **Table / TVP / proc naming** — the recommended `arm.*` names (per the API doc + §2.1/§7; the 11
   dimensions have the `Pl` prefix **stripped** per §A.0):
   dimensions `Region, State, PointStatus, PointType, PipelineNoticeCategory, Pipeline,
   Point, PointMetadata, County, Facility, Subregion`; facts `DemandForecastRegion,
   DemandForecastUsLower48, GasProductionProducingArea, MarketBalancesUsLower48, ModeledDemandRegionType,
   PipelineFlowThroughput, PipelineNoticeSearch, UsImportsExportsByPointsAggregate, UsSampleStorageFacility,
   StateFlowsThroughputAggregate, SupplyAndDemand, SupplyAndDemandByRegion, SupplyAndDemandBySubRegion,
   PointVolume`. Each with `arm.<Table>Tvp` + `arm.usp_BulkMerge<Table>` and `FileLogId` first (§7).
2. **DECIMAL sizing** (from the verified API doc): measures/volumes/flows → `DECIMAL(18,6)`; temperatures
   (`departurefromnormalf`, `normaltemperaturef`) → `DECIMAL(6,2)`; lat/lon → `DECIMAL(9,6)`. Ids →
   `INT`, **except** `pipelinenotice_search.id` → `BIGINT`. Bit flags (`iscritical`, `pointisactive`) →
   `BIT`. Confirm the widths.
3. **The 5 read procs** `arm.usp_GetRegionIds` / `…GetStateIds` / `…GetPointTypeIds` / `…GetSubRegionIds`
   (returns `(SubRegionId, RegionId)`) / `…GetPointIds` — DISTINCT, non-null ids ordered ascending, each
   reading its just-refreshed table (§3). Confirm the `Point`-vs-`PointMetadata` source and the
   **PointVolume scoping** (all ~25 116 points → ~503 batches/run, or an active subset).
4. **FKs for the composite discovery keys:** `County(StateId) → State`, `Facility(PointTypeId) →
   PointType`, `Subregion(RegionId) → Region` (renamed per §A.0), and the fact FKs listed in §10. Confirm whether to
   enforce FKs (safe once dimensions load first) or leave logical-only for the build-only pass.
5. **`gasproduction.reporteddate` key granularity** — carries `HH:mm`; modeled `DATETIME2(0)` and kept
   at datetime granularity in the PK (matches `ReportedDate`). Confirm datetime-vs-date keying is
   acceptable.
6. **Persist redundant `facilitytypeid`?** (§21) — it arrives as a JSON string equal to the path
   `PointTypeId`. Persist `FacilityTypeId` for audit or drop it — DB's call (affects the `Facility` TVP
   column list).
7. **`arm.FileLog` hub + `arm.usp_UpsertFileLog`** — natural key `(Endpoint, ParamKey, Variant,
   RepresentativeDate)` with SQL NULL-equality (§6). Confirm: (a) normalize `Endpoint`/`Status`
   (and optionally `ParamKey`) to seeded surrogate-Id lookups (CWG resolved-item-7) vs inline
   `VARCHAR + CHECK` for the build-only pass; note the `ParamKey` slot holds **numeric ids / batch
   tokens**, not geography strings, so a generic label lookup fits better than the geography-style
   `arm.Region`; (b) column widths for `ParamKey`/`Variant`.
8. **`SupplyAndDemand` (§19) vs `MarketBalancesUsLower48` (§11)** share the identical 16-measure shape
   but are **separate tables** (different keys — `Date` vs `TimePeriod` — and different endpoints/
   families). Confirm two tables (recommended) rather than a union.
9. **`PageSize = 10000`** is a reader constant; the probe saw it fixed but couldn't confirm it is
   client-adjustable (cosmetic — the loader pages by `pageIndex` regardless). Confirm no need to expose
   it as config.
10. **`volumeHistory/point` date-range** — RESOLVED: `startDate` (`yyyy-MM-dd`, `date >= startDate`) is
    confirmed (API doc §25) and is now the basis of the **PointVolume incremental backfill in Section C**
    (per-point `arm.PointMetadata.MaxDateQueued` watermark, group-by-watermark batching, write-back in
    `arm.usp_BulkMergePointVolume`, new `DefaultStartDateForPointVolume` setting). See Section C for the
    DATABASE_DEVELOPER + CODER contract. (`endDate` remains unconfirmed and unused.)
11. **Rate limit / 429** — set a conservative `RequestsPerSecond` default and confirm `429` back-off /
    `Retry-After` in the Polly policy (none surfaced in the probe).
12. **`arm.usp_ValidateLoad`** (§10) — the per-table counts, PK null/dup, referential sanity, and
    status/domain checks for the run window (coded now, run later).
13. **Both `ClientId` and `ClientSecret` are required for any run** (Basic header on every call — no
    per-endpoint key exemption like AGSI). Confirm the fail-fast wording and that both `core.Param` rows
    must exist for a run (the `SEE_DB` resolver throws on a missing row when `IOptions` is materialized).

---

## Coverage checklist — all 25 endpoints

| # | Endpoint | Fam | Tier | Arch | Work-unit / key rule (§4) | Envelope / paging | Target table (PK) | FileLog identity (§6) | Fields |
|---|----------|:---:|:---:|:----:|---------------------------|-------------------|-------------------|-----------------------|:------:|
| 5 | Region | P | 0 | A | one hot unit/run; `run={hot}` | array | `Region` (RegionId) | Endpoint / NULL / NULL / NULL | ✔ 2 |
| 6a | State | P | 0 | A | one hot unit/run | array | `State` (StateId) | Endpoint (nulls) | ✔ 2 |
| 6b | PointStatus | P | 0 | A | one hot unit/run | array | `PointStatus` (PointStatusId) | Endpoint (nulls) | ✔ 2 |
| 6c | PointType | P | 0 | A | one hot unit/run | array | `PointType` (PointTypeId) | Endpoint (nulls) | ✔ 2 |
| 6d | PipelineNoticeCategory | P | 0 | A | one hot unit/run | array | `PipelineNoticeCategory` (Id) | Endpoint (nulls) | ✔ 2 |
| 6e | Pipeline | P | 0 | A | one hot unit/run | array | `Pipeline` (PipelineId) | Endpoint (nulls) | ✔ 3 |
| 7 | Point | P | 0 | A | one hot unit/run; reader pages | wrapper (3 pg) | `Point` (PointId) | Endpoint (nulls) | ✔ 5 |
| 12 | PointMetadata | V | 0 | A | one hot unit/run; reader pages | array (≥5 pg) | `PointMetadata` (PointId) | Endpoint (nulls) | ✔ 22 |
| 15 | PipelineNoticeSearch | V | 0 | A | one hot unit/run; reader pages | array (paged) | `PipelineNoticeSearch` (Id) | Endpoint (nulls) | ✔ 11 |
| 8 | DemandForecastRegion | V | 0 | B | one hot unit/run; **stamp ForecastDate=UTC** | array | `DemandForecastRegion` (ForecastDate,Date,Region,Subregion) | Endpoint / NULL / NULL / capture-date | ✔ 11 |
| 9 | DemandForecastUsLower48 | V | 0 | B | one hot unit/run; **stamp ForecastDate=UTC** | array | `DemandForecastUsLower48` (ForecastDate,Date,Region) | Endpoint / capture-date | ✔ 7 |
| 10 | GasProductionProducingArea | V | 0 | B | one hot unit/run; payload dates | array | `GasProductionProducingArea` (ReportedDate,ReferenceDate,Region,ProducingArea,State) | Endpoint / capture-date | ✔ 7 |
| 11 | MarketBalancesUsLower48 | V | 0 | B | one hot unit/run; payload TimePeriod | array | `MarketBalancesUsLower48` (TimePeriod) | Endpoint / capture-date | ✔ 17 |
| 13 | ModeledDemandRegionType | V | 0 | B | one hot unit/run; payload dates | array | `ModeledDemandRegionType` (ReferenceDate,PLEProductName,RegionName) | Endpoint / capture-date | ✔ 4 |
| 14 | PipelineFlowThroughput | V | 0 | B | one hot unit/run; payload flowdate; `reporteddate` EXCLUDED | array | `PipelineFlowThroughput` (FlowDate,Region,Pipeline,Throughput,FlowType) | Endpoint / capture-date | ✔ 6 |
| 16 | UsImportsExportsByPointsAggregate | V | 0 | B | one hot unit/run; payload rundate/flowdate | array | `UsImportsExportsByPointsAggregate` (RunDate,FlowDate,PointName,PipelineName,LedgerSide) | Endpoint / capture-date | ✔ 11 |
| 17 | UsSampleStorageFacility | V | 0 | B | one hot unit/run; reader pages; payload reportdate | array (2 pg) | `UsSampleStorageFacility` (ReportDate,FlowDate,Name,EiaRegion,State,FieldType) | Endpoint / capture-date | ✔ 7 |
| 18 | StateFlowsThroughputAggregate | V | 0 | B | one hot unit/run; payload flowdate | array | `StateFlowsThroughputAggregate` (FlowDate,Region,FromState,ToState,FlowType) | Endpoint / capture-date | ✔ 6 |
| 19 | SupplyAndDemand | P | 0 | B | one hot unit/run; full history | wrapper | `SupplyAndDemand` (Date) | Endpoint / capture-date | ✔ 17 |
| 20 | County | P | 1 | C | one hot unit per **StateId**; inject StateId | array | `County` (CountyId,StateId) | Endpoint / StateId / "State" / NULL | ✔ 3 |
| 21 | Facility | P | 1 | C | one hot unit per **PointTypeId**; inject PointTypeId | array | `Facility` (FacilityId,PointTypeId) | Endpoint / PointTypeId / "PointType" / NULL | ✔ 4 |
| 22 | Subregion | P | 1 | C | one hot unit per **RegionId**; inject RegionId; feeds map | array | `Subregion` (SubRegionId,RegionId) | Endpoint / RegionId / "Region" / NULL | ✔ 3 |
| 23 | SupplyAndDemandByRegion | P | 2 | D | unit per (**RegionId** × reportDate), two-zone; inject RegionId+Date | wrapper | `SupplyAndDemandByRegion` (RegionId,Date,Product) | Endpoint / RegionId / "Region" / reportDate | ✔ 4 |
| 24 | SupplyAndDemandBySubRegion | P | 2 | D | unit per (**SubRegionId** × reportDate), two-zone; inject SubRegionId+RegionId[map]+Date | wrapper | `SupplyAndDemandBySubRegion` (SubRegionId,RegionId,Date,Product) | Endpoint / SubRegionId / "SubRegion" / reportDate | ✔ 5 |
| 25 | PointVolume | P | 2 | E | one hot unit per **≤50-id batch** | wrapper | `PointVolume` (PointId,Date) | Endpoint / batch-token / "Batch" / capture-date | ✔ 3 |

All 25 endpoints have a descriptor (§2.1), an archetype + work-unit/keying rule (§4), a
both-envelope/paging plan (§5), a FileLog identity (§6), and a complete live-verified column mapping
with per-field SQL type + nullability + PK + injected/stamped columns (§7). Ready for
DATABASE_DEVELOPER (25 tables + TVPs + merge procs + `arm.FileLog`/`arm.usp_UpsertFileLog` + 5
`arm.usp_Get*Ids` read procs + optional `arm.usp_ValidateLoad` [+ optional seeded lookups]) and CODER
(module + 25 closed pipelines + 4 provider classes + 5 reference providers + Basic-auth handler + one
shared tolerant JSON pager + `PlParse` + FileLog writer + 25 rows/factories/sinks + validator; loader
left **disabled** in `Platform:EnabledLoaders`).

---

# Follow-up changes (post-build) — review checkpoint

Two independent follow-up changes to the already-built loader, kept **clearly separated** so the
review stays clean. **No code / no SQL bodies here** — object names + behavior are proposed to
DATABASE_DEVELOPER and CODER; this is the user's review checkpoint before any SQL/C# is written.
Names below were reconciled against the shipped `sql/IHSPointLogic/001–003+999` and
`src/DataLoader.IHSPointLogic/*` so they match reality.

## Section A — Dimension/lookup table rename cascade (strip the `Pl` prefix)

**What changes:** the 11 dimension/lookup **tables** (and their TVP / merge-proc / row / sink
identifiers) lose the `Pl` prefix. The 14 fact tables, the hub `arm.FileLog`, and the `arm.Endpoint`
/ `arm.Status` lookups do **not** change. This section is the single authoritative contract; any
remaining `arm.Pl*` / `Pl*Row` / `Pl*SqlSink` mention of one of these 11 elsewhere in this document
is superseded by the mapping in A.0.

### A.0 The rename map (the 11)

| # | Old table | New table | =EndpointId | TVP `arm.<X>Tvp` | Merge proc `arm.usp_BulkMerge<X>` | Row `<X>Row` | Sink `<X>SqlSink` |
|---|-----------|-----------|:-----------:|------------------|-----------------------------------|--------------|--------------------|
| 1 | `arm.PlRegion` | `arm.Region` | Region | `PlRegionTvp`→`RegionTvp` | `…PlRegion`→`…Region` | `PlRegionRow`→`RegionRow` | `PlRegionSqlSink`→`RegionSqlSink` |
| 2 | `arm.PlState` | `arm.State` | State | `PlStateTvp`→`StateTvp` | `…PlState`→`…State` | `PlStateRow`→`StateRow` | `PlStateSqlSink`→`StateSqlSink` |
| 3 | `arm.PlPointStatus` | `arm.PointStatus` | PointStatus | `…`→`PointStatusTvp` | `…PlPointStatus`→`…PointStatus` | `→PointStatusRow` | `→PointStatusSqlSink` |
| 4 | `arm.PlPointType` | `arm.PointType` | PointType | `→PointTypeTvp` | `…PlPointType`→`…PointType` | `→PointTypeRow` | `→PointTypeSqlSink` |
| 5 | `arm.PlPipelineNoticeCategory` | `arm.PipelineNoticeCategory` | PipelineNoticeCategory | `→PipelineNoticeCategoryTvp` | `…PlPipelineNoticeCategory`→`…PipelineNoticeCategory` | `→PipelineNoticeCategoryRow` | `→PipelineNoticeCategorySqlSink` |
| 6 | `arm.PlPipeline` | `arm.Pipeline` | Pipeline | `→PipelineTvp` | `…PlPipeline`→`…Pipeline` | `→PipelineRow` | `→PipelineSqlSink` |
| 7 | `arm.PlPoint` | `arm.Point` | Point | `→PointTvp` | `…PlPoint`→`…Point` | `→PointRow` | `→PointSqlSink` |
| 8 | `arm.PlPointMetadata` | `arm.PointMetadata` | PointMetadata | `→PointMetadataTvp` | `…PlPointMetadata`→`…PointMetadata` | `→PointMetadataRow` | `→PointMetadataSqlSink` |
| 9 | `arm.PlCounty` | `arm.County` | County | `→CountyTvp` | `…PlCounty`→`…County` | `→CountyRow` | `→CountySqlSink` |
| 10 | `arm.PlFacility` | `arm.Facility` | Facility | `→FacilityTvp` | `…PlFacility`→`…Facility` | `→FacilityRow` | `→FacilitySqlSink` |
| 11 | `arm.PlSubregion` | `arm.Subregion` | Subregion | `→SubregionTvp` | `…PlSubregion`→`…Subregion` | `→SubregionRow` | `→SubregionSqlSink` |

After the rename the **table name equals the EndpointId** for all 11 (intended). **Renaming the sink
classes too is a DECISION taken here** (keeps row/sink/table naming aligned).

**Collision check — CONFIRMED no collision.** None of the 11 new names collide with the 14 fact
tables, the 3 hub/lookup tables, any TVP, any proc, any row/sink type, or each other. Nearest
lexical neighbours are all distinct: `Pipeline` vs `PipelineFlowThroughput`/`PipelineNoticeSearch`/
`PipelineNoticeCategory`; `Point` vs `PointMetadata`/`PointStatus`/`PointType`/`PointVolume`;
`Region`/`State` (tables) vs the `Region`/`[State]` **columns** on the facts (different namespace —
no clash). `arm` is loader-local to the `IHSPointLogic` DB, so no cross-loader clash. The renamed
merge procs `arm.usp_BulkMergePoint` / `…Pipeline` / `…PointType` are distinct from the fact procs
`…PointVolume` / `…PipelineFlowThroughput` / `…PipelineNoticeSearch`. DATABASE_DEVELOPER to re-confirm
at build.

### A.1 Downstream cascade — the single contract

**SQL (`sql/IHSPointLogic/`)**

- **`001`** — for each of the 11: the `CREATE TABLE arm.Pl<X>` name and its `IF OBJECT_ID('arm.Pl<X>')`
  guard; **every constraint name that embeds the old table name** — `PK_Pl<X>`→`PK_<X>`,
  `DF_Pl<X>_DateCreated`/`DF_Pl<X>_ModifiedAtUtc`→`DF_<X>_*`, `FK_Pl<X>_FileLog`→`FK_<X>_FileLog`, the
  Tier-1 parent FKs `FK_PlCounty_State`→`FK_County_State`, `FK_PlFacility_PointType`→`FK_Facility_PointType`,
  `FK_PlSubregion_Region`→`FK_Subregion_Region`, and the per-table index `IX_Pl<X>_FileLogId`→`IX_<X>_FileLogId`;
  and the `-- §n arm.Pl<X>` header comments.
- **`001` FK *targets*** — repoint every FK clause that REFERENCES a renamed table:
  `FK_County_State … REFERENCES arm.State`, `FK_Facility_PointType … REFERENCES arm.PointType`,
  `FK_Subregion_Region … REFERENCES arm.Region`; and the Tier-2 fact FKs whose targets are renamed
  (**the fact tables' own names / constraint names do NOT change — only the target clause**):
  `FK_SupplyAndDemandByRegion_Region → arm.Region`, `FK_SupplyAndDemandBySubRegion_Subregion →
  arm.Subregion (SubRegionId, RegionId)`, `FK_SupplyAndDemandBySubRegion_Region → arm.Region`,
  `FK_PointVolume_PointMetadata → arm.PointMetadata (PointId)`.
- **`002`** — the 11 TVP types `arm.Pl<X>Tvp`→`arm.<X>Tvp` (names + `IF TYPE_ID` guards; column lists
  unchanged). The 14 fact TVPs unchanged.
- **`003`** — (a) the 11 merge procs `arm.usp_BulkMergePl<X>`→`arm.usp_BulkMerge<X>` — **name AND body**
  (the `MERGE arm.Pl<X>` target, the `@Records arm.Pl<X>Tvp READONLY` param type, the comment header);
  (b) the **5 read-proc bodies** — repoint the `FROM` table only, **names unchanged**:
  `usp_GetRegionIds` FROM `arm.Region`, `usp_GetStateIds` FROM `arm.State`, `usp_GetPointTypeIds` FROM
  `arm.PointType`, `usp_GetSubRegionIds` FROM `arm.Subregion`, and **`usp_GetPointIds` FROM
  `arm.PointMetadata WHERE PointIsActive = 1`** (the active-scope source — note it reads
  PointMetadata, not Point); (c) **`arm.usp_ValidateLoad` body** — it references ~15 tables and emits
  `RowCount_*` / `Orphan_*` **check-name string labels**: repoint the 11 renamed tables in every
  `FROM`/`NOT EXISTS` clause (incl. `Orphan_PlPoint_*`, `Orphan_PlPointMetadata_CountyId`, and the
  `PointVolume_ActiveScope` PointMetadata ref) **and** rename the emitted labels
  `RowCount_Pl<X>`→`RowCount_<X>`, `Orphan_PlPoint_*`→`Orphan_Point_*`,
  `Orphan_PlPointMetadata_CountyId`→`Orphan_PointMetadata_CountyId` (DECISION: rename the labels too —
  they are human-facing; the C# validator logs them generically, so no C# change is needed). The
  `arm.usp_UpsertFileLog` name/body is unaffected.
- **`999`** — rename the 11 `DROP TABLE arm.Pl<X>` statements + guards, the 11 `DROP TYPE arm.Pl<X>Tvp`,
  and the 11 `DROP PROCEDURE arm.usp_BulkMergePl<X>`. The read-proc drops, the validator drop, and the
  `usp_UpsertFileLog` drop are unchanged.

**C# (`src/DataLoader.IHSPointLogic/`)**

- **`Rows.cs`** — the 11 row types `Pl<X>Row`→`<X>Row` (and their `From(elem, unit)` factories).
- **`Sinks.cs`** — **DECISION: rename the 11 sink classes** `Pl<X>SqlSink`→`<X>SqlSink`; per sink also
  update the base generic arg `PlSqlSinkBase<Pl<X>Row>`→`PlSqlSinkBase<<X>Row>`, the ctor
  `ILogger<Pl<X>SqlSink>`, and the two string members
  `StoredProcedureName "arm.usp_BulkMergePl<X>"`→`"arm.usp_BulkMerge<X>"` and
  `TableValuedParameterType "arm.Pl<X>Tvp"`→`"arm.<X>Tvp"`. (`PlSqlSinkBase<TRow>` — the shared base —
  KEEPS its `Pl` name; it is plumbing, not one of the 11 — see A.2.)
- **`IHSPointLogicModule.cs`** — the 11 `Add(...)` registration lines: `Pl<X>Row.From`→`<X>Row.From`,
  `new Pl<X>SqlSink(…)`→`new <X>SqlSink(…)`, `Log<Pl<X>SqlSink>`→`Log<<X>SqlSink>`.
- **`PlDescriptors.cs`** — for the 11 descriptors, the `TargetTable`/`TargetTvp`/`TargetProc` **string
  args** only: `"arm.PlRegion","arm.PlRegionTvp","arm.usp_BulkMergePlRegion"` →
  `"arm.Region","arm.RegionTvp","arm.usp_BulkMergeRegion"`, and likewise for the other 10. The static
  **field names stay** (`PlDescriptors.Region`, `.State`, … are already unprefixed) and the
  `EndpointId` string values are already unprefixed — no change. The descriptors' `RefProvider`
  selector strings (`"Region"`/`"State"`/`"PointType"`/`"Subregion"`/`"Point"`) are provider keys, not
  table names — unchanged.

### A.2 The "do NOT over-rename" boundary (explicit)

Only the 11 dimension **table / TVP / proc / row / sink** identifiers change. All shared plumbing
keeps its `Pl` prefix:

- `PlSourceReader<TRow>`, `PlPipeline<TRow>` / `IPlPipeline`, `PlWorkUnit`, `PlEndpointDescriptor`,
  `PlDescriptors` (incl. its already-unprefixed static fields `PlDescriptors.Region` etc.),
  `PlSqlSinkBase<TRow>`, `IPlFactRow`.
- The 4 work-unit providers: `PlSnapshotWorkUnitProvider`, `PlDiscoveryLookupWorkUnitProvider`,
  `PlDiscoveryDatedFactWorkUnitProvider`, `PlBatchedFactWorkUnitProvider`.
- The 5 reference providers: `IPlRegionProvider`/`SqlPlRegionProvider`, `IPlStateProvider`/
  `SqlPlStateProvider`, `IPlPointTypeProvider`/`SqlPlPointTypeProvider`, `IPlSubregionProvider`/
  `SqlPlSubregionProvider`, `IPlPointProvider`/`SqlPlPointProvider` (+ the `SqlPlIntIdProvider` base).
- `PlParse`, `PlBasicAuthHandler`, `PlFileContext`/`IPlFileLog`/`SqlPlFileLog`, `PlRateLimiter`/
  `PlRateLimitingHandler`/`PlHttpPolicy`, all enums (`PlFamily`, `PlArchetype`, `PlPathParam`,
  `PlReportDateBasis`, `PlEnvelope`, `PlHotKeyStrategy`).
- **The 5 read procs KEEP their names** (`arm.usp_GetRegionIds`, `arm.usp_GetStateIds`,
  `arm.usp_GetPointTypeIds`, `arm.usp_GetSubRegionIds`, `arm.usp_GetPointIds`) — only their
  `FROM`-table refs change (A.1). `arm.usp_UpsertFileLog` / `arm.usp_ValidateLoad` names are unchanged
  (only ValidateLoad's body/labels change).

### A.3 Deployment note

The user already created the empty `Pl*` tables. `001`'s `IF OBJECT_ID(...) IS NULL` guards mean a
renamed re-run would CREATE the new-named tables and leave the old empties **orphaned**. So:

1. Run the **current (pre-rename) `999`** teardown to drop the empty `Pl*` tables/TVPs/procs (they
   hold no data, so the destructive teardown is safe here).
2. Apply the **renamed** `001 → 002 → 003`.

(This same teardown also gives Section B a clean `arm.Endpoint`, so B's idempotent `ALTER TABLE … ADD
RunHoursCST` is belt-and-suspenders for an env where `999` was not run.)

---

## Section B — Per-endpoint hourly schedule (`arm.Endpoint.RunHoursCST`) + hour-granular hot key

**What changes:** the host is scheduled **hourly**; each enabled endpoint runs only on the **US
Central (CST/CDT)** hours it is tuned for (a new `arm.Endpoint.RunHoursCST` column), and the hot
resume-key suffix gains
**hour** granularity so a scheduled hourly endpoint re-pulls each hour while a same-hour re-run still
idempotently skips.

### B.1 Schedule model — `arm.Endpoint.RunHoursCST`

- **New column** `arm.Endpoint.RunHoursCST VARCHAR(80) NOT NULL CONSTRAINT DF_Endpoint_RunHoursCST
  DEFAULT '*'`. Added **idempotently** in `001` (guard: `IF COL_LENGTH('arm.Endpoint','RunHoursCST')
  IS NULL ALTER TABLE arm.Endpoint ADD …`), so an already-seeded `arm.Endpoint` gains it without a
  rebuild.
- **Semantics.** `'*'` = run every hour. Otherwise a CSV of **US Central** hours 0–23, e.g. `'6'`,
  `'6,18'`, `'0,6,12,18'`. Hours are interpreted **DST-aware** (CST in winter, CDT in summer, via the
  US Central time zone), so `'6'` always means 06:00 on the Central wall clock — matching the US
  gas-market clock and the CWG loader's US-Eastern precedent.
- **Seed — INSERT-only (DECISION).** Extend the `001` `MERGE arm.Endpoint` seed's `USING (VALUES …)`
  with a `RunHoursCST` per endpoint: **`PointVolume = '*'`; the other 24 = `'6'`.** Put `RunHoursCST`
  in the `WHEN NOT MATCHED BY TARGET THEN INSERT` list **only** — **never** in the `WHEN MATCHED THEN
  UPDATE` branch, so a re-run **never overwrites an operator's retuned value**. (Url keeps updating on
  match; `RunHoursCST` does not.)
- **Parse tolerance (DECISIONS).** Trim/space-tolerant CSV. **Blank / all-invalid / all-out-of-range
  → fail-OPEN to `'*'` (every hour) with a WARN** — a typo must never silently disable an endpoint. A
  **missing schedule row for an enabled endpoint → treat as `'*'`.** Parsing + tolerance live on the
  C# side (B.2), not in SQL.

### B.2 Read proc + schedule provider (load-once, cached)

- **New read proc** `arm.usp_GetEndpointSchedule` — returns `(Name, RunHoursCST)` for all
  `arm.Endpoint` rows (`Name` = the EndpointId). No params; plain read (no `SqlWriteGate`). Add to the
  `999` drop list. (Keeps all DB access behind procs, per loader convention.)
- **New schedule provider** `IPlEndpointSchedule` / `SqlPlEndpointSchedule`, shaped like the existing
  reference providers: registered **singleton** (= load-once per run), reads
  `arm.usp_GetEndpointSchedule` once behind a double-checked `SemaphoreSlim`, parses each
  `RunHoursCST` (B.1 tolerance) into a per-endpoint hour set (with an "every-hour" marker for `'*'` /
  fallback), and caches a `Dictionary<string, HourSet>` keyed by EndpointId (OrdinalIgnoreCase).
  Registered via `services.AddSingleton<IPlEndpointSchedule, SqlPlEndpointSchedule>();`. Proposed
  contract:
  ```
  interface IPlEndpointSchedule {
      Task<bool> ShouldRunAsync(string endpointId, DateTime runStartedUtc, CancellationToken ct);
  }
  ```
  `ShouldRunAsync` **converts `runStartedUtc` to US Central (DST-aware) first** — via
  `TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")` (Windows) / `"America/Chicago"`
  (Linux), resolved with the same OS-id fallback the CWG loader uses for US Eastern — then returns
  `true` when the endpoint's parsed set is every-hour (`'*'`, blank/invalid fallback, **or a missing
  row**) OR contains that **Central** hour. Unlike the 5 id caches it does **not** fail-fast on empty —
  `arm.Endpoint` is deploy-seeded and always present; a missing per-endpoint row falls through to `'*'`.
  (`runStartedUtc` = `context.StartedAtUtc`; only the *gating comparison* is converted to Central — the
  resume key stays UTC, §B.4.)

### B.3 Gating in `IHSPointLogicModule.RunAsync` (precise placement)

- Load the schedule **once per run** (AGSI load-once pattern; the singleton caches it).
- **Gate location:** inside the existing tier loop
  `foreach (tier in {0,1,2}) { foreach (pipeline in tierPipelines) { … } }`, **immediately before**
  `await pipeline.ExecuteAsync(context)`:
  `if (!await schedule.ShouldRunAsync(pipeline.EndpointId, context.StartedAtUtc, ct)) { log one line;
  continue; }`. A gated-off pipeline therefore enumerates **no** work units and makes **no** HTTP
  call. `EnabledEndpoints` stays the **hard master on/off** (the `pipelines` list is already filtered
  by it); `RunHoursCST` is the **cadence gate among enabled endpoints** (compared against the current
**US Central** hour, converted from `context.StartedAtUtc` inside the provider).
- **Skip accounting (OPEN DECISION for the reviewer):** a gated-off pipeline is simply not executed
  and contributes nothing to the aggregate `LoaderRunResult`. Confirm whether a single info log line
  suffices, or a synthetic "skipped" result should be recorded for observability.

### B.4 Hour-granular hot key — `PlHotKeyStrategy.RunHour` (load-bearing)

- **New enum value** `PlHotKeyStrategy.RunHour`, made the **default**. The shared `{hot}` token
  becomes `context.StartedAtUtc` formatted `yyyyMMddHH` (was `yyyyMMdd` for `RunDate`). The `hot =`
  derivation in **all four** work-unit providers (`PlSnapshotWorkUnitProvider`,
  `PlDiscoveryLookupWorkUnitProvider`, `PlDiscoveryDatedFactWorkUnitProvider`,
  `PlBatchedFactWorkUnitProvider`) becomes a 3-way switch: `RunHour → yyyyMMddHH`, `RunDate →
  yyyyMMdd`, `RunId → RunId:N`. (Recommend centralizing the token derivation in one helper so all four
  stay in lockstep.)
- **Effect:** a scheduled hourly endpoint **re-pulls each hour** (the hot key changes hourly), while a
  same-hour re-run still idempotently **skips** via `core.LoadLog` (the key is stable within the hour).
- **The hot key stays UTC (deliberate).** `{hot}` = `context.StartedAtUtc:yyyyMMddHH` is **UTC**, not
  Central, even though the *gate* (§B.2/§B.3) compares in Central. UTC is monotonic — it never repeats
  or skips an hour across a DST transition — so it is the correct, DST-unambiguous idempotency basis;
  the Central conversion is confined to the run/skip gate. Since the host runs at most once per UTC
  hour, each hourly invocation still gets a distinct hot bucket regardless of the Central offset.
- **Archetype-D settled zone UNCHANGED:** only the **hot** suffix gains hour granularity; archetype
  D's **settled**-zone key (`pl:{EndpointId}:{ParamId}:{reportDate:yyyyMMdd}`, no `run=` suffix) is
  untouched — settled dated units still load once then cheap-skip forever.
- **This default flip is a real cadence change** (`RunDate`→`RunHour` = hourly re-pull, not daily).
  Existing keying tests survive because they **pin the strategy explicitly**; the new "all-`'*'`"
  regression asserts **gating** behavior, not key cadence.

### B.5 Tier interaction

- Gating Tier-0 dimensions off on most hours is fine: Tier-2 `PointVolume` (and the SD facts) read the
  **persisted** dimension tables via the reference providers, not a within-run refresh. The reference
  providers already **fail-fast on a truly-empty** table (first deploy). Operational rule: **seed the
  catalogue with one Tier-0 run** (all dimensions loaded) before relying on the hourly cadence. With
  the seed defaults (`PointVolume='*'`, others `'6'`) the dimensions refresh daily at 06:00 **US
  Central** while `PointVolume` runs hourly against the last-persisted point catalogue.

### B.6 Config / DB surface

- `RunHoursCST` lives in `arm.Endpoint` — **retune in the DB, no redeploy** (change a row's value; the
  singleton picks it up on the next run).
- `IHSPointLogicSettings.HotZoneKeyStrategy` **default flips** `RunDate → RunHour`; the
  `appsettings.json` `Loaders:IHSPointLogic:HotZoneKeyStrategy` default becomes `"RunHour"`.
- Schedule hours are **US Central (DST-aware, CST/CDT)**; the report-date stamping and the
  hour-granular resume key stay **UTC** (monotonic, DST-unambiguous — §B.4). `EnabledEndpoints`
  unchanged as the hard on/off master.

### Open decisions for the reviewer to confirm

- **A:** rename the 11 **sink classes** too (`Pl<X>SqlSink`→`<X>SqlSink`) — taken as a DECISION here.
- **A:** rename the `arm.usp_ValidateLoad` **check-name labels** (`RowCount_Pl<X>`→`RowCount_<X>`,
  `Orphan_Pl*`→`Orphan_*`) — taken as a DECISION here.
- **A:** deploy sequence = current `999` teardown of the empty `Pl*` tables, then renamed `001→002→003`.
- **B:** seed `PointVolume='*'`, the other 24 `'6'`; seed is INSERT-only (never overwrite a retune).
- **B:** parse fail-open to `'*'` on blank/invalid/out-of-range and on a missing row (WARN, never
  disable).
- **B:** `HotZoneKeyStrategy` default flips to `RunHour` (hourly re-pull cadence change).
- **B:** whether a gated-off (out-of-hour) pipeline records a synthetic "skipped" result or just a log
  line (B.3).

---

## Section C — PointVolume incremental backfill (`startDate` + `arm.PointMetadata.MaxDateQueued`)

**What changes:** PointVolume (archetype **E**, endpoint #25, `cs/v1/pointlogic/volumeHistory/point`)
becomes an **incremental, per-point** fact. Each active point remembers how far its volume history has
already been pulled in a new watermark column `arm.PointMetadata.MaxDateQueued DATE NULL`; the batched
work-unit provider groups points by that watermark, appends `&startDate=yyyy-MM-dd` per group so the API
returns only rows `date >= startDate`, and the PointVolume merge proc advances each point's watermark to
the newest date it just stored — **all in the one round-trip the sink already makes**. No re-pull of the
whole history on every run.

This is **PointVolume-only.** No other endpoint, archetype, table, TVP, merge/read proc, reference
provider, row, sink, or descriptor changes (boundary in C.13). Names below were reconciled against the
shipped `src/DataLoader.IHSPointLogic/*` and `sql/IHSPointLogic/001–003+999` so they match reality; this
section is the authoritative contract for the PointVolume backfill and **supersedes** the plain-batch
description of archetype E in §4-E and the "no startDate" open item in §11.10.

> **Build-only posture (unchanged).** The loader stays **out of `Platform:EnabledLoaders`** and is not
> deployed/loaded this pass, so there is **no live `DATA_QUALITY_VALIDATOR` stage** for this change. It
> is coded + unit-tested + buildable only, exactly like Sections A and B. (The `startDate` param itself
> was live-confirmed on §25 of the API doc.)

### C.1 API contract (confirmed)

`GET cs/v1/pointlogic/volumeHistory/point?pointIds={csv, ≤50}&startDate={yyyy-MM-dd}` — WRAPPER envelope;
`Data[]` rows `{id, volume, date}`. `startDate` filters history to rows with `date >= startDate`;
combines with `pointIds` and `pageIndex`. Row shape, PK `(PointId, Date)`, and the mapping
`id→PointId`, `date→Date`, `volume→Volume` are unchanged (§7 §25 / API doc §25). Absent `startDate` the
API returns only *recent* history — the whole point of this feature is to make the window explicit and
per-point.

### C.2 DB — new watermark column `arm.PointMetadata.MaxDateQueued`

- **Column:** `arm.PointMetadata.MaxDateQueued DATE NULL` (no default). `NULL` means "never queued" →
  the point backfills from `DefaultStartDateForPointVolume` (C.11) on its next PointVolume pull.
  **Deliberately on `arm.PointMetadata`, NOT `arm.Point`** — PointVolume's batching/scope and the
  `arm.PointVolume.PointId` FK are both driven by `arm.usp_GetPointIds` reading
  `arm.PointMetadata WHERE PointIsActive = 1` (§A.1(b)), which is the superset of `arm.Point` this loader
  actually iterates; putting the watermark anywhere else would decouple it from the id list that drives
  the pulls.
- **`001`** — add the column two ways so it lands on both fresh and already-deployed databases (the
  Section B `RunHoursCST` idempotent-ADD precedent): (a) include `MaxDateQueued DATE NULL` in the
  `CREATE TABLE arm.PointMetadata` column list; and (b) a guarded idempotent ALTER outside the create
  guard — `IF COL_LENGTH('arm.PointMetadata','MaxDateQueued') IS NULL ALTER TABLE arm.PointMetadata ADD
  MaxDateQueued DATE NULL;`. No `DF_` default constraint (NULL is the meaningful "never queued" state).
- **`arm.PointMetadataTvp` and `arm.usp_BulkMergePointMetadata` do NOT gain the column.** The Tier-0
  PointMetadata refresh maps the API payload only (which carries no watermark), so `MaxDateQueued` is
  absent from that TVP and from both the merge proc's `INSERT` and `UPDATE SET` lists. **Consequence
  (required):** a daily PointMetadata refresh **never touches an existing point's watermark**, and a
  newly-discovered point is inserted with `MaxDateQueued = NULL` (→ backfills from the default on its
  first PointVolume pull). Only `arm.usp_BulkMergePointVolume` (C.7) writes this column.
- **`999`** — no new drop statement needed; the column is dropped with `arm.PointMetadata`.

### C.3 Id-source contract — `arm.usp_GetPointIds` + `IPlPointProvider`

- **`arm.usp_GetPointIds` returns `(PointId INT, MaxDateQueued DATE NULL)` pairs** (was: a single
  DISTINCT `PointId` column). Body:
  `SELECT PointId, MaxDateQueued FROM arm.PointMetadata WHERE PointIsActive = 1 ORDER BY PointId;`
  (`PointId` is the table PK so the row is already 1-per-point — no `DISTINCT`/`GROUP BY` needed; the
  active-scope filter and ascending order are retained, the ascending order making the batch slicing in
  C.4 deterministic). Name and `999` drop entry unchanged.
- **C# `IPlPointProvider` / `SqlPlPointProvider` (`ReferenceProviders.cs` ~lines 135–145) carry the
  nullable date.** Introduce a tiny value type
  `public readonly record struct PlPointRef(int PointId, DateOnly? MaxDateQueued);` and change the
  contract to `Task<IReadOnlyList<PlPointRef>> GetPointsAsync(CancellationToken ct)` (rename from
  `GetPointIdsAsync` to signal the payload changed; the only caller is
  `PlBatchedFactWorkUnitProvider`). Because the read is now two-column, `SqlPlPointProvider` **stops
  deriving from the single-`INT` `SqlPlIntIdProvider` base** and gets a bespoke load-once/fail-fast
  reader **mirroring `SqlPlSubregionProvider`** (double-checked `SemaphoreSlim`, cache the list, throw
  `InvalidOperationException` if empty). Read the date as
  `reader.IsDBNull(1) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(1))`. The other four
  reference providers (Region/State/PointType/Subregion) and the shared `SqlPlIntIdProvider` base are
  **unchanged**.

### C.4 Batching by watermark (`PlBatchedFactWorkUnitProvider`, `PlWorkUnit.cs` ~lines 270–324)

Replace the single flat "chunk all ids into ≤50 slices" loop with a **group-then-slice**:

1. `points = await pointProvider.GetPointsAsync(ct)` → `IReadOnlyList<PlPointRef>` (already ascending by
   `PointId`).
2. Resolve each point's start date `S(point) = point.MaxDateQueued ?? DefaultStartDate`, where
   `DefaultStartDate` is the parsed `DefaultStartDateForPointVolume` setting (C.11).
3. **Group by the resolved start date `S`** (a `DateOnly`), preserving ascending `PointId` order within
   each group. Grouping on the *resolved* `S` (rather than the raw nullable `MaxDateQueued`) is the
   deliberate reading of the locked "group by `MaxDateQueued` value" decision: it collapses the `NULL`
   group and any point whose `MaxDateQueued` already equals the default into **one** group — they issue
   byte-identical `startDate` queries, so they belong together, and it removes the token collision two
   separate groups mapping to the same `startDate` would otherwise cause (C.6, and the open decision in
   C.14). Groups are naturally ordered by `S` for legibility.
4. **Sub-chunk each group** into ≤ `PointVolumeBatchSize` (=50) **contiguous** slices; `subIndex` counts
   `0,1,2,…` **within the group** (reset per group). Emit one hot `PlWorkUnit` per slice.

Because `points` is deterministic (ordered `PointId`, stable `S` map), the whole group→slice partition —
and therefore every batch token (C.6) — is deterministic for a given DB state.

### C.5 Request composition (provider owns it)

Per slice the provider builds
`RequestPath = "cs/v1/pointlogic/volumeHistory/point?pointIds={csv}&startDate={S:yyyy-MM-dd}"` (CSV =
the slice's ids, invariant culture; `S` formatted `yyyy-MM-dd`). The provider owns query composition;
`PlSourceReader.BuildPageUri` already appends `&pageIndex=N` with the correct separator because the path
already contains `?` (no reader change). `S` is invariant-culture formatted.

### C.6 Batch token, resume key, and `arm.FileLog` ParamKey

The batch composition now depends on the per-group `startDate`, so the token **encodes the start date +
the in-group sub-index** (was: a single zero-padded global batch index):

- **`BatchToken = $"{S:yyyyMMdd}-{subIndex:D4}"`** — e.g. `20200101-0000`, `20200101-0001`,
  `20260812-0000`. Stable and legible; the `startDate` prefix makes a batch self-describing in the log
  and in `arm.FileLog`. **Uniqueness within a run:** `(S, subIndex)` pairs are unique → tokens are
  unique (this is why C.3-step-3 groups on the resolved `S`: two raw watermark values that resolve to the
  same `S` must not both mint a `…-0000`).
- **Resume key** stays the archetype-E shape with the richer token:
  `KeyValue = $"pl:PointVolume:{BatchToken}:run={hot}"` (`{hot}` = the shared UTC `RunHour` token,
  §B.4 — **unchanged**).
- **`arm.FileLog` identity** is unchanged in *shape* (§6 row E): `PlWorkUnit.ParamKey` already returns
  `BatchToken ?? ParamId`, so the hub natural key is
  `(Endpoint='PointVolume', ParamKey='{S:yyyyMMdd}-{subIndex}', Variant='Batch',
  RepresentativeDate=run's UTC capture date)` → one hub row per (endpoint, batch, capture day). Only the
  *content* of the `ParamKey` slot changed (now start-date-stamped); no hub/DDL change. Keep
  `Variant = "Batch"` and `RepresentativeDate = runDate` as today.

### C.7 Write-back — extend `arm.usp_BulkMergePointVolume` (one round-trip)

The watermark advance happens **inside the existing PointVolume merge proc**, in the same call the sink
already makes (`PointVolumeSqlSink` → `SqlSinkBase.WriteAsync`, `ProcedureReturnsRowCount => true` →
`ExecuteScalar`). The proc's TVP (`arm.PointVolumeTvp`: `FileLogId, PointId, Date, Volume`), its
signature, and the sink C# are **all unchanged** — only the proc *body* grows a trailing UPDATE:

1. Run the existing `MERGE arm.PointVolume … ;` on PK `(PointId, Date)`.
2. **Capture the merge count immediately, before the write-back:**
   `DECLARE @merged INT = @@ROWCOUNT;`.
3. **Advance the watermark from the TVP** (per-point MAX of the dates just stored):
   ```
   UPDATE pm
      SET pm.MaxDateQueued = mx.MaxDate
     FROM arm.PointMetadata AS pm
     JOIN (SELECT PointId, MAX([Date]) AS MaxDate FROM @Records GROUP BY PointId) AS mx
       ON mx.PointId = pm.PointId
    WHERE mx.MaxDate > ISNULL(pm.MaxDateQueued, '00010101');
   ```
   The `WHERE … > ISNULL(...)` guard makes the watermark **monotonic non-decreasing** (defensive — since
   `startDate = MaxDateQueued` and the API returns `date >= startDate`, `MAX(Date) ≥ MaxDateQueued`
   always, so this only ever advances or no-ops; it never regresses).
4. **Return the captured merge count as the scalar** (`SELECT @merged;`) so
   `SqlSinkBase.WriteAsync`'s `ExecuteScalar` — and therefore the pipeline's `RecordsProcessed` /
   `core.LoadLog` — reflects **PointVolume rows merged**, unaffected by the write-back UPDATE's own
   `@@ROWCOUNT`. (This is the load-bearing ordering: capture → update → select the captured value.)

The date field written back is the JSON `date` → `PointVolumeRow.Date` (`DateOnly`) → TVP `Date` (`DATE`)
→ `MAX([Date])`. De-dup in the sink is on `(PointId, Date)`, which does not change `MAX(Date)`.

### C.8 Zero-row pulls leave the watermark untouched (explicit)

Two layers guarantee a point that returned nothing keeps its watermark:

- A batch that returns **no rows at all** never reaches the proc — `SqlSinkBase.WriteAsync` short-circuits
  on `rows.Count == 0` (and the reader logs `NotAvailable`), so no UPDATE runs.
- A mixed batch (some points return rows, others don't) sends a TVP that **only contains the points that
  returned rows**; the `JOIN … (SELECT … FROM @Records GROUP BY PointId)` in C.7-step-3 therefore
  touches only those points. Points absent from the payload are not in `@Records` → their `MaxDateQueued`
  is left exactly as it was. This is intentional: an empty pull must not falsely advance the watermark.

### C.9 Idempotency, resume, and same-hour re-run (accepted behavior)

- **Steady state:** on a normal run each point pulls `date >= its watermark`, stores the tail, and the
  watermark advances to the newest stored date. The MERGE on `(PointId, Date)` makes re-storing the
  boundary day idempotent.
- **Same-hour manual re-run (accepted, no extra machinery):** after a successful pull the watermark has
  advanced, so a second run in the same UTC hour re-reads the advanced `(PointId, MaxDateQueued)` list,
  **recomposes** groups/batches (the `startDate` prefixes — and thus the batch tokens and `core.LoadLog`
  keys — differ from the first run), and re-pulls **incrementally** from the new watermark (a small
  window, idempotent MERGE). It is deliberately **not** a `core.LoadLog` no-op. Per the locked decision
  we do **not** add batch-token/resume-key complexity to force a no-op; the hot key stays `RunHour`/UTC
  (§B.4). The cost is one small incremental re-pull, which is correct and cheap.
- **Crash/resume within a run:** work units are enumerated once at `GetWorkUnitsAsync`, so batch
  composition is fixed for the life of a run. A resumed run (after the overlap guard releases) re-reads
  the now-partially-advanced watermarks and recomposes — already-completed points re-pull their small
  advanced tail (idempotent), not-yet-done points pull from their unchanged watermark. No duplicates, no
  data loss; the overlap guard still prevents two hosts running at once.

### C.10 Backfill operator workflow (locked mechanism)

To force a point (or set of points) to re-pull full history from `DefaultStartDateForPointVolume` on the
next run, an operator sets its watermark back to NULL directly in the DB:

```
UPDATE arm.PointMetadata SET MaxDateQueued = NULL WHERE PointId IN (…);   -- or any predicate
```

On the next PointVolume run those points fall into the `NULL → DefaultStartDate` group and re-pull from
the default forward; the write-back then re-advances their watermark. This is the intended, no-redeploy
backfill lever (retune in the DB, like Section B's `RunHoursCST`). Setting it to a **specific** date
(e.g. `'2023-01-01'`) instead of NULL backfills from that date forward — same mechanism.

### C.11 New setting `DefaultStartDateForPointVolume`

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `DefaultStartDateForPointVolume` | string (`yyyy-MM-dd`) | `"2020-01-01"` | `startDate` used for any point whose `MaxDateQueued IS NULL` (the initial backfill floor). **Not a secret — NOT `SEE_DB`.** |

- Add to `IHSPointLogicSettings` (a plain `string` with the `"2020-01-01"` default) and to the
  `appsettings.json` `Loaders:IHSPointLogic` block alongside `PointVolumeBatchSize`.
- **Parse once** (invariant `DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture)`) in
  `PlBatchedFactWorkUnitProvider` (or a shared settings-validation helper). On a blank/unparseable value
  **fail fast** with a clear message (`InvalidOperationException`) — a global backfill floor typo must
  surface loudly, not silently backfill from an unexpected date. (Contrast Section B's fail-*open* parse:
  there a per-endpoint schedule typo must not disable an endpoint; here a bad global date has no safe
  default, so fail-*closed*.)

### C.12 Concurrency / write-gate notes

- `PointVolumeSqlSink.WriteAsync` auto-acquires `SqlWriteGate` on `arm.usp_BulkMergePointVolume`
  (unchanged). The write-back UPDATE to `arm.PointMetadata` now rides **inside that same gated call**, so
  concurrent PointVolume batches serialize on the one PointVolume-proc key — and because batches partition
  the points into **disjoint** id sets, their `arm.PointMetadata` UPDATEs touch disjoint rows anyway (no
  logical conflict, no deadlock).
- No contention with the Tier-0 `arm.usp_BulkMergePointMetadata` (a different gate key): Tier 0 and
  Tier 2 are separated by the §1.4 tier barrier and never run concurrently in one host run;
  cross-process is the overlap guard's job. No new gate key is required.

### C.13 Boundary — what does NOT change

Only PointVolume's batching/keying, `arm.PointMetadata` (one column), `arm.usp_GetPointIds`,
`IPlPointProvider`/`SqlPlPointProvider`, `PlBatchedFactWorkUnitProvider`,
`arm.usp_BulkMergePointVolume` (body only), and the new setting change. **Unchanged:** the other 24
endpoints, archetypes A/B/C/D, the `PlWorkUnit`/`PlSourceReader`/`PlPipeline` plumbing, the hot-key
strategy and `RunHour` cadence (§B.4), the `arm.FileLog` hub DDL and `arm.usp_UpsertFileLog`, the
PointVolume row/sink/TVP/descriptor identifiers and the `arm.PointVolume` table + PK, and the four other
reference providers + `SqlPlIntIdProvider` base. `arm.usp_ValidateLoad` need not change (optionally it
could gain a watermark sanity check — MaxDateQueued not in the future / not before the default — but that
is out of scope for this pass).

### C.14 Open decisions for the reviewer to confirm

- **Grouping key:** group on the **resolved** `startDate` `S = MaxDateQueued ?? Default` (C.3-step-3),
  which collapses the `NULL` group and any point already at the default into one group. This is the
  design's reading of the locked "group by `MaxDateQueued` value" decision and is what removes the
  duplicate-token risk; confirm it is acceptable (the alternative — grouping on the raw nullable value —
  would need an extra discriminator in the token to stay unique).
- **`IPlPointProvider` method rename** `GetPointIdsAsync → GetPointsAsync` returning `PlPointRef` — taken
  as a DECISION here; existing unit tests that reference `GetPointIdsAsync` / the `int` list are updated
  by the CODE_TESTER stage.
- **Watermark monotonic guard** `WHERE mx.MaxDate > ISNULL(pm.MaxDateQueued, '00010101')` in the
  write-back UPDATE (C.7) — recommended as defensive; confirm keep vs drop (functionally a no-op given
  `MAX(Date) ≥ startDate`).
- **`DefaultStartDateForPointVolume` parse failure = fail-fast** (C.11) — confirm fail-closed here vs
  Section B's fail-open schedule parse.
- **Backfill lever is a manual DB `UPDATE … SET MaxDateQueued = NULL`** (C.10) — confirm no dedicated
  proc/CLI is wanted for the build-only pass.
