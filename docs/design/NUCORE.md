# NUCORE loader — design of record

Reads five risk endpoints from ARM's NUCORE integration gateway into five `dbo` tables in
the `NUCORE` database.

Vendor behaviour lives in `docs/apis/NUCORE.md`; this document is about the loader.

---

## 1. Shape

Standard platform machinery — work units, resume keys, TVP merges, the SQL write gate,
`core.LoadLog` for idempotency — with one pipeline per endpoint.

```
NucoreModule
  └── per endpoint: NucorePipeline
                      ├── NucoreWorkUnitProvider   what to fetch
                      ├── NucoreWatermarkStore     where to resume (PositionDetails only)
                      ├── NucoreSourceReader       page → convert → batch
                      └── NucoreTableSink          TVP merge
```

The five pipelines are **mutually independent**: no barrier, no shared state, no FK between
the target tables. A failure in one is recorded and the run continues, matching the
Argus/ICE/Criterion/Genscape/OilX posture.

### Why not `LoaderPipelineBase`

The base implements read-all → transform-all → write-once. A single `Volatilities` day is
147,908 rows and `PositionDetails` is 2,468,453 rows across its trade months, so
materialising a unit before writing would mean millions of row objects in memory times
`MaxConcurrentWorkUnits`. `NucorePipeline` keeps the base's structure — idempotency via
`ILoadLogRepository`, bounded concurrency via `ParallelRunner`, per-unit timeout, counters —
but streams and merges in batches of `BatchSize`.

---

## 2. The column contract is generated, not written

436 columns across five tables, each of which must appear in the **same order** with the
**same type** in four places: the table (001), the TVP type (002), the merge procedure (003)
and the `DataTable` the sink builds. A TVP binds **by position**, so one typo in any of them
shifts every later column and writes rows that are entirely plausible and entirely wrong.

`scripts/Generate-NucoreSchema.ps1` emits all four from two inputs that are evidence rather
than opinion:

- `docs/apis/NUCORE.openapi.json` — the vendor's own OpenAPI document: column name, order,
  type, nullability.
- `docs/apis/NUCORE.observed-profile.json` — a profile of real full-day responses: `VARCHAR`
  sizing, `DATETIME2`-vs-`DATETIMEOFFSET`, the decimal scale actually in use.

**Never hand-edit `NucoreColumns.Generated.cs` or `sql/NUCORE/*.sql`.** Re-run the generator.
`NucoreTvpContractTests` parses the real `.sql` files and asserts name + order + type +
nullability against the descriptors, so a one-sided edit fails the build.

The generator also refuses to emit a primary key wider than SQL Server's 900-byte index
limit, and `NucoreDescriptorTests` asserts the same thing from the C# side — a two-sided
guard, so widening a `VARCHAR` cannot silently produce a table that fails to create.

### What is a decision rather than a fact

The endpoint block at the top of the generator — URL path, date parameter name, partition,
write mode, key columns — is hand-maintained and mirrored in `NucoreDescriptors`.
`NucoreDescriptorTests` pins the parts that were established empirically.

### Key ordering is deliberate

`KeyOrdinal` is carried separately from column order. The column list follows the vendor's
spec, where `PositionDetails` declares `systemDate` as its 58th property; the **primary key
leads with the date** so the clustered index groups a day together and day-range reads are a
seek. Deriving key order from column order silently reverses that — it did, and the test
caught it.

---

## 3. Work units and resumability

| Endpoint | Unit | Hot while | Settled |
|---|---|---|---|
| VarResults, PositionSummary, PnlExplain, Volatilities | one business **date** | within `SettledAfterDays` (3) of today | stable key, skipped forever |
| PositionDetails | one **trade month** | within `SettledTradeMonthsBack` (3) months, **and every future month** | stable key, skipped forever |

A settled unit's resume key is stable, so `ILoadLogRepository.BeginAsync` returns `null` and
the unit costs **nothing** — no request at all. That is what makes a full-history backfill
(from `StartDate`, shipped as 2025-01-01) affordable on every subsequent run.

A hot unit's key carries a UTC run token (`HotKeyStrategy`: `RunDate` by default), so it is
re-read each run. The token is UTC because `yyyyMMddHH` is monotonic only in UTC — `01:00`
local happens twice on a fall-back night, the key would go backwards, and an
already-recorded success would suppress a legitimate re-pull.

Weekends are not enumerated (`SkipWeekends`, default on): NUCORE publishes on business days
only, and skipping them removes ~29% of a backfill's units at no cost in data. Holidays are
**not** skipped — there is no calendar to consult, so they arrive as legitimate empty reads.

### PositionDetails: the watermark

That endpoint has no date parameter, so a day cannot be asked for and an unfiltered call is
~10 GB. Instead:

1. Work unit = one trade month (`TradeMonthFrom`/`TradeMonthTo`, a **lossless** partition —
   the yearly census sums to exactly the unfiltered total).
