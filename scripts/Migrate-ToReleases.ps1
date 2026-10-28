<#
.SYNOPSIS
    ONE-TIME migration from the legacy Loader\ layout to the zero-downtime
    Releases\ + current.txt layout that Deploy-Release.ps1 uses.

.DESCRIPTION
    Standalone -- needs nothing but PowerShell. Windows PowerShell 5.1 compatible
    (no &&/||, no ternary, no null-coalescing). Companion to Deploy-Release.ps1 and
    Generate-Launchers.ps1; follows the same comment-based-help / Write-Ok /
    Write-Warn2 / Fail style as Publish-ToProd.ps1 and Rotate-Logs.ps1.

    Run this ONCE, on the host itself (ARMH-OPSDB01), before ever using
    Deploy-Release.ps1. It refuses to run a second time (Releases\ or
    current.txt already existing is treated as "already migrated").

    Steps:
      1. Create Releases\ and copy Loader\'s CURRENT contents into
         Releases\<timestamp>_initial\, excluding Old\ (12 archives, 14.4 GB,
         being deleted separately -- never worth copying into a release) and
         files\ (ICE's download cache -- already a PlatformRoot-level sibling
         of Loader\, not nested inside it, per appsettings.json's
         ICE:DownloadDirectory; excluded here too, defensively, in case that
         ever changes).
      2. Write current.txt = "<timestamp>_initial".
      3. Regenerate every launcher (<Loader>.cmd + Watchdog.cmd) from
         scripts\templates\Launcher.cmd.template so they read current.txt
         instead of always running from Loader\. THIS IS THE ONLY STEP THAT
         NEEDS A QUIET WINDOW -- rewriting a .cmd file is a plain file copy,
         not an atomic operation, so a \DLP\ scheduled task firing while a
         launcher is mid-rewrite can read a partial/garbled file and fail.
         This script prints a loud warning before it runs; see also
         docs\runbooks\deployment.md.
      4. Leaves the old Loader\ folder in place, untouched, as a fallback,
         and prints the command to remove it once the new layout has proven
         itself.

    Dot-sources scripts\lib\LauncherGeneration.ps1 (its Invoke-GenerateLaunchers
    function) for step 3, rather than shelling out to a second process, so a
    failure there surfaces as a single in-process exception. NOT
    Generate-Launchers.ps1 itself -- see that library file's own header
    comment for why dot-sourcing a script with its own param() block would be
    unsafe here.

.PARAMETER PlatformRoot
    The platform's root folder on this host. Defaults to production's path.

.PARAMETER DryRun
    Report every step without creating, copying or writing anything.

.EXAMPLE
    .\scripts\Migrate-ToReleases.ps1 -DryRun
    Show exactly what the migration would do, touching nothing.

.EXAMPLE
    .\scripts\Migrate-ToReleases.ps1
    Run it for real. Disable the \DLP\ scheduled tasks first -- see the
    warning this prints before step 3, and docs\runbooks\deployment.md.
