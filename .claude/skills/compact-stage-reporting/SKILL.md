# compact-stage-reporting

Purpose: Keep stage outputs short and actionable to reduce token growth in loops.

## Trigger
Use for all stage completions and handoffs.

## Required format
Return only:
1. Files changed
2. Key decisions
3. Risks/open questions
4. Verification results (build/test/parse-check)

## Limits
- Stage summary target: 5 to 10 lines.
- No full source or SQL paste.
- No full test log paste; include only counts and failing test names.

## Output template
- Stage:
- Files:
- Decisions:
- Risks:
- Verification:

## Escalation rules
If user asks for deeper detail, provide targeted excerpts only for requested files or failures.
