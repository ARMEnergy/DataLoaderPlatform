# Criterion (Criterion Research — data-delivery replica) loader — source field reference

**Vendor:** Criterion Research, LLC
**Product:** Data-delivery PostgreSQL replica (`dda.criterionrsch.com`)
**Source type:** **PostgreSQL 
 relational database** — not an HTTP API, not a file feed
**In-scope relations:** 8 (across schemas `data_series`, `misc`, `pipelines`)
**Target:** database `Criterion`, schema `arm`, nine tables

---

## Gate status: **FULLY VERIFIED — zero reconstruction** ✅

Every statement in this document was verified against the **live production database** on
**2026-09-03** with the issued credentials. Nothing is inferred from documentation, and nothing is
reconstructed.

Evidence base:

- `pg_attribute` / `pg_class` / `pg_constraint` / `pg_indexes` read directly for every in-scope
  relation — so column names, types, nullability, dropped columns and partitioning are observed
  facts, not guesses.
- Row counts, key-uniqueness and NULL-rate checks over the live tables.
- JSON payload shapes sampled across 300+ rows.
- **The loader's own generated SQL executed against production for all nine feeds**, asserting that
  every descriptor column comes back by name in descriptor order (see §7).
- **The loader's full read path executed against production for all nine feeds** — 0 truncations,
  0 dropped rows, 0 unparseable values, all CLR types correct.

---

## ⚠⚠ THE FIVE THINGS THAT WILL BITE YOU

Read these before anything else. Each was verified live; each is silent if you get it wrong.

| # | Trap | Consequence if missed |
|---|------|----------------------|
| 1 | **`data_series.financial_json_partitioned` is DEAD.** Last write 2024-10-07, and it shares **zero** `financial_json_uuid` values with the live table. | Loading `arm.Financial_SeriesData` from it yields **0 rows** on any recent window, *and* the table would never join `arm.Financial_Series`. |
| 2 | **The `data` JSON has TWO shapes in the same column.** ~97% daily `{date,value}`; ~3% five-minute intraday `{date,timestamp,time_zone,value}`. | A parser that assumes one shape either drops half the rows or mis-reads the intraday ones. And the PK admits one row per *date*. |
| 3 | **Three different date formats in the same column**, and `"10/5/2021"` is **month-first**. | A day-first reading turns 5 October into 10 May. An ambient `en-GB` culture does exactly that. |
| 4 | **`misc.period` and `misc.unit` do not exist.** The tables are `misc.periods` / `misc.units`, keyed `period_uuid` / `unit_uuid`. | Query fails outright — the *lucky* failure mode here. |
| 5 | **`character(n)` columns are BLANK-PADDED.** `state_abb` comes back `'IA   '`. | Untrimmed values compare unequal to the same code everywhere downstream. |

---

## 1. Connection

| Setting | Value | Note |
|---|---|---|
| Host | `dda.criterionrsch.com` | |
| Port | **443** | **Not 5432.** Criterion fronts the database on the HTTPS port so it survives egress firewalls that allow only 80/443. It still speaks the **PostgreSQL wire protocol**, not HTTP. |
| Database | `production` | |
| SSL | `Require` | Mandatory. A weaker mode either fails the handshake or risks plaintext credentials. |
| Username / password | `SEE_DB` | Resolved from `core.Param(LoaderName='Criterion', …)`. Never logged. |

Verified live: connection succeeds, `pg_catalog` is readable, and all in-scope relations are
selectable with the issued account.

---

## 2. Source scale — why every fact table is windowed

Read from `pg_class.reltuples` and `pg_total_relation_size` on 2026-09-03.

| Relation | Est. rows | Size | Loader treatment |
|---|---:|---:|---|
| `pipelines.nomination_points` | **75,553,232** | 124 GB | `DaysBack` window on `eff_gas_day` |
| `data_series.financial_json_partitioned` | 52,419,860 | 1,066 GB | **not read — stale, see §5.1** |
| `data_series.financial_json` | 8,901,150 | 1,309 GB | **not read — superseded, see §5.1** |
| `data_series.financial_json_latest` | 2,242,662 | — | `DaysBack` window on `post_date` |
| `pipelines.metadata` | 42,446 | 109 MB | full snapshot |
| `data_series.financial_metadata` | 2,265 | 3.9 MB | full snapshot |
| `misc.units` | 101 | 8 KB | full snapshot |
| `pipelines.regions` | 51 | 24 KB | full snapshot |
| `misc.periods` | 12 | 8 KB | full snapshot |

A full-table read of the first three is not merely slow — it is more data than the destination has
any use for, since they are **vintage stores** that keep every republication of every series.

