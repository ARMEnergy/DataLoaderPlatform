using System.Text.Json;
using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <see cref="WatchdogRunner"/>'s DATA_VOLUME wiring (design §D7): when the reads run, what
/// the pass records, what the email carries, and that with the signal off a pass is exactly
/// what it was before the signal existed.
/// </summary>
public class WatchdogVolumeRunnerTests
{
    private static DateTime CentralNow() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"));

    private static DateOnly TodayCentral => DateOnly.FromDateTime(CentralNow());

    private sealed class Repo : IWatchdogRepository
    {
        public List<LoaderLastRun> LastRuns = new();
        public Exception? ThrowOnHistory;
        public List<DailyVolume>? Volumes = new();
        public List<VolumeLoaderDay> Excluded = new();
        public Exception? ThrowOnVolumes;
        public Exception? ThrowOnExcluded;
        public bool ThrowIfVolumeCalled;
        public int VolumeCalls;
        public int ExcludedCalls;
        public (DateOnly From, DateOnly To)? VolumeRange;
        public (DateOnly From, DateOnly To)? ExcludedRange;

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) =>
            ThrowOnHistory is not null
                ? Task.FromException<IReadOnlyList<LoaderLastRun>>(ThrowOnHistory)
                : Task.FromResult<IReadOnlyList<LoaderLastRun>>(LastRuns);

        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int h, int d, string? host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StuckRun>>(Array.Empty<StuckRun>());

