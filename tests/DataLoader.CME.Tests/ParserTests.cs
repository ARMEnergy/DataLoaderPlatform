using Xunit;
namespace DataLoader.CME.Tests;

/// <summary>
/// Parser behaviour, pinned against verbatim live bulletin fixtures.
///
/// <para>
/// The emphasis is on the things positional parsing gets silently wrong: values
/// landing in the neighbouring column, indicators being dropped, aggregate lines
/// loading as facts, and tick notation being scaled by the wrong denominator.
/// Each of those is a test that would pass just as happily on garbage if it only
/// asserted "some rows were produced", so every one asserts specific values in
/// specific columns.
/// </para>
/// </summary>
public class ParserTests
{
    // ---------------------------------------------------------------------
    // The blank-versus-dashes trap
    // ---------------------------------------------------------------------

    /// <summary>
    /// THE test for this loader. On this row ACTUAL VOL, PRIOR VOL and PRIOR INT
    /// are BLANK while OPEN..LAST are "----", so a whitespace split would put
    /// PriorSettle's 1828.4 into EstVol and leave PriorSettle null. Asserting the
    /// exact column each value lands in is what catches that.
    /// </summary>
    [Fact]
    public void BlankTrailingColumnsDoNotShiftEarlierValues()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        var row = rows.Future("0CJ", 2026, 9);

        Assert.Null(row.Open);
        Assert.Null(row.High);
        Assert.Null(row.Low);
        Assert.Null(row.Last);
        Assert.Equal(1821.0m, row.Settle);
        Assert.Equal(-7.4m, row.PctChange);

