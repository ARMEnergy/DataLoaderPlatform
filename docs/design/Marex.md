# Marex loader — design

Source contract: [`docs/apis/Marex.md`](../apis/Marex.md).
SQL: [`sql/Marex/`](../../sql/Marex/). Code: [`src/DataLoader.Marex/`](../../src/DataLoader.Marex/).

Database `Marex`, schema `arm`, five tables, all merged by primary key.

| Feed | Source event | Target | Key | Live rows |
|---|---|---|---|---|
| `PeriodGroup` | `PeriodGroupSnapshot` | `arm.PeriodGroup` | `(PeriodGroupId)` | 6 |
| `Period` | `PeriodSnapshot` | `arm.Period` | `(PeriodId)` | 724 |
| `Product` | `ProductSnapshot` | `arm.Product` | `(ProductId)` | 1,666 |
| `ClosingPrice` | `ClosingPriceSnapshot` | `arm.ClosingPrice` | `(ExchangeDate, ClosingPriceId)` | 4,227/day |
| `MarketStatistic` | `MarketStatisticSnapshot` | `arm.MarketStatistic` | `(TradeDate, Id)` | 266/day |

---

## 1. Why this loader is shaped differently

The platform's standard loader pulls: a work-unit provider enumerates windows, a reader
fetches each one, a sink merges the rows. Neon does not answer requests — it **pushes**
(see the API doc §1). On connect, the gateway sends one complete snapshot of every entity
down one websocket, whether you wanted all of them or not.

So the run is:

```
connect once  →  capture the opening snapshot  →  merge all five tables from it  →  disconnect
```

Everything downstream of "capture" is the platform's ordinary machinery — work units,
resume keys, the load log, TVP merges, the write gate. Only the extract step is unusual,
and it is confined to `MarexSnapshotSession`.

### One connection, five pipelines

`MarexSnapshotSession` is a singleton. The first pipeline to call `GetSnapshotAsync`
connects and captures; the other four get the same object back.

That is not just an optimisation. The exchange date is a **primary key component** of two
tables, and it comes from the gateway. Five independent connections could straddle a day
boundary and split one run's output across two `ExchangeDate`s, with no error anywhere.
Sharing the capture makes that impossible.

### The feeds run sequentially

There is no I/O to overlap — every feed reads the same in-memory snapshot — so parallelism
would only contend on the SQL write gate. The order is dimensions first
(`PeriodGroup`, `Period`, `Product`, then `ClosingPrice`, `MarketStatistic`) so that a run
which dies partway leaves the fact tables referencing dimension rows that are already
present.

A feed's failure is recorded and the run continues (the Genscape/Argus/ICE/NGX posture):
the five tables have no FK between them and losing one must not cost the others.

---

## 2. What is deliberately NOT built

### The live update stream

After the snapshot the gateway streams `*Update` deltas (`{Items, MessageId, UpdateType}`,
`UpdateType ∈ {Add, Update, Delete}`). The loader subscribes to none of them.

Consuming deltas means a **resident process**. This platform runs loaders as batch jobs
under a per-loader SQL app lock whose entire purpose is to stop long-lived overlapping
runs; a loader that stays connected would hold that lock indefinitely and never report a
run result.

Re-running on a schedule and re-merging the current snapshot by primary key reaches the
same end state for these five entities. The cost is intra-interval movement: prices and
statistics that change between runs are not individually observable. **That is why the
default resume key varies by HOUR** (`MarexResumeKeyStrategy.RunHour`) — schedule the
loader as often as the desired resolution.

If per-tick history is ever needed, that is a different target table (an append-only tick
table) and a resident service, not a change to this loader.

### The rest of the SDK's surface

`INeonApiClient` also exposes historical requests (`RequestHistoricTrades`,
`RequestClosingPrices`, `RequestIndexPrices`, …) and the **trading** surface
(`CreateOrder`, `ModifyTrade`, `CancelOrder`, `EnterSettlementPrices`, …). None is called.
`FakeNeonApiClient` in the tests throws on every one of them, so a future change that
starts using the trading surface fails loudly in the test suite rather than quietly
becoming possible.

### Nested vendor data

