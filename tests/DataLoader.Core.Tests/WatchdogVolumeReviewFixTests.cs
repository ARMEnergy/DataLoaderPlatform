using System.Text.Json;
using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// DATA_VOLUME review fixes (2026-11-20): settle lag, DEGRADED|VOLUME_QUERY carry-forward,
/// cancellation degrading rather than aborting, unparseable keys, section limits, the
/// 400-char Summary cap, and the Low percentage text.
/// </summary>
public class WatchdogVolumeReviewFixTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);          // Friday
    private static readonly DateOnly Yesterday = new(2026, 11, 19);      // Thursday

    private static WatchdogSettings Settings(bool enabled = true, params string[] loaders)
    {
        var s = new WatchdogSettings();
        s.DataVolume.Enabled = enabled;
        foreach (var l in loaders.Length == 0 ? new[] { "Marex" } : loaders)
            s.Loaders[l] = new WatchdogLoaderSettings { MaxGapHours = 31 };
        return s;
    }

    /// <summary>One successful run per day, 1,000 rows, except where <paramref name="rows"/> says otherwise.</summary>
    private static List<DailyVolume> History(Func<DateOnly, long?>? rows = null)
    {
        var list = new List<DailyVolume>();
        for (var d = Today.AddDays(-200); d < Today; d = d.AddDays(1))
            if ((rows?.Invoke(d) ?? 1000) is { } r) list.Add(new DailyVolume("Marex", d, r, 1, 1, new[] { 1 }));
        return list;
    }

    private static DateTime At(DateOnly day, int hour, int minute = 0) => day.ToDateTime(new TimeOnly(hour, minute));

    // ----------------------------------------------------------------- 1. settle lag ------

    [Fact]
    public void BeforeSettleHours_WindowEndsTheDayBeforeYesterday_AndKeepsItsLength()
    {
        // Yesterday is half-loaded at 00:05 — its late runs have not completed yet.
        var r = WatchdogVolumeEvaluator.Evaluate(
            History(d => d == Yesterday ? 300 : 1000), Array.Empty<VolumeLoaderDay>(), Settings(), At(Today, 0, 5));

        Assert.Equal(new DateOnly(2026, 11, 16), r.WindowStart);
        Assert.Equal(new DateOnly(2026, 11, 18), r.WindowEnd);
        Assert.Empty(r.Anomalies);                                        // no false Low
        Assert.Equal(3, r.JudgedInRange.Count);
        Assert.DoesNotContain(r.JudgedInRange, j => j.DayCentral == Yesterday);
    }

    [Fact]
    public void AtSettleHours_YesterdayJoinsTheWindow()
    {
        var r = WatchdogVolumeEvaluator.Evaluate(
            History(d => d == Yesterday ? 300 : 1000), Array.Empty<VolumeLoaderDay>(), Settings(), At(Today, 3));

        Assert.Equal(new DateOnly(2026, 11, 17), r.WindowStart);
        Assert.Equal(Yesterday, r.WindowEnd);
        Assert.Equal(Yesterday, Assert.Single(r.Anomalies).DayCentral);
    }

    [Fact]
    public void SettleHoursZero_YesterdayIsAlwaysSettled()
    {
        var s = Settings();
        s.DataVolume.SettleHours = 0;
        Assert.True(WatchdogVolumeEvaluator.YesterdaySettled(s.DataVolume, At(Today, 0)));
        Assert.Equal(Yesterday, WatchdogVolumeEvaluator.Evaluate(History(), [], s, At(Today, 0)).WindowEnd);
    }

    [Fact]
    public void QueryRange_FollowsTheShiftedWindow()
    {
        var s = new WatchdogDataVolumeSettings();
        Assert.Equal((new DateOnly(2026, 7, 13), new DateOnly(2026, 11, 18)), WatchdogVolumeEvaluator.QueryRange(s, At(Today, 1)));
        Assert.Equal((new DateOnly(2026, 7, 14), Yesterday), WatchdogVolumeEvaluator.QueryRange(s, At(Today, 4)));
    }

    [Fact]
    public void OpenFindingForYesterday_BeforeSettle_IsKeptOpen_NotClosedNorAgedOut()
    {
        var settings = Settings();
        var result = WatchdogVolumeEvaluator.Evaluate(History(), [], settings, At(Today, 0, 5));
        var open = new OpenWatchdogFinding(
            WatchdogFindingMapper.VolumeFindingKey("Marex", Yesterday), "DATA_VOLUME", "Marex", null);

        var hint = Assert.Single(WatchdogFindingMapper.BuildVolumeHints(new[] { open }, [], settings, result, false));

        Assert.Equal(WatchdogResolutionHints.KeepOpen, hint.ResolutionReason);
    }

    [Fact]
    public void OpenFindingForTheOldestSettledDay_BeforeSettle_IsStillInTheWindow()
    {
        // 11-17 was the oldest day at yesterday's 23:00 pass. At 00:05 the shifted window is
        // 11-16..11-18, so it must NOT age out just because the date ticked over.
        var settings = Settings();
        var result = WatchdogVolumeEvaluator.Evaluate(
            History(d => d == new DateOnly(2026, 11, 17) ? 1000 : 1000), [], settings, At(Today, 0, 5));
        var open = new OpenWatchdogFinding("DATA_VOLUME|Marex|2026-11-17", "DATA_VOLUME", "Marex", null);

        Assert.Empty(WatchdogFindingMapper.BuildVolumeHints(new[] { open }, [], settings, result, false)); // judged → NotDetected
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void ValidateSection_RejectsSettleHoursOutOfRange(int hours)
    {
        var s = new WatchdogDataVolumeSettings { SettleHours = hours };
        Assert.Contains("SettleHours", WatchdogVolumeEvaluator.ValidateSection(s));
    }

    [Fact]
    public void SettleHours_DefaultsToThree() => Assert.Equal(3, new WatchdogDataVolumeSettings().SettleHours);

    // ------------------------------------------------------------- 5. section limits ------

    [Fact]
    public void ValidateSection_RejectsWindowDaysAboveSeven()
    {
        Assert.Contains("WindowDays", WatchdogVolumeEvaluator.ValidateSection(new WatchdogDataVolumeSettings { WindowDays = 8 }));
        Assert.Null(WatchdogVolumeEvaluator.ValidateSection(new WatchdogDataVolumeSettings { WindowDays = 7 }));
    }

    [Fact]
    public void ValidateSection_RejectsASpanReachingTheFindingRetention()
    {
        // 3 + 7 × 25 = 178 is fine; 3 + 7 × 26 = 185 reaches past the 180-day retention.
        Assert.Null(WatchdogVolumeEvaluator.ValidateSection(new WatchdogDataVolumeSettings { MaxBaselineLookbackWeeks = 25 }));
        var reason = WatchdogVolumeEvaluator.ValidateSection(new WatchdogDataVolumeSettings { MaxBaselineLookbackWeeks = 26 });
        Assert.Contains("180", reason);
        Assert.Contains("MaxBaselineLookbackWeeks", reason);
    }

    // ---------------------------------------------------------- 4. unparseable keys ------

    [Theory]
    [InlineData("DATA_VOLUME|Marex|not-a-date")]
    [InlineData("DATA_VOLUME|Marex")]
    [InlineData("DATA_VOLUME||2026-11-19")]
    public void UnparseableOpenKey_IsAgedOutUnresolved(string key)
    {
        var open = new OpenWatchdogFinding(key, "DATA_VOLUME", "Marex", null);
        var result = WatchdogVolumeEvaluator.Evaluate(History(), [], Settings(), Today);

        Assert.Equal("AgedOutUnresolved",
            Assert.Single(WatchdogFindingMapper.BuildVolumeHints(new[] { open }, [], Settings(), result, false)).ResolutionReason);
        Assert.Equal("AgedOutUnresolved",
            Assert.Single(WatchdogFindingMapper.BuildVolumeHints(new[] { open }, [], Settings(), null, false)).ResolutionReason);
    }

    // ------------------------------------------------------------ 6. summary length ------

    [Fact]
    public void Summary_UnderTheLimit_IsByteIdenticalToClean()
    {
        const string line = "Marex: 2026-11-19 03:00 Central, 2/10 units failed";
        Assert.Equal(ErrorTextRedactor.Clean(line, WatchdogFindingMapper.MaxSummaryChars), WatchdogFindingMapper.Summary(line));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(1_000)]
    [InlineData(100_000)]
    public void Summary_OverTheLimit_FitsAndStillSaysItWasTruncated(int length)
    {
        var s = WatchdogFindingMapper.Summary(new string('x', length));
        Assert.True(s.Length <= WatchdogFindingMapper.MaxSummaryChars);
        Assert.EndsWith($"… [truncated, {length} chars total]", s);
    }

    [Fact]
    public void DegradedRow_WithAHugeReason_FitsTheColumn()
    {
        var row = WatchdogFindingMapper.DegradedRow(new DegradedCheck("STUCK_QUERY", new string('e', 2_000), "r"));
        Assert.True(row.Summary.Length <= WatchdogFindingMapper.MaxSummaryChars);
    }

    // ---------------------------------------------------------------- 7. cosmetics ------

    private static VolumeAnomaly Low(long rows, double median, int lowPercent = 50, VolumeDirection dir = VolumeDirection.Low) =>
        new("Marex", Yesterday, dir, rows, 1, 1, median, rows / median, lowPercent, 3,
            Array.Empty<VolumeSample>(), Array.Empty<DateOnly>(), null, new[] { 1 });

    [Theory]
    [InlineData(2_500, 8_700, "29%")]        // §R.5's example, unchanged
    [InlineData(4_960, 10_000, "49.6%")]     // would round to 50% = the threshold
    [InlineData(4_999, 10_000, "49.9%")]     // rounded DOWN, never up to the threshold
    [InlineData(40, 10_000, "<1%")]          // non-zero must never read 0%
    [InlineData(1, 1_000_000, "<1%")]
    [InlineData(150, 10_000, "2%")]
    public void LowPercentText(long rows, double median, string expected) =>
        Assert.Equal(expected, WatchdogReport.LowPercentText(Low(rows, median)));

    [Fact]
    public void ZeroDay_ReadsZeroPercent() =>
        Assert.Equal("0%", WatchdogReport.LowPercentText(Low(0, 8_700, dir: VolumeDirection.Zero)));

    [Fact]
    public void LowLine_UsesTheDecimalText()
    {
        Assert.Equal("Marex loaded 4,960 rows on Thu 2026-11-19 (Central), 49.6% of its usual Thursday (10,000).",
            WatchdogReport.VolumeLine(Low(4_960, 10_000)));
    }

    [Fact]
    public void MedianZero_IsUnjudged_AsMedianIsZero()
    {
        var s = Settings();
        s.DataVolume.MinMedianRows = 0;
        var r = WatchdogVolumeEvaluator.Evaluate(History(_ => 0), [], s, Today);

        Assert.Empty(r.Anomalies);
        Assert.Contains(r.Unjudged, u => u.DayCentral == Yesterday && u.Reason == "median is 0");
    }

    [Fact]
    public void DetailJson_HighMultiple_IsNotRounded()
    {
        var a = Low(2_500, 8_700) with { HighMultiple = 2.125 };
        var json = JsonDocument.Parse(WatchdogFindingMapper.VolumeRow(a, Settings()).DetailJson).RootElement;
        Assert.Equal(2.125, json.GetProperty("highMultiple").GetDouble());
    }

    // ------------------------------------------- 2. DEGRADED|VOLUME_QUERY carry-forward ------

    private sealed class Store : IWatchdogFindingStore
    {
        public List<OpenWatchdogFinding> Open = new();
        public WatchdogPassRecord? Recorded;
        public IReadOnlyList<WatchdogResolutionHint>? Hints;

        public Task<IReadOnlyList<OpenWatchdogFinding>> ReadOpenFindingsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OpenWatchdogFinding>>(Open);

        public Task<WatchdogPersistResult> RecordPassAsync(
            WatchdogPassRecord pass, IReadOnlyList<WatchdogResolutionHint> hints, CancellationToken ct)
        {
            Recorded = pass;
            Hints = hints;
            return Task.FromResult(new WatchdogPersistResult(1, null));
        }
    }

    private const string VolumeQueryKey = "DEGRADED|VOLUME_QUERY";

    private static async Task<WatchdogPassRecord> RecordWithOpenVolumeQuery(
        WatchdogSettings settings, bool volumeInScope, WatchdogVolumeResult? volume = null)
    {
        var store = new Store { Open = { new OpenWatchdogFinding(VolumeQueryKey, "DEGRADED", null, null) } };
        var (chk, deg) = WatchdogFindingMapper.ComputeSignals(Array.Empty<string>(), volumeInScope);
        var pass = new WatchdogPassRecord(DateTime.UtcNow, "h", WatchdogPassStatus.Complete, chk, deg,
            Array.Empty<WatchdogFindingRow>(), null) { Volume = volume };
        await new WatchdogFindingSink(store, null!, Options.Create(settings), NullLogger<WatchdogFindingSink>.Instance)
            .RecordPassAsync(pass, default);
        return store.Recorded!;
    }

    [Fact]
    public async Task OpenVolumeQuery_InvalidSection_IsCarriedForward()
    {
        var s = Settings();
        s.DataVolume.MinSamples = 99;        // invalid → the runner skips the check, volumeInScope false
        var recorded = await RecordWithOpenVolumeQuery(s, volumeInScope: false);

        var carried = Assert.Single(recorded.Findings, f => f.FindingKey == VolumeQueryKey);
        Assert.Contains("not re-attempted", carried.Summary);
    }

    [Fact]
    public async Task OpenVolumeQuery_MasterSwitchOff_IsLetClose()
    {
        var recorded = await RecordWithOpenVolumeQuery(Settings(enabled: false), volumeInScope: false);
        Assert.DoesNotContain(recorded.Findings, f => f.FindingKey == VolumeQueryKey);
    }

    [Fact]
    public async Task OpenVolumeQuery_CheckRanThisPass_IsLetClose()
    {
        var result = WatchdogVolumeEvaluator.Evaluate(History(), [], Settings(), Today);
        var recorded = await RecordWithOpenVolumeQuery(Settings(), volumeInScope: true, result);
        Assert.DoesNotContain(recorded.Findings, f => f.FindingKey == VolumeQueryKey);
    }

    // ------------------------------------------------------------- 3. cancellation ------

    private sealed class Repo : IWatchdogRepository
    {
        public Func<Exception>? OnVolumes;
        public (DateOnly From, DateOnly To)? VolumeRange;

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LoaderLastRun>>(new[] { new LoaderLastRun("Marex", At(Today, 0)) });
        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int h, int d, string? host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StuckRun>>(Array.Empty<StuckRun>());
        public Task<RecentFailuresResult> GetRecentFailuresAsync(int h, DateTime n, bool s, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>()));
        public Task<RecentFailuresResult> ResolveFailuresByRunIdAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>()));

        public Task<IReadOnlyList<DailyVolume>> GetDailyVolumesAsync(DateOnly f, DateOnly t, CancellationToken ct)
        {
            VolumeRange = (f, t);
            return OnVolumes is not null
                ? Task.FromException<IReadOnlyList<DailyVolume>>(OnVolumes())
                : Task.FromResult<IReadOnlyList<DailyVolume>>(History());
        }

        public Task<IReadOnlyList<VolumeLoaderDay>> GetVolumeExcludedDaysAsync(DateOnly f, DateOnly t, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VolumeLoaderDay>>(Array.Empty<VolumeLoaderDay>());
    }

    private sealed class PassSink : IWatchdogFindingSink
    {
        public readonly List<WatchdogPassRecord> Passes = new();
        public Task<WatchdogPersistResult> RecordPassAsync(WatchdogPassRecord pass, CancellationToken ct)
        {
            Passes.Add(pass);
            return Task.FromResult(new WatchdogPersistResult(1, null));
        }
    }

    private sealed class Notifier : IFailureNotifier
    {
        public int Calls;
        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(payload.ShouldNotify ? NotificationOutcome.Sent : NotificationOutcome.NothingToReport);
        }
    }

    private static async Task<(int Exit, WatchdogPassRecord Pass, Notifier Notifier, Repo Repo)> RunAt(DateTime nowCentral, Repo repo)
    {
        var sink = new PassSink();
        var notifier = new Notifier();
        var runner = new WatchdogRunner(repo, notifier, sink, Options.Create(Settings()), NullLogger<WatchdogRunner>.Instance)
        {
            CentralClock = () => nowCentral
        };
        var exit = await runner.RunAsync(default);
        return (exit, Assert.Single(sink.Passes), notifier, repo);
    }

    [Fact]
    public async Task VolumeReadCancelled_DegradesDataVolume_AndThePassStillCompletes()
    {
        var (exit, pass, notifier, _) = await RunAt(At(Today, 4),
            new Repo { OnVolumes = () => new OperationCanceledException("read cancelled") });

        Assert.Equal(0, exit);
        Assert.Equal(1, notifier.Calls);
        Assert.Equal(WatchdogPassStatus.Degraded, pass.Status);
        Assert.Equal(new[] { "DATA_VOLUME" }, pass.SignalsDegraded);
        Assert.Contains(pass.Findings, f => f.FindingKey == VolumeQueryKey);
    }

    [Fact]
    public async Task Runner_BeforeSettleHours_ReadsAndJudgesTheShiftedWindow()
    {
        var (_, pass, _, repo) = await RunAt(At(Today, 0, 5), new Repo());

        Assert.Equal(new DateOnly(2026, 11, 18), repo.VolumeRange!.Value.To);
        Assert.Equal(new DateOnly(2026, 11, 18), pass.Volume!.WindowEnd);
        Assert.Contains("DATA_VOLUME", pass.SignalsChecked);
    }

    [Fact]
    public async Task Runner_AfterSettleHours_IncludesYesterday()
    {
        var (_, pass, _, repo) = await RunAt(At(Today, 3, 30), new Repo());

        Assert.Equal(Yesterday, repo.VolumeRange!.Value.To);
        Assert.Equal(Yesterday, pass.Volume!.WindowEnd);
    }
}
