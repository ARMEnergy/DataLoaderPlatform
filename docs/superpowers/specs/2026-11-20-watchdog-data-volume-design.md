# Design: Watchdog `DATA_VOLUME` signal

**Date:** 2026-11-20 · **Database:** `DataLoaderPlatform` · **Routing:** Full lane
**Source requirements:** `2026-11-20-watchdog-data-volume-requirements.md` (DBA Agent team), reproduced in
§R below. **Builds on:** commit `5280e78` (persisted findings, `sql/Core/007`).

This document is the requirements **plus four amendments agreed with the owner on 2026-11-20** after the rule
was simulated against production history. Where §A and §R disagree, **§A wins**.

---

## A. Amendments (agreed 2026-11-20) and why

Evidence: read-only queries against `ARMH-OPSDB01`/`DataLoaderPlatform`, 2026-11-20.
`core.LoaderRun_Summary`: 4,000 rows since 2026-07-16, never pruned, clustered on `LoaderRunId` only (no
`StartTime` index — a full scan of ~5k rows, which is trivial). `TotalRecords` is `INT NULL`; 0 successful
runs have it NULL. Applying §R.4 as written to the last 28 days: **23 findings across 6 loaders**, most of
them artefacts.

### A1. A day's rows = ALL runs that day; judge only days with ≥ 1 successful run
Replaces §R.3's "SUM(TotalRecords) over runs with `Success = 1`".

- **Rows** = `SUM(ISNULL(TotalRecords, 0))` over **every** run whose `CAST(StartTime AS date)` is the day
  (non-blank `LoaderName`), whatever its `Success`.
- A loader-day **exists** (can be judged, can be a sample) only if at least one of those runs has
  `Success = 1`. A day with only failed runs is still skipped — FAILED's job.
- Why: on retry days the rows are split between a failed run and a successful retry. AGSI since 2026-10-02:
  the failed run carries ~480 rows and the successful retry 1–8, so a success-only sum read as a 98% drop
  EVERY day. 11 loaders had such mixed days in 42 days (AGSI 38, IHSPointLogic 25, ICE 13, EvolutionMarkets
  11 …). EvolutionMarkets' two "Zero" findings were the same artefact. All-runs: 17 findings, no false Zero.
- `DetailJson` carries `successfulRuns` AND `totalRuns`; `loaderRunIds` lists ALL of the day's runs (≤ 20).

### A2. Baseline looks further back past excluded days
Replaces the fixed "D−7 … D−42" sample set in §R.4.2.

- Walk back over same weekdays D−7, D−14, … up to **D − 7 × `MaxBaselineLookbackWeeks`** (new setting,
  default **18** = 3 × `BaselineWeeks`; must be ≥ `BaselineWeeks`). Take the **most recent `BaselineWeeks`**
  qualifying samples (loader-day exists, not excluded, not before `BaselineFromCentral`). Stop when that
  many are collected or the lookback is exhausted.
- `excludedSampleDays` lists the excluded days met during that walk (up to the point it stopped).
- Why: with a fixed 6-week set, a drop that never recovers has every sample excluded after 6 weeks →
  "not enough history" → silence, the opposite of §R.4.3's "keeps being reported". Now the old level
  stays the baseline for up to 18 weeks, until the owner sets `BaselineFromCentral`.
- Query span becomes `WindowDays + 7 × MaxBaselineLookbackWeeks` days (~129).

### A3. An open finding that cannot be judged stays open
New hint value **`KeepOpen`** (a hint, never a `ResolutionReason`; it is not in that CHECK constraint).

For each OPEN `DATA_VOLUME` finding NOT re-detected this pass, the sink sends:

| Situation | Hint | Result |
|---|---|---|
| `Platform:Watchdog:DataVolume:Enabled = false` | `LoaderDisabled` | closed (see A4) |
| Loader gone from `Platform:Watchdog:Loaders` | `ConfigRemoved` | closed |
| Loader `Enabled = false` or `Volume.Enabled = false` | `LoaderDisabled` | closed |
| Day older than the window's oldest day | `AgedOutUnresolved` | closed |
| Day in window, loader-day judged this pass, in range | *(none)* | closed `NotDetected` (the rerun case) |
| Day in window, loader has `BaselineFromCentral` and day ≥ it | *(none)* | closed `NotDetected` — the owner declared that level normal |
| Day in window but NOT judged (too few samples, median < `MinMedianRows`, invalid per-loader config, no loader-day) | `KeepOpen` | **stays open**; it ages out later as `AgedOutUnresolved` |

