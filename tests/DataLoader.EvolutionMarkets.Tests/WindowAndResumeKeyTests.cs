using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.EvolutionMarkets.Tests;

/// <summary>
/// Pins the date window and the two-zone resume key (design §3.2, §3.3) — the other half of the
/// <c>resume-key-and-status-matrix-check</c> skill.
///
/// <para><b>Why the resume key is load-bearing here.</b> A SETTLED (stable-key) date is pulled once
/// and then skipped forever by <c>core.LoadLog</c>. On this feed that costs two things, and both are
/// silent:</para>
/// <list type="number">
///   <item><b>Revisions.</b> The feed has no revision/version/status field; a corrected price is
///     delivered by re-serving the SAME <c>marketDataId</c> with new values. Only a re-pull sees it.</item>
///   <item><b>Late publication.</b> An empty <c>200</c> completes a unit as SUCCESS and is
///     indistinguishable from a weekend, so a date that had merely not published yet gets recorded as
///     permanently done.</item>
/// </list>
/// <para>Hence the shipped 30/30 default, which leaves the settled zone EMPTY. These tests pin that,
/// and pin that a narrower configuration still behaves as documented.</para>
/// </summary>
public class WindowAndResumeKeyTests
{
    /// <summary>A fixed UTC instant well clear of a Central midnight boundary, so the run date is unambiguous.</summary>
    private static readonly DateTime RunUtc = new(2026, 8, 25, 18, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly CentralRunDate = new(2026, 8, 25);

    private static LoaderRunContext Context(Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = RunUtc,
        CancellationToken = CancellationToken.None
    };

    private static async Task<IReadOnlyList<EvoMarketDataWorkUnit>> UnitsAsync(
        EvoSettings settings, LoaderRunContext? context = null, ListLogger? log = null)
    {
        var provider = new EvoMarketDataWorkUnitProvider(settings, log ?? new ListLogger());
        return await provider.GetWorkUnitsAsync(context ?? Context());
    }

    // ---- the window ----------------------------------------------------------------------------

    [Fact]
    public void ResolveWindow_EndsAtTodayInclusive_NeverInTheFuture()
    {
        var w = EvoTime.ResolveWindow(RunUtc, 30);

        Assert.Equal(CentralRunDate, w.RunDate);
        Assert.Equal(CentralRunDate, w.Newest);
        Assert.Equal(CentralRunDate, w.To);
        Assert.Equal(new DateOnly(2026, 7, 27), w.From);   // 30 days inclusive: 07-27 .. 08-25
        Assert.Equal(30, w.DaysBack);
    }

    [Fact]
    public void ResolveWindow_DefaultsToThirtyDays_PerTheLoaderSpec()
    {
        // "Go back 30 days by default."
        Assert.Equal(30, new EvoSettings().DaysBack);
        Assert.Equal(30, new EvoSettings().SettledAfterDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public void ResolveWindow_ClampsDaysBackToAtLeastOne(int daysBack)
    {
        // A zero or negative window would enumerate nothing (or run backwards); one day is the floor.
        var w = EvoTime.ResolveWindow(RunUtc, daysBack);
        Assert.Equal(1, w.DaysBack);
        Assert.Equal(w.From, w.To);
        Assert.Equal(CentralRunDate, w.From);
    }

    [Fact]
    public void CentralToday_IsBehindUtc_SoTheFutureDate400IsUnreachable()
    {
        // 02:00 UTC on the 26th is still the 25th in US Central. That is the property that makes the
        // window's newest member safe: the vendor rejects a dateFrom in ITS future with a hard 400,
        // and a Central date can never be ahead of a UTC date.
        var justAfterUtcMidnight = new DateTime(2026, 8, 26, 2, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 8, 25), EvoTime.CentralToday(justAfterUtcMidnight));
    }

    [Fact]
    public async Task Provider_EnumeratesOneUnitPerCalendarDay_Inclusive()
    {
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 30));

        Assert.Equal(30, units.Count);
        Assert.Equal(30, units.Select(u => u.BusinessDate).Distinct().Count());
        Assert.Equal(CentralRunDate, units.Max(u => u.BusinessDate));
        Assert.Equal(new DateOnly(2026, 7, 27), units.Min(u => u.BusinessDate));
    }

