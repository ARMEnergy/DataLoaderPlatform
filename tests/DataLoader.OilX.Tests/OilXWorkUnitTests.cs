using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.OilX.Tests;

/// <summary>
/// The window and the resume-key semantics — the repo's
/// <c>resume-key-and-status-matrix-check</c> gate.
///
/// <para>
/// Getting these wrong is silent in both directions. A settled day that gets a VARYING
/// key re-downloads gigabytes forever while reporting clean runs; a hot day that gets a
/// STABLE key is loaded once and never picks up the three further snapshots the vendor
/// publishes later that same day.
/// </para>
/// </summary>
public sealed class OilXWorkUnitTests
{
    private static readonly DateTime Noon = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly OilXFeedDescriptor Feed = TestHelpers.Feed(OilXDescriptors.GlobalBalance);

    private static async Task<IReadOnlyList<OilXWorkUnit>> UnitsAsync(
        Action<OilXSettings>? configure = null, DateTime? startedAtUtc = null)
    {
        var provider = new OilXWorkUnitProvider(Feed, TestHelpers.Settings(configure), NullLogger.Instance);
        return await provider.GetWorkUnitsAsync(TestHelpers.Context(startedAtUtc ?? Noon));
    }

    // ------------------------------------------------------------------ window

    /// <summary>The window is inclusive at BOTH ends, so DaysBack = 30 is 31 days.</summary>
    [Fact]
    public async Task Window_is_inclusive_at_both_ends()
    {
        var units = await UnitsAsync(s => s.DaysBack = 30);

        Assert.Equal(31, units.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), units[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 1), units[^1].Day);
    }

    [Fact]
    public async Task Window_is_ordered_oldest_first_and_has_no_gaps()
    {
        var units = await UnitsAsync(s => s.DaysBack = 10);

        for (var i = 1; i < units.Count; i++)
            Assert.Equal(units[i - 1].Day.AddDays(1), units[i].Day);
    }

    [Fact]
    public async Task DaysBack_zero_loads_only_today()
    {
        var units = await UnitsAsync(s => s.DaysBack = 0);

        Assert.Single(units);
        Assert.Equal(new DateOnly(2026, 10, 1), units[0].Day);
    }

    /// <summary>A negative DaysBack is clamped rather than throwing; the module warns.</summary>
    [Fact]
    public async Task Negative_DaysBack_is_clamped_to_today_only()
    {
        Assert.Single(await UnitsAsync(s => s.DaysBack = -5));
    }

    /// <summary>
    /// ⚠ The window is derived from the UTC start time. A local-zone boundary would shift
    /// twice a year and silently re-key two days' units.
    /// </summary>
    [Fact]
    public async Task Window_is_computed_in_utc()
    {
        // 23:30 UTC on the 1st is already the 2nd in several local zones.
        var units = await UnitsAsync(s => s.DaysBack = 0,
            new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 10, 1), units[0].Day);
    }

    // ------------------------------------------------------------- settled/hot

    /// <summary>With the shipped SettledAfterDays = 1, only today and yesterday are hot.</summary>
    [Fact]
    public async Task Shipped_default_leaves_only_today_and_yesterday_hot()
    {
        var units = await UnitsAsync(s => { s.DaysBack = 30; s.SettledAfterDays = 1; });

        var hot = units.Where(u => !u.Settled).Select(u => u.Day).ToList();

        Assert.Equal(new[] { new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1) }, hot);
        Assert.Equal(29, units.Count(u => u.Settled));
    }

    [Fact]
    public async Task A_settled_day_gets_a_stable_key_that_does_not_vary_between_runs()
    {
        var first = await UnitsAsync(s => { s.DaysBack = 30; s.SettledAfterDays = 1; },
            new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc));

        var second = await UnitsAsync(s => { s.DaysBack = 30; s.SettledAfterDays = 1; },
            new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc));

        var settledFirst = first.First(u => u.Settled);
        var settledSecond = second.First(u => u.Day == settledFirst.Day);

        Assert.True(settledSecond.Settled);
        Assert.Equal(settledFirst.Key, settledSecond.Key);
        Assert.Equal("feed=GlobalBalance;day=2026-09-01", settledFirst.Key);
    }

    /// <summary>
    /// ⚠ A hot day MUST re-key between runs, or the loader stops picking up the later
    /// snapshots of the day it is still watching.
    /// </summary>
    [Fact]
    public async Task A_hot_day_gets_a_key_that_varies_between_run_dates()
    {
        var monday = await UnitsAsync(s => { s.DaysBack = 0; s.SettledAfterDays = 1; },
            new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc));

        var tuesday = await UnitsAsync(s => { s.DaysBack = 0; s.SettledAfterDays = 1; },
            new DateTime(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc));

        Assert.False(monday[0].Settled);
        Assert.NotEqual(monday[0].Key, tuesday[0].Key);
    }

    [Theory]
    [InlineData(OilXHotKeyStrategy.RunDate, "feed=GlobalBalance;day=2026-10-01;run=20261001")]
    [InlineData(OilXHotKeyStrategy.RunHour, "feed=GlobalBalance;day=2026-10-01;run=2026100106")]
    public void Hot_key_strategies_stamp_the_documented_token(OilXHotKeyStrategy strategy, string expected)
    {
        var key = OilXWorkUnitProvider.BuildKey(
            Feed, new DateOnly(2026, 10, 1), settled: false, strategy,
            new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc),
            Guid.Parse("11111111-2222-3333-4444-555555555555"));

        Assert.Equal(expected, key);
    }

    [Fact]
    public void RunId_strategy_re_pulls_on_every_invocation()
    {
        string Key(Guid runId) => OilXWorkUnitProvider.BuildKey(
            Feed, new DateOnly(2026, 10, 1), settled: false, OilXHotKeyStrategy.RunId,
            Noon, runId);

        Assert.NotEqual(Key(Guid.NewGuid()), Key(Guid.NewGuid()));
    }

    /// <summary>
    /// ⚠ RunHour must be stamped in UTC: 01:00 local happens twice on a fall-back night,
    /// so a local token would repeat and an already-recorded success would suppress a
    /// legitimate re-pull for an hour.
    /// </summary>
    [Fact]
    public void RunHour_token_is_monotonic_across_a_dst_fall_back()
    {
        // The two 01:00 local instants of a European fall-back night, in UTC.
        var first = OilXWorkUnitProvider.BuildKey(Feed, new DateOnly(2026, 10, 25), false,
            OilXHotKeyStrategy.RunHour, new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), Guid.Empty);

        var second = OilXWorkUnitProvider.BuildKey(Feed, new DateOnly(2026, 10, 25), false,
            OilXHotKeyStrategy.RunHour, new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc), Guid.Empty);

        Assert.NotEqual(first, second);
        Assert.EndsWith("2026102500", first);
        Assert.EndsWith("2026102501", second);
    }

    [Fact]
    public void Keys_are_namespaced_by_feed_so_two_feeds_never_collide_on_a_day()
    {
        var cargo = OilXWorkUnitProvider.BuildKey(
            TestHelpers.Feed(OilXDescriptors.CargoTracking), new DateOnly(2026, 9, 1),
            settled: true, OilXHotKeyStrategy.RunDate, Noon, Guid.Empty);

        var global = OilXWorkUnitProvider.BuildKey(
            Feed, new DateOnly(2026, 9, 1), settled: true, OilXHotKeyStrategy.RunDate, Noon, Guid.Empty);

        Assert.NotEqual(cargo, global);
    }

    [Fact]
    public async Task Work_unit_keys_are_unique_across_the_window()
    {
        var units = await UnitsAsync(s => { s.DaysBack = 30; s.SettledAfterDays = 1; });

        Assert.Equal(units.Count, units.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Display_name_names_the_feed_and_the_day()
    {
        var units = await UnitsAsync(s => s.DaysBack = 0);

        Assert.Equal("GlobalBalance 2026-10-01", units[0].DisplayName);
    }

    /// <summary>
    /// Raising SettledAfterDays to DaysBack makes everything hot — the misconfiguration
    /// the module warns about, since OilX files are immutable once published.
    /// </summary>
    [Fact]
    public async Task All_hot_configuration_leaves_nothing_settled()
    {
        var units = await UnitsAsync(s => { s.DaysBack = 30; s.SettledAfterDays = 30; });

        Assert.All(units, u => Assert.False(u.Settled));
    }

    [Fact]
    public void IsSettled_treats_a_future_day_as_hot()
    {
        Assert.False(OilXTime.IsSettled(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1), 1));
    }
}
