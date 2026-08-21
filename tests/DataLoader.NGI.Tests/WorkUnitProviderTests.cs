using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.NGI.Tests;

/// <summary>
/// *** THE SECOND HIGHEST-VALUE TEST IN THIS PROJECT: the resumability rule. ***
///
/// <para>The work-unit <c>Key</c> <b>is</b> the idempotency contract - <c>core.LoadLog</c> skips a
/// unit only when that exact key is already recorded SUCCESSFUL, so a key-format change silently
/// alters what gets re-pulled and what never gets re-pulled again. These tests pin the three literal
/// formats and the settled/hot boundary:</para>
/// <code>
/// settled (ageDays &gt;  SettledAfterDays) : ngi:bidweek:{yyyyMMdd}            (STABLE across runs)
/// hot     (ageDays &lt;= SettledAfterDays) : ngi:bidweek:{yyyyMMdd}:run={hot}  (varies per cadence)
/// locations (undated, ALWAYS hot)         : ngi:locations:run={hot}
/// </code>
///
/// <para>Both providers are DB-free, network-free and clock-free (the run's
/// <c>StartedAtUtc</c> is supplied), so every assertion here is deterministic.</para>
/// </summary>
public class WorkUnitProviderTests
{
    // 2026-08-21 12:00Z -> 07:00 US Central (CDT) -> Central run date 2026-08-21.
    private static readonly DateTime StartedAt = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly RunDate = new(2026, 8, 21);
    private const string RunDateToken = "20260821";

    private static LoaderRunContext Context(Guid? runId = null, DateTime? startedAt = null) => new()
    {
        RunId = runId ?? Guid.NewGuid(),
        StartedAtUtc = startedAt ?? StartedAt,
        CancellationToken = CancellationToken.None
    };

    private static Task<IReadOnlyList<NgiBidWeekWorkUnit>> BidWeek(NgiSettings settings, LoaderRunContext? ctx = null) =>
        new NgiBidWeekWorkUnitProvider(settings, NullLogger.Instance).GetWorkUnitsAsync(ctx ?? Context());

    private static Task<IReadOnlyList<NgiLocationsWorkUnit>> Locations(NgiSettings settings, LoaderRunContext? ctx = null) =>
        new NgiLocationsWorkUnitProvider(settings, NullLogger.Instance).GetWorkUnitsAsync(ctx ?? Context());

