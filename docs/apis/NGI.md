# NGI (NGI Data Services — Bidweek natural-gas price survey) loader — API field reference

Monthly **Bidweek** natural-gas price-survey data from **NGI (Natural Gas Intelligence) Data
Services**: a per-point price/volume survey record set (endpoint 1) plus a
location-name → point-code catalogue (endpoint 2). This file is the **full-field-set gate**
for the loader — every field of both endpoints is enumerated below with its live JSON name,
observed type, nullability, observed maxima and a recommended SQL Server type. Nothing
downstream (model → sink → TVP → table → merge proc) may ship a partial column set.

- **Loader id:** `NGI`. **Recommended DB name:** `NGI` — **schema:** `arm` (per the platform
  convention used by Platts/CWG/AGSI/IHSPointLogic/IIR/OPIS); audit hub `arm.FileLog`.
  Final names/shapes are `DATABASE_DEVELOPER`'s call — see §7.
- **Suggested targets:** a Bidweek price fact (endpoint 1) and a Bidweek location lookup
  (endpoint 2). **Two independent pipelines — no FK between them** (see §7.3).
- **Secrets:** the account credentials are referred to **only** by config setting name —
  `Loaders:NGI:Username` and `Loaders:NGI:Password`, both shipped as the `"SEE_DB"` sentinel
  and resolved at run time from `core.Param`. **No username, password, or token value appears
  anywhere in this file, and none may be written into `appsettings.json`, a log, or a test
  fixture.**

---

## Gate status: **FULLY VERIFIED — zero reconstruction** ✅

This is the **first loader in the repo whose entire field set is live-observed with no
reconstructed field at all.** Contrast `IIR`, whose `detail`-JSON casing/nesting is inferred
from vendor docs and still carries an open live-verification checklist (`docs/apis/IIR.md`
§10). Here, every field name, every value type, every null sentinel and every observed
maximum below comes from a captured HTTP 200 body.

| Item | Provenance |
|------|-----------|
| `GET /bidweekDatafeed.json` — envelope shape + **all 14 JSON fields** (3 `meta` + 11 record) + the map key | **Verified live** 2026-08-21 (`issue_date=2026-08-01`, 163 records). Body committed at `tests/DataLoader.NGI.Tests/Samples/bidweekDatafeed_20260801.json`. |
| `GET /bidweekLocations?format=json` — envelope + **both fields** (name, code) | **Verified live** 2026-08-21, 163 entries. Body committed at `tests/DataLoader.NGI.Tests/Samples/bidweekLocations.json`. |
| `"None"` null sentinel + the 45/163 and 47/163 counts | **Verified live** — counted in the committed fixture. |
| Every value being a JSON **string** (incl. numerics) | **Verified live** in both fixtures. |
| Status-code matrix (`200` / `404` / `400`) + the observed 404 date list | **Verified live** — 8 distinct probe dates (§3.2). |
| `Survey Start`/`Survey End` varying month to month | **Verified live** across 3 issues (§3.3). |
| Omitting `issue_date` → the latest issue | **Verified live** (§3.4). |
| `POST /auth` request/response shape, `refresh` present, JWT `exp − iat = 86400` | **Verified live** — token decoded, never stored or reproduced. |
| Request/parameter declarations, `NGITokenObtainPair` / `Token` / `jwtAuth` schemas, the sibling-endpoint inventory | **Spec-read** — OpenAPI 3.0.3 at `https://api.ngidata.com/static/apispec.json` (`info.version` `0.0.1`, 59 paths). |
| Rate limits, revision policy, history depth, holiday calendar, token-expiry behaviour | ⚠ **Not verified** — see §9. These are *behavioural* unknowns; **no field is unknown.** |

> **⚠ convention in this document:** because no field is reconstructed, ⚠ is used **only** for
> behavioural/operational unknowns (§9). There are **zero ⚠ marks in the field tables.**

### ⚠⚠ The OpenAPI spec declares "No response body" for BOTH data endpoints

This is a **documented contradiction in the vendor spec, not an omission in this document.**
For `/bidweekDatafeed.json` and `/bidweekLocations` the spec literally declares:

```json
"responses": { "200": { "description": "No response body" } }
```

There is **no schema, no example, no `content` block** for either endpoint's 200 — the spec
describes only the *request*. The **entire field set in §4 and §5 therefore comes from live
calls**, transcribed from the two committed fixtures. Consequences to internalise:

- **Do not "check the spec" to resolve a field question.** It has no answer. The fixtures in
  `tests/DataLoader.NGI.Tests/Samples/` are the only schema of record, and a `CODE_TESTER`
  contract test against them is the only regression net.
- **There is no vendor-published contract for these responses**, so any NGI-side change is by
  definition silent. Parse tolerantly (multiple candidate property names/casings, unknown
  fields ignored, unparseable values → NULL, records missing `Point Code` dropped-and-counted)
  per the platform's tolerant-JSON convention, and let `usp_ValidateLoad` catch a shape drift
  as a row-count/NULL-rate anomaly.
- `/auth` is the **only** in-scope operation whose response *is* schematised — and even there
  the spec is wrong-by-omission (see §2.2).

---

## 1. Base URL, transport, paths

- **Base URL:** `https://api.ngidata.com` — no version segment, no `basePath`; the spec has
  **no `servers` block**, so the host is taken from the documentation site. Config setting
  `Loaders:NGI:BaseUrl`.
- **Payloads:** JSON (`application/json`) for all three in-scope operations.
- **Path-style inconsistency — easy to get wrong:** the datafeed carries a **format extension
  in the path** (`/bidweekDatafeed.json`, with a sibling `/bidweekDatafeed.txt`), while the
  locations endpoint has **no extension** and selects its format via a **query parameter**
  (`/bidweekLocations?format=json`). Do not "normalise" these into one shape.
- **No paging of any kind.** Neither endpoint accepts `page`, `pageIndex`, `offset`, `limit`
  or a cursor; neither response carries a paging envelope, a `next` link, a total count or a
  `Link` header. **One request = the complete result set** (163 records / 163 entries
  observed). This is the platform's first in-scope REST feed with *no* paging mechanism —
  contrast IHSPointLogic (`?pageIndex=` 0-based) and IIR (`limit`/`offset`).
- **No batching parameter.** Neither endpoint accepts a repeated id parameter, so the ≤50-id
  batching rule used by IIR and IHSPointLogic **does not apply here**. Fan-out is over
  *dates* (one request per candidate issue date), not over ids.
