# Review-Fix-Test Loop Workflow

Use when review or tests find issues.

## Sequence
1. Capture findings in compact format.
2. Route fixes to CODER.
3. Re-run only affected checks/tests first.
4. Escalate to broader testing if failures imply shared impact.
5. Repeat until clean.

## Loop discipline
- Keep each loop summary to files, issue list, fix list, and verification.
- Avoid re-pasting unchanged context.
- Track loop count and root-cause category.

## Mandatory loop metrics
- Loop number
- Files changed
- Tests run (scoped or solution-wide)
- Pass/fail counts
- Remaining blockers

## Exit criteria
- No blocking findings.
- Required tests pass.
- Final compact summary includes what changed and why.
