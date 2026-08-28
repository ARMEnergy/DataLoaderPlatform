# EvolutionMarkets (Evolution Markets — "EVO DataPipeline API") loader — API field reference

**Vendor:** Evolution Markets Inc.
**Product:** EVO DataPipeline API (`evolve-api.evomarkets.com`)
**In-scope endpoint:** `GET /v1/market-data/history`
**Target:** database `EvolutionMarkets`, schema `arm`, one table `arm.MarketData`

---

## Gate status: **FULLY VERIFIED — zero reconstruction** ✅

Every statement in this document was verified against the **live API** on **2026-08-25** with the
issued key. Nothing here is inferred from documentation alone, and nothing is reconstructed.

The vendor publishes a machine-readable OpenAPI 3.0.1 document at
`GET https://evolve-api.evomarkets.com/swagger/v1/swagger.json` (title *"EVO DataPipeline API"*,
version `V1`). It is **accurate about the request contract** and **incomplete about response
behaviour** — see §4.3, which documents three live behaviours the spec does not mention and one it
contradicts.

Evidence base:
- ~60 live requests across the full 60-day retention window.
- **8,118 rows** — the complete history the key can see (2026-06-26 … 2026-08-24, 38 published
  business dates).
- Every one of the 28 `responseFields` probed individually **and** in the full projection.
- All four authentication states (valid key / no header / wrong key / Basic-encoded key).
- All documented query parameters, including their boundary and error behaviour.

---

## ⚠⚠ THE FOUR THINGS THAT WILL BITE YOU

Read these before anything else. Each was verified live; each is silent if you get it wrong.

| # | Trap | Consequence if missed |
|---|------|----------------------|
| 1 | **"Basic authentication" is NOT HTTP Basic.** The key is the *raw* `Authorization` header value. | A Basic-encoded value gets `403` on every request. |
| 2 | **Requests and responses use DIFFERENT field spellings.** Ask for `marketDataId`, receive `priceId`. | Asking with the response spelling returns **nothing** for that field — silently. |
| 3 | **`change` only resolves in the FULL 23-name projection.** In a short list the server substitutes `term`. | `Change` silently fills with NULLs. |
| 4 | **An empty result is `200 []`, never `404`.** And the response is a bare array with a silent 10,000-row cap. | A weekend looks like an error; a truncated read looks complete. |

---

## 1. Base URL, transport, paths

| Item | Value |
|---|---|
| Host | `https://evolve-api.evomarkets.com` |
| Transport | HTTPS only. HTTP/1.1. |
| Fronted by | AWS API Gateway (`x-amzn-RequestId`, `x-amz-apigw-id`, `X-Amzn-Trace-Id` on every response) |
| In-scope path | `/v1/market-data/history` |
| OpenAPI document | `/swagger/v1/swagger.json` (public — served **without** a key) |
| Content type | `application/json; charset=utf-8` |
| Compression | none observed (no `Content-Encoding`) |
| `servers` block | **absent** from the OpenAPI document — the host above is the observed one |

### 1.1 The full path inventory (from the OpenAPI document)

| Method | Path | In scope? |
|---|---|---|
| `POST` | `/v1/refresh-token` | ✗ — for apps that cannot keep the key secret; not needed here |
| `GET` | `/v1/datasets` | ✗ *(read once during discovery; see §3)* |
| `GET` | `/v1/datasets-preview` | ✗ |
| `GET` | `/v1/evoid` | ✗ — EvoFTP backwards-compatibility shim |
| `GET` | `/v1/instruments` | ✗ — see §8 |
| `GET` | `/v1/market-data` | ✗ — same-day snapshot; see §8 |
| **`GET`** | **`/v1/market-data/history`** | **✓ THE ONLY IN-SCOPE ENDPOINT** |

---

## 2. Authentication — a RAW api key in the `Authorization` header

### 2.1 ⚠⚠ It is NOT HTTP Basic, despite how the requirement was phrased

The OpenAPI document declares exactly one security scheme:

```json
"securitySchemes": {
  "API Key": {
    "type": "apiKey",
    "description": "Authorization header (Example: '12345abcdef')",
    "name": "Authorization",
    "in": "header"
  }
}
```

`type: apiKey` — **not** `type: http, scheme: basic`. The vendor's own example value is a bare token.
Live confirmation:

```
Authorization: 0bf5…                      -> 200 OK          ✅ correct
Authorization: Basic <base64(key:)>       -> 403             ✗ rejected
(no Authorization header)                 -> 401 {"message":"Unauthorized"}
Authorization: deadbeef…  (wrong key)     -> 403 {"Message":"User is not authorized to access
                                                 this resource with an explicit deny in an
                                                 identity-based policy"}
```

Note the `401` vs `403` split, which is diagnostically useful:

| Condition | Status | Body |
|---|---|---|
| Header absent or empty | `401` | `{"message":"Unauthorized"}` (lowercase `message`) |
| Header present, key wrong or unentitled | `403` | `{"Message":"…explicit deny…"}` (**capital** `Message`) |

The capital-`M` `Message` on the 403 is AWS API Gateway's own IAM denial, not the application's —
another sign the 403 comes from the edge, before the app is reached.

### 2.2 Implementation consequence

The header value is not a well-formed RFC 7235 credential (it has no `scheme SP token` shape), so
`HttpRequestHeaders.Authorization = new AuthenticationHeaderValue(...)` cannot express it and the
validating setter throws `FormatException`. Use
`request.Headers.TryAddWithoutValidation("Authorization", key)`.
See `EvoApiKeyAuthHandler`.

### 2.3 There is no token, no expiry and no refresh

The key is **static**. `POST /v1/refresh-token` exists only to let an untrusted client exchange the
key for a short-lived `accessToken`; it is unnecessary for a server-side loader and is not used.

**Consequence for the status matrix:** a `401` is *terminal*, not a trigger to re-mint. Contrast
NGI and IIR (JWT) and IHSPointLogic (Basic + PAT), where a 401 drives a refresh-and-replay.

### 2.4 No URL ever carries the credential

Because the key is a header, **no request URI is sensitive**. This loader therefore logs request URIs
**in full and unsanitised** — a deliberate divergence from CWG/StormVista, whose `?apikey=` URLs must
be truncated at the path before logging. The *header* is still never logged (the client is registered
with `RemoveAllLoggers()`).

---

## 3. Entitlements and retention — `GET /v1/datasets`

Read once during discovery to establish what the key can see. **Not** loaded (see §8).

```
GET /v1/datasets?isPermissioned=true&limit=500
```

The key is permissioned for **exactly one dataset**:

| Field | Value |
|---|---|
| `id` | `60b6db34-e0ac-4502-afe6-5ce0acfe7090` |
| `name` | `EVOID/USNaturalGasIndex` |
| `market` | `US Natural Gas Index` |
| `products` | **41** |
| `isPermissioned` | `true` |
| **`lookbackWindow`** | **`60`** |

### 3.1 The 41 products

All 41 carry **5 terms** each (`2026-Sep`, `2026-Oct`, `2026-Winter`, `Sep'26-Oct'26`,
`Apr'27-Oct'27`) and `startDate` `2026-06-26`, except **`Socal-KRS Physical Index (Priced Off Socal
NGI)`** whose `startDate` is `2026-07-27` — i.e. products can be added mid-window, so the instrument
set is a **snapshot, not a contract**.

Names are US natural-gas basis points: `Socal-Border Index Futures`, `Waha Hub Phys Index`,
`PG&E-Citygate Physical Index`, `Perm Index Futures`, `Rex ANR Phys Index`, … (longest observed: 63
characters — `Socal-City Gate Physical Index (Priced Off Socal City Gate NGI)`).

### 3.2 ⚠ `lookbackWindow: 60` — the retention horizon

The API serves a **rolling 60-day window**. Verified: `dateFrom=1990-01-01` returns exactly the same
8,118 rows as `dateFrom=2026-06-01`, with a minimum `date` of **2026-06-26** = 60 days before the
probe date.

**A pre-retention `dateFrom` is not an error** — it returns `200 []`, exactly like a weekend. So an
over-large `DaysBack` costs wasted requests, not failures. The loader warns rather than clamps.

> **Anything older than 60 days cannot be re-fetched.** If `arm.MarketData` is ever truncated, the
> history beyond 60 days is gone from the source. This is stated in
> `sql/EvolutionMarkets/999_DropEvolutionMarketsObjects.sql`.

