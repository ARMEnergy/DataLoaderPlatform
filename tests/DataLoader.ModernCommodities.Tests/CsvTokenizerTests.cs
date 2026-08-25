using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The quote-aware RFC-4180 tokenizer (<see cref="ModComCsv"/>).
///
/// <para><b>Why this matters more than it looks:</b> every field of every row of all three endpoints
/// is double-quoted, and five columns legitimately carry commas <i>inside</i> the quotes
/// (<c>Bid/Offer Legal Name</c>, <c>Bid/Offer Address</c>, <c>Contract Terms</c>). A naive
/// <c>line.Split(',')</c> on a single such row invents extra columns and shifts every later value
/// left - corrupting <c>Bid Commission</c> through <c>Product Type</c> <b>silently, on an HTTP
/// 200</b>. These tests pin the tokenizer against exactly that.</para>
/// </summary>
public class CsvTokenizerTests
{
    // ============================================================ embedded commas

    [Fact]
    public void QuotedAddressWithThreeCommas_ParsesAsExactlyONEField()
    {
        var line = $"{Samples.Q("66986")},{Samples.Q(Samples.CommaAddress)},{Samples.Q("Physical")}";

        var records = ModComCsv.Parse(line);

        var record = Assert.Single(records);
        Assert.Equal(3, record.Length);                       // three fields, NOT six
        Assert.Equal("66986", record[0]);
        Assert.Equal(Samples.CommaAddress, record[1]);        // the whole address, commas intact
        Assert.Equal("Physical", record[2]);

        // The trap, spelled out: a naive split on ',' would have produced 6 fields and shifted
        // every subsequent value left by three positions.
        Assert.Equal(6, line.Split(',').Length);
    }

    [Fact]
    public void QuotedLegalNameWithAComma_ParsesAsOneField()
    {
        var records = ModComCsv.Parse($"{Samples.Q(Samples.CommaLegalName)},{Samples.Q("USD")}");

        var record = Assert.Single(records);
        Assert.Equal(2, record.Length);
        Assert.Equal("Bluewater Energy Trading, LLC", record[0]);
        Assert.Equal("USD", record[1]);
    }

    [Fact]
    public void FullThirtyFourColumnRecordWithCommaBearingFields_StillHasExactlyThirtyFourFields()
    {
        var csv = Samples.TradesCsv(Samples.TradesRecord(
            (ModComColumns.TradeNumber, "66986"),
            (ModComColumns.BidAddress, Samples.CommaAddress),
            (ModComColumns.OfferLegalName, Samples.CommaLegalName),
            (ModComColumns.ContractTerms, "Northwind Petroleum Marketing Inc., Calgary"),
            (ModComColumns.ProductType, "Physical")));

        var records = ModComCsv.Parse(csv);

        Assert.Equal(2, records.Count);          // header + 1 data record
        Assert.Equal(34, records[0].Length);
        Assert.Equal(34, records[1].Length);     // the comma-bearing values did NOT add columns
        Assert.Equal("Physical", records[1][33]);
    }

    // ============================================================ quote escaping

    [Fact]
    public void DoubledQuotes_UnescapeToASingleLiteralQuote()
    {
        // Never observed live, which is exactly why a hand-rolled splitter is unacceptable.
        var records = ModComCsv.Parse("\"say \"\"hi\"\" now\",\"plain\"");

        var record = Assert.Single(records);
        Assert.Equal(2, record.Length);
        Assert.Equal("say \"hi\" now", record[0]);
        Assert.Equal("plain", record[1]);
    }

    [Fact]
    public void DoubledQuotesAroundAnEntireField_Unescape()
    {
        var records = ModComCsv.Parse("\"\"\"quoted\"\"\",\"x\"");

        Assert.Equal("\"quoted\"", records[0][0]);
        Assert.Equal("x", records[0][1]);
    }

    [Fact]
    public void ACommaInsideAQuotedFieldSurvivesEvenNextToAnEscapedQuote()
    {
        var records = ModComCsv.Parse("\"Acme \"\"Energy\"\", LLC\",\"y\"");

        Assert.Equal("Acme \"Energy\", LLC", records[0][0]);
        Assert.Equal("y", records[0][1]);
    }

    [Fact]
    public void AnEmbeddedNewlineInsideQuotesStaysInsideTheField()
    {
        var records = ModComCsv.Parse("\"line1\nline2\",\"z\"");

        var record = Assert.Single(records);        // ONE record, not two
        Assert.Equal("line1\nline2", record[0]);
        Assert.Equal("z", record[1]);
    }

    // ============================================================ line terminators

