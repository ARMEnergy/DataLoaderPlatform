# NGI (NGI Data Services — Bidweek natural-gas price survey) loader — design & processing flow

Design/flow spec for the **NGI** loader. Input of record is the field reference at
`docs/apis/NGI.md` (two data endpoints + `/auth`, **fully live-verified — zero reconstructed
fields**, per-field SQL types, natural keys, the `"None"` sentinel, the status-code matrix).
This document is the CODER hand-off; it contains **no code and no SQL**. SQL objects named here
are *proposed* to DATABASE_DEVELOPER (open items collected in §13).

**Modelled on `docs/design/AGSI.md`** — the closest structural twin (two endpoints, two
explicitly-built closed pipelines, `arm` schema + `arm.FileLog` hub, StormVista two-zone
settled/hot resume key over `LoaderPipelineBase` directly, `DaysBack`/`SettledAfterDays`
config pair, module-level post-load validator, build-only posture). **Auth is borrowed from
`docs/design/IIR.md` §3** (`src/DataLoader.IIR/TokenProvider.cs`): a thread-safe singleton JWT
token provider + a delegating handler that re-mints once on `401`. **Deviations from both are
called out inline and summarised in §1.2.**

Locked decisions this design is built around: DB **`NGI`**, schema **`arm`**, Integrated
Security; `Username`/`Password` via **`SEE_DB`**, POSTed as a **JSON body** to `/auth` to mint a
JWT presented as `Authorization: Bearer …`; **two endpoints modelled as two explicitly-built,
mutually INDEPENDENT closed pipelines** (`BidWeekLocations`, `BidWeekData`) run **sequentially,
Locations first — for deterministic logging only, NOT because of a data dependency**; one
`arm.FileLog` hub + two tables (`arm.BidWeekData` PK `(IssueDate, PointCode)`,
`arm.BidWeekLocation` PK `(PointCode)`) carrying `FileLogId`, **no FK between them**;
go-forward trailing-window resume with the two-zone (settled/hot) key, `DaysBack = 60` /
`SettledAfterDays = 60`; a **US-Central** run-date basis; **build-only pass** — the loader is
wired but left **out of `Platform:EnabledLoaders`**.

> **Build-only posture (read first).** Same posture CWG / AGSI / IHSPointLogic / IIR shipped in:
> everything is coded, unit-tested and buildable, but the loader is **disabled by default**
> (`"NGI"` is **not** in `Platform:EnabledLoaders`) and there is no live DB deploy or data load.
> Unlike AGSI/IIR the **field set needs no live confirmation** (`docs/apis/NGI.md` is
> zero-reconstruction); what remains open is purely **behavioural** — revision policy, history
> depth, token-expiry behaviour, rate limits (§12).

---

## 🛑 The one rationale you must not "optimise away"

**Enumerate EVERY calendar date in the window. Do NOT build a business-day or holiday filter.**

NGI's own OpenAPI spec says of `issue_date`: *"Issue date will always be a business day."*
**The live data contradicts the spec.** Full evidence in **§3.4** — the short version:

| `issue_date` | Weekday | Result |
|---|---|---|
| **2026-08-01** | **SATURDAY** | **`200`, 163 records** |
| 2026-07-31 | Friday | `404` |

A weekday filter would have skipped 2026-08-01 and **silently missed the entire August 2026
issue** while reporting a clean run. There is no vendor-published publication calendar. Probe
every day; let the `404` be the answer (§3.4, §5.3).

---

## 0. Class / structure inventory

| Concern | Type(s) | Notes |
|--------|---------|-------|
| Settings | `NgiSettings : LoaderSettingsBase`, `NgiHotKeyStrategy` enum | §10 |
| Module | `NgiModule : ILoaderModule` (`LoaderId = "NGI"`) | builds both pipelines explicitly |
| Pipeline marker | `INgiPipeline : ILoaderPipeline { string EndpointId }` | so `RunAsync` can enumerate + toggle + order |
| Pipeline | `NgiPipeline<TUnit,TRow> : LoaderPipelineBase<TUnit,TRow,TRow>, INgiPipeline` | one generic class, two closed instances; `IdentityTransformer<TRow>` |
| Work unit (2) | `NgiLocationsWorkUnit : WorkUnit` | single undated unit per run, always hot (§3.1) |
| Work unit (1) | `NgiBidWeekWorkUnit : WorkUnit` | one per **calendar date** in the window; two-zone key (§3.1/§3.3) |
| Provider (2) | `NgiLocationsWorkUnitProvider : IWorkUnitProvider<NgiLocationsWorkUnit>` | returns one unit/run |
| Provider (1) | `NgiBidWeekWorkUnitProvider : IWorkUnitProvider<NgiBidWeekWorkUnit>` | **date window ALONE** (§2) |
| Source reader (2) | `NgiLocationsSourceReader : ISourceReader<NgiLocationsWorkUnit, BidWeekLocationRow>` | name→code map; direction-critical (§5.2) |
| Source reader (1) | `NgiBidWeekSourceReader : ISourceReader<NgiBidWeekWorkUnit, BidWeekDataRow>` | `{meta,data}`, `data` = map keyed by point code; 404-tolerant (§5.3) |
| Rows | `BidWeekLocationRow`, `BidWeekDataRow` (both carry `int FileLogId`) | one per table |
| Sinks | `BidWeekLocationSqlSink`, `BidWeekDataSqlSink` (both `: NgiSqlSinkBase<T> : SqlSinkBase<T>`) | TVP bulk MERGE on the natural key (§8.3) |
| Auth | `INgiTokenProvider`, `NgiTokenProvider` (singleton), `NgiTokenAuthHandler : DelegatingHandler` | §4 |
| FileLog | `NgiFileContext` (readonly struct), `INgiFileLog`, `SqlNgiFileLog` (`arm.usp_UpsertFileLog`, `SqlWriteGate`, returns `FileLogId`) | shared hub (§6) |
| Parse / time | `NgiParse` (tolerant `"None"`-aware readers), `NgiTime` + `NgiWindow` record struct | §5.1, §7 |
| Post-load validation | `NgiLoadValidator` (module-level hook) → `arm.usp_ValidateLoad` | §9 |
| HTTP plumbing | `NgiRateLimiter` (singleton), `NgiRateLimitingHandler`, `NgiHttpPolicy` | §4.4 |

### 0.1 Types that must **NOT** exist (deliberate absences)

Listed because a copy of AGSI would add all of them and they would be wrong here:

| Absent | Why |
|--------|-----|
| `INgiLocationProvider` / any reference provider | The `BidWeekData` work units come from the **date window alone** — nothing is read back out of `arm.BidWeekLocation`. §2. |
| `arm.usp_GetBidWeekLocations` (a `usp_Get…` read proc) | Nothing consumes it. §2. |
| Any tier barrier / fail-fast-if-dimension-empty guard | There is no dependency to protect. §2. |
| `FK_BidWeekData_BidWeekLocation` | Codes may legitimately diverge; arrival order is not guaranteed. §2, §8.2. |
| `arm.Region` lookup / a `Region` axis on `arm.FileLog` | NGI has no region request axis, and the name would collide confusingly with the `Region` **payload column** on the fact. §6. |
| A business-day / holiday calendar helper | Disproven by the data. §3.4. |
| A paging loop, an `?pageIndex=`/`limit`/`offset` param, an id-batching (`≤50`) loop | Neither endpoint pages or batches — **one request = the complete result set**. |
| A `/auth/refresh` call or any persisted `refresh` token | No refresh endpoint exists in the 59-path spec; `refresh` is captured-and-ignored. §4.2. |

---

## 1. Module topology & pipeline strategy

**One module, two closed pipelines**, built explicitly in `NgiModule` via `Build*Pipeline`
factory closures (the AGSI / CWG / StormVista pattern), toggled by `EnabledEndpoints[]`
(`"BidWeekLocations"`, `"BidWeekData"`).

### 1.1 Why two closed pipelines (and not descriptor-driven fan-out)

The two endpoints have different work-unit types, different response envelopes (a `{meta,data}`
map-keyed-by-code vs. a single `{"Bidweek Locations": {name → code}}` map), different tables and
different keys. With **only two** endpoints, the CWG/IHSPointLogic descriptor record + fan-out
would be pure indirection — there is nothing to amortise. So each pipeline is `new`ed inside a
module factory closure and handed to `NgiPipeline<TUnit,TRow>`'s constructor. **We never register
a generic `IWorkUnitProvider<T>` / `ISourceReader<T>` in DI** (the collision
Platts/StormVista/CWG/AGSI warn about); the pipeline's generics are never resolved by the
container.

### 1.2 ⚠ The critical divergence from AGSI — the pipelines are **NOT** coupled

**This is the single easiest thing a copy-paste from AGSI will get wrong.** In AGSI the two
pipelines are *coupled and ordered*: `/api/about` populates `arm.GasStorageEntity`, an
`IAgsiCountryProvider` reference provider reads it back through `arm.usp_GetGasStorageEntities`,
and the storage pipeline's work units **are** that country list × the date window — a hard
ordering barrier with a fail-fast-if-empty guard.

**NGI has no such dependency.** `bidweekLocations` is **not** a discovery tier:
`/bidweekDatafeed.json` is parameterised by **date alone**. So:

| AGSI | NGI |
|------|-----|
| Entities pipeline feeds Storage's work units | **Each pipeline's work units are self-contained** |
| `IAgsiCountryProvider` (load-once, fail-fast) | **No reference provider at all** |
| `arm.usp_GetGasStorageEntities` read proc | **No read proc** |
| Hard barrier: About must complete first | **No barrier** — order is a logging convenience only |
| `FK_GasStorage_Entity` enforces integrity | **No FK** (§8.2) |
| Storage-only run fails fast on an empty dimension | **A `BidWeekData`-only run is completely valid** |

**Order:** `RunAsync` still runs **Locations first, then BidWeekData**, purely so a run's log
reads deterministically (the small lookup pull lands before the 60-unit fan-out). It is **not**
load-bearing. Concretely:

- A **failure** in the Locations pipeline must **NOT** prevent `BidWeekData` from running. Both
  are executed unconditionally (when enabled) and their `LoaderRunResult`s aggregated.
- Running them **concurrently** would also be correct; sequential is chosen for log clarity and
  to keep the global request rate predictable against an unpublished rate limit (§12 item 9).
- Per the platform's discovery-first principle: **do not introduce a tier barrier when the
  dependency is local — and here there is no dependency at all.**

### 1.3 Why `LoaderPipelineBase` directly (AGSI/CWG-style), NOT a custom orchestrator

Unit counts are trivial: **Locations = 1 unit; BidWeekData = `DaysBack` units (60 at the
default).** The whole list materialises for free, so NGI uses `LoaderPipelineBase` directly and
reuses the vetted per-unit loop unchanged — `BeginAsync` idempotency skip → `ReadAsync` →
identity transform → `WriteAsync` → `CompleteSuccess`/`CompleteFailure`, bounded by
`ParallelRunner` at `MaxConcurrentWorkUnits`, per-unit timeout, **fail-a-block-not-the-run**.
StormVista's windowed orchestrator is not needed and is not used; NGI borrows only its
**two-zone resume key** (§3.3).

