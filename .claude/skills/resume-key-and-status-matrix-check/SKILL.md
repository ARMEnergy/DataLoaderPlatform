# resume-key-and-status-matrix-check

Purpose: Catch high-impact logic bugs in idempotency and HTTP status classification.

## Trigger
Run for any change touching work-unit keys, date windows, or HTTP response handling.

## Inputs
- WorkUnit key format logic.
- DaysBack/SettledAfterDays settings.
- Source reader status handling and retry/auth behavior.

## Checks
1. Settled keys are stable; hot keys vary at intended cadence.
2. Legitimate empty responses are modeled as success where required.
3. Malformed request statuses throw.
4. Auth status handling is compatible with retry/auth handler design.
5. Window/helper behavior is consistent between provider and validator.

## Output template
- Key behavior: pass/fail
- Status matrix: pass/fail
- Risks if config tightens
- Required tests to pin behavior

## Escalation rules
Any ambiguity in vendor status semantics escalates to API_DOCUMENTATION_EXPERT.
