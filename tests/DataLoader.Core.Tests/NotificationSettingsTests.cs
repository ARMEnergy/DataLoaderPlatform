using DataLoader.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataLoader.Core.Tests;

public class NotificationSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void FromConfiguration_BindsTheSection()
    {
        var config = Config(
            ("Platform:Notifications:Enabled", "true"),
            ("Platform:Notifications:SmtpHost", "relay.example.com"),
            ("Platform:Notifications:SmtpPort", "25"),
            ("Platform:Notifications:UseSsl", "true"),
            ("Platform:Notifications:From", "svc@armenergy.com"),
            ("Platform:Notifications:To:0", "a@armenergy.com"),
            ("Platform:Notifications:To:1", "b@armenergy.com"),
            ("Platform:Notifications:SubjectPrefix", "[DL]"),
            ("Platform:Notifications:TimeoutSeconds", "45"));

        var settings = NotificationSettings.FromConfiguration(config);

        Assert.True(settings.Enabled);
        Assert.Equal("relay.example.com", settings.SmtpHost);
        Assert.Equal(25, settings.SmtpPort);
        Assert.True(settings.UseSsl);
        Assert.Equal("svc@armenergy.com", settings.From);
        Assert.Equal(new[] { "a@armenergy.com", "b@armenergy.com" }, settings.To);
        Assert.Equal("[DL]", settings.SubjectPrefix);
        Assert.Equal(45, settings.TimeoutSeconds);
    }

    [Fact]
    public void FromConfiguration_MissingSection_DefaultsToEnabled()
    {
        var settings = NotificationSettings.FromConfiguration(Config());

        // Deliberately TRUE when the section is absent. A missing config section
        // defaulting to "off" would give a platform that looks healthy and tells
        // nobody anything — the exact silence this feature exists to end. On
        // instead means it fails Validate() and degrades loudly to
        // NullNotificationSender, which cannot stop the loaders running.
        Assert.True(settings.Enabled);
        Assert.Equal(25, settings.SmtpPort);
        Assert.True(settings.UseSsl);
        Assert.Equal("[DataLoader]", settings.SubjectPrefix);
        Assert.Equal(30, settings.TimeoutSeconds);
    }

    [Theory]
    [InlineData("SmtpUsername", "whatever")]
    [InlineData("SmtpPassword", "whatever")]
    [InlineData("SmtpUsername", "")]
    [InlineData("SmtpPassword", "")]
    public void FromConfiguration_RejectsCredentialKeys(string key, string value)
    {
        // The chosen endpoint advertises STARTTLS but no AUTH verb. A credential
        // here does nothing, and config that LOOKS like a working secret invites
        // someone to put a real password in it later. The set-but-EMPTY case is
        // pinned too: a future refactor to GetValue<string>/IsNullOrEmpty could
        // otherwise silently stop catching "SmtpPassword": "".
        var config = Config(($"Platform:Notifications:{key}", value));

        var ex = Assert.Throws<InvalidOperationException>(
            () => NotificationSettings.FromConfiguration(config));

        Assert.Contains(key, ex.Message);
        if (!string.IsNullOrEmpty(value))
            Assert.DoesNotContain(value, ex.Message);   // never echo the value
    }

    [Fact]
    public void Validate_EnabledWithoutHost_Throws()
    {
        var settings = new NotificationSettings
        {
            Enabled = true, From = "a@armenergy.com", To = { "b@armenergy.com" }
        };

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_EnabledWithoutRecipients_Throws()
    {
        var settings = new NotificationSettings
        {
            Enabled = true, SmtpHost = "relay", From = "a@armenergy.com"
        };

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_Disabled_NeverThrows_EvenWhenEmpty()
    {
        // Must stay inert until IT confirms egress; an incomplete disabled
        // config must not stop a loader from running.
        new NotificationSettings { Enabled = false }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Validate_RejectsAnOutOfRangePort(int port)
    {
        // SmtpClient accepts 0 and 70000 without complaint, and FailureNotifier
        // swallows the eventual send failure — so if this gate does not catch a
        // typo'd port, nothing ever will.
        var settings = new NotificationSettings
        {
            Enabled = true, SmtpHost = "relay", From = "a@armenergy.com",
            To = { "b@armenergy.com" }, SmtpPort = port
        };

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Validate());
        Assert.Contains("SmtpPort", ex.Message);
    }

    [Fact]
    public void Validate_RejectsAnUnparseableFrom()
    {
        // Otherwise this passes every check here, then throws FormatException on
        // the FIRST REAL SEND — where FailureNotifier swallows it as Failed and
        // writes one line to a log nobody reads. A feature that looks configured
        // and is silently dead, discoverable only by an alert never arriving.
        var settings = new NotificationSettings
        {
            Enabled = true, SmtpHost = "relay", From = "not-an-email-address-at-all ???",
            To = { "b@armenergy.com" }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Validate());
        Assert.Contains("Platform:Notifications:From", ex.Message);
        Assert.Contains("not-an-email-address-at-all ???", ex.Message);   // an address, not a secret
    }

    [Fact]
    public void Validate_RejectsAnUnparseableRecipient()
    {
        var settings = new NotificationSettings
        {
            Enabled = true, SmtpHost = "relay", From = "a@armenergy.com",
            To = { "b@armenergy.com", "not-an-email-address-at-all ???" }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Validate());
        Assert.Contains("Platform:Notifications:To[1]", ex.Message);
        Assert.Contains("not-an-email-address-at-all ???", ex.Message);
    }
}
