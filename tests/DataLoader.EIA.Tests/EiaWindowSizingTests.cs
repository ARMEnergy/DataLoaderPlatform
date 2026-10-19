using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// Window sizing and range splitting — the machinery added on 2026-10-16 after the first
/// full run timed out on <c>electricity/rto/daily-fuel-type-data</c>.
///
/// <para>
/// Two separate faults were behind that: a per-attempt-vs-global HTTP timeout bug (see
/// <see cref="EiaResilienceTests"/>) and windows wide enough to need 141 pages, whose
/// later offsets EIA was 503-ing at the time. ⚠ That offset finding was RETRACTED — see
/// docs/apis/EIA.md §7.1; it was an outage artefact. The sizing is kept on unit-count
/// and blast-radius grounds, not because deep offsets are known to fail.
/// Splitting correctness is not cosmetic: an overlap merely wastes requests, but a GAP
/// would be silent data loss recorded in <c>core.LoadLog</c> as a success.
/// </para>
/// </summary>
public sealed class EiaWindowSizingTests
{
    private static EiaPeriodMask Mask(string name) => Enum.Parse<EiaPeriodMask>(name);

    private static EiaPeriod P(string text, string maskName)
    {
        Assert.True(EiaPeriod.TryParse(text, Mask(maskName), out var p), $"could not parse '{text}'");
        return p;
    }

    // ------------------------------------------------------- StepsBetween

    [Theory]
    [InlineData("Year", "2000", "2010", 10)]
    [InlineData("Year", "2000", "2000", 0)]
    [InlineData("Quarter", "2020-Q1", "2021-Q1", 4)]
    [InlineData("Quarter", "2020-Q1", "2020-Q4", 3)]
    [InlineData("Month", "2020-01", "2021-01", 12)]
    [InlineData("Month", "2020-01", "2020-06", 5)]
    [InlineData("Day", "2020-01-01", "2020-01-13", 12)]
    [InlineData("Hour", "2020-01-01T00", "2020-01-03T00", 48)]
    public void Counts_steps_in_calendar_units(string mask, string from, string to, int expected) =>
        Assert.Equal(expected, P(from, mask).StepsBetween(P(to, mask)));

    /// <summary>
    /// Calendar units, not elapsed time: February and a leap year must still be one
    /// month and one year. Dividing a timespan would drift and eventually mis-split.
    /// </summary>
    [Fact]
    public void Step_counting_is_immune_to_month_length_and_leap_years()
    {
        Assert.Equal(1, P("2020-02", "Month").StepsBetween(P("2020-03", "Month")));
        Assert.Equal(1, P("2019-02", "Month").StepsBetween(P("2019-03", "Month")));
        Assert.Equal(1, P("2020", "Year").StepsBetween(P("2021", "Year")));   // 2020 is a leap year
    }

    // ------------------------------------------------------------- Split

    /// <summary>A single period cannot be narrowed — that is the subdivision floor.</summary>
    [Theory]
    [InlineData("Year", "2021")]
    [InlineData("Month", "2021-07")]
    [InlineData("Day", "2021-07-04")]
    [InlineData("Hour", "2021-07-04T13")]
    public void A_single_period_cannot_be_split(string mask, string text)
    {
        var p = P(text, mask);
        Assert.Null(p.Split(p));
    }

    [Theory]
    [InlineData("Year", "2000", "2009")]
    [InlineData("Year", "2000", "2001")]
    [InlineData("Quarter", "2020-Q1", "2026-Q4")]
    [InlineData("Month", "2009-01", "2026-07")]
    [InlineData("Day", "2019-01-01", "2019-12-31")]
    [InlineData("Hour", "2019-01-01T00", "2019-01-03T00")]
    public void Split_halves_are_contiguous_and_cover_the_whole_range(string mask, string from, string to)
    {
        var start = P(from, mask);
        var end = P(to, mask);

        var split = start.Split(end);
        Assert.NotNull(split);
        var (leftEnd, rightStart) = split!.Value;

        // Left half is non-empty and inside the range.
        Assert.True(start.IsAtOrBefore(leftEnd));
        Assert.True(leftEnd.IsAtOrBefore(end));

        // Right half is non-empty and inside the range.
        Assert.True(rightStart.IsAtOrBefore(end));

        // ⚠ No gap and no overlap: rightStart is EXACTLY one step after leftEnd.
        Assert.Equal(rightStart.Format(), leftEnd.AddSteps(1).Format());
        Assert.True(leftEnd.IsBefore(rightStart));

        // The two halves together hold exactly as many periods as the original range.
        var total = start.StepsBetween(end) + 1;
        var left = start.StepsBetween(leftEnd) + 1;
        var right = rightStart.StepsBetween(end) + 1;
        Assert.Equal(total, left + right);
    }