Rows evaluated first-to-last; the first match wins. Why: otherwise an unjudgeable finding closes as
`NotDetected` = "fixed", breaking the platform rule that silence is not health.

### A4. Turning the master switch off closes leftovers
When `DataVolume.Enabled = false` **and** open `DATA_VOLUME` findings exist (the sink already reads the
open view), the sink adds `DATA_VOLUME` to `SignalsChecked` and sends `LoaderDisabled` for each. When none
are open, the pass is byte-for-byte what it is today (§R.2.2 still holds for every normal case). Never on a
`Failed` pass (nothing checked → nothing closes).

### A5. Exclusion rule, tightened
A sample day is excluded when ANY `DATA_VOLUME` finding with key `DATA_VOLUME|<L>|<day>` exists that is
open or resolved with **anything other than `NotDetected`**. §R.4.3 lists only open + `AgedOutUnresolved`;
`LoaderDisabled`/`ConfigRemoved` closures were never confirmed normal either, so they stay excluded. Only a
rerun that brought the day back into range (`NotDetected`) makes it a normal sample.

### A6. Notes for go-live (no code)
- **ICE** dropped permanently on 2026-11-11 (~16.6M → ~0.75M rows/day) because the `LastModified` hot-key
  change stopped re-merging unchanged files; `TotalRecords` counts rows WRITTEN. Set
  `Loaders:ICE:Volume:BaselineFromCentral = "2026-11-11"` when enabling, or it reports every day.
- **AGSI** stepped from ~2,000 to 482 rows/day on 2026-11-17 (the transient-retry fix ended re-writes).
  Consider `BaselineFromCentral = "2026-11-17"`.
- §R.4.2's EvolutionMarkets example is wrong: `MinMedianRows` applies to the DAILY total and its daily
  median is ~123, so it IS judged.
- The signal measures rows **written** (merge/hash loaders like CWG count only changed rows, settled units
  count 0), not rows available. The runbook must say so.

---

## D. Component design (the contract every stage builds to)

### D1. Settings (`DataLoader.Core/Watchdog/WatchdogSettings.cs`)
- `WatchdogSettings.DataVolume` : `WatchdogDataVolumeSettings` (binds `Platform:Watchdog:DataVolume`).
  Fields and defaults: `Enabled=false`, `Notify=false`, `WindowDays=3`, `BaselineWeeks=6`,
  `MaxBaselineLookbackWeeks=18`, `MinSamples=4`, `MinMedianRows=100`, `LowPercent=50`, `HighMultiple=3`
  (double), `SettleHours=3` *(added in review, 2026-11-20)*.
- `SettleHours`: yesterday joins the window only when the Central time of day is ≥ `SettleHours`.
  Before that, the window ends at today−2 and still holds `WindowDays` days. Why: a summary row is
  written only when its run completes, and runs start as late as 23:00 Central and last up to
  126 min. A 00:05 pass would otherwise judge a partial yesterday and raise a false Low. An open
  finding for a day newer than the window end gets `KeepOpen`; only a day OLDER than the window
  start ages out.
- `WatchdogLoaderSettings.Volume` : `WatchdogLoaderVolumeSettings?` — `Enabled=true`, nullable overrides
  `LowPercent`, `HighMultiple`, `MinMedianRows`, and `BaselineFromCentral` as **string** `yyyy-MM-dd`
  (parsed with InvariantCulture; unparseable = invalid).
