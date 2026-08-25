# scoped-test-policy

Purpose: Minimize test cost while preserving confidence.

## Trigger
Run before choosing test command(s).

## Inputs
- Changed files list.
- Affected loader(s).
- Whether DataLoader.Core or DataLoader.Host changed.

## Policy
Default: run loader-scoped tests first.

Run solution-wide tests only when:
- src/DataLoader.Core/** changed, or
- src/DataLoader.Host/** changed, or
- shared conventions/contracts were changed across loaders.

## Commands
- Scoped: dotnet test tests/DataLoader.<Vendor>.Tests/DataLoader.<Vendor>.Tests.csproj -c Release
- Full: dotnet test DataLoaderPlatform.sln -c Release

## Output template
- Selected scope: scoped or full
- Why selected
- Command run
- Pass/fail counts

## Escalation rules
If scoped tests fail in shared code paths, escalate to full solution test run.