### 3.3 The `responseFields` catalogue — 28 fields, 10 of them "default"

`GET /v1/datasets` publishes, per dataset, the fields it can return and which are in the **default**
projection:

| `isDefault: true` (10) | non-default (18) |
|---|---|
| `priceId`, `instrumentTermId`, `instrumentTerm2Id`, `tenor`, `instrumentId`, `priceTs`, `date`, `ask`, `bid`, `mid` | `marketId`, `market`, `marketSourceId`, `term`, `term2`, `instrumentSourceId`, `instrumentSourceName`, `instrument`, `priceType`, `size`, `depth`, `price`, `askSize`, `bidSize`, `midSize`, `change`, `pctRetDaily`, `currency` |

**This is why omitting `field` is not an option:** the default projection silently excludes `market`,
`priceType`, `currency` and `change`, all four of which the target table needs.

> ⚠ **These are the RESPONSE names, and they are NOT the names you request with.** See §4.3.

---

## 4. `GET /v1/market-data/history`

### 4.1 Query parameters (all live-verified)

| Parameter | Required | Type | Behaviour |
|---|---|---|---|
| `dateFrom` | **YES** | `yyyy-MM-dd` | Inclusive lower bound. Malformed → `400`. Vendor-future → `400`. |
| `dateTo` | no | `yyyy-MM-dd` | Inclusive upper bound. Omitted ⇒ up to the latest available. |
| `field` | no | CSV | Projection. **Omitted ⇒ the 10 default fields only.** See §4.3. |
| `limit` | no | `1..10000` | Page size. Outside the range → `400`. |
| `offset` | no | `0..2147483647` | Page offset. Negative → `400`. |
| `datasetId` / `datasetName` | no | string | Filter. Omitted ⇒ **every permissioned dataset**. |
| `instrumentId` / `instrumentName` | no | string | Filter. Not used by this loader. |
| `term` / `tenor` | no | string | Filter. Not used by this loader. |
| `search` | no | `DatasetName:InstrumentName:Term` | Composite filter. Not used. |

**Parameter names bind case-insensitively** — `DateFrom` and `dateFrom` both work. (An early probe
appeared to show `DateTo` being ignored; that was a non-discriminating test — the requested `dateTo`
happened to equal the latest available date. `dateTo` works correctly, verified with
`dateFrom=2026-08-01&dateTo=2026-08-10` → 1,230 rows ending 2026-08-10.)

**Unknown parameters are silently ignored** (`&Bogus=1` → normal `200`).

### 4.2 Error and boundary behaviour

| Request | Status | Body |
|---|---|---|
| no `dateFrom` | `400` | `{"message":"The DateFrom field is required."}` |
| `dateFrom=notadate` | `400` | `{"message":"\"dateFrom\" invalid format. Should be yyyy-MM-dd"}` |
| `dateFrom=2026-12-31` (future) | `400` | `{"message":"\"dateFrom\" must be smaller than current date"}` |
| `dateFrom=2026-08-25` (**today**) | `200` | `[]` — **today is NOT rejected**, it just has no data yet |
| `dateTo` < `dateFrom` | `400` | `{"message":"\"dateTo\" must be smaller than \"dateFrom\""}` *(message is worded backwards; the rule is `dateTo >= dateFrom`)* |
| `limit=0` or `limit=20000` | `400` | `{"message":"The field Limit must be between 1 and 10000."}` |
| `offset=-1` | `400` | `{"message":"The field Offset must be between 0 and 2147483647."}` |
| unknown `field` name | **`500`** | `{"message":"Something went wrong.","requestId":"…"}` |

> **The last row is the nasty one.** A *permanent caller bug* (a typo in `field`) is reported with a
> *transient* status code, so Polly retries it and the full retry budget is burned on every work unit
> before the failure surfaces. `EvoStatus.Describe` names the field list as the first thing to check
> on a reproducible 500.

**Today is requestable.** This is why the loader's window ends at *today* inclusive rather than
yesterday: it costs one cheap `200 []` and guarantees an earlier-than-usual publication is never
missed. Only a *future* date is a hard `400`, which the work-unit provider clamps against.

### 4.3 ⚠⚠ THE FIELD-VOCABULARY TRAP

