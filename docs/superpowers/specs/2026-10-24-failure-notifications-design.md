# Failure Notifications — Design Spec

- **Date:** 2026-10-24
- **Scope:** `DataLoader.Core` (new `Notifications/` component), `DataLoader.Host` (`Program.cs`), `appsettings.json`
- **Status:** Approved via brainstorming; pending written-spec review
- **Channel:** Email over ARM's internal SMTP relay. Zoom deliberately deferred.
- **New SQL objects:** none. The watchdog is `SELECT`-only.

## 1. Goal

Tell a human when a loader run fails, without anyone having to read
`C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log`.

Two mechanisms, because no single one covers every failure class:

1. **In-process notifier** — `DataLoader.Host` emails at the end of its own run.
2. **Absence watchdog** — a second invocation mode of the same exe, on its own
   schedule, that notices runs which never happened or never finished.

### 1.1 Why both — the failure matrix

| Failure class | In-process | SQL-side (end of run) | Watchdog |
|---|---|---|---|
| Loader fails, process exits normally | yes | yes | yes (late) |
| Unhandled exception at top of `Main` | yes | only if the run row closed | yes |
| **Platform DB unreachable** | yes | **no** | yes |
| Process killed / OOM / machine reboot | **no** | **no** | yes |
| Scheduled task never fired | **no** | **no** | yes |

The third row is why the notifier is in C# rather than in SQL. The last two are
why the watchdog exists: in-process code cannot report its own death.

### 1.2 Why now — measured 2026-10-23 against `ARMH-OPSDB01`

In the previous 7 days, production recorded **43 failed runs** and **5 runs that
never closed**, with no notification of any kind:

| Loader | Failed / runs | Loader | Failed / runs |
|---|---|---|---|
| EnergyAspects | **6 / 7** | AGSI | 7 / 14 |
| IHSPointLogic | 11 / 83 | EvolutionMarkets | 6 / 91 |
| ICE | 7 / 44 | NUCORE | 3 / 9 |
| Criterion | 2 / 106 | EIA | 1 / 4 |

Never-closed runs (the killed-process class): StormVista x3, NUCORE x1, EIA x1.

EnergyAspects failing 6 of 7 runs is a standing outage this feature will start
reporting on day one. **Fixing it is out of scope** — see §9.

## 2. Production facts this design depends on

All verified by `SELECT` against `ARMH-OPSDB01` on 2026-10-23.

### 2.1 `core.LoaderRun_Summary` already exists and is live

It is a **table**, not a view, and production's `core.usp_CompleteLoaderRun`
MERGEs into it. One row **per loader per run**:

```
Id, LoaderRunId, LoaderName, StartTime, EndTime, Success,
UnitsPass, UnitsFail, UnitsTotal, TotalRecords, HasErrors,
AddProcRan, HasAddProcErrors, AddProcPass, AddProcFail,
AddProcStartTime, AddProcEndTime
```

This is the entire data source for the watchdog. **No new SQL objects are
required.**

### 2.2 Production's core schema has drifted from the repo

Two differences, recorded here because they are load-bearing and are **not**
fixed by this work:

- `core.LoaderRun` has a 6th column in production, `LoaderRunId INT NOT NULL`,
  which `sql/Core/001_CreateCoreSchema.sql` does not create.
- Production's `core.usp_CompleteLoaderRun` contains the `LoaderRun_Summary`
  MERGE; the repo's `sql/Core/002_CreateCoreProcedures.sql` version does not.

A clean deploy of `sql/Core/` to a fresh server would therefore produce a
database the watchdog cannot read. Out of scope to reconcile, but it must not
be discovered by surprise later.

### 2.3 `LoaderRun_Summary.StartTime` is CENTRAL, `LoaderRun.StartedAtUtc` is UTC

Production's `usp_CompleteLoaderRun` converts UTC to Central Standard Time when
writing `StartTime`. `core.LoaderRun.StartedAtUtc` is raw UTC.

**Mixing them makes every loader look ~5-6 hours overdue.** The watchdog
evaluator takes the comparison instant per source and a unit test pins this.

### 2.4 One scheduled task per loader

