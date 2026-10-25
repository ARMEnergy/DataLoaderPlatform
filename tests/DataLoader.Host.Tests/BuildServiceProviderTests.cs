using DataLoader.Core.Abstractions;
using DataLoader.Core.Hosting;
using DataLoader.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Host.Tests;

/// <summary>
/// Covers the one behaviour this whole task exists to protect: a broken
/// <c>Platform:Notifications</c> config must degrade to no notifications, not
/// take the loaders down with it. See the CODER task notes — a Validate()
/// throw left uncaught inside BuildServiceProvider used to abort the
/// container before PlatformHost could ever be resolved, so one typo in a
/// side-channel feature meant zero loaders ran.
///
/// <para>This exercises the real DI graph (via <c>Program.BuildServiceProvider</c>,
/// made visible to this assembly through <c>InternalsVisibleTo</c>) rather than
/// re-deriving the same conclusion against <see cref="NotificationSettings"/> in
/// isolation — the thing that actually has to be proven is that a bad config
/// resolves a WORKING, inert sender and a WORKING host, not just that
/// <c>Validate()</c> throws (that part is already covered in
/// DataLoader.Core.Tests).</para>
/// </summary>
public class BuildServiceProviderTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    /// <summary>
    /// "Enabled: true and nothing else" is the exact shape of a copy/paste
    /// mistake: someone flips the switch before filling in SmtpHost/From/To.
    /// Validate() throws on that, and the degrade path must swallow it.
    /// </summary>
    [Fact]
    public void InvalidEnabledNotificationConfig_DegradesToNullSenderAndStillBuildsTheHost()
    {
        var configuration = Config(("Platform:Notifications:Enabled", "true"));

        using var services = Program.BuildServiceProvider(
            configuration,
            Array.Empty<ILoaderModule>(),
            Array.Empty<string>(),
            NullLogger.Instance);

        var sender = services.GetRequiredService<INotificationSender>();
        Assert.IsType<NullNotificationSender>(sender);

        // The entire point: a broken notification config must not have stopped
        // the rest of the container from standing up. If PlatformHost cannot be
        // resolved, the ETL cannot run.
        var host = services.GetRequiredService<PlatformHost>();
        Assert.NotNull(host);
    }

    /// <summary>
    /// A config that never turned notifications on in the first place must
    /// behave identically — this is the common case (ships disabled) and must
    /// not be confused with the degrade path above.
    /// </summary>
    [Fact]
    public void NotificationsDisabled_ResolvesNullSenderAndTheHost()
    {
        var configuration = Config();

        using var services = Program.BuildServiceProvider(
            configuration,
            Array.Empty<ILoaderModule>(),
            Array.Empty<string>(),
            NullLogger.Instance);

        Assert.IsType<NullNotificationSender>(services.GetRequiredService<INotificationSender>());
        Assert.NotNull(services.GetRequiredService<PlatformHost>());
    }

    /// <summary>
    /// A fully valid, enabled config must resolve the real transport — the
    /// degrade path must not fire on a config that is actually fine.
    /// </summary>
    [Fact]
    public void ValidEnabledNotificationConfig_ResolvesTheSmtpSender()
    {
        var configuration = Config(
            ("Platform:Notifications:Enabled", "true"),
            ("Platform:Notifications:SmtpHost", "relay.example.com"),
            ("Platform:Notifications:From", "svc@armenergy.com"),
            ("Platform:Notifications:To:0", "a@armenergy.com"));

        using var services = Program.BuildServiceProvider(
            configuration,
            Array.Empty<ILoaderModule>(),
            Array.Empty<string>(),
            NullLogger.Instance);

        Assert.IsType<SmtpNotificationSender>(services.GetRequiredService<INotificationSender>());
        Assert.NotNull(services.GetRequiredService<PlatformHost>());
    }

    /// <summary>
    /// The OTHER throw site: FromConfiguration itself rejects a stray
    /// SmtpUsername/SmtpPassword key before Validate() is ever reached. Same
    /// rule applies — see the CODER notes on why this must degrade too rather
    /// than being treated as fatal: a credential typo in a side-channel
    /// feature is no more entitled to take the loaders down than a missing
    /// SmtpHost is.
    /// </summary>
    [Fact]
    public void ForbiddenCredentialKey_DegradesToNullSenderAndStillBuildsTheHost()
    {
        var configuration = Config(
            ("Platform:Notifications:Enabled", "true"),
            ("Platform:Notifications:SmtpHost", "relay.example.com"),
            ("Platform:Notifications:From", "svc@armenergy.com"),
            ("Platform:Notifications:To:0", "a@armenergy.com"),
            ("Platform:Notifications:SmtpPassword", "shouldnt-be-here"));

        using var services = Program.BuildServiceProvider(
            configuration,
            Array.Empty<ILoaderModule>(),
            Array.Empty<string>(),
            NullLogger.Instance);

        Assert.IsType<NullNotificationSender>(services.GetRequiredService<INotificationSender>());
        Assert.NotNull(services.GetRequiredService<PlatformHost>());
    }
}
