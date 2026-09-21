using System.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// The DataTable half of the TVP contract, asserted without opening a connection.
///
/// <para><c>NgxTvpContractTests</c> proves the descriptor matches the .sql; these prove
/// the DataTable the sink actually sends matches the descriptor. Together they close the
/// loop from parsed row to server.</para>
/// </summary>
public class NgxSinkTests
{
    /// <summary>Exposes the protected BuildTable and the proc/type names.</summary>
    private sealed class ProbeSink : NgxTvpSink
    {
        public ProbeSink(NgxTableDescriptor table, ILogger? logger = null)
            : base(table, "Server=(local);Database=NGX;Integrated Security=SSPI;",
                   logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { }

        public DataTable Build(IReadOnlyList<NgxRow> rows) => BuildTable(rows);
        public string Proc => StoredProcedureName;
        public string Tvp => TableValuedParameterType;
    }

    public static TheoryData<string> TableNames() => new()
    {
        NgxDescriptors.IndexPrice.TableName,
        NgxDescriptors.StripTradingSummary.TableName
    };

    private static NgxTableDescriptor Describe(string name) =>
        NgxDescriptors.All.Single(t => t.TableName == name);

    [Theory]
    [MemberData(nameof(TableNames))]
    public void BuildTable_ColumnsMatchDescriptor_ByNameOrderAndClrType(string tableName)
    {
        var table = Describe(tableName);
        var built = new ProbeSink(table).Build(Array.Empty<NgxRow>());

        Assert.Equal(table.Columns.Count, built.Columns.Count);

        for (var i = 0; i < table.Columns.Count; i++)
        {
            Assert.Equal(table.Columns[i].Name, built.Columns[i].ColumnName);
            Assert.Equal(table.Columns[i].ClrType, built.Columns[i].DataType);
        }
    }

    [Theory]
    [MemberData(nameof(TableNames))]
    public void Sink_PointsAtTheDescribedProcAndType(string tableName)
    {
        var table = Describe(tableName);
        var sink = new ProbeSink(table);

        Assert.Equal(table.MergeProc, sink.Proc);
        Assert.Equal(table.TvpType, sink.Tvp);
    }

    /// <summary>A row of the wrong width must throw, never load shifted.</summary>
    [Fact]
    public void BuildTable_WrongRowWidth_Throws()
    {
        var table = NgxDescriptors.IndexPrice;
        var sink = new ProbeSink(table);
        var row = new NgxRow { Values = new object[table.Columns.Count - 1] };

        var ex = Assert.Throws<InvalidOperationException>(() => sink.Build(new[] { row }));
        Assert.Contains("column(s)", ex.Message);
    }

    /// <summary>
    /// An oversized string is truncated to the declared width rather than aborting the
    /// batch. SqlClient's "String or binary data would be truncated" fails the WHOLE
    /// TVP, so one long label would otherwise cost a work unit's entire window.
    /// </summary>
    [Fact]
    public void BuildTable_OversizedString_IsTruncatedNotRejected()
    {
        var table = NgxDescriptors.StripTradingSummary;
        var values = SampleStripValues();
        values[table.Ordinal("HubName")] = new string('X', 200);   // column is VARCHAR(50)

        var built = new ProbeSink(table).Build(new[] { new NgxRow { Values = values } });

        Assert.Equal(new string('X', 50), built.Rows[0][table.Ordinal("HubName")]);
    }

    /// <summary>Truncation must not mutate the caller's array — a retry re-sends these rows.</summary>
    [Fact]
    public void BuildTable_Truncation_DoesNotMutateTheSourceRow()
    {
        var table = NgxDescriptors.StripTradingSummary;
        var values = SampleStripValues();
        values[table.Ordinal("MarketName")] = new string('Y', 400);   // VARCHAR(256)

        var row = new NgxRow { Values = values };
        new ProbeSink(table).Build(new[] { row });

        Assert.Equal(400, ((string)row.Values[table.Ordinal("MarketName")]).Length);
    }

