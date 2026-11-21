namespace DataLoader.Core.Watchdog;

/// <summary>
/// A loader's most recent run start, as recorded in
/// <c>core.LoaderRun_Summary</c>.
///
/// <para>⚠ <c>LastStartCentral</c> is US CENTRAL, not UTC — production's
/// <c>usp_CompleteLoaderRun</c> converts before storing. The name says so
/// because mixing it with a UTC clock reports the entire platform overdue.</para>
/// </summary>
public sealed record LoaderLastRun(string LoaderName, DateTime LastStartCentral);

/// <summary>
/// A <c>core.LoaderRun</c> row that never closed. Its timestamp IS UTC — a
/// different table with a different convention from <see cref="LoaderLastRun"/>.
/// </summary>
public sealed record StuckRun(Guid RunId, DateTime StartedAtUtc, string? HostName);

/// <summary>
/// A run that recorded <c>Success = 0</c>. Timestamp is Central.
///
/// <para><see cref="UnresolvedUnits"/> is how many of this run's failed work units still
/// have NO later successful <c>core.LoadLog</c> row, per
/// <see cref="Watchdog.WatchdogFailureResolver"/>. When
/// <see cref="WatchdogSettings.SkipAlertIfResolved"/> is off, nothing is evaluated and
/// this equals <see cref="UnitsFailed"/> — "unresolved" here means "not checked", not
/// "confirmed still broken", which is exactly the pre-feature behaviour.</para>
///
/// <para><see cref="RunId"/> is the <c>core.LoaderRun</c> GUID — the identity of the
/// persisted <c>FAILED|&lt;RunId&gt;</c> finding (see <see cref="WatchdogFindingMapper"/>).
/// Not shown in the email.</para>
/// </summary>
public sealed record RecentFailure(
    Guid RunId, string LoaderName, DateTime StartTimeCentral, int UnitsFailed, int UnitsTotal, int UnresolvedUnits);

/// <summary>A loader that has not run recently enough.</summary>
public sealed record OverdueLoader(
    string LoaderName,
    DateTime? LastStartCentral,
    double HoursSinceLastRun,
    int MaxGapHours);

// ------------------------------------------------------------------------------------
// Resolution inputs/outputs for WatchdogFailureResolver. Deliberately separate from the
// models above: those describe what the WATCHDOG REPORTS, these describe the raw
// core.LoadLog rows the RESOLUTION ALGORITHM reasons over, before it has decided what
// (if anything) gets reported.
// ------------------------------------------------------------------------------------

/// <summary>
/// One <c>core.LoaderRun_Summary</c> row with <c>Success = 0</c> — a candidate for the
/// RECENT FAILURES signal, before resolution decides whether it still needs reporting.
/// <see cref="RunId"/> is the join key back to its <c>core.LoadLog</c> rows (see
/// <see cref="FailedWorkUnit"/>) — <c>core.LoaderRun_Summary</c> itself has no
/// work-unit-level detail, only these run-level totals.
/// </summary>
public sealed record FailedRunCandidate(
    Guid RunId, string LoaderName, DateTime StartTimeCentral, int UnitsFailed, int UnitsTotal);

/// <summary>
/// One <c>core.LoadLog</c> row with <c>IsComplete = 0</c> belonging to a
/// <see cref="FailedRunCandidate"/> — the "this unit failed" half of resolution.
///
/// <para><see cref="WorkUnitKey"/> is carried only for completeness/debugging. Resolution
/// itself must never compare it — see <see cref="Watchdog.WatchdogFailureResolver"/>.</para>
/// </summary>
public sealed record FailedWorkUnit(
    Guid RunId, string LoaderId, string WorkUnitKey, string WorkUnitDisplay, DateTime StartedAtUtc);

/// <summary>
/// One <c>core.LoadLog</c> row with <c>IsComplete = 1</c> for a loader that also has a
/// failure in the window — the "did it later succeed" half of resolution. Not tied to any
/// particular run: a hot-keyed failure is resolved by a DIFFERENT run's success row, so
/// this type carries no <see cref="FailedRunCandidate.RunId"/>.
/// </summary>
public sealed record SuccessfulWorkUnit(
    string LoaderId, string WorkUnitKey, string WorkUnitDisplay, DateTime StartedAtUtc);

