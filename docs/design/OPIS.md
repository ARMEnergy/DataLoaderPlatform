# OPIS loader — flow and design

Implementation spec for `src/DataLoader.OPIS/`. Field-level source detail lives in
[`docs/apis/OPIS.md`](../apis/OPIS.md); schema in [`sql/OPIS/`](../../sql/OPIS/).

**Status:** built, unit-tested (52 tests), and smoke-tested end to end against the
live FTP drop. The **SQL merge is the one unverified link** — no database was
reachable from the build environment. See §10.

---

## 1. Shape at a glance

| | |
|---|---|
| Loader id | `OPIS` |
| Source | plain FTP, `ftp.opisnet.com:21`, ~24 daily CSVs in the root |
| Database / schema | `OPIS` / `arm` |
| Pipeline | ONE, over `LoaderPipelineBase<OpisWorkUnit, OpisLpReportRow, OpisLpReportRow>` |
| Work unit | **one file** |
| Transformer | `IdentityTransformer` — the reader already emits the final row type |
| Sink | ONE, calling ONE proc that writes BOTH fact tables |
| Audit hub | `arm.FileLog`, one row per file, every outcome |

This is the platform's **first FTP loader**. Platts speaks SSH SFTP via SSH.NET
and cannot serve this feed; `System.Net.FtpWebRequest` is obsolete (SYSLIB0014)
on net8.0. Hence `FluentFTP` 53.0.1 and a new `OpisFtpFileSystem : IFileSystemDriver`.

---

## 2. Decisions and why

1. **One work unit = one file.** The natural grain: files are independent, ~14 KB,
   and individually re-downloadable. A failure isolates to one day.
2. **One sink, one proc, two tables.** `arm.LPReport` and `arm.LPReportHistory`
   take an *identical* payload and differ only in grain, so a single TVP carries
   the parsed file and `arm.usp_BulkMergeLPReport` fans it into both **inside one
   transaction**. The two tables therefore can never drift apart, and a failure
   leaves neither half-written. This deviates from the repo's usual one-proc-per-
   table shape; the atomicity is worth it and the fan-out is documented at both ends.
3. **`Price` is a status code, not a number** — `I` = initial, `U` = revision. It
   is the fourth key column on the history table precisely because a `U` row
   restates an existing `(Mkt_Prod, Date, Timing)` with different prices (§4).
4. **`SourceFileDate` is a payload column, not a key.** It carries the file's
   publication date and exists solely as the merge ordering guard (§5).
5. **Go-forward, no backfill.** The drop only ever holds ~a month; there is no
   archive to walk. `DaysBack = 0` (the default) processes everything present.

---

## 3. Flow

```
OpisWorkUnitProvider.GetWorkUnitsAsync
  → FTP LIST  /  matching *LP.csv
  → for each file: parse yyyyMMdd out of the name  (no date -> SKIP + warn)
  → optional DaysBack filter (0 = take everything)
  → sort ASCENDING by file date; remember the max for validation scope
  → one OpisWorkUnit per file

per work unit (ParallelRunner, MaxConcurrentWorkUnits = 4):
  core.LoadLog.BeginAsync(key)         -- already succeeded? -> SKIP
  OpisSourceReader.ReadAsync
      FTP download -> bytes -> Latin-1 text
      parse: skip header/blank, split, trim, map, drop-and-count bad rows
      arm.usp_UpsertFileLog(Success, rowCount)   -> FileLogId
      stamp FileLogId on every row
      (on ANY failure: arm.usp_UpsertFileLog(Failed, 0, message), then rethrow)
  IdentityTransformer
  OpisLpReportSqlSink.WriteAsync
      SqlWriteGate(arm.usp_BulkMergeLPReport)
      arm.usp_BulkMergeLPReport(@Records)
          BEGIN TRAN
            MERGE arm.LPReportHistory   -- key (Mkt_Prod, Date, Timing, Price)
            MERGE arm.LPReport          -- key (Mkt_Prod, Date, Timing)
          COMMIT
  core.LoadLog.CompleteSuccessAsync / CompleteFailureAsync

after the pipeline:
  OpisLoadValidator -> arm.usp_ValidateLoad(@ReportDate)  -- observational only
```

---

## 4. The two tables, and why the history one needs `Price`

Both tables carry the same columns. They differ only in PK.

| Table | PK | Keeps |
|-------|----|-------|
| `arm.LPReport` | `(Mkt_Prod, [Date], Timing)` | the **current** value — a `U` revision supersedes the `I` it revises |
| `arm.LPReportHistory` | `(Mkt_Prod, [Date], Timing, Price)` | **both** the `I` quote and its later `U` revision — the price-change history |

Live evidence (`docs/apis/OPIS.md` §4): `SARNIA PRO / 07/30/26 / Timing='O'` is
published as `I` (High 81.5000, Avg 81.3750) in `20260730LP.csv`, then republished
as `U` (High **82.0000**, Avg **81.6250**) in the five following files.

The live smoke run over all 24 files produced **4 830 distinct `LPReport` keys**
and **4 831 distinct `LPReportHistory` keys** — the history retains exactly the one
extra row, which is the mechanism working.