**Fields are REQUESTED by one set of names and RETURNED under another.** Both are the vendor's own;
they are not interchangeable.

The **request** vocabulary is the "output"/CSV name set. The **response** vocabulary is the
`responseFields` name set from `GET /v1/datasets`. They differ for five fields:

| Request as (`&field=`) | Comes back as (JSON key) |
|---|---|
| `marketDataId` | **`priceId`** |
| `instrumentName` | **`instrument`** |
| `priceTS` | **`priceTs`** |
| `businessDate` | **`date`** |
| `pct_ret_daily` | **`pctRetDaily`** |

Live proof of each:

```
field=marketDataId    -> {"priceId":"6d11bc3c-a601-480a-8032-b6d42068b9ea"}    ✅
field=priceId         -> {}                                          ✗ NOTHING returned
field=instrumentName  -> {"instrument":"Socal-Border Index Futures"}           ✅
field=businessDate    -> {"date":"2026-08-24"}                                 ✅
field=pctRetDaily     -> HTTP 500 "Something went wrong."             ✗ unknown name
```

`marketDataId` and `priceId` are **the same field**: for row
`6d11bc3c-a601-480a-8032-b6d42068b9ea`, the JSON rendering calls it `priceId` and the CSV/XML
renderings of the identical row call it `marketDataId` (verified byte-for-byte). This is why the
target column is `MarketDataId` while the JSON reader looks for `priceId`.

#### The `change` positional bug

`change` resolves **correctly only inside the full 23-name projection**. In a shorter list the server
returns `term` in its place:

```
field=marketDataId,change                        -> {"priceId":"…","term":"Sep'26-Oct'26"}   ✗
field=marketDataId,ask,bid,change,currency       -> priceId, term, ask, bid, currency        ✗
field=<all 23>                                   -> …,"change":-0.0263,…                     ✅
```

In the full list `change` is genuinely distinct data: **196 of 205** rows on 2026-08-24 had
`change != mid` (the 9 coincidental matches are real ties).

Two other aliasing quirks, recorded for completeness (neither is used by the loader, which requests
only the pinned list):

```
field=instrumentTermId    -> {"term": …}         (aliases to `term`)
field=instrumentSourceId  -> {"instrument": …}   (aliases to `instrument`)
```

#### ⇒ The pinned projection

The loader always sends this **exact** 23-name list, in this order
(`EvoRequestFields.All` / `EvoRequestFields.Csv`):

```
marketDataId,market,term,term2,tenor,instrumentSourceName,instrumentId,instrumentName,
priceTS,businessDate,priceType,size,depth,price,ask,askSize,bid,bidSize,mid,midSize,
change,pct_ret_daily,currency
```

**Do not trim it.** Doing so silently NULLs `Change`. `FieldProjectionTests` pins the list, and the
reader warns `PROJECTION DRIFT` if an expected response key is missing from every row of a page.

### 4.4 Response shape

A **bare JSON array** of objects. No envelope, no total count, no page count, no next-page link, no
`Link` header, no pagination metadata of any kind.

```json
[
  {"priceId":"6d11bc3c-a601-480a-8032-b6d42068b9ea","market":"US Natural Gas Index",
   "term":"Sep'26-Oct'26","instrumentId":"997feae2-4c97-4e14-b090-203d625ff5a5",
   "instrument":"Socal-Border Index Futures","priceTs":"2026-08-24T00:00:00.000Z",
   "date":"2026-08-24","priceType":"Indicative","ask":-0.02,"bid":-0.0325,"mid":-0.0263,
   "change":-0.0263,"currency":"USD"}
]
```

> ⚠ **Rows are heterogeneous.** A field with no value has its **key OMITTED ENTIRELY** — it is never
> sent as JSON `null`. `tenor` is present on ~47% of rows and absent on the rest, and nine fields are
> absent on every row. Default POCO binding is not wrong here, but "absent" and "null" must be one
> code path.

The endpoint also serves `text/csv` and `application/xml` via `Accept`. **The CSV header is where the
target DDL came from** — it matches the requested table column-for-column:

```
marketDataId,market,term,term2,tenor,instrumentSourceName,instrumentId,instrumentName,
priceTS,businessDate,priceType,size,depth,price,ask,askSize,bid,bidSize,mid,midSize,
change,pct_ret_daily,currency,extra.…
```

