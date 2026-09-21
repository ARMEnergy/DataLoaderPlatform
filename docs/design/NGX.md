# NGX loader — design

Source contract: [`docs/apis/NGX.md`](../apis/NGX.md). SQL: `sql/NGX/`. Code:
`src/DataLoader.NGX/`. Tests: `tests/DataLoader.NGX.Tests/`.

---

## 1. Shape

Two independent pipelines on the platform's standard `LoaderPipelineBase` loop, one per
feed, both landing in the `NGX` database's `arm` schema.

```
                       dbo.[Index]  (read-only, IndexType='IndexPrice')
                            |
                            v
  IndexPrice  ──  work units (batch of <=10 ids x window chunk)
                       │
                       ├─ NgxIndexPriceReader ── indexPrice.xml ──┐
                       │      (Basic auth, pageSize, paging,       │
                       │       403-narrowing)                      │
                       │                                           v
                       └──────────────────────────────>  arm.usp_BulkMergeIndexPrice
                                                                   │
                                                                   v
                                                            arm.IndexPrice

  Strip       ──  work units (one date chunk each)
                       │
                       ├─ NgxStripReader ── stripTradingSummaryXml.xml ──┐
                       │                                                  v
                       └────────────────────>  arm.usp_BulkMergeStripTradingSummary
                                                                          │
                                                                          v
                                                          arm.StripTradingSummary
```

The pipelines share nothing: no barrier, no FK, no ordering constraint. `NgxModule`
records a feed's failure and carries on, so losing one feed never costs the other — the
Genscape/Argus/ICE posture.

Both readers emit the sink's row type directly, so the transformer is
`IdentityTransformer`.

---

## 2. Windows and work units

Both windows are anchored to **first-of-month** boundaries in **US Central**, so a run
is reproducible and does not drift with the time of day.

### IndexPrice

Window: first of the month **3 months back** … first of the month **6 months forward**.
On 2026-09-18 that is `2026-06-01 … 2027-03-01`.

A unit is **one batch of up to 10 index ids × one window chunk**. With the shipped
defaults (47 entitled ids, one 12-month chunk) that is **5 units and 5 requests** for
the whole feed — ~1,200 rows each, comfortably inside the 20,000-row page.

The 10-id ceiling is a hard vendor limit (11 → `403`), so `IndexIdsPerRequest` is
clamped to 10 in the provider and warned about at startup rather than trusted.

### StripTradingSummary

Window: first of the month **1 month back** … first of the month **1 month forward**.
On 2026-09-18 that is `2026-08-01 … 2026-10-01`.

A unit is **one date chunk**, weekly by default → ~9 units of ~8,000 rows / ~7 MB each.

Chunking is not an optimisation here, it is the safety margin. This endpoint has **no
pagination envelope at all** — no `truncated`, no `fullListSize` — so if the vendor ever
caps a response there is nothing in the body to detect it with. A calendar month is
36,430 records and 29 MB; a week keeps requests far from any plausible cap and makes a
failed chunk cost a week rather than a month. Chunks are emitted newest-first so a run
cut short has done the days people are watching.

---

## 3. Resume keys

```
IndexPrice   ngx:IndexPrice:ids=1,2,3,…:2026-06-01..2027-03-01:exec=2026-09-18:run={token}
Strip (hot)  ngx:StripTradingSummary:2026-09-14..2026-09-20:run={token}
Strip (cold) ngx:StripTradingSummary:2026-02-16..2026-02-22
```

(Both strip spans are real epoch-anchored chunks. The cold one is deliberately from
outside the default window: with the shipped 75-day horizon **nothing** in the standard
two-month window is settled — cold keys only appear once `StripMonthsBack` is raised for
a backfill.)

### `ExecutionDate` is in the index key explicitly

Not merely implied by the run token — and this is a fix for a real defect, not belt
and braces for its own sake.

`ExecutionDate` is **Central**; the `RunDate` hot token was originally **UTC**. A run at
`03:00Z` on the 19th is still the evening of the **18th** in Chicago and stamps
`ExecutionDate 2026-09-18`; a run at `13:00Z` the same UTC day stamps `2026-09-19`. The
window bounds do not vary inside a month, so the two units produced a **byte-identical
key** — the load log skipped the second as already done and that day's snapshot
generation was never written. Silent, permanent, and exactly the shape a
nightly-plus-morning-catch-up schedule produces.