#>
[CmdletBinding()]
param(
    [string] $PlatformRoot = 'C:\ETL_Tools\DataLoaderPlatform',
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Step  ([string]$m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    ([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn2 ([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }
function Fail        ([string]$m) { Write-Host ''; Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host '=========================================================' -ForegroundColor White
Write-Host ' DataLoaderPlatform: migrate Loader\ -> Releases\' -ForegroundColor White
Write-Host "   platform root : $PlatformRoot"
if ($DryRun) { Write-Host '   MODE          : DRY RUN (nothing will be changed)' -ForegroundColor Yellow }
Write-Host '=========================================================' -ForegroundColor White
Write-Host ''
Write-Host '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!' -ForegroundColor Red
Write-Host '!!  QUIET WINDOW NEEDED FOR STEP 3 (launcher rewrite)  !!' -ForegroundColor Red
Write-Host '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!' -ForegroundColor Red
Write-Host '   Rewriting <Loader>.cmd / Watchdog.cmd is a plain file copy, NOT an' -ForegroundColor Yellow
Write-Host '   atomic operation. A \DLP\ scheduled task firing while a launcher is' -ForegroundColor Yellow
Write-Host '   mid-rewrite reads a partial/garbled .cmd and fails. Disable every' -ForegroundColor Yellow
Write-Host '   task under \DLP\ in Task Scheduler on THIS host BEFORE continuing' -ForegroundColor Yellow
Write-Host '   for real (skip this for -DryRun), and re-enable them once this' -ForegroundColor Yellow
Write-Host '   script reports MIGRATION COMPLETE. See docs\runbooks\deployment.md.' -ForegroundColor Yellow
Write-Host ''

$loaderDir   = Join-Path $PlatformRoot 'Loader'
$releasesDir = Join-Path $PlatformRoot 'Releases'
$currentFile = Join-Path $PlatformRoot 'current.txt'

if (-not (Test-Path -LiteralPath $PlatformRoot -PathType Container)) { Fail "PlatformRoot not found: $PlatformRoot" }
if (-not (Test-Path -LiteralPath $loaderDir -PathType Container))    { Fail "Loader\ not found under $PlatformRoot -- nothing to migrate" }
if (Test-Path -LiteralPath $releasesDir) { Fail "Releases\ already exists under $PlatformRoot -- migration already run. Use Deploy-Release.ps1 for subsequent releases." }
if (Test-Path -LiteralPath $currentFile) { Fail "current.txt already exists under $PlatformRoot -- migration already run." }

# ---------------------------------------------------------------- 1. copy Loader\ -> Releases\<ts>_initial
Write-Step 'Creating the initial release from Loader\'
$stamp = Get-Date -Format 'yyyy_MM_dd_HH_mm_ss'   # HH = 24h, matches Publish-ToProd.ps1's Old\ stamp
$releaseName = "${stamp}_initial"
$releasePath = Join-Path $releasesDir $releaseName

$excludedNames = @('Old', 'files')   # see .DESCRIPTION step 1
$toCopy = @(Get-ChildItem -LiteralPath $loaderDir -Force | Where-Object { $excludedNames -notcontains $_.Name })

if ($DryRun) {
    Write-Warn2 "[DryRun] would create Releases\$releaseName and copy $($toCopy.Count) top-level entr(ies) from Loader\ (excluding Old\, files\):"
    $toCopy | ForEach-Object { Write-Warn2 "    $($_.Name)" }
} else {
    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null
    New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
    foreach ($e in $toCopy) {
        Copy-Item -LiteralPath $e.FullName -Destination $releasePath -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $releasePath 'DataLoader.Host.exe'))) {
        Fail ("DataLoader.Host.exe missing from $releasePath after copy -- Loader\ may not hold a real deployment. " +
              "current.txt was NOT written. Remove Releases\$releaseName and investigate before re-running.")
    }
    Write-Ok "created Releases\$releaseName ($($toCopy.Count) top-level entr(ies) copied)"
}

# ---------------------------------------------------------------- 2. current.txt
Write-Step 'Writing current.txt'
if ($DryRun) {
    Write-Warn2 "[DryRun] would write current.txt = $releaseName"
} else {
    [System.IO.File]::WriteAllText($currentFile, $releaseName)
    Write-Ok "current.txt -> $releaseName"
}

# ---------------------------------------------------------------- 3. launchers (QUIET WINDOW)
Write-Step 'Regenerating launchers -- QUIET WINDOW, see warning above'
# Dot-sources the parameter-free function library directly -- NOT Generate-Launchers.ps1,
# which has its own -PlatformRoot/-DryRun parameters that dot-sourcing would otherwise
# silently reassign in THIS script's scope (dot-sourcing merges scope; it does not create
# a child one). See lib\LauncherGeneration.ps1's own header comment.
. (Join-Path $PSScriptRoot 'lib\LauncherGeneration.ps1')
$templatePath = Join-Path $PSScriptRoot 'templates\Launcher.cmd.template'
try {
    $results = Invoke-GenerateLaunchers -PlatformRoot $PlatformRoot -TemplatePath $templatePath -DryRun:$DryRun
} catch {
    Fail ("launcher generation failed: $($_.Exception.Message) -- current.txt and Releases\$releaseName were " +
          "still written; fix the problem and re-run scripts\Generate-Launchers.ps1 directly (it is safe to " +
          "run again -- every launcher is just a verbatim copy of the template).")
}
foreach ($r in $results) {
    if ($DryRun) { Write-Ok "[DryRun] would write $($r.Name).cmd" } else { Write-Ok "wrote $($r.Name).cmd" }
}

# ---------------------------------------------------------------- 4. summary
Write-Host ''
Write-Host '=========================================================' -ForegroundColor Green
if ($DryRun) {
    Write-Host ' DRY RUN complete -- nothing was changed.' -ForegroundColor Yellow
} else {
    Write-Host ' MIGRATION COMPLETE' -ForegroundColor Green
    Write-Host "   initial release : Releases\$releaseName"
    Write-Host "   current.txt     : $releaseName"
    Write-Host "   launchers       : $($results.Count) regenerated"
    Write-Host ''
    Write-Host '   Re-enable the \DLP\ scheduled tasks now.' -ForegroundColor White
    Write-Host ''
    Write-Host '   Loader\ was left EXACTLY as it was, as a fallback. Once the new' -ForegroundColor White
    Write-Host '   layout has run successfully for a few days, remove it by hand:' -ForegroundColor White
    Write-Host "     Remove-Item -Recurse -Force '$loaderDir'" -ForegroundColor White
}
Write-Host '=========================================================' -ForegroundColor Green
exit 0
