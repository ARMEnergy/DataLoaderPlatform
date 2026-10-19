using Xunit;

namespace DataLoader.NUCORE.Tests;

/// <summary>
/// The TVP contract: name + ORDER + type + nullability must agree between the C#
/// descriptors, the TVP types in <c>sql/NUCORE/002</c> and the tables in
/// <c>sql/NUCORE/001</c>.
///
/// <para>
/// This matters more here than in most loaders because a table-valued parameter binds BY
/// POSITION across 436 columns. If the descriptor and the TVP disagree about one column,
/// SQL Server does not complain - it shifts every later value by one and writes a row that
/// is entirely plausible and entirely wrong. These tests read the REAL .sql files, so a
/// one-sided edit fails the build.
/// </para>
/// </summary>
public class NucoreTvpContractTests
{
    private static readonly string TvpSql    = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string TableSql  = File.ReadAllText(RepoPaths.TablesScript);
    private static readonly string ProcSql   = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string DropSql   = File.ReadAllText(RepoPaths.DropScript);

    public static TheoryData<string> EndpointNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in NucoreDescriptors.All) data.Add(e.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EndpointNames))]
    public void TvpColumnsMatchDescriptorByNameOrderTypeAndNullability(string endpointName)
    {
        var endpoint = NucoreDescriptors.Find(endpointName)!;
        var declared = TvpParser.Parse(TvpSql, endpoint.TvpType);

        Assert.True(declared.Count > 0, $"No CREATE TYPE found for {endpoint.TvpType}.");

        Assert.Equal(endpoint.Columns.Count, declared.Count);

        for (var i = 0; i < endpoint.Columns.Count; i++)
        {
            var expected = endpoint.Columns[i];
            var actual = declared[i];

            // Position is asserted alongside the name, because a column list that contains
            // the right names in the wrong ORDER is exactly the failure mode a TVP hides.
            Assert.True(
                string.Equals(expected.Name, actual.Name, StringComparison.Ordinal),
                $"{endpoint.Name} column {i}: descriptor says '{expected.Name}', 002 says '{actual.Name}'.");

            Assert.True(
                string.Equals(expected.SqlType, actual.SqlType, StringComparison.OrdinalIgnoreCase),
                $"{endpoint.Name}.{expected.Name}: descriptor says '{expected.SqlType}', 002 says '{actual.SqlType}'.");

            Assert.True(
                expected.Nullable == !actual.NotNull,
                $"{endpoint.Name}.{expected.Name}: descriptor nullable={expected.Nullable}, " +
                $"002 says {(actual.NotNull ? "NOT NULL" : "NULL")}.");
        }
    }

    [Theory]
    [MemberData(nameof(EndpointNames))]
    public void TableColumnsMatchTheTvpColumns(string endpointName)
    {
        var endpoint = NucoreDescriptors.Find(endpointName)!;
        var tableColumns = TvpParser.ParseTable(TableSql, endpoint.TableName);

        // The table carries the vendor columns plus the two DB-stamped audit columns, and
        // the ReplaceByDate table additionally leads with its surrogate identity.
        var expectedExtra = new List<string> { "FileLogId", "ModifiedAtUtc" };
        var leading = new List<string>();
        if (endpoint.IsReplaceMode)
            leading.Add($"{endpoint.Name}Id");

        var expectedNames = leading
            .Concat(endpoint.Columns.Select(c => c.Name))
            .Concat(expectedExtra)
            .ToList();

        Assert.Equal(expectedNames, tableColumns.Select(c => c.Name).ToList());

        foreach (var column in endpoint.Columns)
        {
            var actual = tableColumns.Single(c => c.Name == column.Name);
            Assert.True(
                string.Equals(column.SqlType, actual.SqlType, StringComparison.OrdinalIgnoreCase),
                $"{endpoint.Name}.{column.Name}: descriptor '{column.SqlType}' vs table '{actual.SqlType}'.");
            Assert.True(
                column.Nullable == !actual.NotNull,
                $"{endpoint.Name}.{column.Name}: nullability differs between descriptor and table.");
        }
    }

    [Fact]
    public void AuditColumnsAreNeverTvpColumns()
    {
        // FileLogId and ModifiedAtUtc are set by the procedure. If either leaked into a TVP
        // the positional binding would shift and ModifiedAtUtc would stop meaning "when this
        // row last actually changed".
        foreach (var endpoint in NucoreDescriptors.All)
        {
            Assert.DoesNotContain(endpoint.Columns, c => c.Name == "FileLogId");
            Assert.DoesNotContain(endpoint.Columns, c => c.Name == "ModifiedAtUtc");
        }
    }

    [Fact]
    public void EveryDeclaredTvpBelongsToAnEndpoint()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql);
        var expected = NucoreDescriptors.All.Select(e => e.TvpType).ToList();

        Assert.Equal(expected.OrderBy(x => x), declared.OrderBy(x => x));
    }

    [Theory]
    [MemberData(nameof(EndpointNames))]
    public void MergeProcedureTakesTheRightTvpAndReturnsACount(string endpointName)
    {
        var endpoint = NucoreDescriptors.Find(endpointName)!;
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, endpoint.MergeProc));

        Assert.Contains("READONLY", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(endpoint.Name + "Type", body, StringComparison.OrdinalIgnoreCase);

        // The sink reads the row count with ExecuteScalar, so the proc must SELECT one.
        Assert.Contains("SELECT @Changed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EndpointNames))]
    public void MergeProcedureMentionsEveryColumn(string endpointName)
    {
        var endpoint = NucoreDescriptors.Find(endpointName)!;
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, endpoint.MergeProc));

        // A column present in the TVP but absent from the INSERT list would silently never
        // be written - the procedure would still compile and still report rows merged.
        foreach (var column in endpoint.Columns)
            Assert.True(
                body.Contains($"[{column.Name}]", StringComparison.Ordinal),
                $"{endpoint.MergeProc} never references [{column.Name}].");
    }

    [Fact]
    public void MergeProceduresJoinOnExactlyTheDeclaredKey()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.WriteMode != NucoreWriteMode.Merge) continue;

            var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, endpoint.MergeProc));

            foreach (var key in endpoint.KeyColumns)
                Assert.True(
                    body.Contains($"t.[{key.Name}] = s.[{key.Name}]", StringComparison.Ordinal),
                    $"{endpoint.MergeProc} does not join on key column [{key.Name}].");

            // A key column must never appear in the UPDATE SET list: updating a column the
            // MERGE matched on is both pointless and, for a clustered PK, a row move.
            foreach (var key in endpoint.KeyColumns)
                Assert.False(
                    body.Contains($"t.[{key.Name}] = s.[{key.Name}],", StringComparison.Ordinal) &&
                    body.IndexOf("UPDATE SET", StringComparison.OrdinalIgnoreCase) <
                    body.IndexOf($"t.[{key.Name}] = s.[{key.Name}],", StringComparison.Ordinal),
                    $"{endpoint.MergeProc} appears to UPDATE key column [{key.Name}].");
        }
    }

    [Fact]
    public void ChangeDetectionUsesExceptRatherThanInequality()
    {
        // A "t.col <> s.col OR ..." chain evaluates to UNKNOWN wherever either side is NULL,
        // so changes into and out of NULL stop being detected and ModifiedAtUtc silently
        // goes stale. EXCEPT treats NULL as equal to NULL, which is what is wanted.
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.WriteMode != NucoreWriteMode.Merge) continue;

            var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, endpoint.MergeProc));
            Assert.Contains("EXCEPT", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryReplaceProcedureDeletesOnlyTheScopesInItsBatch()
    {
        var replacing = NucoreDescriptors.All.Where(e => e.IsReplaceMode).ToList();
        Assert.Equal(3, replacing.Count);

        foreach (var endpoint in replacing)
        {
            var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, endpoint.MergeProc));

            Assert.Contains("DELETE", body, StringComparison.OrdinalIgnoreCase);

            // The load-bearing half: the DELETE must be correlated to the batch's own
            // scopes. An uncorrelated DELETE would empty the whole table on every batch.
            Assert.Contains("FROM @Records", body, StringComparison.OrdinalIgnoreCase);

            foreach (var scope in endpoint.ScopeColumns)
                Assert.True(
                    body.Contains($"r.[{scope}] = t.[{scope}]", StringComparison.Ordinal),
                    $"{endpoint.MergeProc} does not correlate its DELETE on scope column [{scope}].");

            Assert.Contains("BEGIN TRANSACTION", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("COMMIT TRANSACTION", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryReplaceTableIsClusteredOnItsScope()
    {
        // The per-scope DELETE runs on every batch. Without a clustered index leading with
        // the scope it is a table scan - on PositionDetails, of millions of rows.
        var tables = TvpParser.StripComments(TableSql);

        foreach (var endpoint in NucoreDescriptors.All.Where(e => e.IsReplaceMode))
        {
            var marker = $"CREATE CLUSTERED INDEX [CIX_{endpoint.Name}]";
            var index = tables.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            Assert.True(index >= 0, $"No clustered index found for {endpoint.Name}.");

            var clause = tables[index..tables.IndexOf(';', index)];
            foreach (var scope in endpoint.ScopeColumns)
                Assert.True(
                    clause.Contains($"[{scope}]", StringComparison.Ordinal),
                    $"CIX_{endpoint.Name} does not include scope column [{scope}].");
        }
    }

    [Fact]
    public void EverySqlScriptDeclaresItsDatabase()
    {
        // Repo rule (.claude/skills/sql-use-database): these run by hand in SSMS against a
        // server holding every loader's database side by side.
        foreach (var path in new[]
                 {
                     RepoPaths.TablesScript, RepoPaths.TvpScript,
                     RepoPaths.ProceduresScript, RepoPaths.DropScript
                 })
        {
            var sql = TvpParser.StripComments(File.ReadAllText(path));
            Assert.Contains("USE [NUCORE]", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DropScriptRemovesEveryObjectAndNothingElse()
    {
        var sql = TvpParser.StripComments(DropSql);

        foreach (var endpoint in NucoreDescriptors.All)
        {
            Assert.Contains($"DROP TABLE IF EXISTS [dbo].[{endpoint.Name}]", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"DROP TYPE IF EXISTS [dbo].[{endpoint.Name}Type]", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"DROP PROCEDURE IF EXISTS [dbo].[usp_Merge{endpoint.Name}]", sql, StringComparison.OrdinalIgnoreCase);
        }

        // Every DROP is schema-qualified. An unqualified one would resolve against whatever
        // default schema the connection happens to have.
        var drops = System.Text.RegularExpressions.Regex.Matches(sql, @"DROP\s+(TABLE|TYPE|PROCEDURE)\s+IF\s+EXISTS\s+(\S+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.NotEmpty(drops);
        foreach (System.Text.RegularExpressions.Match m in drops)
            Assert.StartsWith("[dbo].", m.Groups[2].Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionDetailsWatermarkIsServedByItsClusteredIndex()
    {
        // The watermark is MAX(SystemDate) WHERE TradeMonth = @TradeMonth. The clustered
        // index on (TradeMonth, SystemDate) serves that as a range seek, which is why no
        // separate non-clustered index exists for it.
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        Assert.Equal(new[] { "TradeMonth", "SystemDate" }, endpoint.ScopeColumns.ToArray());

        var tables = TvpParser.StripComments(TableSql);
        Assert.Contains(
            "CREATE CLUSTERED INDEX [CIX_PositionDetails] ON [dbo].[PositionDetails] ([TradeMonth], [SystemDate])",
            tables, StringComparison.OrdinalIgnoreCase);

        var procs = TvpParser.StripComments(ProcSql);
        Assert.Contains("usp_GetPositionDetailsWatermark", procs, StringComparison.OrdinalIgnoreCase);
    }
}
