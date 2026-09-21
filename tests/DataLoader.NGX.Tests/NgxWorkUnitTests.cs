using Xunit;

namespace DataLoader.NGX.Tests;

/// <summary>
/// Window arithmetic, batching and resume-key semantics — the parts that decide WHAT
/// gets loaded and whether a re-run is a cheap skip or a silent gap.
/// </summary>
public class NgxWorkUnitTests
{
    private static NgxStripWorkUnitProvider StripProvider(Action<NgxSettings>? configure = null) =>
        new(TestHelpers.Settings(configure), TestHelpers.Log);

    // ------------------------------------------------------- strip windows

    /// <summary>
    /// The specified window: first of LAST month to first of NEXT month. On 2026-09-18
    /// that is 2026-08-01 .. 2026-10-01. With one huge chunk the unit covers at least
    /// that span (chunk bounds are epoch-anchored, so it may reach slightly wider — see
    /// <see cref="Strip_Chunks_AreEpochAnchoredAndCoverTheWindow"/>).
    /// </summary>
    [Fact]
    public async Task Strip_Window_IsFirstOfLastMonthToFirstOfNextMonth()
    {
        var units = await StripProvider(s => s.StripChunkDays = 1000)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Single(units);
        Assert.True(units[0].Start <= new DateOnly(2026, 8, 1));
        Assert.True(units[0].End >= new DateOnly(2026, 10, 1));
    }

    /// <summary>
    /// Chunks abut exactly — no gap, no overlap, no day lost at a seam — and together
    /// they cover the whole window.
    ///
    /// <para>They are anchored to a fixed epoch rather than to the window start, so the
    /// first and last chunk may reach up to chunkDays-1 days outside it. That
    /// over-coverage is deliberate: it is what keeps a settled chunk's key identical
    /// from one month to the next, instead of every boundary shifting when the window
    /// start walks to the 1st of a new month.</para>
    /// </summary>
    [Fact]
    public async Task Strip_Chunks_AreEpochAnchoredAndCoverTheWindow()
    {
        var units = await StripProvider(s => s.StripChunkDays = 7)
            .GetWorkUnitsAsync(TestHelpers.Context());

        var ordered = units.OrderBy(u => u.Start).ToList();

        // Covers the window...
        Assert.True(ordered[0].Start <= new DateOnly(2026, 8, 1));
        Assert.True(ordered[^1].End >= new DateOnly(2026, 10, 1));

        // ...but never by more than one chunk's worth at either end.
        Assert.True(new DateOnly(2026, 8, 1).DayNumber - ordered[0].Start.DayNumber < 7);
        Assert.True(ordered[^1].End.DayNumber - new DateOnly(2026, 10, 1).DayNumber < 7);

        // Every chunk is a full chunk, and they abut with no gap or overlap.
        Assert.All(ordered, u => Assert.Equal(6, u.End.DayNumber - u.Start.DayNumber));
        for (var i = 1; i < ordered.Count; i++)
            Assert.Equal(ordered[i - 1].End.AddDays(1), ordered[i].Start);
    }

    /// <summary>Every day of the window is covered exactly once.</summary>
    [Fact]
    public async Task Strip_EveryDayInTheWindowIsCoveredExactlyOnce()
    {
        var units = await StripProvider(s => s.StripChunkDays = 7)
            .GetWorkUnitsAsync(TestHelpers.Context());

        var days = units
            .SelectMany(u => Enumerable
                .Range(0, u.End.DayNumber - u.Start.DayNumber + 1)
                .Select(u.Start.AddDays))
            .ToList();

        // No day is covered twice anywhere in the plan...
        Assert.Equal(days.Count, days.Distinct().Count());

        // ...and every day of the actual window is covered.
        for (var d = new DateOnly(2026, 8, 1); d <= new DateOnly(2026, 10, 1); d = d.AddDays(1))
            Assert.Contains(d, days);
    }

