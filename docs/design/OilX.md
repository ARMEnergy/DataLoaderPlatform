# OilX loader — design of record

LoaderId `OilX` · Database `OilX` · Schema `arm` · 8 feeds → 8 tables

Field reference: `docs/apis/OilX.md` (per-feed headers, vendor behaviours, limits).
Template: `src/DataLoader.EOX` (descriptor-driven CSV loader) and
`src/DataLoader.Genscape` (per-feed pipelines, settled/hot resume keys).

---

## 1. Shape

Eight feeds, each a CSV published one-or-more times a day, each a full snapshot of
all history. One feed → one descriptor → one TVP → one merge proc → one table.
Adding a feed is a descriptor plus its SQL, never new plumbing.

```
OilXModule
  └── per feed: OilXPipeline
        ├── OilXWorkUnitProvider   → (feed, day) units over [today-DaysBack, today]
        ├── OilXManifestClient     → GET /csv/?day=&files=<one feed>   (fresh URLs)
        ├── OilXSourceReader       → streams one CSV, yields batches
        └── OilXTableSink          → TVP merge, one batch per call
```

Feeds are **mutually independent**: no barrier, no shared state, no FK between
target tables. A feed that fails is recorded and the run continues (the
Argus/ICE/Criterion/Genscape posture).

---

## 2. Work units

**One work unit = one (feed, day).** Not one per file, and not one per feed.

* *Not per file*, because a day's snapshots must merge in upload order (§4) and
  the platform's `ParallelRunner` gives no ordering between units.
* *Not per feed*, because `RunDate` leads the primary key and a unit that spanned
  days could not be resumed or reported per day.

Units of the same feed but different days run in parallel safely: `RunDate` leads
the PK, so different days touch disjoint keys.

The window is `[today - DaysBack, today]` **inclusive**, in UTC — the vendor's
`uploaded_at` and the in-file `RunDateTime` are both UTC, and a local-zone window
would shift the boundary twice a year. With the shipped `DaysBack = 30` that is 31
days × 8 feeds = **248 work units**.

---

## 3. Resume keys — settled vs hot

A day's age is `today - day` in UTC days.

| zone | condition | key | effect |
|---|---|---|---|
| settled | `age > SettledAfterDays` | `feed={f};day={yyyy-MM-dd}` | loaded once, then skipped forever — no manifest call, no download |
| hot | `age <= SettledAfterDays` | `feed={f};day={yyyy-MM-dd};{token}` | re-pulled, token per `HotKeyStrategy` |

`HotKeyStrategy` ∈ `RunDate` (UTC `yyyyMMdd`, **the default**), `RunHour` (UTC
`yyyyMMddHH`), `RunId` (every invocation). All tokens are **UTC**: `RunHour`'s
`yyyyMMddHH` is strictly monotonic only in UTC, since `01:00` local occurs twice
on a fall-back night and a repeated token would let an already-recorded success
suppress a legitimate re-pull.

**`SettledAfterDays` ships as 1, deliberately deviating from the repo's usual
all-hot default.** ICE, Genscape and EvolutionMarkets all ship
`SettledAfterDays == DaysBack` because those vendors restate recent data, so a
stable key would freeze the first value seen. OilX does not restate: a published
day's file is immutable (`docs/apis/OilX.md` §2 Behaviour 8). What *does* still
change is **today**, whose file set grows through the day (Behaviour 1) — hence 1
rather than 0.

`OilXModule.WarnAboutConfiguration` warns when `SettledAfterDays >= DaysBack`,
which would make every day hot and re-download ~6.5 GB per run for data that
cannot change — the inverse of the warning Genscape carries, because the risk here
runs the other way.

---

## 4. Ordering within a work unit — load-bearing

A unit fetches that (feed, day)'s manifest, sorts `data[]` by **`uploaded_at`
ascending**, and merges each file in turn. Later snapshots overwrite earlier ones
on the same key, so the final table state is the newest value the vendor
published that day.

This is a correctness requirement. All of a day's snapshots carry the same in-file
`RunDate`, so they collide on `(RunDate, RowId)`; without ordering, a row's value
is whichever snapshot's merge happened to land last.

Ties on `uploaded_at` are broken by file name, so the order is total and a re-run
reproduces it.

Files within a unit are processed **sequentially**. Parallelism is across units.

---

## 5. RowId — a deterministic GUID, never random

`RowId` is `UUIDv5`-style: **SHA-1 over the feed's business key, first 16 bytes,
with the RFC-4122 version and variant bits set**, under a fixed namespace GUID
constant to this loader.

A random GUID would make every run insert a fresh row instead of merging — the
user's "merge the data by PKs" would silently become append-only, and the table
would grow by ~2M rows per run. The derivation must therefore be:

* **deterministic** — same business key ⇒ same `RowId`, across processes and
  machines. Nothing seed-dependent: never `GetHashCode`, never `HashCode.Combine`
  (.NET Core randomises string hashing per process).
