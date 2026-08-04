# Platts SFTP Loader — Design Spec

- **Date:** 2026-08-04
- **Loader id:** `Platts`
- **Status:** Approved-in-principle via brainstorming; pending written-spec review
- **Target DB:** new `Platts` database, all objects in schema **`arm`**

## 1. Goal

Add a plugin loader `DataLoader.Platts` that pulls two feeds off Platts' SFTP
server and lands them in a new `Platts` database (`arm` schema):

1. **SymbolData** — daily `.ftp` market files, one per `yyyymmdd` date folder.
2. **Symbol** — reference metadata CSVs under `symbols/csv-version/`, merged by symbol.

The host and `DataLoader.Core` do not change (plugin contract only).

## 2. Source system — SFTP

- Host `sftp://sftp.platts.com` — this is **SSH** SFTP, not FTP/FTPS. The existing
  `DataLoader.FtpExample` driver uses `FtpWebRequest` and **cannot** talk SSH SFTP.
- New dependency: **`SSH.NET` (Renci.SshNet)** NuGet package (MIT).
- Layout on the server:
  - Root contains one folder per date, named `yyyymmdd` (e.g. `20260731`). Each holds
    `*.ftp` market files (plain text despite the extension).
  - `symbols/csv-version/` contains reference CSVs (`AA_sym.csv`, `AC_sym.csv`, …).
- Credentials (host, port, username, password) live in config; password supplied via
  environment variable in production per platform convention.

### 2.1 SFTP driver

New loader-local `PlattsSftpFileSystem : IFileSystemDriver` reusing the platform's
[`IFileSystemDriver`/`RemoteFile`](../../../src/DataLoader.Core/Sources/IFileSystemDriver.cs)
(`RemoteFile` already carries `Size` and `LastModifiedUtc`). Adds one method the
platform interface lacks:

```csharp
public interface IPlattsSftp : IFileSystemDriver
{
    // yyyymmdd date folders under a root
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken ct);
}
```

- `ListDirectoriesAsync` — connect, list `path`, keep entries that are directories whose
  name matches `^\d{8}$` (configurable), return their paths.
- `ListAsync(path, pattern, ct)` — list files matching the glob → `RemoteFile` with
  `LastModifiedUtc = SftpFile.LastWriteTimeUtc`, `Size = SftpFile.Length`.
- `OpenReadAsync(fullPath, ct)` — download the file fully into a `MemoryStream`, return it
  positioned at 0 (so the connection can close immediately).
- **Thread-safety:** `SftpClient` is not safe for concurrent operations, so the driver
  opens a **short-lived `SftpClient` per operation** (connect → work → disconnect). Simple
  and safe under `MaxConcurrentWorkUnits > 1`.
- **Host key:** subscribe to `HostKeyReceived`, accept and log the fingerprint (no pinned
  known-hosts). See Assumptions §13.
- `MoveAsync` → `NotSupportedException` (not needed; files stay on the server).

## 3. Architecture — one module, two closed pipelines

Mirrors [`VulcanModule`](../../../src/DataLoader.Vulcan/VulcanModule.cs), which runs several
closed pipelines behind one `ILoaderModule`. Both Platts pipelines reuse the platform's
[`LoaderPipelineBase`](../../../src/DataLoader.Core/Pipeline/LoaderPipelineBase.cs); its
per-unit `core.LoadLog` idempotency **is** the "process only if changed" mechanism.

| Pipeline | Work unit | Source reader | Sink target | Merge key |
|---|---|---|---|---|
| **SymbolData** | one `.ftp` file | parse `.ftp` text | `arm.SymbolData` | (Symbol, Bate, Date, Action) |
| **Symbol** | one `*_sym.csv` | parse CSV | `arm.Symbol` | Symbol (PK) |

`PlattsModule.RunAsync` selects the pipelines named in `EnabledFeeds`, runs them
sequentially, and aggregates the `LoaderRunResult`s (Vulcan pattern). Because both use a
file-shaped work unit, the pipelines are **built explicitly** in `RegisterServices` (not via
interface-keyed `IWorkUnitProvider<T>`) to avoid DI collisions — again the Vulcan approach.

### 3.1 Reuse map

| Reused as-is | New for Platts |
|---|---|
| `LoaderPipelineBase`, `ParallelRunner`, `SqlLoadLogRepository`, `SqlLoaderOverlapGuard` | `PlattsSftpFileSystem` (SSH.NET) |
| `IFileSystemDriver` / `RemoteFile` | `PlattsSettings`, work units, readers, sinks, module |
| `SqlSinkBase<TRow>` (TVP bulk-merge) | `arm.*` SQL objects |
| `IdentityTransformer<T>` | `arm.FileLog` audit + writer |
| `LoaderSettingsBase`, config binding | — |

