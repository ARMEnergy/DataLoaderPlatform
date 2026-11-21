using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// The sink's DATA_VOLUME decisions (design §A3/§A4/§D6): which closing hints go to the proc
/// for open volume findings, and the master-switch-off case. The proc's half (what it does
/// with them) is in sql/Core/tests/008_WatchdogDataVolume.Tests.sql.
/// </summary>
public class WatchdogVolumeSinkTests
{
    private static readonly DateOnly Thu1119 = new(2026, 11, 19);

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

    /// <summary>The sink must never read volumes itself; every read throws.</summary>
    private sealed class FakeRepository : IWatchdogRepository
    {
        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int h, int d, string? host, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecentFailuresResult> GetRecentFailuresAsync(int h, DateTime n, bool s, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecentFailuresResult> ResolveFailuresByRunIdAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>()));
        public Task<IReadOnlyList<DailyVolume>> GetDailyVolumesAsync(DateOnly f, DateOnly t, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VolumeLoaderDay>> GetVolumeExcludedDaysAsync(DateOnly f, DateOnly t, CancellationToken ct) => throw new NotSupportedException();
    }

    private static WatchdogSettings Settings(bool enabled, params string[] loaders)
    {
        var s = new WatchdogSettings();
        s.DataVolume.Enabled = enabled;
        foreach (var l in loaders) s.Loaders[l] = new WatchdogLoaderSettings { MaxGapHours = 31 };
        return s;
    }

    private static WatchdogFindingSink Sink(FakeStore store, WatchdogSettings settings) =>
        new(store, new FakeRepository(), Options.Create(settings), NullLogger<WatchdogFindingSink>.Instance);

    private static WatchdogPassRecord Pass(
        bool volumeInScope, string[]? codes = null, WatchdogVolumeResult? volume = null,
        WatchdogPassStatus? status = null, IReadOnlyList<WatchdogFindingRow>? findings = null)
    {
        codes ??= Array.Empty<string>();
        var (chk, deg) = codes.Contains(WatchdogDegradedCodes.HistoryQuery)
            ? WatchdogFindingMapper.ComputeSignals(codes)
            : WatchdogFindingMapper.ComputeSignals(codes, volumeInScope);
        return new WatchdogPassRecord(DateTime.UtcNow, "ARMH-OPSDB01",
            status ?? (codes.Length > 0 ? WatchdogPassStatus.Degraded : WatchdogPassStatus.Complete),
            chk, deg, findings ?? Array.Empty<WatchdogFindingRow>(), null) { Volume = volume };
    }

