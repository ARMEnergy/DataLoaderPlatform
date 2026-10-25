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
    }
}
