namespace DataLoader.Core.Watchdog;

/// <summary>
/// Turns run history into a report. A pure function with no database, no clock
/// and no I/O, so every rule here is unit-testable.
/// </summary>
public static class WatchdogEvaluator
{
    /// <summary>
    /// ⚠ <paramref name="nowCentral"/> must be US CENTRAL, matching
    /// <c>core.LoaderRun_Summary.StartTime</c>, which production stores already
    /// converted. Passing a UTC clock adds ~5 hours to every gap and reports
    /// the whole platform overdue — a test pins this.
    ///
    /// <para><paramref name="stuckRuns"/> is already filtered by the query that
    /// produced it, because <c>core.LoaderRun.StartedAtUtc</c> is UTC and the
    /// comparison belongs next to the column, not here where the clock is
    /// Central.</para>
    /// </summary>
    public static WatchdogReport Evaluate(
        IReadOnlyList<LoaderLastRun> lastRuns,
        IReadOnlyList<StuckRun> stuckRuns,
        IReadOnlyList<RecentFailure> recentFailures,
        WatchdogSettings settings,
        DateTime nowCentral)
    {
        var seen = lastRuns
            .GroupBy(r => r.LoaderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(r => r.LastStartCentral), StringComparer.OrdinalIgnoreCase);

        var overdue = new List<OverdueLoader>();

        // Driven by CONFIG, not by the table. A loader nobody configured is
        // never reported — build-only loaders run by hand once would otherwise
        // alert forever. The flip side: a configured loader missing from the
        // table has never run, which is itself overdue.
        foreach (var (loaderName, loaderSettings) in settings.Loaders)
        {
            if (!loaderSettings.Enabled) continue;
            if (loaderSettings.MaxGapHours <= 0) continue;

            if (!seen.TryGetValue(loaderName, out var lastStart))
            {
                overdue.Add(new OverdueLoader(loaderName, null, double.PositiveInfinity, loaderSettings.MaxGapHours));
                continue;
            }

            var hours = (nowCentral - lastStart).TotalHours;

            // Strictly greater-than: a loader on a 24h cadence with a 24h
            // threshold must not alert on ordinary scheduling jitter.
            if (hours > loaderSettings.MaxGapHours)
                overdue.Add(new OverdueLoader(loaderName, lastStart, hours, loaderSettings.MaxGapHours));
        }

        return new WatchdogReport
        {
            Overdue = overdue,
            Stuck = stuckRuns,
            RecentFailures = recentFailures
        };
    }
}
