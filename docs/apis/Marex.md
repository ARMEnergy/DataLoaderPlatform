# Marex "Neon" crude market gateway

Vendor: **Marex** (SDK authored by Spectron Services). Product: **Neon**.
Access is through the vendor's own .NET SDK, `NeonMarkets.NeonAPI` **3.15.1.22**,
vendored in [`lib/neon`](../../lib/neon/README.md). There is no REST API for this data.

**Verified live 2026-09-21** against `https://app-ca.neon.markets/api/crude` with the ARM
Energy account: full Auth0 token grant, SignalR websocket handshake, and all six opening
snapshots received and mapped. Counts that day: 6 period groups, 724 periods, 1,666
products, 4,227 closing prices, 266 market statistics.

> This document records what was **observed**, and flags where it contradicts the vendor's
> own integration note (`NEON_LOADER_INTEGRATION.md`), which was written from reflection
> alone and never run.

---

## 1. Shape: it pushes, it does not answer

Every other loader in this repo pulls — ask for a window, get an answer. Neon does not
work that way.

1. The client acquires an Auth0 access token.
2. It opens a **SignalR websocket** to the gateway.
3. The gateway pushes an **unsolicited opening snapshot** of every entity.
4. It then streams **deltas** (`*Update` events) until the socket closes.

There is no request that means "give me the closing prices". The snapshot is the read.

The SDK drives this itself: on connect it invokes the hub method `SnapshotRequest` with
`{Compress: true, PartitionHistorical: true}`, receives a compressed payload,
decompresses it, and raises one event per entity.

---

## 2. Authentication — Auth0 resource-owner password grant

Tenant: `login.neon.markets` (standard Auth0 OIDC discovery at
`/.well-known/openid-configuration`). Public client — **no client secret**.

```
POST https://login.neon.markets/oauth/token
grant_type=password, username, password, client_id, audience, scope
```

### ⚠ You do not need to implement this

The vendor's integration note marks the Auth0 domain and client id as `[CONFIRM]`, states
they are "*not parameters of `NeonApiClient` or `PasswordConnectionData`*", and recommends
hand-rolling the token call so you can control the `audience`.

**That is wrong.** `PasswordConnectionData` has both:

```csharp
public class PasswordConnectionData {
    public string UserName { get; set; }
    public string Password { get; set; }
    public string Domain   { get; set; }   // <- login.neon.markets
    public string ClientId { get; set; }   // <- issued by Marex
    public string[] AdditionalScopes { get; set; }
}
```

`client.GetToken(...)` fills in the audience itself and returns a `BoolResult<string>`
whose `.Data` is the JWT. Verified live. `MarexSdkContractTests` asserts those two
properties still exist, because the whole auth path depends on them.

The issued token decodes to `aud: https://app.neon.markets/api`, `scope: crude`,
`permissions: [crude, insights:content-reader]`, RS256, 24-hour expiry. No MFA on this
account (MFA would fail the grant with `mfa_required`).

---

## 3. ⚠ `NeonApiConfig.Name` is the SignalR HUB NAME

The single most expensive thing to get wrong, and the one the vendor's note gets wrong.

```csharp
// SignalRClient.Implementation.SignalRConnector.Start(), decompiled:
_hubProxy = _hubConnection.CreateHubProxy(_clientConfig.Name);
```

`Name` is passed straight to `CreateHubProxy`. The vendor's note suggests
`Name = "ARM-DataLoader"` — an application label. Setting it to anything that is not a
real hub produces:

```
OnServerError : StatusCode: 500, ReasonPhrase: 'Internal Server Error'
  Server: Microsoft-HTTPAPI/2.0
SignalRConnector: Schedule reconnect attempt in 5s time
```

...forever. `ConnectionStatus` sits on `Connecting`, no snapshot ever arrives, no
exception is thrown, and `Connect()` itself returned `IsSuccess = true`. The only symptom
is a run that hangs until its timeout.

**The correct hub is `Gateway.Crude`**, which the SDK supplies itself when `Name` is
`null`:

```csharp
// Neon.API — NeonApiConfig.CreateSignalRConnectionParams(), decompiled:
Name = (Name ?? "Gateway.Crude"),
```

So leave `MarexSettings.HubName` empty. The loader warns if it is set to anything else.

---

## 4. ⚠ Snapshots arrive BEFORE the state reaches `Connected`

Observed ordering on a live connection:

| t (s) | event |
|---|---|
| 0.00 | `Connect()` returns, state → `Connecting` |
| 0.93 | websocket transport up |
| 2.10 | first heartbeat validated |
| 2.11 | SDK invokes `SnapshotRequest` on the hub |
| 4.90 | payload received, decompressing |
| 5.00 | **all six snapshot events fire** |
| ~6 | state → `Connected` |

Two consequences:

* **Subscribe before `Connect()`.** A handler attached afterwards races the arrival.
* **Wait on the snapshots, not on `ConnectionStatus == Connected`.** Blocking on the
  state first works by luck on a fast link.

`MarexSnapshotSessionTests.Snapshot_CompletesWithoutEverReachingConnectedState` pins this.

---

## 5. The six opening events

| Event | Payload | Collection property | Live count |
|---|---|---|---|
| `ExchangeDateSnapshot` | `DateTime` | — (the value itself) | `2026-09-21T00:00:00Z` |
| `PeriodGroupSnapshot` | `PeriodGroupSnapshotDto` | `.PeriodGroups` | 6 |
| `PeriodSnapshot` | `PeriodSnapshotDto` | `.Periods` | 724 |
| `ProductSnapshot` | `ProductSnapshotDto` | `.Products` | 1,666 |
| `ClosingPriceSnapshot` | `ClosingPriceSnapshotDto` | `.ClosingPrices` | 4,227 |
| `MarketStatisticSnapshot` | `MarketStatisticsSnapshotDto` | `.MarketStatistics` | 266 |

The wrapper property name differs per entity, and the event is
`MarketStatistic`**Snapshot** (singular) while its DTO is `MarketStatistic`**s**`SnapshotDto`
(plural). `MarexSdkContractTests` pins all of them.

### ⚠ `ExchangeDate` is the only source of the trading day

Neither `ClosingPriceDto` nor `MarketStatisticsDto` carries a date. The trading day comes
**only** from `ExchangeDateSnapshot`, and it leads the primary key of both target tables.
Substituting any local clock forks the key whenever the two disagree — which they do for
any run outside the exchange's own day boundary, and for any run straddling midnight.

---

## 6. Field-level observations

### `ClosingPriceDto`

```csharp
public string Id => $"{ProductId}:{PeriodId}";   // COMPUTED, not wire data
public int      ProductId;
public long     PeriodId;
public decimal? Price;
public DateTime Time;             // non-nullable
public decimal? PreviousPrice;
public DateTime PreviousTime;     // non-nullable
```

* **`Id` is computed**, so it is never null and is bounded at 32 characters
  (int + long + colon) against the target's `VARCHAR(50)`. Observed max: 9.
* ⚠ **`Time`/`PreviousTime` are non-nullable, so "no value" arrives as
  `0001-01-01T00:00:00Z`.** Observed on exactly the rows whose `PreviousPrice` is null.
  `DATETIME2`'s range starts at year 1, so this stores *silently* and reads back as a
  real timestamp. The loader converts year ≤ 1 to NULL.
* `Time` is **not** the exchange date — e.g. `2026-06-29T23:00:00Z` on the 2026-09-21
  snapshot. It is when that closing price was set.

### `MarketStatisticsDto`

* `State` is typed `MarketState` but serializes as `marketState`, and
  ⚠ **`MarketState` lives in `SignalRClient.Contracts`, not `Common.Core.Enums`** — the
  vendor's data-contract document implies otherwise. The other four enums
  (`Side`, `TradeEventSource`, `MarketStatisticsUpdateReason`, `ProductType`,
  `TradeChartDataSource`) *are* in `Common.Core.Enums`.
* All five enum-backed columns are `VARCHAR` in the target schema, so the loader writes
  the **name**. Longest member across all of them is 20 chars.
