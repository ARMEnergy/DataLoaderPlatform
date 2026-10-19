<#
.SYNOPSIS
    Generates the NUCORE column contract: the C# descriptor file and all four .sql files.

.DESCRIPTION
    NUCORE lands 437 columns across five tables. Each column has to appear, in the SAME
    ORDER and with the SAME TYPE, in four places:

        1. dbo.<Table>                       (sql/NUCORE/001)
        2. dbo.<Table>Type  (the TVP)        (sql/NUCORE/002)
        3. dbo.usp_Merge<Table>              (sql/NUCORE/003)
        4. the DataTable built by NucoreSink (NucoreColumns.Generated.cs)

    A TVP binds BY POSITION, so one hand-typo in any of those shifts every later column
    silently. Hand-maintaining four copies of 437 columns is not a thing a person can do
    reliably, so all four are generated here from two inputs that are evidence rather
    than opinion:

        docs/apis/NUCORE.openapi.json          - the vendor's OWN OpenAPI document, which
                                                 is the authority on column name, order,
                                                 type and nullability.
        docs/apis/NUCORE.observed-profile.json - a profile of real full-day responses,
                                                 which is the authority on VARCHAR sizing,
                                                 on DATETIME2-vs-DATETIMEOFFSET, and on
                                                 the decimal scale actually in use.

    Re-run it after any vendor schema change; never hand-edit the generated files.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER Check
    Generate into memory and compare against what is on disk. Exits 1 if they differ,
    without writing. This is what the test suite and CI use to prove the checked-in files
    still match the spec.

.EXAMPLE
    .\scripts\Generate-NucoreSchema.ps1
    .\scripts\Generate-NucoreSchema.ps1 -Check
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------------------
# Endpoint configuration.
#
# Everything here is a DECISION, as opposed to the column lists below it, which are facts
# read out of the vendor's spec. Each entry says: which DTO describes it, which URL path
# serves it, what its date parameter is called (the spec's name, which for two endpoints
# is NOT what the loader was originally specified with), and how rows are identified.
#
# WriteMode - which one an endpoint gets is decided by EVIDENCE, not by taste. A key is
# used only where it was shown unique over COMPLETE populations (whole days / whole trade
# months), never over a sample:
#
#   Merge          - natural primary key verified unique over several complete days.
#                    VarResults (7 days) and Volatilities (5 days / 712k rows).
#   ReplaceByDate  - no trustworthy key: delete every row for the dates in the batch, then
#                    insert. The whole unit is written as ONE batch, so the delete and the
#                    insert cover exactly the same set of rows.
#   ReplaceByScope - as above, but the scope is wider than a date because the unit is not a
#                    date. PositionDetails only: its unit is a trade month covering many
#                    system dates, so rows are replaced per (TradeMonth, SystemDate) and
#                    batches are flushed only on a SystemDate boundary.
#
# ⚠ History: VarResults/Volatilities/PnlExplain/PositionDetails were all originally given
# natural keys on the strength of ONE sample each. For PositionDetails that sample was the
# first 100,000 rows of a trade-ordered response - only 130 distinct trades - and the key it
# "verified" turned out to be wrong in production with a PK violation. Re-verified against
# complete populations, PnlExplain and PositionDetails both have duplicates and lost their
# keys. Do not reinstate a key here without re-running that verification.
# ---------------------------------------------------------------------------------------
$Endpoints = @(
    [ordered]@{
        Name          = 'VarResults'
        Dto           = 'DgVarResultsDto'
        Path          = '/Integration/api/risk/VarResults/Get'
        DateParam     = 'VarDate'          # spec name; the loader's URL already had this right
        DateColumn    = 'VarDate'
        Partition     = 'Date'
        WriteMode     = 'Merge'
        # Verified UNIQUE over SEVEN complete days spanning 2025-10 to 2026-10 (9,378 /
        # 9,684 / 9,666 / 10,350 / 10,170 / 6,714 / 6,948 rows), not one sample.
        # productGroup is deliberately NOT in the key: the key is already unique without it,
        # and it is a 100-char space-padded column that would cost 400 bytes of index for
        # nothing.
        KeyColumns    = @('varDate','varBucket','legalEntity','confidenceLevelId','holdingPeriod','tradeBook','tradeMonth','productSpec')
        ScopeColumns  = @()
        ClusterColumns = @()
    },
    [ordered]@{
        Name          = 'PositionSummary'
        Dto           = 'PositionSummaryViewDto'
        Path          = '/Integration/api/risk/PositionSummary/Get'
        DateParam     = 'SystemDate'
        DateColumn    = 'SystemDate'
        Partition     = 'Date'
        WriteMode     = 'ReplaceByDate'
        # NO natural key exists. Tested exhaustively against a full day (13,555 rows): a
        # 30-column candidate including every dimension AND several measures still left 6
        # collisions - rows differing only in e.g. contractPrice 139.75 vs 139.70. So the
        # day is replaced wholesale rather than keyed on something invented.
        KeyColumns    = @()
        ScopeColumns  = @('systemDate')
        ClusterColumns = @('SystemDate','TradeId','TradeMonth')
    },
    [ordered]@{
        Name          = 'PnlExplain'
        Dto           = 'PnlExplainDetailsDto'
        Path          = '/Integration/api/risk/PnlExplain/Get'
        DateParam     = 'CurDate'          # NOT SystemDate - see docs/apis/NUCORE.md Behaviour 1
        DateColumn    = 'CurDate'
        Partition     = 'Date'
        # ⚠ Was Merge on (curDate, tradeId, detailId, tradeMonth, productSpec), which is
        # unique on most days and NOT on all of them: 2025-10-15 carries 19 BYTE-IDENTICAL
        # copies of one row (1,712 distinct keys for 1,730 rows). On a first load all 19 go
        # down the NOT MATCHED branch and the INSERT violates the primary key.
        #
        # Those duplicates are exact, so de-duplicating would be lossless TODAY - but that
        # was verified on one day, and betting on it is the same mistake that produced the
        # PositionDetails outage. The day is replaced wholesale instead, which is faithful
        # to the source whether the duplicates are exact or not.
        WriteMode     = 'ReplaceByDate'
        KeyColumns    = @()
        ScopeColumns  = @('curDate')
        ClusterColumns = @('CurDate','TradeId','TradeMonth')
    },
    [ordered]@{
        Name          = 'PositionDetails'
        Dto           = 'PositionDetailsDto'
        Path          = '/Integration/api/risk/PositionDetails/Get'
        DateParam     = $null              # HAS NO DATE PARAMETER - see Behaviour 2
        DateColumn    = 'SystemDate'
        Partition     = 'TradeMonth'
        # ⚠ NO natural key exists, and this is the one that failed in production.
        #
        # It was originally Merge on (systemDate, tradeId, detailId, tradeMonth), "verified
        # unique" over 100,000 rows - which were the first page of a TRADE-ORDERED response
        # and covered only 130 distinct trades. Re-verified over five COMPLETE trade months
        # (443,477 rows) that key has 45,151 duplicates. Extending it does not rescue it:
        #   + specId                              2,189 duplicates
        #   + specId, priceType, settlementIndex    129 duplicates
        #   + productId as well                     129 duplicates
        # and the survivors are NOT exact duplicates - one group holds 17 distinct
        # settlement rows at 17 different contract prices, differing only in measures.
        # De-duplicating would destroy real realized P&L.
        #
        # So rows are replaced per (TradeMonth, SystemDate): the unit is a trade month
        # covering many system dates, and batches are flushed only on a SystemDate boundary
        # so a date is never split across two delete-and-insert calls.
        WriteMode     = 'ReplaceByScope'
        KeyColumns    = @()
        ScopeColumns  = @('tradeMonth','systemDate')
        ClusterColumns = @('TradeMonth','SystemDate')
    },
    [ordered]@{
        Name          = 'Volatilities'
        Dto           = 'ProductSpecVolatilitiesVDto'
        Path          = '/Integration/api/risk/Volatilities/Get'
        DateParam     = 'SystemDate'
        DateColumn    = 'SystemDate'
        Partition     = 'Date'
        WriteMode     = 'Merge'
        # Verified UNIQUE over FIVE complete days totalling 712,004 rows (147,908 / 147,770
        # / 144,878 / 139,860 / 132,588), spanning 2025-10 to 2026-10.
        KeyColumns    = @('systemDate','productSpec','instrumentType','startTradeMonth','endTradeMonth','putCallFlag','strikeType','strikePrice')
        ScopeColumns  = @()
        ClusterColumns = @()
    }
)

