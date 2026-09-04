using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// The DataTable half of the TVP contract, plus the descriptor invariants the SQL
/// tests assume.
///
/// <para>
/// <see cref="CriterionTvpContractTests"/> compares the descriptor to the .sql file.
/// These tests compare the descriptor to the DataTable the sink actually builds — the
/// third side of the triangle, and the one that reaches SQL Server.
/// </para>
/// </summary>
public sealed class SinkAndDescriptorTests
{
    /// <summary>Exposes the protected BuildTable without opening a connection.</summary>
    private sealed class ProbeSink : CriterionTvpSink
    {
        public ProbeSink(CriterionTableDescriptor table)
            : base(table, "Server=(local);Database=Criterion;Integrated Security=SSPI;", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<CriterionRow> rows) => BuildTable(rows);
    }

    public static TheoryData<string> AllTableNames()
    {
        var data = new TheoryData<string>();
        foreach (var table in CriterionDescriptors.AllTables) data.Add(table.TableName);
        return data;
    }

    private static CriterionTableDescriptor Table(string tableName) =>
        CriterionDescriptors.AllTables.Single(t => t.TableName == tableName);

    // ------------------------------------------------------------- the DataTable

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void DataTable_columns_match_the_descriptor_by_name_order_and_clr_type(string tableName)
    {
        var table = Table(tableName);
        var built = new ProbeSink(table).Build(Array.Empty<CriterionRow>());

        Assert.Equal(table.Columns.Count, built.Columns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            Assert.Equal(table.Columns[i].Name, built.Columns[i].ColumnName);
            Assert.Equal(table.Columns[i].ClrType, built.Columns[i].DataType);
        }
    }

    /// <summary>
    /// A row whose length disagrees with the descriptor is a BUG, and it must throw
    /// rather than reach the server as silently shifted columns.
    /// </summary>
    [Fact]
    public void A_row_of_the_wrong_width_throws_rather_than_shifting_columns()
    {
        var table = CriterionDescriptors.MiscUnitTable;
        var sink = new ProbeSink(table);

        var ex = Assert.Throws<InvalidOperationException>(
            () => sink.Build(new[] { new CriterionRow(new object[] { Guid.NewGuid(), "x" }) }));

        Assert.Contains("declares", ex.Message);
    }

    [Fact]
    public void Rows_land_in_descriptor_order()
    {
        var table = CriterionDescriptors.MiscUnitTable;
        var id = Guid.NewGuid();

        var built = new ProbeSink(table).Build(new[]
        {
            new CriterionRow(new object[] { id, "5521544f8215d6ac32947ccd", "Bbls/d" })
        });

        var row = built.Rows[0];
        Assert.Equal(id, row["UnitId"]);
        Assert.Equal("5521544f8215d6ac32947ccd", row["MongoId"]);
        Assert.Equal("Bbls/d", row["UnitDesc"]);
    }

    [Fact]
    public void DBNull_survives_into_the_DataTable()
    {
        var table = CriterionDescriptors.MiscUnitTable;

        var built = new ProbeSink(table).Build(new[]
        {
            new CriterionRow(new object[] { Guid.NewGuid(), DBNull.Value, DBNull.Value })
        });

        Assert.Equal(DBNull.Value, built.Rows[0]["MongoId"]);
        Assert.Equal(DBNull.Value, built.Rows[0]["UnitDesc"]);
    }

    // ------------------------------------------------------------- the batching

    /// <summary>A batch smaller than the limit takes the single-call path unchanged.</summary>
    [Fact]
    public async Task An_empty_write_never_touches_the_database()
    {
        var sink = new CriterionBatchingSink(
            CriterionDescriptors.MiscUnitTable,
            "Server=(local);Database=Criterion;Integrated Security=SSPI;Connect Timeout=1;",
            50_000, NullLogger.Instance);

        // No connection is attempted for zero rows, so this returns rather than
        // failing to reach a server that is not there.
        Assert.Equal(0, await sink.WriteAsync(Array.Empty<CriterionRow>(), CancellationToken.None));
    }

    // ----------------------------------------------------------- the descriptors

