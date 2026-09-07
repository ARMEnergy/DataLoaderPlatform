# EOX loader — design

Loader id `EOX`. Source: `docs/apis/EOX.md`. Database `EOX`, schema `arm`.

Three end-of-day broker curve series on one FTP drop — crude oil, natural gas and
NGL — one CSV per trading day per series, into three tables the requester
specified verbatim.

---

## 1. Shape

```
EoxListingCache  ── one LIST of the 18k-entry root, per run, shared by all three
       │
       ├─ EoxWorkUnitProvider(CrudeOil)   ─┐
       ├─ EoxWorkUnitProvider(NaturalGas) ─┤  one unit per (feed × curve date)
       └─ EoxWorkUnitProvider(NGL)        ─┘  that the drop actually has a file for
                                              │
   EoxSourceReader (one instance per feed) ───┤  download → FileLog → parse
                                              │
   EoxTableSink (one per feed)  ──────────────┘  TVP → arm.usp_BulkMerge<Feed>
```

Three independent `LoaderPipelineBase` pipelines, built explicitly in
`EoxModule` (the OPIS/Argus posture) rather than through open-generic DI, so this
loader's `IWorkUnitProvider<T>` and `ISink<T>` cannot collide with another
loader's in the shared container.

The pipelines share no state and have no ordering dependency. A feed that fails is
recorded and the run continues — losing the NGL file must not cost the other two.

### 1.1 Why one descriptor-driven reader instead of three row classes

The three feeds differ only in their column list. Three POCOs would mean three
`BuildTable` methods — three independent chances for the DataTable/TVP position
drift that `tvp-contract-check` exists to catch, and that SQL Server does not
report.

Instead `EoxDescriptors` holds one ordered `EoxColumn` list per feed, and that
single list drives four things at once:

1. which CSV header(s) the reader looks for,
2. how each cell is converted and to which CLR type,
3. the DataTable's column name, order and type in the sink,
4. the expected TVP column in `sql/EOX/002`, asserted by `EoxTvpContractTests`.

The reader fills `EoxRow.Values` in descriptor order; the sink reads it back in
descriptor order. Those two **cannot** drift from each other. What still could
drift is the descriptor versus the `.sql` — so the tests parse the real `.sql`
file and compare name, order, SQL type and nullability. Changing either side
alone fails the build.

`EoxDescriptors.Validate()` additionally runs at module startup and rejects a
registry where a key column is not `Required`, where `Required` and
`HeaderOptional` are both set, or where the last column is not the derived
`FileName` guard.

---

## 2. Tables

As specified by the requester, with the deviations below.

```
arm.CrudeOil    PK (CurveDate, LocationCode, TimeKey)   ~20,800 rows/day
arm.NaturalGas  PK (CurveDate, MarketCode,   TimeKey)   ~34,000 rows/day
arm.NGL         PK (CurveDate, LocationCode, TimeKey)    ~4,560 rows/day
```

### 2.1 Deviations from the supplied DDL

| Change | Why |
|---|---|
| `DEFAULT (SYSUTCDATETIME())` instead of `SYSDATETIME()` on `ModifiedAtUtc` | `SYSDATETIME()` is server-**local** time and contradicts the column's own name. Every other loader here stamps UTC, and the merge procs set the column explicitly — a local-time DEFAULT would make inserted rows disagree with updated ones. `EoxTvpContractTests` pins UTC on both sides so a later edit cannot reintroduce the mismatch on one side only. **One word to revert if local time is genuinely wanted.** |
| One non-clustered index per table on `(CurveDate) INCLUDE (…)` | Every consuming query is "give me a curve date". |

Nothing else was added: no `FileLogId`, no `SourceFileDate`, no surrogate key.

### 2.2 Added by this loader

