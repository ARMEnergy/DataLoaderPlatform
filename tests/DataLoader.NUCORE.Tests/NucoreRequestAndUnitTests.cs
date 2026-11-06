using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.NUCORE.Tests;

/// <summary>
/// Request construction and work-unit enumeration - the two places where a vendor trap
/// turns into a wrong URL or a wrong resume key.
/// </summary>
public class NucoreRequestAndUnitTests
{
    private static NucoreSettings Settings() => new()
    {
        BaseUrl = "https://example.invalid",
        ApiKey = "test",
        ConnectionString = "Server=.;Database=NUCORE;Integrated Security=true",
        PageSize = 20_000,
        StartDate = "2026-10-01",
        SettledAfterDays = 3,
        SkipWeekends = true
    };

    private static NucoreSourceReader Reader(NucoreEndpoint endpoint, NucoreSettings settings) =>
        new(endpoint, new HttpClient(), settings, NullLogger.Instance);

    private static NucoreWorkUnit DateUnit(NucoreEndpoint endpoint, DateTime date) =>
        new() { Endpoint = endpoint, Date = date, IsHot = false, ResumeKey = "k" };

    private static NucoreWorkUnit TradeMonthUnit(NucoreEndpoint endpoint, string month) =>
        new() { Endpoint = endpoint, TradeMonth = month, IsHot = true, ResumeKey = "k" };

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        CancellationToken = CancellationToken.None
    };

    // ------------------------------------------------------------------ request URLs

    [Fact]
    public void VarResultsUsesVarDate()
    {
        var endpoint = NucoreDescriptors.Find("VarResults")!;
        var settings = Settings();
        var uri = Reader(endpoint, settings).BuildRequestUri(DateUnit(endpoint, new DateTime(2026, 10, 14)), 1);

        Assert.Contains("/Integration/api/risk/VarResults/Get", uri);
        Assert.Contains("VarDate=2026-10-14", uri);
        Assert.Contains("pageSize=20000", uri);
        Assert.Contains("page=1", uri);
    }

    /// <summary>
    /// âš  The URL must say CurDate. With systemDate the endpoint answers 200 and returns the
    /// latest run for every date asked for.
    /// </summary>
    [Fact]
    public void PnlExplainUrlUsesCurDateAndNeverSystemDate()
    {
        var endpoint = NucoreDescriptors.Find("PnlExplain")!;
        var uri = Reader(endpoint, Settings()).BuildRequestUri(DateUnit(endpoint, new DateTime(2026, 10, 14)), 1);

        Assert.Contains("CurDate=2026-10-14", uri);
        Assert.DoesNotContain("SystemDate", uri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// âš  PositionDetails must never carry a date parameter - it has none, and sending one
    /// would create the false impression that the response is filtered. It carries the trade
    /// month bounds and a DETERMINISTIC descending sort instead.
    /// </summary>
    [Fact]
    public void PositionDetailsUrlCarriesTradeMonthBoundsAndADescendingTotalOrder()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var uri = Reader(endpoint, Settings()).BuildRequestUri(TradeMonthUnit(endpoint, "202601"), 2);

        Assert.Contains("TradeMonthFrom=202601", uri);
        Assert.Contains("TradeMonthTo=202601", uri);
        Assert.Contains("page=2", uri);

        // Leading -systemDate is what lets the watermark stop the unit early; the trailing
        // key columns make the order TOTAL, so rows cannot shuffle between pages.
        Assert.Contains("SortFields=-systemDate,tradeId,detailId,tradeMonth", uri);

        Assert.DoesNotContain("SystemDate=", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VarDate=", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CurDate=", uri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// âš  Not the 500,000 the endpoints were originally specified with: PositionDetails
    /// answers HTTP 504 at that page size, because one unfiltered call is 2,468,453 rows.
    /// </summary>
    /// <summary>
    /// ⚠ SettlementPublicationIndexPrices takes a DATETIME range, and the times are what
    /// make it a day.
    ///
    /// <para>
    /// A date-only bound selects the midnight INSTANT:
    /// <c>LocalTimeFrom=2026-10-15&amp;LocalTimeTo=2026-10-15</c> returns 56 rows where the
    /// real day holds 1,823. And <c>LocalTimeTo</c> is INCLUSIVE, so the upper bound must be
    /// 23:59:59 rather than the next midnight - <c>From=15, To=16</c> returns 1,879 rows
    /// spanning two days, swallowing day 16's first row into day 15's unit.
    /// </para>
    /// <para>
    /// With these bounds the split is lossless: 1,823 + 1,823 = 3,646, exactly the two-day
    /// window.
    /// </para>
    /// </summary>
    [Fact]
    public void SettlementPricesAskForAWholeDayAndNotTheMidnightInstant()
    {
        var endpoint = NucoreDescriptors.Find("SettlementPublicationIndexPrices")!;
        var uri = Reader(endpoint, Settings())
            .BuildRequestUri(DateUnit(endpoint, new DateTime(2026, 10, 15)), 1);

        Assert.Contains("/Integration/api/risk/SettlementPublicationIndexPrices/Get", uri);
        Assert.Contains("LocalTimeFrom=2026-10-15T00:00:00", uri);
        Assert.Contains("LocalTimeTo=2026-10-15T23:59:59", uri);

        // The next midnight would double-count day 16's first row.
        Assert.DoesNotContain("LocalTimeTo=2026-10-16", uri);

        // A bare date on either bound is the 56-rows-instead-of-1,823 bug.
        Assert.DoesNotContain("LocalTimeFrom=2026-10-15&", uri);
    }

    /// <summary>
    /// ProductSpecCurves takes a From/To pair over whole dates, both bounds inclusive, and
    /// one unit asks for exactly one day. Leaving the bounds off does NOT default to the
    /// current system date as the vendor's spec claims - it returns all 10,551,909 rows.
    /// </summary>
    [Fact]
    public void ProductSpecCurvesBoundsOneSystemDateOnBothEnds()
    {
        var endpoint = NucoreDescriptors.Find("ProductSpecCurves")!;
        var uri = Reader(endpoint, Settings())
            .BuildRequestUri(DateUnit(endpoint, new DateTime(2026, 10, 15)), 1);

        Assert.Contains("/Integration/api/risk/ProductSpecCurves/Get", uri);
        Assert.Contains("SystemDateFrom=2026-10-15", uri);
        Assert.Contains("SystemDateTo=2026-10-15", uri);
    }

    /// <summary>
    /// The four endpoints taking a single date must keep taking a single date - rendering
    /// one of them as a From/To pair would send a parameter the vendor does not know, and
    /// an unknown filter on this API is ignored rather than rejected.
    /// </summary>
    [Fact]
    public void SingleDateEndpointsSendExactlyOneDateParameter()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.DateParamStyle != NucoreDateParamStyle.SingleDate) continue;

            var uri = Reader(endpoint, Settings())
                .BuildRequestUri(DateUnit(endpoint, new DateTime(2026, 10, 14)), 1);

            Assert.Contains($"{endpoint.DateParam}=2026-10-14", uri);
            Assert.DoesNotContain($"{endpoint.DateParam}From=", uri);
            Assert.DoesNotContain($"{endpoint.DateParam}To=", uri);
        }
    }

    [Fact]
    public void DefaultPageSizeIsWellBelowTheSizeThatTimesOut()
    {
        Assert.True(new NucoreSettings().PageSize <= 50_000);
    }

    /// <summary>
    /// ⚠ EVERY request carries SortFields, not just the paged trade-month one.
    ///
    /// Paging this API unsorted is not stable: pages 1 and 2 of one Volatilities day shared
    /// 111 rows unsorted and 0 sorted, which also means the unsorted read was MISSING ~111
    /// rows of that day. The loader originally sent SortFields only for PositionDetails, so
    /// every other endpoint needing more than one page was silently lossy.
    /// </summary>
    [Fact]
    public void EveryEndpointSendsSortFieldsOnEveryRequest()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint.SortFields),
                $"{endpoint.Name} declares no SortFields.");

            var unit = endpoint.Partition == NucorePartition.TradeMonth
                ? TradeMonthUnit(endpoint, "202601")
                : DateUnit(endpoint, new DateTime(2026, 10, 14));

            var uri = Reader(endpoint, Settings()).BuildRequestUri(unit, 1);

            Assert.Contains($"SortFields={endpoint.SortFields}", uri);
        }
    }

    /// <summary>
    /// The sort must be as close to a total order as the payload allows, or ties can still
    /// straddle a page boundary. For a merge endpoint that means the whole primary key.
    /// </summary>
    [Fact]
    public void MergeEndpointsSortByTheirWholePrimaryKey()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.WriteMode != NucoreWriteMode.Merge) continue;

            var sorted = endpoint.SortFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.TrimStart('-'))
                .ToList();

            foreach (var key in endpoint.KeyColumns)
                Assert.True(
                    sorted.Contains(key.JsonName, StringComparer.OrdinalIgnoreCase),
                    $"{endpoint.Name} sorts by [{endpoint.SortFields}], which omits key column '{key.JsonName}'.");
        }
    }

    /// <summary>
    /// PositionDetails must sort DESCENDING by system date, or the watermark cannot end the
    /// unit early and every run re-reads the trade month's entire history.
    /// </summary>
    [Fact]
    public void PositionDetailsSortsNewestSystemDateFirst()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        Assert.StartsWith("-systemDate", endpoint.SortFields, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- work units

    [Fact]
    public async Task WeekendsAreNotEnumerated()
    {
        // NUCORE publishes on business days only; weekends answer 200 with zero rows.
        var endpoint = NucoreDescriptors.Find("VarResults")!;
        var settings = Settings();
        settings.StartDate = "2026-10-05";   // Monday

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
            .GetWorkUnitsAsync(Context());

        Assert.All(units, u => Assert.True(
            u.Date!.Value.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            $"{u.Date:yyyy-MM-dd} is a {u.Date!.Value.DayOfWeek}"));
    }

    [Fact]
    public async Task WeekendsAreEnumeratedWhenTheSettingIsOff()
    {
        var endpoint = NucoreDescriptors.Find("VarResults")!;
        var settings = Settings();
        settings.SkipWeekends = false;
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-10));

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
            .GetWorkUnitsAsync(Context());

        Assert.Contains(units, u => u.Date!.Value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>
    /// ⚠ The regression guard for a silent two-sevenths data loss.
    ///
    /// SettlementPublicationIndexPrices publishes a FULL day of prices on Saturdays and
    /// Sundays (1,823 rows each, same as a weekday). SkipWeekends is on by default and is
    /// correct for every other endpoint, so this one has to opt out - and if it ever stops
    /// opting out, nothing fails: the units simply are not enumerated, no request is made,
    /// and the missing days look exactly like days the vendor had no data for.
    /// </summary>
    [Fact]
    public async Task WeekendsAreStillEnumeratedForTheEndpointThatPublishesOnThem()
    {
        var endpoint = NucoreDescriptors.Find("SettlementPublicationIndexPrices")!;
        var settings = Settings();
        settings.SkipWeekends = true;                 // the platform default, deliberately on
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-10));

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(Context());

        Assert.Contains(units, u => u.Date!.Value.DayOfWeek is DayOfWeek.Saturday);
        Assert.Contains(units, u => u.Date!.Value.DayOfWeek is DayOfWeek.Sunday);
    }

    /// <summary>
    /// Day-ahead prices mean tomorrow already has rows, so the window must run past today.
    /// Stopping at today would pick every day up exactly one day late, forever.
    /// </summary>
    [Fact]
    public async Task TheForwardLookingEndpointEnumeratesPastToday()
    {
        var endpoint = NucoreDescriptors.Find("SettlementPublicationIndexPrices")!;
        var settings = Settings();
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-3));

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(Context());

        var latest = units.Max(u => u.Date!.Value);
        Assert.True(latest > NucoreTime.Today(),
            $"latest unit is {latest:yyyy-MM-dd}, which is not past today.");
        Assert.Equal(NucoreTime.Today().AddDays(endpoint.DaysAhead), latest);
    }

    /// <summary>
    /// Every other endpoint stops at today; only a declared forward-looking one runs past it.
    /// </summary>
    [Fact]
    public async Task EndpointsThatReportOnClosedDaysStopAtToday()
    {
        foreach (var endpoint in NucoreDescriptors.All)
        {
            if (endpoint.Partition != NucorePartition.Date || endpoint.DaysAhead > 0) continue;

            var settings = Settings();
            settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-10));

            var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
                .GetWorkUnitsAsync(Context());

            Assert.All(units, u => Assert.True(u.Date!.Value <= NucoreTime.Today(),
                $"{endpoint.Name} enumerated {u.Date:yyyy-MM-dd}, past today."));
        }
    }

    /// <summary>
    /// ⚠ SettledAfterDays may be raised by the operator but never lowered past what an
    /// endpoint's own restatement behaviour requires. Settlement prices are corrected for up
    /// to 21 days; a settled unit is skipped with no request at all, so an over-eager
    /// setting here would discard three weeks of corrections silently.
    /// </summary>
    [Fact]
    public async Task AnEndpointsRestatementFloorOverridesATooNarrowGlobalSetting()
    {
        var endpoint = NucoreDescriptors.Find("SettlementPublicationIndexPrices")!;
        var settings = Settings();
        settings.SettledAfterDays = 1;                 // far too narrow for this endpoint
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-40));

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(Context());

        // A day 10 back is well inside the 21-day restatement horizon and must still be hot.
        var tenDaysBack = units.Single(u => u.Date == NucoreTime.Today().AddDays(-10));
        Assert.True(tenDaysBack.IsHot,
            "a day inside the restatement horizon settled because the global setting was lower.");

        // A day well outside it should settle, or every run re-reads unbounded history.
        var longAgo = units.Single(u => u.Date == NucoreTime.Today().AddDays(-35));
        Assert.False(longAgo.IsHot);
    }

    /// <summary>
    /// The floor only raises. A global setting WIDER than an endpoint's own minimum must
    /// still win, or raising it would silently do nothing for these endpoints.
    /// </summary>
    [Fact]
    public async Task AWiderGlobalSettingStillWidensTheHotWindow()
    {
        var endpoint = NucoreDescriptors.Find("ProductSpecCurves")!;
        var settings = Settings();
        settings.SettledAfterDays = 30;
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-40));

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(Context());

        // ProductSpecCurves skips weekends, so "20 days back" must land on a weekday or this
        // test fails two days in seven (it did on 2026-11-06, when -20 was a Saturday).
        var target = NucoreTime.Today().AddDays(-20);
        while (target.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            target = target.AddDays(-1);
        var twentyBack = units.Single(u => u.Date == target);
        Assert.True(twentyBack.IsHot);
    }

    /// <summary>
    /// A per-endpoint start date overrides the loader-wide one, which is how the two months
    /// of history before the shipped 2025-01-01 floor can be picked up without disturbing
    /// the five endpoints that have nothing there.
    /// </summary>
    [Fact]
    public async Task APerEndpointStartDateOverridesTheLoaderWideOne()
    {
        var endpoint = NucoreDescriptors.Find("ProductSpecCurves")!;
        var settings = Settings();
        settings.StartDate = "2026-10-01";
        settings.StartDateByEndpoint["ProductSpecCurves"] = "2026-09-20";

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(Context());

        Assert.Equal(new DateTime(2026, 9, 21), units.Min(u => u.Date!.Value));  // 9/20 is a Sunday
    }

    [Fact]
    public async Task RecentDatesAreHotAndOlderOnesAreSettled()
    {
        var endpoint = NucoreDescriptors.Find("VarResults")!;
        var settings = Settings();
        settings.SkipWeekends = false;
        settings.SettledAfterDays = 3;
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-20));

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
            .GetWorkUnitsAsync(Context());

        var today = NucoreTime.Today();
        foreach (var unit in units)
        {
            var expectedHot = unit.Date!.Value > today.AddDays(-3);
            Assert.Equal(expectedHot, unit.IsHot);
        }
    }

    [Fact]
    public async Task SettledKeysAreStableAcrossRunsAndHotKeysAreNot()
    {
        var endpoint = NucoreDescriptors.Find("VarResults")!;
        var settings = Settings();
        settings.SkipWeekends = false;
        settings.HotKeyStrategy = NucoreHotKeyStrategy.RunId;
        settings.StartDate = NucoreTime.FormatDate(NucoreTime.Today().AddDays(-20));

        var provider = new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance);
        var first = await provider.GetWorkUnitsAsync(Context());
        var second = await provider.GetWorkUnitsAsync(Context());

        foreach (var (a, b) in first.Zip(second))
        {
            Assert.Equal(a.Date, b.Date);

            if (a.IsHot)
                // A hot unit must get a different key each run or a re-pull is suppressed.
                Assert.NotEqual(a.Key, b.Key);
            else
                // A settled unit's key must be stable or it is re-read forever.
                Assert.Equal(a.Key, b.Key);
        }
    }

    [Fact]
    public async Task TradeMonthUnitsCoverThePastFloorAndTheFuture()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var settings = Settings();
        settings.TradeMonthFrom = "202501";
        settings.TradeMonthsAhead = 12;

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
            .GetWorkUnitsAsync(Context());

        Assert.Contains(units, u => u.TradeMonth == "202501");
        Assert.All(units, u => Assert.NotNull(u.TradeMonth));

        // Live data reaches trade month 2031, so the window must extend past today.
        var today = NucoreTime.Today();
        Assert.Contains(units, u => u.TradeMonth == today.AddMonths(12).ToString("yyyyMM"));
    }

    [Fact]
    public async Task EveryFutureTradeMonthIsHot()
    {
        // A future trade month gains rows every business day as new system dates land, so it
        // can never be settled.
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var settings = Settings();
        settings.SettledTradeMonthsBack = 3;

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
            .GetWorkUnitsAsync(Context());

        var currentMonth = NucoreTime.Today().ToString("yyyyMM");
        foreach (var unit in units.Where(u => string.CompareOrdinal(u.TradeMonth, currentMonth) >= 0))
            Assert.True(unit.IsHot, $"trade month {unit.TradeMonth} should be hot.");
    }

    [Fact]
    public async Task OldTradeMonthsAreSettled()
    {
        var endpoint = NucoreDescriptors.Find("PositionDetails")!;
        var settings = Settings();
        settings.TradeMonthFrom = "202501";
        settings.SettledTradeMonthsBack = 3;

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
            .GetWorkUnitsAsync(Context());

        var cutoff = NucoreTime.Today().AddMonths(-3).ToString("yyyyMM");
        foreach (var unit in units.Where(u => string.CompareOrdinal(u.TradeMonth, cutoff) < 0))
            Assert.False(unit.IsHot, $"trade month {unit.TradeMonth} should be settled.");
    }

    [Fact]
    public async Task AnExplicitRunWindowOverridesConfiguration()
    {
        var endpoint = NucoreDescriptors.Find("VarResults")!;
        var settings = Settings();
        settings.SkipWeekends = false;

        var context = new LoaderRunContext
        {
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            DateFrom = new DateTime(2026, 3, 2),
            DateTo = new DateTime(2026, 3, 4),
            CancellationToken = CancellationToken.None
        };

        var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(context);

        Assert.Equal(3, units.Count);
        Assert.Equal(new DateTime(2026, 3, 2), units[0].Date);
        Assert.Equal(new DateTime(2026, 3, 4), units[^1].Date);
    }

    [Fact]
    public async Task WorkUnitKeysAreUniqueWithinAnEndpoint()
    {
        // Two units sharing a key would make the load log treat them as the same work, and
        // the second would be skipped forever.
        foreach (var endpoint in NucoreDescriptors.All)
        {
            var settings = Settings();
            settings.StartDate = "2026-09-01";

            var units = await new NucoreWorkUnitProvider(endpoint, settings, NullLogger.Instance)
            
                .GetWorkUnitsAsync(Context());

            Assert.Equal(units.Count, units.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
        }
    }
}
