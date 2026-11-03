using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Exercises the pure matching algorithm behind <see cref="WatchdogSettings.SkipAlertIfResolved"/>
/// directly — no database, no fake repository. See <see cref="WatchdogFailureResolver"/> for
/// the rule these tests pin: resolution matches on (LoaderId, WorkUnitDisplay), never
/// WorkUnitKey.
/// </summary>
public class WatchdogFailureResolverTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc);

    private static FailedRunCandidate Candidate(
        Guid runId, string loader = "Platts", int unitsFailed = 1, int unitsTotal = 10) =>
        new(runId, loader, new DateTime(2026, 11, 2, 13, 0, 0), unitsFailed, unitsTotal);

    [Fact]
    public void FailedUnit_WithLaterSuccess_ByDisplay_IsSuppressed()
    {
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId) };
        var failedUnits = new[] { new FailedWorkUnit(runId, "Platts", "key-1", "Platts daily", T0) };
        var laterSuccesses = new[] { new SuccessfulWorkUnit("Platts", "key-2", "Platts daily", T0.AddHours(1)) };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);

        Assert.Empty(result.Failures);
        var suppressed = Assert.Single(result.Suppressed);
        Assert.Equal(0, suppressed.UnresolvedUnits);
    }

    [Fact]
    public void FailedUnit_WithNoLaterSuccess_IsReported()
    {
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId) };
        var failedUnits = new[] { new FailedWorkUnit(runId, "Platts", "key-1", "Platts daily", T0) };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, Array.Empty<SuccessfulWorkUnit>());

        Assert.Empty(result.Suppressed);
        var reported = Assert.Single(result.Failures);
        Assert.Equal(1, reported.UnresolvedUnits);
    }

    /// <summary>
    /// ⚠ THE CORE INSIGHT: a hot-key failure's key can never recur, but its display does,
    /// and resolution must follow the display.
    ///
    /// Mirrors production 2026-11-02: IHSPointLogic stamps the run into the work-unit key
    /// (HotKeyStrategy: RunHour), so a failed key like <c>pl:PointMetadata:run=2026110219</c>
    /// is never seen again — by key alone, zero later runs ever match it (measured: 0 of 3
    /// actual later successes matched by key, all 3 matched by display). If resolution
    /// matched on WorkUnitKey instead of WorkUnitDisplay, this failure would be reported on
    /// every watchdog pass forever, which is exactly the regression this test exists to
    /// catch.
    /// </summary>
    [Fact]
    public void HotKeyFailure_KeyNeverRecurs_ButDisplayDoes_IsCorrectlySuppressed()
    {
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId, "IHSPointLogic") };
        var failedUnits = new[]
        {
            new FailedWorkUnit(runId, "IHSPointLogic", "pl:PointMetadata:run=2026110219", "PointMetadata latest", T0)
        };
        var laterSuccesses = new[]
        {
            // Three different runs, three different (also hot) keys, the SAME display —
            // exactly how this resolves for real on production.
            new SuccessfulWorkUnit("IHSPointLogic", "pl:PointMetadata:run=2026110220", "PointMetadata latest", T0.AddHours(2)),
            new SuccessfulWorkUnit("IHSPointLogic", "pl:PointMetadata:run=2026110221", "PointMetadata latest", T0.AddHours(4)),
            new SuccessfulWorkUnit("IHSPointLogic", "pl:PointMetadata:run=2026110222", "PointMetadata latest", T0.AddHours(6)),
        };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);

        Assert.Empty(result.Failures);
        var suppressed = Assert.Single(result.Suppressed);
        Assert.Equal("IHSPointLogic", suppressed.LoaderName);
        Assert.Equal(0, suppressed.UnresolvedUnits);
    }

    [Fact]
    public void PartiallyResolvedRun_ReportsWithUnresolvedUnitsCount()
    {
        // 3 failed, 2 resolved -> UnresolvedUnits = 1, and the run is still reported (not
        // suppressed) because one unit remains genuinely broken.
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId, "AGSI", unitsFailed: 3, unitsTotal: 2630) };
        var failedUnits = new[]
        {
            new FailedWorkUnit(runId, "AGSI", "agsi:storage:nl:run=1", "AGSI storage nl", T0),
            new FailedWorkUnit(runId, "AGSI", "agsi:storage:pl:run=1", "AGSI storage pl", T0),
            new FailedWorkUnit(runId, "AGSI", "agsi:storage:cz:run=1", "AGSI storage cz", T0),
        };
        var laterSuccesses = new[]
        {
            new SuccessfulWorkUnit("AGSI", "agsi:storage:nl:run=2", "AGSI storage nl", T0.AddHours(1)),
            new SuccessfulWorkUnit("AGSI", "agsi:storage:pl:run=2", "AGSI storage pl", T0.AddHours(1)),
            // "AGSI storage cz" never recurs — stays unresolved.
        };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);

        Assert.Empty(result.Suppressed);
        var reported = Assert.Single(result.Failures);
        Assert.Equal(3, reported.UnitsFailed);
        Assert.Equal(1, reported.UnresolvedUnits);
    }

    [Fact]
    public void RunWhereAllUnitsResolved_IsSuppressedEntirely()
    {
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId, "AGSI", unitsFailed: 2, unitsTotal: 2630) };
        var failedUnits = new[]
        {
            new FailedWorkUnit(runId, "AGSI", "agsi:storage:nl:run=1", "AGSI storage nl", T0),
            new FailedWorkUnit(runId, "AGSI", "agsi:storage:pl:run=1", "AGSI storage pl", T0),
        };
        var laterSuccesses = new[]
        {
            new SuccessfulWorkUnit("AGSI", "agsi:storage:nl:run=2", "AGSI storage nl", T0.AddHours(1)),
            new SuccessfulWorkUnit("AGSI", "agsi:storage:pl:run=2", "AGSI storage pl", T0.AddHours(1)),
        };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);

        Assert.Empty(result.Failures);
        var suppressed = Assert.Single(result.Suppressed);
        Assert.Equal(0, suppressed.UnresolvedUnits);
    }

    [Fact]
    public void CandidateWithNoMatchingFailedUnitRows_IsNeverSuppressed()
    {
        // E.g. the run failed for a reason that never marked any individual work unit (an
        // AddProc-chain failure, UnitsFail = 0). Absence of evidence is not evidence of
        // resolution, so this must never be silently dropped.
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId, "Platts", unitsFailed: 0, unitsTotal: 10) };

        var result = WatchdogFailureResolver.Resolve(
            candidates, Array.Empty<FailedWorkUnit>(), Array.Empty<SuccessfulWorkUnit>());

        var reported = Assert.Single(result.Failures);
        Assert.Empty(result.Suppressed);
        Assert.True(reported.UnresolvedUnits > 0);
    }

    [Fact]
    public void MatchingIsCaseInsensitive_OnBothLoaderIdAndWorkUnitDisplay()
    {
        // These identifiers are programmatically generated, not user input, so a harmless
        // casing difference must not be mistaken for a different unit — matching
        // WatchdogEvaluator's own case-insensitive loader-name comparison.
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId, "Platts") };
        var failedUnits = new[] { new FailedWorkUnit(runId, "Platts", "key-1", "Platts Daily", T0) };
        var laterSuccesses = new[] { new SuccessfulWorkUnit("PLATTS", "key-2", "platts daily", T0.AddHours(1)) };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);

        Assert.Empty(result.Failures);
        Assert.Single(result.Suppressed);
    }

    [Fact]
    public void SuccessBeforeTheFailure_DoesNotResolveIt()
    {
        // A success that happened BEFORE the failure (e.g. the run before the one that
        // broke) must not count — only a success strictly AFTER the failure resolves it.
        var runId = Guid.NewGuid();
        var candidates = new[] { Candidate(runId) };
        var failedUnits = new[] { new FailedWorkUnit(runId, "Platts", "key-2", "Platts daily", T0) };
        var laterSuccesses = new[] { new SuccessfulWorkUnit("Platts", "key-1", "Platts daily", T0.AddHours(-1)) };

        var result = WatchdogFailureResolver.Resolve(candidates, failedUnits, laterSuccesses);

        Assert.Empty(result.Suppressed);
        Assert.Single(result.Failures);
    }
}
