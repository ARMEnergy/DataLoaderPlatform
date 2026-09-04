using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// Window enumeration and the resume-key contract.
///
/// <para>
/// The resume key is what <c>core.LoadLog</c> uses to decide "already done". Get it
/// wrong in one direction and revised settlements are never re-pulled; wrong in the
/// other and nothing is ever skipped.
/// </para>
/// </summary>
public sealed class WorkUnitTests
{
    private static readonly IceFeedDescriptor Gas = IceDescriptors.Find("IceGas")!;

    private static LoaderRunContext Context(DateTime? startedAtUtc = null) => new()
    {
        RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = startedAtUtc ?? new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    private static IReadOnlyList<IceWorkUnit> Units(IceSettings settings, LoaderRunContext? context = null) =>
        new IceWorkUnitProvider(Gas, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(context ?? Context()).GetAwaiter().GetResult();

    [Fact]
    public void Window_is_inclusive_at_both_ends()
    {
        var units = Units(TestHelpers.Settings(s => s.DaysBack = 5));

        // today - 5 .. today inclusive = 6 dates
        Assert.Equal(6, units.Count);
    }

    [Fact]
    public void Units_come_back_newest_first()
    {
        var units = Units(TestHelpers.Settings(s => s.DaysBack = 3));

        var dates = units.Select(u => u.TradeDate).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
    }

    /// <summary>
    /// The shipped 30/30 default: the oldest date is exactly 30 days old, so
    /// <c>age &gt; 30</c> is never true and the settled zone is EMPTY. Every date
    /// stays hot, which is what keeps revised settlements flowing.
    /// </summary>
    [Fact]
    public void Shipped_defaults_make_every_date_hot()
    {
        var units = Units(TestHelpers.Settings());

        Assert.Equal(31, units.Count);
        Assert.All(units, u => Assert.True(u.IsHot, $"{u.TradeDate} should be hot under the 30/30 default."));
    }

    [Fact]
    public void A_settled_zone_appears_only_when_SettledAfterDays_is_below_DaysBack()
    {
        var units = Units(TestHelpers.Settings(s => { s.DaysBack = 10; s.SettledAfterDays = 3; }));

        // ages 0..3 hot (4 dates), ages 4..10 settled (7 dates)
        Assert.Equal(4, units.Count(u => u.IsHot));
        Assert.Equal(7, units.Count(u => !u.IsHot));
    }

    [Fact]
    public void Hot_key_varies_between_runs_and_settled_key_does_not()
    {
        var settings = TestHelpers.Settings(s => { s.DaysBack = 10; s.SettledAfterDays = 3; });

        var monday = Units(settings, Context(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc)));
        var tuesday = Units(settings, Context(new DateTime(2026, 9, 1, 23, 0, 0, DateTimeKind.Utc)));

        // Same UTC date -> RunDate token identical -> keys identical (a same-day
        // re-run must idempotently skip).
        Assert.Equal(monday[0].Key, tuesday[0].Key);

        var nextDay = Units(settings, Context(new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc)));
        var settledMonday = monday.Single(u => !u.IsHot && u.TradeDate == monday.Last().TradeDate);
        var settledNextDay = nextDay.SingleOrDefault(u => u.TradeDate == settledMonday.TradeDate);

        // A settled date's key carries no run token at all.
        Assert.DoesNotContain(":run=", settledMonday.Key);
        if (settledNextDay is not null)
            Assert.Equal(settledMonday.Key, settledNextDay.Key);
    }

    [Fact]
    public void Hot_key_changes_when_the_utc_day_rolls_over()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 0);

        var today = Units(settings, Context(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)))[0];
        var tomorrow = Units(settings, Context(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc)))[0];

        Assert.NotEqual(today.Key, tomorrow.Key);
    }

    [Theory]
    [InlineData(IceHotKeyStrategy.RunDate, "20260901")]
    [InlineData(IceHotKeyStrategy.RunHour, "2026090118")]
    public void Hot_token_follows_the_configured_strategy(IceHotKeyStrategy strategy, string expected)
    {
        var provider = new IceWorkUnitProvider(
            Gas, TestHelpers.Settings(s => s.HotKeyStrategy = strategy), NullLogger.Instance);

        Assert.Equal(expected, provider.HotToken(Context()));
    }

    [Fact]
    public void RunId_strategy_forces_every_run_to_repull()
    {
        var provider = new IceWorkUnitProvider(
            Gas, TestHelpers.Settings(s => s.HotKeyStrategy = IceHotKeyStrategy.RunId), NullLogger.Instance);

        Assert.Equal("11111111222233334444555555555555", provider.HotToken(Context()));
    }

    /// <summary>
    /// A negative DaysBack would invert the window and silently produce nothing —
    /// a loader that "succeeds" while doing no work.
    /// </summary>
    [Fact]
    public void Negative_DaysBack_is_clamped_to_a_single_day()
    {
        var units = Units(TestHelpers.Settings(s => s.DaysBack = -5));
        Assert.Single(units);
    }

    /// <summary>
    /// A negative SettledAfterDays would make every date settled, freezing the whole
    /// window on stable keys so revisions would never be re-pulled again.
    /// </summary>
    [Fact]
    public void Negative_SettledAfterDays_is_clamped_so_today_stays_hot()
    {
        var units = Units(TestHelpers.Settings(s => { s.DaysBack = 5; s.SettledAfterDays = -1; }));

        Assert.True(units[0].IsHot, "Today must remain hot even under a negative SettledAfterDays.");
    }

    [Fact]
    public void Key_and_display_name_are_stable_and_culture_invariant()
    {
        var unit = new IceWorkUnit
        {
            Feed = Gas,
            TradeDate = new DateOnly(2026, 8, 28),
            KeySuffix = ":run=20260901"
        };

        Assert.Equal("ice:IceGas:2026-08-28:run=20260901", unit.Key);
        Assert.Equal("ICE IceGas 2026-08-28", unit.DisplayName);
    }

    [Fact]
    public void Keys_are_unique_within_a_run()
    {
        var units = Units(TestHelpers.Settings());
        Assert.Equal(units.Count, units.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Two feeds must never share a key, or one would suppress the other.</summary>
    [Fact]
    public async Task Different_feeds_produce_different_keys_for_the_same_date()
    {
        var settings = TestHelpers.Settings(s => s.DaysBack = 0);
        var context = Context();

        var gas = (await new IceWorkUnitProvider(Gas, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(context))[0];

        var oil = (await new IceWorkUnitProvider(IceDescriptors.Find("IceOil")!, settings, NullLogger.Instance)
            .GetWorkUnitsAsync(context))[0];

        Assert.Equal(gas.TradeDate, oil.TradeDate);
        Assert.NotEqual(gas.Key, oil.Key);
    }
}
