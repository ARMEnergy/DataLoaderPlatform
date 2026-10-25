using Microsoft.Extensions.Logging;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Decides whether a payload is worth sending, renders it, and hands it to the
/// transport — swallowing every failure on the way.
///
/// <para>⚠ The swallowing is the point, not laziness. This runs at the end of
/// an ETL run that may have just loaded millions of rows successfully; an
/// unreachable mail relay must not turn that into a failed job. The same
/// reasoning already governs <c>SqlAdditionalProcessRunner</c>.</para>
///
/// <para>The timeout matters for the same reason: these processes run as
/// scheduled tasks, and a relay that accepts a connection then stalls would
/// otherwise hold the task open indefinitely.</para>
/// </summary>
public sealed class FailureNotifier : IFailureNotifier
{
    private readonly INotificationSender _sender;
    private readonly NotificationSettings _settings;
    private readonly ILogger<FailureNotifier> _logger;

    public FailureNotifier(
        INotificationSender sender,
        NotificationSettings settings,
        ILogger<FailureNotifier> logger)
    {
        _sender = sender;
        _settings = settings;
        _logger = logger;
    }

    public async Task<NotificationOutcome> NotifyAsync(
        INotificationPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            if (!payload.ShouldNotify) return NotificationOutcome.NothingToReport;
            if (!_settings.Enabled)
            {
                SafeLog(() => _logger.LogInformation(
                    "Notifications are disabled; not sending an alert that would otherwise go out"));
                return NotificationOutcome.Disabled;
            }

            var message = Finalise(payload.BuildMessage());

            // cancellationToken (the method parameter) is deliberately never linked
            // into this CTS or passed to the sender — see the ⚠ on
            // IFailureNotifier.NotifyAsync. An operator cancelling a failing run
            // should still receive the alert explaining why it was failing, so the
            // send is bounded only by the notifier's own internal timeout below.
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));

            await _sender.SendAsync(message, timeout.Token).ConfigureAwait(false);

            SafeLog(() => _logger.LogInformation("Sent failure notification: {Subject}", message.Subject));
            return NotificationOutcome.Sent;
        }
        catch (Exception ex)
        {
            // Never rethrow. The run's own outcome is not this component's to change.
            SafeLog(() => _logger.LogError(ex, "Failed to send failure notification"));
            return NotificationOutcome.Failed;
        }
    }

    /// <summary>
    /// Logging must not be able to break the never-throw guarantee, nor invert
    /// the reported outcome. A provider that threw from the success log would
    /// otherwise be caught below and reported as <c>Failed</c> for a message that
    /// was actually delivered — and the watchdog sets its exit code from that
    /// value. A provider that threw from the failure log would escape the method
    /// entirely. Standard <c>Microsoft.Extensions.Logging</c> providers do not
    /// throw, so this is a low-probability event, but the invariant is absolute
    /// and the fix costs nothing.
    /// </summary>
    private static void SafeLog(Action log)
    {
        try { log(); }
        catch { /* There is nothing useful left to do: the logger is the thing that failed. */ }
    }

    /// <summary>
    /// Applies the two things that must happen on EVERY message, whatever the
    /// payload did.
    ///
    /// <para>The subject prefix lives here rather than in each payload so none
    /// of them can omit it and produce an alert whose DEV and PROD copies read
    /// identically.</para>
    ///
    /// <para>🔒 The redaction pass is defence in depth. Payloads are already
    /// required to redact, but a doc comment is not an enforcement mechanism,
    /// and this platform has already leaked an API key through a path that
    /// trusted its caller. Redaction is idempotent, so redacting twice costs
    /// nothing and a future payload cannot reintroduce the leak by forgetting.</para>
    /// </summary>
    private NotificationMessage Finalise(NotificationMessage message) =>
        new($"{_settings.SubjectPrefix} {ErrorTextRedactor.Redact(message.Subject)}".Trim(),
            ErrorTextRedactor.Redact(message.Body));
}
