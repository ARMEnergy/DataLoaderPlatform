# Claude Token Usage Optimization Playbook

Date: 2026-08-24
Scope: Reduce token usage in Claude Code workflows after a loader is finished.
Status: Ready to execute.

## Why this exists
Current agent instructions are very thorough but expensive. The cost is mostly from:
- Mandatory multi-stage routing for many tasks.
- Re-reading long global guidance on each stage.
- Re-reading large API/design documents across agents.
- Repeated loop stages (review/test) with broad context.

This playbook keeps quality gates and reduces context overhead.

## Expected impact
- New loader work: 45% to 65% lower token usage.
- Existing loader changes: 60% to 80% lower token usage.
- Review/test fix loops: 30% to 50% lower incremental usage.

With skills and workflow additions in this playbook, expect:
- An additional 10% to 25% token reduction from better routing discipline and smaller stage outputs.
- 15% to 35% faster cycle time on routine loader changes.
- Fewer review/test retries due to earlier, reusable contract checks.

## Guardrails that must NOT be relaxed
Keep these as hard requirements:
- TVP contract checks (name/order/type) remain mandatory.
- Resume-key correctness remains mandatory.
- Status-matrix correctness (no-data vs malformed request) remains mandatory.
- Reviewer and tester gates remain required before complete.
- Secrets handling and safe logging remain required.

## Skills and workflow additions (new)
These additions do not replace the original optimization plan. They enforce it.

### Skills to add
Create these project skills under `.claude/skills/`:
- `loader-routing/` -> route fast lane vs full lane using explicit criteria.
- `tvp-contract-check/` -> validate TVP/DataTable name-order-type contract before review.
- `scoped-test-policy/` -> select loader-scoped vs solution-wide tests based on changed files.
- `compact-stage-reporting/` -> enforce short stage outputs (changed files, decisions, risks, pass/fail).
- `resume-key-and-status-matrix-check/` -> verify hot/settled key behavior and no-data vs malformed status handling.

Create a `SKILL.md` in each folder with:
- Trigger conditions.
- Required inputs.
- Step-by-step checks.
- Output template.
- Escalation rules.

### Workflow definitions to add
Create workflow docs under `docs/copilot/workflows/`:
- `fast-lane-workflow.md`
- `full-lane-workflow.md`
- `review-fix-test-loop-workflow.md`

Each workflow document should define:
- Entry criteria.
- Stage sequence.
- Mandatory checks.
- Exit criteria.
- When to escalate to full lane.

### Why this helps
- Skills load on demand, so broad guidance is not injected into every stage.
- Workflow entry criteria reduce unnecessary manager-first/full-chain runs.
- Reusable checks catch common defects earlier, reducing loop count.
- Compact output templates reduce token growth per stage.

## Implementation sequence
Apply in this order. Do not skip steps.

### Step 0: Add skills and workflow scaffolding (new)
Goal: Enforce routing and quality checks with targeted, reusable context.

Create folders:
- `.claude/skills/loader-routing/`
- `.claude/skills/tvp-contract-check/`
- `.claude/skills/scoped-test-policy/`
- `.claude/skills/compact-stage-reporting/`
- `.claude/skills/resume-key-and-status-matrix-check/`
- `docs/copilot/workflows/`

Create files:
- One `SKILL.md` per skill folder.
- `docs/copilot/workflows/fast-lane-workflow.md`
- `docs/copilot/workflows/full-lane-workflow.md`
- `docs/copilot/workflows/review-fix-test-loop-workflow.md`

Definition of done:
- All skill files include trigger, input, checks, output template, and escalation.
- Workflow files define entry/exit criteria and mandatory gates.

### Step 1: Add concise operating policy to CLAUDE.md
Goal: Replace default heavy routing with explicit fast lane vs full lane.

Edit target:
- CLAUDE.md

Action:
- In the Agents section, keep the specialist roster.
- Replace mandatory broad routing language with a simple route matrix:
  - Fast lane: coder -> reviewer -> tester for changes that do not modify API shape or DB schema.
  - Full lane: documentation -> design -> database -> coder -> reviewer -> tester (+ data validation only when live data exists) for net-new loaders or schema/API-shape changes.
- Add a short note that routing decisions should use the `loader-routing` skill.
- Add explicit skip rules:
  - Skip API docs stage if endpoint contract unchanged.
  - Skip design stage if execution flow unchanged.
  - Skip data validation for build-only loaders with no live-loaded DB.
- Keep quality-gate statements unchanged in strictness.

Definition of done:
- CLAUDE.md can be read top-down in under 3 minutes.
- Routing rules are unambiguous and do not require interpretation.

### Step 2: Convert each agent prompt to checklist style
Goal: Keep behavior but remove repeated narrative payload.

Edit targets:
- .claude/agents/manager.md
- .claude/agents/coder.md
- .claude/agents/database-developer.md
- .claude/agents/api-documentation-expert.md
- .claude/agents/application-designer.md
- .claude/agents/code-reviewer.md
- .claude/agents/code-tester.md
- .claude/agents/data-quality-validator.md

Action pattern for each file:
- Keep: role, scope, hard quality gates, expected output format.
- Remove/shorten: long historical examples, repeated rationale paragraphs, duplicated cross-loader explanations.
- Replace repeated rule prose with references to project skills where applicable.
- Add a strict output cap section:
  - Return only files changed, key decisions, risks, and pass/fail summary.
  - Do not paste full source, SQL, docs, or long test logs.