$DatabaseName = 'NUCORE'
$SchemaName   = 'dbo'

# Decimal shape for every `number/double` the vendor declares.
#
# The source really is a double, but these are money and risk numbers that get SUMmed
# downstream, where binary floating point drifts. The widest scale observed in live data is
# 10 (the option greeks), and the largest magnitude is ~3.57e6, so (28,12) keeps every
# observed digit and leaves 16 integer digits of headroom. Values are rounded to this scale
# in C# before they reach the server rather than being silently rounded by it.
$DecimalPrecision = 28
$DecimalScale     = 12

# SQL Server refuses an index key wider than 900 bytes. The natural keys are checked
# against this below so a too-generous VARCHAR fails the generator instead of failing
# CREATE TABLE on a machine nobody is watching.
$MaxKeyBytes = 900

# ---------------------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------------------
$specPath    = Join-Path $RepoRoot 'docs\apis\NUCORE.openapi.json'
$profilePath = Join-Path $RepoRoot 'docs\apis\NUCORE.observed-profile.json'

foreach ($p in @($specPath, $profilePath)) {
    if (-not (Test-Path $p)) { throw "Required input not found: $p" }
}

$spec    = Get-Content $specPath    -Raw | ConvertFrom-Json
$profile = Get-Content $profilePath -Raw | ConvertFrom-Json

# ---------------------------------------------------------------------------------------
# Naming: the vendor's camelCase JSON property -> our PascalCase SQL column.
#
# Deliberately mechanical (upper-case the first letter and nothing else) so the mapping
# stays reversible and predictable. Acronym-prettifying 'cIleId' into 'CIleId' vs 'CILEId'
# is exactly the sort of judgement that drifts between the four generated artefacts.
# ---------------------------------------------------------------------------------------
function ConvertTo-ColumnName([string] $jsonName) {
    if ([string]::IsNullOrEmpty($jsonName)) { throw 'Empty JSON property name' }
    return $jsonName.Substring(0,1).ToUpperInvariant() + $jsonName.Substring(1)
}

