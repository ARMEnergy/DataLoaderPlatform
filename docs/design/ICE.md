# ICE loader — design

Source contract: `docs/apis/ICE.md` (verified live 2026-09-01).

Database `ICE`, schema `arm`. 21 file feeds → 14 fact tables, one pipeline per feed.

---

## 1. Shape

```
IceModule
  ├─ IceSsoAuthenticator      one cached token, refreshed on the login sentinel
  ├─ IceFileCache             download-once-per-day local folder + retention sweep
  └─ 18 × IcePipeline (LoaderPipelineBase<IceWorkUnit, IceRow, IceRow>)
        ├─ IceWorkUnitProvider   trade dates in the DaysBack window
        ├─ IceSourceReader       fetch → classify → parse → IceRow[]
        └─ IceTableSink          one TVP merge per file
```

The pipelines are **mutually independent**: no barrier, no shared state, no FK.
A failure in one feed must not cost the other 17, so `RunAsync` records the
failure and continues (the Argus posture).

Pipelines are built explicitly rather than through open-generic DI, so this
loader's `IWorkUnitProvider<T>` / `ISink<T>` registrations cannot collide with
another loader's in the shared container.

### 1.1 Descriptor-driven, not 18 hand-written parsers

`IceDescriptors` is the single source of truth. An `IceTableDescriptor` owns the
ordered column list (**the TVP contract**), the TVP type name and the merge proc;
an `IceFeedDescriptor` owns the URL template, the date token format, the file
format and a pointer to its table.

`IceRow` is `object[]` in descriptor order — deliberately **not** 12 per-table
POCOs, which would mean 12 `BuildTable` methods and 12 independent chances for the
DataTable/TVP position drift that `tvp-contract-check` exists to catch. The reader
fills the array from the descriptor and the sink reads it back from the same
descriptor, so the two cannot disagree. What *could* still drift is the descriptor
versus the `.sql`, and `IceTvpContractTests` parses the real
`002_CreateIceTvpTypes.sql` and asserts name + order + type against every
descriptor — so that fails the build, not production.

Six feeds share `arm.Futures` and two share `arm.Options`; they reference the same
`IceTableDescriptor`, which is what guarantees they produce identically-shaped
rows for one TVP.

---

## 2. ⚠ Status classification is content-based (design driver #1)

`downloads.ice.com` answers **HTTP 200 for everything** — data, missing file, and
expired authentication alike (`docs/apis/ICE.md` §2). The status code carries no
information.

`IceResponseClassifier` is therefore the correctness centrepiece:

| Body | Classification | Pipeline outcome |
|---|---|---|
| HTML containing `Index of` + `No Files Available` | `NotAvailable` | success, 0 rows, `FileLog=NotAvailable` |
| HTML containing `ICE SSO Client` | `AuthExpired` | re-auth once, retry; still expired → **fail** |
| Text whose first line matches the feed's expected header | `Data` | parse |
| Text whose first line does **not** match | `Malformed` | **fail** — never parsed |
| `PK\x03\x04` (xlsx feeds only) | `Data` | parse |

The distinction that matters most: **`NotAvailable` (0 rows, success) vs
`AuthExpired`/`Malformed` (failure)**. Recording an auth failure as a clean
zero-row success is the silent-data-loss mode this loader is most exposed to, and
it is the reason classification happens before any parsing and is asserted in
tests against captured copies of all three real bodies.

A real file that legitimately contains only a header row (feed 10 on a closed
index window — 178 bytes) is `Data` with 0 records: success, and distinct from
`NotAvailable`.

### 2.1 Auth refresh

One `IceSsoAuthenticator` shared by all 18 pipelines holds a single token behind a
`SemaphoreSlim`. On `AuthExpired` the reader calls `InvalidateAsync(staleToken)`
and retries once. The stale-token compare-and-swap means 18 concurrent readers
hitting expiry together trigger **one** re-authentication, not 18.

Credentials are `SEE_DB` sentinels resolved from `core.Param`, are never logged,
and never appear in `FileLog.RequestPath`.