---

## 3. Relation-by-relation column reference

All columns below were read from `pg_attribute`. Where a target column is missing from the source,
that is stated explicitly rather than left to inference.

### 3.1 `data_series.financial_metadata` → `arm.Financial_Metadata`

2,265 rows. `metadata_uuid` unique, never NULL (2,265/2,265).

| # | Source column | PostgreSQL type | → Target | Note |
|---|---|---|---|---|
| 11 | `metadata_uuid` | `uuid` | `MetadataId` | PK |
| 1 | `entity_name` | `varchar(100)` | `EntityName` | max observed 48 |
| 2 | `metadata_desc` | `varchar(100)` | `MetadataDesc` | max observed 73 |
| 3 | `cmdty_class` | `varchar(50)` | `CmdtyClass` | |
| 4 | `sub_cmdty_desc` | `varchar(150)` | `SubCmdtyDesc` | |
| 5 | `asset_name` | `varchar(150)` | `AssetName` | |
| 6 | `region_name` | `varchar(200)` | `RegionName` | |
| 7 | `country_name` | `varchar(50)` | `CountryName` | |
| 8 | `state_name` | `varchar(50)` | `StateName` | |
| 9 | `province_name` | `varchar(100)` | `ProvinceName` | |
| 10 | `mongo_id` | **`character(24)`** | `MongoId` | **blank-padded — trimmed** |
| 12 | `status` | `boolean` | `Status` | |
| 13 | `series_desc` | `varchar(150)` | `SeriesDesc` | max observed 73 |
| 14 | `series_id` | `varchar(50)` | `SeriesId` | |
| 15 | `table_name` | `varchar(100)` | `TableName` | **NULL for every row today** |
| 17 | `entity_id` | **`character(24)`** | `EntityId` | **blank-padded — trimmed** |
| 19 | `series_type` | `varchar(50)` | `SeriesType` | Forecast 817 / *(null)* 696 / Actual 628 / Weather 102 / … |
| 20 | `sub_region` | `varchar(100)` | `SubRegion` | |
| 22 | `ticker` | `varchar(100)` | `Ticker` | |
| — | **no source column** | — | **`Enabled`** | **ARM-local flag — never written by the loader** |

Attnums 16, 18 and 21 are dropped columns (`pg_attribute.attisdropped`), which is why the numbering
has gaps.

### 3.2 `data_series.financial_json_latest` → `arm.Financial_Series`

Partitioned `RANGE (post_date)`. PK `(financial_json_uuid, post_date)`. Over a 30-day window:
**46,032 rows, 46,032 distinct UUIDs, one row per (series, post_date)**. No UUID was seen under two
post_dates across 60 days, so the target's single-column PK is safe.

| # | Source column | PostgreSQL type | → Target | Note |
|---|---|---|---|---|
| 1 | `financial_json_uuid` | `uuid` | `FinancialJsonId` | PK |
| 2 | `metadata_uuid` | `uuid` | `MetadataId` | → `arm.Financial_Metadata.MetadataId` |
| 3 | `post_date` | `date` | `PostDate` | **the partition key — window on this** |
| 4 | `forecast_date` | `date` | `ForecastDate` | populated on 21,428 / 46,032 |
| 5 | `load_date` | `timestamp` | `LoadDate` | |
| 6 | `filename` | `varchar(255)` | `Filename` | populated on 2,085 / 46,032; max observed 56 |
| 7 | `version` | `integer` | `Version` | always populated |
| 8 | `period_id` | **`character(24)`** | `PeriodId` | **a MONGO ID, not a UUID** — see §4.2 |
| 9 | `unit_id` | **`character(24)`** | `UnitId` | **a MONGO ID**; 60 rows are all-blank → NULL |
| 10 | `data` | `json` | *(→ `arm.Financial_SeriesData`)* | **not selected by this feed** — see §4.3 |
| 11 | `active` | `boolean` | `Active` | always populated |
| 12 | `forecast_date_time` | `timestamp` | `ForecastDateTime` | populated on 16,294 / 46,032 |
| 13 | `ticker` | `varchar` | *(not mapped)* | no target column in the supplied DDL |

### 3.3 `misc.periods` → `arm.Misc_Period` · `misc.units` → `arm.Misc_Unit`

**The request named `misc.period` and `misc.unit`. Neither exists.** 12 and 101 rows respectively;
both keys unique and never NULL.

