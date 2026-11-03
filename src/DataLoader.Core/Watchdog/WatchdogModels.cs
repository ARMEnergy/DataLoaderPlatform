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
/// </summary>
public sealed record RecentFailure(
    string LoaderName, DateTime StartTimeCentral, int UnitsFailed, int UnitsTotal, int UnresolvedUnits);

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