/// <summary>
/// <see cref="Watchdog.WatchdogFailureResolver"/>'s output: which candidates still need
/// reporting, and which were suppressed because every one of their failed work units later
/// succeeded. Kept as two lists rather than filtering silently down to one, so
/// <c>WatchdogRunner</c> can log what it suppressed instead of the signal just going quiet.
/// </summary>
public sealed record RecentFailuresResult(
    IReadOnlyList<RecentFailure> Failures, IReadOnlyList<RecentFailure> Suppressed);

// ------------------------------------------------------------------------------------
// Persistence (core.WatchdogPass / core.WatchdogFinding — sql/Core/007). These describe
// what a pass RECORDS, which is detection only: acknowledgement and notification state
// belong to the DBA Agent, never here.
// ------------------------------------------------------------------------------------

/// <summary>
/// Signal codes, exactly as the <c>CK_WatchdogFinding_Signal</c> CHECK constraint and
/// <c>core.WatchdogPass.SignalsChecked</c> spell them. Part of the persisted contract —
/// the DBA Agent keys on these, so never rename one.
/// </summary>
public static class WatchdogSignals
{
    public const string Overdue = "OVERDUE";
    public const string Stuck = "STUCK";
    public const string Failed = "FAILED";
    public const string Misconfigured = "MISCONFIGURED";
    public const string Degraded = "DEGRADED";

    /// <summary>
    /// "Ran successfully but wrote an unusual number of rows" — see
    /// <see cref="WatchdogVolumeEvaluator"/>.
    ///
    /// <para>⚠ Deliberately NOT in <see cref="All"/>. It is opt-in
    /// (<see cref="WatchdogDataVolumeSettings.Enabled"/>), and with it off a pass must be
    /// byte-for-byte what it was before it existed — including <c>SignalsChecked</c>, which
    /// is persisted. Callers opt in through
    /// <see cref="WatchdogFindingMapper.ComputeSignals(IEnumerable{string}, bool)"/>.</para>
    /// </summary>
    public const string DataVolume = "DATA_VOLUME";

    /// <summary>Every signal a normal pass evaluates, in the order they are listed.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Overdue, Misconfigured, Stuck, Failed, Degraded };
}

/// <summary>
/// Hint values the sink sends that are NOT resolution reasons — the
/// <c>CK_WatchdogFinding_ResolutionReason</c> constraint cannot store them and the proc
/// never writes them to the table. Kept apart from <see cref="WatchdogResolutionReasons"/>
/// so nobody mistakes one for the other.
/// </summary>
public static class WatchdogResolutionHints
{
    /// <summary>
    /// "Not re-detected, but NOT fixed either — leave it open." Sent for an open
    /// <c>DATA_VOLUME</c> finding whose day is still in the window but could not be judged
    /// this pass (too few samples, median too small, loader config invalid, no successful
    /// run that day). Without it the proc would close it <c>NotDetected</c> — silence
    /// mistaken for health. It ages out later as <c>AgedOutUnresolved</c>.
    /// <c>sql/Core/008</c> honours it for <c>DATA_VOLUME</c> only.
    /// </summary>
    public const string KeepOpen = "KeepOpen";
}

/// <summary>
/// Stable codes for a check that could not run. The human reason changes with every
/// exception; the code does not, so it is what <c>DEGRADED|&lt;code&gt;</c> keys on.
/// </summary>
public static class WatchdogDegradedCodes
{
    /// <summary>The primary history query failed — the pass knows nothing.</summary>
    public const string HistoryQuery = "HISTORY_QUERY";
    public const string StuckQuery = "STUCK_QUERY";
    public const string FailuresQuery = "FAILURES_QUERY";
    public const string NoLoadersConfigured = "NO_LOADERS_CONFIGURED";

    /// <summary>
    /// The <c>DATA_VOLUME</c> reads (<see cref="IWatchdogRepository.GetDailyVolumesAsync"/>,
    /// <see cref="IWatchdogRepository.GetVolumeExcludedDaysAsync"/>) or the evaluation over
    /// them failed. Blinds <c>DATA_VOLUME</c> only, so no open volume finding closes.
    /// </summary>
    public const string VolumeQuery = "VOLUME_QUERY";

    /// <summary>
    /// Persistence-only: the sink could not work out whether an open FAILED finding that
    /// left the look-back window had self-healed. FAILED is then treated as degraded for
    /// this pass, so nothing closes on a guess. Never appears in the email.
    /// </summary>
    public const string FailureResolution = "FAILURE_RESOLUTION";

