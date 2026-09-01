# Argus loader — design

Companion to `docs/apis/Argus.md` (the verified source contract). This document
covers the loader's structure, keys, merge semantics and failure behaviour.

Database `Argus`, schema `dlp`. Loader id `Argus`.

---

## 1. Shape

16 feeds, one work unit per **file**:

```
DOCUMENTATION/latest*.csv   15 feeds, 1 file each   ->  15 reference tables
DCRDEUS/<yyyyMMdd><sfx>.csv  1 feed, N files (~20)  ->  TimeSeriesDetail (+History)
```

Two pipelines over the platform's standard `LoaderPipelineBase` loop:

| Pipeline | Work units | Provider |
|---|---|---|
| `Documentation` | one per enabled reference file present on the server | `ArgusDocumentationWorkUnitProvider` |
| `TimeSeries` | one per dated file in `DCRDEUS` | `ArgusTimeSeriesWorkUnitProvider` |

Both pipelines are **independent**: no barrier, no reference provider, no FK, no
shared state. Either can run alone, in any order, and a failure in one must not
stop the other. This is the ModernCommodities posture, not AGSI's coupled one.

### 1.1 Why one descriptor-driven reader instead of 16 row classes

Every feed does the same thing: download a CSV, map header names to TVP columns,
convert 6 scalar types, hand a `DataTable` to a merge proc. Writing that 16 times
would mean 16 `BuildTable` methods, each a separate chance for the
DataTable/TVP position drift that `tvp-contract-check` exists to catch.

Instead there is **one** description of each feed's columns
(`ArgusDescriptors.cs`), and everything else is generated from it:

```
ArgusColumn(TvpName, SourceHeader, Type, Required)
        |
        +--> the reader knows which header to read and how to convert it
        +--> the sink's DataTable column order IS the descriptor order
        +--> the unit test parses sql/Argus/002 and asserts the TVP matches
```

The last arrow is the important one. `ArgusTvpContractTests` reads the actual
`.sql` file, extracts each `CREATE TYPE ... AS TABLE` body, and asserts
**name + order + type** against the descriptor. The TVP contract is therefore
checked by the build, not by review alone.

### 1.2 Row type

`ArgusRow` is a thin wrapper over `object?[] Values`, positionally aligned to the
descriptor's column list. It is not a per-feed POCO. The trade is deliberate:
compile-time field names are lost, but the DataTable can never disagree with the
descriptor, and 16 near-identical classes are avoided.

---

## 2. Tables

Schema `dlp` in database `Argus`.

### 2.1 As specified by the requester (verbatim)

`CategoryLookup`, `CodeLookup`, `ModuleDetailLookup`, `ModuleLookup`,
`PriceTypeLookup`, `QuoteLookup`, `TimestampTypeLookup`, `TimeSeriesDetail`,
`TimeSeriesDetailHistory` — column names, types, nullability, PKs, default
constraint names and the four `TimeSeriesDetail` indexes are reproduced exactly
as given, with one approved addition:

- `dlp.CodeLookup.Specification varchar(50) NULL` — `latestCodes.csv` publishes a
  6th column that the original DDL omitted; without it the value is discarded.

Two things in the supplied DDL were kept deliberately even though they are
slightly odd, because changing them silently would be worse than flagging them:

1. `ModifiedAtUtc` defaults to `sysdatetime()` (server **local** time) despite the
   `Utc` suffix. The DEFAULT is kept verbatim; the merge procs set the column
   explicitly with `SYSUTCDATETIME()`, so every row the loader writes holds true
   UTC. The DEFAULT only applies to rows inserted by something other than this
   loader. **Worth changing to `sysutcdatetime()` if you agree.**
2. `dlp.TimeSeriesDetail.RecordStatus` is nullable while
   `dlp.TimeSeriesDetailHistory.RecordStatus` is a NOT NULL PK component. The
   source always populates it, so this never bites; it is left as specified.

### 2.2 Added by this loader