- **No discovery dependency.** `bidweekLocations` is **not** a discovery tier: nothing in the
  datafeed request needs a value from it (the datafeed is parameterised by date alone). The
  two pipelines are independent and may run in either order or concurrently. `bidweekLocations`
  takes **no parameters other than `format`**, so it is a plain full-snapshot lookup pull.

---

## 2. Authentication — `POST /auth` → JWT bearer, minted from credentials

**Auth shape (matched to the patterns already in this repo): a minted JWT bearer token with a
fixed expiry and re-mint from credentials — the `IIR` pattern**, not the `?apikey=` query of
CWG/StormVista, not the `x-key` header of AGSI, and not the HTTP Basic/PAT of IHSPointLogic.

Spec security scheme (the only one defined, and applied to **every** data path):

```json
"jwtAuth": { "type": "http", "scheme": "bearer", "bearerFormat": "JWT" }
```

### 2.1 Minting a token

```
POST https://api.ngidata.com/auth
Content-Type: application/json

{"email": "<Loaders:NGI:Username>", "password": "<Loaders:NGI:Password>"}
```

| Request field | Type | Required | Config setting supplying it | Notes |
|---|---|:--:|---|---|
| `email` | string (`writeOnly`) | **Yes** | `Loaders:NGI:Username` | ⚠ **The field is named `email`, NOT `username`.** Request body schema is `NGITokenObtainPair`, `required: [email, password]`. A body posted with `username` will not authenticate. The value is an e-mail address; the setting is nonetheless called `Username` to match the platform's `Loaders:<Id>:Username` convention. |
| `password` | string (`writeOnly`) | **Yes** | `Loaders:NGI:Password` | Both settings ship as the `"SEE_DB"` sentinel and resolve from `core.Param`. Never logged, never in `appsettings.json`. |

- **Content types accepted** (all three declared in the spec, all `$ref` the same schema):
  `application/json`, `application/x-www-form-urlencoded`, `multipart/form-data`. Use
  `application/json`.
- `/auth` also accepts an optional `?format=` query param (`csv` | `json` | `txt`). Omit it,
  or send `json`.
- `/auth` is the **only** path in the spec without a `security` requirement — it is the
  unauthenticated entry point.

### 2.2 Token response — the spec understates it

Live `200` body (shape only — **no token value is reproduced here or anywhere in the repo**):

```json
{ "refresh": "<opaque ~200-char string>", "access_token": "<JWT>" }
```

| Response field | Type | In spec? | Use |
|---|---|:--:|---|
| `access_token` | string (JWT) | **Yes** — the `Token` schema declares it and marks it `required` | The bearer credential. Send as `Authorization: Bearer <access_token>`. |
| `refresh` | string (opaque, ~200 chars, not a JWT) | **No** — absent from the `Token` schema, but present in the live body *and* in the spec's own `TokenExample` | **Unusable — ignore it.** |

- **Parse tolerantly:** the spec's `Token` schema declares **only** `access_token`, yet the
  live response returns **two** fields. A strict/`JsonUnmappedMemberHandling.Disallow` binding
  would break; ignore unknown members.
- **`refresh` is dead weight — there is NO refresh endpoint.** `/auth` is the only path under
  the `Authentication` tag and the only auth path in the whole 59-path spec: there is no
  `/auth/refresh`, `/token/refresh`, or equivalent. **Discard `refresh` and re-mint from
  credentials.** (Do not persist it: it is a credential.)

### 2.3 Lifetime, expiry and renewal

- Decoded JWT payload claims observed: `type` (`"access"`), `exp`, `iat`, `jti`, `user_id`,
  `identity` (the account e-mail). **`exp − iat = 86400` → a fixed 24-hour lifetime.**
  There is no `tokenLifeTime`-style request parameter (contrast IIR) — the lifetime is
  server-fixed.
- **Renewal:** re-`POST /auth`. Recommended handler behaviour, identical to the established
  IIR pattern: a thread-safe singleton token provider mints once per process, caches the
  token, **re-mints once on a `401`**, and pre-emptively re-mints when `exp` is within a small
  safety margin. Because the platform runs the host per invocation and a run is far shorter
  than 24 h, a single mint per run will normally suffice; the 401 re-mint is the safety net.
- **Handler order is the repo standard: retry (OUTER) → auth → throttle (INNER)**, so a
  re-mint is retried and throttling stays innermost.
- ⚠ **Expiry behaviour was never observed live** — the probe never held a token for 24 h, so
  the exact response to an expired token (`401` with what body? a different status?) is
  **unverified**. See §9.
- **Credentials are not in the URL** for any call (they are in the `/auth` request *body*),
  so the `RemoveAllLoggers()` URL-scrubbing precaution required for CWG/StormVista
  (`?apikey=` in the query) is not strictly needed here — but **the `/auth` request body must
  never be logged**, and the `Authorization` header must never be logged.

### 2.4 Presenting the token on data calls

```
GET https://api.ngidata.com/bidweekDatafeed.json?issue_date=2026-08-01
Authorization: Bearer <access_token>
Accept: application/json
```

Both data endpoints declare `security: [{ "jwtAuth": [] }]`; both return `401` without it.

---

## 3. Shared HTTP semantics — status codes and date behaviour

### 3.1 Status-code matrix (with the probe's evidence)