# VARCHAR sizing from observed data, with headroom.
#
# The vendor's spec declares no maxLength on any string, so the only evidence is what came
# back. Sizes are tiered rather than exact so that a slightly longer value next week does
# not throw a truncation error, and a column never seen populated gets a generous default
# instead of a size inferred from zero observations.
function Get-VarcharSize([int] $observedMax, [bool] $neverPopulated) {
    if ($neverPopulated) { return 255 }
    if ($observedMax -le 10)  { return 50 }
    if ($observedMax -le 25)  { return 100 }
    if ($observedMax -le 50)  { return 200 }
    if ($observedMax -le 100) { return 400 }
    if ($observedMax -le 250) { return 1000 }
    return 4000
}

# Byte cost of one column inside an index key, for the 900-byte check.
function Get-KeyByteCost([string] $sqlType) {
    switch -Regex ($sqlType) {
        '^VARCHAR\((\d+)\)$'  { return [int]$Matches[1] }
        '^INT$'               { return 4 }
        '^BIGINT$'            { return 8 }
        '^BIT$'               { return 1 }
        '^DATETIME2'          { return 8 }
        '^DATETIMEOFFSET'     { return 10 }
        '^UNIQUEIDENTIFIER$'  { return 16 }
        '^DECIMAL\((\d+),'    { $p = [int]$Matches[1]; if ($p -le 9) { return 5 } elseif ($p -le 19) { return 9 } elseif ($p -le 28) { return 13 } else { return 17 } }
        default               { throw "Unknown SQL type for key cost: $sqlType" }
    }
}

# ---------------------------------------------------------------------------------------
# Resolve every column of every endpoint from the spec + profile.
# ---------------------------------------------------------------------------------------
function Resolve-Columns($endpoint) {
    $dto = $spec.components.schemas.($endpoint.Dto)
    if ($null -eq $dto) { throw "Schema $($endpoint.Dto) not found in the OpenAPI document" }

    $prof = $profile.endpoints.($endpoint.Name)
    if ($null -eq $prof) { throw "Endpoint $($endpoint.Name) not found in the observed profile" }
    $profiledRows = [int]$prof.rowsProfiled

    # 1-based position within the PRIMARY KEY. Deliberately independent of the column
    # list's order: the column list follows the vendor's spec (PositionDetails declares
    # systemDate as its 58th property), while the key LEADS with the date so the clustered
    # index groups a day together.
    $keySet = @{}
    for ($i = 0; $i -lt $endpoint.KeyColumns.Count; $i++) {
        $keySet[$endpoint.KeyColumns[$i]] = $i + 1
    }

    $columns = New-Object System.Collections.Generic.List[object]

    foreach ($p in $dto.properties.PSObject.Properties) {
        $json = $p.Name
        $t    = if ($p.Value.PSObject.Properties.Name -contains 'type')     { $p.Value.type }     else { $null }
        $fmt  = if ($p.Value.PSObject.Properties.Name -contains 'format')   { $p.Value.format }   else { $null }
        $nul  = if ($p.Value.PSObject.Properties.Name -contains 'nullable') { [bool]$p.Value.nullable } else { $false }

        $o = if ($prof.columns.PSObject.Properties.Name -contains $json) { $prof.columns.$json } else { $null }
        $observedMax     = if ($o) { [int]$o.maxLen }   else { 0 }
        $hasOffset       = if ($o) { [bool]$o.hasOffset } else { $false }
        $neverPopulated  = if ($o) { ([long]$o.nulls -ge $profiledRows) } else { $true }

        $isKey = $keySet.ContainsKey($json)
        $keyOrdinal = if ($isKey) { $keySet[$json] } else { 0 }

        if ($null -eq $t) {
            # `recordState` is the only property the vendor declares without a type; it is an
            # enum $ref and arrives as a small integer (constant 0 in every row observed).
            $sqlType = 'INT'; $clr = 'int'; $kind = 'Int32'
        }
        elseif ($t -eq 'string' -and $fmt -eq 'date-time') {
            # Offset-bearing timestamps (createdAt / modifiedAt / originalModifiedAt) keep
            # their offset; the plain wall-clock dates must NOT be given one, or every
            # business date shifts by the host's zone.
            if ($hasOffset) { $sqlType = 'DATETIMEOFFSET(7)'; $clr = 'DateTimeOffset'; $kind = 'DateTimeOffset' }
            else            { $sqlType = 'DATETIME2(7)';      $clr = 'DateTime';       $kind = 'DateTime' }
        }
        elseif ($t -eq 'string' -and $fmt -eq 'uuid') {
            $sqlType = 'UNIQUEIDENTIFIER'; $clr = 'Guid'; $kind = 'Guid'
        }
        elseif ($t -eq 'string') {
            $size = Get-VarcharSize $observedMax $neverPopulated
            $sqlType = "VARCHAR($size)"; $clr = 'string'; $kind = 'String'
        }
        elseif ($t -eq 'integer' -and $fmt -eq 'int64') {
            $sqlType = 'BIGINT'; $clr = 'long'; $kind = 'Int64'
        }
        elseif ($t -eq 'integer') {
            $sqlType = 'INT'; $clr = 'int'; $kind = 'Int32'
        }
        elseif ($t -eq 'number') {
            $sqlType = "DECIMAL($DecimalPrecision,$DecimalScale)"; $clr = 'decimal'; $kind = 'Decimal'
        }
        elseif ($t -eq 'boolean') {
            $sqlType = 'BIT'; $clr = 'bool'; $kind = 'Boolean'
        }
        else {
            throw "Unhandled type '$t'/'$fmt' for $($endpoint.Name).$json"
        }

        # A key column is never nullable. String keys are coalesced to '' on the way in
        # (see NucoreRowBuilder) so the natural key is a real PRIMARY KEY and the MERGE
        # join needs no NULL-safe comparison - the classic silent-duplicate bug.
        $nullable = $nul -and -not $isKey

        $columns.Add([pscustomobject]@{
            Json       = $json
            Column     = ConvertTo-ColumnName $json
            SqlType    = $sqlType
            Clr        = $clr
            Kind       = $kind
            Nullable   = $nullable
            IsKey      = $isKey
            KeyOrdinal = $keyOrdinal
            Observed   = $observedMax
        })
    }

    # Every configured key column must exist, or the key silently degrades to the columns
    # that happened to match.
    foreach ($k in $endpoint.KeyColumns) {
        if (-not ($columns | Where-Object { $_.Json -eq $k })) {
            throw "$($endpoint.Name): key column '$k' is not a property of $($endpoint.Dto)"
        }
    }

    return $columns
}