    /// <summary>
    /// Which signals each code blinds. A code not listed here blinds nothing extra.
    ///
    /// <para>⚠ NO_LOADERS_CONFIGURED must blind OVERDUE and MISCONFIGURED: with an empty
    /// <c>Loaders</c> section the evaluator reports nothing, and treating that as "checked"
    /// would close every open OVERDUE finding — silence mistaken for health.</para>
    ///
    /// <para>⚠ It must blind DATA_VOLUME too: with no loaders every open volume finding
    /// would otherwise look "gone from config" and close as <c>ConfigRemoved</c>.
    /// HISTORY_QUERY blinds everything including DATA_VOLUME. Listing DATA_VOLUME here
    /// changes nothing for <see cref="WatchdogFindingMapper.ComputeSignals(IEnumerable{string})"/>,
    /// which only ever considers <see cref="WatchdogSignals.All"/>.</para>
    /// </summary>
    public static IReadOnlyList<string> SignalsBlindedBy(string code) => code switch
    {
        HistoryQuery => WatchdogSignals.All.Append(WatchdogSignals.DataVolume).ToArray(),
        NoLoadersConfigured => new[] { WatchdogSignals.Overdue, WatchdogSignals.Misconfigured, WatchdogSignals.DataVolume },
        StuckQuery => new[] { WatchdogSignals.Stuck },
        FailuresQuery or FailureResolution => new[] { WatchdogSignals.Failed },
        VolumeQuery => new[] { WatchdogSignals.DataVolume },
        _ => Array.Empty<string>()
    };
}

/// <summary>
/// One check that could not run. <see cref="EmailLine"/> is exactly the string the email
/// has always shown under COULD NOT CHECK; <see cref="Reason"/> is the short redacted
/// exception text persisted in <c>DetailJson.reason</c>.
/// </summary>
public sealed record DegradedCheck(string Code, string EmailLine, string Reason);

/// <summary>Pass-level status, as the <c>CK_WatchdogPass_Status</c> constraint spells it.</summary>
public enum WatchdogPassStatus
{
    Complete,
    Degraded,

    /// <summary>The primary history query failed.</summary>
    Failed
}

/// <summary>
/// One row of <c>core.WatchdogFindingType</c>. Property ORDER mirrors the TVP's column
/// order, which <c>SqlWatchdogFindingSink.BuildFindingsTable</c> must match by name,
/// order and type.
/// </summary>
public sealed record WatchdogFindingRow(
    string FindingKey,
    string Signal,
    string? LoaderName,
    Guid? RunId,
    string Severity,
    string Summary,
    string DetailJson,
    bool IsSuppressed);

/// <summary>What one pass hands the sink.</summary>
public sealed record WatchdogPassRecord(
    DateTime StartedAtUtc,
    string HostName,
    WatchdogPassStatus Status,
    IReadOnlyList<string> SignalsChecked,
    IReadOnlyList<string> SignalsDegraded,
    IReadOnlyList<WatchdogFindingRow> Findings,
    string? AppVersion)
{
    /// <summary>
    /// This pass's <c>DATA_VOLUME</c> outcome — window, judged-in-range days, per-loader
    /// baselines — which the sink needs to choose a closing hint for every open volume
    /// finding NOT re-detected (design §A3). Null when the volume check did not run
    /// (disabled, invalid section, degraded); never persisted itself.
    ///
    /// <para>An init-only member rather than a constructor parameter, so every existing
    /// construction of this record compiles and behaves unchanged.</para>
    /// </summary>
    public WatchdogVolumeResult? Volume { get; init; }
}

// ------------------------------------------------------------------------------------
// DATA_VOLUME (design 2026-11-20). Read-side rows, then the evaluator's output.
// ------------------------------------------------------------------------------------

/// <summary>
/// One loader-day of <c>core.LoaderRun_Summary</c>, summed over EVERY run that started
/// that Central day — failed ones included (design §A1).
///
/// <para>⚠ Why all runs, not just successful ones: on a retry day the rows are split
/// between a failed run and its successful retry (AGSI: ~480 on the failed run, 1–8 on the
/// retry), so a success-only sum reads as a 98% drop EVERY day. A loader-day only EXISTS
/// — can be judged, can be a sample — when <see cref="SuccessfulRuns"/> ≥ 1; a day of
/// nothing but failures is FAILED's business, and the repository never returns it.</para>
///
/// <para><see cref="DayCentral"/> is <c>CAST(StartTime AS date)</c>; <c>StartTime</c> is
/// already Central. <see cref="LoaderRunIds"/> lists every run of the day (all of them,
/// ascending — the mapper caps what it persists).</para>
/// </summary>
public sealed record DailyVolume(
    string LoaderName,
    DateOnly DayCentral,
    long Rows,
    int SuccessfulRuns,
    int TotalRuns,
    IReadOnlyList<int> LoaderRunIds);

