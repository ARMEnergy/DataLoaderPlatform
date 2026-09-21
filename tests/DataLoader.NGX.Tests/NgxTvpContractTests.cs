using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// The TVP contract, enforced by the build rather than by review.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If <c>sql/NGX/002</c> and
/// <see cref="NgxDescriptors"/> ever disagree about a column's NAME, ORDER or TYPE,
/// every row loads shifted — silently, with no SQL error, because the neighbouring
/// columns are type-compatible. <c>arm.IndexPrice</c> is especially exposed: it has
/// four <c>DECIMAL(18,8)</c> columns and five <c>VARCHAR(50)</c> columns interleaved,
/// so a one-position slip type-checks perfectly and writes garbage.
/// </para>
/// <para>
/// These tests parse the REAL .sql file rather than a copy, so editing one side without
/// the other fails here.
/// </para>
/// </summary>
public class NgxTvpContractTests
{
    private static string Tvp => File.ReadAllText(RepoPaths.TvpScript);
    private static string Schema => File.ReadAllText(RepoPaths.SchemaScript);
    private static string Procs => File.ReadAllText(RepoPaths.ProceduresScript);

    /// <summary>Compare types insensitively to case and to the spaces inside DECIMAL(18, 8).</summary>
    private static string Norm(string sqlType) =>
        sqlType.Replace(" ", string.Empty).ToUpperInvariant();

    public static TheoryData<string> TableNames() => new()
    {
        NgxDescriptors.IndexPrice.TableName,
        NgxDescriptors.StripTradingSummary.TableName
    };

    private static NgxTableDescriptor Describe(string tableName) =>
        NgxDescriptors.All.Single(t => t.TableName == tableName);

    [Theory]
    [MemberData(nameof(TableNames))]
    public void TvpColumns_MatchDescriptor_ByNameOrderAndType(string tableName)
    {
        var table = Describe(tableName);
        var columns = TvpParser.Parse(Tvp, table.TvpType);

        Assert.True(columns.Count > 0, $"No CREATE TYPE found for {table.TvpType} in 002.");

        Assert.Equal(table.Columns.Count, columns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            Assert.Equal(table.Columns[i].Name, columns[i].Name);
            Assert.Equal(Norm(table.Columns[i].SqlType), Norm(columns[i].SqlType));
        }
    }

    /// <summary>
    /// The TVP must carry the table's columns in the table's own order, minus the
    /// DB-stamped <c>ModifiedAtUtc</c>. This catches a column added to 001 but forgotten
    /// in 002 — which would otherwise only surface as a NULL column in production.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void TvpColumns_AreTheTableColumns_LessModifiedAtUtc(string tableName)
    {
        var table = Describe(tableName);

        var tableColumns = TvpParser
            .CreateTableBody(Schema, table.TableName)
            .Split('\n')
            .Select(l => TvpParser.StripComments(l).Trim())
            .Select(l => System.Text.RegularExpressions.Regex.Match(
                l, @"^\[?(?<name>[A-Za-z][A-Za-z0-9_]*)\]?\s+(?:DATE|INT|VARCHAR|DECIMAL|DATETIME2|BIT)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => m.Groups["name"].Value)
            .ToList();

        Assert.Contains("ModifiedAtUtc", tableColumns);

        Assert.Equal(
            tableColumns.Where(c => c != "ModifiedAtUtc").ToList(),
            table.Columns.Select(c => c.Name).ToList());
    }

    /// <summary>ModifiedAtUtc is DB-stamped and must never be sent by the loader.</summary>
    [Theory]
    [MemberData(nameof(TableNames))]
    public void ModifiedAtUtc_IsNeverATvpColumn(string tableName)
    {
        var table = Describe(tableName);
        Assert.DoesNotContain(table.Columns, c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>002 must declare exactly the two types the descriptors name — no more, no fewer.</summary>
    [Fact]
    public void TvpScript_DeclaresExactlyTheDescribedTypes()
    {
        Assert.Equal(
            NgxDescriptors.All.Select(t => t.TvpType).OrderBy(n => n).ToList(),
            TvpParser.DeclaredTypeNames(Tvp).OrderBy(n => n).ToList());
    }

    /// <summary>
    /// Every primary key component must be NOT NULL in the TVP. A nullable key column
    /// would let a record with a missing key merge under a blank value instead of being
    /// dropped by the reader.
    /// </summary>
    [Theory]
    [InlineData("arm.IndexPriceTvp", "ExecutionDate,IndexId,PriceEffectiveStart,PriceEffectiveEnd")]
    [InlineData("arm.StripTradingSummaryTvp",
        "TradeDateTime,HubId,MarketId,StripType,ExchangeReference,BeginDate,EndDate")]
    public void PrimaryKeyComponents_AreNotNullInTheTvp(string typeName, string keyColumns)
    {
        var columns = TvpParser.Parse(Tvp, typeName).ToDictionary(c => c.Name, c => c.NotNull);

        foreach (var key in keyColumns.Split(','))
        {
            Assert.True(columns.ContainsKey(key), $"{typeName} has no column {key}.");
            Assert.True(columns[key], $"{typeName}.{key} is a key component and must be NOT NULL.");
        }
    }

    /// <summary>
    /// The merge predicate must list the FULL declared primary key. For the strip table
    /// that is seven columns: ExchangeReference is recycled across dates and markets, so
    /// a shorter predicate would collapse genuinely distinct trades onto one another.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeIndexPrice",
        "ExecutionDate,IndexId,PriceEffectiveStart,PriceEffectiveEnd")]
    [InlineData("arm.usp_BulkMergeStripTradingSummary",
        "HubId,MarketId,StripType,TradeDateTime,BeginDate,EndDate,ExchangeReference")]
    public void MergePredicate_MatchesOnEveryKeyColumn(string procName, string keyColumns)
    {
        // The predicates are column-aligned in the .sql, so collapse runs of whitespace
        // before matching rather than guessing at the padding.
        var body = System.Text.RegularExpressions.Regex.Replace(
            TvpParser.StripComments(TvpParser.ProcedureBody(Procs, procName)), @"\s+", " ");

        foreach (var key in keyColumns.Split(','))
            Assert.Contains($"tgt.{key} = s.{key}", body);
    }

    /// <summary>
    /// Neither merge may delete by absence. Each work unit carries one slice of the
    /// window; a DELETE would wipe every other unit's rows — and for the index table,
    /// every prior ExecutionDate.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeIndexPrice")]
    [InlineData("arm.usp_BulkMergeStripTradingSummary")]
    public void Merges_DoNotDeleteByAbsence(string procName)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(Procs, procName));
        Assert.DoesNotContain("NOT MATCHED BY SOURCE", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every non-key column must be refreshed on a match. A column present in the TVP
    /// but missing from the UPDATE set would go stale the moment a value changed — for
    /// the index feed that means a settlement state stuck on 'Projected'.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeIndexPrice", "arm.IndexPriceTvp",
        "ExecutionDate,IndexId,PriceEffectiveStart,PriceEffectiveEnd")]
    [InlineData("arm.usp_BulkMergeStripTradingSummary", "arm.StripTradingSummaryTvp",
        "TradeDateTime,HubId,MarketId,StripType,ExchangeReference,BeginDate,EndDate")]
    public void Merges_UpdateEveryNonKeyColumn(string procName, string typeName, string keyColumns)
    {
        var keys = keyColumns.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(Procs, procName));

