using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// Narrowing a hot window's read to its revisable tail.
///
/// <para>
/// ⚠ <b>Why this needs careful tests.</b> The sparse frequencies use wide windows —
/// <c>annual</c> is 25 years — and the FINAL window of every axis ends in the future, so
/// it never settles and is re-read on every run. Measured 2026-10-17: 186 hot units per
/// day, ~1.4 hours, of which annual alone was 67. Narrowing fixes that, but the failure
/// mode if it is wrong is silent data loss: a window narrowed before it has ever been
/// read in full would skip its older half today, skip it again tomorrow under a fresh
/// run token, and eventually settle having never loaded it.
/// </para>
/// </summary>
public sealed class EiaHotWindowNarrowingTests
{
    private const string Base =
        "eia:series=petroleum/stoc/cu;freq=annual;from=2006;to=2030";

    private static IReadOnlySet<string> History(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.Ordinal);

    // --------------------------------------------- the history predicate

    /// <summary>
    /// A hot window's recorded keys always carry a run token, so the test matches on
    /// the base plus the <c>;run=</c> separator.
    /// </summary>
    [Fact]
    public void A_window_completed_under_a_previous_token_counts_as_read_in_full()
    {
        var history = History(Base + ";run=20261016");

        Assert.True(EiaLoadHistory.HasEverCompleted(history, Base));
    }

    [Fact]
    public void A_window_never_completed_does_not_count()
    {
        var history = History(
            "eia:series=other/series;freq=annual;from=2006;to=2030;run=20261016",
            "eia:series=petroleum/stoc/cu;freq=monthly;from=2006-01;to=2010-12;run=20261016");

        Assert.False(EiaLoadHistory.HasEverCompleted(history, Base));
    }

    /// <summary>
    /// ⚠ The separator in the prefix is load-bearing. Without it, a DIFFERENT window
    /// whose key merely starts with the same text — <c>…to=2030</c> vs <c>…to=20301</c>,
    /// or a longer period rendering — would satisfy the check and wrongly authorise
    /// narrowing.
    /// </summary>
    [Fact]
    public void A_longer_unrelated_key_with_the_same_prefix_does_not_count()
    {
        var history = History(
            "eia:series=petroleum/stoc/cu;freq=annual;from=2006;to=20301;run=20261016");

        Assert.False(EiaLoadHistory.HasEverCompleted(history, Base));
    }

    [Fact]
    public void An_empty_history_authorises_nothing()
    {
        Assert.False(EiaLoadHistory.HasEverCompleted(History(), Base));
    }

    // ------------------------------------ what narrowing does to the read

    private static EiaWorkUnit Unit(string windowStart, string windowEnd, string? readStart, string maskName)
    {
        var mask = Enum.Parse<EiaPeriodMask>(maskName);
        EiaPeriod.TryParse(windowStart, mask, out var ws);
        EiaPeriod.TryParse(windowEnd, mask, out var we);
        EiaPeriod.TryParse(readStart ?? windowStart, mask, out var rs);

        var dataTypes = new[] { TestHelpers.DataType("value", "petroleum/stoc/cu") };
        var plan = TestHelpers.Plan(
            TestHelpers.Series("petroleum/stoc/cu", windowStart, windowEnd, "YYYY", "annual"),
            new[] { TestHelpers.Frequency("annual", "YYYY", "petroleum/stoc/cu") },
            dataTypes);

        return new EiaWorkUnit
        {
            Plan = plan,
            Frequency = plan.Frequencies[0],
            WindowStart = ws,
            ReadStart = rs,
            WindowEnd = we,
            Settled = false,
            HotToken = "20261017"
        };
    }

    /// <summary>
    /// ⚠ THE CENTRAL INVARIANT. Narrowing must not change a window's IDENTITY, or every
    /// hot window would get a new key the day narrowing turns on and the whole axis
    /// would re-download.
    /// </summary>
    [Fact]
    public void Narrowing_does_not_change_the_resume_key()
    {
        var full = Unit("2006", "2030", readStart: null, "Year");
        var narrow = Unit("2006", "2030", readStart: "2026", "Year");

        Assert.Equal(full.Key, narrow.Key);
        Assert.Equal("eia:series=petroleum/stoc/cu;freq=annual;from=2006;to=2030;run=20261017", narrow.Key);
    }

    [Fact]
    public void Narrowing_does_not_change_the_display_name()
    {
        var full = Unit("2006", "2030", readStart: null, "Year");
        var narrow = Unit("2006", "2030", readStart: "2026", "Year");

        Assert.Equal(full.DisplayName, narrow.DisplayName);
    }

    [Fact]
    public void IsNarrowed_reports_only_a_genuinely_moved_start()
    {
        Assert.False(Unit("2006", "2030", readStart: null, "Year").IsNarrowed);
        Assert.False(Unit("2006", "2030", readStart: "2006", "Year").IsNarrowed);
        Assert.True(Unit("2006", "2030", readStart: "2026", "Year").IsNarrowed);
    }

    /// <summary>
    /// The read range must still reach the window's end — narrowing moves the START
    /// only. A narrowed unit that stopped short would lose the newest data, which is
    /// the whole point of it being hot.
    /// </summary>
    [Fact]
    public void A_narrowed_unit_still_reads_through_the_window_end()
    {
        var narrow = Unit("2006", "2030", readStart: "2026", "Year");

        Assert.Equal("2030", narrow.WindowEnd.Format());
        Assert.True(narrow.ReadStart.IsBefore(narrow.WindowEnd));
    }

    // ----------------------------------------------------- the setting

    [Fact]
    public void Narrowing_is_on_by_default()
    {
        Assert.True(new EiaSettings().NarrowHotWindowReads);
    }

    /// <summary>
    /// The saving is only worth having because the hot tail is what the wide windows
    /// made expensive: a 25-year annual window re-read daily to catch the current year.
    /// </summary>
    [Fact]
    public void The_narrowed_span_is_far_smaller_than_the_window_it_replaces()
    {
        var settings = new EiaSettings();
        var windowYears = settings.WindowSteps["annual"];
        var tailDays = settings.SettledAfterDays;

        Assert.True(tailDays < windowYears * 365 / 10,
            $"A {tailDays}-day tail against a {windowYears}-year window is not worth the mechanism.");
    }
}
