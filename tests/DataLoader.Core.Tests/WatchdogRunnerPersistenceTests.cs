using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Persistence is an ADDITIONAL sink: it must never change what is detected, what is
/// mailed, or the exit code — and the email must say when it failed.
/// </summary>
public class WatchdogRunnerPersistenceTests
{
    private sealed class Repo : IWatchdogRepository
    {
        public List<LoaderLastRun> LastRuns = new();
        public Exception? ThrowOnHistory;
        public Exception? ThrowOnStuck;
        public List<RecentFailure> Suppressed = new();

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) =>
            ThrowOnHistory is not null
                ? Task.FromException<IReadOnlyList<LoaderLastRun>>(ThrowOnHistory)
                : Task.FromResult<IReadOnlyList<LoaderLastRun>>(LastRuns);

        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int h, int d, string? host, CancellationToken ct) =>
            ThrowOnStuck is not null
                ? Task.FromException<IReadOnlyList<StuckRun>>(ThrowOnStuck)
                : Task.FromResult<IReadOnlyList<StuckRun>>(Array.Empty<StuckRun>());

        public Task<RecentFailuresResult> GetRecentFailuresAsync(int h, DateTime n, bool s, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Suppressed));

        public Task<RecentFailuresResult> ResolveFailuresByRunIdAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct) =>
            Task.FromResult(new RecentFailuresResult(Array.Empty<RecentFailure>(), Array.Empty<RecentFailure>()));
    }

    private sealed class Sink : IWatchdogFindingSink
    {
        public readonly List<WatchdogPassRecord> Passes = new();
        public Exception? Throw;
        public TimeSpan Delay;

        /// <summary>Like SqlConnection.OpenAsync mid-login: does not react to cancellation.</summary>
        public bool IgnoreToken;

        public async Task<WatchdogPersistResult> RecordPassAsync(WatchdogPassRecord pass, CancellationToken ct)
        {
            Passes.Add(pass);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, IgnoreToken ? CancellationToken.None : ct);
            if (Throw is not null) throw Throw;
            return new WatchdogPersistResult(7, null);
        }
    }

    private sealed class Notifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();
        public NotificationOutcome Outcome = NotificationOutcome.Sent;

        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.FromResult(payload.ShouldNotify ? Outcome : NotificationOutcome.NothingToReport);
        }
    }

    private static WatchdogRunner Runner(Repo repo, Notifier notifier, Sink sink, Action<WatchdogSettings>? configure = null)
    {
        var settings = new WatchdogSettings();
        settings.Loaders["Platts"] = new WatchdogLoaderSettings { MaxGapHours = 31 };
        configure?.Invoke(settings);
        return new WatchdogRunner(repo, notifier, sink, Options.Create(settings), NullLogger<WatchdogRunner>.Instance);
    }

    private static Repo OverdueRepo() => new() { LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) } };

    [Fact]
    public async Task CleanPass_IsRecordedComplete_WithEverySignalChecked()
    {
        var sink = new Sink();
        var repo = new Repo { LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) } };

        var exit = await Runner(repo, new Notifier(), sink).RunAsync(default);

        Assert.Equal(0, exit);
        var pass = Assert.Single(sink.Passes);
        Assert.Equal(WatchdogPassStatus.Complete, pass.Status);
        Assert.Equal(WatchdogSignals.All, pass.SignalsChecked);
        Assert.Empty(pass.SignalsDegraded);
        Assert.Empty(pass.Findings);
    }

    [Fact]
    public async Task OverduePass_PersistsTheFinding_AndSendsTheSameEmailAsBefore()
    {
        var sink = new Sink();
        var notifier = new Notifier();

        await Runner(OverdueRepo(), notifier, sink).RunAsync(default);

        Assert.Equal("OVERDUE|Platts", Assert.Single(Assert.Single(sink.Passes).Findings).FindingKey);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Null(report.PersistenceWarning);
        Assert.DoesNotContain("NOT persisted", report.BuildMessage().Body);
    }

    [Fact]
    public async Task PersistenceThrows_EmailStillSent_WithTheNotPersistedLine_AndExitUnchanged()
    {
        // Acceptance #10.
        var sink = new Sink { Throw = new InvalidOperationException("Could not find stored procedure 'core.usp_RecordWatchdogPass'. password=abcdefghijk1") };
        var notifier = new Notifier();

        var exit = await Runner(OverdueRepo(), notifier, sink).RunAsync(default);

        Assert.Equal(0, exit);
        var body = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads)).BuildMessage().Body;
        Assert.Contains("Findings were NOT persisted: InvalidOperationException: Could not find stored procedure", body);
        Assert.DoesNotContain("abcdefghijk1", body);
    }

    [Fact]
    public async Task PersistenceHangs_IsBoundedByItsTimeout_AndTheEmailStillGoesOut()
    {
        var sink = new Sink { Delay = TimeSpan.FromMinutes(5) };
        var notifier = new Notifier();

        var started = DateTime.UtcNow;
        var exit = await Runner(OverdueRepo(), notifier, sink, s => s.PersistTimeoutSeconds = 5).RunAsync(default);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        var body = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads)).BuildMessage().Body;
        Assert.Contains("Findings were NOT persisted: timed out after 5s", body);
    }

    [Fact]
    public async Task PersistenceIgnoringCancellation_StillCannotHoldTheEmailPastTheBudget()
    {
        // The budget must be a HARD bound, not just a token the sink may or may not honour.
        var sink = new Sink { Delay = TimeSpan.FromMinutes(5), IgnoreToken = true };
        var notifier = new Notifier();

        var started = DateTime.UtcNow;
        var exit = await Runner(OverdueRepo(), notifier, sink, s => s.PersistTimeoutSeconds = 2).RunAsync(default);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.Equal(0, exit);
        var body = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads)).BuildMessage().Body;
        Assert.Contains("Findings were NOT persisted: timed out after 2s", body);
    }

    [Fact]
    public async Task HistoryFailurePersistence_UsesTheShorterCappedBudget()
    {
        var sink = new Sink { Delay = TimeSpan.FromMinutes(5), IgnoreToken = true };
        var notifier = new Notifier();
        var repo = new Repo { ThrowOnHistory = new TimeoutException("x") };

        await Runner(repo, notifier, sink, s => s.PersistTimeoutSeconds = 600).RunAsync(default);

        var failure = Assert.IsType<RunFailureReport>(Assert.Single(notifier.Payloads));
        Assert.Contains($"timed out after {WatchdogRunner.HistoryFailurePersistBudgetSeconds}s", failure.BuildMessage().Body);
    }

    [Fact]
    public async Task EmailFails_FindingsAreStillPersisted_AndExitIsOne()
    {
        // Acceptance #11.
        var sink = new Sink();
        var notifier = new Notifier { Outcome = NotificationOutcome.Failed };

        var exit = await Runner(OverdueRepo(), notifier, sink).RunAsync(default);

        Assert.Equal(1, exit);
        Assert.Single(sink.Passes);
    }

    [Fact]
    public async Task HistoryQueryFails_RecordsAFailedPass_WithOnlyHistoryDegraded_AndNothingChecked()
    {
        // Acceptance #12.
        var sink = new Sink();
        var notifier = new Notifier();
        var repo = new Repo { ThrowOnHistory = new TimeoutException("history timed out") };

        var exit = await Runner(repo, notifier, sink).RunAsync(default);

        Assert.Equal(1, exit);
        var pass = Assert.Single(sink.Passes);
        Assert.Equal(WatchdogPassStatus.Failed, pass.Status);
        Assert.Empty(pass.SignalsChecked);
        Assert.Equal("DEGRADED|HISTORY_QUERY", Assert.Single(pass.Findings).FindingKey);
        var failure = Assert.IsType<RunFailureReport>(Assert.Single(notifier.Payloads));
        Assert.Null(failure.Footnote);
    }

    [Fact]
    public async Task HistoryQueryFails_AndPersistenceFails_TheFailureEmailSaysSo()
    {
        var sink = new Sink { Throw = new InvalidOperationException("db down") };
        var notifier = new Notifier();
        var repo = new Repo { ThrowOnHistory = new TimeoutException("history timed out") };

        await Runner(repo, notifier, sink).RunAsync(default);

        var failure = Assert.IsType<RunFailureReport>(Assert.Single(notifier.Payloads));
        Assert.Contains("Findings were NOT persisted: InvalidOperationException: db down", failure.BuildMessage().Body);
    }

    [Fact]
    public async Task StuckQueryThrows_PassIsDegraded_StuckIsNotChecked_AndADegradedFindingIsSent()
    {
        // Acceptance #3 (the C# half — the proc leaves the open STUCK finding untouched
        // because STUCK is absent from @SignalsChecked).
        var sink = new Sink();
        var repo = new Repo
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) },
            ThrowOnStuck = new TimeoutException("stuck query timed out")
        };

        await Runner(repo, new Notifier(), sink).RunAsync(default);

        var pass = Assert.Single(sink.Passes);
        Assert.Equal(WatchdogPassStatus.Degraded, pass.Status);
        Assert.DoesNotContain("STUCK", pass.SignalsChecked);
        Assert.Equal(new[] { "STUCK" }, pass.SignalsDegraded);
        var row = Assert.Single(pass.Findings);
        Assert.Equal("DEGRADED|STUCK_QUERY", row.FindingKey);
        Assert.Contains("TimeoutException: stuck query timed out", row.DetailJson);
    }

    [Fact]
    public async Task SuppressedFailures_ArePersistedForTrendHistory_ButNeverEmailed()
    {
        var sink = new Sink();
        var notifier = new Notifier();
        var repo = new Repo
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) },
            Suppressed = { new RecentFailure(Guid.NewGuid(), "Platts", DateTime.Now.AddHours(-1), 3, 14, 0) }
        };

        await Runner(repo, notifier, sink).RunAsync(default);

        var row = Assert.Single(Assert.Single(sink.Passes).Findings);
        Assert.True(row.IsSuppressed);
        Assert.All(notifier.Payloads, p => Assert.False(p.ShouldNotify));
    }

    [Fact]
    public async Task PersistFindingsFalse_SinkIsNeverCalled()
    {
        // Acceptance #14.
        var sink = new Sink();
        var notifier = new Notifier();

        var exit = await Runner(OverdueRepo(), notifier, sink, s => s.PersistFindings = false).RunAsync(default);

        Assert.Equal(0, exit);
        Assert.Empty(sink.Passes);
        Assert.Null(Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads)).PersistenceWarning);
    }

    [Fact]
    public async Task PersistFindingsFalse_OnHistoryFailure_SinkIsNeverCalledEither()
    {
        var sink = new Sink();
        var repo = new Repo { ThrowOnHistory = new TimeoutException("x") };

        await Runner(repo, new Notifier(), sink, s => s.PersistFindings = false).RunAsync(default);

        Assert.Empty(sink.Passes);
    }
}
