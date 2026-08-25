using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.ModernCommodities.Tests;

/// <summary>
/// The window arithmetic (<see cref="ModComTime"/>) and the resume key / chunking behaviour of
/// <see cref="ModComWorkUnitProvider"/> - the loader idempotency contract.
///
/// <para><b>Two facts are pinned to exact dates on purpose:</b></para>
/// <list type="bullet">
///   <item><b>D13: the window is INCLUSIVE at both ends, so <c>DaysBack = 30</c> spans 31 calendar
///     days.</b> That extra day is wanted (our <c>endDate</c> is UTC today while the vendor clock
///     runs behind UTC, and merge-by-PK makes an overlapping day free). A future "fix" to 30 days
///     must fail loudly here.</item>
///   <item><b>D6: the history clamp is <c>today.AddMonths(-6).AddDays(1)</c> - CALENDAR months,
///     never a day count.</b> The tests below straddle month boundaries where a 183-day arithmetic
///     gives a different answer, so the two cannot be confused.</item>
/// </list>
///
/// <para><b>There is NO settled resume zone and none may be added</b>: the trades window filters on
/// <c>Last Updated Timestamp</c> and a trade can be cancelled or restated months later, so a stable
/// key would freeze it at its pre-revision state forever while the loader reported clean runs. Every
/// key therefore carries <c>:run={hot}</c>.</para>
/// </summary>
public class WindowAndResumeKeyTests
{
    private static readonly Guid FixedRunId = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private static LoaderRunContext Context(DateTime startedAtUtc, Guid? runId = null) => new()
    {
        RunId = runId ?? FixedRunId,
        StartedAtUtc = startedAtUtc,
        CancellationToken = CancellationToken.None
    };

