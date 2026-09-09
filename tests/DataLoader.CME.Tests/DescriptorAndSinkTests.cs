using Xunit;
using System.Data;

namespace DataLoader.CME.Tests;

/// <summary>
/// Descriptor self-checks and the DataTable half of the TVP contract.
/// </summary>
public class DescriptorTests
{
    [Fact]
    public void RegistryIsSelfConsistent()
    {
        Assert.Empty(CmeDescriptors.Validate());
    }

    /// <summary>
    /// The fixed-width geometry is the whole basis of the parse. These are the
    /// numbers measured from 727,042 live rows; pinning them means a future edit
    /// has to be deliberate.
    /// </summary>
    [Fact]
    public void GeometryMatchesTheMeasuredBulletinLayout()
    {
        Assert.Equal(1, CmeDescriptors.LabelStart);
        Assert.Equal(13, CmeDescriptors.LabelEnd);

        var expected = new[]
        {
            ("Open", 14, 22, 0),
            ("High", 23, 35, 36),
            ("Low", 37, 48, 49),
            ("Last", 50, 61, 62),
            ("Settle", 63, 74, 0),
            ("PctChange", 75, 87, 0),
            ("EstVol", 88, 99, 0),
            ("PriorSettle", 100, 114, 0),
            ("PriorVol", 115, 126, 0),
            ("PriorInt", 127, 138, 0)
        };

        Assert.Equal(expected, CmeDescriptors.MeasureFields);
    }

    /// <summary>Only High, Low and Last carry indicators — matching the DDL's three indicator columns.</summary>
    [Fact]
    public void ExactlyThreeFieldsCarryAnIndicator()
    {
        var withIndicator = CmeDescriptors.MeasureFields
            .Where(f => f.IndicatorColumn != 0)
            .Select(f => f.Name)
            .ToList();

        Assert.Equal(new[] { "High", "Low", "Last" }, withIndicator);
    }

    [Fact]
    public void ValueRightEdgesCoverTheNumericEdgesAndTheIndicatorColumns()
    {
        Assert.Equal(
            new[] { 22, 35, 36, 48, 49, 61, 62, 74, 87, 99, 114, 126, 138 },
            CmeDescriptors.ValueRightEdges.OrderBy(x => x));
    }

    [Fact]
    public void EveryKeyColumnIsRequired()
    {
        foreach (var table in CmeDescriptors.All)
            foreach (var key in table.KeyColumns)
                Assert.True(table.Columns[table.IndexOf(key)].Required, $"{table.TableId}.{key}");
    }

    [Fact]
    public void FeedIdSplitsOnTheFirstUnderscore()
    {
        var feed = new CmeFeed("EOD", "STLNYMEX", "BAS_STLNYMEX_STLCPC", "EOD_STLNYMEX");

        Assert.Equal("EOD_STLNYMEX", feed.FeedId);
        Assert.Equal("BAS_STLNYMEX_STLCPC/EOD_STLNYMEX", feed.RelativePath);
        Assert.Equal("STLNYMEX_20260904.txt", feed.FileNameFor(new DateOnly(2026, 9, 4)));
    }

    [Fact]
    public void KnownFeedListMatchesTheEightEntitledFeeds()
    {
        Assert.Equal(8, CmeDescriptors.KnownFeedIds.Count);
        Assert.All(CmeDescriptors.KnownFeedIds, id => Assert.StartsWith("EOD_", id));
    }
}

/// <summary>
/// The DataTable half of the TVP contract, and the sink's NOT NULL guard.
/// </summary>
public class SinkTests
{
    private static CmeFactRow Row(CmeRowKind kind, string description = "DESC") => new()
    {
        Kind = kind,
        ExchangeCode = "STLNYMEX",
        ProductCode = "EOD",
        TradeDate = new DateOnly(2026, 9, 4),
        ProductSymbol = "0CJ",
        ProductDescription = description,
        ContractYear = 2026,
        ContractMonth = 9,
        PutCall = kind == CmeRowKind.Option ? "C" : string.Empty,
        Strike = kind == CmeRowKind.Option ? 1540m : 0m,
        Settle = 1821.0m,
        High = 2136.9m,
        HighABIndicator = "B",
        RowOrdinal = 1
    };