    /// <summary>
    /// Every split must shrink both halves, or recursion would not terminate. The
    /// two-period case is the tight one: it must produce one period each, not a
    /// zero-length half.
    /// </summary>
    [Fact]
    public void Splitting_always_makes_progress()
    {
        var start = P("2020-01-01", "Day");
        var end = P("2020-01-02", "Day");          // exactly two periods

        var (leftEnd, rightStart) = start.Split(end)!.Value;

        Assert.Equal("2020-01-01", leftEnd.Format());
        Assert.Equal("2020-01-02", rightStart.Format());
        Assert.Null(leftEnd.Split(leftEnd));       // both halves are now indivisible
        Assert.Null(rightStart.Split(end));
    }

    /// <summary>
    /// Recursively splitting a real window must partition it exactly: every period
    /// exactly once, in order, with nothing missing.
    /// </summary>
    [Fact]
    public void Recursive_splitting_partitions_a_window_exactly()
    {
        var start = P("2019-01-01", "Day");
        var end = P("2019-12-31", "Day");          // 365 periods, the old daily default

        var leaves = new List<(EiaPeriod From, EiaPeriod To)>();
        Descend(start, end, 0);

        void Descend(EiaPeriod from, EiaPeriod to, int depth)
        {
            Assert.True(depth < 32, "recursion ran away");
            var split = from.Split(to);
            if (split is null) { leaves.Add((from, to)); return; }
            var (leftEnd, rightStart) = split.Value;
            Descend(from, leftEnd, depth + 1);
            Descend(rightStart, to, depth + 1);
        }

        Assert.Equal(365, leaves.Count);
        Assert.Equal(start.Format(), leaves[0].From.Format());
        Assert.Equal(end.Format(), leaves[^1].To.Format());

        for (var i = 1; i < leaves.Count; i++)
            Assert.Equal(leaves[i].From.Format(), leaves[i - 1].To.AddSteps(1).Format());
    }

    // --------------------------------------------- the shipped window sizes

    /// <summary>
    /// ⚠ <c>daily</c> and <c>hourly</c> are FROZEN. 9.7M and 24.1M rows are already
    /// loaded under these boundaries; changing a value here invalidates those settled
    /// resume keys and re-downloads the lot. Their sizing also still satisfies the
    /// one-window-near-the-limit rule, which the densest-member figures below check.
    /// </summary>
    [Theory]
    [InlineData("daily", 12, 2012)]              // ≈24,100
    public void Frozen_window_sizes_still_fit_one_window_in_the_row_limit(
        string frequencyId, int expectedSteps, int measuredRowsPerStep)
    {
        var settings = new EiaSettings();

        Assert.Equal(expectedSteps, settings.WindowSteps[frequencyId]);

        var rowsPerWindow = (long)expectedSteps * measuredRowsPerStep;
        Assert.True(
            rowsPerWindow <= settings.MaxRowsPerWindow * 1.05,
            $"{frequencyId}: {expectedSteps} steps x {measuredRowsPerStep} rows/step = {rowsPerWindow}, " +
            $"over MaxRowsPerWindow {settings.MaxRowsPerWindow}.");
    }

    // ------------------------- hourly sizing, and the lookback-cap guard

    /// <summary>
    /// ⚠ <c>hourly</c> was briefly set to 1 (one hour per request) with a 7-day lookback
    /// cap on 2026-10-16, in response to persistent 504s. Both were REVERTED the same
    /// day: the 504s were an EIA-side outage, and once it cleared, 48 interleaved
    /// requests across six window/facet shapes all succeeded at a 2.1–2.5s median —
    /// including an unfaceted 8-day query covering 91,637 rows. The cap had abandoned
    /// ten months of hourly history for nothing.
    /// </summary>
    [Theory]
    [InlineData("hourly", 48)]
    [InlineData("local-hourly", 48)]
    public void Hourly_keeps_its_original_window_size(string frequencyId, int expectedSteps)
    {
        var settings = new EiaSettings();

        Assert.Equal(expectedSteps, settings.WindowSteps[frequencyId]);
    }