---

## 2b. Rate limiting (design driver #2)

ICE allows **30 requests per minute**, account-wide, stated in the body of its own
`429` and confirmed by measurement (`docs/apis/ICE.md` §2a: at concurrency 1, exactly
30 succeed and the 31st is rejected). Exceeding it blocks *every* request for 60
seconds.

```
retry (OUTER, 429 + 5xx/408/network, honours Retry-After)
  └─ rate limit (INNER, paced at RequestsPerMinute)
       └─ HttpClientHandler
```

The throttle sits **inner of the retry** so a retried request waits its turn too —
a retry that jumped the queue would spend the exact budget the backoff is waiting to
recover.

`RequestsPerMinute` defaults to **25** rather than 30: Cloudflare counts over a
sliding window, so pacing at exactly the limit puts boundary requests on the wrong
side of it under clock skew. The four minutes this costs on a cold run buys away an
entire class of failure, and the `Retry-After` backoff still covers anything that
slips through — including requests made by *other* consumers of the same ICE account,
which the client-side limiter cannot see.

**This setting, not bandwidth, governs run duration.** A cold run is
21 feeds × 31 dates = 651 requests ≈ 26 minutes. That is why the local file cache
(§4) matters as much as it does, and why the module logs the request count and
estimated duration at startup — a 20-minute first run otherwise looks like a hang.

---

## 3. Work units and the resume key

One work unit = **one feed × one trade date** = one file = one merge.

```
Key = ice:{FeedId}:{yyyy-MM-dd}[:run={hot}]
```

