namespace DataLoader.Core.Notifications;

/// <summary>
/// What a notification attempt actually did.
///
/// <para>This exists because two requirements would otherwise contradict each
/// other. The notifier must NEVER throw — a broken mail relay must not fail a
/// good load (see <see cref="FailureNotifier"/>). But the watchdog needs a
/// non-zero exit code when it could not deliver, because an undelivered
/// watchdog alert is indistinguishable from "all clear" and would hide
/// everything it exists to report.</para>
///
/// <para>So the notifier reports rather than throws: <see cref="PlatformHost"/>
/// ignores this value, and the watchdog acts on it.</para>
/// </summary>
public enum NotificationOutcome
{
    /// <summary>Delivered to the transport without error.</summary>
    Sent,

    /// <summary>Notifications are switched off in configuration.</summary>
    Disabled,

    /// <summary>Nothing was wrong, so nothing was sent. The common case.</summary>
    NothingToReport,

    /// <summary>The transport failed. Already logged; never thrown.</summary>
    Failed
}