* `Bids`, `Asks` (`OtcPriceDto[]`), `PeriodIds` (`long[]`) and `ExtensionValues` have no
  column in the supplied schema and are dropped.
* Widest observed strings: `Product` 35, `Pipeline` 25, `Location` 24, `Period` 19,
  `Index` 16 — all against `VARCHAR(50)`. `CciIndex` was null throughout.
* `Location`/`Pipeline` arrive as `""` rather than null on most rows.

### `PeriodDto`

* The four date fields are non-nullable `DateTime`. Across all 724 live periods (2,896
  values) the only non-midnight values are intraday trading stamps and **three**
  `DeliveryEnd`s at `23:59`. **Nothing sits at `23:00Z`** — that is the pattern that
  would betray a London-midnight-expressed-as-UTC value and make date truncation off by
  one. Truncation is therefore safe here; it was checked, not assumed.
* `NoticeOfShipment` is nullable and its live values are all `23:59:59`, so it is stored
  as `DATETIME2(7)` rather than `DATE`.

### `ProductDto`

* JSON names differ from property names (`group`→`GroupName`, `grade`→`GradeName`,
  `location`→`LocationName`, `index`→`IndexName`, `pipeline`→`PipelineName`). Irrelevant
  when using the SDK's typed DTOs, but confusing when reading raw payloads.
* `ClearingHouseId` (scalar) is populated independently of the nested `ClearingHouse`
  object, which is frequently null. Read the scalar.
* Nested `MarketProperties` is a 30-field trading-rules object; `TradingUnit`,
  `StrategyLegs`, `ExtensionValues`, `PeriodGroupIds` likewise have no columns here.
* Widest observed: `IndexName` 66, `Name` 45 — against `VARCHAR(256)`.

### Vendor type inconsistencies, reproduced as supplied

| Concept | One DTO says | Another says |
|---|---|---|
| Product id | `ProductDto.Id` is `long` | `ClosingPriceDto.ProductId` is `int` |
| Period group id | `PeriodGroupDto.Id` is `long` | `PeriodDto.PeriodGroupId` is `int` |

Live ids are 4 digits, so neither is at risk. The supplied DDL mirrors the inconsistency
and the loader reproduces it verbatim.

---

## 7. Version gap — real, but not currently a blocker

The gateway heartbeat reports `minVersion: 3.37.0.1522`; the vendored SDK is `3.15.1.22`.
The SDK has an `IncompatibleVersion` connection state for exactly this, and the vendor's
note advises resolving the gap before building.

**Observed: the server accepts 3.15.1.22 today.** Full handshake, full snapshot, correct
data. The loader still treats `IncompatibleVersion` as a fail-fast, and
`MarexLiveSmokeTests.Live_EveryLiveEnumValue_IsKnownToTheVendoredSdk` checks whether the
gateway has started sending enum values this SDK cannot name — the most likely first
symptom of the gap actually beginning to matter.

Ask Marex for a newer package anyway. See [`lib/neon/README.md`](../../lib/neon/README.md)
for how to swap it.

---

## 8. Hosting: .NET Framework 4.6.1 assemblies on .NET 8

The vendor's note concludes these DLLs cannot be hosted in a .NET Core process and
prescribes a separate net48 connector writing to a staging schema.

**Tested: not necessary.** The loader runs in-process on net8.0. See
[`lib/neon/README.md`](../../lib/neon/README.md) for exactly why and what the constraints
are. The short version: every needed package has a `netstandard2.0` target, the
`System.Configuration`/`mscorlib`/`System.Core` references resolve to in-box .NET 8
facades, and `Common.Core.dll`'s AutoMapper/Unity/Topshelf references are never touched
because .NET resolves assembly references lazily.

---

## 9. Rate and volume

No documented rate limit, and none needed: the loader makes **one** Auth0 call and **one**
websocket connection per run. The whole snapshot is a single compressed payload,
~5 seconds end to end.

The vendor does count concurrent sessions, so the loader always disconnects in a `finally`
rather than leaving a socket open for the life of the host process.

---

## 10. Diagnosing a connection problem

