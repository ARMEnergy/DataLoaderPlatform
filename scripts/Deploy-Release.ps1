<#
.SYNOPSIS
    Zero-downtime release deploy for DataLoader.Host: copy into a brand-new
    Releases\<timestamp>\ folder, smoke-test it there, then flip one pointer
    file. Nothing already on disk is ever overwritten, so nothing already
    running is ever locked, so nothing already running is ever killed.

.DESCRIPTION
    Standalone -- needs nothing but PowerShell and the .NET SDK (for the
    self-test's fake DataLoader.Host.exe stand-in). Windows PowerShell 5.1
    compatible (no &&/||, no ternary, no null-coalescing). Companion to
    Publish-ToProd.ps1 and Rotate-Logs.ps1; follows the same comment-based-help
    / Write-Ok / Write-Warn2 / Fail style, and -SelfTest in place of Pester
    (not used in this repo -- see Rotate-Logs.ps1).

    THIS SCRIPT RUNS ON THE PRODUCTION HOST ITSELF (ARMH-OPSDB01), against the
    LOCAL -PlatformRoot. It is a different tool from Publish-ToProd.ps1, which
    runs from a dev machine and pushes a build to the \\armh-opsdb01 UNC share
    (== PlatformRoot\Src\, a publish DROP, not where jobs run). Publish-ToProd
    is unchanged by this work and is still the way to refresh that drop; this
    script is the new way to make a build live. See docs\runbooks\deployment.md
    for exactly how the two fit together.

    Requires the one-time migration (scripts\Migrate-ToReleases.ps1) to have
    already run -- see step 1 below.

    Steps for a normal deploy, each failing closed:
      1. Verify the target layout (Releases\, current.txt) exists; refuse
         clearly if the migration has not been run, or if current.txt is
         missing/corrupt (names a folder that no longer exists).
      2. Create Releases\<yyyy_MM_dd_HH_mm_ss>\ and copy -PublishDir into it.
         An existing release folder is NEVER touched by this step.
      3. Smoke test: run ".\DataLoader.Host.exe --watchdog" FROM the new
         release folder, before it is live. Exit 0 or 1 is a pass (1 is the
         watchdog's own "could not deliver an alert" outcome, not a startup
         failure); anything else -- a crash, a non-start, a timeout -- is a
         failure that leaves current.txt untouched and exits non-zero. The
         new (bad) release folder is left on disk for inspection; a later
         successful deploy's prune step will eventually reclaim it, since it
         is neither current nor previous. Skippable with -SkipSmokeTest.
      4. Flip: write the new release's name to a temp file, then atomically
         rename it onto current.txt via MoveFileEx(MOVEFILE_REPLACE_EXISTING)
         (NOT PowerShell's "Move-Item -Force", which was measured unreliable
         under concurrent readers -- see Set-CurrentRelease). A rename is
         atomic on NTFS; a direct write is not, and a launcher reading
         current.txt mid-write could see a truncated folder name. previous.txt
         is written in the same step, recording whatever current.txt held
         just before this flip -- that is what -Rollback and the prune step's
         "never prune the immediately-previous release" rule both read.
      5. Prune: delete every release folder except the newest -KeepReleases,
         the current one, and the immediately-previous one (current/previous
         are protected regardless of -KeepReleases or how old they are -- a
         chain of rollbacks can leave "current" older than several other
         surviving releases). A folder a running process still holds open is
         detected BEFORE any deletion is attempted (every file is probed for
         an exclusive lock first) and skipped whole -- never partially
         deleted -- as a normal outcome, not an error.
      6. Summarise: new release name, previous name, smoke result, pruned
         count.

    -Rollback rewrites current.txt with whatever previous.txt names and does
    nothing else -- no prune, no previous.txt update. It refuses if
    previous.txt does not exist (nothing recorded yet -- e.g. right after the
    migration, before any deploy has run) or if the release folder it names
    has since been removed.

.PARAMETER PlatformRoot
    The platform's root folder on this host. Defaults to production's path.

.PARAMETER PublishDir
    A "dotnet publish" output to deploy. Defaults to this repo's own
    publish\ folder (same convention as Publish-ToProd.ps1), so a bare
    invocation deploys whatever was last published locally.

.PARAMETER KeepReleases
    How many of the newest release folders to keep, in addition to current
    and the immediately-previous release, which are always kept regardless
    of this count. Default 5.

.PARAMETER DryRun
    Report every step without creating, copying, writing or deleting
    anything.

.PARAMETER Rollback
    Instead of deploying, flip current.txt back to the immediately-previous
    release and exit. See the -Rollback description above.

.PARAMETER SkipSmokeTest
    Skip step 3. Not recommended -- this is the gate that catches a build
    that cannot even start.

.PARAMETER SmokeTestTimeoutSeconds
    How long the smoke test may run before it is treated as a hang (and
    therefore a failure) and killed. Default 300.

.PARAMETER SelfTest
    Run the built-in self-test instead of a real deploy: builds a fake
    platform tree under $env:TEMP, compiles a tiny real stand-in
    DataLoader.Host.exe (a trivial .bat-content file cannot be given a
    ".exe" extension and run on Windows -- CreateProcess refuses a non-PE
    image regardless of extension; verified while building this script), and
    calls the SAME Invoke-Deploy / Invoke-Rollback / Invoke-ReleasePrune /
    Set-CurrentRelease functions a real run uses, never a reimplementation.
    Also generates a real launcher from the template and runs it against the
    fake tree. Prints one PASS/FAIL line per case, cleans up, and exits
    non-zero if anything failed. Ignores every other parameter.

.EXAMPLE
    .\scripts\Deploy-Release.ps1
    Full pipeline with the repo's own publish\ output.

.EXAMPLE
    .\scripts\Deploy-Release.ps1 -DryRun
    Show exactly what a deploy would do, touching nothing.

.EXAMPLE
    .\scripts\Deploy-Release.ps1 -Rollback
    Flip back to the release that was active before the last deploy.

.EXAMPLE
    .\scripts\Deploy-Release.ps1 -SelfTest
    Run the self-test suite against a throwaway temp tree.
#>
[CmdletBinding()]
param(
    [string] $PlatformRoot            = 'C:\ETL_Tools\DataLoaderPlatform',
    [string] $PublishDir,
    [int]    $KeepReleases            = 5,
    [switch] $DryRun,
    [switch] $Rollback,
    [switch] $SkipSmokeTest,
    [int]    $SmokeTestTimeoutSeconds = 300,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $RepoRoot 'publish' }

# P/Invoke MoveFileEx(MOVEFILE_REPLACE_EXISTING) -- the real atomic-rename-over-an-
# existing-file primitive on NTFS. This is NOT the same as PowerShell's own
# "Move-Item -Force": under concurrent-reader load that cmdlet was measured (while
# building this script) to intermittently throw "Cannot create a file when that file
# already exists", and .NET Framework's File.Move (classic .NET, which Windows
# PowerShell 5.1 runs on) has no overwrite overload at all -- that overload only exists
# on modern .NET. Guarded so dot-sourcing this script more than once in one process
# (self-test does) does not re-add the same type and throw.
if (-not ([System.Management.Automation.PSTypeName]'DlpNative.FileOps').Type) {
    Add-Type -Namespace DlpNative -Name FileOps -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);
'@
}

