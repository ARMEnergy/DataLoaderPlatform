using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// End-to-end mapping for <see cref="ModComSettlementsSourceReader"/>, driven from the committed
/// live fixture.
///
/// <para><b>Six of the nine columns are the PRIMARY KEY</b> and all six are <c>NOT NULL</c>:
/// <c>SettlementDate, Product, Location, PieplineTerminal, PriceBasis, Term</c>. Two consequences
/// are pinned hard here:</para>
/// <list type="bullet">
///   <item>the literal <c>-</c> that 82 of 1,443 live rows carry in <c>Location</c> <b>and</b>
///     <c>Pipeline/Terminal</c> is a key <b>VALUE</b> and must survive <b>verbatim</b> - mapping it
///     to NULL/blank would violate the PK and split one logical row in two;</item>
///   <item>a row blank (or over-width) in ANY key column is <b>dropped and counted</b>, never
///     inserted with an empty-string key and never silently truncated onto a different entity.</item>
/// </list>
/// </summary>
public class SettlementsReaderTests
{
    // ============================================================ the real fixture

    [Fact]
    public async Task TheLiveSettlementsFixture_ParsesEveryRowWithNoDrops()
    {
        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, Samples.SettlementsLive);

        Assert.Equal(Samples.SettlementsRowCount, result.Rows.Count);

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(200, call.HttpStatus);
        Assert.Equal(Samples.SettlementsRowCount, call.RowCount);
        Assert.Equal(0, call.DroppedRowCount);
        Assert.Empty(result.Log.OfLevel(LogLevel.Error));