        // The three that a whitespace split would corrupt.
        Assert.Null(row.EstVol);
        Assert.Equal(1828.4m, row.PriorSettle);
        Assert.Null(row.PriorVol);
        Assert.Null(row.PriorInt);
    }

    [Fact]
    public void EveryPopulatedColumnIsReadFromItsOwnField()
    {
        // 0.00  ...  2.92  -.35   (blank AVOL)  3.27  (blank PVOL)  7875
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        var row = rows.Option("7A", 0.00m, "C");

        Assert.Equal(2.92m, row.Settle);
        Assert.Equal(-0.35m, row.PctChange);
        Assert.Null(row.EstVol);
        Assert.Equal(3.27m, row.PriorSettle);
        Assert.Null(row.PriorVol);
        Assert.Equal(7875m, row.PriorInt);
    }

    // ---------------------------------------------------------------------
    // A/B indicators
    // ---------------------------------------------------------------------

    /// <summary>
    /// The indicator sits ONE column past the numeric right edge, so a slice that
    /// stopped at the edge would parse every number correctly and drop every
    /// indicator — leaving all three indicator columns NULL on every row while the
    /// load still looked perfect. This test is the regression guard for exactly
    /// that bug.
    /// </summary>
    [Fact]
    public void IndicatorIsCapturedAndDoesNotCorruptItsNumber()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        // JAN29  1942.5  2136.9B  1942.5  1942.5  1936.0 ...
        var high = rows.Future("0CJ", 2029, 1);
        Assert.Equal(1942.5m, high.Open);
        Assert.Equal(2136.9m, high.High);
        Assert.Equal("B", high.HighABIndicator);
        Assert.Null(high.LowABIndicator);
        Assert.Equal(1942.5m, high.Low);

        // APR29  ----  ----  1881.7A  1881.7A  1945.6 ...
        var low = rows.Future("0CJ", 2029, 4);
        Assert.Equal(1881.7m, low.Low);
        Assert.Equal("A", low.LowABIndicator);
        Assert.Equal(1881.7m, low.Last);
        Assert.Equal("A", low.LastABIndicator);
        Assert.Null(low.High);
        Assert.Null(low.HighABIndicator);
    }

    [Fact]
    public void OnlyTheThreeDdlIndicatorColumnsEverGetAnIndicator()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Nymex);

        // Nothing in the fixture should land an indicator on a field the DDL has no
        // column for; if the geometry drifted, this counter is how we would know.
        Assert.Equal(0, stats.UnexpectedIndicators);
        Assert.All(rows, r =>
        {
            Assert.True(r.HighABIndicator is null or "A" or "B");
            Assert.True(r.LowABIndicator is null or "A" or "B");
            Assert.True(r.LastABIndicator is null or "A" or "B");
        });
    }

    // ---------------------------------------------------------------------
    // Section routing: option versus future
    // ---------------------------------------------------------------------

    [Fact]
    public void CallAndPutSectionsBecomeOptionRowsWithTheMonthFromTheHeader()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        var call = rows.Option("7A", 0.50m, "C");
        Assert.Equal(CmeRowKind.Option, call.Kind);
        Assert.Equal("Crude Oil Financial Calendar Spread Option (One Month)", call.ProductDescription);
        Assert.Equal(2026, call.ContractYear);
        Assert.Equal(10, call.ContractMonth); // OCT26, from the header
        Assert.Equal(2.42m, call.Settle);

        var put = rows.Option("7A", -0.75m, "P");
        Assert.Equal("P", put.PutCall);
        Assert.Equal("Crude Oil Financial Calendar Spread Option (One Month)", put.ProductDescription);
        Assert.Equal(10, put.ContractMonth);
    }

    /// <summary>CALL/PUT must be stripped from the description, not left in it.</summary>
    [Fact]
    public void PutCallWordIsRemovedFromTheDescription()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        Assert.All(rows.Where(r => r.Kind == CmeRowKind.Option), r =>
        {
            Assert.DoesNotContain("CALL", r.ProductDescription);
            Assert.DoesNotContain("PUT", r.ProductDescription);
        });
    }

    /// <summary>The MMMYY token must be stripped from the description too.</summary>
    [Fact]
    public void ContractMonthTokenIsRemovedFromTheDescription()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        var option = rows.Option("0PO", 1540m, "C");
        Assert.Equal("TEST PLATINUM OPTION", option.ProductDescription);
        Assert.Equal(10, option.ContractMonth);

        var november = rows.Option("0PO", 1880m, "C");
        Assert.Equal("TEST PLATINUM OPTION", november.ProductDescription);
        Assert.Equal(11, november.ContractMonth);
    }

    [Fact]
    public void FutureSectionTakesItsMonthFromEachRowLabel()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        var futures = rows.Where(r => r.Kind == CmeRowKind.Future).ToList();

        Assert.All(futures, r =>
        {
            Assert.Equal("0CJ", r.ProductSymbol);
            Assert.Equal("TEST PLATINUM FUTURE", r.ProductDescription);
            Assert.Equal(string.Empty, r.PutCall);
            Assert.Equal(0m, r.Strike);
        });

        // 14 contract months, all distinct, spanning 2026-09 .. 2029-07.
        Assert.Equal(14, futures.Count);
        Assert.Equal(14, futures.Select(r => (r.ContractYear, r.ContractMonth)).Distinct().Count());
        Assert.Equal((short)2026, futures.Min(r => r.ContractYear));
        Assert.Equal((short)2029, futures.Max(r => r.ContractYear));
    }

    [Fact]
    public void NegativeStrikesAreParsed()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        Assert.Equal(0.01m, rows.Option("7A", -1.00m, "P").Settle);
        Assert.Equal(0.01m, rows.Option("7A", -0.50m, "P").Settle);
    }

    // ---------------------------------------------------------------------
    // TOTAL aggregate lines
    // ---------------------------------------------------------------------

    /// <summary>
    /// A "TOTAL" value line parses perfectly as a data row — 3,197 of them do in
    /// the live sample — so it would load as a fact with a bogus label unless the
    /// label is checked first.
    /// </summary>
    [Fact]
    public void TotalLinesAreSkippedNotLoaded()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Nymex);

        Assert.Equal(2, stats.TotalLinesSkipped);
        Assert.DoesNotContain(rows, r => r.ProductSymbol == "TOTAL");

        // 201 is the open-interest figure on the TOTAL line; nothing should carry it.
        Assert.DoesNotContain(rows, r => r.PriorInt == 201m && r.Settle is null);
    }

    // ---------------------------------------------------------------------
    // Tick notation
    // ---------------------------------------------------------------------

    /// <summary>
    /// STLAGS quotes eighths: 512'0 is 512.0 and 515'2 is 515.25. Reading the
    /// fraction against a 64ths denominator would give 512.0 and 515.03 — both
    /// plausible-looking and both wrong, which is why the denominator is keyed on
    /// the feed and asserted here.
    /// </summary>
    [Fact]
    public void AgsTicksAreEighths()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Ags, "STLAGS");

        var sep = rows.Future("00C", 2026, 9);
        Assert.Equal(515.25m, sep.Open);   // 515'2 = 515 + 2/8
        Assert.Equal(515.25m, sep.High);
        Assert.Equal(512.0m, sep.Settle);  // 512'0
        Assert.Equal(-3.25m, sep.PctChange); // -3'2 = -(3 + 2/8)
        Assert.Equal(5m, sep.EstVol);
        Assert.Equal(515.25m, sep.PriorSettle);

        var dec = rows.Future("00C", 2026, 12);
        Assert.Equal(536.75m, dec.Settle);      // 536'6
        Assert.Equal(-4.0m, dec.PctChange);     // -4'0
        Assert.Equal(540.75m, dec.PriorSettle); // 540'6

        Assert.True(stats.TickValues > 0);
        Assert.Equal(0, stats.TickAnomalies);
    }

    /// <summary>A tick value carrying an A/B indicator must yield both parts.</summary>
    [Fact]
    public void TickWithIndicatorYieldsBothTheNumberAndTheLetter()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Ags, "STLAGS");

        // JAN27 ... 534'2A in the LAST column
        var jan = rows.Future("00C", 2027, 1);
        Assert.Equal(534.25m, jan.Last);
        Assert.Equal("A", jan.LastABIndicator);
        Assert.Null(jan.Settle);
    }

    /// <summary>
    /// A feed with no tick convention must NOT convert a tick value under a guessed
    /// denominator — it stores NULL and counts the anomaly, because a wrongly
    /// scaled price is worse than a missing one.
    /// </summary>
    [Fact]
    public void TickInAFeedWithNoConventionIsNulledAndCounted()
    {
        // Same STLAGS bytes, but read as STLNYMEX, which quotes no ticks.
        var (rows, stats) = TestHelpers.Parse(Samples.Ags, "STLNYMEX");

        Assert.True(stats.TickAnomalies > 0);
        Assert.Null(rows.Future("00C", 2026, 9).Open);
    }

    [Theory]
    [InlineData("STLAGS", 8)]
    [InlineData("STLINT", 64)]
    [InlineData("STLNYMEX", 0)]
    [InlineData("STLCOMEX", 0)]
    public void TickConventionIsKeyedOnExchangeCode(string exchangeCode, int expectedDenominator)
    {
        var convention = CmeDescriptors.TickConventionFor(exchangeCode);

        if (expectedDenominator == 0)
            Assert.Null(convention);
        else
            Assert.Equal(expectedDenominator, convention!.Denominator);
    }

    // ---------------------------------------------------------------------
    // CAB
    // ---------------------------------------------------------------------

    /// <summary>
    /// CAB is a cabinet price — a real settlement marker with no numeric form. It
    /// must become NULL and be counted, never 0 (which would read as a real price
    /// of zero).
    /// </summary>
    [Fact]
    public void CabinetPricesBecomeNullAndAreCounted()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Ags, "STLAGS");

        var row = rows.Option("48", 221.000m, "C");
        Assert.Null(row.PriorSettle);
        Assert.NotEqual(0m, row.PriorSettle ?? -1m);

        // The rest of the row still loads.
        Assert.Equal(0m, row.Settle);
        Assert.Equal(16m, row.PriorVol);
        Assert.Equal(335m, row.PriorInt);

        Assert.Equal(2, stats.CabinetValues);
    }

    // ---------------------------------------------------------------------
    // BALMO / day-label futures
    // ---------------------------------------------------------------------

    /// <summary>
    /// The requester's decision: day-label futures rows are skipped, because
    /// arm.STLBASIC_Future has no day component in its key. They must be COUNTED,
    /// so the omission is auditable.
    /// </summary>
    [Fact]
    public void DayLabelFutureRowsAreSkippedAndCounted()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Cpc, "STLCPC");

        Assert.Equal(2, stats.DayLabelRowsSkipped);
        Assert.Empty(rows);
        Assert.Equal(0, stats.FutureRows);

        // Specifically: nothing was loaded under the header's own month, which is
        // what a naive implementation would do (collapsing both days onto one key).
        Assert.DoesNotContain(rows, r => r.ProductSymbol == "1D");
    }

    /// <summary>
    /// A bulletin whose every row was DELIBERATELY skipped is a legitimate empty
    /// read, not a layout change — so it must not throw. This fixture is entirely
    /// BALMO sections, which is exactly that case. The "no fact rows" guard only
    /// fires when the parser recognised nothing at all.
    /// </summary>
    [Fact]
    public void AnAllSkippedBulletinIsEmptyRatherThanAFormatError()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Cpc, "STLCPC");

        Assert.Empty(rows);
        Assert.Equal(0, stats.TotalRows);
        Assert.True(stats.DayLabelRowsSkipped > 0);
        Assert.Equal(0, stats.UnclassifiedLines);
    }

    /// <summary>
    /// The other side of that distinction: a bulletin the parser genuinely does not
    /// recognise still throws, so a format change can never be recorded as a
    /// successful load of zero rows.
    /// </summary>
    [Fact]
    public void ABulletinWithNothingRecognisedStillThrows()
    {
        var garbage = string.Join("\n", new[]
        {
            "        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)",
            "MTH/                       -------  DAILY  ------",
            "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE  ACTUAL VOL           SETT         VOL         INT",
            "0CJ TEST PLATINUM FUTURE",
            "this line is not a data row at all"
        });

        Assert.Throws<CmeBulletinFormatException>(() => TestHelpers.Parse(garbage));
    }

    /// <summary>
    /// A BALMO header still parses cleanly into symbol + description + month — the
    /// requester confirmed this reading explicitly. Only the ROWS are dropped.
    /// </summary>
    [Fact]
    public void BalmoHeaderStillParsesSymbolAndDescription()
    {
        var header = CmeBulletinParser.ParseProductHeader("1D SEP26 RBOB Gasoline BALMO Futures");

        Assert.Equal("1D", header.Symbol);
        Assert.Equal("RBOB Gasoline BALMO Futures", header.Description);
        Assert.False(header.IsOption);
        Assert.Equal(2026, header.ContractYear);
        Assert.Equal(9, header.ContractMonth);
    }

    // ---------------------------------------------------------------------
    // The two-line product header
    // ---------------------------------------------------------------------

    /// <summary>
    /// CME embeds a literal "&lt;br /&gt;" in a few product names, which pushes the
    /// description onto the next line. The continuation must be consumed as the
    /// description — not treated as a new product whose symbol is "E-mini".
    /// </summary>
    [Fact]
    public void TwoLineProductHeaderIsStitchedBackTogether()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Eqt, "STLEQT");

        Assert.Equal(1, stats.ContinuationLines);
        Assert.Equal(0, stats.UnclassifiedLines);

        Assert.All(rows, r =>
        {
            Assert.Equal("KYP11", r.ProductSymbol);
            Assert.Equal("E-mini S&P 500 Synthetic Future (11:00 A.M. ET )", r.ProductDescription);
        });

        Assert.DoesNotContain(rows, r => r.ProductSymbol == "E-mini");
        Assert.Equal(4, rows.Count);
        Assert.Equal(7715.75m, rows.Future("KYP11", 2026, 9).Settle);
    }

    // ---------------------------------------------------------------------
    // Header handling
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("STLNYMEX")] // POST-CLEARING / "ACTUAL VOL"
    [InlineData("STLAGS")]   // PRE-CLEARING  / "EST.VOL"
    public void BothHeaderDialectsAreAccepted(string exchangeCode)
    {
        var text = exchangeCode == "STLAGS" ? Samples.Ags : Samples.Nymex;

        var (rows, stats) = TestHelpers.Parse(text, exchangeCode);

        Assert.NotEmpty(rows);
        Assert.Contains("PRICES AS OF", stats.ReportHeader);
    }

    [Fact]
    public void ReportHeaderIsCapturedForTheAuditTrail()
    {
        var (_, stats) = TestHelpers.Parse(Samples.Nymex);

        Assert.Equal("FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)", stats.ReportHeader);
    }

    /// <summary>
    /// If CME ever widens a column, positional slicing would keep "working" while
    /// writing every value one field out of place. The column-header geometry check
    /// is the early warning, so it must actually fire.
    /// </summary>
    [Fact]
    public void ShiftedColumnHeaderIsRejected()
    {
        var shifted = string.Join("\n", new[]
        {
            "        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)",
            "MTH/                       -------  DAILY  ------",
            "STRIKE   OPEN   HIGH   LOW   LAST   SETT   CHGE   ACTUAL VOL   SETT   VOL   INT",
            "SEP26             ----         ----         ----         ----       1821.0         -7.4                     1828.4"
        });

        var ex = Assert.Throws<CmeBulletinFormatException>(() => TestHelpers.Parse(shifted));
        Assert.Contains("fixed-width geometry", ex.Message);
    }

    [Fact]
    public void EmptyBulletinThrows()
    {
        Assert.Throws<CmeBulletinFormatException>(() => TestHelpers.Parse("   \n  \n"));
    }

    /// <summary>A file with structure but no facts is a layout change, not a quiet success.</summary>
    [Fact]
    public void BulletinWithNoFactRowsThrows()
    {
        var headerOnly = string.Join("\n", Samples.Nymex.Split('\n').Take(4));

        var ex = Assert.Throws<CmeBulletinFormatException>(() => TestHelpers.Parse(headerOnly));
        Assert.Contains("no fact rows", ex.Message);
    }

    // ---------------------------------------------------------------------
    // Identity, ordinals and key integrity
    // ---------------------------------------------------------------------

    [Fact]
    public void FeedIdentityIsStampedOnEveryRow()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex, "STLNYMEX", new DateOnly(2026, 9, 4));

        Assert.All(rows, r =>
        {
            Assert.Equal("STLNYMEX", r.ExchangeCode);
            Assert.Equal("EOD", r.ProductCode);
            Assert.Equal(new DateOnly(2026, 9, 4), r.TradeDate);
        });
    }

    [Fact]
    public void RowOrdinalsAreSequentialFromOne()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        Assert.Equal(Enumerable.Range(1, rows.Count), rows.Select(r => r.RowOrdinal));
    }

    /// <summary>
    /// The supplied primary keys must actually be unique on real data — the whole
    /// merge design rests on it.
    /// </summary>
    [Fact]
    public void ParsedRowsHaveNoDuplicatePrimaryKeys()
    {
        foreach (var (text, exchange) in new[]
                 {
                     (Samples.Nymex, "STLNYMEX"), (Samples.Ags, "STLAGS"),
                     (Samples.Cpc, "STLCPC"), (Samples.Eqt, "STLEQT")
                 })
        {
            var (rows, _) = TestHelpers.Parse(text, exchange);

            var optionKeys = rows.Where(r => r.Kind == CmeRowKind.Option)
                .Select(r => (r.ExchangeCode, r.ProductCode, r.TradeDate, r.ProductSymbol,
                    r.ProductDescription, r.ContractYear, r.ContractMonth, r.PutCall, r.Strike))
                .ToList();

            var futureKeys = rows.Where(r => r.Kind == CmeRowKind.Future)
                .Select(r => (r.ExchangeCode, r.ProductCode, r.TradeDate, r.ProductSymbol,
                    r.ContractYear, r.ContractMonth))
                .ToList();

            Assert.Equal(optionKeys.Count, optionKeys.Distinct().Count());
            Assert.Equal(futureKeys.Count, futureKeys.Distinct().Count());
        }
    }

    /// <summary>
    /// ProductDescription is part of the OPTION key, and SQL Server promotes PK
    /// columns to NOT NULL — so it must never be null, even for a section whose
    /// description is genuinely absent.
    /// </summary>
    [Fact]
    public void KeyStringsAreNeverNull()
    {
        var (rows, _) = TestHelpers.Parse(Samples.Nymex);

        Assert.All(rows, r =>
        {
            Assert.NotNull(r.ExchangeCode);
            Assert.NotNull(r.ProductCode);
            Assert.NotNull(r.ProductSymbol);
            Assert.NotNull(r.ProductDescription);
            Assert.NotNull(r.PutCall);
        });
    }

    // ---------------------------------------------------------------------
    // Contract-month token
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("SEP26", 2026, 9)]
    [InlineData("JAN29", 2029, 1)]
    [InlineData("DEC99", 2099, 12)] // a far-dated test contract, seen live
    [InlineData("JUN14", 2014, 6)]  // Eris swap futures list past effective dates
    public void ContractMonthTokensMapTo2000PlusYy(string token, int year, int month)
    {
        Assert.True(CmeBulletinParser.TryParseContractMonth(token, out var y, out var m));
        Assert.Equal(year, y);
        Assert.Equal(month, m);
    }

    [Theory]
    [InlineData("XXX26")]  // not a month
    [InlineData("SEP2")]   // too short
    [InlineData("SEP266")] // too long
    [InlineData("SEPXY")]  // non-numeric year
    [InlineData("1540")]   // a strike
    public void NonContractMonthTokensAreRejected(string token)
    {
        Assert.False(CmeBulletinParser.TryParseContractMonth(token, out _, out _));
    }

    // ---------------------------------------------------------------------
    // Product-header parsing
    // ---------------------------------------------------------------------

    [Fact]
    public void FutureHeaderWithoutAMonthKeepsItsWholeDescription()
    {
        var header = CmeBulletinParser.ParseProductHeader("1CE CHICAGO ETHANOL 1 MTH SYN CAL SPD");

        Assert.Equal("1CE", header.Symbol);
        Assert.Equal("CHICAGO ETHANOL 1 MTH SYN CAL SPD", header.Description);
        Assert.False(header.IsOption);
        Assert.Equal(0, header.ContractMonth);
    }

    [Fact]
    public void OptionHeaderWithParenthesesAndMixedCaseIsPreserved()
    {
        var header = CmeBulletinParser.ParseProductHeader(
            "7A OCT26 Crude Oil Financial Calendar Spread Option (One Month) PUT");

        Assert.Equal("7A", header.Symbol);
        Assert.Equal("Crude Oil Financial Calendar Spread Option (One Month)", header.Description);
        Assert.True(header.IsOption);
        Assert.Equal("P", header.PutCall);
        Assert.Equal(10, header.ContractMonth);
    }

    /// <summary>
    /// CALL/PUT is matched as a trailing WHOLE WORD. A substring match would
    /// misread a description ending in a word that merely contains "PUT".
    /// </summary>
    [Fact]
    public void PutIsMatchedAsAWordNotASubstring()
    {
        var header = CmeBulletinParser.ParseProductHeader("ZZ1 Gross OUTPUT Futures");

        Assert.False(header.IsOption);
        Assert.Equal(string.Empty, header.PutCall);
        Assert.Equal("Gross OUTPUT Futures", header.Description);
    }

    /// <summary>
    /// CME pads product names inside a fixed-width field, so descriptions really do
    /// arrive with doubled spaces (962 of the sampled header lines have one).
    ///
    /// <para>
    /// This matters beyond tidiness: <c>ProductDescription</c> is part of the
    /// OPTION primary key, so spacing decides row identity — a re-padded name
    /// would key a second row for the same contract instead of updating the first.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("HC HEATING OIL CRK SPD  SYN FUT NYMEX", "HEATING OIL CRK SPD SYN FUT NYMEX")]
    [InlineData("HC HEATING OIL CRK SPD     SYN FUT", "HEATING OIL CRK SPD SYN FUT")]
    [InlineData("ZZ1  Leading And Trailing  Runs  ", "Leading And Trailing Runs")]
    [InlineData("ZZ1 Already Single Spaced", "Already Single Spaced")]
    public void RepeatedSpacesInTheDescriptionAreCollapsed(string header, string expected)
    {
        Assert.Equal(expected, CmeBulletinParser.ParseProductHeader(header).Description);
    }

    /// <summary>
    /// The rule must hold on the CONTINUATION path too, or the same product would
    /// key differently depending on whether CME split its name across two lines.
    /// </summary>
    [Fact]
    public void RepeatedSpacesAreCollapsedOnTheContinuationPathAsWell()
    {
        var text = string.Join("\n", new[]
        {
            "        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)",
            "MTH/                       -------  DAILY  ------",
            "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE  ACTUAL VOL           SETT         VOL         INT",
            "KYP11 <br />",
            "E-mini  S and P 500   Synthetic Future",
            "SEP26             ----         ----         ----         ----      7715.75        -1.75                    7717.50"
        });

        var (rows, stats) = TestHelpers.Parse(text, "STLEQT");

        Assert.Equal(1, stats.ContinuationLines);
        Assert.Equal("E-mini S and P 500 Synthetic Future", Assert.Single(rows).ProductDescription);
    }

    /// <summary>No description on any row, by either path, may contain a doubled space.</summary>
    [Fact]
    public void NoLoadedDescriptionEverContainsADoubledSpace()
    {
        foreach (var (text, exchange) in new[]
                 {
                     (Samples.Nymex, "STLNYMEX"), (Samples.Ags, "STLAGS"), (Samples.Eqt, "STLEQT")
                 })
        {
            var (rows, _) = TestHelpers.Parse(text, exchange);

            Assert.All(rows, r =>
            {
                Assert.DoesNotContain("  ", r.ProductDescription);
                Assert.Equal(r.ProductDescription.Trim(), r.ProductDescription);
            });
        }
    }

    [Fact]
    public void HtmlOnlyDescriptionSignalsAContinuation()
    {
        var header = CmeBulletinParser.ParseProductHeader("KYP11 <br />");

        Assert.Equal("KYP11", header.Symbol);
        Assert.Equal(string.Empty, header.Description);
        Assert.True(header.AwaitingDescription);
    }

    // ---------------------------------------------------------------------
    // Strict mode
    // ---------------------------------------------------------------------

    /// <summary>
    /// Strict mode exists so a deployment can choose to fail rather than skip. It
    /// must not fire on the fixtures that are legitimately clean.
    /// </summary>
    [Fact]
    public void StrictModeAcceptsCleanBulletins()
    {
        var (rows, stats) = TestHelpers.Parse(Samples.Nymex, "STLNYMEX", strict: true);

        Assert.NotEmpty(rows);
        Assert.Equal(0, stats.UnclassifiedLines);
    }

    [Fact]
    public void StrictModeStillAcceptsTheTwoLineHeaderBecauseItIsUnderstood()
    {
        // The <br /> artifact is HANDLED, not tolerated — so even strict mode loads
        // it. This pins that distinction.
        var (rows, stats) = TestHelpers.Parse(Samples.Eqt, "STLEQT", strict: true);

        Assert.Equal(4, rows.Count);
        Assert.Equal(1, stats.ContinuationLines);
    }

    // ---------------------------------------------------------------------
    // Slicing
    // ---------------------------------------------------------------------

    /// <summary>
    /// CME right-pads only as far as the last populated column, so a short line is
    /// normal and must not throw.
    /// </summary>
    [Fact]
    public void ShortLinesAreToleratedRatherThanThrowing()
    {
        var truncated = string.Join("\n", new[]
        {
            "        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)",
            "MTH/                       -------  DAILY  ------",
            "STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE  ACTUAL VOL           SETT         VOL         INT",
            "0CJ TEST PLATINUM FUTURE",
            "SEP26             ----         ----         ----         ----       1821.0"
        });

        var (rows, _) = TestHelpers.Parse(truncated);

        var row = Assert.Single(rows);
        Assert.Equal(1821.0m, row.Settle);
        Assert.Null(row.PriorInt);
    }

    [Fact]
    public void SliceTrimsAndHandlesOutOfRangeSpans()
    {
        Assert.Equal("AB", CmeBulletinParser.Slice("  AB  ", 1, 6));
        Assert.Equal(string.Empty, CmeBulletinParser.Slice("abc", 10, 20));
        Assert.Equal("b", CmeBulletinParser.Slice("abc", 2, 2));
    }
}
