# Criterion loader — design of record

**Loader id:** `Criterion`
**Database:** `Criterion` · **Schema:** `arm` · **Tables:** nine
**Source:** Criterion Research PostgreSQL replica, `dda.criterionrsch.com:443/production` (SSL required)
**Field reference:** [`docs/apis/Criterion.md`](../apis/Criterion.md) — source facts live there and
are not restated here.
**Status:** **build-only.** Builds clean, 253 loader tests green (2,221 solution-wide), SQL
parse-checked, and the **read path verified against live production**. **Not** in
`Platform:EnabledLoaders`; the SQL has never been deployed and no row has been merged.

---

## 0. Summary and the five decisions that shape everything

| # | Decision | Because |
|---|---|---|
| D1 | **First relational source in the repo** — Npgsql, not HTTP/FTP | The vendor delivers a PostgreSQL replica. Confined to one class so nothing else in the platform changes |
| D2 | **Both financial feeds read `financial_json_latest`** | The specified relations were stale and disjoint — see §2 / apis §5.1 |
| D3 | **`FinancialSeriesData` is PAGED, everything else is not** | One source row unpivots to ~1,800 target rows; a whole day at once would buffer gigabytes (§4) |
| D4 | **Intraday observations collapse in C#, deterministically, and are counted** | The requested PK admits one row per date. Collapsing in SQL would be non-deterministic *and* would error (§5) |
| D5 | **Pointflows merges on the NATURAL key**, not the declared PK | The declared PK contains an `IDENTITY` column, which a merge cannot match on (§6) |

### User decisions taken on the record

| Ref | Question | Decision (2026-09-03) |
|---|---|---|
| U1 | `financial_json_partitioned` is 2 years stale and shares no keys with `financial_json` — what should feed the two financial tables? | **`data_series.financial_json_latest` for both** |
| U2 | The PK discards 287 of every 288 intraday observations — widen the key, or accept the loss? | **Load as specified, last-wins** |
| U3 | `arm.Pipelines_Pointflows` has no source and none exists — what populates it? | **Derive by joining `nomination_points` ⟕ `metadata`** |

### Deviations from the supplied DDL

**The nine tables are reproduced VERBATIM** — every column name, type, nullability, primary key,
`IDENTITY`, the `geography` column and the `DEFAULT (sysdatetime())` semantics. The only additions
are indexes:

- **+** one unique nonclustered index, `UQ_ARM_Pointflows_NaturalKey` — **load-bearing, not
  cosmetic.** The merge matches on `(MetadataId, EffGasDay, CycleId)`; the clustered PK leads with
  those three but does not enforce their uniqueness because `Id` is in the key. Without this index a
  duplicate could accumulate unnoticed and the *next* merge would fail with "attempted to update the
  same row more than once".
- **+** eleven nonclustered indexes for query access (`Ticker`, `TspShort`, `EffGasDay`, `MongoId`,
  `PostDate`, `ModifiedAtUtc`, …). Purely additive.

One oddity is kept **on purpose** rather than silently fixed: `ModifiedAtUtc DEFAULT (sysdatetime())`
is server **local** time in a column named `...Utc`. Every merge proc sets `SYSUTCDATETIME()`
explicitly, so the default only affects rows inserted by something else. Same posture as
`sql/ICE/001`.

---

## 1. Component layout

```
CriterionDescriptors.cs   the registry — 9 feeds, 9 tables, every column mapping
CriterionSettings.cs      config + the SEE_DB credential sentinels
CriterionTime.cs          UTC window arithmetic + the 3 JSON date formats
CriterionConvert.cs       PostgreSQL → CLR narrowing, truncation reporting
CriterionSeriesData.cs    the JSON unpivot and the intraday collapse
CriterionSource.cs        Npgsql: generated SELECT, paging, the read path
WorkUnits.cs              slice enumeration + the resume-key contract
SourceReaders.cs          ISourceReader adapter — and all the loss reporting
Sinks.cs                  TVP sink + the batching wrapper
CriterionLoadValidator.cs post-run observational checks
CriterionModule.cs        DI wiring, per-feed pipelines, startup warnings
```

**The descriptor is the single source of truth.** One ordered column list per table drives four
things that would otherwise be kept in sync by hand:

```
CriterionTableDescriptor.Columns
   ├─→ the generated SELECT's column list and order      (CriterionSource.BuildSelect)
   ├─→ the CLR conversion of each value                  (CriterionConvert)
   ├─→ the DataTable's columns, order and types          (CriterionTvpSink.BuildTable)
   └─→ the expected TVP in sql/Criterion/002             (asserted by CriterionTvpContractTests)
```

The first three cannot drift because they all walk the same list. The fourth could — so the test
**parses the real `.sql` file** and fails the build on any mismatch of name, order, type or
nullability.

