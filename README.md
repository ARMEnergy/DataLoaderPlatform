# Data Loader Platform

A scalable .NET 8 platform for hosting many independent data loaders. Each
loader pulls data from a vendor (REST API, FTP, CSV drop, S3, …), transforms
it, and writes it to its own database. The platform provides the
cross-cutting machinery: configuration, DI, logging, parallelism, retry,
idempotent re-runs, audit logging, overlap protection.

The original `EnergyAspectsETL` project is now one plugin among many —
`DataLoader.EnergyAspects`.

---

## Deployment model

**One install location. One executable. Many loaders.**

```
C:\DataLoaderPlatform\
├── DataLoader.Host.exe          ← the single binary
├── DataLoader.Core.dll
├── DataLoader.EnergyAspects.dll
├── DataLoader.Vulcan.dll
├── DataLoader.Platts.dll
├── DataLoader.StormVista.dll
├── DataLoader.CWG.dll
├── DataLoader.<Vendor>.dll      ← one DLL per loader; drop in new ones here
├── appsettings.json             ← every loader's config in one file
└── … (transitive .NET DLLs)
```

The host's invocation mode is controlled by command-line arguments:

| Command                                                         | What runs                                                    |
| --------------------------------------------------------------- | ------------------------------------------------------------ |
| `DataLoader.Host.exe`                                           | Every loader in `Platform:EnabledLoaders` (parallel in-process up to `MaxConcurrentLoaders`) |
| `DataLoader.Host.exe EnergyAspects`                             | Just that loader, overriding config                          |
| `DataLoader.Host.exe EnergyAspects Vulcan`                      | Just those, in parallel in-process                           |

The scheduler should use the **second** form: one scheduled task per loader,
each invoking the same `.exe` with a different loader id.

---

## Solution layout

```
DataLoaderPlatform.sln
├── src/
│   ├── DataLoader.Core/           ← shared platform library
│   ├── DataLoader.Host/           ← the one executable
│   ├── DataLoader.EnergyAspects/  ← REST/JSON loader (migrated from
│   │                                 the original EnergyAspectsETL)
│   ├── DataLoader.Vulcan/         ← incremental REST loader (SynMax query_datalinks;
│   │                                 5 tables via 5 closed pipelines, watermark resume)
│   ├── DataLoader.Platts/         ← SFTP feed loader (SSH.NET; two closed pipelines;
│   │                                 work-unit key embeds the file's mtime)
│   ├── DataLoader.StormVista/     ← HTTP+CSV hybrid loader (StormVista Wx Models;
│   │                                 two closed pipelines; two-zone resume key)
│   └── DataLoader.CWG/            ← HTTP+CSV descriptor-driven loader (Commodity
│                                     Weather Group; 15 endpoints, arm schema)
├── sql/
│   ├── Core/                      ← platform DB scripts (LoaderRun, LoadLog, overlap guard)
│   ├── EnergyAspects/             ← ea schema
│   ├── Vulcan/                    ← Vulcan schema
│   ├── Platts/                    ← arm schema
│   ├── StormVista/                ← dbo schema (own StormVista database)
│   └── CWG/                       ← arm schema (own CWG database)
├── tests/                         ← xUnit test projects (Core, Vulcan, Platts, StormVista, CWG)
└── docs/ARCHITECTURE.md
```

---

## Scheduling

The expected operational shape: configure one Task Scheduler task (or cron
entry, or Kubernetes CronJob) per loader. Each task launches the same
`DataLoader.Host.exe` from the same install directory with a different
loader id as its argument.

### Windows Task Scheduler example

```
Task name:   DataLoader-EnergyAspects
Trigger:     Every 30 minutes
Action:      Program/script:  C:\DataLoaderPlatform\DataLoader.Host.exe
             Add arguments:   EnergyAspects
             Start in:        C:\DataLoaderPlatform
```

The same one-task-per-loader pattern applies to every loader added since,
including `Vulcan`, `Platts`, and `StormVista` — just point `Add arguments`
at the loader id.

For each task, set "If the task is already running" → **Run a new instance
in parallel.** The platform's overlap guard (see below) makes that safe.

### cron equivalent

```
*/30 *  * * *   cd /opt/dataloader && ./DataLoader.Host EnergyAspects
0    2  * * *   cd /opt/dataloader && ./DataLoader.Host Platts
```

### Running multiple loaders concurrently

This is the default behaviour. Each scheduled task is a separate OS process
with its own memory, threads, DB connections, and logs. `EnergyAspects`
running at the same moment as `Platts` doesn't share anything except
the platform DB's load-log writes, which are concurrent-safe.

### Overlap protection (same loader scheduled twice)

If `EnergyAspects` is scheduled every 30 minutes and one run takes 45,
the next scheduler tick will fire while the previous run is still going.
Without protection, both processes would enumerate the same work units
and race against the load-log idempotency check.

The platform protects against this with a session-scoped SQL Server app
lock named `DataLoader:<LoaderId>`, acquired at the start of every loader
run by `core.usp_TryAcquireLoaderLock`:

- **Lock free** → loader runs as normal.
- **Lock held** → the new invocation logs a warning and exits cleanly with
  exit code 0. The next scheduler tick that finds the lock free will run.
- **Holder crashes** → SQL Server auto-releases the lock as soon as the
  dead session disconnects. The next scheduled invocation proceeds.

This means you can over-schedule safely. If a loader is set to run "every
15 minutes" but usually takes 25, most invocations will no-op quickly.

The lock is keyed by `LoaderId`, so it never interferes with a different
loader's invocation.

---

## Compile, deploy, run

### Compile

```
cd <repo>
dotnet build DataLoaderPlatform.sln -c Release
```

Output is at `src/DataLoader.Host/bin/Release/net8.0/`. To produce a clean
deployment directory:

```
dotnet publish src/DataLoader.Host/DataLoader.Host.csproj -c Release -o publish
```

`publish/` contains everything needed to run on the target machine:
`DataLoader.Host.exe`, every loader DLL referenced by the host, every
third-party dependency, and `appsettings.json`.

### Deploy

Copy the `publish/` directory contents to the target machine. For Windows:

```
C:\DataLoaderPlatform\
├── DataLoader.Host.exe
├── DataLoader.Core.dll
├── DataLoader.EnergyAspects.dll
├── DataLoader.Vulcan.dll
├── DataLoader.Platts.dll
├── DataLoader.StormVista.dll
├── DataLoader.CWG.dll
├── appsettings.json
└── (transitive DLLs)
```

Before the first run:

1. **Create the platform database.** Run these in order against the server
   you'll point `Platform:LoadLogConnectionString` at:
   ```
   sql/Core/001_CreateCoreSchema.sql
   sql/Core/002_CreateCoreProcedures.sql
   sql/Core/003_CreateOverlapGuard.sql
   sql/Core/004_CreateParamStore.sql
   ```
   (`004` adds `core.Param` + `core.usp_GetParam`, the key/value store behind the
   `SEE_DB` config indirection — see "Storing config in the database" below.)
2. **Create each loader's database.** For every loader you plan to enable,
   run its scripts against the database named in
   `Loaders:<LoaderId>:ConnectionString`:
   ```
   sql/EnergyAspects/001_…sql, 002_…sql, 003_…sql
   sql/Vulcan/001_…sql, 002_…sql, 003_…sql
   sql/Platts/001_…sql, 002_…sql, 003_…sql
   sql/StormVista/001_…sql, 002_…sql, 003_…sql
   sql/CWG/001_…sql, 002_…sql, 003_…sql
   ```
   `StormVista`'s scripts also contain a `CREATE DATABASE StormVista` guard,
   since (unlike the others) it isn't assumed to pre-exist.
3. **Edit `appsettings.json`** connection strings as needed. Sensitive fields
   (API keys, usernames, passwords) default to `SEE_DB` — supply them via
   `core.Param` (see "Storing config in the database" below) or env vars,
   rather than editing the file.
4. **Set secrets via environment variables** (preferred over editing the
   JSON for production):
   ```
   set DATALOADER_Loaders__EnergyAspects__ApiKey=actualkey
   set DATALOADER_Loaders__Platts__SftpPassword=actualpwd
   ```
   The double underscore is .NET's section separator. Variables override
   anything in `appsettings.json`.

### Run

**Ad-hoc, one loader:**
```
cd C:\DataLoaderPlatform
DataLoader.Host.exe EnergyAspects
```

**Ad-hoc, several loaders together (in one process):**
```
DataLoader.Host.exe EnergyAspects Vulcan
```

**Ad-hoc, every loader in `EnabledLoaders`:**
```
DataLoader.Host.exe
```

**Scheduled** — one Task Scheduler task per loader, as shown above.

Exit codes:
- `0` — success (or skipped because another instance was already running)
- `1` — error (unknown loader id, configuration problem, unhandled exception)
- `2` — cancelled (Ctrl+C, parent process kill)

---

## Adding a new loader

To add a new vendor `Foo`:

1. **Create the project.** `src/DataLoader.Foo/DataLoader.Foo.csproj`,
   referencing `DataLoader.Core`.

2. **Settings.** `FooSettings : LoaderSettingsBase` with vendor-specific
   fields.

3. **Work unit type.** Subclass `WorkUnit` and define a stable `Key`.

4. **Source reader.** Implement `ISourceReader<FooWorkUnit, FooApiItem>`.
   Use `HttpJsonSourceReaderBase` for REST/JSON, an `IFileSystemDriver`
   wrapper for file-based sources.

5. **Transformer.** Implement `ITransformer<FooApiItem, FooRow>`, or
   register `IdentityTransformer<T>` if no transform is needed.

6. **Sink.** Subclass `SqlSinkBase<FooRow>`.

7. **Work-unit provider.** Implement `IWorkUnitProvider<FooWorkUnit>`.

8. **Module.** Implement `ILoaderModule` — `LoaderId`, `DisplayName`,
   `RegisterServices`, `RunAsync`.

