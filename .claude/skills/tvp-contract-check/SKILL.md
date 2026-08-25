# tvp-contract-check

Purpose: Prevent silent data corruption from TVP/DataTable drift.

## Trigger
Run before marking code review complete for any sink or SQL TVP/proc change.

## Inputs
- Sink BuildTable definition.
- sql/<Vendor>/002 TVP type.
- sql/<Vendor>/003 proc signature and merge columns.

## Checks
1. DataTable and TVP columns match by name, order, and type.
2. Scalars such as RunDate are proc params, not TVP columns.
3. Disallowed TVP columns are absent (ModifiedAtUtc, DateCreated, computed columns).
4. Merge SELECT/INSERT/UPDATE column chain matches table and TVP.

## Output template
- Contract status: pass/fail
- Mismatches (file + column + expected vs actual)
- Required fixes

## Escalation rules
- Any mismatch is Blocking.
- Route back to CODER and/or DATABASE_DEVELOPER until pass.