## 4. Configuration (`Loaders:Platts`)

`PlattsSettings : LoaderSettingsBase` (inherits `ConnectionString`,
`MaxConcurrentWorkUnits`, `RetryCount`, `RetryDelayMs`, `WorkUnitTimeoutSeconds`) plus:

```jsonc
"Platts": {
  "ConnectionString": "Server=ARMH-OPSDB01;Database=Platts;Integrated Security=SSPI;TrustServerCertificate=True;",
  "MaxConcurrentWorkUnits": 4,
  "RetryCount": 3,
  "RetryDelayMs": 1000,
  "WorkUnitTimeoutSeconds": 600,
  "SftpHost": "sftp.platts.com",
  "SftpPort": 22,
  "SftpUsername": "REPLACE",
  "SftpPassword": "REPLACE-VIA-ENV-VAR",   // DATALOADER_Loaders__Platts__SftpPassword
  "RootDirectory": "/",
  "DateFolderPattern": "^\\d{8}$",
  "MarketDataFilePattern": "*.ftp",
  "SymbolsDirectory": "symbols/csv-version",
  "SymbolFilePattern": "*.csv",
  "EnabledFeeds": [ "SymbolData", "Symbol" ]
}
```

Add `"Platts"` to `Platform:EnabledLoaders`.

## 5. Feed A — SymbolData (`.ftp`)

### 5.1 Discovery & idempotency

- List all date folders under `RootDirectory` (all folders every run) → list `*.ftp` in each
  → **one work unit per file**.
- Work-unit key embeds LastModified so an unchanged file is skipped and a changed one
  reprocesses:
  `Key = platts:sd:{folder}/{file}:{Size}:{LastModifiedUtc:O}`
  `DisplayName = {folder}/{file}` (stable, human-readable).
- Effect: `core.LoadLog.BeginAsync` returns null (skip) when this exact key already
  succeeded; a new LastModified → new key → reprocessed. This satisfies "process a file
  again only if its SFTP LastModified changed, otherwise skip."

### 5.2 File format & parsing

Read as text (UTF-8). Example:

```
© 2026 by S&P Global Inc.
PlattsMarketData 202607312341 10511 FINAL     GD  20260731
N AEWAA00c 202607310000 27.11
X ANTAE00u 202607310000
N ANTAL00w 202607300000 7500000
```

- **Line 1** (`© …`): skip.
- **Line 2 (header):** split on whitespace (`\s+`). With tokens `t[0..n-1]`:
  - `ActionDate = t[1]` → parse `yyyyMMddHHmm` → **DATETIME2** (constant per file).
  - `MDC = t[n-2]` (e.g. `GD`) — **constant per file**.
  - `t[n-1]` (e.g. `20260731`) is the file/business date → **used only to validate against the
    folder name (warn on mismatch); not stored** (redundant with folder / `SourcePath`).
- **Data lines (line 3+, non-empty):** split on whitespace (`RemoveEmptyEntries`) → 3 or 4
  tokens `[Action, SymbolBate, Date, Value?]`:
  - `Action = t[0]` (e.g. `N`, `X`).
  - `SymbolBate = t[1]`: **Bate = last character**, **Symbol = the rest**
    (`AEWAA00c` → Symbol `AEWAA00`, Bate `c`).
  - `Date = t[2]` → parse `yyyyMMddHHmm` → **DATETIME2** (per-row; this is the `Date` column).
  - `Value` = `t[3]` if present and non-blank → **decimal(38,10)**; blank/missing → **NULL**.
- Stamp `MDC` and `ActionDate` (from header) and `SourcePath = {folder}\{file}` onto every row.

### 5.3 Row model → `arm.SymbolData`

Columns (exactly the 8 requested plus an audit stamp):

| Column | Type | Source |
|---|---|---|
| `MDC` | nvarchar(10) NOT NULL | header |
| `Symbol` | nvarchar(20) NOT NULL | data line (split) |
| `Bate` | nchar(1) NOT NULL | data line (last char) |
| `Date` | datetime2(0) NOT NULL | data line col 3 |
| `Action` | nvarchar(5) NOT NULL | data line |
| `Value` | decimal(38,10) NULL | data line col 4 |
| `ActionDate` | datetime2(0) NOT NULL | header |
| `SourcePath` | nvarchar(500) NOT NULL | `{folder}\{file}` |
| `ModifiedAtUtc` | datetime2(3) NOT NULL default `SYSUTCDATETIME()` | audit |

- **PK / merge key: (Symbol, Bate, Date, Action).** (`MDC` is a payload attribute, not part of
  the key — see Assumptions §13 if that should change.)
- Sink **dedups the batch** by key (keep max `ActionDate`) before the merge, so a file with an
  accidental duplicate key can't break the TVP merge. TVP has no PK; the proc merges a
  de-duplicated source.
