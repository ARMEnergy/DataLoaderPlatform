using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The <c>header name -&gt; field index</c> map (<see cref="ModComHeaderMap"/>).
///
/// <para><b>24 of the 34 trades headers contain a space, two contain an ampersand and one contains a
/// slash</b> - <c>Pipeline/Terminal</c>, <c>GT&amp;C</c>, <c>Click &amp; Trade</c>. <b>No
/// <c>System.Text.Json</c> or CSV naming policy matches those</b>: a property called
/// <c>PipelineTerminal</c> / <c>GTandC</c> / <c>ClickAndTrade</c> binds to <b>nothing</b>, which is
/// exactly the trap that produced a silently all-NULL table for NGI. These tests pin that every
/// field is resolved by <b>literal vendor name</b>, that binding is by NAME and not by position (a
/// reordered header still maps), and that a casing-only vendor change cannot blank all 34 columns at
/// once.</para>
/// </summary>
public class HeaderMapTests
{
    private static string[] LiveTradesHeader() => ModComCsv.Parse(Samples.TradesHeader).Single();
    private static string[] LiveSettlementsHeader() => ModComCsv.Parse(Samples.SettlementsHeader).Single();

    private static ModComHeaderMap TradesMap(string[]? header = null) =>
        ModComHeaderMap.Build(header ?? LiveTradesHeader(), ModComColumns.Trades);

    // ============================================================ the literal names

    [Fact]
    public void TheLiveTradesHeader_MapsAll34ColumnsCleanly_InCsvOrder()
    {
        var map = TradesMap();

        Assert.True(map.IsClean);
        Assert.Equal(34, map.FieldCount);
        Assert.Empty(map.MissingExpected);
        Assert.Empty(map.Unexpected);
        Assert.Equal(string.Empty, map.DriftNote(ModComColumns.Trades.Length));

        for (var i = 0; i < ModComColumns.Trades.Length; i++)
            Assert.Equal(i, map.IndexOf(ModComColumns.Trades[i]));
    }

    [Fact]
    public void TheAwkwardNames_ResolveByTheirLITERALVendorSpelling()
    {
        var map = TradesMap();

        // The three names no naming policy can produce.
        Assert.Equal(4, map.IndexOf("Pipeline/Terminal"));
        Assert.Equal(29, map.IndexOf("GT&C"));
        Assert.Equal(32, map.IndexOf("Click & Trade"));

        // ... and the constants used by the reader are exactly those literals.
        Assert.Equal("Pipeline/Terminal", ModComColumns.PipelineTerminal);
        Assert.Equal("GT&C", ModComColumns.GTandC);
        Assert.Equal("Click & Trade", ModComColumns.ClickAndTrade);

        // A "tidied" name binds to NOTHING - the whole point of the literal-name rule.
        Assert.False(map.Has("PipelineTerminal"));
        Assert.False(map.Has("pipeline_terminal"));
        Assert.False(map.Has("pipelineTerminal"));
        Assert.False(map.Has("GTandC"));
        Assert.False(map.Has("ClickAndTrade"));
    }

    [Fact]
    public void EverySpaceBearingName_ResolvesToo()
    {
        var map = TradesMap();

        foreach (var name in new[]
                 {
                     "Trade Number", "Price Basis", "Term Start", "Term End", "Unit of Measure",
                     "Executed Timestamp", "Last Updated Timestamp", "Trade Type", "Bid Trader",
                     "Bid Legal Name", "Bid Address", "Bid Commission", "Offer Trader",
                     "Offer Legal Name", "Offer Address", "Offer Commission", "Spread Trade Number",
                     "Apportionment Protected", "Clearing ID", "Settlement Currency",
                     "Contract Terms", "In Index", "Product Type"
                 })
            Assert.True(map.Has(name), $"'{name}' did not resolve");
    }

    [Fact]
    public void TheLiveSettlementsHeader_MapsAll9ColumnsCleanly()
    {
        var map = ModComHeaderMap.Build(LiveSettlementsHeader(), ModComColumns.Settlements);

        Assert.True(map.IsClean);
        Assert.Equal(9, map.FieldCount);
        for (var i = 0; i < ModComColumns.Settlements.Length; i++)
            Assert.Equal(i, map.IndexOf(ModComColumns.Settlements[i]));

        // The SOURCE header spells it 'Pipeline/Terminal' on this endpoint too - only the TARGET
        // column is spelled PieplineTerminal [sic].
        Assert.Equal(3, map.IndexOf("Pipeline/Terminal"));
        Assert.False(map.Has("PieplineTerminal"));
    }

    // ============================================================ binding is by NAME, not position

    [Fact]
    public void AReorderedHeader_StillMapsEveryColumnCorrectly()
    {
        // Reverse the vendor order completely. If anything bound by ordinal, every value would be
        // read from the wrong field; because binding is by name, every value still lands right.
        var reordered = ModComColumns.Trades.Reverse().ToArray();
        var map = ModComHeaderMap.Build(reordered, ModComColumns.Trades);

        Assert.True(map.IsClean);
        Assert.Equal(33, map.IndexOf(ModComColumns.TradeNumber));   // was 0, now last
        Assert.Equal(0, map.IndexOf(ModComColumns.ProductType));    // was 33, now first
        Assert.Equal(28, map.IndexOf(ModComColumns.PriceBasis));    // was 5, now 33 - 5

        // Build a data record in the SAME reordered layout and read it back by name.
        var record = reordered
            .Select(name => name == ModComColumns.TradeNumber ? "68043"
                : name == ModComColumns.PipelineTerminal ? "Husky"
                : name == ModComColumns.GTandC ? "MASTER 2017"
                : name == ModComColumns.ClickAndTrade ? "True"
                : string.Empty)
            .ToArray();

        Assert.Equal("68043", map.Raw(record, ModComColumns.TradeNumber));
        Assert.Equal("Husky", map.Raw(record, ModComColumns.PipelineTerminal));
        Assert.Equal("MASTER 2017", map.Raw(record, ModComColumns.GTandC));
        Assert.Equal("True", map.Raw(record, ModComColumns.ClickAndTrade));
    }

