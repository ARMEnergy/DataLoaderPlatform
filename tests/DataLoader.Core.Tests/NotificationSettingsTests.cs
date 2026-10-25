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
    public void FromConfiguration_MissingSection_YieldsDisabledDefaults()
    {
        var settings = NotificationSettings.FromConfiguration(Config());

        Assert.False(settings.Enabled);
        Assert.Equal(25, settings.SmtpPort);
        Assert.True(settings.UseSsl);
        Assert.Equal("[DataLoader]", settings.SubjectPrefix);
        Assert.Equal(30, settings.TimeoutSeconds);
    }

    [Theory]
    [InlineData("SmtpUsername")]
    [InlineData("SmtpPassword")]
    public void FromConfiguration_RejectsCredentialKeys(string key)
    {
        // The chosen endpoint advertises STARTTLS but no AUTH verb. A credential
        // here does nothing, and config that LOOKS like a working secret invites
        // someone to put a real password in it later.
        var config = Config(($"Platform:Notifications:{key}", "whatever"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => NotificationSettings.FromConfiguration(config));

        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain("whatever", ex.Message);   // never echo the value
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
}