    [Theory]
    [InlineData("Option")]
    [InlineData("Future")]
    public void DataTableColumnsMatchTheDescriptorExactly(string tableId)
    {
        var descriptor = CmeDescriptors.All.Single(t => t.TableId == tableId);
        var kind = tableId == "Option" ? CmeRowKind.Option : CmeRowKind.Future;

        var table = CmeFactSink.BuildTable(descriptor, new[] { Row(kind) });

        Assert.Equal(descriptor.Columns.Count, table.Columns.Count);

        for (var i = 0; i < descriptor.Columns.Count; i++)
        {
            Assert.Equal(descriptor.Columns[i].Name, table.Columns[i].ColumnName);
            Assert.Equal(descriptor.Columns[i].ClrType, table.Columns[i].DataType);
        }
    }

    [Fact]
    public void ValuesLandInTheColumnsTheDescriptorNames()
    {
        var table = CmeFactSink.BuildTable(CmeDescriptors.OptionTable, new[] { Row(CmeRowKind.Option) });

        var row = table.Rows[0];

        Assert.Equal("STLNYMEX", row["ExchangeCode"]);
        Assert.Equal("EOD", row["ProductCode"]);
        Assert.Equal(new DateTime(2026, 9, 4), row["TradeDate"]);
        Assert.Equal("0CJ", row["ProductSymbol"]);
        Assert.Equal("DESC", row["ProductDescription"]);
        Assert.Equal((short)2026, row["ContractYear"]);
        Assert.Equal((byte)9, row["ContractMonth"]);
        Assert.Equal("C", row["PutCall"]);
        Assert.Equal(1540m, row["Strike"]);
        Assert.Equal(2136.9m, row["High"]);
        Assert.Equal("B", row["HighABIndicator"]);
        Assert.Equal(1821.0m, row["Settle"]);
        Assert.Equal(1, row["RowOrdinal"]);

        // Unset measures must be DBNull, not CLR null or zero.
        Assert.Equal(DBNull.Value, row["Open"]);
        Assert.Equal(DBNull.Value, row["PriorInt"]);
        Assert.Equal(DBNull.Value, row["LowABIndicator"]);
    }

    /// <summary>
    /// ProductDescription is part of the OPTION key, and SQL Server promotes PK
    /// columns to NOT NULL — so a section with no description must reach the server
    /// as '' and NOT as NULL, or the whole batch fails. This is why the option
    /// table's projection is deliberately direct rather than null-coalescing, and
    /// it is the opposite of the future table's behaviour below.
    /// </summary>
    [Fact]
    public void OptionDescriptionStaysAnEmptyStringRatherThanBecomingNull()
    {
        var table = CmeFactSink.BuildTable(
            CmeDescriptors.OptionTable, new[] { Row(CmeRowKind.Option, description: string.Empty) });

        var value = table.Rows[0]["ProductDescription"];

        Assert.NotEqual(DBNull.Value, value);
        Assert.Equal(string.Empty, value);
    }

    /// <summary>
    /// A NOT NULL TVP column arriving NULL fails the whole batch with a
    /// server-side message that names the type, not the row. The sink fails first,
    /// where the offending column is known.
    /// </summary>
    [Fact]
    public void NullInARequiredColumnFailsWithTheColumnName()
    {
        var bad = new CmeFactRow
        {
            Kind = CmeRowKind.Option,
            ExchangeCode = "STLNYMEX",
            ProductCode = "EOD",
            TradeDate = new DateOnly(2026, 9, 4),
            ProductSymbol = null!, // a key column the parser must always populate
            ProductDescription = "DESC",
            ContractYear = 2026,
            ContractMonth = 9,
            PutCall = "C",
            Strike = 1m,
            RowOrdinal = 1
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CmeFactSink.BuildTable(CmeDescriptors.OptionTable, new[] { bad }));

        Assert.Contains("ProductSymbol", ex.Message);
    }

    /// <summary>
    /// The future table's description is NOT part of its key, so it is genuinely
    /// nullable there — the same empty string must be accepted.
    /// </summary>
    [Fact]
    public void FutureDescriptionMayBeNull()
    {
        var table = CmeFactSink.BuildTable(
            CmeDescriptors.FutureTable, new[] { Row(CmeRowKind.Future, description: string.Empty) });

        Assert.Equal(DBNull.Value, table.Rows[0]["ProductDescription"]);
    }
}