The supplied schema has columns only for scalars. Dropped: `MarketStatisticsDto.Bids`,
`.Asks`, `.PeriodIds`, `.ExtensionValues`; `ProductDto.ClearingHouse`, `.MarketProperties`,
`.TradingUnit`, `.StrategyLegs`, `.ExtensionValues`, `.PeriodGroupIds`. Each would need
its own child table.

---

## 3. Resume keys and the status matrix

Work unit key: `feed={FeedId};at={token}`.

| Strategy | Token | Effect |
|---|---|---|
| `RunHour` **(default)** | UTC `yyyyMMddHH` | one load per clock hour |
| `RunDate` | UTC `yyyyMMdd` | one load per UTC day |
| `RunId` | run guid | every invocation reloads |

**There is no settled zone**, and that is the correct design here rather than a missing
feature. A settled unit is one whose source data can no longer change — but the gateway
serves only the *current* state of the market and has no history endpoint for these five
entities. Every run necessarily re-reads the same live set. A stable key would load a feed
once and skip it forever, which for a live snapshot means the table silently stops
tracking the market.

⚠ All tokens are UTC. A local-time hour token goes **backwards** on a fall-back night,
which would let an already-recorded success suppress a legitimate reload for an hour.

`RunDate` is warned about at startup for the same reason: the first successful run of the
day would suppress every later one, and closing prices, settlement prices and market
statistics all move intraday.

### Status matrix

| Situation | Outcome |
|---|---|
| Snapshot captured, feed has rows | success, `RecordsProcessed` = merged rows |
| Snapshot captured, feed legitimately empty | **success, 0 records** — an empty vendor collection is an empty set, not a missing part |
| Token request fails | run fails; message carries the vendor's own text (e.g. `mfa_required`) |
| Gateway → `Unauthorized` | run fails immediately, does not wait out the timeout |
| Gateway → `IncompatibleVersion` | run fails immediately, names `lib/neon` |
| Connect refused by the SDK | run fails |
| Snapshot incomplete at timeout | run fails; message lists which parts arrived and which are missing, and names the wrong-hub-name cause |
| Unit already loaded for this token | skipped |

The "legitimately empty vs broken" distinction is the one the platform's status matrix
cares about most. It is handled at capture: a null vendor collection becomes an empty
array and still **counts toward snapshot completeness**, so an empty feed produces a
zero-record success rather than a timeout.

---

## 4. Mapping decisions

### `ExchangeDate` / `TradeDate` come from the gateway

Stamped onto every row of both fact tables from `ExchangeDateSnapshot`, and passed through
the TVP. They are *not* defaulted in the merge proc with `CAST(SYSDATETIME() AS DATE)` —
that would put a key component on the **server's** clock and time zone instead of the
exchange's.

Because the date leads the key, a re-run during the same trading day updates that day's
snapshot in place, and the next trading day inserts a new generation.

### The dimensions have no date in their key

`Product`, `Period` and `PeriodGroup` are current-state reference data, refreshed in place.
Keying them by date would multiply them by every run and leave the fact tables nothing
stable to join to.

### No delete-by-absence — including for the dimensions

Neither fact table can have it (each batch is one day's snapshot; deleting rows absent from
it would wipe every other `ExchangeDate`).

