# OilX CSV API — contract reference

Vendor: Energy Aspects (OilX product line)
Base: `https://api.energyaspects.com/oilx/v2`
Vendor docs: <https://developer.energyaspects.com/reference/get_csv>
Database: `OilX`  ·  Schema: `arm`  ·  LoaderId: `OilX`

Everything below was verified against the **live** API and live files on
**2026-10-01** unless marked otherwise. Where the vendor documentation and the
observed behaviour disagree, the observed behaviour is what this loader targets
and the disagreement is called out.

---

## 1. Endpoints

### 1.1 `GET /oilx/v2/csv/list` — the feed catalogue

```
GET /oilx/v2/csv/list?api_key=<key>
```

```json
{"data":["cargotracking","cargotrackingcruderevisions","floatingstoragevessels",
         "flows","globalbalance","oilfieldsdailyproduction","oilfieldsproduction",
         "regional_balance","supplydemand","terminals","terminalsweekly","usweekly"],
 "success":true}
```

Twelve feeds exist. **Eight are in scope** for this loader; the other four
(`cargotrackingcruderevisions`, `oilfieldsdailyproduction`, `terminalsweekly`,
`usweekly`) are deliberately not loaded — no target table was specified for them.

Note the catalogue returns names **lower-cased**, while the `files` parameter and
the S3 object names are **mixed-case** (`CargoTracking`, `Regional_Balance`). The
parameter is case-insensitive, so either spelling is accepted on the way in; the
file name that comes back is always the mixed-case form.

### 1.2 `GET /oilx/v2/csv/` — the manifest

```
GET /oilx/v2/csv/?api_key=<key>&day=2026-09-30&files=CargoTracking,SupplyDemand,…
```

| Parameter | Required | Notes |
|---|---|---|
| `api_key` | yes | Query parameter, **not** a header. See §5 — this is why URLs must never be logged. |
| `files`   | yes | Comma-separated feed names. Case-insensitive, whitespace-tolerant. |
| `day`     | one of | `YYYY-MM-DD`. All files **published on that calendar day**. |
| `range`   | one of | `YYYY-MM-DD,YYYY-MM-DD`. End may be omitted to mean "through today". |

`day` and `range` are **mutually exclusive**.

Response:

```json
{"data":[{"uploaded_at":"2026-09-30T03:40:23.462000+00:00",
          "url":"https://oilx-csvs.s3.amazonaws.com/CargoTracking.2026-09-30T03-39.csv?AWSAccessKeyId=…"}],
 "success":true}
```

`data[]` is sorted by file name then upload date ascending. Each entry carries
only `uploaded_at` and `url` — **the feed name is not a field**; it must be parsed
out of the S3 object name (see §3.1).

---

## 2. Behaviours that are silent if you get them wrong

### Behaviour 1 — a day publishes MANY files per feed, and the count has grown

`day=2026-09-30` with the eight in-scope feeds returns **17 files**, not 8:

| Feed | files on 2026-09-30 | upload times (UTC) |
|---|---|---|
| CargoTracking | 4 | 03:40, 09:23, 15:30, 21:11 |
| FloatingStorageVessels | 4 | 03:40, 09:23, 15:30, 21:11 |
| Flows | 4 | 03:40, 09:23, 15:30, 21:11 |
| GlobalBalance | 1 | 10:10 |
| OilFieldsProduction | 1 | 10:10 |
| Regional_Balance | 1 | 10:10 |
| SupplyDemand | 1 | 10:10 |
| Terminals | 1 | 10:10 |

**The count is not stable across history.** Same eight-feed request, by date:

| `day` | files returned |
|---|---|
| 2023-01-01 | 9 |
| 2024-01-01 | 8 |
| 2025-01-01 | 8 |
| 2026-01-01 | 8 |
| 2026-04-01 | 8 |
| 2026-07-01 | 14 |
| 2026-08-01 | 17 |
| 2026-09-30 | 17 |

So the intraday refresh is a **2026 change**: one file per feed per day until
roughly mid-2026, then two, then four. A loader that assumes "one file per feed
per day" silently loads only a fraction of a recent day, and one that hard-codes
four finds none on a 2024 backfill. The loader must take the file list as it
comes.

