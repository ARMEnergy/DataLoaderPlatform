namespace DataLoader.Core.Notifications;

/// <summary>
/// Puts a <see cref="NotificationMessage"/> on the wire.
///
/// <para>Separate from <see cref="IFailureNotifier"/> so that adding a channel
/// — Zoom, Teams, PagerDuty — is one new class and a config value, with no
/// change to any caller. Implementations MAY throw; <see cref="FailureNotifier"/>
/// is responsible for making sure that never escapes.</para>
/// </summary>
public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