$resolved = [ordered]@{}
foreach ($e in $Endpoints) {
    $cols = Resolve-Columns $e
    $resolved[$e.Name] = $cols

    if ($e.WriteMode -eq 'Merge') {
        if (@($e.KeyColumns).Count -eq 0) {
            throw "$($e.Name): Merge write mode with no key columns."
        }
        $keyCols = $cols | Where-Object { $_.IsKey } | Sort-Object KeyOrdinal
        $bytes = ($keyCols | ForEach-Object { Get-KeyByteCost $_.SqlType } | Measure-Object -Sum).Sum
        if ($bytes -gt $MaxKeyBytes) {
            throw "$($e.Name): primary key is $bytes bytes, over SQL Server's $MaxKeyBytes limit. Tighten a VARCHAR or drop a column from KeyColumns."
        }
        Write-Verbose "$($e.Name): $($cols.Count) columns, MERGE on PK $($keyCols.Count) cols / $bytes bytes"
    }
    else {
        # A replace mode is defined by its scope and its clustered index. Both must exist
        # and must name real columns, or the generated DELETE would either miss rows or
        # match far too many.
        if (@($e.KeyColumns).Count -gt 0) {
            throw "$($e.Name): $($e.WriteMode) must not declare key columns."
        }
        if (@($e.ScopeColumns).Count -eq 0) {
            throw "$($e.Name): $($e.WriteMode) requires ScopeColumns - without them the DELETE would empty the table."
        }
        foreach ($s in $e.ScopeColumns) {
            if (-not ($cols | Where-Object { $_.Json -eq $s })) {
                throw "$($e.Name): scope column '$s' is not a property of $($e.Dto)."
            }
        }
        if (@($e.ClusterColumns).Count -eq 0) {
            throw "$($e.Name): $($e.WriteMode) requires ClusterColumns so the per-scope DELETE is a range operation."
        }
        foreach ($c in $e.ClusterColumns) {
            if (-not ($cols | Where-Object { $_.Column -eq $c })) {
                throw "$($e.Name): cluster column '$c' is not a column of the table."
            }
        }
        # The clustered index must LEAD with the scope, or the delete scans.
        $scopeAsColumns = @($e.ScopeColumns | ForEach-Object {
            $json = $_; ($cols | Where-Object { $_.Json -eq $json }).Column
        })
        $leading = @($e.ClusterColumns)[0..([Math]::Min(@($scopeAsColumns).Count, @($e.ClusterColumns).Count) - 1)]
        $missing = $scopeAsColumns | Where-Object { $_ -notin $leading }
        if ($missing) {
            throw "$($e.Name): ClusterColumns must lead with the scope ($($scopeAsColumns -join ', ')); '$($missing -join ', ')' is not in the leading positions."
        }
        Write-Verbose "$($e.Name): $($cols.Count) columns, $($e.WriteMode) on ($($scopeAsColumns -join ', '))"
    }
}

