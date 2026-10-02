# Loader data sources — how each loader gets its data

A per-loader cheat sheet: transport, auth, and the exact paths requested. Full
field-level contracts live in `docs/apis/<Loader>.md`; settings live in
`src/DataLoader.Host/appsettings.json` (secrets are `SEE_DB` → `core.Param`).

| Transport | Loaders |
|---|---|
| HTTP API | AGSI, CWG, EnergyAspects, EvolutionMarkets, Genscape, IHSPointLogic, IIR, ModernCommodities, NGI, NGX, StormVista, Vulcan |
| HTTP file download | ICE |
| FTP / FTPS | Argus, EOX, OPIS |
| SFTP | CME, Platts |
| PostgreSQL | Criterion |
| SignalR push (vendor SDK) | Marex |

---

## HTTP APIs

### AGSI — `https://agsi.gie.eu` → DB `AGSI`
Auth: header `x-key: <ApiKey>`. 1 req/s.
```
GET /api/about                             # 22 country codes (discovery)
GET /api?country=de&date=2026-09-25        # one entity, one gas day
```
Attribute every `data[]` element by its **own** `code`, and drop rows whose
`gasDayStart` ≠ the requested date (the vendor silently clamps unpublished dates).

### CWG — `https://api.commoditywx.com/v1` → DB `CWG`
Auth: `?apikey=<key>`. 18 endpoints, each addressed as a filename.
```
GET /city15dfcst_northamerica_20260925_F.csv?apikey=…   # dated
GET /daily_normals.csv?apikey=…                         # undated "latest"
```
Templates carry `{region}` (northamerica/asia/europe, or ISO), `{date}` and
`{units}` — full list in `src/DataLoader.CWG/CwgDescriptors.cs`.

### EnergyAspects — `https://api.energyaspects.com` → DB `EnergyAspects`
Auth: `?api_key=<key>`. Time series pulled in 30-day windows.
```
GET /data/dataset_mappings?api_key=…
GET /data/timeseries/?api_key=…&dataset_id=123,124&date_from=2026-08-26&date_to=2026-09-25
```

### EvolutionMarkets — `https://evolve-api.evomarkets.com` → DB `EvolutionMarkets`
Auth: header `Authorization: <bare key>` — **not** Basic. One endpoint, one business
date per request, paged by `limit`/`offset` (5000).
```
GET /v1/market-data/history?dateFrom=2026-09-25&dateTo=2026-09-25&field=<23 field names>&limit=5000&offset=0
```

### Genscape — `https://api.genscape.com/oil-fundamentals/v1` → DB `Genscape`
Auth: header `Gen-Api-Key`. `endDate` is **exclusive**; responses cap at 5000 rows.
```
GET /crude-storage/weekly?region=NorthAmerica&revision=revised&startDate=2026-01-01&endDate=2027-01-01&format=json
GET /crude-transportation/weekly?region=GulfCoast&revision=revised&…
```

### IHSPointLogic — `https://api.connect.ihsmarkit.com` → DB `IHSPointLogic`
Auth: HTTP Basic (ClientId / ClientSecret PAT). 25 endpoints, `pageIndex` paging
(10 000/page), in three families:
```
GET /cs/v1/pointlogic/lookup_region                    # lookups: region, state, pointStatus,
GET /cs/v1/pointlogic/lookup_point                     #   pointType, pipelineNoticeCategory, pipeline, point
GET /cs/v1/plview/retrieve/demandforecast_region       # retrieve facts (10 endpoints)
GET /cs/v1/pointlogic/supplyDemand/region/{id}?reportDate=2026-09-25
GET /cs/v1/pointlogic/volumeHistory/point              # PointVolume, ≤50 point ids per call
```

### IIR — `https://api.industrialinfo.com` → DB `IIR`
Auth: mint a JWT, then `Authorization: Bearer <token>`. Mandatory two-step
summary → detail per endpoint (Plant / Unit / OfflineEvent).
```
POST /idb/v2.7/token?username=…&password=…&tokenLifeTime=1
GET  /idb/v2.7/plants/summary?<filters>       # id discovery, countries U.S.A. / Canada
POST /idb/v2.7/plants/detail?plantId=…        # ≤50 ids per batch
```
Same shape for `units/*` and `offlineevents/*`.

### ModernCommodities — `https://app.modcom.inc/api/integration/` → DB `ModernCommodities`
Auth: HTTP Basic. CSV over HTTP; 30-day re-pull window (filters on `LastUpdated`).
```
GET /allTrades/v1?startDate=2026-08-20&endDate=2026-09-25
GET /myTrades/v1?startDate=2026-08-20&endDate=2026-09-25
GET /settlements/v1?startDate=2026-08-20&endDate=2026-09-25
```

### NGI — `https://api.ngidata.com` → DB `NGI`
Auth: `POST /auth` (username/password) → `Authorization: Bearer <token>`.
```
GET /bidweekLocations?format=json                # location crosswalk
GET /bidweekDatafeed.json?issue_date=2026-08-01  # one bidweek issue
```

### NGX — `https://ngxclearing.ice.com/ngxcs` → DB `NGX`
Auth: **HTTP Basic** (not the documented bearer token); redirects must never be followed.
```
GET /indexPrice.xml?indexId=350&indexId=351&…&effectiveStart=1-September-2026&effectiveEnd=18-September-2026&includeProjected=true&pageSize=20000&page=1
GET /stripTradingSummaryXml.xml?grouping=Hub&tradeStartDate=…&tradeEndDate=…
```
≤10 `indexId` per request; one unentitled id 403s the whole batch. Timestamps are
converted Mountain → US Central.

