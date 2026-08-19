# CWG (Commodity Weather Group) loader — design & processing flow

Design/flow spec for the CWG loader. Input of record is the field reference at
`docs/apis/CWG.md` (18 endpoints, parse shapes A–E, natural keys, DECIMAL sizing).
This document is the CODER hand-off; it contains no code. SQL objects named here are
proposed to DATABASE_DEVELOPER (open items are collected in §9).

> **2026-08 addendum — three regional/ISO degree-day endpoints added (§2.1 #16–#18,
> §6.16–§6.18).** `Regions5DegreeDays`, `Regions9DegreeDays`, `ISODegreeDays` are the
> per-region siblings of `NationalDegreeDays` (§8/§6.8) in the same
> `northamerica_{subregion}_wdd_{date}.csv` family, with `{subregion}` = `5region` /
> `9region` / `iso`. They reuse **Shape A** unchanged (plus a shared `END.`-footer skip),
> add an in-file `REGION_NAME` to the merge key, and get **three** new tables/TVPs/procs
> (no unified table — see §9). This raises the endpoint count **15 → 18**.

Locked decisions this design is built around: DB `CWG`, schema `arm`, server
`ARMH-OPSDB01`, Integrated Security; `ApiKey` via `SEE_DB`; descriptor-driven single
module; one `arm.FileLog` hub + 18 flat fact tables carrying `FileLogId` + natural
columns; go-forward only, `DaysBack` default 3; Station = the forecast-station file;
RunDate hot key for the undated "latest" files; enums live in C# descriptors
(DB-free enumeration); units are **per-region on CityForecast** (`F` for
`northamerica`, `C` for `europe`; **`asia` dropped**) and fixed elsewhere (§2.1 #1, §6.1).

> **2026-08 addendum — CityForecast per-region units (§2/§2.1 #1, §3, §5, §6.1, §9
> item 18).** CityForecast stays ONE endpoint but now enumerates only
> `region ∈ {northamerica, europe}` (drops `asia`), fetching each region in its native
> unit (`_F` / `_C`) via a new `{units}` filename token driven by an index-aligned
> `RegionUnits` descriptor field. Units is stamped as the FileLog `Variant` and stored in
> a new **`Units` attribute column** on `arm.CityForecast` (NOT a key column). The two
> Celsius normals carry up to 5 dp, so the temperature `DECIMAL` columns widen
> (DATABASE_DEVELOPER). No new endpoint, table, TVP or proc — one endpoint, additive
> column.

Reference implementations mirrored: `src/DataLoader.StormVista/` (HTTP+CSV, `?apikey=`,
404-tolerant GET, per-request FileLog upsert returning a `FileLogId` that stamps rows,
`RemoveAllLoggers()`, sanitized-path logging, explicit per-pipeline DI wiring) and
`src/DataLoader.Platts/` (filename-keyed work units, flat `arm` tables, `arm.FileLog`
+ `arm.usp_UpsertFileLog` direct writer under `SqlWriteGate`).

> **Discovery-first note.** CWG has no runtime discovery/mapping endpoint. The IDs later
> calls need (regions, geographies, hour labels, forecast-date widths, block names,
> region column sets) are either compile-time constants (regions/geographies/units) held
> in the C# **descriptor registry**, or data-driven and read from each file's own header
> at parse time (sub-region labels, capacity region rows, matrix widths). The descriptor
> registry *is* the (static) discovery result; enumeration is therefore DB-free
> (locked decision 7) and unit tests enumerate with no live DB.

---

## 0. Class / structure inventory (shared vs per-endpoint)

| Concern | Type(s) | Shared or per-endpoint |
|--------|---------|------------------------|
| Settings | `CwgSettings : LoaderSettingsBase` | shared |
| Descriptor | `CwgEndpointDescriptor` (record), `CwgDescriptors` (static registry of 18) | shared |
| Enums | `CwgParseShape {A,B,C,D,E}`, `CwgDateToken {None,Ymd,Mdyyyy}`, `CwgRegionKind {None,Geography,Iso}`, `HotKeyStrategy {RunDate,RunId}` | shared |
| Work unit | `CwgWorkUnit : WorkUnit` (one for **all** endpoints) | shared |
| Work-unit provider | `CwgWorkUnitProvider : IWorkUnitProvider<CwgWorkUnit>` (constructed per descriptor) | shared class, per-descriptor instance |
| CSV tokenizer + value/date parsers | `CwgCsv.Parse`, `CwgParse.*`, `CwgHours` (24-label array) | shared |
| Shape intermediates | `CwgTabularRecord` (A), `CwgUnpivotCell` (B), `CwgMatrixCell` (C), `CwgSubRegionCell` (D), `CwgCapacityRow` (E) | shared |
| Shape parsers | `ICwgShapeParser<TRecord>` + `ShapeAParser … ShapeEParser` (written once each) | shared |
| Source reader | `CwgSourceReader<TRecord,TRow> : ISourceReader<CwgWorkUnit,TRow>` (HTTP + FileLog + parse + map + stamp) | shared |
| FileLog | `CwgFileContext` (readonly struct), `ICwgFileLog`, `SqlCwgFileLog` (calls `arm.usp_UpsertFileLog`, `SqlWriteGate`, returns `FileLogId`) | shared |
| Fact row | `ICwgFactRow { int FileLogId { get; set; } }` + **18 row types** | interface shared, 18 rows per-endpoint |
| Row factory | `Func<TRecord, CwgWorkUnit, TRow?>` (a `static TRow? From(...)` per row type) | **per-endpoint** (the only mapping code) |
| Sink | `SqlSinkBase<TRow>` subclass **×18** (per-endpoint proc + TVP) | **per-endpoint** |
| Pipeline | `ICwgEndpointPipeline : ILoaderPipeline { string EndpointId }`, `CwgEndpointPipeline<TRow> : LoaderPipelineBase<CwgWorkUnit,TRow,TRow>` | shared |
| Module | `CwgModule : ILoaderModule` | shared |
| HTTP plumbing | rate limiter + delegating handler + Polly policy (mirror StormVista) | shared |

The **only** per-endpoint C# is: the `TRow` class, its `From(...)` row factory, the
`SqlSinkBase<TRow>` subclass, and the descriptor entry. Everything above the row factory
(HTTP, retry, throttle, FileLog, 404-tolerance, parse shapes, work-unit enumeration,
idempotency, the pipeline loop) is written once.

---

## 1. Module topology & pipeline strategy

**One module, eighteen closed per-endpoint pipelines**, built explicitly (Platts /
StormVista pattern), toggled by `EnabledEndpoints[]`.

### 1.1 Why closed per-endpoint pipelines (not one generic pipeline)

All 18 endpoints share the `CwgWorkUnit` type. If we registered a single
`IWorkUnitProvider<CwgWorkUnit>` (or `ISourceReader<CwgWorkUnit,TRow>`) in DI, every
pipeline would resolve the *same* provider and load the wrong endpoint's descriptor —
exactly the collision the Platts/StormVista headers warn about. We therefore **never
register the generic provider/reader in DI**. Each pipeline is assembled inside its own
factory closure that `new`s a provider and reader bound to that endpoint's descriptor,
and passes them to `CwgEndpointPipeline<TRow>`'s constructor. `LoaderPipelineBase` takes
its provider/source/sink as constructor arguments, so those generics are never resolved
by the container at all.

### 1.2 The generic pipeline class

```
ICwgEndpointPipeline : ILoaderPipeline { string EndpointId { get; } }

CwgEndpointPipeline<TRow> : LoaderPipelineBase<CwgWorkUnit, TRow, TRow>, ICwgEndpointPipeline
    // ctor(endpointId, IWorkUnitProvider<CwgWorkUnit>, ISourceReader<CwgWorkUnit,TRow>,
    //      ISink<TRow>, ILoadLogRepository, CwgSettings, ILogger)
    //   → base(CwgModule.Id, provider, source, IdentityTransformer<TRow>(), sink, loadLog, settings, logger)
```

This reuses the platform's vetted per-unit loop unchanged: `BeginAsync` idempotency skip
→ `ReadAsync` → identity transform → `WriteAsync` → `CompleteSuccess/Failure`, bounded by
`ParallelRunner` at `MaxConcurrentWorkUnits`, per-unit timeout, fail-one-not-the-run.
CWG needs **no** custom windowed orchestrator (unlike StormVista) because `DaysBack` is
small (default 3): the whole unit list for a run materializes cheaply.

### 1.3 DI registration (RegisterServices)

1. `services.AddLoaderSettings<CwgSettings>(configuration, Id);` (binds `Loaders:CWG`,
   resolves `ApiKey="SEE_DB"` from `core.Param`).
2. Shared throttle: `CwgRateLimiter` (singleton) + `CwgRateLimitingHandler` (transient).
3. Named `HttpClient` `"CWG"`:
   - `Timeout = HttpTimeoutSeconds`, `Accept: text/csv`;
   - `.RemoveAllLoggers()` — suppress `IHttpClientFactory` default logging so the
     `?apikey=` query string is never logged (StormVista rationale);
   - `.AddPolicyHandler(...)` retry policy **outer** (Polly, `RetryCount`/`RetryDelayMs`,
     429/5xx/transient) ;
   - `.AddHttpMessageHandler<CwgRateLimitingHandler>()` throttle **inner** so every
     attempt (first + each retry) is paced by `RequestsPerSecond`.
4. `services.AddSingleton<ICwgFileLog, SqlCwgFileLog>();`
5. Register the 18 pipelines with a generic helper, one line each:

   ```
   services.AddSingleton<ICwgEndpointPipeline>(sp => BuildPipeline<CwgTabularRecord, CityForecastRow>(
       sp, CwgDescriptors.CityForecast, ShapeA, CityForecastRow.From,
       s => new CityForecastSqlSink(options, log)));
   … ×18 …
   ```

   `BuildPipeline<TRecord,TRow>(sp, descriptor, shapeParser, rowFactory, sinkFactory)`
   (a private static in `CwgModule`) constructs:
   - `provider = new CwgWorkUnitProvider(descriptor, settings, logger)`
   - `source   = new CwgSourceReader<TRecord,TRow>(http("CWG"), settings, fileLog, descriptor, shapeParser, rowFactory, logger)`
   - `sink     = sinkFactory(sp)` (per-endpoint `SqlSinkBase<TRow>`)
   - returns `new CwgEndpointPipeline<TRow>(descriptor.EndpointId, provider, source, sink, loadLog, settings, logger)`.

   The 5 `ShapeX` parsers are shared singletons/statics; each of the 18 lines picks the
   one its descriptor's `ParseShape` names (`ShapeA` is reused by **8** endpoints —
   CityForecast, CityGasForecast, CityObservation, Station, NationalDegreeDays,
   **Regions5DegreeDays, Regions9DegreeDays, ISODegreeDays** — `ShapeB` by 3, `ShapeC` by
   3, `ShapeD` by 1, `ShapeE` by 3).