/// <summary>A (loader, Central day) pair — the identity of a <c>DATA_VOLUME</c> finding.</summary>
public readonly record struct VolumeLoaderDay(string LoaderName, DateOnly DayCentral);

/// <summary>Which way a day was abnormal. The persisted <c>direction</c> is the enum name.</summary>
public enum VolumeDirection
{
    /// <summary>Wrote 0 rows against a non-zero median. Warning.</summary>
    Zero,

    /// <summary>Below <c>median × LowPercent / 100</c>. Warning.</summary>
    Low,

    /// <summary>Above <c>median × HighMultiple</c>. Info — usually a rerun or a backfill.</summary>
    High
}

/// <summary>One baseline sample: a same-weekday day and the rows written on it.</summary>
public readonly record struct VolumeSample(DateOnly DayCentral, long Rows);

/// <summary>
/// One abnormal loader-day, with everything the finding's Summary and DetailJson need.
/// <see cref="LoaderName"/> is the config's canonical spelling. <see cref="Ratio"/> is
/// <c>Rows / MedianRows</c>, unrounded (the mapper rounds to 2 dp). <see cref="LowPercent"/>
/// and <see cref="HighMultiple"/> are the EFFECTIVE values (per-loader override applied).
/// </summary>
public sealed record VolumeAnomaly(
    string LoaderName,
    DateOnly DayCentral,
    VolumeDirection Direction,
    long Rows,
    int SuccessfulRuns,
    int TotalRuns,
    double MedianRows,
    double Ratio,
    int LowPercent,
    double HighMultiple,
    IReadOnlyList<VolumeSample> Samples,
    IReadOnlyList<DateOnly> ExcludedSampleDays,
    DateOnly? BaselineFromCentral,
    IReadOnlyList<int> LoaderRunIds);

/// <summary>A window day that could not be judged, and why. Logged only; never persisted.</summary>
public sealed record UnjudgedVolumeDay(string LoaderName, DateOnly DayCentral, string Reason);

/// <summary>A loader whose per-loader volume config is invalid, and why. Logged at Warning.</summary>
public sealed record SkippedVolumeLoader(string LoaderName, string Reason);

/// <summary>
/// <see cref="WatchdogVolumeEvaluator"/>'s output for one pass.
///
/// <para><see cref="WindowStart"/>/<see cref="WindowEnd"/> are the oldest/newest day
/// judged (<c>today − WindowDays</c> … <c>today − 1</c>). <see cref="JudgedInRange"/> are
/// the loader-days judged and found normal — the only ones an open finding may close
/// <c>NotDetected</c> for. <see cref="BaselineFromByLoader"/> holds the parsed
/// <c>BaselineFromCentral</c> of every EVALUATED loader that has one (canonical name,
/// case-insensitive).</para>
/// </summary>
public sealed record WatchdogVolumeResult(
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<VolumeAnomaly> Anomalies,
    IReadOnlyCollection<VolumeLoaderDay> JudgedInRange,
    IReadOnlyList<UnjudgedVolumeDay> Unjudged,
    IReadOnlyList<SkippedVolumeLoader> SkippedLoaders,
    IReadOnlyDictionary<string, DateOnly> BaselineFromByLoader);

/// <summary>One currently-open finding, as read back from <c>core.vw_WatchdogFinding_Open</c>.</summary>
public sealed record OpenWatchdogFinding(string FindingKey, string Signal, string? LoaderName, Guid? RunId);

/// <summary>One row of <c>core.WatchdogResolutionHintType</c>.</summary>
public sealed record WatchdogResolutionHint(string FindingKey, string ResolutionReason);

/// <summary>Resolution reasons, as the <c>CK_WatchdogFinding_ResolutionReason</c> constraint spells them.</summary>
public static class WatchdogResolutionReasons
{
    public const string NotDetected = "NotDetected";
    public const string SelfHealed = "SelfHealed";
    public const string AgedOutUnresolved = "AgedOutUnresolved";
    public const string LoaderDisabled = "LoaderDisabled";
    public const string ConfigRemoved = "ConfigRemoved";
}
