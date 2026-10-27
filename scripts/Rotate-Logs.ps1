<#
.SYNOPSIS
    Rotates, compresses and expires the production loader logs under
    C:\ETL_Tools\DataLoaderPlatform\Log\ -- nothing else does.

.DESCRIPTION
    Standalone -- needs nothing but PowerShell. Windows PowerShell 5.1 compatible
    (no &&/||, no ternary, no null-coalescing). Companion to Publish-ToProd.ps1;
    follows the same comment-based-help / Write-Ok / Write-Warn2 / Fail style.

    Each loader's .cmd launcher does:
        DataLoader.Host.exe "<LoaderName>" >> "<LOGFILE>" 2>&1
    so cmd.exe holds an OPEN APPEND HANDLE on the active log for the entire run --
    minutes to hours -- and several loaders run concurrently. These logs are the
    ONLY place stack traces exist (the app logs to console only, and core.LoadLog
    stores just ex.Message), yet nothing has ever rotated them. This script does,
    in three steps, every time it runs:

      1. Rotate.   Any *.log in -LogPath at or above -ThresholdMB is renamed into
                   Archive\<basename>_yyyy_MM_dd_HH_mm_ss.log (Archive\ created if
                   needed). A file cmd.exe still has open cannot be renamed -- that
                   throws, and it is the ORDINARY steady state, not an error: it is
                   caught, logged at information level, and tried again next pass.
                   The one loud exception is a file still >= 3x -ThresholdMB after a
                   failed attempt -- that means it has been locked for a long time
                   (or something else is wrong), and silently waiting for "next
                   pass" forever would hide exactly the kind of problem this script
                   exists to surface.
      2. Compress. Any Archive\*.log older than -CompressAfterDays becomes a .zip
                   next to it (these logs compress roughly 15x); the .log is only
                   deleted once the .zip verifiably exists and is non-empty, so a
                   half-written zip never costs the only copy of a log.
      3. Delete.   Any Archive\*.zip or Archive\*.log older than -DeleteAfterDays
                   is removed.

    Exit codes: 0 for a normal pass, including one where every candidate file was
    locked (that is success, not failure -- see above). Non-zero ONLY if the script
    itself could not function, i.e. -LogPath does not exist or cannot be read.

.PARAMETER LogPath
    Directory holding the per-loader .log files. Defaults to production's path.

.PARAMETER ThresholdMB
    A *.log at or above this size is rotated into Archive\. Default 25.

.PARAMETER CompressAfterDays
    An archived .log older than this (by LastWriteTime) is zipped. Default 7.

.PARAMETER DeleteAfterDays
    An archived .zip or .log older than this (by LastWriteTime) is removed. Default 90.

.PARAMETER DryRun
    Report every rotate/compress/delete candidate and change nothing on disk.

.PARAMETER SelfTest
    Run the built-in self-test instead of a real pass: fabricates files of known
    size/age in a throwaway temp directory, calls the SAME Invoke-LogRotationPass /
    Invoke-RotateStep / Invoke-CompressStep / Invoke-DeleteStep functions used by a
    real run (not a reimplementation), asserts outcomes, prints one PASS/FAIL line
    per case, cleans up, and exits non-zero if anything failed. Ignores every other
    parameter -- it never touches -LogPath.

.EXAMPLE
    .\scripts\Rotate-Logs.ps1
    A real pass with every default.

.EXAMPLE
    .\scripts\Rotate-Logs.ps1 -DryRun
    Show what a real pass would do, touching nothing.

.EXAMPLE
    .\scripts\Rotate-Logs.ps1 -SelfTest
    Run the self-test suite against a temp directory.
