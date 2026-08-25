using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// Guards the FIXTURES themselves, at the raw-file level, before any parsing is involved.
///
/// <para><b>Why this exists:</b> every mapping test above is only as strong as the traps the fixture
/// still contains. If someone re-captures, trims or "tidies" <c>Samples\*.csv</c> - dropping the
/// comma-laden address, the PM timestamps, the negative price, the <c>-</c> sentinel or the blank
/// commission - the mapping tests would keep passing while testing nothing. These assertions fail
/// instead.</para>
///
/// <para>It is also the anonymisation guard. <c>myTrades_anonymised.csv</c> must carry INVENTED
/// identities (decision D9); the two live slices are committed verbatim only because the vendor
/// anonymises the entire counterparty block on those endpoints, which this file re-proves.
/// <b>No fixture may contain a credential.</b></para>
/// </summary>
public class FixtureFactsTests
{
    [Fact]
    public void AllFourFixturesArePresentNextToTheTestAssembly()
    {
        Assert.False(string.IsNullOrWhiteSpace(Samples.AllTradesLive));
        Assert.False(string.IsNullOrWhiteSpace(Samples.SettlementsLive));
        Assert.False(string.IsNullOrWhiteSpace(Samples.MyTradesAnonymised));
        Assert.False(string.IsNullOrWhiteSpace(Samples.MyTradesHeaderOnly));
    }

    // ============================================================ structural facts

    [Fact]
    public void TheFixtureRowCountsAreTheOnesTheMappingTestsAssume()
    {
        Assert.Equal(Samples.AllTradesRowCount + 1, ModComCsv.Parse(Samples.AllTradesLive).Count);
        Assert.Equal(Samples.SettlementsRowCount + 1, ModComCsv.Parse(Samples.SettlementsLive).Count);
        Assert.Equal(Samples.MyTradesRowCount + 1, ModComCsv.Parse(Samples.MyTradesAnonymised).Count);
        Assert.Single(ModComCsv.Parse(Samples.MyTradesHeaderOnly));
    }

    [Fact]
    public void TheLiveSliceKeepsTheLiveTransportSHAPE_NoTrailingNewline()
    {
        // The live body ends WITHOUT a terminator; the settlements fixture deliberately keeps a
        // trailing newline, so the tokenizer is exercised both ways by real files.
        Assert.False(Samples.AllTradesLive.EndsWith('\n'));
        Assert.EndsWith("\n", Samples.SettlementsLive);
        Assert.EndsWith("\n", Samples.MyTradesHeaderOnly);
    }

    [Fact]
    public void NoFixtureCarriesAByteOrderMark()
    {
        // The live payload has no BOM. BOM tolerance is tested with a synthetic string instead, so
        // nothing may start depending on a BOM being present here.
        foreach (var text in new[]
                 {
                     Samples.AllTradesLive, Samples.SettlementsLive,
                     Samples.MyTradesAnonymised, Samples.MyTradesHeaderOnly
                 })
            Assert.NotEqual('﻿', text[0]);
    }

    // ============================================================ the traps must still be there

    [Fact]
    public void TheAnonymisedFixtureStillContainsACommaLadenQuotedAddress()
    {
        Assert.Contains("\"" + Samples.CommaAddress + "\"", Samples.MyTradesAnonymised);
        Assert.Equal(3, Samples.CommaAddress.Count(c => c == ','));      // 3 embedded commas

        // ... and it really does tokenize to ONE field.
        var records = ModComCsv.Parse(Samples.MyTradesAnonymised);
        Assert.Contains(records.Skip(1), r => r.Contains(Samples.CommaAddress));
        Assert.All(records, r => Assert.Equal(34, r.Length));
    }

    [Fact]
    public void TheAnonymisedFixtureStillContainsACommaInsideALegalName()
    {
        Assert.Contains("\"" + Samples.CommaLegalName + "\"", Samples.MyTradesAnonymised);
        Assert.Contains(",", Samples.CommaLegalName);
    }

