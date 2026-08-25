# ModernCommodities (Modern Commodities / "ModCom") loader — API field reference

Crude-oil and refined-product **trade** and **daily settlement** data from **Modern Commodities**
(`app.modcom.inc`), the electronic trading venue. Three endpoints: an anonymised market-wide trade
tape (`allTrades`), the company's own fully-attributed trades (`myTrades`), and a daily
forward-curve settlement file (`settlements`).

This file is the **mandatory full-field-set gate** for the loader. Every column of all three
endpoints is enumerated below in exact source order with its verbatim header name, inferred type,
observed maximum length, blank/NULL behaviour, and the target column it maps to in the user's
authoritative DDL. Nothing downstream (model → sink → TVP → table → merge proc) may ship a partial
column set.

- **Loader id:** `ModernCommodities`. **DB:** `ModernCommodities` — **schema:** `arm` (per the
  platform convention used by Platts/CWG/AGSI/IHSPointLogic/IIR/OPIS/NGI); audit hub `arm.FileLog`.
- **Targets (user-supplied DDL, authoritative):** `arm.AllTrades`, `arm.MyTrades`,
  `arm.Settlements`. Three **closed, fully independent** pipelines — no discovery tier, no
  reference provider, no barrier, **no FK between the three tables**. A settlements-only run is
  fully valid.
- **Secrets:** the API credentials are referred to **only** by config setting name —
  `Loaders:ModernCommodities:Username` and `Loaders:ModernCommodities:Password`, both shipped as
  the `"SEE_DB"` sentinel and resolved at run time from `core.Param`. **No username, password, or
  encoded `Authorization` value appears anywhere in this file**, and none may be written into
  `appsettings.json`, a log, a test fixture, or a committed sample.

---

## Gate status: **FULLY VERIFIED — zero reconstruction** ✅

Every column name, type, observed maximum, blank/sentinel behaviour, row cap, history limit and
error body in this document was **observed against the live API on 2026-08-24**. There is **not one
reconstructed field.** This matches `NGI`'s posture and contrasts with `IIR`, whose `detail`-JSON
casing is inferred and still carries an open live-verification checklist.

> **⚠ convention in this document:** because no field is reconstructed, ⚠ is used **only** for
> behavioural/operational unknowns (§13) and for hazards that will silently corrupt a load.
> **There are zero ⚠ "unverified field" marks in any field table.**

| Item | Provenance |
|---|---|
| `GET allTrades/v1` — **all 34 columns**, exact order, header verbatim | **Verified live** 2026-08-24, `startDate=2026-08-20&endDate=2026-08-24`, HTTP 200 `text/csv`, `Content-Length: 25816`, **98 data rows**. Capture: `raw_allTrades.csv`. |
| `GET myTrades/v1` — **all 34 columns**, byte-identical header to `allTrades` | **Verified live** 2026-08-24, `startDate=2026-02-24&endDate=2026-08-24`, HTTP 200, **37 data rows**. Capture: `my180.csv`. |
| `GET settlements/v1` — **all 9 columns**, exact order, header verbatim | **Verified live** 2026-08-24, `startDate=2026-08-20&endDate=2026-08-24`, HTTP 200 `text/csv`, `Content-Length: 140019`, **1,443 data rows**. Capture: `raw_settlements.csv`. |
| The **anonymisation matrix** (14 columns blank in 100% of `allTrades`, populated in `myTrades`) | **Verified live** — recounted field-by-field in this pass against both captures. §9. **One correction to the briefing made here.** |
| The **`Last Updated Timestamp` window-filter finding** (5 of 98 rows executed before the window, 0 outside on last-updated) | **Verified live** — every timestamp in `raw_allTrades.csv` re-read in this pass. §5.2. |
| Settlements filters on `Settlement Date` (no out-of-window leakage) | **Verified live** — all 1,443 rows are dated `2026-08-20` (720) or `2026-08-21` (723); zero rows on any other date. §5.3. |
| Header-only `200` = successful empty read | **Verified live** — `myTrades` for `2026-08-20..2026-08-24` returned HTTP 200, `Content-Length: 498`, header only, **zero data rows**. Capture: `raw_myTrades.csv`. §7.3. |
| **6-calendar-month** history limit, to the day | **Verified live** — on 2026-08-24, `startDate=2026-02-24` → `200`, `startDate=2026-02-23` → `400`. §5.4. |
| Row caps (trades 10,000 / settlements 100,000) and reachability | Caps: **vendor PDF + live 400 body**. The 170-day → 9,849-row / 172-day → 400 probe: **verified live per the probe record** (see the provenance note in §6.2). Settlements per-publication volume: **recounted live** in this pass. |
| All four distinct `400` bodies + the empty-bodied `401` | **Verified live** — each condition provoked individually. §7.1. |
| The `-` sentinel in settlements (82 of 1,443 rows, always in **both** key columns together, all one product) | **Verified live** — recounted in this pass. §11.3. |
| 12-hour `AM`/`PM` timestamps, negative prices, `True`/`False` booleans, embedded commas | **Verified live** in the captures. §8. |
| Endpoint list, auth scheme, "not intended to be rapidly polled", rate-limit warning | **Vendor PDF** — `docs/Dev Documentation/Modern Commodities API Information.pdf`, corroborated live. |

### There is no Swagger/OpenAPI document at all

Modern Commodities publishes **no machine-readable spec** — no OpenAPI/Swagger document, no JSON
schema, no `.wsdl`, nothing. The only vendor artefact is a **4-page PDF** which documents the three
URLs, the four query parameters, the auth scheme and the two record caps, and then says of the
response format only:

> *"Responses will be in CSV format, provided separate from this PDF."*

The "separate" CSV specification **was never supplied**. Consequences to internalise, in the same
spirit as NGI's "No response body" finding:

- **Do not go looking for a schema — there is none.** The captured CSVs are the only schema of
  record, and a `CODE_TESTER` contract test asserting the 34-column and 9-column header text,
  order and count is the only regression net.
- **Any vendor-side column change is silent by construction.** Parse tolerantly, bind by literal
  header name, and let `usp_ValidateLoad` surface a shape drift as a row-count / NULL-rate anomaly.
- Where the PDF makes a behavioural claim that would change loader behaviour, it was **probed**;
  §5.4 and §6 record the probes. Two PDF statements needed correcting or sharpening (§5.4, §12).

---

## 1. Identity and transport

| Property | Value |
|---|---|
| **Base URL** | `https://app.modcom.inc/api/integration/` |
| **Version segment** | `/v1`, appended **per endpoint** (`allTrades/v1`), not to the base path. Config setting `Loaders:ModernCommodities:BaseUrl`. |
| **Endpoints** | `allTrades/v1`, `myTrades/v1`, `settlements/v1` — **three, all HTTPS `GET`** |
| **Auth** | **HTTP Basic** — `Authorization: Basic base64(username:password)` (§2) |
| **Response `Content-Type`** | **`text/csv`** on every successful call |
| **Error `Content-Type`** | `text/plain` — the error body is a bare sentence, **not** CSV and **not** JSON (§7.1) |
| **Paging** | **None.** No `page`, `pageIndex`, `offset`, `limit` or cursor parameter; no paging envelope, no `next` link, no total count, no `Link` header. One request = the complete result set, bounded instead by a hard row cap (§6). |
| **Batching** | **None.** No endpoint accepts a repeated id parameter, so the ≤50-id batching rule from `IIR`/`IHSPointLogic` **does not apply**. Fan-out is over **date ranges**, not ids. |
| **Discovery** | **None.** No endpoint needs a value obtained from another. `myTrades`'s optional `legalEntityName` is the closest thing, and its valid values are discoverable *from the 400 error body* (§7.1) or the web UI — not from a discovery endpoint. |
| **Rate limits** | Unpublished. The PDF states a rate limit *"may be applied to requests that are made too rapidly"* and that the API *"is not intended to be rapidly polled, but rather queried periodically."* No `429` was observed; no `Retry-After` or `X-RateLimit-*` header appears in any captured response. **Pace conservatively** with a throttle handler. |
| **Infrastructure** | Responses come through **AWS CloudFront** (`Via: 1.1 …cloudfront.net`, `X-Cache: Miss from cloudfront`). Security headers present: `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin`, `Content-Security-Policy`. No `ETag`, no `Last-Modified`, no `Cache-Control` — so **no conditional-GET / 304 optimisation is available.** |

### 1.1 ⚠⚠ Responses are CSV, not JSON — `HttpJsonSourceReaderBase` is unusable

Every successful response is `Content-Type: text/csv`. There is **no JSON representation of any
endpoint** — no `?format=json`, no `Accept`-negotiated alternative, no `.json` sibling path.

**Therefore:**

- **Do NOT use `HttpJsonSourceReaderBase`.** This is a CSV-over-HTTP loader; the closest precedents
  in this repo are **StormVista** (HTTP + CSV hybrid) and **CWG** (HTTP + CSV, descriptor-driven).
  Reuse the existing CWG/StormVista CSV parse helper if it fits.
- **Do NOT split lines on `,`.** Every field is double-quoted and fields legitimately contain commas
  *inside* the quotes (§8.1). A naive comma split silently shifts every subsequent column on the
  affected rows — the exact class of failure (an HTTP 200 that produces corrupt rows and throws
  nothing) that this document exists to prevent. Use a real **RFC 4180** tokenizer.

### 1.2 Line terminator and encoding (derived from the observed `Content-Length`)

The header-only `myTrades` response was `Content-Length: 498`. The 34 header names total **396**
characters; plus `34 × 2 = 68` quote characters, plus `33` commas = **497 bytes of header text**.
`498 − 497 = 1` byte remains.

Two things follow, and both are load-bearing:

- **The line terminator is a single byte — LF (`\n`), not CRLF.** Do not assume `\r\n`; a parser
  that requires it will treat the whole payload as one line. (Accept both anyway.)
- **There is no UTF-8 BOM.** A BOM would occupy 3 bytes, and only 1 unaccounted byte exists. Reading
  with a BOM-tolerant encoding is still harmless, but nothing may *depend* on a BOM being present.

The same arithmetic checks out on settlements: header text = 111 bytes + 1 LF = 112; the remaining
`140019 − 112 = 139,907` bytes over 1,443 rows ≈ 96.96 bytes/row, consistent with the observed row
width. **All observed characters in all three captures are ASCII** → `VARCHAR` is correct
throughout; `NVARCHAR` is not needed (revisit only if non-US counterparties with accented legal
names are ever onboarded — see §13).

---

## 2. Authentication — HTTP Basic