    private static WatchdogVolumeResult Result(params VolumeLoaderDay[] judged) =>
        new(new DateOnly(2026, 11, 17), Thu1119, Array.Empty<VolumeAnomaly>(), judged,
            Array.Empty<UnjudgedVolumeDay>(), Array.Empty<SkippedVolumeLoader>(),
            new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase));

    private static OpenWatchdogFinding OpenVolume(string loader, DateOnly day) =>
        new(WatchdogFindingMapper.VolumeFindingKey(loader, day), "DATA_VOLUME", loader, null);

    // ---------------------------------------------------------------- A4 ------

    [Fact]
    public async Task Item27_MasterSwitchOff_WithOpenFindings_ClosesThemLoaderDisabled()
    {
        var store = new FakeStore
        {
            Open =
            {
                OpenVolume("Marex", Thu1119),
                OpenVolume("ICE", new DateOnly(2026, 10, 1)),
                new OpenWatchdogFinding("OVERDUE|Marex", "OVERDUE", "Marex", null)
            }
        };

        await Sink(store, Settings(false, "Marex")).RecordPassAsync(Pass(volumeInScope: false), default);

        Assert.Equal(WatchdogSignals.All.Append("DATA_VOLUME"), store.Recorded!.SignalsChecked);
        Assert.Empty(store.Recorded.SignalsDegraded);
        Assert.Equal(WatchdogPassStatus.Complete, store.Recorded.Status);
        var volumeHints = store.Hints!.Where(h => h.FindingKey.StartsWith("DATA_VOLUME|")).ToList();
        Assert.Equal(2, volumeHints.Count);
        Assert.All(volumeHints, h => Assert.Equal("LoaderDisabled", h.ResolutionReason));
    }

    [Fact]
    public async Task Item27_MasterSwitchOff_WithNothingOpen_RecordsThePassExactlyAsGiven()
    {
        var store = new FakeStore { Open = { new OpenWatchdogFinding("OVERDUE|Marex", "OVERDUE", "Marex", null) } };
        var pass = Pass(volumeInScope: false);

        await Sink(store, Settings(false, "Marex")).RecordPassAsync(pass, default);

        var r = store.Recorded!;
        Assert.Equal(pass.Status, r.Status);
        Assert.Equal(pass.SignalsChecked, r.SignalsChecked);
        Assert.Equal(WatchdogSignals.All, r.SignalsChecked);
        Assert.Equal(pass.SignalsDegraded, r.SignalsDegraded);
        Assert.Equal(pass.Findings, r.Findings);
        Assert.Null(r.Volume);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task MasterSwitchOff_NeverOnAFailedPass()
    {
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(false, "Marex"))
            .RecordPassAsync(Pass(false, new[] { WatchdogDegradedCodes.HistoryQuery }, status: WatchdogPassStatus.Failed), default);

        Assert.Equal(0, store.ReadCalls);
        Assert.Empty(store.Recorded!.SignalsChecked);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task MasterSwitchOff_NotWhenLoadersIsEmpty_BecauseThatMayBeABindingFailure()
    {
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(false))
            .RecordPassAsync(Pass(false, new[] { WatchdogDegradedCodes.NoLoadersConfigured }), default);

        Assert.DoesNotContain("DATA_VOLUME", store.Recorded!.SignalsChecked);
        Assert.DoesNotContain(store.Hints!, h => h.FindingKey.StartsWith("DATA_VOLUME|"));
    }

    // ----------------------------------------------------- degraded / blind ------

    [Fact]
    public async Task Item13_VolumeQueryDegraded_OpenFindingGetsNoHint_AndStaysUnchecked()
    {
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(true, "Marex"))
            .RecordPassAsync(Pass(true, new[] { WatchdogDegradedCodes.VolumeQuery }), default);

        Assert.DoesNotContain("DATA_VOLUME", store.Recorded!.SignalsChecked);
        Assert.Contains("DATA_VOLUME", store.Recorded.SignalsDegraded);
        Assert.Equal(WatchdogPassStatus.Degraded, store.Recorded.Status);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task Item29_NoLoadersConfigured_WithVolumeEnabled_NothingCloses()
    {
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(true))
            .RecordPassAsync(Pass(true, new[] { WatchdogDegradedCodes.NoLoadersConfigured }), default);

        Assert.DoesNotContain("DATA_VOLUME", store.Recorded!.SignalsChecked);
        Assert.Contains("DATA_VOLUME", store.Recorded.SignalsDegraded);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task Item21_EnabledButInvalidSection_IsNeitherCheckedNorClosed()
    {
        // The runner leaves DATA_VOLUME out of both lists; the sink must not apply A4 either
        // (the switch is on), so nothing opens and nothing closes.
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(true, "Marex")).RecordPassAsync(Pass(false), default);

        Assert.DoesNotContain("DATA_VOLUME", store.Recorded!.SignalsChecked);
        Assert.DoesNotContain("DATA_VOLUME", store.Recorded.SignalsDegraded);
        Assert.Empty(store.Hints!);
    }

    // --------------------------------------------------------------- A3 ------

    [Fact]
    public async Task Item25_InWindowButUnjudged_IsSentKeepOpen()
    {
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(true, "Marex")).RecordPassAsync(Pass(true, volume: Result()), default);

        Assert.Equal("KeepOpen", Assert.Single(store.Hints!).ResolutionReason);
    }

    [Fact]
    public async Task Item09_JudgedInRange_GetsNoHint()
    {
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(true, "Marex"))
            .RecordPassAsync(Pass(true, volume: Result(new VolumeLoaderDay("Marex", Thu1119))), default);

        Assert.Contains("DATA_VOLUME", store.Recorded!.SignalsChecked);
        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task Item10_AgedOut_And_Item17_VolumeDisabled_AreHinted()
    {
        var settings = Settings(true, "Marex", "ICE");
        settings.Loaders["ICE"].Volume = new WatchdogLoaderVolumeSettings { Enabled = false };
        var store = new FakeStore
        {
            Open = { OpenVolume("Marex", new DateOnly(2026, 11, 16)), OpenVolume("ICE", Thu1119), OpenVolume("Gone", Thu1119) }
        };

        await Sink(store, settings).RecordPassAsync(Pass(true, volume: Result()), default);

        var byKey = store.Hints!.ToDictionary(h => h.FindingKey, h => h.ResolutionReason);
        Assert.Equal("AgedOutUnresolved", byKey["DATA_VOLUME|Marex|2026-11-16"]);
        Assert.Equal("LoaderDisabled", byKey["DATA_VOLUME|ICE|2026-11-19"]);
        Assert.Equal("ConfigRemoved", byKey["DATA_VOLUME|Gone|2026-11-19"]);
    }

    [Fact]
    public async Task ReDetectedFinding_GetsNoHint()
    {
        var open = OpenVolume("Marex", Thu1119);
        var store = new FakeStore { Open = { open } };
        var current = new[] { new WatchdogFindingRow(open.FindingKey, "DATA_VOLUME", "Marex", null, "Warning", "s", "{}", false) };

        await Sink(store, Settings(true, "Marex")).RecordPassAsync(Pass(true, volume: Result(), findings: current), default);

        Assert.Empty(store.Hints!);
    }

    [Fact]
    public async Task CheckedButNoOutcomeAttached_KeepsEverythingOpen()
    {
        // A runner bug that marks DATA_VOLUME checked without attaching the result must not
        // close anything as NotDetected.
        var store = new FakeStore { Open = { OpenVolume("Marex", Thu1119) } };

        await Sink(store, Settings(true, "Marex")).RecordPassAsync(Pass(true, volume: null), default);

        Assert.Equal("KeepOpen", Assert.Single(store.Hints!).ResolutionReason);
    }

    [Fact]
    public async Task VolumeHints_DoNotDisturbOtherSignalsHints()
    {
        var settings = Settings(true, "Marex");
        var store = new FakeStore
        {
            Open = { OpenVolume("Marex", Thu1119), new OpenWatchdogFinding("OVERDUE|Gone", "OVERDUE", "Gone", null) }
        };

        await Sink(store, settings).RecordPassAsync(Pass(true, volume: Result()), default);

        Assert.Contains(store.Hints!, h => h.FindingKey == "OVERDUE|Gone" && h.ResolutionReason == "ConfigRemoved");
        Assert.Contains(store.Hints!, h => h.FindingKey == "DATA_VOLUME|Marex|2026-11-19" && h.ResolutionReason == "KeepOpen");
    }
}
