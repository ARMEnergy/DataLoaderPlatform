using Xunit;
using System.Text.RegularExpressions;

namespace DataLoader.CME.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION, so if <c>sql/CME/002</c> and
/// <see cref="CmeDescriptors"/> disagree about a column's name, order, type or
/// nullability, every row is written one column out of place — and the server
/// will happily accept it whenever the shifted types remain compatible. Nothing
/// in a green build or a passing unit test would notice.
/// </para>
/// <para>
/// So these tests parse the REAL <c>.sql</c> files off disk and assert them
/// against the descriptors. Editing one side alone fails the build.
/// </para>
/// </summary>
public class CmeTvpContractTests
{
    private static readonly Regex TypeBlock = new(
        @"CREATE\s+TYPE\s+(?<name>\[?arm\]?\.\[?\w+\]?)\s+AS\s+TABLE\s*\((?<body>.*?)\)\s*;",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>One declared TVP column: name, type text, and whether it is NOT NULL.</summary>
    private sealed record SqlColumn(string Name, string SqlType, bool NotNull);

    private static IReadOnlyList<SqlColumn> ParseType(string sql, string typeName)
    {
        var match = TypeBlock.Matches(sql)
            .SingleOrDefault(m => Unbracket(m.Groups["name"].Value)
                .Equals(typeName, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(match); // the type must exist in 002 at all
        return ParseColumns(match!.Groups["body"].Value);
    }

    private static IReadOnlyList<SqlColumn> ParseColumns(string body)
    {
        var columns = new List<SqlColumn>();

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',').Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;

            // <name> <type>[(args)] [NOT NULL | NULL]
            var m = Regex.Match(line,
                @"^(?<name>\[?\w+\]?)\s+(?<type>[A-Za-z0-9_]+(\s*\([^)]*\))?)\s*(?<null>NOT\s+NULL|NULL)?$",
                RegexOptions.IgnoreCase);

            if (!m.Success) continue;

            columns.Add(new SqlColumn(
                Unbracket(m.Groups["name"].Value),
                Normalize(m.Groups["type"].Value),
                m.Groups["null"].Value.Replace(" ", string.Empty).Equals("NOTNULL", StringComparison.OrdinalIgnoreCase)));
        }

        return columns;
    }

    private static string Unbracket(string value) => value.Replace("[", string.Empty).Replace("]", string.Empty);

    /// <summary>Collapse whitespace inside a type so <c>DECIMAL(18, 8)</c> matches <c>DECIMAL(18,8)</c>.</summary>
    private static string Normalize(string sqlType) =>
        Regex.Replace(sqlType, @"\s+", string.Empty).ToUpperInvariant();

    public static TheoryData<string> TableIds() => new() { "Option", "Future" };

    [Theory]
    [MemberData(nameof(TableIds))]
    public void TvpMatchesDescriptorByNameOrderTypeAndNullability(string tableId)
    {
        var descriptor = CmeDescriptors.All.Single(t => t.TableId == tableId);
        var declared = ParseType(TestHelpers.SqlText("002_CreateCmeTvpTypes.sql"), descriptor.TvpType);

        Assert.Equal(descriptor.Columns.Count, declared.Count);

        for (var i = 0; i < descriptor.Columns.Count; i++)
        {
            var expected = descriptor.Columns[i];
            var actual = declared[i];

            // Name AND position together — a rename or a reorder both matter.
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(Normalize(expected.SqlType), actual.SqlType);
            Assert.Equal(expected.Required, actual.NotNull);
        }
    }

    /// <summary>
    /// The two types are deliberately NOT the same shape — the option type puts
    /// ProductDescription before ContractYear, the future type puts it after
    /// ContractMonth. Swapping them is the single easiest mistake to make here, so
    /// it gets its own test.
    /// </summary>
    [Fact]
    public void TheTwoTvpsKeepTheirDifferentDescriptionPositions()
    {
        var sql = TestHelpers.SqlText("002_CreateCmeTvpTypes.sql");

        var option = ParseType(sql, "arm.STLBASIC_OptionTvp").Select(c => c.Name).ToList();
        var future = ParseType(sql, "arm.STLBASIC_FutureTvp").Select(c => c.Name).ToList();

        Assert.Equal(3, option.IndexOf("ProductSymbol"));
        Assert.Equal(4, option.IndexOf("ProductDescription"));
        Assert.Equal(5, option.IndexOf("ContractYear"));

        Assert.Equal(5, future.IndexOf("ContractMonth"));
        Assert.Equal(6, future.IndexOf("ProductDescription"));

        // And the option type carries the two columns the future type must not.
        Assert.Contains("PutCall", option);
        Assert.Contains("Strike", option);
        Assert.DoesNotContain("PutCall", future);
        Assert.DoesNotContain("Strike", future);
    }

    [Theory]
    [MemberData(nameof(TableIds))]
    public void RowOrdinalIsTheLastTvpColumnAndIsNotATableColumn(string tableId)
    {
        var descriptor = CmeDescriptors.All.Single(t => t.TableId == tableId);
        var declared = ParseType(TestHelpers.SqlText("002_CreateCmeTvpTypes.sql"), descriptor.TvpType);

        Assert.Equal("RowOrdinal", declared[^1].Name);
        Assert.True(declared[^1].NotNull);

        // The fact table must NOT have it: it is a merge tiebreak, not data.
        var tableBody = TableBody(TestHelpers.SqlText("001_CreateCmeSchema.sql"), descriptor.TargetTable);
        Assert.DoesNotContain("RowOrdinal", tableBody);
    }

    /// <summary>
    /// Every TVP column must also exist on the target table (except RowOrdinal),
    /// because the merge in 003 writes them straight across. A column present in
    /// the type but missing from the table would fail only at deployment.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableIds))]
    public void EveryTvpColumnExistsOnTheTargetTable(string tableId)
    {
        var descriptor = CmeDescriptors.All.Single(t => t.TableId == tableId);
        var tableBody = TableBody(TestHelpers.SqlText("001_CreateCmeSchema.sql"), descriptor.TargetTable);
        var tableColumns = ParseColumns(tableBody).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in descriptor.Columns.Where(c => c.Name != "RowOrdinal"))
            Assert.Contains(column.Name, tableColumns);
    }