    private static DateTime Utc(int y, int mo, int d, int h = 13, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static ModComWorkUnitProvider Provider(
        ModComEndpointDescriptor descriptor, ModComSettings settings) =>
        new(descriptor, settings, NullLogger.Instance);

    private static IReadOnlyList<ModComWorkUnit> Units(
        ModComEndpointDescriptor descriptor, ModComSettings settings, LoaderRunContext context) =>
        Provider(descriptor, settings).GetWorkUnitsAsync(context).GetAwaiter().GetResult();

    // ============================================================ D13: the inclusive 31-day window

    [Fact]
    public void DaysBack30_YieldsTheINCLUSIVE31DayWindow_WithExactDates()
    {
        var window = ModComTime.ResolveWindow(Utc(2026, 8, 24), daysBack: 30, historyLimited: true);

        Assert.Equal(new DateOnly(2026, 8, 24), window.Today);
        Assert.Equal(new DateOnly(2026, 8, 24), window.End);       // endDate = UTC today
        Assert.Equal(new DateOnly(2026, 7, 25), window.Start);     // endDate - 30 days, LITERALLY
        Assert.Equal(31, window.LengthDays);                       // *** 31, not 30 - decision D13 ***
        Assert.False(window.Clamped);
        Assert.Equal(30, window.DaysBack);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(7, 8)]
    [InlineData(30, 31)]
    [InlineData(60, 61)]
    public void TheWindowLengthIsAlwaysDaysBackPlusOne(int daysBack, int expectedLength)
    {
        var window = ModComTime.ResolveWindow(Utc(2026, 8, 24), daysBack, historyLimited: false);
        Assert.Equal(expectedLength, window.LengthDays);
        Assert.Equal(window.End.AddDays(-daysBack), window.Start);
    }

    [Fact]
    public void ANegativeDaysBack_IsClampedToZero_NotToAnInvertedWindow()
    {
        var window = ModComTime.ResolveWindow(Utc(2026, 8, 24), daysBack: -5, historyLimited: true);

        Assert.Equal(window.End, window.Start);
        Assert.Equal(1, window.LengthDays);
        Assert.Equal(0, window.DaysBack);
    }

    [Fact]
    public void TheWindowUsesTheUtcCalendarDate_NotTheLocalOne()
    {
        // 23:30 UTC on the 24th is already the 25th in some zones and still the 24th in others; the
        // window is defined by the UTC date alone (decision D5).
        Assert.Equal(new DateOnly(2026, 8, 24),
            ModComTime.ResolveWindow(Utc(2026, 8, 24, 23, 30), 30, true).Today);
        Assert.Equal(new DateOnly(2026, 8, 25),
            ModComTime.ResolveWindow(Utc(2026, 8, 25, 0, 5), 30, true).Today);
    }

    // ============================================================ D6: the CALENDAR-month clamp

    [Fact]
    public void TheHistoryFloorIsSixCALENDARMonthsBackPlusOneDay()
    {
        Assert.Equal(new DateOnly(2026, 2, 25), ModComTime.HistoryFloor(new DateOnly(2026, 8, 24)));
    }

    [Fact]
    public void ADeepDaysBack_IsClampedForTheHistoryLimitedEndpoints_ToTheCalendarFloor()
    {
        var window = ModComTime.ResolveWindow(Utc(2026, 8, 24), daysBack: 3650, historyLimited: true);

        Assert.True(window.Clamped);
        Assert.Equal(new DateOnly(2026, 2, 25), window.Start);   // today.AddMonths(-6).AddDays(1)
        Assert.Equal(new DateOnly(2026, 8, 24), window.End);
    }

    [Fact]
    public void TheClampIsCalendarMonthBased_ProvedAcrossTheFebruaryBoundary()
    {
        // today = 2026-08-31. AddMonths(-6) MONTH-END-CLAMPS to 2026-02-28 (February is short and
        // 2026 is not a leap year) rather than overflowing into March, so the floor is 2026-03-01.
        // A fixed day count lands elsewhere - except for 183 days, which coincides at THIS date,
        // which is precisely why the second test (from 2026-03-01) exists.
        var today = new DateOnly(2026, 8, 31);

        var floor = ModComTime.HistoryFloor(today);
        Assert.Equal(new DateOnly(2026, 2, 28), today.AddMonths(-6));   // month-end clamping
        Assert.Equal(new DateOnly(2026, 3, 1), floor);
        Assert.NotEqual(today.AddDays(-180), floor);
        Assert.NotEqual(today.AddDays(-182), floor);
        Assert.NotEqual(today.AddDays(-184), floor);

        var window = ModComTime.ResolveWindow(Utc(2026, 8, 31), daysBack: 3650, historyLimited: true);
        Assert.True(window.Clamped);
        Assert.Equal(new DateOnly(2026, 3, 1), window.Start);
    }

    [Fact]
    public void TheClampIsCalendarMonthBased_ProvedFromTheFirstOfMarch()
    {
        // today = 2026-03-01 -> AddMonths(-6) = 2025-09-01 -> floor 2025-09-02. A day-count clamp
        // would land in AUGUST 2025 and be rejected by the API.
        var today = new DateOnly(2026, 3, 1);

        var floor = ModComTime.HistoryFloor(today);
        Assert.Equal(new DateOnly(2025, 9, 2), floor);
        Assert.NotEqual(today.AddDays(-183), floor);
        Assert.Equal(9, floor.Month);      // September, NOT August

        var window = ModComTime.ResolveWindow(Utc(2026, 3, 1), daysBack: 3650, historyLimited: true);
        Assert.Equal(new DateOnly(2025, 9, 2), window.Start);
    }

    [Fact]
    public void MyTradesIsNOTClamped_BecauseItsHistoryIsAllTime()
    {
        var window = ModComTime.ResolveWindow(Utc(2026, 8, 24), daysBack: 3650, historyLimited: false);

        Assert.False(window.Clamped);
        Assert.Equal(new DateOnly(2016, 8, 26), window.Start);                  // 3,650 days back
        Assert.Equal(new DateOnly(2026, 8, 24).AddDays(-3650), window.Start);
        Assert.Equal(3651, window.LengthDays);
    }

    [Fact]
    public void TheDescriptorsCarryTheRightHistoryFlags()
    {
        Assert.True(ModComDescriptors.AllTrades.HistoryLimited);
        Assert.True(ModComDescriptors.Settlements.HistoryLimited);
        Assert.False(ModComDescriptors.MyTrades.HistoryLimited);     // all-time
        Assert.True(ModComDescriptors.MyTrades.SupportsLegalEntityName);
        Assert.False(ModComDescriptors.AllTrades.SupportsLegalEntityName);
        Assert.False(ModComDescriptors.Settlements.SupportsLegalEntityName);
        Assert.Equal(10_000, ModComDescriptors.AllTrades.RowCap);
        Assert.Equal(10_000, ModComDescriptors.MyTrades.RowCap);
        Assert.Equal(100_000, ModComDescriptors.Settlements.RowCap);
    }

    [Fact]
    public void ADeepBackfillIsClampedOnAllTradesButNotOnMyTrades_ThroughTheProvider()
    {
        var settings = ReaderHarness.Settings(daysBack: 3650);
        var context = Context(Utc(2026, 8, 24));

        var allTrades = Assert.Single(Units(ModComDescriptors.AllTrades, settings, context));
        var myTrades = Assert.Single(Units(ModComDescriptors.MyTrades, settings, context));

        Assert.Equal(new DateOnly(2026, 2, 25), allTrades.WindowStart);   // clamped to the 6-month floor
        Assert.Equal(new DateOnly(2016, 8, 26), myTrades.WindowStart);    // all-time: NOT clamped
    }

    [Fact]
    public void ResolveWindow_NeverProducesAnInvertedRange_AcrossAWideInputSweep()
    {
        // An inverted range is answered by the API with a SILENT header-only 200, so a window bug
        // would load zero rows forever with no error at all. ResolveWindow therefore throws on
        // start > end; this sweep documents that no legal (daysBack, date, clamp) combination can
        // reach that throw - the guard is defensive, and the reader asserts it again per request
        // (see StatusMatrixTests.AnInvertedWindow_FailsLoudlyBEFOREAnyRequestIsSent).
        foreach (var daysBack in new[] { 0, 1, 30, 31, 60, 183, 365, 3650 })
        foreach (var date in new[]
                 {
                     Utc(2026, 1, 1), Utc(2026, 2, 28), Utc(2026, 3, 1), Utc(2026, 8, 24),
                     Utc(2026, 8, 31), Utc(2026, 12, 31), Utc(2028, 2, 29)
                 })
        foreach (var limited in new[] { true, false })
        {
            var window = ModComTime.ResolveWindow(date, daysBack, limited);
            Assert.True(window.Start <= window.End, $"{date:O} daysBack={daysBack} limited={limited}");
            Assert.True(window.LengthDays >= 1);
            Assert.Equal(DateOnly.FromDateTime(date), window.End);
        }
    }

    // ============================================================ chunking

    [Fact]
    public void ChunkDaysZero_IsOneRequestForTheWholeWindow()
    {
        var units = Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(chunkDays: 0), Context(Utc(2026, 8, 24)));

        var unit = Assert.Single(units);
        Assert.Equal(new DateOnly(2026, 7, 25), unit.WindowStart);
        Assert.Equal(new DateOnly(2026, 8, 24), unit.WindowEnd);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ANonPositiveChunkDays_IsAlsoOneRequest(int chunkDays)
    {
        Assert.Single(Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(chunkDays: chunkDays), Context(Utc(2026, 8, 24))));
    }