### 1.4 DI registration (`RegisterServices`)

1. `services.AddLoaderSettings<NgiSettings>(configuration, Id);` — binds `Loaders:NGI`; resolves
   `Username`/`Password` = `"SEE_DB"` from `core.Param` lazily at run time.
2. Shared throttle: `NgiRateLimiter` (singleton state) + `NgiRateLimitingHandler` (transient).
3. Auth: `services.AddSingleton<INgiTokenProvider, NgiTokenProvider>();` and
   `services.AddTransient<NgiTokenAuthHandler>();`
4. Named `HttpClient` **`"NGI.Token"`** — the **un-authed** mint client (§4.1). `Timeout =
   HttpTimeoutSeconds`, `Accept: application/json`; `.RemoveAllLoggers()`; a Polly policy that
   retries `429`/`5xx`/transient but **never** `401`/`403` (a credential failure must be
   immediate and loud); the rate-limiting handler (innermost). **It must NOT carry
   `NgiTokenAuthHandler`** — that would recurse infinitely.
5. Named `HttpClient` **`"NGI"`** — the data client:
   - `Timeout = HttpTimeoutSeconds`, `Accept: application/json`;
   - `.RemoveAllLoggers()` — suppress `IHttpClientFactory`'s default logging. **No secret is in
     any URL here** (unlike CWG/StormVista's `?apikey=` or IIR's mint query string) — the
     credential is in the `/auth` **request body** — but we remove the loggers anyway for parity
     and to keep the log surface identical across loaders;
   - `.AddPolicyHandler(...)` — retry **OUTER** (`RetryCount`/`RetryDelayMs`; retry
     `429`/`5xx`/transient, honour `Retry-After`). **`401` is EXCLUDED from the policy** so the
     auth handler owns it (§4.3). `400` and `404` are **not** retryable (`404` is a normal
     outcome; `400` is a caller bug);
   - `.AddHttpMessageHandler<NgiTokenAuthHandler>()` — auth **MIDDLE**;
   - `.AddHttpMessageHandler<NgiRateLimitingHandler>()` — throttle **INNER**, so every attempt
     (first and each retry, and the 401 replay) is paced by `RequestsPerSecond`.
   → **Handler order: retry (OUTER) → auth → throttle (INNER)** — the repo standard.
6. `services.AddSingleton<INgiFileLog, SqlNgiFileLog>();`
7. `services.AddSingleton<NgiLoadValidator>();`
8. The two pipelines, one line each:
   - `BidWeekLocations` → provider `NgiLocationsWorkUnitProvider`, reader
     `NgiLocationsSourceReader`, sink `BidWeekLocationSqlSink`.
   - `BidWeekData` → provider `NgiBidWeekWorkUnitProvider`, reader `NgiBidWeekSourceReader`,
     sink `BidWeekDataSqlSink`.
   Each factory constructs provider/reader/sink and wraps them in
   `NgiPipeline<TUnit,TRow>(endpointId, provider, source, sink, loadLog, settings, logger)` —
   exactly AGSI's `BuildEntitiesPipeline` / `BuildStoragePipeline` shape.

### 1.5 `RunAsync` fan-out

1. Read `NgiSettings`; create the module logger.
2. If `NgiTime.UsingUtcFallback` (neither US-Central time-zone id resolved on this host) → log a
   **warning** (the window boundary would shift — §7). AGSI does the same for CET.
3. **Credential guard.** If `Username` or `Password` is blank or still the literal `"SEE_DB"`
   placeholder → log an **error naming the `core.Param` rows** (`LoaderName='NGI'`,
   `ParamName='Username'` / `'Password'`) and return `LoaderRunResult.Failed`. **Never log the
   values.** Unlike AGSI, **both** endpoints need the credential (both data paths declare
   `security: [{jwtAuth: []}]`), so this guard is unconditional — there is no key-free endpoint.
   *(Note, as AGSI documents: materialising `IOptions<NgiSettings>` already runs
   `SeeDbSettingsResolver`, which **throws** if either `core.Param` row is missing. This guard is
   therefore the **secondary** check that catches a row whose value was left as the literal
   `SEE_DB` placeholder.)*
4. `enabled = HashSet(EnabledEndpoints, OrdinalIgnoreCase)`. Enumerate
   `services.GetServices<INgiPipeline>()`; **warn** for any enabled id with no matching pipeline;
   if none enabled → warn and return `Success = true`.
5. Run in the fixed order **`BidWeekLocations` then `BidWeekData`** (§1.2 — cosmetic ordering).
   Each `ExecuteAsync` fans its units out via `ParallelRunner` (`MaxConcurrentWorkUnits`); the
   shared rate-limited client bounds global RPS. **Both run even if the first fails.**
6. Run the **module-level post-load validation** (§9) scoped to the run's issue-date window.
7. Aggregate the per-pipeline `LoaderRunResult`s (sum totals; `Success = all succeeded`;
   concatenate error messages) exactly as AGSI/CWG/StormVista/Platts do.

---

## 2. Work-unit derivation — **no discovery, no reference handoff, no barrier**

This section exists to say what NGI *does not* do, because the twin it is modelled on does the
opposite (§1.2).

**`BidWeekData` work units are derived from the trailing date window and nothing else.**
`NgiBidWeekWorkUnitProvider` takes `LoaderRunContext.StartedAtUtc` + `NgiSettings.DaysBack`,
calls `NgiTime.ResolveWindow(...)`, and emits one unit per calendar date. It touches **no
database table**, holds **no reference cache**, and has **no fail-fast-if-empty guard**.

**`BidWeekLocation` is a plain full-snapshot lookup pull**, not a discovery tier. It is one
undated request that refreshes the name↔code crosswalk in place. Nothing downstream reads it at
run time; its only consumers are humans and the **observational** reconciliation checks in
`arm.usp_ValidateLoad` (§9).

Consequences a reviewer should confirm are intentional:

- **A `BidWeekData`-only run is completely valid** and loads everything correctly with an empty
  `arm.BidWeekLocation`. (Contrast AGSI, where a Storage-only first run is a configuration
  error.)
- **A `BidWeekLocations`-only run is valid too** — it just refreshes the crosswalk.
- **The two pipelines could be reordered or parallelised** without changing correctness.
- **A new point code appearing in a datafeed before the locations snapshot is refreshed loads
  fine** — the fact carries its own `Region` and `PricingPoint` (§8.2), so it is fully
  self-describing.

---

## 3. Work-unit definitions, window resolution & two-zone resume keying

### 3.1 Work units

- **`NgiLocationsWorkUnit`** — a single **undated** unit per run. No payload beyond the run
  token. **Key = `ngi:locations:run={hot}`** → always hot (§3.3), so the crosswalk is refreshed
  every run (RunDate cadence by default). `DisplayName = "NGI bidweek locations"`.
- **`NgiBidWeekWorkUnit`** — one unit per **candidate issue date**. Fields: `IssueDate`
  (`DateOnly`, the `issue_date` query value), `RequestPath` (sanitised descriptor for logging),
  `KeyValue` (precomputed, §3.3), `Key => KeyValue`.
  `DisplayName = $"NGI bidweek {IssueDate:yyyy-MM-dd}"`. One unit = one HTTP request = one
  `arm.FileLog` row.

There is **no** per-point work unit: one request returns all ~163 points for the date (no paging,
no id batching).

### 3.2 Window resolution — `NgiTime.ResolveWindow` is the **single source of truth**

```
runDate = CentralToday(context.StartedAtUtc)          // US Central calendar date (§7)
newest  = runDate                                     // window ENDS AT TODAY — never a future date
days    = Max(1, DaysBack)                            // clamp
from    = newest.AddDays(-(days - 1))
to      = newest
→ NgiWindow(RunDate, Newest, From, To, DaysBack)      // inclusive [From .. To], `days` dates
```

`NgiTime.ResolveWindow(startedAtUtc, daysBack)` is used by **BOTH** the work-unit provider (the
load window) and `NgiLoadValidator` (the validation window) — AGSI's single-source-of-truth rule,
so the two can never drift apart. **Do not recompute the window anywhere else.**

**Why the window ends at *today*, not `today − 1`** (AGSI uses `DateOffsetDays = -1` for a
publication lag): NGI publishes the issue **on** its issue date, so today can legitimately carry
a publication. What must be excluded is the **future**:

> **⚠ Never enumerate a future date.** A future date returns `404` (verified: `2026-09-01`), and
> because a `404` **completes the unit successfully** (§5.3), a future date that fell in the
> **settled** zone would be recorded permanently done and never re-probed once it became a real
> issue date. The provider therefore hard-clamps: any candidate `d > runDate` is **skipped with a
> warning** (defence in depth — `newest = runDate` already guarantees it, but a future
> `DaysBack`/offset edit must not be able to break the invariant silently).

Enumeration (`NgiBidWeekWorkUnitProvider.GetWorkUnitsAsync`):

```
window = NgiTime.ResolveWindow(context.StartedAtUtc, DaysBack)
hot    = HotZoneKeyStrategy == RunDate ? window.RunDate:yyyyMMdd   // Central; §3.3
                                       : context.RunId:N

for k in 0 .. window.DaysBack-1:
    d = window.Newest.AddDays(-k)
    if (d > window.RunDate) { warn; continue }                     // future-date clamp
    ageDays = window.RunDate.DayNumber - d.DayNumber               // 0 .. DaysBack-1
    baseKey = $"ngi:bidweek:{d:yyyyMMdd}"
    unit = {
        IssueDate   = d,
        RequestPath = $"/bidweekDatafeed.json?issue_date={d:yyyy-MM-dd}",
        KeyValue    = ageDays > SettledAfterDays ? baseKey                    // SETTLED → stable
                                                : $"{baseKey}:run={hot}"      // HOT → run-varying
    }
```

`NgiLocationsWorkUnitProvider` simply returns
`[ new NgiLocationsWorkUnit { KeyValue = $"ngi:locations:run={hot}" } ]`.

**Date formatting is load-bearing:** `issue_date` MUST be formatted `yyyy-MM-dd` with
`CultureInfo.InvariantCulture`. Any other format returns **`400`**, which this loader treats as a
hard failure (§5.4).

### 3.3 Two-zone settled/hot resume key (literal formats)

`DaysBack` sets the **enumeration** window; `SettledAfterDays` splits it into two zones by the
candidate date's age (`ageDays = runDate.DayNumber − d.DayNumber`, both on the Central run date).
`core.LoadLog` skips a unit only when its `Key` is already recorded **successful**, so these three
literal formats *are* the idempotency contract:

| Zone | Literal key format | Behaviour |
|------|--------------------|-----------|
| **Settled** (`ageDays > SettledAfterDays`) | `ngi:bidweek:{yyyyMMdd}` | Stable. Once `core.LoadLog` records success, every later run's `BeginAsync` returns `null` → **cheap skip, no HTTP** (the Platts/CWG/AGSI settled behaviour). |
| **Hot** (`ageDays <= SettledAfterDays`) | `ngi:bidweek:{yyyyMMdd}:run={hot}` | Run-varying → **re-pulled**, upserting idempotently through the natural-key MERGE (§11). |
| **Locations** (undated) | `ngi:locations:run={hot}` | **Always hot** → the crosswalk is refreshed every run. |

