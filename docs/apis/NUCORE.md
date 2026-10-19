# NUCORE Integration API

ARM's own risk system (NUCORE), read through the `NUCORE.IntegrationService` gateway at
`https://prodarmapigateway.azurewebsites.net`.

Everything in this document was verified against the live production API on **2026-10-16**
unless marked otherwise.

---

## 1. Discovery, auth and the spec

The gateway publishes an **OpenAPI document**, and it is the authority on parameter names.
It is **not** at any of the usual paths (`/swagger/v1/swagger.json` is 404); the URL is
listed inside `/swagger/index.js`:

```
https://prodarmapigateway.azurewebsites.net/swagger/docs/v1/Integration
```

A copy is checked in at `docs/apis/NUCORE.openapi.json`, alongside
`docs/apis/NUCORE.observed-profile.json` — a profile of real full-day responses that is the
evidence behind every `VARCHAR` size and every `DATETIME2`-vs-`DATETIMEOFFSET` choice in
`sql/NUCORE`. Both are inputs to `scripts/Generate-NucoreSchema.ps1`.

**Auth** is a single request header, `X-Api-Key`. Missing, empty or wrong all return a clean
`401`. 🔒 The key is never in source, config or a commit: `appsettings.json` carries the
`SEE_DB` sentinel and the real value is resolved at run time from
`core.Param(LoaderName='NUCORE', ParamName='ApiKey')`, or from the environment override
`DATALOADER_Loaders__NUCORE__ApiKey`.

**Response envelope** — identical across every endpoint:

```json
{ "success": true, "recordsReturned": 300, "totalRecords": 9378,
  "pageSize": 300, "page": 1, "totalPages": 32, "data": [ ... ] }
```

`success` has been `true` on every observed response, including empty ones, but the loader
reads it rather than assuming it.

---

## 2. The five endpoints

| Endpoint | Date parameter | Rows / day | Columns |
|---|---|---:|---:|
| `/Integration/api/risk/VarResults/Get` | `VarDate` | 9,378 | 18 |
| `/Integration/api/risk/PositionSummary/Get` | `SystemDate` | 13,555 | 118 |
| `/Integration/api/risk/PnlExplain/Get` | **`CurDate`** | 11,364 | 120 |
| `/Integration/api/risk/PositionDetails/Get` | **none** | 2,468,453 *(all history)* | 165 |
| `/Integration/api/risk/Volatilities/Get` | `SystemDate` | 147,908 | 15 |

Every endpoint also accepts `Page`, `PageSize` and `SortFields`. Several accept extra
filters the loader does not use (`TradeStatus`, `Trader`, `ProductSpec`, `InstrumentType`).

---

## 3. Behaviours that are silent if you get them wrong

### 3.1 `PnlExplain` ignores `systemDate`; its parameter is `CurDate`

Asked with `systemDate`, the endpoint returns **HTTP 200** and the **latest** run — for
every date. Verified: `systemDate=2026-10-14`, `=2025-10-15` and `=2024-01-10` all came back
as `curDate 2026-10-15`, 11,376 rows. Nothing in the response says the parameter was
ignored; the only tell is an identical `totalRecords` across unrelated dates.

With the correct `CurDate=2026-10-14` the same day returns **11,364** rows — a different
figure, which is the proof the parameter is now doing something.

The vendor's own spec confirms it: `PnlExplain`'s only date parameter is `CurDate`.

### 3.2 `PositionDetails` has no date parameter at all

The spec lists exactly `TradeMonthFrom`, `TradeMonthTo`, `TradeStatus`, `Page`, `PageSize`,
`SortFields`. Every date-ish name tried — `systemDate`, `positionDate`, `curDate`, `date`,
`asOfDate`, `asofDate`, `valuationDate`, `sysDate`, `tradeDate`, `priorDate` — was accepted
and **ignored**, each returning the same 2,468,453 rows.