    /// <summary>
    /// ⚠ THE COUPLING GUARD. A narrow window without a bounded lookback explodes the
    /// unit count: 1-hour tiles over the 2019-onward hourly axis would be 70,080 work
    /// units. Nothing trips this today, and that is the point — it fails the build if
    /// someone narrows a window again without thinking about the lookback.
    /// </summary>
    [Theory]
    [InlineData("hourly")]
    [InlineData("local-hourly")]
    [InlineData("daily")]
    public void A_narrow_window_is_always_paired_with_a_lookback_cap(string frequencyId)
    {
        var settings = new EiaSettings();

        var steps = settings.WindowSteps[frequencyId];
        var capped = settings.MaxBackfillDaysByFrequency.TryGetValue(frequencyId, out var days) && days > 0;

        const int FullAxisHours = 70_080;                     // 2019-01-01 to roughly now
        var periodsPerHour = frequencyId == "daily" ? 1 / 24.0 : 1.0;
        var unitsIfUncapped = (int)(FullAxisHours * periodsPerHour) / Math.Max(1, steps);

        if (unitsIfUncapped > 5_000)
            Assert.True(capped,
                $"{frequencyId} at {steps}-step windows would generate {unitsIfUncapped:N0} units over the " +
                "full axis. Set MaxBackfillDaysByFrequency for it, or widen the window.");
    }

    /// <summary>
    /// ⚠ Nothing is capped today, and that matters: a cap ABANDONS history rather than
    /// deferring it. This test is the tripwire for a cap being added silently.
    /// </summary>
    [Fact]
    public void No_frequency_is_capped_by_default()
    {
        var settings = new EiaSettings();

        Assert.Empty(settings.MaxBackfillDaysByFrequency);
    }

    /// <summary>
    /// ⚠ The SPARSE frequencies were widened on 2026-10-16. Sizing them for their
    /// densest member produced 4,398 monthly units across 49 mostly-tiny series and 703
    /// annual units across 77 — 40% of a 12,755-unit backfill, most fetching a few
    /// thousand rows in one request. Now that <see cref="EiaSettings.MaxRowsPerWindow"/>
    /// subdivision exists and is verified, a dense outlier splits itself, so these can be
    /// wide. The assertion is deliberately the opposite of the frozen ones: a window must
    /// be big enough to be worth a work unit.
    /// </summary>
    [Theory]
    [InlineData("monthly", 60)]
    [InlineData("annual", 25)]
    [InlineData("quarterly", 40)]
    public void Sparse_frequencies_use_wide_windows_and_let_dense_members_subdivide(
        string frequencyId, int expectedSteps)
    {
        var settings = new EiaSettings();

        Assert.Equal(expectedSteps, settings.WindowSteps[frequencyId]);
        Assert.True(settings.MaxRowsPerWindow > 0,
            "Wide windows are only safe while subdivision is enabled.");
    }

    /// <summary>
    /// The typical monthly series is small, and the whole point of widening was that such
    /// a series becomes ONE unit instead of ten.
    /// </summary>
    [Fact]
    public void A_typical_sparse_series_now_fits_a_single_window()
    {
        var settings = new EiaSettings();

        // `international` monthly: 744 rows/month, the middle of the monthly pack.
        var rows = 744L * settings.WindowSteps["monthly"];

        Assert.True(rows <= settings.MaxRowsPerWindow * 2,
            $"A 744 rows/month series yields {rows} rows per window — it should need at most one split.");
    }

    /// <summary>
    /// And the offset a full window implies must stay well clear of the cliff. Measured
    /// in a controlled run (idle API, no cached URLs): offsets 0 / 15,000 / 100,000 all
    /// failed 0% with no latency penalty between them; offset 400,000 failed 100%
    /// (4/4 HTTP 503). The deepest confirmed-good offset is therefore 100,000 and the
    /// shallowest confirmed-bad one 400,000 — the cliff is somewhere between, and its
    /// exact position is NOT known, so the shipped sizing keeps a multiple of headroom
    /// rather than creeping up to the last value that happened to work.
    /// </summary>
    [Fact]
    public void Shipped_window_sizes_keep_offsets_far_below_the_cliff()
    {
        const int DeepestConfirmedGood = 100_000;
        const int ShallowestConfirmedBad = 400_000;
        const int RequiredHeadroomFactor = 5;

        var settings = new EiaSettings();
        var deepestOffset = settings.MaxRowsPerWindow - settings.PageSize;

        Assert.True(deepestOffset * RequiredHeadroomFactor <= DeepestConfirmedGood,
            $"A full window pages to offset {deepestOffset}. EIA answered {DeepestConfirmedGood} fine but " +
            $"failed 100% at {ShallowestConfirmedBad}; keep at least {RequiredHeadroomFactor}x headroom " +
            "under the deepest confirmed-good depth.");

        // And nowhere near the depth that is known to fail outright.
        Assert.True(deepestOffset < ShallowestConfirmedBad / 10);
    }