        foreach (var column in TvpParser.Parse(Tvp, typeName).Select(c => c.Name).Where(c => !keys.Contains(c)))
            Assert.Contains($"= s.{column}", body);
    }

    /// <summary>
    /// The MERGE's <c>INSERT (…)</c> and <c>VALUES (…)</c> lists must line up
    /// POSITIONALLY.
    ///
    /// <para>
    /// The other contract tests cover the <c>ON</c> predicate and the <c>UPDATE SET</c>
    /// clause, both of which name their columns in pairs and so cannot slip. The MERGE
    /// INSERT is the one place left where a one-position shift type-checks perfectly and
    /// loads every inserted row with its values rotated — and for <c>arm.IndexPrice</c>,
    /// with four <c>DECIMAL(18,8)</c> columns in a row, it would not even look wrong.
    /// </para>
    /// <para>
    /// The trailing <c>ModifiedAtUtc</c> ↔ <c>SYSUTCDATETIME()</c> pair is the one
    /// deliberate mismatch and is asserted explicitly rather than skipped.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeIndexPrice")]
    [InlineData("arm.usp_BulkMergeStripTradingSummary")]
    public void MergeInsert_ColumnListAndValuesList_AlignPositionally(string procName)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(Procs, procName));

        var insert = ParenListAfter(body, "INSERT");
        var values = ParenListAfter(body, "VALUES");

        Assert.NotEmpty(insert);
        Assert.Equal(insert.Count, values.Count);

        for (var i = 0; i < insert.Count; i++)
        {
            var column = insert[i].Trim('[', ']');

            if (column.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal("SYSUTCDATETIME()", values[i]);
                continue;
            }

            Assert.StartsWith("s.", values[i], StringComparison.Ordinal);
            Assert.Equal(column, values[i][2..].Trim('[', ']'));
        }

        // The last column really is the DB-stamped one, so the loop above actually
        // exercised the special case rather than vacuously passing.
        Assert.Equal("ModifiedAtUtc", insert[^1].Trim('[', ']'));
    }

    /// <summary>Comma-separated items of the first balanced paren group after a keyword.</summary>
    private static List<string> ParenListAfter(string sql, string keyword)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            sql, $@"\b{keyword}\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.True(match.Success, $"No '{keyword} (' found in the procedure body.");

        var open = match.Index + match.Length - 1;
        var depth = 0;
        var close = -1;

        for (var i = open; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && --depth == 0) { close = i; break; }
        }

        Assert.True(close > open, $"Unbalanced parentheses after '{keyword}'.");

        var items = new List<string>();
        var start = open + 1;
        depth = 0;

        for (var i = start; i < close; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
            else if (sql[i] == ',' && depth == 0)
            {
                items.Add(Squash(sql[start..i]));
                start = i + 1;
            }
        }

        items.Add(Squash(sql[start..close]));
        return items;

        static string Squash(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"\s+", string.Empty);
    }

    /// <summary>The drop script must name every object the create scripts make.</summary>
    [Fact]
    public void DropScript_CoversEveryCreatedObject()
    {
        var drop = File.ReadAllText(RepoPaths.DropScript);

        foreach (var table in NgxDescriptors.All)
        {
            Assert.Contains(table.TableName, drop);
            Assert.Contains(table.TvpType, drop);
            Assert.Contains(table.MergeProc, drop);
        }

        Assert.Contains("arm.usp_ValidateLoad", drop);
    }

    /// <summary>
    /// The drop script must not touch the dbo objects. This database also holds the LIVE
    /// incumbent tables and the dbo.[Index] catalogue this loader reads; a drop script
    /// that reached into dbo would be catastrophic and irreversible.
    /// </summary>
    [Fact]
    public void DropScript_NeverNamesADboObject()
    {
        var drop = TvpParser.StripComments(File.ReadAllText(RepoPaths.DropScript));
        Assert.DoesNotContain("dbo.", drop, StringComparison.OrdinalIgnoreCase);
    }
}