Two changes close it: `RunDate` is now stamped in **Central** so it agrees with
`ExecutionDate`, and `exec=` is in the key so the two can never disagree again whatever
the strategy is. `RunHour` deliberately stays UTC — an *hour* token has to be monotonic,
and 01:00 local occurs twice on a fall-back night. A *date* token has no such problem.
`NgxWorkUnitTests.Index_TwoRunsOneUtcDayButTwoCentralDays_ProduceDifferentKeys` guards it.

### The index feed has no settled zone, deliberately

`ExecutionDate` **leads** the primary key. Every run is *supposed* to write a fresh
generation of the same delivery window — that is what makes the settlement walk
(`Projected` → `Pending` → `Settled`) observable after the fact, and it is why the
incumbent holds 735 distinct `ExecutionDate` values over 2.9M rows.

A stable key would therefore be actively wrong: the load log would skip the unit from
day two onward and the table would freeze at one snapshot. With the default `RunDate`
token the unit re-pulls once per UTC day and a same-day retry is a cheap skip.

### The strip feed does have one

A strip row is a trade that happened, not a snapshot of a moving value. Once a chunk is
older than `StripSettledAfterDays` it gets a stable key, loads once, and is skipped
cheaply forever — which is what makes a backfill affordable.

The shipped default is **75 days**, which keeps the whole two-month window hot, as the
specification asks. 75 rather than a rounder number because of the worst case: the
window starts on the first of *last* month and its oldest day is at its oldest when
viewed on the *last* day of *this* month — `(31-1) + 31 = 61` days. Anything below that
turns the start of the window cold at the end of a long month but not at the start of
one, so the feed would quietly behave differently depending on today's date. (The
original 45 had exactly that flaw and it went unnoticed until the chunk anchoring below
made it visible.) The settled zone therefore exists for **backfills**: raise
`StripMonthsBack` and everything past the horizon loads once and is then skipped.

A chunk ages by its **newest** day: while any day it covers is still hot the whole chunk
must be re-pulled, because one request retrieves them together. Chunks ending in the
future have a negative age and are therefore hot, which is correct — those trades do not
exist yet.

### Strip chunks are anchored to a fixed epoch

Chunk bounds come from day-number arithmetic (`dayNumber - dayNumber % chunkDays`), not
from the window start, and are never clamped to the window.

The window start walks forward to the 1st of each month. Cut from it, every boundary
would move when the month rolled — a settled chunk minted in September as
`2026-08-29..2026-09-04` would reappear in October as `2026-09-01..2026-09-07`, a
different key — so the entire settled zone would re-load every month instead of being
skipped, and a "stable" settled key would not actually be stable. Anchoring means a
given trade day always falls in the same chunk for as long as `StripChunkDays` is
unchanged.

The cost is that the first and last chunks reach up to `chunkDays-1` days outside the
window. That is harmless: the extra days merge idempotently, and a request past the end
of the data returns a well-formed empty list.

### Two things that change keys

* **The id list is part of the index key.** Adding or removing a row in `dbo.[Index]`
  reshuffles the batches, so every key changes and that day's window reloads under fresh
  keys. That is the safe direction — the merge is idempotent, whereas a stale key that
  no longer covers the same ids would leave a silent gap.
* **Window bounds are part of both keys.** Changing `StripChunkDays` or either
  `Months*` setting moves the chunk boundaries and the old keys stop matching, so the
  affected range reloads rather than being skipped. Same reasoning.

---

## 4. The four things that fail silently

Each of these produces a `200`, a plausible row count and no exception when done wrong.
They are the reason this loader is more than a mapping.

### 4.1 Authentication is HTTP Basic, not the bearer token

The documented `/api/v2/authentication/token` endpoint works but its token is **ignored**
on the `.xml` paths; fifteen placements all returned `302` to ICE SSO. The loader uses
Basic, and `AllowAutoRedirect = false` is load-bearing: the SSO target answers `200` with
33 KB of HTML, so following the redirect would turn an expired password into a permanent
silent "zero records". Any `3xx` is raised as a credential error, and a `200` whose body
lacks the expected root element is raised as malformed.

### 4.2 One unentitled index id fails the whole batch

`403` on this endpoint means entitlement, not auth — the "Access Denied" page is served
*logged in*. A batch of ten where one id is unentitled loses all ten.

