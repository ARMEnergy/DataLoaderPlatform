<#
.SYNOPSIS
    One-time repair of the six columns that were NULL on every arm.OfflineEvent row before the
    2026-10-21 nested-`offlineCapacity` fix: PlantId, ProductId, ProductDescription, UnitCapacity,
    OfflineCapacity, OfflineCapacityUOM.

.DESCRIPTION
    WHY AN UPDATE AND NOT A RELOAD. /offlineevents/detail has no date parameter and returns the
    CURRENT state of an event; arm.OfflineEvent's RunDate is STAMPED by the loader, not returned by
    the API. Re-running the loader for a past date would stamp TODAY's payload with an OLD RunDate —
    fabricating history, not recovering it. A true historical reload is impossible.

    It is also unnecessary. The product/capacity group is effectively immutable per EventId:
    measured on the incumbent dbo.OfflineEvent over 16,617 events tracked >= 10 RunDates
    (2026-06-01..2026-08-13), ProductId changed on 2, UnitCapacity on 7, OfflineCapacity on 10 and
    the UOM on 0 — a worst case of ~0.06%. So fetching each EventId's CURRENT values once and
    writing them to every RunDate that event appears on is accurate for ~99.94% of rows.

    Pass 1 (always): read distinct EventIds, fetch their detail records, report coverage, write a CSV.
    Pass 2 (-Apply): stage the fetched values in a #temp table and UPDATE arm.OfflineEvent in one
    transaction. Only rows whose column is currently NULL are touched, so a re-run is idempotent and
    nothing the fixed loader has since written correctly is overwritten.

.PARAMETER Apply
    Perform the UPDATE. WITHOUT this the script only reads the API and writes the CSV.
    *** -Apply WRITES TO PRODUCTION (ARMH-OPSDB01). ***

.EXAMPLE
    .\scripts\Repair-IirOfflineCapacity.ps1
    .\scripts\Repair-IirOfflineCapacity.ps1 -Apply
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Server     = 'ARMH-OPSDB01',
    [string]$Database   = 'IIR',
    [int]   $BatchSize  = 50,     # detail id cap per call (docs/apis/IIR.md 3.2)
    [int]   $RequestsPerSecond = 5,
    [string]$CsvPath    = "$PSScriptRoot\iir-offlinecapacity-repair.csv"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ConnStr = "Server=$Server;Database=$Database;Integrated Security=SSPI;TrustServerCertificate=True;"

function New-Conn { $c = New-Object System.Data.SqlClient.SqlConnection $ConnStr; $c.Open(); $c }

function Get-Param([string]$name) {
    $cs = "Server=$Server;Database=DataLoaderPlatform;Integrated Security=SSPI;TrustServerCertificate=True;"
    $cn = New-Object System.Data.SqlClient.SqlConnection $cs; $cn.Open()
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = 'core.usp_GetParam'
        $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
        [void]$cmd.Parameters.AddWithValue('@LoaderName','IIR')
        [void]$cmd.Parameters.AddWithValue('@ParamName',$name)
        $v = $cmd.ExecuteScalar()
        if ($null -eq $v -or $v -is [DBNull]) { throw "core.Param has no IIR/$name" }
        [string]$v
    } finally { $cn.Close() }
}

# ---------------------------------------------------------------- 1. ids needing repair
Write-Host 'Reading EventIds with NULL capacity columns...' -ForegroundColor Cyan
$ids = New-Object System.Collections.Generic.List[int]
$cn = New-Conn
try {
    $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 600
    $cmd.CommandText = @'
SELECT DISTINCT EventId
FROM arm.OfflineEvent
WHERE OfflineCapacity IS NULL OR UnitCapacity IS NULL OR ProductId IS NULL
   OR ProductDescription IS NULL OR OfflineCapacityUOM IS NULL OR PlantId IS NULL
ORDER BY EventId;
'@
    $rd = $cmd.ExecuteReader()
    while ($rd.Read()) { $ids.Add($rd.GetInt32(0)) }
    $rd.Close()
} finally { $cn.Close() }
Write-Host "  $($ids.Count) distinct EventIds to fetch (~$([math]::Ceiling($ids.Count/$BatchSize)) calls)" -ForegroundColor Green
if ($ids.Count -eq 0) { Write-Host 'Nothing to repair.' -ForegroundColor Yellow; return }

# ---------------------------------------------------------------- 2. token
# NOTE: the JWT comes back in the AUTHORIZATION *response header*, not the body.
# NOTE: Invoke-WebRequest POST to /token answers HTTP 500 here; HttpClient answers 200.
$u = Get-Param 'Username'; $p = Get-Param 'Password'
$cli = New-Object System.Net.Http.HttpClient
$cli.Timeout = [TimeSpan]::FromSeconds(120)

