# Runbook: watchdog findings (`core.WatchdogPass` / `core.WatchdogFinding`)

Every hourly watchdog pass (`\DLP\Watchdog` → `DataLoader.Host.exe --watchdog`) now
records itself in `DataLoaderPlatform` on `ARMH-OPSDB01` alongside sending its email:

| Object | What it is |
|---|---|
| `core.WatchdogPass` | One row per pass. `MAX(StartedAtUtc)` is the **heartbeat**. |
| `core.WatchdogFinding` | One row per finding, from first detection to resolution. |
| `core.vw_WatchdogFinding_Open` | Open findings plus `OpenMinutes`. **Main read surface (DBA Agent).** |
| `core.usp_RecordWatchdogPass` | The only write path. One transaction per pass, plus a retention sweep. |

Source: `sql/Core/007_CreateWatchdogFindings.sql`. Behaviour tests:
`sql/Core/tests/007_WatchdogFindings.Tests.sql` (rollback-only; run on a DEV copy).
Requirements: DBA Agent's `2026-11-12-watchdog-findings-requirements.md`.

This records **detection only**. Ack, assignment and notification state live in the DBA
Agent (`DBAdmin`), never here.

## Deploying (first time)

**Order matters: SQL first, then the release.** A config change goes live with the
release (`PersistFindings` ships `true`), and with no 007 in place every pass logs a
persistence failure and adds a "NOT persisted" line to any email it sends. That's noisy
but never harmful.

1. Run `sql/Core/007_CreateWatchdogFindings.sql` against `ARMH-OPSDB01` in SSMS. It's
   re-runnable. It PRINTs if the `DBAgent` user is missing from the database. If so,
   create that user and re-run the script to apply the read grants. *(Deployed to
   `ARMH-OPSDB01` 2026-11-13, with the `DBAgent` grants in place.)*
2. `.\scripts\Release.ps1`. ⚠ `Deploy-Release.ps1` smoke-tests the new release with
   `DataLoader.Host.exe --watchdog`, so **the deploy itself records a real pass**
   (`AppVersion` = the new release folder). That is expected.
3. Verify with the queries below. The first hourly pass after the release should
   appear with `Status = 'Complete'` and `CompletedAtUtc` set.

## Everyday queries

```sql
USE DataLoaderPlatform;

-- Heartbeat: is the watchdog running? (The DBA Agent alerts at > 90 minutes.)
SELECT TOP (5) PassId, StartedAtUtc, CompletedAtUtc, Status, SignalsDegraded, OpenCount, AppVersion
FROM core.WatchdogPass ORDER BY StartedAtUtc DESC;

-- What's wrong right now.
SELECT Signal, LoaderName, Severity, Summary, OpenMinutes, DetectionCount
FROM core.vw_WatchdogFinding_Open ORDER BY Severity, OpenMinutes DESC;

-- Trend: how often each loader went overdue or failed in the last 30 days.
SELECT LoaderName, Signal, COUNT(*) AS Findings,
       SUM(DATEDIFF(MINUTE, FirstDetectedAtUtc, ISNULL(ResolvedAtUtc, SYSUTCDATETIME()))) AS MinutesOpen
FROM core.WatchdogFinding
WHERE FirstDetectedAtUtc >= DATEADD(DAY, -30, SYSUTCDATETIME()) AND Signal IN ('OVERDUE','FAILED')
GROUP BY LoaderName, Signal ORDER BY Findings DESC;
```

## How findings open and close

