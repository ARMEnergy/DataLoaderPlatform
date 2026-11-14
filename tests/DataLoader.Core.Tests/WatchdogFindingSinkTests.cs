using System.Data;
using System.Text.RegularExpressions;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

public class WatchdogFindingSinkTests
{
    private sealed class FakeStore : IWatchdogFindingStore
    {
        public List<OpenWatchdogFinding> Open = new();
        public int ReadCalls;
        public WatchdogPassRecord? Recorded;
        public IReadOnlyList<WatchdogResolutionHint>? Hints;

        public Task<IReadOnlyList<OpenWatchdogFinding>> ReadOpenFindingsAsync(CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult<IReadOnlyList<OpenWatchdogFinding>>(Open);
        }

        public Task<WatchdogPersistResult> RecordPassAsync(
            WatchdogPassRecord pass, IReadOnlyList<WatchdogResolutionHint> hints, CancellationToken ct)
        {
            Recorded = pass;
            Hints = hints;
            return Task.FromResult(new WatchdogPersistResult(42, null));
        }
    }

    private sealed class FakeRepository : IWatchdogRepository
    {
        public TimeSpan ResolveDelay;
        public Exception? ThrowOnResolve;
        public RecentFailuresResult Resolution = new(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>());
        public IReadOnlyCollection<Guid>? AskedFor;

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int h, int d, string? host, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecentFailuresResult> GetRecentFailuresAsync(int h, DateTime n, bool s, CancellationToken ct) => throw new NotSupportedException();

