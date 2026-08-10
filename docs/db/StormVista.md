# StormVista — database structure

As-built database design for the StormVista WDD loader (**normalization
redesign**). The scripts live in `sql/StormVista/` and run in numbered order
against this loader's own database (`Loaders:StormVista:ConnectionString`):

| Script | Contents |
|--------|----------|
| `001_CreateStormVistaSchema.sql` | `CREATE DATABASE StormVista` guard, `USE`, all lookup + dimension + hub + fact tables, two convenience views, and the reference-data seeds. |
| `002_CreateStormVistaTvpTypes.sql` | The two TVP types the sinks stream rows through. |
| `003_CreateStormVistaProcedures.sql` | The reference read proc, the FileLog hub upsert, the two bulk-merge upserts, and the post-load validation proc. |

Sources of truth: `docs/apis/StormVista.md` (verified API field reference) and
this redesign brief. All objects are now in schema **`dbo`** in the
**`StormVista`** database (the earlier `sv` schema is gone — drop the DB or the
old `sv.*` objects before re-running). Every script is idempotent (guarded
creates; seeds via non-deleting `MERGE`; procs/views via `CREATE OR ALTER`).

---

## What changed vs. the earlier design

- **Schema `sv` → `dbo`** for every object (tables, TVP types, procs, views).
- **`dbo.FileLog` is now the hub.** One row per downloaded file/request, carrying
  FK IDs to every dimension (`Endpoint/Model/Cycle/WddType/RegionSet/Status`) +
  `InitDate` + audit columns. Its old denormalized strings (Feed/Model/Cycle/
  WddType/RegionSetCode/Status) are gone.
- **Facts hang off FileLog.** `dbo.DailyWdd` and `dbo.RegionalWdd` each carry
  `FileLogId` (provenance) + only their own leaf attributes. They reach
  model/cycle/type/init-date/endpoint **through FileLog** — those dimensions are
  **not** repeated on the fact rows, and there are no raw dimension strings.
- **Three new lookups:** `dbo.Endpoint` (Daily/Regional), `dbo.Status`
  (Success/NotAvailable/Failed), `dbo.Flag` (0=obs/1=fcst/2=norm). `DailyWdd`
  references `FlagId` instead of an INT `Flag`.
- **PERSISTED `InitDatetimeUtc` computed columns dropped** from both facts
  (`InitDate` now lives on FileLog). The datetime is instead exposed by the two
  convenience views.

---

## Table shape convention

Every table begins with the standing two columns:

```
Id          INT      IDENTITY(1,1) NOT NULL  -- PK_<table>
DateCreated DATETIME NOT NULL DEFAULT GETDATE()
```

Business/natural keys are enforced with a named `UNIQUE` constraint; the
idempotent `MERGE` procs join on that key. Dimension and fact tables carry a
`ModifiedAtUtc DATETIME2(3)` audit column (bumped by every `MERGE`/seed update).

---

## Lookup tables (seeded, resolved server-side by the procs)

### `dbo.Endpoint` — 2 rows
`Id`, `DateCreated`, `Name VARCHAR(20)` UNIQUE, CHECK ∈ {`Daily`,`Regional`}.
Replaces FileLog's old `Feed` string. Seeds: `Daily`, `Regional`.

### `dbo.Status` — 3 rows
`Id`, `DateCreated`, `Label VARCHAR(20)` UNIQUE, CHECK ∈
{`Success`,`NotAvailable`,`Failed`}. Seeds: those three.

### `dbo.Flag` — 3 rows
`Id`, `DateCreated`, `FlagCode INT` UNIQUE CHECK ∈ {0,1,2}, `Label VARCHAR(10)`
UNIQUE. Seeds: `(0,'obs')`, `(1,'fcst')`, `(2,'norm')`.

---

## Dimension tables (seeded; kept from earlier, moved `sv`→`dbo`, otherwise unchanged)

### `dbo.Model` — 23 rows
Natural key `ModelSlug VARCHAR(40)`. `DisplayName NVARCHAR(100) NULL`,
`SupportsDaily BIT`, `SupportsRegional BIT`, `IsExperimental BIT DEFAULT 0`. 17
daily models (`1/1` — they also serve regional WDD) + 6 weekly regional-only
models (`0/1`). All 23 support regional; the daily set additionally supports
daily, so daily and regional sets overlap (not disjoint). AI/MLR flagged
experimental.

### `dbo.Cycle` — 4 rows
Natural key `CycleCode CHAR(2)` ∈ {`00`,`06`,`12`,`18`} (CHECK). `SupportsDaily`/
`SupportsRegional`: `00`→1/1, `06`→1/0, `12`→1/1, `18`→1/0.

