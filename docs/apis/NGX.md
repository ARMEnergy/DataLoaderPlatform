# ICE NGX Clearing — XML web services

Source contract for the `NGX` loader. **Verified live on 2026-09-18** with the
`armenergyapi` web-service account; every claim below was measured against the real
endpoints, and the field mappings were additionally cross-checked against the incumbent
`dbo.IndexPrice` / `dbo.StripTradingSummary` rows for the same keys.

Host: `https://ngxclearing.ice.com/ngxcs`

---

## 1. The two endpoints this loader reads

| Document | Target table | Shape |
|---|---|---|
| `GET /ngxcs/indexPrice.xml` | `arm.IndexPrice` | Paged list, `<indexPriceSummary>` records |
| `GET /ngxcs/stripTradingSummaryXml.xml` | `arm.StripTradingSummary` | Flat list, `<stripTradingSummary>` records, **no pagination envelope** |

A third document, `GET /ngxcs/priceIndex.xml`, returns the exchange's whole index
catalogue (1,071 entries — 826 Power, 209 Gas, 36 Environmental). Despite the `.xml`
extension **it returns JSON.** The loader does not use it: the index universe comes from
`dbo.[Index]` instead (see §4).

---

## 2. Authentication — HTTP Basic, *not* the documented bearer token

This is the single most surprising thing about the endpoint and the one most likely to
cost someone a day.

ICE NGX publishes a modern REST API at `/ngxcs/api/v2` with an OpenAPI document at
`/ngxcs/v3/api-docs` and a documented token endpoint:

```
POST /ngxcs/api/v2/authentication/token
{"username":"…","password":"…"}
-> {"access_token":"armenergyapi.CSEXT.2026_09_18.…","token_type":"Bearer"}
```

That token works — **for `/api/v2` paths only.** The endpoint's own description says so:
*"The returned token is presented as 'Authorization: Bearer <token>' on subsequent
/api/v2 requests."*

The `.xml` documents sit **outside** that prefix, behind the legacy servlet session
filter. The bearer token is ignored there entirely. Fifteen placements were tried —
`Authorization: Bearer`, seven cookie names (`ICE_SSO`, `ICESSO`, `ssoToken`, …), six
query parameters (`ssoToken`, `ticket`, `access_token`, …) and five custom headers — and
**every one returned `302`** to
`https://sso.ice.com/appUserLogin?…&loginApp=CSEXT`.

What works is plain **HTTP Basic** with the same credentials:

```bash
curl -u 'armenergyapi:…' \
  'https://ngxclearing.ice.com/ngxcs/indexPrice.xml?indexId=350&effectiveStart=1-September-2026&effectiveEnd=18-September-2026&includeProjected=true'
# 200, application/xml
```

### Consequences the loader depends on

* **Redirects must not be followed.** `AllowAutoRedirect = false`. The SSO target
  answers `200` with 33 KB of HTML; following it turns an authentication failure into a
  silent "zero records" on every work unit, forever. The loader treats any `3xx` as a
  loud credential error.
* **Do not carry cookies.** Basic auth is evaluated per request. Re-sending the
  `JSESSIONID` the server issues actually *breaks* the call — a cookie-jar session
  without the Basic header returned `302` on all 12 follow-up requests tested.
* A `403` is **not** an auth failure — see §3.

### Why the v2 REST API was rejected

`/api/v2/index-prices` and `/api/v2/strip-trading-summary` exist and accept the bearer
token, but they are **reduced projections**. `IndexPriceResponse` carries 9 fields
against the 23 columns of `arm.IndexPrice`: no `sourceDataDelivery*`, no `duration`, no
`quantityTraded` unit/contractUnit/totalAmount, no `lastUpdateDate`, no record `id`.
The CSV export adds some of those back but **drops `indexId`**, which is a key column.
`/api/v2/strip-trading-summary/export?format=xml` *is* byte-identical to the legacy
strip document, but there is no XML export for index prices at all
(`400 Unsupported format 'XML' (expected: xls, csv)`).

Only the legacy `.xml` documents carry the full field set the target tables were
designed around, so the loader uses them for both feeds.

---

## 3. `403` means **entitlement**, and it fails the whole request

The account is entitled to **194 of the 1,071** published indices. Requesting one it is
not entitled to returns `403 Access Denied` — an HTML page that, notably, is *logged in*
("Welcome armenergyapi"), which is why it must not be mistaken for an auth failure.

**The failure is all-or-nothing.** A batch of ten ids where nine are entitled and one is
not returns `403` for the entire request — the nine are lost too:

```
indexId=1..10   -> 200, 18 records
indexId=1..20   -> 403                (11..20 are unentitled)
indexId=85      -> 403                (a CrudeIndexPrice id)
indexId=1,2,3,85 -> 403               (one bad id poisons three good ones)
```

