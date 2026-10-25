# CLAUDE.md

This file provides guidance to Claude Code when working in this repository.

## 🔒 NEVER WRITE TO `ARMH-OPSDB01` UNLESS SPECIFICALLY ASKED

`ARMH-OPSDB01` is production. **Read it freely; never change it.** This is a standing
rule, not a per-task preference, and it outranks any instruction implied by a task —
"fix it", "apply it", "deploy it", "make it live" are NOT specific requests to write.
A specific request names the write: *"update `Loader\appsettings.json` on prod"*,
*"run `Publish-ToProd.ps1`"*, *"execute that migration against `CWG`"*. Approval for
one write is not approval for the next one.

**Writing** includes all of:
- any file under `\\armh-opsdb01\...`, the admin shares (`c$`, `e$`, `f$`, `g$`), or
  `C:\ETL_Tools\DataLoaderPlatform\**` — including *backups and `.bak` copies*, which
  are still writes to production
- `scripts\Publish-ToProd.ps1`, `-p:DeployToProd=true`, `/publish-to-prod`
- any SQL that is not a `SELECT`: `INSERT`/`UPDATE`/`DELETE`/`MERGE`, any DDL, and
  **`EXEC` of a proc that mutates** (a stored-procedure call is easy to mistake for a
  read — `core.usp_RunAdditionalProcesses` and every `Mako.DLP.merge_*` write data)
- scheduled tasks, services, registry, and env vars on that host

**Reading is expected and encouraged** — `SELECT`s, `sys.*` catalog queries,
`sys.sql_modules` definitions, listing/reading files on the share and under
`C:\ETL_Tools\DataLoaderPlatform\`, and the `Log\<Loader>.log` files. Most production
questions are answerable entirely from those; see the investigation that produced the
`AdditionalProcessesTimeoutSeconds` finding.

When a task genuinely needs a write, **stop and hand the exact command to the user**
rather than running it, and say plainly what it would change.

## Build and Run

```bash
# Build entire solution
dotnet build DataLoaderPlatform.sln -c Release

# Publish deployable artifacts
dotnet publish src/DataLoader.Host/DataLoader.Host.csproj -c Release -o publish

# Run a specific loader
cd src/DataLoader.Host/bin/Release/net8.0
DataLoader.Host.exe EnergyAspects