### `dbo.WddType` — 4 rows
Natural key `TypeSlug VARCHAR(10)` ∈ {`ew_cdd`,`gw_hdd`,`pw_cdd`,`pw_hdd`} (CHECK).
`Weighting` ∈ {energy,gas,population}, `Metric CHAR(3)` ∈ {CDD,HDD}.

### `dbo.RegionSet` — 4 rows
Natural key `RegionSetCode VARCHAR(4)` ∈ {`3`,`5`,`9`,`iso`} (CHECK, string
because `iso`). `Kind` ∈ {EIA,ISO}, `Description`.

### `dbo.RegionSetWddType` — 11 rows (bridge)
Natural key `(RegionSetCode, TypeSlug)`, both FKs to the dimension UNIQUE keys.
EIA `{3,5,9}×{ew_cdd,gw_hdd,pw_cdd}` (9) + `iso×{pw_cdd,pw_hdd}` (2).

### `dbo.Region` — 38 rows
Natural key `(RegionSetCode, RegionName VARCHAR(30))` (names scoped to their
set). `Ordinal INT` (unique within a set) preserves the wide-CSV column order.
FK to `dbo.RegionSet`. Counts: `3`→3, `5`→5, `9`→9, `iso`→21.

---

## Hub table — `dbo.FileLog`

One row per downloaded file/request (ALL outcomes: Success / NotAvailable(404) /
Failed). Columns after the standing two:

| Column | Type | Notes |
|--------|------|-------|
| `EndpointId` | `INT NOT NULL` | FK → `dbo.Endpoint` |
| `ModelId` | `INT NOT NULL` | FK → `dbo.Model` |
| `CycleId` | `INT NOT NULL` | FK → `dbo.Cycle` |
| `WddTypeId` | `INT NOT NULL` | FK → `dbo.WddType` |
| `RegionSetId` | `INT NULL` | FK → `dbo.RegionSet`; **NULL for daily** |
| `InitDate` | `DATE NOT NULL` | model init date |
| `StatusId` | `INT NOT NULL` | FK → `dbo.Status` |
| `HttpStatus` | `INT NULL` | raw HTTP status |
| `[RowCount]` | `INT NOT NULL DEFAULT 0` | rows loaded for the file |
| `RequestPath` | `NVARCHAR(400) NOT NULL` | sanitized path, **no apikey** |
| `LastCheckedUtc` | `DATETIME2(3) NULL` | stamped server-side by the upsert |
| `ModifiedAtUtc` | `DATETIME2(3) NULL` | stamped server-side by the upsert |

**Natural key** `UQ_FileLog_File (EndpointId, ModelId, CycleId, WddTypeId,
RegionSetId, InitDate)` — one row per file. `RegionSetId` is NULL for daily, and
SQL Server treats NULL as **equal** for a `UNIQUE` constraint, so daily rows
collapse to one row per `(Endpoint, Model, Cycle, WddType, InitDate)`; regional
rows differ on the non-null `RegionSetId`. (The upsert `MERGE` matches this key
NULL-safely via an `ISNULL(...,-1)` sentinel — see procs.)

---

## Fact tables

Both upsert via idempotent `MERGE` on the `UNIQUE` natural key (`FileLogId` +
leaf key).

### `dbo.DailyWdd`
`FileLogId INT NOT NULL` (FK → `dbo.FileLog`), `ValidDate DATE NOT NULL`,
`FlagId INT NOT NULL` (FK → `dbo.Flag`), `Value DECIMAL(9,4) NULL`,
`ModifiedAtUtc`. Natural key `UQ_DailyWdd_NaturalKey (FileLogId, ValidDate)`.

### `dbo.RegionalWdd`
`FileLogId INT NOT NULL` (FK → `dbo.FileLog`), `RegionId INT NOT NULL`
(FK → `dbo.Region`), `ValidDate DATE NOT NULL`, `Value DECIMAL(9,4) NULL`,
`ModifiedAtUtc`. Natural key `UQ_RegionalWdd_NaturalKey (FileLogId, RegionId,
ValidDate)`. No Flag (forecast-only). Region is scoped to its set — the regional
merge proc resolves `RegionId` from the parent FileLog's `RegionSet` + the CSV
`RegionName`.

> **"Index on FileLogId"** (both facts): satisfied by the leading column of each
> `UQ_*_NaturalKey` composite index (`FileLogId, …`). A separate single-column
> index would be strictly redundant and is deliberately **not** created — see
> Decisions #3.

---

## Convenience views (`001_…`)

