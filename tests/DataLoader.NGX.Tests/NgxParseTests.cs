using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// Parser tests driven by payloads captured VERBATIM from the live endpoints, with the
/// expected values taken from the incumbent <c>dbo.*</c> rows for the same keys.
///
/// <para>That pairing is the point: these assert that the new loader reproduces what
/// production already holds, not merely that the parser agrees with itself.</para>
/// </summary>
public class NgxParseTests
{
    private static NgxIndexPriceReader IndexReader(NgxSettings? settings = null) =>
        new(new HttpClient(), settings ?? TestHelpers.Settings(), TestHelpers.Log);

    private static NgxStripReader StripReader(NgxSettings? settings = null) =>
        new(new HttpClient(), settings ?? TestHelpers.Settings(), TestHelpers.Log);

    private static readonly NgxTableDescriptor Ip = NgxDescriptors.IndexPrice;
    private static readonly NgxTableDescriptor St = NgxDescriptors.StripTradingSummary;

    // ======================================================== index price ===

    /// <summary>
    /// Every column of the record whose incumbent row reads:
    /// ExecutionDate 2026-09-18, IndexId 350, Id 3086005, PES/PEE 2026-09-18,
    /// SDDS/SDDE 2026-09-18, CommodityType 'Natural Gas', PriceAmount -0.00460000 CAD,
    /// Duration 1, TradedAmount 313100, GJ, Day, TradedTotalAmount 313100,
    /// NumberOfTrades 27, SettlementState 'Settled',
    /// LastUpdatedDate 2026-09-18 03:15:41.
    /// </summary>
    [Fact]
    public void IndexPrice_FullRecord_MatchesIncumbentRow()
    {
        var unit = TestHelpers.IndexUnit();
        var result = IndexReader().ParsePage(Samples.IndexPriceXml.AsStream(), unit, "test");

        var row = result.Rows[0];

        Assert.Equal(new DateTime(2026, 9, 18), row.Value(Ip, "ExecutionDate"));
        Assert.Equal(350, row.Value(Ip, "IndexId"));
        Assert.Equal("3086005", row.Value(Ip, "Id"));
        Assert.Equal(new DateTime(2026, 9, 18), row.Value(Ip, "PriceEffectiveStart"));
        Assert.Equal(new DateTime(2026, 9, 18), row.Value(Ip, "PriceEffectiveEnd"));
        Assert.Equal(new DateTime(2026, 9, 18), row.Value(Ip, "SourceDataDeliveryStart"));
        Assert.Equal(new DateTime(2026, 9, 18), row.Value(Ip, "SourceDataDeliveryEnd"));
        Assert.Equal("Natural Gas", row.Value(Ip, "CommodityType"));
        Assert.Equal("ICE NGX AB-NIT - TCPL-Empress Transport Day Ahead Index", row.Value(Ip, "IndexName"));
        Assert.Equal(-0.0046m, row.Value(Ip, "PriceAmount"));
        Assert.Equal("CAD", row.Value(Ip, "PriceCurrency"));
        Assert.Equal(1, row.Value(Ip, "Duration"));
        Assert.Equal(313100m, row.Value(Ip, "TradedAmount"));
        Assert.Equal("GJ", row.Value(Ip, "TradedUnit"));
        Assert.Equal("Day", row.Value(Ip, "TradedContractUnit"));
        Assert.Equal(313100m, row.Value(Ip, "TradedTotalAmount"));
        Assert.Equal(27, row.Value(Ip, "NumberOfTrades"));
        Assert.Equal("Settled", row.Value(Ip, "SettlementState"));
        Assert.Equal(new DateTime(2026, 9, 18, 3, 15, 41), row.Value(Ip, "LastUpdatedDate"));
    }

