# Full Lane Workflow

Use for new loaders and cross-stage changes.

## Entry criteria
- New loader build, or
- Endpoint contract change, or
- Schema/TVP/proc/key changes.

## Sequence
1. Run loader-routing skill and confirm Full lane.
2. API_DOCUMENTATION_EXPERT (if API contract changed).
3. APPLICATION_DESIGNER (if flow changed).
4. DATABASE_DEVELOPER (if schema/TVP/proc changed).
5. CODER implements.
6. CODE_REVIEWER validates.
7. CODE_TESTER validates.
8. DATA_QUALITY_VALIDATOR only when live loaded DB exists.

## Mandatory checks
- SQL parse-check when SQL changes.
- TVP contract check passes.
- Resume-key and status-matrix checks pass.
- Build and required tests pass.

## Exit criteria
- Required stages complete or explicitly skipped with reason.
- Reviewer/tester clear.
- Stage evidence includes build/test/parse-check results.

## Reporting format
Use compact-stage-reporting for each stage handoff.