The SDK logs through **log4net** and, unconfigured, prints
`WARNING: GetLogger called before Init` and then nothing — every internal failure is
invisible. Set `Loaders:Marex:EnableSdkLogging` (and optionally
`EnableSignalRTracing`) to route it into the platform logger.

⚠ The SDK logs its entire connection config — **bearer token included** — at INFO on every
`Connect`. `MarexSdkLogBridge` drops that line and any line containing a JWT, which is why
SDK logging goes through the bridge rather than a plain log4net appender.

---

## 11. Historical data — investigated 2026-09-21, NOT loaded

A backfill of the five loaded tables was investigated against the live gateway and is
**not possible**. Recorded here so the question does not get re-opened from first
principles.

### The five loaded tables cannot be backfilled

| Table | Why not |
|---|---|
| `arm.MarketStatistic` | `INeonApiClient` exposes **no** historical market-statistics method at all |
| `arm.Product`, `arm.Period`, `arm.PeriodGroup` | dimensions with no date — a backfill has no meaning |
| `arm.ClosingPrice` | history exists but is a **different entity** — see below |

### `RequestClosingPrices` returns real history, in a different shape

It works, and the account is entitled. Measured windows ending 2026-09-21:

| Window | Rows | Distinct dates |
|---|---|---|
| 7 days | 22,399 | 6 |
| 30 days | 75,435 | 21 |
| 60 days | 159,458 | 43 |
| `-90 .. -60` | 86,639 | 23 |

So history reaches **at least 90 days** back, at ~3,800 rows/day.

But `HistoricClosingPriceDto` shares almost nothing with the snapshot's `ClosingPriceDto`:

```json
{"id":"0.1% CIF Med|8/24/2026 12:00:00 AM2027", "priceDate":"2026-08-24T00:00:00Z",
 "instrumentName":"0.1% CIF Med", "marketName":"Distillates", "periodName":"2027",
 "midPoint":958.279, "bid":null, "offer":null}
```

* **No `ProductId`, no `PeriodId`** — only instrument/period *names*.
* **No `Price`, `Time`, `PreviousPrice`, `PreviousTime`.** It carries `midPoint`
  (always populated) plus `bid`/`offer` (only 10.6% populated).
* **A different id scheme**: `"{instrumentName}|{date}{periodName}"` against the
  snapshot's `"{productId}:{periodId}"`. Max observed length **160 chars**, and
  **69% exceed 50** — they cannot go in `ClosingPriceId VARCHAR(50)` at all.
* **A different universe**: 29 markets (Distillates, UK Power, Tanker, LNG, …) against
  the snapshot's 2 (Crude Oil Physical/Financial).
* `(priceDate, id)` is **not unique** — 774 duplicate pairs in a 30-day pull, so it
  cannot be used as a primary key unmodified.

Loading it would need its own table, which was offered and **declined 2026-09-21** —
the shape question goes back to Marex first.

### ⚠ Three silent behaviours if this is ever revisited

1. **The rows are in `Payload`, not `Items`.** `Items` is **always empty** on these
   historical responses. `Payload` is **zlib-compressed, base64-encoded JSON**; decode it
   with `Common.Core.Utility.Extensions.FromCompressedBase64Json()`. Reading `Items` and
   concluding "no history" is the easy mistake — it is the one this investigation made
   first.
2. **A window wider than ~60 days returns `Success = true` with an EMPTY payload.**
   90, 180 and 365-day requests all came back empty and successful, while `-90..-60`
   returned 86,639 rows. There is no error and no truncation flag. Any backfill **must
   chunk** to ≤60 days (30 is the safe figure) or it silently loads nothing.
3. **Every filter returns empty.** `MarketId`, `InstrumentId`, `PeriodId` and
   `PeriodGroupId` were each tried against known-good ids and all produced an empty
   payload (`i44FAA==`). Only the unfiltered request returns data.

`RequestIndexPrices` likewise returned 0 rows for every window tried.
`RequestHistoricTrades` **does** return data (41,076 rows over 30 days) but is a trade
feed with no target table here.
