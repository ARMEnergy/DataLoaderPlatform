# MISO (Data Exchange, LGI API) loader — design

Input of record: `docs/apis/MISO.md` (live-verified 2026-11-24). This is the hand-off for
DATABASE_DEVELOPER and CODER. Reference implementation to mirror: **`src/DataLoader.PJM/`** and
**`sql/PJM/`** (same database `Misc`, same shape: day units, header key, TVP MERGE returning a
changed-row count). Where this document is silent, do what PJM does.

## 0. Locked decisions (user, 2026-11-24)

- Loader id **`MISO`**; database **`Misc`**; schema **`miso`** (new; 001 creates it). Integrated Security.
- Only GridStatusIO's `miso_zonal_load_hourly` has an exact API twin, so it keeps **the same table
  name and columns**. Load, fuel mix and load forecast exist in the API only as *different* series,
  so they get **their own honest table names** (never the GridStatusIO names).
- **LMP is deferred** (the key has no Pricing API subscription). Solar/wind forecasts have no API.
- API key: `ApiKey = "SEE_DB"` → `core.Param('MISO','ApiKey')`; env override
  `DATALOADER_Loaders__MISO__ApiKey`. Never logged, never in a URL, never in the repo.
- Correction horizon: the standing default **`DaysBack = 10` / `SettledAfterDays = 10`**, no exceptions.
- **Build-only**: not in `Platform:EnabledLoaders`, no watchdog entry, SQL not deployed.

## 1. Feeds → tables

| Feed id | Request (relative to `BaseUrl` = `https://apim.misoenergy.org/lgi/v1`) | Table | Members per hour |
|---|---|---|---|
| `zonal_load_hourly` | `real-time/{D}/demand/actual?geoResolution=localResourceZone` | `miso.miso_zonal_load_hourly` | 6 LRZ, pivoted wide |
| `load_hourly` | `real-time/{D}/demand/actual?geoResolution=region` | `miso.miso_load_hourly` | 3 regions, pivoted wide |
| `fuel_mix_hourly` | `real-time/{D}/generation/fuel-type` | `miso.miso_fuel_mix_hourly` | 3 regions, one row each |
| `load_forecast_mtlf` | `forecast/{D}/load?init={I}` | `miso.miso_load_forecast_mtlf` | 10 zones, one row each |

`{D}`, `{I}` are `yyyy-MM-dd`, invariant culture. Always send `pageNumber=1` explicitly and page
while `page.lastPage == false` (§4). Every request carries the explicit `geoResolution`/`init` shown
— never rely on a vendor default.

## 2. SQL contract (DATABASE_DEVELOPER) — exact

Files: `sql/MISO/001_CreateMisoSchema.sql`, `002_CreateMisoTvpTypes.sql`, `003_CreateMisoProcedures.sql`,
`999_DropMisoObjects.sql`. All start `USE Misc; GO` plus PJM's `DB_NAME() <> N'Misc'` →
`SET NOEXEC ON` guard. 001 creates the schema with `IF SCHEMA_ID(N'miso') IS NULL EXEC(N'CREATE SCHEMA miso AUTHORIZATION dbo');`.
Every object is `miso.`-qualified. Tables are `IF OBJECT_ID … IS NULL` guarded; types `IF TYPE_ID … IS NULL`;
procs `CREATE OR ALTER`. No `#temp` tables.

Common: all timestamps `DATETIME2(0) NOT NULL`; all measures `DECIMAL(18,4) NULL`; audit columns
(table only, never in the TVP) `CreatedAtUtc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()` and
`ModifiedAtUtc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()`. Clustered PK as listed. Column order
below is the **TVP order** (table = TVP columns, then the two audit columns).