    [Fact]
    public void ACasingOnlyChange_StillMaps_BecauseTheComparerIsOrdinalIgnoreCase()
    {
        // Deliberately one notch more tolerant than an exact ordinal match: a casing-only vendor
        // change must not blank all 34 columns at once. The NAMES themselves stay literal.
        var shouty = ModComColumns.Trades.Select(n => n.ToUpperInvariant()).ToArray();
        var map = ModComHeaderMap.Build(shouty, ModComColumns.Trades);

        Assert.True(map.IsClean);
        Assert.Empty(map.MissingExpected);
        Assert.Equal(0, map.IndexOf(ModComColumns.TradeNumber));
        Assert.Equal(4, map.IndexOf(ModComColumns.PipelineTerminal));   // "PIPELINE/TERMINAL"
        Assert.Equal(32, map.IndexOf(ModComColumns.ClickAndTrade));     // "CLICK & TRADE"
    }

    [Fact]
    public void SurroundingWhitespaceInAHeaderNameIsTrimmed()
    {
        var padded = ModComColumns.Trades.Select(n => "  " + n + " ").ToArray();
        var map = ModComHeaderMap.Build(padded, ModComColumns.Trades);

        Assert.True(map.IsClean);
        Assert.Equal(13, map.IndexOf(ModComColumns.LastUpdatedTimestamp));
    }

    // ============================================================ drift classification

    [Fact]
    public void AMissingNonKeyColumn_IsReported_AndReadsAsNullForEveryRow()
    {
        var header = ModComColumns.Trades.Where(n => n != ModComColumns.Notes).ToArray();
        var map = ModComHeaderMap.Build(header, ModComColumns.Trades);

        Assert.False(map.IsClean);
        Assert.Equal(new[] { ModComColumns.Notes }, map.MissingExpected);
        Assert.Empty(map.Unexpected);
        Assert.False(map.Has(ModComColumns.Notes));
        Assert.Equal(-1, map.IndexOf(ModComColumns.Notes));
        Assert.Null(map.Raw(new string[33], ModComColumns.Notes));

        var note = map.DriftNote(ModComColumns.Trades.Length);
        Assert.Contains("HeaderDrift", note);
        Assert.Contains("columnCount=33 (expected 34)", note);
        Assert.Contains("missing=[Notes]", note);
    }

    [Fact]
    public void AnUnexpectedExtraColumn_IsReportedAndIgnored()
    {
        var header = ModComColumns.Trades.Concat(new[] { "Brand New Column" }).ToArray();
        var map = ModComHeaderMap.Build(header, ModComColumns.Trades);

        Assert.False(map.IsClean);
        Assert.Empty(map.MissingExpected);          // the 34 known columns still all map
        Assert.Equal(new[] { "Brand New Column" }, map.Unexpected);
        Assert.Equal(0, map.IndexOf(ModComColumns.TradeNumber));
        Assert.Contains("unexpected=[Brand New Column]", map.DriftNote(ModComColumns.Trades.Length));
    }

    [Fact]
    public void ADuplicateHeaderName_KeepsTheFirstOccurrence_AndReportsTheDuplicate()
    {
        // A vendor emitting a column twice must not silently swap which one is read.
        var header = ModComColumns.Trades.Concat(new[] { ModComColumns.Price }).ToArray();
        var map = ModComHeaderMap.Build(header, ModComColumns.Trades);

        Assert.Equal(9, map.IndexOf(ModComColumns.Price));   // the FIRST Price, not the appended one
        Assert.Contains(map.Unexpected, u => u.Contains("duplicate at position 35"));
    }

    [Fact]
    public void AShortDataRecord_DegradesItsTailToNull_RatherThanThrowing()
    {
        var map = TradesMap();
        var shortRecord = new[] { "68043", "Finalized" };   // only 2 of 34 fields

        Assert.Equal("68043", map.Raw(shortRecord, ModComColumns.TradeNumber));
        Assert.Equal("Finalized", map.Raw(shortRecord, ModComColumns.State));
        Assert.Null(map.Raw(shortRecord, ModComColumns.ProductType));   // no IndexOutOfRangeException
        Assert.Null(map.Raw(shortRecord, ModComColumns.Price));
    }

    [Fact]
    public void ABlankHeaderNameIsSkipped_NotMappedToAnEmptyKey()
    {
        var header = new[] { "Trade Number", "   ", "Product" };
        var map = ModComHeaderMap.Build(header, ModComColumns.Trades);

        Assert.Equal(0, map.IndexOf("Trade Number"));
        Assert.Equal(2, map.IndexOf("Product"));
        Assert.False(map.Has(string.Empty));
        Assert.False(map.Has("   "));
    }

    [Fact]
    public void DriftNote_IsEmptyOnlyWhenTheHeaderIsCleanAndTheCountMatches()
    {
        Assert.Equal(string.Empty, TradesMap().DriftNote(34));

        // Same 34 names, but the caller expected a different count -> the note fires.
        Assert.NotEqual(string.Empty, TradesMap().DriftNote(33));
    }
}
