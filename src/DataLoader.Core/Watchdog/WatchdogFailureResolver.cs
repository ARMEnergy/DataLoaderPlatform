namespace DataLoader.Core.Watchdog;

/// <summary>
/// Decides which RECENT FAILURES candidates are still worth reporting once
/// <see cref="WatchdogSettings.SkipAlertIfResolved"/> is on. A pure function with no
/// database and no clock, like <see cref="WatchdogEvaluator"/>, so the rule below — the one
/// most likely to be "simplified" into a bug — is unit-testable without a live
/// <c>core.LoadLog</c>.
///
/// <para>⚠ THE DESIGN DECISION THAT MATTERS MOST: resolution matches on
/// <c>(LoaderId, WorkUnitDisplay)</c>, NEVER <c>WorkUnitKey</c>. This is counterintuitive
/// and was measured, not assumed.</para>
///
/// <para>Several loaders stamp the run into the work-unit key
/// (<c>HotKeyStrategy: RunHour</c> / <c>RunDate</c>), so a failed key can NEVER recur —
/// matching on it would report a hot-keyed failure forever, which is worse than not
/// suppressing at all. Measured on production 2026-11-02:</para>
///
/// <list type="table">
/// <item><description>IHSPointLogic <c>pl:PointMetadata:run=2026110219</c> — later success
/// BY KEY: 0. BY DISPLAY (<c>PointMetadata latest</c>): 3.</description></item>
/// <item><description>IHSPointLogic <c>pl:PointStatus:run=2026110205</c> — BY KEY: 0.
/// BY DISPLAY (<c>PointStatus latest</c>): 10.</description></item>
/// <item><description>AGSI <c>agsi:storage:rs:20261016:run=20261102</c> — BY KEY: 1.
/// BY DISPLAY (<c>AGSI storage rs 2026-10-16</c>): 1.</description></item>
/// </list>
///
/// <para><c>WorkUnitDisplay</c> is the stable, run-independent identity that resolves both
/// shapes correctly. Verified across the last 200,000 <c>core.LoadLog</c> rows that no
/// loader leaves <c>WorkUnitDisplay</c> null or equal to <c>WorkUnitKey</c>, so matching on
/// it is safe platform-wide. Someone will be tempted to "simplify" this to
/// <c>WorkUnitKey</c> — don't: it will silently start reporting every hot-keyed failure
/// forever, and nothing will fail loudly to say so.</para>
/// </summary>
public static class WatchdogFailureResolver
{
    /// <summary>
    /// Splits <paramref name="candidates"/> into what still needs reporting and what was
    /// suppressed because every failed unit it contains later succeeded.
    ///
    /// <para>A candidate with no matching row in <paramref name="failedUnits"/> at all
    /// (e.g. the run failed for a reason that never marked any individual work unit, such
    /// as an AddProc-chain failure with <c>UnitsFail = 0</c>) cannot be assessed — absence
    /// of evidence is not evidence of resolution, so it is NEVER suppressed; it falls back
    /// to the run's own reported failure count.</para>
    /// </summary>
    public static RecentFailuresResult Resolve(
        IReadOnlyList<FailedRunCandidate> candidates,
        IReadOnlyList<FailedWorkUnit> failedUnits,
        IReadOnlyList<SuccessfulWorkUnit> laterSuccesses)
    {
        // Only the LATEST success per (LoaderId, WorkUnitDisplay) can matter for "is there
        // a success after this failure": if the latest one is after, at least one success
        // is; if the latest one is not, none can be either.
        //
        // OrdinalIgnoreCase on both parts of the key, matching WatchdogEvaluator's own
        // loader-name matching — these identifiers are programmatically generated, not
        // user input, so a case mismatch would be an upstream formatting quirk rather
        // than a real identity difference, and the default tuple comparer is
        // case-sensitive.
        var latestSuccess = new Dictionary<(string LoaderId, string WorkUnitDisplay), DateTime>(
            LoaderWorkUnitComparer.Instance);
        foreach (var s in laterSuccesses)
        {
            var key = (s.LoaderId, s.WorkUnitDisplay);
            if (!latestSuccess.TryGetValue(key, out var existing) || s.StartedAtUtc > existing)
                latestSuccess[key] = s.StartedAtUtc;
        }

        // ⚠ (LoaderId, WorkUnitDisplay) ONLY — see the class doc comment. WorkUnitKey is
        // available on FailedWorkUnit but must never enter this comparison.
        bool IsResolved(FailedWorkUnit f) =>
            latestSuccess.TryGetValue((f.LoaderId, f.WorkUnitDisplay), out var latest) &&
            latest > f.StartedAtUtc;

        var unresolvedByRun = failedUnits
            .GroupBy(f => f.RunId)
            .ToDictionary(
                g => g.Key,
                g => g.Where(f => !IsResolved(f))
                      .Select(f => f.WorkUnitDisplay)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .Count());

        var reported = new List<RecentFailure>();
        var suppressed = new List<RecentFailure>();

        foreach (var c in candidates)
        {
            var unresolvedUnits = unresolvedByRun.TryGetValue(c.RunId, out var count)
                ? count
                // No failed-unit rows found for this run at all — can't assess it, so
                // never suppress it. Fall back to the run's own reported failure count
                // (or 1, if even that is 0, so the run still surfaces as unresolved).
                : Math.Max(c.UnitsFailed, 1);

            var failure = new RecentFailure(c.LoaderName, c.StartTimeCentral, c.UnitsFailed, c.UnitsTotal, unresolvedUnits);
            (unresolvedUnits > 0 ? reported : suppressed).Add(failure);
        }

        return new RecentFailuresResult(reported, suppressed);
    }

    /// <summary>
    /// Case-insensitive equality over the (LoaderId, WorkUnitDisplay) resolution key. The
    /// built-in <see cref="ValueTuple{T1,T2}"/> comparer is case-SENSITIVE, which would
    /// silently fail to match a success against its failure over a harmless casing quirk —
    /// the fail-open direction here is "stays reported", not "wrongly suppressed", but
    /// there is no reason to accept even that for free.
    /// </summary>
    private sealed class LoaderWorkUnitComparer : IEqualityComparer<(string LoaderId, string WorkUnitDisplay)>
    {
        public static readonly LoaderWorkUnitComparer Instance = new();

        public bool Equals((string LoaderId, string WorkUnitDisplay) x, (string LoaderId, string WorkUnitDisplay) y) =>
            string.Equals(x.LoaderId, y.LoaderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.WorkUnitDisplay, y.WorkUnitDisplay, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string LoaderId, string WorkUnitDisplay) obj) =>
            HashCode.Combine(
                obj.LoaderId.ToUpperInvariant(),
                obj.WorkUnitDisplay.ToUpperInvariant());
    }
}
