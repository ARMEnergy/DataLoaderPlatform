# CME loader — design

Source contract: `docs/apis/CME.md`. Read that first — every decision here
follows from a fact established there.

Database **`CMEGroup`**, schema `arm`. Loader id `CME`. **Build-only**: disabled in
`Platform.EnabledLoaders`, and the SQL has never been deployed.

## 1. Shape

```
CmeSftpFileSystem.WalkAsync        one SSH session, 5 levels, ~120 listings
        │
CmeDiscoveryCache                  path -> (feed, trade date, file);  once per run
        │
CmeWorkUnitProvider                one unit per (feed x trade date) that EXISTS
        │
LoaderPipelineBase<CmeWorkUnit, CmeFactRow, CmeFactRow>
        │
CmeSourceReader                    download -> CmeBulletinParser -> arm.FileLog
        │
CmeFactSink                        partitions by kind -> TWO TVP merges
```

### 1.1 Why ONE pipeline, not one per feed

EOX, Argus and ICE give each feed its own pipeline because each feed has its own
table and column list. Here all 8 feeds share the same two tables, the same
parser and one directory walk, so a single pipeline over (feed × date) work units
is both simpler and cheaper. Feed isolation is preserved where it matters: a
failing bulletin fails only its own work unit and the rest of the run continues.

### 1.2 Why ONE work unit feeds TWO tables

A bulletin interleaves futures and options sections in a single 3–13 MB file, so
both tables are fed by one parse. Two pipelines would download and re-parse every
file twice (~40 MB and ~750k lines of duplicated work per trade date) and would
split one file's outcome across two `core.LoadLog` rows — so a half-loaded
bulletin would look fully loaded from either side. One unit, one parse, one log
row, two merges.

`CmeFactRow.Kind` says which table a row belongs to; the two descriptor column
lists project different subsets of the same row type.

## 2. Tables

Both fact tables are **exactly** the supplied DDL — column names, order, types,
nullability, key order and index — with one flagged exception (§2.2).

Note two deliberate asymmetries, both as supplied:

- The option table places `ProductDescription` between `ProductSymbol` and
  `ContractYear`; the future table places it **after** `ContractMonth`.
- `ProductDescription` is part of the **option** key but not the future key, so
  it is genuinely nullable on futures and effectively `NOT NULL` on options.

A TVP binds by position, so getting those two backwards is the easiest mistake
available here — `CmeTvpContractTests` has a test dedicated to it.

`ProductDescription` being in the option key also means SQL Server promotes it to
`NOT NULL` regardless of the DDL. The loader therefore sends `''`, never NULL,
for a description-less section; the option column's projection is deliberately
direct rather than null-coalescing, which is the opposite of the future column's.

### 2.1 ⚠ BALMO / day-label futures rows are NOT loaded

A minority of futures sections put the contract month on the header and use a day
of month as the row label:

```
1D AUG26 RBOB Gasoline BALMO Futures
10  ... 3.2375 ...    17  ... 3.2784 ...    20  ... 3.2795 ...
```

`PK_ARM_STLBASIC_Future` is
`(ExchangeCode, ProductCode, TradeDate, ProductSymbol, ContractYear, ContractMonth)`
— **no day component** — so all of a section's day rows collapse onto one key and
only one arbitrary day's prices would survive the merge.

Offered the choice between adding a `ContractDay` column to the table and its key,
or skipping these rows, **the requester chose to skip them.** That is **8,827 of
56,019** futures rows per trade date, all in STLCPC and STLEQT.

The omission is made auditable rather than silent:

- counted per file in `CmeParseStats.DayLabelRowsSkipped`
- written to `arm.FileLog.DayLabelRowsSkipped` and folded into that row's message
- logged at **warning** level per file
- reported by `arm.usp_ValidateLoad` (`SkippedDayLabelRows`)

To start loading them, add `ContractDay` to the table **and** its primary key
**and** the TVP in `002` **and** the merge in `003` — all four, together.

### 2.2 Deviation from the supplied DDL: `ModifiedAtUtc`