# ---------------------------------------------------------------------------------------
# Emit: C# descriptors
# ---------------------------------------------------------------------------------------
function New-CSharp {
    $sb = [System.Text.StringBuilder]::new()
    $null = $sb.AppendLine('// <auto-generated>')
    $null = $sb.AppendLine('//     Generated by scripts/Generate-NucoreSchema.ps1 from')
    $null = $sb.AppendLine('//         docs/apis/NUCORE.openapi.json          (the vendor''s own OpenAPI document)')
    $null = $sb.AppendLine('//         docs/apis/NUCORE.observed-profile.json (a profile of real full-day responses)')
    $null = $sb.AppendLine('//')
    $null = $sb.AppendLine('//     DO NOT EDIT. The column name, ORDER and type here are one half of the TVP')
    $null = $sb.AppendLine('//     contract; sql/NUCORE/002 is the other. A TVP binds by POSITION, so editing')
    $null = $sb.AppendLine('//     one side alone shifts every later column silently. Re-run the generator.')
    $null = $sb.AppendLine('// </auto-generated>')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('namespace DataLoader.NUCORE;')
    $null = $sb.AppendLine()
    $null = $sb.AppendLine('internal static partial class NucoreColumns')
    $null = $sb.AppendLine('{')

    foreach ($e in $Endpoints) {
        $cols = $resolved[$e.Name]
        $null = $sb.AppendLine("    /// <summary>The $($cols.Count) columns of <c>$SchemaName.$($e.Name)</c>, in TVP order.</summary>")
        $null = $sb.AppendLine("    public static readonly IReadOnlyList<NucoreColumn> $($e.Name) = new NucoreColumn[]")
        $null = $sb.AppendLine('    {')
        foreach ($c in $cols) {
            $null = $sb.AppendLine("        new(`"$($c.Column)`", `"$($c.Json)`", `"$($c.SqlType)`", NucoreColumnKind.$($c.Kind), Nullable: $($c.Nullable.ToString().ToLowerInvariant()), IsKey: $($c.IsKey.ToString().ToLowerInvariant()), KeyOrdinal: $($c.KeyOrdinal)),")
        }
        $null = $sb.AppendLine('    };')
        $null = $sb.AppendLine()
    }

    $null = $sb.AppendLine('    /// <summary>Decimal shape applied to every <c>number/double</c> the vendor declares.</summary>')
    $null = $sb.AppendLine("    public const int DecimalPrecision = $DecimalPrecision;")
    $null = $sb.AppendLine("    public const int DecimalScale = $DecimalScale;")
    $null = $sb.AppendLine('}')
    return $sb.ToString()
}

# ---------------------------------------------------------------------------------------
# Emit: SQL
# ---------------------------------------------------------------------------------------
function New-SqlHeader([string] $title, [bool] $createDatabase = $false) {
    $sb = [System.Text.StringBuilder]::new()
    $null = $sb.AppendLine('-- =====================================================================================')
    $null = $sb.AppendLine("-- $title")
    $null = $sb.AppendLine('--')
    $null = $sb.AppendLine('-- GENERATED by scripts/Generate-NucoreSchema.ps1 - do not hand-edit.')
    $null = $sb.AppendLine('-- Re-run the generator after any vendor schema change.')
    $null = $sb.AppendLine('--')
    $null = $sb.AppendLine("-- Target: Loaders:NUCORE:ConnectionString -> Database=$DatabaseName, schema [$SchemaName].")
    $null = $sb.AppendLine('-- =====================================================================================')
    $null = $sb.AppendLine()
    if ($createDatabase) {
        $null = $sb.AppendLine('-- Idempotent CREATE DATABASE guard (legal inside an IF block).')
        $null = $sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$DatabaseName')")
        $null = $sb.AppendLine("    CREATE DATABASE [$DatabaseName];")
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()
    }
    # Every .sql in this repo must name its own database: these scripts are run by hand in
    # SSMS against a server holding every loader's database side by side, several of which
    # hold identically-named objects. USE must be followed by GO - it only takes effect for
    # SUBSEQUENT batches, so DDL in the same batch still runs against the previous database.
    $null = $sb.AppendLine("USE [$DatabaseName];")
    $null = $sb.AppendLine('GO')
    $null = $sb.AppendLine()
    return $sb
}

function New-Sql001 {
    $sb = New-SqlHeader "NUCORE 001 - target tables" $true
    foreach ($e in $Endpoints) {
        $cols = $resolved[$e.Name]
        $table = "[$SchemaName].[$($e.Name)]"
        $null = $sb.AppendLine("-- ------------------------------------------------------------------------------------")
        $null = $sb.AppendLine("-- $($e.Name)  ($($cols.Count) vendor columns)")
        if ($e.WriteMode -in @('ReplaceByDate', 'ReplaceByScope')) {
            $scopeText = (@($e.ScopeColumns) | ForEach-Object {
                $json = $_
                ($cols | Where-Object { $_.Json -eq $json }).Column
            }) -join ', '
            $null = $sb.AppendLine("--")
            $null = $sb.AppendLine("-- No trustworthy natural key exists for this feed, so rows are REPLACED per")
            $null = $sb.AppendLine("-- ($scopeText) rather than merged: the proc DELETEs every row for the scopes")
            $null = $sb.AppendLine("-- present in the batch and re-INSERTs. The identity column exists only so the table")
            $null = $sb.AppendLine("-- has a primary key; it carries no business meaning and is NOT stable across reloads,")
            $null = $sb.AppendLine("-- so nothing downstream may join on it.")
            $null = $sb.AppendLine("--")
            $null = $sb.AppendLine("-- ModifiedAtUtc therefore means 'when this scope was last loaded', not 'when this row")
            $null = $sb.AppendLine("-- last changed' - there is no row identity to track change against.")
        }
        $null = $sb.AppendLine("-- ------------------------------------------------------------------------------------")
        $isReplace = $e.WriteMode -in @('ReplaceByDate', 'ReplaceByScope')

        $null = $sb.AppendLine("IF OBJECT_ID(N'$table', N'U') IS NULL")
        $null = $sb.AppendLine('BEGIN')
        $null = $sb.AppendLine("    CREATE TABLE $table")
        $null = $sb.AppendLine('    (')
        if ($isReplace) {
            $null = $sb.AppendLine("        [$($e.Name)Id]        BIGINT IDENTITY(1,1) NOT NULL,")
        }
        foreach ($c in $cols) {
            $nullText = if ($c.Nullable) { 'NULL' } else { 'NOT NULL' }
            $null = $sb.AppendLine("        [$($c.Column)] $($c.SqlType) $nullText,")
        }
        # Audit column, DB-stamped. Never a TVP column - matching every other loader here.
        $null = $sb.AppendLine('        [FileLogId]     BIGINT NULL,')
        $null = $sb.AppendLine('        [ModifiedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_' + $e.Name + '_ModifiedAtUtc] DEFAULT (SYSUTCDATETIME()),')
        if ($isReplace) {
            $null = $sb.AppendLine("        CONSTRAINT [PK_$($e.Name)] PRIMARY KEY NONCLUSTERED ([$($e.Name)Id])")
        } else {
            $keyList = ($cols | Where-Object { $_.IsKey } | Sort-Object KeyOrdinal | ForEach-Object { "[$($_.Column)]" }) -join ', '
            $null = $sb.AppendLine("        CONSTRAINT [PK_$($e.Name)] PRIMARY KEY CLUSTERED ($keyList)")
        }
        $null = $sb.AppendLine('    );')
        $null = $sb.AppendLine('END')
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()

        if ($isReplace) {
            # Clustered on the replace SCOPE so the delete is a range operation rather than
            # a scan. For PositionDetails this index doubles as the watermark lookup
            # (MAX(SystemDate) WHERE TradeMonth = @TradeMonth), which is why no separate
            # non-clustered index is emitted for it.
            $clusterList = (@($e.ClusterColumns) | ForEach-Object { "[$_]" }) -join ', '
            $null = $sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'CIX_$($e.Name)' AND object_id = OBJECT_ID(N'$table'))")
            $null = $sb.AppendLine("    CREATE CLUSTERED INDEX [CIX_$($e.Name)] ON $table ($clusterList);")
            $null = $sb.AppendLine('GO')
            $null = $sb.AppendLine()
        }
        elseif ((@($cols | Where-Object { $_.IsKey } | Sort-Object KeyOrdinal)[0]).Column -ne $e.DateColumn) {
            $null = $sb.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_$($e.Name)_$($e.DateColumn)' AND object_id = OBJECT_ID(N'$table'))")
            $null = $sb.AppendLine("    CREATE NONCLUSTERED INDEX [IX_$($e.Name)_$($e.DateColumn)] ON $table ([$($e.DateColumn)]);")
            $null = $sb.AppendLine('GO')
            $null = $sb.AppendLine()
        }
    }
    return $sb.ToString()
}