* **business-key only** — not over the whole row. A value that changes between
  snapshots must UPDATE its row, not create a second one. Change detection is
  `Checksum`'s job (§7), which only makes sense if `RowId` is stable under value
  changes.

The canonical string is the key columns joined by `U+001F`, with `U+0000` for
null — distinct from empty string, so a value being cleared is still a change.
Rendering is invariant-culture and type-normalised exactly as the stored column
(§7), so the key cannot drift with locale.

A key component may legitimately be **blank** — Flow's two sub-country columns
usually are. A blank CSV cell converts to NULL, so it contributes the null token:
a stable, intentional identity rather than a blank key. This is why no key column
is marked `Required`; marking one would drop every row whose key is legitimately
blank, which for Flow is most of them.

### The business keys, each verified unique against a live file

| feed | key columns | verified |
|---|---|---|
| CargoTracking | `FlowID` | 396,865 / 396,865 |
| FloatingStorage | `IMO`, `ReferenceDate` | 6,072 / 6,072 |
| Flow | `OriginCountryName`, `DestinationCountryName`, `GroupByDateIndicator`, `OriginSubCountry`, `DestinationSubCountry`, `ReferenceDate`, `GradeName` | 232,311 / 232,311 |
| GlobalBalance | `GroupName`, `ReferenceDate`, `FlowBreakdown`, `UnitMeasure` | 4,663 / 4,663 |
| OilFieldProduction | `OilFieldName`, `PortName`, `CountryName`, `ReferenceDate`, `UnitMeasure` | 252,130 / 252,130 |
| RegionalBalance | `GroupName`, `ReferenceDate`, `FlowBreakdown`, `UnitMeasure` | 22,090 / 22,090 |
| SupplyDemand | `CountryISOCode`, `ReferenceDate`, `FlowBreakdown`, `UnitMeasure` | 364,543 / 364,543 |
| Terminal | `TerminalName`, `ReferenceDate`, `FlowBreakdown`, `UnitMeasure` | 786,798 / 786,798 |

**⚠ Flow's sub-country columns are load-bearing.** Dropping
`OriginSubCountry` + `DestinationSubCountry` from that key collapses **28,887 of
232,311 rows** onto keys they would share — measured, not hypothetical. They are
frequently empty, which is exactly what makes them easy to mistake for noise.

`OilFieldProduction` keys on the fuller `(field, port, country, date, unit)` grain
rather than the minimal `(field, date, unit)` — which is also unique live — so a
field served by a second port becomes a second row rather than a value silently
dropped by the merge's de-dup guard. The trade-off is that a `PortName` the vendor
later populates on a previously-blank row changes that row's `RowId` and strands
the old one; `arm.usp_ValidateLoad` reports it.

---

## 6. Status matrix

Per (feed, day) work unit:

| condition | outcome | rows |
|---|---|---|
| HTTP 200, ≥1 file, parsed | **success** | n |
| HTTP 422, body starts `No data for files` | **success** — legitimate empty day | 0 |
| HTTP 422, body starts `Unavailable files` | **FAIL** — feed name is wrong | — |
| HTTP 422, any other message | **FAIL** — unrecognised, do not guess | — |
| HTTP 401 | **FAIL** — key invalid/unresolved | — |
| HTTP 200 but `success:false` | **FAIL** | — |
| HTTP 200, `data[]` empty | **FAIL** — unexpected; the empty case is 422, so this means the contract moved | — |
| file downloads but header unrecognised | **FAIL** | — |
| row fails conversion | row dropped + counted; unit succeeds unless **all** rows fail | n−dropped |

The first three rows are the whole point. An empty day answers **422**, not
200-with-empty-data, so a blanket "422 = error" fails every publication gap, and a
blanket "422 = no data" turns a typo in `EnabledFeeds` into clean empty runs
forever. They are separated **only by the message prefix**
(`docs/apis/OilX.md` §2 Behaviour 4).

Requesting **one feed per manifest call** is what makes this work: with several
feeds in one call a missing one is silently omitted at HTTP 200 (Behaviour 5) and
could not be attributed.

---

## 7. Checksum

`Checksum` is FNV-1a/32 over the row's **value** columns — key columns excluded,
since they are identical on both sides of every matched comparison — reinterpreted
into `INT`. Same algorithm and rationale as `EvoChecksum`
(`src/DataLoader.EvolutionMarkets/EvoChecksum.cs`): seedless, fixed, reproducible
in a unit test.

It lets the MERGE skip the UPDATE when nothing changed, so `ModifiedAtUtc` means
*"when this row's values last actually changed"* rather than *"time of last run"*.
That matters more here than anywhere else in the repo: with four snapshots a day
of a 396,866-row file, almost every row is byte-identical to the one before it, and
without the guard every run would re-stamp ~2M rows.

