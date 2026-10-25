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
                _logger.LogInformation(
                    "Notifications are disabled; not sending an alert that would otherwise go out");
                return NotificationOutcome.Disabled;
            }

            var message = Finalise(payload.BuildMessage());

            // The run may already have been cancelled — CancellationToken.None
            // deliberately, so an operator stopping the job still gets told why.
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));

            await _sender.SendAsync(message, timeout.Token).ConfigureAwait(false);

            _logger.LogInformation("Sent failure notification: {Subject}", message.Subject);
            return NotificationOutcome.Sent;
        }
        catch (Exception ex)
        {
            // Never rethrow. The run's own outcome is not this component's to change.
            _logger.LogError(ex, "Failed to send failure notification");
            return NotificationOutcome.Failed;
        }
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