2. At unit start, read `MAX(SystemDate)` for that trade month via
   `dbo.usp_GetPositionDetailsWatermark`.
3. Page **newest-first** (`SortFields=-systemDate,tradeId,detailId,tradeMonth`) and stop at
   the first row at or below the watermark.

So a daily run reads only the system dates that are new, rather than the trade month's whole
history. The watermark is read **from the target table**, not a separate state table, so it
is self-healing: delete rows and they are simply re-fetched, with no state to drift.

⚠ This depends on `IX_PositionDetails_TradeMonth_SystemDate`. Without it the watermark read
is a scan of the largest table in the database, once per work unit. The watermark read uses a
120-second command timeout, not the 30-second default — Criterion's validator proved what a
silent timeout costs, and here a timeout that was swallowed into "no watermark" would trigger
a full re-read of the trade month and look like a slow run rather than a broken one, so it
throws instead.

---

## 4. Correctness guards

### The date-echo check

Omitting or misspelling a date parameter returns **HTTP 200 with the latest day's data**, and
two endpoints ignore a date parameter that looks correct. So every row's echoed date column
is compared against the requested date and a mismatch fails the unit with a message that says
what happened. Without this the loader would write today's numbers under 400 historical dates
and the load log would record 400 successes.

Trade-month units are deliberately **not** echo-checked: their rows legitimately span
hundreds of system dates.

### Stable paging

⚠ **Every request carries `SortFields`.** Paging this API unsorted is not stable — one
Volatilities day's pages 1 and 2 shared 111 rows, and therefore omitted ~111 others
(`docs/apis/NUCORE.md` §4.1). The omission is silent, which is what makes it dangerous.

As a backstop, the reader reconciles the rows it read against the envelope's own
`totalRecords` and fails the unit on a mismatch. That is independent of the sort, so it also
catches a tie straddling a page boundary — which matters because no column list makes
`PositionDetails`' order genuinely total. The check is skipped when the unit deliberately
stopped early at a watermark.

### Duplicate detection on the merge path

For the two endpoints that still have keys, the reader tracks every key it has seen within a
unit and throws on a repeat. Within a batch a duplicate key would raise a primary key
violation anyway; **across** batches it would silently overwrite one row with another. Both
mean the declared key is wrong, and both are now loud. This guard is what found the unsorted
paging bug.

### NULL handling in keys

Most of this vendor's dimension columns are nullable and several are genuinely empty —
`VarResults` alone had 540 rows with a null `productSpec` in one day. A NULL key column makes
the MERGE join `UNKNOWN`, so the row never matches and is re-inserted on every run. Key
strings are therefore coalesced to `''` on the way in, which is also exactly how key
uniqueness was verified against live data. Key columns are `NOT NULL` in the DDL, and both
the generator and `NucoreDescriptorTests` refuse a nullable key.

### Change detection

The merge procedures use `WHEN MATCHED AND EXISTS (SELECT t.* EXCEPT SELECT s.*)`. `EXCEPT`
treats NULL as equal to NULL, so the row is updated only when values genuinely differ and
`ModifiedAtUtc` tracks real change. A `t.col <> s.col OR ...` chain is `UNKNOWN` wherever
either side is NULL and silently stops detecting changes into and out of NULL.

A re-merge that moves nothing reports `RecordsProcessed = 0`. That is a healthy result, not a
fault.

### Row rejection

A value that cannot be represented — over-long string, unparseable date, decimal overflow —
**drops that row, counts it and reports it**, rather than being truncated or clamped. An
over-long string is named by column, because SqlClient's own "String or binary data would be
truncated" names none. A silently clamped P&L number is far worse than a missing row.

### Timeout budgets

⚠ `HttpClient.Timeout` bounds a whole **retried** operation, because the Polly handler runs
inside it. So the client timeout is `InfiniteTimeSpan` and the real budget lives **per
attempt** inside `NucoreHttp.Build` (`retry.WrapAsync(timeout)`), with
`TimeoutRejectedException` in the retry predicate. This is the trap EIA hit, where a vendor
504 surfaced as a client timeout.

The budgets are coupled: `(RetryCount + 1) × HttpTimeoutSeconds` plus backoff must stay under
`WorkUnitTimeoutSeconds`. `NucoreSinkAndPolicyTests` asserts it — and that test caught
`NucoreSettings` inheriting the base 300 s default while its own worst case was 1200 s.

---

## 5. Write modes

> **Corrected 2026-10-18 after a production failure.** The loader originally merged four of
> the five tables on natural keys. Two of those keys do not identify a row, and
> `PositionDetails` died on `Violation of PRIMARY KEY constraint 'PK_PositionDetails'`. See
> `docs/apis/NUCORE.md` §5 for the evidence and
> `sql/NUCORE/hotfixes/2026-10-18_RemoveUntrustworthyKeys.sql` for the migration.