The loader's reader recovers by narrowing a refused batch to single-id requests and
skipping only the ids genuinely refused. Verified live: a `1,2,3,85` batch yields 37
rows from 1–3 and one warning naming 85.

### The 10-id ceiling

Separately, **at most 10 `indexId` parameters per request.** Eleven returns `403`
regardless of which ids they are. Measured, not documented:

| ids | result |
|---|---|
| 5 | 200 |
| 10 | 200 |
| 11 | 403 |
| 12 (ids 300–311) | 403 |
| 5 ids + 400 chars of padding | **200** |

The padding case rules out URL length; the high-id case rules out the id values. It is
the parameter count.

---

## 4. Which indices to request

`dbo.[Index]` in the NGX database holds 219 rows in two `IndexType` families:

| `IndexType` | rows | endpoint |
|---|---|---|
| `IndexPrice` | 47 | `indexPrice.xml` — **this loader** |
| `CrudeIndexPrice` | 172 | a different endpoint (not built) |

All 47 `IndexPrice` ids were verified entitled. **No** `CrudeIndexPrice` id is entitled
on `indexPrice.xml` (85–120 all `403`). Since one unentitled id fails its whole batch,
filtering to `IndexType = 'IndexPrice'` is required for correctness, not just tidiness.

The incumbent `dbo.IndexPrice` corroborates this: it holds exactly those 47 ids (plus
one retired id, 351, absent from `dbo.[Index]`), and ~39 of them return data on any
given day.

---

## 5. `indexPrice.xml`

### Request

```
GET /ngxcs/indexPrice.xml
  ?effectiveStart=1-June-2026        # d-MMMM-yyyy, INCLUSIVE
  &effectiveEnd=1-March-2027         # INCLUSIVE
  &includeProjected=true
  &pageSize=20000
  &page=2                            # optional
  &indexId=1&indexId=2&…             # 1..10 of them
```

Dates use the vendor's `d-MMMM-yyyy` spelling with an English month name — format it
invariantly or a non-English server locale will send an unparseable month.

### Pagination — three traps in one envelope

```xml
<indexPriceList xmlns="http://www.ngx.com/Clearing">
  <listSize>50</listSize>
  <truncated>true</truncated>
  <pageSize>50</pageSize>
  <pageNumber>1</pageNumber>
  <fullListSize>1237</fullListSize>
  <indexPrices> … </indexPrices>
  <indexPriceAggregates> … </indexPriceAggregates>
</indexPriceList>
```

1. **The default page is 50 rows.** A caller that omits `pageSize` gets 50 of 1,237 with
   a `200` status and a well-formed body. The only signal is `<truncated>`.
2. **`pageNumber` is silently ignored.** `pageNumber=2` returns page **1** again. The
   parameter that works is **`page`**. `size` is ignored too (`pageSize` is the one).
3. **`pageSize` caps at 20,000** server-side — ask for 100,000 and the response reports
   `<pageSize>20000</pageSize>`.

Measured: ids 1–10 over 2026-06-01…2027-03-01 → `fullListSize` 1237; at `pageSize=20000`
one request returns all 1,237 with `truncated=false`.

### Record

```xml
<indexPriceSummary>
  <id>3086005</id>
  <uri>/ngxcs/indexPrice/3086005.xml</uri>
  <index>
    <id>350</id>
    <name>ICE NGX AB-NIT - TCPL-Empress Transport Day Ahead Index</name>
    <uri>/ngxcs/priceIndex/350.xml</uri>
  </index>
  <priceEffectiveStart>2026-09-18</priceEffectiveStart>
  <priceEffectiveEnd>2026-09-18</priceEffectiveEnd>
  <sourceDataDeliveryStart>2026-09-18</sourceDataDeliveryStart>
  <sourceDataDeliveryEnd>2026-09-18</sourceDataDeliveryEnd>
  <price><amount>-0.0046</amount><currency>CAD</currency></price>
  <duration>1</duration>
  <quantityTraded>                          <!-- OPTIONAL -->
    <amount>313,100</amount>                <!-- THOUSANDS SEPARATOR -->
    <unit>GJ</unit>
    <contractUnit>Day</contractUnit>
    <totalAmount>313,100</totalAmount>
  </quantityTraded>
  <numberOfTrades>27</numberOfTrades>       <!-- OPTIONAL, co-occurs with quantityTraded -->
  <settlementState>Settled</settlementState>
  <lastUpdateDate>2026-09-18T02:15:41-06:00</lastUpdateDate>
</indexPriceSummary>
```