    /// <summary>
    /// The four AlternateTraded* columns have no source element on this endpoint and
    /// must be NULL — the incumbent has 0 non-NULL alternates across all 2,946,561 of
    /// its Natural Gas rows.
    /// </summary>
    [Fact]
    public void IndexPrice_AlternateColumns_AreAlwaysNull()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        foreach (var row in result.Rows)
        foreach (var column in new[]
                 {
                     "AlternateTradedAmount", "AlternateTradedUnit",
                     "AlternateTradedContractUnit", "AlternateTradedTotalAmount"
                 })
            Assert.Equal(DBNull.Value, row.Value(Ip, column));
    }

    /// <summary>
    /// <c>quantityTraded</c> is optional and arrives whole. When it is absent, all four
    /// of its columns AND numberOfTrades are NULL together — never a partial row.
    /// </summary>
    [Fact]
    public void IndexPrice_WithoutQuantityTraded_LeavesTheWholeBlockNull()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        var row = result.Rows[1];   // index 1, a Month Ahead index with no traded block

        Assert.Equal(1, row.Value(Ip, "IndexId"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "TradedAmount"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "TradedUnit"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "TradedContractUnit"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "TradedTotalAmount"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "NumberOfTrades"));

        // The rest of the record still parses.
        Assert.Equal(1.3087m, row.Value(Ip, "PriceAmount"));
        Assert.Equal(31, row.Value(Ip, "Duration"));
        Assert.Equal("Projected", row.Value(Ip, "SettlementState"));
        Assert.Equal(new DateTime(2026, 12, 1), row.Value(Ip, "PriceEffectiveStart"));
        Assert.Equal(new DateTime(2026, 12, 31), row.Value(Ip, "PriceEffectiveEnd"));
    }

    /// <summary>ExecutionDate comes from the work unit, not the payload.</summary>
    [Fact]
    public void IndexPrice_ExecutionDate_IsStampedFromTheWorkUnit()
    {
        var unit = new NgxIndexPriceWorkUnit
        {
            IndexIds = new[] { 350 },
            Start = new DateOnly(2026, 6, 1),
            End = new DateOnly(2027, 3, 1),
            ExecutionDate = new DateOnly(2030, 1, 2)
        };

        var result = IndexReader().ParsePage(Samples.IndexPriceXml.AsStream(), unit, "test");

        Assert.All(result.Rows, r =>
            Assert.Equal(new DateTime(2030, 1, 2), r.Value(Ip, "ExecutionDate")));
    }

    /// <summary>CommodityType is a configured constant, since the endpoint emits none.</summary>
    [Fact]
    public void IndexPrice_CommodityType_ComesFromSettings()
    {
        var settings = TestHelpers.Settings(s => s.IndexCommodityType = "Crude Oil");
        var result = IndexReader(settings).ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.All(result.Rows, r => Assert.Equal("Crude Oil", r.Value(Ip, "CommodityType")));
    }

    /// <summary>
    /// The truncation flag must be surfaced, because it is the ONLY signal that a
    /// response is incomplete — the status is 200 and the body is well-formed either
    /// way.
    /// </summary>
    [Fact]
    public void IndexPrice_TruncatedResponse_IsDetected()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceTruncatedXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.True(result.Truncated);
        Assert.Equal(1237, result.FullListSize);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void IndexPrice_CompleteResponse_IsNotFlaggedTruncated()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.False(result.Truncated);
        Assert.Equal(2, result.FullListSize);
        Assert.Equal(2, result.Rows.Count);
    }

    /// <summary>An empty window is a legitimate empty read, not an error.</summary>
    [Fact]
    public void IndexPrice_EmptyWindow_ParsesToZeroRowsWithoutThrowing()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceEmptyXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// An HTML login page must NOT read as an empty result. This is the failure mode
    /// that would otherwise turn an expired password into months of silent no-ops.
    /// </summary>
    [Fact]
    public void IndexPrice_SsoLoginHtml_Throws()
    {
        var ex = Assert.Throws<NgxMalformedResponseException>(() =>
            IndexReader().ParsePage(Samples.SsoLoginHtml.AsStream(), TestHelpers.IndexUnit(), "test"));

        // The message must point at the real cause — credentials — not at a DTD
        // complaint from the XML reader, which is what this path raises underneath.
        Assert.Contains("credentials", ex.Message);
    }

    /// <summary>A record missing a key component is dropped, never merged under a blank key.</summary>
    [Fact]
    public void IndexPrice_RecordWithoutKey_IsDropped()
    {
        const string xml =
            """
            <indexPriceList xmlns="http://www.ngx.com/Clearing">
              <truncated>false</truncated><fullListSize>1</fullListSize>
              <indexPrices>
                <indexPriceSummary>
                  <id>1</id>
                  <index><name>No id here</name></index>
                  <priceEffectiveStart>2026-09-18</priceEffectiveStart>
                  <priceEffectiveEnd>2026-09-18</priceEffectiveEnd>
                </indexPriceSummary>
              </indexPrices>
            </indexPriceList>
            """;

        var result = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test");
        Assert.Empty(result.Rows);
    }

    /// <summary>Row width must equal the descriptor, or the TVP would bind shifted.</summary>
    [Fact]
    public void IndexPrice_RowWidth_MatchesDescriptor()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.All(result.Rows, r => Assert.Equal(Ip.Columns.Count, r.Values.Length));
    }

    // ================================================= strip trading summary ===

    /// <summary>
    /// Every column of the trade whose incumbent row reads:
    /// TradeDateTime 2026-09-01 07:37:06, HubId 28, MarketId 1, StripType 'Yesterday',
    /// ExchangeReference 48000000003842, Begin/EndDate 2026-08-31, Cleared true,
    /// TradedVolumeAmount 2500 GJ, TotalVolumeAmount 2500 GJ, TotalVolumeinTJ 2.5,
    /// PriceAmount 1.20000000 CAD, RFQ false, IncludeInIndex true.
    /// </summary>
    [Fact]
    public void Strip_FullRecord_MatchesIncumbentRow()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        var row = rows[0];

        Assert.Equal(new DateTime(2026, 9, 1, 7, 37, 6), row.Value(St, "TradeDateTime"));
        Assert.Equal(28, row.Value(St, "HubId"));
        Assert.Equal(1, row.Value(St, "MarketId"));
        Assert.Equal("Yesterday", row.Value(St, "StripType"));
        Assert.Equal("48000000003842", row.Value(St, "ExchangeReference"));
        Assert.Equal(new DateTime(2026, 8, 31), row.Value(St, "BeginDate"));
        Assert.Equal(new DateTime(2026, 8, 31), row.Value(St, "EndDate"));
        Assert.Equal("AB-NIT", row.Value(St, "HubName"));
        Assert.Equal("NGX Phys, FP (CA/GJ), AB-NIT", row.Value(St, "MarketName"));
        Assert.Equal("1-September-2026 (31-August-2026)", row.Value(St, "SettlementTitle"));
        Assert.Equal(true, row.Value(St, "Cleared"));
        Assert.Equal(2500m, row.Value(St, "TradedVolumeAmount"));
        Assert.Equal("GJ", row.Value(St, "TradedVolumeUnit"));
        Assert.Equal(2500m, row.Value(St, "TotalVolumeAmount"));
        Assert.Equal("GJ", row.Value(St, "TotalVolumeUnit"));
        Assert.Equal(2.5m, row.Value(St, "TotalVolumeinTJ"));
        Assert.Equal(1.2m, row.Value(St, "PriceAmount"));
        Assert.Equal("CAD", row.Value(St, "PriceCurrency"));
        Assert.Equal(false, row.Value(St, "RequestForQuoteIndicator"));
        Assert.Equal(true, row.Value(St, "IncludeInIndexIndicator"));
    }

    /// <summary>
    /// The winter record, whose incumbent row reads TradeDateTime 2026-01-15 15:14:54 —
    /// the DST half of the timestamp contract, exercised through the real parser rather
    /// than only through NgxTime.
    /// </summary>
    [Fact]
    public void Strip_WinterRecord_ConvertsAcrossTheDstBoundary()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripWinterXml.AsStream(),
            TestHelpers.StripUnit(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15)), "test");

        var row = rows[0];

        Assert.Equal(new DateTime(2026, 1, 15, 15, 14, 54), row.Value(St, "TradeDateTime"));
        Assert.Equal("48000000007314", row.Value(St, "ExchangeReference"));
        Assert.Equal("Summer Block", row.Value(St, "StripType"));

        // Thousands separators again, on a volume this time.
        Assert.Equal(5000m, row.Value(St, "TradedVolumeAmount"));
        Assert.Equal(1070000m, row.Value(St, "TotalVolumeAmount"));
    }

    /// <summary>
    /// BrokerCompanyName is absent from current responses and must land as NULL rather
    /// than an empty string — the column means "no broker", not "a broker with no name".
    /// </summary>
    [Fact]
    public void Strip_AbsentBrokerCompanyName_IsNull()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.All(rows, r => Assert.Equal(DBNull.Value, r.Value(St, "BrokerCompanyName")));
    }

    /// <summary>...but it is read, not hard-coded, so it works the day it reappears.</summary>
    [Fact]
    public void Strip_PresentBrokerCompanyName_IsRead()
    {
        var xml = Samples.StripXml.Replace(
            "<cleared>true</cleared>",
            "<cleared>true</cleared><brokerCompanyName>CalRock Brokers Inc.</brokerCompanyName>");

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal("CalRock Brokers Inc.", rows[0].Value(St, "BrokerCompanyName"));
    }

    [Fact]
    public void Strip_EmptyDay_ParsesToZeroRowsWithoutThrowing()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripEmptyXml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Empty(rows);
    }

    [Fact]
    public void Strip_SsoLoginHtml_Throws() =>
        Assert.Throws<NgxMalformedResponseException>(() =>
            StripReader().ParseDocument(Samples.SsoLoginHtml.AsStream(), TestHelpers.StripUnit(), "test"));

    /// <summary>
    /// All seven key components are required. ExchangeReference alone is NOT a key — the
    /// vendor recycles it — so a record missing hub, market, strip type, trade time or
    /// either delivery date must be dropped rather than collide with a real trade.
    /// </summary>
    [Theory]
    [InlineData("<hub><id>28</id><name>AB-NIT</name></hub>", "<hub><name>AB-NIT</name></hub>")]
    [InlineData("<market><id>1</id>", "<market>")]
    [InlineData("<stripType>Yesterday</stripType>", "")]
    [InlineData("<tradeDateTime>2026-09-01T06:37:06-06:00</tradeDateTime>", "")]
    [InlineData("<exchangeReference>48000000003842</exchangeReference>", "")]
    [InlineData("<beginDate>2026-08-31</beginDate>", "")]
    [InlineData("<endDate>2026-08-31</endDate>", "")]
    public void Strip_RecordMissingAnyKeyComponent_IsDropped(string find, string replace)
    {
        // Only the FIRST record is damaged; the second must still load.
        var index = Samples.StripXml.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Fixture does not contain '{find}'.");

        var xml = string.Concat(
            Samples.StripXml.AsSpan(0, index),
            replace,
            Samples.StripXml.AsSpan(index + find.Length));

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Single(rows);
        Assert.Equal("48000000003846", rows[0].Value(St, "ExchangeReference"));
    }

    [Fact]
    public void Strip_RowWidth_MatchesDescriptor()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.All(rows, r => Assert.Equal(St.Columns.Count, r.Values.Length));
    }

    /// <summary>
    /// Element lookup is by LOCAL name, so a vendor namespace change degrades to "still
    /// works" rather than to every column silently going NULL behind a 200.
    /// </summary>
    [Fact]
    public void Strip_DifferentNamespace_StillParses()
    {
        var xml = Samples.StripXml.Replace(
            "http://www.ngx.com/Clearing", "http://www.ice.com/NgxClearing/v2");

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTime(2026, 9, 1, 7, 37, 6), rows[0].Value(St, "TradeDateTime"));
    }

    /// <summary>
    /// Both feeds' keys were verified unique within a live response (1,237/1,237 index
    /// keys, 1,601/1,601 strip keys). This pins the fixture's own uniqueness so a future
    /// fixture edit cannot quietly introduce the duplicate the merge de-dup would then
    /// hide.
    /// </summary>
    [Fact]
    public void Strip_KeysAreUniqueWithinAResponse()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        var keys = rows.Select(r => string.Join("|",
            r.Value(St, "HubId"), r.Value(St, "MarketId"), r.Value(St, "StripType"),
            r.Value(St, "TradeDateTime"), r.Value(St, "BeginDate"), r.Value(St, "EndDate"),
            r.Value(St, "ExchangeReference")));

        Assert.Equal(rows.Count, keys.Distinct().Count());
    }
}
