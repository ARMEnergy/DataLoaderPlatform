# ModernCommodities (Modern Commodities / "ModCom" — trade tape + daily settlements) loader — design & processing flow

Design/flow spec for the **ModernCommodities** loader (vendor short name **ModCom**). Input of
record is the field reference at `docs/apis/ModernCommodities.md` (**43 source columns across 3
endpoints, fully live-verified — zero reconstructed fields**: per-field SQL types, the four distinct
`400` bodies, both row caps, the 6-calendar-month history limit, the `-` sentinel). This document is
the CODER hand-off; it contains **no code and no SQL**. SQL objects named here are *proposed* to
DATABASE_DEVELOPER (open items collected in §13).

**Modelled on `docs/design/NGI.md`** — the closest structural precedent: closed, mutually
INDEPENDENT pipelines built in module factory closures, one shared window helper used by both the
work-unit providers and the validator, an `arm.FileLog` hub, an observational `usp_ValidateLoad`, and
a build-only posture. **Deviations from NGI are called out inline and summarised in §1.2.** The CSV
transport and the descriptor registry come from `docs/design/CWG.md` (`CwgCsv` RFC-4180 tokenizer,
`CwgSourceReader`, `CwgEndpointPipeline`); the auth shape and the hour-granular hot key come from
`docs/design/IHSPointLogic.md` (`PlBasicAuthHandler`, `PlResumeKey.HotToken`); the order-independent
`WHEN MATCHED` recency guard comes from `docs/design/OPIS.md`
(`sql/OPIS/003_CreateOpisProcedures.sql`).

Locked decisions this design is built around (from `MODCOM_DECISIONS.md`, **binding**): DB
**`ModernCommodities`**, schema **`arm`**, Integrated Security; `Username`/`Password` via **`SEE_DB`**
resolved from `core.Param`, presented as **HTTP Basic** in a request **header**; **three endpoints
modelled as three closed, mutually INDEPENDENT pipelines** (`AllTrades`, `MyTrades`, `Settlements`)
run sequentially **for deterministic logging only, NOT because of any data dependency**; three tables
per the user's authoritative DDL (`arm.AllTrades` / `arm.MyTrades` PK `TradeNumber`,
`arm.Settlements` PK `(SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term)`) with
**no `FileLogId` column, no `Id`, no `DateCreated`, no `Checksum` and no FK between them**;
**`DaysBack = 30`** on every run with a per-endpoint override; **hot-only resume key at
`yyyyMMddHH` hour granularity, no settled zone**; a **UTC** window basis; **build-only pass** — the
loader is wired but left **out of `Platform:EnabledLoaders`**.

> **Build-only posture (read first).** Same posture CWG / AGSI / IHSPointLogic / IIR / NGI shipped
> in: everything is coded, unit-tested and buildable, but the loader is **disabled by default**
> (`"ModernCommodities"` is **not** in `Platform:EnabledLoaders`) and there is no live DB deploy or
> data load. Like NGI, the **field set needs no live confirmation** — `docs/apis/ModernCommodities.md`
> is zero-reconstruction. What remains open is purely **behavioural** (§12): settlement revision
> policy, the timestamp timezone, `myTrades` history depth, the unreconciled `allTrades` daily rate.

> **SECURITY.** The vendor issues an opaque machine username + password (and the PDF also prints them
> pre-encoded as a ready-made `Authorization: Basic …` value — **that encoded form is a credential
> too**). **No username, password or encoded header value appears in this document, in
> `appsettings.json`, in `sql/`, in any log line, in any exception message or in any test fixture.**
> Both settings default to the literal `"SEE_DB"` sentinel and resolve at run time from
> `core.Param(LoaderName='ModernCommodities', ParamName='Username'|'Password')`.

---

## ⚠⚠ The two rationales you must not "optimise away"

### A. There is **NO settled zone**. The resume key is **hot-only, at hour granularity**.

Every other date-windowed loader here (StormVista, AGSI, NGI) splits its window into a **settled**
zone (stable key → loaded once, forever) and a **hot** zone (run-varying key → re-pulled).
**ModernCommodities must not.** The reason is a verified property of the API, not caution:

> **The trades window filters on `Last Updated Timestamp`, not `Executed Timestamp`**
> (`docs/apis/ModernCommodities.md` §5.1). Of 98 live `allTrades` rows for `2026-08-20..24`, **5 were
> executed *before* the window** — including three legs executed `2026-08-06` and revised
> `2026-08-24`, **18 days later** — and **0 fell outside it on last-updated**.

So:

1. **Nothing in this feed is ever final.** A trade `State` flips `Finalized` → `Cancelled`, or its
   economics are corrected, and `Last Updated Timestamp` advances to the moment of the change. A
   five-month-old trade can still be restated inside today's window.
2. **A stable (settled) key would freeze a trade at its pre-revision state forever.** `core.LoadLog`
   skips a unit whose `Key` is already recorded successful — so once a settled window key succeeded,
   that window is never re-requested, and every later revision inside it is invisible. The loader
   would report clean runs while silently serving stale, cancelled trades.
3. **The rolling re-pull is not defensive padding — it is the ONLY mechanism that surfaces revisions
   and cancellations.** Narrow the window or settle its tail and those corrections are never seen.
4. **A second, independent reason:** a legitimate "no data" here is a **header-only `200`**
   (§5.6), which *succeeds*. Under a settled key, a window that was empty merely because nothing had
   been updated yet would be recorded done forever.

**Therefore the key is `…:run={hot}` on every unit, with `{hot}` = the run's **UTC** `yyyyMMddHH`**
(the `PlResumeKey.HotToken` / `PlHotKeyStrategy.RunHour` shape — `src/DataLoader.IHSPointLogic/PlWorkUnit.cs`).
A new clock hour re-pulls the whole window; a second run inside the same hour idempotently skips.
There is **no `SettledAfterDays` setting at all** — see §0.1 and §3.3.

### B. The settlements `-` is a **key value**, not a null sentinel.

`arm.Settlements.Location` and `arm.Settlements.PieplineTerminal` are **`NOT NULL` columns inside the
6-column PRIMARY KEY**, and the vendor writes the **literal one-character string `-`** in both of
them (82 of 1,443 rows, always as a pair, all `Product = 'Sweet Guernsey Blend'`).

> **Do NOT copy `NgiParse.IsNullSentinel`.** It lists `"-"` among its defensive null sentinels
> (`src/DataLoader.NGI/NgiParse.cs`). Reusing that helper here would map `-` to `NULL`, violate the
> `NOT NULL` PK, and — if applied inconsistently — create duplicate logical rows the MERGE cannot
> reconcile. `ModComParse` has **no `-` sentinel and no `"None"`/`"N/A"` sentinel of any kind**: the
> only absent-value representation in this API is the empty string `""` (§5.3).

Contrast §8.1 note on `Price`: a `-` **prefixing digits** in a numeric column is a **sign** (74% of
settlement prices are negative). Never a parse failure, and never a `CHECK (Price >= 0)`.

---

## 0. Class / structure inventory

| Concern | Type(s) | Notes |
|--------|---------|-------|
| Settings | `ModComSettings : LoaderSettingsBase`, `ModComEndpointOverride`, `ModComHotKeyStrategy` enum | §10 |
| Module | `ModernCommoditiesModule : ILoaderModule` (`LoaderId = "ModernCommodities"`) | builds all three pipelines explicitly |
| Descriptors | `ModComEndpointDescriptor` record + `ModComDescriptors` registry (3 entries), `ModComParseShape` enum (`Trades`\|`Settlements`), `ModComEndpoints` id constants | §1.1, §2 |
| Pipeline marker | `IModComPipeline : ILoaderPipeline { string EndpointId }` | so `RunAsync` can enumerate + toggle + order |
| Pipeline | `ModComPipeline<TRow> : LoaderPipelineBase<ModComWorkUnit, TRow, TRow>, IModComPipeline` | one generic class, three closed instances; `IdentityTransformer<TRow>` (the CWG `CwgEndpointPipeline<TRow>` shape) |
| Work unit | `ModComWorkUnit : WorkUnit` | one per **endpoint × window chunk** (§3.1) |
| Provider | `ModComWorkUnitProvider : IWorkUnitProvider<ModComWorkUnit>` | ONE descriptor-parameterised class, three instances; window + chunking only (§2, §3.4) |
| Window / time | `ModComTime` (`ResolveWindow`, `HistoryFloor`, `HotToken`, `Iso`), `ModComWindow` readonly record struct | **single source of truth** for providers AND validator (§3.2) |
| CSV | `ModComCsv` (RFC-4180 tokenizer), `ModComHeaderMap` (literal-name → index) | §5.1, §5.2 |
| Parse | `ModComParse` (invariant culture; `hh:mm:ss tt` timestamps; `Dec92` range guard; `Bit`; width-guarded `Str`) | §5.3 — **no `-` sentinel** |
| Readers | `ModComTradesSourceReader : ISourceReader<ModComWorkUnit, TradeRow>`, `ModComSettlementsSourceReader : ISourceReader<ModComWorkUnit, SettlementRow>` | §5.4, §5.5 — **never** `HttpJsonSourceReaderBase` |
| Rows | `TradeRow` (34 props, **shared by both trades tables**), `SettlementRow` (9 props) | **neither carries `FileLogId`** (§6) |
| Sinks | `AllTradesSqlSink`, `MyTradesSqlSink`, `SettlementsSqlSink` (all `: ModComSqlSinkBase<T> : SqlSinkBase<T>`) | TVP bulk MERGE (§8.3, §11) |
| Auth | `ModComBasicAuthHandler : DelegatingHandler` | §4.1 — no token, no provider, no re-mint |
| HTTP plumbing | `ModComRateLimiter` (singleton), `ModComRateLimitingHandler`, `ModComHttpPolicy` | §4.3 |
| Errors | `ModComRequestException` + `ModComFailureKind` enum (`RowCap`, `History`, `InvalidParam`, `InvalidLegalEntity`, `Auth`, `ShapeDrift`, `Other`) | §5.6 |
| FileLog | `ModComFileContext` (readonly struct), `IModComFileLog`, `SqlModComFileLog` (`arm.usp_UpsertFileLog`, `SqlWriteGate`) | §6 |
| Post-load validation | `ModernCommoditiesLoadValidator` (module-level hook) → `arm.usp_ValidateLoad` | §9 |

### 0.1 Types and objects that must **NOT** exist (deliberate absences)

Listed because a copy of AGSI, NGI or IHSPointLogic would add them and each would be wrong here.

| Absent | Why |
|--------|-----|
| `SettledAfterDays` setting / any settled-zone branch / a two-zone key | **Rationale A.** Nothing in this feed is ever final. Hot-only, always. §3.3. |
| Any reference provider (`IModComEntityProvider`…) | No endpoint needs a value obtained from another. All three take dates only. §2. |
| `arm.usp_Get…` read proc | Nothing is read back out of any table at run time. §2, §13 item 9. |
| Any tier barrier / fail-fast-if-dimension-empty guard | There is no dependency to protect. §1.2, §2. |
| Any FK between `arm.AllTrades`, `arm.MyTrades` and `arm.Settlements` | Three independent views of the venue; the same `TradeNumber` legitimately exists in the two trades tables with no parent/child relationship. §2, §8.1. |
| `FileLogId` on any row type, any TVP or any fact table | The user's DDL has no such column. **Deliberate break from the house "`FileLogId` is column 1" rule.** §6. |
| `HttpJsonSourceReaderBase` | **Every response is `text/csv`.** There is no JSON representation of any endpoint — no `?format=json`, no `Accept` negotiation, no `.json` sibling. And its `EnsureSuccessStatusCode()` would pre-empt the body-based `400` classification (§5.6). |
| A token provider / `/token` mint / re-mint-once-on-401 | **There is no token.** HTTP Basic is presented in full on every call; a `401` is terminal. §4.2. |
| A paging loop, `?page`/`pageIndex`/`offset`/`limit`, a cursor, an id-batching (≤50) loop | The API has **no paging and no batching**. One request = the complete result set, bounded only by a hard row cap. Fan-out is over **date ranges**. §3.4. |
| A `-` → NULL (or `""`) normalisation, a `"None"`/`"N/A"` sentinel list | **Rationale B.** `-` is a PK value. §5.3. |
| `CHECK (Price >= 0)` on any price/commission column | 74% of settlement prices and 21% of `allTrades` prices are negative. §8. |
| A `Checksum` column or a checksum short-circuit in any MERGE | Removed from all three tables by explicit user decision (U2). |
| A business-day / holiday calendar for settlements | Unnecessary by construction: settlements are requested as a **range**, and unpublished days simply contribute no rows. §5.5. |
| A lookup table, `CHECK` or C# enum that rejects an unknown `State`/`TradeType`/`ProductType`/`UnitOfMeasure`/`Side`/`SettlementCurrency`/`PriceBasis` | No vendor document enumerates them; a new value must load and be **reported**, never rejected. §9. |
| A shared `PriceBasis` dimension across trades and settlements | Same column name, **different value spaces** (settlements: 2 values ≤7 chars; trades: compound index expressions to 45 chars). §8.2. |

---

## 1. Module topology & pipeline strategy

**One module, three closed pipelines**, built explicitly in `ModernCommoditiesModule` via
`BuildPipeline(descriptor, …)` factory closures (the CWG / NGI / AGSI pattern), toggled by
`EnabledEndpoints[]` (`"AllTrades"`, `"MyTrades"`, `"Settlements"`).

### 1.1 Descriptor-driven registration over three pipelines

Three endpoints is below the threshold where a CWG-scale (15) or IHSPointLogic-scale (25) fan-out
pays for itself in indirection — but the endpoints are **near-identical in mechanics** (same auth,
same two query params, same CSV transport, same status matrix, two of them byte-identical in shape),
so the design still keeps **one descriptor record** and fans out over it. What repeats per endpoint is
only the **sink** (and, for the two parse shapes, the row type + reader).

`ModComEndpointDescriptor` (a `record`, the compile-time discovery result — enumeration is DB-free):

| Field | `AllTrades` | `MyTrades` | `Settlements` |
|---|---|---|---|
| `EndpointId` | `AllTrades` | `MyTrades` | `Settlements` |
| `DisplayName` | All Trades (anonymised market tape) | My Trades (fully attributed) | Daily Settlements |
| `Path` | `allTrades/v1` | `myTrades/v1` | `settlements/v1` |
| `ParseShape` | `Trades` | `Trades` | `Settlements` |
| `HistoryLimited` | **true** (6 calendar months) | **false** (all time) | **true** (6 calendar months) |
| `RowCap` | `10000` | `10000` | `100000` |
| `SupportsLegalEntityName` | false | **true** | false |
| `TargetTable` / `TargetTvp` / `TargetProc` | `arm.AllTrades` / `arm.TradesTvp` / `arm.usp_BulkMergeAllTrades` | `arm.MyTrades` / `arm.TradesTvp` / `arm.usp_BulkMergeMyTrades` | `arm.Settlements` / `arm.SettlementsTvp` / `arm.usp_BulkMergeSettlements` |

Two consequences worth stating:

- **`AllTrades` and `MyTrades` share ONE row type (`TradeRow`), ONE reader class, ONE `BuildTable`
  and ONE TVP type.** Their CSV header is **byte-identical** (497 bytes, 34 columns, same order —
  verified across three captures) and the user's DDL gives the two tables identical column lists.
  Two 34-column definitions kept in sync by hand is exactly the drift risk the TVP contract exists to
  prevent, so they are defined **once**. §8.3 and §13 item 3 record the alternative (two identically
  shaped `arm.AllTradesTvp` / `arm.MyTradesTvp` types) if DATABASE_DEVELOPER prefers per-table naming.
- **The two pipelines still write to two different tables via two different procs.** The *only*
  per-endpoint C# is the sink's `StoredProcedureName`.

**We never register a generic `IWorkUnitProvider<T>` / `ISourceReader<T>` / `ISink<T>` in DI** (the
collision Platts/StormVista/CWG/AGSI/NGI all warn about). Each pipeline is `new`ed inside a module
factory closure and handed to `ModComPipeline<TRow>`'s constructor; the pipeline's generics are never
resolved by the container.

### 1.2 ⚠ The three pipelines are **mutually INDEPENDENT** — do not inherit AGSI's coupling

**This is the single easiest thing a copy-paste from a "lookup + fact" loader will get wrong.** In
AGSI the two pipelines are *coupled and ordered*: `/api/about` populates `arm.GasStorageEntity`, an
`IAgsiCountryProvider` reference provider reads it back through `arm.usp_GetGasStorageEntities`, and
the fact pipeline's work units **are** that country list × the date window — a hard ordering barrier
with a fail-fast-if-empty guard and an enforcing FK.

**ModernCommodities has no such dependency anywhere.** All three endpoints are parameterised by
**dates alone**; none returns an id another needs.

| AGSI | ModernCommodities |
|------|-------------------|
| Discovery pipeline feeds the fact's work units | **Each pipeline's work units are self-contained** (window + chunking only) |
| `IAgsiCountryProvider` (load-once, fail-fast) | **No reference provider at all** |
| `arm.usp_GetGasStorageEntities` read proc | **No read proc** |
| Hard barrier: About must complete first | **No barrier** — order is a logging convenience only |
| `FK_GasStorage_Entity` enforces integrity | **No FK anywhere** |
| A fact-only first run is a configuration error | **A `Settlements`-only run is fully valid. A `MyTrades`-only run is fully valid. Any subset is valid.** |

**Order:** `RunAsync` runs **`AllTrades` → `MyTrades` → `Settlements`**, purely so a run's log reads
deterministically and the global request rate stays predictable against an unpublished rate limit
(§4.3). It is **not** load-bearing. Concretely:

- A **failure** in any pipeline must **NOT** prevent the others from running. All enabled pipelines
  execute unconditionally and their `LoaderRunResult`s are aggregated.
