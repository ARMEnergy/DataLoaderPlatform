# loader-routing

Purpose: Select the lowest-cost workflow that still satisfies correctness and quality gates.

## Trigger
Use at task start for any request touching loaders, SQL scripts, loader docs, or loader tests.

## Inputs
- Requested change summary.
- Files expected to change.
- Whether endpoint contract changes.
- Whether DB schema/TVP/proc signatures change.

## Decision
Choose exactly one route:
- Fast lane: code-only or test-only changes without API shape or schema changes.
- Full lane: new loader, new endpoint fields, schema/TVP/proc/key changes, or cross-stage chain updates.

## Fast lane sequence
1. CODER
2. CODE_REVIEWER
3. CODE_TESTER

## Full lane sequence
1. API_DOCUMENTATION_EXPERT (only when API contract changes)
2. APPLICATION_DESIGNER (only when flow changes)
3. DATABASE_DEVELOPER (when schema/TVP/proc changes)
4. CODER
5. CODE_REVIEWER
6. CODE_TESTER
7. DATA_QUALITY_VALIDATOR (only with live loaded DB)

## Output template
- Route: Fast lane or Full lane
- Why: one sentence
- Stages to run
- Stages skipped and reasons

## Escalation rules
Escalate from Fast lane to Full lane if any of these are discovered mid-run:
- New/changed API fields.
- Table/TVP/proc signature/key change.
- Resume-key or status-matrix semantics changed.
- Review finds cross-stage contract drift.