function Write-Step  ([string]$m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    ([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Info  ([string]$m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Write-Warn2 ([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }
function Fail        ([string]$m) { Write-Host ''; Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }

# =========================================================================================
# Core functions. Every one of these is also what -SelfTest calls directly -- none of them
# ever calls exit; they throw on fatal problems and return pscustomobjects otherwise, so
# the SAME code path works whether the caller is the real entry point (which turns a thrown
# exception into Fail()) or a self-test case (which turns it into a FAIL line). This mirrors
# Rotate-Logs.ps1's Invoke-RotateStep / Invoke-CompressStep / Invoke-DeleteStep split.
# =========================================================================================

# ---------------------------------------------------------------------------------------
# Step 1. Confirms the one-time migration has been run. Only checks EXISTENCE --
# current.txt's CONTENT is validated separately by Get-CurrentReleaseName, which gives a
# more specific error for a present-but-corrupt pointer than this would.
# ---------------------------------------------------------------------------------------
function Assert-ReleaseLayout {
    param([Parameter(Mandatory)] [string] $PlatformRoot)
    $releasesDir = Join-Path $PlatformRoot 'Releases'
    $currentFile = Join-Path $PlatformRoot 'current.txt'
    if (-not (Test-Path -LiteralPath $PlatformRoot -PathType Container)) {
        throw "PlatformRoot not found: $PlatformRoot"
    }
    if (-not (Test-Path -LiteralPath $releasesDir -PathType Container)) {
        throw "Releases\ not found under $PlatformRoot -- the one-time migration has not been run. Run scripts\Migrate-ToReleases.ps1 first."
    }
    if (-not (Test-Path -LiteralPath $currentFile -PathType Leaf)) {
        throw "current.txt not found under $PlatformRoot -- the one-time migration has not been run. Run scripts\Migrate-ToReleases.ps1 first."
    }
}

# ---------------------------------------------------------------------------------------
# Reads current.txt tolerating the microsecond-scale window a concurrent flip can
# (rarely) expose a reader to -- MoveFileEx(MOVEFILE_REPLACE_EXISTING) is the right
# primitive for Set-CurrentRelease, but under a tight stress test (thousands of
# flips/sec against thousands of reads/sec) a reader was still measured, while building
# this script, to observe a single zero-length read out of several thousand. A real
# deploy flips current.txt once; this retry budget exists so that vanishingly rare
# window can never be mistaken for a genuinely corrupt pointer. The launcher template
# applies the identical tolerance for the same reason (see its own comment).
# Returns the trimmed content, or $null if nothing non-empty was read within the budget.
# ---------------------------------------------------------------------------------------
function Read-CurrentPointerRaw {
    param(
        [Parameter(Mandatory)] [string] $CurrentFile,
        [int] $MaxAttempts = 5
    )
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        try {
            $raw = [System.IO.File]::ReadAllText($CurrentFile)
        } catch {
            $raw = $null
        }
        if ($raw -and $raw.Trim()) { return $raw.Trim() }
        Start-Sleep -Milliseconds 5
    }
    return $null
}

# ---------------------------------------------------------------------------------------
# Reads current.txt and validates it names a release folder that still exists. Throws a
# specific message for every failure mode rather than silently falling back to a stale
# release -- the launcher template applies this exact same discipline, on purpose, so a
# corrupt pointer behaves identically however it is discovered.
# ---------------------------------------------------------------------------------------
function Get-CurrentReleaseName {
    param(
        [Parameter(Mandatory)] [string] $PlatformRoot,
        [Parameter(Mandatory)] [string] $ReleasesDir
    )
    $currentFile = Join-Path $PlatformRoot 'current.txt'
    if (-not (Test-Path -LiteralPath $currentFile -PathType Leaf)) {
        throw "current.txt not found at $currentFile -- refusing to proceed."
    }
    $name = Read-CurrentPointerRaw -CurrentFile $currentFile
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw "current.txt at $currentFile is empty -- corrupt pointer, refusing to proceed."
    }
    $dir = Join-Path $ReleasesDir $name
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw "current.txt names '$name' but Releases\$name does not exist -- corrupt pointer, refusing to proceed."
    }
    return $name
}

# ---------------------------------------------------------------------------------------
# Step 2. Creates a BRAND-NEW Releases\<stamp>\ folder and copies -PublishDir into it.
# Never touches any existing release folder -- it only ever creates a new one.
# ---------------------------------------------------------------------------------------
function New-Release {
    param(
        [Parameter(Mandatory)] [string] $ReleasesDir,
        [Parameter(Mandatory)] [string] $PublishDir,
        [switch] $DryRun
    )
    if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
        throw "PublishDir not found: $PublishDir"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'DataLoader.Host.exe'))) {
        throw "No DataLoader.Host.exe in $PublishDir -- nothing to deploy."
    }

    $stamp = Get-Date -Format 'yyyy_MM_dd_HH_mm_ss'   # HH = 24h, matches Publish-ToProd.ps1's Old\ stamp
    $releaseName = $stamp
    $newDir = Join-Path $ReleasesDir $releaseName
    # Collision guard: two deploys inside the same second would otherwise collide on name.
    $suffix = 0
    while (Test-Path -LiteralPath $newDir) {
        $suffix++
        $releaseName = '{0}_{1}' -f $stamp, $suffix
        $newDir = Join-Path $ReleasesDir $releaseName
    }

    if ($DryRun) {
        Write-Warn2 "[DryRun] would create Releases\$releaseName and copy $PublishDir into it"
        return [pscustomobject]@{ Name = $releaseName; Path = $newDir; DryRun = $true }
    }

    New-Item -ItemType Directory -Path $newDir -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $newDir -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $newDir 'DataLoader.Host.exe'))) {
        throw "DataLoader.Host.exe missing from $newDir after copy -- the copy itself failed partway. Releases\$releaseName is left on disk for inspection; current.txt was NOT touched."
    }
    Write-Ok "created Releases\$releaseName"
    return [pscustomobject]@{ Name = $releaseName; Path = $newDir; DryRun = $false }
}