    /// <summary>
    /// The declared primary key in 001 must be exactly the descriptor's key
    /// columns, in the same order — the merge's ON clause and the batch de-dup's
    /// PARTITION BY are both generated from the descriptor's list.
    /// </summary>
    [Theory]
    [MemberData(nameof(TableIds))]
    public void PrimaryKeyInSqlMatchesTheDescriptorKey(string tableId)
    {
        var descriptor = CmeDescriptors.All.Single(t => t.TableId == tableId);
        var sql = TestHelpers.SqlText("001_CreateCmeSchema.sql");

        var m = Regex.Match(sql,
            $@"CONSTRAINT\s+PK_ARM_{Regex.Escape(descriptor.TableId == "Option" ? "STLBASIC_Option" : "STLBASIC_Future")}\s+PRIMARY\s+KEY\s+CLUSTERED\s*\((?<cols>[^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(m.Success, $"no clustered PK found for {descriptor.TargetTable}");

        var declared = m.Groups["cols"].Value
            .Split(',')
            .Select(s => Unbracket(s.Trim()))
            .Where(s => s.Length > 0)
            .ToList();

        Assert.Equal(descriptor.KeyColumns, declared);
    }

    /// <summary>
    /// The merge's PARTITION BY must cover the WHOLE primary key. A short
    /// PARTITION BY would let two rows with the same key both survive de-dup and
    /// then make MERGE raise "cannot UPDATE/INSERT the same row more than once".
    /// </summary>
    [Theory]
    [MemberData(nameof(TableIds))]
    public void MergeDeduplicatesOnTheFullPrimaryKey(string tableId)
    {
        var descriptor = CmeDescriptors.All.Single(t => t.TableId == tableId);
        var sql = TestHelpers.SqlText("003_CreateCmeProcedures.sql");

        // Anchor on the CREATE, not on the first mention of the name — the script's
        // header comment lists every proc, so a bare IndexOf would slice the
        // comment block instead of the procedure body.
        var procStart = sql.IndexOf(
            $"CREATE OR ALTER PROCEDURE {descriptor.MergeProc}", StringComparison.OrdinalIgnoreCase);

        Assert.True(procStart >= 0, $"{descriptor.MergeProc} is not declared in 003");

        var procEnd = sql.IndexOf("\nGO", procStart, StringComparison.Ordinal);
        var proc = procEnd < 0 ? sql[procStart..] : sql[procStart..procEnd];

        var partition = Regex.Match(proc, @"PARTITION\s+BY\s+(?<cols>.*?)\s+ORDER\s+BY",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        Assert.True(partition.Success, $"{descriptor.MergeProc} has no PARTITION BY");

        var declared = partition.Groups["cols"].Value
            .Split(',')
            .Select(s => Unbracket(s.Trim()))
            .Where(s => s.Length > 0)
            .ToList();

        Assert.Equal(descriptor.KeyColumns, declared);

        // Deterministic tiebreak: last row in the bulletin wins.
        Assert.Contains("ORDER BY RowOrdinal DESC", proc, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One work unit carries one bulletin — one exchange, one trade date — so a
    /// "NOT MATCHED BY SOURCE" branch would delete every other exchange and date
    /// in the table.
    /// </summary>
    [Fact]
    public void NoMergeDeletesByAbsence()
    {
        // Comments stripped first: the script's header explains WHY there is no
        // such branch, and that prose must not read as the branch itself.
        var code = StripComments(TestHelpers.SqlText("003_CreateCmeProcedures.sql"));

        Assert.DoesNotContain("NOT MATCHED BY SOURCE", code, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripComments(string sql) => Regex.Replace(sql, @"--[^\n]*", string.Empty);

    /// <summary>
    /// ModifiedAtUtc must be UTC everywhere. The supplied DDL said SYSDATETIME()
    /// (server LOCAL time, contradicting the column's own name); this loader
    /// deliberately uses SYSUTCDATETIME() and this test keeps the deviation
    /// consistent, so a later edit cannot reintroduce a mix of the two.
    /// </summary>
    [Fact]
    public void EveryTimestampIsUtc()
    {
        foreach (var file in new[] { "001_CreateCmeSchema.sql", "003_CreateCmeProcedures.sql" })
        {
            var sql = TestHelpers.SqlText(file);

            // Strip comments before looking, so the header's discussion of
            // SYSDATETIME() does not trip the assertion.
            var code = StripComments(sql);

            Assert.DoesNotContain("SYSDATETIME()", code, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SYSUTCDATETIME()", code, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The drop script must remove objects in dependency order.</summary>
    [Fact]
    public void DropScriptRemovesProceduresBeforeTypesBeforeTables()
    {
        var sql = TestHelpers.SqlText("999_DropCmeObjects.sql");

        var lastProc = new[] { "usp_ValidateLoad", "usp_BulkMergeStlbasicFuture", "usp_BulkMergeStlbasicOption", "usp_UpsertFileLog" }
            .Max(p => sql.IndexOf(p, StringComparison.OrdinalIgnoreCase));

        var firstType = sql.IndexOf("DROP TYPE", StringComparison.OrdinalIgnoreCase);
        var firstTable = sql.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase);

        Assert.True(lastProc < firstType, "procedures must be dropped before the table types they reference");
        Assert.True(firstType < firstTable, "table types must be dropped before the tables");
    }

    private static string TableBody(string sql, string qualifiedTable)
    {
        var name = qualifiedTable.Split('.')[^1];

        var m = Regex.Match(sql,
            $@"CREATE\s+TABLE\s+\[?arm\]?\.\[?{Regex.Escape(name)}\]?\s*\((?<body>.*?)\n    \);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        Assert.True(m.Success, $"CREATE TABLE for {qualifiedTable} not found in 001");
        return m.Groups["body"].Value;
    }
}