One unfiltered call therefore returns the **entire history**: 2,468,453 rows spanning
**392 system dates (2025-03-27 … 2026-10-16)** and roughly **10 GB** of JSON. Rows are
ordered by trade, not by date — the first 100,000 rows alone span the whole range.

**`pageSize=500000` answers HTTP 504** after four minutes. `pageSize=25000` returns in ~39 s
(104 MB); `pageSize=100000` in ~133 s (405 MB).

**`TradeMonthFrom`/`TradeMonthTo` is the only partition the vendor offers, and it is
lossless.** The yearly census sums to exactly the unfiltered total:

| Trade-month year | Rows |
|---|---:|
| 2025 | 697,098 |
| 2026 | 946,664 |
| 2027 | 690,128 |
| 2028 | 119,952 |
| 2029 | 7,231 |
| 2031 | 7,380 |
| **Total** | **2,468,453** ✓ |

### 3.3 Omitting the date parameter returns 200 with the LATEST day

Not an error — the newest day's data, under whatever date you thought you asked for. So a
dropped or misspelled parameter is indistinguishable from success by status code alone.

Every response echoes its own date column, so **the loader asserts the echoed date equals
the requested one** and fails the unit otherwise. This is
`NucoreSourceReader.EchoedDateMatches`, and it is the single most important check in the
loader. A live negative control confirms it fires.

### 3.4 A legitimate empty read and a malformed request look nothing alike — and neither is loud

| Condition | Response |
|---|---|
| Weekend / holiday / pre-history date | `200`, `totalRecords: 0` — **legitimate empty read** |
| Future date | `200`, `totalRecords: 0` |
| Date parameter omitted | `200`, **latest day's data** |
| Date in the wrong format (`10/14/2026`) | `200`, leniently parsed |
| **Garbage date string** | **`500`**, empty body |
| Unknown `SortFields` value | `400`, `Invalid sort field: <name>` |
| Missing / wrong / empty `X-Api-Key` | `401` |
| `pageSize` far too large | `504` |

⚠ **The `500` is deterministic, not transient.** It is what a malformed request gets. Polly's
stock `HandleTransientHttpError` treats every 5xx as retryable, which would burn the whole
retry budget before failing anyway and bury the real cause — so `NucoreHttp.IsTransientStatus`
retries only `408`, `429`, `502`, `503`, `504`.

### 3.5 `id` and `guid` are always zero

On every row of every endpoint, `id` is `0` and `guid` is
`00000000-0000-0000-0000-000000000000`. `recordState` is constant `0` and `entityName` is
just the DTO class name. **None of these is usable as a key** — a key of `(date, guid)`
collapses a 9,378-row day to one row.

---

## 4. Paging

`Page` is 1-based. A page past the end returns **`200` with `recordsReturned: 0`**, not an
error, so **an empty or short page is the stop condition**.

`SortFields` takes a comma-separated list, with a `-` prefix for descending. An unknown field
returns `400` by name.

### ⚠ 4.1 Paging without a sort is NOT stable (verified 2026-10-18)

This is the most dangerous behaviour in the API, because half of it is invisible.

Fetching one `Volatilities` day (147,908 rows) at `pageSize=20000` **without** `SortFields`:

| | |
|---|---|
| rows on page 1 | 20,000 |
| rows on page 2 | 20,000 |
| **keys appearing on BOTH pages** | **111** |

With `SortFields` set, the same two pages overlap by **zero**.

111 rows returned twice also means **111 rows were never returned at all**. The duplicates
are loud — they violate a primary key — but the omissions are completely silent. Measured
against the live API, a loader that paged unsorted left `Volatilities` short by ~2.6% per
day (2026-10-14: 144,107 stored vs 147,908 available).

**Every request must therefore carry `SortFields`**, not only the paged ones. The loader
sends the most discriminating column list each endpoint offers (`NucoreEndpoint.SortFields`),
and additionally reconciles the rows it read against the envelope's own `totalRecords`, so a
residual shortfall fails the unit instead of being stored.

