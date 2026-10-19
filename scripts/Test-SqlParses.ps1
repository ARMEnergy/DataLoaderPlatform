<#
.SYNOPSIS
    Parse-checks .sql files with Microsoft's T-SQL ScriptDom parser.

.DESCRIPTION
    A green `dotnet build` and a green `dotnet test` say NOTHING about whether a .sql file
    even parses - nothing in the build pipeline compiles SQL. CLAUDE.md therefore requires
    SQL changes to be parse-checked before being reported as done, and this is the tool
    that does it. There is no sqlcmd or sqlpackage on these machines, and parsing needs no
    server anyway.

    ScriptDom catches syntax errors, unterminated strings, bad batch structure and
    mistyped keywords. It does NOT resolve object names, so a reference to a table that
    does not exist still parses - this is a syntax gate, not a deployment gate.

.PARAMETER Path
    Files or directories to check. Directories are searched recursively for *.sql.
    Defaults to the repository's sql/ directory.

.PARAMETER SqlVersion
    ScriptDom parser version. Defaults to Sql160 (SQL Server 2022).

.PARAMETER RequireUseDatabase
    Also assert the repo rule from .claude/skills/sql-use-database: every file must
    declare `USE <DatabaseName>` before its first statement that touches a database
    object. On by default.

.EXAMPLE
    .\scripts\Test-SqlParses.ps1
    .\scripts\Test-SqlParses.ps1 -Path sql\NUCORE
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [string]   $SqlVersion = 'Sql160',
    [bool]     $RequireUseDatabase = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Path -or $Path.Count -eq 0) { $Path = @((Join-Path $repoRoot 'sql')) }

# ---------------------------------------------------------------------------------------
# Locate ScriptDom.
#
# Preferred source is the NuGet package restored into the user's package cache by any
# project that references it; failing that, the DacFx / SSMS install that ships the same
# assembly. Both are checked because build agents and developer boxes differ.
# ---------------------------------------------------------------------------------------
function Resolve-ScriptDomAssembly {
    $candidates = @()

    $nuget = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.sqlserver.transactsql.scriptdom'
    if (Test-Path $nuget) {
        $candidates += Get-ChildItem -Path $nuget -Recurse -Filter 'Microsoft.SqlServer.TransactSql.ScriptDom.dll' -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending
    }

    foreach ($root in @(
        "${env:ProgramFiles}\Microsoft SQL Server",
        "${env:ProgramFiles(x86)}\Microsoft SQL Server",
        "${env:ProgramFiles}\Microsoft Visual Studio",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) {
        if (Test-Path $root) {
            $candidates += Get-ChildItem -Path $root -Recurse -Filter 'Microsoft.SqlServer.TransactSql.ScriptDom.dll' -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending
        }
    }

    # Two traps here, both of which produce a bogus "ScriptDom not found" while the real
    # assembly sits in the cache:
    #
    #  1. The localised satellites are named
    #     <culture>\Microsoft.SqlServer.TransactSql.ScriptDom.resources.dll. They load
    #     without complaint and contain no parser type.
    #  2. The package ships several target frameworks and the cache usually holds several
    #     package VERSIONS. Windows PowerShell 5.1 runs on .NET Framework and cannot load
    #     the netstandard2.1 / net8.0 flavours; PowerShell 7 is the other way round. Once
    #     one flavour of an assembly NAME is loaded, later Add-Type calls for a different
    #     flavour do not replace it, so guessing wrong is not recoverable in-process.
    #
    # So: filter to the real DLL, then rank by the TFM this host can actually load.
    $preferred = if ($PSVersionTable.PSEdition -eq 'Core') {
        @('net8.0', 'net6.0', 'netstandard2.1', 'netstandard2.0')
    } else {
        @('net472', 'net462', 'netstandard2.0')
    }

    $ranked = $candidates |
        Where-Object { $_.Name -eq 'Microsoft.SqlServer.TransactSql.ScriptDom.dll' } |
        ForEach-Object {
            $tfm = Split-Path -Leaf (Split-Path -Parent $_.FullName)
            $rank = [array]::IndexOf($preferred, $tfm)
            if ($rank -lt 0) { $rank = 99 }
            [pscustomobject]@{ Path = $_.FullName; Rank = $rank; Tfm = $tfm }
        } |
        Where-Object { $_.Rank -lt 99 } |
        Sort-Object Rank, @{ Expression = { $_.Path }; Descending = $true }

    foreach ($c in $ranked) {
        try {
            Add-Type -Path $c.Path -ErrorAction Stop
            # Only accept it once the parser type is actually reachable. A reference-only
            # assembly can load and still leave the type unusable.
            if ("Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser" -as [type]) { return $c.Path }
        } catch { }
    }
    return $null
}

$asm = Resolve-ScriptDomAssembly
if (-not $asm) {
    Write-Output 'ScriptDom not found. Install it with:'
    Write-Output '    dotnet tool install --global Microsoft.SqlPackage'
    Write-Output '  or add a PackageReference to Microsoft.SqlServer.TransactSql.ScriptDom and build once.'
    exit 2
}
Write-Verbose "ScriptDom: $asm"

$parserType = "Microsoft.SqlServer.TransactSql.ScriptDom.T${SqlVersion}Parser"
if (-not ($parserType -as [type])) { throw "Unknown ScriptDom parser version '$SqlVersion'." }

# ---------------------------------------------------------------------------------------
# Collect files
# ---------------------------------------------------------------------------------------
$files = @()
foreach ($p in $Path) {
    $full = if ([System.IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $repoRoot $p }
    if (Test-Path $full -PathType Container) {
        $files += Get-ChildItem -Path $full -Recurse -Filter '*.sql' | Select-Object -ExpandProperty FullName
    }
    elseif (Test-Path $full -PathType Leaf) { $files += $full }
    else { throw "Path not found: $full" }
}
$files = $files | Sort-Object -Unique
if ($files.Count -eq 0) { Write-Output 'No .sql files found.'; exit 0 }

# ---------------------------------------------------------------------------------------
# Parse
# ---------------------------------------------------------------------------------------
$failed = 0
$checked = 0

foreach ($file in $files) {
    $checked++
    $text = Get-Content -Path $file -Raw
    $parser = New-Object $parserType -ArgumentList $true   # initialQuotedIdentifiers
    $reader = New-Object System.IO.StringReader($text)
    $errors = $null
    $null = $parser.Parse($reader, [ref]$errors)
    $reader.Dispose()

    $rel = $file.Replace($repoRoot, '').TrimStart('\')

    if ($errors -and $errors.Count -gt 0) {
        $failed++
        Write-Output "FAIL  $rel"
        foreach ($e in $errors) {
            Write-Output ("        line {0} col {1}: {2} (error {3})" -f $e.Line, $e.Column, $e.Message, $e.Number)
        }
        continue
    }

    if ($RequireUseDatabase) {
        # The rule is about the FIRST statement that touches a database object, so a
        # leading comment block or a CREATE DATABASE guard before the USE is fine.
        $stripped = [regex]::Replace($text, '(?s)/\*.*?\*/', '')
        $stripped = [regex]::Replace($stripped, '(?m)^\s*--.*$', '')
        if ($stripped -notmatch '(?im)^\s*USE\s+[\[\w]') {
            $failed++
            Write-Output "FAIL  $rel"
            Write-Output '        no USE <DatabaseName> statement (see .claude/skills/sql-use-database)'
            continue
        }
    }

    Write-Output "ok    $rel"
}

Write-Output ''
Write-Output "$checked file(s) checked, $failed failed."
if ($failed -gt 0) { exit 1 }
exit 0