function Get-Jwt {
    $tokUri = "https://api.industrialinfo.com/idb/v2.7/token?username=$([uri]::EscapeDataString($script:u))&password=$([uri]::EscapeDataString($script:p))&tokenLifeTime=1"
    $tResp = $cli.SendAsync((New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $tokUri))).GetAwaiter().GetResult()
    if (-not $tResp.IsSuccessStatusCode) { throw "token mint failed HTTP $([int]$tResp.StatusCode)" }
    $t = (($tResp.Headers.GetValues('AUTHORIZATION') | Select-Object -First 1) -replace '^\s*Bearer\s+','')
    if (-not $t) { throw 'no JWT in AUTHORIZATION header' }
    $t
}
$jwt = Get-Jwt
Write-Host 'JWT acquired.' -ForegroundColor Green

# ---------------------------------------------------------------- 3. fetch detail
$tbl = New-Object System.Data.DataTable
foreach ($c in @(
    @('EventId',[int]), @('PlantId',[int]), @('ProductId',[int]),
    @('ProductDescription',[string]), @('UnitCapacity',[double]),
    @('OfflineCapacity',[double]), @('OfflineCapacityUOM',[string]))) {
    [void]$tbl.Columns.Add($c[0], $c[1])
}

$delayMs = [int](1000 / [Math]::Max(1,$RequestsPerSecond))
$batches = [math]::Ceiling($ids.Count / $BatchSize)
$fetched = 0; $missing = 0; $retries = 0; $sw = [Diagnostics.Stopwatch]::StartNew()

# A 333-call job meets transient faults (socket resets, 502/503/504) and can outlive its token.
# Retry transients with backoff and re-mint once on 401 — the loader's own handlers do both.
function Invoke-Detail([string]$uri, [int]$maxAttempts = 4) {
    for ($a = 1; $a -le $maxAttempts; $a++) {
        try {
            $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $uri)
            [void]$req.Headers.TryAddWithoutValidation('Authorization', "Bearer $script:jwt")
            $resp = $cli.SendAsync($req).GetAwaiter().GetResult()

            if ([int]$resp.StatusCode -eq 401 -and $a -lt $maxAttempts) {
                Write-Host '    401 — re-minting JWT' -ForegroundColor DarkYellow
                $script:jwt = Get-Jwt; $script:retries++; continue
            }
            if ($resp.IsSuccessStatusCode) { return $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
            if ([int]$resp.StatusCode -notin 408,429,500,502,503,504) {
                throw "detail failed HTTP $([int]$resp.StatusCode) (not retryable)"
            }
            if ($a -eq $maxAttempts) { throw "detail failed HTTP $([int]$resp.StatusCode) after $a attempts" }
        } catch [System.Net.Http.HttpRequestException] {
            if ($a -eq $maxAttempts) { throw }
        }
        $script:retries++
        Start-Sleep -Milliseconds ([int](500 * [Math]::Pow(2, $a - 1)))
    }
}

for ($b = 0; $b -lt $batches; $b++) {
    $slice = $ids.GetRange($b * $BatchSize, [Math]::Min($BatchSize, $ids.Count - $b * $BatchSize))
    # `limit` is REQUIRED: /detail defaults to 5 records and silently returns only the first 5.
    $qs   = (($slice | ForEach-Object { "eventId=$_" }) -join '&') + "&limit=$($slice.Count)"
    $uri  = "https://api.industrialinfo.com/idb/v2.7/offlineevents/detail?$qs"

    $j = (Invoke-Detail $uri | ConvertFrom-Json)
    $seen = @{}
    foreach ($e in $j.offlineEvents) {
        $oc = $e.offlineCapacity
        $r  = $tbl.NewRow()
        $r['EventId'] = [int]$e.eventId
        $r['PlantId'] = if ($null -ne $e.associatedPlantId) { [int]$e.associatedPlantId } else { [DBNull]::Value }
        if ($null -ne $oc) {
            $r['ProductId']          = if ($null -ne $oc.productId)       { [int]$oc.productId }    else { [DBNull]::Value }
            $r['ProductDescription'] = if ($oc.productDesc)               { [string]$oc.productDesc } else { [DBNull]::Value }
            $r['UnitCapacity']       = if ($null -ne $oc.unitCapacity)    { [double]$oc.unitCapacity } else { [DBNull]::Value }
            $r['OfflineCapacity']    = if ($null -ne $oc.capacityOffline) { [double]$oc.capacityOffline } else { [DBNull]::Value }
            $r['OfflineCapacityUOM'] = if ($oc.uom)                       { [string]$oc.uom }       else { [DBNull]::Value }
        } else {
            foreach ($c in 'ProductId','ProductDescription','UnitCapacity','OfflineCapacity','OfflineCapacityUOM') { $r[$c] = [DBNull]::Value }
        }
        $tbl.Rows.Add($r); $seen[[int]$e.eventId] = $true; $fetched++
    }
    $missing += ($slice | Where-Object { -not $seen.ContainsKey($_) }).Count
    Write-Progress -Activity 'Fetching /offlineevents/detail' -Status "batch $($b+1)/$batches  fetched=$fetched" -PercentComplete (100*($b+1)/$batches)
    Start-Sleep -Milliseconds $delayMs
}
Write-Progress -Activity 'Fetching /offlineevents/detail' -Completed
$sw.Stop()