**Auth shape, matched to the patterns already in this repo: HTTP Basic — the `IHSPointLogic`
PAT pattern.** Not the `?apikey=` query of CWG/StormVista, not the `x-key` header of AGSI, and
**not** a minted JWT bearer token (contrast IIR and NGI — there is no token endpoint here at all).

```
GET https://app.modcom.inc/api/integration/allTrades/v1?startDate=2026-08-20&endDate=2026-08-24
Authorization: Basic <base64(username:password)>
```

| Aspect | Detail |
|---|---|
| Scheme | `Authorization: Basic base64(<Username>:<Password>)`, sent on **every** request |
| Credential source | `Loaders:ModernCommodities:Username` and `Loaders:ModernCommodities:Password`, both `"SEE_DB"` in `appsettings.json`, resolved from `core.Param`. Env override: `DATALOADER_Loaders__ModernCommodities__Username` / `__Password`. |
| Credential shape | The vendor issues an opaque machine username and an opaque machine password per API key (created in the web UI under *API Integration*). They are **not** a person's login. The PDF also presents the pair pre-encoded as a ready-made `Authorization` header value — **that encoded form is a credential too and must never be committed or logged.** |
| **Where the credential travels** | In a **request header**. **Not** in the URL and **not** in a request body. |
| **Token lifetime / renewal** | **None — there is no token.** There is no mint endpoint, no refresh endpoint, no expiry, and nothing to cache. The credential is presented in full on every call. Consequently there is **no 401-re-mint retry** to implement (contrast IIR and NGI); a `401` is terminal and means the configured credential is wrong or revoked. |
| Wrong credentials | **HTTP 401 with an empty body** (§7.1) |
| Handler placement | A `DelegatingHandler`, ordered **retry (OUTER) → auth → throttle (INNER)** per the repo standard, so throttling stays innermost. |
| Logging | Because the credential is **not in the URL**, `RemoveAllLoggers()` (required for CWG/StormVista, whose `?apikey=` sits in the query) is **not** strictly needed — the request URI is safe to log. **The `Authorization` header must never be logged**, at any level, in any handler. |
| Rotation | Multiple API keys can coexist (the PDF documents creating several), so a key can be rotated without an outage. Nothing in the loader needs to change — only the `core.Param` value. |

---

## 3. Endpoint inventory

| # | Endpoint | Path | Cols | Scope | History | Row cap | Extra param |
|:-:|---|---|:-:|---|---|:-:|---|
| 1 | **All Trades** | `GET allTrades/v1` | **34** | *All* finalized and cancelled trades on the venue, **counterparty block anonymised** (§9) | **past 6 calendar months** | 10,000 | — |
| 2 | **My Trades** | `GET myTrades/v1` | **34** | Only trades the company is a party to, **fully attributed** | **all time** | 10,000 | `legalEntityName` |
| 3 | **Settlements** | `GET settlements/v1` | **9** | Daily settlement prices (a full forward curve per product/location per settlement date) | **past 6 calendar months** | 100,000 | — |

Endpoints 1 and 2 return a **byte-identical 34-column header in identical order** (verified: the
header line of `raw_allTrades.csv`, `my180.csv` and `raw_myTrades.csv` are the same 497 bytes). They
differ **only** in row scope and in which columns carry values (§9). This is why one parse shape and
one row type serve both, and why two tables exist.

---

## 4. Query parameters

All three endpoints accept `startDate` and `endDate`. `myTrades` additionally accepts
`legalEntityName`. **All parameters are optional on all endpoints.**

| Param | Endpoints | Type | Required | Format | Default | Notes |
|---|---|---|:--:|---|---|---|
| `startDate` | all 3 | string | No | **`YYYY-MM-DD`** | **today** (vendor-side "today", §5.5) | Unparseable → `400 Invalid startDate`. On `allTrades`/`settlements`, earlier than `today.AddMonths(-6)` → `400` (§5.4). |
| `endDate` | all 3 | string | No | **`YYYY-MM-DD`** | **today** | A **future** `endDate` is **accepted** (`200`) — a harmless upper bound (§5.5). |
| `legalEntityName` | `myTrades` only | string | No | free text, **spaces and commas are literal** | *(omitted → **both** ARM legal entities)* | Verified: omitting it returns trades for **both** entities. An invalid value → `400` whose body **enumerates the valid values** (§7.1) — a de-facto discovery mechanism. Must be **URL-encoded**: valid values contain spaces and a comma. |

**Example requests** (credential shown only as a placeholder):

```
GET https://app.modcom.inc/api/integration/allTrades/v1?startDate=2026-08-20&endDate=2026-08-24
Authorization: Basic <base64(Username:Password)>

GET https://app.modcom.inc/api/integration/myTrades/v1?startDate=2026-02-24&endDate=2026-08-24
Authorization: Basic <base64(Username:Password)>

GET https://app.modcom.inc/api/integration/settlements/v1?startDate=2026-08-20&endDate=2026-08-24
Authorization: Basic <base64(Username:Password)>
```

**Always send both dates explicitly.** Relying on the "defaults to today" behaviour makes the
work-unit `Key` unable to encode what was actually fetched, which breaks the platform's idempotency
contract (`core.LoadLog` skips only keys already recorded successful) — the same objection recorded
for NGI's parameterless call. It also silently binds the result to the *vendor's* notion of today
(§5.5).

---

## 5. Date-window semantics — read this section before designing the resume key

### 5.1 ⚠⚠ THE HEADLINE FINDING: the trades window filters on `Last Updated Timestamp`, **not** `Executed Timestamp`

Measured on `raw_allTrades.csv`, requested window **`2026-08-20` .. `2026-08-24`**, 98 rows:

| Column | Min observed | Max observed | Rows falling **outside** the requested window |
|---|---|---|:--:|
| `Executed Timestamp` | **`2026-08-06 02:02:06 PM`** (= 14:02:06) | `2026-08-24 01:44:41 PM` | **5 of 98** |
| `Last Updated Timestamp` | `2026-08-20 08:10:38 AM` | `2026-08-24 01:54:34 PM` | **0 of 98** |

The five rows executed **before** the requested window — each of them re-touched inside it:

| `Trade Number` | `Executed Timestamp` | `Last Updated Timestamp` | Days between |
|---|---|---|:--:|
| `67868` | `2026-08-18 10:52:44 AM` | `2026-08-21 10:02:19 AM` | 3 |
| `67183` | `2026-08-10 09:39:50 AM` | `2026-08-20 12:07:21 PM` | 10 |
| `66989` | `2026-08-06 02:02:06 PM` | `2026-08-24 12:29:15 PM` | **18** |
| `66988` | `2026-08-06 02:02:06 PM` | `2026-08-24 12:29:15 PM` | **18** |
| `66987` | `2026-08-06 02:02:06 PM` | `2026-08-24 12:29:15 PM` | **18** |

`Executed` leaks out of the window in both directions of reasoning; `Last Updated` does not leak at
all, in 98 of 98 rows. **The window predicate is `Last Updated Timestamp`.**

#### Why this determines the whole loader design

1. **A trade that is 18 days old can re-enter a 5-day window.** The last three rows above are a
   spread triple executed on 2026-08-06 and revised on 2026-08-24. The venue really does restate
   older trades: `State` flips `Finalized` → `Cancelled`, or economics are corrected, and
   `Last Updated Timestamp` advances to the moment of the change.
2. **This is *why* a rolling 30-day re-pull is the right shape.** The re-pull is not defensive
   padding against late arrivals — it is the **only** mechanism that surfaces revisions and
   cancellations of older trades. Narrow the window and those corrections are never seen.
3. **This is *why* merging by `TradeNumber` alone is correct.** A revision arrives as the *same*
   `TradeNumber` with a later `Last Updated Timestamp` and changed payload. A key that included
   the timestamp or the date would insert a duplicate row instead of correcting the existing one.
4. **⚠ This is *why* a settled (stable) resume key would be WRONG for this loader.** Nothing about
   this data is ever final: a five-month-old trade can still be revised. The two-zone
   settled/hot pattern used by StormVista/AGSI/NGI pins a settled key as done-forever, which here
   would permanently freeze a trade at its pre-revision state. Use a **hot-only** key at hour
   granularity (the `IHSPointLogic` `RunHour` strategy): a new hour re-pulls the whole window; a
   second run inside the same hour idempotently skips.
5. **⚠ Out-of-order merges are therefore real, not theoretical.** Hourly runs × configurable
   chunking × parallel work units mean the same `TradeNumber` can reach the MERGE twice from
   different chunks. Guard `WHEN MATCHED` with a last-updated recency test so a stale copy cannot
   overwrite a newer revision (the OPIS `SourceFileDate` precedent). `Settlements` has no such
   column and takes plain last-wins.

### 5.2 Settlements behaves differently — it filters on `Settlement Date`, and does not leak

Measured on `raw_settlements.csv`, same requested window `2026-08-20 .. 2026-08-24`, 1,443 rows:

| `Settlement Date` | Day of week | Rows |
|---|---|:--:|
| `2026-08-20` | Thursday | **720** |
| `2026-08-21` | Friday | **723** |
| `2026-08-22` | Saturday | **0** |
| `2026-08-23` | Sunday | **0** |
| `2026-08-24` | Monday (the request day) | **0** |

`720 + 723 = 1443` = the full row count, so **zero rows fell outside the requested window**. Three
distinct facts, all verified:

- **The settlements predicate is `Settlement Date` itself** — an in-payload column, exact, no
  leakage. Unlike trades, there is no hidden "last updated" dimension.
- **Settlements publish on business days only.** Saturday and Sunday returned nothing. This is a
  legitimate gap inside a `200`, not an error.
- **⚠ The current day's settlements were not yet published** at the time of the call
  (`Date: Mon, 24 Aug 2026 19:55:50 GMT` — mid-afternoon in North America). So an `endDate` of
  *today* will routinely return nothing for today, and today's curve appears on a **later** run.
  A single daily run risks permanently missing the newest settlement date if its key is settled —
  another argument for the hot-only, re-pulling key of §5.1 #4.

### 5.3 History limit: exactly **6 calendar months** — `today.AddMonths(-6)`, **not** 183 days

The PDF says `allTrades` and `settlements` are *"limited to data from the past 6 months"* and that
an earlier `startDate` yields `400`. That claim was **probed to the day**, because the difference
between "6 calendar months" and "183 days" changes what the loader computes:

| Probe on 2026-08-24 | `startDate` | Result |
|---|---|---|
| exactly 6 calendar months back | **`2026-02-24`** | **`200`** ✅ |
| one day earlier | **`2026-02-23`** | **`400`** — `startDate must be within the last six months` |

