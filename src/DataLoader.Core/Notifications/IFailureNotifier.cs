namespace DataLoader.Core.Notifications;

/// <summary>
/// Reports a failure to a human. One method, because both sources — the
/// in-process run report and the watchdog sweep — arrive as an
/// <see cref="INotificationPayload"/>.
/// </summary>
public interface IFailureNotifier
{
    /// <summary>
    /// Sends <paramref name="payload"/> if it has anything to say.
    ///
    /// <para>⚠ NEVER throws, whatever the transport does. Returns what
    /// happened instead, so a caller that genuinely needs to know — the
    /// watchdog, whose exit code depends on delivery — can act on it, while
    /// the loader host can safely ignore it.</para>
    /// </summary>
    Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken cancellationToken);
}