| Table | Source file | PK |
|---|---|---|
| `dlp.TimingLookup` | `latestTiming.csv` | (TimingID) |
| `dlp.UnitLookup` | `latestUnits.csv` | (UnitID) |
| `dlp.UnitCodeConversion` | `latestUnitCodeConv.csv` | (UnitID, BaseUnitID, CodeID, ValidFrom) |
| `dlp.HolidayRegionLookup` | `latestHolidayRegion.csv` | (HolidayRegionID) |
| `dlp.Holiday` | `latestHoliday.csv` | (HolidayRegionID, HolidayDate) |
| `dlp.QuoteHolidayRegion` | `latestQuoteHolidayRegion.csv` | (Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID) |
| `dlp.NewsCategoryLookup` | `latestNewsCategory.csv` | (CategoryType, CategoryID) |
| `dlp.RvpCodeReference` | `latestRVP_Code_reference.csv` | (CodeID, RvpCodeID) |

They follow the requester's pattern exactly: source columns, `ModifiedAtUtc
datetime2(3) NULL DEFAULT (sysdatetime())`, clustered PK named `PK_DLP_<Table>`,
default constraint named `DF_<Table>_ModifiedAtUtc`.

Sizing comes from measured maxima (API doc §4), with headroom. Two are not
negotiable:

- `NewsCategoryLookup.CategoryID` / `ParentID` are **`BIGINT`** — the live data
  exceeds `INT` (10 000 004 936).
- `UnitCodeConversion.Ratio` is **`DECIMAL(28,17)`** — 17 decimal places observed.

### 2.3 Audit hub

`dlp.Status` (`Success` / `NotAvailable` / `Failed`) and `dlp.FileLog`, one row per
source file keyed on `FileName`, written on **every** outcome including failure, so
a file that could not be downloaded or parsed leaves an auditable record rather
than vanishing. `RequestPath` stores `ftp://host:port/path` and **never**
credentials.

The fact tables carry no `FileLogId` — the supplied DDL does not have one, and it
was not added. Provenance is `dlp.TimeSeriesDetail.SourcePath`, which contains the
file name and therefore joins to `dlp.FileLog`.

---

## 3. Extraction

### 3.1 Work units and the resume key

```
argus:<folder>:<fileName>:<lastModifiedUtc:yyyyMMddHHmmss>:<size>
```

A file whose content Argus republishes gets a new server timestamp, hence a new
key, hence is reprocessed. An unchanged file is skipped by `core.LoadLog` however
often the loader runs.

**On "scrape all the date files on every run":** every run *enumerates* every dated
file. Files whose bytes have not changed are then skipped. Because the merge of
identical content is a no-op, the database ends in exactly the same state either
way — the skip only avoids wasted work. `ForceReprocess: true` appends the run date
to the key and disables the skip if an unconditional re-merge is ever wanted.

### 3.2 File selection

- DOCUMENTATION: an explicit `EnabledFeeds` list of the 15 feed ids. A feed whose
  file is **missing from the listing** is recorded `NotAvailable` in `dlp.FileLog`
  and skipped — it does not fail the run.
- DCRDEUS: `^(\d{8})([A-Za-z][A-Za-z0-9]*)\.csv$`. This single regex is what
  excludes `latestdhc.csv`, `previousdhc.csv` and `7667.csv` (API doc §2), while
  still picking up a new module suffix automatically.

### 3.3 Derived columns (DCRDEUS only)

| Column | Rule |
|---|---|
| `Module` | **`UPPER(suffix)`, always** — `dhc` -> `DHC`. See below. |
| `RecordStatusDate` | the file-name date. There is no such column in the feed; this records when Argus published that status. |
| `SourcePath` | the remote path, e.g. `/DCRDEUS/20260827dhc.csv`. |
| `SourceFileDate` | the file-name date. **TVP-only** — the merge ordering guard, not a table column. |

**On `Module` being unconditionally uppercased.** The requester's instruction is that
`Module` is always capitalised when stored, and the loader does exactly that in one
place (`ArgusTimeSeriesWorkUnitProvider.ResolveModule`), so no other case can reach
the database. There is no suffix-to-module lookup table and no configuration knob.

The trade-off was raised before the decision and is recorded here so nobody
rediscovers it as a bug: the strict authority is `latestModules.FileName`, which
differs from `LOWER(Module)` for **124 of the 200 modules** (`DAMCOAL`/`dcm`).
Uppercasing is exact for `dhc` and `dhca` — the only two suffixes DCRDEUS
publishes — but a future third file type could yield a `Module` that does not exist
in `dlp.ModuleLookup`. That is why `usp_ValidateLoad` keeps the
`FactModulesNotInModuleLookup` check (§5): it turns the silent version of that
failure into a reported one.

