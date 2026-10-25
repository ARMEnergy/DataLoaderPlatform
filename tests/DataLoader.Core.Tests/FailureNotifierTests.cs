using DataLoader.Core.Abstractions;
using DataLoader.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Core.Tests;

public class FailureNotifierTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeSender : INotificationSender
    {
        public readonly List<NotificationMessage> Sent = new();
        public Exception? ThrowThis;
        public TimeSpan Delay = TimeSpan.Zero;

        public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            if (ThrowThis is not null) throw ThrowThis;
            Sent.Add(message);
        }
    }

    private sealed class FakePayload : INotificationPayload
    {
        public bool ShouldNotify { get; init; }
        public string Body { get; init; } = "body";
        public NotificationMessage BuildMessage() => new("subject", Body);
    }

    private sealed class ExplodingPayload : INotificationPayload
    {
        public bool ShouldNotify => true;
        public NotificationMessage BuildMessage() =>
            throw new InvalidOperationException("bad payload");
    }

    /// <summary>
    /// Standard Microsoft.Extensions.Logging providers do not throw, but the
    /// never-throw guarantee has to hold even if one does — logging must not be
    /// able to escape the method or invert the reported outcome.
    /// </summary>
    private sealed class ThrowingLogger : ILogger<FailureNotifier>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger exploded");
    }

    private static FailureNotifier Build(FakeSender sender, bool enabled = true, int timeoutSeconds = 30) =>
        new(sender,
            new NotificationSettings
            {
                Enabled = enabled,
                SmtpHost = "relay",
                From = "svc@armenergy.com",
                To = { "ops@armenergy.com" },
                TimeoutSeconds = timeoutSeconds
            },
            NullLogger<FailureNotifier>.Instance);

    // --------------------------------------------------------------- outcomes

    [Fact]
    public async Task NothingToReport_WhenPayloadSaysSo_AndNothingIsSent()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = false }, default);

        Assert.Equal(NotificationOutcome.NothingToReport, outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Sent_WhenPayloadSaysSo()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Sent, outcome);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Disabled_SendsNothing_EvenWhenThereIsNewsToReport()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender, enabled: false)
            .NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Disabled, outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Disabled_ButNothingToReport_ReturnsNothingToReport()
    {
        // ShouldNotify is checked before Enabled, not the other way round. Pinned
        // because the order is what makes this correct: Disabled would leave a
        // reader wondering whether there was ever anything to report at all,
        // and that is never true when the payload itself says no.
        var sender = new FakeSender();

        var outcome = await Build(sender, enabled: false)
            .NotifyAsync(new FakePayload { ShouldNotify = false }, default);

        Assert.Equal(NotificationOutcome.NothingToReport, outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task UsesTheConfiguredSubjectPrefix()
    {
        var sender = new FakeSender();
        var notifier = new FailureNotifier(
            sender,
            new NotificationSettings
            {
                Enabled = true, SmtpHost = "relay", From = "a@b.com",
                To = { "c@d.com" }, SubjectPrefix = "[CUSTOM]"
            },
            NullLogger<FailureNotifier>.Instance);

        await notifier.NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        // The payload returns a bare "subject"; prefixing is the notifier's job,
        // so a payload cannot ship an alert with DEV and PROD reading alike.
        Assert.Equal("[CUSTOM] subject", sender.Sent[0].Subject);
    }

    [Fact]
    public async Task RedactsTheFinishedMessage_EvenWhenThePayloadForgot()
    {
        // Defence in depth. RunFailureReport does redact, but the contract is a
        // doc comment, and this platform has already leaked an API key through a
        // path that trusted its caller. A future payload must not be able to
        // reintroduce that by forgetting.
        var sender = new FakeSender();
        var payload = new FakePayload
        {
            ShouldNotify = true,
            Body = "access denied (API key: abc123XYZ)"
        };

        await Build(sender).NotifyAsync(payload, default);

        Assert.DoesNotContain("abc123XYZ", sender.Sent[0].Body);
    }

    // ------------------------------------------------- it must NEVER propagate

    [Fact]
    public async Task NeverThrows_WhenTheSenderThrows()
    {
        // A broken relay must not fail a good load. This is the single most
        // important behaviour in the class.
        var sender = new FakeSender { ThrowThis = new InvalidOperationException("relay down") };

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Failed, outcome);
    }

    [Fact]
    public async Task NeverThrows_WhenBuildingTheMessageThrows()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new ExplodingPayload(), default);

        Assert.Equal(NotificationOutcome.Failed, outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task TimesOut_RatherThanHangingTheScheduledTask()
    {
        var sender = new FakeSender { Delay = TimeSpan.FromSeconds(10) };

        var started = DateTime.UtcNow;
        var outcome = await Build(sender, timeoutSeconds: 1)
            .NotifyAsync(new FakePayload { ShouldNotify = true }, default);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(NotificationOutcome.Failed, outcome);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed.TotalSeconds:F1}s — the timeout did not apply");
    }

    [Fact]
    public async Task AThrowingLogger_DoesNotInvertASuccessfulSend()
    {
        // A provider that throws from the success-path log must not be caught by
        // the surrounding try/catch and reported as Failed for a message that was
        // actually delivered — the watchdog sets its exit code from that value.
        var sender = new FakeSender();
        var notifier = new FailureNotifier(
            sender,
            new NotificationSettings { Enabled = true, SmtpHost = "relay", From = "a@b.com", To = { "c@d.com" } },
            new ThrowingLogger());

        var outcome = await notifier.NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Sent, outcome);   // it WAS delivered
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task AThrowingLogger_DoesNotEscapeTheCatchBlock()
    {
        // A provider that throws from the failure-path log must not escape the
        // method entirely — that would break the one invariant this class exists
        // to guarantee.
        var sender = new FakeSender { ThrowThis = new InvalidOperationException("relay down") };
        var notifier = new FailureNotifier(
            sender,
            new NotificationSettings { Enabled = true, SmtpHost = "relay", From = "a@b.com", To = { "c@d.com" } },
            new ThrowingLogger());

        var outcome = await notifier.NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Failed, outcome);
    }

    // ------------------------------------------------ the caller's own token

    [Fact]
    public async Task AnAlreadyCancelledCallerToken_StillSends()
    {
        // Deliberate: an operator cancelling a failing run must still learn why.
        // The caller's token is never linked into the send — only the notifier's
        // own internal timeout bounds it. See IFailureNotifier.NotifyAsync.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = true }, cts.Token);

        Assert.Equal(NotificationOutcome.Sent, outcome);
        Assert.Single(sender.Sent);
    }

    // --------------------------------------------- end to end over a real report

    [Fact]
    public async Task SendsARealRunFailureReport_WithRedactionApplied()
    {
        // This proves the end-to-end path with a real payload — it is NOT backstop
        // coverage, since RunFailureReport redacts on its own and this test still
        // passes if FailureNotifier's own redaction is removed.
        // RedactsTheFinishedMessage_EvenWhenThePayloadForgot is the test that
        // actually covers the backstop.
        var sender = new FakeSender();
        var report = new RunFailureReport
        {
            HostName = "ARMH-OPSDB01",
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Results = new List<LoaderRunResult>
            {
                LoaderRunResult.Failed("AGSI", "access denied (API key: abc123XYZ)", TimeSpan.Zero)
            }
        };

        var outcome = await Build(sender).NotifyAsync(report, default);

        Assert.Equal(NotificationOutcome.Sent, outcome);
        Assert.Contains("AGSI", sender.Sent[0].Subject);
        Assert.DoesNotContain("abc123XYZ", sender.Sent[0].Body);
    }
}