9. **SQL.** Add `sql/Foo/*.sql` for the loader's own schema.

10. **Reference and enable.**
    - Add `<ProjectReference>` from `DataLoader.Host` to the new project.
    - Add a `"Foo": { … }` section under `Loaders` in `appsettings.json`.
    - Either add `"Foo"` to `Platform:EnabledLoaders`, or just rely on the
      command-line argument to invoke it: `DataLoader.Host.exe Foo`.
11. **Schedule.** Add one Task Scheduler entry pointing at the same
    `DataLoader.Host.exe` with `Foo` as the argument.

The host code, `DataLoader.Core`, and the deployment directory layout
never change. The new loader's DLL drops in alongside the others.

---

## How a run works

```
┌─ Program.cs ──────────────────────────────────────────────────────────┐
│  1. Build IConfiguration from appsettings.json + env vars              │
│  2. ModuleDiscovery.Discover() — find every ILoaderModule in           │
│                                  DataLoader.*.dll next to the host    │
│  3. Parse command-line args → list of loader ids to run                │
│  4. For each module: module.RegisterServices(services, config)         │
│  5. services.BuildServiceProvider()                                    │
│  6. PlatformHost.RunAsync()                                            │
└────────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌─ PlatformHost ────────────────────────────────────────────────────────┐
│  1. open core.LoaderRun row                                            │
│  2. for each enabled module, in parallel up to MaxConcurrentLoaders:   │
│       acquire core.usp_TryAcquireLoaderLock                            │
│       if lock not free → skip                                          │
│       else → module.RunAsync(services, context)                        │
│       release lock                                                     │
│  3. close core.LoaderRun row                                           │
└────────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼  (one of these per loader)
┌─ LoaderPipelineBase<TUnit,TItem,TRow> ────────────────────────────────┐
│  units = workUnitProvider.GetWorkUnitsAsync(context)                   │
│  for each unit, in parallel up to MaxConcurrentWorkUnits:              │
│      handle = loadLog.BeginAsync(loaderId, runId, unit.Key)            │
│      if handle is null → SKIP (already done — idempotent rerun)        │
│      else:                                                             │
│          items   = source.ReadAsync(unit)                              │
│          rows    = transformer.Transform(items, unit)                  │
│          written = sink.WriteAsync(rows)                               │
│          loadLog.CompleteSuccessAsync(handle, written)                 │
└────────────────────────────────────────────────────────────────────────┘
```

---

## Configuration reference

`Platform` section (read by the host):
- `EnabledLoaders` — list of loader ids run when no command-line argument is given
- `MaxConcurrentLoaders` — when running multiple loaders in one process, how many run at once
- `LoadLogConnectionString` — shared platform database
- `DefaultDaysBackStart`, `DefaultDaysBackEnd` — fallback date window for loaders that don't have their own

`Loaders:<LoaderId>` section (read by each loader):
- `ConnectionString` — that loader's own database
- `MaxConcurrentWorkUnits` — within one loader, how many work units run at once
- `RetryCount`, `RetryDelayMs` — transient-failure retry inside the loader
- `WorkUnitTimeoutSeconds` — per-work-unit timeout
- *(loader-specific fields)* — API keys, endpoints, FTP credentials, etc

Secrets should come from environment variables, not the JSON file:
```
DATALOADER_Loaders__EnergyAspects__ApiKey=…
DATALOADER_Loaders__Platts__SftpPassword=…
```

### Storing config in the database (`SEE_DB`)

Any loader string setting can be sourced from the platform database instead of
`appsettings.json` / env vars. Set the value to the sentinel `"SEE_DB"`, and at
startup the loader resolves it via `core.usp_GetParam(<LoaderId>, <SettingName>)`:

```jsonc
"Loaders": {
  "StormVista": { "ApiKey": "SEE_DB", … }   // ← resolved from core.Param at run time
}
```
```sql
INSERT INTO core.Param (LoaderName, ParamName, [Value])
VALUES ('StormVista', 'ApiKey', '<the real key>');
```

Resolution runs against `Platform:LoadLogConnectionString` (where `core.Param` lives),
only for loaders that actually run, and fails fast if a `SEE_DB` setting has no matching
`core.Param` row. Applies to any string setting — API keys, passwords, and even a
loader's own `ConnectionString`. The one value that **cannot** be `SEE_DB` is
`Platform:LoadLogConnectionString` itself (it's the connection used to resolve the rest).

---

## Key files to read

- `docs/ARCHITECTURE.md` — design rationale.
- `DataLoader.Core/Abstractions/` — every interface a plugin author needs.
- `DataLoader.Core/Pipeline/LoaderPipelineBase.cs` — the standard ETL loop.
- `DataLoader.EnergyAspects/EnergyAspectsModule.cs` — example REST/JSON loader.
- `DataLoader.Platts/PlattsModule.cs` — example SFTP loader with a change-signal (mtime) resume key.
- `DataLoader.StormVista/StormVistaModule.cs` — example HTTP+CSV hybrid loader with a two-zone resume key and a normalized, FileLog-hub database schema.
