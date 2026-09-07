using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.EOX.Tests;

/// <summary>
/// The DataTable half of the TVP contract, asserted without opening a connection.
/// <see cref="EoxTvpContractTests"/> asserts the .sql half; together they close the
/// loop between the descriptor, the sink and the server.
/// </summary>
public sealed class SinkTests
{
    /// <summary>Exposes the protected <c>BuildTable</c>.</summary>
    private sealed class TestableSink : EoxTableSink
    {
        public TestableSink(EoxFeedDescriptor feed)
            : base(feed, "Server=(local);Database=EOX;Integrated Security=SSPI;", NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<EoxRow> rows) => BuildTable(rows);
    }

    public static TheoryData<string> AllFeedIds()
    {
        var data = new TheoryData<string>();
        foreach (var feed in EoxDescriptors.All) data.Add(feed.FeedId);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void The_data_table_columns_are_the_descriptor_columns_in_order(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var table = new TestableSink(feed).Build(Array.Empty<EoxRow>());

        Assert.Equal(feed.Columns.Count, table.Columns.Count);

        for (var i = 0; i < feed.Columns.Count; i++)
        {
            Assert.Equal(feed.Columns[i].Name, table.Columns[i].ColumnName);
            Assert.Equal(feed.Columns[i].ClrType, table.Columns[i].DataType);
        }
    }

    /// <summary>
    /// The requester's DDL says FLOAT for every price. A <c>decimal</c> DataTable
    /// column would force a server-side conversion on every one of ~59,000 rows a
    /// day, so the CLR type is pinned here.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeedIds))]
    public void Price_columns_are_double_and_date_columns_are_DateTime(string feedId)
    {
        var feed = EoxDescriptors.Find(feedId)!;
        var table = new TestableSink(feed).Build(Array.Empty<EoxRow>());

        foreach (var name in new[] { "Mid", "Bid", "Ask" })
            Assert.Equal(typeof(double), table.Columns[name]!.DataType);

        foreach (var name in new[] { "CurveDate", "ContractBegin", "ContractEnd" })
            Assert.Equal(typeof(DateTime), table.Columns[name]!.DataType);
    }

    [Fact]
    public void Real_parsed_rows_land_in_the_table_unchanged()
    {
        var feed = EoxDescriptors.CrudeOil;
        var rows = TestHelpers.Reader().Parse(Samples.CrudeOil2026, TestHelpers.Unit(feed));

        var table = new TestableSink(feed).Build(rows);

        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(new DateTime(2026, 9, 4), table.Rows[0]["CurveDate"]);
        Assert.Equal("A", table.Rows[0]["LocationCode"]);
        Assert.Equal(93.639, (double)table.Rows[0]["Mid"], 6);
        Assert.Equal("EOD_CSV_C_20260904_1430.csv", table.Rows[0]["FileName"]);
    }

    [Fact]
    public void A_null_optional_cell_becomes_DBNull_and_not_a_clr_null()
    {
        var feed = EoxDescriptors.NaturalGas;
        var rows = TestHelpers.Reader().Parse(
            Samples.NaturalGas2014, TestHelpers.Unit(feed, new DateOnly(2014, 5, 19)));

        var table = new TestableSink(feed).Build(rows);

        Assert.Equal(DBNull.Value, table.Rows[0]["FP"]);
    }

    /// <summary>
    /// A row whose length disagrees with the descriptor means the reader and the
    /// registry have drifted. That must throw, not reach the server as silently
    /// shifted columns.
    /// </summary>
    [Fact]
    public void A_row_of_the_wrong_width_throws_rather_than_shifting_columns()
    {
        var feed = EoxDescriptors.Ngl;
        var bad = new EoxRow(new object[] { DBNull.Value, DBNull.Value });

        var ex = Assert.Throws<InvalidOperationException>(() => new TestableSink(feed).Build(new[] { bad }));

        Assert.Contains("declares", ex.Message);
    }
}
