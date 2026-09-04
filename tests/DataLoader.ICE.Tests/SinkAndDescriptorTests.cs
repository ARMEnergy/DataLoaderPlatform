using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The DataTable half of the TVP contract, plus the descriptor invariants the
/// whole loader rests on.
/// </summary>
public sealed class SinkAndDescriptorTests
{
    /// <summary>Exposes the protected BuildTable so the shape can be asserted without SQL.</summary>
    private sealed class TestableSink : IceTableSink
    {
        public TestableSink(IceTableDescriptor table) : base(table, "Server=(local);", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<IceRow> rows) => BuildTable(rows);
    }

    public static TheoryData<string> AllTableNames()
    {
        var data = new TheoryData<string>();
        foreach (var table in IceDescriptors.AllTables) data.Add(table.TableName);
        return data;
    }

    private static IceTableDescriptor Table(string name) =>
        IceDescriptors.AllTables.Single(t => t.TableName == name);

    [Theory]
    [MemberData(nameof(AllTableNames))]
    public void DataTable_columns_match_the_descriptor_by_name_order_and_clr_type(string tableName)
    {
        var table = Table(tableName);
        var built = new TestableSink(table).Build(Array.Empty<IceRow>());

        Assert.Equal(table.Columns.Count, built.Columns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            Assert.Equal(table.Columns[i].Name, built.Columns[i].ColumnName);
            Assert.Equal(table.Columns[i].ClrType, built.Columns[i].DataType);
        }
    }

    [Fact]
    public void Rows_are_added_in_descriptor_order()
    {
        var table = IceDescriptors.EnvFuturesTable;
        var (rows, _) = TestHelpers.Reader(IceDescriptors.Find("EnvFutures")!)
                                   .Parse(TestHelpers.Bytes(Samples.PhysEnv), "u");

        var built = new TestableSink(table).Build(rows);

        Assert.Equal(3, built.Rows.Count);
        Assert.Equal(new DateTime(2026, 8, 28), built.Rows[0][table.Ordinal("TradeDate")]);
        Assert.Equal("ACA", built.Rows[0][table.Ordinal("Contract")]);
        Assert.Equal("CCA", built.Rows[0][table.Ordinal("Hub")]);
    }

    /// <summary>
    /// A row whose length disagrees with the descriptor is a bug that must not reach
    /// the server as silently shifted columns.
    /// </summary>
    [Fact]
    public void Wrong_width_row_throws_rather_than_shifting_columns()
    {
        var sink = new TestableSink(IceDescriptors.EnvFuturesTable);
        var bad = new IceRow(new object[] { DBNull.Value, DBNull.Value });

        var ex = Assert.Throws<InvalidOperationException>(() => sink.Build(new[] { bad }));
        Assert.Contains("declares", ex.Message);
    }

    // ---------------------------------------------------------------- descriptors

    [Fact]
    public void There_are_eighteen_feeds_and_twelve_tables()
    {
        Assert.Equal(18, IceDescriptors.All.Count);
        Assert.Equal(12, IceDescriptors.AllTables.Count);
    }

