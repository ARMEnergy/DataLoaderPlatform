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
    /// Record each pass and its findings in <c>core.WatchdogPass</c> /
    /// <c>core.WatchdogFinding</c> via <c>core.usp_RecordWatchdogPass</c>
    /// (<c>sql/Core/007</c>). Set false to switch the sink off without a redeploy —
    /// detection and email are unaffected either way.
    ///
    /// <para>⚠ Requires <c>007_CreateWatchdogFindings.sql</c> deployed FIRST. Without
    /// it every pass logs a persistence failure and adds a "NOT persisted" line to the
    /// email (when one is sent) — noisy, never harmful.</para>
    /// </summary>
    public bool PersistFindings { get; set; } = true;

    /// <summary>
    /// Total budget for one pass's persistence (read open findings, resolve FAILED
    /// hints, call the proc). Persistence runs BEFORE the email so the email can say
    /// whether it worked, so this is also the most it can delay an alert.
    /// </summary>
    public int PersistTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Per-loader expectations. A loader absent from here is never reported,
    /// which is what keeps build-only loaders that were run by hand once from
    /// alerting forever.
    /// </summary>
    public Dictionary<string, WatchdogLoaderSettings> Loaders { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>DATA_VOLUME</c> signal — "ran successfully but wrote an unusual number of
    /// rows". Binds <c>Platform:Watchdog:DataVolume</c>. OFF by default; see
    /// <see cref="WatchdogDataVolumeSettings"/> and
    /// <c>docs/superpowers/specs/2026-11-20-watchdog-data-volume-design.md</c>.
    /// </summary>
    public WatchdogDataVolumeSettings DataVolume { get; set; } = new();
}

/// <summary>
/// Platform-wide settings for the <c>DATA_VOLUME</c> signal
/// (<see cref="WatchdogVolumeEvaluator"/>).
///
/// <para>Unlike every other watchdog threshold this one IS learned from history — a
/// same-weekday median — because "how many rows is normal" differs by orders of
/// magnitude between loaders and nobody can hand-seed it. The learned-goalposts trap the
/// comment on <see cref="WatchdogSettings"/> warns about is closed by EXCLUSION: a day
/// that was ever reported (and not later confirmed normal by a rerun) never becomes a
/// baseline sample. A legitimate level change is DECLARED, not learned — see
/// <see cref="WatchdogLoaderVolumeSettings.BaselineFromCentral"/>.</para>
///
/// <para>⚠ It measures rows WRITTEN (<c>core.LoaderRun_Summary.TotalRecords</c>), not rows
/// available: a merge/hash loader counts only changed rows and a settled unit counts 0. A
/// code change that stops needless re-writes (ICE 2026-11-11, AGSI 2026-11-17) is a real,
/// permanent drop that needs a <c>BaselineFromCentral</c>.</para>
///
/// <para>⚠ An invalid section (see <see cref="WatchdogVolumeEvaluator.ValidateSection"/>)
/// skips the WHOLE check with a Warning: no query, <c>DATA_VOLUME</c> neither checked nor
/// degraded, so nothing opens and nothing closes. It is never a MISCONFIGURED finding —
/// that signal means "a loader's cadence threshold is unusable".</para>
/// </summary>
public sealed class WatchdogDataVolumeSettings
{
    /// <summary>
    /// Master switch. With it off and no open <c>DATA_VOLUME</c> findings, a pass is
    /// byte-for-byte what it was before this signal existed. With it off and findings left
    /// open, the sink closes them as <c>LoaderDisabled</c> (design §A4).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Also EMAIL anomalies (a "ROW VOLUME" section, part of the subject and of
    /// <see cref="WatchdogReport.ShouldNotify"/>). Off by default: findings are persisted
    /// for the DBA Agent and the email is unchanged — including a <c>VOLUME_QUERY</c>
    /// degradation, which is persisted but not mailed while this is off.
    /// </summary>
    public bool Notify { get; set; }

    /// <summary>
    /// How many completed Central days to judge: <c>today − WindowDays … today − 1</c>
    /// (shifted back one day before <see cref="SettleHours"/>). Today is never judged — it is
    /// not over. A finding whose day leaves this window closes as <c>AgedOutUnresolved</c>,
    /// never as fixed. Must be in [1, 7]: a wider window would judge a day against samples
    /// that overlap the window itself.
    /// </summary>
    public int WindowDays { get; set; } = 3;

