# Data Loader Platform — Architecture

## Goals
1. **One platform, many loaders.** A single host application runs any number of data loaders. Adding a new vendor means adding a new project (a plugin), not modifying the host.
2. **Loaders are independent.** Each loader owns its endpoints, its database schema, its config, its parsing logic, and its source type (REST API, FTP, CSV, SFTP, S3, etc.).
3. **The platform owns the cross-cutting concerns.** Logging, configuration, dependency injection, parallelism, retry policies, run scheduling, load-log bookkeeping, and the overall pipeline shape are all defined once in `DataLoader.Core`.
4. **EnergyAspects becomes a plugin.** The current Energy Aspects implementation is migrated into `DataLoader.EnergyAspects`, which references `DataLoader.Core` and implements its contracts.

---

## Layered View

```
┌────────────────────────────────────────────────────────────────────┐
│                    DataLoader.Host (executable)                     │
│  - Reads platform config (which loaders to run)                     │
│  - Discovers loader plugins                                         │
│  - Builds DI container, calls each loader's RegisterServices        │
│  - Resolves each ILoaderModule and runs RunAsync                    │
└────────────────────────────────────────────────────────────────────┘
                                  │
                                  │ depends on
                                  ▼
┌────────────────────────────────────────────────────────────────────┐
│                  DataLoader.Core (class library)                    │
│                                                                     │
│  Contracts:                                                         │
│  - ILoaderModule          (plugin entry point — Register + Run)     │
│  - ILoaderPipeline<TItem> (the Extract→Transform→Load pipeline)     │
│  - ISourceReader<TItem>   (Extract — REST, FTP, CSV, …)            │
│  - ISink<TItem>           (Load — SQL, Parquet, Kafka, …)          │
│  - ILoadLogRepository     (audit log of every run, per loader)      │
│                                                                     │
│  Base helpers:                                                      │
│  - LoaderPipelineBase<TItem>  (Extract+Transform+Load loop, retry,  │
│                                idempotency check, log writing)      │
│  - SqlSinkBase<TItem>         (TVP-based bulk merge helper)         │
│  - HttpSourceReaderBase       (HttpClient + Polly + JSON helpers)   │
│  - FileSourceReaderBase       (FTP / SFTP / local / S3 driver hook) │
│                                                                     │
│  Cross-cutting:                                                     │
│  - LoaderRunContext   (loader id, run id, dates, cancellation)      │
│  - LoaderSettingsBase (common settings every loader gets)           │
│  - ParallelRunner     (semaphore-bounded parallel runner)           │
│  - RetryPolicyFactory (Polly retry policies)                        │
└────────────────────────────────────────────────────────────────────┘
                                  ▲
              ┌───────────────────┼─────────────────────────┐
              │                   │                         │
┌───────────────────────┐ ┌───────────────────────┐ ┌──────────────────────┐
│ DataLoader.           │ │ DataLoader.           │ │ DataLoader.          │
│   EnergyAspects       │ │   FtpExample          │ │   CsvExample         │
│                       │ │                       │ │                      │
│ - EnergyAspectsModule │ │ - FtpExampleModule    │ │ - CsvExampleModule   │
│ - REST source reader  │ │ - FTP source reader   │ │ - Local CSV reader   │
│ - SQL sink            │ │ - CSV parser          │ │ - SQL sink           │
│ - Loader-specific     │ │ - SQL sink            │ │ - Loader-specific    │
│   settings & DTOs     │ │ - Loader-specific     │ │   settings & DTOs    │
│                       │ │   settings & DTOs     │ │                      │
│ Owns its OWN DB schema│ │ Owns its OWN DB schema│ │ Owns its OWN DB      │
│ Owns its OWN config   │ │ Owns its OWN config   │ │   schema & config    │
│   section             │ │   section             │ │                      │
└───────────────────────┘ └───────────────────────┘ └──────────────────────┘
```

---

## Plugin Contract: `ILoaderModule`

The single seam between host and loader.

```csharp
public interface ILoaderModule
{
    string LoaderId { get; }                            // e.g. "EnergyAspects"
    string DisplayName { get; }

    // Register everything the loader needs — settings, services, sources, sinks
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    // Run one pass of the loader
    Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context);
}
```

A loader assembly exposes exactly **one** `ILoaderModule`. The host enumerates them, calls `RegisterServices` on each (building the DI container in one pass), then resolves and invokes `RunAsync` on the enabled ones.

---

## Pipeline Contract: `ILoaderPipeline<TItem>`

Every loader, regardless of source type, ultimately does the same shape of work:

1. **List work units** — the things to load. For EnergyAspects, a work unit is one mapping × one date window. For a CSV loader, a work unit is one file in a drop directory. For an FTP feed, a work unit is one file on the remote server.
2. **For each work unit, in parallel up to a bounded concurrency:**
   - Check if it has already been loaded (idempotency).
   - Open a `LoadLog` row.
   - Call the source to read items.
   - Transform items into target rows.
   - Hand rows to the sink.
   - Close the `LoadLog` row (success or failure).

`LoaderPipelineBase<TItem>` implements that loop once. A loader plugin supplies:

- `ISourceReader<TItem>` — how to read items for one work unit
- `ITransformer<TItem, TRow>` — how to convert items into DB rows (optional; identity by default)
- `ISink<TRow>` — how to write rows
- `IWorkUnitProvider` — how to enumerate work units for a run

This means an FTP-CSV loader and an HTTP-JSON loader differ only in three small classes, not in the entire flow.

---

## Database Layout

Each loader owns its own schema. The platform reserves the `core` schema for the shared load-log table, which every loader writes to (with `LoaderId` as a discriminator).

- `core.LoaderRun`        — one row per host run (start, end, status)
- `core.LoadLog`          — one row per work unit (loader id, run id, key, status, records, error)

A loader's own schema (e.g. `ea` for Energy Aspects) is created by SQL scripts shipped with that loader's project. The platform does not know or care what tables exist in `ea`.

This lets two loaders share one physical database without colliding, or run against entirely separate databases — each loader has its own connection-string setting.

---

## Configuration

`appsettings.json` is structured by section, one section per loader plus a platform section:

```jsonc
{
  "Platform": {
    "EnabledLoaders": [ "EnergyAspects", "CsvExample" ],
    "MaxConcurrentLoaders": 2,
    "LoadLogConnectionString": "Server=…;Database=Platform;…"
  },
  "Loaders": {
    "EnergyAspects": { /* energy aspects-specific settings */ },
    "FtpExample":    { /* ftp loader-specific settings */ },
    "CsvExample":    { /* csv loader-specific settings */ }
  },
  "Logging": { … }
}
```

The platform reads only `Platform`. Each loader binds its own slice under `Loaders:<LoaderId>` inside its `RegisterServices` method. Loaders cannot see each other's configuration.

---

## Adding a New Loader — Concrete Steps

1. Create a new class library `DataLoader.MyVendor` referencing `DataLoader.Core`.
2. Add a class `MyVendorModule : ILoaderModule`.
3. In `RegisterServices`:
   - Bind `MyVendorSettings` from `Loaders:MyVendor`.
   - Register an `ISourceReader<MyVendorItem>` — REST, FTP, CSV, whatever.
   - Register an `ISink<MyVendorRow>` — usually `MyVendorSqlSink : SqlSinkBase<MyVendorRow>`.
   - Register an `IWorkUnitProvider` if work units aren't trivial.
   - Register `LoaderPipelineBase<MyVendorItem>` (or a custom pipeline).
4. In `RunAsync`, resolve the pipeline and call `ExecuteAsync(context)`.
5. Ship `sql/` scripts that create the loader's schema in its own database.
6. Add `"MyVendor"` to `Platform:EnabledLoaders` in `appsettings.json`.

The host code never changes.

---

## What Each Project Contains

### `DataLoader.Core`
Contracts (`ILoaderModule`, `ILoaderPipeline<T>`, `ISourceReader<T>`, `ISink<T>`, `ILoadLogRepository`), base classes (`LoaderPipelineBase`, `SqlSinkBase`, `HttpSourceReaderBase`, `FileSourceReaderBase`), models (`LoaderRunContext`, `LoaderRunResult`, `WorkUnit`, `LoadLogRecord`), helpers (`ParallelRunner`, `RetryPolicyFactory`).

### `DataLoader.Host`
`Program.cs` — entry point. Builds configuration, discovers `ILoaderModule` implementations from referenced assemblies, builds a single DI container with each module's registrations, then runs the enabled modules (optionally in parallel).

### `DataLoader.EnergyAspects`
`EnergyAspectsModule`, `EnergyAspectsSettings`, `EnergyAspectsApiSource` (refactored from the old `EnergyAspectsApiService`), `EnergyAspectsSqlSink` (refactored from the old `DataRepository`), `EnergyAspectsMappingProvider` (work-unit provider that lists mapping × date-window units), plus the existing models. All of the original Energy Aspects behavior is preserved.

### `DataLoader.FtpExample`
Stub plugin that demonstrates an FTP-driven loader. Pulls files from an FTP server, parses each as CSV, writes to its own SQL schema.

### `DataLoader.CsvExample`
Stub plugin that demonstrates a local-drop CSV loader. Watches a directory, parses each new file, writes to its own SQL schema.
