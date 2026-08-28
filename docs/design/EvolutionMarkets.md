# EvolutionMarkets loader — design of record

**Loader id:** `EvolutionMarkets`
**Database:** `EvolutionMarkets` · **Schema:** `arm` · **Table:** `arm.MarketData`
**Endpoint:** `GET /v1/market-data/history` (one endpoint, one pipeline)
**Field reference:** [`docs/apis/EvolutionMarkets.md`](../apis/EvolutionMarkets.md) — API facts live
there and are not restated here.
**Status:** **build-only.** Builds, 203 tests green, SQL parse-checked. **Not** in
`Platform:EnabledLoaders`; the SQL has never been deployed and no data has been loaded.

---

## 0. Summary and the four decisions that shape everything

| # | Decision | Because |
|---|---|---|
| D1 | **One work unit per business date** (`dateFrom == dateTo`), paged | Keeps every request ~205 rows against a **silent** 10,000-row cap; gives per-date audit and resumability; makes the reversed-range `400` unreachable (§3.1) |
| D2 | **A pinned 23-name `field` projection**, never trimmed | The vendor returns `change` correctly *only* in the full list, and omitting `field` drops 4 needed columns (§5.6) |
| D3 | **`Checksum` computed in C#** (FNV-1a/32), driving a MERGE short-circuit | Makes `ModifiedAtUtc` mean *"when this price last changed"* instead of *"when the loader last ran"* — the only way to see a revision (§8.4) |
| D4 | **All-hot 30-day window** (`DaysBack = SettledAfterDays = 30`) | The feed has no revision field, so a revision only lands on a re-pull; and an empty `200` completes a unit as SUCCESS, so a settled date that had not published yet would be lost (§3.5) |

### User decisions taken on the record

| Ref | Question | Decision |
|---|---|---|
| U1 | The API has no `Checksum` field — what populates it? | **C#-computed row hash**, MERGE short-circuits on it |
| U2 | Nine of 23 payload columns can never be filled — keep them? | **Keep all nine** (vendor contract; future entitlement needs no migration) |
| U3 | The supplied DDL named its default constraint `DF_AllTrades_ModifiedAtUtc` | **Rename** to `DF_MarketData_ModifiedAtUtc` |

### Deviations from the supplied DDL — all additive, all recorded

The 23 payload columns, their names, types and order are **exactly** as supplied. Changes:

- **+** `DateCreated DATETIME NOT NULL DEFAULT GETDATE()` (leading) — repo convention.
- **+** `FileLogId INT NULL` FK → `arm.FileLog(Id)` — repo convention; without it the fact cannot be
  traced to the request that produced it and the audit hub is unjoinable.
- **+** three nonclustered indexes (`BusinessDate`, `(InstrumentId, BusinessDate)`, `FileLogId`) —
  the clustered random-GUID PK is useless for analytical access.
- **~** `DF_AllTrades_ModifiedAtUtc` → `DF_MarketData_ModifiedAtUtc` (U3).
- **=** `PRIMARY KEY CLUSTERED (MarketDataId)` **retained as supplied.** The usual objection
  (page splits on a random GUID) is immaterial at ~205 rows/day into a ~75k-row/year table.

---

## 1. Component layout

```
EvolutionMarketsModule          ILoaderModule; DI wiring + pre-flight guards
  └─ EvoPipeline<TUnit,TRow>    IEvoPipeline; thin wrapper over LoaderPipelineBase
       ├─ EvoMarketDataWorkUnitProvider   date window -> units + resume keys
       ├─ EvoMarketDataSourceReader       HTTP + pager + status matrix + FileLog + checksum
       ├─ IdentityTransformer             (the reader already emits the sink's row type)
       └─ MarketDataSqlSink               TVP bulk merge
EvolutionMarketsLoadValidator    module-level post-load report (observational)
```

### 1.1 The generics are never resolved from DI
`EvoPipeline<,>`, the provider and the reader are `new`ed inside
`EvolutionMarketsModule.BuildMarketDataPipeline`, not registered as open generics. Two loaders
registering `IWorkUnitProvider<T>` would otherwise collide in the shared container. Only closed,
loader-specific types (`IEvoPipeline`, `IEvoFileLog`, the handlers, the limiter) are registered.