---

## 2. The nine feeds

| Feed | Source relation | Window | Work units / run (defaults) |
|---|---|---|---|
| `MiscPeriod` | `misc.periods` | snapshot | 1 |
| `MiscUnit` | `misc.units` | snapshot | 1 |
| `PipelinesRegion` | `pipelines.regions` | snapshot | 1 |
| `FinancialMetadata` | `data_series.financial_metadata` | snapshot | 1 |
| `PipelinesMetadata` | `pipelines.metadata` | snapshot | 1 |
| `FinancialSeries` | `data_series.financial_json_latest` | `post_date` | 31 |
| `FinancialSeriesData` | `data_series.financial_json_latest` | `post_date`, **paged** | ~217 |
| `PipelinesNominationPoint` | `pipelines.nomination_points` | `eff_gas_day` | 31 |
| `PipelinesPointflows` | `nomination_points` ⟕ `metadata` | `eff_gas_day` | 31 |

Ordered **dimensions first**. Nothing depends on it — the pipelines are independent and the target
tables carry no foreign keys — but an interrupted run then leaves the database consistent at more
points in time.

**Snapshot vs window.** The five dimensions are 12–42,446 rows and carry no usable change column, so
a full read is both simpler and cheaper than tracking deltas. The four fact feeds sit on relations of
2.2 M–75.5 M rows and are *always* windowed.

**Why `post_date` and not `load_date`.** `financial_json_latest` is `RANGE`-partitioned on
`post_date` across ~150 partitions. Filtering on it **prunes**; filtering on `load_date` scans every
partition. Over a 30-day window the two select nearly the same rows (`load_date` within 30 days
yields `post_date` 2026-08-05…09-03, exactly 30 post-dates).

---

## 3. Resume keys

```
settled:  criterion:{FeedId}:{slice}
hot:      criterion:{FeedId}:{slice}:run={token}

slice  =  "snapshot"              (snapshot feeds)
       |  "2026-09-02"            (day-windowed feeds)
       |  "2026-09-02:p3"         (the paged feed)
```

A day older than `SettledAfterDays` gets the **stable** key — loaded once, then a cheap skip forever.
A newer one gets the **run-varying** key so revisions are re-pulled.

**With the shipped 30/30 defaults the settled zone is EMPTY and every day is hot.** The oldest day in
the window is exactly 30 days old and `age > 30` is false. That is deliberate and matches ICE
(30/30), EvolutionMarkets (30/30) and NGI (60/60): **this source revises history.**
`financial_json_latest` republishes a series under a new version, and nomination cycles are restated
during and after the gas day. A stable key would freeze the first value seen while the loader
reported clean runs. `SettledAfterDays < DaysBack` is warned about at startup.

**Snapshots are hot too**, under `AlwaysReloadSnapshots` (default true) — the catalogs genuinely
change, so a stable key would load them once and never look again.

**Tokens are UTC.** `yyyyMMddHH` is strictly monotonic only in UTC: `01:00` local occurs twice on a
fall-back night, so a local-zone hour token would repeat, the key would go backwards, and an
already-recorded success would suppress a legitimate re-pull for an hour.

**The page number is part of the key.** Changing `SeriesPageSize` therefore re-loads the affected
days under fresh keys rather than skipping half of them. That is the safe direction: re-merging is
idempotent, whereas a stale page key that no longer covers the same series would leave a gap.

---

## 4. Why `FinancialSeriesData` is paged, and how

This one feed is a different order of magnitude from the other eight.

| | Per source row | Per post_date | Per 30-day run |
|---|---:|---:|---:|
| Source rows | 1 | ~1,530 | ~46,000 |
| JSON bytes | ~165 KB (max 1.75 MB) | ~250 MB | **~7 GB** |
| Target rows | ~1,800 (max 17,496) | ~2.8 M | **~90 M** |

Measured live: **25 source rows → 46,064 target rows**.

A work unit of one whole post_date would buffer millions of rows — hundreds of megabytes — multiplied
by `MaxConcurrentWorkUnits`. So the day is split into **pages of `SeriesPageSize` source rows**
(default 250 ≈ 460,000 target rows ≈ 40 MB of JSON per unit).

**Paging by pre-enumerated keys, not by `OFFSET`.** The work-unit provider runs one cheap query per
day selecting **only `financial_json_uuid`** — never the 165 KB payload — and chunks the result:

```sql
-- once per day, during work-unit enumeration
SELECT financial_json_uuid FROM data_series.financial_json_latest
WHERE post_date = @day ORDER BY financial_json_uuid

-- then once per page
SELECT financial_json_uuid, data FROM data_series.financial_json_latest
WHERE post_date = @day AND financial_json_uuid = ANY(@keys)
```