function New-Sql002 {
    $sb = New-SqlHeader "NUCORE 002 - table-valued parameter types"
    $null = $sb.AppendLine('-- A TVP binds BY POSITION. The column order below is the contract with')
    $null = $sb.AppendLine('-- NucoreColumns.Generated.cs; NucoreTvpContractTests parses this file and asserts')
    $null = $sb.AppendLine('-- name + order + type + nullability against the descriptors, so the two cannot drift.')
    $null = $sb.AppendLine()
    foreach ($e in $Endpoints) {
        $cols = $resolved[$e.Name]
        $type = "[$SchemaName].[$($e.Name)Type]"
        $null = $sb.AppendLine("IF TYPE_ID(N'$type') IS NULL")
        $null = $sb.AppendLine("    CREATE TYPE $type AS TABLE")
        $null = $sb.AppendLine('    (')
        $lines = @()
        foreach ($c in $cols) {
            $nullText = if ($c.Nullable) { 'NULL' } else { 'NOT NULL' }
            $lines += "        [$($c.Column)] $($c.SqlType) $nullText"
        }
        $null = $sb.AppendLine(($lines -join ",`r`n"))
        $null = $sb.AppendLine('    );')
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()
    }
    return $sb.ToString()
}

function New-Sql003 {
    $sb = New-SqlHeader "NUCORE 003 - merge procedures"
    $null = $sb.AppendLine('-- Each proc returns, via SELECT, the number of rows it actually CHANGED. The loader')
    $null = $sb.AppendLine('-- records that as RecordsProcessed, so a re-merge that moves nothing reports 0 - a')
    $null = $sb.AppendLine('-- healthy result, not a fault.')
    $null = $sb.AppendLine()

    foreach ($e in $Endpoints) {
        $cols   = $resolved[$e.Name]
        $table  = "[$SchemaName].[$($e.Name)]"
        $type   = "[$SchemaName].[$($e.Name)Type]"
        $proc   = "[$SchemaName].[usp_Merge$($e.Name)]"

        $null = $sb.AppendLine("-- ------------------------------------------------------------------------------------")
        $null = $sb.AppendLine("-- usp_Merge$($e.Name)")
        $null = $sb.AppendLine("-- ------------------------------------------------------------------------------------")
        $null = $sb.AppendLine("IF OBJECT_ID(N'$proc', N'P') IS NULL")
        $null = $sb.AppendLine("    EXEC(N'CREATE PROCEDURE $proc AS BEGIN SET NOCOUNT ON; END');")
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()
        $null = $sb.AppendLine("ALTER PROCEDURE $proc")
        $null = $sb.AppendLine("    @Records $type READONLY,")
        $null = $sb.AppendLine('    @FileLogId BIGINT = NULL')
        $null = $sb.AppendLine('AS')
        $null = $sb.AppendLine('BEGIN')
        $null = $sb.AppendLine('    SET NOCOUNT ON;')
        $null = $sb.AppendLine('    SET XACT_ABORT ON;')
        $null = $sb.AppendLine()
        $null = $sb.AppendLine('    DECLARE @Changed INT = 0;')
        $null = $sb.AppendLine()

        if ($e.WriteMode -in @('ReplaceByDate', 'ReplaceByScope')) {
            $insertCols = (($cols | ForEach-Object { "[$($_.Column)]" }) + '[FileLogId]') -join ', '
            $selectCols = (($cols | ForEach-Object { "s.[$($_.Column)]" }) + '@FileLogId') -join ', '

            # The scope is what one delete-and-insert replaces. The loader guarantees a
            # scope is never split across two calls, so this DELETE always covers exactly
            # the rows the INSERT is about to write.
            $scopeCols = @($e.ScopeColumns | ForEach-Object {
                $json = $_
                ($cols | Where-Object { $_.Json -eq $json }).Column
            })
            $scopePredicate = ($scopeCols | ForEach-Object { "r.[$_] = t.[$_]" }) -join ' AND '

            $null = $sb.AppendLine('    BEGIN TRANSACTION;')
            $null = $sb.AppendLine()
            $null = $sb.AppendLine("    -- Replace by ($($scopeCols -join ', ')). Only the scopes actually present in this")
            $null = $sb.AppendLine('    -- batch are touched, so a partial load cannot blank data it was not given.')
            $null = $sb.AppendLine('    --')
            $null = $sb.AppendLine('    -- ⚠ This is only correct because the loader never splits one scope across two')
            $null = $sb.AppendLine('    -- calls. If it did, the second call would DELETE the rows the first had just')
            $null = $sb.AppendLine('    -- INSERTed. NucoreSourceReader enforces that and throws if a scope repeats.')
            $null = $sb.AppendLine("    DELETE t")
            $null = $sb.AppendLine("    FROM $table AS t")
            $null = $sb.AppendLine("    WHERE EXISTS (SELECT 1 FROM @Records AS r WHERE $scopePredicate);")
            $null = $sb.AppendLine()
            $null = $sb.AppendLine("    INSERT INTO $table ($insertCols)")
            $null = $sb.AppendLine("    SELECT $selectCols")
            $null = $sb.AppendLine('    FROM @Records AS s;')
            $null = $sb.AppendLine()
            $null = $sb.AppendLine('    SET @Changed = @@ROWCOUNT;')
            $null = $sb.AppendLine()
            $null = $sb.AppendLine('    COMMIT TRANSACTION;')
        }
        else {
            $keyCols    = $cols | Where-Object { $_.IsKey } | Sort-Object KeyOrdinal
            $valueCols  = $cols | Where-Object { -not $_.IsKey }
            $onClause   = ($keyCols | ForEach-Object { "t.[$($_.Column)] = s.[$($_.Column)]" }) -join "`r`n     AND "
            $insertCols = (($cols | ForEach-Object { "[$($_.Column)]" }) + '[FileLogId]') -join ', '
            $insertVals = (($cols | ForEach-Object { "s.[$($_.Column)]" }) + '@FileLogId') -join ', '

            $null = $sb.AppendLine("    MERGE $table AS t")
            $null = $sb.AppendLine('    USING @Records AS s')
            $null = $sb.AppendLine("       ON $onClause")
            $null = $sb.AppendLine('    WHEN MATCHED AND EXISTS (')
            $null = $sb.AppendLine('             -- EXCEPT treats NULL as equal to NULL, so this yields a row only when the')
            $null = $sb.AppendLine('             -- values genuinely differ and ModifiedAtUtc tracks real change. A plain')
            $null = $sb.AppendLine('             -- "t.col <> s.col OR ..." chain is UNKNOWN wherever either side is NULL,')
            $null = $sb.AppendLine('             -- which silently stops detecting changes into and out of NULL.')
            if ($valueCols.Count -gt 0) {
                $tList = ($valueCols | ForEach-Object { "t.[$($_.Column)]" }) -join ', '
                $sList = ($valueCols | ForEach-Object { "s.[$($_.Column)]" }) -join ', '
                $null = $sb.AppendLine("             SELECT $tList")
                $null = $sb.AppendLine('             EXCEPT')
                $null = $sb.AppendLine("             SELECT $sList")
            }
            $null = $sb.AppendLine('         )')
            $null = $sb.AppendLine('    THEN UPDATE SET')
            $setLines = @()
            foreach ($c in $valueCols) { $setLines += "             t.[$($c.Column)] = s.[$($c.Column)]" }
            $setLines += '             t.[FileLogId]     = @FileLogId'
            $setLines += '             t.[ModifiedAtUtc] = SYSUTCDATETIME()'
            $null = $sb.AppendLine(($setLines -join ",`r`n"))
            $null = $sb.AppendLine('    WHEN NOT MATCHED BY TARGET THEN')
            $null = $sb.AppendLine("        INSERT ($insertCols)")
            $null = $sb.AppendLine("        VALUES ($insertVals);")
            $null = $sb.AppendLine()
            $null = $sb.AppendLine('    SET @Changed = @@ROWCOUNT;')
        }

        $null = $sb.AppendLine()
        $null = $sb.AppendLine('    SELECT @Changed;')
        $null = $sb.AppendLine('END')
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()
    }

    # Watermark reader for the trade-month partitioned feed.
    $pd = $Endpoints | Where-Object { $_.Partition -eq 'TradeMonth' }
    foreach ($e in $pd) {
        $proc = "[$SchemaName].[usp_Get$($e.Name)Watermark]"
        $null = $sb.AppendLine("-- ------------------------------------------------------------------------------------")
        $null = $sb.AppendLine("-- usp_Get$($e.Name)Watermark")
        $null = $sb.AppendLine('--')
        $null = $sb.AppendLine("-- $($e.Name) has NO date parameter, so the loader cannot ask for one day. It instead")
        $null = $sb.AppendLine('-- pages a trade month newest-first (SortFields=-systemDate) and stops at the newest')
        $null = $sb.AppendLine('-- date already stored. That watermark is read from the table itself rather than a')
        $null = $sb.AppendLine('-- separate state table, so it is self-healing: delete rows and they are re-fetched.')
        $null = $sb.AppendLine("-- ------------------------------------------------------------------------------------")
        $null = $sb.AppendLine("IF OBJECT_ID(N'$proc', N'P') IS NULL")
        $null = $sb.AppendLine("    EXEC(N'CREATE PROCEDURE $proc AS BEGIN SET NOCOUNT ON; END');")
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()
        $null = $sb.AppendLine("ALTER PROCEDURE $proc")
        $null = $sb.AppendLine('    @TradeMonth VARCHAR(20)')
        $null = $sb.AppendLine('AS')
        $null = $sb.AppendLine('BEGIN')
        $null = $sb.AppendLine('    SET NOCOUNT ON;')
        $null = $sb.AppendLine("    SELECT MAX([$($e.DateColumn)])")
        $null = $sb.AppendLine("    FROM [$SchemaName].[$($e.Name)]")
        $null = $sb.AppendLine('    WHERE [TradeMonth] = @TradeMonth;')
        $null = $sb.AppendLine('END')
        $null = $sb.AppendLine('GO')
        $null = $sb.AppendLine()
    }

    return $sb.ToString()
}