Note also that the reference tables keep their source casing: `dlp.ModuleLookup` and
`dlp.ModuleDetailLookup` store `Module` exactly as Argus publishes it, mixed case
included (`DABaM`, `DAmAsph`). Uppercasing those would corrupt real source values
and would not make a mismatched suffix join anyway — and SQL Server's default
collation is case-insensitive, so joins are unaffected either way.

`RecordStatusDate` and `SourceFileDate` hold the same value today. They are kept
separate because one is *data* the requester asked for and the other is *mechanism*;
collapsing them would couple the ordering guard to a column whose meaning could
later be redefined.

### 3.4 Parsing

`ArgusCsv` is RFC 4180-aware (quoted fields, `""` escapes, embedded commas and
newlines), then trims. Behaviour per row:

| Situation | Outcome |
|---|---|
| Header missing a column the descriptor needs | **Fail the whole file.** Contract drift must be loud — the TVP binds by position, so guessing would corrupt rows. |
| Field count differs from the header | Drop the row, count it, log the first reason. |
| `Required` column blank or unparseable | Drop the row (it cannot be keyed), count it. |
| Optional column blank or unparseable | Store `NULL`, count it as degraded. |
| File parses to zero rows | Log a warning; for a **replace** feed this is also a hard stop (§4.2). |

One malformed line never costs the rest of the file.

---

## 4. Loading

### 4.1 Standard merge (14 reference feeds + both fact tables)

Each feed has its own TVP and its own `dlp.usp_BulkMerge<Feed>` proc. Every merge:

1. **De-duplicates the batch** — `ROW_NUMBER() OVER (PARTITION BY <pk> ORDER BY ...)`,
   keep row 1. Required because SQL `MERGE` errors on a duplicated source key, and
   `latestHoliday.csv` genuinely contains 11 duplicates.
2. `WHEN MATCHED THEN UPDATE` all non-key columns + `ModifiedAtUtc = SYSUTCDATETIME()`.
3. `WHEN NOT MATCHED BY TARGET THEN INSERT`.
4. Returns `SELECT @@ROWCOUNT AS RecordsProcessed` for `core.LoadLog`.

There is deliberately **no `WHEN NOT MATCHED BY SOURCE THEN DELETE`** on the
reference tables: each work unit merges one snapshot, and a delete-by-absence would
be correct only if the snapshot were guaranteed complete. A short download would
otherwise wipe good rows.

### 4.2 Full replace (`QuoteLookup` only)

`dlp.usp_ReplaceQuoteLookup` runs `DELETE` + `INSERT ... SELECT` in one transaction,
so the table mirrors the snapshot exactly, including removals.

This is the only destructive path in the loader, so it is guarded:

| Guard | Behaviour |
|---|---|
| Empty TVP | `RAISERROR` and abort — never wipe the table with nothing to put back. (`SqlSinkBase` also short-circuits on 0 rows, so the proc guard is belt-and-braces.) |
| Incoming count < `@MinRowFraction` (default `0.50`) of the stored count | `RAISERROR` and abort. Catches a truncated download, which the row-level parser cannot detect — a half-transferred CSV yields well-formed rows, just fewer of them. |

`DELETE` rather than `TRUNCATE` so the loader needs only `db_datareader` /
`db_datawriter` / `EXECUTE`, not `ALTER`. At 150 k rows the cost is immaterial. If
the deployment grants `ALTER`, swapping in `TRUNCATE TABLE` is a one-line change.

### 4.3 The fact merge and its ordering guard

`dlp.usp_BulkMergeTimeSeriesDetail` takes one file's rows and writes **both** fact
tables in one transaction, so current and history can never drift apart:

```
dlp.TimeSeriesDetailHistory  PK (Module, Code, TimestampTypeID, PriceTypeID, ContFwd, Date, RecordStatus)
dlp.TimeSeriesDetail         PK (Module, Code, TimestampTypeID, PriceTypeID, ContFwd, Date)
```

An `N` row and a later `C` correction of the same quote produce **two** history rows
and **one** current row.