### 2.1 `miso.miso_zonal_load_hourly` — PK (`interval_start_utc`)
`interval_start_local, interval_start_utc, interval_end_local, interval_end_utc, lrz1, lrz2_7, lrz3_5, lrz4, lrz6, lrz8_9_10, miso`
(same names and order as GridStatusIO's table, minus its `Id`/`RecordDate`/`API_Info_Log_Id`).
TVP `miso.MisoZonalLoadHourlyType` → proc `miso.usp_MergeMisoZonalLoadHourly`.

### 2.2 `miso.miso_load_hourly` — PK (`interval_start_utc`)
`interval_start_local, interval_start_utc, interval_end_local, interval_end_utc, north, central, south, miso`
TVP `miso.MisoLoadHourlyType` → `miso.usp_MergeMisoLoadHourly`.

### 2.3 `miso.miso_fuel_mix_hourly` — PK (`interval_start_utc`, `region`)
`interval_start_local, interval_start_utc, interval_end_local, interval_end_utc, region VARCHAR(20) NOT NULL, coal, gas, nuclear, water, wind, solar, other, storage, total_mw`
TVP `miso.MisoFuelMixHourlyType` → `miso.usp_MergeMisoFuelMixHourly`.

### 2.4 `miso.miso_load_forecast_mtlf` — PK (`init_date`, `interval_start_utc`, `local_resource_zone`)
`init_date DATE NOT NULL, interval_start_local, interval_start_utc, interval_end_local, interval_end_utc, region VARCHAR(20) NOT NULL, local_resource_zone VARCHAR(10) NOT NULL, load_forecast`
TVP `miso.MisoLoadForecastMtlfType` → `miso.usp_MergeMisoLoadForecastMtlf`.

### 2.5 Procs
Exactly PJM's pattern: THROW 50001 on a duplicate key in `@Records` (never de-dup); MERGE on the PK;
`WHEN MATCHED AND EXISTS (SELECT t.<non-key> EXCEPT SELECT s.<non-key>)` → update + `ModifiedAtUtc =
SYSUTCDATETIME()`; insert when not matched; no delete; return one row, one INT column
`RecordsProcessed` (= changed/inserted rows). The MTLF proc may scope the target to the batch's
`init_date` MIN..MAX (as PJM's forecast procs do).

`999` drops procs → types → tables, all `miso.`-qualified, then the schema only if empty. Nothing
outside `miso` may be touched (`Misc.dbo` holds PJM's live tables).

## 3. Time

`start`/`end` are fixed **EST (UTC−5, no DST)** wall-clock. Parse with
`DateTime.ParseExact(…, "yyyy-MM-dd'T'HH:mm:ss", Invariant)`; `*_local = value`; `*_utc = value + 5h`.
Never use a `TimeZoneInfo` (Eastern *prevailing* would be wrong 8 months a year). "Today" for the
loader is `todayEst = DateOnly.FromDateTime(UtcNow − 5h)`.

## 4. Reader rules (one `MisoDayReader` shared by all feeds, per-feed mappers)

For each unit, page 1..N until `lastPage`. Then validate, and **fail the unit** (never silently
drop) when any of these is false:
1. Σ rows read == `page.totalElements` of page 1; `page.pageNumber` echoes the request; every later
   page reports the same `totalElements` and `totalPages` as page 1 (a moving total fails the unit).
2. Every row: `timeInterval.resolution == "hourly"`, `end − start == 1h`, `start.Date == D` (echo check).
3. MTLF: every row's `init == I` (echo check — the vendor silently defaults `init`).
4. The member (`localResourceZone`/`region`) is in the feed's known set, matched case-insensitively
   (actual load sends `Central`, fuel sends `CENTRAL`): LRZ `{LRZ1, LRZ2_7, LRZ3_5, LRZ4, LRZ6, LRZ8_9_10}`;
   regions `{NORTH, CENTRAL, SOUTH}`; MTLF zones `{Z1..Z10}`. Unknown member → fail.
5. Fuel: every key in `fuelTypes` is in `{coal, gas, nuclear, water, wind, solar, other, storage}`
   (case-insensitive). Unknown key → fail. A missing known key → NULL.
6. No duplicate `(interval, member)`.
7. **Complete**: exactly 24 distinct intervals (00:00..23:00) and every interval has every member
   of its set. A 200 that is partial fails the unit.

Pivot (zonal, load): one row per interval; `miso` = **decimal** sum of the members (decimal, not
double — 68,923.0 must not become 68,923.000000001); NULL if any member is NULL.
Fuel: region stored UPPER. MTLF: zone stored in its canonical spelling (`Z1`..`Z10`), region UPPER,
and the region must be the zone's own or the unit fails. The mapping is the one **observed in every
row of both live MTLF captures** (2026-11-01, 2026-11-18): NORTH = Z1, Z3; CENTRAL = Z2, Z4–Z7;
SOUTH = Z8–Z10 (`MisoFeeds.MtlfZoneRegion`). ⚠ `docs/apis/MISO.md` §5.3 says "NORTH (Z1,Z2) | CENTRAL
(Z3..Z7)"; the vendor's own data contradicts that for Z2 and Z3, and the data wins.

## 5. Status matrix

Implement exactly `docs/apis/MISO.md` §4, classifying 400 by `reason` prefix (ordinal, ignore case):

| Outcome | Rows | Unit result | Settles? |
|---|---|---|---|
| 200 complete | n | success | per zone (§6) |
| 400 not-yet (`data not available yet`, `Init date must be before today`) | 0 | success | **never** (key forced hot) |
| 400 `beyond the allowed lookback period`, age of `D` ≥ `MaxLookbackDays` − 30 | 0 | success + warning | per zone |
| 400 `beyond the allowed lookback period`, younger `D` | — | **fail** (MISO shrank its lookback or mis-answered; must not settle an empty key) | — |
| 404 `Empty data returned for date` on page 1, age ≤ `ExpectRowsWithinDays` (3) | 0 | success | never |
| 404 on page 1 older than that, or on page > 1 | — | **fail** | — |
| other 400 | — | **fail**, not retried | — |
| 401 / 403 | — | **stop the run** (run guard, as PJM's `PjmRunGuard`), not retried; the guard cancels the run token, so no later unit is begun (no `core.LoadLog` row per remaining unit), later feeds are not started, and the run ends with one aggregate failure | — |
| 408 / 429 / 500 / 502 / 503 / 504 / `HttpRequestException` / per-attempt timeout | — | retry (Polly, honour `Retry-After`), then fail | — |

Error text that reaches a log or `core.LoadLog` is the `reason` string only (bounded, ≤ 300 chars);
never headers.

## 6. Work units and resume keys

`Start = max(StartDate, todayEst − MaxLookbackDays)`; `End = todayEst − 1`. Also always include the
last `DaysBack` days (age 1..DaysBack) even if `StartDate` is later.

- **Day feeds** (zonal, load, fuel): one unit per market date `D ∈ [Start, End]`. Age = `todayEst − D`.
  Settled when `age > SettledAfterDays` → key `MISO|{feed}|{D}`; else hot → key
  `MISO|{feed}|{D}|run={todayEst}` (re-read once per EST day).
- **MTLF**: one unit per `(I, D)`, `I ∈ [Start, End]`, `D = I+1 … I+MtlfHorizonDays (7)`. Age from `I`.
  Settled key `MISO|load_forecast_mtlf|init={I}|{D}`; hot appends `|run={todayEst}`.
- A not-yet or young-empty outcome must not leave a settled key behind: the provider decides the
  zone, and those outcomes only occur for age ≤ 3 < SettledAfterDays, so they are always hot. Keep a
  test asserting that invariant.
- Order units newest-first so a partial run lands the freshest data first.
- ⚠ **Every run enumerates the whole range**, about 11k units with the shipped settings
  (3 × 1,095 day units + 7 × 1,095 MTLF units), and the pipeline does one `core.LoadLog` lookup
  per unit (`usp_BeginLoadLog`) even when it then skips a settled unit. After the backfill has
  completed, raise `StartDate` (for example to about 30 days back) to cut that to roughly 300
  units per run (3 × 30 + 7 × 30). The last `DaysBack` days are always enumerated, whatever `StartDate` says.

Budget: backfill ≈ 3 × 1,095 + 7 × 1,095 ≈ 11k requests, which at 90/min is **~2 hours** (the
vendor quota, not latency, is the bound); steady state ≈ 3 × 10 + 7 × 10
= 100 requests/run.

## 7. HTTP

`HttpClient` via factory with `.RemoveAllLoggers()`; key added as `Ocp-Apim-Subscription-Key` by the
client (not a default header visible to logs). `client.Timeout = InfiniteTimeSpan`; Polly
`retry.WrapAsync(perAttemptTimeout(HttpTimeoutSeconds))`. One shared rate limiter
(`RequestsPerMinute`, default **90**, under the vendor quota of 100 per 60 s per key; mirror `src/DataLoader.PJM/Throttling.cs`) inner of the retry.
Test-enforced coupling: `(RetryCount+1) × HttpTimeoutSeconds + total backoff < WorkUnitTimeoutSeconds`.

## 8. Settings (`Loaders:MISO`, `MisoSettings : LoaderSettingsBase`)

| Key | Default |
|---|---|
| `ConnectionString` | `Server=ARMH-OPSDB01;Database=Misc;Integrated Security=SSPI;TrustServerCertificate=True;` |
| `BaseUrl` | `https://apim.misoenergy.org/lgi/v1` |
| `ApiKey` | `SEE_DB` |
| `HttpTimeoutSeconds` | 60 |
| `EnabledFeeds` | all four, in §1 order |
| `StartDate` | `2023-11-20` |
| `MaxLookbackDays` | 1095 (vendor allowed ~1,100 on 2026-11-24) |
| `DaysBack` / `SettledAfterDays` | 10 / 10 |
| `ExpectRowsWithinDays` | 3 |
| `MtlfHorizonDays` | 7 (vendor max; validate 1..7) |
| `RequestsPerMinute` | 90 (vendor quota 100 / 60 s per key; keep below it) |
| `MaxConcurrentWorkUnits` / `RetryCount` / `RetryDelayMs` / `WorkUnitTimeoutSeconds` | 4 / 3 / 2000 / 600 |

Validate at startup (fail fast): URL absolute https, `MtlfHorizonDays` 1..7, positive ints,
`SettledAfterDays ≥ ExpectRowsWithinDays`, known feed ids.

## 9. Wiring

`src/DataLoader.MISO/` + `tests/DataLoader.MISO.Tests/` added to the solution and the Host exactly as
PJM is; `appsettings.json` gains the `Loaders:MISO` block (NOT added to `EnabledLoaders`); a dev
launcher `MISO.cmd` like `PJM.cmd`.

## 10. Schedule (for when it is enabled)

Actual load publishes 02:00 EST, fuel type and MTLF 07:00 EST the next day → one daily run at
**08:15 Central** (`\DLP\MISO`). Watchdog `MaxGapHours: 31` once enabled.

## 11. Deploy hand-over (production writes — a person runs them)

1. `sql/MISO/001` → `002` → `003` against `ARMH-OPSDB01`/`Misc`.
2. `INSERT core.Param (LoaderName, ParamName, Value) VALUES ('MISO', 'ApiKey', N'<key>')` in `DataLoaderPlatform`.
3. Release with `"MISO"` in `EnabledLoaders` + the scheduled task.

## 12. Open items

- LMP (`miso_lmp_day_ahead_hourly`): needs a Pricing API subscription; then `aggregated-pnode` +
  `day-ahead/{D}/lmp-expost` filtered to GridStatusIO's 424 locations.
- Whether MISO restates actual load/fuel after publication is unmeasured; `ModifiedAtUtc >
  CreatedAtUtc` will answer it.
- The lookback is rolling; `MaxLookbackDays` keeps a 5-day margin under the observed ~1,100.