| Element | Column | Note |
|---|---|---|
| — | `ExecutionDate` | **Loader-supplied**: the run's US-Central date |
| `index/id` | `IndexId` | |
| `priceEffectiveStart` / `End` | `PriceEffectiveStart` / `End` | |
| `sourceDataDeliveryStart` / `End` | `SourceDataDeliveryStart` / `End` | |
| — | `CommodityType` | **Loader-supplied constant** `'Natural Gas'` — no element exists |
| `id` | `Id` | the record id, not the index id |
| `index/name` | `IndexName` | |
| `price/amount`, `price/currency` | `PriceAmount`, `PriceCurrency` | |
| `duration` | `Duration` | delivery days, vendor-counted |
| `quantityTraded/*` | `TradedAmount`, `TradedUnit`, `TradedContractUnit`, `TradedTotalAmount` | all NULL together when the block is absent |
| *(absent)* | `AlternateTraded*` (×4) | **never emitted here** — see below |
| `numberOfTrades` | `NumberOfTrades` | |
| `settlementState` | `SettlementState` | |
| `lastUpdateDate` | `LastUpdatedDate` | **converted to US Central** (§6) |

**`quantityTraded` is optional and arrives whole.** Present on 678 of 1,237 sampled
records (55%); `numberOfTrades` appears on exactly the same 678. The incumbent shows the
same split (1,109,829 of 2,946,561 rows have a `TradedContractUnit`).

**Amounts carry thousands separators.** `313,100` is three hundred thirteen thousand one
hundred. `decimal.Parse(s, InvariantCulture)` *rejects* it; a comma-decimal culture reads
it as 313.1. Parse with `NumberStyles.Number` under `InvariantCulture`.

**`settlementState`** observed: `Settled`, `Projected`, `Pending`. The incumbent also
holds `NotSettled` — one word, no space (the v2 JSON API spells the same filter
`"Name = Not Settled"`, which is *not* the stored value).

### `CommodityType` and `AlternateTraded*`

Neither has a source element on this endpoint. They are not vestigial — they belong to
the **crude** index endpoint. The retired `dbo.IndexPrice_2` (2022-03 … 2023-11) held
both families in one table and settles it:

| `CommodityType` | rows | non-NULL `AlternateTradedAmount` |
|---|---|---|
| `Natural Gas` | 1,189,358 | **0** |
| `Crude Oil` | 177,145 | 45,046 |

The live `dbo.IndexPrice` (since 2023-11) is `Natural Gas` only, and has 0 alternates
across all 2,946,561 rows. The loader still *reads* `alternateQuantityTraded` if it ever
appears, and `arm.usp_ValidateLoad` flags any non-NULL value.

---

## 6. Timestamps are converted to **US Central**

The vendor stamps its own Mountain offset — `-07:00` in winter, `-06:00` in summer
(a January pull carried `-07:00` on every record; a September pull `-06:00`).

The incumbent stores **US Central**, and the loader matches it byte-for-byte. Verified
on both sides of the DST boundary against `dbo.StripTradingSummary`:

| XML `tradeDateTime` | offset | incumbent row | |
|---|---|---|---|
| `2026-09-01T06:37:06-06:00` | MDT | `2026-09-01 07:37:06` | ref 48000000003842 |
| `2026-01-15T14:14:54-07:00` | MST | `2026-01-15 15:14:54` | ref 48000000007314 |
| `2026-01-15T06:38:52-07:00` | MST | `2026-01-15 07:38:52` | ref 48000000002116 |

Same for `lastUpdateDate`: `2026-09-18T02:15:41-06:00` → `2026-09-18 03:15:41`.

Corroborating: the incumbent's time-of-day range over a fortnight is `06:09:28`–
`15:59:58`, i.e. Central business hours for a Calgary market.

**Convert through the offset, never by adding an hour.** Mountain and Central shift on
the same dates *today*, so "+1h" is right all year — until it is not, or until the
vendor stamps UTC. `TradeDateTime` is a primary key component, so a wrong hour **forks
the key** rather than merely mis-stamping the row.

---

## 7. `stripTradingSummaryXml.xml`

### Request

```
GET /ngxcs/stripTradingSummaryXml.xml
  ?grouping=Hub
  &tradeStartDate=1-September-2026     # d-MMMM-yyyy, INCLUSIVE
  &tradeEndDate=1-September-2026       # INCLUSIVE
```

`tradeEndDate` is inclusive — a 1-Sep…1-Sep request returns 1,601 trades, all stamped
that day. `grouping=Hub` makes no difference to this document: the response is per-trade
either way and is byte-identical (1,281,934 bytes) to the ungrouped
`/api/v2/strip-trading-summary/export?format=xml`.

### Record