**The rule is `startDate >= today.AddMonths(-6)`.** Clamp with `AddMonths(-6)`; **never** with a day
count. (183 days before 2026-08-24 is 2026-02-22 — already rejected; 180 days is 2026-02-25 —
needlessly tight. Neither day count reproduces the boundary, and the error is off-by-one *in the
failing direction* for the calendar-accurate cases.)

- **`myTrades` has no history limit** — the PDF states it *"will provide data from all time"*, and
  the 180-day probe (`2026-02-24 .. 2026-08-24`) succeeded with no history complaint. Only the row
  cap applies.
- **Add a +1 day safety margin** (`today.AddMonths(-6).AddDays(1)`) on `allTrades`/`settlements`.
  The vendor computes "today" in **its own** zone (Modern Commodities is in **Calgary, Alberta** —
  the PDF footer gives `400, 119 14 St NW | Calgary | AB | T2N 1Z6`, i.e. Mountain Time), so a
  UTC-vs-Mountain rollover can otherwise produce a **guaranteed 400** for several hours a day. One
  day of margin costs nothing and removes an entire class of scheduled failure.

### 5.4 Other date behaviours (all verified)

| Condition | Result | Design note |
|---|---|---|
| `endDate` in the **future** | **`200`** — accepted, treated as a harmless upper bound | A work-unit generator that runs slightly ahead of the clock is safe. UTC-based windowing is therefore fine (UTC is ahead of both Central and Mountain, so it can never clip a just-published row). |
| `startDate` **after** `endDate` | **`200` with a header-only body** — **not** an error | ⚠ An inverted range fails **silently as an empty success**. A window-computation bug will not raise; it will quietly load zero rows forever. Assert `startDate <= endDate` in the loader and treat a violation as a bug. |
| Both dates omitted | `200` for the vendor's "today" | Avoid — see §4. |

### 5.5 "Today" is the vendor's today, not yours

Both date parameters default to *today*, and the 6-month clamp is computed relative to *today* — but
that "today" is evaluated **server-side, in the vendor's zone (Mountain)**. Nothing in the response
states which date the server used, and there is no `meta`/as-of field anywhere in any payload. Do
not build any logic on the server's clock: **always send explicit dates**, and keep the +1 day
history margin of §5.3.

---

## 6. Limits and reachability arithmetic

### 6.1 The two row caps

| Endpoint | Cap per request | Source |
|---|:--:|---|
| `allTrades`, `myTrades` | **10,000 rows** | PDF, and the live `400` body quotes `10000` |
| `settlements` | **100,000 rows** | PDF |

An over-cap request is **rejected with `400`** — it does **not** return a truncated page. Since
there is no paging (§1), **the only way to stay under the cap is to narrow the date range**, i.e.
to chunk the window. Keep `ChunkDays` configurable.

### 6.2 Trades: the cap is genuinely reachable, and the daily rate is **not** uniform

Directly observed cap probes:

| Window length | Result |
|---|---|
| **170 days** | **`200`, 9,849 rows** — 98.5% of the cap |
| **172 days** | **`400`** — `Request returns more than the limit of 10000 rows. Please apply a more granular filter` |

So the `allTrades` boundary sits at **≈171 days**, and **a backfill deeper than ~170 days must be
chunked.** This is a real requirement, not a theoretical one.

> **Provenance note (stated plainly):** the two probes above are transcribed from the probe record
> in `MODCOM_FACTS.md`. The 170-day capture file (`at170.csv`) referenced in the briefing is
> **not present in the artifact directory**, so the 9,849 figure could not be *recounted* in this
> pass — unlike every other number in this document, which was recounted from a file on disk. The
> 400 body itself is independently verified (§7.1). Treat 9,849 as reliable-but-unrecounted; the
> *conclusion* (the cap is reachable at ~171 days) is confirmed by the 172-day 400 regardless.

**⚠ Do not extrapolate a per-day rate from a short window — the two measurements disagree by ~3×:**

| Measurement basis | Rows/calendar day |
|---|:--:|
| 170-day pull (9,849 rows) | **≈ 57.9** |
| 5-day pull (98 rows, `2026-08-20..24`) | **≈ 19.6** |

The 5-day window contained a weekend and a partial final day, and the window filters on
*last-updated* activity rather than executions — but even normalising for that, the rates do not
reconcile (§13 #1). The practical rule: **size chunks against the 170-day measurement (the one that
actually approached the cap), never against a recent short sample.**

Derived headroom at the ≈58/day rate:

| Window | Estimated rows | % of 10,000 cap |
|---|:--:|:--:|
| **30 days (the standing default)** | ≈ 1,740 | **17%** — safe |
| 90 days | ≈ 5,200 | 52% |
| 170 days | 9,849 (observed) | **98.5%** |
| 172 days | — | **over cap → 400** |

`myTrades` is trivial by volume: **37 rows over 180 days**. Its cap is unreachable in practice, and
its all-time history is available — which is why a per-endpoint `DaysBack` override is worth having
(it leaves a documented path to a deep `myTrades` backfill with no code change).

### 6.3 Settlements: the cap is **reachable** over the full history — correcting the briefing

Recounted in this pass: 1,443 rows across **2 published settlement dates** = **≈ 721 rows per
published date** (720 and 723 — the product × term grid shifts slightly day to day). The naive
`1443 / 5 calendar days ≈ 289` figure quoted in the briefing **understates the real per-publication
volume by ~2.5×**, because 3 of the 5 requested days published nothing (§5.2).

Correct arithmetic, per **business** day:

| Window | Business days (approx) | Estimated rows | % of 100,000 cap |
|---|:--:|:--:|:--:|
| 5 calendar days (observed) | 2 published | **1,443** (observed) | 1.4% |
| **30 days (the standing default)** | ≈ 21 | ≈ **15,150** | **15%** — safe |
| 90 days | ≈ 63 | ≈ 45,400 | 45% |
| **6 months (the full allowed history)** | ≈ **130** | ≈ **93,700** | **≈ 94% — WITHIN 6% OF THE CAP** |

> **⚠ Correction to the briefing:** the settlements 100,000-row cap is **not** "unreachable inside
> the allowed history." A single full-6-month settlements request lands at roughly **94% of the
> cap**, and the curve depth is not fixed — the observed grid already runs out to **`DEC-31`** term
> months (§11.2), and any product addition or curve extension trips it. **A full-history settlements
> backfill must be chunked**, on the same footing as the trades backfill. The 30-day standing
> window remains comfortably safe.

### 6.4 What is *not* limited

No observed limit on: number of requests per run (beyond the unquantified rate limit), response
size (140 KB observed, no compression negotiated), or concurrent requests. **No `429` was ever
observed** across the probe session.

---

## 7. Status contract and the error matrix

### 7.1 The four distinct `400` bodies and the empty `401` — quoted verbatim

Bodies are **`text/plain`**, a bare sentence, **no JSON envelope, no error code, no trailing
punctuation**. All five rows were provoked individually and observed live.

| # | Condition | Status | Body (verbatim) |
|:-:|---|:---:|---|
| 1 | `startDate` earlier than `today.AddMonths(-6)` (`allTrades` / `settlements`) | **400** | `startDate must be within the last six months` |
| 2 | Request would return more rows than the endpoint's cap | **400** | `Request returns more than the limit of 10000 rows. Please apply a more granular filter` |
| 3 | Unparseable date value | **400** | `Invalid startDate` |
| 4 | Invalid `legalEntityName` (`myTrades`) | **400** | `Invalid legalEntityName, valid options: "ARM Energy Management, LLC", "ARM Energy Management Canada ULC"` |
| 5 | Wrong / revoked credentials | **401** | *(empty body — zero bytes)* |

Notes on each:

- **#2** interpolates the applicable cap into the message, so the settlements form reads
  `…limit of 100000 rows…`. Match on the **stable substring** `more than the limit of`, not on the
  whole sentence.
