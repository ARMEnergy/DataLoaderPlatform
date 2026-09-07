using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.EOX.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable the sink builds and
/// the <c>CREATE TYPE</c> in <c>sql/EOX/002</c> disagree on column order or type,
/// SQL Server does not complain — it writes every row into the wrong columns. The
/// repo's <c>tvp-contract-check</c> skill exists because that failure is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILES and comparing them to
/// <see cref="EoxDescriptors"/>, which is also what the sink builds its DataTable
/// from. Editing one side without the other fails the build, so the contract is
/// enforced continuously rather than at review time.
/// </para>
/// </summary>
public sealed class EoxTvpContractTests
{
    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);

    public static TheoryData<string> AllFeedIds()
    {
        var data = new TheoryData<string>();
        foreach (var feed in EoxDescriptors.All) data.Add(feed.FeedId);
        return data;
    }

    // ---------------------------------------------------------------- 002

    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Tvp_matches_descriptor_by_name_order_type_and_nullability(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var sqlColumns = TvpParser.Parse(TvpSql, feed.TvpType);

        Assert.True(sqlColumns.Count > 0, $"No CREATE TYPE found for {feed.TvpType} in 002.");
        Assert.Equal(feed.Columns.Count, sqlColumns.Count);

        for (var i = 0; i < feed.Columns.Count; i++)
        {
            var expected = feed.Columns[i];
            var actual = sqlColumns[i];

            Assert.Equal(expected.Name, actual.Name);

            Assert.True(
                string.Equals(expected.SqlType, actual.SqlType, StringComparison.OrdinalIgnoreCase),
                $"{feed.TvpType} column {i} '{expected.Name}': descriptor says {expected.SqlType}, 002 says {actual.SqlType}.");

            Assert.True(
                expected.Required == actual.NotNull,
                $"{feed.TvpType} column {i} '{expected.Name}': descriptor Required={expected.Required}, " +
                $"002 NOT NULL={actual.NotNull}.");
        }
    }

    /// <summary>
    /// <c>ModifiedAtUtc</c> is DB-stamped. A TVP that carried it would either
    /// overwrite the server's value or shift every following column.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Tvp_never_carries_db_stamped_columns(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;

        Assert.DoesNotContain(feed.Columns, c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("DateCreated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_type_in_the_script_belongs_to_a_feed()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql);
        var expected = EoxDescriptors.All.Select(f => f.TvpType).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(EoxDescriptors.All.Count, declared.Count);
        foreach (var name in declared)
            Assert.True(expected.Contains(name), $"002 declares {name}, which no feed descriptor uses.");
    }

    /// <summary>
    /// FileName is the merge ordering guard. It must be last (so the payload prefix
    /// stays aligned with the table) and NOT NULL (a NULL makes
    /// <c>src.FileName &gt;= tgt.FileName</c> indeterminate).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Every_tvp_ends_with_a_not_null_FileName_guard(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var last = TvpParser.Parse(TvpSql, feed.TvpType)[^1];

        Assert.Equal("FileName", last.Name);
        Assert.True(last.NotNull, $"{feed.TvpType}.FileName must be NOT NULL — the merge guard cannot order a NULL.");
        Assert.Equal(EoxDerived.FileName, feed.Columns[^1].Derived);
    }

    /// <summary>
    /// Feeds must not share a merge proc: <see cref="Core.Concurrency.SqlWriteGate"/>
    /// keys on the proc name, so a shared proc would serialize unrelated feeds — and
    /// a copy-paste error here would silently write one feed's rows into another's
    /// table.
    /// </summary>
    [Fact]
    public void Feed_ids_tvps_procs_and_tables_are_all_distinct()
    {
        var n = EoxDescriptors.All.Count;

        Assert.Equal(n, EoxDescriptors.All.Select(f => f.FeedId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(n, EoxDescriptors.All.Select(f => f.TvpType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(n, EoxDescriptors.All.Select(f => f.MergeProc).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(n, EoxDescriptors.All.Select(f => f.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(n, EoxDescriptors.All.Select(f => f.FilePrefix).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---------------------------------------------------------------- 003

    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Every_merge_proc_exists_and_takes_its_own_tvp_type(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;

        var pattern =
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(feed.MergeProc)}\s*" +
            $@"@Records\s+{Regex.Escape(feed.TvpType)}\s+READONLY";

        Assert.True(
            Regex.IsMatch(ProcSql, pattern, RegexOptions.IgnoreCase),
            $"003 does not define {feed.MergeProc} with '@Records {feed.TvpType} READONLY' as its first parameter.");
    }

    /// <summary>
    /// LATEST FILE WINS, on every table. Two things make that hold, and both are a
    /// one-character edit away from being false:
    ///
    /// <list type="number">
    ///   <item>the batch de-dup orders <c>FileName DESC</c>, so within a merge the
    ///         newest file's row is the survivor;</item>
    ///   <item>the UPDATE guard is <c>&gt;=</c>, so an older file cannot overwrite a
    ///         newer one already stored, and a same-name republish still applies.</item>
    /// </list>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Every_merge_resolves_overlap_by_latest_file_name(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var body = ExtractProcedureBody(ProcSql, feed.MergeProc);

        Assert.Matches(new Regex(@"ORDER BY\s+FileName\s+DESC", RegexOptions.IgnoreCase), body);
        Assert.DoesNotContain("FileName ASC", body, StringComparison.OrdinalIgnoreCase);

        Assert.Matches(
            new Regex(@"WHEN\s+MATCHED\s+AND\s+src\.FileName\s*>=\s*ISNULL\s*\(\s*tgt\.FileName", RegexOptions.IgnoreCase),
            body);

        Assert.DoesNotContain("src.FileName <", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The de-dup must partition by exactly the target table's primary key.</summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void The_batch_dedup_partitions_by_the_primary_key(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var body = ExtractProcedureBody(ProcSql, feed.MergeProc);

        var expected = $@"PARTITION BY\s+{string.Join(@",\s*", feed.KeyColumns.Select(Regex.Escape))}";
        Assert.Matches(new Regex(expected, RegexOptions.IgnoreCase), body);

        // …and the MERGE joins on the same three columns.
        foreach (var key in feed.KeyColumns)
            Assert.Matches(new Regex($@"tgt\.{Regex.Escape(key)}\s*=\s*src\.{Regex.Escape(key)}", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// No merge may delete by absence. Each work unit carries ONE day's snapshot for
    /// ONE feed, so <c>NOT MATCHED BY SOURCE</c> would wipe every other day.
    /// </summary>
    [Fact]
    public void No_merge_deletes_by_absence()
    {
        // Comments must be stripped first — 003 *documents* this rule in prose, and
        // matching that text would make the test pass for the wrong reason.
        Assert.DoesNotContain("NOT MATCHED BY SOURCE", StripComments(ProcSql), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Proves the check above is not vacuous: the prose really is in the file.</summary>
    [Fact]
    public void The_delete_by_absence_rule_is_documented_in_the_script()
    {
        Assert.Contains("NOT MATCHED BY SOURCE", ProcSql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every merge column chain must line up: the TVP's payload columns, the CTE's
    /// SELECT list, the INSERT column list and the VALUES list all carry the same
    /// names in the same order.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void The_insert_column_list_matches_the_tvp_columns_in_order(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var body = ExtractProcedureBody(ProcSql, feed.MergeProc);

        var insert = Regex.Match(body, @"INSERT\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
        Assert.True(insert.Success, $"{feed.MergeProc} has no INSERT column list.");

        var columns = insert.Groups[1].Value
            .Split(',')
            .Select(c => c.Trim().Trim('[', ']'))
            .Where(c => c.Length > 0)
            .ToList();

        // The INSERT adds exactly one column the TVP does not carry: ModifiedAtUtc,
        // which the proc stamps rather than the loader supplying.
        Assert.Equal("ModifiedAtUtc", columns[^1]);
        Assert.Equal(feed.Columns.Select(c => c.Name), columns[..^1]);
    }

    /// <summary>
    /// ModifiedAtUtc is stamped in UTC everywhere — the DEFAULT in 001 and both
    /// merge branches in 003. The requester's DDL used SYSDATETIME() (local time),
    /// which contradicts the column's own name and would make inserted rows
    /// disagree with updated ones. The deviation is deliberate and documented; this
    /// pins it so a later edit cannot reintroduce the mismatch on one side only.
    /// </summary>
    [Fact]
    public void ModifiedAtUtc_is_stamped_in_utc_consistently_in_001_and_003()
    {
        Assert.DoesNotContain("SYSDATETIME()", StripComments(SchemaSql), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYSDATETIME()", StripComments(ProcSql), StringComparison.OrdinalIgnoreCase);

        foreach (var feed in EoxDescriptors.All)
        {
            var body = ExtractProcedureBody(ProcSql, feed.MergeProc);
            Assert.Equal(2, Regex.Matches(body, @"SYSUTCDATETIME\(\)", RegexOptions.IgnoreCase).Count);
        }
    }

    // ---------------------------------------------------------------- 001

    /// <summary>
    /// The fact tables must carry exactly the requester's columns plus nothing the
    /// loader invented, and their primary keys must be the ones the descriptors and
    /// merge procs assume.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void The_table_primary_key_in_001_is_the_descriptor_key(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var table = feed.TargetTable.Split('.')[^1];

        var pk = Regex.Match(
            SchemaSql,
            $@"CREATE TABLE arm\.{Regex.Escape(table)}\b.*?PRIMARY KEY CLUSTERED\s*\(([^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(pk.Success, $"001 has no clustered PK for arm.{table}.");

        var columns = pk.Groups[1].Value
            .Split(',')
            .Select(c => c.Trim().Replace(" ASC", "", StringComparison.OrdinalIgnoreCase).Trim())
            .Where(c => c.Length > 0)
            .ToList();

        Assert.Equal(feed.KeyColumns, columns);
    }

    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Every_target_table_exists_in_001(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;

        Assert.Matches(
            new Regex($@"CREATE TABLE {Regex.Escape(feed.TargetTable)}\b", RegexOptions.IgnoreCase),
            SchemaSql);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Removes <c>--</c> line comments so assertions see executable T-SQL only.</summary>
    private static string StripComments(string sql) =>
        string.Join(
            "\n",
            sql.Split('\n').Select(line =>
            {
                var marker = line.IndexOf("--", StringComparison.Ordinal);
                return marker < 0 ? line : line[..marker];
            }));

    /// <summary>
    /// The executable body of one procedure, comments removed — so an assertion can
    /// never be satisfied by prose that merely describes the rule.
    /// </summary>
    private static string ExtractProcedureBody(string sql, string procName)
    {
        var start = sql.IndexOf($"PROCEDURE {procName}", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"{procName} not found in 003.");

        var next = sql.IndexOf("CREATE OR ALTER", start + 1, StringComparison.OrdinalIgnoreCase);
        var body = next < 0 ? sql[start..] : sql[start..next];

        return StripComments(body);
    }
}

/// <summary>
/// Minimal <c>CREATE TYPE ... AS TABLE</c> reader. Deliberately not a T-SQL parser —
/// it only needs to handle the shape 002 actually uses.
/// </summary>
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
        var body = ReadBalanced(sql, open);

        return SplitTopLevel(body).Select(ParseColumn).ToList();
    }

    public static IReadOnlyList<string> DeclaredTypeNames(string sql) =>
        Regex.Matches(sql, @"CREATE\s+TYPE\s+([A-Za-z0-9_\.\[\]]+)\s+AS\s+TABLE", RegexOptions.IgnoreCase)
             .Select(m => m.Groups[1].Value.Replace("[", "").Replace("]", ""))
             .ToList();

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

        throw new InvalidOperationException("Unbalanced parentheses in CREATE TYPE body.");
    }

    /// <summary>Split on commas at depth 0, so VARCHAR(128) stays intact.</summary>
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

    private static Column ParseColumn(string raw)
    {
        var text = string.Join(
            " ",
            raw.Split('\n')
               .Select(line => { var c = line.IndexOf("--", StringComparison.Ordinal); return c < 0 ? line : line[..c]; })
               .Select(line => line.Trim())
               .Where(line => line.Length > 0));

        text = Regex.Replace(text, @"\s+", " ").Trim();

        string name;
        int rest;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            name = text[1..close];
            rest = close + 1;
        }
        else
        {
            var space = text.IndexOf(' ');
            name = text[..space];
            rest = space;
        }

        var remainder = text[rest..].Trim();

        bool notNull;
        if (remainder.EndsWith("NOT NULL", StringComparison.OrdinalIgnoreCase))
        {
            notNull = true;
            remainder = remainder[..^"NOT NULL".Length];
        }
        else if (remainder.EndsWith("NULL", StringComparison.OrdinalIgnoreCase))
        {
            notNull = false;
            remainder = remainder[..^"NULL".Length];
        }
        else
        {
            // No explicit nullability — SQL Server's default is NULL.
            notNull = false;
        }

        var sqlType = Regex.Replace(remainder.Trim(), @"\s*,\s*", ",");
        sqlType = Regex.Replace(sqlType, @"\s*\(\s*", "(");
        sqlType = Regex.Replace(sqlType, @"\s*\)\s*", ")");

        return new Column(name, sqlType, notNull);
    }
}

/// <summary>
/// Locates the repo's <c>sql/EOX</c> scripts from the test bin directory by walking
/// up to the folder holding them, so the tests work from any runner's working
/// directory.
/// </summary>
internal static class RepoPaths
{
    public static string SqlDirectory { get; } = FindSqlDirectory();

    public static string SchemaScript => Path.Combine(SqlDirectory, "001_CreateEoxSchema.sql");
    public static string TvpScript => Path.Combine(SqlDirectory, "002_CreateEoxTvpTypes.sql");
    public static string ProceduresScript => Path.Combine(SqlDirectory, "003_CreateEoxProcedures.sql");

    private static string FindSqlDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sql", "EOX");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sql/EOX by walking up from '{AppContext.BaseDirectory}'.");
    }
}
