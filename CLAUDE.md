# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

```bash
# Build entire solution
dotnet build DataLoaderPlatform.sln -c Release

# Publish deployable artifacts
dotnet publish src/DataLoader.Host/DataLoader.Host.csproj -c Release -o publish

# Run a specific loader (development)
cd src/DataLoader.Host/bin/Release/net8.0
DataLoader.Host.exe EnergyAspects

# Run multiple loaders in one process
DataLoader.Host.exe EnergyAspects Vulcan

# Run all loaders in Platform:EnabledLoaders
DataLoader.Host.exe
```

Exit codes: `0` = success or skipped (overlap guard), `1` = error, `2` = cancelled.

Automated tests live under `tests/` (xUnit): `DataLoader.Core.Tests`, `DataLoader.Vulcan.Tests`, `DataLoader.Platts.Tests`, `DataLoader.StormVista.Tests`, `DataLoader.CWG.Tests`, `DataLoader.AGSI.Tests`, `DataLoader.IHSPointLogic.Tests`, `DataLoader.IIR.Tests`, `DataLoader.OPIS.Tests`, and `DataLoader.NGI.Tests`. Run them with:

```bash
dotnet test DataLoaderPlatform.sln -c Release
```

## Architecture

This is a plugin-based ETL platform. One host executable discovers and runs independent loader plugins. The platform owns all cross-cutting concerns (DI, logging, retry, parallelism, audit logging, overlap protection, deadlock-safe write serialization); each loader plugin owns its source, transform, sink, and database schema.

**Dependency direction:** `DataLoader.<Vendor>` → `DataLoader.Core` ← `DataLoader.Host`

### Plugin Contract

Each loader assembly exposes exactly one `ILoaderModule`:

```csharp
public interface ILoaderModule
{
    string LoaderId { get; }       // e.g. "EnergyAspects"
    string DisplayName { get; }
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
    Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context);
}
```

`ModuleDiscovery` finds all `DataLoader.*.dll` assemblies at runtime via reflection. The host calls `RegisterServices` on every discovered module (building one shared DI container), then calls `RunAsync` only on enabled ones.

### ETL Pipeline Shape

`LoaderPipelineBase<TUnit, TItem, TRow>` implements the standard loop once. Plugins supply four small types:

| Interface | Responsibility |
|-----------|---------------|
| `IWorkUnitProvider<TUnit>` | Enumerate what needs loading (e.g., mapping × date window, or files in a directory) |
| `ISourceReader<TUnit, TItem>` | Read raw items for one work unit from the external source |
| `ITransformer<TItem, TRow>` | Convert items to DB rows (use `IdentityTransformer<T>` if none needed) |
| `ISink<TRow>` | Persist rows (usually a `SqlSinkBase<TRow>` subclass using TVP bulk merge) |

**Per-unit flow:**
1. `ILoadLogRepository.BeginAsync()` — if already succeeded → **skip** (idempotent reruns)
2. `ISourceReader.ReadAsync()` → `ITransformer.Transform()` → `ISink.WriteAsync()`
3. `ILoadLogRepository.CompleteSuccessAsync()` or `CompleteFailureAsync()`

Bounded concurrency via `ParallelRunner` (semaphore). HTTP retry via `RetryPolicyFactory` (Polly).

### Overlap Guard

`SqlLoaderOverlapGuard` acquires a SQL Server app lock named `DataLoader:<LoaderId>` at run start. If held by another process, the new invocation logs a warning and exits `0`. Safe to over-schedule.

### Concurrent Writes

`ParallelRunner` runs work units concurrently, so several `MERGE`/upsert procs can hit the same table at once — a deadlock (and NOT-MATCHED insert-race) risk. `SqlWriteGate` (`src/DataLoader.Core/Concurrency/`) is a process-wide, per-target keyed lock (`{server}/{db}::{proc}`) that serializes those calls. `SqlSinkBase.WriteAsync` acquires it automatically, so every TVP-merge sink is covered; direct proc callers acquire it too — the Platts, CWG, AGSI, IHSPointLogic, IIR and NGI `arm.usp_UpsertFileLog` writers, the StormVista `dbo.usp_UpsertFileLog` writer, the EnergyAspects sink, and IIR's reader census side-write (under a proc key **distinct** from its fact merge, so the two cannot deadlock against each other). It is **in-process only** — cross-process serialization is the overlap guard's job. `core.LoadLog` bookkeeping is intentionally *not* gated (keyed per work unit, so it rarely contends).