JSON is used by the loader (the CSV rendering has its own quirk: it emits the full fixed header with
`market`/`priceType`/`currency` **blank** unless `field` is supplied, and the `extra.*` tail contains
a **duplicated `extra.change` column**).

### 4.5 Full field set for `EVOID/USNaturalGasIndex` — what is actually populated

Census over the **complete 8,118-row history** with the full pinned projection:

| # | Response key | Type | Populated | Target column | Notes |
|---|---|---|---|---|---|
| 1 | `priceId` | uuid | **8118/8118** | `MarketDataId` | **PK.** Unique and stable — see §5 |
| 2 | `market` | string | 8118/8118 | `Market` | single value `US Natural Gas Index` (len 20) |
| 3 | `term` | string | 8118/8118 | `Term` | free text, **several shapes** — see §4.6 |
| 4 | `tenor` | string | **3854/8118 (47%)** | `Tenor` | `2m`..`5m`; **absence is NORMAL** |
| 5 | `instrumentId` | uuid | 8118/8118 | `InstrumentId` | 41 distinct |
| 6 | `instrument` | string | 8118/8118 | `InstrumentName` | max len **63** |
| 7 | `priceTs` | ISO instant | 8118/8118 | `PriceTS` | always **midnight `Z`** |
| 8 | `date` | `yyyy-MM-dd` | 8118/8118 | `BusinessDate` | equals the requested date |
| 9 | `priceType` | string | 8118/8118 | `PriceType` | single value `Indicative` |
| 10 | `ask` | number | 8118/8118 | `Ask` | **SIGNED** |
| 11 | `bid` | number | 8118/8118 | `Bid` | **SIGNED** |
| 12 | `mid` | number | 8118/8118 | `Mid` | **SIGNED**, read never computed |
| 13 | `change` | number | 8118/8118 | `Change` | **SIGNED**, legitimately `0`; see §4.3 |
| 14 | `currency` | string | 8118/8118 | `Currency` | single value `USD` |
| — | `term2` | string | **0/8118** | `Term2` | never populated |
| — | `instrumentSourceName` | string | **0/8118** | `InstrumentSourceName` | never populated |
| — | `size` | number | **0/8118** | `Size` | never populated |
| — | `depth` | number | **0/8118** | `Depth` | never populated |
| — | `price` | number | **0/8118** | `Price` | never populated — **do NOT backfill from `mid`** |
| — | `askSize` | number | **0/8118** | `AskSize` | never populated |
| — | `bidSize` | number | **0/8118** | `BidSize` | never populated |
| — | `midSize` | number | **0/8118** | `MidSize` | never populated |
| — | `pctRetDaily` | number | **0/8118** | `PctRetDaily` | never populated |

**14 populated, 9 always-empty.** The nine are real columns of the vendor's documented
`TransformedDataItem` contract that other datasets (carbon, power/nodal — note the `nodal` object and
the `extra.carbon*` / `extra.po*` fields in the schema) do populate. They are requested and modelled
anyway so a new entitlement needs no migration; `arm.usp_ValidateLoad` reports them
**informationally**, and a non-zero count is *good news*, not a fault.

### 4.6 Semantics you must not get wrong (all live-observed)

1. **`term` is free text in several distinct shapes.** Observed: `Sep'26-Oct'26` (quoted month
   range), `Apr'27-Oct'27`, `2026-Winter` (season), `2026-Oct`, `2026-Sep` (single month).
   **Never parse it, never normalise it, never derive a date from it.** Max len 13.
2. **`mid` is read, never computed.** It *is* the midpoint, but rounded to 4 dp:
   bid `-0.0325` / ask `-0.02` → published mid `-0.0263` (the exact midpoint is `-0.02625`).
   Recomputing it would disagree with the vendor in the last digit on roughly half the rows.
3. **Negative prices are ROUTINE.** These are basis differentials, not outright prices. Observed
   `ask -0.10 / bid -0.25 / mid -0.175` on Socal-Needles. **No non-negative CHECK constraint on any
   price column.**
4. **`change` of exactly `0` is real** (a point that did not move) and must never be normalised to
   NULL.