### Behaviour 2 — all of a day's snapshots carry the SAME in-file `RunDate`

Every row of every CSV carries a `RunDate` column, and for all four of a day's
CargoTracking snapshots that value is identical (`2026-09-30`). What differs is
`RunDateTime`, the exact publication instant:

| file | rows | `RunDateTime` |
|---|---|---|
| `CargoTracking.2026-09-30T03-39.csv` | 396,866 | `2026-09-30 03:00:01.237916` |
| `CargoTracking.2026-09-30T09-23.csv` | 396,893 | `2026-09-30 08:54:40.279852` |

Because the target primary key is `(RunDate, RowId)` and `RunDate` is read **from
the file**, the four snapshots of a day all land on the same keys and overwrite
one another. They are progressive refreshes, not disjoint parts — the 09:23 file
is the 03:39 file plus 27 rows.

**This loader merges all of a day's snapshots in ascending `uploaded_at` order**,
so the newest published value wins. That ordering is a correctness requirement,
not a preference: merging them in an arbitrary order leaves a row at whichever
snapshot happened to finish last. See `docs/design/OilX.md` §4.

### Behaviour 3 — presigned URLs expire in 5 hours

The vendor documents the download link as valid "within the next 5 hours", and the
URL carries `AWSAccessKeyId`, `Signature` and `x-amz-security-token`.

A full 31-day × 8-feed backfill moves roughly 6.5 GB and can easily run past five
hours. Therefore the loader **re-requests the manifest for each day at the moment
it processes that day** rather than taking URLs from one upfront `range=` call.
Enumerating with `range=` and downloading from those same URLs hours later is the
failure this avoids.

### Behaviour 4 — an empty day and a bad feed name are BOTH HTTP 422

This is the one that quietly corrupts a status matrix.

| request | status | body |
|---|---|---|
| valid key, valid feed, day with data | 200 | `{"data":[…],"success":true}` |
| valid key, valid feed, day with **no data** | **422** | `{"error":"No data for files 'globalbalance'","success":false}` |
| valid key, **unknown feed name** | **422** | `{"error":"Unavailable files 'notarealfeed'","success":false}` |
| **bad api key** | 401 | `{"error":"Authorization key is invalid","success":false}` |

A legitimately empty day does **not** answer `200` with an empty `data[]` — it
answers **422**. So 422 cannot be treated as a blanket error, or every gap in the
vendor's publication history fails a work unit. Nor can it be treated as a blanket
"no data", or a typo in `EnabledFeeds` reports clean, empty runs forever.

The two are told apart **only by the error message prefix**:

* `No data for files` → legitimate empty read → work unit succeeds with 0 rows.
* `Unavailable files` → malformed request → work unit FAILS loudly.

Anything else at 422, and any other status, is a failure. See
`docs/design/OilX.md` §6 for the full status matrix.

### Behaviour 5 — a partially-available day returns 200 and silently omits the missing feed

Requesting two feeds where only one has data for that day returns **200** with
only the available feed in `data[]`. The 422 of Behaviour 4 fires only when
**every** requested feed is missing.

So a multi-feed request cannot distinguish "this feed published nothing today"
from "this feed was never requested". This loader therefore issues **one manifest
request per (feed, day)**, so an empty feed is an unambiguous 422 against that
feed alone and is recorded against that feed's work unit. Batching all eight feeds
into one call would have made a missing feed invisible.

### Behaviour 6 — the CSVs contain quoted fields with embedded commas

3,447 of CargoTracking's 396,866 rows contain a quoted field, because country
names like `"Bonaire, Sint Eustatius and Saba"` carry a comma. Splitting on `,`
shifts every later column one place left and writes plausible-looking garbage
rather than failing — the TVP binds by position, so nothing raises. An RFC-4180
parser is mandatory.

### Behaviour 7 — each daily file is a FULL snapshot of all history

`GlobalBalance.2026-09-30` carries `ReferenceDate` values from 2010-01-01 onward.
Every feed behaves this way: a day's file is the vendor's complete current view,
not that day's increment. One `RunDate` therefore stores a complete copy of the
dataset — about **2.07 million rows across the eight feeds**:

| feed | rows in one day's file |
|---|---|
| Terminals | 786,798 |
| CargoTracking | 396,866 |
| SupplyDemand | 364,543 |
| OilFieldsProduction | 252,130 |
| Flows | 232,311 |
| Regional_Balance | 22,090 |
| FloatingStorageVessels | 6,072 |
| GlobalBalance | 4,663 |

A 31-day window is therefore ~64M rows and ~6.5 GB of CSV. This is why the loader
streams rather than materialising a file, and why `SettledAfterDays` matters so
much (§2, Behaviour 8).

### Behaviour 8 — a past day's file is immutable

A given day's published CSV never changes; the vendor revises by publishing a
**new** day, not by rewriting an old one. That makes the settled zone genuinely
settled, which is **not** true of most vendors in this repo (ICE, Genscape and
EvolutionMarkets all restate recent data and therefore ship `SettledAfterDays`
equal to `DaysBack`, i.e. all-hot).

This loader ships `SettledAfterDays = 1`: only today stays hot, because today's
file set is still growing (Behaviour 1). Every earlier day is loaded once and then
skipped without any download. With the repo's usual all-hot default this loader
would re-download ~6.5 GB on every run for data that provably cannot change.

---

## 3. File naming and feed attribution

### 3.1 The S3 object name is the only feed label

```
https://oilx-csvs.s3.amazonaws.com/CargoTracking.2026-09-30T03-39.csv?AWSAccessKeyId=…
```

Form: `<FeedName>.<yyyy-MM-dd>T<HH-mm>.csv`. The timestamp in the NAME is the
job's start minute and is **close to but not equal to** `uploaded_at`
(`03-39` vs `03:40:23`) or to the in-file `RunDateTime` (`03:00:01`). Three
near-but-unequal timestamps for one file; only `uploaded_at` is used for
ordering, and only the file name is used for feed attribution and for the
`FileName` provenance column.

`Regional_Balance` is the one feed whose name contains an underscore, and its
target table is `arm.RegionalBalance` — the mapping from file name to table is
explicit in the descriptors, never derived by string munging.

---

## 4. Per-feed CSV contracts

Headers below are **verbatim** from the live files of 2026-09-30. Columns the
target tables do not use are listed too, so a future request to add one does not
need another live pull.

Spelling traps, all real:

* Flows publishes **`GroupbyDateIndicator`** (lower-case `b`); the column is
  `GroupByDateIndicator`.
* CargoTracking publishes **`LoadQuantity(KT)`** and **`LoadQuantity(KBBL)`**
  with parentheses; the columns are `LoadQuantity_KT` / `LoadQuantity_KBBL`.
* CargoTracking's CSV order is `…KT, KBBL…` while the requested DDL lists
  `…KBBL, KT…`. Mapping is **by header name**, never by position.

### 4.1 CargoTracking → `arm.CargoTracking`

52 CSV columns; 32 are used.

```
IMO,VesselName,VesselClass,LoadDate,LoadArea,LoadCountry,LoadSubcountryArea,LoadPort,
LoadSTSIndicator,LoadSTSIMO,LoadQuantity(KT),LoadQuantity(KBBL),OriginCountry,
OriginCountryGroup,GradeName,APIGravity,SulphurContent,DischargeDate,DischargeArea,
DischargeCountry,DischargeSubcountryArea,DischargePort,DischargeSTSIndicator,
DischargeSTSIMO,DestinationCountry,DestinationCountryGroup,Charterer,Supplier,Buyer,
LastUpdateDate,LoadGeoAsset,DischargeGeoAsset,GradeSource,CargoType,CargoTypeSource,
SanctionEntities,LoadSTSID,DischargeSTSID,LoadGeoAssetSource,LoadPortSource,
LoadCountrySource,LoadAreaSource,DischargeGeoAssetSource,DischargePortSource,
DischargeCountrySource,DischargeAreaSource,VoyageID,FlowID,RunDate,RunDateTime,
IsExporter,IsImporter
```