`arm.Status` and `arm.FileLog` — the repo's standard audit hub, one row per source
file for every outcome (`Success` / `NotAvailable` / `Failed`), keyed on
`FileName`. It records `FeedId`, `CurveDate`, the server-reported
`RemoteLastModifiedUtc` / `SizeBytes`, the row count, any error, and the FTP path
(**host + path only — never credentials**).

The fact tables carry no FK to it: the supplied DDL gives them a `FileName` column
instead, so provenance runs through the name. That column does double duty as the
merge ordering guard (§4.2).

### 2.3 ⚠ Why there is no `SourceFileDate` ordering column

Every other file-based loader in this repo carries one. This one does not need it:

1. `Curve_Date` inside a file always equals the `yyyyMMdd` in its name (verified on
   every row of 25 files spanning 2011‑2026). `CurveDate` leads every primary key,
   so two **different** files write **disjoint** key sets and cannot race.
2. When the same file is republished, `FileName` gives a deterministic winner —
   see §4.2.

Both the reader and `arm.usp_ValidateLoad` report any violation of (1), and the
merge guard means a violation degrades to "newest file wins" rather than to an
arrival-order race. See `docs/apis/EOX.md` §4.

---

## 3. Extraction

### 3.1 Work units and the resume key

One work unit = one feed × one curve date = one file = one merge.

```
settled:  eox:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}
hot:      eox:{FeedId}:{yyyy-MM-dd}:{lastModifiedUtc}:{size}:run={token}
```

The key composes **two** mechanisms, covering different failure modes:

* **Content stamp** (`lastModifiedUtc` + `size`, from the one directory listing).
  EOX revises by overwriting a file in place, so a republished file gets a new key
  and reloads **at any age**, while an unchanged file is skipped however often the
  loader is scheduled. This is the OPIS/Argus posture, and it is what makes a
  settled date cheap.
* **Hot suffix** (`SettledAfterDays`). Forces a re-pull inside the hot window even
  when the stamp has not moved — insurance against a republish that somehow
  preserved both mtime and size. This is the ICE/EvolutionMarkets/NGI posture, and
  it is what `SettledAfterDays` controls.

`HotKeyStrategy` picks the token: `RunDate` (default, one re-pull per UTC day),
`RunHour`, or `RunId` (every invocation). All tokens are **UTC** —
`yyyyMMddHH` is strictly monotonic only in UTC, because 01:00 Central happens
twice on a fall-back night and a repeating token would make the key go backwards.

`ForceReprocess` salts **every** key, hot or settled, with the run id.

### 3.2 The window, and why it costs what it costs

`[today − DaysBack, today]` inclusive, in **US Central** — 14:30 Central is the
snap these files are named for, and enumerating in UTC would ask for "today"
hours before Central has one, producing pointless `NotAvailable` rows every
evening. `EoxTime` falls back to UTC with a startup warning if the host has no
Central time-zone entry, rather than failing the load.

Both settings are clamped at 0. A negative `DaysBack` would invert the window and
produce a loader that silently does nothing; a negative `SettledAfterDays` would
settle the entire window.

⚠ **With the shipped `DaysBack = SettledAfterDays = 30`, the settled zone is
empty and every date is hot** — the oldest date is exactly 30 days old and
`age > 30` is false. That matches ICE (30/30), EvolutionMarkets (30/30) and NGI
(60/60), and it is deliberate: a stable key would freeze a corrected curve at its
original value while the loader reported clean runs.

It also means **the whole 30-day window re-downloads on every run day**: 3 feeds ×
31 dates = 93 files ≈ 230 MB. Because the content stamp already detects a
republished file on its own, **lowering `SettledAfterDays` to 2 or 3 makes
steady-state runs download only what actually changed** without losing revision
detection. That is a config change, not a code change, and the module logs the
choice at startup.

### 3.3 File selection

The provider **constructs** the expected name — `{FilePrefix}{yyyyMMdd}_{token}.csv`
— and looks it up in the cached listing. It never globs.

