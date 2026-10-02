<#
.SYNOPSIS
    Build, test, publish and deploy DataLoader.Host to the production share,
    archiving whatever is already there into Old\yyyy_MM_dd_HH_mm_ss first.

.DESCRIPTION
    Standalone — needs nothing but PowerShell and the .NET SDK. Windows PowerShell
    5.1 compatible.

    Steps:
      1. dotnet build   (solution, Release)          -- skip with -SkipBuild
      2. dotnet test    (solution, Release)          -- skip with -SkipTests
      3. dotnet publish (DataLoader.Host -> publish) -- skip with -SkipBuild
      4. appsettings.json safety gate                -- see -KeepProdSettings / -OverwriteSettings
      5. Archive the target's current contents into Old\<timestamp>
      6. Copy publish\* to the target
      7. Verify file counts and report

    Exit codes follow the platform convention: 0 = success, 1 = error.

.PARAMETER Target
    UNC path to deploy to. Defaults to the production share.

.PARAMETER DryRun
    Report every action without creating, moving or copying anything.
    Steps 1-3 still run (they only touch the local working tree); pair with
    -SkipBuild for a pure look-at-the-share run.

.PARAMETER KeepProdSettings
    Keep the target's existing appsettings.json instead of the repo's. The original
    is still archived; it is restored over the newly-copied files.

.PARAMETER OverwriteSettings
    Deploy the repo's appsettings.json even though it differs from production's.

.EXAMPLE
    .\scripts\Publish-ToProd.ps1
    Full pipeline with all gates.

.EXAMPLE
    .\scripts\Publish-ToProd.ps1 -DryRun -SkipBuild -SkipTests
    Show what would be archived and copied, touching nothing.

.EXAMPLE
    .\scripts\Publish-ToProd.ps1 -KeepProdSettings
    Deploy binaries but leave production's own appsettings.json in place.
#>
[CmdletBinding()]
param(
    [string] $Target = '\\armh-opsdb01\DataLoaderPlatform',
    [string] $PublishDir,
    [switch] $DryRun,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $KeepProdSettings,
    [switch] $OverwriteSettings
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Solution   = Join-Path $RepoRoot 'DataLoaderPlatform.sln'
$HostProj   = Join-Path $RepoRoot 'src\DataLoader.Host\DataLoader.Host.csproj'
if (-not $PublishDir) { $PublishDir = Join-Path $RepoRoot 'publish' }

function Write-Step  ([string]$m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    ([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn2 ([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }
function Fail        ([string]$m) { Write-Host ''; Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }

# Emits the dotted path of every leaf whose value differs between two parsed JSON
# objects. Paths ONLY -- appsettings values may be live credentials and must never
# be printed (platform rule: secrets are never logged).
function Get-JsonDiffPath {
    param($Left, $Right, [string]$Prefix = '')
    $out = @()
    # ForEach-Object rather than $x.PSObject.Properties.Name: Set-StrictMode 2.0
    # suppresses member enumeration over a collection, so the shorter form throws
    # "The property 'Name' cannot be found on this object".
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
        $lv = if ($lp -contains $name) { $Left.$name  } else { $null }
        $rv = if ($rp -contains $name) { $Right.$name } else { $null }
        $out += Get-JsonDiffPath -Left $lv -Right $rv -Prefix $path
    }
    return $out
}

Write-Host ''
Write-Host '=========================================================' -ForegroundColor White
Write-Host ' DataLoaderPlatform -> production' -ForegroundColor White
Write-Host "   target : $Target"
Write-Host "   source : $PublishDir"
if ($DryRun) { Write-Host '   MODE   : DRY RUN (nothing will be written to the target)' -ForegroundColor Yellow }
Write-Host '=========================================================' -ForegroundColor White

# ---------------------------------------------------------------- 1-3. build / test / publish
if ($SkipBuild) {
    Write-Step 'Build + publish SKIPPED (-SkipBuild)'
} else {
    Write-Step 'Building solution (Release)'
    & dotnet build $Solution -c Release --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build exited $LASTEXITCODE" }
    Write-Ok 'build clean'
}

if ($SkipTests) {
    Write-Warn2 'TEST GATE SKIPPED (-SkipTests)'
} else {
    Write-Step 'Running solution test suite (Release)'
    & dotnet test $Solution -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "dotnet test exited $LASTEXITCODE -- not deploying a red build" }
    Write-Ok 'tests green'
}

if (-not $SkipBuild) {
    Write-Step 'Publishing DataLoader.Host'
    & dotnet publish $HostProj -c Release -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish exited $LASTEXITCODE" }
    Write-Ok "published to $PublishDir"
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'DataLoader.Host.exe'))) {
    Fail "No DataLoader.Host.exe in $PublishDir -- nothing to deploy."
}
# Canonicalise: the verify step slices this off each source FullName to get a
# relative path, so it must be the same absolute form Get-ChildItem reports.
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\')

# Report uncommitted source changes; informational, never blocking (hotfixes happen).
try {
    Push-Location $RepoRoot
    $dirty = @(& git status --porcelain -- src tests sql 2>$null | Where-Object { $_ })
    Pop-Location
    if ($dirty.Count -gt 0) { Write-Warn2 "note: $($dirty.Count) uncommitted change(s) under src/tests/sql are included in this build" }
} catch { }

# ---------------------------------------------------------------- 4. target + appsettings gate
Write-Step 'Inspecting target'
if (-not (Test-Path -LiteralPath $Target)) { Fail "Target unreachable: $Target" }

# Write-permission preflight.
#
# Checked BEFORE anything is archived. Read access to a share does not imply write
# access, and the naive ordering would move production's live files into Old\ and only
# THEN discover it cannot copy the replacements in -- leaving the deployment gutted.
#
# It also runs under -DryRun on purpose. A dry run that reports "would copy 126 files"
# and then has the real run die on Access Denied is worse than no dry run at all; this
# is the single most likely way a deploy fails, so the dry run must be able to see it.
# The probe file is created and deleted immediately.
$probe = Join-Path $Target ('.deploy_probe_{0}.tmp' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    [System.IO.File]::WriteAllText($probe, 'probe')
    Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
    Write-Ok 'write access confirmed'
} catch {
    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }
    Fail ("no write access to $Target as $env:USERDOMAIN\$env:USERNAME -- " +
          "$($_.Exception.Message) Nothing has been changed. Grant that account write " +
          "permission on the share, or run this as an account that has it.")
}