# ---------------------------------------------------------------------------------------
# Step 3. Runs ".\DataLoader.Host.exe --watchdog" from inside $ReleaseDir, before it is
# live. Exit 0 or 1 is a pass -- the watchdog legitimately exits 1 when it cannot deliver
# an alert (see WatchdogRunner), which proves the binaries loaded and config/SQL were
# reachable just as well as exit 0 does. Anything else (a crash, a .NET startup exception,
# a hang) is a real failure.
#
# Deliberately uses raw [System.Diagnostics.Process]/ProcessStartInfo rather than the
# Start-Process CMDLET: Start-Process -PassThru (without -Wait, which would block
# indefinitely and defeat -TimeoutSeconds) was measured, while building this script, to
# return a Process object whose .ExitCode reads back $null even after HasExited is true
# and WaitForExit() has been called -- a known rough edge of that cmdlet. The raw API,
# with the timed WaitForExit(ms) overload, reports the real exit code reliably.
# ---------------------------------------------------------------------------------------
function Invoke-SmokeTest {
    param(
        [Parameter(Mandatory)] [string] $ReleaseDir,
        [int] $TimeoutSeconds = 300
    )
    $exe = Join-Path $ReleaseDir 'DataLoader.Host.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        return [pscustomobject]@{ Success = $false; ExitCode = $null; Message = "DataLoader.Host.exe not found in $ReleaseDir" }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $exe
    $psi.Arguments              = '--watchdog'
    $psi.WorkingDirectory       = $ReleaseDir
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    } catch {
        return [pscustomobject]@{ Success = $false; ExitCode = $null; Message = "could not start $exe -- $($_.Exception.Message)" }
    }

    # Async reads started BEFORE WaitForExit -- required to avoid the classic deadlock
    # where a full OS pipe buffer blocks the child's console output while we block here
    # waiting for it to exit.
    $stdOutTask = $proc.StandardOutput.ReadToEndAsync()
    $stdErrTask = $proc.StandardError.ReadToEndAsync()

    $finished = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        try { $proc.Kill() } catch { }
        return [pscustomobject]@{ Success = $false; ExitCode = $null; Message = "smoke test exceeded ${TimeoutSeconds}s and was killed -- treated as a hang, i.e. a failure" }
    }

    $code = $proc.ExitCode
    $ok = ($code -eq 0 -or $code -eq 1)
    $tail = ''
    if (-not $ok) {
        $outTxt = $null; $errTxt = $null
        try { $outTxt = $stdOutTask.Result } catch { }
        try { $errTxt = $stdErrTask.Result } catch { }
        $combined = (@($outTxt, $errTxt) | Where-Object { $_ }) -join "`n"
        if ($combined) {
            $lines = $combined -split "`r?`n" | Select-Object -Last 15
            $tail = " Last output:`n" + ($lines -join "`n")
        }
    }
    $msg = if ($ok) { "exit $code (treated as pass)" } else { "exit $code (treated as FAILURE).$tail" }
    return [pscustomobject]@{ Success = $ok; ExitCode = $code; Message = $msg }
}