- Validation: section invalid (`WindowDays ∉ [1,7]`, `SettleHours ∉ [0,23]`, `BaselineWeeks < 1`,
  `MinSamples < 1`, `MinSamples > BaselineWeeks`, `MaxBaselineLookbackWeeks < BaselineWeeks`,
  `WindowDays + 7 × MaxBaselineLookbackWeeks ≥ 180` (the resolved-finding retention in 007; past it,
  a purged reported day would come back as a "normal" sample), `MinMedianRows < 0`,
  `LowPercent ∉ [1,99]`, `HighMultiple ≤ 1`) → log Warning, skip the WHOLE volume check: no query,
  `DATA_VOLUME` neither checked nor degraded (nothing closes, nothing opens). Per-loader invalid (effective
  values out of range, bad date) → Warning, skip that loader; its open findings get `KeepOpen` (A3). Never
  a MISCONFIGURED finding.

### D2. Signals and codes (`WatchdogModels.cs`)
- `WatchdogSignals.DataVolume = "DATA_VOLUME"` — NOT added to `WatchdogSignals.All`.
- `WatchdogDegradedCodes.VolumeQuery = "VOLUME_QUERY"` blinds `DATA_VOLUME`.
- `NO_LOADERS_CONFIGURED` must ALSO blind `DATA_VOLUME` (an empty `Loaders` section would otherwise close
  every open one as `ConfigRemoved`).
- `HISTORY_QUERY` pass: volume is not evaluated, `DATA_VOLUME` not checked.
- Hint constant `KeepOpen` (keep it separate from `WatchdogResolutionReasons`, e.g.
  `WatchdogResolutionHints.KeepOpen`, because it is not a reason the table can store).

### D3. Read path (`IWatchdogRepository` / `SqlWatchdogRepository`)
- `GetDailyVolumesAsync(DateOnly fromCentral, DateOnly toCentralInclusive, ct)` →
  `DailyVolume(LoaderName, DayCentral, Rows long, SuccessfulRuns, TotalRuns, LoaderRunIds int[])`, only
  days with ≥ 1 successful run (A1). Inline parameterised SELECT, `SUM(CAST(ISNULL(TotalRecords,0) AS
  BIGINT))`, same blank-name filter as `GetLastRunsAsync`. Bound the `StartTime` predicate as a half-open
  range `>= @From AND < DATEADD(day,1,@To)` (sargable).
- `GetVolumeExcludedDaysAsync(DateOnly from, DateOnly to, ct)` → the `(LoaderName, DayCentral)` of
  `DATA_VOLUME` findings in `core.WatchdogFinding` that are open or resolved with a reason other than
  `NotDetected` (A5). Day parsed from the key's last `|` segment; unparseable keys ignored.
- Either read failing → `DEGRADED|VOLUME_QUERY`, pass `Degraded`, `DATA_VOLUME` in `SignalsDegraded`.

### D4. `WatchdogVolumeEvaluator` (new, pure, static)
Input: daily volumes, excluded days, `WatchdogSettings`, `DateOnly todayCentral`. Output a result carrying:
- `Anomalies` — `VolumeAnomaly(LoaderName canonical, DayCentral, Direction Low|Zero|High, Rows,
  SuccessfulRuns, TotalRuns, MedianRows double, Ratio, LowPercent, HighMultiple, Samples
  [(day, rows)], ExcludedSampleDays, BaselineFromCentral?, LoaderRunIds)`.
- `JudgedInRange` — the `(loader, day)` pairs judged and found normal.
- `Unjudged` — `(loader, day, reason)` for logging (pass log only, Information).
- `WindowStart` / `WindowEnd` (oldest / newest checked day: today−WindowDays … today−1).
- `SkippedLoaders` — invalid per-loader config, with reason (Warning).
Rules: §R.4 with A1/A2/A5. Median = true median (mean of middle two). Order: unjudged if samples <
`MinSamples` or median < effective `MinMedianRows`; else Zero (`rows = 0 && median > 0`) → Low
(`rows < median × LowPercent / 100`) → High (`rows > median × HighMultiple`). Loaders: entries in
`Loaders` with `Enabled` and `Volume?.Enabled != false`, matched case-insensitively, canonical spelling.
Today is never checked.

