# Fast Lane Workflow

Use for routine loader changes that do not alter endpoint contract or DB shape.

## Entry criteria
- No new endpoint fields.
- No table/TVP/proc signature changes.
- No merge-key or resume-key semantic changes.

## Sequence
1. Run loader-routing skill and confirm Fast lane.
2. CODER implements change.
3. CODE_REVIEWER reviews with tvp-contract-check and resume-key/status checks as applicable.
4. CODE_TESTER runs scoped-test-policy.

## Mandatory checks
- Build succeeds.
- Relevant scoped tests pass.
- Any contract checks triggered by changed files are explicitly reported.

## Exit criteria
- Reviewer clean.
- Tests green.
- Compact stage summary produced.

## Escalate to full lane when
- A schema/API shape change is discovered.
- Reviewer identifies cross-stage contract drift.
- Scoped tests indicate shared-system impact.
