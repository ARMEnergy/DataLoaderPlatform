# resume-key-and-status-matrix-check

Purpose: Catch high-impact logic bugs in idempotency and HTTP status classification.

## Trigger
Run for any change touching work-unit keys, date windows, or HTTP response handling, and
whenever a new loader is designed (APPLICATION_DESIGNER) or built (CODER), because the
correction-horizon default below decides its settings.

## Inputs
- WorkUnit key format logic.
- DaysBack/SettledAfterDays settings.
- Source reader status handling and retry/auth behavior.

## Default correction horizon: `DaysBack = 10`, `SettledAfterDays = 10`
This is the platform default for any loader whose vendor can correct (restate) already
published data. Apply it to every new loader, and to any existing loader whose horizon you
are changing, unless the user or the vendor's documented behaviour says otherwise.

- **Both are loader-level settings** with class defaults of 10 AND explicit `10` entries
  in `appsettings.json`, so a missing key cannot silently shrink the horizon. A test pins both.
- **Semantics:** read `today − DaysBack … yesterday`. Day D is HOT while
  `D > today − SettledAfterDays`. A hot key carries the run's date (`;run=yyyyMMdd`), so the
  day is re-read once per day and corrections are upserted. Older days use a stable settled
  key and are never re-read. With 10/10, every day in the window is re-read daily, and the
  day leaving the window gets one final settled read.
- **Per-dataset exceptions are FLOORS, never overrides:**
  `effective = max(setting, descriptor.MinDaysBack ?? 0)`, and likewise for
  `MinSettledAfterDays`. The operator setting can widen a horizon but never narrow one the
  vendor needs (e.g. GridStatusIO AESO 12/12, NUCORE SPIP 21).
- **Hourly-cadence datasets** read ONE short "recent" window per run for freshness, keyed on
  the run hour. They also get a separate **daily correction pass** of whole-day units over
  the same `DaysBack`/`SettledAfterDays` window, keyed exactly like daily units, so they are
  re-read once a day rather than once per run. A backfill (explicit run window) builds DAY
  units only. Never build one unit per hour across days: a 91-day GridStatusIO backfill
  did, made 2,184 requests per dataset, and exhausted the vendor key on 2026-11-19.
  Reference implementation: `GridStatusIOWorkUnitProvider.BuildHourly`.
- **Count requests against the vendor's limit before shipping.** Count units per run ×
  pages per unit × runs per day, plus any retries. Also check whether another process
  (a legacy loader during a shadow run) shares the key.
- **Corrections need an upsert.** The merge must UPDATE changed rows. A NULL-safe `EXCEPT`
  compare or a hash is fine, but insert-only "first write wins" defeats the re-read.
  `ReportDate`-style first-seen columns must stay out of the update list.
- **Deviate only with a reason written down** in the loader's design doc, for example:
  immutable published days (OilX `SettledAfterDays = 1`); window sizes frozen into existing
  settled keys (EIA); or a vendor that restates later than 10 days (widen it, and record the
  evidence).
- A settled day that reads zero rows from a dataset that always publishes should FAIL, not
  be recorded done forever (GridStatusIO `ExpectsRows`).

## Checks
0. The correction horizon follows the default above, or the design doc says why not.
1. Settled keys are stable; hot keys vary at intended cadence.
2. Legitimate empty responses are modeled as success where required.
3. Malformed request statuses throw.
4. Auth status handling is compatible with retry/auth handler design.
4a. A status that refuses the KEY rather than the request stops the whole run and is never
    retried on a seconds-scale backoff. Examples: an hourly 429, a quota 403, an ICE-style
    account-wide limit. The loader must not keep failing unit after unit and burning a
    shared key.
5. Window/helper behavior is consistent between provider and validator.

## Output template
- Key behavior: pass/fail
- Status matrix: pass/fail
- Risks if config tightens
- Required tests to pin behavior

## Escalation rules
Any ambiguity in vendor status semantics escalates to API_DOCUMENTATION_EXPERT.
