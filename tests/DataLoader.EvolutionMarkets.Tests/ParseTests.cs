using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the field-by-field mapping and the tolerant-parse contract (design §5.1, §5.3).
///
/// <para>The contract: <b>an unrecognised or unparseable value degrades to NULL and never fails the
/// row or the run; only a record whose KEY (<c>marketDataId</c>) is unusable is dropped, and it is
/// counted.</b></para>
///
/// <para>The mapping itself is where the vendor's two field vocabularies bite: the projection is
/// REQUESTED as <c>marketDataId</c>/<c>instrumentName</c>/<c>priceTS</c>/<c>businessDate</c> and
/// arrives as <c>priceId</c>/<c>instrument</c>/<c>priceTs</c>/<c>date</c>. Getting either side wrong
/// yields a column of NULLs with nothing in the log, so these tests read from the committed live
/// fixture rather than a paraphrase of it.</para>
/// </summary>
public class ParseTests
{
    // ---- the live fixture, end to end ----------------------------------------------------------

    [Fact]
    public async Task LiveSlice_ParsesEveryRecord()
    {
        var rows = await ReaderHarness.ReadAsync(Samples.LiveSlice);
        Assert.Equal(5, rows.Count);
        Assert.Equal(5, rows.Select(r => r.MarketDataId).Distinct().Count());
    }

    [Fact]
    public async Task LiveSlice_MapsThePrimaryKeyFromPriceId()
    {
        // The response key is `priceId`; the column is MarketDataId. Same field, two vendor spellings.
        var rows = await ReaderHarness.ReadAsync(Samples.LiveSlice);

        Assert.Contains(Guid.Parse("6d11bc3c-a601-480a-8032-b6d42068b9ea"),
            rows.Select(r => r.MarketDataId));
    }

    [Fact]
    public async Task FullRecord_MapsEveryPopulatedFieldToItsOwnColumn()
    {
        var row = await ReaderHarness.ReadOneAsync(Samples.FullRecord);

        Assert.Equal(Guid.Parse("6d11bc3c-a601-480a-8032-b6d42068b9ea"), row.MarketDataId);
        Assert.Equal("US Natural Gas Index", row.Market);
        Assert.Equal("Sep'26-Oct'26", row.Term);
        Assert.Equal("3m", row.Tenor);
        Assert.Equal(Guid.Parse("997feae2-4c97-4e14-b090-203d625ff5a5"), row.InstrumentId);
        Assert.Equal("Socal-Border Index Futures", row.InstrumentName);   // from JSON "instrument"
        Assert.Equal(new DateOnly(2026, 8, 24), row.BusinessDate);        // from JSON "date"
        Assert.Equal("Indicative", row.PriceType);
        Assert.Equal(-0.02m, row.Ask);
        Assert.Equal(-0.0325m, row.Bid);
        Assert.Equal(-0.0263m, row.Mid);
        Assert.Equal(-0.0263m, row.Change);
        Assert.Equal("USD", row.Currency);
    }

    [Fact]
    public async Task NineColumns_AreNullForThisDataset_AndThatIsNormal()
    {
        // Term2, InstrumentSourceName, Size, Depth, Price, AskSize, BidSize, MidSize, PctRetDaily
        // returned no value on ANY of the 8,118 rows available for EVOID/USNaturalGasIndex. This
        // records that reality so a future reader does not "fix" a non-existent bug.
        var rows = await ReaderHarness.ReadAsync(Samples.LiveSlice);

        Assert.All(rows, r =>
        {
            Assert.Null(r.Term2);
            Assert.Null(r.InstrumentSourceName);
            Assert.Null(r.Size);
            Assert.Null(r.Depth);
            Assert.Null(r.Price);
            Assert.Null(r.AskSize);
            Assert.Null(r.BidSize);
            Assert.Null(r.MidSize);
            Assert.Null(r.PctRetDaily);
        });
    }

    [Fact]
    public async Task Price_IsNeverBackfilledFromMid()
    {
        // Backfilling would invent a print the vendor never made.
        var rows = await ReaderHarness.ReadAsync(Samples.LiveSlice);
        Assert.All(rows, r => Assert.Null(r.Price));
        Assert.Contains(rows, r => r.Mid is not null);
    }

