# CLAUDE.md

This file provides guidance to Claude Code when working in this repository.

## Build and Run

```bash
# Build entire solution
dotnet build DataLoaderPlatform.sln -c Release

# Publish deployable artifacts
dotnet publish src/DataLoader.Host/DataLoader.Host.csproj -c Release -o publish

# Run a specific loader
cd src/DataLoader.Host/bin/Release/net8.0
DataLoader.Host.exe EnergyAspects

# Run all enabled loaders
DataLoader.Host.exe
```

Exit codes: `0` success or overlap-skip, `1` error, `2` cancelled.

## Non-negotiable quality gates

- TVP contract must match by NAME + ORDER + TYPE across DataTable and TVP.
- Resume-key semantics must be correct for settled and hot zones.
- Status matrix must distinguish legitimate empty reads from malformed requests.
- Reviewer and tester gates must pass before completion.
- Secrets must use `SEE_DB` or environment overrides and must never be logged.
- SQL changes must be parse-checked; build/test do not compile SQL.

## Architecture summary

- Plugin-based ETL: `DataLoader.<Vendor>` -> `DataLoader.Core` <- `DataLoader.Host`.
- One module per loader: `ILoaderModule`.
- Standard pipeline via `LoaderPipelineBase<TUnit, TItem, TRow>`.
- `SqlSinkBase<T>` handles TVP writes and acquires `SqlWriteGate`.
- Overlap is guarded by SQL app lock per loader.

## Routing policy (default)

Always start with `.claude/skills/loader-routing/SKILL.md`.

### Fast lane
Use for routine changes that do NOT alter endpoint contract or DB shape.

Flow:
1. CODER
2. CODE_REVIEWER
3. CODE_TESTER

### Full lane
Use for new loaders or cross-stage contract changes.

Flow:
1. API_DOCUMENTATION_EXPERT (only if API contract changes)
2. APPLICATION_DESIGNER (only if flow changes)
3. DATABASE_DEVELOPER (only if schema/TVP/proc/key changes)
4. CODER
5. CODE_REVIEWER
6. CODE_TESTER
7. DATA_QUALITY_VALIDATOR (only with live loaded DB)

### Escalation
Escalate from fast lane to full lane if any schema/API/key contract change is discovered mid-run.

## Test execution policy

Always apply `.claude/skills/scoped-test-policy/SKILL.md`.

Default command:
```bash
dotnet test tests/DataLoader.<Vendor>.Tests/DataLoader.<Vendor>.Tests.csproj -c Release
```

Escalate to solution-wide tests only when:
- `src/DataLoader.Core/**` changed, or
- `src/DataLoader.Host/**` changed, or
- shared contracts changed across loaders.

Solution-wide command:
```bash
dotnet test DataLoaderPlatform.sln -c Release
```

## SQL safety rule

For SQL changes, parse-check scripts with ScriptDom before reporting done.
Green build/test does not validate SQL syntax.

## Skills

Project skills are under `.claude/skills/`:
- `loader-routing`
- `tvp-contract-check`
- `scoped-test-policy`
- `compact-stage-reporting`
- `resume-key-and-status-matrix-check`

Use relevant skills on demand; do not load unrelated guidance.

## Workflows

Workflow docs are under `docs/copilot/workflows/`:
- `fast-lane-workflow.md`
- `full-lane-workflow.md`
- `review-fix-test-loop-workflow.md`

Pick one workflow at task start and report the choice.

## Reporting format

Use `.claude/skills/compact-stage-reporting/SKILL.md`.
Return only:
- Files changed
- Key decisions
- Risks/open questions
- Verification results

Avoid large inline dumps of code, SQL, or logs.

## Build-only loaders

CWG, AGSI, IHSPointLogic, IIR, NGI, ModernCommodities, EvolutionMarkets, Argus, ICE, Criterion, EOX, CME,
Genscape, NGX, Marex are build-only unless explicitly deployed and run.
Do not report data validation as passed when no live loaded database exists.

Criterion is the only loader with a **relational (PostgreSQL) source**. Its read path is verified
against live production; its SQL has never been deployed.

Genscape (oil fundamentals, DB `Genscape`) is verified against the live API end to end; its SQL
has never been deployed. Three of its behaviours are silent if you get them wrong: `endDate` is
**exclusive**, responses are **capped at 5,000 rows** with no indication (oldest dropped), and the
crude-storage endpoint's `week` is a week-of-**month**, so the loader derives `Year`/`Week` from
`ReportDate` in C# — never with `DATEPART` in SQL, because `Week` is in the primary key and
`DATEPART(week, …)` follows the session's `DATEFIRST`. See `docs/apis/Genscape.md`.

