---
description: Publish DataLoader.Host to the production share, archiving whatever is already there into Old\<timestamp> first
argument-hint: "[--dry-run] [--skip-tests] [--keep-prod-settings] [--overwrite-settings]"
---

# publish-to-prod

Thin wrapper over `scripts\Publish-ToProd.ps1`, which holds all the logic. **Do not
reimplement the steps here** — the script is the single source of truth, is unit-tested
against sandbox targets, and is what runs unattended from a scheduler or a plain shell.

**Target:** `\\armh-opsdb01\DataLoaderPlatform`
**Archive:** `\\armh-opsdb01\DataLoaderPlatform\Old\yyyy_MM_dd_HH_mm_ss`

## What to run

Map `$ARGUMENTS` onto the script's switches and run it:

| Argument | Script switch |
|---|---|
| `--dry-run` | `-DryRun` |
| `--skip-tests` | `-SkipTests` |
| `--keep-prod-settings` | `-KeepProdSettings` |
| `--overwrite-settings` | `-OverwriteSettings` |
| *(none)* | *(no switches — full pipeline with all gates)* |

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-ToProd.ps1 <switches>
```

## Your job around it

1. **Deploying is outward-facing.** Run this only when the user has asked for it in this
   turn. Never chain it onto the end of unrelated work.
2. **Relay the script's own output** — it already reports gates, archive path, file count
   and rollback. Don't re-verify by hand or paraphrase it into something vaguer.
3. **If it exits non-zero, stop and report.** Do not retry, do not pass
   `-OverwriteSettings` or `-SkipTests` to get past a gate the user didn't ask you to
   bypass. The four ways it fails on purpose:
   - **no write access to the share** → an environment/permissions problem, not
     something to work around. Report the account name the script printed and stop;
     someone has to grant it write permission on the share. This is checked before
     anything is archived, so the target is always left untouched.
   - **red tests** → fix them, or the user explicitly asks for `--skip-tests`
   - **`appsettings.json` differs** → the script lists the differing setting *names*.
     Show them and ask which the user wants: `--keep-prod-settings` (deploy binaries,
     keep production's config — usually right) or `--overwrite-settings`.
   - **a locked file mid-archive** → something is using the folder. The script stops
     before overwriting anything and names the entry; report it and let the user clear it.
4. **Never print a config value.** The script deliberately reports setting *names* only,
   because `appsettings.json` can hold live credentials. Don't go read the file and helpfully
   paste the difference.