    // ------------------------------------------- per-series step overrides

    [Fact]
    public void A_per_series_override_beats_the_per_frequency_value()
    {
        var settings = new EiaSettings();

        Assert.Equal(1, EiaWorkUnitProvider.ResolveWindowSteps(settings, "international", "annual"));
        Assert.Equal(25, EiaWorkUnitProvider.ResolveWindowSteps(settings, "coal/market-sales-price", "annual"));
    }

    /// <summary>
    /// <c>international</c> publishes 55,711 rows per YEAR, so the 25-year annual default
    /// would now build 1.39M-row windows — far past the point where paging fails. It is
    /// the one series shipped with an override.
    /// </summary>
    [Fact]
    public void The_shipped_override_covers_the_measured_outlier()
    {
        var settings = new EiaSettings();

        Assert.True(settings.WindowStepsBySeries.ContainsKey("international:annual"));

        const int RowsPerYear = 55_711;
        var steps = EiaWorkUnitProvider.ResolveWindowSteps(settings, "international", "annual");
        Assert.Equal(1, steps);

        // Even one year exceeds the limit, which is why the reader must still be able to
        // page a single indivisible period.
        Assert.True((long)steps * RowsPerYear > settings.MaxRowsPerWindow);
    }

    [Fact]
    public void Falls_back_to_the_frequency_then_to_the_default()
    {
        var settings = new EiaSettings
        {
            WindowSteps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["daily"] = 7 },
            WindowStepsBySeries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            DefaultWindowSteps = 3
        };

        Assert.Equal(7, EiaWorkUnitProvider.ResolveWindowSteps(settings, "any/series", "daily"));
        Assert.Equal(3, EiaWorkUnitProvider.ResolveWindowSteps(settings, "any/series", "fortnightly"));
    }

    [Fact]
    public void Override_lookup_is_case_insensitive_on_the_key()
    {
        var settings = new EiaSettings
        {
            WindowStepsBySeries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["International:Annual"] = 2
            }
        };

        Assert.Equal(2, EiaWorkUnitProvider.ResolveWindowSteps(settings, "international", "annual"));
    }

    // ------------------------------------------- excluded frequencies

    /// <summary>
    /// <c>local-hourly</c> ships excluded: 1,460 units / 27.7M rows covering the SAME
    /// instants as <c>hourly</c>, differing only in that each row is labelled in its
    /// respondent's local offset rather than UTC. Operator decision, 2026-10-16.
    /// </summary>
    [Fact]
    public void Local_hourly_ships_excluded()
    {
        var settings = new EiaSettings();

        Assert.Contains("local-hourly", settings.ExcludedFrequencies, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("hourly", settings.ExcludedFrequencies, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ Excluding a frequency must NOT disturb the sizing of anything else — its
    /// <see cref="EiaSettings.WindowSteps"/> entry stays so that re-enabling it loads the
    /// same window boundaries it would always have had, rather than re-tiling the axis.
    /// </summary>
    [Fact]
    public void Excluding_a_frequency_leaves_its_window_sizing_intact()
    {
        var settings = new EiaSettings();

        Assert.True(settings.WindowSteps.ContainsKey("local-hourly"));
        Assert.Equal(settings.WindowSteps["hourly"], settings.WindowSteps["local-hourly"]);
    }

    [Fact]
    public void An_empty_exclusion_list_excludes_nothing()
    {
        var settings = new EiaSettings { ExcludedFrequencies = Array.Empty<string>() };

        Assert.Empty(settings.ExcludedFrequencies);
    }

    [Fact]
    public void A_non_positive_configured_value_is_ignored_rather_than_producing_an_empty_window()
    {
        var settings = new EiaSettings
        {
            WindowSteps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["daily"] = 0 },
            WindowStepsBySeries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["s:daily"] = -5
            },
            DefaultWindowSteps = 9
        };

        Assert.Equal(9, EiaWorkUnitProvider.ResolveWindowSteps(settings, "s", "daily"));
    }
}