# Run all enabled loaders
DataLoader.Host.exe
```

Exit codes: `0` success or overlap-skip, `1` error, `2` cancelled.

## Non-negotiable quality gates

- TVP contract must match by NAME + ORDER + TYPE across DataTable and TVP.
- Resume-key semantics must be correct for settled and hot zones.
- Status matrix must distinguish legitimate empty reads from malformed requests.
- Reviewer and tester gates must pass before completion.
- Secrets must use `SEE_DB` or environment overrides and must never be logged.
- SQL changes must be parse-checked; build/test do not compile SQL.

## Architecture summary

- Plugin-based ETL: `DataLoader.<Vendor>` -> `DataLoader.Core` <- `DataLoader.Host`.
- One module per loader: `ILoaderModule`.
- Standard pipeline via `LoaderPipelineBase<TUnit, TItem, TRow>`.
- `SqlSinkBase<T>` handles TVP writes and acquires `SqlWriteGate`.
- Overlap is guarded by SQL app lock per loader.

## Routing policy (default)

Always start with `.claude/skills/loader-routing/SKILL.md`.

### Fast lane
Use for routine changes that do NOT alter endpoint contract or DB shape.

Flow:
1. CODER
2. CODE_REVIEWER
3. CODE_TESTER

### Full lane
Use for new loaders or cross-stage contract changes.

Flow:
1. API_DOCUMENTATION_EXPERT (only if API contract changes)
2. APPLICATION_DESIGNER (only if flow changes)
3. DATABASE_DEVELOPER (only if schema/TVP/proc/key changes)
4. CODER
5. CODE_REVIEWER
6. CODE_TESTER
7. DATA_QUALITY_VALIDATOR (only with live loaded DB)

### Escalation
Escalate from fast lane to full lane if any schema/API/key contract change is discovered mid-run.

## Test execution policy

Always apply `.claude/skills/scoped-test-policy/SKILL.md`.

Default command:
```bash
dotnet test tests/DataLoader.<Vendor>.Tests/DataLoader.<Vendor>.Tests.csproj -c Release
```

Escalate to solution-wide tests only when:
- `src/DataLoader.Core/**` changed, or
- `src/DataLoader.Host/**` changed, or
- shared contracts changed across loaders.

Solution-wide command:
```bash
dotnet test DataLoaderPlatform.sln -c Release
```

## SQL safety rule

For SQL changes, parse-check scripts with ScriptDom before reporting done.
Green build/test does not validate SQL syntax.

```powershell
.\scripts\Test-SqlParses.ps1                  # every .sql in sql/
.\scripts\Test-SqlParses.ps1 -Path sql\NUCORE # one loader
```

It also enforces the `USE <DatabaseName>` rule below. ScriptDom comes from the NuGet
package cache; if it is missing, `dotnet restore` any project referencing
`Microsoft.SqlServer.TransactSql.ScriptDom` once. All 114 `.sql` files in the repo parse
as of 2026-10-16.

**Every `.sql` file must declare its target database** with `USE <DatabaseName>;`
followed by `GO`, before any statement touching a database object. No exceptions
— loader scripts, `sql/Core/`, hotfixes, migrations, one-off scripts. The name is
literal (there is no templating in this repo) and comes from the `Database=` value
in `appsettings.json`, not from the folder name: CME's database is **`CMEGroup`**
and `sql/Core/`'s is **`DataLoaderPlatform`**. These scripts are run by hand in
SSMS against a server holding every loader's database side by side, several with
identically-named objects, so a missing `USE` runs the script wherever the editor
was last pointed. Always apply `.claude/skills/sql-use-database/SKILL.md`.

## arm-to-dbo reconciliation (`004_Create<Loader>Comparison.sql`)

Each loader that shadows an incumbent `dbo` dataset has a 5th SQL file creating
`arm.usp_CompareArmToDbo @Date = NULL, @TablePair = NULL, @MaxRowsPerIssue = 1000`.
It reports `MISSING_IN_DBO` / `MISSING_IN_ARM` / `VALUE_DIFF` (one row per differing
column) plus a per-pair `SUMMARY`, in one uniform result set.

**These files are GENERATED — do not hand-edit the pair list.** Run
`scripts\Generate-ArmToDboComparison.ps1` after any schema change; it resolves every
arm↔dbo pair, join key and date column from the **live catalog**, so the mapping
cannot be invented. Value columns are rediscovered at run time (common columns minus
key, minus audit columns, minus types that `<>` cannot compare), so adding a column
does not stale the proc.

With `@Date` NULL the window is the **arm table's own MIN..MAX** of its date column,
not all of history — `dbo` holds years and `arm` only what the loader has run for, so
an unfiltered compare would report nearly all of `dbo` as `MISSING_IN_ARM`.

Three loaders are deliberately different:
- **Argus** — no comparison is possible: not one table corresponds (dbo is a generic
  time-series model, arm is a single purpose-built fact), and the arm lookups have no
  primary keys. Its 004 file explains this and returns the reason per table.
- **Criterion** — its database has **zero** `dbo` tables; nothing to reconcile.
- **OilX** — the `OilX` database is NEW and holds no incumbent `dbo` dataset, so there
  is nothing to shadow. It ships 001/002/003/999 only.

**CWG stopped being an exception on 2026-10-05.** Its arm tables were reshaped to mirror
the incumbent `dbo` ones (same names, same primary keys, same column names/types), so the
hand-written `sql/CWG/CompareLegacyDboVsArm.sql` was deleted and CWG is now an ordinary
entry in the generator. Four pairs still need a name override (`$TableNameOverride`),
because the arm names describe the breakdown while the legacy dbo names carry a `US`
prefix and a singular `DegreeDay`: `Regions5DegreeDays`↔`US5RegionDegreeDay`,
`Regions9DegreeDays`↔`US9RegionDegreeDay`, `ISODegreeDays`↔`USISODegreeDay`,
`NationalDegreeDays`↔`USNationalDegreeDay`. Its `004` file does not exist yet — the
generator reads the **live catalog**, and CWG's new schema has not been deployed.

## Skills

Project skills are under `.claude/skills/`:
- `loader-routing`
- `tvp-contract-check`
- `scoped-test-policy`
- `compact-stage-reporting`
- `resume-key-and-status-matrix-check`
- `sql-use-database` (required for every `.sql` file)

Use relevant skills on demand; do not load unrelated guidance.

## Deploying to production

`scripts\Publish-ToProd.ps1` is the single source of truth. Build + test gates,
`dotnet publish`, archive the target's current contents into
`\\armh-opsdb01\DataLoaderPlatform\Old\yyyy_MM_dd_HH_mm_ss`, copy, verify.

```powershell
.\scripts\Publish-ToProd.ps1                 # full pipeline
.\scripts\Publish-ToProd.ps1 -DryRun         # show what would happen
.\scripts\Publish-ToProd.ps1 -KeepProdSettings   # deploy binaries, keep prod's appsettings.json
```

Also reachable two other ways, both delegating to that script:
- `dotnet publish src/DataLoader.Host/DataLoader.Host.csproj -c Release -o publish -p:DeployToProd=true`
  (MSBuild target in `DataLoader.Host.csproj`; opt-in — a plain `dotnet publish` never deploys)
- `/publish-to-prod` (`.claude/commands/`)

Four deliberate fail-closed gates, all checked before anything is moved or copied:
no write access to the share; red tests; `appsettings.json` differing from production's
(pass `-KeepProdSettings` or `-OverwriteSettings`); a locked file during the archive.

🔒 Deploying is a WRITE to `ARMH-OPSDB01` — see the standing rule at the top of this
file. Never run this script, or edit anything under `C:\ETL_Tools\DataLoaderPlatform\`,
unless the user specifically asked for that write. Hand over the command instead.

⚠⚠ **PUBLISHING TO THE SHARE IS NOT DEPLOYING — verified 2026-10-20.** This answers the
"whether the daily jobs run from this share is still unverified" question that stood here
before: **they do not.** `\\armh-opsdb01\DataLoaderPlatform` maps to
`C:\ETL_Tools\DataLoaderPlatform\Src` (confirmed via `Win32_Share`), which is only the
publish **drop**. The real layout on `ARMH-OPSDB01`:

```
C:\ETL_Tools\DataLoaderPlatform\
    <Loader>.cmd      one launcher per loader (Platts.cmd, CWG.cmd, …)
    Loader\           WHERE THE JOBS ACTUALLY RUN  <- the appsettings.json in force
    Src\              = the \\armh-opsdb01\DataLoaderPlatform share (publish drop)
    Log\<Loader>.log  per-loader stdout, appended forever, nothing rotates it
```

So `Publish-ToProd.ps1` updates `Src\` and **changes nothing about what runs** until someone
copies `Src` → `Loader`. The two can and do sit at different versions. Both keep their own
`Old\<timestamp>\` archives (5 on `Src`, more on `Loader`); only `Loader\Old\` reflects what
actually ran, and it is how the 2026-10-12 timeout revert was proved.

Scheduled tasks live under **`\DLP\`** in Task Scheduler on `ARMH-OPSDB01` (the older
`\CDT\<Name> Loader` tasks are **Disabled**), each running
`cmd.exe /C "%DataLoaderPlatform%\<Loader>.cmd"` where `%DataLoaderPlatform%` is a machine
env var = `C:\ETL_Tools\DataLoaderPlatform`. Each `.cmd` does `cd /d "%~dp0Loader"` then
`DataLoader.Host.exe "<LoaderName>" >> "%~dp0Log\<LoaderName>.log" 2>&1`. There is **no SQL
Agent job** — `msdb` has none. Each run passes ONE loader as argv, so a run row can name a
single loader regardless of `Platform:EnabledLoaders`. Logging is console-only
(`Program.cs`), so `Log\<Loader>.log` is the ONLY place stack traces exist.

**Config drift — re-measured 2026-10-20** (this supersedes the earlier "differs in 18 keys"
note, which described the state before production's config was refreshed on 2026-10-16; the
claim that `Platform:RunAdditionalProcesses` and `Platform:AdditionalProcessesTimeoutSeconds`
exist only in the repo is **false** — production has both). The repo's `appsettings.json` and
production's running `Loader\appsettings.json` are now **identical except for
`Platform:AdditionalProcessesTimeoutSeconds`** (repo 1800, prod still 600 — see below), and
`Src\appsettings.json` is byte-identical to `Loader\`'s. The config gate will still stop a
plain deploy over that one key; `-OverwriteSettings` is now the correct flag, not
`-KeepProdSettings`, because keeping prod's copy is what reintroduces the bug.

⚠ **A full `Publish-ToProd.ps1` run today also ships binaries, not just config** — the working
tree carries the EIA and NUCORE host wiring. Changing config alone means editing the key in
`Loader\appsettings.json` (and `Src\`'s copy); `Platform:*` settings are read at startup, so
the next scheduled run picks it up with no restart or redeploy. 🔒 That is a production write
— prepare the command and hand it to the user; do not run it unless specifically asked.

## Workflows

Workflow docs are under `docs/copilot/workflows/`:
- `fast-lane-workflow.md`
- `full-lane-workflow.md`
- `review-fix-test-loop-workflow.md`

Pick one workflow at task start and report the choice.

## Reporting format

Use `.claude/skills/compact-stage-reporting/SKILL.md`.
Return only:
- Files changed
- Key decisions
- Risks/open questions
- Verification results

Avoid large inline dumps of code, SQL, or logs.

## Build-only loaders

IHSPointLogic, IIR, NGI, ModernCommodities, EvolutionMarkets, Argus, ICE, EOX, CME,
Genscape, NGX, Marex, OilX, EIA, NUCORE are build-only unless explicitly deployed and run.
Do not report data validation as passed when no live loaded database exists.

NUCORE (ARM's own risk system via `NUCORE.IntegrationService`, 5 endpoints, **`dbo` schema**,
DB **`NUCORE`**) is the only loader reading an *internal* ARM API, and the only one whose
target schema is `dbo` rather than `arm`. Its read path is live-verified end to end (225,677
rows across all five endpoints, zero dropped); its SQL has **never been deployed** and no
`NUCORE` database exists. Its 436 columns across 5 tables are **GENERATED** — never hand-edit
`NucoreColumns.Generated.cs` or `sql/NUCORE/*.sql`; re-run `scripts\Generate-NucoreSchema.ps1`,
which reads the vendor's **own OpenAPI document** (`docs/apis/NUCORE.openapi.json`, discovered
via `/swagger/index.js` — it is NOT at any `/swagger/v1/swagger.json` path) plus an observed
full-day profile. Five behaviours are silent if you get them wrong, and two of them mean the
originally-specified URLs were wrong. **`PnlExplain` ignores `systemDate`; its parameter is
`CurDate`** — asked the wrong way it answers HTTP 200 with the LATEST run for every date
(2026-10-14, 2025-10-15 and 2024-01-10 all returned curDate 2026-10-15, 11,376 rows; the
correct `CurDate` returns 11,364). **`PositionDetails` has NO date parameter at all** — the
spec offers only `TradeMonthFrom`/`TradeMonthTo`/`TradeStatus`/`Page`/`PageSize`/`SortFields`,
so one unfiltered call returns the ENTIRE history (2,468,453 rows / 392 system dates /
~10 GB) and `pageSize=500000` answers **HTTP 504**; it is partitioned by trade month (a
**lossless** split — the 2025–2031 yearly census sums to exactly 2,468,453) and made
incremental by paging `SortFields=-systemDate` down to a per-trade-month watermark read from
the target table. **Omitting a date parameter returns 200 with the latest day**, not an error,
which is why every row's echoed date is asserted against the requested one — the single most
important check in the loader. **A garbage date answers HTTP 500, not 400**, so 500 is
deterministic here and is deliberately excluded from the retry predicate (only 408/429/502/503/504
retry), while weekends/holidays/future dates answer 200 with zero rows as legitimate empty
reads. And **`id` is always 0 and `guid` always all-zeros** on every row of every endpoint, so
neither is ever a key.

⚠ **Two faults from the first real run, fixed 2026-10-18 — THREE of the five tables have no
usable key, and paging was silently lossy.** (1) **`PositionDetails` died on
`Violation of PRIMARY KEY constraint 'PK_PositionDetails'`.** Its key had been "verified
unique" over 100,000 rows that were the first page of a **trade-ordered** response covering
only **130 distinct trades** — a sample, not a population. Over five COMPLETE trade months
(443,477 rows) that key has **45,151 duplicates**, and nothing rescues it: `+specId` → 2,189,
`+priceType,settlementIndex` → 129, `+productId` → still 129, and the survivors are NOT exact
duplicates (one group is 17 distinct settlement rows at 17 contract prices, differing only in
measures, so de-duplicating would destroy real realized P&L). `PnlExplain`'s key fails the
same way on 2025-10-15 (19 byte-identical copies of one row). Both now REPLACE instead of
merge — `PnlExplain` per `(CurDate)`, `PositionDetails` per `(TradeMonth, SystemDate)` —
joining `PositionSummary`, which never had a key. Only **`VarResults` and `Volatilities`
still merge**, re-verified over 7 and 5 COMPLETE days. ⚠ A uniqueness check on this vendor is
meaningless unless it is over a complete population, compares values **as SQL will** (`7.5`
and `7.50` are one DECIMAL), and is repeated across several periods.
⚠ A replace depends on **a scope never being split across two write calls** — the second
call's DELETE would remove the first's rows, silently. A `ReplaceByDate` unit is therefore
written as ONE batch and a `ReplaceByScope` unit flushes only on a `SystemDate` boundary;
`NucoreSourceReader` throws if a scope repeats.
(2) **Paging without `SortFields` is NOT stable, and half of it is invisible.** One
`Volatilities` day's pages 1 and 2 at `pageSize=20000` shared **111 rows** unsorted and **0**
sorted — meaning ~111 rows were never returned at all. The loader sent `SortFields` only for
`PositionDetails`, so the loaded `Volatilities` was short by **~2.6% per day** (144,107 stored
vs 147,908 available) across all 64 days, with nothing failing. **Every request now carries
`SortFields`**, and the reader reconciles rows read against the envelope's own `totalRecords`
so a residual shortfall fails the unit. Only multi-page units were exposed: `VarResults`
(9,378/day) and `PositionSummary` (13,555/day) are single-page and verified complete.
Migration: `sql/NUCORE/hotfixes/2026-10-18_RemoveUntrustworthyKeys.sql`.

See `docs/apis/NUCORE.md` and `docs/design/NUCORE.md`.

EIA (U.S. Energy Information Administration API v2, 95 series × 154 frequency axes, `arm`
schema, DB **`EIAv2`**) is the only loader whose **scope lives in the database rather than
in config** — `arm.Series` / `arm.SeriesFrequency` / `arm.SeriesDataType` say what to
request, read through stored procs so no query is compiled into the binary — and the only
one that lands 95 structurally different datasets in **one generic table**,
`arm.SeriesData`, as canonical JSON plus two hashes. Its read path is live-verified end to
end (10,002 rows across all 8 frequencies); its SQL has **never been deployed**.
🔒 **`dbo.SeriesData` in the SAME database is the live incumbent with ~94M rows and an
identical column list**, so every statement in `sql/EIA/*` is `arm.`-qualified and a test
asserts that `999` drops nothing else; the three `arm` catalog tables are INPUT and are
neither created by `001` nor dropped by `999`. Six behaviours are silent if you get them
wrong. **`FacetKey`/`DataHash` are NOT a free choice** — they are the non-randomised .NET
*Framework* `String.GetHashCode` over the canonical JSON, recovered from the incumbent and
verified 20,000/20,000; .NET 8 randomises `GetHashCode` per process, so calling it would
re-key every row every run and append ~90M rows instead of updating. A **`local-hourly`
`start`/`end` without a two-digit `±HH` zone suffix answers HTTP 500**, not 400 — it looks
transient and gets retried forever (`Z` and `-0400` also 500; a raw `+` decodes to a space
and 500s, which is why bounds render `-00`). An **`offset` past the end returns 200 with
ZERO rows while still carrying the `"incomplete return"` warning**, so that warning is not
a "there is more" signal and the stop condition must be an empty page. **404 has an EMPTY
body** and means the catalog has drifted from the API. **`response.total` is a string and
`warnings` sits at the ROOT**, outside `response`. And the vendor **changed its JSON value
types from numbers to strings between 2022 and 2024**, which forked the incumbent into
duplicate rows under two FacetKeys (`international` 1949: API 49 rows, `dbo` 98) — the
loader normalises numeric strings so a future flip re-keys nothing, which is also what the
request specifies.

⚠ **Two faults from the first full run, fixed 2026-10-16 — both pointed at the wrong
thing.** (1) **`HttpClient.Timeout` bounds a RETRIED operation, not one attempt.** A Polly
policy from `AddPolicyHandler` runs *inside* it, so `HttpTimeoutSeconds=120` with
`RetryCount=4` gave all five attempts 120s between them; against `electricity/rto/*`,
whose gateway 504s at ~60s, attempt 2 blew the budget and the error surfaced as a CLIENT
timeout, hiding the vendor's 504. The budget now lives in the policy per attempt
(`retry.WrapAsync(timeout)`) with `client.Timeout = InfiniteTimeSpan`, and
`TimeoutRejectedException` is in the retry predicate. ⚠ The budgets are coupled —
`(RetryCount+1) × HttpTimeoutSeconds` + backoff must stay under
`WorkUnitTimeoutSeconds`; a test asserts it, and that test caught `EiaSettings` inheriting
the base 300s default while appsettings shipped 3600. (2) **EIA's `offset` paging hits a CLIFF.**
A controlled run (idle API, no cached URLs, 136 cold requests) found offsets of 0 / 15,000
/ 100,000 all failing **0%** with no latency penalty between them, and offset 400,000
failing **100% (4/4 HTTP 503)** — so the old `daily: 365` window (704,880 rows, 141 pages,
offset 700,000) could never finish. `WindowSteps` are now sized from measured density to
~25,000 rows/window (`daily: 12`, `hourly: 48`, `annual: 5`, …), capping offset at 20,000,
with `WindowStepsBySeries` for outliers (`international` annual is 55,711 rows/YEAR) and
`MaxRowsPerWindow` as an automatic backstop that **splits a too-big range in half and
recurses** rather than paging deeper.
⚠ **Do NOT throttle this loader to fix 503s.** The same run ruled out every other
variable: concurrency 1/2/4 over 72 requests → 0 failures (and c=4 is ~4× the throughput
of c=1), `sort` on/off over 36 → 0, cheap-vs-heavy endpoint at concurrency 8 over 24 → 0.
⚠ **api-umbrella caches successful responses** (cold ≈7–13s, same URL again ≈0.2s), which
invalidated an earlier version of this very finding — benchmark with unique URLs and check
`X-Cache`. Latency also wanders 7.5–14.4s run to run with zero failures, so a slow cell is
not a failing one.
⚠ **That burst experiment does NOT clear the loader for sustained load.** A real
multi-hour backfill degraded ~20× (321 → 13 units per 30 min, avg unit 22s → 250s) with
**zero failures** — errors shifting from 503 to mostly 504/502 — so the only symptom was a
log full of retries. The backfill is resumable at zero cost (settled units skip before any
request), so the response to degradation is **stop and resume off-peak**, not grind.
⚠ **Window sizing is not a free knob once data exists** — it is baked into settled resume
keys. `daily: 12` and `hourly: 48` are FROZEN (9.7M and 24.1M rows settled under them).
The sparse frequencies were widened 2026-10-16 (`monthly` 6→60, `annual` 5→25,
`quarterly` 12→40, cutting ~4,550 of 12,755 units) because sizing each frequency for its
DENSEST member made 4,398 monthly units across 49 tiny series; that is safe only because
`MaxRowsPerWindow` subdivision now handles the dense outliers.
⚠⚠ **MOST OF THE 2026-10-16 PERFORMANCE FINDINGS WERE MEASURED DURING AN EIA OUTAGE AND
ARE RETRACTED.** `electricity/rto/*` spent hours returning 502/503/504; once it recovered,
**56 consecutive requests succeeded across every shape** at 2–9s medians. Retracted: the
"offset cliff" (400,000 and 700,000 now answer in 6–9s), "1-hour windows are more
reliable" (7/8 vs 6/8 — noise), and "faceting reduces backend cost" (48 interleaved
requests, all six shapes 100% at 2.1–2.5s — **faceting is NOT implemented**). Unproven:
"sustained load degrades throughput 20×". Still valid: the per-attempt timeout bug (a code
defect), api-umbrella caching, and the facet cross-product combinatorics (structural).
**Before attributing anything to request shape, prove the endpoint is healthy** — a
handful of plain requests costs seconds. Interleave test cells rather than running blocks,
and use unique URLs so the cache cannot flatter the result.
**The incremental run** re-attempts failed units, re-fetches every HOT window (fresh
`;run=<UTC date>` token), and skips everything settled before any request — measured
**186 hot units ≈ 1.4h/day**. ⚠ Widening the sparse windows (§4.1) made the hot tail as
wide as the window: the FINAL tile of every axis ends in the future and never settles, so
each annual series was re-reading **25 years daily** for the current year.
`NarrowHotWindowReads` (default on) starts a hot read at
`max(WindowStart, today − SettledAfterDays)`. ⚠ The key is built from `WindowStart`, NEVER
`ReadStart`, so narrowing changes no identity and re-downloads nothing. It narrows only a
window that has **completed at least once before** (`EiaLoadHistory`, one read of
`core.LoadLog` per run, empty-on-failure so it disables itself rather than guessing) —
narrowing a never-fully-read window would skip its older half today, again tomorrow under
a fresh token, and it would settle having never loaded it.
⚠ `WindowSteps` and `MaxBackfillDaysByFrequency` are COUPLED — 1-hour tiles over the full
2019-onward hourly axis would be 70,080 units — and a test fails if a window is narrowed
past ~5,000 implied units without a matching cap. `MaxBackfillDaysByFrequency` ships
EMPTY: it ABANDONS history rather than deferring it, and a 7-day hourly cap briefly added
on 2026-10-16 (and reverted the same day) would have discarded ten months. When used, it
filters the FIXED tiling grid and never moves its origin — moving it would shift every
settled key daily.
`Loaders:EIA:ExcludedFrequencies` ships excluding **`local-hourly`** — same instants as
`hourly`, relabelled to each respondent's local offset, 27.7M rows / ~29% of the backfill
(operator decision 2026-10-16). It is an operational lever; the architectural one is
`arm.SeriesFrequency.Enabled` + a predicate in the proc, which needs the column populated
(NULL on all 371 rows).
Note also `arm.SeriesFrequency.Enabled` and
`arm.SeriesDataType.Enabled` are **NULL on every row**, so adding an `Enabled = 1` filter
to those procs would load nothing, and `arm.SeriesDataType`'s audit column is
**`ModifedAtUtc`** (sic), not the `ConduitLastUpdate` the `dbo` twin uses.
See `docs/apis/EIA.md` and `docs/design/EIA.md`.

**CWG is only HALF build-only** — it is not in `Platform:EnabledLoaders`, so it never runs on a
schedule, but its **pre-reshape SQL is deployed and carries ~1.91M manually-loaded rows**. Treat
it as a live database for anything destructive. See the CWG paragraph below.

**AGSI is NOT build-only any more** — its SQL is deployed to `ARMH-OPSDB01`/`AGSI` and it has been
running daily since ~2026-08-18. Three vendor behaviours are silent if you get them wrong, all
live-verified 2026-09-28: `?country=eu` and `?country=ne` return the **same two-element body**
(`total:2`, `data[0]`=eu, `data[1]`=ne) regardless of which aggregate you ask for, so **every
`data[]` element must be attributed by its OWN `code`** — stamping the requesting unit's
`EntityId` on all of them collapses them onto one `(EntityId, GasDayStart)` merge key and the
de-dup silently keeps the wrong one (this stored the Non-EU aggregate as EU for 61 days); an
**unpublished or future `date` is silently clamped** to the latest available gas day rather than
answering empty, so a record whose `gasDayStart` ≠ the request date must be dropped or it
overwrites the previous day's row; and a **missing/invalid API key answers HTTP 200** with
`{"error":"access denied", "total":0, "data":[]}`, shaped exactly like an empty day, so `error`
must be read before any no-data tolerance. That same `error` channel also carries transient
vendor faults (~0.7% of requests), and 🔒 **AGSI echoes the secret `x-key` back in the error
body's `message` field** (`"API key: <the key>"`) — so `message` must NEVER be logged, quoted
or re-thrown, and anything else quoted out of a response body must be redacted. Note also that
`/api/about` returns only the 22 real country codes — `eu`, `ne` and `RS` are **hand-inserted**
into `arm.GasStorageEntity` (`FileLogId IS NULL`) and sit outside the discovery contract.
See `docs/apis/AGSI.md` §2 Behaviours 1, 2, 2a, 6, 7.

ICE (21 file feeds, `arm` schema, DB `ICE`) enforces a **hard 30 requests/min account-wide**, and
`downloads.ice.com` answers **HTTP 200 for everything** — real data, a missing file and expired
auth alike — so outcome is read from the body, never the status code. Feeds 19-21 pull the
**`Settlement_Reports/Environmentals` XLSX set**, a different directory from the `_CSV` `.dat`
tree and a separate publication that lands in its own tables (`arm.PhysEnvFutures`,
`arm.PhysEnvOptions`); nothing de-duplicates across the two. `icecleared_physenv` and
`ngxphysical_env` share `arm.PhysEnvFutures`, told apart by **`SourceSystem`** — loader-derived
like `SourcePath`, but REQUIRED and the 2nd PK column. Four things are silent if you get them
wrong: `Strip` must stay `VARCHAR` because the two feeds sharing that table disagree about it
(ICE publishes `Apr27`/`23 Sep 26`/spreads, NGX publishes `9/1/2026`); `STRIKE` is blank on
**every** futures row and Excel **omits the cell entirely**, so a document-order read shifts every
later column; the sheets store numbers as doubles, so they carry round-trip noise
(`78.01000000000001`) and exponent forms (`-1E-05`) the `.dat` twins never show; and the read path
for these three feeds is **verified against real files but never downloaded by the loader** — the
URL template and the entitlement are unexercised. Its SQL has never been deployed.
See `docs/apis/ICE.md` §4A.

Criterion is the only loader with a **relational (PostgreSQL) source**. Its read path is verified
against live production.

**Criterion is NOT build-only any more** — its SQL is deployed and it runs against a live database
with enough data that `arm.Financial_SeriesData` can no longer be scanned in 30 seconds. That table
is by far the largest in the repo (~110M rows for a 30-day window), and it produced a trap worth
remembering: **a post-load validator's timeout is silent and total**. `arm.usp_ValidateLoad`'s
`OrphanSeriesData` check scanned it unbounded, expired at `SqlCommand`'s 30-second default
(`SqlException` -2), and because the validator swallows its own failures by design, the abort took
**every other check in the proc** down with it while the run still reported success (observed
2026-10-02). Two rules came out of it: **every check in `usp_ValidateLoad` must be bounded**
(the orphan check now scopes on `ModifiedAtUtc` via `@OrphanLookbackHours`, default 48h, backed by
`IX_ARM_Financial_SeriesData_ModifiedAtUtc`), and deliberately expensive full-history work lives in
**`arm.usp_ValidateIntegrity`** — which nothing calls per run and which is meant for a scheduled or
on-demand sweep. `CriterionValidationTests` enforces both, and the bounding guard carries its own
two-sided self-test so it cannot rot into passing on anything. Note every OTHER loader's validator
still uses the 30s default; Criterion is simply the first whose tables outgrew it.

Genscape (oil fundamentals, DB `Genscape`) is verified against the live API end to end; its SQL
has never been deployed. Three of its behaviours are silent if you get them wrong: `endDate` is
**exclusive**, responses are **capped at 5,000 rows** with no indication (oldest dropped), and the
crude-storage endpoint's `week` is a week-of-**month**, so the loader derives `Year`/`Week` from
`ReportDate` in C# — never with `DATEPART` in SQL, because `Week` is in the primary key and
`DATEPART(week, …)` follows the session's `DATEFIRST`. See `docs/apis/Genscape.md`.

EOX's read path is verified end to end against the live FTP drop (25 files, 2011-2026, parsed
through the real reader); its SQL has never been deployed.

CME is the only loader whose source is a **fixed-width report**, and the only one whose SFTP
server rejects absolute paths (`/dir` fails, `./dir` works). Its read path is verified against
16 live bulletins (757,360 rows, 8 feeds); its SQL has never been deployed. BALMO/day-label
futures rows are **deliberately not loaded** — see `sql/CME/001` for why and what it costs.

NGX (ICE NGX clearing, DB `NGX`) is the only loader whose target database **already holds live
incumbent tables** (`dbo.IndexPrice`, `dbo.StripTradingSummary`, still written by Conduit today);
the new `arm.*` tables shadow them, and the loader reads `dbo.[Index]` but never writes `dbo`.
Its read path is verified live end to end; its SQL has never been deployed. Four behaviours are
silent if you get them wrong: auth is **HTTP Basic**, not the documented `/api/v2` bearer token
(which these `.xml` paths ignore, answering 302 to ICE SSO — so redirects must never be
followed); at most **10 `indexId` params** per request, and **one unentitled id returns 403 for
the whole batch**; the index response **truncates at 50 rows** unless `pageSize` is sent, and the
paging param is `page` (`pageNumber` and `size` are silently ignored); and every timestamp must
be converted from the vendor's Mountain offset to **US Central**, which matters doubly because
`TradeDateTime` is in the primary key. Amounts also carry thousands separators (`313,100`).
See `docs/apis/NGX.md`.

Marex (Neon crude market, DB `Marex`) is the only loader whose source **pushes rather than
answers**, and the only one built on a **vendor SDK** — three .NET Framework 4.6.1 assemblies
vendored in `lib/neon` (not on any NuGet feed). They run fine in-process on .NET 8; the vendor's
own integration note says to build a separate net48 connector, and that is **not** necessary —
see `lib/neon/README.md`. There is no request that fetches data: the client authenticates
(Auth0 password grant), opens a SignalR websocket, and the gateway pushes one opening snapshot of
every entity. So a run is connect once → capture → merge five tables → disconnect; the live
`*Update` delta stream is deliberately not consumed, because that would need a resident process
and this platform runs loaders as batch jobs under an overlap lock. Its read path is verified
live end to end; its SQL has never been deployed. Four behaviours are silent if you get them
wrong, and three of them contradict the vendor's own note: `NeonApiConfig.Name` is the SignalR
**hub name** (`Gateway.Crude`, supplied by the SDK when null) and NOT a client label — a wrong
value makes the gateway answer the negotiate with **HTTP 500** while the SDK retries every 5s
forever and the status sits on `Connecting`; handlers must be attached **before** `Connect` and
the run must wait on the **snapshots**, not on `ConnectionStatus == Connected`, which is reached
*after* they arrive; `PasswordConnectionData` **does** carry `Domain` and `ClientId` (the note
says it does not), so `GetToken` works and hand-rolling the Auth0 call is unnecessary; and
`ClosingPriceDto.Time`/`.PreviousTime` are **non-nullable**, so "no value" arrives as
`0001-01-01`, which `DATETIME2` stores silently. `ExchangeDate`/`TradeDate` exist in no DTO —
they come only from the separate `ExchangeDateSnapshot` event and lead both fact tables' primary
keys. Also note `MarketState` lives in `SignalRClient.Contracts`, not `Common.Core.Enums`.
See `docs/apis/Marex.md`.

OilX (Energy Aspects' OilX line, 8 CSV feeds, `arm` schema, DB `OilX`) is the only loader whose
API returns a **manifest of presigned S3 URLs** rather than data, and the only one where each
daily file is a **full snapshot of all history** — one day is ~2.07M rows across the eight feeds
(CargoTracking alone is 208 MB / 396,866 rows), so a 31-day window is ~64M rows and ~6.5 GB and
everything streams and merges in 20,000-row batches. Its read path is verified live end to end
(all 8 feeds, 2.46M real rows, zero dropped, every RowId unique); its SQL has never been deployed.
Five behaviours are silent if you get them wrong: those presigned URLs **expire in 5 hours**, so
the manifest is fetched per day at the moment that day runs and never once up front; **an empty
day and a bad feed name are BOTH HTTP 422**, told apart only by the message prefix (`No data for
files` = legitimate empty read, `Unavailable files` = fail loudly), so neither blanket reading is
safe; a **multi-feed manifest request answers 200 and silently omits** any feed that published
nothing, which is why the loader asks for **one feed per request**; a day now publishes **up to
four snapshots per feed, all carrying the same in-file `RunDate`**, so they collide on
`(RunDate, RowId)` and must merge in ascending `uploaded_at` order for the newest value to win
(the count is NOT historic — 8 files/day before mid-2026, 17 now); and 🔒 the **`api_key` travels
in the QUERY STRING**, unlike every other HTTP loader here, so the client has `RemoveAllLoggers()`
and nothing derived from a URL reaches a log unredacted. `RowId` is a deterministic UUIDv5 over
each feed's business key — never random, or the merge would become an append of ~2M rows a run —
and **Flow's two usually-empty sub-country columns are load-bearing in that key**: dropping them
collapses 28,887 of its 232,311 rows. Unlike ICE/Genscape/EvolutionMarkets it ships
`SettledAfterDays = 1`, because a published OilX day is immutable and all-hot would re-download
~6.5 GB per run for data that cannot change. See `docs/apis/OilX.md` §2 and `docs/design/OilX.md`.

CWG (19 endpoints, `arm` schema, DB `CWG`) was **reshaped on 2026-10-05 so every arm fact mirrors
its incumbent `dbo` twin** by name, primary key and column name/type — arm is now a drop-in for
dbo. Facts lost `FileLogId`/`DateCreated` and gained `RecordHash CHAR(32)`, an MD5 over the
non-key value columns computed **only in the merge proc**; the MERGE updates solely when the hash
differs, so `ModifiedAtUtc` tracks real change and **a no-op re-merge reports
`RecordsProcessed = 0`** (correct, not a bug). `arm.FileLog` survives as the per-request run/status
log. Five things are silent if you get them wrong. The forecast key is re-derived as
`ForecastDate` = the INIT date and `UTCHourEnding` = valid day + hour label **+ the file's own zone
offset**, read from the Shape C/D matrix header: `Date (EST)` → **+4h** (northamerica),
`Date (UTC)` → **+0h** (europe). Each offset is a **FIXED per-zone constant, never a time-zone
lookup**, because DST would give 23 hours on spring-forward and a *duplicate* hour on fall-back that
collides on the PK; an unrecognised zone is **refused** rather than guessed. (Until 2026-10-12 the
+4h was hardcoded and the header matched the literal `Date (EST)`, so **every europe wind file
parsed to ZERO rows on an HTTP 200** — see the wind paragraph below.) `ForecastLabel`/`HourLabel`
are kept alongside so the un-shifted values stay recoverable — and they hold the file's *own* label
(`12:00 AM` NA, `00:00` europe), since `CwgHours` now indexes both clock formats; both are arm-only
columns that `dbo` lacks. Since `(ForecastLabel, HourLabel) → UTCHourEnding` is a bijection no
forecast run is lost. Four
dimensions no longer have a column and are handled by **narrowing the read, never by colliding**:
CityForecast/CityObservation/Station are flagged `RegionMergedTarget`, meaning every geography in
`Geographies` **merges into the one table** exactly as the incumbent `dbo` tables do — safe because
all three key on the station and CWG station identifiers are globally unique (verified 2026-10-12:
`dbo.Station` = 891 rows / 891 distinct `Identifier` across 51 countries, and `dbo.CityForecast`
has carried US 19.47M alongside France 1.47M / Italy 1.40M / UK 1.22M under that key for years).
⚠ **Units are per-geography and nothing on the row records them**: CityForecast is fetched °F for
`northamerica` and °C for `europe` (`RegionUnits`), which is what `dbo.CityForecast` stores too
(US mean 65.6 °F vs Norway 8.8 °C); CityObservation is °F for both. Join `arm.Station.Country` to
resolve the unit. *(Until 2026-10-12 the provider instead **threw** when a second geography
survived, on the belief the rows would collide on the PK — they cannot, and that guard was what
kept Europe out of the load.)* WindTotalCapacityMW/Pct
write the **Current block only**; DailyNormal/WindHourly drop NW+SW; SolarHourly keeps 2 of 14
regions — and for those last two the descriptor holds `WideRegionColumns` (full in-file
VALIDATION set, unknown column still fatal) separately from `TargetRegionColumns` (the narrower
WRITE set). Shape B is **no longer an unpivot** — its three targets are wide, so a `"NULL"` cell
is a NULL column on a kept row rather than a dropped record. `StateDegreeDayObservation` is a
**19th endpoint added and live-verified 2026-10-05** (`northamerica_state_wdd_observations_{date}.csv`
— its OWN filename, *not* the `{subregion}` wdd family; 48 rows, one `DATES` = the filename date,
**no `END.` footer** unlike its siblings).
See `docs/apis/CWG.md` §19 and the 2026-10-05 addendum at the top of `docs/design/CWG.md`.

**CWG's SQL IS deployed — corrected 2026-10-06** (earlier notes here claimed it never had been;
that was wrong, verified against the live catalog). `ARMH-OPSDB01`/`CWG` holds the **pre-reshape**
`arm` schema with **~1.91M loaded rows** (CityForecast 600,030; SolarHourly 498,213;
WindForecastSubRegion 232,560; …), 18 TVP types, 19 procs and 3,088 `arm.FileLog` rows. CWG is
still **not** in `Platform:EnabledLoaders` on either side, so nothing loads it on a schedule —
the data came from manual runs. The reshape therefore needs a **tear-down first**: 001/002 are
creation-guarded (`IF OBJECT_ID … IS NULL` / `IF TYPE_ID … IS NULL`), so re-running them over the
old objects is a silent no-op. `sql/CWG/999_DropCwgObjects.sql` does it in dependency order
(procs → types → facts), keeping `FileLog`/`Endpoint`/`Status`/`Region` since their shapes did not
change and 001's seed MERGEs top them up. 🔒 That script is the single most dangerous file in the
repo: the same database's `dbo` schema holds the live incumbent dataset (~95M rows) in tables with
**identical names** (`dbo.CityForecast` vs `arm.CityForecast`), so every statement is explicitly
`arm.`-qualified and must stay that way.

## Failure notifications and the watchdog

Two mechanisms, because neither covers every failure class alone. **In-process**
(`DataLoader.Core/Notifications/`) emails at the end of a run when any loader
failed, when the process throws, or when the load-log sweeper closed rows an
EARLIER run left open. **The watchdog** (`DataLoader.Host.exe --watchdog`, its own
hourly `\DLP\Watchdog` task) reports loaders that have not run inside their
configured `MaxGapHours`, run rows still open after `StuckAfterHours`, recent
`Success = 0` runs, misconfigured loaders, and anything it could not check.
In-process code cannot report its own death; the watchdog cannot report the box
being down. Both are configured under `Platform:Notifications` / `Platform:Watchdog`.

Six things are silent if you get them wrong:

- 🔒 **Email leaves the box; logs do not.** All vendor error text goes through
  `ErrorTextRedactor` first — AGSI echoes your `x-key` back in its error body, OilX
  puts `api_key` in the query string, and IIR/NUCORE use JWT bearer tokens. Its rule:
  a keyword that is also an English word (`key`, `token`) counts as a credential only
  in `name=value` or `"name":` form; one that is never English (`api_key`, `password`)
  counts in either. `FailureNotifier` redacts the finished message again as a backstop.
- ⚠ **`core.LoaderRun_Summary.StartTime` is CENTRAL, `core.LoaderRun.StartedAtUtc`
  is UTC.** Production's `usp_CompleteLoaderRun` converts before storing. Mixing them
  reports every loader ~5h overdue; `WatchdogEvaluator` takes `nowCentral` and a test
  pins it.
- ⚠ **The relay accepts NO credentials.** `armenergy-com.mail.protection.outlook.com`
  is Exchange Online's inbound MX connector — an `EHLO` probe advertises STARTTLS and
  no `AUTH` verb. `NotificationSettings` rejects `SmtpUsername`/`SmtpPassword` outright
  so nobody "fixes" it later with a real password. Recipients must be internal
  `@armenergy.com`; direct send relay-denies external addresses.
- ⚠ **A bad notification config must never stop the loaders.** `BuildServiceProvider`
  catches validation failure and degrades to `NullNotificationSender` with a loud log
  line, because an uncaught throw there aborts before `host.RunAsync()` and would mean
  zero loaders ran because of a typo in a side channel.
- ⚠ **The watchdog exits 0 when it finds overdue loaders** — that is the job working.
  It exits 1 only when it could not read history or could not deliver a finding,
  because an undelivered alert looks exactly like all-clear. A failure in a secondary
  query degrades that signal and reports it rather than discarding the whole pass.
- ⚠ **A loader absent from `Platform:Watchdog:Loaders` is never reported**, so
  build-only loaders run by hand once do not alert forever. The flip side: a
  misconfigured entry (`MaxGapHours` missing or ≤ 0) IS reported, and an empty
  `Loaders` section reports itself — a watchdog monitoring nothing otherwise looks
  exactly like a healthy platform.

`core.LoaderRun_Summary` is a **table** in production (written by prod's
`usp_CompleteLoaderRun`), not a view, and prod's `core.LoaderRun` has a 6th column
`LoaderRunId` the repo DDL does not create. The watchdog depends on both, and
`sql/Core/` has drifted from them — a clean deploy to a fresh server would not
produce a database it can read. Nothing sweeps `core.LoaderRun` either (the
`AbandonStaleLoadLogs` sweeper closes `core.LoadLog` rows only), so orphaned run
rows accumulate forever; the watchdog's stuck-run query is bounded by
`StuckLookbackDays` and this host for that reason.

See `docs/superpowers/specs/2026-10-24-failure-notifications-design.md`.

## Key files

- `src/DataLoader.Core/Abstractions/`
- `src/DataLoader.Core/Pipeline/LoaderPipelineBase.cs`
- `src/DataLoader.Core/Sinks/SqlSinkBase.cs`
- `src/DataLoader.Core/Concurrency/SqlWriteGate.cs`
- `src/DataLoader.Core/Persistence/SqlLoaderOverlapGuard.cs`
- `src/DataLoader.Host/Program.cs`
- `sql/Core/`
- `docs/apis/`
- `docs/design/`
- `tests/`

## Configuration reminders

- Loader settings live in `src/DataLoader.Host/appsettings.json`.
- Sensitive settings default to `SEE_DB` and resolve from `core.Param`.
- Use `AddLoaderSettings<TSettings>` for correct `SEE_DB` resolution.