### 1.4 RunAsync fan-out

Mirror `StormVistaModule.RunAsync`:

1. Read `CwgSettings`. Fail fast (log error, `LoaderRunResult.Failed`) if `ApiKey` is
   blank/placeholder — never log the key value.
2. `enabled = HashSet(EnabledEndpoints, OrdinalIgnoreCase)`;
   `all = services.GetServices<ICwgEndpointPipeline>()`;
   `pipelines = all.Where(p => enabled.Contains(p.EndpointId))`.
   Warn for any enabled id with no matching pipeline; if none enabled → warn, return
   `Success=true`.
3. **Run the enabled endpoints sequentially**; within each, `ExecuteAsync` fans work
   units out concurrently via `ParallelRunner` (`MaxConcurrentWorkUnits`). The single
   shared rate-limited `HttpClient` bounds the *global* request rate (`RequestsPerSecond`)
   regardless of how many units run at once, so sequential-endpoints keeps memory/log
   reasoning simple while still saturating the allowed RPS. (Endpoint-level parallelism is
   an available knob but unnecessary given the global throttle — see §9 open item.)
4. Aggregate the 18 `LoaderRunResult`s (sum totals; `Success = all succeeded`) exactly
   as StormVista/Platts do.

---

## 2. The endpoint descriptor schema

```
record CwgEndpointDescriptor(
    string        EndpointId,        // stable id: "CityForecast" … "Station"
    string        DisplayName,
    CwgParseShape ParseShape,        // A|B|C|D|E
    string        FilenameTemplate,  // {date}/{datemmddyyyy} + named enum placeholders
    bool          Dated,             // true → date token; false → undated "latest"/hot
    CwgDateToken  DateToken,         // None | Ymd (YYYYMMDD) | Mdyyyy (MMDDYYYY)
    int           DateOffsetDays,    // newest represented date = runDate + offset (0, or -1 for CityObservation)
    CwgRegionKind RegionKind,        // None | Geography (na/asia/europe) | Iso (ERCOT…)
    string?       RegionPlaceholder, // token name in the template, e.g. "region" (null if none)
    string[]      Regions,           // enum values for the region placeholder (empty if none)
    string[]?     RegionUnits,       // OPTIONAL, index-aligned 1:1 with Regions → the per-region {units} token (CityForecast: {"F","C"}); null on the other 17 endpoints. In CwgDescriptors.cs this is a TRAILING optional ctor param (`string[]? RegionUnits = null`), like ExpectedColumns/AllowShortRows, so the other 17 descriptor literals are untouched.
    (string Name,string[] Values)[] ExtraPlaceholders, // e.g. subregion=["national"]; CityForecast no longer bakes a units literal here — see RegionUnits + the {units} token
    string[]?     WideRegionColumns, // Shape B in-file region column set (ordered); null otherwise
    int           KeyColumns,        // Shape B leading key-column count (1: DATE / UTC_HOUR_ENDING)
    int?          ExpectedBlocks,    // Shape E: 1 (Climo) or 3 (MW/Pct); null otherwise
    string        TargetTable,       // arm.<Table> (informational; sink is per-endpoint)
    string        TargetTvp,         // arm.<Table>Tvp
    string        TargetProc);       // arm.usp_BulkMerge<Table>
```

Derived at runtime: `IsHot = !Dated` (undated → RunDate hot key); `RepresentativeDate`
per unit (below).

**The `{units}` token (CityForecast only).** CityForecast's template carries a `{units}`
placeholder (`city15dfcst_{region}_{date}_{units}.csv`); its `RegionUnits = {"F","C"}` is
index-aligned 1:1 with `Regions = {"northamerica","europe"}` (northamerica→`F`,
europe→`C`). The work-unit provider resolves the region's units by its **position in the
descriptor's `Regions` array** (carried through the Geographies filter as a `(Region,Units)`
pair so a filtered-out region drops its units with it), substitutes `{units}` in the
filename, and stamps the token onto the work unit (as both `Units` and the FileLog
`Variant` — §3). Every other descriptor has `RegionUnits = null` and no `{units}` token in
its template, so `Substitute` performs **no** `{units}` replacement and their `Units`/`Variant`
are unchanged — the change is fully additive. (The old literal `_F` in the template and the
"units=F, not stored" note are **superseded** by this mechanism.)

### 2.1 Concrete descriptor values — all 18

| # | EndpointId | Shape | FilenameTemplate | Dated | DateToken | Off | RegionKind / Regions | Extra | Wide cols (B) / Blocks (E) |
|---|-----------|:----:|------------------|:----:|:--------:|:--:|----------------------|-------|-----------------------------|
| 1 | CityForecast | A | `city15dfcst_{region}_{date}_{units}.csv` | Y | Ymd | 0 | Geography: `northamerica,europe` (**asia dropped**); `RegionUnits={F,C}` | — | — |
| 2 | CityGasForecast | A | `city_gasday_fcst.csv` | N | None | — | None | — | — |
| 3 | CityObservation | A | `{region}_observations_final_{date}.csv` | Y | Ymd | **−1** | Geography: `northamerica,asia,europe` | — | — |
| 4 | DailyNormal | B | `daily_normals.csv` | N | None | — | None | — | Wide=`CAISO,SPP,ERCOT,MISO,PJM,NEPOOL,NYISO,BPA,IESO,AESO,NW,SW` (12); KeyCols=1 |
| 5 | SolarForecast | C | `{region}solar_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | Iso: `ERCOT,CAISO,MISO,PJM,SPP,NEPOOL,IESO,AESO,NW,SW` (10) | — | — |
| 6 | SolarForecastChange | C | `{region}solarchanges_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | Iso: same 10 as #5 | — | — |
| 7 | SolarHourly | B | `Gen_hrly_solar.csv` | N | None | — | None | — | Wide=`ERCOT,CAISO,PJM,AESO,MISO,IESO,NEPOOL,NW,SW,SPP,FRCC,CAR,SE,TVA` (14); KeyCols=1 |
| 8 | NationalDegreeDays | A | `northamerica_{subregion}_wdd_{date}.csv` | Y | Ymd | 0 | Geography-fixed: `northamerica` (see note) | subregion=`national` | — |
| 9 | WindForecast | C | `{region}wind_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | Iso: `ERCOT,CAISO,MISO,PJM,SPP,NYISO,NEPOOL,BPA,IESO,AESO,UK,GERMANY,FRANCE,SPAIN,NW,SW` (16) | — | — |
| 10 | WindForecastSubRegion | D | `{region}wind_regions_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | Iso: `ERCOT,CAISO,MISO,PJM,SPP,UK,GERMANY,FRANCE` (8) | — | sub-regions data-driven |
| 11 | WindHourly | B | `Gen_hrly_5day.csv` | N | None | — | None | — | Wide=`CAISO,SPP,ERCOT,MISO,PJM,NEPOOL,NYISO,BPA,IESO,AESO,NW,SW` (12); KeyCols=1 |
| 12 | WindTotalCapacityClimatology | E | `Total_Capacity_climo_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | None (regions in-file) | — | Blocks=1 |
| 13 | WindTotalCapacityMW | E | `Total_Capacity_vals_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | None (regions in-file) | — | Blocks=3 |
| 14 | WindTotalCapacityPct | E | `Total_Capacity_{datemmddyyyy}.csv` | Y | Mdyyyy | 0 | None (regions in-file) | — | Blocks=3 |
| 15 | Station | A | `{region}_station_information.csv` | N | None | — | Geography: `northamerica,asia,europe` | — | — |
| 16 | Regions5DegreeDays | A | `northamerica_{subregion}_wdd_{date}.csv` | Y | Ymd | 0 | None (see note) — `northamerica` literal | subregion=`5region` | `ExpectedColumns=18` |
| 17 | Regions9DegreeDays | A | `northamerica_{subregion}_wdd_{date}.csv` | Y | Ymd | 0 | None (see note) — `northamerica` literal | subregion=`9region` | `ExpectedColumns=18` |
| 18 | ISODegreeDays | A | `northamerica_{subregion}_wdd_{date}.csv` | Y | Ymd | 0 | None (see note) — `northamerica` literal | subregion=`iso` | `ExpectedColumns=11` |

Notes:
- **`Off` (DateOffsetDays):** the *newest* represented date = `runDate + Off`. Only
  CityObservation is −1 (final obs = yesterday; today 404s). All other dated endpoints
  are 0 (today is valid). `DaysBack` then walks back from the newest (§3).
- **#8 NationalDegreeDays:** filename geography (`northamerica`) and subregion
  (`national`) are both fixed, modelled as a literal geography + one `ExtraPlaceholders`
  value. It produces exactly one work unit per date. (Reviewer: confirm we keep
  `northamerica` as `FileLog.Region` and `national` as `FileLog.Variant` — see §5.)
- **#16–#18 Regions5/Regions9/ISO DegreeDays** are the per-region siblings of #8 in the
  **same `wdd` family** and follow #8's descriptor pattern **exactly**: `RegionKind.None`
  (NOT `Geography` — `northamerica` is baked into the filename and stamped on the
  unit/FileLog, but must NOT be subject to the `Geographies` filter, so narrowing
  `Geographies` never zeroes these out), `Regions = ["northamerica"]` literal, one
  `ExtraPlaceholders` value `subregion=5region|9region|iso`, `Dated=true`, `DateToken=Ymd`,
  `Off=0`. Each produces exactly one work unit per RunDate. They differ from #8 in only
  three ways: the descriptor carries `ExpectedColumns=18` (5/9region) or `11` (iso) as the
  Shape-A width guard; the file adds an in-file `REGION_NAME` column (multiple regions per
  date) that becomes the third natural-key column; and the file terminates with an `END.`
  footer row the shared Shape-A parser must skip (§4). `AllowShortRows=false` for all three.
  Targets: `arm.Regions5DegreeDays` / `arm.Regions9DegreeDays` / `arm.ISODegreeDays` (+
  matching `…Tvp` / `arm.usp_BulkMergeRegions5DegreeDays` etc.).
