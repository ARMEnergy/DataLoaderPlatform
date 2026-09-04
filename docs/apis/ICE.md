# ICE (Intercontinental Exchange) — Settlement & Index file downloads

**Source contract verified LIVE on 2026-09-01** against the real service with the
`arm_settles` account. Every header string, date format, delimiter, status code
and row count in this document was observed, not inferred. Where a fact was
measured on a specific trade date the date is given.

Loader design: `docs/design/ICE.md`.

---

## 1. Authentication

Two steps. There is no API key and no bearer token header.

### 1.1 Sign on

```
POST https://sso.theice.com/api/authenticateTfa
Content-Type: application/json

{"userId":"<user>","password":"<password>","appKey":"ICEDOWNLOADS"}
```

Response `200` with `SSO-Success: OK` and a JSON body. The value the loader needs
is **`result.data.token`** — note it is a sibling of `attributes` (which is an
empty object), not nested inside it. The same value is echoed as the
`iceSsoCookie` cookie:

```json
{"result":{"data":{
  "attributes":{},
  "roles":["ICEDOWNLOADS:CRUDE_INDEX","..."],
  "token":"exampleuser.ICEDOWNLOADS.2026_09_01.13_45_00_387.4T..EXAMPLETOKEN..0000000000",
  "user":"arm_settles","app":"ICEDOWNLOADS",
  "session":"EXAMPLESESSIONID0000000000000000"
}}}
```

The token embeds the issue **date and time** (`2026_09_01.13_45_00_387`), so it is
inherently short-lived. The loader must therefore treat token expiry as a normal
event, not an error — see §4.2.

### 1.2 Download

```
GET https://downloads.ice.com/<path>
Cookie: iceSsoCookie=<token>
```

The SSO `Set-Cookie` is scoped to `.sso.theice.com` and is **not** sent to
`downloads.ice.com` automatically (different registrable domain: `ice.com` vs
`theice.com`). The loader sets the `Cookie` header explicitly.

Without the cookie the download returns `302` to the login flow.

### 1.3 Roles observed on the `arm_settles` account

All roles required by the 18 feeds below are present:

```
CRUDE_INDEX                              FIXEDINCOME_SETTLEMENTS
ICEF_OPTIONS_GREEKS                      SETTLEMENT_REPORTS_CSV_ENVIRONMENTALS
SETTLEMENT_REPORTS_CSV_GAS               SETTLEMENT_REPORTS_CSV_NGL
SETTLEMENT_REPORTS_CSV_OIL               SETTLEMENT_REPORTS_CSV_POWER
```

---

## 2. ⚠ The status matrix — everything is HTTP 200

**This is the single most important fact about this source.** `downloads.ice.com`
never returns `404`, and never returns a `4xx`/`5xx` for a missing file or for
failed authentication. **All three outcomes below are `HTTP 200`** and are
distinguishable only by *content*.

| Outcome | HTTP | Body | Size | Detection |
|---|---|---|---|---|
| **Data** | 200 | pipe/CSV text, known header row | KB–MB | first line matches the feed's expected header |
| **Not available** (weekend, holiday, future date, feed predates the date) | 200 | HTML directory index | ~985 B | contains `Index of` **and** `No Files Available` |
| **Auth expired / invalid** | 200 | HTML SSO login page | ~33 KB | contains `ICE SSO Client` |

Verified 2026-09-01:

```
Sunday 2026_08_30        -> 200, 986 B, "<title >Index of ...</title>" + "No Files Available"
Future 2027_01_15        -> 200, 986 B, same
Nonexistent file name    -> 200, 982 B, same
Bogus iceSsoCookie       -> 200, 33220 B, "<title>ICE SSO Client</title>"
Valid request            -> 200, 127608 B, "TRADE DATE|HUB|PRODUCT|..."
```

A loader that trusts the status code will parse the login page as data, or record
a clean "0 rows" success for an authentication failure. Both are silent data loss.
`IceResponseClassifier` implements this table and is unit-tested against captured
copies of all three bodies.

**Legitimate empty is a fourth, distinct case**: a real file containing only its
header row. `ICE_Crude_Oil_Index_Trades_20260828.csv` is 178 bytes — header, zero
data rows — on a day the index window is closed. That is `Success` with 0 records,
**not** `NotAvailable`.

