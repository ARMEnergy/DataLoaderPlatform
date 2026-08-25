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

CWG, AGSI, IHSPointLogic, IIR, NGI, ModernCommodities are build-only unless explicitly deployed and run.
Do not report data validation as passed when no live loaded database exists.

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