The supplied DDL defaulted `ModifiedAtUtc` to `SYSDATETIME()`, which is the
server's **local** time and contradicts the column's own name. Every other loader
in this repo stamps `SYSUTCDATETIME()`, and the merge procs set the column
explicitly, so a local-time default would also make freshly inserted rows
disagree with updated ones. `SYSUTCDATETIME()` is used throughout, and
`EveryTimestampIsUtc` pins it so a later edit cannot reintroduce a mix.

This is the same deviation EOX made, for the same reason. Change the default and
both procs together if local time is genuinely wanted.

### 2.3 Added by this loader

- `arm.Status` — fixed outcome catalog (`Success`, `Failed`, `NotAvailable`).
- `arm.FileLog` — one row per bulletin, every outcome, keyed on `FileName`
  (unique across the drop because the exchange code is in the name).

`arm.FileLog` carries the **parse counters**, not just a status, because the
supplied DDL gives the fact tables no provenance column at all. That makes the
hub the only place a partially-loaded or partially-skipped bulletin is visible:
`DayLabelRowsSkipped` is data the loader deliberately dropped, and
`UnclassifiedLines` is the early warning that CME changed the layout — neither is
reconstructible from the fact tables.

### 2.4 `RowOrdinal` — in the TVPs, not the tables

The 1-based position of a row within its bulletin. It gives the merges' batch
de-duplication a deterministic tiebreak (`ORDER BY RowOrdinal DESC` — last
occurrence in the file wins) instead of depending on row order inside a table
variable, which is not guaranteed. Live data has zero duplicate keys within a
bulletin, so it never actually breaks a tie today; it exists so that if CME ever
emits the same key twice, the outcome is defined rather than arbitrary. `MERGE`
raises *"cannot UPDATE/INSERT the same row more than once"* on a duplicated
source key, so the de-dup itself is mandatory regardless.

## 3. Extraction

### 3.1 The absolute-path quirk

`docs/apis/CME.md` §1.1 has the detail. In short: the server accepts only
relative paths, and SSH.NET's `SftpFile.FullName` reports the absolute form it
rejects. `CmeSftpFileSystem` never propagates `FullName`; `Normalize` strips
leading/trailing slashes and `Combine` appends leaf names to a relative parent.
`SftpPathTests` pins both.

`WalkAsync` runs the whole 5-level walk over **one** SSH session — per-listing
connections would be ~120 handshakes. It is capped at 20,000 directories so a
symlink loop fails loudly instead of consuming the work-unit timeout.

### 3.2 Work units and the resume key

One unit = one feed × one trade date = one bulletin = one parse = two merges.

```
settled:  cme:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}
hot:      cme:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}:run={token}
```

Both halves are load-bearing:

- The **content stamp** means a bulletin CME republishes gets a new key at any
  age and is reloaded, while an unchanged file is skipped however often the
  loader runs. This matters here because clearing genuinely does revise a
  settlement file in place — the header itself distinguishes `FINAL PRE-CLEARING`
  from `FINAL POST-CLEARING` — keeping the same name.
- The **hot suffix** forces a re-pull inside `SettledAfterDays` even when the
  stamp has not moved, insuring against a republish that preserves both mtime and
  size.

The hot token is **UTC** in every strategy. `01:00` Central happens twice on a
fall-back night, so a local-zone hour token would repeat, the key would go
backwards, and an already-recorded success would suppress a legitimate re-pull.

Shipped default: `SettledAfterDays: 3`, `HotKeyStrategy: RunDate` → the newest 4
dates re-pull once per UTC day, older ones only when their stamp moves. On the
live drop that is 8 hot / 72 settled units.

### 3.3 Enumerate-what-exists, not enumerate-a-window

Unlike the date-window loaders in this repo, the drop is a short rolling window
and the requester asked to "always load all the available files". So discovery is
the authority and no candidate dates are constructed. A weekend has no folder, so
an absent date is an **absence of work** rather than a gap to record — which is
why, unlike EOX, there are no `NotAvailable` rows. (The status value is kept in
the catalog so a future date-window mode has one.)

`MinTradeDate` is available as an optional floor and ships blank.