---

## 2a. Rate limit — 30 requests per minute (hard)

Measured live 2026-09-01. ICE states the limit in the **body** of its own `429`:

```
HTTP/1.1 429 Too Many Requests
Retry-After: 60
Server: cloudflare

You are limited to 30 requests per minute. You are being blocked due to Rate
Limiting Threshold. Please retry after 1 min. Contact: support@ice.com for
further information.
```

**It is a rate limit, not a concurrency limit.** Measurements:

| Test | Result |
|---|---|
| 10 concurrent requests | all `200` |
| 20 concurrent requests | all `200` |
| 40 concurrent requests | all `429` |
| **Sequential, concurrency 1, fresh window** | **exactly 30 × `200`, then `429` on request #31** |

The sequential run is the decisive one: at a concurrency of *one*, the 31st request
in the window still fails. The budget is counted per **request per minute**,
account-wide, and once tripped **every** request is blocked for 60 seconds no matter
how few are in flight. Raising concurrency does not help and lowering it does not
either — only pacing does.

Consequences for the loader:

* `IceRateLimiter` paces every request at `RequestsPerMinute` (default **25**, not
  30 — Cloudflare counts over a *sliding* window, so pacing exactly at the limit puts
  boundary requests on the wrong side of it under clock skew or jitter).
* `IceHttpPolicy` treats `429` as transient and honours `Retry-After: 60`. Backing
  off for the full 60 s is what actually clears the block; retrying sooner re-trips it
  and burns an attempt.
* The budget is **account-wide**. Anything else using the same ICE credential spends
  from the same 30/min, and the client-side limiter cannot see those requests — the
  `Retry-After` backoff is the safety net for that case.
* **Run duration is governed by this, not by bandwidth.** A cold run is
  18 feeds × 31 dates = 558 requests ≈ 22 minutes at 25/min. Warm runs are far
  shorter because the local file cache serves anything already downloaded.

---

## 3. Feed catalogue — 18 files into 12 tables

Date token is `yyyy_MM_dd` for every feed except the two Crude Index feeds, which
use `yyyyMMdd`.

| # | Path | Token | Target table |
|---|---|---|---|
| 1 | `Settlement_Reports_CSV/Environmentals/icecleared_physenv_{d}.dat` | `yyyy_MM_dd` | `arm.EnvFutures` |
| 2 | `Settlement_Reports_CSV/Environmentals/icecleared_physenvoptions_{d}.dat` | `yyyy_MM_dd` | `arm.EnvOptions` |
| 3 | `Settlement_Reports_CSV/Gas/ngxcleared_gas_{d}.dat` | `yyyy_MM_dd` | `arm.Futures` |
| 4 | `Settlement_Reports_CSV/Power/ngxcleared_power_{d}.dat` | `yyyy_MM_dd` | `arm.Futures` |
| 5 | `Settlement_Reports_CSV/Gas/icecleared_gas_{d}.dat` | `yyyy_MM_dd` | `arm.Futures` |
| 6 | `Settlement_Reports_CSV/NGL/icecleared_ngl_{d}.dat` | `yyyy_MM_dd` | `arm.Futures` |
| 7 | `Settlement_Reports_CSV/Oil/icecleared_oil_{d}.dat` | `yyyy_MM_dd` | `arm.Futures` |
| 8 | `Settlement_Reports_CSV/Oil/iceclearedoil_ca_{d}.dat` | `yyyy_MM_dd` | `arm.Futures` |
| 9 | `Crude_Index/ICE_Crude_Oil_Index_{d}.csv` | `yyyyMMdd` | `arm.ICE_Crude_Oil_Index` |
| 10 | `Crude_Index/ICE_Crude_Oil_Index_Trades_{d}.csv` | `yyyyMMdd` | `arm.ICE_Crude_Oil_Index_Trades` |
| 11 | `Settlement_Reports_CSV/Power/icecleared_power_{d}.dat` | `yyyy_MM_dd` | `arm.ICEClearedPowerFutures` |
| 12 | `Settlement_Reports_CSV/Power/icecleared_poweroptions_{d}.dat` | `yyyy_MM_dd` | `arm.ICEClearedPowerOptions` |
| 13 | `ICEF_options_greeks/ICEFCA_Options_{d}.dat` | `yyyy_MM_dd` | `arm.ICEFCA_Options` |
| 14 | `ICEF_options_greeks/ICEFUS_FinOptions_{d}.dat` | `yyyy_MM_dd` | `arm.ICEFUS_FinOptions` |
| 15 | `ICEF_options_greeks/ICEFUS_SoftOptions_{d}.dat` | `yyyy_MM_dd` | `arm.ICEFUS_SoftOptions` |
| 16 | `FixedIncome_Settlements/IFLL_Options_{d}.xlsx` | `yyyy_MM_dd` | `arm.IFLL_Options` |
| 17 | `Settlement_Reports_CSV/Gas/icecleared_gasoptions_{d}.dat` | `yyyy_MM_dd` | `arm.Options` |
| 18 | `Settlement_Reports_CSV/Oil/icecleared_oiloptions_{d}.dat` | `yyyy_MM_dd` | `arm.Options` |