    private static string Stamp(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // ================================================== window bounds: [runDate-(DaysBack-1) .. runDate]

    [Fact]
    public async Task Window_SpansRunDateMinusDaysBackPlusOne_ThroughRunDate_Inclusive()
    {
        var units = await BidWeek(new NgiSettings { DaysBack = 5, SettledAfterDays = 60 });

        Assert.Equal(5, units.Count);
        Assert.Equal(new DateOnly(2026, 8, 17), units.Min(u => u.IssueDate)); // runDate - (5-1)
        Assert.Equal(RunDate, units.Max(u => u.IssueDate));                   // ends AT today
        Assert.Equal(
            new[]
            {
                new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 19),
                new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 21)
            },
            units.Select(u => u.IssueDate).OrderBy(d => d).ToArray());
    }

    [Fact]
    public async Task Window_MatchesResolveWindow_Exactly_NoDrift()
    {
        // The provider and NgiLoadValidator share NgiTime.ResolveWindow so the load window and the
        // validation window can never drift apart.
        var settings = new NgiSettings { DaysBack = 17, SettledAfterDays = 60 };
        var window = NgiTime.ResolveWindow(StartedAt, settings.DaysBack);

        var units = await BidWeek(settings);

        Assert.Equal(window.From, units.Min(u => u.IssueDate));
        Assert.Equal(window.To, units.Max(u => u.IssueDate));
        Assert.Equal(window.DaysBack, units.Count);
        Assert.Equal(window.RunDate, window.Newest);
        Assert.Equal(window.Newest, window.To);
    }

    [Fact]
    public async Task Window_HasNoGaps_EveryCalendarDayIsEnumeratedExactlyOnce()
    {
        var units = await BidWeek(new NgiSettings { DaysBack = 60, SettledAfterDays = 60 });

        var dates = units.Select(u => u.IssueDate).OrderBy(d => d).ToArray();
        Assert.Equal(60, dates.Length);
        Assert.Equal(60, dates.Distinct().Count());
        for (var i = 1; i < dates.Length; i++)
            Assert.Equal(dates[i - 1].AddDays(1), dates[i]);   // strictly consecutive, no holes
    }

    [Fact]
    public async Task DaysBackBelowOne_IsClampedToOne()
    {
        foreach (var daysBack in new[] { 0, -1, -100 })
        {
            var units = await BidWeek(new NgiSettings { DaysBack = daysBack, SettledAfterDays = 60 });
            var u = Assert.Single(units);
            Assert.Equal(RunDate, u.IssueDate);
        }
    }

    // ================================================== NO FUTURE DATE, EVER

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(60)]
    [InlineData(365)]
    public async Task NoFutureDateIsEverEmitted(int daysBack)
    {
        // A future date returns 404, and a 404 COMPLETES ITS UNIT SUCCESSFULLY - so a future date in
        // the settled zone would be recorded permanently done and never re-probed once it became a
        // real issue date. The newest candidate must therefore never exceed the run date.
        var units = await BidWeek(new NgiSettings { DaysBack = daysBack, SettledAfterDays = 60 });

        Assert.All(units, u => Assert.True(u.IssueDate <= RunDate, $"future date emitted: {u.IssueDate}"));
        Assert.Equal(RunDate, units.Max(u => u.IssueDate));
    }

    [Fact]
    public async Task WindowEndsAtToday_NotYesterday_BecauseNgiPublishesOnTheIssueDate()
    {
        // Contrast AGSI, which offsets by -1 for a publication lag. NGI publishes ON the issue date,
        // so today must be probed.
        var units = await BidWeek(new NgiSettings { DaysBack = 1, SettledAfterDays = 60 });
        Assert.Equal(RunDate, Assert.Single(units).IssueDate);
    }

    // ================================================== NO business-day filter (the proven-necessary one)

    [Fact]
    public async Task NoBusinessDayFilter_TheSaturday_2026_08_01_IsEnumerated()
    {
        // *** REGRESSION TEST FOR THE SINGLE MOST IMPORTANT DESIGN RATIONALE. ***
        // NGI's own spec claims "issue date will always be a business day". The LIVE DATA CONTRADICTS
        // IT: 2026-08-01 is a SATURDAY and really does return 200 with 163 records (that response is
        // this project's fixture), while Friday 2026-07-31 returns 404. A weekday filter would have
        // silently missed the entire August 2026 issue while reporting a clean run.
        var units = await BidWeek(new NgiSettings { DaysBack = 60, SettledAfterDays = 60 });

        var saturday = new DateOnly(2026, 8, 1);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);         // it really is a Saturday
        Assert.Equal(Samples.FixtureIssueDate, saturday);             // and it is the captured fixture's issue date
        Assert.Contains(saturday, units.Select(u => u.IssueDate));    // ... and it IS enumerated

        // The Friday before it (a live 404) is enumerated too - the loader lets the 404 be the answer.
        Assert.Contains(new DateOnly(2026, 7, 31), units.Select(u => u.IssueDate));
    }

    [Fact]
    public async Task NoBusinessDayFilter_AllWeekendDaysInTheWindowAreEnumerated()
    {
        var units = await BidWeek(new NgiSettings { DaysBack = 60, SettledAfterDays = 60 });
        var dates = units.Select(u => u.IssueDate).ToHashSet();

        // Window 2026-06-23 .. 2026-08-21 contains 8 Saturdays and 8 Sundays; all 16 must be present.
        Assert.Equal(8, dates.Count(d => d.DayOfWeek == DayOfWeek.Saturday));
        Assert.Equal(8, dates.Count(d => d.DayOfWeek == DayOfWeek.Sunday));

        // And nothing is filtered by day of week at all: the count equals the raw calendar span.
        Assert.Equal(60, dates.Count);
    }

    [Fact]
    public async Task NoDayOfMonthRule_EveryDayOfMonthAppears_NotJustTheFirst()
    {
        // "Always the 1st of the month" is NOT established either (only three issue dates were ever
        // probed), so no day-of-month rule may be encoded.
        var units = await BidWeek(new NgiSettings { DaysBack = 60, SettledAfterDays = 60 });
        var days = units.Select(u => u.IssueDate.Day).Distinct().ToArray();
        Assert.True(days.Length > 25, $"only {days.Length} distinct days-of-month enumerated");
    }

    // ================================================== the three literal key formats

    [Fact]
    public async Task HotKey_LiteralFormat_IsNgiBidweekDateRunToken()
    {
        var units = await BidWeek(new NgiSettings { DaysBack = 3, SettledAfterDays = 60 });

        var today = Assert.Single(units, u => u.IssueDate == RunDate);
        Assert.Equal($"ngi:bidweek:{RunDateToken}:run={RunDateToken}", today.Key);
        Assert.Equal(today.Key, today.KeyValue);
        Assert.Equal("NGI bidweek 2026-08-21", today.DisplayName);
        Assert.Equal("/bidweekDatafeed.json?issue_date=2026-08-21", today.RequestPath);
    }

    [Fact]
    public async Task LocationsKey_LiteralFormat_IsNgiLocationsRunToken_AlwaysHot()
    {
        var units = await Locations(new NgiSettings());

        var u = Assert.Single(units);                 // exactly ONE undated unit per run
        Assert.Equal($"ngi:locations:run={RunDateToken}", u.Key);
        Assert.Equal("NGI bidweek locations", u.DisplayName);
    }

    [Fact]
    public async Task SettledKey_LiteralFormat_IsBareAndCarriesNoRunToken()
    {
        // DaysBack 10 / SettledAfterDays 3 -> ages 0..9; ages 4..9 are SETTLED (age > 3).
        var units = await BidWeek(new NgiSettings { DaysBack = 10, SettledAfterDays = 3 });

        var settled = units.Single(u => u.IssueDate == RunDate.AddDays(-9)); // age 9
        Assert.Equal($"ngi:bidweek:{Stamp(RunDate.AddDays(-9))}", settled.Key);
        Assert.DoesNotContain(":run=", settled.Key);
    }

    // ================================================== the settled/hot boundary

    [Fact]
    public async Task SettledHotBoundary_IsExactlyAtSettledAfterDays_AgeGreaterThanIsSettled()
    {
        const int settledAfter = 3;
        var units = (await BidWeek(new NgiSettings { DaysBack = 10, SettledAfterDays = settledAfter }))
            .ToDictionary(u => RunDate.DayNumber - u.IssueDate.DayNumber);

        // age == SettledAfterDays is still HOT (the rule is `age > SettledAfterDays`) ...
        var boundaryHot = units[settledAfter];
        Assert.Equal($"ngi:bidweek:{Stamp(boundaryHot.IssueDate)}:run={RunDateToken}", boundaryHot.Key);

        // ... and age == SettledAfterDays + 1 is the first SETTLED date.
        var boundarySettled = units[settledAfter + 1];
        Assert.Equal($"ngi:bidweek:{Stamp(boundarySettled.IssueDate)}", boundarySettled.Key);

        // Zone membership across the whole window, by age.
        for (var age = 0; age <= settledAfter; age++)
            Assert.Contains(":run=", units[age].Key);
        for (var age = settledAfter + 1; age <= 9; age++)
            Assert.DoesNotContain(":run=", units[age].Key);
    }

    [Fact]
    public async Task SettledKeys_AreStableAcrossRuns_HotKeysAreNot()
    {
        var settings = new NgiSettings { DaysBack = 10, SettledAfterDays = 3, HotZoneKeyStrategy = NgiHotKeyStrategy.RunId };

        var runA = await BidWeek(settings, Context(Guid.NewGuid()));
        var runB = await BidWeek(settings, Context(Guid.NewGuid()));

        var settledA = runA.Where(u => !u.Key.Contains(":run=")).Select(u => u.Key).OrderBy(k => k).ToArray();
        var settledB = runB.Where(u => !u.Key.Contains(":run=")).Select(u => u.Key).OrderBy(k => k).ToArray();

        Assert.Equal(6, settledA.Length);                 // ages 4..9
        Assert.Equal(settledA, settledB);                 // STABLE -> loaded once, then a cheap skip forever

        var hotA = runA.Where(u => u.Key.Contains(":run=")).Select(u => u.Key).OrderBy(k => k).ToArray();
        var hotB = runB.Where(u => u.Key.Contains(":run=")).Select(u => u.Key).OrderBy(k => k).ToArray();
        Assert.Equal(4, hotA.Length);                     // ages 0..3
        Assert.NotEqual(hotA, hotB);                      // run-varying -> re-pulled
    }

    // ================================================== hot-key cadence

    [Fact]
    public async Task HotKeyStrategy_RunDate_IsStableWithinTheSameCentralDay_AcrossDifferentRunIds()
    {
        var settings = new NgiSettings { DaysBack = 2, SettledAfterDays = 60 }; // RunDate is the default

        // Two different runs, different run ids, different UTC times, SAME Central calendar day.
        var morning = await BidWeek(settings, Context(Guid.NewGuid(), new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)));
        var evening = await BidWeek(settings, Context(Guid.NewGuid(), new DateTime(2026, 8, 21, 23, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(
            morning.Select(u => u.Key).OrderBy(k => k).ToArray(),
            evening.Select(u => u.Key).OrderBy(k => k).ToArray());   // a second same-day run SKIPS idempotently

        var locA = await Locations(settings, Context(Guid.NewGuid(), new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)));
        var locB = await Locations(settings, Context(Guid.NewGuid(), new DateTime(2026, 8, 21, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(locA[0].Key, locB[0].Key);
    }

    [Fact]
    public async Task HotKeyStrategy_RunDate_ChangesOnTheNextCentralDay()
    {
        var settings = new NgiSettings { DaysBack = 1, SettledAfterDays = 60 };

        var day1 = await BidWeek(settings, Context(startedAt: new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)));
        var day2 = await BidWeek(settings, Context(startedAt: new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("ngi:bidweek:20260821:run=20260821", day1[0].Key);
        Assert.Equal("ngi:bidweek:20260822:run=20260822", day2[0].Key);
        Assert.NotEqual(day1[0].Key, day2[0].Key);   // one re-pull per calendar day
    }

    [Fact]
    public async Task HotKeyStrategy_RunId_VariesEveryInvocation()
    {
        var settings = new NgiSettings
        {
            DaysBack = 1,
            SettledAfterDays = 60,
            HotZoneKeyStrategy = NgiHotKeyStrategy.RunId
        };

        var runId = Guid.NewGuid();
        var units = await BidWeek(settings, Context(runId));
        Assert.Equal($"ngi:bidweek:{RunDateToken}:run={runId:N}", units[0].Key);

        var again = await BidWeek(settings, Context(Guid.NewGuid()));
        Assert.NotEqual(units[0].Key, again[0].Key);
    }

    [Fact]
    public async Task LocationsKey_RunIdStrategy_VariesEveryInvocation()
    {
        var settings = new NgiSettings { HotZoneKeyStrategy = NgiHotKeyStrategy.RunId };

        var runId = Guid.NewGuid();
        var units = await Locations(settings, Context(runId));
        Assert.Equal($"ngi:locations:run={runId:N}", units[0].Key);
    }

    [Fact]
    public void HotKeyStrategy_HasExactlyTwoMembers_RunHourIsDeliberatelyAbsent()
    {
        // RunHour is NOT offered: Bidweek is a MONTHLY feed. (And a Central yyyyMMddHH token would be
        // unsafe anyway - 01:00 Central occurs twice on a fall-back night.)
        Assert.Equal(new[] { "RunDate", "RunId" }, Enum.GetNames<NgiHotKeyStrategy>());
        Assert.Equal(NgiHotKeyStrategy.RunDate, new NgiSettings().HotZoneKeyStrategy);
    }

    // ================================================== the SHIPPED DEFAULTS: 60 / 60 -> all hot

    [Fact]
    public void ShippedDefaults_AreDaysBack60_AndSettledAfterDays60()
    {
        // The user-required defaults. If either changes, the consequence below changes with it.
        var settings = new NgiSettings();
        Assert.Equal(60, settings.DaysBack);
        Assert.Equal(60, settings.SettledAfterDays);
    }

    [Fact]
    public async Task ShippedDefaults_MakeTheSettledZoneEmpty_TheWholeWindowIsHot()
    {
        // Consequence of 60/60: ages run 0..59, so `age > SettledAfterDays` (age > 60) is NEVER true.
        // The settled zone is EMPTY and every date is re-probed each run - the safest configuration,
        // because the settled-zone-404 invariant (a transient 404 frozen as a permanent success)
        // cannot bite.
        var units = await BidWeek(new NgiSettings());

        Assert.Equal(60, units.Count);
        Assert.Equal(59, units.Max(u => RunDate.DayNumber - u.IssueDate.DayNumber));  // max age 59
        Assert.All(units, u => Assert.Contains(":run=", u.Key));                       // ALL hot
        Assert.DoesNotContain(units, u => !u.Key.Contains(":run="));               // settled zone empty
    }

    [Fact]
    public async Task RaisingDaysBackAloneEngagesTheSettledZone_ForGenuineHistory()
    {
        // The documented backfill behaviour: at the 60/60 default a bigger DaysBack settles everything
        // older than 60 days (load once, never re-probe) while the recent 61 days stay hot.
        var units = await BidWeek(new NgiSettings { DaysBack = 400 });

        Assert.Equal(400, units.Count);
        Assert.Equal(61, units.Count(u => u.Key.Contains(":run=")));   // ages 0..60 inclusive
        Assert.Equal(339, units.Count(u => !u.Key.Contains(":run=")));
    }

    // ================================================== request path formatting is load-bearing

    [Fact]
    public async Task RequestPath_IsAlwaysInvariantYyyyMmDd_AnyOtherFormatIsA400()
    {
        var units = await BidWeek(new NgiSettings { DaysBack = 60, SettledAfterDays = 60 });

        Assert.All(units, u =>
        {
            var expected = "/bidweekDatafeed.json?issue_date=" +
                           u.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Equal(expected, u.RequestPath);
        });
        Assert.Contains("/bidweekDatafeed.json?issue_date=2026-08-01", units.Select(u => u.RequestPath));
    }

    [Fact]
    public async Task Providers_TouchNoDatabase_AndNeedNoReferenceData()
    {
        // The critical divergence from AGSI: nothing is read back out of arm.BidWeekLocation, so the
        // two pipelines are independent and a BidWeekData-only run is perfectly valid. Both providers
        // are constructed with settings + a logger ONLY - there is no reference provider to inject.
        Assert.Equal(2, typeof(NgiBidWeekWorkUnitProvider).GetConstructors().Single().GetParameters().Length);
        Assert.Equal(2, typeof(NgiLocationsWorkUnitProvider).GetConstructors().Single().GetParameters().Length);

        Assert.NotEmpty(await BidWeek(new NgiSettings { DaysBack = 1 }));
        Assert.NotEmpty(await Locations(new NgiSettings()));
    }
}