#>
[CmdletBinding()]
param(
    [string] $LogPath           = 'C:\ETL_Tools\DataLoaderPlatform\Log',
    [double] $ThresholdMB       = 25,
    [int]    $CompressAfterDays = 7,
    [int]    $DeleteAfterDays   = 90,
    [switch] $DryRun,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Step  ([string]$m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    ([string]$m) { Write-Host "    $m" -ForegroundColor Green }
function Write-Info  ([string]$m) { Write-Host "    $m" -ForegroundColor DarkGray }
function Write-Warn2 ([string]$m) { Write-Host "    $m" -ForegroundColor Yellow }
function Fail        ([string]$m) { Write-Host ''; Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }

function Get-FriendlySize {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return '{0:N2} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N2} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N2} KB' -f ($Bytes / 1KB) }
    return "$Bytes B"
}

# ---------------------------------------------------------------------------------------
# Step 1: rotate oversized *.log files out of $LogPath into $ArchiveDir.
# ---------------------------------------------------------------------------------------
function Invoke-RotateStep {
    param(
        [Parameter(Mandatory)] [string] $LogPath,
        [Parameter(Mandatory)] [string] $ArchiveDir,
        [double] $ThresholdMB,
        [switch] $DryRun
    )

    $thresholdBytes = [long]($ThresholdMB * 1MB)
    $loudThresholdBytes = $thresholdBytes * 3
    $rotated = 0
    $skippedLocked = 0
    $loudWarningFiles = @()

    $candidates = @(Get-ChildItem -LiteralPath $LogPath -Filter '*.log' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -ge $thresholdBytes })

    if ($candidates.Count -gt 0 -and -not (Test-Path -LiteralPath $ArchiveDir)) {
        if ($DryRun) {
            Write-Ok '[DryRun] would create Archive\'
        } else {
            New-Item -ItemType Directory -Path $ArchiveDir -Force | Out-Null
        }
    }

    # One stamp for the whole step (not per file) -- matches Publish-ToProd.ps1's Old\
    # timestamp, which is also captured once per run rather than per moved item.
    $stamp = Get-Date -Format 'yyyy_MM_dd_HH_mm_ss'

    foreach ($f in $candidates) {
        $destName = '{0}_{1}{2}' -f $f.BaseName, $stamp, $f.Extension
        $dest = Join-Path $ArchiveDir $destName

        if ($DryRun) {
            Write-Ok ("[DryRun] would rotate {0} ({1}) -> Archive\{2}" -f $f.Name, (Get-FriendlySize $f.Length), $destName)
            $rotated++
            continue
        }

        try {
            # cmd.exe's ">> file.log" holds an open append handle for the whole run of a
            # loader -- minutes to hours -- and several loaders run concurrently. Moving a
            # file someone else has open throws. That is the EXPECTED steady state for an
            # active log, not a bug: skip it -- it rotates on a later pass once the run
            # that holds it finishes.
            Move-Item -LiteralPath $f.FullName -Destination $dest -ErrorAction Stop
            Write-Ok ("rotated {0} ({1}) -> Archive\{2}" -f $f.Name, (Get-FriendlySize $f.Length), $destName)
            $rotated++
        } catch {
            $skippedLocked++
            Write-Info ("skip (locked): {0} -- {1}" -f $f.Name, $_.Exception.Message)

            if ($f.Length -ge $loudThresholdBytes) {
                # Still this large after an attempted rotation means it has been
                # continuously locked across many passes, or something else is wrong --
                # the ordinary "it'll get it next time" skip would otherwise hide that
                # silently forever.
                $msg = "'{0}' is {1} (>= 3x the {2} threshold) and could NOT be rotated -- continuously locked, or something else is wrong" -f $f.Name, (Get-FriendlySize $f.Length), (Get-FriendlySize $thresholdBytes)
                Write-Warn2 $msg
                $loudWarningFiles += $f.Name
            }
        }
    }

    [pscustomobject]@{
        Rotated          = $rotated
        SkippedLocked    = $skippedLocked
        LoudWarningFiles = $loudWarningFiles
    }
}