    [Fact]
    public void LfTerminatedRecords_AreSplitCorrectly_TheLiveTerminator()
    {
        var records = ModComCsv.Parse("\"a\",\"b\"\n\"c\",\"d\"");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "a", "b" }, records[0]);
        Assert.Equal(new[] { "c", "d" }, records[1]);
    }

    [Fact]
    public void CrLfTerminatedRecords_AreSplitCorrectly_AndTheCrIsNeverKept()
    {
        var records = ModComCsv.Parse("\"a\",\"b\"\r\n\"c\",\"d\"\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "a", "b" }, records[0]);
        Assert.Equal(new[] { "c", "d" }, records[1]);
        Assert.DoesNotContain("\r", records[0][1]);
        Assert.DoesNotContain("\r", records[1][1]);
    }

    [Fact]
    public void MixedLfAndCrLfInOnePayload_BothTerminate()
    {
        var records = ModComCsv.Parse("\"a\"\n\"b\"\r\n\"c\"");

        Assert.Equal(3, records.Count);
        Assert.Equal("a", records[0][0]);
        Assert.Equal("b", records[1][0]);
        Assert.Equal("c", records[2][0]);
    }

    [Fact]
    public void ABareCrTerminatesARecordToo()
    {
        var records = ModComCsv.Parse("\"a\"\r\"b\"");

        Assert.Equal(2, records.Count);
        Assert.Equal("a", records[0][0]);
        Assert.Equal("b", records[1][0]);
    }

    [Fact]
    public void ATrailingNewlineDoesNotYieldAPhantomRow()
    {
        Assert.Equal(2, ModComCsv.Parse("\"a\"\n\"b\"\n").Count);       // trailing LF
        Assert.Equal(2, ModComCsv.Parse("\"a\"\r\n\"b\"\r\n").Count);   // trailing CRLF
        Assert.Equal(2, ModComCsv.Parse("\"a\"\n\"b\"").Count);         // no terminator at all (live shape)
    }

    [Fact]
    public void TrulyBlankLinesAreDropped_ButAStructurallyEmptyRowSurvives()
    {
        var records = ModComCsv.Parse("\"a\"\n\n\"b\"\n");
        Assert.Equal(2, records.Count);                                  // the bare newline vanished

        // ...whereas a legitimately all-blank DATA row must NOT vanish silently.
        var structural = ModComCsv.Parse(",,,");
        var record = Assert.Single(structural);
        Assert.Equal(4, record.Length);
        Assert.All(record, f => Assert.Equal(string.Empty, f));
    }

    // ============================================================ BOM

    [Fact]
    public void ASingleLeadingUtf8Bom_IsStripped_SoTheHeaderNameStillMatchesLiterally()
    {
        // Trim() does not remove U+FEFF, so a BOM-prefixed body would otherwise defeat the
        // header[0] literal-name match and blank the whole payload.
        var records = ModComCsv.Parse("﻿" + Samples.TradesHeader);

        var header = Assert.Single(records);
        Assert.Equal("Trade Number", header[0]);
        // NOTE: an ORDINAL check. U+FEFF is a zero-weight character under culture-sensitive
        // comparison, so a substring assertion would "find" it in any string at all.
        Assert.False(header[0].Contains('﻿'));
        Assert.Equal(12, header[0].Length);

        // ... and the map built from it resolves the key column.
        var map = ModComHeaderMap.Build(header, ModComColumns.Trades);
        Assert.True(map.Has(ModComColumns.TradeNumber));
        Assert.True(map.IsClean);
    }

    [Fact]
    public void ABomBeforeAnUnquotedFieldIsAlsoStripped()
    {
        var records = ModComCsv.Parse("﻿a,b");
        Assert.Equal(new[] { "a", "b" }, records[0]);
    }

    // ============================================================ degenerate input

    [Fact]
    public void EmptyContent_YieldsNoRecords()
    {
        Assert.Empty(ModComCsv.Parse(string.Empty));
        Assert.Empty(ModComCsv.Parse("\n"));
        Assert.Empty(ModComCsv.Parse("\r\n"));
    }

    [Fact]
    public void UnterminatedQuote_DoesNotThrow_AndKeepsWhatItRead()
    {
        // A truncated response must degrade, never throw: the header/key assertions downstream are
        // what decide whether the pull is usable.
        var records = ModComCsv.Parse("\"a\",\"b");

        var record = Assert.Single(records);
        Assert.Equal(new[] { "a", "b" }, record);
    }

    // ============================================================ against the committed fixtures

    [Fact]
    public void TheRealAllTradesFixture_TokenizesTo34FieldsOnEveryRecord()
    {
        var records = ModComCsv.Parse(Samples.AllTradesLive);

        Assert.Equal(Samples.AllTradesRowCount + 1, records.Count);   // header + 9 data records
        Assert.All(records, r => Assert.Equal(34, r.Length));
    }

    [Fact]
    public void TheRealSettlementsFixture_TokenizesTo9FieldsOnEveryRecord_TrailingNewlineNotwithstanding()
    {
        var records = ModComCsv.Parse(Samples.SettlementsLive);

        Assert.Equal(Samples.SettlementsRowCount + 1, records.Count);  // header + 8, no phantom row
        Assert.All(records, r => Assert.Equal(9, r.Length));
    }

    [Fact]
    public void TheAnonymisedMyTradesFixture_TokenizesTo34FieldsDespiteItsCommaLadenAddresses()
    {
        var records = ModComCsv.Parse(Samples.MyTradesAnonymised);

        Assert.Equal(Samples.MyTradesRowCount + 1, records.Count);
        Assert.All(records, r => Assert.Equal(34, r.Length));
    }

    [Fact]
    public void TheRealHeaderOnlyFixture_TokenizesToExactlyOneRecord()
    {
        var records = ModComCsv.Parse(Samples.MyTradesHeaderOnly);

        var header = Assert.Single(records);      // the header alone - the legitimate empty read
        Assert.Equal(34, header.Length);
        Assert.Equal("Trade Number", header[0]);
        Assert.Equal("Product Type", header[33]);
    }
}