### D5. Findings (`WatchdogFindingMapper`)
Exactly §R.5: key `DATA_VOLUME|<Loader>|<yyyy-MM-dd>`, `RunId` NULL, Zero/Low → Warning, High → Info,
Summary per §R.5 (redacted via the same `Summary()` path, ≤ 400 chars), DetailJson fields per §R.5 plus
`totalRuns` (A1); `ratio` rounded to 2 dp; days as `yyyy-MM-dd`; `weekday` as English day name. Volume rows
are passed into `BuildFindings` separately from the report so `Notify=false` leaves the report untouched.

### D6. Sink hints (`WatchdogFindingSink` / `WatchdogFindingMapper.BuildHints`)
The table in A3, plus A4. The sink needs this pass's volume outcome (window start, judged-in-range keys,
whether the volume check ran). Carry it on `WatchdogPassRecord` as an optional property (null when the
check did not run), so existing callers and tests are unaffected. If `DATA_VOLUME` is not in
`SignalsChecked`, send no DATA_VOLUME hints (except A4).

### D7. Runner, report, email
- Runner: after the existing secondary reads, if `DataVolume.Enabled` and section valid and
  `Loaders` non-empty → run both reads; on success evaluate and add `DATA_VOLUME` to `SignalsChecked`;
  on failure add `DEGRADED|VOLUME_QUERY`. Log `"{n} volume anomalies, {m} unjudged"`.
- `Notify=false`: `WatchdogReport` gets nothing; email subject/body/`ShouldNotify` unchanged.
  `Notify=true`: report carries `VolumeAnomalies`; "ROW VOLUME" section after RECENT FAILURES, one line
  per anomaly (same text as Summary), part of `ShouldNotify` and the subject
  (`"{n} row-volume anomal{y|ies}"`). `WithPersistenceWarning` must copy it.

### D8. SQL (`sql/Core/008_WatchdogDataVolume.sql`)
- `USE DataLoaderPlatform; GO`, `SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;`.
- `CREATE OR ALTER PROCEDURE core.usp_RecordWatchdogPass` — the FULL 007 proc with two changes in step 3-5:
  1. closure CASE gains `WHEN w.Signal = 'DATA_VOLUME' THEN CASE WHEN h.ResolutionReason IN
     ('AgedOutUnresolved','LoaderDisabled','ConfigRemoved') THEN h.ResolutionReason ELSE 'NotDetected' END`.
     `DATA_STALE` keeps `ELSE 'NotDetected'`.
  2. the WHERE gains `AND NOT (w.Signal = 'DATA_VOLUME' AND h.ResolutionReason = 'KeepOpen')`.
  Nothing else changes; no table/TVP/constraint changes.
- Guarded grant: `GRANT SELECT ON OBJECT::core.WatchdogFinding TO [ARM\sqltask_svc]` (PRINT if missing).
- ⚠ Re-running 007 after 008 reverts the proc. Add a header note to 007 saying so, and that 008 must be
  re-run after it.
- Tests: `sql/Core/tests/008_WatchdogDataVolume.Tests.sql`, same rollback-only style as 007's tests.

### D9. Deploy order (hand to the user; never run by an agent)
1. `008` on `ARMH-OPSDB01`/`DataLoaderPlatform` (SSMS).
2. Release with `DataVolume.Enabled` absent/false (`.\scripts\Release.ps1`).
3. When ready: config `DataVolume.Enabled = true` (+ ICE/AGSI `BaselineFromCentral`), committed, released.

---

## R. Source requirements (verbatim summary)

1. **Goal:** tell the owner when a loader ran successfully but loaded an unusual number of rows. Findings flow
   `core.WatchdogFinding` → DBA Agent `WATCHDOG` rule. Out of scope: `DATA_QUALITY`; `DATA_STALE` reserved.
2. **Non-negotiables:** (1) no behaviour change to existing signals/email; (2) off by default — with it off
   the pass is byte-for-byte today's; (3) persisted, not emailed, by default (`Notify=false`); (4) a degraded
   check never resolves anything (`DEGRADED|VOLUME_QUERY`); (5) a learned baseline must not raise its own
   goalposts (exclusion); (6) ageing out is not being fixed (`AgedOutUnresolved`); (7) Central times stay
   Central in DetailJson (`*Central`); (8) `USE DataLoaderPlatform; GO`, `Test-SqlParses.ps1`, gates;
   (9) nothing applied to ARMH-OPSDB01 by the agent.