Sizes observed for 2026-08-28 range from 178 B (feed 10) to 23.6 MB (feed 18);
the whole day is ~100 MB across all 18 feeds.

---

## 4. File formats

### 4.1 Settlement `.dat` (feeds 1–8, 11–15, 17–18)

Pipe-delimited (`|`), one header row, **LF** line endings, no BOM, plain ASCII,
trailing newline present. No quoting and no escaping was observed — a `|` never
appears inside a value.

**Futures shape** (11 columns) — feeds 1, 3, 4, 5, 6, 7, 8, 11:

```
TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID
8/28/2026|AB-NIT|NG Basis LD1 for NGX 7a Futures|9/1/2026|AEC|F||-1.95750|-0.01750|9/1/2026|451
```

**Options shape** (13 columns) — feeds 2, 12, 17, 18 — same, plus two columns:

```
...|PRODUCT_ID|OPTION_VOLATILITY|DELTA_FACTOR
```

**Greeks shape** (12 columns) — feeds 13, 14, 15:

```
TRADE DATE|CONTRACT|STRIP|EXPIRATION_DATE|STRIKE|PUT_CALL|SETTLEMENT_PRICE|VOLATILITY|DELTA|GAMMA|THETA|VEGA
8/28/2026|RS|10/1/2026|9/25/2026|635.0000|C|188.8000|34.7327|0.9970|0.0001|-4.6617|2.0592
```

> **Header order ≠ table order.** In the futures/options shapes the file order is
> `TRADE DATE, HUB, PRODUCT, STRIP, CONTRACT, …` while the target tables are
> `TradeDate, Contract, ContractType, Strip, ProductId, Hub, Product, …`.
> Mapping **must** be by header name. A positional load silently swaps
> `Hub`/`Contract` and corrupts the primary key.

Dates are `M/d/yyyy` (not zero-padded: `8/28/2026`, `9/1/2026`).

### 4.2 `STRIP` is not one type

| Feed | Example values | Column type |
|---|---|---|
| 1 `physenv` | `Aug19`, `Feb27`, `01 Sep 26` | `VARCHAR(50)` |
| 2 `physenvoptions` | `Apr27`, `BH26 Apr27 FH26` (a spread) | `VARCHAR(50)` |
| 11/12 `power`, `poweroptions` | `1/1/2027`, `8/27/2026` | `VARCHAR(50)` |
| 3–8 futures feeds | `9/1/2026` — **100 % date-parseable** | `DATE` |
| 17/18 gas/oil options | `8/1/2026` — **100 % date-parseable** | `DATE` |

Verified 2026-08-28: zero non-date `STRIP` values across all six `arm.Futures`
feeds and both `arm.Options` feeds. The `VARCHAR` strips genuinely need text —
`BH26 Apr27 FH26` is a three-leg spread and `01 Sep 26` is a daily contract.

### 4.3 Crude Index CSV (feeds 9, 10)

RFC 4180 comma-delimited, **quoted strings, unquoted numbers**, one header row.