| Status | Meaning | Evidence | Loader must |
|:---:|---|---|---|
| **`200`** | Publication exists for the requested issue date; body is the full result set. | `issue_date` = `2026-08-01`, `2026-07-01`, `2026-06-01`; and `issue_date` omitted. | Parse and load. |
| **`404`** | **No publication on this date. THIS IS THE NORMAL CASE.** Body: `{"msg":"Datafeed not found. The date entered could be a weekend or holiday."}` | `2026-07-31`, `2026-08-14`, `2026-08-15`, `2026-08-20`, `2026-08-21`, **and `2026-09-01` (a future date)** — 6 of the 9 probed dates. | **Treat as an empty, SUCCESSFUL read.** Record it in `arm.FileLog` with its HTTP status and `RowCount = 0`, complete the work unit as success, and move on. **Never** a failure, never a retry, never an alert. |
| **`400`** | Malformed `issue_date`. Body: `{"msg":"Incorrect date format for issue_date, should be YYYY-MM-DD"}` | `issue_date=01-28-2021` (the spec's own "Invalid" example). | **Treat as a caller bug and FAIL LOUDLY.** A `400` can only mean the loader formatted the date wrong — it must never be swallowed like a 404. Always format with `yyyy-MM-dd` invariant. |
| **`401`** | Missing/invalid/expired bearer token. | Auth is required on both data paths. | Re-mint once via `POST /auth`, then retry; fail if it recurs. |

> **The 404-is-normal rule is the single biggest operational fact about this feed.** NGI
> Bidweek is **monthly**: over a 60-day back window, **~58 of 60 work units legitimately
> return `404`**. A loader (or a `usp_ValidateLoad` rule, or an on-call alert) that treats a
> 404 as an error will produce ~58 false failures per run and drown the real signal. This is
> the same 404-tolerant posture as CWG/AGSI/StormVista, but here it is the *majority* outcome
> rather than an edge case.

### 3.2 Which dates carry a publication (observed)

| `issue_date` | Result | Records |
|---|---|:--:|
| *(omitted)* | `200` — the **latest** issue, which was `2026-08-01` | 163 |
| `2026-08-01` | `200` — survey window 2026-07-27 → 2026-07-29 | 163 |
| `2026-07-01` | `200` — survey window 2026-06-24 → 2026-06-26 | 163 |
| `2026-06-01` | `200` — survey window 2026-05-22 → 2026-05-27 | 163 |
| `2026-07-31` | `404` | — |
| `2026-08-14` | `404` | — |
| `2026-08-15` | `404` | — |
| `2026-08-20` | `404` | — |
| `2026-08-21` | `404` | — |
| `2026-09-01` *(future)* | `404` | — |
| `01-28-2021` *(bad format)* | `400` | — |

**A future date returns `404`, not an error and not an empty `200`** — so a work-unit
generator that runs slightly ahead of publication is harmless.

> **⚠ Do NOT filter candidate dates to business days.** NGI's own spec says of `issue_date`:
> *"Issue date will always be a business day"* — **the observed data contradicts this.** In
> all **three** probed months the issue date was the **1st calendar day of the month**, and
> **2026-08-01 is a Saturday** (2026-06-01 = Monday, 2026-07-01 = Wednesday, 2026-08-01 =
> Saturday — by calendar arithmetic from 2026-01-01 = Thursday; the weekdays are derived here,
> the 200/404 results are live-observed). The **Friday before** it, `2026-07-31`, returned
> `404`. A generator that skipped weekends would therefore have **silently missed the entire
> August 2026 issue.**
>
> **Recommendation:** do not try to predict publication dates at all. Enumerate **every
> calendar date** in the configured back window as a candidate work unit and let the `404`
> be the answer — it costs one cheap request per non-publication day and is immune to both
> NGI's holiday calendar and any change in the day-of-month convention. (Only 3 issue dates
> were probed, so "always the 1st" is **not** established either — see §9.)

### 3.3 `Survey Start` / `Survey End` are NOT a fixed offset — read them from the payload

| Issue Date | `Survey Start` | `Survey End` | Start offset (days before issue) | Window length (calendar days, inclusive) |
|---|---|---|:--:|:--:|
| 2026-08-01 | 2026-07-27 | 2026-07-29 | **5** | 3 |
| 2026-07-01 | 2026-06-24 | 2026-06-26 | **7** | 3 |
| 2026-06-01 | 2026-05-22 | 2026-05-27 | **10** | 6 |

**Both the offset and the length vary month to month** (offset 5 / 7 / 10; length 3 / 3 / 6).
**Never compute the survey window from the issue date — always read
`Survey Start`/`Survey End` (or `meta.start_date`/`meta.end_date`) out of the response.** A
hard-coded offset would have been wrong in 2 of the 3 observed months. (The June window spans
the U.S. Memorial Day weekend, which is *consistent with* a business-day-based definition —
that explanation is inference, not verified.)

### 3.4 Omitting `issue_date` — documented, but unsuitable for a work unit

`issue_date` is **optional** in the spec, and omitting it live returned `200` with the
**latest** issue (2026-08-01, 163 records).

**Flagged: do not build a work unit on the parameterless call.** It is
**non-deterministic** — its result changes on NGI's publication schedule, so the work-unit
`Key` cannot encode what was actually fetched, which breaks the platform's idempotency
contract (`core.LoadLog` skips only keys already recorded successful). Two runs with the same
key could legitimately return different issues, and a settled key would pin the wrong issue
forever. **Always pass an explicit `issue_date`.** The parameterless form is useful only as a
one-off diagnostic ("what is the newest issue NGI holds?"), and if it is ever used, the
authoritative answer is `meta.issue_date` in the response — never the run date.

---

## 4. Endpoint 1 — `GET /bidweekDatafeed.json?issue_date=YYYY-MM-DD`

- **Tag:** `Bidweek Survey`. **Method:** `GET`. **Auth:** `Authorization: Bearer <token>`.
- **Query parameters:** exactly one.

| Param | Type | Required | Format | Notes |
|---|---|:--:|---|---|
| `issue_date` | string | **No** (but *always send it* — §3.4) | `YYYY-MM-DD` | The publication (issue) date. Spec description: *"Issue Date (YYYY-MM-DD format). Issue date will always be a business day"* — see the ⚠ in §3.2. Wrong format → `400`. |

- **Example request:**

```
GET https://api.ngidata.com/bidweekDatafeed.json?issue_date=2026-08-01
Authorization: Bearer <access_token>
Accept: application/json
```

- **Sibling format:** `/bidweekDatafeed.txt` takes the same parameter and returns a text
  rendering. Out of scope — use the `.json` path.

### 4.1 Response envelope — `data` is a DICTIONARY KEYED BY POINT CODE, not an array

```json
{
  "meta": { "issue_date": "2026-08-01", "start_date": "2026-07-27", "end_date": "2026-07-29" },
  "data": {
    "STXAGUAD": { "Point Code": "STXAGUAD", "Issue Date": "2026-08-01",
                  "Survey Start": "2026-07-27", "Survey End": "2026-07-29",
                  "Region": "South Texas", "Pricing Point": "Agua Dulce",
                  "Low": "2.360", "High": "2.390", "Average": "2.375",
                  "Volume": "None", "Deals": "None" },
    "STXFGTZ1": { "Point Code": "STXFGTZ1", "…": "…" }
  }
}
```