**The guard.** API doc §5.5 establishes that the same PK arrives from more than one
file in a single run, and that `ParallelRunner` may complete those files in any
order. Every UPDATE branch is therefore guarded by

```sql
WHEN MATCHED AND src.SourceFileDate >= ISNULL(tgt.RecordStatusDate, '19000101')
```

so an older file can never overwrite a newer one and the result is independent of
arrival order and of how often the loader re-runs. `tgt.RecordStatusDate` is the
stored file date, which is what makes this work without adding a column to the
requester's DDL.

**Batch de-dup tie-break.** Within one file the key is unique (verified), so the
`ROW_NUMBER` is defensive. Its ordering is
`SourceFileDate DESC, CASE RecordStatus WHEN 'C' THEN 0 ELSE 1 END` — if a
correction and an original ever share a file, the correction wins.

### 4.4 Concurrency

`SqlSinkBase` acquires `SqlWriteGate` keyed on (connection string + proc name).
Distinct procs therefore do not serialize against each other, while two work units
targeting the same proc do. `dlp.usp_UpsertFileLog` has its own key, so hub upserts
never deadlock against a fact `MERGE`.

---

## 5. Validation

`dlp.usp_ValidateLoad` is **observational** — one result set in the repo's uniform
`(CheckName, Scope, ExpectedCount, ActualCount, Detail)` shape.
`ArgusLoadValidator` logs it and swallows its own failures; it never changes a run's
outcome. Checks:

- Row counts per table; history count >= current count (true by construction).
- Every current row has a matching history row (expect 0 gaps).
- Keys whose history holds more than one `RecordStatus` — the revisions the history
  table exists to capture.
- Orphan checks against the lookups (`Code`, `Module`, `TimestampTypeID`,
  `PriceTypeID`) — the relationships no FK enforces (§2.3). Reported, never fatal:
  the fact feed legitimately runs before a lookup refresh.
- `FileLog` rows not `Success`, and `Success` rows whose `[RowCount]` disagrees with
  the rows actually stored.
- Untrimmed key values (expect 0 — the source quotes space-padded values).
- `QuoteLookup` row count, the table most exposed to a bad replace.

---

## 6. Configuration

`Loaders:Argus` in `appsettings.json`. `Username` / `Password` ship as `"SEE_DB"`
and resolve from `core.Param(LoaderName='Argus', ...)` via `AddLoaderSettings<T>`.

| Setting | Default | Note |
|---|---|---|
| `FtpHost` / `FtpPort` | `ftp.argusmedia.com` / `21` | plain FTP |
| `DocumentationDirectory` | `/DOCUMENTATION` | |
| `TimeSeriesDirectory` | `/DCRDEUS` | |
| `FileNameDatePattern` | `^(\d{8})([A-Za-z][A-Za-z0-9]*)\.csv$` | the exclusion mechanism (§3.2) |
| *(no module setting)* | — | `Module` is always `UPPER(suffix)`; §3.3 |
| `EnabledFeeds` | all 15 documentation feed ids | drop one to skip that file |
| `DaysBack` | `0` (all) | optional recency filter on dated files |
| `ForceReprocess` | `false` | §3.1 |
| `MaxConcurrentWorkUnits` | `4` | |
| `WorkUnitTimeoutSeconds` | `600` | the largest merge is 177 k rows |

The loader ships **disabled** — it is not in `Platform:EnabledLoaders`.

---

## 7. Status and known gaps

**Build-only.** The SQL has been parse-checked with ScriptDom but **never
deployed**; no row has been written to a real database. Do not report data
validation as passed.

Open items, all recorded rather than silently assumed:

1. `RecordStatusDate` is **inferred** to be the file-name date (approved). The feed
   has no such column.
2. The `Record Status = 'C'` correction path is implemented and unit-tested but has
   never been observed in a *dated* file — only in the excluded `7667.csv`.
3. `latestDoc.csv` (56 MB) and the `FWDNGIV` folder are out of scope by decision.
4. `QuoteLookup`'s verified natural key
   `(Code, ContinuousForwardPeriod, TimestampTypeID, PriceTypeID, StartDate)` is
   documented but **not enforced** — the table is loaded by replace and keeps the
   supplied DDL's "no PK, all nullable" shape.
5. `ModifiedAtUtc DEFAULT (sysdatetime())` is local time in a UTC-named column
   (§2.1).
