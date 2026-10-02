<#
.SYNOPSIS
    Generates sql\<Loader>\004_Create<Loader>Comparison.sql for each loader whose
    database holds a comparable dbo schema.

.DESCRIPTION
    The generated proc is arm.usp_CompareArmToDbo. Pair metadata (which arm table
    maps to which dbo table, the join key, the date column) is resolved HERE, against
    the live catalog, so it cannot be invented. The comparable value columns are
    discovered at RUN time from sys.columns, so the proc does not go stale when a
    column is added.

    Re-run this whenever the arm or dbo schema changes.
#>
[CmdletBinding()]
param(
    [string] $Server = 'ARMH-OPSDB01',
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

# Loader -> database. Argus and Criterion are deliberately absent (see the stub files
# in their sql folders); CWG is deferred - its arm redesign renamed keys and columns,
# so its pairs need hand-written mappings rather than catalog matching.
$Loaders = [ordered]@{
    AGSI              = 'AGSI'
    CME               = 'CMEGroup'
    EOX               = 'EOX'
    EvolutionMarkets  = 'EvolutionMarkets'
    Genscape          = 'Genscape'
    ICE               = 'ICE'
    IHSPointLogic     = 'IHSPointLogic'
    IIR               = 'IIR'
    Marex             = 'Marex'
    ModernCommodities = 'ModernCommodities'
    NGI               = 'NGI'
    NGX               = 'NGX'
    OPIS              = 'OPIS'
}

# arm tables that exist only to run the loader - no dbo counterpart is expected.
$InfraTables = @('FileLog', 'Status', 'Endpoint', 'Region')

# Key overrides where the arm PK cannot be used to join.
#   AGSI.GasStorageEntity: arm keys on a surrogate Id, dbo on its own EntityId. The two
#   happen to hold the same values today (both seeded from 6593), but joining surrogate
#   to surrogate is only correct by accident - Code is the real natural key.
$KeyOverride = @{
    'AGSI.GasStorageEntity' = @('Code')
}

# Types that cannot be compared with a plain <> .
$UnComparableTypes = @('geography', 'geometry', 'xml', 'image', 'text', 'ntext',
                       'hierarchyid', 'sql_variant', 'varbinary', 'binary', 'timestamp')

# Loader-written audit columns - present on the arm side only, or meaningless to diff.
$AuditColumns = @('DateCreated', 'ModifiedAtUtc', 'FileLogId', 'RecordHash', 'ConduitLastUpdate')

$DateTypes = @('date', 'datetime', 'datetime2', 'smalldatetime', 'datetimeoffset')

function Get-Catalog([string]$db) {
    $cn = New-Object System.Data.SqlClient.SqlConnection("Server=$Server;Database=$db;Integrated Security=SSPI;TrustServerCertificate=True;Connect Timeout=20")
    $cn.Open()
    $c = $cn.CreateCommand()
    $c.CommandText = @'
SELECT s.name AS sch, t.name AS tbl, c.name AS col, ty.name AS typ,
       c.column_id, ISNULL(ic.key_ordinal, 0) AS pkord
FROM sys.tables t
JOIN sys.schemas s        ON s.schema_id = t.schema_id
JOIN sys.columns c        ON c.object_id = t.object_id
JOIN sys.types ty         ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.indexes i   ON i.object_id = t.object_id AND i.is_primary_key = 1
LEFT JOIN sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
WHERE s.name IN ('arm','dbo')
ORDER BY s.name, t.name, c.column_id
'@
    $r = $c.ExecuteReader()
    $tables = @{}
    while ($r.Read()) {
        $k = "$($r['sch'])|$($r['tbl'])"
        if (-not $tables.ContainsKey($k)) { $tables[$k] = [ordered]@{ Cols = [ordered]@{}; PK = @() } }
        $tables[$k].Cols[[string]$r['col']] = [string]$r['typ']
        if ([int]$r['pkord'] -gt 0) { $tables[$k].PK += , @([int]$r['pkord'], [string]$r['col']) }
    }
    $r.Close(); $cn.Close()
    return $tables
}

function Normalize([string]$n) { ($n -replace '[_\s]', '').ToLower().TrimEnd('s') }

$summary = @()

foreach ($loader in $Loaders.Keys) {
    $db = $Loaders[$loader]
    Write-Host "== $loader [$db]" -ForegroundColor Cyan
    $cat = Get-Catalog $db

    $armKeys = @($cat.Keys | Where-Object { $_.StartsWith('arm|') } | Sort-Object)
    $dboKeys = @($cat.Keys | Where-Object { $_.StartsWith('dbo|') })

    $pairs = @()
    $skipped = @()

    foreach ($ak in $armKeys) {
        $armName = $ak.Split('|')[1]
        if ($InfraTables -contains $armName) { continue }

        $match = @($dboKeys | Where-Object { (Normalize ($_.Split('|')[1])) -eq (Normalize $armName) })
        if ($match.Count -eq 0) { $skipped += "$armName (no dbo counterpart)"; continue }

        $dboName = $match[0].Split('|')[1]
        $armCols = $cat[$ak].Cols
        $dboCols = $cat[$match[0]].Cols

        # ---- join key
        $ovKey = "$loader.$armName"
        if ($KeyOverride.ContainsKey($ovKey)) { $keyCols = $KeyOverride[$ovKey] }
        else { $keyCols = @($cat[$ak].PK | Sort-Object { $_[0] } | ForEach-Object { $_[1] }) }

        if ($keyCols.Count -eq 0) { $skipped += "$armName (arm table has no primary key)"; continue }
        $missingKey = @($keyCols | Where-Object { -not $dboCols.Contains($_) })
        if ($missingKey.Count -gt 0) { $skipped += "$armName (key column(s) $($missingKey -join ',') absent from dbo.$dboName)"; continue }

        # ---- comparable value columns (recorded for documentation; rediscovered at run time)
        $valueCols = @($armCols.Keys | Where-Object {
                $dboCols.Contains($_) -and
                $keyCols -notcontains $_ -and
                $AuditColumns -notcontains $_ -and
                $UnComparableTypes -notcontains $armCols[$_] -and
                $UnComparableTypes -notcontains $dboCols[$_]
            })

        # ---- date column: prefer one that is part of the key, else any common date column
        $dateCol = $null
        foreach ($k in $keyCols) { if ($DateTypes -contains $armCols[$k]) { $dateCol = $k; break } }
        if (-not $dateCol) {
            foreach ($c in $armCols.Keys) {
                if ($AuditColumns -contains $c) { continue }
                if ($dboCols.Contains($c) -and $DateTypes -contains $armCols[$c]) { $dateCol = $c; break }
            }
        }

        $keyJoin = (($keyCols | ForEach-Object { "a.[$_] = d.[$_]" }) -join ' AND ')
        $keyExprA = (($keyCols | ForEach-Object { "CONVERT(NVARCHAR(64), a.[$_])" }) -join " + N'|' + ")
        $keyExprD = (($keyCols | ForEach-Object { "CONVERT(NVARCHAR(64), d.[$_])" }) -join " + N'|' + ")

        $pairs += [pscustomobject]@{
            Arm = $armName; Dbo = $dboName; KeyCols = $keyCols; KeyJoin = $keyJoin
            KeyExprA = $keyExprA; KeyExprD = $keyExprD; DateCol = $dateCol; ValueCount = $valueCols.Count
        }
    }

    if ($pairs.Count -eq 0) { Write-Host "   no comparable pairs - skipped" -ForegroundColor Yellow; continue }

    # ------------------------------------------------------------------ emit
    $sb = New-Object System.Text.StringBuilder
    function Add([string]$s) { [void]$sb.AppendLine($s) }

    Add "-- ============================================================================="
    Add "-- 004_Create${loader}Comparison.sql"
    Add "-- arm.usp_CompareArmToDbo - reconciles this loader's [arm] tables against the"
    Add "-- incumbent [dbo] tables already in the $db database."
    Add "--"
    Add "-- GENERATED by scripts\Generate-ArmToDboComparison.ps1 against the live catalog"
    Add "-- on $(Get-Date -Format 'yyyy-MM-dd'). Re-run it after a schema change rather"
    Add "-- than hand-editing the pair list below."
    Add "--"
    Add "-- Reports three issue kinds per table pair, plus a SUMMARY row:"
    Add "--   MISSING_IN_DBO  a key present in arm but not in dbo"
    Add "--   MISSING_IN_ARM  a key present in dbo but not in arm (within the window)"
    Add "--   VALUE_DIFF      one row per differing COLUMN of a key-matched pair"
    Add "--"
    Add "-- The comparison WINDOW matters. dbo holds years of history and arm only the"
    Add "-- window the loader has run for, so an unfiltered compare would report nearly"
    Add "-- all of dbo as MISSING_IN_ARM. With @Date NULL the window is therefore the arm"
    Add "-- table's OWN MIN..MAX of its date column; @Date narrows it to a single day."
    Add "-- Pairs with no date column (dimensions/lookups) are always compared in full."
    Add "--"
    Add "-- Value columns are NOT hard-coded: they are rediscovered at run time as the"
    Add "-- columns common to both tables, minus the join key, minus loader audit columns"
    Add "-- ($($AuditColumns -join ', ')), minus types that cannot be compared with <>."
    Add "-- ============================================================================="
    Add ""
    Add "USE $db;"
    Add "GO"
    Add ""
    Add "CREATE OR ALTER PROCEDURE arm.usp_CompareArmToDbo"
    Add "    @Date            DATE    = NULL,  -- NULL = the arm table's own date range"
    Add "    @TablePair       SYSNAME = NULL,  -- NULL = every pair below"
    Add "    @MaxRowsPerIssue INT     = 1000   -- cap per issue kind per pair"
    Add "AS"
    Add "BEGIN"
    Add "    SET NOCOUNT ON;"
    Add ""
    Add "    -- Pair metadata, resolved against the live catalog at generation time."
    Add "    DECLARE @Pairs TABLE"
    Add "    ("
    Add "        Ord      INT IDENTITY(1,1) NOT NULL,"
    Add "        ArmTable SYSNAME        NOT NULL,"
    Add "        DboTable SYSNAME        NOT NULL,"
    Add "        KeyJoin  NVARCHAR(1000) NOT NULL,"
    Add "        KeyExprA NVARCHAR(1000) NOT NULL,"
    Add "        KeyExprD NVARCHAR(1000) NOT NULL,"
    Add "        DateCol  SYSNAME        NULL"
    Add "    );"
    Add ""
    Add "    INSERT @Pairs (ArmTable, DboTable, KeyJoin, KeyExprA, KeyExprD, DateCol) VALUES"
    # The row comment goes ABOVE its row, never after it: a trailing "-- ..." would
    # swallow the separating comma of the next VALUES row and break the whole INSERT.
    for ($i = 0; $i -lt $pairs.Count; $i++) {
        $p = $pairs[$i]
        $dc = if ($p.DateCol) { "N'$($p.DateCol)'" } else { 'NULL' }
        $tail = if ($i -eq $pairs.Count - 1) { ';' } else { ',' }
        Add "        -- key $($p.KeyCols -join ' + '); ~$($p.ValueCount) comparable value column(s)$(if (-not $p.DateCol) { '; no date column - always compared in full' })"
        Add "        (N'$($p.Arm)', N'$($p.Dbo)', N'$($p.KeyJoin.Replace("'","''"))', N'$($p.KeyExprA.Replace("'","''"))', N'$($p.KeyExprD.Replace("'","''"))', $dc)$tail"
    }
    Add ""
    if ($skipped.Count -gt 0) {
        Add "    -- Deliberately NOT compared:"
        foreach ($s in $skipped) { Add "    --   $s" }
        Add ""
    }
    Add "    IF @TablePair IS NOT NULL DELETE FROM @Pairs WHERE ArmTable <> @TablePair;"
    Add ""
    Add "    CREATE TABLE #Result"
    Add "    ("
    Add "        Seq        INT IDENTITY(1,1) NOT NULL,"
    Add "        TablePair  SYSNAME       NOT NULL,"
    Add "        Issue      VARCHAR(16)   NOT NULL,"
    Add "        KeyValues  NVARCHAR(512) NULL,"
    Add "        ColumnName SYSNAME       NULL,"
    Add "        ArmValue   NVARCHAR(256) NULL,"
    Add "        DboValue   NVARCHAR(256) NULL"
    Add "    );"
    Add ""
    Add "    DECLARE @ArmTable SYSNAME, @DboTable SYSNAME, @KeyJoin NVARCHAR(1000),"
    Add "            @KeyExprA NVARCHAR(1000), @KeyExprD NVARCHAR(1000), @DateCol SYSNAME,"
    Add "            @sql NVARCHAR(MAX), @valueList NVARCHAR(MAX),"
    Add "            @armFilter NVARCHAR(400), @dboFilter NVARCHAR(400),"
    Add "            @lo DATE, @hi DATE, @armRows INT, @dboRows INT;"
    Add ""
    Add "    DECLARE pair_cur CURSOR LOCAL FAST_FORWARD FOR"
    Add "        SELECT ArmTable, DboTable, KeyJoin, KeyExprA, KeyExprD, DateCol FROM @Pairs ORDER BY Ord;"
    Add "    OPEN pair_cur;"
    Add "    FETCH NEXT FROM pair_cur INTO @ArmTable, @DboTable, @KeyJoin, @KeyExprA, @KeyExprD, @DateCol;"
    Add ""
    Add "    WHILE @@FETCH_STATUS = 0"
    Add "    BEGIN"
    Add "        -- ---- comparison window -------------------------------------------------"
    Add "        SET @lo = NULL; SET @hi = NULL;"
    Add "        IF @DateCol IS NOT NULL"
    Add "        BEGIN"
    Add "            IF @Date IS NOT NULL"
    Add "                SELECT @lo = @Date, @hi = @Date;"
    Add "            ELSE"
    Add "            BEGIN"
    Add "                SET @sql = N'SELECT @loOut = MIN(CONVERT(DATE, ' + QUOTENAME(@DateCol) + N')),"
    Add "                                    @hiOut = MAX(CONVERT(DATE, ' + QUOTENAME(@DateCol) + N'))"
    Add "                             FROM arm.' + QUOTENAME(@ArmTable) + N';';"
    Add "                EXEC sp_executesql @sql, N'@loOut DATE OUTPUT, @hiOut DATE OUTPUT', @loOut = @lo OUTPUT, @hiOut = @hi OUTPUT;"
    Add "            END"
    Add "            SET @armFilter = N'CONVERT(DATE, a.' + QUOTENAME(@DateCol) + N') BETWEEN @lo AND @hi';"
    Add "            SET @dboFilter = N'CONVERT(DATE, d.' + QUOTENAME(@DateCol) + N') BETWEEN @lo AND @hi';"
    Add "        END"
    Add "        ELSE"
    Add "        BEGIN"
    Add "            SET @armFilter = N'1 = 1';"
    Add "            SET @dboFilter = N'1 = 1';"
    Add "        END"
    Add ""
    Add "        -- ---- value columns common to both tables, discovered now ---------------"
    Add "        SELECT @valueList = STRING_AGG(CAST("
    Add "                   N'(N' + QUOTENAME(ac.name, '''') + N', CONVERT(NVARCHAR(256), a.' + QUOTENAME(ac.name) + N')'"
    Add "                 + N', CONVERT(NVARCHAR(256), d.' + QUOTENAME(ac.name) + N')'"
    Add "                 + N', CASE WHEN a.' + QUOTENAME(ac.name) + N' IS NULL AND d.' + QUOTENAME(ac.name) + N' IS NULL THEN 0'"
    Add "                 + N' WHEN a.' + QUOTENAME(ac.name) + N' IS NULL OR d.' + QUOTENAME(ac.name) + N' IS NULL THEN 1'"
    Add "                 + N' WHEN a.' + QUOTENAME(ac.name) + N' <> d.' + QUOTENAME(ac.name) + N' THEN 1 ELSE 0 END)'"
    Add "               AS NVARCHAR(MAX)), N',' + CHAR(13) + CHAR(10))"
    Add "               WITHIN GROUP (ORDER BY ac.column_id)"
    Add "        FROM sys.columns ac"
    Add "        JOIN sys.types aty ON aty.user_type_id = ac.user_type_id"
    Add "        JOIN sys.columns dc ON dc.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@DboTable)) AND dc.name = ac.name"
    Add "        JOIN sys.types dty ON dty.user_type_id = dc.user_type_id"
    Add "        WHERE ac.object_id = OBJECT_ID(N'arm.' + QUOTENAME(@ArmTable))"
    Add "          AND ac.name NOT IN ($(($AuditColumns | ForEach-Object { "N'$_'" }) -join ', '))"
    Add "          AND aty.name NOT IN ($(($UnComparableTypes | ForEach-Object { "N'$_'" }) -join ', '))"
    Add "          AND dty.name NOT IN ($(($UnComparableTypes | ForEach-Object { "N'$_'" }) -join ', '))"
    Add "          AND CHARINDEX(N'a.' + QUOTENAME(ac.name) + N' =', @KeyJoin) = 0;   -- never diff the join key"
    Add ""
    Add "        -- ---- rows present on one side only -------------------------------------"
    Add "        SET @sql = N'"
    Add "INSERT #Result (TablePair, Issue, KeyValues, ColumnName, ArmValue, DboValue)"
    Add "SELECT TOP (@cap) @pair, ''MISSING_IN_DBO'', ' + @KeyExprA + N', NULL, NULL, NULL"
    Add "FROM arm.' + QUOTENAME(@ArmTable) + N' a"
    Add "WHERE ' + @armFilter + N'"
    Add "  AND NOT EXISTS (SELECT 1 FROM dbo.' + QUOTENAME(@DboTable) + N' d WHERE ' + @KeyJoin + N');"
    Add ""
    Add "INSERT #Result (TablePair, Issue, KeyValues, ColumnName, ArmValue, DboValue)"
    Add "SELECT TOP (@cap) @pair, ''MISSING_IN_ARM'', ' + @KeyExprD + N', NULL, NULL, NULL"
    Add "FROM dbo.' + QUOTENAME(@DboTable) + N' d"
    Add "WHERE ' + @dboFilter + N'"
    Add "  AND NOT EXISTS (SELECT 1 FROM arm.' + QUOTENAME(@ArmTable) + N' a WHERE ' + @KeyJoin + N');';"
    Add ""
    Add "        EXEC sp_executesql @sql,"
    Add "             N'@cap INT, @pair SYSNAME, @lo DATE, @hi DATE',"
    Add "             @cap = @MaxRowsPerIssue, @pair = @ArmTable, @lo = @lo, @hi = @hi;"
    Add ""
    Add "        -- ---- column-level differences on key-matched rows ----------------------"
    Add "        IF @valueList IS NOT NULL"
    Add "        BEGIN"
    Add "            SET @sql = N'"
    Add "INSERT #Result (TablePair, Issue, KeyValues, ColumnName, ArmValue, DboValue)"
    Add "SELECT TOP (@cap) @pair, ''VALUE_DIFF'', ' + @KeyExprA + N', v.ColumnName, v.ArmValue, v.DboValue"
    Add "FROM arm.' + QUOTENAME(@ArmTable) + N' a"
    Add "JOIN dbo.' + QUOTENAME(@DboTable) + N' d ON ' + @KeyJoin + N'"
    Add "CROSS APPLY (VALUES"
    Add "' + @valueList + N'"
    Add ") v (ColumnName, ArmValue, DboValue, IsDiff)"
    Add "WHERE v.IsDiff = 1 AND ' + @armFilter + N';';"
    Add ""
    Add "            EXEC sp_executesql @sql,"
    Add "                 N'@cap INT, @pair SYSNAME, @lo DATE, @hi DATE',"
    Add "                 @cap = @MaxRowsPerIssue, @pair = @ArmTable, @lo = @lo, @hi = @hi;"
    Add "        END"
    Add ""
    Add "        -- ---- per-pair summary --------------------------------------------------"
    Add "        SET @sql = N'SELECT @aOut = COUNT_BIG(1) FROM arm.' + QUOTENAME(@ArmTable) + N' a WHERE ' + @armFilter + N';';"
    Add "        EXEC sp_executesql @sql, N'@aOut INT OUTPUT, @lo DATE, @hi DATE', @aOut = @armRows OUTPUT, @lo = @lo, @hi = @hi;"
    Add "        SET @sql = N'SELECT @dOut = COUNT_BIG(1) FROM dbo.' + QUOTENAME(@DboTable) + N' d WHERE ' + @dboFilter + N';';"
    Add "        EXEC sp_executesql @sql, N'@dOut INT OUTPUT, @lo DATE, @hi DATE', @dOut = @dboRows OUTPUT, @lo = @lo, @hi = @hi;"
    Add ""
    Add "        INSERT #Result (TablePair, Issue, KeyValues, ColumnName, ArmValue, DboValue)"
    Add "        SELECT @ArmTable, 'SUMMARY',"
    Add "               CASE WHEN @DateCol IS NULL THEN N'(no date column - full compare)'"
    Add "                    ELSE CONCAT(@DateCol, N' ', CONVERT(NVARCHAR(10), @lo, 23), N'..', CONVERT(NVARCHAR(10), @hi, 23)) END,"
    Add "               N'dbo.' + @DboTable,"
    Add "               CONCAT(@armRows, N' arm row(s)'), CONCAT(@dboRows, N' dbo row(s)');"
    Add ""
    Add "        FETCH NEXT FROM pair_cur INTO @ArmTable, @DboTable, @KeyJoin, @KeyExprA, @KeyExprD, @DateCol;"
    Add "    END"
    Add ""
    Add "    CLOSE pair_cur;"
    Add "    DEALLOCATE pair_cur;"
    Add ""
    Add "    SELECT TablePair, Issue, KeyValues, ColumnName, ArmValue, DboValue"
    Add "    FROM #Result"
    Add "    ORDER BY TablePair, CASE Issue WHEN 'SUMMARY' THEN 0 ELSE 1 END, Issue, Seq;"
    Add "END"
    Add "GO"

    $path = Join-Path $RepoRoot "sql\$loader\004_Create${loader}Comparison.sql"
    $sb.ToString() | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host ("   {0} pair(s), {1} skipped -> {2}" -f $pairs.Count, $skipped.Count, (Split-Path $path -Leaf)) -ForegroundColor Green
    $summary += [pscustomobject]@{ Loader = $loader; Pairs = $pairs.Count; Skipped = $skipped.Count }
}

Write-Host ''
$summary | Format-Table -AutoSize
Write-Host ("TOTAL pairs: " + ($summary | Measure-Object -Property Pairs -Sum).Sum)