function New-Sql999 {
    $sb = New-SqlHeader "NUCORE 999 - drop every object this loader creates"
    $null = $sb.AppendLine('-- Dependency order: procedures, then TVP types, then tables. Every statement names')
    $null = $sb.AppendLine("-- [$SchemaName] explicitly so this cannot reach past the objects the loader owns.")
    $null = $sb.AppendLine('--')
    $null = $sb.AppendLine('-- DESTRUCTIVE. Intended for a rebuild of a NUCORE database, not for routine use.')
    $null = $sb.AppendLine()

    foreach ($e in $Endpoints) {
        $null = $sb.AppendLine("DROP PROCEDURE IF EXISTS [$SchemaName].[usp_Merge$($e.Name)];")
    }
    foreach ($e in ($Endpoints | Where-Object { $_.Partition -eq 'TradeMonth' })) {
        $null = $sb.AppendLine("DROP PROCEDURE IF EXISTS [$SchemaName].[usp_Get$($e.Name)Watermark];")
    }
    $null = $sb.AppendLine('GO')
    $null = $sb.AppendLine()
    foreach ($e in $Endpoints) {
        $null = $sb.AppendLine("DROP TYPE IF EXISTS [$SchemaName].[$($e.Name)Type];")
    }
    $null = $sb.AppendLine('GO')
    $null = $sb.AppendLine()
    foreach ($e in $Endpoints) {
        $null = $sb.AppendLine("DROP TABLE IF EXISTS [$SchemaName].[$($e.Name)];")
    }
    $null = $sb.AppendLine('GO')
    $null = $sb.AppendLine()
    return $sb.ToString()
}