Write-Host ''
Write-Host "Fetched $fetched of $($ids.Count) EventIds in $([int]$sw.Elapsed.TotalSeconds)s; $missing not returned by the API; $retries retry/retries." -ForegroundColor Green
$withCap = ($tbl.Rows | Where-Object { $_['OfflineCapacity'] -ne [DBNull]::Value }).Count
Write-Host "  $withCap carry an offlineCapacity object." -ForegroundColor Green

$tbl | Export-Csv -NoTypeInformation -Path $CsvPath -Encoding UTF8
Write-Host "CSV written: $CsvPath" -ForegroundColor Green

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN — nothing written. Re-run with -Apply to UPDATE arm.OfflineEvent.' -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------- 4. stage + UPDATE
Write-Host ''
Write-Host 'Applying UPDATE to arm.OfflineEvent...' -ForegroundColor Cyan
$cn = New-Conn
try {
    $tx = $cn.BeginTransaction()
    try {
        $mk = $cn.CreateCommand(); $mk.Transaction = $tx
        $mk.CommandText = @'
CREATE TABLE #Fix (
    EventId            INT NOT NULL PRIMARY KEY,
    PlantId            INT NULL,
    ProductId          INT NULL,
    ProductDescription VARCHAR(8000) NULL,
    UnitCapacity       FLOAT NULL,
    OfflineCapacity    FLOAT NULL,
    OfflineCapacityUOM VARCHAR(8000) NULL);
'@
        [void]$mk.ExecuteNonQuery()

        $bc = New-Object System.Data.SqlClient.SqlBulkCopy($cn, [System.Data.SqlClient.SqlBulkCopyOptions]::Default, $tx)
        $bc.DestinationTableName = '#Fix'; $bc.BulkCopyTimeout = 600
        foreach ($c in $tbl.Columns) { [void]$bc.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
        $bc.WriteToServer($tbl)

        # Only fill columns that are still NULL — idempotent, and never overwrites a value the
        # fixed loader has since written correctly.
        $up = $cn.CreateCommand(); $up.Transaction = $tx; $up.CommandTimeout = 1800
        $up.CommandText = @'
UPDATE t
   SET t.PlantId            = COALESCE(t.PlantId,            f.PlantId),
       t.ProductId          = COALESCE(t.ProductId,          f.ProductId),
       t.ProductDescription = COALESCE(t.ProductDescription, f.ProductDescription),
       t.UnitCapacity       = COALESCE(t.UnitCapacity,       f.UnitCapacity),
       t.OfflineCapacity    = COALESCE(t.OfflineCapacity,    f.OfflineCapacity),
       t.OfflineCapacityUOM = COALESCE(t.OfflineCapacityUOM, f.OfflineCapacityUOM),
       t.ModifiedAtUtc      = SYSUTCDATETIME()
  FROM arm.OfflineEvent AS t
  JOIN #Fix             AS f ON f.EventId = t.EventId
 WHERE (t.PlantId IS NULL AND f.PlantId IS NOT NULL)
    OR (t.ProductId IS NULL AND f.ProductId IS NOT NULL)
    OR (t.ProductDescription IS NULL AND f.ProductDescription IS NOT NULL)
    OR (t.UnitCapacity IS NULL AND f.UnitCapacity IS NOT NULL)
    OR (t.OfflineCapacity IS NULL AND f.OfflineCapacity IS NOT NULL)
    OR (t.OfflineCapacityUOM IS NULL AND f.OfflineCapacityUOM IS NOT NULL);
'@
        $n = $up.ExecuteNonQuery()
        $tx.Commit()
        Write-Host "  $n row(s) updated. Committed." -ForegroundColor Green
    } catch { $tx.Rollback(); Write-Host '  ROLLED BACK.' -ForegroundColor Red; throw }
} finally { $cn.Close() }
