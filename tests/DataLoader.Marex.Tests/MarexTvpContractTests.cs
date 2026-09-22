using System.Text.RegularExpressions;
using DataLoader.Marex;
using Xunit;

namespace DataLoader.Marex.Tests;

/// <summary>
/// THE contract test for this loader.
///
/// <para>A TVP binds BY POSITION, so <c>sql/Marex/002</c> and
/// <see cref="MarexDescriptors"/> must agree on every column's NAME, ORDER and TYPE. If
/// they drift, nothing raises: SqlClient sends the DataTable's columns positionally, the
/// server accepts them against whatever the type declares, and every row loads into the
/// wrong columns as long as the types happen to be compatible. On
/// <c>arm.MarketStatisticTvp</c> that is not a remote possibility — it has ten
/// consecutive <c>DECIMAL(18,8)</c> columns.</para>
///
/// <para>So this parses the REAL .sql file rather than a copy, which means editing
/// either side alone fails the build.</para>
/// </summary>
public class MarexTvpContractTests
{
    private static readonly Dictionary<string, MarexTableDescriptor> Expected = new()
    {
        ["arm.ClosingPriceTvp"] = MarexDescriptors.ClosingPrice,
        ["arm.MarketStatisticTvp"] = MarexDescriptors.MarketStatistic,
        ["arm.PeriodTvp"] = MarexDescriptors.Period,
        ["arm.PeriodGroupTvp"] = MarexDescriptors.PeriodGroup,
        ["arm.ProductTvp"] = MarexDescriptors.Product
    };

    [Fact]
    public void EveryDescriptor_HasAMatchingCreateTypeInTheSqlFile()
    {
        var parsed = ParseCreateTypes(RepoPaths.ReadSql("002_CreateMarexTvpTypes.sql"));

        Assert.Equal(
            Expected.Keys.OrderBy(x => x).ToArray(),
            parsed.Keys.OrderBy(x => x).ToArray());
    }

    [Theory]
    [InlineData("arm.ClosingPriceTvp")]
    [InlineData("arm.MarketStatisticTvp")]
    [InlineData("arm.PeriodTvp")]
    [InlineData("arm.PeriodGroupTvp")]
    [InlineData("arm.ProductTvp")]
    public void TvpColumns_MatchTheDescriptor_ByNameOrderAndType(string typeName)
    {
        var parsed = ParseCreateTypes(RepoPaths.ReadSql("002_CreateMarexTvpTypes.sql"))[typeName];
        var descriptor = Expected[typeName];

        // Compared as a single ordered projection, so a mismatch reports the whole
        // sequence rather than just the first differing element.
        var fromSql = parsed.Select(c => $"{c.Name} {c.SqlType}").ToArray();
        var fromCode = descriptor.Columns.Select(c => $"{c.Name} {c.SqlType}").ToArray();

        Assert.Equal(fromCode, fromSql);
    }

