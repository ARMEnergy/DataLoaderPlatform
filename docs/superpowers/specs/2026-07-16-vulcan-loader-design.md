# Vulcan Loader — Design Spec

**Date:** 2026-07-16
**Status:** Approved (pending spec review)
**Loader ID:** `Vulcan`
**Source:** SynMax Vulcan tables via `query_datalinks` beta endpoint
(https://apidocs.synmax.com/v4/beta_query_datalinks.html#vulcan)

## 1. Goal

Add a plugin loader `DataLoader.Vulcan` that pulls all five SynMax Vulcan
datalink tables **incrementally** (only rows changed since the last load) and
MERGEs each into its own SQL Server table in a dedicated `Vulcan` database
(`dbo` schema). The first run of each table backfills the full table. The host and `DataLoader.Core`
do not change (per the plugin contract in CLAUDE.md).

## 2. Decisions (locked)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Tables to ingest | **All five**: `under_construction`, `datacenters`, `lng_projects`, `project_rankings`, `metadata_history` |
| 2 | Refresh model | **Incremental since last load** — each run pulls only rows whose watermark column advanced past the stored watermark, then MERGEs into the target by natural key. First run (no watermark) = full backfill |
| 3 | Deduplication | **At source in the SQL query** via `ROW_NUMBER()` for the tables that need it |
| 4 | C# modeling | **Strongly-typed per table** (own model, TVP, table, merge proc) |
| 5 | Work-unit key scope | **Per table + UTC run date** — one successful pull per table per day |
| 6 | Watermark storage | **Dedicated table** `dbo.LoadWatermark(TableName, WatermarkValue, UpdatedAtUtc)`, one row per table |

### Per-table watermark columns

| Table | Watermark column | Note |
|-------|------------------|------|
| `under_construction` | `date_image` | freshness proxy (latest satellite image date); no true modified column |
| `datacenters` | `modified_at` | genuine last-modified |
| `lng_projects` | `modified_at` | genuine last-modified |
| `project_rankings` | `date_updated` | genuine last-modified |
| `metadata_history` | `date_eia_updated` | EIA monthly update date |

## 3. Structure

One loader (`LoaderId = "Vulcan"`) composed of **five closed per-table
pipelines** that share almost all plumbing, so we get strong typing without
five copies of every class.

**Approaches considered:**
- *(A) Single generic column-bag pipeline* — least code, but loses type safety
  and column-level validation. Rejected (decision #4).
- *(B) Five fully hand-written typed pipelines* — maximal duplication. Rejected.
- *(C, chosen) Five closed pipelines over shared generic bases* — strongly
  typed per table, minimal duplication.

**Shared components (written once):**
- `VulcanWorkUnit : WorkUnit` — carries `TableName`, `RunDate`, and the resolved
  SQL query. `Key => "vulcan:table=<table>;date=<yyyy-MM-dd>"`.
- `VulcanQuerySourceReader<TRow>` — overrides `ReadAsync` to POST the SQL query
  and deserialize the JSON response's data array directly into `List<TRow>`
  (the DB row model), reusing the injected `HttpClient` + Polly retry.
- `VulcanWorkUnitProvider<TRow>` — reads the stored watermark for the table from
  `dbo.LoadWatermark`, then yields exactly one `VulcanWorkUnit` (this table
  for the run date) carrying the resolved incremental query (or the full-backfill
  query when no watermark exists yet).
- `IdentityTransformer<TRow>` — source already deserializes to the row shape.

**Per table (written five times, small):**
- A row model class (properties = documented fields).
- A `SqlSinkBase<TRow>` subclass supplying proc name, TVP type, and `BuildTable`.
- Its `SELECT` query string (with `ROW_NUMBER()` dedup where required).

**Module (`VulcanModule : ILoaderModule`):** registers settings, the shared
HTTP client with Polly retry, and the five closed
`LoaderPipelineBase<VulcanWorkUnit, TRow, TRow>` instances. `RunAsync` runs the
five pipelines and aggregates their `LoaderRunResult`s into one. All five log
under `LoaderId="Vulcan"` with distinct work-unit keys, rolling up under a
single `core.LoaderRun`.

## 4. Source / API access

- **POST** `https://hyperion.api.synmax.com/v4/beta/query_datalinks`
- Header: `Access-Key: <ApiKey>`; `Content-Type: application/json`
- Body: `{"query":"SELECT ... FROM vdl.<table>"}`
- `HttpJsonSourceReaderBase` is GET-only, so `VulcanQuerySourceReader` overrides
  `ReadAsync` to send a POST body.

**Incremental + dedup-at-source queries:**
- Every query filters on the table's watermark column: `WHERE <watermarkcol> >= @watermark`.
  The boundary is **inclusive** (`>=`) because the columns are day-granularity;
  re-pulling the boundary day is harmless since the sink MERGEs by natural key.
- `datacenters`, `project_rankings`, `metadata_history` additionally wrap with
  `ROW_NUMBER() OVER (PARTITION BY synmax_id ORDER BY <watermarkcol> DESC)` and
  filter to `rn = 1` (latest row per id among the changed rows).
- `under_construction`, `lng_projects`: filter on the watermark column
  (`date_image` / `modified_at`), no `ROW_NUMBER()` needed.
- **First run / empty watermark:** the provider omits the `WHERE` filter (or uses
  a floor date) so the whole table is backfilled once, then incremental thereafter.

**Watermark advancement:** the per-table MERGE proc updates
`dbo.LoadWatermark` to `MAX(<watermarkcol>)` of the rows it just merged, in the
same call — so the watermark advances atomically with the data write. A run that
returns zero new rows leaves the watermark unchanged.

**⚠️ Open item (resolve during implementation):** the exact JSON *response
envelope* (plain array vs `{"data":[...]}` vs a columns/rows shape) is not
nailed down in public docs and cannot be confirmed without a live API key. The
deserializer will target one assumed shape and be verified against a real
response. Delegate this confirmation to the `API_DOCUMENTATION_EXPERT` agent
before finalizing the source reader.

## 5. Configuration (`Loaders:Vulcan`)

```jsonc
"Vulcan": {
  "ConnectionString": "Server=ARMH-OPSDB01;Database=Vulcan;Integrated Security=SSPI;TrustServerCertificate=True;",
  "MaxConcurrentWorkUnits": 5,
  "RetryCount": 3,
  "RetryDelayMs": 1000,
  "WorkUnitTimeoutSeconds": 300,
  "ApiKey": "REPLACE-VIA-ENV-VAR",
  "BaseUrl": "https://hyperion.api.synmax.com",
  "QueryEndpoint": "/v4/beta/query_datalinks",
  "HttpTimeoutSeconds": 60,
  "EnabledTables": ["under_construction","datacenters","lng_projects","project_rankings","metadata_history"]
}
```

- Add `"Vulcan"` to `Platform:EnabledLoaders`.
- `ApiKey` supplied via env var `DATALOADER_Loaders__Vulcan__ApiKey` — never in JSON.
- `VulcanSettings : LoaderSettingsBase` adds: `ApiKey`, `BaseUrl`,
  `QueryEndpoint`, `HttpTimeoutSeconds`, `EnabledTables`.

## 6. Idempotency & platform behavior

- Work-unit key `vulcan:table=<table>;date=<yyyy-MM-dd>` (UTC run date). One
  successful snapshot per table per day; same-day reruns skip; next day
  re-pulls. Failed units retry on rerun.
- Overlap guard (`DataLoader:Vulcan` SQL app-lock) is provided by the platform —
  safe to over-schedule.

## 7. Database (`sql/Vulcan/`, schema `dbo`)

Five data tables, five TVP types, five `usp_BulkMerge<Table>` procs, plus one
shared **`dbo.LoadWatermark(TableName PK, WatermarkValue date, UpdatedAtUtc)`**
table and a `usp_GetVulcanWatermark`/`usp_SetVulcanWatermark` accessor (or the
merge proc updates it inline). Each merge proc MERGEs on the table's natural key
and advances the watermark to `MAX(<watermarkcol>)` of the merged rows:

| Table | Natural key (MERGE) | Dedup at source? |
|-------|---------------------|------------------|
| `dbo.UnderConstruction` | `synmax_id` | no |
| `dbo.DataCenters` | `synmax_id` | yes (`modified_at`) |
| `dbo.LngProjects` | `plant_name` + `phase_number` | no |
| `dbo.ProjectRankings` | `synmax_id` | yes (`date_updated`) |
| `dbo.MetadataHistory` | `synmax_id` | yes (`date_eia_updated`) |

**Column types** derived from the documented field lists, e.g.:
- capacities (`nameplate_capacity`, `unit_capacity`) → `decimal(18,4)` / `float`
- dates (`date_vulcan_*`, `modified_at`, `date_updated`, etc.) → `date`
- `latitude`/`longitude` → `float`
- ids/codes/names/status → `nvarchar`
- `final_rank`/`project_rank` → `decimal(4,2)`
- `trains`, `phase_number` → `int`

Every row carries audit columns `LoadedAtUtc` and `RunId`. Scripts numbered like
other loaders: `001_CreateVulcanSchema.sql` (data tables + `LoadWatermark`),
`002_CreateVulcanTvpTypes.sql`, `003_CreateVulcanProcedures.sql` (merge procs +
watermark accessors). Exact column DDL is the `DATABASE_DEVELOPER` stage's
deliverable; the field lists in the API docs are the input.

## 8. Error handling & resilience

- Per-table isolation: one table failing does not fail the others; each result
  is a separate `core.LoadLog` row. The aggregate loader run succeeds only if
  all enabled tables succeed.
- HTTP transient retry via the existing `RetryPolicyFactory` Polly policy.
- Per-work-unit timeout from `WorkUnitTimeoutSeconds`.
- API key never logged; request URI sanitized in logs (base class already
  strips query strings).

## 9. Testing

Via `CODE_TESTER` with HTTP/SQL doubles:
- Fake `HttpMessageHandler` returns canned Vulcan JSON → assert each typed row
  model deserializes correctly.
- Assert each dedup query string is well-formed and includes the `ROW_NUMBER()`
  filter for the three tables that need it.
- Assert each sink's TVP `DataTable` column set/order matches its proc's TVP type.
- SQL verified against a test database or SQL fake.

## 10. Build steps (maps to CLAUDE.md "Adding a New Loader")

1. `src/DataLoader.Vulcan/DataLoader.Vulcan.csproj` → refs `DataLoader.Core`.
2. `VulcanSettings : LoaderSettingsBase` (section `Loaders:Vulcan`).
3. `VulcanWorkUnit : WorkUnit` (stable per-table+date key).
4. Shared `VulcanQuerySourceReader<TRow>` (POST + SQL body) and
   `VulcanWorkUnitProvider<TRow>`.
5. `IdentityTransformer<TRow>` registered per table.
6. Five row models + five `SqlSinkBase<TRow>` subclasses.
7. Five closed `LoaderPipelineBase<VulcanWorkUnit, TRow, TRow>` pipelines.
8. `VulcanModule : ILoaderModule` (registers all, `RunAsync` fans out + aggregates).
9. `sql/Vulcan/` scripts (schema, TVPs, merge procs).
10. `<ProjectReference>` from `DataLoader.Host` → `DataLoader.Vulcan`.
11. `Loaders:Vulcan` config + add `"Vulcan"` to `Platform:EnabledLoaders`.

## 11. Open items / risks

1. **JSON response envelope** — must be confirmed against a live response
   (see §4). Highest-risk unknown.
2. **Exact SQL column types** — drafted from doc field lists; finalized by
   `DATABASE_DEVELOPER`.
3. **API key / access** — a valid `Access-Key` is required to run and to verify
   the response shape and end-to-end load.
4. **`under_construction` watermark fidelity** — `date_image` is a freshness
   proxy, not a true modified timestamp; an edit that doesn't move the image date
   could be missed by the incremental filter. Acceptable per current scope; a
   periodic full reconcile would close the gap if needed.
5. **Source deletions not detected** — incremental (and merge-only) loads never
   remove rows deleted at the source. Out of scope unless delete reconciliation
   is requested later.