Two defences: `dbo.[Index]` is filtered to `IndexType='IndexPrice'` (the 172
`CrudeIndexPrice` ids are not entitled here), and the reader **narrows** a refused batch
to single-id requests, skipping only the ids genuinely refused and warning once per id.
Verified live: a `1,2,3,85` batch yields 37 rows from 1–3 plus a warning naming 85.

`403` is never retried — it is permanent for that request, and retrying only delays the
narrowing.

### 4.3 The index response truncates at 50 rows by default

Omit `pageSize` and you get 50 of 1,237, with a `200` and a well-formed body. The loader
sends an explicit `pageSize` **and** checks `<truncated>`, then pages until it has
`fullListSize` records. Two adjacent traps: the paging parameter is `page` (`pageNumber`
is accepted and silently returns page 1 again), and `pageSize` caps at 20,000
server-side.

Progress is counted in **records seen**, not rows kept — a record dropped by the key
check still advanced the vendor's cursor, and counting kept rows would make the target
unreachable and page past the end of the data every time.

A read that provably could not finish **fails the unit** rather than returning a short
window. Two cases throw: reaching the page bound with pages still non-empty (the server
has more to give and is not paginating as its envelope describes), and a page whose
leading record id a previous page already served (the signature of a server ignoring
`page`, which would otherwise pile up duplicates until the count crossed `fullListSize`
and report a "complete" window whose real tail was never fetched). The bound itself is
sized from the records actually served per page, not the `pageSize` requested, so a
server honouring a smaller page is paged to completion rather than cut off.

An **empty** page is the one case that stops quietly: the server has nothing further to
give, so its `fullListSize` merely overstated what it would serve. Warning, not failure —
otherwise a vendor bookkeeping bug would fail every run forever.

### 4.4 Timestamps must be converted to US Central

The vendor stamps Mountain (`-07:00` / `-06:00`); the incumbent stores Central. Verified
on both sides of DST against real incumbent rows. The conversion goes **through the
offset** via `TimeZoneInfo`, never by adding an hour — Mountain and Central shift on the
same dates today, but that is a coincidence to rely on only until it isn't, and the
vendor could start stamping UTC.

This matters more than a mis-stamped column: `TradeDateTime` is a **primary key
component**, so a wrong hour forks the key instead of overwriting.

`NgxTime` throws at startup if neither `Central Standard Time` nor `America/Chicago`
resolves, rather than falling back to UTC — a silent five-hour shift on every row would
be far worse than a loud failure.

---

## 5. Parsing

Both readers stream with `XmlReader` and materialise **one record element at a time**
via `XNode.ReadFrom`, then project straight into TVP column order. Memory is bounded by
row count, not document size — which matters because a month of strip trades is 29 MB of
XML and `MaxConcurrentWorkUnits` defaults to 4.

⚠ `XNode.ReadFrom` and `ReadElementContentAsString` both leave the reader on the **next**
node. An unconditional `while (reader.Read())` around them drops every second record —
this was a real bug caught by the tests, and both loops now advance explicitly only when
they have not already consumed. Any edit to those loops needs the same care.

Element lookup is by **local name**, ignoring the namespace. A namespace-qualified
lookup would work today but would turn a vendor namespace bump into every column
silently going NULL behind a `200`; matching on local name degrades to "still works".

`DtdProcessing.Prohibit` and a null `XmlResolver` are set on every read, so a hostile or
malformed document cannot become an XXE fetch or a billion-laughs expansion. The SSO
login page's `<!DOCTYPE html>` trips this, and the resulting `XmlException` is
re-thrown as a malformed-response naming credentials as the likely cause — the operator
should not have to decode a DTD complaint.

Amounts parse with `NumberStyles.Number` under `InvariantCulture`, because
`<amount>313,100</amount>` is 313,100 and a plain invariant parse rejects it outright
while a comma-decimal culture reads it as 313.1.

A record missing any primary key component is **dropped with a warning**, never merged
under a blank key.

---

## 6. Writing

One `NgxTvpSink`, parameterised by the table descriptor. The DataTable is built by
walking `NgxTableDescriptor.Columns`, and both readers filled their row arrays from the
same list in the same order, so the two cannot drift.