Only endpoints needing more than one page are exposed: at `pageSize=20000` that is
`Volatilities` (8 pages/day) and `PositionDetails`. `VarResults` (9,378/day) and
`PositionSummary` (13,555/day) are single-page and were verified complete.

### 4.2 Descending sort and the watermark

**`SortFields=-systemDate` works on `PositionDetails`**, and that is what makes an endpoint
with no date filter incremental: the loader pages a trade month newest-first and stops below
the newest date already stored (`dbo.usp_GetPositionDetailsWatermark`). Without it, every run
would re-download all of history.

The loader sends `-systemDate,tradeId,detailId,tradeMonth`. The trailing columns make the
order as close to total as the payload allows — though see §5, which shows that no column
list makes it genuinely total for this endpoint.

---

## 5. Natural keys — and the three endpoints that have none

⚠ **Read this section before giving any NUCORE table a primary key.** Three of the five do
not have one, and the first attempt at this loader gave keys to four of them on the strength
of a single sample each. One of those keys failed in production.

### How the keys were (re-)verified

A uniqueness check on this vendor is only meaningful if it is:

1. **over a complete population** — a whole day, or a whole trade month. The original
   `PositionDetails` key was "verified unique" over 100,000 rows that were the first page of
   a **trade-ordered** response and covered just **130 distinct trades**. Over five complete
   trade months (443,477 rows) the same key has 45,151 duplicates.
2. **compared the way SQL Server will compare** — as converted values, not raw JSON text.
   `7.5` and `7.50` are different strings and the same `DECIMAL`.
3. **repeated across several periods** — `PnlExplain`'s key is unique on five of the six days
   sampled and not on the sixth.

### The two keys that survived

| Table | Key | Verified over |
|---|---|---|
| `VarResults` | `VarDate, VarBucket, LegalEntity, ConfidenceLevelId, HoldingPeriod, TradeBook, TradeMonth, ProductSpec` | 7 complete days, 2025-10 → 2026-10 |
| `Volatilities` | `SystemDate, ProductSpec, InstrumentType, StartTradeMonth, EndTradeMonth, PutCallFlag, StrikeType, StrikePrice` | 5 complete days, 712,004 rows |

`ProductGroup` is deliberately **not** in `VarResults`' key — the key is already unique
without it, and it is a 100-character space-padded column that would cost 400 bytes of index
for nothing.

### `PositionSummary` — no key

A full day of 13,555 rows. A 30-column candidate — every dimension the payload carries
**plus** `mktPrice` and `contractPrice` — still left **6 collisions**:

```
key = 2026-10-14 | tradeId 1376 | 202508 | HB_NORTH_5X16_FIN
  totalTradeQuantity   -336          |  -1344
  contractPrice         139.75       |   139.7
  totalRealizedAmount    33918.1875  |   135605.55
```

### `PnlExplain` — no trustworthy key

`(CurDate, TradeId, DetailId, TradeMonth, ProductSpec)` is unique on 2026-10-15, 2026-09-30,
2026-06-16, 2026-03-10 and 2025-12-19 — and **not** on 2025-10-15, which carries **19
byte-identical copies** of one row (1,712 distinct keys for 1,730 rows). On a first load all
19 take the `NOT MATCHED` branch and the INSERT violates the primary key.

Those duplicates are exact, so de-duplicating would be lossless *today*. That was observed on
one day, and betting on it is precisely the mistake that caused the `PositionDetails` outage.

### `PositionDetails` — no key exists, and none can be built

Over five complete trade months (443,477 rows):

| Candidate key | Duplicates |
|---|---:|
| `SystemDate, TradeId, DetailId, TradeMonth` *(the original)* | 45,151 |
| … `+ SpecId` | 2,189 |
| … `+ PriceType, SettlementIndex` | 129 |
| … `+ ProductId` | 129 |

And the 129 survivors are **not** exact duplicates. One group holds **17 distinct settlement
rows** for the same trade, detail line, month, spec and price leg, at 17 different contract
prices:

```
key = 2025-12-19 | 5455 | 7804 | 202509 | 103 | Float | ERCOT RT LMP-HB_NORTH-15 Minute
  contractPrice       0 | -2.6 | -1.32 | -0.32 | ... | 11.41 | 10.78 | 0.09 | ... | 1.61
  realizedAmount      0 |  2.6 |  1.32 |  0.32 | ... |     0 |     0 | -0.09 | ... | -1.61
  units              64 |    8 |     8 |     8 | ... |     8 |     8 |     8 | ... |    8
```

De-duplicating these would destroy real realized P&L.

### What the loader does instead

All three are **replaced**, not merged — the day (or the `(TradeMonth, SystemDate)` scope) is
deleted and re-inserted wholesale, which is what a snapshot actually means and needs no key
at all. See `docs/design/NUCORE.md` §5.

---

## 6. Data shape notes

- **Padded strings.** Fixed-width `CHAR` columns arrive space-padded — `VarResults.productGroup`
  is padded to 100 characters. All strings are trimmed on the way in, uniformly, so both
  sides of any later comparison agree.
- **Bare business dates.** Most dates are `2026-10-14T00:00:00` with no zone and are stored
  as `DATETIME2(7)` with `DateTimeKind.Unspecified`. Parsing them as local-or-UTC and
  converting would move them across a day boundary — and they are primary key columns.
- **Three offset-bearing stamps.** Only `createdAt`, `modifiedAt` and `originalModifiedAt`
  carry an offset (`2026-10-14T13:29:54.623437+00:00`); they are `DATETIMEOFFSET(7)`.
  `originalModifiedAt` is usually the sentinel `0001-01-01T00:00:00+00:00`.
- **Numbers are `double` in the spec, `DECIMAL(28,12)` in the database.** These are money and
  risk values that get `SUM`med downstream, where binary floating point drifts. The widest
  scale in live data is **10** (the option greeks: `priorDelta -0.9094652105`,
  `gamma 0.4483348049`); the largest magnitude is ~3.57e6. `(28,12)` keeps every observed
  digit with 16 integer digits of headroom. Values are rounded in C# before they reach the
  server, so an over-precise value is a visible decision rather than a silent truncation.
- **Business days only.** A 14-day census found data on every weekday and `totalRecords: 0`
  on every Saturday and Sunday. Holidays behave the same way but are not predictable, so the
  loader skips weekends by configuration and treats everything else as a legitimate empty
  read.

---

## 7. Live verification (2026-10-16)

The real `NucoreSourceReader` was run against the live API for every endpoint, with a
counting sink in place of SQL:

| Endpoint | Unit | Rows read | Dropped | Pages | Elapsed |
|---|---|---:|---:|---:|---|
| VarResults | 2026-10-14 | 9,378 | 0 | 1 | 1.9 s |
| PositionSummary | 2026-10-14 | 13,555 | 0 | 1 | 12.2 s |
| PnlExplain | 2026-10-14 | 11,364 | 0 | 1 | 3.9 s |
| PositionDetails | tradeMonth 202601 | 43,472 | 0 | 3 | 92.4 s |
| Volatilities | 2026-10-14 | 147,908 | 0 | 8 | 12.6 s |
| **Total** | | **225,677** | **0** | | |

All 436 columns converted; every row produced a full value array. A negative control with a
deliberately wrong parameter name confirmed the date-echo guard fires rather than storing the
latest day.

**The SQL has never been deployed** — no `NUCORE` database exists yet, so nothing has been
merged and no data-quality validation against a loaded database has been done.

---

## 8. See also

- `docs/design/NUCORE.md` — the loader's design of record
- `docs/apis/NUCORE.openapi.json` — the vendor's own spec
- `docs/apis/NUCORE.observed-profile.json` — observed column profile
- `scripts/Generate-NucoreSchema.ps1` — generates the descriptors and all four `.sql` files