```
"INDEX_DATE_RANGE","MKTID","PCC","INDEX_ID","INDEX_DATE","INDEX_PRICE","VOLUME","MKT_DESC","CREATION_TIME","LAST_UPDATE_TIME","BBL","DAILY_PRICE","DAILY_VOLUME","DAILY_BBL","NUM_OF_TRADES"
"08/26/2026-09/25/2026",273,"BGS",1416,"2026-08-28",-3.6,0,"ICE WCS CUS 1a - INDEX - Oct26","2026-08-28 03:00:00 PM","2026-08-28 03:00:00 PM",0,0,0,0,0
```

Note the three different date/time shapes in one row:
* `INDEX_DATE` → `yyyy-MM-dd`
* `CREATION_TIME` / `LAST_UPDATE_TIME` → `yyyy-MM-dd hh:mm:ss tt` (**12-hour with AM/PM**)
* `INDEX_DATE_RANGE` → free text `MM/dd/yyyy-MM/dd/yyyy`, stored as-is

Header order again differs from table order (`INDEX_DATE_RANGE` is first in the
file, sixth in the table).

**Feed 10 is a cumulative rolling window, not a daily snapshot.** Verified:

```
ICE_Crude_Oil_Index_Trades_20260814.csv  1505 rows, TRADE_DATE 2026-08-04 .. 2026-08-14
ICE_Crude_Oil_Index_Trades_20260821.csv  1562 rows, TRADE_DATE 2026-08-04 .. 2026-08-17
ICE_Crude_Oil_Index_Trades_20260825.csv  1562 rows, byte-identical to 20260821 (md5 5743a35f…)
ICE_Crude_Oil_Index_Trades_20260828.csv     0 rows (header only)
```

The file name is a *publication* date; each row carries its own `TRADE_DATE`. The
same deal is therefore republished across many files. Merging on the declared PK
is idempotent and correct, and is why this feed needs no special handling.

`DEAL_ID` exceeds `INT` (`297636150009` observed) — `BIGINT` is required.

### 4.4 IFLL Options XLSX (feed 16)

OOXML, single sheet `Sheet_1`, 16,420 rows including the header, shared-string
table of 378 entries. Same 12 columns as the greeks shape with **one difference**:

```
Header 5 is  PUT/CALL   (slash) — the .dat greeks feeds use  PUT_CALL  (underscore)
```

Two mechanical hazards:

1. **Sparse cells.** A row omits the `<c>` element entirely for a blank cell —
   row 2 is `A,B,C,D,F,G` with **no `E`**. Reading cells positionally shifts every
   value after the gap. The parser must map by the cell reference (`E2` → index 4).
2. **Mixed date formats in shared strings**, within a single row:
   `TRADE DATE` = `08/28/2026`, `STRIP` = `2026-09-01`, `EXPIRATION_DATE` = `09/18/2026`.

Magic bytes are `PK\x03\x04`; an HTML sentinel (§2) is detected before any unzip.

---

## 5. Primary-key findings (all measured on 2026-08-28)

### 5.1 `arm.Futures` — the declared PK is **not unique**

The six feeds merge into one table, and ICE **reuses contract codes across
markets**:

```
8/28/2026|OLD|F|1/1/2027  ngxcleared_power  Ontario - Essa DA / NGX Fin Extended Peak Futures  ProductId 30948
8/28/2026|OLD|F|1/1/2027  icecleared_oil    NYH LD / Heating Oil Futures                      ProductId 26755
```

| Measure | Value |
|---|---|
| Rows across the 6 files | 116,116 |
| Distinct `(TradeDate, Contract, ContractType, Strip)` | 111,809 |
| Colliding PKs | 4,055 |
| …of which byte-identical republication (harmless) | 3,982 |
| …of which **genuine conflicts (data loss)** | **73** |
| Ambiguous contract codes | `OLD` (41), `ZAF` (16), `YQI` (16) |
| Distinct `(PK + ProductId)` | 111,882 |
| Conflicts after adding `ProductId` | **0** |
| Rows with blank `PRODUCT_ID` | **0** |

**Resolution (approved 2026-09-01):** `ProductId` joins the PK and becomes
`NOT NULL`. This is the only deviation from the supplied DDL.

The 3,982 harmless collisions are ICE republishing the same contract in more than
one category file (LNG futures appear in `icecleared_gas`, `icecleared_ngl` **and**
`icecleared_oil` with identical hub, product, price and product id).

### 5.2 Every other table's declared PK holds