$prodSettings   = Join-Path $Target 'appsettings.json'
$repoSettings   = Join-Path $PublishDir 'appsettings.json'
$restoreProdCfg = $false

if ((Test-Path -LiteralPath $prodSettings) -and (Test-Path -LiteralPath $repoSettings)) {
    $same = (Get-FileHash -LiteralPath $prodSettings).Hash -eq (Get-FileHash -LiteralPath $repoSettings).Hash
    if ($same) {
        Write-Ok 'appsettings.json identical to the one being deployed'
    } else {
        $diffs = @()
        try {
            $diffs = @(Get-JsonDiffPath -Left (Get-Content -LiteralPath $prodSettings -Raw | ConvertFrom-Json) `
                                        -Right (Get-Content -LiteralPath $repoSettings -Raw | ConvertFrom-Json))
        } catch { $diffs = @('<could not parse one of the files>') }

        Write-Warn2 'appsettings.json DIFFERS between production and this build.'
        Write-Warn2 "differing setting(s) -- names only, values withheld:"
        $diffs | Select-Object -First 40 | ForEach-Object { Write-Warn2 "    $_" }
        if ($diffs.Count -gt 40) { Write-Warn2 "    ... and $($diffs.Count - 40) more" }

        if ($KeepProdSettings)      { $restoreProdCfg = $true; Write-Ok "-KeepProdSettings: production's appsettings.json will be preserved" }
        elseif ($OverwriteSettings) { Write-Warn2 "-OverwriteSettings: production's appsettings.json WILL be replaced" }
        else {
            Fail ("appsettings.json differs and neither -KeepProdSettings nor -OverwriteSettings was given. " +
                  "Production config (connection strings, Platform:EnabledLoaders, per-loader windows) would " +
                  "be silently replaced. Re-run with -KeepProdSettings to deploy binaries only, or " +
                  "-OverwriteSettings to deploy the repo's config. Nothing has been changed.")
        }
    }
} elseif (Test-Path -LiteralPath $prodSettings) {
    Write-Warn2 'target has appsettings.json but the build does not -- it will be archived and not replaced'
}

# ---------------------------------------------------------------- 5. archive
$stamp   = Get-Date -Format 'yyyy_MM_dd_HH_mm_ss'   # HH = 24h: 'hh' collides at 09:00/21:00
$oldDir  = Join-Path $Target 'Old'
$archive = Join-Path $oldDir $stamp

# -Force includes hidden/system entries; 'Old' is excluded so it is never nested in itself.
$existing = @(Get-ChildItem -LiteralPath $Target -Force | Where-Object { $_.Name -ne 'Old' })

Write-Step 'Archiving current contents'
if ($existing.Count -eq 0) {
    Write-Ok 'target is empty (or holds only Old\) -- nothing to archive'
} elseif ($DryRun) {
    Write-Warn2 "would create $archive and move $($existing.Count) entr(ies) into it:"
    $existing | ForEach-Object { Write-Warn2 "    $($_.Name)" }
} else {
    $stash = $null
    if ($restoreProdCfg) {
        $stash = Join-Path ([System.IO.Path]::GetTempPath()) ("appsettings.prod.$stamp.json")
        Copy-Item -LiteralPath $prodSettings -Destination $stash -Force
    }
    New-Item -ItemType Directory -Path $archive -Force | Out-Null   # -Force is safe on a directory
    $moved = 0
    foreach ($e in $existing) {
        try {
            Move-Item -LiteralPath $e.FullName -Destination $archive -ErrorAction Stop
            $moved++
        } catch {
            Fail ("stopped after moving $moved of $($existing.Count) entr(ies): could not move '$($e.Name)' " +
                  "($($_.Exception.Message)). Usually a locked file -- the loader is running, or the folder " +
                  "is open somewhere. NOTHING has been overwritten; the moved entries are in $archive. " +
                  "Stop whatever holds the files and re-run.")
        }
    }
    Write-Ok "archived $moved entr(ies) to $archive"
}

# ---------------------------------------------------------------- 6. deploy
$sourceFiles = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Force)
Write-Step 'Deploying'
if ($DryRun) {
    Write-Warn2 "would copy $($sourceFiles.Count) file(s) from $PublishDir to $Target"
    Write-Host ''
    Write-Host 'DRY RUN complete -- nothing was changed.' -ForegroundColor Yellow
    exit 0
}

Copy-Item -Path (Join-Path $PublishDir '*') -Destination $Target -Recurse -Force
if ($restoreProdCfg -and $stash) {
    Copy-Item -LiteralPath $stash -Destination $prodSettings -Force
    Remove-Item -LiteralPath $stash -Force -ErrorAction SilentlyContinue
    Write-Ok "restored production's appsettings.json (the original is also in the archive)"
}

# ---------------------------------------------------------------- 7. verify
# Checked per source file by RELATIVE path rather than by counting files at the
# target: the target also holds Old\, and a prefix/wildcard filter to exclude it is
# fragile (8.3 short paths, and -like treats [] as a character class), which made an
# earlier version miscount the archive as deployed output.
Write-Step 'Verifying'
if (-not (Test-Path -LiteralPath (Join-Path $Target 'DataLoader.Host.exe'))) {
    Fail "DataLoader.Host.exe is missing from $Target after the copy."
}

$missing = @()
foreach ($f in $sourceFiles) {
    $rel = $f.FullName.Substring($PublishDir.Length).TrimStart('\')
    if (-not (Test-Path -LiteralPath (Join-Path $Target $rel))) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    $missing | Select-Object -First 15 | ForEach-Object { Write-Warn2 "    missing: $_" }
    Fail "$($missing.Count) of $($sourceFiles.Count) published file(s) did not arrive at the target."
}
Write-Ok "all $($sourceFiles.Count) published file(s) verified at the target, DataLoader.Host.exe present"

Write-Host ''
Write-Host '=========================================================' -ForegroundColor Green
Write-Host ' DEPLOY OK' -ForegroundColor Green
Write-Host "   files    : $($sourceFiles.Count)"
if ($existing.Count -gt 0) {
    Write-Host "   archive  : $archive"
    Write-Host "   rollback : remove everything except Old\ from the target, then copy"
    Write-Host "              the archive's contents back over it."
} else {
    Write-Host '   archive  : nothing to archive (target was empty)'
}
Write-Host '=========================================================' -ForegroundColor Green
exit 0