Window: `[today - DaysBack, today]` in **US Central** (ICE settlement dates are
exchange dates, and Central is the exchange's business day for these products).

### 3.1 Two-zone key

Following the platform convention: a trade date older than `SettledAfterDays` gets
a **stable** key (loaded once, then skipped forever); a newer one gets a
**run-varying** hot key so revisions are picked up.

**With the shipped defaults `DaysBack = 30` and `SettledAfterDays = 30` the settled
zone is empty and every date is hot** — the oldest date in the window is exactly
30 days old and `age > 30` is false. This is deliberate and matches
EvolutionMarkets (30/30) and NGI (60/60): ICE republishes corrected settlements,
so a stable key would freeze a corrected price at its original value while the
loader reported clean runs.

`SettledAfterDays < DaysBack` creates a real settled zone and is logged as a
warning at startup, because it is almost always a misconfiguration rather than an
intent.

The class-default hot key is `RunDate` (UTC `yyyyMMdd`) → one re-pull per UTC day; a
second run the same day idempotently skips. `RunHour` and `RunId` are available for
several-times-a-day schedules and forced re-pulls. UTC, not local: `01:00` Central
occurs twice on a fall-back night, so a local hour token would repeat and a
recorded success would suppress a legitimate re-pull.

**Production ships `LastModified` instead (since 2026-11-10) — see §3.2.**

### 3.2 `LastModified`: key on the file's own version (2026-11-10)

**Why the clock strategies were wrong for this source.** Two faults, both silent:

1. *Late loads.* The schedule is every 30 minutes from 15:03 Central plus 21:30; ICE
   publishes ~17:00–17:50 **ET** (16:00–16:50 Central). The 15:03 run reached the new
   trade date first, found no file, and recorded `NotAvailable` — 0 rows, success —
   under `run=<UTC date>`. Every later run that UTC day skipped it, so the file only
   loaded on the first run after **00:00 UTC = 18:00 Central** (2026-11-09: published
   16:05, loaded 18:33). Same pattern on every weekday checked.
2. *Revisions never re-downloaded.* A clock re-pull reached `IceFileCache`, which
   served the on-disk copy regardless of age (§4). ICE re-published the 09-30 and
   10-02 gas files 8 and 5 days later (`docs/apis/ICE.md` §2b); had those landed after
   caching, the "hot" re-reads would have re-merged the stale bytes forever.

**The key.** Each hot date costs one `HEAD` (`IceHeadVersionProbe`), and the key carries
what it found:

| `HEAD` result | Key suffix | Effect |
|---|---|---|
| `Last-Modified` present | `:lm={yyyyMMddHHmmss}` | this version loaded once; skipped until ICE changes it. The unit's `ExpectedVersion` refuses a cached copy of any other version |
| no `Last-Modified` (not published) | `:lm=none` | **stable**: the first run's `GET` confirms `NotAvailable` through the classifier; later runs skip on the `HEAD` alone, and the day a version appears the key changes |
| `HEAD` failed / still bounced to sign-on after one refresh | `:lm=unknown:run={RunId}` | always downloads — a `HEAD` problem can never hide a file behind a completed key |

**The check window.** At 25 requests/min a `HEAD` of the whole window is 21 × 31 = 651
requests ≈ 26 min — the whole 30-minute interval. So:

* an **ordinary run** checks `[today − VersionCheckDaysBack, today]` (ships **3**: 84
  `HEAD`s ≈ 3.5 min; a Monday reaches back to Friday). Older hot dates are **not
  enumerated** that run;
* a run starting in a **Central** hour listed in `FullVersionCheckHoursCentral` (ships
  **[21]**, the daily 21:30 run, which nothing follows until 15:03) checks the whole
  `DaysBack` window, catching late re-publications.

Settled dates (only when `SettledAfterDays < DaysBack`) keep their stable key, are
enumerated every run and are never probed.

**Guards (from review, 2026-11-10).**
* A `:lm={version}` unit whose `GET` answers "No Files Available" **fails** rather than
  recording a 0-row success — the `HEAD` said that version exists, and a completed key
  would hide it forever. The same key retries next run; if ICE really withdrew the
  file, the next `HEAD` says `Absent`.
* `:lm=unknown` units also **bypass the cache** (`BypassCache`), so a failed `HEAD` can
  never merge a cached copy ICE has since replaced.
* `Absent` is inferred narrowly: no `Last-Modified` header at all **and** no
  `Content-Disposition`. A file-shaped answer with a missing/unparseable
  `Last-Modified` is `Unknown`.
* Each `HEAD` is bounded at **30 s** (the client's 300 s is sized for downloads); after
  **5 consecutive** `Unknown`s the probe stops sending `HEAD`s for the rest of the run
  and every remaining hot date downloads unconditionally — the old behaviour, without
  paying a timeout per date first.
* A **sign-on failure propagates** (the feed fails) instead of becoming `Unknown` —
  swallowed, it would retry the SSO login once per date, 651 times in a full run.

**Open trade-off: the full check is a single run.** Hot dates older than 3 days are
enumerated only by a run starting at 21:xx Central. If that run is overlap-skipped,
starts late, or the host is down, that day gets no full check; and a 4–30-day-old unit
that *fails* in it is not retried until the next evening (under `RunDate` a failed unit
was retried 30 minutes later). A date that fails at age 30 then ages out of the window.
Mitigations if this bites: add a second hour (e.g. `[21, 2]` with a matching trigger),
or make the full check state-based ("none completed in the last ~20 h").

**Residual gaps, accepted.**
* A **corrected** file older than 3 days is picked up at 21:30, not within 30 minutes.
* An **expired** (not garbage) token that made a `HEAD` answer `200` HTML on the
  downloads host *without* redirecting would read as `Absent`. For a date whose
  `:lm=none` key is already complete, that run skips it; the next run (a new process,
  a fresh sign-on) sees the version. Each run is a separate process that signs on at
  first use, so a mid-run expiry is rare and costs one 30-minute cycle.
* ICE can publish between a unit's `HEAD` and its `GET`. The `GET` then serves the
  newer bytes under the older key; the cache sidecar records the version actually
  **served** (the `GET`'s own `Last-Modified`), so the next run's new key is satisfied
  from cache and nothing is downloaded twice.

**One-off cost of switching.** Every existing key is a `:run=` key and no cached file has
a version sidecar, so the first `LastModified` runs re-download what they check: the
first afternoon run ~84 files, the first 21:30 run the rest of the window (~650 `HEAD`s
plus ~460 downloads, ~45 min). That is the same total work as a post-deploy cold cache
used to be.

---

## 4. Local file cache and retention

The user-specified download folder is a genuine cache, not a scratch dir.

* `DownloadDirectory` (default `files/ICE`, relative to the host base directory)
  laid out `<dir>/<FeedId>/<filename>`.
* A file already on disk and non-empty is **reused instead of re-downloaded**
  unless `ForceDownload = true`. With an all-hot 30-day window this is what keeps
  a daily run from re-pulling ~100 MB × 31 days; only genuinely new dates and
  files aged past retention are fetched.
* ⚠ **Version sidecar (2026-11-10).** Each cached file may have a `<file>.version`
  holding the `Last-Modified` it was served as. A unit with an `ExpectedVersion`
  (`LastModified` strategy, §3.2) uses the cached copy **only** when the sidecar
  matches; no sidecar counts as a mismatch. The old sidecar is deleted before a new
  body is moved in and the new one written after, so a crash leaves a file with no
  sidecar (one extra download), never a file paired with the wrong version. Before
  this, a cached file was reused for its whole retention period whatever ICE had
  published since — the clock strategies still behave that way.
* HTML sentinel bodies are **never written to disk** — caching a login page would
  poison every later run for that date.
* `FileRetentionDays` (default 7) sweeps once per run, before enumeration, deleting
  files older than the cutoff by last-write time. The sweep is best-effort: a
  locked or unreadable file is logged and skipped, never fatal.
* Retention shorter than `DaysBack` means dates between the two re-download each
  time they go hot. That is a bandwidth/disk trade-off, not a correctness issue,
  and the shipped 7/30 pair deliberately favours disk.

---

## 5. Parsing

`IceDelimited` handles both text shapes: pipe-delimited with no quoting (the
`.dat` feeds) and RFC 4180 with quoted strings and bare numbers (the Crude Index
`.csv` feeds). One parser, a delimiter parameter, and full quote handling for both
— the `.dat` feeds show no quoting today, but a naively split future value
containing a delimiter would shift columns and corrupt rows rather than fail them.

`IceXlsx` reads OOXML directly with `System.IO.Compression` + `XDocument` — no
package dependency. It resolves shared strings and, critically, maps cells **by
their `r` reference** (`E2` → index 4) so the sparse rows described in
`docs/apis/ICE.md` §4.4 do not shift.

**Mapping is by header name, never by position** (§4.1 of the API doc: file order
and table order genuinely differ). A missing required header fails the whole file.
Columns accept alternate spellings so `PUT/CALL` and `PUT_CALL` both resolve.

### 5.1 Required columns drop rows, they do not fail files

A column marked `Required` is `NOT NULL` in the TVP. A row whose required value is
blank or unparseable is **dropped, counted, and reported** — never merged under a
blank key. This is what handles the ~2–26 % of options rows that describe the
underlying future and carry no strike (`docs/apis/ICE.md` §5.4). The drop count
goes to `FileLog.RowsDropped` and to the log at Information, so a feed that starts
dropping everything is visible rather than silently empty.

---

## 6. SQL

`sql/ICE/001` tables + `arm.Status` + `arm.FileLog`, `002` the 14 TVP types,
`003` `usp_UpsertFileLog` + 14 merge procs + `usp_ValidateLoad`, `999` drop.

Conventions shared with the other loaders:

* **Batch de-dup** in every merge (`ROW_NUMBER() OVER (PARTITION BY <pk>)`).
  Load-bearing: feed 10 genuinely ships exact duplicate rows
  (`docs/apis/ICE.md` §5.3) and `MERGE` errors on a duplicated source key.
* **No delete-by-absence.** Each unit merges one file; a short download must not
  wipe good rows.
* `ModifiedAtUtc` is set explicitly with `SYSUTCDATETIME()` (the table `DEFAULT` is
  `sysdatetime()` — server local — reproduced verbatim from the supplied DDL).
* `ModifiedAtUtc` is never a TVP column.
* Every proc returns `RecordsProcessed`, which `SqlSinkBase` surfaces to
  `core.LoadLog`.

### 6.1 The one deviation from the supplied DDL

`arm.Futures`: `ProductId` is `NOT NULL` and joins the primary key —

```sql
CONSTRAINT PK_ARM_Futures PRIMARY KEY CLUSTERED (TradeDate, Contract, ContractType, Strip, ProductId)
```

Approved 2026-09-01. Without it, ICE's reuse of contract codes across markets
(`OLD` is both an Ontario power future and a NYH heating-oil future) silently
loses ~73 rows per day. Measured impact and the verification that `ProductId`
resolves it completely are in `docs/apis/ICE.md` §5.1.

Everything else — column names, types, nullability, PKs, default-constraint names
and the `ModifiedAtUtc` indexes — is reproduced verbatim.

---

### 6.2 `arm.PhysEnvFutures` — one table, two publishing processes

The `Settlement_Reports/Environmentals` XLSX set (feeds 19-21, added 2026-09-24)
introduces the loader's first table written by feeds from **different sources**:
`icecleared_physenv` and `ngxphysical_env` publish a column-for-column identical
11-column sheet, so they share one table and are told apart by a discriminator.

```sql
CONSTRAINT PK_ARM_PhysEnvFutures PRIMARY KEY CLUSTERED (TradeDate, SourceSystem, Contract, ContractType, Strip)
```

`SourceSystem` (`VARCHAR(20)`, `ICECleared` / `NGXPhysical`) is a new kind of
column for this loader: **loader-derived, like `SourcePath`, but REQUIRED and part
of the key.** It comes from `IceFeedDescriptor.SourceSystem` and is never read
from the sheet — `IceSourceReader.ResolveSourceSystem` throws if a table declaring
the column is paired with a feed that supplies no value, because the alternative
is a blank in a PK column that silently merges the two processes together.

Two consequences worth knowing:

* The two feeds address **disjoint row sets**, so merge order between them is
  irrelevant and no ordering guard is needed — but they do share
  `arm.usp_BulkMergePhysEnvFutures`, and `SqlWriteGate` keys on the proc name, so
  they serialize against each other.
* `arm.usp_ValidateLoad` reports `arm.PhysEnvFutures` row counts **per
  `SourceSystem`** (check 8) and flags any value outside the known set (check 9).
  Because the column is a key component, a mis-wired value would fork the key
  space rather than collide, so nothing else would ever complain.

These tables are **new**, not a replacement: `arm.EnvFutures` / `arm.EnvOptions`
keep loading the `.dat` publication from `Settlement_Reports_CSV`, and nothing
de-duplicates across the two.

---

## 7. Status and known gaps

**Build-only.** SQL is ScriptDom parse-checked but **never deployed**; no row has
been written to a real database. Do not report data validation as passed.

1. Live-verified end to end through *parsing* (all 18 `_CSV` feeds downloaded and
   parsed from real 2026-08-28 responses). The SQL half is unexecuted.
1a. **Feeds 19-21 were never downloaded by the loader.** The three real files were
   supplied directly and parsed offline on 2026-09-24, so the column mapping,
   sparse-cell handling and numeric edge cases are verified against real bytes —
   but the URL template, the entitlement (§1.3 of the API doc lists only
   `SETTLEMENT_REPORTS_CSV_ENVIRONMENTALS`) and the classifier path for this
   directory are all unexercised. One live run is needed.
2. `arm.ICE_Crude_Oil_Index_Trades` merges a cumulative window, so its row count
   grows within an index month and plateaus — normal, not a duplication bug.
3. The loader ships **disabled** (`Platform:EnabledLoaders` unchanged) and must be
   enabled explicitly after the SQL is deployed.
4. Header stability across years is assumed; a changed header fails the file loudly
   (§2 `Malformed`) rather than loading it shifted.
