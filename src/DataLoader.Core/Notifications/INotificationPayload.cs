namespace DataLoader.Core.Notifications;

/// <summary>
/// Something that might be worth telling a human about.
///
/// <para>Both notification sources implement this —
/// <see cref="RunFailureReport"/> for the in-process path and
/// <c>WatchdogReport</c> for the scheduled sweep — so
/// <see cref="FailureNotifier"/> has exactly one code path for sending, and
/// the two decisions that actually differ (is this worth reporting, and what
/// does it say) stay with the type that knows the answer.</para>
///
/// <para>Both members are pure. That is deliberate: it means every subject
/// line and every "should this even send" rule is unit-testable without a
/// notifier, a transport, a clock or a database.</para>
/// </summary>
public interface INotificationPayload
{
    /// <summary>True when a human needs to see this.</summary>
    bool ShouldNotify { get; }

    /// <summary>
    /// Renders the message, WITHOUT the subject prefix. <c>FailureNotifier</c>
    /// prepends that on the single shared send path, so no implementation can
    /// forget it and ship an alert whose DEV and PROD copies read identically.
    ///
    /// <para>Only called when <see cref="ShouldNotify"/> is true.</para>
    ///
    /// <para>🔒 Implementations MUST pass any vendor-supplied text through
    /// <c>ErrorTextRedactor</c> first — email leaves the machine, logs do not.
    /// <c>FailureNotifier</c> redacts the finished message again as a backstop,
    /// because a doc comment is not an enforcement mechanism and this platform
    /// has already leaked one API key through a path that trusted its caller.</para>
    /// </summary>
    NotificationMessage BuildMessage();
}
