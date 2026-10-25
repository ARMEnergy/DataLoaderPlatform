using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Bound from the <c>Platform:Notifications</c> section.
///
/// <para>Note <see cref="FromConfiguration"/>, which builds this straight from
/// <see cref="IConfiguration"/> without DI. That is not a convenience: the
/// bootstrap notifier in <c>Program.cs</c> has to report failures that happen
/// while BUILDING the DI container, so it cannot resolve anything from it.</para>
/// </summary>
public sealed class NotificationSettings
{
    public const string SectionName = "Platform:Notifications";

    /// <summary>
    /// Ships false. The feature stays inert until IT confirms the server can
    /// egress on port 25 and that the tenant's "Reject Direct Send" setting is
    /// off (see the spec, §10).
    /// </summary>
    public bool Enabled { get; set; }

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 25;

    /// <summary>
    /// STARTTLS. In <c>System.Net.Mail</c> on port 25 this means opportunistic
    /// TLS, not implicit TLS. The chosen endpoint advertises STARTTLS and
    /// Microsoft requires TLS 1.2 on that path, so this must stay true.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    public string From { get; set; } = string.Empty;

    /// <summary>
    /// ⚠ Internal recipients only. The relay is Exchange Online's inbound MX
    /// connector, which accepts mail only for the tenant's own accepted
    /// domains — anything outside <c>@armenergy.com</c> is relay-denied.
    /// </summary>
    public List<string> To { get; set; } = new();

    public string SubjectPrefix { get; set; } = "[DataLoader]";
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Keys that must never appear. The endpoint advertises no <c>AUTH</c>
    /// verb, so a credential here would be dead config that looks alive.
    /// </summary>
    private static readonly string[] ForbiddenKeys = { "SmtpUsername", "SmtpPassword" };

    /// <summary>
    /// Binds the section and rejects the forbidden credential keys, but performs
    /// NO other validation — the result can still be an enabled config missing a
    /// host, a sender or every recipient. Callers MUST call <see cref="Validate"/>
    /// themselves; <c>SmtpNotificationSender</c>'s constructor also calls it as a
    /// backstop, but a caller that only binds and never constructs that sender
    /// (the bootstrap notifier, a future caller) would otherwise ship an unusable
    /// config that looks fine until the first send is attempted.
    /// </summary>
    public static NotificationSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        foreach (var key in ForbiddenKeys)
        {
            if (section[key] is not null)
            {
                throw new InvalidOperationException(
                    $"'{SectionName}:{key}' is set, but the configured SMTP endpoint accepts no " +
                    "credentials — it advertises STARTTLS and no AUTH verb (Exchange Online direct " +
                    "send). Remove the key. If an authenticated relay is genuinely needed, that is a " +
                    "transport change, not a config change — see the failure-notifications design spec.");
            }
        }

        var settings = new NotificationSettings();
        section.Bind(settings);
        return settings;
    }

    /// <summary>
    /// Fails fast on a configuration that cannot work. Only checked when
    /// <see cref="Enabled"/> — a half-filled disabled block must never stop a
    /// loader from running.
    /// </summary>
    public void Validate()
    {
        if (!Enabled) return;

        if (string.IsNullOrWhiteSpace(SmtpHost))
            throw new InvalidOperationException($"'{SectionName}:SmtpHost' is required when Enabled is true.");
        if (string.IsNullOrWhiteSpace(From))
            throw new InvalidOperationException($"'{SectionName}:From' is required when Enabled is true.");
        if (To.Count == 0)
            throw new InvalidOperationException($"'{SectionName}:To' must list at least one recipient when Enabled is true.");

        // SmtpClient's constructor accepts 0 and 70000 without complaint — only a
        // negative port throws — and FailureNotifier swallows every send exception
        // by design, logging only "Failed to send failure notification" to a log
        // file nobody reads. A typo'd port would otherwise never name itself.
        if (SmtpPort is <= 0 or > 65535)
            throw new InvalidOperationException(
                $"'{SectionName}:SmtpPort' must be between 1 and 65535, was {SmtpPort}.");

        // An unparseable address passes every check above, then throws
        // FormatException on the FIRST REAL SEND — where FailureNotifier swallows
        // it as Failed and writes one line to a log nobody reads. The result is a
        // feature that looks configured and is silently dead, discoverable only
        // by an alert never arriving. The address is not a secret, so naming the
        // offending key and value is what makes the failure actionable.
        ValidateAddress(From, $"{SectionName}:From");
        for (var i = 0; i < To.Count; i++)
            ValidateAddress(To[i], $"{SectionName}:To[{i}]");
    }

    private static void ValidateAddress(string value, string key)
    {
        try
        {
            _ = new MailAddress(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"'{key}' is not a valid email address: '{value}'.", ex);
        }
    }
}
