namespace DataLoader.Core.Watchdog;

/// <summary>
/// Reads the run history the watchdog evaluates. Read-only by construction —
/// the watchdog adds no SQL objects and writes nothing.
/// </summary>
public interface IWatchdogRepository
{
    /// <summary>Most recent start per loader, in CENTRAL time (see <see cref="LoaderLastRun"/>).</summary>
    Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Run rows still open after <paramref name="stuckAfterHours"/>, compared in
    /// UTC, bounded to the last <paramref name="lookbackDays"/> and optionally to
    /// one host. A null <paramref name="hostName"/> means every host.
    /// </summary>
    Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(
        int stuckAfterHours, int lookbackDays, string? hostName, CancellationToken cancellationToken);

    /// <summary>
    /// Runs that recorded Success = 0 within the look-back, in CENTRAL time.
    ///
    /// <para>When <paramref name="skipAlertIfResolved"/> is true, a candidate whose failed
    /// work units have ALL since succeeded (matched on <c>(LoaderId, WorkUnitDisplay)</c> —
    /// see <see cref="WatchdogFailureResolver"/>) is returned in
    /// <see cref="RecentFailuresResult.Suppressed"/> instead of
    /// <see cref="RecentFailuresResult.Failures"/>. When false, every candidate is returned
    /// in <c>Failures</c>, matching the platform's behaviour before this setting existed.</para>
    /// </summary>
    Task<RecentFailuresResult> GetRecentFailuresAsync(
        int lookbackHours, DateTime nowCentral, bool skipAlertIfResolved, CancellationToken cancellationToken);
}
