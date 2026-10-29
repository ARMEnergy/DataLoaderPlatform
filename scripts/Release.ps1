<#
.SYNOPSIS
    One-command release: build, test and publish on the laptop, then drive the
    whole server-side deploy over PowerShell remoting -- so the smoke test
    still runs where it has to (on ARMH-OPSDB01 itself) without anyone RDP-ing
    in by hand.

.DESCRIPTION
    Standalone -- needs nothing but PowerShell and the .NET SDK (for build/test/
    publish) plus working PS remoting to -Server. Windows PowerShell 5.1
    compatible (no &&/||, no ternary, no null-coalescing). Companion to
    Deploy-Release.ps1 (which this script invokes REMOTELY, unchanged, on the
    server) and to Publish-ToProd.ps1/Rotate-Logs.ps1, whose comment-based-help /
    Write-Ok / Write-Warn2 / Fail / -SelfTest style it follows.

    Today's manual flow spans two machines and a person carrying a path between
    them:
        Laptop : git pull -> dotnet test -> dotnet publish -o .\publish-staging
                 -> copy to \\armh-opsdb01\c$\...\_staging
        Server : cd ...\scripts -> .\Deploy-Release.ps1 -PublishDir ...\_staging
    This script replaces both halves with one invocation from the laptop.
    Deploy-Release.ps1 itself is NOT changed -- it still does exactly what it
    always did (new Releases\<ts>\ folder, smoke test THERE, atomic current.txt
    flip, prune). This script's only job is orchestration: get the right bits
    onto the server, trigger that script under the right account, and verify
    the pointer actually moved.

    Steps, each failing closed (an uncaught exception at any step aborts
    everything after it; nothing already done is undone):

      1. PREFLIGHT.
         a. Git state (SKIPPED under -Rollback -- see the note on that below):
            on branch master; local master neither ahead nor behind
            origin/master (a `git fetch` is performed as part of this, so it
            needs network to origin, same as `git pull` would); no uncommitted
            SOURCE changes. "Source" deliberately excludes bin\, obj\ (at any
            depth) and the tracked publish\ folder, and this run's own
            -PublishDir -- see Test-BuildArtifactPath below for exactly why and
            how that exclusion is drawn; getting it wrong makes the gate either
            always refuse (useless) or never refuse (unsafe).
         b. Server state (NOT skipped under -Rollback): -Server answers over
            PS remoting, and Releases\/current.txt exist under -PlatformRoot
            there (the one-time migration has run).
      2. BUILD    dotnet build DataLoaderPlatform.sln -c Release
      3. TEST     dotnet test  DataLoaderPlatform.sln -c Release -- the totals
                  reported are whatever this run actually observed (parsed from
                  the per-project "Passed! - Failed: N, Passed: N, ..." summary
                  lines), never a hardcoded expectation.
      4. PUBLISH  -PublishDir is emptied first (so a file left over from a
                  previous, unrelated publish can never ride along), then
                  `dotnet publish src/DataLoader.Host -o -PublishDir`.
      5. SYNC     scripts\ (this whole folder, including lib\ and templates\)
                  is copied onto the server before anything else touches it.
                  Deploy-Release.ps1 is a COPY on the server, not a link; if it
                  or the launcher template changed in this repo, skipping this
                  step would mean the server runs a stale version of the very
                  script this tool depends on. Syncing every run removes that
                  failure mode entirely -- there is no "did the script
                  changes make it over" question to ask.
      6. COPY     -PublishDir's contents go to <PlatformRoot>\_staging on the
                  server (emptied first, for the same reason as step 4).
      7. CONFIRM  current release -> proposed action, one prompt. Skippable
                  with -Force; never shown under -DryRun (there is nothing to
                  confirm -- nothing after this point runs).
      8. DEPLOY   Deploy-Release.ps1 -PublishDir <that _staging path> is run
                  ON THE SERVER via PS remoting, under the caller's own
                  Windows identity (no stored credential anywhere in this
                  script). Its console output is relayed back verbatim. A
                  non-zero exit stops here; current.txt is reported as NOT
                  changed (and this is actively re-verified, not assumed --
                  see Invoke-ReleaseFlow).
      9. VERIFY   current.txt is read back OVER REMOTING (not a fallback local
                  guess) and compared to what it was before step 8. This does
                  NOT wait for a scheduled loader run to prove anything --
                  that was an explicit decision; the pointer having moved is
                  the whole contract this script is responsible for.

    -Rollback skips steps 2-7 ENTIRELY (nothing is built, tested, published,
    synced, copied, or confirmed) and goes straight from the server-state half
    of preflight to `Deploy-Release.ps1 -Rollback` on the server, then verifies
    the pointer moved back. See the DEVIATION note below.

    -DryRun exercises the FULL git+server preflight, build, test and publish
    for real -- those are safe (publish only ever touches -PublishDir, which
    is local) and are the entire point of a rehearsal. It still reads the
    server's current release (so it can print the "would deploy" line
    truthfully), but it makes NO remote call that writes anything -- no sync,
    no copy, no Deploy-Release.ps1 invocation -- and never prompts.

    DEVIATION FROM A LITERAL READING, flagged rather than silently applied:
    the task description lists "server unreachable / layout missing" as part
    of step 1 without saying -Rollback skips it, which would seem to imply
    -Rollback should ALSO run the git-state checks in step 1a. This script
    does NOT do that: -Rollback flips a pointer on the server to a release
    that is already staged there -- it builds nothing and reads nothing from
    the local working tree, so a reviewer's unrelated local WIP on a feature
    branch must never be able to block an emergency rollback. The server-state
    checks (reachable, migrated) ARE still enforced for -Rollback, since those
    describe whether the rollback can physically happen. If literal
    compliance (git checks apply to -Rollback too) is actually wanted, that is
    a small change to Invoke-ReleaseFlow's -Rollback branch -- say so.

    DESIGN NOTE -- why PS remoting (sessions/Invoke-Command), not the admin
    share (\\<Server>\c$\...): only ONE server capability is verified by the
    person who scoped this work: "Invoke-Command -ComputerName ARMH-OPSDB01
    ... can read the platform root." Nothing says the admin share is reachable
    for this account (it may well be, but that is a second, unverified access
    path). Every remote operation here therefore goes through either an
    ephemeral `Invoke-Command -ComputerName` call (for reachability, reading
    current.txt, and running Deploy-Release.ps1 -- see Invoke-RemoteDeploy's
    own comment for why THAT one spawns a child process instead of invoking
    the script inline) or a ONE-OFF `New-PSSession` opened, used for a single
    `Copy-Item -ToSession`, and closed immediately (the only way to copy local
    files over remoting without a second transport). No session is held open
    across the whole run -- build+test can take many minutes, and holding a
    single session idle that long for no reason would be an unforced
    dependency on whatever idle-timeout the server's WinRM config happens to
    have today.

.PARAMETER DryRun
    Rehearse everything through publish for real; make no remote write and
    never prompt. See .DESCRIPTION.

.PARAMETER Rollback
    Skip build/test/publish/sync/copy/confirm; invoke
    `Deploy-Release.ps1 -Rollback` on -Server and verify the pointer moved
    back. See the DEVIATION note in .DESCRIPTION for exactly what preflight
    still runs.

.PARAMETER Force
    Skip the confirmation prompt in step 7. Has no effect under -DryRun
    (which never prompts regardless) or alongside -Rollback (which also never
    prompts -- see .DESCRIPTION).