        public Task<RecentFailuresResult> GetRecentFailuresAsync(int h, DateTime n, bool s, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>()));

        public Task<RecentFailuresResult> ResolveFailuresByRunIdAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>()));

        public Task<IReadOnlyList<DailyVolume>> GetDailyVolumesAsync(DateOnly f, DateOnly t, CancellationToken ct)
        {
            VolumeCalls++;
            VolumeRange = (f, t);
            if (ThrowIfVolumeCalled) throw new InvalidOperationException("volume read must not run");
            if (ThrowOnVolumes is not null) return Task.FromException<IReadOnlyList<DailyVolume>>(ThrowOnVolumes);
            return Task.FromResult<IReadOnlyList<DailyVolume>>(Volumes!);
        }

        public Task<IReadOnlyList<VolumeLoaderDay>> GetVolumeExcludedDaysAsync(DateOnly f, DateOnly t, CancellationToken ct)
        {
            ExcludedCalls++;
            ExcludedRange = (f, t);
            if (ThrowIfVolumeCalled) throw new InvalidOperationException("volume read must not run");
            if (ThrowOnExcluded is not null) return Task.FromException<IReadOnlyList<VolumeLoaderDay>>(ThrowOnExcluded);
            return Task.FromResult<IReadOnlyList<VolumeLoaderDay>>(Excluded);
        }
    }

    private sealed class Sink : IWatchdogFindingSink
    {
        public readonly List<WatchdogPassRecord> Passes = new();

        public Task<WatchdogPersistResult> RecordPassAsync(WatchdogPassRecord pass, CancellationToken ct)
        {
            Passes.Add(pass);
            return Task.FromResult(new WatchdogPersistResult(7, null));
        }
    }

    private sealed class Notifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();

        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.FromResult(payload.ShouldNotify ? NotificationOutcome.Sent : NotificationOutcome.NothingToReport);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static WatchdogSettings Settings(bool enabled = true, bool notify = false, params string[] loaders)
    {
        var s = new WatchdogSettings();
        s.DataVolume.Enabled = enabled;
        s.DataVolume.Notify = notify;
        // These tests run on the REAL Central clock and judge "yesterday". With the shipped
        // SettleHours (3) a run between 00:00 and 03:00 Central would shift the window back a
        // day and fail spuriously, so yesterday is always settled here. The settle behaviour
        // itself is pinned with a fixed clock in WatchdogVolumeReviewFixTests.
        s.DataVolume.SettleHours = 0;
        foreach (var l in loaders.Length == 0 ? new[] { "Marex" } : loaders)
            s.Loaders[l] = new WatchdogLoaderSettings { MaxGapHours = 31 };
        return s;
    }

    /// <summary>A loader that ran an hour ago (not overdue) with 1,000 rows every day, except yesterday.</summary>
    private static Repo HealthyRepo(long? yesterdayRows = 1000, string loader = "Marex")
    {
        var today = TodayCentral;
        var (from, to) = WatchdogVolumeEvaluator.QueryRange(new WatchdogDataVolumeSettings(), today);
        var repo = new Repo { LastRuns = { new LoaderLastRun(loader, CentralNow().AddHours(-1)) } };
        var id = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            id++;
            var rows = d == today.AddDays(-1) ? yesterdayRows : 1000;
            if (rows is { } r) repo.Volumes!.Add(new DailyVolume(loader, d, r, 1, 1, new[] { id }));
        }
        return repo;
    }

    private static async Task<(int Exit, WatchdogPassRecord Pass, WatchdogReport Report, CapturingLogger<WatchdogRunner> Log)> Run(
        Repo repo, WatchdogSettings settings)
    {
        var sink = new Sink();
        var notifier = new Notifier();
        var log = new CapturingLogger<WatchdogRunner>();
        var exit = await new WatchdogRunner(repo, notifier, sink, Options.Create(settings), log).RunAsync(default);
        var report = notifier.Payloads.OfType<WatchdogReport>().SingleOrDefault();
        return (exit, Assert.Single(sink.Passes), report!, log);
    }

    // --------------------------------------------------- off = unchanged ------

    [Fact]
    public async Task Item01_Disabled_IssuesNoVolumeQuery_AndRecordsNoSignal()
    {
        var repo = HealthyRepo(yesterdayRows: 0);
        repo.ThrowIfVolumeCalled = true;

        var (exit, pass, report, _) = await Run(repo, Settings(enabled: false));

        Assert.Equal(0, exit);
        Assert.Equal(0, repo.VolumeCalls);
        Assert.Equal(0, repo.ExcludedCalls);
        Assert.Equal(WatchdogSignals.All, pass.SignalsChecked);
        Assert.Empty(pass.SignalsDegraded);
        Assert.Empty(pass.Findings);
        Assert.Null(pass.Volume);
        Assert.Equal(WatchdogPassStatus.Complete, pass.Status);
        Assert.False(report.ShouldNotify);
    }

    [Fact]
    public async Task Invariance_DisabledPass_IsIdenticalToAPassWithoutTheFeature_WhateverElseIsConfigured()
    {
        // Baseline: default DataVolume section, i.e. how every pass looked before the signal.
        // Variant: switch off, but every other volume knob set — including Notify=true, an
        // INVALID section and per-loader overrides. None of it may leak into the pass.
        Repo OverdueRepo() => new()
        {
            ThrowIfVolumeCalled = true,
            LastRuns = { new LoaderLastRun("Marex", CentralNow().AddHours(-100)) }
        };

        var baselineSettings = new WatchdogSettings();
        baselineSettings.Loaders["Marex"] = new WatchdogLoaderSettings { MaxGapHours = 31 };

        var variantSettings = Settings(enabled: false, notify: true);
        variantSettings.DataVolume.WindowDays = -1;
        variantSettings.DataVolume.MinSamples = 99;
        variantSettings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { LowPercent = 0, BaselineFromCentral = "garbage" };

        var baseRepo = OverdueRepo();
        var variantRepo = OverdueRepo();
        var a = await Run(baseRepo, baselineSettings);
        var b = await Run(variantRepo, variantSettings);

        Assert.Equal(0, variantRepo.VolumeCalls + variantRepo.ExcludedCalls);
        Assert.Equal(a.Exit, b.Exit);

        // Signal lists: identical to each other AND to the pre-feature one-argument mapping.
        var (preChecked, preDegraded) = WatchdogFindingMapper.ComputeSignals(Array.Empty<string>());
        Assert.Equal(preChecked, b.Pass.SignalsChecked);
        Assert.Equal(preDegraded, b.Pass.SignalsDegraded);
        Assert.Equal(a.Pass.SignalsChecked, b.Pass.SignalsChecked);
        Assert.Equal(a.Pass.SignalsDegraded, b.Pass.SignalsDegraded);
        Assert.Equal(a.Pass.Status, b.Pass.Status);
        Assert.Equal(a.Pass.Findings, b.Pass.Findings);
        Assert.Null(b.Pass.Volume);

        // Email: same subject, same body (the first line carries the pass timestamp).
        var ma = a.Report.BuildMessage();
        var mb = b.Report.BuildMessage();
        Assert.Equal(ma.Subject, mb.Subject);
        Assert.Equal(StripFirstLine(ma.Body), StripFirstLine(mb.Body));
        Assert.DoesNotContain("ROW VOLUME", mb.Body);
        Assert.DoesNotContain("row-volume", mb.Subject);
        Assert.Empty(b.Report.VolumeAnomalies);

        static string StripFirstLine(string s) => s[(s.IndexOf('\n') + 1)..];
    }

    // ------------------------------------------------------- on: happy path ------

    [Fact]
    public async Task Item15_NotifyOff_AnomalyIsPersisted_ButNotEmailed()
    {
        var (exit, pass, report, _) = await Run(HealthyRepo(yesterdayRows: 240), Settings(notify: false));

        Assert.Equal(0, exit);
        var key = $"DATA_VOLUME|Marex|{TodayCentral.AddDays(-1):yyyy-MM-dd}";
        var row = Assert.Single(pass.Findings, f => f.Signal == "DATA_VOLUME");
        Assert.Equal(key, row.FindingKey);
        Assert.Equal("Warning", row.Severity);
        Assert.Contains("DATA_VOLUME", pass.SignalsChecked);
        Assert.Equal(WatchdogPassStatus.Complete, pass.Status);
        Assert.NotNull(pass.Volume);

        Assert.Empty(report.VolumeAnomalies);
        Assert.False(report.ShouldNotify);
    }

    [Fact]
    public async Task Item16_NotifyOn_EmailCarriesTheRowVolumeSection()
    {
        var (_, pass, report, _) = await Run(HealthyRepo(yesterdayRows: 240), Settings(notify: true));

        Assert.True(report.ShouldNotify);
        var msg = report.BuildMessage();
        Assert.Equal("WATCHDOG: 1 row-volume anomaly", msg.Subject);
        Assert.Contains("ROW VOLUME", msg.Body);
        var summary = Assert.Single(pass.Findings, f => f.Signal == "DATA_VOLUME").Summary;
        Assert.Contains("  " + summary, msg.Body);
    }

    [Fact]
    public async Task NotifyOn_ButNoAnomalies_EmailIsUnchanged()
    {
        var (_, pass, report, _) = await Run(HealthyRepo(), Settings(notify: true));

        Assert.Contains("DATA_VOLUME", pass.SignalsChecked);
        Assert.False(report.ShouldNotify);
        Assert.Empty(report.VolumeAnomalies);
    }

    [Fact]
    public async Task HighDay_IsPersistedAsInfo()
    {
        var (_, pass, _, _) = await Run(HealthyRepo(yesterdayRows: 3500), Settings());
        Assert.Equal("Info", Assert.Single(pass.Findings, f => f.Signal == "DATA_VOLUME").Severity);
    }

    [Fact]
    public async Task QueryRange_IsCentralDates_EndingYesterday_NeverToday()
    {
        var repo = HealthyRepo();
        var before = TodayCentral;
        await Run(repo, Settings());
        var after = TodayCentral;

        var (from, to) = repo.VolumeRange!.Value;
        Assert.True(to == before.AddDays(-1) || to == after.AddDays(-1));
        Assert.Equal(to.AddDays(-(3 + 7 * 18) + 1), from);
        Assert.Equal(repo.VolumeRange, repo.ExcludedRange);
    }

    [Fact]
    public async Task ExcludedDays_FromTheRepository_AreUsed_AndReported()
    {
        var repo = HealthyRepo(yesterdayRows: 240);
        var y = TodayCentral.AddDays(-1);
        // Last week's same weekday was itself reported: not a sample.
        repo.Excluded.Add(new VolumeLoaderDay("marex", y.AddDays(-7)));

        var (_, pass, _, _) = await Run(repo, Settings());

        var json = JsonDocument.Parse(Assert.Single(pass.Findings, f => f.Signal == "DATA_VOLUME").DetailJson).RootElement;
        Assert.Equal(new[] { $"{y.AddDays(-7):yyyy-MM-dd}" },
            json.GetProperty("excludedSampleDays").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task LogsAnomalyAndUnjudgedCounts()
    {
        var (_, _, _, log) = await Run(HealthyRepo(yesterdayRows: 240), Settings());
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("1 volume anomalies, 0 unjudged"));
    }

    [Fact]
    public async Task PerLoaderInvalid_LogsAWarning_AndProducesNoFindingForIt()
    {
        var settings = Settings();
        settings.Loaders["Marex"].Volume = new WatchdogLoaderVolumeSettings { BaselineFromCentral = "11/12/2026" };

        var (_, pass, _, log) = await Run(HealthyRepo(yesterdayRows: 0), settings);

        Assert.DoesNotContain(pass.Findings, f => f.Signal == "DATA_VOLUME");
        Assert.DoesNotContain(pass.Findings, f => f.Signal == "MISCONFIGURED");
        Assert.Contains("DATA_VOLUME", pass.SignalsChecked);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("row-volume check skipped for Marex"));
    }

    // -------------------------------------------------------- degraded ------

    [Fact]
    public async Task Item13_VolumeQueryThrows_DegradesOnlyDataVolume_PassDegraded()
    {
        var repo = HealthyRepo(yesterdayRows: 0);
        repo.ThrowOnVolumes = new InvalidOperationException("Invalid object name 'core.LoaderRun_Summary'. password=hunter2hunter2");

        var (exit, pass, report, _) = await Run(repo, Settings(notify: false));

        Assert.Equal(0, exit);
        Assert.Equal(WatchdogPassStatus.Degraded, pass.Status);
        Assert.Equal(WatchdogSignals.All, pass.SignalsChecked);
        Assert.Equal(new[] { "DATA_VOLUME" }, pass.SignalsDegraded);
        var degraded = Assert.Single(pass.Findings);
        Assert.Equal("DEGRADED|VOLUME_QUERY", degraded.FindingKey);
        Assert.DoesNotContain("hunter2hunter2", degraded.DetailJson);
        Assert.Null(pass.Volume);

        // Notify off: the degradation is persisted, not mailed.
        Assert.False(report.ShouldNotify);
        Assert.Empty(report.Degraded);
    }

    [Fact]
    public async Task Item13_ExcludedDaysQueryThrows_IsTheSameDegradation()
    {
        var repo = HealthyRepo(yesterdayRows: 0);
        repo.ThrowOnExcluded = new TimeoutException("timeout");

        var (_, pass, _, _) = await Run(repo, Settings());

        Assert.Equal("DEGRADED|VOLUME_QUERY", Assert.Single(pass.Findings).FindingKey);
        Assert.Contains("DATA_VOLUME", pass.SignalsDegraded);
    }

    [Fact]
    public async Task VolumeQueryThrows_WithNotifyOn_IsInTheEmailsCouldNotCheck()
    {
        var repo = HealthyRepo();
        repo.ThrowOnVolumes = new InvalidOperationException("boom");

        var (_, _, report, _) = await Run(repo, Settings(notify: true));

        Assert.True(report.ShouldNotify);
        Assert.Contains(report.Degraded, d => d.StartsWith("row volume (InvalidOperationException: boom"));
    }

    [Fact]
    public async Task EvaluationBug_DegradesOnlyDataVolume_TheRestOfThePassSurvives()
    {
        var repo = HealthyRepo();
        repo.Volumes = null;   // the evaluator throws on it
        repo.LastRuns[0] = new LoaderLastRun("Marex", CentralNow().AddHours(-100));   // overdue

        var (exit, pass, report, _) = await Run(repo, Settings());

        Assert.Equal(0, exit);
        Assert.Contains(pass.Findings, f => f.FindingKey == "DEGRADED|VOLUME_QUERY");
        Assert.Contains(pass.Findings, f => f.FindingKey == "OVERDUE|Marex");
        Assert.Single(report.Overdue);
    }

    [Fact]
    public async Task Item14_NextPassRecovers_NoDegradedFinding_SoTheProcClosesIt()
    {
        var repo = HealthyRepo();
        repo.ThrowOnVolumes = new InvalidOperationException("x");
        var first = await Run(repo, Settings());
        Assert.Contains(first.Pass.Findings, f => f.FindingKey == "DEGRADED|VOLUME_QUERY");
        Assert.Contains("DEGRADED", first.Pass.SignalsChecked);

        repo.ThrowOnVolumes = null;
        var second = await Run(repo, Settings());

        Assert.DoesNotContain(second.Pass.Findings, f => f.Signal == "DEGRADED");
        Assert.Contains("DEGRADED", second.Pass.SignalsChecked);   // checked + absent → closes NotDetected
        Assert.Contains("DATA_VOLUME", second.Pass.SignalsChecked);
        Assert.Equal(WatchdogPassStatus.Complete, second.Pass.Status);
    }

    [Fact]
    public async Task Item21_InvalidSection_NoQuery_WarningLogged_NeitherCheckedNorDegraded()
    {
        var repo = HealthyRepo(yesterdayRows: 0);
        repo.ThrowIfVolumeCalled = true;
        var settings = Settings();
        settings.DataVolume.MinSamples = 7;   // > BaselineWeeks = 6

        var (exit, pass, _, log) = await Run(repo, settings);

        Assert.Equal(0, exit);
        Assert.Equal(0, repo.VolumeCalls + repo.ExcludedCalls);
        Assert.DoesNotContain("DATA_VOLUME", pass.SignalsChecked);
        Assert.DoesNotContain("DATA_VOLUME", pass.SignalsDegraded);
        Assert.Equal(WatchdogPassStatus.Complete, pass.Status);
        Assert.Empty(pass.Findings);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MinSamples=7"));
    }

    [Fact]
    public async Task Item29_NoLoadersConfigured_NoVolumeQuery_DataVolumeDegraded()
    {
        var repo = new Repo { ThrowIfVolumeCalled = true };
        var settings = new WatchdogSettings();
        settings.DataVolume.Enabled = true;

        var (_, pass, _, _) = await Run(repo, settings);

        Assert.Equal(0, repo.VolumeCalls + repo.ExcludedCalls);
        Assert.DoesNotContain("DATA_VOLUME", pass.SignalsChecked);
        Assert.Contains("DATA_VOLUME", pass.SignalsDegraded);
        Assert.Contains(pass.Findings, f => f.FindingKey == "DEGRADED|NO_LOADERS_CONFIGURED");
        Assert.DoesNotContain(pass.Findings, f => f.FindingKey == "DEGRADED|VOLUME_QUERY");
    }

    [Fact]
    public async Task HistoryQueryFails_VolumeNotEvaluated_NotChecked()
    {
        var repo = HealthyRepo(yesterdayRows: 0);
        repo.ThrowOnHistory = new InvalidOperationException("db down");
        repo.ThrowIfVolumeCalled = true;

        var sink = new Sink();
        var exit = await new WatchdogRunner(repo, new Notifier(), sink, Options.Create(Settings()),
            new CapturingLogger<WatchdogRunner>()).RunAsync(default);

        Assert.Equal(1, exit);
        Assert.Equal(0, repo.VolumeCalls);
        var pass = Assert.Single(sink.Passes);
        Assert.Equal(WatchdogPassStatus.Failed, pass.Status);
        Assert.DoesNotContain("DATA_VOLUME", pass.SignalsChecked);
        Assert.Empty(pass.SignalsChecked);
    }
}