3. **Data source:** `core.LoaderRun_Summary`; day = `CAST(StartTime AS date)` (Central); blank names
   ignored; only `Loaders` entries with `Enabled` and `Volume.Enabled`. *(Row sum amended by A1.)*
4. **Detection:** 4.1 last `WindowDays` (3) completed Central days, never today. 4.2 same-weekday samples,
   true median, `MinSamples` (4), `MinMedianRows` (100) *(sample set amended by A2)*. 4.3 exclusion of
   abnormal days; `BaselineFromCentral` for legitimate level changes *(amended by A5)*. 4.4 Zero (Warning),
   Low `< median × LowPercent/100` (Warning), High `> median × HighMultiple` (Info); 3× because a rerun
   doubles a day.
5. **Finding:** key `DATA_VOLUME|<Loader>|<yyyy-MM-dd>`; `RunId` NULL; Summary e.g. `Marex loaded 2,500 rows
   on Thu 2026-11-19 (Central), 29% of its usual Thursday (8,700).` / High `... 3.6x its usual Monday
   (190,000).`; DetailJson `dayCentral, weekday, direction, rows, successfulRuns, medianRows, ratio,
   lowPercent, highMultiple, samples[{dayCentral,rows}], excludedSampleDays[], baselineFromCentral,
   loaderRunIds (≤20)`. Lifecycle: opens; refreshed each pass; `NotDetected` on rerun; `AgedOutUnresolved`
   when the day leaves the window; `LoaderDisabled`/`ConfigRemoved` *(+ A3, A4)*.
6. **Settings:** as D1.
7. **Write path:** as D2–D8.
8. **Permissions:** `SELECT ON core.WatchdogFinding` for the watchdog identity (`ARM\sqltask_svc`, which is
   db_owner today — stated anyway); `DBAgent` unchanged.
9. **Deliverables:** 008 + SQL tests; Core code; Core tests; runbook `DATA_VOLUME` section; CLAUDE.md
   paragraph; deployment commands as text.
10. **Acceptance tests:** 1 disabled = no query/no signal; 2 Low ≈0.24; 3 Zero; 4 2× = nothing; 5 3.5× =
    High Info; 6 three samples = nothing; 7 median 15 = nothing; 8 weekend dip = nothing; 9 rerun →
    NotDetected; 10 leaves window → AgedOutUnresolved; 11 five low Thursdays all reported, earlier ones in
    `excludedSampleDays`; 12 `BaselineFromCentral` → silent until 4 new samples; 13 query throws → open
    finding untouched, `DEGRADED|VOLUME_QUERY`, pass Degraded; 14 next pass recovers → degraded closed;
    15 `Notify=false` → persisted, not emailed; 16 `Notify=true` → ROW VOLUME section, `ShouldNotify`;
    17 `Volume.Enabled=false` → LoaderDisabled; 18 per-loader `LowPercent=30`, day 40% → nothing;
    19 NULL `LoaderName` ignored; 20 proc twice → one open row; 21 `MinSamples=7 > BaselineWeeks=6` →
    skipped with warning, no finding.
    **Added by the amendments:** 22 retry day (failed run 480 + success 5 vs median 500) → nothing (A1);
    23 day with only failed runs → not judged, not a sample (A1); 24 permanent drop for 8 weeks → still
    reported, baseline from pre-drop samples within 18 weeks (A2); 25 open finding, day in window, now
    unjudged → `KeepOpen`, stays open (A3); 26 open finding, `BaselineFromCentral` set ≤ its day → closed
    `NotDetected` (A3); 27 master switch off with open findings → closed `LoaderDisabled`; off with none
    open → pass identical to today (A4); 28 `LoaderDisabled`-closed day stays excluded (A5);
    29 `NO_LOADERS_CONFIGURED` → `DATA_VOLUME` not checked, nothing closes.