| Source | PostgreSQL type | → Target |
|---|---|---|
| `periods.period_uuid` | `uuid` | `PeriodId` (PK) |
| `periods.mongo_id` | **`character(24)`** | `MongoId` — **the join column**, see §4.2 |
| `periods.period_desc` | `varchar(75)` | `PeriodDesc` |
| `periods.period_short` | `varchar(20)` | `PeriodShort` |
| `units.unit_uuid` | `uuid` | `UnitId` (PK) |
| `units.mongo_id` | **`character(24)`** | `MongoId` — **the join column** |
| `units.unit_desc` | `varchar(50)` | `UnitDesc` |

Live contents of `misc.periods`: Annual, Day, Hour, Month, Point In Time, Quarter, Quarter to
Previous Quarter, Week, Weekend, Work Week, Year, Year-on-Year.

### 3.4 `pipelines.metadata` → `arm.Pipelines_Metadata`

42,446 rows; `metadata_id` unique, never NULL. `update_date` spans 2016-10-25 … 2026-08-07.

All 37 mapped columns match the supplied DDL by name and width. The ones worth calling out:

| Source column | PostgreSQL type | → Target | Note |
|---|---|---|---|
| `asset_id` | **`character(24)`** | `AssetId` | trimmed |
| `loc_qti_short` | **`character(3)`** | `LocQtiShort` | trimmed |
| `storage_calc_flag` | **`character(1)`** | `StorageCalcFlag` | trimmed |
| `tsp_short` | **`character(3)`** | `TspShort` | trimmed; max observed 3 |
| `state_abb` | `varchar(7)` | `StateAbb` | max observed 2 |
| `latitude` | `numeric(13,10)` | `Latitude` | ⚠ **NULL for all 42,446 rows** |
| `longitude` | `numeric(13,10)` | `Longitude` | ⚠ **NULL for all 42,446 rows** |
| `updn_loc` | `text` | `UpdnLoc` | target is `VARCHAR(MAX)`; max observed 48 |
| — | **no source column** | **`Point`** (geography) | **derived in-proc from lat/long → NULL today** |
| — | **no source column** | **`MappingId`** | **local `IDENTITY`** |

The source also carries `loc_segment`, `connection_xref_ticker`, `connection_xref` and
`connection_xref_tickers` (`text[]`), none of which has a target column in the supplied DDL.

### 3.5 `pipelines.nomination_points` → `arm.Pipelines_NominationPoint`

75.5 M rows; `eff_gas_day` spans 2008-01-01 … 2026-09-06 (**the source publishes forward-dated gas
days**). A 30-day window is **602,533 rows**.

| Source column | PostgreSQL type | → Target | Note |
|---|---|---|---|
| `metadata_id` | `varchar(30)` | `MetadataId` | PK; max observed 26 |
| `eff_gas_day` | `date` | `EffGasDay` | PK; **the window column**, indexed |
| `cycle_id` | `smallint` | `CycleId` | PK |
| `hourly_cycle_id` | `integer` | `HourlyCycleId` | PK; observed values 0, 9, 10, 17, 18, 19 |
| `end_eff_gas_day` | `date` | `EndEffGasDay` | |
| `tsp_short` | **`character(3)`** | `TspShort` | trimmed |
| `cycle_desc` | `varchar(30)` | `CycleDesc` | free text — see below |
| `design_capacity` | `double precision` | `DesignCapacity` | |
| `operating_capacity` | `double precision` | `OperatingCapacity` | |
| `scheduled_quantity` | `double precision` | `ScheduledQuantity` | |
| `operationally_available` | `double precision` | `OperationallyAvailable` | |
| `tbl` | `varchar(10)` | `Tbl` | |
| `ticker` | `varchar(100)` | `Ticker` | |
| — | **no source column** | **`IsLatest`** | **ARM-local flag — never written by the loader** |

No PK component is ever NULL (0 / 602,533). **The source ships 118 exact duplicate rows on the full
four-column key in a 30-day window**, with identical payloads — which is why the merge de-duplicates.

`cycle_desc` is *not* a controlled vocabulary. One gas day carries `Timely`, `Evening`, `Intraday 1`,
`ID2`, `INTRADAY 2`, `Intra-Day 2`, `Criterion Estimate`, `INTRDY_2026-09-03_1800`, and more — the
same `cycle_id` maps to several spellings. Do not key anything on it.

### 3.6 `pipelines.regions` → `arm.Pipelines_Region`

51 rows; `(region_id, state_id)` unique. Six regions: Midwest, Northeast, Rockies, South Central,
Southeast, West.

