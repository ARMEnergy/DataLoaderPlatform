namespace DataLoader.Core.Notifications;

/// <summary>
/// Discards everything. Registered when <c>Platform:Notifications:Enabled</c>
/// is false, so that no call site needs a null check and the notifier's own
/// logic stays under test whether or not mail is switched on.
/// </summary>
public sealed class NullNotificationSender : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