`OFFSET` is O(n) in PostgreSQL and would re-scan the partition once per page. Keys make each page an
index seek on the source PK. The `ORDER BY` is not decoration — it makes page boundaries reproducible
between runs, without which a resume key would stop meaning anything.

**The day predicate is kept alongside the key predicate.** The keys alone identify the rows, but
without `post_date = @day` PostgreSQL must probe all ~150 partitions instead of one.

**`SequentialAccess`** on the reader, so Npgsql streams the 165 KB–1.75 MB payload rather than
buffering the whole row first.

**Merge batching.** A unit's ~460,000 rows are merged in batches of `MergeBatchSize` (50,000). A
single TVP that size would hold one MERGE's locks on `arm.Financial_SeriesData` and its tempdb space
for the whole call. Each batch is independently idempotent, so a failure part-way leaves earlier
batches durably written and the unit's resume key **un-recorded** — the next run redoes the unit and
converges. That at-least-once posture is safe here precisely because **no merge deletes by absence**.

---

## 5. ⚠ The intraday collapse

`arm.Financial_SeriesData` is keyed `(FinancialJsonId, Date)` — one row per calendar day. About 3% of
series are five-minute intraday and carry **up to 288 observations per date** (ERCOT Real-time Fuel
Mix: 17,496 elements across 61 dates).

**Decision U2: keep the supplied key, accept last-wins.** So **287 of every 288 intraday observations
are discarded by design.** Three things keep that honest rather than merely lossy.

**1. It happens in C#, not in SQL.** Leaving it to `MERGE` would be worse in both directions: MERGE
*errors* on a duplicated source key ("attempted to update the same row more than once"), and if it
did not, the survivor would be whichever row the query plan reached last.

**2. The winner is deterministic and meaningful.** `CriterionSeriesData.Parse` keeps the **last
element for a date in source array order** — for a chronologically ordered intraday array that is the
day's closing observation. Pinned by
`SeriesDataTests.The_last_observation_of_a_date_wins`; if that test ever fails, the loaded value for
every intraday series has changed meaning.

**3. It is counted and logged at Information level**, per work unit:

```
Criterion FinancialSeriesData 2026-09-02:p0: collapsed 2999 intraday observation(s)
into 46064 daily row(s) from 25 series — PK (FinancialJsonId, Date) admits one row
per day, so the LAST observation of each date wins
```

The loss is visible in every normal run's output, not something you need a log-level change to find.

The merge proc keeps a `ROW_NUMBER()` de-dup as a second line of defence, so it is safe to call with
an uncollapsed batch. It orders by `[Value] DESC` — arbitrary in content but **deterministic**, so
re-running a unit cannot change what is stored. The loader's own collapse decides the business
answer.

**To reverse this decision** later: add `ObsTime datetime2(0) NOT NULL` to the table, TVP and
descriptor, put it in the PK, and have `Parse` stop collapsing. Nothing else changes.

---

## 6. ⚠ Pointflows: a derived table with an unmergeable primary key

The table is **derived** (§ apis 5.3) by a LEFT JOIN of `nomination_points` onto `metadata`. Two
things about it are unusual.

**The join must be LEFT.** 25 of 19,431 nomination rows on 2026-09-02 reference a `metadata_id` with
no catalog row. An INNER JOIN would silently drop those flows; the LEFT JOIN keeps the flow and
leaves the descriptive columns NULL. `arm.usp_ValidateLoad` reports the count so a *rising* orphan
rate is visible.

**The declared PK cannot be merged on.** `PRIMARY KEY CLUSTERED (MetadataId, EffGasDay, CycleId, Id)`
contains `Id INT IDENTITY(1,1)`. The server generates it, so no inbound row can carry one, and
matching on it would make **every run insert a fresh duplicate** rather than update.

So the proc merges on the **natural key** `(MetadataId, EffGasDay, CycleId)` — verified unique in the
source (19,431 rows = 19,431 distinct triples) — and `UQ_ARM_Pointflows_NaturalKey` keeps it that
way. `Pointflows_merges_on_the_natural_key_not_the_identity_pk` pins this so nobody "fixes" it back
to the PK after reading 001 alone.

**Row counts will not match `arm.Pipelines_NominationPoint`**, and that is correct: Pointflows holds
one row per `(point, gas day, cycle)` while NominationPoint holds one per
`(point, gas day, cycle, hourly cycle)`.

---

## 7. Safety properties

**No SQL injection surface.** Every SELECT is generated from compile-time descriptor constants; the
only runtime values are the parameters `@day` and `@keys`. `Every_runtime_value_is_a_parameter`
asserts that the generated SQL contains **no non-empty string literal** (the sole permitted literal
is the `''` inside `NULLIF(TRIM(col), '')`), and a companion test proves the guard would actually
catch a concatenated value rather than passing vacuously.