        public async Task<RecentFailuresResult> ResolveFailuresByRunIdAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct)
        {
            AskedFor = runIds;
            if (ResolveDelay > TimeSpan.Zero) await Task.Delay(ResolveDelay, CancellationToken.None);
            if (ThrowOnResolve is not null) throw ThrowOnResolve;
            return Resolution;
        }
    }

    private static WatchdogFindingSink Sink(FakeStore store, FakeRepository repo, WatchdogSettings? settings = null) =>
        new(store, repo, Options.Create(settings ?? new WatchdogSettings()), NullLogger<WatchdogFindingSink>.Instance);

    private static WatchdogPassRecord Pass(WatchdogPassStatus status = WatchdogPassStatus.Complete,
        IReadOnlyList<WatchdogFindingRow>? findings = null)
    {
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals(
            status == WatchdogPassStatus.Failed ? new[] { WatchdogDegradedCodes.HistoryQuery } : Array.Empty<string>());
        return new WatchdogPassRecord(DateTime.UtcNow, "ARMH-OPSDB01", status, chk, deg,
            findings ?? Array.Empty<WatchdogFindingRow>(), null);
    }

    [Fact]
    public async Task OpenFailedThatLeftTheWindow_IsResolvedWithoutTheWindow_AndHinted()
    {
        var runId = Guid.NewGuid();
        var store = new FakeStore { Open = { new OpenWatchdogFinding($"FAILED|{runId}", "FAILED", "Platts", runId) } };
        var repo = new FakeRepository
        {
            Resolution = new(Array.Empty<RecentFailure>(),
                new[] { new RecentFailure(runId, "Platts", DateTime.Now, 1, 2, 0) })
        };

        await Sink(store, repo).RecordPassAsync(Pass(), default);

        Assert.Equal(new[] { runId }, repo.AskedFor);
        var hint = Assert.Single(store.Hints!);
        Assert.Equal("SelfHealed", hint.ResolutionReason);
        Assert.Equal(WatchdogPassStatus.Complete, store.Recorded!.Status);
    }

    [Fact]
    public async Task ResolutionFailure_TreatsFailedAsDegraded_SoNothingFailedCloses()
    {
        var runId = Guid.NewGuid();
        var store = new FakeStore { Open = { new OpenWatchdogFinding($"FAILED|{runId}", "FAILED", "Platts", runId) } };
        var repo = new FakeRepository { ThrowOnResolve = new TimeoutException("password=hunter2hunter2 timed out") };

        await Sink(store, repo).RecordPassAsync(Pass(), default);

        var recorded = store.Recorded!;
        Assert.DoesNotContain("FAILED", recorded.SignalsChecked);
        Assert.Contains("FAILED", recorded.SignalsDegraded);
        Assert.Equal(WatchdogPassStatus.Degraded, recorded.Status);
        var degradedRow = Assert.Single(recorded.Findings, f => f.FindingKey == "DEGRADED|FAILURE_RESOLUTION");
        Assert.DoesNotContain("hunter2hunter2", degradedRow.DetailJson);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task SlowResolution_DegradesOnlyFailed_AndThePassIsStillRecorded()
    {
        // A slow, unwindowed resolution read must not run into the whole pass budget —
        // that would lose the pass (no heartbeat row) instead of degrading FAILED.
        var runId = Guid.NewGuid();
        var store = new FakeStore { Open = { new OpenWatchdogFinding($"FAILED|{runId}", "FAILED", "Platts", runId) } };
        var repo = new FakeRepository { ResolveDelay = TimeSpan.FromSeconds(30) };   // ignores its token
        var settings = new WatchdogSettings { PersistTimeoutSeconds = 2 };          // → 1s sub-budget

        var started = DateTime.UtcNow;
        await Sink(store, repo, settings).RecordPassAsync(Pass(), default);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        var recorded = store.Recorded!;
        Assert.Contains("FAILED", recorded.SignalsDegraded);
        var row = Assert.Single(recorded.Findings, f => f.FindingKey == "DEGRADED|FAILURE_RESOLUTION");
        Assert.Contains("timed out after 1s", row.DetailJson);
    }

    [Fact]
    public async Task OpenFailureResolutionFinding_IsCarriedForward_WhileFailedCannotBeChecked()
    {
        // FAILED blinded (e.g. FAILURES_QUERY threw) → the resolution never ran, so the
        // open DEGRADED|FAILURE_RESOLUTION must not close as if it had.
        var store = new FakeStore
        {
            Open = { new OpenWatchdogFinding("DEGRADED|FAILURE_RESOLUTION", "DEGRADED", null, null) }
        };
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals(new[] { WatchdogDegradedCodes.FailuresQuery });
        var pass = new WatchdogPassRecord(DateTime.UtcNow, "h", WatchdogPassStatus.Degraded, chk, deg,
            new[] { WatchdogFindingMapper.DegradedRow(new DegradedCheck(WatchdogDegradedCodes.FailuresQuery, "x", "x")) }, null);

        await Sink(store, new FakeRepository()).RecordPassAsync(pass, default);

        Assert.Contains(store.Recorded!.Findings, f => f.FindingKey == "DEGRADED|FAILURE_RESOLUTION");
    }

    [Fact]
    public async Task OpenFailureResolutionFinding_ClosesOnceFailedIsCheckedAgain()
    {
        var store = new FakeStore
        {
            Open = { new OpenWatchdogFinding("DEGRADED|FAILURE_RESOLUTION", "DEGRADED", null, null) }
        };

        await Sink(store, new FakeRepository()).RecordPassAsync(Pass(), default);

        Assert.DoesNotContain(store.Recorded!.Findings, f => f.FindingKey == "DEGRADED|FAILURE_RESOLUTION");
    }

    [Fact]
    public async Task FailedPass_DoesNotReadOpenFindings_AndSendsNoHints()
    {
        // Nothing is checked on a Failed pass, so nothing can close; and the DB the view
        // lives in is probably the thing that is down.
        var store = new FakeStore();
        await Sink(store, new FakeRepository()).RecordPassAsync(Pass(WatchdogPassStatus.Failed), default);

        Assert.Equal(0, store.ReadCalls);
        Assert.Empty(store.Recorded!.SignalsChecked);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task OpenFailedStillDetected_IsNotSentToTheResolver()
    {
        var runId = Guid.NewGuid();
        var key = $"FAILED|{runId}";
        var store = new FakeStore { Open = { new OpenWatchdogFinding(key, "FAILED", "Platts", runId) } };
        var repo = new FakeRepository();
        var current = new[] { new WatchdogFindingRow(key, "FAILED", "Platts", runId, "Warning", "s", "{}", false) };

        await Sink(store, repo).RecordPassAsync(Pass(findings: current), default);

        Assert.Null(repo.AskedFor);
    }

    // ------------------------------------------------------------- TVP contract ------

    private static readonly string SqlPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sql", "Core", "007_CreateWatchdogFindings.sql");

    /// <summary>Column (name, sql type) pairs of one CREATE TYPE … AS TABLE in the 007 script.</summary>
    private static List<(string Name, string Type)> TvpColumns(string typeName)
    {
        var sql = File.ReadAllText(SqlPath);
        var m = Regex.Match(sql,
            @"CREATE TYPE " + Regex.Escape(typeName) + @" AS TABLE\s*\((?<body>.*?)\n\s*\);",
            RegexOptions.Singleline);
        Assert.True(m.Success, $"CREATE TYPE {typeName} not found in {SqlPath}");

        return m.Groups["body"].Value
            .Split('\n')
            .Select(l => l.Trim().TrimEnd(','))
            .Where(l => l.Length > 0)
            .Select(l => Regex.Match(l, @"^(\w+)\s+(\w+)"))
            .Select(x => (x.Groups[1].Value, x.Groups[2].Value.ToUpperInvariant()))
            .ToList();
    }

    private static string ClrFor(string sqlType) => sqlType switch
    {
        "NVARCHAR" or "VARCHAR" => nameof(String),
        "UNIQUEIDENTIFIER" => nameof(Guid),
        "BIT" => nameof(Boolean),
        _ => throw new InvalidOperationException($"unmapped SQL type {sqlType}")
    };

    [Theory]
    [InlineData("core.WatchdogFindingType")]
    [InlineData("core.WatchdogResolutionHintType")]
    public void DataTable_MatchesTheTvp_ByNameOrderAndType(string typeName)
    {
        DataTable table = typeName == SqlWatchdogFindingStore.FindingTypeName
            ? SqlWatchdogFindingStore.BuildFindingsTable([])
            : SqlWatchdogFindingStore.BuildHintsTable([]);

        var expected = TvpColumns(typeName);
        var actual = table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType.Name)).ToList();

        Assert.Equal(expected.Select(e => (e.Name, ClrFor(e.Type))).ToList(), actual);
    }

    [Fact]
    public void FindingsTable_WritesNullsAsDbNull()
    {
        var table = SqlWatchdogFindingStore.BuildFindingsTable(new[]
        {
            new WatchdogFindingRow("DEGRADED|STUCK_QUERY", "DEGRADED", null, null, "Critical", "s", "{}", false)
        });

        Assert.Equal(DBNull.Value, table.Rows[0]["LoaderName"]);
        Assert.Equal(DBNull.Value, table.Rows[0]["RunId"]);
    }

    [Fact]
    public void HintsTable_SendsEachKeyOnce_CaseInsensitively()
    {
        var table = SqlWatchdogFindingStore.BuildHintsTable(new[]
        {
            new WatchdogResolutionHint("OVERDUE|Platts", "LoaderDisabled"),
            new WatchdogResolutionHint("OVERDUE|PLATTS", "ConfigRemoved")
        });

        Assert.Equal(1, table.Rows.Count);
    }
}