`{hot}` per `NgiHotKeyStrategy`:

| Strategy | `{hot}` token | Cadence |
|----------|---------------|---------|
| **`RunDate`** (**default**) | the **Central** run date `yyyyMMdd` | re-pull **once per calendar day**; a second same-day run idempotently **skips**. |
| `RunId` | `context.RunId.ToString("N")` | re-pull on **every** invocation. |

`RunDate` is the default because Bidweek is a **monthly** feed — nothing arrives intra-day worth
re-probing more than once. **`RunHour` is deliberately NOT offered** (contrast IHSPointLogic,
whose hourly endpoints need it). If it is ever added, the hour token **must be UTC**, per
IHSPointLogic's DST rationale: `01:00` Central occurs twice on a fall-back night, so an
`yyyyMMddHH` Central token would repeat and the key would go backwards. At **date** granularity
the Central calendar date is monotonic non-decreasing across both DST transitions, so the
`RunDate` token is safe on the Central clock and stays consistent with the window (§7).

The `arm.FileLog` hub row is keyed **independently of the run token** (§6), so a daily hot
re-pull **upserts one stable hub row per issue date** (refreshing `LastCheckedUtc`, `HttpStatus`
and `RowCount`); the audit never fragments across runs.

### 3.4 Enumerate EVERY calendar day — no business-day / holiday filter (the evidence)

**Verified live 2026-08-21** (`docs/apis/NGI.md` §3.2). Weekdays are derived by calendar
arithmetic from 2026-01-01 = Thursday; the `200`/`404` results are live-observed:

| `issue_date` | Weekday | Result | Records |
|---|---|---|:--:|
| 2026-06-01 | Monday | `200` | 163 |
| 2026-07-01 | Wednesday | `200` | 163 |
| **2026-08-01** | **SATURDAY** | **`200`** | **163** |
| **2026-07-31** | **Friday** | **`404`** | — |
| 2026-08-14 / 08-15 / 08-20 / 08-21 | Fri / Sat / Thu / Fri | `404` | — |
| 2026-09-01 *(future)* | Tuesday | `404` | — |

Three conclusions, all load-bearing:

1. **The spec is wrong.** NGI declares *"Issue date will always be a business day"*, yet the
   August 2026 issue date is a **Saturday** and the business day immediately before it (Friday
   2026-07-31) **404s**. A weekday filter would have **silently missed the entire August issue** —
   no error, no warning, just a month of missing prices. **This is the single most important
   design rationale in this document.**
2. **The publication calendar is unenumerated** — not in the spec, not in the payload; the 404
   body only *guesses* (`"The date entered could be a weekend or holiday."`). There is no
   reliable way to predict which dates publish (§12 item 2).
3. **"Always the 1st of the month" is NOT established either** — only **three** issue dates were
   ever probed (§12 item 4). **Do not encode a day-of-month rule** any more than a weekday rule.

**Therefore: probe every calendar date in the window and let the `404` be the answer.** The cost
is one cheap request per non-publication day (~58 of 60 at the default), and the design is immune
to NGI's holiday calendar, to a change in the day-of-month convention, and to an off-cycle
special issue.

### 3.5 ⚠ Documented invariant: a settled-zone `404` is never re-probed — and the configuration hazard

**The invariant (do not "fix" this).** A `404` completes its work unit as **success** (§5.3).
`core.LoadLog` therefore records the key as done. In the **settled** zone the key is *stable*, so
**a settled-zone `404` is never re-probed, forever.** That is the correct and intended behaviour:
a month-old date that never published never will, and re-probing it every run for eternity is
pure waste. **Anyone tempted to make a `404` a failure, or to make settled keys re-probe, must
read §5.3 first** — with a 60-day window ~58 of 60 units legitimately `404`, so treating a `404`
as a failure would produce ~58 false failures per run and drown every real signal.

**Why it is harmless *as configured*.** With the user's `DaysBack = 60` and
`SettledAfterDays = 60`, the window is `[runDate−59 … runDate]`, so `ageDays ∈ [0 … 59]` and
**`ageDays > SettledAfterDays` is never true**. The settled zone is **empty**: the entire 60-day
window is **hot** and re-probed every run. (This exactly mirrors AGSI's inert `21/21` default —
the mechanism ships present but unused, ready for an operator who wants a cheap settled tail.)

> ### ⚠ Configuration hazard — the invariant becomes load-bearing the moment `SettledAfterDays` is lowered
>
> Set `SettledAfterDays = 7` (say) and dates aged 8–59 days get **stable** keys. A **transient**
> `404` on one of those dates — an NGI outage, a maintenance window, a partial deploy — is then
> recorded as a permanent success and **that issue is lost silently**.
>
> **Recommended safe floor: `SettledAfterDays >= 35`** — longer than one full monthly
> publication cycle, so a date is only frozen after the issue that would have covered it has
> definitively come and gone. **Safest of all is `SettledAfterDays >= DaysBack` (all-hot), which
> is the shipped default.**
>
> **Mitigation that always exists:** `arm.FileLog` keys on `(EndpointId, RepresentativeDate)`
> and records the **last** outcome per issue date including `HttpStatus = 404` and
> `RowCount = 0` (§6). So even for a frozen settled date the 404 is **auditable**:
> `SELECT … FROM arm.FileLog WHERE HttpStatus = 404` lists every never-published date. To
> re-drive one, delete its `core.LoadLog` row (or run once with a wider `SettledAfterDays`).
>
> **Backfill guidance.** A deep backfill via a large `DaysBack` (the crude lever — one request
> per day; `/bidweekHistoricalData.json` is the proper one, §12 item 8) should be run **first
> with `SettledAfterDays = DaysBack` (all-hot)**, verified against `arm.FileLog`, and only then
> narrowed. Note the deliberate consequence: at the default `60/60` a raised `DaysBack` alone
> (e.g. `3650`) *does* engage the settled zone for everything older than 60 days, which is the
> desired "load once, never re-probe" behaviour for genuine history.

---

## 4. Authentication — JWT bearer minted from `POST /auth` (IIR pattern, body-borne secret)

NGI uses a **JWT presented as `Authorization: Bearer <access_token>`**, minted from
credentials. No OAuth2 grant, no scope, **no usable refresh token**. Both data paths declare
`security: [{ jwtAuth: [] }]` and return `401` without it. `/auth` is the only unauthenticated
path in the 59-path spec.

### 4.1 Minting (`NgiTokenProvider.MintAsync`, over the un-authed `"NGI.Token"` client)

```
POST {BaseUrl}{AuthPath}                  // AuthPath default "/auth"
Content-Type: application/json
Accept: application/json

{"email": "<Loaders:NGI:Username>", "password": "<Loaders:NGI:Password>"}
```

> **⚠ The request field is `email`, NOT `username`.** The `NGITokenObtainPair` schema is
> `required: [email, password]`; a body posted with `username` **will not authenticate**. The
> platform setting is nonetheless called `Username` to match the `Loaders:<Id>:Username`
> convention — the mapping `Username → "email"` happens in the provider and must be covered by a
> unit test.

**Security rules (all mandatory, all differ subtly from IIR):**

| Rule | Why NGI differs from IIR |
|------|--------------------------|
| **NEVER log the `/auth` request body** — not at Trace, not in an exception message, not on a serialisation failure | **IIR's secret is in the mint URL; NGI's is in the BODY.** IIR's guard is "never log the URI"; NGI's is "never log the body". Both loaders must do both. |
| `.RemoveAllLoggers()` on **both** `"NGI.Token"` and `"NGI"` | The URL carries no secret here, so this is belt-and-braces + log-surface parity. Keep it anyway. |
| Never log the `Authorization` header, the `access_token`, or `refresh` | Same as IIR. |
| Log only a **fixed sanitised string** for the mint (`"NGI token minted (POST …/auth)"`) | Same as IIR. |
| Non-2xx from `/auth` → **throw**, message naming `core.Param(LoaderName='NGI', ParamName='Username'/'Password')`, **never the values** | Same as IIR. |

### 4.2 Token response — tolerant read, and **capture-and-ignore `refresh`**

Live `200` body shape: `{ "refresh": "<opaque ~200 chars>", "access_token": "<JWT>" }`.
The spec's `Token` schema declares **only** `access_token` — the live body returns **two**
fields, so a strict binding would break.

- **Never use `JsonUnmappedMemberHandling.Disallow`.** Ignore unknown members (STJ default).
- **Tolerant extraction, in order** (IIR's `ExtractToken` shape): body JSON property matching
  `access_token` / `accessToken` / `token` / `jwt` **case-insensitively** → else the response
  `Authorization` header → else any response header value starting `Bearer ` or `ey`. Strip a
  leading `Bearer `. If nothing yields a token → **throw** a clear error citing
  `docs/apis/NGI.md` §2.2.
- **`refresh` is captured-and-ignored.** Read it so a shape-strict binder can never trip on it,
  then **discard it immediately**: **there is NO refresh endpoint** anywhere in the 59-path spec
  (`/auth` is the only path under the `Authentication` tag). `refresh` must **never** be stored,
  cached, persisted, logged, or written to a fixture — **it is a credential**. Renewal is
  re-`POST /auth`.

### 4.3 Caching + the delegating handler (`NgiTokenAuthHandler`)

- `NgiTokenProvider` is a **singleton** → one mint per process (= per run). `GetTokenAsync`
  double-checks a `SemaphoreSlim` so the first of many concurrent callers mints and the rest
  await the same result. **Cached for the process lifetime** — the JWT's `exp − iat = 86400`
  (24 h) vastly exceeds any run, so proactive `exp`-based pre-expiry is **optional and not
  required**; the 401 re-mint is the correctness mechanism.
- `RefreshTokenAsync(staleToken, ct)` — under the lock, re-mint **only if** the cached token still
  equals `staleToken`; otherwise return the already-refreshed one. This is the
  **thundering-herd guard**: when many parallel requests `401` at once, exactly **one** mint
  happens.
- **`NgiTokenAuthHandler.SendAsync`:** stamp
  `request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token)` → send → if
  the response is **`401`**, dispose it, `RefreshTokenAsync(staleToken)`, re-stamp, and replay
  **once**. A **second consecutive `401` propagates** (a genuine credential failure, not an
  expiry) and the reader turns it into a thrown unit failure (§5.4).
- **Replay is trivially safe:** both data calls are `GET`s with **no request body**.
- The handler is registered **INNER of the retry policy** (so it re-stamps on every retry
  attempt) and **OUTER of the throttle**. **`401` is excluded from the Polly policy** precisely
  so the handler owns it (§1.4).

### 4.4 Throttle & retry

`NgiRateLimiter` (singleton) + `NgiRateLimitingHandler` pace **all** NGI traffic (both clients)
at `RequestsPerSecond`. NGI publishes **no** rate limit, no `Retry-After`, no `X-RateLimit-*`
header, and no `429` was ever observed in ~11 probe calls (§12 item 9) — so pace conservatively
(default 2 rps) and back off on `429`. `NgiHttpPolicy.Build(retryCount, retryDelayMs, logger)`
retries `429`/`5xx`/transient with exponential backoff, honours `Retry-After` when present, and
**does not retry** `400`, `401`, `403` or `404`.

---

## 5. Source readers (custom `ISourceReader`, JSON, tolerant)

Both readers implement `ISourceReader<TUnit,TRow>.ReadAsync` **directly** — they do **NOT** use
`HttpJsonSourceReaderBase`, because that base calls `EnsureSuccessStatusCode()` and would throw
on the `404` that is NGI's **majority** outcome. They use the registered `"NGI"` client (auth +
retry + throttle all applied by handlers), parse with `System.Text.Json` +
`CultureInfo.InvariantCulture`, write their own `arm.FileLog` outcome row (they hold the HTTP
status + row count) and stamp the returned `FileLogId` onto every produced row.

