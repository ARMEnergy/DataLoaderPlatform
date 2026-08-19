# CWG (Commodity Weather Group) loader — API field reference

Weather forecasts, observations, degree-days, and renewable (wind/solar) generation
from Commodity Weather Group. This is the **mandatory full-field-set gate**: every
column of all **18 endpoints** is enumerated below with its exact source name, ordered
position, recommended SQL Server type, nullability/sentinel, and date format. Nothing
downstream (model → sink → TVP → table → merge proc) may ship a partial column set.

All facts below were verified against **live `200 text/csv` responses** captured
2026-08-11 and the saved raw sample CSVs (the raw files are ground truth for column
counts, hour labels, matrix widths, and sentinels — not the prose recon).

> **Three corrections to the earlier recon prose, confirmed from the raw files
> (documented values below are the raw-verified ones):**
> 1. **WindForecastSubRegion** blocks carry **15** forecast-date columns, not 16
>    (verified on ERCOT and CAISO data + header rows).
> 2. **DailyNormal** has **366** data rows (one per calendar day *including* `02-29`),
>    not 365.
> 3. **WindTotalCapacityPct** and **WindTotalCapacityMW** each contain **three stacked
>    blocks** (Current / Yesterday's Forecast / Change from Yesterday), not a single
>    region table. **WindTotalCapacityClimatology** contains a **single** block.

## API

- **Base URL:** `https://api.commoditywx.com/v1/<filename>`
- **Auth:** API key as a query-string parameter `?apikey=<key>` on every request.
  No header / Basic / cookie is required. Key is stored in config as
  `Loaders:CWG:ApiKey` (env `DATALOADER_Loaders__CWG__ApiKey`), defaulting to the
  `SEE_DB` sentinel per the platform secret convention. **Never hard-code the key or
  place it in this file.**
- **Responses:** always CSV (`text/csv`). No pagination; each URL is one whole file.
- **HTTP semantics:** `200` = file exists for that date/region; **`404` = not produced**
  for that date/region (holiday gaps, region not modeled that day, or the "observation
  = yesterday" file requested for today). The loader must treat 404 as
  "not available for this combination" → **skip, not hard-fail**.
- **History:** dated files are archived **6+ years back** (probed to 2020). Scattered
  per-day 404s (holidays/gaps) exist but there is no hard horizon cutoff. Files with a
  date token are immutable snapshots once published; the parameter-less "single latest"
  files (`city_gasday_fcst.csv`, `daily_normals.csv`, `Gen_hrly_solar.csv`,
  `Gen_hrly_5day.csv`, and the `Station` reference files) are **overwritten in place**.
- **Rate limits:** none observed in response headers; pace conservatively.

## Date tokens (apply everywhere the filename templates reference them)

- `{date}` = `YYYYMMDD` (e.g. `20260811`).
- `{datemmddyyyy}` = `MMDDYYYY` (e.g. `08112026`).
- Which calendar date a file represents is stated per endpoint (e.g. CityObservation =
  run-date − 1). In-file date formats vary widely by endpoint (`M/D/YY`,
  `YYYY-MM-DD`, `M/D/YYYY`, `MM/DD/YYYY`, `MM-DD`, `YYYY-MM-DD HH:MM:SS`) — each is
  called out in the relevant section. **Do not assume a single date format.**

## Parse-shape legend

| Shape | Description | Endpoints |
|-------|-------------|-----------|
| **A** | Simple tabular, one row = one record | CityForecast, CityGasForecast, CityObservation, Station, NationalDegreeDays, Regions5DegreeDays, Regions9DegreeDays, ISODegreeDays |
| **B** | Wide-by-region tabular → **unpivot** to long | DailyNormal, SolarHourly, WindHourly |
| **C** | Pivoted hour×forecast-day matrix w/ title/date leading rows → **unpivot** | SolarForecast, SolarForecastChange, WindForecast |
| **D** | Stacked sub-region matrix blocks → **unpivot per block** | WindForecastSubRegion |
| **E** | Region-row summary with title/header rows (multi-block) | WindTotalCapacityPct, WindTotalCapacityMW, WindTotalCapacityClimatology |

## Recommended DECIMAL sizing (READ THIS — flagged for DATABASE_DEVELOPER)

Pick precision by **measure family**. `DECIMAL(9,4)` has only 5 integer digits (max
`99999.9999`) and **WILL OVERFLOW** on MW capacity/generation totals — e.g. the
`Total All (MW)` row reaches **163,999** and the `1-5 Day Avg (MW)` column reaches
**59,459**. Sub-region and hourly MW values reach tens of thousands. Use:

| Measure family | Where | Recommended type | Rationale |
|----------------|-------|------------------|-----------|
| **MW generation / capacity** | SolarForecast, SolarForecastChange (signed), WindForecast, WindForecastSubRegion, SolarHourly, WindHourly, DailyNormal, WindTotalCapacity `Total Capacity (MW)` + MW-avg columns | **`DECIMAL(12,4)`** (or `DECIMAL(12,2)`) | totals to ~164k today, headroom for growth; signed for changes. **NOT `DECIMAL(9,4)`.** |
| **Percent of capacity** | WindTotalCapacityPct / Climatology avg columns (signed in Change block) | **`DECIMAL(6,2)`** | strip trailing `%`; can be >100 or negative |
| **Weighted degree-days (decimal)** | NationalDegreeDays, Regions5DegreeDays, Regions9DegreeDays, ISODegreeDays — every NG/POP/ELEC HDD/CDD family column (incl. `30Y_`/`10Y_`/`LAST_Y_` variants) | **`DECIMAL(9,4)`** | values < ~100 (max observed ~35), 4 dp |
| **Region weights (fraction)** | Regions5DegreeDays / Regions9DegreeDays `GAS_WEIGHT`, `ELCT_WEIGHT`, `POP_WEIGHT` | **`DECIMAL(9,4)`** | 0..1 fraction, 4 dp; constant per region within a file. `DECIMAL(7,4)` would also suffice — DATABASE_DEVELOPER's call |
| **Temperature (°F)** | CityForecast Fcst/Norm Mn/Mx/Avg; CityObservation Min/Max | **`DECIMAL(5,1)`** | Fcst Avg has `.5`, Norm has `.1`; signed (below-zero possible) |
| **City HDD/CDD (integer)** | CityForecast HDD/CDD; CityObservation HDD/CDD | **`SMALLINT`** | whole numbers, 0–~50/day |
| **Lat / Lon** | Station | **`DECIMAL(9,6)`** | standard geo precision |

---

## 1. CityForecast → table `CityForecast`

- **Filename:** `city15dfcst_{region}_{date}_{units}.csv`
  (e.g. `city15dfcst_northamerica_20260811_F.csv`)
- **`{region}` × `{units}` loading rule (UPDATED):** **`northamerica` → `F`**,
  **`europe` → `C`**, **`asia` → not loaded** (dropped from scope). Each geography is
  fetched in exactly one unit; the work unit is one (region, units) pair. CWG produces
  both `F` and `C` variants for every geography, but only these two combinations are
  ingested. *(Supersedes the earlier "`F` only, all three geographies" scope.)*
- **`{units}`** ∈ `F` (Fahrenheit), `C` (Celsius) — determined by region per the rule
  above. **Verified from live `200` files:** the `_F` and `_C` files share an identical
  header and column layout (see field table); values differ by unit **and** by the
  `Norm Mn`/`Norm Max` precision (the `_C` file carries up to 5 decimal places — see the
  ⚠ note under the field table).
- **New `Units` discriminator column:** a **`Units` `VARCHAR(1)`** (`'F'` for northamerica,
  `'C'` for europe) has been added to `arm.CityForecast` so every row is self-describing
  about the unit its temperatures are in. It is a **non-key attribute** — `region` already
  uniquely keys the row and implies the unit (see the natural-key note below).
- **`{date}`** = `YYYYMMDD` = **production date** (today is valid — no 404 for today).
- **Represents:** a production date's rolling **15-day** city forecast; ~6,346 rows =
  station × 15 forecast days per NA file.
- **Shape A** (simple tabular). One row per (Production Date, forecast Date, Station).

Ordered columns (header row 1 exactly as shown):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `Production Date` | `DATE` | No | format **`M/D/YY`** (2-digit year, e.g. `8/11/26` → 2026); the run/production date |
| 2 | `Date` | `DATE` | No | format **`M/D/YY`**; the forecast valid day |
| 3 | `Station` | `VARCHAR(8)` | No | ICAO-style station id (e.g. `KABR`); FK to `Station.identifier` |
| 4 | `Fcst Mn` | `DECIMAL(8,5)` | No | forecast min temp — **°F in the northamerica `_F` file, °C in the europe `_C` file**. Integer-valued in both units. Header is literally `Fcst Mn` |
| 5 | `Fcst Mx` | `DECIMAL(8,5)` | No | forecast max temp (°F NA / °C europe). Integer-valued in both. Header is literally `Fcst Mx` |
| 6 | `Fcst Avg` | `DECIMAL(8,5)` | No | forecast avg temp (°F NA / °C europe); 1 dp (`.5` values, e.g. `72.5` in `_F`, `11.5` in `_C`) |
| 7 | `Norm Mn` | `DECIMAL(8,5)` ⚠ | No | normal min temp (°F NA / °C europe). **Precision is unit-dependent:** the `_F` file rounds to **1 dp** (`57.9`), but the `_C` file carries **up to 5 decimal places** (e.g. `7.74478`, `8.99571`, `20.3699`). `DECIMAL(5,1)` would silently round the Celsius values — see the ⚠ note below the table. Header is literally `Norm Mn` |
| 8 | `Norm Max` | `DECIMAL(8,5)` ⚠ | No | normal max temp (°F NA / °C europe); same unit-dependent precision as `Norm Mn` — 1 dp in `_F`, up to 5 dp in `_C` (e.g. `15.4283`, `33.0891`). **Header quirk: `Norm Max` (spelled out), asymmetric with `Norm Mn`** |
| 9 | `HDD` | `SMALLINT` | No | heating degree-days (integer) |
| 10 | `CDD` | `SMALLINT` | No | cooling degree-days (integer) |

> **⚠ `Norm Mn` / `Norm Max` precision — RESOLVED (DATABASE_DEVELOPER).** These two
> columns are the **only** structural surprise between the `_F` and `_C` files. In the
> northamerica `_F` file the normals are rounded to **1 dp** (`57.9`, `83.8`), but the
> europe `_C` file delivers them at **full float precision — up to 5 decimal places**
> (`7.74478`, `15.4283`, `20.3699`, `33.0891`). Because both feed **one** `arm.CityForecast`
> table, the column must hold F values (integer part up to ~3 digits, e.g. `97.1`, and
> negative winter normals) **and** the 5-dp Celsius values. Recommended
> **`DECIMAL(8,5)`** (`999.99999`; covers ~110.0 °F and −40, preserves the 5 dp).
> **Resolved:** full precision is preserved — DATABASE_DEVELOPER set **all five**
> temperature columns (`Fcst Mn/Mx/Avg`, `Norm Mn`, `Norm Max`) to `DECIMAL(8,5)`
> uniformly in **both** `arm.CityForecast` and `arm.CityForecastTvp`, and the loader does
> **not** round. This supersedes the `DECIMAL(5,1)` "Temperature (°F)" entry in the
> "Recommended DECIMAL sizing" section above for the CityForecast temperatures.
>
> **No parse-guard change is required:** column count (10), column order, header text
> (incl. the `Norm Max` / `Fcst Mn/Mx` quirks), the `M/D/YY` date format, 4-char ICAO
> station ids (`VARCHAR(8)`), and whole-integer HDD/CDD (`SMALLINT`) are all **identical**
> between `_F` and `_C`. No blank/`NULL` cells were observed. Verified against
> `tests/DataLoader.CWG.Tests/Samples/city15dfcst_northamerica_20260811_F.csv` (`_F`) and
> `tests/DataLoader.CWG.Tests/Samples/city15dfcst_europe_20260811_C.csv` (`_C`).

Natural key: `region + Station + ProductionDate + Date` (unchanged — `Units` is **not** a
key part). `Units` (`VARCHAR(1)`, `'F'`/`'C'`) is a self-describing attribute: `region`
already uniquely keys the row and implies the unit (NA→`F`, europe→`C`), so adding `Units`
to the key would be redundant. The merge proc's PK / `MERGE … ON` / `PARTITION BY` are
therefore unchanged; only the TVP and INSERT/UPDATE column lists gained `Units`.

---

## 2. CityGasForecast → table `CityGasForecast`

- **Filename:** `city_gasday_fcst.csv` — **no parameters** (single latest file,
  overwritten in place; ~273 KB).
- **Represents:** the latest **gas-day-aligned** city forecast. The production date is
  carried **in-row** in `Production Date`, not in the filename → use the in-file
  `Production Date` as the run marker.
- **Shape A.** Header and column set are **identical** to CityForecast (same 10 columns,
  same types, same `M/D/YY` date format, same `Norm Max` quirk) — the only difference is
  gas-day alignment and no filename params.

Ordered columns: **identical to CityForecast §1** (`Production Date`, `Date`, `Station`,
`Fcst Mn`, `Fcst Mx`, `Fcst Avg`, `Norm Mn`, `Norm Max`, `HDD`, `CDD`) — see that table
for types/nullability.

Natural key: `Station + ProductionDate + Date`.

---

## 3. CityObservation → table `CityObservation`

- **Filename:** `{region}_observations_final_{date}.csv`
  (e.g. `northamerica_observations_final_20260810.csv`)
- **`{region}`** ∈ `northamerica`, `asia`, `europe` (all three in scope).
- **`{date}`** = `YYYYMMDD` = **observation day = run-date − 1 (yesterday)**. Requesting
  today's date **404s** (final obs not yet produced).
- **Represents:** finalized observed temperatures/degree-days for one day; ~424 rows,
  one per station.
- **Shape A.**

Ordered columns (header row 1):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `date` | `DATE` | No | format **`YYYY-MM-DD`** (e.g. `2026-08-10`); the observation day |
| 2 | `station` | `VARCHAR(8)` | No | station id; FK to `Station.identifier` |
| 3 | `MinTemp` | `DECIMAL(5,1)` | No | observed min temp °F (integer-valued in samples) |
| 4 | `MaxTemp` | `DECIMAL(5,1)` | No | observed max temp °F |
| 5 | `HDD` | `SMALLINT` | No | heating degree-days (integer) |
| 6 | `CDD` | `SMALLINT` | No | cooling degree-days (integer) |

Natural key: `region + station + date`.

---

## 4. DailyNormal → table `DailyNormal`

- **Filename:** `daily_normals.csv` — **no parameters** (single static file).
- **Represents:** climatological **normal generation (MW)** per ISO/region for each
  calendar day of the year — year-agnostic. **366 rows** (one per calendar day
  **including `02-29`**), keyed by `MM-DD`.
- **Shape B** (wide-by-region → unpivot). 12 region value columns.

Wide source columns (header row 1):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `DATE` | `CHAR(5)` | No | **`MM-DD`** month-day key (no year), e.g. `01-01`, `02-29`, `12-31`. Keep as `CHAR(5)` (climatology; do not force a year) |
| 2 | `CAISO` | `DECIMAL(12,4)` | No | normal MW (integer-valued) |
| 3 | `SPP` | `DECIMAL(12,4)` | No | normal MW |
| 4 | `ERCOT` | `DECIMAL(12,4)` | No | normal MW |
| 5 | `MISO` | `DECIMAL(12,4)` | No | normal MW |
| 6 | `PJM` | `DECIMAL(12,4)` | No | normal MW |
| 7 | `NEPOOL` | `DECIMAL(12,4)` | No | normal MW |
| 8 | `NYISO` | `DECIMAL(12,4)` | No | normal MW |
| 9 | `BPA` | `DECIMAL(12,4)` | No | normal MW |
| 10 | `IESO` | `DECIMAL(12,4)` | No | normal MW |
| 11 | `AESO` | `DECIMAL(12,4)` | No | normal MW |
| 12 | `NW` | `DECIMAL(12,4)` | No | normal MW |
| 13 | `SW` | `DECIMAL(12,4)` | No | normal MW |

**Region set (12, in file order):** `CAISO, SPP, ERCOT, MISO, PJM, NEPOOL, NYISO, BPA,
IESO, AESO, NW, SW`. (Same 12 as WindHourly §11; **differs** from SolarHourly §7.)

**Recommended normalized (unpivoted) row the loader should emit:**
`(MonthDay CHAR(5), Region VARCHAR(10), NormalMw DECIMAL(12,4))` — one row per
region column. Natural key `MonthDay + Region`.

---

## 5. SolarForecast → table `SolarForecast`

- **Filename:** `{region}solar_{datemmddyyyy}.csv` (e.g. `ERCOTsolar_08112026.csv`)
- **`{region}`** (UPPERCASE, **10**): `ERCOT, CAISO, MISO, PJM, SPP, NEPOOL, IESO,
  AESO, NW, SW`.
- **`{datemmddyyyy}`** = `MMDDYYYY` = **production / init date** (today valid).
- **Represents:** forecast solar generation MW, by clock-hour × forecast-day, for one
  init date.
- **Shape C** (pivoted matrix). File layout:
  - **Row 1:** a **leading blank/spacer row** of empty fields (`,,,,,…`) — skip.
  - **Row 2:** `Date (EST),<16 forecast dates>` — the header. Dates in **`M/D/YYYY`**
    (non-padded, e.g. `8/10/2026`). **16 forecast-date columns**, spanning
    **(init_date − 1) … (init_date + 14)** (first column is the day *before* the file
    date; verified: file `08112026` → first column `8/10/2026`, last `8/25/2026`).
  - **Rows 3–26:** 24 hourly rows, one MW value per forecast-day column.
  - **Row 27:** trailing empty line.
- **Hour-row labels (24, exact, in order):** `12:00 AM, 1:00 AM, 2:00 AM, 3:00 AM,
  4:00 AM, 5:00 AM, 6:00 AM, 7:00 AM, 8:00 AM, 9:00 AM, 10:00 AM, 11:00 AM, 12:00 PM,
  1:00 PM, 2:00 PM, 3:00 PM, 4:00 PM, 5:00 PM, 6:00 PM, 7:00 PM, 8:00 PM, 9:00 PM,
  10:00 PM, 11:00 PM` → derive hour-of-day 0…23 (these are **hour-beginning EST**
  clock labels).

**Recommended normalized (unpivoted) row the loader should emit:**

| Column | SQL type | Null? | Notes |
|--------|----------|-------|-------|
| `Region` | `VARCHAR(10)` | No | UPPERCASE region from filename |
| `InitDate` | `DATE` | No | from filename `MMDDYYYY` (production/init date) |
| `ForecastDate` | `DATE` | No | from the `Date (EST)` header cell (`M/D/YYYY`) |
| `HourLabel` | `VARCHAR(8)` | No | e.g. `12:00 AM` (store literal) |
| `HourOfDay` | `TINYINT` | No | derived 0–23 |
| `ValueMw` | `DECIMAL(12,4)` | No | forecast solar MW (integer-valued; ≥ 0) |

Natural key: `Region + InitDate + ForecastDate + HourOfDay`.

---

## 6. SolarForecastChange → table `SolarForecastChange`

- **Filename:** `{region}solarchanges_{datemmddyyyy}.csv`
  (e.g. `ERCOTsolarchanges_08112026.csv`)
- **`{region}`** — same **10** regions as SolarForecast §5.
- **`{datemmddyyyy}`** = `MMDDYYYY` = production/init date.
- **Represents:** run-over-run **change** (this-run minus prior-run) in forecast solar
  MW; values are **signed** (negative allowed).
- **Shape C**, with these differences from §5:
  - **No leading blank row** — file starts directly at the header.
  - **Row 1 (header):** `Date (EST),<14 forecast dates>,` — **14 forecast-date columns
    plus a trailing empty column** (trailing comma). Dates in **`MM/DD/YYYY`**
    (zero-padded, e.g. `08/11/2026`) — **note the format differs from SolarForecast's
    non-padded `M/D/YYYY`**. First column is the **init date itself** (`08/11/2026`),
    last is `08/24/2026`.
  - **Rows 2–25:** 24 hourly rows (same 24 labels as §5), each with a trailing comma.
  - **Row 26:** `sum change for the day,<14 values>,` — column totals footer.
  - **Row 27:** `average change for the day,<14 values>,` — column averages footer.

**Recommended normalized (unpivoted) row the loader should emit** (skip the two footer
rows, or capture them separately with a `RowKind` discriminator if downstream wants the
per-day sum/avg):

| Column | SQL type | Null? | Notes |
|--------|----------|-------|-------|
| `Region` | `VARCHAR(10)` | No | from filename |
| `InitDate` | `DATE` | No | from filename `MMDDYYYY` |
| `ForecastDate` | `DATE` | No | from header (`MM/DD/YYYY`) |
| `HourLabel` | `VARCHAR(8)` | No | e.g. `7:00 AM` |
| `HourOfDay` | `TINYINT` | No | derived 0–23 |
| `ChangeMw` | `DECIMAL(12,4)` | No | **signed** run-over-run change in MW |

Natural key: `Region + InitDate + ForecastDate + HourOfDay`. The trailing empty column
(from the trailing comma) is **not a data field** — drop it.

---

## 7. SolarHourly → table `SolarHourly`

- **Filename:** `Gen_hrly_solar.csv` — **no parameters** (single rolling file, ~4 MB,
  actuals back to **2020-07-26**; overwritten in place).
- **Represents:** **actual** hourly solar generation MW by region, long history.
- **Shape B** (wide-by-region → unpivot). **14 region value columns.**

Wide source columns (header row 1):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `UTC_HOUR_ENDING` | `DATETIME2(0)` | No | **`YYYY-MM-DD HH:MM:SS`** UTC, hour-ending (e.g. `2020-07-26 15:00:00`) |
| 2 | `ERCOT` | `DECIMAL(12,4)` | Yes* | actual MW |
| 3 | `CAISO` | `DECIMAL(12,4)` | Yes* | actual MW |
| 4 | `PJM` | `DECIMAL(12,4)` | Yes* | actual MW |
| 5 | `AESO` | `DECIMAL(12,4)` | Yes* | actual MW |
| 6 | `MISO` | `DECIMAL(12,4)` | Yes* | actual MW |
| 7 | `IESO` | `DECIMAL(12,4)` | Yes* | actual MW |
| 8 | `NEPOOL` | `DECIMAL(12,4)` | Yes* | actual MW |
| 9 | `NW` | `DECIMAL(12,4)` | Yes* | actual MW |
| 10 | `SW` | `DECIMAL(12,4)` | Yes* | actual MW |
| 11 | `SPP` | `DECIMAL(12,4)` | Yes* | actual MW |
| 12 | `FRCC` | `DECIMAL(12,4)` | Yes* | actual MW |
| 13 | `CAR` | `DECIMAL(12,4)` | Yes* | actual MW |
| 14 | `SE` | `DECIMAL(12,4)` | Yes* | actual MW |
| 15 | `TVA` | `DECIMAL(12,4)` | Yes* | actual MW |

**\* Sentinel:** missing values are the **literal string `NULL`** (uppercase) — the
loader must map `"NULL"` → SQL `NULL`. Early history has only `ERCOT` populated; all
other regions are `NULL` until they enter coverage.

**Region set (14, in file order):** `ERCOT, CAISO, PJM, AESO, MISO, IESO, NEPOOL, NW,
SW, SPP, FRCC, CAR, SE, TVA`. **Note this set differs from WindHourly/DailyNormal** —
it adds `FRCC, CAR, SE, TVA` and omits `NYISO, BPA`.

**Recommended normalized (unpivoted) row the loader should emit:**
`(HourEndingUtc DATETIME2(0), Region VARCHAR(10), ActualMw DECIMAL(12,4) NULL)` — one
row per non-blank region column; emit `NULL` for the `"NULL"` sentinel (or skip — a
design decision for DATABASE_DEVELOPER/APPLICATION_DESIGNER). Natural key
`HourEndingUtc + Region`.

---

## 8. NationalDegreeDays → table `NationalDegreeDays`

- **Filename:** `northamerica_{subregion}_wdd_{date}.csv`
  (e.g. `northamerica_national_wdd_20260811.csv`)
- **`{subregion}`** — the `wdd` file family has **four confirmed tokens**, all returning
  `200`: **`national`** (this section — the single-region aggregate; **no `REGION_NAME`
  column and no weight columns**), plus **`5region`**, **`9region`**, and **`iso`** — the
  per-region siblings documented in **§16, §17, §18**. *(Correction: an earlier probe
  reported "`national` only / all other sub-region guesses 404"; that probe used the
  wrong tokens. `5region`/`9region`/`iso` are valid and now confirmed from live `200`
  files.)*
- **`{date}`** = `YYYYMMDD` = **production / RUN date** taken from the **filename** (today
  valid; there is no RunDate column in the file).
- **Represents:** a window of **observed + forecast** national weighted degree-days
  around the run date; each row is one calendar day, split by the `IS_FORECAST` flag
  (past = `False`, future = `True`).
- **Shape A.** **14 columns** (the siblings add `REGION_NAME` + weights → 18, or the
  `iso` variant → 11; see §16–18).
- **Footer:** consistent with the rest of the `wdd` family, the file terminates with a
  literal **`END.` sentinel row** (`DATES=END.`, remaining fields empty) that consumers
  must skip. *(How the national parser currently handles this row is a separate concern;
  the row's existence is documented here for completeness.)*

Ordered columns (header row 1):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `DATES` | `DATE` | No | **`YYYY-MM-DD`** valid day |
| 2 | `NG_HDD` | `DECIMAL(9,4)` | No | gas-weighted HDD (this year), 4 dp |
| 3 | `30Y_NG_HDD` | `DECIMAL(9,4)` | No | 30-year-normal gas-weighted HDD. **Header starts with a digit** |
| 4 | `10Y_NG_HDD` | `DECIMAL(9,4)` | No | 10-year-normal gas-weighted HDD (`0.0000` is a valid value, not null) |
| 5 | `LAST_Y_NG_HDD` | `DECIMAL(9,4)` | No | last-year gas-weighted HDD |
| 6 | `POP_CDD` | `DECIMAL(9,4)` | No | population-weighted CDD (this year) |
| 7 | `30Y_POP_CDD` | `DECIMAL(9,4)` | No | 30-year-normal pop-weighted CDD |
| 8 | `10Y_POP_CDD` | `DECIMAL(9,4)` | No | 10-year-normal pop-weighted CDD |
| 9 | `LAST_Y_POP_CDD` | `DECIMAL(9,4)` | No | last-year pop-weighted CDD |
| 10 | `ELEC_CDD` | `DECIMAL(9,4)` | No | electricity-weighted CDD (this year) |
| 11 | `30Y_ELEC_CDD` | `DECIMAL(9,4)` | No | 30-year-normal elec-weighted CDD |
| 12 | `10Y_ELEC_CDD` | `DECIMAL(9,4)` | No | 10-year-normal elec-weighted CDD |
| 13 | `LAST_Y_ELEC_CDD` | `DECIMAL(9,4)` | No | last-year elec-weighted CDD |
| 14 | `IS_FORECAST` | `BIT` | No | text **`True`/`False`** → map to `1`/`0` |

Natural key: `RunDate (from filename) + DATES`. (`subregion` = `national` for this
table; the `5region`/`9region`/`iso` siblings add `REGION_NAME` to the key — see §16–18.)

> Column names 3–4, 7–8, 11–12 begin with digits (`30Y_…`, `10Y_…`) — quote/escape
> when used as SQL identifiers, or rename in the model (e.g. `Ng_Hdd_30y`).

---

## 9. WindForecast → table `WindForecast`

- **Filename:** `{region}wind_{datemmddyyyy}.csv` (e.g. `ERCOTwind_08112026.csv`)
- **`{region}`** (UPPERCASE, **16**): `ERCOT, CAISO, MISO, PJM, SPP, NYISO, NEPOOL,
  BPA, IESO, AESO, UK, GERMANY, FRANCE, SPAIN, NW, SW`.
- **`{datemmddyyyy}`** = `MMDDYYYY` = production/init date (today valid).
- **Represents:** forecast wind generation MW by clock-hour × forecast-day.
- **Shape C** — **identical layout to SolarForecast §5**:
  - Row 1 = leading blank spacer; Row 2 = `Date (EST),<16 forecast dates>` in
    **`M/D/YYYY`** (non-padded), spanning **(init_date − 1) … (init_date + 14)**
    (first column = day before file date; `08112026` → `8/10/2026`…`8/25/2026`);
    Rows 3–26 = the same 24 hour labels; Row 27 blank.
  - **16 forecast-date columns.** Values are forecast wind MW (integer-valued, ≥ 0).

**Recommended normalized (unpivoted) row** (same shape as §5):
`(Region VARCHAR(10), InitDate DATE, ForecastDate DATE, HourLabel VARCHAR(8),
HourOfDay TINYINT, ValueMw DECIMAL(12,4))`.
Natural key `Region + InitDate + ForecastDate + HourOfDay`.

---

## 10. WindForecastSubRegion → table `WindForecastSubRegion`

- **Filename:** `{region}wind_regions_{datemmddyyyy}.csv`
  (e.g. `ERCOTwind_regions_08112026.csv`, `CAISOwind_regions_08112026.csv`)
- **`{region}`** (UPPERCASE, **8**): `ERCOT, CAISO, MISO, PJM, SPP, UK, GERMANY,
  FRANCE`.
- **`{datemmddyyyy}`** = `MMDDYYYY` = production/init date.
- **Represents:** forecast wind MW broken down into **sub-regions** of the parent
  region, one stacked matrix block per sub-region.
- **Shape D** (stacked pivoted matrix blocks → unpivot per block). Layout:
  - **No leading blank row** — file starts at the first block's label row.
  - Each block =
    1. **Block-delimiter/label row:** `<SubRegion> region,,,,,` — literally the
       sub-region name followed by the word ` region`, then **5 trailing commas**
       (6 fields total; the label row is **not** padded to full matrix width). The
       loader must strip the trailing ` region` suffix to get the sub-region label.
    2. **`Date (EST),<15 forecast dates>`** — **15 forecast-date columns** (verified on
       both ERCOT and CAISO — data rows also carry exactly 15 values). Dates in
       **`M/D/YYYY`** (non-padded), spanning **init_date … init_date + 14** (first
       column = the file date itself, e.g. `8/11/2026` … `8/25/2026`).
       **NOTE:** this is **15**, one fewer than the national WindForecast's 16 (the
       sub-region file does not include the prior-day column). Do not hardcode 16 —
       the loader should read the width from the block's `Date (EST)` header row.
    3. **24 hourly rows** (same 24 `12:00 AM…11:00 PM` labels), 15 MW values each.
  - **Between blocks:** exactly **3 blank rows** (`,,,,,` — 6 fields each).
- **Sub-region set is data-dependent (NOT a fixed enum)** — enumerate from the label
  rows at parse time. Observed:
  - **ERCOT (8 blocks, 229 lines):** `North, South, West, Geo-North, Geo-South,
    Geo-West, Geo-Panhandle, Geo-Coastal`.
  - **CAISO:** labels appear as `SP-15`, `NP-15` (companion `_CAISO_subregion_labels.txt`
    shows a numeric-prefixed variant `1:SP-15`, `30:NP-15`) — i.e. the sub-region label
    may carry a numeric prefix and hyphens. Treat the label as free text after stripping
    ` region`.
  - Other parent regions carry their own sub-region sets — discover, don't assume.

**Recommended normalized (unpivoted) row the loader should emit:**

| Column | SQL type | Null? | Notes |
|--------|----------|-------|-------|
| `Region` | `VARCHAR(10)` | No | parent region (UPPERCASE) from filename |
| `SubRegion` | `VARCHAR(40)` | No | from block label, ` region` suffix stripped (may include prefix/hyphens, e.g. `Geo-Panhandle`, `1:SP-15`) |
| `InitDate` | `DATE` | No | from filename `MMDDYYYY` |
| `ForecastDate` | `DATE` | No | from the block's `Date (EST)` header (`M/D/YYYY`) |
| `HourLabel` | `VARCHAR(8)` | No | e.g. `12:00 AM` |
| `HourOfDay` | `TINYINT` | No | derived 0–23 |
| `ValueMw` | `DECIMAL(12,4)` | No | forecast wind MW (integer-valued, ≥ 0) |

Natural key: `Region + SubRegion + InitDate + ForecastDate + HourOfDay`.

---

## 11. WindHourly → table `WindHourly`

- **Filename:** `Gen_hrly_5day.csv` — **no parameters** (single rolling ~9 KB file,
  most-recent 5 days; overwritten in place).
- **Represents:** **actual** hourly wind generation MW by region (rolling 5-day window).
- **Shape B** (wide-by-region → unpivot). **12 region value columns.**

Wide source columns (header row 1):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `UTC_HOUR_ENDING` | `DATETIME2(0)` | No | **`YYYY-MM-DD HH:MM:SS`** UTC, hour-ending |
| 2 | `CAISO` | `DECIMAL(12,4)` | Yes† | actual MW |
| 3 | `SPP` | `DECIMAL(12,4)` | Yes† | actual MW |
| 4 | `ERCOT` | `DECIMAL(12,4)` | Yes† | actual MW |
| 5 | `MISO` | `DECIMAL(12,4)` | Yes† | actual MW |
| 6 | `PJM` | `DECIMAL(12,4)` | Yes† | actual MW |
| 7 | `NEPOOL` | `DECIMAL(12,4)` | Yes† | actual MW |
| 8 | `NYISO` | `DECIMAL(12,4)` | Yes† | actual MW |
| 9 | `BPA` | `DECIMAL(12,4)` | Yes† | actual MW |
| 10 | `IESO` | `DECIMAL(12,4)` | Yes† | actual MW |
| 11 | `AESO` | `DECIMAL(12,4)` | Yes† | actual MW |
| 12 | `NW` | `DECIMAL(12,4)` | Yes† | actual MW |
| 13 | `SW` | `DECIMAL(12,4)` | Yes† | actual MW |

**† Sentinel:** samples are fully populated, but treat a literal `NULL` the same way as
SolarHourly (§7) for safety. **Region set (12, in file order):** `CAISO, SPP, ERCOT,
MISO, PJM, NEPOOL, NYISO, BPA, IESO, AESO, NW, SW` — **identical to DailyNormal §4**;
**differs from SolarHourly §7.**

**Recommended normalized (unpivoted) row:**
`(HourEndingUtc DATETIME2(0), Region VARCHAR(10), ActualMw DECIMAL(12,4) NULL)`.
Natural key `HourEndingUtc + Region`.

---

## 12. WindTotalCapacityClimatology → table `WindTotalCapacityClimatology`

- **Filename:** `Total_Capacity_climo_{datemmddyyyy}.csv`
  (e.g. `Total_Capacity_climo_08112026.csv`)
- **`{datemmddyyyy}`** = `MMDDYYYY` = production date.
- **Represents:** climatological wind-generation averages as **% of capacity** for
  three forecast horizons. **Single block** (no Yesterday/Change blocks).
- **Shape E.** Layout:
  - **Row 1 (title):** `Total Wind Generation Climo Across All Regions` (note the word
    **Climo**) — single cell, skip.
  - **Row 2 (header):** `,Total Capacity (MW),1-5 Day Avg (%),6-10 Day Avg (%),11-15 Day Avg (%)`
    (leading empty cell = the region label column).
  - **Rows 3–11:** one row per region (see set below).
  - **Row 12 (footer):** `Total All (MW),163999,39563,39649,40163` — note the avg
    columns here are **in MW**, not %.
  - **Row 13 (footer):** `Total Percent,,24%,24%,24%`.
  - **Row 14:** blank.

Per-region row columns (rows 3–11):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | *(region label)* → `Region` | `VARCHAR(10)` | No | e.g. `MISO` |
| 2 | `Total Capacity (MW)` | `DECIMAL(12,4)` | No | installed wind capacity MW (e.g. `33687`) |
| 3 | `1-5 Day Avg (%)` | `DECIMAL(6,2)` | No | **strip trailing `%`** (e.g. `23%` → `23.00`); climatological % of capacity |
| 4 | `6-10 Day Avg (%)` | `DECIMAL(6,2)` | No | strip `%` |
| 5 | `11-15 Day Avg (%)` | `DECIMAL(6,2)` | No | strip `%` |

**Region set (per-file, data-driven — 9 observed, in file order):** `MISO, ERCOT,
CAISO, SPP, PJM, NYISO, NEPOOL, NW, SW`. Enumerate from the region rows, not a fixed
enum.

**Recommended normalized row the loader should emit** (one per region; the loader may
optionally also capture the `Total All (MW)` / `Total Percent` footer rows as a
special `Region` value, or skip them):
`(ProductionDate DATE, Region VARCHAR(10), TotalCapacityMw DECIMAL(12,4),
Avg_1_5 DECIMAL(6,2), Avg_6_10 DECIMAL(6,2), Avg_11_15 DECIMAL(6,2))`.
Natural key `ProductionDate + Region`.

---

## 13. WindTotalCapacityMW → table `WindTotalCapacityMW`

- **Filename:** `Total_Capacity_vals_{datemmddyyyy}.csv`
  (e.g. `Total_Capacity_vals_08112026.csv`)
- **`{datemmddyyyy}`** = `MMDDYYYY` = production date.
- **Represents:** forecast wind generation averaged over three horizons, expressed in
  **MW**, in **three stacked blocks**: **Current forecast**, **Yesterday's Forecast**,
  and **Change from Yesterday's Forecast**.
- **Shape E** (multi-block). Layout:
  - **Block 1 — Current:** Row 1 title `Total Wind Generation Across All Regions`;
    Row 2 header `,Total Capacity (MW),1-5 Day Avg (MW),6-10 Day Avg (MW),11-15 Day Avg (MW)`;
    9 region rows; footer `Total All (MW),163999,59459,41707,35595`; footer
    `Total Percent,,36%,25%,22%`; blank row.
  - **Block 2 — Yesterday:** title row `Yesterday's Forecast`; 9 region rows; same two
    footer rows; blank row.
  - **Block 3 — Change:** title row `Change from Yesterday's Forecast`; 9 region rows
    with **empty `Total Capacity (MW)`** and **signed** MW deltas in the avg columns;
    footer `Total Change,,994,490,1591`; blank row.

Per-region row columns (each block, rows under its header):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | *(region label)* → `Region` | `VARCHAR(10)` | No | e.g. `ERCOT` |
| 2 | `Total Capacity (MW)` | `DECIMAL(12,4)` | Yes | installed capacity MW; **blank in the Change block** → `NULL` |
| 3 | `1-5 Day Avg (MW)` | `DECIMAL(12,4)` | No | forecast MW (signed delta in Change block) |
| 4 | `6-10 Day Avg (MW)` | `DECIMAL(12,4)` | No | forecast MW (signed in Change block) |
| 5 | `11-15 Day Avg (MW)` | `DECIMAL(12,4)` | No | forecast MW (signed in Change block) |

**Region set:** same 9 as §12 (`MISO, ERCOT, CAISO, SPP, PJM, NYISO, NEPOOL, NW, SW`;
data-driven).

**Recommended normalized row the loader should emit** — add a **`Block` discriminator**
to keep the three blocks distinct:
`(ProductionDate DATE, Block VARCHAR(12) /* Current | Yesterday | Change */,
Region VARCHAR(10), TotalCapacityMw DECIMAL(12,4) NULL, Avg_1_5 DECIMAL(12,4),
Avg_6_10 DECIMAL(12,4), Avg_11_15 DECIMAL(12,4))`.
Natural key `ProductionDate + Block + Region`. (Footer `Total All`/`Total Percent`/
`Total Change` rows: capture as special `Region` values or skip.)

---

## 14. WindTotalCapacityPct → table `WindTotalCapacityPct`

- **Filename:** `Total_Capacity_{datemmddyyyy}.csv`
  (e.g. `Total_Capacity_08112026.csv`)
- **`{datemmddyyyy}`** = `MMDDYYYY` = production date.
- **Represents:** the same three-block structure as §13, but the avg columns are the
  horizon averages as **% of capacity** (not MW). **Three stacked blocks**: Current,
  Yesterday, Change.
- **Shape E** (multi-block). The **only cross-file differences vs §13** are: the header
  avg columns read `… Avg (%)`; the region-row avg values carry a trailing `%`; and the
  Block-3 footer is `Total Percent,,0%,0%,1%` (not `Total Change`). **Structure (three
  blocks, 9 regions, footers) is otherwise identical.** NOTE: even in this %-file, the
  `Total All (MW)` footer row's avg cells are in **MW** (`59459,41707,35595`), while
  `Total Percent` is in %.

Per-region row columns (each block):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | *(region label)* → `Region` | `VARCHAR(10)` | No | e.g. `MISO` |
| 2 | `Total Capacity (MW)` | `DECIMAL(12,4)` | Yes | installed capacity MW; **blank in Change block** → `NULL` |
| 3 | `1-5 Day Avg (%)` | `DECIMAL(6,2)` | No | **strip `%`**; signed in Change block (e.g. `-4%` → `-4.00`) |
| 4 | `6-10 Day Avg (%)` | `DECIMAL(6,2)` | No | strip `%`; signed in Change block |
| 5 | `11-15 Day Avg (%)` | `DECIMAL(6,2)` | No | strip `%`; signed in Change block |

**Region set:** same 9 as §12/§13 (data-driven).

**Recommended normalized row the loader should emit** (mirror §13, with `%` measure):
`(ProductionDate DATE, Block VARCHAR(12), Region VARCHAR(10),
TotalCapacityMw DECIMAL(12,4) NULL, Avg_1_5 DECIMAL(6,2), Avg_6_10 DECIMAL(6,2),
Avg_11_15 DECIMAL(6,2))`.
Natural key `ProductionDate + Block + Region`.

> **§12–§14 relationship:** `Total_Capacity` (Pct), `Total_Capacity_vals` (MW), and
> `Total_Capacity_climo` (climatological %) share the same `Region` + `Total Capacity
> (MW)` columns. Differences: Pct/MW carry **three** blocks (Current/Yesterday/Change);
> Climo carries **one**. Avg-column unit = % (Pct), MW (MW), climatological % (Climo).
> DATABASE_DEVELOPER may model these as three tables or one table with a
> `Measure` (`Pct`/`MW`/`Climo`) + `Block` discriminator — coordinate on that choice.
> **This build models them as three separate tables** (one per endpoint/target name).

---

## 15. Station → table `Station`

- **Filename:** `{region}_station_information.csv`
  (e.g. `northamerica_station_information.csv`) — the **forecast-station** file (in
  scope). The `{region}_all_station_information.csv` variant (all stations) is **out of
  scope** per the locked decision.
- **`{region}`** ∈ `northamerica`, `asia`, `europe` (all three).
- **Represents:** static station reference metadata; ~424 rows (NA), overwritten in
  place. No date token.
- **Shape A.** **9 columns.**

Ordered columns (header row 1):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `identifier` | `VARCHAR(8)` | No | station id (ICAO-style, e.g. `CYUL`); FK target of `CityForecast.Station` / `CityObservation.station` |
| 2 | `wmoid` | `VARCHAR(11)` | Yes | WMO id (e.g. `71383`); keep as string (id, not a measure) |
| 3 | `wban` | `VARCHAR(11)` | Yes | WBAN id; **`99999` is the "missing" sentinel** (real values also occur, e.g. `94792`) — map `99999` → `NULL` or keep as-is per DB decision |
| 4 | `ghcnd` | `VARCHAR(16)` | Yes | GHCN-Daily id; **empty in NA samples** → nullable |
| 5 | `lat` | `DECIMAL(9,6)` | No | latitude (3 dp in samples, e.g. `50.733`) |
| 6 | `lon` | `DECIMAL(9,6)` | No | longitude (signed, 3 dp, e.g. `-71.017`) |
| 7 | `name` | `NVARCHAR(64)` | No | station name; **source truncates to ~16 chars** (e.g. `Edmonton Interna`, `Halifax Internat`) — allow room but expect truncated source values |
| 8 | `state` | `VARCHAR(8)` | Yes | state/province code (e.g. `NB`, `AB`); may be blank for non-US/CA |
| 9 | `country` | `VARCHAR(4)` | No | 2-letter country code (e.g. `CA`, `US`) |

Natural key: `region + identifier`.

---

## 16. Regions5DegreeDays → table `Regions5DegreeDays`

- **Filename:** `northamerica_{subregion}_wdd_{date}.csv` with **`{subregion}` = `5region`**
  (e.g. `northamerica_5region_wdd_20260811.csv`).
- **`{date}`** = `YYYYMMDD` = **production / RUN date**, taken from the **filename** —
  there is **no RunDate column** in the file. Confirmed `200` for `20260811`.
- **Represents:** the same weighted-degree-day window as NationalDegreeDays §8, but
  **broken out into 5 super-regions** with per-region gas/electric/population weights.
  Each row is one calendar day for one region across a **(RunDate − 7 … RunDate + 14)**
  window — 22 days per region (`2026-08-04 … 2026-08-25` for the `20260811` file), split
  by `IS_FORECAST` (dates **before** the run date = `False`; the run date and later =
  `True`).
- **Shape A** (simple tabular; one row = one `(RunDate, DATES, REGION_NAME)` record).
- **18 columns** — **identical layout to Regions9DegreeDays §17.**

Ordered columns (header row 1 exactly as shown):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `DATES` | `DATE` | No | **`YYYY-MM-DD`** observation/forecast day (within the RunDate−7…+14 window) |
| 2 | `REGION_NAME` | `VARCHAR(32)` | No | **data-driven** region label (free text, mixed case, may contain spaces); observed max length **13**. See region set below |
| 3 | `NG_HDD` | `DECIMAL(9,4)` | No | gas-weighted HDD (this year), 4 dp |
| 4 | `30Y_NG_HDD` | `DECIMAL(9,4)` | No | 30-year-normal gas-weighted HDD. **Header starts with a digit** |
| 5 | `10Y_NG_HDD` | `DECIMAL(9,4)` | No | 10-year-normal gas-weighted HDD (`0.0000` is a valid value, not null) |
| 6 | `LAST_Y_NG_HDD` | `DECIMAL(9,4)` | No | last-year gas-weighted HDD |
| 7 | `POP_CDD` | `DECIMAL(9,4)` | No | population-weighted CDD (this year) |
| 8 | `30Y_POP_CDD` | `DECIMAL(9,4)` | No | 30-year-normal pop-weighted CDD |
| 9 | `10Y_POP_CDD` | `DECIMAL(9,4)` | No | 10-year-normal pop-weighted CDD |
| 10 | `LAST_Y_POP_CDD` | `DECIMAL(9,4)` | No | last-year pop-weighted CDD |
| 11 | `ELEC_CDD` | `DECIMAL(9,4)` | No | electricity-weighted CDD (this year) |
| 12 | `30Y_ELEC_CDD` | `DECIMAL(9,4)` | No | 30-year-normal elec-weighted CDD |
| 13 | `10Y_ELEC_CDD` | `DECIMAL(9,4)` | No | 10-year-normal elec-weighted CDD |
| 14 | `LAST_Y_ELEC_CDD` | `DECIMAL(9,4)` | No | last-year elec-weighted CDD |
| 15 | `IS_FORECAST` | `BIT` | No | text **`True`/`False`** → `1`/`0`; `True` for `DATES` ≥ RunDate |
| 16 | `GAS_WEIGHT` | `DECIMAL(9,4)` | No | region's gas-demand weight — a **0..1 fraction** (4 dp); constant per region within a file |
| 17 | `ELCT_WEIGHT` | `DECIMAL(9,4)` | No | region's electric weight (**header spelled `ELCT`, not `ELEC`** — note the divergence from the `ELEC_CDD` family) |
| 18 | `POP_WEIGHT` | `DECIMAL(9,4)` | No | region's population weight (0..1 fraction) |

**Region set (5, data-driven — enumerate from `REGION_NAME`, do not hardcode):**
`Pacific, Mountain, South Central, Midwest, East` (max label length **13**).

**Footer:** the file's **last line is a literal `END.` sentinel row** (`DATES=END.`,
all other fields empty) — **the loader must skip it.**

Natural key: `RunDate (from filename) + DATES + REGION_NAME`.

> Column names 4–5, 8–9, 12–13 begin with digits (`30Y_…`, `10Y_…`) — quote/escape when
> used as SQL identifiers, or rename in the model (e.g. `Ng_Hdd_30y`), consistent with
> §8. The `LAST_Y_…` columns start with a letter and are safe as-is.

---

## 17. Regions9DegreeDays → table `Regions9DegreeDays`

- **Filename:** `northamerica_{subregion}_wdd_{date}.csv` with **`{subregion}` = `9region`**
  (e.g. `northamerica_9region_wdd_20260811.csv`).
- **`{date}`** = `YYYYMMDD` = **production / RUN date** from the **filename** (no RunDate
  column). Confirmed `200` for `20260811`.
- **Represents:** identical measures to Regions5DegreeDays §16 but broken out into the
  **9 U.S. Census divisions**. Same (RunDate − 7 … RunDate + 14) window, same
  `IS_FORECAST` split.
- **Shape A.** **18 columns — the layout, column names, types, nullability, digit-leading
  header quirks, and the `ELCT_WEIGHT` spelling are all IDENTICAL to Regions5DegreeDays
  §16.** See that table for the full per-column reference; the only difference is the
  `REGION_NAME` value set.

**Region set (9, data-driven — enumerate from `REGION_NAME`):**
`NEW ENGLAND, MIDDLE ATLANTIC, E N CENTRAL, W N CENTRAL, SOUTH ATLANTIC, E S CENTRAL,
W S CENTRAL, MOUNTAIN, PACIFIC` (UPPERCASE here, unlike §16's mixed case; max label
length **15** — `MIDDLE ATLANTIC`). `VARCHAR(32)` for `REGION_NAME` covers this with
headroom.

**Footer:** last line is the literal **`END.` sentinel row** (`DATES=END.`, other fields
empty) — **skip it.**

Natural key: `RunDate (from filename) + DATES + REGION_NAME`.

---

## 18. ISODegreeDays → table `ISODegreeDays`

- **Filename:** `northamerica_{subregion}_wdd_{date}.csv` with **`{subregion}` = `iso`**
  (e.g. `northamerica_iso_wdd_20260811.csv`).
- **`{date}`** = `YYYYMMDD` = **production / RUN date** from the **filename** (no RunDate
  column). Confirmed `200` for `20260811`.
- **Represents:** weighted degree-days broken out by **ISO / power-market region**. Same
  (RunDate − 7 … RunDate + 14) window and `IS_FORECAST` split as §16/§17.
- **Shape A.** **11 columns.**
- **⚠ Layout DIVERGES from §16/§17** (do not reuse their column set): the HDD family is
  **`POP_HDD`** (population-weighted), **not `NG_HDD`**; there is **NO `ELEC_*` family**;
  and there are **NO weight columns** (`GAS_WEIGHT`/`ELCT_WEIGHT`/`POP_WEIGHT` absent).

Ordered columns (header row 1 exactly as shown):

| # | Source column | SQL type | Null? | Notes |
|---|---------------|----------|-------|-------|
| 1 | `DATES` | `DATE` | No | **`YYYY-MM-DD`** observation/forecast day |
| 2 | `REGION_NAME` | `VARCHAR(32)` | No | **data-driven** ISO/market label (free text, UPPERCASE, may contain spaces); observed max length **11**. See region set below |
| 3 | `POP_HDD` | `DECIMAL(9,4)` | No | population-weighted HDD (this year), 4 dp. **Note: `POP_HDD`, NOT `NG_HDD`** |
| 4 | `30Y_POP_HDD` | `DECIMAL(9,4)` | No | 30-year-normal pop-weighted HDD. **Header starts with a digit** |
| 5 | `10Y_POP_HDD` | `DECIMAL(9,4)` | No | 10-year-normal pop-weighted HDD (`0.0000` is a valid value, not null) |
| 6 | `LAST_Y_POP_HDD` | `DECIMAL(9,4)` | No | last-year pop-weighted HDD |
| 7 | `POP_CDD` | `DECIMAL(9,4)` | No | population-weighted CDD (this year); max observed ~35 (SOUTHWEST) |
| 8 | `30Y_POP_CDD` | `DECIMAL(9,4)` | No | 30-year-normal pop-weighted CDD |
| 9 | `10Y_POP_CDD` | `DECIMAL(9,4)` | No | 10-year-normal pop-weighted CDD |
| 10 | `LAST_Y_POP_CDD` | `DECIMAL(9,4)` | No | last-year pop-weighted CDD |
| 11 | `IS_FORECAST` | `BIT` | No | text **`True`/`False`** → `1`/`0`; `True` for `DATES` ≥ RunDate |

**Region set (21, data-driven — enumerate from `REGION_NAME`):**
`ERCOT, EAST PJM, WEST PJM, PJM, CAISO, NYISO, WECC, SERC, MISO, TVA, SPP, NEPOOL, IESO,
UPPER MISO, LOWER MISO, QUEBEC, AESO, BPA, CAISO NORTH, CAISO SOUTH, SOUTHWEST` (max
label length **11** — `CAISO NORTH`/`CAISO SOUTH`).

**Footer:** last line is the literal **`END.` sentinel row** (`DATES=END.`, other fields
empty) — **skip it.**

Natural key: `RunDate (from filename) + DATES + REGION_NAME`.

> Column names 4–5, 8–9 begin with digits (`30Y_POP_HDD`, `10Y_POP_HDD`,
> `30Y_POP_CDD`, `10Y_POP_CDD`) — quote/escape or rename in the model, consistent with §8.

---

## Coverage checklist (all 18 endpoints — column list + type + nullability + shape)

| # | Table | Shape | Cols documented | Key notes captured |
|---|-------|:----:|:---------------:|--------------------|
| 1 | CityForecast | A | 10 | `M/D/YY`; `Norm Max`/`Fcst Mn/Mx` header quirks |
| 2 | CityGasForecast | A | 10 | identical to §1; no params; in-row production date |
| 3 | CityObservation | A | 6 | `YYYY-MM-DD`; date = run-date − 1 (today 404s) |
| 4 | DailyNormal | B | 13 (1 key + 12 regions) | **366 rows** incl. `02-29`; `MM-DD` key; unpivot |
| 5 | SolarForecast | C | 24×**16** matrix | leading blank row; `M/D/YYYY`; init−1…+14; 24 hour labels |
| 6 | SolarForecastChange | C | 24×**14** + trailing comma + 2 footer rows | `MM/DD/YYYY` padded; no lead blank; sum/avg footers |
| 7 | SolarHourly | B | 15 (1 ts + 14 regions) | `DATETIME2` UTC; 14-region set (FRCC/CAR/SE/TVA); `"NULL"` sentinel |
| 8 | NationalDegreeDays | A | 14 | `national` aggregate (no REGION_NAME/weights); siblings `5region`/`9region`/`iso` in §16–18; `IS_FORECAST` True/False→BIT; digit-leading headers; `END.` footer |
| 9 | WindForecast | C | 24×**16** matrix | same shape as §5; 16 regions param |
| 10 | WindForecastSubRegion | D | per block: label + 24×**15** matrix, 3-blank separators | **15 cols (not 16)**; data-driven sub-region labels; ` region` suffix |
| 11 | WindHourly | B | 13 (1 ts + 12 regions) | 12-region set = DailyNormal set |
| 12 | WindTotalCapacityClimatology | E | 5/region (single block) | title `…Climo…`; strip `%`; 9-region data-driven set |
| 13 | WindTotalCapacityMW | E | 5/region × **3 blocks** | Current/Yesterday/Change; MW; signed deltas |
| 14 | WindTotalCapacityPct | E | 5/region × **3 blocks** | strip `%`; signed deltas; `Total All (MW)` footer in MW |
| 15 | Station | A | 9 | forecast-station file only; name truncated ~16; `wban=99999` sentinel |
| 16 | Regions5DegreeDays | A | 18 (2 keys + 12 DD + IS_FORECAST + 3 weights) | `5region`; REGION_NAME 5-set (max 13); weights 0..1; `ELCT` spelling; `END.` footer |
| 17 | Regions9DegreeDays | A | 18 | layout identical to §16; REGION_NAME 9-set (UPPERCASE, max 15) |
| 18 | ISODegreeDays | A | 11 | `iso`; **POP_HDD (no NG_HDD/ELEC family), no weights**; REGION_NAME 21-set (max 11); `END.` footer |

**Gate status: PASS** — all 18 endpoints have a complete ordered column list, a
recommended SQL Server type per column, nullability/sentinel notes, and a parse-shape
assignment (A–E). The `DECIMAL(12,4)` MW-family sizing (vs. the overflowing
`DECIMAL(9,4)`) is flagged explicitly for DATABASE_DEVELOPER.