EOX's read path is verified end to end against the live FTP drop (25 files, 2011-2026, parsed
through the real reader); its SQL has never been deployed.

CME is the only loader whose source is a **fixed-width report**, and the only one whose SFTP
server rejects absolute paths (`/dir` fails, `./dir` works). Its read path is verified against
16 live bulletins (757,360 rows, 8 feeds); its SQL has never been deployed. BALMO/day-label
futures rows are **deliberately not loaded** — see `sql/CME/001` for why and what it costs.

NGX (ICE NGX clearing, DB `NGX`) is the only loader whose target database **already holds live
incumbent tables** (`dbo.IndexPrice`, `dbo.StripTradingSummary`, still written by Conduit today);
the new `arm.*` tables shadow them, and the loader reads `dbo.[Index]` but never writes `dbo`.
Its read path is verified live end to end; its SQL has never been deployed. Four behaviours are
silent if you get them wrong: auth is **HTTP Basic**, not the documented `/api/v2` bearer token
(which these `.xml` paths ignore, answering 302 to ICE SSO — so redirects must never be
followed); at most **10 `indexId` params** per request, and **one unentitled id returns 403 for
the whole batch**; the index response **truncates at 50 rows** unless `pageSize` is sent, and the
paging param is `page` (`pageNumber` and `size` are silently ignored); and every timestamp must
be converted from the vendor's Mountain offset to **US Central**, which matters doubly because
`TradeDateTime` is in the primary key. Amounts also carry thousands separators (`313,100`).
See `docs/apis/NGX.md`.

Marex (Neon crude market, DB `Marex`) is the only loader whose source **pushes rather than
answers**, and the only one built on a **vendor SDK** — three .NET Framework 4.6.1 assemblies
vendored in `lib/neon` (not on any NuGet feed). They run fine in-process on .NET 8; the vendor's
own integration note says to build a separate net48 connector, and that is **not** necessary —
see `lib/neon/README.md`. There is no request that fetches data: the client authenticates
(Auth0 password grant), opens a SignalR websocket, and the gateway pushes one opening snapshot of
every entity. So a run is connect once → capture → merge five tables → disconnect; the live
`*Update` delta stream is deliberately not consumed, because that would need a resident process
and this platform runs loaders as batch jobs under an overlap lock. Its read path is verified
live end to end; its SQL has never been deployed. Four behaviours are silent if you get them
wrong, and three of them contradict the vendor's own note: `NeonApiConfig.Name` is the SignalR
**hub name** (`Gateway.Crude`, supplied by the SDK when null) and NOT a client label — a wrong
value makes the gateway answer the negotiate with **HTTP 500** while the SDK retries every 5s
forever and the status sits on `Connecting`; handlers must be attached **before** `Connect` and
the run must wait on the **snapshots**, not on `ConnectionStatus == Connected`, which is reached
*after* they arrive; `PasswordConnectionData` **does** carry `Domain` and `ClientId` (the note
says it does not), so `GetToken` works and hand-rolling the Auth0 call is unnecessary; and
`ClosingPriceDto.Time`/`.PreviousTime` are **non-nullable**, so "no value" arrives as
`0001-01-01`, which `DATETIME2` stores silently. `ExchangeDate`/`TradeDate` exist in no DTO —
they come only from the separate `ExchangeDateSnapshot` event and lead both fact tables' primary
keys. Also note `MarketState` lives in `SignalRClient.Contracts`, not `Common.Core.Enums`.
See `docs/apis/Marex.md`.

## Key files

- `src/DataLoader.Core/Abstractions/`
- `src/DataLoader.Core/Pipeline/LoaderPipelineBase.cs`
- `src/DataLoader.Core/Sinks/SqlSinkBase.cs`
- `src/DataLoader.Core/Concurrency/SqlWriteGate.cs`
- `src/DataLoader.Core/Persistence/SqlLoaderOverlapGuard.cs`
- `src/DataLoader.Host/Program.cs`
- `sql/Core/`
- `docs/apis/`
- `docs/design/`
- `tests/`

## Configuration reminders

- Loader settings live in `src/DataLoader.Host/appsettings.json`.
- Sensitive settings default to `SEE_DB` and resolve from `core.Param`.
- Use `AddLoaderSettings<TSettings>` for correct `SEE_DB` resolution.
