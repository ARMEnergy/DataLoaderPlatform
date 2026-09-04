using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// Work-unit enumeration and the RESUME-KEY contract.
///
/// <para>
/// The resume key is the loader's idempotency contract: <c>core.LoadLog</c> skips a
/// key already recorded successful. Getting it wrong is silent in both directions — a
/// key that never varies stops re-pulling revisions, and a key that varies when it
/// should not re-loads settled history forever. These tests pin both halves.
/// </para>
/// </summary>
public sealed class WorkUnitTests
{
    private static CriterionWorkUnitProvider Provider(
        CriterionFeedDescriptor feed, CriterionSettings settings, ICriterionSource? source = null) =>
        new(feed, source ?? new FakeCriterionSource(), settings, NullLogger.Instance);

    private static CriterionFeedDescriptor Feed(string id) => CriterionDescriptors.Find(id)!;

    // ------------------------------------------------------------------ window

    [Fact]
    public async Task Day_window_covers_todayMinusDaysBack_through_today_inclusive()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 5);
        var units = await Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(6, units.Count);

        var days = units.Select(u => u.Day!.Value).OrderBy(d => d).ToList();
        Assert.Equal(new DateOnly(2026, 8, 29), days.First());
        Assert.Equal(new DateOnly(2026, 9, 3), days.Last());
    }

    /// <summary>Newest day first, so an interrupted run has already done the day that matters.</summary>
    [Fact]
    public async Task Day_window_is_enumerated_newest_first()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 3);
        var units = await Provider(Feed(CriterionFeed.FinancialSeries), settings)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(
            units.Select(u => u.Day!.Value),
            units.Select(u => u.Day!.Value).OrderByDescending(d => d));
    }

    /// <summary>
    /// ⚠ CLAMPED. A negative DaysBack would invert the window and yield zero units — a
    /// loader that silently does nothing every night.
    /// </summary>
    [Fact]
    public async Task Negative_days_back_is_clamped_to_today_only()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = -10);
        var units = await Provider(Feed(CriterionFeed.PipelinesPointflows), settings)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Single(units);
        Assert.Equal(new DateOnly(2026, 9, 3), units[0].Day!.Value);
    }

    [Fact]
    public async Task Snapshot_feeds_yield_exactly_one_unit_regardless_of_days_back()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 90);

        foreach (var feed in CriterionDescriptors.All.Where(f => f.WindowMode == CriterionWindowMode.Snapshot))
        {
            var units = await Provider(feed, settings).GetWorkUnitsAsync(TestHelpers.Context());

            Assert.Single(units);
            Assert.Null(units[0].Day);
            Assert.Equal("snapshot", units[0].Slice);
        }
    }

    // -------------------------------------------------------------- resume keys

    /// <summary>
    /// With the shipped 30/30 defaults the settled zone is EMPTY: the oldest day is
    /// exactly 30 days old and <c>age &gt; 30</c> is false. Deliberate — this source
    /// revises history, so a stable key would freeze the first value seen.
    /// </summary>
    [Fact]
    public async Task Shipped_defaults_make_every_day_hot()
    {
        var settings = TestHelpers.Settings();
        Assert.Equal(30, settings.DaysBack);
        Assert.Equal(30, settings.SettledAfterDays);

        var units = await Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(31, units.Count);
        Assert.All(units, u => Assert.True(u.IsHot, $"{u.Key} should be hot under the 30/30 defaults."));
    }

    [Fact]
    public async Task Days_older_than_settled_after_days_get_a_stable_key()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 10; s.SettledAfterDays = 3; });
        var units = await Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings)
            .GetWorkUnitsAsync(TestHelpers.Context());

        var today = new DateOnly(2026, 9, 3);

        foreach (var unit in units)
        {
            var age = today.DayNumber - unit.Day!.Value.DayNumber;
            Assert.Equal(age <= 3, unit.IsHot);

            if (unit.IsHot) Assert.Contains(":run=", unit.Key);
            else Assert.DoesNotContain(":run=", unit.Key);
        }
    }

    /// <summary>A settled day's key must be byte-identical between runs, or it re-loads forever.</summary>
    [Fact]
    public async Task A_settled_key_is_identical_across_runs()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 10; s.SettledAfterDays = 0; });
        var provider = Provider(Feed(CriterionFeed.FinancialSeries), settings);

        var first = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 3, 1, 0, 0, DateTimeKind.Utc), Guid.NewGuid()));
        var second = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 3, 23, 0, 0, DateTimeKind.Utc), Guid.NewGuid()));

        var settledFirst = first.Where(u => !u.IsHot).Select(u => u.Key).ToList();
        var settledSecond = second.Where(u => !u.IsHot).Select(u => u.Key).ToList();

        Assert.NotEmpty(settledFirst);
        Assert.Equal(settledFirst, settledSecond);
    }

    /// <summary>A hot day's key must CHANGE between runs, or revisions are never re-pulled.</summary>
    [Fact]
    public async Task A_hot_key_changes_between_run_dates()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 0);
        var provider = Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings);

        var monday = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc)));
        var tuesday = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 4, 6, 0, 0, DateTimeKind.Utc)));

        Assert.NotEqual(monday[0].Key, tuesday[0].Key);
    }

    /// <summary>The same UTC day must produce the SAME key, so a second run that day skips.</summary>
    [Fact]
    public async Task RunDate_strategy_gives_one_repull_per_utc_day()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 0);
        var provider = Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings);

        var morning = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 3, 1, 0, 0, DateTimeKind.Utc), Guid.NewGuid()));
        var evening = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 3, 23, 0, 0, DateTimeKind.Utc), Guid.NewGuid()));

        Assert.Equal(morning[0].Key, evening[0].Key);
    }

    [Fact]
    public void RunHour_token_is_utc_and_therefore_monotonic()
    {
        var settings = TestHelpers.Settings(s => s.HotKeyStrategy = CriterionHotKeyStrategy.RunHour);
        var provider = Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings);

        // The two instants an America/Chicago fall-back night maps to the same local
        // "01:00". In UTC they stay distinct, which is the whole point.
        var first = provider.HotToken(TestHelpers.Context(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc)));
        var second = provider.HotToken(TestHelpers.Context(new DateTime(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc)));

        Assert.NotEqual(first, second);
        Assert.Equal("2026110106", first);
        Assert.Equal("2026110107", second);
    }

    [Fact]
    public void RunId_strategy_forces_a_repull_every_invocation()
    {
        var settings = TestHelpers.Settings(s => s.HotKeyStrategy = CriterionHotKeyStrategy.RunId);
        var provider = Provider(Feed(CriterionFeed.PipelinesNominationPoint), settings);

        var a = provider.HotToken(TestHelpers.Context(runId: Guid.NewGuid()));
        var b = provider.HotToken(TestHelpers.Context(runId: Guid.NewGuid()));

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Snapshots are hot by default so the dimensions stay current; setting
    /// AlwaysReloadSnapshots=false must give them a stable key instead.
    /// </summary>
    [Fact]
    public async Task Snapshot_hotness_follows_AlwaysReloadSnapshots()
    {
        var hot = await Provider(Feed(CriterionFeed.MiscUnit), TestHelpers.Settings())
            .GetWorkUnitsAsync(TestHelpers.Context());
        Assert.True(hot[0].IsHot);
        Assert.Contains(":run=", hot[0].Key);

        var settled = await Provider(
                Feed(CriterionFeed.MiscUnit),
                TestHelpers.Settings(s => s.AlwaysReloadSnapshots = false))
            .GetWorkUnitsAsync(TestHelpers.Context());
        Assert.False(settled[0].IsHot);
        Assert.Equal("criterion:MiscUnit:snapshot", settled[0].Key);
    }

    /// <summary>Keys are namespaced per feed, so two feeds' units can never collide in the load log.</summary>
    [Fact]
    public async Task Keys_are_unique_across_every_feed_and_day()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 3);
        var keys = new List<string>();

        foreach (var feed in CriterionDescriptors.All.Where(f => f.WindowMode != CriterionWindowMode.PagedDayWindow))
        {
            var units = await Provider(feed, settings).GetWorkUnitsAsync(TestHelpers.Context());
            keys.AddRange(units.Select(u => u.Key));
        }

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Dates in keys are ISO and culture-invariant — never the ambient calendar.</summary>
    [Fact]
    public async Task Keys_use_invariant_iso_dates()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");

            var settings = TestHelpers.Settings(s => s.DaysBack = 0);
            var units = await Provider(Feed(CriterionFeed.FinancialSeries), settings)
                .GetWorkUnitsAsync(TestHelpers.Context());

            Assert.StartsWith("criterion:FinancialSeries:2026-09-03", units[0].Key);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ----------------------------------------------------------------- paging

    [Fact]
    public async Task Paged_feed_splits_a_day_into_pages_of_page_size()
    {
        var source = new FakeCriterionSource();
        var day = new DateOnly(2026, 9, 3);
        source.SetKeys(day, Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray());

        var settings = TestHelpers.Settings(s => { s.DaysBack = 0; s.SeriesPageSize = 3; });
        var units = await Provider(Feed(CriterionFeed.FinancialSeriesData), settings, source)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(new[] { 3, 3, 1 }, units.Select(u => u.Keys.Count));
        Assert.Equal(new[] { 0, 1, 2 }, units.Select(u => u.Page));
    }

    /// <summary>Every source key must land in exactly one page — no gaps, no overlaps.</summary>
    [Fact]
    public async Task Paging_partitions_the_days_keys_exactly()
    {
        var source = new FakeCriterionSource();
        var day = new DateOnly(2026, 9, 3);
        var keys = Enumerable.Range(0, 17).Select(_ => Guid.NewGuid()).ToArray();
        source.SetKeys(day, keys);

        var settings = TestHelpers.Settings(s => { s.DaysBack = 0; s.SeriesPageSize = 5; });
        var units = await Provider(Feed(CriterionFeed.FinancialSeriesData), settings, source)
            .GetWorkUnitsAsync(TestHelpers.Context());

        var covered = units.SelectMany(u => u.Keys).ToList();

        Assert.Equal(keys.Length, covered.Count);
        Assert.Equal(keys.ToHashSet(), covered.ToHashSet());
    }

    /// <summary>The page number is part of the key, so pages cannot collide in the load log.</summary>
    [Fact]
    public async Task Paged_keys_include_the_page_number()
    {
        var source = new FakeCriterionSource();
        source.SetKeys(new DateOnly(2026, 9, 3), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var settings = TestHelpers.Settings(s => { s.DaysBack = 0; s.SeriesPageSize = 1; });
        var units = await Provider(Feed(CriterionFeed.FinancialSeriesData), settings, source)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(3, units.Count);
        Assert.Equal(3, units.Select(u => u.Key).Distinct().Count());
        Assert.Contains("2026-09-03:p0", units[0].Key);
        Assert.Contains("2026-09-03:p2", units[2].Key);
    }

    /// <summary>A day the source has not published is skipped, not turned into an empty unit.</summary>
    [Fact]
    public async Task A_day_with_no_source_rows_produces_no_paged_units()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 2; s.SeriesPageSize = 10; });
        var units = await Provider(Feed(CriterionFeed.FinancialSeriesData), settings, new FakeCriterionSource())
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Empty(units);
    }

    /// <summary>A non-positive page size is clamped rather than dividing by zero.</summary>
    [Fact]
    public async Task Non_positive_page_size_is_clamped_to_one()
    {
        var source = new FakeCriterionSource();
        source.SetKeys(new DateOnly(2026, 9, 3), Guid.NewGuid(), Guid.NewGuid());

        var settings = TestHelpers.Settings(s => { s.DaysBack = 0; s.SeriesPageSize = 0; });
        var units = await Provider(Feed(CriterionFeed.FinancialSeriesData), settings, source)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(2, units.Count);
        Assert.All(units, u => Assert.Single(u.Keys));
    }
}