### Database Layout

- **Platform DB** (`core` schema): `core.LoaderRun` (one per host invocation), `core.LoadLog` (one per work unit attempt, keyed by `LoaderId` + `WorkUnitKey`), and `core.Param` (key/value config store keyed by `LoaderName` + `ParamName`, read by `core.usp_GetParam` for the `SEE_DB` indirection). Scripts in `sql/Core/` (run `001`–`004` in order).
- **Per-loader DB**: each loader owns its own schema (e.g., `ea` for EnergyAspects; `arm` for Platts, CWG, AGSI, IHSPointLogic, IIR, OPIS, and NGI — the `arm` name is reused but each lives in its own database) — though `StormVista` deliberately uses the default **`dbo`** schema rather than a vendor prefix, per the `DATABASE_DEVELOPER` agent's own standing convention ("target schema is `dbo` unless the task says otherwise"). Scripts in `sql/<Vendor>/`. Connection string comes from `Loaders:<LoaderId>:ConnectionString`.

### Cross-Loader Conventions

Patterns every recent loader shares. Follow them rather than re-deriving — deviating is a
deliberate, stated decision, not a default.

**Table shapes** — three, pick by role:

| Role | Shape |
|------|-------|
| Dimension / lookup / `FileLog` | `Id INT IDENTITY(1,1)` PK **first**, `DateCreated DATETIME DEFAULT GETDATE()` **second**, then payload, then `ModifiedAtUtc DATETIME2(3)`. Natural key gets a `UNIQUE` constraint (the MERGE target). |
| High-volume fact | **Composite natural PK, no surrogate `Id`** (e.g. `arm.GasStorage (EntityId, GasDayStart)`, `arm.OfflineEvent (RunDate, EventId)`). Still leads with `DateCreated`, still carries `ModifiedAtUtc`. Repeated per-entity strings normalize out into a dimension referenced by FK. |
| Per-run census / id-catalog | Minimal: `(RunDate, <Id>)` PK + `DiscoveredAtUtc`/`ModifiedAtUtc`. **No** `Id`, `DateCreated`, `FileLogId`, or FKs. See `arm.PlantSummary` in `sql/IIR/001`. |

**TVP contract (load-bearing).** Every `SqlSinkBase.BuildTable` DataTable must match its TVP in
`sql/<Vendor>/002` by column **NAME + ORDER + TYPE** — the TVP binds *by position*, so a silent
reorder corrupts every loaded row. `FileLogId` is first where present. `ModifiedAtUtc` and computed
columns (e.g. `GEOGRAPHY`) never cross the TVP — the MERGE proc stamps/builds them. Scalars that are
constant per batch (`@RunDate`) are proc parameters, not TVP columns. Procs dedup on the merge key
(`ROW_NUMBER`, last wins) before the `MERGE`.

**Audit hub.** Each vendor DB has a `FileLog` table + `usp_UpsertFileLog` — one row per endpoint
pull per run (path, status, HTTP status, row count), with `FileLogId` stamped onto the fact rows for
provenance. This is per-loader and separate from the platform's `core.LoadLog`.

**Post-load validation.** Loaders ship `<schema>.usp_ValidateLoad(@RunDate…)` returning ONE result
set with a uniform shape (`CheckName, Scope, ExpectedCount, ActualCount, Detail`), called in-pipeline
by a C# `<Vendor>LoadValidator`. It is **observational** — logs warnings, never throws (a legitimately
sparse day must not fail a run). See `sql/AGSI`, `sql/StormVista`, `sql/IHSPointLogic`, `sql/IIR`,
`sql/OPIS`, `sql/NGI`.

**Resume keys.** The work-unit `Key` *is* the idempotency contract (`core.LoadLog` skips only keys
already recorded successful). Established shapes: **two-zone settled/hot** (a date older than
`SettledAfterDays` gets a stable key → loaded once; recent dates get a run-varying key → re-pulled),
**trailing-window go-forward** (CWG), and **`HotKeyStrategy`** enums selecting the hot-zone cadence
(`RunDate` = once per day, `RunHour` = once per hour, `RunId` = every run).