    [Fact]
    public async Task Provider_EnumeratesWeekendsToo_ThereIsNoWeekdayFilter()
    {
        // The live data forbids a weekday filter: 2026-08-20 (a Thursday) is missing from the feed with
        // no holiday explanation, so no computable calendar predicts publication. Every day is probed
        // and the empty array is the answer.
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 30));
        var days = units.Select(u => u.BusinessDate.DayOfWeek).Distinct().ToList();

        Assert.Contains(DayOfWeek.Saturday, days);
        Assert.Contains(DayOfWeek.Sunday, days);
        Assert.Contains(new DateOnly(2026, 8, 20), units.Select(u => u.BusinessDate));
    }

    [Fact]
    public async Task Provider_NeverEnumeratesAFutureDate()
    {
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 60));
        Assert.All(units, u => Assert.True(u.BusinessDate <= CentralRunDate));
    }

    // ---- the request path ----------------------------------------------------------------------

    [Fact]
    public void BuildRequestPath_PinsDateFromEqualsDateTo_AndTheFullFieldProjection()
    {
        var path = EvoMarketDataWorkUnitProvider.BuildRequestPath(new DateOnly(2026, 8, 24), null);

        Assert.Equal(
            "/v1/market-data/history?dateFrom=2026-08-24&dateTo=2026-08-24&field=" + EvoRequestFields.Csv,
            path);
    }

    [Fact]
    public void BuildRequestPath_UsesInvariantIsoDates()
    {
        // A non-ISO or culture-formatted date is a hard 400 from the vendor
        // ("dateFrom invalid format. Should be yyyy-MM-dd").
        var path = EvoMarketDataWorkUnitProvider.BuildRequestPath(new DateOnly(2026, 1, 5), null);
        Assert.Contains("dateFrom=2026-01-05", path);
        Assert.Contains("dateTo=2026-01-05", path);
    }

    [Fact]
    public void BuildRequestPath_UrlEscapesTheDatasetName()
    {
        // The live value contains a '/' (EVOID/USNaturalGasIndex) which would otherwise alter the path.
        var path = EvoMarketDataWorkUnitProvider.BuildRequestPath(
            new DateOnly(2026, 8, 24), "EVOID/USNaturalGasIndex");

        Assert.Contains("&datasetName=EVOID%2FUSNaturalGasIndex", path);
        Assert.DoesNotContain("datasetName=EVOID/US", path);
    }

    [Fact]
    public void BuildRequestPath_OmitsDatasetNameWhenNotConfigured()
    {
        // Absent filter = every permissioned dataset, so a new entitlement needs no config change.
        foreach (var value in new[] { null, "", "   " })
            Assert.DoesNotContain("datasetName",
                EvoMarketDataWorkUnitProvider.BuildRequestPath(new DateOnly(2026, 8, 24), value));
    }

    // ---- the two-zone resume key ---------------------------------------------------------------

    [Fact]
    public void BuildKey_SettledKeyIsStable_HotKeyCarriesTheRunToken()
    {
        var d = new DateOnly(2026, 8, 24);

        Assert.Equal("evolutionmarkets:marketdata:20260824",
            EvoMarketDataWorkUnitProvider.BuildKey(d, settled: true, "IGNORED"));

        Assert.Equal("evolutionmarkets:marketdata:20260824:run=20260825",
            EvoMarketDataWorkUnitProvider.BuildKey(d, settled: false, "20260825"));
    }

    /// <summary>
    /// *** THE SHIPPED DEFAULT. *** 30/30 means the maximum age in the window is 29, so
    /// <c>age &gt; SettledAfterDays</c> is never true and EVERY unit is hot. That is what makes the
    /// daily re-pull pick up revisions and late publications.
    /// </summary>
    [Fact]
    public async Task DefaultConfiguration_LeavesTheSettledZoneEmpty_EverythingIsHot()
    {
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 30, settledAfterDays: 30));

        Assert.Equal(30, units.Count);
        Assert.All(units, u => Assert.Contains(":run=", u.Key));
    }

    [Fact]
    public async Task ANarrowerSettledAfterDays_MakesTheOlderTailStable()
    {
        // age > 7 settles: ages 8..29 (22 units) get a stable key, ages 0..7 (8 units) stay hot.
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 30, settledAfterDays: 7));

        var hot = units.Where(u => u.Key.Contains(":run=")).ToList();
        var settled = units.Where(u => !u.Key.Contains(":run=")).ToList();

        Assert.Equal(8, hot.Count);
        Assert.Equal(22, settled.Count);

        // The hot ones are the most recent.
        Assert.Equal(CentralRunDate, hot.Max(u => u.BusinessDate));
        Assert.Equal(CentralRunDate.AddDays(-7), hot.Min(u => u.BusinessDate));
        Assert.True(settled.Max(u => u.BusinessDate) < hot.Min(u => u.BusinessDate));
    }

    [Fact]
    public async Task ANegativeSettledAfterDays_IsClampedToZero_SoOnlyTodayStaysHot()
    {
        // Unclamped, a negative value would settle the ENTIRE window on the first run — the worst
        // possible configuration, and silent. Clamped to 0, only today is hot.
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 30, settledAfterDays: -5));

        var hot = units.Where(u => u.Key.Contains(":run=")).ToList();
        Assert.Single(hot);
        Assert.Equal(CentralRunDate, hot[0].BusinessDate);
    }

    [Fact]
    public async Task RunDateStrategy_ProducesTheSameKeyTwiceInOneDay_SoASecondRunSkips()
    {
        var settings = ReaderHarness.Settings(hotKey: EvoHotKeyStrategy.RunDate);

        var first = await UnitsAsync(settings, Context(Guid.NewGuid()));
        var second = await UnitsAsync(settings, Context(Guid.NewGuid()));

        // Different run ids, same calendar day => identical keys => the second run is a cheap skip.
        Assert.Equal(first.Select(u => u.Key), second.Select(u => u.Key));
        Assert.All(first, u => Assert.Contains(":run=20260825", u.Key));
    }

    [Fact]
    public async Task RunIdStrategy_ProducesFreshKeysEveryInvocation()
    {
        var settings = ReaderHarness.Settings(hotKey: EvoHotKeyStrategy.RunId);

        var first = await UnitsAsync(settings, Context(Guid.NewGuid()));
        var second = await UnitsAsync(settings, Context(Guid.NewGuid()));

        Assert.Empty(first.Select(u => u.Key).Intersect(second.Select(u => u.Key)));
    }

    [Fact]
    public void HotToken_RunDateIsInvariantYyyyMMdd_RunIdIsTheGuid()
    {
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("20260825", EvoTime.HotToken(EvoHotKeyStrategy.RunDate, CentralRunDate, runId));
        Assert.Equal(runId.ToString("N"), EvoTime.HotToken(EvoHotKeyStrategy.RunId, CentralRunDate, runId));
    }

    [Fact]
    public async Task Keys_AreUniquePerUnit()
    {
        // A duplicate key would make two dates share one core.LoadLog row: the second would be
        // skipped as "already loaded" and its prices silently lost.
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 60));
        Assert.Equal(units.Count, units.Select(u => u.Key).Distinct().Count());
    }

    // ---- window/validator agreement ------------------------------------------------------------

    [Fact]
    public async Task TheValidatorAndTheProviderShareOneWindowHelper()
    {
        // Both call EvoTime.ResolveWindow with the same arguments. Pinning the helper's output is what
        // guarantees the validation window cannot drift from the load window.
        var w = EvoTime.ResolveWindow(RunUtc, 30);
        var units = await new EvoMarketDataWorkUnitProvider(
            ReaderHarness.Settings(daysBack: 30), new ListLogger())
            .GetWorkUnitsAsync(Context());

        Assert.Equal(w.From, units.Min(u => u.BusinessDate));
        Assert.Equal(w.To, units.Max(u => u.BusinessDate));
    }

    // ---- display / logging ---------------------------------------------------------------------

    [Fact]
    public void Iso_IsCultureInvariant()
    {
        // The work-unit DisplayName is persisted verbatim into core.LoadLog. Under an ambient culture
        // with a non-Gregorian calendar an interpolated date would print a different year entirely.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("2026-08-24", EvoTime.Iso(new DateOnly(2026, 8, 24)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task DisplayName_NamesTheBusinessDate()
    {
        var units = await UnitsAsync(ReaderHarness.Settings(daysBack: 1));
        Assert.Equal("EvolutionMarkets market-data 2026-08-25", units[0].DisplayName);
    }

    [Fact]
    public async Task Provider_LogsTheHotAndSettledSplit()
    {
        var log = new ListLogger();
        await UnitsAsync(ReaderHarness.Settings(daysBack: 30, settledAfterDays: 7), log: log);

        Assert.Contains(log.OfLevel(LogLevel.Debug).Select(e => e.Message),
            m => m.Contains("enumerated 30 work unit(s)") && m.Contains("hot") && m.Contains("settled"));
    }
}