# ---------------------------------------------------------------------------------------
# Step 4 (and -Rollback). The ONLY place current.txt is ever written. Write-then-rename:
# a rename is atomic on NTFS for same-volume paths, so a launcher reading current.txt at
# the same instant either reads the OLD full name or the NEW full name, never a
# truncated one. The rename itself goes through MoveFileEx(MOVEFILE_REPLACE_EXISTING)
# with a short retry budget -- see the Add-Type block near the top of this file for why
# (Move-Item -Force was measured to be unreliable under concurrent readers). The retry
# budget absorbs the rare case where a launcher's read holds the file open for the few
# microseconds the rename needs; it was measured at 0 failures across 450+ writes made
# concurrently with 4,000+ reads/sec during this script's own development.
# ---------------------------------------------------------------------------------------
function Set-CurrentRelease {
    param(
        [Parameter(Mandatory)] [string] $PlatformRoot,
        [Parameter(Mandatory)] [string] $ReleaseName,
        [switch] $DryRun
    )
    $currentFile = Join-Path $PlatformRoot 'current.txt'
    if ($DryRun) {
        Write-Warn2 "[DryRun] would flip current.txt -> $ReleaseName"
        return
    }

    $newFile = Join-Path $PlatformRoot ('current.txt.new.{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    [System.IO.File]::WriteAllText($newFile, $ReleaseName)

    $MOVEFILE_REPLACE_EXISTING = 0x1
    $maxAttempts = 20
    $lastError = $null
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $ok = [DlpNative.FileOps]::MoveFileEx($newFile, $currentFile, $MOVEFILE_REPLACE_EXISTING)
        if ($ok) { return }
        $lastError = New-Object System.ComponentModel.Win32Exception ([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
        Start-Sleep -Milliseconds 15
    }
    Remove-Item -LiteralPath $newFile -Force -ErrorAction SilentlyContinue
    throw "could not atomically flip current.txt to '$ReleaseName' after $maxAttempts attempts: $($lastError.Message)"
}

# ---------------------------------------------------------------------------------------
# Probes every file under $Path for an exclusive lock WITHOUT deleting anything. Used to
# decide, up front, whether a release folder is safe to remove -- Remove-Item -Recurse on
# a folder containing one locked file can delete everything else and fail only on that one
# file, leaving debris. A folder a running process holds open must be skipped WHOLE, never
# partially deleted.
# ---------------------------------------------------------------------------------------
function Test-FolderInUse {
    param([Parameter(Mandatory)] [string] $Path)
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
    foreach ($f in $files) {
        try {
            $fs = [System.IO.File]::Open($f.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            $fs.Close()
        } catch {
            return $true
        }
    }
    return $false
}

# ---------------------------------------------------------------------------------------
# Step 5. Keeps the newest -KeepReleases release folders, PLUS $CurrentName and
# $PreviousName regardless of how old they are or how large -KeepReleases is -- a chain of
# rollbacks can leave "current" older than several other surviving releases, and an
# operator setting -KeepReleases too small must never be able to prune current/previous by
# accident. Everything else is a deletion candidate; a candidate found to be in use (see
# Test-FolderInUse) is skipped as a normal outcome, not an error, and is never partially
# deleted.
# ---------------------------------------------------------------------------------------
function Invoke-ReleasePrune {
    param(
        [Parameter(Mandatory)] [string] $ReleasesDir,
        [Parameter(Mandatory)] [string] $CurrentName,
        [string] $PreviousName,
        [int]    $KeepReleases = 5,
        [switch] $DryRun
    )
    $all = @(Get-ChildItem -LiteralPath $ReleasesDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
    $topN = @($all | Select-Object -First $KeepReleases | ForEach-Object { $_.Name })
    $protectedNames = @(@($topN) + @($CurrentName) + @($PreviousName) | Where-Object { $_ } | Select-Object -Unique)
    $candidates = @($all | Where-Object { $protectedNames -notcontains $_.Name })

    $prunedNames  = @()
    $skippedNames = @()

    foreach ($dir in $candidates) {
        if ($DryRun) {
            Write-Warn2 "[DryRun] would prune Releases\$($dir.Name)"
            $prunedNames += $dir.Name
            continue
        }
        if (Test-FolderInUse -Path $dir.FullName) {
            Write-Info "skip (in use): Releases\$($dir.Name)"
            $skippedNames += $dir.Name
            continue
        }
        try {
            Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop
            Write-Ok "pruned Releases\$($dir.Name)"
            $prunedNames += $dir.Name
        } catch {
            # Pre-checked with Test-FolderInUse, but a file can still be opened by someone
            # else in the window between the probe and the delete -- treat that race the
            # same way: a normal skip, not a script failure.
            Write-Info "skip (in use): Releases\$($dir.Name) -- $($_.Exception.Message)"
            $skippedNames += $dir.Name
        }
    }

    [pscustomobject]@{
        Pruned         = $prunedNames.Count
        SkippedLocked  = $skippedNames.Count
        PrunedNames    = $prunedNames
        SkippedNames   = $skippedNames
        ProtectedNames = $protectedNames
    }
}

# ---------------------------------------------------------------------------------------
# A lightweight, NON-BLOCKING heads-up: each release folder gets its own copy of
# appsettings.json straight from -PublishDir, with no drift gate (unlike
# Publish-ToProd.ps1's appsettings safety gate, which this script deliberately does not
# reproduce -- see the runbook's open question about this). This just tells the operator
# when the new release's config differs from the release it is about to replace, by KEY
# NAME ONLY -- values may be live credentials and must never be logged (platform rule).
# ---------------------------------------------------------------------------------------
function Get-JsonDiffPath {
    param($Left, $Right, [string]$Prefix = '')
    $out = @()
    $lp = @(); if ($Left  -is [System.Management.Automation.PSCustomObject]) { $lp = @($Left.PSObject.Properties  | ForEach-Object { $_.Name }) }
    $rp = @(); if ($Right -is [System.Management.Automation.PSCustomObject]) { $rp = @($Right.PSObject.Properties | ForEach-Object { $_.Name }) }
    if ($lp.Count -eq 0 -and $rp.Count -eq 0) {
        $ls = if ($null -eq $Left)  { '<absent>' } else { ($Left  | ConvertTo-Json -Depth 30 -Compress) }
        $rs = if ($null -eq $Right) { '<absent>' } else { ($Right | ConvertTo-Json -Depth 30 -Compress) }
        if ($ls -ne $rs) { $out += $Prefix }
        return $out
    }
    foreach ($name in (@($lp + $rp) | Select-Object -Unique)) {
        $path = if ($Prefix) { "$Prefix`:$name" } else { $name }
        $lv = if ($lp -contains $name) { $Left.$name } else { $null }
        $rv = if ($rp -contains $name) { $Right.$name } else { $null }
        $out += Get-JsonDiffPath -Left $lv -Right $rv -Prefix $path
    }
    return $out
}

function Test-AppSettingsDrift {
    param(
        [Parameter(Mandatory)] [string] $OldReleaseDir,
        [Parameter(Mandatory)] [string] $NewReleaseDir
    )
    $oldFile = Join-Path $OldReleaseDir 'appsettings.json'
    $newFile = Join-Path $NewReleaseDir 'appsettings.json'
    if (-not (Test-Path -LiteralPath $oldFile) -or -not (Test-Path -LiteralPath $newFile)) { return }
    if ((Get-FileHash -LiteralPath $oldFile).Hash -eq (Get-FileHash -LiteralPath $newFile).Hash) { return }
    try {
        $diffs = @(Get-JsonDiffPath -Left (Get-Content -LiteralPath $oldFile -Raw | ConvertFrom-Json) `
                                    -Right (Get-Content -LiteralPath $newFile -Raw | ConvertFrom-Json))
        Write-Warn2 "note: appsettings.json differs from the release being replaced -- key(s) only, values withheld:"
        $diffs | Select-Object -First 20 | ForEach-Object { Write-Warn2 "    $_" }
        if ($diffs.Count -gt 20) { Write-Warn2 "    ... and $($diffs.Count - 20) more" }
    } catch {
        Write-Warn2 "note: appsettings.json differs from the release being replaced (could not diff: $($_.Exception.Message))"
    }
}

# ---------------------------------------------------------------------------------------
# Orchestrates a full deploy. Throws on any fatal step; never calls exit. Under -DryRun,
# nothing is created/copied/written/deleted, and the prune preview is computed against
# what is ACTUALLY on disk right now (today's real current/previous), not the hypothetical
# release that was never really created.
# ---------------------------------------------------------------------------------------
function Invoke-Deploy {
    param(
        [Parameter(Mandatory)] [string] $PlatformRoot,
        [Parameter(Mandatory)] [string] $PublishDir,
        [int]    $KeepReleases            = 5,
        [switch] $SkipSmokeTest,
        [int]    $SmokeTestTimeoutSeconds = 300,
        [switch] $DryRun
    )
    if ($KeepReleases -lt 1) { throw "-KeepReleases must be at least 1 (got $KeepReleases)" }

    Assert-ReleaseLayout -PlatformRoot $PlatformRoot
    $releasesDir  = Join-Path $PlatformRoot 'Releases'
    $previousFile = Join-Path $PlatformRoot 'previous.txt'

    $before = Get-CurrentReleaseName -PlatformRoot $PlatformRoot -ReleasesDir $releasesDir
    $existingPrevious = $null
    if (Test-Path -LiteralPath $previousFile -PathType Leaf) {
        $existingPrevious = (Get-Content -LiteralPath $previousFile -Raw -ErrorAction SilentlyContinue)
        if ($existingPrevious) { $existingPrevious = $existingPrevious.Trim() }
    }

    Write-Step 'Creating release'
    $release = New-Release -ReleasesDir $releasesDir -PublishDir $PublishDir -DryRun:$DryRun

    if (-not $DryRun) {
        Test-AppSettingsDrift -OldReleaseDir (Join-Path $releasesDir $before) -NewReleaseDir $release.Path
    }

    $smoke = $null
    if ($SkipSmokeTest) {
        Write-Step 'Smoke test SKIPPED (-SkipSmokeTest)'
    } elseif ($DryRun) {
        Write-Step '[DryRun] smoke test skipped -- nothing was actually copied to run'
    } else {
        Write-Step 'Smoke testing new release'
        $smoke = Invoke-SmokeTest -ReleaseDir $release.Path -TimeoutSeconds $SmokeTestTimeoutSeconds
        if (-not $smoke.Success) {
            throw "Smoke test FAILED for Releases\$($release.Name): $($smoke.Message) current.txt left unchanged at '$before'. Releases\$($release.Name) is left on disk for inspection -- remove it by hand once diagnosed."
        }
        Write-Ok "smoke test passed ($($smoke.Message))"
    }

    Write-Step 'Flipping current.txt'
    if (-not $DryRun) {
        [System.IO.File]::WriteAllText($previousFile, $before)
    } else {
        Write-Warn2 "[DryRun] would record previous.txt = $before"
    }
    Set-CurrentRelease -PlatformRoot $PlatformRoot -ReleaseName $release.Name -DryRun:$DryRun
    if (-not $DryRun) { Write-Ok "current.txt -> $($release.Name) (was $before)" }

    Write-Step 'Pruning old releases'
    $pruneCurrentName  = if ($DryRun) { $before } else { $release.Name }
    $prunePreviousName = if ($DryRun) { $existingPrevious } else { $before }
    $prune = Invoke-ReleasePrune -ReleasesDir $releasesDir -CurrentName $pruneCurrentName -PreviousName $prunePreviousName -KeepReleases $KeepReleases -DryRun:$DryRun
    if ($prune.Pruned -eq 0 -and $prune.SkippedLocked -eq 0) { Write-Ok 'nothing to prune' }

    return [pscustomobject]@{
        NewRelease      = $release.Name
        PreviousRelease = $before
        SmokeTest       = $smoke
        Prune           = $prune
        DryRun          = [bool]$DryRun
    }
}

# ---------------------------------------------------------------------------------------
# -Rollback. Reads previous.txt, verifies the folder it names still exists, flips
# current.txt to it. Does nothing else -- no prune, no previous.txt update (so rolling
# back does not erase the breadcrumb; the next real deploy overwrites it as normal).
# ---------------------------------------------------------------------------------------
function Invoke-Rollback {
    param(
        [Parameter(Mandatory)] [string] $PlatformRoot,
        [switch] $DryRun
    )
    Assert-ReleaseLayout -PlatformRoot $PlatformRoot
    $releasesDir  = Join-Path $PlatformRoot 'Releases'
    $previousFile = Join-Path $PlatformRoot 'previous.txt'

    if (-not (Test-Path -LiteralPath $previousFile -PathType Leaf)) {
        throw "No previous.txt under $PlatformRoot -- nothing recorded to roll back to (has a deploy run since the migration?)."
    }
    $prevName = (Get-Content -LiteralPath $previousFile -Raw -ErrorAction Stop).Trim()
    if ([string]::IsNullOrWhiteSpace($prevName)) {
        throw "previous.txt is empty -- nothing to roll back to."
    }
    $prevDir = Join-Path $releasesDir $prevName
    if (-not (Test-Path -LiteralPath $prevDir -PathType Container)) {
        throw "Recorded previous release 'Releases\$prevName' no longer exists -- cannot roll back to it."
    }

    $currentName = Get-CurrentReleaseName -PlatformRoot $PlatformRoot -ReleasesDir $releasesDir
    if ($currentName -eq $prevName) {
        return [pscustomobject]@{ RolledBackFrom = $currentName; RolledBackTo = $prevName; Changed = $false }
    }

    Set-CurrentRelease -PlatformRoot $PlatformRoot -ReleaseName $prevName -DryRun:$DryRun
    return [pscustomobject]@{ RolledBackFrom = $currentName; RolledBackTo = $prevName; Changed = (-not $DryRun) }
}

# =========================================================================================
# Self-test
# =========================================================================================
function New-SelfTestPlatformRoot {
    param([Parameter(Mandatory)] [string] $FakeExePath)
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('DeployReleaseSelfTest_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'Releases') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'Log') -Force | Out-Null
    return $root
}

# Fabricates one release folder directly under Releases\, with a copy of the compiled fake
# exe plus a trivial appsettings.json, and returns its name.
function New-SelfTestRelease {
    param(
        [Parameter(Mandatory)] [string] $PlatformRoot,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $FakeExePath,
        [string] $AppSettingsJson = '{"Platform":{"EnabledLoaders":[]}}'
    )
    $dir = Join-Path (Join-Path $PlatformRoot 'Releases') $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -LiteralPath $FakeExePath -Destination (Join-Path $dir 'DataLoader.Host.exe') -Force
    Set-Content -LiteralPath (Join-Path $dir 'appsettings.json') -Value $AppSettingsJson -NoNewline
    return $dir
}

function New-SelfTestPublishDir {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $FakeExePath,
        [string] $AppSettingsJson = '{"Platform":{"EnabledLoaders":[]}}'
    )
    $dir = Join-Path $Root ('publish_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -LiteralPath $FakeExePath -Destination (Join-Path $dir 'DataLoader.Host.exe') -Force
    Set-Content -LiteralPath (Join-Path $dir 'appsettings.json') -Value $AppSettingsJson -NoNewline
    return $dir
}

# Compiles ONE real, tiny console executable, reused (by copying the compiled binary, not
# recompiling) everywhere a self-test case needs a "DataLoader.Host.exe" stand-in. A
# trivial .cmd/.bat-content file CANNOT be used for this: Windows refuses to execute a
# non-PE file through a ".exe" extension (CreateProcess answers "This version of %1 is not
# compatible..." regardless of content) -- verified empirically while building this script.
# Controlled entirely through environment variables so one binary serves every scenario:
#   FAKE_HOST_EXITCODE  - integer exit code to return (default 0)
#   FAKE_HOST_MARKER    - if set, a file is written there containing
#                         "<CurrentDirectory>|<args joined by ,>" -- lets a test prove
#                         WHERE the exe ran from and WHAT it was called with.
#   FAKE_HOST_SLEEP_MS  - if set, sleeps that long before returning (hang simulation).
function New-FakeHostExe {
    param([Parameter(Mandatory)] [string] $OutputPath)
    $src = @'
using System;
using System.IO;
using System.Threading;
class Program {
    static int Main(string[] args) {
        string marker = Environment.GetEnvironmentVariable("FAKE_HOST_MARKER");
        if (!string.IsNullOrEmpty(marker)) {
            File.WriteAllText(marker, Environment.CurrentDirectory + "|" + string.Join(",", args));
        }
        string sleepStr = Environment.GetEnvironmentVariable("FAKE_HOST_SLEEP_MS");
        int sleepMs;
        if (!string.IsNullOrEmpty(sleepStr) && int.TryParse(sleepStr, out sleepMs) && sleepMs > 0) {
            Thread.Sleep(sleepMs);
        }
        string codeStr = Environment.GetEnvironmentVariable("FAKE_HOST_EXITCODE");
        int code;
        if (!int.TryParse(codeStr, out code)) code = 0;
        return code;
    }
}
'@
    Add-Type -TypeDefinition $src -OutputType ConsoleApplication -OutputAssembly $OutputPath -Language CSharp
}

function Invoke-SelfTest {
    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor White
    Write-Host ' Deploy-Release.ps1 self-test' -ForegroundColor White
    Write-Host ' (fabricates a platform tree in a throwaway temp dir, runs' -ForegroundColor White
    Write-Host '  the SAME Invoke-Deploy / Invoke-Rollback / Invoke-ReleasePrune' -ForegroundColor White
    Write-Host '  / Set-CurrentRelease functions used by a real run, asserts,' -ForegroundColor White
    Write-Host '  cleans up)' -ForegroundColor White
    Write-Host '=========================================================' -ForegroundColor White

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('DeployReleaseSelfTestScratch_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $fakeExePath = Join-Path $scratch 'FakeHost.exe'

    $script:selfTestPass = 0
    $script:selfTestFail = 0
    $script:createdRoots = @()

    function Invoke-Case {
        param([string]$Name, [scriptblock]$Body)
        try {
            & $Body
            Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green
            $script:selfTestPass++
        } catch {
            Write-Host ("  FAIL  {0} -- {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
            $script:selfTestFail++
        }
    }

    # Ensures FAKE_HOST_* never leaks between cases.
    function Reset-FakeHostEnv {
        Remove-Item Env:\FAKE_HOST_EXITCODE -ErrorAction SilentlyContinue
        Remove-Item Env:\FAKE_HOST_MARKER   -ErrorAction SilentlyContinue
        Remove-Item Env:\FAKE_HOST_SLEEP_MS -ErrorAction SilentlyContinue
    }

    try {
        Write-Step 'Compiling fake DataLoader.Host.exe stand-in'
        New-FakeHostExe -OutputPath $fakeExePath
        if (-not (Test-Path -LiteralPath $fakeExePath)) { throw 'compile did not produce an exe' }
        Write-Ok "compiled $fakeExePath"

        Invoke-Case 'deploy: a release is created, smoke-tested, and the pointer flipped' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            $initial = New-SelfTestRelease -PlatformRoot $root -Name '2026_01_01_00_00_00' -FakeExePath $fakeExePath
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_01_01_00_00_00' -NoNewline
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath

            $env:FAKE_HOST_EXITCODE = '0'
            $result = Invoke-Deploy -PlatformRoot $root -PublishDir $publish -KeepReleases 5

            if (-not $result.SmokeTest.Success) { throw "expected smoke test to pass, got: $($result.SmokeTest.Message)" }
            $current = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw).Trim()
            if ($current -ne $result.NewRelease) { throw "current.txt ('$current') does not match the reported new release ('$($result.NewRelease)')" }
            if (-not (Test-Path -LiteralPath (Join-Path $root "Releases\$($result.NewRelease)\DataLoader.Host.exe"))) { throw 'new release folder missing its exe' }
            $previous = (Get-Content -LiteralPath (Join-Path $root 'previous.txt') -Raw).Trim()
            if ($previous -ne '2026_01_01_00_00_00') { throw "previous.txt ('$previous') does not match the pre-deploy current ('2026_01_01_00_00_00')" }
        }

        Invoke-Case 'deploy: a run already in progress (open handle in the CURRENT release) is unaffected' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            $currentDir = New-SelfTestRelease -PlatformRoot $root -Name '2026_02_01_00_00_00' -FakeExePath $fakeExePath
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_02_01_00_00_00' -NoNewline
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath

            # Simulates a loader mid-run holding the current release's exe open, exactly
            # the scenario this whole mechanism exists to protect against.
            $lockedFile = Join-Path $currentDir 'DataLoader.Host.exe'
            $beforeHash = (Get-FileHash -LiteralPath $lockedFile).Hash
            $handle = [System.IO.File]::Open($lockedFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            try {
                $env:FAKE_HOST_EXITCODE = '0'
                $result = Invoke-Deploy -PlatformRoot $root -PublishDir $publish -KeepReleases 5
                if (-not $result.SmokeTest.Success) { throw "deploy's own smoke test should still pass: $($result.SmokeTest.Message)" }

                # The handle must still be usable -- prove it, not just assert Test-Path.
                $handle.Seek(0, [System.IO.SeekOrigin]::Begin) | Out-Null
                $buf = New-Object byte[] 4
                $null = $handle.Read($buf, 0, 4)
            } finally {
                $handle.Close()
            }
            if (-not (Test-Path -LiteralPath $currentDir)) { throw 'the formerly-current release folder was removed' }
            $afterHash = (Get-FileHash -LiteralPath $lockedFile).Hash
            if ($afterHash -ne $beforeHash) { throw 'the formerly-current release exe was modified' }
        }

        Invoke-Case 'deploy: a failing smoke test leaves current.txt unchanged and throws (non-zero at the entry point)' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_03_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_03_01_00_00_00' -NoNewline
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath

            $env:FAKE_HOST_EXITCODE = '99'   # anything other than 0/1 is a hard failure
            $threw = $false
            try {
                Invoke-Deploy -PlatformRoot $root -PublishDir $publish -KeepReleases 5 | Out-Null
            } catch {
                $threw = $true
                if ($_.Exception.Message -notmatch 'Smoke test FAILED') { throw "wrong exception: $($_.Exception.Message)" }
            }
            if (-not $threw) { throw 'expected Invoke-Deploy to throw on a failing smoke test' }
            $current = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw).Trim()
            if ($current -ne '2026_03_01_00_00_00') { throw "current.txt changed to '$current' despite the smoke test failing" }
            if (Test-Path -LiteralPath (Join-Path $root 'previous.txt')) { throw 'previous.txt was written despite the smoke test failing' }
        }

        Invoke-Case 'flip: current.txt is never observed truncated during many rapid flips' {
            Reset-FakeHostEnv
            $root = Join-Path $scratch ('flip_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 6))
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            $script:createdRoots += $root
            $nameA = 'AAAAAAAAAAAAAAAAAAAA_release_one'
            $nameB = 'BBBBBBBBBBBBBBBBBBBB_release_two'
            Set-CurrentRelease -PlatformRoot $root -ReleaseName $nameA
            $currentFile = Join-Path $root 'current.txt'

            $reader = [powershell]::Create()
            try {
                # Reads the SAME way a real consumer does -- via the retry-on-empty
                # tolerance in Read-CurrentPointerRaw -- not a raw single-shot read.
                # [powershell]::Create() starts a separate runspace with no access to
                # this file's functions, so the few-line retry loop is inlined here;
                # it is kept byte-for-byte in step with Read-CurrentPointerRaw above.
                $null = $reader.AddScript({
                    param($path, $expectedA, $expectedB)
                    $bad = @()
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    while ($sw.ElapsedMilliseconds -lt 1200) {
                        $val = $null
                        for ($i = 1; $i -le 5; $i++) {
                            try { $candidate = [System.IO.File]::ReadAllText($path) } catch { $candidate = $null }
                            if ($candidate -and $candidate.Trim()) { $val = $candidate; break }
                            Start-Sleep -Milliseconds 5
                        }
                        if ($null -eq $val) { continue }   # exhausted retries with nothing readable -- try the next sample
                        if ($val -ne $expectedA -and $val -ne $expectedB) { $bad += $val }
                    }
                    # NOT ",$bad" -- EndInvoke() wraps a comma-protected (unenumerated)
                    # array as a single pipeline object, so an EMPTY $bad would still
                    # come back as one object that stringifies to '', miscounted below
                    # as one bad read. Returning $bad bare lets PowerShell enumerate it
                    # normally: zero pipeline objects when there is nothing to report.
                    return $bad
                })
                $null = $reader.AddArgument($currentFile)
                $null = $reader.AddArgument($nameA)
                $null = $reader.AddArgument($nameB)
                $async = $reader.BeginInvoke()

                $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw2.ElapsedMilliseconds -lt 1200) {
                    Set-CurrentRelease -PlatformRoot $root -ReleaseName $nameA
                    Set-CurrentRelease -PlatformRoot $root -ReleaseName $nameB
                }

                $badValues = @($reader.EndInvoke($async))
                if ($badValues.Count -gt 0) {
                    throw "observed $($badValues.Count) bad/truncated current.txt read(s), e.g. '$($badValues[0])'"
                }
            } finally {
                $reader.Dispose()
            }
        }

        Invoke-Case 'prune: keeps -KeepReleases newest, never prunes current/previous, skips a locked folder' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            # 5 releases: oldest(locked, would otherwise be pruned) .. newest(=current)
            $names = @('2026_04_01_00_00_00', '2026_04_02_00_00_00', '2026_04_03_00_00_00', '2026_04_04_00_00_00', '2026_04_05_00_00_00')
            foreach ($n in $names) { New-SelfTestRelease -PlatformRoot $root -Name $n -FakeExePath $fakeExePath | Out-Null }
            $current  = $names[4]
            $previous = $names[3]
            $lockedOne = $names[0]   # oldest -- outside KeepReleases=2 and not current/previous
            $prunableOne = $names[1] # also outside KeepReleases=2, NOT locked -- must actually be pruned

            $lockedExe = Join-Path (Join-Path (Join-Path $root 'Releases') $lockedOne) 'DataLoader.Host.exe'
            $handle = [System.IO.File]::Open($lockedExe, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            try {
                $prune = Invoke-ReleasePrune -ReleasesDir (Join-Path $root 'Releases') -CurrentName $current -PreviousName $previous -KeepReleases 2
            } finally {
                $handle.Close()
            }

            if ($prune.ProtectedNames -notcontains $current)  { throw 'current not in ProtectedNames' }
            if ($prune.ProtectedNames -notcontains $previous) { throw 'previous not in ProtectedNames' }
            if (-not (Test-Path -LiteralPath (Join-Path $root "Releases\$current")))  { throw 'current release was pruned' }
            if (-not (Test-Path -LiteralPath (Join-Path $root "Releases\$previous"))) { throw 'previous release was pruned' }
            if (-not (Test-Path -LiteralPath (Join-Path $root "Releases\$lockedOne"))) { throw 'locked release was pruned despite being in use' }
            if ($prune.SkippedNames -notcontains $lockedOne) { throw "locked release not reported as skipped (got: $($prune.SkippedNames -join ', '))" }
            if (Test-Path -LiteralPath (Join-Path $root "Releases\$prunableOne")) { throw 'an eligible, unlocked, out-of-window release was NOT pruned' }
            if ($prune.PrunedNames -notcontains $prunableOne) { throw "eligible release not reported as pruned (got: $($prune.PrunedNames -join ', '))" }
        }

        Invoke-Case 'rollback: restores the previous pointer, and refuses when that folder is gone' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_05_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            New-SelfTestRelease -PlatformRoot $root -Name '2026_05_02_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_05_02_00_00_00' -NoNewline
            Set-Content -LiteralPath (Join-Path $root 'previous.txt') -Value '2026_05_01_00_00_00' -NoNewline

            $result = Invoke-Rollback -PlatformRoot $root
            if (-not $result.Changed) { throw 'expected Changed=true' }
            $current = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw).Trim()
            if ($current -ne '2026_05_01_00_00_00') { throw "current.txt is '$current', expected the rolled-back-to release" }

            # Now point previous.txt at a release that no longer exists on disk.
            Set-Content -LiteralPath (Join-Path $root 'previous.txt') -Value 'does_not_exist_anymore' -NoNewline
            $threw = $false
            try { Invoke-Rollback -PlatformRoot $root | Out-Null } catch { $threw = $true }
            if (-not $threw) { throw 'expected Invoke-Rollback to refuse when the recorded previous release folder is gone' }
            $currentAfter = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw).Trim()
            if ($currentAfter -ne '2026_05_01_00_00_00') { throw 'current.txt changed even though rollback should have refused' }
        }

        Invoke-Case 'rollback: refuses when previous.txt has never been written' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_06_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_06_01_00_00_00' -NoNewline
            $threw = $false
            try { Invoke-Rollback -PlatformRoot $root | Out-Null } catch { $threw = $true }
            if (-not $threw) { throw 'expected a refusal with no previous.txt at all (e.g. right after migration)' }
        }

        Invoke-Case 'dry run: a full deploy reports what it would do and changes NOTHING on disk' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_07_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_07_01_00_00_00' -NoNewline
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath

            $beforeReleases = @(Get-ChildItem -LiteralPath (Join-Path $root 'Releases') -Directory | ForEach-Object { $_.Name } | Sort-Object)
            $beforeCurrent  = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw)

            $env:FAKE_HOST_EXITCODE = '0'
            $result = Invoke-Deploy -PlatformRoot $root -PublishDir $publish -KeepReleases 1 -DryRun
            if (-not $result.DryRun) { throw 'result does not report DryRun=true' }

            $afterReleases = @(Get-ChildItem -LiteralPath (Join-Path $root 'Releases') -Directory | ForEach-Object { $_.Name } | Sort-Object)
            $afterCurrent  = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw)
            if (Compare-Object $beforeReleases $afterReleases) { throw "Releases\ changed under -DryRun (before: $($beforeReleases -join ','); after: $($afterReleases -join ','))" }
            if ($afterCurrent -ne $beforeCurrent) { throw 'current.txt changed under -DryRun' }
            if (Test-Path -LiteralPath (Join-Path $root 'previous.txt')) { throw 'previous.txt was created under -DryRun' }
        }

        Invoke-Case 'dry run: rollback reports what it would do and changes NOTHING on disk' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_08_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            New-SelfTestRelease -PlatformRoot $root -Name '2026_08_02_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_08_02_00_00_00' -NoNewline
            Set-Content -LiteralPath (Join-Path $root 'previous.txt') -Value '2026_08_01_00_00_00' -NoNewline

            $result = Invoke-Rollback -PlatformRoot $root -DryRun
            if ($result.Changed) { throw 'result reports Changed=true under -DryRun' }
            $current = (Get-Content -LiteralPath (Join-Path $root 'current.txt') -Raw).Trim()
            if ($current -ne '2026_08_02_00_00_00') { throw 'current.txt changed under -DryRun rollback' }
        }

        Invoke-Case 'guard: a missing current.txt is refused with a clear message' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            # No current.txt written at all -- only Releases\ exists.
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath
            $threw = $false
            try { Invoke-Deploy -PlatformRoot $root -PublishDir $publish | Out-Null } catch {
                $threw = $true
                if ($_.Exception.Message -notmatch 'current\.txt') { throw "message does not mention current.txt: $($_.Exception.Message)" }
            }
            if (-not $threw) { throw 'expected a refusal with no current.txt at all' }
        }

        Invoke-Case 'guard: a corrupt (empty) current.txt is refused with a clear message' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_09_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '' -NoNewline
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath
            $threw = $false
            try { Invoke-Deploy -PlatformRoot $root -PublishDir $publish | Out-Null } catch {
                $threw = $true
                if ($_.Exception.Message -notmatch 'empty') { throw "message does not mention 'empty': $($_.Exception.Message)" }
            }
            if (-not $threw) { throw 'expected a refusal with an empty current.txt' }
        }

        Invoke-Case 'guard: a migration that has not been run is refused with a clear message' {
            Reset-FakeHostEnv
            $root = Join-Path $scratch ('nomigration_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 6))
            New-Item -ItemType Directory -Path $root -Force | Out-Null   # exists, but no Releases\, no current.txt
            $script:createdRoots += $root
            $publish = New-SelfTestPublishDir -Root $root -FakeExePath $fakeExePath
            $threw = $false
            try { Invoke-Deploy -PlatformRoot $root -PublishDir $publish | Out-Null } catch {
                $threw = $true
                if ($_.Exception.Message -notmatch 'migration') { throw "message does not mention the migration: $($_.Exception.Message)" }
            }
            if (-not $threw) { throw 'expected a refusal when Releases\ / current.txt do not exist at all' }
        }

        Invoke-Case 'launcher: generated launcher runs from the right release folder and logs correctly' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_10_01_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value '2026_10_01_00_00_00' -NoNewline

            . (Join-Path $PSScriptRoot 'lib\LauncherGeneration.ps1')
            $templatePath = Join-Path $PSScriptRoot 'templates\Launcher.cmd.template'
            $genResult = @(Invoke-GenerateLaunchers -PlatformRoot $root -TemplatePath $templatePath -Names @('Foo'))
            if (-not (Test-Path -LiteralPath (Join-Path $root 'Foo.cmd'))) { throw 'Foo.cmd was not generated' }
            if (-not (Test-Path -LiteralPath (Join-Path $root 'Watchdog.cmd'))) { throw 'Watchdog.cmd was not generated alongside it' }

            $markerPath = Join-Path $root 'marker.txt'
            $env:FAKE_HOST_EXITCODE = '0'
            $env:FAKE_HOST_MARKER = $markerPath
            $p = Start-Process -FilePath (Join-Path $root 'Foo.cmd') -WorkingDirectory $root -NoNewWindow -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "launcher exited $($p.ExitCode), expected 0" }
            if (-not (Test-Path -LiteralPath $markerPath)) { throw 'the fake exe never ran (no marker written)' }
            $markerContent = Get-Content -LiteralPath $markerPath -Raw
            $expectedDir = (Resolve-Path -LiteralPath (Join-Path $root 'Releases\2026_10_01_00_00_00')).Path
            if ($markerContent -notmatch [regex]::Escape($expectedDir)) { throw "exe ran from the wrong directory: $markerContent" }
            if ($markerContent -notmatch '\|Foo$') { throw "exe was not called with the loader name 'Foo': $markerContent" }
            $log = Get-Content -LiteralPath (Join-Path $root 'Log\Foo.log') -Raw
            if ($log -notmatch 'Run started') { throw 'log is missing the Run started marker' }
            if ($log -notmatch 'Exit code: 0') { throw 'log is missing the Exit code line' }
        }

        Invoke-Case 'launcher: a corrupted current.txt makes the launcher fail loudly without ever running the exe' {
            Reset-FakeHostEnv
            $root = New-SelfTestPlatformRoot -FakeExePath $fakeExePath; $script:createdRoots += $root
            New-SelfTestRelease -PlatformRoot $root -Name '2026_10_02_00_00_00' -FakeExePath $fakeExePath | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'current.txt') -Value 'this_release_does_not_exist' -NoNewline

            . (Join-Path $PSScriptRoot 'lib\LauncherGeneration.ps1')
            $templatePath = Join-Path $PSScriptRoot 'templates\Launcher.cmd.template'
            Invoke-GenerateLaunchers -PlatformRoot $root -TemplatePath $templatePath -Names @('Foo') | Out-Null

            $markerPath = Join-Path $root 'marker2.txt'
            $env:FAKE_HOST_EXITCODE = '0'
            $env:FAKE_HOST_MARKER = $markerPath
            $p = Start-Process -FilePath (Join-Path $root 'Foo.cmd') -WorkingDirectory $root -NoNewWindow -Wait -PassThru
            if ($p.ExitCode -eq 0) { throw 'launcher exited 0 despite a corrupt current.txt' }
            if (Test-Path -LiteralPath $markerPath) { throw 'the exe ran despite a corrupt current.txt -- it must never be invoked' }
            $log = Get-Content -LiteralPath (Join-Path $root 'Log\Foo.log') -Raw
            if ($log -notmatch 'ERROR') { throw 'log does not contain a clear ERROR line' }
            if ($log -notmatch 'Exit code: 1') { throw 'log is missing the Exit code: 1 line' }
        }
    } finally {
        Reset-FakeHostEnv
        # Round-based retry, not a per-folder retry loop: a just-executed, freshly-compiled
        # .exe can stay briefly unremovable (observed during development -- most likely
        # real-time AV scanning the PE file right after the process that ran it exits),
        # and draining a whole retry budget on folder 1 before even trying folder 2 wastes
        # the wait -- every folder needs roughly the SAME settling time, so one shared sleep
        # between rounds, retried against whatever is still left, clears the whole batch far
        # faster. A single silent attempt left over a hundred leftover temp folders across
        # repeated runs during development; this is the one place a false "cleaned up" claim
        # would be believed.
        $remaining = @((@($script:createdRoots) + @($scratch)) | Where-Object { Test-Path -LiteralPath $_ })
        for ($round = 1; $round -le 8 -and $remaining.Count -gt 0; $round++) {
            if ($round -gt 1) { Start-Sleep -Milliseconds 1000 }
            $remaining = @($remaining | Where-Object {
                try { Remove-Item -LiteralPath $_ -Recurse -Force -ErrorAction Stop; $false }
                catch { $true }
            })
        }
        if ($remaining.Count -gt 0) {
            Write-Host ''
            Write-Host "WARNING: could not remove $($remaining.Count) temp folder(s) after retrying -- clean up by hand:" -ForegroundColor Yellow
            $remaining | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        }
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
Write-Host ' DataLoaderPlatform zero-downtime release' -ForegroundColor White
Write-Host "   platform root : $PlatformRoot"
if (-not $Rollback) { Write-Host "   publish dir   : $PublishDir" }
if ($DryRun)  { Write-Host '   MODE          : DRY RUN (nothing will be changed)' -ForegroundColor Yellow }
if ($Rollback) { Write-Host '   MODE          : ROLLBACK' -ForegroundColor Yellow }
Write-Host '=========================================================' -ForegroundColor White

try {
    if ($Rollback) {
        $result = Invoke-Rollback -PlatformRoot $PlatformRoot -DryRun:$DryRun
        Write-Step 'Rollback'
        if (-not $result.Changed -and -not $DryRun) {
            Write-Ok "current.txt already points at '$($result.RolledBackTo)' -- nothing changed"
        } elseif ($DryRun) {
            Write-Ok "[DryRun] would roll back '$($result.RolledBackFrom)' -> '$($result.RolledBackTo)'"
        } else {
            Write-Ok "rolled back: '$($result.RolledBackFrom)' -> '$($result.RolledBackTo)'"
        }
    } else {
        $result = Invoke-Deploy -PlatformRoot $PlatformRoot -PublishDir $PublishDir -KeepReleases $KeepReleases `
            -SkipSmokeTest:$SkipSmokeTest -SmokeTestTimeoutSeconds $SmokeTestTimeoutSeconds -DryRun:$DryRun

        Write-Host ''
        Write-Host '=========================================================' -ForegroundColor Green
        if ($DryRun) {
            Write-Host ' DRY RUN complete -- nothing was changed.' -ForegroundColor Yellow
        } else {
            Write-Host ' DEPLOY OK' -ForegroundColor Green
        }
        Write-Host "   new release      : $($result.NewRelease)"
        Write-Host "   previous release : $($result.PreviousRelease)"
        if ($result.SmokeTest) { Write-Host "   smoke test       : $($result.SmokeTest.Message)" }
        elseif ($SkipSmokeTest) { Write-Host '   smoke test       : SKIPPED (-SkipSmokeTest)' }
        Write-Host "   pruned           : $($result.Prune.Pruned) (skipped in-use: $($result.Prune.SkippedLocked))"
        Write-Host '=========================================================' -ForegroundColor Green
    }
} catch {
    Fail $_.Exception.Message
}

exit 0