Definition of done:
- Each prompt is <= 80 lines if possible.
- No agent prompt repeats large explanatory text that already exists elsewhere.

### Step 3: Make test execution policy scoped by default
Goal: Avoid solution-wide testing unless needed.

Edit targets:
- .claude/agents/code-tester.md
- .claude/agents/coder.md

Action:
- Change default from solution-wide tests to loader-scoped tests first.
- Add a rule that scope selection is decided by the `scoped-test-policy` skill.
- Add escalation rule to run full solution tests only when:
  - DataLoader.Core changed, or
  - DataLoader.Host changed, or
  - shared conventions/contracts changed across loaders.
- Keep requirement to report real pass/fail counts.

Definition of done:
- Default test command references loader-specific test project first.
- Full-solution test run is conditional and clearly documented.

### Step 4: Add response-size discipline to manager and reviewer flows
Goal: Reduce tokens per loop stage.

Edit targets:
- .claude/agents/manager.md
- .claude/agents/code-reviewer.md

Action:
- Manager stage summaries should be 5-10 lines max per stage.
- Reviewer findings should be terse: file:line, severity, issue, fix.
- Explicitly forbid large code excerpts unless required to disambiguate a fix.
- Add a rule that output format follows the `compact-stage-reporting` skill template.

Definition of done:
- Multi-stage run summaries remain compact and actionable.

### Step 5: Keep large reference material as optional read, not default read
Goal: Prevent repeated ingestion of large text by every stage.

Edit targets:
- CLAUDE.md
- Any agent prompt that says to always read broad sections first.

Action:
- Change wording from "always read full section" to "consult only relevant section(s) for this task".
- Keep links/paths to references but avoid mandatory broad reads.
- For recurring checks, point to specific skills instead of re-embedding long examples.

Definition of done:
- Agents still know where references are.
- They are not instructed to ingest large unrelated content by default.

### Step 6: Add workflow execution rules and metrics (new)
Goal: Improve token usage, speed, and quality with measurable feedback.

Edit targets:
- CLAUDE.md
- docs/copilot/workflows/fast-lane-workflow.md
- docs/copilot/workflows/full-lane-workflow.md
- docs/copilot/workflows/review-fix-test-loop-workflow.md

Action:
- Add an explicit requirement to pick a workflow at task start.
- Require one-line justification when escalating from fast lane to full lane.
- Track metrics for each run:
  - total stages executed
  - review/test loop count
  - tests run scope (scoped vs solution-wide)
  - approximate prompt+response token usage
  - cycle time from first edit to green tests

Definition of done:
- Workflow choice is visible and reproducible.
- Team can compare before/after metrics over multiple tasks.

## Suggested route matrix (copy-ready)
Use this matrix language in CLAUDE.md.

- Fast lane (default):
  - Use for bug fixes and enhancements that do not change endpoint contract, table shape, TVP shape, merge key, or proc signature.
  - Flow: CODER -> CODE_REVIEWER -> CODE_TESTER.
- Full lane (conditional):
  - Use for new loader creation, endpoint/schema shape changes, or column-chain changes.
  - Flow: API_DOCUMENTATION_EXPERT -> APPLICATION_DESIGNER -> DATABASE_DEVELOPER -> CODER -> CODE_REVIEWER -> CODE_TESTER -> DATA_QUALITY_VALIDATOR (only if live-loaded DB exists).
- Skip policy:
  - Skip non-applicable stages and state reason explicitly.

Add this enforcement line:
- Workflow selector: use `loader-routing` skill before stage selection.

## Verification checklist (after edits)
Run these checks after applying the instruction updates.

1) File size check
- Confirm CLAUDE.md is significantly smaller than before.
- Confirm each agent file is materially shorter.

2) Policy check
- Confirm fast lane and full lane rules are present.
- Confirm quality gates are still strict.

3) Dry-run behavior check
- Ask Claude to plan a tiny existing-loader bug fix.
- Expected: it chooses fast lane and does not require full chain.

4) Full-lane behavior check
- Ask Claude to plan an endpoint field addition.
- Expected: it chooses full lane and preserves all required gates.

5) Skill invocation check
- Run one routine bugfix prompt.
- Expected: `loader-routing`, `scoped-test-policy`, and `compact-stage-reporting` are used.

6) Speed and quality check
- Compare 3 tasks before vs after rollout.
- Track cycle time, loop count, and defect escapes.

7) Token check
- Compare approximate token consumption for equivalent tasks.
- Expected: reduction in baseline and loop-stage growth.

## Rollback plan
If behavior quality drops:
- Revert only the specific prompt file that caused the regression.
- Keep the new route matrix in CLAUDE.md.
- Re-introduce only the minimum needed guidance in that one agent.

## Ownership
- Primary owner: repository maintainer.
- Best time to apply: immediately after current loader work is complete.

## One-session execution checklist
Use this quick checklist while editing:
- [ ] Create skills under `.claude/skills/` and workflow docs under `docs/copilot/workflows/`.
- [ ] Update CLAUDE.md route matrix and skip policy.
- [ ] Shorten all 8 agent prompt files to checklist format.
- [ ] Update tester/coder test-scope defaults.
- [ ] Add output-size discipline to manager/reviewer.
- [ ] Add workflow selector and escalation rules.
- [ ] Add run metrics for token/speed/quality tracking.
- [ ] Validate with two dry-run prompts (fast lane and full lane).
- [ ] Commit as a single "agent-instructions optimization" change.