# ---------------------------------------------------------------------------------------
# Step 2: zip any archived .log older than $CompressAfterDays; delete the .log only once
# the .zip verifiably exists and is non-empty.
# ---------------------------------------------------------------------------------------
function Invoke-CompressStep {
    param(
        [Parameter(Mandatory)] [string] $ArchiveDir,
        [int] $CompressAfterDays,
        [switch] $DryRun
    )

    $compressed = 0
    $bytesReclaimed = 0L
    if (-not (Test-Path -LiteralPath $ArchiveDir)) {
        return [pscustomobject]@{ Compressed = $compressed; BytesReclaimed = $bytesReclaimed }
    }

    $cutoff = (Get-Date).AddDays(-$CompressAfterDays)
    $candidates = @(Get-ChildItem -LiteralPath $ArchiveDir -Filter '*.log' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -le $cutoff })

    foreach ($f in $candidates) {
        $zipPath = [System.IO.Path]::ChangeExtension($f.FullName, '.zip')

        if ($DryRun) {
            Write-Ok ("[DryRun] would compress {0} -> {1}, then remove the .log" -f $f.Name, (Split-Path -Leaf $zipPath))
            $compressed++
            continue
        }

        try {
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction Stop }
            Compress-Archive -LiteralPath $f.FullName -DestinationPath $zipPath -CompressionLevel Optimal -ErrorAction Stop

            # Only delete the source once the zip verifiably exists AND is non-empty --
            # a half-written or zero-byte zip must never cost us the only copy of a log.
            $zipInfo = Get-Item -LiteralPath $zipPath -ErrorAction Stop
            if ($zipInfo.Length -le 0) { throw 'Compress-Archive produced a zero-byte zip' }

            $savedBytes = $f.Length - $zipInfo.Length
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            Write-Ok ("compressed {0} ({1} -> {2})" -f $f.Name, (Get-FriendlySize $f.Length), (Get-FriendlySize $zipInfo.Length))
            $compressed++
            if ($savedBytes -gt 0) { $bytesReclaimed += $savedBytes }
        } catch {
            Write-Info ("skip (could not compress): {0} -- {1}" -f $f.Name, $_.Exception.Message)
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }
        }
    }

    [pscustomobject]@{ Compressed = $compressed; BytesReclaimed = $bytesReclaimed }
}

# ---------------------------------------------------------------------------------------
# Step 3: delete archived .zip/.log older than $DeleteAfterDays.
# ---------------------------------------------------------------------------------------
function Invoke-DeleteStep {
    param(
        [Parameter(Mandatory)] [string] $ArchiveDir,
        [int] $DeleteAfterDays,
        [switch] $DryRun
    )

    $deleted = 0
    $bytesReclaimed = 0L
    if (-not (Test-Path -LiteralPath $ArchiveDir)) {
        return [pscustomobject]@{ Deleted = $deleted; BytesReclaimed = $bytesReclaimed }
    }

    $cutoff = (Get-Date).AddDays(-$DeleteAfterDays)
    $candidates = @(Get-ChildItem -LiteralPath $ArchiveDir -Filter '*.zip' -File -ErrorAction SilentlyContinue) +
                  @(Get-ChildItem -LiteralPath $ArchiveDir -Filter '*.log' -File -ErrorAction SilentlyContinue)
    $candidates = @($candidates | Where-Object { $_.LastWriteTime -le $cutoff })

    foreach ($f in $candidates) {
        if ($DryRun) {
            Write-Ok ("[DryRun] would delete {0} ({1})" -f $f.Name, (Get-FriendlySize $f.Length))
            $deleted++
            $bytesReclaimed += $f.Length
            continue
        }
        try {
            $size = $f.Length
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            Write-Ok ("deleted {0} ({1}, past the {2}-day retention)" -f $f.Name, (Get-FriendlySize $size), $DeleteAfterDays)
            $deleted++
            $bytesReclaimed += $size
        } catch {
            Write-Info ("skip (could not delete): {0} -- {1}" -f $f.Name, $_.Exception.Message)
        }
    }

    [pscustomobject]@{ Deleted = $deleted; BytesReclaimed = $bytesReclaimed }
}

# ---------------------------------------------------------------------------------------
# Runs all three steps against one log directory and returns a summary. This is the
# function both the real pass AND -SelfTest call, so the self-test exercises the exact
# code path production uses, not a reimplementation of it.
# ---------------------------------------------------------------------------------------
function Invoke-LogRotationPass {
    param(
        [Parameter(Mandatory)] [string] $LogPath,
        [double] $ThresholdMB       = 25,
        [int]    $CompressAfterDays = 7,
        [int]    $DeleteAfterDays   = 90,
        [switch] $DryRun
    )

    $archiveDir = Join-Path $LogPath 'Archive'

    Write-Step 'Rotate'
    $rotate = Invoke-RotateStep -LogPath $LogPath -ArchiveDir $archiveDir -ThresholdMB $ThresholdMB -DryRun:$DryRun
    if ($rotate.Rotated -eq 0 -and $rotate.SkippedLocked -eq 0) { Write-Ok 'nothing at or above threshold' }

    Write-Step 'Compress'
    $compress = Invoke-CompressStep -ArchiveDir $archiveDir -CompressAfterDays $CompressAfterDays -DryRun:$DryRun
    if ($compress.Compressed -eq 0) { Write-Ok 'nothing old enough to compress' }

    Write-Step 'Delete'
    $delete = Invoke-DeleteStep -ArchiveDir $archiveDir -DeleteAfterDays $DeleteAfterDays -DryRun:$DryRun
    if ($delete.Deleted -eq 0) { Write-Ok 'nothing old enough to delete' }

    [pscustomobject]@{
        Rotated          = $rotate.Rotated
        SkippedLocked    = $rotate.SkippedLocked
        LoudWarningFiles = $rotate.LoudWarningFiles
        Compressed       = $compress.Compressed
        Deleted          = $delete.Deleted
        BytesReclaimed   = [long]($compress.BytesReclaimed + $delete.BytesReclaimed)
    }
}

