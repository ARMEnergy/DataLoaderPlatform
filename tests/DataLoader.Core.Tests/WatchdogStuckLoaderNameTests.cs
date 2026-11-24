using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// STUCK findings used to carry no loader name, on the belief that a killed run has no
/// <c>core.LoaderRun_Summary</c> row. Production's <c>usp_BeginLoadLog</c> inserts that row
/// (with <c>LoaderName</c>) when the run's FIRST unit begins, so the two IHSPointLogic runs
/// killed on 2026-11-21 had one — yet the watchdog email and the DBAdmin alert showed only
/// a GUID.
/// </summary>
public class WatchdogStuckLoaderNameTests
{
    private static readonly DateTime Started = new(2026, 11, 21, 15, 0, 3);
    private static readonly Guid RunA = Guid.Parse("40b8adbd-5e2a-4580-948e-4094e7857dc1");
    private static readonly Guid RunB = Guid.Parse("e06b978f-cfd0-41b9-b520-a914d635c0ca");

    private static SqlWatchdogRepository.StuckRunRow Row(Guid id, string? name, string? host = "ARMH-OPSDB01") =>
        new(id, Started, host, name);

    private static WatchdogReport Report(params StuckRun[] stuck) => new()
    {
        HostName = "ARMH-OPSDB01",
        GeneratedAtCentral = new DateTime(2026, 11, 22, 9, 0, 0),
        Stuck = stuck
    };

    private static WatchdogSettings Settings()
    {
        var s = new WatchdogSettings();
        s.Loaders["IHSPointLogic"] = new WatchdogLoaderSettings { MaxGapHours = 18 };
        return s;
    }

    // ------------------------------------------------------------ grouping the query rows

    [Fact]
    public void OneNamePerRun_IsCarriedThrough()
    {
        var runs = SqlWatchdogRepository.GroupStuckRuns(new[] { Row(RunA, "IHSPointLogic"), Row(RunB, "IHSPointLogic") });

        Assert.Equal(new[] { RunA, RunB }, runs.Select(r => r.RunId));
        Assert.All(runs, r => Assert.Equal("IHSPointLogic", r.LoaderName));
    }

    [Fact]
    public void TwoRowsForOneRun_FoldIntoOne_CaseInsensitively()
    {
        // Production returns one row per run; a schema that does not must still not
        // report one run twice, nor turn one loader into two over a case difference.
        var run = Assert.Single(SqlWatchdogRepository.GroupStuckRuns(new[] { Row(RunA, "IHSPointLogic"), Row(RunA, "ihspointlogic") }));
        Assert.Equal("IHSPointLogic", run.LoaderName);
    }

    [Fact]
    public void MultiLoaderRun_JoinsItsNamesSorted()
    {
        var run = Assert.Single(SqlWatchdogRepository.GroupStuckRuns(new[] { Row(RunA, "Vulcan"), Row(RunA, "EnergyAspects") }));
        Assert.Equal("EnergyAspects, Vulcan", run.LoaderName);
    }

    [Fact]
    public void RunKilledBeforeItsFirstUnit_HasNoName_AndIsStillReported()
    {
        // Acceptance 6: no summary row and no load-log row → the OUTER APPLY yields NULL.
        var run = Assert.Single(SqlWatchdogRepository.GroupStuckRuns(new[] { Row(RunA, null, host: null) }));
        Assert.Null(run.LoaderName);
        Assert.Null(run.HostName);
        Assert.Equal(Started, run.StartedAtUtc);
    }

    // ------------------------------------------------------------ the finding, summary and email

    [Fact]
    public void Finding_CarriesTheLoaderName_InTheRowAndTheSummary()
    {
        // Acceptance 5.
        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(
            Report(new StuckRun(RunA, Started, "ARMH-OPSDB01", "IHSPointLogic")), [], [], Settings()));

        Assert.Equal(WatchdogSignals.Stuck, row.Signal);
        Assert.Equal("IHSPointLogic", row.LoaderName);
        Assert.Equal($"STUCK|{RunA}", row.FindingKey);       // identity unchanged
        Assert.StartsWith("IHSPointLogic run 40b8adbd", row.Summary);
    }

    [Fact]
    public void Finding_TakesTheConfigSpellingOfTheName()
    {
        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(
            Report(new StuckRun(RunA, Started, null, "ihspointlogic")), [], [], Settings()));
        Assert.Equal("IHSPointLogic", row.LoaderName);
    }

    [Fact]
    public void Finding_WithoutAName_StillWorks()
    {
        // Acceptance 6.
        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(
            Report(new StuckRun(RunA, Started, null)), [], [], Settings()));

        Assert.Null(row.LoaderName);
        Assert.StartsWith("Unknown loader run 40b8adbd", row.Summary);
    }

    [Fact]
    public void Finding_LoaderName_NeverExceedsTheColumn()
    {
        var many = string.Join(", ", Enumerable.Range(0, 40).Select(i => $"Loader{i:00}"));
        var row = Assert.Single(WatchdogFindingMapper.BuildFindings(
            Report(new StuckRun(RunA, Started, null, many)), [], [], Settings()));

        Assert.Equal(WatchdogFindingMapper.MaxLoaderNameChars, row.LoaderName!.Length);
    }

    [Fact]
    public void Email_NamesTheLoader()
    {
        var body = Report(new StuckRun(RunA, Started, "ARMH-OPSDB01", "IHSPointLogic")).BuildMessage().Body;
        Assert.Contains($"  IHSPointLogic run {RunA} started 2026-11-21 15:00 UTC on ARMH-OPSDB01", body);
    }
}