    /// <summary>
    /// Yesterday joins the window only once the Central time of day is at least this many
    /// hours; before that the window ends at <c>today − 2</c> (still <see cref="WindowDays"/>
    /// days long). Must be in [0, 23].
    ///
    /// <para>⚠ Why: a <c>core.LoaderRun_Summary</c> row is written only when its run
    /// COMPLETES, and production runs start as late as 23:00 Central and last up to 126
    /// minutes. A pass at 00:05 would otherwise judge a yesterday still missing its last
    /// runs and raise a false Low. An open finding for a day newer than the window end is
    /// kept open (<c>KeepOpen</c>), never aged out.</para>
    /// </summary>
    public int SettleHours { get; set; } = 3;

    /// <summary>How many same-weekday samples make a baseline (the most recent qualifying ones).</summary>
    public int BaselineWeeks { get; set; } = 6;

    /// <summary>
    /// How far back the sample walk may go, in weeks, stepping over excluded and missing
    /// days. Must be ≥ <see cref="BaselineWeeks"/>. Default 18 = 3 × 6.
    ///
    /// <para>⚠ Why not a fixed 6-week set: after a drop that never recovers, all 6 samples
    /// are themselves excluded (reported) days, the loader drops to "not enough history"
    /// and goes SILENT — the opposite of reporting a persistent problem. Walking further
    /// back keeps the pre-drop level as the baseline for up to 18 weeks, until the owner
    /// sets <c>BaselineFromCentral</c>.</para>
    ///
    /// <para>⚠ <c>WindowDays + 7 × MaxBaselineLookbackWeeks</c> must stay below 180, the
    /// resolved-finding retention in <c>core.usp_RecordWatchdogPass</c> (<c>sql/Core/007</c>).
    /// Exclusion reads the findings table: once a reported day's finding is purged, that
    /// day silently turns back into a "normal" baseline sample — the goalposts move.</para>
    /// </summary>
    public int MaxBaselineLookbackWeeks { get; set; } = 18;

    /// <summary>Fewer qualifying samples than this → the day is not judged (an open finding for it stays open).</summary>
    public int MinSamples { get; set; } = 4;

    /// <summary>
    /// A median below this many rows is too small to judge (a 12-row loader halving is
    /// noise). Applies to the DAILY total, not to individual runs.
    /// </summary>
    public int MinMedianRows { get; set; } = 100;

    /// <summary>Report Low when rows &lt; median × LowPercent / 100. Must be in [1, 99].</summary>
    public int LowPercent { get; set; } = 50;

    /// <summary>
    /// Report High when rows &gt; median × HighMultiple. Must be &gt; 1. Default 3 rather than
    /// 2 because an ordinary manual rerun doubles a day.
    /// </summary>
    public double HighMultiple { get; set; } = 3;
}

/// <summary>
/// One loader's overrides for the <c>DATA_VOLUME</c> signal
/// (<c>Platform:Watchdog:Loaders:&lt;Loader&gt;:Volume</c>). Every override is optional;
/// null means "use the platform value".
///
/// <para>An invalid effective value (or an unparseable <see cref="BaselineFromCentral"/>)
/// skips THAT loader with a Warning; its open findings are kept open (<c>KeepOpen</c>),
/// never closed, and never reported as MISCONFIGURED.</para>
/// </summary>
public sealed class WatchdogLoaderVolumeSettings
{
    /// <summary>Set false to mute this loader's volume check only. Its open findings close as <c>LoaderDisabled</c>.</summary>
    public bool Enabled { get; set; } = true;

    public int? LowPercent { get; set; }
    public double? HighMultiple { get; set; }
    public int? MinMedianRows { get; set; }

    /// <summary>
    /// <c>yyyy-MM-dd</c>, a CENTRAL date. Samples before it are ignored: the owner declares
    /// "from this day on, this level is normal". The loader is then silent until
    /// <c>MinSamples</c> new same-weekday samples exist, and an open finding on or after
    /// this day closes as <c>NotDetected</c>.
    ///
    /// <para>A STRING parsed exactly with InvariantCulture, not a <c>DateTime</c>: the
    /// config binder would accept <c>11/12/2026</c> in whatever culture the box runs, and a
    /// date that silently means the wrong day is worse than one that fails validation.</para>
    /// </summary>
    public string? BaselineFromCentral { get; set; }
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

    /// <summary>
    /// <c>DATA_VOLUME</c> overrides for this loader; null means "platform values,
    /// enabled". Only consulted when <see cref="WatchdogDataVolumeSettings.Enabled"/> is on.
    /// </summary>
    public WatchdogLoaderVolumeSettings? Volume { get; set; }
}
