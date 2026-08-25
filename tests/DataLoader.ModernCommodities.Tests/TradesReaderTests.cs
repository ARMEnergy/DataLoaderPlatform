using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// End-to-end mapping for the ONE trades reader that serves BOTH trades endpoints
/// (<see cref="ModComTradesSourceReader"/>), driven from the committed fixtures over a fake HTTP
/// handler and a fake <c>arm.FileLog</c>.
///
/// <para>The per-field pins here are the guard against a <b>one-position shift</b>: the columns
/// <c>Location</c> / <c>Pipeline/Terminal</c> / <c>Price Basis</c> are adjacent, all text, and all
/// blank on exactly the <c>Financial</c> rows - a mapping that read them off by one would still
/// "parse 9 rows" and still throw nothing.</para>
/// </summary>
public class TradesReaderTests
{
    private static TradeRow ByNumber(IReadOnlyList<TradeRow> rows, int tradeNumber) =>
        rows.Single(r => r.TradeNumber == tradeNumber);

    // ============================================================ the real allTrades fixture

    [Fact]
    public async Task TheLiveAllTradesFixture_ParsesToEveryRowWithNoDrops()
    {
        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, Samples.AllTradesLive);

        Assert.Equal(Samples.AllTradesRowCount, result.Rows.Count);
        Assert.Equal(
            new[] { 67989, 68004, 68020, 68028, 68029, 68030, 68031, 68041, 68043 },
            result.Rows.Select(r => r.TradeNumber).OrderBy(n => n).ToArray());

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(Samples.AllTradesRowCount, call.RowCount);
        Assert.Equal(0, call.DroppedRowCount);
        Assert.Null(call.ErrorMessage);                  // a clean header leaves no drift note
        Assert.Empty(result.Log.OfLevel(LogLevel.Error));
    }

    [Fact]
    public async Task ALiveRow_IsPinnedFieldByField_IncludingTheThreeAdjacentTextColumns()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);
        var r = ByNumber(rows, 68043);

        Assert.Equal("Finalized", r.State);
        Assert.Equal("WCS", r.Product);
        Assert.Equal("Hardisty", r.Location);              // a one-position shift would swap
        Assert.Equal("Husky", r.PipelineTerminal);         // these three...
        Assert.Equal("WTI CMA", r.PriceBasis);             // ...and throw nothing
        Assert.Equal("SEP-26", r.Term);
        Assert.Equal(new DateOnly(2026, 9, 1), r.TermStart);
        Assert.Equal(new DateOnly(2026, 9, 30), r.TermEnd);
        Assert.Equal(-16.65m, r.Price);                    // negative: a differential, not an error
        Assert.Equal(10000m, r.Volume);
        Assert.Equal("m3/month", r.UnitOfMeasure);
        Assert.Equal(new DateTime(2026, 8, 24, 13, 44, 41), r.Executed);      // 01:44:41 PM
        Assert.Equal(new DateTime(2026, 8, 24, 13, 54, 34), r.LastUpdated);   // 01:54:34 PM
        Assert.Equal("Outright", r.TradeType);
        Assert.False(r.ApportionmentProtected);            // the literal "False"
        Assert.False(r.InIndex);
        Assert.Null(r.ClickAndTrade);                      // blank -> NULL, NOT false
        Assert.Equal("Physical", r.ProductType);

        // The 14 anonymised columns arrive EMPTY from this endpoint - correct, not a defect.
        Assert.Null(r.Side);
        Assert.Null(r.BidTrader);
        Assert.Null(r.BidLegalName);
        Assert.Null(r.BidAddress);
        Assert.Null(r.BidCommission);
        Assert.Null(r.OfferTrader);
        Assert.Null(r.OfferLegalName);
        Assert.Null(r.OfferAddress);
        Assert.Null(r.OfferCommission);
        Assert.Null(r.SettlementCurrency);
        Assert.Null(r.ContractTerms);
        Assert.Null(r.GTandC);
        Assert.Null(r.Notes);
        Assert.Null(r.ClearingID);                         // 100% NULL on BOTH endpoints
    }

    [Fact]
    public async Task TheThreeHundredThousandVolumeRow_ParsesExactly_AndZeroPriceIsARealValue()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);
        var r = ByNumber(rows, 68020);

        Assert.Equal(300000m, r.Volume);                                     // the observed maximum
        Assert.Equal(0.00m, r.Price);                                        // 0.00 is REAL, not missing
        Assert.NotNull(r.Price);
        Assert.Equal("ARGUS WCS Cushing Diff + WTI CMA", r.PriceBasis);      // free text, 31 chars
        Assert.Equal("bbls/month", r.UnitOfMeasure);
        Assert.Equal(new DateTime(2026, 8, 21, 13, 48, 9), r.Executed);
    }

    [Fact]
    public async Task TheFinancialRows_HaveNullLocationPipelineAndBasis_Semantically()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);

        var financial = rows.Where(r => r.ProductType == "Financial").ToList();
        Assert.Equal(Samples.AllTradesFinancialRowCount, financial.Count);
        Assert.All(financial, r =>
        {
            Assert.Null(r.Location);            // a financial contract has no delivery location
            Assert.Null(r.PipelineTerminal);
            Assert.Null(r.PriceBasis);
            Assert.NotNull(r.Product);          // ... but the product is still there
            Assert.NotNull(r.Price);
        });

        // The physical rows are the complement, and they DO carry all three.
        Assert.All(rows.Where(r => r.ProductType == "Physical"), r =>
        {
            Assert.NotNull(r.Location);
            Assert.NotNull(r.PipelineTerminal);
            Assert.NotNull(r.PriceBasis);
        });
    }

    [Fact]
    public async Task TheQuarterAndTildeTermShapes_ArePersistedAsPublished_NeverParsed()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);

        Assert.Equal("1Q-27", ByNumber(rows, 68004).Term);
        Assert.Equal("OCT-26~NOV-26", ByNumber(rows, 67989).Term);
        Assert.Equal("SSP/SYN", ByNumber(rows, 68029).Product);          // slash-joined on a spread
        Assert.Equal("Edmonton/Edmonton", ByNumber(rows, 68029).Location);
        Assert.Equal("AOSPL/Enb T@S", ByNumber(rows, 68029).PipelineTerminal);
    }

    [Fact]
    public async Task EveryAfternoonRowInTheFixture_LandsInTheAfternoon()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);

        var pm = rows.Where(r => r.Executed!.Value.Hour >= 12).ToList();
        Assert.Equal(Samples.AllTradesPmTimestampCount, pm.Count);
        Assert.All(pm, r => Assert.True(r.Executed!.Value.Hour >= 12));

        // ... and no row lost its 12 hours: every Executed/LastUpdated parsed.
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.Executed);
            Assert.NotNull(r.LastUpdated);
            Assert.True(r.LastUpdated >= r.Executed);   // a trade is updated no earlier than executed
        });
    }

    [Fact]
    public async Task TheSpreadGroup_KeepsItsSpreadTradeNumberAsTEXT_AndIsNeverReconciled()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);
        var group = rows.Where(r => r.SpreadTradeNumber == "68029").ToList();

        Assert.Equal(3, group.Count);                                   // parent + two legs, as published
        Assert.Equal(new[] { "First Leg", "Second Leg", "Spread" },
            group.Select(r => r.TradeType!).OrderBy(t => t).ToArray());

        // Volume is NOT additive across a spread group and Price is a differential - the loader
        // stores them verbatim and reconciles nothing.
        Assert.All(group, r => Assert.Equal(20000m, r.Volume));
        Assert.Equal(2.25m, ByNumber(rows, 68029).Price);
    }

    [Fact]
    public async Task InIndexIsTrueOnExactlyTheExpectedRow_AndClickAndTradeIsNullEverywhere()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);

        Assert.Equal(Samples.AllTradesInIndexTrueCount, rows.Count(r => r.InIndex == true));
        Assert.True(ByNumber(rows, 68028).InIndex);
        Assert.Equal(rows.Count, rows.Count(r => r.ClickAndTrade is null));      // anonymised -> unknown
        Assert.Equal(rows.Count, rows.Count(r => r.ApportionmentProtected == false));
    }

    [Fact]
    public async Task ACancelledStateSurvives_BecauseARevisionCanFlipIt()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.AllTradesLive);
        Assert.Equal("Cancelled", ByNumber(rows, 68041).State);
    }

    // ============================================================ the anonymised myTrades fixture

    [Fact]
    public async Task TheAnonymisedMyTradesFixture_ParsesAllFiveRowsWithTheirCounterpartyBlock()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.MyTradesAnonymised, ModComDescriptors.MyTrades);

        Assert.Equal(Samples.MyTradesRowCount, rows.Count);
        var r = ByNumber(rows, 66986);

        Assert.Equal("Sell", r.Side);
        Assert.Equal("Dana Whitfield", r.BidTrader);
        Assert.Equal("Northwind Petroleum Marketing Inc.", r.BidLegalName);
        Assert.Equal(Samples.CommaAddress, r.BidAddress);              // 3 embedded commas, ONE field
        Assert.Null(r.BidCommission);                                  // blank -> NULL, never 0
        Assert.Equal("Robin Alcott", r.OfferTrader);
        Assert.Equal(Samples.CommaLegalName, r.OfferLegalName);        // a comma inside a legal name
        Assert.Equal("4500 Beechwood Parkway, 12th Floor, Houston, TX 77099", r.OfferAddress);
        Assert.Equal(0.01m, r.OfferCommission);
        Assert.Equal("USD", r.SettlementCurrency);
        Assert.Equal("Northwind Petroleum Marketing Inc.", r.ContractTerms);
        Assert.Equal("MASTER 1998 with 2011 Amends", r.GTandC);        // header 'GT&C'
        Assert.True(r.InIndex);
        Assert.False(r.ClickAndTrade);                                 // header 'Click & Trade'
        Assert.Null(r.ClearingID);                                     // never populated by either endpoint
        Assert.Equal(new DateTime(2026, 8, 6, 13, 49, 33), r.Executed);
        Assert.Equal(new DateTime(2026, 8, 6, 13, 50, 46), r.LastUpdated);
    }

    [Fact]
    public async Task TheNoonHourRowsParseToTwelve_NotToZero()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.MyTradesAnonymised, ModComDescriptors.MyTrades);

        var noon = rows.Where(r => r.Executed == new DateTime(2026, 8, 6, 12, 42, 37)).ToList();
        Assert.Equal(Samples.MyTradesNoonHourTimestampCount, noon.Count);
        Assert.All(noon, r => Assert.Equal(new DateTime(2026, 8, 6, 12, 44, 27), r.LastUpdated));
    }

    [Fact]
    public async Task TheCommissionColumnsAreComplementary_NeverBothPresent()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.MyTradesAnonymised, ModComDescriptors.MyTrades);

        Assert.Equal(Samples.MyTradesBlankBidCommissionCount, rows.Count(r => r.BidCommission is null));
        Assert.Equal(Samples.MyTradesBlankOfferCommissionCount, rows.Count(r => r.OfferCommission is null));
        Assert.All(rows, r => Assert.False(r.BidCommission is not null && r.OfferCommission is not null));

        // 0.00 is a REAL commission and must not read as "missing".
        Assert.Equal(0.00m, ByNumber(rows, 66968).BidCommission);
    }

    [Fact]
    public async Task TheNegativePriceInMyTrades_KeepsItsSign()
    {
        var rows = await ReaderHarness.ReadTradesAsync(Samples.MyTradesAnonymised, ModComDescriptors.MyTrades);
        Assert.Equal(-0.15m, ByNumber(rows, 66664).Price);
        Assert.Equal("Verbal confirm pending", ByNumber(rows, 66664).Notes);
    }

    [Fact]
    public async Task BothTradesEndpoints_ReadTheIdenticalBodyIdentically_NoBranchingOnEndpoint()
    {
        var asAllTrades = await ReaderHarness.ReadTradesAsync(Samples.MyTradesAnonymised, ModComDescriptors.AllTrades);
        var asMyTrades = await ReaderHarness.ReadTradesAsync(Samples.MyTradesAnonymised, ModComDescriptors.MyTrades);

        Assert.Equal(asAllTrades.Count, asMyTrades.Count);
        for (var i = 0; i < asAllTrades.Count; i++)
        {
            Assert.Equal(asAllTrades[i].TradeNumber, asMyTrades[i].TradeNumber);
            Assert.Equal(asAllTrades[i].BidTrader, asMyTrades[i].BidTrader);          // read the SAME way
            Assert.Equal(asAllTrades[i].BidCommission, asMyTrades[i].BidCommission);
            Assert.Equal(asAllTrades[i].ClickAndTrade, asMyTrades[i].ClickAndTrade);
        }
    }

    // ============================================================ unkeyable rows

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("68,043")]
    [InlineData("TN68043")]
    [InlineData("6.8e4")]
    public async Task ATradeWithoutAUsableTradeNumber_IsDroppedAndCounted_NeverInserted(string tradeNumber)
    {
        var csv = Samples.TradesCsv(
            Samples.TradesRecord((ModComColumns.TradeNumber, tradeNumber), (ModComColumns.State, "Finalized")),
            Samples.TradesRecord((ModComColumns.TradeNumber, "68043"), (ModComColumns.State, "Finalized")));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, csv);

        var kept = Assert.Single(result.Rows);
        Assert.Equal(68043, kept.TradeNumber);          // the keyable row survives...

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal(1, call.DroppedRowCount);          // ...and the drop is COUNTED, the only durable record
        Assert.Equal(1, call.RowCount);
        Assert.Equal("Success", call.Status);

        // A drop is never silent.
        Assert.Contains(result.Log.OfLevel(LogLevel.Error), e => e.Message.Contains("dropped 1 unkeyable row"));
    }

    [Fact]
    public async Task APullWhoseOnlyRowIsUnkeyable_ReportsZeroRowsAndTheDrop()
    {
        var csv = Samples.TradesCsv(Samples.TradesRecord((ModComColumns.TradeNumber, "")));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, csv);

        Assert.Empty(result.Rows);
        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("NotAvailable", call.Status);       // zero usable rows
        Assert.Equal(0, call.RowCount);
        Assert.Equal(1, call.DroppedRowCount);           // but the drop is still recorded
    }

    // ============================================================ tolerant field degradation

    [Fact]
    public async Task AnUnparseableNonKeyField_DegradesToNull_AndTheRowSurvives()
    {
        var csv = Samples.TradesCsv(Samples.TradesRecord(
            (ModComColumns.TradeNumber, "68043"),
            (ModComColumns.State, "Finalized"),
            (ModComColumns.Price, "n/a"),
            (ModComColumns.TermStart, "01/09/2026"),
            (ModComColumns.LastUpdatedTimestamp, "2026-08-24 13:54:34"),   // 24-hour: wrong shape
            (ModComColumns.InIndex, "1")));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, csv);

        var row = Assert.Single(result.Rows);
        Assert.Equal(68043, row.TradeNumber);
        Assert.Equal("Finalized", row.State);      // the rest of the row is intact
        Assert.Null(row.Price);
        Assert.Null(row.TermStart);
        Assert.Null(row.LastUpdated);
        Assert.Null(row.InIndex);
        Assert.Equal(0, Assert.Single(result.FileLog.Calls).DroppedRowCount);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("unparseable"));
    }

    [Fact]
    public async Task AnOverDecimal92Volume_DegradesThatFieldToNull_AndKeepsTheRowAndTheBatch()
    {
        var csv = Samples.TradesCsv(
            Samples.TradesRecord((ModComColumns.TradeNumber, "68043"), (ModComColumns.Volume, "10000000.00")),
            Samples.TradesRecord((ModComColumns.TradeNumber, "68044"), (ModComColumns.Volume, "300000")));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, csv);

        Assert.Equal(2, result.Rows.Count);                                    // the batch survives
        Assert.Null(result.Rows.Single(r => r.TradeNumber == 68043).Volume);    // degraded field
        Assert.Equal(300000m, result.Rows.Single(r => r.TradeNumber == 68044).Volume);
        Assert.Contains(result.Log.OfLevel(LogLevel.Error), e => e.Message.Contains("DECIMAL(9,2) ceiling"));
    }

    [Fact]
    public async Task AnOverWidthNonKeyValue_IsClampedToItsColumnWidth_AndWarnedAboutOnce()
    {
        var csv = Samples.TradesCsv(Samples.TradesRecord(
            (ModComColumns.TradeNumber, "68043"),
            (ModComColumns.Side, new string('x', 400)),
            (ModComColumns.Product, new string('p', 80))));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, csv);

        var row = Assert.Single(result.Rows);
        Assert.Equal(256, row.Side!.Length);       // VARCHAR(256)
        Assert.Equal(50, row.Product!.Length);     // VARCHAR(50)
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("clamped 2 over-width"));
    }

    [Fact]
    public async Task ADuplicateTradeNumberInOneResponse_IsCollapsedNewestWins_AndCounted()
    {
        var csv = Samples.TradesCsv(
            Samples.TradesRecord(
                (ModComColumns.TradeNumber, "68043"), (ModComColumns.State, "Finalized"),
                (ModComColumns.LastUpdatedTimestamp, "2026-08-24 01:00:00 PM")),
            Samples.TradesRecord(
                (ModComColumns.TradeNumber, "68043"), (ModComColumns.State, "Cancelled"),
                (ModComColumns.LastUpdatedTimestamp, "2026-08-24 03:00:00 PM")),
            Samples.TradesRecord(
                (ModComColumns.TradeNumber, "68043"), (ModComColumns.State, "Finalized"),
                (ModComColumns.LastUpdatedTimestamp, "2026-08-24 02:00:00 PM")));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, csv);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Cancelled", row.State);                                  // the NEWEST revision wins
        Assert.Equal(new DateTime(2026, 8, 24, 15, 0, 0), row.LastUpdated);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("collapsed 2 duplicate merge key"));
    }

    [Fact]
    public void Dedup_AnUndatedCopyNeverBeatsADatedOne()
    {
        var counters = new ModComParseCounters();
        var rows = new List<TradeRow>
        {
            new() { TradeNumber = 68043, State = "Finalized", LastUpdated = new DateTime(2026, 8, 24, 12, 0, 0) },
            new() { TradeNumber = 68043, State = "Cancelled", LastUpdated = null }
        };

        var deduped = ModComTradesSourceReader.Dedup(rows, counters);

        var row = Assert.Single(deduped);
        Assert.Equal("Finalized", row.State);
        Assert.Equal(1, counters.DuplicateKeys);
    }

    [Fact]
    public void Dedup_LeavesADuplicateFreeBatchUntouched()
    {
        var counters = new ModComParseCounters();
        var rows = new List<TradeRow>
        {
            new() { TradeNumber = 1 }, new() { TradeNumber = 2 }, new() { TradeNumber = 3 }
        };

        Assert.Equal(3, ModComTradesSourceReader.Dedup(rows, counters).Count);
        Assert.Equal(0, counters.DuplicateKeys);
    }

    // ============================================================ header drift on a 200

    [Fact]
    public async Task ARenamedNonKeyColumn_MapsToNullForEveryRow_AndTheDriftIsRecordedInTheHubRow()
    {
        // A vendor RENAME must not drop the other 33 columns - and with no per-row provenance, the
        // arm.FileLog row is the only durable record that a pull was parsed against a drifted header.
        var driftedHeader = string.Join(",", ModComColumns.Trades
            .Select(n => Samples.Q(n == ModComColumns.UnitOfMeasure ? "UOM" : n)));
        var record = Samples.TradesRecord(
            (ModComColumns.TradeNumber, "68043"),
            (ModComColumns.State, "Finalized"),
            (ModComColumns.UnitOfMeasure, "m3/month"));

        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, Samples.Lf(driftedHeader, record));

        var row = Assert.Single(result.Rows);
        Assert.Equal(68043, row.TradeNumber);      // the 33 recognised columns still map
        Assert.Equal("Finalized", row.State);
        Assert.Null(row.UnitOfMeasure);            // the renamed one degrades to NULL

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.NotNull(call.ErrorMessage);
        Assert.Contains("HeaderDrift", call.ErrorMessage!);
        Assert.Contains("Unit of Measure", call.ErrorMessage!);
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("HeaderDrift"));
    }

    [Fact]
    public async Task ANonCsvContentType_Warns_ButStillParses()
    {
        var handler = FakeHttpMessageHandler.ForRequest(_ =>
            FakeHttpMessageHandler.WithContentType(HttpStatusCode.OK, Samples.AllTradesLive, "text/plain"));
        var fileLog = new FakeModComFileLog();
        var log = new ListLogger();
        var reader = new ModComTradesSourceReader(
            handler.NewClient(), ReaderHarness.Settings(), fileLog, ModComDescriptors.AllTrades, log);

        var rows = await reader.ReadAsync(ReaderHarness.Unit(), CancellationToken.None);

        Assert.Equal(Samples.AllTradesRowCount, rows.Count);
        Assert.Contains(log.OfLevel(LogLevel.Warning), e => e.Message.Contains("text/plain"));
    }

    // ============================================================ the request itself

    [Fact]
    public async Task TheRequestUri_CarriesTheWindowExplicitly_AndNoCredential()
    {
        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, Samples.AllTradesLive);

        var uri = Assert.Single(result.Handler.Requests);
        Assert.Equal("https://app.modcom.inc/api/integration/allTrades/v1?startDate=2026-07-25&endDate=2026-08-24",
            uri.ToString());

        // The credential is HTTP Basic in a HEADER; it can never appear in a ModCom URL.
        Assert.DoesNotContain(ReaderHarness.DummyUser, uri.ToString());
        Assert.DoesNotContain(ReaderHarness.DummyPassword, uri.ToString());
        Assert.DoesNotContain("apikey", uri.ToString());
    }

    [Fact]
    public async Task NoLogLineEverEchoesTheCredential()
    {
        var result = await ReaderHarness.RunTradesAsync(HttpStatusCode.OK, Samples.AllTradesLive);

        Assert.All(result.Log.Messages, m =>
        {
            Assert.DoesNotContain(ReaderHarness.DummyPassword, m);
            Assert.DoesNotContain("Authorization", m);
        });
    }
}