Read-only, `CREATE OR ALTER`, rebuild the denormalized shape and expose
`InitDatetimeUtc = InitDate + Cycle hours` (replacing the dropped PERSISTED
computed columns) by joining FileLog + dimensions:

- `dbo.vw_DailyWdd` — `Model, InitDate, Cycle, WddType, ValidDate, Flag,
  FlagLabel, Value, InitDatetimeUtc, …`
- `dbo.vw_RegionalWdd` — `WkModel, InitDate, Cycle, WddType, RegionSetCode,
  RegionName, Ordinal, ValidDate, Value, InitDatetimeUtc, …`

---

## TVP types (`002_…`)

Column order is the **C# sink contract** (must match the sink's DataTable). Both
now carry `FileLogId` (from `usp_UpsertFileLog`) instead of the raw dimension
strings. No `Id`/`DateCreated`/`ModifiedAtUtc`; no PK (the merge proc de-dups).

| TVP | Columns (in order) |
|-----|--------------------|
| `dbo.DailyWddTvp` | `FileLogId INT`, `ValidDate DATE`, `FlagCode INT`, `Value DECIMAL(9,4) NULL` |
| `dbo.RegionalWddTvp` | `FileLogId INT`, `RegionName VARCHAR(30)`, `ValidDate DATE`, `Value DECIMAL(9,4) NULL` |

---

## Procedures (`003_…`)

### Load order the procs imply (per request/file)
1. `dbo.usp_UpsertFileLog(...)` → returns `FileLogId` (called for **every**
   outcome).
2. On Success, stamp that `FileLogId` onto every TVP row and call the matching
   bulk-merge proc.

### `dbo.usp_GetReference` (no params)
Six result sets in fixed order (consumed positionally, `NextResult`):

| # | Result set | Columns (in order) | Source | `ORDER BY` |
|---|------------|--------------------|--------|------------|
| 1 | Models | `ModelSlug, DisplayName, SupportsDaily, SupportsRegional, IsExperimental` | `dbo.Model` | `ModelSlug` |
| 2 | Cycles | `CycleCode, SupportsDaily, SupportsRegional` | `dbo.Cycle` | `CycleCode` |
| 3 | WddTypes | `TypeSlug, Weighting, Metric` | `dbo.WddType` | `TypeSlug` |
| 4 | RegionSets | `RegionSetCode, Kind, Description` | `dbo.RegionSet` | `RegionSetCode` |
| 5 | Bridge | `RegionSetCode, TypeSlug` | `dbo.RegionSetWddType` | `RegionSetCode, TypeSlug` |
| 6 | Regions | `RegionSetCode, RegionName, Ordinal` | `dbo.Region` | `RegionSetCode, Ordinal` |

Unchanged in shape from before (only `sv`→`dbo`). Endpoint/Status/Flag are not
returned — the loader speaks their string labels / numeric codes and the write
procs resolve them.

### `dbo.usp_UpsertFileLog`
```
@EndpointName VARCHAR(20), @ModelSlug VARCHAR(40), @CycleCode CHAR(2),
@WddTypeSlug VARCHAR(10), @RegionSetCode VARCHAR(4) = NULL, @InitDate DATE,
@StatusLabel VARCHAR(20), @RequestPath NVARCHAR(400), @HttpStatus INT = NULL,
@RowCount INT = 0
```
Validates required inputs; resolves each `*Id` by joining the lookups and
`RAISERROR`s (sev 16) if any required one fails to resolve (`@RegionSetCode` NULL
→ `RegionSetId` NULL, allowed for daily). `MERGE`s `dbo.FileLog` on the natural
key (NULL-safe on `RegionSetId` via `ISNULL(...,-1)`), setting
`StatusId/HttpStatus/RequestPath/[RowCount]/LastCheckedUtc/ModifiedAtUtc`.

**Returns the FileLogId** as a single-row, single-column result set named
`FileLogId`, captured via `MERGE … OUTPUT inserted.Id` (yields the Id for **both**
the inserted and the updated branch — robust for re-runs, unlike
`SCOPE_IDENTITY()` which is NULL on the update path). **The C# reads it with
`ExecuteScalar`.**

### `dbo.usp_BulkMergeDailyWdd @Records dbo.DailyWddTvp READONLY`
De-dups the batch on `(FileLogId, ValidDate)`; resolves `FlagId` from `FlagCode`
via `dbo.Flag` (RAISERROR if any code fails to resolve); `MERGE`s `dbo.DailyWdd`
on `(FileLogId, ValidDate)` updating `Value/FlagId/ModifiedAtUtc`; returns
`SELECT @@ROWCOUNT AS RecordsProcessed`.