- Running them **concurrently** would also be correct; sequential is chosen for log clarity.
- Per the platform's discovery-first principle: **do not introduce a tier barrier when there is no
  dependency** — and here there is none, not even a local one.

**Recorded as intended (from `MODCOM_DECISIONS.md`):** the same `TradeNumber` exists independently in
`arm.AllTrades` (anonymised) and `arm.MyTrades` (attributed), with **no FK and no cross-table dedup**.
They are two *views* of the venue at different disclosure levels, not a parent/child pair. The
validator reports the overlap **informationally** (§9 check 18), never as a defect.

### 1.3 Divergences from the NGI template (the model), stated

| NGI | ModernCommodities | Why |
|---|---|---|
| JSON payloads, `System.Text.Json` | **CSV payloads, RFC-4180 tokenizer** | Every response is `text/csv`; there is no JSON form of any endpoint. §5.1. |
| JWT bearer minted from a body-borne secret, re-mint once on `401` | **HTTP Basic header, no token, `401` is terminal** | There is no mint endpoint at all. §4. |
| `404` is the **normal** case (~58 of 60 units) | **There is NO legitimate non-2xx.** Emptiness is a header-only `200`; every `400`/`401` is our bug or a bad credential | §5.6. |
| Two-zone settled/hot key, `RunDate` default | **Hot-only key, `RunHour` (UTC) default** | Rationale A. §3.3. |
| One unit per **calendar date** | One unit per **window chunk** (default: the whole window, 1 unit/endpoint) | The API takes a range, has no paging, and caps rows per request. §3.4. |
| US-Central date basis | **UTC** basis | D5; UTC is ahead of both Central and Mountain so it can never clip a just-published row, and `yyyyMMddHH` UTC is strictly monotonic (no DST). §7. |
| `FileLogId` stamped on every fact row; hub keyed `(EndpointId, RepresentativeDate)` | **No `FileLogId` on any fact row**; hub keyed `(EndpointId, WindowStart, WindowEnd, RunToken)`, one row **per pull** | D1 + D7. §6. |
| `arm.BidWeekLocation` is a lookup refreshed in place | All three targets are **facts** | §8. |

### 1.4 Why `LoaderPipelineBase` directly, NOT a custom orchestrator

Unit counts are trivial: **1 unit per endpoint at the shipped default** (`ChunkDays = 0` = the whole
window in a single request → **3 requests per run**), rising to a handful only for a chunked deep
backfill (§3.4). The whole list materialises for free, so the loader uses `LoaderPipelineBase`
directly and reuses the vetted per-unit loop unchanged — `BeginAsync` idempotency skip → `ReadAsync`
→ identity transform → `WriteAsync` → `CompleteSuccess`/`CompleteFailure`, bounded by
`ParallelRunner` at `MaxConcurrentWorkUnits`, per-unit timeout, **fail-a-block-not-the-run**.
StormVista's windowed orchestrator is **not** needed and is **not** used.

### 1.5 DI registration (`RegisterServices`)

1. `services.AddLoaderSettings<ModComSettings>(configuration, Id);` — binds `Loaders:ModernCommodities`
   and registers the `SEE_DB` post-configure resolver so `Username`/`Password` resolve lazily from
   `core.Param` at run time. **Never `services.Configure<ModComSettings>(…)`.**
2. Shared throttle: `ModComRateLimiter` (singleton state) + `ModComRateLimitingHandler` (transient).
3. Auth: `services.AddTransient<ModComBasicAuthHandler>();` — transient so it re-stamps the header on
   every retry attempt. **No singleton token provider** (there is no token).
