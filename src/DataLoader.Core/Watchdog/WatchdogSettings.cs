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
    ///
    /// <para>Lowered from 7 to 2 on 2026-11-02. A stuck run is a persistent
    /// CONDITION, not an event — nothing closes the row, so every hourly pass
    /// re-reports it for the whole window. At 7 days a single killed process
    /// produced roughly 168 identical emails; at 2 days it is about 48, still
    /// far more than enough to notice and act on. The window only governs how
    /// long something is SHOUTED about, never whether it is detected.</para>
    /// </summary>
    public int StuckLookbackDays { get; set; } = 2;

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
    ///
    /// <para>Lowered from 24 to 2 on 2026-11-02. The overlap is the whole point
    /// of the signal, but a 24-hour window means every hourly pass re-sends the
    /// same handful of failures for a full day — failures the reader already
    /// received an in-process email about. Since that in-process path is now
    /// proven working in production (a real alert was delivered on 2026-10-26),
    /// the net only has to cover the short gap where delivery might have failed.
    /// At 2 hours a failure is mentioned at most twice.</para>
    ///
    /// <para>⚠ There is no "off": <c>SqlWatchdogRepository</c> applies
    /// <c>Math.Max(1, …)</c>, so 0 becomes a one-hour window, not no window.</para>
    /// </summary>
    public int ReportRecentFailureHours { get; set; } = 2;

    /// <summary>
    /// Suppress a RECENT FAILURES finding once every one of its failed work units has a
    /// later successful <c>core.LoadLog</c> row for the same loader — see
    /// <see cref="Watchdog.WatchdogFailureResolver"/> for the matching rule.
    ///
    /// <para>Defaulted true from measurement, not assumption: in the 7 days before this
    /// was written, production recorded 37 failed runs and ALL 37 were followed by a
    /// later success. The RECENT FAILURES signal was reporting entirely on problems that
    /// had already fixed themselves — which is exactly what buries the ones that have
    /// NOT — so suppressing self-healed failures by default is what makes the signal
    /// worth reading again.</para>
    ///
    /// <para>Set to false to report exactly as before this feature existed — every
    /// candidate in the window, unfiltered. See <see cref="WatchdogReport.RecentFailures"/>
    /// and <see cref="RecentFailure.UnresolvedUnits"/>.</para>
    /// </summary>
    public bool SkipAlertIfResolved { get; set; } = true;

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