        // Two settlement dates, and every key column is populated on every row.
        Assert.Equal(new[] { new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 21) },
            result.Rows.Select(r => r.SettlementDate).Distinct().OrderBy(d => d).ToArray());
        Assert.All(result.Rows, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Product));
            Assert.False(string.IsNullOrWhiteSpace(r.Location));
            Assert.False(string.IsNullOrWhiteSpace(r.PieplineTerminal));
            Assert.False(string.IsNullOrWhiteSpace(r.PriceBasis));
            Assert.False(string.IsNullOrWhiteSpace(r.Term));
        });
    }

    [Fact]
    public async Task ALiveRow_IsPinnedFieldByField()
    {
        var rows = await ReaderHarness.ReadSettlementsAsync(Samples.SettlementsLive);

        var r = rows.Single(x => x.SettlementDate == new DateOnly(2026, 8, 21)
                                 && x.Product == "AHS" && x.Term == "AUG-26");

        Assert.Equal("Edmonton", r.Location);
        Assert.Equal("Enb T@S", r.PieplineTerminal);      // the '@' survives; only the COLUMN is [sic]
        Assert.Equal("WTI CMA", r.PriceBasis);
        Assert.Equal(new DateOnly(2026, 8, 1), r.TermStart);
        Assert.Equal(new DateOnly(2026, 8, 31), r.TermEnd);
        Assert.Equal(-14.30m, r.Price);                   // negative: 74% of live rows are
    }

    [Fact]
    public async Task TheDashRows_KeepTheLiteralDashInBOTHKeyColumns_PairedWithTheirProduct()
    {
        var rows = await ReaderHarness.ReadSettlementsAsync(Samples.SettlementsLive);

        var dashed = rows.Where(r => r.Location == "-").ToList();
        Assert.Equal(Samples.SettlementsDashKeyRowCount, dashed.Count);

        // BOTH directions of the pairing, so an inverted or half-applied mapping fails loudly:
        // every '-' Location row also has a '-' PieplineTerminal AND is the blended product...
        Assert.All(dashed, r =>
        {
            Assert.Equal("-", r.PieplineTerminal);
            Assert.Equal("Sweet Guernsey Blend", r.Product);
            Assert.NotNull(r.Price);
        });

        // ...and conversely every Sweet Guernsey Blend row is exactly one of those rows.
        Assert.Equal(dashed.Count, rows.Count(r => r.Product == "Sweet Guernsey Blend"));
        Assert.Equal(dashed.Count, rows.Count(r => r.PieplineTerminal == "-"));

        // The '-' is NOT a null sentinel here (contrast NGI's "None").
        Assert.All(dashed, r => Assert.NotEqual(string.Empty, r.Location));
        Assert.Equal(new[] { 1.70m, 5.05m }, dashed.Select(r => r.Price!.Value).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task TheUsdDollarPriceBasisRows_AreNeverTrimmedOrNormalised()
    {
        var rows = await ReaderHarness.ReadSettlementsAsync(Samples.SettlementsLive);

        var usd = rows.Where(r => r.PriceBasis == "USD $").ToList();
        Assert.Equal(Samples.SettlementsUsdBasisRowCount, usd.Count);
        Assert.All(usd, r =>
        {
            Assert.Equal("USD $", r.PriceBasis);              // a space AND a '$', inside a KEY
            Assert.Equal("Bak-USGC/DSW-Cush", r.Product);
            Assert.Equal("Ex-ETCOP/Enterprise", r.PieplineTerminal);
        });
        Assert.Equal(new[] { "Beaumont/Cushing", "Nederland/Cushing" },
            usd.Select(r => r.Location).OrderBy(l => l).ToArray());
    }

    [Fact]
    public async Task PricesAreSigned_AndTheNegativeCountMatchesTheFixture()
    {
        var rows = await ReaderHarness.ReadSettlementsAsync(Samples.SettlementsLive);

        Assert.Equal(Samples.SettlementsNegativePriceCount, rows.Count(r => r.Price < 0));
        Assert.All(rows, r => Assert.NotNull(r.Price));
    }

    [Fact]
    public async Task TheTermIsAlwaysASingleMonthHere_AndIsPersistedAsPublished()
    {
        var rows = await ReaderHarness.ReadSettlementsAsync(Samples.SettlementsLive);

        Assert.All(rows, r =>
        {
            Assert.DoesNotContain("/", r.Term);   // contrast the trades Term, which can be joined
            Assert.DoesNotContain("~", r.Term);
            Assert.Contains("-", r.Term);         // MMM-YY
        });
    }

    // ============================================================ key handling

    [Theory]
    [InlineData(ModComColumns.Product)]
    [InlineData(ModComColumns.Location)]
    [InlineData(ModComColumns.PipelineTerminal)]
    [InlineData(ModComColumns.PriceBasis)]
    [InlineData(ModComColumns.Term)]
    public async Task ARowBlankInAnyTextKeyColumn_IsDroppedAndCounted(string blankColumn)
    {
        var csv = Samples.SettlementsCsv(
            Samples.SettlementsRecord((blankColumn, "")),
            Samples.SettlementsRecord((ModComColumns.Term, "SEP-26")));

        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, csv);

        var kept = Assert.Single(result.Rows);
        Assert.Equal("SEP-26", kept.Term);

        var call = Assert.Single(result.FileLog.Calls);
        Assert.Equal(1, call.DroppedRowCount);
        Assert.Equal(1, call.RowCount);
        Assert.Contains(result.Log.OfLevel(LogLevel.Error), e => e.Message.Contains("dropped 1 unkeyable row"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("21/08/2026")]
    [InlineData("AUG-26")]
    public async Task ARowWithoutAUsableSettlementDate_IsDroppedAndCounted(string date)
    {
        var csv = Samples.SettlementsCsv(
            Samples.SettlementsRecord((ModComColumns.SettlementDate, date)),
            Samples.SettlementsRecord((ModComColumns.Term, "SEP-26")));

        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, csv);

        Assert.Single(result.Rows);
        Assert.Equal(1, Assert.Single(result.FileLog.Calls).DroppedRowCount);
    }

    [Fact]
    public async Task ARowWhoseKeyIsOverWidth_IsDroppedWithAnExplicitError_NeverTruncated()
    {
        // A truncated key would MERGE onto a DIFFERENT logical row - worse than a missing row.
        var csv = Samples.SettlementsCsv(
            Samples.SettlementsRecord((ModComColumns.Location, new string('x', 51))),
            Samples.SettlementsRecord((ModComColumns.PriceBasis, new string('y', 101))),
            Samples.SettlementsRecord((ModComColumns.Term, "SEP-26")));

        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, csv);

        Assert.Single(result.Rows);
        Assert.Equal(2, Assert.Single(result.FileLog.Calls).DroppedRowCount);
        Assert.Contains(result.Log.OfLevel(LogLevel.Error),
            e => e.Message.Contains("KEY value exceeded its column width"));
    }

    [Fact]
    public async Task AKeyValueAtExactlyItsColumnWidth_IsKept()
    {
        var csv = Samples.SettlementsCsv(
            Samples.SettlementsRecord((ModComColumns.Location, new string('x', 50))),
            Samples.SettlementsRecord((ModComColumns.PriceBasis, new string('y', 100)), (ModComColumns.Term, "SEP-26")));

        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, csv);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, Assert.Single(result.FileLog.Calls).DroppedRowCount);
    }

    [Fact]
    public async Task ADashInEveryTextKeyColumn_IsKept_BecauseDashIsAValue()
    {
        var row = await ReaderHarness.ReadOneSettlementAsync(Samples.SettlementsRecord(
            (ModComColumns.Product, "-"),
            (ModComColumns.Location, "-"),
            (ModComColumns.PipelineTerminal, "-"),
            (ModComColumns.PriceBasis, "-"),
            (ModComColumns.Term, "-")));

        Assert.Equal("-", row.Product);
        Assert.Equal("-", row.Location);
        Assert.Equal("-", row.PieplineTerminal);
        Assert.Equal("-", row.PriceBasis);
        Assert.Equal("-", row.Term);
    }

    // ============================================================ non-key nullability

    [Fact]
    public async Task BlankTermStartTermEndAndPrice_LeaveTheRowInPlaceWithNulls()
    {
        // Only an unusable KEY drops a row; the three non-key columns are NULLable by design.
        var row = await ReaderHarness.ReadOneSettlementAsync(Samples.SettlementsRecord(
            (ModComColumns.TermStart, ""),
            (ModComColumns.TermEnd, ""),
            (ModComColumns.Price, "")));

        Assert.Equal(new DateOnly(2026, 8, 21), row.SettlementDate);
        Assert.Null(row.TermStart);
        Assert.Null(row.TermEnd);
        Assert.Null(row.Price);
    }

    [Fact]
    public async Task AFarForwardTermEnd_ParsesWithoutAnyDateRangeAssumption()
    {
        var row = await ReaderHarness.ReadOneSettlementAsync(Samples.SettlementsRecord(
            (ModComColumns.Term, "DEC-31"),
            (ModComColumns.TermStart, "2031-12-01"),
            (ModComColumns.TermEnd, "2031-12-31")));

        Assert.Equal(new DateOnly(2031, 12, 31), row.TermEnd);   // the curve really runs this far
    }

    // ============================================================ in-batch de-dup

    [Fact]
    public async Task ADuplicateSixColumnKeyInOneResponse_IsCollapsedLastWins_AndCounted()
    {
        var csv = Samples.SettlementsCsv(
            Samples.SettlementsRecord((ModComColumns.Price, "-14.30")),
            Samples.SettlementsRecord((ModComColumns.Price, "-14.55")));

        var result = await ReaderHarness.RunSettlementsAsync(HttpStatusCode.OK, csv);

        var row = Assert.Single(result.Rows);
        Assert.Equal(-14.55m, row.Price);        // last in file order wins
        Assert.Contains(result.Log.OfLevel(LogLevel.Warning), e => e.Message.Contains("collapsed 1 duplicate merge key"));
    }

    [Fact]
    public void Dedup_TreatsTheKeyCaseInsensitively_ButKeepsTheLastRowAsWritten()
    {
        var counters = new ModComParseCounters();
        var rows = new List<SettlementRow>
        {
            NewRow("Edmonton", 1.00m),
            NewRow("EDMONTON", 2.00m)
        };

        var deduped = ModComSettlementsSourceReader.Dedup(rows, counters);

        var row = Assert.Single(deduped);
        Assert.Equal(2.00m, row.Price);
        Assert.Equal("EDMONTON", row.Location);
        Assert.Equal(1, counters.DuplicateKeys);
    }

    [Fact]
    public void Dedup_KeepsRowsDifferingInASingleKeyColumn()
    {
        var counters = new ModComParseCounters();
        var rows = new List<SettlementRow>
        {
            NewRow("Edmonton", 1.00m),
            NewRow("Hardisty", 2.00m),
            NewRow("-", 3.00m)
        };

        Assert.Equal(3, ModComSettlementsSourceReader.Dedup(rows, counters).Count);
        Assert.Equal(0, counters.DuplicateKeys);
    }

    private static SettlementRow NewRow(string location, decimal price) => new()
    {
        SettlementDate = new DateOnly(2026, 8, 21),
        Product = "AHS",
        Location = location,
        PieplineTerminal = "Enb T@S",
        PriceBasis = "WTI CMA",
        Term = "AUG-26",
        TermStart = new DateOnly(2026, 8, 1),
        TermEnd = new DateOnly(2026, 8, 31),
        Price = price
    };
}