5. **`priceTs` is UTC with an explicit `Z`**, always midnight, always equal to `date`. Parsing it as
   local time on a US-Central host would shift it back 5–6 h and — at `DATETIME2(0)` — land it on the
   **previous day**, silently disagreeing with `BusinessDate` on every row.
   `arm.usp_ValidateLoad`'s `PriceTsDateMismatch` check exists for exactly this.
6. **`priceType` and `currency` are NOT enums** in any vendor document, despite each having a single
   observed value. No lookup table, no CHECK constraint — a new value is reported, never rejected.
7. **Row count per date is NOT constant.** 205 rows on most dates, **287** on 2026-07-21…07-24. No
   validation check may assert a fixed number.

### 4.7 ⚠ Paging — and why the pager is a correctness guard

`offset`/`limit` are honoured and **stable**. Verified twice:

| Window | Un-paged | Paged | Distinct ids |
|---|---|---|---|
| 2026-08-01 … 08-10 | 1,230 | 500 + 500 + 230 | **1,230** ✅ |
| 2026-08-01 … 08-31 | 3,075 | 1000 + 1000 + 1000 + 75 | **3,075** ✅ |

No duplicates, no gaps, no reordering. Ordering is newest-date-first
(`dateFrom=1990-01-01&limit=50` returned only 2026-08-24 rows).

**But the 10,000-row cap is SILENT.** The OpenAPI summary states *"with a limit of 10K records per
request"*, and the response carries no total, no page count and no truncation flag — so a read that
hit the cap would be **byte-indistinguishable from a complete read**. Paging until a short page is
the only way to know a read finished.

The loader therefore uses **one work unit per business date** (`dateFrom == dateTo`), which keeps a
single request at ~205 rows — three orders of magnitude below the cap — *and* pages anyway as a
guard. It also makes the reversed-range `400` structurally unreachable.

### 4.8 The publication calendar — ⚠ NOT DERIVABLE

Over the full 60-day window: **38 published dates out of 60 calendar days.**

- **No weekend date is EVER present** — so a weekday filter *looks* safe.
- **It is not.** Four **weekdays** are missing anyway:

| Missing weekday | Day | Explanation |
|---|---|---|
| 2026-07-02 | Thu | US Independence Day cluster |
| 2026-07-03 | Fri | US Independence Day cluster (Jul 4 is a Saturday in 2026) |
| 2026-07-06 | Mon | US Independence Day cluster |
| **2026-08-20** | **Thu** | **NONE — a plain mid-week day with no holiday** |

That last one is the whole argument. The gap is not derivable from a weekday rule, a US federal
holiday table, or anything else the loader could compute, and **the vendor publishes no trading
calendar anywhere**.

⇒ **Probe every calendar day and let the empty array be the answer.** `DO NOT` add a weekday or
holiday filter to `EvoMarketDataWorkUnitProvider`.

---

## 5. The primary key — a vendor-supplied surrogate

`priceId` / `marketDataId` is used directly as `arm.MarketData`'s `PRIMARY KEY`, per the loader spec
("merge data by PK"). Two properties make that safe, both verified:

1. **Unique.** 8,118 rows → 8,118 distinct values.
2. **Stable across re-pulls.** Re-requesting a date returns the *same* id for the same
   (instrument, term, tenor, business date). This is what makes "merge by PK" idempotent — and it is
   the **only** mechanism by which a vendor **revision** can land, since the feed carries no
   revision, version or status field at all: a corrected price arrives as the same id with new values.

The composite alternative **`(instrumentId, term, tenor, date)` is ALSO unique** over the same 8,118
rows. That is the guard: `arm.usp_ValidateLoad`'s `DuplicateCompositeKey` check counts composite keys
carrying more than one `MarketDataId`. If the vendor ever starts minting a fresh id per pull, that
check goes non-zero — the one failure mode this key choice admits. The documented fallback is to
re-key on the composite and keep `MarketDataId` as a `UNIQUE` attribute.

---

## 6. Status-code matrix (with the probe's evidence)

