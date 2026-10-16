using DataLoader.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Vulcan.Tests;

/// <summary>
/// Which Vulcan source tables load comes from <c>dbo.LoaderTable</c> (Enabled / LastRun), not from
/// <c>Loaders:Vulcan:EnabledTables</c> — that list was removed on 2026-10-13 so the set is operable
/// without a redeploy.
///
/// <para>Two rules carry real operational weight and are pinned here in both directions:
/// <c>Enabled = 0</c> skips the table, and an unreadable or empty registry FAILS the run rather
/// than quietly loading nothing (which would hide an undeployed <c>sql/Vulcan/001</c>).</para>
/// </summary>
public class TableRegistryTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeRegistry : IVulcanTableRegistry
    {
        private readonly IReadOnlyList<VulcanTableRegistration> _rows;
        private readonly Exception? _throwOnGet;

        public readonly List<string> LastRunStamped = new();
        public Exception? ThrowOnStamp;

        public FakeRegistry(IReadOnlyList<VulcanTableRegistration> rows, Exception? throwOnGet = null)
        {
            _rows = rows;
            _throwOnGet = throwOnGet;
        }

        public Task<IReadOnlyList<VulcanTableRegistration>> GetAllAsync(CancellationToken ct) =>
            _throwOnGet is not null
                ? Task.FromException<IReadOnlyList<VulcanTableRegistration>>(_throwOnGet)
                : Task.FromResult(_rows);

        public Task SetLastRunAsync(string tableName, CancellationToken ct)
        {
            if (ThrowOnStamp is not null) return Task.FromException(ThrowOnStamp);
            LastRunStamped.Add(tableName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePipeline : IVulcanTablePipeline
    {
        private readonly bool _succeeds;
        public int Executions;

        public FakePipeline(string tableId, bool succeeds = true)
        {
            TableId = tableId;
            _succeeds = succeeds;
        }

        public string TableId { get; }

        public Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context)
        {
            Executions++;
            return Task.FromResult(_succeeds
                ? new LoaderRunResult { LoaderId = VulcanModule.Id, Success = true, WorkUnitsSucceeded = 1 }
                : LoaderRunResult.Failed(VulcanModule.Id, "boom", TimeSpan.Zero));
        }
    }

    private static VulcanTableRegistration Row(string name, bool enabled, DateTime? lastRun = null) =>
        new(name, enabled, lastRun);

    private static IServiceProvider Services(IVulcanTableRegistry registry, params IVulcanTablePipeline[] pipelines)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        sc.AddSingleton(registry);
        foreach (var p in pipelines) sc.AddSingleton(p);
        return sc.BuildServiceProvider();
    }

    private static LoaderRunContext Context() => new()
    {
        RunId = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        CancellationToken = CancellationToken.None
    };

    // ------------------------------------------------------------ Enabled = 0

    [Fact]
    public async Task DisabledTable_IsSkipped_AndItsPipelineNeverRuns()
    {
        var on = new FakePipeline("datacenters");
        var off = new FakePipeline("metadata_history");
        var registry = new FakeRegistry(new[] { Row("datacenters", true), Row("metadata_history", false) });

        var result = await new VulcanModule().RunAsync(Services(registry, on, off), Context());

        Assert.True(result.Success);
        Assert.Equal(1, on.Executions);
        Assert.Equal(0, off.Executions);          // Enabled = 0 -> never touched
    }

    [Fact]
    public async Task EveryTableEnabled_RunsThemAll()
    {
        var a = new FakePipeline("datacenters");
        var b = new FakePipeline("lng_projects");
        var registry = new FakeRegistry(new[] { Row("datacenters", true), Row("lng_projects", true) });

        await new VulcanModule().RunAsync(Services(registry, a, b), Context());

        Assert.Equal(1, a.Executions);
        Assert.Equal(1, b.Executions);
    }

    [Fact]
    public async Task EnabledTableWithNoPipeline_IsWarnedAbout_NotFatal()
    {
        var a = new FakePipeline("datacenters");
        var registry = new FakeRegistry(new[] { Row("datacenters", true), Row("typo_table", true) });

        var result = await new VulcanModule().RunAsync(Services(registry, a), Context());

        Assert.True(result.Success);
        Assert.Equal(1, a.Executions);
    }

    // ----------------------------------------------------- registry unusable

    [Fact]
    public async Task UnreadableRegistry_FailsTheRun_RatherThanLoadingNothing()
    {
        // A missing dbo.LoaderTable means sql/Vulcan/001 was never deployed. Reporting success
        // would hide that for as long as nobody checked the row counts.
        var registry = new FakeRegistry(Array.Empty<VulcanTableRegistration>(),
                                        throwOnGet: new InvalidOperationException("Invalid object name 'dbo.LoaderTable'."));
        var pipeline = new FakePipeline("datacenters");

        var result = await new VulcanModule().RunAsync(Services(registry, pipeline), Context());

        Assert.False(result.Success);
        Assert.Contains("dbo.LoaderTable", result.ErrorMessage);
        Assert.Equal(0, pipeline.Executions);
    }

    [Fact]
    public async Task EmptyRegistry_FailsTheRun()
    {
        var registry = new FakeRegistry(Array.Empty<VulcanTableRegistration>());
        var pipeline = new FakePipeline("datacenters");

        var result = await new VulcanModule().RunAsync(Services(registry, pipeline), Context());

        Assert.False(result.Success);
        Assert.Equal(0, pipeline.Executions);
    }

    [Fact]
    public async Task AllTablesDisabled_FailsTheRun()
    {
        // Distinct from "registry missing", but equally not a silent success: somebody turned
        // everything off and the loader should say so.
        var registry = new FakeRegistry(new[] { Row("datacenters", false), Row("lng_projects", false) });
        var pipeline = new FakePipeline("datacenters");

        var result = await new VulcanModule().RunAsync(Services(registry, pipeline), Context());

        Assert.False(result.Success);
        Assert.Equal(0, pipeline.Executions);
    }

    // -------------------------------------------------------------- LastRun

    [Fact]
    public async Task LastRun_IsStamped_OnlyForTablesThatLoadedCleanly()
    {
        var ok = new FakePipeline("datacenters", succeeds: true);
        var bad = new FakePipeline("lng_projects", succeeds: false);
        var registry = new FakeRegistry(new[] { Row("datacenters", true), Row("lng_projects", true) });

        var result = await new VulcanModule().RunAsync(Services(registry, ok, bad), Context());

        Assert.False(result.Success);                                  // one table failed
        Assert.Equal(new[] { "datacenters" }, registry.LastRunStamped); // ...and only the good one stamped
    }

    [Fact]
    public async Task LastRun_IsNotStamped_ForADisabledTable()
    {
        var registry = new FakeRegistry(new[] { Row("datacenters", true), Row("metadata_history", false) });

        await new VulcanModule().RunAsync(
            Services(registry, new FakePipeline("datacenters"), new FakePipeline("metadata_history")), Context());

        Assert.DoesNotContain("metadata_history", registry.LastRunStamped);
    }

    [Fact]
    public async Task StampFailure_DoesNotTurnAGoodLoadIntoAFailedOne()
    {
        // The data landed; only the bookkeeping write failed.
        var registry = new FakeRegistry(new[] { Row("datacenters", true) })
        {
            ThrowOnStamp = new InvalidOperationException("deadlock")
        };

        var result = await new VulcanModule().RunAsync(
            Services(registry, new FakePipeline("datacenters")), Context());

        Assert.True(result.Success);
    }

    // ------------------------------------------------- the setting is gone

    [Fact]
    public void VulcanSettings_NoLongerCarriesAnEnabledTablesList()
    {
        // Guards against reintroducing it: the enabled set living in two places that can
        // disagree is exactly what moving it to the database removed.
        Assert.Null(typeof(VulcanSettings).GetProperty("EnabledTables"));
    }
}
