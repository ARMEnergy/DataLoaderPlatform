namespace DataLoader.Core.Watchdog;

/// <summary>
/// Bound from the <c>Platform:Watchdog</c> section.
///
/// <para>Thresholds are EXPLICIT per loader rather than learned from history.
/// A learned median quietly raises its own goalposts: a loader that has been
/// failing for a week would stop being considered late precisely when it most
/// needs reporting.</para>
/// </summary>
public sealed class WatchdogSettings
{
    public const string SectionName = "Platform:Watchdog";

    /// <summary>
    /// How long a <c>core.LoaderRun</c> row may sit with <c>CompletedAtUtc</c>
    /// NULL before it is treated as a killed process rather than a long run.
    /// </summary>
    public int StuckAfterHours { get; set; } = 6;

    /// <summary>
    /// How far back to look for stuck runs.
    ///
    /// <para>⚠ Without a bound the watchdog re-reports the same ancient rows on
    /// every pass forever. Nothing sweeps <c>core.LoaderRun</c> — the
    /// <c>AbandonStaleLoadLogs</c> sweeper closes <c>core.LoadLog</c> rows only —
    /// so production held 35 orphans going back to August when this was written.
    /// An alert that never clears is one people stop reading, which is worse
    /// than no alert at all.</para>
    /// </summary>
    public int StuckLookbackDays { get; set; } = 7;

    /// <summary>
    /// Report only stuck runs recorded against the machine the watchdog is
    /// running on.
    ///
    /// <para>28 of production's 35 orphans were left by a developer laptop
    /// pointed at the production platform database. A watchdog on
    /// ARMH-OPSDB01 reporting someone's laptop is noise by construction —
    /// it cannot act on it and neither can the reader.</para>
    /// </summary>
    public bool RestrictStuckRunsToThisHost { get; set; } = true;

    /// <summary>
    /// Look-back for reporting runs that recorded <c>Success = 0</c>. This
    /// overlaps with the in-process email on purpose — it is the safety net for
    /// when that email could not be delivered.
    /// </summary>
    public int ReportRecentFailureHours { get; set; } = 24;

    /// <summary>
    /// Per-loader expectations. A loader absent from here is never reported,
    /// which is what keeps build-only loaders that were run by hand once from
    /// alerting forever.
    /// </summary>
    public Dictionary<string, WatchdogLoaderSettings> Loaders { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One loader's expected cadence.</summary>
public sealed class WatchdogLoaderSettings
{
    /// <summary>
    /// Report the loader when this many hours have passed since its last run
    /// STARTED. Seeded from 30-day observed cadence; see the design spec §2.5.
    ///
    /// <para>Defaults to 0, which <see cref="WatchdogEvaluator"/> treats as
    /// MISCONFIGURED rather than healthy — an entry left as
    /// <c>"Platts": {}</c> must not silently vanish from watchdog coverage,
    /// since that is precisely the blindness this component exists to catch.
    /// See <see cref="WatchdogReport.Misconfigured"/>.</para>
    /// </summary>
    public int MaxGapHours { get; set; }

    /// <summary>Set false to mute a loader without deleting its threshold.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Exclude Saturdays and Sundays when measuring how late a loader is.
    ///
    /// <para>⚠ Set this from measured behaviour, never assumed. Thresholds were
    /// originally seeded from the MEAN inter-run gap, which for a weekday-only
    /// feed stays low because most gaps are same-day — so the first weekend
    /// after go-live reported five loaders overdue that were working exactly as
    /// designed. Raising those thresholds to cover a Friday-to-Monday gap
    /// instead would have meant a weekday loader dying on Monday morning going
    /// unreported until Thursday.</para>
    /// </summary>
    public bool SkipWeekends { get; set; }
}