Three structural facts that will corrupt a load if missed:

1. **`data` is a JSON object (map), NOT an array.** Deserialising it as a list fails outright;
   worse, a "tolerant" reader that silently yields zero records would report a successful
   empty load. Bind it as a dictionary — e.g. `Dictionary<string, Dictionary<string, string>>`
   is the most robust shape, since **every leaf value is a string** (§4.3).
2. **The map key equals the record's own `Point Code`** — verified for **all 163 records** in
   the fixture (0 mismatches). The key is therefore **redundant**, and the record field is
   the value to persist. Recommended posture: read `Point Code` from the record, and treat a
   key ≠ `Point Code` disagreement as a *validation warning*, not a parse strategy. A record
   whose `Point Code` is missing/blank must be **dropped and counted** (the key is a usable
   fallback, but log it).
3. **The map form structurally allows only one record per point code per issue** (JSON objects
   cannot express a duplicate key), which is exactly the grain of the recommended PK
   `(IssueDate, PointCode)`. Do not rely on any particular .NET behaviour if NGI ever emitted
   a literal duplicate key — that is version-dependent; the shape simply should not occur.

**Envelope field count: 3 (`meta`) + 11 (per record) = 14 distinct JSON fields**, plus the map
key. There is **no** `count`, `total`, `page`, `next`, `status`, or `version` field — the
envelope is exactly `{meta, data}` and nothing else.

### 4.2 `meta` block — 3 fields

| JSON field | Live type | Nullable | Meaning | Persist? |
|---|---|---|---|---|
| `meta.issue_date` | string `YYYY-MM-DD` | never null | The issue date of the returned publication. **Equals every record's `Issue Date`** (verified, all 163). When `issue_date` was omitted from the request, this is the **only** way to learn which issue came back. | Not as a column — use as an **assertion** against the requested date and against `Issue Date`. |
| `meta.start_date` | string `YYYY-MM-DD` | never null | Survey window start. **Equals every record's `Survey Start`** (verified, all 163). | Not as a column — assert against `Survey Start`. |
| `meta.end_date` | string `YYYY-MM-DD` | never null | Survey window end. **Equals every record's `Survey End`** (verified, all 163). | Not as a column — assert against `Survey End`. |

`meta` is fully redundant with the record fields on the observed payload, so the fact table
takes its dates from the **record** (self-describing rows). `meta` earns its keep in three
ways: the "which issue did I get?" answer for a parameterless call, a cheap has-data check,
and a per-file consistency assertion for `usp_ValidateLoad`.

### 4.3 Two parsing hazards that break default binding

**(a) Field names CONTAIN SPACES.** Five of the eleven record fields do:

```
"Point Code"   "Issue Date"   "Survey Start"   "Survey End"   "Pricing Point"
```

**Default `System.Text.Json` POCO binding will NOT match these.** A property named
`PointCode` binds to nothing — even `PropertyNamingPolicy.CamelCase` and
`SnakeCaseLower` produce `pointCode` / `point_code`, never `Point Code`. Every affected
property needs an explicit `[JsonPropertyName("Point Code")]` (exact spelling, exact single
space, exact casing) or must be read through a dictionary/`JsonElement`. **A silent
mis-binding here yields a full 163-row batch of NULL point codes and NULL dates** — i.e. an
unkeyable batch — which is precisely the failure mode this document exists to prevent. Per
the repo's tolerant-parse convention, accept candidate spellings
(`Point Code` / `PointCode` / `point_code`) and degrade to NULL rather than throwing.
Field order within a record was stable in the fixture (the table order in §4.4) but **JSON
object order is not a contract — bind by name, never by position.**

**(b) EVERY value is a JSON string — including all numerics.** Prices arrive as `"2.360"`,
volumes as `"240"`, counts as `"15"`, dates as `"2026-08-01"`. There is **not one** JSON
number, boolean or `null` anywhere in either fixture. So:
- Parse numerics with **invariant culture** (`.` decimal separator) — never the ambient
  culture.
- Bind the DTO as strings and convert in the transformer; a `decimal`/`int` property will
  fail or need `NumberHandling.AllowReadingFromString`.
- Accept a **leading minus**: NGI point prices can legitimately go negative (Waha has
  printed negative cash prices historically), so `"-0.250"` must parse. **Do not put a
  `CHECK (… >= 0)` constraint on any price column.**

**(c) The null sentinel is the literal string `"None"`.** Not `null`, not `""`, not `"N/A"`,
not a missing property — the property is always present with the exact four-character value
`None`:

```json
"Low": "None", "High": "None", "Average": "None", "Volume": "None", "Deals": "None"
```

**`"None"` MUST map to SQL `NULL`.** The two ways this goes wrong are both silent:
`decimal.TryParse("None")` fails → an unguarded `0` gets written (a real price of zero!), or
the string lands in a text column and pollutes it. Handle `"None"` explicitly, before any
numeric parse, and treat any other unparseable value as NULL + a counted warning.

Observed on the 2026-08-01 issue (163 records):

| Sentinel group | Fields | `"None"` count | Populated |
|---|---|:--:|:--:|
| Prices | `Low`, `High`, `Average` | **45 / 163** (27.6%) | 118 |
| Activity | `Volume`, `Deals` | **47 / 163** (28.8%) | 116 |

The two groups move together **within** a group (a record with `Low = "None"` also has
`High` and `Average` = `"None"`), and the price-null set is a **strict subset** of the
activity-null set. Exactly **2** records have prices but no volume/deals:
`STXAGUAD` (Agua Dulce) and `CALSPGE` (Southern Border, PG&E). Two consequences:
- **All five measure columns must be NULLable**, and a validation rule must **not** require
  all 163 rows to be priced — ~28% unpriced is a *normal* issue, not a bad load.
- "Priced but no volume" is legitimate (an assessed/rolled-up price with no reported deals),
  so a "price implies volume" DQ rule would produce false positives.

### 4.4 Full record field set — exactly **11 fields**, no more

Union over **all 163 records** in the live fixture. Every field is present on every record.
Lengths/decimals are **observed maxima on the 2026-08-01 issue**, not vendor-declared limits —
recommended SQL types add headroom.