    /// <summary>One feed per table and one table per feed — no accidental sharing.</summary>
    [Fact]
    public void Every_feed_has_its_own_table()
    {
        var feeds = CriterionDescriptors.All;

        Assert.Equal(CriterionDescriptors.AllTables.Count, feeds.Count);
        Assert.Equal(feeds.Count, feeds.Select(f => f.Table.TableName).Distinct().Count());
        Assert.Equal(feeds.Count, feeds.Select(f => f.FeedId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Every table in the registry must be reachable through a feed.</summary>
    [Fact]
    public void Every_table_belongs_to_a_feed()
    {
        var used = CriterionDescriptors.All.Select(f => f.Table.TableName).ToHashSet();

        foreach (var table in CriterionDescriptors.AllTables)
            Assert.Contains(table.TableName, used);
    }

    /// <summary>The shipped EnabledFeeds list must name only real feeds, and all of them.</summary>
    [Fact]
    public void Shipped_enabled_feeds_covers_every_feed_and_nothing_else()
    {
        var enabled = TestHelpers.Settings().EnabledFeeds;

        Assert.Equal(CriterionDescriptors.All.Count, enabled.Count);
        foreach (var id in enabled)
            Assert.NotNull(CriterionDescriptors.Find(id));
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_an_unknown_id()
    {
        Assert.NotNull(CriterionDescriptors.Find("miscunit"));
        Assert.NotNull(CriterionDescriptors.Find("MISCUNIT"));
        Assert.Null(CriterionDescriptors.Find("NotAFeed"));
    }

    /// <summary>
    /// Every key column must be Required, or a NULL would merge the row under a blank
    /// key and collide with every other such row.
    /// </summary>
    [Theory]
    [InlineData("arm.Financial_Metadata", new[] { "MetadataId" })]
    [InlineData("arm.Financial_Series", new[] { "FinancialJsonId" })]
    [InlineData("arm.Financial_SeriesData", new[] { "FinancialJsonId", "Date" })]
    [InlineData("arm.Misc_Period", new[] { "PeriodId" })]
    [InlineData("arm.Misc_Unit", new[] { "UnitId" })]
    [InlineData("arm.Pipelines_Metadata", new[] { "MetadataId" })]
    [InlineData("arm.Pipelines_NominationPoint", new[] { "MetadataId", "EffGasDay", "CycleId", "HourlyCycleId" })]
    [InlineData("arm.Pipelines_Pointflows", new[] { "MetadataId", "EffGasDay", "CycleId" })]
    [InlineData("arm.Pipelines_Region", new[] { "RegionId", "StateId" })]
    public void Key_columns_are_required_and_nothing_else_is(string tableName, string[] keyColumns)
    {
        var table = Table(tableName);
        var expected = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns)
            Assert.Equal(expected.Contains(column.Name), column.Required);
    }

    /// <summary>
    /// The two JSON-derived columns are the only ones with no source expression. Any
    /// other empty expression would generate <c>SELECT  AS "X"</c> and fail at the
    /// server.
    /// </summary>
    [Fact]
    public void Only_the_json_derived_columns_lack_a_source_expression()
    {
        foreach (var table in CriterionDescriptors.AllTables)
        foreach (var column in table.Columns)
        {
            var derived = table.TableName == "arm.Financial_SeriesData" &&
                          column.Name is "Date" or "Value";

            Assert.Equal(derived, string.IsNullOrEmpty(column.SourceExpression));
        }
    }

    /// <summary>
    /// Only <c>FinancialSeriesData</c> is paged, and it is the only feed that declares
    /// key columns. The reader special-cases it by feed id, so a second paged feed
    /// would silently take the tabular path.
    /// </summary>
    [Fact]
    public void Exactly_one_feed_is_paged_and_it_declares_its_key_columns()
    {
        var paged = CriterionDescriptors.All
            .Where(f => f.WindowMode == CriterionWindowMode.PagedDayWindow)
            .ToList();

        var single = Assert.Single(paged);
        Assert.Equal(CriterionFeed.FinancialSeriesData, single.FeedId);
        Assert.False(string.IsNullOrEmpty(single.KeyColumns));

        foreach (var feed in CriterionDescriptors.All.Where(f => f.WindowMode != CriterionWindowMode.PagedDayWindow))
            Assert.True(string.IsNullOrEmpty(feed.KeyColumns), $"{feed.FeedId} declares key columns but is not paged.");
    }

    /// <summary>A windowed feed must name its window column; a snapshot must not.</summary>
    [Fact]
    public void Window_column_is_present_exactly_when_the_feed_is_windowed()
    {
        foreach (var feed in CriterionDescriptors.All)
        {
            var windowed = feed.WindowMode != CriterionWindowMode.Snapshot;
            Assert.Equal(windowed, !string.IsNullOrEmpty(feed.WindowColumn));
        }
    }

    /// <summary>
    /// ⚠ The two financial feeds MUST read the same relation. Reading different ones is
    /// exactly the mistake the original specification would have produced — it named
    /// <c>financial_json</c> for the publications and the stale
    /// <c>financial_json_partitioned</c> for the observations, whose UUID sets do not
    /// intersect, so <c>arm.Financial_SeriesData</c> would never have joined
    /// <c>arm.Financial_Series</c>.
    /// </summary>
    [Fact]
    public void Both_financial_feeds_read_the_same_source_relation()
    {
        var series = CriterionDescriptors.Find(CriterionFeed.FinancialSeries)!;
        var data = CriterionDescriptors.Find(CriterionFeed.FinancialSeriesData)!;

        Assert.Equal(series.FromClause, data.FromClause);
        Assert.Equal("data_series.financial_json_latest", series.FromClause);
        Assert.Equal(series.WindowColumn, data.WindowColumn);
    }

    /// <summary>
    /// The stale relation must never be read. Pinned so nobody "restores" the original
    /// specification without also re-reading why it was changed.
    /// </summary>
    [Fact]
    public void The_stale_partitioned_relation_is_never_read()
    {
        foreach (var feed in CriterionDescriptors.All)
            Assert.DoesNotContain("financial_json_partitioned", feed.FromClause, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The source table names are the CORRECTED plural ones. The request said
    /// <c>misc.period</c> and <c>misc.unit</c>; neither exists.
    /// </summary>
    [Fact]
    public void Misc_dimension_sources_use_the_real_plural_table_names()
    {
        Assert.Equal("misc.periods", CriterionDescriptors.Find(CriterionFeed.MiscPeriod)!.FromClause);
        Assert.Equal("misc.units", CriterionDescriptors.Find(CriterionFeed.MiscUnit)!.FromClause);
    }
}
