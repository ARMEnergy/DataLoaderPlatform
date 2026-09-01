using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Argus.Tests;

/// <summary>
/// The sink's DataTable is the other half of the TVP contract
/// (<see cref="ArgusTvpContractTests"/> covers the .sql half). These tests assert
/// it is built from the descriptor in descriptor order, with the CLR types
/// SqlClient needs to bind each SQL type.
/// </summary>
public sealed class SinkTests
{
    private sealed class TestableSink : ArgusTableSink
    {
        public TestableSink(ArgusFeedDescriptor feed)
            : base(feed, "Server=(local);Database=Argus;Integrated Security=SSPI;", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<ArgusRow> rows) => BuildTable(rows);
    }

    public static TheoryData<string> AllFeedIds()
    {
        var data = new TheoryData<string>();
        foreach (var feed in ArgusDescriptors.All) data.Add(feed.FeedId);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void The_data_table_mirrors_the_descriptor_columns_in_order(string feedId)
    {
        var feed = ArgusDescriptors.Find(feedId)!;
        var table = new TestableSink(feed).Build(Array.Empty<ArgusRow>());

        Assert.Equal(feed.Columns.Count, table.Columns.Count);

        for (var i = 0; i < feed.Columns.Count; i++)
        {
            Assert.Equal(feed.Columns[i].Name, table.Columns[i].ColumnName);
            Assert.Equal(feed.Columns[i].ClrType, table.Columns[i].DataType);
        }
    }

    /// <summary>
    /// End to end within the process: real sample bytes -> reader -> sink table.
    /// If the reader and the sink ever disagreed on column order this is where the
    /// values would land in the wrong columns.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Parsed_rows_load_into_the_table_without_type_errors(string feedId)
    {
        var feed = ArgusDescriptors.Find(feedId)!;
        var unit = feed.IsDatedFeed ? TestHelpers.TimeSeriesUnit() : TestHelpers.Unit(feed);

        var rows = TestHelpers.Reader().Parse(Samples.For(feedId), unit);
        var table = new TestableSink(feed).Build(rows);

        Assert.Equal(rows.Count, table.Rows.Count);
    }

    [Fact]
    public void Values_land_in_the_columns_the_descriptor_names()
    {
        var feed = ArgusDescriptors.TimeSeries;
        var rows = TestHelpers.Reader().Parse(Samples.TimeSeries, TestHelpers.TimeSeriesUnit());
        var table = new TestableSink(feed).Build(rows);

        var row = table.Rows[0];
        Assert.Equal("DHC", row["Module"]);
        Assert.Equal("PA0045347", row["Code"]);
        Assert.Equal((short)2, row["TimestampTypeID"]);
        Assert.Equal((short)6, row["PriceTypeID"]);
        Assert.Equal(new DateTime(2026, 8, 26), row["Date"]);
        Assert.Equal("N", row["RecordStatus"]);
        Assert.Equal(6.02m, row["Value"]);
        Assert.Equal("/DCRDEUS/20260827dhc.csv", row["SourcePath"]);
        Assert.Equal(new DateTime(2026, 8, 27), row["SourceFileDate"]);
    }

    [Fact]
    public void Nulls_are_dbnull_in_the_table()
    {
        var rows = TestHelpers.Reader().Parse(Samples.Codes, TestHelpers.Unit(ArgusDescriptors.Codes));
        var table = new TestableSink(ArgusDescriptors.Codes).Build(rows);

        Assert.Equal(DBNull.Value, table.Rows[0]["Specification"]);
    }

    /// <summary>
    /// A length mismatch means the reader and the descriptor have diverged. Better
    /// to throw than to hand SqlClient a row that binds into shifted columns.
    /// </summary>
    [Fact]
    public void A_row_of_the_wrong_length_throws_rather_than_shifting_columns()
    {
        var sink = new TestableSink(ArgusDescriptors.PriceType);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sink.Build(new[] { new ArgusRow(new object[] { (short)1 }) }));

        Assert.Contains("descriptor declares", ex.Message);
    }

    /// <summary>
    /// Each feed must point at its own proc: SqlWriteGate keys on the proc name, so
    /// this is both a correctness and a concurrency property.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void The_sink_targets_the_feeds_own_proc_and_tvp(string feedId)
    {
        var feed = ArgusDescriptors.Find(feedId)!;

        Assert.StartsWith("dlp.", feed.MergeProc);
        Assert.StartsWith("dlp.", feed.TvpType);
        Assert.StartsWith("dlp.", feed.TargetTable);
    }
}

