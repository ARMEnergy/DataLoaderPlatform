using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.Argus.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable the sink builds
/// and the <c>CREATE TYPE</c> in <c>sql/Argus/002</c> disagree on column order or
/// type, SQL Server does not complain — it writes every row into the wrong
/// columns. The repo's <c>tvp-contract-check</c> skill exists because that failure
/// is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILE and comparing it to
/// <see cref="ArgusDescriptors"/>, which is also what the sink builds its
/// DataTable from. Editing one side without the other fails the build, so the
/// contract is enforced continuously rather than at review time.
/// </para>
/// </summary>
public sealed class ArgusTvpContractTests
{
    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);

    public static TheoryData<string> AllFeedIds()
    {
        var data = new TheoryData<string>();
        foreach (var feed in ArgusDescriptors.All) data.Add(feed.FeedId);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Tvp_matches_descriptor_by_name_order_and_type(string feedId)
    {
        var feed = ArgusDescriptors.Find(feedId)!;
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
    /// <c>ModifiedAtUtc</c> and <c>DateCreated</c> are DB-stamped defaults. A TVP
    /// that carried them would either overwrite the server's value or shift every
    /// following column.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Tvp_never_carries_db_stamped_columns(string feedId)
    {
        var feed = ArgusDescriptors.Find(feedId)!;

        Assert.DoesNotContain(feed.Columns, c =>
            c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals("DateCreated", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every type declared in 002 must belong to a feed — no orphans left behind by an edit.</summary>
    [Fact]
    public void Every_type_in_the_script_belongs_to_a_feed()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql);
        var expected = ArgusDescriptors.All.Select(f => f.TvpType).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(ArgusDescriptors.All.Count, declared.Count);
        foreach (var name in declared)
            Assert.True(expected.Contains(name), $"002 declares {name}, which no feed descriptor uses.");
    }

    /// <summary>
    /// The fact TVP carries one column the fact TABLES do not: SourceFileDate, the
    /// merge ordering guard. It must be last so the payload prefix stays aligned
    /// with the tables, and it must be NOT NULL or the guard degrades to a no-op.
    /// </summary>
    [Fact]
    public void TimeSeries_tvp_ends_with_the_ordering_guard()
    {
        var columns = ArgusDescriptors.TimeSeries.Columns;
        var last = columns[^1];

        Assert.Equal("SourceFileDate", last.Name);
        Assert.True(last.Required);
        Assert.Equal(ArgusDerived.SourceFileDate, last.Derived);
    }

    /// <summary>
    /// Feeds must not share a merge proc: <see cref="Core.Concurrency.SqlWriteGate"/>
    /// keys on the proc name, so a shared proc would serialize unrelated feeds, and
    /// a copy-paste error here would silently write one feed's rows into another's
    /// table.
    /// </summary>
    [Fact]
    public void Feed_ids_tvps_procs_and_tables_are_all_distinct()
    {
        Assert.Equal(ArgusDescriptors.All.Count, ArgusDescriptors.All.Select(f => f.FeedId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ArgusDescriptors.All.Count, ArgusDescriptors.All.Select(f => f.TvpType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ArgusDescriptors.All.Count, ArgusDescriptors.All.Select(f => f.MergeProc).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ArgusDescriptors.All.Count, ArgusDescriptors.All.Select(f => f.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Each feed's merge proc must actually be defined in 003.</summary>
    [Fact]
    public void Every_merge_proc_exists_in_the_procedures_script()
    {
        var procSql = File.ReadAllText(RepoPaths.ProceduresScript);

        foreach (var feed in ArgusDescriptors.All)
        {
            var pattern = $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(feed.MergeProc)}\b";
            Assert.True(
                Regex.IsMatch(procSql, pattern, RegexOptions.IgnoreCase),
                $"003 does not define {feed.MergeProc} (feed {feed.FeedId}).");
        }
    }

    /// <summary>
    /// Each merge proc must take its feed's own TVP type. A mismatch here is the
    /// exact copy-paste slip that would send one feed's rows through another's
    /// merge.
    /// </summary>
    [Fact]
    public void Every_merge_proc_takes_its_own_tvp_type()
    {
        var procSql = File.ReadAllText(RepoPaths.ProceduresScript);

        foreach (var feed in ArgusDescriptors.All)
        {
            var pattern =
                $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(feed.MergeProc)}\s*" +
                $@"@Records\s+{Regex.Escape(feed.TvpType)}\s+READONLY";

            Assert.True(
                Regex.IsMatch(procSql, pattern, RegexOptions.IgnoreCase),
                $"{feed.MergeProc} does not declare '@Records {feed.TvpType} READONLY' as its first parameter.");
        }
    }

    /// <summary>
    /// The one destructive proc must keep both guards. Losing either turns a
    /// truncated download into silent data loss on a 150k-row table.
    /// </summary>
    [Fact]
    public void Replace_proc_keeps_its_empty_and_short_source_guards()
    {
        var procSql = File.ReadAllText(RepoPaths.ProceduresScript);
        var body = ExtractProcedureBody(procSql, "dlp.usp_ReplaceQuoteLookup");

        Assert.Contains("@Incoming = 0", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@MinRowFraction", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Regex.Matches(body, @"RAISERROR", RegexOptions.IgnoreCase).Count);

        // Exactly one feed may be destructive.
        Assert.Single(ArgusDescriptors.All, f => f.WriteMode == ArgusWriteMode.Replace);
        Assert.Equal("Quotes", ArgusDescriptors.All.Single(f => f.WriteMode == ArgusWriteMode.Replace).FeedId);
    }

    /// <summary>
    /// The fact merge must keep its ordering guard on BOTH tables. Without it, a
    /// concurrent run could let an older file overwrite a newer one — the exact
    /// hazard created by DCRDEUS files overlapping (docs/apis/Argus.md 5.5).
    /// </summary>
    [Fact]
    public void Fact_merge_guards_both_tables_on_source_file_date()
    {
        var procSql = File.ReadAllText(RepoPaths.ProceduresScript);
        var body = ExtractProcedureBody(procSql, "dlp.usp_BulkMergeTimeSeriesDetail");

        var guards = Regex.Matches(
            body,
            @"WHEN\s+MATCHED\s+AND\s+src\.SourceFileDate\s*>=\s*ISNULL\s*\(\s*tgt\.RecordStatusDate",
            RegexOptions.IgnoreCase);

        Assert.Equal(2, guards.Count);

        // And the merge really does write both tables.
        Assert.Contains("MERGE dlp.TimeSeriesDetailHistory", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MERGE dlp.TimeSeriesDetail ", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// LATEST FILE DATE WINS, on both fact tables.
    ///
    /// <para>
    /// DCRDEUS files overlap — the same key really does arrive from several files in
    /// one run (docs/apis/Argus.md §5.5) — and work units run concurrently, so the
    /// resolution cannot depend on arrival order. Two things make "latest wins"
    /// hold, and both are pinned here because flipping either is a one-word edit
    /// that no other test would catch:
    /// </para>
    /// <list type="number">
    ///   <item>the batch de-dup orders <c>SourceFileDate DESC</c>, so within a merge
    ///         the newest row is the survivor;</item>
    ///   <item>the UPDATE guard is <c>&gt;=</c>, so an older file cannot overwrite a
    ///         newer one already stored.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_fact_merge_resolves_overlap_by_latest_file_date()
    {
        var body = ExtractProcedureBody(File.ReadAllText(RepoPaths.ProceduresScript), "dlp.usp_BulkMergeTimeSeriesDetail");

        // 1) De-dup keeps the newest, on both the history and the current CTE.
        Assert.Equal(2, Regex.Matches(body, @"ORDER BY\s+SourceFileDate\s+DESC", RegexOptions.IgnoreCase).Count);
        Assert.DoesNotContain("SourceFileDate ASC", body, StringComparison.OrdinalIgnoreCase);

        // 2) An older file can never overwrite a newer one.
        Assert.Equal(2, Regex.Matches(body, @"src\.SourceFileDate\s*>=\s*ISNULL", RegexOptions.IgnoreCase).Count);
        Assert.DoesNotContain("src.SourceFileDate <", body, StringComparison.OrdinalIgnoreCase);

        // A same-date republish must still apply, so the comparison is >= not >.
        Assert.DoesNotContain("src.SourceFileDate >  ", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every dated file is merged — nothing is filtered out of the merge by date.
    /// "Merge all the data" is a property of the work-unit provider (DaysBack = 0 by
    /// default), not of the proc, so the proc must contain no date predicate that
    /// could quietly drop older rows.
    /// </summary>
    [Fact]
    public void The_fact_merge_discards_nothing_by_date()
    {
        var body = ExtractProcedureBody(File.ReadAllText(RepoPaths.ProceduresScript), "dlp.usp_BulkMergeTimeSeriesDetail");

        Assert.DoesNotContain("WHERE src.[Date]", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DATEADD", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GETDATE", body, StringComparison.OrdinalIgnoreCase);

        // The shipped default enumerates every dated file the server offers.
        Assert.Equal(0, new ArgusSettings().DaysBack);
    }

    /// <summary>
    /// No merge may delete by absence. Each work unit carries one snapshot, so a
    /// short download would otherwise wipe rows it simply did not mention.
    /// </summary>
    [Fact]
    public void No_merge_deletes_by_absence()
    {
        // Comments must be stripped first — 003 *documents* this rule in prose, and
        // matching that text would make the test pass for the wrong reason.
        var procSql = StripComments(File.ReadAllText(RepoPaths.ProceduresScript));

        Assert.DoesNotContain("NOT MATCHED BY SOURCE", procSql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Proves the check above is not vacuous: the prose really is in the file.</summary>
    [Fact]
    public void The_delete_by_absence_rule_is_documented_in_the_script()
    {
        var raw = File.ReadAllText(RepoPaths.ProceduresScript);

        Assert.Contains("NOT MATCHED BY SOURCE", raw, StringComparison.OrdinalIgnoreCase);
    }

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
    /// The executable body of one procedure, comments removed — so an assertion
    /// can never be satisfied by prose that merely describes the rule.
    /// </summary>
    private static string ExtractProcedureBody(string sql, string procName)
    {
        var start = sql.IndexOf($"PROCEDURE {procName}", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"{procName} not found in 003.");

        // Body runs to the next CREATE OR ALTER, or to the end of the file.
        var next = sql.IndexOf("CREATE OR ALTER", start + 1, StringComparison.OrdinalIgnoreCase);
        var body = next < 0 ? sql[start..] : sql[start..next];

        return StripComments(body);
    }
}

/// <summary>
/// Minimal <c>CREATE TYPE ... AS TABLE</c> reader. Deliberately not a T-SQL
/// parser — it only needs to handle the shape 002 actually uses.
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

    /// <summary>Split on commas at depth 0, so DECIMAL(28,17) stays intact.</summary>
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
        // Strip comment lines so a "-- note" inside the body cannot be read as a column.
        var text = string.Join(
            " ",
            raw.Split('\n')
               .Select(line => { var c = line.IndexOf("--", StringComparison.Ordinal); return c < 0 ? line : line[..c]; })
               .Select(line => line.Trim())
               .Where(line => line.Length > 0));

        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Name — bracketed or bare.
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

        // Normalize "DECIMAL(28, 17)" -> "DECIMAL(28,17)" so the comparison is on
        // substance rather than on whitespace.
        var sqlType = Regex.Replace(remainder.Trim(), @"\s*,\s*", ",");
        sqlType = Regex.Replace(sqlType, @"\s*\(\s*", "(");
        sqlType = Regex.Replace(sqlType, @"\s*\)\s*", ")");

        return new Column(name, sqlType, notNull);
    }
}

/// <summary>
/// Locates the repo's <c>sql/Argus</c> scripts from the test bin directory by
/// walking up to the folder holding the solution file, so the tests work from any
/// runner's working directory.
/// </summary>
internal static class RepoPaths
{
    public static string SqlDirectory { get; } = FindSqlDirectory();

    public static string SchemaScript => Path.Combine(SqlDirectory, "001_CreateArgusSchema.sql");
    public static string TvpScript => Path.Combine(SqlDirectory, "002_CreateArgusTvpTypes.sql");
    public static string ProceduresScript => Path.Combine(SqlDirectory, "003_CreateArgusProcedures.sql");

    private static string FindSqlDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sql", "Argus");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sql/Argus by walking up from '{AppContext.BaseDirectory}'.");
    }
}
