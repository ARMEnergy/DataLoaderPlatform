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
    /// </summary>
    public int MaxGapHours { get; set; }

    /// <summary>Set false to mute a loader without deleting its threshold.</summary>
    public bool Enabled { get; set; } = true;
}
