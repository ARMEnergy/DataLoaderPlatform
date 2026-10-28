<#
.SYNOPSIS
    (Re)generates every "<Loader>.cmd" launcher, plus Watchdog.cmd, from the one
    checked-in template at scripts\templates\Launcher.cmd.template.

.DESCRIPTION
    Standalone -- needs nothing but PowerShell. Windows PowerShell 5.1 compatible
    (no &&/||, no ternary, no null-coalescing). Companion to Deploy-Release.ps1
    and Migrate-ToReleases.ps1; follows the same comment-based-help / Write-Ok /
    Write-Warn2 / Fail style as Publish-ToProd.ps1 and Rotate-Logs.ps1.

    The template uses the classic "%~n0" trick (the launcher's own filename,
    minus extension, supplies both the log file name and the loader name passed
    to DataLoader.Host.exe), so EVERY destination file is byte-for-byte the same
    content -- Watchdog.cmd included, which the template tells apart from an
    ordinary loader launcher by checking whether %~n0 is literally "Watchdog".
    "Generating" a launcher is therefore just copying the template verbatim to
    a new name; there is no per-file substitution anywhere in this file.

    The actual writing logic (Set-GeneratedLauncher / Invoke-GenerateLaunchers)
    lives in scripts\lib\LauncherGeneration.ps1, a function-only file with no
    param() block -- this script, Migrate-ToReleases.ps1, and Deploy-Release.ps1's
    -SelfTest all dot-source THAT file directly rather than this one. Dot-sourcing
    THIS script (which has its own -PlatformRoot / -DryRun parameters) would
    silently reassign any same-named variable already set in the caller's scope,
    since dot-sourcing merges scope rather than creating a child one -- that bit
    Migrate-ToReleases.ps1 during development (it clobbered -DryRun back to off).

.PARAMETER PlatformRoot
    Where the launchers live. Defaults to production's path.

.PARAMETER LoaderNames
    Explicit loader names to (re)generate a launcher for ("Watchdog" is always
    included automatically; no need to list it). Default: auto-discover every
    "*.cmd" file that already exists directly under -PlatformRoot, so a bare
    invocation safely regenerates exactly what is already there -- it never
    invents a launcher for a loader that doesn't have one yet.

.PARAMETER TemplatePath
    Source template file. Defaults to scripts\templates\Launcher.cmd.template
    next to this script.

.PARAMETER DryRun
    Report every file that would be written, without touching anything.

.EXAMPLE
    .\scripts\Generate-Launchers.ps1 -PlatformRoot C:\ETL_Tools\DataLoaderPlatform
    Regenerate every launcher that already exists in production, in place.

.EXAMPLE
    .\scripts\Generate-Launchers.ps1 -LoaderNames StormVista -PlatformRoot C:\ETL_Tools\DataLoaderPlatform
    Add (or refresh) just one loader's launcher -- e.g. after bringing a new
    loader online for the first time.

.EXAMPLE
    .\scripts\Generate-Launchers.ps1 -PlatformRoot C:\fake\tree -DryRun
    Show what would be (over)written without touching anything.
#>
[CmdletBinding()]
param(
    [string]   $PlatformRoot = 'C:\ETL_Tools\DataLoaderPlatform',
    [string[]] $LoaderNames,
    [string]   $TemplatePath,
    [switch]   $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not $TemplatePath) { $TemplatePath = Join-Path $PSScriptRoot 'templates\Launcher.cmd.template' }

function Write-Step  ([string]$m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    ([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn2 ([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }
function Fail        ([string]$m) { Write-Host ''; Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }

# Parameter-free library -- safe to dot-source without disturbing $PlatformRoot/$DryRun
# above. See its own header comment for why this is a separate file.
. (Join-Path $PSScriptRoot 'lib\LauncherGeneration.ps1')

# =========================================================================================
# Entry point -- skipped when THIS script is dot-sourced (nothing in this repo does that;
# consumers dot-source lib\LauncherGeneration.ps1 instead -- see .DESCRIPTION above. The
# guard is kept anyway as a safety net).
# =========================================================================================
if ($MyInvocation.InvocationName -ne '.') {
    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor White
    Write-Host ' Generate launchers from template' -ForegroundColor White
    Write-Host "   platform root : $PlatformRoot"
    Write-Host "   template      : $TemplatePath"
    if ($DryRun) { Write-Host '   MODE          : DRY RUN (nothing will be written)' -ForegroundColor Yellow }
    Write-Host '=========================================================' -ForegroundColor White

    if (-not (Test-Path -LiteralPath $PlatformRoot -PathType Container)) {
        Fail "PlatformRoot not found: $PlatformRoot"
    }

    Write-Step 'Writing launchers'
    try {
        $results = Invoke-GenerateLaunchers -PlatformRoot $PlatformRoot -TemplatePath $TemplatePath -Names $LoaderNames -DryRun:$DryRun
    } catch {
        Fail $_.Exception.Message
    }

    foreach ($r in $results) {
        if ($DryRun) { Write-Ok "[DryRun] would write $($r.Name).cmd" }
        else          { Write-Ok "wrote $($r.Name).cmd" }
    }

    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor Green
    Write-Host " DONE -- $($results.Count) launcher(s)" -ForegroundColor Green
    Write-Host '=========================================================' -ForegroundColor Green
    exit 0
}