### 5.1 `NgiParse` — the tolerant value readers (shared)

Per the platform's tolerant-JSON convention: **accept multiple candidate property
names/casings; degrade an unrecognised or unparseable field to NULL rather than failing the run;
drop-and-count only a record whose KEY is unusable.**

| Helper | Rule |
|--------|------|
| `Prop(JsonElement, params string[] candidates)` | Returns the first present property matching any candidate **case-insensitively**. Used because **five record field names contain SPACES** — `"Point Code"`, `"Issue Date"`, `"Survey Start"`, `"Survey End"`, `"Pricing Point"` — which **no** `JsonNamingPolicy` will ever produce. Candidate sets: `"Point Code"`/`"PointCode"`/`"point_code"`, `"Issue Date"`/`"IssueDate"`/`"issue_date"`, `"Survey Start"`/`"SurveyStart"`/`"survey_start"`, `"Survey End"`/`…`, `"Pricing Point"`/`…`, and `"Bidweek Locations"`/`"BidweekLocations"`/`"bidweek_locations"`. **Bind by NAME, never by position** — JSON object order is not a contract. |
| `IsNullSentinel(string?)` | `true` for `null`, blank, or (trimmed, **case-insensitive**) `"None"`. Defensively also `"N/A"`, `"null"`, `"-"`. **The observed sentinel is the literal 4-char string `"None"`, not JSON `null`** — it must map to SQL `NULL`. |
| `Str(...)` | Trimmed; sentinel → `null`. |
| `Dec(...)` | Sentinel → `null`. Else `decimal.TryParse(NumberStyles.Float \| AllowLeadingSign, InvariantCulture)`. **A leading minus MUST parse — negative gas prices are real.** Unparseable → `null` + a counted warning. |
| `Int(...)` | Sentinel → `null`. Else `int.TryParse(NumberStyles.Integer \| AllowThousands, InvariantCulture)`; if that fails, try `decimal` and accept it only when it has no fractional part. Else `null` + a counted warning. |
| `Date(...)` | Sentinel → `null`. Else `DateOnly.TryParseExact("yyyy-MM-dd", InvariantCulture)`. Unparseable → `null` + a counted warning. |

**Every value in both payloads is a JSON string** — including all numerics (`"2.360"`, `"240"`)
and all dates. There is **not one** JSON number, boolean or `null` anywhere in either fixture. So
bind the payload as strings (`Dictionary<string, Dictionary<string,string>>` or `JsonElement`
navigation) and convert in the reader. **Never use the ambient culture.**

### 5.2 `NgiLocationsSourceReader` (endpoint 2 — `GET /bidweekLocations?format=json`)

1. `GET {BaseUrl}/bidweekLocations?format=json`. Send `format=json` **explicitly** — do not rely
   on the server default. Log only the sanitised path.
2. `httpStatus = (int)response.StatusCode`. Dispatch per §5.4.
3. **`200`:** locate the single top-level node via `Prop(root, "Bidweek Locations",
   "BidweekLocations", "bidweek_locations")` — note the **space** and the **capital L**.
   - node **absent**, or present but **not a JSON object** → `FileLog "Failed"` then **THROW**
     ("locations envelope shape drift"). A silent zero-row success here is exactly the failure
     mode `docs/apis/NGI.md` was written to prevent.
   - node is an **empty object** → `FileLog "NotAvailable"`, return **zero rows**, and log a
     **warning** (unexpected: NGI 404s rather than returning an empty set).
4. For each property of the node:

   > ## 🛑 **THE MAP RUNS NAME → CODE. THE JSON KEY IS THE NAME; THE VALUE IS THE POINT CODE.**
   > ```
   >     "Agua Dulce"   :   "STXAGUAD"
   >      ^^^^ KEY = LocationName        ^^^^ VALUE = PointCode
   > ```
   > Inverting this produces **no exception, no parse error and no warning** — just 163 rows with
   > the name in `PointCode` and the code in `LocationName`, after which every join silently
   > misses. **Mnemonic: codes are UPPERCASE and unspaced; the uppercase side is always the
   > VALUE.** A `CODE_TESTER` unit test against
   > `tests/DataLoader.NGI.Tests/Samples/bidweekLocations.json` asserting
   > `PointCode == "STXAGUAD"` **and** `LocationName == "Agua Dulce"` is **mandatory** — it is the
   > only regression net (the vendor spec declares "No response body" for this endpoint).

   `LocationName = property.Name` (trimmed), `PointCode = property.Value` (trimmed, string).
   **Drop-and-count** any entry whose `PointCode` is blank (it is the merge key). Emit
   `BidWeekLocationRow(FileLogId, PointCode, LocationName)`.