- On a matched key the row is updated (Value, MDC, ActionDate, SourcePath) when
  `src.ActionDate >= tgt.ActionDate` — **latest ActionDate wins**. Reprocessing a changed file
  is therefore idempotent (no duplicate rows).

## 6. Feed B — Symbol reference (`*_sym.csv`)

### 6.1 Discovery & idempotency

- List `SymbolFilePattern` files in `SymbolsDirectory` → one work unit per file.
- Same LastModified-in-key idempotency: `Key = platts:sym:{file}:{Size}:{LastModifiedUtc:O}`.

### 6.2 File format & parsing

- Comma-delimited, **has a header row**, **quote-aware** parsing (Description can contain
  commas — the naive `Split(',')` used by `CsvExample` is not sufficient here).
- **Full 14-column set** (per the CLAUDE.md "never a partial column set" rule), from the
  sample header `MDC, Trans, Symbol, Bates, Freq, Curr, UOM, DEC, Conv, */, To_UOM, Earliest,
  Latest, Description`.

### 6.3 Row model → `arm.Symbol`

| Column | Type | Notes |
|---|---|---|
| `MDC` | nvarchar(10) NULL | |
| `Trans` | nvarchar(50) NULL | |
| `Symbol` | nvarchar(20) NOT NULL | **PK** |
| `Bates` | nvarchar(20) NULL | applicable bate chars for the symbol |
| `Freq` | nvarchar(10) NULL | e.g. `DA` |
| `Curr` | nvarchar(10) NULL | e.g. `BRL` |
| `UOM` | nvarchar(20) NULL | e.g. `LTR` |
| `[DEC]` | int NULL | decimal places |
| `Conv` | decimal(38,10) NULL | |
| `Flag` | nvarchar(20) NULL | maps to the CSV `*/` column (semantics TBC — see §13) |
| `To_UOM` | nvarchar(20) NULL | |
| `Earliest` | date NULL | e.g. `4/1/2026` |
| `Latest` | date NULL | e.g. `6/25/2026` |
| `Description` | nvarchar(500) NULL | |
| `ModifiedAtUtc` | datetime2(3) NOT NULL default `SYSUTCDATETIME()` | audit |

- **PK / merge key: Symbol.** MERGE upserts every column by `Symbol`.
- Relationship: `arm.SymbolData.Symbol` (after stripping the bate) corresponds to
  `arm.Symbol.Symbol`. **No FK is enforced** (load order / completeness not guaranteed).

## 7. File audit log — `arm.FileLog` ("Both")

In addition to `core.LoadLog` (idempotency), each processed file writes an audit row for
in-DB reporting:

| Column | Type |
|---|---|
| `FileLogId` | bigint IDENTITY PK |
| `Feed` | nvarchar(20) NOT NULL (`SymbolData` \| `Symbol`) |
| `SourcePath` | nvarchar(500) NOT NULL |
| `FileName` | nvarchar(260) NOT NULL |
| `LastModifiedUtc` | datetime2(3) NOT NULL |
| `SizeBytes` | bigint NULL |
| `RowCount` | int NOT NULL |
| `Status` | nvarchar(20) NOT NULL |
| `ProcessedAtUtc` | datetime2(3) NOT NULL default `SYSUTCDATETIME()` |

- Unique index on `(Feed, SourcePath)`; `arm.usp_UpsertFileLog` merges by `(Feed, SourcePath)`
  (latest LastModified / RowCount / Status / ProcessedAt).
- Written by the sink after a successful data merge, from the batch's file metadata (every
  row carries `SourcePath`, `FileName`, `LastModifiedUtc`, `SizeBytes`) plus the merged row
  count. Files that are skipped as unchanged do **not** rewrite it.