| Status | Meaning | Loader action | Evidence |
|---|---|---|---|
| `200` + rows | data | parse → `Success` | routine |
| **`200` + `[]`** | **no publication that day** | **`NotAvailable`, 0 rows, unit SUCCEEDS** | weekends, holidays, today, pre-retention |
| `400` | malformed request | **THROW** — loader bug | §4.2 |
| `401` | key missing/blank | **THROW** — no token to refresh | §2.1 |
| `403` | key wrong/unentitled | **THROW** | §2.1 |
| `404` | *never observed* | `NotAvailable` **+ warn** | path/version moved? |
| `429` | *never observed* in ~60 calls | retry (Polly) then THROW | no rate limit published |
| `500` | server error **or unknown `field`** | retry then THROW, naming `field` | §4.2 |

No `Retry-After` and no `X-RateLimit-*` header was ever returned. The vendor publishes no rate limit;
the loader paces at a conservative default of **5 rps**.

---

## 7. Notes for `DATABASE_DEVELOPER` (recommendations only)

### 7.1 Grain and keys
- Grain: one row per **instrument × term × tenor × business date**.
- PK: `MarketDataId` (vendor surrogate) — see §5, and keep the `DuplicateCompositeKey` guard.
- Volume: ~205 rows/business date, ~250/yr publishing days ⇒ **~50–75k rows/year**. Tiny.

### 7.2 Type calls
- Prices → `DECIMAL(18,8)`, **never FLOAT**, **signed, no CHECK** (§4.6 item 3).
- `PriceTS` → `DATETIME2(0)` **UTC**; `BusinessDate` → `DATE`.
- `MarketDataId`/`InstrumentId` → `UNIQUEIDENTIFIER`.
- Strings → `VARCHAR` (all observed characters are ASCII), widths well above observed maxima.

### 7.3 Index note
The supplied DDL clusters on the random-GUID PK. Retained: at ~205 rows per daily load into a
~75k-row/year table the page-split cost is immaterial. But the clustered key is useless for
analytical access, so nonclustered indexes on `BusinessDate`, `(InstrumentId, BusinessDate)` and
`FileLogId` were added.

---

## 8. Out of scope (and why)

| Endpoint / feature | Why not |
|---|---|
| `GET /v1/market-data` (snapshot) | The loader spec names the *history* path. The snapshot returns the last business date only — data the history endpoint also serves. Its **one** advantage is same-day latency: `history` cannot return today's data until tomorrow (today returns `[]`). Revisit only if same-day prices are required. |
| `GET /v1/instruments` | Would back an `arm.Instrument` dimension. The spec pins exactly one table, and nothing reads a dimension at load time. `InstrumentId`/`InstrumentName`/`Market` stay denormalised inline. |
| `GET /v1/datasets` | Read during discovery (§3); not persisted. A future `arm.Dataset` table could record `lookbackWindow` and `responseFields`, which would let the loader *detect* an entitlement change rather than be told. |
| `POST /v1/refresh-token` | Unnecessary for a server-side loader holding a static key (§2.3). |
| `GET /v1/evoid`, `/v1/datasets-preview` | Legacy FTP shim / preview surface. No loader value. |
| CSV / XML response types | JSON is used. Recorded in §4.4 because the CSV header is where the target DDL originated. |
| The `nodal` object and `extra.*` fields | Present in the shared `TransformedDataItem` schema but belong to carbon/power datasets this key cannot see. **Not modelled** — do not add columns the API cannot fill. |

---

## 9. Open questions (behavioural only — no field is unknown)

1. **Revision frequency is unmeasured.** The mechanism is understood (same id re-served with new
   values — §5) but no revision was *observed* in a single-day probe; that needs two runs a day
   apart. The all-hot 30-day default makes the loader correct either way.
2. **Publication time of day is unknown.** Every `priceTs` is midnight UTC, which is a *stamp*, not a
   publication time. The current date returned `[]` at 20:30 UTC on 2026-08-25, so publication for
   date *D* lands sometime after that on *D* — or on *D+1*. Only relevant if same-day latency is ever
   wanted (see §8).
3. **The 2026-08-20 gap is unexplained.** A plain Thursday with no data. Whether it was a vendor
   outage or a genuine non-publication is unknown; either way it proves the calendar is not
   derivable (§4.8).
4. **Whether `287`-row dates indicate added products or added terms** was not decomposed. Harmless —
   no check asserts a fixed row count.
5. **Rate limits are undocumented and unobserved.** No `429` in ~60 calls. The 5 rps default is a
   guess on the safe side.
