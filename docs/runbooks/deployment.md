# Deployment runbook: zero-downtime releases

This runbook is for whoever is on call for `DataLoaderPlatform` on
`ARMH-OPSDB01`, whether or not they built this mechanism. It covers: the
one-time migration, the normal one-command release (`scripts\Release.ps1`),
the manual two-machine fallback it wraps, a rollback, and what to do when any
step fails.

🔒 Everything in this document that writes to `ARMH-OPSDB01` is a production
write. Read the standing rule at the top of `CLAUDE.md` before running any of
these commands against the real host.

## Why this exists

The old deploy process copied new binaries straight over
`C:\ETL_Tools\DataLoaderPlatform\Loader\`. Windows locks a running `.exe` and
its loaded `.dll`s for the life of the process, so any loader mid-run got
killed by that copy — measured twice: 2026-10-13 (Criterion, ICE, Platts) and
2026-10-27 (Criterion, ICE). ICE runs every 30 minutes and can take 30, so
there is no reliable quiet window to deploy around. Each kill also leaves an
orphaned `core.LoaderRun` row that nothing closes, which the watchdog then
reports hourly for a week.

The fix: never overwrite a folder a process might be running from. Each
release is copied into a brand-new folder; going live is a matter of changing
which folder name one pointer file names. A run that started before a deploy
keeps running out of the old folder, untouched, until it finishes normally.

## The normal way: `Release.ps1`

`scripts\Release.ps1` is the one-command release path, run entirely from a
dev machine (the laptop that has the .NET SDK). It does not replace
`Deploy-Release.ps1` — it drives it, unchanged, on the server over PowerShell
remoting (`Invoke-Command -ComputerName ARMH-OPSDB01`, under the caller's own
Windows identity — no stored credential anywhere in it), so the smoke test
still runs where it has to in order to mean anything: on the server itself.

```powershell
.\scripts\Release.ps1 -DryRun      # rehearse preflight + build + test + publish; touches no server
.\scripts\Release.ps1              # full pipeline, one confirmation prompt before anything goes to the server
.\scripts\Release.ps1 -Force       # same, no confirmation prompt
.\scripts\Release.ps1 -Rollback    # flip the server back to the previous release; no build/test/publish
.\scripts\Release.ps1 -SelfTest    # run its own test suite against throwaway temp state; touches no real server
```

What it does, in order (failing closed at every step — see the script's own
comment-based help for the full detail and the two deliberate design
decisions called out there: why file transfer goes over a one-off
`New-PSSession`/`Copy-Item -ToSession` rather than the admin share, and why
`-Rollback` skips the git-based half of preflight):

1. **Preflight.** Local `master` branch, in sync with `origin/master`, no
   uncommitted *source* changes (the repo's own tracked `bin\`/`obj\`/
   `publish\` build churn is deliberately excluded from that check — see the
   script's `Test-BuildArtifactPath`); `-Server` reachable over remoting;
   `Releases\`/`current.txt` present there (migration done).
2. **Build** `dotnet build DataLoaderPlatform.sln -c Release`.
3. **Test** `dotnet test DataLoaderPlatform.sln -c Release` — reports the
   totals it actually observed.
4. **Publish** to `-PublishDir` (default `.\publish-staging`), emptied first.
5. **Sync** this repo's whole `scripts\` folder onto the server, every run —
   `Deploy-Release.ps1` and the launcher template are copies on the server,
   not links, so this is what keeps the server from ever running a stale
   version of them.
6. **Copy** the publish output to `<PlatformRoot>\_staging` on the server
   (emptied first, same reasoning as step 4).
7. **Confirm** — current release → proposed action, one prompt (`-Force`
   skips it; `-DryRun` never reaches it).
8. **Deploy remotely** — `Deploy-Release.ps1 -PublishDir <that _staging
   path>` runs ON the server; its console output is relayed back verbatim. A
   non-zero exit stops here, and the script actively re-checks (not just
   assumes) that `current.txt` was not changed.
9. **Verify** — reads `current.txt` back *over remoting* and asserts it
   changed to the expected new release. It does not wait for a scheduled
   loader run to prove anything — that was an explicit decision.

`-DryRun` runs preflight/build/test/publish for real (those only ever touch
the local working tree and `-PublishDir`) but makes no remote call that
writes anything — no sync, no copy, no `Deploy-Release.ps1` invocation — and
never prompts.

## How this relates to `Publish-ToProd.ps1` and `Deploy-Release.ps1`

Three tools, three different jobs. **None of them replaces another**, and
neither `Publish-ToProd.ps1` nor `Deploy-Release.ps1` was changed by adding
`Release.ps1`.

| | `Publish-ToProd.ps1` | `Deploy-Release.ps1` | `Release.ps1` |
|---|---|---|---|
| Runs from | a dev machine | **on `ARMH-OPSDB01` itself** | a dev machine (drives the server over remoting) |
| Target | `\\armh-opsdb01\DataLoaderPlatform` (== `PlatformRoot\Src\`, a publish **drop**) | `PlatformRoot` directly (`Releases\`, `current.txt`) — where jobs actually run | the same, via `Deploy-Release.ps1` on the server |
| Builds/tests first | yes (`dotnet build`/`test`/`publish`) | no — it deploys whatever `-PublishDir` already contains | yes, then hands the result to `Deploy-Release.ps1` |
| Downtime behavior | overwrites `Src\` in place; copying `Src\` → `Loader\` by hand is the old, manual, downtime-causing step this whole mechanism replaced | zero-downtime: new folder, smoke test, then a one-file pointer flip | same as `Deploy-Release.ps1` — it is the thing actually doing the flip |

### Manual fallback (two machines)

If PS remoting to the server is down, or `Release.ps1` itself cannot be used
for some other reason, the flow it automates can still be done by hand:

1. On a dev machine: `dotnet publish src/DataLoader.Host/DataLoader.Host.csproj -c Release -o publish` (or `Publish-ToProd.ps1`, which also refreshes the `Src\` drop on the share — optional, mainly useful as an audit trail/backup of what was built).
2. Get that publish output onto `ARMH-OPSDB01` (copy it over, or point `-PublishDir` at the `Src\` share path if running remotely — but **run `Deploy-Release.ps1` itself on the host**, not remotely, since its default `-PlatformRoot` is a local path and its smoke test runs a local process).
3. On `ARMH-OPSDB01`: `.\scripts\Deploy-Release.ps1 -PublishDir <path to that publish output>`.

## One-time migration

Run exactly once, before `Deploy-Release.ps1` is used for the first time.
`scripts\Migrate-ToReleases.ps1`:

1. Copies `Loader\`'s current contents into a new `Releases\<timestamp>_initial\`
   folder (skipping `Old\` and `files\` — the 14.4 GB of deploy archives and
   ICE's download cache, neither of which belongs in a release).
2. Writes `current.txt` to point at that folder.
3. Regenerates every `<Loader>.cmd` launcher (plus `Watchdog.cmd`) from the
   checked-in template so they read `current.txt` instead of always running
   out of `Loader\`.
4. Leaves `Loader\` exactly as it was, as a fallback.

```powershell
# From a dev machine or an RDP session on ARMH-OPSDB01:
.\scripts\Migrate-ToReleases.ps1 -DryRun          # see exactly what it would do
.\scripts\Migrate-ToReleases.ps1                  # run it for real
```

### ⚠ Step 3 needs a quiet window

Rewriting a `.cmd` file is a plain file copy, not an atomic operation.
**Before running this for real** (the `-DryRun` is safe to run any time):

1. Open Task Scheduler on `ARMH-OPSDB01`.
2. Disable every task under `\DLP\`.
3. Run `.\scripts\Migrate-ToReleases.ps1` for real. It prints the same warning
   loudly before step 3, in case this gets skipped.
4. Once it reports `MIGRATION COMPLETE`, re-enable the `\DLP\` tasks.

This is the only step in the whole deployment mechanism — migration, a normal
release, or a rollback — that is not already safe to run while jobs are live.
Everything after migration is designed specifically so this is never needed
again.

### After migration

- `Loader\` is left in place, untouched. Keep it around for a few days as a
  fallback while the new layout proves itself, then remove it by hand — the
  script prints the exact command at the end.
- The target layout is now:

  ```
  C:\ETL_Tools\DataLoaderPlatform\
      current.txt            one line: the active release folder name
      previous.txt           one line: the release active before the last deploy (written by Deploy-Release.ps1; absent until the first deploy)
      Releases\
          2026_10_28_14_00_00_initial\    the migrated Loader\ contents
      files\                 ICE's download cache -- unchanged, still shared across releases
      Log\
      <Loader>.cmd           regenerated; reads current.txt now
      Watchdog.cmd
      Loader\                left in place as a fallback -- no longer used by any launcher
  ```

## A normal release

Run `scripts\Deploy-Release.ps1` **on `ARMH-OPSDB01`**, pointing `-PublishDir`
at a `dotnet publish` output:

```powershell
.\scripts\Deploy-Release.ps1 -DryRun -PublishDir <path>   # see what it would do first
.\scripts\Deploy-Release.ps1 -PublishDir <path>           # deploy for real
```

A bare `.\scripts\Deploy-Release.ps1` (no arguments) uses production's real
`-PlatformRoot` and this repo's own local `publish\` folder as `-PublishDir`
— correct if run from a checkout on the host with a build already published
there.

What happens, in order, failing closed at every step:

1. **Checks the layout.** Refuses clearly if `Releases\`/`current.txt` don't
   exist (migration not run) or if `current.txt` is corrupt.
2. **Creates `Releases\<yyyy_MM_dd_HH_mm_ss>\`** and copies `-PublishDir`
   into it. Nothing that already exists on disk is ever touched by this step.
3. **Smoke tests** the new release by running
   `.\DataLoader.Host.exe --watchdog` from inside it, before it is live. Exit
   0 or 1 both count as a pass (1 is the watchdog's own "could not deliver an
   alert" outcome — not a startup failure). A crash, a .NET startup error, or
   a hang is a real failure: `current.txt` is left untouched, nothing goes
   live, and the script exits non-zero. Skip with `-SkipSmokeTest` (not
   recommended — this is the gate that catches a build that cannot even
   start).
4. **Flips `current.txt`** to the new release, atomically. `previous.txt` is
   written in the same step.
5. **Prunes** old releases, keeping the newest `-KeepReleases` (default 5)
   plus the current and immediately-previous release always, regardless of
   count. A release folder a running process still has open is skipped, not
   deleted — that is the normal, expected case for a long-running loader
   still executing out of an older release.
6. **Prints a summary**: new release name, previous release name, smoke test
   result, how many old releases were pruned.

A deploy that fails at the smoke-test step leaves the bad release folder on
disk (for inspection — see `Log\Watchdog.log`-style diagnostics printed
inline in the failure message) and leaves production running the old release
the whole time. There is no partial/broken state for a scheduled task to hit.

## Rollback

```powershell
.\scripts\Deploy-Release.ps1 -Rollback -DryRun   # see what it would do
.\scripts\Deploy-Release.ps1 -Rollback           # flip back to the previous release
```

This rewrites `current.txt` to whatever `previous.txt` names and does
**nothing else** — no pruning, no changes to `previous.txt` itself. It
refuses if `previous.txt` doesn't exist (nothing recorded yet — e.g. right
after the migration, before any deploy has run) or if the release folder it
names has since been removed (manually, or pruned by a later deploy — prune
always protects the current *and* previous release, so this should only
happen from manual cleanup).

Rollback does not touch the `Releases\` folder itself — the release being
rolled back to must still be on disk. If it has been removed, redeploy that
build instead.

## When something fails

**Migration fails partway (step 1 or 2).** `Releases\` or `current.txt` may
exist without the other, or without launchers regenerated. The script's error
message says exactly what state was reached. Nothing in `Loader\` was
touched, so the OLD launchers (if not yet regenerated) still work exactly as
before — there is no outage. Fix the reported problem and re-run; re-running
`scripts\Generate-Launchers.ps1` directly is always safe (it is a verbatim
copy of the template, nothing more).

**A deploy's smoke test fails.** Production keeps running the old release;
nothing changed. Check the failure message (it includes the tail of the
watchdog's own stdout/stderr) and `Log\Watchdog.log` from the attempt. The bad
`Releases\<timestamp>\` folder is left on disk for inspection — remove it by
hand, or just leave it; the next successful deploy's prune step will reclaim
it once it ages out of the keep window (it is never "current" or "previous",
so it is never specially protected).

**A deploy's flip fails** (the atomic rename itself — see
`Set-CurrentRelease`'s retry budget in the script). This is extremely rare in
practice: it only fires after 20 retries against a file that stayed
unreadable/locked for the whole retry window, which real launcher reads
(a single open-read-close, sub-millisecond) should never produce. If it does
happen, `current.txt` is left exactly as it was before the attempt — rerun
the deploy.

**A launcher fails with "current.txt is missing or empty" or "current.txt
names ... but ... was not found".** Read `Log\<Loader>.log` for the exact
message. This means `current.txt` is genuinely missing, empty, or points at a
release folder that no longer exists — not a transient read (the launcher
retries a handful of times, immediately, before concluding this). Check
`current.txt`'s contents and that the named `Releases\<name>\` folder exists
with `DataLoader.Host.exe` in it. If it does not, the most likely fix is
`.\scripts\Deploy-Release.ps1 -Rollback` (if `previous.txt` still names a good
release) or a fresh `Deploy-Release.ps1` run.

**Prune skipped a release you expected to be removed.** It is still in use —
some process has a file in it open. This is reported as a normal line, not an
error (`skip (in use): Releases\...`). It will be pruned on a later deploy
once nothing holds it open; if it genuinely never finishes (a stuck process),
investigate that process first — do not manually delete a release folder
something might still be running from.

**You need to add a launcher for a brand-new loader.** Run
`scripts\Generate-Launchers.ps1 -LoaderNames <NewLoaderName>` — it is safe to
run any time, live, without a quiet window (unlike the one-time migration):
it only ever adds or overwrites `.cmd` files whose content never depends on
when they are written, and nothing currently running reads a `.cmd` file
mid-run (a scheduled task reads it once, at launch, each time it fires).

## Open question for the reviewer

`Deploy-Release.ps1` copies `-PublishDir` into each new release verbatim,
including `appsettings.json`, with **no drift gate** — unlike
`Publish-ToProd.ps1`, which refuses to silently overwrite a differing
`appsettings.json` on the target. This script instead prints a **non-blocking**
note (key names only, no values) when the new release's `appsettings.json`
differs from the release it is replacing, but it does not stop the deploy.
This was a deliberate choice to keep the deploy mechanism's contract exactly
to the six steps specified for it, rather than silently inventing a second
`-KeepProdSettings`/`-OverwriteSettings`-style gate that was never asked for —
but it means whoever runs a deploy is responsible for making sure
`-PublishDir`'s `appsettings.json` is the one they actually want live, same as
today. Flagging this explicitly rather than guessing whether a hard gate
belongs here too; if it should, say so and it can be added using the same
`Get-JsonDiffPath` approach `Publish-ToProd.ps1` already uses.

## Testing

Both `Deploy-Release.ps1` and the launcher template are covered by
`.\scripts\Deploy-Release.ps1 -SelfTest`, which builds a throwaway platform
tree under `$env:TEMP`, compiles a tiny real stand-in `DataLoader.Host.exe`,
and exercises the real `Invoke-Deploy` / `Invoke-Rollback` /
`Invoke-ReleasePrune` / `Set-CurrentRelease` functions directly — the same
functions a real run uses. It is always safe to run, anywhere, including
against production's own machine (it never touches `-PlatformRoot`'s default;
everything happens under `$env:TEMP`). See the script's own header comment
for the full case list.