# =========================================================================================
# Self-test
# =========================================================================================
function New-SelfTestFile {
    param([string]$Path, [long]$SizeBytes, $LastWriteTime)
    $bytes = New-Object byte[] $SizeBytes
    [System.IO.File]::WriteAllBytes($Path, $bytes)
    if ($LastWriteTime) { (Get-Item -LiteralPath $Path).LastWriteTime = $LastWriteTime }
}

function Invoke-SelfTest {
    Write-Host ''
    Write-Host '=========================================================' -ForegroundColor White
    Write-Host ' Rotate-Logs.ps1 self-test' -ForegroundColor White
    Write-Host ' (fabricates files in a throwaway temp dir, runs the SAME' -ForegroundColor White
    Write-Host '  Invoke-LogRotationPass used by a real pass, asserts,' -ForegroundColor White
    Write-Host '  cleans up)' -ForegroundColor White
    Write-Host '=========================================================' -ForegroundColor White

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('RotateLogsSelfTest_{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    $script:selfTestPass = 0
    $script:selfTestFail = 0

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

    try {
        Invoke-Case 'rotate: file at/above threshold is renamed into Archive with the yyyy_MM_dd_HH_mm_ss stamp' {
            $dir = Join-Path $root 'case1'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $log = Join-Path $dir 'Foo.log'
            New-SelfTestFile -Path $log -SizeBytes 2MB
            $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
            if ($r.Rotated -ne 1) { throw "expected 1 rotated, got $($r.Rotated)" }
            if (Test-Path -LiteralPath $log) { throw 'original file still present at source' }
            $archived = @(Get-ChildItem -LiteralPath (Join-Path $dir 'Archive') -Filter 'Foo_*.log' -File)
            if ($archived.Count -ne 1) { throw "expected exactly 1 archived file, found $($archived.Count)" }
            if ($archived[0].Name -notmatch '^Foo_\d{4}_\d{2}_\d{2}_\d{2}_\d{2}_\d{2}\.log$') {
                throw "archive name '$($archived[0].Name)' does not match the yyyy_MM_dd_HH_mm_ss convention"
            }
        }

        Invoke-Case 'rotate: file under threshold is left alone' {
            $dir = Join-Path $root 'case2'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $log = Join-Path $dir 'Small.log'
            New-SelfTestFile -Path $log -SizeBytes 200KB
            $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
            if ($r.Rotated -ne 0) { throw "expected 0 rotated, got $($r.Rotated)" }
            if (-not (Test-Path -LiteralPath $log)) { throw 'file was moved even though it is under threshold' }
        }

        Invoke-Case 'rotate: a locked file (open write handle) is skipped, not a failure, and left in place' {
            $dir = Join-Path $root 'case3'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $log = Join-Path $dir 'Locked.log'
            New-SelfTestFile -Path $log -SizeBytes 2MB
            # Simulates cmd.exe's open append handle (">> file.log") for the duration of a
            # loader run -- this is the single most common real-world condition this
            # script has to deal with gracefully.
            $fs = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            try {
                $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
                if ($r.SkippedLocked -ne 1) { throw "expected 1 skipped-locked, got $($r.SkippedLocked)" }
                if ($r.Rotated -ne 0) { throw "expected 0 rotated, got $($r.Rotated)" }
            } finally {
                $fs.Close()
            }
            if (-not (Test-Path -LiteralPath $log)) { throw 'locked file was moved/deleted -- it must be left exactly where it was' }
            $archiveDir = Join-Path $dir 'Archive'
            if ((Test-Path -LiteralPath $archiveDir) -and (@(Get-ChildItem -LiteralPath $archiveDir -ErrorAction SilentlyContinue).Count -gt 0)) {
                throw 'Archive unexpectedly received an entry for a locked file'
            }
        }

        Invoke-Case 'rotate: a locked file >= 3x threshold triggers the loud needs-attention warning' {
            $dir = Join-Path $root 'case4'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $log = Join-Path $dir 'StuckBig.log'
            New-SelfTestFile -Path $log -SizeBytes 4MB   # ThresholdMB 1 -> loud at >= 3MB
            $fs = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            try {
                $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
                if ($r.LoudWarningFiles -notcontains 'StuckBig.log') { throw "expected StuckBig.log in LoudWarningFiles, got: $($r.LoudWarningFiles -join ', ')" }
            } finally {
                $fs.Close()
            }
        }

        Invoke-Case 'compress: an archived .log older than CompressAfterDays becomes a non-empty .zip and the .log is removed' {
            $dir = Join-Path $root 'case5'; $archiveDir = Join-Path $dir 'Archive'
            New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
            $log = Join-Path $archiveDir 'Old_2026_01_01_00_00_00.log'
            New-SelfTestFile -Path $log -SizeBytes 500KB -LastWriteTime (Get-Date).AddDays(-10)
            $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
            if ($r.Compressed -ne 1) { throw "expected 1 compressed, got $($r.Compressed)" }
            if (Test-Path -LiteralPath $log) { throw '.log was not removed after compression' }
            $zip = Join-Path $archiveDir 'Old_2026_01_01_00_00_00.zip'
            if (-not (Test-Path -LiteralPath $zip)) { throw '.zip was not created' }
            if ((Get-Item -LiteralPath $zip).Length -le 0) { throw '.zip is zero bytes' }
        }

        Invoke-Case 'delete: a .zip older than DeleteAfterDays is removed' {
            $dir = Join-Path $root 'case6'; $archiveDir = Join-Path $dir 'Archive'
            New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
            $zip = Join-Path $archiveDir 'Ancient_2025_01_01_00_00_00.zip'
            New-SelfTestFile -Path $zip -SizeBytes 10KB -LastWriteTime (Get-Date).AddDays(-100)
            $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
            if ($r.Deleted -ne 1) { throw "expected 1 deleted, got $($r.Deleted)" }
            if (Test-Path -LiteralPath $zip) { throw '.zip was not deleted' }
        }

        Invoke-Case 'dry run: rotate/compress/delete candidates are all reported but NOTHING changes on disk' {
            $dir = Join-Path $root 'case7'; $archiveDir = Join-Path $dir 'Archive'
            New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
            $big    = Join-Path $dir 'Big.log'
            $oldLog = Join-Path $archiveDir 'OldArch_2026_01_01_00_00_00.log'
            $oldZip = Join-Path $archiveDir 'OldZip_2025_01_01_00_00_00.zip'
            New-SelfTestFile -Path $big    -SizeBytes 2MB
            New-SelfTestFile -Path $oldLog -SizeBytes 500KB -LastWriteTime (Get-Date).AddDays(-10)
            New-SelfTestFile -Path $oldZip -SizeBytes 10KB  -LastWriteTime (Get-Date).AddDays(-100)

            $beforeBig    = (Get-Item -LiteralPath $big).Length
            $beforeOldLog = (Get-Item -LiteralPath $oldLog).Length
            $beforeOldZip = (Get-Item -LiteralPath $oldZip).Length

            $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90 -DryRun
            if ($r.Rotated -eq 0 -or $r.Compressed -eq 0 -or $r.Deleted -eq 0) {
                throw "dry run should still REPORT all three actions (rotated=$($r.Rotated) compressed=$($r.Compressed) deleted=$($r.Deleted))"
            }
            if (-not (Test-Path -LiteralPath $big))    { throw 'dry run moved the oversized log' }
            if (-not (Test-Path -LiteralPath $oldLog)) { throw 'dry run removed the old archived log' }
            if (-not (Test-Path -LiteralPath $oldZip)) { throw 'dry run deleted the old zip' }
            if ((Get-Item -LiteralPath $big).Length    -ne $beforeBig)    { throw 'Big.log size changed under dry run' }
            if ((Get-Item -LiteralPath $oldLog).Length -ne $beforeOldLog) { throw 'OldArch log size changed under dry run' }
            if ((Get-Item -LiteralPath $oldZip).Length -ne $beforeOldZip) { throw 'OldZip size changed under dry run' }
            $archEntries = @(Get-ChildItem -LiteralPath $archiveDir)
            if ($archEntries.Count -ne 2) { throw "dry run changed the Archive entry count (expected 2, found $($archEntries.Count))" }
        }

        Invoke-Case 'archive handling: a missing Archive folder and an empty one both produce zero counts, no error' {
            $dirMissing = Join-Path $root 'case8_missing'; New-Item -ItemType Directory -Path $dirMissing -Force | Out-Null
            $r1 = Invoke-LogRotationPass -LogPath $dirMissing -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
            if ($r1.Compressed -ne 0 -or $r1.Deleted -ne 0) { throw 'expected zero compress/delete activity with no Archive folder at all' }

            $dirEmpty = Join-Path $root 'case8_empty'
            New-Item -ItemType Directory -Path (Join-Path $dirEmpty 'Archive') -Force | Out-Null
            $r2 = Invoke-LogRotationPass -LogPath $dirEmpty -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
            if ($r2.Compressed -ne 0 -or $r2.Deleted -ne 0) { throw 'expected zero compress/delete activity with an empty Archive folder' }
        }

        Invoke-Case 'exit path: a pass that is ENTIRELY locked files still completes without throwing' {
            $dir = Join-Path $root 'case9'; New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $log1 = Join-Path $dir 'A.log'; $log2 = Join-Path $dir 'B.log'
            New-SelfTestFile -Path $log1 -SizeBytes 2MB
            New-SelfTestFile -Path $log2 -SizeBytes 2MB
            $fs1 = [System.IO.File]::Open($log1, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            $fs2 = [System.IO.File]::Open($log2, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            try {
                $r = Invoke-LogRotationPass -LogPath $dir -ThresholdMB 1 -CompressAfterDays 7 -DeleteAfterDays 90
                if ($r.Rotated -ne 0 -or $r.SkippedLocked -ne 2) { throw "expected 0 rotated / 2 skipped-locked, got $($r.Rotated)/$($r.SkippedLocked)" }
            } finally {
                $fs1.Close(); $fs2.Close()
            }
        }
    } finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
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
Write-Host ' DataLoaderPlatform log rotation' -ForegroundColor White
Write-Host "   path       : $LogPath"
Write-Host "   threshold  : $ThresholdMB MB | compress after $CompressAfterDays d | delete after $DeleteAfterDays d"
if ($DryRun) { Write-Host '   MODE       : DRY RUN (nothing will be changed)' -ForegroundColor Yellow }
Write-Host '=========================================================' -ForegroundColor White

# These are the ONLY conditions that exit non-zero: the script itself could not function.
# A pass where every file is locked is a normal, successful pass (see the header comment).
if (-not (Test-Path -LiteralPath $LogPath -PathType Container)) {
    Fail "LogPath not found or not a directory: $LogPath"
}
try {
    $null = Get-ChildItem -LiteralPath $LogPath -ErrorAction Stop
} catch {
    Fail "LogPath exists but could not be read: $LogPath -- $($_.Exception.Message)"
}

$summary = Invoke-LogRotationPass -LogPath $LogPath -ThresholdMB $ThresholdMB -CompressAfterDays $CompressAfterDays -DeleteAfterDays $DeleteAfterDays -DryRun:$DryRun

Write-Host ''
Write-Host '=========================================================' -ForegroundColor Green
Write-Host ' ROTATION PASS COMPLETE' -ForegroundColor Green
Write-Host "   rotated           : $($summary.Rotated)"
Write-Host "   skipped (locked)  : $($summary.SkippedLocked)"
Write-Host "   compressed        : $($summary.Compressed)"
Write-Host "   deleted           : $($summary.Deleted)"
Write-Host "   bytes reclaimed   : $(Get-FriendlySize $summary.BytesReclaimed)"
if ($summary.LoudWarningFiles.Count -gt 0) {
    Write-Host '   STILL OVERSIZED AND LOCKED (needs attention):' -ForegroundColor Yellow
    $summary.LoudWarningFiles | ForEach-Object { Write-Host "     - $_" -ForegroundColor Yellow }
}
if ($DryRun) { Write-Host '   MODE              : DRY RUN -- nothing above actually happened' -ForegroundColor Yellow }
Write-Host '=========================================================' -ForegroundColor Green

exit 0