What *could* drift is the descriptor versus `sql/NGX/002`, so
`NgxTvpContractTests` parses the real `.sql` and asserts name + order + type against the
descriptor. That matters especially for `arm.IndexPrice`, which interleaves four
`DECIMAL(18,8)` and five `VARCHAR(50)` columns — a one-position slip type-checks
perfectly and loads garbage.

Oversized **non-key** strings are **truncated to the declared width and warned about**,
not rejected: SqlClient aborts the entire TVP on "String or binary data would be
truncated", so one long label would otherwise cost a whole work unit's window. The source
row array is cloned before truncation so a retry re-sends the original.

An oversized **primary key** string is a different matter and the row is **dropped with
an error** instead. Clipping a key does not fail — it merges the row into a *different*
one via the merge predicate, which is silent corruption rather than a clipped label. Two
of the strip key columns are strings (`StripType`, `ExchangeReference`), and since the
vendor already recycles `ExchangeReference` across dates and markets, a truncated one is
genuinely likely to land on a real trade. `NgxColumn.IsKey` carries the distinction.

`ModifiedAtUtc` is DB-stamped and never a TVP column. The merges set it explicitly with
`SYSUTCDATETIME()`, so rows this loader writes hold true UTC even though the table
default is `sysdatetime()` (server local) — an oddity kept from the supplied DDL rather
than silently "fixed".

Neither merge deletes by absence: each unit carries one slice of the window, and a
`WHEN NOT MATCHED BY SOURCE THEN DELETE` would wipe every other unit's rows — and for
the index table, every prior `ExecutionDate`.

---

## 7. Shadowing the incumbent

The `NGX` database already contains, and is still being written by, the Conduit process:

| table | rows | status |
|---|---|---|
| `dbo.IndexPrice` | 2,946,561 | **live**, written today |
| `dbo.StripTradingSummary` | 8,606,875 | **live** |
| `dbo.IndexPrice_2` | 1,366,503 | retired 2023-11 (held crude + gas) |
| `dbo.[Index]` | 219 | the index catalogue — this loader **reads** it |

`arm.IndexPrice` and `arm.StripTradingSummary` are column-identical to their `dbo.*`
counterparts save for the Conduit bookkeeping columns (`ConduitLastUpdate`, `Checksum`)
being replaced by `ModifiedAtUtc`. The loader writes only `arm.*` and reads only
`dbo.[Index]`; `999_DropNgxObjects.sql` names no `dbo` object and a test asserts that it
never will.

That overlap is an asset, not just a hazard: the field mapping was validated row-by-row
against incumbent rows for the same keys, which is how the US-Central convention,
the `'Natural Gas'` constant and the always-NULL `AlternateTraded*` columns were
established as facts rather than guesses.

**Expected volumes**, from the incumbent: ~3,800 index rows per `ExecutionDate` across
~39 of the 47 configured indices (the other 8 have no trades in the window), and
~1,300–1,600 strip rows per weekday / ~250–400 per weekend day.

---

## 8. Validation

`arm.usp_ValidateLoad` runs after both pipelines and is **observational only** — it never
throws and never changes the run's outcome, because the data is already merged by the
time it runs. It is passed the loader's own US-Central `ExecutionDate` rather than
defaulting to the server's date, so a run straddling midnight still checks the rows it
actually wrote.

| Check | What it catches |
|---|---|
| `IndexPriceNoSnapshot` | the run wrote nothing at all (every other index check is vacuously clean on an empty snapshot) |
| `IndexPriceRowCountDrift` | >10% move against the previous snapshot |
| `IndexPriceIndexMissingDrift` | the number of configured-but-empty indices **moved** — an entitlement revocation or a catalogue edit |
| `IndexPriceFutureLastUpdate` | the Central conversion applied twice, or raw UTC slipping through |
| `IndexPriceUnexpectedAlternate` | a non-NULL `AlternateTraded*`, i.e. a vendor change or a TVP column shift |
| `IndexPricePartialQuantity` | `TradedAmount` without `TradedUnit` — the parser dropped a child rather than the vendor omitting the block |
| `StripTradeTimeOutOfBand` | trades outside 05:00–18:00 Central — the time zone regression detector |
| `StripFutureTradeDateTime` | same family |
| `StripEmptyWeekday` | a weekday with no trades — the only backstop against a silent cap on the envelope-less endpoint |

Two deliberate choices about **signal quality**, because a report nobody reads catches
nothing:

* `IndexPriceIndexMissingDrift` reports **one** finding carrying a count, and only when
  that count changes. About 8 of the 47 indices are routinely empty — they simply have
  no trades in the window — so a row-per-index version put 8 warnings in the log on a
  perfectly healthy run.
* The proc takes `@IndexFeedRan` / `@StripFeedRan` and skips a feed's checks when that
  pipeline did not run. Without them, running only `StripTradingSummary` would report
  `IndexPriceNoSnapshot` on every run forever.

`StripEmptyWeekday`'s weekday test is `DATEFIRST`- and language-**independent**:
`DATEDIFF(DAY, '19000101', d) % 7 < 5` (1900-01-01 was a Monday), with the day name from
a `CASE` rather than `DATENAME`. `DATEPART(WEEKDAY, …)` would have been tolerable here —
it is a report, and at worst it names a Saturday — but there is no reason to accept a
session-dependent answer when a deterministic one costs nothing. No `DATEFIRST`-dependent
expression appears anywhere in a key or a merge predicate.

The day list comes from a `VALUES` constructor rather than `master.dbo.spt_values`,
which is undocumented and a cross-database dependency in an otherwise self-contained
script.

---

## 9. Verification status

| | |
|---|---|
| Release build | 0 warnings, 0 errors |
| NGX tests | 265 passing |
| Solution-wide tests | 2,873 passing, 0 failures |
| SQL | all 4 scripts parse clean (ScriptDom `TSql160Parser`) |
| Review gate | passed after fixes — see §10 |
| Test gate | passed — 102 cases added by the gate, plus the provider suite below |
| **Read path** | **verified live end to end** — see below |
| **SQL deployment** | **never run.** `arm.*` does not exist in any database |

Live verification drove the real readers against the real endpoints and compared against
independent `curl` measurements and incumbent rows:

* `dbo.[Index]` → 47 ids
* strip 1-Sep-2026 → 1,601 rows (curl: 1,601), PK unique 1,601/1,601
* the known trade `48000000003842` → `TradeDateTime 2026-09-01 07:37:06`, matching the
  incumbent exactly, with `TradedVolumeAmount` 2500 and `PriceAmount` 1.2
* index ids 1–10 over the full window → 1,237 rows (curl `fullListSize`: 1,237), PK
  unique, `CommodityType` constant, all `AlternateTraded*` NULL
* 403-narrowing on a batch containing crude id 85 → 37 rows recovered, 85 skipped
* `pageSize=50` over the same window → all 1,237 rows collected by the pager
* a far-future window → a clean 0 rows

A **real vendor outage** was also observed, on 2026-09-19: the whole host — both `.xml`
documents and the `/api/v2` token endpoint — returned `503` with an Apache error page.
The loader handled it correctly end to end: retries exhausted on the transient status,
then `HttpRequestException`, which fails the work unit and records it in `core.LoadLog`
rather than scoring 299 bytes of HTML as an empty window.

The loader is **disabled** in `appsettings.json` (`Platform:EnabledLoaders` does not
list `NGX`) and its credentials resolve from `core.Param(LoaderName='NGX')`, which has
no rows yet.

---

## 10. Defects found in review, and fixed

Recorded because each was a silent failure that the build, the tests and the live
verification had all been green through.