**A legitimate "no data" status is not an error.** Where an endpoint answers "nothing published for
this key" with a non-2xx code, that is a **successful empty read** — an audit row, zero rows, and the
work unit *succeeds*. Only a malformed-request status is a real failure, because it can only be our
bug. NGI is the sharp case: `bidweekDatafeed.json` is a *monthly* feed queried per calendar day, so
roughly **58 of every 60** work units legitimately return `404`, while a `400` means a bad
`issue_date`. Two consequences worth internalising: never use `HttpJsonSourceReaderBase` (its
`EnsureSuccessStatusCode` would fail almost every unit) and never exclude the auth status from the
auth handler by folding it into the retry policy. Note the interaction with the resume key above —
because such a unit *succeeds*, a **settled** (stable) key records it done forever, so a date that was
empty only because it had not been published yet is never re-probed. Keep those units in the hot zone.

**Do not inherit a template loader's coupling.** AGSI's two pipelines are ordered and dependent (a
discovery endpoint fills a dimension that a reference provider reads to enumerate the fact). NGI is
structurally its twin and deliberately **uncoupled** — its fact work units come from the date window
alone, so there is no reference provider, no barrier and **no FK**, and a fact-only run is fully
valid. Two endpoints sharing a vocabulary *today* is an observation, not a contract: report divergence
in `usp_ValidateLoad` informationally instead of enforcing it with a foreign key that would make
pipeline arrival order load-bearing.

**Parsing failures that throw nothing.** Three have shipped here; each produced an empty or all-NULL
table on an HTTP 200. (1) **Overload resolution** — a one-arg `Str(element)` call binding to
`Str(JsonElement, params string[])` instead of `Str(JsonElement?)`, passing an empty name array and
returning `null` for every row (this dropped all 163 NGI location rows). Prefer helper signatures
whose arities cannot overlap. (2) **JSON names containing spaces** (`"Point Code"`, `"Issue Date"`) —
no `System.Text.Json` naming policy matches them, so read such payloads with explicit literal names,
never implicit POCO binding. (3) **String null sentinels** — NGI encodes "no value" as the literal
string `"None"`; numerics survive by accident because `TryParse` fails, but an unguarded string field
persists the text, so route *every* field through one central helper. Use a **narrower** sentinel list
for key fields than for measures, or a legitimate key resembling a sentinel gets silently dropped.

**Multi-endpoint loaders are descriptor-driven.** One record per endpoint (path, query rule, parse
shape, target table/TVP/proc) fanning out to N pipelines over shared readers/parsers, so only the row
type, its `From(...)` factory, and its sink repeat. See CWG (15), IHSPointLogic (25), IIR (3).

**HTTP auth + handler order.** Auth lives in a `DelegatingHandler`. Ordering is
**retry (OUTER) → auth → throttle (INNER)** so a re-mint is retried and throttling is innermost. In
use: `?apikey=` query (CWG, StormVista), `x-key` header (AGSI), HTTP Basic/PAT (IHSPointLogic),
JWT bearer with re-mint-once-on-401 (IIR; NGI, whose secret sits in the mint request BODY rather than the URL). Call `RemoveAllLoggers()` on any client whose URL carries
credentials. File feeds authenticate at the protocol level instead — SFTP user/password over SSH.NET
(Platts), plain FTP user/password over FluentFTP (OPIS) — behind an `IFileSystemDriver`.

**Tolerant JSON parsing.** Readers accept multiple candidate property names/casings and degrade an
unrecognized field to NULL rather than failing the run; a record missing its key is dropped and counted.