- **Identity:** `FindingKey` is one of:
  - `OVERDUE|<Loader>` or `MISCONFIGURED|<Loader>`: the loader name uses the config spelling.
  - `STUCK|<RunId>` or `FAILED|<RunId>`.
  - `DATA_VOLUME|<Loader>|<yyyy-MM-dd>` (see [DATA_VOLUME](#data_volume-a-loader-ran-successfully-but-wrote-an-unusual-number-of-rows)).
  - `DEGRADED|<code>`, where the code is `HISTORY_QUERY`, `STUCK_QUERY`, `FAILURES_QUERY`,
    `NO_LOADERS_CONFIGURED`, `FAILURE_RESOLUTION` or `VOLUME_QUERY`.

  There is one open row per key. A key that clears and comes back later opens a new row.
- **A degraded check never resolves anything.** If a query could not run, its signal is
  left out of `SignalsChecked` and its open findings stay exactly as they were.
  - `NO_LOADERS_CONFIGURED` blinds OVERDUE and MISCONFIGURED.
  - A `HISTORY_QUERY` failure blinds everything. That pass is `Status = 'Failed'` and
    closes nothing.
- **Ageing out isn't the same as being fixed.**
  - A STUCK finding closes as `AgedOutUnresolved` if its `core.LoaderRun` row is still
    open, and as `NotDetected` once the run row has closed.
  - A FAILED finding closes as `SelfHealed` only when the resolver confirms that every
    failed unit later succeeded. Otherwise it closes as `AgedOutUnresolved`.
- **Other closing reasons:**
  - OVERDUE and MISCONFIGURED close as `LoaderDisabled` (the loader's config was set to
    `Enabled = false`), `ConfigRemoved` (the loader was removed from
    `Platform:Watchdog:Loaders`), or `NotDetected` (fixed).
- **Self-healed failures** (the email never shows them) are recorded once, already
  resolved, with `Severity = 'Info'`. They're kept for trend history only.

## When something is wrong

| Symptom | Meaning / action |
|---|---|
| Email line `Findings were NOT persisted: …` | The sink failed. The email and exit code are unaffected. Full exception is in `Log\Watchdog.log`. Common causes: 007 not deployed, platform DB unreachable, timeout. The budget is a hard bound: `PersistTimeoutSeconds` (default 60), capped at 15s when the history read already failed, with half of it given to the FAILED resolution read. |
| Open `DEGRADED\|FAILURE_RESOLUTION` | The no-window read that decides SelfHealed vs AgedOutUnresolved failed or timed out. Open FAILED findings are frozen, not closed, until it succeeds on a later pass. |
| No new `WatchdogPass` row for > 90 min | Either the watchdog isn't running (check the `\DLP\Watchdog` task and `Log\Watchdog.log`) or every pass is failing to persist on an all-clear (no email is sent in that case, so the log is the only place it shows). |
| Latest pass `Status = 'Failed'` | The primary history query failed. A `DEGRADED\|HISTORY_QUERY` finding is open, and the fallback email went out. |
| Pass row with `CompletedAtUtc` NULL | Shouldn't happen: the pass is one transaction. Investigate. |
| `Watchdog findings retention sweep failed` (Warning in log) | The pass **was** recorded. Only the cleanup failed. It retries next pass. |
| Need to stop persisting now | Set `Platform:Watchdog:PersistFindings` to `false` in the live `Releases\<current.txt>\appsettings.json`. That's a production write: do it deliberately. It takes effect from the next pass. Detection and email are unchanged. |

## DATA_VOLUME: a loader ran successfully but wrote an unusual number of rows

Added 2026-11-20 (design: `docs/superpowers/specs/2026-11-20-watchdog-data-volume-design.md`;
SQL: `sql/Core/008_WatchdogDataVolume.sql`). **Off by default**
(`Platform:Watchdog:DataVolume:Enabled = false`). While it's off and no `DATA_VOLUME`
finding is open, a pass is exactly what it was before: no extra query, and `DATA_VOLUME`
doesn't appear in `SignalsChecked`.

### What it measures

For each of the last `WindowDays` (3) **completed Central days** (never today), it takes a
loader's rows for that day and compares them with the median of the same weekday in earlier
weeks.

**Settle lag:** yesterday counts only once the Central clock reaches `SettleHours` (default
3, allowed 0–23). Before that, the window ends the day before yesterday and is still 3 days
long. A summary row is written only when its run *completes*, and runs start as late as
23:00 Central and last up to about two hours. A 00:05 pass would otherwise see half of
yesterday and report a false Low. An open finding for yesterday on such a pass stays open.

The rules:

- **A day's rows** = `SUM(TotalRecords)` over **every** `core.LoaderRun_Summary` run that
  started that Central day, failed runs included. A day counts only if at least one of its
  runs succeeded. (On retry days the rows are split between the failed run and the retry;
  counting only successes made AGSI look 98% down every day.) A day with only failed runs is
  never judged; FAILED covers it.
- **It counts rows WRITTEN, not rows available.** Merge/hash loaders (CWG, NUCORE's merges)
  count only rows that changed. A settled unit counts 0. A code change that stops needless
  re-writes is a real, permanent drop.
- **Baseline:** walk back D−7, D−14, … up to `MaxBaselineLookbackWeeks` (18). Skip days with
  no successful run, days before the loader's `BaselineFromCentral`, and **excluded** days.
  Keep the most recent `BaselineWeeks` (6). A day is excluded when a `DATA_VOLUME` finding
  for it is open, or closed with anything other than `NotDetected`. So a reported day never
  becomes "normal", unless a rerun brings it back into range.
- **Not judged** when there are fewer than `MinSamples` (4) samples, or the median is below
  `MinMedianRows` (100, applied to the DAILY total) or is 0.
- **Verdict** (first match wins): **Zero** = 0 rows against a non-zero median (Warning).
  **Low** = below `median × LowPercent / 100` (50%; Warning). **High** = above
  `median × HighMultiple` (3×; Info). The High threshold is 3× because an ordinary rerun
  doubles a day.

### Reading a finding

`FindingKey` = `DATA_VOLUME|<Loader>|<yyyy-MM-dd>` (Central day). `RunId` is NULL. Example
`Summary`: `Marex loaded 2,500 rows on Thu 2026-11-19 (Central), 29% of its usual Thursday
(8,700).` `DetailJson` contains:

| Field | Meaning |
|---|---|
| `dayCentral`, `weekday`, `direction` | The day judged and `Zero` / `Low` / `High`. |
| `rows`, `successfulRuns`, `totalRuns` | That day's total over all runs, and how many of them succeeded. |
| `medianRows`, `ratio` | Baseline median and `rows / medianRows` (2 dp). |
| `lowPercent`, `highMultiple` | The **effective** thresholds (per-loader overrides applied). |
| `samples[]` | `{dayCentral, rows}` for each sample used, newest first. |
| `excludedSampleDays[]` | Same-weekday days skipped because they were reported earlier. A long list means a persistent change, which usually calls for `BaselineFromCentral`. |
| `baselineFromCentral` | The loader's declared level-change date, or null. |
| `loaderRunIds` | The day's `core.LoaderRun_Summary.LoaderRunId`s (at most 20). |

```sql
USE DataLoaderPlatform;
SELECT LoaderName, Severity, Summary, DetailJson, OpenMinutes
FROM core.vw_WatchdogFinding_Open WHERE Signal = 'DATA_VOLUME' ORDER BY LoaderName, FindingKey;
```

### Closing

A finding is refreshed on every pass where its day is still abnormal. When it is no longer
detected, it closes as follows (first match wins):

| Situation | Result |
|---|---|
| `DataVolume.Enabled` switched to `false` | closed `LoaderDisabled` |
| Loader removed from `Platform:Watchdog:Loaders` | closed `ConfigRemoved` |
| Loader `Enabled = false` or `Volume.Enabled = false` | closed `LoaderDisabled` |
| Key has no readable day (malformed) | closed `AgedOutUnresolved` |
| Day is older than the window | closed `AgedOutUnresolved` (aged out, **not** fixed) |
| Day re-judged and now within range (e.g. a rerun) | closed `NotDetected` |
| Loader's `BaselineFromCentral` is on or before the day | closed `NotDetected` (you declared that level normal) |
| Day in the window but **can't be judged** this pass, or newer than the window (yesterday before `SettleHours`) | **stays open** (`KeepOpen` hint) and ages out later |

The "can't be judged" cases are: too few samples, median too small or 0, invalid
per-loader config, or no successful run. `KeepOpen` is a hint the sink sends to the proc. It is never
stored as a `ResolutionReason`. When the master switch is turned off, the sink closes the
leftover findings with `LoaderDisabled`. It does **not** do this if
`Platform:Watchdog:Loaders` is empty, because that looks the same as config that failed to
bind.

If either volume read fails or is cancelled, the pass is `Degraded` and a
`DEGRADED|VOLUME_QUERY` finding opens. Open `DATA_VOLUME` findings stay untouched until a later
pass reads successfully. An open `DEGRADED|VOLUME_QUERY` is carried forward ("not
re-attempted") on passes where the check doesn't run, such as an invalid section or an empty
`Loaders`. It closes only when a later read succeeds, or when the master switch is turned off.
`NO_LOADERS_CONFIGURED` and `HISTORY_QUERY` also leave `DATA_VOLUME` unchecked. An invalid
`DataVolume` section logs a Warning and skips the check. Examples: `MinSamples > BaselineWeeks`,
`WindowDays` outside 1–7, `SettleHours` outside 0–23, or
`WindowDays + 7 × MaxBaselineLookbackWeeks ≥ 180`. The last limit exists because resolved
findings are purged after 180 days, after which a reported day would count as a normal sample
again. When the check is skipped, nothing opens or closes, and it's never a MISCONFIGURED
finding. An invalid
per-loader `Volume` block (a bad date, `LowPercent` outside 1..99, `HighMultiple ≤ 1`) skips
just that loader with a Warning, and its open findings stay open.

### Email

With `Notify = false` (the default), findings are persisted only. The email, its subject and
whether it's sent at all are unchanged, and a `VOLUME_QUERY` degradation is also kept out of
the email. With `Notify = true`, the email gets a **ROW VOLUME** section after RECENT
FAILURES (one line per anomaly, the same text as `Summary`), the subject gains
`N row-volume anomaly/anomalies`, and an anomaly alone is enough to send the email.

### Settings

```json
"Platform": { "Watchdog": {
  "DataVolume": { "Enabled": true, "Notify": false, "WindowDays": 3, "SettleHours": 3, "BaselineWeeks": 6,
                  "MaxBaselineLookbackWeeks": 18, "MinSamples": 4, "MinMedianRows": 100,
                  "LowPercent": 50, "HighMultiple": 3 },
  "Loaders": {
    "ICE":   { "MaxGapHours": 26, "Volume": { "BaselineFromCentral": "2026-11-11" } },
    "Marex": { "MaxGapHours": 26, "Volume": { "LowPercent": 30 } },
    "Argus": { "MaxGapHours": 26, "Volume": { "Enabled": false } }
  } } }
```

(`MaxGapHours` values above are placeholders. Keep each loader's real value.)

- **Mute one loader:** `"Volume": { "Enabled": false }`. Its open findings close as
  `LoaderDisabled`. Setting the loader's own `Enabled = false` mutes every signal for it.
- **`BaselineFromCentral` (`yyyy-MM-dd`, Central, quoted string):** use it when a loader's
  level changed **legitimately and permanently**. Samples before that date are ignored, so
  the loader stays silent until `MinSamples` new same-weekday samples exist (about 4 weeks).
  Open findings on or after the date close as `NotDetected`. Without it, a permanent change
  is reported every day for up to 18 weeks against the old level. Known cases to set **when
  enabling**:
  - **ICE `2026-11-11`.** It dropped from ~16.6M to ~0.75M rows/day when the `LastModified`
    hot key stopped re-merging unchanged files. Without this, it reports every day.
  - **AGSI `2026-11-17`** (recommended). It stepped from ~2,000 to 482 rows/day when the
    transient-retry fix ended re-writes.
- EvolutionMarkets **is** judged: its daily median is ~123, which is above `MinMedianRows`.

### Deploying (commands for the operator to run; an agent never runs these)

1. In SSMS, connected to `ARMH-OPSDB01`, run `sql/Core/008_WatchdogDataVolume.sql`. It
   starts with `USE DataLoaderPlatform;`. It replaces `core.usp_RecordWatchdogPass` so the
   proc honours the volume hints. ⚠ **Re-running 007 after 008 reverts the proc**, so re-run
   008 afterwards.
2. Release with `DataVolume.Enabled` absent or `false`:
   ```powershell
   .\scripts\Release.ps1 -DryRun
   .\scripts\Release.ps1
   ```
   The pass is unchanged at this point.
3. When ready, set `Platform:Watchdog:DataVolume:Enabled = true` in
   `src/DataLoader.Host/appsettings.json`, plus `BaselineFromCentral` for ICE (and AGSI). Commit,
   push, then run `.\scripts\Release.ps1` again. The config goes live with the release.
   Check the first pass:
   ```sql
   USE DataLoaderPlatform;
   SELECT TOP (3) StartedAtUtc, Status, SignalsChecked, SignalsDegraded
   FROM core.WatchdogPass ORDER BY StartedAtUtc DESC;   -- expect DATA_VOLUME in SignalsChecked
   ```

## Retention

At the end of each pass, in batches of 5,000:
- resolved findings older than 180 days are deleted;
- passes older than 90 days that no finding still references are deleted.

Expect about 8,800 passes a year.

## Permissions

| Principal | Grant |
|---|---|
| `ARM\sqltask_svc` (runs `\DLP\Watchdog`) | EXECUTE on the proc and both TVP types, SELECT on the view. It's also `db_owner` today, so these are the intended least privilege rather than what actually gates it. |
| `DBAgent` (DBA Agent, read-only; a **SQL login**, not a Windows account) | SELECT on both tables and the view. Nothing else in `DataLoaderPlatform`. |
| `ARM\sqltask_svc`'s write path | The watchdog itself still writes as the Watchdog task's own Windows identity (`Integrated Security=SSPI`). `DBAgent` cannot write here. |
