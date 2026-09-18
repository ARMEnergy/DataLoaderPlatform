using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// Descriptor and merge-semantics checks that the generic TVP gate cannot express.
/// </summary>
public sealed class GenscapeDescriptorTests
{
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);

    /// <summary>
    /// ⚠ The descriptor column ORDER is pinned by name, because the parsers in
    /// <see cref="GenscapeDescriptors"/> fill <see cref="GenscapeRow.Values"/> by INDEX
    /// (<c>values[3]</c> is Region, <c>values[4]</c> is Product / Type…).
    ///
    /// <para>
    /// Reordering the list without reordering the parser would put every value in the
    /// wrong column — and because the TVP, the DataTable and the .sql all follow the
    /// descriptor, they would all agree with each other and none of the other contract
    /// tests would notice. This is the one that would.
    /// </para>
    /// </summary>
    [Fact]
    public void The_descriptor_column_order_is_pinned_because_the_parsers_index_positionally()
    {
        Assert.Equal(
            new[]
            {
                "ReportDate", "Year", "Week", "Region",
                "Product", "StorageFieldType", "StorageAmount", "CapacityUtilization"
            },
            GenscapeDescriptors.CrudeStorageWeeklyTable.Columns.Select(c => c.Name));

        Assert.Equal(
            new[] { "ReportDate", "Year", "Week", "Region", "Type", "FlowBPD" },
            GenscapeDescriptors.CrudeTransportationWeeklyTable.Columns.Select(c => c.Name));
    }

    /// <summary>
    /// Every key column must be Required, and nothing else may be — a NULL key would
    /// merge the row under a blank key, and a Required measure would drop rows the
    /// vendor legitimately leaves empty.
    /// </summary>
    [Theory]
    [InlineData("arm.OilFundamentals_CrudeStorage_Weekly",
                new[] { "ReportDate", "Year", "Week", "Region", "Product", "StorageFieldType" })]
    [InlineData("arm.OilFundamentals_CrudeTransportation_Weekly",
                new[] { "ReportDate", "Year", "Week", "Region", "Type" })]
    public void Key_columns_are_required_and_nothing_else_is(string tableName, string[] keyColumns)
    {
        var table = GenscapeDescriptors.AllTables.Single(t => t.TableName == tableName);
        var expected = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns)
            Assert.Equal(expected.Contains(column.Name), column.Required);
    }

    [Fact]
    public void Every_feed_has_its_own_table_and_a_distinct_id()
    {
        var feeds = GenscapeDescriptors.All;

        Assert.Equal(GenscapeDescriptors.AllTables.Count, feeds.Count);
        Assert.Equal(feeds.Count, feeds.Select(f => f.Table.TableName).Distinct().Count());
        Assert.Equal(feeds.Count, feeds.Select(f => f.FeedId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(feeds.Count, feeds.Select(f => f.EndpointPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_an_unknown_id()
    {
        Assert.NotNull(GenscapeDescriptors.Find("crudestorageweekly"));
        Assert.NotNull(GenscapeDescriptors.Find("CRUDESTORAGEWEEKLY"));
        Assert.Null(GenscapeDescriptors.Find("NotAFeed"));
    }

    /// <summary>The shipped EnabledFeeds list must name only real feeds, and all of them.</summary>
    [Fact]
    public void Shipped_enabled_feeds_covers_every_feed_and_nothing_else()
    {
        var enabled = new GenscapeSettings { ConnectionString = "x" }.EnabledFeeds;

        Assert.Equal(GenscapeDescriptors.All.Count, enabled.Count);
        foreach (var id in enabled)
            Assert.NotNull(GenscapeDescriptors.Find(id));
    }

    /// <summary>The shipped defaults are the ones documented — a silent drift here changes behaviour.</summary>
    [Fact]
    public void The_shipped_defaults_are_the_documented_ones()
    {
        var s = new GenscapeSettings { ConnectionString = "x" };

        Assert.Equal("https://api.genscape.com/oil-fundamentals/v1", s.BaseUrl);
        Assert.Equal("SEE_DB", s.ApiKey);
        Assert.Equal("revised", s.Revision);
        Assert.Equal(new[] { "NorthAmerica", "GulfCoast" }, s.Regions);
        Assert.Equal(30, s.DaysBack);            // 31 days inclusive
        Assert.Equal(30, s.SettledAfterDays);    // => the settled zone is empty, all hot
        Assert.Equal(365, s.WindowChunkDays);
        Assert.Equal(5000, s.MaxRowsPerResponse);
        Assert.Equal(GenscapeHotKeyStrategy.RunDate, s.HotKeyStrategy);
    }

    // ------------------------------------------------------------ merge semantics

    /// <summary>
    /// ⚠ The merge must match on EVERY primary-key column. Year and Week are derived
    /// from ReportDate, so a merge that matched on fewer would still work today — but the
    /// target's PK spans all of them, and a partial match risks updating a row into a key
    /// that already exists.
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly",
                new[] { "ReportDate", "Year", "Week", "Region", "Product", "StorageFieldType" })]
    [InlineData("arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly",
                new[] { "ReportDate", "Year", "Week", "Region", "Type" })]
    public void The_merge_matches_on_the_full_primary_key(string procName, string[] keyColumns)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, procName));

        foreach (var column in keyColumns)
            Assert.Matches(
                new Regex($@"tgt\.\[?{column}\]?\s*=\s*s\.\[?{column}\]?", RegexOptions.IgnoreCase), body);
    }

    /// <summary>
    /// ...and de-duplicates on that same key, so a repeated source row cannot abort the
    /// batch with "attempted to update the same row more than once".
    /// </summary>
    [Theory]
    [InlineData("arm.usp_BulkMergeOilFundamentalsCrudeStorageWeekly", "StorageFieldType")]
    [InlineData("arm.usp_BulkMergeOilFundamentalsCrudeTransportationWeekly", "Type")]
    public void The_dedup_partitions_by_the_same_key_the_merge_matches_on(string procName, string lastKeyColumn)
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, procName));

        var partition = Regex.Match(body, @"PARTITION\s+BY\s+(?<cols>[^)]*?)\s+ORDER\s+BY", RegexOptions.IgnoreCase);
        Assert.True(partition.Success, $"{procName} has no PARTITION BY.");

        var columns = partition.Groups["cols"].Value;
        Assert.Contains("ReportDate", columns, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Region", columns, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(lastKeyColumn, columns, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tables carry the primary keys the requester specified, column for column and
    /// in order. Transportation deliberately keys Type BEFORE Region even though the
    /// columns are declared the other way round.
    /// </summary>
    [Theory]
    [InlineData("PK_ARM_OilFundamentals_CrudeStorage_Weekly",
                new[] { "ReportDate", "Year", "Week", "Region", "Product", "StorageFieldType" })]
    [InlineData("PK_ARM_OilFundamentals_CrudeTransportation_Weekly",
                new[] { "ReportDate", "Year", "Week", "Type", "Region" })]
    public void The_primary_keys_match_the_supplied_ddl(string constraintName, string[] columnsInOrder)
    {
        var index = SchemaSql.IndexOf(constraintName, StringComparison.OrdinalIgnoreCase);
        Assert.True(index >= 0, $"001 has no constraint named {constraintName}.");

        var declaration = SchemaSql[index..Math.Min(SchemaSql.Length, index + 400)];

        var searchFrom = 0;
        foreach (var column in columnsInOrder)
        {
            var at = declaration.IndexOf(column, searchFrom, StringComparison.OrdinalIgnoreCase);
            Assert.True(at >= 0, $"{constraintName} does not list '{column}' after position {searchFrom}.");
            searchFrom = at + column.Length;
        }
    }

    /// <summary>
    /// ⚠ The only place <c>DATEPART(week, …)</c> may appear is the validation proc,
    /// which sets DATEFIRST explicitly first. A merge proc that computed the week itself
    /// would reintroduce exactly the session-dependence the C# derivation exists to
    /// avoid.
    /// </summary>
    [Fact]
    public void No_merge_proc_computes_the_week_in_sql()
    {
        foreach (var table in GenscapeDescriptors.AllTables)
        {
            var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, table.MergeProc));

            Assert.DoesNotContain("DATEPART", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DATEFIRST", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// ...and where the validation proc DOES use DATEPART, it must pin DATEFIRST first,
    /// or the check would report drift on a server whose default language is not
    /// us_english.
    /// </summary>
    [Fact]
    public void The_validation_proc_pins_DATEFIRST_before_using_DATEPART_week()
    {
        var body = TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, "arm.usp_ValidateLoad"));

        var setDateFirst = body.IndexOf("SET DATEFIRST 7", StringComparison.OrdinalIgnoreCase);
        var firstDatePart = body.IndexOf("DATEPART(week", StringComparison.OrdinalIgnoreCase);

        Assert.True(setDateFirst >= 0, "usp_ValidateLoad uses DATEPART(week) without pinning DATEFIRST.");
        Assert.True(firstDatePart > setDateFirst, "SET DATEFIRST must precede every DATEPART(week) use.");
    }

    /// <summary>
    /// The two derived key columns must still travel in the TVP. Deriving them inside the
    /// proc instead would make a PRIMARY KEY component depend on the session's DATEFIRST.
    /// </summary>
    [Theory]
    [InlineData("Year")]
    [InlineData("Week")]
    public void The_derived_key_columns_still_travel_in_the_tvp(string columnName)
    {
        foreach (var table in GenscapeDescriptors.AllTables)
        {
            var column = Assert.Single(table.Columns, c => c.Name == columnName);
            Assert.True(column.Required, $"{table.TableName}.{columnName} is a key column and must be NOT NULL.");
        }
    }
}