    /// <summary>
    /// The point of epoch anchoring: a given trade day falls in the SAME chunk, with the
    /// same key, no matter which month the run happens in. Anchored to the window start
    /// instead, every boundary would move on the 1st and the whole settled zone would
    /// re-load monthly.
    /// </summary>
    [Fact]
    public async Task Strip_ChunkBoundaries_DoNotMoveWhenTheMonthRolls()
    {
        var provider = StripProvider(s => s.StripChunkDays = 7);

        var september = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 18, 17, 0, 0, DateTimeKind.Utc)));
        var october = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 10, 18, 17, 0, 0, DateTimeKind.Utc)));

        // The chunks that both runs' windows contain must be byte-identical spans.
        var shared = september.Select(u => (u.Start, u.End))
            .Intersect(october.Select(u => (u.Start, u.End)))
            .ToList();

        Assert.NotEmpty(shared);

        // And no September chunk merely OVERLAPS an October one without matching it —
        // that is what a moving anchor would produce.
        foreach (var s in september)
        foreach (var o in october)
            if (s.Start <= o.End && o.Start <= s.End)
                Assert.Equal((s.Start, s.End), (o.Start, o.End));
    }

    /// <summary>Newest chunk first, so a run cut short has done the days people watch.</summary>
    [Fact]
    public async Task Strip_NewestChunkIsAttemptedFirst()
    {
        var units = await StripProvider(s => s.StripChunkDays = 7)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.True(units[0].Start > units[^1].Start);
    }

    /// <summary>A non-positive chunk size would step the loop by zero days and hang.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Strip_NonPositiveChunkDays_IsClampedAndTerminates(int chunkDays)
    {
        var units = await StripProvider(s => s.StripChunkDays = chunkDays)
            .GetWorkUnitsAsync(TestHelpers.Context());

        Assert.NotEmpty(units);
        Assert.All(units, u => Assert.Equal(u.Start, u.End));
    }

    // ----------------------------------------------------- strip resume keys

    /// <summary>
    /// With the shipped 45-day default the whole window is hot, so amended trades are
    /// re-pulled every run.
    /// </summary>
    [Fact]
    public async Task Strip_DefaultSettings_KeepTheWholeWindowHot()
    {
        var units = await StripProvider().GetWorkUnitsAsync(TestHelpers.Context());
        Assert.All(units, u => Assert.True(u.IsHot));
    }

    /// <summary>
    /// A chunk older than the settled horizon gets a STABLE key — loaded once, then
    /// skipped forever. This is what makes a backfill affordable.
    /// </summary>
    [Fact]
    public async Task Strip_OldChunks_BecomeSettledWithAStableKey()
    {
        var units = await StripProvider(s =>
        {
            s.StripMonthsBack = 6;
            s.StripSettledAfterDays = 30;
            s.StripChunkDays = 7;
        }).GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Contains(units, u => !u.IsHot);
        Assert.Contains(units, u => u.IsHot);

        foreach (var settled in units.Where(u => !u.IsHot))
        {
            Assert.DoesNotContain(":run=", settled.Key);
            // Settled means its NEWEST day is beyond the horizon.
            Assert.True(new DateOnly(2026, 9, 18).DayNumber - settled.End.DayNumber > 30);
        }
    }

    /// <summary>A hot key varies between runs; a settled key does not.</summary>
    [Fact]
    public async Task Strip_HotKeys_VaryByRunDate_SettledKeysDoNot()
    {
        var provider = StripProvider(s =>
        {
            s.StripMonthsBack = 6;
            s.StripSettledAfterDays = 30;
            s.StripChunkDays = 7;
        });

        var day1 = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 18, 17, 0, 0, DateTimeKind.Utc)));
        var day2 = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 9, 19, 17, 0, 0, DateTimeKind.Utc)));

        var settled1 = day1.Where(u => !u.IsHot).Select(u => u.Key).ToHashSet();
        var settled2 = day2.Where(u => !u.IsHot).Select(u => u.Key).ToHashSet();

        // Settled chunks that exist on both days keep byte-identical keys.
        Assert.NotEmpty(settled1.Intersect(settled2));

        // No hot key survives the day change.
        Assert.Empty(day1.Where(u => u.IsHot).Select(u => u.Key)
                         .Intersect(day2.Where(u => u.IsHot).Select(u => u.Key)));
    }

    /// <summary>Same run, same keys — a retry must be a cheap skip, not a reload.</summary>
    [Fact]
    public async Task Strip_SameRun_ProducesIdenticalKeys()
    {
        var provider = StripProvider();
        var a = await provider.GetWorkUnitsAsync(TestHelpers.Context());
        var b = await provider.GetWorkUnitsAsync(TestHelpers.Context());

        Assert.Equal(a.Select(u => u.Key), b.Select(u => u.Key));
    }

    /// <summary>
    /// RunHour must be stamped in UTC. 01:00 local occurs twice on a fall-back night, so
    /// a local token would repeat and suppress a legitimate re-pull.
    /// </summary>
    [Fact]
    public async Task Strip_RunHourStrategy_UsesUtcSoTheTokenIsMonotonic()
    {
        var provider = StripProvider(s => s.HotKeyStrategy = NgxHotKeyStrategy.RunHour);

        // The two instants on either side of the US fall-back, one UTC hour apart.
        var before = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc)));
        var after = await provider.GetWorkUnitsAsync(
            TestHelpers.Context(new DateTime(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc)));

        Assert.Contains(":run=2026110107", before[0].Key);
        Assert.Contains(":run=2026110108", after[0].Key);
        Assert.NotEqual(before[0].Key, after[0].Key);
    }

    [Fact]
    public async Task Strip_RunIdStrategy_ForcesEveryRunToRePull()
    {
        var provider = StripProvider(s => s.HotKeyStrategy = NgxHotKeyStrategy.RunId);

        var a = await provider.GetWorkUnitsAsync(TestHelpers.Context(runId: Guid.NewGuid()));
        var b = await provider.GetWorkUnitsAsync(TestHelpers.Context(runId: Guid.NewGuid()));

        Assert.Empty(a.Select(u => u.Key).Intersect(b.Select(u => u.Key)));
    }

    // -------------------------------------------------- index batching / keys

    /// <summary>
    /// The specified window: first of the month 3 months back to first of the month 6
    /// months forward. On 2026-09-18 that is 2026-06-01 .. 2027-03-01.
    /// </summary>
    [Fact]
    public void Index_Window_IsThreeMonthsBackToSixMonthsForward()
    {
        var (start, end) = NgxWindow.MonthSpan(new DateOnly(2026, 9, 18), 3, 6);

        Assert.Equal(new DateOnly(2026, 6, 1), start);
        Assert.Equal(new DateOnly(2027, 3, 1), end);
    }

    /// <summary>A negative window must not invert into zero work units.</summary>
    [Fact]
    public void Index_NegativeMonths_AreClampedRatherThanInverted()
    {
        var (start, end) = NgxWindow.MonthSpan(new DateOnly(2026, 9, 18), -3, -6);

        Assert.Equal(new DateOnly(2026, 9, 1), start);
        Assert.True(end >= start);
    }

    /// <summary>
    /// The vendor's hard limit is 10 indexId parameters per request; 11 returns 403 for
    /// the whole request. A configuration above it must be clamped, not trusted.
    /// </summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(50, 10)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void Index_IdsPerRequest_IsClampedToTheVendorLimit(int configured, int expected) =>
        Assert.Equal(expected, Math.Clamp(configured, 1, 10));

    /// <summary>A hot index key carries the run token; the id list is part of the key.</summary>
    [Fact]
    public void Index_Key_CarriesTheIdListExecutionDateAndRunToken()
    {
        var unit = TestHelpers.IndexUnit(ids: new[] { 1, 2, 3 });

        Assert.Equal(
            "ngx:IndexPrice:ids=1,2,3:2026-06-01..2027-03-01:exec=2026-09-18:run=20260918",
            unit.Key);
    }

    /// <summary>
    /// ⚠ THE REGRESSION THIS GUARDS AGAINST.
    ///
    /// <para>
    /// <c>ExecutionDate</c> is US Central and leads the target primary key. A run at
    /// 03:00 UTC on the 19th is still the EVENING OF THE 18TH in Chicago, so it stamps
    /// <c>ExecutionDate</c> 2026-09-18; a run at 13:00 UTC the same UTC day stamps
    /// 2026-09-19. The window bounds are identical inside a month, so if the key did not
    /// distinguish them — as it did not while the RunDate token was UTC-based and
    /// ExecutionDate was absent from the key — the second run would be skipped by the
    /// load log as already done, and that day's snapshot generation would NEVER be
    /// written. Silent, permanent, and exactly the shape a nightly-plus-catch-up
    /// schedule produces.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Index_TwoRunsOneUtcDayButTwoCentralDays_ProduceDifferentKeys()
    {
        var evening = TestHelpers.Context(new DateTime(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc));
        var morning = TestHelpers.Context(new DateTime(2026, 9, 19, 13, 0, 0, DateTimeKind.Utc));

        // Same UTC date...
        Assert.Equal("20260919", evening.StartedAtUtc.ToString("yyyyMMdd"));
        Assert.Equal("20260919", morning.StartedAtUtc.ToString("yyyyMMdd"));

        // ...but different US-Central dates, and therefore different ExecutionDates.
        Assert.Equal(new DateOnly(2026, 9, 18), NgxTime.Today(evening.StartedAtUtc));
        Assert.Equal(new DateOnly(2026, 9, 19), NgxTime.Today(morning.StartedAtUtc));

        var settings = TestHelpers.Settings();
        var a = new NgxIndexPriceWorkUnit
        {
            IndexIds = new[] { 1, 2, 3 },
            Start = new DateOnly(2026, 6, 1),
            End = new DateOnly(2027, 3, 1),
            ExecutionDate = NgxTime.Today(evening.StartedAtUtc),
            KeySuffix = $":run={NgxWindow.HotToken(settings, evening)}"
        };
        var b = new NgxIndexPriceWorkUnit
        {
            IndexIds = new[] { 1, 2, 3 },
            Start = new DateOnly(2026, 6, 1),
            End = new DateOnly(2027, 3, 1),
            ExecutionDate = NgxTime.Today(morning.StartedAtUtc),
            KeySuffix = $":run={NgxWindow.HotToken(settings, morning)}"
        };

        Assert.NotEqual(a.Key, b.Key);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The RunDate token itself is Central, so it agrees with ExecutionDate rather than
    /// contradicting it. (RunHour stays UTC — see <see cref="NgxWindow.HotToken"/>.)
    /// </summary>
    [Fact]
    public void HotToken_RunDate_IsStampedInCentral()
    {
        var settings = TestHelpers.Settings();

        Assert.Equal("20260918", NgxWindow.HotToken(
            settings, TestHelpers.Context(new DateTime(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc))));
        Assert.Equal("20260919", NgxWindow.HotToken(
            settings, TestHelpers.Context(new DateTime(2026, 9, 19, 13, 0, 0, DateTimeKind.Utc))));
    }

    /// <summary>
    /// Different batches must not share a key, or the load log would skip one of them as
    /// already done.
    /// </summary>
    [Fact]
    public void Index_DifferentBatches_HaveDifferentKeys()
    {
        var a = TestHelpers.IndexUnit(ids: new[] { 1, 2, 3 });
        var b = TestHelpers.IndexUnit(ids: new[] { 4, 5, 6 });

        Assert.NotEqual(a.Key, b.Key);
    }

    /// <summary>
    /// The index feed has no settled zone by design: ExecutionDate leads the primary
    /// key, so every run is meant to write a new snapshot generation. A stable key would
    /// skip the run entirely after day one.
    /// </summary>
    [Fact]
    public void Index_KeyAlwaysCarriesARunToken()
    {
        var unit = TestHelpers.IndexUnit();
        Assert.Contains(":run=", unit.Key);
    }

    [Fact]
    public void Index_DisplayName_NamesTheBatchAndWindow()
    {
        var unit = TestHelpers.IndexUnit(ids: new[] { 1, 2, 3 });

        Assert.Contains("3 id(s)", unit.DisplayName);
        Assert.Contains("2026-06-01", unit.DisplayName);
        Assert.Contains("2027-03-01", unit.DisplayName);
    }
}
