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
DataLoader.Host.exe EnergyAspects CsvExample

# Run all loaders in Platform:EnabledLoaders
DataLoader.Host.exe
```

Exit codes: `0` = success or skipped (overlap guard), `1` = error, `2` = cancelled.

There are no automated test projects in this solution.

## Architecture

This is a plugin-based ETL platform. One host executable discovers and runs independent loader plugins. The platform owns all cross-cutting concerns (DI, logging, retry, parallelism, audit logging, overlap protection); each loader plugin owns its source, transform, sink, and database schema.

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

### Database Layout

- **Platform DB** (`core` schema): `core.LoaderRun` (one per host invocation) and `core.LoadLog` (one per work unit attempt, keyed by `LoaderId` + `WorkUnitKey`). Scripts in `sql/Core/`.
- **Per-loader DB**: each loader owns its own schema (e.g., `ea`, `csv`). Scripts in `sql/<Vendor>/`. Connection string comes from `Loaders:<LoaderId>:ConnectionString`.

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
- `DataLoader.CsvExample/` — local file-drop loader
- `DataLoader.Vulcan/` — incremental REST loader (POST SQL to SynMax query_datalinks; 5 tables via 5 closed pipelines; watermark-based resume)

## Agents

This repo ships specialist subagents in `.claude/agents/` that automate an
end-to-end loader build. `MANAGER` coordinates; the others each own one stage
and hand off to the next. Invoke `MANAGER` first for any multi-stage loader
build or change; invoke a single specialist directly for a scoped task.

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

**Default sequence:** documentation → design → database → code → review (loops
back to `CODER`) → test (loops back to `CODER`) → data validation (loops back to
`CODER`).

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

## Key Files

- `src/DataLoader.Core/Abstractions/` — all contracts a plugin author needs
- `src/DataLoader.Core/Pipeline/LoaderPipelineBase.cs` — the standard ETL loop
- `src/DataLoader.Core/Sinks/SqlSinkBase.cs` — TVP bulk merge helper
- `src/DataLoader.Core/Sources/HttpJsonSourceReaderBase.cs` — HTTP + Polly base
- `src/DataLoader.Host/Program.cs` — bootstrap and discovery entry point
- `sql/Core/` — platform database scripts (run these first, in order, before any loader scripts)
- `docs/ARCHITECTURE.md` — design rationale