That single decision excludes, with no extra rules: the `.xls`/`.xlsx` twins, the
`EOD_CSV_20YR_NG_*` monthly series, and the ~19 Dropbox
`…(HOST's conflicted copy DATE).csv` artefacts that share both the feed prefix and
the date. See `docs/apis/EOX.md` §1.

A date with no file yields **no work unit** and one `NotAvailable` hub row. EOX
publishes on trading days only; guessing at a holiday calendar risks skipping a
day EOX *did* publish, which is the failure mode that actually loses data.

### 3.4 Parsing

Failure policy, in order of severity:

| Situation | Outcome |
|---|---|
| Empty file, or a header missing a **required** column | throw — the file fails, nothing is merged, `arm.FileLog` records why |
| Header missing an **optional** column (`FP` only) | that column is NULL for every row; the file loads |
| Row with the wrong field count | drop the row, count it, warn once |
| Blank/unparseable **required** column | drop the row — it cannot be keyed |
| Blank/unparseable optional column | store NULL, count as degraded |
| `CurveDate` ≠ the file name's date | **keep the row** (the file's own value is authoritative), count and warn |

One malformed line never costs the rest of the file. Contract drift is always
loud, because the TVP binds by position and a guess writes plausible garbage.

Column resolution is by **name**, trying each column's accepted headers in
preference order — `Line`/`Number`, `Data_Code`/`Code`. Preference order matters
where a file could carry both spellings: NGL prefers `Data_Code`, CrudeOil prefers
`Code`, matching what each actually publishes.

Two-digit years are widened to `20yy` before parsing
(`EoxCsv.ExpandTwoDigitYear`) — see `docs/apis/EOX.md` §3.3 for the 1950 trap this
avoids.

---

## 4. Loading

### 4.1 One TVP and one merge proc per feed

`arm.CrudeOilTvp` → `arm.usp_BulkMergeCrudeOil` → `arm.CrudeOil`, and likewise for
the other two. Distinct procs are load-bearing: `SqlWriteGate` keys on the proc
name, so two units of the same feed serialize on that feed's merge while the other
two feeds proceed in parallel — and none of them contends with
`arm.usp_UpsertFileLog`, which has its own key.

Each TVP carries the whole row except `ModifiedAtUtc`, in table column order, with
`FileName` last and `NOT NULL`.

### 4.2 The ordering guard

Every UPDATE branch is guarded by

```sql
WHEN MATCHED AND src.FileName >= ISNULL(tgt.FileName, '')
```

and every batch de-dup orders `FileName DESC`.

Within one feed the names are `<fixed prefix><zero-padded yyyyMMdd><fixed suffix>`,
so **lexicographic order is chronological order**. The comparison is `>=`, not
`>`, so a same-name republish — which is EOX's revision mechanism — still applies.
`ISNULL(…,'')` makes a pre-existing row with a NULL `FileName` always lose rather
than being left permanently un-updatable on a three-valued comparison.

In practice cross-file collisions cannot occur at all (§2.3). The guard exists so
that if that assumption ever fails, the result is deterministic instead of
arrival-order dependent.

### 4.3 No merge deletes by absence