    // ---- priceTs: the timezone trap ------------------------------------------------------------

    /// <summary>
    /// *** THE TIMEZONE TRAP. ***
    /// <c>priceTs</c> is an instant with an explicit <c>Z</c>. It must be stored as UTC. Parsing it as
    /// local time on a US-Central host would shift it back 5–6 hours and, at <c>DATETIME2(0)</c>, land
    /// it on the PREVIOUS DAY — silently disagreeing with <c>BusinessDate</c> on every single row.
    /// </summary>
    [Fact]
    public async Task PriceTs_IsParsedAsUtc_NotLocalTime()
    {
        var row = await ReaderHarness.ReadOneAsync(Samples.FullRecord);

        Assert.NotNull(row.PriceTs);
        Assert.Equal(DateTimeKind.Utc, row.PriceTs!.Value.Kind);
        Assert.Equal(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc), row.PriceTs);

        // The invariant arm.usp_ValidateLoad's PriceTsDateMismatch check asserts.
        Assert.Equal(row.BusinessDate, DateOnly.FromDateTime(row.PriceTs!.Value));
    }

    [Fact]
    public async Task PriceTs_WithoutAnOffset_IsAssumedUtc_NotLocal()
    {
        // Defensive: if the vendor ever drops the Z, treating the value as local would shift every
        // timestamp. AssumeUniversal keeps it UTC.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","priceTs":"2026-08-24T00:00:00"}""");

        Assert.Equal(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc), row.PriceTs);
    }

    // ---- tolerant degradation ------------------------------------------------------------------

    [Fact]
    public async Task AMissingTenor_IsNull_NotAnError()
    {
        // ~53% of live rows omit `tenor` entirely. This is the single most common "absent" case and
        // must be completely routine.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","term":"2026-Winter"}""");

        Assert.Null(row.Tenor);
        Assert.Equal("2026-Winter", row.Term);
    }

    [Fact]
    public async Task AStringInADecimalSlot_DegradesToNull_NeverThrows()
    {
        // *** THIS IS WHERE THE `change` VENDOR BUG LANDS HARMLESSLY. *** With a trimmed `field`
        // projection the server sends the `term` STRING in the `change` slot. The parse must yield
        // NULL rather than throwing or inventing a number.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","change":"Sep'26-Oct'26","ask":0.5}""");

        Assert.Null(row.Change);
        Assert.Equal(0.5m, row.Ask);      // the rest of the row is unaffected
    }

    [Fact]
    public async Task AnUnparseableNumber_NeverBecomesZero()
    {
        // A fabricated 0 is a real, tradeable price and would be indistinguishable from a genuine
        // flat print. NULL is the only honest answer.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","ask":"not-a-number","bid":"","mid":null}""");

        Assert.Null(row.Ask);
        Assert.Null(row.Bid);
        Assert.Null(row.Mid);
    }

    [Theory]
    [InlineData("1e30")]                    // beyond decimal itself
    [InlineData("99999999999.5")]           // 11 integral digits: beyond DECIMAL(18,8)
    [InlineData("-99999999999.5")]
    public async Task ADecimalTooLargeForTheColumn_DegradesToNull_RatherThanFailingThePage(string value)
    {
        // A DECIMAL(18,8) tops out just below 1e10. Passing more would throw at TVP BIND time, which
        // fails the ENTIRE page — one absurd number would cost a whole business date. No observed
        // price is anywhere near this; the guard is against vendor drift.
        var row = await ReaderHarness.ReadOneAsync(
            $$"""{"priceId":"00000000-0000-0000-0000-000000000001","ask":{{value}},"bid":0.5}""");

        Assert.Null(row.Ask);
        Assert.Equal(0.5m, row.Bid);      // the rest of the row still loads
    }

    [Theory]
    [InlineData("9999999999.5")]           // 10 integral digits: still inside DECIMAL(18,8)
    [InlineData("-9999999999.5")]
    public async Task ADecimalAtTheEdgeOfTheColumn_IsKept(string value)
    {
        var row = await ReaderHarness.ReadOneAsync(
            $$"""{"priceId":"00000000-0000-0000-0000-000000000001","ask":{{value}}}""");

        Assert.NotNull(row.Ask);
    }

    [Fact]
    public async Task ExtraDecimalPlacesBeyondTheColumnScale_AreKept_NotRejected()
    {
        // SQL Server rounds to the column's scale on assignment. That is what storing the value means
        // and is not worth rejecting a row over — only an oversized INTEGRAL part is a bind failure.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","ask":0.123456789012345}""");

        Assert.NotNull(row.Ask);
    }

    [Fact]
    public async Task AGenuineZero_IsPreserved()
    {
        // The counterpart to the test above: 0 IS a real value (a point that did not move) and must
        // survive as 0, not become NULL.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","change":0,"ask":0.0}""");

        Assert.Equal(0m, row.Change);
        Assert.Equal(0m, row.Ask);
    }

    [Fact]
    public async Task NegativePrices_AreParsedAndKept()
    {
        // These are basis differentials. Negative values are ROUTINE, not exceptional, which is why
        // no price column carries a non-negative CHECK constraint.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","ask":-0.1,"bid":-0.25,"mid":-0.175}""");

        Assert.Equal(-0.1m, row.Ask);
        Assert.Equal(-0.25m, row.Bid);
        Assert.Equal(-0.175m, row.Mid);
    }

    [Fact]
    public async Task AnIntegralDecimal_BecomesAnInt()
    {
        // The vendor types size/depth as `number` even though the columns are INT, so 5.0 must become
        // 5 rather than NULL.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","size":5.0,"depth":7}""");

        Assert.Equal(5, row.Size);
        Assert.Equal(7, row.Depth);
    }

    [Fact]
    public async Task AGenuinelyFractionalInt_DegradesToNull_RatherThanTruncating()
    {
        // Silently truncating 5.4 to 5 would invent a count the vendor never published.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","size":5.4}""");

        Assert.Null(row.Size);
    }

    [Fact]
    public async Task AnUnparseableGuidInANonKeyField_DegradesToNull()
    {
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","instrumentId":"not-a-guid"}""");

        Assert.Null(row.InstrumentId);
    }

    [Fact]
    public async Task ADateInTheWrongFormat_DegradesToNull()
    {
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","date":"24/08/2026"}""");

        Assert.Null(row.BusinessDate);
    }

    [Fact]
    public async Task AFullInstantInTheDateField_YieldsItsDatePart()
    {
        // Defensive: if the vendor switches `date` to the `priceTs` shape, take the date rather than
        // dropping the field.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","date":"2026-08-24T00:00:00.000Z"}""");

        Assert.Equal(new DateOnly(2026, 8, 24), row.BusinessDate);
    }

    // ---- drop-and-count: only an unusable KEY -------------------------------------------------

    [Fact]
    public async Task AMissingKey_DropsTheRecordAndCountsIt()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK,
            """[{"market":"US Natural Gas Index","ask":1},{"priceId":"00000000-0000-0000-0000-000000000002","ask":2}]""");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.FileLog.Single.DroppedRowCount);
        Assert.Contains(result.Log.Warnings, m => m.Contains("droppedNoKey=1"));
    }

    [Fact]
    public async Task AnUnparseableKey_DropsTheRecord()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK,
            """[{"priceId":"not-a-guid","ask":1}]""");

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.FileLog.Single.DroppedRowCount);
    }

    [Fact]
    public async Task AnAllZeroGuidKey_IsRejected()
    {
        // Guid.Empty is a placeholder, not an id; accepting it would collide across unrelated records
        // and make the PK meaningless.
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK,
            """[{"priceId":"00000000-0000-0000-0000-000000000000","ask":1}]""");

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.FileLog.Single.DroppedRowCount);
    }

    [Fact]
    public async Task ANonObjectArrayElement_IsDroppedAndCounted()
    {
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK,
            """["a string",42,null,{"priceId":"00000000-0000-0000-0000-000000000001"}]""");

        Assert.Single(result.Rows);
        Assert.Equal(3, result.FileLog.Single.DroppedRowCount);
        Assert.Contains(result.Log.Warnings, m => m.Contains("droppedNotAnObject=3"));
    }

    [Fact]
    public async Task AnEntirelyUnrecognisedRecord_StillLoadsIfItHasAKey()
    {
        // Everything degrades to NULL; the row survives because the key is usable. Losing the row
        // would lose the evidence that the vendor changed something.
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","somethingBrandNew":"x"}""");

        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), row.MarketDataId);
        Assert.Null(row.Ask);
        Assert.Null(row.Market);
    }

    // ---- string capping ------------------------------------------------------------------------

    [Fact]
    public async Task AnOverlongString_IsTruncatedToTheColumnWidthAndCounted()
    {
        // Observed maxima are far below the column widths (longest instrument name is 63 of 250), so
        // this should never fire in production — which is exactly why it must be counted and logged
        // rather than silently failing the TVP bind for the whole page.
        var longName = new string('x', 400);
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK,
            $$"""[{"priceId":"00000000-0000-0000-0000-000000000001","instrument":"{{longName}}"}]""");

        Assert.Single(result.Rows);
        Assert.Equal(250, result.Rows[0].InstrumentName!.Length);
        Assert.Contains(result.Log.Warnings, m => m.Contains("truncated=1"));
    }

    [Theory]
    [InlineData("term", 50)]
    [InlineData("tenor", 50)]
    [InlineData("currency", 50)]
    [InlineData("market", 250)]
    [InlineData("priceType", 250)]
    public async Task StringColumns_AreCappedAtTheirDeclaredWidth(string field, int width)
    {
        var value = new string('y', width + 20);
        var rows = await ReaderHarness.ReadAsync(
            $$"""[{"priceId":"00000000-0000-0000-0000-000000000001","{{field}}":"{{value}}"}]""");

        var actual = field switch
        {
            "term" => rows[0].Term,
            "tenor" => rows[0].Tenor,
            "currency" => rows[0].Currency,
            "market" => rows[0].Market,
            "priceType" => rows[0].PriceType,
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(width, actual!.Length);
    }

    [Fact]
    public async Task Strings_AreTrimmed_AndBlanksBecomeNull()
    {
        var row = await ReaderHarness.ReadOneAsync(
            """{"priceId":"00000000-0000-0000-0000-000000000001","term":"  2026-Oct  ","tenor":"   "}""");

        Assert.Equal("2026-Oct", row.Term);
        Assert.Null(row.Tenor);
    }

    // ---- case-insensitive / alternate spellings ------------------------------------------------

    [Fact]
    public async Task TheCsvSpellings_AreAlsoAccepted()
    {
        // The vendor's CSV/XML renderings use marketDataId / instrumentName / priceTS / businessDate
        // for the same fields. Accepting both spellings means the loader survives the vendor settling
        // on either one.
        var row = await ReaderHarness.ReadOneAsync(
            """
            {"marketDataId":"6d11bc3c-a601-480a-8032-b6d42068b9ea",
             "instrumentName":"Socal-Border Index Futures",
             "priceTS":"2026-08-24T00:00:00.000Z",
             "businessDate":"2026-08-24"}
            """);

        Assert.Equal(Guid.Parse("6d11bc3c-a601-480a-8032-b6d42068b9ea"), row.MarketDataId);
        Assert.Equal("Socal-Border Index Futures", row.InstrumentName);
        Assert.Equal(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc), row.PriceTs);
        Assert.Equal(new DateOnly(2026, 8, 24), row.BusinessDate);
    }

    [Fact]
    public async Task PropertyLookupIsCaseInsensitive()
    {
        var row = await ReaderHarness.ReadOneAsync(
            """{"PRICEID":"00000000-0000-0000-0000-000000000001","ASK":1.5}""");

        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), row.MarketDataId);
        Assert.Equal(1.5m, row.Ask);
    }

    [Fact]
    public async Task ACleanPage_LogsNoToleranceWarning()
    {
        // The counters line must stay quiet on a healthy load, or it will be ignored when it matters.
        var result = await ReaderHarness.RunAsync(HttpStatusCode.OK, Samples.LiveSlice);
        Assert.DoesNotContain(result.Log.Warnings, m => m.Contains("tolerance counters"));
    }
}