5. **Dedup on `PointCode`** (last wins) before returning — belt-and-braces; the sink and the proc
   dedup too. Log a counted warning per collision (a duplicate code would mean NGI renamed one
   code onto another's name).
6. `FileLog` outcome: `rows.Count == 0 ? "NotAvailable" : "Success"`; context
   `NgiFileContext(Endpoint: "BidWeekLocations", RepresentativeDate: null)`. Stamp the returned
   `FileLogId` onto every row.
7. **The merge is upsert-only — it NEVER deletes.** A retired point code stays in
   `arm.BidWeekLocation`; a truncated snapshot can never wipe the table. Stale codes surface as
   an **informational** `usp_ValidateLoad` check (§9), never a delete.

### 5.3 `NgiBidWeekSourceReader` (endpoint 1 — `GET /bidweekDatafeed.json?issue_date=…`)

1. Build `{BaseUrl}/bidweekDatafeed.json?issue_date={IssueDate:yyyy-MM-dd}` (**invariant**
   format — a wrong format is a `400`, §5.4). Log only the sanitised path.

   > **⚠ NEVER call the parameterless `bidweekDatafeed.json`.** Omitting `issue_date` returns
   > *"the latest issue"*, which is **non-deterministic**: the work-unit `Key` could not encode
   > what was actually fetched, breaking the `core.LoadLog` idempotency contract (two runs with
   > the same key could return different issues; a settled key would pin the wrong issue
   > forever). **Today's window already covers the latest issue date**, so the parameterless form
   > buys nothing. It is a one-off human diagnostic only.

2. `GET`. `httpStatus = (int)response.StatusCode`. Dispatch per §5.4.
3. **`200`:** read `meta` tolerantly (`issue_date`/`start_date`/`end_date` + camel/Pascal
   variants) and locate `data`.
   - `data` present but **NOT a JSON object** (an array, string or number) → `FileLog "Failed"`
     then **THROW** ("datafeed `data` shape drift — expected an object keyed by point code").
     **This guard is load-bearing**: `data` is a **map**, not an array, and a "tolerant" reader
     that quietly yielded zero records from an array would report a successful empty load.
   - `data` **absent** or an **empty object** → `FileLog "NotAvailable"`, return **zero rows**,
     log a **warning** (unexpected — NGI 404s for no publication).
4. For each property of `data` (`mapKey` = property name, value = the record object):
   - **`PointCode`** ← `Prop(record, "Point Code", "PointCode", "point_code")`, trimmed.
     - blank → **fall back to `mapKey`** with a counted `MapKeyFallback` warning (the key equals
       `Point Code` in **all 163** observed records, so it is a sound fallback).
     - blank **and** `mapKey` blank → **DROP the record and count it** (unkeyable).
     - `mapKey != PointCode` → counted `MapKeyMismatch` warning; **persist the record's
       `Point Code`**, never the key.
   - **`IssueDate`** ← record `"Issue Date"` → else `meta.issue_date` → else **the requested
     `IssueDate`** (which always exists). Log a counted `IssueDateMismatch` warning if the
     resolved value differs from the requested date. It is a PK component, so an unresolvable
     value would mean **drop-and-count** (unreachable in practice given the request fallback).
   - **`SurveyStart` / `SurveyEnd`** ← record `"Survey Start"`/`"Survey End"` → else
     `meta.start_date`/`meta.end_date` → else **NULL** + a counted warning.
     > **Read them, never compute them.** Both the offset from the issue date **and** the window
     > length vary month to month (offset 5 / 7 / 10 days; length 3 / 3 / 6 days across the three
     > probed issues). A hard-coded offset would have been wrong in **2 of 3** observed months.
   - **`Region`** ← record `"Region"`. **Take it from the field, never derive it from
     `PointCode`** — the code prefix is a legacy artefact and frequently disagrees (`NEALEB` is
     `Midwest`; `MCWNIAGR` is `Northeast`; `SLAFGTZ3` is `Southeast`; `ETXTGT` is
     `North Louisiana/Arkansas`). Unrecognised value → **persist as published**; the 14 observed
     values are **not** an enum and must never gate the load.
   - **`PricingPoint`** ← record `"Pricing Point"`.
   - **`Low` / `High` / `Average`** ← `NgiParse.Dec` (`"None"` → NULL; signed).
     `Average` is **not** the `Low`/`High` midpoint (it is deal-weighted) — never compute it.
   - **`Volume` / `Deals`** ← `NgiParse.Int` (`"None"` → NULL).
   - Emit `BidWeekDataRow(FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd, Region,
     PricingPoint, Low, High, Average, Volume, Deals)`.
5. **Dedup on `(IssueDate, PointCode)`** (last wins) before returning — structurally impossible
   from a JSON object (duplicate keys cannot be expressed) but cheap insurance; the sink and the
   proc dedup identically.
6. `FileLog` outcome: `rows.Count == 0 ? "NotAvailable" : "Success"`; context
   `NgiFileContext(Endpoint: "BidWeekData", RepresentativeDate: unit.IssueDate)`. Stamp
   `FileLogId` onto every row.
7. Log the drop/warning counters at the end of the unit
   (`dropped=…, mapKeyFallback=…, mapKeyMismatch=…, unparseableNumeric=…`) — the platform's
   dropped-and-counted convention.

### 5.4 Error / status matrix (both readers)

| Status | Meaning | `arm.FileLog` | Work unit | Retry? |
|:---:|---|---|---|---|
| **`200`** | Publication exists; body is the complete result set (no paging). | `Success` (or `NotAvailable` when zero rows survive) | **succeeds** | — |
| **`404`** | **NO PUBLICATION ON THIS DATE — THE NORMAL CASE.** Body: `{"msg":"Datafeed not found. The date entered could be a weekend or holiday."}` | `NotAvailable`, `HttpStatus = 404`, `RowCount = 0` | **SUCCEEDS with zero rows** | **never** |
| **`400`** | Malformed `issue_date`. Body: `{"msg":"Incorrect date format for issue_date, should be YYYY-MM-DD"}` | `Failed`, `HttpStatus = 400` | **THROWS** | **never** |
| **`401`** | Missing/invalid/expired bearer token. | `Failed`, `HttpStatus = 401` (only if it reaches the reader) | handler re-mints **once** and replays; a **second** `401` reaches the reader → **THROWS** | handler only, once |
| **`403`** | Entitlement/subscription. | `Failed` | **THROWS** | never |
| **`429` / `5xx`** | Throttle / server. | `Failed` after Polly exhausts retries | **THROWS** (unit fails, **run continues**) | yes (Polly, `Retry-After` honoured) |
| **`200` + shape drift** | `data` (or the locations node) present but not a JSON object. | `Failed` | **THROWS** ("shape drift") | never |
| cancellation | | **no FileLog write** (spent token) — `core.LoadLog` records cancellation | rethrow `OperationCanceledException` | — |

**`404` is a successful, empty read — this is the single biggest operational fact about the
feed.** Bidweek is **monthly**, so over a 60-day window **~58 of 60 units legitimately `404`**. A
loader (or a `usp_ValidateLoad` rule, or an on-call alert) that treats `404` as an error produces
~58 false failures per run and drowns the real signal. Same 404-tolerant posture as
CWG/AGSI/StormVista — but here it is the **majority** outcome rather than an edge case. See §3.5
for the invariant this creates.

**`400` MUST throw — never swallow it as `NotAvailable`.** A `400` can only mean the loader
formatted the date wrong: **it is a caller bug**, and it is unfixable-by-retry. Because every
unit formats identically, a `400` fails every unit and the pipeline's `Success = false` — which
is the intended loudness.

**Exception discipline (both readers):** `catch (OperationCanceledException) → throw;` (no
FileLog write on a spent token). `catch (Exception)` → best-effort `FileLog "Failed"` with
`CancellationToken.None`, then **rethrow**, so `LoaderPipelineBase` records the `core.LoadLog`
failure and the run continues to the next unit (**fail-a-block-not-the-run**).

---

## 6. FileLog flow (`arm.FileLog` hub)

Reuse the CWG/StormVista/AGSI hub posture: **one row per endpoint pull per run**
(`Success` / `NotAvailable` / `Failed`) for **every** request, with `FileLogId` stamped onto the
fact rows it produced. Natural key:

```
UNIQUE (EndpointId, RepresentativeDate)     -- SQL NULL-equality collapses the undated Locations row
```

| Endpoint | `RepresentativeDate` |
|----------|----------------------|
| `BidWeekLocations` | **NULL** (undated snapshot) → **one stable hub row**, upserted each run |
| `BidWeekData` | the requested **issue date** → one hub row per date, refreshed on each hot re-pull |

> **⚠ Divergence from AGSI: the `Region` axis is DROPPED.** AGSI's hub key is
> `(EndpointId, RegionId, RepresentativeDate)` because its request axis *is* the country. **NGI
> has no region request axis** — one request returns all regions. Worse, keeping the column would
> make `arm.FileLog.RegionId` (an audit axis) sit next to `arm.BidWeekData.Region` (a **payload**
> column) under the same name, meaning two unrelated things. **Do not create `arm.Region`, do not
> add `@Region` to `arm.usp_UpsertFileLog`, and do not put a `Region` slot in `NgiFileContext`.**

- `NgiFileContext(string Endpoint, DateOnly? RepresentativeDate, string RequestPath)` — AGSI's
  `AgsiFileContext` minus the `Region` slot (and, like AGSI, without CWG's `Variant`).
- `INgiFileLog.UpsertAsync(file, status, httpStatus, requestPath, rowCount, ct) → FileLogId`.
  `SqlNgiFileLog` calls `arm.usp_UpsertFileLog` under
  `SqlWriteGate.AcquireAsync(KeyFor(ConnectionString, "arm.usp_UpsertFileLog"))` (one shared key
  across both endpoints — a fast single-row upsert, so serialising it is cheap and
  deadlock-proof), passes **explicitly typed** parameters (no implicit `NVARCHAR→VARCHAR`
  conversion) and reads the returned scalar `FileLogId` — the `SqlAgsiFileLog` shape exactly.
- Normalise `Endpoint` and `Status` to seeded surrogate-Id lookups (`arm.Endpoint`
  `{BidWeekLocations, BidWeekData}` with their paths; `arm.Status`
  `{Success, NotAvailable, Failed}`), with `usp_UpsertFileLog` keeping its **name-string**
  signature and resolving names → Ids server-side (CWG/AGSI's resolved posture). Inline
  `VARCHAR + CHECK` is an acceptable build-only alternative — DATABASE_DEVELOPER's call (§13).
- `RequestPath` is **sanitised** (path + `issue_date`/`format` only). No credential appears in
  any NGI URL, but the field stays sanitised on principle.
- **`arm.FileLog` is the audit surface that makes "no publication that day" visible rather than
  invisible**, and it is the mitigation for the settled-`404` invariant (§3.5).

---

## 7. Timezone basis — **US Central**

The run-date basis is the **US Central** calendar date (`America/Chicago`, Windows
`"Central Standard Time"`), resolved once in a static `NgiTime` initialiser that tries **both**
ids and falls back to UTC with a `UsingUtcFallback` flag the module warns on at run start (§1.5)
— AGSI's `AgsiTime.ResolveCet` shape.

- `runDate = CentralToday(context.StartedAtUtc)`; `ageDays` and the `RunDate` `{hot}` token are
  computed on this date, so a run straddling UTC midnight cannot target a not-yet-existent
  future issue date, and the daily hot re-pull key flips on one consistent boundary.
- **Why Central, not the vendor's clock:** house style (IHSPointLogic schedules in US Central) and
  this is a **US** gas market whose day boundary the business already runs on. NGI publishes no
  timezone anywhere in the API — every date in both payloads is a bare `YYYY-MM-DD` with **no
  time component at all**, which is exactly why all three date columns are `DATE`, not
  `DATETIME2`.
- **DST safety:** at **date** granularity the Central calendar date is monotonic non-decreasing
  across both transitions, so a `RunDate` `{hot}` token can never go backwards. **This holds only
  at date granularity** — see §3.3 for why an `yyyyMMddHH` Central token would be unsafe and must
  be UTC if `RunHour` is ever added.
- **Contrast (documented on purpose):** AGSI uses **CET/Europe** (European gas day), CWG uses
  **US Eastern**, StormVista uses **UTC**, IIR stamps its `OfflineEvent` `RunDate` in **US
  Central**. NGI matches IIR.

---

## 8. Full-field mapping

Types and nullability follow `docs/apis/NGI.md` §4.4 / §5.2 / §7.2, with the two deliberate
deviations noted below. Every row carries `FileLogId` (provenance; stamped in §6, **UPDATEd on
MERGE, never a merge-key column**) and the table stamps `ModifiedAtUtc` in the proc (**it never
crosses the TVP**). "Key" marks the MERGE natural key.

### 8.1 Endpoint 2 → `arm.BidWeekLocation` (PK / merge key `PointCode`)

| Source | Row prop | Column | Type | Null? | Notes |
|--------|----------|--------|------|-------|-------|
| JSON object **value** | `PointCode` | `PointCode` | `VARCHAR(20)` | No | **Key.** Observed max len 12; 0 duplicates across 163 entries. ASCII-only observed → `VARCHAR`. |
| JSON object **key** | `LocationName` | `LocationName` | `VARCHAR(100)` | No | Observed max len 33 (`SoCal Border - Kern River Station`). Contains `& - , . / ( )`; ASCII-only observed. |
| stamped | `FileLogId` | `FileLogId` | `INT` | Yes | provenance FK → `arm.FileLog(Id)`; UPDATEd on match |
| table | — | `DateCreated` | `DATETIME` `DEFAULT GETDATE()` | No | second column |
| proc | — | `ModifiedAtUtc` | `DATETIME2(3)` | No | stamped by the MERGE; **not in the TVP** |

> **⚠ Stated deviation from the dimension-table convention.** The convention is `Id INT
> IDENTITY(1,1)` PK **first** with the natural key as a `UNIQUE` constraint. **The user pinned
> `PK (PointCode)`**, and because there is **no FK** into this table (§8.2) **nothing needs a
> surrogate Id**. So: **PK on `PointCode`, no surrogate `Id`.** `DateCreated` still leads the
> payload and `ModifiedAtUtc` still trails. A hybrid (`Id IDENTITY UNIQUE` + PK on `PointCode`)
> is available if DATABASE_DEVELOPER wants the template byte-identical — §13 item 2.

This endpoint returns **only these 2 fields**. There is **no** region, state/province/country,
**no latitude/longitude or any coordinate** (so **no `FLOAT` coordinate columns and no
`GEOGRAPHY` column** — contrast IIR's `PlantPoint`), no pipeline/operator metadata, no
active flag, no first/last-published date, no unit of measure, no sort order, and **no
parent-aggregate linkage**. **Do not model columns the API cannot fill.**

### 8.2 Endpoint 1 → `arm.BidWeekData` (PK / merge key `(IssueDate, PointCode)`)

| # | JSON field | Row prop | Column | Type | Null? | Notes |
|:-:|---|---|---|---|---|---|
| — | *(stamped)* | `FileLogId` | `FileLogId` | `INT` | Yes | provenance FK → `arm.FileLog(Id)`; **first TVP column**; UPDATEd on match, **never** a merge key |
| 1 | `Issue Date` | `IssueDate` | `IssueDate` | `DATE` | **No** | **Key.** = `meta.issue_date` = the requested `issue_date` (all 163 verified). `DATE`, not `DATETIME2` — no time component exists in this feed. |
| 2 | `Point Code` | `PointCode` | `PointCode` | `VARCHAR(20)` | **No** | **Key.** Max len 12 observed; equals the `data` map key for all 163. |
| 3 | `Survey Start` | `SurveyStart` | `SurveyStart` | `DATE` | Yes † | = `meta.start_date`. **Read, never compute** (§5.3). |
| 4 | `Survey End` | `SurveyEnd` | `SurveyEnd` | `DATE` | Yes † | = `meta.end_date`. |
| 5 | `Region` | `Region` | `Region` | `VARCHAR(64)` | Yes † | Max len 24 (`West Texas/SE New Mexico`); 14 distinct values observed. **Free text, NOT an enum — no lookup table, no `CHECK`.** **Not derivable from `PointCode`.** |
| 6 | `Pricing Point` | `PricingPoint` | `PricingPoint` | `VARCHAR(100)` | Yes † | Max len 33; equals the locations name for all 163 codes. |
| 7 | `Low` | `Low` | `[Low]` | `DECIMAL(13,6)` | Yes | 3 dp observed; scale 6 is grain headroom and the 7 integer digits are price-spike headroom (Feb-2021 gas printed four figures at some hubs). `"None"` in 45/163. **`DECIMAL`, not `FLOAT`** — a published price must round-trip exactly. **SIGNED — NO non-negative `CHECK`.** |
| 8 | `High` | `High` | `[High]` | `DECIMAL(13,6)` | Yes | `"None"` in 45/163. May **equal** `Low` on thin points (zero-width range is valid). |
| 9 | `Average` | `Average` | `[Average]` | `DECIMAL(13,6)` | Yes | `"None"` in 45/163. **Deal-weighted — NOT the `Low`/`High` midpoint.** Never compute it. |
| 10 | `Volume` | `Volume` | `[Volume]` | `INT` | Yes | `"None"` in 47/163. Max 4 digits (`8647`). `INT` (not `SMALLINT`: 32 767 is reachable; `TINYINT` banned by repo convention). **Unit undocumented** (§12 item 7). |
| 11 | `Deals` | `Deals` | `Deals` | `INT` | Yes | `"None"` in 47/163. Max 4 digits (`1663`). |
| — | table | — | `DateCreated` | `DATETIME` `DEFAULT GETDATE()` | No | **leads the table** (fact-table shape) |
| — | proc | — | `ModifiedAtUtc` | `DATETIME2(3)` | No | stamped by the MERGE; **not in the TVP** |
| — | `meta.issue_date` / `start_date` / `end_date` | — | — | — | — | **Not persisted** — fully redundant with the record fields (verified all 163). Used as a **fallback source** (§5.3) and a validation assertion (§9). |

† **Stated deviation from `docs/apis/NGI.md` §7.2**, which recommends `NOT NULL` for
`SurveyStart`/`SurveyEnd`/`Region`/`PricingPoint` (never null in 163/163). **This design makes
those four NULLable** so the platform's tolerant-parse contract is expressible end-to-end:
**only an unusable KEY drops a record; every other field degrades to NULL.** The API doc's own
finding is the reason — NGI publishes **no response contract** ("No response body" in the spec),
so any NGI-side rename is **silent**, and dropping ~163 priced rows because a field was renamed
is far worse than storing four NULLs and raising a validation warning. `arm.usp_ValidateLoad`
asserts **0 NULLs** in all four (§9, `UnexpectedNulls`). `IssueDate` and `PointCode` are `NOT
NULL` structurally (PK). DATABASE_DEVELOPER to confirm — §13 item 3.

**Decisions recorded (do not re-litigate):**

- **`Region` and `PricingPoint` stay DENORMALISED INLINE on the fact** — a deliberate deviation
  from the "repeated per-entity strings normalise out into a dimension" convention, for four
  reasons: (a) **`Region` is not available from the locations endpoint AT ALL**, so the dimension
  physically cannot supply it; (b) volume is tiny — **~163 rows/month, ~2 000/year** — this is
  **not** a "high-volume fact"; (c) the user specified **exactly two tables**; (d) inlining
  preserves the **as-published** values if NGI ever re-classifies or renames a point.
- **NO FK `arm.BidWeekData.PointCode → arm.BidWeekLocation.PointCode`.** The 163-code sets match
  **today**, but that is a **same-day observation, not a contract** (§12 item 3): codes drift as
  NGI adds/retires points, and because the two pipelines are **independent** their arrival order
  is not guaranteed — a new code can legitimately appear in a datafeed before the locations
  snapshot refreshes. An FK would fail the fact load on a perfectly valid response. Reconciled
  **observationally, both ways**, in §9 — **warn, never error**.
- **In-band aggregate rows are persisted as ordinary rows.** The feed mixes granular points with
  regional averages (`*RAVG`, `SEREGAVG`, `APPREGAVG`), a sub-aggregate (`CALSAVG`) and the
  national average (`USAVG`) — and there is **no flag field** distinguishing them. Persist all of
  them; **any downstream aggregation over this table will double-count** unless it excludes them.
  Recorded here and surfaced as an informational check (§9). Note `Arizona/Nevada` had **no**
  regional-average row in the observed issue, so "every region has an average" is **false**.
- **`USAVG` carries `Region = "California"`** (Quirk B) — a vendor artefact. **Persist as
  published; never correct it in the loader.** Flagged informationally in §9.
- **Distinct points legitimately share identical prices** (pooled points: `WTXEPKPWP`/`WTXEPP`/
  `WTXEPKEY`; `CALSSOCAL` ≡ `CALSAVG`; `RMTCHEY` ≡ `RMTCHEYOTH`). A "duplicate row" rule keyed on
  **values** rather than on `(IssueDate, PointCode)` would false-positive — do not write one.

### 8.3 TVP / column-order contract (load-bearing)

The TVP binds **BY POSITION**, so a silent reorder corrupts every loaded row. These orders MUST
be mirrored **identically** across the `001` table body, the `002` TVP type, the `003` merge
proc's `SELECT`/`INSERT`/`UPDATE` lists, and each C# sink's `BuildTable` (plus its unit test).
**`FileLogId` is FIRST in both TVPs** — note this deliberately does **not** copy AGSI, whose
entity TVP puts `FileLogId` **last**; the cross-loader convention is "`FileLogId` first where
present", and NGI follows the convention.

**`arm.BidWeekLocationTvp` — 3 columns:**
```
FileLogId, PointCode, LocationName
```

**`arm.BidWeekDataTvp` — 12 columns:**
```
FileLogId, IssueDate, PointCode, SurveyStart, SurveyEnd, Region, PricingPoint,
Low, High, Average, Volume, Deals
```

- `ModifiedAtUtc` and `DateCreated` **never cross the TVP** — the table default / MERGE proc owns
  them.
- Both sinks derive from `NgiSqlSinkBase<TRow> : SqlSinkBase<TRow>` (setting
  `StoredProcedureName`, `TableValuedParameterType`, `GetConnectionString`,
  `ProcedureReturnsRowCount = true`) and **de-dup the batch on the merge key** (`PointCode`;
  `(IssueDate, PointCode)`) before building the DataTable.
- **`SqlSinkBase` passes the TVP as `@Records`** — both merge procs must name their TVP parameter
  **`@Records`** (AGSI's convention; IIR's `@Rows` needed a custom sink base, which NGI does not
  use).
- C# DataTable column CLR types: `int`, `DateTime` (for `DATE`), `string`, `decimal`, `int` —
  nullables via `DbNullable`/`NullIfEmpty`.

---

## 9. Post-load validation — `arm.usp_ValidateLoad` + `NgiLoadValidator`

`LoaderPipelineBase` has no post-load hook and `NgiModule` owns orchestration, so — following the
StormVista/AGSI/IIR precedent — **`NgiLoadValidator`** runs as a module-level step in `RunAsync`
**after both pipelines complete** (§1.5 step 6), calling
`arm.usp_ValidateLoad(@DateFrom DATE, @DateTo DATE)` with the window from
**`NgiTime.ResolveWindow`** (the same call the provider makes — §3.2, so the validation window
can never drift from the load window).

`arm.usp_ValidateLoad` returns **ONE** result set with the uniform shape
`CheckName, Scope, ExpectedCount, ActualCount, Detail`. `NgiLoadValidator` treats a row with a
non-NULL `ExpectedCount` that differs from `ActualCount` as an **anomaly → `LogWarning`**, and
every other row as **informational → `LogInformation`**, then logs a final summary
(`N anomaly row(s), M informational row(s)`).

> **⚠ IT IS OBSERVATIONAL AND MUST NEVER THROW.** It catches and logs its own exceptions
> (`LogError`, non-fatal) and only rethrows `OperationCanceledException`. With ~58 of 60 units
> legitimately `404`, a legitimately sparse window must not fail an otherwise-good load.

### 9.1 Check catalogue

| # | `CheckName` | Scope | `ExpectedCount` | Meaning / rationale |
|:-:|---|---|:--:|---|
| 1 | `LocationRowCount` | — | **NULL** (info) | total rows in `arm.BidWeekLocation`. Detail: observed baseline `163` **as a snapshot, not a contract**. |
| 2 | `LocationBlankKey` | — | **0** | rows with blank/NULL `PointCode` or `LocationName`. |
| 3 | `IssueDateRowCount` | `IssueDate=yyyy-MM-dd` | **NULL** (info) | per-issue-date row count in the window. Detail carries the observed baseline `163`. **`ExpectedCount` stays NULL on purpose** — 163 is a same-day snapshot and NGI adding/retiring a point must not raise an anomaly. |
| 4 | `IssueDateRowCountOutOfBand` | — | **0** | count of issue dates in the window whose row count falls outside **±20 %** of `COUNT(*) FROM arm.BidWeekLocation`. **Flags a JUMP, not the level.** If `arm.BidWeekLocation` is empty (e.g. `BidWeekData`-only runs), emit the row as informational with `Detail = 'location table empty — band check skipped'` so it cannot false-positive. |
| 5 | `PriceRangeOrdering` | — | **0** | rows violating `Low <= Average <= High` **where all three are non-NULL** (verified 0 violations in the live sample). **⚠ Do NOT add a non-negative price check — negative gas prices are real** (Waha has printed negative cash prices). Put that sentence in the SQL comment. |
| 6 | `SurveyWindowOrdering` | — | **0** | rows violating `SurveyStart <= SurveyEnd < IssueDate` where all three are non-NULL. (Observed offsets 5/7/10 days, so strict `<` on the issue date is safe.) |
| 7 | `UnexpectedNulls` | — | **0** | rows in the window with NULL/blank `SurveyStart`, `SurveyEnd`, `Region` or `PricingPoint`. Detail notes that `IssueDate`/`PointCode` are `NOT NULL` by the PK, making **six** never-null-observed columns in total (§8.2 †). |
| 8 | `FactCodesMissingFromLocation` | — | **NULL (INFORMATIONAL ONLY)** | distinct `PointCode`s in the window absent from `arm.BidWeekLocation`. Detail lists a sample. **⚠ NEVER an error** — the two pipelines are independent and code sets legitimately diverge (§8.2). SQL comment must say so. |
| 9 | `LocationCodesMissingFromFact` | `IssueDate=<latest in window>` | **NULL (INFORMATIONAL ONLY)** | the inverse, scoped to the latest issue date in the window (whole-window scoping would be noise). Surfaces retired codes the upsert-only merge never purges (§5.2 step 7). |
| 10 | `IssueDatesWithZeroRows` | — | **NULL (info)** | `(DATEDIFF(DAY,@DateFrom,@DateTo)+1) − COUNT(DISTINCT IssueDate)`. **Expected to be ~58 of 60 — a monthly feed over a 60-day window.** No tally table needed. **⚠ NEVER an error.** |
| 11 | `PriceNullRate` | — | **NULL (info)** | rows with `Average IS NULL`. Detail: percentage + observed baseline **27.6 %** (45/163). Watch for a *jump*, not the level. |
| 12 | `ActivityNullRate` | — | **NULL (info)** | rows with `Volume IS NULL`. Detail: baseline **28.8 %** (47/163). |
| 13 | `PricedWithoutActivity` | — | **NULL (INFORMATIONAL ONLY)** | rows with `Average IS NOT NULL AND Volume IS NULL`. **⚠ Quirk A (verified): exactly 2 of 163 (`STXAGUAD`, `CALSPGE`) are priced with NULL `Volume`/`Deals`** — the 45 price-null rows are a **strict subset** of the 47 activity-null rows. A "price implies volume" rule would false-positive: an assessed/rolled-up price with no reported deals is legitimate. **The SQL comment must say `-- do NOT set ExpectedCount = 0 here`.** |
| 14 | `UsavgRegionQuirk` | `PointCode=USAVG` | **NULL (INFORMATIONAL ONLY)** | count of `USAVG` rows in the window; Detail = the distinct `Region` value(s) they carry. **⚠ Quirk B (verified): the national aggregate `USAVG` ("National Avg.") carries `Region = "California"`** — a vendor data quirk. **Persist as published; NEVER correct it.** A `GROUP BY Region` would attribute the U.S. national average to California. |
| 15 | `RegionValueSet` | — | **NULL (info)** | distinct `Region` values in the window; Detail = how many are outside the 14 observed values, plus a sample. **Report a new region, never fail on it** — `Region` is not an enum in any vendor document. |
| 16 | `AggregateRowsPresent` | — | **NULL (info)** | rows whose `PointCode` matches the known aggregate set (`%RAVG`, `SEREGAVG`, `APPREGAVG`, `CALSAVG`, `USAVG`). Detail warns that downstream aggregation double-counts unless these are excluded (§8.2). |
| 17 | `IssueDateMatchesRequest` | — | **0** | `arm.BidWeekData` joined to `arm.FileLog` on `FileLogId`, counting rows where `IssueDate <> RepresentativeDate`. Asserts the record/`meta`/request agreement the reader falls back through (§5.3). |
| 18 | `FileLogOutcomeSummary` | `Status=<name>` | **NULL (info)** | `arm.FileLog` row counts per status over the window — lets an operator read `NotAvailable = 58, Success = 2` at a glance, which is the audit answer to "did those dates really not publish?" (§3.5). |
| 19 | `DuplicateLocationName` | — | **NULL (info)** | `LocationName`s attached to more than one `PointCode`. Structurally impossible within one snapshot (the name is a JSON key) but possible across snapshots after an NGI rename. |

Argument validation mirrors AGSI: `RAISERROR` if `@DateFrom`/`@DateTo` is NULL or
`@DateFrom > @DateTo`.

**Build-only:** the validator and the proc are written and unit-testable but **not exercised
against live data** this pass; the deeper reconciliation is the deferred `DATA_QUALITY_VALIDATOR`
step (which needs a live, loaded database).

---

## 10. Config surface — `NgiSettings : LoaderSettingsBase`

Inherited from `LoaderSettingsBase`: `ConnectionString`, `MaxConcurrentWorkUnits`, `RetryCount`,
`RetryDelayMs`, `WorkUnitTimeoutSeconds`. Added:

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `BaseUrl` | string | `https://api.ngidata.com` | API host. **No version segment, no `basePath`** (the spec has no `servers` block). |
| `AuthPath` | string | `/auth` | mint path, appended to `BaseUrl` (§4.1). |
| `Username` | string | **`"SEE_DB"`** | the account **e-mail**; resolved from `core.Param(LoaderName='NGI', ParamName='Username')` by `AddLoaderSettings`, or env `DATALOADER_Loaders__NGI__Username`. **Sent as the JSON body field `email` — NOT `username`** (§4.1). **Never logged, never in `appsettings.json`.** |
| `Password` | string | **`"SEE_DB"`** | resolved from `core.Param(LoaderName='NGI', ParamName='Password')`, or env `DATALOADER_Loaders__NGI__Password`. **Never logged, never in `appsettings.json`, never in a test fixture.** |
| `HttpTimeoutSeconds` | int | `60` | per-request timeout on both clients. |
| `EnabledEndpoints` | string[] | `["BidWeekLocations","BidWeekData"]` | endpoint toggle, matched case-insensitively to `INgiPipeline.EndpointId`. |
| **`DaysBack`** | int | **`60`** | `BidWeekData` trailing-window length ending at **today** inclusive → dates `[runDate−59 … runDate]`. `BidWeekLocations` ignores it. Clamped to ≥ 1. **(User requirement.)** |
| **`SettledAfterDays`** | int | **`60`** | hot/settled boundary by candidate-date age (§3.3). At `60` with `DaysBack = 60` the max age is **59**, so `age > 60` is never true → **the whole window is hot and re-probed every run** (§3.5). **(User requirement.)** ⚠ See the §3.5 configuration hazard and the **recommended safe floor of 35** before lowering it. |
| `HotZoneKeyStrategy` | `RunDate` \| `RunId` | `RunDate` | hot-zone + Locations re-pull cadence (§3.3). `RunHour` deliberately not offered. |
| `RequestsPerSecond` | double? | `2` | global client-side throttle (both clients); `null`/≤ 0 = unlimited. NGI publishes **no** rate limit (§12 item 9) — pace gently, back off on `429`. |

`appsettings.json` `Loaders:NGI` mirrors the AGSI/CWG block:
`ConnectionString` (`Server=…;Database=NGI;Integrated Security=SSPI;TrustServerCertificate=True;`),
`Username: "SEE_DB"`, `Password: "SEE_DB"`, `BaseUrl`, `AuthPath`, `HttpTimeoutSeconds`,
`EnabledEndpoints`, `DaysBack: 60`, `SettledAfterDays: 60`, `HotZoneKeyStrategy: "RunDate"`,
`RequestsPerSecond: 2`, `MaxConcurrentWorkUnits`, `RetryCount`, `RetryDelayMs`,
`WorkUnitTimeoutSeconds`.

> **⚠ Leave `"NGI"` OUT of `Platform:EnabledLoaders`** — build-only pass, loader **disabled** by
> default (the AGSI/CWG/IHSPointLogic/IIR posture).
>
> **SECURITY: no username, password, e-mail address or token value may appear in
> `appsettings.json`, this document, any log line, any exception message, or any test fixture.**
> The real values live only in `core.Param` (or the `DATALOADER_Loaders__NGI__*` env vars).

---

## 11. Concurrency & idempotency

- **Sinks** derive from `SqlSinkBase<TRow>`, which auto-acquires `SqlWriteGate` keyed
  `{server}/{db}::{proc}`. The two merge procs are **two distinct keys**, so the location and
  fact merges never serialise against each other; concurrent `BidWeekData` units serialise on the
  one fact-proc key (correct — it prevents the parallel-MERGE deadlock and NOT-MATCHED insert
  race). With the pipelines run sequentially (§1.5) only within-`BidWeekData` units actually
  contend.
- **FileLog** is a **direct** proc writer, so `SqlNgiFileLog` acquires `SqlWriteGate` explicitly
  on `arm.usp_UpsertFileLog` (one shared key across both endpoints) — the
  Platts/StormVista/CWG/AGSI posture. `core.LoadLog` is intentionally **not** gated (keyed per
  work unit, so it rarely contends).
- **Idempotent MERGE — merge, never blindly insert.**
  `arm.usp_BulkMergeBidWeekLocation` dedups the batch (`ROW_NUMBER` `PARTITION BY PointCode`,
  last wins) then `MERGE … ON PointCode`. `arm.usp_BulkMergeBidWeekData` dedups
  `PARTITION BY (IssueDate, PointCode)` then `MERGE … ON (IssueDate, PointCode)` — **never** on
  `FileLogId` (provenance, UPDATEd on match). Both stamp `ModifiedAtUtc = SYSUTCDATETIME()` on
  insert and on match. So the daily hot re-pull of the 60-day window and the once-per-run
  locations refresh both upsert **in place** — no duplicates, safe over-scheduling.
- **Merge semantics are plain last-wins.** Whether NGI ever **revises** a published issue is
  **unknown** — the feed carries **no revision/version/status field at all** (§12 item 1). If a
  revision ever needs preserving, the pattern to reach for is OPIS's `arm.LPReportHistory` (a
  record-status code in the key). **Not built now.**
- **Distinct work units never collide on a PK:** one unit per issue date, and `IssueDate` is a PK
  component — so two concurrent units write disjoint key sets. The gate and the dedup remain as
  correctness insurance.
- **Overlap guard:** the platform's `DataLoader:NGI` app lock prevents two host processes running
  the loader at once (safe to over-schedule; a blocked invocation exits `0`).
- **Resume:** `core.LoadLog` skips a unit **only** when its `Key` is recorded **SUCCESSFULLY
  COMPLETED**. A started-but-failed unit is retried on the next run. A `404` unit **succeeds** —
  see §3.5 for the invariant that creates and its mitigation.

---

## 12. Open Items

**Behavioural/operational unknowns only — the field set needs no live confirmation**
(`docs/apis/NGI.md` is zero-reconstruction: every field name, type, null sentinel and observed
maximum comes from a captured `200` body). This is the **first-live-run checklist**.

| # | Open item | Current design decision | How to close it |
|:-:|---|---|---|
| **1** | **Is a published issue ever REVISED?** The feed has **no revision/version/status field**, so a revision would be indistinguishable from the original except by comparing values. This decides **plain last-wins upsert on `(IssueDate, PointCode)`** vs. an **OPIS-style history table** (`arm.LPReportHistory`, where the `I`/`U` record-status code is part of the key). | **Plain last-wins upsert** (§11). | **Ask NGI / the user.** Re-open if `DATA_QUALITY_VALIDATOR` ever sees a value change on a settled issue. |
| **2** | **NGI's holiday/publication calendar is unenumerated** — not in the spec, not in the payload; the 404 body only guesses. **The spec's "always a business day" claim is already DISPROVEN** (2026-08-01 = Saturday = `200`; 2026-07-31 = Friday = `404`). | **Mitigated by design: enumerate every calendar date** and let the 404 answer (§3.4). No filter to get wrong. | Nothing required; the mitigation is structural. |
| **3** | **The 163-code equality between the two endpoints is a same-day observation, not a contract.** Codes will drift as NGI adds/retires points; because a name is a JSON *key*, two locations sharing a name could not both appear. | **No FK**; observational both-ways reconciliation (§8.2, §9 checks 8/9). | Watch checks 8/9 over a few runs. |
| **4** | **Only THREE issue dates were probed** (2026-06-01, 2026-07-01, 2026-08-01) — the "1st of the month" cadence is inferred from three points and NGI's own wording ("first business day") disagrees with the Saturday observation. | **No day-of-month rule encoded** (§3.4). | Observe `arm.FileLog` `Success` dates over 3–6 months. |
| **5** | **Token-expiry behaviour never exercised live.** A 24-hour lifetime is decoded from the JWT `exp`/`iat` claims, but no call was ever made with an expired token — the actual failure response (status + body) is unverified. | **Re-mint-once-on-401** (§4.3); no proactive `exp` pre-expiry. **The re-mint path will be UNIT-TESTED ONLY** this pass. | Confirm on the first live run (or a deliberately long-lived process). |
| **6** | **History depth unprobed** — the oldest date requested was 2026-06-01. How far back `bidweekDatafeed.json` serves, and whether older issues carry the same 11 fields and the same `"None"` sentinel, is unknown. This bounds any backfill plan. | Not relied upon; `DaysBack = 60` (≈ 2 issues). | Probe a handful of old dates. |
| **7** | **`Volume` unit and `Average` weighting are undocumented by the vendor.** No unit of measure appears in the spec or the payload (values look like a deal-volume total, max 8 647 on the national average; thousand MMBtu/d is a plausible but **unverified** reading). Prices are USD/MMBtu by NGI convention — also not stated in the API. `Average` is verified **not** the `Low`/`High` midpoint, so it is deal-weighted by an undocumented method. | Persist the raw values; no unit conversion, no derived column. | One question to NGI so the column comments/documentation are right. |
| **8** | **`/bidweekHistoricalData.json` exists and is the natural DEEP-BACKFILL lever** — spec-declared (never probed) params `start_date` (default today−365d), `end_date` (default today) and **`location`, a single point code defaulting to Henry Hub `SLAHH`**. Note the inverted shape: **per-location across a date range** (vs. this endpoint's all-locations, single-date), so a full backfill would be ~163 calls (one per code) rather than one. **Its response shape is completely unknown.** | **Out of scope for this build; cheap to add later** as a third pipeline. | Probe it before anyone plans around it. |
| **9** | **Rate limits unpublished and unprobed.** No numeric limit, no `Retry-After`, no `X-RateLimit-*` header in the spec, the docs site or any captured response; no `429` ever seen (~11 probe calls). | `RequestsPerSecond = 2`, `MaxConcurrentWorkUnits` modest, Polly backs off on `429` and honours `Retry-After` (§4.4). | Watch for `429` on the first full 60-unit run and tune. |

### 12.1 Decisions the loader spec left unspecified — please confirm

| Decision | Chosen here | Where |
|---|---|---|
| Endpoint ids / `EnabledEndpoints` values | `"BidWeekLocations"`, `"BidWeekData"` (also the `arm.Endpoint` seed names) | §1.4, §6 |
| Pipeline execution order | Locations → BidWeekData, **sequential, cosmetic only** (either order is correct) | §1.2 |
| `MaxConcurrentWorkUnits` / `RequestsPerSecond` defaults | modest concurrency + `2` rps against an unpublished limit | §10 |
| `HttpTimeoutSeconds`, `AuthPath` | `60`, `/auth` | §10 |
| Nullability of `SurveyStart`/`SurveyEnd`/`Region`/`PricingPoint` | **NULLable** (deviating from the API doc's `NOT NULL`) so tolerant parse degrades instead of dropping; asserted 0 by validation | §8.2 †, §13 item 3 |
| `arm.BidWeekLocation` PK | **`PointCode`, no surrogate `Id`** (user requirement; deviates from the dimension shape) | §8.1, §13 item 2 |
| `meta` block persistence | **not persisted** — used as a fallback source + a validation assertion | §8.2, §9 check 17 |
| Map-key vs `Point Code` disagreement | persist the **record's** `Point Code`; counted warning | §5.3 |
| Record with a blank `Point Code` | **fall back to the map key** (counted warning); drop-and-count only if both are blank | §5.3 |
| `200` with a non-object `data` node | **THROW** (shape drift) — not a silent empty success | §5.3, §5.4 |
| `HotKeyStrategy` options offered | `RunDate` (default) \| `RunId`; **no `RunHour`** | §3.3 |
| Timezone | **US Central** (house style; IIR precedent) | §7 |

---

## 13. Open items / coordination for DATABASE_DEVELOPER & reviewer

Two tables + one `FileLog` hub + two seeded lookups + supporting objects, in
`sql/NGI/001…003` plus a guarded, idempotent `999_DropNgiObjects.sql` teardown that drops every
object in reverse dependency order (see `sql/AGSI/`). **DB `NGI`, schema `arm`.** Coordinate on:

1. **`arm.BidWeekData`** — 11 payload columns + `FileLogId` (12-col TVP) + `DateCreated`
   (leading) + `ModifiedAtUtc`. **PK / merge key `(IssueDate, PointCode)`** (user requirement).
   Composite natural PK, **no surrogate `Id`** (fact-table shape). Bracket `[Low]`, `[High]`,
   `[Average]`, `[Volume]` in DDL for safety. Index recommendation: PK covers the merge; add a
   nonclustered index on `FileLogId` (provenance lookups) and consider one on `PointCode` for
   the reconciliation checks.
2. **`arm.BidWeekLocation`** — **PK `(PointCode)`, no surrogate `Id`** per the user requirement
   (§8.1). Confirm you are happy with that deviation from the `Id IDENTITY`-PK dimension shape,
   or take the hybrid (`Id INT IDENTITY(1,1) UNIQUE` + PK on `PointCode`) if you want the
   template byte-identical. **No FK from `arm.BidWeekData`** either way (§8.2).
3. **Nullability call to confirm (§8.2 †).** `SurveyStart`, `SurveyEnd`, `Region`,
   `PricingPoint` are **NULLable** here, deviating from `docs/apis/NGI.md` §7.2's `NOT NULL`
   recommendation, so that a silent NGI rename degrades to NULL rather than dropping ~163 priced
   rows. `usp_ValidateLoad` asserts **0 NULLs** in all four. Confirm or push back.
4. **Type calls (from the API doc, all live-observed).** `DECIMAL(13,6)` **not `FLOAT`** for
   `Low`/`High`/`Average` (published prices must round-trip exactly; source is exact to 3 dp,
   scale 4 is headroom). **SIGNED — NO `CHECK (>= 0)` on any price column** (negative gas prices
   occur). `DATE` **not `DATETIME2`** for all three date columns (the feed has **no** time
   component anywhere). `INT` for `Volume`/`Deals` (`SMALLINT` risks overflow above 32 767;
   `TINYINT` banned by convention). **`VARCHAR` not `NVARCHAR`** throughout (every observed
   character in both fixtures is ASCII) — revisit only if a `mexico*` feed with accented names
   is ever folded in. `ModifiedAtUtc DATETIME2(3)`.
5. **No lookup/`CHECK` on `Region`.** The 14 observed values are **not** an enum in any vendor
   document — a constrained lookup would fail the load on a new region. Plain text on the fact
   (§8.2).
6. **TVP contract (§8.3).** Mirror the 3-column and 12-column orders **identically** across
   `001` table body, `002` TVP type, `003` proc `SELECT`/`INSERT`/`UPDATE` lists, and the C#
   sinks' `BuildTable` + their tests. **`FileLogId` FIRST in both** (convention — deliberately
   NOT AGSI's `FileLogId`-last entity TVP). **TVP parameter name is `@Records`** (what
   `SqlSinkBase` passes).
7. **`arm.FileLog` hub + `arm.usp_UpsertFileLog`** — natural key
   **`(EndpointId, RepresentativeDate)`** with SQL NULL-equality (§6). **The AGSI `Region` axis is
   DROPPED** — no `arm.Region` table, no `@Region` parameter, no `RegionId` column (§6 explains
   why the name would be actively confusing next to the `Region` payload column). Confirm:
   (a) normalise `Endpoint`/`Status` to seeded lookups (`arm.Endpoint`
   `{BidWeekLocations, BidWeekData}` with their URL paths; `arm.Status`
   `{Success, NotAvailable, Failed}`) with the proc keeping its name-string signature and
   resolving server-side — recommended; vs. inline `VARCHAR + CHECK` for the build-only pass.
   (b) whether to keep `LastCheckedUtc` (recommended — it is how a hot re-pull shows freshness on
   a stable hub row).
8. **`arm.usp_ValidateLoad(@DateFrom DATE, @DateTo DATE)`** — the 19 checks in §9.1, ONE result
   set, uniform `CheckName, Scope, ExpectedCount, ActualCount, Detail`, `ORDER BY CheckName,
   Scope`. **Load-bearing SQL comments:** (a) `-- do NOT add a non-negative price check —
   negative gas prices are real` on check 5; (b) `-- do NOT set ExpectedCount = 0 here` on check
   13 (`PricedWithoutActivity`, Quirk A); (c) `-- informational only, NEVER an error` on checks
   8, 9, 10, 14; (d) the `USAVG`/`Region='California'` quirk note on check 14. Check 10 needs
   **no tally table** (`DATEDIFF + COUNT(DISTINCT)`).
9. **No `usp_Get…` read proc.** Deliberately absent — nothing reads `arm.BidWeekLocation` back at
   run time (§2, §0.1). **Do not add one "for parity with AGSI".**
10. **Merge procs** `arm.usp_BulkMergeBidWeekData` / `arm.usp_BulkMergeBidWeekLocation` — dedup
    on the merge key (`ROW_NUMBER`, last wins) **before** the `MERGE`; stamp `ModifiedAtUtc` in
    the proc; `SELECT` the affected row count so `ProcedureReturnsRowCount = true` works.
    **Upsert-only — NEVER a `DELETE`/`WHEN NOT MATCHED BY SOURCE` branch** (§5.2 step 7): a
    truncated snapshot must not be able to wipe either table.
11. **`core.Param` rows** required before any run: `(LoaderName='NGI', ParamName='Username')` and
    `(…, ParamName='Password')`. Note the `SeeDbSettingsResolver` **throws** on a missing row, so
    both must exist even for a build-only smoke test (a placeholder value is enough to get past
    resolution; the §1.5 guard then rejects the literal `SEE_DB`). **No secret value in `sql/`,
    in `appsettings.json`, or in this document.**

---

## Coverage checklist

| Endpoint | Pipeline (`EndpointId`) | Work unit / literal key | Reader tolerance | Target table (merge key) | FileLog identity |
|---|---|---|---|---|---|
| 2 `GET /bidweekLocations?format=json` | `BidWeekLocations` (runs 1st — cosmetic) | single undated unit, **always hot**: `ngi:locations:run={hot}` | node missing/non-object → **throw**; empty object → `NotAvailable` + warn; **KEY = name, VALUE = code**; blank code dropped-and-counted; dedup on `PointCode` | `arm.BidWeekLocation` (`PointCode`) | `BidWeekLocations` / **NULL** → one stable hub row |
| 1 `GET /bidweekDatafeed.json?issue_date=` | `BidWeekData` (runs 2nd) | **one unit per CALENDAR DATE** in `[runDate−(DaysBack−1) … runDate]`; two-zone key `ngi:bidweek:{yyyyMMdd}` (settled) / `…:run={hot}` (hot) — **all-hot at the 60/60 default** | **`404` → NotAvailable, SUCCESS, 0 rows** (~58/60); **`400` → THROW**; `401` → re-mint once then throw; `data` non-object → **throw**; `"None"` → NULL; spaces-in-field-names bound by candidate name; unkeyable record dropped-and-counted | `arm.BidWeekData` (`IssueDate, PointCode`) | `BidWeekData` / the requested **issue date** |
| — `POST /auth` | (no pipeline) | one mint per process, cached; re-mint once on `401` | tolerant token read; **`refresh` captured-and-ignored**; non-2xx → throw naming `core.Param` (never values) | — | — |

Both endpoints have a complete persisted field set (**2 / 11**), a per-field SQL type +
nullability, a natural key, a literal resume-key rule, a FileLog identity, and an explicit
tolerance policy for every status the API can return. Ready for **DATABASE_DEVELOPER** (two
tables + two TVPs + two merge procs + `arm.FileLog` / `arm.usp_UpsertFileLog` + two seeded
lookups + `arm.usp_ValidateLoad` + a guarded `999` teardown — and **no** `usp_Get…`, **no**
`arm.Region`, **no** FK) and **CODER** (module + two closed independent pipelines + two providers
+ two readers + `NgiParse`/`NgiTime` + two rows + two sinks + FileLog writer + token
provider/handler + throttle/policy + validator; loader left **disabled** in
`Platform:EnabledLoaders`).