    /// <summary>A string exactly at the limit is left alone.</summary>
    [Fact]
    public void BuildTable_StringAtExactlyTheLimit_IsUntouched()
    {
        var table = NgxDescriptors.StripTradingSummary;
        var values = SampleStripValues();
        values[table.Ordinal("HubName")] = new string('Z', 50);

        var built = new ProbeSink(table).Build(new[] { new NgxRow { Values = values } });

        Assert.Equal(new string('Z', 50), built.Rows[0][table.Ordinal("HubName")]);
    }

    /// <summary>DBNull survives into the DataTable as a null, not as an empty string.</summary>
    [Fact]
    public void BuildTable_DbNull_StaysNull()
    {
        var table = NgxDescriptors.StripTradingSummary;
        var values = SampleStripValues();
        values[table.Ordinal("BrokerCompanyName")] = DBNull.Value;

        var built = new ProbeSink(table).Build(new[] { new NgxRow { Values = values } });

        Assert.Equal(DBNull.Value, built.Rows[0][table.Ordinal("BrokerCompanyName")]);
    }

    /// <summary>
    /// Real parsed rows must survive the sink unchanged — the end-to-end check from
    /// captured XML to the DataTable that would go on the wire.
    /// </summary>
    [Fact]
    public void BuildTable_AcceptsRealParsedRows()
    {
        var reader = new NgxStripReader(new HttpClient(), TestHelpers.Settings(), TestHelpers.Log);
        var rows = reader.ParseDocument(Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        var table = NgxDescriptors.StripTradingSummary;
        var built = new ProbeSink(table).Build(rows);

        Assert.Equal(2, built.Rows.Count);
        Assert.Equal(new DateTime(2026, 9, 1, 7, 37, 6), built.Rows[0][table.Ordinal("TradeDateTime")]);
        Assert.Equal(2500m, built.Rows[0][table.Ordinal("TradedVolumeAmount")]);
    }

    [Fact]
    public void BuildTable_AcceptsRealParsedIndexRows()
    {
        var reader = new NgxIndexPriceReader(new HttpClient(), TestHelpers.Settings(), TestHelpers.Log);
        var result = reader.ParsePage(Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        var table = NgxDescriptors.IndexPrice;
        var built = new ProbeSink(table).Build(result.Rows);

        Assert.Equal(2, built.Rows.Count);
        Assert.Equal(313100m, built.Rows[0][table.Ordinal("TradedAmount")]);
        Assert.Equal(DBNull.Value, built.Rows[1][table.Ordinal("TradedAmount")]);
    }

    /// <summary>A valid strip row, in descriptor order.</summary>
    private static object[] SampleStripValues()
    {
        var table = NgxDescriptors.StripTradingSummary;
        var values = new object[table.Columns.Count];

        values[table.Ordinal("TradeDateTime")] = new DateTime(2026, 9, 1, 7, 37, 6);
        values[table.Ordinal("HubId")] = 28;
        values[table.Ordinal("MarketId")] = 1;
        values[table.Ordinal("StripType")] = "Yesterday";
        values[table.Ordinal("ExchangeReference")] = "48000000003842";
        values[table.Ordinal("BeginDate")] = new DateTime(2026, 8, 31);
        values[table.Ordinal("EndDate")] = new DateTime(2026, 8, 31);
        values[table.Ordinal("HubName")] = "AB-NIT";
        values[table.Ordinal("MarketName")] = "NGX Phys, FP (CA/GJ), AB-NIT";
        values[table.Ordinal("SettlementTitle")] = "1-September-2026 (31-August-2026)";
        values[table.Ordinal("Cleared")] = true;
        values[table.Ordinal("TradedVolumeAmount")] = 2500m;
        values[table.Ordinal("TradedVolumeUnit")] = "GJ";
        values[table.Ordinal("TotalVolumeAmount")] = 2500m;
        values[table.Ordinal("TotalVolumeUnit")] = "GJ";
        values[table.Ordinal("TotalVolumeinTJ")] = 2.5m;
        values[table.Ordinal("PriceAmount")] = 1.2m;
        values[table.Ordinal("PriceCurrency")] = "CAD";
        values[table.Ordinal("BrokerCompanyName")] = DBNull.Value;
        values[table.Ordinal("RequestForQuoteIndicator")] = false;
        values[table.Ordinal("IncludeInIndexIndicator")] = true;

        return values;
    }
}
