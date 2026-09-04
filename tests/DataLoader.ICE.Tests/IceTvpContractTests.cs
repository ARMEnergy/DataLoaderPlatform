using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable the sink builds and
/// the <c>CREATE TYPE</c> in <c>sql/ICE/002</c> disagree on column order or type,
/// SQL Server does not complain — it writes every row into the wrong columns. The
/// repo's <c>tvp-contract-check</c> skill exists because that failure is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILE and comparing it to
/// <see cref="IceDescriptors"/>, which is also what the sink builds its DataTable
/// from. Editing one side without the other fails the build, so the contract is
/// enforced continuously rather than at review time.
/// </para>
/// </summary>
public sealed class IceTvpContractTests
{
    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);

    public static TheoryData<string> AllTableNames()
    {
        var data = new TheoryData<string>();
        foreach (var table in IceDescriptors.AllTables) data.Add(table.TableName);
        return data;
    }

    private static IceTableDescriptor Table(string tableName) =>
        IceDescriptors.AllTables.Single(t => t.TableName == tableName);

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Tvp_matches_descriptor_by_name_order_and_type(string tableName)
    {
        var table = Table(tableName);
        var sqlColumns = TvpParser.Parse(TvpSql, table.TvpType);

        Assert.True(sqlColumns.Count > 0, $"No CREATE TYPE found for {table.TvpType} in 002.");
        Assert.Equal(table.Columns.Count, sqlColumns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var expected = table.Columns[i];
            var actual = sqlColumns[i];

            Assert.True(
                string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase),
                $"{table.TvpType} column {i}: descriptor says '{expected.Name}', 002 says '{actual.Name}'.");

            Assert.True(
                NormalizeType(expected.SqlType) == NormalizeType(actual.SqlType),
                $"{table.TvpType} column {i} '{expected.Name}': descriptor says {expected.SqlType}, 002 says {actual.SqlType}.");

            Assert.True(
                expected.Required == actual.NotNull,
                $"{table.TvpType} column {i} '{expected.Name}': descriptor Required={expected.Required}, " +
                $"002 NOT NULL={actual.NotNull}.");
        }
    }

    /// <summary>
    /// <c>ModifiedAtUtc</c> and <c>DateCreated</c> are DB-stamped defaults. A TVP that
    /// carried them would either overwrite the server's value or shift every
    /// following column.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Tvp_never_carries_db_stamped_columns(string tableName)
    {
        var table = Table(tableName);

        Assert.DoesNotContain(table.Columns, c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("DateCreated", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every type declared in 002 must belong to a table — no orphans left by an edit.</summary>
    [Fact]
    public void Every_type_in_the_script_belongs_to_a_table()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql);
        var expected = IceDescriptors.AllTables.Select(t => t.TvpType).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(IceDescriptors.AllTables.Count, declared.Count);
        foreach (var name in declared)
            Assert.True(expected.Contains(name), $"002 declares {name}, which no table descriptor uses.");
    }

    /// <summary>Every merge proc the descriptors name must exist in 003.</summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_exists_and_takes_the_right_tvp(string tableName)
    {
        var table = Table(tableName);

        var pattern = new Regex(
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(table.MergeProc)}\s+@Records\s+{Regex.Escape(table.TvpType)}\s+READONLY",
            RegexOptions.IgnoreCase);

        Assert.True(pattern.IsMatch(ProcSql),
            $"003 has no '{table.MergeProc}' taking '@Records {table.TvpType} READONLY'.");
    }

    /// <summary>Every target table the descriptors name must exist in 001.</summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Target_table_is_created_in_the_schema_script(string tableName)
    {
        Assert.Contains($"CREATE TABLE {tableName}", SchemaSql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every descriptor column must be a real column of its target table in 001, in
    /// the same order. This catches the drift 002 alone cannot: a TVP and a
    /// descriptor that agree with each other but not with the table.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Descriptor_columns_appear_in_the_table_in_order(string tableName)
    {
        var table = Table(tableName);
        var body = TvpParser.CreateTableBody(SchemaSql, tableName);

        var searchFrom = 0;
        foreach (var column in table.Columns)
        {
            var index = body.IndexOf(column.Name, searchFrom, StringComparison.OrdinalIgnoreCase);
            Assert.True(index >= 0,
                $"001 CREATE TABLE {tableName} does not declare '{column.Name}' after position {searchFrom}. " +
                "Descriptor column order must match the table.");
            searchFrom = index + column.Name.Length;
        }
    }

    /// <summary>
    /// Tables must not share a merge proc or a TVP.
    /// <see cref="Core.Concurrency.SqlWriteGate"/> keys on the proc name, so sharing
    /// one would serialize unrelated tables — and a copy-paste error here would
    /// silently write one table's rows into another's.
    /// </summary>
    [Fact]
    public void Table_names_tvps_and_procs_are_all_distinct()
    {
        var tables = IceDescriptors.AllTables;

        Assert.Equal(tables.Count, tables.Select(t => t.TableName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(tables.Count, tables.Select(t => t.TvpType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(tables.Count, tables.Select(t => t.MergeProc).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The one approved deviation from the supplied DDL, pinned so it cannot be
    /// quietly reverted: <c>arm.Futures.ProductId</c> is REQUIRED (a PK column).
    /// Without it, ICE's reuse of contract codes across markets silently drops ~73
    /// rows/day — see docs/apis/ICE.md 5.1.
    /// </summary>
    [Fact]
    public void Futures_ProductId_is_required_because_it_is_a_key_column()
    {
        var productId = IceDescriptors.FuturesTable.Columns
            .Single(c => c.Name == "ProductId");

        Assert.True(productId.Required,
            "arm.Futures.ProductId must be Required — it is the 5th primary-key column. " +
            "See docs/apis/ICE.md 5.1.");

        Assert.Contains(
            "TradeDate, Contract, ContractType, Strip, ProductId",
            SchemaSql);
    }

    /// <summary>
    /// The sibling tables must NOT have picked up that change by copy-paste: only
    /// arm.Futures needed it, and requiring ProductId elsewhere would drop rows that
    /// legitimately have none.
    /// </summary>
    [Theory]
    [InlineData("arm.EnvFutures")]
    [InlineData("arm.ICEClearedPowerFutures")]
    [InlineData("arm.EnvOptions")]
    [InlineData("arm.ICEClearedPowerOptions")]
    [InlineData("arm.Options")]
    public void ProductId_is_optional_everywhere_else(string tableName)
    {
        var productId = Table(tableName).Columns.Single(c => c.Name == "ProductId");
        Assert.False(productId.Required, $"{tableName}.ProductId is not a key column and must stay optional.");
    }

    /// <summary>
    /// Every merge proc must de-duplicate its source. ICE_Crude_Oil_Index_Trades
    /// genuinely ships exact duplicate rows, and MERGE errors the whole batch on a
    /// duplicated source key.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_deduplicates_its_source(string tableName)
    {
        var table = Table(tableName);
        var body = TvpParser.ProcedureBody(ProcSql, table.MergeProc);

        Assert.Contains("ROW_NUMBER() OVER", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE rn = 1", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No merge may delete by absence. Each work unit merges ONE file; a short or
    /// partial download would otherwise wipe good rows for the whole trade date.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_never_deletes_by_absence(string tableName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc);
        Assert.DoesNotContain("NOT MATCHED BY SOURCE", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rows this loader writes must carry true UTC, not the table's local-time DEFAULT.</summary>
    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void Merge_procedure_stamps_ModifiedAtUtc_explicitly(string tableName)
    {
        var body = TvpParser.ProcedureBody(ProcSql, Table(tableName).MergeProc);
        Assert.Contains("SYSUTCDATETIME()", body, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeType(string sqlType) =>
        sqlType.Replace(" ", string.Empty).ToUpperInvariant();
}

/// <summary>Minimal T-SQL reader: enough to pull column lists and bodies out of the scripts.</summary>
internal static class TvpParser
{
    internal readonly record struct Column(string Name, string SqlType, bool NotNull);

    public static IReadOnlyList<Column> Parse(string sql, string typeName)
    {
        var header = new Regex(
            $@"CREATE\s+TYPE\s+{Regex.Escape(typeName)}\s+AS\s+TABLE\s*\(",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success) return Array.Empty<Column>();

        var open = match.Index + match.Length - 1;   // index of the '('
        return SplitTopLevel(ReadBalanced(sql, open)).Select(ParseColumn).ToList();
    }

    public static IReadOnlyList<string> DeclaredTypeNames(string sql) =>
        Regex.Matches(sql, @"CREATE\s+TYPE\s+([A-Za-z0-9_\.\[\]]+)\s+AS\s+TABLE", RegexOptions.IgnoreCase)
             .Select(m => m.Groups[1].Value.Replace("[", string.Empty).Replace("]", string.Empty))
             .ToList();

    /// <summary>Body of a <c>CREATE TABLE</c>, for order checks against the descriptor.</summary>
    public static string CreateTableBody(string sql, string tableName)
    {
        var header = new Regex($@"CREATE\s+TABLE\s+{Regex.Escape(tableName)}\s*\(", RegexOptions.IgnoreCase);
        var match = header.Match(sql);

        if (!match.Success)
            throw new InvalidOperationException($"No CREATE TABLE found for {tableName}.");

        return ReadBalanced(sql, match.Index + match.Length - 1);
    }

    /// <summary>Text of one procedure, from its CREATE to the next one (or end of file).</summary>
    public static string ProcedureBody(string sql, string procName)
    {
        var header = new Regex(
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(procName)}\b",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success)
            throw new InvalidOperationException($"No procedure found named {procName}.");

        var next = Regex.Match(sql[(match.Index + match.Length)..], @"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+", RegexOptions.IgnoreCase);
        var length = next.Success ? next.Index : sql.Length - match.Index - match.Length;

        return sql.Substring(match.Index, match.Length + length);
    }

    /// <summary>Content between <paramref name="openIndex"/>'s paren and its match.</summary>
    private static string ReadBalanced(string sql, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0) return sql[(openIndex + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced parentheses in body.");
    }

    /// <summary>Split on commas at depth 0, so DECIMAL(18,6) stays intact.</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') depth--;
            else if (body[i] == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        yield return body[start..];
    }

    private static Column ParseColumn(string declaration)
    {
        // Strip comments so a "-- note" after a column does not become part of it.
        var cleaned = string.Join(' ',
            declaration.Split('\n')
                       .Select(line =>
                       {
                           var comment = line.IndexOf("--", StringComparison.Ordinal);
                           return comment >= 0 ? line[..comment] : line;
                       }))
            .Trim();

        var notNull = Regex.IsMatch(cleaned, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);

        var withoutNullability = Regex.Replace(cleaned, @"\b(NOT\s+)?NULL\b", string.Empty, RegexOptions.IgnoreCase).Trim();

        var parts = Regex.Match(withoutNullability, @"^\[?(?<name>[A-Za-z0-9_]+)\]?\s+(?<type>.+)$");
        if (!parts.Success)
            throw new InvalidOperationException($"Could not parse column declaration '{declaration}'.");

        return new Column(
            parts.Groups["name"].Value.Trim(),
            Regex.Replace(parts.Groups["type"].Value.Trim(), @"\s+", string.Empty),
            notNull);
    }
}

/// <summary>Locates <c>sql/ICE</c> by walking up from the test binary.</summary>
internal static class RepoPaths
{
    public static string SqlDirectory { get; } = FindSqlDirectory();

    public static string SchemaScript => Path.Combine(SqlDirectory, "001_CreateIceSchema.sql");
    public static string TvpScript => Path.Combine(SqlDirectory, "002_CreateIceTvpTypes.sql");
    public static string ProceduresScript => Path.Combine(SqlDirectory, "003_CreateIceProcedures.sql");
    public static string DropScript => Path.Combine(SqlDirectory, "999_DropIceObjects.sql");

    private static string FindSqlDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sql", "ICE");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sql/ICE by walking up from '{AppContext.BaseDirectory}'.");
    }
}
