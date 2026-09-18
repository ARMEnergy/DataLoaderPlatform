# Genscape loader — design of record

**Loader id:** `Genscape`
**Database:** `Genscape` · **Schema:** `arm` · **Tables:** two
**Source:** Genscape Oil Fundamentals API v1, `https://api.genscape.com/oil-fundamentals/v1`
(HTTPS REST, JSON, `Gen-Api-Key` header)
**Field reference:** [`docs/apis/Genscape.md`](../apis/Genscape.md) — source facts live there and
are not restated here.
**Status:** **build-only.** Builds clean, 146 loader tests green (2,608 solution-wide), SQL
parse-checked, and the **read path verified against the live API**. **Not** in
`Platform:EnabledLoaders`; the SQL has never been deployed and no row has been merged.

---

## 0. Summary and the five decisions that shape everything

| # | Decision | Because |
|---|---|---|
| D1 | **`Year` and `Week` are DERIVED from `ReportDate`, in C#** | The storage endpoint returns a week-of-**month**. Deriving in the merge proc instead would make a PK component depend on the session's `DATEFIRST` (§2) |
| D2 | **A work unit is a date RANGE × request region, not a day** | The API rejects `startDate == endDate` outright, and the data is weekly — 31 day-units per region would be 31 requests for 4 report dates (§3) |
| D3 | **A response at the 5,000-row cap is DISCARDED and the window bisected** | The cap is silent and drops the **oldest** rows; accepting it would load partial history as if complete (§4) |
| D4 | **`endDate` is sent as the window's last day + 1** | The API window is `[start, end)`. Sending the last day drops the newest report on every run (apis §3.1) |
| D5 | **Both request regions are read, and their overlap is tolerated** | Each returns rows the other does not; the ~40 shared keys per window were verified value-identical (apis §7) |

### Deviations from the supplied DDL

**Both tables are reproduced VERBATIM** — every column name, type, nullability, the primary key
column order (transportation keys `Type` before `Region`, which is *not* the column declaration
order) and the `DEFAULT (sysdatetime())` semantics. The only additions are:

- **+** a NAME for each `ModifiedAtUtc` default constraint. An unnamed default gets a random system
  name that cannot later be dropped by name.
- **+** one nonclustered index per table on `(Region, …, ReportDate)`. The clustered PK already
  serves date-range scans since `ReportDate` leads it; this covers the region-first slice. Purely
  additive.

One oddity is kept **on purpose** rather than silently fixed: `ModifiedAtUtc DEFAULT
(sysdatetime())` is server **local** time in a column named `...Utc`. Both merge procs set
`SYSUTCDATETIME()` explicitly, so the default only affects rows inserted by something else. Same
posture as `sql/ICE/001` and `sql/Criterion/001`.

---

## 1. Component layout

```
GenscapeModule            plugin entry point; DI, HttpClient, the two pipelines, startup warnings
  GenscapeWorkUnitProvider   window -> chunks x regions; assigns hot/settled resume keys
  GenscapeSourceReader       one HTTP request per window; status matrix; cap bisection
    GenscapeFeedDescriptor     per-feed JSON DTO + positional projection to GenscapeRow
      GenscapeTime               week/year derivation, ISO formatting, report-date parsing
      GenscapeProblemDocument    RFC 7807 summarisation for 400 messages
  GenscapeTvpSink            DataTable -> TVP -> merge proc (SqlSinkBase, SqlWriteGate)
  GenscapeLoadValidator      post-run arm.usp_ValidateLoad, observational only
  GenscapeHttpPolicy         transient retry + 429 with Retry-After
```

Everything downstream of the reader is the platform's standard machinery. The vendor's quirks are
confined to `GenscapeSourceReader` (window semantics, status matrix, the cap) and `GenscapeTime`
(the week derivation).

---

## 2. ⚠ The week derivation