.PARAMETER SelfTest
    Run the built-in self-test instead of a real release: exercises the real
    git-based preflight functions against throwaway local git repos (a real
    `git init`, a local bare "origin", real commits -- no network, and no
    stubbing needed since these are pure local operations), and exercises
    Invoke-ReleaseFlow's ORCHESTRATION (what calls what, in what order, under
    which mode) against an injectable $script:Remote / $script:Local pair of
    hashtables-of-scriptblocks, swapped for recording stubs that never touch a
    network or run dotnet. It does NOT re-prove that `dotnet build/test/publish`
    themselves work -- that would mean running the real multi-minute solution
    build/test inside a self-test that is supposed to finish in seconds, and
    it is exactly what a plain `dotnet test DataLoaderPlatform.sln -c Release`
    already proves, outside this script. Prints one PASS/FAIL line per case,
    cleans up, and exits non-zero if anything failed. Ignores every other
    parameter.

.PARAMETER Server
    The production host. Defaults to ARMH-OPSDB01.

.PARAMETER PlatformRoot
    The platform's root folder ON THE SERVER (a local path as seen from
    -Server, not a UNC path). Defaults to production's path.

.PARAMETER PublishDir
    Where `dotnet publish` writes its output, resolved against this repo's
    root (not the caller's current directory) if given as a relative path --
    see Resolve-PathFull. Defaults to .\publish-staging. Emptied before every
    publish (step 4).

.EXAMPLE
    .\scripts\Release.ps1 -DryRun
    Rehearse the whole local half (preflight, build, test, publish) and show
    what would be sent to the server, touching nothing remote.

.EXAMPLE
    .\scripts\Release.ps1
    Full pipeline, with one confirmation prompt before the server is touched.

.EXAMPLE
    .\scripts\Release.ps1 -Force
    Full pipeline, no confirmation prompt.

.EXAMPLE
    .\scripts\Release.ps1 -Rollback
    Flip the server back to the previous release. No build/test/publish, no
    confirmation.

.EXAMPLE
    .\scripts\Release.ps1 -SelfTest
    Run the self-test suite. Never touches any real server.
#>
[CmdletBinding()]
param(
    [switch] $DryRun,
    [switch] $Rollback,
    [switch] $Force,
    [switch] $SelfTest,
    [string] $Server       = 'ARMH-OPSDB01',
    [string] $PlatformRoot = 'C:\ETL_Tools\DataLoaderPlatform',
    [string] $PublishDir   = '.\publish-staging'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step  ([string]$m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    ([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Info  ([string]$m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Write-Warn2 ([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }
function Fail        ([string]$m) { Write-Host ''; Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }

# =========================================================================================
# Small pure helpers -- no I/O, independently self-testable.
# =========================================================================================

# Resolves $Path against $BaseDir LEXICALLY (never touches the filesystem, so it works
# before a directory exists -- important for -PublishDir, which step 4 may be creating
# for the first time). A rooted $Path (user passed an absolute -PublishDir) is returned
# as-is; a relative one (the ".\publish-staging" default) is resolved against $BaseDir
# (this repo's root), NOT the caller's current directory -- matching Deploy-Release.ps1's
# own convention of resolving its default PublishDir against $RepoRoot, so the same
# invocation behaves identically regardless of where the shell happened to be sitting.
function Resolve-PathFull {
    param([Parameter(Mandatory)] [string] $BaseDir, [Parameter(Mandatory)] [string] $Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $BaseDir $Path))
}

# Is $Path build-artifact noise that the clean-tree preflight check should ignore?
#
# THIS REPO TRACKS bin/, obj/ AND publish/ IN GIT (force-added at some point, despite
# .gitignore's [Bb]in / obj/ / etc. -- .gitignore only ever stops something from being
# ADDED, never un-tracks what is already tracked). That means `git status --porcelain`
# is NEVER empty after a build: every tracked .dll/.pdb under bin\/obj\/publish\ shows as
# modified. A clean-tree check that does not account for this either (a) always refuses,
# because there is always build churn, or (b) if someone "fixes" that by ignoring
# EVERYTHING under those names including genuinely new source files dropped there by
# mistake, never refuses -- both are useless. The fix is to recognise ONLY the three
# known-noisy shapes and flag everything else, so a real uncommitted change anywhere
# else (including a brand-new untracked file nobody `git add`ed yet) still blocks:
#   1. bin/ or obj/ as a WHOLE path segment, at ANY depth -- case-insensitively, the
#      same way this repo's own .gitignore spells it ([Bb]in / obj/). dotnet recreates
#      every file under these on every build, so a tracked copy churns regardless of
#      what the build actually changed. Segment-bounded (?:^|/)(bin|obj)(?:/|$) so a
#      real file like "src/Foo/ObjectMapper.cs" or "src/Lobby/Thing.cs" is NOT matched
#      -- "obj"/"bin" must be the ENTIRE segment, not a substring of one.
#   2. The repo's own tracked publish\ drop (`dotnet publish -o .\publish`, see
#      CLAUDE.md's Build and Run section) -- deliberately committed, so it too churns
#      on every build, exactly like bin/obj.
#   3. THIS run's own -PublishDir (default .\publish-staging, overridable), passed in
#      via $ExtraPrefixes as a repo-relative path. Step 4 empties and regenerates it
#      every run; without this rule, the very first time anyone published once, this
#      script would report its OWN output as "uncommitted source" (it is untracked and
#      NOT covered by .gitignore -- "publish-staging" was never added there).
function Test-BuildArtifactPath {
    param([Parameter(Mandatory)] [string] $Path, [string[]] $ExtraPrefixes = @())
    $p = ($Path -replace '\\', '/').TrimStart('/')

    if ($p -match '(?i)(^|/)(bin|obj)(/|$)') { return $true }
    if ($p -eq 'publish' -or $p -like 'publish/*') { return $true }

    foreach ($prefix in $ExtraPrefixes) {
        $pref = ($prefix -replace '\\', '/').Trim('/')
        if ($pref -and ($p -eq $pref -or $p -like "$pref/*")) { return $true }
    }
    return $false
}

# =========================================================================================
# Git-based preflight (step 1a). Real `git` calls throughout -- no indirection needed,
# since these are pure local/read operations the self-test can exercise against a
# throwaway repo with zero risk. NOTE: never redirect a git/dotnet invocation's stderr
# with "2>&1" under $ErrorActionPreference='Stop' -- Windows PowerShell wraps each
# merged stderr line into a terminating ErrorRecord (NativeCommandError) even on a
# clean exit 0, and both `git push`/`git fetch` write their routine progress to stderr.
# "2>$null" (discard, not merge) is safe where quiet is wanted; otherwise just don't
# redirect and let it print.
# =========================================================================================

function Get-CurrentBranch {
    param([Parameter(Mandatory)] [string] $RepoRoot)
    $branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD)
    if ($LASTEXITCODE -ne 0) { throw "git rev-parse failed (exit $LASTEXITCODE) in $RepoRoot -- is this a git repo?" }
    return $branch.Trim()
}

function Get-BranchSyncStatus {
    param(
        [Parameter(Mandatory)] [string] $RepoRoot,
        [string] $Branch = 'master',
        [string] $Remote = 'origin'
    )
    & git -C $RepoRoot fetch $Remote $Branch --quiet 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git fetch $Remote $Branch failed (exit $LASTEXITCODE) -- cannot verify sync with $Remote/$Branch." }
    $raw = (& git -C $RepoRoot rev-list --left-right --count "$Branch...$Remote/$Branch")
    if ($LASTEXITCODE -ne 0 -or -not $raw) { throw "could not compare $Branch against $Remote/$Branch (exit $LASTEXITCODE)." }
    $parts = $raw.Trim() -split '\s+'
    [pscustomobject]@{ Ahead = [int]$parts[0]; Behind = [int]$parts[1] }
}

# Returns the `git status --porcelain=v1` lines that are NOT build-artifact noise (see
# Test-BuildArtifactPath). A rename line ("R  old -> new") is treated as dirty unless
# BOTH sides are noise -- conservative/fail-closed, since build dirs are never renamed
# in practice.
function Get-DirtySourcePaths {
    param([Parameter(Mandatory)] [string] $RepoRoot, [string] $PublishDirFull)

    $raw = @(& git -C $RepoRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw "git status failed (exit $LASTEXITCODE) in $RepoRoot" }

    $extra = @()
    if ($PublishDirFull) {
        $repoFull = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\') + '\'
        $pubFull  = [System.IO.Path]::GetFullPath($PublishDirFull)
        if ($pubFull.StartsWith($repoFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            $extra += ($pubFull.Substring($repoFull.Length) -replace '\\', '/')
        }
    }

    $dirty = @()
    foreach ($line in $raw) {
        if (-not $line) { continue }
        $pathPart = $line.Substring(3)   # porcelain v1 is always "XY" + space + path
        $paths = @($pathPart)
        if ($pathPart -match '^(.*) -> (.*)$') { $paths = @($Matches[1], $Matches[2]) }

        $allNoise = $true
        foreach ($p in $paths) {
            $clean = $p.Trim('"')
            if (-not (Test-BuildArtifactPath -Path $clean -ExtraPrefixes $extra)) { $allNoise = $false }
        }
        if (-not $allNoise) { $dirty += $line }
    }
    return $dirty
}

function Invoke-GitPreflight {
    param([Parameter(Mandatory)] [string] $RepoRoot, [string] $PublishDirFull, [string] $Branch = 'master')

    $current = Get-CurrentBranch -RepoRoot $RepoRoot
    if ($current -ne $Branch) {
        throw "Refusing: current branch is '$current', not '$Branch'. Checkout $Branch first."
    }
    Write-Ok "on branch $Branch"

    $sync = Get-BranchSyncStatus -RepoRoot $RepoRoot -Branch $Branch
    if ($sync.Behind -gt 0) {
        throw "Refusing: local $Branch is $($sync.Behind) commit(s) behind origin/$Branch. Pull first."
    }
    if ($sync.Ahead -gt 0) {
        throw "Refusing: local $Branch is $($sync.Ahead) commit(s) ahead of origin/$Branch. Push first (or this would ship unreviewed commits)."
    }
    Write-Ok "in sync with origin/$Branch"

    $dirty = @(Get-DirtySourcePaths -RepoRoot $RepoRoot -PublishDirFull $PublishDirFull)
    if ($dirty.Count -gt 0) {
        $preview = ($dirty | Select-Object -First 10) -join "`n        "
        throw "Refusing: $($dirty.Count) uncommitted source change(s) (bin/obj/publish/-PublishDir churn excluded):`n        $preview"
    }
    Write-Ok 'source tree clean (bin/obj/publish/-PublishDir churn excluded)'
}

# =========================================================================================
# Remote operations -- ALL go through $script:Remote, a hashtable of scriptblocks, so the
# self-test can replace the whole thing with a stub that records what it was asked to do
# and returns canned results, never touching a network. See the DESIGN NOTE in the file
# header for why these are built the way they are (ephemeral Invoke-Command for
# exec/read, a one-off New-PSSession only for the two file copies).
# =========================================================================================

$script:RealRemote = @{

    TestReachable = {
        param($Server)
        Invoke-Command -ComputerName $Server -ScriptBlock { $true } -ErrorAction Stop | Out-Null
        $true
    }

    GetLayoutStatus = {
        param($Server, $PlatformRoot)
        Invoke-Command -ComputerName $Server -ScriptBlock {
            param($PlatformRoot)
            [pscustomobject]@{
                ReleasesExists   = Test-Path -LiteralPath (Join-Path $PlatformRoot 'Releases')    -PathType Container
                CurrentTxtExists = Test-Path -LiteralPath (Join-Path $PlatformRoot 'current.txt') -PathType Leaf
            }
        } -ArgumentList $PlatformRoot -ErrorAction Stop
    }

    GetCurrentRelease = {
        param($Server, $PlatformRoot)
        Invoke-Command -ComputerName $Server -ScriptBlock {
            param($PlatformRoot)
            $f = Join-Path $PlatformRoot 'current.txt'
            if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { return $null }
            (Get-Content -LiteralPath $f -Raw).Trim()
        } -ArgumentList $PlatformRoot -ErrorAction Stop
    }

    # A one-off session, used for exactly one Copy-Item, then closed -- Copy-Item -ToSession
    # is the only supported way to push local files over PS remoting; there is no
    # "-ComputerName" form of it. Never held open longer than this one call.
    SyncScripts = {
        param($Server, $LocalScriptsDir, $PlatformRoot)
        $session = New-PSSession -ComputerName $Server -ErrorAction Stop
        try {
            $remoteScriptsDir = Invoke-Command -Session $session -ScriptBlock {
                param($PlatformRoot)
                $d = Join-Path $PlatformRoot 'scripts'
                if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
                $d
            } -ArgumentList $PlatformRoot
            # Overlay, not mirror: this overwrites every current repo file (which fixes a
            # stale Deploy-Release.ps1/template, the one failure mode this step exists to
            # close) but does not delete a server-side file that no longer exists in the
            # repo's scripts\ folder. A deliberate, safer choice on a production host --
            # an orphaned old file left behind is far cheaper than this step ever deleting
            # something unexpected under PlatformRoot\scripts.
            Copy-Item -Path (Join-Path $LocalScriptsDir '*') -Destination $remoteScriptsDir -ToSession $session -Recurse -Force
        } finally {
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
        }
    }

    CopyPublish = {
        param($Server, $LocalPublishDir, $PlatformRoot)
        $session = New-PSSession -ComputerName $Server -ErrorAction Stop
        try {
            $remoteStagingDir = Invoke-Command -Session $session -ScriptBlock {
                param($PlatformRoot)
                $d = Join-Path $PlatformRoot '_staging'
                if (Test-Path -LiteralPath $d) {
                    # Mirrors step 4's "clean -PublishDir first" reasoning: a file left
                    # over from an earlier, unrelated publish must never ride along into
                    # what Deploy-Release.ps1 is about to smoke-test and go live with.
                    Get-ChildItem -LiteralPath $d -Force | Remove-Item -Recurse -Force
                } else {
                    New-Item -ItemType Directory -Path $d -Force | Out-Null
                }
                $d
            } -ArgumentList $PlatformRoot
            Copy-Item -Path (Join-Path $LocalPublishDir '*') -Destination $remoteStagingDir -ToSession $session -Recurse -Force
            return $remoteStagingDir
        } finally {
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
        }
    }

    # Deploy-Release.ps1 calls `exit 0`/`exit 1` at its OWN top level (not inside a
    # function). Running it INLINE inside an Invoke-Command scriptblock -- "& $ScriptPath
    # @params" directly in the remote block -- would have that `exit` tear down the
    # remote session's runspace itself mid-script, before any "return $exitCode" after it
    # ever runs, and with no reliable way to recover the real exit code or the output
    # already produced. Spawning it as its own CHILD PROCESS on the server (exactly the
    # raw-Process pattern Deploy-Release.ps1's own Invoke-SmokeTest already uses, and for
    # the same reason: Start-Process's .ExitCode was measured unreliable while building
    # that script) sidesteps this entirely -- the remoting session's runspace is never at
    # risk, and the child's real exit code and both output streams come back cleanly.
    InvokeDeploy = {
        param($Server, $PlatformRoot, $PublishDir, $Rollback)
        Invoke-Command -ComputerName $Server -ScriptBlock {
            param($PlatformRoot, $PublishDir, $Rollback)
            $scriptPath = Join-Path $PlatformRoot 'scripts\Deploy-Release.ps1'
            $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath)
            if ($Rollback) { $argList += '-Rollback' } else { $argList += @('-PublishDir', $PublishDir) }
            $quoted = ($argList | ForEach-Object { '"' + $_ + '"' }) -join ' '

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName               = 'powershell.exe'
            $psi.Arguments              = $quoted
            $psi.WorkingDirectory       = (Join-Path $PlatformRoot 'scripts')
            $psi.UseShellExecute        = $false
            $psi.CreateNoWindow         = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError  = $true

            $proc = [System.Diagnostics.Process]::Start($psi)
            $stdOutTask = $proc.StandardOutput.ReadToEndAsync()
            $stdErrTask = $proc.StandardError.ReadToEndAsync()
            $proc.WaitForExit()
            [pscustomobject]@{ ExitCode = $proc.ExitCode; StdOut = $stdOutTask.Result; StdErr = $stdErrTask.Result }
        } -ArgumentList $PlatformRoot, $PublishDir, ([bool]$Rollback) -ErrorAction Stop
    }
}

$script:Remote = $script:RealRemote

function Test-ServerReachable {
    param([Parameter(Mandatory)] [string] $Server)
    try { & $script:Remote.TestReachable $Server | Out-Null; return $true } catch { return $false }
}
function Get-RemoteLayoutStatus {
    param([Parameter(Mandatory)] [string] $Server, [Parameter(Mandatory)] [string] $PlatformRoot)
    & $script:Remote.GetLayoutStatus $Server $PlatformRoot
}
function Get-RemoteCurrentRelease {
    param([Parameter(Mandatory)] [string] $Server, [Parameter(Mandatory)] [string] $PlatformRoot)
    & $script:Remote.GetCurrentRelease $Server $PlatformRoot
}
function Sync-ScriptsToServer {
    param([Parameter(Mandatory)] [string] $Server, [Parameter(Mandatory)] [string] $LocalScriptsDir, [Parameter(Mandatory)] [string] $PlatformRoot)
    & $script:Remote.SyncScripts $Server $LocalScriptsDir $PlatformRoot
}
function Copy-PublishToServer {
    param([Parameter(Mandatory)] [string] $Server, [Parameter(Mandatory)] [string] $LocalPublishDir, [Parameter(Mandatory)] [string] $PlatformRoot)
    & $script:Remote.CopyPublish $Server $LocalPublishDir $PlatformRoot
}
function Invoke-RemoteDeploy {
    param([Parameter(Mandatory)] [string] $Server, [Parameter(Mandatory)] [string] $PlatformRoot, [string] $PublishDir, [switch] $Rollback)
    & $script:Remote.InvokeDeploy $Server $PlatformRoot $PublishDir ([bool]$Rollback)
}

function Invoke-ServerPreflight {
    param([Parameter(Mandatory)] [string] $Server, [Parameter(Mandatory)] [string] $PlatformRoot)
    if (-not (Test-ServerReachable -Server $Server)) {
        throw "Refusing: server '$Server' is not reachable over PowerShell remoting."
    }
    Write-Ok "$Server reachable"
    $layout = Get-RemoteLayoutStatus -Server $Server -PlatformRoot $PlatformRoot
    if (-not $layout.ReleasesExists -or -not $layout.CurrentTxtExists) {
        throw "Refusing: Releases\ / current.txt missing under $PlatformRoot on $Server -- the one-time migration (scripts\Migrate-ToReleases.ps1) has not been run."
    }
    Write-Ok 'release layout present on the server'
}

# =========================================================================================
# Local build/test/publish -- ALSO behind an indirection ($script:Local), so the
# self-test can assert ORCHESTRATION (e.g. "-Rollback never calls any of these") without
# paying for a real multi-minute solution build/test on every self-test run.
# =========================================================================================

$script:RealLocal = @{
    Build = {
        param($RepoRoot)
        $sln = Join-Path $RepoRoot 'DataLoaderPlatform.sln'
        & dotnet build $sln -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build exited $LASTEXITCODE" }
    }
    Test = {
        param($RepoRoot)
        $sln = Join-Path $RepoRoot 'DataLoaderPlatform.sln'
        $output = & dotnet test $sln -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            $output | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }
            throw "dotnet test exited $LASTEXITCODE -- not deploying a red build"
        }
        # Report what THIS run actually observed, never a hardcoded expectation: `dotnet
        # test` on a solution prints one "Passed! - Failed: N, Passed: N, Skipped: N,
        # Total: N" line per test project; sum them.
        $summaryLines = @($output | Where-Object { $_ -match 'Failed:\s*\d+,\s*Passed:\s*\d+,\s*Skipped:\s*\d+,\s*Total:\s*\d+' })
        $totals = [pscustomobject]@{ Failed = 0; Passed = 0; Skipped = 0; Total = 0; Projects = $summaryLines.Count }
        foreach ($line in $summaryLines) {
            if ($line -match 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)') {
                $totals.Failed  += [int]$Matches[1]
                $totals.Passed  += [int]$Matches[2]
                $totals.Skipped += [int]$Matches[3]
                $totals.Total   += [int]$Matches[4]
            }
        }
        return $totals
    }
    Publish = {
        param($RepoRoot, $PublishDirFull)
        $hostProj = Join-Path $RepoRoot 'src\DataLoader.Host\DataLoader.Host.csproj'
        if (Test-Path -LiteralPath $PublishDirFull) {
            Get-ChildItem -LiteralPath $PublishDirFull -Force | Remove-Item -Recurse -Force
        } else {
            New-Item -ItemType Directory -Path $PublishDirFull -Force | Out-Null
        }
        & dotnet publish $hostProj -c Release -o $PublishDirFull --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited $LASTEXITCODE" }
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDirFull 'DataLoader.Host.exe'))) {
            throw "No DataLoader.Host.exe in $PublishDirFull after publish -- nothing to deploy."
        }
    }
}