- **Caveat:** a header-only `.ftp` file with zero data rows produces no `arm.FileLog` row
  (the sink's batch is empty); it is still recorded in `core.LoadLog`.
- `RunId` is intentionally **not** duplicated here — `core.LoadLog`/`core.LoaderRun` already
  tie every unit to its run.

## 8. Database objects — `sql/Platts/` (schema `arm`)

Following the [EnergyAspects SQL](../../../sql/EnergyAspects/003_CreateEnergyAspectsProcedures.sql)
conventions: idempotent `CREATE` guards, TVP types, MERGE procs that return
`SELECT @@ROWCOUNT AS RecordsProcessed` (so `SqlSinkBase.ProcedureReturnsRowCount = true`).

1. `001_CreatePlattsSchema.sql` — `CREATE SCHEMA arm`; tables `arm.SymbolData`, `arm.Symbol`,
   `arm.FileLog` + indexes.
2. `002_CreatePlattsTvpTypes.sql` — `arm.SymbolDataTvp`, `arm.SymbolTvp`.
3. `003_CreatePlattsProcedures.sql` — `arm.usp_BulkMergeSymbolData`,
   `arm.usp_BulkMergeSymbol`, `arm.usp_UpsertFileLog`.

## 9. Idempotency & reprocessing (summary)

- **Skip unchanged:** work-unit key includes `LastModifiedUtc`; `core.LoadLog` skips a key
  that already succeeded.
- **Reprocess changed:** new LastModified → new key → not-yet-succeeded → processed.
- **No duplicates on reprocess:** both targets MERGE by their key; SymbolData keeps latest
  `ActionDate`, Symbol overwrites by `Symbol`.
- **Overlap guard:** `SqlLoaderOverlapGuard` (`DataLoader:Platts`) already prevents concurrent
  host invocations — unchanged.

## 10. Error handling / concurrency / resilience

- One failed file fails only its own unit (pipeline isolates per-unit); `core.LoadLog` records
  the failure and it is retried next run (only success marks a key done).
- `WorkUnitTimeoutSeconds` bounds each file; `MaxConcurrentWorkUnits` bounds parallelism.
- SFTP transient errors (connect/`SshException`): a small connect-retry inside the driver;
  otherwise rely on cheap idempotent reruns rather than heavy in-run retry.
- SFTP `OperationTimeout` set from settings.

## 11. Wiring & host integration

- `src/DataLoader.Platts/DataLoader.Platts.csproj` → references `DataLoader.Core`, adds
  `SSH.NET`.
- `PlattsModule : ILoaderModule` (`Id = "Platts"`) — `RegisterServices` binds settings,
  registers `IPlattsSftp`, both sinks, the `arm.FileLog` writer, and builds the two closed
  pipelines; `RunAsync` runs the `EnabledFeeds` pipelines and aggregates.
- `DataLoader.Host` gets a `<ProjectReference>` to `DataLoader.Platts`.
- `appsettings.json`: add the `Loaders:Platts` block and `"Platts"` to
  `Platform:EnabledLoaders`.

## 12. Testing plan (CODE_TESTER)

- **Parsing:** header parse (ActionDate/MDC/file-date, extra middle tokens); symbol/bate
  split; per-row Date parse; blank & present Value; trailing-space lines → NULL value;
  copyright/blank-line skip.
- **CSV:** header handling; quote-aware fields incl. commas in Description; all 14 columns.
- **Idempotency:** same LastModified → skip; changed LastModified → reprocess (fake
  `ILoadLogRepository`).
- **SFTP driver:** exercised via a fake `IPlattsSftp`/`IFileSystemDriver` (no live server);
  date-folder filtering, glob matching, LastModified propagation.
- **Sinks:** TVP shape and merge semantics via SQL test doubles; `arm.FileLog` upsert; batch
  de-dup keeps latest `ActionDate`.
- Then `CODE_REVIEWER` → `CODER` (fixes) → `CODE_TESTER`, and `DATA_QUALITY_VALIDATOR` after a
  first real load.

## 13. Assumptions & open items

1. **Date column** = per-line 3rd value as `DATETIME2` (time is `0000` in samples); header
   `yyyyMMdd` value is dropped. *(Confirmed by user.)*
2. **MDC not in the SymbolData key** — assumed functionally determined by Symbol. If two MDCs
   can publish the same Symbol/Bate/Date/Action, promote the key to
   `(MDC, Symbol, Bate, Date, Action)`.
3. **Symbol CSV**: has a header row, comma-delimited, quote-aware; exact column order/names
   and the meaning of the `*/` column to be verified against a real file (mapped to `Flag`
   for now). Types `DEC`→int, `Earliest`/`Latest`→date, `Conv`→decimal.
4. **SFTP host key** accepted-and-logged (no pinned fingerprint). Can add a configurable
   fingerprint if required.
5. **All date folders listed every run.** If history grows large enough that listing/stat cost
   matters, revisit with a look-back window or a folder watermark (not needed now).
6. Files are **not** moved/deleted on the server after processing.

## 14. Out of scope

- Backfilling or transforming beyond the described columns.
- Downstream joins/views over `arm.SymbolData` × `arm.Symbol`.
- Changes to `DataLoader.Core` or the host beyond the project reference and config.

## 15. Implementation sequence (agents, per CLAUDE.md)

`MANAGER` coordinates: (skip API-documentation — no REST API) → `APPLICATION_DESIGNER`
(confirm flow) → `DATABASE_DEVELOPER` (`sql/Platts/*`) → `CODER` (C# loader) →
`CODE_REVIEWER` → `CODER` (fixes) → `CODE_TESTER` → `DATA_QUALITY_VALIDATOR` (after first load).