    [Fact]
    public void TheFixturesStillContainPmTimestamps_IncludingTheNoonHour()
    {
        Assert.Contains(" PM\"", Samples.AllTradesLive);
        Assert.Contains(" AM\"", Samples.AllTradesLive);      // both halves of the day
        Assert.Contains("12:42:37 PM", Samples.MyTradesAnonymised);   // the subtlest case
        Assert.Equal(Samples.AllTradesPmTimestampCount,
            ModComCsv.Parse(Samples.AllTradesLive).Skip(1)
                .Count(r => r[12].EndsWith("PM", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheFixturesStillContainNegativePrices()
    {
        Assert.Contains("\"-16.65\"", Samples.AllTradesLive);
        Assert.Contains("\"-14.30\"", Samples.SettlementsLive);
        Assert.Contains("\"-0.15\"", Samples.MyTradesAnonymised);

        Assert.Equal(Samples.AllTradesNegativePriceCount,
            ModComCsv.Parse(Samples.AllTradesLive).Skip(1).Count(r => r[9].StartsWith("-", StringComparison.Ordinal)));
        Assert.Equal(Samples.SettlementsNegativePriceCount,
            ModComCsv.Parse(Samples.SettlementsLive).Skip(1).Count(r => r[8].StartsWith("-", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheSettlementsFixtureStillContainsTheDashSentinelPair_AndTheUsdDollarBasis()
    {
        Assert.Contains("\"-\",\"-\"", Samples.SettlementsLive);      // Location AND Pipeline/Terminal
        Assert.Contains("\"USD $\"", Samples.SettlementsLive);        // a space and a '$', inside a KEY

        var records = ModComCsv.Parse(Samples.SettlementsLive).Skip(1).ToList();
        Assert.Equal(Samples.SettlementsDashKeyRowCount, records.Count(r => r[2] == "-" && r[3] == "-"));
        Assert.Equal(Samples.SettlementsUsdBasisRowCount, records.Count(r => r[4] == "USD $"));
    }

    [Fact]
    public void TheAnonymisedFixtureStillContainsABlankCommissionAndAPopulatedOne()
    {
        var records = ModComCsv.Parse(Samples.MyTradesAnonymised).Skip(1).ToList();

        Assert.Equal(Samples.MyTradesBlankBidCommissionCount, records.Count(r => r[19].Length == 0));
        Assert.Equal(Samples.MyTradesBlankOfferCommissionCount, records.Count(r => r[23].Length == 0));
        Assert.Contains(records, r => r[23] == "0.01");   // a populated commission
        Assert.Contains(records, r => r[19] == "0.00");   // and a REAL zero, which is not "missing"
    }

    [Fact]
    public void TheAllTradesFixtureStillContainsTheVolumeCeilingProbeAndTheFinancialRows()
    {
        Assert.Contains("\"300000\"", Samples.AllTradesLive);   // the observed Volume maximum
        Assert.Contains("\"0.00\"", Samples.AllTradesLive);     // a real zero price

        var records = ModComCsv.Parse(Samples.AllTradesLive).Skip(1).ToList();
        Assert.Equal(Samples.AllTradesFinancialRowCount,
            records.Count(r => r[33] == "Financial" && r[3].Length == 0 && r[4].Length == 0 && r[5].Length == 0));
        Assert.Contains(records, r => r[1] == "Cancelled");     // a State that a revision flipped
        Assert.Contains(records, r => r[14] == "Spread");       // a spread group parent
        Assert.Contains(records, r => r[31] == "True");         // In Index
        Assert.Equal(records.Count, records.Count(r => r[32].Length == 0));   // Click & Trade: all blank
    }

    // ============================================================ anonymisation and secrets

    [Fact]
    public void TheAnonymisedFixtureCarriesTheINVENTEDIdentities()
    {
        // Decision D9: the live myTrades capture holds real trader names, counterparty legal entities
        // and street addresses. Those stay in the scratchpad, uncommitted; the committed fixture
        // preserves the SHAPE with invented identities. If someone replaces it with a fresh raw
        // capture, these names disappear and this test fails.
        foreach (var invented in new[]
                 {
                     "Dana Whitfield", "Robin Alcott", "Kelsey Marn", "Marta Quill",
                     "Northwind Petroleum Marketing Inc.", "Bluewater Energy Trading, LLC",
                     "Harbourline Commodities USA LLC", "Cedarpoint Refining Company"
                 })
            Assert.Contains(invented, Samples.MyTradesAnonymised);
    }

    [Fact]
    public void TheTwoLiveSlicesCarryNoCounterpartyTextAtAll()
    {
        // This is WHY they can be committed verbatim: the vendor anonymises the whole counterparty
        // block on allTrades, and settlements has no such column. Columns 16-24 and 28-31 (0-based
        // 15..23, 27..30) must be empty on every allTrades row.
        var records = ModComCsv.Parse(Samples.AllTradesLive).Skip(1).ToList();

        foreach (var record in records)
        foreach (var ordinal in new[] { 15, 16, 17, 18, 19, 20, 21, 22, 23, 26, 27, 28, 29, 30 })
            Assert.Equal(string.Empty, record[ordinal]);

        // Settlements has no counterparty column in the first place - just the 9 published names.
        Assert.Equal(ModComColumns.Settlements, ModComCsv.Parse(Samples.SettlementsLive)[0]);
    }

    [Fact]
    public void NoFixtureContainsAnythingThatLooksLikeACredential()
    {
        foreach (var text in new[]
                 {
                     Samples.AllTradesLive, Samples.SettlementsLive,
                     Samples.MyTradesAnonymised, Samples.MyTradesHeaderOnly
                 })
        {
            foreach (var forbidden in new[]
                     {
                         "Authorization", "Basic ", "Bearer ", "password", "apikey", "api_key",
                         "SEE_DB", "@modcom", "token"
                     })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoFixtureContainsAnEmailAddress()
    {
        foreach (var text in new[]
                 {
                     Samples.AllTradesLive, Samples.SettlementsLive,
                     Samples.MyTradesAnonymised, Samples.MyTradesHeaderOnly
                 })
            Assert.DoesNotContain("@", text.Replace("Enb T@S", string.Empty));   // the pipeline name is not an email
    }
}