| | Defect | Fix |
|---|---|---|
| 1 | **Index key collision.** `ExecutionDate` is Central, the `RunDate` token was UTC, and the key carried neither. Two runs on the same UTC day but different Central days produced an identical key, so the load log skipped the second and that snapshot generation was never written. | `RunDate` stamped in Central; `exec=` added to the key. §3 |
| 2 | A dead `NgxXml.StreamRecords` helper still carried the `XNode.ReadFrom` double-advance bug (every second record dropped) and the class doc advertised it as *the* streaming mechanism. | Deleted, with a note explaining why no such helper should exist. §5 |
| 3 | **Paging counted rows kept, not records seen.** One record dropped by the key check made `fullListSize` unreachable, so the pager ran past the end; against a server that clamps an out-of-range `page` to page 1 it would have collected duplicates and reported success with the tail missing. | Count `RecordsSeen`; add an absolute page bound. §4.3 |
| 4 | **Total entitlement loss read as an empty window.** If narrowing refused *every* id, the unit returned empty and was logged as a success — a revoked entitlement was indistinguishable from "no data". | Throw when all ids are refused; partial refusal stays a warning. §4.2 |
| 5 | **The sink truncated primary key strings.** A clipped `ExchangeReference` or `StripType` merges into a *different* trade rather than failing. | Key columns are dropped with an error, never truncated. §6 |
| 6 | `HttpResponseMessage` was never disposed on the success path or the `EnsureSuccessStatusCode` throw, leaking a connection per failure under `ResponseHeadersRead`. | `SendAsync` returns an owned response; every exit disposes or hands over. |
| 7 | **Offset-less timestamps fell back to the server's zone**, so the same payload parsed differently per host — and `TradeDateTime` is a key component. (`RoundtripKind` was also a no-op on the `DateTimeOffset` overload, so the comment described a protection that was not there.) | Require an explicit offset; reject otherwise. §4.4 |
| 8 | `StripSettledAfterDays = 45` did **not** cover the window as documented — the oldest day reaches 61 days at the end of a long month, so the window went half-cold depending on the date. | Default raised to 75, with the arithmetic written down. §3 |
| 9 | Strip chunk bounds were cut from the window start, so every boundary moved when the month rolled and no settled key was stable across it. | Epoch-anchored chunks. §3 |
| 10 | `usp_ValidateLoad` emitted ~8 warnings on a healthy run and fired index checks even when that feed was disabled. | Collapsed to a drift count; `@IndexFeedRan`/`@StripFeedRan`. §8 |

Also tightened without being defects: the row-count-drift query is bounded to 14 days
rather than scanning all history; `StripEmptyWeekday` is now `DATEFIRST`- and
language-independent and no longer depends on `master.dbo.spt_values`; and an
`INSERT`/`VALUES` positional-alignment test was added, since the MERGE INSERT is the one
place left where a one-position slip type-checks.

### From the test gate

| | Defect | Fix |
|---|---|---|
| 11 | `ParseCentralTimestamp("2026-09-01")` returned midnight instead of null — the offset guard read the `-01` of a bare date as a `-HH` offset, so a date-only value could enter a key column. | `\d{2}:\d{2}` prefix on the offset pattern. §4.4 |
| 12 | **`NgxIndexPriceWorkUnitProvider` was untestable.** `NgxIndexCatalog` was `internal sealed` with a non-virtual method, so id batching — the 10-id vendor ceiling, the most consequential constraint in this loader — had no coverage above `Math.Clamp`. | Catalog un-sealed and the method made virtual (the precedent `NgxTvpSink` already sets); `NgxIndexProviderTests` added. |
| 13 | **The page bound was sized from the `pageSize` we REQUESTED**, so a server honouring a smaller page was cut off early — 15 of a claimed 10,000 records, reported as success. | Bound sized from records actually served per page. |
| 14 | Reaching the bound, or a server ignoring `page` and re-serving page 1, **terminated quietly** with a partial window recorded as a success. Because `ExecutionDate` leads the primary key, that day's snapshot would stay permanently short with nothing to come back for it. | Both now FAIL the unit; a repeated leading record id is the ignoring-`page` detector. An EMPTY page still stops quietly — the server has nothing more to give, so its `fullListSize` was merely overstated. |

Declined, with reasons: making the XML reads async (the blocking read holds a thread per
concurrent unit, which at the shipped concurrency of 4 is not worth the churn), and
treating the DST fall-back hour as a bug — two instants an hour apart do collapse to one
Central wall clock and could collide on the strip primary key, but that is inherent to
storing Central in `DATETIME2`, the incumbent does the same, and 01:00–02:00 Central on
the first Sunday of November is a closed market. It is pinned by test rather than fixed.

---

## 11. Not built

* **Crude index prices.** 172 `CrudeIndexPrice` ids in `dbo.[Index]`, a separate
  endpoint, not entitled on `indexPrice.xml`. They are the origin of the
  `CommodityType` and `AlternateTraded*` columns, and `arm.IndexPrice` already has the
  shape to receive them — a third pipeline plus a `CommodityType` of `'Crude Oil'` would
  do it.
* The `/api/v2` REST feeds: `margin-requirements`, `market-settlement-prices`, `trades`,
  and `index-price-detail` (per-delivery detail with `highPrice`, `lowPrice`, `altPrice`,
  `exchangeRate`).
