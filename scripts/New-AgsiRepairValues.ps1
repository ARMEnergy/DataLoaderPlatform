<#
.SYNOPSIS
    Fills the GENERATED VALUES block of sql\AGSI\hotfixes\2026-11-11_RepairMisattributedRows.sql
    with the correct AGSI values, fetched from the AGSI API.

.DESCRIPTION
    Read-only against every system: it SELECTs the AGSI API key (unless the environment override
    DATALOADER_Loaders__AGSI__ApiKey is set), makes 17 GET requests to agsi.gie.eu, and rewrites
    ONLY the lines between the BEGIN/END GENERATED markers of the local .sql file. It changes nothing
    in any database; the .sql it produces must still be reviewed and run by hand in SSMS.

    🔒 The key is never printed, and an AGSI error body is never printed either (AGSI echoes the
    x-key back in its `message` field).

.EXAMPLE
    .\scripts\New-AgsiRepairValues.ps1
#>
[CmdletBinding()]
param(
    [string]$SqlFile = (Join-Path $PSScriptRoot '..\sql\AGSI\hotfixes\2026-11-11_RepairMisattributedRows.sql'),
    [string]$ParamConnectionString = 'Server=ARMH-OPSDB01;Database=DataLoaderPlatform;Integrated Security=SSPI;TrustServerCertificate=True;',
    [string]$BaseUrl = 'https://agsi.gie.eu'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$inv = [Globalization.CultureInfo]::InvariantCulture

# --- API key: environment override, else core.usp_GetParam (a SELECT) -----------------------
$key = $env:DATALOADER_Loaders__AGSI__ApiKey
if ([string]::IsNullOrWhiteSpace($key)) {
    $conn = New-Object System.Data.SqlClient.SqlConnection $ParamConnectionString
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'core.usp_GetParam'
        $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
        [void]$cmd.Parameters.AddWithValue('@LoaderName', 'AGSI')
        [void]$cmd.Parameters.AddWithValue('@ParamName', 'ApiKey')
        $key = [string]$cmd.ExecuteScalar()
    } finally { $conn.Dispose() }
}
if ([string]::IsNullOrWhiteSpace($key)) { throw 'AGSI API key not found (env override or core.Param).' }

function Format-Dec([object]$v) {
    $s = [string]$v
    if ([string]::IsNullOrWhiteSpace($s) -or $s.Trim() -eq '-') { return 'NULL' }
    $d = [decimal]0
    if (-not [decimal]::TryParse($s.Trim(), [Globalization.NumberStyles]::Float, $inv, [ref]$d)) { return 'NULL' }
    return $d.ToString($inv)
}
function Format-Date([object]$v, [switch]$Nullable) {
    $s = [string]$v; $d = [datetime]::MinValue
    if ([datetime]::TryParseExact($s.Trim(), 'yyyy-MM-dd', $inv, 'None', [ref]$d)) { return "'" + $d.ToString('yyyy-MM-dd') + "'" }
    if ($Nullable) { return 'NULL' }
    throw "Unparseable date '$s'"
}
function Format-DateTime([object]$v) {
    $s = [string]$v; $d = [datetime]::MinValue
    if ([datetime]::TryParseExact($s.Trim(), 'yyyy-MM-dd HH:mm:ss', $inv, 'None', [ref]$d)) { return "'" + $d.ToString('yyyy-MM-ddTHH:mm:ss') + "'" }
    return 'NULL'
}

# --- The 17 rows to repair --------------------------------------------------------------------
$targets = New-Object System.Collections.Generic.List[object]
for ($d = [datetime]'2026-08-30'; $d -le [datetime]'2026-09-14'; $d = $d.AddDays(1)) { $targets.Add(@('eu', $d.ToString('yyyy-MM-dd'))) }
$targets.Add(@('ES', '2026-08-31'))