### 1.2 One pipeline, but the `IEvoPipeline` seam is kept
There is exactly one endpoint today. The marker interface and the `EnabledEndpoints` toggle exist
anyway so adding `GET /v1/market-data` later is a registration rather than a refactor, and so the
loader behaves identically to every other one in the repo.

### 1.3 It uses `LoaderPipelineBase` directly
Unit count is trivial (30 at the default), so the standard per-unit loop is reused unchanged:
`BeginAsync` idempotency skip → `ReadAsync` → identity transform → `WriteAsync` →
`CompleteSuccess`/`CompleteFailure`, bounded by `ParallelRunner`, with a per-unit timeout and
**fail-a-unit-not-the-run** semantics. StormVista's windowed orchestrator is not needed.

### 1.4 HTTP handler order
```
Polly retry (OUTER — 5xx/408/network/429 only)
  └─ EvoApiKeyAuthHandler      re-stamps Authorization on every attempt
       └─ EvoRateLimitingHandler (INNERMOST) paces every attempt including each retry
```
`RemoveAllLoggers()` so the `Authorization` header can never reach a log sink. There is **no token
client and no refresh path** — the key is static, so a `401` is terminal (contrast NGI/IIR/IHS).

### 1.5 Run sequence
1. Warn if the US-Central time zone did not resolve (window would silently shift to UTC).
2. Warn if `SettledAfterDays < DaysBack` (§3.5), if `DaysBack > 60` (vendor retention), or if
   `PageSize` is outside `1..10000`.
3. **Credential guard** — fail the run if `ApiKey` is blank or still the `SEE_DB` sentinel. Never log
   the value.
4. Resolve enabled pipelines; an empty/unknown list is a warning + success, never a throw.
5. Execute the pipeline.
6. `EvolutionMarketsLoadValidator.ValidateAsync` — observational, never throws.

---

## 2. Work-unit derivation

Units come from the **date window alone**. The provider touches no database table, holds no reference
cache and has no fail-fast-if-empty guard: the endpoint is parameterised by date only, so there is no
discovery tier and nothing to barrier against (contrast AGSI, whose storage units are a country list
read back out of a dimension its first pipeline populated).

---

## 3. Windowing and resumability

### 3.1 Why per-date units (D1)
The endpoint accepts a date *range* and would return the whole 30-day window in one call — fewer
requests. Per-date was chosen for four reasons:

1. **The 10,000-row cap is silent.** No envelope, no total, no next-page link, so a capped read is
   indistinguishable from a complete one. One date is ~205 rows: the cap becomes *unreachable* rather
   than merely *handled*.
2. **Per-date audit.** "Did 2026-08-14 publish?" is one `SELECT` against `arm.FileLog`.
3. **Per-date resumability.** A failure re-runs one date, not the window.
4. It makes the reversed-range `400` structurally unreachable (`dateFrom == dateTo` always).

### 3.2 The window
`EvoTime.ResolveWindow(startedAtUtc, DaysBack)` → `[runDate − (DaysBack − 1) … runDate]` on the
**US-Central** calendar, `DaysBack` clamped to ≥ 1. **Single source of truth** — the provider and the
validator both call it, so the validation window cannot drift from the load window.

**It ends at TODAY inclusive.** Today is requestable (it returns `200 []`), so including it costs one
cheap request and guarantees an earlier-than-usual publication is never missed. A *future* date is a
hard `400`, which the provider clamps against as defence in depth.

**US-Central is also the safe choice against that `400`:** Central is always *behind* UTC, so a
Central date can never be ahead of the server's date. A timezone ahead of UTC would make the window's
last day intermittently fail around midnight.

### 3.3 The two-zone resume key
```
settled (age >  SettledAfterDays):  evolutionmarkets:marketdata:20260824
hot     (age <= SettledAfterDays):  evolutionmarkets:marketdata:20260824:run=20260825
```
A settled key is stable ⇒ loaded once, then a cheap `core.LoadLog` skip forever. A hot key varies per
run ⇒ re-pulled and upserted idempotently through the PK MERGE. `HotZoneKeyStrategy`: `RunDate`
(default — one re-pull per Central calendar day; a second same-day run skips) or `RunId` (every
invocation).

`RunHour` is deliberately **not offered**: this is an end-of-day dataset (every `priceTs` is midnight
`Z`). If it is ever added, the hour token **must be UTC** — `01:00` Central occurs twice on a
fall-back night, so a Central `yyyyMMddHH` token would repeat and the key would go backwards. At
*date* granularity the Central date is monotonic non-decreasing across both DST transitions, which is
why `RunDate` is safe on the Central clock.