| Source column | PostgreSQL type | → Target | Note |
|---|---|---|---|
| `region_id` | **`character(24)`** | `RegionId` | PK; trimmed |
| `state_id` | **`character(24)`** | `StateId` | PK; trimmed |
| `region_name` | `varchar(30)` | `RegionName` | |
| `state_abb` | **`character(5)`** | `StateAbb` | trimmed — comes back `'IA   '` |
| `state_name` | `varchar(50)` | `StateName` | |
| `eia_ng_regions` | `varchar(30)` | `EIA_NG_Regions` | empty string for AK and HI |
| `eia_padd_regions` | `varchar(10)` | `EIA_PADD_Regions` | |

---

## 4. The behaviours that are not in any schema

### 4.1 `character(n)` is blank-padded

PostgreSQL pads `character(n)` to its declared width. Fourteen in-scope columns are affected. Two
consequences, both silent:

- `state_abb` arrives as `'IA   '` and compares unequal to `'IA'` after landing in a `VARCHAR`.
- `financial_json_latest.unit_id` holds **24 spaces** rather than NULL for **60 rows** in a 30-day
  window. Trimming alone turns that into `''`, which reads as *"a unit id we could not resolve"*
  rather than *"no unit"*.

The loader wraps every such column as `NULLIF(TRIM(col), '')`. Both halves are load-bearing.

### 4.2 `PeriodId` / `UnitId` are Mongo ids, and join on `MongoId`

`financial_json_latest.period_id` and `.unit_id` carry values like `5528841f18d8ffe42a1becce` — the
dimensions' **`mongo_id`**, not their `period_uuid` / `unit_uuid`. The supplied `VARCHAR(24)` target
types are therefore correct, and the join is:

```sql
arm.Financial_Series.PeriodId = arm.Misc_Period.MongoId   -- NOT = PeriodId
arm.Financial_Series.UnitId   = arm.Misc_Unit.MongoId     -- NOT = UnitId
```

No FK is declared because the match is not total: over a 2-day sample, **46 / 3,242 period ids and
26 / 3,242 unit ids** had no dimension row. `arm.usp_ValidateLoad` reports the rate rather than
failing on it.

### 4.3 ⚠ The `data` JSON has two shapes, three date formats, and two value encodings

**Shape.** Sampled over 300 recent rows: **152 daily, 148 intraday** — roughly half the *rows*, and
about 3% of *series* once weighted by how often each publishes.

```jsonc
// daily  (~97% of rows in a typical window)
[{"date":"09/02/2026","value":4966.60}, ...]

// intraday  (~3%) — five-minute observations
[{"date":"07/05/2026","timestamp":"07/05/2026 00:04:57",
  "time_zone":"Central Time","value":4966.60}, ...]
```

**Size.** Mean **165 KB** per array, max **1.75 MB**; mean **3,041 elements**, max **17,496**. Seven
days of `financial_json` alone is **6.3 GB** of JSON.

**Date format.** All three of these are live **in the same column**:

| Format | Example | Note |
|---|---|---|
| `MM/dd/yyyy` | `09/02/2026` | most common |
| `yyyy-MM-dd` | `2023-08-24` | |
| ISO-8601 + `Z` | `2016-08-21T00:00:00.000Z` | time component always midnight |

⚠ **`"10/5/2021"` is 5 October, not 10 May.** Criterion is a US vendor and every sampled series is
month-first (values > 12 appear only in the second component). The loader pins
`CultureInfo.InvariantCulture` and month-first formats, because an ambient culture of `en-GB` on the
host would otherwise silently reinterpret every date in the table.

**Value encoding.** Live data sends a bare JSON **number**. The sample supplied with the request sent
a **quoted string** (`"value":"21453.5943776865"`). The loader accepts both.

### 4.4 ⚠ The intraday shape does not fit the requested primary key

`arm.Financial_SeriesData` is keyed `(FinancialJsonId, Date)` — one row per calendar day. An intraday
array carries **up to 288 observations per date** (ERCOT Real-time Fuel Mix: 17,496 elements across
**61 distinct dates**).

**Decision taken 2026-09-03: keep the supplied key, accept last-wins.** See
[`docs/design/Criterion.md`](../design/Criterion.md) §5 for how the loader makes that deterministic
and observable rather than silent.

---

## 5. Where the source disagreed with the request

Each of these was verified live and resolved on the record.

### 5.1 ⚠ `financial_json_partitioned` is stale; `financial_json_latest` is used instead

The request specified `data_series.financial_json` for `arm.Financial_Series` and
`data_series.financial_json_partitioned` for `arm.Financial_SeriesData`. Measured:

| Relation | Max `load_date` | Max `post_date` | 30-day rows | Distinct UUIDs |
|---|---|---|---:|---:|
| `financial_json_partitioned` | **2024-10-07** | **2024-10-07** | **0** | — |
| `financial_json` | 2026-09-03 | 2026-09-03 | 165,859 | 163,004 |
| `financial_json_latest` | 2026-09-03 | 2026-09-03 | **46,032** | **46,032** |