Not loaded: `LoadGeoAsset`, `DischargeGeoAsset`, `GradeSource`, `CargoType`,
`CargoTypeSource`, `SanctionEntities`, `LoadSTSID`, `DischargeSTSID`, the eight
`*Source` provenance columns, `VoyageID`, `RunDateTime`, `IsExporter`,
`IsImporter`.

**`FlowID` is loaded** (added to the supplied DDL by request): a 64-character
sha256 the vendor assigns per row. Verified **unique across all 396,865 rows** of
`CargoTracking.2026-09-30T03-39.csv`, which makes it this feed's row identity.

`VoyageID` is **not** a row identity — 396,865 rows share only 681 distinct
values.

Notes: `LoadSTSIMO` / `DischargeSTSIMO` are `-1` when the leg is not an STS
transfer, and `LoadSTSIndicator` / `DischargeSTSIndicator` are `0`/`1`. Both are
stored as text per the supplied DDL. `SulphurContent` is `VARCHAR(MAX)` in the
DDL and is **kept as text** — the live values look numeric (`5.40`) but the DDL
is honoured verbatim.

### 4.2 FloatingStorageVessels → `arm.FloatingStorage`

```
IMO,VesselName,VesselClass,ReferenceDate,StartDate,EndDate,QuantityKiloBarrels,Area,
RunDate,OriginCountry,QuantityKiloTonnes,RunDateTime
```

Not loaded: `OriginCountry`, `QuantityKiloTonnes`, `RunDateTime`.

### 4.3 Flows → `arm.Flow`

```
OriginCountryName,DestinationCountryName,GroupbyDateIndicator,OriginSubCountry,
DestinationSubCountry,ReferenceDate,GradeName,QuantityKBD,QuantityKBBL,QuantityKT,
ApiGravity,SulphurContent,GradeCategory,RunDate,RunDateTime
```

Not loaded: `QuantityKT`, `RunDateTime`.

`GroupbyDateIndicator` takes values `Exports` / `Imports` — the same physical
cargo appears under both, so it is a key component.

### 4.4 GlobalBalance → `arm.GlobalBalance`

```
GroupName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,RunDate
```

All six loaded. No `RunDateTime` on this feed or any of §4.5–4.8.

### 4.5 Regional_Balance → `arm.RegionalBalance`

```
GroupName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,GeneralizedSource,RunDate
```

### 4.6 SupplyDemand → `arm.SupplyDemand`

```
CountryISOCode,CountryName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,
GeneralizedSource,RunDate
```

### 4.7 OilFieldsProduction → `arm.OilFieldProduction`

```
OilFieldName,PortName,CountryName,ReferenceDate,UnitMeasure,ObservedValue,RunDate
```

### 4.8 Terminals → `arm.Terminal`

```
TerminalName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,RunDate,CountryName,
IsForwardFilled
```

Not loaded: `IsForwardFilled`.

---

## 5. 🔒 The API key travels in the QUERY STRING

Unlike every header-authenticated loader in this repo (Genscape's `Gen-Api-Key`,
AGSI's `x-key`), OilX takes `api_key` as a **query parameter**. It is therefore
present in every request URL, which means:

* the named `HttpClient` has `.RemoveAllLoggers()` so the factory never logs a
  request line;
* the loader never logs a full URL — it logs feed, day and file name only, and
  `OilXHttp.Redact` strips `api_key=…` from anything that could still carry one
  (exception messages, `HttpRequestException.Message`);
* the key is `SEE_DB` in `appsettings.json` and resolves from
  `core.Param(LoaderName='OilX', ParamName='ApiKey')`.

The presigned S3 URLs carry `AWSAccessKeyId`, `Signature` and
`x-amz-security-token` and are likewise never logged.

---

## 6. Observed limits

* No documented or observed rate limit. The loader bounds itself with
  `MaxConcurrentWorkUnits` (default 4) and this is the only throttle.
* No pagination — a manifest response is complete.
* History reaches back at least to **2023-01-01** for all eight feeds; no earlier
  bound was probed.
* A future `day` answers 422 `No data for files …`, i.e. the Behaviour-4 empty
  case, not an error.
