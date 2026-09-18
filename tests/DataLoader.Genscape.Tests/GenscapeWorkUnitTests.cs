using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// The resume-key contract: what a work unit covers, and which units get a stable key
/// versus a run-varying one.
///
/// <para>
/// A settled key that should have been hot silently freezes a revision the vendor
/// published; a hot key that should have been settled re-reads history forever. Both
/// are invisible in a green run, so they are pinned here.
/// </para>
/// </summary>
public sealed class GenscapeWorkUnitTests
{
    private static readonly DateTime RunAt = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 9, 8);

    private static GenscapeWorkUnitProvider Provider(
        Action<GenscapeSettings>? configure = null, GenscapeFeedDescriptor? feed = null) =>
        new(feed ?? GenscapeDescriptors.All[0], TestHelpers.Settings(configure), NullLogger.Instance);

    private static IReadOnlyList<GenscapeWorkUnit> Units(
        Action<GenscapeSettings>? configure = null, GenscapeFeedDescriptor? feed = null) =>
        Provider(configure, feed).GetWorkUnitsAsync(TestHelpers.Context(RunAt)).GetAwaiter().GetResult();

    // ------------------------------------------------------------- the window

    /// <summary>
    /// The shipped defaults: a 31-day window (DaysBack=30, inclusive both ends) fits
    /// inside one 365-day chunk, so each region is exactly one unit and one request.
    /// </summary>
    [Fact]
    public void The_default_window_is_one_chunk_per_region()
    {
        var units = Units();

        Assert.Equal(2, units.Count);
        Assert.Equal(new[] { "GulfCoast", "NorthAmerica" }, units.Select(u => u.Region).OrderBy(r => r));

        foreach (var unit in units)
        {
            Assert.Equal(Today.AddDays(-30), unit.Start);
            Assert.Equal(Today, unit.End);

            // 31 days inclusive — the "last 31 days" that was asked for.
            Assert.Equal(31, unit.End.DayNumber - unit.Start.DayNumber + 1);
        }
    }

    /// <summary>
    /// A window wider than the chunk is split, the chunks tile the window exactly — no
    /// gap, no overlap — and the last one is clipped to today.
    /// </summary>
    [Fact]
    public void Chunks_tile_the_window_without_gaps_or_overlaps()
    {
        var units = Units(s =>
        {
            s.DaysBack = 100;
            s.WindowChunkDays = 30;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        var ordered = units.OrderBy(u => u.Start).ToList();

        Assert.Equal(Today.AddDays(-100), ordered[0].Start);
        Assert.Equal(Today, ordered[^1].End);

        for (var i = 1; i < ordered.Count; i++)
            Assert.Equal(ordered[i - 1].End.AddDays(1), ordered[i].Start);

        // Every day of the window is covered exactly once.
        Assert.Equal(101, ordered.Sum(u => u.End.DayNumber - u.Start.DayNumber + 1));
    }

    /// <summary>Newest chunk first, so a run cut short has done the days anyone is watching.</summary>
    [Fact]
    public void Chunks_are_emitted_newest_first_within_a_region()
    {
        var units = Units(s =>
        {
            s.DaysBack = 100;
            s.WindowChunkDays = 30;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        Assert.Equal(Today, units[0].End);
        Assert.True(units[0].Start > units[^1].Start);
    }

    // ------------------------------------------------------------- hot vs settled

    /// <summary>
    /// ⚠ With the shipped 30/30 defaults the settled zone is EMPTY and everything is
    /// hot. That is deliberate: the feed is <c>revision=revised</c>, so the vendor
    /// restates recent weeks, and a stable key would freeze the first value seen while
    /// the loader kept reporting clean runs.
    /// </summary>
    [Fact]
    public void With_the_shipped_defaults_every_unit_is_hot()
    {
        var units = Units();

        Assert.All(units, u => Assert.True(u.IsHot));
        Assert.All(units, u => Assert.EndsWith(":run=20260908", u.Key));
    }

    /// <summary>
    /// A chunk ages by its NEWEST day: one request retrieves the whole chunk, so as long
    /// as any day it covers is hot the chunk must be re-pulled.
    /// </summary>
    [Fact]
    public void A_chunk_is_hot_when_its_newest_day_is_inside_the_settled_horizon()
    {
        var units = Units(s =>
        {
            s.DaysBack = 100;
            s.SettledAfterDays = 30;
            s.WindowChunkDays = 10;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        foreach (var unit in units)
            Assert.Equal(Today.DayNumber - unit.End.DayNumber <= 30, unit.IsHot);

        Assert.Contains(units, u => u.IsHot);
        Assert.Contains(units, u => !u.IsHot);
    }

    /// <summary>A settled chunk's key carries no run token, so it is loaded once and then skipped.</summary>
    [Fact]
    public async Task Settled_keys_are_stable_across_runs_and_hot_keys_are_not()
    {
        var provider = Provider(s =>
        {
            s.DaysBack = 100;
            s.SettledAfterDays = 30;
            s.WindowChunkDays = 10;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        var morning = await provider.GetWorkUnitsAsync(TestHelpers.Context(RunAt));
        var laterSameDay = await provider.GetWorkUnitsAsync(TestHelpers.Context(RunAt.AddHours(6)));

        // Same UTC day under the default RunDate strategy => identical keys throughout.
        Assert.Equal(morning.Select(u => u.Key), laterSameDay.Select(u => u.Key));

        var settled = morning.Where(u => !u.IsHot).ToList();
        Assert.NotEmpty(settled);
        Assert.All(settled, u => Assert.DoesNotContain(":run=", u.Key));
    }

    [Theory]
    [InlineData(GenscapeHotKeyStrategy.RunDate, ":run=20260908")]
    [InlineData(GenscapeHotKeyStrategy.RunHour, ":run=2026090812")]
    public void The_hot_token_follows_the_configured_strategy(GenscapeHotKeyStrategy strategy, string expected)
    {
        var units = Units(s =>
        {
            s.HotKeyStrategy = strategy;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        Assert.EndsWith(expected, Assert.Single(units).Key);
    }

    [Fact]
    public async Task RunId_strategy_re_pulls_on_every_invocation()
    {
        var provider = Provider(s =>
        {
            s.HotKeyStrategy = GenscapeHotKeyStrategy.RunId;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        var first = await provider.GetWorkUnitsAsync(TestHelpers.Context(RunAt, Guid.NewGuid()));
        var second = await provider.GetWorkUnitsAsync(TestHelpers.Context(RunAt, Guid.NewGuid()));

        Assert.NotEqual(first[0].Key, second[0].Key);
    }

    // ------------------------------------------------------------- the key shape

    [Fact]
    public void The_key_names_the_loader_the_feed_the_region_and_the_window()
    {
        var unit = Units(s => s.Regions = new List<string> { "NorthAmerica" }).Single();

        Assert.Equal(
            $"genscape:{GenscapeFeed.CrudeStorageWeekly}:NorthAmerica:2026-08-09..2026-09-08:run=20260908",
            unit.Key);
    }

    /// <summary>
    /// Keys must be unique across feeds, regions and chunks — two units sharing a key
    /// would make the load log skip one of them.
    /// </summary>
    [Fact]
    public void Keys_are_unique_across_every_feed_region_and_chunk()
    {
        var all = GenscapeDescriptors.All
            .SelectMany(feed => Units(s => { s.DaysBack = 100; s.WindowChunkDays = 10; }, feed))
            .ToList();

        Assert.Equal(all.Count, all.Select(u => u.Key).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// ⚠ Changing the chunk size moves the boundaries, so the old keys stop matching and
    /// the window RE-LOADS rather than being skipped. That is the safe direction —
    /// re-merging is idempotent — but it must be a known consequence, not a surprise.
    /// </summary>
    [Fact]
    public void Changing_the_chunk_size_changes_the_keys()
    {
        var thirty = Units(s => { s.DaysBack = 100; s.WindowChunkDays = 30; s.Regions = new List<string> { "NorthAmerica" }; });
        var sixty = Units(s => { s.DaysBack = 100; s.WindowChunkDays = 60; s.Regions = new List<string> { "NorthAmerica" }; });

        Assert.Empty(thirty.Select(u => u.Key).Intersect(sixty.Select(u => u.Key), StringComparer.Ordinal));
    }

    // ------------------------------------------------------------- the guardrails

    /// <summary>
    /// A negative DaysBack must not invert the window and silently produce nothing; a
    /// non-positive chunk size must not produce a zero-length step and loop forever.
    /// </summary>
    [Theory]
    [InlineData(-5, 365)]
    [InlineData(0, 365)]
    [InlineData(30, 0)]
    [InlineData(30, -1)]
    public void Negative_or_zero_settings_are_clamped_rather_than_looping_or_emptying(int daysBack, int chunk)
    {
        var units = Units(s =>
        {
            s.DaysBack = daysBack;
            s.WindowChunkDays = chunk;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        Assert.NotEmpty(units);
        Assert.All(units, u => Assert.True(u.Start <= u.End));
        Assert.Contains(units, u => u.End == Today);
    }

    /// <summary>
    /// A negative SettledAfterDays would otherwise make every chunk settled — history
    /// frozen on first read, revisions never picked up again.
    /// </summary>
    [Fact]
    public void A_negative_SettledAfterDays_still_leaves_the_newest_chunk_hot()
    {
        var units = Units(s =>
        {
            s.SettledAfterDays = -10;
            s.Regions = new List<string> { "NorthAmerica" };
        });

        Assert.True(Assert.Single(units).IsHot);
    }

    /// <summary>
    /// Empty regions yields no units and a warning, rather than a request with no region
    /// — which the API answers 404.
    /// </summary>
    [Fact]
    public void No_regions_yields_no_units()
    {
        Assert.Empty(Units(s => s.Regions = new List<string>()));
        Assert.Empty(Units(s => s.Regions = new List<string> { "", "   " }));
    }

    /// <summary>Blank and duplicate region entries are collapsed rather than duplicating work.</summary>
    [Fact]
    public void Duplicate_and_blank_regions_are_collapsed()
    {
        var units = Units(s => s.Regions = new List<string> { "NorthAmerica", " NorthAmerica ", "northamerica", "" });

        Assert.Single(units);
        Assert.Equal("NorthAmerica", units[0].Region);
    }
}