    [Fact]
    public void ChunkDays10_SplitsThe31DayWindowIntoFourContiguousChunks_WithExactBoundaries()
    {
        var units = Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(chunkDays: 10), Context(Utc(2026, 8, 24)));

        Assert.Equal(4, units.Count);
        Assert.Equal(
            new[]
            {
                (new DateOnly(2026, 7, 25), new DateOnly(2026, 8, 3)),
                (new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 13)),
                (new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 23)),
                (new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 24))   // the 31st day, alone
            },
            units.Select(u => (u.WindowStart, u.WindowEnd)).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(60)]
    public void EveryChunkSize_CoversTheWindowExactlyOnce_NoGapNoOverlap(int chunkDays)
    {
        var context = Context(Utc(2026, 8, 24));
        var window = ModComTime.ResolveWindow(context.StartedAtUtc, 30, historyLimited: true);
        var units = Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(chunkDays: chunkDays), context);

        Assert.NotEmpty(units);
        Assert.Equal(window.Start, units[0].WindowStart);                     // starts AT the window
        Assert.Equal(window.End, units[^1].WindowEnd);                       // ends AT the window

        for (var i = 0; i < units.Count; i++)
        {
            Assert.True(units[i].WindowStart <= units[i].WindowEnd, "a chunk must not be inverted");
            Assert.True(units[i].WindowEnd.DayNumber - units[i].WindowStart.DayNumber + 1 <= chunkDays,
                "a chunk must not exceed ChunkDays inclusive days");
            if (i > 0)
                // The next chunk starts the DAY AFTER: both ends are inclusive, so a shared boundary
                // date would double-request rows and make both the row-cap arithmetic and the
                // FileLog counts lie.
                Assert.Equal(units[i - 1].WindowEnd.AddDays(1), units[i].WindowStart);
        }

        var covered = units.Sum(u => u.WindowEnd.DayNumber - u.WindowStart.DayNumber + 1);
        Assert.Equal(window.LengthDays, covered);                            // exactly 31 days, once

        // ... and every calendar day of the window is claimed by exactly one chunk.
        var days = Enumerable.Range(0, window.LengthDays).Select(i => window.Start.AddDays(i));
        foreach (var day in days)
            Assert.Single(units, u => u.WindowStart <= day && day <= u.WindowEnd);
    }

    [Fact]
    public void ChunkDaysOne_ProducesOneSingleDayUnitPerCalendarDay()
    {
        var units = Units(ModComDescriptors.Settlements, ReaderHarness.Settings(chunkDays: 1), Context(Utc(2026, 8, 24)));

        Assert.Equal(31, units.Count);
        Assert.All(units, u => Assert.Equal(u.WindowStart, u.WindowEnd));
        Assert.Equal(units.Select(u => u.Key).Distinct().Count(), units.Count);   // 31 distinct keys
    }

    [Fact]
    public void AChunkSizeWiderThanTheWindow_IsClippedToTheWindowEnd()
    {
        var units = Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(chunkDays: 1000), Context(Utc(2026, 8, 24)));

        var unit = Assert.Single(units);
        Assert.Equal(new DateOnly(2026, 8, 24), unit.WindowEnd);
    }

    [Fact]
    public void PerEndpointOverrides_BeatTheGlobals_AndAreMatchedCaseInsensitively()
    {
        var settings = ReaderHarness.Settings(daysBack: 30, chunkDays: 0, endpoints: new()
        {
            ["alltrades"] = new ModComEndpointOverride { DaysBack = 9, ChunkDays = 5 }
        });
        var context = Context(Utc(2026, 8, 24));

        var allTrades = Units(ModComDescriptors.AllTrades, settings, context);
        Assert.Equal(2, allTrades.Count);                                  // 10 inclusive days / 5
        Assert.Equal(new DateOnly(2026, 8, 15), allTrades[0].WindowStart);

        // The other endpoints keep the globals.
        var settlements = Assert.Single(Units(ModComDescriptors.Settlements, settings, context));
        Assert.Equal(new DateOnly(2026, 7, 25), settlements.WindowStart);
    }

    // ============================================================ the resume key

    [Fact]
    public void TheKeyFormatIsPinned_AndAlwaysCarriesTheHotRunToken()
    {
        var unit = Assert.Single(Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(), Context(Utc(2026, 8, 24, 13, 5))));

        Assert.Equal("modcom:AllTrades:20260725-20260824:run=2026082413", unit.Key);
        Assert.Equal("2026082413", unit.RunToken);
        Assert.Equal("AllTrades 2026-07-25..2026-08-24", unit.DisplayName);
    }

    [Fact]
    public void TwoRunsInTheSameUtcHour_ProduceTheIDENTICALKey_SoTheSecondIdempotentlySkips()
    {
        var settings = ReaderHarness.Settings();

        var early = Assert.Single(Units(ModComDescriptors.AllTrades, settings, Context(Utc(2026, 8, 24, 13, 0))));
        var late = Assert.Single(Units(ModComDescriptors.AllTrades, settings, Context(Utc(2026, 8, 24, 13, 59), Guid.NewGuid())));

        Assert.Equal(early.Key, late.Key);       // same hour, DIFFERENT run id -> same key
    }

    [Fact]
    public void TheNextUtcHour_ProducesADIFFERENTKey_SoTheWindowIsRePulled()
    {
        var settings = ReaderHarness.Settings();

        var thirteen = Assert.Single(Units(ModComDescriptors.AllTrades, settings, Context(Utc(2026, 8, 24, 13, 59))));
        var fourteen = Assert.Single(Units(ModComDescriptors.AllTrades, settings, Context(Utc(2026, 8, 24, 14, 0))));

        Assert.NotEqual(thirteen.Key, fourteen.Key);
        Assert.EndsWith(":run=2026082414", fourteen.Key);
    }

    [Fact]
    public void RunHourIsTheDefaultStrategy_AndTheTokenIsUtcYyyyMMddHH()
    {
        Assert.Equal(ModComHotKeyStrategy.RunHour, new ModComSettings().HotKeyStrategy);
        Assert.Equal("2026082413", ModComTime.HotToken(ModComHotKeyStrategy.RunHour, Context(Utc(2026, 8, 24, 13, 44))));
        Assert.Equal("20260824", ModComTime.HotToken(ModComHotKeyStrategy.RunDate, Context(Utc(2026, 8, 24, 13, 44))));
        Assert.Equal(FixedRunId.ToString("N"), ModComTime.HotToken(ModComHotKeyStrategy.RunId, Context(Utc(2026, 8, 24))));
    }

    [Fact]
    public void ThereIsNoSettledZone_EveryKeyOfEveryEndpointCarriesRunEqualsSomething()
    {
        // *** The property this test exists for: no work unit ANYWHERE gets a stable, run-independent
        // key. A settled key would record a window "done forever" and freeze a trade at its
        // pre-revision state while the loader reported clean runs. ***
        var settings = ReaderHarness.Settings(daysBack: 3650, chunkDays: 7, legalEntityName: "Acme Energy Management, LLC");
        var hourA = Context(Utc(2026, 8, 24, 13, 0));
        var hourB = Context(Utc(2026, 8, 24, 14, 0));

        foreach (var descriptor in ModComDescriptors.All)
        {
            var a = Units(descriptor, settings, hourA);
            var b = Units(descriptor, settings, hourB);

            Assert.NotEmpty(a);
            Assert.Equal(a.Count, b.Count);
            Assert.All(a, u => Assert.Contains(":run=", u.Key));
            Assert.All(a, u => Assert.EndsWith(":run=2026082413", u.Key));

            // Not one key survives the hour change - not even the OLDEST chunk, which any
            // settled/hot split would have frozen.
            Assert.Empty(a.Select(u => u.Key).Intersect(b.Select(u => u.Key)));
        }
    }

    [Fact]
    public void ChangingDaysBackOrChunkDays_YieldsNewKeys_RatherThanCollidingWithAnOldSuccess()
    {
        var context = Context(Utc(2026, 8, 24, 13, 0));

        var thirty = Assert.Single(Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(daysBack: 30), context));
        var sixty = Assert.Single(Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(daysBack: 60), context));
        Assert.NotEqual(thirty.Key, sixty.Key);

        var chunked = Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(daysBack: 30, chunkDays: 10), context);
        Assert.DoesNotContain(thirty.Key, chunked.Select(u => u.Key));
    }

    [Fact]
    public void EveryEndpointGetsItsOwnKeyNamespace()
    {
        var settings = ReaderHarness.Settings();
        var context = Context(Utc(2026, 8, 24, 13, 0));

        var keys = ModComDescriptors.All
            .Select(d => Units(d, settings, context).Single().Key)
            .ToArray();

        Assert.Equal(3, keys.Distinct().Count());
        Assert.Contains("modcom:AllTrades:", keys[0]);
        Assert.Contains("modcom:MyTrades:", keys[1]);
        Assert.Contains("modcom:Settlements:", keys[2]);
    }

    // ============================================================ the myTrades scope

    [Fact]
    public void AConfiguredLegalEntityName_EntersTheKeyAsASlug_AndThePathUrlEncoded()
    {
        // Without the slug in the key, switching the scope would make the run idempotently skip and
        // quietly keep the old, wider data. The raw value never enters the key: it has spaces AND a
        // comma.
        var settings = ReaderHarness.Settings(legalEntityName: "Acme Energy Management, LLC");
        var unit = Assert.Single(Units(ModComDescriptors.MyTrades, settings, Context(Utc(2026, 8, 24, 13, 0))));

        Assert.Equal("modcom:MyTrades:20260725-20260824:entity=acme-energy-management-llc:run=2026082413", unit.Key);
        Assert.Equal("Acme Energy Management, LLC", unit.LegalEntityName);
        Assert.Equal(
            "myTrades/v1?startDate=2026-07-25&endDate=2026-08-24&legalEntityName=Acme%20Energy%20Management%2C%20LLC",
            unit.RequestPath);
        Assert.Contains("[Acme Energy Management, LLC]", unit.DisplayName);
    }

    [Fact]
    public void SwitchingTheScope_ChangesTheKey_SoTheNarrowerPullCannotSkip()
    {
        var context = Context(Utc(2026, 8, 24, 13, 0));

        var unscoped = Assert.Single(Units(ModComDescriptors.MyTrades, ReaderHarness.Settings(), context));
        var scopedA = Assert.Single(Units(ModComDescriptors.MyTrades,
            ReaderHarness.Settings(legalEntityName: "Acme Energy Management, LLC"), context));
        var scopedB = Assert.Single(Units(ModComDescriptors.MyTrades,
            ReaderHarness.Settings(legalEntityName: "Acme Energy Management Canada ULC"), context));

        Assert.Equal(3, new[] { unscoped.Key, scopedA.Key, scopedB.Key }.Distinct().Count());
        Assert.DoesNotContain("entity=", unscoped.Key);          // the shipped default is the widest scope
        Assert.Null(unscoped.LegalEntityName);
        Assert.DoesNotContain("legalEntityName", unscoped.RequestPath);
    }

    [Fact]
    public void TheScopeIsIgnoredOnTheEndpointsThatDoNotAcceptIt()
    {
        var settings = ReaderHarness.Settings(legalEntityName: "Acme Energy Management, LLC");
        var context = Context(Utc(2026, 8, 24, 13, 0));

        foreach (var descriptor in new[] { ModComDescriptors.AllTrades, ModComDescriptors.Settlements })
        {
            var unit = Assert.Single(Units(descriptor, settings, context));
            Assert.Null(unit.LegalEntityName);
            Assert.DoesNotContain("entity=", unit.Key);
            Assert.DoesNotContain("legalEntityName", unit.RequestPath);
        }
    }

    [Theory]
    [InlineData("Acme Energy Management, LLC", "acme-energy-management-llc")]
    [InlineData("Acme Energy Management Canada ULC", "acme-energy-management-canada-ulc")]
    [InlineData("  Padded  Name  ", "padded-name")]
    [InlineData("A&B/C", "a-b-c")]
    [InlineData("ALLCAPS", "allcaps")]
    public void TheSlugIsLowercasedWithEveryNonAlphanumericRunCollapsed(string value, string expected)
    {
        Assert.Equal(expected, ModComWorkUnitProvider.Slug(value));
    }

    // ============================================================ the request path

    [Fact]
    public void TheRequestPathAlwaysSendsBOTHDatesExplicitly_InInvariantFormat()
    {
        // Never rely on the vendor "defaults to today" - that is computed in the vendor own zone.
        foreach (var descriptor in ModComDescriptors.All)
        {
            var unit = Assert.Single(Units(descriptor, ReaderHarness.Settings(), Context(Utc(2026, 8, 24, 13, 0))));
            Assert.StartsWith(descriptor.Path + "?startDate=2026-07-25&endDate=2026-08-24", unit.RequestPath);
            Assert.Contains("/v1", unit.RequestPath);
        }
    }

    [Fact]
    public void TheSanitisedRequestPathNeverCarriesACredential()
    {
        var unit = Assert.Single(Units(ModComDescriptors.AllTrades, ReaderHarness.Settings(), Context(Utc(2026, 8, 24, 13, 0))));

        Assert.DoesNotContain(ReaderHarness.DummyUser, unit.RequestPath);
        Assert.DoesNotContain(ReaderHarness.DummyPassword, unit.RequestPath);
        Assert.DoesNotContain("apikey", unit.RequestPath);
        Assert.DoesNotContain("Basic", unit.RequestPath);
    }
}