4. **One** named `HttpClient` **`"ModernCommodities"`** — there is no second, un-authed client
   (nothing to mint):
   - `Timeout = HttpTimeoutSeconds`; `Accept: text/csv`;
   - `.RemoveAllLoggers()` — **not strictly required** (the credential is a header, so the request
     URI is safe to log, unlike CWG/StormVista's `?apikey=`), but kept for log-surface parity across
     loaders and as belt-and-braces. **The `Authorization` header must never be logged at any level
     in any handler.**
   - `.AddPolicyHandler(...)` — retry **OUTER** (`RetryCount`/`RetryDelayMs`; retry `429`/`5xx`/
     transient, honour `Retry-After`). **`400`, `401`, `403` and `404` are NOT retryable** — all four
     are deterministic (our bug, a bad credential, or a wrong path) and retrying them only delays the
     real signal;
   - `.AddHttpMessageHandler<ModComBasicAuthHandler>()` — auth **MIDDLE**;
   - `.AddHttpMessageHandler<ModComRateLimitingHandler>()` — throttle **INNER**, so every attempt is
     paced.
   → **Handler order: retry (OUTER) → auth → throttle (INNER)** — the repo standard.
5. `services.AddSingleton<IModComFileLog, SqlModComFileLog>();`
6. `services.AddSingleton<ModernCommoditiesLoadValidator>();`
7. The three pipelines, one registration each
   (`services.AddSingleton<IModComPipeline>(sp => BuildPipeline(sp, ModComDescriptors.AllTrades))`, …).
   Each factory resolves settings/logger-factory/`HttpClient`/FileLog, constructs
   `ModComWorkUnitProvider` + the shape's reader + the endpoint's sink, and wraps them in
   `ModComPipeline<TRow>(descriptor.EndpointId, provider, source, sink, loadLog, settings, logger)`.

### 1.6 `RunAsync` fan-out

1. Read `ModComSettings`; create the module logger.
2. **Credential guard.** If `Username` or `Password` is blank or still the literal `"SEE_DB"`
   placeholder (compared **trimmed, case-insensitively** — a hand-edited `core.Param` row saying
   `see_db` is just as unconfigured), log an **error naming the `core.Param` rows** and return
   `LoaderRunResult.Failed`. **Never log the values.** All three endpoints require the credential, so
   the guard is unconditional. *(As NGI documents: materialising `IOptions<ModComSettings>` already
   ran `SeeDbSettingsResolver`, which **throws** if a `core.Param` row is missing — this guard is the
   **secondary** check that catches a row whose value was left as the placeholder.)*
3. **Configuration sanity warnings** (each once, at the top of the run, before any request):
   - `HotKeyStrategy != RunHour` → **warn**, naming the cadence it selects instead
     (`RunDate` = one re-pull per UTC day; `RunId` = every invocation) and noting that the host is
     expected to run hourly (U5).
   - For each enabled endpoint, compute the effective window (§3.2) and the estimated row count
     (§3.4). If the estimate exceeds **60 %** of the descriptor's `RowCap`, **warn** naming
     `ChunkDays` and the recommended value. This is the pre-flight version of the `400` in §5.6 #2 —
     it costs nothing and turns a failed run into a warning before the first request.
   - If a `HistoryLimited` endpoint's requested start was **clamped** (§3.2), log at **information**
     with both dates, so an operator who set `DaysBack = 365` on `allTrades` sees why they got 183
     days.
4. `enabled = HashSet(EnabledEndpoints ?? [], OrdinalIgnoreCase)` (a `null` binding means "run
   nothing" → warn + success, never an `ArgumentNullException`). Enumerate
   `services.GetServices<IModComPipeline>()`; **warn** for any enabled id with no matching pipeline;
   if none enabled → warn and return `Success = true`.
5. Run in the fixed order **`AllTrades` → `MyTrades` → `Settlements`** (§1.2 — cosmetic). Each
   `ExecuteAsync` fans its units out via `ParallelRunner`. **Every enabled pipeline runs even if an
   earlier one fails** (catch, log, record a failed result, continue).
6. Run the **module-level post-load validation** (§9) scoped to the union window.
7. Aggregate the per-pipeline `LoaderRunResult`s (sum totals; `Success = all succeeded`; concatenate
   error messages) exactly as NGI/AGSI/CWG/StormVista/Platts do.

---

## 2. Work-unit derivation — no discovery, no reference handoff, no barrier

This section exists to say what the loader *does not* do, because the loaders it most resembles do
the opposite (§1.2).

**Every work unit is derived from the run's UTC clock, the endpoint's `DaysBack`, its `ChunkDays` and
its `HistoryLimited` flag — and nothing else.** `ModComWorkUnitProvider` takes
`LoaderRunContext.StartedAtUtc`, calls `ModComTime.ResolveWindow(...)`, splits the window into
chunks, and emits one unit per chunk. It touches **no database table**, holds **no reference cache**,
and has **no fail-fast-if-empty guard**.

`myTrades`'s optional `legalEntityName` is the closest thing this API has to a discovery axis, and it
is **deliberately not one**: omitting it returns trades for **both** ARM legal entities (verified), so
the shipped default omits it entirely (§10). Its valid values are discoverable only from the `400`
body (§5.6 #4) or the web UI — never from a discovery endpoint, and never enumerated by this loader.

Consequences a reviewer should confirm are intentional:

- **Any subset of the three endpoints is a valid run.** There is no ordering requirement, no
  prerequisite table and no first-run bootstrap.
- **The three pipelines could be reordered or parallelised** without changing correctness.
- **Nothing in the loader reads a ModCom table at run time** — hence no `usp_Get…` proc (§13 item 9).

---

## 3. Work-unit definition, window resolution, chunking & resume keying

### 3.1 The work unit

**`ModComWorkUnit : WorkUnit`** — one unit = one endpoint × one window chunk = **one HTTP request** =
**one `arm.FileLog` row**.

| Member | Content |
|---|---|
| `EndpointId` | `AllTrades` \| `MyTrades` \| `Settlements` (from the descriptor) |
| `WindowStart`, `WindowEnd` | `DateOnly`, **inclusive both ends** — the literal `startDate`/`endDate` query values |
| `LegalEntityName` | `string?` — the `myTrades` scope, or `null` when omitted (the default) |
| `RequestPath` | Sanitised relative descriptor for logging + `FileLog`, e.g. `allTrades/v1?startDate=2026-07-25&endDate=2026-08-24` (no host, no credential — the credential is a header) |
| `RunToken` | The `{hot}` token (§3.3) — also the `FileLog` natural-key slot (§6) |
| `KeyValue` / `Key` | Precomputed literal resume key (§3.3) |
| `DisplayName` | `$"{EndpointId} {WindowStart:yyyy-MM-dd}..{WindowEnd:yyyy-MM-dd}"` (+ `" [entity]"` when scoped), rendered **invariant** — it is persisted verbatim into `core.LoadLog` |

There is **no per-day, per-product or per-id work unit**: one request returns the complete result set
for its range (no paging, no batching).

### 3.2 Window resolution — `ModComTime.ResolveWindow` is the **single source of truth**

```
today   = DateOnly.FromDateTime(startedAtUtc)          // UTC calendar date (D5)
end     = today                                        // endDate = UTC today
days    = Max(0, daysBack)
start   = end.AddDays(-days)                           // D5, LITERALLY: endDate - DaysBack
floor   = today.AddMonths(-6).AddDays(1)                // D6 — CALENDAR months, never a day count
if (historyLimited && start < floor) { start = floor; clamped = true }
assert start <= end                                     // else THROW (see below)
→ ModComWindow(Today, Start, End, DaysBack, Clamped)
```

`ModComTime.ResolveWindow(startedAtUtc, daysBack, historyLimited)` is used by **BOTH** every
work-unit provider (the load window) and `ModernCommoditiesLoadValidator` (the validation window) —
the NGI `NgiTime.ResolveWindow` single-source-of-truth rule, so the two can never drift apart. **Do
not recompute this arithmetic anywhere else.**

**Five load-bearing properties:**

1. **The window is inclusive at both ends, and that is verified.** For the request
   `startDate=2026-08-20&endDate=2026-08-24`, the minimum `Last Updated Timestamp` observed is
   `2026-08-20 08:10:38` (the start day) and the maximum `2026-08-24 13:54:34` (the end day). So
   `endDate` covers its whole day and consecutive chunks must **not** share a boundary date (§3.4).
2. **`start = end - DaysBack` spans `DaysBack + 1` calendar days.** With the shipped `DaysBack = 30`
   the window is 31 days (`2026-07-25 .. 2026-08-24`). This is D5 **as written** and is *deliberately*
   not NGI's `end.AddDays(-(days-1))`: the extra day is free overlap that guarantees no gap between
   consecutive daily windows. **Do not "fix" it to 30 days** — and see §12.1 for the reviewer
   confirmation.
3. **A future `endDate` is harmless** (verified `200`), and UTC is ahead of both US-Central and the
   vendor's Mountain zone — so a UTC `end` can never clip a just-published row. This is why the basis
   is UTC (§7).
4. **The history clamp uses `AddMonths(-6)`, never a day count.** Probed to the day on 2026-08-24:
   `startDate=2026-02-24` → `200`; `2026-02-23` → `400 startDate must be within the last six months`.
   183 days back is 2026-02-22 (**already rejected**); 180 days is 2026-02-25 (**needlessly tight**).
   The `+1` day absorbs the vendor computing "today" in **its own** (Mountain) zone, removing an
   entire class of scheduled failure for the hours when UTC and Mountain disagree on the date.
   `MyTrades` is **not** clamped (all-time history) — only its row cap applies.
5. **An inverted range fails SILENTLY as an empty `200`** (verified — `startDate` after `endDate`
   returns a header-only body, **not** an error). A window-computation bug would therefore load zero
   rows forever without a single error. So `ResolveWindow` **throws** on `start > end` (reachable only
   via a negative `DaysBack` after clamping, i.e. a bug), and the reader asserts it again per request.

### 3.3 The resume key — **hot-only, hour granularity** (literal formats)

`core.LoadLog` skips a unit only when its `Key` is already recorded **successful**, so these literal
formats *are* the idempotency contract.

```
modcom:{endpointId}:{WindowStart:yyyyMMdd}-{WindowEnd:yyyyMMdd}:run={hot}
modcom:{endpointId}:{WindowStart:yyyyMMdd}-{WindowEnd:yyyyMMdd}:entity={slug}:run={hot}   // when LegalEntityName is set
```

Examples (run at `2026-08-24T14:05Z`, `DaysBack = 30`, `ChunkDays = 0`):

```
modcom:AllTrades:20260725-20260824:run=2026082414
modcom:MyTrades:20260725-20260824:run=2026082414
modcom:Settlements:20260725-20260824:run=2026082414
```

| Component | Rule |
|---|---|
| `{endpointId}` | The descriptor id. Two endpoints never share a key. |
| `{WindowStart}-{WindowEnd}` | The **actual** dates requested, after clamping and chunking. A change to `DaysBack`, `ChunkDays` or the history clamp therefore yields **new** keys rather than colliding with a differently-scoped previous success — and the key documents exactly what was fetched, which is why §4 of the API doc insists both dates are always sent explicitly instead of relying on the vendor's "defaults to today". |
| `{slug}` | Present **only** when `LegalEntityName` is configured: the value lowercased with every non-alphanumeric run collapsed to `-`. **This is not cosmetic** — a scoped pull returns a *subset*, so without it, switching the setting would make the run idempotently skip and quietly keep the old, wider data. Never the raw value in the key (it contains spaces and a comma). |
| `{hot}` | Per `ModComHotKeyStrategy` (below). **Always present — there is no settled variant.** |

`{hot}` per `ModComHotKeyStrategy` — the `PlResumeKey.HotToken` shape, all tokens **UTC**:

| Strategy | `{hot}` token | Cadence |
|---|---|---|
| **`RunHour`** (**default**) | `context.StartedAtUtc.ToString("yyyyMMddHH")` | re-pull **once per clock hour**; a second run inside the same hour idempotently **skips**. Matches the user's hourly/several-times-a-day schedule (U5). |
| `RunDate` | `yyyyMMdd` of the UTC run date | re-pull once per UTC day. |
| `RunId` | `context.RunId.ToString("N")` | re-pull on **every** invocation (diagnostics / a forced re-pull). |

**Why UTC and why hour granularity are the same decision.** `yyyyMMddHH` on the **UTC** clock is
**strictly monotonic** — it never repeats and never goes backwards. A Central or Mountain
`yyyyMMddHH` would repeat `01:00` on a fall-back night, so the key would go *backwards* and an
already-recorded success would suppress a legitimate re-pull for an hour. This is IHSPointLogic's
documented rationale (schedule in a local zone if you must; **stamp the key in UTC**), and it is why
D5 pins the whole window basis to UTC (§7).

**What re-pulls, and what that costs.** Every run in a new hour re-requests each endpoint's full
window and re-MERGEs it. At the defaults that is **3 requests and ~1,740 + ~37 + ~15,150 rows per
hour** — trivial, and there is **no conditional-GET available** (no `ETag`, no `Last-Modified`, no
`Cache-Control` on any response), so nothing cheaper exists. The MERGE is an idempotent upsert on the
natural key (§11), so a re-pull that changes nothing changes nothing.

### 3.4 Chunking, and the row-cap arithmetic

**There is no paging.** An over-cap request is **rejected with `400`**, not truncated. The *only* way
to stay under a cap is to narrow the date range — so `ChunkDays` is the sole scaling lever and must
stay configurable per endpoint.

Enumeration (`ModComWorkUnitProvider.GetWorkUnitsAsync`):

```
window    = ModComTime.ResolveWindow(context.StartedAtUtc, EffectiveDaysBack(d), d.HistoryLimited)
hot       = ModComTime.HotToken(settings.HotKeyStrategy, context)
chunkDays = EffectiveChunkDays(d)                       // 0 or <0 → one chunk = the whole window

if (chunkDays <= 0)  emit one unit [window.Start .. window.End]
else
    cursor = window.Start
    while (cursor <= window.End)
        chunkEnd = Min(cursor.AddDays(chunkDays - 1), window.End)   // chunkDays = INCLUSIVE days per chunk
        emit unit [cursor .. chunkEnd]
        cursor = chunkEnd.AddDays(1)                                // +1 — the API window is inclusive (§3.2)
```

**Chunks must not share a boundary date.** Both ends are inclusive (§3.2 #1), so `cursor =
chunkEnd.AddDays(1)`. Overlapping chunks would double-request rows; the MERGE would absorb it, but
the row-cap arithmetic and the `FileLog` row counts would both lie.

**Shipped default: `ChunkDays = 0` — the whole window in a single request, 3 requests per run** (U6:
no deep backfill on the first run; 30 days sits far under every cap).

#### `allTrades` / `myTrades` — cap `10,000`

Size against the **170-day** measurement (≈**57.9** rows/calendar day), **never** against a short
recent sample: the 5-day capture implies ≈19.6 rows/day and the two figures **do not reconcile**
(§12 item 1). The long window is the one that actually approached the cap.

| Window (calendar days) | Estimated rows @ ≈58/day | % of 10,000 |
|---|---:|---:|
| **31 (the shipped default: `DaysBack = 30`)** | ≈ 1,798 | **18 %** — safe |
| 60 | ≈ 3,480 | 35 % |
| 90 | ≈ 5,220 | 52 % |
| 170 | **9,849 (observed)** | **98.5 %** |
| **172** | — | **over cap → `400`** |

**Conclusion for a deep `allTrades` backfill:** the boundary is ≈**171 days**, so the full 6-month
history (≈183 days) **cannot** be pulled in one request. Set **`ChunkDays = 30`** (7 chunks at ≈18 %
of cap each). 30 is chosen over 90 (≈52 %) deliberately: the per-day rate is unreconciled by ~3×, so
90 days would sit one adverse month away from the cap. **Never set `ChunkDays > 90` on a trades
endpoint.**

**`myTrades`** is trivial by volume — **37 rows over 180 days** (≈0.21/day). Its cap is unreachable in
practice (10 years ≈ 750 rows ≈ 7.5 % of cap), so `ChunkDays = 0` is safe even for an all-time
backfill via a large `DaysBack` (which is exactly what the per-endpoint override in D10 exists for).
The real limits there are the **unprobed history depth** (§12 item 6) and the response timeout, not
the row cap.

#### `settlements` — cap `100,000`

The rate is **per *published* date, not per calendar day**: 1,443 rows across **2 published dates** =
≈**721 rows/published date** (720 Thu + 723 Fri; Sat/Sun/Mon published nothing). The naive
`1443 / 5 calendar days ≈ 289` understates the real volume by ~2.5×. Business days are ≈0.7 of
calendar days.

| Window (calendar days) | Published dates (approx) | Estimated rows | % of 100,000 |
|---|---:|---:|---:|
| **31 (the shipped default)** | ≈ 22 | ≈ **15,860** | **16 %** — safe |
| 60 | ≈ 43 | ≈ 31,000 | 31 % |
| 90 | ≈ 63 | ≈ 45,400 | 45 % |
| **183 (full allowed history)** | ≈ **130** | ≈ **93,700** | **≈ 94 % — WITHIN 6 % OF THE CAP** |

**Conclusion for a deep `settlements` backfill:** the 100,000-row cap **is** reachable inside the
allowed history — a single full-6-month request lands at ~94 % and **must not be attempted**. Set
**`ChunkDays = 60`** (4 chunks at ≤31 % each) or `30` to match `allTrades`. And note the moving
target: the curve grid is **not fixed** (720 vs 723 rows on consecutive days) and already runs out to
`DEC-31` term months — **any product addition or curve extension inflates rows/date**, so re-measure
before widening. The pre-flight warning in §1.6 step 3 exists for exactly this.

#### Where the estimate lives

`ModComDescriptors` carries the two measured rates (`EstimatedRowsPerCalendarDay`: 58 / 0.21 / ~505 =
721 × 0.7) purely so §1.6 step 3 can pre-warn and §5.6 #2 can quote real numbers in its error
message. They are **estimates for warnings only** — never a gate, never a reason to skip a request.

### 3.5 ⚠ The settled-key hazard, and why it cannot arise here

Sister loaders carry a documented hazard: a legitimate-empty response *completes the unit
successfully*, so a **stable (settled)** key records it done forever and a window that was empty only
because nothing had been published yet is never re-probed (NGI §3.5; AGSI's inert `21/21`).

**Here the hazard is structurally impossible, not merely inert.** There is no settled zone, no
`SettledAfterDays` setting and no stable key variant: **every** key carries `:run={hot}` (§3.3), so
**every** window is re-pulled on the next clock hour regardless of the previous outcome. A header-only
`200` — whether from a genuinely quiet `myTrades` window, a weekend-only settlements window, or the
current day's settlements not yet published (verified: the day's curve appears on a **later** run) —
is recorded, audited in `arm.FileLog`, and **re-probed an hour later**.

> **This is the invariant a future editor must not break.** If anyone adds a settled zone to "save
> requests", read Rationale A first: the saving is 3 requests an hour, and the cost is a permanently
> frozen pre-revision copy of every restated trade plus a permanently missing settlement date. If a
> settled zone is ever genuinely wanted for a deep-history tail, it must be gated on something the
> API does not offer — an immutability guarantee — and the loader would need a separate
> "history-only" endpoint scope, not a key change.

---

## 4. Authentication — HTTP Basic in a delegating handler

```
GET https://app.modcom.inc/api/integration/allTrades/v1?startDate=2026-07-25&endDate=2026-08-24
Authorization: Basic <base64(Username:Password)>
Accept: text/csv
```

### 4.1 `ModComBasicAuthHandler` (the `PlBasicAuthHandler` shape)

- A `DelegatingHandler` that stamps
  `request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential)` on **every**
  outgoing request, where `credential = base64(UTF8($"{Username}:{Password}"))`, computed **once** and
  cached in the handler (both values are immutable for a run).
- Registered **INNER of the retry policy** (so it re-stamps on every retry attempt) and **OUTER of
  the throttle** → **retry (OUTER) → auth → throttle (INNER)**, the repo standard (§1.5 step 4).
- **The `Authorization` header, the base64 credential and both raw values are never logged** — not at
  Trace, not in an exception message, not in a `FileLog` row. Combined with `.RemoveAllLoggers()` on
  the client, no request log line can leak it.
- **The credential travels in a header, not the URL and not a body.** So — unlike CWG/StormVista
  (`?apikey=`) — the **request URI is safe to log**, and unlike NGI/IIR there is no request body to
  suppress. `RequestPath` in `arm.FileLog` is nonetheless kept sanitised (path + the two/three query
  params only) on principle.

### 4.2 There is **no token** — so there is no re-mint, and `401` is terminal

The vendor publishes **no mint endpoint, no refresh endpoint, no expiry and nothing to cache**. This
is the sharp difference from IIR and NGI, whose designs both hinge on a re-mint-once-on-`401` handler.

| Consequence | Detail |
|---|---|
| No `ModComTokenProvider`, no `"…Token"` client | Nothing to mint. §0.1. |
| **No 401 replay** | A `401` means the configured credential is **wrong or revoked**. Replaying it cannot help. |
| `401` is **excluded from the retry policy** | Same reason. It must surface immediately and loudly. |
| `401` handling | Body is **empty (zero bytes)** — verified. Do not attempt to parse or quote it. Throw `ModComRequestException(Auth)` whose message names `core.Param(LoaderName='ModernCommodities', ParamName='Username'/'Password')` and **never** the values (§5.6). |
| Rotation | Multiple API keys can coexist, so a key rotates without an outage. **Nothing in the loader changes — only the `core.Param` value.** |

### 4.3 Throttle & retry

- `ModComRateLimiter` (singleton) + `ModComRateLimitingHandler` pace **all** ModCom traffic at
  `RequestsPerSecond` (default **1**). The vendor publishes no numeric rate limit, sends no
  `Retry-After` and no `X-RateLimit-*` header, and **no `429` was observed** across the probe
  session — but the PDF states a limit *"may be applied to requests that are made too rapidly"* and
  that the API *"is not intended to be rapidly polled, but rather queried periodically"*. With 3
  requests per run, pacing at 1 rps costs nothing and honours that instruction.
- `ModComHttpPolicy.Build(retryCount, retryDelayMs, logger)` retries **`429` / `5xx` / 408 / network**
  with exponential backoff and honours `Retry-After` when present (the `PlHttpPolicy` shape). It does
  **not** retry `400`, `401`, `403` or `404` — every one of those is deterministic here (§5.6).
- Responses arrive through **AWS CloudFront** with no compression negotiated and **no conditional-GET
  support**, so every run re-downloads its full window. At ~140 KB per 1,443 settlement rows this is
  immaterial today; it bounds how cheaply the window can be widened (§12 item 8).

---

## 5. Source readers — CSV over HTTP, tolerant, custom `ISourceReader`

Both readers implement `ISourceReader<ModComWorkUnit, TRow>.ReadAsync` **directly**, on the
`CwgSourceReader` model (`src/DataLoader.CWG/CwgSourceReader.cs`).

> ### ⚠⚠ `HttpJsonSourceReaderBase` is FORBIDDEN — twice over
>
> 1. **Every successful response is `Content-Type: text/csv`.** There is no JSON representation of any
>    endpoint: no `?format=json`, no `Accept`-negotiated alternative, no `.json` sibling path. A JSON
>    deserializer has nothing to bind.
> 2. **It calls `EnsureSuccessStatusCode()`**, which would throw before the reader could read the
>    response **body** — and the body is the *only* thing that distinguishes the four `400`s from one
>    another (§5.6). A bare "HTTP 400" is not actionable; the four causes have four different fixes.
>
> The closest precedents are **StormVista** (HTTP + CSV hybrid) and **CWG** (HTTP + CSV,
> descriptor-driven). Follow those.

Per-request flow (shared by both shapes):

1. Build the absolute URI from `BaseUrl` + `descriptor.Path` + `?startDate=…&endDate=…`
   (+ `&legalEntityName=…`, **URL-encoded** — the valid values contain spaces **and a comma**). Dates
   are formatted `yyyy-MM-dd` with `CultureInfo.InvariantCulture`; anything else is a `400`.
   Re-assert `WindowStart <= WindowEnd` (§3.2 #5). Log the sanitised path only.
2. `GET`. `httpStatus = (int)response.StatusCode`. Dispatch per **§5.6** — read the **body** on every
   non-2xx before throwing, and put it in both the log and the `arm.FileLog` row.
3. On `200`: read the body as text (UTF-8, BOM-tolerant), tokenize with `ModComCsv` (§5.1), validate
   and map the header (§5.2), map each data record with `ModComParse` (§5.3), drop-and-count the
   unkeyable (§5.4/§5.5).
4. Upsert the `arm.FileLog` row (§6) with the outcome, `httpStatus`, the parsed row count and the drop
   count. **No `FileLogId` is stamped onto any row** — the fact tables have no such column (§6).
5. Return the rows. `LoaderPipelineBase` identity-transforms and hands them to the sink.

**Exception discipline (both readers), the CWG/NGI posture:**
`catch (OperationCanceledException) → throw;` — **no `FileLog` write on a spent token** (the per-unit
timeout is already recorded by `core.LoadLog`). `catch (Exception)` → best-effort `FileLog "Failed"`
with `CancellationToken.None` (never masking the original exception), then **rethrow**, so
`LoaderPipelineBase` records the `core.LoadLog` failure and the run continues to the next unit
(**fail-a-block-not-the-run**).

### 5.1 `ModComCsv` — a real RFC-4180 tokenizer (a naive comma split corrupts rows)

Copy `src/DataLoader.CWG/CwgCsv.cs` (itself lifted from StormVista) **minus the CWG-specific `END.`
terminator special case** — ModCom files have no terminator line, and keeping that branch would let a
hypothetical single-field row silently truncate the payload. Keep: quote awareness, doubled `""` →
literal `"`, embedded CR/LF inside quotes, a single leading UTF-8 BOM stripped, truly-blank lines
dropped.

> **⚠⚠ NEVER `line.Split(',')`.** **Every** field in **every** row of **all three** endpoints is
> double-quoted (empty values appear as `""`), and five columns legitimately carry commas *inside* the
> quotes: `Bid Legal Name`, `Offer Legal Name`, `Bid Address`, `Offer Address`, `Contract Terms`.
> Real observed values:
> ```
> "P.O. Box 2844, 150 - 6 Avenue SW, Calgary, AB T2P 3E3"   ← 4 embedded commas
> "1001 Fannin Street, Suite 1500, Houston, TX 77002"        ← 3 embedded commas
> "ARM Energy Management, LLC"                              ← a comma inside a LEGAL NAME
> ```
> A naive split on the first value produces **4 extra columns and shifts every subsequent value
> left**, corrupting `Bid Commission` through `Product Type` on that row — **silently, on an HTTP
> 200**. This is precisely the failure class this design exists to prevent.

Transport details, all derived from verified `Content-Length` arithmetic (`docs/apis/…` §1.2):

- **The line terminator is a single byte, LF (`\n`) — not CRLF.** A parser that *requires* `\r\n`
  treats the whole payload as one line. Accept both anyway (the tokenizer does).
- **There is no UTF-8 BOM.** BOM-tolerant reading is harmless; nothing may *depend* on one.
- **All observed characters in all three captures are ASCII** → `VARCHAR` throughout, `NVARCHAR` not
  needed (§13 item 6).
- No field in any capture contains a `"` or an embedded newline, so quote-escaping and multi-line
  records were never exercised — a compliant parser handles both anyway, which is why a hand-rolled
  splitter is not acceptable.

### 5.2 `ModComHeaderMap` — bind by **literal header name**, never by convention

> **⚠⚠ 24 of the 34 trades headers contain a space; two contain an ampersand; one contains a slash.**
> ```
> "Pipeline/Terminal"        "GT&C"        "Click & Trade"
> "Apportionment Protected"  "Last Updated Timestamp"   "Unit of Measure"
> ```
> **No `System.Text.Json` / CSV naming policy matches these.** A property named `PipelineTerminal`,
> `GTandC` or `ClickAndTrade` binds to **nothing** — camelCase yields `pipelineTerminal`, snake_case
> `pipeline_terminal`, and neither is `Pipeline/Terminal`. This is exactly the trap that produced a
> silently all-NULL table for NGI (`"Point Code"`, `"Issue Date"`) and it must not recur.

Rules:

1. Build a `header → index` dictionary from the **first record**, matching **exact literal strings**
   (exact spelling, exact single spaces, exact casing, exact `&` and `/`) with `StringComparer.Ordinal`
   after `Trim()`. Every field read goes through `map["Last Updated Timestamp"]` — **never** through an
   ordinal literal and **never** through implicit POCO binding. (Positions are documented in §8 so the
   TVP can be ordered correctly; the *parser* locates by name so an inserted vendor column is detected
   rather than absorbed.)
2. **Header drift is tolerated, reported, and never silently absorbed:**
   - a **key** column missing (`Trade Number`; or any of the six settlements key columns) → the pull
     cannot be keyed at all → `FileLog "Failed"` then **THROW** `ModComRequestException(ShapeDrift)`;
   - a **non-key** expected column missing → **warn once** naming it, map it to NULL for every row,
     continue. (A vendor rename must not drop the other 33 columns.)
   - an **unexpected extra** column → **warn once** naming it, ignore it;
   - a column-count mismatch → **warn once** with both counts.
   Every drift warning is also written into the `FileLog` row's `ErrorMessage` as a non-fatal note
   (§6) — because there is no per-row provenance, the hub row is the only durable record that a pull
   was parsed against a drifted header.
3. **`200` with zero bytes, or a body whose first record is not a recognisable header** → `FileLog
   "Failed"` then **THROW**. The header is invariant across every capture; its absence means something
   is wrong (an error page, a truncated response), and a "tolerant" reader that returned zero rows here
   would report a successful empty load. Also **warn** when the response `Content-Type` is not
   `text/csv` (then still attempt the parse — the header assertion is the real gate).
4. A **`CODE_TESTER` contract test asserting both header lines verbatim** (the 497-byte 34-column
   trades line and the 9-column settlements line — text, order and count) is **the only regression net
   that exists**: Modern Commodities publishes **no** OpenAPI/Swagger/JSON-schema document of any kind,
   and the "separate CSV specification" the PDF promises **was never supplied**. Any vendor-side column
   change is silent by construction.

### 5.3 `ModComParse` — invariant value readers

| Helper | Rule |
|---|---|
| `Str(field, maxLen, isKey, ctx)` | `Trim()`; **empty → `null`**. **No sentinel remapping of any kind** — see the width guard below. |
| `Int(field)` | `int.TryParse(NumberStyles.Integer, InvariantCulture)`. Used only for `Trade Number`. Failure → the row is **unkeyable** (§5.4). |
| `Dec92(field, ctx)` | Empty → `null`. Else `decimal.TryParse(NumberStyles.Number \| AllowLeadingSign, InvariantCulture)`. **A leading minus MUST parse.** Unparseable → `null` + a counted warning. **Then range-guard** (below). |
| `Bit(field)` | Case-insensitive `True` / `False`. **Blank → `null`, NOT `false`.** Anything else → `null` + a counted warning. |
| `Date(field)` | Empty → `null`. Else `DateOnly.TryParseExact("yyyy-MM-dd", InvariantCulture)`. Used for `Term Start`, `Term End`, `Settlement Date`. |
| `Timestamp(field)` | Empty → `null`. Else `DateTime.TryParseExact("yyyy-MM-dd hh:mm:ss tt", InvariantCulture)`. |

Four traps this helper set exists to close:

- **⚠⚠ Timestamps are 12-hour with `AM`/`PM`.** `2026-08-24 01:44:41 PM` is **13:44:41**. The exact
  format is **`yyyy-MM-dd hh:mm:ss tt`** with **`CultureInfo.InvariantCulture`**. An `HH` (24-hour)
  format string mis-parses or fails **every afternoon timestamp** (roughly a third of rows), and a
  bare `DateTime.Parse` on a non-US locale may not recognise `PM` at all — losing 12 hours. The
  `12:xx PM` noon-hour rows are the subtlest case (`2026-08-06 12:42:37 PM` = 12:42:37).
  **No timezone offset is supplied and no timezone is stated anywhere** — store the value **exactly as
  given** in `DATETIME2(0)` and **do not shift it** (§7, §12 item 3). Seconds precision only; no
  fractional seconds were ever observed.
- **⚠⚠ There is no null sentinel other than the empty string.** In particular **`-` is NOT a
  sentinel** — Rationale B. Do not port `NgiParse.IsNullSentinel`, and do not add `"None"`, `"N/A"`,
  `"NULL"` or `"-"` to any sentinel list. (A `-` prefixing digits in `Price` is a **sign**: negative
  in 21/98 `allTrades`, 13/37 `myTrades` and **1,071/1,443 settlements** rows. Never a parse failure,
  never a non-negative `CHECK`.)
- **⚠ Numeric range guard (`Dec92`).** Every decimal target is `DECIMAL(9,2)` (ceiling
  `9,999,999.99`) — the user's type (D8), kept. The observed `Volume` maximum is **300,000**, so real
  headroom is **~33×, not ~1,000×**, and an over-range value is a hard **arithmetic-overflow error**
  that fails the *entire batch*, not a truncation. So `Dec92` checks `abs(value) <= 9_999_999.99m` and
  on breach **degrades that field to NULL + logs an error-level warning naming the column, the row's
  key and the value** — preserving the other 33 columns of the row and the rest of the batch. This is
  the platform's tolerant-parse contract applied to a numeric ceiling; §12 item 5 carries the standing
  recommendation to widen the column to `DECIMAL(13,2)` instead.
- **⚠ String width guard (`Str`).** A value longer than its target `VARCHAR(n)` raises *"String or
  binary data would be truncated"* and fails the whole batch. Widths are comfortable but not enormous
  — the two tightest ratios are trades `Product` **28/50** and `Location` **23/50**, and **both are
  `/`-joined on spread rows**, so a three-leg product or a longer location pair could approach 50. So:
  - **non-key** column over width → **truncate to `maxLen` + warn** (naming the column and the
    *length*, never the value itself for the counterparty/PII block);
  - **key** column over width (settlements `Product` / `Location` / `PieplineTerminal` / `PriceBasis` /
    `Term`) → **drop the row and count it**, at error level. A truncated key is worse than a missing
    row: it silently MERGEs onto a *different* logical entity.
  Both counts feed the `FileLog` drop counter and §9.

### 5.4 `ModComTradesSourceReader` (`allTrades/v1`, `myTrades/v1` — one class, one row type)

One reader serves both endpoints; the descriptor supplies the path, the row factory is shared, and the
**sink** is what differs. Per data record:

1. **`TradeNumber`** ← `Int(map["Trade Number"])`. **Blank or unparseable → DROP the row and count
   it** — it is the entire primary key and the merge key; an unkeyable row cannot be stored. (Never
   blank in 135/135 observed rows; the guard is house convention, not speculation.)
2. Map the remaining **33 columns** by literal name per §8.1, through the helpers in §5.3. Nothing is
   derived, computed or inferred:
   - **the 14 anonymised columns are read exactly the same way for both endpoints** — they simply
     arrive empty from `allTrades` and populate from `myTrades`. **Do not branch on the endpoint, and
     do not "optimise" them away from `arm.AllTrades`**: the venue could begin populating some of them
     and a narrower table would silently discard data (§8.1).
   - `Click & Trade` blank → **NULL, not `false`**: conflating them destroys the anonymisation signal
     (all 98 `allTrades` rows would read "not a click trade" instead of "unknown").
   - `Spread Trade Number` stays **text** (`VARCHAR(50)`) per the user's DDL — it is a reference, not
     a measure. **Do not "fix" it to `INT`.**
   - `Location`, `Pipeline/Terminal` and `Price Basis` are blank on exactly the `Product Type =
     'Financial'` rows (19/98, all `contracts/month`). That is **semantic** — a financial contract has
     no delivery location — so they map to NULL with **no warning**; §9 check 12 reports violations of
     the invariant in both directions.
3. **Dedup on `TradeNumber` before returning** — last wins, ordered by `LastUpdated` (non-NULL beats
   NULL). Structurally unnecessary within one request (0 duplicates in 98/98 and 37/37) but cheap, and
   it keeps the C# consistent with the proc's tie-break rule (§11) so the two can never disagree.
4. Log the counters at the end of the unit: `parsed=… dropped=… unparseableNumeric=… truncated=…
   headerDrift=…` — the platform's dropped-and-counted convention.
5. `FileLog` outcome: `rows.Count == 0 ? "NotAvailable" : "Success"`; context
   `ModComFileContext(EndpointId, WindowStart, WindowEnd, RunToken, RequestPath)`.

**Spread trades are persisted exactly as published, never filtered or reconciled.** A `Spread` arrives
as a 3-row group sharing one `Spread Trade Number` (the parent, whose `Spread Trade Number` equals its
own `Trade Number`, plus a `First Leg` and a `Second Leg`); the parent's `Volume` is **not additive**
with its legs' and its `Price` is a **differential** while the legs carry outright prices. Naively
summing `Volume` over the table **triple-counts** spread volume. The loader does nothing about this;
§9 check 13 surfaces it and the note belongs in the table comment for downstream consumers.

### 5.5 `ModComSettlementsSourceReader` (`settlements/v1`)

1. Map the **9 columns** by literal name per §8.2.
2. **All six PK columns are `NOT NULL`.** They were never blank in 1,443 rows (settlements contains
   **zero** empty fields anywhere), but guard anyway: **blank in any key column → DROP the row and
   count it** rather than inserting an empty-string key. Over-width in a key column → same (§5.3).
3. **`Location` / `Pipeline/Terminal` = `-` is persisted VERBATIM** — Rationale B. 82 of 1,443 rows,
   always **as a pair** (never one alone), all `Product = 'Sweet Guernsey Blend'` (41 term months × 2
   settlement dates): one blended product with no single physical location or pipeline.
4. `Term` here is **always a single `MMM-YY` month** — no `/`, no `~`, no quarters. **Unlike the trades
   `Term`** (max 13 chars, four shapes: `MMM-YY`, `nQ-YY`, `MMM-YY/MMM-YY`, `MMM-YY~MMM-YY`, **two
   different separators**). Do not share one `Term` parser or validator across the two shapes without
   allowing for both — and note the loader parses neither: `Term` is persisted as published text.
5. **A weekend-only or unpublished-day window returning nothing is normal, not an error.** Settlements
   publish on business days only, and the **current day's curve is routinely not yet published** when
   the loader runs (verified: a mid-afternoon North-American call returned nothing for that day). Both
   are legitimate gaps inside a `200`. Because the key is hot-only (§3.5), the day's curve is picked up
   by a later run automatically — which is a second reason there is no settled zone.
6. **Dedup on the 6-column key before returning** — last wins in file order. Within one request a
   duplicate key would mean the vendor published two prices for one (date, product, location,
   pipeline, basis, term); that never occurred in 1,443 rows. Count and warn on any collapse; §9
   reports it.
7. `FileLog` outcome and context exactly as §5.4 step 5.

### 5.6 Status contract & the error matrix — **there is no legitimate non-2xx**

> ### ⚠⚠ Explicit contrast with NGI — do NOT copy NGI's 404 tolerance here
>
> **NGI:** `404` is the *normal* case; ~58 of every 60 units legitimately 404, and failing on 404
> would fail ~97 % of the work.
>
> **ModernCommodities: emptiness is expressed INSIDE a `200`, as a header-only body.** Every `400` and
> every `401` is either **our bug** (a malformed date, an over-wide window, an inverted range) or a
> **configuration failure** (a bad credential). Both halves matter:
> - **Do not tolerate a `400`/`404` as "no data"** — that would swallow a real defect, e.g. a chunking
>   bug in which *every* request is over cap and the loader "succeeds" having loaded nothing.
> - **Do not treat a header-only `200` as a failure** — that would fail every genuinely quiet
>   `myTrades` window and every weekend settlements window.

| Status / condition | Meaning | `arm.FileLog` | Work unit | Retry? |
|:--:|---|---|---|---|
| **`200`** + header + ≥1 data row | Data. One request = the complete result set (no paging). | `Success`, `HttpStatus = 200`, `RowCount = n` | **succeeds** | — |
| **`200`** + header only (0 data rows) | **Legitimate empty read** — a quiet `myTrades` window, a weekend/unpublished settlements window, or (a bug) an inverted range. **Verified**: `Content-Length: 498`, header only. | `NotAvailable`, `HttpStatus = 200`, `RowCount = 0` | **SUCCEEDS with zero rows.** Never a failure, never a retry, never an alert. | never |
| **`200`** + zero bytes / unrecognisable header | Never observed. The header is invariant, so its absence means something is wrong. | `Failed` | **THROWS** (`ShapeDrift`) | never |
| **`400`** — body contains `more than the limit of` | **ROW CAP.** The request would return more rows than the endpoint's cap. **D11: classified distinctly.** | `Failed`, `HttpStatus = 400`, `ErrorMessage` = the verbatim body | **THROWS** `ModComRequestException(RowCap)` | never |
| **`400`** — body contains `within the last six months` | **HISTORY CAP.** Can only mean our clamp (§3.2 #4) is wrong or was bypassed. | `Failed`, body recorded | **THROWS** (`History`) | never |
| **`400`** — body starts `Invalid ` (e.g. `Invalid startDate`) | **Malformed date parameter** — our formatting bug. Match the substring `Invalid `, not the full sentence: only the `startDate` form was provoked, so `Invalid endDate` is presumed but unverified (§12 item 4). | `Failed`, body recorded | **THROWS** (`InvalidParam`) | never |
| **`400`** — body contains `Invalid legalEntityName` | **Bad `myTrades` scope setting.** The body *enumerates the valid values* — safe to log and to surface (no secret), and it is the de-facto discovery mechanism for that parameter. | `Failed`, body recorded | **THROWS** (`InvalidLegalEntity`) naming the setting | never |
| **`400`** — any other body | Unclassified. | `Failed`, body recorded | **THROWS** (`Other`) | never |
| **`401`** | Wrong / revoked credential. **Body is empty (zero bytes)** — do not parse or quote it. There is no token to refresh (§4.2). | `Failed`, `HttpStatus = 401` | **THROWS** (`Auth`), message naming the `core.Param` rows, **never the values** | never |
| **`403`** | Entitlement. Never observed. | `Failed` | **THROWS** | never |
| **`404`** on the endpoint path | **Not a data condition** — never observed. It means the path or the `/v1` version segment is wrong: a **deployment error**. | `Failed` | **THROWS**, loudly | never |
| **`429` / `5xx`** | Throttle / server. | `Failed` after Polly exhausts retries | **THROWS** (unit fails, **run continues**) | yes (Polly, `Retry-After` honoured) |
| cancellation | | **no `FileLog` write** (spent token) — `core.LoadLog` records it | rethrow `OperationCanceledException` | — |

**Mandatory rules this matrix imposes:**

1. **Always read and log the body of a non-2xx**, and always store it (truncated to
   `ErrorMessage`'s width) in the `arm.FileLog` row. A bare "HTTP 400" is not actionable: the four
   causes have four different fixes, and `FileLog` is the **only** provenance this loader has (§6).
2. **⚠⚠ The row-cap and history-cap messages MASK ONE ANOTHER.** A request that violates *both* (e.g.
   a 200-day `allTrades` window starting more than 6 months back) returns `400` with the **row-cap**
   message even though the start date is also out of range. **This already caused one wrong
   conclusion** — an early reading concluded "the history limit is 183 days" because a wide request
   failed, when it had actually failed on the row cap. So: **never infer one limit from the other's
   failure**, and match on the **stable substrings** above rather than on whole sentences (the row-cap
   message interpolates the applicable cap, so the settlements form reads `…limit of 100000 rows…`).
3. **The `RowCap` failure message must name the remedy** (D11). It carries: the endpoint id, the
   requested window and its length in days, the estimated row count and the cap (§3.4), the current
   effective `ChunkDays`, and the concrete instruction — *"reduce
   `Loaders:ModernCommodities:Endpoints:<EndpointId>:ChunkDays` (currently N) — recommended 30 for
   trades, 60 for settlements"*. Anyone who "fixes" a row-cap `400` by moving `startDate` forward has
   fixed it for the wrong reason and hidden the real constraint.
4. **`400` fails the unit, not the run.** Because every unit of an endpoint formats identically, a
   genuine `400` fails every unit of that endpoint and the pipeline reports `Success = false` — which
   is the intended loudness — while the other two pipelines still run (§1.2).

---

## 6. Provenance **without** `FileLogId` — the `arm.FileLog` hub (D1, D7)

**This section is the design's most deliberate deviation from house convention. Read it before
touching the TVPs.**

### 6.1 What was decided, and what it costs

The user's authoritative DDL gives `arm.AllTrades`, `arm.MyTrades` and `arm.Settlements` **no
`FileLogId` column** (and no `Id`, no `DateCreated`). Adding one would be a schema change they did not
ask for, so **D1 stands: provenance lives in `arm.FileLog` alone.**

| Consequence | Detail |
|---|---|
| **No per-row lineage** | Given a fact row you **cannot** ask "which pull produced this?". You can only ask "which pulls covered a window that contains it?" — a many-to-one answer. Accepted. |
| **No fact → hub join, ever** | Every check in `arm.usp_ValidateLoad` must be **window-based** or **`ModifiedAtUtc`-based** (§6.3). There is no `JOIN arm.FileLog ON FileLogId`, and NGI's check 17 (`IssueDateMatchesRequest`, which joins facts to the hub) **has no analogue here**. |
| **⚠ The TVPs begin with the first PAYLOAD column** | `arm.TradesTvp` starts at `TradeNumber`; `arm.SettlementsTvp` starts at `SettlementDate`. This **breaks the cross-loader "`FileLogId` is column 1 where present" rule** — intentionally, because the column does not exist. **The TVP binds by POSITION**, so this must be mirrored identically in `BuildTable`, in `sql/ModernCommodities/002`, and in the merge procs (§8.3). |
| **No `FileLogId` property on `TradeRow` / `SettlementRow`** | There is no `IModComFactRow { int FileLogId }` marker analogous to CWG's `ICwgFactRow`. The reader does **not** stamp anything onto rows after the hub upsert. |
| **`IModComFileLog.UpsertAsync` still returns the `FileLogId`** | Nothing consumes it as data; it is returned purely so the reader can log `FileLog #N` and so a future schema change has the value available without a signature change. |

### 6.2 Hub grain: **one row per pull** (D7)

```
UNIQUE (EndpointId, WindowStart, WindowEnd, RunToken)
```

| Column | Purpose |
|---|---|
| `Id` `INT IDENTITY` PK, `DateCreated` `DATETIME DEFAULT GETDATE()` | the house `FileLog` shape (PK first, `DateCreated` second) |
| `EndpointId` `INT NOT NULL` | FK → `arm.Endpoint(Id)`, seeded `{AllTrades, MyTrades, Settlements}` with their URL templates |
| `WindowStart` / `WindowEnd` `DATE NOT NULL` | the **actual** `startDate`/`endDate` sent — after clamping and chunking |
| `RunToken` `VARCHAR(32) NOT NULL` | the `{hot}` token — `yyyyMMddHH` (UTC) at the default; the run id under `RunId` |
| `StatusId` `INT NOT NULL` | FK → `arm.Status(Id)`, seeded `{Success, NotAvailable, Failed}` |
| `HttpStatus` `INT NULL` | 200 / 400 / 401 / 403 / 404 / 429 / 5xx |
| `[RowCount]` `INT NOT NULL DEFAULT 0` | **rows PARSED and handed to the sink** — *not* rows merged. The hub is written by the reader, before the sink runs. Document this in the column comment. |
| `DroppedRowCount` `INT NOT NULL DEFAULT 0` | unkeyable + over-width-key drops (§5.3–§5.5). **Without a fact-side provenance column this is the ONLY durable record that rows were discarded** — which is why it is a first-class column and a validated check (§9 check 3). |
| `ScopeLabel` `VARCHAR(100) NULL` | the `legalEntityName` when set, else NULL (`myTrades` only) |
| `RequestPath` `NVARCHAR(400) NOT NULL` | sanitised: path + `startDate`/`endDate`(+`legalEntityName`). **No credential ever appears in a ModCom URL** (it is a header), but the field stays sanitised on principle |
| `ErrorMessage` `NVARCHAR(400) NULL` | the **verbatim non-2xx body**, truncated — the four `400`s are only distinguishable by body text (§5.6 rule 1). Also carries a **non-fatal note** on a `Success` row (e.g. `HeaderDrift: unexpected column 'X'`); document that dual use |
| `LastCheckedUtc` `DATETIME2(3) NULL`, `ModifiedAtUtc` `DATETIME2(3) NULL` | freshness + the standard trailing stamp |

**Why per-pull and not per-window (an upsert over a stable key):** `FileLog` is the *only* provenance
(D1), so collapsing pulls would **erase** the fact that the 03:00 pull returned 0 rows after the 02:00
pull returned 20. Every hour's outcome for every window must survive. Volume is negligible: ~24 rows
per day per endpoint at the hourly cadence ≈ **~26 k rows/year/endpoint**.

**Why it is still a MERGE, not a blind INSERT:** a *failed* unit is retried by the next run within the
same hour (`core.LoadLog` only skips **successes**), and that retry has the same
`(EndpointId, WindowStart, WindowEnd, RunToken)`. The MERGE lets the retry's outcome **replace** the
failure in place, so the hub records the **last** outcome per pull identity rather than duplicating.
Distinct hours and distinct windows never collapse.

`ModComFileContext(string Endpoint, DateOnly WindowStart, DateOnly WindowEnd, string RunToken,
string? ScopeLabel, string RequestPath)` — the AGSI/NGI `…FileContext` shape, with the date pair +
run token replacing NGI's single `RepresentativeDate`. `SqlModComFileLog` calls
`arm.usp_UpsertFileLog` under `SqlWriteGate.AcquireAsync(KeyFor(ConnectionString,
"arm.usp_UpsertFileLog"))` — one shared key across all three endpoints, **distinct from the three
merge-proc keys**, so parallel work units cannot deadlock on the hub upsert and the hub upsert cannot
deadlock against a fact merge. Parameters are added with **explicit** `SqlDbType` + size (no implicit
`NVARCHAR → VARCHAR` conversion), and the proc resolves the `Endpoint`/`Status` **name strings** to
surrogate ids server-side (the CWG/AGSI/NGI posture), raising on an unknown name.

### 6.3 How `usp_ValidateLoad` works without a fact → hub join

Three scoping mechanisms replace the missing join. Every check in §9 uses one of them:

| Scope | Mechanism | Used for |
|---|---|---|
| **Window** | `arm.Settlements.SettlementDate BETWEEN @DateFrom AND @DateTo`; trades `LastUpdated >= @DateFrom AND LastUpdated < DATEADD(day, 1, @DateTo)` | per-endpoint row counts, settlement-date coverage, out-of-window detection |
| **Freshness** | `ModifiedAtUtc >= @ModifiedSinceUtc` — rows this run actually wrote | "did the run persist anything?", the pulled-vs-persisted substitute for the lost join |
| **Hub-only** | aggregates over `arm.FileLog` alone, filtered on `WindowEnd BETWEEN @DateFrom AND @DateTo` or `LastCheckedUtc >= @ModifiedSinceUtc` | outcome tallies, HTTP-status tallies, drop counts, `ErrorMessage` samples |

`@ModifiedSinceUtc` is passed as `context.StartedAtUtc - ValidationClockSkewMinutes` (default **5**,
§10). The tolerance exists because the fact rows are stamped by the **SQL Server's**
`SYSUTCDATETIME()` while the run start comes from the **host's** clock; without it, a few seconds of
skew would make every freshness check read zero.

> **⚠ The freshness check is INFORMATIONAL, and here is why.** The trades MERGE is guarded by
> last-updated recency (§11), so a re-pull that brings back an **unchanged or older** copy of a trade
> **does not update the row and does not touch `ModifiedAtUtc`**. A run can therefore legitimately
> pull 1,798 rows and persist 0 changes. The check reports the pair; it must **never** assert
> equality. The SQL comment must say so.

---

## 7. Timezone basis — **UTC** (D5)

There are **three** clocks in play and the design keeps them strictly separate.

| Clock | Where it is used | Rule |
|---|---|---|
| **UTC** | The window (`ModComTime.ResolveWindow`), the resume-key `{hot}` token, `ModifiedAtUtc`, `LastCheckedUtc`, the validator's window | **The only clock the loader computes with.** `DateOnly.FromDateTime(context.StartedAtUtc)`. |
| **The vendor's clock (Mountain / Calgary)** | The server-side default for an omitted `startDate`/`endDate`, and the basis of the 6-calendar-month history limit | **Never relied upon.** Both dates are **always sent explicitly** (§4 of the API doc), and the history clamp carries a `+1` day margin (§3.2 #4) so a UTC-vs-Mountain date rollover can never produce a guaranteed `400`. |
| **The payload's unstated clock** | `Executed Timestamp`, `Last Updated Timestamp` | **Stored exactly as given in `DATETIME2(0)`, never shifted, never normalised.** |

**Why UTC and not US-Central (NGI's basis):**

- **It cannot clip.** UTC is ahead of both US-Central and the vendor's Mountain zone, so a UTC `today`
  as `endDate` is never *behind* the venue's day — and a future `endDate` is verified harmless
  (`200`). A Central or Mountain basis would, for part of each day, request a window that ends before
  the venue's current day.
- **`yyyyMMddHH` is strictly monotonic in UTC and NOT in any DST zone.** The resume key is
  hour-granular (Rationale A), and `01:00` Central/Mountain occurs **twice** on a fall-back night — so
  a local-zone hour token would repeat, the key would go backwards, and a legitimate re-pull would be
  suppressed for an hour. This is IHSPointLogic's documented rule: **schedule in a local zone if you
  like, but stamp the key in UTC.** It applies with full force here and is the reason the *window*
  basis is UTC too — one clock for both, so they cannot disagree.
- **There is no host time-zone lookup and therefore no `UsingUtcFallback` warning** (contrast
  `NgiTime`/`AgsiTime`, which resolve `America/Chicago`/CET and warn when the host's tz database is
  missing). Nothing to resolve, nothing to misconfigure: one fewer failure mode.

**⚠ What UTC does NOT fix.** The payload timestamps carry **no offset and no stated zone anywhere** —
not in the PDF, not in the body, not in a response header. Consequently:

- **Never join `Executed`/`LastUpdated` to a UTC-based column and assume they align.** Any
  cross-source time join must resolve §12 item 3 first.
- **The trades window predicate is the vendor's `Last Updated` clock, but the window we send is
  UTC-based.** So a row can legitimately fall a few hours outside a *literal* UTC reading of the
  requested window. §9 check 10 is therefore **informational** with an explicit
  `-- do NOT set ExpectedCount = 0 here` comment — a strict-zero assertion would false-positive at
  every window boundary.

> **⚠ An unverified but useful observation, recorded for §12 item 3.** In the `allTrades` capture the
> maximum `Last Updated Timestamp` is `2026-08-24 01:54:34 PM` (13:54:34) while the response's own
> `Date` header reads `Mon, 24 Aug 2026 19:55:50 GMT` — i.e. **13:55:50 US-Central** and **12:55:50
> Mountain**. A timestamp of 13:54 cannot be one minute in the *future*, so the payload clock is
> almost certainly **UTC−5/−6 (Central-ish), not the vendor's own Mountain zone** as the Calgary
> address would suggest. That is **inference from a single capture, not a verified fact** — it changes
> nothing in this design (store as given, do not shift) but it is the sharpest evidence available and
> should go in the question to the vendor.

---

## 8. Full-field mapping

The user's DDL (`MODCOM_DDL.sql`) is authoritative for names, order, widths and nullability. **Source
CSV order IS the target column order IS the TVP order** — `#` is the 1-based CSV ordinal. Nothing is
dropped, nothing is derived, nothing is renamed except where the DDL renames it.

Two columns exist on every table and **never cross the TVP**: none. Precisely one column is
DB-supplied — `ModifiedAtUtc DATETIME2(3)`, **stamped by the merge proc on insert and on match**
(its `DEFAULT` is `SYSUTCDATETIME()` per D3 and is effectively unreachable). **There is no
`FileLogId`, no `Id` and no `DateCreated`** on any of the three tables (§6).

### 8.1 `allTrades/v1` and `myTrades/v1` → `arm.AllTrades` / `arm.MyTrades` (34 columns, PK `TradeNumber`)

**One shape, two tables** — the header is byte-identical (497 bytes) and the column order is
identical, so one `TradeRow`, one reader, one `BuildTable`, one TVP (§1.1). `A` = populated in the 98
live `allTrades` rows; `M` = populated in the 37 live `myTrades` rows.

| # | Source header (verbatim) | `TradeRow` prop | Column | SQL type | Null? | A | M | Mapping notes |
|:-:|---|---|---|---|:--:|:--:|:--:|---|
| 1 | `Trade Number` | `TradeNumber` | `TradeNumber` | `INT` | **No** | 98 | 37 | **PK / merge key.** `Int`; blank or unparseable → **drop-and-count** (§5.4 #1). |
| 2 | `State` | `State` | `State` | `VARCHAR(50)` | Yes | 98 | 37 | `Finalized` / `Cancelled`. **A revision can flip this** — the whole point of the re-pull (Rationale A). **No `CHECK`, no enum.** |
| 3 | `Product` | `Product` | `Product` | `VARCHAR(50)` | Yes | 98 | 37 | `/`-joined on spread rows. Observed max **28** — the tightest width ratio; width-guarded (§5.3). |
| 4 | `Location` | `Location` | `Location` | `VARCHAR(50)` | Yes | **79** | 37 | Blank on exactly the 19 `Financial` rows — **semantic, not a defect** (§5.4 #2). Observed max **23**. |
| 5 | `Pipeline/Terminal` | `PipelineTerminal` | `PipelineTerminal` | `VARCHAR(256)` | Yes | **79** | 37 | Header contains a **slash**. Blank on the same 19 rows. Spelled **correctly** here (contrast §8.2 #4). |
| 6 | `Price Basis` | `PriceBasis` | `PriceBasis` | `VARCHAR(256)` | Yes | **79** | 37 | Blank on the same 19 rows. Free text; ` + `-joined index expression, observed max **45**. **Not the same value space as settlements' `PriceBasis`** — no shared dimension (§0.1). |
| 7 | `Term` | `Term` | `Term` | `VARCHAR(256)` | Yes | 98 | 37 | **Four shapes, two separators**: `MMM-YY`, `nQ-YY`, `MMM-YY/MMM-YY`, `MMM-YY~MMM-YY`. Persisted as published; never parsed. |
| 8 | `Term Start` | `TermStart` | `TermStart` | `DATE` | Yes | 98 | 37 | `yyyy-MM-dd`; always the 1st of a month. |
| 9 | `Term End` | `TermEnd` | `TermEnd` | `DATE` | Yes | 98 | 37 | Always a month end; leap-year correct (`2028-02-29`). |
| 10 | `Price` | `Price` | `Price` | `DECIMAL(9,2)` | Yes | 98 | 37 | **SIGNED** — negative in 21/98 and 13/37 (location/quality differentials). `0.00` is a real value. **No non-negative `CHECK`.** |
| 11 | `Volume` | `Volume` | `Volume` | `DECIMAL(9,2)` | Yes | 98 | 37 | Integer-valued in every observed row (**never a decimal point**); observed max **300,000**. `DECIMAL(9,2)` caps at 9,999,999.99 → **~33× headroom**, range-guarded in `Dec92` (§5.3, §12 item 5). |
| 12 | `Unit of Measure` | `UnitOfMeasure` | `UnitOfMeasure` | `VARCHAR(50)` | Yes | 98 | 37 | `m3/month`, `bbls/day`, `bbls/month`, `contracts/month`. **No enum** — a new unit must load. |
| 13 | `Executed Timestamp` | `Executed` | `Executed` | `DATETIME2(0)` | Yes | 98 | 37 | **12-hour `tt`** (§5.3). **NOT the window predicate** — 5 of 98 rows fall outside the requested window on this column, which is the revision signal (§9 check 11). |
| 14 | `Last Updated Timestamp` | `LastUpdated` | `LastUpdated` | `DATETIME2(0)` | Yes | 98 | 37 | **12-hour `tt`.** **THIS is the window predicate AND the MERGE recency guard** (Rationale A, §11). Longest header (22 chars). |
| 15 | `Trade Type` | `TradeType` | `TradeType` | `VARCHAR(50)` | Yes | 98 | 37 | `Outright` / `Spread` / `First Leg` / `Second Leg`. |
| 16 | `Side` | `Side` | `Side` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED in `allTrades`.** `Buy`/`Sell` in `myTrades`. |
| 17 | `Bid Trader` | `BidTrader` | `BidTrader` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED. PII** — fixtures must be anonymised (D9). |
| 18 | `Bid Legal Name` | `BidLegalName` | `BidLegalName` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED.** **Contains commas** (`ARM Energy Management, LLC`). Max 41. |
| 19 | `Bid Address` | `BidAddress` | `BidAddress` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED.** Up to **4 embedded commas**, max 53. |
| 20 | `Bid Commission` | `BidCommission` | `BidCommission` | `DECIMAL(9,2)` | Yes | **0** | **16** | **ANONYMISED and sparse even in `myTrades`.** Populated on the company's own side only. Blank → NULL. |
| 21 | `Offer Trader` | `OfferTrader` | `OfferTrader` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED. PII.** |
| 22 | `Offer Legal Name` | `OfferLegalName` | `OfferLegalName` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED.** Contains commas. |
| 23 | `Offer Address` | `OfferAddress` | `OfferAddress` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED.** Contains commas. |
| 24 | `Offer Commission` | `OfferCommission` | `OfferCommission` | `DECIMAL(9,2)` | Yes | **0** | **21** | **ANONYMISED and sparse.** Complementary to #20 (`16 + 21 = 37`) — **do not build a "commission always present" or "both sides present" rule** (§9 note). |
| 25 | `Spread Trade Number` | `SpreadTradeNumber` | `SpreadTradeNumber` | `VARCHAR(50)` | Yes | **42** | 3 | **NOT anonymised.** Numeric-looking but **stays text** per the DDL. Blank on `Outright`; on a `Spread` it equals the row's own `Trade Number`; on a leg it points to the parent (§5.4). |
| 26 | `Apportionment Protected` | `ApportionmentProtected` | `ApportionmentProtected` | `BIT` | Yes | 98 | 37 | `True`/`False`; **`True` never observed** (§12 item 7). Longest header (23 chars). Blank → NULL. |
| 27 | `Clearing ID` | `ClearingID` | `ClearingID` | `VARCHAR(50)` | Yes | **0** | **0** | ⚠ **Blank in 100 % of BOTH endpoints — NOT an anonymised column.** It is *expected-NULL everywhere*. **Never assert it populated anywhere** (§9 check 8): a rule demanding a value in `arm.MyTrades` would fail **every row**. Width is the user's allocation, not an observed maximum. |
| 28 | `Settlement Currency` | `SettlementCurrency` | `SettlementCurrency` | `VARCHAR(50)` | Yes | **0** | 37 | **ANONYMISED.** Only `USD` observed — **do not constrain to it** (a Canadian venue may settle `CAD`). |
| 29 | `Contract Terms` | `ContractTerms` | `ContractTerms` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED.** Contains commas. *"Whose paper governs"*, **not** a counterparty identifier (2 of 37 name ARM itself) — §12 item 9. |
| 30 | `GT&C` | `GTandC` | `GTandC` | `VARCHAR(256)` | Yes | **0** | 37 | **ANONYMISED.** Header contains an **ampersand and no space**. |
| 31 | `Notes` | `Notes` | `Notes` | `VARCHAR(8000)` | Yes | **0** | **3** | **ANONYMISED and very sparse.** Free operator text (observed max 31); the generous width is right. **PII-adjacent** — never log the value, only its length (§5.3). |
| 32 | `In Index` | `InIndex` | `InIndex` | `BIT` | Yes | 98 | 37 | `True` 11/`False` 87 (A); `True` 22/`False` 15 (M). **Not anonymised.** |
| 33 | `Click & Trade` | `ClickAndTrade` | `ClickAndTrade` | `BIT` | Yes | **0** | 37 | **ANONYMISED.** Header has **ampersand AND spaces**. **Blank → NULL, not `false`** (§5.4 #2). |
| 34 | `Product Type` | `ProductType` | `ProductType` | `VARCHAR(50)` | Yes | 98 | 37 | `Physical` / `Financial`. **The discriminator for #4/#5/#6 blankness.** `myTrades` had **no** `Financial` rows at all, so the invariant is verified on `allTrades` only (§12 item 8). |
| — | *(proc)* | — | `ModifiedAtUtc` | `DATETIME2(3)` | Yes | — | — | Stamped by the MERGE; **never in the TVP**. `DEFAULT SYSUTCDATETIME()` (D3). |

**Column count: 34 payload + 1 stamped. Every source column is mapped. None dropped.**

**Decisions recorded (do not re-litigate):**

- **The full 34-column set exists on BOTH tables**, per the user's DDL. `arm.AllTrades` simply carries
  NULLs in the 14 anonymised columns (15 counting `ClearingID`). **Do not "optimise" them away**: the
  venue could begin populating some of them and a narrower table would silently discard data. The
  validator reports them as **informational, expected** (§9 check 7) — 100 % NULL there is *correct*.
- **`TradeNumber` alone is the merge key.** A revision arrives as the **same** `TradeNumber` with a
  later `Last Updated Timestamp` and a changed payload; a key including the timestamp or a date would
  **insert a duplicate** instead of correcting the existing row (Rationale A #3).
- **No surrogate `Id`, no `DateCreated`.** Fact-shaped tables, so the missing `Id` is conventional;
  the missing `DateCreated` is a stated deviation — **follow the user's DDL** (§13 item 1).
- **The same `TradeNumber` in both tables is intended**, with no FK and no cross-table dedup (§1.2).

### 8.2 `settlements/v1` → `arm.Settlements` (9 columns, PK = 6 of them)

Counts are over the 1,443 live rows. **Zero blank fields anywhere in 1,443 × 9** — the only
absent-value representation is the literal `-` in #3/#4.

| # | Source header (verbatim) | `SettlementRow` prop | Column | SQL type | Null? | Mapping notes |
|:-:|---|---|---|---|:--:|---|
| 1 | `Settlement Date` | `SettlementDate` | `SettlementDate` | `DATE` | **No** | **PK 1/6.** The window predicate for this endpoint (exact, no leakage). Business days only; the request day is routinely **not yet published** (§5.5). |
| 2 | `Product` | `Product` | `Product` | `VARCHAR(50)` | **No** | **PK 2/6.** `/`-joined on spread products; observed max 22. |
| 3 | `Location` | `Location` | `Location` | `VARCHAR(50)` | **No** | **PK 3/6.** ⚠ **Literal `-` on 82 rows — persist VERBATIM** (Rationale B). |
| 4 | `Pipeline/Terminal` | **`PieplineTerminal`** *(sic)* | **`PieplineTerminal`** *(sic)* | `VARCHAR(50)` | **No** | **PK 4/6.** ⚠ **The misspelling is the user's, it is IN THE PRIMARY KEY, and it is reproduced EXACTLY — in the table, the TVP, the proc AND the C# property name** (D2). Misspelling the C# property too is deliberate: it makes the intent obvious so nobody later "tidies" the name and silently breaks the **positional** TVP binding. The two trades tables spell the same concept **correctly** (§8.1 #5), so both spellings coexist on purpose. Header contains a **slash**. Literal `-` on the same 82 rows. |
| 5 | `Price Basis` | `PriceBasis` | `PriceBasis` | `VARCHAR(100)` | **No** | **PK 5/6.** Only 2 values: `WTI CMA` (1,391), `USD $` (52). ⚠ **`USD $` contains a space and a `$` — no trimming, no stripping, no normalisation** (it is a key). |
| 6 | `Term` | `Term` | `Term` | `VARCHAR(50)` | **No** | **PK 6/6.** **Always a single `MMM-YY` month** — never `/`, `~` or a quarter (contrast §8.1 #7). |
| 7 | `Term Start` | `TermStart` | `TermStart` | `DATE` | Yes | Never blank in 1,443/1,443, but the DDL column is NULLable — **keep it so** (tolerant-parse contract: only an unusable KEY drops a row). |
| 8 | `Term End` | `TermEnd` | `TermEnd` | `DATE` | Yes | Extends to **`2031-12-31`** — the curve runs ≈**5.4 years forward**, and 192 rows are in the 2030s. **No tighter `DATE` range assumption anywhere.** |
| 9 | `Price` | `Price` | `Price` | `DECIMAL(9,2)` | Yes | **SIGNED — negative in 1,071/1,443 (74 %).** `0.00` is real. **No non-negative `CHECK`.** |
| — | *(proc)* | — | `ModifiedAtUtc` | `DATETIME2(3)` | Yes | Stamped by the MERGE; **never in the TVP**. |

**Column count: 9 payload + 1 stamped. Every source column is mapped. None dropped.**

**Decisions recorded:**

- **A settlement price revision OVERWRITES.** `Price` sits **outside** the PK, so there is no history.
  Contrast OPIS's `arm.LPReportHistory`, which puts the revision code **in** the key precisely to keep
  both prints. This is fine **if** settlements are never restated — which is **unverified** (§12 item
  2, the highest-value open question). The payload carries **no** revision, version, status or as-of
  field, so a restatement would be indistinguishable from the original except by comparing values.
  The precedent exists if it proves false.
- **The curve grid is not fixed** — 720 rows Thursday vs 723 Friday. Validation uses a **band**, never
  an equality (§9 check 15).
- **No shared `PriceBasis` (or `Product`/`Location`) dimension with the trades tables** — same names,
  different value spaces (§0.1).

### 8.3 TVP / column-order contract (load-bearing)

**The TVP binds BY POSITION**, so a silent reorder corrupts every loaded row. These orders MUST be
mirrored **identically** across four places: the `001` table body, the `002` TVP type, the `003` merge
proc's `SELECT`/`INSERT`/`UPDATE` lists, and each C# sink's `BuildTable` (plus its unit test).

**`arm.TradesTvp` — 34 columns, starting at the FIRST PAYLOAD COLUMN:**
```
TradeNumber, State, Product, Location, PipelineTerminal, PriceBasis, Term, TermStart, TermEnd,
Price, Volume, UnitOfMeasure, Executed, LastUpdated, TradeType, Side, BidTrader, BidLegalName,
BidAddress, BidCommission, OfferTrader, OfferLegalName, OfferAddress, OfferCommission,
SpreadTradeNumber, ApportionmentProtected, ClearingID, SettlementCurrency, ContractTerms, GTandC,
Notes, InIndex, ClickAndTrade, ProductType
```

**`arm.SettlementsTvp` — 9 columns, starting at the FIRST PAYLOAD COLUMN:**
```
SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term, TermStart, TermEnd, Price
```

- **⚠ NO `FileLogId` — the TVPs begin with the first payload column.** A deliberate, stated break from
  the cross-loader "`FileLogId` is column 1 where present" rule, because the column does not exist on
  these tables (§6.1). Anyone "restoring the convention" must add the column to the user's three
  tables first, which is out of scope.
- **`ModifiedAtUtc` never crosses the TVP** — the merge proc stamps it.
- **Both trades tables use the SAME TVP type `arm.TradesTvp`** and the same C# `BuildTable` (§1.1).
  This is a naming deviation from the house `arm.<Table>Tvp` pattern, chosen because **34 columns
  duplicated across two type definitions is precisely the drift the positional contract cannot
  survive**. §13 item 3 records the alternative if DATABASE_DEVELOPER prefers two identically-shaped
  per-table types (the C# then needs two `TableValuedParameterType` strings and nothing else).
- **`SqlSinkBase` passes the TVP as `@Records`** — all three merge procs must name their TVP parameter
  **`@Records`** (the AGSI/NGI convention; IIR's `@Rows` needed a custom sink base, which this loader
  does not use). `ModComSqlSinkBase<TRow>` sets `StoredProcedureName`, `TableValuedParameterType`,
  `GetConnectionString` and `ProcedureReturnsRowCount = true`, and **de-dups the batch on the merge
  key before building the DataTable** (`TradeNumber` ordered by `LastUpdated` desc; the settlements
  6-column key, last-in-file wins).
- C# DataTable column CLR types: `int`, `string`, `DateTime` (for both `DATE` and `DATETIME2(0)`),
  `decimal`, `bool` — nullables via `SqlSinkBase.DbNullable`/`NullIfEmpty`.
- **Scalars that are constant per batch are NOT TVP columns.** There are none here — no `@RunDate`,
  no `@WindowStart`: nothing about the window is persisted on the fact rows (that is `arm.FileLog`'s
  job, §6).

---

## 9. Post-load validation — `arm.usp_ValidateLoad` + `ModernCommoditiesLoadValidator`

`LoaderPipelineBase` has no post-load hook and the module owns orchestration, so — following the
StormVista/AGSI/IIR/NGI precedent — **`ModernCommoditiesLoadValidator`** runs as a module-level step
in `RunAsync` **after all enabled pipelines complete** (§1.6 step 6), calling

```
arm.usp_ValidateLoad(@DateFrom DATE, @DateTo DATE, @ModifiedSinceUtc DATETIME2(3) = NULL)
```

The window comes from **`ModComTime.ResolveWindow`** — the **same helper** the work-unit providers
call (§3.2), so the validation window can never drift from the load window. Because `DaysBack` and the
history clamp are **per-endpoint** (D10), the validator passes the **union**: `@DateTo = today`,
`@DateFrom = MIN(start)` across the *enabled* endpoints (with each endpoint's own
`HistoryLimited` flag applied). At the shipped defaults all three windows are identical, so the union
is the window. `@ModifiedSinceUtc = context.StartedAtUtc − ValidationClockSkewMinutes` (§6.3).

`arm.usp_ValidateLoad` returns **ONE** result set with the uniform shape
`CheckName, Scope, ExpectedCount, ActualCount, Detail`, `ORDER BY CheckName, Scope`. The C# treats a
row with a **non-NULL `ExpectedCount` that differs from `ActualCount`** as an **anomaly →
`LogWarning`**, and every other row as **informational → `LogInformation`**, then logs a summary
(`N anomaly row(s), M informational row(s)`).

> ### ⚠ IT IS OBSERVATIONAL AND MUST NEVER THROW.
> It catches and logs its own exceptions (`LogError`, non-fatal) and only rethrows
> `OperationCanceledException`. A legitimately sparse window — a quiet `myTrades` month, a weekend-only
> settlements window, a run whose recency guard updated nothing (§6.3) — **must not be able to fail an
> otherwise-good load**. Several checks below are informational *on purpose*; **do not "promote" them**.
> Argument validation mirrors AGSI: `RAISERROR` if `@DateFrom`/`@DateTo` is NULL or `@DateFrom > @DateTo`.

### 9.1 Check catalogue

`Scope` carries the endpoint/table name (or the grouped value) so one result set covers all three
targets. **No check joins a fact table to `arm.FileLog`** — there is no `FileLogId` (§6.3).

| # | `CheckName` | Scope | `Expected` | Meaning / rationale |
|:-:|---|---|:--:|---|
| 1 | `RowCountInWindow` | `AllTrades` / `MyTrades` / `Settlements` | **NULL** (info) | Rows in the window per target — trades by `LastUpdated`, settlements by `SettlementDate` (§6.3). Detail carries the observed baselines (≈58/day trades, ≈721/published-date settlements) **as snapshots, not contracts**. |
| 2 | `RowsPersistedThisRun` | per target | **NULL** (info) | `COUNT(*) WHERE ModifiedAtUtc >= @ModifiedSinceUtc`, alongside `SUM([RowCount])` from the run's `arm.FileLog` rows. **This is the substitute for the lost fact→hub join.** `-- do NOT set ExpectedCount here: the trades recency guard legitimately skips updates, so pulled != persisted is NORMAL` (§6.3). |
| 3 | `UnkeyableDrops` | per endpoint | **0** | `SUM(DroppedRowCount)` over the run's `arm.FileLog` rows. A drop means a blank/unparseable `Trade Number`, a blank settlements key column, or an over-width key (§5.3). **Observed 0 across all 1,578 live rows**, so 0 is a genuine expectation and a non-zero value is actionable. The **only** durable record of discarded rows (§6.2). |
| 4 | `PkDuplicates` | per target | **0** | Keys occurring more than once. Structurally guaranteed by each PK — kept as a cheap honest assertion that stays meaningful if anyone ever disables the constraint for a bulk load. Verified 0 in every capture (98/98, 37/37, 1,443 with 0 collisions on the 6-column key). |
| 5 | `TermOrdering` | per target | **0** | Rows with `TermStart > TermEnd` (both non-NULL). Zero violations observed. |
| 6 | `SettlementSentinelRows` | `Settlements` | **NULL** (info) | Rows with `Location = '-'` **or** `PieplineTerminal = '-'`. Detail: baseline **82 / 1,443 ≈ 5.7 %**, all `Product = 'Sweet Guernsey Blend'`. **Informational — the sentinel is CORRECT data** (Rationale B); `-- NEVER normalise these to NULL: they are PRIMARY KEY values`. |
| 7 | `SettlementSentinelPairing` | `Settlements` | **0** | Rows where **exactly one** of `Location`/`PieplineTerminal` is `-`. The two were **always** `-` together in 82/82 rows; a one-sided sentinel would signal a vendor change or a parse bug. |
| 8 | `StateDistribution` | `State=<value>` | **NULL** (info) | Row counts per `State` per trades table. Baselines: `Finalized` 95 / `Cancelled` 3 (A), 35 / 2 (M). **Watch for a jump in `Cancelled`, never fail on one** — a cancellation is exactly what the re-pull exists to capture. |
| 9 | `AnonymisedColumnsNull` | `AllTrades` | **NULL (INFORMATIONAL ONLY)** | Per-column NULL counts for the **14 anonymised columns**. **100 % NULL in `arm.AllTrades` is CORRECT, not a defect** (§8.1). `-- informational only, NEVER an error`. Conversely a *drop* in population on `arm.MyTrades` (check 10) is worth a look. |
| 10 | `MyTradesAttributionRate` | `MyTrades` | **NULL** (info) | Population rate of the 14 columns on `arm.MyTrades`. Baselines: 37/37 for most, **16/37 `BidCommission`, 21/37 `OfferCommission`, 3/37 `Notes`**. `-- do NOT require both commissions on a row: 16 + 21 = 37, they are COMPLEMENTARY (the company's own side only)` — a "both present" or "commission always present" rule must not ship. |
| 11 | `ClearingIdPopulated` | both trades tables | **NULL (INFORMATIONAL ONLY)** | Non-NULL `ClearingID` count. **Baseline 0 on BOTH endpoints — `ClearingID` has never been populated by anything** (§8.1 #27). `-- MUST NOT be asserted populated anywhere; a rule expecting a value in arm.MyTrades would fail EVERY row`. A first non-zero value is a *discovery* to re-check the column's format and width (§12 item 10), not a failure. |
| 12 | `FinancialBlankInvariant` | both trades tables | **NULL** (info) | Reported **both ways**: rows with `ProductType = 'Financial'` **and** any of `Location`/`PipelineTerminal`/`PriceBasis` non-NULL; **and** rows with all three NULL that are **not** `Financial`. Verified 0/0 on `allTrades` (19 ⇔ 19). `myTrades` had **no** `Financial` rows, so this is unverified there (§12 item 8) — hence informational, not `Expected = 0`. |
| 13 | `SpreadGroupIntegrity` | both trades tables | **NULL (INFORMATIONAL ONLY)** | Populated `SpreadTradeNumber` values that do not resolve to a `Spread` row in the same table, and groups whose size is not exactly 3 (parent + `First Leg` + `Second Leg`). **Report, never enforce**: a group can legitimately straddle the window boundary (`LastUpdated` differs per leg). Detail must warn that **summing `Volume` over the table triple-counts spread volume** and that a parent's `Price` is a differential (§5.4). |
| 14 | `SettlementDateCoverage` | `Settlements` | **NULL** (info) | Distinct `SettlementDate`s present vs calendar days in the window. **Weekend gaps and an unpublished current day are EXPECTED** (§5.5). `-- informational only, NEVER an error`. |
| 15 | `SettlementRowsPerDateBand` | `Settlements` | **NULL** (info) | Count of settlement dates whose row count falls outside **±20 %** of the window's median rows-per-date. **A BAND, never an equality** — the grid shifts day to day (720 vs 723) and the curve depth grows (§8.2). Flags a *jump*, not the level. |
| 16 | `LastUpdatedOutsideWindow` | both trades tables | **NULL (INFORMATIONAL ONLY)** | Rows whose `LastUpdated` falls outside `[@DateFrom, @DateTo]`. Live-verified **0 of 98** on a literal reading — but the payload's timezone is **unstated** and the window is **UTC** (§7), so a boundary row can legitimately fall a few hours out. `-- do NOT set ExpectedCount = 0 here: the payload clock is unverified (design §12 item 3)`. |
| 17 | `ExecutedOutsideWindow` | both trades tables | **NULL** (info) | Rows whose `Executed` falls outside the window. **Expected NON-ZERO — this IS the revision signal** (5 of 98 live rows, one pair 18 days old). A zero here on a long-running deployment would suggest the re-pull has stopped surfacing revisions. `-- a NON-ZERO count is HEALTHY`. |
| 18 | `TradeNumberOverlapAcrossTables` | — | **NULL** (info) | `TradeNumber`s present in **both** `arm.AllTrades` and `arm.MyTrades`. **Expected non-zero and intended** — no FK, no cross-table dedup, two views at different disclosure levels (§1.2). `-- informational only, NEVER an error`. |
| 19 | `UnknownEnumValues` | `<Column>=<value>` | **NULL** (info) | Values of `State`, `TradeType`, `ProductType`, `UnitOfMeasure`, `Side`, `SettlementCurrency` and settlements `PriceBasis` outside the observed sets (§8). **Report a new value, NEVER fail on it** — no vendor document enumerates any of them, and a `CAD` settlement or a third product type must load. |
| 20 | `NumericHeadroom` | `<Table>.<Column>` | **NULL** (info) | `MAX(ABS(value))` for `Volume`, `Price`, both commissions vs the `DECIMAL(9,2)` ceiling `9,999,999.99`; Detail flags any column above **50 %** of ceiling. Baseline: `Volume` max **300,000 ≈ 3 %**. Ties to D8/§12 item 5 — an over-range value is a hard **overflow**, not a truncation. |
| 21 | `WidthHeadroom` | `<Table>.<Column>` | **NULL** (info) | `MAX(LEN())` vs declared width for the tight columns (trades `Product` 28/50, trades `Location` 23/50, settlements `Product` 22/50, `PieplineTerminal` 21/50). Both trades columns are `/`-joined on spread rows, so a three-leg product could approach 50 (§5.3). |
| 22 | `FileLogOutcomeSummary` | `Status=<name>` | **NULL** (info) | `arm.FileLog` row counts per status over the window — lets an operator read `Success = 3, NotAvailable = 0, Failed = 0` at a glance. |
| 23 | `FileLogHttpStatusSummary` | `HttpStatus=<n>` | **NULL** (info) | Counts per HTTP status, **plus a sample of `ErrorMessage`** for any non-2xx. Because `FileLog` is the only provenance (§6), this is how an operator distinguishes the four `400`s after the fact. **`ErrorMessage` never contains a credential** (§4.1). |
| 24 | `FileLogFailuresInWindow` | — | **0** | `arm.FileLog` rows with `StatusId → 'Failed'` whose `LastCheckedUtc >= @ModifiedSinceUtc`. The one hub check with a real expectation: **there is no legitimate non-2xx** for this API (§5.6), so a `Failed` row in the current run always merits attention. |

**Build-only:** the validator and the proc are written and unit-testable but **not exercised against
live data** this pass; the deeper reconciliation is the deferred `DATA_QUALITY_VALIDATOR` step, which
requires a live, loaded database (D12).

---

## 10. Config surface — `ModComSettings : LoaderSettingsBase`

Inherited from `LoaderSettingsBase`: `ConnectionString`, `MaxConcurrentWorkUnits`, `RetryCount`,
`RetryDelayMs`, `WorkUnitTimeoutSeconds`. Added:

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `BaseUrl` | string | `https://app.modcom.inc/api/integration/` | API root. **The `/v1` segment is per endpoint** (`allTrades/v1`), not part of the base path. Use a `BaseUrlRoot()` helper that coalesces a JSON-`null` binding back to the default and trims a trailing `/` (the NGI pattern) — never `BaseUrl.TrimEnd('/')` at a call site. |
| `Username` | string | **`"SEE_DB"`** | Basic-auth user. Resolved from `core.Param(LoaderName='ModernCommodities', ParamName='Username')`, or env `DATALOADER_Loaders__ModernCommodities__Username`. **Never logged, never in `appsettings.json`, never in a fixture.** |
| `Password` | string | **`"SEE_DB"`** | Basic-auth password. Same resolution, same prohibitions. **The PDF's pre-encoded `Authorization` value is a credential too and must never be committed.** |
| `HttpTimeoutSeconds` | int | `120` | Per-request timeout. Higher than NGI's 60 because a chunked settlements pull can return tens of thousands of rows with **no compression negotiated** (a 30-day pull ≈ 1.5 MB; a 60-day chunk ≈ 3 MB). |
| `EnabledEndpoints` | string[] | `["AllTrades","MyTrades","Settlements"]` | Toggle, matched case-insensitively against `IModComPipeline.EndpointId`. **Any subset is a valid run** (§1.2). A JSON-`null` binding is coalesced to empty → "run nothing" (warn + success). |
| **`DaysBack`** | int | **`30`** | Global trailing-window length. `startDate = endDate − DaysBack` → an **inclusive `DaysBack + 1` calendar-day window** (§3.2 #2). **(User requirement U4.)** Clamped to ≥ 0. |
| **`ChunkDays`** | int | **`0`** | Global chunk size in **inclusive** calendar days per request. **`0` (or negative) = the whole window in a single request** — the shipped default, giving **3 requests per run** (U6). §3.4. |
| `HotKeyStrategy` | `RunHour` \| `RunDate` \| `RunId` | **`RunHour`** | Resume-key cadence (§3.3). **There is no settled-zone setting** (Rationale A). |
| `LegalEntityName` | string? | **`null`** | `myTrades` only. **Omitted by default — verified that omitting it returns trades for BOTH ARM legal entities.** When set it is URL-encoded (the valid values contain spaces and a comma) and its slug enters the resume key (§3.3). An invalid value is a `400` whose body enumerates the valid options (§5.6). |
| **`Endpoints`** | `Dictionary<string, ModComEndpointOverride>` | `{}` | **Per-endpoint overrides (D10)**, keyed by `EndpointId` (case-insensitive). Each entry: `int? DaysBack`, `int? ChunkDays`. Resolution is `Endpoints[id]?.X ?? X`. This is the documented, code-free path to a deep backfill — e.g. `MyTrades: { DaysBack: 3650 }` to exploit its all-time history, or `AllTrades: { DaysBack: 183, ChunkDays: 30 }` / `Settlements: { DaysBack: 183, ChunkDays: 60 }` for a full-history pull (§3.4). |
| `RequestsPerSecond` | double? | `1` | Global client-side throttle; `null`/≤ 0 = unlimited. The vendor publishes no numeric limit but warns the API *"is not intended to be rapidly polled"* (§4.3). |
| `ValidationClockSkewMinutes` | int | `5` | Tolerance subtracted from `context.StartedAtUtc` for `@ModifiedSinceUtc` (§6.3) — host clock vs SQL Server clock. |
| `MaxConcurrentWorkUnits` (inherited) | int | `2` | At the default there is **1 unit per endpoint**, so this only bites during a chunked backfill. Keep it modest against an unpublished rate limit. |

`appsettings.json` `Loaders:ModernCommodities` mirrors the AGSI/CWG/NGI block: `ConnectionString`
(`Server=…;Database=ModernCommodities;Integrated Security=SSPI;TrustServerCertificate=True;`),
`Username: "SEE_DB"`, `Password: "SEE_DB"`, `BaseUrl`, `HttpTimeoutSeconds`, `EnabledEndpoints`,
`DaysBack: 30`, `ChunkDays: 0`, `HotKeyStrategy: "RunHour"`, `LegalEntityName: null`,
`Endpoints: {}`, `RequestsPerSecond: 1`, `ValidationClockSkewMinutes: 5`,
`MaxConcurrentWorkUnits`, `RetryCount`, `RetryDelayMs`, `WorkUnitTimeoutSeconds`.

> **⚠ Leave `"ModernCommodities"` OUT of `Platform:EnabledLoaders`** — build-only pass, loader
> **disabled** by default (D12; the CWG/AGSI/IHSPointLogic/IIR/NGI posture).
>
> **SECURITY: no username, password or encoded `Authorization` value may appear in
> `appsettings.json`, in this document, in `sql/`, in any log line, in any exception message or in any
> test fixture.** The real values live only in `core.Param` (or the
> `DATALOADER_Loaders__ModernCommodities__*` env vars).
>
> **⚠ Committed test fixtures MUST be ANONYMISED (D9).** The live captures contain **real trader
> names, counterparty legal entities and street addresses**. Fixtures must preserve *shape* — a
> comma-laden quoted address, a `PM` timestamp, a blank commission, a negative price, a `True`/`False`
> pair, the `-` sentinel, a `Financial` row with three blanks, a 3-row spread group — with **invented**
> names and addresses. The raw captures stay in the scratchpad, **uncommitted**.

---

## 11. Concurrency, idempotency & merge semantics

### 11.1 Write serialization

- **Sinks** derive from `SqlSinkBase<TRow>`, which auto-acquires `SqlWriteGate` keyed
  `{server}/{db}::{proc}`. The three merge procs are **three distinct keys**, so the three tables'
  merges never serialise against one another; concurrent units of the *same* endpoint serialise on
  that endpoint's key (correct — it prevents the parallel-MERGE deadlock and the NOT-MATCHED insert
  race). With the pipelines run sequentially (§1.6) and 1 unit each at the defaults, there is
  effectively no contention; the gate matters during a chunked backfill.
- **FileLog** is a **direct** proc writer, so `SqlModComFileLog` acquires `SqlWriteGate` explicitly on
  `arm.usp_UpsertFileLog` — **one shared key across all three endpoints, distinct from the three merge
  keys**, so the hub upsert can neither deadlock against a fact merge nor against another endpoint's
  hub write. It is a fast single-row upsert, so serialising it is cheap.
- `core.LoadLog` bookkeeping is intentionally **not** gated (keyed per work unit, so it rarely
  contends).
- **Overlap guard:** the platform's `DataLoader:ModernCommodities` app lock prevents two host processes
  running the loader at once. Safe to over-schedule — a blocked invocation logs a warning and exits
  `0`. This is what makes the hourly cadence (U5) safe even if a run overruns an hour.

### 11.2 Merge, never blindly insert — trades (`WHEN MATCHED` guarded by recency, D4)

`arm.usp_BulkMergeAllTrades` / `arm.usp_BulkMergeMyTrades` (`@Records arm.TradesTvp READONLY`):

1. **Dedup the batch, last wins, ordered by `LastUpdated DESC`** —
   `ROW_NUMBER() OVER (PARTITION BY TradeNumber ORDER BY LastUpdated DESC)`, keep `rn = 1`.
   > **⚠ Order by `LastUpdated DESC`, NOT by arbitrary batch order.** T-SQL sorts `NULL` lowest, so
   > `DESC` places a NULL-timestamped duplicate **last** — a dated copy always beats an undated one,
   > which is exactly the desired precedence. Using `ORDER BY (SELECT NULL)` here would make the
   > winner arbitrary and could keep a **stale** copy of a revised trade. The C# sink de-dups with the
   > same rule (§8.3) so the two can never disagree.
2. `MERGE … ON tgt.TradeNumber = src.TradeNumber`.
3. **`WHEN MATCHED AND (src.LastUpdated IS NULL OR tgt.LastUpdated IS NULL OR src.LastUpdated >= tgt.LastUpdated)`**
   → `UPDATE` all 33 non-key columns + `ModifiedAtUtc = SYSUTCDATETIME()`.
4. `WHEN NOT MATCHED BY TARGET` → `INSERT` all 34 columns + `ModifiedAtUtc = SYSUTCDATETIME()`.
5. **No `WHEN NOT MATCHED BY SOURCE` branch — the merge is upsert-only and NEVER deletes.** A
   truncated or empty response must never be able to wipe a table.
6. `SELECT` the affected row count so `ProcedureReturnsRowCount = true` surfaces it to `core.LoadLog`.
7. **No checksum short-circuit** — the `Checksum` column is removed from all three tables (U2), so the
   `WHEN MATCHED` branch is a straight update.

**Why the recency guard is a real requirement, not defensive decoration.** It is the OPIS
`src.SourceFileDate >= tgt.SourceFileDate` pattern (`sql/OPIS/003_CreateOpisProcedures.sql`), and here
is the concrete race it closes: chunked windows partition by **last-updated**, so a trade normally
appears in exactly one chunk — **but a trade revised *between* two chunk requests appears in both**.
Chunk A `[Jul 1 .. Jul 30]` is requested at 10:00:00 and returns trade `67183` with
`LastUpdated = Jul 29`; the venue revises it at 10:00:30; chunk B `[Jul 31 .. Aug 24]` is requested at
10:01:00 and returns the **same** `TradeNumber` with `LastUpdated = Aug 24`. The two chunks are
independent work units that may merge in **either order**. Without the guard, whichever lands last
wins and the loader can persist the **pre-revision** copy — silently, with a clean run. With it, the
outcome is **independent of arrival order and of re-run count**, which is the whole point.

> **⚠ Flagged, and implemented as decided (D4).** The guard as specified includes
> `src.LastUpdated IS NULL`, which lets a row whose timestamp we could **not parse** overwrite a row
> whose timestamp we know. Strictly, "unknown recency" should not beat "known newer". The practical
> impact is nil — `Last Updated Timestamp` was **never blank in 135/135** observed rows, so
> `src.LastUpdated IS NULL` means a parse failure, which is itself logged (§5.3). The decision is
> settled and is implemented verbatim; §12.1 records the tighter alternative for a reviewer who wants
> to close it.

### 11.3 Merge — settlements (plain last-wins on the 6-column PK)

`arm.usp_BulkMergeSettlements` (`@Records arm.SettlementsTvp READONLY`):

1. **Dedup the batch, last wins** —
   `ROW_NUMBER() OVER (PARTITION BY SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term ORDER BY (SELECT NULL))`.
   **There is no recency column to order by** — the payload carries no revision, version, status or
   as-of field of any kind. That is acceptable *here* and not in §11.2 because a duplicate 6-column key
   **within one request** would mean the vendor published two prices for the same
   (date, product, location, pipeline, basis, term) — which never occurred in 1,443 rows and is a
   vendor defect, reported by §9 check 4, not something to arbitrate. The C# sink de-dups
   last-in-file-order first, so the proc's tie-break is pure insurance. The SQL comment must say why
   there is no `ORDER BY` column.
2. `MERGE … ON` all six key columns. **`WHEN MATCHED` → plain unconditional `UPDATE`** of `TermStart`,
   `TermEnd`, `Price` + `ModifiedAtUtc`.
3. `WHEN NOT MATCHED BY TARGET` → `INSERT` all 9 + `ModifiedAtUtc`. **No delete branch.**
4. `SELECT` the affected row count.

**Cross-chunk collisions are impossible for settlements** — chunks partition by `SettlementDate`, which
is PK column 1, so two concurrent settlements units always write disjoint key sets. (This is precisely
why settlements needs no recency guard while trades does: the trades window predicate is *not* a key
column.)

> **⚠ A settlement price revision therefore OVERWRITES, with no history kept.** `Price` sits outside
> the PK. This is a settled decision and correct **if** settlements are never restated — **unverified**
> (§12 item 2). If it proves false, the pattern to reach for is OPIS's `arm.LPReportHistory`: add the
> distinguishing value to the key so both prints survive. **Not built now**, and the note belongs in
> the proc's header comment so whoever revisits it finds the precedent.

### 11.4 Idempotency & resume

- **`core.LoadLog` skips a unit only when its `Key` is recorded SUCCESSFULLY COMPLETED.** A
  started-but-failed unit is retried on the next run — including a unit that failed on a `400`/`401`,
  which will keep failing until the underlying bug or credential is fixed (correct: the failure stays
  visible).
- **A second run inside the same clock hour is a full no-op**: every key matches, every unit skips, no
  HTTP request is made (§3.3). A run in a new hour re-pulls everything and upserts in place.
- **A header-only `200` succeeds** and — because the key is hot-only — is **re-probed next hour**
  (§3.5). No settled zone means no frozen-empty hazard.
- **The MERGE is the second idempotency layer.** Even if `core.LoadLog` were wiped, re-running loads
  the same natural keys and upserts them in place: no duplicates, and (for trades) no regression,
  thanks to the recency guard.
- **Fail a block without failing the run:** a unit that throws is recorded as a `core.LoadLog` failure
  plus an `arm.FileLog` `Failed` row, and the pipeline continues to the next unit; a pipeline that
  throws is recorded and the module continues to the next pipeline (§1.2, §1.6 step 5).

---

## 12. Open items — the first-live-run checklist

**Behavioural/operational unknowns only.** The field set needs **no live confirmation**:
`docs/apis/ModernCommodities.md` is zero-reconstruction — every one of the 43 source columns, every
type, every observed maximum, every blank/sentinel behaviour, both row caps, the history limit and all
five error bodies were observed live on 2026-08-24. There is **no "needs one-shot live verification"
field checklist** of the kind IIR carries.

| # | Open item | Current design decision | How to close it |
|:-:|---|---|---|
| **1** | **⚠ The `allTrades` daily row rate is not stable and the two measurements do not reconcile.** The 170-day pull implies ≈57.9 rows/calendar day; the 5-day pull implies ≈19.6 — a ~3× gap that does not close even allowing for the weekend and the partial final day in the short window. Possible causes: seasonality, a burst of revisions inside the long window, or the long window capturing older trades re-touched later. | **Size chunks against the 170-day figure only** (§3.4), and keep `ChunkDays = 30` for a deep backfill rather than the arithmetically-sufficient 90. §1.6 step 3 pre-warns at 60 % of cap. | **Re-measure before any deep backfill**, and watch §9 check 1 over a few weeks. A `DATA_QUALITY_VALIDATOR` pass on the first live load. |
| **2** | **Are settlement prices ever REVISED?** ⚠ **The highest-value question here.** Each settlement date was captured once. The payload carries **no** revision/version/status/as-of field, so a restatement is indistinguishable from the original except by comparing values. | **Plain last-wins overwrite** on the 6-column PK; **no history table** (§8.2, §11.3). | **Ask the vendor.** Meanwhile: re-open the moment §9 check 2/15 or a `DATA_QUALITY_VALIDATOR` pass sees a `Price` change on an already-loaded settled date. The remedy is known — the OPIS `arm.LPReportHistory` pattern (put the distinguishing value in the key). |
| **3** | **⚠ The timestamp timezone is not stated anywhere** — not in the PDF, not in the payload, not in a response header. The vendor is in **Calgary (Mountain)**, but the response-`Date`-vs-max-`LastUpdated` arithmetic in §7 makes **UTC−5/−6 (Central-ish)** far more likely than Mountain. Either way it is **unverified**, and it matters for any cross-source time join and for the strictness of §9 check 16. | **Store exactly as given in `DATETIME2(0)`; never shift, never normalise** (§5.3, §7). Check 16 is informational precisely because of this. | **Ask the vendor** (quote the §7 arithmetic — it narrows the question to "Central or Eastern?"). |
| **4** | **Only the `Invalid startDate` form of the malformed-date `400` was provoked.** An invalid `endDate` presumably yields `Invalid endDate`, and an invalid *format* vs an invalid *date* (e.g. `2026-02-30`) may or may not differ. | **Match the substring `Invalid `, not the full sentence** (§5.6). | Nothing required — the classifier is already substring-based. |
| **5** | **⚠ `Volume DECIMAL(9,2)` headroom is ~33×, not ~1,000×.** Observed max is **300,000** (`TradeNumber 68020`, a `bbls/month` row) against a `9,999,999.99` ceiling. An over-range value is a hard **arithmetic-overflow error** that fails the whole batch, not a truncation — and a monthly `bbls/month` cargo deal an order of magnitude larger than today's biggest would breach it. `Volume` has also **never** carried a decimal point, so scale 2 is pure headroom. | **Keep `DECIMAL(9,2)` as the user specified (D8)**, with the `Dec92` range guard degrading a breach to NULL + an error-level warning so one bad row cannot fail a batch (§5.3), and §9 check 20 reporting headroom continuously. | **Flag to the user: `DECIMAL(13,2)` removes the risk entirely at negligible cost.** Their call. |
| **6** | **`myTrades`' all-time history depth was probed only to 180 days.** The PDF says "all time"; the oldest row observed is `2026-03-02`. How far back it actually answers, and whether older rows carry the same 34 columns, is unknown. | Not relied upon — `DaysBack = 30` by default; the per-endpoint override (D10) is the documented path if someone wants more. | Probe a few old `startDate`s before planning a `myTrades` backfill. |
| **7** | **Several enum paths are untested end-to-end.** `Apportionment Protected = True` and `Click & Trade = True` were **never observed** (0 of 135 rows), nor a `Settlement Currency` other than `USD`, nor a `Product Type` outside `Physical`/`Financial`. | `BIT`/`VARCHAR` with **no `CHECK` and no C# enum** — an unseen value must load (§0.1). §9 check 19 reports new values. | Watch check 19 on the first live loads. |
| **8** | **No `Financial` rows appeared in `myTrades`** (37/37 `Physical`), so the "`Financial` ⇒ blank `Location`/`PipelineTerminal`/`PriceBasis`" invariant is verified on `allTrades` only. | §9 check 12 is **informational** (not `Expected = 0`) and reports both directions on both tables. | Confirm once `myTrades` carries a `Financial` row. |
| **9** | **The `Contract Terms` semantic is undocumented.** It usually names the counterparty, but 2 of 37 rows name ARM itself — so it is *"whose paper governs"*, not *"the other party"*. | Persisted as published; **not used as a counterparty identifier anywhere**, and no derived column. | One question to the vendor so the column comment is right. |
| **10** | **`ClearingID` has never been populated by either endpoint**, so its format, width and semantics are **unknown** — `VARCHAR(50)` is the user's allocation, not an observed maximum. | Kept on both tables, treated as **expected-NULL everywhere**, and **never asserted populated** (§9 check 11). | Re-check the width/format the first time a non-NULL value appears (check 11 will surface it). |
| **11** | **Rate limits are unpublished and unprobed.** No numeric limit, no `Retry-After`, no `X-RateLimit-*` header, no `429` ever observed — but the PDF explicitly warns the API is *"not intended to be rapidly polled"*. | `RequestsPerSecond = 1`, `MaxConcurrentWorkUnits = 2`, Polly retries `429` and honours `Retry-After` (§4.3). | Watch for `429` on the first hourly days; §9 check 23 tallies HTTP statuses. |
| **12** | **The row-cap / history-cap masking order was observed in one direction only** (a request violating both reported the **row cap**). A formal precedence rule is not established. | **Log the body; never infer one limit from the other's failure** (§5.6 rule 2). | Nothing required — the classifier reads the body. |
| **13** | **No conditional-GET support** (no `ETag`, `Last-Modified` or `Cache-Control` on any response), so every hourly run re-downloads its full window. | Accepted — ~1.7 MB/hour at the defaults. | Nothing available to change; it bounds how cheaply the window can be widened. |

### 12.1 Decisions to confirm (unspecified by the brief, or settled in a way worth a second look)

| Decision | Chosen here | Where |
|---|---|---|
| **`DaysBack = 30` yields a 31-day inclusive window** (`start = end − 30`, both ends inclusive) | **Implemented literally per D5.** The extra day is free overlap guaranteeing no gap between consecutive daily windows. Flagged because a reader may expect exactly 30 days. | §3.2 #2 |
| **The trades `WHEN MATCHED` guard admits `src.LastUpdated IS NULL`** | **Implemented verbatim per D4.** Flagged: "unknown recency" beating "known newer" is the one branch that can still regress a revision. Zero practical impact (never blank in 135/135). The tighter alternative is `(src.LastUpdated IS NOT NULL AND (tgt.LastUpdated IS NULL OR src.LastUpdated >= tgt.LastUpdated)) OR (src.LastUpdated IS NULL AND tgt.LastUpdated IS NULL)`. | §11.2 |
| **`ModifiedAtUtc DEFAULT` changed from the user's `SYSDATETIME()` to `SYSUTCDATETIME()`** | Per D3. The column is named `…Utc` but `SYSDATETIME()` is server-**local**; the procs stamp it explicitly on every write so the default is effectively unreachable — this only stops the two paths disagreeing in basis. **Flag to the user.** | §8, §13 item 2 |
| **`PieplineTerminal` misspelling reproduced in the C# property name too** | Per D2 — deliberate, with a code comment, so nobody "tidies" it and breaks the positional TVP binding. **Flag to the user** that the misspelling is in their PK. | §8.2 #4 |
| One shared `arm.TradesTvp` for both trades tables (vs two identically-shaped per-table types) | **Shared** — 34 columns duplicated is exactly the drift the positional contract cannot survive. Alternative recorded. | §8.3, §13 item 3 |
| Endpoint ids / `EnabledEndpoints` values | `"AllTrades"`, `"MyTrades"`, `"Settlements"` (also the `arm.Endpoint` seed names) | §1.1, §6.2 |
| Pipeline execution order | `AllTrades` → `MyTrades` → `Settlements`, sequential, **cosmetic only** — any order or full concurrency is equally correct | §1.2 |
| `ChunkDays = 0` default (whole window, 1 request/endpoint) | 3 requests/run at the defaults; per-endpoint recommendations for a deep backfill are **30** (trades) and **60** (settlements) | §3.4, §10 |
| `HotKeyStrategy = RunHour` default, UTC token | Matches the hourly schedule (U5); UTC because `yyyyMMddHH` must be monotonic | §3.3, §7 |
| `LegalEntityName` omitted by default, and its slug in the resume key when set | Omitting it returns **both** ARM entities (verified); the slug prevents a scope change from idempotently skipping | §3.3, §10 |
| `HttpTimeoutSeconds = 120`, `RequestsPerSecond = 1`, `MaxConcurrentWorkUnits = 2` | Sized for an uncompressed multi-MB settlements chunk against an unpublished rate limit | §10 |
| Over-width **key** value → drop-and-count; over-width **non-key** → truncate + warn | A truncated key silently MERGEs onto a different logical entity, which is worse than a missing row | §5.3 |
| Over-range numeric → field degrades to NULL + error-level warning (row survives) | One bad `Volume` must not fail a whole batch with an arithmetic overflow | §5.3, §12 item 5 |
| Header drift: key column missing → throw; non-key missing/extra → warn + continue | A vendor rename must not drop the other 33 columns, but an unkeyable pull is a hard failure | §5.2 |
| `arm.FileLog.[RowCount]` = rows **parsed**, not rows **merged** | The hub is written by the reader, before the sink runs | §6.2 |
| `@ModifiedSinceUtc` skew tolerance of 5 minutes | Host clock vs SQL Server clock | §6.3, §10 |

---

## 13. Open items / coordination for DATABASE_DEVELOPER & reviewer

Three fact tables + one `FileLog` hub + two seeded lookups + supporting objects, in
`sql/ModernCommodities/001…003` plus a guarded, idempotent `999_DropModernCommoditiesObjects.sql`
teardown that drops every object in **reverse dependency order** (see `sql/AGSI/`). **DB
`ModernCommodities`, schema `arm`.** Coordinate on:

1. **The three fact tables come from the user's authoritative script (`MODCOM_DDL.sql`) — reproduce it
   character-for-character**, with exactly the two stated deviations (`Checksum` removed, U2;
   `ModifiedAtUtc DEFAULT SYSUTCDATETIME()`, D3). Specifically: **no surrogate `Id`, no
   `DateCreated`, no `FileLogId`** on any of the three; PK `TradeNumber` CLUSTERED on both trades
   tables; PK `(SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term)` CLUSTERED on
   settlements. The missing `Id` is conventional for a fact; **the missing `DateCreated` is a
   deviation from the house shape — follow the user's DDL rather than adding one.**
2. **⚠ Two flags for the user, both deliberate and both stated in the script header:** (a) the
   `ModifiedAtUtc` default basis change (D3 — the column is named `…Utc` but `SYSDATETIME()` is server
   local; the procs stamp it explicitly so the default is effectively unreachable); (b)
   **`PieplineTerminal` is MISSPELLED in `arm.Settlements` and the misspelling is part of the PRIMARY
   KEY** — reproduced exactly, in the table, the TVP, the proc **and** the C# property name (D2).
   The trades tables spell the same concept correctly, so both spellings coexist on purpose.
   **Flagged, not fixed** — it is the user's schema and may have downstream consumers.
3. **TVPs (§8.3) — the positional contract.** `arm.TradesTvp` (34 cols, starting at `TradeNumber`) and
   `arm.SettlementsTvp` (9 cols, starting at `SettlementDate`). **⚠ Neither begins with `FileLogId`**
   — a deliberate break from the house rule because the column does not exist (§6.1). Confirm the
   **single shared `arm.TradesTvp`** for both trades procs (recommended — it makes drift between two
   34-column definitions impossible) vs two identically-shaped `arm.AllTradesTvp`/`arm.MyTradesTvp`.
   **TVP parameter name is `@Records`** on all three procs (what `SqlSinkBase` passes).
4. **Merge procs** `arm.usp_BulkMergeAllTrades`, `arm.usp_BulkMergeMyTrades`,
   `arm.usp_BulkMergeSettlements` — §11.2/§11.3. **Load-bearing SQL comments:** (a) on both trades
   procs, why `ORDER BY LastUpdated DESC` in the dedup and why the `WHEN MATCHED` recency guard exists
   (the between-chunks revision race, with the OPIS `SourceFileDate` precedent named); (b) on
   settlements, **why there is no `ORDER BY` column in the dedup** and **that a price revision
   overwrites with no history, with the `arm.LPReportHistory` precedent named** if that changes;
   (c) on all three, `-- upsert-only: NEVER add a DELETE / WHEN NOT MATCHED BY SOURCE branch`;
   (d) `-- no checksum short-circuit: the Checksum column was removed by explicit user decision`.
   All three `SELECT` the affected row count (`ProcedureReturnsRowCount = true`) and stamp
   `ModifiedAtUtc = SYSUTCDATETIME()` on insert **and** on match.
5. **No `CHECK (Price >= 0)`, `CHECK (Volume >= 0)` or any non-negative constraint anywhere** — 74 % of
   settlement prices and 21 % of `allTrades` prices are negative (they are differentials). Put that
   sentence in the table comment so nobody adds one later. Likewise **no `CHECK`/lookup on `State`,
   `TradeType`, `ProductType`, `UnitOfMeasure`, `Side`, `SettlementCurrency` or `PriceBasis`** — none
   is enumerated by any vendor document; a new value must load and be reported (§9 check 19).
6. **Type calls (all live-observed, from the API doc §13.3).** `DECIMAL(9,2)` **not `FLOAT`** for
   `Price`, `Volume`, `BidCommission`, `OfferCommission` (a traded price must round-trip exactly;
   observed scale is exactly 2 in 100 % of rows across all three captures). `DATETIME2(0)` for
   `Executed`/`LastUpdated` (seconds precision only, no fractional seconds ever observed, **no
   timezone supplied — store as given**). `DATE` for `TermStart`/`TermEnd`/`SettlementDate` (no time
   component exists; note `TermEnd` reaches **`2031-12-31`**, ~5.4 years forward, so no tighter range
   assumption). **`VARCHAR` not `NVARCHAR` throughout** (every observed character in all three
   captures is ASCII — revisit only if non-US/CA counterparties with accented legal names are
   onboarded). `ModifiedAtUtc DATETIME2(3)`. `SpreadTradeNumber` stays `VARCHAR(50)` — **do not "fix"
   it to `INT`**.
7. **Nullability.** On the trades tables **only `TradeNumber` is `NOT NULL`**; everything else is
   NULLable, which is correct given the 14 always-NULL anonymised columns in `arm.AllTrades` plus the
   3 that are NULL on `Financial` rows. On `arm.Settlements` the **six PK columns are `NOT NULL`**
   (never blank in 1,443 rows — safe) and `TermStart`/`TermEnd`/`Price` stay NULLable per the DDL even
   though they were never blank, so the tolerant-parse contract is expressible end-to-end.
8. **`arm.FileLog` hub + `arm.usp_UpsertFileLog`** — §6.2. Natural key
   **`UNIQUE (EndpointId, WindowStart, WindowEnd, RunToken)`** (all four `NOT NULL`, so a plain UNIQUE
   constraint suffices — **no NULL-equality trick is needed here**, unlike NGI's undated Locations
   row). Confirm: (a) normalise `Endpoint`/`Status` to seeded lookups (`arm.Endpoint`
   `{AllTrades, MyTrades, Settlements}` with their URL templates; `arm.Status`
   `{Success, NotAvailable, Failed}`) with the proc keeping its **name-string** signature and resolving
   server-side, raising on an unknown name — recommended, the CWG/AGSI/NGI posture; (b) the
   ModCom-specific columns `DroppedRowCount`, `ScopeLabel` and `ErrorMessage`, and the **column comment
   that `[RowCount]` is rows PARSED, not rows merged**; (c) keeping `LastCheckedUtc` (recommended — the
   validator's freshness filter uses it). **⚠ Put the "this is the ONLY provenance — there is no
   `FileLogId` on any fact table" note in the `001` header**, because that is the fact a future reader
   most needs and cannot infer from the DDL.
9. **No `usp_Get…` read proc.** Deliberately absent — nothing reads any ModCom table back at run time
   (§2, §0.1). **Do not add one "for parity with AGSI".** Likewise **no FK between the three fact
   tables** and **no FK from a fact to `arm.FileLog`**.
10. **`arm.usp_ValidateLoad(@DateFrom DATE, @DateTo DATE, @ModifiedSinceUtc DATETIME2(3) = NULL)`** —
    the 24 checks in §9.1, **ONE** result set, uniform `CheckName, Scope, ExpectedCount, ActualCount,
    Detail`, `ORDER BY CheckName, Scope`. **Load-bearing SQL comments:** (a)
    `-- NEVER normalise the '-' sentinel to NULL: Location/PieplineTerminal are PRIMARY KEY values`
    on check 6; (b) `-- do NOT add a non-negative price check: negative prices are differentials and
    are 74% of settlements` wherever prices are touched; (c) `-- do NOT set ExpectedCount = 0 here` on
    checks 2, 10, 12 and **16** (each with its one-line reason: the recency guard skips updates; the
    commissions are complementary; `Financial` is unverified on `myTrades`; the payload clock is
    unverified); (d) `-- informational only, NEVER an error` on checks 9, 11, 13, 14, 18; (e)
    `-- a NON-ZERO count is HEALTHY here` on check 17; (f) on check 11,
    `-- ClearingID has never been populated by EITHER endpoint; do not assert it populated anywhere`.
    Checks that **do** carry an expectation: 3 (`UnkeyableDrops` = 0), 4 (`PkDuplicates` = 0), 5
    (`TermOrdering` = 0), 7 (`SettlementSentinelPairing` = 0), 24 (`FileLogFailuresInWindow` = 0).
11. **Indexes.** Each PK covers its merge. Recommended additions: on both trades tables a
    nonclustered index on **`LastUpdated`** (every window-scoped validation check and any operational
    query filters on it) and one on **`SpreadTradeNumber`** (check 13's self-join); on
    `arm.Settlements` a nonclustered index on **`SettlementDate`** is unnecessary (PK column 1) but one
    on **`ModifiedAtUtc`** helps check 2 across all three tables. On `arm.FileLog`, an index on
    `(WindowEnd, EndpointId)` and one on `LastCheckedUtc` support §9 checks 22–24. DATABASE_DEVELOPER's
    call on which to ship.
12. **`core.Param` rows required before any run:**
    `(LoaderName='ModernCommodities', ParamName='Username')` and `(…, ParamName='Password')`. The
    `SeeDbSettingsResolver` **throws** on a missing row, so both must exist even for a build-only smoke
    test (a placeholder value is enough to get past resolution; the §1.6 step 2 guard then rejects the
    literal `SEE_DB`). **No secret value in `sql/`, in `appsettings.json`, or in this document.**

---

## Coverage checklist

| # | Endpoint | Pipeline (`EndpointId`) | Work unit / literal resume key | Reader tolerance | Target table (merge key) | FileLog identity |
|:-:|---|---|---|---|---|---|
| 1 | `GET allTrades/v1?startDate=&endDate=` (34 cols, cap 10,000, 6-month history) | `AllTrades` (runs 1st — cosmetic) | one unit per **window chunk**; `modcom:AllTrades:{yyyyMMdd}-{yyyyMMdd}:run={yyyyMMddHH UTC}` — **hot-only, NO settled zone** | header-only `200` → `NotAvailable`, **SUCCESS**, 0 rows; every non-2xx → **THROW** (row-cap `400` classified distinctly, naming `ChunkDays`); RFC-4180 tokenizer; bind by literal header name; `hh:mm:ss tt` timestamps; blank `BIT` → NULL; unkeyable/over-width-key row dropped-and-counted; over-range numeric → NULL + warn | `arm.AllTrades` (`TradeNumber`) — 14 columns always NULL by design | `AllTrades` / `(WindowStart, WindowEnd, RunToken)` |
| 2 | `GET myTrades/v1?startDate=&endDate=[&legalEntityName=]` (**byte-identical 34-col header**, cap 10,000, **all-time history**) | `MyTrades` (2nd) | same shape; **no history clamp**; `…:entity={slug}` inserted when `LegalEntityName` is set | identical to #1 — **one reader class, one row type, one `BuildTable`, one TVP** | `arm.MyTrades` (`TradeNumber`) — fully attributed; **no FK to `arm.AllTrades`** | `MyTrades` / same, + `ScopeLabel` |
| 3 | `GET settlements/v1?startDate=&endDate=` (9 cols, cap 100,000, 6-month history) | `Settlements` (3rd) | same shape; window predicate is `Settlement Date` itself | as #1, plus: **literal `-` persisted VERBATIM in two PK columns**; weekend/unpublished-day gaps are a normal empty; `Term` is always a single `MMM-YY` month | `arm.Settlements` (`SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term`) — plain last-wins | `Settlements` / same |

All three endpoints have a complete persisted field set (**34 / 34 / 9 = 43 source columns → 77 target
columns**), a per-field SQL type + nullability, a natural key, a literal resume-key rule, a `FileLog`
identity, and an explicit tolerance policy for every status the API can return.

Ready for **DATABASE_DEVELOPER** (3 fact tables + 2 TVP types + 3 merge procs + `arm.FileLog` /
`arm.usp_UpsertFileLog` + 2 seeded lookups + `arm.usp_ValidateLoad` + a guarded `999` teardown — and
**no `usp_Get…`, no FK anywhere, no `FileLogId` on any fact table, no `CHECK` on any price or enum
column**) and for **CODER** (module + 3 closed independent pipelines over one descriptor record + one
provider class + 2 readers + `ModComCsv`/`ModComHeaderMap`/`ModComParse`/`ModComTime` + 2 row types +
3 sinks + FileLog writer + Basic-auth handler + throttle/policy + validator; loader left **disabled**
in `Platform:EnabledLoaders`).