# ---------------------------------------------------------------------------------------
# Write or check
# ---------------------------------------------------------------------------------------
$outputs = [ordered]@{
    (Join-Path $RepoRoot 'src\DataLoader.NUCORE\NucoreColumns.Generated.cs') = New-CSharp
    (Join-Path $RepoRoot 'sql\NUCORE\001_CreateNucoreTables.sql')            = New-Sql001
    (Join-Path $RepoRoot 'sql\NUCORE\002_CreateNucoreTvpTypes.sql')          = New-Sql002
    (Join-Path $RepoRoot 'sql\NUCORE\003_CreateNucoreProcedures.sql')        = New-Sql003
    (Join-Path $RepoRoot 'sql\NUCORE\999_DropNucoreObjects.sql')             = New-Sql999
}

$differences = @()
foreach ($path in $outputs.Keys) {
    $content = $outputs[$path]
    if ($Check) {
        if (-not (Test-Path $path)) { $differences += "MISSING: $path"; continue }
        $onDisk = Get-Content $path -Raw
        if ($onDisk.Replace("`r`n","`n") -ne $content.Replace("`r`n","`n")) { $differences += "STALE:   $path" }
    }
    else {
        $dir = Split-Path -Parent $path
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-Content -Path $path -Value $content -Encoding utf8 -NoNewline
        Write-Output "wrote $path"
    }
}

if ($Check) {
    if ($differences.Count -gt 0) {
        Write-Output 'Generated NUCORE artefacts are out of date:'
        $differences | ForEach-Object { Write-Output "  $_" }
        Write-Output 'Run scripts\Generate-NucoreSchema.ps1 to regenerate.'
        exit 1
    }
    Write-Output 'All generated NUCORE artefacts are up to date.'
}

$total = ($resolved.Keys | ForEach-Object { $resolved[$_].Count } | Measure-Object -Sum).Sum
Write-Output "NUCORE: $($Endpoints.Count) endpoints, $total columns."