Each work unit carries one day's snapshot for one feed, so a `NOT MATCHED BY
SOURCE` branch would wipe every other day in the table. No proc has one, and a
test asserts that over the comment-stripped script (the rule is also documented in
prose, and a second test proves the first is not passing on that prose).

### 4.4 Concurrency

`MaxConcurrentWorkUnits = 4`. Work units complete in any order —
`ParallelRunner` makes no promise — so correctness rests on §4.2, never on the
newest-first ordering the provider returns (which exists only so the day people
care about lands first if a run is cut short).

---

## 5. Validation

`arm.usp_ValidateLoad(@CurveDate)` returns one result set in the repo's uniform
shape (`CheckName, Scope, ExpectedCount, ActualCount, Detail`).
`EoxLoadValidator` logs rows with a stated expectation that misses as warnings and
the rest as counters, and **never throws** — EOX publishes on trading days only, so
a Saturday run legitimately adds nothing and must not read as a failed load.

Checks: per-table row counts; `CurveDateDoesNotMatchFileName` (expect 0 — the §2.3
assumption); `FileNameDateUnparseable` (expect 0 — invalidates the previous check
if non-zero); `RowsWithoutFileName` (expect 0); `CrossedMarkets` (Bid > Ask —
a **counter**, not an expectation: thin back months really do cross);
`RowsWithNoPriceAtAll` (expect 0); `FileLogFailed` (expect 0);
`FileLogNotAvailable` (counter — weekends are normal); `FileLogRowCountMismatch`
(expect 0); and per-curve-date row counts (counters).

Deeper reconciliation is `DATA_QUALITY_VALIDATOR`'s job and needs a live loaded
database.

---

## 6. Configuration

`Loaders:EOX` in `src/DataLoader.Host/appsettings.json`.

| Setting | Default | Note |
|---|---|---|
| `FtpHost` / `FtpPort` | `ftp.eoxlive.com` / 21 | |
| `Username` / `Password` | `SEE_DB` | resolved from `core.Param(LoaderName='EOX')`; never logged, never in `RequestPath` |
| `RemoteDirectory` | `/` | EOX drops everything in the root |
| `UseFtps` | **`true`** | differs from OPIS/Argus. The server advertises `AUTH TLS` and list+download over explicit TLS were verified live; plain FTP would send the password in cleartext. Turning it off logs a warning. |
| `ValidateAnyCertificate` | `false` | strict — the live certificate validated. Escape hatch if a deployment host trips on the chain. |
| `FtpTimeoutMs` | 180000 | the root listing is 1.6 MB and the NG file ~4 MB |
| `DaysBack` | 30 | window is `[today−30, today]` US Central |
| `SettledAfterDays` | 30 | see §3.2 — **this is the run-cost dial** |
| `HotKeyStrategy` | `RunDate` | `RunDate` / `RunHour` / `RunId` |
| `ForceReprocess` | `false` | salts every key for a forced re-merge |
| `FileNameTimeToken` | `1430` | a constant on the live drop; configurable only against a future second daily snap |
| `EnabledFeeds` | all three | an unknown id warns and is ignored |

`EOX` is **not** in `Platform:EnabledLoaders` — run it explicitly with
`DataLoader.Host.exe EOX` or `EOX.cmd`.

---

## 7. Status and known gaps

**Build-only.** Solution builds clean; 119 EOX tests and 2,342 solution-wide tests
pass; all four `.sql` scripts parse clean under ScriptDom (TSql160).

Live-verified: the FTP connection (plain and TLS), the directory census, the CSV
dialect, the header drift, key uniqueness, and the full parse path — all 25
downloaded files, 2011‑2026, parsed through the real `EoxSourceReader` with zero
dropped rows, zero degraded cells and zero warnings.

**Not verified — the SQL has never been deployed.** No `EOX` database exists.
ScriptDom checks syntax only: it cannot catch a missing object, a wrong column
count, or a type mismatch. The merge behaviour, the `FileLog` upsert and the
validator's result-set shape all need a real deployment before anyone should call
them working.

Open risks:

* **`ValidateAnyCertificate = false` is verified with curl's CA bundle, not .NET's.**
  .NET on Windows validates against the machine store. If the first live run fails
  on the TLS handshake, that is the setting to flip.
* **The 30/30 default re-downloads ~230 MB per run day** (§3.2). Correct, but worth
  a deliberate decision before scheduling this hourly.
* **`FileNameTimeToken` is assumed constant.** If EOX ever adds a second daily
  snap, the `usp_ValidateLoad` file-name-date checks — which read an 8-digit token
  16 characters from the end — start reporting `FileNameDateUnparseable` rather
  than failing silently. That is the intended signal.
