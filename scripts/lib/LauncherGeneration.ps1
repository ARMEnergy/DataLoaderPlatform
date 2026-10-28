<#
    Function-only library -- NO param() block and NO top-level executable logic on
    purpose. Dot-sourcing a script that HAS a param() block runs that block in the
    CALLER's scope (dot-sourcing merges scope, it does not create a child scope), which
    silently reassigns any same-named variable the caller already had -- this bit
    Migrate-ToReleases.ps1 during development: dot-sourcing Generate-Launchers.ps1
    (which has its own -PlatformRoot and -DryRun parameters) clobbered the CALLER's
    -PlatformRoot back to production's default and would have silently turned a
    -DryRun migration into a real one. Splitting the reusable functions out into this
    parameter-free file is what makes dot-sourcing them safe.

    Used by:
      - Generate-Launchers.ps1   (the CLI-facing script; dot-sources this, then adds
                                   its own param block and entry point)
      - Migrate-ToReleases.ps1   (dot-sources this directly for step 3)
      - Deploy-Release.ps1       (its -SelfTest dot-sources this directly for the
                                   launcher-behaviour test cases)
#>

# ---------------------------------------------------------------------------------------
# Writes the template VERBATIM to $DestinationPath -- the one place a launcher is ever
# written. No per-destination substitution happens here or anywhere else; see the
# template's own header comment (scripts\templates\Launcher.cmd.template) for why none
# is needed -- the "%~n0" trick means the identical bytes work as every destination name.
# ---------------------------------------------------------------------------------------
function Set-GeneratedLauncher {
    param(
        [Parameter(Mandatory)] [string] $TemplatePath,
        [Parameter(Mandatory)] [string] $DestinationPath,
        [switch] $DryRun
    )
    if (-not (Test-Path -LiteralPath $TemplatePath -PathType Leaf)) {
        throw "Launcher template not found: $TemplatePath"
    }
    if ($DryRun) {
        return [pscustomobject]@{ Path = $DestinationPath; Written = $false }
    }
    Copy-Item -LiteralPath $TemplatePath -Destination $DestinationPath -Force
    return [pscustomobject]@{ Path = $DestinationPath; Written = $true }
}

# ---------------------------------------------------------------------------------------
# Regenerates every launcher named in $Names (".cmd" appended) under $PlatformRoot from
# $TemplatePath. $Names defaults to "whatever *.cmd already exists there" when not given,
# so a bare call safely regenerates exactly what is already there rather than inventing a
# launcher for a loader that doesn't have one yet. "Watchdog" is always force-included.
# ---------------------------------------------------------------------------------------
function Invoke-GenerateLaunchers {
    param(
        [Parameter(Mandatory)] [string]   $PlatformRoot,
        [Parameter(Mandatory)] [string]   $TemplatePath,
        [string[]] $Names,
        [switch]   $DryRun
    )
    if (-not $Names -or $Names.Count -eq 0) {
        $Names = @(Get-ChildItem -LiteralPath $PlatformRoot -Filter '*.cmd' -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.BaseName })
    }
    if ($Names -notcontains 'Watchdog') { $Names = @($Names) + @('Watchdog') }
    $Names = @($Names | Select-Object -Unique)

    $results = @()
    foreach ($name in $Names) {
        $dest = Join-Path $PlatformRoot ($name + '.cmd')
        $r = Set-GeneratedLauncher -TemplatePath $TemplatePath -DestinationPath $dest -DryRun:$DryRun
        $results += [pscustomobject]@{ Name = $name; Path = $dest; Written = $r.Written }
    }
    return $results
}
