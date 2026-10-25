using System.Net.Mail;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Sends over SMTP with <c>System.Net.Mail</c>.
///
/// <para>⚠ No credentials, by design. The configured endpoint
/// (<c>armenergy-com.mail.protection.outlook.com</c>) is Exchange Online's
/// inbound MX connector — an <c>EHLO</c> probe on 2026-10-24 advertised
/// <c>STARTTLS</c> and NO <c>AUTH</c> verb. Setting
/// <see cref="SmtpClient.Credentials"/> against it does nothing useful, which
/// is why <see cref="NotificationSettings"/> rejects credential keys outright
/// rather than binding them.</para>
///
/// <para>⚠ Consequence of the same fact: recipients must be inside the tenant's
/// accepted domains. Direct send relay-denies external addresses.</para>
///
/// <para>On port 25, <see cref="SmtpClient.EnableSsl"/> means STARTTLS, not
/// implicit TLS. Microsoft requires TLS 1.2 on this path.</para>
///
/// <para>This class is deliberately thin and has no unit tests — there is
/// nothing here to assert that would not just be asserting
/// <c>System.Net.Mail</c>. All the behaviour worth testing lives in
/// <see cref="FailureNotifier"/> and the payloads.</para>
/// </summary>
public sealed class SmtpNotificationSender : INotificationSender
{
    private readonly NotificationSettings _settings;

    public SmtpNotificationSender(NotificationSettings settings)
    {
        settings.Validate();
        _settings = settings;
    }

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
        {
            EnableSsl = _settings.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = null,
            Timeout = Math.Max(1, _settings.TimeoutSeconds) * 1000
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(_settings.From),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false
        };
        foreach (var to in _settings.To) mail.To.Add(to);

        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
    }
}