The correction the requester asked for, and the one place this loader changes a value the vendor
supplied. Full evidence in [apis §4](../apis/Genscape.md#4--the-week-field-and-why-the-loader-overwrites-it).

Three properties make it safe rather than merely different:

1. **It is a no-op on the transportation feed.** That endpoint already returns the calendar
   week-of-year, and the derived value reproduces it exactly — across a year boundary, on live
   data. That is the evidence the formula is right.
2. **It is computed in C#, never in SQL.** `DATEPART(week, …)` follows the session's `DATEFIRST`,
   which follows the login's default language. `Week` is a **primary-key component**, so a
   Monday-first connection would fork every key and load each report twice.
   `GenscapeDescriptorTests.No_merge_proc_computes_the_week_in_sql` pins that no merge proc ever
   does it.
3. **`Year` is derived too.** `Week` resets on 1 January of the *calendar* year, so a payload
   `year` paired with a derived `week` could produce `(2025, 1)` for a January 2026 report. The
   pair has to come from one source. No observed value changes — this removes the chance of a
   mismatch rather than fixing one.

`arm.usp_ValidateLoad` re-asserts the equality server-side under an explicit `SET DATEFIRST 7`, so
a regression is caught by the data as well as by the tests.

---

## 3. Work units and resume keys

```
one work unit = one FEED x one REQUEST REGION x one window chunk

settled:  genscape:{FeedId}:{Region}:{start}..{end}
hot:      genscape:{FeedId}:{Region}:{start}..{end}:run={token}
```

The window `[today - DaysBack, today]` is **inclusive at both ends**, so the shipped `DaysBack=30`
covers the last **31 days** — which is what was asked for. It is split into chunks of
`WindowChunkDays` (default 365), so with the defaults that is **one chunk per region: four work
units and four HTTP requests for the whole loader**.

A chunk ages by its **newest** day, because one request retrieves the whole chunk: as long as any
day it covers is inside `SettledAfterDays`, the chunk is hot and re-pulled. With the shipped 30/30
defaults **everything is hot** — deliberate, and the same posture as ICE, EvolutionMarkets and
Criterion. The feed is `revision=revised`, so the vendor restates recent weeks; a stable key would
freeze the first value seen while the loader reported clean runs.

⚠ The window bounds are **in the key**. Changing `DaysBack` or `WindowChunkDays` moves the chunk
boundaries, so old keys stop matching and the range re-loads under fresh ones. That is the safe
direction — re-merging is idempotent — but it is a consequence to know about, and
`GenscapeWorkUnitTests.Changing_the_chunk_size_changes_the_keys` pins it.

**Why not one unit per day**, the pattern most loaders here use? Two reasons, both hard: the API
answers `400 [endDate] must be after [startDate]` for a zero-width window, and the data is weekly,
so 31 day-units per region would be 31 requests to retrieve 4 or 5 report dates.

---

## 4. ⚠ The row cap, and why bisection is safe

`GenscapeSourceReader.ReadWindowAsync` compares the count the API **returned** — not the count that
survived parsing, since a record dropped for a blank key still occupied a slot — against
`MaxRowsPerResponse`. At or above it:

1. the response is **discarded whole**, along with its dropped-row and truncation counts;
2. the window is re-read as `[start..mid]` and `[mid+1..end]`.

The halves are **disjoint** and their union is exactly the original window, so bisection can
neither duplicate nor drop a row. A single day still at the cap **throws** — it cannot be split
further, and loading a knowingly truncated window is worse than failing. A depth cap of 16 guards
against a logic error recursing forever; the real terminator is the single-day case.

Verified live on 2026-09-08 with the cap forced to 100: for both feeds the bisected read was
**row-for-row identical** to the single-request read (315 and 385 rows respectively).

---

## 5. Status matrix

Full table in [apis §8](../apis/Genscape.md#8-the-status-matrix). The two that matter to this
design:

- **`200 {"data":[]}` succeeds with zero rows.** A window before the feed's first report (~2010) or
  after the latest published one is a normal outcome, and a backfill would fail on every early
  chunk if it were not.
- **`400`, `401`, `403` and `404` throw without retrying.** A malformed window, a bad key and a
  wrong path are permanent; retrying delays the same failure and, for the auth cases, replays
  rejected credentials at the vendor. Only `408`, `429` and `5xx` are retried — `429` was added
  explicitly because Polly's `HandleTransientHttpError` does not treat it as transient and the API
  sits behind Imperva.

A missing `data` property in a `200` **throws** rather than reading as an empty load: that is a
contract change, and reporting it as a clean empty load would hide it forever.

---

## 6. Safety properties

| Property | How it holds |
|---|---|
| **Idempotent** | Merges key on the full PK; no delete-by-absence; re-running a unit converges |
| **Order-independent** | The two request regions write ~40 shared keys per window with values verified identical |
| **No secret in a URL** | The key is a `Gen-Api-Key` **header**; the factory's URI-logging handlers are removed; the reader logs the path only |
| **No secret in an exception** | Pinned by `GenscapeReaderTests.An_auth_failure_never_echoes_the_key` |
| **Culture-independent** | Dates formatted and parsed invariant; the week derivation is pure arithmetic, pinned under `th-TH` |
| **Session-independent** | `Week` never computed with `DATEPART` outside `usp_ValidateLoad`, which pins `SET DATEFIRST 7` |
| **Thread-safe** | One reader instance serves every concurrent unit of its feed and holds no per-request state |
| **Bounded** | Work units are bounded by `WindowChunkDays`; responses by the vendor's own 5,000-row cap |

### Why there is no batching sink

Every other TVP sink in this repo splits large writes. This one does not need to: a work unit's row
count is bounded by the response cap times the number of bisection leaves, and at the observed
density (~460 rows per year for the busiest feed/region) even a decade-wide backfill chunk is a few
thousand rows. A batching layer here would be untested machinery guarding a case that cannot arise.

---

## 7. Configuration

| Setting | Default | Note |
|---|---|---|
| `ConnectionString` | `…Database=Genscape…` | Destination |
| `BaseUrl` | `https://api.genscape.com/oil-fundamentals/v1` | Trailing slash tolerated |
| `ApiKey` | `SEE_DB` | `core.Param(LoaderName='Genscape', ParamName='ApiKey')` |
| `Revision` | `revised` | The only accepted value; a change is warned about at startup |
| `Regions` | `[NorthAmerica, GulfCoast]` | REQUEST regions, **not** row regions |
| `HttpTimeoutSeconds` | `120` | |
| `DaysBack` | `30` | ⇒ a 31-day inclusive window |
| `SettledAfterDays` | `30` | ⇒ the settled zone is empty; everything is hot |
| `HotKeyStrategy` | `RunDate` | One re-pull per UTC day |
| `WindowChunkDays` | `365` | ~10× headroom under the row cap at observed density |
| `MaxRowsPerResponse` | `5000` | Raising it above the real cap re-admits silent truncation |
| `EnabledFeeds` | both | |

`GenscapeModule.WarnAboutConfiguration` warns (never throws) about: an unresolved `SEE_DB` ApiKey,
a `Revision` other than `revised`, unknown or empty `Regions`, `SettledAfterDays < DaysBack`, a
negative `DaysBack`, a `MaxRowsPerResponse` above 5,000, and a `WindowChunkDays` beyond ~10 years.

---

## 8. Post-load validation

`arm.usp_ValidateLoad`, called after every run and **observational only** — it swallows its own
failures and never changes the run's outcome.

| Check | Severity | Catches |
|---|---|---|
| `EmptyOilFundamentals` | WARN | A table with no rows at all |
| `OilFundWeekDrift` | **ERROR** | Any row whose `Year`/`Week` is not `YEAR(ReportDate)`/`DATEPART(week, ReportDate)` — i.e. the loader started trusting the payload again |
| `OilFundForkedWeek` | **ERROR** | One business key carrying two `(Year, Week)` pairs — the derivation changed and the merge inserted beside the old rows instead of updating them |
| `StaleOilFundamentals` | WARN | No report date within 21 days — the feed stopped, the key lapsed, or the window shrank |
| `FeedDateGap` | INFO | A report date in one table but not the other — usually one pipeline failed while the run still reported success |

The proc pins `SET DATEFIRST 7` before any `DATEPART(week, …)`, so the check means the same thing on
a server whose default language is not `us_english`.

---

## 9. Verification status

| Gate | Status |
|---|---|
| `dotnet build DataLoaderPlatform.sln -c Release` | ✅ 0 errors |
| `dotnet test tests/DataLoader.Genscape.Tests` | ✅ **146 passed**, 0 failed |
| `dotnet test DataLoaderPlatform.sln -c Release` | ✅ **2,608 passed**, 0 failed — no regressions |
| SQL parse-check (ScriptDom, TSql160) | ✅ 4 / 4 files clean; checker confirmed non-vacuous |
| TVP contract (descriptor ↔ 001 ↔ 002 ↔ 003 ↔ DataTable) | ✅ enforced by the build |
| **Live API: read path, all 4 feed × region combinations** | ✅ arity, CLR types, key uniqueness, widths, derived week |
| **Live API: bisection equivalence** | ✅ row-for-row identical to the single-request read |
| SQL deployed to a live database | ❌ **never** |
| Data loaded / data-quality validated | ❌ **never** — no target database exists |

**Do not report data validation as passed.** The read half of this loader is proven against the
live API; the write half has been parse-checked and contract-tested but has never touched a SQL
Server.

### To deploy

```bash
# 1. create the database, then in order:
sqlcmd -d Genscape -i sql/Genscape/001_CreateGenscapeSchema.sql
sqlcmd -d Genscape -i sql/Genscape/002_CreateGenscapeTvpTypes.sql
sqlcmd -d Genscape -i sql/Genscape/003_CreateGenscapeProcedures.sql

# 2. store the API key (never in appsettings.json)
#    core.Param(LoaderName='Genscape', ParamName='ApiKey', ParamValue='<key>')

# 3. first run
Genscape.cmd            # or: DataLoader.Host.exe Genscape
```

The loader is deliberately absent from `Platform:EnabledLoaders`, so a scheduled
`DataLoader.Host.exe` with no arguments will not run it until someone adds it.
