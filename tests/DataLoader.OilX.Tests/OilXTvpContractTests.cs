using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// The TVP contract gate.
///
/// <para>
/// A table-valued parameter binds BY POSITION. If the DataTable the sink builds and the
/// <c>CREATE TYPE</c> in <c>sql/OilX/002</c> disagree on column order or type, SQL
/// Server does not complain — it writes every row into the wrong columns. The repo's
/// <c>tvp-contract-check</c> skill exists because that failure is silent.
/// </para>
/// <para>
/// These tests close the loop by PARSING THE REAL .sql FILES and comparing them to
/// <see cref="OilXDescriptors"/>, which is also what the sink builds its DataTable from
/// AND what the reader fills positionally. Editing one side without the other fails the
/// build, so the contract is enforced continuously rather than at review time.
/// </para>
/// </summary>
public sealed class OilXTvpContractTests
{
    private static readonly string TvpSql = File.ReadAllText(RepoPaths.TvpScript);
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string DropSql = File.ReadAllText(RepoPaths.DropScript);

    public static TheoryData<string> AllFeeds()
    {
        var data = new TheoryData<string>();
        foreach (var feed in OilXDescriptors.All) data.Add(feed.FeedId);
        return data;
    }

    private static OilXFeedDescriptor Feed(string feedId) => TestHelpers.Feed(feedId);

    /// <summary>Exposes the protected BuildTable without opening a connection.</summary>
    private sealed class ProbeSink : OilXTableSink
    {
        public ProbeSink(OilXFeedDescriptor feed)
            : base(feed, "Server=(local);Database=OilX;Integrated Security=SSPI;", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<OilXRow> rows) => BuildTable(rows);
    }