| # | JSON field | Live type | Nullable (`"None"` count) | Observed max length / decimals | Recommended SQL type | Meaning |
|:-:|---|---|---|---|---|---|
| 1 | `Point Code` | string | **never null** | **max len 12**, uppercase A–Z + digits | `VARCHAR(20)` **NOT NULL** | NGI pricing-point code (e.g. `STXAGUAD`, `OTHREXZN3DEL`). **Equals the `data` map key** for all 163 records. **PK component.** ASCII-only observed → `VARCHAR`, not `NVARCHAR`. |
| 2 | `Issue Date` | string `YYYY-MM-DD` | **never null** | 10 | `DATE` **NOT NULL** | Publication date of the issue. Equals `meta.issue_date` and the requested `issue_date`. **PK component.** `DATE` not `DATETIME2` — there is no time component anywhere in this feed. |
| 3 | `Survey Start` | string `YYYY-MM-DD` | **never null** | 10 | `DATE` **NOT NULL** | First day of the surveyed trading window. Equals `meta.start_date`. **Varies month to month — read it, never compute it** (§3.3). |
| 4 | `Survey End` | string `YYYY-MM-DD` | **never null** | 10 | `DATE` **NOT NULL** | Last day of the surveyed trading window. Equals `meta.end_date`. Same warning. |
| 5 | `Region` | string | **never null** | **max len 24** (`West Texas/SE New Mexico`); **14 distinct values** (§4.5) | `VARCHAR(64)` **NOT NULL** | NGI regional grouping. Free text, not a code; contains `/`. **Not derivable from `Point Code`** (§4.6). ASCII-only observed. |
| 6 | `Pricing Point` | string | **never null** | **max len 33** (`SoCal Border - Kern River Station`) | `VARCHAR(100)` **NOT NULL** | Human-readable location name. **Matches the `bidweekLocations` key** for all 163 codes (§5.4). Contains `&`, `-`, `,`, `.`, `/` — ASCII-only observed, so `VARCHAR`; use `NVARCHAR` only if the `mexico*` feeds (accented names) are ever folded in. |
| 7 | `Low` | string, numeric | **YES — `"None"` in 45/163** | **exactly 3 decimals**, 1 integer digit, max len 5 (`4.250`; min `1.060`) | `DECIMAL(13,6)` **NULL** | Low price of the survey range, USD/MMBtu. `DECIMAL` **not `FLOAT`** — a published price must round-trip exactly. Scale 6 stores the observed 3-dp grain exactly with room for a finer one, and the 7 integer digits are deliberate price-spike headroom (February 2021 US gas printed four figures at some hubs). **Signed — no non-negative CHECK.** |
| 8 | `High` | string, numeric | **YES — `"None"` in 45/163** | 3 decimals | `DECIMAL(13,6)` **NULL** | High price of the survey range, USD/MMBtu. |
| 9 | `Average` | string, numeric | **YES — `"None"` in 45/163** | 3 decimals | `DECIMAL(13,6)` **NULL** | Average price. **Not the midpoint of `Low`/`High`** — e.g. `STXFGTZ1` = 2.435 / 2.465 / **2.440** (midpoint would be 2.450), so it is a weighted (deal-volume) average. The weighting method is NGI methodology, ⚠ not documented in the spec. |
| 10 | `Volume` | string, integer | **YES — `"None"` in 47/163** | **0 decimals**, max len **4** (`8647` on `USAVG`; `240` typical) | `INT` **NULL** | Reported deal volume for the point over the survey window. **Unit is not documented anywhere in the spec or payload** — ⚠ see §9. No decimal ever observed; `INT` (never `TINYINT` per repo convention, and `SMALLINT` would overflow at 32 767, which is well within reach as coverage grows). |
| 11 | `Deals` | string, integer | **YES — `"None"` in 47/163** | 0 decimals, max len **4** (`1663` on `USAVG`; `15` typical) | `INT` **NULL** | Count of deals underpinning the price. Same `INT` reasoning. |

**Derived/stamped columns (no API field)** — for `DATABASE_DEVELOPER`, per the repo's fact
table shape: `DateCreated DATETIME DEFAULT GETDATE()` (leads the table), `FileLogId` (audit
provenance, first column of the TVP), `ModifiedAtUtc DATETIME2(3)` (**stamped by the merge
proc — never crosses the TVP**). No `GEOGRAPHY`, no computed columns: **the locations feed
carries no coordinates** (§5.3), so there is nothing to build a point from.

### 4.5 The 14 distinct `Region` values (observed 2026-08-01)

In payload order, with the number of records carrying each (163 total; counts are a **snapshot,
not a contract**):

| # | `Region` | Len | Records |
|:-:|---|:--:|:--:|
| 1 | `South Texas` | 11 | 7 |
| 2 | `East Texas` | 10 | 11 |
| 3 | `West Texas/SE New Mexico` | **24** | 11 |
| 4 | `Midwest` | 7 | 23 |
| 5 | `Midcontinent` | 12 | 11 |
| 6 | `North Louisiana/Arkansas` | **24** | 5 |
| 7 | `South Louisiana` | 15 | 17 |
| 8 | `Southeast` | 9 | 12 |
| 9 | `Appalachia` | 10 | 11 |
| 10 | `Northeast` | 9 | 15 |
| 11 | `Rocky Mountains` | 15 | 19 |
| 12 | `Arizona/Nevada` | 14 | 2 |
| 13 | `California` | 10 | 15 |
| 14 | `Canada` | 6 | 4 |

Longest value = **24 chars** (two of them tie). `Region` is **not an enum in any vendor
document** — do not build a constrained lookup/CHECK on these 14 values; a new region would
then fail the load. Model it as plain text on the fact (see §7.2 for why it is *not*
normalised into the location dimension).

### 4.6 Semantics you must not get wrong (all live-observed)

1. **`Region` is NOT derivable from `Point Code`.** The code prefix is a legacy naming
   artefact and frequently disagrees with `Region`: `NEALEB` (Lebanon) is `Midwest`;
   `MCWANR` (ANR SW) is `Midcontinent` while `MCWCCITY` is `Midwest`; `MCWNIAGR` (Niagara) is
   `Northeast`; `OTHREXZN3DEL` is `Midwest`; `ETXTGT` and `ALATETM124` are
   `North Louisiana/Arkansas`; `SLAFGTZ3` is `Southeast`. **Always take `Region` from the
   field.**
