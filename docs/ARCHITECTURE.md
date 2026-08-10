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
│   EnergyAspects       │ │   Ftp                 │ │   CsvExample         │
│                       │ │                       │ │                      │
│ - EnergyAspectsModule │ │ - FtpModule           │ │ - CsvExampleModule   │
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

The plugin boxes in the diagram are illustrative of the plugin *shape*, not the
current roster. The platform's loaders today are `DataLoader.EnergyAspects`
(REST/JSON), `DataLoader.Vulcan` (REST), `DataLoader.Platts` (SFTP), and
`DataLoader.StormVista` (HTTP+CSV) — see "What Each Project Contains" below for each.

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

A loader's own schema (e.g. `ea` for Energy Aspects, `arm` for Platts) is created by SQL scripts shipped with that loader's project. The platform does not know or care what tables exist there. `StormVista` deliberately uses the default `dbo` schema instead of a vendor prefix, and normalizes further than the others: its fact tables don't repeat dimension columns (model, cycle, date, type) — they carry a single `FileLogId` and reach those dimensions through a `dbo.FileLog` hub table (one row per downloaded file). This is a per-loader design choice, not a platform rule; other loaders are free to keep denormalized fact tables.

This lets two loaders share one physical database without colliding, or run against entirely separate databases — each loader has its own connection-string setting.

---

## Configuration

`appsettings.json` is structured by section, one section per loader plus a platform section:

```jsonc
{
  "Platform": {
    "EnabledLoaders": [ "EnergyAspects", "Vulcan" ],
    "MaxConcurrentLoaders": 2,
    "LoadLogConnectionString": "Server=…;Database=Platform;…"
  },
  "Loaders": {
    "EnergyAspects": { /* energy aspects-specific settings */ },
    "Vulcan":        { /* vulcan loader-specific settings */ },
    "Platts":        { /* platts loader-specific settings */ }
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

### `DataLoader.Vulcan`
Incremental REST loader against SynMax's `query_datalinks` endpoint — it POSTs a SQL query and paginates the JSON response. Five independent tables loaded via five closed pipelines in one module (`VulcanModule`), each resumed by its own persisted watermark rather than a fixed date window, so a rerun continues from wherever each table last left off.

### `DataLoader.Platts`
SFTP pull loader (SSH.NET) with two closed pipelines in one module: daily `.ftp` market-data files → `arm.SymbolData`, and reference CSVs → `arm.Symbol`. A work unit's `Key` embeds the SFTP file's `Size` and `LastModified`, so a file is reprocessed only when it actually changes on the server — idempotency driven by a real change signal from the source, rather than a date window. `arm.FileLog` audits every file's outcome.

### `DataLoader.StormVista`
An HTTP source that returns CSV bodies (not JSON, not files) — a hybrid between the REST and file-based patterns above. One module, two closed pipelines (Daily national / Regional weekly) behind an `EnabledFeeds` toggle. The API gives no per-file change signal to key on (no mtime, no reliable ETag), so instead of Platts' approach it uses a **two-zone resume key**: an init date older than a configurable age (`SettledAfterDays`) gets a *stable* key — skipped forever once loaded, which makes a multi-year backfill cheap to resume — while a recent/"hot" init date gets a key that *varies every run*, so it's always re-pulled to catch newly published cycles. Because that backfill can span tens of thousands of work units, `StormVista` implements a custom windowed `ILoaderPipeline` (allowed per `CLAUDE.md`) that chunks the date range and processes one window at a time — each window still delegates to a real `LoaderPipelineBase` internally, so the vetted per-unit loop (skip/retry/timeout logic) is reused rather than duplicated. Its database is fully normalized (see Database Layout above): `dbo.FileLog` is a hub table that every fact row references by `FileLogId`.