23 loaders ran in the last 30 days while `Platform:EnabledLoaders` lists only 4,
confirming each `\DLP\` task passes its loader as argv. Consequence: an
in-process email is naturally rate-limited to one per loader per scheduled run,
which is why no suppression window is needed (§9).

### 2.5 Observed 30-day cadence — seeds the watchdog thresholds

Mean gap between runs, in minutes:

| Loader | Avg | Loader | Avg | Loader | Avg |
|---|---|---|---|---|---|
| Criterion | 109 | AGSI | 763 | NGI | 1285 |
| IHSPointLogic | 125 | Argus | 900 | Vulcan | 1305 |
| EvolutionMarkets | 140 | EnergyAspects | 1044 | OilX | 1349 |
| ICE | 277 | OPIS | 1058 | IIR | 1386 |
| EOX | 371 | NGX | 1090 | StormVista | 1392 |
| EIA | 461 | NUCORE | 1148 | Marex | 1440 |
| Platts | 529 | CWG | 636 | ModernCommodities / Genscape / CME | 1440 |

## 3. Component layout

New folder `src/DataLoader.Core/Notifications/`:

| Type | Responsibility |
|---|---|
| `IFailureNotifier` / `FailureNotifier` | "Something failed — tell someone." Builds a message from a `RunFailureReport`. **Never throws.** |
| `INotificationSender` / `SmtpNotificationSender` | "Put this message on the wire." A future Zoom sender is one new class with no caller changes. |
| `NullNotificationSender` | Used when `Enabled: false`, so no call site needs a null check. |
| `NotificationMessage` | `Subject`, `Body` (plain text). |
| `RunFailureReport` | Immutable: host, runId, started/ended, per-loader `LoaderRunResult`s, optional exception, stale-row count. |
| `NotificationSettings` | Bound from `Platform:Notifications`; **also constructible directly from `IConfiguration`** (see §4.2). |
| `ErrorTextRedactor` | Strips secrets out of any vendor text before it can be emailed (§6.1). |
| `WatchdogEvaluator` / `WatchdogReport` | Pure function over run history. No DB. |
| `WatchdogSettings` | Bound from `Platform:Watchdog`. |

Two seams on purpose. `IFailureNotifier` is the domain decision ("is this worth
an alert, and what does it say"); `INotificationSender` is the transport. They
are tested independently, and swapping or adding a channel touches only the
transport.

### 3.1 The notifier must never fail a run

`FailureNotifier` wraps every send in `try/catch` and a `TimeoutSeconds` budget
(default 30s), logging failures at `Error`. A wedged relay must not hang a
scheduled task, and a notification failure must never change the exit code or
fail a good load. This mirrors the existing precedent in
`SqlAdditionalProcessRunner`, which swallows AddProc failures for the same
reason.

**It never throws, but it does report.** `NotifyAsync` returns a
`NotificationOutcome` (`Sent`, `Disabled`, `NothingToReport`, `Failed`) rather
than `void`. `PlatformHost` ignores the result — a failed email must not change
a load's outcome. The watchdog *uses* it, because "the watchdog could not reach
the relay" is the one case where a human needs the non-zero exit code to notice
(§5). Without this return value those two requirements contradict each other.

Transport: `System.Net.Mail.SmtpClient` with `EnableSsl = true` (STARTTLS) and
no credentials. It handles the chosen endpoint (§7.1) and adds no dependency.
`INotificationSender` exists so that moving to MailKit — which would become
necessary only if the transport ever needs OAuth, something `SmtpClient` cannot
do — is a one-class change.

## 4. Where it fires

### 4.1 `PlatformHost.RunAsync`

Call site: **after** `LogSummary`, i.e. after the `finally` that closes the
`core.LoaderRun` row. Closing that row must stay fast; SMTP is not fast.

Trigger: `!allSucceeded || staleRowsClosed > 0`.

Deliberately **not** on cancellation. `ParallelRunner` rethrows
`OperationCanceledException`, so a cancelled run never reaches this code. An
operator pressing Ctrl+C or ending the task is not a failure; a *killed* process
is, and §5 covers it.

### 4.2 `Program.cs` — requires a structural change

Today `BuildServiceProvider` sits **outside** the `try`, so a DI or
configuration failure throws out of `Main` with no catch at all:

```csharp
await using var services = BuildServiceProvider(...);   // outside try
...
try { return await host.RunAsync(cts.Token); }
catch (Exception ex) { ... }
```

Change: widen the `try` to cover everything after `BuildConfiguration()`, and
construct the bootstrap notifier **directly from `IConfiguration`**, without DI,
so the one failure class that breaks DI can still send mail.

This path cannot resolve `SEE_DB`, because that needs the database and DI.
**That is not a limitation here:** the relay is anonymous (§7.1), so the
bootstrap notifier needs no secret and works even when DI and the database are
both unavailable. If the transport ever moves to an authenticated endpoint,
this gap reopens and must be re-examined.

### 4.3 Never-closed detection

`SweepStaleLoadLogsAsync` already receives the count of rows earlier runs left
open and currently only logs a warning
(`src/DataLoader.Core/Hosting/PlatformHost.cs`, lines 67-71). That count is
carried into `RunFailureReport` and triggers an email **even when the current
run succeeded**.

Email wording: "N load-log row(s) from an earlier run were left open — that run
was killed." This is the in-process half of killed-process detection; the
watchdog is the other half, and they are allowed to overlap.

## 5. Watchdog mode

Invocation: `DataLoader.Host.exe --watchdog` (accept `/watchdog` too). The flag
is consumed in `ParseLoaderArguments` **before** loader-name resolution, because
an unrecognised argument is currently fatal.

One pass, then exit:

1. Read `Platform:Watchdog:Loaders`.
2. `SELECT LoaderName, MAX(StartTime) FROM core.LoaderRun_Summary GROUP BY LoaderName`,
   excluding `NULL`/empty `LoaderName` — one such row exists, from a run that
   produced no `core.LoadLog` rows.
3. **Overdue** — `now_central - lastStart > MaxGapHours`. Covers "the task never
   fired" and "the process died before writing anything".
4. **Stuck** — `core.LoaderRun` where
   `CompletedAtUtc IS NULL AND StartedAtUtc < now_utc - StuckAfterHours`.
   Covers "killed mid-run"; production has 5 such rows today.
5. **Recent failures** — `LoaderRun_Summary` rows with `Success = 0` in the last
   `ReportRecentFailureHours`. A safety net for when the in-process email itself
   could not send.
6. Any of 3/4/5 non-empty produces **one digest email**. All clear sends nothing.

Steps 3 and 4 read different tables with different time bases — see §2.3.

The decision logic is a pure function,
`WatchdogEvaluator.Evaluate(lastRuns, stuckRuns, recentFailures, settings, nowUtc, nowCentral)`
returning a `WatchdogReport`, so it unit-tests with no database.

**Exit code 0 even when loaders are overdue** — the watchdog did its job. Exit 1
only when the watchdog itself could not run (DB unreachable, SMTP broken). In
the DB-unreachable case it still attempts the email, because that state is
itself alert-worthy.

Loaders present in `LoaderRun_Summary` but absent from
`Platform:Watchdog:Loaders` are **ignored**, not reported — build-only loaders
that were run manually once must not alert forever. A loader configured but
never seen in the table **is** reported as overdue.

## 6. Email content

Subject carries the answer, because subject lines are what get read on a phone:

```
[DataLoader] FAILED: Platts on ARMH-OPSDB01 (2/14 units failed)
[DataLoader] WATCHDOG: 3 loaders overdue, 1 run stuck
```

Body, plain text:

- Run id, host, started/ended (UTC and Central), duration, exit code
- Per loader: status, units pass/fail/skipped, records, error message
- Exception type and stack trace when there was one
- Stale-row count when non-zero
- Pointer to `C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log`

Error text is truncated to 2,000 characters per loader so a pathological
exception cannot produce a multi-megabyte email.

### 6.1 Redaction — a NEW secret-exposure surface

Email leaves the machine; logs do not. Vendor error text is known to carry
secrets in this platform:

- **AGSI echoes the `x-key` back** inside the error body's `message` field
  (it contains the literal text `API key:` followed by the key).
- **OilX puts `api_key` in the query string**, so any URL in an exception is
  sensitive.

Every `ErrorMessage` and exception string passes through `ErrorTextRedactor`
before it can enter a `NotificationMessage`: strip query strings from URLs, and
mask values following known key-bearing tokens (`api_key`, `apikey`, `x-key`,
`key=`, `password`, `token`). A unit test asserts a crafted secret never reaches
the message.

## 7. Configuration

Added to `src/DataLoader.Host/appsettings.json` under `Platform`:

```jsonc
"Notifications": {
  // UPDATE 2026-10-27: now ships TRUE, and the C# property defaults to true
  // as well. The gate below was answered empirically on 2026-10-26 — a real
  // alert left ARMH-OPSDB01 and arrived, proving port-25 egress and that
  // "Reject Direct Send" is off. Originally:
  // Ships false. Flipped to true once §10 confirms ARMH-OPSDB01 can egress
  // on 25 and that Reject Direct Send is off.
  "Enabled": false,
  "SmtpHost": "armenergy-com.mail.protection.outlook.com",
  "SmtpPort": 25,
  "UseSsl": true,              // STARTTLS — required, see §7.1
  "From": "ARMHOPSDB01_SVC@armenergy.com",
  // Single recipient while this is proven out. EnergyAspects alone will
  // generate traffic from day one (§1.2). Widen by editing config — internal
  // @armenergy.com addresses only, see §7.1 point 3.
  "To": [ "yaroslav.chekhuta@armenergy.com" ],
  "SubjectPrefix": "[DataLoader]",
  "TimeoutSeconds": 30
},
"Watchdog": {
  "StuckAfterHours": 6,
  "ReportRecentFailureHours": 24,
  "Loaders": {
    "Criterion":         { "MaxGapHours": 6 },
    "IHSPointLogic":     { "MaxGapHours": 6 },
    "EvolutionMarkets":  { "MaxGapHours": 8 },
    "ICE":               { "MaxGapHours": 12 },
    "EOX":               { "MaxGapHours": 16 },
    "Platts":            { "MaxGapHours": 26 },
    "AGSI":              { "MaxGapHours": 26 },
    "Argus":             { "MaxGapHours": 26 },
    "OPIS":              { "MaxGapHours": 26 },
    "CWG":               { "MaxGapHours": 26 },
    "EnergyAspects":     { "MaxGapHours": 30 },
    "NGX":               { "MaxGapHours": 30 },
    "NGI":               { "MaxGapHours": 30 },
    "Vulcan":            { "MaxGapHours": 30 },
    "OilX":              { "MaxGapHours": 30 },
    "IIR":               { "MaxGapHours": 30 },
    "StormVista":        { "MaxGapHours": 30 },
    "Marex":             { "MaxGapHours": 30 },
    "ModernCommodities": { "MaxGapHours": 30 },
    "Genscape":          { "MaxGapHours": 30 },
    "CME":               { "MaxGapHours": 30 }
  }
}
```

`EIA` and `NUCORE` are omitted on purpose: both ran irregularly during
development (EIA last ran 2026-10-18) and would alert constantly. Add them when
their schedules are settled.

**Recipients:** one `To` list serves both the in-process emails and the watchdog
digest.

### 7.1 The relay is Exchange Online direct send — verified 2026-10-24

An `EHLO` probe of `armenergy-com.mail.protection.outlook.com:25` returned:

```
250-SIZE 157286400   250-PIPELINING   250-DSN      250-ENHANCEDSTATUSCODES
250-STARTTLS         250-8BITMIME     250-BINARYMIME
250-CHUNKING         250 SMTPUTF8
```

**No `AUTH` verb is advertised.** This host is Exchange Online's inbound **MX
connector**, used for *direct send*, not a submission endpoint. Four
consequences, all load-bearing:

1. **There is no password to store.** The supplied
   `ARMHOPSDB01_SVC@armenergy.com` credential cannot be used against this
   endpoint, and `SmtpPassword` / `SmtpUsername` are therefore **absent from
   the config** rather than set to `SEE_DB`. Dead config that looks like a
   working secret is worse than no config. The mailbox still matters as the
   `From` identity — it just never authenticates.
2. **`UseSsl: true` is mandatory.** `STARTTLS` is advertised and Microsoft
   requires TLS 1.2 on this path. In `System.Net.Mail` on port 25,
   `EnableSsl = true` means STARTTLS, not implicit TLS.
3. **Recipients must be internal.** Direct send only accepts recipients in the
   tenant's own accepted domains. Any address outside `@armenergy.com` is
   relay-denied. If an external recipient is ever needed, IT must create an
   Exchange **inbound connector** trusting the server's public IP — a
   different setup, not a config change.
4. **`SmtpPassword` is gone, so the §4.2 bootstrap gap is closed.** The
   notifier needs no secret, so it works before DI and with the database down.

**Two things the probe could NOT establish**, because it ran from a developer
workstation (the server greeted `Hello [108.44.45.57]`, not ARMH-OPSDB01):

- whether **ARMH-OPSDB01 can egress on port 25** and is SPF-authorised for
  `armenergy.com`;
- whether the tenant has Microsoft's **"Reject Direct Send"** setting enabled
  (rolled out in Exchange Online during 2025), which blocks precisely this
  pattern.

Both are §10 prerequisites. If either blocks it, the fallback is
`smtp.office365.com:587` with SMTP AUTH — which would reintroduce the secret,
reopen the §4.2 bootstrap gap, and likely force a move from
`System.Net.Mail` to MailKit, since Microsoft is retiring basic auth for SMTP
client submission in favour of OAuth and `SmtpClient` cannot do OAuth.

**Secrets, for the record:** `core.Param` is keyed `(LoaderName, ParamName)`
with a `Value` column, and all 36 existing rows belong to loaders. A
platform-level secret would be the first non-loader entry, resolved by a small
`PlatformSecretResolver` calling `ISeeDbParamStore` directly —
`SeeDbSettingsResolver<T>` cannot serve it, being constrained to
`LoaderSettingsBase` and keyed by loader id. **None of this is built**, because
the chosen endpoint takes no credential.

**Config gate:** `Publish-ToProd.ps1` fails closed when the repo's
`appsettings.json` differs from production's. These new keys guarantee a
difference, so the deploy must pass `-OverwriteSettings`.

## 8. Testing

Added to the existing `tests/DataLoader.Core.Tests` project, alongside
`PlatformHostTests.cs`:

**Notifier**
- Nothing sent when every loader succeeded and no stale rows were closed
- Sent when any loader reports `Success = false`
- Sent when `staleRowsClosed > 0` even though the run succeeded
- Sent on an unhandled exception
- **Never throws** when the sender throws; logs at `Error` instead
- Respects `TimeoutSeconds` — a sender that hangs does not hang the caller
- `Enabled: false` resolves `NullNotificationSender` and sends nothing
- Subject contains loader id, host and unit counts; body contains the log path
- `NotifyAsync` returns `Sent` / `Disabled` / `NothingToReport` / `Failed`
  correctly in each case (§3.1 — the watchdog's exit code depends on it)

**Transport configuration** (§7.1 regression guards)
- `SmtpNotificationSender` sets `EnableSsl = true` and leaves `Credentials`
  null — an authenticated send against this endpoint would fail
- A config supplying `SmtpUsername`/`SmtpPassword` is rejected at bind time
  with a message naming §7.1, rather than silently attempting AUTH

**Redaction**
- An `ErrorMessage` carrying the AGSI shape (the literal `API key:` followed by
  a key) never appears unmasked in the message
- A URL containing an `api_key` query parameter (the OilX shape) is stripped
- Error text longer than 2,000 chars is truncated

**Watchdog evaluator**
- Overdue when the gap exceeds `MaxGapHours`; not overdue exactly at the boundary
- Configured loader never seen in `LoaderRun_Summary` is reported overdue
- Loader present in the table but absent from config is ignored
- Stuck-run detection honours `StuckAfterHours`
- Empty input produces an empty report and no email
- **Time-base test:** a loader whose Central `StartTime` is 1 hour old is not
  reported overdue when compared against a UTC `now` (§2.3 regression guard)

**Argument parsing**
- `--watchdog` and `/watchdog` select watchdog mode
- `--watchdog` is not treated as an unknown loader id
- Loader names still resolve, and a genuine typo still exits 1

**Test command.** This touches `src/DataLoader.Core/**` and
`src/DataLoader.Host/**`, so per the repo's scoped-test policy it escalates to
solution-wide:

```bash
dotnet test DataLoaderPlatform.sln -c Release
```

## 9. Out of scope

- **Zoom / Teams.** `INotificationSender` makes it a later one-class addition.
- **Suppression windows and digests.** One email per invocation. Naturally
  rate-limited by the per-loader schedule (§2.4).
- **Silent-success detection** — AddProc timeouts (`AddProcStartTime IS NOT NULL
  AND AddProcEndTime IS NULL`), all-units-skipped runs, zero-record runs. The
  columns are present in `LoaderRun_Summary`, so this is a clean follow-up.
- **An off-box watchdog.** A watchdog on `ARMH-OPSDB01` dies with the box.
  Catching "the whole server is down" needs something elsewhere.
- **Reconciling the prod/repo `core` schema drift** (§2.2).
- **Fixing EnergyAspects' 6-of-7 failure rate** and the other standing failures
  this will surface.

## 10. Prerequisites outside the repo

| Item | Owner | Blocking? |
|---|---|---|
| ~~SMTP relay host and port~~ — **supplied 2026-10-24**: `armenergy-com.mail.protection.outlook.com:25`, anonymous + STARTTLS, verified by `EHLO` probe (§7.1) | — | Done |
| ~~`From` address~~ — **supplied**: `ARMHOPSDB01_SVC@armenergy.com` | — | Done |
| ~~Recipient list~~ — **supplied 2026-10-24**: `yaroslav.chekhuta@armenergy.com` only, while this is proven out | — | Done |
| Confirm **ARMH-OPSDB01 can egress on TCP 25** to that host, and is SPF-authorised | IT | **Yes** — probe ran from a workstation, not the server |
| Confirm the tenant does **not** have **"Reject Direct Send"** enabled | IT | **Yes** — it blocks this exact pattern |
| ~~`core.Param` row for `SmtpPassword`~~ — not needed; the endpoint takes no credential | — | N/A |
| `Watchdog.cmd` on the server, in the **production** idiom (change directory to the sibling `Loader\` folder and append stdout to `Log\Watchdog.log`) — the 23 `.cmd` files in the repo root are *dev* launchers that point at `bin/Release` and `pause`, and are **not** what production runs | IT | Yes, for the watchdog half |
| New hourly `\DLP\Watchdog` scheduled task | IT / change ticket | Yes, for the watchdog half |
| Deploy: `Publish-ToProd.ps1 -OverwriteSettings`, then the `Src` to `Loader` copy | IT | Yes |

The in-process notifier can be built, tested and merged before any of these
land; it stayed inert while `Enabled: false`.

**UPDATE 2026-10-27 — the SMTP prerequisites are satisfied and alerting now
ships ON.** The two IT questions above were answered by evidence rather than
confirmation: on 2026-10-26 a real alert (`FAILED: ICE on ARMH-OPSDB01
(1/651 units failed)`) was sent from the server and delivered, which proves
both port-25 egress and that "Reject Direct Send" is not blocking. So
`Platform:Notifications:Enabled` now ships `true`, and `NotificationSettings.Enabled`
defaults to `true` in code as well.

⚠ The **code** default is the load-bearing half. It governs the case where the
`Platform:Notifications` section is missing entirely — a fresh deployment, or
someone trimming config. Defaulting to `false` there would produce a platform
that looks healthy and silently tells nobody anything, which is precisely the
condition this component exists to end. Defaulting to `true` means an
unconfigured deployment instead fails `Validate()` and degrades to
`NullNotificationSender` with a loud log line: visibly broken rather than
invisibly silent. That degrade path (§Task 10) is what makes on-by-default safe
— a bad notification config still cannot stop the loaders running.

Still outstanding for the **watchdog** half only: `Watchdog.cmd` and the
`\DLP\Watchdog` task. The watchdog has no master on/off switch by design — the
scheduled task's existence is the switch.

**Production writes.** Deploying this is a write to `ARMH-OPSDB01`. Per the
standing rule, the exact commands are prepared and handed over, never run
without a specific request.

## 11. Build order

1. `Notifications/` component + `ErrorTextRedactor` + unit tests (no wiring)
2. `PlatformHost` call site + stale-row plumbing + tests
3. `Program.cs` restructure + bootstrap notifier + tests
4. `WatchdogEvaluator` + tests (pure, no DB)
5. Watchdog data access + `--watchdog` mode + argument-parsing tests
6. `appsettings.json` keys, `Watchdog.cmd` (dev form), README/RUNBOOK notes
7. Solution-wide test run; hand over the deploy commands
