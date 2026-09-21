using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// The parser behaviours that <see cref="NgxParseTests"/> covers only for the happy
/// path, plus the ones that are silent when wrong: partial optional blocks, every index
/// key component rather than just one, the DST transition days themselves, and the
/// <see cref="DateTimeKind"/> of what reaches a DATETIME2 column.
/// </summary>
public class NgxParseEdgeCaseTests
{
    private static NgxIndexPriceReader IndexReader(NgxSettings? settings = null) =>
        new(new HttpClient(), settings ?? TestHelpers.Settings(), TestHelpers.Log);

    private static NgxStripReader StripReader(NgxSettings? settings = null) =>
        new(new HttpClient(), settings ?? TestHelpers.Settings(), TestHelpers.Log);

    private static readonly NgxTableDescriptor Ip = NgxDescriptors.IndexPrice;
    private static readonly NgxTableDescriptor St = NgxDescriptors.StripTradingSummary;

    // ================================================== index primary key ===

    /// <summary>
    /// <c>arm.IndexPrice</c> keys on (ExecutionDate, IndexId, PriceEffectiveStart,
    /// PriceEffectiveEnd). ExecutionDate is loader-supplied, so the three that can be
    /// missing from the payload are the three below — and EACH of them must drop the
    /// record rather than merge it under a blank or defaulted key.
    ///
    /// <para><see cref="NgxParseTests.IndexPrice_RecordWithoutKey_IsDropped"/> covers
    /// only a missing <c>index/id</c>. This covers all three, in the three shapes the
    /// vendor can actually produce: absent, present-but-blank, and present-but-not the
    /// ISO format the column expects. The last is the nastiest — a date in the request's
    /// own <c>d-MMMM-yyyy</c> spelling parses as nothing at all.</para>
    ///
    /// <para>The fixture's SECOND record is left intact, so the assertion is
    /// "the bad record was dropped", not "the whole page was rejected".</para>
    /// </summary>
    [Theory]
    // index/id
    [InlineData("<id>350</id>", "")]
    [InlineData("<id>350</id>", "<id>   </id>")]
    [InlineData("<id>350</id>", "<id>not-a-number</id>")]
    // priceEffectiveStart
    [InlineData("<priceEffectiveStart>2026-09-18</priceEffectiveStart>", "")]
    [InlineData("<priceEffectiveStart>2026-09-18</priceEffectiveStart>", "<priceEffectiveStart></priceEffectiveStart>")]
    [InlineData("<priceEffectiveStart>2026-09-18</priceEffectiveStart>", "<priceEffectiveStart>18-September-2026</priceEffectiveStart>")]
    // priceEffectiveEnd
    [InlineData("<priceEffectiveEnd>2026-09-18</priceEffectiveEnd>", "")]
    [InlineData("<priceEffectiveEnd>2026-09-18</priceEffectiveEnd>", "<priceEffectiveEnd> </priceEffectiveEnd>")]
    [InlineData("<priceEffectiveEnd>2026-09-18</priceEffectiveEnd>", "<priceEffectiveEnd>2026-09-18T00:00:00</priceEffectiveEnd>")]
    public void IndexPrice_RecordMissingAnyKeyComponent_IsDropped(string find, string replace)
    {
        var position = Samples.IndexPriceXml.IndexOf(find, StringComparison.Ordinal);
        Assert.True(position >= 0, $"Fixture does not contain '{find}'.");

        var xml = string.Concat(
            Samples.IndexPriceXml.AsSpan(0, position),
            replace,
            Samples.IndexPriceXml.AsSpan(position + find.Length));

        var result = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0].Value(Ip, "IndexId"));   // the undamaged record
    }

    /// <summary>
    /// A dropped record must not take the page's envelope with it — the truncation flag
    /// and the row target still have to be reported, or the pager would stop early.
    /// </summary>
    [Fact]
    public void IndexPrice_DroppedRecord_DoesNotSuppressTheEnvelope()
    {
        var xml = Samples.IndexPriceTruncatedXml.Replace("<id>350</id>", string.Empty);

        var result = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Empty(result.Rows);
        Assert.True(result.Truncated);
        Assert.Equal(1237, result.FullListSize);

        // ...and the record still COUNTS as seen. The pager measures progress against
        // fullListSize in records the vendor emitted, not rows we kept: a dropped record
        // that did not count would make the target unreachable and abandon the window.
        Assert.Equal(1, result.RecordsSeen);
    }

    /// <summary>
    /// <c>RecordsSeen</c> counts every record element encountered, kept or dropped, so
    /// it can be compared against the vendor's own <c>fullListSize</c>.
    /// </summary>
    [Fact]
    public void IndexPrice_RecordsSeen_CountsKeptAndDroppedAlike()
    {
        var clean = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Equal(2, clean.Rows.Count);
        Assert.Equal(2, clean.RecordsSeen);

        // Break the first record's key; the count must not move.
        var damaged = IndexReader().ParsePage(
            Samples.IndexPriceXml.Replace("<id>350</id>", string.Empty).AsStream(),
            TestHelpers.IndexUnit(), "test");

        Assert.Single(damaged.Rows);
        Assert.Equal(2, damaged.RecordsSeen);
    }

    // ============================================== optional quantityTraded ===

    /// <summary>
    /// <c>quantityTraded</c> arrives WHOLE — the four columns plus
    /// <c>numberOfTrades</c> are present together or absent together. Asserted as an
    /// invariant over every parsed record rather than on one hand-picked row, so a
    /// future fixture or a vendor change that half-fills the block is caught.
    /// </summary>
    [Fact]
    public void IndexPrice_QuantityTradedAndNumberOfTrades_AreAllOrNothing()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.NotEmpty(result.Rows);

        foreach (var row in result.Rows)
        {
            var block = new[]
            {
                row.Value(Ip, "TradedAmount"), row.Value(Ip, "TradedUnit"),
                row.Value(Ip, "TradedContractUnit"), row.Value(Ip, "TradedTotalAmount"),
                row.Value(Ip, "NumberOfTrades")
            };

            var present = block.Count(v => v != DBNull.Value);

            Assert.True(
                present == 0 || present == block.Length,
                $"quantityTraded/numberOfTrades is partially populated ({present} of {block.Length}) " +
                "— the incumbent's NULL split shows the vendor emits them together or not at all.");
        }
    }

    /// <summary>
    /// If the vendor ever DOES emit a partial block, the missing sub-elements must land
    /// as NULL in their own columns — never shift the remaining values left into a
    /// neighbouring column. All four are interchangeably typed
    /// (DECIMAL, VARCHAR, VARCHAR, DECIMAL), so a shift would type-check.
    /// </summary>
    [Fact]
    public void IndexPrice_PartialQuantityTradedBlock_NullsOnlyTheMissingColumns()
    {
        // Drop two of the four sub-elements, leaving the block itself in place.
        var xml = Samples.IndexPriceXml
            .Replace("<unit>GJ</unit>", string.Empty)
            .Replace("<totalAmount>313,100</totalAmount>", string.Empty);

        Assert.DoesNotContain("<unit>GJ</unit>", xml);            // the replacements really happened
        Assert.Contains("<quantityTraded>", xml);                 // ...and the block survived

        var result = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test");
        var row = result.Rows[0];

        Assert.Equal(313100m, row.Value(Ip, "TradedAmount"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "TradedUnit"));
        Assert.Equal("Day", row.Value(Ip, "TradedContractUnit"));
        Assert.Equal(DBNull.Value, row.Value(Ip, "TradedTotalAmount"));

        // Nothing shifted: the neighbours on both sides are untouched.
        Assert.Equal(1, row.Value(Ip, "Duration"));
        Assert.Equal(27, row.Value(Ip, "NumberOfTrades"));
    }

    /// <summary>
    /// Zero is a value, not an absence. <c>Db()</c> keys on HasValue, so a genuine
    /// <c>0</c> volume must reach the column as 0 rather than NULL — "no trades" and
    /// "traded nothing" are different rows.
    /// </summary>
    [Fact]
    public void Amounts_OfZero_AreNotNull()
    {
        var xml = Samples.IndexPriceXml
            .Replace("<amount>313,100</amount>", "<amount>0</amount>")
            .Replace("<numberOfTrades>27</numberOfTrades>", "<numberOfTrades>0</numberOfTrades>");

        var row = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test").Rows[0];

        Assert.Equal(0m, row.Value(Ip, "TradedAmount"));
        Assert.Equal(0, row.Value(Ip, "NumberOfTrades"));
    }

    /// <summary>
    /// The four <c>AlternateTraded*</c> columns have no element on this endpoint, but
    /// they ARE read — so a vendor that starts emitting them is captured rather than
    /// silently discarded, and <c>arm.usp_ValidateLoad</c> can flag it.
    /// </summary>
    [Fact]
    public void IndexPrice_AlternateQuantityTraded_IsReadIfItEverAppears()
    {
        var xml = Samples.IndexPriceXml.Replace(
            "<numberOfTrades>27</numberOfTrades>",
            "<alternateQuantityTraded><amount>1,500</amount><unit>BBL</unit>" +
            "<contractUnit>Month</contractUnit><totalAmount>45,000</totalAmount>" +
            "</alternateQuantityTraded><numberOfTrades>27</numberOfTrades>");

        var row = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test").Rows[0];

        Assert.Equal(1500m, row.Value(Ip, "AlternateTradedAmount"));
        Assert.Equal("BBL", row.Value(Ip, "AlternateTradedUnit"));
        Assert.Equal("Month", row.Value(Ip, "AlternateTradedContractUnit"));
        Assert.Equal(45000m, row.Value(Ip, "AlternateTradedTotalAmount"));

        // The primary block is untouched by the alternate one.
        Assert.Equal(313100m, row.Value(Ip, "TradedAmount"));
    }

    // ========================================================== timestamps ===

    /// <summary>
    /// <c>TradeDateTime</c> and <c>LastUpdatedDate</c> reach the TVP as
    /// <see cref="DateTimeKind.Unspecified"/>. They are wall-clock readings in a known
    /// zone, not instants: tagging them Local would make them move AGAIN on a machine
    /// that is not in Central, and tagging them Utc would invite a further conversion
    /// somewhere downstream. Either forks <c>TradeDateTime</c>, which is a key column.
    /// </summary>
    [Fact]
    public void Timestamps_ReachTheRowAsUnspecifiedKind()
    {
        var strip = StripReader().ParseDocument(
            Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.All(strip, r =>
            Assert.Equal(DateTimeKind.Unspecified, ((DateTime)r.Value(St, "TradeDateTime")).Kind));

        var index = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.All(index.Rows, r =>
            Assert.Equal(DateTimeKind.Unspecified, ((DateTime)r.Value(Ip, "LastUpdatedDate")).Kind));

        // The DATE columns too — they are built from DateOnly, which is zone-free.
        Assert.All(index.Rows, r =>
        {
            var executionDate = (DateTime)r.Value(Ip, "ExecutionDate");
            Assert.Equal(DateTimeKind.Unspecified, executionDate.Kind);
            Assert.Equal(TimeSpan.Zero, executionDate.TimeOfDay);
        });
    }

    /// <summary>
    /// <b>Spring forward.</b> On 2026-03-08 both zones shift, but not at the same
    /// instant — Mountain at 09:00 UTC, Central at 08:00 UTC — so for one hour the
    /// offset between them is TWO hours, not one.
    ///
    /// <para><c>01:30-07:00</c> is 08:30 UTC. "Add an hour" gives 02:30 Central, a wall
    /// time that DOES NOT EXIST that morning. Converting through the offset gives
    /// 03:30, which is the real answer and the one the incumbent holds.</para>
    /// </summary>
    [Theory]
    [InlineData("2026-03-08T01:30:00-07:00", 2026, 3, 8, 3, 30, 0)]    // +2h across the gap
    [InlineData("2026-03-08T00:30:00-07:00", 2026, 3, 8, 1, 30, 0)]    // +1h, before either shift
    [InlineData("2026-03-08T03:30:00-06:00", 2026, 3, 8, 4, 30, 0)]    // +1h, after both shifts
    public void Timestamp_SpringForwardDay_FollowsTheOffsetNotAFixedDelta(
        string stamp, int y, int m, int d, int hh, int mm, int ss) =>
        Assert.Equal(
            new DateTime(y, m, d, hh, mm, ss),
            NgxTime.ParseCentralTimestamp(stamp));

    /// <summary>
    /// <b>Fall back.</b> The mirror image: on 2026-11-01 Central returns to standard
    /// time at 07:00 UTC and Mountain at 08:00 UTC, so between them the gap is zero for
    /// an hour.
    ///
    /// <para>⚠ The first two cases are an hour apart at the source and land on the SAME
    /// Central wall clock. That is inherent to storing Central in a DATETIME2 — the
    /// incumbent does the same — but it means two distinct trades on the fall-back night
    /// can collide on <c>TradeDateTime</c>. Harmless in practice: 01:00-02:00 Central on
    /// the first Sunday of November is a closed market (the observed trading range is
    /// 06:09-15:59 Central), and the other six key columns still have to match. Pinned
    /// here so the behaviour is a known quantity rather than a surprise.</para>
    /// </summary>
    [Theory]
    [InlineData("2026-11-01T00:30:00-06:00", 2026, 11, 1, 1, 30, 0)]   // 06:30 UTC, Central still CDT
    [InlineData("2026-11-01T01:30:00-06:00", 2026, 11, 1, 1, 30, 0)]   // 07:30 UTC, Central now CST
    [InlineData("2026-11-01T01:30:00-07:00", 2026, 11, 1, 2, 30, 0)]   // 08:30 UTC, both on standard
    public void Timestamp_FallBackDay_FollowsTheOffsetNotAFixedDelta(
        string stamp, int y, int m, int d, int hh, int mm, int ss) =>
        Assert.Equal(
            new DateTime(y, m, d, hh, mm, ss),
            NgxTime.ParseCentralTimestamp(stamp));

    /// <summary>
    /// The two DST cases as the reader actually sees them — through a real record, so
    /// the conversion is proved on the path that writes the key rather than only on the
    /// helper.
    /// </summary>
    [Theory]
    [InlineData("2026-03-08T01:30:00-07:00", 2026, 3, 8, 3, 30, 0)]
    [InlineData("2026-11-01T01:30:00-06:00", 2026, 11, 1, 1, 30, 0)]
    [InlineData("2026-06-30T23:59:59-06:00", 2026, 7, 1, 0, 59, 59)]   // rolls the DATE forward
    public void Strip_TradeDateTime_IsConvertedOnTheRecordPath(
        string stamp, int y, int m, int d, int hh, int mm, int ss)
    {
        var xml = Samples.StripWinterXml.Replace("2026-01-15T14:14:54-07:00", stamp);

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal(new DateTime(y, m, d, hh, mm, ss), rows[0].Value(St, "TradeDateTime"));
    }

    /// <summary>
    /// Fractional seconds survive the parse. No live sample carries them, but the column
    /// is DATETIME2(<b>0</b>) — SQL Server would ROUND rather than truncate on insert,
    /// moving a key by a second. Pinned so that if the vendor ever starts emitting them
    /// the behaviour is visible here rather than discovered as a duplicate row.
    /// </summary>
    [Fact]
    public void Timestamp_FractionalSeconds_AreCarriedNotSilentlyDropped() =>
        Assert.Equal(
            new DateTime(2026, 9, 1, 7, 37, 6, 500),
            NgxTime.ParseCentralTimestamp("2026-09-01T06:37:06.500-06:00"));

    /// <summary>
    /// A timestamp with NO offset is REJECTED rather than guessed at.
    ///
    /// <para>
    /// <see cref="DateTimeOffset.TryParse(string?, IFormatProvider?, System.Globalization.DateTimeStyles, out DateTimeOffset)"/>
    /// would interpret an offset-free string in the MACHINE'S LOCAL ZONE, and the
    /// conversion to Central would then start from whatever zone the server happens to
    /// sit in — so the same payload would parse differently on a UTC build agent and on
    /// a Central host. Because <c>TradeDateTime</c> is a primary key component, that
    /// does not merely mis-stamp the row, it FORKS THE KEY host by host.
    /// </para>
    /// <para>
    /// No live sample lacks an offset — the vendor stamps <c>-06:00</c>/<c>-07:00</c> on
    /// every record — so this is latent rather than live. Returning null means the
    /// reader's key check drops the record loudly instead, which is the right failure
    /// for a value that cannot be placed on the timeline.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("2026-09-01T06:37:06")]
    [InlineData("2026-09-01 06:37:06")]
    [InlineData("2026-09-01")]
    public void Timestamp_WithoutAnOffset_IsRejected(string text) =>
        Assert.Null(NgxTime.ParseCentralTimestamp(text));

    /// <summary>The offset forms the vendor does emit, and the UTC designator, are accepted.</summary>
    [Theory]
    [InlineData("2026-09-01T06:37:06-06:00", 7, 37, 6)]   // MDT, as the vendor sends it
    [InlineData("2026-01-15T14:14:54-07:00", 15, 14, 54)] // MST
    [InlineData("2026-09-01T12:37:06Z", 7, 37, 6)]        // UTC designator
    [InlineData("2026-09-01T12:37:06+00:00", 7, 37, 6)]   // explicit zero offset
    public void Timestamp_WithAnOffset_IsAccepted(string text, int h, int m, int s)
    {
        var parsed = NgxTime.ParseCentralTimestamp(text);

        Assert.NotNull(parsed);
        Assert.Equal(DateTimeKind.Unspecified, parsed!.Value.Kind);
        Assert.Equal(new TimeSpan(h, m, s), parsed.Value.TimeOfDay);
    }

    // ============================================== settlement / passthrough ===

    /// <summary>
    /// <c>settlementState</c> is stored verbatim. <c>NotSettled</c> is one word in the
    /// column — the v2 JSON API spells the same filter "Not Settled", which is NOT the
    /// stored value, so any normalisation here would diverge from the incumbent.
    /// </summary>
    [Theory]
    [InlineData("Settled")]
    [InlineData("Projected")]
    [InlineData("Pending")]
    [InlineData("NotSettled")]
    public void IndexPrice_SettlementState_IsStoredVerbatim(string state)
    {
        var xml = Samples.IndexPriceTruncatedXml.Replace(
            "<settlementState>Settled</settlementState>",
            $"<settlementState>{state}</settlementState>");

        var row = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test").Rows[0];

        Assert.Equal(state, row.Value(Ip, "SettlementState"));
    }

    /// <summary>
    /// The element is <c>totalVolumeInTJ</c> (capital I) and the column is
    /// <c>TotalVolumeinTJ</c> (lowercase i). The mismatch is the supplied DDL's and is
    /// reproduced rather than normalised, so the mapping has to be asserted explicitly —
    /// a lookup that matched the COLUMN spelling would silently null the column.
    /// </summary>
    [Fact]
    public void Strip_TotalVolumeInTJ_ElementAndColumnSpellingsDiffer()
    {
        var rows = StripReader().ParseDocument(
            Samples.StripXml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal(2.5m, rows[0].Value(St, "TotalVolumeinTJ"));

        // ...and it really is that element supplying it.
        var without = Samples.StripXml.Replace("<totalVolumeInTJ>2.5</totalVolumeInTJ>", string.Empty);
        var stripped = StripReader().ParseDocument(without.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal(DBNull.Value, stripped[0].Value(St, "TotalVolumeinTJ"));
    }

    /// <summary>
    /// The three BIT columns are nullable, and an unreadable or absent flag lands as
    /// NULL rather than false — guessing false is indistinguishable from a real false,
    /// and <c>includeInIndexIndicator</c> in particular decides whether a trade counts
    /// towards an index.
    /// </summary>
    [Fact]
    public void Strip_AbsentBooleans_AreNullNotFalse()
    {
        var xml = Samples.StripXml
            .Replace("<cleared>true</cleared>", string.Empty)
            .Replace("<requestForQuoteIndicator>false</requestForQuoteIndicator>", string.Empty)
            .Replace("<includeInIndexIndicator>true</includeInIndexIndicator>", string.Empty);

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.All(rows, r =>
        {
            Assert.Equal(DBNull.Value, r.Value(St, "Cleared"));
            Assert.Equal(DBNull.Value, r.Value(St, "RequestForQuoteIndicator"));
            Assert.Equal(DBNull.Value, r.Value(St, "IncludeInIndexIndicator"));
        });
    }

    // ================================================ streaming robustness ===

    /// <summary>
    /// <c>XNode.ReadFrom</c> leaves the reader on the NEXT node, so a record loop that
    /// also calls <c>Read()</c> drops every second sibling — half the load, reported as
    /// success. Two records cannot distinguish "drops the second" from "drops every
    /// second", so this uses five and checks the SEQUENCE, not just the count.
    /// </summary>
    [Fact]
    public void Strip_ConsecutiveRecords_AreAllKept()
    {
        var records = string.Concat(Enumerable.Range(0, 5).Select(i =>
            "<stripTradingSummary>" +
            "<hub><id>28</id><name>AB-NIT</name></hub>" +
            "<market><id>1</id><name>M</name></market>" +
            "<stripType>Yesterday</stripType>" +
            $"<tradeDateTime>2026-09-01T06:37:0{i}-06:00</tradeDateTime>" +
            $"<exchangeReference>4800000000000{i}</exchangeReference>" +
            "<beginDate>2026-08-31</beginDate><endDate>2026-08-31</endDate>" +
            "</stripTradingSummary>"));

        var xml = $"<stripTradingSummaries xmlns=\"http://www.ngx.com/Clearing\">{records}</stripTradingSummaries>";

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            Enumerable.Range(0, 5).Select(i => $"4800000000000{i}").ToArray(),
            rows.Select(r => (string)r.Value(St, "ExchangeReference")).ToArray());
    }

    /// <summary>
    /// The index feed's equivalent, which additionally has to keep the envelope scalars
    /// straight while streaming the records.
    /// </summary>
    [Fact]
    public void IndexPrice_ConsecutiveRecords_AreAllKept()
    {
        var records = string.Concat(Enumerable.Range(1, 5).Select(i =>
            "<indexPriceSummary>" +
            $"<id>{i}</id><index><id>{i}</id><name>Index {i}</name></index>" +
            $"<priceEffectiveStart>2026-09-0{i}</priceEffectiveStart>" +
            $"<priceEffectiveEnd>2026-09-0{i}</priceEffectiveEnd>" +
            "</indexPriceSummary>"));

        var xml =
            "<indexPriceList xmlns=\"http://www.ngx.com/Clearing\">" +
            "<truncated>false</truncated><fullListSize>5</fullListSize>" +
            $"<indexPrices>{records}</indexPrices></indexPriceList>";

        var result = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5 },
            result.Rows.Select(r => (int)r.Value(Ip, "IndexId")).ToArray());
    }

    /// <summary>
    /// Element lookup is by LOCAL name on the index feed too, so a vendor namespace bump
    /// degrades to "still works" rather than to every column silently going NULL behind
    /// a 200 — the strip feed already asserts this and the two readers must not drift.
    /// </summary>
    [Theory]
    [InlineData("http://www.ice.com/NgxClearing/v2")]
    [InlineData("")]
    public void IndexPrice_NamespaceChange_StillParses(string ns)
    {
        var xml = ns.Length == 0
            ? Samples.IndexPriceXml.Replace(" xmlns=\"http://www.ngx.com/Clearing\"", string.Empty)
            : Samples.IndexPriceXml.Replace("http://www.ngx.com/Clearing", ns);

        var result = IndexReader().ParsePage(xml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(350, result.Rows[0].Value(Ip, "IndexId"));
        Assert.Equal(313100m, result.Rows[0].Value(Ip, "TradedAmount"));
        Assert.Equal(2, result.FullListSize);
    }

    /// <summary>
    /// The aggregate block that trails every index response must not be mistaken for
    /// records. It carries its own <c>&lt;amount&gt;</c> elements, so a looser record
    /// match would invent rows out of the exchange's own summary figures.
    /// </summary>
    [Fact]
    public void IndexPrice_AggregateBlock_IsNotReadAsRecords()
    {
        var result = IndexReader().ParsePage(
            Samples.IndexPriceXml.AsStream(), TestHelpers.IndexUnit(), "test");

        Assert.Equal(2, result.Rows.Count);
        Assert.DoesNotContain(result.Rows, r => Equals(r.Value(Ip, "PriceAmount"), -0.0926m));
    }

    // ========================================== recycled ExchangeReference ===

    /// <summary>
    /// <c>ExchangeReference</c> is NOT a globally unique trade id — <c>48000000003842</c>
    /// was observed on 2025-05-20, 2025-08-11, 2025-11-10 (a different hub and market)
    /// and 2026-09-01. That recycling is the entire reason the primary key carries seven
    /// columns; on the reference alone these would be one row.
    /// </summary>
    [Fact]
    public void Strip_RecycledExchangeReference_IsSeparatedByTheSevenColumnKey()
    {
        const string reference = "48000000003842";

        string Record(int hub, int market, string stripType, string stamp, string begin, string end) =>
            "<stripTradingSummary>" +
            $"<hub><id>{hub}</id><name>H{hub}</name></hub>" +
            $"<market><id>{market}</id><name>M{market}</name></market>" +
            $"<stripType>{stripType}</stripType>" +
            $"<tradeDateTime>{stamp}</tradeDateTime>" +
            $"<exchangeReference>{reference}</exchangeReference>" +
            $"<beginDate>{begin}</beginDate><endDate>{end}</endDate>" +
            "</stripTradingSummary>";

        var xml =
            "<stripTradingSummaries xmlns=\"http://www.ngx.com/Clearing\">" +
            Record(28, 1, "Yesterday", "2026-09-01T06:37:06-06:00", "2026-08-31", "2026-08-31") +
            Record(28, 1, "Yesterday", "2025-11-10T06:37:06-07:00", "2025-11-09", "2025-11-09") +
            Record(31, 4, "Yesterday", "2026-09-01T06:37:06-06:00", "2026-08-31", "2026-08-31") +
            "</stripTradingSummaries>";

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(reference, r.Value(St, "ExchangeReference")));

        var keys = rows.Select(r => string.Join("|",
            r.Value(St, "HubId"), r.Value(St, "MarketId"), r.Value(St, "StripType"),
            r.Value(St, "TradeDateTime"), r.Value(St, "BeginDate"), r.Value(St, "EndDate"),
            r.Value(St, "ExchangeReference"))).ToList();

        Assert.Equal(3, keys.Distinct().Count());
    }

    /// <summary>
    /// A whitespace-only key element collapses to null and drops the record. An empty
    /// string would otherwise land as <c>''</c> in a NOT NULL VARCHAR key column, which
    /// reads as a real value and merges every such record onto one row.
    /// </summary>
    [Fact]
    public void Strip_WhitespaceOnlyKeyElement_DropsTheRecord()
    {
        var xml = Samples.StripXml.Replace(
            "<stripType>Yesterday</stripType>", "<stripType>   </stripType>", StringComparison.Ordinal);

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Empty(rows);
    }

    /// <summary>
    /// Non-key strings are trimmed, so a padded value does not reach the column with its
    /// whitespace — and an element that is only whitespace becomes NULL, not <c>''</c>.
    /// </summary>
    [Fact]
    public void Strip_NonKeyStrings_AreTrimmedAndBlanksBecomeNull()
    {
        var xml = Samples.StripXml
            .Replace("<name>AB-NIT</name>", "<name>  AB-NIT  </name>")
            .Replace(
                "<settlementTitle>1-September-2026 (31-August-2026)</settlementTitle>",
                "<settlementTitle>  </settlementTitle>");

        var rows = StripReader().ParseDocument(xml.AsStream(), TestHelpers.StripUnit(), "test");

        Assert.Equal("AB-NIT", rows[0].Value(St, "HubName"));
        Assert.Equal(DBNull.Value, rows[0].Value(St, "SettlementTitle"));
    }
}
