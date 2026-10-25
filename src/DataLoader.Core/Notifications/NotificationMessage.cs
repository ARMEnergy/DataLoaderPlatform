namespace DataLoader.Core.Notifications;

/// <summary>
/// A notification ready for a transport. Plain text only — these are read on
/// phones at 6am, and HTML buys nothing that matters at that moment.
/// </summary>
public sealed record NotificationMessage(string Subject, string Body);