### `dbo.usp_BulkMergeRegionalWdd @Records dbo.RegionalWddTvp READONLY`
De-dups on `(FileLogId, RegionName, ValidDate)`; resolves `RegionId` by joining
`dbo.Region` on `(parent FileLog's RegionSetCode, RegionName)` — the set is read
via `FileLog → RegionSet` for each row's `FileLogId`. If any `RegionName` fails
to resolve for its set (or the parent FileLog is missing/daily), that is a
data-integrity error → `RAISERROR` (loud, matching the loader's header posture:
**unknown/extra** columns are rejected — the loader throws on them before emit, so
the proc never sees an unknown region except as defense-in-depth. **Missing**
seeded regions are tolerated by the loader — region sets grow over time, e.g. ISO
18 → 21 — and simply produce no rows, so they never reach this proc).
`MERGE`s `dbo.RegionalWdd` on `(FileLogId, RegionId, ValidDate)`
updating `Value/ModifiedAtUtc`; returns `SELECT @@ROWCOUNT AS RecordsProcessed`.

### `dbo.usp_ValidateLoad @InitDateFrom DATE, @InitDateTo DATE`
Rewritten for the hub schema: every check joins fact → `dbo.FileLog` and filters
on `FileLog.InitDate`. One uniform result set `(CheckName, Feed, Scope,
ExpectedCount, ActualCount, Detail)`. Checks: row counts by model per feed (via
FileLog→Model), Value null counts (both feeds), regional region-coverage per file
vs expected per set, and orphan cross-checks (fact→FileLog, expected 0 — FK-
backed). Observational only.

---

## Seed counts (confirm on load)

| Table | Rows |
|-------|------|
| `dbo.Endpoint` | 2 |
| `dbo.Status` | 3 |
| `dbo.Flag` | 3 |
| `dbo.Model` | 23 |
| `dbo.Cycle` | 4 |
| `dbo.WddType` | 4 |
| `dbo.RegionSet` | 4 |
| `dbo.RegionSetWddType` | 11 |
| `dbo.Region` | 38 |

---

## Decisions for a reviewer to confirm

1. **Schema is `dbo` everywhere** (the standing default). The earlier `sv` schema
   is fully retired; there is no cross-schema compatibility shim — drop the DB (or
   the old `sv.*` objects) before re-running 001–003.

2. **FileLog natural key includes the nullable `RegionSetId`.** SQL Server's
   `UNIQUE` constraint treats NULLs as equal, so daily rows (NULL RegionSetId)
   correctly collapse to one row per `(Endpoint, Model, Cycle, WddType, InitDate)`.
   The `MERGE` join uses `ISNULL(RegionSetId, -1)` (a safe sentinel — Ids are
   positive IDENTITY) so the NULL match works there too.

3. **No standalone `IX_*_FileLogId` on the facts.** The `UQ_*_NaturalKey`
   composite indexes lead with `FileLogId`, which already serves every FileLogId
   lookup / FK-check / FileLog→facts join. A separate single-column index would be
   redundant (extra write + storage). Add one only if profiling shows a need.

4. **`usp_UpsertFileLog` returns `FileLogId` via `MERGE … OUTPUT inserted.Id`**
   as a one-row/one-column result set (`FileLogId`), read by `ExecuteScalar`.
   Chosen over `SCOPE_IDENTITY()` because the proc upserts (the update path has no
   new identity); OUTPUT returns the Id for both branches.

5. **Strict resolution in the write procs.** `usp_UpsertFileLog` RAISERRORs when a
   dimension label doesn't resolve; `usp_BulkMergeDailyWdd` RAISERRORs on an
   unknown `FlagCode`; `usp_BulkMergeRegionalWdd` RAISERRORs on an unresolved
   `RegionName`. These indicate a reference-seed gap (vendor added a
   model/type/region) and are meant to fail loudly, matching the loader's posture.

6. **Convenience views (optional).** `vw_DailyWdd` / `vw_RegionalWdd` re-expose the
   denormalized shape + `InitDatetimeUtc` that the dropped PERSISTED computed
   columns used to provide. Drop them if unwanted — nothing in the load path
   depends on them.

7. **`VARCHAR` for slugs/codes/labels/region names; `NVARCHAR` for free text**
   (`DisplayName`, `Description`, `RequestPath`). Deliberate, per the API doc's
   recommended types (ASCII enum identifiers).

8. **Lookup-table label domains pinned by CHECK** (`Endpoint.Name`,
   `Status.Label`, `Flag.FlagCode`) in addition to the UNIQUE constraints, so a
   bad seed/insert is rejected at the DB.
