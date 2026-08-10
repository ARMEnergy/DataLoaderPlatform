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

Automated tests live under `tests/` (xUnit): `DataLoader.Core.Tests`, `DataLoader.Vulcan.Tests`, `DataLoader.Platts.Tests`, and `DataLoader.StormVista.Tests`. Run them with:

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

`ParallelRunner` runs work units concurrently, so several `MERGE`/upsert procs can hit the same table at once — a deadlock (and NOT-MATCHED insert-race) risk. `SqlWriteGate` (`src/DataLoader.Core/Concurrency/`) is a process-wide, per-target keyed lock (`{server}/{db}::{proc}`) that serializes those calls. `SqlSinkBase.WriteAsync` acquires it automatically, so every TVP-merge sink is covered; direct proc callers (e.g. the Platts `arm.usp_UpsertFileLog` writer, the StormVista `dbo.usp_UpsertFileLog` writer, and the EnergyAspects sink) acquire it too. It is **in-process only** — cross-process serialization is the overlap guard's job. `core.LoadLog` bookkeeping is intentionally *not* gated (keyed per work unit, so it rarely contends).

### Database Layout

- **Platform DB** (`core` schema): `core.LoaderRun` (one per host invocation), `core.LoadLog` (one per work unit attempt, keyed by `LoaderId` + `WorkUnitKey`), and `core.Param` (key/value config store keyed by `LoaderName` + `ParamName`, read by `core.usp_GetParam` for the `SEE_DB` indirection). Scripts in `sql/Core/` (run `001`–`004` in order).
- **Per-loader DB**: each loader owns its own schema (e.g., `ea` for EnergyAspects, `arm` for Platts) — though `StormVista` deliberately uses the default **`dbo`** schema rather than a vendor prefix, per the `DATABASE_DEVELOPER` agent's own standing convention ("target schema is `dbo` unless the task says otherwise"). Scripts in `sql/<Vendor>/`. Connection string comes from `Loaders:<LoaderId>:ConnectionString`.

## Adding a New Loader

1. Create `src/DataLoader.Foo/DataLoader.Foo.csproj` referencing `DataLoader.Core`
2. `FooSettings : LoaderSettingsBase` — bind from `Loaders:Foo` in `RegisterServices`
3. `FooWorkUnit : WorkUnit` — define a stable `Key` (used for idempotency)
4. `ISourceReader<FooWorkUnit, FooItem>` — use `HttpJsonSourceReaderBase` for REST, `IFileSystemDriver` for file-based
5. `ITransformer<FooItem, FooRow>` or register `IdentityTransformer<T>`
6. `FooSqlSink : SqlSinkBase<FooRow>`
7. `IWorkUnitProvider<FooWorkUnit>`
8. `FooModule : ILoaderModule`
9. Add `sql/Foo/` scripts for the loader's own schema
10. Add `<ProjectReference>` from `DataLoader.Host` to the new project
11. Add `Loaders:Foo` section in `appsettings.json`; add `"Foo"` to `Platform:EnabledLoaders` or pass as CLI arg

The host and `DataLoader.Core` never change when adding a loader.

**Reference implementations:**
- `DataLoader.EnergyAspects/` — REST/JSON loader (most complete example)
- `DataLoader.Vulcan/` — incremental REST loader (POST SQL to SynMax query_datalinks; 5 tables via 5 closed pipelines; watermark-based resume)
- `DataLoader.Platts/` — SFTP loader (SSH.NET; one module, two closed pipelines — daily `.ftp` market files → `arm.SymbolData` and reference CSVs → `arm.Symbol`; work-unit `Key` embeds the SFTP `LastModified` so a file is reprocessed only when it changes; `arm.FileLog` audit)
- `DataLoader.StormVista/` — HTTP+CSV hybrid loader (StormVista Wx Models weighted-degree-day API; one module, two closed pipelines — Daily national and Regional/weekly, `EnabledFeeds` toggle). Custom windowed `ILoaderPipeline` (not `LoaderPipelineBase` directly) chunks each run's date range so a multi-year backfill never materializes its whole unit list at once; each chunk still runs through a real `LoaderPipelineBase` internally. Two-zone resume key: a settled init date (older than `SettledAfterDays`) gets a **stable** key (skipped forever once loaded); a hot/recent one gets a key that varies every run (always re-pulled) — because the API gives no per-file change signal to key on, unlike Platts' SFTP `LastModified`. DB schema is `dbo` (see Database Layout) with `dbo.FileLog` as a normalized **hub table**: every fact row (`DailyWdd`/`RegionalWdd`) carries only a `FileLogId` and reaches model/cycle/type/init-date/endpoint through it — no repeated dimension columns on the facts.

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
- SQL changes go through `DATABASE_DEVELOPER`; C# through `CODER`; both are
  reviewed (`CODE_REVIEWER`) and tested (`CODE_TESTER`) before you report done.
- After a load, data is validated (`DATA_QUALITY_VALIDATOR`).

If the user explicitly tells you to skip the agents for a given task, honor
that — but say which stages/requirements are being bypassed so the choice is
deliberate.

| Agent | Stage | Writes | Model |
|-------|-------|--------|-------|
| `MANAGER` | Plans and sequences the whole build, summarizes each stage | nothing (coordinates only) | opus |
| `API_DOCUMENTATION_EXPERT` | Documents the source API — endpoints, fields, types; recommends SQL types | field reference (read-only otherwise) | opus |
| `APPLICATION_DESIGNER` | Designs the loader's end-to-end flow (discovery, work units, idempotency/resume) | design spec (no code) | opus |
| `DATABASE_DEVELOPER` | Designs SQL Server tables + stored procedures | `.sql` scripts | opus |
| `CODER` | Implements the C# loader and applies review fixes | C# code | opus |
| `CODE_REVIEWER` | Reviews C# for correctness/security/conventions | findings list (read-only) | opus |
| `CODE_TESTER` | Writes and runs `dotnet test` with HTTP/SQL test doubles | test project | opus |
| `DATA_QUALITY_VALIDATOR` | Validates the loaded data (reconciliation, nulls, ranges, anomalies) | findings report (read-only on data) | sonnet |

**Required sequence** (skip a stage only when it plainly does not apply — e.g. no
API change means no documentation stage — and say so): documentation → design →
database → code → review (loops back to `CODER`) → test (loops back to `CODER`) →
data validation (loops back to `CODER`). Do not report a task complete until the
review and test stages have run and passed.

Note: these agents assume some conventions that differ from the rest of this
file — they read/write loader specs under `docs/apis|db|design|quality/` and
`DATABASE_DEVELOPER` writes SQL to `docs/db/scripts/<loader>/` rather than
`sql/<Vendor>/`, and `MANAGER` references a `DOCUMENTATION_WRITER` agent that is
not yet present in `.claude/agents/`. Reconcile these paths with the SQL/docs
layout above when using the agents.

## Skills

This repo defines no custom skills of its own (`.claude/skills/` is absent).
All skills available in a session come from installed global plugins
(e.g. `superpowers`, `code-review`, `skill-creator`), not from this project.
Add any project-specific skills under `.claude/skills/` and document them here.

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
- `src/DataLoader.Host/Program.cs` — bootstrap and discovery entry point
- `sql/Core/` — platform database scripts (run these first, in order, before any loader scripts)
- `tests/` — xUnit test projects (`DataLoader.Core.Tests`, `DataLoader.Vulcan.Tests`, `DataLoader.Platts.Tests`, `DataLoader.StormVista.Tests`)
- `docs/ARCHITECTURE.md` — design rationale