For the dimensions it is a deliberate choice: a product the vendor retires stops appearing
in the snapshot, but historical `arm.ClosingPrice` and `arm.MarketStatistic` rows still
reference its `ProductId` and would otherwise lose the only row saying what it was. The
cost is that dimensions accumulate dead rows; `ModifiedAtUtc` distinguishes them (a live
row carries the most recent run's stamp).

This is also why `999_DropMarexObjects.sql` warns about the dimensions specifically —
dropping them is not recoverable from the gateway, which only serves what is live today.

### Enum columns hold names

`MarketStatistic.State`, `.LastTradeEventSource`, `.LastTradeAggressorSide`,
`.UpdateReason` and `Product.ProductType`, `.TradeChartDataSource` arrive as integers and
are stored as enum **names**, because the supplied DDL made them `VARCHAR`.

A value the vendored SDK does not define stringifies to its number rather than throwing —
so it lands as a visible `"37"` instead of silently taking a neighbouring name or failing
the batch. `arm.usp_ValidateLoad` reports any such column, and the live smoke test fails
on one.

### Year-1 timestamps become NULL

⚠ `ClosingPriceDto.Time`/`.PreviousTime` are non-nullable in the SDK, so the vendor sends
`0001-01-01` to mean "no value" — observed on exactly the rows whose `PreviousPrice` is
null. `DATETIME2` accepts year 1 without complaint, so this would store silently and read
back as a real timestamp. `MarexMapper.NullIfUnset` maps year ≤ 1 to NULL (year rather
than exact `DateTime.MinValue`, so a value carrying a stray offset conversion is caught
too).

### Date truncation is safe here — and was checked

`Period`'s four `DATE` columns truncate a `DateTime`. Verified against all 724 live
periods: the only non-midnight values are intraday trading stamps and three `DeliveryEnd`s
at `23:59`, whose date part is already the intended day. Nothing at `23:00Z`, which is the
pattern that would signal a London-midnight-as-UTC value and make truncation off by one.

`NoticeOfShipment` keeps its time (`DATETIME2(7)`) because its live values are all
`23:59:59`.

---

## 5. The TVP contract

A TVP binds **by position**. `MarexDescriptors` is the single ordered source of truth:
`MarexMapper` fills each row by walking the same list the sink builds its `DataTable`
from, so mapper and sink cannot drift.

What *could* drift is the descriptor versus `sql/Marex/002`, so
`MarexTvpContractTests` parses the real `.sql` and asserts name + order + type — plus that
`001`'s `CREATE TABLE` matches, that no TVP declares `ModifiedAtUtc`, that every key
column is `NOT NULL`, and that each proc takes its own type.

`arm.MarketStatisticTvp` is the dangerous one: ten consecutive `DECIMAL(18,8)` columns
(`Average` … `SettlementPrice`) and four consecutive `VARCHAR(50)`s. A one-position slip
anywhere in either run type-checks perfectly and loads garbage.

Oversized strings are **truncated** (non-key) or the row is **dropped** (key) — clipping a
key does not fail, it merges the row into a different one.

---

## 6. Security

* `ClientId`, `Username` and `Password` all default to `SEE_DB` and resolve from
  `core.Param(LoaderName='Marex', ParamName=…)` via `AddLoaderSettings<MarexSettings>`.
  Startup warns on each unresolved sentinel.
* The loader logs the endpoint and the user name; never the password, never the token.
* ⚠ The SDK itself logs the **full bearer token** in its connection-config line at INFO.
  SDK logging is therefore off by default and, when enabled, goes through
  `MarexSdkLogBridge`, which drops that line and anything containing a JWT.
  `MarexSdkLogBridgeTests` pins both.

---

## 7. Verification status

| | |
|---|---|
| Build | ✅ clean, solution-wide |
| Tests | ✅ 108 offline, 2,981 solution-wide |
| SQL parse-check | ✅ all 4 scripts, ScriptDom TSql160 |
| **Read path, live** | ✅ **verified end to end 2026-09-21** — token, handshake, all six snapshots, every live row mapped and width/type-checked |
| SQL deployed | ❌ **never run against a server** |
| Data loaded / validated | ❌ no live database |

Marex is **build-only** until its SQL is deployed. The read path is genuinely verified;
the write path is not. `arm.usp_ValidateLoad` exists for the first real load.

Re-run the live check with:

```bash
MAREX_LIVE=1 MAREX_USERNAME=... MAREX_PASSWORD=... MAREX_CLIENTID=... \
  dotnet test tests/DataLoader.Marex.Tests -c Release --filter "Category=Live"
```

---

## 8. Open questions for Marex

1. A newer `NeonMarkets.NeonAPI` matching gateway `minVersion` 3.37.0.1522 (current
   vendored SDK: 3.15.1.22 — accepted today, but the gap is real).
2. Confirmation that the integration account will not have MFA enabled (an Auth0 password
   grant cannot survive it).
3. Whether concurrent-session limits apply to this account, if the loader is ever
   scheduled more frequently than hourly.
4. Whether `arm.Product`/`arm.Period` should retain retired entities indefinitely (current
   behaviour) or be soft-deleted with an `IsActive` flag.