- **Geographies filter:** `RegionKind == Geography` descriptors (#1, #3, #15) intersect
  their `Regions` list with the `Geographies[]` setting (default all three). `Iso`
  descriptors are **not** filtered by `Geographies` (different axis). CityForecast (#1)
  intersects its **two-element** `Regions = {northamerica, europe}` — `asia` is dropped
  **structurally** (removed from this endpoint's `Regions`), not by the setting, so a run
  with the default `Geographies` still yields only `northamerica`+`europe`; narrowing
  `Geographies` further (e.g. to `{northamerica}`) drops `europe` **and its `C` units**
  because the units travel with the region as a pair through the filter. CityObservation
  (#3) and Station (#15) keep all three geographies — the drop is **CityForecast-only**.
- **CityForecast units (#1):** `RegionUnits` is index-aligned with `Regions`
  (northamerica↔`F`, europe↔`C`). The token feeds the `{units}` filename slot, the FileLog
  `Variant`, and the new `Units` attribute column (§5, §6.1). It is a per-endpoint
  descriptor field; the other 17 leave `RegionUnits = null`.

---

## 3. Work-unit construction & resume keying

`CwgWorkUnit : WorkUnit` carries: `EndpointId`, `Region?` (the region/geography literal
or null), `Variant?` (the FileLog sub-variant slot — the `wdd` subregion token
`national` / `5region` / `9region` / `iso` for the DD family, **or the units token
`F` / `C` for CityForecast** — null otherwise), `Units?` (**NEW** — the per-region units
token `F` / `C` for CityForecast, null on every other endpoint; the typed attribute the
CityForecast row factory copies into the row's `Units` column), `RepresentativeDate?`
(DateOnly, null for undated), `Filename` (fully substituted, no host/query), `KeyValue`
(precomputed), `Key => KeyValue`, `DisplayName`.

> **`Units` vs `Variant` (why both).** `Variant` is the generic **FileLog** sub-key slot
> (`arm.FileLog.Variant`) that the source reader already passes through to
> `usp_UpsertFileLog` — no source-reader/FileLog change is needed to log units. `Units` is
> the typed **fact** attribute the CityForecast row stores. They hold the **same value**
> (`F`/`C`) for CityForecast but represent different concerns, so both are stamped. For the
> DD family `Variant` = subregion and `Units` = null; for all other endpoints both are null.

> **The four `wdd` degree-day endpoints (#8, #16–#18) all enumerate identically:** one
> region literal (`northamerica`) × one `subregion` extra (`national`/`5region`/`9region`/
> `iso`) × each date in the `DaysBack` window → **one work unit per RunDate** each. The
> `Variant` (subregion token) makes their keys and FileLog rows distinct even though they
> share `Region='northamerica'`. Resume keying (below), the two-zone settled/hot split, the
> go-forward trailing window, and the per-RunDate work-unit granularity are the **same** as
> NationalDegreeDays — the region breakout lives entirely *inside* each file (in-file
> `REGION_NAME` rows), not in the work-unit axis. Base key, e.g.
> `cwg:Regions5DegreeDays:northamerica:5region:20260811`.

`CwgWorkUnitProvider.GetWorkUnitsAsync(context)` (DB-free) for its bound descriptor:

```
runDate = DateOnly.FromDateTime(EasternNow(context.StartedAtUtc))   // US Eastern, NOT UTC
// ResolveRegions now yields (Region, Units) PAIRS. Build pairs by zipping Regions with
// RegionUnits (Units = RegionUnits?[i] ?? null, index-aligned), THEN apply the Geographies
// filter on Region — so a filtered-out region drops its Units with it. None/Iso: Regions
// verbatim, Units=null. No regions: single (null, null).
regionPairs = zip(Regions, RegionUnits)          // (region, units); units=null when RegionUnits is null
              |> (RegionKind==Geography ? filter by settings.Geographies on region : identity)
extras  = cartesian of ExtraPlaceholders  (e.g. {subregion:national}); [{}] if none

hot = settings.HotKeyStrategy==RunDate ? runDate:yyyyMMdd : context.RunId:N   // hot-zone run token

if (descriptor.Dated):
    newest = runDate.AddDays(descriptor.DateOffsetDays)
    for k in 0 .. settings.DaysBack-1:                                        // enumerate the DaysBack window
        d = newest.AddDays(-k)
        ageDays = runDate.DayNumber - d.DayNumber
        for (region, units) in regionPairs, for extra in extras:
            variant  = extra.subregion ?? units                              // FileLog slot: subregion (DD) OR units (CityForecast)
            filename = substitute(template, {date/datemmddyyyy}=fmt(d), {region}=region, {units}=units, extra…)
            baseKey  = $"cwg:{EndpointId}:{region ?? '-'}:{variant ?? '-'}:{d:yyyyMMdd}"
            unit = { EndpointId, Region=region, Variant=variant, Units=units, RepresentativeDate=d,
                     Filename=filename,
                     // TWO-ZONE: settled (older than SettledAfterDays) → STABLE; recent → HOT
                     KeyValue = ageDays > settings.SettledAfterDays ? baseKey
                                                                    : $"{baseKey}:run={hot}" }
else:  // undated / latest — always HOT
    for (region, units) in regionPairs, for extra in extras:
        variant  = extra.subregion ?? units
        filename = substitute(template, {region}=region, {units}=units, extra…)
        unit = { EndpointId, Region=region, Variant=variant, Units=units, RepresentativeDate=null,
                 Filename=filename,
                 KeyValue = $"cwg:{EndpointId}:{region ?? '-'}:{variant ?? '-'}:run={hot}" }   // HOT
```

`Substitute` replaces `{units}` with the pair's units token only when it is non-null (mirrors
the existing `{region}` guard); templates without a `{units}` token are unaffected. CODER
should assert `RegionUnits.Length == Regions.Length` when `RegionUnits` is non-null (a
descriptor-registry invariant, checked once at construction).

`runDate` is the **US Eastern** calendar date (CWG publishes dated files on the Eastern
day), not the UTC date — this keeps a run that straddles the UTC midnight boundary from
targeting a not-yet-existent future date.

Date-token formatting: `Ymd` → `yyyyMMdd`; `Mdyyyy` → `MMddyyyy` (both zero-padded in the
filename — CWG.md's `{datemmddyyyy}` examples are padded, e.g. `08112026`).

### 3.1 Resume keying — the two-zone model (StormVista rule)

`DaysBack` sets the **enumeration** window; `SettledAfterDays` splits that window into two
zones by the represented date's age (`ageDays = runDate.DayNumber − d.DayNumber`, both on the
Eastern run date):

- **Settled zone (`ageDays > SettledAfterDays`) — STABLE key.** The key is the bare
  `cwg:{Endpoint}:{region}:{variant}:{d:yyyyMMdd}` with **no** `:run=` suffix. It never varies
  between runs, so once that `(region,date)` file is loaded `core.LoadLog` records the key and
  every later run's `BeginAsync` returns `null` → **cheap skip, no HTTP** (Platts pattern).
- **Hot zone (`ageDays ≤ SettledAfterDays`) — run-varying key.** The key is
  `…:{d:yyyyMMdd}:run=<hot>`. With `HotKeyStrategy=RunDate` (default) `<hot>` = the Eastern
  `<yyyyMMdd>`, so the recent days are **re-pulled once per calendar day** (skipped on a
  second run the same day); `RunId` re-pulls every invocation. The re-pull upserts idempotently
  via the natural-key MERGE (§8), so it catches **not-yet-published / late / revised** recent
  files without duplication.
- **Undated "latest" endpoints — always HOT.** `city_gasday_fcst`, `daily_normals`,
  `Gen_hrly_solar`, `Gen_hrly_5day` (decision 6) **and** the Station files (undated; see §9)
  enumerate one `:run=<hot>` unit each (`RepresentativeDate=null`).
- **CityObservation** newest = `runDate − 1` (`DateOffsetDays=-1`; requesting today 404s), then
  `DaysBack` further days back; its newest day has `ageDays = 1`, so it starts in the hot zone.
- **CityForecast** is unchanged by the units work: still `Dated`, `Ymd`, `Off=0`, two-zone
  settled/hot. The **only** enumeration deltas are (a) the region set drops `asia`
  (2 regions instead of 3 → fewer units) and (b) the per-region units token now fills the
  `Variant` slot, so the key becomes `cwg:CityForecast:{region}:{F|C}:{yyyyMMdd}` (was
  `…:{region}:-:{yyyyMMdd}`). `region` alone already keys each unit uniquely, so the units
  token in the `Variant` slot adds no ambiguity — it only makes the key self-describing and
  consistent with the DD family. Because CWG is **build-only/never loaded live**, there are
  no existing `core.LoadLog` rows for this key to invalidate.

The `arm.FileLog` hub row is keyed on `(Endpoint,Region,Variant,RepresentativeDate)` —
independent of the run token — so a daily hot re-pull **upserts one stable hub row** (refreshing
`LastCheckedUtc`/`RowCount`); the audit is never fragmented.

**Trade-off (intended).** This two-zone model reintroduces the property that *a settled date
that 404s stays settled* (loaded-as-0-rows once, then skipped): `CwgSourceReader` returns empty
for a 404 (no throw — §5/decision 5), `LoaderPipelineBase` records `CompleteSuccessAsync` with 0
rows, and the stable key is then skipped forever. That is **acceptable and desired**: after
`SettledAfterDays` a dated file reliably either exists or is a permanent gap (holiday /
region-not-modelled), so re-hammering it forever is wasteful. The earlier reviewer concern
(a not-yet-published dated file being lost) is still handled — just **scoped to the hot zone**,
which is exactly where late/revised files live. Defaults are `DaysBack=21` **>**
`SettledAfterDays=7`, so the settled zone (days 8–21) is non-empty (unlike StormVista's 21/21,
which is effectively all-hot). CWG stays **incremental / go-forward** — no Backfill mode,
`ChunkDays`, or windowed pipeline (the `DaysBack` list materializes cheaply).

---

## 4. The five parse-shape contracts (A–E)

All shapes sit **above** the shared quote-aware CSV tokenizer `CwgCsv.Parse` (lift
StormVista's `StormVistaCsv.Parse` verbatim: RFC-4180 quotes, doubled `""`, embedded
CR/LF, BOM strip, and it **drops only truly blank lines** — a bare newline — while
`,,,,,` structural rows survive as arrays of empty strings, which shapes C/D/E rely on).

Each shape parser is a pure function
`Parse(descriptor, unit, csvText, log) → IReadOnlyList<TRecord>`. It emits **no** footer
or title rows. The per-endpoint row factory then maps each `TRecord` (+ unit) to `TRow?`;
returning **null drops the record** (used for sentinels). Shared helpers:

- `CwgHours` — the 24 exact labels in order (`12:00 AM,1:00 AM,…,11:00 PM`); index =
  `HourOfDay` 0–23. Matching is exact after `Trim()`.
- `CwgParse` — invariant-culture: `Decimal`, `Percent` (strip one trailing `%`, then
  Decimal; keeps sign), `DateMdyy` (`M/d/yy`, .NET two-digit-year pivot → `26`→2026),
  `DateIso` (`yyyy-MM-dd`), `DateMdyyyy` (tries both `M/d/yyyy` and `MM/dd/yyyy`),
  `DateTimeIso` (`yyyy-MM-dd HH:mm:ss` → DATETIME2(0)), `Bit` (`True/False`).
  A blank/`"NULL"`/unparseable numeric → factory returns null for that cell/row (skip)
  with a debug/warn log; a bad **key/date** cell skips the whole record.

### Shape A — simple tabular (CityForecast, CityGasForecast, CityObservation, Station, NationalDegreeDays, Regions5DegreeDays, Regions9DegreeDays, ISODegreeDays)
Emits `CwgTabularRecord { string[] Fields; Header }`, one per data row.
1. Tokenize. `header = records[0]`. Width guard: require `header.Length ≥ N` (N =
   `descriptor.ExpectedColumns` when set, else `header.Length`). Optionally verify each
   header cell equals the expected name (warn, don't fail) — parsing is by **ordered
   position** (CWG.md gives a stable order; position avoids the C# identifier problem of
   `30Y_…`/`10Y_…` headers).
2. **`END.` footer skip (shared, applies to all four `wdd` endpoints).** For `i=1..end`,
   **first** skip any row whose `cells[0].Trim() == "END."` at **Debug** level — the `wdd`
   family (national + 5region + 9region + iso) terminates with a literal `END.` sentinel
   row (`DATES=END.`, remaining fields empty). Then skip rows with `Length < N`
   (warn, unless `AllowShortRows`); else emit the raw field array.
3. Row factory reads by index and applies the endpoint's typed parsers (see §6). For most
   Shape-A endpoints Region (geography/ISO) and the date come from the **unit**, not the
   CSV — **except the three region-carrying `wdd` siblings (#16–#18), whose `RegionName`
   is read from the in-file `REGION_NAME` cell (`Fields[1]`)** while `RunDate` still comes
   from the unit (filename) and `Dates` from `Fields[0]` (see the Region-trap note in §5).

> **Parse-shape decision (pivotal): REUSE Shape A — no new shape.** The `ShapeAParser`
> already emits raw positional field arrays and is region-agnostic (region semantics live
> entirely in the per-endpoint row factory), so the three new region/ISO endpoints reuse it
> verbatim; the only per-endpoint C# is their three new row types + `From(...)` factories +
> sinks + descriptors. **One small shared enhancement is required:** the explicit `END.`
> skip in step 2. *Verified against the fixtures:* NationalDegreeDays **already** drops the
> `END.` row today — but only incidentally, via the `Length < N` short-row guard, which logs
> a spurious **Warning** every run (the row is a bare single field `END.`, so `1 < 14`). The
> explicit `END.` check makes the intent self-documenting, demotes it to Debug (no per-file
> warning noise across the now-four `wdd` endpoints), and is robust whether the real footer
> is bare (`END.`) or comma-padded to full width. It is purely additive: the four non-`wdd`
> Shape-A endpoints never emit an `END.` row, so their behavior is unchanged. Row types:
> `Regions5DegreeDaysRow` / `Regions9DegreeDaysRow` / `ISODegreeDaysRow`; ISO gets its own
> divergent column set (`POP_HDD`, no `ELEC_*`, no weights — §6.18).

### Shape B — wide-by-region → unpivot (DailyNormal, SolarHourly, WindHourly)
Emits `CwgUnpivotCell { string KeyCell; string Region; string RawValue }`.
1. Tokenize. `header = records[0]`. `keyName = header[0]`; region columns =
   `header[KeyColumns..]`, trimmed. Validate against `WideRegionColumns` **StormVista-style**:
   fatal on an *unknown/extra* column, tolerate *missing* columns (early history has only
   some regions), warn on reordering; resolve region→column-index by header name (robust
   to reorder), not fixed position.
2. For each data row, for each present region column `j`: emit
   `{ KeyCell = fields[0], Region = name[j], RawValue = fields[colIndex(j)] }`.
3. Row factory: parse the key (`MonthDay` CHAR(5) kept literally for DailyNormal;
   `HourEndingUtc` DATETIME2(0) for the hourly feeds) and the value; **`"NULL"`/blank →
   return null (skip the cell)** so sparse actuals tables don't store null grids
   (SolarHourly early years are ERCOT-only). DailyNormal has no sentinel (all 12 present).

### Shape C — pivoted hour×forecast-day matrix → unpivot (SolarForecast, SolarForecastChange, WindForecast)
Emits `CwgMatrixCell { DateOnly ForecastDate; string HourLabel; int HourOfDay; string RawValue }`.
Region + InitDate come from the unit.
1. Tokenize. **Skip leading fully-empty rows** (handles SolarForecast/WindForecast's
   leading spacer `,,,,,`; SolarForecastChange has none — no special-casing needed).
2. Find the header: first row whose `cell[0].Trim() == "Date (EST)"`. Its remaining cells
   are the forecast-date strings. **Read the width dynamically** = count of *non-empty*
   forecast-date cells → naturally yields **16** (SolarForecast/WindForecast) vs **14**
   (SolarForecastChange, whose trailing comma leaves an empty 15th cell that is excluded).
   Parse each with `CwgParse.DateMdyyyy` (tolerates both `M/D/YYYY` and `MM/DD/YYYY`, so
   the parser needn't be told which variant). First column = `init−1` (SolarForecast/Wind)
   or `init` (Change) — the parser does not assume; it takes whatever the header says.
3. For each subsequent row: if `cell[0].Trim()` is one of the 24 `CwgHours` labels →
   data row: for `j=0..width-1` emit `{ ForecastDate=dates[j], HourLabel, HourOfDay,
   RawValue=cell[j+1] }`. Otherwise (`sum change for the day`, `average change for the
   day`, blank, any non-hour leading cell) → **skip** — this drops SolarForecastChange's
   two footer rows and any trailing blank generically. An unrecognized non-footer leading
   cell is logged (warn) and skipped defensively.
4. Row factory → `ValueMw` (SolarForecast/WindForecast) or **signed** `ChangeMw`
   (SolarForecastChange), `CwgParse.Decimal`.

### Shape D — stacked sub-region matrix blocks → unpivot per block (WindForecastSubRegion)
Emits `CwgSubRegionCell { string SubRegion; DateOnly ForecastDate; string HourLabel; int HourOfDay; string RawValue }`.
1. Tokenize. Walk rows; **detect a block by its label row**: `cell[0].Trim()` ends with
   `" region"` (case-insensitive) and the remaining cells are empty (the
   `<x> region,,,,,` pattern). `SubRegion = cell[0]` with the trailing `" region"`
   stripped — **keep any numeric prefix / hyphens** (`Geo-Panhandle`, `1:SP-15`); treat as
   free text. Sub-region set is **data-driven** (not an enum) — discovered from these rows.
2. After a label row, the next non-blank row is the block's `Date (EST),<dates>` header;
   parse forecast dates with dynamic width (**15** here, `init … init+14`, `M/D/YYYY`) via
   the shared "read Date(EST) header + 24 hour rows" helper reused from Shape C. Do **not**
   hardcode 16 — read the width from the block header.
3. Read the block's 24 hour rows exactly as Shape C, emitting one cell per (hour ×
   forecast-date) tagged with the block's `SubRegion`.
4. The **3 blank separator rows** between blocks need no counting — a new label row starts
   the next block; blank/short rows in between are skipped.

### Shape E — region-row summary, multi-block (WindTotalCapacityClimatology, …MW, …Pct)
Emits `CwgCapacityRow { string Block; string Region; string TotalCapacityRaw; string Avg1_5Raw; string Avg6_10Raw; string Avg11_15Raw }`.
1. Tokenize. Walk rows tracking the current `Block`, driven by **title rows**:
   - `Total Wind Generation Across All Regions` **or** `…Climo Across All Regions` → `Block="Current"`.
   - `Yesterday's Forecast` → `Block="Yesterday"`.
   - `Change from Yesterday's Forecast` → `Block="Change"`.
   Climatology has only the first title → a single `Current` block (its row factory drops
   the Block, per §6). MW/Pct yield 3 blocks. (`ExpectedBlocks` validates the count; warn
   on mismatch.)
2. **Skip**: header rows (`cell[0]` empty **and** `cell[1]=="Total Capacity (MW)"`);
   footer rows (`cell[0] ∈ {Total All (MW), Total Percent, Total Change}`); blank rows.
   (Footer storage is an open DB item — §9. Note the `Total All (MW)` avg cells are in MW
   even in the %-file; irrelevant while footers are skipped.)
3. Otherwise → a region row: `Region=cell[0].Trim()`, emit the 4 raw numeric cells with
   the current `Block`.
4. Row factory: avg columns via `CwgParse.Percent` (Pct/Climo — strips `%`, keeps sign in
   the Change block, e.g. `-4%`→`-4.00`) or `CwgParse.Decimal` (MW — signed in Change);
   `TotalCapacityMw` = `Decimal` or **NULL when blank** (the Change block leaves it empty).
   `Percent` degrades to plain `Decimal` when no `%` is present, so one call works for all.

---

## 5. FileLog flow (`arm.FileLog` hub, generalized across all 18 endpoints)

`arm.FileLog` is the single hub logging **every outcome** (Success / NotAvailable /
Failed) for every request. Generalized natural key:

```
UNIQUE (Endpoint, Region, Variant, RepresentativeDate)   -- SQL NULL-equality collapses undated rows
```

| Endpoint | Region | Variant | RepresentativeDate |
|----------|--------|---------|--------------------|
| CityForecast | geography (`northamerica`/`europe`; **no `asia`**) | **units** (`F`/`C`) | file date |
| CityObservation / Station | geography (na/asia/europe) | NULL | file date (Station: NULL) |
| SolarForecast / SolarForecastChange / WindForecast / WindForecastSubRegion | ISO region | NULL | file date |
| NationalDegreeDays | `northamerica` | `national` | file date (RunDate) |
| Regions5DegreeDays | `northamerica` | `5region` | file date (RunDate) |
| Regions9DegreeDays | `northamerica` | `9region` | file date (RunDate) |
| ISODegreeDays | `northamerica` | `iso` | file date (RunDate) |
| WindTotalCapacity {Climatology,MW,Pct} | NULL | NULL | file date |
| CityGasForecast / DailyNormal / SolarHourly / WindHourly | NULL | NULL | **NULL** (undated) |

> **THE REGION TRAP (critical — read before implementing #16–#18).** There are **two
> distinct "regions"** in the `wdd` family and they must never be conflated:
> 1. **`arm.FileLog.RegionId`** = the **filename geography**, which stays `northamerica`
>    for all four `wdd` endpoints (it is the file's geography axis, resolved to the seeded
>    `arm.Region` lookup — get-or-create per §9 item 7). Because `EndpointId` differs per
>    endpoint (National vs Regions5 vs Regions9 vs ISO) **and** `Variant` differs
>    (`national`/`5region`/`9region`/`iso`), the four families never collide on the
>    `(EndpointId, RegionId, Variant, RepresentativeDate)` hub key despite sharing
>    `RegionId='northamerica'`.
> 2. **The in-file fact `REGION_NAME`** (free-text labels — `Pacific`, `South Central`,
>    `NEW ENGLAND`, `CAISO NORTH`, …) is **data**, stored only in the fact table's
>    `RegionName` column and used as the 3rd merge-key column. These labels **MUST NOT be
>    added to the `arm.Region` seed catalog** — that catalog holds filename axes only
>    (geographies + ISO *filename* regions). Only `northamerica` is ever passed to
>    `usp_UpsertFileLog` for these endpoints, so the us5/us9/iso labels never reach
>    `arm.Region`.

**Why `RepresentativeDate` is NULL for undated files (critical):** an undated file's
FileLog row must be *stable* across daily re-pulls so the same `FileLogId` is reused and
its facts MERGE in place. If it varied by RunDate, each day would mint a new `FileLogId`
and (for a natural-key MERGE) that is still fine for the facts, but it would fragment the
audit hub. We keep **one** hub row per undated endpoint, **upserted** each run
(Status/HttpStatus/RowCount/`LastCheckedUtc` refreshed) — the StormVista upsert posture.
The "re-pulled today" signal lives in `LastCheckedUtc`+`RowCount`, not in extra rows.

**Per-request flow in `CwgSourceReader<TRecord,TRow>.ReadAsync` (StormVista shape):**
1. Build sanitized `RequestPath = "/" + unit.Filename` and the absolute URI
   `BaseUrl + RequestPath + "?apikey=" + EscapeDataString(ApiKey)`. Log only the
   sanitized path (never the query).
2. `file = new CwgFileContext(EndpointId, unit.Region, unit.Variant, unit.RepresentativeDate, RequestPath)`.
3. GET. `httpStatus = (int)response.StatusCode`.
   - **404** → `fileLog.UpsertAsync(file, "NotAvailable", 404, RequestPath, 0)`; return
     `Array.Empty` — **no unit failure** (holiday gaps / region-not-modeled / obs-today).
   - non-success (401/403, or 429/5xx after retries) → `throw HttpRequestException`.
   - **200** → read CSV → `shapeParser.Parse(...)` → map each record via the row factory,
     **dropping nulls** → `rows`.
4. Outcome status = `rows.Count == 0 ? "NotAvailable" : "Success"` (decision 5: a 404
   **or** a zero-row 200 both log `NotAvailable`, no failure).
   `fileLogId = fileLog.UpsertAsync(file, status, httpStatus, RequestPath, rows.Count)`.
   If `rows.Count > 0`, stamp `r.FileLogId = fileLogId` on every row and return them;
   else return empty.
5. `catch (OperationCanceledException) → throw;` **without** a FileLog write (per-unit
   timeout / run cancellation is already recorded by `LoadLog`; don't write a spurious
   hub row on a spent token).
6. `catch (Exception)` → best-effort `UpsertAsync(file, "Failed", httpStatus, RequestPath, 0)`
   (with `CancellationToken.None`, never masking the original), then **rethrow** so
   `LoaderPipelineBase` records the `LoadLog` failure and the run continues to the next
   unit (fail-a-block-not-the-run).

`SqlCwgFileLog.UpsertAsync` calls `arm.usp_UpsertFileLog` under
`SqlWriteGate.AcquireAsync(KeyFor(ConnectionString, "arm.usp_UpsertFileLog"))`, passing
explicit-typed params (Endpoint, Region NULL-able, Variant NULL-able, RepresentativeDate
DATE NULL, StatusLabel, HttpStatus INT NULL, RequestPath NVARCHAR(400), RowCount), and
reads the returned scalar `FileLogId` (StormVista pattern). Endpoint/Status may be
resolved to small seeded lookups server-side (§9); the C# contract speaks strings.

---

## 6. Full-field mapping (all 18 endpoints)

Legend: **origin** = `unit` (from filename/work-unit) or the exact source CSV column.
Every fact row also carries `FileLogId` (provenance; stamped in §5; UPDATEd on MERGE, not
a merge-key column). `ModifiedAtUtc` is a table default. Types are the CWG.md
recommendations (see §9 for the sizing confirmations). "Key" marks the MERGE natural key.

### 1. CityForecast → `arm.CityForecast` (Shape A) — merge key `(Region, Station, ProductionDate, ForecastDate)`

**Region set = `{northamerica, europe}` (asia dropped). Per-region units:** northamerica in
`F` (`_F` file), europe in `C` (`_C` file), via the `{units}` token + `RegionUnits={F,C}`
(§2/§3). `Units` is a **self-describing ATTRIBUTE column, NOT a key** — `Region` already
keys each row and units is 1:1 with region, so the merge key / PK / MERGE-ON / PARTITION BY
are **unchanged**; only the row, TVP, and INSERT/UPDATE column lists gain `Units`.

| Origin | Row prop | TVP/table col | Type | Notes |
|--------|----------|---------------|------|-------|
| unit.Region | Region | Region | VARCHAR(16) | **Key**; geography literal (`northamerica`/`europe`) |
| `Production Date` | ProductionDate | ProductionDate | DATE | **Key**; `M/D/YY` (2-digit yr) |
| `Date` | ForecastDate | ForecastDate | DATE | **Key**; `M/D/YY` |
| `Station` | Station | Station | VARCHAR(8) | **Key** |
| `Fcst Mn` | FcstMin | FcstMin | DECIMAL — WIDENED | signed; 1 dp in both `_F`/`_C`; widened with the others for uniformity (§9 item 18) |
| `Fcst Mx` | FcstMax | FcstMax | DECIMAL — WIDENED | 1 dp both |
| `Fcst Avg` | FcstAvg | FcstAvg | DECIMAL — WIDENED | `.5` occurs; 1 dp both |
| `Norm Mn` | NormMin | NormMin | DECIMAL — WIDENED (≥5 dp) | **`_C` normals carry up to 5 dp** (e.g. `7.74478`); `_F` = 1 dp. **Preserve source precision — do NOT round in the loader** (§9 item 18) |
| `Norm Max` | NormMax | NormMax | DECIMAL — WIDENED (≥5 dp) | header quirk `Norm Max`; `_C` up to 5 dp (e.g. `20.3699`) |
| `HDD` | Hdd | Hdd | SMALLINT | |
| `CDD` | Cdd | Cdd | SMALLINT | |
| unit.Units | **Units** | **Units** | VARCHAR(1) (DB's call; F/C) | **Attribute, not key.** Value from the descriptor's `RegionUnits` (via the work unit); **appended LAST** in the row / TVP / BuildTable / INSERT-UPDATE lists (FileLogId stays first, all existing positions stable) |

**`Units` end-to-end flow (what CODER wires):**
1. **descriptor** — `RegionUnits={F,C}` aligned to `Regions={northamerica,europe}`; template `…_{units}.csv`.
2. **work unit** — provider stamps `Units = F|C` (and `Variant = F|C` for the FileLog) per §3.
3. **row factory** — `CityForecastRow.From(rec, unit)` copies `Units = unit.Units` (guard: `unit.Units is null → return null`, mirroring the existing `Region` guard). No other field changes; **do NOT round** `NormMin/NormMax` — parse with `CwgParse.Decimal` (full `decimal` precision) as today.
4. **row** — `CityForecastRow` gains `public required string Units { get; init; }`, declared **last** (property order documents TVP order).
5. **sink `BuildTable`** — append `t.Columns.Add("Units", typeof(string));` **last**, and `r.Units` last in `t.Rows.Add(...)`. The dedup `GroupBy (Region, Station, ProductionDate, ForecastDate)` is **unchanged** (Units is not part of the key).
6. **TVP** `arm.CityForecastTvp` — append `Units VARCHAR(1) NOT NULL` last (sink order == TVP order — the load-bearing contract).
7. **merge proc** `arm.usp_BulkMergeCityForecast` — add `Units` to the `SELECT`, the `INSERT (…)` column list and the `WHEN MATCHED … UPDATE SET` list; the `PARTITION BY` / `ON` / PK stay exactly as-is.

### 2. CityGasForecast → `arm.CityGasForecast` (Shape A) — merge key `(Station, ProductionDate, ForecastDate)`
Identical 10 columns to #1 (`Production Date→ProductionDate` Key, `Date→ForecastDate` Key,
`Station` Key, `Fcst Mn/Mx/Avg`, `Norm Mn/Max`, `HDD`, `CDD`) — no `Region` (undated hub;
production date comes from the CSV, not the filename).

### 3. CityObservation → `arm.CityObservation` (Shape A) — merge key `(Region, Station, ObsDate)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.Region | Region | Region | VARCHAR(16) | **Key** |
| `date` | ObsDate | ObsDate | DATE | **Key**; `YYYY-MM-DD`; = runDate−1 |
| `station` | Station | Station | VARCHAR(8) | **Key** |
| `MinTemp` | MinTemp | MinTemp | DECIMAL(5,1) | |
| `MaxTemp` | MaxTemp | MaxTemp | DECIMAL(5,1) | |
| `HDD` | Hdd | Hdd | SMALLINT | |
| `CDD` | Cdd | Cdd | SMALLINT | |

### 4. DailyNormal → `arm.DailyNormal` (Shape B) — merge key `(MonthDay, Region)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| `DATE` (key cell) | MonthDay | MonthDay | CHAR(5) | **Key**; `MM-DD` kept literal (incl. `02-29`, 366 rows) |
| region column name | Region | Region | VARCHAR(10) | **Key**; one of the 12 |
| cell value | NormalMw | NormalMw | DECIMAL(12,4) | |

### 5. SolarForecast → `arm.SolarForecast` (Shape C) — merge key `(Region, InitDate, ForecastDate, HourOfDay)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.Region | Region | Region | VARCHAR(10) | **Key** |
| unit.date (filename) | InitDate | InitDate | DATE | **Key** |
| `Date (EST)` header cell | ForecastDate | ForecastDate | DATE | **Key**; `M/D/YYYY`; init−1…+14 |
| hour label | HourLabel | HourLabel | VARCHAR(8) | store literal (§9) |
| hour index | HourOfDay | HourOfDay | TINYINT | **Key**; 0–23 |
| matrix cell | ValueMw | ValueMw | DECIMAL(12,4) | ≥0 |

### 6. SolarForecastChange → `arm.SolarForecastChange` (Shape C) — merge key `(Region, InitDate, ForecastDate, HourOfDay)`
Same columns as #5 except the measure is **signed** `ChangeMw DECIMAL(12,4)` (source
cell), and `ForecastDate` is `MM/DD/YYYY` starting at `init`. The two footer rows
(`sum change`, `average change`) are **skipped** by the parser.

### 7. SolarHourly → `arm.SolarHourly` (Shape B) — merge key `(HourEndingUtc, Region)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| `UTC_HOUR_ENDING` (key cell) | HourEndingUtc | HourEndingUtc | DATETIME2(0) | **Key**; `YYYY-MM-DD HH:MM:SS` UTC |
| region column name | Region | Region | VARCHAR(10) | **Key**; one of the 14 |
| cell value | ActualMw | ActualMw | DECIMAL(12,4) NULL | `"NULL"`/blank → **row skipped** |

### 8. NationalDegreeDays → `arm.NationalDegreeDays` (Shape A) — merge key `(RunDate, Dates)`
| Origin | Row prop | Col | Type |
|--------|----------|-----|------|
| unit.date (filename) | RunDate | RunDate | DATE **Key** |
| `DATES` | Dates | Dates | DATE **Key** (`YYYY-MM-DD`) |
| `NG_HDD` | NgHdd | NgHdd | DECIMAL(9,4) |
| `30Y_NG_HDD` | NgHdd30y | NgHdd30y | DECIMAL(9,4) |
| `10Y_NG_HDD` | NgHdd10y | NgHdd10y | DECIMAL(9,4) |
| `LAST_Y_NG_HDD` | NgHddLastY | NgHddLastY | DECIMAL(9,4) |
| `POP_CDD` | PopCdd | PopCdd | DECIMAL(9,4) |
| `30Y_POP_CDD` | PopCdd30y | PopCdd30y | DECIMAL(9,4) |
| `10Y_POP_CDD` | PopCdd10y | PopCdd10y | DECIMAL(9,4) |
| `LAST_Y_POP_CDD` | PopCddLastY | PopCddLastY | DECIMAL(9,4) |
| `ELEC_CDD` | ElecCdd | ElecCdd | DECIMAL(9,4) |
| `30Y_ELEC_CDD` | ElecCdd30y | ElecCdd30y | DECIMAL(9,4) |
| `10Y_ELEC_CDD` | ElecCdd10y | ElecCdd10y | DECIMAL(9,4) |
| `LAST_Y_ELEC_CDD` | ElecCddLastY | ElecCddLastY | DECIMAL(9,4) |
| `IS_FORECAST` | IsForecast | IsForecast | BIT (`True/False`) |

(Column names 3–4/7–8/11–12 lead with a digit in the source; the C# props/DB columns are
renamed as above — mapped by ordered position, not header text.)

### 9. WindForecast → `arm.WindForecast` (Shape C) — merge key `(Region, InitDate, ForecastDate, HourOfDay)`
Same column set as #5 (`Region`, `InitDate`, `ForecastDate` `M/D/YYYY` init−1…+14,
`HourLabel`, `HourOfDay`, `ValueMw DECIMAL(12,4)`); the 16-region filename axis.

### 10. WindForecastSubRegion → `arm.WindForecastSubRegion` (Shape D) — merge key `(Region, SubRegion, InitDate, ForecastDate, HourOfDay)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.Region | Region | Region | VARCHAR(10) | **Key**; parent region |
| block label (` region` stripped) | SubRegion | SubRegion | VARCHAR(40) | **Key**; data-driven, keep prefixes/hyphens |
| unit.date | InitDate | InitDate | DATE | **Key** |
| `Date (EST)` header cell | ForecastDate | ForecastDate | DATE | **Key**; `M/D/YYYY`, init…+14 (**15** cols) |
| hour label | HourLabel | HourLabel | VARCHAR(8) | |
| hour index | HourOfDay | HourOfDay | TINYINT | **Key** |
| matrix cell | ValueMw | ValueMw | DECIMAL(12,4) | |

### 11. WindHourly → `arm.WindHourly` (Shape B) — merge key `(HourEndingUtc, Region)`
Same shape as #7: `UTC_HOUR_ENDING→HourEndingUtc DATETIME2(0)` **Key**, region column →
`Region VARCHAR(10)` **Key** (12-region set), cell → `ActualMw DECIMAL(12,4) NULL`
(`"NULL"`→skip).

### 12. WindTotalCapacityClimatology → `arm.WindTotalCapacityClimatology` (Shape E, 1 block) — merge key `(ProductionDate, Region)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.date | ProductionDate | ProductionDate | DATE | **Key** |
| region label | Region | Region | VARCHAR(10) | **Key**; data-driven |
| `Total Capacity (MW)` | TotalCapacityMw | TotalCapacityMw | DECIMAL(12,4) | |
| `1-5 Day Avg (%)` | Avg_1_5 | Avg_1_5 | DECIMAL(6,2) | strip `%` |
| `6-10 Day Avg (%)` | Avg_6_10 | Avg_6_10 | DECIMAL(6,2) | strip `%` |
| `11-15 Day Avg (%)` | Avg_11_15 | Avg_11_15 | DECIMAL(6,2) | strip `%` |

Footer rows `Total All (MW)` / `Total Percent` are skipped. (No `Block` column — single block.)

### 13. WindTotalCapacityMW → `arm.WindTotalCapacityMW` (Shape E, 3 blocks) — merge key `(ProductionDate, Block, Region)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.date | ProductionDate | ProductionDate | DATE | **Key** |
| derived block | Block | Block | VARCHAR(12) | **Key**; `Current|Yesterday|Change` |
| region label | Region | Region | VARCHAR(10) | **Key** |
| `Total Capacity (MW)` | TotalCapacityMw | TotalCapacityMw | DECIMAL(12,4) NULL | blank in Change → NULL |
| `1-5 Day Avg (MW)` | Avg_1_5 | Avg_1_5 | DECIMAL(12,4) | signed in Change |
| `6-10 Day Avg (MW)` | Avg_6_10 | Avg_6_10 | DECIMAL(12,4) | signed in Change |
| `11-15 Day Avg (MW)` | Avg_11_15 | Avg_11_15 | DECIMAL(12,4) | signed in Change |

Footers `Total All (MW)` / `Total Percent` / `Total Change` skipped.

### 14. WindTotalCapacityPct → `arm.WindTotalCapacityPct` (Shape E, 3 blocks) — merge key `(ProductionDate, Block, Region)`
Same columns as #13 but the avg columns are **`DECIMAL(6,2)`**, values `strip %` and are
signed in the Change block; `TotalCapacityMw DECIMAL(12,4) NULL` (blank in Change).

### 15. Station → `arm.Station` (Shape A) — merge key `(Region, Identifier)`
| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.Region | Region | Region | VARCHAR(16) | **Key**; geography |
| `identifier` | Identifier | Identifier | VARCHAR(8) | **Key** |
| `wmoid` | WmoId | WmoId | VARCHAR(11) NULL | keep as string |
| `wban` | Wban | Wban | VARCHAR(11) NULL | keep literal (no sentinel mapping) |
| `ghcnd` | Ghcnd | Ghcnd | VARCHAR(16) NULL | empty→NULL |
| `lat` | Lat | Lat | DECIMAL(9,6) | |
| `lon` | Lon | Lon | DECIMAL(9,6) | signed |
| `name` | Name | Name | NVARCHAR(64) | source truncates ~16 |
| `state` | State | State | VARCHAR(8) NULL | may be blank |
| `country` | Country | Country | VARCHAR(4) | |

### 16. Regions5DegreeDays → `arm.Regions5DegreeDays` (Shape A) — merge key `(RunDate, Dates, RegionName)`
18 source columns; the DD family is offset by **+1** vs National (§6.8) because `REGION_NAME`
occupies `Fields[1]`. `RunDate` from the unit (filename); `RegionName` from the CSV
`REGION_NAME` cell (`Fields[1]`) — **not** from the unit. Source header positions in brackets.

| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.date (filename) | RunDate | RunDate | DATE | **Key** |
| `DATES` [0] | Dates | Dates | DATE | **Key** (`YYYY-MM-DD`) |
| `REGION_NAME` [1] | RegionName | RegionName | VARCHAR(32) | **Key**; in-file fact region (free text, e.g. `South Central`; max obs 13). See Region-trap §5 |
| `NG_HDD` [2] | NgHdd | NgHdd | DECIMAL(9,4) | |
| `30Y_NG_HDD` [3] | NgHdd30y | NgHdd30y | DECIMAL(9,4) | digit-leading header → renamed |
| `10Y_NG_HDD` [4] | NgHdd10y | NgHdd10y | DECIMAL(9,4) | `0.0000` is valid, not null |
| `LAST_Y_NG_HDD` [5] | NgHddLastY | NgHddLastY | DECIMAL(9,4) | |
| `POP_CDD` [6] | PopCdd | PopCdd | DECIMAL(9,4) | |
| `30Y_POP_CDD` [7] | PopCdd30y | PopCdd30y | DECIMAL(9,4) | |
| `10Y_POP_CDD` [8] | PopCdd10y | PopCdd10y | DECIMAL(9,4) | |
| `LAST_Y_POP_CDD` [9] | PopCddLastY | PopCddLastY | DECIMAL(9,4) | |
| `ELEC_CDD` [10] | ElecCdd | ElecCdd | DECIMAL(9,4) | |
| `30Y_ELEC_CDD` [11] | ElecCdd30y | ElecCdd30y | DECIMAL(9,4) | |
| `10Y_ELEC_CDD` [12] | ElecCdd10y | ElecCdd10y | DECIMAL(9,4) | |
| `LAST_Y_ELEC_CDD` [13] | ElecCddLastY | ElecCddLastY | DECIMAL(9,4) | |
| `IS_FORECAST` [14] | IsForecast | IsForecast | BIT | `True/False` → 1/0 |
| `GAS_WEIGHT` [15] | GasWeight | GasWeight | DECIMAL(9,4) | 0..1 fraction; constant per region within a file |
| `ELCT_WEIGHT` [16] | ElctWeight | ElctWeight | DECIMAL(9,4) | **header spelled `ELCT`**, not `ELEC` — keep the prop name `ElctWeight` |
| `POP_WEIGHT` [17] | PopWeight | PopWeight | DECIMAL(9,4) | 0..1 fraction |

`END.` footer row skipped by the shared Shape-A `END.` guard (§4). ~110 fact rows/file
(5 regions × 22-day in-file window). Batch de-dup GroupBy `(RunDate, Dates, RegionName)`.

### 17. Regions9DegreeDays → `arm.Regions9DegreeDays` (Shape A) — merge key `(RunDate, Dates, RegionName)`
**Column set, positions, types, and row shape are IDENTICAL to §6.16** (same 18 columns,
same `+1` offset, same weights incl. the `ELCT_WEIGHT` spelling, same digit-leading
headers). The **only** difference is the `RegionName` value set (9 U.S. Census divisions,
UPPERCASE, e.g. `NEW ENGLAND`, `MIDDLE ATLANTIC`; max obs 15 — still fits `VARCHAR(32)`).
Row type `Regions9DegreeDaysRow` mirrors `Regions5DegreeDaysRow`. ~198 fact rows/file
(9 × 22). `END.` footer skipped. Merge/de-dup key `(RunDate, Dates, RegionName)`.

### 18. ISODegreeDays → `arm.ISODegreeDays` (Shape A) — merge key `(RunDate, Dates, RegionName)`
**⚠ DIVERGENT 11-column layout — do NOT reuse §6.16's column set.** The HDD family is
**`POP_HDD`** (population-weighted), **not `NG_HDD`**; there is **NO `ELEC_*` family** and
**NO weight columns**. `RegionName` from `Fields[1]`; `RunDate` from the unit.

| Origin | Row prop | Col | Type | Notes |
|--------|----------|-----|------|-------|
| unit.date (filename) | RunDate | RunDate | DATE | **Key** |
| `DATES` [0] | Dates | Dates | DATE | **Key** (`YYYY-MM-DD`) |
| `REGION_NAME` [1] | RegionName | RegionName | VARCHAR(32) | **Key**; ISO/market label (e.g. `CAISO NORTH`; max obs 11). See Region-trap §5 |
| `POP_HDD` [2] | PopHdd | PopHdd | DECIMAL(9,4) | **`POP_HDD`, NOT `NG_HDD`** |
| `30Y_POP_HDD` [3] | PopHdd30y | PopHdd30y | DECIMAL(9,4) | digit-leading header → renamed |
| `10Y_POP_HDD` [4] | PopHdd10y | PopHdd10y | DECIMAL(9,4) | `0.0000` valid, not null |
| `LAST_Y_POP_HDD` [5] | PopHddLastY | PopHddLastY | DECIMAL(9,4) | |
| `POP_CDD` [6] | PopCdd | PopCdd | DECIMAL(9,4) | |
| `30Y_POP_CDD` [7] | PopCdd30y | PopCdd30y | DECIMAL(9,4) | |
| `10Y_POP_CDD` [8] | PopCdd10y | PopCdd10y | DECIMAL(9,4) | |
| `LAST_Y_POP_CDD` [9] | PopCddLastY | PopCddLastY | DECIMAL(9,4) | |
| `IS_FORECAST` [10] | IsForecast | IsForecast | BIT | `True/False` → 1/0 |

`END.` footer skipped. ~462 fact rows/file (21 ISO/market regions × 22). Merge/de-dup key
`(RunDate, Dates, RegionName)`. Row type `ISODegreeDaysRow` (its own 11-field shape).

Each sink's `BuildTable` column order must match its TVP exactly (SqlSinkBase contract);
each `SqlSinkBase<TRow>` sets `StoredProcedureName`/`TableValuedParameterType`/
`GetConnectionString` and de-dups the batch on its merge key before building the TVP
(Platts/StormVista posture) so an in-file duplicate can't break the MERGE. `FileLogId` is
the first TVP column on every table.

---

## 7. Settings shape (`Loaders:CWG`)

`CwgSettings : LoaderSettingsBase` (inherits `ConnectionString`, `MaxConcurrentWorkUnits`,
`RetryCount`, `RetryDelayMs`, `WorkUnitTimeoutSeconds`) adds:

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `BaseUrl` | string | `https://api.commoditywx.com/v1` | API host + `/v1` |
| `ApiKey` | string | `SEE_DB` | resolved from `core.Param` by `AddLoaderSettings`; `?apikey=`; never logged |
| `HttpTimeoutSeconds` | int | 60 | per-request timeout on the shared client |
| `EnabledEndpoints` | string[] | all 18 EndpointIds | toggle; matched case-insensitively to `ICwgEndpointPipeline.EndpointId` |
| `DaysBack` | int | 3 | dated-endpoint look-back window (undated ignore it) |
| `HotZoneKeyStrategy` | `RunDate`\|`RunId` | `RunDate` | undated "latest" file re-pull cadence (decision 6) |
| `Geographies` | string[] | `northamerica,asia,europe` | filters `RegionKind==Geography` descriptors only |
| `RequestsPerSecond` | double? | (e.g. 25) | optional global client-side throttle; null/≤0 = unlimited |

`appsettings.json` `Loaders:CWG` mirrors the StormVista block: `ConnectionString`
(`Server=ARMH-OPSDB01;Database=CWG;Integrated Security=SSPI;TrustServerCertificate=True;`),
`ApiKey:"SEE_DB"`, the tuning knobs above, `EnabledEndpoints`, `DaysBack:3`,
`HotZoneKeyStrategy:"RunDate"`, `Geographies:[…]`, `RequestsPerSecond`. **Leave `"CWG"`
OUT of `Platform:EnabledLoaders`** (build-only pass — loader disabled by default). The
real key lives in `core.Param(LoaderName='CWG', ParamName='ApiKey')` (or env
`DATALOADER_Loaders__CWG__ApiKey`), never in the file.

---

## 8. Concurrency & idempotency

- **Sinks** derive from `SqlSinkBase<TRow>`, which auto-acquires `SqlWriteGate` keyed
  `{server}/{db}::{proc}`. The 18 fact procs are 18 distinct keys, so different endpoints
  never serialize against each other; two concurrent work units of the *same* endpoint
  serialize on that one proc key (correct — prevents the parallel-MERGE deadlock /
  insert-race). With endpoints run sequentially (§1.4), only within-endpoint units contend.
- **FileLog** is a **direct** proc writer, so `SqlCwgFileLog` acquires `SqlWriteGate`
  explicitly on `arm.usp_UpsertFileLog` (one shared key across all 18 endpoints; a fast
  single-row upsert, so serializing it is cheap and deadlock-proof) — Platts/StormVista
  posture. `core.LoadLog` is intentionally not gated (keyed per work unit).
- **Idempotent MERGE.** Every fact proc MERGEs on the endpoint's **natural key** (§6),
  never on `FileLogId`; `FileLogId` is stamped/updated as provenance. So dated re-runs and
  undated daily re-pulls both upsert in place — no duplicates, safe over-scheduling. The
  platform overlap guard (`DataLoader:CWG` app-lock) already prevents two host processes
  from running the loader at once.

---

## 9. Open items for DATABASE_DEVELOPER / reviewer — RESOLVED

Parent-agent decisions on the designer's open items (apply these):

1. **Three capacity tables** — CONFIRMED: `arm.WindTotalCapacityClimatology`,
   `arm.WindTotalCapacityMW`, `arm.WindTotalCapacityPct`. MW/Pct carry a `Block` column;
   Climatology does not.
2. **Footer-row storage** — CONFIRMED **skip** all footers (`sum/average change for the
   day`; `Total All (MW)`/`Total Percent`/`Total Change`).
3. **HourLabel storage** — CONFIRMED store **both** `HourLabel VARCHAR(8)` and
   `HourOfDay TINYINT`.
4. **Hourly `"NULL"` sentinel** — CONFIRMED **skip** (emit no row) for
   SolarHourly/WindHourly `"NULL"`/blank cells.
5. **Station sentinels** — keep `wban` **literal** as string (no `99999`→NULL mapping;
   lossless); `ghcnd`/`state` empty → NULL.
6. **DailyNormal `MonthDay`** — CONFIRMED `CHAR(5)` (`MM-DD`, year-agnostic, 366 rows).
7. **FileLog identity** — `arm.FileLog UNIQUE (EndpointId, RegionId, Variant,
   RepresentativeDate)` with SQL NULL-equality. NationalDegreeDays uses
   `Region='northamerica'`, `Variant='national'`. **RESOLVED (revised):** `Endpoint`,
   `Region` and `Status` are **normalized to surrogate-Id lookup tables**
   (`arm.Endpoint` / `arm.Region` / `arm.Status`, each `Id INT IDENTITY` PK + UNIQUE
   `Name`), matching StormVista and the standing `DATABASE_DEVELOPER` convention. FileLog
   stores `EndpointId` / `RegionId` / `StatusId` FKs; `usp_UpsertFileLog` keeps its
   name-string signature and resolves names→Ids server-side (Endpoint/Status must exist —
   RAISERROR on miss; Region is get-or-create for non-NULL). Supersedes the earlier
   build-only suggestion of flat inline `VARCHAR + CHECK`.
8. **`FileLogId` = provenance, not merge key** — CONFIRMED. Facts MERGE on natural
   columns; `FileLogId` is a regular FK column updated on match.
9. **Station is undated** — CONFIRMED same RunDate hot key + `RepresentativeDate=NULL`.
10. **`Region` width** — CONFIRMED `VARCHAR(16)` for geography endpoints (#1/#3/#15);
    `VARCHAR(10)` for ISO endpoints.
11. **DECIMAL sizing** — apply CWG.md families: `DECIMAL(12,4)` MW; `DECIMAL(6,2)` pct;
    `DECIMAL(9,4)` national DD; `DECIMAL(5,1)` temps; `SMALLINT` city HDD/CDD;
    `DECIMAL(9,6)` lat/lon.
12. **Endpoint-level parallelism** — CONFIRMED sequential endpoints (global throttle caps RPS).

**Regional / ISO degree-day additions (2026-08, #16–#18):**

13. **Three separate tables** — CONFIRMED (user-directed): `arm.Regions5DegreeDays`,
    `arm.Regions9DegreeDays`, `arm.ISODegreeDays`, each its own descriptor / table / TVP /
    merge proc / `EnabledEndpoints` entry / `arm.Endpoint` seed row, mirroring the
    per-endpoint pattern. **Not** a single unified table: ISO's divergent column set
    (`POP_HDD`, no `ELEC_*`, no weights) makes unification awkward, and the user explicitly
    asked for the three named tables. (5region and 9region *could* share a table since their
    layouts are identical, but they are kept separate for symmetry and per the directive.)
14. **Merge key `(RunDate, Dates, RegionName)`** — CONFIRMED for all three. `RunDate` from
    the filename, `Dates` from the `DATES` column, `RegionName` from the in-file
    `REGION_NAME`. `FileLogId` is provenance (updated on match), never a merge-key column —
    consistent with item 8.
15. **Shared `END.`-footer skip in `ShapeAParser`** — the pivotal parse decision (§4).
    Reuse Shape A; add one explicit `cells[0]=="END."` → skip (Debug) ahead of the
    short-row guard. Purely additive; also removes NationalDegreeDays' current spurious
    per-file Warning. **Reviewer confirm:** OK to touch the shared `ShapeAParser` (used by
    all 8 Shape-A endpoints) for this behavior-preserving change vs. leaving National's
    noisy short-row skip in place.
16. **`RegionName VARCHAR(32)` column naming** — the in-file fact region column is named
    `RegionName` (maps source `REGION_NAME`) to keep it visibly **distinct** from the
    geography `Region` used elsewhere and from `arm.FileLog.RegionId` (the Region trap,
    §5). It is the column the PK/merge-key text calls "Region". `VARCHAR(32)` covers the
    observed maxima (5region 13 / 9region 15 / iso 11) with headroom. **Reviewer confirm**
    the `RegionName` name (vs. plain `Region`) and the `(9,4)` weight sizing
    (`GAS_WEIGHT`/`ELCT_WEIGHT`/`POP_WEIGHT` are 0..1 fractions; CWG.md notes `(7,4)` would
    also suffice — DATABASE_DEVELOPER's call).
17. **`arm.Endpoint` seed rows** — add three: `Regions5DegreeDays`, `Regions9DegreeDays`,
    `ISODegreeDays` (coordinate with DATABASE_DEVELOPER). `arm.Region` gets **no** new
    rows — the four `wdd` endpoints all log `RegionId='northamerica'` (already seeded); the
    in-file `REGION_NAME` labels never touch `arm.Region` (§5 Region trap).

**CityForecast per-region units (2026-08, #1):**

18. **`Units` attribute column on `arm.CityForecast`** — add a `Units VARCHAR(1) NOT NULL`
    column (F/C; DATABASE_DEVELOPER picks the exact width — `VARCHAR(1)` suffices, a touch
    wider is harmless). It is an **ATTRIBUTE, NOT a key**: `Region` already uniquely keys a
    row and units is 1:1 with region, so **do NOT** change `PK_CityForecast`, the MERGE
    `ON`, or the `PARTITION BY`. Changes are confined to: the table (append `Units` — a
    default or a data-migration for any pre-existing rows is unnecessary since CWG is
    build-only/never loaded), the TVP `arm.CityForecastTvp` (append `Units` **last**, after
    `Cdd`, to match the sink's `BuildTable` order), and `arm.usp_BulkMergeCityForecast`
    (add `Units` to `SELECT` / `INSERT` / `UPDATE SET`). Column position: **append last**;
    `FileLogId` stays the first TVP column.
19. **Widen the CityForecast temperature `DECIMAL`s (preserve source precision)** — the
    europe `_C` file's `Norm Mn` / `Norm Max` carry up to **5 decimal places**
    (e.g. `7.74478`, `20.3699`) vs 1 dp in `_F`; `Fcst Mn/Mx/Avg` are 1 dp in both. The
    loader **must not round** (row uses full-precision `decimal`), so the **table AND TVP**
    columns must widen from `DECIMAL(5,1)` — **the TVP width matters too** (a `DECIMAL(5,1)`
    TVP column would round the value on load). Requirement: `NormMin`/`NormMax` need scale
    ≥ 5. **Recommended (DATABASE_DEVELOPER's exact `(p,s)` call):** widen all five temp
    columns uniformly, e.g. `DECIMAL(8,5)` (covers F magnitudes ~≤130 and C ~≤55 with 5 dp),
    in **both** `arm.CityForecast` and `arm.CityForecastTvp`. Fcst columns widen only for
    consistency (their data stays 1 dp).
20. **`arm.Endpoint` seed URL template** — CityForecast's single seed row `Url` must change
    from `…/city15dfcst_{region}_{date}_F.csv` to `…/city15dfcst_{region}_{date}_{units}.csv`
    (one endpoint, one seed row — no new `arm.Endpoint` row). `arm.Region` needs no change
    (`northamerica`/`europe` already seeded; `asia` simply stops being requested).
21. **Compare-script scoping (`CompareLegacyDboVsArm.sql` §1)** — `arm.CityForecast` will now
    hold europe `_C` rows and **no** `asia` rows, while legacy `dbo.CityForecast` is
    northamerica/°F only. The CityForecast comparison currently joins arm↔dbo on
    `(ProductionDate, ForecastDate, Station)` with **no** region/units scope, so europe rows
    (and any europe station code colliding with a US one) would be miscounted as
    `ExtraInArm` or spuriously paired. **Scope the arm side like-for-like:** restrict every
    `arm.CityForecast` predicate (row count, `MatchedKeys`, `ExtraInArm`, value-mismatch
    join, and the `1b`/`1c` detail selects) to `a.Units = 'F'` **and** `a.Region =
    'northamerica'` so only the comparable NA/°F subset is measured. DATABASE_DEVELOPER owns
    the script edit.
22. **API-doc reconciliation (note for DATABASE_DEVELOPER, not this doc):** `docs/apis/CWG.md`
    §1 briefly lists `Units` inside the CityForecast **natural key**. That is now incorrect —
    `Units` is an attribute, not a key. Fix the one line in the API doc so the field reference
    matches this design (key stays `Region + Station + ProductionDate + Date`).

---

## Coverage checklist — all 18 endpoints

| # | Endpoint | Descriptor (§2.1) | Work-unit / key rule (§3) | Shape (§4) | FileLog identity (§5) | Column map (§6) |
|---|----------|:----------------:|:--------------------------:|:---------:|:---------------------:|:---------------:|
| 1 | CityForecast | ✔ | dated stable, geo×date (geo={na,europe}) | A | Endpoint+geo+units(F/C)+date | ✔ |
| 2 | CityGasForecast | ✔ | undated hot (RunDate) | A | Endpoint (nulls) | ✔ |
| 3 | CityObservation | ✔ | dated stable, off −1 | A | Endpoint+geo+date | ✔ |
| 4 | DailyNormal | ✔ | undated hot (RunDate) | B | Endpoint (nulls) | ✔ |
| 5 | SolarForecast | ✔ | dated stable, ISO×date | C | Endpoint+ISO+date | ✔ |
| 6 | SolarForecastChange | ✔ | dated stable, ISO×date | C | Endpoint+ISO+date | ✔ |
| 7 | SolarHourly | ✔ | undated hot (RunDate) | B | Endpoint (nulls) | ✔ |
| 8 | NationalDegreeDays | ✔ | dated stable, ×date | A | Endpoint+na+national+date | ✔ |
| 9 | WindForecast | ✔ | dated stable, ISO×date | C | Endpoint+ISO+date | ✔ |
| 10 | WindForecastSubRegion | ✔ | dated stable, ISO×date | D | Endpoint+ISO+date | ✔ |
| 11 | WindHourly | ✔ | undated hot (RunDate) | B | Endpoint (nulls) | ✔ |
| 12 | WindTotalCapacityClimatology | ✔ | dated stable, ×date | E(1) | Endpoint+date | ✔ |
| 13 | WindTotalCapacityMW | ✔ | dated stable, ×date | E(3) | Endpoint+date | ✔ |
| 14 | WindTotalCapacityPct | ✔ | dated stable, ×date | E(3) | Endpoint+date | ✔ |
| 15 | Station | ✔ | undated hot (RunDate), geo | A | Endpoint+geo | ✔ |
| 16 | Regions5DegreeDays | ✔ | dated stable, ×date (per RunDate) | A | Endpoint+na+`5region`+date | ✔ |
| 17 | Regions9DegreeDays | ✔ | dated stable, ×date (per RunDate) | A | Endpoint+na+`9region`+date | ✔ |
| 18 | ISODegreeDays | ✔ | dated stable, ×date (per RunDate) | A | Endpoint+na+`iso`+date | ✔ |

All 18 endpoints have a descriptor, a work-unit/keying rule, a parse-shape assignment, a
FileLog identity, and a complete column mapping. Ready for DATABASE_DEVELOPER (three new
tables + TVPs + merge procs + `arm.Endpoint` seed rows; `arm.FileLog`/`arm.usp_UpsertFileLog`
and `arm.Region` unchanged) and CODER (three row types + `From(...)` factories + sinks +
descriptors + module registrations; one shared `END.` skip in `ShapeAParser`).