Two independent problems with the specification as written:

1. **`financial_json_partitioned` has not been written for ~2 years.** A `DaysBack` window over it
   returns **zero rows**, forever.
2. **The two relations share no keys.** Of 50 sampled live `financial_json` UUIDs, **0** exist in
   `financial_json_partitioned`. `arm.Financial_SeriesData` would therefore have contained
   observations whose `FinancialJsonId` matched **no** row in `arm.Financial_Series` — the two tables
   could never be joined.

**Decision (user, 2026-09-03): both financial feeds read `data_series.financial_json_latest`.** It is
live, it holds exactly one row per `(series, post_date)`, and it eliminates `financial_json`'s 2,320
byte-identical duplicate UUIDs per 30-day window. Cost: 46,032 rows per window instead of 165,859 —
the omitted 119,827 are superseded republications of the same series/day.

A regression test (`Both_financial_feeds_read_the_same_source_relation`) pins this so the two feeds
cannot drift apart again.

### 5.2 ⚠ `misc.period` / `misc.unit` do not exist

The real names are **`misc.periods`** and **`misc.units`**, and their key columns are
**`period_uuid`** and **`unit_uuid`** — not `period_id` / `unit_id`. Corrected in the descriptors.

### 5.3 ⚠ `arm.Pipelines_Pointflows` had no source, and none exists

The request left this table's source table blank. **No pointflows-shaped relation exists anywhere in
the Criterion database** (`information_schema` search for `%flow%` returns nothing).

Every one of its columns is present in exactly one of two relations:

```
pipelines.nomination_points  →  MetadataId, EffGasDay, CycleId, CycleDesc, ScheduledQuantity
pipelines.metadata           →  PipelineName, LocName, CategoryShort, LocZone,
                                LocPurpDesc, StateName, StateAbb, CountryName
```

**Decision (user, 2026-09-03): derive it by joining the two.**

Two facts shape the implementation:

- **The join must be LEFT.** On 2026-09-02, **25 of 19,431** nomination rows reference a
  `metadata_id` with no row in `pipelines.metadata`. An INNER JOIN would silently drop those flows.
- **The natural key `(metadata_id, eff_gas_day, cycle_id)` is unique** — 19,431 rows = 19,431
  distinct triples on that day. This matters because the supplied PK includes the `IDENTITY` column
  `Id`, which a merge cannot match on; the proc merges on the natural key instead.

### 5.4 Target columns with no source column

Four, all left unwritten by the merge procs so a hand-set value survives every reload:

| Target column | Nature |
|---|---|
| `arm.Financial_Metadata.Enabled` | ARM curation flag |
| `arm.Pipelines_NominationPoint.IsLatest` | ARM curation flag |
| `arm.Pipelines_Metadata.MappingId` | local `IDENTITY` surrogate |
| `arm.Pipelines_Pointflows.Id` | local `IDENTITY` surrogate |

A fifth, `arm.Pipelines_Metadata.Point`, is **derived** in the merge proc from `Latitude`/`Longitude`
— which are **NULL for all 42,446 source rows today**, so `Point` lands NULL until Criterion
populates the coordinates.

---

## 6. Field-width headroom

Every mapped string column was measured against the live data. **Nothing is close to its target
width**; the largest ratios are `metadata_desc` and `series_desc` at 73 characters into
`VARCHAR(100)` and `VARCHAR(150)`.

Confirmed by execution: a full read of all nine feeds against production produced **0 truncations**.
The loader still truncates-and-reports rather than failing a work unit, because the source can widen
a column without notice.

---

## 7. How this document was verified

```
1. pg_attribute / pg_class / pg_constraint / pg_indexes  — every in-scope relation
2. Row counts, key-uniqueness, NULL rates, date ranges   — live queries
3. JSON shape / size / format sampling                   — 300+ rows
4. The loader's OWN generated SELECT, per feed, executed against production:
     all 9 feeds returned rows; every descriptor column matched by NAME and ORDER
5. The loader's OWN read path, per feed, executed against production:
     0 truncations · 0 dropped rows · 0 unparseable values · all CLR types correct
     FinancialSeriesData: 25 source rows → 46,064 target rows, 2,999 collapsed
```

Step 4 and step 5 are what make this document evidence about **the loader**, not merely about the
database.

**Not verified — no live target database exists:** the SQL in `sql/Criterion/` has been
parse-checked with ScriptDom but never deployed, and no row has been merged.