### StormVista — `https://api.stormvistawxmodels.com/v1` → DB `StormVista`
Auth: `?apikey=<key>`. Two feeds; the model / cycle / region-set axes come from
`dbo.Model`, `dbo.Cycle`, `dbo.RegionSet`. WDD types: `ew_cdd`, `gw_hdd`, `pw_cdd`.
```
GET /model-data/{model}/20260925/00z/wdd/gw_hdd-daily.csv?apikey=…     # Daily
GET /model-data/{model}/20260925/00z/wdd/gw_hdd_reg1.csv?apikey=…      # Regional
```

### Vulcan — `https://hyperion.api.synmax.com` → DB `Vulcan`
Auth: header `Access-Key`. Not REST-per-entity: one SQL-over-HTTP query endpoint,
paged 10 000 rows.
```
POST /v4/beta/query_datalinks    body: { "query": "select … from datacenters where modified_at > …" }
```
Tables: `under_construction`, `datacenters`, `lng_projects`, `project_rankings`,
`metadata_history`.

---

## HTTP file download

### ICE — `https://downloads.ice.com/` → DB `ICE`
Auth: `POST https://sso.theice.com/api/authenticateTfa` (appKey `ICEDOWNLOADS`) →
token replayed as `Cookie: iceSsoCookie=…`. **Hard 30 req/min account-wide.** Every
response is HTTP 200 — outcome is read from the body, never the status code.
21 feeds; date token is `yyyy_MM_dd` (Crude Index uses `yyyyMMdd`):
```
GET /Settlement_Reports_CSV/Gas/icecleared_gas_2026_09_25.dat       # + Power, Oil, NGL, Environmentals,
GET /Settlement_Reports_CSV/Power/ngxcleared_power_2026_09_25.dat   #   options variants
GET /Crude_Index/ICE_Crude_Oil_Index_20260925.csv                   # + ..._Index_Trades_...
GET /ICEF_options_greeks/ICEFCA_Options_2026_09_25.dat              # + ICEFUS_FinOptions, _SoftOptions
GET /FixedIncome_Settlements/IFLL_Options_2026_09_25.xlsx
GET /Settlement_Reports/Environmentals/icecleared_physenv_2026_09_25.xlsx   # XLSX set — a separate
GET /Settlement_Reports/Environmentals/ngxphysical_env_2026_09_25.xlsx      #   publication from _CSV
```

---

## FTP / SFTP file drops

### Argus — FTP `ftp.argusmedia.com:21` → DB `Argus`
```
/DOCUMENTATION/latestCategory.csv    # 15 reference feeds: latestCodes, latestModules,
/DOCUMENTATION/latestQuotes.csv      #   latestModuleDetails, latestPricetype, latestTimestamp,
                                     #   latestTiming, latestUnits, latestUnitCodeConv,
                                     #   latestHoliday(Region), latestQuoteHolidayRegion,
                                     #   latestNewsCategory, latestRvpCodeReference
/DCRDEUS/20260925dhc.csv             # time series: <yyyyMMdd><module>.csv
```

### CME — SFTP `sftp.cmeprod.datahex.rozettatech.com:22` → DB `CMEGroup`
Root is `.` — **absolute paths are rejected**. The drop is walked 5 levels:
`<product>/<feed>/<yyyy>/<MM>/<dd>/<EXCHANGE>_<yyyyMMdd>.txt` (fixed-width report).
```
./BAS_STLAGS/EOD_STLAGS/2026/09/25/STLAGS_20260925.txt
```
8 feeds: `EOD_STLAGS`, `EOD_STLALT`, `EOD_STLCOMEX`, `EOD_STLCPC`, `EOD_STLCUR`,
`EOD_STLEQT`, `EOD_STLINT`, `EOD_STLNYMEX`.

### EOX — FTPS `ftp.eoxlive.com:21` → DB `EOX`
Root `/`; files addressed by exact constructed name `<prefix><yyyyMMdd>_<time>.csv`:
```
/EOD_CSV_C_20260925_1430.csv     # CrudeOil
/EOD_CSV_NG_20260925_1430.csv    # NaturalGas
/EOD_CSV_NGL_20260925_1430.csv   # NGL
```

### OPIS — FTP `ftp.opisnet.com:21` → DB `OPIS`
Root `/`, pattern `*LP.csv`, dated `^(\d{8})LP\.csv$`:
```
/20260925LP.csv
```

### Platts — SFTP `sftp.platts.com:22` → DB `Platts`
```
/20260925/*.ftp                # market data: one folder per yyyyMMdd
/symbols/csv-version/*.csv     # symbol reference
```

---

## Other

### Criterion — PostgreSQL `dda.criterionrsch.com:443`, database `production` → DB `Criterion`
The only relational source (SSL required). 9 feeds read directly:
```
misc.periods                        misc.units                   pipelines.regions
data_series.financial_metadata      pipelines.metadata           pipelines.nomination_points
data_series.financial_json_latest   (series + JSON-unpivot observations)
pipelines pointflows                (derived join: nomination_points × metadata)
```

### Marex — `https://app-ca.neon.markets/api/crude` → DB `Marex`
The only **push** source, via three vendored .NET SDK assemblies (`lib/neon`). No
request fetches data: Auth0 password grant at `login.neon.markets` → SignalR
websocket on hub `Gateway.Crude` → the gateway pushes one opening snapshot per
entity. A run is connect → capture → merge → disconnect.
```
ExchangeDateSnapshot   PeriodGroupSnapshot    PeriodSnapshot
ProductSnapshot        ClosingPriceSnapshot   MarketStatisticsSnapshot
```
Handlers must be attached **before** `Connect`, and the run waits on the snapshots,
not on `ConnectionStatus == Connected`.