### 3.4 Every calendar day is enumerated
No weekday filter, no holiday calendar. **The live data rules out every derivable calendar** — see
[api §4.8](../apis/EvolutionMarkets.md#48-the-publication-calendar--not-derivable): no weekend date is
ever present, yet **2026-08-20 (a Thursday) is missing with no holiday explanation**. Probe every day
and let the empty array answer. `DO NOT` optimise this loop.

### 3.5 ⚠ The settled-zone hazard — why the default is all-hot (D4)

A settled date is pulled once and never re-probed. On *this* feed that costs two things, both silent:

1. **Revisions.** The feed has **no revision, version or status field**. A corrected price is
   delivered by re-serving the **same `marketDataId`** with new values (api §5). Only a re-pull can
   see it. A settled date freezes its first print forever.
2. **Late publication.** An empty `200` completes its unit as **SUCCESS** — it is indistinguishable
   from a weekend — so a date that had merely *not published yet* when first probed is recorded as
   permanently done and its prices are lost with no signal.

The shipped **30/30** default makes max age 29, so `age > SettledAfterDays` is never true and the
settled zone is **empty**. `EvolutionMarketsModule.RunAsync` warns whenever a configuration opens it,
naming the age band that turns stable. `SettledAfterDays` is clamped to ≥ 0 (a negative value would
settle the *entire* window on the first run).

**Standing mitigation:** `arm.FileLog` records every empty read, so
`SELECT … WHERE [RowCount] = 0` lists every date that produced nothing — the audit trail that makes
the hazard diagnosable even if it is ever opened.

---

## 4. Auth, transport, resilience

Full detail in [api §2](../apis/EvolutionMarkets.md#2-authentication--a-raw-api-key-in-the-authorization-header).
Design-relevant points:

- **`ApiKey` is the RAW `Authorization` header value — NOT HTTP Basic**, despite the requirement's
  phrasing. Set with `TryAddWithoutValidation` (a bare token is not a well-formed RFC 7235
  credential). Resolved from `core.Param(LoaderName='EvolutionMarkets', ParamName='ApiKey')` via the
  `SEE_DB` sentinel. Never logged, never in a fixture, never in `appsettings.json`.
- **No URL is ever sensitive** (the key is a header), so this loader logs request URIs **in full** —
  a deliberate divergence from CWG/StormVista.
- **Retry:** 5xx/408/network/429 only, honouring `Retry-After`. **Not** retried: 400 (loader bug),
  401 (nothing to refresh), 403 (entitlement), 404 (classified as empty).
- **⚠ 500 is retried and that has a known cost.** An unknown `field` name is reported as a `500`, not
  a `400` — a permanent caller bug wearing a transient status code, so a typo burns the full retry
  budget on every unit. The status code cannot distinguish it, so the policy stays correct and
  `EvoStatus.Describe` names the field list as the first thing to check on a reproducible 500.
- **Throttle:** 5 rps default. The vendor publishes no rate limit and no `429` was seen in ~60 calls.

---

## 5. The read

### 5.1 Tolerant parse contract
An unrecognised or unparseable value degrades to `NULL` and never fails the row or the run. **Only a
record whose KEY (`marketDataId`) is unusable is dropped, and it is counted.** `Guid.Empty` counts as
unusable — it is a placeholder, not an id, and would collide across unrelated records.

Everything is navigated by candidate name via `EvoParse.Prop`, never by POCO binding, for two
reasons: rows are **heterogeneous** (an absent field has its key *omitted*, not nulled), and the
response spelling differs from the request spelling for five fields.

### 5.2 Numbers never default to zero
A fabricated `0` is a real, tradeable price. An unparseable measure becomes `NULL`. An integral
decimal (`5.0`) becomes `5`; a genuinely fractional one becomes `NULL` rather than being truncated
into an invented count.

### 5.3 Per-page tolerance counters
`droppedNoKey`, `droppedDuplicateKey`, `droppedNotAnObject`, `truncated` — logged as **one** warning
line per page when non-zero, and summed into `arm.FileLog.DroppedRowCount`.

### 5.4 Status matrix
See [api §6](../apis/EvolutionMarkets.md#6-status-code-matrix-with-the-probes-evidence). The two
load-bearing rows:

- **`200 []` → `NotAvailable`, 0 rows, unit SUCCEEDS, no warning.** Routine. Warning here would emit
  ~10 warnings per run and make the log untrustworthy.
- **`400` → THROW.** A malformed request is a *loader bug*. Swallowing it like an empty read would
  record it as permanently done and lose that date forever.

### 5.5 The pager
`limit`/`offset`, stop on a short **or** empty page, `MaxPagesSafety = 200` as a runaway net.
`PageSize` is **clamped** into `1..10000` rather than validated, so a mis-set value cannot fail every
unit with a vendor `400` the operator has to decode.

Two subtleties the tests pin:
- The short-page test uses the count of **array elements the vendor sent**, not rows successfully
  mapped. Otherwise a dropped record would make a full page look short and end paging early.
- When the row count is an **exact multiple** of the page size, one extra request is required to see
  the empty page. Optimising it away would truncate exactly those datasets.

Cross-page de-dup on the PK (first occurrence wins, duplicates counted) — defensive against a
concurrent vendor-side insert shifting the offset window.

### 5.6 The pinned projection (D2) and its drift guard
The exact 23-name list in
[api §4.3](../apis/EvolutionMarkets.md#43--the-field-vocabulary-trap). After the first populated
page the reader checks the response for `EvoRequestFields.ExpectedResponseKeys` and logs
**`PROJECTION DRIFT`** naming the missing keys. That set deliberately excludes the nine
never-populated fields and `tenor` (absent on ~53% of rows) — including them would make the guard cry
wolf on every healthy load and it would be muted within a week.

If the projection *is* trimmed, the `change` slot arrives carrying the `term` **string**, which the
tolerant decimal parse turns into `NULL`. **The failure mode is a silent column of NULLs, never a
wrong number** — and `arm.usp_ValidateLoad`'s `AllNullChangeDates` check catches it server-side too.

---

## 6. `arm.FileLog` — the audit hub

One row per business date pulled, for **every** outcome. Natural key
`(EndpointId, RepresentativeDate)`; the surrogate `Id` is kept because `arm.MarketData` references it.

**Why it is load-bearing here:** a non-publishing day returns `200 []`, so "that date published
nothing" and "we never asked" are indistinguishable in `arm.MarketData` — both are an absence of rows.
The hub is the only place the distinction is recorded.

**⚠ `RepresentativeDate` is `NOT NULL` — a deliberate divergence from AGSI/NGI.** Those loaders have
an *undated* snapshot endpoint, so theirs is nullable and their upsert MERGE needs an explicit
`(… = … OR (… IS NULL AND … IS NULL))` predicate to reconcile SQL Server's NULL-as-equal behaviour in
a `UNIQUE` constraint with NULL-as-unknown in a join. **This loader has no undated endpoint**, so the
column is `NOT NULL` and the MERGE uses plain equality. Do not copy that OR-branch in — it would be
dead code implying a case that cannot occur.

**No region axis and no dataset axis.** The only request axis is the date. A dataset axis would also
collide confusingly with the payload column `arm.MarketData.Market`.

Extra columns beyond the repo baseline: `DroppedRowCount`, **`PageCount`** (a `PageCount > 1` is the
audit trail proving the pager ran; an unexpectedly high one is the first sign of a vendor paging
change), and `ErrorMessage`.

---

## 7. Time basis

US-Central (`America/Chicago`, falling back to `Central Standard Time`, then UTC with a warning).
Rationale in §3.2. `priceTs` is stored as **UTC** — it is an instant, not a calendar date.

Contrast, documented on purpose: AGSI uses CET, CWG US-Eastern, StormVista UTC; NGI and IIR use
US-Central. This loader matches NGI/IIR.

---

## 8. Storage

### 8.1 `arm.MarketData`
See `sql/EvolutionMarkets/001`. Grain: instrument × term × tenor × business date. PK
`MarketDataId` — the vendor surrogate, justified and guarded per
[api §5](../apis/EvolutionMarkets.md#5-the-primary-key--a-vendor-supplied-surrogate).

### 8.2 Nullability
Every non-PK payload column is NULLable, as supplied — and correctly so: **nine are always NULL**
for the only permissioned dataset, so a `NOT NULL` on any of them would reject 100% of the feed.
Anomalies are surfaced by `arm.usp_ValidateLoad`, not by the DDL.

### 8.3 The TVP contract
`arm.MarketDataTvp`, **25 columns**, `FileLogId` first, `Checksum` last. `ModifiedAtUtc` and
`DateCreated` never cross it. The identical order must appear in **five** places: 001, 002, the 003
merge lists, `MarketDataSqlSink.BuildTable`, and `SinkTests`.

> ⚠ **This TVP is unusually easy to corrupt silently.** It carries four interleaved decimal/int pairs
> (`Price`, `Ask`/`AskSize`, `Bid`/`BidSize`, `Mid`/`MidSize`). Swapping `Ask` with `Bid` is
> **type-compatible** — nothing would raise, and every row would carry inverted prices, which looks
> plausible in a spot check. `SinkTests.BuildTable_PutsEachValueInItsOwnColumn` asserts each value
> lands in its own *named* column for exactly this reason, and
> `arm.usp_ValidateLoad`'s `BidAboveAsk` check is the server-side backstop.

### 8.4 `Checksum` and the MERGE short-circuit (D3/U1)
FNV-1a/32 over the 22 payload columns (`MarketDataId` excluded — it is the merge key and identical on
both sides; `FileLogId` excluded — provenance, and hashing it would make **every** row look changed on
**every** run, silently defeating the guard).

```sql
WHEN MATCHED AND (tgt.[Checksum] IS NULL OR tgt.[Checksum] <> src.[Checksum]) THEN UPDATE …
```

**Why not `string.GetHashCode`:** .NET randomises string hashing **per process**, so the same row
would hash differently every run. Every row would look changed, and the guard would be silently
useless — worse than absent, because it would *look* like it was working.
**Why not SQL `BINARY_CHECKSUM`:** not guaranteed stable across SQL Server versions or collations,
documented as collision-prone, and not assertable in a unit test.

Normalisation matters: decimals render `F8` (a `decimal` preserves trailing zeros, and the API's JSON
number scale varies row to row, so `0.5` vs `0.50` would otherwise report a phantom change);
`PriceTs` renders at second precision to match `DATETIME2(0)`; `NULL` and `""` hash **differently**
so a cleared value registers as a change; fields are `U+001F`-separated so `("ab","c")` and
`("a","bc")` cannot collide.

> **⚠ CONSEQUENCE: `@@ROWCOUNT` COUNTS CHANGED ROWS, NOT ROWS SENT.** A re-run over an unchanged
> window reports `RecordsProcessed = 0` while having verified ~4,400 rows. **That is a healthy
> success**, and `core.LoadLog` will show 0 records for that unit. Do not "fix" it by removing the
> guard.
>
> Changing the canonical form invalidates every stored `Checksum`: the next run sees every row as
> changed and re-stamps every `ModifiedAtUtc` once. Recoverable, but a real event —
> `ChecksumTests` pins the value (`-1779413438`) so it cannot happen by accident.

### 8.5 Merge semantics
Upsert-only. **There is no `WHEN NOT MATCHED BY SOURCE` / DELETE branch, and there must never be**:
an empty `200` is a routine non-publishing day, so a DELETE branch would wipe a date's prices every
weekend. The batch is de-duped on `MarketDataId` (ROW_NUMBER, highest `FileLogId` wins) both in
`BuildTable` and in the proc — a MERGE errors outright if one target row is matched twice.

---

## 9. Validation — `arm.usp_ValidateLoad(@DateFrom, @DateTo)`

17 checks, one result set, uniform shape (`CheckName, Scope, ExpectedCount, ActualCount, Detail`).
**Observational only: never throws, never `RAISERROR`s.** A non-NULL `ExpectedCount` that differs
from `ActualCount` is logged as a warning; everything else is informational. The window is the
*same* `EvoTime.ResolveWindow` call the provider makes.

**Anomaly checks (`ExpectedCount = 0`):** `DatesNotPulled`, `FailedRequests`, `DroppedRecords`,
**`DuplicateCompositeKey`**, `NullBusinessDate`, **`AllNullChangeDates`**, `BidAboveAsk`,
`PriceTsDateMismatch`, `BusinessDateOutsideWindow`.

The two starred ones are the load-bearing tripwires:
- **`DuplicateCompositeKey`** — guards the vendor-surrogate PK assumption (api §5). **Do not remove.**
- **`AllNullChangeDates`** — the server-side signature of a trimmed `field` projection (§5.6).

**Informational on purpose — do NOT promote:** `RowCount`, `DistinctBusinessDates`, `ZeroRowDates`,
`MidOutsideBidAsk` (the vendor rounds `Mid` to 4 dp, so small counts are expected),
**`PopulatedNullableExtras`** (a non-zero count is *good news* — a new entitlement),
`DistinctPriceType`, `DistinctCurrency`, `DistinctInstruments`.

---

## 10. Configuration (`Loaders:EvolutionMarkets`)

| Setting | Default | Note |
|---|---|---|
| `ConnectionString` | `…Database=EvolutionMarkets…` | |
| `BaseUrl` | `https://evolve-api.evomarkets.com` | coalesces a blank/null binding |
| `ApiKey` | `SEE_DB` | RAW `Authorization` value — **not** Basic. Never logged |
| `EnabledEndpoints` | `[ "MarketDataHistory" ]` | null binding ⇒ "run nothing" + warning |
| `DaysBack` | **30** | per the loader spec; clamped ≥ 1; warns above 60 (vendor retention) |
| `SettledAfterDays` | **30** | `== DaysBack` ⇒ all-hot ⇒ revisions land (§3.5) |
| `HotZoneKeyStrategy` | `RunDate` | or `RunId` |
| `PageSize` | 5000 | clamped into `1..10000` |
| `DatasetName` | `null` | null ⇒ **every permissioned dataset** (a new entitlement needs no config change) |
| `RequestsPerSecond` | 5 | vendor publishes no limit |
| `MaxConcurrentWorkUnits` | 4 | |

**Required before any run:** `core.Param(LoaderName='EvolutionMarkets', ParamName='ApiKey')` — store
the **bare token**, not base64, not `Basic …`, not `Bearer …`.

---

## 11. Concurrency and idempotency

- Overlap across host runs: the platform's SQL app lock per loader.
- `SqlWriteGate` serialises the fact merge; `arm.usp_UpsertFileLog` takes a **distinct** key, so the
  hub upsert and the fact merge never serialise against each other or deadlock.
- Re-running is safe: PK MERGE + upsert-only + the checksum guard ⇒ **safe to over-schedule**, no
  duplicates, and no `ModifiedAtUtc` churn on unchanged rows.

---

## 12. Risks and open items

1. **The PK is a vendor id.** Justified and verified (api §5), guarded by `DuplicateCompositeKey`.
   Fallback if that check ever fires: re-key on `(InstrumentId, Term, Tenor, BusinessDate)` and keep
   `MarketDataId` as a `UNIQUE` attribute.
2. **Same-day latency is one day.** `history` cannot return today's data (today → `200 []`). If
   same-day prices are ever needed, add a second pipeline over `GET /v1/market-data` (api §8).
3. **No instrument dimension.** `InstrumentId`/`InstrumentName`/`Market` are denormalised inline;
   `GET /v1/instruments` is out of scope by spec.
4. **Nine always-NULL columns** are carried by explicit decision (U2). If a new entitlement starts
   populating them, `PopulatedNullableExtras` will say so.
5. **Revision frequency unmeasured** — the mechanism is understood, no revision was observed in a
   single-day probe. The all-hot default is correct either way.
6. **60-day retention is unrecoverable.** Truncating the table loses everything older than 60 days
   permanently. Flagged in the 999 drop script.
7. **`arm.MarketData` is clustered on a random GUID.** Fine at this volume; revisit only if the
   permissioned dataset set grows by orders of magnitude.

---

## 13. Verification performed this pass

| Gate | Result |
|---|---|
| API contract | **Fully verified live**, zero reconstruction (api doc §0) |
| `dotnet build` (solution, Release) | **0 warnings, 0 errors** |
| `dotnet test` (EvolutionMarkets) | **203 passed, 0 failed** |
| `dotnet test` (solution-wide) | see the run report |
| SQL parse-check (ScriptDom, TSql160) | **4 files, 0 errors** |
| TVP contract (`tvp-contract-check`) | pinned by `SinkTests` against 002 |
| Resume key + status matrix | pinned by `WindowAndResumeKeyTests` + `StatusMatrixTests` |
| **SQL deployed** | **NO — never run against any server** |
| **Data loaded** | **NO** |
| **Data-quality validation** | **NOT PERFORMED** — requires a live, loaded database |