    // --------------------------------------------------- descriptor vs the .sql

    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Tvp_matches_descriptor_by_name_order_type_and_nullability(string feedId)
    {
        var feed = Feed(feedId);
        var sqlColumns = TvpParser.Parse(TvpSql, feed.TvpType);

        Assert.True(sqlColumns.Count > 0, $"No CREATE TYPE found for {feed.TvpType} in 002.");
        Assert.Equal(feed.Columns.Count, sqlColumns.Count);

        for (var i = 0; i < feed.Columns.Count; i++)
        {
            var expected = feed.Columns[i];
            var actual = sqlColumns[i];

            Assert.True(
                string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase),
                $"{feed.TvpType} column {i}: descriptor says '{expected.Name}', 002 says '{actual.Name}'.");

            Assert.True(
                Normalize(expected.SqlType) == Normalize(actual.SqlType),
                $"{feed.TvpType} column {i} '{expected.Name}': descriptor says {expected.SqlType}, " +
                $"002 says {actual.SqlType}.");

            Assert.True(
                expected.Required == actual.NotNull,
                $"{feed.TvpType} column {i} '{expected.Name}': descriptor Required={expected.Required}, " +
                $"002 NOT NULL={actual.NotNull}.");
        }
    }

    /// <summary>
    /// The TVP must be the target table's columns minus <c>ModifiedAtUtc</c>, IN THE SAME
    /// ORDER. A table whose order drifted from its type would load every row shifted.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Tvp_is_the_table_minus_ModifiedAtUtc_in_order(string feedId)
    {
        var feed = Feed(feedId);

        var tableColumns = TvpParser.ParseTable(SchemaSql, feed.TargetTable)
            .Where(c => !c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tvpColumns = TvpParser.Parse(TvpSql, feed.TvpType);

        Assert.Equal(
            tableColumns.Select(c => c.Name.ToUpperInvariant()).ToList(),
            tvpColumns.Select(c => c.Name.ToUpperInvariant()).ToList());

        for (var i = 0; i < tableColumns.Count; i++)
            Assert.True(
                Normalize(tableColumns[i].SqlType) == Normalize(tvpColumns[i].SqlType),
                $"{feed.TargetTable} column '{tableColumns[i].Name}': 001 says {tableColumns[i].SqlType}, " +
                $"002 says {tvpColumns[i].SqlType}.");
    }

    /// <summary>
    /// <c>ModifiedAtUtc</c> is a DB-stamped default. A TVP that carried it would either
    /// overwrite the server's value or shift every following column.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Tvp_never_carries_the_db_stamped_column(string feedId)
    {
        var feed = Feed(feedId);

        Assert.DoesNotContain(feed.Columns,
            c => c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(TvpParser.Parse(TvpSql, feed.TvpType),
            c => c.Name.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The DataTable the sink actually sends must match the descriptor one-for-one.</summary>
    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Sink_DataTable_matches_the_descriptor(string feedId)
    {
        var feed = Feed(feedId);
        var table = new ProbeSink(feed).Build(Array.Empty<OilXRow>());

        Assert.Equal(feed.Columns.Count, table.Columns.Count);

        for (var i = 0; i < feed.Columns.Count; i++)
        {
            Assert.Equal(feed.Columns[i].Name, table.Columns[i].ColumnName);
            Assert.Equal(feed.Columns[i].ClrType, table.Columns[i].DataType);
        }
    }

    [Fact]
    public void Sink_rejects_a_row_whose_width_disagrees_with_the_descriptor()
    {
        var feed = Feed(OilXDescriptors.GlobalBalance);
        var sink = new ProbeSink(feed);

        var ex = Assert.Throws<InvalidOperationException>(
            () => sink.Build(new[] { new OilXRow(new object[] { 1, 2 }) }));

        Assert.Contains("declares", ex.Message);
    }

    // ------------------------------------------------------- the rest of the SQL

    /// <summary>Every declared type belongs to a feed, and every feed's type is declared.</summary>
    [Fact]
    public void Declared_types_and_descriptors_agree()
    {
        var declared = TvpParser.DeclaredTypeNames(TvpSql)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var expected = OilXDescriptors.All.Select(f => f.TvpType)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(expected, declared);
    }

    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Schema_declares_the_target_table_with_the_RunDate_RowId_primary_key(string feedId)
    {
        var feed = Feed(feedId);
        var bare = feed.TargetTable.Replace("arm.", string.Empty);

        Assert.Matches(
            new Regex($@"CREATE\s+TABLE\s+\[?arm\]?\s*\.\s*\[?{Regex.Escape(bare)}\]?", RegexOptions.IgnoreCase),
            SchemaSql);

        Assert.Matches(
            new Regex(
                $@"CONSTRAINT\s+\[?PK_ARM_{Regex.Escape(bare)}\]?\s+PRIMARY\s+KEY\s+CLUSTERED\s*\(\s*\[?RunDate\]?\s*,\s*\[?RowId\]?\s*\)",
                RegexOptions.IgnoreCase),
            SchemaSql);
    }

    /// <summary>
    /// Each merge proc must take its feed's TVP, partition the de-dup on the PRIMARY KEY,
    /// and guard the UPDATE on Checksum. Those three are what make a re-merge idempotent
    /// and stop ModifiedAtUtc degrading into "time of last run".
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Merge_proc_takes_the_tvp_dedups_on_the_key_and_guards_on_checksum(string feedId)
    {
        var feed = Feed(feedId);
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, feed.MergeProc));

        Assert.Matches(
            new Regex($@"@Records\s+{Regex.Escape(feed.TvpType)}\s+READONLY", RegexOptions.IgnoreCase),
            body);

        Assert.Matches(
            new Regex(@"PARTITION\s+BY\s+\[?RunDate\]?\s*,\s*\[?RowId\]?", RegexOptions.IgnoreCase),
            body);

        Assert.Matches(
            new Regex(@"WHEN\s+MATCHED\s+AND\s*\(\s*tgt\.\[?Checksum\]?\s+IS\s+NULL\s+OR\s+tgt\.\[?Checksum\]?\s*<>\s*s\.\[?Checksum\]?\s*\)",
                RegexOptions.IgnoreCase),
            body);

        Assert.Matches(new Regex(@"SELECT\s+@@ROWCOUNT\s+AS\s+RecordsProcessed", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// ⚠ No merge may delete by absence. A batch carries 20,000 rows of ONE day; a
    /// <c>WHEN NOT MATCHED BY SOURCE THEN DELETE</c> would wipe the rest of that day and
    /// every other day in the table.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Merge_proc_never_deletes_by_absence(string feedId)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, Feed(feedId).MergeProc));

        Assert.DoesNotMatch(new Regex(@"NOT\s+MATCHED\s+BY\s+SOURCE", RegexOptions.IgnoreCase), body);
        Assert.DoesNotMatch(new Regex(@"\bDELETE\b", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// The merge must set and insert EVERY non-key, non-stamped column. A column added to
    /// the table but forgotten in the UPDATE list would silently never be revised.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeeds))]
    public void Merge_proc_writes_every_payload_column(string feedId)
    {
        var feed = Feed(feedId);
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, feed.MergeProc));

        foreach (var column in feed.Columns)
        {
            if (column.Name is "RunDate" or "RowId") continue;   // the merge key

            Assert.True(
                Regex.IsMatch(body, $@"\[{Regex.Escape(column.Name)}\]\s*=\s*s\.\[{Regex.Escape(column.Name)}\]",
                    RegexOptions.IgnoreCase),
                $"{feed.MergeProc} does not UPDATE [{column.Name}].");
        }

        foreach (var column in feed.Columns)
            Assert.True(
                Regex.IsMatch(body, $@"s\.\[{Regex.Escape(column.Name)}\]", RegexOptions.IgnoreCase),
                $"{feed.MergeProc} does not INSERT s.[{column.Name}].");
    }

    /// <summary>999 must drop everything 001–003 create, or a rebuild leaves orphans.</summary>
    [Fact]
    public void Drop_script_covers_every_object()
    {
        foreach (var feed in OilXDescriptors.All)
        {
            Assert.Contains(feed.TvpType, DropSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(feed.MergeProc, DropSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(feed.TargetTable, DropSql, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("arm.usp_ValidateLoad", DropSql, StringComparison.OrdinalIgnoreCase);

        // Procedures before types before tables: a type cannot be dropped while a proc
        // references it.
        var firstProc = DropSql.IndexOf("DROP PROCEDURE", StringComparison.OrdinalIgnoreCase);
        var firstType = DropSql.IndexOf("DROP TYPE", StringComparison.OrdinalIgnoreCase);
        var firstTable = DropSql.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase);

        Assert.True(firstProc >= 0 && firstProc < firstType, "Procedures must be dropped before types.");
        Assert.True(firstType < firstTable, "Types must be dropped before tables.");
    }

    /// <summary>
    /// Types compare ignoring case and inner spacing, so <c>DECIMAL(18,8)</c> and
    /// <c>decimal(18, 8)</c> agree while <c>DECIMAL(28,8)</c> still differs.
    /// </summary>
    private static string Normalize(string sqlType) =>
        Regex.Replace(sqlType, @"\s+", string.Empty).ToUpperInvariant();
}