| Table | Mode | Scope / key |
|---|---|---|
| `VarResults` | Merge | natural key, verified over 7 complete days |
| `Volatilities` | Merge | natural key, verified over 5 complete days (712k rows) |
| `PositionSummary` | ReplaceByDate | `(SystemDate)` |
| `PnlExplain` | ReplaceByDate | `(CurDate)` |
| `PositionDetails` | ReplaceByScope | `(TradeMonth, SystemDate)` |

A replace procedure DELETEs the scopes present in its batch and then INSERTs, inside a
transaction, so a partial load cannot blank data it was not given.

### ⚠ The assumption a replace depends on

**A scope must never be split across two write calls.** If it were, the second call's DELETE
would remove the rows the first had just INSERTed — and the run would report success with a
silently short table. Two mechanisms keep that true, and a third catches it if they fail:

- a `ReplaceByDate` unit is a single scope, so the **whole unit is written as one batch**,
  regardless of `BatchSize` (days are ~13,600 and ~11,400 rows; `MaxUnitBufferRows` fails
  loudly rather than splitting if one ever grew past 500,000);
- a `ReplaceByScope` unit flushes **only on a `SystemDate` boundary**, which is safe because
  the response is sorted by `-systemDate`, so a date's rows are contiguous;
- `NucoreSourceReader` tracks every scope it has written in the unit and **throws** if one
  repeats.

Verified live: trade month 202510 read 158,975 rows over 8 pages into 8 batches covering 386
distinct scopes, **zero split**; 202509 likewise, 380 scopes, zero split.

The replace tables carry a `BIGINT IDENTITY` primary key only so that they have one. It has
no business meaning and is **not stable across reloads**, so nothing downstream may join on
it. `ModifiedAtUtc` on those tables means "when this scope was last loaded", not "when this
row last changed" — there is no row identity to track change against.

Each replace table is clustered on its scope (`CIX_<Table>`) so the per-batch DELETE is a
range operation. For `PositionDetails` that index, `(TradeMonth, SystemDate)`, doubles as the
watermark lookup, which is why it has no separate non-clustered index.

The sink's `SqlWriteGate` keys on the procedure name, so the several units of one endpoint
serialise on that endpoint's write while the other four proceed in parallel — which is what
keeps five concurrent bulk writes from deadlocking.

---

## 6. Operating it

```powershell
# One endpoint, one day, against a DEV database first.
DataLoader.Host.exe NUCORE
```

Settings live under `Loaders:NUCORE` in `src/DataLoader.Host/appsettings.json`.

| Setting | Shipped | Why |
|---|---|---|
| `PageSize` | 20000 | ⚠ **not** 500000 — `PositionDetails` answers 504 at that size |
| `StartDate` | 2025-01-01 | full available history |
| `SettledAfterDays` | 3 | last 3 business days re-read each run |
| `SettledTradeMonthsBack` | 3 | plus every future trade month |
| `HttpTimeoutSeconds` | 300 | **per attempt** |
| `WorkUnitTimeoutSeconds` | 1800 | must exceed `(RetryCount+1) × HttpTimeoutSeconds` |
| `MaxConcurrentWorkUnits` | 4 | |

### Expected cost

- **First run (full backfill):** ~470 business days × 4 date-partitioned endpoints, plus ~84
  trade-month units. The dominant cost is `PositionDetails` — the whole 2.47M rows must be
  read once, at roughly 20,000 rows per ~30 s page.
- **Steady state:** 3 hot days × 4 endpoints (~0.5M rows), plus one watermark check per trade
  month returning only the newest system date's rows. Settled units cost nothing.

### Deployment status

**Build-only.** `NUCORE` is not in `Platform:EnabledLoaders`, the SQL has never been run, and
no `NUCORE` database exists. The read path is live-verified end to end (225,677 rows, zero
dropped, `docs/apis/NUCORE.md` §7); the **write path has never executed**.

To deploy: run `sql/NUCORE/001` → `002` → `003` against the target server (001 creates the
database), grant the app-pool service account rights, resolve the API key into `core.Param`,
then add `NUCORE` to `Platform:EnabledLoaders`.

---

## 7. Known gaps

- **No post-load validator.** Other loaders ship `usp_ValidateLoad`; NUCORE does not yet,
  because there is no loaded database to validate against. When one exists, every check must
  be **bounded** — Criterion's unbounded orphan check expired at the 30-second default and
  took every other check in the proc down with it while the run still reported success.
- **No arm-to-dbo comparison (`004`).** There is no incumbent `dbo` dataset to shadow: the
  loader writes `dbo` directly in a new database, as specified.
- **Holidays are not predicted.** They arrive as legitimate empty reads, which is correct but
  means a genuine publication failure on a business day looks the same as a holiday. A
  calendar, or an alert on consecutive empty business days, would close that.
- **`PositionDetails` trade-month enumeration is configured, not discovered.** The window is
  `TradeMonthFrom` … today + `TradeMonthsAhead`. A trade month outside it is never read. The
  shipped 84 months ahead comfortably covers the 2031 maximum seen in live data.