### 3.4 Trade date is taken from the file name and cross-checked

The name and the `yyyy/MM/dd` folders encode the same date, so requiring them to
agree turns a mis-filed bulletin into a logged skip instead of rows stamped with
the wrong trade date — which no downstream check would catch, because every value
in such a row would be internally consistent.

### 3.5 Parsing

`CmeBulletinParser` is the heart of the loader; `docs/apis/CME.md` §3 is its
specification. Failure policy, in order of severity:

| Condition | Outcome |
|---|---|
| Empty file | throw |
| Column header no longer matches the expected geometry | throw |
| Nothing recognised at all (0 rows, 0 skipped) | throw |
| 0 rows but rows were deliberately skipped | **success, empty** |
| Data row before any product header | counted unclassified |
| Futures row with a day label | skipped, counted |
| One unreadable value | that column NULL, row loaded, counted |

The fourth and fifth rows of that table are the status-matrix distinction that
matters: *"we understood this file and chose to drop every row"* is a legitimate
empty read, while *"we recognised nothing in it"* means the format moved and the
file must not be recorded as loaded.

**The column-header geometry check** is the cheap early warning for the failure
mode positional parsing has to be watched for: if CME widens a column, slicing
would keep "working" while writing every value one field out of place. The
header's own labels are right-aligned to the same edges as the data, so verifying
that they still land there costs one line per file. The label *text* is not
pinned — `EST.VOL` and `ACTUAL VOL` are both correct.

`StrictLineParsing` (default **off**) turns a counted anomaly into a failed work
unit. It is off because the live drop really does contain one `KYP11 <br />`
artifact per STLEQT bulletin, and one such line must not cost a 12 MB file. Note
that the `<br />` header is *handled*, not tolerated, so strict mode still loads
it — only genuinely unrecognised lines fail.

## 4. Loading

### 4.1 Two TVPs, two merge procs, one sink

`CmeFactSink` partitions the parsed rows by `Kind` and calls
`arm.usp_BulkMergeStlbasicOption` then `arm.usp_BulkMergeStlbasicFuture`. The
order is fixed so two concurrent work units always take the two write gates the
same way round and cannot deadlock by taking them in opposite orders.

The descriptor's column list is the single source of truth: `BuildTable` walks it
to create the DataTable columns **and** to project each row's values through the
same list's `Get` selectors, so the schema and the values cannot drift from one
another. What could still drift is the descriptor versus the `.sql` — and
`CmeTvpContractTests` parses the real `.sql` and asserts name + order + type +
nullability, so editing one side alone fails the build.

The sink also fails fast if a `NOT NULL` column would reach the server as NULL,
because the server-side error names the *type*, not the row.

### 4.2 Batching

One bulletin yields up to ~150k rows across 21–23 columns. Rows are merged in
`MergeBatchSize` (default 20,000) chunks so each request and each server-side
transaction stays bounded.

### 4.3 ⚠ Why there is NO cross-file ordering guard

The OPIS/Argus/EOX merges guard every UPDATE with
`src.FileName >= tgt.FileName` because those feeds can deliver overlapping rows
in several files. Here they cannot: `ExchangeCode`, `ProductCode` **and**
`TradeDate` all sit in both primary keys, and one bulletin carries exactly one
(exchange, product, trade date), so two different files write **disjoint** key
sets. There is nothing to order. The supplied DDL also has no provenance column
to order by, so inventing one would be a schema change for a race that cannot
happen.

What *does* repeat is the same file, republished in place when clearing revises
it. That is handled by re-running the merge, which is why the UPDATE branch is
unconditional rather than guarded.

### 4.4 No merge deletes by absence

Each unit carries one bulletin — one exchange, one trade date — so a
`NOT MATCHED BY SOURCE` branch would wipe every other exchange and date in the
table. Neither proc has one; `NoMergeDeletesByAbsence` pins that.

### 4.5 Concurrency

`SqlWriteGate` keys on the proc name, so the option merge and the future merge
serialize independently of each other and neither contends with
`arm.usp_UpsertFileLog`. Two work units hitting the same table do serialize on
it, which is what keeps concurrent MERGEs off each other's key ranges.