/// <summary>
/// Properties of the descriptor registry itself — the cheap invariants that catch
/// a copy-paste slip when a feed is added.
/// </summary>
public sealed class DescriptorTests
{
    [Fact]
    public void There_are_fifteen_reference_feeds_and_one_fact_feed()
    {
        Assert.Equal(15, ArgusDescriptors.Documentation.Count);
        Assert.Equal(16, ArgusDescriptors.All.Count);
        Assert.Single(ArgusDescriptors.All, f => f.IsDatedFeed);
    }

    [Fact]
    public void Every_reference_feed_names_a_documentation_file()
    {
        Assert.All(ArgusDescriptors.Documentation, feed =>
        {
            Assert.Equal(ArgusDescriptors.DocumentationFolder, feed.RemoteFolder);
            Assert.False(string.IsNullOrWhiteSpace(feed.FileName));
            Assert.StartsWith("latest", feed.FileName!, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".csv", feed.FileName!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void The_fact_feed_discovers_its_files()
    {
        Assert.Null(ArgusDescriptors.TimeSeries.FileName);
        Assert.Equal(ArgusDescriptors.TimeSeriesFolder, ArgusDescriptors.TimeSeries.RemoteFolder);
    }

    [Fact]
    public void Every_feed_has_at_least_one_required_column()
    {
        // Without one, a merge would have nothing to key on.
        Assert.All(ArgusDescriptors.All, feed => Assert.Contains(feed.Columns, c => c.Required));
    }

    [Fact]
    public void Column_names_are_unique_within_a_feed()
    {
        Assert.All(ArgusDescriptors.All, feed =>
            Assert.Equal(
                feed.Columns.Count,
                feed.Columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    [Fact]
    public void Source_headers_are_unique_within_a_feed()
    {
        Assert.All(ArgusDescriptors.All, feed =>
        {
            var headers = feed.RequiredHeaders.ToList();
            Assert.Equal(headers.Count, headers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void Only_the_fact_feed_has_derived_columns()
    {
        Assert.All(ArgusDescriptors.Documentation, feed =>
            Assert.All(feed.Columns, c => Assert.Equal(ArgusDerived.None, c.Derived)));

        Assert.Equal(4, ArgusDescriptors.TimeSeries.Columns.Count(c => c.Derived != ArgusDerived.None));
    }

    [Fact]
    public void A_derived_column_never_also_names_a_header()
    {
        Assert.All(ArgusDescriptors.All, feed =>
            Assert.All(feed.Columns, c =>
            {
                if (c.Derived == ArgusDerived.None) Assert.NotNull(c.SourceHeader);
                else Assert.Null(c.SourceHeader);
            }));
    }

    [Fact]
    public void Feeds_are_findable_by_id_case_insensitively()
    {
        Assert.NotNull(ArgusDescriptors.Find("timeseries"));
        Assert.NotNull(ArgusDescriptors.Find("CODES"));
        Assert.Null(ArgusDescriptors.Find("NoSuchFeed"));
    }

    /// <summary>The shipped default must enable every reference feed.</summary>
    [Fact]
    public void The_default_enabled_feeds_list_covers_all_reference_feeds()
    {
        var settings = new ArgusSettings();

        Assert.Equal(
            ArgusDescriptors.Documentation.Select(f => f.FeedId).OrderBy(x => x),
            settings.EnabledFeeds.OrderBy(x => x));

        Assert.All(settings.EnabledFeeds, id => Assert.NotNull(ArgusDescriptors.Find(id)));
    }

    [Fact]
    public void The_shipped_defaults_point_at_the_verified_source()
    {
        var settings = new ArgusSettings();

        Assert.Equal("ftp.argusmedia.com", settings.FtpHost);
        Assert.Equal(21, settings.FtpPort);
        Assert.False(settings.UseFtps);              // the drop is plain FTP
        Assert.True(settings.UsePassiveMode);
        Assert.Equal("/DOCUMENTATION", settings.DocumentationDirectory);
        Assert.Equal("/DCRDEUS", settings.TimeSeriesDirectory);

        // Credentials must ship as the sentinel, never as literals.
        Assert.Equal("SEE_DB", settings.Username);
        Assert.Equal("SEE_DB", settings.Password);
    }
}