2. **The feed mixes granular points with in-band AGGREGATE rows** — there is **no flag field**
   distinguishing them; only the code/name convention does. Regional averages appear as
   `STXRAVG`, `ETXRAVG`, `WTXRAVG`, `MWERAVG`, `MCTRAVG`, `NLARAVG`, `SLARAVG`, `SEREGAVG`,
   `APPREGAVG`, `NEARAVG`, `RMTRAVG`, `CALRAVG`; a sub-aggregate as `CALSAVG`
   (`SoCal Border Avg.`); and the national average as **`USAVG`** (`National Avg.`). **Any
   downstream aggregation over the fact table will double-count** unless these are excluded.
   Note `Arizona/Nevada` had **no** regional-average row in this issue (only 2 points), so
   "every region has an average row" is **false**.
3. **`USAVG` is filed under `Region = "California"`.** The national-average row carries a
   regional label, which is almost certainly an NGI-side artefact. A `GROUP BY Region` would
   attribute the U.S. national average to California. Flagged for `DATA_QUALITY_VALIDATOR`.
4. **Distinct points legitimately share identical prices** (pooled points):
   `WTXEPKPWP` / `WTXEPP` / `WTXEPKEY` all report 1.650 / 1.870 / 1.760 with Volume 37,
   Deals 10; likewise `CALSSOCAL` ≡ `CALSAVG` and `RMTCHEY` ≡ `RMTCHEYOTH`. A
   "duplicate row" DQ rule keyed on values (rather than on `(IssueDate, PointCode)`) will
   false-positive.
5. **`Low ≤ Average ≤ High`** held in every record inspected — a good `usp_ValidateLoad`
   check, but offered as a **recommended rule, not a verified invariant** (not asserted over
   all 118 priced rows).
6. **`Low` can equal `High`** on thin points (e.g. `STXST30`: 2.385 / 2.385 / 2.385,
   Deals 1) — a zero-width range is valid, not an error.

---

## 5. Endpoint 2 — `GET /bidweekLocations?format=json`

- **Tag:** `Bidweek Survey`. **Method:** `GET`. **Auth:** `Authorization: Bearer <token>`.
- **Query parameters:**

| Param | Type | Required | Values | Notes |
|---|---|:--:|---|---|
| `format` | string | No | `csv` \| `json` \| `txt` (spec `enum`) | JSON is returned even when omitted, but **send `format=json` explicitly** — do not depend on the server default. |

- **No `issue_date`, no date parameter of any kind.** This is an **as-of-now snapshot**; the
  response carries **no** issue date, no `meta`, no timestamp and no version. The loader must
  stamp its own as-of/run date if point-code history matters.
- **Example request:**

```
GET https://api.ngidata.com/bidweekLocations?format=json
Authorization: Bearer <access_token>
Accept: application/json
```

### 5.1 Response envelope — ⚠⚠ THE JSON KEY IS THE NAME, THE VALUE IS THE POINT CODE

```json
{
  "Bidweek Locations": {
    "Agua Dulce": "STXAGUAD",
    "Algonquin Citygate": "NEAALGCG",
    "Algonquin Citygate (non-G)": "NEALGNG",
    "SoCal Border - Kern River Station": "CALSAVGKRS"
  }
}
```

> ## 🛑 Read this twice — the easiest bug in this loader
>
> ```
>          "Agua Dulce"      :      "STXAGUAD"
>          ^^^^^^^^^^^^             ^^^^^^^^^^
>          JSON KEY   = LOCATION NAME
>                                   JSON VALUE = POINT CODE
> ```
>
> **The map runs NAME → CODE. It is NOT code → name.** Both sides are strings of similar
> shape, so **inverting the direction produces no exception, no parse error and no warning** —
> it produces 163 rows with the name in the `PointCode` column and the code in the `Name`
> column. The natural key and every join to the price fact then silently miss, and the only
> symptom is "the lookup table looks weird". There is **no** vendor schema to catch this
> (§ "No response body"), so the mapping direction must be pinned by an explicit unit test
> against `tests/DataLoader.NGI.Tests/Samples/bidweekLocations.json` asserting, e.g.,
> `PointCode == "STXAGUAD"` **and** `LocationName == "Agua Dulce"` for that entry.
>
> Mnemonic: **codes are UPPERCASE and unspaced; names have spaces and mixed case.** The
> uppercase side is always the *value*.

Structural facts:

- **Exactly one top-level key: `"Bidweek Locations"`** — note the **space** and the
  **capitalised second word**. This key must also be matched literally (the same
  spaces-in-names hazard as §4.3(a)); bind tolerantly
  (`Bidweek Locations` / `BidweekLocations` / `bidweek_locations`).
- The value is a **flat single-level object** — no nesting, no arrays, no envelope metadata.
- **163 entries**, matching the datafeed's 163 records (§5.4).
- **No paging, no total, no timestamp.**

### 5.2 Full field set — exactly **2 fields**

| # | Source | Live type | Nullable | Observed max length | Recommended SQL type | Meaning |
|:-:|---|---|---|:--:|---|---|
| 1 | **JSON object key** | string | never null (a JSON key cannot be null) | **33** (`SoCal Border - Kern River Station`) | `VARCHAR(100)` **NOT NULL** | **Location / pricing-point NAME.** Equals the datafeed's `Pricing Point` for all 163 codes. ASCII-only observed (contains `&`, `-`, `,`, `.`, `/`, `(`, `)`). |
| 2 | **JSON object value** | string | never null in the fixture | **12** | `VARCHAR(20)` **NOT NULL** | **NGI point code.** Equals the datafeed's `Point Code`. **Unique — 0 duplicates across all 163 entries**, so it is a safe natural key / `UNIQUE` constraint. |