$script:Local = $script:RealLocal

function Invoke-BuildStep   { param($RepoRoot) & $script:Local.Build $RepoRoot }
function Invoke-TestStep    { param($RepoRoot) & $script:Local.Test $RepoRoot }
function Invoke-PublishStep { param($RepoRoot, $PublishDirFull) & $script:Local.Publish $RepoRoot $PublishDirFull }

function Confirm-Action {
    param([Parameter(Mandatory)] [string] $Message)
    $resp = Read-Host "$Message [y/N]"
    if ($resp -notmatch '^(?i:y|yes)$') { throw 'Aborted: confirmation declined.' }
}

# =========================================================================================
# Orchestrator. Never calls exit; throws on any fatal problem, returns a pscustomobject
# otherwise -- the SAME function the real entry point and every self-test case call.
# =========================================================================================
function Invoke-ReleaseFlow {
    param(
        [Parameter(Mandatory)] [string] $RepoRoot,
        [Parameter(Mandatory)] [string] $Server,
        [Parameter(Mandatory)] [string] $PlatformRoot,
        [Parameter(Mandatory)] [string] $PublishDirFull,
        [switch] $Rollback,
        [switch] $DryRun,
        [switch] $Force
    )

    Write-Step 'Preflight: server reachable + release layout present'
    Invoke-ServerPreflight -Server $Server -PlatformRoot $PlatformRoot

    if ($Rollback) {
        # See the DEVIATION note in the file header: the git-based half of preflight is
        # deliberately NOT run here.
        $before = Get-RemoteCurrentRelease -Server $Server -PlatformRoot $PlatformRoot
        Write-Host "    current release on $Server`: $before"

        if ($DryRun) {
            Write-Warn2 "[DryRun] would invoke Deploy-Release.ps1 -Rollback on $Server (current: $before). Nothing was sent to the server."
            return [pscustomobject]@{ DryRun = $true; Rollback = $true; Before = $before; After = $null; TestSummary = $null }
        }

        Write-Step 'Deploy remotely (rollback)'
        $deployResult = Invoke-RemoteDeploy -Server $Server -PlatformRoot $PlatformRoot -Rollback
        if ($deployResult.StdOut) { $deployResult.StdOut -split "`r?`n" | ForEach-Object { Write-Host $_ } }
        if ($deployResult.StdErr) { $deployResult.StdErr -split "`r?`n" | Where-Object { $_ } | ForEach-Object { Write-Warn2 $_ } }
        if ($deployResult.ExitCode -ne 0) {
            $afterFailure = $null
            try { $afterFailure = Get-RemoteCurrentRelease -Server $Server -PlatformRoot $PlatformRoot } catch { }
            if ($afterFailure -and $afterFailure -ne $before) {
                throw "Remote rollback FAILED (exit $($deployResult.ExitCode)) on $Server -- AND current.txt changed anyway, to '$afterFailure'. Investigate immediately."
            }
            throw "Remote rollback FAILED (exit $($deployResult.ExitCode)) on $Server -- current.txt was NOT changed."
        }

        Write-Step 'Verify'
        $after = Get-RemoteCurrentRelease -Server $Server -PlatformRoot $PlatformRoot
        if ($after -eq $before) {
            throw "Verification FAILED: current.txt on $Server still reads '$before' after a reported-successful rollback."
        }
        Write-Ok "rolled back: '$before' -> '$after'"
        return [pscustomobject]@{ DryRun = $false; Rollback = $true; Before = $before; After = $after; TestSummary = $null }
    }

    # ---------------------------------------------------------------- normal release
    Write-Step 'Preflight: git state'
    Invoke-GitPreflight -RepoRoot $RepoRoot -PublishDirFull $PublishDirFull

    Write-Step 'Build'
    Invoke-BuildStep -RepoRoot $RepoRoot
    Write-Ok 'build clean'

    Write-Step 'Test'
    $testSummary = Invoke-TestStep -RepoRoot $RepoRoot
    Write-Ok ("tests: {0} passed, {1} failed, {2} skipped, {3} total across {4} project summary line(s)" -f `
        $testSummary.Passed, $testSummary.Failed, $testSummary.Skipped, $testSummary.Total, $testSummary.Projects)

    Write-Step 'Publish'
    Invoke-PublishStep -RepoRoot $RepoRoot -PublishDirFull $PublishDirFull
    Write-Ok "published to $PublishDirFull"

    $before = Get-RemoteCurrentRelease -Server $Server -PlatformRoot $PlatformRoot
    Write-Host ''
    Write-Host "    current release on $Server : $before"
    Write-Host "    proposed action            : deploy '$PublishDirFull' as a new release on $Server"

    if ($DryRun) {
        Write-Warn2 '[DryRun] would sync scripts\, copy the publish output, and invoke Deploy-Release.ps1 remotely -- stopping here. Nothing was sent to the server.'
        return [pscustomobject]@{ DryRun = $true; Rollback = $false; Before = $before; After = $null; TestSummary = $testSummary }
    }

    if (-not $Force) {
        Confirm-Action 'Deploy now?'
    }

    Write-Step 'Sync scripts\ to the server'
    Sync-ScriptsToServer -Server $Server -LocalScriptsDir (Join-Path $RepoRoot 'scripts') -PlatformRoot $PlatformRoot
    Write-Ok 'scripts\ synced'

    Write-Step 'Copy publish output to _staging'
    $remoteStagingDir = Copy-PublishToServer -Server $Server -LocalPublishDir $PublishDirFull -PlatformRoot $PlatformRoot
    Write-Ok "copied to $remoteStagingDir"

    Write-Step 'Deploy remotely'
    $deployResult = Invoke-RemoteDeploy -Server $Server -PlatformRoot $PlatformRoot -PublishDir $remoteStagingDir
    if ($deployResult.StdOut) { $deployResult.StdOut -split "`r?`n" | ForEach-Object { Write-Host $_ } }
    if ($deployResult.StdErr) { $deployResult.StdErr -split "`r?`n" | Where-Object { $_ } | ForEach-Object { Write-Warn2 $_ } }
    if ($deployResult.ExitCode -ne 0) {
        $afterFailure = $null
        try { $afterFailure = Get-RemoteCurrentRelease -Server $Server -PlatformRoot $PlatformRoot } catch { }
        if ($afterFailure -and $afterFailure -ne $before) {
            throw "Remote deploy FAILED (exit $($deployResult.ExitCode)) on $Server -- AND current.txt changed anyway, to '$afterFailure'. Investigate immediately."
        }
        throw "Remote deploy FAILED (exit $($deployResult.ExitCode)) on $Server -- current.txt was NOT changed."
    }

    Write-Step 'Verify'
    $after = Get-RemoteCurrentRelease -Server $Server -PlatformRoot $PlatformRoot
    if ($after -eq $before) {
        throw "Verification FAILED: current.txt on $Server still reads '$before' after a reported-successful deploy."
    }
    Write-Ok "deployed: '$before' -> '$after'"

    return [pscustomobject]@{ DryRun = $false; Rollback = $false; Before = $before; After = $after; TestSummary = $testSummary }
}

# =========================================================================================
# Self-test
# =========================================================================================

$script:RemoteCallLog = @()
$script:LocalCallLog  = @()
$script:RemoteState   = $null

function Set-RemoteStubState {
    param(
        [bool]     $Reachable        = $true,
        [bool]     $ReleasesExists   = $true,
        [bool]     $CurrentTxtExists = $true,
        [string[]] $CurrentReleases  = @('SELFTEST_REL'),
        [int]      $DeployExitCode   = 0,
        [string]   $DeployStdOut     = 'deploy ok',
        [string]   $DeployStdErr     = ''
    )
    $script:RemoteCallLog = @()
    $script:RemoteState = [pscustomobject]@{
        Reachable        = $Reachable
        ReleasesExists   = $ReleasesExists
        CurrentTxtExists = $CurrentTxtExists
        Releases         = $CurrentReleases
        ReleaseCallIndex = 0
        DeployExitCode   = $DeployExitCode
        DeployStdOut     = $DeployStdOut
        DeployStdErr     = $DeployStdErr
    }
}

# Records every call (Op + arguments) so a case can assert exactly what was asked for,
# and returns canned results driven by $script:RemoteState -- never touches a network.
$script:FakeRemote = @{
    TestReachable = {
        param($Server)
        $script:RemoteCallLog += [pscustomobject]@{ Op = 'TestReachable'; Server = $Server }
        if (-not $script:RemoteState.Reachable) { throw "stub: $Server unreachable" }
        $true
    }
    GetLayoutStatus = {
        param($Server, $PlatformRoot)
        $script:RemoteCallLog += [pscustomobject]@{ Op = 'GetLayoutStatus'; Server = $Server; PlatformRoot = $PlatformRoot }
        [pscustomobject]@{ ReleasesExists = $script:RemoteState.ReleasesExists; CurrentTxtExists = $script:RemoteState.CurrentTxtExists }
    }
    GetCurrentRelease = {
        param($Server, $PlatformRoot)
        $script:RemoteCallLog += [pscustomobject]@{ Op = 'GetCurrentRelease'; Server = $Server; PlatformRoot = $PlatformRoot }
        $releases = $script:RemoteState.Releases
        $idx = [Math]::Min($script:RemoteState.ReleaseCallIndex, $releases.Count - 1)
        $script:RemoteState.ReleaseCallIndex++
        $releases[$idx]
    }
    SyncScripts = {
        param($Server, $LocalScriptsDir, $PlatformRoot)
        $script:RemoteCallLog += [pscustomobject]@{ Op = 'SyncScripts'; Server = $Server; LocalScriptsDir = $LocalScriptsDir; PlatformRoot = $PlatformRoot }
    }
    CopyPublish = {
        param($Server, $LocalPublishDir, $PlatformRoot)
        $script:RemoteCallLog += [pscustomobject]@{ Op = 'CopyPublish'; Server = $Server; LocalPublishDir = $LocalPublishDir; PlatformRoot = $PlatformRoot }
        Join-Path $PlatformRoot '_staging'
    }
    InvokeDeploy = {
        param($Server, $PlatformRoot, $PublishDir, $Rollback)
        $script:RemoteCallLog += [pscustomobject]@{ Op = 'InvokeDeploy'; Server = $Server; PlatformRoot = $PlatformRoot; PublishDir = $PublishDir; Rollback = $Rollback }
        [pscustomobject]@{ ExitCode = $script:RemoteState.DeployExitCode; StdOut = $script:RemoteState.DeployStdOut; StdErr = $script:RemoteState.DeployStdErr }
    }
}

$script:FakeLocal = @{
    Build   = { param($RepoRoot) $script:LocalCallLog += 'Build' }
    Test    = { param($RepoRoot) $script:LocalCallLog += 'Test'; [pscustomobject]@{ Passed = 1; Failed = 0; Skipped = 0; Total = 1; Projects = 1 } }
    Publish = { param($RepoRoot, $PublishDirFull) $script:LocalCallLog += 'Publish' }
}

# Fabricates a REAL throwaway git repo (a real `git init`, real commits) with a local
# bare "origin" -- entirely on disk, no network -- so the git-based preflight functions
# can be exercised for real rather than stubbed. `checkout -b master` right after `init`
# (not relying on init.defaultBranch / `git init -b`) makes this version-agnostic across
# whatever git is installed.
function New-SelfTestGitRepo {
    $root   = Join-Path ([System.IO.Path]::GetTempPath()) ('ReleaseSelfTest_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    $repo   = Join-Path $root 'repo'
    $origin = Join-Path $root 'origin.git'
    New-Item -ItemType Directory -Path $repo -Force | Out-Null

    & git init --quiet $repo
    & git -C $repo checkout -b master --quiet
    & git -C $repo config user.email 'selftest@local'
    & git -C $repo config user.name  'SelfTest'

    New-Item -ItemType Directory -Path (Join-Path $repo 'src') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $repo 'src\Foo.cs') -Value 'class Foo {}' -NoNewline
    Set-Content -LiteralPath (Join-Path $repo 'README.md')  -Value '# test'       -NoNewline
    & git -C $repo add -A
    & git -C $repo commit -m 'initial' --quiet | Out-Null

    & git init --bare --quiet $origin
    & git -C $repo remote add origin $origin
    & git -C $repo push -u origin master --quiet 2>$null
    if ($LASTEXITCODE -ne 0) { throw "self-test setup: initial push failed (exit $LASTEXITCODE)" }

    [pscustomobject]@{ Root = $root; RepoPath = $repo; OriginPath = $origin }
}

function Invoke-SelfTest {
    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor White
    Write-Host ' Release.ps1 self-test' -ForegroundColor White
    Write-Host ' (real throwaway git repos for the git-based preflight;' -ForegroundColor White
    Write-Host '  $script:Remote / $script:Local swapped for recording' -ForegroundColor White
    Write-Host '  stubs for everything server/dotnet-shaped; never touches' -ForegroundColor White
    Write-Host '  a real network or server; cleans up)' -ForegroundColor White
    Write-Host '=========================================================' -ForegroundColor White

    $script:selfTestPass = 0
    $script:selfTestFail = 0

    function Invoke-Case {
        param([string] $Name, [scriptblock] $Body)
        try {
            & $Body
            Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green
            $script:selfTestPass++
        } catch {
            Write-Host ("  FAIL  {0} -- {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
            $script:selfTestFail++
        }
    }

    function Remove-SelfTestRoot { param($Ctx) if ($Ctx) { Remove-Item -LiteralPath $Ctx.Root -Recurse -Force -ErrorAction SilentlyContinue } }
    function Reset-Fakes {
        $script:Remote = $script:FakeRemote
        $script:Local  = $script:FakeLocal
        $script:LocalCallLog = @()
        Set-RemoteStubState
    }
    function Restore-RealImpls {
        $script:Remote = $script:RealRemote
        $script:Local  = $script:RealLocal
    }

    # ---------------------------------------------------------------- pure helpers
    Invoke-Case 'pure: Resolve-PathFull resolves relative paths against BaseDir, leaves rooted paths alone' {
        $r1 = Resolve-PathFull -BaseDir 'C:\Repo' -Path '.\publish-staging'
        if ($r1 -ne 'C:\Repo\publish-staging') { throw "got '$r1'" }
        $r2 = Resolve-PathFull -BaseDir 'C:\Repo' -Path 'D:\Elsewhere\out'
        if ($r2 -ne 'D:\Elsewhere\out') { throw "got '$r2'" }
    }

    Invoke-Case 'pure: Test-BuildArtifactPath recognizes bin/obj/publish at any depth, and an extra prefix, without false-positiving on real source' {
        if (-not (Test-BuildArtifactPath -Path 'bin/Debug/Foo.dll')) { throw 'top-level bin/ not recognized' }
        if (-not (Test-BuildArtifactPath -Path 'src/Foo/obj/Release/net8.0/Foo.dll')) { throw 'nested obj/ not recognized' }
        if (-not (Test-BuildArtifactPath -Path 'publish/App.dll')) { throw 'publish/ not recognized' }
        if (-not (Test-BuildArtifactPath -Path 'publish-staging/App.dll' -ExtraPrefixes @('publish-staging'))) { throw 'extra prefix not recognized' }
        if (Test-BuildArtifactPath -Path 'src/Objects/Mapper.cs') { throw 'false positive on a path that merely CONTAINS "obj"' }
        if (Test-BuildArtifactPath -Path 'src/Foo.cs') { throw 'false positive on an ordinary source file' }
        if (Test-BuildArtifactPath -Path 'publish-staging/App.dll') { throw 'false positive without the matching -ExtraPrefixes' }
    }

    # ---------------------------------------------------------------- git preflight (real repos)
    Invoke-Case 'git: Get-DirtySourcePaths ignores bin/obj/publish/-PublishDir churn but flags real source changes -- THE SUBTLE CASE' {
        $ctx = New-SelfTestGitRepo
        try {
            $repo = $ctx.RepoPath
            New-Item -ItemType Directory -Path (Join-Path $repo 'src\Foo\bin\Release') -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $repo 'src\Foo\obj\Release') -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $repo 'publish') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $repo 'src\Foo\bin\Release\Foo.dll')   -Value 'v1' -NoNewline
            Set-Content -LiteralPath (Join-Path $repo 'src\Foo\obj\Release\Foo.cache') -Value 'v1' -NoNewline
            Set-Content -LiteralPath (Join-Path $repo 'publish\App.dll')               -Value 'v1' -NoNewline
            & git -C $repo add -A
            & git -C $repo commit -m 'seed tracked build artifacts (mirrors this repo''s own history)' --quiet | Out-Null

            # Simulate a rebuild: modify the tracked artifacts, and drop brand-new,
            # never-committed files under obj/ and under this run's -PublishDir.
            Set-Content -LiteralPath (Join-Path $repo 'src\Foo\bin\Release\Foo.dll') -Value 'v2' -NoNewline
            Set-Content -LiteralPath (Join-Path $repo 'publish\App.dll')             -Value 'v2' -NoNewline
            Set-Content -LiteralPath (Join-Path $repo 'src\Foo\obj\Release\New.cache') -Value 'new' -NoNewline
            $publishStagingFull = Join-Path $repo 'publish-staging'
            New-Item -ItemType Directory -Path $publishStagingFull -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $publishStagingFull 'App.dll') -Value 'staged' -NoNewline

            $rawStatus = @(& git -C $repo status --porcelain=v1)
            if ($rawStatus.Count -eq 0) { throw 'test setup bug: git status should be non-empty before the filter is applied' }

            $dirty = @(Get-DirtySourcePaths -RepoRoot $repo -PublishDirFull $publishStagingFull)
            if ($dirty.Count -ne 0) { throw "expected zero real changes despite $($rawStatus.Count) raw status line(s), got $($dirty.Count): $($dirty -join ' | ')" }

            # Now make a REAL source change -- it must be the only thing flagged.
            Set-Content -LiteralPath (Join-Path $repo 'src\Foo.cs') -Value 'class Foo { /* changed */ }' -NoNewline
            $dirty2 = @(Get-DirtySourcePaths -RepoRoot $repo -PublishDirFull $publishStagingFull)
            if ($dirty2.Count -ne 1) { throw "expected exactly 1 real change, got $($dirty2.Count): $($dirty2 -join ' | ')" }
            if ($dirty2[0] -notmatch 'Foo\.cs') { throw "expected the flagged change to be Foo.cs, got: $($dirty2[0])" }
        } finally { Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'git: Invoke-GitPreflight passes on a clean, synced, on-branch repo' {
        $ctx = New-SelfTestGitRepo
        try {
            Invoke-GitPreflight -RepoRoot $ctx.RepoPath   # must not throw
        } finally { Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'git: Invoke-GitPreflight refuses off master' {
        $ctx = New-SelfTestGitRepo
        try {
            & git -C $ctx.RepoPath checkout -b feature/x --quiet
            $threw = $false
            try { Invoke-GitPreflight -RepoRoot $ctx.RepoPath }
            catch { $threw = $true; if ($_.Exception.Message -notmatch 'branch') { throw "wrong message: $($_.Exception.Message)" } }
            if (-not $threw) { throw 'expected a refusal off master' }
        } finally { Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'git: Invoke-GitPreflight refuses when local master is AHEAD of origin/master' {
        $ctx = New-SelfTestGitRepo
        try {
            Set-Content -LiteralPath (Join-Path $ctx.RepoPath 'Extra.txt') -Value 'x' -NoNewline
            & git -C $ctx.RepoPath add -A
            & git -C $ctx.RepoPath commit -m 'local only' --quiet | Out-Null
            $threw = $false
            try { Invoke-GitPreflight -RepoRoot $ctx.RepoPath }
            catch { $threw = $true; if ($_.Exception.Message -notmatch 'ahead') { throw "wrong message: $($_.Exception.Message)" } }
            if (-not $threw) { throw 'expected a refusal when ahead of origin' }
        } finally { Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'git: Invoke-GitPreflight refuses when local master is BEHIND origin/master' {
        $ctx = New-SelfTestGitRepo
        try {
            $second = Join-Path $ctx.Root 'second_clone'
            & git clone --quiet -b master $ctx.OriginPath $second 2>$null
            & git -C $second config user.email 'selftest2@local'
            & git -C $second config user.name  'SelfTest2'
            Set-Content -LiteralPath (Join-Path $second 'FromElsewhere.txt') -Value 'x' -NoNewline
            & git -C $second add -A
            & git -C $second commit -m 'pushed elsewhere' --quiet | Out-Null
            & git -C $second push --quiet 2>$null
            if ($LASTEXITCODE -ne 0) { throw "test setup bug: push from second clone failed" }

            $threw = $false
            try { Invoke-GitPreflight -RepoRoot $ctx.RepoPath }
            catch { $threw = $true; if ($_.Exception.Message -notmatch 'behind') { throw "wrong message: $($_.Exception.Message)" } }
            if (-not $threw) { throw 'expected a refusal when behind origin' }
        } finally { Remove-SelfTestRoot $ctx }
    }

    # ---------------------------------------------------------------- orchestration (stubbed remote/local)
    Invoke-Case 'flow: -DryRun makes no write-type remote call, but DOES run build/test/publish for real (via the injected stub)' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            $publishFull = Join-Path $ctx.RepoPath 'publish-staging'
            $result = Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE' -PlatformRoot 'C:\Fake' -PublishDirFull $publishFull -DryRun

            if (-not $result.DryRun) { throw 'expected DryRun=true in the result' }
            $writeOps = @($script:RemoteCallLog | Where-Object { @('SyncScripts', 'CopyPublish', 'InvokeDeploy') -contains $_.Op })
            if ($writeOps.Count -ne 0) { throw "expected zero write-type remote calls under -DryRun, got: $(($writeOps | ForEach-Object { $_.Op }) -join ', ')" }
            if ($script:LocalCallLog -notcontains 'Build')   { throw 'expected Build to be invoked under -DryRun' }
            if ($script:LocalCallLog -notcontains 'Test')    { throw 'expected Test to be invoked under -DryRun' }
            if ($script:LocalCallLog -notcontains 'Publish') { throw 'expected Publish to be invoked under -DryRun' }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: the remote deploy call is constructed with the right arguments' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            Set-RemoteStubState -CurrentReleases @('REL_A', 'REL_B') -DeployExitCode 0
            $publishFull = Join-Path $ctx.RepoPath 'publish-staging'
            New-Item -ItemType Directory -Path $publishFull -Force | Out-Null

            $result = Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull $publishFull -Force

            if ($result.DryRun) { throw 'expected a real (non-DryRun) run' }
            if ($result.Before -ne 'REL_A' -or $result.After -ne 'REL_B') { throw "expected REL_A -> REL_B, got $($result.Before) -> $($result.After)" }

            $deployCall = $script:RemoteCallLog | Where-Object { $_.Op -eq 'InvokeDeploy' } | Select-Object -Last 1
            if (-not $deployCall) { throw 'InvokeDeploy was never called' }
            if ($deployCall.Server -ne 'FAKE-SRV') { throw "wrong Server: $($deployCall.Server)" }
            if ($deployCall.PlatformRoot -ne 'C:\FakeRoot') { throw "wrong PlatformRoot: $($deployCall.PlatformRoot)" }
            if ($deployCall.Rollback) { throw 'Rollback should be false for a normal deploy' }
            if ($deployCall.PublishDir -notmatch '_staging') { throw "expected the SERVER-side _staging path, got: $($deployCall.PublishDir)" }

            $syncCall = $script:RemoteCallLog | Where-Object { $_.Op -eq 'SyncScripts' } | Select-Object -Last 1
            if (-not $syncCall) { throw 'SyncScripts was never called' }
            if ($syncCall.LocalScriptsDir -ne (Join-Path $ctx.RepoPath 'scripts')) { throw "wrong LocalScriptsDir: $($syncCall.LocalScriptsDir)" }

            $copyCall = $script:RemoteCallLog | Where-Object { $_.Op -eq 'CopyPublish' } | Select-Object -Last 1
            if (-not $copyCall) { throw 'CopyPublish was never called' }
            if ($copyCall.LocalPublishDir -ne $publishFull) { throw "wrong LocalPublishDir: $($copyCall.LocalPublishDir)" }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: a non-zero remote exit aborts and reports current.txt NOT changed' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            Set-RemoteStubState -CurrentReleases @('REL_A') -DeployExitCode 7 -DeployStdErr 'boom'
            $publishFull = Join-Path $ctx.RepoPath 'publish-staging'
            New-Item -ItemType Directory -Path $publishFull -Force | Out-Null

            $threw = $false
            try { Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull $publishFull -Force | Out-Null }
            catch {
                $threw = $true
                if ($_.Exception.Message -notmatch 'exit 7')     { throw "wrong message (missing exit code): $($_.Exception.Message)" }
                if ($_.Exception.Message -notmatch 'NOT changed') { throw "wrong message (missing 'NOT changed'): $($_.Exception.Message)" }
            }
            if (-not $threw) { throw 'expected Invoke-ReleaseFlow to throw on a non-zero remote exit' }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: verification catches current.txt NOT actually changing despite exit 0' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            Set-RemoteStubState -CurrentReleases @('REL_A') -DeployExitCode 0   # one value -> GetCurrentRelease returns REL_A every call
            $publishFull = Join-Path $ctx.RepoPath 'publish-staging'
            New-Item -ItemType Directory -Path $publishFull -Force | Out-Null

            $threw = $false
            try { Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull $publishFull -Force | Out-Null }
            catch { $threw = $true; if ($_.Exception.Message -notmatch 'Verification FAILED') { throw "wrong message: $($_.Exception.Message)" } }
            if (-not $threw) { throw 'expected a verification failure when current.txt does not actually change' }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: -Rollback skips build/test/publish/sync/copy entirely and passes Rollback=true through' {
        $ctx = New-SelfTestGitRepo
        try {
            # Deliberately dirty local tree -- proves -Rollback does NOT run git preflight.
            Set-Content -LiteralPath (Join-Path $ctx.RepoPath 'Extra.txt') -Value 'uncommitted' -NoNewline

            Reset-Fakes
            Set-RemoteStubState -CurrentReleases @('REL_B', 'REL_A') -DeployExitCode 0

            $result = Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull 'C:\unused' -Rollback -Force

            if ($script:LocalCallLog.Count -ne 0) { throw "expected build/test/publish to be skipped entirely under -Rollback, got: $($script:LocalCallLog -join ', ')" }
            $syncOrCopy = @($script:RemoteCallLog | Where-Object { @('SyncScripts', 'CopyPublish') -contains $_.Op })
            if ($syncOrCopy.Count -ne 0) { throw 'expected no sync/copy calls under -Rollback' }
            $deployCall = $script:RemoteCallLog | Where-Object { $_.Op -eq 'InvokeDeploy' } | Select-Object -Last 1
            if (-not $deployCall) { throw 'InvokeDeploy was never called' }
            if (-not $deployCall.Rollback) { throw 'expected Rollback=true to be passed through' }
            if ($result.Before -ne 'REL_B' -or $result.After -ne 'REL_A') { throw "expected REL_B -> REL_A, got $($result.Before) -> $($result.After)" }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: -Rollback -DryRun makes no remote write call at all' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            Set-RemoteStubState -CurrentReleases @('REL_B')
            $result = Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull 'C:\unused' -Rollback -DryRun

            if (-not $result.DryRun) { throw 'expected DryRun=true' }
            $writeOps = @($script:RemoteCallLog | Where-Object { @('SyncScripts', 'CopyPublish', 'InvokeDeploy') -contains $_.Op })
            if ($writeOps.Count -ne 0) { throw "expected zero write-type remote calls, got: $(($writeOps | ForEach-Object { $_.Op }) -join ', ')" }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: a missing server-side release layout refuses before any build/test/publish' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            Set-RemoteStubState -ReleasesExists $false
            $publishFull = Join-Path $ctx.RepoPath 'publish-staging'

            $threw = $false
            try { Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull $publishFull -Force | Out-Null }
            catch { $threw = $true; if ($_.Exception.Message -notmatch 'migration') { throw "wrong message: $($_.Exception.Message)" } }
            if (-not $threw) { throw 'expected a refusal when Releases\ is missing on the server' }
            if ($script:LocalCallLog.Count -ne 0) { throw 'build/test/publish ran despite a failed layout check' }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Invoke-Case 'flow: an unreachable server refuses before any build/test/publish' {
        $ctx = New-SelfTestGitRepo
        try {
            Reset-Fakes
            Set-RemoteStubState -Reachable $false
            $publishFull = Join-Path $ctx.RepoPath 'publish-staging'

            $threw = $false
            try { Invoke-ReleaseFlow -RepoRoot $ctx.RepoPath -Server 'FAKE-SRV' -PlatformRoot 'C:\FakeRoot' -PublishDirFull $publishFull -Force | Out-Null }
            catch { $threw = $true; if ($_.Exception.Message -notmatch 'reachable') { throw "wrong message: $($_.Exception.Message)" } }
            if (-not $threw) { throw 'expected a refusal when the server is unreachable' }
            if ($script:LocalCallLog.Count -ne 0) { throw 'build/test/publish ran despite an unreachable server' }
        } finally { Restore-RealImpls; Remove-SelfTestRoot $ctx }
    }

    Write-Host ''
    $color = 'Green'; if ($script:selfTestFail -gt 0) { $color = 'Red' }
    Write-Host ("{0} passed, {1} failed" -f $script:selfTestPass, $script:selfTestFail) -ForegroundColor $color
    if ($script:selfTestFail -gt 0) { return 1 }
    return 0
}

# =========================================================================================
# Entry point
# =========================================================================================
if ($SelfTest) {
    exit (Invoke-SelfTest)
}

Write-Host ''
Write-Host '=========================================================' -ForegroundColor White
Write-Host ' DataLoaderPlatform one-command release' -ForegroundColor White
Write-Host "   server        : $Server"
Write-Host "   platform root : $PlatformRoot"
if (-not $Rollback) { Write-Host "   publish dir   : $PublishDir" }
if ($Rollback) { Write-Host '   MODE          : ROLLBACK' -ForegroundColor Yellow }
if ($DryRun)   { Write-Host '   MODE          : DRY RUN (no server writes)' -ForegroundColor Yellow }
Write-Host '=========================================================' -ForegroundColor White

try {
    $publishDirFull = Resolve-PathFull -BaseDir $RepoRoot -Path $PublishDir
    $result = Invoke-ReleaseFlow -RepoRoot $RepoRoot -Server $Server -PlatformRoot $PlatformRoot `
        -PublishDirFull $publishDirFull -Rollback:$Rollback -DryRun:$DryRun -Force:$Force

    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor Green
    if ($result.DryRun) {
        Write-Host ' DRY RUN complete -- nothing was sent to the server.' -ForegroundColor Yellow
        if ($result.Rollback) {
            Write-Host "   would roll back from : $($result.Before)"
        } else {
            Write-Host "   current release      : $($result.Before)"
            if ($result.TestSummary) {
                Write-Host ("   tests observed       : {0} passed, {1} failed, {2} skipped, {3} total ({4} project summary line(s))" -f `
                    $result.TestSummary.Passed, $result.TestSummary.Failed, $result.TestSummary.Skipped, $result.TestSummary.Total, $result.TestSummary.Projects)
            }
        }
    } else {
        if ($result.Rollback) { Write-Host ' ROLLBACK OK' -ForegroundColor Green } else { Write-Host ' RELEASE OK' -ForegroundColor Green }
        Write-Host "   $($result.Before) -> $($result.After)"
        if ($result.TestSummary) {
            Write-Host ("   tests observed : {0} passed, {1} failed, {2} skipped, {3} total ({4} project summary line(s))" -f `
                $result.TestSummary.Passed, $result.TestSummary.Failed, $result.TestSummary.Skipped, $result.TestSummary.Total, $result.TestSummary.Projects)
        }
    }
    Write-Host '=========================================================' -ForegroundColor Green
} catch {
    Fail $_.Exception.Message
}

exit 0