**Credentials never reach a log.** `SourceUsername` / `SourcePassword` ship as `SEE_DB` and resolve
from `core.Param`. The Npgsql connection string is built with `NpgsqlConnectionStringBuilder` — so a
password containing `;` or `'` is escaped rather than terminating the string — and is never written
out. The module logs host, port and database only.

**The source's connection budget is respected.** The pool is capped at
`MaxConcurrentWorkUnits + 1`, not Npgsql's default 100. It is a shared vendor replica.

**Columns with no source are never written.** Four target columns (`Enabled`, `IsLatest`,
`MappingId`, `Id`) appear in no TVP, no INSERT list and no UPDATE SET, so a hand-set value survives
every reload. Asserted three ways: absent from the descriptor, absent from the `.sql` type, and
absent from the parsed proc body.

**`geography::Point` is guarded.** It *throws* on a latitude outside [-90, 90] or a longitude outside
[-180, 180], which would abort the whole batch. The range test in the proc is load-bearing. (Today
both coordinates are NULL for all 42,446 source rows, so `Point` lands NULL.)

**No merge deletes by absence.** Each work unit carries one slice, so `WHEN NOT MATCHED BY SOURCE
THEN DELETE` would wipe every other slice's rows. Asserted for all nine procs.

**A feed's failure costs only that feed.** The module records it and carries on — the Argus/ICE
posture.

---

## 8. Configuration

`Loaders:Criterion` in `src/DataLoader.Host/appsettings.json`.

| Setting | Default | Notes |
|---|---|---|
| `DaysBack` | **30** | Window is `[today - DaysBack, today]` **inclusive** → 31 units per day-windowed feed |
| `SettledAfterDays` | 30 | Equal to `DaysBack` ⇒ all-hot. Lowering it is warned about |
| `HotKeyStrategy` | `RunDate` | `RunHour` is worth considering — nomination cycles are restated through the gas day |
| `AlwaysReloadSnapshots` | `true` | The dimensions change |
| `SeriesPageSize` | 250 | **A memory control.** ≈460,000 target rows / ~40 MB JSON per unit |
| `MergeBatchSize` | 50,000 | Rows per TVP call |
| `MaxConcurrentWorkUnits` | 4 | Also caps the Npgsql pool at 5 |
| `WorkUnitTimeoutSeconds` | 1800 | A paged unit reads ~40 MB and merges ~460,000 rows |
| `SourceCommandTimeoutSeconds` | 600 | |

**⚠ `DaysBack` is the setting that governs run cost.** With `FinancialSeriesData` enabled it scales
linearly at roughly **230 MB read and 3.7 M rows merged per day of window**. The module logs an
estimate at startup when `DaysBack > 30`.

---

## 9. Verification status

| Gate | Status |
|---|---|
| `dotnet build DataLoaderPlatform.sln -c Release` | ✅ 0 errors (2 pre-existing NU1903 warnings in Platts) |
| `dotnet test tests/DataLoader.Criterion.Tests` | ✅ **253 passed**, 0 failed |
| `dotnet test DataLoaderPlatform.sln -c Release` | ✅ **2,221 passed**, 0 failed — no regressions |
| SQL parse-check (ScriptDom, TSql160) | ✅ 4 / 4 files clean; checker confirmed non-vacuous |
| TVP contract (descriptor ↔ 001 ↔ 002 ↔ 003) | ✅ enforced by the build |
| **Live source: generated SQL, all 9 feeds** | ✅ every descriptor column matched by name **and order** |
| **Live source: full read path, all 9 feeds** | ✅ 0 truncations, 0 dropped, 0 unparseable, all types correct |
| SQL deployed to a live database | ❌ **never** |
| Data loaded / data-quality validated | ❌ **never** — no target database exists |

**Do not report data validation as passed.** The read half of this loader is proven against
production; the write half has been parse-checked and contract-tested but has never touched a SQL
Server.

### To deploy

```bash
# 1. create the database, then in order:
sqlcmd -S ARMH-OPSDB01 -d Criterion -i sql/Criterion/001_CreateCriterionSchema.sql
sqlcmd -S ARMH-OPSDB01 -d Criterion -i sql/Criterion/002_CreateCriterionTvpTypes.sql
sqlcmd -S ARMH-OPSDB01 -d Criterion -i sql/Criterion/003_CreateCriterionProcedures.sql

# 2. store the source credentials (never in appsettings.json)
--   core.Param: LoaderName='Criterion', ParamName='SourceUsername' / 'SourcePassword'

# 3. first run — start SMALL. The shipped DaysBack=30 is a ~90-million-row first load.
--   Set DaysBack=1 and EnabledFeeds to the five dimensions, confirm, then widen.
Criterion.cmd
```