    [Fact]
    public void Feed_ids_are_unique_and_findable()
    {
        Assert.Equal(
            IceDescriptors.All.Count,
            IceDescriptors.All.Select(f => f.FeedId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var feed in IceDescriptors.All)
            Assert.Same(feed, IceDescriptors.Find(feed.FeedId));

        Assert.Same(IceDescriptors.Find("icegas"), IceDescriptors.Find("IceGas"));
        Assert.Null(IceDescriptors.Find("NoSuchFeed"));
    }

    [Fact]
    public void Every_feed_points_at_a_registered_table()
    {
        foreach (var feed in IceDescriptors.All)
            Assert.Contains(feed.Table, IceDescriptors.AllTables);
    }

    /// <summary>
    /// The two shared tables are shared by IDENTITY, not by copy: six feeds must
    /// reference the same arm.Futures descriptor instance, or their column lists
    /// could drift apart while still agreeing with one TVP.
    /// </summary>
    [Fact]
    public void Feeds_that_share_a_table_share_one_descriptor_instance()
    {
        var futuresFeeds = IceDescriptors.All.Where(f => f.Table.TableName == "arm.Futures").ToList();
        Assert.Equal(6, futuresFeeds.Count);
        Assert.All(futuresFeeds, f => Assert.Same(IceDescriptors.FuturesTable, f.Table));

        var optionsFeeds = IceDescriptors.All.Where(f => f.Table.TableName == "arm.Options").ToList();
        Assert.Equal(2, optionsFeeds.Count);
        Assert.All(optionsFeeds, f => Assert.Same(IceDescriptors.OptionsTable, f.Table));
    }

    [Fact]
    public void Every_table_ends_with_the_derived_SourcePath_column()
    {
        foreach (var table in IceDescriptors.AllTables)
        {
            var last = table.Columns[^1];
            Assert.Equal("SourcePath", last.Name);
            Assert.Equal(IceDerived.SourcePath, last.Derived);
            Assert.False(last.Required);
        }
    }

    /// <summary>Only SourcePath may be derived; anything else must come from a header.</summary>
    [Fact]
    public void Non_derived_columns_all_declare_at_least_one_header()
    {
        foreach (var table in IceDescriptors.AllTables)
            foreach (var column in table.Columns.Where(c => c.Derived == IceDerived.None))
                Assert.True(column.SourceHeaders.Count > 0,
                    $"{table.TableName}.{column.Name} has no source header.");
    }

    // ---------------------------------------------------------------- URLs

    [Theory]
    [InlineData("EnvFutures", "Settlement_Reports_CSV/Environmentals/icecleared_physenv_2026_08_28.dat")]
    [InlineData("EnvOptions", "Settlement_Reports_CSV/Environmentals/icecleared_physenvoptions_2026_08_28.dat")]
    [InlineData("NgxGas", "Settlement_Reports_CSV/Gas/ngxcleared_gas_2026_08_28.dat")]
    [InlineData("NgxPower", "Settlement_Reports_CSV/Power/ngxcleared_power_2026_08_28.dat")]
    [InlineData("IceGas", "Settlement_Reports_CSV/Gas/icecleared_gas_2026_08_28.dat")]
    [InlineData("IceNgl", "Settlement_Reports_CSV/NGL/icecleared_ngl_2026_08_28.dat")]
    [InlineData("IceOil", "Settlement_Reports_CSV/Oil/icecleared_oil_2026_08_28.dat")]
    [InlineData("IceOilCa", "Settlement_Reports_CSV/Oil/iceclearedoil_ca_2026_08_28.dat")]
    [InlineData("CrudeIndex", "Crude_Index/ICE_Crude_Oil_Index_20260828.csv")]
    [InlineData("CrudeIndexTrades", "Crude_Index/ICE_Crude_Oil_Index_Trades_20260828.csv")]
    [InlineData("PowerFutures", "Settlement_Reports_CSV/Power/icecleared_power_2026_08_28.dat")]
    [InlineData("PowerOptions", "Settlement_Reports_CSV/Power/icecleared_poweroptions_2026_08_28.dat")]
    [InlineData("FcaOptions", "ICEF_options_greeks/ICEFCA_Options_2026_08_28.dat")]
    [InlineData("FusFinOptions", "ICEF_options_greeks/ICEFUS_FinOptions_2026_08_28.dat")]
    [InlineData("FusSoftOptions", "ICEF_options_greeks/ICEFUS_SoftOptions_2026_08_28.dat")]
    [InlineData("IfllOptions", "FixedIncome_Settlements/IFLL_Options_2026_08_28.xlsx")]
    [InlineData("GasOptions", "Settlement_Reports_CSV/Gas/icecleared_gasoptions_2026_08_28.dat")]
    [InlineData("OilOptions", "Settlement_Reports_CSV/Oil/icecleared_oiloptions_2026_08_28.dat")]
    public void Feed_path_matches_the_verified_url(string feedId, string expected)
    {
        var feed = IceDescriptors.Find(feedId)!;
        Assert.Equal(expected, feed.PathFor(new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void Only_the_crude_index_feeds_use_the_compact_date_token()
    {
        foreach (var feed in IceDescriptors.All)
        {
            var expected = feed.FeedId.StartsWith("CrudeIndex", StringComparison.Ordinal)
                ? "yyyyMMdd"
                : "yyyy_MM_dd";

            Assert.Equal(expected, feed.DateFormat);
        }
    }

    [Fact]
    public void File_name_is_the_last_path_segment()
    {
        Assert.Equal("icecleared_gas_2026_08_28.dat",
            IceDescriptors.Find("IceGas")!.FileNameFor(new DateOnly(2026, 8, 28)));

        Assert.Equal("IFLL_Options_2026_08_28.xlsx",
            IceDescriptors.Find("IfllOptions")!.FileNameFor(new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void Built_url_joins_the_base_without_doubling_the_slash()
    {
        var reader = TestHelpers.Reader(
            IceDescriptors.Find("IceGas")!,
            TestHelpers.Settings(s => s.DownloadBaseUrl = "https://downloads.ice.com/"));

        Assert.Equal(
            "https://downloads.ice.com/Settlement_Reports_CSV/Gas/icecleared_gas_2026_08_28.dat",
            reader.BuildUrl(new DateOnly(2026, 8, 28)));
    }

    /// <summary>The URL is persisted to FileLog and read by humans — no token in it, ever.</summary>
    [Fact]
    public void Built_url_never_carries_a_token()
    {
        var reader = TestHelpers.Reader(IceDescriptors.Find("IceGas")!);
        var url = reader.BuildUrl(new DateOnly(2026, 8, 28));

        Assert.DoesNotContain("iceSsoCookie", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_is_the_only_non_text_feed()
    {
        var xlsx = IceDescriptors.All.Where(f => f.Format == IceFileFormat.Xlsx).ToList();

        Assert.Single(xlsx);
        Assert.Equal("IfllOptions", xlsx[0].FeedId);
    }

    [Fact]
    public void Csv_format_is_used_only_by_the_crude_index_feeds()
    {
        var csv = IceDescriptors.All.Where(f => f.Format == IceFileFormat.Csv).Select(f => f.FeedId).ToList();
        Assert.Equal(new[] { "CrudeIndex", "CrudeIndexTrades" }, csv);
    }

    /// <summary>The shipped defaults are the ones the requester asked for.</summary>
    [Fact]
    public void Shipped_defaults_match_the_specification()
    {
        var settings = new IceSettings();

        Assert.Equal(30, settings.DaysBack);
        Assert.Equal(30, settings.SettledAfterDays);
        Assert.Equal(7, settings.FileRetentionDays);
        Assert.Equal("SEE_DB", settings.UserId);
        Assert.Equal("SEE_DB", settings.Password);
        Assert.False(settings.ForceDownload);
        Assert.Equal(IceHotKeyStrategy.RunDate, settings.HotKeyStrategy);
    }

    /// <summary>The default EnabledFeeds list must name every feed and nothing else.</summary>
    [Fact]
    public void Default_enabled_feeds_covers_every_descriptor()
    {
        var enabled = new IceSettings().EnabledFeeds;

        Assert.Equal(IceDescriptors.All.Count, enabled.Count);
        foreach (var id in enabled)
            Assert.NotNull(IceDescriptors.Find(id));
    }
}