- **#3** names the offending parameter, so an `endDate` failure presumably reads `Invalid endDate`
  (only the `startDate` form was provoked — §13 #4).
- **#4** is genuinely useful: the error body **enumerates the valid `legalEntityName` values**, so
  it doubles as the discovery mechanism for that parameter. The two ARM legal entities are
  `ARM Energy Management, LLC` and `ARM Energy Management Canada ULC`. Note the **comma inside the
  first name** — it must be URL-encoded in the query string.
- **#5** returning an **empty body** means there is nothing to log but the status. Do not attempt to
  parse or quote the body of a 401.

### 7.2 ⚠⚠ The history-cap and row-cap messages MASK ONE ANOTHER

A request can violate **both** limits at once — e.g. a 200-day `allTrades` window that also starts
more than 6 months back. In that case the response is `400` with the **row-cap** message (#2), even
though the start date is *also* out of range. The reverse ordering was not observed.

**This already caused one wrong conclusion.** An early reading of the API concluded "the history
limit is 183 days," because a wide request failed — but it failed on the **row cap**, not on
history. The real history limit is 6 calendar months ≈ 183 days *by coincidence of magnitude*,
which is exactly what made the misreading plausible (§5.3).

**Requirements this imposes on the loader:**

1. **Always log the response body of a non-2xx.** A bare "HTTP 400" is not actionable; the four
   causes have four different fixes.
2. **Classify the row-cap 400 distinctly** and fail with a message naming the remedy (*reduce
   `ChunkDays`*). Do not lump it in with the other 400s.
3. **Never infer one limit from the other's failure.** Fixing a row-cap 400 by moving `startDate`
   forward "works" for the wrong reason and hides the real constraint.

### 7.3 A legitimate "no data" is a **`200` with a header-only body** — there is no legitimate non-2xx

**Verified:** `myTrades` for `2026-08-20 .. 2026-08-24` returned **HTTP 200**,
`Content-Type: text/csv`, `Content-Length: 498` — **the 34-column header line and nothing else, zero
data rows.** The company simply did no trades in that window.

| Response | Meaning | Loader must |
|---|---|---|
| **`200` + header + ≥1 data row** | Data | Parse and load. |
| **`200` + header only (0 data rows)** | **Legitimate empty read.** Nothing to report for this window. | **Succeed.** Write the `arm.FileLog` audit row (path, HTTP 200, `RowCount = 0`), write zero fact rows, **complete the work unit as success**. Never a failure, never a retry, never an alert. Also the correct outcome for an inverted range (§5.4) and for a weekend-only settlements window (§5.2). |
| **`200` + zero bytes / missing header** | Never observed | Treat as a **failure** — the header is invariant, so its absence means something is wrong. |
| **any non-2xx** | **A real failure.** | **Throw.** Log the status *and* the body. |

> ### ⚠ Explicit contrast with NGI — do not copy NGI's 404 tolerance here
>
> **NGI:** `404` is the *normal* case — a monthly feed queried per calendar day, so roughly
> **58 of every 60** work units legitimately 404, and a loader that failed on 404 would fail ~97%
> of its work.
>
> **ModernCommodities: there is NO legitimate non-2xx status.** Emptiness is expressed *inside* a
> `200`, as a header-only body. Every 400 and every 401 in §7.1 is either **our bug** (a malformed
> date, an over-wide window, an inverted range) or a **configuration failure** (a bad credential).
>
> Both halves of this matter:
> - **Do not tolerate a 404/400 as "no data"** — that would swallow a real defect (e.g. a chunking
>   bug that silently loads nothing because every request is over cap).
> - **Do not treat a header-only 200 as a failure** — that would fail every genuinely quiet
>   `myTrades` window and every weekend settlements window.
>
> Because a header-only 200 *succeeds*, note the interaction with the resume key: a **settled** key
> would record that window done forever. §5.1 #4 already rules out a settled zone for this loader;
> this is a second, independent reason for the same decision.
>
> ⚠ **A `404` on the endpoint path itself was never observed and is not a data condition.** If one
> appears, it means the path or version segment is wrong — a deployment error. Fail loudly.

---

## 8. Parse traps

Six hazards. Each of them produces **no exception** — the failure mode is corrupt or all-NULL rows
on an HTTP 200, the class of bug this repo has already shipped three times.

### 8.1 Every field is double-quoted, and fields contain legitimate embedded commas

Every value in every row of all three endpoints is wrapped in `"` — including empty values, which
appear as `""`. Five columns were observed carrying **commas inside the quotes**:
`Bid Legal Name`, `Offer Legal Name`, `Bid Address`, `Offer Address`, `Contract Terms`.

Real observed examples (from `my180.csv`):

```
"P.O. Box 2844, 150 - 6 Avenue SW, Calgary, AB T2P 3E3"      ← 4 embedded commas, 53 chars
"1001 Fannin Street, Suite 1500, Houston, TX 77002"          ← 3 embedded commas
"1100 Louisiana St., Suite 2700 Houston, TX 77002"           ← comma + embedded periods
"ARM Energy Management, LLC"                                 ← a comma inside a LEGAL NAME
```

**A naive `line.Split(',')` on the first example produces 4 extra columns and shifts every
subsequent value left**, corrupting `Bid Commission` through `Product Type` on that row — silently.
Use an **RFC 4180** tokenizer. Reassuringly, **no field in any capture contains a `"` or an embedded
newline**, so quote-escaping (`""`) and multi-line records were never exercised — but a compliant
parser handles both anyway and should be preferred over a hand-rolled one.

### 8.2 ⚠⚠ Header names contain spaces, `&` and `/` — bind by literal name only

**24 of the 34** trades headers contain at least one space. Two contain an ampersand. One contains
a slash:

```
"Pipeline/Terminal"        ← slash
"GT&C"                     ← ampersand, no spaces
"Click & Trade"            ← ampersand AND spaces
"Apportionment Protected"  ← longest header, 23 chars
"Last Updated Timestamp"   ← 22 chars
```

**No `System.Text.Json`/CSV naming policy matches these.** A property named `PipelineTerminal`,
`GTandC` or `ClickAndTrade` binds to **nothing** — camelCase yields `pipelineTerminal`, snake_case
yields `pipeline_terminal`, and neither is `Pipeline/Terminal`. This is precisely the trap that
produced a silently all-NULL table for NGI (`"Point Code"`, `"Issue Date"`).

**Requirement: bind every column by its exact literal header string** (exact spelling, exact single
spaces, exact casing, exact `&` and `/`) — via a header→index map built from the first line, or
explicit attribute mapping. **Never** by implicit POCO name convention, and **never by ordinal
position alone** (position is documented here so the TVP can be ordered correctly, but the parser
should locate columns by name so an inserted vendor column is detected rather than absorbed).

A `CODE_TESTER` contract test should assert the header line **verbatim** for both shapes — that is
the only alarm that will fire if Modern Commodities renames or inserts a column.

### 8.3 ⚠⚠ Timestamps are **12-hour with AM/PM**, and carry no timezone

`Executed Timestamp` and `Last Updated Timestamp` arrive as:

```
2026-08-24 01:44:41 PM        ← 13:44:41
2026-08-20 08:10:38 AM        ← 08:10:38
2026-08-06 12:42:37 PM        ← 12:42:37 (noon hour — the classic 12 vs 00 trap)
```

Exact format string: **`yyyy-MM-dd hh:mm:ss tt`**, parsed with
**`CultureInfo.InvariantCulture`**. Two ways this goes silently wrong:

- **An `HH` (24-hour) format string** fails or mis-parses **every afternoon timestamp**. Roughly a
  third of the observed rows are `PM`.
- **A bare `DateTime.Parse`** on a non-US locale will not recognise `PM` as a designator and may
  parse `2026-08-24 01:44:41 PM` as 01:44:41 — losing 12 hours. On the `12:xx PM` rows the error is
  subtler still.

**No timezone offset is supplied**, and no timezone is stated anywhere in the PDF or the payload.
The vendor is in Calgary (Mountain); the values are presumably venue-local, but **this is not
verified** (§13 #3). **Store the value exactly as given — `DATETIME2(0)` — and do not shift it.**
Seconds precision only; no fractional seconds were ever observed.

`Term Start`, `Term End` and `Settlement Date` are plain `yyyy-MM-dd` → `DATE`.

### 8.4 Negative prices are normal, not an error

`Price` is signed and frequently negative — these are location/quality **differentials**, not
absolute prices:

| Where | Negative rows | Examples |
|---|:--:|---|
| `allTrades` `Price` | **21 of 98** | `-16.65`, `-14.40`, `-10.50`, `-2.96`, `-0.30` |
| `myTrades` `Price` | **13 of 37** | `-1.75`, `-2.95`, `-0.15` |
| `settlements` `Price` | **1,071 of 1,443** (74%) | `-14.30`, `-16.80`, `-3.00` |

**Never put a `CHECK (Price >= 0)` constraint on any price column**, and never treat a leading `-`
as a parse failure. (Contrast the settlements `-` *sentinel*, §8.6 — a lone `-` in a **text** column
is a sentinel; a `-` **prefixing digits** in a numeric column is a sign.)

### 8.5 Booleans are the literal strings `True` / `False`, or blank

Three columns are boolean-valued: `Apportionment Protected`, `In Index`, `Click & Trade`. They carry
the exact strings `True` or `False` (initial capital, no quotes beyond the CSV quoting), or an empty
value.

| Column | `allTrades` (98 rows) | `myTrades` (37 rows) |
|---|---|---|
| `Apportionment Protected` | `False` × 98 — **`True` never observed** | `False` × 37 |
| `In Index` | `True` × 11, `False` × 87 | `True` × 22, `False` × 15 |
| `Click & Trade` | **blank × 98** (anonymised, §9) | `False` × 37 — **`True` never observed** |

Parse case-insensitively against `True`/`False`; **blank → NULL, not `false`**. Conflating blank
with `false` destroys the anonymisation signal on `Click & Trade` (all 98 `allTrades` rows would
become "not a click trade" instead of "unknown").

### 8.6 ⚠⚠ Settlements uses a literal `-` sentinel — and it is INSIDE the primary key

In `settlements`, an absent `Location` / `Pipeline/Terminal` is the **literal one-character string
`-`**, never blank. Recounted this pass:

| Fact | Value |
|---|---|
| Rows with `Location` = `-` | **82 of 1,443** |
| Rows with `Pipeline/Terminal` = `-` | **82 of 1,443** |
| Rows with **both** = `-` | **82** — the two **always** co-occur; neither was ever `-` alone |
| Rows with **any** empty (`""`) field, any column | **0** — settlements contains **zero** blanks |
| `Product` on all 82 rows | **`Sweet Guernsey Blend`** — a single blended product with no one physical location or pipeline (41 term months × 2 settlement dates) |

Example row:

```
"2026-08-21","Sweet Guernsey Blend","-","-","WTI CMA","AUG-26","2026-08-01","2026-08-31","5.05"
```

> **Never normalise `-` to NULL or to an empty string.** `Location` and `PieplineTerminal` are both
> **`NOT NULL` columns inside the 6-column primary key** of `arm.Settlements`. Rewriting `-` breaks
> the key; rewriting it *inconsistently* (some rows `-`, some `''`) creates duplicate logical rows
> that the MERGE cannot reconcile. **Persist `-` verbatim.**

Belt-and-braces: settlements key columns were **never** blank in 1,443 rows, but a blank in any key
column means the row cannot be keyed — **drop it and count the drop** rather than inserting an
empty-string key. Same rule for a blank or unparseable `Trade Number` on the trades endpoints.

### 8.7 Data-integrity observations (all clean, but still guard)

| Check | Result |
|---|---|
| Ragged rows (field count ≠ header count) | **0** across all three captures |
| Duplicate `Trade Number` within a window | **0** (`allTrades` 98 distinct / 98; `myTrades` 37/37) |
| Duplicate settlements 6-column PK | **0** across 1,443 rows |
| Embedded `"` or newline in any field | **0** |
| Non-ASCII characters | **0** |

House convention still applies: **dedup last-wins on the merge key in the proc** (`ROW_NUMBER`)
before the `MERGE`. A clean sample is not a contract, and §5.1 #5 shows the same key legitimately
arriving twice from different chunks.

---

## 9. The anonymisation matrix — why two tables exist

`allTrades` and `myTrades` return the **same 34 columns**. What differs is *which columns carry
values*: `allTrades` is the **anonymised market tape**, so the entire counterparty block is blanked.

**Verified in this pass by direct field-position count** over all 98 `allTrades` rows and all 37
`myTrades` rows.

### 9.1 The 14 anonymised columns

Blank in **100% of `allTrades` rows** and populated in `myTrades`:

| # | Column | `allTrades` | `myTrades` | What `myTrades` carries |
|:-:|---|:--:|:--:|---|
| 16 | `Side` | **0 / 98** | 37 / 37 | `Buy` / `Sell` — the company's side |
| 17 | `Bid Trader` | **0 / 98** | 37 / 37 | Individual trader name (**PII**), max 19 chars |
| 18 | `Bid Legal Name` | **0 / 98** | 37 / 37 | Counterparty legal entity, max 41, **contains commas** |
| 19 | `Bid Address` | **0 / 98** | 37 / 37 | Street address, max 53, **contains commas** |
| 20 | `Bid Commission` | **0 / 98** | **16 / 37** | Decimal (`0.00` / `0.01`) — **sparse even in `myTrades`** |
| 21 | `Offer Trader` | **0 / 98** | 37 / 37 | Individual trader name (**PII**) |
| 22 | `Offer Legal Name` | **0 / 98** | 37 / 37 | Counterparty legal entity, max 41 |
| 23 | `Offer Address` | **0 / 98** | 37 / 37 | Street address, max 53 |
| 24 | `Offer Commission` | **0 / 98** | **21 / 37** | Decimal — **sparse even in `myTrades`** |
| 28 | `Settlement Currency` | **0 / 98** | 37 / 37 | `USD` (only value observed) |
| 29 | `Contract Terms` | **0 / 98** | 37 / 37 | Governing legal entity, max 41, **contains commas** |
| 30 | `GT&C` | **0 / 98** | 37 / 37 | General terms & conditions ref., max 41 |
| 31 | `Notes` | **0 / 98** | **3 / 37** | Free text — **very sparse even in `myTrades`** |
| 33 | `Click & Trade` | **0 / 98** | 37 / 37 | `False` (only value observed) |

### 9.2 ⚠ Correction to the briefing: `Clearing ID` is **not** an anonymised column

`Clearing ID` (position 27) was listed in the briefing among the anonymised columns. It is not:

| Column | `allTrades` | `myTrades` |
|---|:--:|:--:|
| `Clearing ID` | **0 / 98 populated** | **0 / 37 populated** |

It is **blank in 100% of both endpoints**. Verified twice, by two independent field-position greps
against `my180.csv`. It therefore belongs in its own bucket — **"never observed populated anywhere"**
— and must **not** be used as an anonymisation-baseline column: a validation rule expecting
`Clearing ID` to be populated in `arm.MyTrades` would fail on every row.

Keep the column (it is in the user's DDL, and the venue clearly intends to populate it for cleared
trades — none appeared in the sampled windows), but treat it as **expected-NULL on both tables**.

### 9.3 Why this drives the design

1. **This is the entire reason two tables exist.** Same shape, different disclosure level. The user's
   DDL keeps the **full 34-column set on both tables** — `arm.AllTrades` simply carries NULLs in the
   14 columns above (15 counting `Clearing ID`). **Do not "optimise" those columns away**: the venue
   could begin populating some of them, and a narrower `AllTrades` would then silently discard data.
2. **This is the expected-NULL baseline for validation.** `usp_ValidateLoad` should report the 14
   columns as **informational** for `arm.AllTrades` — 100% NULL there is *correct*, not a defect.
   Conversely, a sudden *drop* in population on `arm.MyTrades` is worth a warning.
3. **Do not build a "price implies counterparty" or "commission is always present" rule.**
   `Bid Commission` (16/37) and `Offer Commission` (21/37) are complementary and mutually exclusive
   in the sample — `16 + 21 = 37`, with no row carrying both and no row carrying neither. The
   commission appears on **the company's own side only**; the counterparty's side is blank. This is
   an observation over 37 rows, not a contract — but any DQ rule requiring both must not ship.
4. **`Notes` (3/37) and the addresses/trader names are PII and commercially sensitive.** Committed
   test fixtures must be **anonymised**, preserving shape only: keep a comma-laden quoted address, a
   `PM` timestamp, a blank commission, a negative price and the `-` sentinel — with **invented**
   names and addresses. The raw captures must not be committed.

---

## 10. Dataset 1 & 2 — the trades shape (34 columns)

**Applies identically to `allTrades/v1` and `myTrades/v1`.** The header is byte-identical
(497 bytes) and the column order is identical.

### 10.1 Header line, verbatim

```
"Trade Number","State","Product","Location","Pipeline/Terminal","Price Basis","Term","Term Start","Term End","Price","Volume","Unit of Measure","Executed Timestamp","Last Updated Timestamp","Trade Type","Side","Bid Trader","Bid Legal Name","Bid Address","Bid Commission","Offer Trader","Offer Legal Name","Offer Address","Offer Commission","Spread Trade Number","Apportionment Protected","Clearing ID","Settlement Currency","Contract Terms","GT&C","Notes","In Index","Click & Trade","Product Type"
```

### 10.2 Full field reference — all 34 columns in exact source order

**Source order IS the TVP order.** `#` is the 1-based ordinal position in the CSV.
`A` = `allTrades` populated / 98 rows. `M` = `myTrades` populated / 37 rows.
Max length / precision figures are **observed maxima**, not vendor-declared limits.

| # | Source header (verbatim) | Inferred type | Observed max | A | M | Blank / NULL behaviour | → Target column | Target SQL type |
|:-:|---|---|---|:-:|:-:|---|---|---|
| 1 | `Trade Number` | integer | 5 digits (`57112`–`68043`) | 98 | 37 | **Never blank.** A blank/unparseable value makes the row unkeyable → **drop and count**. | `TradeNumber` | `INT` **NOT NULL** — **PK** |
| 2 | `State` | enum text | 9 (`Finalized`) | 98 | 37 | Never blank. 2 values: `Finalized`, `Cancelled` (A: 95/3, M: 35/2). A revision can flip this — see §5.1. | `State` | `VARCHAR(50)` NULL |
| 3 | `Product` | text | **28** (`LT SW-Ft Laramie/LT SW-Guern`) / M: 10 | 98 | 37 | Never blank. On a spread row, the two legs are joined with `/`. | `Product` | `VARCHAR(50)` NULL |
| 4 | `Location` | text | **21** (`Fort Laramie/Guernsey`) / M: **23** (`Clearbrook/Beaver Lodge`) | **79** | 37 | **Blank on 19/98** — exactly the `Product Type = Financial` rows (§10.4 #1). `/`-joined on spreads. | `Location` | `VARCHAR(50)` NULL |
| 5 | `Pipeline/Terminal` | text | **19** (`Magellan/Enterprise`) / M: 9 | **79** | 37 | **Blank on the same 19 Financial rows.** ⚠ Header contains a **slash**. | `PipelineTerminal` | `VARCHAR(256)` NULL |
| 6 | `Price Basis` | text | **45** (`WTI CMA + ARGUS WTI CMA Diff + ARGUS MEH Diff`) / M: 26 | **79** | 37 | **Blank on the same 19 Financial rows.** Free text; ` + `-joined index expression. | `PriceBasis` | `VARCHAR(256)` NULL |
| 7 | `Term` | text code | **13** (`AUG-26/SEP-26`, `OCT-26~NOV-26`) | 98 | 37 | Never blank. **4 shapes**: `MMM-YY`; quarterly `nQ-YY` (`1Q-27`, `4Q-26`); spread `MMM-YY/MMM-YY`; range `MMM-YY~MMM-YY`. ⚠ **Two different separators, `/` and `~`** — do not assume one. | `Term` | `VARCHAR(256)` NULL |
| 8 | `Term Start` | date | 10, `yyyy-MM-dd` | 98 | 37 | Never blank in either capture. Always the 1st of a month. | `TermStart` | `DATE` NULL |
| 9 | `Term End` | date | 10, `yyyy-MM-dd` | 98 | 37 | Never blank. Always a month end (`2028-02-29` observed — leap-year correct). Up to `2027-03-31` in the trades captures. | `TermEnd` | `DATE` NULL |
| 10 | `Price` | decimal, **signed** | 2 int digits + **exactly 2 dp** (max `89.05`, min `-16.65`) | 98 | 37 | Never blank. **Negative in 21/98 and 13/37** (§8.4). `0.00` is a real value, not a null. | `Price` | `DECIMAL(9,2)` NULL |
| 11 | `Volume` | integer | **6 digits — max `300000`**; **no decimal point ever observed** / M: max `1000` | 98 | 37 | Never blank. Unit given by column 12. | `Volume` | `DECIMAL(9,2)` NULL — see §12 #3 |
| 12 | `Unit of Measure` | enum text | **15** (`contracts/month`) | 98 | 37 | Never blank. 4 values observed: `m3/month`, `bbls/day`, `bbls/month`, `contracts/month`. Header contains **spaces**. | `UnitOfMeasure` | `VARCHAR(50)` NULL |
| 13 | `Executed Timestamp` | datetime, **12-hour** | 22 (`2026-08-24 01:44:41 PM`) | 98 | 37 | Never blank. **`yyyy-MM-dd hh:mm:ss tt`, invariant culture, no timezone** (§8.3). **Not** the window predicate (§5.1). | `Executed` | `DATETIME2(0)` NULL |
| 14 | `Last Updated Timestamp` | datetime, **12-hour** | 22 | 98 | 37 | Never blank. Same format. **THIS is the window predicate** (§5.1) and the recency guard for the MERGE. Longest header (22 chars). | `LastUpdated` | `DATETIME2(0)` NULL |
| 15 | `Trade Type` | enum text | 10 (`Second Leg`) | 98 | 37 | Never blank. 4 values: `Outright`, `Spread`, `First Leg`, `Second Leg` (A: 14 `Spread` rows → §10.4 #2). | `TradeType` | `VARCHAR(50)` NULL |
| 16 | `Side` | enum text | 4 (`Sell`) | **0** | 37 | **ANONYMISED in `allTrades` (0/98).** `Buy` / `Sell` in `myTrades`. | `Side` | `VARCHAR(256)` NULL |
| 17 | `Bid Trader` | text (**PII**) | 19 (`Christopher Clement`) | **0** | 37 | **ANONYMISED (0/98).** Individual's name. | `BidTrader` | `VARCHAR(256)` NULL |
| 18 | `Bid Legal Name` | text | **41** (`Marathon Petroleum Supply and Trading LLC`) | **0** | 37 | **ANONYMISED (0/98).** ⚠ **Contains commas** (`ARM Energy Management, LLC`). | `BidLegalName` | `VARCHAR(256)` NULL |
| 19 | `Bid Address` | text | **53** (`P.O. Box 2844, 150 - 6 Avenue SW, Calgary, AB T2P 3E3`) | **0** | 37 | **ANONYMISED (0/98).** ⚠ **Up to 4 embedded commas** (§8.1). | `BidAddress` | `VARCHAR(256)` NULL |
| 20 | `Bid Commission` | decimal | 4 (`0.01`, `0.00`) | **0** | **16** | **ANONYMISED (0/98) and sparse in `myTrades` (16/37).** Populated only on the company's own side (§9.3 #3). Blank → NULL. | `BidCommission` | `DECIMAL(9,2)` NULL |
| 21 | `Offer Trader` | text (**PII**) | 19 | **0** | 37 | **ANONYMISED (0/98).** | `OfferTrader` | `VARCHAR(256)` NULL |
| 22 | `Offer Legal Name` | text | **41** | **0** | 37 | **ANONYMISED (0/98).** Contains commas. | `OfferLegalName` | `VARCHAR(256)` NULL |
| 23 | `Offer Address` | text | **53** | **0** | 37 | **ANONYMISED (0/98).** Contains commas. | `OfferAddress` | `VARCHAR(256)` NULL |
| 24 | `Offer Commission` | decimal | 4 | **0** | **21** | **ANONYMISED (0/98) and sparse (21/37).** Complementary to #20 (`16 + 21 = 37`). | `OfferCommission` | `DECIMAL(9,2)` NULL |
| 25 | `Spread Trade Number` | numeric-looking **text** | 5 digits | **42** | 3 | **NOT anonymised** — populated in 42/98 of `allTrades`. Blank on `Outright` rows. On a `Spread` row it equals the row's **own** `Trade Number`; on the legs it points to the parent (§10.4 #2). ⚠ Stays **text** per the user's DDL — do not "fix" to `INT`. | `SpreadTradeNumber` | `VARCHAR(50)` NULL |
| 26 | `Apportionment Protected` | boolean text | 5 | 98 | 37 | Never blank. **`False` in 98/98 and 37/37 — `True` never observed.** Longest header (23 chars). | `ApportionmentProtected` | `BIT` NULL |
| 27 | `Clearing ID` | text | **0 — never populated** | **0** | **0** | ⚠ **Blank in 100% of BOTH endpoints** (§9.2). **Not** an anonymisation column. Expected-NULL on both tables. | `ClearingID` | `VARCHAR(50)` NULL |
| 28 | `Settlement Currency` | text | 3 (`USD`) | **0** | 37 | **ANONYMISED (0/98).** Only `USD` observed — do **not** constrain to it (a Canadian venue may settle `CAD`). | `SettlementCurrency` | `VARCHAR(50)` NULL |
| 29 | `Contract Terms` | text | **41** | **0** | 37 | **ANONYMISED (0/98).** The legal entity whose contract terms govern — usually but **not always** the counterparty (2 of 37 rows name `ARM Energy Management, LLC`). Contains commas. | `ContractTerms` | `VARCHAR(256)` NULL |
| 30 | `GT&C` | text | **41** (`Conoco 2017 GTCs and HMSC Oct 2017 Amends`) | **0** | 37 | **ANONYMISED (0/98).** ⚠ Header contains an **ampersand and no space** — `GT&C`. | `GTandC` | `VARCHAR(256)` NULL |
| 31 | `Notes` | free text | **31** (`deal completed with open credit.`) | **0** | **3** | **ANONYMISED (0/98) and very sparse (3/37).** Free-form operator note. | `Notes` | `VARCHAR(8000)` NULL |
| 32 | `In Index` | boolean text | 5 | 98 | 37 | Never blank. A: `True` 11 / `False` 87. M: `True` 22 / `False` 15. Header contains a **space**. | `InIndex` | `BIT` NULL |
| 33 | `Click & Trade` | boolean text | 5 | **0** | 37 | **ANONYMISED (0/98).** `False` in all 37 `myTrades` rows — `True` never observed. ⚠ Header contains **ampersand AND spaces**. Blank → NULL, **not** `false` (§8.5). | `ClickAndTrade` | `BIT` NULL |
| 34 | `Product Type` | enum text | 9 (`Financial`) | 98 | 37 | Never blank. 2 values: `Physical` (A: 79, M: 37), `Financial` (A: 19, M: **0**). **The discriminator for #4/#5/#6 blankness** (§10.4 #1). | `ProductType` | `VARCHAR(50)` NULL |

**Column count: 34. Every column is documented. None dropped.**

**Not in the payload — loader/DB-supplied:** `ModifiedAtUtc DATETIME2(3)` (stamped by the merge
proc; **never crosses the TVP**). Note what is *absent* by the user's design: **no surrogate `Id`,
no `DateCreated`, and no `FileLogId`** on these tables — so the TVP begins with `TradeNumber`, the
first payload column, breaking the house "`FileLogId` is column 1" convention. That is intentional
and provenance lives in `arm.FileLog` alone (§12 #1).

### 10.3 Observed value inventory (snapshots, **not** contracts)

| Column | Distinct values observed |
|---|---|
| `State` | `Finalized`, `Cancelled` |
| `Trade Type` | `Outright`, `Spread`, `First Leg`, `Second Leg` |
| `Product Type` | `Physical`, `Financial` |
| `Unit of Measure` | `m3/month`, `bbls/day`, `bbls/month`, `contracts/month` |
| `Side` (`myTrades`) | `Buy`, `Sell` |
| `Settlement Currency` (`myTrades`) | `USD` |
| `Apportionment Protected` | `False` only |
| `Click & Trade` (`myTrades`) | `False` only |

**Do not build a lookup table, `CHECK` constraint or C# enum that rejects unknown values on any of
these.** No vendor document enumerates them; a new product type or a `CAD` settlement would then
fail the load. Report new values in `usp_ValidateLoad` informationally instead.

### 10.4 Structural invariants worth knowing (all live-observed)

1. **`Product Type = 'Financial'` ⇒ `Location`, `Pipeline/Terminal` and `Price Basis` are ALL
   blank.** Verified exactly, both directions, on `raw_allTrades.csv`:
   - 19 rows have all three columns blank
   - 19 rows have `Product Type = 'Financial'`
   - **0 rows have the blanks while being `Physical`**
   All 19 also carry `Unit of Measure = 'contracts/month'`, and `contracts/month` appears on no
   other row. So the blanks are **semantic** (a financial contract has no delivery location), not a
   data-quality defect. A "Location must be populated" DQ rule would false-positive on 19% of the
   tape. `myTrades` contained **no** Financial rows at all (37/37 `Physical`), so this pattern is
   invisible there — a validator written only against `myTrades` would miss it.

2. **Spread trades arrive as a 3-row group sharing one `Spread Trade Number`.** 14 `Spread` rows in
   `raw_allTrades.csv` → **42 rows** with a populated `Spread Trade Number` = 14 × 3. Each group is
   one `Spread` (the parent, whose `Spread Trade Number` equals its own `Trade Number`) plus one
   `First Leg` and one `Second Leg` pointing at the parent. Example:

   | `Trade Number` | `Trade Type` | `Spread Trade Number` | `Product` |
   |---|---|---|---|
   | `68029` | `Spread` | `68029` | `SSP/SYN` |
   | `68030` | `First Leg` | `68029` | `SSP` |
   | `68031` | `Second Leg` | `68029` | `SYN` |

   Consequences: **the parent row's `Volume` is not additive with its legs'** — naively summing
   `Volume` over the fact table triple-counts spread volume. And the parent's `Price` is the
   *differential* (`2.25`), while the legs carry outright prices (`18.75`, `16.50`). Flag for
   `DATA_QUALITY_VALIDATOR`; do not filter in the loader — persist as published.

3. **`Term` may itself be a spread or a range**, mirroring #2 at the tenor level:
   `AUG-26/SEP-26` (calendar spread) and `OCT-26~NOV-26` (a range). In both cases `Term Start` and
   `Term End` span the whole thing (`2026-08-01` → `2026-09-30`).

4. **The same `Trade Number` exists independently in `arm.AllTrades` and `arm.MyTrades`** (e.g.
   `66986` appears in `my180.csv`; `66987`–`66989` appear in `raw_allTrades.csv`). There is **no FK
   and no cross-table dedup** — that follows from the user's three-table DDL and is intended. The
   two tables are two *views* of the venue at different disclosure levels, not a parent/child pair.

---

## 11. Dataset 3 — `settlements/v1` (9 columns)

Daily settlement prices: for each `Settlement Date`, a **full forward curve** — one row per
(product, location, pipeline, price basis, **term month**). The 41-term `Sweet Guernsey Blend` block
and the 37-term `Bak-BL` block in the capture show the curve depth.

### 11.1 Header line, verbatim

```
"Settlement Date","Product","Location","Pipeline/Terminal","Price Basis","Term","Term Start","Term End","Price"
```

### 11.2 Full field reference — all 9 columns in exact source order

Counts are over the **1,443 rows** of `raw_settlements.csv`. **Source order IS the TVP order.**

| # | Source header (verbatim) | Inferred type | Observed max / values | Populated | Blank / NULL behaviour | → Target column | Target SQL type |
|:-:|---|---|---|:-:|---|---|---|
| 1 | `Settlement Date` | date | 10, `yyyy-MM-dd`. 2 values: `2026-08-20` (720 rows), `2026-08-21` (723) | 1443 / 1443 | **Never blank.** Business days only; the request day may not yet be published (§5.2). Header contains a **space**. | `SettlementDate` | `DATE` **NOT NULL** — **PK 1/6** |
| 2 | `Product` | text | **22** (`High Tan-Westridge FOB`, `High Tan-Cush/CLK-Cush`) | 1443 / 1443 | **Never blank.** `/`-joined on spread products. | `Product` | `VARCHAR(50)` **NOT NULL** — **PK 2/6** |
| 3 | `Location` | text | **19** (`Nederland/Nederland`) | 1443 / 1443 | **Never blank.** ⚠ **Literal `-` on 82 rows** — persist verbatim, it is in the PK (§8.6). | `Location` | `VARCHAR(50)` **NOT NULL** — **PK 3/6** |
| 4 | `Pipeline/Terminal` | text | **21** (`Enterprise/Enterprise`) | 1443 / 1443 | **Never blank.** ⚠ **Literal `-` on the same 82 rows.** ⚠ Header contains a **slash**. | `PieplineTerminal` **[sic]** | `VARCHAR(50)` **NOT NULL** — **PK 4/6** |
| 5 | `Price Basis` | text | **7**. Only **2** distinct values: `WTI CMA` (1,391 rows), `USD $` (52) | 1443 / 1443 | **Never blank.** ⚠ `USD $` contains a **space and a `$`** — no trimming/stripping. Header contains a space. | `PriceBasis` | `VARCHAR(100)` **NOT NULL** — **PK 5/6** |
| 6 | `Term` | text code | **6** — `MMM-YY` **only** (`AUG-26` … `DEC-31`) | 1443 / 1443 | **Never blank.** ⚠ **Unlike the trades `Term` (max 13, four shapes), the settlements `Term` is always a single month** — no `/`, no `~`, no quarters. Do not share a single `Term` parser/validator across the two shapes without allowing for both. | `Term` | `VARCHAR(50)` **NOT NULL** — **PK 6/6** |
| 7 | `Term Start` | date | 10, `yyyy-MM-dd`; always the 1st | 1443 / 1443 | Never blank in 1,443/1,443 (but the DDL column is `NULL`able — keep it so). | `TermStart` | `DATE` NULL |
| 8 | `Term End` | date | 10; always a month end. **Extends to `2031-12-31`** — 192 rows have terms in the 2030s | 1443 / 1443 | Never blank. Leap years correct (`2028-02-29`). ⚠ Curve depth ≈ **5.4 years forward** — no `DATE` range assumption may be tighter. | `TermEnd` | `DATE` NULL |
| 9 | `Price` | decimal, **signed** | ≤ 2 int digits + **exactly 2 dp** (no 3-dp or 3-int-digit value in 1,443 rows). **Negative in 1,071 / 1,443 (74%)** | 1443 / 1443 | Never blank. `0.00` is a real value. **No non-negative `CHECK`** (§8.4). | `Price` | `DECIMAL(9,2)` NULL |

**Column count: 9. Every column is documented. None dropped.**

**Not in the payload:** `ModifiedAtUtc DATETIME2(3)` (proc-stamped, never in the TVP). No `Id`, no
`DateCreated`, no `FileLogId` — per the user's DDL.

### 11.3 Settlements structural facts

1. **Zero blank fields anywhere** in 1,443 rows × 9 columns. The only "absent value" representation
   is the literal `-` in columns 3 and 4 (§8.6). This is a **cleaner** payload than the trades
   shape, and the 6-column `NOT NULL` PK is safe.
2. **The 6-column PK held with zero duplicates** across 1,443 rows. Still dedup last-wins in the
   proc (house convention; concurrent chunks can deliver the same key twice).
3. **The curve grid is not fixed.** 720 rows on Thursday vs 723 on Friday — the product × term grid
   shifts day to day. Never assume a constant row count per settlement date; base validation on a
   *band*, not an equality.
4. **A settlement price revision would OVERWRITE.** `Price` sits **outside** the PK, so there is no
   history. Contrast OPIS's `arm.LPReportHistory`, which keys on the revision code precisely to keep
   both prints. Fine if settlements are never restated (⚠ unverified — §13 #2); the precedent exists
   if that proves false.
5. **The two `Price Basis` values are not interchangeable with the trades vocabulary.** Settlements
   carries only `WTI CMA` and `USD $`; the trades `Price Basis` runs to 45 characters of compound
   index expression (`WTI CMA + ARGUS WTI CMA Diff + ARGUS MEH Diff`). They are different value
   spaces in a same-named column — do **not** build a shared lookup dimension across the two.

---

## 12. Coverage checklist

| # | Endpoint | Method / path | Auth | Params | Envelope | Paging | Batching | Columns documented | Verified? |
|:-:|---|---|---|---|---|---|---|:--:|---|
| 1 | All Trades | `GET allTrades/v1` | HTTP Basic | `startDate`, `endDate` | flat CSV, 1 header row + N data rows | **none** (hard 10,000-row cap) | none | **34** | **Live-verified** ✅ 98 rows |
| 2 | My Trades | `GET myTrades/v1` | HTTP Basic | `startDate`, `endDate`, `legalEntityName` | flat CSV, byte-identical header to #1 | **none** (10,000 cap) | none | **34** | **Live-verified** ✅ 37 rows + a 0-row 200 |
| 3 | Settlements | `GET settlements/v1` | HTTP Basic | `startDate`, `endDate` | flat CSV | **none** (100,000 cap) | none | **9** | **Live-verified** ✅ 1,443 rows |

**Total documented source columns: 43** (34 trades, shared by two endpoints, + 9 settlements),
mapping to **34 + 34 + 9 = 77** target columns across the three tables.

**Gate status: PASS** — no reconstructed field, no `⚠`-unverified field in any table, nothing
dropped without a stated reason. The "needs one-shot live verification" checklist that
`IHSPointLogic` and `IIR` carry is **empty** for this loader's field sets; §13 lists only
behavioural unknowns.

---

## 13. Notes for `DATABASE_DEVELOPER` (recommendations only — the DDL is the user's)

The three tables come from the **user's own authoritative script**. Reproduce it; the items below
are the calls worth confirming, not a redesign.

### 13.1 Grain and keys

| Target | Role | Natural key | Rationale |
|---|---|---|---|
| `arm.AllTrades` | High-volume fact → **composite/natural PK, no surrogate `Id`** | **`TradeNumber`** (clustered) | One row per trade; a revision arrives as the same `TradeNumber` with a later `Last Updated Timestamp` (§5.1). Unique in 98/98. |
| `arm.MyTrades` | Same | **`TradeNumber`** (clustered) | Unique in 37/37. **No FK to `arm.AllTrades`** — see §10.4 #4. |
| `arm.Settlements` | Same | **`(SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term)`** (clustered) | 0 duplicates in 1,443 rows. All six `NOT NULL`; the `-` sentinel is a legitimate key value (§8.6). |
| `arm.FileLog` | Audit hub | One row **per endpoint pull per run** | The **only** provenance available (§13.2 #1), so pulls must not collapse: keeping the 03:00 pull's 0 rows distinct from the 02:00 pull's 20 rows is the whole point. |

### 13.2 Deviations from house convention, stated deliberately

1. **⚠ No `FileLogId` on any of the three tables.** The user's DDL has no such column, and adding
   one is a schema change they did not ask for. **Accepted cost: no per-row lineage** — so
   `usp_ValidateLoad` checks must be window/`ModifiedAtUtc`-based and **cannot join facts to the
   hub**. Consequence for the TVP contract: the TVPs begin with the **first payload column**
   (`TradeNumber` / `SettlementDate`), breaking the house "`FileLogId` is column 1" rule. This is
   intentional and must be reflected identically in `BuildTable` and in `sql/ModernCommodities/002`
   — the TVP binds **by position**, so a silent divergence corrupts every row.
2. **No `Id IDENTITY` and no `DateCreated`** on any of the three tables. The user's DDL has neither.
   These are fact-shaped tables, so the missing `Id` is conventional; the missing `DateCreated` is
   a deviation — follow the user's DDL rather than adding one.
3. **⚠ `PieplineTerminal` in `arm.Settlements` is MISSPELLED in the user's DDL, and the misspelling
   is part of the PRIMARY KEY.** Reproduce it **exactly** — in the table, the TVP, the proc **and**
   the C# property name (misspelled there too, with a comment), so that nobody later "tidies" the
   C# name and silently breaks the positional TVP binding. The two trades tables spell the same
   concept **correctly** (`PipelineTerminal`), so both spellings coexist on purpose. **Flagged, not
   fixed** — it is the user's schema and may already have downstream consumers.
4. **`Checksum INT` is removed from all three tables** (explicit user decision). The MERGE therefore
   does a straight `WHEN MATCHED` update with no checksum short-circuit.
5. **`ModifiedAtUtc` DEFAULT is `SYSUTCDATETIME()`**, not the user's `SYSDATETIME()`. The column is
   named `…Utc` but `SYSDATETIME()` is server-local. The procs stamp it explicitly on every write,
   so the DEFAULT is effectively unreachable — this only stops the two paths disagreeing in basis.
   **Flag to the user.**

### 13.3 Type calls worth confirming

1. **`DECIMAL(9,2)` not `FLOAT` for `Price`, `Volume`, `BidCommission`, `OfferCommission`** — a
   traded price and a settlement print must round-trip exactly. (`FLOAT` is reserved in this repo
   for genuinely approximate measures such as coordinates; this API returns none.) Observed scale
   is exactly 2 everywhere, in 100% of rows across all three captures.
2. **All price/commission columns must be SIGNED — no `CHECK (>= 0)`.** 74% of settlement prices and
   21% of `allTrades` prices are negative (§8.4).
3. **⚠ `Volume DECIMAL(9,2)` — correcting the briefing's headroom claim.** The briefing records the
   observed max as `10,000`; the actual observed max in `raw_allTrades.csv` is **`300,000`**
   (`TradeNumber 68020`, a `bbls/month` row). `DECIMAL(9,2)` caps at `9,999,999.99`, so **300,000
   still fits comfortably** and the user's type stands — but the real headroom is **~33×, not
   ~1,000×**. That matters because an over-range value is a hard **arithmetic-overflow error**, not
   a truncation: the row fails, and so does the batch. A monthly `bbls/month` cargo deal an order of
   magnitude larger than today's biggest would breach it. **Flag to the user**; `DECIMAL(13,2)`
   would remove the risk entirely at negligible cost. Also note `Volume` has **never** carried a
   decimal point — scale 2 is pure headroom.
4. **`DATETIME2(0)` for `Executed` / `LastUpdated`** — seconds precision only, no fractional seconds
   ever observed, **no timezone supplied**. Store as given; do not shift (§8.3). (`ModifiedAtUtc`
   stays `DATETIME2(3)` per convention.)
5. **`DATE` not `DATETIME2` for `TermStart` / `TermEnd` / `SettlementDate`** — no time component
   exists anywhere in those fields. Note `TermEnd` reaches **`2031-12-31`**, ~5.4 years forward.
6. **`VARCHAR` not `NVARCHAR` throughout** — every observed character in all three captures is
   ASCII. Revisit only if non-US/CA counterparties with accented legal names are onboarded.
7. **Widths are comfortable but not enormous.** Longest observed vs the DDL: trades `Price Basis`
   **45** / `VARCHAR(256)`; `Bid`/`Offer Address` **53** / `VARCHAR(256)`; `Bid`/`Offer Legal Name`,
   `Contract Terms`, `GT&C` **41** / `VARCHAR(256)`; trades `Product` **28** / `VARCHAR(50)`;
   trades `Location` **23** / `VARCHAR(50)`; settlements `Pipeline/Terminal` **21** /
   `VARCHAR(50)`; settlements `Price Basis` **7** / `VARCHAR(100)`. The two tightest ratios are
   `Product` (28/50) and `Location` (23/50) — both are `/`-joined on spread rows, so a
   three-leg product or a longer location pair could approach 50. Worth watching in validation.
8. **`Notes VARCHAR(8000)`** is far larger than the observed max (**31**), which is right — it is
   free operator text with no vendor-stated limit.
9. **`SpreadTradeNumber VARCHAR(50)`** keeps a numeric-looking value as text, per the user's DDL.
   Do not "fix" it to `INT`: the column is a reference, not a measure, and the vendor has not
   documented its domain.
10. **Nullability:** on the trades tables only `TradeNumber` is `NOT NULL`; everything else is
    `NULL`able, which is correct given §9 (14 columns are always NULL in `AllTrades`) and §10.4 #1
    (3 more are NULL on Financial rows). On `arm.Settlements` the six PK columns are `NOT NULL` and
    were never blank in 1,443 rows — safe.

### 13.4 Load-shape notes

- **Three closed, independent pipelines.** No discovery tier, no reference provider, no barrier, no
  FK. A settlements-only or `myTrades`-only run is fully valid. **Do not inherit AGSI's coupling.**
- **Resume key: hot-only, hour granularity** (`RunHour`). **No settled zone** — §5.1 #4 and §7.3
  each independently require it.
- **Merge guard:** trades `WHEN MATCHED` must be gated on last-updated recency
  (`src.LastUpdated IS NULL OR tgt.LastUpdated IS NULL OR src.LastUpdated >= tgt.LastUpdated`) so
  out-of-order chunks cannot regress a revision (§5.1 #5). Settlements has no such column → plain
  last-wins.
- **`usp_ValidateLoad` candidate checks** (uniform `CheckName, Scope, ExpectedCount, ActualCount,
  Detail`; **observational — warn, never throw**):
  - The **14 anonymised columns are 100% NULL in `arm.AllTrades`** — informational, expected (§9.1).
  - **`ClearingID` is 100% NULL in BOTH tables** — informational, expected (§9.2). Never a failure.
  - `Product Type = 'Financial'` ⇒ `Location`/`PipelineTerminal`/`PriceBasis` all NULL (§10.4 #1) —
    report violations both ways.
  - Spread-group integrity: every populated `SpreadTradeNumber` resolves to a `Spread` row in the
    same table; groups of exactly 3 (§10.4 #2). Report, don't enforce.
  - Row count per settlement date within a **band** around ~721 (never an equality — §11.3 #3).
  - Settlements 6-column PK collisions = 0; trades `TradeNumber` collisions = 0.
  - New/unknown values in `State`, `TradeType`, `ProductType`, `UnitOfMeasure`, `Side`,
    `SettlementCurrency` — **report, never fail** (§10.3).
  - Rows whose `LastUpdated` falls outside the requested window (expected 0 — §5.1) and rows whose
    `Executed` falls outside it (expected non-zero — the revision signal).
  - Settlement dates present vs business days in the window (weekend gaps expected — §5.2).
- **Chunking is a real requirement**, not a nicety: `allTrades` trips its cap at ~171 days (§6.2)
  and a full-6-month `settlements` pull sits at ~94% of its cap (§6.3). Keep `ChunkDays`
  configurable on all three endpoints.

---

## 14. Verification status — **zero fields reconstructed**

**Every one of the 43 source columns, every type, every observed maximum, every blank/sentinel
behaviour, every error body, both row caps and the history limit in this document was observed
against the live API on 2026-08-24.** Nothing was inferred from vendor documentation and then
presented as fact.

| Category | Status |
|---|---|
| Column names, order, count (34 + 9) | **Verified live** — recounted from the captured CSVs in this pass |
| Types, observed maxima, decimal scales | **Verified live** — recounted per column position |
| Blank / populated counts, per column, per endpoint | **Verified live** — recounted per column position |
| Anonymisation matrix (14 columns) | **Verified live** — recounted; **the briefing's list corrected** (§9.2) |
| `Last Updated` vs `Executed` window predicate | **Verified live** — all 98 rows' timestamps re-read |
| Settlements window predicate + weekend/current-day gaps | **Verified live** — recounted per settlement date |
| `-` sentinel: 82 rows, always paired, one product | **Verified live** — recounted |
| Error matrix: 4 × `400` bodies + empty `401` | **Verified live** — each provoked individually |
| Header-only `200` = empty success | **Verified live** — `Content-Length: 498`, 0 data rows |
| History limit = exactly 6 calendar months | **Verified live** — boundary probed to the day |
| Row caps + the 172-day `400` | **Verified live** — see the provenance note in §6.2 |
| 170-day row count of **9,849** | **Verified live per the probe record; capture file absent** (§6.2) — the only number in this document not independently recounted |
| Line terminator = LF, no BOM | **Derived** from the verified `Content-Length: 498` by exact byte arithmetic (§1.2) |
| **Reconstructed fields** | **ZERO.** No field, type, or nullability in §10.2 or §11.2 comes from documentation or inference. |

**There is no "needs one-shot live verification" checklist for field sets.** §15 lists only
behavioural unknowns, none of which affect a column, a type or a nullability.

---

## 15. Discrepancies between the briefing artifacts and the sample files

The sample files are ground truth. Four corrections and two sharpenings, all already applied above.

| # | Briefing said | The samples show | Impact |
|:-:|---|---|---|
| 1 | `Clearing ID` is one of **15** columns "blank in allTrades but populated in myTrades" | `Clearing ID` is blank in **0/98 allTrades AND 0/37 myTrades** | **Corrected (§9.2).** The anonymisation matrix is **14** columns. A validator expecting `ClearingID` to be populated in `arm.MyTrades` would fail on every row. |
| 2 | Settlements ≈ **290 rows/day**, so its 100,000 cap is **"unreachable inside the allowed history"** | Only **2 of 5** requested days published; **≈721 rows per published date**. A full 6-month pull ≈ **93,700 rows = ~94% of the cap** | **Corrected (§6.3).** The cap **is** reachable. A full-history settlements backfill must be chunked. `290/day` is a calendar-day average that hides the weekend/unpublished gaps. |
| 3 | `Volume` observed max = **10,000** (the basis for keeping `DECIMAL(9,2)`) | Observed max = **300,000** (`TradeNumber 68020`) | **Corrected (§13.3 #3).** `DECIMAL(9,2)` still fits, so the decision stands — but headroom is **~33×, not ~1,000×**, and an over-range value is a hard overflow error. Re-flagged to the user. |
| 4 | `at170.csv` (9,849 rows) is in the artifact directory | **The file is absent.** Present: `raw_allTrades.csv`, `my180.csv`, `raw_settlements.csv`, `raw_myTrades.csv`, `sample_*.csv`, `hdr_*.txt`, `modcom.txt`, `profile.py` | **Provenance downgraded (§6.2).** The 9,849 figure is transcribed from the probe record, not recounted. The *conclusion* survives: the 172-day `400` independently proves the cap is reachable. |
| 5 | The `-` sentinel marks "absent `Location`/`Pipeline/Terminal` (82 of 1,443 rows)" | Sharper: the two columns are `-` **together** in exactly the same 82 rows (never one alone); **all 82 are `Product = 'Sweet Guernsey Blend'`**; and settlements contains **zero** empty fields anywhere | **Sharpened (§8.6).** The sentinel is a property of one blended product, not scattered noise. |
| 6 | The trades window filters on `Last Updated` (stated for trades only) | Also verified: **settlements filters on `Settlement Date`** and does **not** leak — the two endpoints have **different window predicates** | **Added (§5.2).** Prevents assuming one windowing rule across all three pipelines. |

---

## 16. Open questions / unverified (behavioural only — no field is unknown)

Everything below is operational. None of it affects the column lists, types or nullability in §10.2
and §11.2, which are live-observed.

1. **⚠ The `allTrades` daily row rate is not stable and I cannot reconcile the two measurements.**
   The 170-day pull implies ≈57.9 rows/calendar day; the 5-day pull implies ≈19.6. Even allowing for
   the weekend and the partial final day in the short window, the gap does not close (§6.2).
   Possible causes: seasonal volume, a burst of revisions inside the long window, or the long window
   capturing older trades re-touched later. **Consequence:** size chunks against the long-window
   figure, and re-measure before any deep backfill. Worth a `DATA_QUALITY_VALIDATOR` pass on the
   first live load.
2. **Are settlement prices ever REVISED?** Unverified — each settlement date was captured once. This
   decides whether `arm.Settlements`' overwrite semantics are adequate or whether a history table
   (the OPIS `LPReportHistory` pattern) is needed. The payload carries **no** revision, version,
   status or as-of field, so a restatement would be indistinguishable from the original except by
   comparing values. **Recommendation:** overwrite now; re-open if validation ever sees a value
   change on a settled date. **Worth asking the vendor.**
3. **⚠ The timestamp timezone is not stated anywhere** — not in the PDF, not in the payload, not in
   a response header. The vendor is in Calgary (Mountain), so venue-local Mountain is the plausible
   reading, but it is **unverified**. This matters for any cross-source time join. **Worth asking
   the vendor**; meanwhile store as given and do not shift (§8.3).
4. **Only the `Invalid startDate` form of error #3 was provoked.** An invalid `endDate` presumably
   yields `Invalid endDate`, and an invalid *format* vs an invalid *date* (e.g. `2026-02-30`) may or
   may not differ. Match on the substring `Invalid `, not on the full sentence.
5. **The row-cap error's masking order was observed in one direction only** (§7.2): a request that
   violated both limits reported the **row cap**. Whether a request that is out of history but
   *under* the row cap always reports the history message is confirmed (probe §5.3), but a formal
   precedence rule is not established. Log the body; do not infer.
6. **Rate limits are unpublished and unprobed.** No numeric limit, no `Retry-After`, no
   `X-RateLimit-*` header, and no `429` was ever observed. With 3 endpoints × a handful of chunks
   per run this is low-risk, but the PDF's *"not intended to be rapidly polled"* is an explicit
   warning. Pace conservatively and back off on `429`.
7. **`myTrades`' all-time history depth was probed only to 180 days.** The PDF says "all time"; the
   oldest row observed is `2026-03-02`. How far back it actually answers, and whether older rows
   carry the same 34 columns, is unknown. Bounds any `myTrades` deep-backfill plan.
8. **`Apportionment Protected = True` and `Click & Trade = True` were never observed** (0 of 135
   trade rows across both endpoints). Both are documented as `BIT`, but the `True` path is
   untested end-to-end. Same for a `Settlement Currency` other than `USD` and a `Product Type`
   other than `Physical`/`Financial`.
9. **No `Financial` rows appeared in `myTrades`** (37/37 `Physical`). So the "Financial ⇒ blank
   Location" invariant (§10.4 #1) is verified on `allTrades` only. Expect it to hold on `MyTrades`
   too; do not assume it does.
10. **The `Contract Terms` semantic is not documented.** It usually names the counterparty, but 2 of
    37 rows name `ARM Energy Management, LLC` instead — so it is "whose paper governs", not "the
    other party". Do not use it as a counterparty identifier.
11. **`Clearing ID` is never populated in any sampled window** (§9.2). The column plainly exists for
    cleared/exchange-given-up trades; none appeared. Its format, width and semantics are therefore
    **unknown** — `VARCHAR(50)` is the user's allocation, not an observed maximum. Re-check the
    first time a non-NULL value appears.
12. **No conditional-GET support** (no `ETag`, `Last-Modified` or `Cache-Control` on any response),
    so every run re-downloads the full window. At 140 KB for 1,443 settlement rows this is
    immaterial today; it bounds how cheaply the window can be widened.