## 5. Validation

`arm.usp_ValidateLoad` is **observational only** — `CmeLoadValidator` swallows its
own failures and never changes the run's outcome. Nine checks: failed files,
skipped day-label rows, unclassified lines, empty successes, hub-versus-facts row
counts, all-measures-NULL rows, unexpected indicator letters, unexpected
`PutCall`, and out-of-range contract months.

`AllMeasuresNull` deserves a note: a single such row is legal (a listed contract
with no activity), but a whole feed like that means the fixed-width geometry has
shifted — the specific failure mode positional parsing must be watched for.

## 6. Configuration

`Loaders:CME` in `appsettings.json`. `SftpUsername`/`SftpPassword` default to the
`SEE_DB` sentinel and resolve from `core.Param` via `AddLoaderSettings`; neither
is ever logged. `RootDirectory` ships as `"."` — the form the server wants —
though the driver normalises `"/"` too.

`CmeModule.WarnAboutConfiguration` warns at startup about settings that are legal
but nearly always a mistake: an unknown `EnabledFeeds` id, a missing US Central
time-zone entry, negative `SettledAfterDays`, `ForceReprocess` on,
`StrictLineParsing` on (with the STLEQT caveat), a tiny `MergeBatchSize`, and an
empty connection string.

## 7. Status and known gaps

**Build-only.** Solution builds clean (0 errors); **109** CME tests and **2,456**
solution-wide tests pass; all four `.sql` scripts parse clean under ScriptDom
(TSql160).

**Live-verified** (2026-09-07) through the real loader classes, no database:
the SFTP connection, `WalkAsync` over all five levels with relative paths (120
directories, 80 files, ~17 s), the discovery mapping for all 8 feeds, 80 distinct
resume keys with the expected 8-hot/72-settled split, and download + parse of one
bulletin per feed — **345,150 option + 25,516 future rows for a single trade
date, 7,101 BALMO rows skipped, zero unclassified lines** — plus both TVP
DataTables built from the parsed rows (23 and 21 columns).

The parser was additionally cross-checked against an independently written `awk`
implementation over all 16 bulletins: identical counts on every counter
(701,341 option rows, 47,192 future rows, 8,827 BALMO skipped, 6,394 TOTAL lines,
80,917 `CAB` values, 272,819 tick conversions, 0 duplicate primary keys).

**Not verified — the SQL has never been deployed.** No `CMEGroup` database exists.
ScriptDom checks syntax only: it cannot catch a missing object, a wrong column
count or a type mismatch. The merge behaviour, the `FileLog` upsert and the
validator's result-set shape all need a real deployment before anyone should call
them working.

Open risks:

1. **BALMO rows are dropped by design** (§2.1) — 8,827 rows per trade date. This
   is a decision, not a bug, but it is the largest known gap in coverage.
2. **Tick denominators are inferred from observed maxima**, not from vendor
   documentation, which does not exist for this drop. The evidence is strong (max
   numerator 7 for STLAGS, 63/635 for STLINT) but a product that changed its
   quoting convention would need re-measuring. A width outside the convention is
   nulled and counted rather than mis-scaled, so the failure is loud.
3. **`CAB` loses information.** The cabinet marker becomes NULL because the DDL
   has nowhere to record it. 80,917 values per 16 files.
4. **SSH.NET 2024.2.0 carries advisory GHSA-q939-rpr3-3284.** It is pinned to
   match `DataLoader.Platts` because the host references every loader, so NuGet
   unifies the package across the whole app — bumping it here would silently
   change Platts' runtime SSH stack. Upgrading is a solution-wide change (CME +
   Platts together, with a Platts regression run) and was deliberately kept out
   of this loader.
5. **The 10-day window is an observation, not a guarantee.** If CME lengthens the
   retention, the loader will happily enumerate everything it finds; set
   `MinTradeDate` if that becomes unwelcome.
6. **`TEST`-prefixed products are loaded as-is.** They are real rows in the real
   feed; filtering them was not requested.