    [Fact]
    public void NoTvp_DeclaresModifiedAtUtc()
    {
        // It is stamped by the merge proc with SYSUTCDATETIME(). A TVP column of that
        // name would mean the loader's clock silently won instead.
        foreach (var (typeName, columns) in ParseCreateTypes(RepoPaths.ReadSql("002_CreateMarexTvpTypes.sql")))
            Assert.DoesNotContain(columns, c =>
                c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryPrimaryKeyColumn_IsDeclaredNotNullInTheTvp()
    {
        var parsed = ParseCreateTypes(RepoPaths.ReadSql("002_CreateMarexTvpTypes.sql"));

        foreach (var (typeName, descriptor) in Expected)
        {
            var columns = parsed[typeName];
            foreach (var key in descriptor.Columns.Where(c => c.IsKey))
            {
                var sqlColumn = columns.Single(c =>
                    c.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase));

                Assert.True(sqlColumn.NotNull,
                    $"{typeName}.{key.Name} is a primary key component and must be NOT NULL in the TVP; " +
                    "a NULL key silently merges every such row onto one target row.");
            }
        }
    }

    [Fact]
    public void TableDdl_DeclaresTheSameColumnsAsTheTvp_PlusModifiedAtUtc()
    {
        // 001 and 002 are separate files and can drift from each other just as easily as
        // 002 can drift from the descriptors.
        var ddl = RepoPaths.ReadSql("001_CreateMarexSchema.sql");

        foreach (var descriptor in MarexDescriptors.All)
        {
            var body = ExtractCreateTableBody(ddl, descriptor.TableName);
            var columns = ParseColumns(body);

            var expected = descriptor.Columns.Select(c => c.Name).Append("ModifiedAtUtc").ToArray();
            var actual = columns.Select(c => c.Name).ToArray();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void EveryMergeProcAndTvpNamedByADescriptor_ExistsInTheSql()
    {
        var procs = RepoPaths.ReadSql("003_CreateMarexProcedures.sql");
        var types = RepoPaths.ReadSql("002_CreateMarexTvpTypes.sql");

        foreach (var descriptor in MarexDescriptors.All)
        {
            Assert.Contains($"CREATE OR ALTER PROCEDURE {descriptor.MergeProc}", procs,
                StringComparison.OrdinalIgnoreCase);

            // The proc must take the descriptor's own TVP type, not a neighbour's.
            var signature = new Regex(
                $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(descriptor.MergeProc)}\s+@Records\s+{Regex.Escape(descriptor.TvpType)}\s+READONLY",
                RegexOptions.IgnoreCase);
            Assert.True(signature.IsMatch(procs),
                $"{descriptor.MergeProc} does not declare @Records as {descriptor.TvpType} READONLY");

            Assert.Contains($"CREATE TYPE {descriptor.TvpType}", types, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryDropScriptEntry_CoversEveryObjectTheDescriptorsName()
    {
        var drop = RepoPaths.ReadSql("999_DropMarexObjects.sql");

        foreach (var descriptor in MarexDescriptors.All)
        {
            Assert.Contains($"DROP PROCEDURE IF EXISTS {descriptor.MergeProc};", drop, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"DROP TYPE IF EXISTS {descriptor.TvpType};", drop, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"DROP TABLE IF EXISTS {descriptor.TableName};", drop, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ parsing

    private sealed record SqlColumn(string Name, string SqlType, bool NotNull);

    private static Dictionary<string, List<SqlColumn>> ParseCreateTypes(string sql)
    {
        var result = new Dictionary<string, List<SqlColumn>>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(
            sql,
            @"CREATE\s+TYPE\s+(?<name>[\w\.\[\]]+)\s+AS\s+TABLE\s*\((?<body>.*?)\n\s*\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in matches)
            result[Unbracket(m.Groups["name"].Value)] = ParseColumns(m.Groups["body"].Value);

        return result;
    }

    private static string ExtractCreateTableBody(string sql, string tableName)
    {
        var m = Regex.Match(
            sql,
            $@"CREATE\s+TABLE\s+{Regex.Escape(tableName)}\s*\((?<body>.*?)\n\s*\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        Assert.True(m.Success, $"No CREATE TABLE found for {tableName} in 001_CreateMarexSchema.sql");
        return m.Groups["body"].Value;
    }

    /// <summary>
    /// Reads "Name TYPE[(a,b)] [NULL|NOT NULL]" lines, skipping CONSTRAINT lines and the
    /// continuation lines of an inline named DEFAULT.
    /// </summary>
    private static List<SqlColumn> ParseColumns(string body)
    {
        var columns = new List<SqlColumn>();

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',');
            if (line.Length == 0) continue;
            if (line.StartsWith("--", StringComparison.Ordinal)) continue;
            if (line.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase)) continue;

            var m = Regex.Match(
                line,
                @"^(?<name>\[?\w+\]?)\s+(?<type>\w+(\s*\(\s*\d+\s*(,\s*\d+\s*)?\))?)(?<rest>.*)$");
            if (!m.Success) continue;

            var type = Regex.Replace(m.Groups["type"].Value, @"\s+", "").ToUpperInvariant();
            // Normalise DECIMAL(18,8) vs DECIMAL(18, 8) to the descriptor's spelling.
            type = Regex.Replace(type, @"^(\w+)\((\d+),(\d+)\)$", "$1($2,$3)");

            columns.Add(new SqlColumn(
                Unbracket(m.Groups["name"].Value),
                type,
                m.Groups["rest"].Value.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)));
        }

        return columns;
    }

    private static string Unbracket(string value) => value.Replace("[", "").Replace("]", "");
}
