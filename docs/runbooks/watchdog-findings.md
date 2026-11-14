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
  - `DEGRADED|<code>`, where the code is `HISTORY_QUERY`, `STUCK_QUERY`, `FAILURES_QUERY`,
    `NO_LOADERS_CONFIGURED` or `FAILURE_RESOLUTION`.

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
