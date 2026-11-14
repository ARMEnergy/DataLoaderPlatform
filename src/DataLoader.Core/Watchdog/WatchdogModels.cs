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

    /// <summary>Every signal a normal pass evaluates, in the order they are listed.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Overdue, Misconfigured, Stuck, Failed, Degraded };
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
    /// </summary>
    public static IReadOnlyList<string> SignalsBlindedBy(string code) => code switch
    {
        HistoryQuery => WatchdogSignals.All,
        NoLoadersConfigured => new[] { WatchdogSignals.Overdue, WatchdogSignals.Misconfigured },
        StuckQuery => new[] { WatchdogSignals.Stuck },
        FailuresQuery or FailureResolution => new[] { WatchdogSignals.Failed },
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
    string? AppVersion);

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