| Table | Rows | Duplicate PKs |
|---|---|---|
| `arm.EnvFutures` | 1,494 | 0 |
| `arm.EnvOptions` | 11,856 | 0 |
| `arm.Options` (2 feeds) | 279,624 | 0 conflicting payloads |
| `arm.ICEClearedPowerFutures` | 49,788 | 0 |
| `arm.ICEClearedPowerOptions` | 164,816 | 0 |
| `arm.ICEFCA_Options` | 1,546 | 0 |
| `arm.ICEFUS_FinOptions` | 2,172 | 0 |
| `arm.ICEFUS_SoftOptions` | 25,900 | 0 |
| `arm.IFLL_Options` | 15,396 | 0 |

### 5.3 `arm.ICE_Crude_Oil_Index_Trades` ships exact duplicate rows

Two fully byte-identical rows per file (same `DEAL_ID` emitted twice):

```
"2026-08-10","AWX","BFP","WCS","Hardisty - Husky","Sep26",1377,82821932,... (x2)
"2026-08-11","AT1","BFL","CAL","Edmonton - Central Ab","Sep26",1375,82823126,... (x2)
```

`MERGE` raises *"attempted to UPDATE or DELETE the same row more than once"* on a
duplicated source key, so every merge proc de-duplicates with
`ROW_NUMBER() OVER (PARTITION BY <pk>)`. No data is lost — the duplicates are
identical.

### 5.4 Options files contain futures rows with no strike

Every options/greeks feed carries rows for the **underlying future**, marked
`CONTRACT TYPE`/`PUT_CALL` = `F` (or `D`), with an **empty `STRIKE`**. `Strike` is
a `NOT NULL` PK component in all six options tables, so these rows cannot be
stored and are **dropped, counted and logged**:

| Feed | Total rows | Dropped (blank strike) | % |
|---|---|---|---|
| `icecleared_physenvoptions` | 12,215 | 359 | 2.9 % |
| `icecleared_gasoptions` | 87,971 | 2,669 | 3.0 % |
| `icecleared_oiloptions` | 198,480 | 4,158 | 2.1 % |
| `icecleared_poweroptions` | 176,067 | 11,251 | 6.4 % |
| `ICEFCA_Options` | 1,566 | 20 | 1.3 % |
| `ICEFUS_FinOptions` | 2,932 | 760 | 25.9 % |
| `ICEFUS_SoftOptions` | 26,302 | 402 | 1.5 % |
| `IFLL_Options` (xlsx) | 16,419 | 1,023 | 6.2 % |

`ICEFUS_FinOptions` drops a quarter of its rows. That is expected — the file is
mostly interest-rate futures — but it is exactly the sort of number that looks
like a bug later, so `arm.usp_ValidateLoad` reports the ratio rather than leaving
it invisible.

---

## 6. Column widths — all declared widths are sufficient

Longest values observed 2026-08-28 against the declared types:

| Column | Declared | Longest observed |
|---|---|---|
| `Contract` | `VARCHAR(10)` | 3 |
| `Hub` | `VARCHAR(100)` | 72 |
| `Product` | `VARCHAR(100)` | 66 |
| `Strip` (varchar feeds) | `VARCHAR(50)` | 13 |
| `CONTRACT` (greeks) | `VARCHAR(50)` | 3 |
| `STRIP` (greeks) | `VARCHAR(50)` | 10 |
| `INDEX_DATE_RANGE` | `VARCHAR(50)` | 21 |

No truncation risk at present. `arm.usp_ValidateLoad` reports any value within
90 % of its column width so that a widening feed surfaces before it errors.

---

## 7. Open items

1. **Never deployed.** The SQL is parse-checked with ScriptDom only. No row has
   been written to a real database. Do not report data validation as passed.
2. Token lifetime is not documented by ICE. The loader re-authenticates on
   detecting the login sentinel rather than on a timer, so the exact TTL does not
   matter operationally.
3. Feeds 9 and 10 were sampled on five dates; the cumulative-window behaviour of
   feed 10 is inferred from those five and could change at a month boundary.
4. Only `2026-08-28` (plus 2019-05-31 and five Crude Index dates) was examined in
   depth. Header stability across years is assumed, and is guarded by the
   header-match check in §2 — a changed header fails the file loudly rather than
   loading it shifted.