Column names deliberately mirror the CSV header (`Mkt_Prod`, `[Date]`, `[Low]`,
`[High]`, `[Avg]`, `[Unit]`…) so the end-to-end mapping is obvious. `[Date]` is
bracketed everywhere because it collides with the type name.

---

## 5. Ordering guard — the correctness crux

`ParallelRunner` runs up to 4 files at once and the loader is re-runnable, so rows
can reach the merge **in any order**. Without a guard, an older file's `I` row
could overwrite a newer file's `U` row in `arm.LPReport` and silently un-revise a
price.

Therefore every `WHEN MATCHED` branch in `arm.usp_BulkMergeLPReport` is guarded:

```sql
WHEN MATCHED AND src.SourceFileDate >= tgt.SourceFileDate THEN UPDATE SET …
```

An older file can never overwrite a newer one. The result is independent of
arrival order, of concurrency, and of how many times the loader re-runs.
Ascending enumeration order (§3) is a cheap best-effort nicety on top; correctness
does not depend on it.

The current-table dedup additionally breaks a same-file tie on `Price DESC`, so
`U` beats `I` deterministically if a future file ever carries both at once.

---

## 6. Resume key (idempotency)

```
opis:{fileName}:{lastModifiedUtc:yyyyMMddHHmmss}:{size}
```

The Platts posture. `core.LoadLog` skips a key already recorded successful, so:

- an **unchanged** file is skipped on every subsequent run, however often the
  loader is scheduled;
- a file OPIS **republishes** gets a new `MDTM` stamp → a new key → it is
  reprocessed, which is how a revision reaches the history table;
- size joins the key as belt-and-braces against a same-second rewrite.

The server's `MDTM` support was verified live, so the stamp is reliable.

---

## 7. Failure handling

| Situation | Behaviour |
|---|---|
| One file fails to download or parse | `arm.FileLog` gets a **`Failed`** row with the message; that work unit fails; **the run continues** with the other files |
| A row cannot be keyed (bad field count / blank PK / bad date) | dropped, counted, logged with the first reason — the file's other rows still load |
| A price cell is non-numeric | stored as NULL, counted, logged — the row is kept |
| A file parses to zero rows | logged as a warning; `FileLog` records `Success` with `RowCount = 0` |
| Weekend/holiday with no new file | nothing to do; not an error |
| Validation query fails | logged, **non-fatal** — it must not fail a good load |

---

## 8. Secrets

`Username` and `Password` are `"SEE_DB"` in `appsettings.json` and resolve at run
time from `core.Param` via `AddLoaderSettings<OpisSettings>`. They reach FluentFTP
only. No code path builds a `ftp://user:pass@host` URI; `arm.FileLog.RequestPath`
stores `ftp://host:port/path`.

---

## 9. Validation (`arm.usp_ValidateLoad`)

Observational, one uniform result set, run after the pipeline by
`OpisLoadValidator`. Checks:

1. `LPReportRowCount` / 2. `LPReportHistoryRowCount` — counters.
3. `CurrentRowsMissingFromHistory` — expect **0**: every current row must have its
   matching history row.
4. `RevisedKeys` — counter: keys whose history holds more than one `Price` code.
   This is the feature working; over the observed month it should be ≥ 1.
5. `PriceBandOutOfOrder` — expect **0**: `Low ≤ Avg ≤ High` where all are present.
6. `RowsWithNoPriceAtAll` — expect **0**.
7. `UntrimmedMktProd` — expect **0**: guards the trim on a key column.
8. `FileLogNotSuccess` — expect **0**: a failed file is a gap in the month.
9. `FileLogRowCountMismatch` — expect **0**: each Success file's `RowCount` equals
   the history rows stamped with its `FileLogId`.
10. `LPReportRowCountForDate` — counter for the run's newest date.

---

## 10. Outstanding

- **Deploy and first load.** Run `sql/Core/001–004` (if not already), then
  `sql/OPIS/001–003`; set the two `core.Param` secrets; add `"OPIS"` to
  `Platform:EnabledLoaders`.
- **The SQL merge is the one unverified link.** Everything upstream of it —
  FTP listing, download, work-unit keys, parsing, row mapping, the TVP DataTable
  contract — has been exercised against live data or unit tests. The two `MERGE`
  statements, the ordering guard and `usp_ValidateLoad` have not run against a
  real SQL Server. First-load checks:
  1. `arm.LPReport` ≈ 4 830 rows, `arm.LPReportHistory` ≈ 4 831 after a full
     month's load (the numbers the smoke run predicts).
  2. `SELECT * FROM arm.LPReportHistory WHERE Mkt_Prod='SARNIA PRO' AND [Date]='2026-07-30' AND Timing='O'`
     returns **two** rows (`I` and `U`); `arm.LPReport` for the same key returns
     **one**, the `U` with High 82.0000.
  3. Re-run the loader immediately — every work unit should report **skipped**,
     and no row's `ModifiedAtUtc` should change.
- **`DATA_QUALITY_VALIDATOR` pass** once data is loaded.
- Longer-window field-domain questions are listed in `docs/apis/OPIS.md` §6.