Plus the **loader-stamped** columns per the repo's dimension/lookup shape: `Id INT
IDENTITY(1,1)` PK **first**, `DateCreated DATETIME DEFAULT GETDATE()` **second**, then the two
payload columns, then `ModifiedAtUtc DATETIME2(3)`; the `UNIQUE` constraint on `PointCode` is
the MERGE target.

### 5.3 What this endpoint does NOT return

**Only 2 fields exist. There is NO:**

- ❌ region / zone / area — **`Region` is available ONLY from the datafeed row** (§4.4 #5).
  It therefore **cannot** be normalised into this dimension without inventing data.
- ❌ state / province / country — the `Canada` grouping exists only as a datafeed `Region`.
- ❌ latitude / longitude / any coordinate — so **no `FLOAT` coordinate columns and no
  `GEOGRAPHY` column** on either table (contrast IIR's `PlantPoint`).
- ❌ pipeline / operator / hub metadata, ❌ active/inactive flag, ❌ first/last-published date,
  ❌ unit of measure, ❌ id other than the code, ❌ sort order, ❌ parent-aggregate linkage
  (nothing marks `USAVG` or the `*RAVG` codes as aggregates).

Anyone expecting a rich location dimension will be disappointed: this endpoint is a **name↔code
crosswalk and nothing else.** Do not model columns the API cannot fill.

### 5.4 Cross-endpoint reconciliation (same-day observation)

Against the 2026-08-01 datafeed:

- The 163 datafeed `Point Code` values are **exactly** the 163 location codes — 0 missing in
  either direction.
- The location **name** equals the datafeed **`Pricing Point`** for **all 163** codes — 0
  mismatches. Same vocabulary; **no normalisation/trimming needed**.

> ⚠ **This is a same-day observation, NOT a contract** (§9). Because a name is a JSON *key*,
> two locations that ever shared a name could not both appear — one would be structurally
> lost. And because the two endpoints are pulled by **independent pipelines**, a new point
> code can legitimately appear in a datafeed *before* the locations snapshot is refreshed.
> **Therefore: do NOT create an FK from the price fact to the location table** (the same call
> made for AGSI/IIR-style independent pulls). Reconcile with an **observational**
> `usp_ValidateLoad` check that reports unmatched codes both ways — warn, never throw.

---

## 6. Coverage checklist

| # | Endpoint | Method / path | Auth | Params | Envelope | Paging | Fields documented | Verified? |
|:-:|---|---|---|---|---|---|:--:|---|
| 1 | Bidweek datafeed | `GET /bidweekDatafeed.json` | Bearer JWT | `issue_date` (optional, `YYYY-MM-DD`) | `{meta, data}`, `data` = **map keyed by point code** | **none** | **14** (3 `meta` + **11** record) + map key | **Live-verified** ✅ |
| 2 | Bidweek locations | `GET /bidweekLocations` | Bearer JWT | `format` (`csv`\|`json`\|`txt`) | `{"Bidweek Locations": {name → code}}` | **none** | **2** (name = key, code = value) | **Live-verified** ✅ |
| 3 | Auth | `POST /auth` | none | JSON body `{email, password}` | flat object | n/a | **4** (2 request: `email`, `password`; 2 response: `access_token`, `refresh`) | **Live-verified** ✅ |

**Total documented fields: 20** (14 datafeed + 2 locations + 4 auth), plus the datafeed map key.

**Gate status: PASS — no reconstructed field, no `⚠` in any field table, nothing dropped
without a stated reason.** The "needs one-shot live verification" checklist that
`IHSPointLogic` and `IIR` carry is **empty for field sets here**; §9 lists only behavioural
unknowns.

---

## 7. Notes for `DATABASE_DEVELOPER` (recommendations only — the schema is yours)

### 7.1 Grain and keys

| Target (suggested) | Role | Suggested natural key | Rationale |
|---|---|---|---|
| Bidweek price fact | High-volume fact → **composite natural PK, no surrogate `Id`** | **`(IssueDate, PointCode)`** | The `data` map is structurally one record per point per issue, so this is exact and collision-free. ~163 rows/month. Leads with `DateCreated`, carries `FileLogId` + `ModifiedAtUtc`. |
| Bidweek location lookup | Dimension / lookup | **`PointCode`** (`UNIQUE`, the MERGE target) | 0 duplicate codes across 163 entries. `Id IDENTITY` PK first, `DateCreated` second, then `PointCode` + `LocationName`, then `ModifiedAtUtc`. Full snapshot, overwrite in place. |
| `arm.FileLog` | Audit hub | per the platform convention | **One row per endpoint pull per run**, including every `404` (status + `RowCount = 0`) — this is what makes "no publication that day" auditable rather than invisible. |

### 7.2 Type calls worth confirming with me

- **`DECIMAL(13,6)`, not `FLOAT`, for `Low`/`High`/`Average`** — published prices must
  round-trip exactly; the source is exact to 3 dp, and scale 4 is deliberate headroom.
  (`FLOAT` is reserved in this repo for genuinely approximate measures such as coordinates —
  and this feed has none.) **Signed: no `CHECK (>= 0)`** (negative gas prices occur).
- **`DATE`, not `DATETIME2`,** for all three date fields — the feed has **no** time component
  anywhere, and no timezone is stated or needed. (`ModifiedAtUtc` remains `DATETIME2(3)` per
  convention.)
- **`INT` for `Volume`/`Deals`** — integers only observed; `SMALLINT` risks overflow above
  32 767; `TINYINT` is banned by repo convention.
- **`VARCHAR`, not `NVARCHAR`,** throughout — every observed character in both fixtures is
  ASCII. Revisit **only** if a `mexico*` feed (accented names) is ever added.
- **`Region` and `PricingPoint` stay denormalised ON the fact.** This is a deliberate
  deviation from the "repeated per-entity strings normalize out into a dimension" convention,
  for two verified reasons: (a) **`Region` is not available from the locations endpoint at
  all**, so the dimension physically cannot supply it; (b) the fact must remain loadable when
  a datafeed contains a point code the locations snapshot has not yet seen. Keeping both on
  the fact also preserves the as-published values if NGI ever re-labels a point.
- **All five measures NULLable**; `PointCode`, `IssueDate`, `SurveyStart`, `SurveyEnd`,
  `Region`, `PricingPoint` are safely `NOT NULL` (never null in 163/163).

### 7.3 Load-shape notes

- **Two independent pipelines, no discovery tier, no cross-endpoint dependency, no FK
  between the tables** (§5.4).
- **No batching parameter exists** (§1) — the ≤50-id rule from IIR/IHSPointLogic does not
  apply. Fan-out is one request per candidate issue date.
- **Resume key:** the two-zone settled/hot pattern fits well — a published issue is
  immutable-as-far-as-observed (⚠ §9 #1), so an issue date older than `SettledAfterDays`
  deserves a stable key (loaded once, then skipped forever), while recent dates take a
  run-varying key so a late publication is picked up. A `404` work unit **completes
  successfully** and so is skipped on rerun — which is correct for a settled month, but means
  the **hot zone must stay wide enough** (a run-varying key) that a date which 404'd on the
  1st is re-probed once the issue lands. `DATABASE_DEVELOPER`/`APPLICATION_DESIGNER` should
  pin the exact zone boundary together.
- **`usp_ValidateLoad` candidate checks** (uniform `CheckName, Scope, ExpectedCount,
  ActualCount, Detail` shape; **observational — warn, never throw**): record count per issue
  ≈ location count; `meta` vs record `Issue Date`/`Survey Start`/`Survey End` agreement; map
  key vs `Point Code` agreement; unmatched point codes fact↔location **both ways**;
  `Low ≤ Average ≤ High`; NULL-rate on prices within a sane band (~28% observed — flag a
  *jump*, not the level); `Region` value set vs the known 14 (report new values, do not fail);
  aggregate rows (`*RAVG`, `SEREGAVG`, `APPREGAVG`, `CALSAVG`, `USAVG`) present.

---

## 8. Out of scope

The spec exposes **59 paths** (1 auth + 58 data). Everything except the three in §6 is out of
scope for this build: the `daily*`, `midday*`, `weekly*`, `shale*`, `forward*`, `forwardEOD*`,
`mexico{Bidweek,Daily,Forward}*` families and **11** `lng*` feeds (`lngArbCurves`, `lngDESPrices`,
`lngFlow`, `lngLandedCosts`, `lngLandedPrices`, `lngNetback`, `lngShippingCosts`, `lngSlopes`,
`lngVesselRateCurves`, …), each with `*Datafeed` (`.json`/`.txt`/`.csv`/`.xls`),
`*HistoricalData` and `*Locations` variants. All are `jwtAuth`-protected and all likewise
declare **"No response body"**, so each would need its own live probe.

> 🔭 **`/bidweekHistoricalData.json` is the future deep-backfill lever.** It is the natural way
> to load history in a handful of calls instead of one request per issue date. Spec-declared
> params (**spec-read, never probed**): `start_date` (`YYYY-MM-DD`, *default* "today − 365
> days"), `end_date` (`YYYY-MM-DD`, *default* "today"), and **`location` — a single point code,
> defaulting to Henry Hub `SLAHH`**. Note the shape difference that matters: it looks
> **per-location across a date range** (the inverse of `bidweekDatafeed.json`'s all-locations,
> single-date), so a full backfill would be ~163 calls (one per point code) rather than one —
> still far cheaper than one call per issue date per decade, but **its response shape is
> completely unknown** and must be probed before anyone plans around it.

---

## 9. Open questions / unverified (behavioural only — no field is unknown)

Everything below is an **operational/behavioural** unknown. None of it affects the field
lists, types or nullability in §4/§5, which are live-observed.

1. **Is a published issue ever REVISED?** Unknown. The probe captured each issue once, so
   whether NGI restates prices/volumes for an already-published `issue_date` was never
   observed. This decides the merge semantics: a plain last-wins upsert on
   `(IssueDate, PointCode)` is correct if revisions simply overwrite, but if NGI republishes
   with changed values and the business needs the prior print, a history table would be
   required (the OPIS `LPReportHistory` pattern, where the `I`/`U` record-status code is part
   of the key). **The feed carries no revision/version/status field at all**, so a revision
   would be indistinguishable from the original except by comparing values. *Recommendation:*
   upsert last-wins now; re-open if `DATA_QUALITY_VALIDATOR` ever sees a value change on a
   settled issue. **Ask the user / NGI.**
2. **NGI's publication/holiday calendar is not enumerated anywhere** — not in the spec, not in
   the payload. The 404 body only *guesses* ("could be a weekend or holiday"). Combined with
   the finding that 2026-08-01 (a **Saturday**) *was* an issue date while the preceding Friday
   404'd (§3.2), there is **no reliable way to predict which dates publish**. Mitigation is
   already the recommendation: probe every calendar date and treat 404 as normal.
3. **Whether the issue date is always the 1st of the month is NOT established** — only
   **three** issue dates were probed (**2026-06-01, 2026-07-01, 2026-08-01**), all of which
   happened to be the 1st. Three data points do not make a rule, and NGI's own wording
   ("first business day") disagrees with the Saturday observation. Do not encode a
   day-of-month rule.
4. **The 163-code equality between the two endpoints is a same-day observation, not a
   contract** (§5.4). Codes will drift as NGI adds/retires points. Hence: no FK, and an
   observational both-ways reconciliation check.
5. **Token-expiry behaviour was never observed live.** The 24-hour lifetime is read from the
   JWT `exp`/`iat` claims, but no call was made with an expired token, so the actual
   failure response (status + body) is unverified. The 401-re-mint-once handler covers the
   expected case; confirm on first live run.
6. **How far back the datafeed serves history was not probed.** The oldest date requested was
   2026-06-01. Whether `bidweekDatafeed.json` will answer for e.g. 2015-01-02, and whether
   older issues carry the same 11 fields and the same `"None"` sentinel, is unknown. This
   bounds any backfill plan (and is another reason to probe `/bidweekHistoricalData.json`).
7. **Rate limits are unpublished and unprobed.** No numeric limit, no `Retry-After`, no
   `X-RateLimit-*` header appears in the spec, the documentation site or the captured
   responses; no `429` was ever seen (the probe made ~11 calls). Because a 60-day window is
   ~60 requests per run, this is low-risk — but pace conservatively (a low
   requests-per-second cap) and back off on `429`.
8. **`Volume` unit is undocumented.** No unit of measure appears in the spec or the payload.
   Values look like a deal-volume total (max 8 647 on the national average); the plausible
   reading is thousand MMBtu/d, **unverified**. Prices are USD/MMBtu by NGI convention —
   also not stated in the API. Worth one question to NGI so column comments/documentation are
   right.
9. **`Average` weighting method** is NGI methodology, not in the API. Verified only that it is
   **not** the `Low`/`High` midpoint (§4.4 #9).
10. **The spec's `info.description` mentions "an API key"** ("you'll need an email address
    associated with an active NGI data subscription and an API key"), but the **only**
    authentication mechanism in the spec *or* observed live is `email`+`password` → JWT.
    Treat the "API key" wording as legacy/marketing text; if NGI ever issues a separate key
    header, this section needs revisiting.
11. **`USAVG` carrying `Region = "California"`** (§4.6 #3) looks like a vendor data error.
    Worth reporting to NGI; do not "fix" it in the loader — persist as published and flag in
    validation.