**Secrets.** Sensitive settings default to the `"SEE_DB"` sentinel and resolve from `core.Param` —
see [Configuration](#configuration). Never a literal secret in `appsettings.json`.

### Per-Loader Artifacts

A loader is "complete" when all five exist and agree:

| Artifact | Path | Owner |
|----------|------|-------|
| API field reference | `docs/apis/<Loader>.md` | `API_DOCUMENTATION_EXPERT` |
| Flow/design spec | `docs/design/<Loader>.md` | `APPLICATION_DESIGNER` |
| SQL scripts | `sql/<Vendor>/001…003`, `999_Drop…` | `DATABASE_DEVELOPER` |
| C# loader | `src/DataLoader.<Vendor>/` | `CODER` |
| Tests | `tests/DataLoader.<Vendor>.Tests/` | `CODE_TESTER` |

`docs/db/<Loader>.md` is a legacy hand-written *input* spec that exists only for `EnergyAspects` and
`StormVista`; it is **not** produced for new loaders — read it if present, don't create it. There is
**no** `docs/quality/` directory: data-quality expectations live in the design doc's validation
section and are executed by `usp_ValidateLoad`.

## Adding a New Loader

1. Create `src/DataLoader.Foo/DataLoader.Foo.csproj` referencing `DataLoader.Core`
2. `FooSettings : LoaderSettingsBase` — bind from `Loaders:Foo` in `RegisterServices`
3. `FooWorkUnit : WorkUnit` — define a stable `Key` (used for idempotency)
4. `ISourceReader<FooWorkUnit, FooItem>` — use `HttpJsonSourceReaderBase` for REST, `IFileSystemDriver` for file-based
5. `ITransformer<FooItem, FooRow>` or register `IdentityTransformer<T>`
6. `FooSqlSink : SqlSinkBase<FooRow>`
7. `IWorkUnitProvider<FooWorkUnit>`
8. `FooModule : ILoaderModule`
9. Add `sql/Foo/` scripts for the loader's own schema (numbered `001…` in dependency order: `001` schema+tables, `002` TVP types, `003` procedures; plus a guarded, idempotent `999_DropFooObjects.sql` teardown that drops every object in reverse dependency order — see `sql/AGSI/`). Include a `FileLog` table + `usp_UpsertFileLog`, and a `usp_ValidateLoad` per [Cross-Loader Conventions](#cross-loader-conventions)
10. Add `<ProjectReference>` from `DataLoader.Host` to the new project
11. Add `Loaders:Foo` section in `appsettings.json` (sensitive fields = `"SEE_DB"`); add `"Foo"` to `Platform:EnabledLoaders` or pass as CLI arg
12. Add `tests/DataLoader.Foo.Tests/` to the solution — at minimum the TVP column NAME/ORDER/TYPE contract, the parse/row-factory mapping, and the resume-key rule
13. Write `docs/apis/Foo.md` and `docs/design/Foo.md`, and add a bullet to **Reference implementations** below

The host and `DataLoader.Core` never change when adding a loader.

**Reference implementations** — one distinctive trait each. The bullets below plus each loader's
`docs/design/<Loader>.md` are the current record; [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
("What Each Project Contains") has longer write-ups but predates AGSI, IHSPointLogic, IIR, OPIS and NGI:
- `DataLoader.EnergyAspects/` — REST/JSON; the most complete example.
- `DataLoader.Vulcan/` — incremental REST (POST SQL to SynMax `query_datalinks`); 5 tables via 5 closed pipelines; watermark-based resume.
- `DataLoader.Platts/` — SFTP (SSH.NET); work-unit `Key` embeds the SFTP `LastModified`, so a file is reprocessed only when it changes; `arm.FileLog` audit.
- `DataLoader.StormVista/` — HTTP+CSV hybrid; custom windowed pipeline + two-zone (settled/hot) resume key; `dbo` schema with `dbo.FileLog` as a normalized hub.
- `DataLoader.CWG/` — HTTP+CSV, descriptor-driven; 15 per-endpoint pipelines over 5 shared CSV parse shapes; go-forward-only trailing-window resume; `arm` schema, `arm.FileLog` hub. Build-only (disabled); live deploy/validation deferred. See also `docs/apis/CWG.md`, `docs/design/CWG.md`, `sql/CWG/`.
- `DataLoader.AGSI/` — HTTP+JSON (GIE gas-storage inventory); a country-discovery pipeline (`/api/about` → `arm.GasStorageEntity`) feeds a per-country×date fact pipeline (`arm.GasStorage`), **normalized** so the fact references the entity dimension via an `EntityId` FK rather than repeating country strings (PK `(EntityId, GasDayStart)`); StormVista-style two-zone (settled/hot) resume key but over `LoaderPipelineBase` directly (no windowed orchestrator); `x-key` **header** auth; `arm` schema, `arm.FileLog` hub; ships a guarded `999_DropAgsiObjects.sql` teardown. Build-only so far (disabled). See also `docs/apis/AGSI.md`, `docs/design/AGSI.md`, `sql/AGSI/`.
- `DataLoader.IHSPointLogic/` — HTTP+JSON (S&P Global / IHS Markit Connect — PointLogic gas), the largest loader: **25 endpoints, descriptor-driven**, one shared tolerant JSON pager over two envelope shapes (flat array and `{PagingInfo,Data}` wrapper, `?pageIndex=` 0-based paging). **HTTP Basic (PAT) auth** — `Authorization: Basic base64(ClientId:ClientSecret)` via a delegating handler (no token/bearer). A **3-tier discovery graph** run with hard barriers (Tier 0 lookups+facts → Tier 1 discovery lookups county/facility/subregion → Tier 2 parametrized facts), driven by **5 AGSI-style load-once/fail-fast reference providers**; 5 work-unit archetypes (LatestLookup / GoForwardSnapshot / DiscoveryLookup / DiscoveryDatedFact / BatchedFact ≤50-id `pointIds` batches); go-forward accumulation (UTC report-date stamping) with the two-zone resume key on the only date-param endpoints (supplyDemand region/subregion). **DB-driven per-endpoint schedule**: run the host hourly and each endpoint runs/skips by its `arm.Endpoint.RunHoursCST` (`'*'` or a CSV of **US Central, DST-aware** hours — e.g. `PointVolume='*'` hourly, others `'6'` = 06:00 Central daily), gated in `RunAsync` before `ExecuteAsync` by converting the run's UTC start to Central (CWG's US-Eastern TZ pattern); the hot resume key stays **UTC**-monotonic and hour-granular (`PlHotKeyStrategy.RunHour`, `yyyyMMddHH`, the default) so a scheduled hourly endpoint re-pulls each hour while a same-hour re-run idempotently skips. `arm` schema (dimension tables are unprefixed — `arm.Region`, `arm.Point`, …), `arm.FileLog` hub, guarded `999` teardown. Build-only (disabled); schemas were verified against the live API. See also `docs/apis/IHSPointLogic.md`, `docs/design/IHSPointLogic.md`, `sql/IHSPointLogic/`.
- `DataLoader.IIR/` — HTTP+JSON (Industrial Info Resources IDB v2.7 — industrial plant/unit/outage data), **3 independent descriptor-registered pipelines, no discovery tiers**: `Plant` → `arm.Plant`, `Unit` → `arm.Unit`, `OfflineEvent` → `arm.OfflineEvent`. Each pipeline runs a **mandatory intra-pipeline two-step summary→detail**: STEP 1 pages `POST …/{plants|units|offlineevents}/summary?physicalAddressCountryName=U.S.A.&…=Canada` (configurable `PhysicalAddressCountryNames`) to discover entity ids (+lat/long); STEP 2 batches those ids ≤50 into `POST …/detail?<idParam>=…&<idParam>=…` — the **detail** record is what populates the fact tables. Discovered ids are persisted to per-run **id-catalog census tables** (`arm.PlantSummary`/`arm.UnitSummary`/`arm.OfflineEventSummary`, **id-only**: PK `(RunDate, Id)`, no lat/long) via a **reader side-write** through an injected summary sink on its own `SqlWriteGate` key (the pipeline's single `ISink` stays the fact sink). First loader with **JWT bearer-token auth** — a thread-safe singleton token provider mints via `POST /token?username=&password=&tokenLifeTime=` (tolerant token read from body or header), and `IirTokenAuthHandler` re-mints once on 401 (handler order retry → auth → throttle, IHS pattern). `OfflineEvent` is a **country-only snapshot** (kind/status filters optional, off by default) stamped with a **US-Central `RunDate`** into its `(RunDate, EventId)` PK (daily go-forward history); `Plant`/`Unit` are single-row-per-id upserts. First loader to write the SQL `GEOGRAPHY` type — `PlantPoint` is built **in the MERGE proc** via `geography::Point(lat,long,4326)` with a range guard, so the TVP/C# carry only lat/long FLOATs; because lat/long may be **summary-only**, the detail row takes coordinates from detail else falls back to the STEP-1 summary value held in memory (**carry-forward**; the census tables themselves store no coordinates). Custom `IirSqlSinkBase` (procs take `@Rows`, OfflineEvent an extra `@RunDate`) with last-wins dedup + ~10k-row chunked MERGEs for the ~112k-row `plants` pull; `arm` schema, `arm.FileLog` hub, guarded `999` teardown. Build-only (disabled); endpoints/auth/paging source-confirmed but per-field `detail` JSON casing/nesting is reconstructed and needs a one-shot live verification (see the checklist in `docs/design/IIR.md`). See also `docs/apis/IIR.md`, `docs/design/IIR.md`, `sql/IIR/`.
- `DataLoader.OPIS/` — **plain FTP + CSV** (OPIS "LP" daily refined-product price report), the platform's **first FTP loader**: `OpisFtpFileSystem : IFileSystemDriver` over **FluentFTP** (Platts' SSH.NET driver speaks SFTP and cannot serve it; `FtpWebRequest` is obsolete on net8.0). One pipeline, **one work unit per file** — the drop holds a rolling ~month of `<yyyyMMdd>LP.csv` in the root, weekdays only, and `DaysBack=0` (the default) takes every file present. Platts-style resume key embedding the server's `MDTM` stamp + size, so an unchanged file is skipped forever and a **republished** one is reprocessed. First loader whose **one sink calls one proc that writes TWO tables in a single transaction** (`arm.usp_BulkMergeLPReport` over one `arm.LPReportTvp`), because both take an identical payload and differ only in grain: `arm.LPReport` PK `(Mkt_Prod, [Date], Timing)` holds the **current** value, `arm.LPReportHistory` PK `(Mkt_Prod, [Date], Timing, Price)` holds the **price-change history** — `Price` is a record-status code (`I` = initial, `U` = revision), and OPIS really does republish a prior day's row as `U` with different prices, so the extra key column is what preserves both. Because files merge concurrently and out of order, every `WHEN MATCHED` branch is guarded by `src.SourceFileDate >= tgt.SourceFileDate` (a payload column parsed from the file name), making the outcome independent of arrival order and re-run count. Tolerant CSV parse: two-digit-year `MM/dd/yy` dates, space-padded fields trimmed (`Mkt_Prod` is a key), blank `Low`/`High` → NULL on basket rows, unkeyable rows dropped-and-counted. `arm` schema (DB `OPIS`), `arm.FileLog` hub keyed on file name recording every outcome, `arm.usp_ValidateLoad`, guarded `999` teardown. **Verified live** end-to-end through parsing (24 files, 4835 rows, 0 dropped, 4830 current vs 4831 history keys); the SQL merge itself is the one link not yet run against a database. See also `docs/apis/OPIS.md`, `docs/design/OPIS.md`, `sql/OPIS/`.
- `DataLoader.NGI/` — HTTP+JSON (NGI Data Services — natural gas **bidweek price survey**), the repo's **first loader whose entire field set is live-verified with zero reconstruction**. Two **closed, fully INDEPENDENT pipelines**: `BidWeekLocations` (`/bidweekLocations?format=json`) → `arm.BidWeekLocation` (PK `(PointCode)`) and `BidWeekData` (`/bidweekDatafeed.json?issue_date=`) → `arm.BidWeekData` (PK `(IssueDate, PointCode)`). Structurally AGSI's twin but with AGSI's coupling deliberately **removed** — there is no reference provider, no `usp_Get…`, no barrier and **no FK**: `BidWeekData` work units derive from the **date window alone**, so a `BidWeekData`-only run is fully valid (the two endpoints share the same 163-code vocabulary today, but that is a same-day observation, not a contract, and the validator reports divergence *informationally*). Second **JWT bearer** loader after IIR, with one difference that matters: the secret rides in the **mint request BODY** (`POST /auth` with `{"email","password"}` — the API field is `email`, the setting is `Username`) rather than the URL, so the body is never logged; the 24h token is cached for process lifetime and **re-minted once on 401** (there is no refresh endpoint, so the returned `refresh` is captured-and-ignored), handler order retry → auth → throttle. **`404` is the normal case, not an error**: NGI Bidweek is a *monthly* survey, so with the required `DaysBack`/`SettledAfterDays` = **60/60** roughly 58 of 60 units legitimately return `404` ("no publication on this date") → `NotAvailable` hub row, 0 rows, **unit succeeds**; a `400` (malformed `issue_date`) throws, because that can only be our bug. The window enumerates **every calendar day with no business-day/holiday filter** — proven necessary, not cautious: `2026-08-01` is a **Saturday** that returns 163 records while Friday `2026-07-31` returns `404`, so the vendor's own "issue date will always be a business day" claim is false and a weekday filter would silently drop the entire August issue. Three parse traps the code guards centrally: JSON field names **contain spaces** (`Point Code`, `Issue Date`, `Pricing Point` — default `System.Text.Json` POCO binding would yield a silently all-NULL table, so lookups are tolerant `JsonElement` by explicit name), **every value is a quoted string** including numerics, and the null sentinel is the **literal string `"None"`** — which must map to NULL for *string* columns too, or `Region`/`PricingPoint` persist the text "None". `data` is a **dictionary keyed by point code**, not an array; `bidweekLocations` is `{"Bidweek Locations":{name → code}}`, where the **key is the name and the value is the code** (inverting it throws nothing and corrupts everything). `Region`/`PricingPoint` stay **denormalized inline** on the fact — `Region` is not obtainable from the locations feed at all and the volume is ~163 rows/month. StormVista-style two-zone settled/hot resume key (`HotZoneKeyStrategy`, `RunDate` default) over `LoaderPipelineBase` directly, with one shared `NgiTime.ResolveWindow` (US-Central, never emits a future date) used by **both** the work-unit provider and the validator. `arm` schema (DB `NGI`), `arm.FileLog` hub keyed `(EndpointId, RepresentativeDate)` — NULL-dated for Locations, which a `UNIQUE` constraint permits exactly once — `arm.usp_ValidateLoad`, guarded `999` teardown. Two verified vendor quirks the validation deliberately tolerates: 2 of 163 rows are priced but carry NULL `Volume`/`Deals`, and the `USAVG` national aggregate is published with `Region = 'California'`. Build-only (disabled); **both endpoints, the auth flow and the 404/400 behaviour were confirmed against the live API**, but the SQL has never been deployed and no load has run. See also `docs/apis/NGI.md`, `docs/design/NGI.md`, `sql/NGI/`.

## Agents

This repo ships specialist subagents in `.claude/agents/` that automate an
end-to-end loader build. `MANAGER` coordinates; the others each own one stage
and hand off to the next.

### Agent use is MANDATORY, not optional

**You MUST use these agents for any work that touches a loader. Do NOT do this
work directly in the main thread, even when the change looks small or you are
confident you can do it yourself.** Skipping the agents skips the requirements
gathering, design, review, and validation that they enforce — which is how
fields, columns, edge cases, and regressions get missed.

Apply this decision rule BEFORE writing any code or SQL:

- **Any change spanning ≥2 stages** (e.g. new/changed API fields → SQL → C# →
  tests, or "add a loader", "add/change columns", "fix the mapping",
  "the endpoint returns more fields") → invoke **`MANAGER` FIRST**. Let it plan
  and sequence the specialists. Do not pre-empt it by editing files yourself.
- **A change cleanly scoped to one stage** (only SQL, only a code-review pass,
  only tests) → invoke that single specialist directly (`DATABASE_DEVELOPER`,
  `CODE_REVIEWER`, `CODE_TESTER`, …).
- **Genuinely trivial, zero-design edits** (a typo, a comment, a log string, a
  one-line config value) → you may do it directly. When in doubt, use the
  agents; do not rationalize a multi-file change into "trivial".

Non-negotiable requirements the agents exist to enforce — you own these
regardless of who does the typing:

- When an API/endpoint is involved, its **full field set** must be documented
  (via `API_DOCUMENTATION_EXPERT`) and every field mapped end-to-end (model →
  sink → TVP → table → merge proc). Never ship a partial column set.
- A column added or removed anywhere in that chain is changed at **every** link
  in the same pass — a TVP that drifts from its `BuildTable` binds by position
  and silently corrupts rows.
- SQL changes go through `DATABASE_DEVELOPER`; C# through `CODER`; both are
  reviewed (`CODE_REVIEWER`) and tested (`CODE_TESTER`) before you report done.
- After a load, data is validated (`DATA_QUALITY_VALIDATOR`).
- Report the actual `dotnet build` / `dotnet test` result. "Build-only" loaders
  (CWG, AGSI, IHSPointLogic, IIR, NGI) are compiled and unit-tested but have never been
  deployed or run against the live API — say so rather than implying a load ran.

If the user explicitly tells you to skip the agents for a given task, honor
that — but say which stages/requirements are being bypassed so the choice is
deliberate.

**Roster** — one stage each; full instructions and model live in `.claude/agents/<name>.md`.
Each subagent runs in its **own isolated context** and returns a short summary plus the
path to the artifact it wrote (not the full document):

- `MANAGER` — plans/sequences the build, summarizes each stage (coordinates only).
- `API_DOCUMENTATION_EXPERT` → writes the field reference to `docs/apis/<Loader>.md`.
- `APPLICATION_DESIGNER` → writes the flow spec to `docs/design/<Loader>.md`.
- `DATABASE_DEVELOPER` → writes `.sql` scripts to `sql/<Vendor>/`.
- `CODER` → writes the C# loader under `src/DataLoader.<Vendor>/`; applies review fixes.
- `CODE_REVIEWER` → returns a findings list (read-only).
- `CODE_TESTER` → writes/runs tests under `tests/DataLoader.<Vendor>.Tests/` (read-only on app code).
- `DATA_QUALITY_VALIDATOR` → returns a data-quality findings report (read-only).
  Requires a **live, loaded database**; on a build-only loader it has nothing to
  check, so skip it and say why.

**Required sequence** (skip a stage only when it plainly does not apply — e.g. no
API change means no documentation stage — and say so): documentation → design →
database → code → review (loops back to `CODER`) → test (loops back to `CODER`) →
data validation (loops back to `CODER`). Do not report a task complete until the
review and test stages have run and passed.

## Skills

No project skills (`.claude/skills/` is absent); all skills come from installed global
plugins (`superpowers`, `code-review`, `skill-creator`). Add project skills under
`.claude/skills/` and document them here.

## Configuration

`appsettings.json` has two top-level sections:

- `Platform` — host-level: `EnabledLoaders`, `MaxConcurrentLoaders`, `LoadLogConnectionString`, `DefaultDaysBackStart/End`
- `Loaders.<LoaderId>` — per-loader: `ConnectionString`, `MaxConcurrentWorkUnits`, `RetryCount`, `RetryDelayMs`, `WorkUnitTimeoutSeconds`, plus vendor-specific fields

Secrets come from environment variables (preferred over editing JSON):
```
DATALOADER_Loaders__EnergyAspects__ApiKey=…
```
Double underscore is .NET's section separator; prefix is `DATALOADER_`.

**Config from the database (`SEE_DB`):** any loader string setting whose value is the
sentinel `"SEE_DB"` is resolved at run time from the platform DB via
`core.usp_GetParam(<LoaderId>, <SettingName>)` (backed by `core.Param`, `sql/Core/004`).
The shared helper `LoaderServiceCollectionExtensions.AddLoaderSettings<TSettings>(configuration, loaderId)`
(which every loader uses in place of `services.Configure<TSettings>(…)`) binds the section
and registers a post-configure resolver (`SeeDbSettingsResolver<T>` over `ISeeDbParamStore`)
that reflects over the settings' string properties and swaps any `SEE_DB` value for the
DB value. It resolves lazily (only loaders that run), fails fast if a `SEE_DB` setting has
no `core.Param` row, and never logs the resolved secret. Resolution uses
`Platform:LoadLogConnectionString` — which therefore cannot itself be `SEE_DB`.
The shipped `appsettings.json` defaults every sensitive field (API keys, usernames,
passwords) to `"SEE_DB"` so real secrets live in `core.Param` (or env-var overrides),
never in the file; connection strings use Integrated Security and stay in the file.
A new loader should follow this convention for its own secret fields.

## Key Files

- `src/DataLoader.Core/Abstractions/` — all contracts a plugin author needs
- `src/DataLoader.Core/Pipeline/LoaderPipelineBase.cs` — the standard ETL loop
- `src/DataLoader.Core/Sinks/SqlSinkBase.cs` — TVP bulk merge helper
- `src/DataLoader.Core/Sources/HttpJsonSourceReaderBase.cs` — HTTP + Polly base
- `src/DataLoader.Core/Sources/IFileSystemDriver.cs` — file-source abstraction (local / FTP / SFTP; `RemoteFile` carries `LastModifiedUtc` + `Size`)
- `src/DataLoader.Core/Concurrency/SqlWriteGate.cs` — per-target write lock guarding parallel `MERGE`s from deadlocks
- `src/DataLoader.Core/Concurrency/ParallelRunner.cs` — bounded work-unit concurrency (semaphore)
- `src/DataLoader.Core/Configuration/` — `LoaderSettingsBase`, `SeeDbSettingsResolver`, `SqlParamStore` (the `SEE_DB` indirection)
- `src/DataLoader.Core/Hosting/LoaderServiceCollectionExtensions.cs` — `AddLoaderSettings<T>` and the keyed-DI helpers every module registers through
- `src/DataLoader.Core/Persistence/SqlLoaderOverlapGuard.cs` — the per-loader app lock
- `src/DataLoader.Host/Program.cs` — bootstrap and discovery entry point
- `sql/Core/` — platform database scripts (run these first, in order, before any loader scripts)
- `tests/` — xUnit test projects, one per loader (`DataLoader.Core.Tests`, `DataLoader.Vulcan.Tests`, `DataLoader.Platts.Tests`, `DataLoader.StormVista.Tests`, `DataLoader.CWG.Tests`, `DataLoader.AGSI.Tests`, `DataLoader.IHSPointLogic.Tests`, `DataLoader.IIR.Tests`, `DataLoader.OPIS.Tests`, `DataLoader.NGI.Tests`)
- `docs/apis/` + `docs/design/` — per-loader field reference and flow spec (the current per-loader record)
- `docs/ARCHITECTURE.md` — design rationale (predates AGSI / IHSPointLogic / IIR / OPIS / NGI)