`FileName` is **excluded** from the checksum — it is provenance, not payload. A
later snapshot that changed nothing must not count as a change. (It is still
UPDATEd on match, so it always names the snapshot that last wrote the row.)

Normalisation is part of the contract: `decimal` renders `F8`, `float` renders
`R`, dates render `yyyy-MM-dd`, datetimes `yyyy-MM-dd HH:mm:ss.fffffff`, all
invariant-culture. Un-normalised `decimal` rendering would report phantom changes,
because `decimal` preserves trailing zeros in its scale (`0.5` vs `0.50`).

---

## 8. Streaming and batching

A CargoTracking snapshot is **208 MB / 396,866 rows**, and a 31-day window moves
~6.5 GB. Nothing may be materialised whole:

* **the HTTP response** is read with `HttpCompletionOption.ResponseHeadersRead`
  and consumed as a `Stream`;
* **the CSV** is parsed by `OilXCsv.ReadRecords(TextReader)`, which yields one
  record at a time. This is the one place OilX deliberately departs from the EOX
  template — `EoxCsv.ParseRecords(string)` takes the whole document as a string,
  which for a 208 MB file is ~416 MB of UTF-16 plus a ~1–2 GB `List<string[]>`;
* **the merge** runs per batch of `BatchSize` rows (default 20,000), so a work
  unit's peak footprint is one batch, not one file.

Each batch is an independent TVP call. A unit that fails mid-file therefore leaves
earlier batches merged — acceptable because every merge is idempotent on
`(RunDate, RowId)` and the unit's resume key is only recorded on success, so the
whole day is re-merged on the next run and converges.

---

## 9. TVP contract

A TVP binds **by position**. For each feed, the DataTable column order, the
`CREATE TYPE` column order and the target table's column order are the same list.
`BuildTable` iterates the descriptor's `Columns`, and the reader filled each row's
values from that same list, so the two in-process sides cannot drift.

What could drift is the descriptor versus the `.sql`, so
`tests/DataLoader.OilX.Tests/OilXTvpContractTests.cs` **parses the real
`sql/OilX/002` file**, extracts each `CREATE TYPE` body and asserts name + order +
type + nullability against the descriptors. Editing one side alone fails the
build.

`ModifiedAtUtc` is DB-stamped and is never a TVP column, matching every loader in
the repo.

`RunDate`, `RowId`, `FileName` and `Checksum` are `NOT NULL` in every TVP:
the first two are the primary key, and the last two are required provenance and
change-detection. A row that cannot produce all four is dropped and counted rather
than merged under a blank key.

---

## 10. Merge procedures

One `arm.usp_BulkMerge<Table>` per feed, all sharing:

* **Batch de-dup** via `ROW_NUMBER() OVER (PARTITION BY RunDate, RowId)`, keep row
  1. Verified not to fire — every key is unique within a live file (§5) — but kept
  because MERGE does not degrade gracefully: one duplicated source key aborts the
  whole batch with *"attempted to update the same row more than once"*, so a
  vendor that starts repeating a row would take the loader down rather than merely
  repeat itself.
* **No delete-by-absence.** No `WHEN NOT MATCHED BY SOURCE THEN DELETE`: a batch
  carries 20,000 rows of one day, and deleting rows absent from it would wipe the
  rest of that day and every other day.
* **Checksum-guarded UPDATE** — `WHEN MATCHED AND tgt.Checksum <> s.Checksum`, so
  an unchanged row is not re-stamped (§7).
* `ModifiedAtUtc = SYSUTCDATETIME()` written explicitly, so rows hold true UTC
  even though the table default is `sysdatetime()`.
* `SELECT … AS RecordsProcessed`, which `SqlSinkBase` surfaces to `core.LoadLog`.

---

## 11. Validation

`arm.usp_ValidateLoad` is observational — it never changes a run's outcome, and
`OilXLoadValidator` swallows its own failures. It reports:

* `RunDateGap` — a day in the window with no rows in a feed.
* `SnapshotShrink` — a `RunDate` whose row count is materially below the previous
  day's, which is how a truncated download or a partially-merged unit shows up.
* `OrphanPort` — `OilFieldProduction` rows whose `(field, date, unit)` appears
  under two `PortName`s, one of them blank (§5's trade-off made visible).
* `BlankKeyComponent` — any key column empty-but-not-null.
* `StaleModified` — rows whose `ModifiedAtUtc` predates their `RunDate`.

---

## 12. Secrets

`api_key` is a **query parameter** (`docs/apis/OilX.md` §5), not a header — unlike
every other HTTP loader here. The named client has `.RemoveAllLoggers()`, the
loader logs feed/day/file rather than URLs, and `OilXHttp.Redact` strips
`api_key=…` and the S3 `Signature`/`x-amz-security-token` from any string that
reaches a log or an exception. `ApiKey` is `SEE_DB` in `appsettings.json`.