$rows = New-Object System.Collections.Generic.List[string]
foreach ($t in $targets) {
    $code = $t[0]; $day = $t[1]
    $uri = "$BaseUrl/api?country=$($code.ToLowerInvariant())&date=$day"
    $r = $null
    for ($attempt = 1; ; $attempt++) {
        $r = Invoke-RestMethod -Uri $uri -Headers @{ 'x-key' = $key }
        if (-not $r.error) { break }
        if ($attempt -ge 4) { throw "AGSI kept answering with an error body for $code $day (body not shown: it echoes the key)." }
        Start-Sleep -Seconds (5 * $attempt)
    }
    # Attribute by the record's OWN code: ?country=eu answers [eu, ne].
    $rec = @($r.data | Where-Object { $_.code -ceq $code -and $_.gasDayStart -eq $day })
    if ($rec.Count -ne 1) { throw "Expected exactly one '$code' record for $day, got $($rec.Count)." }
    $x = $rec[0]
    if ((Format-Dec $x.gasInStorage) -eq 'NULL') { throw "'$code' $day has no gasInStorage; refusing to write a NULL repair." }

    $gasDay = if ($r.gas_day) { Format-Date $r.gas_day } else { "'$day'" }
    $status = if ([string]::IsNullOrWhiteSpace([string]$x.status)) { 'N' } else { ([string]$x.status).Trim() }
    if ($status -notmatch '^[A-Z]{1,4}$') { throw "Unexpected status '$status' for $code $day." }
    $gasDayEnd = Format-Date $x.gasDayEnd -Nullable
    if ($gasDayEnd -eq 'NULL') { $gasDayEnd = "'" + ([datetime]$day).AddDays(1).ToString('yyyy-MM-dd') + "'" }

    $rows.Add(("    ('{0}', '{1}', {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12}, {13}, {14}, {15}, {16}, '{17}', {18}, {19})" -f
        $code, $day, $gasDay, (Format-DateTime $x.updatedAt), $gasDayEnd,
        (Format-Dec $x.gasInStorage), (Format-Dec $x.consumption), (Format-Dec $x.consumptionFull),
        (Format-Dec $x.injection), (Format-Dec $x.withdrawal), (Format-Dec $x.netWithdrawal),
        (Format-Dec $x.workingGasVolume), (Format-Dec $x.injectionCapacity), (Format-Dec $x.withdrawalCapacity),
        (Format-Dec $x.contractedCapacity), (Format-Dec $x.availableCapacity), (Format-Dec $x.coveredCapacity),
        $status, (Format-Dec $x.trend), (Format-Dec $x.full)))
    Write-Host ("{0} {1}: gasInStorage={2} full={3}" -f $code, $day, $x.gasInStorage, $x.full)
    Start-Sleep -Milliseconds 1100   # AGSI publishes no limit; the loader paces at 1 req/s
}

# --- Rewrite only the GENERATED block ---------------------------------------------------------
$begin = '-- BEGIN GENERATED VALUES'
$end   = '-- END GENERATED VALUES'
$lines = [IO.File]::ReadAllLines((Resolve-Path $SqlFile))
$b = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l.StartsWith($begin) })
$e = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l.StartsWith($end) })
if ($b -lt 0 -or $e -le $b) { throw "GENERATED markers not found in $SqlFile." }

$block = @(
    "-- generated $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) from $BaseUrl by scripts\New-AgsiRepairValues.ps1",
    'INSERT INTO @Fix (Code, GasDayStart, Gas_Day, UpdatedAt, GasDayEnd, GasInStorage, Consumption, ConsumptionFull,',
    '                  Injection, Withdrawal, NetWithdrawal, WorkingGasVolume, InjectionCapacity, WithdrawalCapacity,',
    '                  ContractedCapacity, AvailableCapacity, CoveredCapacity, [Status], Trend, [Full])',
    'VALUES',
    ($rows -join ",`r`n") + ';'
)
$out = @($lines[0..$b]) + $block + @($lines[$e..($lines.Length - 1)])
[IO.File]::WriteAllLines((Resolve-Path $SqlFile), $out, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote $($rows.Count) row(s) into $SqlFile. Review it, parse-check it (.\scripts\Test-SqlParses.ps1), then run it in SSMS: dry run first, @Commit = 1 to apply."