```xml
<stripTradingSummary>
  <hub><id>28</id><name>AB-NIT</name></hub>
  <market><id>1</id><name>NGX Phys, FP (CA/GJ), AB-NIT</name></market>
  <stripType>Yesterday</stripType>
  <settlementTitle>1-September-2026 (31-August-2026)</settlementTitle>
  <tradeDateTime>2026-09-01T06:37:06-06:00</tradeDateTime>
  <exchangeReference>48000000003842</exchangeReference>
  <cleared>true</cleared>
  <beginDate>2026-08-31</beginDate>
  <endDate>2026-08-31</endDate>
  <tradedVolume><amount>2500</amount><unit>GJ</unit></tradedVolume>
  <totalVolume><amount>2500</amount><unit>GJ</unit></totalVolume>
  <totalVolumeInTJ>2.5</totalVolumeInTJ>
  <price><amount>1.2000</amount><currency>CAD</currency></price>
  <requestForQuoteIndicator>false</requestForQuoteIndicator>
  <includeInIndexIndicator>true</includeInIndexIndicator>
</stripTradingSummary>
```

Maps 1:1 onto `arm.StripTradingSummary`. Note `totalVolumeInTJ` (capital I) against the
column `TotalVolumeinTJ` (lowercase i) — the column spelling is the supplied DDL's.

`brokerCompanyName` is the one optional element. It appeared in **none** of the 2026
samples nor in a 2023 replay, while the incumbent holds values only for bilateral broker
trades up to 2023 (`CalRock Brokers Inc.`). The loader reads it when present rather than
hard-coding NULL.

### `ExchangeReference` is **recycled**

It is not a globally unique trade id. `48000000003842` was observed on 2025-05-20,
2025-08-11, 2025-11-10 (a different hub and market) and 2026-09-01. This is why the
primary key carries all seven columns. Within a single response the seven-column key
*is* unique (1,601/1,601 distinct).

### No envelope, no truncation flag

Unlike the index document there is **no** `<truncated>`, `<fullListSize>` or page size —
nothing in the body could reveal a server-side cap. Volumes measured:

| window | records | bytes |
|---|---|---|
| 1 day (Tue 1-Sep-2026) | 1,601 | 1.3 MB |
| 1 day (Thu 15-Jan-2026) | 1,724 | 1.4 MB |
| 1 month (August 2026) | 36,430 | 29 MB |

36,430 is not a round number, so no cap was hit — but with no flag to rely on, the
loader chunks weekly by default and `arm.usp_ValidateLoad`'s `StripEmptyWeekday` check
is the backstop.

---

## 8. Status matrix

| Situation | Status | Body | Loader treats as |
|---|---|---|---|
| Normal read | 200 | expected root element | data |
| Window with no data | 200 | well-formed, 0 records | **legitimate empty** |
| Missing / rejected credentials | **302** | empty, `Location:` sso.ice.com | credential error (never followed) |
| Unentitled index id | **403** | HTML "Access Denied", logged in | entitlement — narrow the batch, skip the id |
| More than 10 `indexId` params | **403** | same | configuration error |
| Malformed `tradeStartDate` | **500** | problem+json | failure (retried, then fails the unit) |
| Nonexistent index id (999999) | 200 | 0 records | legitimate empty |
| HTML where XML expected | 200 | `<!DOCTYPE html>` | **malformed** — must not read as empty |
| Vendor outage / maintenance | **503** | Apache HTML error page | failure (retried, then fails the unit) |

The `<!DOCTYPE html>` row is the important one: an authentication regression that returns
a `200` HTML page would otherwise be indistinguishable from "no data today". The loader
requires the expected root element and raises a diagnosable error otherwise.

**The 503 row was observed live** on 2026-09-19, when the whole host — both `.xml`
documents *and* the `/api/v2` token endpoint — returned `503 Service Unavailable` with an
Apache error page, apparently a weekend maintenance window. The loader behaved correctly
end to end: the retry policy exhausted its budget on the transient status, then
`EnsureSuccessStatusCode` raised `HttpRequestException`, which fails the work unit and
records it in `core.LoadLog`. It is never mistaken for an empty read — worth stating
because a 503 body is 299 bytes of HTML, and a reader that counted records without
checking the status would score it as zero rows and move on. (That is exactly how it
first presented during this investigation.)

**Not established:** the behaviour of a malformed `effectiveStart` on `indexPrice.xml`.
The probe was cut short and the service was unavailable at the next attempt. The strip
endpoint answers `500` to a malformed `tradeStartDate`, so the index endpoint most likely
does too, but that is an inference rather than a measurement — treat it as unknown.

---

## 9. Not built

* **Crude index prices** — 172 `CrudeIndexPrice` ids in `dbo.[Index]`, a separate
  endpoint, and the origin of the `CommodityType` / `AlternateTraded*` columns. The
  target table shape already accommodates them.
* `/api/v2` REST feeds — `margin-requirements`, `market-settlement-prices`, `trades`,
  `index-price-detail` (richer per-delivery detail incl. `highPrice`, `lowPrice`,
  `altPrice`, `exchangeRate`).
